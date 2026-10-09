using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Apex.Editor.Services.Preview.ToolsGfx;
using Apex.Render.Assets;
using Apex.Render.Data.Lighting;
using Apex.Render.Presentation;
using Apex.Render.Scene;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

namespace Apex.Editor.Controls;

/// <summary>Why a <see cref="ToolsGfxPreviewViewport"/> cannot draw.</summary>
/// <param name="Reason">Human-readable reason.</param>
/// <param name="InteropUnavailable">True when D3D11 composition interop itself is missing (applies to every preview
/// in this process); false when only this model failed.</param>
public sealed record ToolsGfxViewportFailure(string Reason, bool InteropUnavailable);

/// <summary>
/// Preview viewport drawing APE's own frame through the ToolsGfx renderer (<see cref="PreviewSceneRenderer"/> in a
/// <see cref="D3DViewport"/>): same shaders, lighting states, skybox, shadows and post chain as the Asset Editor.
/// Opens with APE's framing — pitch 22.5° down, yaw 0, looking at the centre of the model's bounds (all LODs) from
/// <c>radius / tanHalfFovY</c> away, which reproduces the RenderDoc captures' camera exactly — and then orbits with
/// the GL viewport's controls (APE's own orbit math is unknown): drag orbit, right/middle-drag pan, wheel or
/// Alt+right-drag zoom, Shift+drag rotates the sun, double-click reframes. Renders on demand.
/// Raises <see cref="Failed"/> when D3D11 composition interop is unavailable or the model cannot be created on the
/// GPU, so the host can fall back to <see cref="GlPreviewViewport"/>.
/// </summary>
public sealed class ToolsGfxPreviewViewport : Decorator
{
    public static readonly StyledProperty<PreviewEnvironment?> EnvironmentProperty =
        AvaloniaProperty.Register<ToolsGfxPreviewViewport, PreviewEnvironment?>(nameof(Environment));

    public static readonly StyledProperty<PreparedPreviewModel?> ModelProperty =
        AvaloniaProperty.Register<ToolsGfxPreviewViewport, PreparedPreviewModel?>(nameof(Model));

    public static readonly StyledProperty<IPreviewPose?> PoseProperty =
        AvaloniaProperty.Register<ToolsGfxPreviewViewport, IPreviewPose?>(nameof(Pose));

    public static readonly StyledProperty<PreviewLightState> LightStateProperty =
        AvaloniaProperty.Register<ToolsGfxPreviewViewport, PreviewLightState>(nameof(LightState));

    public static readonly StyledProperty<bool> FirstPersonProperty =
        AvaloniaProperty.Register<ToolsGfxPreviewViewport, bool>(nameof(FirstPerson));

    public static readonly StyledProperty<PreviewKick?> KickProperty =
        AvaloniaProperty.Register<ToolsGfxPreviewViewport, PreviewKick?>(nameof(Kick));

    public static readonly StyledProperty<double> FieldOfViewProperty =
        AvaloniaProperty.Register<ToolsGfxPreviewViewport, double>(nameof(FieldOfView), 65.0);

    public static readonly StyledProperty<bool> WheelChangesFieldOfViewProperty =
        AvaloniaProperty.Register<ToolsGfxPreviewViewport, bool>(nameof(WheelChangesFieldOfView));

    public static readonly DirectProperty<ToolsGfxPreviewViewport, string?> StatusTextProperty =
        AvaloniaProperty.RegisterDirect<ToolsGfxPreviewViewport, string?>(nameof(StatusText), o => o.StatusText);

    public static readonly DirectProperty<ToolsGfxPreviewViewport, string?> StatusDetailProperty =
        AvaloniaProperty.RegisterDirect<ToolsGfxPreviewViewport, string?>(nameof(StatusDetail), o => o.StatusDetail);

    public PreviewEnvironment? Environment
    {
        get => GetValue(EnvironmentProperty);
        set => SetValue(EnvironmentProperty, value);
    }

    public PreparedPreviewModel? Model
    {
        get => GetValue(ModelProperty);
        set => SetValue(ModelProperty, value);
    }

    /// <summary>GPU skinning of <see cref="Model"/> (an animation); null draws the bind pose. The frame is redrawn whenever
    /// the pose changes, so a playing anim renders continuously and a paused one on demand.</summary>
    public IPreviewPose? Pose
    {
        get => GetValue(PoseProperty);
        set => SetValue(PoseProperty, value);
    }

    public PreviewLightState LightState
    {
        get => GetValue(LightStateProperty);
        set => SetValue(LightStateProperty, value);
    }

    /// <summary>Look from the pose's view tag (<see cref="IPreviewPose.ViewTag"/>: <c>tag_camera</c> / <c>tag_view</c>)
    /// instead of orbiting; ignored while the pose has none.</summary>
    public bool FirstPerson
    {
        get => GetValue(FirstPersonProperty);
        set => SetValue(FirstPersonProperty, value);
    }

    /// <summary>Recoil on the camera and the model (<see cref="PreviewSceneRenderer.Kick"/>); null draws the frame without it.</summary>
    public PreviewKick? Kick
    {
        get => GetValue(KickProperty);
        set => SetValue(KickProperty, value);
    }

    /// <summary>First-person field of view as the game's <c>cg_fov</c>: horizontal degrees at 4:3, widened for wider
    /// windows (Hor+), so the vertical FOV is <c>2·atan(0.75·tan(fov/2))</c>.</summary>
    public double FieldOfView
    {
        get => GetValue(FieldOfViewProperty);
        set => SetValue(FieldOfViewProperty, value);
    }

    /// <summary>
    /// While looking through the view tag the camera can't zoom, so the wheel changes <see cref="FieldOfView"/> instead
    /// (5° a notch, within the game's <c>cg_fov</c> range); for a host that binds it two way.
    /// </summary>
    public bool WheelChangesFieldOfView
    {
        get => GetValue(WheelChangesFieldOfViewProperty);
        set => SetValue(WheelChangesFieldOfViewProperty, value);
    }

    /// <summary>The game's <c>cg_fov</c> range, and the wheel's step through it.</summary>
    private const double MinFov = 65, MaxFov = 120, FovStep = 5;

    private string? _statusText;
    /// <summary>"N warnings" when the model has any, else null. With <c>APEX_PREVIEW_STATS_LOG</c> set, the full line:
    /// renderer, adapter, lighting state, LOD, frame size and CPU/GPU frame time.</summary>
    public string? StatusText
    {
        get => _statusText;
        private set => SetAndRaise(StatusTextProperty, ref _statusText, value);
    }

    private string? _statusDetail;
    /// <summary>The model's warnings, one per line (tooltip of <see cref="StatusText"/>).</summary>
    public string? StatusDetail
    {
        get => _statusDetail;
        private set => SetAndRaise(StatusDetailProperty, ref _statusDetail, value);
    }

    /// <summary>The viewport cannot draw (argument: reason) — D3D11 interop missing, or the model failed on the GPU.</summary>
    public event EventHandler<ToolsGfxViewportFailure>? Failed;

    /// <summary>Last frame's numbers (diagnostics, scripted verification).</summary>
    public PreviewFrameStats? LastStats => _renderer?.Stats;

    private D3DViewport? _viewport;
    private PreviewSceneRenderer? _renderer;
    // What the camera was last framed for: the preview's view model (one per previewed asset), so a reload of the same
    // asset keeps the user's view while the next asset in the recycled template is framed.
    private object? _framedSubject;
    private bool _framedOnPose;

    // ── Camera: APE angles (pitch + = looking down, yaw 0 = looking along +X) around a target ──
    private Vector3 _target;
    private float _yaw;
    private float _pitch = DefaultPitch;
    private float _distance = 100f;
    private float _radius = 1f;
    private float? _sunPitch, _sunYaw;
    private const float DefaultPitch = 22.5f;
    private const float DegPerPixel = 0.5729578f; // the GL viewport's 0.01 rad/px

    private enum DragMode { None, Orbit, Pan, Zoom, Light }
    private DragMode _drag;
    private Point _last;

    /// <summary>Home frames the model like F (false in an anim preview, where Home goes to the first frame).</summary>
    public bool HomeFrames { get; set; } = true;

    public ToolsGfxPreviewViewport()
    {
        ClipToBounds = true;
        // Focusable so F / Home can frame the model once the user has clicked into the preview.
        Focusable = true;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == EnvironmentProperty || change.Property == ModelProperty)
        {
            SyncViewport();
            FrameIfNew();
            PushCamera();
        }
        else if (change.Property == PoseProperty)
        {
            if (_renderer != null)
                _renderer.Pose = Pose;
            if (FrameIfNew())
                PushCamera();
        }
        else if (change.Property == KickProperty)
        {
            if (_renderer != null)
                _renderer.Kick = Kick;
        }
        else if (change.Property == FirstPersonProperty || change.Property == FieldOfViewProperty)
        {
            PushCamera();
        }
        else if (change.Property == LightStateProperty)
        {
            // A lighting state brings its own sun; drop any user rotation.
            _sunPitch = _sunYaw = null;
            PushCamera();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        SyncViewport();
    }

    /// <summary>
    /// Creates the D3D viewport while there is something to draw, drops it (and its GPU copy) otherwise. A failure
    /// setting the renderer up (resource creation on a bad device, a model the GPU path rejects) tears this viewport
    /// down and reports <see cref="Failed"/>, so the host falls back for this asset and the editor carries on.
    /// </summary>
    private void SyncViewport()
    {
        try
        {
            SyncViewportCore();
        }
        catch (Exception ex) when (Services.CrashGuard.IsRecoverable(ex))
        {
            Services.CrashGuard.WriteLog(ex, "ToolsGfx viewport setup");
            if (_viewport != null)
            {
                _viewport.PropertyChanged -= OnViewportPropertyChanged;
                Child = null;
                _viewport = null;
            }
            DetachRenderer();
            // Posted: the host reacts by changing Model, which must not re-enter this property change.
            var failure = new ToolsGfxViewportFailure("the preview renderer failed: " + ex.Message, false);
            Dispatcher.UIThread.Post(() => Failed?.Invoke(this, failure));
        }
    }

    private void SyncViewportCore()
    {
        var env = Environment;
        if (env is null || Model is null)
        {
            if (_viewport != null)
            {
                _viewport.PropertyChanged -= OnViewportPropertyChanged;
                Child = null; // unload tears the device side down (renderer.OnDeviceDestroyed)
                _viewport = null;
            }
            DetachRenderer();
            return;
        }
        if (_renderer is null || !ReferenceEquals(_renderer.Environment, env))
        {
            DetachRenderer();
            _renderer = new PreviewSceneRenderer(env);
            _renderer.RenderRequested += RequestRender;
            _renderer.StatsChanged += OnStatsChanged;
            _renderer.ModelFailed += OnModelFailed;
        }
        _renderer.Model = Model;
        _renderer.Pose = Pose;
        _renderer.Kick = Kick;
        if (_viewport is null)
        {
            _viewport = new D3DViewport();
            _viewport.PropertyChanged += OnViewportPropertyChanged;
            Child = _viewport;
        }
        _viewport.Renderer = _renderer;
        UpdateStatus();
        StartSpinWhenReady();
    }

    private void DetachRenderer()
    {
        if (_renderer is null)
            return;
        _renderer.RenderRequested -= RequestRender;
        _renderer.StatsChanged -= OnStatsChanged;
        _renderer.ModelFailed -= OnModelFailed;
        _renderer.Pose = null;
        _renderer = null;
    }

    private void OnViewportPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != D3DViewport.StatusProperty || _viewport is not { } v)
            return;
        UpdateStatus();
        // D3DViewport sets IsInteropUnavailable, then Status = "unavailable: <reason>" — either the platform lacks
        // D3D11 shared-texture interop (true for every preview), or a frame failed for another reason.
        const string prefix = "unavailable: ";
        if (v.IsInteropUnavailable && v.Status.StartsWith(prefix, StringComparison.Ordinal))
        {
            var reason = v.Status[prefix.Length..];
            bool interop = reason.Contains("interop", StringComparison.OrdinalIgnoreCase)
                           || reason.Contains("compositor", StringComparison.OrdinalIgnoreCase)
                           || reason.Contains("adapter", StringComparison.OrdinalIgnoreCase);
            Failed?.Invoke(this, new ToolsGfxViewportFailure(reason, interop));
        }
    }

    private void OnModelFailed(string reason) => Failed?.Invoke(this, new ToolsGfxViewportFailure("model could not be created on the GPU: " + reason, false));

    private void RequestRender() => _viewport?.RequestRender();

    // The status line follows the renderer's stats (twice per frame while animating) at a readable rate.
    private static readonly TimeSpan StatusInterval = TimeSpan.FromMilliseconds(250);
    private readonly Stopwatch _statusClock = Stopwatch.StartNew();
    private bool _statusQueued;

    private void OnStatsChanged()
    {
        if (_renderer is { } r && _viewport is { } v && v.Status.StartsWith("D3D11", StringComparison.Ordinal))
            SpinStep(r.Stats);
        var wait = StatusInterval - _statusClock.Elapsed;
        if (wait <= TimeSpan.Zero)
        {
            UpdateStatus();
            return;
        }
        if (_statusQueued)
            return;
        _statusQueued = true;
        DispatcherTimer.RunOnce(() =>
        {
            _statusQueued = false;
            UpdateStatus();
        }, wait);
    }

    private void UpdateStatus()
    {
        _statusClock.Restart();
        var warnings = Model?.Warnings ?? (IReadOnlyList<string>)Array.Empty<string>();
        StatusDetail = warnings.Count > 0 ? string.Join(System.Environment.NewLine, warnings.Distinct().Take(30)) : null;
        if (!ToolsGfxPreviewService.IsLogging)
        {
            // The renderer's numbers are for development; the user only needs to know something didn't build.
            StatusText = _viewport is null || warnings.Count == 0 ? null : $"{warnings.Count} warning{(warnings.Count == 1 ? "" : "s")}";
            return;
        }
        if (_viewport is null || _renderer is null)
        {
            StatusText = null;
            return;
        }
        var vs = _viewport.Status;
        if (!vs.StartsWith("D3D11", StringComparison.Ordinal))
        {
            StatusText = "ToolsGfx · " + vs;
            return;
        }
        var s = _renderer.Stats;
        var inv = CultureInfo.InvariantCulture;
        var text = $"ToolsGfx · {_renderer.AdapterName} · {LightState}";
        if (s.Width > 0)
        {
            text += string.Format(inv, " · LOD{0} {1:N0} tris · {2}×{3} · cpu {4:0.0} ms", s.Lod, s.Triangles, s.Width, s.Height, s.CpuMilliseconds);
            if (!double.IsNaN(s.GpuMilliseconds))
                text += string.Format(inv, " · gpu {0:0.0} ms", s.GpuMilliseconds);
        }
        if (Model is { Warnings.Count: > 0 } m)
            text += $" · {m.Warnings.Count} warning{(m.Warnings.Count == 1 ? "" : "s")}";
        if (ToolsGfxPreviewService.IsLogging && text != StatusText && !double.IsNaN(s.GpuMilliseconds))
            ToolsGfxPreviewService.Log($"{Model?.Name} | {text}");
        StatusText = text;
    }

    // ── APEX_PREVIEW_SPIN: scripted orbit to measure interactive frame time ──

    private int _spinLeft = -1;
    private long _spinLastFrame;
    private readonly List<double> _spinCpu = new(), _spinGpu = new(), _spinWall = new();
    private readonly Stopwatch _spinClock = new();
    private double _spinLastGpu = double.NaN;

    private void StartSpinWhenReady()
    {
        if (ToolsGfxPreviewService.SpinFrames <= 0 || _spinLeft >= 0)
            return;
        _spinLeft = 0;
        // Let the skybox and first frames settle.
        DispatcherTimer.RunOnce(() =>
        {
            _spinLeft = ToolsGfxPreviewService.SpinFrames;
            _spinClock.Restart();
            PushCamera();
        }, TimeSpan.FromSeconds(3));
    }

    private void SpinStep(PreviewFrameStats s)
    {
        if (_spinLeft <= 0 || _viewport is null)
            return;
        if (!double.IsNaN(s.GpuMilliseconds) && s.GpuMilliseconds != _spinLastGpu)
        {
            _spinLastGpu = s.GpuMilliseconds;
            _spinGpu.Add(s.GpuMilliseconds);
        }
        if (_renderer is null || _renderer.FrameCount == _spinLastFrame)
            return; // a GPU-time update of the same frame
        _spinLastFrame = _renderer.FrameCount;
        _spinCpu.Add(s.CpuMilliseconds);
        _spinWall.Add(_spinClock.Elapsed.TotalMilliseconds);
        _spinClock.Restart();
        if (--_spinLeft == 0)
        {
            static string Summ(List<double> v)
            {
                if (v.Count == 0) return "n/a";
                var o = v.OrderBy(x => x).ToList();
                return string.Format(CultureInfo.InvariantCulture, "avg {0:0.00} / median {1:0.00} / p95 {2:0.00} ms",
                    v.Average(), o[o.Count / 2], o[Math.Min(o.Count - 1, (int)(o.Count * 0.95))]);
            }
            var wall = _spinWall.Skip(1).ToList();
            ToolsGfxPreviewService.Log($"{Model?.Name} | SPIN {_spinCpu.Count} frames at {s.Width}x{s.Height}: cpu {Summ(_spinCpu)}; gpu {Summ(_spinGpu)}; "
                + $"frame-to-frame {Summ(wall)} ({(wall.Count > 0 ? 1000.0 / wall.Average() : 0):0} fps)");
            return;
        }
        _yaw += 360f / ToolsGfxPreviewService.SpinFrames;
        PushCamera();
    }

    // ── Camera ──────────────────────────────────────────────────────────────

    /// <summary>APE's auto framing: the bounds centre at <c>radius / tanHalfFovY</c>, 22.5° down, yaw 0 — the bind bounds of
    /// all LODs, or for an animated model its posed bone boxes (<see cref="IPreviewPose.FramingBounds"/>).</summary>
    private void FrameModel()
    {
        if (Model is not { } m)
            return;
        _framedSubject = DataContext ?? m;
        _framedOnPose = Pose?.FramingBounds is not null;
        var (min, max) = Pose?.FramingBounds ?? (m.BoundsMin, m.BoundsMax);
        _target = (min + max) * 0.5f;
        _radius = MathF.Max((max - min).Length() * 0.5f, 0.01f);
        _yaw = 0f;
        _pitch = DefaultPitch;
        _distance = _radius / PreviewCamera.Default.TanHalfFovY;
        _sunPitch = _sunYaw = null;
    }

    /// <summary>
    /// Frames the model when it belongs to a new asset (a new preview view model), or when the asset's first pose arrives
    /// (APE frames an animated model on its posed bone boxes at t = 0, not the bind bounds). A reload of the same asset
    /// keeps the user's camera. True when it framed.
    /// </summary>
    private bool FrameIfNew()
    {
        if (Model is not { } m)
            return false;
        bool newAsset = !ReferenceEquals(DataContext ?? m, _framedSubject);
        if (!newAsset && (_framedOnPose || Pose?.FramingBounds is null))
            return false;
        FrameModel();
        return true;
    }

    /// <summary>Back to APE's framing of the model (the Frame button, F / Home, double-click).</summary>
    public void Frame()
    {
        FrameModel();
        PushCamera();
    }

    private PreviewCamera CurrentCamera()
    {
        PreviewViewMath.AngleVectors(_pitch, _yaw, 0f, out var forward, out _, out _);
        return new PreviewCamera(_target - forward * _distance, _pitch, _yaw);
    }

    private void PushCamera()
    {
        if (_renderer is null)
            return;
        _renderer.Camera = CurrentCamera();
        _renderer.FirstPersonFov = FirstPerson ? PreviewFov(FieldOfView) : null;
        _renderer.LightState = LightState;
        _renderer.SunPitchOverride = _sunPitch;
        _renderer.SunYawOverride = _sunYaw;
        RequestRender();
    }

    /// <summary>A <c>cg_fov</c> value (4:3 horizontal) as the renderer's FOV, whose tangent is taken against 16:9
    /// (<see cref="PreviewCamera.TanHalfFovY"/> = tan(fov/2)·0.5625): same vertical FOV.</summary>
    private static float PreviewFov(double cgFov)
    {
        double half = Math.Clamp(cgFov, 1.0, 170.0) * Math.PI / 360.0;
        return (float)(2.0 * Math.Atan(Math.Tan(half) * 0.75 / 0.5625) * 180.0 / Math.PI);
    }

    /// <summary>Orbit / pan / zoom do nothing while looking through the view tag.</summary>
    private bool LooksFromTag => FirstPerson && Pose?.ViewTag is not null;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetCurrentPoint(this);
        Focus(NavigationMethod.Pointer);
        if (e.ClickCount == 2 && point.Properties.IsLeftButtonPressed)
        {
            Frame();
            e.Handled = true;
            return;
        }
        _last = point.Position;
        var props = point.Properties;
        _drag = props.IsLeftButtonPressed
            ? (e.KeyModifiers & (KeyModifiers.Shift | KeyModifiers.Control)) != 0 ? DragMode.Light : LooksFromTag ? DragMode.None : DragMode.Orbit
            : LooksFromTag ? DragMode.None
            : props.IsRightButtonPressed
                ? (e.KeyModifiers & KeyModifiers.Alt) != 0 ? DragMode.Zoom : DragMode.Pan
                : props.IsMiddleButtonPressed ? DragMode.Pan : DragMode.None;
        if (_drag != DragMode.None)
        {
            e.Pointer.Capture(this);
            e.Handled = true;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_drag == DragMode.None)
            return;
        // The release can be lost (another window took the mouse mid-drag): with no button down the drag is over.
        var buttons = e.GetCurrentPoint(this).Properties;
        if (!buttons.IsLeftButtonPressed && !buttons.IsRightButtonPressed && !buttons.IsMiddleButtonPressed)
        {
            EndDrag(e.Pointer);
            return;
        }
        var pos = e.GetPosition(this);
        float dx = (float)(pos.X - _last.X), dy = (float)(pos.Y - _last.Y);
        _last = pos;
        switch (_drag)
        {
            case DragMode.Orbit:
                _yaw -= dx * DegPerPixel;
                _pitch = Math.Clamp(_pitch + dy * DegPerPixel, -89f, 89f);
                break;
            case DragMode.Pan:
            {
                PreviewViewMath.AngleVectors(_pitch, _yaw, 0f, out _, out var right, out var up);
                float scale = _distance * 0.0016f;
                _target += right * (-dx * scale) + up * (dy * scale);
                break;
            }
            case DragMode.Zoom:
                _distance = Math.Clamp(_distance * MathF.Pow(1.005f, dy), _radius * 0.15f, _radius * 25f);
                break;
            case DragMode.Light:
            {
                var sun = Environment?.State(LightState).Sun;
                _sunYaw = (_sunYaw ?? sun?.Yaw ?? 0f) - dx * DegPerPixel;
                _sunPitch = (_sunPitch ?? sun?.Pitch ?? 45f) + dy * DegPerPixel;
                break;
            }
        }
        PushCamera();
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_drag == DragMode.None)
            return;
        EndDrag(e.Pointer);
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _drag = DragMode.None;
    }

    private void EndDrag(IPointer pointer)
    {
        _drag = DragMode.None;
        if (ReferenceEquals(pointer.Captured, this))
            pointer.Capture(null);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || !Commands.CommandCatalog.Is(Commands.CommandCatalog.FramePreview, e))
            return;
        // In an anim preview Home belongs to the transport (first frame); F always frames.
        if (e.Key == Key.Home && !HomeFrames)
            return;
        Frame();
        e.Handled = true;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (LooksFromTag)
        {
            if (WheelChangesFieldOfView && e.Delta.Y != 0)
            {
                // Wheel up narrows the view, as zooming in does.
                SetCurrentValue(FieldOfViewProperty, Math.Clamp(Math.Round(FieldOfView - Math.Sign(e.Delta.Y) * FovStep), MinFov, MaxFov));
                e.Handled = true;
            }
            return;
        }
        _distance = Math.Clamp(_distance * MathF.Pow(0.86f, (float)e.Delta.Y), _radius * 0.15f, _radius * 25f);
        PushCamera();
        e.Handled = true;
    }
}
