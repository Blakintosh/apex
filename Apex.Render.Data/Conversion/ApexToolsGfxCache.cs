using System.Collections.Concurrent;
using System.Diagnostics;
using Apex.Render.Data.IO;

namespace Apex.Render.Data.Conversion;

/// <summary>Where a cache file came from.</summary>
public enum CacheOrigin
{
    /// <summary>APE's own cache under the install's <c>share\assetconvert\ToolsGfx</c> (read-only).</summary>
    Ape,

    /// <summary>Apex's cache, converted by Apex in an earlier session.</summary>
    ApexCache,

    /// <summary>Converted by Apex just now (and written to Apex's cache).</summary>
    ApexConverted,
}

/// <summary>
/// Which caches the data layer may use. Lookup order is always APE's cache (exact hash) -> Apex's cache -> convert
/// the source on demand -> APE's newest file by name (stale). <c>APEX_TOOLSGFX_CACHE</c> overrides the defaults for
/// testing: <c>apex-only</c> (ignore APE's cache), <c>ape-only</c> (never convert), <c>no-apex-cache</c> (always
/// re-convert, still writing the result).
/// </summary>
public sealed record CachePolicy(bool UseApeCache = true, bool UseApexCache = true, bool ConvertMissing = true)
{
    public static CachePolicy Default { get; } = FromEnvironment();

    public static CachePolicy FromEnvironment() =>
        (Environment.GetEnvironmentVariable("APEX_TOOLSGFX_CACHE") ?? "").Trim().ToLowerInvariant() switch
        {
            "apex-only" => new CachePolicy(UseApeCache: false),
            "ape-only" => new CachePolicy(UseApexCache: false, ConvertMissing: false),
            "no-apex-cache" => new CachePolicy(UseApexCache: false),
            _ => new CachePolicy(),
        };
}

/// <summary>
/// Apex's own ToolsGfx cache (<c>%LOCALAPPDATA%\Apex\ToolsGfxCache</c>): the same directory layout, file names and
/// LZ4 container as APE's (<c>xmeshes\v39</c>, <c>xbins\v1</c>, <c>images\v20</c>, <c>xanims\v11</c>), so the cache
/// readers load either unchanged. Names are content hashes of the source files, so one cache can serve any install.
/// Nothing is ever written into the game install.
/// </summary>
public sealed class ApexToolsGfxCache
{
    private readonly ConcurrentDictionary<string, Lazy<byte[]>> _inFlight = new(StringComparer.OrdinalIgnoreCase);

    public ApexToolsGfxCache(string root)
    {
        Root = Path.GetFullPath(root);
        XMeshCacheDir = Path.Combine(Root, "xmeshes", $"v{ToolsGfxInstall.XMeshCacheVersion}");
        XBinCacheDir = Path.Combine(Root, "xbins", $"v{ToolsGfxInstall.XBinCacheVersion}");
        ImageCacheDir = Path.Combine(Root, "images", $"v{ToolsGfxInstall.ImageCacheVersion}");
        XAnimCacheDir = Path.Combine(Root, "xanims", $"v{ToolsGfxInstall.XAnimCacheVersion}");
    }

    /// <summary><c>%LOCALAPPDATA%\Apex\ToolsGfxCache</c>, or <c>APEX_TOOLSGFX_CACHE_DIR</c> when set.</summary>
    public static string DefaultRoot =>
        Environment.GetEnvironmentVariable("APEX_TOOLSGFX_CACHE_DIR") is { Length: > 0 } dir
            ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Apex", "ToolsGfxCache");

    public static ApexToolsGfxCache Default { get; } = new(DefaultRoot);

    public string Root { get; }
    public string XMeshCacheDir { get; }
    public string XBinCacheDir { get; }
    public string ImageCacheDir { get; }
    public string XAnimCacheDir { get; }

    /// <summary>
    /// Returns <paramref name="path"/> if it exists (unless <paramref name="reconvert"/>), else runs <paramref name="convert"/> (once per path even when
    /// several threads ask) and writes its payload there atomically. Reports to <see cref="ConversionScope"/>.
    /// Exceptions from <paramref name="convert"/> propagate (and nothing is written).
    /// </summary>
    /// <param name="reconvert">Convert even when the file exists (overwriting it).</param>
    public string GetOrConvert(string path, string label, Func<byte[]> convert, out CacheOrigin origin, bool reconvert = false) =>
        GetOrConvert(path, label, convert, out origin, out _, reconvert);

    /// <summary>
    /// <see cref="GetOrConvert(string, string, Func{byte[]}, out CacheOrigin, bool)"/> that also hands back the payload
    /// when it was converted by this call or one it waited for (the bytes just written to <paramref name="path"/>), so
    /// the caller can parse it instead of reading the file back; null when the file was already there.
    /// </summary>
    public string GetOrConvert(string path, string label, Func<byte[]> convert, out CacheOrigin origin, out byte[]? payload, bool reconvert = false)
    {
        payload = null;
        if (!reconvert && File.Exists(path))
        {
            origin = CacheOrigin.ApexCache;
            ConversionScope.Report(new ConversionEvent(label, path, CacheOrigin.ApexCache, 0, null));
            return path;
        }
        bool ran = false;
        var lazy = _inFlight.GetOrAdd(path, p => new Lazy<byte[]>(() =>
        {
            ran = true;
            var sw = Stopwatch.StartNew();
            try
            {
                var converted = convert();
                Lz4Container.WriteFile(p, converted);
                ConversionScope.Report(new ConversionEvent(label, p, CacheOrigin.ApexConverted, sw.Elapsed.TotalMilliseconds, null));
                return converted;
            }
            catch (Exception ex)
            {
                ConversionScope.Report(new ConversionEvent(label, p, CacheOrigin.ApexConverted, sw.Elapsed.TotalMilliseconds, ex.Message));
                throw;
            }
        }, LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            payload = lazy.Value;
            origin = ran ? CacheOrigin.ApexConverted : CacheOrigin.ApexCache;
            return path;
        }
        finally
        {
            _inFlight.TryRemove(new KeyValuePair<string, Lazy<byte[]>>(path, lazy));
        }
    }
}

/// <summary>One cache resolution done by Apex (a hit in its cache, a conversion, or a failed conversion).</summary>
/// <param name="Asset">What was resolved (e.g. the xmodel_bin file name).</param>
/// <param name="Path">The Apex cache file.</param>
/// <param name="Origin"><see cref="CacheOrigin.ApexCache"/> or <see cref="CacheOrigin.ApexConverted"/>.</param>
/// <param name="Milliseconds">Conversion time (0 for cache hits).</param>
/// <param name="Error">Why the conversion failed, or null.</param>
public sealed record ConversionEvent(string Asset, string Path, CacheOrigin Origin, double Milliseconds, string? Error);

/// <summary>
/// Collects the <see cref="ConversionEvent"/>s raised on the current async flow, so a caller can tell whether what it
/// just loaded came from Apex's converter: <c>using var scope = ConversionScope.Begin(); …load…; scope.Events</c>.
/// </summary>
public sealed class ConversionScope : IDisposable
{
    private static readonly AsyncLocal<ConversionScope?> CurrentScope = new();
    private readonly ConversionScope? _parent;
    private readonly List<ConversionEvent> _events = new();

    private ConversionScope(ConversionScope? parent) => _parent = parent;

    public static ConversionScope Begin()
    {
        var s = new ConversionScope(CurrentScope.Value);
        CurrentScope.Value = s;
        return s;
    }

    /// <summary>Events recorded so far (thread-safe snapshot).</summary>
    public IReadOnlyList<ConversionEvent> Events
    {
        get { lock (_events) return _events.ToArray(); }
    }

    /// <summary>True when anything loaded in this scope came from Apex's converter or cache.</summary>
    public bool AnyFromApex => Events.Any(e => e.Error is null);

    /// <summary>A short summary for a status line, e.g. "converted by Apex (3 files, 120 ms)"; null when nothing came from Apex.</summary>
    public string? Summary()
    {
        var ev = Events.Where(e => e.Error is null).ToArray();
        if (ev.Length == 0)
            return null;
        var converted = ev.Where(e => e.Origin == CacheOrigin.ApexConverted).ToArray();
        if (converted.Length > 0)
            return $"converted by Apex ({converted.Length} file{(converted.Length == 1 ? "" : "s")}, {converted.Sum(e => e.Milliseconds):F0} ms)";
        return $"converted by Apex (cached, {ev.Length} file{(ev.Length == 1 ? "" : "s")})";
    }

    internal static void Report(ConversionEvent e)
    {
        for (var s = CurrentScope.Value; s is not null; s = s._parent)
            lock (s._events)
                s._events.Add(e);
    }

    public void Dispose()
    {
        if (CurrentScope.Value == this)
            CurrentScope.Value = _parent;
    }
}
