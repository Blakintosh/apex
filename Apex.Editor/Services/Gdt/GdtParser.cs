using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using GdtEncoding = Apex.Render.Data.Gdt.GdtEncoding;

namespace Apex.Editor.Services.Gdt;

/// <summary>
/// Low-level, allocation-frugal helpers for reading GDT text at the byte level, plus lazy
/// materialization of a single asset body into its property dictionary.
///
/// GDT is structurally ASCII: the significant bytes (<c>" \ ( ) [ ] { }</c>) are the same in every ANSI
/// code page, so we can scan raw bytes and only decode the contents of quoted strings (see
/// <see cref="GdtEncoding"/>). Values are raw, as APE reads them: a backslash is a literal character
/// (paths mix <c>\</c> and <c>\\</c>; skinOverride separates lines with the literal text <c>\r\n</c>) and a
/// value ends at the next quote.
/// </summary>
public static class GdtParser
{
    // Property keys are drawn from a small vocabulary repeated across ~95k assets — intern them so
    // the whole database shares one string instance per key. Values are left as-is (highly unique).
    private static readonly ConcurrentDictionary<string, string> KeyPool = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, string>.AlternateLookup<ReadOnlySpan<char>> KeyPoolBySpan =
        KeyPool.GetAlternateLookup<ReadOnlySpan<char>>();

    internal const byte Quote = (byte)'"';

    /// <summary>
    /// Re-reads the <paramref name="length"/>-byte body slice at <paramref name="offset"/> in
    /// <paramref name="path"/> and decodes it into a case-insensitive property dictionary.
    /// Single-asset path: opens the file, seeks, reads only the slice. For scanning many assets in
    /// the same file prefer the whole-file-buffer overload below (read the file once, parse each slice).
    /// </summary>
    public static Dictionary<string, string> ParseProperties(string path, long offset, int length)
    {
        if (length <= 0)
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var buf = new byte[length];
        // bufferSize: a sane default (not 1) so the seek-then-read isn't throttled to byte-at-a-time.
        using var fs = new FileStream(
            path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, bufferSize: 4096, FileOptions.None);
        fs.Seek(offset, SeekOrigin.Begin);

        var read = 0;
        while (read < length)
        {
            var r = fs.Read(buf, read, length - read);
            if (r <= 0)
                break;
            read += r;
        }

        return ParseBody(buf.AsSpan(0, read));
    }

    /// <summary>
    /// Parses an asset's property body directly out of a caller-provided whole-file buffer, avoiding
    /// a per-asset file open/seek. <paramref name="offset"/>/<paramref name="length"/> are the body
    /// slice (as produced by <see cref="GdtIndexer"/>). Used by the batched full-corpus scan path so a
    /// .gdt is read once and every asset in it is parsed from that single buffer. Returns an empty
    /// dictionary if the slice falls outside the buffer (e.g. the file changed on disk since indexing).
    /// </summary>
    public static Dictionary<string, string> ParseProperties(ReadOnlySpan<byte> fileBuffer, long offset, int length)
    {
        if (length <= 0 || offset < 0 || offset > fileBuffer.Length)
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var start = (int)offset;
        var avail = Math.Min(length, fileBuffer.Length - start);
        if (avail <= 0)
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        return ParseBody(fileBuffer.Slice(start, avail));
    }

    /// <summary>Parses a body slice (the content between an asset's braces) into key/value pairs.</summary>
    internal static Dictionary<string, string> ParseBody(ReadOnlySpan<byte> body)
    {
        // One property per line: sizing from the line count avoids ~7 rehashes for a ~180-entry body
        // (insertion order, and so enumeration order, does not depend on capacity).
        var dict = new Dictionary<string, string>(body.Count((byte)'\n'), StringComparer.OrdinalIgnoreCase);
        var pos = 0;
        while (true)
        {
            SkipWhitespace(body, ref pos);
            if (pos >= body.Length || body[pos] != Quote)
                break;

            var keySpan = ReadQuoted(body, ref pos);
            SkipWhitespace(body, ref pos);
            if (pos >= body.Length || body[pos] != Quote)
                break; // key without a value — stop cleanly

            var valueSpan = ReadQuoted(body, ref pos);
            dict[InternKey(keySpan)] = Decode(valueSpan);
        }

        return dict;
    }

    /// <summary>Advances past ASCII whitespace / control bytes and <c>//</c> comments between tokens.</summary>
    internal static void SkipWhitespace(ReadOnlySpan<byte> s, ref int pos)
    {
        while (pos < s.Length)
        {
            if (s[pos] <= (byte)' ')
                pos++;
            else if (IsComment(s, pos))
                SkipComment(s, ref pos);
            else
                return;
        }
    }

    internal const byte Slash = (byte)'/';

    /// <summary>
    /// A <c>//</c> comment starts at <paramref name="pos"/>: two slashes outside a quoted string, where a token could
    /// start. It runs to the end of its line. APE never writes one; a hand edit can, and the data layer's GDT reader
    /// (<c>Apex.Render.Data.Gdt.GdtFile</c>) reads the same comment, so the editor skips it rather than stopping there.
    /// </summary>
    internal static bool IsComment(ReadOnlySpan<byte> s, int pos) =>
        s[pos] == Slash && pos + 1 < s.Length && s[pos + 1] == Slash;

    /// <summary>Moves <paramref name="pos"/> from a comment's first slash to just past its line's LF (or the end).</summary>
    internal static void SkipComment(ReadOnlySpan<byte> s, ref int pos)
    {
        var lf = s[pos..].IndexOf((byte)'\n');
        pos = lf < 0 ? s.Length : pos + lf + 1;
    }

    /// <summary>
    /// Reads a quoted string whose opening quote is at <paramref name="pos"/> and returns the raw
    /// content up to the next quote, leaving <paramref name="pos"/> just past the close.
    /// </summary>
    internal static ReadOnlySpan<byte> ReadQuoted(ReadOnlySpan<byte> s, scoped ref int pos)
    {
        pos++; // past opening quote
        var start = pos;
        var idx = s.Slice(start).IndexOf(Quote);
        var end = idx < 0 ? s.Length : start + idx;
        pos = end < s.Length ? end + 1 : s.Length; // past closing quote when present
        return s.Slice(start, end - start);
    }

    /// <summary>Decodes a quoted-string body (see <see cref="GdtEncoding"/>). Values are raw, as APE reads them: backslashes are literal.</summary>
    internal static string Decode(ReadOnlySpan<byte> s) => GdtEncoding.GetString(s);

    internal static string InternKey(string key) => KeyPool.GetOrAdd(key, key);

    /// <summary>
    /// The interned string for a raw key. Short keys are decoded into a stack buffer and looked up by
    /// span, so a key already in the pool — nearly all of them — allocates nothing.
    /// </summary>
    internal static string InternKey(ReadOnlySpan<byte> raw)
    {
        if (raw.Length > 128)
            return InternKey(Decode(raw));

        Span<char> chars = stackalloc char[raw.Length];
        var key = chars[..GdtEncoding.GetChars(raw, chars)];
        return KeyPoolBySpan.TryGetValue(key, out var interned) ? interned : InternKey(key.ToString());
    }

    /// <summary>
    /// Case-insensitive ASCII substring test over raw (undecoded) bytes. <paramref name="needleLower"/>
    /// must already be lowercase ASCII. Lets a <c>prop:</c> scan test a property key without decoding
    /// it to a string (keys are ASCII identifiers), avoiding an allocation per property per asset.
    /// </summary>
    internal static bool AsciiContainsIgnoreCase(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needleLower)
    {
        if (needleLower.IsEmpty)
            return true;
        if (haystack.Length < needleLower.Length)
            return false;

        var last = haystack.Length - needleLower.Length;
        for (var i = 0; i <= last; i++)
        {
            var j = 0;
            for (; j < needleLower.Length; j++)
            {
                var h = haystack[i + j];
                if (h >= (byte)'A' && h <= (byte)'Z')
                    h += 32;
                if (h != needleLower[j])
                    break;
            }
            if (j == needleLower.Length)
                return true;
        }
        return false;
    }
}

/// <summary>
/// Zero-allocation forward reader over an asset's property body (the bytes between its braces),
/// yielding each key/value as the still-escaped raw span. Lets a scan decode only the properties it
/// actually needs (e.g. a <c>prop:</c> filter decodes a value only when the key matches) instead of
/// building the full ~180-entry dictionary per asset — the difference between a ~5 s and a sub-second
/// full-corpus scan. Use <see cref="GdtParser.Decode"/> to turn a raw span into a resolved string.
/// </summary>
public ref struct GdtPropertyReader
{
    private readonly ReadOnlySpan<byte> _body;
    private int _pos;

    public GdtPropertyReader(ReadOnlySpan<byte> body)
    {
        _body = body;
        _pos = 0;
        KeyRaw = default;
        ValueRaw = default;
    }

    /// <summary>Raw (still-escaped) key bytes of the current property.</summary>
    public ReadOnlySpan<byte> KeyRaw { get; private set; }

    /// <summary>Raw (still-escaped) value bytes of the current property.</summary>
    public ReadOnlySpan<byte> ValueRaw { get; private set; }

    public bool MoveNext()
    {
        var pos = _pos;
        GdtParser.SkipWhitespace(_body, ref pos);
        if (pos >= _body.Length || _body[pos] != GdtParser.Quote)
        {
            _pos = pos;
            return false;
        }
        KeyRaw = GdtParser.ReadQuoted(_body, ref pos);

        GdtParser.SkipWhitespace(_body, ref pos);
        if (pos >= _body.Length || _body[pos] != GdtParser.Quote)
        {
            _pos = pos;
            return false;
        }
        ValueRaw = GdtParser.ReadQuoted(_body, ref pos);
        _pos = pos;
        return true;
    }
}
