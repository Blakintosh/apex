using Apex.Render.Constants;
using Apex.Render.Resources;
using Apex.Render.Targets;
using Vortice.Direct3D11;
using Vortice.Mathematics;

namespace Apex.Render.Passes;

/// <summary>Per-frame inputs of <see cref="PreviewFrameRenderer"/>.</summary>
public sealed class PreviewFrameInputs
{
    /// <summary>Main-view b9 (<see cref="Scene.SceneConstantsBuilder.Build"/>).</summary>
    public required CodeSceneConsts Scene { get; init; }
    /// <summary>Three sun-shadow view b9s (<see cref="Scene.SceneConstantsBuilder.BuildSunShadowViews"/>); null = sun
    /// disabled (the cascade array is still cleared so t54 is defined).</summary>
    public CodeSceneConsts[]? SunShadowViews { get; init; }
    /// <summary>Model draws; each is drawn by every stage whose technique its material provides.</summary>
    public required IReadOnlyList<IPreviewDrawItem> Items { get; init; }
    /// <summary>RENDER_STAGE_EMISSIVE_SKY draws (the SSI skybox model at the camera).</summary>
    public IReadOnlyList<IPreviewDrawItem> SkyItems { get; init; } = Array.Empty<IPreviewDrawItem>();
    public required SceneLightingResources Lighting { get; init; }
    /// <summary>HemiAO b12 for dispatches 2–5; null = <see cref="HemiAoConstants.ForWindow"/>.</summary>
    public CodeSsaoConsts[]? Ssao { get; init; }
    /// <summary>The viewport's 8-bit (R8G8B8A8_UNORM) target — APE's window back buffer.</summary>
    public required ID3D11RenderTargetView Output { get; init; }
    /// <summary>Write model coverage into the output's alpha after SMAA (APE's post-SMAA depth-to-alpha draw).</summary>
    public bool DepthToAlpha { get; init; } = true;
}

/// <summary>
/// Sequences APE's preview frame (<c>ToolsGfx_PreviewRenderFrame</c>) in the captured marker order:
/// sun shadow dynamics (0–2) → back-buffer clear → DS0 clear → RENDER_STAGE_LIT_FORWARD_SSS_OPAQUE (depth prepass) →
/// RENDER_STAGE_LIT_DEFERRED_OPAQUE → RENDER_STAGE_LIT_DEFERRED_DECAL (only with deferred decals) →
/// Gfx::RenderSSAO_HemiAO → Gfx::RenderLightCulling → HDR clear →
/// RENDER_STAGE_EMISSIVE_SKY (+ emissive / lit-forward opaque lists) → copy DS0 (t43 gDepthTexture) →
/// Gfx::RenderDeferredLighting → RENDER_STAGE_LIT_FORWARD_SSS_OPAQUE + SSS Blur (only with SSS surfaces) →
/// RENDER_STAGE_LIT_FORWARD_DECAL (only with forward decals) → copy RT33→RT34 → Gfx::PrepareOIT → RENDER_STAGE_TRANSPARENT → Gfx::RenderOIT (→ RT35) → ExposureDownscale → TonemapLUT
/// → SMAA (→ output) → depth-to-alpha. Bloom, lens flares, volumetrics and distortion are off in the preview (no
/// markers with work in any capture). Steps whose results nothing reads are left out where that cannot change the
/// output (the RT34 copy; the DS0 copy without SSS surfaces; see <see cref="PassCompleted"/>).
/// </summary>
public sealed class PreviewFrameRenderer : IDisposable
{
    public static readonly Color4 BackBufferClearColor = new(0.3f, 0.4f, 0.5f, 0f);

    private readonly RenderContext _rc;
    private readonly SunShadowPass _shadow;
    private readonly GBufferPass _gbuffer;
    private readonly HemiAoPass _ssao;
    private readonly LightCullingPass _lightCulling;
    private readonly ForwardPass _forward;
    private readonly DeferredLightingPass _deferred;
    private readonly OitPass _oit;
    private readonly SssPass _sss;
    private readonly SceneDrawer _drawer;
    private readonly ExposureDownscalePass _exposure;
    private readonly TonemapPass _tonemap;
    private readonly SmaaPass _smaa;
    private readonly DepthToAlphaPass _depthToAlpha;

    private readonly Action<Shaders.ShaderBindings> _bindLit, _bindOit;
    // Per-frame inputs of the lit-forward bindings (read by _bindLit / _bindOit).
    private SceneLightingResources? _lighting;
    private ID3D11ShaderResourceView? _lightCull, _probeCull, _decalCull;

    /// <summary>Raised after each pass with its marker name (diagnostics / per-pass readback). Passes that cannot change
    /// the displayed image — ExposureDownscale (never read by the preview) and, without transparent draws, PrepareOIT /
    /// RenderOIT (then an exact copy of RT33) — only run while something is subscribed.</summary>
    public event Action<string>? PassCompleted;

    public SunShadowPass Shadow => _shadow;

    public PreviewFrameRenderer(RenderContext rc)
    {
        _rc = rc;
        _bindLit = BindLit;
        _bindOit = BindOit;
        _shadow = new SunShadowPass(rc);
        _gbuffer = new GBufferPass(rc);
        _ssao = new HemiAoPass(rc);
        _lightCulling = new LightCullingPass(rc);
        _forward = new ForwardPass(rc);
        _deferred = new DeferredLightingPass(rc);
        _oit = new OitPass(rc);
        _sss = new SssPass(rc);
        _drawer = new SceneDrawer(rc);
        _exposure = new ExposureDownscalePass(rc);
        _tonemap = new TonemapPass(rc);
        _smaa = new SmaaPass(rc, SmaaLookupTextures.Area, SmaaLookupTextures.Search);
        _depthToAlpha = new DepthToAlphaPass(rc);
    }

    /// <summary>Renders one frame at the size of <see cref="RenderContext.Targets"/> (call <c>Targets.Resize</c> first).</summary>
    public void Render(PreviewFrameInputs f)
    {
        _rc.BeginFrame();
        try
        {
            RenderFrame(f);
        }
        finally
        {
            _rc.EndFrame();
            _lighting = null;
            _lightCull = _probeCull = _decalCull = null;
        }
    }

    private void RenderFrame(PreviewFrameInputs f)
    {
        var ctx = _rc.Gfx.Context;
        var t = _rc.Targets;
        int w = t.Width, h = t.Height;
        _rc.UploadScene(f.Scene);

        // sun shadow dynamics (0..2)
        if (f.SunShadowViews is { } views)
            _shadow.Render(f.Items, views);
        else
            for (int i = 0; i < SunShadowPass.CascadeCount; i++)
                _shadow.RenderCascade(Array.Empty<IPreviewDrawItem>(), f.Scene, i);
        PassCompleted?.Invoke("sun shadow dynamics");

        ctx.ClearRenderTargetView(f.Output, BackBufferClearColor);

        // Prepass: lit-forward opaque and RENDER_STAGE_LIT_FORWARD_SSS_OPAQUE's "depth prepass" (DS0 cleared first, depth 0 / stencil 0)
        var depth = t.SceneDepth;
        var hdr = t[RenderTargetId.Hdr];
        ctx.ClearDepthStencilView(depth.Dsv!, DepthStencilClearFlags.Depth | DepthStencilClearFlags.Stencil, 0f, 0);
        _forward.RenderPrepass(f.Items, _rc.SceneCb, hdr, depth);
        _sss.RenderPrepass(f.Items, _rc.SceneCb, hdr, depth);
        PassCompleted?.Invoke("Prepass");

        // RENDER_STAGE_LIT_DEFERRED_OPAQUE
        var albedo = t[RenderTargetId.GBufferAlbedo];
        var normal = t[RenderTargetId.GBufferNormalGloss];
        var refl = t[RenderTargetId.GBufferReflectanceOcclusion];
        _gbuffer.Render(f.Items, _rc.SceneCb, albedo, normal, refl, depth, clearDepth: false);
        PassCompleted?.Invoke("RENDER_STAGE_LIT_DEFERRED_OPAQUE");
        if (_gbuffer.RenderDecals(f.Items, _rc.SceneCb, albedo, normal, refl, depth) > 0)
            PassCompleted?.Invoke("RENDER_STAGE_LIT_DEFERRED_DECAL");

        var ao = f.Ssao is { } ssao ? _ssao.Run(depth.Srv!, ssao) : _ssao.Run(depth.Srv!);
        PassCompleted?.Invoke("Gfx::RenderSSAO_HemiAO");

        _lightCulling.Run(_rc.SceneCb, f.Lighting.CullConstants);
        PassCompleted?.Invoke("Gfx::RenderLightCulling");

        _forward.ClearHdr(hdr);
        _forward.RenderSky(f.SkyItems, _rc.SceneCb, hdr, depth);
        PassCompleted?.Invoke("RENDER_STAGE_EMISSIVE_SKY");
        _lighting = f.Lighting;
        _lightCull = t[RenderTargetId.LightCull].Srv!;
        _probeCull = t[RenderTargetId.ProbeCull].Srv!;
        _decalCull = t[RenderTargetId.DecalCull].Srv!;
        _forward.Render(f.Items, PreviewTechnique.Emissive, _rc.SceneCb, hdr, depth);
        _forward.Render(f.Items, PreviewTechnique.Lit, _rc.SceneCb, hdr, depth, _bindLit);
        // Only the SSS stage reads the DS0 copy (t43); RT34, which APE also fills with a copy of RT33 here, is
        // cleared by that stage before use and read by nothing else.
        bool sss = SceneDrawer.Draws(f.Items, PreviewTechnique.Sss);
        var depthCopy = sss ? t.SceneDepthCopy : null;
        if (depthCopy != null)
            ctx.CopyResource(depthCopy.Resource, depth.Resource);

        _deferred.Run(_rc.SceneCb, f.Lighting, albedo.Srv!, normal.Srv!, refl.Srv!, depth.Srv!, ao.Srv!, _shadow.Srv, hdr.Uav!, w, h);
        PassCompleted?.Invoke("Gfx::RenderDeferredLighting");

        // RENDER_STAGE_LIT_FORWARD_SSS_OPAQUE + SSS Blur
        if (depthCopy != null && _sss.Render(f.Items, _rc.SceneCb, f.Scene, _bindLit, depthCopy.Srv!, ao.Srv!) > 0)
            PassCompleted?.Invoke("SSS Blur");

        // RENDER_STAGE_LIT_FORWARD_DECAL: forward decals over the lit surfaces.
        if (_forward.Render(f.Items, PreviewTechnique.LitDecal, _rc.SceneCb, hdr, depth, _bindLit) > 0)
            PassCompleted?.Invoke("RENDER_STAGE_LIT_FORWARD_DECAL");

        // Without transparent draws the fragment counts stay at the prepare pass's zero, and oit_compute then only
        // copies each texel of RT33 into RT35 (count 0: load t2, store u0), so post-processing reads RT33 directly
        // and RT27–29 are never allocated. Diagnostic subscribers get APE's full sequence.
        var resolved = hdr;
        if (PassCompleted != null || SceneDrawer.Draws(f.Items, PreviewTechnique.Oit))
        {
            _oit.Prepare(_rc.SceneCb);
            var accum = t[RenderTargetId.OitAccum];
            _drawer.Draw(f.Items, PreviewTechnique.Oit, _rc.SceneCb, [accum.Rtv!], depth.Dsv, new Viewport(0, 0, w, h, 0f, 1f),
                ForwardPass.TransparentState, _bindOit);
            resolved = t[RenderTargetId.HdrResolved];
            _oit.Resolve(_rc.SceneCb, hdr, resolved);
            PassCompleted?.Invoke("Gfx::RenderOIT");
        }

        if (PassCompleted != null)
        {
            _exposure.Run(resolved.Srv!);
            PassCompleted?.Invoke("ExposureDownscale");
        }

        var ldr = _tonemap.Run(resolved.Srv!);
        PassCompleted?.Invoke("TonemapLUT");

        _smaa.Run(_rc.SceneCb, ldr, f.Output, w, h);
        PassCompleted?.Invoke("SMAA");

        if (f.DepthToAlpha)
            _depthToAlpha.Run(depth.Srv!, f.Output, w, h);
        PassCompleted?.Invoke("DepthToAlpha");
    }

    private void BindLit(Shaders.ShaderBindings b)
        => ForwardLighting.Bind(_rc, b, _lighting!, _shadow.Srv, _lightCull, _probeCull, _decalCull);

    private void BindOit(Shaders.ShaderBindings b)
    {
        BindLit(b);
        b.TrySetUnorderedAccess("gOITFragmentCount", _rc.Targets[RenderTargetId.OitHead].Uav);
        b.TrySetUnorderedAccess("gOITFragmentData", _rc.Targets[RenderTargetId.OitFragments].Uav);
    }

    public void Dispose()
    {
        _shadow.Dispose();
        _ssao.Dispose();
        _deferred.Dispose();
        _sss.Dispose();
        _smaa.Dispose();
    }
}
