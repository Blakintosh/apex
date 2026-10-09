using System;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Apex.Editor.Commands;
using Apex.Editor.Controls;
using Apex.Editor.ViewModels;

namespace Apex.Editor.Views;

public partial class MainWindow : Window, IShellView
{
    private PreviewWindow? _previewWindow;
    private bool _closingApp;
    private TabStripPanel? _tabPanel;
    private bool _applyingLayout;

    public MainWindow()
    {
        InitializeComponent();
        UpdateButton.PropertyChanged += UpdateButton_PropertyChanged;
        if (OperatingSystem.IsMacOS())
        {
            // Leave room for the macOS traffic lights in the extended client area. (Key hints need no swap: the
            // command catalog writes every gesture the platform's way.)
            TopBarLeft.Margin = new Thickness(78, 0, 0, 0);
        }
        else
        {
            // Avalonia's drawn decorations render their own title text and caption buttons over the
            // custom title bar — keep only the border and draw Fluent 46 px caption buttons ourselves.
            WindowDecorations = WindowDecorations.BorderOnly;
            CaptionButtons.IsVisible = true;
        }

        // Fluent Mica behind the title bar and gutters; it degrades to the solid window background
        // on Windows 10 and elsewhere.
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Mica, WindowTransparencyLevel.None };
        TransparencyBackgroundFallback = null;

        DataContextChanged += (_, _) =>
        {
            if (DataContext is not MainViewModel vm)
                return;
            vm.PropertyChanged += Vm_PropertyChanged;
            vm.EditorFocusRequested += FocusEditor;
            vm.Shell = this;
            RestorePaneSizes(vm);
            ApplyLayout();
        };

        TabList.ContainerPrepared += (_, _) => HookTabPanel();
        // Alt+Tab away mid Ctrl+Tab walk: the Ctrl release happens in another window.
        Deactivated += (_, _) => Vm?.EndTabCycle();
        // Each splitter saves only the size it sets. The others may be fitted to a narrow window right now (ApplyLayout);
        // saving those would make a small window shrink the layout for good.
        ExplorerSplitter.DragCompleted += (_, _) => SavePaneSize(ExplorerSplitter);
        RightSplitter.DragCompleted += (_, _) => SavePaneSize(RightSplitter);
        PreviewSplitter.DragCompleted += (_, _) => SavePaneSize(PreviewSplitter);
        // Saved pane widths come from whatever window they were dragged in: refit them whenever the room changes.
        Workspace.SizeChanged += (_, e) =>
        {
            if (e.WidthChanged)
                ApplyLayout();
        };
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    // Grid definitions can't carry x:Name, so the columns and the right column's rows are addressed by position: the
    // sidebar is the workspace's first column; the editor, the hairline and the right column are the card's.
    private ColumnDefinition ExplorerColumn => Workspace.ColumnDefinitions[0];
    private ColumnDefinition EditorColumn => CardBody.ColumnDefinitions[0];
    private ColumnDefinition RightGutter => CardBody.ColumnDefinitions[1];
    private ColumnDefinition RightColumn => CardBody.ColumnDefinitions[2];
    private RowDefinition PreviewRow => RightStack.RowDefinitions[0];
    private RowDefinition PreviewGutter => RightStack.RowDefinitions[1];
    private RowDefinition InspectorRow => RightStack.RowDefinitions[2];

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        UpdateMica();
        // The preview was popped out when the app last closed: open its window again.
        SyncPreviewWindow();
        // Nothing else in the window does anything until the install is found: start on the one action there is.
        if (Vm is { IsInstallMissing: true })
            Dispatcher.UIThread.Post(() => LocateButton.Focus(NavigationMethod.Tab), DispatcherPriority.Loaded);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ActualTransparencyLevelProperty)
            UpdateMica();
        if (change.Property == WindowStateProperty && CaptionButtons.Children[1] is Button max)
        {
            var maximized = WindowState == WindowState.Maximized;
            max.Content = maximized ? "❐" : "▢";
            ToolTip.SetTip(max, maximized ? "Restore down" : "Maximize");
            AutomationProperties.SetName(max, maximized ? "Restore down" : "Maximize");
        }
    }

    /// <summary>With Mica active the window paints a translucent tint over it instead of a solid colour.</summary>
    private void UpdateMica()
    {
        var mica = ActualTransparencyLevel == WindowTransparencyLevel.Mica;
        if (mica)
        {
            Background = Avalonia.Media.Brushes.Transparent;
            MicaTint[!Border.BackgroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("MicaTintBrush");
        }
        else
        {
            this[!BackgroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("BgWindowBrush");
            MicaTint.Background = Avalonia.Media.Brushes.Transparent;
        }
    }

    private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.IsPaletteOpen):
            case nameof(MainViewModel.IsModalOpen):
                TrackOverlayFocus();
                break;
        }
        switch (e.PropertyName)
        {
            // Focus the Quick Open box the moment the palette appears.
            case nameof(MainViewModel.IsPaletteOpen) when Vm?.IsPaletteOpen == true:
                Dispatcher.UIThread.Post(() =>
                {
                    PaletteBox.Focus();
                    // Keep a typed prefix (">" or "@") and put the caret after it.
                    if ((PaletteBox.Text ?? "").Length <= 1)
                        PaletteBox.CaretIndex = PaletteBox.Text?.Length ?? 0;
                    else
                        PaletteBox.SelectAll();
                });
                break;
            // Confirmation dialogs open with Cancel focused: the safe choice should be the one a
            // reflexive Enter or Space lands on.
            case nameof(MainViewModel.IsConfirmOpen) when Vm?.IsConfirmOpen == true:
                Dispatcher.UIThread.Post(() => ConfirmCancelButton.Focus());
                break;
            // The save conflict dialog opens on Cancel too: Save writes over another program's change.
            case nameof(MainViewModel.IsConflictOpen) when Vm?.IsConflictOpen == true:
                Dispatcher.UIThread.Post(() => ConflictCancelButton.Focus());
                break;
            case nameof(MainViewModel.IsRenameOpen) when Vm?.IsRenameOpen == true:
                Dispatcher.UIThread.Post(() =>
                {
                    RenameBox.Focus();
                    RenameBox.SelectAll();
                });
                break;
            case nameof(MainViewModel.IsNewOpen) when Vm?.IsNewOpen == true:
                Dispatcher.UIThread.Post(() => NewNameBox.Focus());
                break;
            case nameof(MainViewModel.ActiveTab):
                _tabPanel?.InvalidateMeasure();
                ApplyLayout();
                break;
            case nameof(MainViewModel.ShowExplorer):
            case nameof(MainViewModel.ShowInspector):
            case nameof(MainViewModel.Layout):
            case nameof(MainViewModel.HasPreview):
            case nameof(MainViewModel.HasRecoilPreview):
                ApplyLayout();
                break;
            case nameof(MainViewModel.IsPreviewFloating):
                SyncPreviewWindow();
                ApplyLayout();
                break;
        }
    }

    // ── Overlays: focus comes back, shortcuts wait ──────────────────────────
    // The palette and the dialogs take focus when they open. Whatever had it before gets it back when
    // the last one closes, so Esc out of Ctrl+P lands where the user was typing. A chain of overlays
    // (a palette command that opens a dialog) restores to where the chain began.

    private IInputElement? _focusBeforeOverlay;
    private bool _overlayWasOpen;

    private bool OverlayOpen => Vm is { } vm && (vm.IsPaletteOpen || vm.IsModalOpen);

    private void TrackOverlayFocus()
    {
        var open = OverlayOpen;
        if (open == _overlayWasOpen)
            return;
        _overlayWasOpen = open;
        if (open)
        {
            _focusBeforeOverlay ??= FocusManager?.GetFocusedElement();
            return;
        }
        Dispatcher.UIThread.Post(() =>
        {
            if (OverlayOpen)
                return; // another overlay opened in the meantime; it restores when it closes
            var target = _focusBeforeOverlay;
            _focusBeforeOverlay = null;
            if (target is Visual { IsEffectivelyVisible: true } v && v.IsAttachedToVisualTree() && target.IsEffectivelyEnabled)
                target.Focus();
        });
    }

    /// <summary>After Quick Open opens an asset, the keyboard goes to its first field.</summary>
    private void FocusEditor()
    {
        _focusBeforeOverlay = null;
        Dispatcher.UIThread.Post(() =>
        {
            // An xanim's fields are its properties panel's.
            Visual? form = Vm is { IsAnimLayout: true } ? AnimProperties
                : this.GetVisualDescendants().OfType<AssetEditorView>().FirstOrDefault()?.FindControl<ItemsControl>("Form");
            var field = form?.GetVisualDescendants().OfType<InputElement>()
                .FirstOrDefault(c => c is ScrubNumberBox or TextBox or ComboBox or ToggleButton
                    && c.Focusable && c.IsEffectivelyVisible && c.IsEffectivelyEnabled);
            field?.Focus(NavigationMethod.Tab);
        }, DispatcherPriority.Loaded);
    }

    // ── Layout ───────────────────────────────────────────────────────────────
    // Column/row sizes are owned here rather than bound: GridSplitters write widths straight onto
    // the definitions, and a binding would fight them. Visible sizes are saved on every drag and
    // restored when a pane comes back.

    private const double ExplorerMin = 220, EditorMin = 420, RightMin = 280;
    // The gutter between the sidebar and the card; the card's right margin and its two borders; the collapsed sidebar,
    // which keeps its verbs in a narrow column.
    private const double SidebarGutter = 6, CardChrome = 10, CollapsedSidebar = 44;

    private void RestorePaneSizes(MainViewModel vm)
    {
        var s = vm.Settings;
        ExplorerColumn.Width = new GridLength(Math.Clamp(s.ExplorerWidth, ExplorerMin, 600));
        RightColumn.Width = new GridLength(Math.Clamp(s.RightColumnWidth, RightMin, 900));
        PreviewRow.Height = new GridLength(Math.Clamp(s.PreviewHeight, 120, 1200));
    }

    /// <summary>The size the user just dragged with <paramref name="splitter"/> becomes the saved preference.</summary>
    private void SavePaneSize(GridSplitter splitter)
    {
        if (Vm is not { } vm || _applyingLayout)
            return;
        var s = vm.Settings;
        if (splitter == ExplorerSplitter && vm.ShowExplorer && ExplorerColumn.Width.IsAbsolute)
            s.ExplorerWidth = ExplorerColumn.Width.Value;
        else if (splitter == RightSplitter && vm.IsEditLayout && RightColumn.Width.IsAbsolute)
            s.RightColumnWidth = RightColumn.Width.Value;
        else if (splitter == PreviewSplitter && vm.IsEditLayout && PreviewRow.Height.IsAbsolute && vm.HasPreview)
            s.PreviewHeight = PreviewRow.Height.Value;
        else
            return;
        vm.SaveSettings();
    }

    private void ApplyLayout()
    {
        if (Vm is not { } vm)
            return;
        _applyingLayout = true;
        try
        {
            var s = vm.Settings;
            var hasAsset = vm.HasActiveTab;
            // An xanim: its preview is in the editor, so the right column is its properties panel alone (the Inspector's
            // toggle and the preview layout's tuck-away apply to it), and maximizing the preview gives the editor the room.
            var anim = vm.IsAnimLayout;

            // Right column: nothing open → the start page takes the room and the Inspector steps aside.
            var showRight = hasAsset && (anim ? vm.ShowInspector : vm.ShowInspector || vm.ShowDockedPreview);
            var previewLayout = vm.IsPreviewLayout && hasAsset && !anim;

            // The saved widths, shrunk (right column first, then the Explorer) until the editor keeps its minimum: the
            // columns never add up to more than the window, which would push the right edge of every pane off it.
            var explorerWidth = Math.Clamp(s.ExplorerWidth, ExplorerMin, 600);
            var rightWidth = previewLayout ? RightMin : Math.Clamp(s.RightColumnWidth, RightMin, 900);
            var room = Workspace.Bounds.Width;
            if (room > 0)
            {
                var over = (vm.ShowExplorer ? explorerWidth : CollapsedSidebar) + SidebarGutter + CardChrome + EditorMin
                           + (showRight ? rightWidth + 1 : 0) - room;
                if (over > 0 && showRight && !previewLayout)
                {
                    var take = Math.Min(over, rightWidth - RightMin);
                    rightWidth -= take;
                    over -= take;
                }
                if (over > 0 && vm.ShowExplorer)
                    explorerWidth -= Math.Min(over, explorerWidth - ExplorerMin);
            }

            // Explorer: the sidebar, or (hidden) a narrow column of its verbs over a strip that brings it back.
            ExplorerLayer.IsVisible = vm.ShowExplorer;
            ExplorerStrip.IsVisible = !vm.ShowExplorer;
            ExplorerSplitter.IsVisible = vm.ShowExplorer;
            ExplorerColumn.MinWidth = vm.ShowExplorer ? ExplorerMin : 0;
            ExplorerColumn.Width = vm.ShowExplorer ? new GridLength(explorerWidth) : new GridLength(CollapsedSidebar);
            SidebarTitle.IsVisible = vm.ShowExplorer;
            SidebarFoot.IsVisible = vm.ShowExplorer;
            SidebarActions.Orientation = vm.ShowExplorer ? Avalonia.Layout.Orientation.Horizontal : Avalonia.Layout.Orientation.Vertical;
            DockPanel.SetDock(SidebarActions, vm.ShowExplorer ? Dock.Right : Dock.Bottom);
            TopBarLeft.Height = vm.ShowExplorer ? 44 : double.NaN;
            TopBarLeft.Margin = vm.ShowExplorer ? new Thickness(OperatingSystem.IsMacOS() ? 78 : 14, 0, 6, 0) : new Thickness(9, 12, 9, 8);
            // The mark's button padding (Button.appmark, 6,4) is taken back here, so the mark sits where it would bare.
            DockPanel.SetDock(AppMenuButton, vm.ShowExplorer ? Dock.Left : Dock.Top);
            AppMenuButton.HorizontalAlignment = vm.ShowExplorer ? Avalonia.Layout.HorizontalAlignment.Left : Avalonia.Layout.HorizontalAlignment.Center;
            AppMenuButton.Margin = vm.ShowExplorer ? new Thickness(-6, -4) : new Thickness(-6, -4, -6, 6);

            RightStack.IsVisible = showRight;
            RightSplitter.IsVisible = showRight;
            RightGutter.Width = new GridLength(showRight ? 1 : 0);

            if (previewLayout)
            {
                // Preview layout: the editor keeps a working width beside a preview that fills the rest (both give way
                // to a narrow window, down to their minimums).
                var editorWidth = 760.0;
                if (room > 0)
                    editorWidth = Math.Clamp(room - (vm.ShowExplorer ? explorerWidth : CollapsedSidebar) - SidebarGutter - CardChrome
                                             - (showRight ? RightMin + 1 : 0), EditorMin, 760);
                EditorColumn.Width = new GridLength(editorWidth);
                EditorColumn.MinWidth = EditorMin;
                RightColumn.MinWidth = RightMin;
                RightColumn.Width = new GridLength(1, GridUnitType.Star);
            }
            else
            {
                EditorColumn.Width = new GridLength(1, GridUnitType.Star);
                EditorColumn.MinWidth = EditorMin;
                RightColumn.MinWidth = showRight ? RightMin : 0;
                RightColumn.Width = showRight ? new GridLength(rightWidth) : new GridLength(0);
            }

            PropertiesLayer.IsVisible = anim;

            // Preview over Inspector. The preview shrinks to its header when there is nothing to render.
            var docked = vm.ShowDockedPreview && !anim;
            // A recoil preview takes the column's full height, as an xanim's preview takes the editor's: degrees of kick
            // can't be read in a strip. The Inspector steps aside until the preview goes or pops out (⧉).
            var inspector = vm.ShowInspector && !anim && !(docked && vm.HasRecoilPreview);
            PreviewLayer.IsVisible = docked;
            InspectorLayer.IsVisible = inspector;
            PreviewSplitter.IsVisible = docked && inspector && vm.HasPreview;
            PreviewGutter.Height = new GridLength(docked && inspector ? 1 : 0);
            if (!docked)
            {
                PreviewRow.Height = new GridLength(0);
                InspectorRow.Height = new GridLength(1, GridUnitType.Star);
            }
            else if (!inspector)
            {
                PreviewRow.Height = new GridLength(1, GridUnitType.Star);
                InspectorRow.Height = new GridLength(0);
            }
            else if (!vm.HasPreview)
            {
                PreviewRow.Height = new GridLength(36);
                InspectorRow.Height = new GridLength(1, GridUnitType.Star);
            }
            else
            {
                PreviewRow.Height = new GridLength(Math.Clamp(s.PreviewHeight, 120, 1200));
                InspectorRow.Height = new GridLength(1, GridUnitType.Star);
            }
        }
        finally
        {
            _applyingLayout = false;
        }
    }

    private void Alert_PointerEntered(object? sender, PointerEventArgs e) => ((sender as Control)?.DataContext as AlertItem)?.Hold(true);

    private void Alert_PointerExited(object? sender, PointerEventArgs e) => ((sender as Control)?.DataContext as AlertItem)?.Hold(false);

    private void Alert_GotFocus(object? sender, FocusChangedEventArgs e) => ((sender as Control)?.DataContext as AlertItem)?.Hold(true);

    private void Alert_LostFocus(object? sender, FocusChangedEventArgs e)
    {
        // Focus moving between the banner's own buttons keeps it held.
        if (sender is Control banner && TopLevel.GetTopLevel(banner)?.FocusManager?.GetFocusedElement() is Visual now && banner.IsVisualAncestorOf(now))
            return;
        ((sender as Control)?.DataContext as AlertItem)?.Hold(false);
    }

    // ── Floating preview ─────────────────────────────────────────────────────

    private void SyncPreviewWindow()
    {
        if (Vm is not { } vm)
            return;
        if (vm.IsPreviewFloating && _previewWindow is null)
        {
            // Not before the main window is up: an owned window needs a shown owner. OnOpened calls back.
            if (!IsVisible)
                return;
            var window = _previewWindow = new PreviewWindow { DataContext = vm };
            window.Restore(vm.Settings.PreviewWindow);
            window.Closing += (_, _) => vm.Settings.PreviewWindow = window.Placement();
            window.Closed += (_, _) =>
            {
                _previewWindow = null;
                // Closing the app closes this window too; it must open again next time.
                if (!_closingApp && Vm is { IsPreviewFloating: true } v)
                    v.IsPreviewFloating = false;
                else
                    vm.SaveSettings();
            };
            window.Show(this);
        }
        else if (!vm.IsPreviewFloating && _previewWindow is { } w)
        {
            _previewWindow = null;
            w.Close();
        }
    }

    // ── Tab strip overflow ───────────────────────────────────────────────────

    private void HookTabPanel()
    {
        if (_tabPanel is not null)
            return;
        _tabPanel = TabList.ItemsPanelRoot as TabStripPanel;
        if (_tabPanel is null)
            return;
        _tabPanel.OverflowChanged += (before, after) =>
        {
            TabOverflowButton.IsVisible = before + after > 0;
            // Which side the hidden tabs are on, as the strip would scroll to them.
            TabOverflowText.Text = (before, after) switch
            {
                (0, _) => $"{after} more ›",
                (_, 0) => $"‹ {before} more",
                _ => $"‹ {before} · {after} ›",
            };
            AutomationProperties.SetName(TabOverflowButton, (before, after) switch
            {
                (0, _) => $"{TabsText(after)} hidden to the right",
                (_, 0) => $"{TabsText(before)} hidden to the left",
                _ => $"{TabsText(before)} hidden to the left, {after} to the right",
            });
        };

        static string TabsText(int n) => n == 1 ? "1 tab" : $"{n} tabs";
    }

    private void TabOverflow_Click(object? sender, RoutedEventArgs e) => ShowAllTabsAt(TabOverflowButton);

    /// <summary>
    /// Every open tab in strip order, the overflow button's and Show all tabs' list: the ones hidden to the left, the ones
    /// in view, the ones hidden to the right, each group set apart, so the list reads like the strip it stands for.
    /// </summary>
    private void ShowAllTabsAt(Control anchor)
    {
        if (Vm is not { } vm)
            return;
        var (before, after) = _tabPanel?.HiddenSides ?? (0, 0);
        var count = vm.OpenTabs.Count;
        var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        for (var i = 0; i < count; i++)
        {
            var tab = vm.OpenTabs[i];
            if ((i == before && before > 0) || (i == count - after && after > 0 && i > 0))
                menu.Items.Add(new Separator());
            var side = i < before ? "‹" : i >= count - after ? "›" : "";
            var header = new DockPanel();
            var mark = new TextBlock { Text = side, Margin = new Thickness(12, 0, 0, 0) };
            mark.Classes.Add("faint");
            DockPanel.SetDock(mark, Avalonia.Controls.Dock.Right);
            header.Children.Add(mark);
            var glyph = new TextBlock { Text = tab.Glyph, Foreground = tab.TypeBrush, Width = 16 };
            DockPanel.SetDock(glyph, Avalonia.Controls.Dock.Left);
            header.Children.Add(glyph);
            header.Children.Add(new TextBlock { Text = tab.Name, Margin = new Thickness(8, 0, 0, 0) });
            menu.Items.Add(new MenuItem
            {
                Header = header,
                Command = vm.ActivateTabCommand,
                CommandParameter = tab,
                ToggleType = MenuItemToggleType.Radio,
                IsChecked = tab == vm.ActiveTab,
            });
        }
        // Attached, so what is open can be found from the control it opened at.
        FlyoutBase.SetAttachedFlyout(anchor, menu);
        FlyoutBase.ShowAttachedFlyout(anchor);
    }

    private void Tab_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        // Middle-click closes, as in every tabbed editor.
        if (e.InitialPressMouseButton == MouseButton.Middle && (sender as Control)?.DataContext is AssetEditorViewModel tab)
        {
            tab.CloseCommand.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>Closing keeps the session (journaled); only when it can't be kept does the first close go to the discard dialog.</summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (Vm is { } vm && !vm.RequestClose(Close))
        {
            e.Cancel = true;
            return;
        }
        // No pane sizes here: they were saved as they were dragged, and what is on screen may be fitted to this window.
        _closingApp = true;
        _previewWindow?.Close();
        Vm?.FlushSettings();
        base.OnClosing(e);
    }

    private void ConfirmBackdrop_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Clicking away cancels — never confirms.
        Vm?.CancelConfirmCommand.Execute(null);
        e.Handled = true;
    }

    private void RenameBackdrop_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        Vm?.CancelRenameCommand.Execute(null);
        e.Handled = true;
    }

    private void NewBackdrop_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        Vm?.CancelNewCommand.Execute(null);
        e.Handled = true;
    }

    private void Minimize_Click(object? sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void Maximize_Click(object? sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseButton_Click(object? sender, RoutedEventArgs e) => Close();

    private void TopBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Only the empty title bar drags the window; buttons and the command box handle their own presses.
        if (e.Source is Visual v && v.FindAncestorOfType<Button>(includeSelf: true) is not null)
            return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
            return;
        }
        BeginMoveDrag(e);
    }

    private void PaletteBackdrop_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        Vm?.ClosePaletteCommand.Execute(null);
        e.Handled = true;
    }

    private void PaletteCard_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Keep clicks inside the card from reaching the dismissing backdrop.
        e.Handled = true;
    }

    private void PaletteList_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        // Only a left click on a row opens it: a right click, or letting go of the scrollbar, must not.
        if (e.InitialPressMouseButton != MouseButton.Left)
            return;
        if ((e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true) is not { DataContext: PaletteRow { Item: { } item } })
            return;
        Vm?.ConfirmPaletteItem(item);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        // The mouse's side buttons are Back and Forward, as in a browser; a menu or overlay in the way keeps them.
        var kind = e.GetCurrentPoint(this).Properties.PointerUpdateKind;
        if (kind is PointerUpdateKind.XButton1Pressed or PointerUpdateKind.XButton2Pressed
            && Vm is { } vm && !vm.IsAnyLayerOpen)
        {
            var go = kind == PointerUpdateKind.XButton1Pressed ? vm.GoBackCommand : vm.GoForwardCommand;
            if (go.CanExecute(null))
                go.Execute(null);
            e.Handled = true;
            return;
        }
        base.OnPointerPressed(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // Escape unwinds exactly one layer, so it always means "back out of this" rather than
        // "close something, possibly not the thing you were looking at".
        if (e.Key == Key.Escape && Vm?.DismissTopLayer() == true)
        {
            e.Handled = true;
            return;
        }
        if (TryRunShortcut(e))
        {
            e.Handled = true;
            return;
        }
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control))
            Vm?.EndTabCycle();
        base.OnKeyDown(e);
    }

    /// <summary>
    /// Runs the window command <paramref name="e"/> is the key for, if it applies now. Every window shortcut goes
    /// through here (the popped-out preview forwards its keys too), after the focused control has had its chance:
    /// a key a control uses itself (F2 starting an edit in a number box, Ctrl+Z in a text box with its own undo)
    /// never gets this far.
    /// </summary>
    public bool TryRunShortcut(KeyEventArgs e)
    {
        // A dialog holds the keyboard: Ctrl+W must not close the tab being deleted, Ctrl+N must not start a new
        // asset behind the discard prompt.
        if (Vm is not { IsModalOpen: false } vm || CommandCatalog.Match(CommandScope.Window, e) is not { } info)
            return false;
        if (CommandCatalog.IsTyping(info, e))
            return false;
        var command = vm.Registry[info.Id];
        if (!command.CanRun)
            return info.Id is CommandCatalog.NextTab or CommandCatalog.PreviousTab; // Ctrl+Tab never moves focus instead
        // A command other than the palette's own takes the palette down first, as it would with a click elsewhere.
        if (vm.IsPaletteOpen && info.Category != "Go")
            vm.ClosePaletteCommand.Execute(null);
        command.Execute();
        return true;
    }

    // ── No install found: Locate… ───────────────────────────────────────────

    /// <summary>The folder picker Locate… opens; null when it was cancelled. Replaceable so the harness can answer it.</summary>
    public static Func<TopLevel, System.Threading.Tasks.Task<string?>> PickInstallFolder { get; set; } = DefaultPickInstallFolderAsync;

    private static async System.Threading.Tasks.Task<string?> DefaultPickInstallFolderAsync(TopLevel top)
    {
        var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose the Black Ops III folder",
            AllowMultiple = false,
        });
        return folders.Count == 0 ? null : folders[0].TryGetLocalPath();
    }

    private async void Locate_Click(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm)
            return;
        string? folder;
        try { folder = await PickInstallFolder(this); }
        catch { folder = null; } // a picker the platform couldn't open: nothing was chosen
        // Found: the window is the app again, and the keyboard starts in the Explorer's search like any launch would.
        if (folder is not null && vm.TryUseInstall(folder))
            FocusAssetSearch();
    }

    // ── Updates ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The app menu's Updates status line names the update the update control holds; clicking it opens the control's
    /// flyout (the menu has closed by then).
    /// </summary>
    private void UpdateStatus_Click(object? sender, RoutedEventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        if (UpdateButton is { IsEffectivelyVisible: true, Flyout: { IsOpen: false } flyout })
            flyout.ShowAt(UpdateButton);
    }, DispatcherPriority.Loaded);

    /// <summary>
    /// Check now found a newer release: the update control appears in the top row, so the app menu gets out of its
    /// way rather than turning into a download under the cursor.
    /// </summary>
    private void UpdateButton_PropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == IsVisibleProperty && e.GetNewValue<bool>() && UpdatesMenu.IsSubMenuOpen
            && AppMenuButton.Flyout is { IsOpen: true } menu)
            menu.Hide();
    }

    private void UpdateFlyout_Closed(object? sender, EventArgs e) => Vm?.Updates.Dismiss();

    /// <summary>About Apex, from the app menu or the palette: a small flyout under the mark (the menu or palette has closed by then).</summary>
    public void ShowAbout() => Dispatcher.UIThread.Post(() =>
    {
        if (AppMenuButton.IsEffectivelyVisible && FlyoutBase.GetAttachedFlyout(AppMenuButton) is { IsOpen: false } about)
            about.ShowAt(AppMenuButton);
    }, DispatcherPriority.Loaded);

    // ── IShellView: what commands need the window for ───────────────────────

    public void FocusAssetSearch() =>
        // After the layout the command may have just changed (showing a hidden Explorer).
        Dispatcher.UIThread.Post(() => Browser.FocusSearch(), DispatcherPriority.Loaded);

    public void FocusPropertyFilter()
    {
        // An xanim's properties are the panel on the right (brought back if it was hidden).
        if (Vm is { IsAnimLayout: true } vm)
        {
            if (!vm.ShowInspector)
                vm.ToggleInspectorCommand.Execute(null);
            Dispatcher.UIThread.Post(() => AnimProperties.FocusFilter(), DispatcherPriority.Loaded);
            return;
        }
        this.GetVisualDescendants().OfType<AssetEditorView>().FirstOrDefault(v => v.IsEffectivelyVisible)?.FocusFilter();
    }

    public void OpenAddProperty() =>
        this.GetVisualDescendants().OfType<AssetEditorView>().FirstOrDefault(v => v.IsEffectivelyVisible)?.OpenAddProperty();

    public void ShowAllTabs() => ShowAllTabsAt(TabOverflowButton.IsVisible ? TabOverflowButton : TabList);

    void IShellView.CyclePaneFocus(int delta) => CyclePaneFocus(delta);

    public void FramePreview()
    {
        // The shown preview: docked here, or in its own window.
        Visual root = _previewWindow is { } floating ? floating : Vm is { IsAnimLayout: true } ? EditorLayer : PreviewLayer;
        foreach (var d in root.GetVisualDescendants())
        {
            switch (d)
            {
                case ToolsGfxPreviewViewport { IsEffectivelyVisible: true } gfx:
                    gfx.Frame();
                    return;
                case GlPreviewViewport { IsEffectivelyVisible: true } gl:
                    gl.Frame();
                    return;
            }
        }
    }

    public void CopyText(string text) => _ = CopyTextAsync(text);

    public async System.Threading.Tasks.Task CopyTextAsync(string text)
    {
        try
        {
            if (Clipboard is { } clipboard)
                await clipboard.SetTextAsync(text);
        }
        catch (Exception) { /* another program holds the clipboard open: the copy just doesn't reach it */ }
    }

    public async System.Threading.Tasks.Task<(bool Read, string? Text)> GetClipboardTextAsync()
    {
        try { return Clipboard is { } clipboard ? (true, await clipboard.TryGetTextAsync()) : (false, null); }
        catch (Exception) { return (false, null); } // another program holds the clipboard open
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        // Letting go of Ctrl ends a Ctrl+Tab walk.
        if (e.Key is Key.LeftCtrl or Key.RightCtrl || !e.KeyModifiers.HasFlag(KeyModifiers.Control))
            Vm?.EndTabCycle();
        base.OnKeyUp(e);
    }

    /// <summary>F6 / Shift+F6 move keyboard focus between the visible panes.</summary>
    private void CyclePaneFocus(int delta)
    {
        var panes = new Control[] { ExplorerLayer, TabList.Parent!.Parent as Control ?? TabList, PreviewLayer, InspectorLayer, PropertiesLayer }
            .Where(p => p.IsEffectivelyVisible)
            .ToList();
        if (panes.Count == 0)
            return;
        var focused = FocusManager?.GetFocusedElement() as Visual;
        var current = panes.FindIndex(p => focused is not null && (focused == p || p.IsVisualAncestorOf(focused)));
        var next = panes[(current + delta + panes.Count) % panes.Count];
        if (next == ExplorerLayer)
        {
            Browser.FocusTree();
            return;
        }
        var target = next.GetVisualDescendants().OfType<InputElement>().FirstOrDefault(c => c.Focusable && c.IsEffectivelyVisible && c.IsEffectivelyEnabled);
        target?.Focus(NavigationMethod.Tab);
    }
}
