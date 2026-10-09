using System.Runtime.InteropServices;
using Apex.Render.Constants;
using Apex.Render.Resources;
using Apex.Render.States;
using Apex.Render.Targets;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using static Apex.Render.States.GfxStateBits;

namespace Apex.Render.Passes;

/// <summary>
/// "sun shadow dynamics (0..2)" (<c>Gfx_RenderSunShadows</c> 0x14060fe10): three orthographic cascades rendered with
/// each item's "build shadowmap depth" technique into lighting RT2 (R16_UNORM 1024² × 3, one RTV per slice, the
/// shader writes depth into colour) over a shared D16 depth buffer. Per cascade: clear the slice to (0,0,0,1) and the
/// depth to 0 (reversed Z), upload that cascade's view as b9, draw. Captured state: blend off, depth GREATER + write,
/// no stencil, cull back, depth clip off, shadow-map depth bias (-4 / -4). The array is then bound as t54
/// <c>gSunShadowmapArray</c>.
/// </summary>
public sealed class SunShadowPass : IDisposable
{
    public const int CascadeCount = 3;
    public const int Resolution = 1024;

    public static StageState State => new(BlendReplace, DepthStencil(true, Compare.Less), 0, RasterBits.SunShadow);

    private readonly RenderContext _rc;
    private readonly SceneDrawer _drawer;
    private readonly ID3D11RenderTargetView[] _sliceRtvs;
    private readonly CascadeInputs[] _rendered = [new(), new(), new()];
    private CascadeInputs _scratch = new();

    /// <summary>Lighting RT2: the cascade array (SRV = t54).</summary>
    public GpuTexture ShadowArray { get; }
    public GpuTexture Depth { get; }

    public SunShadowPass(RenderContext rc)
    {
        _rc = rc;
        _drawer = new SceneDrawer(rc);
        ShadowArray = GpuTexture.CreateTarget(rc.Gfx, RenderTargetTable.LightingTarget(2), "lighting RT2 sun shadow cascades");
        Depth = GpuTexture.CreateTarget(rc.Gfx, RenderTargetTable.SunShadowDepth, "sun shadow depth D16");
        _sliceRtvs = new ID3D11RenderTargetView[CascadeCount];
        for (int i = 0; i < CascadeCount; i++)
            _sliceRtvs[i] = ShadowArray.CreateSliceRtv(rc.Gfx, Format.R16_UNorm, i);
    }

    public ID3D11ShaderResourceView Srv => ShadowArray.Srv!;

    /// <summary>Renders the cascades; <paramref name="cascadeViews"/> are the three shadow-view b9 contents
    /// (<see cref="Scene.SceneConstantsBuilder.BuildSunShadowView"/>).</summary>
    public void Render(IReadOnlyList<IPreviewDrawItem> items, ReadOnlySpan<CodeSceneConsts> cascadeViews)
    {
        if (cascadeViews.Length != CascadeCount)
            throw new ArgumentException($"{CascadeCount} cascade views expected");
        for (int i = 0; i < CascadeCount; i++)
            RenderCascade(items, cascadeViews[i], i);
    }

    /// <summary>
    /// One "sun shadow dynamics (i)" block: clear slice i and the depth, upload the view, draw. Skipped when the view
    /// and the cascade's draws (pass, mesh, range and object constants, in order) are those the slice was last
    /// rendered from — it still holds that result. Draws with bones are never skipped (their buffer changes in place).
    /// </summary>
    public int RenderCascade(IReadOnlyList<IPreviewDrawItem> items, in CodeSceneConsts view, int cascade)
    {
        var inputs = _scratch;
        bool cacheable = inputs.Capture(items, view, cascade);
        var last = _rendered[cascade];
        if (cacheable && last.Valid && last.SameAs(inputs))
            return last.Draws;

        var ctx = _rc.Gfx.Context;
        ctx.ClearRenderTargetView(_sliceRtvs[cascade], new Color4(0, 0, 0, 1));
        ctx.ClearDepthStencilView(Depth.Dsv!, DepthStencilClearFlags.Depth | DepthStencilClearFlags.Stencil, 0f, 0);
        _rc.ShadowSceneCb.Update(ctx, view);
        int n = _drawer.Draw(items, PreviewTechnique.ShadowDepth, _rc.ShadowSceneCb, [_sliceRtvs[cascade]], Depth.Dsv,
            new Viewport(0, 0, Resolution, Resolution, 0f, 1f), State, cascade: cascade);

        last.Valid = false;
        if (cacheable)
        {
            (_rendered[cascade], _scratch) = (inputs, last);
            inputs.Valid = true;
            inputs.Draws = n;
        }
        return n;
    }

    /// <summary>What a cascade slice was rendered from.</summary>
    private sealed class CascadeInputs
    {
        private CodeSceneConsts _view;
        private readonly List<(MaterialPass Pass, MeshGeometry Mesh, DrawRange Range, CodeObjectConsts Object)> _draws = new();
        public bool Valid;
        public int Draws;

        /// <summary>Records the view and the draws <see cref="SceneDrawer"/> will make; false when one has bones.</summary>
        public bool Capture(IReadOnlyList<IPreviewDrawItem> items, in CodeSceneConsts view, int cascade)
        {
            _view = view;
            _draws.Clear();
            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if ((item.ShadowCascadeMask & (1 << cascade)) == 0 || item.GetPass(PreviewTechnique.ShadowDepth) is not { } pass)
                    continue;
                if (!item.Bones.IsEmpty)
                    return false;
                _draws.Add((pass, item.Mesh, item.ShadowRange(cascade) ?? item.Range, item.ObjectConsts));
            }
            return true;
        }

        public bool SameAs(CascadeInputs other)
        {
            if (!Bytes(_view).SequenceEqual(Bytes(other._view)) || _draws.Count != other._draws.Count)
                return false;
            for (int i = 0; i < _draws.Count; i++)
            {
                var (a, b) = (_draws[i], other._draws[i]);
                if (a.Pass != b.Pass || a.Mesh != b.Mesh || a.Range != b.Range || !Bytes(a.Object).SequenceEqual(Bytes(b.Object)))
                    return false;
            }
            return true;
        }

        private static ReadOnlySpan<byte> Bytes<T>(in T value) where T : unmanaged
            => MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in value));
    }

    public void Dispose()
    {
        foreach (var r in _sliceRtvs)
            r.Dispose();
        ShadowArray.Dispose();
        Depth.Dispose();
    }
}
