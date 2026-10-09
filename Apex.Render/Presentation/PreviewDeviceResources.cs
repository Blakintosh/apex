using Apex.Render.Assets;
using Apex.Render.Data.Lighting;
using Apex.Render.Device;
using Apex.Render.Passes;
using Apex.Render.Scene;
using Avalonia.Threading;

namespace Apex.Render.Presentation;

/// <summary>
/// The device-bound half of the preview shared by every viewport on one <see cref="GfxDevice"/>: the
/// <see cref="RenderContext"/> (state/layout/shader caches, window-sized targets, engine constant buffers), the
/// <see cref="PreviewFrameRenderer"/> passes, per lighting state the LED lighting textures and the skybox model, and the
/// GPU copies of preview models (reference counted; the <see cref="IdleModelCapacity"/> most recently released stay
/// resident so switching back to a tab does not re-upload).
/// Models and skyboxes are created on worker threads (device objects only — the device is free-threaded, the immediate
/// context is never touched there) and handed over on the UI thread.
/// Created on first use and owned by the device (<see cref="GfxDevice.Own{T}"/>), so it goes away with it — on device
/// loss, or when the last viewport releases a shared device. UI thread only.
/// </summary>
public sealed class PreviewDeviceResources : IDisposable
{
    private static readonly Dictionary<(GfxDevice, PreviewEnvironment), PreviewDeviceResources> s_byDevice = new();

    private readonly Dictionary<PreviewLightState, LedLightingResources> _lighting = new();
    private readonly Dictionary<PreviewLightState, ModelEntry?> _skies = new();
    private readonly HashSet<PreviewLightState> _skiesBuilding = new();
    private readonly Dictionary<PreparedPreviewModel, ModelEntry> _models = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<PreparedPreviewModel, List<TaskCompletionSource<PreviewModel>>> _building = new(ReferenceEqualityComparer.Instance);
    private readonly LinkedList<PreparedPreviewModel> _idleModels = new();
    private bool _disposed;

    /// <summary>Unreferenced preview models kept on the GPU.</summary>
    public const int IdleModelCapacity = 3;

    private sealed class ModelEntry(PreviewModel model, MaterialPassFactory passes)
    {
        public PreviewModel Model { get; } = model;
        public MaterialPassFactory Passes { get; } = passes;
        public int Refs;

        public void Dispose()
        {
            Model.Dispose();
            Passes.Dispose();
        }
    }

    public GfxDevice Gfx { get; }
    public PreviewEnvironment Environment { get; }
    public RenderContext Rc { get; }
    public PreviewFrameRenderer Frame { get; }

    /// <summary>A skybox finished loading on the GPU (<see cref="Sky"/> returns it now): draw again.</summary>
    public event Action? SkyReady;

    private PreviewDeviceResources(GfxDevice gfx, PreviewEnvironment env)
    {
        Gfx = gfx;
        Environment = env;
        Rc = new RenderContext(gfx, env.Data.Shaders);
        Frame = new PreviewFrameRenderer(Rc);
    }

    /// <summary>The resources of <paramref name="gfx"/> for <paramref name="env"/>, created on first use.</summary>
    public static PreviewDeviceResources For(GfxDevice gfx, PreviewEnvironment env)
    {
        if (s_byDevice.TryGetValue((gfx, env), out var r) && !r._disposed)
            return r;
        r = new PreviewDeviceResources(gfx, env);
        s_byDevice[(gfx, env)] = r;
        gfx.Own(r);
        return r;
    }

    /// <summary>t21–t54 lighting inputs of a state (GPU copies of the LED data).</summary>
    public SceneLightingResources Lighting(PreviewLightState state)
    {
        if (!_lighting.TryGetValue(state, out var l))
            _lighting[state] = l = new LedLightingResources(Rc, Environment.State(state), Environment.EnvBrdf);
        return l.Resources;
    }

    /// <summary>The state's skybox model on this device; null when it could not be prepared or is still being
    /// prepared (<see cref="PreviewEnvironment.SkyAsync"/>) or uploaded (<see cref="SkyReady"/> follows) — this never
    /// blocks on the CPU work.</summary>
    public PreviewModel? Sky(PreviewLightState state)
    {
        if (_skies.TryGetValue(state, out var sky))
            return sky?.Model;
        if (!Environment.IsSkyLoaded(state) || !_skiesBuilding.Add(state))
            return null;
        var (prepared, _) = Environment.Sky(state);
        if (prepared is null)
        {
            _skiesBuilding.Remove(state);
            _skies[state] = null;
            return null;
        }
        Build(prepared, t =>
        {
            _skiesBuilding.Remove(state);
            if (!t.IsCompletedSuccessfully)
            {
                _skies[state] = null;
                return;
            }
            _skies[state] = t.Result;
            prepared.ReleaseImageData();
            SkyReady?.Invoke();
        });
        return null;
    }

    /// <summary>
    /// The GPU copy of <paramref name="prepared"/>: at once when resident, else created on a worker thread and then
    /// shared. The task completes on the UI thread (continuations registered with
    /// <see cref="TaskContinuationOptions.ExecuteSynchronously"/> run there). Pair each successful acquire with
    /// <see cref="ReleaseModel"/>, also when the caller no longer wants the model. After the first upload the prepared
    /// model's image data is released to save memory.
    /// </summary>
    public Task<PreviewModel> AcquireModelAsync(PreparedPreviewModel prepared)
    {
        if (TryAcquireResident(prepared) is { } resident)
            return Task.FromResult(resident);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var done = new TaskCompletionSource<PreviewModel>();
        if (_building.TryGetValue(prepared, out var waiters))
        {
            waiters.Add(done);
            return done.Task;
        }
        waiters = [done];
        _building[prepared] = waiters;
        Build(prepared, t =>
        {
            _building.Remove(prepared);
            if (!t.IsCompletedSuccessfully)
            {
                foreach (var w in waiters)
                    w.SetException(t.Exception?.InnerException ?? new TaskCanceledException());
                return;
            }
            var entry = t.Result;
            entry.Refs = waiters.Count;
            _models[prepared] = entry;
            prepared.ReleaseImageData();
            foreach (var w in waiters)
                w.SetResult(entry.Model);
        });
        return done.Task;
    }

    private PreviewModel? TryAcquireResident(PreparedPreviewModel prepared)
    {
        if (!_models.TryGetValue(prepared, out var e))
            return null;
        if (e.Refs++ == 0)
            _idleModels.Remove(prepared);
        return e.Model;
    }

    private ModelEntry CreateEntry(PreparedPreviewModel prepared, MaterialPassFactory passes)
    {
        try
        {
            return new ModelEntry(new PreviewModelLoader(Rc, Environment.Data, passes).Create(prepared), passes);
        }
        catch
        {
            passes.Dispose();
            throw;
        }
    }

    /// <summary>Creates <paramref name="prepared"/>'s entry on a worker and calls <paramref name="completed"/> with it on
    /// the UI thread. The device is kept from being disposed meanwhile; an entry finished after this was disposed is
    /// dropped (<paramref name="completed"/> then sees a cancelled task).</summary>
    private void Build(PreparedPreviewModel prepared, Action<Task<ModelEntry>> completed)
    {
        Gfx.BeginBackgroundWork();
        var passes = new MaterialPassFactory(Rc, Environment.Data);
        Task.Run(() => CreateEntry(prepared, passes)).ContinueWith(t => Dispatcher.UIThread.Post(() =>
        {
            try
            {
                if (_disposed)
                {
                    if (t.IsCompletedSuccessfully)
                        t.Result.Dispose();
                    completed(Task.FromCanceled<ModelEntry>(new CancellationToken(true)));
                }
                else
                {
                    completed(t);
                }
            }
            finally
            {
                Gfx.EndBackgroundWork();
            }
        }), TaskScheduler.Default);
    }

    /// <summary>Drops a reference taken by <see cref="AcquireModelAsync"/>; the model stays resident while it is among the
    /// <see cref="IdleModelCapacity"/> most recently released.</summary>
    public void ReleaseModel(PreparedPreviewModel prepared)
    {
        if (!_models.TryGetValue(prepared, out var e) || --e.Refs > 0)
            return;
        _idleModels.AddFirst(prepared);
        while (_idleModels.Count > IdleModelCapacity)
        {
            var old = _idleModels.Last!.Value;
            _idleModels.RemoveLast();
            _models.Remove(old, out var evicted);
            evicted?.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        s_byDevice.Remove((Gfx, Environment));
        foreach (var m in _models.Values)
            m.Dispose();
        _models.Clear();
        _idleModels.Clear();
        foreach (var s in _skies.Values)
            s?.Dispose();
        _skies.Clear();
        foreach (var l in _lighting.Values)
            l.Dispose();
        _lighting.Clear();
        Frame.Dispose();
        Rc.Dispose();
    }
}
