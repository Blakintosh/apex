using Apex.Render.Resources;
using Apex.Render.States;
using Apex.Render.Targets;
using Vortice.Direct3D11;
using Vortice.Mathematics;
using static Apex.Render.States.GfxStateBits;

namespace Apex.Render.Passes;

/// <summary>
/// RENDER_STAGE_LIT_DEFERRED_OPAQUE (<c>Gfx_RenderSceneGBuffer</c>): clears DS0 (depth 0, stencil 0) and the three
/// G-buffer targets to 0, then draws every item's "gbuffer" technique into MRT (RT0 albedo sRGB, RT2 normal/gloss,
/// RT1 reflectance/occlusion — in that order) with DS0. There is no separate depth prepass: this pass writes depth
/// (GREATER_EQUAL) and stamps stencil 255 (ALWAYS/REPLACE) where geometry is, which the sky stage later tests.
/// </summary>
public sealed class GBufferPass
{
    public static StageState State => new(BlendReplace, DepthStencil(true, Compare.LessEqual, Compare.Always, StencilOp.Replace), 255,
        new RasterBits(Cull.Back), RenderTargetCount: 3);

    private readonly RenderContext _rc;
    private readonly SceneDrawer _drawer;

    private readonly Action<Shaders.ShaderBindings> _bindDecal;

    public GBufferPass(RenderContext rc)
    {
        _rc = rc;
        _drawer = new SceneDrawer(rc);
        _bindDecal = BindDecal;
    }

    public int Render(IReadOnlyList<IPreviewDrawItem> items, GpuBuffer sceneCb, GpuTexture albedo, GpuTexture normalGloss,
        GpuTexture reflectanceOcclusion, GpuTexture sceneDepth, bool clear = true, bool clearDepth = true)
    {
        var ctx = _rc.Gfx.Context;
        if (clear)
        {
            // The frame clears DS0 itself before the prepass lists (RENDER_STAGE_LIT_FORWARD_SSS_OPAQUE) and passes
            // clearDepth: false; standalone the G-buffer stage starts from a cleared DS0.
            if (clearDepth)
                ctx.ClearDepthStencilView(sceneDepth.Dsv!, DepthStencilClearFlags.Depth | DepthStencilClearFlags.Stencil, 0f, 0);
            ctx.ClearRenderTargetView(albedo.Rtv!, new Color4(0, 0, 0, 0));
            ctx.ClearRenderTargetView(normalGloss.Rtv!, new Color4(0, 0, 0, 0));
            ctx.ClearRenderTargetView(reflectanceOcclusion.Rtv!, new Color4(0, 0, 0, 0));
        }
        return _drawer.Draw(items, PreviewTechnique.Gbuffer, sceneCb,
            [albedo.Rtv!, normalGloss.Rtv!, reflectanceOcclusion.Rtv!], sceneDepth.Dsv,
            new Viewport(0, 0, albedo.Width, albedo.Height, 0f, 1f), State);
    }

    /// <summary>
    /// "GBuffer Decal" (RENDER_STAGE_LIT_DEFERRED_DECAL, <c>Gfx_RenderSceneGBuffer</c> → sub_1405669D0 with list 6), only
    /// when a deferred decal is drawn: the normal/gloss target is copied into RT3, which the decal shaders read as t55
    /// <c>gResolvedNormalTexture</c> (the surface's normal encoding basis, and whether anything is under the decal at
    /// all), then the decals' "gbuffer" technique blends into the same MRT with DS0 bound read-only and sampled at t43.
    /// </summary>
    public int RenderDecals(IReadOnlyList<IPreviewDrawItem> items, GpuBuffer sceneCb, GpuTexture albedo, GpuTexture normalGloss,
        GpuTexture reflectanceOcclusion, GpuTexture sceneDepth)
    {
        if (!SceneDrawer.Draws(items, PreviewTechnique.GbufferDecal))
            return 0;
        var resolvedNormal = _rc.Targets[RenderTargetId.GBufferExtra];
        _rc.Gfx.Context.CopyResource(resolvedNormal.Resource, normalGloss.Resource);
        (_resolvedNormal, _sceneDepth) = (resolvedNormal.Srv, sceneDepth.Srv);
        int n = _drawer.Draw(items, PreviewTechnique.GbufferDecal, sceneCb,
            [albedo.Rtv!, normalGloss.Rtv!, reflectanceOcclusion.Rtv!], _rc.Targets.SceneDepthReadOnlyDsv,
            new Viewport(0, 0, albedo.Width, albedo.Height, 0f, 1f), State, _bindDecal);
        (_resolvedNormal, _sceneDepth) = (null, null);
        return n;
    }

    private ID3D11ShaderResourceView? _resolvedNormal, _sceneDepth;

    private void BindDecal(Shaders.ShaderBindings b)
    {
        b.TrySetResource("gResolvedNormalTexture", _resolvedNormal);
        b.TrySetResource("gDepthTexture", _sceneDepth);
    }
}

/// <summary>
/// Forward stages into the HDR lit buffer RT33 with DS0: RENDER_STAGE_EMISSIVE_SKY (the SSI skybox model centred on
/// the camera, drawn only where the G-buffer left stencil ≠ 255, depth GREATER_EQUAL without write), and the emissive /
/// lit-forward opaque lists (absent in the captured preview content; same depth state with write, no stencil test).
/// The HDR target is cleared to APE's scene clear colour (0.3, 0.4, 0.5, 0) before the sky.
/// </summary>
public sealed class ForwardPass
{
    public static readonly Color4 HdrClearColor = new(0.3f, 0.4f, 0.5f, 0f);

    public static StageState SkyState => new(BlendReplace, DepthStencil(false, Compare.LessEqual, Compare.NotEqual, StencilOp.Keep), 255,
        new RasterBits(Cull.Back));
    public static StageState OpaqueState => new(BlendReplace, DepthStencil(true, Compare.LessEqual), 0, new RasterBits(Cull.Back));

    /// <summary>RENDER_STAGE_TRANSPARENT through OIT (fx capture): fragments go to the PS UAVs; RT29 gets the
    /// fallback accumulation with colour DestAlpha·src + dst, alpha 0·src + (1 − srcA)·dst; depth test GREATER_EQUAL
    /// without write, cull back.</summary>
    public static StageState TransparentState => new(Blend(WriteRgba, BlendFactor.DestAlpha, BlendFactor.One, BlendFactor.Zero, BlendFactor.InvSrcAlpha),
        DepthStencil(false, Compare.LessEqual), 0, new RasterBits(Cull.Back));

    private readonly RenderContext _rc;
    private readonly SceneDrawer _drawer;

    public ForwardPass(RenderContext rc)
    {
        _rc = rc;
        _drawer = new SceneDrawer(rc);
    }

    public void ClearHdr(GpuTexture hdr) => _rc.Gfx.Context.ClearRenderTargetView(hdr.Rtv!, HdrClearColor);

    public int RenderSky(IReadOnlyList<IPreviewDrawItem> skyItems, GpuBuffer sceneCb, GpuTexture hdr, GpuTexture sceneDepth)
        => _drawer.Draw(skyItems, PreviewTechnique.Emissive, sceneCb, [hdr.Rtv!], sceneDepth.Dsv,
            new Viewport(0, 0, hdr.Width, hdr.Height, 0f, 1f), SkyState);

    /// <summary>The Prepass stage's lit-forward opaque lists: "depth prepass" into DS0 (and RT33), as the SSS prepass.</summary>
    public int RenderPrepass(IReadOnlyList<IPreviewDrawItem> items, GpuBuffer sceneCb, GpuTexture hdr, GpuTexture sceneDepth)
        => _drawer.Draw(items, PreviewTechnique.LitPrepass, sceneCb, [hdr.Rtv!], sceneDepth.Dsv,
            new Viewport(0, 0, hdr.Width, hdr.Height, 0f, 1f), SssPass.PrepassState);

    public int Render(IReadOnlyList<IPreviewDrawItem> items, PreviewTechnique technique, GpuBuffer sceneCb, GpuTexture hdr,
        GpuTexture sceneDepth, Action<Shaders.ShaderBindings>? bindPassResources = null)
        => _drawer.Draw(items, technique, sceneCb, [hdr.Rtv!], sceneDepth.Dsv,
            new Viewport(0, 0, hdr.Width, hdr.Height, 0f, 1f), OpaqueState, bindPassResources);
}
