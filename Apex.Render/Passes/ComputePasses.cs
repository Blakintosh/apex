using System.Runtime.InteropServices;
using Apex.Render.Constants;
using Apex.Render.Resources;
using Apex.Render.Shaders;
using Apex.Render.States;
using Apex.Render.Targets;
using Vortice.Direct3D11;

namespace Apex.Render.Passes;

/// <summary>
/// Gfx::RenderSSAO_HemiAO — five CS dispatches:
/// <list type="number">
/// <item>prepare_depth_buffers1 [USE_MAX_DEPTH]: DS0 → DS2x (RT8), DS2xAtlas (RT12), DS4x (RT9), DS4xAtlas (RT13); groups = ⌈DS2x/8⌉.</item>
/// <item>render [INTERLEAVE_RESULT] on DS4xAtlas → RT17; groups ⌈100/8⌉×⌈54/8⌉×16.</item>
/// <item>render on DS4x → RT21 (16×16 threads); groups ⌈DS4x/16⌉.</item>
/// <item>render [INTERLEAVE_RESULT] on DS2xAtlas → RT16; groups ⌈DS2xAtlas/8⌉×16.</item>
/// <item>blur_and_upsample [COMBINE_LOWER_RESOLUTIONS, BLEND_WITH_HIGHER_RESOLUTION]: t0 DS4x, t1 DS2x, t2 RT17, t3 RT21,
/// t4 RT16 → AoResult RT7 (DL t4); groups = ⌈((RT7 + 2) &gt;&gt; 1) / 8⌉.</item>
/// </list>
/// Dispatches 2–5 each get their own b12 (<see cref="HemiAoConstants"/>), each in its own buffer that is re-uploaded
/// only when its contents change (they depend on the window size alone).
/// </summary>
public sealed class HemiAoPass : IDisposable
{
    private readonly RenderContext _rc;
    private readonly ShaderBindings _prepare, _renderInterleaved, _render, _blur;
    private readonly GpuBuffer[] _cbs = new GpuBuffer[4];
    private readonly CodeSsaoConsts[] _uploaded = new CodeSsaoConsts[4];
    private readonly bool[] _valid = new bool[4];
    private (int W, int H) _windowSize;
    private CodeSsaoConsts[]? _window;

    public HemiAoPass(RenderContext rc)
    {
        _rc = rc;
        var lib = rc.CodeShaders;
        _prepare = new ShaderBindings(lib.Get(CodeShaderKeys.HemiAoPrepareDepth));
        _renderInterleaved = new ShaderBindings(lib.Get(CodeShaderKeys.HemiAoRenderInterleaved));
        _render = new ShaderBindings(lib.Get(CodeShaderKeys.HemiAoRender));
        _blur = new ShaderBindings(lib.Get(CodeShaderKeys.HemiAoBlurUpsample));
        for (int i = 0; i < _cbs.Length; i++)
            _cbs[i] = GpuBuffer.CreateConstant(rc.Gfx, CodeSsaoConsts.Size, $"cb12 ssao dispatch {i + 2}");
    }

    /// <summary>Runs the chain on <paramref name="depthSrv"/> (DS0 through R32_FLOAT_X8X24) and returns RT7.
    /// <paramref name="consts"/> = b12 of dispatches 2, 3, 4 and 5.</summary>
    public GpuTexture Run(ID3D11ShaderResourceView depthSrv)
    {
        var size = (_rc.Targets.Width, _rc.Targets.Height);
        if (_window is null || _windowSize != size)
            (_window, _windowSize) = (HemiAoConstants.ForWindow(size.Width, size.Height), size);
        return Run(depthSrv, _window);
    }

    /// <summary>The b12 buffer of dispatch <paramref name="index"/> + 2 holding <paramref name="consts"/>.</summary>
    private GpuBuffer Constants(int index, in CodeSsaoConsts consts)
    {
        var bytes = MemoryMarshal.AsBytes(new ReadOnlySpan<CodeSsaoConsts>(in consts));
        if (!_valid[index] || !bytes.SequenceEqual(MemoryMarshal.AsBytes(new ReadOnlySpan<CodeSsaoConsts>(in _uploaded[index]))))
        {
            _cbs[index].Update(_rc.Gfx.Context, consts);
            _uploaded[index] = consts;
            _valid[index] = true;
        }
        return _cbs[index];
    }

    public GpuTexture Run(ID3D11ShaderResourceView depthSrv, ReadOnlySpan<CodeSsaoConsts> consts)
    {
        if (consts.Length != 4)
            throw new ArgumentException("4 CodeSSAOConsts expected (dispatches 2–5)");
        var t = _rc.Targets;
        var ctx = _rc.Gfx.Context;
        // s2 "LinearSampler" is whatever APE left bound: sampler object 166 = POINT, WRAP, MaxLOD 0 (all captures).
        var linear = _rc.States.GetSampler(GfxStateBits.FilterNomipNearest | GfxStateBits.TileBoth);
        var ds2 = t[RenderTargetId.HemiAoDepth2x];
        var ds4 = t[RenderTargetId.HemiAoDepth4x];
        var ds2a = t[RenderTargetId.HemiAoDepth2xAtlas];
        var ds4a = t[RenderTargetId.HemiAoDepth4xAtlas];
        var ao2i = t[RenderTargetId.HemiAoOcclusion2xInterleaved];
        var ao4i = t[RenderTargetId.HemiAoOcclusion4xInterleaved];
        var ao4 = t[RenderTargetId.HemiAoOcclusion4x];
        var result = t[RenderTargetId.Ssao];

        _prepare.SetResource("GBufferDepth", depthSrv);
        _prepare.SetUnorderedAccess("DS2x", ds2.Uav);
        _prepare.SetUnorderedAccess("DS2xAtlas", ds2a.Uav);
        _prepare.SetUnorderedAccess("DS4x", ds4.Uav);
        _prepare.SetUnorderedAccess("DS4xAtlas", ds4a.Uav);
        ComputePass.Dispatch(ctx, _prepare, Ceil(ds2.Width, 8), Ceil(ds2.Height, 8));

        void Render(ShaderBindings b, GpuBuffer cb, GpuTexture src, GpuTexture dst, int groupSize, int slices)
        {
            b.SetConstantBuffer(CodeBuffer.SsaoName, cb);
            b.SetSampler("LinearSampler", linear);
            b.SetResource("codeTexture0", src.Srv);
            b.SetUnorderedAccess("Occlusion", dst.Uav);
            ComputePass.Dispatch(ctx, b, Ceil(src.Width, groupSize), Ceil(src.Height, groupSize), slices);
        }
        Render(_renderInterleaved, Constants(0, consts[0]), ds4a, ao4i, 8, 16);
        Render(_render, Constants(1, consts[1]), ds4, ao4, 16, 1);
        Render(_renderInterleaved, Constants(2, consts[2]), ds2a, ao2i, 8, 16);

        _blur.SetConstantBuffer(CodeBuffer.SsaoName, Constants(3, consts[3]));
        _blur.SetSampler("LinearSampler", linear);
        _blur.SetResource("codeTexture0", ds4.Srv);
        _blur.SetResource("codeTexture1", ds2.Srv);
        _blur.SetResource("codeTexture2", ao4i.Srv);
        _blur.SetResource("codeTexture3", ao4.Srv);
        _blur.SetResource("codeTexture4", ao2i.Srv);
        _blur.SetUnorderedAccess("AoResult", result.Uav);
        // MiniEngine: one thread per 2×2 high-res pixels plus a one-texel border.
        ComputePass.Dispatch(ctx, _blur, Ceil((result.Width + 2) >> 1, 8), Ceil((result.Height + 2) >> 1, 8));
        return result;
    }

    internal static int Ceil(int n, int d) => (n + d - 1) / d;

    public void Dispose()
    {
        foreach (var cb in _cbs)
            cb.Dispose();
    }
}

/// <summary>
/// Gfx::RenderLightCulling: <c>light_culling.hlsl</c> ([numthreads(4,4,16)]) bins lights/probes/forward decals into
/// three Texture3D R32_UINT volumes (RT4/5/6: ⌈W/64⌉ × ⌈H/64⌉ × 256). Groups = ⌈tilesX/4⌉ × ⌈tilesY/4⌉ × 1 (the
/// captured 1600×861 frame: 25×14 tiles → Dispatch(7, 4, 1)). Inputs: b9 and t21 <c>gCullConstants</c>.
/// </summary>
public sealed class LightCullingPass
{
    private readonly RenderContext _rc;
    private readonly ShaderBindings _cs;

    public LightCullingPass(RenderContext rc)
    {
        _rc = rc;
        _cs = new ShaderBindings(rc.CodeShaders.Get(CodeShaderKeys.LightCulling));
    }

    public void Run(GpuBuffer sceneCb, ID3D11ShaderResourceView cullConstants)
    {
        var t = _rc.Targets;
        var light = t[RenderTargetId.LightCull];
        _cs.SetConstantBuffer(CodeBuffer.SceneName, sceneCb);
        _cs.SetResource("gCullConstants", cullConstants);
        _cs.SetUnorderedAccess("lightCullResultsOutput", light.Uav);
        _cs.SetUnorderedAccess("probeCullResultsOutput", t[RenderTargetId.ProbeCull].Uav);
        _cs.SetUnorderedAccess("forwardDecalCullResultsOutput", t[RenderTargetId.DecalCull].Uav);
        var (gx, gy) = ComputePass.GroupsFor(_cs.Programs[0], light.Width, light.Height);
        ComputePass.Dispatch(_rc.Gfx.Context, _cs, gx, gy);
    }
}

/// <summary>
/// Scene lighting resources the deferred-lighting CS reads (all from assetviewer.led and code images for the
/// preview; the local light/probe/decal buffers are bound but empty — numLights = numProbes = 0).
/// </summary>
public sealed class SceneLightingResources
{
    public required ID3D11ShaderResourceView CullConstants { get; init; }          // t21 gCullConstants (80 B)
    public required ID3D11ShaderResourceView Lights { get; init; }                 // t22 gLights (224 B)
    public required ID3D11ShaderResourceView Probes { get; init; }                 // t23 gProbes (224 B)
    public required ID3D11ShaderResourceView ReflectionProbeBlends { get; init; }  // t24 (96 B)
    public required ID3D11ShaderResourceView SunShadowTree { get; init; }          // t40 uint
    public required ID3D11ShaderResourceView EnvBrdf { get; init; }                // t44 64² R8G8
    public required ID3D11ShaderResourceView ProbeVolumeX { get; init; }           // t46 BC6H 3D
    public required ID3D11ShaderResourceView ProbeVolumeY { get; init; }           // t47
    public required ID3D11ShaderResourceView ProbeVolumeZ { get; init; }           // t48
    public required ID3D11ShaderResourceView CookieArray { get; init; }            // t50
    public required ID3D11ShaderResourceView ReflectionProbeArray { get; init; }   // t51 cube array
    public required ID3D11ShaderResourceView SpotShadowArray { get; init; }        // t52 (lighting RT0)
    public required ID3D11ShaderResourceView OmniShadowArray { get; init; }        // t53 (lighting RT1 cube)
}

/// <summary>
/// Gfx::RenderDeferredLighting: <c>deferred_lighting.hlsl</c> CS, [numthreads(8,8,1)], Dispatch(⌈W/8⌉, ⌈H/8⌉, 1),
/// every input bound by reflected name; writes the exposure-divided radiance into RT33 (u0 <c>litBuffer</c>) where
/// the G-buffer has geometry. Its b10 is the identity object with <c>customInt4s.x = 1</c> (captured).
/// </summary>
public sealed class DeferredLightingPass : IDisposable
{
    private readonly RenderContext _rc;
    private readonly ShaderBindings _cs;
    private readonly GpuBuffer _objectCb;

    public DeferredLightingPass(RenderContext rc)
    {
        _rc = rc;
        _cs = new ShaderBindings(rc.CodeShaders.Get(CodeShaderKeys.DeferredLighting));
        _objectCb = GpuBuffer.CreateConstant(rc.Gfx, CodeObjectConsts.Size, "cb10 deferred lighting");
        var o = CodeObjectConsts.Identity;
        o.CustomInt0 = 1;
        _objectCb.Update(rc.Gfx.Context, o);
    }

    public ShaderProgram Program => _cs.Programs[0];

    public void Run(GpuBuffer sceneCb, SceneLightingResources lighting, ID3D11ShaderResourceView albedo, ID3D11ShaderResourceView normalGloss,
        ID3D11ShaderResourceView reflectanceOcclusion, ID3D11ShaderResourceView depth, ID3D11ShaderResourceView ssao,
        ID3D11ShaderResourceView sunShadowArray, ID3D11UnorderedAccessView litBuffer, int width, int height)
    {
        var b = _cs;
        b.SetConstantBuffer(CodeBuffer.SceneName, sceneCb);
        b.SetConstantBuffer(CodeBuffer.ObjectName, _objectCb);
        b.SetResource("gbufferTextureAlbedo", albedo);
        b.SetResource("gbufferTextureNormalGloss", normalGloss);
        b.SetResource("gbufferTextureReflectanceOcclusion", reflectanceOcclusion);
        b.SetResource("depthTexture", depth);
        b.SetResource("ssaoTexture", ssao);
        ForwardLighting.Bind(_rc, b, lighting, sunShadowArray);
        b.SetUnorderedAccess("litBuffer", litBuffer);
        var (gx, gy) = ComputePass.GroupsFor(Program, width, height);
        ComputePass.Dispatch(_rc.Gfx.Context, b, gx, gy);
    }

    public IReadOnlyList<string> Unassigned() => _cs.Unassigned();

    public void Dispose() => _objectCb.Dispose();
}

/// <summary>
/// Gfx::PrepareOIT / RENDER_STAGE_TRANSPARENT / Gfx::RenderOIT. Prepare: <c>oit_compute_uav_clear</c> zeroes the
/// per-pixel fragment counter RT27 (u0 <c>oitFragmentCount</c>, Dispatch ⌈W/8⌉×⌈H/8⌉) and RT29 is cleared to
/// (0,0,0,1). Transparent items then append fragments through their "oit" technique (PS UAVs). Resolve:
/// <c>oit_compute</c> reads t0 RT27 counts, t1 RT28 fragments (R32G32_UINT × 9), t2 the lit buffer RT33, t3 RT29 and
/// writes the composited HDR into RT35 (u0 <c>litBuffer</c>), which post-processing consumes.
/// </summary>
public sealed class OitPass
{
    private readonly RenderContext _rc;
    private readonly ShaderBindings _clear, _resolve;

    public OitPass(RenderContext rc)
    {
        _rc = rc;
        _clear = new ShaderBindings(rc.CodeShaders.Get(CodeShaderKeys.OitClear));
        _resolve = new ShaderBindings(rc.CodeShaders.Get(CodeShaderKeys.OitResolve));
    }

    public void Prepare(GpuBuffer sceneCb)
    {
        var t = _rc.Targets;
        var head = t[RenderTargetId.OitHead];
        _clear.TrySetConstantBuffer(CodeBuffer.SceneName, sceneCb);
        _clear.SetUnorderedAccess("oitFragmentCount", head.Uav);
        var (gx, gy) = ComputePass.GroupsFor(_clear.Programs[0], head.Width, head.Height);
        ComputePass.Dispatch(_rc.Gfx.Context, _clear, gx, gy);
        _rc.Gfx.Context.ClearRenderTargetView(t[RenderTargetId.OitAccum].Rtv!, new Vortice.Mathematics.Color4(0, 0, 0, 1));
    }

    /// <summary>Composites the OIT fragments over <paramref name="litBuffer"/> into <paramref name="output"/> (RT35).</summary>
    public void Resolve(GpuBuffer sceneCb, GpuTexture litBuffer, GpuTexture output)
    {
        var t = _rc.Targets;
        _resolve.TrySetConstantBuffer(CodeBuffer.SceneName, sceneCb);
        _resolve.SetResource("oitFragmentCount", t[RenderTargetId.OitHead].Srv);
        _resolve.SetResource("oitFragmentData", t[RenderTargetId.OitFragments].Srv);
        _resolve.SetResource("litColorTexture", litBuffer.Srv);
        _resolve.SetResource("oitFallback", t[RenderTargetId.OitAccum].Srv);
        _resolve.SetUnorderedAccess("litBuffer", output.Uav);
        var (gx, gy) = ComputePass.GroupsFor(_resolve.Programs[0], output.Width, output.Height);
        ComputePass.Dispatch(_rc.Gfx.Context, _resolve, gx, gy);
    }

    public IReadOnlyList<string> ResolveUnassigned() => _resolve.Unassigned();
    public ShaderBindings ResolveBindings => _resolve;
}
