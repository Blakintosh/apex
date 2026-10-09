using System.Runtime.InteropServices;

namespace Apex.Render.Data.Conversion.Images;

/// <summary>
/// Source image decoding exactly as APE does it (ImageProcessing.cpp):
/// <list type="bullet">
/// <item><c>.tif/.tiff/.png</c> (0x140405900): WIC. Frame 0; size limit 4096; pixel formats 32bppGrayFloat,
/// 128bppRGBAFloat, 128bppPRGBAFloat, 128bppRGBFloat and {E3FED78F-E8DB-4ACF-84C1-E97F6136B327} are converted to
/// 128bppRGBAFloat and copied as-is, everything else is converted to 64bppRGBA and scaled by <c>(float)u16 *
/// (1/65535f)</c>.</item>
/// <item><c>.exr</c> (0x140405EB0): OpenEXR <c>RgbaInputFile</c> over the data window, half -&gt; float,
/// every channel clamped to <c>max(v, 0)</c> (<c>v &lt;= 0 -&gt; 0</c>).</item>
/// </list>
/// Both give a 4-channel <see cref="FloatImage"/>. Anything else is "Unsupported image file format".
/// </summary>
public static unsafe class SourceImageLoader
{
    public const int MaxDimension = 4096;

    public static bool IsSupported(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".tif" or ".tiff" or ".png" or ".exr";
    }

    public static FloatImage Load(ToolsGfxInstall install, string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".tif" or ".tiff" or ".png" => LoadWic(path),
            ".exr" => LoadExr(install, path),
            _ => throw new NotSupportedException($"Unsupported image file format '{path}'"),
        };
    }

    // ------------------------------------------------------------------------------------ WIC

    private static readonly Guid ClsidWicImagingFactory1 = new("cacaf262-9370-4615-a13b-9f5539da4c0a");
    private static readonly Guid IidIWicImagingFactory = new("ec5ec8a9-c395-4314-9c77-54d7a935ff70");
    private static readonly Guid Fmt64bppRgba = new("6fddc324-4e03-4bfe-b185-3d77768dc916");
    private static readonly Guid Fmt128bppRgbaFloat = new("6fddc324-4e03-4bfe-b185-3d77768dc919");
    private static readonly Guid[] FloatSourceFormats =
    [
        new("6fddc324-4e03-4bfe-b185-3d77768dc911"), // 32bppGrayFloat
        new("e3fed78f-e8db-4acf-84c1-e97f6136b327"),
        new("6fddc324-4e03-4bfe-b185-3d77768dc919"), // 128bppRGBAFloat
        new("6fddc324-4e03-4bfe-b185-3d77768dc91a"), // 128bppPRGBAFloat
        new("6fddc324-4e03-4bfe-b185-3d77768dc91b"), // 128bppRGBFloat
    ];

    [DllImport("ole32.dll")] private static extern int CoInitializeEx(nint reserved, uint coInit);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
    [DllImport("ole32.dll")] private static extern int CoCreateInstance(in Guid clsid, nint outer, uint clsContext, in Guid iid, out nint ppv);
    [DllImport("windowscodecs.dll")] private static extern int WICConvertBitmapSource(in Guid dstFormat, nint source, out nint dst);

    private static nint Slot(nint obj, int index) => (*(nint**)obj)[index];
    private static void Release(nint obj)
    {
        if (obj != 0)
            ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(obj, 2))(obj);
    }

    private static void Check(int hr, string what, string path)
    {
        if (hr < 0)
            throw new InvalidDataException($"WIC {what} failed (0x{hr:X8}) for '{path}'.");
    }

    public static FloatImage LoadWic(string path)
    {
        int init = CoInitializeEx(0, 0 /* COINIT_MULTITHREADED */);
        bool uninit = init >= 0; // S_OK / S_FALSE; RPC_E_CHANGED_MODE on an STA thread is fine too
        nint factory = 0, decoder = 0, frame = 0, converted = 0;
        try
        {
            Check(CoCreateInstance(ClsidWicImagingFactory1, 0, 1 /* CLSCTX_INPROC_SERVER */, IidIWicImagingFactory, out factory), "CoCreateInstance", path);
            fixed (char* name = path)
            {
                // IWICImagingFactory::CreateDecoderFromFilename(name, NULL vendor, GENERIC_READ, CacheOnDemand, &decoder)
                nint dec;
                Check(((delegate* unmanaged[Stdcall]<nint, char*, Guid*, uint, int, nint*, int>)Slot(factory, 3))(factory, name, null, 0x80000000, 0, &dec), "CreateDecoderFromFilename", path);
                decoder = dec;
            }
            nint fr;
            Check(((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Slot(decoder, 13))(decoder, 0, &fr), "GetFrame", path); // IWICBitmapDecoder::GetFrame
            frame = fr;
            uint w, h;
            Check(((delegate* unmanaged[Stdcall]<nint, uint*, uint*, int>)Slot(frame, 3))(frame, &w, &h), "GetSize", path);
            if (w > MaxDimension || h > MaxDimension)
                throw new InvalidDataException($"Image size ({w},{h}) exceeds the limit of {MaxDimension}: '{path}'.");
            Guid src;
            Check(((delegate* unmanaged[Stdcall]<nint, Guid*, int>)Slot(frame, 4))(frame, &src), "GetPixelFormat", path);
            bool isFloat = Array.IndexOf(FloatSourceFormats, src) >= 0;
            Check(WICConvertBitmapSource(isFloat ? Fmt128bppRgbaFloat : Fmt64bppRgba, frame, out converted), "WICConvertBitmapSource", path);

            int bpp = isFloat ? 128 : 64;
            uint stride = (uint)(w * bpp) >> 3;
            uint size = h * stride;
            var img = FloatImage.Uninitialized((int)w, (int)h, 4);
            if (isFloat)
            {
                fixed (float* dst = img.Data)
                    Check(((delegate* unmanaged[Stdcall]<nint, void*, uint, uint, byte*, int>)Slot(converted, 7))(converted, null, stride, size, (byte*)dst), "CopyPixels", path);
            }
            else
            {
                var raw = GC.AllocateUninitializedArray<ushort>((int)(w * h * 4));
                fixed (ushort* dst = raw)
                    Check(((delegate* unmanaged[Stdcall]<nint, void*, uint, uint, byte*, int>)Slot(converted, 7))(converted, null, stride, size, (byte*)dst), "CopyPixels", path);
                const float Scale = 1.0f / 65535.0f; // 0x37800080 (0.000015259022)
                var d = img.Data;
                ImageProcessing.ForRanges(d.Length, (from, to) =>
                {
                    for (int i = from; i < to; i++)
                        d[i] = raw[i] * Scale;
                });
            }
            return img;
        }
        finally
        {
            Release(converted);
            Release(frame);
            Release(decoder);
            Release(factory);
            if (uninit)
                CoUninitialize();
        }
    }

    // ------------------------------------------------------------------------------------ OpenEXR

    public static FloatImage LoadExr(ToolsGfxInstall install, string path)
    {
        // Imf throws C++ exceptions on bad files; check the magic first so the common failure is a managed one.
        using (var fs = File.OpenRead(path))
        {
            Span<byte> magic = stackalloc byte[4];
            if (fs.Read(magic) != 4 || magic[0] != 0x76 || magic[1] != 0x2F || magic[2] != 0x31 || magic[3] != 0x01)
                throw new InvalidDataException($"Not an OpenEXR file: '{path}'.");
        }

        var halves = ReadExrHalves(install, path, out int w, out int h);
        var img = FloatImage.Uninitialized(w, h, 4);
        var d = img.Data;
        ImageProcessing.ForRanges(d.Length, (from, to) =>
        {
            for (int i = from; i < to; i++)
            {
                float v = (float)BitConverter.UInt16BitsToHalf(halves[i]);
                d[i] = v <= 0.0f ? 0.0f : v;
            }
        });
        return img;
    }

    // Imf_2_1 crashes when several threads read files at once (e.g. a cube's faces converted in parallel).
    private static readonly object ExrLock = new();

    /// <summary>The data window as RGBA halves.</summary>
    private static ushort[] ReadExrHalves(ToolsGfxInstall install, string path, out int w, out int h)
    {
        var exr = NativeLibs.GetExr(install);
        var nameBytes = System.Text.Encoding.Default.GetBytes(path + "\0");
        lock (ExrLock)
        {
            var obj = (byte*)NativeMemory.AllocZeroed(1024); // sizeof(RgbaInputFile) is 56 in APE's build
            bool constructed = false;
            try
            {
                fixed (byte* name = nameBytes)
                    exr.Ctor(obj, name, 0);
                constructed = true;
                int* dw = exr.DataWindow(obj);
                int minX = dw[0], minY = dw[1], maxX = dw[2], maxY = dw[3];
                w = maxX - minX + 1;
                h = maxY - minY + 1;
                if (w <= 0 || h <= 0)
                    throw new InvalidDataException($"Invalid EXR data window in '{path}'.");
                var halves = new ushort[(long)w * h * 4];
                fixed (ushort* buf = halves)
                {
                    byte* basePtr = (byte*)buf - 8L * minX - 8L * w * minY;
                    exr.SetFrameBuffer(obj, basePtr, 1, (nuint)w);
                    exr.ReadPixels(obj, minY, maxY);
                }
                return halves;
            }
            catch (SEHException e)
            {
                throw new InvalidDataException($"OpenEXR failed to read '{path}'.", e);
            }
            finally
            {
                if (constructed)
                    exr.Dtor(obj);
                NativeMemory.Free(obj);
            }
        }
    }
}
