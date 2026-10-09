using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Apex.Editor.Models;
using Apex.Editor.Services.Extensions;
using Apex.Editor.Services.Extensions.Simulation;
using Apex.Editor.Services.Gdt;
using Apex.Editor.Services.Preview.ToolsGfx;
using Apex.Render.Assets;
using Apex.Render.Data.Animation;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Apex.Editor.ViewModels;

/// <summary>
/// A weapon's recoil preview, part of an extension: the first-person viewmodel (<see cref="WeaponPreviewSource"/>) drawn
/// by APE's renderer and moved by the extension's preview module, with the spray overlay and its readout measured from
/// the same steps (<see cref="Trace"/>). Hip is the idle anim's first frame, ADS the ADS-up anim's last frame, mixed by
/// the ADS fraction the module is given. Only the motion the module says it computed is drawn; its note says what it
/// doesn't. A weapon with no gun to draw still fires: the overlay and readout carry it. The module is asked for on load
/// (its question shown in place, the first time), and the simulation is made again whenever the weapon's extension
/// values change (they are a copy). It steps on the view's display frames while something moves, and not at all
/// otherwise.
/// </summary>
public sealed partial class WeaponPreviewViewModel : ObservableObject, IPreviewContent
{
    /// <summary>weapon-tech's own fire time key (milliseconds): the cadence its module expects when the weapon sets it.</summary>
    public const string FireTimeKey = "wtFireTimeMs";

    private readonly AssetEditorViewModel _tab;
    private readonly GameEnvironment _env;
    private readonly Func<string, string, AssetRecord?> _resolve;
    private readonly Func<SimulatorHost> _simulators;
    private readonly ModuleQuestions? _questions;
    private readonly DelayedLoading _loading;
    private SimulatorHost? _host;
    private CancellationTokenSource? _cts;
    private bool _disposed;

    private string? _planKey;
    private string? _requestKey;
    private SimulatorRequest? _request;
    private IDisposable? _watch;
    private ISimulation? _simulation;
    private int _simulationGeneration;
    private bool _creating;
    private AnimBlendClip? _blend;
    private IReadOnlyList<string> _planNotes = Array.Empty<string>();
    private string? _adsNote;

    public WeaponPreviewViewModel(AssetEditorViewModel tab, GameEnvironment env, Func<string, string, AssetRecord?> resolve, Func<SimulatorHost> simulators,
        ModuleQuestions? questions = null)
    {
        _tab = tab;
        _env = env;
        _resolve = resolve;
        _simulators = simulators;
        _questions = questions;
        _loading = new DelayedLoading(v => IsLoading = v);
        Driver.Stepped += (dt, _, shots, frame) => Trace.Add(dt, shots, frame);
        if (questions is not null)
            questions.Changed += OnQuestionsChanged;
    }

    /// <summary>What the trigger, the ADS fraction and the steps are (verification reads it).</summary>
    public RecoilDriver Driver { get; } = new();

    /// <summary>The overlay's dots and path and the readout's numbers, from the driver's steps.</summary>
    public SprayTrace Trace { get; } = new();

    /// <summary>APE's renderer; a weapon preview has no approximate fallback.</summary>
    public ToolsGfxPreviewState Gfx { get; } = new();

    [ObservableProperty]
    private AnimRigPose? _pose;

    /// <summary>The motion on the viewport this frame (only the parts the module computed); null at rest.</summary>
    [ObservableProperty]
    private PreviewKick? _kick;

    /// <summary>Why there is no gun to draw: one line on the viewport, under the overlay.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowError))]
    private string? _error;

    [ObservableProperty]
    private string? _errorDetail;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowError))]
    private bool _isLoading;

    public bool ShowError => !IsLoading && !string.IsNullOrEmpty(Error);

    /// <summary>The weapon names a gun and an idle anim that exist, so a viewmodel is drawn.</summary>
    [ObservableProperty]
    private bool _hasPlan;

    /// <summary>What is drawn and where each part came from.</summary>
    [ObservableProperty]
    private string? _viewmodelInfo;

    /// <summary>The module's state when it isn't simulating ("you chose not to load it"), else null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Notice), nameof(NoticeTip))]
    private string? _simulatorNotice;

    /// <summary>The module's note on the last step (what it doesn't simulate), or the parts it left out.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Notice), nameof(NoticeTip))]
    private string? _motionNote;

    /// <summary>"Load module…" beside the notice: the module was declined, or couldn't be used (fixed since?).</summary>
    [ObservableProperty]
    private bool _canLoadModule;

    /// <summary>The module's question, asked in this preview while it is open; null otherwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Notice), nameof(NoticeTip))]
    private ModuleQuestion? _question;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ResetCommand))]
    private bool _canFire;

    /// <summary>Aiming down the sights. Hip and ADS show it one way (a checked class each): a segment that set it back
    /// through a two-way binding could end up with neither checked.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHip))]
    private bool _isAds;

    public bool IsHip => !IsAds;

    // The readout under Fire: the last burst (see SprayTrace for what each one measures).

    [ObservableProperty]
    private string _climbText = NoValue;

    [ObservableProperty]
    private string _driftText = NoValue;

    [ObservableProperty]
    private string _settleText = NoValue;

    [ObservableProperty]
    private string _shotsText = "0";

    /// <summary>Said in the readout's place when the module doesn't compute view angles: there is nothing to measure.</summary>
    [ObservableProperty]
    private string? _measureNote;

    public const string NoValue = "—";

    public const string NoViewKick = "The module doesn't simulate view kick for this weapon, so there's nothing to measure.";

    /// <summary>
    /// The line under the viewport: the module's state, its note, and what the weapon lacks (no ADS anim), with the
    /// extension's keys in them said as the form labels them (<see cref="NoticeTip"/> keeps the keys).
    /// </summary>
    public string? Notice => Labelled(RawNotice);

    /// <summary>The notice as written, keys included.</summary>
    public string? NoticeTip => RawNotice;

    private string? RawNotice
    {
        get
        {
            // While the question is on screen it is the notice.
            var state = Question is null ? SimulatorNotice : null;
            var parts = new[] { state, MotionNote }.Concat(_planNotes).Append(_adsNote).Where(p => !string.IsNullOrEmpty(p)).ToList();
            return parts.Count == 0 ? null : string.Join(" ", parts);
        }
    }

    private string? _labelledFrom, _labelled;

    private string? Labelled(string? raw)
    {
        if (raw is null || _request is null)
            return raw;
        if (raw != _labelledFrom)
        {
            _labelledFrom = raw;
            _labelled = ExtensionKeyText.ToLabels(raw, _request.Extension);
        }
        return _labelled;
    }

    /// <summary>The game's <c>cg_fov</c>, as last chosen in an anim preview.</summary>
    public double FieldOfView => AnimPreviewViewModel.SessionFov;

    public string FireTip => CanFire
        ? $"{Commands.CommandCatalog.Get(Commands.CommandCatalog.Fire).Tip}: hold to fire at the weapon's fire time ({Driver.FireTime * 1000:0} ms a round), tap for one"
        : "Nothing to fire: the preview module isn't simulating this weapon";

    partial void OnCanFireChanged(bool value) => OnPropertyChanged(nameof(FireTip));

    /// <summary>Every edit reaches here: the plan and the module's values are compared as text, so only a change that
    /// matters reloads the viewmodel or remakes the simulation.</summary>
    public bool DependsOn(string propertyKey) => true;

    public async Task RefreshAsync()
    {
        if (_disposed)
            return;
        var resolution = WeaponPreviewSource.Resolve(_tab.Record, _resolve);
        var request = _tab.SimulatorRequests().FirstOrDefault();
        HasPlan = resolution.Plan is not null;
        Driver.FireTime = FireTimeOf(request, resolution.Timing.FireTime);
        Driver.AdsInTime = resolution.Timing.AdsInTime;
        Driver.AdsOutTime = resolution.Timing.AdsOutTime;
        OnPropertyChanged(nameof(FireTip));
        SetRequest(request);
        if (resolution.Plan is not { } plan)
        {
            // No gun to draw: the module still runs, and the overlay and readout carry the preview.
            _cts?.Cancel();
            _planKey = null;
            _planNotes = Array.Empty<string>();
            _adsNote = null;
            ClearScene(resolution.Problem);
            return;
        }
        if (plan.Key == _planKey)
            return;
        _planKey = plan.Key;
        _planNotes = plan.Notes;
        OnNoticeChanged();
        await LoadSceneAsync(plan);
    }

    /// <summary>The extension's own fire time when the weapon (or an ancestor) sets it, else the weapon's <c>fireTime</c>.</summary>
    private float FireTimeOf(SimulatorRequest? request, float fireTime)
    {
        if (request is not null && _tab.SetExtensionValue(request.Extension.Id, FireTimeKey) is { } ms
            && float.TryParse(ms, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && float.IsFinite(v) && v > 0f)
            return v / 1000f;
        return fireTime;
    }

    private void OnNoticeChanged()
    {
        OnPropertyChanged(nameof(Notice));
        OnPropertyChanged(nameof(NoticeTip));
    }

    // ── The viewmodel ────────────────────────────────────────────────────

    private async Task LoadSceneAsync(WeaponPreviewPlan plan)
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        _loading.Begin();
        try
        {
            WeaponPreviewScene? loaded = null;
            var lastHands = AnimPreviewViewModel.SessionHands;
            var ok = await Gfx.TryLoadAsync(_env, _resolve, data =>
            {
                loaded = WeaponPreviewSource.Prepare(data, _env, plan, lastHands, cts.Token);
                return loaded.Viewmodel.Primary;
            }, cts.Token);
            if (cts.IsCancellationRequested)
                return;
            if (!ok || loaded is null || Gfx.Model is not { } model)
            {
                ClearScene(CantDraw, Gfx.FallbackDetail);
                return;
            }
            var scene = loaded.Viewmodel;
            _blend = new AnimBlendClip(scene.Clip.Clip, loaded.Ads ?? scene.Clip.Clip, loaded.Ads is null ? 0f : 1f) { Weight = Driver.Ads };
            Pose = new AnimRigPose(model, scene.Attached, scene.Rig, _blend);
            _adsNote = loaded.AdsNote;
            var info = new List<string>();
            if (scene.Description.Length > 0)
                info.Add(scene.Description);
            info.Add($"hip {plan.IdleAnim}");
            if (loaded.Ads is not null)
                info.Add($"ADS {plan.AdsAnim}");
            if (((IPreviewPose)Pose).ViewTag is null)
                info.Add("no camera tag on the rig, so the camera orbits");
            ViewmodelInfo = string.Join(" · ", info);
            Error = Pose.IsAnimated ? null : "The viewmodel has no skeleton to animate.";
            ErrorDetail = null;
            OnNoticeChanged();
            ApplyFrame();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (PreviewFailure.Contain(ex, _tab.Record, cts))
        {
            if (!cts.IsCancellationRequested)
                ClearScene(PreviewFailure.Text);
        }
        finally
        {
            if (_cts == cts)
                _loading.End();
        }
    }

    /// <summary>Said in the gun's place when the renderer can't draw it (why is in the tooltip); Fire still works.</summary>
    public const string CantDraw = "Apex can't draw this viewmodel, so the preview shows the spray alone.";

    /// <summary>The viewport couldn't draw (no D3D11 interop, the GPU refused the model): there is no approximation to fall back on.</summary>
    public void FallBack(string reason)
    {
        if (_disposed)
            return;
        Gfx.Fallback(reason);
        ClearScene(CantDraw, reason);
    }

    private void ClearScene(string? error, string? detail = null)
    {
        _loading.End();
        Gfx.Clear();
        Pose = null;
        _blend = null;
        ViewmodelInfo = null;
        Error = error;
        ErrorDetail = detail;
        OnNoticeChanged();
    }

    // ── The simulation ───────────────────────────────────────────────────

    /// <summary>A new simulation when the extension's values changed (none when no extension with a module is on).</summary>
    private void SetRequest(SimulatorRequest? request)
    {
        var key = KeyOf(request);
        if (key == _requestKey)
            return;
        _requestKey = key;
        _request = request;
        _labelledFrom = null;
        // Watched before the host is asked: its question is then asked here, in place.
        var old = _watch;
        _watch = request is not null ? _questions?.Watch(request.Extension.Id) : null;
        old?.Dispose();
        _ = RecreateSimulationAsync();
    }

    private static string? KeyOf(SimulatorRequest? request)
    {
        if (request is null)
            return null;
        var sb = new StringBuilder(request.Extension.Id);
        foreach (var (k, v) in request.Values)
            sb.Append('\n').Append(k).Append('=').Append(v);
        return sb.ToString();
    }

    /// <summary>
    /// Drops the simulation and asks the host for a new one for the current values. A newer ask supersedes this one:
    /// its result, when it comes, is disposed unused.
    /// </summary>
    private async Task RecreateSimulationAsync()
    {
        var generation = ++_simulationGeneration;
        DropSimulation();
        if (_request is not { } request || _disposed)
        {
            SimulatorNotice = null;
            CanLoadModule = false;
            return;
        }
        var host = Host();
        if (host.StatusOf(request.Extension) is SimulatorStatus.NotLoaded or SimulatorStatus.Pending)
            SimulatorNotice = $"Waiting for your answer about {request.Extension.Id}'s preview module.";
        _creating = true;
        SimulationResult result;
        try
        {
            var create = host.CreateAsync(request, _closed.Token);
            OnQuestionsChanged();
            result = await create;
        }
        finally
        {
            if (generation == _simulationGeneration)
                _creating = false;
        }
        if (generation != _simulationGeneration || _disposed)
        {
            result.Simulation?.Dispose();
            return;
        }
        Apply(result);
    }

    private void Apply(SimulationResult result)
    {
        if (result.Simulation is { } simulation)
        {
            _simulation = simulation;
            Driver.Simulation = simulation;
            Trace.Clear();
            SimulatorNotice = null;
            CanLoadModule = false;
            CanFire = true;
            ApplyFrame();
            Wake();
            return;
        }
        SimulatorNotice = result.Message ?? $"The {_request?.Extension.Id} preview module isn't simulating this weapon.";
        CanLoadModule = result.Status is SimulatorStatus.Declined or SimulatorStatus.Failed or SimulatorStatus.NotLoaded;
    }

    private void DropSimulation()
    {
        Driver.Simulation = null;
        _simulation?.Dispose();
        _simulation = null;
        CanFire = false;
        MotionNote = null;
        _rawMotionNote = null;
        Trace.Clear();
        ApplyFrame();
    }

    /// <summary>Cancelled when the preview closes: a module question it alone was waiting on is withdrawn.</summary>
    private readonly CancellationTokenSource _closed = new();

    private SimulatorHost Host()
    {
        if (_host is null)
        {
            _host = _simulators();
            _host.StatusChanged += OnModuleStatusChanged;
        }
        return _host;
    }

    /// <summary>The module was loaded elsewhere (another preview's "Load module…"), or turned off: follow it.</summary>
    private void OnModuleStatusChanged(string extensionId)
    {
        if (_disposed || _creating || _request is not { } request || !request.Extension.Id.Equals(extensionId, StringComparison.OrdinalIgnoreCase))
            return;
        if (_host!.StatusOf(request.Extension) == SimulatorStatus.TurnedOff)
        {
            DropSimulation();
            CanLoadModule = false;
            SimulatorNotice = _host.MessageOf(request.Extension);
            return;
        }
        if (_simulation is null)
            _ = RecreateSimulationAsync();
    }

    /// <summary>The module's question opened or closed somewhere: show it here while it is about this preview's module.</summary>
    private void OnQuestionsChanged()
    {
        if (_disposed)
            return;
        Question = _request is { } r ? _questions?.For(r.Extension.Id) : null;
    }

    /// <summary>"Load module…": forget a "Don't load" (or a failed load) and ask again.</summary>
    [RelayCommand]
    private async Task LoadModule()
    {
        if (_request is not { } request)
            return;
        _creating = true;
        try
        {
            var ask = Host().ReconsiderAsync(request.Extension, _closed.Token);
            OnQuestionsChanged();
            await ask;
        }
        finally
        {
            _creating = false;
        }
        if (!_disposed)
            await RecreateSimulationAsync();
    }

    // ── Input ────────────────────────────────────────────────────────────

    /// <summary>The trigger goes down (Space, or the Fire button pressed): a round now, then one per fire time.</summary>
    public void PressTrigger()
    {
        if (!CanFire)
            return;
        Driver.PressTrigger();
        Wake();
    }

    public void ReleaseTrigger() => Driver.ReleaseTrigger();

    /// <summary>One round (Enter on the Fire button, or the command palette).</summary>
    public void FireOnce()
    {
        PressTrigger();
        ReleaseTrigger();
    }

    [RelayCommand]
    private void ToggleAds() => IsAds = !IsAds;

    [RelayCommand]
    private void Hip() => IsAds = false;

    [RelayCommand]
    private void Ads() => IsAds = true;

    partial void OnIsAdsChanged(bool value)
    {
        Driver.AimDownSights = value;
        Wake();
    }

    /// <summary>Back to a fresh burst at rest (R), the overlay and readout cleared.</summary>
    [RelayCommand(CanExecute = nameof(CanFire))]
    private void Reset()
    {
        Driver.Reset();
        Trace.Clear();
        ApplyFrame();
    }

    // ── Frames ───────────────────────────────────────────────────────────

    private readonly List<TopLevel?> _views = new();
    private TopLevel? _frameSource;
    private int _frameGeneration;
    private bool _frameRequested;
    private readonly System.Diagnostics.Stopwatch _clock = new();

    /// <summary>True while display frames are being asked for (something moves).</summary>
    public bool IsTicking { get; private set; }

    /// <summary>Frames stepped so far (verification: an idle preview doesn't add to it).</summary>
    public long FramesStepped { get; private set; }

    public void ViewAttached(TopLevel? frameSource)
    {
        _views.Add(frameSource);
        UseFrameSource(frameSource);
    }

    /// <summary>A view let go of the preview; with none left the trigger is let go and the frames stop.</summary>
    public void ViewDetached(TopLevel? frameSource = null)
    {
        var at = _views.LastIndexOf(frameSource);
        if (at >= 0)
            _views.RemoveAt(at);
        else if (_views.Count > 0)
            _views.RemoveAt(_views.Count - 1);
        if (_views.Count > 0)
        {
            UseFrameSource(_views[^1]);
            return;
        }
        Dispatcher.UIThread.Post(() =>
        {
            if (_views.Count > 0 || _disposed)
                return;
            Driver.ReleaseTrigger();
            UseFrameSource(null);
        }, DispatcherPriority.Background);
    }

    private void UseFrameSource(TopLevel? source)
    {
        if (ReferenceEquals(source, _frameSource))
            return;
        if (_frameSource is Window old)
            old.PropertyChanged -= FrameWindow_PropertyChanged;
        _frameSource = source;
        if (_frameSource is Window window)
            window.PropertyChanged += FrameWindow_PropertyChanged;
        _frameGeneration++;
        _frameRequested = false;
        IsTicking = false;
        Wake();
    }

    /// <summary>A minimised window shows nothing, so nothing is stepped (a held trigger included) until it is restored.</summary>
    private bool IsMinimised => _frameSource is Window { WindowState: WindowState.Minimized };

    private void FrameWindow_PropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.WindowStateProperty && !IsMinimised)
        {
            _clock.Restart();
            Wake();
        }
    }

    /// <summary>Starts asking for frames when something moves and nothing is asking yet.</summary>
    private void Wake()
    {
        if (IsTicking || _disposed || _frameSource is null || IsMinimised || !Driver.NeedsFrames)
            return;
        IsTicking = true;
        _clock.Restart();
        RequestFrame();
    }

    private void RequestFrame()
    {
        if (_frameRequested || _frameSource is null)
            return;
        _frameRequested = true;
        var generation = _frameGeneration;
        _frameSource.RequestAnimationFrame(_ =>
        {
            if (generation != _frameGeneration || _disposed)
                return;
            _frameRequested = false;
            if (IsMinimised)
            {
                IsTicking = false;
                return;
            }
            var seconds = (float)_clock.Elapsed.TotalSeconds;
            _clock.Restart();
            var more = Driver.Step(seconds);
            FramesStepped++;
            if (!more)
                Trace.Rest();
            ApplyFrame();
            if (more)
                RequestFrame();
            else
                IsTicking = false;
        });
    }

    private string? _rawMotionNote;
    private int _shownTrace = -1;

    /// <summary>The driver's state onto the viewport: the kick (supported parts only), the pose's ADS mix, the readout.</summary>
    private void ApplyFrame()
    {
        var frame = Driver.Frame;
        Kick = Driver.Simulation is null || !frame.Ok ? null : KickOf(frame) is { IsZero: false } k ? k : null;
        if (_blend is not null && _blend.Weight != Driver.Ads)
        {
            _blend.Weight = Driver.Ads;
            Pose?.Refresh();
        }
        if (Driver.Simulation is not null && NoteOf(frame) is var note && note != _rawMotionNote)
        {
            _rawMotionNote = note;
            MotionNote = note;
        }
        if (Trace.Version != _shownTrace)
        {
            _shownTrace = Trace.Version;
            ShowReadout();
        }
    }

    private void ShowReadout()
    {
        MeasureNote = Trace.ViewSupported ? null : NoViewKick;
        ShotsText = Count(Trace.Shots);
        if (!Trace.HasBurst || !Trace.ViewSupported)
        {
            ClimbText = DriftText = SettleText = NoValue;
            return;
        }
        ClimbText = Degrees(Trace.Climb);
        DriftText = DriftOf(Trace.Drift);
        SettleText = Trace.SettleMs is { } ms ? Milliseconds(ms) : Trace.DoesNotReturn ? "doesn't return" : NoValue;
    }

    // The readout changes every frame of a burst: its texts are made once and reused, so a frame allocates none.
    private static readonly string?[] DegreeTexts = new string?[10_000], LeftTexts = new string?[10_000], RightTexts = new string?[10_000];
    private static readonly string?[] MsTexts = new string?[10_000], CountTexts = new string?[10_000];

    private static string Cached(string?[] cache, int i, Func<int, string> make) =>
        i >= 0 && i < cache.Length ? cache[i] ??= make(i) : make(i);

    /// <summary>"3.2°", to a tenth.</summary>
    public static string Degrees(float degrees) =>
        Cached(DegreeTexts, (int)MathF.Round(MathF.Max(0f, degrees) * 10f), t => (t / 10.0).ToString("0.0", CultureInfo.InvariantCulture) + "°");

    /// <summary>"0.8° left" (positive yaw), "0.8° right", or "0.0°".</summary>
    public static string DriftOf(float yaw)
    {
        var tenths = (int)MathF.Round(MathF.Abs(yaw) * 10f);
        if (tenths == 0)
            return Degrees(0f);
        return yaw > 0
            ? Cached(LeftTexts, tenths, t => (t / 10.0).ToString("0.0", CultureInfo.InvariantCulture) + "° left")
            : Cached(RightTexts, tenths, t => (t / 10.0).ToString("0.0", CultureInfo.InvariantCulture) + "° right");
    }

    public static string Milliseconds(float ms) =>
        Cached(MsTexts, (int)MathF.Round(ms), m => m.ToString(CultureInfo.InvariantCulture) + " ms");

    private static string Count(int n) => Cached(CountTexts, n, c => c.ToString(CultureInfo.InvariantCulture));

    /// <summary>The frame as the viewport draws it: a part the module didn't compute is not drawn, whatever it holds.</summary>
    public static PreviewKick KickOf(SimulatorFrame frame) => new(
        frame.Supported.HasFlag(SimulatorParts.ViewAngles) ? frame.ViewAngles : default,
        frame.Supported.HasFlag(SimulatorParts.ViewOrigin) ? frame.ViewOrigin : default,
        frame.Supported.HasFlag(SimulatorParts.GunAngles) ? frame.GunAngles : default,
        frame.Supported.HasFlag(SimulatorParts.GunOrigin) ? frame.GunOrigin : default);

    /// <summary>What to say beside the preview about a step: the module's note as it wrote it, else the parts it left
    /// out; null for a step that computed everything (or none yet).</summary>
    public static string? NoteOf(SimulatorFrame frame) =>
        !string.IsNullOrEmpty(frame.Note) ? frame.Note
        : !frame.Ok || frame.Supported == SimulatorParts.All ? null
        : NotSimulated[(int)(SimulatorParts.All & ~frame.Supported)] ??= $"Not simulated: {Describe(SimulatorParts.All & ~frame.Supported)}.";

    // Asked every frame: one text per combination of parts left out.
    private static readonly string?[] NotSimulated = new string?[(int)SimulatorParts.All + 1];

    private static string Describe(SimulatorParts parts)
    {
        var names = new List<string>();
        if (parts.HasFlag(SimulatorParts.ViewAngles))
            names.Add("view angles");
        if (parts.HasFlag(SimulatorParts.ViewOrigin))
            names.Add("view origin");
        if (parts.HasFlag(SimulatorParts.GunAngles))
            names.Add("gun angles");
        if (parts.HasFlag(SimulatorParts.GunOrigin))
            names.Add("gun origin");
        return string.Join(", ", names);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _cts?.Cancel();
        if (_questions is not null)
            _questions.Changed -= OnQuestionsChanged;
        _watch?.Dispose();
        _watch = null;
        Question = null;
        _closed.Cancel();
        _loading.End();
        _simulationGeneration++;
        if (_host is not null)
            _host.StatusChanged -= OnModuleStatusChanged;
        Driver.Simulation = null;
        _simulation?.Dispose();
        _simulation = null;
        UseFrameSource(null);
        Pose = null;
        Kick = null;
        Gfx.Clear();
    }
}
