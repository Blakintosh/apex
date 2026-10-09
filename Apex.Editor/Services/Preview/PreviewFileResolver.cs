using System;
using System.IO;
using Apex.Editor.Services.Gdt;

namespace Apex.Editor.Services.Preview;

/// <summary>
/// Resolves raw GDT path values (e.g. a material's <c>baseImage</c>) to an absolute file on disk.
///
/// Empirical shapes found in BO3 source_data: every value is game-root-relative and prefixed with
/// either <c>model_export\...</c> or <c>texture_assets\...</c>; separators appear as escaped
/// backslashes (<c>\\</c>) or forward slashes; the extension is always present (.png/.tiff/.tif,
/// occasionally upper-cased, a handful of unsupported .exr). No absolute/drive-letter values were seen.
/// Resolution therefore tries the value as-given under the game root first, then under
/// texture_assets / model_export, with case-insensitive extension probing as the final fallback.
/// Pure path logic + File.Exists probing; no caching. Blocking I/O: call off the UI thread.
/// </summary>
public static class PreviewFileResolver
{
    private static readonly string[] ImageExtensions = { ".tif", ".tiff", ".png", ".tga" };

    /// <summary>Resolves a raw image path value to an absolute file, or null when nothing exists.</summary>
    public static string? ResolveImage(GameEnvironment env, string rawValue)
    {
        var root = env.Bo3Root;
        if (string.IsNullOrEmpty(root))
            return null;

        var norm = Normalize(rawValue);
        if (norm.Length == 0)
            return null;

        // 1. As-given relative to the game root (the observed shape: value already carries its prefix).
        var hit = ResolveRelative(env, root, norm, ImageExtensions);
        if (hit is not null)
            return hit;

        // 2/3. Fallbacks for values missing the standard prefix.
        hit = ResolveRelative(env, Path.Combine(root, "texture_assets"), norm, ImageExtensions);
        if (hit is not null)
            return hit;

        return ResolveRelative(env, Path.Combine(root, "model_export"), norm, ImageExtensions);
    }

    /// <summary>
    /// Generic resolver used for image/model_export/xanim_export files: probes <paramref name="rawValue"/>
    /// under <paramref name="relativeRoot"/> as-given, then with each candidate extension appended and
    /// substituted, case-insensitively. Returns an absolute path or null. Absolute inputs are honoured
    /// directly. <paramref name="env"/> is accepted for API symmetry and future root-relative fallbacks.
    /// </summary>
    public static string? ResolveRelative(GameEnvironment env, string relativeRoot, string rawValue, string[] extensionCandidates)
    {
        _ = env;
        if (string.IsNullOrEmpty(relativeRoot))
            return null;

        var norm = Normalize(rawValue);
        if (norm.Length == 0)
            return null;

        // Honour absolute inputs on their own.
        if (Path.IsPathRooted(norm))
            return ProbePath(norm, extensionCandidates);

        var combined = Path.Combine(relativeRoot, norm);
        return ProbePath(combined, extensionCandidates);
    }

    /// <summary>Probes an absolute candidate as-given, then with appended/substituted extensions, then case-insensitively.</summary>
    private static string? ProbePath(string absolute, string[] extensionCandidates)
    {
        if (File.Exists(absolute))
            return Path.GetFullPath(absolute);

        var withoutExt = StripExtension(absolute);

        foreach (var ext in extensionCandidates)
        {
            var normalizedExt = ext.StartsWith('.') ? ext : "." + ext;

            // Substitute the candidate extension for the value's own extension.
            var substituted = withoutExt + normalizedExt;
            if (File.Exists(substituted))
                return Path.GetFullPath(substituted);

            // Append onto the full value (covers extensionless inputs).
            var appended = absolute + normalizedExt;
            if (File.Exists(appended))
                return Path.GetFullPath(appended);
        }

        // Case-insensitive directory scan fallback for case-sensitive filesystems. On Windows the probes above already
        // matched case-insensitively, and listing a folder like xanim_export per miss is expensive.
        return OperatingSystem.IsWindows() ? null : ScanDirectoryInsensitive(absolute, withoutExt, extensionCandidates);
    }

    private static string? ScanDirectoryInsensitive(string absolute, string withoutExt, string[] extensionCandidates)
    {
        var dir = Path.GetDirectoryName(absolute);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            return null;

        var targetAsGiven = Path.GetFileName(absolute);
        var stem = Path.GetFileName(withoutExt);

        foreach (var file in Directory.EnumerateFiles(dir))
        {
            var name = Path.GetFileName(file);

            if (string.Equals(name, targetAsGiven, StringComparison.OrdinalIgnoreCase))
                return Path.GetFullPath(file);

            var fileStem = Path.GetFileNameWithoutExtension(file);
            if (!string.Equals(fileStem, stem, StringComparison.OrdinalIgnoreCase))
                continue;

            var fileExt = Path.GetExtension(file);
            foreach (var ext in extensionCandidates)
            {
                var normalizedExt = ext.StartsWith('.') ? ext : "." + ext;
                if (string.Equals(fileExt, normalizedExt, StringComparison.OrdinalIgnoreCase))
                    return Path.GetFullPath(file);
            }
        }

        return null;
    }

    /// <summary>Trims quotes/whitespace, collapses escaped/forward separators to the platform separator.</summary>
    private static string Normalize(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        var s = raw.Trim().Trim('"');
        s = s.Replace("\\\\", "\\").Replace('/', '\\');

        if (Path.DirectorySeparatorChar != '\\')
            s = s.Replace('\\', Path.DirectorySeparatorChar);

        return s.Trim().TrimStart(Path.DirectorySeparatorChar);
    }

    private static string StripExtension(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Length == 0 ? path : path[..^ext.Length];
    }
}
