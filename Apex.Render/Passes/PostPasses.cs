using Apex.Render.Constants;
using Apex.Render.Resources;
using Apex.Render.Shaders;
using Apex.Render.States;
using Apex.Render.Targets;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Apex.Render.Passes;

/// <summary>
/// ExposureDownscale: eight fullscreen quads halving the HDR frame (RT35) into RT36 (½) … RT43 (1/256), the first
/// with <c>bloom_downsample</c> [USE_LOG_DOWNSAMPLE] and the rest plain, sampler bilinear clamp. APE reads RT40 back
/// for auto-exposure statistics that the preview never applies (its exposure is the static LED/SSI formula), so the
/// chain has no effect on the displayed image; it is kept for fidelity/diagnostics.
/// </summary>
public sealed class ExposureDownscalePass
{
    public const int Levels = 8;

    private readonly RenderContext _rc;
    private readonly ShaderBindings _log, _plain;

    public ExposureDownscalePass(RenderContext rc)
    {
        _rc = rc;
        var lib = rc.CodeShaders;
        var vs = lib.Get(CodeShaderKeys.ExposureLogVs);
        _log = new ShaderBindings(vs, lib.Get(CodeShaderKeys.ExposureLogPs));
        _plain = new ShaderBindings(vs, lib.Get(CodeShaderKeys.ExposurePs));
    }

    /// <summary>Runs the chain; b9 per level = the current scene constants (<see cref="RenderContext.UploadScene"/>)
    /// with that level's render-target size.</summary>
    public GpuTexture Run(ID3D11ShaderResourceView hdr)
    {
        var sampler = _rc.States.GetSampler(GfxStateBits.CodeSamplers.PostBilinearClamp);
        var src = hdr;
        GpuTexture? dst = null;
        for (int i = 0; i < Levels; i++)
        {
            var b = i == 0 ? _log : _plain;
            dst = _rc.Targets[RenderTargetId.ExposureChainFirst + i];
            b.TrySetConstantBuffer(CodeBuffer.SceneName, _rc.SceneForTarget(dst.Width, dst.Height));
            b.SetResource("inputTexture", src);
            b.SetSampler("bilinearClamp", sampler);
            _rc.Quad.Draw(_rc.Gfx, _rc.States, _rc.Layouts, b, [dst.Rtv!], dst.Width, dst.Height);
            src = dst.Srv!;
        }
        return dst!;
    }
}

/// <summary>
/// TonemapLUT (<c>tonemap_lut.hlsl</c>): HDR → RT30 R8G8B8A8_UNORM with alpha = luma for SMAA. Preview bindings:
/// b10 = post object (customInt4s (0,0,0,1): LUT off), b13 = <see cref="CodePostFxConsts.PreviewDefaults"/>,
/// t1 bloom = 1×1 black, t2 lens flare = RT55 cleared to black, t3 LUT = 1×1 white. See pipeline.md §3.5.
/// </summary>
public sealed class TonemapPass
{
    private readonly RenderContext _rc;
    private readonly ShaderBindings _b;
    private GpuTexture? _clearedFlare;

    public TonemapPass(RenderContext rc)
    {
        _rc = rc;
        _b = new ShaderBindings(rc.CodeShaders.Get(CodeShaderKeys.TonemapVs), rc.CodeShaders.Get(CodeShaderKeys.TonemapPs));
    }

    public GpuTexture Run(ID3D11ShaderResourceView hdr, ID3D11DepthStencilView? sceneDepth = null)
    {
        var flare = _rc.Targets[RenderTargetId.LensFlare];
        // Nothing draws into RT55 (lens flares are off), so it keeps its clear until the pool replaces it.
        if (!ReferenceEquals(flare, _clearedFlare))
        {
            _rc.Gfx.Context.ClearRenderTargetView(flare.Rtv!, new Color4(0, 0, 0, 1));
            _clearedFlare = flare;
        }
        var output = _rc.Targets[RenderTargetId.TonemapOutput];
        _b.SetConstantBuffer(CodeBuffer.ObjectName, _rc.PostObjectCb);
        _b.SetConstantBuffer(CodeBuffer.PostFxName, _rc.PostFxCb);
        _b.SetResource("frameBuffer", hdr);
        _b.SetResource("bloomTexture", _rc.Black.Srv);
        _b.SetResource("lensFlareTexture", flare.Srv);
        _b.SetResource("lutTexture", _rc.White.Srv);
        _b.SetSampler("bilinearClamp", _rc.States.GetSampler(GfxStateBits.CodeSamplers.PostBilinearClamp));
        _rc.Quad.Draw(_rc.Gfx, _rc.States, _rc.Layouts, _b, [output.Rtv!], output.Width, output.Height, sceneDepth);
        return output;
    }
}

/// <summary>
/// SMAA 1x (<c>antialias_edge_detection</c> → <c>antialias_blend_weights</c> → <c>antialias</c>): luma edges of RT30
/// into RT31 (R8G8), blend weights into RT32 with the SMAA area (160×560 R8G8) and search (66×33 R8) lookup
/// textures, then neighbourhood blending into the output (the window back buffer). RT31/RT32 are cleared to
/// (0,0,0,1) first; samplers LinearSampler (bilinear clamp, no mips) and PointSampler.
/// </summary>
public sealed class SmaaPass : IDisposable
{
    private readonly RenderContext _rc;
    private readonly ShaderBindings _edges, _weights, _blend;
    private readonly GpuTexture _area, _search;

    public SmaaPass(RenderContext rc, ReadOnlyMemory<byte> areaTexRg8, ReadOnlyMemory<byte> searchTexR8)
    {
        _rc = rc;
        var lib = rc.CodeShaders;
        _edges = new ShaderBindings(lib.Get(CodeShaderKeys.SmaaEdgeVs), lib.Get(CodeShaderKeys.SmaaEdgePs));
        _weights = new ShaderBindings(lib.Get(CodeShaderKeys.SmaaWeightsVs), lib.Get(CodeShaderKeys.SmaaWeightsPs));
        _blend = new ShaderBindings(lib.Get(CodeShaderKeys.SmaaBlendVs), lib.Get(CodeShaderKeys.SmaaBlendPs));
        _area = GpuTexture.FromData(rc.Gfx, TextureData.Single2D(Format.R8G8_UNorm, AreaWidth, AreaHeight, areaTexRg8), debugName: "SMAA areaTex");
        _search = GpuTexture.FromData(rc.Gfx, TextureData.Single2D(Format.R8_UNorm, SearchWidth, SearchHeight, searchTexR8), debugName: "SMAA searchTex");
    }

    public const int AreaWidth = 160, AreaHeight = 560, SearchWidth = 66, SearchHeight = 33;

    public void Run(GpuBuffer sceneCb, GpuTexture colour, ID3D11RenderTargetView output, int width, int height,
        ID3D11DepthStencilView? sceneDepth = null)
    {
        var ctx = _rc.Gfx.Context;
        var linear = _rc.States.GetSampler(GfxStateBits.CodeSamplers.PostBilinearClamp);
        var point = _rc.States.GetSampler(GfxStateBits.CodeSamplers.PostPointClamp);
        var edges = _rc.Targets[RenderTargetId.SmaaEdges];
        var weights = _rc.Targets[RenderTargetId.SmaaBlendWeights];

        ctx.ClearRenderTargetView(edges.Rtv!, new Color4(0, 0, 0, 1));
        _edges.TrySetConstantBuffer(CodeBuffer.SceneName, sceneCb);
        _edges.SetResource("colorTexGamma", colour.Srv);
        _edges.SetSampler("LinearSampler", linear);
        _rc.Quad.Draw(_rc.Gfx, _rc.States, _rc.Layouts, _edges, [edges.Rtv!], width, height, sceneDepth);

        ctx.ClearRenderTargetView(weights.Rtv!, new Color4(0, 0, 0, 1));
        _weights.TrySetConstantBuffer(CodeBuffer.SceneName, sceneCb);
        _weights.SetResource("edgesTex", edges.Srv);
        _weights.SetResource("areaTex", _area.Srv);
        _weights.SetResource("searchTex", _search.Srv);
        _weights.SetSampler("LinearSampler", linear);
        _weights.SetSampler("PointSampler", point);
        _rc.Quad.Draw(_rc.Gfx, _rc.States, _rc.Layouts, _weights, [weights.Rtv!], width, height, sceneDepth);

        _blend.TrySetConstantBuffer(CodeBuffer.SceneName, sceneCb);
        _blend.SetResource("blendTex", weights.Srv);
        _blend.SetResource("colorTex", colour.Srv);
        _blend.SetSampler("LinearSampler", linear);
        _rc.Quad.Draw(_rc.Gfx, _rc.States, _rc.Layouts, _blend, [output], width, height, sceneDepth);
    }

    public ShaderBindings EdgeBindings => _edges;
    public ShaderBindings WeightBindings => _weights;
    public ShaderBindings BlendBindings => _blend;

    public void Dispose()
    {
        _area.Dispose();
        _search.Dispose();
    }
}

/// <summary>
/// The draw APE issues right after SMAA (no marker): <c>depth_test_to_alpha</c> writes the back buffer's ALPHA only
/// (colour mask A) from the scene depth, so the viewport widget can tell model pixels from background. b10 = scene
/// object (identity, zeros).
/// </summary>
public sealed class DepthToAlphaPass
{
    private readonly RenderContext _rc;
    private readonly ShaderBindings _b;

    public DepthToAlphaPass(RenderContext rc)
    {
        _rc = rc;
        _b = new ShaderBindings(rc.CodeShaders.Get(CodeShaderKeys.DepthToAlphaVs), rc.CodeShaders.Get(CodeShaderKeys.DepthToAlphaPs));
    }

    public void Run(ID3D11ShaderResourceView sceneDepth, ID3D11RenderTargetView output, int width, int height, ID3D11DepthStencilView? dsv = null)
    {
        _b.TrySetConstantBuffer(CodeBuffer.ObjectName, _rc.ObjectCb);
        _b.SetResource("depthTexture", sceneDepth);
        _rc.Quad.Draw(_rc.Gfx, _rc.States, _rc.Layouts, _b, [output], width, height, dsv, GfxStateBits.WriteA);
    }
}
