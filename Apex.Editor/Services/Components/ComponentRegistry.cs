using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Apex.Editor.Services.Stock;
using Apex.Render.Data.Hashing;

namespace Apex.Editor.Services.Components;

/// <summary>
/// The Mod Tools component registry: <c>%LOCALAPPDATA%\BO3ModTools\components\&lt;install hash&gt;\&lt;id&gt;.json</c>, one file
/// per component, written by the component itself (stocklib docs/status.md). The install hash is the one of
/// <c>stock.db</c>'s folder (<see cref="StockDb.InstallHash"/>). Apex only reads it. <c>BO3MODTOOLS_COMPONENTS_DIR</c>
/// replaces the <c>components</c> folder (checks); otherwise the <c>LOCALAPPDATA</c> variable is honoured before the
/// shell folder, so a process can be pointed at a fake registry.
/// </summary>
public static class ComponentRegistry
{
    /// <summary>The <c>components</c> folder (all installs).</summary>
    public static string Root
    {
        get
        {
            if (Environment.GetEnvironmentVariable("BO3MODTOOLS_COMPONENTS_DIR") is { Length: > 0 } dir)
                return dir;
            var local = Environment.GetEnvironmentVariable("LOCALAPPDATA") is { Length: > 0 } env
                ? env
                : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(local, "BO3ModTools", "components");
        }
    }

    /// <summary>The folder of one install's entries.</summary>
    public static string InstallFolder(string installPath) => Path.Combine(Root, StockDb.InstallHash(installPath));

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
        if (algo.Length == 0)
            return null;
        try
        {
            return new DragonAutoLod(exe, Str(entry, "version"), new AutoLodAlgo(algo));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static bool IsCurrent(JsonElement entry, string exe)
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
