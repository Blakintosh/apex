using System.Runtime.InteropServices;

namespace Apex.Render.Data.Conversion.Images;

/// <summary>
/// The native pieces APE's image converter uses, loaded from the user's machine at runtime (never redistributed):
/// <list type="bullet">
/// <item><c>GAME\bin\ispc_texcomp64r.dll</c> — Intel ISPC Texture Compressor (BC1/BC3/BC6H/BC7).</item>
/// <item><c>GAME\bin\openexr64r.dll</c> — OpenEXR 2.1 <c>Imf::RgbaInputFile</c> (.exr sources).</item>
/// <item><c>msvcr110.dll</c> <c>powf</c> (via <see cref="CrtMath"/>) — the CRT APE is linked against (sRGB
/// conversions); falls back to <see cref="MathF.Pow"/> when the VC++ 2012 runtime is missing.</item>
/// </list>
/// </summary>
internal static unsafe class NativeLibs
{
    private static readonly object Lock = new();
    private static readonly Dictionary<string, Ispc> IspcByRoot = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Exr> ExrByRoot = new(StringComparer.OrdinalIgnoreCase);

    // ---------------------------------------------------------------- powf (MSVCR110, shared CrtMath)

    /// <summary>True when <c>powf</c> comes from msvcr110.dll (bit-exact with APE).</summary>
    public static bool HasCrtPowf { get; } = NativeLibrary.TryLoad("msvcr110.dll", out var h) && NativeLibrary.TryGetExport(h, "powf", out _);

    /// <summary>APE's <c>powf</c> (MSVCR110 import) via <see cref="CrtMath.PowF32"/>.</summary>
    public static float Powf(float x, float y) => CrtMath.PowF32(x, y);

    // ---------------------------------------------------------------- ISPC texcomp

    /// <summary><c>rgba_surface</c> of ispc_texcomp.h.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct RgbaSurface
    {
        public byte* Ptr;
        public int Width;
        public int Height;
        public int Stride;
    }

    public sealed class Ispc
    {
        public required delegate* unmanaged<RgbaSurface*, byte*, void> BC1;
        public required delegate* unmanaged<RgbaSurface*, byte*, void> BC3;
        public required delegate* unmanaged<RgbaSurface*, byte*, void*, void> BC6H;
        public required delegate* unmanaged<RgbaSurface*, byte*, void*, void> BC7;

        /// <summary><c>GetProfile_fast</c> / <c>GetProfile_alpha_fast</c> / <c>GetProfile_bc6h_fast</c> results
        /// (settings structs, padded buffers).</summary>
        public required byte[] Bc7Fast, Bc7AlphaFast, Bc6hFast;
    }

    public static Ispc GetIspc(ToolsGfxInstall install)
    {
        lock (Lock)
        {
            if (IspcByRoot.TryGetValue(install.Root, out var cached))
                return cached;
            var path = Path.Combine(install.Root, "bin", "ispc_texcomp64r.dll");
            if (!File.Exists(path))
                throw new FileNotFoundException("ispc_texcomp64r.dll not found in the BO3 install (bin).", path);
            var h = NativeLibrary.Load(path);
            nint E(string name) => NativeLibrary.GetExport(h, name);
            byte[] Profile(string name)
            {
                var buf = new byte[256];
                fixed (byte* p = buf)
                    ((delegate* unmanaged<byte*, void>)E(name))(p);
                return buf;
            }
            var ispc = new Ispc
            {
                BC1 = (delegate* unmanaged<RgbaSurface*, byte*, void>)E("CompressBlocksBC1"),
                BC3 = (delegate* unmanaged<RgbaSurface*, byte*, void>)E("CompressBlocksBC3"),
                BC6H = (delegate* unmanaged<RgbaSurface*, byte*, void*, void>)E("CompressBlocksBC6H"),
                BC7 = (delegate* unmanaged<RgbaSurface*, byte*, void*, void>)E("CompressBlocksBC7"),
                Bc7Fast = Profile("GetProfile_fast"),
                Bc7AlphaFast = Profile("GetProfile_alpha_fast"),
                Bc6hFast = Profile("GetProfile_bc6h_fast"),
            };
            IspcByRoot[install.Root] = ispc;
            return ispc;
        }
    }

    // ---------------------------------------------------------------- OpenEXR 2.1 (Imf_2_1::RgbaInputFile)

    public sealed class Exr
    {
        public required delegate* unmanaged<void*, byte*, int, void*> Ctor;
        public required delegate* unmanaged<void*, void> Dtor;
        public required delegate* unmanaged<void*, int*> DataWindow;
        public required delegate* unmanaged<void*, void*, nuint, nuint, void> SetFrameBuffer;
        public required delegate* unmanaged<void*, int, int, void> ReadPixels;
    }

    public static Exr GetExr(ToolsGfxInstall install)
    {
        lock (Lock)
        {
            if (ExrByRoot.TryGetValue(install.Root, out var cached))
                return cached;
            var path = Path.Combine(install.Root, "bin", "openexr64r.dll");
            if (!File.Exists(path))
                throw new FileNotFoundException("openexr64r.dll not found in the BO3 install (bin).", path);
            // openexr64r.dll imports zlib64r.dll from the same folder.
            var h = NativeLibrary.Load(path, typeof(NativeLibs).Assembly, DllImportSearchPath.UseDllDirectoryForDependencies);
            nint E(string name) => NativeLibrary.GetExport(h, name);
            var exr = new Exr
            {
                Ctor = (delegate* unmanaged<void*, byte*, int, void*>)E("??0RgbaInputFile@Imf_2_1@@QEAA@QEBDH@Z"),
                Dtor = (delegate* unmanaged<void*, void>)E("??1RgbaInputFile@Imf_2_1@@UEAA@XZ"),
                DataWindow = (delegate* unmanaged<void*, int*>)E("?dataWindow@RgbaInputFile@Imf_2_1@@QEBAAEBV?$Box@V?$Vec2@H@Imath_2_1@@@Imath_2_1@@XZ"),
                SetFrameBuffer = (delegate* unmanaged<void*, void*, nuint, nuint, void>)E("?setFrameBuffer@RgbaInputFile@Imf_2_1@@QEAAXPEAURgba@2@_K1@Z"),
                ReadPixels = (delegate* unmanaged<void*, int, int, void>)E("?readPixels@RgbaInputFile@Imf_2_1@@QEAAXHH@Z"),
            };
            ExrByRoot[install.Root] = exr;
            return exr;
        }
    }
}
