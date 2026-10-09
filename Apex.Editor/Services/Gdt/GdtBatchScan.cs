using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using Apex.Editor.Models;

namespace Apex.Editor.Services.Gdt;

/// <summary>
/// Scan-scoped, single-file byte cache used to collapse per-asset file opens during a full-corpus
/// <c>prop:</c>/<c>is:modified</c> scan. Callers walk records grouped by their backing .gdt (the
/// browser's <c>ComputeHits</c> already iterates one <see cref="GdtFile"/> — and therefore one
/// source file — at a time), so remembering just the most-recently-read file lets every asset in a
/// file reuse a single whole-file read. Up to ~95k per-asset <see cref="FileStream"/> opens collapse
/// into ~2,034 sequential whole-file reads while at most one file (≤ 11 MB) is resident.
///
/// The laziness invariant holds: transient property dictionaries are handed back to the caller and
/// never cached onto <see cref="AssetRecord"/>, and the cached file buffer is dropped when the scope
/// is disposed. The scope is ambient (thread-static) so an existing per-record matcher —
/// e.g. <see cref="AssetQuery"/> — can consult it without threading a context object through every
/// call. Not thread-safe: one scope per scanning thread (a parallel scan opens one per worker, e.g.
/// one <see cref="AssetQuery.MatchAll"/> per GDT).
/// </summary>
public sealed class GdtFileScan : IDisposable
{
    [ThreadStatic] private static GdtFileScan? _current;

    /// <summary>The scope active on the current thread, or null if no scan is in progress.</summary>
    public static GdtFileScan? Current => _current;

    private string? _path;
    private byte[]? _bytes;
    private int _length;
    private int _depth;

    private GdtFileScan() { }

    /// <summary>
    /// Opens a scan scope on the current thread. Wrap a per-record filter loop in
    /// <c>using var scan = GdtFileScan.Begin();</c> and every <see cref="AssetQuery"/> property read
    /// inside it reuses one whole-file buffer per source file. Nested Begin/Dispose pairs reuse the
    /// outermost scope (reference-counted) so an inner dispose never releases the shared buffer early.
    /// </summary>
    public static GdtFileScan Begin()
    {
        _current ??= new GdtFileScan();
        _current._depth++;
        return _current;
    }

    /// <summary>
    /// Transient property dictionary for <paramref name="record"/>. Already-materialized records
    /// (open/edited/mock) return their in-memory dictionary; disk-backed records are parsed from a
    /// cached whole-file buffer (read once per file). The result is never cached onto the record.
    /// </summary>
    public IReadOnlyDictionary<string, string> ScanProperties(AssetRecord record)
    {
        if (record.IsMaterialized)
            return record.Properties;

        if (record.Source is GdtPropertySource s)
        {
            var buf = GetFile(s.Path);
            if (buf is null)
                return Empty;
            return GdtParser.ParseProperties(buf.AsSpan(0, _length), s.Offset, s.Length);
        }

        // Sourceless record with no materialized data — falls back to the record's own view.
        return record.ScanProperties;
    }

    /// <summary>
    /// Materializes <paramref name="source"/> from the shared whole-file buffer (one read per file
    /// across the scope), or null when the file couldn't be read. Used by
    /// <see cref="GdtPropertySource.Materialize"/> so bulk materialization inside a scan scope —
    /// e.g. opening table mode over 400 rows — doesn't open one <see cref="FileStream"/> per record.
    /// </summary>
    public Dictionary<string, string>? TryMaterialize(GdtPropertySource source)
    {
        var buf = GetFile(source.Path);
        return buf is null ? null : GdtParser.ParseProperties(buf.AsSpan(0, _length), source.Offset, source.Length);
    }

    /// <summary>
    /// Returns the raw (undecoded) property-body bytes for <paramref name="record"/> as a slice of the
    /// cached whole-file buffer, reading the file once and reusing it across every asset in that file.
    /// Sets <paramref name="hasBody"/> false for materialized/sourceless records or when the file
    /// couldn't be read / the slice is out of range (the caller then falls back to the dictionary path).
    /// The returned span is only valid until the next file is read or the scope is disposed.
    /// </summary>
    public ReadOnlySpan<byte> GetBody(AssetRecord record, out bool hasBody)
    {
        if (!record.IsMaterialized && record.Source is GdtPropertySource s && s.Length > 0)
        {
            var buf = GetFile(s.Path);
            if (buf is not null && s.Offset >= 0 && s.Offset + s.Length <= _length)
            {
                hasBody = true;
                return buf.AsSpan((int)s.Offset, s.Length);
            }
        }
        hasBody = false;
        return default;
    }

    private byte[]? GetFile(string path)
    {
        if (_bytes is not null && string.Equals(_path, path, StringComparison.OrdinalIgnoreCase))
            return _bytes;

        // Moving to a new file: return the previous pooled buffer so at most one file is resident and
        // the whole-corpus scan doesn't allocate ~430 MB of one-shot arrays (pool reuse across files).
        Release();
        try
        {
            using var fs = new FileStream(
                path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, bufferSize: 1 << 16, FileOptions.SequentialScan);
            var len = fs.Length;
            if (len is <= 0 or > int.MaxValue)
                return null;

            var n = (int)len;
            var bytes = ArrayPool<byte>.Shared.Rent(n);
            var read = 0;
            while (read < n)
            {
                var r = fs.Read(bytes, read, n - read);
                if (r <= 0)
                    break;
                read += r;
            }

            _bytes = bytes;
            _length = read;
            _path = path;
            return bytes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void Release()
    {
        if (_bytes is not null)
            ArrayPool<byte>.Shared.Return(_bytes);
        _bytes = null;
        _path = null;
        _length = 0;
    }

    public void Dispose()
    {
        if (_depth > 0 && --_depth > 0)
            return; // inner scope of a nested pair — keep the shared buffer for the outer scope
        Release();
        if (ReferenceEquals(_current, this))
            _current = null;
    }

    private static readonly Dictionary<string, string> Empty =
        new(StringComparer.OrdinalIgnoreCase);
}
