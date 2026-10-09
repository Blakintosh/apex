using Apex.Render.Device;
using Apex.Render.Resources;

namespace Apex.Render.Targets;

/// <summary>
/// Window-sized render targets from <see cref="RenderTargetTable"/>, created on first use and dropped on resize
/// (so a pass that never runs never allocates — the preview only touches ~30 of the 85 entries).
/// Viewports sharing a device each keep their own set (<see cref="Resize(object, int, int)"/>), so two previews of
/// different sizes do not reallocate every target on every frame; the indexers serve the active set.
/// </summary>
public sealed class RenderTargetPool : IDisposable
{
    private sealed class TargetSet(int width, int height) : IDisposable
    {
        public readonly int Width = width, Height = height;
        public readonly GpuTexture?[] Targets = new GpuTexture?[RenderTargetId.Count];
        public GpuTexture? SceneDepth, SceneDepthCopy;
        public Vortice.Direct3D11.ID3D11DepthStencilView? SceneDepthReadOnly;

        public void Dispose()
        {
            SceneDepthReadOnly?.Dispose();
            SceneDepthCopy?.Dispose();
            foreach (var t in Targets)
                t?.Dispose();
            SceneDepth?.Dispose();
        }
    }

    private readonly GfxDevice _gfx;
    private readonly Dictionary<object, TargetSet> _sets = new(ReferenceEqualityComparer.Instance);
    private TargetSet? _active;

    public int Width => _active?.Width ?? 0;
    public int Height => _active?.Height ?? 0;

    public RenderTargetPool(GfxDevice gfx) => _gfx = gfx;

    /// <summary>Sets the window size; returns true (and releases every target) when it changed.</summary>
    public bool Resize(int width, int height) => Resize(this, width, height);

    /// <summary>Makes <paramref name="owner"/>'s set active at <paramref name="width"/>×<paramref name="height"/>;
    /// returns true (and releases that set's targets) when its size changed. Pair with <see cref="Release"/>.</summary>
    public bool Resize(object owner, int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        if (_sets.TryGetValue(owner, out var set) && set.Width == width && set.Height == height)
        {
            _active = set;
            return false;
        }
        set?.Dispose();
        _sets[owner] = _active = new TargetSet(width, height);
        return true;
    }

    /// <summary>Drops <paramref name="owner"/>'s targets.</summary>
    public void Release(object owner)
    {
        if (!_sets.Remove(owner, out var set))
            return;
        if (ReferenceEquals(set, _active))
            _active = null;
        set.Dispose();
    }

    public GpuTexture this[int id] => Get(id);

    public GpuTexture Get(int id)
    {
        var set = Active;
        return set.Targets[id] ??= GpuTexture.CreateTarget(_gfx, RenderTargetTable.Get(id).DescFor(set.Width, set.Height), $"RT{id} {RenderTargetTable.Get(id).Role}");
    }

    public bool IsAllocated(int id) => _active?.Targets[id] != null;

    /// <summary>DS0 (D32_FLOAT_S8X24_UINT, SRV R32_FLOAT_X8X24), window size.</summary>
    public GpuTexture SceneDepth
    {
        get
        {
            var set = Active;
            return set.SceneDepth ??= GpuTexture.CreateTarget(_gfx, RenderTargetTable.SceneDepth(set.Width, set.Height), "DS0 scene depth");
        }
    }

    /// <summary>Copy of DS0 taken after the sky stage (CopyResource), bound to lit-forward shaders as t43
    /// <c>gDepthTexture</c> while DS0 itself is the depth target.</summary>
    public GpuTexture SceneDepthCopy
    {
        get
        {
            var set = Active;
            return set.SceneDepthCopy ??= GpuTexture.CreateTarget(_gfx, RenderTargetTable.SceneDepth(set.Width, set.Height), "DS0 copy");
        }
    }

    /// <summary>DS0 with read-only depth and stencil (tested while DS0 is also sampled).</summary>
    public Vortice.Direct3D11.ID3D11DepthStencilView SceneDepthReadOnlyDsv
    {
        get
        {
            var set = Active;
            return set.SceneDepthReadOnly ??= SceneDepth.CreateReadOnlyDsv(_gfx, RenderTargetTable.SceneDepth(set.Width, set.Height).DsvFormat);
        }
    }

    private TargetSet Active => _active ?? throw new InvalidOperationException("Resize must be called before targets are requested");

    /// <summary>Releases every set's targets.</summary>
    public void ReleaseAll()
    {
        foreach (var set in _sets.Values)
            set.Dispose();
        _sets.Clear();
        _active = null;
    }

    public void Dispose() => ReleaseAll();
}
