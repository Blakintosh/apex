using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Apex.Editor.Services.Preview.Notetracks;

/// <summary>What an alias name resolves to: its state and, when playable, the variants with a raw file.</summary>
public sealed record SoundResolution(NotetrackSoundState State, IReadOnlyList<SoundVariant> Variants);

/// <summary>
/// The sound alias CSVs (share/raw/sound/aliases), indexed by name only: each name maps to the byte offsets of its rows in
/// the first file that defines it, so the ~17 MB of CSVs is never held in memory. Rows are read back on demand
/// (<see cref="Resolve"/>) for the few aliases an anim uses. Top-level CSVs are indexed before subfolders, so a copy
/// in a backup folder never shadows the live one. Built once per install, lazily and off the UI thread; rebuilt when
/// a CSV is added, removed or changed (checked at most once a second, before a lookup), and retried after a failure.
/// </summary>
public sealed class SoundAliasIndex
{
    private static readonly ConcurrentDictionary<string, SoundAliasIndex> Shared = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan FreshnessInterval = TimeSpan.FromSeconds(1);

    private readonly string _aliasesDir;
    private readonly string _soundAssetsDir;
    private readonly object _gate = new();
    private readonly object _rebuildGate = new();
    private Task? _build;
    private Snapshot? _snapshot;
    private readonly Stopwatch _sinceCheck = new();

    private readonly record struct Columns(int FileSpec, int VolMin, int VolMax);

    /// <summary>One CSV as indexed: its path and columns (its length and write time are in <see cref="Snapshot.Seen"/>).</summary>
    private sealed record IndexedFile(string Path, Columns Columns);

    /// <summary>
    /// One build: name -> head of its row chain in <see cref="Rows"/>; rows are (file, byte offset of the line, next row of
    /// the same name or -1). Never changed once built; a rebuild swaps in a new one.
    /// </summary>
    private sealed class Snapshot
    {
        public readonly Dictionary<string, int> Names = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<(int File, int Offset, int Next)> Rows = new();
        public readonly List<IndexedFile> Files = new();
        // Every CSV seen (indexed or not), with its length and write time: a change to any is a rebuild.
        public readonly Dictionary<string, (long, DateTime)> Seen = new(StringComparer.OrdinalIgnoreCase);
    }

    public SoundAliasIndex(string aliasesDir, string soundAssetsDir)
    {
        _aliasesDir = aliasesDir;
        _soundAssetsDir = Path.GetFullPath(soundAssetsDir);
    }

    /// <summary>The shared index of an install (built on first use).</summary>
    public static SoundAliasIndex ForInstall(string bo3Root) =>
        Shared.GetOrAdd(bo3Root, root => new SoundAliasIndex(
            Path.Combine(root, "share", "raw", "sound", "aliases"), Path.Combine(root, "sound_assets")));

    /// <summary>Time the last build took, names, rows and files indexed (for the report line and the harness).</summary>
    public (TimeSpan Elapsed, int Names, int Rows, int Files) Stats { get; private set; }

    /// <summary>How many times the index has been built (a changed CSV rebuilds it).</summary>
    public int Builds { get; private set; }

    /// <summary>Starts the build (once; again after a failure) and completes when the index is ready.</summary>
    public Task ReadyAsync()
    {
        lock (_gate)
        {
            if (_build is null || _build.IsFaulted || _build.IsCanceled)
                _build = Task.Run(Build);
            return _build;
        }
    }

    private void Build()
    {
        var clock = Stopwatch.StartNew();
        var snapshot = new Snapshot();
        foreach (var path in CsvFiles())
            IndexFile(snapshot, path);
        lock (_gate)
        {
            _snapshot = snapshot;
            _sinceCheck.Restart();
            Builds++;
            Stats = (clock.Elapsed, snapshot.Names.Count, snapshot.Rows.Count, snapshot.Files.Count);
        }
    }

    /// <summary>The CSVs in index order: top level first, then each subfolder; an unreadable folder is skipped.</summary>
    private List<string> CsvFiles()
    {
        var files = new List<string>();
        try
        {
            if (!Directory.Exists(_aliasesDir))
                return files;
            files.AddRange(Directory.GetFiles(_aliasesDir, "*.csv").OrderBy(p => p, StringComparer.OrdinalIgnoreCase));
            foreach (var dir in Directory.GetDirectories(_aliasesDir).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    files.AddRange(Directory.GetFiles(dir, "*.csv", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.OrdinalIgnoreCase));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return files;
    }

    /// <summary>The stamp of a file that could not be stat'ed.</summary>
    private static readonly (long, DateTime) Unknown = (-1, DateTime.MinValue);

    private static (long, DateTime)? Stamp(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? (info.Length, info.LastWriteTimeUtc) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void IndexFile(Snapshot snapshot, string path)
    {
        byte[] bytes;
        DateTime written;
        try
        {
            written = File.GetLastWriteTimeUtc(path);
            // Shared as Resolve opens it: Excel keeps a CSV open (for writing) while it is being edited.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable now (a scanner or an exclusive lock): left out of Seen, so the next freshness check (at most
            // once a second, and only when a sound is looked up) tries it again whether or not it changed.
            snapshot.Seen.Remove(path);
            return;
        }
        snapshot.Seen[path] = (bytes.LongLength, written);
        var file = -1;
        var seenHere = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // A UTF-8 byte order mark (Excel writes one) is not part of the header's first column.
        var first = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        for (var start = first; start < bytes.Length;)
        {
            var end = Array.IndexOf(bytes, (byte)'\n', start);
            if (end < 0)
                end = bytes.Length;
            var name = FirstField(bytes, start, end);
            if (file < 0)
            {
                // The header (Name,Behavior,Storage,FileSpec,…) comes first; a file without one is not an alias table.
                if (name.Equals("Name", StringComparison.OrdinalIgnoreCase))
                {
                    var header = Csv.Split(Encoding.UTF8.GetString(bytes, start, end - start).TrimEnd('\r'));
                    var columns = new Columns(Find(header, "FileSpec"), Find(header, "VolMin"), Find(header, "VolMax"));
                    if (columns.FileSpec < 0)
                        return;
                    file = snapshot.Files.Count;
                    snapshot.Files.Add(new IndexedFile(path, columns));
                }
                else if (name.Length > 0 && !name.StartsWith('#'))
                    return;
            }
            else if (name.Length > 0 && !name.StartsWith('#') && !name.Equals("Name", StringComparison.OrdinalIgnoreCase))
            {
                var rows = snapshot.Rows;
                if (snapshot.Names.TryGetValue(name, out var head))
                {
                    // Rows of a name in this same file are its random variants; another file defining it again is a copy.
                    if (seenHere.Contains(name))
                    {
                        rows.Add((file, start, rows[head].Next));
                        rows[head] = rows[head] with { Next = rows.Count - 1 };
                    }
                }
                else
                {
                    snapshot.Names[name] = rows.Count;
                    rows.Add((file, start, -1));
                    seenHere.Add(name);
                }
            }
            start = end + 1;
        }
    }

    /// <summary>A line's first field: up to the first comma, or a quoted field read properly (it may hold commas).</summary>
    private static string FirstField(byte[] bytes, int start, int end)
    {
        var i = start;
        while (i < end && bytes[i] is (byte)' ' or (byte)'\t')
            i++;
        if (i < end && bytes[i] == '"')
        {
            var fields = Csv.Split(Encoding.UTF8.GetString(bytes, start, end - start).TrimEnd('\r'));
            return fields.Count > 0 ? fields[0].Trim() : "";
        }
        var comma = Array.IndexOf(bytes, (byte)',', start, end - start);
        return Encoding.UTF8.GetString(bytes, start, (comma < 0 ? end : comma) - start).Trim();
    }

    /// <summary><see cref="FirstField(byte[], int, int)"/> over a line already read, so a row read back is named as it was indexed.</summary>
    private static string FirstField(string line)
    {
        var trimmed = line.TrimStart(' ', '\t');
        if (trimmed.StartsWith('"'))
        {
            var fields = Csv.Split(line.TrimEnd('\r'));
            return fields.Count > 0 ? fields[0].Trim() : "";
        }
        var comma = line.IndexOf(',');
        return (comma < 0 ? line : line[..comma]).Trim();
    }

    private static int Find(IReadOnlyList<string> header, string column)
    {
        for (var i = 0; i < header.Count; i++)
            if (header[i].Trim().Equals(column, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    /// <summary>
    /// The snapshot to resolve against: rebuilt first when a CSV was added, removed or rewritten since it was built
    /// (checked at most once per <see cref="FreshnessInterval"/>, so a burst of lookups stats the folder once).
    /// </summary>
    private Snapshot Current()
    {
        Snapshot snapshot;
        lock (_gate)
        {
            snapshot = _snapshot ?? throw new InvalidOperationException("the alias index is not built yet");
            if (_sinceCheck.Elapsed < FreshnessInterval)
                return snapshot;
            _sinceCheck.Restart();
        }
        var files = CsvFiles();
        var changed = files.Count != snapshot.Seen.Count
                      || files.Any(f => !snapshot.Seen.TryGetValue(f, out var seen) || (Stamp(f) ?? Unknown) != seen);
        return changed ? Rebuilt(snapshot) : snapshot;
    }

    /// <summary>
    /// A snapshot newer than <paramref name="stale"/>: built here, or by another lookup that got there first (one
    /// rebuild at a time; the others wait for it rather than each building their own).
    /// </summary>
    private Snapshot Rebuilt(Snapshot stale)
    {
        lock (_rebuildGate)
        {
            lock (_gate)
                if (!ReferenceEquals(_snapshot, stale))
                    return _snapshot!;
            Build();
            lock (_gate)
                return _snapshot!;
        }
    }

    /// <summary>
    /// Every row of <paramref name="alias"/> with a raw file under sound_assets (blocking file reads: call off the UI
    /// thread, after <see cref="ReadyAsync"/>).
    /// </summary>
    public SoundResolution Resolve(string alias) => Resolve(alias.Trim(), Current(), retry: true);

    private SoundResolution Resolve(string alias, Snapshot snapshot, bool retry)
    {
        if (!snapshot.Names.TryGetValue(alias, out var head))
            return new SoundResolution(NotetrackSoundState.UnknownAlias, Array.Empty<SoundVariant>());

        var variants = new List<SoundVariant>();
        var undecodable = 0;
        var file = snapshot.Files[snapshot.Rows[head].File];
        try
        {
            using var stream = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            for (var row = head; row >= 0; row = snapshot.Rows[row].Next)
            {
                var line = ReadLine(stream, snapshot.Rows[row].Offset);
                var fields = Csv.Split(line);
                // The file changed since it was indexed (within the freshness window): the offset now lands on another
                // row. Never play that row's sound: re-index and look again.
                if (!FirstField(line).Equals(alias, StringComparison.OrdinalIgnoreCase))
                {
                    if (!retry)
                        return new SoundResolution(NotetrackSoundState.UnknownAlias, Array.Empty<SoundVariant>());
                    stream.Dispose();
                    return Resolve(alias, Rebuilt(snapshot), retry: false);
                }
                if (RawFile(Field(fields, file.Columns.FileSpec)) is not { } path)
                    continue;
                // The preview decodes WAV only; any other kind (and a WAV it can't read, found on play) stays silent.
                if (!path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
                {
                    undecodable++;
                    continue;
                }
                // Volumes are 0..100 in the CSVs (blank is full volume).
                float min = Volume(Field(fields, file.Columns.VolMin)), max = Volume(Field(fields, file.Columns.VolMax));
                variants.Add(new SoundVariant(path, Math.Min(min, max), Math.Max(min, max)));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return variants.Count > 0 ? new SoundResolution(NotetrackSoundState.Playable, variants)
            : new SoundResolution(undecodable > 0 ? NotetrackSoundState.Undecodable : NotetrackSoundState.NoRawFile, Array.Empty<SoundVariant>());

        static string Field(IReadOnlyList<string> fields, int i) => i >= 0 && i < fields.Count ? fields[i].Trim() : "";

        static float Volume(string s) =>
            float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? Math.Clamp(v / 100f, 0f, 1f) : 1f;
    }

    /// <summary>A FileSpec's file under sound_assets, or null when it is empty, missing, or points outside sound_assets.</summary>
    private string? RawFile(string spec)
    {
        if (spec.Length == 0 || Path.IsPathRooted(spec))
            return null;
        string full;
        try
        {
            full = Path.GetFullPath(Path.Combine(_soundAssetsDir, spec.Replace('/', '\\')));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
        if (!full.StartsWith(_soundAssetsDir.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
            return null;
        return File.Exists(full) ? full : null;
    }

    private static string ReadLine(FileStream stream, int offset)
    {
        stream.Position = offset;
        var buffer = new List<byte>(256);
        int b;
        while ((b = stream.ReadByte()) >= 0 && b != '\n')
            buffer.Add((byte)b);
        return Encoding.UTF8.GetString(buffer.ToArray()).TrimEnd('\r');
    }

    /// <summary>One CSV line split into fields (double-quoted fields may hold commas and doubled quotes).</summary>
    internal static class Csv
    {
        public static List<string> Split(string line)
        {
            var fields = new List<string>();
            var field = new StringBuilder();
            var quoted = false;
            for (var i = 0; i < line.Length; i++)
            {
                var c = line[i];
                if (quoted)
                {
                    if (c == '"' && i + 1 < line.Length && line[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else if (c == '"')
                        quoted = false;
                    else
                        field.Append(c);
                }
                else if (c == '"')
                    quoted = true;
                else if (c == ',')
                {
                    fields.Add(field.ToString());
                    field.Clear();
                }
                else
                    field.Append(c);
            }
            fields.Add(field.ToString());
            return fields;
        }
    }
}
