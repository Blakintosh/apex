using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using K = Avalonia.Input.Key;
using Avalonia.VisualTree;
using Apex.Editor.Controls;
using Apex.Editor.Models;
using Apex.Editor.Services;
using Apex.Editor.ViewModels;
using Apex.Editor.Views;

namespace Apex.Shots;

/// <summary>
/// Table mode at its full 400 rows: the open, show-a-column and scroll-to-bottom gates, then real input on the header
/// checkbox, a sort, a switch cell with Ctrl+Z, and rows recycled by a long scroll showing their own assets.
/// </summary>
public partial class Program
{
    /// <summary>
    /// <see cref="TableViewModel.RowCap"/> rows of the type with the most properties among those that fill a table. The
    /// mock corpus has no type that large, so its rows are copies of one type's assets (records outside the database,
    /// which nothing else reads).
    /// </summary>
    private static List<AssetRecord> TableRows(AssetDatabase db)
    {
        var byType = db.Assets.GroupBy(a => a.Type, StringComparer.OrdinalIgnoreCase).ToList();
        var full = byType.Where(g => g.Count() >= TableViewModel.RowCap && SchemaRegistry.Get(g.Key) is not null)
            .OrderByDescending(g => SchemaRegistry.Get(g.Key)!.Properties.Count).FirstOrDefault();
        if (full is not null)
            return full.Take(TableViewModel.RowCap).ToList();
        var source = byType.OrderByDescending(g => SchemaRegistry.Get(g.Key)?.Properties.Count ?? 0).First().ToList();
        return Enumerable.Range(0, TableViewModel.RowCap).Select(i =>
        {
            var from = source[i % source.Count];
            var copy = new AssetRecord { Name = $"{from.Name}_{i:000}", Type = from.Type, GdtName = from.GdtName };
            foreach (var (k, v) in from.Properties)
                copy.Properties[k] = v;
            return copy;
        }).ToList();
    }

    private static void MeasureTable(MainWindow window, MainViewModel vm, AssetDatabase db, int warm, int runs, List<PerfResult> results)
    {
        var rows = TableRows(db);
        var type = rows[0].Type;
        var tableView = window.GetVisualDescendants().OfType<Apex.Editor.Views.TableView>().First();
        var rowsControl = tableView.FindControl<ItemsControl>("Rows")!;
        ScrollViewer Scroller() => rowsControl.GetVisualDescendants().OfType<ScrollViewer>().First();

        // ── Open: the first screen of rows, cells and all ──
        var shown = true;
        results.Add(Time(window, $"Table: open ({rows.Count} rows)", PerfBudgets.OpenAsset, warm, runs,
            _ => vm.OpenTableFor(rows),
            after: _ =>
            {
                shown &= vm.Table is { } t && rowsControl.ContainerFromIndex(0) is { } first
                    && first.GetVisualDescendants().OfType<TableCellsPanel>().FirstOrDefault()?.Children.Count(c => c.IsVisible && c.Opacity > 0) > 0
                    && CellsShowingOthers(rowsControl, t).Count == 0;
                vm.Table?.CloseCommand.Execute(null);
            },
            note: $"{type}, {rows.Count} rows"));
        Check($"perf: every timed table open showed its first row's cells, each its own", shown);

        results.Add(Time(window, "Table: close", PerfBudgets.Frame, warm, runs,
            _ => vm.Table!.CloseCommand.Execute(null),
            before: _ =>
            {
                vm.OpenTableFor(rows);
                // The open's garbage is the open's cost (gated above): collected here, it can't land in a close sample.
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }));

        vm.OpenTableFor(rows);
        Settle(window);
        var table = vm.Table!;
        var built = table.Rows.Count(r => r.BuiltCells is not null);
        Check($"table: opening built the cells of the rows on screen only ({built} of {table.Rows.Count} rows)",
            built > 0 && built < table.Rows.Count / 2);

        // ── Show one more column: one cell per row on screen, the rest untouched ──
        table.RefreshColumnChoices();
        var hidden = table.ColumnChoices.Where(c => !c.IsShown).Select(c => c.Def).Take(warm + runs).ToList();
        var columnAdded = true;
        var addAll = new List<double>();
        var add = Time(window, "Table: show one more column", PerfBudgets.Frame, warm, runs,
            i => table.SetColumnShown(hidden[i % hidden.Count], true),
            after: i =>
            {
                // The first frame shows only each row's own cells (some still blank); settled, every cell in view is built.
                columnAdded &= CellsShowingOthers(rowsControl, table).Count == 0;
                addAll.Add(SettleMs(window));
                columnAdded &= table.Rows[0].Cells.Count == table.Columns.Count && CellsShowingOthers(rowsControl, table, complete: true).Count == 0;
                table.SetColumnShown(hidden[i % hidden.Count], false);
            },
            firstFrame: true);
        results.Add(add with { Note = $"to the first frame; every cell in view {Median(addAll):0} ms later" });
        Check("perf: every shown column gave the rows on screen their own cells, none another row's", columnAdded);

        // ── Scroll to the bottom: rows on screen take the last assets, recycled ──
        foreach (var def in hidden.Take(12))
            table.SetColumnShown(def, true);
        Settle(window);
        var foreign = new List<string>();
        var scrollAll = new List<double>();
        // Gated at a UI chunk, not a frame: the rows themselves (recycled through RowPanel's pool) take most of it.
        var scroll = Time(window, "Table: scroll to bottom", PerfBudgets.UiChunk, warm, runs,
            _ => Scroller().Offset = new Vector(0, Scroller().Extent.Height),
            after: i =>
            {
                if (i >= warm)
                    foreign.AddRange(CellsShowingOthers(rowsControl, table));
                scrollAll.Add(SettleMs(window));
                if (i >= warm)
                    foreign.AddRange(CellsShowingOthers(rowsControl, table, complete: true));
                Scroller().Offset = default;
                Settle(window);
            },
            firstFrame: true);
        results.Add(scroll with { Note = $"{table.Columns.Count} columns, to the first frame; every cell in view {Median(scrollAll):0} ms later" });
        Scroller().Offset = new Vector(0, Scroller().Extent.Height);
        Settle(window);
        var lastShown = rowsControl.GetRealizedContainers().Any(c => c.IsEffectivelyVisible && c.DataContext == table.Rows[^1]);
        Check($"table: scrolled to the bottom, the last row is on screen and every cell shows its own asset's value ({foreign.Count} didn't{string.Concat(foreign.Take(3).Select(f => "; " + f))})",
            lastShown && foreign.Count == 0 && CellsShowingOthers(rowsControl, table).Count == 0);
        var edited = rows.Where(r => r.History.CanUndo).ToList();
        Check($"table: scrolling and adding columns edited no asset ({string.Join(", ", edited.Take(3).Select(r => r.Name))})", edited.Count == 0);
        Scroller().Offset = default;
        Settle(window);

        // ── The pointer onto a cell: its editor swaps in for the drawing, before any click ──
        PointerAway(window);
        var hosts = rowsControl.GetVisualDescendants().OfType<TableCellValue>()
            // Clear of the sideways scroll bar, which lies over the bottom row.
            .Where(h => h.IsEffectivelyEnabled && OnScreen(h) && rowsControl.FindAncestorOfType<ScrollViewer>() is { } sv
                        && h.TranslatePoint(default, sv) is { } p && p.Y + h.Bounds.Height < sv.Viewport.Height - 16).ToList();
        var missed = new List<string>();
        results.Add(Time(window, "Table: pointer onto a cell", PerfBudgets.Frame, warm, runs,
            i => RawInput.Send(window, "MouseMove", CentreOf(window, hosts[i * 7 % hosts.Count]), RawInputModifiers.None),
            before: _ => DrawFrame(window),
            after: i =>
            {
                if (hosts[i * 7 % hosts.Count] is { LiveEditor: null } miss)
                    missed.Add($"{(miss.DataContext as PropertyItemViewModel)?.Key} at {CentreOf(window, miss)} enabled {miss.IsEffectivelyEnabled} visible {miss.IsEffectivelyVisible}");
                PointerAway(window);
            },
            note: $"{hosts.Count} cells on screen, one at a time across the kinds"));
        Check($"perf: every cell the pointer came onto held its editor ({missed.Count} didn't{string.Concat(missed.Take(3).Select(m => "; " + m))})", missed.Count == 0 && hosts.Count > 0);

        TableInput(window, vm, table, rowsControl);
        table.CloseCommand.Execute(null);
        Settle(window);

        var schema = SchemaRegistry.Get(type)!;
        TableCellLook(window, vm, rows.Take(12).ToList(),
            schema.Properties.GroupBy(d => d.Kind).Select(g => g.First()).Take(10).ToList(), "table cells (mock)");
    }

    /// <summary>
    /// Table open on the real install: the session's first table (reported), then tables of the big types in turn, each
    /// opened after closing the last, timed until every cell on screen shows its own value. Alternating types is the
    /// common case and the costly one: each type puts different editor kinds in each column.
    /// </summary>
    private static void MeasureTableLive(MainWindow window, MainViewModel vm, AssetDatabase db, List<PerfResult> results)
    {
        var tables = new[] { "bulletweapon", "material", "xmodel" }
            .Select(t => db.Assets.Where(a => a.Type.Equals(t, StringComparison.OrdinalIgnoreCase)).Take(TableViewModel.RowCap).ToList())
            .Where(rows => rows.Count > 0 && SchemaRegistry.Get(rows[0].Type) is not null)
            .ToList();
        if (tables.Count == 0)
        {
            Console.WriteLine("perf (live): no bulletweapon, material or xmodel assets; table open skipped");
            return;
        }
        if (window.GetVisualDescendants().OfType<Apex.Editor.Views.TableView>().FirstOrDefault() is not { } tableView)
        {
            Check("perf (live): the window has a table view to open tables in", false);
            return;
        }
        var rowsControl = tableView.FindControl<ItemsControl>("Rows")!;

        // The session's first table: no cell built yet, every editor kind new. Timed until every cell on screen is filled.
        Settle(window);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Settle(window);
        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        var cycles = UiThreadClock.Cycles();
        vm.OpenTableFor(tables[0]);
        Settle(window);
        var first = UiThreadClock.Available ? UiThreadClock.ToMs(UiThreadClock.Cycles() - cycles) : double.NaN;
        var firstWall = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        var firstFilled = vm.Table is { } t0 && rowsControl.ContainerFromIndex(0) is not null && CellsShowingOthers(rowsControl, t0, complete: true).Count == 0;
        results.Add(Summarize("Table: first open of the session (live)", PerfBudgets.OpenAsset, new List<double> { first },
            $"{tables[0][0].Type}, {tables[0].Count} rows, to every cell in view filled; one sample") with { WallMedian = firstWall, WallP95 = firstWall });
        Check("perf (live): the session's first table showed every row's cells in view, each its own", firstFilled);
        vm.Table?.CloseCommand.Execute(null);
        Settle(window);

        var shown = true;
        var open = Time(window, "Table: open after another (live)", PerfBudgets.OpenAsset, 5, 30,
            i => vm.OpenTableFor(tables[i % tables.Count]),
            after: _ =>
            {
                shown &= vm.Table is { } t && rowsControl.ContainerFromIndex(0) is not null
                    && CellsShowingOthers(rowsControl, t, complete: true).Count == 0;
                vm.Table?.CloseCommand.Execute(null);
            },
            note: $"{string.Join(", ", tables.Select(t => t[0].Type))} in turn, every cell in view");
        results.Add(open);
        Check("perf (live): every timed table open showed every row's cells in view, each its own", shown);
    }

    /// <summary>Real input on the table: the header checkbox, a header sort, a switch cell and Ctrl+Z.</summary>
    private static void TableInput(MainWindow window, MainViewModel vm, TableViewModel table, ItemsControl rowsControl)
    {
        var tableView = window.GetVisualDescendants().OfType<Apex.Editor.Views.TableView>().First();

        // Header checkbox: all, then none.
        var all = tableView.GetVisualDescendants().OfType<CheckBox>().First(c => AutomationPropertiesName(c) == "Select all rows");
        ClickAt(window, CentreOf(window, all));
        Settle(window);
        var selectedAll = table.SelectedCount == table.Rows.Count && table.Rows.All(r => r.IsSelected);
        ClickAt(window, CentreOf(window, all));
        Settle(window);
        Check($"table: the header checkbox selects every row and then none (click: {selectedAll}, again: {table.SelectedCount} selected)",
            selectedAll && table.SelectedCount == 0);

        // Sort: a header click sorts by that column, a second reverses it, and the rows on screen follow.
        var column = table.Columns.First(c => c.Def.Kind != PropertyKind.Toggle);
        var header = tableView.GetVisualDescendants().OfType<Button>().First(b => b.DataContext == column);
        string Value(TableRowViewModel r) => r.Record.Properties.GetValueOrDefault(column.Key, column.Def.Default);
        ClickAt(window, CentreOf(window, header));
        Settle(window);
        var ascending = column.SortGlyph == "▲" && rowsControl.ContainerFromIndex(0)?.DataContext == table.Rows[0];
        var firstUp = Value(table.Rows[0]);
        ClickAt(window, CentreOf(window, header));
        Settle(window);
        var descending = column.SortGlyph == "▼" && rowsControl.ContainerFromIndex(0)?.DataContext == table.Rows[0];
        Check($"table: a header click sorts by {column.Key} and a second reverses it (first '{firstUp}', then '{Value(table.Rows[0])}')",
            ascending && descending && CellsShowingOthers(rowsControl, table).Count == 0);

        // Column picker: the Columns button opens it; a click on a property's box makes it a column, another takes it away.
        var columnsButton = tableView.FindControl<Button>("ColumnsButton")!;
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Settle(window);
        ClickAt(window, CentreOf(window, columnsButton));
        Settle(window);
        var filterBox = tableView.FindControl<TextBox>("ColumnFilterBox")!;
        if (columnsButton.Flyout is { IsOpen: true } && TopLevel.GetTopLevel(filterBox) is { } picker)
        {
            // Shown columns lead the list; the properties to add are further down.
            if (filterBox.FindAncestorOfType<DockPanel>()?.GetVisualDescendants().OfType<ScrollViewer>().LastOrDefault() is { } list)
            {
                picker.UpdateLayout();
                list.Offset = new Vector(0, list.Extent.Height);
                picker.UpdateLayout();
            }
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Settle(window);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Settle(window);
            var pickerList = filterBox.FindAncestorOfType<DockPanel>()?.GetVisualDescendants().OfType<ScrollViewer>().LastOrDefault();
            var box = picker.GetVisualDescendants().OfType<CheckBox>().FirstOrDefault(b => b.DataContext is TableColumnChoice { IsShown: false }
                && pickerList is not null && b.TranslatePoint(new Point(b.Bounds.Width / 2, b.Bounds.Height / 2), pickerList) is { } c
                && c.Y > 0 && c.Y < pickerList.Viewport.Height);
            var def = (box?.DataContext as TableColumnChoice)?.Def;
            void ClickBox()
            {
                var at = box!.TranslatePoint(new Point(box.Bounds.Width / 2, box.Bounds.Height / 2), picker)!.Value;
                RawInput.Send(picker, "MouseMove", at, RawInputModifiers.None);
                RawInput.Send(picker, "MouseDown", at, MouseButton.Left, RawInputModifiers.None);
                RawInput.Send(picker, "MouseUp", at, MouseButton.Left, RawInputModifiers.None);
                Settle(window);
            }
            if (box is not null)
            {
                ClickBox();
                var added =table.Columns.Any(c => c.Def == def) && table.Rows[0].Cells.Count == table.Columns.Count
                    && table.Rows[0].Cells[^1].Column.Def == def && CellsShowingOthers(rowsControl, table, complete: true).Count == 0;
                ClickBox();
                var removed = table.Columns.All(c => c.Def != def) && table.Rows[0].Cells.Count == table.Columns.Count
                    && CellsShowingOthers(rowsControl, table, complete: true).Count == 0;
                Check($"table: a click in the column picker shows {def?.Key} as a column with a cell per row ({added}), another hides it ({removed})",
                    added && removed);
            }
            else
                Check("table: the column picker lists a property to show", false);
            KeyStroke(window, K.Escape);
            Settle(window);
        }
        else
            Check("table: the Columns button opens the column picker", false);

        // Cells drawn at rest, editors on use: a switch, a text cell, a number cell, Tab.
        table.RefreshColumnChoices();
        if (table.ColumnChoices.Where(c => !c.IsShown).Select(c => c.Def).FirstOrDefault(d => d.Kind == PropertyKind.Toggle) is { } toggleDef)
        {
            table.SetColumnShown(toggleDef, true);
            Settle(window);
            // The new column is the last, off to the right: scrolled to, its cells are built.
            var sideways = rowsControl.FindAncestorOfType<ScrollViewer>()!;
            sideways.Offset = new Vector(sideways.Extent.Width, 0);
            DrawFrame(window);
            TableCellInput(window, table, rowsControl, toggleDef);
            table.SetColumnShown(toggleDef, false);
            sideways.Offset = default;
            Settle(window);
        }
        else
            Check("table: the type has a switch property to show as a column", false);
    }

    private static double SettleMs(TopLevel top)
    {
        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        Settle(top);
        return System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }

    private static double Median(List<double> samples) =>
        samples.Count == 0 ? double.NaN : samples.OrderBy(x => x).ElementAt(samples.Count / 2);

    private static string? AutomationPropertiesName(Control c) => Avalonia.Automation.AutomationProperties.GetName(c);

    /// <summary>
    /// The table cells on screen that aren't their own row's cell for their column, or whose editor doesn't show the
    /// asset's value: what a recycled row would show if a rebind missed.
    /// </summary>
    private static List<string> CellsShowingOthers(ItemsControl rowsControl, TableViewModel table, bool complete = false)
    {
        var sideways = rowsControl.FindAncestorOfType<ScrollViewer>();
        var wrong = new List<string>();
        foreach (var container in rowsControl.GetRealizedContainers().Where(c => c.IsEffectivelyVisible))
        {
            if (container.DataContext is not TableRowViewModel row)
                continue;
            var panel = container.GetVisualDescendants().OfType<TableCellsPanel>().FirstOrDefault();
            if (panel is null || row.Cells.Count != table.Columns.Count)
            {
                wrong.Add($"{row.Name}: {row.Cells.Count} cells for {table.Columns.Count} columns");
                continue;
            }
            // Settled, every column in view has its cell shown.
            if (complete && sideways is not null && panel.TranslatePoint(default, sideways) is { } left)
            {
                var shownColumns = panel.Children.Where(c => c.IsVisible && c.Opacity > 0).Select(c => (c.DataContext as TableCellViewModel)?.Column).ToHashSet();
                double x0 = 0;
                foreach (var col in table.Columns)
                {
                    if (x0 + col.Width > -left.X && x0 < -left.X + sideways.Viewport.Width && !shownColumns.Contains(col))
                        wrong.Add($"{row.Name}.{col.Key}: in view with no cell");
                    x0 += col.Width;
                }
            }
            // Cells are built where the table is scrolled to; each one built and shown must be its own row's, in its column's place.
            foreach (var cellView in panel.Children.Where(c => c.IsVisible && c.Opacity > 0))
            {
                if (cellView.DataContext is not TableCellViewModel cell || cell.Row != row)
                {
                    wrong.Add($"{row.Name}: a cell of {(cellView.DataContext as TableCellViewModel)?.Row.Name}");
                    continue;
                }
                var column = cell.Column;
                var index = table.Columns.IndexOf(column);
                var x = table.Columns.Take(index).Sum(c => c.Width);
                if (index < 0 || Math.Abs(cellView.Bounds.X - x) > 0.5)
                {
                    wrong.Add($"{row.Name}.{column.Key}: at x {cellView.Bounds.X:0}, its column starts at {x:0}");
                    continue;
                }
                var value = row.Record.Properties.GetValueOrDefault(column.Key, column.Def.Default);
                var host = cellView.GetVisualDescendants().OfType<TableCellValue>().FirstOrDefault();
                // A cell at rest draws its value; one in use holds its editor.
                if (host?.Display is { } display)
                {
                    string? shown = cell.Editor switch
                    {
                        TogglePropertyViewModel t => display.IsOn == t.IsOn && display.Text == t.StateText ? t.RawValue : "(switch disagrees)",
                        NumberPropertyViewModel n => display.Text == n.DisplayText ? n.RawValue : $"(shows '{display.Text}')",
                        ChoicePropertyViewModel c => display.Text == (c.Choices.Contains(c.Value) ? c.Value : "") ? c.RawValue : display.Text,
                        LinesPropertyViewModel l => display.Text == l.Summary ? l.RawValue : display.Text,
                        _ => display.Text,
                    };
                    if (host.DataContext != cell.Editor || cell.Editor.RawValue != value || (shown ?? "") != value)
                        wrong.Add($"{row.Name}.{column.Key}: draws '{shown}', is '{value}'");
                    continue;
                }
                var editorView = host?.LiveEditor;
                var parts = editorView?.GetVisualDescendants().ToList() ?? new List<Visual>();
                string? drawn = cell.Editor switch
                {
                    TogglePropertyViewModel t => parts.OfType<ToggleButton>().FirstOrDefault()?.IsChecked == t.IsOn ? t.RawValue : "(switch disagrees)",
                    NumberPropertyViewModel n => parts.OfType<TextBlock>().Any(b => b.Text == n.DisplayText) || parts.OfType<TextBox>().Any(b => b.Text == n.DisplayText)
                        ? n.RawValue : "(not shown)",
                    ChoicePropertyViewModel c => !c.Choices.Contains(c.Value) || Equals(parts.OfType<ComboBox>().FirstOrDefault()?.SelectedItem, c.Value)
                        ? c.RawValue : $"{parts.OfType<ComboBox>().FirstOrDefault()?.SelectedItem}",
                    _ => parts.OfType<TextBox>().FirstOrDefault(b => b.Classes.Contains("pfield"))?.Text ?? cell.Editor.RawValue,
                };
                if (editorView?.DataContext != cell.Editor || cell.Editor.RawValue != value || (drawn ?? "") != value)
                    wrong.Add($"{row.Name}.{column.Key}: shows '{drawn}', is '{value}'");
            }
        }
        return wrong;
    }
}
