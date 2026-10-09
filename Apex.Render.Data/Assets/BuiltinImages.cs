using Apex.Render.Data.Techsets;

namespace Apex.Render.Data.Assets;

/// <summary>
/// Engine built-in images referenced as techsetdef defaults (<c>$white_diffuse</c>, <c>$identitynormalmap</c>, ...).
/// They are not GDT assets; this table maps them to the stock source files under <c>texture_assets</c>
/// whose converted copies live in the image cache.
/// </summary>
/// <remarks>
/// The mapping is inferred (APE's own table was not found in the executable). Checked against the
/// barrel capture: <c>$specular</c> is an 8x8 BC7_SRGB 2-mip image (= <c>default_specular.tif</c> in
/// <c>Texture_color_mipAvg</c>) and <c>$white_ao</c> an 8x8 BC4 2-mip image. <c>$env_brdf_generic</c> is exact
/// (lighting_data.md §4).
/// </remarks>
public static class BuiltinImages
{
    private static readonly Dictionary<string, (string File, ImageClass Class)> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["$white"] = (@"t7_default\default_white_255.tif", ImageClass.Color),
        ["$white_diffuse"] = (@"t7_default\default_white_255.tif", ImageClass.Color),
        ["$white_color"] = (@"t7_default\default_white_255.tif", ImageClass.Color),
        ["$white_effect"] = (@"t7_default\default_white_255.tif", ImageClass.Color),
        ["$white_diffuse_hdr"] = (@"t7_default\default_white_255.tif", ImageClass.HdrColor),
        ["$black"] = (@"t7_default\default_black_0.tif", ImageClass.Color),
        ["$black_diffuse"] = (@"t7_default\default_black_0.tif", ImageClass.Color),
        ["$black_color"] = (@"t7_default\default_black_0.tif", ImageClass.Color),
        ["$blacktransparent_color"] = (@"t7_default\default_black_0_alpha_0.tif", ImageClass.Color),
        ["$gray_color"] = (@"t7_default\default_gray_128.tif", ImageClass.Color),
        ["$gray_32_one_channel"] = (@"t7_default\default_gray_32.tif", ImageClass.Scalar),
        ["$identitynormalmap"] = (@"t7_default\default_normal.tif", ImageClass.Normal),
        ["$identitynormal"] = (@"t7_default\default_normal.tif", ImageClass.Normal),
        ["$normal"] = (@"t7_default\default_normal.tif", ImageClass.Normal),
        ["$specular"] = (@"t7_default\default_specular.tif", ImageClass.Color),
        ["$white_specular"] = (@"t7_default\default_white_255.tif", ImageClass.Scalar),
        ["$white_gloss"] = (@"t7_default\default_gloss.tif", ImageClass.Scalar),
        ["$white_ao"] = (@"t7_default\default_white_255.tif", ImageClass.Scalar),
        ["$white_reveal"] = (@"t7_default\default_white_255.tif", ImageClass.Scalar),
        ["$half_reveal"] = (@"t7_default\default_gray_128.tif", ImageClass.Scalar),
        ["$black_reveal"] = (@"t7_default\default_black_0.tif", ImageClass.Scalar),
        ["$thickness"] = (@"t7_default\default_thickness.tif", ImageClass.Scalar),
        ["$white_thickness"] = (@"t7_default\default_white_255.tif", ImageClass.Scalar),
        ["$white_multimask"] = (@"t7_default\default_white_255.tif", ImageClass.QuadScalar),
        ["$black_multimask"] = (@"t7_default\default_black_0.tif", ImageClass.QuadScalar),
        ["$identityflowmap"] = (@"t7_default\default_flowmap.tif", ImageClass.Color),
        ["$env_brdf_generic"] = (@"code\env_brdf_generic.tif", ImageClass.DualScalar),
        ["$env_brdf_glass"] = (@"code\env_brdf_glass.tif", ImageClass.DualScalar),
        ["$env_cone_lut"] = (@"code\cone_lut.tif", ImageClass.Lut),
    };

    /// <summary>All known built-in names.</summary>
    public static IEnumerable<string> Names => Map.Keys;

    /// <summary>The stock source file (absolute) and cache class a built-in maps to.</summary>
    public static bool TryResolve(ToolsGfxInstall install, string name, out string sourcePath, out ImageClass imageClass)
    {
        if (Map.TryGetValue(name, out var m))
        {
            sourcePath = Path.Combine(install.TextureAssetsDir, m.File);
            imageClass = m.Class;
            return true;
        }
        sourcePath = string.Empty;
        imageClass = ImageClass.Color;
        return false;
    }

    /// <summary>
    /// Loads a built-in from the image cache. <paramref name="preferredClass"/> (the texture element's class)
    /// wins over the table's class when a cached copy of that class exists. Null when unknown or not cached.
    /// </summary>
    public static CachedImage? Load(ImageCache cache, string name, ImageClass? preferredClass = null)
    {
        if (!TryResolve(cache.Install, name, out var src, out var cls))
            return null;
        ImageCacheEntry? entry = null;
        if (preferredClass is { } p && cache.Find(src, p, out _) is { } e
            && string.Equals(e.ClassToken, ImageClasses.CacheToken(p), StringComparison.OrdinalIgnoreCase))
            entry = e;
        entry ??= cache.Find(src, cls, out _);
        return entry is null ? null : ImageCache.LoadFile(entry.Path);
    }
}
