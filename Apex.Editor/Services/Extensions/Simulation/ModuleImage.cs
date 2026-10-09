using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Apex.Editor.Services.Extensions.Simulation;

/// <summary>
/// What a module file would pull into Apex besides itself, read from its bytes before Windows sees them. Consent is
/// given for one file's hash, so a module may need nothing but Windows: every DLL it imports, delay-loaded ones too,
/// must be an API set (a name starting <c>api-</c> or <c>ext-</c>, which Windows resolves itself), a KnownDLL, or a file
/// of that name in System32 (the only folder the load searches, see <see cref="SimulatorNative.Load"/>); and it may
/// carry no side-by-side manifest naming files or assemblies, which Windows would look for beside it. A DLL a module
/// loads by itself at run time (LoadLibrary) can't be seen here: consent is the only gate for that.
/// </summary>
public static partial class ModuleImage
{
    private const ushort Pe32Plus = 0x20B;
    private const ushort Amd64 = 0x8664;
    private const int ImportDirectory = 1;
    private const int ResourceDirectory = 2;
    private const int DelayImportDirectory = 13;
    private const uint ManifestResource = 24;

    /// <summary>More than any real module has: a table this long is damage, not imports.</summary>
    private const int MaxEntries = 4096;

    /// <summary>
    /// Why the module can't be loaded as one self-contained file, or null when it can. A file that isn't a 64-bit
    /// Windows image at all is left to Windows, which refuses it with its own error.
    /// </summary>
    public static string? Problem(ReadOnlySpan<byte> image)
    {
        Contents? contents;
        try
        {
            contents = Read(image);
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or InvalidDataException)
        {
            return "isn't a well-formed Windows DLL";
        }
        if (contents is null)
            return null;
        if (contents.Manifest is { } manifest && NamesFiles().IsMatch(manifest))
            return "carries a side-by-side manifest that names other files; a module must be one self-contained DLL";
        foreach (var name in contents.Imports)
            if (!IsWindows(name))
                return $"needs {Printable(name)}, which isn't part of Windows; a module must be one self-contained DLL (link the C runtime statically, /MT)";
        return null;
    }

    /// <summary>The DLL names the image imports, plain and delay-loaded (null when it isn't a 64-bit image).</summary>
    public static IReadOnlyList<string>? Imports(ReadOnlySpan<byte> image) => Read(image)?.Imports;

    /// <summary>Whether Windows supplies <paramref name="name"/>: an API set, a KnownDLL or a file in System32.</summary>
    public static bool IsWindows(string name)
    {
        if (name.Length is 0 or > 255 || name.Trim('.').Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return false;
        if (name.StartsWith("api-", StringComparison.OrdinalIgnoreCase) || name.StartsWith("ext-", StringComparison.OrdinalIgnoreCase))
            return true;
        return KnownDlls.Contains(name) || File.Exists(Path.Combine(Environment.SystemDirectory, name));
    }

    private static readonly HashSet<string> KnownDlls = ReadKnownDlls();

    private static HashSet<string> ReadKnownDlls()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!OperatingSystem.IsWindows())
            return set;
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\KnownDLLs");
            if (key is not null)
                foreach (var value in key.GetValueNames())
                    if (key.GetValue(value) is string dll)
                        set.Add(dll);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // Unreadable: System32 alone decides, and every KnownDLL is there anyway.
        }
        return set;
    }

    /// <summary>A manifest element that makes Windows look for a file or an assembly: &lt;file&gt; or &lt;dependency&gt;.</summary>
    [GeneratedRegex(@"<\s*(?:[A-Za-z0-9_]+:)?(?:file|dependency)\b", RegexOptions.IgnoreCase)]
    private static partial Regex NamesFiles();

    private sealed record Contents(List<string> Imports, string? Manifest);

    private static Contents? Read(ReadOnlySpan<byte> image)
    {
        if (image.Length < 0x40 || image[0] != 'M' || image[1] != 'Z')
            return null;
        var pe = BinaryPrimitives.ReadInt32LittleEndian(image[0x3C..]);
        if (pe < 0 || pe > image.Length - 26 || !image.Slice(pe, 4).SequenceEqual("PE\0\0"u8))
            return null;
        var optional = pe + 24;
        if (U16(image, pe + 4) != Amd64 || U16(image, optional) != Pe32Plus)
            return null;
        var map = new Map(U16(image, pe + 6), optional + U16(image, pe + 20), (int)Math.Min(U32(image, optional + 108), 16u), optional);
        var imageBase = BinaryPrimitives.ReadUInt64LittleEndian(image[(optional + 24)..]);

        var imports = new List<string>();
        if (map.Directory(image, ImportDirectory) is var importRva and not 0)
            for (var at = map.Offset(image, importRva); ; at += 20)
            {
                var name = U32(image, at + 12);
                if (name == 0 && U32(image, at) == 0 && U32(image, at + 16) == 0)
                    break;
                Add(imports, Ascii(image, map.Offset(image, name)));
            }
        if (map.Directory(image, DelayImportDirectory) is var delayRva and not 0)
            for (var at = map.Offset(image, delayRva); ; at += 32)
            {
                var name = U32(image, at + 4);
                if (name == 0)
                    break;
                // An old-style descriptor (attribute bit 0 clear) holds addresses, not RVAs.
                var rva = (U32(image, at) & 1) != 0 ? name : (uint)(name - imageBase);
                Add(imports, Ascii(image, map.Offset(image, rva)));
            }

        string? manifest = null;
        if (map.Directory(image, ResourceDirectory) is var resourceRva and not 0)
        {
            var root = map.Offset(image, resourceRva);
            foreach (var (type, typeTarget, typeIsDirectory) in Children(image, root))
            {
                if (type != ManifestResource || !typeIsDirectory)
                    continue;
                // Every manifest (by id), in every language: whichever Windows picks must pass.
                var text = new StringBuilder();
                foreach (var (_, idTarget, idIsDirectory) in Children(image, root + typeTarget))
                {
                    if (!idIsDirectory)
                        continue;
                    foreach (var (_, data, dataIsDirectory) in Children(image, root + idTarget))
                    {
                        if (dataIsDirectory)
                            continue;
                        var at = map.Offset(image, U32(image, root + data));
                        var size = (int)Math.Min(U32(image, root + data + 4), (uint)(image.Length - at));
                        text.Append(Encoding.UTF8.GetString(image.Slice(at, size)));
                    }
                }
                manifest = text.ToString();
            }
        }
        return new Contents(imports, manifest);
    }

    private static void Add(List<string> imports, string name)
    {
        if (imports.Count >= MaxEntries)
            throw new InvalidDataException("too many imports");
        imports.Add(name);
    }

    /// <summary>The section table: RVAs to file offsets, and the data directories.</summary>
    private readonly record struct Map(int SectionCount, int Sections, int DirectoryCount, int Optional)
    {
        public uint Directory(ReadOnlySpan<byte> image, int index) =>
            index < DirectoryCount ? U32(image, Optional + 112 + index * 8) : 0;

        public int Offset(ReadOnlySpan<byte> image, uint rva)
        {
            for (var i = 0; i < SectionCount; i++)
            {
                var s = Sections + i * 40;
                var address = U32(image, s + 12);
                var rawSize = U32(image, s + 16);
                if (rva < address || rva - address >= rawSize)
                    continue;
                var offset = (long)U32(image, s + 20) + (rva - address);
                if (offset < image.Length)
                    return (int)offset;
            }
            throw new InvalidDataException($"RVA {rva:x} isn't in the file");
        }
    }

    private static List<(uint Id, int Target, bool IsDirectory)> Children(ReadOnlySpan<byte> image, int directory)
    {
        var count = U16(image, directory + 12) + U16(image, directory + 14);
        if (count > MaxEntries)
            throw new InvalidDataException("too many resources");
        var list = new List<(uint, int, bool)>(count);
        for (var i = 0; i < count; i++)
        {
            var e = directory + 16 + i * 8;
            var name = U32(image, e);
            var target = U32(image, e + 4);
            list.Add(((name & 0x8000_0000) != 0 ? uint.MaxValue : name, (int)(target & 0x7FFF_FFFF), (target & 0x8000_0000) != 0));
        }
        return list;
    }

    private static string Ascii(ReadOnlySpan<byte> image, int at)
    {
        var rest = image[at..];
        var end = rest.IndexOf((byte)0);
        if (end is < 0 or > 260)
            throw new InvalidDataException("an import name has no end");
        return Encoding.Latin1.GetString(rest[..end]);
    }

    private static string Printable(string name)
    {
        var chars = name.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
            if (chars[i] is < ' ' or > '~')
                chars[i] = '?';
        return new string(chars);
    }

    private static ushort U16(ReadOnlySpan<byte> image, int at) => BinaryPrimitives.ReadUInt16LittleEndian(image[at..]);

    private static uint U32(ReadOnlySpan<byte> image, int at) => BinaryPrimitives.ReadUInt32LittleEndian(image[at..]);
}
