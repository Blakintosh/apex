using System.Globalization;
using System.Text;

namespace Apex.Render.Data.Conversion.Images;

/// <summary>ToolsGfx image type (<c>gImageTypeNames</c>); APE only implements Texture and Cube.</summary>
public enum ToolsGfxImageType { Texture = 0, TextureArray = 1, Cube = 2, CubeArray = 3, Volume = 4 }

/// <summary>ToolsGfx conversion semantic (the class token of the settings directory).</summary>
public enum ToolsGfxImageSemantic { Color = 0, HdrColor = 1, Normal = 2, Scalar = 3, DualScalar = 4, QuadScalar = 5, Lut = 6 }

/// <summary>
/// Conversion settings of one GDT image asset, as <c>ToolsGfx_Image_LoadFromGdt</c> (0x1403EF8C0) derives them, plus
/// the settings directory name built in <c>ToolsGfx_Image_LoadOrConvert</c> (0x1403ED1F0).
/// </summary>
public sealed record ImageConversionSettings
{
    public ToolsGfxImageType ImageType { get; init; }
    public ToolsGfxImageSemantic Semantic { get; init; }

    /// <summary>1 compressed, 2 compressed high color, 3 compressed low color, 4 compressed no alpha, 5 uncompressed
    /// (also "shared exponent").</summary>
    public int Compression { get; init; } = 1;

    public bool ClampU { get; init; }
    public bool ClampV { get; init; }

    /// <summary>0 Average, 1 Luminance, 2 Alpha, 3 Luminance Alpha, 4 Max, 5 PunchThru, 6 none (<c>noMipMaps</c>).</summary>
    public int MipMode { get; init; }

    /// <summary>Levels dropped from the top (<c>1/1</c>=0 .. <c>1/8</c>=3).</summary>
    public int MipBase { get; init; }

    /// <summary><c>glossVarianceScale</c> clamped to [0,1] (normal maps; 1.0 otherwise).</summary>
    public float GlossVarianceScale { get; init; } = 1.0f;

    public bool PremultipliedAlpha { get; init; }

    private static readonly string[] TypeNames = ["Texture", "TextureArray", "Cube", "CubeArray", "Volume"];
    private static readonly string[] SemanticNames = ["color", "hdrcolor", "normal", "scalar", "dualscalar", "quadscalar", "lut"];
    private static readonly string[] MipModeSuffixes = ["_mipAvg", "_mipLum", "_mipAlpha", "_mipLumAlpha", "_mipMax", "_mipPunchThru", "_mipNone"];

    /// <summary>
    /// <c>Type_semantic[_uncomp|_lowColor|_noAlpha][_clampUV|_clampU|_clampV]_mipX[_mipBaseN][_varN][_premul]</c>
    /// (asset_caches.md §6.1), e.g. <c>Texture_normal_mipAvg_var100</c>.
    /// </summary>
    public string SettingsDirectory
    {
        get
        {
            var sb = new StringBuilder();
            sb.Append(TypeNames[(int)ImageType]).Append('_').Append(SemanticNames[(int)Semantic]);
            if (Compression == 5) sb.Append("_uncomp");
            else if (Semantic == ToolsGfxImageSemantic.Color && Compression == 3) sb.Append("_lowColor");
            else if (Semantic == ToolsGfxImageSemantic.Color && Compression == 4) sb.Append("_noAlpha");
            bool cu = ClampU && ImageType != ToolsGfxImageType.Cube, cv = ClampV && ImageType != ToolsGfxImageType.Cube;
            if (cu && cv) sb.Append("_clampUV");
            else if (cu) sb.Append("_clampU");
            else if (cv) sb.Append("_clampV");
            sb.Append(MipModeSuffixes[MipMode]);
            if (MipBase != 0) sb.Append("_mipBase").Append(MipBase.ToString(CultureInfo.InvariantCulture));
            if (Semantic == ToolsGfxImageSemantic.Normal)
                sb.Append("_var").Append(((int)(GlossVarianceScale * 100.0f + 0.5f)).ToString(CultureInfo.InvariantCulture));
            if (PremultipliedAlpha) sb.Append("_premul");
            return sb.ToString();
        }
    }

    /// <summary>GDT <c>semantic</c> -&gt; (conversion semantic), from <c>Techset_InitImageSemanticNames</c> (0x1407E53D0).
    /// Names not in the table (camoMap, Emblem, emissiveMap, ...) are rejected by APE ("Unknown image semantic").</summary>
    public static readonly IReadOnlyDictionary<string, ToolsGfxImageSemantic> SemanticTable = new Dictionary<string, ToolsGfxImageSemantic>(StringComparer.Ordinal)
    {
        ["2d"] = ToolsGfxImageSemantic.Color,
        ["colorMap"] = ToolsGfxImageSemantic.Color,
        ["diffuseMap"] = ToolsGfxImageSemantic.Color,
        ["effectMap"] = ToolsGfxImageSemantic.Color,
        ["specularMap"] = ToolsGfxImageSemantic.Color,
        ["HDR"] = ToolsGfxImageSemantic.HdrColor,
        ["specularMask"] = ToolsGfxImageSemantic.Scalar,
        ["multipleMask"] = ToolsGfxImageSemantic.QuadScalar,
        ["normalMap"] = ToolsGfxImageSemantic.Normal,
        ["glossMap"] = ToolsGfxImageSemantic.Scalar,
        ["occlusionMap"] = ToolsGfxImageSemantic.Scalar,
        ["revealMap"] = ToolsGfxImageSemantic.Scalar,
        ["thicknessMap"] = ToolsGfxImageSemantic.Scalar,
        ["LutTpage"] = ToolsGfxImageSemantic.Lut,
        ["One Channel"] = ToolsGfxImageSemantic.Scalar,
        ["Two Channel"] = ToolsGfxImageSemantic.DualScalar,
        ["Eye Caustic"] = ToolsGfxImageSemantic.HdrColor,
        ["Custom"] = ToolsGfxImageSemantic.HdrColor,
    };

    private static readonly Dictionary<string, int> CompressionTable = new(StringComparer.Ordinal)
    {
        ["compressed"] = 1, ["compressed high color"] = 2, ["compressed low color"] = 3,
        ["compressed no alpha"] = 4, ["shared exponent"] = 5, ["uncompressed"] = 5,
    };

    private static readonly Dictionary<string, int> MipModeTable = new(StringComparer.Ordinal)
    {
        ["Average"] = 0, ["Luminance"] = 1, ["Alpha"] = 2, ["Luminance Alpha"] = 3, ["Max"] = 4, ["PunchThru"] = 5,
    };

    private static readonly Dictionary<string, int> MipBaseTable = new(StringComparer.Ordinal)
    {
        ["1/1"] = 0, ["1/2"] = 1, ["1/4"] = 2, ["1/8"] = 3,
    };

    /// <summary>
    /// Settings from a GDT <c>image.gdf</c> entry (0x1403EF8C0). Missing fields take image.awi's defaults
    /// (first combo entry / checkbox off / <c>glossVarianceScale</c> 1). Throws <see cref="NotSupportedException"/> for
    /// what APE refuses (unknown semantic/compression/mip names, image types other than Texture/Cube).
    /// </summary>
    public static ImageConversionSettings FromGdt(IReadOnlyDictionary<string, string> f)
    {
        string Get(string key, string def) => f.TryGetValue(key, out var v) ? v : def;
        bool Flag(string key) => f.TryGetValue(key, out var v) && v.Trim() is { Length: > 0 } t && t != "0" &&
                                  !(double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d == 0);

        var typeName = Get("imageType", "Texture");
        var type = typeName switch
        {
            "Texture" => ToolsGfxImageType.Texture,
            "Cube" => ToolsGfxImageType.Cube,
            "TextureArray" or "CubeArray" or "Volume" => throw new NotSupportedException($"Image type '{typeName}' not implemented"),
            _ => throw new NotSupportedException($"Unknown image type '{typeName}'"),
        };

        var semName = Get("semantic", "2d");
        if (!SemanticTable.TryGetValue(semName, out var sem))
            throw new NotSupportedException($"Unknown image semantic '{semName}'");

        var compName = Get("compressionMethod", sem == ToolsGfxImageSemantic.Color && semName is "diffuseMap" or "effectMap" ? "compressed high color" : "compressed");
        if (!CompressionTable.TryGetValue(compName, out int comp))
            throw new NotSupportedException($"Unknown image compression method '{compName}'");

        int mipMode = 6;
        if (!Flag("noMipMaps"))
        {
            var mm = Get("mipMode", "Average");
            if (!MipModeTable.TryGetValue(mm, out mipMode))
                throw new NotSupportedException($"Unknown image mipmap method '{mm}'");
        }

        var mb = Get("mipBase", "1/1");
        if (!MipBaseTable.TryGetValue(mb, out int mipBase))
            throw new NotSupportedException($"Unknown image mipBase '{mb}'");

        bool clampU = Flag("clampU"), clampV = Flag("clampV");
        if (type == ToolsGfxImageType.Cube)
            clampU = clampV = false;

        float var = 1.0f;
        if (sem == ToolsGfxImageSemantic.Lut)
        {
            comp = 5;
            mipMode = 6;
            clampU = clampV = true;
        }
        else if (sem == ToolsGfxImageSemantic.Normal)
        {
            var = f.TryGetValue("glossVarianceScale", out var gv) && float.TryParse(gv, NumberStyles.Float, CultureInfo.InvariantCulture, out var g) ? g : 1.0f;
            var = var >= 1.0f ? 1.0f : var < 0.0f ? 0.0f : var;
        }

        return new ImageConversionSettings
        {
            ImageType = type,
            Semantic = sem,
            Compression = comp,
            ClampU = clampU,
            ClampV = clampV,
            MipMode = mipMode,
            MipBase = mipBase,
            GlossVarianceScale = var,
            PremultipliedAlpha = Flag("premulAlpha"),
        };
    }
}
