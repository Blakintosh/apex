using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Apex.Editor.Commands;
using Apex.Editor.Services;
using Apex.Editor.ViewModels;
using Apex.Editor.Views;
using K = Avalonia.Input.Key;

namespace Apex.Shots;

/// <summary>
/// The app menu under the Apex mark (the theme, Updates, About Apex) with real input: the mark sits and looks as it did
/// before it was a button, hovers and presses like the row's other buttons, opens by click and by keyboard, About shows
/// the version, and the title row still drags the window around it. Shot in Graphite, Slate and Light.
/// </summary>
public partial class Program
{
    private static void RunAppMenuChecks(string outDir)
    {
        var sessionRoot = Path.Combine(Path.GetTempPath(), "apex-shots-appmenu-" + Guid.NewGuid().ToString("N")[..8]);
        var app = Application.Current!;
        app.RequestedThemeVariant = ThemeVariant.Dark;
        var vm = new MainViewModel(sessionRoot) { Theme = ThemeChoice.Graphite };
        vm.OpenByName("wpn_ar_havoc_zm_upgraded");
        var window = new MainWindow { DataContext = vm, Width = 1600, Height = 1000 };
        window.Show();
        window.Activate();
        Pump(100);
        try
        {
            var mark = AppMenuButton(window);
            var menu = (MenuFlyout)mark.Flyout!;
            var about = (Flyout)FlyoutBase.GetAttachedFlyout(mark)!;
            InOverlay(menu);
            InOverlay(about);
            var row = window.FindControl<DockPanel>("TopBarLeft")!;
            var image = window.FindControl<Image>("SidebarMark")!;
            var title = window.FindControl<TextBlock>("SidebarTitle")!;
            var undo = window.FindControl<Button>("UndoButton")!;
            var layout = window.FindControl<Button>("LayoutButton")!;
            var add = window.FindControl<StackPanel>("SidebarActions")!.Children.OfType<Button>()
                .First(b => Avalonia.Automation.AutomationProperties.GetName(b) == "New asset or GDT");
            var presenter = mark.GetVisualDescendants().OfType<ContentPresenter>().First(p => p.Name == "PART_ContentPresenter");
            Color? Fill() => (presenter.Background as ISolidColorBrush)?.Color;
            Color? Token(string key) => ThemeToken(window, key);

            // At rest: where the bare mark sat (flush left in the row, centred in its 44 px), the name in the text colour,
            // nothing drawn behind it.
            var imageAt = image.TranslatePoint(default, row)!.Value;
            var titleAt = title.TranslatePoint(default, row)!.Value;
            Check($"app menu: the mark sits where it did (panther at {imageAt}, name at {titleAt})",
                Math.Abs(imageAt.X) < 0.5 && Math.Abs(imageAt.Y - 10) < 0.5 && Math.Abs(titleAt.X - 32) < 0.5);
            Check($"app menu: at rest it is only the mark (fill {Fill()}, name {(title.Foreground as ISolidColorBrush)?.Color} = text {Token("TextBrush")})",
                Fill() is not { A: > 0 } && (title.Foreground as ISolidColorBrush)?.Color == Token("TextBrush"));
            Check($"app menu: the mark says what it opens ('{ToolTip.GetTip(mark)}', name '{Avalonia.Automation.AutomationProperties.GetName(mark)}')",
                ToolTip.GetTip(mark) as string == "Theme, updates and about Apex"
                && Avalonia.Automation.AutomationProperties.GetName(mark) == "Apex");
            Check($"app menu: the mark is a hit target of at least 24 px ({mark.Bounds.Width:0}×{mark.Bounds.Height:0})",
                mark.Bounds.Width >= 24 && mark.Bounds.Height >= 24);

            // Hover and press: the icon buttons' fills.
            var markCentre = mark.TranslatePoint(new Point(mark.Bounds.Width / 2, mark.Bounds.Height / 2), window)!.Value;
            window.MouseMove(markCentre);
            Pump();
            Check($"app menu: hovering the mark fills it like the row's buttons ({Fill()} = {Token("BgHoverBrush")})",
                mark.IsPointerOver && Fill() == Token("BgHoverBrush"));
            ShootThemes(vm, window, outDir, "126-appmenu-mark-hover", crop: true);
            window.MouseDown(markCentre, MouseButton.Left);
            Pump();
            Check($"app menu: pressing it darkens it like them ({Fill()} = {Token("BgActiveBrush")})", Fill() == Token("BgActiveBrush"));
            window.MouseUp(markCentre, MouseButton.Left);
            Pump(60);

            // The click opened the menu: the themes, Updates, About Apex.
            var items = menu.Items.Select(i => i is MenuItem m ? m.Header as string ?? "?" : "—").ToList();
            var expected = new[]
            {
                CommandCatalog.Get(CommandCatalog.ThemeSystem).Name, CommandCatalog.Get(CommandCatalog.ThemeGraphite).Name,
                CommandCatalog.Get(CommandCatalog.ThemeSlate).Name, CommandCatalog.Get(CommandCatalog.ThemeLight).Name,
                "—", "Updates", "—", "About Apex",
            };
            Check($"app menu: a click on the mark opens the theme, Updates and About Apex ({string.Join(", ", items)})",
                menu.IsOpen && items.SequenceEqual(expected));
            var radios = menu.Items.OfType<MenuItem>().Where(i => i.ToggleType == MenuItemToggleType.Radio).ToList();
            Check($"app menu: the themes are radio items with the one in use checked ({string.Join(", ", radios.Where(r => r.IsChecked).Select(r => r.Header))})",
                radios.Count == 4 && radios.Single(r => r.IsChecked).Header as string == CommandCatalog.Get(CommandCatalog.ThemeGraphite).Name);
            ShootThemes(vm, window, outDir, "127-appmenu-open", crop: false);
            menu.Hide();
            window.MouseMove(new Point(800, 600));
            Pump(60);

            // The Layout menu is the panes and the preview layout again.
            Click(window, layout);
            var layoutItems = ((MenuFlyout)layout.Flyout!).Items.OfType<MenuItem>().Select(i => i.Header as string).ToList();
            Check($"app menu: Layout lists no theme, Updates or About any more ({string.Join(", ", layoutItems)}; tip '{ToolTip.GetTip(layout)}')",
                layoutItems.All(h => !expected.Contains(h)) && ToolTip.GetTip(layout) as string == "Panes and the preview layout");
            InOverlay((MenuFlyout)layout.Flyout!);
            ((MenuFlyout)layout.Flyout!).Hide();
            Pump();
            Click(window, layout);
            Pump(60);
            Capture(window, Path.Combine(outDir, "130-layout-menu-after.png"));
            ((MenuFlyout)layout.Flyout!).Hide();
            Pump(60);

            // Keyboard: the mark is the row's first stop, before its first enabled button (Undo and Redo are off with
            // nothing to undo), with the focus ring, and Enter and Space open the menu.
            var first = new[] { undo, window.FindControl<Button>("RedoButton")!, add, layout }.First(b => b.IsEffectivelyEnabled);
            first.Focus(NavigationMethod.Tab);
            Key(window, K.Tab, RawInputModifiers.Shift);
            var reached = window.FocusManager?.GetFocusedElement();
            Check($"app menu: Shift+Tab from the row's first button ({Avalonia.Automation.AutomationProperties.GetName(first)}) reaches the mark ({(reached as Control)?.Name})", reached == mark);
            Check($"app menu: the focused mark shows the focus ring ({presenter.BoxShadow})", presenter.BoxShadow.Count > 0);
            ShootThemes(vm, window, outDir, "126-appmenu-mark-focus", crop: true);
            Key(window, K.Tab);
            Check($"app menu: Tab from the mark goes on along the row ({Avalonia.Automation.AutomationProperties.GetName((Control)window.FocusManager?.GetFocusedElement()!)})",
                window.FocusManager?.GetFocusedElement() == first);
            mark.Focus(NavigationMethod.Tab);
            Key(window, K.Enter);
            Pump(60);
            Check("app menu: Enter on the focused mark opens the menu", menu.IsOpen);
            Key(window, K.Escape);
            Pump(60);
            Check("app menu: Escape closes it", !menu.IsOpen);
            mark.Focus(NavigationMethod.Tab);
            Key(window, K.Space);
            Pump(60);
            Check("app menu: Space on the focused mark opens it too", menu.IsOpen);
            menu.Hide();
            Pump(60);

            // Updates, moved whole: open on the version, Check now beside it.
            OpenUpdatesMenu(window);
            var status = window.FindControl<MenuItem>("UpdateStatusItem")!;
            Check($"app menu: Updates opens from the mark on the version line ('{status.Header}', sub-menu {window.FindControl<MenuItem>("UpdatesMenu")!.IsSubMenuOpen})",
                menu.IsOpen && window.FindControl<MenuItem>("UpdatesMenu")!.IsSubMenuOpen
                && status.Header as string == $"Version {AppUpdates.CurrentVersion}");
            ShootThemes(vm, window, outDir, "128-appmenu-updates", crop: false);
            menu.Hide();
            Pump(1000);

            // About Apex, by a real click in the menu: the version and one line, under the mark.
            Click(window, mark);
            Pump(60);
            ClickInPopup(menu.Items.OfType<MenuItem>().Last());
            Pump(60);
            var aboutTitle = window.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Name == "AboutTitle");
            Check($"app menu: About Apex shows the version ('{aboutTitle?.Text}', menu closed {!menu.IsOpen})",
                about.IsOpen && !menu.IsOpen && aboutTitle?.Text == $"Apex {AppUpdates.CurrentVersion}");
            ShootThemes(vm, window, outDir, "129-appmenu-about", crop: false);
            about.Hide();
            Pump(60);
            vm.Registry[CommandCatalog.About].Execute();
            Pump(60);
            Check("app menu: the palette's About Apex opens the same flyout", about.IsOpen);
            about.Hide();
            Pump(60);
            Check($"app menu: the palette still finds the themes, Check for updates and About Apex",
                new[] { CommandCatalog.ThemeSystem, CommandCatalog.ThemeGraphite, CommandCatalog.ThemeSlate, CommandCatalog.ThemeLight,
                        CommandCatalog.CheckForUpdates, CommandCatalog.About }.All(id => vm.Registry.Contains(id) && vm.Registry[id].CanRun));

            // Shot at rest, the pointer away from it.
            window.MouseMove(new Point(800, 600));
            Pump();
            ShootThemes(vm, window, outDir, "126-appmenu-mark-rest", crop: true);

            // The title row still drags: the empty part of the sidebar's row (between the mark and Undo) and of the card's
            // top row reach the drag handler (a double-click there maximizes and restores, the same handler's other half);
            // a double-click on the mark doesn't.
            var gapLeft = mark.TranslatePoint(new Point(mark.Bounds.Width, 0), window)!.Value.X;
            var gapRight = window.FindControl<StackPanel>("SidebarActions")!.TranslatePoint(default, window)!.Value.X;
            var gap = new Point((gapLeft + gapRight) / 2, row.TranslatePoint(new Point(0, row.Bounds.Height / 2), window)!.Value.Y);
            var topBar = window.FindControl<Grid>("TopBar")!;
            var cardRow = topBar.TranslatePoint(new Point(topBar.Bounds.Width * 0.55, topBar.Bounds.Height / 2), window)!.Value;
            foreach (var (where, at) in new[] { ("the sidebar row's gap", gap), ("the card's top row", cardRow) })
            {
                var hit = window.InputHitTest(at) as Visual;
                var onButton = hit?.FindAncestorOfType<Button>(includeSelf: true) is not null;
                DoubleClick(window, at);
                var maximized = window.WindowState == WindowState.Maximized;
                DoubleClick(window, at);
                Check($"app menu: {where} still drags the window (hit {hit?.GetType().Name}, on a button {onButton}, double-click maximized {maximized}, restored {window.WindowState})",
                    !onButton && maximized && window.WindowState == WindowState.Normal);
            }
            Check($"app menu: the sidebar row keeps drag room beside the mark at its default width ({gapRight - gapLeft:0} px)", gapRight - gapLeft >= 40);
            DoubleClick(window, markCentre);
            Pump(60);
            Check($"app menu: a double-click on the mark is the button's, not a maximize ({window.WindowState})", window.WindowState == WindowState.Normal);
            menu.Hide();
            Pump(60);

            // The Explorer hidden: the mark alone, centred over the verbs, still opens the menu.
            vm.Registry[CommandCatalog.ToggleExplorer].Execute();
            Pump(60);
            var sidebar = window.FindControl<DockPanel>("Sidebar")!;
            var centre = image.TranslatePoint(new Point(12, 12), sidebar)!.Value.X;
            Click(window, mark);
            Pump(60);
            Check($"app menu: with the Explorer hidden the mark stays centred ({centre:0} of {sidebar.Bounds.Width:0}) and opens the menu",
                Math.Abs(centre - sidebar.Bounds.Width / 2) < 1 && menu.IsOpen && !title.IsVisible);
            Capture(window, Path.Combine(outDir, "131-appmenu-explorer-hidden.png"));
            menu.Hide();
            vm.Registry[CommandCatalog.ToggleExplorer].Execute();
            Pump(60);
        }
        finally
        {
            vm.Theme = ThemeChoice.Graphite;
            app.RequestedThemeVariant = ThemeVariant.Dark;
            window.Close();
            try { Directory.Delete(sessionRoot, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>Draws <paramref name="flyout"/> in the window's overlay layer so the window capture includes it.</summary>
    private static void InOverlay(PopupFlyoutBase flyout)
    {
        if (typeof(PopupFlyoutBase).GetProperty("Popup", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(flyout) is Popup popup)
            popup.ShouldUseOverlayLayer = true;
    }

    private static void DoubleClick(Window window, Point at)
    {
        // Past the double-click time, so these two presses count as a pair of their own.
        System.Threading.Thread.Sleep(600);
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        Pump();
    }

    /// <summary>The window, or (<paramref name="crop"/>) its top-left corner, in Graphite, Slate and Light.</summary>
    private static void ShootThemes(MainViewModel vm, Window window, string outDir, string name, bool crop)
    {
        var was = vm.Theme;
        foreach (var (choice, theme) in new[] { (ThemeChoice.Graphite, "graphite"), (ThemeChoice.Slate, "slate"), (ThemeChoice.Light, "light") })
        {
            vm.Theme = choice;
            Pump(60);
            var path = Path.Combine(outDir, $"{name}-{theme}.png");
            if (!crop)
            {
                Capture(window, path);
                continue;
            }
            Pump();
            using var frame = window.CaptureRenderedFrame();
            if (frame is null)
                continue;
            var scale = frame.PixelSize.Width / window.Bounds.Width;
            var size = new PixelSize((int)(360 * scale), (int)(64 * scale));
            using var corner = new Avalonia.Media.Imaging.RenderTargetBitmap(size, frame.Dpi);
            using (var dc = corner.CreateDrawingContext())
                dc.DrawImage(frame, new Rect(0, 0, size.Width, size.Height), new Rect(0, 0, size.Width, size.Height));
            corner.Save(path, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            Console.WriteLine($"wrote {path}");
        }
        vm.Theme = was;
        Pump(60);
    }
}
