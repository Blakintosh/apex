using System.Numerics;
using Apex.Render.Constants;
using Apex.Render.Resources;
using Apex.Render.Shaders;
using Apex.Render.States;
using Apex.Render.Targets;
using Vortice.Direct3D11;
using Vortice.Mathematics;
using static Apex.Render.States.GfxStateBits;

namespace Apex.Render.Passes;

/// <summary>
/// RENDER_STAGE_LIT_FORWARD_SSS_OPAQUE and "SSS Blur" (<c>Gfx_RenderSceneLighting</c>): subsurface-scattering materials
/// (render class 4, e.g. the <c>skin_*</c> techsets) are not in the G-buffer. Their "depth prepass" runs before the
/// G-buffer stage (depth GREATER with write into DS0; the prepass PS also writes RT33, which the HDR clear before the
/// sky discards). After deferred lighting, when any SSS surface is drawn:
/// <list type="number">
/// <item>RT34 and RT35 are cleared to 0 and DS0's stencil to 0;</item>
/// <item>the "lit" technique draws MRT RT33 (lit colour, replaces the deferred result), RT34 (the diffuse light to be
/// scattered) and RT0 (G-buffer albedo, the blur's per-pixel mask/profile), stamping stencil 255 (ALWAYS/REPLACE)
/// with depth GREATER_EQUAL and no write; lit-forward shaders read t43 <c>gDepthTexture</c> = the DS0 copy taken
/// after the sky, and t58 <c>gSSAOTexture</c>;</item>
/// <item><c>skin_hblur_ps</c> blurs RT34 → RT35 (blend One/Zero) and <c>skin_vblur_ps</c> blurs RT35 → RT33 adding
/// onto the lit colour (One/One), both fullscreen quads with stencil EQUAL 255, DS0 bound read-only and sampled as
/// <c>depthTex</c>, point-clamp sampler, b9 = the 2D scene constants and b10 = <see cref="BlurObjectConsts"/>.</item>
/// </list>
/// </summary>
public sealed class SssPass : IDisposable
{
    public static StageState PrepassState => new(BlendReplace, DepthStencil(true, Compare.Less), 0, new RasterBits(Cull.Back));

    public static StageState LitState => new(BlendReplace, DepthStencil(false, Compare.LessEqual, Compare.Always, StencilOp.Replace), 255,
        new RasterBits(Cull.Back), RenderTargetCount: 3);

    /// <summary>skin_hblur blend: One/Zero with blending enabled (word 152736 | RGBA).</summary>
    public static readonly uint HBlurBlend = Blend(WriteRgba, BlendFactor.One, BlendFactor.Zero, BlendFactor.One, BlendFactor.Zero);

    /// <summary>skin_vblur blend: additive One/One (word 169248 | RGBA).</summary>
    public static readonly uint VBlurBlend = Blend(WriteRgba, BlendFactor.One, BlendFactor.One, BlendFactor.One, BlendFactor.One);

    /// <summary>Blur depth-stencil word 160: depth off, stencil EQUAL (ref 255), keep.</summary>
    public static readonly uint BlurDepthStencil = DepthStencil(false, Compare.Disable, Compare.Equal, StencilOp.Keep);

    /// <summary>
    /// cb10 of both blur passes (sub_1404A35D0 with the preview's sceneDesc tanHalfFov 1 and aspect 16/9, the same
    /// constants that give cb9 <c>viewSpaceScaleBias</c>): identity world and
    /// <c>customFloat4s[0] = (0.4 · 0.5 / 1, 10, 1 / (16/9), 1 / (2 · ln 2))</c> — blur width, depth scale,
    /// horizontal aspect, falloff.
    /// </summary>
    public static CodeObjectConsts BlurObjectConsts
    {
        get
        {
            var c = CodeObjectConsts.Identity;
            c.CustomFloat4s[0] = new Vector4(0.2f, 10f, 0.5625f, 1f / MathF.Log(2f) / 2f);
            return c;
        }
    }

    private readonly RenderContext _rc;
    private readonly SceneDrawer _drawer;
    private readonly ShaderBindings _hblur, _vblur;
    private readonly GpuBuffer _blurObjectCb, _blurSceneCb;
    private readonly Action<ShaderBindings> _bindLit;
    // Inputs of _bindLit during Render.
    private Action<ShaderBindings>? _bindLighting;
    private ID3D11ShaderResourceView? _sceneDepthCopy, _ssao;

    public SssPass(RenderContext rc)
    {
        _rc = rc;
        _bindLit = BindLit;
        _drawer = new SceneDrawer(rc);
        var lib = rc.CodeShaders;
        var vs = lib.Get(CodeShaderKeys.SkinBlurVs);
        _hblur = new ShaderBindings(vs, lib.Get(CodeShaderKeys.SkinHBlurPs));
        _vblur = new ShaderBindings(vs, lib.Get(CodeShaderKeys.SkinVBlurPs));
        _blurObjectCb = GpuBuffer.CreateConstant(rc.Gfx, CodeObjectConsts.Size, "cb10 sss blur");
        _blurObjectCb.Update(rc.Gfx.Context, BlurObjectConsts);
        _blurSceneCb = GpuBuffer.CreateConstant(rc.Gfx, CodeSceneConsts.Size, "cb9 2d");
    }

    public ShaderBindings HBlurBindings => _hblur;
    public ShaderBindings VBlurBindings => _vblur;

    /// <summary>The "depth prepass" of the SSS surfaces into DS0 (and RT33); returns the number of draws.</summary>
    public int RenderPrepass(IReadOnlyList<IPreviewDrawItem> items, GpuBuffer sceneCb, GpuTexture hdr, GpuTexture sceneDepth)
        => _drawer.Draw(items, PreviewTechnique.SssPrepass, sceneCb, [hdr.Rtv!], sceneDepth.Dsv,
            new Viewport(0, 0, hdr.Width, hdr.Height, 0f, 1f), PrepassState);

    /// <summary>
    /// The SSS stage after deferred lighting (nothing happens without SSS surfaces). <paramref name="bindLighting"/>
    /// binds the lit-forward code resources (<see cref="ForwardLighting.Bind"/>); t43/t58 are bound here.
    /// </summary>
    public int Render(IReadOnlyList<IPreviewDrawItem> items, GpuBuffer sceneCb, in CodeSceneConsts scene, Action<ShaderBindings> bindLighting,
        ID3D11ShaderResourceView sceneDepthCopy, ID3D11ShaderResourceView ssao)
    {
        if (!SceneDrawer.Draws(items, PreviewTechnique.Sss))
            return 0;
        var ctx = _rc.Gfx.Context;
        var t = _rc.Targets;
        var lit = t[RenderTargetId.Hdr];
        var diffuse = t[RenderTargetId.HdrCopy];
        var blurred = t[RenderTargetId.HdrResolved];
        var albedo = t[RenderTargetId.GBufferAlbedo];
        var depth = t.SceneDepth;
        int w = lit.Width, h = lit.Height;

        ctx.ClearRenderTargetView(diffuse.Rtv!, new Color4(0, 0, 0, 0));
        ctx.ClearRenderTargetView(blurred.Rtv!, new Color4(0, 0, 0, 0));
        ctx.ClearDepthStencilView(depth.Dsv!, DepthStencilClearFlags.Stencil, 0f, 0);

        (_bindLighting, _sceneDepthCopy, _ssao) = (bindLighting, sceneDepthCopy, ssao);
        int n = _drawer.Draw(items, PreviewTechnique.Sss, sceneCb, [lit.Rtv!, diffuse.Rtv!, albedo.Rtv!], depth.Dsv,
            new Viewport(0, 0, w, h, 0f, 1f), LitState, _bindLit);
        (_bindLighting, _sceneDepthCopy, _ssao) = (null, null, null);

        _blurSceneCb.Update(ctx, Scene2D(scene));
        var point = _rc.States.GetSampler(CodeSamplers.PostPointClamp);
        var readOnlyDsv = t.SceneDepthReadOnlyDsv;
        Blur(_hblur, diffuse, blurred, HBlurBlend, point, readOnlyDsv, w, h);
        Blur(_vblur, blurred, lit, VBlurBlend, point, readOnlyDsv, w, h);
        return n;
    }

    private void Blur(ShaderBindings b, GpuTexture source, GpuTexture target, uint blend, ID3D11SamplerState sampler,
        ID3D11DepthStencilView readOnlyDsv, int w, int h)
    {
        var t = _rc.Targets;
        b.TrySetConstantBuffer(CodeBuffer.SceneName, _blurSceneCb);
        b.TrySetConstantBuffer(CodeBuffer.ObjectName, _blurObjectCb);
        b.SetResource("colorTex", source.Srv);
        b.SetResource("depthTex", t.SceneDepth.Srv);
        b.SetResource("albedoTex", t[RenderTargetId.GBufferAlbedo].Srv);
        b.SetSampler("PointSampler", sampler);
        _rc.Quad.Draw(_rc.Gfx, _rc.States, _rc.Layouts, b, [target.Rtv!], w, h, readOnlyDsv, blend, BlurDepthStencil, 255);
    }

    private void BindLit(ShaderBindings b)
    {
        _bindLighting!(b);
        b.TrySetResource("gDepthTexture", _sceneDepthCopy);
        b.TrySetResource("gSSAOTexture", _ssao);
    }

    /// <summary>ToolsGfx's 2D scene constants (sub_140605B20): the frame's b9 with every transform identity, so the
    /// fullscreen quad's clip-space positions pass straight through <c>camToClpMatrix</c>.</summary>
    public static CodeSceneConsts Scene2D(in CodeSceneConsts scene)
    {
        var s = scene;
        s.Transforms = new CodeSceneTransforms
        {
            WldToCam = Matrix4x4.Identity,
            CamToOff = Matrix4x4.Identity,
            OffToCam = Matrix4x4.Identity,
            CamToClp = Matrix4x4.Identity,
            CamToWld = Matrix4x4.Identity,
            WldToClp = Matrix4x4.Identity,
            OffToClp = Matrix4x4.Identity,
            ClpToCam = Matrix4x4.Identity,
        };
        return s;
    }

    public void Dispose()
    {
        _blurObjectCb.Dispose();
        _blurSceneCb.Dispose();
    }
}
