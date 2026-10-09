using System.Diagnostics;
using System.Numerics;
using Apex.Render.Assets;
using Apex.Render.Constants;
using Apex.Render.Data.Lighting;
using Apex.Render.Device;
using Apex.Render.Passes;
using Apex.Render.Scene;
using Avalonia.Threading;

namespace Apex.Render.Presentation;

/// <summary>Per-frame numbers of a <see cref="PreviewSceneRenderer"/>.</summary>
/// <param name="CpuMilliseconds">Time to record the frame (constants, LOD selection, every pass).</param>
/// <param name="GpuMilliseconds">GPU time of the frame (timestamp queries; NaN until the first result).</param>
/// <param name="Width">Frame width in pixels.</param>
/// <param name="Height">Frame height in pixels.</param>
/// <param name="Lod">Main-view LOD APE's selection picked.</param>
/// <param name="Triangles">Triangles of that LOD.</param>
public readonly record struct PreviewFrameStats(double CpuMilliseconds, double GpuMilliseconds, int Width, int Height, int Lod, int Triangles);

/// <summary>
/// Draws APE's preview frame (<see cref="PreviewFrameRenderer"/>) of one prepared model into a <see cref="D3DViewport"/>:
/// the model's LODs and sun-shadow LODs chosen per frame like APE, the lighting state's skybox at the camera, LED
/// lighting, and APE's cb9 for <see cref="Camera"/> (reversed-Z infinite projection). Device resources come from
/// <see cref="PreviewDeviceResources"/> (shared per device); the model's GPU copy is rebuilt from the
/// <see cref="PreparedPreviewModel"/> after device loss. UI thread only.
/// </summary>
public sealed class PreviewSceneRenderer : ID3DViewportRenderer
{
    private readonly PreviewEnvironment _env;
    private GfxDevice? _gfx;
    private PreviewDeviceResources? _res;
    private GpuFrameTimer? _timer;
    private PreviewModel? _model;
    private PreparedPreviewModel? _modelKey;
    private PreparedPreviewModel? _prepared;
    private readonly Dictionary<PreparedPreviewModel, PreviewModel> _attached = new();
    private readonly HashSet<PreparedPreviewModel> _attachedPending = new();
    private int _modelBuild;
    private LineOverlayPass? _overlay;
    private DispatcherTimer? _statsPoll;
    private readonly HashSet<PreviewLightState> _skyRequested = new();
    private readonly Stopwatch _cpu = new();
    // Per-frame lists, reused: the model plus attached items, and the skybox placed at the camera.
    private readonly List<IPreviewDrawItem> _frameItems = new();
    private readonly List<PlacedItem> _skyItems = new();
    private CodeSceneConsts[] _shadowViews = [];

    public PreviewSceneRenderer(PreviewEnvironment environment) => _env = environment;

    public PreviewEnvironment Environment => _env;

    /// <summary>The model to draw (null = sky only). Its GPU copy is created on a worker thread when set (or once a
    /// device exists); the frames draw the sky until it is ready, then <see cref="RenderRequested"/> is raised.</summary>
    public PreparedPreviewModel? Model
    {
        get => _prepared;
        set
        {
            if (ReferenceEquals(_prepared, value))
                return;
            _prepared = value;
            if (_gfx != null)
                BuildModel();
        }
    }

    /// <summary>Skinning of the model for the next frames (null = bind pose). Its <see cref="IPreviewPose.Changed"/>
    /// raises <see cref="RenderRequested"/>.</summary>
    public IPreviewPose? Pose
    {
        get => _pose;
        set
        {
            if (ReferenceEquals(_pose, value))
                return;
            if (_pose != null)
                _pose.Changed -= OnPoseChanged;
            _pose = value;
            if (_pose != null)
                _pose.Changed += OnPoseChanged;
            SyncAttached();
            RenderRequested?.Invoke();
        }
    }

    /// <summary>First-person view: when set and the pose has a view tag (<see cref="IPreviewPose.ViewTag"/>), each
    /// frame looks from that tag at the pose's current time with this FOV instead of <see cref="Camera"/>.</summary>
    public float? FirstPersonFov { get; set; }

    /// <summary>
    /// Recoil on the model and the camera for the next frames (a weapon preview's simulator). Null, or a kick of all
    /// zeros, draws exactly the frame there would be without it: nothing below runs.
    /// </summary>
    public PreviewKick? Kick
    {
        get => _kick;
        set
        {
            if (_kick == value)
                return;
            _kick = value;
            RenderRequested?.Invoke();
        }
    }

    private PreviewKick? _kick;
    private readonly List<PlacedItem> _kickedItems = new();

    private IPreviewPose? _pose;

    private void OnPoseChanged() => RenderRequested?.Invoke();

    public PreviewLightState LightState { get; set; } = PreviewLightState.Morning;
    public PreviewCamera Camera { get; set; } = PreviewCamera.Default;
    /// <summary>User sun rotation (degrees) replacing the SSI pitch/yaw; null = the lighting state's sun.</summary>
    public float? SunPitchOverride { get; set; }
    public float? SunYawOverride { get; set; }

    /// <summary>Adapter the frames are drawn on (null without a device).</summary>
    public string? AdapterName => _gfx?.AdapterName;

    /// <summary>Why the model's GPU copy could not be created (null when it was, or while it is being created).</summary>
    public string? ModelError { get; private set; }

    public PreviewFrameStats Stats { get; private set; }

    /// <summary>Frames drawn so far.</summary>
    public long FrameCount { get; private set; }

    /// <summary>Something the frame depends on became ready (e.g. the skybox finished loading): draw again.</summary>
    public event Action? RenderRequested;

    /// <summary>Creating the model's GPU copy failed (argument: reason). The frame still draws the sky.</summary>
    public event Action<string>? ModelFailed;

    /// <summary><see cref="Stats"/> changed (after a frame, and again when its GPU time arrives).</summary>
    public event Action? StatsChanged;

    public void OnDeviceCreated(GfxDevice device)
    {
        _gfx = device;
        _res = PreviewDeviceResources.For(device, _env);
        _res.SkyReady += OnSkyReady;
        _timer = new GpuFrameTimer(device);
        _overlay = new LineOverlayPass(device);
        BuildModel();
        SyncAttached();
    }

    public void OnDeviceDestroyed()
    {
        DisposeModel();
        ReleaseAttached(_attached.Keys.ToList());
        _attachedPending.Clear();
        if (_res != null)
        {
            _res.SkyReady -= OnSkyReady;
            _res.Rc.Targets.Release(this);
        }
        _overlay?.Dispose();
        _overlay = null;
        _timer?.Dispose();
        _timer = null;
        _res = null;
        _gfx = null;
    }

    private void OnSkyReady() => RenderRequested?.Invoke();

    /// <summary>Starts getting the model's GPU copy (created on a worker; the frames draw the sky until it is there).</summary>
    private void BuildModel()
    {
        DisposeModel();
        ModelError = null;
        if (_prepared is not { } prepared || _res is not { } res)
            return;
        int build = _modelBuild;
        Acquire(res, prepared, t =>
        {
            if (build != _modelBuild || !ReferenceEquals(res, _res))
            {
                if (t.IsCompletedSuccessfully)
                    res.ReleaseModel(prepared);
                return;
            }
            if (t.IsCompletedSuccessfully)
            {
                _model = t.Result;
                _modelKey = prepared;
                RenderRequested?.Invoke();
            }
            else if (Failure(t) is { } reason)
            {
                ModelError = reason;
                ModelFailed?.Invoke(reason);
            }
        });
    }

    /// <summary>GPU copies of the pose's attached models (<see cref="IPreviewPose.Attached"/>): created for new ones,
    /// handed back for those no longer attached.</summary>
    private void SyncAttached()
    {
        var wanted = _pose?.Attached.Select(a => a.Model).OfType<PreparedPreviewModel>().ToHashSet() ?? new HashSet<PreparedPreviewModel>();
        ReleaseAttached(_attached.Keys.Where(k => !wanted.Contains(k)).ToList());
        _attachedPending.RemoveWhere(k => !wanted.Contains(k));
        if (_res is not { } res)
            return;
        foreach (var model in wanted)
        {
            if (_attached.ContainsKey(model) || !_attachedPending.Add(model))
                continue;
            Acquire(res, model, t =>
            {
                if (!ReferenceEquals(res, _res) || !_attachedPending.Remove(model))
                {
                    if (t.IsCompletedSuccessfully)
                        res.ReleaseModel(model);
                    return;
                }
                if (t.IsCompletedSuccessfully)
                {
                    _attached[model] = t.Result;
                    RenderRequested?.Invoke();
                }
                else if (Failure(t) is { } reason)
                {
                    ModelFailed?.Invoke($"{model.Name}: {reason}");
                }
            });
        }
    }

    /// <summary>Acquires <paramref name="model"/> on <paramref name="res"/>; <paramref name="done"/> runs on the UI thread
    /// (at once when the model is resident).</summary>
    private static void Acquire(PreviewDeviceResources res, PreparedPreviewModel model, Action<Task<PreviewModel>> done)
        => res.AcquireModelAsync(model).ContinueWith(done, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    /// <summary>Why a model could not be created, or null when the device was lost (the viewport then recreates it and
    /// every model is built again).</summary>
    private string? Failure(Task<PreviewModel> t)
    {
        var ex = t.Exception?.InnerException ?? new TaskCanceledException();
        return _gfx?.IsDeviceLossException(ex) == true ? null : ex.Message;
    }

    private void ReleaseAttached(List<PreparedPreviewModel> models)
    {
        foreach (var m in models)
        {
            _attached.Remove(m);
            _res?.ReleaseModel(m);
        }
    }

    /// <summary>Hands the GPU model back to the device's cache (kept briefly for a quick re-attach); a copy still being
    /// created is handed back when it arrives.</summary>
    private void DisposeModel()
    {
        if (_modelKey is not null)
            _res?.ReleaseModel(_modelKey);
        _modelKey = null;
        _model = null;
        _modelBuild++;
    }

    public void Render(in D3DFrame frame)
    {
        var res = _res ?? throw new InvalidOperationException("no device");
        _cpu.Restart();
        int w = frame.Width, h = frame.Height;
        res.Rc.Targets.Resize(this, w, h);

        var state = _env.HasState(LightState) ? LightState : _env.Lighting.States[0].State;
        // A pose bound to another model (the host swaps model and pose one after the other) is not applied.
        var pose = _pose is { } p && (p.Model is null || ReferenceEquals(p.Model, _modelKey)) ? p : null;
        var camera = Camera;
        if (FirstPersonFov is { } fov && pose?.ViewTag is { } tag)
        {
            // Tag axes: X forward, Y left, Z up.
            var forward = Vector3.Normalize(new Vector3(tag.M11, tag.M12, tag.M13));
            var right = -Vector3.Normalize(new Vector3(tag.M21, tag.M22, tag.M23));
            camera = PreviewCamera.FromAxes(tag.Translation, forward, right, fov);
        }
        var kick = _kick is { IsZero: false } k ? k : (PreviewKick?)null;
        if (kick is { MovesView: true } viewKick)
            camera = Kicked(camera, viewKick.View);
        var builder = _env.CreateSceneBuilder(state, camera, w, h);
        builder.SunPitchOverride = SunPitchOverride;
        builder.SunYawOverride = SunYawOverride;
        var main = builder.Build(out var setup);
        CodeSceneConsts[]? views = null;
        if (setup is not null)
        {
            views = _shadowViews.Length == setup.Cascades.Length ? _shadowViews : _shadowViews = new CodeSceneConsts[setup.Cascades.Length];
            SceneConstantsBuilder.BuildSunShadowViews(main, setup, views);
        }
        var origin = camera.Position;

        IReadOnlyList<IPreviewDrawItem> items = _model?.SelectForFrame(main, views, origin, pose) ?? Array.Empty<IPreviewDrawItem>();
        if (pose is not null && _attached.Count > 0)
        {
            var all = _frameItems;
            all.Clear();
            all.AddRange(items);
            foreach (var a in pose.Attached)
                if (a.Model is { } am && _attached.TryGetValue(am, out var gpu))
                    all.AddRange(gpu.SelectForFrame(main, views, origin, a));
            items = all;
        }
        Matrix4x4 kickWorld = default;
        if (kick is { } itemKick)
        {
            kickWorld = itemKick.Gun * itemKick.View;
            items = Placed(items, kickWorld);
        }
        IReadOnlyList<IPreviewDrawItem> skyItems = Array.Empty<IPreviewDrawItem>();
        if (res.Sky(state) is { } sky)
        {
            // The SSI skybox is drawn at the camera origin (t30 world translation), sortDepth 1.
            var world = Matrix4x4.CreateTranslation(origin);
            var selected = sky.SelectForFrame(main, null, origin);
            while (_skyItems.Count < selected.Count)
                _skyItems.Add(new PlacedItem());
            _skyItems.RemoveRange(selected.Count, _skyItems.Count - selected.Count);
            for (int i = 0; i < selected.Count; i++)
                _skyItems[i].Place(selected[i], world);
            skyItems = _skyItems;
        }
        else if (!_env.IsSkyLoaded(state) && _skyRequested.Add(state))
        {
            _ = _env.SkyAsync(state).ContinueWith(_ => RenderRequested?.Invoke(), TaskScheduler.FromCurrentSynchronizationContext());
        }

        _timer?.BeginFrame();
        res.Frame.Render(new PreviewFrameInputs
        {
            Scene = main,
            SunShadowViews = views,
            Items = items,
            SkyItems = skyItems,
            Lighting = res.Lighting(state),
            Output = frame.Target,
            // The viewport surface is composited: keep it opaque instead of APE's coverage-in-alpha.
            DepthToAlpha = false,
        });
        if (pose?.Overlay is { } overlay && _overlay is not null)
        {
            if (kick is not null)
                overlay = new PreviewOverlay(overlay.Segments.Select(p => Vector3.Transform(p, kickWorld)).ToArray(),
                    overlay.Joints.Select(p => Vector3.Transform(p, kickWorld)).ToArray());
            _overlay.Draw(frame.Target, w, h, main.Transforms.WldToClp, overlay.Segments, overlay.Joints);
        }
        _timer?.EndFrame();
        _cpu.Stop();
        FrameCount++;

        int lod = _model?.LastSelection.Main ?? 0;
        int tris = _model is { } m && lod < m.Lods.Count ? m.Lods[lod].Triangles : 0;
        Stats = new PreviewFrameStats(_cpu.Elapsed.TotalMilliseconds, _timer?.LastMilliseconds ?? double.NaN, w, h, lod, tris);
        StatsChanged?.Invoke();
        SchedulePoll();
    }

    /// <summary><paramref name="camera"/> moved by a view kick (row vectors: the camera's frame in model space, then the kick).</summary>
    public static PreviewCamera Kicked(PreviewCamera camera, Matrix4x4 view)
    {
        camera.GetAxes(out var forward, out var right, out _);
        return PreviewCamera.FromAxes(Vector3.Transform(camera.Position, view), Vector3.TransformNormal(forward, view),
            Vector3.TransformNormal(right, view), camera.FovDegrees);
    }

    /// <summary>The frame's draws with <paramref name="world"/> after each one's own world matrix (wrappers reused).</summary>
    private IReadOnlyList<IPreviewDrawItem> Placed(IReadOnlyList<IPreviewDrawItem> items, Matrix4x4 world)
    {
        while (_kickedItems.Count < items.Count)
            _kickedItems.Add(new PlacedItem());
        _kickedItems.RemoveRange(items.Count, _kickedItems.Count - items.Count);
        for (int i = 0; i < items.Count; i++)
            _kickedItems[i].Place(items[i], items[i].ObjectConsts.WorldMatrix * world);
        return _kickedItems;
    }

    /// <summary>Polls the GPU timer 30 ms later (one timer, reused).</summary>
    private void SchedulePoll()
    {
        if (_statsPoll is null)
        {
            _statsPoll = new DispatcherTimer(DispatcherPriority.Default) { Interval = TimeSpan.FromMilliseconds(30) };
            _statsPoll.Tick += OnStatsPoll;
        }
        if (!_statsPoll.IsEnabled)
            _statsPoll.Start();
    }

    private void OnStatsPoll(object? sender, EventArgs e)
    {
        _statsPoll!.Stop();
        if (_timer is not { } t)
            return;
        if (t.Poll())
        {
            Stats = Stats with { GpuMilliseconds = t.LastMilliseconds };
            StatsChanged?.Invoke();
        }
        if (t.HasPending)
            SchedulePoll();
    }

    /// <summary>A draw placed with another world matrix (the skybox follows the camera); re-placed every frame.</summary>
    private sealed class PlacedItem : IPreviewDrawItem
    {
        private IPreviewDrawItem _inner = null!;
        private CodeObjectConsts _obj;

        public void Place(IPreviewDrawItem inner, Matrix4x4 world)
        {
            _inner = inner;
            _obj = inner.ObjectConsts;
            _obj.WorldMatrix = world;
        }

        public MeshGeometry Mesh => _inner.Mesh;
        public DrawRange Range => _inner.Range;
        public CodeObjectConsts ObjectConsts => _obj;
        public ReadOnlyMemory<byte> Bones => _inner.Bones;
        public MaterialPass? GetPass(PreviewTechnique technique) => _inner.GetPass(technique);
        public DrawRange? ShadowRange(int cascade) => _inner.ShadowRange(cascade);
        public int ShadowCascadeMask => _inner.ShadowCascadeMask;
    }
}
