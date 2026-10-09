using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Apex.Render.Data.Conversion;

namespace Apex.Render.Data.Hashing;

/// <summary>
/// <see cref="CacheKeys.FileSignature"/> results keyed by (full path, last-write time, size), remembered across
/// sessions for files of at least <see cref="PersistMinBytes"/> (APE keeps <c>filesignature.db</c> for the same
/// reason: re-hashing a big source image on every start costs more than the lookup). Stored in
/// <c>filesignatures.tsv</c> under <see cref="ApexToolsGfxCache.DefaultRoot"/>, one appended line per signature:
/// <c>hash \t ticks \t size \t path \t check</c>. A line that does not parse or whose check does not match (a torn write
/// from another process, a damaged file) is ignored; the last valid line for a path wins. Best effort: when the file
/// cannot be read or written the signatures are simply recomputed.
/// </summary>
internal static class SignatureStore
{
    public const long PersistMinBytes = 1 << 20;

    private static readonly Lazy<ConcurrentDictionary<string, Entry>> Memo = new(Load);
    private static readonly object WriteLock = new();

    private readonly record struct Entry(long Ticks, long Size, CacheHash Hash);

    private static string FilePath => Path.Combine(ApexToolsGfxCache.DefaultRoot, "filesignatures.tsv");

    public static bool TryGet(string fullPath, long ticks, long size, out CacheHash hash)
    {
        if (Memo.Value.TryGetValue(fullPath, out var e) && e.Ticks == ticks && e.Size == size)
        {
            hash = e.Hash;
            return true;
        }
        hash = default;
        return false;
    }

    public static void Add(string fullPath, long ticks, long size, CacheHash hash)
    {
        Memo.Value[fullPath] = new Entry(ticks, size, hash);
        if (size < PersistMinBytes)
            return;
        var line = Line(fullPath, new Entry(ticks, size, hash));
        lock (WriteLock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                // One write per line in append mode, so concurrent writers interleave whole lines.
                using var fs = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                fs.Write(Encoding.UTF8.GetBytes(line));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static ConcurrentDictionary<string, Entry> Load()
    {
        var map = new ConcurrentDictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        string[] lines;
        try
        {
            if (!File.Exists(FilePath))
                return map;
            lines = File.ReadAllLines(FilePath, Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return map;
        }
        foreach (var l in lines)
            if (TryParse(l, out var path, out var e))
                map[path] = e;
        // Every re-signed file leaves its old line behind; rewrite once the stale ones dominate.
        if (lines.Length > 1000 && lines.Length > 2 * map.Count)
            Compact(map);
        return map;
    }

    private static void Compact(ConcurrentDictionary<string, Entry> map)
    {
        var tmp = FilePath + "." + Environment.ProcessId + ".tmp";
        try
        {
            File.WriteAllText(tmp, string.Concat(map.Where(kv => kv.Value.Size >= PersistMinBytes).Select(kv => Line(kv.Key, kv.Value))), Encoding.UTF8);
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            File.Delete(tmp);
        }
    }

    private static string Line(string path, Entry e)
    {
        var body = $"{e.Hash.ToFileName()}\t{e.Ticks.ToString(CultureInfo.InvariantCulture)}\t{e.Size.ToString(CultureInfo.InvariantCulture)}\t{path}";
        return $"{body}\t{Check(body):x8}\n";
    }

    private static bool TryParse(string line, out string path, out Entry entry)
    {
        path = string.Empty;
        entry = default;
        var f = line.Split('\t');
        if (f.Length != 5 || !uint.TryParse(f[4], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var check)
            || check != Check(line.AsSpan(0, line.Length - f[4].Length - 1))
            || !CacheHash.TryParseFileName(f[0], out var hash)
            || !long.TryParse(f[1], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
            || !long.TryParse(f[2], NumberStyles.None, CultureInfo.InvariantCulture, out var size))
            return false;
        path = f[3];
        entry = new Entry(ticks, size, hash);
        return true;
    }

    /// <summary>FNV-1a over the line's UTF-16 code units.</summary>
    private static uint Check(ReadOnlySpan<char> s)
    {
        uint h = 2166136261;
        foreach (char c in s)
            h = (h ^ c) * 16777619;
        return h;
    }
}
