using System.Text;
using Apex.Render.Data.Hashing;
using Apex.Render.Data.IO;
using Apex.Render.Data.Techsets;

namespace Apex.Render.Data.Assets;

/// <summary>One mip level of one face, tightly packed (BC: 4x4 blocks row-major).</summary>
public sealed class ImageLevel
{
    public required int Width { get; init; }
    public required int Height { get; init; }

    /// <summary>The cache's bits-per-pixel field (4 for BC1/BC4, 8 for BC3/BC6H/BC7/R8, 16, 32, 64).</summary>
    public required int BitsPerPixel { get; init; }

    public required byte[] Data { get; init; }

    /// <summary><c>D3D11_SUBRESOURCE_DATA.SysMemPitch</c>: <c>(w*bpp)/8</c>, x4 for block-compressed formats (one row of blocks).</summary>
    public int RowPitch(int dxgiFormat) =>
        DxgiFormats.IsBlockCompressed(dxgiFormat)
            ? Math.Max(1, (Width + 3) / 4) * DxgiFormats.BlockBytes(dxgiFormat)
            : Width * BitsPerPixel / 8;
}

/// <summary>A decoded ToolsGfx image v20 (one file = one face; cubes carry six).</summary>
public sealed class CachedImage
{
    /// <summary>DXGI_FORMAT of every level.</summary>
    public required int DxgiFormat { get; init; }

    /// <summary>0, 1 or 2 (probably opaque / punch-through / full alpha; default 2).</summary>
    public required int AlphaType { get; init; }

    /// <summary>Faces (array slices) x mip levels, largest level first. 2D textures have one face.</summary>
    public required IReadOnlyList<IReadOnlyList<ImageLevel>> Faces { get; init; }

    /// <summary>True when the image was assembled from six face files (TextureCube, ArraySize 6).</summary>
    public bool IsCube { get; init; }

    /// <summary>The cache files this image was read from (one per face).</summary>
    public required IReadOnlyList<string> CachePaths { get; init; }

    public int Width => Faces[0][0].Width;
    public int Height => Faces[0][0].Height;
    public int MipCount => Faces[0].Count;
    public int ArraySize => Faces.Count;

    /// <summary>
    /// Subresources in D3D11 order (<c>index = mip + face * MipCount</c>), ready for
    /// <c>D3D11_SUBRESOURCE_DATA</c>.
    /// </summary>
    public IEnumerable<(int Face, int Mip, ImageLevel Level)> Subresources()
    {
        for (int f = 0; f < Faces.Count; f++)
            for (int m = 0; m < Faces[f].Count; m++)
                yield return (f, m, Faces[f][m]);
    }

    public override string ToString() =>
        $"{DxgiFormats.Name(DxgiFormat)} {Width}x{Height} mips {MipCount}{(IsCube ? " cube" : ArraySize > 1 ? $" x{ArraySize}" : "")}";

    /// <summary>
    /// Parses an image v20 payload: <c>u32 dxgiFormat; u32 alphaType; u32 levelCount;
    /// { u32 w, h, bpp; u8 data[(w*h*bpp)&gt;&gt;3]; }[levelCount]</c>.
    /// </summary>
    public static (int Format, int AlphaType, List<ImageLevel> Levels) ParsePayload(byte[] data, string label)
    {
        var r = new ByteReader(data, label);
        int format = (int)r.U32();
        int alpha = (int)r.U32();
        int count = r.Count(12);
        var levels = new List<ImageLevel>(count);
        for (int i = 0; i < count; i++)
        {
            int w = (int)r.U32(), h = (int)r.U32(), bpp = (int)r.U32();
            long size = (long)w * h * bpp >> 3;
            if (size > r.Remaining)
                throw new InvalidDataException($"{label}: level {i} ({w}x{h} @ {bpp} bpp) overruns the file.");
            levels.Add(new ImageLevel { Width = w, Height = h, BitsPerPixel = bpp, Data = r.Bytes((int)size) });
        }
        r.ExpectEnd();
        return (format, alpha, levels);
    }
}

/// <summary>One file in the image cache.</summary>
/// <param name="SettingsDirectory">Settings folder, e.g. <c>Texture_color_mipAvg</c>.</param>
/// <param name="SourceFileName">Source image file name with extension (<c>foo_c.png</c>).</param>
/// <param name="Face">0 for 2D; 0..5 for cube faces.</param>
/// <param name="Hash"><c>Sig(source file)</c>.</param>
/// <param name="Path">Absolute path of the .lz4.</param>
public sealed record ImageCacheEntry(string SettingsDirectory, string SourceFileName, int Face, CacheHash Hash, string Path)
{
    /// <summary>Image type token (<c>Texture</c>, <c>Cube</c>, ...), the first part of the settings folder.</summary>
    public string ImageType => SettingsDirectory.Split('_')[0];

    /// <summary>Class token (<c>color</c>, <c>normal</c>, ...), the second part of the settings folder.</summary>
    public string ClassToken => SettingsDirectory.Split('_') is { Length: > 1 } p ? p[1] : string.Empty;

    public DateTime LastWriteUtc => File.GetLastWriteTimeUtc(Path);
}

/// <summary>
/// Settings that name an image cache folder (asset_caches.md §6.1). Only the folder name depends on these;
/// the file header alone is enough to load a cached image.
/// </summary>
public sealed record ImageSettings(
    int ImageType,
    ImageClass Class,
    int Compression = 0,
    bool ClampU = false,
    bool ClampV = false,
    int MipMode = 0,
    int MipBase = 0,
    int NormalVariance = 100,
    bool PremultipliedAlpha = false)
{
    private static readonly string[] TypeNames = ["Texture", "TextureArray", "Cube", "CubeArray", "Volume"];
    private static readonly string[] MipModes = ["_mipAvg", "_mipLum", "_mipAlpha", "_mipLumAlpha", "_mipMax", "_mipPunchThru", "_mipNone"];

    /// <summary>
    /// <c>ImageTypeName_Semantic[_uncomp|_lowColor|_noAlpha][_clampUV|_clampU|_clampV]_mipMode[_mipBaseN][_varN][_premul]</c>.
    /// </summary>
    public string ToDirectoryName()
    {
        var sb = new StringBuilder();
        sb.Append(TypeNames[ImageType]).Append('_').Append(ImageClasses.CacheToken(Class));
        if (Compression == 5) sb.Append("_uncomp");
        else if (Class == ImageClass.Color && Compression == 3) sb.Append("_lowColor");
        else if (Class == ImageClass.Color && Compression == 4) sb.Append("_noAlpha");
        if (ClampU && ClampV) sb.Append("_clampUV");
        else if (ClampU) sb.Append("_clampU");
        else if (ClampV) sb.Append("_clampV");
        sb.Append(MipModes[MipMode]);
        if (MipBase != 0) sb.Append("_mipBase").Append(MipBase);
        if (Class == ImageClass.Normal) sb.Append("_var").Append(NormalVariance);
        if (PremultipliedAlpha) sb.Append("_premul");
        return sb.ToString();
    }
}

/// <summary>
/// Reader and index for <c>share\assetconvert\ToolsGfx\images\v20\&lt;settings&gt;\&lt;src&gt;_&lt;face&gt;_&lt;hash&gt;.lz4</c>.
/// The hash is <c>Sig(source image file)</c> (nothing else is mixed in); the folder encodes the conversion
/// settings. Lookups prefer an exact (name, hash) match and fall back to name-only matches.
/// </summary>
public sealed class ImageCache
{
    private static readonly string[] CubeSuffixes = ["_ft", "_bk", "_lf", "_rt", "_up", "_dn"];

    private readonly object _indexLock = new();
    private Dictionary<string, List<ImageCacheEntry>>? _byName;

    public ImageCache(ToolsGfxInstall install)
    {
        Install = install;
        Root = install.ImageCacheDir;
    }

    public ToolsGfxInstall Install { get; }

    /// <summary>The <c>images\v20</c> folder.</summary>
    public string Root { get; }

    /// <summary>Loads one cache file (a single face).</summary>
    public static CachedImage LoadFile(string path) => FromPayload(Lz4Container.ReadFile(path), path);

    /// <summary>As <see cref="LoadFile"/> for the file's already decompressed payload.</summary>
    internal static CachedImage FromPayload(byte[] payload, string path)
    {
        var (format, alpha, levels) = CachedImage.ParsePayload(payload, path);
        return new CachedImage { DxgiFormat = format, AlphaType = alpha, Faces = [levels], CachePaths = [path] };
    }

    /// <summary>Loads six face files as one cube (face index = D3D array slice).</summary>
    public static CachedImage LoadCube(IReadOnlyList<string> facePaths) => LoadCube(facePaths, null);

    /// <summary>As <see cref="LoadCube(IReadOnlyList{string})"/>, taking a face's payload from <paramref name="payloads"/> where given.</summary>
    internal static CachedImage LoadCube(IReadOnlyList<string> facePaths, IReadOnlyList<byte[]?>? payloads)
    {
        if (facePaths.Count != 6)
            throw new ArgumentException("A cube needs exactly six face files.", nameof(facePaths));
        var faces = new List<IReadOnlyList<ImageLevel>>(6);
        int format = 0, alpha = 0;
        for (int i = 0; i < 6; i++)
        {
            var (f, a, levels) = CachedImage.ParsePayload(payloads?[i] ?? Lz4Container.ReadFile(facePaths[i]), facePaths[i]);

            if (i == 0) { format = f; alpha = a; }
            else if (f != format || levels.Count != faces[0].Count)
                throw new InvalidDataException($"Cube face {i} ({facePaths[i]}) does not match face 0.");
            faces.Add(levels);
        }
        return new CachedImage { DxgiFormat = format, AlphaType = alpha, Faces = faces, IsCube = true, CachePaths = facePaths.ToArray() };
    }

    /// <summary>Every cache entry whose source file name matches (case-insensitive), across all settings folders.</summary>
    public IReadOnlyList<ImageCacheEntry> FindBySourceName(string sourceFileName)
    {
        var index = EnsureIndex();
        return index.TryGetValue(Path.GetFileName(sourceFileName), out var list) ? list : [];
    }

    /// <summary>
    /// Finds the cache entry for a source image file: hashes it (<see cref="CacheKeys.FileSignature"/>) and
    /// matches (file name, hash). With several settings folders for the same file, <paramref name="settingsDirectory"/>
    /// wins, then entries whose class token matches <paramref name="imageClass"/>, then the newest file.
    /// Falls back to a name-only match (newest) when the source is missing or was edited since conversion;
    /// <paramref name="exact"/> reports which case applied.
    /// </summary>
    public ImageCacheEntry? Find(string sourceFilePath, ImageClass? imageClass, out bool exact,
        string? settingsDirectory = null, int face = 0)
    {
        exact = false;
        var candidates = FindBySourceName(sourceFilePath).Where(e => e.Face == face).ToList();
        if (candidates.Count == 0)
            return null;

        if (File.Exists(sourceFilePath))
        {
            var sig = CacheKeys.FileSignature(sourceFilePath);
            var hashed = candidates.Where(e => e.Hash == sig).ToList();
            if (hashed.Count > 0)
            {
                exact = true;
                candidates = hashed;
            }
        }

        return Pick(candidates, imageClass, settingsDirectory);
    }

    /// <summary>Finds and loads a 2D image by its source file (see <see cref="Find"/>); null when not cached.</summary>
    public CachedImage? Load(string sourceFilePath, ImageClass? imageClass, string? settingsDirectory = null)
    {
        var e = Find(sourceFilePath, imageClass, out _, settingsDirectory);
        return e is null ? null : LoadFile(e.Path);
    }

    /// <summary>
    /// Loads a cube map from its base source path (<c>sky_ft.exr</c> ... or the path of any face): the six
    /// faces come from <c>_ft,_bk,_lf,_rt,_up,_dn</c> files, cache face index 0..5.
    /// </summary>
    public CachedImage? LoadCubeFromSource(string anyFaceSourcePath, ImageClass? imageClass = null)
    {
        var dir = Path.GetDirectoryName(anyFaceSourcePath) ?? string.Empty;
        var ext = Path.GetExtension(anyFaceSourcePath);
        var stem = Path.GetFileNameWithoutExtension(anyFaceSourcePath);
        foreach (var s in CubeSuffixes)
        {
            if (stem.EndsWith(s, StringComparison.OrdinalIgnoreCase))
            {
                stem = stem[..^s.Length];
                break;
            }
        }

        var paths = new string[6];
        for (int i = 0; i < 6; i++)
        {
            var src = Path.Combine(dir, stem + CubeSuffixes[i] + ext);
            var e = Find(src, imageClass, out _, face: i);
            if (e is null)
                return null;
            paths[i] = e.Path;
        }
        return LoadCube(paths);
    }

    private static ImageCacheEntry Pick(List<ImageCacheEntry> candidates, ImageClass? imageClass, string? settingsDirectory)
    {
        if (settingsDirectory is not null)
        {
            var exactDir = candidates.FirstOrDefault(e => string.Equals(e.SettingsDirectory, settingsDirectory, StringComparison.OrdinalIgnoreCase));
            if (exactDir is not null)
                return exactDir;
        }
        IEnumerable<ImageCacheEntry> pool = candidates;
        if (imageClass is { } c)
        {
            var token = ImageClasses.CacheToken(c);
            var byClass = candidates.Where(e => string.Equals(e.ClassToken, token, StringComparison.OrdinalIgnoreCase)).ToList();
            if (byClass.Count > 0)
                pool = byClass;
        }
        return pool.OrderByDescending(e => e.LastWriteUtc).First();
    }

    /// <summary>Drops the folder index (call after APE converted new images).</summary>
    public void InvalidateIndex()
    {
        lock (_indexLock)
            _byName = null;
    }

    private Dictionary<string, List<ImageCacheEntry>> EnsureIndex()
    {
        lock (_indexLock)
        {
            if (_byName is not null)
                return _byName;
            var map = new Dictionary<string, List<ImageCacheEntry>>(StringComparer.OrdinalIgnoreCase);
            if (Directory.Exists(Root))
            {
                // ~140 settings folders, ~30k files: list the folders in parallel, then merge in folder order.
                var dirs = Directory.GetDirectories(Root);
                var perDir = new List<ImageCacheEntry>[dirs.Length];
                Parallel.For(0, dirs.Length, i =>
                {
                    var settings = Path.GetFileName(dirs[i]);
                    var entries = new List<ImageCacheEntry>();
                    foreach (var f in Directory.EnumerateFiles(dirs[i], "*.lz4"))
                        if (TryParseFileName(Path.GetFileName(f.AsSpan()), out var src, out int face, out var hash))
                            entries.Add(new ImageCacheEntry(settings, src, face, hash, f));
                    perDir[i] = entries;
                });
                foreach (var entries in perDir)
                    foreach (var entry in entries)
                    {
                        if (!map.TryGetValue(entry.SourceFileName, out var list))
                            map[entry.SourceFileName] = list = [];
                        list.Add(entry);
                    }
            }
            return _byName = map;
        }
    }

    /// <summary><c>&lt;src&gt;_&lt;digit&gt;_&lt;32 hex&gt;.lz4</c> (extension and hex case-insensitive).</summary>
    private static bool TryParseFileName(ReadOnlySpan<char> name, out string source, out int face, out CacheHash hash)
    {
        const int Suffix = 3 + 32 + 4;
        source = string.Empty;
        face = 0;
        hash = default;
        if (name.Length <= Suffix || !name.EndsWith(".lz4", StringComparison.OrdinalIgnoreCase))
            return false;
        var tail = name[^Suffix..];
        if (tail[0] != '_' || !char.IsDigit(tail[1]) || tail[2] != '_' || !CacheHash.TryParseFileName(tail.Slice(3, 32), out hash))
            return false;
        source = name[..^Suffix].ToString();
        face = tail[1] - '0';
        return true;
    }
}
