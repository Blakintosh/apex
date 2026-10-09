using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.VisualTree;
using K = Avalonia.Input.Key;
using Apex.Editor.Controls;
using Apex.Editor.Models;
using Apex.Editor.ViewModels;
using Apex.Editor.Views;

namespace Apex.Shots;

/// <summary>
/// Table cells draw their values and take an editor when used: they look the same either way (pixel for pixel, both
/// themes), and a pointer or the keyboard reaches the real editor in one click or one key.
/// </summary>
public partial class Program
{
    /// <summary>The visible value of a table cell whose property matches.</summary>
    private static TableCellValue? CellHost(Visual root, Func<PropertyItemViewModel, bool> match) =>
        root.GetVisualDescendants().OfType<TableCellValue>()
            .FirstOrDefault(h => h.IsEffectivelyVisible && h.IsEffectivelyEnabled && h.Bounds.Width > 0 && OnScreen(h)
                                 && h.DataContext is PropertyItemViewModel p && match(p));

    /// <summary>Wholly inside every scroller around it.</summary>
    private static bool OnScreen(Visual v) =>
        v.GetVisualAncestors().OfType<ScrollViewer>().All(sv => v.TranslatePoint(default, sv) is { } at
            && new Rect(sv.Viewport).Contains(new Rect(at, v.Bounds.Size)));

    /// <summary>Draws a frame (two ticks: the second commits it), so hit testing sees what layout placed.</summary>
    private static void DrawFrame(TopLevel top)
    {
        Settle(top);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Settle(top);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Settle(top);
    }

    /// <summary>The pointer onto a cell, and a frame drawn, as before any real click: the cell's editor.</summary>
    private static PropertyEditorView? HoverCell(Window window, TableCellValue host, Point? at = null)
    {
        // The pointer finds the cell in the drawn scene: draw what layout placed first.
        DrawFrame(window);
        RawInput.Send(window, "MouseMove", at ?? CentreOf(window, host), RawInputModifiers.None);
        DrawFrame(window);
        return host.LiveEditor;
    }

    /// <summary>The pointer somewhere no cell is, so every cell goes back to its drawing.</summary>
    private static void PointerAway(Window window)
    {
        RawInput.Send(window, "MouseMove", new Point(2, 2), RawInputModifiers.None);
        DrawFrame(window);
    }

    /// <summary>
    /// Opens <paramref name="rows"/> with <paramref name="defs"/> shown, once with every cell holding its editor (as
    /// before) and once drawn, in each theme, and compares every cell on screen pixel for pixel.
    /// </summary>
    private static void TableCellLook(Window window, MainViewModel vm, IReadOnlyList<AssetRecord> rows, IReadOnlyList<PropertyDef> defs, string label)
    {
        const int Tolerance = 16;
        var compared = new SortedSet<string>();
        var differ = new List<string>();
        int cells = 0, exact = 0;
        var shotDir = Path.Combine(SaveRoot, "cell-look");
        Directory.CreateDirectory(shotDir);
        foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
        {
            Application.Current!.RequestedThemeVariant = theme;
            PointerAway(window);
            var editors = Shoot(true, $"{Safe(label)}-{theme}-editors.png");
            var drawn = Shoot(false, $"{Safe(label)}-{theme}-drawn.png");
            foreach (var (key, (kind, a, w, h)) in editors)
            {
                if (!drawn.TryGetValue(key, out var b) || b.Pixels.Length != a.Length)
                    continue;
                compared.Add(kind);
                cells++;
                if (a.AsSpan().SequenceEqual(b.Pixels))
                    exact++;
                var bad = 0;
                // Antialiasing may land a few levels apart (the same shapes, composed through other layers: the
                // switch's edge by 1-2, the dropdown glyph's tips by up to 14, now that a list opener draws at full strength); a change anyone could see is far past that.
                for (var i = 0; i < a.Length; i++)
                    if ((i & 3) != 3 && Math.Abs(a[i] - b.Pixels[i]) > Tolerance)
                    {
                        bad++;
                        i |= 3;
                    }
                if (bad > 0)
                {
                    differ.Add($"{theme} {key}: {bad} px");
                    Save(Path.Combine(shotDir, $"{theme}-{Safe(key)}-editor.png"), a, w, h);
                    Save(Path.Combine(shotDir, $"{theme}-{Safe(key)}-drawn.png"), b.Pixels, w, h);
                }
            }
        }
        Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
        TableCellValue.EditorsEverywhere = false;
        Check($"{label}: every cell drawn at rest matches its editor at rest pixel for pixel (within {Tolerance}/255), dark and light ({string.Join(", ", compared)}; "
              + $"{exact} of {cells} identical to the bit, {differ.Count} differ{string.Concat(differ.Take(4).Select(d => "; " + d))})",
            differ.Count == 0 && compared.Count > 0);

        Dictionary<string, (string Kind, byte[] Pixels, int W, int H)> Shoot(bool everywhere, string shot)
        {
            TableCellValue.EditorsEverywhere = everywhere;
            vm.OpenTableFor(rows.ToList());
            Settle(window);
            var table = vm.Table!;
            foreach (var def in defs)
                if (table.Columns.All(c => c.Def != def))
                    table.SetColumnShown(def, true);
            var tableView = window.GetVisualDescendants().OfType<Apex.Editor.Views.TableView>().First();
            var rowsControl = tableView.FindControl<ItemsControl>("Rows")!;
            var sideways = rowsControl.FindAncestorOfType<ScrollViewer>()!;
            var shots = new Dictionary<string, (string, byte[], int, int)>();
            for (double x = 0; ; x += Math.Max(200, sideways.Viewport.Width - 300))
            {
                sideways.Offset = new Vector(Math.Min(x, Math.Max(0, sideways.Extent.Width - sideways.Viewport.Width)), 0);
                DrawFrame(window);
                using var frame = window.CaptureRenderedFrame();
                if (frame is null)
                    break;
                if (x == 0)
                    frame.Save(Path.Combine(shotDir, shot), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
                using var fb = frame.Lock();
                var view = new Rect(sideways.TranslatePoint(default, window)!.Value, sideways.Viewport);
                foreach (var host in rowsControl.GetVisualDescendants().OfType<TableCellValue>())
                {
                    if (!host.IsEffectivelyVisible || host.FindAncestorOfType<Border>() is not { } cell || cell.Opacity == 0
                        || cell.DataContext is not TableCellViewModel c || host.TranslatePoint(default, window) is not { } at0)
                        continue;
                    var at = cell.TranslatePoint(default, window)!.Value;
                    var rect = new PixelRect((int)at.X, (int)at.Y, (int)cell.Bounds.Width, (int)cell.Bounds.Height);
                    if (!view.Contains(new Rect(rect.X, rect.Y, rect.Width, rect.Height)) || rect.Right > fb.Size.Width || rect.Bottom > fb.Size.Height)
                        continue;
                    var key = $"{c.Row.Name}.{c.Column.Key}";
                    if (shots.ContainsKey(key))
                        continue;
                    var px = new byte[rect.Width * rect.Height * 4];
                    for (var y = 0; y < rect.Height; y++)
                        Marshal.Copy(fb.Address + (rect.Y + y) * fb.RowBytes + rect.X * 4, px, y * rect.Width * 4, rect.Width * 4);
                    shots[key] = (c.Editor.GetType().Name.Replace("PropertyViewModel", ""), px, rect.Width, rect.Height);
                }
                if (sideways.Offset.X >= sideways.Extent.Width - sideways.Viewport.Width - 1)
                    break;
            }
            sideways.Offset = default;
            table.CloseCommand.Execute(null);
            Settle(window);
            return shots;
        }

        static string Safe(string s) => string.Concat(s.Select(ch => char.IsLetterOrDigit(ch) || ch is '_' or '.' ? ch : '_'));

        static void Save(string path, byte[] px, int w, int h)
        {
            using var bmp = new Avalonia.Media.Imaging.WriteableBitmap(new PixelSize(w, h), new Vector(96, 96),
                Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Premul);
            using (var fb = bmp.Lock())
                for (var y = 0; y < h; y++)
                    Marshal.Copy(px, y * w * 4, fb.Address + y * fb.RowBytes, w * 4);
            bmp.Save(path, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        }
    }

    /// <summary>
    /// Real input on drawn cells: a switch toggles in one click (after hovering, and on a press that beats the frame),
    /// a click in a text cell puts the caret there, a number cell starts typing, Tab moves the keyboard into the next
    /// cell's editor, and cells let go of their editors when the pointer and keyboard leave.
    /// </summary>
    private static void TableCellInput(Window window, TableViewModel table, ItemsControl rowsControl, PropertyDef toggleDef)
    {
        string Value(TableRowViewModel r, PropertyDef d) => r.Record.Properties.GetValueOrDefault(d.Key, d.Default);
        TableRowViewModel RowOf(Visual v) => v.GetVisualAncestors().OfType<StyledElement>().Select(e => e.DataContext).OfType<TableRowViewModel>().First();
        var switches = rowsControl.GetVisualDescendants().OfType<TableCellValue>()
            .Where(h => h.IsEffectivelyVisible && h.DataContext is TogglePropertyViewModel p && p.Key == toggleDef.Key).Take(2).ToList();
        if (switches.Count < 2)
        {
            Check($"table cells: two {toggleDef.Key} switch cells on screen", false);
            return;
        }
        PointerAway(window);
        var drawnAtRest = switches.All(h => h.LiveEditor is null && h.Display is { IsOn: not null });

        // Hover, then one click.
        var host = switches[0];
        var row = RowOf(host);
        var before = Value(row, toggleDef);
        var editor = HoverCell(window, host);
        var toggle = editor?.GetVisualDescendants().OfType<ToggleButton>().FirstOrDefault(t => t.IsEffectivelyVisible);
        if (toggle is not null)
        {
            var at = CentreOf(window, toggle);
            RawInput.Send(window, "MouseDown", at, MouseButton.Left, RawInputModifiers.None);
            RawInput.Send(window, "MouseUp", at, MouseButton.Left, RawInputModifiers.None);
            Settle(window);
        }
        var after = Value(row, toggleDef);
        KeyStroke(window, K.Z, RawInputModifiers.Control);
        Settle(window);
        Check($"table cells: a switch cell at rest is drawn; hovered, it is the switch, and ONE click flips {row.Name}.{toggleDef.Key} ('{before}' → '{after}'), Ctrl+Z takes it back ('{Value(row, toggleDef)}')",
            drawnAtRest && toggle is not null && after != before && Value(row, toggleDef) == before);

        // A press that lands before a frame shows the editor (the pointer only just came onto the cell): still one click.
        PointerAway(window);
        host = switches[1];
        row = RowOf(host);
        before = Value(row, toggleDef);
        // On the switch's track (the drawing's first 26 px).
        var point = host.TranslatePoint(new Point(13, host.Bounds.Height / 2), window)!.Value;
        RawInput.Send(window, "MouseMove", point, RawInputModifiers.None);
        RawInput.Send(window, "MouseDown", point, MouseButton.Left, RawInputModifiers.None);
        DrawFrame(window);
        RawInput.Send(window, "MouseUp", point, MouseButton.Left, RawInputModifiers.None);
        Settle(window);
        after = Value(row, toggleDef);
        KeyStroke(window, K.Z, RawInputModifiers.Control);
        Settle(window);
        Check($"table cells: a press on a switch cell before its editor is drawn still flips it in ONE click ('{before}' → '{after}', undone '{Value(row, toggleDef)}')",
            after != before && Value(row, toggleDef) == before);

        // A text cell: one click puts the caret where it lands (the text columns are at the left).
        rowsControl.FindAncestorOfType<ScrollViewer>()!.Offset = default;
        PointerAway(window);
        var textHost = CellHost(rowsControl, p => p is TextPropertyViewModel { Value.Length: > 1 and < 12 } && !p.IsRuleDisabled);
        if (textHost is not null)
        {
            var text = ((TextPropertyViewModel)textHost.DataContext!).Value;
            HoverCell(window, textHost);
            var box = textHost.LiveEditor?.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(b => b.Classes.Contains("pfield"));
            if (box is not null)
            {
                // Past the end of the text, inside the field: the caret goes to the end, nothing selected.
                var at = box.TranslatePoint(new Point(box.Bounds.Width - 12, box.Bounds.Height / 2), window)!.Value;
                RawInput.Send(window, "MouseMove", at, RawInputModifiers.None);
                RawInput.Send(window, "MouseDown", at, MouseButton.Left, RawInputModifiers.None);
                RawInput.Send(window, "MouseUp", at, MouseButton.Left, RawInputModifiers.None);
                Settle(window);
            }
            Check($"table cells: ONE click in a text cell ('{text}') puts the caret there (focused {box?.IsFocused}, caret {box?.CaretIndex} of {text.Length}, selection {box?.SelectionStart}–{box?.SelectionEnd})",
                box is { IsFocused: true } && box.CaretIndex == text.Length && box.SelectionStart == box.SelectionEnd);

            // Tab: the keyboard goes into the next cell's editor; the cell left behind goes back to its drawing.
            PointerAway(window);
            KeyStroke(window, K.Tab);
            DrawFrame(window);
            var focused = window.FocusManager?.GetFocusedElement() as Visual;
            var next = focused?.FindAncestorOfType<TableCellValue>();
            Check($"table cells: Tab from a text cell puts the keyboard in the next cell's editor ({focused?.GetType().Name} in {(next?.DataContext as PropertyItemViewModel)?.Key}), and the cell left goes back to its drawing ({textHost.LiveEditor is null})",
                next is not null && next != textHost && next.LiveEditor is not null && next.LiveEditor.IsVisualAncestorOf(focused!) && textHost.LiveEditor is null);
            KeyStroke(window, K.Tab, RawInputModifiers.Shift);
            DrawFrame(window);
            focused = window.FocusManager?.GetFocusedElement() as Visual;
            Check($"table cells: Shift+Tab goes back into the text cell's editor ({focused?.GetType().Name})",
                textHost.LiveEditor is { } back && focused is not null && back.IsVisualAncestorOf(focused));
            rowsControl.FindAncestorOfType<Apex.Editor.Views.TableView>()!.FindControl<Button>("ColumnsButton")!.Focus();
            DrawFrame(window);
        }
        else
            Check("table cells: a text cell with a short value on screen", false);

        // A number cell: one click starts typing in it.
        PointerAway(window);
        var numberHost = CellHost(rowsControl, p => p is NumberPropertyViewModel && !p.IsRuleDisabled);
        if (numberHost is not null)
        {
            var at = CentreOf(window, numberHost);
            HoverCell(window, numberHost, at);
            RawInput.Send(window, "MouseDown", at, MouseButton.Left, RawInputModifiers.None);
            RawInput.Send(window, "MouseUp", at, MouseButton.Left, RawInputModifiers.None);
            Settle(window);
            var scrub = numberHost.LiveEditor?.GetVisualDescendants().OfType<ScrubNumberBox>().FirstOrDefault();
            Check($"table cells: ONE click in a number cell starts typing in it ({scrub?.Classes.Contains("editing")})", scrub?.Classes.Contains("editing") == true);
            KeyStroke(window, K.Escape);
            rowsControl.FindAncestorOfType<Apex.Editor.Views.TableView>()!.FindControl<Button>("ColumnsButton")!.Focus();
        }
        else
            Check("table cells: a number cell on screen", false);

        TableCellInUse(window, table, rowsControl);

        // Let go: no cell holds an editor once the pointer and keyboard have left.
        rowsControl.FindAncestorOfType<Apex.Editor.Views.TableView>()!.FindControl<Button>("ColumnsButton")!.Focus();
        PointerAway(window);
        var held = rowsControl.GetVisualDescendants().OfType<TableCellValue>().Count(h => h.LiveEditor is not null);
        var editorsShown = rowsControl.GetVisualDescendants().OfType<PropertyEditorView>().Count();
        Check($"table cells: with the pointer and keyboard gone, every cell is drawn again ({held} hold an editor, {editorsShown} editors in the table)",
            held == 0 && editorsShown == 0);
    }

    /// <summary>
    /// A cell's editor while it is in use: a scrub drag that leaves the cell keeps it, typing pending when the cell
    /// moves to another row (a column hidden, a sort) never reaches another row, a suggestion list open then closes,
    /// Tab onto a cell whose editor has nothing to focus keeps the keyboard, the change bar follows the theme, and the
    /// drawing tells assistive tech the column, value and problem.
    /// </summary>
    private static void TableCellInUse(Window window, TableViewModel table, ItemsControl rowsControl)
    {
        var columnsButton = rowsControl.FindAncestorOfType<Apex.Editor.Views.TableView>()!.FindControl<Button>("ColumnsButton")!;
        TableRowViewModel RowOf(Visual v) => v.GetVisualAncestors().OfType<StyledElement>().Select(e => e.DataContext).OfType<TableRowViewModel>().First();
        Dictionary<TableRowViewModel, Dictionary<string, string>> Snapshot() =>
            table.Rows.ToDictionary(r => r, r => r.Record.Properties.ToDictionary(kv => kv.Key, kv => kv.Value));
        List<string> Changed(Dictionary<TableRowViewModel, Dictionary<string, string>> before) =>
            before.SelectMany(b => b.Value.Keys.Union(b.Key.Record.Properties.Keys)
                .Where(k => b.Value.GetValueOrDefault(k) != b.Key.Record.Properties.GetValueOrDefault(k))
                .Select(k => $"{b.Key.Name}.{k} '{b.Value.GetValueOrDefault(k)}' → '{b.Key.Record.Properties.GetValueOrDefault(k)}'")).ToList();
        // A field showing text its own property doesn't have: typing pending somewhere.
        List<string> Pending() => rowsControl.GetVisualDescendants().OfType<TextBox>()
            .Where(b => b.Classes.Contains("pfield") && b.IsEffectivelyVisible && b.DataContext is PropertyItemViewModel p && (b.Text ?? "") != p.RawValue)
            .Select(b => $"{((PropertyItemViewModel)b.DataContext!).Key} shows '{b.Text}'").ToList();
        rowsControl.FindAncestorOfType<ScrollViewer>()!.Offset = default;

        // ── A scrub drag that leaves the cell: the editor stays until release, and the drag is one undo step ──
        columnsButton.Focus();
        PointerAway(window);
        var numberHost = CellHost(rowsControl, p => p is NumberPropertyViewModel && !p.IsRuleDisabled);
        if (numberHost is not null && HoverCell(window, numberHost)?.GetVisualDescendants().OfType<ScrubNumberBox>().FirstOrDefault() is { } scrub)
        {
            var number = (NumberPropertyViewModel)numberHost.DataContext!;
            var before = number.RawValue;
            var editor = numberHost.LiveEditor;
            var at = CentreOf(window, scrub);
            RawInput.Send(window, "MouseDown", at, MouseButton.Left, RawInputModifiers.None);
            RawInput.Send(window, "MouseMove", at + new Point(12, 0), RawInputModifiers.None);
            DrawFrame(window);
            // Down past the cell, onto the next row, still dragging.
            RawInput.Send(window, "MouseMove", at + new Point(24, 40), RawInputModifiers.None);
            DrawFrame(window);
            var heldOutside = ReferenceEquals(numberHost.LiveEditor, editor) && editor is not null;
            var midway = number.RawValue;
            RawInput.Send(window, "MouseMove", at + new Point(48, 40), RawInputModifiers.None);
            DrawFrame(window);
            RawInput.Send(window, "MouseUp", at + new Point(48, 40), MouseButton.Left, RawInputModifiers.None);
            DrawFrame(window);
            var after = number.RawValue;
            KeyStroke(window, K.Z, RawInputModifiers.Control);
            Settle(window);
            Check($"table cells: a scrub drag that leaves its cell keeps the editor until release (held outside: {heldOutside}), keeps scrubbing ('{before}' → '{midway}' → '{after}'), and ONE Ctrl+Z undoes it ('{number.RawValue}')",
                heldOutside && after != midway && midway != before && number.RawValue == before);
        }
        else
            Check("table cells: a number cell on screen to scrub", false);

        // ── Typing pending when the cell moves to another row: it lands on no other row ──
        void TypeThenMove(string how, Func<TableRowViewModel, Action?> move, bool keyboardStays)
        {
            columnsButton.Focus();
            PointerAway(window);
            var host = CellHost(rowsControl, p => p is TextPropertyViewModel { Value.Length: > 1 and < 12 } && !p.IsRuleDisabled);
            if (host is null || HoverCell(window, host)?.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(b => b.Classes.Contains("pfield")) is not { } box)
            {
                Check($"table cells ({how}): a text cell on screen to type in", false);
                return;
            }
            var prop = (PropertyItemViewModel)host.DataContext!;
            var row = RowOf(host);
            var original = prop.RawValue;
            var snapshot = Snapshot();
            var at = box.TranslatePoint(new Point(box.Bounds.Width - 12, box.Bounds.Height / 2), window)!.Value;
            RawInput.Send(window, "MouseDown", at, MouseButton.Left, RawInputModifiers.None);
            RawInput.Send(window, "MouseUp", at, MouseButton.Left, RawInputModifiers.None);
            Settle(window);
            // A second click this soon is a double click (a word selected): the caret to the end, as typing on would.
            KeyStroke(window, K.End);
            TypeChar(window, 'q');
            TypeChar(window, 'z');
            Settle(window);
            var typed = box.Text;
            var restore = move(row);
            Settle(window);
            DrawFrame(window);
            var ownChange = $"{row.Name}.{prop.Key} '{original}' → '{original}qz'";
            var changed = Changed(snapshot).Where(c => c != ownChange).ToList();
            var own = row.Record.Properties.GetValueOrDefault(prop.Key);
            // Still pending is fine only in its own property's field (the cell went with its row).
            var pending = Pending();
            var stillOwn = pending.Count == 1 && pending[0] == $"{prop.Key} shows '{original}qz'"
                           && rowsControl.GetVisualDescendants().OfType<TextBox>().Any(b => b.IsFocused && b.DataContext == prop);
            var outcome = own == original + "qz" ? "committed to its own row" : stillOwn ? "still pending in its own field" : own == original ? "discarded" : $"own row now '{own}'";
            var focused = window.FocusManager?.GetFocusedElement() as Visual;
            // Moving the keyboard out commits whatever a field still holds: nothing may land anywhere else then either.
            columnsButton.Focus();
            Settle(window);
            var changedAfter = Changed(snapshot).Where(c => c != ownChange).ToList();
            Check($"table cells ({how}): 'qz' typed in {row.Name}.{prop.Key} ('{typed}'), then the cells moved: the typing was {outcome}, no other value changed ({string.Join("; ", changed.Concat(changedAfter).Distinct().Take(3))}), no other field holds it ({string.Join("; ", pending.Take(2))}), keyboard in ({focused?.GetType().Name} for {(focused?.DataContext as PropertyItemViewModel)?.Key})",
                typed == original + "qz" && changed.Count == 0 && changedAfter.Count == 0 && (pending.Count == 0 || stillOwn)
                && (own == original + "qz" || stillOwn) && (focused is not null || !keyboardStays));
            restore?.Invoke();
            Settle(window);
            if (row.Record.Properties.GetValueOrDefault(prop.Key) != original)
            {
                KeyStroke(window, K.Z, RawInputModifiers.Control);
                Settle(window);
            }
        }
        var textColumn = CellHost(rowsControl, p => p is TextPropertyViewModel { Value.Length: > 1 and < 12 } && !p.IsRuleDisabled)?.DataContext is PropertyItemViewModel tp
            ? table.Columns.FirstOrDefault(c => c.Key == tp.Key)
            : null;
        if (textColumn is not null)
        {
            var def = textColumn.Def;
            // The rows change under the cell (as when another table opens): the keyboard's row goes, the keyboard with it.
            TypeThenMove("its row replaced", row =>
            {
                var all = table.Rows.ToList();
                table.Rows.ReplaceAll(all.Where(r => r != row).ToList());
                return () => table.Rows.ReplaceAll(all);
            }, keyboardStays: false);
            // The cell's row stays with it (the keyboard's row is kept wherever it sorts to).
            TypeThenMove("the table sorted", _ =>
            {
                table.Columns.First(c => c.Def == def).SortCommand.Execute(null);
                return null;
            }, keyboardStays: true);
            // The cell goes with its column; the keyboard has nowhere in the row to stay.
            TypeThenMove("its column hidden", _ =>
            {
                table.SetColumnShown(def, false);
                return () => table.SetColumnShown(def, true);
            }, keyboardStays: false);
            rowsControl.FindAncestorOfType<ScrollViewer>()!.Offset = default;
            DrawFrame(window);
        }

        // ── A suggestion list open when the cell moves to another row: it closes ──
        columnsButton.Focus();
        table.RefreshColumnChoices();
        var refDef = table.Columns.Any(c => c.Def.Kind == PropertyKind.AssetRef) ? null
            : table.ColumnChoices.Where(c => !c.IsShown).Select(c => c.Def).FirstOrDefault(d => d.Kind == PropertyKind.AssetRef);
        if (refDef is not null)
        {
            table.SetColumnShown(refDef, true);
            Settle(window);
            var sideways = rowsControl.FindAncestorOfType<ScrollViewer>()!;
            sideways.Offset = new Vector(sideways.Extent.Width, 0);
        }
        DrawFrame(window);
        rowsControl.GetVisualDescendants().OfType<TableCellValue>().FirstOrDefault(h => h.DataContext is RefPropertyViewModel)?.BringIntoView();
        PointerAway(window);
        var refHost = CellHost(rowsControl, p => p is RefPropertyViewModel && !p.IsRuleDisabled);
        if (refHost is not null && HoverCell(window, refHost)?.GetVisualDescendants().OfType<RefField>().FirstOrDefault() is { } field)
        {
            var refEditor = refHost.LiveEditor!;
            var refRow = RowOf(refHost);
            ClickAt(window, CentreOf(window, field));
            Settle(window);
            KeyStroke(window, K.F4);
            Settle(window);
            var opened = field.IsSuggesting;
            var all = table.Rows.ToList();
            table.Rows.ReplaceAll(all.Where(r => r != refRow).ToList());
            Settle(window);
            DrawFrame(window);
            var openPopups = Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(refEditor).OfType<Popup>().Count(p => p.IsOpen);
            var moved = refEditor.DataContext != refHost.DataContext || refHost.LiveEditor != refEditor;
            Check($"table cells: a reference cell's suggestion list (opened: {opened}) closes when the cell moves to another row (moved: {moved}; {openPopups} popups open, list open {field.IsSuggesting})",
                opened && moved && openPopups == 0 && !field.IsSuggesting);
            columnsButton.Focus();
            table.Rows.ReplaceAll(all);
            Settle(window);
        }
        else
            Check("table cells: a reference cell on screen", false);
        if (refDef is not null)
            table.SetColumnShown(refDef, false);

        // ── Tab onto a cell whose editor has nothing to focus: the keyboard stays on the cell ──
        rowsControl.FindAncestorOfType<ScrollViewer>()!.Offset = default;
        columnsButton.Focus();
        PointerAway(window);
        var plainHost = CellHost(rowsControl, p => p is TextPropertyViewModel && !p.IsRuleDisabled);
        if (plainHost is not null && HoverCell(window, plainHost) is { } plainEditor)
        {
            // The pooled editor this cell takes next, with every field disabled (as a read-only kind would be).
            plainEditor.IsEnabled = false;
            PointerAway(window);
            var focusedAfter = plainHost.Display?.Focus(NavigationMethod.Tab) == true ? window.FocusManager?.GetFocusedElement() as Visual : null;
            DrawFrame(window);
            focusedAfter = window.FocusManager?.GetFocusedElement() as Visual;
            var kept = focusedAfter is not null && (focusedAfter == plainHost || plainHost.IsVisualAncestorOf(focusedAfter));
            Check($"table cells: Tab onto a cell whose editor has nothing to focus keeps the keyboard on that cell ({focusedAfter?.GetType().Name})", kept);
            plainEditor.IsEnabled = true;
            columnsButton.Focus();
            PointerAway(window);
        }
        else
            Check("table cells: a text cell on screen to tab onto", false);

        // ── The change bar follows the theme ──
        var barHost = CellHost(rowsControl, p => p is TextPropertyViewModel && !p.IsRuleDisabled);
        if (barHost?.DataContext is PropertyItemViewModel barProp)
        {
            var original = barProp.RawValue;
            barProp.RawValue = original + "x";
            var colours = new List<string>();
            var ok = barProp.IsChanged;
            foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light, ThemeVariant.Dark })
            {
                Application.Current!.RequestedThemeVariant = theme;
                DrawFrame(window);
                var expected = barHost.TryFindResource("MarkBrush", barHost.ActualThemeVariant, out var b) && b is Avalonia.Media.ISolidColorBrush s ? s.Color : default;
                var at = barHost.TranslatePoint(new Point(1, barHost.Bounds.Height / 2), window)!.Value;
                using var frame = window.CaptureRenderedFrame();
                using var fb = frame!.Lock();
                var px = new byte[4];
                Marshal.Copy(fb.Address + (int)at.Y * fb.RowBytes + (int)at.X * 4, px, 0, 4);
                var (r, g, bl) = fb.Format == Avalonia.Platform.PixelFormat.Rgba8888 ? (px[0], px[1], px[2]) : (px[2], px[1], px[0]);
                var near = Math.Abs(r - expected.R) <= 2 && Math.Abs(g - expected.G) <= 2 && Math.Abs(bl - expected.B) <= 2;
                colours.Add($"{theme} #{r:x2}{g:x2}{bl:x2} (mark #{expected.R:x2}{expected.G:x2}{expected.B:x2})");
                ok &= near;
            }
            barProp.RawValue = original;
            Check($"table cells: a changed cell's bar is redrawn in the theme's mark colour when the theme switches ({string.Join(", ", colours)})", ok);
        }
        else
            Check("table cells: a text cell on screen to change", false);

        // ── Assistive tech: a drawn cell names its column and value, a switch its state, a problem its text ──
        var named = new List<string>();
        var a11y = true;
        foreach (var host in rowsControl.GetVisualDescendants().OfType<TableCellValue>().Where(h => h.IsEffectivelyVisible && h.Display is not null).Take(40))
        {
            var prop = (PropertyItemViewModel)host.DataContext!;
            var peer = Avalonia.Automation.Peers.ControlAutomationPeer.CreatePeerForElement(host.Display!);
            var name = peer.GetName();
            var good = name.Contains(prop.Label) && name.Contains(host.Display!.Text ?? "");
            if (prop is TogglePropertyViewModel toggle)
                good &= peer.GetProvider<Avalonia.Automation.Provider.IToggleProvider>()?.ToggleState
                        == (toggle.IsOn ? Avalonia.Automation.Provider.ToggleState.On : Avalonia.Automation.Provider.ToggleState.Off);
            if (prop.Problem is { } problem)
                good &= peer.GetHelpText().Contains(problem);
            if (named.Count < 3 || !good)
                named.Add($"{prop.Key}: '{name}'{(good ? "" : " (missing)")}");
            a11y &= good;
        }
        Check($"table cells: drawn cells tell assistive tech the column, the value, a switch's state and a problem ({string.Join("; ", named.Take(5))})",
            a11y && named.Count > 0);

        // A switch flipped through assistive tech flips once and says so; one a deffile rule disables doesn't flip.
        if (rowsControl.GetVisualDescendants().OfType<TableCellValue>()
                .FirstOrDefault(h => h.IsEffectivelyVisible && h.Display is not null && h.DataContext is TogglePropertyViewModel) is { } switchHost)
        {
            var toggle = (TogglePropertyViewModel)switchHost.DataContext!;
            var original = toggle.RawValue;
            var peer = Avalonia.Automation.Peers.ControlAutomationPeer.CreatePeerForElement(switchHost.Display!);
            var heard = new List<string>();
            void Heard(object? s, Avalonia.Automation.AutomationPropertyChangedEventArgs e) => heard.Add(e.Property == Avalonia.Automation.AutomationElementIdentifiers.NameProperty ? "name" : e.Property == Avalonia.Automation.TogglePatternIdentifiers.ToggleStateProperty ? "toggle state" : "other");
            peer.PropertyChanged += Heard;
            var was = toggle.IsOn;
            peer.GetProvider<Avalonia.Automation.Provider.IToggleProvider>()!.Toggle();
            Settle(window);
            Check($"table cells: a switch toggled by assistive tech flips once and tells it ({was} → {toggle.IsOn}; heard {string.Join(", ", heard)})",
                toggle.IsOn == !was && heard.Count > 0);
            toggle.RawValue = original;
            toggle.IsRuleDisabled = true;
            peer.GetProvider<Avalonia.Automation.Provider.IToggleProvider>()!.Toggle();
            Settle(window);
            Check($"table cells: a switch a rule disables can't be flipped by assistive tech ({toggle.RawValue})", toggle.RawValue == original);
            toggle.IsRuleDisabled = false;
            peer.PropertyChanged -= Heard;
        }
        else
            Check("table cells: a switch cell on screen for assistive tech", false);
    }
}
