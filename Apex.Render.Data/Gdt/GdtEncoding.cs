using System;
using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;

namespace Apex.Render.Data.Gdt;

/// <summary>
/// The text encoding of GDT files: the machine's ANSI code page, as APE reads and writes them (Windows-1252 on a
/// Western install, so <c>é</c> is the single byte <c>E9</c>). Nearly every GDT is plain ASCII, which every code page
/// agrees on; only the rare accented display name depends on this. A character the code page has no byte for can't be
/// stored at all, so the save refuses it (see <see cref="CanHold"/>) rather than letting APE read back something else.
/// </summary>
public static class GdtEncoding
{
    /// <summary>The ANSI code page; decoding never throws, encoding throws on a character it can't hold.</summary>
    public static Encoding File { get; } = Create();

    /// <summary>Decodes a quoted-string body. ASCII, nearly all of them, takes the vectorized path.</summary>
    public static string GetString(ReadOnlySpan<byte> s) =>
        s.IsEmpty ? string.Empty : System.Text.Ascii.IsValid(s) ? Encoding.ASCII.GetString(s) : File.GetString(s);

    /// <summary>Decodes into <paramref name="chars"/>, which must be at least <c>s.Length</c> long. Returns the count.</summary>
    public static int GetChars(ReadOnlySpan<byte> s, Span<char> chars) =>
        System.Text.Ascii.ToUtf16(s, chars, out var n) == OperationStatus.Done ? n : File.GetChars(s, chars);

    public static byte[] GetBytes(string s) => File.GetBytes(s);

    /// <summary>Whether a GDT can hold <paramref name="s"/>; if not, <paramref name="bad"/> is the first character it can't.</summary>
    public static bool CanHold(string s, out string bad)
    {
        bad = string.Empty;
        if (System.Text.Ascii.IsValid(s))
            return true;
        for (var i = 0; i < s.Length; i++)
        {
            var len = char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]) ? 2 : 1;
            if (s[i] >= 0x80)
                try
                {
                    File.GetByteCount(s.AsSpan(i, len));
                }
                catch (EncoderFallbackException)
                {
                    bad = s.Substring(i, len);
                    return false;
                }
            i += len - 1;
        }
        return true;
    }

    private static Encoding Create()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var codePage = OperatingSystem.IsWindows() ? (int)GetACP() : 1252;
        try
        {
            return Encoding.GetEncoding(codePage, EncoderFallback.ExceptionFallback, DecoderFallback.ReplacementFallback);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException)
        {
            return Encoding.GetEncoding(1252, EncoderFallback.ExceptionFallback, DecoderFallback.ReplacementFallback);
        }
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetACP();
}
