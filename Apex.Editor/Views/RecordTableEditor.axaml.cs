using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Apex.Editor.Controls;
using Apex.Editor.Models;
using Apex.Editor.Services.Extensions;
using Apex.Editor.ViewModels;

namespace Apex.Editor.Views;

public partial class RecordTableEditor : UserControl
{
    /// <summary>The row number's column; the header and the Add button line up after it.</summary>
    private const double NumberWidth = 28;

    /// <summary>⚠ and ↑ ↓ ✕, always reserved so a problem or the hover never moves the cells.</summary>
    private const double ActionsWidth = 20 + 3 * 24;

    private const double RowHeight = 28;

    private RecordsPropertyViewModel? _listening;

    /// <summary>
    /// Each list's header and row controls, kept in the tree for as long as the editor lives: another weapon's table,
    /// a row added or removed, only rebinds rows and shows or hides the spare ones. Taking a row out of the tree and
    /// putting it back styles all eight of its editors again, which made switching to a weapon's table cost the better
    /// part of a second (two tables on a form swap editors whenever the form's rows are dealt out again).
    /// </summary>
    private readonly Dictionary<ExtensionRecordList, (StackPanel Table, StackPanel Rows)> _tables = new();

    private StackPanel? _rows;

    public RecordTableEditor()
    {
        InitializeComponent();
        // Handled keys too: a cell's own Enter (commit) runs first, and Ctrl+Enter must reach here past a number box.
        AddHandler(KeyDownEvent, Table_KeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(LostFocusEvent, (_, e) => PropertyEditorView.CommitPending(e.Source), RoutingStrategies.Bubble);
        AddHandler(Button.ClickEvent, Table_Click, RoutingStrategies.Bubble);
        AddHandler(GotFocusEvent, (_, e) => ScrollToCell(e.Source), RoutingStrategies.Bubble);
        AddHandler(PointerWheelChangedEvent, Table_PointerWheelChanged, RoutingStrategies.Tunnel);
        // A click anywhere in the table without Shift ends a marking of rows.
        AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (!e.KeyModifiers.HasFlag(KeyModifiers.Shift) && Vm is { } vm)
                SelectRows(vm, -1, -1);
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        HScroll.ValueChanged += (_, _) => Scroll(HScroll.Value);
        // A wide table in a narrow editor would size the thumb to a sliver: it stays a hit area. Set here because the
        // theme's template sets the thumb's minimum itself, which a style can't override.
        HScroll.TemplateApplied += (_, _) =>
        {
            foreach (var thumb in HScroll.GetVisualDescendants().OfType<Thumb>())
                thumb.MinWidth = 24;
        };
    }

    private RecordsPropertyViewModel? Vm => DataContext as RecordsPropertyViewModel;

    /// <summary>The form's row height for this table (the form scrolls to rows by adding heights up).</summary>
    public static double HeightOf(RecordsPropertyViewModel vm) => 30 + 20 + Math.Max(1, vm.Rows.Count) * RowHeight + 26 + 6;

    /// <summary>The table takes the width its row gives it and never asks for more: cells scroll their text.</summary>
    protected override Size MeasureOverride(Size availableSize) => base.MeasureOverride(availableSize).WithWidth(0);

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_listening is not null)
            _listening.Rows.CollectionChanged -= Rows_CollectionChanged;
        _listening = Vm;
        if (Vm is not { } vm)
            return;
        vm.Rows.CollectionChanged += Rows_CollectionChanged;
        // A pooled row can take another list (a slot table after a kick table): its header and rows are its own.
        if (!_tables.TryGetValue(vm.List, out var table))
        {
            table = Build(vm.List);
            _tables[vm.List] = table;
            RowList.Children.Add(table.Table);
        }
        foreach (var (_, other) in _tables)
            other.Table.IsVisible = other.Table == table.Table;
        _rows = table.Rows;
        Sync();
    }

    private void Rows_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => Sync();

    /// <summary>
    /// The first N row controls show the N rows, in order (rebinding only those whose row moved); the rest are hidden
    /// spares, bound to nothing.
    /// </summary>
    private void Sync()
    {
        if (Vm is not { } vm || _rows is null)
            return;
        var children = _rows.Children;
        while (children.Count < vm.Rows.Count)
            children.Add(BuildRow(vm.List));
        for (var i = 0; i < children.Count; i++)
        {
            var row = children[i];
            var item = i < vm.Rows.Count ? vm.Rows[i] : null;
            // A spare keeps the row it last showed: unbinding it emptied its cells, and showing it again (a paste, the
            // undo of a removal) then built and styled every editor in it anew, some 5 ms a row.
            if (item is not null && !ReferenceEquals(row.DataContext, item))
                row.DataContext = item;
            row.IsVisible = item is not null;
        }
        FitColumns(vm);
        Scroll(_offset);
    }

    // ── Column widths: each column as wide as what it holds ──────────────────

    /// <summary>A character of a cell's value (12 px mono) and of a column title (11 px text), for sizing columns.</summary>
    private const double ValueChar = 7.3, TitleChar = 6.6;

    // Each list's current column widths (they follow its rows: a table with short names gets short name columns).
    private readonly Dictionary<ExtensionRecordList, double[]> _widths = new();

    /// <summary>
    /// Sizes each column to its content: the longest value in it now (or, for a dropdown, its longest option) and its
    /// title, within a floor and a cap per kind. Stretching columns to the editor's width left gaps wider than the data
    /// between them. Fitted when the rows change, never while a value is typed, so a cell never moves under the caret.
    /// </summary>
    private void FitColumns(RecordsPropertyViewModel vm)
    {
        if (!_tables.TryGetValue(vm.List, out var table))
            return;
        var columns = vm.List.DisplayColumns;
        var widths = new double[columns.Count];
        for (var i = 0; i < columns.Count; i++)
        {
            var column = i;
            widths[i] = WidthOf(columns[i], vm.Rows.Select(r => column < r.Cells.Count ? r.Cells[column] : null).OfType<PropertyItemViewModel>());
        }
        if (_widths.TryGetValue(vm.List, out var old) && old.AsSpan().SequenceEqual(widths))
            return;
        _widths[vm.List] = widths;
        foreach (var line in table.Rows.Children.Prepend(table.Table.Children[0]).OfType<Grid>())
        {
            var defs = ((Grid)line.Children.OfType<Canvas>().First().Children[0]).ColumnDefinitions;
            for (var i = 0; i < defs.Count && i < widths.Length; i++)
                defs[i].Width = new GridLength(widths[i]);
        }
    }

    private static double WidthOf(RecordDisplayColumn c, IEnumerable<PropertyItemViewModel> cells)
    {
        var longest = cells.Select(p => p is ChoicePropertyViewModel choice ? choice.LabelOf(p.RawValue) : p.RawValue)
            .Append(c.Def.Default).Max(v => v.Length);
        var content = c.Def.Kind switch
        {
            PropertyKind.Toggle => 76,
            // The list's longest option, so picking another never needs a wider column; and its arrow.
            PropertyKind.Choice => Math.Clamp(Math.Max(longest, (c.Def.ChoiceLabels ?? c.Def.Choices).DefaultIfEmpty("").Max(s => s.Length))
                * ValueChar + 40, 64, 220),
            // The number and the field's room for its step buttons.
            PropertyKind.Number => Math.Clamp(longest * ValueChar + 40, 64, 140),
            // A reference's type glyph and its ▾ beside the name.
            PropertyKind.AssetRef => Math.Clamp(longest * ValueChar + 56, 140, 300),
            _ => Math.Clamp(longest * ValueChar + 20, 90, 280),
        };
        // The title fits too (a long one trims, whole in its tooltip), and the cell's gap to the next.
        var title = Math.Min(c.Def.Label.Length * TitleChar + 10, 160);
        return Math.Ceiling(Math.Max(content, title) + 4);
    }

    /// <summary>The columns' widths for <paramref name="list"/> (fitted, or each kind's floor before the first fit).</summary>
    private double[] WidthsOf(ExtensionRecordList list) =>
        _widths.TryGetValue(list, out var w) ? w : list.DisplayColumns.Select(c => WidthOf(c, [])).ToArray();

    // ── Sideways: the cells scroll when the editor is narrower than they are ──

    // How far the cells are scrolled, and the room between the row number and the row controls.
    private double _offset;
    private double _viewport;

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (e.WidthChanged)
            Scroll(_offset);
    }

    /// <summary>
    /// Lays the cells out at their fitted widths, with ⚠ ↑ ↓ ✕ right after them; when that is wider than the room there
    /// is, the cells move left by <paramref name="offset"/> (the bar beside Add row, Shift+wheel, or Tab into a hidden
    /// cell) while the row number and ⚠ ↑ ↓ ✕ stay put.
    /// </summary>
    private void Scroll(double offset)
    {
        if (Vm is not { } vm || !_tables.TryGetValue(vm.List, out var table))
            return;
        var need = WidthsOf(vm.List).Sum();
        _viewport = Math.Max(0, Bounds.Width - NumberWidth - ActionsWidth);
        // Visibility="Auto": the bar shows exactly while there is something to scroll.
        var max = need - _viewport > 0.5 ? need - _viewport : 0;
        _offset = Math.Clamp(offset, 0, max);
        HScroll.Maximum = max;
        HScroll.ViewportSize = _viewport;
        HScroll.LargeChange = Math.Max(1, _viewport * 0.9);
        HScroll.SmallChange = 40;
        if (HScroll.Value != _offset)
            HScroll.Value = _offset;
        var shown = Math.Min(_viewport, need);
        foreach (var line in table.Rows.Children.Prepend(table.Table.Children[0]).OfType<Grid>())
        {
            var clip = line.Children.OfType<Canvas>().First();
            clip.Width = shown;
            var cells = (Grid)clip.Children[0];
            cells.Width = need;
            cells.RenderTransform = _offset == 0 ? null : new Avalonia.Media.TranslateTransform(-_offset, 0);
        }
    }

    /// <summary>Tab or an arrow into a cell scrolled out of sight brings it in.</summary>
    private void ScrollToCell(object? source)
    {
        if (!HScroll.IsVisible || (source as Visual)?.GetSelfAndVisualAncestors().OfType<ContentControl>()
                .FirstOrDefault(c => c.Classes.Contains("rcell")) is not { } cell)
            return;
        var left = cell.Bounds.X;
        var right = left + cell.Bounds.Width;
        if (left < _offset)
            Scroll(left);
        else if (right > _offset + _viewport)
            Scroll(right - _viewport);
    }

    /// <summary>Shift+wheel (or a sideways wheel) over a table that scrolls moves it; a plain wheel scrolls the form.</summary>
    private void Table_PointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (!HScroll.IsVisible)
            return;
        var delta = e.Delta.X != 0 ? e.Delta.X : e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? e.Delta.Y : 0;
        if (delta == 0)
            return;
        Scroll(_offset - delta * 50);
        e.Handled = true;
    }

    /// <summary>The rows showing (spares are hidden), in order.</summary>
    private IEnumerable<Control> ShownRows => _rows?.Children.Where(c => c.IsVisible) ?? Enumerable.Empty<Control>();

    // ── Layout: one grid per row, sharing column widths with the header ─────

    /// <summary>The cells' columns, at each kind's floor until <see cref="FitColumns"/> fits them to the rows.</summary>
    private static ColumnDefinitions CellColumns(ExtensionRecordList list)
    {
        var defs = new ColumnDefinitions();
        foreach (var c in list.DisplayColumns)
            defs.Add(new ColumnDefinition(WidthOf(c, []), GridUnitType.Pixel));
        return defs;
    }

    /// <summary>
    /// A header or row line: the row number, the cells (clipped to the room there is and moved by the table's scroll
    /// offset), then the row controls (⚠ ↑ ↓ ✕) right after them; what's left of the line stays empty.
    /// </summary>
    private static Grid Line(ExtensionRecordList list, double height, out Grid cells)
    {
        // A canvas asks for no width itself, so cells wider than the room never widen the line (a stack panel would
        // lay a line out at its full desired width, pushing the row controls past the editor's edge).
        cells = new Grid { ColumnDefinitions = CellColumns(list), Height = height };
        cells.Classes.Add("rcells");
        var clip = new Canvas { ClipToBounds = true, Children = { cells } };
        Grid.SetColumn(clip, 1);
        return new Grid
        {
            Height = height,
            ColumnDefinitions =
            {
                new(NumberWidth, GridUnitType.Pixel), new(1, GridUnitType.Auto), new(ActionsWidth, GridUnitType.Pixel), new(1, GridUnitType.Star),
            },
            Children = { clip },
        };
    }

    private static (StackPanel Table, StackPanel Rows) Build(ExtensionRecordList list)
    {
        var header = Line(list, 20, out var cells);
        for (var i = 0; i < list.DisplayColumns.Count; i++)
        {
            var c = list.DisplayColumns[i];
            var label = new TextBlock { Text = c.Def.Label };
            label.Classes.Add("rhead");
            ToolTip.SetTip(label, ColumnTip(list, c));
            Grid.SetColumn(label, i);
            cells.Children.Add(label);
        }
        var rows = new StackPanel();
        return (new StackPanel { Children = { header, rows } }, rows);
    }

    /// <summary>The manifest's description, then Apex's own facts: where the column is stored and what empty means.</summary>
    private static string ColumnTip(ExtensionRecordList list, RecordDisplayColumn display)
    {
        var tip = display.Def.Description.Length > 0 ? display.Def.Description : display.Def.Label;
        if (display.Combine is { } combine)
        {
            var names = combine.Columns.Select(i => list.Columns[i].Name).ToList();
            return tip + $"\n\nStored as the {string.Join(" and ", names)} fields of each {list.Def.Key} row; a choice sets them together.";
        }
        var c = list.Columns[display.Column];
        tip += $"\n\nStored as the {c.Name} field of each {list.Def.Key} row.";
        if (c.Prefix.Length > 0)
            tip += $" Written as {c.Prefix}<value>.";
        if (c.Def.Default.Length > 0)
            tip += $"\n\nEmpty means {c.Def.Default}.";
        if (c.Optional)
            tip += "\n\nOptional.";
        return tip;
    }

    private static Grid BuildRow(ExtensionRecordList list)
    {
        var grid = Line(list, RowHeight, out var cells);
        grid.Classes.Add("rrow");
        grid.BindClass("selected", new Avalonia.Data.Binding(nameof(RecordRowViewModel.IsSelected)), null!);

        var number = new TextBlock();
        number.Classes.Add("rnum");
        number.Bind(TextBlock.TextProperty, new Avalonia.Data.Binding(nameof(RecordRowViewModel.Number)));
        grid.Children.Add(number);

        for (var i = 0; i < list.DisplayColumns.Count; i++)
        {
            var cell = new ContentControl
            {
                Focusable = false,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 2, 4, 2),
            };
            cell.Classes.Add("rcell");
            cell.Bind(ContentProperty, new Avalonia.Data.Binding($"{nameof(RecordRowViewModel.Cells)}[{i}]"));
            ToolTip.SetTip(cell, ColumnTip(list, list.DisplayColumns[i]));
            Grid.SetColumn(cell, i);
            cells.Children.Add(cell);
        }

        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var problem = new TextBlock { Text = "⚠", Width = 20, TextAlignment = Avalonia.Media.TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        problem.Classes.Add("problem");
        problem.Bind(IsVisibleProperty, new Avalonia.Data.Binding(nameof(RecordRowViewModel.HasProblem)));
        problem.Bind(ToolTip.TipProperty, new Avalonia.Data.Binding(nameof(RecordRowViewModel.Problem)));
        var reserve = new Panel { Width = 20, Children = { problem } };
        actions.Children.Add(reserve);
        actions.Children.Add(Action("↑", "up", "Move up (Alt+↑)"));
        actions.Children.Add(Action("↓", "down", "Move down (Alt+↓)"));
        actions.Children.Add(Action("✕", "remove", "Remove row (Alt+Delete)"));
        Grid.SetColumn(actions, 2);
        grid.Children.Add(actions);
        return grid;
    }

    private static Button Action(string glyph, string what, string tip)
    {
        // Not focusable (Tab walks the cells), and acting on press: clicking ✕ again where the next row has just moved
        // up removes that one too, as the line list's ✕ does.
        var b = new Button { Content = glyph, Focusable = false, ClickMode = ClickMode.Press, Tag = what };
        b.Classes.Add("rowaction");
        b.Classes.Add("raction");
        ToolTip.SetTip(b, tip);
        Avalonia.Automation.AutomationProperties.SetName(b, tip[..tip.IndexOf(" (", StringComparison.Ordinal)]);
        return b;
    }

    // ── Where a control sits ─────────────────────────────────────────────────

    /// <summary>The row and column (0-based, -1 outside a cell) a control sits in; null outside any row.</summary>
    private (RecordRowViewModel Row, int Column)? CellOf(object? source)
    {
        if (source is not Visual v)
            return null;
        var grid = v.GetSelfAndVisualAncestors().TakeWhile(a => a != this).OfType<Grid>().FirstOrDefault(g => g.Classes.Contains("rrow"));
        if (grid?.DataContext is not RecordRowViewModel row)
            return null;
        var cell = v.GetSelfAndVisualAncestors().TakeWhile(a => a != grid).OfType<ContentControl>().LastOrDefault(c => c.Classes.Contains("rcell"));
        return (row, cell is null ? -1 : Grid.GetColumn(cell));
    }

    /// <summary>The ↑/↓ belongs to the table (there is a row that way); otherwise it walks the editor's rows.</summary>
    public static bool OwnsArrows(object? source, Key key)
    {
        if ((source as Visual)?.FindAncestorOfType<RecordTableEditor>() is not { } table || table.Vm is not { } vm
            || table.CellOf(source) is not var (row, _))
            return false;
        var index = vm.Rows.IndexOf(row);
        return key == Key.Up ? index > 0 : key == Key.Down && index < vm.Rows.Count - 1;
    }

    // ── Mouse: ↑ ↓ ✕ on a row, Add row ───────────────────────────────────────

    private void Table_Click(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || e.Source is not Button button)
            return;
        if (button == AddButton)
        {
            e.Handled = true;
            if (vm.Insert(vm.Rows.Count) is not null)
                FocusCell(vm.Rows.Count - 1, 0);
            return;
        }
        if (button.Tag is not string what || CellOf(button) is not var (row, _))
            return;
        e.Handled = true;
        switch (what)
        {
            case "up":
                vm.Move(row, -1);
                break;
            case "down":
                vm.Move(row, 1);
                break;
            case "remove":
                vm.Remove(row);
                break;
        }
    }

    // ── Keys ─────────────────────────────────────────────────────────────────

    private void Table_KeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm is not { } vm)
            return;
        // On + Add row, Ctrl+V adds the pasted rows at the end.
        if (e.Source == AddButton && e.Key == Key.V && e.KeyModifiers == KeyModifiers.Control)
        {
            e.Handled = true;
            PasteAsync(vm, vm.Rows.Count, 0, null);
            return;
        }
        if (e.Source == AddButton || CellOf(e.Source) is not var (row, column))
            return;
        // A cell's own dropdown or suggestion list takes its keys first.
        if ((e.Source as ILogical)?.FindLogicalAncestorOfType<ComboBox>(includeSelf: true) is { IsDropDownOpen: true }
            || (e.Source as Visual)?.FindAncestorOfType<SuggestBox>() is { IsOpen: true })
            return;
        var index = vm.Rows.IndexOf(row);
        column = Math.Max(0, column);
        switch (e.Key)
        {
            case Key.Up or Key.Down when e.KeyModifiers == KeyModifiers.Shift && !(e.Source is TextBox { Classes: var typing } && typing.Contains("scrubedit")):
                // Shift+↑↓ marks rows from where the marking started, for Ctrl+C and Ctrl+V.
                var end = index + (e.Key == Key.Up ? -1 : 1);
                e.Handled = true;
                if (end < 0 || end >= vm.Rows.Count)
                    break;
                PropertyEditorView.CommitPending(e.Source);
                _anchor = _anchor is { } a && vm.Rows.Contains(a) ? a : row;
                SelectRows(vm, vm.Rows.IndexOf(_anchor), end);
                FocusCell(end, column);
                break;
            case Key.C when e.KeyModifiers == KeyModifiers.Control
                            && !(e.Source is TextBox { SelectionStart: var from, SelectionEnd: var to } && from != to):
                e.Handled = true;
                CopyRows(vm, row);
                break;
            case Key.V when e.KeyModifiers == KeyModifiers.Control:
                e.Handled = true;
                PasteAsync(vm, vm.Rows.FirstOrDefault(r => r.IsSelected) is { } first ? vm.Rows.IndexOf(first) : index, column, e.Source as TextBox);
                break;
            case Key.Enter when e.KeyModifiers == KeyModifiers.Control:
                PropertyEditorView.CommitPending(e.Source);
                e.Handled = true;
                if (vm.Insert(index + 1) is not null)
                    FocusCell(index + 1, column);
                else if (TopLevel.GetTopLevel(this)?.DataContext is MainViewModel full)
                    full.Status = vm.AddTip;
                break;
            case Key.Delete when e.KeyModifiers == KeyModifiers.Alt:
                e.Handled = true;
                vm.Remove(row);
                if (vm.Rows.Count > 0)
                    FocusCell(Math.Min(index, vm.Rows.Count - 1), column);
                else
                    Dispatcher.UIThread.Post(() => AddButton.Focus(NavigationMethod.Directional), DispatcherPriority.Loaded);
                break;
            case Key.Up or Key.Down when e.KeyModifiers == KeyModifiers.Alt:
                PropertyEditorView.CommitPending(e.Source);
                e.Handled = true;
                var delta = e.Key == Key.Up ? -1 : 1;
                if (vm.Move(row, delta))
                    FocusCell(index + delta, column);
                break;
            case Key.Up or Key.Down when e.KeyModifiers == KeyModifiers.None && !e.Handled:
                // A number being typed steps with ↑↓; everywhere else they walk the column.
                if (e.Source is TextBox t && t.Classes.Contains("scrubedit"))
                    break;
                SelectRows(vm, -1, -1);
                var next = index + (e.Key == Key.Up ? -1 : 1);
                if (next < 0 || next >= vm.Rows.Count)
                    break;
                PropertyEditorView.CommitPending(e.Source);
                e.Handled = true;
                FocusCell(next, column);
                break;
            case Key.Enter when e.KeyModifiers == KeyModifiers.None:
                // A field the row editor never sees commits here (a reference field's own Enter already did).
                PropertyEditorView.CommitPending(e.Source);
                break;
            case Key.Escape when !e.Handled:
                if (PropertyEditorView.DiscardPending(e.Source))
                    e.Handled = true;
                else if (vm.Rows.Any(r => r.IsSelected))
                {
                    SelectRows(vm, -1, -1);
                    e.Handled = true;
                }
                break;
        }
    }

    // ── Rows in and out: Shift+↑↓ marks, Ctrl+C copies, Ctrl+V pastes ──────

    // Where Shift+↑↓ started marking rows.
    private RecordRowViewModel? _anchor;

    /// <summary>Marks rows <paramref name="from"/> to <paramref name="to"/> (either order); -1 marks none.</summary>
    private void SelectRows(RecordsPropertyViewModel vm, int from, int to)
    {
        if (from < 0)
            _anchor = null;
        var (lo, hi) = from <= to ? (from, to) : (to, from);
        for (var i = 0; i < vm.Rows.Count; i++)
        {
            var on = from >= 0 && i >= lo && i <= hi;
            if (vm.Rows[i].IsSelected != on)
                vm.Rows[i].IsSelected = on;
        }
    }

    private MainViewModel? Shell => TopLevel.GetTopLevel(this)?.DataContext as MainViewModel;

    /// <summary>The marked rows, or the row the keyboard is in, onto the clipboard as stored (one record per line).</summary>
    private void CopyRows(RecordsPropertyViewModel vm, RecordRowViewModel focused)
    {
        var rows = vm.Rows.Where(r => r.IsSelected).ToList();
        if (rows.Count == 0)
            rows.Add(focused);
        Shell?.Shell?.CopyText(RecordsPropertyViewModel.Copy(rows));
        if (Shell is { } shell)
            shell.Status = rows.Count == 1
                ? $"Copied {vm.Label} row {rows[0].Number}. Ctrl+V in a table pastes it."
                : $"Copied {vm.Label} rows {rows[0].Number}–{rows[^1].Number}. Ctrl+V in a table pastes them.";
    }

    /// <summary>
    /// Ctrl+V: rows on the clipboard (more than one line, or fields split by commas or tabs) are pasted over the table
    /// from row <paramref name="at"/> down, adding rows past the last; a single bare value goes into the text field the
    /// keyboard is in, as any paste would.
    /// </summary>
    private async void PasteAsync(RecordsPropertyViewModel vm, int at, int column, TextBox? box)
    {
        try
        {
            if (Shell is not { Shell: { } shellView } shell)
                return;
            var (read, text) = await shellView.GetClipboardTextAsync();
            if (!read || string.IsNullOrEmpty(text))
            {
                shell.Status = read ? "The clipboard holds no text to paste." : "Apex couldn't read the clipboard: another program has it open. Try again.";
                return;
            }
            var trimmed = text.Trim();
            if (!trimmed.Contains('\n') && !trimmed.Contains(',') && !trimmed.Contains('\t'))
            {
                if (box is not null)
                    box.Paste();
                else
                    shell.Status = $"Ctrl+V here pastes rows: one per line, fields separated by commas or tabs.";
                return;
            }
            var records = RecordCodec.ParseRows(vm.List, text);
            PropertyEditorView.CommitPending(box);
            var (replaced, added, leftOut) = vm.Paste(at, records);
            var pasted = replaced + added;
            if (pasted == 0)
            {
                shell.Status = $"{vm.Label} holds up to {vm.List.MaxRows} rows, so nothing was pasted.";
                return;
            }
            SelectRows(vm, at, at + pasted - 1);
            FocusCell(at, column);
            var what = (replaced, added) switch
            {
                (0, _) => $"added {added} row{(added == 1 ? "" : "s")}",
                (_, 0) => $"replaced row{(replaced == 1 ? $" {at + 1}" : $"s {at + 1}–{at + replaced}")}",
                _ => $"replaced rows {at + 1}–{at + replaced} and added {added}",
            };
            var undo = Commands.CommandCatalog.Get(Commands.CommandCatalog.Undo).GestureText;
            shell.Status = $"Pasted into {vm.Label}: {what}."
                + (leftOut > 0 ? $" {leftOut} more didn't fit: it holds up to {vm.List.MaxRows}." : "")
                + (vm.Rows.Skip(at).Take(pasted).Count(r => r.HasProblem) is var bad and > 0
                    ? $" {bad} of them {(bad == 1 ? "has a problem" : "have problems")} (⚠)." : "")
                + $" {undo} undoes it.";
        }
        catch (Exception)
        {
            // An async void handler: nothing may escape it.
        }
    }

    /// <summary>Focuses the editor in row <paramref name="index"/>'s cell <paramref name="column"/> once the rows have settled.</summary>
    public void FocusCell(int index, int column) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (ShownRows.ElementAtOrDefault(index) is not { } container)
                return;
            var cells = container.GetVisualDescendants().OfType<ContentControl>()
                .Where(c => c.Content is PropertyItemViewModel && c.FindAncestorOfType<RecordTableEditor>() == this).ToList();
            if (cells.Count == 0)
                return;
            var cell = cells[Math.Clamp(column, 0, cells.Count - 1)];
            var editor = cell.GetVisualDescendants().OfType<InputElement>()
                .FirstOrDefault(c => c is ScrubNumberBox or TextBox or ComboBox or ToggleButton && c.Focusable && c.IsEffectivelyEnabled);
            if (editor is null)
                return;
            editor.Focus(NavigationMethod.Directional);
            if (editor is TextBox box)
                box.SelectAll();
            container.BringIntoView();
        }, DispatcherPriority.Loaded);
}
