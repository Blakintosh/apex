using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Apex.Editor.Controls;
using Apex.Editor.ViewModels;
using Apex.Editor.Views;
using K = Avalonia.Input.Key;

namespace Apex.Shots;

/// <summary>
/// The shared visual system, driven with real input in both themes: hover is instant (no fade through a lighter
/// colour), the wheel over a closed dropdown scrolls the form instead of changing the value, dropdown lists are one
/// card with fixed rows in the UI face, labels take their control's state colour, primary buttons and check boxes are
/// the one Sky, mono text stays regular after a semibold mono line, and row actions keep their space.
/// </summary>
public partial class Program
{
    private static void RunStyleChecks(string outDir)
    {
        foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
        {
            Application.Current!.RequestedThemeVariant = theme;
            var name = theme == ThemeVariant.Dark ? "dark" : "light";
            var vm = new MainViewModel();
            var window = new MainWindow { DataContext = vm, Width = 1600, Height = 1000 };
            window.Show();
            window.Activate();
            vm.OpenByName("wpn_snp_locus");
            vm.OpenByName("wpn_ar_havoc_zm_upgraded");
            DrawFrame(window);

            HoverIsInstant(window, name);
            StateColours(window, vm, name);
            DropdownLook(window, vm, name, outDir);
            if (theme == ThemeVariant.Dark)
                DropdownWheel(window, vm);
            MenuLook(window, name, outDir);
            NumberFieldEdge(window, name);
            MonoStaysRegular(window);
            HeaderHeights(window, name);
            FocusedTab(window, vm, name);
            BulkBarHeights(window, vm, name);
            window.Close();

            PrimaryAndChecked(name);
            RowActionsKeepTheirSpace(name);
        }
        Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Color Token(StyledElement at, string key) =>
        at.TryFindResource(key, at.ActualThemeVariant, out var v) switch
        {
            true when v is ISolidColorBrush b => b.Color,
            true when v is Color c => c,
            _ => throw new InvalidOperationException($"no colour token {key}"),
        };

    /// <summary>A brush laid over an opaque backdrop, as it is drawn.</summary>
    private static Color Over(IBrush? brush, Color backdrop)
    {
        if (brush is not ISolidColorBrush s)
            return backdrop;
        var a = s.Color.A / 255.0 * s.Opacity;
        byte Mix(byte f, byte b) => (byte)Math.Round(f * a + b * (1 - a));
        return Color.FromRgb(Mix(s.Color.R, backdrop.R), Mix(s.Color.G, backdrop.G), Mix(s.Color.B, backdrop.B));
    }

    private static double Luminance(Color c)
    {
        static double Lin(byte v)
        {
            var x = v / 255.0;
            return x <= 0.03928 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
    }

    private static double Contrast(Color a, Color b)
    {
        var (l1, l2) = (Luminance(a), Luminance(b));
        return (Math.Max(l1, l2) + 0.05) / (Math.Min(l1, l2) + 0.05);
    }

    private static string Hex(Color c) => $"#{c.R:x2}{c.G:x2}{c.B:x2}";

    private static bool Near(Color a, Color b, int tolerance = 2) =>
        Math.Abs(a.R - b.R) <= tolerance && Math.Abs(a.G - b.G) <= tolerance && Math.Abs(a.B - b.B) <= tolerance;

    private static Color? BrushColor(IBrush? brush) => (brush as ISolidColorBrush)?.Color;

    private static ContentPresenter Presenter(Control button) =>
        button.GetVisualDescendants().OfType<ContentPresenter>().First(p => p.Name == "PART_ContentPresenter");

    /// <summary>The TextBlock that draws a control's label: its longest visible text (a tab's name, not its type glyph).</summary>
    private static TextBlock Label(Control control) =>
        control.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && !string.IsNullOrEmpty(t.Text))
            .OrderByDescending(t => t.Text!.Length).First();

    private static Color PixelOf(Window window, Point at)
    {
        using var frame = window.CaptureRenderedFrame()!;
        using var fb = frame.Lock();
        var px = new byte[4];
        Marshal.Copy(fb.Address + (int)at.Y * fb.RowBytes + (int)at.X * 4, px, 0, 4);
        return fb.Format == Avalonia.Platform.PixelFormat.Rgba8888 ? Color.FromRgb(px[0], px[1], px[2]) : Color.FromRgb(px[2], px[1], px[0]);
    }

    private static void MoveTo(Window window, Point at)
    {
        RawInput.Send(window, "MouseMove", at, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
    }

    // ── U1: hover is instant ──────────────────────────────────────────────────

    /// <summary>
    /// Every flat button class, hovered and left with real pointer moves while frames tick by in real time: the fill
    /// drawn over the pane is only ever the rest colour or the hover colour, never a lighter (or darker) one between
    /// them. A brush fade from Transparent (#00FFFFFF) went through near-opaque light grey on enter and on exit.
    /// </summary>
    private static void HoverIsInstant(Window window, string theme)
    {
        string[] classes = { "tool", "icon", "menu", "railitem", "tabclose", "captionbtn", "linkrow" };
        var backdrop = Token(window, "BgPaneBrush");
        var away = new Point(2, window.Bounds.Height - 2);
        var tried = new List<string>();
        var bad = new List<string>();
        foreach (var cls in classes)
        {
            var button = window.GetVisualDescendants().OfType<Button>()
                .FirstOrDefault(b => b.Classes.Contains(cls) && b.IsEffectivelyVisible && b.IsEffectivelyEnabled && b.Bounds.Width > 0
                                     && !b.Classes.Contains("current") && OnScreen(b));
            if (button is null)
                continue;
            tried.Add(cls);
            var presenter = Presenter(button);
            MoveTo(window, away);
            DrawFrame(window);
            var rest = Over(presenter.Background, backdrop);
            var samples = new List<Color>();
            void Run(Point at)
            {
                MoveTo(window, at);
                for (var i = 0; i < 14; i++)
                {
                    System.Threading.Thread.Sleep(10);
                    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                    Dispatcher.UIThread.RunJobs();
                    samples.Add(Over(presenter.Background, backdrop));
                }
            }
            Run(CentreOf(window, button));
            var hover = samples[^1];
            Run(away);
            var (lo, hi) = (Math.Min(Luminance(rest), Luminance(hover)), Math.Max(Luminance(rest), Luminance(hover)));
            var outside = samples.Where(c => Luminance(c) > hi + 0.002 || Luminance(c) < lo - 0.002).ToList();
            var instant = samples.Count(c => c != rest && c != hover);
            if (outside.Count > 0 || instant > 0 || hover == rest)
                bad.Add($"{cls}: rest {Hex(rest)}, hover {Hex(hover)}, {instant} in-between frames{(outside.Count > 0 ? $", e.g. {Hex(outside.Max(Luminance) > hi ? outside.OrderBy(Luminance).Last() : outside[0])}" : "")}");
        }
        Check($"hover ({theme}): {string.Join(", ", tried)} hover at once, never through a colour outside rest and hover{(bad.Count > 0 ? " — " + string.Join("; ", bad) : "")}",
            bad.Count == 0 && tried.Count >= 5);
        MoveTo(window, away);
    }

    // ── S1: labels take their control's state colour ──────────────────────────

    private static void StateColours(Window window, MainViewModel vm, string theme)
    {
        var text = Token(window, "TextBrush");
        var dim = Token(window, "TextDimBrush");
        var results = new List<string>();
        var ok = true;
        void Expect(string what, TextBlock label, Color want)
        {
            var got = BrushColor(label.Foreground);
            results.Add($"{what} {(got is { } g ? Hex(g) : "?")}");
            ok &= got is { } c && Near(c, want);
        }

        var tabs = window.FindControl<ListBox>("TabList")!;
        var selected = (Control)tabs.ContainerFromItem(vm.ActiveTab!)!;
        var other = (Control)tabs.ContainerFromItem(vm.OpenTabs.First(t => t != vm.ActiveTab))!;
        Expect("current tab", Label(selected), text);
        Expect("other tab", Label(other), dim);

        var rail = window.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("railitem") && b.IsEffectivelyVisible).ToList();
        if (rail.FirstOrDefault(b => !b.Classes.Contains("current") && b.IsEffectivelyEnabled) is { } idle)
        {
            Expect("rail at rest", Label(idle), dim);
            MoveTo(window, CentreOf(window, idle));
            DrawFrame(window);
            Expect("rail hovered", Label(idle), text);
            MoveTo(window, new Point(2, window.Bounds.Height - 2));
        }
        if (rail.FirstOrDefault(b => b.Classes.Contains("current")) is { } current)
            Expect("current rail item", Label(current), text);

        var views = window.GetVisualDescendants().OfType<RadioButton>().Where(r => r.Classes.Contains("viewtab") && r.IsEffectivelyVisible).ToList();
        if (views.FirstOrDefault(r => r.IsChecked == true) is { } on)
            Expect("checked view tab", Label(on), text);
        if (views.FirstOrDefault(r => r.IsChecked != true) is { } off)
            Expect("view tab", Label(off), dim);

        Check($"state colours ({theme}): labels follow their control ({string.Join(", ", results)})", ok && results.Count >= 6);

        // The Explorer's placeholder is the faint token at full strength (Fluent's was its own grey at half opacity).
        var search = window.GetVisualDescendants().OfType<TextBox>().First(t => t.Name == "SearchBox");
        var placeholder = search.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Name == "PART_Placeholder");
        var faint = Token(window, "TextFaintBrush");
        Check($"placeholder ({theme}): faint text at full opacity ({(placeholder is null ? "none" : $"{Hex(BrushColor(placeholder.Foreground) ?? default)} × {placeholder.Opacity}")}, " +
              $"{Contrast(faint, Token(window, "BgFieldBrush")):0.0}:1 on a field)",
            placeholder is not null && BrushColor(placeholder.Foreground) is { } pc && Near(pc, faint) && placeholder.Opacity == 1
            && Contrast(faint, Token(window, "BgFieldBrush")) >= 4.5);
    }

    // ── U2 / S4: one dropdown ─────────────────────────────────────────────────

    private static ChoiceBox? FormChoice(Window window) =>
        window.GetVisualDescendants().OfType<ChoiceBox>()
            .FirstOrDefault(c => c.IsEffectivelyVisible && c.IsEffectivelyEnabled && c.ItemCount >= 3 && OnScreen(c));

    private static void DropdownLook(Window window, MainViewModel vm, string theme, string outDir)
    {
        var choice = FormChoice(window);
        if (choice is null)
        {
            Check($"dropdown ({theme}): a choice row on screen", false);
            return;
        }
        Click(window, choice);
        DrawFrame(window);
        var popupBorder = choice.GetVisualDescendants().OfType<Popup>().First(p => p.Name == "PART_Popup").Child as Border;
        var items = Enumerable.Range(0, choice.ItemCount).Select(i => choice.ContainerFromIndex(i)).OfType<ComboBoxItem>().ToList();
        var ui = (FontFamily)window.FindResource("UiFont")!;
        var heights = items.Select(i => i.Bounds.Height).Distinct().ToList();
        var widths = items.Select(i => Math.Round(i.Bounds.Width, 1)).Distinct().ToList();
        var faces = items.All(i => i.FontFamily == ui && i.FontWeight == FontWeight.Normal);
        var stack = choice.ItemsPanelRoot?.GetType() == typeof(StackPanel);
        var wide = popupBorder is not null && popupBorder.Bounds.Width >= choice.Bounds.Width - 0.5;
        Check($"dropdown ({theme}): {items.Count} rows all {string.Join("/", heights)} px (24 under a row editor), one width ({string.Join("/", widths)}), the UI face at regular weight ({faces}), every row measured at open ({stack}), never narrower than the box ({wide})",
            items.Count == choice.ItemCount && heights.SequenceEqual(new[] { 24.0 }) && widths.Count == 1 && faces && stack && wide);

        var card = Token(window, "ComboBoxDropDownBackground");
        var active = Token(window, "ComboBoxItemBackgroundSelected");
        var selectedItem = items.FirstOrDefault(i => i.IsSelected);
        var selectedFill = selectedItem is null ? null : BrushColor(Presenter(selectedItem).Background);
        var cardFill = BrushColor(popupBorder?.Background);
        var radius = popupBorder?.CornerRadius.TopLeft;
        var overlay = ((CornerRadius)window.FindResource("RadiusOverlay")!).TopLeft;
        Check($"dropdown ({theme}): the menus' card ({(cardFill is { } cf ? Hex(cf) : "?")}, radius {radius}) and a neutral selected row ({(selectedFill is { } sf ? Hex(sf) : "?")})",
            cardFill is { } f && Near(f, card) && radius == overlay && selectedFill is { } s && Near(s, active)
            && Math.Max(s.R, Math.Max(s.G, s.B)) - Math.Min(s.R, Math.Min(s.G, s.B)) <= 8 && s != card);
        Capture(window, Path.Combine(outDir, $"s01-dropdown-{theme}.png"));
        KeyStroke(window, K.Escape);
        DrawFrame(window);
        var shown = choice.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.IsEffectivelyVisible && !string.IsNullOrEmpty(t.Text));
        Check($"dropdown ({theme}): the value in the closed box trims with an ellipsis ({shown?.TextTrimming})",
            shown?.TextTrimming == TextTrimming.CharacterEllipsis);
    }

    // ── E16: the wheel over a closed dropdown scrolls the form ────────────────

    private static void DropdownWheel(Window window, MainViewModel vm)
    {
        var choice = FormChoice(window);
        var row = choice?.DataContext as ChoicePropertyViewModel;
        var scroller = window.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault(s => s.Name == "FormScroll" && s.IsEffectivelyVisible);
        if (choice is null || row is null || scroller is null)
        {
            Check("dropdown wheel: a choice row in the form", false);
            return;
        }
        // Focused the way a user focuses it: open with a click, close with Esc (the keyboard stays on it).
        Click(window, choice);
        DrawFrame(window);
        KeyStroke(window, K.Escape);
        DrawFrame(window);
        var focused = choice.IsFocused;
        var before = row.Value;
        var offset = scroller.Offset.Y;
        var at = CentreOf(window, choice);
        MoveTo(window, at);
        for (var i = 0; i < 3; i++)
            RawInput.Send(window, "MouseWheel", at, new Vector(0, -1), RawInputModifiers.None);
        DrawFrame(window);
        Check($"dropdown wheel: over a focused, closed dropdown ({focused}) the wheel scrolls the form ({offset:0} → {scroller.Offset.Y:0}) and the value stays '{before}' ('{row.Value}')",
            focused && row.Value == before && scroller.Offset.Y > offset);

        // Over the open list the wheel scrolls the list and still picks nothing.
        scroller.Offset = new Vector(scroller.Offset.X, offset);
        DrawFrame(window);
        Click(window, choice);
        DrawFrame(window);
        var open = choice.IsDropDownOpen;
        var item = choice.ContainerFromIndex(0) as Control;
        if (item is not null)
        {
            var itemAt = CentreOf(window, item);
            RawInput.Send(window, "MouseWheel", itemAt, new Vector(0, -1), RawInputModifiers.None);
            DrawFrame(window);
        }
        Check($"dropdown wheel: over the open list the wheel picks nothing ({open}, '{row.Value}')", open && row.Value == before);
        KeyStroke(window, K.Escape);
        DrawFrame(window);
    }

    // ── S10 / S11: menus ──────────────────────────────────────────────────────

    private static void MenuLook(Window window, string theme, string outDir)
    {
        var editor = window.GetVisualDescendants().OfType<AssetEditorView>().First();
        var more = editor.GetVisualDescendants().OfType<Button>().First(b => b.Content as string == "⋯" && b.IsEffectivelyVisible);
        Click(window, more);
        DrawFrame(window);
        var presenter = window.GetVisualDescendants().Concat(TopLevel.GetTopLevel(window)!.GetVisualDescendants())
            .OfType<MenuFlyoutPresenter>().FirstOrDefault()
            ?? Avalonia.Controls.Primitives.OverlayLayer.GetOverlayLayer(window)?.GetVisualDescendants().OfType<MenuFlyoutPresenter>().FirstOrDefault();
        if (presenter is null)
        {
            Check($"menu ({theme}): the ⋯ menu opens", false);
            return;
        }
        var items = presenter.GetVisualDescendants().OfType<MenuItem>().Where(m => m.IsEffectivelyVisible).ToList();
        var heights = items.Select(m => m.Bounds.Height).Distinct().ToList();
        var overlay = ((CornerRadius)window.FindResource("RadiusOverlay")!).TopLeft;
        var root = presenter.GetVisualDescendants().OfType<Border>().First(b => b.Name == "LayoutRoot");
        Check($"menu ({theme}): {items.Count} items all {string.Join("/", heights)} px, {presenter.Padding.Left} px inset, radius {presenter.CornerRadius.TopLeft}, a shadow ({root.BoxShadow.Count > 0})",
            items.Count > 3 && heights.SequenceEqual(new[] { 28.0 }) && presenter.Padding.Left == 4 && presenter.CornerRadius.TopLeft == overlay && root.BoxShadow.Count > 0);
        Capture(window, Path.Combine(outDir, $"s02-menu-{theme}.png"));
        KeyStroke(window, K.Escape);
        DrawFrame(window);
    }

    // ── S7 / S8: the number field's edge and ring are drawn over its steps ────

    private static void NumberFieldEdge(Window window, string theme)
    {
        var box = window.GetVisualDescendants().OfType<ScrubNumberBox>()
            .FirstOrDefault(b => b.IsEffectivelyVisible && b.Bounds.Width >= 120 && OnScreen(b));
        if (box is null)
        {
            Check($"number field ({theme}): a wide number field on screen", false);
            return;
        }
        box.Focus(NavigationMethod.Tab);
        MoveTo(window, CentreOf(window, box));
        DrawFrame(window);
        var steps = box.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("scrubstep")).ToList();
        var shown = steps.Count == 2 && steps.All(b => b.IsEffectivelyVisible && b.Classes.Contains("rowaction"));
        var glyphs = string.Join(" ", steps.Select(b => b.Content as string));
        // The ring's pixel at the right end, beside the + step, and at the top over the steps.
        var right = box.TranslatePoint(new Point(box.Bounds.Width - 1, box.Bounds.Height / 2), window)!.Value;
        var top = box.TranslatePoint(new Point(box.Bounds.Width - 12, 0.5), window)!.Value;
        var focus = Token(window, "FocusBrush");
        var (r, t) = (PixelOf(window, right), PixelOf(window, top));
        Check($"number field ({theme}): focused and hovered, the − + steps show as row actions ({shown}: {glyphs}) and the focus ring runs past them (right {Hex(r)}, top {Hex(t)}, ring {Hex(focus)})",
            shown && glyphs == "− +" && Near(r, focus, 24) && Near(t, focus, 24));
        // Hovering a step fills it like a row action.
        var plus = steps[1];
        MoveTo(window, CentreOf(window, plus));
        DrawFrame(window);
        var fill = BrushColor(Presenter(plus).Background);
        Check($"number field ({theme}): a hovered step takes the row actions' hover fill ({(fill is { } f ? Hex(f) : "?")})",
            fill is { } h && Near(h, Token(window, "BgHoverBrush")));
        MoveTo(window, new Point(2, window.Bounds.Height - 2));
        window.Focus();
        DrawFrame(window);
    }

    // ── S5: mono stays regular ────────────────────────────────────────────────

    private static void MonoStaysRegular(Window window)
    {
        // By now the window has drawn the asset title in SemiBold mono. The values must still draw with a regular face
        // of their own (Avalonia filed every instance of the variable font under 400, so the first weight asked for
        // took the regular slot), at the weight that matches the labels' ink, and the title with a real semibold.
        var mono = (FontFamily)window.FindResource("MonoFont")!;
        FontManager.Current.TryGetGlyphTypeface(new Typeface(mono, FontStyle.Normal, FontWeight.SemiBold), out var semi);
        var drawn = window.GetVisualDescendants().OfType<ScrubNumberBox>().First(b => b.IsEffectivelyVisible)
            .GetVisualDescendants().OfType<TextBlock>().First(t => !string.IsNullOrEmpty(t.Text));
        var face = drawn.TextLayout.TextLines[0].TextRuns.OfType<Avalonia.Media.TextFormatting.ShapedTextRun>().First().GlyphRun.GlyphTypeface;
        var title = window.GetVisualDescendants().OfType<SelectableTextBlock>().First(t => t.Name == "AssetTitle");
        var titleFace = title.TextLayout.TextLines[0].TextRuns.OfType<Avalonia.Media.TextFormatting.ShapedTextRun>().First().GlyphRun.GlyphTypeface;
        Check($"mono: a value draws in its own regular face ({face.FamilyName}, {face.FontSimulations}), apart from the semibold title's ({titleFace.FamilyName}, {titleFace.FontSimulations})",
            face.FamilyName == "IBM Plex Mono" && semi is not null && !ReferenceEquals(face, titleFace)
            && face.FontSimulations == FontSimulations.None && titleFace.FontSimulations == FontSimulations.None);
    }

    // ── S13: a focused current tab keeps its bar ──────────────────────────────

    private static void FocusedTab(Window window, MainViewModel vm, string theme)
    {
        var tabs = window.FindControl<ListBox>("TabList")!;
        var tab = (ListBoxItem)tabs.ContainerFromItem(vm.ActiveTab!)!;
        tab.Focus(NavigationMethod.Tab);
        DrawFrame(window);
        var want = (BoxShadows)window.FindResource(window.ActualThemeVariant, "ShadowTabActiveFocus")!;
        var got = Presenter(tab).BoxShadow;
        Check($"tabs ({theme}): the keyboard on the current tab draws the ring and keeps the accent bar ({got})",
            tab.IsFocused && got == want && got.Count == 2);
        window.Focus();
        DrawFrame(window);
    }

    // ── U10: one control height per row ───────────────────────────────────────

    private static void BulkBarHeights(Window window, MainViewModel vm, string theme)
    {
        vm.FilterText = "type:weapon gdt:t7";
        vm.ApplyFilterNow();
        vm.OpenTableCommand.Execute(null);
        if (vm.Table is not { } table)
        {
            Check($"heights ({theme}): the table opens", false);
            return;
        }
        foreach (var r in table.Rows.Take(3))
            r.IsSelected = true;
        table.SetColumnShown(Apex.Editor.Models.SchemaRegistry.Get(table.TypeName)!.Find("damage")!, true);
        table.SelectedColumn = table.Columns.First(c => c.Key == "damage");
        DrawFrame(window);
        var view = window.GetVisualDescendants().OfType<Apex.Editor.Views.TableView>().FirstOrDefault(v => v.IsEffectivelyVisible);
        var combo = view?.GetVisualDescendants().OfType<ComboBox>().FirstOrDefault(c => c.Classes.Contains("field") && c.IsEffectivelyVisible);
        if (combo is null)
        {
            Check($"heights ({theme}): the table's bulk bar shows ({view is not null}, {table.SelectedCount} rows selected)", false);
            table.CloseCommand.Execute(null);
            return;
        }
        var bar = combo.GetVisualAncestors().OfType<StackPanel>().First();
        var parts = bar.GetVisualDescendants().OfType<Control>()
            .Where(c => c.IsEffectivelyVisible && (c == combo || c is ScrubNumberBox || c is Button { Classes: var k } && k.Contains("accent")))
            .ToList();
        var heights = parts.Select(c => c.Bounds.Height).Distinct().ToList();
        var centres = parts.Select(c => Math.Round(c.TranslatePoint(new Point(0, c.Bounds.Height / 2), bar)!.Value.Y, 1)).Distinct().ToList();
        Check($"heights ({theme}): the bulk bar's column, value and apply ({parts.Count}) are one height ({string.Join("/", heights)}) on one centre line ({string.Join("/", centres)})",
            parts.Count == 3 && heights.Count == 1 && centres.Count == 1);
        table.CloseCommand.Execute(null);
        vm.FilterText = "";
        vm.ApplyFilterNow();
        DrawFrame(window);
    }

    // ── U10: one control height per row ───────────────────────────────────────

    private static void HeaderHeights(Window window, string theme)
    {
        var editor = window.GetVisualDescendants().OfType<AssetEditorView>().First();
        var header = editor.GetVisualDescendants().OfType<Button>()
            .Where(b => b.IsEffectivelyVisible && (b.Content as string is "‹" or "›" or "⋯" || b.Classes.Contains("line")) && b.TranslatePoint(default, editor)!.Value.Y < 60)
            .ToList();
        var heights = header.Select(b => b.Bounds.Height).Distinct().ToList();
        var centres = header.Select(b => Math.Round(b.TranslatePoint(new Point(0, b.Bounds.Height / 2), editor)!.Value.Y)).Distinct().ToList();
        Check($"heights ({theme}): the editor header's Compare and ⋯ are one height ({string.Join("/", heights)}) on one centre line ({string.Join("/", centres)})",
            header.Count >= 2 && heights.Count == 1 && centres.Count == 1);
    }

    // ── S2 / S3: the one Sky ──────────────────────────────────────────────────

    private static void PrimaryAndChecked(string theme)
    {
        var accent = new Button { Classes = { "accent" }, Content = "Save", Margin = new Thickness(20) };
        var check = new CheckBox { IsChecked = true, Content = "On", Margin = new Thickness(20) };
        var field = new TextBox { Classes = { "field" }, Text = "selection", Width = 200, Margin = new Thickness(20) };
        var bar = new StackPanel { Children = { accent, check, field } };
        var window = new Window { Width = 400, Height = 300, Content = new Border { Classes = { "pane" }, Child = bar } };
        window.Show();
        DrawFrame(window);

        var sky = Token(window, "AccentBrush");
        var skyHover = Token(window, "AccentHoverBrush");
        var presenter = Presenter(accent);
        MoveTo(window, new Point(2, 290));
        DrawFrame(window);
        var restFill = BrushColor(presenter.Background);
        var restText = BrushColor(Label(accent).Foreground);
        MoveTo(window, CentreOf(window, accent));
        DrawFrame(window);
        var hoverFill = BrushColor(presenter.Background);
        var hoverText = BrushColor(Label(accent).Foreground);
        var good = restFill is { } rf && Near(rf, sky) && hoverFill is { } hf && Near(hf, skyHover)
                   && restText is { } rt && hoverText is { } ht && rt == ht
                   && Contrast(rt, rf) >= 4.5 && Contrast(ht, hf) >= 4.5;
        Check($"primary ({theme}): Sky at rest ({(restFill is { } a ? Hex(a) : "?")}) and on hover ({(hoverFill is { } b ? Hex(b) : "?")}), one label colour " +
              $"({(restText is { } c ? Hex(c) : "?")} / {(hoverText is { } d ? Hex(d) : "?")}) at AA " +
              $"({(restText is { } e && restFill is { } g ? Contrast(e, g) : 0):0.0}:1, {(hoverText is { } h && hoverFill is { } j ? Contrast(h, j) : 0):0.0}:1)", good);

        var system = Token(window, "SystemAccentColor");
        var box = check.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Name == "NormalRectangle");
        var boxFill = BrushColor(box?.Background);
        var selection = field.SelectionBrush as ISolidColorBrush;
        Check($"one blue ({theme}): Fluent's accent ({Hex(system)}), a checked box ({(boxFill is { } bf ? Hex(bf) : "?")}) and text selection ({(selection is null ? "?" : Hex(selection.Color))}) are the accent ({Hex(sky)})",
            (Near(system, sky) || Near(system, Token(window, "AccentTextBrush"))) && boxFill is { } cb && Near(cb, system)
            && selection is not null && Near(Color.FromRgb(selection.Color.R, selection.Color.G, selection.Color.B), system));
        window.Close();
    }

    // ── S6: row actions keep their space ──────────────────────────────────────

    private static void RowActionsKeepTheirSpace(string theme)
    {
        var vm = new MainViewModel();
        vm.OpenByName("wpn_ar_havoc_zm_upgraded");
        var number = vm.ActiveTab!.AllSentinel.All.OfType<NumberPropertyViewModel>().First(p => p.Key == "damage");
        number.RawValue = "123";
        var editorView = new PropertyEditorView { DataContext = number, VerticalAlignment = VerticalAlignment.Center };
        var row = new Border { Classes = { "prow" }, Width = 200, Child = editorView };
        var window = new Window { Width = 400, Height = 200, Content = new Border { Classes = { "pane" }, Padding = new Thickness(40), Child = row } };
        window.Show();
        DrawFrame(window);
        var value = editorView.GetVisualDescendants().OfType<ContentControl>().First();
        var undo = editorView.GetVisualDescendants().OfType<Button>().First(b => b.Content as string == "↶");
        MoveTo(window, new Point(2, 190));
        DrawFrame(window);
        var restWidth = value.Bounds.Width;
        var hiddenAtRest = !undo.IsEffectivelyVisible;
        var clickThrough = window.InputHitTest(CentreOf(window, undo.GetVisualParent<Panel>()!.GetVisualParent<Panel>()!)) is not Button;
        MoveTo(window, CentreOf(window, row));
        DrawFrame(window);
        var hoverWidth = value.Bounds.Width;
        var shown = undo.IsEffectivelyVisible && undo.IsEffectivelyEnabled;
        Check($"row actions ({theme}): the value keeps its width when ↶ appears ({restWidth:0} → {hoverWidth:0} px); hidden and unclickable at rest ({hiddenAtRest}, {clickThrough}), shown on hover ({shown})",
            restWidth == hoverWidth && hiddenAtRest && clickThrough && shown);
        window.Close();
        vm.Dispose();
    }
}
