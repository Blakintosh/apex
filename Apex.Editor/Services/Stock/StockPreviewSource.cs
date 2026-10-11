using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Apex.Editor.Services.Stock;

/// <summary>
/// What a preview of a stock asset needs besides the asset: the helper writes its caches on the first view
/// (<see cref="EnsureAsync"/>), and everything after reads them from the stock <c>toolsgfx</c> folder. One per preview.
/// </summary>
public sealed class StockPreviewSource(StockLibrary library, StockOrigin origin)
{
    public const string PreparingText = "Preparing preview (first view only)…";

    public StockOrigin Origin { get; } = origin;

    public string Kind => Origin.Kind;

    /// <summary>The name as a GDT writes it (a material without its techset folder).</summary>
    public string Name => StockTypes.BareName(Origin.EngineType, Origin.Name);

    /// <summary>The first view: the caches are not there yet, so the helper has to read the game's files.</summary>
    public bool NeedsHelper => !library.IsCached(Origin.EngineType, Origin.Name);

    /// <summary>
    /// Writes the caches when they are not there. Null when the preview can load; else one line saying why it can't
    /// (no helper, the helper failed, the asset has nothing to draw).
    /// </summary>
    public async Task<string?> EnsureAsync(CancellationToken cancel)
    {
        try
        {
            var result = await library.CacheAsync(Origin.EngineType, Origin.Name, cancel).ConfigureAwait(false);
            return library.IsCached(Origin.EngineType, Origin.Name) ? null : "The stock helper wrote no preview for this asset" + (result.Skipped.Count > 0 ? ": " + result.Skipped[0] : ".");
        }
        catch (StockHelperException ex)
        {
            return ex.Message;
        }
        catch (TimeoutException)
        {
            return "The stock helper isn't answering.";
        }
    }

    /// <summary>
    /// The asset's fields as the cache manifest has them (the same ones <c>describe</c> reads), so a preview does not wait for the
    /// helper to answer a field read; null until the asset is cached.
    /// </summary>
    public IReadOnlyDictionary<string, string>? ManifestFields()
    {
        if (library.CacheRoot is not { } root || library.ManifestPath(Origin.EngineType, Origin.Name) is not { } manifest || !File.Exists(manifest))
            return null;
        return new Apex.Render.Data.Assets.StockManifestLookup(Path.Combine(root, "manifest")).Find(Name, Kind)?.Fields;
    }

    /// <summary>The cache file of a stock image (read from its manifest), or null when it is not cached.</summary>
    public string? ImageFile(string imageName)
    {
        if (library.CacheRoot is not { } root)
            return null;
        var manifest = Path.Combine(root, "manifest", "image", Apex.Render.Data.Assets.StockManifestLookup.FileName(imageName) + ".json");
        try
        {
            if (!File.Exists(manifest))
                return null;
            using var doc = JsonDocument.Parse(File.ReadAllBytes(manifest));
            if (doc.RootElement.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array)
                foreach (var f in files.EnumerateArray())
                    if (f.GetString() is { Length: > 0 } rel && Path.Combine(root, rel) is var path && File.Exists(path))
                        return path;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
        }
        return null;
    }
}
