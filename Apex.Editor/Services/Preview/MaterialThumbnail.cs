using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Apex.Editor.Models;

namespace Apex.Editor.Services.Preview;

/// <summary>
/// A material's swatch: its colour map's source image, small. The material's <c>colorMap</c> names an image asset, whose
/// <c>baseImage</c> is the file (both read through their parent chains, as a derived asset inherits them). A built-in
/// image (<c>$white</c>…) or a material without a colour map has none.
/// </summary>
public static class MaterialThumbnail
{
    /// <summary>Longest side of a swatch, in pixels (drawn at 32, sharp at 2x).</summary>
    public const int Size = 64;

    /// <summary>
    /// The source file of <paramref name="material"/>'s colour map, or null. Looks the assets up with
    /// <paramref name="resolve"/> (type, name), which reads the loaded index: call it on the UI thread.
    /// </summary>
    public static string? ColorMapRaw(Func<string, string, AssetRecord?> resolve, string material)
    {
        var image = Field(resolve, "material", material, "colorMap");
        if (image is null || image.StartsWith('$'))
            return null;
        return Field(resolve, "image", image, "baseImage");
    }

    /// <summary>Decodes the swatch for a <see cref="ColorMapRaw"/> value under the install at <paramref name="root"/>, off the UI thread.</summary>
    public static async Task<Bitmap?> LoadAsync(string? root, string raw, CancellationToken ct)
    {
        var path = await Task.Run(() => PreviewFileResolver.ResolveImage(root, raw), ct).ConfigureAwait(false);
        if (path is null)
            return null;
        return (await ImagePreviewLoader.LoadAsync(path, ImageChannelMode.Rgb, Size, ct).ConfigureAwait(false))?.Bitmap;
    }

    /// <summary>A field of an asset, or of the nearest ancestor that sets it; null when none does.</summary>
    private static string? Field(Func<string, string, AssetRecord?> resolve, string type, string name, string key)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var cur = resolve(type, name); cur is not null && seen.Add(cur.Name); cur = cur.Parent is { } p ? resolve(type, p) : null)
            if (cur.ScanProperties.TryGetValue(key, out var value) && value.Trim().Length > 0)
                return value.Trim();
        return null;
    }
}
