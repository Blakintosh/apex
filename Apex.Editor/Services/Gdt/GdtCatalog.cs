using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace Apex.Editor.Services.Gdt;

/// <summary>A discovered .gdt file: its absolute path plus a root-relative display name.</summary>
public readonly record struct GdtSource(string FullPath, string RelativeName);

/// <summary>
/// Enumerates the .gdt files under a <see cref="GameEnvironment"/> in load priority order:
/// source_data root files, then source_data subdirectories, then xanim_export, then the
/// (lowest-priority) model_export per-model sidecars. Names are relative to the BO3 root with
/// forward slashes so browser grouping is unambiguous (e.g. <c>source_data/blak_iw9/foo.gdt</c>).
/// </summary>
public static class GdtCatalog
{
    public static IReadOnlyList<GdtSource> Enumerate(GameEnvironment env)
    {
        if (!env.IsAvailable || env.Bo3Root is null)
            return Array.Empty<GdtSource>();

        var root = env.Bo3Root;

        // Each root's directory tree is walked ONCE, parallelizing the deep walk across the root's
        // immediate subdirectories (the tree walk is I/O-latency bound, so fanning subdirectories out
        // across cores hides most of it). source_data buckets root-level vs subdirectory files during
        // that single walk; xanim_export/model_export are flat buckets. The three roots are walked
        // concurrently. Ordering is unchanged: source_data root files (by name), source_data subdir
        // files (by full path), xanim_export (by full path), model_export (by full path).
        var srcRoot = new List<string>();
        var srcSub = new List<string>();
        List<string> xanim = new();
        List<string> model = new();

        Parallel.Invoke(
            () => WalkBucketed(env.SourceDataDir, srcRoot, srcSub),
            () => xanim = WalkAll(env.XanimExportDir),
            () => model = WalkAll(env.ModelExportDir));

        srcRoot.Sort(static (a, b) => string.Compare(Path.GetFileName(a), Path.GetFileName(b), StringComparison.OrdinalIgnoreCase));
        srcSub.Sort(PathCompare);
        xanim.Sort(PathCompare);
        model.Sort(PathCompare);

        var result = new List<GdtSource>(srcRoot.Count + srcSub.Count + xanim.Count + model.Count);
        void AddAll(List<string> files)
        {
            foreach (var f in files)
                result.Add(new GdtSource(f, ToRelativeName(root, f)));
        }
        AddAll(srcRoot);
        AddAll(srcSub);
        AddAll(xanim);
        AddAll(model);

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
