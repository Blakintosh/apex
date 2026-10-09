using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using K = Avalonia.Input.Key;
using Apex.Editor.Models;
using Apex.Editor.ViewModels;
using Apex.Editor.Views;

namespace Apex.Shots;

/// <summary>
/// Reference fields that know what they reference, driven with real input on the actual field in the actual editor
/// row and table cell: suggestions while typing (ranked like Quick Open), ↓/↑ and Enter / Tab / click to accept,
/// Esc closing the list before it reverts, Alt+↓ / F4 listing every asset of the type, the list following the index
/// when assets are added, renamed and deleted, and the keyboard never leaving the field.
/// </summary>
public partial class Program
{
    private static void RunRefChecks(string outDir)
    {
        // Changes stay in memory: no session journal for this window.
        var vm = new MainViewModel(sessionRoot: null);
        var window = new MainWindow { DataContext = vm };
        window.Show();
        window.Activate();
        window.UpdateLayout();
        Pump();

        var db = DatabaseOf(vm);
        var xmodels = db.Assets.Where(a => a.Type == "xmodel").ToList();

        vm.OpenByName("wpn_ar_havoc");
        Pump();
        var tab = vm.ActiveTab!;
        var row = (RefPropertyViewModel)tab.AllSentinel.All.First(p => p.Key == "viewModel");
        var original = row.RawValue;
        tab.RevealProperty("viewModel");
        Pump(50);
        var field = window.FocusManager?.GetFocusedElement() as RefField;
        Check($"refs: revealing a reference row puts the keyboard in its field ({window.FocusManager?.GetFocusedElement()?.GetType().Name})",
            field is not null && field.DataContext == row);
        if (field is null)
            return;
        bool InField() => window.FocusManager?.GetFocusedElement() == field;
        RefSuggestPopup List() => field.Suggestions!;

        // ── Typing lists matches, ranked exactly like Quick Open ───────────────
        ReplaceText(window, field, "barrel");
        var expected = RankLikeQuickOpen(xmodels, "barrel");
        Check($"refs typing: the list opens with the xmodels matching 'barrel' ({field.Suggestions?.Items.Count} shown)",
            field.IsSuggesting && List().Items.Count == expected.Count && expected.Count > 0);
        Check($"refs typing: ranked like Quick Open — shorter names first among contains matches ({string.Join(", ", List().Items.Take(4).Select(a => a.Name))})",
            List().Items.SequenceEqual(expected));
        Check("refs typing: nothing is highlighted when nothing matches exactly (Enter keeps what was typed)", List().SelectedIndex == -1);
        CheckLines("refs typing", field, "barrel");
        Check("refs typing: the keyboard stays in the field", InField());
        Check("refs typing: typing alone writes nothing", row.RawValue == original && !tab.CanUndo);

        ReplaceText(window, field, "zm_castle_barrel_01");
        Check($"refs typing: an exact name ranks first and is highlighted ({List().Items.FirstOrDefault()?.Name})",
            List().Items.FirstOrDefault()?.Name == "zm_castle_barrel_01" && List().SelectedIndex == 0);

        ReplaceText(window, field, "rubble_0");
        var prefixQuery = RankLikeQuickOpen(xmodels, "zm_castle_r");
        ReplaceText(window, field, "zm_castle_r");
        Check($"refs typing: starts-with matches before contains matches ({string.Join(", ", List().Items.Take(3).Select(a => a.Name))})",
            List().Items.SequenceEqual(prefixQuery) && List().Items.All(a => a.Name.StartsWith("zm_castle_r", StringComparison.OrdinalIgnoreCase)));

        // ── ↓/↑ move, Enter accepts through RawValue (one undo step), focus stays ──
        ReplaceText(window, field, "barrel");
        Key(window, K.Down);
        Key(window, K.Down);
        Key(window, K.Down);
        Key(window, K.Up);
        var pick = List().Selected;
        Check($"refs keys: ↓↓↓↑ highlights the second suggestion ({pick?.Name}), and ↓ in the list doesn't walk the form",
            pick == expected[1] && InField());
        Key(window, K.Enter);
        Check($"refs Enter: accepts the highlighted suggestion ({row.RawValue})", row.RawValue == expected[1].Name && field.Text == expected[1].Name);
        Check("refs Enter: the list closes and the keyboard stays in the field", !field.IsSuggesting && InField());
        Check($"refs Enter: one undo step, and the reference resolves (problem: {row.Problem ?? "none"})",
            tab.CanUndo && !row.HasProblem && tab.Record.Properties["viewModel"] == expected[1].Name);
        Key(window, K.Z, RawInputModifiers.Control);
        Check($"refs Enter: Ctrl+Z puts the old reference back ({row.RawValue})", row.RawValue == original);

        // ── Esc closes the list first, then reverts the typing ─────────────────
        ReplaceText(window, field, "crate");
        Check("refs Esc: typing 'crate' opens the list", field.IsSuggesting);
        Key(window, K.Escape);
        Check($"refs Esc: the first Esc closes the list and keeps the typing ('{field.Text}')",
            !field.IsSuggesting && field.Text == "crate" && row.RawValue == original && InField());
        Key(window, K.Escape);
        Check($"refs Esc: the second Esc reverts the typing ('{field.Text}')", field.Text == original && row.RawValue == original);

        // ── Ctrl+Z with typing pending throws the typing away, and the list with it ──
        ReplaceText(window, field, "crate");
        Key(window, K.Z, RawInputModifiers.Control);
        Check($"refs Ctrl+Z: discards the typing and closes the list ('{field.Text}', open: {field.IsSuggesting})",
            !field.IsSuggesting && field.Text == original && row.RawValue == original && InField());

        // ── Pasting a name lists its matches too (the clipboard's text lands after the key that pasted it) ──
        Avalonia.Input.Platform.ClipboardExtensions.SetValueAsync(window.Clipboard!, Avalonia.Input.DataFormat.Text, "barrel").GetAwaiter().GetResult();
        field.SelectAll();
        Key(window, K.V, RawInputModifiers.Control);
        Pump(50);
        Check($"refs paste: Ctrl+V of 'barrel' opens the list of matches ('{field.Text}', {field.Suggestions?.Items.Count ?? 0} shown)",
            field.Text == "barrel" && field.IsSuggesting && field.Suggestions!.Items.Count > 0);
        Check("refs paste: pasting alone writes nothing", row.RawValue == original);
        Key(window, K.Escape);
        Key(window, K.Escape);
        Check($"refs paste: Esc Esc puts the value back ('{field.Text}' vs '{original}', open: {field.IsSuggesting})", !field.IsSuggesting && field.Text == original);

        // ── The form scrolling under an open list closes it (it would hang detached from its field) ──
        ReplaceText(window, field, "crate");
        var form = window.GetVisualDescendants().OfType<AssetEditorView>().First().FindControl<ItemsControl>("Form")!;
        var wheelAt = form.TranslatePoint(new Point(120, 30), window)!.Value;
        var scroller = form.GetVisualDescendants().OfType<ScrollViewer>().First();
        var offsetBefore = scroller.Offset.Y;
        RawInput.Send(window, "MouseMove", wheelAt, RawInputModifiers.None);
        RawInput.Send(window, "MouseWheel", wheelAt, new Vector(0, 1), RawInputModifiers.None);
        window.UpdateLayout();
        Pump();
        Check($"refs scroll: the list closes when the form scrolls (offset {offsetBefore:0} → {scroller.Offset.Y:0})", !field.IsSuggesting);
        tab.RevealProperty("viewModel");
        Pump(50);
        field = (RefField)window.FocusManager!.GetFocusedElement()!;
        Key(window, K.Escape);
        Check($"refs scroll: nothing was written ({row.RawValue})", row.RawValue == original && field.Text == original);

        // ── Tab never trades the typing for the top fuzzy match; it takes a highlighted line ──
        ReplaceText(window, field, "gear_large_0");
        var top = List().Items[0];
        Key(window, K.Tab);
        Check($"refs Tab: with nothing highlighted, Tab keeps what was typed ('{row.RawValue}', not {top.Name}) and moves on",
            top.Name != "gear_large_0" && row.RawValue == "gear_large_0" && !field.IsSuggesting && !InField());
        Key(window, K.Z, RawInputModifiers.Control);
        Check($"refs Tab: Ctrl+Z puts the reference back ({row.RawValue})", row.RawValue == original);
        field = RevealRefField(window, vm, "viewModel")!;
        ReplaceText(window, field, "gear_large_0");
        Key(window, K.Down);
        Key(window, K.Tab);
        Check($"refs Tab: takes the highlighted line ({row.RawValue}) with the keyboard still in the field",
            row.RawValue == top.Name && InField() && !field.IsSuggesting);

        // ── Alt+↓ / F4: every xmodel, by name, the value highlighted ────────────
        Key(window, K.Down, RawInputModifiers.Alt);
        var all = xmodels.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
        Check($"refs Alt+↓: lists every xmodel by name ({List().Items.Count} of {all.Count})",
            field.IsSuggesting && List().Items.Select(a => a.Name).SequenceEqual(all.Select(a => a.Name)));
        Check($"refs Alt+↓: the current value is highlighted and in view ({List().Selected?.Name})",
            List().Selected?.Name == row.RawValue && List().ShownLines.Any(l => l.Record?.Name == row.RawValue));
        CheckLines("refs Alt+↓", field, "");
        Capture(window, Path.Combine(outDir, "40-ref-all.png"));
        CapturePopup(field, Path.Combine(outDir, "40-ref-all-list.png"));
        Key(window, K.PageDown);
        Check($"refs Alt+↓: PageDown moves a page ({List().SelectedIndex - all.FindIndex(a => a.Name == row.RawValue)} lines)",
            List().SelectedIndex == all.FindIndex(a => a.Name == row.RawValue) + RefSuggestPopup.VisibleLines - 1);
        Key(window, K.F4);
        Check("refs F4: closes the list again (APE's combo toggle)", !field.IsSuggesting && InField());
        Key(window, K.F4);
        Check("refs F4: opens it", field.IsSuggesting && List().Items.Count == all.Count);
        Key(window, K.Escape);

        // ── ▾: the field's own arrow lists every asset as Alt+↓ does, with the chevron every dropdown draws ──
        Check("refs ▾: the field shows the dropdown chevron (the same glyph a choice's dropdown draws)",
            field.DropButton.IsEffectivelyVisible && field.DropButton.Content is PathIcon { Data: var chevron }
            && ReferenceEquals(chevron, Apex.Editor.Controls.FieldGlyphs.Chevron()));
        Click(window, field.DropButton);
        Check($"refs ▾: a real click lists every xmodel ({List().Items.Count} of {all.Count}), the keyboard in the field",
            field.IsSuggesting && List().Items.Count == all.Count && InField());
        Click(window, field.DropButton);
        Check("refs ▾: a second click closes the list", !field.IsSuggesting && InField());

        // ── A click on a line accepts it; the keyboard never leaves the field ──
        ReplaceText(window, field, "lamp");
        var lines = List().FindControl<ListBox>("Lines")!;
        var second = (Control)lines.ContainerFromIndex(1)!;
        var clicked = ((RefSuggestLine)second.DataContext!).Record!;
        ClickInPopup(second);
        Check($"refs click: a click on a line accepts it ({row.RawValue}, wanted {clicked.Name})", row.RawValue == clicked.Name);
        Check($"refs click: the keyboard stayed in the field ({window.FocusManager?.GetFocusedElement()?.GetType().Name})",
            InField() && !field.IsSuggesting);

        // ── Nothing matches: one plain line, not an empty popup ────────────────
        ReplaceText(window, field, "zzq");
        var message = List().FindControl<TextBlock>("Message")!;
        Check($"refs no match: says so in one line ('{message.Text}')",
            field.IsSuggesting && message.IsVisible && message.Text == "No xmodel named ‘zzq’" && List().Items.Count == 0);
        CapturePopup(field, Path.Combine(outDir, "41-ref-no-match.png"));
        Key(window, K.Escape);
        Key(window, K.Escape);
        Check("refs no match: Esc Esc leaves the value as it was", field.Text == row.RawValue);

        // ── The index: added, renamed and deleted assets show up (and go) at once ──
        var source = xmodels.First(a => a.Name == "mp_prop_barrel_01");
        vm.DuplicateAsset(source);
        Pump();
        var copy = vm.ActiveTab!.Record;
        Check($"refs index: duplicating an xmodel made '{copy.Name}'", copy.Name == "mp_prop_barrel_01_copy");
        vm.OpenAsset(tab.Record);
        Pump();
        field = RevealRefField(window, vm, "viewModel");
        ReplaceText(window, field!, "barrel_01_c");
        Check($"refs index: a new asset is suggested at once ({string.Join(", ", field!.Suggestions!.Items.Select(a => a.Name))})",
            field.Suggestions.Items.Select(a => a.Name).SequenceEqual(new[] { copy.Name }));
        Key(window, K.Escape);
        Key(window, K.Escape);

        vm.OpenAsset(copy);
        Pump();
        vm.OpenRenameCommand.Execute(null);
        vm.ActiveTab!.RenameText = "mp_prop_barrel_renamed";
        vm.CommitRenameCommand.Execute(null);
        WaitFor(() => !vm.IsRenameOpen);
        vm.OpenAsset(tab.Record);
        Pump();
        field = RevealRefField(window, vm, "viewModel")!;
        ReplaceText(window, field, "barrel_");
        Check($"refs index: a renamed asset is suggested by its new name only ({string.Join(", ", field.Suggestions!.Items.Where(a => a == copy).Select(a => a.Name))})",
            field.Suggestions.Items.Any(a => a.Name == "mp_prop_barrel_renamed") && field.Suggestions.Items.All(a => a.Name != "mp_prop_barrel_01_copy"));
        Key(window, K.Escape);
        Key(window, K.Escape);

        vm.OpenAsset(copy);
        Pump();
        vm.DeleteActiveCommand.Execute(null);
        WaitUntil(() => !vm.IsDeletePending, 5_000);
        Pump();
        vm.OpenAsset(tab.Record);
        Pump();
        field = RevealRefField(window, vm, "viewModel")!;
        ReplaceText(window, field, "barrel_renamed");
        Check($"refs index: a deleted asset is gone from the list ({field.Suggestions!.Items.Count} left, '{field.Suggestions.FindControl<TextBlock>("Message")!.Text}')",
            field.Suggestions.Items.Count == 0);
        Key(window, K.Escape);
        Key(window, K.Escape);

        // ── Light theme ────────────────────────────────────────────────────────
        Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
        ReplaceText(window, field, "barrel");
        Key(window, K.Down);
        CapturePopup(field, Path.Combine(outDir, "42-light-ref-typing.png"));
        Key(window, K.Escape);
        Key(window, K.Escape);
        Application.Current.RequestedThemeVariant = ThemeVariant.Dark;
        ReplaceText(window, field, "barrel");
        Key(window, K.Down);
        Capture(window, Path.Combine(outDir, "42-ref-typing.png"));
        CapturePopup(field, Path.Combine(outDir, "42-ref-typing-list.png"));
        Key(window, K.Escape);
        Key(window, K.Escape);

        // ── A table cell: the same field, narrow, writing the row's own asset ──
        var weapons = db.Assets.Where(a => a.Type == "weapon").Take(6).ToList();
        vm.OpenTableFor(weapons);
        Pump();
        var table = vm.Table!;
        table.SetColumnShown(SchemaRegistry.Get("weapon")!.Find("viewModel")!, true);
        Pump(50);
        var tableView = window.GetVisualDescendants().OfType<Apex.Editor.Views.TableView>().First();
        // At rest the cell draws its value; the pointer on it brings the reference field.
        // The new column is the last: scrolled to, as a user would.
        if (tableView.FindControl<ItemsControl>("Rows")?.FindAncestorOfType<ScrollViewer>() is { } sideways)
        {
            sideways.Offset = new Vector(sideways.Extent.Width, 0);
            Pump();
        }
        var refHost = CellHost(tableView, p => p is RefPropertyViewModel { Key: "viewModel" });
        var cell = refHost is null ? null : HoverCell(window, refHost)?.GetVisualDescendants().OfType<RefField>()
            .FirstOrDefault(f => f.IsEffectivelyVisible && f.DataContext is RefPropertyViewModel { Key: "viewModel" });
        Check($"refs table: a viewModel cell has the reference field ({cell?.Bounds.Width:0} px wide)", cell is not null);
        if (cell is not null)
        {
            var cellRow = cell.GetVisualAncestors().OfType<StyledElement>().Select(e => e.DataContext).OfType<TableRowViewModel>().First();
            cell.Focus();
            Pump();
            var before = cellRow.Record.Properties.GetValueOrDefault("viewModel");
            ReplaceText(window, cell, "_world");
            var cellTop = cell.Suggestions?.Items.FirstOrDefault();
            Key(window, K.Down);
            Capture(window, Path.Combine(outDir, "43-ref-table-cell.png"));
            CapturePopup(cell, Path.Combine(outDir, "43-ref-table-cell-list.png"));
            Check($"refs table: the list is wider than the cell ({cell.Suggestions?.Bounds.Width:0} px vs {cell.Bounds.Width:0})",
                cell.Suggestions is { } l && l.Bounds.Width >= 360);
            Key(window, K.Enter);
            Check($"refs table: ↓ Enter writes the row's own asset ({cellRow.Name}.viewModel = {cellRow.Record.Properties.GetValueOrDefault("viewModel")})",
                cellTop is not null && cellTop.Name != before && cellRow.Record.Properties.GetValueOrDefault("viewModel") == cellTop.Name
                && window.FocusManager?.GetFocusedElement() == cell);
        }
        table.CloseCommand.Execute(null);
        Pump();
        window.Close();
    }

    /// <summary>Selects the field's text and types <paramref name="text"/> one character at a time, as a user would.</summary>
    private static void ReplaceText(Window window, TextBox field, string text)
    {
        if (window.FocusManager?.GetFocusedElement() != field)
            field.Focus();
        Pump();
        field.SelectAll();
        foreach (var c in text)
        {
            window.KeyTextInput(c.ToString());
            Pump();
        }
    }

    private static RefField? RevealRefField(Window window, MainViewModel vm, string key)
    {
        vm.ActiveTab!.RevealProperty(key);
        Pump(50);
        return window.FocusManager?.GetFocusedElement() as RefField;
    }

    /// <summary>Quick Open's order, written out independently: exact, starts with, contains; shorter first; then by name.</summary>
    private static List<AssetRecord> RankLikeQuickOpen(IEnumerable<AssetRecord> assets, string query) =>
        assets.Where(a => a.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(a => a.Name.Equals(query, StringComparison.OrdinalIgnoreCase) ? 0 : a.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 1 : 2)
            .ThenBy(a => a.Name.Length)
            .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .Take(MainViewModel.RefSuggestLimit)
            .ToList();

    /// <summary>Every line on show draws its own result's name (with the typed part as the bold run) and GDT.</summary>
    private static void CheckLines(string label, RefField field, string query)
    {
        var list = field.Suggestions!;
        var lines = list.FindControl<ListBox>("Lines")!;
        var wrong = new List<string>();
        var shown = 0;
        for (var i = 0; i < RefSuggestPopup.VisibleLines; i++)
        {
            var index = list.Top + i;
            var expected = index < list.Items.Count ? list.Items[index] : null;
            var container = lines.ContainerFromIndex(i);
            if (expected is null)
            {
                if (container?.IsVisible == true)
                    wrong.Add($"line {i} is showing with no result");
                continue;
            }
            shown++;
            var name = container?.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Classes.Contains("mono"));
            var runs = name?.Inlines?.OfType<Avalonia.Controls.Documents.Run>().Select(r => r.Text ?? "").ToList();
            var drawn = runs is null ? null : string.Concat(runs);
            var hit = runs is { Count: 3 } ? runs[1] : null;
            var gdt = container?.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Classes.Contains("faint"))?.Text;
            if (drawn != expected.Name || (query.Length > 0 && !string.Equals(hit, query, StringComparison.OrdinalIgnoreCase))
                || gdt != MainViewModel.ShortGdt(expected.GdtName) || container?.IsVisible != true)
                wrong.Add($"line {i}: '{drawn}' (hit '{hit}', {gdt}) for {expected.Name}");
        }
        Check($"{label}: each of the {shown} lines shows its own name, the typed part marked, and its GDT{string.Concat(wrong.Take(3).Select(w => "; " + w))}",
            wrong.Count == 0 && shown > 0);
    }

    /// <summary>A real left click on a control inside a popup, sent to the popup's own window.</summary>
    private static void ClickInPopup(Control target)
    {
        Pump();
        var top = TopLevel.GetTopLevel(target)!;
        var point = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), top)!.Value;
        RawInput.Send(top, "MouseMove", point, RawInputModifiers.None);
        RawInput.Send(top, "MouseDown", point, MouseButton.Left, RawInputModifiers.None);
        RawInput.Send(top, "MouseUp", point, MouseButton.Left, RawInputModifiers.None);
        Pump();
    }

    private static void CapturePopup(RefField field, string path)
    {
        Pump();
        if (field.Suggestions is not { } list || TopLevel.GetTopLevel(list) is not { } top)
        {
            Console.WriteLine($"WARN: no list to capture for {path}");
            return;
        }
        top.UpdateLayout();
        var frame = top.CaptureRenderedFrame();
        if (frame is null)
        {
            Console.WriteLine($"WARN: no frame for {path}");
            return;
        }
        frame.Save(path, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        Console.WriteLine($"wrote {path}");
    }

    /// <summary>
    /// The reference field's budgets: a keystroke (match, rank, show) and Alt+↓ (list all), on the reference whose type
    /// has the most assets in the corpus (images on the real install), with real key input on the field in its row.
    /// </summary>
    private static void MeasureRefField(MainWindow window, MainViewModel vm, AssetDatabase db, int warm, int runs, List<PerfResult> results)
    {
        var counts = db.Assets.GroupBy(a => a.Type, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var candidates = counts.Keys
            .Select(t => (Type: t, Schema: SchemaRegistry.Get(t)))
            .Where(x => x.Schema is not null)
            .SelectMany(x => x.Schema!.Properties.Where(p => p.Kind == PropertyKind.AssetRef).Select(p => (x.Type, Def: p)))
            .Where(x => counts.GetValueOrDefault(x.Def.RefType) > 0)
            .OrderByDescending(x => counts.GetValueOrDefault(x.Def.RefType))
            .Take(20)
            .ToList();
        RefField? field = null;
        RefPropertyViewModel? row = null;
        foreach (var (type, def) in candidates)
        {
            if (db.Assets.FirstOrDefault(a => a.Type.Equals(type, StringComparison.OrdinalIgnoreCase) && a.Parent is null) is not { } host)
                continue;
            vm.OpenAsset(host);
            Settle(window);
            if (vm.ActiveTab!.AllSentinel.All.FirstOrDefault(p => p.Key == def.Key) is not RefPropertyViewModel { IsRuleHidden: false, IsRuleDisabled: false } r)
                continue;
            vm.ActiveTab.RevealProperty(def.Key);
            Settle(window);
            Settle(window);
            if (window.FocusManager?.GetFocusedElement() is RefField f && f.DataContext == r)
            {
                (field, row) = (f, r);
                break;
            }
        }
        Check($"perf: a reference field to measure ({row?.Key} → {row?.RefType}, {counts.GetValueOrDefault(row?.RefType ?? ""):N0} assets)", field is not null);
        if (field is null || row is null)
            return;

        // The catalog builds off the UI thread on the real install; the field asked for it when it took focus.
        for (var i = 0; i < 1000 && vm.MatchRefs(row.RefType, "") is null; i++)
        {
            System.Threading.Thread.Sleep(10);
            Settle(window);
        }
        var all = vm.MatchRefs(row.RefType, "")!;
        var original = row.RawValue;

        // The catalog build itself runs on a worker after any index change; how long that takes (not gated).
        const System.Reflection.BindingFlags Private = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
        var snap = typeof(MainViewModel).GetMethod("Snapshot", Private)!;
        var build = typeof(MainViewModel).GetMethod("BuildRefCatalog", Private)!;
        var snapMs = new List<double>();
        var buildMs = new List<double>();
        object snapshot = null!;
        for (var i = 0; i < 5; i++)
        {
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            snapshot = snap.Invoke(null, new object[] { db.Assets })!;
            snapMs.Add(System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            started = System.Diagnostics.Stopwatch.GetTimestamp();
            build.Invoke(null, new[] { snapshot });
            buildMs.Add(System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        snapMs.Sort();
        buildMs.Sort();
        Console.WriteLine($"info  reference catalog over {db.Assets.Count:N0} assets: snapshot median {snapMs[2]:0.0} ms (UI thread), "
            + $"build median {buildMs[2]:0.0} ms (off the UI thread on the real install; not gated)");
        var undoBefore = vm.ActiveTab!.CanUndo;
        // What a modder types: the start of a real name of the type.
        var typed = all[all.Count / 2].Name;
        typed = typed[..Math.Min(typed.Length, 10)];
        var shownWrong = 0;
        results.Add(Time(window, "Reference field: keystroke", PerfBudgets.Frame, warm, runs,
            i => TypeChar(window, typed[i % typed.Length]),
            before: i =>
            {
                if (i % typed.Length != 0)
                    return;
                field.Close();
                field.SetCurrentValue(TextBox.TextProperty, "");
            },
            after: _ =>
            {
                var list = field.Suggestions;
                if (list is null || !field.IsSuggesting || list.Items.Count == 0
                    || !list.Items[0].Name.Contains(field.Text ?? "", StringComparison.OrdinalIgnoreCase))
                    shownWrong++;
            },
            note: $"{row.RefType}: {all.Count:N0} assets, typing '{typed}'"));
        Check($"perf: every timed keystroke showed matches for what was typed ({shownWrong} didn't)", shownWrong == 0);
        CheckLines("perf: reference list after typing", field, field.Text ?? "");

        results.Add(Time(window, "Reference field: Alt+↓ lists all", PerfBudgets.Frame, warm, runs,
            _ => KeyStroke(window, K.Down, RawInputModifiers.Alt),
            before: _ =>
            {
                field.Close();
                field.SetCurrentValue(TextBox.TextProperty, original);
            },
            note: $"{all.Count:N0} {row.RefType} assets"));
        Check($"perf: Alt+↓ listed all {all.Count:N0} {row.RefType} assets", field.IsSuggesting && field.Suggestions!.Items.Count == all.Count);
        field.Close();
        field.SetCurrentValue(TextBox.TextProperty, original);
        Settle(window);
        Check($"perf: measuring the reference field wrote nothing ({row.Key} = '{row.RawValue}')",
            row.RawValue == original && vm.ActiveTab!.CanUndo == undoBefore);
    }
}
