using System;
using System.Collections.Generic;
using Apex.Editor.Services.Gdt;

namespace Apex.Editor.Services.Save;

/// <summary>
/// One asset as it sits in a GDT's bytes: where its name, type (or parent) and body are, and the whole block from the
/// start of its header line to the end of its closing brace's line. Offsets are byte indices into the file.
/// </summary>
public readonly record struct GdtAssetSpan(
    string Name,
    string TypeOrParent,
    bool IsDerived,
    int NameStart,
    int NameEnd,
    int TypeStart,
    int TypeEnd,
    int BodyStart,
    int BodyEnd,
    int BlockStart,
    int BlockEnd,
    bool Closed)
{
    public int BodyLength => Math.Max(0, BodyEnd - BodyStart);
}

/// <summary>A property inside a body: its key and value (contents between the quotes) and the line it sits on.</summary>
public readonly record struct GdtPropSpan(
    string Key,
    string Value,
    int KeyQuote,
    int ValueStart,
    int ValueEnd,
    int LineStart,
    bool LineHasOnlyThis,
    int LineEnd,
    string Indent,
    string Eol);

/// <summary>
/// The byte layout of a GDT, found with exactly the rules <see cref="GdtIndexer"/> uses (so an asset here is the same
/// asset the loader indexed, at the same body offset), plus what a writer needs to splice: block and line bounds and
/// the file's conventions (line ending and indentation) to give new text.
/// </summary>
public sealed class GdtLayout
{
    private const byte OpenBrace = (byte)'{';
    private const byte CloseBrace = (byte)'}';

    public List<GdtAssetSpan> Assets { get; } = new();

    /// <summary>Index of the file's outer closing brace, or -1 when it has none.</summary>
    public int OuterClose { get; private set; } = -1;

    /// <summary>The file's line ending: CRLF unless bare LF is the majority.</summary>
    public string Eol { get; private set; } = "\r\n";

    public string AssetIndent { get; private set; } = "\t";
    public string BraceIndent { get; private set; } = "\t";
    public string CloseIndent { get; private set; } = "\t";
    public string PropIndent { get; private set; } = "\t\t";

    public static GdtLayout Scan(ReadOnlySpan<byte> span)
    {
        var layout = new GdtLayout();
        var n = span.Length;
        var pos = 0;

        var crlf = span.Count("\r\n"u8);
        var lf = span.Count((byte)'\n') - crlf;
        layout.Eol = lf > crlf ? "\n" : "\r\n";

        while (pos < n && span[pos] != OpenBrace)
            pos++;
        if (pos >= n)
            return layout;
        pos++;

        while (pos < n)
        {
            GdtParser.SkipWhitespace(span, ref pos);
            if (pos >= n)
                break;
            var c = span[pos];
            if (c == CloseBrace)
            {
                layout.OuterClose = pos;
                break;
            }
            if (c != GdtParser.Quote)
            {
                pos++;
                continue;
            }

            var nameQuote = pos;
            var nameStart = pos + 1;
            var nameSpan = GdtParser.ReadQuoted(span, ref pos);
            var nameEnd = nameStart + nameSpan.Length;
            var name = GdtParser.Decode(nameSpan);

            GdtParser.SkipWhitespace(span, ref pos);
            if (pos >= n)
                break;

            bool isDerived;
            var typeStart = -1;
            var typeEnd = -1;
            string typeOrParent;
            var open = span[pos];
            if (open == (byte)'(' || open == (byte)'[')
            {
                isDerived = open == (byte)'[';
                pos++;
                GdtParser.SkipWhitespace(span, ref pos);
                typeOrParent = string.Empty;
                if (pos < n && span[pos] == GdtParser.Quote)
                {
                    typeStart = pos + 1;
                    var t = GdtParser.ReadQuoted(span, ref pos);
                    typeEnd = typeStart + t.Length;
                    typeOrParent = GdtParser.Decode(t);
                }
                var close = isDerived ? (byte)']' : (byte)')';
                while (pos < n && span[pos] != close)
                    pos++;
                if (pos < n)
                    pos++;
                if (!isDerived && typeOrParent.EndsWith(".gdf", StringComparison.OrdinalIgnoreCase))
                    typeOrParent = typeOrParent[..^4];
            }
            else
            {
                continue;
            }

            GdtParser.SkipWhitespace(span, ref pos);
            if (pos >= n)
                break;
            if (span[pos] != OpenBrace)
                continue;

            var braceAt = pos;
            pos++;
            var bodyStart = pos;
            var depth = 1;
            while (pos < n && depth > 0)
            {
                var b = span[pos];
                if (b == GdtParser.Quote)
                {
                    GdtParser.ReadQuoted(span, ref pos);
                    continue;
                }
                if (b == GdtParser.Slash && GdtParser.IsComment(span, pos))
                {
                    GdtParser.SkipComment(span, ref pos);
                    continue;
                }
                if (b == OpenBrace)
                    depth++;
                else if (b == CloseBrace)
                    depth--;
                pos++;
            }
            // As the indexer: the body ends at its closing brace, or one byte short of the end of a truncated file.
            var closed = depth == 0;
            var bodyEnd = Math.Max(bodyStart, pos - 1);

            var blockStart = LineStartIfClean(span, nameQuote, out _) ?? nameQuote;
            var blockEnd = closed ? EndOfLineAfter(span, bodyEnd + 1) : n;

            if (layout.Assets.Count == 0)
            {
                if (LineStartIfClean(span, nameQuote, out var ai) is not null) layout.AssetIndent = ai;
                if (LineStartIfClean(span, braceAt, out var bi) is not null) layout.BraceIndent = bi;
                if (closed && LineStartIfClean(span, bodyEnd, out var ci) is not null) layout.CloseIndent = ci;
            }

            layout.Assets.Add(new GdtAssetSpan(name, typeOrParent, isDerived, nameStart, nameEnd, typeStart, typeEnd,
                bodyStart, bodyEnd, blockStart, blockEnd, closed));
        }

        foreach (var a in layout.Assets)
        {
            var props = ScanProps(span, a, out _);
            if (props.Count > 0 && props[0].LineHasOnlyThis)
            {
                layout.PropIndent = props[0].Indent;
                break;
            }
        }
        return layout;
    }

    /// <summary>
    /// The properties of <paramref name="asset"/>, read with <see cref="GdtParser"/>'s rules (so the values here are
    /// what the loader reads). <paramref name="regular"/> is false when something the parser would ignore follows
    /// them in the body, or a value runs to the end of the body: such a body is only ever rewritten by hand.
    /// </summary>
    public static List<GdtPropSpan> ScanProps(ReadOnlySpan<byte> file, GdtAssetSpan asset, out bool regular)
    {
        var props = new List<GdtPropSpan>();
        var body = file[asset.BodyStart..asset.BodyEnd];
        var pos = 0;
        regular = true;
        while (true)
        {
            GdtParser.SkipWhitespace(body, ref pos);
            if (pos >= body.Length)
                break;
            if (body[pos] != GdtParser.Quote)
            {
                regular = false;
                break;
            }
            var keyQuote = pos;
            var key = GdtParser.ReadQuoted(body, ref pos);
            var keyClosed = pos <= body.Length && body[pos - 1] == GdtParser.Quote && pos - 1 > keyQuote;
            GdtParser.SkipWhitespace(body, ref pos);
            if (pos >= body.Length || body[pos] != GdtParser.Quote || !keyClosed)
            {
                regular = false;
                break;
            }
            var valueQuote = pos;
            var value = GdtParser.ReadQuoted(body, ref pos);
            var valueEnd = valueQuote + 1 + value.Length;
            if (valueEnd >= body.Length || body[valueEnd] != GdtParser.Quote)
                regular = false;

            var absKey = asset.BodyStart + keyQuote;
            var absValueEnd = asset.BodyStart + valueEnd;
            var lineStart = LineStartIfClean(file, absKey, out var indent);
            var after = absValueEnd + 1;
            while (after < asset.BodyEnd && file[after] is (byte)' ' or (byte)'\t')
                after++;
            var eol = "";
            var lineEnd = after;
            // A comment after the value stays on its line: the line ends after it, and is never this key's alone.
            var trailing = after < asset.BodyEnd && GdtParser.IsComment(file[..asset.BodyEnd], after);
            if (trailing)
            {
                var lf = file[after..asset.BodyEnd].IndexOf((byte)'\n');
                if (lf >= 0)
                    after += lf;
                if (lf > 0 && file[after - 1] == '\r')
                    after--;
            }
            if (after < asset.BodyEnd && file[after] == '\n') { eol = "\n"; lineEnd = after + 1; }
            else if (after + 1 < asset.BodyEnd && file[after] == '\r' && file[after + 1] == '\n') { eol = "\r\n"; lineEnd = after + 2; }

            props.Add(new GdtPropSpan(
                GdtParser.Decode(key), GdtParser.Decode(value),
                absKey, asset.BodyStart + valueQuote + 1, absValueEnd,
                lineStart ?? absKey, lineStart is not null && eol.Length > 0 && !trailing, lineEnd, indent, eol));
        }
        return props;
    }

    /// <summary>The start of <paramref name="at"/>'s line when only spaces and tabs precede it there, else null.</summary>
    internal static int? LineStartIfClean(ReadOnlySpan<byte> s, int at, out string indent)
    {
        var i = at;
        while (i > 0 && s[i - 1] is (byte)' ' or (byte)'\t')
            i--;
        indent = System.Text.Encoding.ASCII.GetString(s[i..at]);
        return i == 0 || s[i - 1] == '\n' ? i : null;
    }

    /// <summary>Just past the line ending that follows <paramref name="from"/> (spaces and tabs allowed before it), else <paramref name="from"/>.</summary>
    internal static int EndOfLineAfter(ReadOnlySpan<byte> s, int from)
    {
        var i = from;
        while (i < s.Length && s[i] is (byte)' ' or (byte)'\t')
            i++;
        if (i < s.Length && s[i] == '\n')
            return i + 1;
        if (i + 1 < s.Length && s[i] == '\r' && s[i + 1] == '\n')
            return i + 2;
        return from;
    }
}
