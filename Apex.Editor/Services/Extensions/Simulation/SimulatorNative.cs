using System;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Apex.Editor.Services.Extensions.Simulation;

/// <summary>
/// docs/plugin-abi/apex_sim.h, version 1, as C# sees it. The layouts are the header's byte for byte (the test fixture,
/// compiled from the header, refuses any struct whose size field isn't its own sizeof).
/// </summary>
internal static unsafe class SimulatorNative
{
    public const uint AbiVersion = 1;

    public const int IdCapacity = 64;
    public const int VersionCapacity = 32;
    public const int NoteCapacity = 160;
    public const int ErrorCapacity = 256;

    /// <summary>Bytes after every buffer Apex hands a module, filled with <see cref="CanaryByte"/>: a module that writes
    /// past the size it was given changes them, and Apex turns it off before anything reads the damage.</summary>
    public const int CanaryBytes = 32;
    public const byte CanaryByte = 0xA5;

    [StructLayout(LayoutKind.Sequential)]
    public struct Kv
    {
        public byte* Key;
        public byte* Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Info
    {
        public uint Size;
        public uint Abi;
        public fixed byte Id[IdCapacity];
        public fixed byte Version[VersionCapacity];
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Input
    {
        public uint Size;
        public float Dt;
        public float Ads;
        public uint Shots;
        public uint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Output
    {
        public uint Size;
        public uint Supported;
        public fixed float ViewAngles[3];
        public fixed float ViewOrigin[3];
        public fixed float GunAngles[3];
        public fixed float GunOrigin[3];
        public fixed byte Note[NoteCapacity];
    }

    /// <summary>The module's six exports. Plain function pointers: x64 Windows has one calling convention, and nothing
    /// here allocates a delegate per call.</summary>
    public struct Exports
    {
        public delegate* unmanaged[Cdecl]<uint> AbiVersion;
        public delegate* unmanaged[Cdecl]<Info*, int> Info;
        public delegate* unmanaged[Cdecl]<Kv*, uint, byte*, uint, void*> Create;
        public delegate* unmanaged[Cdecl]<void*, void> Reset;
        public delegate* unmanaged[Cdecl]<void*, Input*, Output*, int> Step;
        public delegate* unmanaged[Cdecl]<void*, void> Destroy;
    }

    public static readonly string[] ExportNames =
        ["apex_sim_abi_version", "apex_sim_info", "apex_sim_create", "apex_sim_reset", "apex_sim_step", "apex_sim_destroy"];

    // System32 only: never the module's folder, PATH, the current directory or Apex's folder. A module may import only
    // what Windows supplies (ModuleImage checks before this), so a DLL dropped beside it, even one named like a System32
    // file, is never what loads.
    private const uint LoadLibrarySearchSystem32 = 0x00000800;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern nint LoadLibraryExW(string path, nint file, uint flags);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeLibrary(nint module);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeHandle file, char* buffer, uint length, uint flags);

    /// <summary>
    /// A handle on a folder that keeps it from being renamed or deleted (no FILE_SHARE_DELETE) while held; null when it
    /// can't be opened.
    /// </summary>
    public static SafeFileHandle? HoldFolder(string path)
    {
        const uint readAttributes = 0x80, shareReadWrite = 0x1 | 0x2, openExisting = 3, backupSemantics = 0x0200_0000;
        var h = CreateFileW(path, readAttributes, shareReadWrite, 0, openExisting, backupSemantics, 0);
        if (!h.IsInvalid)
            return h;
        h.Dispose();
        return null;
    }

    /// <summary>
    /// Where an open file or folder really is, every junction and link resolved (no <c>\\?\</c> prefix); null when Windows
    /// can't say.
    /// </summary>
    public static string? FinalPath(SafeHandle handle)
    {
        Span<char> buffer = stackalloc char[520];
        uint length;
        fixed (char* p = buffer)
            length = GetFinalPathNameByHandleW(handle, p, (uint)buffer.Length, 0);
        if (length == 0)
            return null;
        if (length < buffer.Length)
            return Plain(new string(buffer[..(int)length]));
        var large = new char[length];
        fixed (char* p = large)
        {
            var written = GetFinalPathNameByHandleW(handle, p, (uint)large.Length, 0);
            return written is 0 || written >= large.Length ? null : Plain(new string(large, 0, (int)written));
        }

        static string Plain(string path) =>
            path.StartsWith(@"\\?\UNC\", StringComparison.Ordinal) ? @"\\" + path[8..]
            : path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..]
            : path;
    }

    /// <summary>Loads <paramref name="fullPath"/> by that path alone; on failure, the Win32 error.</summary>
    public static LibraryHandle? Load(string fullPath, out int error)
    {
        var h = LoadLibraryExW(fullPath, 0, LoadLibrarySearchSystem32);
        error = h == 0 ? Marshal.GetLastPInvokeError() : 0;
        return h == 0 ? null : new LibraryHandle(h);
    }

    /// <summary>A loaded module; freed once, when the host lets go of it (app exit), never by the finalizer while it may run.</summary>
    public sealed class LibraryHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public LibraryHandle(nint handle) : base(ownsHandle: true) => SetHandle(handle);

        protected override bool ReleaseHandle() => FreeLibrary(handle);
    }

    // The CRT's view of the SSE control register (MXCSR on x64): exception masks, rounding, flush-to-zero and
    // denormals-are-zero. The x87 control word can't be read from here on x64; the header asks modules to keep both.
    [DllImport("ucrtbase.dll", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    [SuppressGCTransition]
    private static extern uint _control87(uint value, uint mask);

    private const uint FloatingPointBits = 0x0008_001F /* _MCW_EM */ | 0x0000_0300 /* _MCW_RC */ | 0x0300_0000 /* _MCW_DN */;

    /// <summary>The floating-point control state before a call into a module.</summary>
    public static uint FloatingPoint() => _control87(0, 0) & FloatingPointBits;

    /// <summary>
    /// True when a call changed the floating-point control state, which is then put back: Apex's own maths, the
    /// renderer's included, must never run with a module's rounding or flush-to-zero.
    /// </summary>
    public static bool FloatingPointChanged(uint before)
    {
        if (FloatingPoint() == before)
            return false;
        _control87(before, FloatingPointBits);
        return true;
    }

    /// <summary>A zeroed native buffer of <paramref name="size"/> bytes followed by the canary.</summary>
    public static byte* Alloc(int size)
    {
        var p = (byte*)NativeMemory.AllocZeroed((nuint)(size + CanaryBytes));
        new Span<byte>(p + size, CanaryBytes).Fill(CanaryByte);
        return p;
    }

    public static bool CanaryIntact(byte* p, int size) =>
        !new ReadOnlySpan<byte>(p + size, CanaryBytes).ContainsAnyExcept(CanaryByte);

    /// <summary>
    /// Text a module wrote into a fixed field: up to the first NUL (or the field's end), UTF-8, control characters as
    /// spaces, so a note can't break the line it's shown on.
    /// </summary>
    public static string Text(byte* p, int capacity)
    {
        var chars = RawText(p, capacity).ToCharArray();
        for (var i = 0; i < chars.Length; i++)
            if (char.IsControl(chars[i]))
                chars[i] = ' ';
        return new string(chars).Trim();
    }

    /// <summary>A fixed field exactly as the module wrote it, up to the first NUL: for comparing (an id), never for showing.</summary>
    public static string RawText(byte* p, int capacity)
    {
        var span = new ReadOnlySpan<byte>(p, capacity);
        var end = span.IndexOf((byte)0);
        return Encoding.UTF8.GetString(end < 0 ? span : span[..end]);
    }

    public static string Describe(int error) => error switch
    {
        // ERROR_BAD_EXE_FORMAT, ERROR_EXE_MACHINE_TYPE_MISMATCH
        193 or 216 => "isn't a 64-bit Windows DLL (Apex is 64-bit)",
        // ERROR_MOD_NOT_FOUND for a file that exists: an API set it names isn't on this Windows
        126 => "needs a part of Windows this computer doesn't have",
        2 or 3 => "isn't there",
        // ERROR_DLL_INIT_FAILED
        1114 => "failed while starting up",
        5 or 32 => "couldn't be opened (another program may have it, or access was refused)",
        _ => $"couldn't be loaded by Windows (error {error})",
    };
}
