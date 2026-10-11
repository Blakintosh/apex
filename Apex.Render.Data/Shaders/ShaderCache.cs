using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using Apex.Render.Data.Conversion;
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

    /// <summary>APE's cache, or Apex's (compiled now or in an earlier session) for a source APE has no current build of.</summary>
    public CacheOrigin Origin { get; init; } = CacheOrigin.Ape;

    /// <summary>The compiler's warnings when Apex compiled this variant just now; null otherwise.</summary>
    public string? CompileWarnings { get; init; }

    private DxbcReflection? _reflection;

    /// <summary>The DXBC's reflection, parsed on first use (a cached variant is bound by every technique using it).</summary>
    public DxbcReflection Reflection => _reflection ??= DxbcReflection.Parse(Dxbc);
}

/// <summary>Thrown when a variant is not in the ToolsGfx cache and there is no source to compile it from.</summary>
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
/// <para>A variant whose source is under <c>share\raw\shaders_stable_toolsgfx</c> (a custom shader; the core sources
/// are not shipped) is compiled from it when no cache has a current build, as APE does on a miss
/// (<see cref="ShaderSourceCompiler"/>). Lookup order: APE's cache (as is when there is no source; with a source, only
/// while every file its <c>_deps</c> names is unchanged) -> Apex's cache (<see cref="ApexToolsGfxCache.ShaderCacheDir"/>,
/// same layout and check) -> compile, writing to Apex's cache only. An edit to a source or an include therefore
/// recompiles on the next load; a memoised source-backed variant re-checks its files' write times on every load.</para>
/// Thread-safe; loaded binaries are memoised.
/// </summary>
public sealed class ShaderCache
{
    // A loaded variant and, for one built from a source, the (path, write time, size) of every file it came from.
    private sealed record Loaded(ShaderBinary Binary, (string Path, long Ticks, long Size)[] Stamps)
    {
        public bool IsCurrent()
        {
            foreach (var (path, ticks, size) in Stamps)
            {
                var f = new FileInfo(path);
                if (!f.Exists || f.LastWriteTimeUtc.Ticks != ticks || f.Length != size)
                    return false;
            }
            return true;
        }
    }

    private readonly ConcurrentDictionary<string, Loaded> _loaded = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, object> _locks = new(StringComparer.Ordinal);

    /// <param name="install">The install whose cache and sources to read.</param>
    /// <param name="apexCache">Where compiled variants go; null = <see cref="ApexToolsGfxCache.Default"/>.</param>
    /// <param name="policy">Which caches to use and whether to compile; null = <see cref="CachePolicy.Default"/>.</param>
    public ShaderCache(ToolsGfxInstall install, ApexToolsGfxCache? apexCache = null, CachePolicy? policy = null)
    {
        Install = install;
        Root = install.ShaderCacheDir;
        SourceRoot = install.ShaderSourceDir;
        ApexCache = apexCache ?? ApexToolsGfxCache.Default;
        Policy = policy ?? CachePolicy.Default;
    }

    public ToolsGfxInstall Install { get; }

    /// <summary>The <c>v14\f8</c> directory.</summary>
    public string Root { get; }

    /// <summary>The include root of shader sources (<see cref="ToolsGfxInstall.ShaderSourceDir"/>).</summary>
    public string SourceRoot { get; }

    /// <summary>Where compiled variants are written.</summary>
    public ApexToolsGfxCache ApexCache { get; }

    public CachePolicy Policy { get; }

    /// <summary>Path of the <c>_deps</c> file a variant would use (whether or not it exists).</summary>
    public string GetDepsPath(ShaderVariantKey key) => GetDepsPath(key, key.DefineKey);

    private string GetDepsPath(ShaderVariantKey key, CacheHash defineKey) =>
        Path.Combine(Root, key.CacheFolder, $"{key.EntryPoint}_{key.Profile}_{defineKey.ToFileName()}_deps.lz4");

    /// <summary>True when the variant's <c>_deps</c> file exists in APE's cache.</summary>
    public bool Contains(ShaderVariantKey key) => File.Exists(GetDepsPath(key));

    /// <summary>The variant's source file under <see cref="SourceRoot"/>, or null (always, for the core shaders).</summary>
    public string? FindSource(ShaderVariantKey key) => ShaderSourceCompiler.FindSource(SourceRoot, key.Source);

    /// <summary>Loads a variant's DXBC, compiling it from its source when no cache holds a current build.</summary>
    /// <exception cref="ShaderCacheMissException">Not cached and there is no source (or compiling is off).</exception>
    /// <exception cref="ShaderCompileException">The source failed to compile.</exception>
    public ShaderBinary Load(ShaderVariantKey key)
    {
        var defineKey = key.DefineKey;
        var memoKey = $"{key.CacheFolder}|{key.EntryPoint}|{key.Profile}|{defineKey}";
        if (_loaded.TryGetValue(memoKey, out var hit) && hit.IsCurrent())
            return Record(hit).Binary;
        lock (_locks.GetOrAdd(memoKey, _ => new object()))
        {
            if (_loaded.TryGetValue(memoKey, out hit) && hit.IsCurrent())
                return Record(hit).Binary;
            Loaded loaded;
            try
            {
                loaded = LoadUncached(key, defineKey);
            }
            catch (ShaderCompileException ex)
            {
                // A fix to the source must reload whatever failed to build from it.
                SourceRecording.Add(ex.Files);
                throw;
            }
            _loaded[memoKey] = loaded;
            return Record(loaded).Binary;
        }
    }

    // ── Which source files a load used (for callers that cache what they built from shaders) ──

    private static readonly AsyncLocal<SourceRecording?> Recording = new();

    /// <summary>
    /// Records the shader source files every <see cref="Load"/> on this async flow (and the parallel work it starts)
    /// used or failed to compile, until disposed; enclosing recordings see them too. Variants with no source (the core
    /// shaders) add nothing. A caller that keeps what it built (a prepared preview model) rebuilds it when one changes.
    /// </summary>
    public static SourceRecording RecordSources() => new();

    /// <summary>A <see cref="RecordSources"/> scope.</summary>
    public sealed class SourceRecording : IDisposable
    {
        private readonly SourceRecording? _parent;
        private readonly HashSet<string> _files = new(StringComparer.OrdinalIgnoreCase);

        internal SourceRecording()
        {
            _parent = Recording.Value;
            Recording.Value = this;
        }

        /// <summary>The files recorded so far (full paths).</summary>
        public IReadOnlyList<string> Files
        {
            get { lock (_files) return _files.ToArray(); }
        }

        internal static void Add(IEnumerable<string> files)
        {
            var list = files as IReadOnlyCollection<string> ?? files.ToList();
            for (var r = Recording.Value; r is not null; r = r._parent)
                lock (r._files)
                    r._files.UnionWith(list);
        }

        public void Dispose()
        {
            if (Recording.Value == this)
                Recording.Value = _parent;
        }
    }

    private static Loaded Record(Loaded loaded)
    {
        if (loaded.Stamps.Length > 0)
            SourceRecording.Add(loaded.Stamps.Select(s => s.Path).ToList());
        return loaded;
    }

    private Loaded LoadUncached(ShaderVariantKey key, CacheHash defineKey)
    {
        var source = FindSource(key);
        var folder = Path.Combine(Root, key.CacheFolder);
        var depsPath = GetDepsPath(key, defineKey);

        // APE's cache: as is when its build cannot be redone here (the core shaders: no source, or headers that are not
        // shipped), else only while every file its _deps names is unchanged.
        bool apeStale = false;
        if (Policy.UseApeCache && File.Exists(depsPath))
        {
            var deps = ShaderDepsFile.Parse(Lz4Container.ReadFile(depsPath), depsPath);
            var blobPath = BlobPath(folder, key, deps.CodeHash);
            bool rebuildable = source is not null && ShaderSourceCompiler.HasAllSources(deps, SourceRoot);
            if (!rebuildable || ShaderSourceCompiler.IsCurrent(deps, SourceRoot))
            {
                if (File.Exists(blobPath))
                    return new Loaded(Binary(key, defineKey, depsPath, blobPath, deps, CacheOrigin.Ape), rebuildable ? Stamps(deps) : []);
                if (!rebuildable)
                {
                    throw new ShaderCacheMissException(key,
                        $"Shader variant {key} has a deps file ({Path.GetFileName(depsPath)}) but its bytecode blob " +
                        $"{Path.GetFileName(blobPath)} is missing (stale cache entry). Re-open the asset in APE to rebuild it.");
                }
            }
            else
                apeStale = true;
        }
        if (source is null)
            throw new ShaderCacheMissException(key, DescribeMiss(key, folder, depsPath));
        if (!Policy.ConvertMissing)
        {
            throw new ShaderCacheMissException(key, $"Shader variant {key} is not cached" +
                (apeStale ? " (APE's build predates its source)" : "") + " and compiling is off (APEX_TOOLSGFX_CACHE).");
        }

        // Apex's cache, then compile.
        var apexFolder = Path.Combine(ApexCache.ShaderCacheDir, key.CacheFolder);
        var apexDepsPath = Path.Combine(apexFolder, Path.GetFileName(depsPath));
        var label = $"{key.Source} {key.EntryPoint}";
        if (Policy.UseApexCache && File.Exists(apexDepsPath))
        {
            try
            {
                var deps = ShaderDepsFile.Parse(Lz4Container.ReadFile(apexDepsPath), apexDepsPath);
                bool current = ShaderSourceCompiler.IsCurrent(deps, SourceRoot);
                // Sources edited since: the build of their current contents may still be there (an edit undone).
                if (!current && ShaderSourceCompiler.Resign(key, deps, SourceRoot) is { } now && File.Exists(BlobPath(apexFolder, key, now.CodeHash)))
                {
                    Lz4Container.WriteFile(apexDepsPath, ShaderSourceCompiler.Serialize(now));
                    (deps, current) = (now, true);
                }
                var blobPath = BlobPath(apexFolder, key, deps.CodeHash);
                if (current && File.Exists(blobPath))
                {
                    var binary = Binary(key, defineKey, apexDepsPath, blobPath, deps, CacheOrigin.ApexCache);
                    ConversionScope.Report(new ConversionEvent(label, blobPath, CacheOrigin.ApexCache, 0, null));
                    return new Loaded(binary, Stamps(deps));
                }
            }
            catch (InvalidDataException)
            {
                // A damaged entry is rebuilt below.
            }
        }

        var sw = Stopwatch.StartNew();
        ShaderSourceCompiler.Result compiled;
        try
        {
            compiled = ShaderSourceCompiler.Compile(SourceRoot, key);
        }
        catch (Exception ex) when (ex is ShaderCompileException or IOException)
        {
            ConversionScope.Report(new ConversionEvent(label, apexDepsPath, CacheOrigin.ApexConverted, sw.Elapsed.TotalMilliseconds, ex.Message));
            throw;
        }
        // The blob is named after the sources it came from, so it goes first; the deps file then points at it.
        var newBlob = BlobPath(apexFolder, key, compiled.Deps.CodeHash);
        var payload = new byte[4 + compiled.Dxbc.Length];
        BinaryPrimitives.WriteInt32LittleEndian(payload, compiled.Dxbc.Length);
        compiled.Dxbc.CopyTo(payload, 4);
        Lz4Container.WriteFile(newBlob, payload);
        Lz4Container.WriteFile(apexDepsPath, ShaderSourceCompiler.Serialize(compiled.Deps));
        ConversionScope.Report(new ConversionEvent(label, newBlob, CacheOrigin.ApexConverted, sw.Elapsed.TotalMilliseconds, null));
        var result = new ShaderBinary
        {
            Key = key,
            Dxbc = compiled.Dxbc,
            DefineKey = defineKey,
            CodeKey = compiled.Deps.CodeHash,
            DepsPath = apexDepsPath,
            BlobPath = newBlob,
            Deps = compiled.Deps,
            Origin = CacheOrigin.ApexConverted,
            CompileWarnings = compiled.Warnings,
        };
        return new Loaded(result, Stamps(compiled.Deps));
    }

    private static string BlobPath(string folder, ShaderVariantKey key, CacheHash codeHash) =>
        Path.Combine(folder, $"{key.EntryPoint}_{key.Profile}_{codeHash.ToFileName()}.lz4");

    private static ShaderBinary Binary(ShaderVariantKey key, CacheHash defineKey, string depsPath, string blobPath,
        ShaderDepsFile deps, CacheOrigin origin) => new()
    {
        Key = key,
        Dxbc = ExtractDxbc(Lz4Container.ReadFile(blobPath), blobPath),
        DefineKey = defineKey,
        CodeKey = deps.CodeHash,
        DepsPath = depsPath,
        BlobPath = blobPath,
        Deps = deps,
        Origin = origin,
    };

    private (string Path, long Ticks, long Size)[] Stamps(ShaderDepsFile deps) =>
        deps.Includes.Prepend(deps.Root).Select(d =>
        {
            var f = new FileInfo(Path.Combine(SourceRoot, d.Path));
            return f.Exists ? (f.FullName, f.LastWriteTimeUtc.Ticks, f.Length) : (f.FullName, -1L, -1L);
        }).ToArray();

    /// <summary>Non-throwing <see cref="Load"/>; <paramref name="error"/> explains a miss.</summary>
    public bool TryLoad(ShaderVariantKey key, out ShaderBinary? binary, out string? error)
    {
        try
        {
            binary = Load(key);
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is ShaderCacheMissException or ShaderCompileException or InvalidDataException or IOException)
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
