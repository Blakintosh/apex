using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Apex.Editor.Services.Gdt;

/// <summary>A discovered .gdt file: its absolute path plus a root-relative display name.</summary>
public readonly record struct GdtSource(string FullPath, string RelativeName);

/// <summary>
/// Enumerates the .gdt files under a <see cref="GameEnvironment"/> in load priority order: source_data root files,
/// then source_data subdirectories, then the other GDT folders in the order the install lists them, then the
/// (lowest-priority) model_export per-model sidecars. The folders are the ones the GDT database (<c>gdtdb\gdt.db</c>)
/// indexes, read from the same file it reads (<see cref="DirListFile"/>), so Radiant, APE and Apex list the same
/// assets: stock GDTs such as <c>texture_assets/t7_concrete.gdt</c>, and map or mod GDTs under <c>usermaps</c> and
/// <c>mods</c>, live outside source_data. Names are relative to the BO3 root with forward slashes so browser grouping
/// is unambiguous (e.g. <c>source_data/blak_iw9/foo.gdt</c>).
/// </summary>
public static class GdtCatalog
{
    /// <summary>The install's list of GDT folders, one per line, relative to the install (gdtdb.exe reads it).</summary>
    public const string DirListFile = @"bin\converter_gdt_dirs_0.txt";

    /// <summary>The list the Mod Tools ship in <see cref="DirListFile"/>, used when the file is missing or unreadable.</summary>
    public static readonly IReadOnlyList<string> DefaultDirs =
    [
        "aitype", "archetypes", "model_export", "source_data", "temp_assets", "texture_assets", "xanim_export",
        "art_assets", "usermaps", "mods",
    ];

    /// <summary>
    /// The GDT folders of the install, full paths in load order: source_data, the others as <see cref="DirListFile"/>
    /// lists them, model_export last. source_data, xanim_export and model_export are always included. A folder may not
    /// exist. Never throws.
    /// </summary>
    public static IReadOnlyList<string> Roots(GameEnvironment env)
    {
        if (env.Bo3Root is not { } root)
            return Array.Empty<string>();

        IEnumerable<string> listed;
        try
        {
            var file = Path.Combine(root, DirListFile);
            listed = File.Exists(file) ? File.ReadAllLines(file) : DefaultDirs;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            listed = DefaultDirs;
        }

        var dirs = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? dir)
        {
            if (dir is not null && seen.Add(dir))
                dirs.Add(dir);
        }
        string? Resolve(string line)
        {
            var entry = line.Trim().Trim('"');
            if (entry.Length == 0 || entry.StartsWith('#') || entry.StartsWith(';') || entry.StartsWith("//", StringComparison.Ordinal))
                return null;
            try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(root, entry))); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
        }

        var sourceData = Resolve("source_data");
        var modelExport = Resolve("model_export");
        Add(sourceData);
        foreach (var line in listed)
            if (Resolve(line) is { } dir && !dir.Equals(modelExport, StringComparison.OrdinalIgnoreCase))
                Add(dir);
        Add(Resolve("xanim_export"));
        Add(modelExport);
        return dirs;
    }

    public static IReadOnlyList<GdtSource> Enumerate(GameEnvironment env)
    {
        if (!env.IsAvailable || env.Bo3Root is null)
            return Array.Empty<GdtSource>();

        var root = env.Bo3Root;
        var roots = Roots(env);

        // Each root's directory tree is walked ONCE, parallelizing the deep walk across the root's
        // immediate subdirectories (the tree walk is I/O-latency bound, so fanning subdirectories out
        // across cores hides most of it). source_data (always the first root) buckets root-level vs
        // subdirectory files during that single walk; the other roots are flat buckets. The roots are
        // walked concurrently. Ordering: source_data root files (by name), source_data subdir files
        // (by full path), then each other root in turn (by full path).
        var srcRoot = new List<string>();
        var srcSub = new List<string>();
        var others = new List<string>[roots.Count];

        Parallel.For(0, roots.Count, i =>
        {
            if (i == 0)
            {
                WalkBucketed(roots[0], srcRoot, srcSub);
                others[0] = new List<string>();
            }
            else
            {
                others[i] = WalkAll(roots[i]);
                others[i].Sort(PathCompare);
            }
        });

        srcRoot.Sort(static (a, b) => string.Compare(Path.GetFileName(a), Path.GetFileName(b), StringComparison.OrdinalIgnoreCase));
        srcSub.Sort(PathCompare);

        // A folder listed inside another one (art_assets and art_assets\t6_legacy) is loaded once, where it first comes.
        var result = new List<GdtSource>(srcRoot.Count + srcSub.Count + others.Sum(o => o.Count));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void AddAll(List<string> files)
        {
            foreach (var f in files)
                if (seen.Add(f))
                    result.Add(new GdtSource(f, ToRelativeName(root, f)));
        }
        AddAll(srcRoot);
        AddAll(srcSub);
        foreach (var files in others)
            AddAll(files);

        return result;
    }

    private static int PathCompare(string a, string b) =>
        string.Compare(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>Single walk of <paramref name="dir"/>, splitting root-level files from subdirectory files.</summary>
    private static void WalkBucketed(string? dir, List<string> rootFiles, List<string> subFiles)
    {
        if (dir is null || !Directory.Exists(dir))
            return;

        rootFiles.AddRange(SafeEnumerate(dir, SearchOption.TopDirectoryOnly));

        var subdirs = SafeGetDirectories(dir);
        var perDir = new List<string>[subdirs.Length];
        Parallel.For(0, subdirs.Length,
            i => perDir[i] = SafeEnumerate(subdirs[i], SearchOption.AllDirectories));
        foreach (var files in perDir)
            subFiles.AddRange(files);
    }

    /// <summary>Single walk of <paramref name="dir"/> returning every .gdt beneath it (flat bucket).</summary>
    private static List<string> WalkAll(string? dir)
    {
        var all = new List<string>();
        if (dir is null || !Directory.Exists(dir))
            return all;

        all.AddRange(SafeEnumerate(dir, SearchOption.TopDirectoryOnly));

        var subdirs = SafeGetDirectories(dir);
        var perDir = new List<string>[subdirs.Length];
        Parallel.For(0, subdirs.Length,
            i => perDir[i] = SafeEnumerate(subdirs[i], SearchOption.AllDirectories));
        foreach (var files in perDir)
            all.AddRange(files);
        return all;
    }

    /// <summary>
    /// Every .gdt (or <paramref name="pattern"/>) under <paramref name="dir"/> (recursively when asked), skipping
    /// inaccessible directories instead of failing the whole walk. Matching is what
    /// <c>Directory.EnumerateFiles(dir, "*.gdt", option)</c> does (Win32 wildcards, hidden/system
    /// files included; <c>*.gdt</c> doesn't match a <c>.gdtx</c>); only the error handling differs.
    /// </summary>
    internal static List<string> SafeEnumerate(string dir, SearchOption option, string pattern = "*.gdt")
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = option == SearchOption.AllDirectories,
            IgnoreInaccessible = true,
            MatchType = MatchType.Win32,
            AttributesToSkip = 0,
        };
        // Enumeration is lazy, so errors surface while iterating: collect inside the try and keep
        // whatever was found before a directory vanished mid-walk.
        var files = new List<string>();
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, pattern, options))
                files.Add(f);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return files;
    }

    private static string[] SafeGetDirectories(string dir)
    {
        try
        {
            return Directory.GetDirectories(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    private static string ToRelativeName(string root, string fullPath)
    {
        var rel = Path.GetRelativePath(root, fullPath);
        return rel.Replace('\\', '/');
    }
}
