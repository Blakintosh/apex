using System.Buffers.Binary;
using Apex.Render.Data.Hashing;

namespace Apex.Render.Data.Conversion.Images;

/// <summary>
/// Source image -&gt; ToolsGfx image cache v20 payload, reproducing <c>ToolsGfx_Image_LoadOrConvert</c> (0x1403ED1F0) and
/// <c>Image_GenerateMipChain</c> (0x1403EC470). Pure and thread-safe: reads the source (and the install's native
/// DLLs), writes nothing. See conversion.md §2.
/// </summary>
public static class ImageConverter
{
    /// <summary>Cube face file suffixes (<c>gCubeNameSuffixes</c>), face index = array slice.</summary>
    public static readonly string[] CubeFaceSuffixes = ["_ft", "_bk", "_lf", "_rt", "_up", "_dn"];

    /// <summary>
    /// Source file of cube face <paramref name="face"/>: the first <c>_ft</c> in <paramref name="baseImagePath"/>
    /// replaced by the face suffix (0x1403ED010). Null when the path has no <c>_ft</c>.
    /// </summary>
    public static string? CubeFacePath(string baseImagePath, int face)
    {
        int i = baseImagePath.IndexOf(CubeFaceSuffixes[0], StringComparison.Ordinal);
        return i < 0 ? null : string.Concat(baseImagePath.AsSpan(0, i), CubeFaceSuffixes[face], baseImagePath.AsSpan(i + 3));
    }

    /// <summary>Cache file name <c>&lt;srcFileName&gt;_&lt;face&gt;_&lt;Sig(src)&gt;.lz4</c> for one face's source file.</summary>
    public static string CacheFileName(string sourcePath, int face = 0) =>
        $"{Path.GetFileName(sourcePath)}_{face}_{CacheKeys.FileSignature(sourcePath).ToFileName()}.lz4";

    /// <summary>Relative cache path <c>&lt;settingsDir&gt;\&lt;file&gt;</c> under <c>images\v20</c>.</summary>
    public static string CacheRelativePath(string sourcePath, ImageConversionSettings settings, int face = 0) =>
        Path.Combine(settings.SettingsDirectory, CacheFileName(sourcePath, face));

    /// <summary>
    /// Converts one face (a 2D texture is face 0; for cubes pass that face's own source file, see
    /// <see cref="CubeFacePath"/>) and returns the uncompressed v20 payload
    /// (<c>u32 dxgi, u32 alphaType, u32 levels, {u32 w,h,bpp; data}[]</c>).
    /// </summary>
    public static byte[] ConvertFace(ToolsGfxInstall install, string sourcePath, ImageConversionSettings settings, int face = 0)
    {
        var img = SourceImageLoader.Load(install, sourcePath);
        return ConvertFace(install, img, settings, face, sourcePath);
    }

    /// <summary>As <see cref="ConvertFace(ToolsGfxInstall,string,ImageConversionSettings,int)"/> for an already
    /// decoded 4-channel source (consumed: modified in place).</summary>
    public static byte[] ConvertFace(ToolsGfxInstall install, FloatImage img, ImageConversionSettings settings, int face, string label)
    {
        var s = settings;
        bool isCube = s.ImageType == ToolsGfxImageType.Cube;
        int mipBase = s.MipBase;
        bool doCompress = s.Compression != 5;
        int minDim = doCompress ? 4 : 1;
        if (img.Width >> mipBase < minDim || img.Height >> mipBase < minDim)
            mipBase = 0;
        if (img.Width == 0 || img.Height == 0)
            throw new InvalidDataException($"Invalid image size ({img.Width}x{img.Height}) for image '{label}'");
        if (isCube)
        {
            if (img.Width != img.Height)
                throw new InvalidDataException($"Invalid cube image size ({img.Width}x{img.Height}) for image '{label}'");
            ImageProcessing.OrientCubeFace(img, face);
        }

        int dxgi;
        switch (s.Semantic)
        {
            case ToolsGfxImageSemantic.HdrColor: dxgi = 10; break;
            case ToolsGfxImageSemantic.Scalar: dxgi = 61; img = ImageProcessing.ToScalar(img); break;
            case ToolsGfxImageSemantic.DualScalar: dxgi = 49; img = ImageProcessing.KeepChannels(img, 2); break;
            case ToolsGfxImageSemantic.QuadScalar: dxgi = 28; break;
            case ToolsGfxImageSemantic.Normal: dxgi = 28; ImageProcessing.PrepareNormals(img); break;
            case ToolsGfxImageSemantic.Lut: dxgi = 24; break;
            default:
                dxgi = 29;
                ImageProcessing.SrgbToLinear(img);
                if (s.PremultipliedAlpha)
                    ImageProcessing.Premultiply(img);
                break;
        }

        int w0 = img.Width >> mipBase, h0 = img.Height >> mipBase;
        if (doCompress)
        {
            if (s.MipMode != 6 && (((w0 - 1) & w0) != 0 || ((h0 - 1) & h0) != 0))
                throw new InvalidDataException($"Can't compress image '{label}'. Dimensions must be power of 2");
            if ((w0 & 3) != 0 || (h0 & 3) != 0)
                throw new InvalidDataException($"Can't compress image '{label}'. Dimensions must be multiple of 4");
        }

        int alphaType = isCube || s.Semantic == ToolsGfxImageSemantic.QuadScalar ? 2
            : s.Semantic == ToolsGfxImageSemantic.Color ? ImageProcessing.ComputeAlphaType(img) : 0;

        if (doCompress)
        {
            dxgi = s.Semantic switch
            {
                ToolsGfxImageSemantic.Scalar => 80,
                ToolsGfxImageSemantic.DualScalar => 83,
                ToolsGfxImageSemantic.QuadScalar or ToolsGfxImageSemantic.Normal => 98,
                ToolsGfxImageSemantic.HdrColor => 95,
                _ => s.Compression == 3 ? (alphaType == 2 ? 78 : 72) : s.Compression == 4 ? 72 : 99,
            };
        }

        var levels = GenerateMipChain(install, img, dxgi, alphaType, s, mipBase, doCompress);
        return WritePayload(dxgi, alphaType, levels);
    }

    /// <summary>Image_GenerateMipChain (0x1403EC470).</summary>
    private static List<PackedLevel> GenerateMipChain(ToolsGfxInstall install, FloatImage img, int dxgi, int alphaType,
        ImageConversionSettings s, int mipBase, bool doCompress)
    {
        int minDim = doCompress ? 4 : 1;
        bool normal = s.Semantic == ToolsGfxImageSemantic.Normal;
        int filterMode = s.MipMode == 6 ? 0 : s.MipMode;
        float var = s.GlossVarianceScale;

        FloatImage? varBase = null, varBlur = null;
        if (normal)
        {
            varBase = ImageProcessing.NormalXyz(img);
            varBlur = ImageProcessing.Blur3(varBase, s.ClampU, s.ClampV, var * 0.05f);
        }

        for (int k = 0; k < mipBase; k++)
        {
            if ((img.Width & 1) != 0 || img.Width <= minDim || (img.Height & 1) != 0 || img.Height <= minDim)
                throw new InvalidDataException($"Failed to reach mipBase {mipBase}");
            img = ImageProcessing.Downsample(img, filterMode);
            if (normal)
                (varBase, varBlur) = NextVariance(varBase!, s, k, var);
        }

        var levels = new List<PackedLevel>();
        for (int level = mipBase; ; level++)
        {
            FloatImage? next = null;
            if (s.MipMode != 6 && (img.Width & 1) == 0 && img.Width > minDim && (img.Height & 1) == 0 && img.Height > minDim)
                next = ImageProcessing.Downsample(img, s.MipMode);

            if (s.Semantic == ToolsGfxImageSemantic.Color)
                ImageProcessing.LinearToSrgb(img);
            else if (normal)
                ImageProcessing.ApplyNormalVariance(img, varBlur!);

            var packed = s.Semantic switch
            {
                ToolsGfxImageSemantic.HdrColor => ImageProcessing.ToHalf4(img),
                ToolsGfxImageSemantic.Lut => ImageProcessing.ToR10G10B10A2(img),
                _ => ImageProcessing.ToUnorm8(img),
            };
            if (doCompress)
                packed = BlockCompression.Compress(install, packed, dxgi, alphaType);
            levels.Add(packed);

            if (next is null)
                break;
            img = next;
            if (normal)
                (varBase, varBlur) = NextVariance(varBase!, s, level, var);
        }
        return levels;
    }

    /// <summary>0x140407E90: box-downsample the unblurred average normals and blur them with
    /// <c>w = (0.05 - clamp01(level/6) * 0.045) * glossVarianceScale</c>.</summary>
    private static (FloatImage Base, FloatImage Blur) NextVariance(FloatImage varBase, ImageConversionSettings s, int level, float var)
    {
        float t = level / 6.0f;
        t = 1.0f > t ? (0.0f > t ? 0.0f : t) : 1.0f;
        var b = ImageProcessing.Box2(varBase);
        float w = (0.05f - t * 0.045f) * var;
        return (b, ImageProcessing.Blur3(b, s.ClampU, s.ClampV, w));
    }

    private static byte[] WritePayload(int dxgi, int alphaType, List<PackedLevel> levels)
    {
        long size = 12;
        foreach (var l in levels)
            size += 12 + l.Data.Length;
        var o = new byte[size];
        var sp = o.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(sp, (uint)dxgi);
        BinaryPrimitives.WriteUInt32LittleEndian(sp[4..], (uint)alphaType);
        BinaryPrimitives.WriteUInt32LittleEndian(sp[8..], (uint)levels.Count);
        int p = 12;
        foreach (var l in levels)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(sp[p..], (uint)l.Width);
            BinaryPrimitives.WriteUInt32LittleEndian(sp[(p + 4)..], (uint)l.Height);
            BinaryPrimitives.WriteUInt32LittleEndian(sp[(p + 8)..], (uint)l.BitsPerPixel);
            p += 12;
            l.Data.CopyTo(sp[p..]);
            p += l.Data.Length;
        }
        return o;
    }
}
