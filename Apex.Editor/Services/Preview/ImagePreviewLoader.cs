using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Apex.Editor.Services.Preview;

/// <summary>How an image's channels are projected into the preview bitmap.</summary>
public enum ImageChannelMode
{
    Rgb,
    Rgba,
    R,
    G,
    B,
    A,

    /// <summary>Visualize a tangent-space normal map with its blue channel reconstructed from R/G.</summary>
    NormalZ
}

/// <summary>A decoded preview bitmap plus source metadata. <see cref="Bitmap"/> is a UI-ready WriteableBitmap owned by
/// the caller (dispose it when it is replaced).</summary>
public sealed record LoadedImage(Bitmap Bitmap, int SourceWidth, int SourceHeight, string SourcePath, bool HasAlpha);

/// <summary>
/// Decodes image files (via ImageSharp) into Avalonia bitmaps for preview, entirely off the UI thread.
/// Supports .png/.tif/.tiff/.tga through ImageSharp's built-in decoders; unsupported formats (e.g. .exr)
/// resolve to null. The decoded pixels are kept in a bounded LRU keyed by path+size+file-mtime, so tab switching and
/// channel-mode changes only re-run the cheap channel projection into a new bitmap.
/// </summary>
public static class ImagePreviewLoader
{
    /// <summary>Longest side of the image preview.</summary>
    public const int PreviewSize = 2048;

    /// <summary>Longest side of a material slot thumbnail.</summary>
    public const int ThumbnailSize = 256;

    private const long CacheBudgetBytes = 256L * 1024 * 1024;

    private static readonly object CacheLock = new();
    private static readonly Dictionary<string, LinkedListNode<Decoded>> Cache = new(StringComparer.Ordinal);
    private static readonly LinkedList<Decoded> Lru = new();
    private static long _cacheBytes;

    /// <summary>Decoded RGBA8 pixels at preview size; <see cref="Translucent"/> when any alpha is below 255.</summary>
    private sealed record Decoded(string Key, string Path, byte[] Rgba, int Width, int Height, int SourceWidth, int SourceHeight, bool Translucent);

    /// <summary>
    /// Decodes <paramref name="path"/> (longest side capped at <paramref name="maxDimension"/>) and projects it through
    /// <paramref name="mode"/>, returning a new UI-ready bitmap or null when the file is missing/undecodable. All
    /// decode/transform/bitmap construction runs on a thread-pool thread.
    /// </summary>
    public static Task<LoadedImage?> LoadAsync(string path, ImageChannelMode mode, int maxDimension, CancellationToken ct)
    {
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();

            long mtime;
            try
            {
                mtime = File.GetLastWriteTimeUtc(path).Ticks;
            }
            catch
            {
                return (LoadedImage?)null;
            }

            var key = string.Concat(path.ToLowerInvariant(), "|", maxDimension.ToString(), "|", mtime.ToString());
            var decoded = TryGetCached(key) ?? Decode(key, path, maxDimension, ct);
            if (decoded is null)
                return null;

            ct.ThrowIfCancellationRequested();
            bool hasAlpha = mode == ImageChannelMode.Rgba && decoded.Translucent;
            var bitmap = BuildBitmap(decoded, mode, hasAlpha);
            return new LoadedImage(bitmap, decoded.SourceWidth, decoded.SourceHeight, path, hasAlpha);
        }, ct);
    }

    /// <summary>Drops every cached decode of <paramref name="path"/> (all sizes/mtimes).</summary>
    public static void Invalidate(string path)
    {
        lock (CacheLock)
        {
            var node = Lru.First;
            while (node is not null)
            {
                var next = node.Next;
                if (string.Equals(node.Value.Path, path, StringComparison.OrdinalIgnoreCase))
                    RemoveNode(node);
                node = next;
            }
        }
    }

    /// <summary>Loads <paramref name="path"/> as RGBA8 with its longest side capped at <paramref name="maxDimension"/>
    /// (aspect kept, each side rounded); throws when the file cannot be decoded.</summary>
    internal static Image<Rgba32> LoadScaled(string path, int maxDimension, out int sourceWidth, out int sourceHeight)
    {
        var image = Image.Load<Rgba32>(path);
        sourceWidth = image.Width;
        sourceHeight = image.Height;
        int longest = Math.Max(image.Width, image.Height);
        if (longest > maxDimension)
        {
            double scale = (double)maxDimension / longest;
            int tw = Math.Max(1, (int)Math.Round(image.Width * scale));
            int th = Math.Max(1, (int)Math.Round(image.Height * scale));
            image.Mutate(x => x.Resize(tw, th));
        }
        return image;
    }

    private static Decoded? Decode(string key, string path, int maxDimension, CancellationToken ct)
    {
        Image<Rgba32> image;
        int sourceWidth, sourceHeight;
        try
        {
            image = LoadScaled(path, maxDimension, out sourceWidth, out sourceHeight);
        }
        catch
        {
            // Undecodable format (e.g. .exr) or corrupt/missing file — degrade gracefully.
            return null;
        }

        using (image)
        {
            ct.ThrowIfCancellationRequested();
            var rgba = new byte[image.Width * image.Height * 4];
            image.CopyPixelDataTo(rgba);
            bool translucent = false;
            for (int i = 3; i < rgba.Length && !translucent; i += 4)
                translucent = rgba[i] != 255;
            var decoded = new Decoded(key, path, rgba, image.Width, image.Height, sourceWidth, sourceHeight, translucent);
            Store(decoded);
            return decoded;
        }
    }

    /// <summary>
    /// Writes <paramref name="d"/> into a new BGRA bitmap per the channel mode, premultiplied where alpha is kept
    /// (<see cref="ImageChannelMode.Rgba"/> only; every other mode is opaque).
    /// </summary>
    private static WriteableBitmap BuildBitmap(Decoded d, ImageChannelMode mode, bool hasAlpha)
    {
        var bitmap = new WriteableBitmap(
            new PixelSize(d.Width, d.Height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            hasAlpha ? AlphaFormat.Premul : AlphaFormat.Opaque);

        using var fb = bitmap.Lock();
        int rowBytes = d.Width * 4;
        var dst = ArrayPool<byte>.Shared.Rent(rowBytes);
        try
        {
            for (int y = 0; y < d.Height; y++)
            {
                ProjectRow(new ReadOnlySpan<byte>(d.Rgba, y * rowBytes, rowBytes), dst.AsSpan(0, rowBytes), mode);
                Marshal.Copy(dst, 0, fb.Address + (nint)((long)y * fb.RowBytes), rowBytes);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(dst);
        }
        return bitmap;
    }

    private static void ProjectRow(ReadOnlySpan<byte> src, Span<byte> dst, ImageChannelMode mode)
    {
        switch (mode)
        {
            case ImageChannelMode.Rgba:
                RowRgba(src, dst);
                break;
            case ImageChannelMode.R:
                RowGrey(src, dst, 0);
                break;
            case ImageChannelMode.G:
                RowGrey(src, dst, 1);
                break;
            case ImageChannelMode.B:
                RowGrey(src, dst, 2);
                break;
            case ImageChannelMode.A:
                RowGrey(src, dst, 3);
                break;
            case ImageChannelMode.NormalZ:
                RowNormalZ(src, dst);
                break;
            default:
                RowRgb(src, dst);
                break;
        }
    }

    private static void RowRgb(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        for (int i = 0; i < src.Length; i += 4)
        {
            dst[i] = src[i + 2];
            dst[i + 1] = src[i + 1];
            dst[i + 2] = src[i];
            dst[i + 3] = 255;
        }
    }

    private static void RowRgba(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        for (int i = 0; i < src.Length; i += 4)
        {
            int r = src[i], g = src[i + 1], b = src[i + 2], a = src[i + 3];
            if (a != 255)
            {
                b = b * a / 255;
                g = g * a / 255;
                r = r * a / 255;
            }
            dst[i] = (byte)b;
            dst[i + 1] = (byte)g;
            dst[i + 2] = (byte)r;
            dst[i + 3] = (byte)a;
        }
    }

    private static void RowGrey(ReadOnlySpan<byte> src, Span<byte> dst, int channel)
    {
        for (int i = 0; i < src.Length; i += 4)
        {
            byte c = src[i + channel];
            dst[i] = c;
            dst[i + 1] = c;
            dst[i + 2] = c;
            dst[i + 3] = 255;
        }
    }

    private static void RowNormalZ(ReadOnlySpan<byte> src, Span<byte> dst)
    {
        var z = NormalZ.Value;
        for (int i = 0; i < src.Length; i += 4)
        {
            byte r = src[i], g = src[i + 1];
            dst[i] = z[(r << 8) | g];
            dst[i + 1] = g;
            dst[i + 2] = r;
            dst[i + 3] = 255;
        }
    }

    /// <summary>Reconstructed normal Z (as a byte) for every (R, G) pair: <c>sqrt(1 - x² - y²)</c> remapped to 0..255.</summary>
    private static readonly Lazy<byte[]> NormalZ = new(() =>
    {
        var table = new byte[256 * 256];
        for (int r = 0; r < 256; r++)
            for (int g = 0; g < 256; g++)
            {
                double nx = r / 255.0 * 2.0 - 1.0;
                double ny = g / 255.0 * 2.0 - 1.0;
                double nz2 = 1.0 - nx * nx - ny * ny;
                double nz = nz2 > 0 ? Math.Sqrt(nz2) : 0.0;
                table[(r << 8) | g] = (byte)Math.Clamp((int)Math.Round((nz * 0.5 + 0.5) * 255.0), 0, 255);
            }
        return table;
    });

    private static Decoded? TryGetCached(string key)
    {
        lock (CacheLock)
        {
            if (!Cache.TryGetValue(key, out var node))
                return null;
            Lru.Remove(node);
            Lru.AddLast(node);
            return node.Value;
        }
    }

    private static void Store(Decoded decoded)
    {
        lock (CacheLock)
        {
            if (Cache.ContainsKey(decoded.Key))
                return;
            Cache[decoded.Key] = Lru.AddLast(decoded);
            _cacheBytes += decoded.Rgba.LongLength;
            while (_cacheBytes > CacheBudgetBytes && Lru.First is { } oldest && Lru.Count > 1)
                RemoveNode(oldest);
        }
    }

    private static void RemoveNode(LinkedListNode<Decoded> node)
    {
        Lru.Remove(node);
        Cache.Remove(node.Value.Key);
        _cacheBytes -= node.Value.Rgba.LongLength;
    }
}
