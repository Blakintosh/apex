using Apex.Render.Data.Conversion;
using Apex.Render.Data.Conversion.XAnim;
using Apex.Render.Data.Conversion.XModel;
using Apex.Render.Data.Hashing;

namespace Apex.Render.Data.Assets;

/// <summary>How a cache file was found.</summary>
public enum CacheMatch
{
    /// <summary>The recomputed hash names an existing file: it is exactly what APE would load (from APE's cache, or
    /// produced by Apex's converter — see <see cref="CacheLocation.Origin"/>).</summary>
    Exact,

    /// <summary>No file with the computed hash (or no hash inputs): newest file with the right name used.</summary>
    ByName,
}

/// <summary>A located cache file.</summary>
public sealed record CacheLocation(string Path, CacheMatch Match, CacheHash? ExpectedHash)
{
    /// <summary>APE's cache, Apex's cache, or converted by Apex just now.</summary>
    public CacheOrigin Origin { get; init; } = CacheOrigin.Ape;

    /// <summary>True when the file was produced by Apex's converter (now or in an earlier session).</summary>
    public bool IsFromApex => Origin != CacheOrigin.Ape;

    /// <summary>
    /// The decompressed payload when this lookup converted the file just now (the same bytes as the file), so the
    /// caller can parse it without reading it back; null otherwise. Holding the location holds the payload.
    /// </summary>
    public byte[]? Payload { get; init; }
}

/// <summary>
/// Finds the xmesh / xbin / xanim cache files of a source asset (asset_caches.md §6.2-6.4). Lookup order
/// (conversion.md §4): APE's cache by exact hash -> Apex's cache (<see cref="ApexToolsGfxCache"/>) -> convert the
/// source now and write it to Apex's cache -> APE's newest file by name (stale, source edited since). Conversion
/// runs synchronously on the calling thread: call from a worker, never the UI thread.
/// </summary>
public sealed class ModelCacheLocator
{
    public ModelCacheLocator(ToolsGfxInstall install, ApexToolsGfxCache? apexCache = null, CachePolicy? policy = null)
    {
        Install = install;
        ApexCache = apexCache ?? ApexToolsGfxCache.Default;
        Policy = policy ?? CachePolicy.Default;
    }

    public ToolsGfxInstall Install { get; }

    /// <summary>Apex's own cache (converted assets).</summary>
    public ApexToolsGfxCache ApexCache { get; }

    public CachePolicy Policy { get; }

    /// <summary>The cache folder name of an xmodel_bin: its file stem, lower-cased.</summary>
    public static string XModelName(string xmodelBinPath) => Path.GetFileNameWithoutExtension(xmodelBinPath).ToLowerInvariant();

    /// <summary>Resolves a GDT xmodel <c>filename</c>-style value (relative to <c>model_export</c>) to an absolute path.</summary>
    public string ResolveModelExportPath(string gdtValue) => ResolveUnder(Install.ModelExportDir, gdtValue);

    /// <summary>Resolves a GDT xanim <c>filename</c> value (relative to <c>xanim_export</c>) to an absolute path.</summary>
    public string ResolveXAnimExportPath(string gdtValue) => ResolveUnder(Install.XAnimExportDir, gdtValue);

    private static string ResolveUnder(string root, string value)
    {
        var v = value.Trim().Trim('"').Replace("\\\\", "\\").Replace('/', '\\');
        while (v.Contains(@"\\", StringComparison.Ordinal)) v = v.Replace(@"\\", @"\");
        return Path.IsPathRooted(v) ? v : Path.GetFullPath(Path.Combine(root, v.TrimStart('\\')));
    }

    /// <summary>
    /// Locates the xmesh cache of one LOD: <c>xmeshes\v39\&lt;xmodelName&gt;\pct&lt;P&gt;_&lt;hash&gt;.lz4</c>, converting the
    /// xmodel_bin into Apex's cache when neither cache has it (authored LODs only; autogen LODs, P &lt; 100, are not
    /// converted).
    /// </summary>
    /// <param name="xmodelBinPath">Absolute path of the LOD's .xmodel_bin (for autogen LODs: the LOD0 file).</param>
    /// <param name="parameters">GDT scale/priority values (<see cref="XModelLodParameters.FromGdt"/>).</param>
    /// <param name="percent">100 for authored LODs, else the autogen percent.</param>
    /// <param name="autogenSourcePath">For autogen LODs built from another file: that LOD0 path string (as APE stores it).</param>
    public CacheLocation? FindXMesh(string xmodelBinPath, XModelLodParameters parameters, int percent = 100, string? autogenSourcePath = null)
    {
        var name = XModelName(xmodelBinPath);
        var dir = Path.Combine(Install.XMeshCacheDir, name);
        CacheHash? expected = null;
        if (File.Exists(xmodelBinPath))
        {
            var sig = CacheKeys.FileSignature(xmodelBinPath);
            var autoSig = autogenSourcePath is not null && File.Exists(autogenSourcePath) ? CacheKeys.FileSignature(autogenSourcePath) : default;
            expected = CacheKeys.XMeshHash(sig, parameters, autogenSourcePath, autoSig);
            var file = $"pct{percent}_{expected.Value.ToFileName()}.lz4";
            var exact = Path.Combine(dir, file);
            if (Policy.UseApeCache && File.Exists(exact))
                return new CacheLocation(exact, CacheMatch.Exact, expected);
            var converted = FromApex(Path.Combine(ApexCache.XMeshCacheDir, name, file), $"{Path.GetFileName(xmodelBinPath)} ({percent}%)",
                percent == 100 ? () => XModelConverter.ConvertXMesh(xmodelBinPath, parameters, percent, autogenSourcePath) : null, expected);
            if (converted is not null)
                return converted;
        }
        return Policy.UseApeCache ? Newest(dir, $"pct{percent}_*.lz4", expected) : null;
    }

    /// <summary>Locates the xbin (material list) cache: <c>xbins\v1\&lt;xmodelName&gt;\&lt;Sig(xmodel_bin)&gt;.lz4</c>.</summary>
    public CacheLocation? FindXBin(string xmodelBinPath)
    {
        var name = XModelName(xmodelBinPath);
        var dir = Path.Combine(Install.XBinCacheDir, name);
        CacheHash? expected = null;
        if (File.Exists(xmodelBinPath))
        {
            expected = CacheKeys.FileSignature(xmodelBinPath);
            var file = expected.Value.ToFileName() + ".lz4";
            var exact = Path.Combine(dir, file);
            if (Policy.UseApeCache && File.Exists(exact))
                return new CacheLocation(exact, CacheMatch.Exact, expected);
            var converted = FromApex(Path.Combine(ApexCache.XBinCacheDir, name, file), Path.GetFileName(xmodelBinPath) + " (materials)",
                () => XModelConverter.ConvertXBin(xmodelBinPath), expected);
            if (converted is not null)
                return converted;
        }
        return Policy.UseApeCache ? Newest(dir, "*.lz4", expected) : null;
    }

    /// <summary>
    /// Locates an xanim cache converted for one model:
    /// <c>xanims\v11\&lt;anim&gt;\&lt;anim&gt;_&lt;animHash&gt;_&lt;model&gt;_&lt;Sig(model xmodel_bin)&gt;.lz4</c>, converting it
    /// into Apex's cache when neither cache has it.
    /// </summary>
    /// <param name="animName">xanim asset name (folder and file prefix).</param>
    /// <param name="xanimBinPath">Absolute path of the .xanim_bin (GDT <c>filename</c> under xanim_export), or null.</param>
    /// <param name="type">GDT <c>type</c> ("delta", "relative", "absolute", "additive").</param>
    /// <param name="looping">GDT <c>looping</c> != 0.</param>
    /// <param name="useBones">GDT <c>useBones</c> != 0.</param>
    /// <param name="node">GDT <c>node</c> (usually empty).</param>
    /// <param name="modelName">Target model name (GDT <c>model</c> stem or the preview model).</param>
    /// <param name="modelXModelBinPath">Absolute path of the target model's .xmodel_bin, or null.</param>
    public CacheLocation? FindXAnim(string animName, string? xanimBinPath, string type, bool looping, bool useBones, string node,
        string modelName, string? modelXModelBinPath)
    {
        var dir = Path.Combine(Install.XAnimCacheDir, animName);
        CacheHash? expected = null;
        if (xanimBinPath is not null && File.Exists(xanimBinPath) && modelXModelBinPath is not null && File.Exists(modelXModelBinPath))
        {
            var animHash = CacheKeys.XAnimHash(CacheKeys.FileSignature(xanimBinPath), type, looping, useBones, node);
            var modelHash = CacheKeys.FileSignature(modelXModelBinPath);
            expected = animHash;
            var file = XAnimConverter.CacheFileName(animName, animHash, modelName, modelHash);
            var exact = Path.Combine(dir, file);
            if (Policy.UseApeCache && File.Exists(exact))
                return new CacheLocation(exact, CacheMatch.Exact, expected);
            var settings = new XAnimSettings(type, looping, useBones, node);
            var converted = FromApex(Path.Combine(ApexCache.XAnimCacheDir, animName, file), $"{animName} on {modelName}",
                () => XAnimConverter.ConvertFiles(xanimBinPath, modelXModelBinPath, settings), expected);
            if (converted is not null)
                return converted;
        }
        return Policy.UseApeCache ? Newest(dir, $"{animName}_*_{modelName}_*.lz4", expected) : null;
    }

    /// <summary>Apex's cache entry, converting when allowed; null when unavailable or the conversion failed.</summary>
    private CacheLocation? FromApex(string path, string label, Func<byte[]>? convert, CacheHash? expected)
    {
        if (Policy.UseApexCache && File.Exists(path) && new FileInfo(path).Length > 4)
        {
            ConversionScope.Report(new ConversionEvent(label, path, CacheOrigin.ApexCache, 0, null));
            return new CacheLocation(path, CacheMatch.Exact, expected) { Origin = CacheOrigin.ApexCache };
        }
        if (!Policy.ConvertMissing || convert is null)
            return null;
        try
        {
            var p = ApexCache.GetOrConvert(path, label, convert, out var origin, out var payload, reconvert: !Policy.UseApexCache);
            return new CacheLocation(p, CacheMatch.Exact, expected) { Origin = origin, Payload = payload };
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or XAnimConversionException
                                       or IndexOutOfRangeException or ArgumentException or InvalidOperationException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static CacheLocation? Newest(string dir, string pattern, CacheHash? expected)
    {
        if (!Directory.Exists(dir))
            return null;
        // Header-only files are interrupted APE writes; never pick them.
        var f = new DirectoryInfo(dir).EnumerateFiles(pattern).Where(x => x.Length > 4)
            .OrderByDescending(x => x.LastWriteTimeUtc).FirstOrDefault();
        return f is null ? null : new CacheLocation(f.FullName, CacheMatch.ByName, expected);
    }
}
