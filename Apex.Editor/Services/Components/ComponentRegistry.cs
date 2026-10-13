using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Apex.Render.Data.Hashing;
using Microsoft.Win32.SafeHandles;

namespace Apex.Editor.Services.Components;

/// <summary>
/// How every BO3 Mod Tools product names an install: its canonical path and the hash of it that names its folders under
/// <c>%LOCALAPPDATA%\BO3ModTools\</c> (<c>stock\&lt;hash&gt;\stock.db</c>, <c>components\&lt;hash&gt;\</c>). Matches stocklib
/// (<c>src\db.rs</c>) and Dragon (<c>src\stock.rs</c>), and Blackbird (<c>Services\ComponentRegistry.cs</c>) except for a UNC
/// install, where Blackbird drops Rust's <c>UNC\</c> form.
/// </summary>
public static class InstallPath
{
    /// <summary><c>%LOCALAPPDATA%\BO3ModTools</c>; the <c>LOCALAPPDATA</c> variable wins over the shell folder, as it does
    /// for the Rust tools, so a process pointed at a temp LOCALAPPDATA reads and writes nothing real.</summary>
    public static string ModToolsData =>
        Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA") is { Length: > 0 } env
            ? env
            : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BO3ModTools");

    /// <summary>
    /// FNV-1a 64 (16 hex digits) of the install's canonical path (<see cref="Canonical"/>), lower-cased, forward slashes
    /// as backslashes, without a trailing backslash, so a junction, a <c>subst</c> drive or an 8.3 name reaches the same
    /// folders as the real path.
    /// </summary>
    public static string Hash(string installPath) => HashCanonical(Canonical(installPath));

    /// <summary>The hash of a path that is already canonical (no file system access).</summary>
    public static string HashCanonical(string canonical)
    {
        var text = canonical.ToLowerInvariant().Replace('/', '\\').TrimEnd('\\');
        var hash = 0xcbf29ce484222325UL;
        foreach (var b in Encoding.UTF8.GetBytes(text))
        {
            hash ^= b;
            hash *= 0x100000001b3UL;
        }
        return hash.ToString("x16");
    }

    /// <summary>
    /// The install path as stocklib and Dragon store and hash it (Rust <c>std::fs::canonicalize</c>): the final path Windows
    /// resolves the folder to (junctions, symbolic links and <c>subst</c> drives followed, 8.3 names expanded, the real
    /// case), without the <c>\\?\</c> prefix. A folder that can't be resolved (missing, no access) is used as given, made
    /// absolute, like Dragon and Blackbird do.
    /// </summary>
    public static string Canonical(string installPath)
    {
        string full;
        try
        {
            full = Path.GetFullPath(installPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            return installPath;
        }
        var resolved = FinalPath(full) ?? full;
        return resolved.StartsWith(@"\\?\", StringComparison.Ordinal) ? resolved[4..] : resolved;
    }

    private static string? FinalPath(string folder)
    {
        if (!OperatingSystem.IsWindows() || !Directory.Exists(folder))
            return null;
        using var handle = CreateFile(folder, 0, 7 /* share read, write, delete */, IntPtr.Zero, 3 /* OPEN_EXISTING */,
            0x02000000 /* FILE_FLAG_BACKUP_SEMANTICS: a folder */, IntPtr.Zero);
        if (handle.IsInvalid)
            return null;
        var text = new StringBuilder(1024);
        var length = GetFinalPathNameByHandle(handle, text, text.Capacity, 0 /* VOLUME_NAME_DOS */);
        return length == 0 || length >= text.Capacity ? null : text.ToString();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, int size, uint flags);
}

/// <summary>
/// The Mod Tools component registry: <c>%LOCALAPPDATA%\BO3ModTools\components\&lt;install hash&gt;\&lt;id&gt;.json</c>, one file
/// per component, written by the component itself (stocklib docs/status.md). Apex reads it directly, so a Dragon feature
/// never needs the Stock Library. <c>BO3MODTOOLS_COMPONENTS_DIR</c> replaces the <c>components</c> folder (checks).
/// </summary>
public static class ComponentRegistry
{
    /// <summary>The <c>components</c> folder (all installs).</summary>
    public static string Root =>
        Environment.GetEnvironmentVariable("BO3MODTOOLS_COMPONENTS_DIR") is { Length: > 0 } dir ? dir : Path.Combine(InstallPath.ModToolsData, "components");

    /// <summary>The folder of one install's entries.</summary>
    public static string InstallFolder(string installPath) => Path.Combine(Root, InstallPath.Hash(installPath));

    /// <summary>
    /// Every entry of the install, read as it stands: id, version, path, its capability names, and whether the entry
    /// still describes the file on disk (<c>path</c> exists, and <c>exe_size</c> / <c>exe_mtime</c> match when given).
    /// Unreadable entries are skipped. Empty for no install.
    /// </summary>
    public static IReadOnlyList<RegisteredComponent> List(string? installPath)
    {
        var list = new List<RegisteredComponent>();
        if (string.IsNullOrWhiteSpace(installPath))
            return list;
        string[] files;
        try
        {
            var folder = InstallFolder(installPath);
            files = Directory.Exists(folder) ? Directory.GetFiles(folder, "*.json") : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return list;
        }
        foreach (var file in files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                var e = doc.RootElement;
                if (e.ValueKind != JsonValueKind.Object)
                    continue;
                var id = Str(e, "id") is { Length: > 0 } named ? named : Path.GetFileNameWithoutExtension(file);
                var path = Str(e, "path");
                list.Add(new RegisteredComponent(id, Str(e, "version"), path, path.Length > 0 && DragonComponent.IsCurrent(e, path), Capabilities(e)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
            }
        }
        return list;
    }

    private static IReadOnlyList<string> Capabilities(JsonElement e)
    {
        if (!e.TryGetProperty("capabilities", out var caps))
            return [];
        if (caps.ValueKind == JsonValueKind.Object)
            return caps.EnumerateObject().Select(p => p.Name).ToList();
        if (caps.ValueKind != JsonValueKind.Array)
            return [];
        return caps.EnumerateArray()
            .Select(c => c.ValueKind == JsonValueKind.String ? c.GetString() ?? "" : Str(c, "id") is { Length: > 0 } id ? id : Str(c, "name"))
            .Where(c => c.Length > 0).ToList();
    }

    internal static string Str(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    /// <summary>The entry <c>&lt;id&gt;.json</c> of an install, parsed (a copy: the caller owns it); null when it is missing or unreadable.</summary>
    public static JsonDocument? Read(string installPath, string id)
    {
        var path = Path.Combine(InstallFolder(installPath), id + ".json");
        try
        {
            if (!File.Exists(path))
                return null;
            return JsonDocument.Parse(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}

/// <summary>One registry entry (<see cref="ComponentRegistry.List"/>).</summary>
/// <param name="Current">The entry still describes the file on disk.</param>
public sealed record RegisteredComponent(string Id, string Version, string Path, bool Current, IReadOnlyList<string> Capabilities);

/// <summary>Dragon's <c>auto-lod</c> capability: the decimator its auto LODs use.</summary>
public sealed record DragonAutoLod(string Exe, string Version, AutoLodAlgo Algo);

/// <summary>
/// Dragon (bb-linker), as its registry entry <c>dragon.json</c> describes it. Trusted only when its <c>path</c> exists and,
/// when the entry gives them, <c>exe_size</c> / <c>exe_mtime</c> (unix seconds) match the file: an entry left by a
/// Dragon that was replaced or removed since its last link is ignored, as if Dragon were not installed.
/// </summary>
public static class DragonComponent
{
    public const string Id = "dragon";
    public const string AutoLodCapability = "auto-lod";

    /// <summary>Dragon's auto LOD when the install has a Dragon that offers it; null otherwise (no Dragon, stale entry,
    /// no <c>auto-lod</c> capability, or one without an algorithm Apex can key caches by).</summary>
    public static DragonAutoLod? AutoLod(string? installPath)
    {
        if (string.IsNullOrWhiteSpace(installPath))
            return null;
        using var doc = ComponentRegistry.Read(installPath, Id);
        return doc is null ? null : ParseAutoLod(doc.RootElement);
    }

    /// <summary><see cref="AutoLod"/> on an entry already read (checks).</summary>
    public static DragonAutoLod? ParseAutoLod(JsonElement entry)
    {
        if (entry.ValueKind != JsonValueKind.Object)
            return null;
        var exe = Str(entry, "path");
        if (exe.Length == 0 || !IsCurrent(entry, exe))
            return null;
        if (FindAutoLod(entry) is not { } capability)
            return null;
        var algo = capability.ValueKind == JsonValueKind.Object ? Str(capability, "algo") : "";
        // "stock": Dragon decimates as the stock tools do, so APE's own caches already show its LODs
        if (algo.Length == 0 || algo == "stock")
            return null;
        var gate = capability.TryGetProperty("gate_faces", out var g) && g.TryGetInt32(out var faces) && faces > 0 ? faces : 0;
        try
        {
            return new DragonAutoLod(exe, Str(entry, "version"), new AutoLodAlgo(algo) { GateFaces = gate });
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    internal static bool IsCurrent(JsonElement entry, string exe)
    {
        FileInfo file;
        try
        {
            file = new FileInfo(exe);
            if (!file.Exists)
                return false;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
        if (entry.TryGetProperty("exe_size", out var size) && size.TryGetInt64(out var bytes) && bytes != file.Length)
            return false;
        if (entry.TryGetProperty("exe_mtime", out var mtime) && mtime.TryGetInt64(out var seconds)
            && seconds != new DateTimeOffset(file.LastWriteTimeUtc).ToUnixTimeSeconds())
            return false;
        return true;
    }

    /// <summary>
    /// The <c>auto-lod</c> capability's details, wherever the entry puts them: <c>capabilities</c> as a list of names with
    /// the details in a top-level <c>auto_lod</c> / <c>auto-lod</c> object, a list holding <c>{"id": "auto-lod", …}</c>,
    /// or an object keyed by capability. Null when Dragon does not list it.
    /// </summary>
    private static JsonElement? FindAutoLod(JsonElement entry)
    {
        if (!entry.TryGetProperty("capabilities", out var caps))
            return null;
        if (caps.ValueKind == JsonValueKind.Object)
            return caps.TryGetProperty(AutoLodCapability, out var v) ? v : null;
        if (caps.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var c in caps.EnumerateArray())
        {
            if (c.ValueKind == JsonValueKind.String && c.GetString() == AutoLodCapability)
            {
                foreach (var key in new[] { "auto_lod", AutoLodCapability })
                    if (entry.TryGetProperty(key, out var details) && details.ValueKind == JsonValueKind.Object)
                        return details;
                return null;
            }
            if (c.ValueKind == JsonValueKind.Object && (Str(c, "id") == AutoLodCapability || Str(c, "name") == AutoLodCapability))
                return c;
        }
        return null;
    }

    private static string Str(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
