using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Apex.Editor.Services;
using Apex.Editor.ViewModels;

namespace Apex.Editor.Views;

public partial class PreviewWindow : Window
{
    // The last size and position while not maximized: what a maximized window restores to next time.
    private PixelPoint _normalPosition;
    private Size _normalSize;

    public PreviewWindow()
    {
        InitializeComponent();
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Mica, WindowTransparencyLevel.None };
        PositionChanged += (_, _) => RememberNormalBounds();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ClientSizeProperty)
            RememberNormalBounds();
    }

    private void RememberNormalBounds()
    {
        if (WindowState != WindowState.Normal)
            return;
        _normalPosition = Position;
        _normalSize = ClientSize;
    }

    /// <summary>Puts the window where it was last time, when that is still on a screen (a monitor may have gone).</summary>
    public void Restore(UiSettings.WindowPlacement? placement)
    {
        if (placement is null || placement.Width < MinWidth || placement.Height < MinHeight)
            return;
        var position = new PixelPoint(placement.X, placement.Y);
        // Enough of the title bar must land on a screen to grab it.
        var grip = position + new PixelPoint(60, 12);
        if (!Screens.All.Any(s => s.WorkingArea.Contains(grip)))
            return;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Position = position;
        Width = placement.Width;
        Height = placement.Height;
        _normalPosition = position;
        _normalSize = new Size(placement.Width, placement.Height);
        if (placement.Maximized)
            WindowState = WindowState.Maximized;
    }

    /// <summary>Where the window is now, for the next session.</summary>
    public UiSettings.WindowPlacement Placement()
    {
        RememberNormalBounds();
        return new UiSettings.WindowPlacement
        {
            X = _normalPosition.X,
            Y = _normalPosition.Y,
            Width = _normalSize.Width,
            Height = _normalSize.Height,
            Maximized = WindowState == WindowState.Maximized,
        };
    }

    /// <summary>
    /// The main window's shortcuts work from here too (Ctrl+S, Ctrl+Z, Ctrl+W, Ctrl+P, Ctrl+Shift+O …): they run as if
    /// pressed there. One that opens something in the main window (the palette, a dialog) brings that window forward
    /// so the user sees it.
    /// </summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (!e.Handled && Owner is MainWindow owner && DataContext is MainViewModel vm)
        {
            var overlay = IsOverlayOpen(vm);
            if (owner.TryRunShortcut(e))
            {
                if (!overlay && IsOverlayOpen(vm))
                    owner.Activate();
                e.Handled = true;
                return;
            }
        }
        base.OnKeyDown(e);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        // Letting go of Ctrl ends a Ctrl+Tab walk started here.
        if (DataContext is MainViewModel vm && (e.Key is Key.LeftCtrl or Key.RightCtrl || !e.KeyModifiers.HasFlag(KeyModifiers.Control)))
            vm.EndTabCycle();
        base.OnKeyUp(e);
    }

    private static bool IsOverlayOpen(MainViewModel vm) =>
        vm.IsPaletteOpen || vm.IsRenameOpen || vm.IsConfirmOpen || vm.Compare is not null || vm.Table is not null;
}
