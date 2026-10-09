using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Apex.Editor.Commands;
using Apex.Editor.Services;
using Apex.Editor.ViewModels;
using Apex.Editor.Views;

namespace Apex.Shots;

/// <summary>
/// The three themes: picked from the app menu (the Apex mark) with real clicks, applied live, remembered, and each one's text and
/// accent tokens clearing their contrast floors on the surfaces they sit on.
/// </summary>
public partial class Program
{
    private static void RunThemeChecks(string outDir)
    {
        var sessionRoot = Path.Combine(Path.GetTempPath(), "apex-shots-theme-" + Guid.NewGuid().ToString("N")[..8]);
        var app = Application.Current!;
        app.RequestedThemeVariant = ThemeVariant.Default;
        var vm = new MainViewModel(sessionRoot);
        vm.OpenByName("wpn_ar_havoc_zm_upgraded");
        var window = new MainWindow { DataContext = vm, Width = 1600, Height = 1000 };
        window.Show();
        window.Activate();
        try
        {
            Check($"theme: a fresh install follows Windows ({vm.Theme}, requested {app.RequestedThemeVariant})",
                vm.Theme == ThemeChoice.System && app.RequestedThemeVariant == ThemeVariant.Default);
            // The headless platform has no light/dark setting, so Default resolves to nothing here: stand in for a dark Windows.
            app.RequestedThemeVariant = ThemeVariant.Dark;
            window.UpdateLayout();
            Pump();

            foreach (var (choice, pane, name) in new[]
                     {
                         (ThemeChoice.Slate, "#FF111720", "slate"),
                         (ThemeChoice.Light, "#FFFFFFFF", "light"),
                         (ThemeChoice.Graphite, "#FF1C1C1C", "graphite"),
                     })
            {
                var item = PickTheme(window, CommandCatalog.Get(ThemeId(choice)).Name);
                var actual = window.ActualThemeVariant;
                var paneColor = ThemeToken(window, "BgPaneBrush");
                Check($"theme: clicking '{item}' in the app menu applies {choice} live (variant {actual}, pane {paneColor})",
                    vm.Theme == choice && app.RequestedThemeVariant == AppTheme.VariantOf(choice)
                    && paneColor == Color.Parse(pane));
                Check($"theme: {choice} is remembered ({vm.Settings.Theme})", vm.Settings.Theme == choice.ToString());

                var light = choice == ThemeChoice.Light;
                // Slate inherits Dark: a key only Fluent defines must still resolve, and to its dark value.
                var fluent = new[] { "SystemControlForegroundBaseHighBrush", "TextControlForeground", "ButtonForeground" }
                    .Select(k => window.TryFindResource(k, actual, out var f) && f is ISolidColorBrush fb ? fb.Color : (Color?)null)
                    .FirstOrDefault(c => c is not null);
                Check($"theme: {choice} still finds Fluent's own keys at the right lightness ({fluent})",
                    fluent is { } fc && (fc.R > 128) != light);
                var weapon = ((ISolidColorBrush)TypeStyles.Brush("weapon")).Color;
                Check($"theme: {choice} gives type glyphs their {(light ? "ink" : "hue")} ({weapon})",
                    weapon == (Color)app.FindResource(light ? "TypeWeaponsInk" : "TypeWeapons")!);

                ContrastChecks(window, choice);
                Capture(window, Path.Combine(outDir, $"120-theme-{name}.png"));
            }

            // The menu marks the one in use, as radio items.
            var mark = AppMenuButton(window);
            Click(window, mark);
            var items = ((MenuFlyout)mark.Flyout!).Items.OfType<MenuItem>()
                .Where(i => i.ToggleType == MenuItemToggleType.Radio).ToList();
            Check($"theme: the app menu lists four themes as radio items with Graphite checked ({string.Join(", ", items.Select(i => $"{i.Header}{(i.IsChecked ? " ✓" : "")}"))})",
                items.Count == 4 && items.Single(i => i.IsChecked).Header as string == CommandCatalog.Get(CommandCatalog.ThemeGraphite).Name);
            Capture(window, Path.Combine(outDir, "121-theme-menu.png"));
            ((MenuFlyout)mark.Flyout!).Hide();
            Pump();

            PickTheme(window, CommandCatalog.Get(CommandCatalog.ThemeSystem).Name);
            Check($"theme: Match Windows theme hands the choice back to Windows ({app.RequestedThemeVariant})",
                vm.Theme == ThemeChoice.System && app.RequestedThemeVariant == ThemeVariant.Default);
        }
        finally
        {
            app.RequestedThemeVariant = ThemeVariant.Dark;
            window.Close();
            try { Directory.Delete(sessionRoot, recursive: true); } catch (IOException) { }
        }
    }

    private static string ThemeId(ThemeChoice choice) => choice switch
    {
        ThemeChoice.Graphite => CommandCatalog.ThemeGraphite,
        ThemeChoice.Slate => CommandCatalog.ThemeSlate,
        ThemeChoice.Light => CommandCatalog.ThemeLight,
        _ => CommandCatalog.ThemeSystem,
    };

    /// <summary>The Apex mark at the top left, which opens the app menu (the theme, Updates, About Apex).</summary>
    private static Button AppMenuButton(Window window) => window.FindControl<Button>("AppMenuButton")!;

    /// <summary>Opens the app menu and clicks the item named <paramref name="header"/> in its popup.</summary>
    private static string PickTheme(Window window, string header)
    {
        var mark = AppMenuButton(window);
        Click(window, mark);
        var item = ((MenuFlyout)mark.Flyout!).Items.OfType<MenuItem>().First(i => i.Header as string == header);
        if (TopLevel.GetTopLevel(item) is { } popup)
        {
            var at = item.TranslatePoint(new Point(item.Bounds.Width / 2, item.Bounds.Height / 2), popup)!.Value;
            popup.MouseDown(at, MouseButton.Left);
            popup.MouseUp(at, MouseButton.Left);
        }
        Pump();
        window.UpdateLayout();
        return header;
    }

    private static Color? ThemeToken(Control anchor, string key) =>
        anchor.TryFindResource(key, anchor.ActualThemeVariant, out var v) && v is ISolidColorBrush b ? b.Color : null;

    /// <summary>The floors Tokens.axaml promises, resolved the way the app resolves them.</summary>
    private static void ContrastChecks(Control anchor, ThemeChoice choice)
    {
        (string Fg, string Bg, double Min)[] pairs =
        [
            ("TextBrush", "BgPaneBrush", 7),
            ("TextBrush", "BgWindowBrush", 7),
            ("TextDimBrush", "BgPaneBrush", 4.5),
            ("TextDimBrush", "BgActiveBrush", 4.5),
            ("TextFaintBrush", "BgPaneBrush", 4.5),
            ("TextFaintBrush", "BgActiveBrush", 4.5),
            ("TextFaintBrush", "BgFieldBrush", 4.5),
            ("AccentTextBrush", "BgPaneBrush", 4.5),
            ("DangerTextBrush", "BgPaneBrush", 4.5),
            ("OnAccentBrush", "AccentBrush", 4.5),
            ("FocusBrush", "BgPaneBrush", 3),
            ("MarkBrush", "BgPaneBrush", 3),
        ];
        var low = pairs.Select(p => (p, ratio: Contrast(ThemeToken(anchor, p.Fg), ThemeToken(anchor, p.Bg))))
            .Where(x => x.ratio < x.p.Min)
            .Select(x => $"{x.p.Fg} on {x.p.Bg} {x.ratio:0.0}:1 < {x.p.Min}")
            .ToList();
        Check($"theme: {choice} text, accent and marks clear their contrast floors ({(low.Count == 0 ? "all" : string.Join("; ", low))})",
            low.Count == 0);
    }

    private static double Contrast(Color? a, Color? b)
    {
        if (a is not { } fg || b is not { } bg)
            return 0;
        static double Lum(Color c)
        {
            static double Ch(byte v)
            {
                var s = v / 255.0;
                return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * Ch(c.R) + 0.7152 * Ch(c.G) + 0.0722 * Ch(c.B);
        }
        var (l1, l2) = (Lum(fg), Lum(bg));
        return (Math.Max(l1, l2) + 0.05) / (Math.Min(l1, l2) + 0.05);
    }
}
