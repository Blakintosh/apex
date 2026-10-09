using System.Buffers.Binary;
using System.Collections.Concurrent;
using Apex.Render.Data.Hashing;
using Apex.Render.Data.IO;

namespace Apex.Render.Data.Shaders;

/// <summary>
/// Identifies one compiled shader variant in the ToolsGfx cache.
/// </summary>
/// <param name="Source">HLSL source path as a techsetdef/APE names it (<c>gbuffer_lit.hlsl</c>,
/// <c>ToolsGfx/deferred_lighting.hlsl</c>). Only the lower-cased file name selects the cache folder.</param>
/// <param name="Stage">Pipeline stage.</param>
/// <param name="Defines">The caller's ordered define list (without the implicit TOOLSGFX/USING_HLSL/IS_* macros).</param>
/// <param name="Entry">Entry point; defaults to <c>&lt;stage&gt;_main</c>.</param>
/// <param name="Target">Profile; defaults to <c>&lt;stage&gt;_5_0</c>.</param>
public sealed record ShaderVariantKey(
    string Source,
    ShaderStage Stage,
    IReadOnlyList<ShaderDefine> Defines,
    string? Entry = null,
    string? Target = null)
{
    public string EntryPoint => Entry ?? ShaderStages.DefaultEntry(Stage);
    public string Profile => Target ?? ShaderStages.Target(Stage);

    /// <summary>Cache folder: the file name of <see cref="Source"/>, lower-cased.</summary>
    public string CacheFolder => Path.GetFileName(Source.Replace('\\', '/')).ToLowerInvariant();

    /// <summary>The <c>_deps</c> key (<see cref="CacheKeys.DefineKey"/>).</summary>
    public CacheHash DefineKey => CacheKeys.DefineKey(Defines);

    public override string ToString() =>
        $"{CacheFolder}:{EntryPoint}/{Profile} [{string.Join(", ", Defines)}]";
}

/// <summary>A dependency recorded in a <c>_deps</c> file.</summary>
/// <param name="Path">Relative source path, e.g. <c>toolsgfx\globals.hlsl</c>.</param>
/// <param name="Signature"><c>MD4(u32 0 || file bytes)</c> of that file when the variant was compiled.</param>
public sealed record ShaderDependency(string Path, CacheHash Signature);

/// <summary>
/// Parsed <c>*_deps.lz4</c>: <c>char root[256]; u8 sig[16]; u32 n; { char path[256]; u8 sig[16]; }[n]; u8 codeHash[16]</c>.
/// </summary>
public sealed record ShaderDepsFile(ShaderDependency Root, IReadOnlyList<ShaderDependency> Includes, CacheHash CodeHash)
{
    public static ShaderDepsFile Parse(byte[] data, string label)
    {
        var r = new ByteReader(data, label);
        var root = new ShaderDependency(r.FixedString(256), new CacheHash(r.Span(16)));
        int n = r.Count(272);
        var includes = new ShaderDependency[n];
        for (int i = 0; i < n; i++)
            includes[i] = new ShaderDependency(r.FixedString(256), new CacheHash(r.Span(16)));
        var code = new CacheHash(r.Span(16));
        r.ExpectEnd();
        return new ShaderDepsFile(root, includes, code);
    }
}

/// <summary>A cached shader variant resolved to DXBC.</summary>
public sealed class ShaderBinary
{
    public required ShaderVariantKey Key { get; init; }

    /// <summary>The DXBC container, ready for <c>ID3D11Device::Create*Shader</c>.</summary>
    public required byte[] Dxbc { get; init; }

    /// <summary>The define-list key naming the <c>_deps</c> file.</summary>
    public required CacheHash DefineKey { get; init; }

    /// <summary>The preprocessed-code key naming the blob (shared by all stages of one program).</summary>
    public required CacheHash CodeKey { get; init; }

    public required string DepsPath { get; init; }
    public required string BlobPath { get; init; }
    public required ShaderDepsFile Deps { get; init; }

    private DxbcReflection? _reflection;

    /// <summary>The DXBC's reflection, parsed on first use (a cached variant is bound by every technique using it).</summary>
    public DxbcReflection Reflection => _reflection ??= DxbcReflection.Parse(Dxbc);
}

/// <summary>Thrown when a variant is not in the ToolsGfx cache (APE would compile it from source; Apex cannot).</summary>
public sealed class ShaderCacheMissException : Exception
{
    public ShaderCacheMissException(ShaderVariantKey key, string message) : base(message) => Key = key;

    public ShaderVariantKey Key { get; }
}

/// <summary>
/// Two-level lookup into <c>share\assetconvert\ToolsGfx\shaders_modtools\v14\f8</c> (materials.md §1.2):
/// <code>
/// deps : &lt;folder&gt;/&lt;entry&gt;_&lt;target&gt;_&lt;DEFINEKEY&gt;_deps.lz4   -> codeHash
/// blob : &lt;folder&gt;/&lt;entry&gt;_&lt;target&gt;_&lt;CODEHASH&gt;.lz4         -> u32 size + DXBC
/// </code>
/// Thread-safe; loaded binaries are memoised.
/// </summary>
public sealed class ShaderCache
{
    private readonly ConcurrentDictionary<string, ShaderBinary> _loaded = new(StringComparer.Ordinal);

    public ShaderCache(ToolsGfxInstall install)
    {
        Install = install;
        Root = install.ShaderCacheDir;
    }

    public ToolsGfxInstall Install { get; }

    /// <summary>The <c>v14\f8</c> directory.</summary>
    public string Root { get; }

    /// <summary>Path of the <c>_deps</c> file a variant would use (whether or not it exists).</summary>
    public string GetDepsPath(ShaderVariantKey key) => GetDepsPath(key, key.DefineKey);

    private string GetDepsPath(ShaderVariantKey key, CacheHash defineKey) =>
        Path.Combine(Root, key.CacheFolder, $"{key.EntryPoint}_{key.Profile}_{defineKey.ToFileName()}_deps.lz4");

    /// <summary>True when the variant's <c>_deps</c> file exists.</summary>
    public bool Contains(ShaderVariantKey key) => File.Exists(GetDepsPath(key));

    /// <summary>Loads a variant's DXBC.</summary>
    /// <exception cref="ShaderCacheMissException">The deps file or the blob it names is missing.</exception>
    public ShaderBinary Load(ShaderVariantKey key)
    {
        var defineKey = key.DefineKey;
        var memoKey = $"{key.CacheFolder}|{key.EntryPoint}|{key.Profile}|{defineKey}";
        if (_loaded.TryGetValue(memoKey, out var hit))
            return hit;

        var folder = Path.Combine(Root, key.CacheFolder);
        var depsPath = GetDepsPath(key, defineKey);
        if (!File.Exists(depsPath))
            throw new ShaderCacheMissException(key, DescribeMiss(key, folder, depsPath));

        var deps = ShaderDepsFile.Parse(Lz4Container.ReadFile(depsPath), depsPath);
        var blobPath = Path.Combine(folder, $"{key.EntryPoint}_{key.Profile}_{deps.CodeHash.ToFileName()}.lz4");
        if (!File.Exists(blobPath))
        {
            throw new ShaderCacheMissException(key,
                $"Shader variant {key} has a deps file ({Path.GetFileName(depsPath)}) but its bytecode blob " +
                $"{Path.GetFileName(blobPath)} is missing (stale cache entry). Re-open the asset in APE to rebuild it.");
        }

        var dxbc = ExtractDxbc(Lz4Container.ReadFile(blobPath), blobPath);
        var binary = new ShaderBinary
        {
            Key = key,
            Dxbc = dxbc,
            DefineKey = defineKey,
            CodeKey = deps.CodeHash,
            DepsPath = depsPath,
            BlobPath = blobPath,
            Deps = deps,
        };
        return _loaded.GetOrAdd(memoKey, binary);
    }

    /// <summary>Non-throwing <see cref="Load"/>; <paramref name="error"/> explains a miss.</summary>
    public bool TryLoad(ShaderVariantKey key, out ShaderBinary? binary, out string? error)
    {
        try
        {
            binary = Load(key);
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is ShaderCacheMissException or InvalidDataException or IOException)
        {
            binary = null;
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Lists the define keys of every cached variant of a source/entry (for diagnostics).</summary>
    public IReadOnlyList<CacheHash> EnumerateDefineKeys(string source, ShaderStage stage, string? entry = null)
    {
        var probe = new ShaderVariantKey(source, stage, [], entry);
        var folder = Path.Combine(Root, probe.CacheFolder);
        if (!Directory.Exists(folder))
            return [];
        var prefix = $"{probe.EntryPoint}_{probe.Profile}_";
        var list = new List<CacheHash>();
        foreach (var f in Directory.EnumerateFiles(folder, prefix + "*_deps.lz4"))
        {
            var name = Path.GetFileName(f);
            if (CacheHash.TryParseFileName(name.AsSpan(prefix.Length, 32), out var h))
                list.Add(h);
        }
        return list;
    }

    /// <summary>Blob payload = <c>u32 dxbcSize</c> + DXBC container.</summary>
    internal static byte[] ExtractDxbc(byte[] payload, string label)
    {
        if (payload.Length < 8 + 32 || payload[4] != 'D' || payload[5] != 'X' || payload[6] != 'B' || payload[7] != 'C')
            throw new InvalidDataException($"No DXBC container in {label}.");
        // The u32 prefix normally equals the container's own size field; the container field is authoritative.
        int containerSize = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(4 + 24));
        if (containerSize <= 0 || containerSize > payload.Length - 4)
            throw new InvalidDataException($"DXBC size {containerSize} out of range in {label}.");
        return payload.AsSpan(4, containerSize).ToArray();
    }

    private string DescribeMiss(ShaderVariantKey key, string folder, string depsPath)
    {
        if (!Directory.Exists(folder))
            return $"Shader variant {key}: no cache folder '{key.CacheFolder}' under {Root}. APE has never compiled this source on this machine.";

        int variants = EnumerateDefineKeys(key.Source, key.Stage, key.Entry).Count;
        return $"Shader variant {key} is not cached: {Path.GetFileName(depsPath)} is missing " +
               $"({variants} other {key.EntryPoint}/{key.Profile} variant(s) exist in '{key.CacheFolder}'). " +
               "Preview the asset once in APE so it compiles this define set, or check the define order.";
    }
}
