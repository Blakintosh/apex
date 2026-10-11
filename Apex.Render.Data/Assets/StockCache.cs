using System.Text.Json;
using Apex.Render.Data.Gdt;

namespace Apex.Render.Data.Assets;

/// <summary>
/// A read-only cache root of stock assets (Stock Library): ToolsGfx cache files that the stock helper (<c>stocklib cache</c>)
/// writes from data extracted out of shipped fastfiles, plus one manifest per asset naming it, for assets that have no
/// source files on disk. Laid out like <c>share\assetconvert\ToolsGfx</c> so the same folders can be merged into an
/// install for APE / Radiant (the contract is the helper's <c>docs/cache-contract.md</c>):
/// <code>
/// &lt;root&gt;\xmeshes\v39\&lt;xmodel_bin stem&gt;\pct100_&lt;hash&gt;.lz4
/// &lt;root&gt;\xbins\v1\&lt;xmodel_bin stem&gt;\&lt;hash&gt;.lz4
/// &lt;root&gt;\images\v20\&lt;settings&gt;\&lt;source file name&gt;_&lt;face&gt;_&lt;hash&gt;.lz4
/// &lt;root&gt;\manifest\&lt;xmodel|material|image&gt;\&lt;name&gt;.json   (GDT-shaped records; their source paths do not exist)
/// </code>
/// Lookups reach it only by name, after the install's caches and GDTs: an asset with real sources always wins.
/// </summary>
public sealed class StockCache
{
    public StockCache(string root)
    {
        Root = Path.GetFullPath(root);
        XMeshCacheDir = Path.Combine(Root, "xmeshes", $"v{ToolsGfxInstall.XMeshCacheVersion}");
        XBinCacheDir = Path.Combine(Root, "xbins", $"v{ToolsGfxInstall.XBinCacheVersion}");
        ImageCacheDir = Path.Combine(Root, "images", $"v{ToolsGfxInstall.ImageCacheVersion}");
        ManifestDir = Path.Combine(Root, "manifest");
        Manifests = new StockManifestLookup(ManifestDir);
    }

    /// <summary>The <c>toolsgfx</c> folder.</summary>
    public string Root { get; }
    public string XMeshCacheDir { get; }
    public string XBinCacheDir { get; }
    public string ImageCacheDir { get; }
    public string ManifestDir { get; }

    /// <summary>The manifests as a GDT lookup (the last layer behind the install's GDTs).</summary>
    public StockManifestLookup Manifests { get; }

    /// <summary><c>APEX_STOCK_ROOT</c> when set and present; null otherwise.</summary>
    public static StockCache? Locate()
    {
        var root = Environment.GetEnvironmentVariable("APEX_STOCK_ROOT");
        return !string.IsNullOrWhiteSpace(root) && Directory.Exists(root) ? new StockCache(root) : null;
    }

    /// <summary>The path of an asset's manifest (the file may not exist).</summary>
    public string ManifestPath(string type, string name) => Path.Combine(ManifestDir, type, StockManifestLookup.FileName(name) + ".json");
}

/// <summary>
/// <c>manifest\&lt;type&gt;\&lt;name&gt;.json</c> files as GDT entries: <c>{"type","name","gdf","fields":{...}}</c>. Looked up on
/// demand (the files appear while the app runs), parsed once per file version.
/// </summary>
public sealed class StockManifestLookup(string dir) : IGdtLookup
{
    private static readonly string[] Types = ["xmodel", "material", "image"];
    private readonly Dictionary<string, (long Ticks, GdtEntry? Entry)> _parsed = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The file name the helper gives a manifest: characters Windows file names can't hold become <c>_</c>.</summary>
    public static string FileName(string name)
    {
        var chars = name.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
            if (chars[i] is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*' || char.IsControl(chars[i]))
                chars[i] = '_';
        return new string(chars);
    }

    public GdtEntry? Find(string name)
    {
        foreach (var type in Types)
            if (Find(name, type) is { } entry)
                return entry;
        return null;
    }

    public GdtEntry? Find(string name, string type)
    {
        type = type.ToLowerInvariant();
        if (Array.IndexOf(Types, type) < 0 || name.Length == 0)
            return null;
        // A material is listed under its techset folder in the fastfiles ("mc/global_invisible"); its record is not.
        if (type == "material" && name.IndexOf('/') is > 0 and <= 3 and var slash)
            name = name[(slash + 1)..];
        var path = Path.Combine(dir, type, FileName(name) + ".json");
        long ticks;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
                return null;
            ticks = info.LastWriteTimeUtc.Ticks;
        }
        catch (IOException)
        {
            return null;
        }
        lock (_parsed)
            if (_parsed.TryGetValue(path, out var known) && known.Ticks == ticks)
                return known.Entry;
        var entry = Read(path, name);
        lock (_parsed)
            _parsed[path] = (ticks, entry);
        return entry;
    }

    private static GdtEntry? Read(string path, string name)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = doc.RootElement;
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            if (root.TryGetProperty("fields", out var f) && f.ValueKind == JsonValueKind.Object)
                foreach (var p in f.EnumerateObject())
                    fields[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.ToString();
            var gdf = root.TryGetProperty("gdf", out var g) ? g.GetString() : null;
            return new GdtEntry(root.TryGetProperty("name", out var n) ? n.GetString() ?? name : name, gdf, null, fields, path);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // A manifest is written atomically; one that can't be read is not there as far as a preview goes.
            return null;
        }
    }
}
