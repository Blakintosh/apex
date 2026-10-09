using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Apex.Editor.Services.Gdt;

namespace Apex.Editor.Services.Session;

/// <summary>
/// The on-disk half of hot exit: an append-only file of <see cref="JournalEntry"/> records under
/// <c>%LOCALAPPDATA%\Apex\session\&lt;environment&gt;\</c>, never anywhere near the install.
///
/// Each record is framed as <c>magic · length · CRC-32 · UTF-8 JSON</c>. A write torn by a crash or a kill fails its
/// length or checksum and is skipped; the reader then looks for the next magic, so one bad record never hides the
/// ones after it. Rewrites (compaction, discard) go to a temp file that replaces the journal in one move.
///
/// Every write happens on one background writer: the UI thread only queues entries and never waits on the disk,
/// except for <see cref="Flush"/> at close and after a crash, where waiting is the point.
/// </summary>
public sealed class SessionJournal : IDisposable
{
    public const string FileName = "journal.bin";
    public const int FormatVersion = 1;

    // "AJ" + 0x5E 0xA7: 0xA7 never starts a UTF-8 sequence, so JSON text can't look like a frame start.
    private const uint Magic = 0xA75E4A41;
    private const int HeaderSize = 12;
    private const int MaxRecord = 64 * 1024 * 1024;

    private readonly Channel<Command> _queue = Channel.CreateUnbounded<Command>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _writer;
    private FileStream _stream;
    private long _length;
    private int _failures;

    public string Directory { get; }
    public string FilePath { get; }

    /// <summary>The journal's size after the writes queued so far land (used to decide when to compact).</summary>
    public long Length => Interlocked.Read(ref _length);

    /// <summary>The first write that failed (disk full, access denied), or null while every write has landed.</summary>
    public Exception? LastError { get; private set; }

    /// <summary>True until a write fails.</summary>
    public bool IsHealthy => Volatile.Read(ref _failures) == 0;

    /// <summary>Raised on the writer thread the first time a write fails.</summary>
    public event Action<Exception>? WriteFailed;

    private SessionJournal(string directory, FileStream stream)
    {
        Directory = directory;
        FilePath = stream.Name;
        _stream = stream;
        _length = stream.Length;
        _writer = Task.Run(WriterLoop);
    }

    // ── Locating the journal ─────────────────────────────────────────────────

    /// <summary>
    /// The folder sessions live under: <c>APEX_SESSION_DIR</c> when set (tests), otherwise
    /// <c>%LOCALAPPDATA%\Apex\session</c>. Null when the screenshot harness forces mock data without naming a folder,
    /// so a harness run never reads or writes a real user's session.
    /// </summary>
    public static string? DefaultRoot
    {
        get
        {
            if (Environment.GetEnvironmentVariable("APEX_SESSION_DIR") is { Length: > 0 } dir)
                return dir;
            if (Environment.GetEnvironmentVariable("APEX_FORCE_MOCK") == "1")
                return null;
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Apex", "session");
        }
    }

    /// <summary>
    /// One session per install, so two installs (or the sample data) never mix: <c>mock</c>, or <c>live-</c> and a
    /// hash of the install folder.
    /// </summary>
    public static string KeyFor(GameEnvironment env)
    {
        if (!env.IsAvailable || env.Bo3Root is null)
            return "mock";
        var root = Path.GetFullPath(env.Bo3Root).TrimEnd('\\', '/').ToLowerInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(root));
        return "live-" + Convert.ToHexString(hash, 0, 6).ToLowerInvariant();
    }

    /// <summary>
    /// Opens (or creates) the journal in <paramref name="directory"/> and reads what the previous run left.
    /// Returns null when the journal can't be opened for writing (another Apex window on the same install holds it,
    /// or the folder isn't writable); <paramref name="error"/> says why.
    /// </summary>
    public static SessionJournal? Open(string directory, out ReadResult previous, out Exception? error)
    {
        previous = ReadResult.Empty;
        error = null;
        FileStream stream;
        try
        {
            System.IO.Directory.CreateDirectory(directory);
            // Write access is exclusive: a second Apex on the same install gets a sharing violation instead of
            // interleaving its records with ours.
            stream = new FileStream(Path.Combine(directory, FileName), FileMode.OpenOrCreate, FileAccess.ReadWrite,
                FileShare.Read, 4096, FileOptions.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            error = ex;
            return null;
        }

        try
        {
            var bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
            previous = Parse(bytes);
            // A torn tail would sit in front of everything appended from now on; cut it off. Damage further in
            // stays (the reader skips it) until the next rewrite drops it.
            if (previous.ValidLength < bytes.Length)
                stream.SetLength(previous.ValidLength);
            stream.Seek(0, SeekOrigin.End);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            stream.Dispose();
            error = ex;
            return null;
        }
        return new SessionJournal(directory, stream);
    }

    /// <summary>Reads a journal file without opening it for writing (tests, diagnostics).</summary>
    public static ReadResult Read(string path)
    {
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var bytes = new byte[s.Length];
        s.ReadExactly(bytes);
        return Parse(bytes);
    }

    // ── Writing (any thread; the UI thread in practice) ──────────────────────

    /// <summary>Queues records to append. Never blocks.</summary>
    public void Append(params JournalEntry[] entries)
    {
        if (entries.Length > 0)
            _queue.Writer.TryWrite(new Command(CommandKind.Append, entries, null));
    }

    /// <summary>Queues a replacement of the whole journal with <paramref name="entries"/> (compaction, discard).</summary>
    public void Rewrite(IReadOnlyList<JournalEntry> entries) =>
        _queue.Writer.TryWrite(new Command(CommandKind.Rewrite, entries, null));

    /// <summary>
    /// Waits until everything queued so far is on disk (flushed through the OS cache). Only for close and crash
    /// handling. False when the writer didn't finish within <paramref name="timeout"/> or a write failed.
    /// </summary>
    public bool Flush(TimeSpan timeout)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_queue.Writer.TryWrite(new Command(CommandKind.Barrier, Array.Empty<JournalEntry>(), done)))
            return false;
        try
        {
            return done.Task.Wait(timeout) && IsHealthy;
        }
        catch (AggregateException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        _queue.Writer.TryComplete();
        try { _writer.Wait(TimeSpan.FromSeconds(3)); }
        catch (AggregateException) { }
        try { _stream.Dispose(); }
        catch (IOException) { }
    }

    private enum CommandKind { Append, Rewrite, Barrier }

    private sealed record Command(CommandKind Kind, IReadOnlyList<JournalEntry> Entries, TaskCompletionSource? Done);

    private async Task WriterLoop()
    {
        var reader = _queue.Reader;
        while (await reader.WaitToReadAsync().ConfigureAwait(false))
        {
            // Drain everything queued, then make it durable once: a burst of edits costs one flush.
            var dirty = false;
            var barriers = new List<TaskCompletionSource>();
            while (reader.TryRead(out var cmd))
            {
                try
                {
                    switch (cmd.Kind)
                    {
                        case CommandKind.Append:
                            foreach (var e in cmd.Entries)
                                WriteFrame(_stream, e);
                            dirty = true;
                            break;
                        case CommandKind.Rewrite:
                            if (dirty)
                                _stream.Flush(true);
                            dirty = false;
                            RewriteNow(cmd.Entries);
                            break;
                        case CommandKind.Barrier:
                            barriers.Add(cmd.Done!);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    Fail(ex);
                }
            }
            if (dirty)
            {
                try { _stream.Flush(true); }
                catch (Exception ex) { Fail(ex); }
            }
            Interlocked.Exchange(ref _length, SafeLength());
            foreach (var b in barriers)
                b.TrySetResult();
        }
    }

    private long SafeLength()
    {
        try { return _stream.Length; }
        catch (Exception) { return Length; }
    }

    private void Fail(Exception ex)
    {
        if (Interlocked.Increment(ref _failures) != 1)
            return;
        LastError = ex;
        WriteFailed?.Invoke(ex);
    }

    private void RewriteNow(IReadOnlyList<JournalEntry> entries)
    {
        var tmp = FilePath + ".tmp";
        using (var t = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            foreach (var e in entries)
                WriteFrame(t, e);
            t.Flush(true);
        }
        // Release our handle, swap the new file in with one rename (atomic on NTFS), reopen for appends.
        _stream.Dispose();
        File.Move(tmp, FilePath, overwrite: true);
        _stream = new FileStream(FilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        _stream.Seek(0, SeekOrigin.End);
    }

    private static void WriteFrame(Stream s, JournalEntry entry)
    {
        var payload = entry.ToUtf8();
        Span<byte> header = stackalloc byte[HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header, Magic);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], payload.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..], Crc32.Compute(payload));
        // One write per frame, so a frame is either wholly in a buffer flush or a detectable tear.
        var frame = new byte[HeaderSize + payload.Length];
        header.CopyTo(frame);
        payload.CopyTo(frame, HeaderSize);
        s.Write(frame);
    }

    // ── Reading ──────────────────────────────────────────────────────────────

    /// <summary>What a journal file held.</summary>
    /// <param name="Entries">Every intact record, in file order.</param>
    /// <param name="DamagedRegions">Runs of bytes that weren't an intact record (torn writes, corruption).</param>
    /// <param name="ValidLength">Where the last intact record ends.</param>
    public sealed record ReadResult(IReadOnlyList<JournalEntry> Entries, int DamagedRegions, long ValidLength)
    {
        public static readonly ReadResult Empty = new(Array.Empty<JournalEntry>(), 0, 0);

        /// <summary>True when the previous run wrote its closing record (it didn't crash or get killed).</summary>
        public bool EndedCleanly => Entries.Count == 0 || Entries[^1].Op == "bye";
    }

    public static ReadResult Parse(ReadOnlySpan<byte> data)
    {
        var entries = new List<JournalEntry>();
        var damaged = 0;
        var inDamage = false;
        long validEnd = 0;
        var pos = 0;
        while (pos + HeaderSize <= data.Length)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(data[pos..]) == Magic)
            {
                var len = BinaryPrimitives.ReadInt32LittleEndian(data[(pos + 4)..]);
                var crc = BinaryPrimitives.ReadUInt32LittleEndian(data[(pos + 8)..]);
                if (len >= 0 && len <= MaxRecord && pos + HeaderSize + len <= data.Length)
                {
                    var payload = data.Slice(pos + HeaderSize, len);
                    if (Crc32.Compute(payload) == crc && TryDeserialize(payload) is { } entry)
                    {
                        entries.Add(entry);
                        pos += HeaderSize + len;
                        validEnd = pos;
                        inDamage = false;
                        continue;
                    }
                }
            }
            if (!inDamage)
            {
                damaged++;
                inDamage = true;
            }
            // Resynchronise on the next frame start.
            var next = IndexOfMagic(data, pos + 1);
            pos = next < 0 ? data.Length : next;
        }
        if (pos < data.Length && !inDamage && validEnd < data.Length)
            damaged++; // a tail too short to hold a header
        return new ReadResult(entries, damaged, validEnd);
    }

    private static JournalEntry? TryDeserialize(ReadOnlySpan<byte> payload)
    {
        try { return JournalEntry.FromUtf8(payload); }
        catch (System.Text.Json.JsonException) { return null; }
    }

    private static int IndexOfMagic(ReadOnlySpan<byte> data, int from)
    {
        Span<byte> magic = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(magic, Magic);
        if (from >= data.Length)
            return -1;
        var i = data[from..].IndexOf(magic);
        return i < 0 ? -1 : from + i;
    }
}

/// <summary>CRC-32 (IEEE), for the journal's per-record checksum.</summary>
internal static class Crc32
{
    private static readonly uint[] Table = Build();

    private static uint[] Build()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[i] = c;
        }
        return table;
    }

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return ~crc;
    }
}
