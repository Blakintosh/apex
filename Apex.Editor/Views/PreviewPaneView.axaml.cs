using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Apex.Editor.Commands;
using Apex.Editor.Controls;
using Apex.Editor.Services.Preview.ToolsGfx;
using Apex.Editor.ViewModels;

namespace Apex.Editor.Views;

public partial class PreviewPaneView : UserControl
{
    /// <summary>
    /// The viewport alone, edge to edge: an xanim's editor hosts it above its notetracks dock, which carries the
    /// transport, so the pane drops its own transport rows and margins (and offers the pop-out on the render).
    /// </summary>
    public static readonly StyledProperty<bool> CompactProperty =
        AvaloniaProperty.Register<PreviewPaneView, bool>(nameof(Compact));

    public PreviewPaneView() => InitializeComponent();

    public bool Compact
    {
        get => GetValue(CompactProperty);
        set => SetValue(CompactProperty, value);
    }

    /// <summary>The ⧉ chip on the render: the window's own Preview in its own window command.</summary>
    private void PopOut_Click(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.DataContext is MainViewModel vm)
            vm.Registry[CommandCatalog.PreviewWindow].Execute();
    }

    // ── ToolsGfx viewport → OpenGL fallback ─────────────────────────────────

    private void GfxViewport_Failed(object? sender, ToolsGfxViewportFailure e)
    {
        if (e.InteropUnavailable)
            ToolsGfxPreviewService.InteropUnavailableReason ??= e.Reason;
        switch ((sender as Control)?.DataContext)
        {
            case ModelPreviewViewModel model:
                model.FallBackToGl(e.Reason);
                break;
            case MaterialPreviewViewModel material:
                material.FallBackToGl(e.Reason);
                break;
            case AnimPreviewViewModel anim:
                anim.FallBackToGl(e.Reason);
                break;
            case WeaponPreviewViewModel recoil:
                recoil.FallBack(e.Reason);
                break;
        }
    }

    // ── Recoil template hooks: display frames, the held trigger, keys ───────

    // As for anims: the template is recycled across weapons, so the attachment follows the root's DataContext.
    private WeaponPreviewViewModel? _attachedRecoil;
    private TopLevel? _recoilTop;
    private Button? _fireButton;

    // The Fire button's hold already fired: the click that ends it is not another round.
    private bool _fireHeld;

    private void RecoilRoot_Loaded(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control root)
            return;
        root.DataContextChanged += RecoilRoot_DataContextChanged;
        root.PropertyChanged += RecoilRoot_PropertyChanged;
        root.AddHandler(PointerPressedEvent, AnimRoot_PointerPressed, RoutingStrategies.Bubble, handledEventsToo: true);
        root.AddHandler(PointerReleasedEvent, RecoilRoot_PointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        _fireButton = root.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Name == "FireButton");
        if (_fireButton is not null)
            _fireButton.PropertyChanged += FireButton_PropertyChanged;
        AttachRecoil(root);
    }

    private void RecoilRoot_Unloaded(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control root)
            return;
        root.DataContextChanged -= RecoilRoot_DataContextChanged;
        root.PropertyChanged -= RecoilRoot_PropertyChanged;
        root.RemoveHandler(PointerPressedEvent, AnimRoot_PointerPressed);
        root.RemoveHandler(PointerReleasedEvent, RecoilRoot_PointerReleased);
        if (_fireButton is not null)
            _fireButton.PropertyChanged -= FireButton_PropertyChanged;
        _fireButton = null;
        _attachedRecoil?.ReleaseTrigger();
        _attachedRecoil?.ViewDetached(_recoilTop);
        if (_attachedRecoil is not null)
            _attachedRecoil.PropertyChanged -= Recoil_PropertyChanged;
        _attachedRecoil = null;
        _recoilRoot = null;
        _recoilTop = null;
        WatchWindow(null);
    }

    /// <summary>
    /// A click on Hip, ADS or Reset hands the keyboard back to the preview once the click is done, so Space fires next
    /// as it did before the click (A and R are their keys). Reached with Tab they keep focus and take Space themselves.
    /// </summary>
    private void RecoilRoot_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is not Control root || e.InitialPressMouseButton != MouseButton.Left)
            return;
        for (var v = e.Source as Visual; v is not null && !ReferenceEquals(v, root); v = v.GetVisualParent())
        {
            if (v is not Button control)
                continue;
            // Load module… is disabled while it asks, which drops the keyboard: it goes to the preview, so the question
            // it brings up opens on Don't load there.
            if (control.Name is "HipToggle" or "AdsToggle" or "ResetButton" && control.IsFocused || control.Name == "LoadModuleButton")
                root.Focus();
            return;
        }
    }

    // The window the preview is in: losing it (Alt+Tab, a minimise, another window) sends the key's release elsewhere.
    private WindowBase? _recoilWindow;

    private void WatchWindow(TopLevel? top)
    {
        if (_recoilWindow is not null)
            _recoilWindow.Deactivated -= RecoilWindow_Deactivated;
        _recoilWindow = top as WindowBase;
        if (_recoilWindow is not null)
            _recoilWindow.Deactivated += RecoilWindow_Deactivated;
    }

    private void RecoilWindow_Deactivated(object? sender, EventArgs e) => _attachedRecoil?.ReleaseTrigger();

    private void RecoilRoot_DataContextChanged(object? sender, EventArgs e)
    {
        if (sender is Control { IsLoaded: true } root)
            AttachRecoil(root);
    }

    private void AttachRecoil(Control root)
    {
        var vm = root.DataContext as WeaponPreviewViewModel;
        if (ReferenceEquals(vm, _attachedRecoil))
            return;
        _attachedRecoil?.ReleaseTrigger();
        _attachedRecoil?.ViewDetached(_recoilTop);
        if (_attachedRecoil is not null)
            _attachedRecoil.PropertyChanged -= Recoil_PropertyChanged;
        _attachedRecoil = vm;
        _recoilRoot = vm is null ? null : root;
        _recoilTop = TopLevel.GetTopLevel(root);
        WatchWindow(_recoilTop);
        if (vm is not null)
            vm.PropertyChanged += Recoil_PropertyChanged;
        vm?.ViewAttached(_recoilTop);
    }

    private Control? _recoilRoot;

    /// <summary>
    /// The module's question appearing never takes the keyboard from where the user is working; when the keyboard is
    /// already in the preview it lands on Don't load, the safe answer.
    /// </summary>
    private void Recoil_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(WeaponPreviewViewModel.Question) || _recoilRoot is not { } root
            || sender is not WeaponPreviewViewModel { Question: not null } vm)
            return;
        bool KeyboardHere() => root.IsKeyboardFocusWithin
            || _keyboardDroppedHere && TopLevel.GetTopLevel(root)?.FocusManager?.GetFocusedElement() is null;
        if (!KeyboardHere())
            return;
        Dispatcher.UIThread.Post(() =>
        {
            if (vm.Question is not null && KeyboardHere()
                && root.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Name == "DontLoadButton") is { IsEffectivelyVisible: true } dontLoad)
                dontLoad.Focus(NavigationMethod.Tab);
        }, DispatcherPriority.Loaded);
    }

    /// <summary>Load or Don't load: the card goes, and the keyboard stays in the preview (once the answer has run), so Space fires next.</summary>
    private void ModuleQuestionButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_recoilRoot is { } root)
            Dispatcher.UIThread.Post(() => root.Focus(), DispatcherPriority.Input);
    }

    /// <summary>Keyboard focus left the preview while Space was down: the key's release won't come here, so let go now.</summary>
    private void RecoilRoot_PropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != IsKeyboardFocusWithinProperty)
            return;
        if (e.NewValue is not false)
        {
            _keyboardDroppedHere = false;
            return;
        }
        _attachedRecoil?.ReleaseTrigger();
        // A control here that disables itself (Load module… while it asks) drops the keyboard rather than moving it;
        // the focused element is updated after this change, so look once it has.
        if (sender is Control root)
            Dispatcher.UIThread.Post(() => _keyboardDroppedHere = TopLevel.GetTopLevel(root)?.FocusManager?.GetFocusedElement() is null);
    }

    private bool _keyboardDroppedHere;

    /// <summary>The Fire button is the trigger: held by the mouse or by Space while it has focus.</summary>
    private void FireButton_PropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != Button.IsPressedProperty || sender is not Button { DataContext: WeaponPreviewViewModel vm })
            return;
        if (e.NewValue is true)
        {
            _fireHeld = true;
            vm.PressTrigger();
            return;
        }
        vm.ReleaseTrigger();
        // The click that follows a release belongs to the hold; it runs before this.
        Dispatcher.UIThread.Post(() => _fireHeld = false, DispatcherPriority.Background);
    }

    /// <summary>A click with no hold before it (Enter on the focused button) is one round.</summary>
    private void FireButton_Click(object? sender, RoutedEventArgs e)
    {
        if (!_fireHeld && (sender as Control)?.DataContext is WeaponPreviewViewModel vm)
            vm.FireOnce();
    }

    /// <summary>
    /// Space holds the trigger, A switches hip and ADS, R resets; a key a focused control uses itself never gets here.
    /// While the module's question is open, Esc and Enter are its default answer, Don't load.
    /// </summary>
    private void RecoilRoot_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || sender is not Control { DataContext: WeaponPreviewViewModel vm })
            return;
        if (vm.Question is { } question && e.KeyModifiers == KeyModifiers.None && e.Key is Key.Escape or Key.Enter)
        {
            question.DontLoadCommand.Execute(null);
            if (sender is Control root)
                root.Focus();
            e.Handled = true;
            return;
        }
        if (CommandCatalog.Match(CommandScope.RecoilPreview, e) is not { } command || CommandCatalog.IsTyping(command, e))
            return;
        if (command.Id == CommandCatalog.Fire)
        {
            // Held keys repeat: only the first press is the trigger going down.
            if (!vm.Driver.IsTriggerHeld)
                vm.PressTrigger();
        }
        else
        {
            MainViewModel.RunRecoilCommand(command.Id, vm);
        }
        e.Handled = true;
    }

    private void RecoilRoot_KeyUp(object? sender, KeyEventArgs e)
    {
        if (sender is Control { DataContext: WeaponPreviewViewModel vm } && CommandCatalog.Is(CommandCatalog.Fire, e))
            vm.ReleaseTrigger();
    }

    // ── Anim template hooks: playback-clock lifetime + frame-step keys ──────

    // The anim the template root is attached to. The content template is recycled when one anim preview replaces
    // another (the dock shows every preview in this one view), so the root stays loaded and only its DataContext
    // changes: the attachment follows the DataContext, not Loaded.
    private AnimPreviewViewModel? _attachedAnim;

    private void AnimRoot_Loaded(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control root)
            return;
        root.DataContextChanged += AnimRoot_DataContextChanged;
        AttachAnim(root);
        // Focus the template root on any click inside it (the viewport handles pointer events
        // itself, so listen to handled events too) — enables keyboard frame stepping.
        root.AddHandler(PointerPressedEvent, AnimRoot_PointerPressed, RoutingStrategies.Bubble, handledEventsToo: true);
        // A drag on the frame slider is a scrub: the notetrack timeline plays each marker at most once per drag.
        root.AddHandler(PointerPressedEvent, AnimRoot_SliderPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        root.AddHandler(PointerReleasedEvent, AnimRoot_SliderReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        root.AddHandler(PointerCaptureLostEvent, AnimRoot_SliderReleased, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private void AnimRoot_Unloaded(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control root)
            return;
        root.DataContextChanged -= AnimRoot_DataContextChanged;
        root.RemoveHandler(PointerPressedEvent, AnimRoot_PointerPressed);
        root.RemoveHandler(PointerPressedEvent, AnimRoot_SliderPressed);
        root.RemoveHandler(PointerReleasedEvent, AnimRoot_SliderReleased);
        root.RemoveHandler(PointerCaptureLostEvent, AnimRoot_SliderReleased);
        AnimRoot_SliderReleased(root, e);
        _attachedAnim?.ViewDetached(_attachedTop);
        _attachedAnim = null;
        _attachedTop = null;
    }

    // The top level the anim was attached with: by Unloaded the root has already left it.
    private TopLevel? _attachedTop;

    private void AnimRoot_DataContextChanged(object? sender, EventArgs e)
    {
        if (sender is Control { IsLoaded: true } root)
            AttachAnim(root);
    }

    /// <summary>Attaches the root's anim (playback clock, frame source) and detaches the one it replaced.</summary>
    private void AttachAnim(Control root)
    {
        var vm = root.DataContext as AnimPreviewViewModel;
        if (ReferenceEquals(vm, _attachedAnim))
            return;
        _attachedAnim?.ViewDetached(_attachedTop);
        _attachedAnim = vm;
        _attachedTop = TopLevel.GetTopLevel(root);
        vm?.ViewAttached(_attachedTop);
    }

    private void AnimRoot_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Don't steal focus from a control inside the template (buttons, toggles, slider, text boxes): e.Source is the
        // innermost element hit (a toggle's glyph or label), and a button that loses focus mid-press cancels its click.
        if (sender is not Control root)
            return;
        for (var v = e.Source as Visual; v is not null && !ReferenceEquals(v, root); v = v.GetVisualParent())
            if (v is InputElement { Focusable: true })
                return;
        root.Focus();
    }

    private static bool InFrameSlider(object? source)
    {
        for (var v = source as Visual; v is not null; v = v.GetVisualParent())
            if (v is Slider { Name: "FrameSlider" })
                return true;
        return false;
    }

    // The anim a press on the frame slider started scrubbing: any release or lost capture ends it, wherever it lands.
    private AnimPreviewViewModel? _sliderScrub;

    /// <summary>A press on the frame slider is a scrub: playback pauses until the release (the clock would fight the
    /// pointer), and the notetrack timeline plays each marker at most once for it.</summary>
    private void AnimRoot_SliderPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { DataContext: AnimPreviewViewModel vm } && InFrameSlider(e.Source)
            && e.GetCurrentPoint(null).Properties.IsLeftButtonPressed)
        {
            _sliderScrub = vm;
            vm.BeginScrub();
            vm.Timeline.IsScrubbing = true;
        }
    }

    private void AnimRoot_SliderReleased(object? sender, RoutedEventArgs e)
    {
        if (_sliderScrub is not { } vm)
            return;
        _sliderScrub = null;
        vm.Timeline.IsScrubbing = false;
        vm.EndScrub();
    }

    /// <summary>
    /// The anim transport keys, APE's and a player's: Space plays / pauses, , . ← → step a frame (pausing first),
    /// Home / End go to the first / last frame. Keys a focused control uses itself (typing in a box, arrows on the
    /// slider, Space on a toggle) never reach here.
    /// </summary>
    private void AnimRoot_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || sender is not Control { DataContext: AnimPreviewViewModel vm }
            || CommandCatalog.Match(CommandScope.AnimPreview, e) is not { } command || CommandCatalog.IsTyping(command, e))
            return;
        MainViewModel.RunAnimCommand(command.Id, vm);
        e.Handled = true;
    }

    /// <summary>The Frame button's tooltip: the command, then the camera controls (a hover tip on the viewport would cover the model).</summary>
    public static string FrameTip { get; } = CommandCatalog.Get(CommandCatalog.FramePreview).Tip
        + "\nDrag to orbit · right or middle drag to pan · wheel or Alt+right drag to zoom · Shift+drag to move the sun · double-click to frame";

    // ── Lighting chip: APE's Preview > Lighting, one state for every preview ──

    /// <summary>The lighting chip's menu: the four states, the one in use checked. Arrows move, Enter picks, Esc closes.</summary>
    private void LightingChip_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control chip)
            return;
        var lighting = PreviewLighting.Shared;
        var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
        foreach (var option in lighting.LightOptions)
            menu.Items.Add(new MenuItem
            {
                Header = option.Label,
                Command = option.SelectCommand,
                ToggleType = MenuItemToggleType.Radio,
                IsChecked = option.State == lighting.LightState,
            });
        LightingMenu = menu;
        menu.ShowAt(chip);
    }

    /// <summary>The lighting menu last opened (verification reads it).</summary>
    public MenuFlyout? LightingMenu { get; private set; }

    // ── Frame button: back to APE's framing of whichever viewport is showing ──

    private void Frame_Click(object? sender, RoutedEventArgs e)
    {
        // Up from the button to the template root, then down to the visible viewport beside it.
        for (var v = sender as Visual; v is not null && v is not Avalonia.Controls.Presenters.ContentPresenter; v = v.GetVisualParent())
        {
            foreach (var d in v.GetVisualDescendants())
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
    }
}
