using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Avalonia;
using Apex.Editor.Controls;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Apex.Editor.Services;
using Apex.Editor.ViewModels;
using Apex.Editor.Views;
using K = Avalonia.Input.Key;

namespace Apex.Shots;

/// <summary>
/// The Explorer's search box and the search menu over it, with real input: a click in the box places the caret and
/// typing filters the Explorer, typed syntax turns into chips, the menu button (or Ctrl+Shift+F) opens the menu on a
/// draft, the fields add chips, results update as you type, Enter opens the chosen asset and leaves the Explorer alone,
/// Ctrl+Enter gives the Explorer the draft, and a click elsewhere keeps the draft for next time.
/// </summary>
public partial class Program
{
    private static void RunSearchMenuChecks(string outDir)
    {
        var vm = new MainViewModel();
        var window = new MainWindow { DataContext = vm };
        window.Show();
        window.Activate();
        window.UpdateLayout();
        Pump();

        var browser = window.GetVisualDescendants().OfType<AssetBrowserView>().First();
        var search = browser.FindControl<TextBox>("SearchBox")!;
        var menu = new MenuShown(browser);
        var menuBox = browser.FindControl<TextBox>("MenuBox")!;
        var menuButton = browser.FindControl<Button>("SearchMenuButton")!;
        // The button is 24 px: a real click at its middle (the nudged click helper can land beside it), apart from the
        // last click so the two never pair into a double-click.
        void OpenMenu()
        {
            System.Threading.Thread.Sleep(450);
            Click(window, menuButton);
            Pump(50);
        }
        Control? Focused() => window.FocusManager?.GetFocusedElement() as Control;
        void WaitResults() => WaitFor(() => vm.SearchMenuIsCurrent);

        // ── The box: typing filters the Explorer as it always has; finished syntax becomes a chip ──
        search.Focus();
        Pump();
        window.KeyTextInput("havoc");
        Pump(300);
        Check($"search box: typing filters the Explorer without opening the menu ('{vm.FilterText}', {vm.MatchCount} matches)",
            vm.FilterText == "havoc" && vm.MatchCount > 0 && !vm.IsSearchMenuOpen && !menu.IsOpen);
        window.KeyTextInput(" type:weapon ");
        Pump(300);
        Check($"search box: typed syntax turns into a chip ({string.Join(",", vm.ExplorerQuery.Chips.Select(c => c.Raw))} + '{search.Text}')",
            vm.ExplorerQuery.Chips.Any(c => c.Raw == "type:weapon") && search.Text?.Trim() == "havoc"
            && vm.FilterText.Contains("type:weapon") && vm.FilterText.Contains("havoc"));
        search.CaretIndex = 0;
        Key(window, K.Back);
        Check($"search box: Backspace at the start takes the last chip ('{vm.FilterText}')",
            !vm.ExplorerQuery.HasChips && vm.FilterText.Trim() == "havoc");
        vm.FilterText = "type:weapon";
        vm.ApplyFilterNow();
        Pump();
        Check($"search box: a query set elsewhere shows as chips ('{search.Text}', {vm.ExplorerQuery.Chips.Count} chip)",
            vm.ExplorerQuery.Chips.Count == 1 && search.Text == "" && vm.SearchPlaceholder == "");
        var chipClose = browser.FindControl<Border>("SearchField")!.GetVisualDescendants().OfType<Button>()
            .FirstOrDefault(b => b.Classes.Contains("chipclose") && b.IsEffectivelyVisible);
        if (chipClose is not null)
            Click(window, chipClose);
        Check($"search box: a chip's ✕ removes it at once, without opening the menu ('{vm.FilterText}')",
            chipClose is not null && vm.FilterText == "" && !vm.IsSearchMenuOpen && vm.FlatRows.Any(n => n.Gdt is not null));
        Console.WriteLine($"info  chip ✕ hit area {chipClose?.Bounds.Width:0}×{chipClose?.Bounds.Height:0}");

        // ── One search model: a click in the box is a click in a text box ──
        search.Focus();
        Pump();
        window.KeyTextInput("riot");
        Pump(300);
        Click(window, search, MouseButton.Left);
        Pump(50);
        Check($"search box: a click puts the caret in the box and opens nothing (caret {search.CaretIndex}, focus {Focused()?.Name})",
            !vm.IsSearchMenuOpen && !menu.IsOpen && Focused() == search && search.CaretIndex is > 0 and <= 4);
        window.KeyTextInput("x");
        Pump(300);
        Check($"search box: ...and typing there filters the Explorer live ('{vm.FilterText}')", vm.FilterText.Contains('x') && !vm.IsSearchMenuOpen);
        var glyph = browser.FindControl<Border>("SearchField")!.GetVisualDescendants().OfType<GlyphIcon>().First(g => g.Glyph == "search");
        Key(window, K.Escape);
        browser.FocusTree();
        Pump();
        Click(window, glyph, MouseButton.Left);
        Check($"search box: a click on its glyph puts the keyboard in it too ({Focused()?.Name})", Focused() == search && !vm.IsSearchMenuOpen);
        Key(window, K.Tab, RawInputModifiers.Shift);
        Key(window, K.Tab);
        Check($"search box: tabbing in opens nothing ({Focused()?.Name}, menu {vm.IsSearchMenuOpen})", !vm.IsSearchMenuOpen && !menu.IsOpen);

        // ── The button at the box's end opens the menu, keyboard in it, on a draft of the Explorer's query ──
        OpenMenu();
        Pump(50);
        Check($"menu: the box's menu button opens it with the keyboard in its box ({Focused()?.Name}, button {menuButton.Bounds.Width:0}×{menuButton.Bounds.Height:0})",
            vm.IsSearchMenuOpen && menu.IsOpen && Focused() == menuBox && menuButton.Bounds.Width >= 24 && menuButton.Bounds.Height >= 24);
        var card = browser.FindControl<Border>("SearchMenuCard")!;
        var field = browser.FindControl<Border>("SearchField")!;
        var cardAt = card.TranslatePoint(default, window);
        var fieldAt = field.TranslatePoint(default, window);
        Check($"menu: anchored over the Explorer's box (card {cardAt}, box {fieldAt}, {card.Bounds.Width:0} wide)",
            cardAt is { } c && fieldAt is { } f && Math.Abs(c.X - (f.X - 6)) < 2 && Math.Abs(c.Y - (f.Y - 9)) < 2 && card.Bounds.Width >= 480);
        // The query stays put as the menu opens over it: the same font, at the same place, to the pixel.
        var boxAt = search.TranslatePoint(default, window);
        var menuBoxAt = menuBox.TranslatePoint(default, window);
        var glyphs = (Box: glyph.TranslatePoint(default, window),
            Menu: card.GetVisualDescendants().OfType<GlyphIcon>().First(g => g.Glyph == "search").TranslatePoint(default, window));
        Check($"menu: its box sits exactly over the Explorer's, same font (text {boxAt} vs {menuBoxAt}, glyph {glyphs.Box} vs {glyphs.Menu}, " +
              $"{search.FontFamily.Name} {search.FontSize} vs {menuBox.FontFamily.Name} {menuBox.FontSize})",
            boxAt is { } b && menuBoxAt is { } m && Math.Abs(b.X - m.X) <= 1 && Math.Abs(b.Y + search.Bounds.Height / 2 - (m.Y + menuBox.Bounds.Height / 2)) <= 1
            && glyphs.Box is { } gb && glyphs.Menu is { } gm && Math.Abs(gb.X - gm.X) <= 1 && Math.Abs(gb.Y - gm.Y) <= 1
            && search.FontFamily == menuBox.FontFamily && search.FontSize == menuBox.FontSize);

        window.KeyTextInput("havoc");
        Pump();
        WaitResults();
        Check($"menu: results as you type ({vm.SearchMenuCountText}: {string.Join(", ", vm.SearchMenuResults.Take(3).Select(r => r.Asset.Name))})",
            vm.SearchMenuResults.Count > 0 && vm.SearchMenuResults.All(r => r.Asset.Name.Contains("havoc", StringComparison.OrdinalIgnoreCase))
            && vm.SearchMenuResults.All(r => r.Hit.Equals("havoc", StringComparison.OrdinalIgnoreCase)));
        Check($"menu: the Explorer is left alone while the draft changes ('{vm.FilterText}')", vm.FilterText == "");

        // A type pill adds a chip to the box above; the pill shows it's on.
        var weaponPill = card.GetVisualDescendants().OfType<ToggleButton>()
            .FirstOrDefault(t => t.DataContext is SearchTypeOption { Type: "weapon" });
        Console.WriteLine($"info  type pills: {string.Join(" ", vm.SearchMenuTypes.Select(t => t.Type))}");
        if (weaponPill is not null)
            Click(window, weaponPill);
        WaitResults();
        Check($"menu: the weapon pill adds a type chip and narrows the results ({vm.SearchMenuQuery}, {vm.SearchMenuCountText})",
            weaponPill is { IsChecked: true } && vm.MenuQuery.Chips.Any(ch => ch.Raw == "type:weapon")
            && vm.SearchMenuResults.Count > 0 && vm.SearchMenuResults.All(r => r.Asset.Type == "weapon"));
        Check($"menu: the footer shows the query as syntax ('{vm.SearchMenuQuery}')", vm.SearchMenuQuery == "type:weapon havoc");
        menuBox.Focus();
        Pump();

        Key(window, K.Down);
        Check($"menu: ↓ chooses the next result ({vm.SearchMenuSelectedIndex})", vm.SearchMenuSelectedIndex == Math.Min(1, vm.SearchMenuResults.Count - 1));
        Capture(window, Path.Combine(outDir, "38-search-menu.png"));
        Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
        Capture(window, Path.Combine(outDir, "38b-light-search-menu.png"));
        Application.Current.RequestedThemeVariant = ThemeVariant.Dark;

        var chosen = vm.SearchMenuResults[vm.SearchMenuSelectedIndex].Asset;
        Key(window, K.Enter);
        Pump(50);
        Check($"menu: Enter opens the chosen asset as a kept tab ({vm.ActiveTab?.Name}) and closes",
            vm.ActiveTab?.Record == chosen && vm.ActiveTab is { IsPreview: false } && !vm.IsSearchMenuOpen && !menu.IsOpen);
        Check($"menu: ...leaving the Explorer alone ('{vm.FilterText}') with the keyboard back in its box ({Focused()?.Name})",
            vm.FilterText == "" && Focused() == search);

        // ── Ctrl+Shift+F opens it too; Ctrl+Enter gives the Explorer the draft ──
        Key(window, K.F, RawInputModifiers.Control | RawInputModifiers.Shift);
        Pump(50);
        Check($"menu: Ctrl+Shift+F opens it ({Focused()?.Name})", vm.IsSearchMenuOpen && menu.IsOpen && Focused() == menuBox);
        window.KeyTextInput("type:weapon havoc");
        Pump();
        Check($"menu: typed syntax turns into a chip in the menu too ({string.Join(",", vm.MenuQuery.Chips.Select(ch => ch.Raw))} + '{menuBox.Text}')",
            vm.MenuQuery.Chips.Any(ch => ch.Raw == "type:weapon") && menuBox.Text == "havoc"
            && vm.SearchMenuTypes.First(t => t.Type == "weapon").IsOn);
        Key(window, K.Enter, RawInputModifiers.Control);
        Pump(50);
        Check($"menu: Ctrl+Enter filters the Explorer with the draft ('{vm.FilterText}', {vm.MatchCount} matches)",
            !vm.IsSearchMenuOpen && vm.FilterText == "type:weapon havoc" && vm.MatchCount > 0
            && vm.ExplorerQuery.Chips.Count == 1 && search.Text == "havoc" && Focused() == search);
        Capture(window, Path.Combine(outDir, "39-search-chips-in-explorer.png"));

        // ── Esc closes and the draft goes with it ──
        OpenMenu();
        Pump(50);
        Check($"menu: reopens on the Explorer's query ('{vm.SearchMenuQuery}')", vm.IsSearchMenuOpen && vm.SearchMenuQuery == "type:weapon havoc");
        window.KeyTextInput("zzz");
        Key(window, K.Escape);
        Check($"menu: Esc closes it and drops the draft ('{vm.FilterText}', keyboard in {Focused()?.Name})",
            !vm.IsSearchMenuOpen && !menu.IsOpen && vm.FilterText == "type:weapon havoc" && Focused() == search);
        Key(window, K.Escape);
        Check($"search box: a second Esc clears the Explorer's search ('{vm.FilterText}')", vm.FilterText == "" && !vm.ExplorerQuery.HasChips);

        // ── The fields: GDT (space separates several), Property, Only ──
        OpenMenu();
        Pump(50);
        var gdtBox = browser.FindControl<TextBox>("MenuGdtBox")!;
        Click(window, gdtBox, MouseButton.Left);
        window.KeyTextInput("zm_weapons");
        Key(window, K.Space);
        WaitResults();
        Check($"menu GDT: a space makes the GDT a chip ({string.Join(",", vm.MenuGdtChips.Select(ch => ch.Raw))}, {vm.SearchMenuCountText})",
            vm.MenuGdtChips.Count == 1 && vm.MenuQuery.Contains("gdt:zm_weapons") && gdtBox.Text == ""
            && vm.SearchMenuResults.Count > 0 && vm.SearchMenuResults.All(r => r.Asset.GdtName.Contains("zm_weapons")));
        Key(window, K.Back);
        Check("menu GDT: Backspace in the empty field takes its last chip", vm.MenuGdtChips.Count == 0);

        var keyBox = browser.FindControl<TextBox>("MenuPropKeyBox")!;
        var valueBox = browser.FindControl<TextBox>("MenuPropValueBox")!;
        Click(window, keyBox, MouseButton.Left);
        window.KeyTextInput("clipSize");
        Click(window, valueBox, MouseButton.Left);
        window.KeyTextInput("30");
        Key(window, K.Enter);
        WaitResults();
        Check($"menu property: Enter adds the property chip ({vm.SearchMenuQuery}, {vm.SearchMenuCountText})",
            vm.MenuQuery.Chips.Any(ch => ch.Kind == TokenKind.Prop && ch.Token.Key == "clipSize" && ch.Token.Value == "30")
            && keyBox.Text == "" && valueBox.Text == "" && vm.IsSearchMenuOpen);
        vm.MenuQuery.RemoveLast();
        valueBox.Focus();
        window.KeyTextInput("1");
        Key(window, K.Enter);
        WaitResults();
        Check($"menu property: no key matches any property ({vm.SearchMenuQuery}, {vm.SearchMenuCountText})",
            vm.SearchMenuQuery == "prop:=1" && vm.SearchMenuResults.Count > 0);
        vm.MenuQuery.RemoveLast();

        var changedPill = card.GetVisualDescendants().OfType<ToggleButton>().First(t => t.Content as string == "Changed");
        Click(window, changedPill, MouseButton.Left);
        WaitResults();
        Check($"menu only: Changed adds is:changed ({vm.SearchMenuQuery}, {vm.SearchMenuCountText})",
            vm.MenuQuery.Contains("is:changed") && changedPill.IsChecked == true);
        Check($"menu: nothing matches says so ('{vm.SearchMenuCountText}')",
            vm.SearchMenuIsEmpty == (vm.SearchMenuResults.Count == 0));
        Click(window, changedPill, MouseButton.Left);
        Check("menu only: a second click takes it off", !vm.MenuQuery.Contains("is:changed") && changedPill.IsChecked == false);

        // ── One click on a result opens it ──
        menuBox.Focus();
        window.KeyTextInput("locus");
        WaitResults();
        var row = browser.FindControl<ListBox>("MenuResults")!;
        row.UpdateLayout();
        var first = row.ContainerFromIndex(0);
        if (first is not null)
            Click(window, first, MouseButton.Left);
        Check($"menu: one click on a result opens it ({vm.ActiveTab?.Name})",
            first is not null && vm.ActiveTab?.Name.Contains("locus") == true && !vm.IsSearchMenuOpen && vm.FilterText == "");

        // ── A click elsewhere closes it and lands where it was aimed ──
        OpenMenu();
        Pump(50);
        window.KeyTextInput("needle");
        var tree = browser.FindControl<ListBox>("Tree")!;
        // Just below the menu (it covers the top of the Explorer, as designed): whatever row is there takes the click.
        var cardBottom = card.TranslatePoint(new Point(0, card.Bounds.Height), window)!.Value.Y;
        var treeLeft = tree.TranslatePoint(default, window)!.Value.X;
        var below = new Point(treeLeft + 60, cardBottom + 14);
        tree.SelectedItems?.Clear();
        Pump();
        window.MouseMove(below);
        window.MouseDown(below, MouseButton.Left);
        window.MouseUp(below, MouseButton.Left);
        Pump();
        System.Threading.Thread.Sleep(600);
        Check($"menu: a click below it closes it, leaves the Explorer alone and still lands on the row there ('{vm.FilterText}', selected {(tree.SelectedItem as BrowserNode)?.Title})",
            !vm.IsSearchMenuOpen && !menu.IsOpen && vm.FilterText == "" && tree.SelectedItem is BrowserNode);
        OpenMenu();
        Pump(50);
        Check($"menu: ...and keeps the draft for the next time it opens ('{vm.SearchMenuQuery}', box '{menuBox.Text}')",
            vm.IsSearchMenuOpen && vm.SearchMenuQuery == "needle" && menuBox.Text == "needle");
        Key(window, K.Escape);
        OpenMenu();
        Pump(50);
        Check($"menu: Esc is the way to drop a draft ('{vm.SearchMenuQuery}')", vm.IsSearchMenuOpen && vm.SearchMenuQuery == "");
        Key(window, K.Escape);

        // ── Hiding the Explorer takes its menu with it ──
        Key(window, K.F, RawInputModifiers.Control | RawInputModifiers.Shift);
        Pump(50);
        Key(window, K.B, RawInputModifiers.Control);
        Pump(50);
        Check($"menu: Ctrl+B hides the Explorer and closes its menu (explorer {vm.ShowExplorer}, menu {vm.IsSearchMenuOpen})",
            !vm.ShowExplorer && !vm.IsSearchMenuOpen && !menu.IsOpen);
        Key(window, K.B, RawInputModifiers.Control);
        Pump(50);

        // ── Keystroke cost in the menu (UI thread): the search itself runs off it ──
        var (open, keys, settle) = TimeSearchMenu(window, vm, "wpn_ar_havoc");
        Console.WriteLine($"info  search menu (mock, {vm.TotalCount:N0} assets, one run): Ctrl+Shift+F open {open:0.0} ms; per letter median {Median(keys):0.0} ms, " +
                          $"worst {keys.Max():0.0} ms; results in {Median(settle):0.0} ms");

        window.Close();
    }

    /// <summary>Whether the search menu is on screen (its popup stays open with the card hidden between uses).</summary>
    private sealed class MenuShown(AssetBrowserView browser)
    {
        public bool IsOpen => browser.IsSearchMenuShown;
    }

    private static double Median(System.Collections.Generic.IEnumerable<double> xs)
    {
        var sorted = xs.OrderBy(x => x).ToList();
        return sorted[sorted.Count / 2];
    }

    /// <summary>
    /// Ctrl+Shift+F, then each letter of <paramref name="typed"/>, timed on the UI thread to idle with layout done (the
    /// headless platform's software render of the window is left out: the app renders on its own thread), and each
    /// letter's wall time until its results are in. Esc closes the menu after.
    /// </summary>
    private static (double Open, System.Collections.Generic.List<double> Keys, System.Collections.Generic.List<double> Settle) TimeSearchMenu(
        Window window, MainViewModel vm, string typed)
    {
        Interactive Focused() => window.FocusManager?.GetFocusedElement() as Interactive ?? window;
        var uiThreadRender = (DispatcherPriority)typeof(DispatcherPriority).GetField("UiThreadRender")!.GetValue(null)!;
        double Time(Action input)
        {
            var sw = Stopwatch.StartNew();
            input();
            Dispatcher.UIThread.RunJobs(DispatcherPriority.Render);
            var render = Stopwatch.StartNew();
            Dispatcher.UIThread.RunJobs(uiThreadRender);
            render.Stop();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            return sw.Elapsed.TotalMilliseconds - render.Elapsed.TotalMilliseconds;
        }
        void Press(K key, KeyModifiers mods)
        {
            var target = Focused();
            target.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = mods, Source = target });
            target.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = key, KeyModifiers = mods, Source = target });
        }
        var open = Time(() => Press(K.F, KeyModifiers.Control | KeyModifiers.Shift));
        var keys = new System.Collections.Generic.List<double>();
        var settle = new System.Collections.Generic.List<double>();
        foreach (var c in typed)
        {
            var sw = Stopwatch.StartNew();
            keys.Add(Time(() => Focused().RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = c.ToString(), Source = Focused() })));
            while (!vm.SearchMenuIsCurrent && sw.ElapsedMilliseconds < 5000)
            {
                System.Threading.Thread.Sleep(1);
                Dispatcher.UIThread.RunJobs();
            }
            settle.Add(sw.Elapsed.TotalMilliseconds);
        }
        Press(K.Escape, KeyModifiers.None);
        Pump();
        return (open, keys, settle);
    }

    /// <summary>The search menu on the real install (read-only; skipped without one): open, each letter, results.</summary>
    private static void LiveSearchMenuTiming()
    {
        try { TimeLiveSearchMenu(); }
        finally { Apex.Editor.Models.SchemaRegistry.ResetToMock(); }
    }

    private static void TimeLiveSearchMenu()
    {
        var saved = Environment.GetEnvironmentVariable("APEX_FORCE_MOCK");
        Environment.SetEnvironmentVariable("APEX_FORCE_MOCK", null);
        MainViewModel vm;
        // A session of its own in this run's temp folder, never the user's journal: restoring that put its edits in the timed
        // corpus and its 'Restored…' in place of 'Loaded…' (so the wait below ran its full 3 minutes), and other runs, or
        // Apex itself, may hold it at the same time.
        try { vm = new MainViewModel(NewScratch("live-session")); }
        finally { Environment.SetEnvironmentVariable("APEX_FORCE_MOCK", saved); }
        if (vm.IsMockData)
        {
            Console.WriteLine("search menu timing: BO3 not found, skipped");
            return;
        }
        var loadUntil = DateTime.UtcNow.AddMinutes(3);
        while (!vm.Status.StartsWith("Loaded", StringComparison.Ordinal) && DateTime.UtcNow < loadUntil)
            Pump(100);
        var window = new MainWindow { DataContext = vm };
        window.Show();
        window.Activate();
        window.UpdateLayout();
        Pump();

        const string typed = "ar_standard";
        const int runs = 7;
        TimeSearchMenu(window, vm, typed); // warm-up: JIT, first templates
        var all = Enumerable.Range(0, runs).Select(_ => TimeSearchMenu(window, vm, typed)).ToList();
        var perLetter = Enumerable.Range(0, typed.Length).Select(i => Median(all.Select(r => r.Keys[i]))).ToList();
        var settle = Enumerable.Range(0, typed.Length).Select(i => Median(all.Select(r => r.Settle[i]))).ToList();
        Console.WriteLine($"search menu timing ({vm.TotalCount:N0} assets, median of {runs}): Ctrl+Shift+F open {Median(all.Select(r => r.Open)):0.0} ms; " +
                          $"typing '{typed}' per letter {string.Join(" ", perLetter.Select(t => t.ToString("0.0")))} ms; " +
                          $"results in {string.Join(" ", settle.Select(t => t.ToString("0")))} ms (off the UI thread)");
        var worst = perLetter.Append(Median(all.Select(r => r.Open))).Max();
        Gate($"search menu timing: Ctrl+Shift+F and every letter typed are within a frame on the UI thread (slowest median {worst:0.0} ms of 16)",
            worst <= PerfBudgets.Frame);
        window.Close();
        vm.Dispose();
    }
}