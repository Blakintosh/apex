using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Ellipse = Avalonia.Controls.Shapes.Ellipse;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Apex.Editor.Controls;
using Apex.Editor.Models;
using Apex.Editor.Services;
using Apex.Editor.ViewModels;
using Apex.Editor.Views;
using K = Avalonia.Input.Key;

namespace Apex.Shots;

/// <summary>
/// The Explorer, with real input: GDT rows lead with the file name, the name measures before its suffix, marks keep their
/// column, groups roll up problems, counts match the rows, picked filters match exactly, the Filter menu toggles, the facet
/// row keeps its place and overflows into "+N", the tree's keys reach the search box, and Collapse all.
/// </summary>
public partial class Program
{
    private static void RunExplorerChecks(string outDir)
    {
        var vm = new MainViewModel();
        var window = ShowJournalWindow(vm);
        try
        {
            ExplorerRowChecks(window, vm, outDir);
            ExplorerCountAndFacetChecks(window, vm, outDir);
            ExplorerKeyChecks(window, vm);
            ExplorerFilterMenuChecks(window, vm, outDir);
        }
        finally
        {
            Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
            window.Close();
            vm.Dispose();
        }
        ExplorerLiveInstallChecks(outDir);
    }

    private static AssetBrowserView Browser(Window window) => window.GetVisualDescendants().OfType<AssetBrowserView>().First();

    private static ListBox Tree(Window window) => Browser(window).FindControl<ListBox>("Tree")!;

    private static TextBlock RowName(Control row) =>
        row.GetVisualDescendants().OfType<TextBlock>().First(t => t.Classes.Contains("rowname"));

    private static string Drawn(TextBlock t) =>
        t.Inlines is { Count: > 0 } inlines ? string.Concat(inlines.OfType<Avalonia.Controls.Documents.Run>().Select(r => r.Text)) : t.Text ?? "";

    /// <summary>How many characters of the text block's text are laid out (fewer when it trims).</summary>
    private static int LaidOut(TextBlock t) => t.TextLayout.TextLines.Sum(l => l.Length);

    private static Panel MarksSlot(Control row) => (Panel)row.GetVisualDescendants().OfType<Ellipse>().First().GetVisualParent()!;

    /// <summary>
    /// UI-thread time for one input to settle (handlers, queued work, layout), leaving out the headless platform's
    /// software render of the window (the app renders on its own thread), as the palette and search menu timings do.
    /// </summary>
    private static double TimeUi(Window window, Action input)
    {
        var uiThreadRender = (Avalonia.Threading.DispatcherPriority)typeof(Avalonia.Threading.DispatcherPriority)
            .GetField("UiThreadRender")!.GetValue(null)!;
        var sw = Stopwatch.StartNew();
        input();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs(Avalonia.Threading.DispatcherPriority.Render);
        var render = Stopwatch.StartNew();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs(uiThreadRender);
        render.Stop();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        return sw.Elapsed.TotalMilliseconds - render.Elapsed.TotalMilliseconds;
    }

    /// <summary>A real press and release at the middle of <paramref name="target"/>, in whatever top level holds it.</summary>
    private static void Press(Control target)
    {
        var top = TopLevel.GetTopLevel(target)!;
        var point = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), top)!.Value;
        RawInput.Send(top, "MouseMove", point, RawInputModifiers.None);
        RawInput.Send(top, "MouseDown", point, MouseButton.Left, RawInputModifiers.None);
        RawInput.Send(top, "MouseUp", point, MouseButton.Left, RawInputModifiers.None);
    }

    /// <summary>After scrolling inside a popup: lays it out and renders it, so hit testing sees the rows where they now are.</summary>
    private static void ScrolledInPopup(Control target)
    {
        var top = TopLevel.GetTopLevel(target)!;
        top.UpdateLayout();
        Pump();
        (top as Window)?.CaptureRenderedFrame();
        Pump();
    }

    private static double X(Visual v, Window window) => v.TranslatePoint(default, window)!.Value.X;

    // ── Rows: GDT names, indent, marks, name first, roll-up, pinned ───────────────────────────────────────────────

    private static void ExplorerRowChecks(Window window, MainViewModel vm, string outDir)
    {
        var tree = Tree(window);
        vm.FilterText = "";
        vm.ApplyFilterNow();
        Pump();

        // U7: the file name leads; the row's key (and the tooltip) is the full name.
        var zm = vm.FlatRows.First(n => n.Gdt?.Name == "zm_weapons.gdt");
        var zmRow = RowContainer(tree, vm, zm);
        Check($"explorer rows: a GDT row leads with its file name ('{Drawn(RowName(zmRow))}', key '{zm.Title}', tip '{zm.Tip}')",
            Drawn(RowName(zmRow)) == "zm_weapons" && zm.Title == "zm_weapons.gdt" && zm.Tip == "zm_weapons.gdt");

        // B3: one chevron per level, so a child's chevron sits under its parent's glyph (a GDT row, which has none,
        // its name).
        var castle = vm.FlatRows.First(n => n.Gdt?.Name == "zm_castle_assets.gdt");
        if (!castle.IsExpanded)
            vm.ToggleNode(castle);
        Pump();
        var typeNode = vm.FlatRows[vm.FlatRows.IndexOf(castle) + 1];
        if (!typeNode.IsExpanded)
            vm.ToggleNode(typeNode);
        Pump();
        var castleRow = RowContainer(tree, vm, castle);
        var typeRow = RowContainer(tree, vm, typeNode);
        Control parentGlyph = castleRow.GetVisualDescendants().OfType<GlyphIcon>().FirstOrDefault(g => g.IsEffectivelyVisible && g.Glyph == castle.Glyph) as Control
            ?? RowName(castleRow);
        var childChevron = typeRow.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("chev"));
        Check($"explorer rows: a child's chevron sits under its parent's glyph (chevron x {X(childChevron, window):0}, glyph x {X(parentGlyph, window):0}, step {BrowserNode.IndentStep})",
            typeNode.Level == 1 && Math.Abs(X(childChevron, window) - X(parentGlyph, window)) <= 1);

        // B2: the marks keep one column across row kinds, and lighting one never shortens the name.
        var assetNode = vm.FlatRows[vm.FlatRows.IndexOf(typeNode) + 1];
        var assetRow = RowContainer(tree, vm, assetNode);
        var nameBefore = RowName(assetRow).Bounds.Width;
        assetNode.HasChanges = true;
        assetNode.HasProblem = true;
        Pump();
        window.UpdateLayout();
        var nameAfter = RowName(assetRow).Bounds.Width;
        Check($"explorer rows: the marks have one column on GDT, type and asset rows (x {X(MarksSlot(castleRow), window):0} · {X(MarksSlot(typeRow), window):0} · {X(MarksSlot(assetRow), window):0})",
            new[] { X(MarksSlot(castleRow), window), X(MarksSlot(typeRow), window), X(MarksSlot(assetRow), window) }.Distinct().Count() == 1);
        Check($"explorer rows: a mark appearing leaves the name its width ({nameBefore:0} → {nameAfter:0} px)",
            Math.Abs(nameBefore - nameAfter) < 0.5 && assetNode.HasChanges);
        Capture(window, Path.Combine(outDir, "60-explorer-rows.png"));
        assetNode.HasChanges = assetNode.Asset!.HasSessionEdits;
        assetNode.HasProblem = false;

        // B4: a collapsed group shows the problems inside it (the mock's spike upgrade has some).
        var spike = vm.FlatRows.First(n => n.Gdt?.Name == "zm_weapons.gdt");
        if (spike.IsExpanded)
            vm.ToggleNode(spike);
        Pump();
        var spikeRow = RowContainer(tree, vm, spike);
        var warn = spikeRow.GetVisualDescendants().OfType<Ellipse>().First(e => e.HorizontalAlignment == Avalonia.Layout.HorizontalAlignment.Right);
        Check($"explorer rows: a collapsed GDT shows its problem dot for problems inside it ({spike.HasProblem}, drawn {warn.IsEffectivelyVisible})",
            spike.HasProblem && warn.IsEffectivelyVisible && !spike.IsExpanded);

        // B1: in search results the name lays out whole; the GDT suffix takes what is left.
        var search = Browser(window).FindControl<TextBox>("SearchBox")!;
        search.Focus();
        window.KeyTextInput("zm_upgraded");
        Pump(300);
        tree.UpdateLayout();
        var cut = tree.GetRealizedContainers().Where(c => c.IsEffectivelyVisible && c.DataContext is BrowserNode { IsAssetRow: true })
            .Select(c => (Node: (BrowserNode)c.DataContext!, Name: RowName(c)))
            .Where(r => LaidOut(r.Name) < r.Node.DisplayName.Length).Select(r => r.Node.Title).ToList();
        var longest = vm.FlatRows.Where(n => n.IsAssetRow).MaxBy(n => n.Title.Length)!;
        Check($"explorer search: every result's name is laid out whole before its GDT ({cut.Count} cut{string.Concat(cut.Take(2).Select(c => ", " + c))}; longest '{longest.Title}')",
            vm.IsSearchResults && cut.Count == 0 && vm.FlatRows.Count(n => n.IsAssetRow) > 3);
        Capture(window, Path.Combine(outDir, "61-explorer-search-names.png"));
        Key(window, K.Escape);

        // B13: a pinned row says which GDT it is from.
        var pinTarget = vm.FlatRows.First(n => n.Gdt?.Name == "zm_weapons.gdt");
        vm.ToggleNode(pinTarget);
        var toPin = vm.FlatRows.First(n => n.Asset?.Name == "wpn_ar_havoc_zm").Asset!;
        if (!vm.IsPinned(toPin))
            vm.TogglePinned(toPin);
        Pump();
        var pinned = vm.FlatRows.First(n => n.IsPinnedEntry && n.Asset == toPin);
        var pinnedRow = RowContainer(tree, vm, pinned);
        var suffix = pinnedRow.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Text == "zm_weapons" && t.IsEffectivelyVisible);
        Check($"explorer pinned: the row names its GDT ('{pinned.Suffix}', drawn {suffix is not null})", pinned.Suffix == "zm_weapons" && suffix is not null);
        vm.TogglePinned(toPin);
        Pump();
    }

    // ── Counts, exact filters, facets, no layout shift ───────────────────────────────────────────────────────────

    private static void ExplorerCountAndFacetChecks(Window window, MainViewModel vm, string outDir)
    {
        var browser = Browser(window);
        var tree = Tree(window);
        var search = browser.FindControl<TextBox>("SearchBox")!;
        var count = browser.FindControl<TextBlock>("ResultCount")!;
        int Shown() => vm.FlatRows.Count(n => n.IsAssetRow);

        // B5: a filtered tree says how many matched, out of how many.
        vm.FilterText = "type:weapon";
        vm.ApplyFilterNow();
        Pump();
        Check($"explorer count: a filtered tree says '{count.Text}' ({vm.MatchCount} matched)",
            count.IsEffectivelyVisible && count.Text == $"{vm.MatchCount:N0} of {vm.TotalCount:N0} assets");
        vm.FilterText = "";
        vm.ApplyFilterNow();
        Pump();
        Check("explorer count: no filter, no count", !count.IsEffectivelyVisible);

        // B6: a type that exists matches exactly; part of one still matches any type containing it.
        var weapon = new AssetRecord { Name = "a", Type = "weapon", GdtName = "source_data/zm/zm_weapons.gdt" };
        var camo = new AssetRecord { Name = "b", Type = "weaponcamo", GdtName = "source_data/zm/zm_weapons_camo.gdt" };
        var both = new[] { weapon, camo };
        var exact = AssetQuery.Parse("type:weapon").Select(t => t with { Exact = true }).ToList();
        Check("query: an exact type: matches that type only (not weaponcamo)",
            AssetQuery.MatchAll(both, exact).SequenceEqual(new[] { weapon }));
        Check("query: a quoted type: is exact", AssetQuery.MatchAll(both, AssetQuery.Parse("type:\"weapon\"")).SequenceEqual(new[] { weapon }));
        Check("query: part of a type still matches every type containing it", AssetQuery.MatchAll(both, AssetQuery.Parse("type:weap")).Count == 2);
        var exactGdt = AssetQuery.Parse("gdt:zm_weapons").Select(t => t with { Exact = true }).ToList();
        Check("query: an exact gdt: is that file (by its short name), not every file containing the name",
            AssetQuery.MatchAll(both, exactGdt).SequenceEqual(new[] { weapon }));
        Check("query: an exact gdt: by its whole path", AssetQuery.MatchAll(both,
            AssetQuery.Parse("gdt:source_data/zm/zm_weapons_camo.gdt").Select(t => t with { Exact = true }).ToList()).SequenceEqual(new[] { camo }));

        // B11 + B10: the facet row's place is kept from the first letter; one type draws no facets; a picked facet
        // resets with the query.
        search.Focus();
        Pump();
        var treeTop = tree.TranslatePoint(default, window)!.Value.Y;
        window.KeyTextInput("h");
        Pump();
        window.UpdateLayout();
        var afterLetter = tree.TranslatePoint(default, window)!.Value.Y;
        Pump(300);
        window.UpdateLayout();
        var afterResults = tree.TranslatePoint(default, window)!.Value.Y;
        window.KeyTextInput("avoc_zm_upgraded");
        Pump(300);
        window.UpdateLayout();
        var oneType = (Y: tree.TranslatePoint(default, window)!.Value.Y, Facets: vm.SearchFacets.Count);
        window.KeyTextInput("zzzz");
        Pump(300);
        window.UpdateLayout();
        var none = tree.TranslatePoint(default, window)!.Value.Y;
        Check($"explorer facets: the list doesn't move while typing (top {treeTop:0} → first letter {afterLetter:0} → results {afterResults:0} → one type {oneType.Y:0} → none {none:0})",
            afterLetter == afterResults && afterResults == oneType.Y && oneType.Y == none);
        Check($"explorer facets: one type draws no facets ({oneType.Facets})", oneType.Facets == 0);
        Key(window, K.Escape);

        search.Focus();
        window.KeyTextInput("havoc");
        Pump(300);
        var facetPanel = browser.FindControl<FacetRowPanel>("Facets")!;
        Button FacetButton(string type) => facetPanel.Children.OfType<Button>().First(b => (b.DataContext as SearchFacet)?.Type == type);
        var picked = vm.SearchFacets.Skip(1).First().Type!;
        Click(window, FacetButton(picked), MouseButton.Left);
        Pump();
        Check($"explorer facets: a click picks one; the count is what the list shows ('{count.Text}', {Shown()} rows)",
            vm.SearchFacets.First(f => f.Type == picked).IsActive && count.Text == $"{Shown():N0} of {vm.TotalCount:N0} assets"
            && vm.FlatRows.Where(n => n.IsAssetRow).All(n => n.Asset!.Type == picked));
        Check("explorer facets: the picked facet is drawn as picked", FacetButton(picked).Classes.Contains("active"));
        search.Focus();
        Key(window, K.Escape);
        search.Focus();
        window.KeyTextInput("havoc");
        Pump(300);
        Check($"explorer facets: clearing the search resets the facet (All picked: {vm.SearchFacets.FirstOrDefault()?.IsActive})",
            vm.SearchFacets.FirstOrDefault() is { Type: null, IsActive: true });
        Key(window, K.Escape);

        // B10: more types than fit go behind "+N", and a type picked from there is drawn in the row.
        search.Focus();
        window.KeyTextInput("_");
        Pump(300);
        window.UpdateLayout();
        var more = browser.FindControl<Button>("FacetMore")!;
        Pump();
        window.UpdateLayout();
        var drawn = facetPanel.Children.OfType<Button>().Count(b => b != more && b.IsVisible && !facetPanel.Hidden.Contains(b));
        Click(window, more);
        Pump();
        var items = (FlyoutBase.GetAttachedFlyout(more) as MenuFlyout)?.Items.OfType<MenuItem>().ToList() ?? new List<MenuItem>();
        Check($"explorer facets: what the row can't show waits behind '{more.Content}' ({vm.SearchFacets.Count} facets, {drawn} drawn, {items.Count} in the menu)",
            more.Bounds.X < facetPanel.Bounds.Width && items.Count > 0 && (string?)more.Content == $"+{items.Count}"
            && drawn + items.Count == vm.SearchFacets.Count);
        var menuItem = items.LastOrDefault();
        var wanted = vm.SearchFacets.FirstOrDefault(f => f.Label == menuItem?.Header as string)?.Type;
        Capture(window, Path.Combine(outDir, "62-explorer-facets-more.png"));
        if (menuItem is not null)
            ClickInPopup(menuItem);
        Pump();
        window.UpdateLayout();
        Pump();
        window.UpdateLayout();
        var active = vm.SearchFacets.FirstOrDefault(f => f.IsActive);
        Check($"explorer facets: a type picked from +N is drawn in the row ({active?.Label}, wanted {wanted})",
            menuItem is not null && active?.Type == wanted && wanted is not null && facetPanel.Hidden.All(h => h.DataContext != active)
            && facetPanel.Children.OfType<Button>().Any(b => b.DataContext == active && b.IsVisible && b.Bounds.X < facetPanel.Bounds.Width));
        search.Focus();
        Key(window, K.Escape);
    }

    // ── Keys: up to the search box, type to search, Esc, Collapse all ─────────────────────────────────────────────

    private static void ExplorerKeyChecks(Window window, MainViewModel vm)
    {
        var browser = Browser(window);
        var tree = Tree(window);
        var search = browser.FindControl<TextBox>("SearchBox")!;
        Control? Focused() => window.FocusManager?.GetFocusedElement() as Control;
        vm.FilterText = "";
        vm.ApplyFilterNow();
        Pump();

        browser.FocusTree();
        Pump();
        Key(window, K.Up);
        Check($"explorer keys: ↑ on the first row goes to the search box ({Focused()?.Name})", Focused() == search);

        browser.FocusTree();
        Pump();
        window.KeyTextInput("riot");
        Pump(300);
        Check($"explorer keys: typing on a row searches with it ('{search.Text}', '{vm.FilterText}', focus {Focused()?.Name})",
            search.Text == "riot" && vm.FilterText == "riot" && Focused() == search && vm.IsSearchResults);
        Key(window, K.Escape);
        Check("explorer keys: Esc in the box clears it", vm.FilterText == "" && search.Text == "");

        browser.FocusTree();
        Pump();
        Key(window, K.Down);
        Key(window, K.Escape);
        Check($"explorer keys: Esc on a row goes back to the search box ({Focused()?.Name})", Focused() == search);

        // Collapse all: two groups open, the keyboard on a row inside one; Ctrl+← closes them all and keeps the row's group.
        var groups = vm.FlatRows.Where(n => n.Gdt is not null).Take(2).ToList();
        foreach (var g in groups)
            if (!g.IsExpanded)
                vm.ToggleNode(g);
        Pump();
        var inside = vm.FlatRows[vm.FlatRows.IndexOf(groups[1]) + 1];
        tree.SelectedItems?.Clear();
        tree.SelectedItem = inside;
        RowContainer(tree, vm, inside).Focus();
        Pump(200);
        var collapseMs = TimeUi(window, () =>
        {
            window.KeyPress(K.Left, RawInputModifiers.Control, PhysicalKey.None, null);
            window.KeyRelease(K.Left, RawInputModifiers.Control, PhysicalKey.None, null);
        });
        var roots = vm.FlatRows.Count(n => n.IsGroup && n.Level == 0);
        Check($"explorer keys: Ctrl+← collapses every group ({vm.FlatRows.Count} rows, {roots} groups, {collapseMs:0.0} ms on the UI thread) and keeps the keyboard on the row's group ({(tree.SelectedItem as BrowserNode)?.Title})",
            vm.FlatRows.All(n => !n.IsExpanded) && vm.FlatRows.Count(n => !n.IsHeader) == roots && tree.SelectedItem == groups[1]
            && Focused()?.DataContext == groups[1]);
        // Ctrl+← against ← on one group (what collapsing looked like before), warm, median of 7.
        double KeyOn(BrowserNode row, RawInputModifiers mods)
        {
            tree.SelectedItems?.Clear();
            tree.SelectedItem = row;
            RowContainer(tree, vm, row).Focus();
            Pump(150);
            return TimeUi(window, () =>
            {
                window.KeyPress(K.Left, mods, PhysicalKey.None, null);
                window.KeyRelease(K.Left, mods, PhysicalKey.None, null);
            });
        }
        var all = new List<double>();
        var one = new List<double>();
        for (var i = 0; i < 7; i++)
        {
            foreach (var g in groups)
                if (!g.IsExpanded)
                    vm.ToggleNode(g);
            all.Add(KeyOn(vm.FlatRows[vm.FlatRows.IndexOf(groups[1]) + 1], RawInputModifiers.Control));
            vm.ToggleNode(groups[1]);
            one.Add(KeyOn(groups[1], RawInputModifiers.None));
        }
        Console.WriteLine($"info  explorer keys (mock): Ctrl+← collapse all median {Median(all):0.0} ms, ← on one open group median {Median(one):0.0} ms (UI thread)");
        var collapse = Apex.Editor.Commands.CommandCatalog.Get(Apex.Editor.Commands.CommandCatalog.CollapseAll);
        Check($"explorer keys: Collapse all is in the command catalog with its key ('{collapse.Name}', {collapse.GestureText}, {collapse.Scope})",
            collapse is { Name: "Collapse all", Scope: Apex.Editor.Commands.CommandScope.Explorer, Gesture.Key: K.Left, Gesture.KeyModifiers: KeyModifiers.Control });
    }

    // ── The Filter menu ───────────────────────────────────────────────────────────────────────────────────────────

    private static void ExplorerFilterMenuChecks(Window window, MainViewModel vm, string outDir)
    {
        var browser = Browser(window);
        vm.FilterText = "";
        vm.ApplyFilterNow();
        Pump();
        var filter = browser.GetVisualDescendants().OfType<Button>().First(b => AutomationPropertiesName(b) == "Filter");
        var firstOpen = TimeUi(window, () => Press(filter));
        var reopen = new List<double>();
        for (var i = 0; i < 5; i++)
        {
            filter.Flyout!.Hide();
            Pump();
            System.Threading.Thread.Sleep(450); // apart, so two presses never pair into a double-click
            reopen.Add(TimeUi(window, () => Press(filter)));
        }
        var list = browser.FindControl<ItemsControl>("FilterMenuList")!;
        var menuRoot = (Control)list.GetVisualParent()!;
        List<ToggleButton> OptionRows() => list.GetVisualDescendants().OfType<ToggleButton>()
            .Where(t => t.Classes.Contains("optionrow") && t.IsEffectivelyVisible).ToList();
        var rows = OptionRows().Where(r => r.DataContext is FilterOption { Tip: null }).ToList();
        var scrollers = menuRoot.GetVisualDescendants().OfType<ScrollViewer>().Count(v => v.FindAncestorOfType<TextBox>() is null);
        var realized = list.GetVisualDescendants().OfType<ToggleButton>().Count(t => t.Classes.Contains("optionrow"));
        Check($"filter menu: opens with one scroller for the whole menu ({scrollers}), building only the lines in view ({realized} of {vm.FilterMenuRows.Count})",
            list.IsEffectivelyVisible && scrollers == 1);
        var scroller = list.GetVisualDescendants().OfType<ScrollViewer>().First();
        var pills = menuRoot.GetVisualDescendants().OfType<ToggleButton>().Where(t => t.Classes.Contains("pill")).ToList();
        Check($"filter menu: Only is the search menu's pills ({string.Join(", ", pills.Select(p => $"{p.Content} {p.Bounds.Height:0}px"))})",
            pills.Select(p => p.Content as string).SequenceEqual(new[] { "Changed", "Off-default", "Problems" }) && pills.All(p => p.Bounds.Height <= 26));
        var glyphs = rows.Select(r => r.GetVisualDescendants().OfType<GlyphIcon>().First(g => g.Glyph == ((FilterOption)r.DataContext!).Glyph)).ToList();
        Check($"filter menu: every type row shows its type's glyph in its colour ({string.Join(" ", glyphs.Take(6).Select(g => g.Glyph))})",
            glyphs.Count > 0 && glyphs.All(g => g.IsEffectivelyVisible && g.Bounds.Width > 0 && g.Foreground is Avalonia.Media.ISolidColorBrush { Color.A: 255 }));
        var counts = vm.TypeOptions.Select(o => o.Count).ToList();
        Check("filter menu: types go by count, then name (as the search menu's pills and the facets do)",
            counts.SequenceEqual(counts.OrderByDescending(c => c)) && vm.TypeOptions.Select(o => o.Label).SequenceEqual(
                vm.TypeOptions.OrderByDescending(o => o.Count).ThenBy(o => o.Label, StringComparer.Ordinal).Select(o => o.Label)));
        Console.WriteLine($"info  filter menu (mock, {vm.TypeOptions.Count} types, {vm.GdtOptions.Count} GDTs): first open {firstOpen:0.0} ms, " +
                          $"then median {Median(reopen):0.0} ms (worst {reopen.Max():0.0}) on the UI thread");

        // B7: a row is a toggle: checked while its chip is in the box, the count beside it is what it shows.
        var xmodel = rows.First(r => r.DataContext is FilterOption { Label: "xmodel" });
        var option = (FilterOption)xmodel.DataContext!;
        System.Threading.Thread.Sleep(450); // past the double-click interval after the last open
        ScrolledInPopup(xmodel); // the reopened menu drawn, so hit testing finds its rows
        ClickInPopup(xmodel);
        Check($"filter menu: a type row puts its chip in the box and shows checked ('{vm.FilterText}', checked {xmodel.IsChecked})",
            vm.ExplorerQuery.Chips.Any(c => c.Raw == "type:xmodel") && xmodel.IsChecked == true);
        Check($"filter menu: ...and the count beside it is what the Explorer shows ({option.Count} = {vm.MatchCount})", option.Count == vm.MatchCount);
        Capture(window, Path.Combine(outDir, "63-filter-menu.png"));
        Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
        Capture(window, Path.Combine(outDir, "63b-light-filter-menu.png"));
        Application.Current.RequestedThemeVariant = ThemeVariant.Dark;
        ClickInPopup(xmodel);
        Check($"filter menu: a second click takes it out ('{vm.FilterText}', checked {xmodel.IsChecked})",
            !vm.ExplorerQuery.HasChips && xmodel.IsChecked == false);
        vm.FilterText = "t:xmodel";
        vm.ApplyFilterNow();
        Pump();
        Check("filter menu: a chip typed in another spelling checks the row too", xmodel.IsChecked == true);
        vm.AddFilterToken("type:xmodel");
        Check($"filter menu: the same filter is never added twice ('{vm.FilterText}')", vm.FilterText == "t:xmodel");
        vm.FilterText = "";
        vm.ApplyFilterNow();
        Pump();

        var zmIndex = vm.FilterMenuRows.IndexOf(vm.GdtOptions.First(o => o.Tip == "zm_weapons.gdt"));
        list.ContainerFromIndex(zmIndex)?.BringIntoView();
        if (list.ContainerFromIndex(zmIndex) is null)
            scroller.Offset = new Vector(0, scroller.Extent.Height);
        ScrolledInPopup(list);
        var zm = OptionRows().First(r => r.DataContext is FilterOption { Tip: "zm_weapons.gdt" });
        ClickInPopup(zm);
        Check($"filter menu: a GDT row adds its short name ('{vm.FilterText}', {vm.MatchCount} = {((FilterOption)zm.DataContext!).Count})",
            vm.FilterText == "gdt:zm_weapons" && vm.MatchCount == ((FilterOption)zm.DataContext!).Count && zm.IsChecked == true);
        ClickInPopup(zm);
        var find = browser.FindControl<TextBox>("FilterMenuBox")!;
        ClickInPopup(find);
        window.KeyTextInput("castle");
        Pump();
        Check($"filter menu: its box finds types and GDTs ({string.Join(", ", vm.FilterMenuRows.OfType<FilterOption>().Select(o => o.Label))})",
            vm.GdtOptions.Count == 1 && vm.GdtOptions[0].Label == "zm_castle_assets" && vm.FilterMenuRows.OfType<FilterOption>().Count() == 1);
        find.Text = "xmo";
        Pump();
        Check($"filter menu: ...types by name too ({string.Join(", ", vm.FilterMenuRows.OfType<FilterOption>().Select(o => o.Label))})",
            vm.FilterMenuRows.OfType<FilterOption>().Any(o => o.Label == "xmodel"));
        Check($"filter menu: the finder keeps the keyboard while the list under it changes ({(window.FocusManager?.GetFocusedElement() as Control)?.GetType().Name})",
            find.IsFocused);
        find.Text = "";
        Pump();
        var changed = menuRoot.GetVisualDescendants().OfType<ToggleButton>().First(t => t.Content as string == "Changed");
        ClickInPopup(changed);
        Check($"filter menu: the Changed pill filters by is:changed ('{vm.FilterText}')", vm.FilterText == "is:changed" && changed.IsChecked == true);
        ClickInPopup(changed);
        filter.Flyout!.Hide();
        Pump();
    }

    // ── A temp install: GDTs in folders, two with the same file name, a weapon type and a camo type ─────────────

    private static void ExplorerLiveInstallChecks(string outDir)
    {
        var install = NewScratch("explorer-install");
        Directory.CreateDirectory(Path.Combine(install, "deffiles"));
        string Gdt(string type, params string[] names) =>
            "{\r\n" + string.Concat(names.Select(n => $"\t\"{n}\" ( \"{type}.gdf\" )\r\n\t{{\r\n\t\t\"displayName\" \"{n}\"\r\n\t}}\r\n")) + "}\r\n";
        void Write(string rel, string text)
        {
            var path = Path.Combine(install, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }
        Write(@"source_data\zm\weapons\zm_test_weapons.gdt", Gdt("weapon", "wpn_test_a", "wpn_test_b", "wpn_test_c"));
        Write(@"source_data\mp\weapons\zm_test_weapons.gdt", Gdt("weapon", "wpn_mp_a"));
        Write(@"source_data\zm\weapons\camo\zm_test_weapons_camo.gdt", Gdt("weaponcamo", "camo_a", "camo_b"));
        Write(@"source_data\a_rather_long_folder_name\with\several\more\levels\inside\it\test_models.gdt", Gdt("xmodel", "test_model_a"));

        var saved = (Mock: Environment.GetEnvironmentVariable("APEX_FORCE_MOCK"), Root: Environment.GetEnvironmentVariable("APEX_BO3_ROOT"),
            Persist: Environment.GetEnvironmentVariable("APEX_NO_PERSIST"));
        Environment.SetEnvironmentVariable("APEX_FORCE_MOCK", null);
        Environment.SetEnvironmentVariable("APEX_BO3_ROOT", install);
        Environment.SetEnvironmentVariable("APEX_NO_PERSIST", "1");
        MainViewModel? vm = null;
        MainWindow? window = null;
        try
        {
            var made = Stopwatch.StartNew();
            vm = new MainViewModel(Path.Combine(install, "session"));
            var loadingShownAt = -1.0;
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MainViewModel.ShowExplorerLoading) && vm.ShowExplorerLoading && loadingShownAt < 0)
                    loadingShownAt = made.Elapsed.TotalMilliseconds;
            };
            window = ShowJournalWindow(vm);
            WaitUntil(() => vm.Status.StartsWith("Loaded"), 60_000);
            Pump(200);
            Check($"explorer loading: the loading line never flashes before 150 ms and is gone once rows are in (shown at {(loadingShownAt < 0 ? "never" : loadingShownAt.ToString("0") + " ms")})",
                (loadingShownAt < 0 || loadingShownAt >= 150) && !vm.ShowExplorerLoading && !vm.BrowserIsEmpty && vm.FlatRows.Count > 0);

            var tree = Tree(window);
            var deep = vm.FlatRows.First(n => n.Gdt?.Name.EndsWith("test_models.gdt") == true);
            var deepRow = RowContainer(tree, vm, deep);
            var folder = deepRow.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == deep.Folder);
            Check($"explorer rows: a GDT in folders leads with its file name, its folder after it, dim, trimmed from the front ('{Drawn(RowName(deepRow))}' · '{deep.Folder}', {LaidOut(folder)} of {deep.Folder.Length} characters)",
                Drawn(RowName(deepRow)) == "test_models" && LaidOut(RowName(deepRow)) >= "test_models".Length
                && deep.Folder.StartsWith("source_data/") && folder.TextTrimming == Avalonia.Media.TextTrimming.LeadingCharacterEllipsis
                && ToolTip.GetTip(deepRow.GetVisualDescendants().OfType<NameFirstPanel>().First()) as string == deep.Title);

            // Expansion is kept by the whole path: two GDTs share a file name, one is open, and stays the only one open.
            var twins = vm.FlatRows.Where(n => n.DisplayName == "zm_test_weapons").ToList();
            vm.ToggleNode(twins[0]);
            vm.ApplyFilterNow();
            Pump();
            var after = vm.FlatRows.Where(n => n.DisplayName == "zm_test_weapons").ToList();
            Check($"explorer rows: two GDTs with one file name keep their own open state ({string.Join(", ", after.Select(a => $"{a.Folder} {(a.IsExpanded ? "open" : "closed")}"))})",
                after.Count == 2 && after.Count(a => a.IsExpanded) == 1 && after.Single(a => a.IsExpanded).Title == twins[0].Title);
            Capture(window, Path.Combine(outDir, "64-explorer-gdt-folders.png"));

            // B6: type:weapon is the weapons, not the camos; the GDT twins' chips use their paths.
            vm.FilterText = "type:weapon";
            vm.ApplyFilterNow();
            Check($"explorer filter: type:weapon is the weapons only ({vm.MatchCount}, the camos are weaponcamo)", vm.MatchCount == 4);
            vm.FilterText = "type:weap";
            vm.ApplyFilterNow();
            Check($"explorer filter: part of a type still matches both ({vm.MatchCount})", vm.MatchCount == 6);
            vm.FilterText = "gdt:zm_test_weapons_camo";
            vm.ApplyFilterNow();
            Check($"explorer filter: a GDT's file name is that file ({vm.MatchCount})", vm.MatchCount == 2);
            var twinOptions = vm.GdtOptions.Where(o => o.Label == "zm_test_weapons").ToList();
            Check($"filter menu: GDTs sharing a file name are told apart by their path ({string.Join(", ", twinOptions.Select(o => o.Token))})",
                twinOptions.Count == 2 && twinOptions.All(o => o.Token.Contains("/weapons/zm_test_weapons.gdt")) && twinOptions.All(o => o.HasFolder));
            vm.FilterText = twinOptions[0].Token;
            vm.ApplyFilterNow();
            Check($"filter menu: a twin's chip shows that file only ({vm.MatchCount} = {twinOptions[0].Count})", vm.MatchCount == twinOptions[0].Count);
            vm.FilterText = "";
            vm.ApplyFilterNow();
        }
        catch (Exception ex)
        {
            Check($"explorer install: {ex}", false);
        }
        finally
        {
            window?.Close();
            vm?.Dispose();
            Environment.SetEnvironmentVariable("APEX_FORCE_MOCK", saved.Mock);
            Environment.SetEnvironmentVariable("APEX_BO3_ROOT", saved.Root);
            Environment.SetEnvironmentVariable("APEX_NO_PERSIST", saved.Persist);
            SchemaRegistry.ResetToMock();
        }

        // B16: an install with no GDTs says so once it has looked.
        var empty = NewScratch("explorer-empty");
        Directory.CreateDirectory(Path.Combine(empty, "deffiles"));
        Directory.CreateDirectory(Path.Combine(empty, "source_data"));
        Environment.SetEnvironmentVariable("APEX_FORCE_MOCK", null);
        Environment.SetEnvironmentVariable("APEX_BO3_ROOT", empty);
        Environment.SetEnvironmentVariable("APEX_NO_PERSIST", "1");
        vm = null;
        window = null;
        try
        {
            vm = new MainViewModel(Path.Combine(empty, "session"));
            window = ShowJournalWindow(vm);
            WaitUntil(() => vm.Status.StartsWith("Loaded"), 60_000);
            Pump(200);
            var text = Browser(window).FindControl<TextBlock>("EmptyText")!;
            Check($"explorer empty: an install with no GDTs says so and what to do ('{text.Text}', shown {text.IsEffectivelyVisible})",
                vm.BrowserIsEmpty && text.IsEffectivelyVisible && text.Text!.StartsWith("No GDTs to list") && text.Text.Contains("Ctrl+Shift+N")
                && !vm.ShowExplorerLoading);
            Capture(window, Path.Combine(outDir, "65-explorer-no-gdts.png"));
        }
        catch (Exception ex)
        {
            Check($"explorer empty install: {ex}", false);
        }
        finally
        {
            window?.Close();
            vm?.Dispose();
            Environment.SetEnvironmentVariable("APEX_FORCE_MOCK", saved.Mock);
            Environment.SetEnvironmentVariable("APEX_BO3_ROOT", saved.Root);
            Environment.SetEnvironmentVariable("APEX_NO_PERSIST", saved.Persist);
            SchemaRegistry.ResetToMock();
        }
    }
}
