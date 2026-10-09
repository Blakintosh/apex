using System.Buffers.Binary;
using System.Text;
using Apex.Render.Data.Shaders;

namespace Apex.Render.Data.Hashing;

/// <summary>
/// The hash recipes APE uses to name its cache files (materials.md §1, asset_caches.md §0/§6).
/// </summary>
public static class CacheKeys
{
    /// <summary>
    /// Shader <c>_deps</c> key (APE 0x14059C160) over the caller's ordered define list:
    /// <code>
    /// h = 0
    /// for (name, value): h = h.dword0 == 0 ? Seeded(name) : Chain(h, name); h = Chain(h, value)
    /// </code>
    /// Strings are hashed as ASCII without terminator. An empty list gives <see cref="CacheHash.Zero"/>.
    /// The implicit compile macros (TOOLSGFX/USING_HLSL/IS_*_SHADER) are not part of the key.
    /// </summary>
    public static CacheHash DefineKey(IEnumerable<ShaderDefine> defines)
    {
        var h = CacheHash.Zero;
        foreach (var d in defines)
        {
            var name = Encoding.ASCII.GetBytes(d.Name);
            h = h.Word0 == 0 ? Md4.Seeded(name) : Md4.Chain(h, name);
            h = Md4.Chain(h, Encoding.ASCII.GetBytes(d.Value));
        }
        return h;
    }

    /// <summary>
    /// APE's <c>DataSignature</c> of a file: <c>MD4(u32le(0) || file bytes)</c>. Memoised per
    /// (path, last-write time, size), like APE's <c>filesignature.db</c>, across sessions for big files
    /// (<see cref="SignatureStore"/>).
    /// </summary>
    public static CacheHash FileSignature(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
            throw new FileNotFoundException("Cannot sign a missing file.", path);

        var full = info.FullName;
        long ticks = info.LastWriteTimeUtc.Ticks, size = info.Length;
        if (SignatureStore.TryGet(full, ticks, size, out var known))
            return known;

        var md = new Md4();
        md.Append(stackalloc byte[4]);
        using (var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 0, FileOptions.SequentialScan))
        {
            var buf = new byte[1 << 20];
            int n;
            while ((n = fs.Read(buf, 0, buf.Length)) > 0)
                md.Append(buf.AsSpan(0, n));
        }
        var hash = md.Finish();
        SignatureStore.Add(full, ticks, size, hash);
        return hash;
    }

    /// <summary>
    /// Hash of a converted xmesh cache file (APE <c>ToolsGfx_XMesh_LoadOrConvert</c> 0x14067EBF0):
    /// <code>
    /// h = Sig(xmodel_bin)
    /// if (lod0Path != "" &amp;&amp; lod0Path != thisPath) { h = Chain(h, lod0Path); h = Chain(h, Sig(lod0)) }   // autogen LODs
    /// h = Chain(h, { i32 -1; f32 1; f32 pos; f32 normal; f32 uv; f32 color; f32 1; f32 scale })
    /// </code>
    /// The LOD percent is not part of the hash.
    /// </summary>
    public static CacheHash XMeshHash(CacheHash xmodelBinSignature, XModelLodParameters parameters,
        string? autogenSourcePath = null, CacheHash autogenSourceSignature = default)
    {
        var h = xmodelBinSignature;
        if (!string.IsNullOrEmpty(autogenSourcePath))
        {
            h = Md4.Chain(h, Encoding.ASCII.GetBytes(autogenSourcePath));
            h = Md4.Chain(h, autogenSourceSignature.ToArray());
        }

        Span<byte> block = stackalloc byte[32];
        BinaryPrimitives.WriteInt32LittleEndian(block, -1);
        BinaryPrimitives.WriteSingleLittleEndian(block[4..], 1.0f);
        BinaryPrimitives.WriteSingleLittleEndian(block[8..], parameters.PositionPriority);
        BinaryPrimitives.WriteSingleLittleEndian(block[12..], parameters.NormalPriority);
        BinaryPrimitives.WriteSingleLittleEndian(block[16..], parameters.UvPriority);
        BinaryPrimitives.WriteSingleLittleEndian(block[20..], parameters.ColorPriority);
        BinaryPrimitives.WriteSingleLittleEndian(block[24..], 1.0f);
        BinaryPrimitives.WriteSingleLittleEndian(block[28..], parameters.Scale);
        return Md4.Chain(h, block);
    }

    /// <summary>
    /// Anim half of an xanim cache name (APE <c>ToolsGfx_XAnim_LoadOrConvert</c> 0x1406368B0):
    /// <c>Chain(Chain(Chain(Chain(Sig(xanim_bin), type), u8 looping), u8 useBones), node)</c>.
    /// </summary>
    public static CacheHash XAnimHash(CacheHash xanimBinSignature, string type, bool looping, bool useBones, string node)
    {
        var h = Md4.Chain(xanimBinSignature, Encoding.ASCII.GetBytes(type ?? string.Empty));
        h = Md4.Chain(h, [looping ? (byte)1 : (byte)0]);
        h = Md4.Chain(h, [useBones ? (byte)1 : (byte)0]);
        return Md4.Chain(h, Encoding.ASCII.GetBytes(node ?? string.Empty));
    }
}

/// <summary>
/// The GDT xmodel values that feed <see cref="CacheKeys.XMeshHash"/>. When the GDT has
/// <c>customAutogenParams == 0</c> APE uses <see cref="Default"/> priorities regardless of the stored
/// <c>lodPositionPriority</c>/<c>lodNormalPriority</c>/<c>LodUvPriority</c>/<c>LodColorPriority</c> fields.
/// </summary>
public readonly record struct XModelLodParameters(
    float PositionPriority,
    float NormalPriority,
    float UvPriority,
    float ColorPriority,
    float Scale)
{
    /// <summary>APE defaults (customAutogenParams = 0): 32, 1/32, 1/16, 1/32, scale 1.</summary>
    public static XModelLodParameters Default => new(32f, 0.03125f, 0.0625f, 0.03125f, 1f);

    /// <summary>
    /// Builds the parameters from GDT xmodel fields (missing or unparsable values fall back to the defaults).
    /// </summary>
    public static XModelLodParameters FromGdt(IReadOnlyDictionary<string, string> fields)
    {
        var d = Default;
        float scale = Read("scale", d.Scale);
        if (Read("customAutogenParams", 0) == 0)
            return d with { Scale = scale };

        return new XModelLodParameters(
            Read("lodPositionPriority", d.PositionPriority),
            Read("lodNormalPriority", d.NormalPriority),
            Read("LodUvPriority", d.UvPriority),
            Read("LodColorPriority", d.ColorPriority),
            scale);

        float Read(string key, float fallback)
        {
            foreach (var (k, v) in fields)
            {
                if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase)
                    && float.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f))
                    return f;
            }
            return fallback;
        }
    }
}
