using Apex.Render.Shaders;
using Apex.Render.States;
using Vortice.Direct3D11;

namespace Apex.Render.Passes;

/// <summary>
/// Binds the scene-lighting code resources (t21–t24 light/probe buffers, t40 SST, t41/t42 cull results, t44 env
/// BRDF, t46–48 probe volumes, t50 cookies, t51 reflection probes, t52–54 shadow maps) and the code samplers
/// s12/s13/s15 by reflected name — what the deferred-lighting CS and lit forward / OIT material shaders read.
/// Names a variant does not declare are skipped.
/// </summary>
public static class ForwardLighting
{
    public static void Bind(RenderContext rc, ShaderBindings b, SceneLightingResources l, ID3D11ShaderResourceView sunShadowArray,
        ID3D11ShaderResourceView? lightCullResults = null, ID3D11ShaderResourceView? probeCullResults = null,
        ID3D11ShaderResourceView? forwardDecalCullResults = null)
    {
        b.TrySetResource("gCullConstants", l.CullConstants);
        b.TrySetResource("gLights", l.Lights);
        b.TrySetResource("gProbes", l.Probes);
        b.TrySetResource("gReflectionProbeBlends", l.ReflectionProbeBlends);
        b.TrySetResource("gSunShadowTree", l.SunShadowTree);
        b.TrySetResource("gLightCullResults", lightCullResults);
        b.TrySetResource("gProbeCullResults", probeCullResults);
        b.TrySetResource("gForwardDecalCullResults", forwardDecalCullResults);
        b.TrySetResource("gEnvBRDFGeneric", l.EnvBrdf);
        b.TrySetResource("gProbeXArray", l.ProbeVolumeX);
        b.TrySetResource("gProbeYArray", l.ProbeVolumeY);
        b.TrySetResource("gProbeZArray", l.ProbeVolumeZ);
        b.TrySetResource("gCookieArray", l.CookieArray);
        b.TrySetResource("gReflectionProbeArray", l.ReflectionProbeArray);
        b.TrySetResource("gDirSpotShadowmapArray", l.SpotShadowArray);
        b.TrySetResource("gOmniShadowmapArray", l.OmniShadowArray);
        b.TrySetResource("gSunShadowmapArray", sunShadowArray);
        b.TrySetSampler("gTrilinearClampSampler", rc.States.GetSampler(GfxStateBits.CodeSamplers.TrilinearClamp));
        b.TrySetSampler("gBilinearClampSampler", rc.States.GetSampler(GfxStateBits.CodeSamplers.BilinearClamp));
        b.TrySetSampler("gCmpBilinearClampSampler", rc.States.GetSampler(GfxStateBits.CodeSamplers.CmpBilinearClamp));
    }
}
