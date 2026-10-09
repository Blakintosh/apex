using System.Buffers.Binary;
using System.Globalization;

namespace Apex.Render.Data.Hashing;

/// <summary>
/// A 128-bit ToolsGfx cache hash (an MD4 digest). Stored in digest byte order; cache file names print it
/// with <c>"%08x%08x%08x%08x"</c> over four little-endian u32 words, i.e. each 4-byte group byte-reversed
/// relative to the usual MD4 hex (<see cref="ToFileName"/>).
/// </summary>
public readonly struct CacheHash : IEquatable<CacheHash>
{
    private readonly uint _w0, _w1, _w2, _w3;

    /// <summary>The all-zero hash (the deps key of an empty define list).</summary>
    public static CacheHash Zero => default;

    public CacheHash(ReadOnlySpan<byte> digest)
    {
        if (digest.Length != 16)
            throw new ArgumentException("A cache hash is exactly 16 bytes.", nameof(digest));
        _w0 = BinaryPrimitives.ReadUInt32LittleEndian(digest);
        _w1 = BinaryPrimitives.ReadUInt32LittleEndian(digest[4..]);
        _w2 = BinaryPrimitives.ReadUInt32LittleEndian(digest[8..]);
        _w3 = BinaryPrimitives.ReadUInt32LittleEndian(digest[12..]);
    }

    /// <summary>First little-endian word; APE's define-key loop restarts the chain when this is 0.</summary>
    public uint Word0 => _w0;

    public bool IsZero => (_w0 | _w1 | _w2 | _w3) == 0;

    /// <summary>Writes the 16 digest bytes.</summary>
    public void CopyTo(Span<byte> destination)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(destination, _w0);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[4..], _w1);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[8..], _w2);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[12..], _w3);
    }

    public byte[] ToArray()
    {
        var b = new byte[16];
        CopyTo(b);
        return b;
    }

    /// <summary>The 32-char lower-case form used in cache file names (<c>%08x</c> x4 over LE words).</summary>
    public string ToFileName() => $"{_w0:x8}{_w1:x8}{_w2:x8}{_w3:x8}";

    /// <summary>Plain digest hex (byte order), as stored in <c>filesignature.db</c> and <c>_deps</c> files.</summary>
    public string ToDigestHex() => Convert.ToHexStringLower(ToArray());

    /// <summary>Parses the file-name form produced by <see cref="ToFileName"/>.</summary>
    public static CacheHash ParseFileName(ReadOnlySpan<char> text)
    {
        if (!TryParseFileName(text, out var h))
            throw new FormatException($"'{text.ToString()}' is not a 32-digit cache hash.");
        return h;
    }

    public static bool TryParseFileName(ReadOnlySpan<char> text, out CacheHash hash)
    {
        hash = default;
        if (text.Length != 32)
            return false;
        Span<byte> d = stackalloc byte[16];
        for (int i = 0; i < 4; i++)
        {
            if (!uint.TryParse(text.Slice(i * 8, 8), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var w))
                return false;
            BinaryPrimitives.WriteUInt32LittleEndian(d[(i * 4)..], w);
        }
        hash = new CacheHash(d);
        return true;
    }

    public bool Equals(CacheHash other) => _w0 == other._w0 && _w1 == other._w1 && _w2 == other._w2 && _w3 == other._w3;
    public override bool Equals(object? obj) => obj is CacheHash h && Equals(h);
    public override int GetHashCode() => HashCode.Combine(_w0, _w1, _w2, _w3);
    public static bool operator ==(CacheHash a, CacheHash b) => a.Equals(b);
    public static bool operator !=(CacheHash a, CacheHash b) => !a.Equals(b);
    public override string ToString() => ToFileName();
}
