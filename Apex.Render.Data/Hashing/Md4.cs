using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Apex.Render.Data.Hashing;

/// <summary>
/// Standard MD4 (RFC 1320). APE wraps it in an MD5-shaped context (<c>sub_1401F3260</c>); the two entry
/// points it uses are reproduced by <see cref="Seeded"/> (0x1401F3940) and <see cref="Chain"/> (0x1401F39B0).
/// Incremental: <see cref="Append"/> any number of spans, then <see cref="Finish"/>.
/// </summary>
public sealed class Md4
{
    private uint _a = 0x67452301, _b = 0xefcdab89, _c = 0x98badcfe, _d = 0x10325476;
    private readonly byte[] _buffer = new byte[64];
    private int _buffered;
    private long _length;

    /// <summary>Feeds more message bytes.</summary>
    public void Append(ReadOnlySpan<byte> data)
    {
        _length += data.Length;
        if (_buffered > 0)
        {
            int take = Math.Min(64 - _buffered, data.Length);
            data[..take].CopyTo(_buffer.AsSpan(_buffered));
            _buffered += take;
            data = data[take..];
            if (_buffered < 64)
                return;
            Transform(_buffer);
            _buffered = 0;
        }

        while (data.Length >= 64)
        {
            Transform(data[..64]);
            data = data[64..];
        }

        data.CopyTo(_buffer);
        _buffered = data.Length;
    }

    /// <summary>Pads, finalises and returns the 16-byte digest (four little-endian state words).</summary>
    public CacheHash Finish()
    {
        long bits = _length * 8;
        Span<byte> pad = stackalloc byte[72];
        pad.Clear();
        pad[0] = 0x80;
        int padLen = ((55 - _buffered) & 63) + 1;
        BinaryPrimitives.WriteInt64LittleEndian(pad[padLen..], bits);
        var saved = _length;
        Append(pad[..(padLen + 8)]);
        _length = saved;

        Span<byte> digest = stackalloc byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(digest, _a);
        BinaryPrimitives.WriteUInt32LittleEndian(digest[4..], _b);
        BinaryPrimitives.WriteUInt32LittleEndian(digest[8..], _c);
        BinaryPrimitives.WriteUInt32LittleEndian(digest[12..], _d);
        return new CacheHash(digest);
    }

    /// <summary>Plain MD4 of <paramref name="data"/>.</summary>
    public static CacheHash Hash(ReadOnlySpan<byte> data)
    {
        var md = new Md4();
        md.Append(data);
        return md.Finish();
    }

    /// <summary>APE <c>Gfx_Md4Seeded</c>: <c>MD4(u32le(seed) || data)</c>. The seed is always 0 in practice.</summary>
    public static CacheHash Seeded(ReadOnlySpan<byte> data, uint seed = 0)
    {
        var md = new Md4();
        Span<byte> s = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(s, seed);
        md.Append(s);
        md.Append(data);
        return md.Finish();
    }

    /// <summary>APE <c>Gfx_Md4Chain</c>: <c>MD4(previous digest || data)</c>.</summary>
    public static CacheHash Chain(CacheHash previous, ReadOnlySpan<byte> data)
    {
        var md = new Md4();
        Span<byte> p = stackalloc byte[16];
        previous.CopyTo(p);
        md.Append(p);
        md.Append(data);
        return md.Finish();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Rol(uint v, int s) => (v << s) | (v >> (32 - s));

    private void Transform(ReadOnlySpan<byte> block)
    {
        Span<uint> x = stackalloc uint[16];
        for (int i = 0; i < 16; i++)
            x[i] = BinaryPrimitives.ReadUInt32LittleEndian(block[(i * 4)..]);

        uint a = _a, b = _b, c = _c, d = _d;

        // Round 1: F(x,y,z) = (x & y) | (~x & z), shifts 3/7/11/19.
        for (int k = 0; k < 16; k += 4)
        {
            a = Rol(a + ((b & c) | (~b & d)) + x[k], 3);
            d = Rol(d + ((a & b) | (~a & c)) + x[k + 1], 7);
            c = Rol(c + ((d & a) | (~d & b)) + x[k + 2], 11);
            b = Rol(b + ((c & d) | (~c & a)) + x[k + 3], 19);
        }

        // Round 2: G(x,y,z) = majority, constant 0x5a827999, shifts 3/5/9/13.
        const uint k2 = 0x5a827999;
        for (int k = 0; k < 4; k++)
        {
            a = Rol(a + ((b & c) | (b & d) | (c & d)) + x[k] + k2, 3);
            d = Rol(d + ((a & b) | (a & c) | (b & c)) + x[k + 4] + k2, 5);
            c = Rol(c + ((d & a) | (d & b) | (a & b)) + x[k + 8] + k2, 9);
            b = Rol(b + ((c & d) | (c & a) | (d & a)) + x[k + 12] + k2, 13);
        }

        // Round 3: H(x,y,z) = x ^ y ^ z, constant 0x6ed9eba1, shifts 3/9/11/15.
        const uint k3 = 0x6ed9eba1;
        ReadOnlySpan<int> order = [0, 2, 1, 3];
        foreach (var k in order)
        {
            a = Rol(a + (b ^ c ^ d) + x[k] + k3, 3);
            d = Rol(d + (a ^ b ^ c) + x[k + 8] + k3, 9);
            c = Rol(c + (d ^ a ^ b) + x[k + 4] + k3, 11);
            b = Rol(b + (c ^ d ^ a) + x[k + 12] + k3, 15);
        }

        _a += a;
        _b += b;
        _c += c;
        _d += d;
    }
}
