using Apex.Render.Data.Shaders;
using DataShaderCache = Apex.Render.Data.Shaders.ShaderCache;
using DataStage = Apex.Render.Data.Shaders.ShaderStage;

namespace Apex.Render.Shaders;

/// <summary>
/// The engine ("code") shader variants the preview frame runs, as ToolsGfx looks them up in its shader cache:
/// source file + ordered define list (code shaders carry no implicit <c>TOOLSGFX</c> define). Every key below was
/// matched against the DXBC SHA-1 of the corresponding RenderDoc capture draw/dispatch (see RenderCheck).
/// </summary>
public static class CodeShaderKeys
{
    private static ShaderVariantKey K(string source, DataStage stage, params string[] defines)
        => new(source, stage, ShaderDefine.ParseList(defines));

    // Sun shadow / G-buffer / sky / forward come from materials (techsetdefs), not from here.

    // SSAO (Gfx::RenderSSAO_HemiAO)
    public static readonly ShaderVariantKey HemiAoPrepareDepth = K("ssao_hemi_ao_prepare_depth_buffers1_inc_compute.hlsl", DataStage.Compute, "USE_MAX_DEPTH");
    public static readonly ShaderVariantKey HemiAoRenderInterleaved = K("ssao_hemi_ao_render_compute.hlsl", DataStage.Compute, "INTERLEAVE_RESULT");
    public static readonly ShaderVariantKey HemiAoRender = K("ssao_hemi_ao_render_compute.hlsl", DataStage.Compute);
    public static readonly ShaderVariantKey HemiAoBlurUpsample = K("ssao_hemi_ao_blur_and_upsample_compute.hlsl", DataStage.Compute,
        "COMBINE_LOWER_RESOLUTIONS", "BLEND_WITH_HIGHER_RESOLUTION");

    // Gfx::RenderLightCulling / Gfx::RenderDeferredLighting
    public static readonly ShaderVariantKey LightCulling = K("light_culling.hlsl", DataStage.Compute);
    public static readonly ShaderVariantKey DeferredLighting = K("ToolsGfx/deferred_lighting.hlsl", DataStage.Compute);

    // Gfx::PrepareOIT / Gfx::RenderOIT
    public static readonly ShaderVariantKey OitClear = K("oit_compute_uav_clear.hlsl", DataStage.Compute);
    public static readonly ShaderVariantKey OitResolve = K("oit_compute.hlsl", DataStage.Compute);

    // SSS Blur (after RENDER_STAGE_LIT_FORWARD_SSS_OPAQUE): both passes run skin_vblur_ps.hlsl's VS.
    public static readonly ShaderVariantKey SkinBlurVs = K("skin_vblur_ps.hlsl", DataStage.Vertex);
    public static readonly ShaderVariantKey SkinHBlurPs = K("skin_hblur_ps.hlsl", DataStage.Pixel);
    public static readonly ShaderVariantKey SkinVBlurPs = K("skin_vblur_ps.hlsl", DataStage.Pixel);

    // ExposureDownscale: first level USE_LOG_DOWNSAMPLE, the rest plain.
    public static readonly ShaderVariantKey ExposureLogVs = K("bloom_downsample.hlsl", DataStage.Vertex, "USE_LOG_DOWNSAMPLE");
    public static readonly ShaderVariantKey ExposureLogPs = K("bloom_downsample.hlsl", DataStage.Pixel, "USE_LOG_DOWNSAMPLE");
    public static readonly ShaderVariantKey ExposurePs = K("bloom_downsample.hlsl", DataStage.Pixel);

    // TonemapLUT
    public static readonly ShaderVariantKey TonemapVs = K("tonemap_lut.hlsl", DataStage.Vertex);
    public static readonly ShaderVariantKey TonemapPs = K("tonemap_lut.hlsl", DataStage.Pixel);

    // SMAA 1x
    public static readonly ShaderVariantKey SmaaEdgeVs = K("antialias_edge_detection.hlsl", DataStage.Vertex);
    public static readonly ShaderVariantKey SmaaEdgePs = K("antialias_edge_detection.hlsl", DataStage.Pixel);
    public static readonly ShaderVariantKey SmaaWeightsVs = K("antialias_blend_weights.hlsl", DataStage.Vertex);
    public static readonly ShaderVariantKey SmaaWeightsPs = K("antialias_blend_weights.hlsl", DataStage.Pixel);
    public static readonly ShaderVariantKey SmaaBlendVs = K("antialias.hlsl", DataStage.Vertex);
    public static readonly ShaderVariantKey SmaaBlendPs = K("antialias.hlsl", DataStage.Pixel);

    // Post-SMAA: scene depth → back-buffer alpha (colour mask A).
    public static readonly ShaderVariantKey DepthToAlphaVs = K("depth_test_to_alpha.hlsl", DataStage.Vertex);
    public static readonly ShaderVariantKey DepthToAlphaPs = K("depth_test_to_alpha.hlsl", DataStage.Pixel);

    public static IEnumerable<(string Name, ShaderVariantKey Key)> All()
    {
        foreach (var f in typeof(CodeShaderKeys).GetFields())
        {
            if (f.GetValue(null) is ShaderVariantKey k)
                yield return (f.Name, k);
        }
    }
}

/// <summary>Creates code-shader programs from the install's ToolsGfx cache (via the data layer) on the device.</summary>
public sealed class CodeShaderLibrary
{
    private readonly ShaderCache _programs;
    private readonly DataShaderCache _cache;

    public CodeShaderLibrary(ShaderCache programs, DataShaderCache cache)
    {
        _programs = programs;
        _cache = cache;
    }

    public ShaderProgram Get(ShaderVariantKey key)
        => _programs.GetOrCreate("code:" + key, () => _cache.Load(key).Dxbc);
}
