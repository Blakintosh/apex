using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Apex.Editor.Services.Gdt;

/// <summary>
/// Resolves the Black Ops 3 install root (read-only) and the GDT/deffile directories under it.
///
/// An install is a folder holding both <c>deffiles</c> and <c>source_data</c> (see <see cref="Check"/>). Resolution
/// order, first install wins:
///   1. <c>APEX_BO3_ROOT</c> environment variable (developer override),
///   2. the root the user chose with Locate…, remembered in the settings file,
///   3. <c>TA_TOOLS_PATH</c> environment variable (the modtools path — may point at the root or a
///      subdirectory such as <c>...\bin</c>, so we search it and its ancestors),
///   4. every Steam library (Steam's registry key, then its <c>libraryfolders.vdf</c>).
///
/// <c>APEX_FORCE_MOCK=1</c> overrides everything and forces <see cref="IsAvailable"/> to false so
/// the app (and the Apex.Shots harness) fall back to deterministic mock data.
/// <c>APEX_SIMULATE_NO_INSTALL=1</c> (harness only) makes automatic detection (1, 3, 4) find nothing, so the
/// "not found" state can be checked on a machine that has the game; a remembered root still counts.
/// </summary>
public sealed class GameEnvironment
{
    /// <summary>Steam's folder name for Black Ops III; the mod tools install into the same folder.</summary>
    private const string SteamFolderName = "Call of Duty Black Ops III";

    /// <summary>The resolved BO3 install root, or null when no candidate resolved.</summary>
    public string? Bo3Root { get; }

    public string? DeffilesDir { get; }

    /// <summary>Primary GDT root (recursive): <c>source_data</c>.</summary>
    public string? SourceDataDir { get; }

    /// <summary>Per-model sidecar GDTs (recursive): <c>model_export</c>.</summary>
    public string? ModelExportDir { get; }

    /// <summary>Animation GDTs (recursive): <c>xanim_export</c>.</summary>
    public string? XanimExportDir { get; }

    /// <summary>True when the root, deffiles, and source_data all exist and mock mode is not forced.</summary>
    public bool IsAvailable { get; }

    /// <summary>The harness (or a developer) asked for generated sample data instead of an install.</summary>
    public static bool MockForced =>
        string.Equals(Environment.GetEnvironmentVariable("APEX_FORCE_MOCK"), "1", StringComparison.Ordinal);

    private static bool DetectionSuppressed =>
        string.Equals(Environment.GetEnvironmentVariable("APEX_SIMULATE_NO_INSTALL"), "1", StringComparison.Ordinal);

    /// <param name="rememberedRoot">The root the user chose last time (settings), tried before automatic detection.</param>
    public GameEnvironment(string? rememberedRoot = null)
    {
        if (MockForced)
            return;

        var root = ResolveRoot(rememberedRoot);
        if (root is null)
            return;

        Bo3Root = root;
        DeffilesDir = Path.Combine(root, "deffiles");
        SourceDataDir = Path.Combine(root, "source_data");
        ModelExportDir = Path.Combine(root, "model_export");
        XanimExportDir = Path.Combine(root, "xanim_export");
        IsAvailable = true;
    }

    /// <summary>The folders an install must hold, in the order a missing-folder message names them.</summary>
    private static readonly string[] Required = { "deffiles", "source_data" };

    /// <summary>
    /// Whether <paramref name="folder"/> is a usable install: it holds <c>deffiles</c> and <c>source_data</c>. A folder
    /// picked one or two levels inside an install (<c>bin</c>, <c>source_data\...</c>) resolves to the install itself.
    /// Returns the install root, or null with the folders <paramref name="folder"/> is missing.
    /// </summary>
    public static string? Check(string folder, out IReadOnlyList<string> missing)
    {
        missing = Required;
        if (Normalize(folder) is not { } dir)
            return null;

        // One probe for the whole walk: a folder on a drive that has gone away answers within the timeout, as unreachable.
        var task = Task.Run(() =>
        {
            var probe = dir;
            for (var i = 0; i < 3 && !string.IsNullOrEmpty(probe); i++)
            {
                if (IsInstallNow(probe))
                    return (Root: probe, Gaps: (IReadOnlyList<string>)Array.Empty<string>());
                probe = Path.GetDirectoryName(probe) ?? string.Empty;
            }
            var gaps = new List<string>();
            foreach (var name in Required)
                if (!Directory.Exists(Path.Combine(dir, name)))
                    gaps.Add(name);
            return (Root: (string?)null, Gaps: (IReadOnlyList<string>)gaps);
        });
        if (!task.Wait(ProbeTimeout))
        {
            missing = Array.Empty<string>(); // unreachable: nothing can be said about what it holds
            return null;
        }
        missing = task.Result.Gaps;
        return task.Result.Root;
    }

    /// <summary>
    /// A full path without its trailing separator, except on a drive or share root (<c>D:\</c> stays <c>D:\</c>, never
    /// the drive-relative <c>D:</c>). Null when <paramref name="path"/> isn't a path. No disk access.
    /// </summary>
    public static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim())); }
        catch { return null; }
    }

    /// <summary>How long one install probe may take before the folder counts as missing (a disconnected network drive
    /// otherwise holds startup for the whole SMB timeout).</summary>
    public static TimeSpan ProbeTimeout { get; set; } = TimeSpan.FromMilliseconds(1500);

    private static bool IsInstall(string dir)
    {
        var task = Task.Run(() => IsInstallNow(dir));
        return task.Wait(ProbeTimeout) && task.Result;
    }

    private static bool IsInstallNow(string dir)
    {
        try
        {
            foreach (var name in Required)
                if (!Directory.Exists(Path.Combine(dir, name)))
                    return false;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? ResolveRoot(string? rememberedRoot)
    {
        var detect = !DetectionSuppressed;

        if (detect && Normalize(Environment.GetEnvironmentVariable("APEX_BO3_ROOT")) is { } explicitRoot && IsInstall(explicitRoot))
            return explicitRoot;

        // A remembered root that has since gone (drive unplugged, folder moved) falls through to detection.
        if (Normalize(rememberedRoot) is { } remembered && IsInstall(remembered))
            return remembered;

        if (!detect)
            return null;

        if (ResolveFromToolsPath(Environment.GetEnvironmentVariable("TA_TOOLS_PATH")) is { } fromTools)
            return fromTools;

        foreach (var library in SteamLibraries())
        {
            var candidate = Path.Combine(library, "steamapps", "common", SteamFolderName);
            if (IsInstall(candidate))
                return candidate;
        }
        return null;
    }

    /// <summary>
    /// TA_TOOLS_PATH may point at the root or a subdirectory (e.g. <c>...\bin</c>). Accept the value
    /// itself, then walk up a few levels looking for a directory that is an install.
    /// </summary>
    private static string? ResolveFromToolsPath(string? toolsPath)
    {
        if (Normalize(toolsPath) is not { } dir)
            return null;

        for (var i = 0; i < 4 && !string.IsNullOrEmpty(dir); i++)
        {
            if (IsInstall(dir))
                return dir;
            dir = Path.GetDirectoryName(dir) ?? string.Empty;
        }
        return null;
    }

    // Today's file: "path" "D:\\Lib" inside a numbered block. Before mid-2021: "1" "D:\\Lib" directly under LibraryFolders.
    private static readonly Regex VdfPath = new("^\\s*\"(?:path|\\d+)\"[ \\t]+\"([^\"]+)\"", RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>The library folders a <c>libraryfolders.vdf</c> lists, in either format.</summary>
    public static IEnumerable<string> ParseLibraryFolders(string vdf)
    {
        foreach (Match m in VdfPath.Matches(vdf))
            if (Normalize(m.Groups[1].Value.Replace(@"\\", @"\")) is { } path && Path.IsPathFullyQualified(path))
                yield return path;
    }

    /// <summary>Steam's own folder, then every library its <c>libraryfolders.vdf</c> lists. Never throws.</summary>
    private static IEnumerable<string> SteamLibraries()
    {
        var steam = SteamInstallPath();
        if (steam is null)
            yield break;
        yield return steam;

        string? vdf = null;
        try
        {
            var file = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            if (File.Exists(file))
                vdf = File.ReadAllText(file);
        }
        catch
        {
            // An unreadable library list just means fewer places to look.
        }
        if (vdf is null)
            yield break;
        foreach (var library in ParseLibraryFolders(vdf))
            yield return library;
    }

    private static string? SteamInstallPath()
    {
        if (!OperatingSystem.IsWindows())
            return null;
        try
        {
            using var user = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (user?.GetValue("SteamPath") is string userPath && Directory.Exists(userPath))
                return Path.GetFullPath(userPath);
            using var machine = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam");
            if (machine?.GetValue("InstallPath") is string machinePath && Directory.Exists(machinePath))
                return Path.GetFullPath(machinePath);
        }
        catch
        {
            // No access to the registry: no Steam libraries to search.
        }
        return null;
    }
}
