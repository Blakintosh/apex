using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using Apex.Editor.Models;

namespace Apex.Editor.Services.Gdt;

/// <summary>
/// One indexed asset: its name, its gdf type (root) or parent name (derived), and the byte
/// offset/length of its property body within the source file — enough to lazily re-read it later.
/// </summary>
public readonly record struct GdtIndexEntry(
    string Name,
    string TypeOrParent,
    bool IsDerived,
    long BodyOffset,
    int BodyLength);

/// <summary>
/// Single forward pass over a .gdt file producing a <see cref="GdtIndexEntry"/> per asset without
/// materializing any property values. Operates on raw bytes for speed and to yield byte offsets
/// that <see cref="GdtParser.ParseProperties"/> can seek to directly.
/// </summary>
public static class GdtIndexer
{
    // gdf type names are a tiny closed vocabulary shared across all files — intern at index time.
    private static readonly ConcurrentDictionary<string, string> TypePool = new(StringComparer.Ordinal);

    private const byte OpenBrace = (byte)'{';
    private const byte CloseBrace = (byte)'}';
    private const byte OpenParen = (byte)'(';
    private const byte CloseParen = (byte)')';
    private const byte OpenBracket = (byte)'[';
    private const byte CloseBracket = (byte)']';

    /// <summary>Reads and indexes a whole file. On any I/O error returns an empty list (never throws).</summary>
    public static List<GdtIndexEntry> IndexFile(string path) => IndexFile(path, out _);

    /// <summary>
    /// Reads and indexes a whole file, stamping the bytes it indexed (what a later save expects to find). On any I/O
    /// error returns an empty list and a null stamp (never throws).
    /// </summary>
    public static List<GdtIndexEntry> IndexFile(string path, out Save.GdtStamp? stamp)
    {
        stamp = null;
        // The file image only lives while it is indexed (entries copy out names and offsets), so it is
        // rented rather than allocated: a full load otherwise leaves ~430 MB of large-object garbage.
        byte[]? bytes = null;
        try
        {
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.SequentialScan);
            var length = RandomAccess.GetLength(handle);
            if (length == 0)
                stamp = Save.GdtStamp.Of(ReadOnlySpan<byte>.Empty, File.GetLastWriteTimeUtc(handle));
            if (length == 0 || length > Array.MaxLength)
                return new List<GdtIndexEntry>();

            var n = (int)length;
            bytes = ArrayPool<byte>.Shared.Rent(n);
            var read = 0;
            while (read < n)
            {
                var r = RandomAccess.Read(handle, bytes.AsSpan(read, n - read), read);
                if (r <= 0)
                    return new List<GdtIndexEntry>(); // truncated underneath us — as File.ReadAllBytes would fail
                read += r;
            }

            stamp = Save.GdtStamp.Of(bytes.AsSpan(0, n), File.GetLastWriteTimeUtc(handle));
            return Index(bytes.AsSpan(0, n));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new List<GdtIndexEntry>();
        }
        finally
        {
            if (bytes is not null)
                ArrayPool<byte>.Shared.Return(bytes);
        }
    }

    /// <summary>Indexes an in-memory file image. Exposed for testing without touching disk.</summary>
    public static List<GdtIndexEntry> Index(ReadOnlySpan<byte> span)
    {
        var entries = new List<GdtIndexEntry>();
        var n = span.Length;
        var pos = 0;

        // Enter the single outer '{'. Empty stubs ("{\r\n}\r\n") fall straight through.
        while (pos < n && span[pos] != OpenBrace)
            pos++;
        if (pos >= n)
            return entries;
        pos++; // past outer brace

        while (pos < n)
        {
            GdtParser.SkipWhitespace(span, ref pos);
            if (pos >= n)
                break;

            var c = span[pos];
            if (c == CloseBrace)
                break; // outer close
            if (c != GdtParser.Quote)
            {
                pos++; // unexpected byte — skip defensively
                continue;
            }

            // Asset name.
            var nameSpan = GdtParser.ReadQuoted(span, ref pos);
            var name = GdtParser.Decode(nameSpan);

            GdtParser.SkipWhitespace(span, ref pos);
            if (pos >= n)
                break;

            bool isDerived;
            string typeOrParent;
            var open = span[pos];
            if (open == OpenParen)
            {
                pos++;
                GdtParser.SkipWhitespace(span, ref pos);
                var gdf = pos < n && span[pos] == GdtParser.Quote
                    ? GdtParser.Decode(GdtParser.ReadQuoted(span, ref pos))
                    : string.Empty;
                typeOrParent = InternType(StripGdf(gdf));
                isDerived = false;
                SkipTo(span, ref pos, CloseParen);
            }
            else if (open == OpenBracket)
            {
                pos++;
                GdtParser.SkipWhitespace(span, ref pos);
                typeOrParent = pos < n && span[pos] == GdtParser.Quote
                    ? GdtParser.Decode(GdtParser.ReadQuoted(span, ref pos))
                    : string.Empty;
                isDerived = true;
                SkipTo(span, ref pos, CloseBracket);
            }
            else
            {
                continue; // malformed header — resync on next quote
            }

            GdtParser.SkipWhitespace(span, ref pos);
            if (pos >= n)
                break;
            if (span[pos] != OpenBrace)
                continue; // no body — resync

            pos++; // past body's opening brace
            var bodyStart = pos;
            var depth = 1;
            while (pos < n && depth > 0)
            {
                var b = span[pos];
                if (b == GdtParser.Quote)
                {
                    GdtParser.ReadQuoted(span, ref pos); // skip whole quoted string (handles braces in values)
                    continue;
                }
                if (b == GdtParser.Slash && GdtParser.IsComment(span, pos))
                {
                    GdtParser.SkipComment(span, ref pos); // a brace or quote in a comment is text
                    continue;
                }
                if (b == OpenBrace)
                    depth++;
                else if (b == CloseBrace)
                    depth--;
                pos++;
            }

            var bodyEnd = pos - 1; // index of the matched closing brace
            var bodyLen = bodyEnd - bodyStart;
            if (bodyLen < 0)
                bodyLen = 0;

            entries.Add(new GdtIndexEntry(name, typeOrParent, isDerived, bodyStart, bodyLen));
        }

        return entries;
    }

    private static void SkipTo(ReadOnlySpan<byte> s, ref int pos, byte target)
    {
        while (pos < s.Length && s[pos] != target)
            pos++;
        if (pos < s.Length)
            pos++; // past the target
    }

    private static string StripGdf(string gdf)
    {
        if (gdf.EndsWith(".gdf", StringComparison.OrdinalIgnoreCase))
            return gdf[..^4];
        return gdf;
    }

    private static string InternType(string type) =>
        type.Length == 0 ? type : TypePool.GetOrAdd(type, type);
}

/// <summary>
/// Lazy <see cref="IPropertySource"/> that materializes an asset's properties by re-reading its body
/// slice from the .gdt file on first access. The <paramref name="Path"/> string is shared across all
/// assets in the same file, so the per-asset overhead is just two offsets and a reference.
/// </summary>
public sealed record GdtPropertySource(string Path, long Offset, int Length) : IPropertySource
{
    public Dictionary<string, string> Materialize()
    {
        // Inside a scan scope (e.g. table mode materializing hundreds of rows) reuse its whole-file
        // buffer — one read per file instead of one FileStream open per record.
        if (GdtFileScan.Current is { } scan && scan.TryMaterialize(this) is { } props)
            return props;
        return GdtParser.ParseProperties(Path, Offset, Length);
    }
}
