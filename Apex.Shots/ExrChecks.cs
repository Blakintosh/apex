using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Avalonia.Media.Imaging;
using Apex.Editor.Services.Gdt;
using Apex.Editor.Services.Preview;

namespace Apex.Shots;

/// <summary>
/// OpenEXR sources preview like the other image formats: read by the install's bin\openexr64r.dll (as APE and the linker
/// read them), values shown as 0..1 with HDR values clipped, found by the resolver with or without the extension. Needs
/// the install's OpenEXR reader, so it runs against a scratch install holding a copy of it.
/// </summary>
public partial class Program
{
    private static void RunExrChecks(string outDir)
    {
        var readerDll = Path.Combine(InstallRoot, "bin", "openexr64r.dll");
        var zlibDll = Path.Combine(InstallRoot, "bin", "zlib64r.dll");
        if (!File.Exists(readerDll) || !File.Exists(zlibDll))
        {
            Console.WriteLine("exr: the install's bin\\openexr64r.dll isn't here — skipped");
            return;
        }

        var scratch = NewScratch("exr");
        var install = Path.Combine(scratch, "Call of Duty Black Ops III");
        Directory.CreateDirectory(Path.Combine(install, "deffiles"));
        Directory.CreateDirectory(Path.Combine(install, "source_data"));
        Directory.CreateDirectory(Path.Combine(install, "bin"));
        File.Copy(readerDll, Path.Combine(install, "bin", "openexr64r.dll"));
        File.Copy(zlibDll, Path.Combine(install, "bin", "zlib64r.dll"));
        var images = Path.Combine(install, "texture_assets", "apex_exr");
        Directory.CreateDirectory(images);

        // 4x1: mid grey with a quarter alpha, HDR white (clipped), a negative (0), NaN (0).
        float[] r = { 0.5f, 4.0f, -1.0f, float.NaN };
        float[] g = { 0.25f, 1.0f, 0.0f, 0.0f };
        float[] b = { 0.75f, 2.0f, 0.0f, 0.0f };
        float[] a = { 0.25f, 1.0f, 1.0f, 1.0f };
        var exr = Path.Combine(images, "apex_exr_hdr.exr");
        File.WriteAllBytes(exr, UncompressedExr(4, 1, r, g, b, a));
        var fake = Path.Combine(images, "apex_exr_png.exr");
        File.WriteAllBytes(fake, Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg=="));

        var saved = (Mock: Environment.GetEnvironmentVariable("APEX_FORCE_MOCK"), Root: Environment.GetEnvironmentVariable("APEX_BO3_ROOT"));
        Environment.SetEnvironmentVariable("APEX_FORCE_MOCK", null);
        Environment.SetEnvironmentVariable("APEX_BO3_ROOT", install);
        try
        {
            var env = new GameEnvironment(null);
            Check("exr: a baseImage naming the .exr resolves to it",
                PreviewFileResolver.ResolveImage(env, @"texture_assets\\apex_exr\\apex_exr_hdr.exr") == exr);
            Check("exr: a baseImage without the extension finds the .exr",
                PreviewFileResolver.ResolveImage(env, @"texture_assets\\apex_exr\\apex_exr_hdr") == exr);

            var full = ImagePreviewLoader.LoadAsync(exr, ImageChannelMode.Rgba, ImagePreviewLoader.PreviewSize, CancellationToken.None).Result;
            Check($"exr: the preview decodes it, with its size and alpha ({full?.SourceWidth}x{full?.SourceHeight}, alpha {full?.HasAlpha})",
                full is { SourceWidth: 4, SourceHeight: 1, HasAlpha: true });
            full?.Bitmap.Dispose();

            byte[] Channel(ImageChannelMode mode)
            {
                var img = ImagePreviewLoader.LoadAsync(exr, mode, ImagePreviewLoader.PreviewSize, CancellationToken.None).Result;
                if (img?.Bitmap is not WriteableBitmap wb)
                    return Array.Empty<byte>();
                using (img.Bitmap)
                using (var fb = wb.Lock())
                {
                    var row = new byte[4 * 4];
                    Marshal.Copy(fb.Address, row, 0, row.Length);
                    return new[] { row[2], row[6], row[10], row[14] }; // BGRA: the red byte of each pixel (grey modes repeat it)
                }
            }
            var red = Channel(ImageChannelMode.R);
            Check($"exr: red is shown as stored, 0..1 to 0..255, HDR clipped to white, negative and NaN black ({string.Join(", ", red)})",
                red.SequenceEqual(new byte[] { 128, 255, 0, 0 }));
            var green = Channel(ImageChannelMode.G);
            var blue = Channel(ImageChannelMode.B);
            var alpha = Channel(ImageChannelMode.A);
            Check($"exr: green, blue and alpha come from their own channels ({green.FirstOrDefault()}, {blue.FirstOrDefault()}, {alpha.FirstOrDefault()})",
                green.FirstOrDefault() == 64 && blue.FirstOrDefault() == 191 && alpha.FirstOrDefault() == 64);

            Check("exr: a PNG named .exr isn't an OpenEXR file and doesn't preview, as the converter rejects it",
                ImagePreviewLoader.LoadAsync(fake, ImageChannelMode.Rgba, ImagePreviewLoader.PreviewSize, CancellationToken.None).Result is null);

            // A real one from the install, if it has one: found by walking up to the install's own reader.
            var real = Directory.Exists(Path.Combine(InstallRoot, "model_export"))
                ? Directory.EnumerateFiles(Path.Combine(InstallRoot, "model_export"), "*.exr", SearchOption.AllDirectories)
                    .FirstOrDefault(f => new FileInfo(f).Length < 16 * 1024 * 1024)
                : null;
            if (real is not null)
            {
                var shown = ImagePreviewLoader.LoadAsync(real, ImageChannelMode.Rgb, ImagePreviewLoader.ThumbnailSize, CancellationToken.None).Result;
                Check($"exr: an install EXR previews as a thumbnail ({Path.GetFileName(real)}, {shown?.SourceWidth}x{shown?.SourceHeight}, shown {shown?.Bitmap.PixelSize})",
                    shown is not null && shown.SourceWidth > 0 && Math.Max(shown.Bitmap.PixelSize.Width, shown.Bitmap.PixelSize.Height) <= ImagePreviewLoader.ThumbnailSize);
                shown?.Bitmap.Dispose();
            }
        }
        catch (Exception ex)
        {
            Check($"exr: {ex}", false);
        }
        finally
        {
            Environment.SetEnvironmentVariable("APEX_FORCE_MOCK", saved.Mock);
            Environment.SetEnvironmentVariable("APEX_BO3_ROOT", saved.Root);
            try { Directory.Delete(scratch, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>A scanline OpenEXR file, half RGBA, no compression: the smallest file the format allows.</summary>
    private static byte[] UncompressedExr(int width, int height, float[] r, float[] g, float[] b, float[] a)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        void Attr(string name, string type, byte[] value)
        {
            w.Write(Encoding.ASCII.GetBytes(name + "\0" + type + "\0"));
            w.Write(value.Length);
            w.Write(value);
        }
        static byte[] Ints(params int[] v) => v.SelectMany(BitConverter.GetBytes).ToArray();
        static byte[] Floats(params float[] v) => v.SelectMany(BitConverter.GetBytes).ToArray();

        w.Write(new byte[] { 0x76, 0x2F, 0x31, 0x01 });
        w.Write(2); // version 2, single-part scanline
        var channels = new MemoryStream();
        foreach (var c in new[] { "A", "B", "G", "R" }) // sorted, as the format requires
        {
            channels.Write(Encoding.ASCII.GetBytes(c + "\0"));
            channels.Write(Ints(1)); // HALF
            channels.Write(new byte[4]); // pLinear, reserved
            channels.Write(Ints(1, 1)); // x/y sampling
        }
        channels.WriteByte(0);
        Attr("channels", "chlist", channels.ToArray());
        Attr("compression", "compression", new byte[] { 0 });
        Attr("dataWindow", "box2i", Ints(0, 0, width - 1, height - 1));
        Attr("displayWindow", "box2i", Ints(0, 0, width - 1, height - 1));
        Attr("lineOrder", "lineOrder", new byte[] { 0 });
        Attr("pixelAspectRatio", "float", Floats(1f));
        Attr("screenWindowCenter", "v2f", Floats(0f, 0f));
        Attr("screenWindowWidth", "float", Floats(1f));
        w.Write((byte)0);

        int lineBytes = width * 4 * 2;
        long first = ms.Position + 8L * height;
        for (int y = 0; y < height; y++)
            w.Write(first + (long)y * (8 + lineBytes));
        for (int y = 0; y < height; y++)
        {
            w.Write(y);
            w.Write(lineBytes);
            foreach (var plane in new[] { a, b, g, r })
                for (int x = 0; x < width; x++)
                    w.Write(BitConverter.HalfToUInt16Bits((Half)plane[y * width + x]));
        }
        w.Flush();
        return ms.ToArray();
    }
}
