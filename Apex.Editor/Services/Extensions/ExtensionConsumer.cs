using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Apex.Editor.Services.Extensions;

/// <summary>
/// Whether the tool that consumes an extension's values is installed (<c>consumer</c> in the manifest), as a probe of the
/// BO3 install: a file that carries a marker text when the tool is there. While it isn't, the manifest's <c>notice</c>
/// says so; once it is, the notice goes. Apex only reads the file; it knows nothing of the tool, and says nothing of
/// whether the tool is switched on for a map.
/// </summary>
public sealed record ExtensionConsumer(string File, string Contains);

public enum ConsumerState
{
    /// <summary>Nothing was learned: no install to look in, or a file the probe won't read.</summary>
    Unknown,

    /// <summary>The marker isn't there (no file, or a file without it).</summary>
    Missing,

    /// <summary>The marker is there.</summary>
    Present,
}

/// <summary>Evaluates an <see cref="ExtensionConsumer"/> against an install: read only, confined to it, capped, cached until the file changes.</summary>
public static class ExtensionConsumerProbe
{
    public const int MaxFileBytes = 16 * 1024 * 1024;
    public const int MaxTextLength = 200;

    private static readonly object Gate = new();
    private static readonly Dictionary<string, (long Length, long Ticks, ConsumerState State)> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The line the editor shows under the extension's header for this install: the manifest's notice, unless its consumer is installed.</summary>
    public static string? NoticeFor(ExtensionManifest manifest, string? bo3Root) =>
        manifest.Consumer is { } consumer && Evaluate(consumer, bo3Root) == ConsumerState.Present ? null : manifest.Notice;

    public static ConsumerState Evaluate(ExtensionConsumer consumer, string? bo3Root)
    {
        if (string.IsNullOrEmpty(bo3Root) || !Directory.Exists(bo3Root))
            return ConsumerState.Unknown;
        string root;
        try { root = Path.GetFullPath(bo3Root); }
        catch { return ConsumerState.Unknown; }
        if (Resolve(root, consumer.File) is not { } path)
            return ConsumerState.Unknown;

        var info = new FileInfo(path);
        if (!info.Exists)
            return ConsumerState.Missing;
        if (info.Length > MaxFileBytes || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            return ConsumerState.Unknown;

        var key = path + "|" + consumer.Contains;
        var ticks = info.LastWriteTimeUtc.Ticks;
        lock (Gate)
        {
            if (Cache.TryGetValue(key, out var hit) && hit.Length == info.Length && hit.Ticks == ticks)
                return hit.State;
        }
        ConsumerState state;
        try
        {
            state = System.IO.File.ReadAllBytes(path).AsSpan().IndexOf(Encoding.ASCII.GetBytes(consumer.Contains)) >= 0
                ? ConsumerState.Present
                : ConsumerState.Missing;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return ConsumerState.Unknown;
        }
        lock (Gate)
            Cache[key] = (info.Length, ticks, state);
        return state;
    }

    /// <summary>For tests: forgets what was read.</summary>
    public static void Reset()
    {
        lock (Gate)
            Cache.Clear();
    }

    /// <summary>The path inside <paramref name="root"/>; null when it would leave it (absolute, <c>..</c>, a drive, a stream, a trailing dot or space).</summary>
    public static string? Resolve(string root, string relative)
    {
        if (relative.Length == 0 || Path.IsPathRooted(relative) || relative.Contains(':') || relative.Any(char.IsControl))
            return null;
        var parts = relative.Split('/', (char)92);
        if (parts.Any(p => p.Length == 0 || p == "." || p == ".." || p.TrimEnd(' ', '.').Length != p.Length))
            return null;
        string full;
        try { full = Path.GetFullPath(Path.Combine(root, string.Join(Path.DirectorySeparatorChar, parts))); }
        catch { return null; }
        return full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? full : null;
    }
}
