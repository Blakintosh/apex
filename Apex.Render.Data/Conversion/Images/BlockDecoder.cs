namespace Apex.Render.Data.Conversion.Images;

/// <summary>
/// Reference CPU decoders for the v20 level formats (diagnostics: comparing converted levels texel-wise).
/// Returns 8-bit texels (<c>channels</c> per texel: 4 for RGBA/BC1/BC3/BC7, 2 for R8G8/BC5, 1 for R8/BC4), or null for
/// formats not decoded here (float formats, BC6H). BC7 3-subset modes (0, 2) decode as magenta — the ISPC "fast"
/// profiles APE uses never emit them.
/// </summary>
public static class BlockDecoder
{
    public static byte[]? Decode(int dxgi, int width, int height, ReadOnlySpan<byte> data)
    {
        switch (dxgi)
        {
            case 28: case 29: return data[..(width * height * 4)].ToArray();
            case 49: return data[..(width * height * 2)].ToArray();
            case 61: return data[..(width * height)].ToArray();
        }
        int bw = Math.Max(1, (width + 3) / 4), bh = Math.Max(1, (height + 3) / 4);
        int ch = dxgi switch { 71 or 72 or 77 or 78 or 98 or 99 => 4, 80 => 1, 83 => 2, _ => 0 };
        if (ch == 0)
            return null;
        int blockBytes = dxgi is 71 or 72 or 80 ? 8 : 16;
        var o = new byte[width * height * ch];
        Span<byte> blk = stackalloc byte[64];
        for (int by = 0; by < bh; by++)
            for (int bx = 0; bx < bw; bx++)
            {
                var src = data.Slice((by * bw + bx) * blockBytes, blockBytes);
                switch (dxgi)
                {
                    case 71: case 72: DecodeBC1(src, blk); break;
                    case 77: case 78: DecodeBC1(src[8..], blk); DecodeAlpha(src[..8], blk, 3, 4); break;
                    case 80: DecodeAlpha(src, blk, 0, 1); break;
                    case 83: DecodeAlpha(src[..8], blk, 0, 2); DecodeAlpha(src[8..], blk, 1, 2); break;
                    default: DecodeBC7(src, blk); break;
                }
                for (int y = 0; y < 4; y++)
                    for (int x = 0; x < 4; x++)
                    {
                        int px = bx * 4 + x, py = by * 4 + y;
                        if (px >= width || py >= height) continue;
                        for (int c = 0; c < ch; c++)
                            o[(py * width + px) * ch + c] = blk[(y * 4 + x) * ch + c];
                    }
            }
        return o;
    }

    private static void DecodeBC1(ReadOnlySpan<byte> s, Span<byte> o)
    {
        int c0 = s[0] | s[1] << 8, c1 = s[2] | s[3] << 8;
        Span<int> pal = stackalloc int[16];
        Rgb(pal, c0, 0);
        Rgb(pal, c1, 1);
        for (int k = 0; k < 3; k++)
        {
            if (c0 > c1)
            {
                pal[8 + k] = (2 * pal[k] + pal[4 + k]) / 3;
                pal[12 + k] = (pal[k] + 2 * pal[4 + k]) / 3;
            }
            else
            {
                pal[8 + k] = (pal[k] + pal[4 + k]) / 2;
                pal[12 + k] = 0;
            }
        }
        pal[11] = 255;
        pal[15] = c0 > c1 ? 255 : 0;
        uint idx = (uint)(s[4] | s[5] << 8 | s[6] << 16 | s[7] << 24);
        for (int i = 0; i < 16; i++)
        {
            int k = (int)(idx >> (2 * i) & 3);
            for (int c = 0; c < 4; c++) o[i * 4 + c] = (byte)pal[k * 4 + c];
        }

        static void Rgb(Span<int> pal, int c, int i)
        {
            pal[i * 4] = (c >> 11 & 31) * 255 / 31;
            pal[i * 4 + 1] = (c >> 5 & 63) * 255 / 63;
            pal[i * 4 + 2] = (c & 31) * 255 / 31;
            pal[i * 4 + 3] = 255;
        }
    }

    private static void DecodeAlpha(ReadOnlySpan<byte> s, Span<byte> o, int channel, int stride)
    {
        int a0 = s[0], a1 = s[1];
        Span<int> p = stackalloc int[8];
        p[0] = a0; p[1] = a1;
        if (a0 > a1) for (int k = 1; k < 7; k++) p[k + 1] = ((7 - k) * a0 + k * a1) / 7;
        else { for (int k = 1; k < 5; k++) p[k + 1] = ((5 - k) * a0 + k * a1) / 5; p[6] = 0; p[7] = 255; }
        ulong bits = 0;
        for (int i = 0; i < 6; i++) bits |= (ulong)s[2 + i] << (8 * i);
        for (int i = 0; i < 16; i++)
            o[i * stride + channel] = (byte)p[(int)(bits >> (3 * i) & 7)];
    }

    // ------------------------------------------------------------------------------------------ BC7

    private static readonly ushort[] P2 =
    [
        0xCCCC, 0x8888, 0xEEEE, 0xECC8, 0xC880, 0xFEEC, 0xFEC8, 0xEC80, 0xC800, 0xFFEC, 0xFE80, 0xE800, 0xFFE8, 0xFF00, 0xFFF0, 0xF000,
        0xF710, 0x008E, 0x7100, 0x08CE, 0x008C, 0x7310, 0x3100, 0x8CCE, 0x088C, 0x3110, 0x6666, 0x366C, 0x17E8, 0x0FF0, 0x718E, 0x399C,
        0xAAAA, 0xF0F0, 0x5A5A, 0x33CC, 0x3C3C, 0x55AA, 0x9696, 0xA55A, 0x73CE, 0x13C8, 0x324C, 0x3BDC, 0x6996, 0xC33C, 0x9966, 0x0660,
        0x0272, 0x04E4, 0x4E40, 0x2720, 0xC936, 0x936C, 0x39C6, 0x639C, 0x9336, 0x9CC6, 0x817E, 0xE718, 0xCCF0, 0x0FCC, 0x7744, 0xEE22,
    ];

    private static readonly byte[] Anchor2 =
    [
        15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15,
        15, 2, 8, 2, 2, 8, 8, 15, 2, 8, 2, 2, 8, 8, 2, 2,
        15, 15, 6, 8, 2, 8, 15, 15, 2, 8, 2, 2, 2, 15, 15, 6,
        6, 2, 6, 8, 15, 15, 2, 2, 15, 15, 15, 15, 15, 2, 2, 15,
    ];

    private static readonly int[] W2 = [0, 21, 43, 64];
    private static readonly int[] W3 = [0, 9, 18, 27, 37, 46, 55, 64];
    private static readonly int[] W4 = [0, 4, 9, 13, 17, 21, 26, 30, 34, 38, 43, 47, 51, 55, 60, 64];

    private ref struct Bits
    {
        private readonly ReadOnlySpan<byte> _s;
        private int _pos;
        public Bits(ReadOnlySpan<byte> s) { _s = s; _pos = 0; }
        public int Read(int n)
        {
            int v = 0;
            for (int i = 0; i < n; i++, _pos++)
                v |= (_s[_pos >> 3] >> (_pos & 7) & 1) << i;
            return v;
        }
    }

    private static void DecodeBC7(ReadOnlySpan<byte> s, Span<byte> o)
    {
        int mode = 0;
        while (mode < 8 && (s[0] >> mode & 1) == 0) mode++;
        if (mode is 8 or 0 or 2)
        {
            for (int i = 0; i < 16; i++) { o[i * 4] = 255; o[i * 4 + 1] = 0; o[i * 4 + 2] = 255; o[i * 4 + 3] = 255; }
            return;
        }
        // NS, PB, RB, ISB, CB, AB, EPB, SPB, IB, IB2
        ReadOnlySpan<int> info = mode switch
        {
            1 => [2, 6, 0, 0, 6, 0, 0, 1, 3, 0],
            3 => [2, 6, 0, 0, 7, 0, 1, 0, 2, 0],
            4 => [1, 0, 2, 1, 5, 6, 0, 0, 2, 3],
            5 => [1, 0, 2, 0, 7, 8, 0, 0, 2, 2],
            6 => [1, 0, 0, 0, 7, 7, 1, 0, 4, 0],
            _ => [2, 6, 0, 0, 5, 5, 1, 0, 2, 0],
        };
        int ns = info[0], cb = info[4], ab = info[5], ib = info[8], ib2 = info[9];
        var r = new Bits(s);
        r.Read(mode + 1);
        int part = r.Read(info[1]);
        int rot = r.Read(info[2]);
        int isb = r.Read(info[3]);
        Span<int> ep = stackalloc int[2 * 2 * 4]; // [subset][end][channel]
        for (int c = 0; c < 3; c++)
            for (int k = 0; k < ns * 2; k++)
                ep[k * 4 + c] = r.Read(cb);
        for (int k = 0; k < ns * 2; k++)
            ep[k * 4 + 3] = ab > 0 ? r.Read(ab) : 255;
        int cbits = cb, abits = ab;
        if (info[6] != 0)
        {
            for (int k = 0; k < ns * 2; k++)
            {
                int p = r.Read(1);
                for (int c = 0; c < 4; c++)
                    if (c < 3 || ab > 0) ep[k * 4 + c] = ep[k * 4 + c] << 1 | p;
            }
            cbits++; if (ab > 0) abits++;
        }
        else if (info[7] != 0)
        {
            for (int sub = 0; sub < ns; sub++)
            {
                int p = r.Read(1);
                for (int e = 0; e < 2; e++)
                    for (int c = 0; c < 4; c++)
                        if (c < 3 || ab > 0) ep[(sub * 2 + e) * 4 + c] = ep[(sub * 2 + e) * 4 + c] << 1 | p;
            }
            cbits++; if (ab > 0) abits++;
        }
        for (int k = 0; k < ns * 2; k++)
            for (int c = 0; c < 4; c++)
            {
                int n = c < 3 ? cbits : abits;
                if (c == 3 && ab == 0) continue;
                int v = ep[k * 4 + c] << (8 - n);
                ep[k * 4 + c] = v | v >> n;
            }
        Span<int> idx1 = stackalloc int[16], idx2 = stackalloc int[16];
        for (int i = 0; i < 16; i++)
        {
            int sub = ns == 2 ? P2[part] >> i & 1 : 0;
            bool anchor = i == 0 || (ns == 2 && sub == 1 && i == Anchor2[part]);
            idx1[i] = r.Read(anchor ? ib - 1 : ib);
        }
        if (ib2 > 0)
            for (int i = 0; i < 16; i++)
                idx2[i] = r.Read(i == 0 ? ib2 - 1 : ib2);
        for (int i = 0; i < 16; i++)
        {
            int sub = ns == 2 ? P2[part] >> i & 1 : 0;
            int ci = idx1[i], ai = idx1[i], cw = ib, aw = ib;
            if (ib2 > 0)
            {
                if (isb == 0) { ai = idx2[i]; aw = ib2; }
                else { ci = idx2[i]; cw = ib2; aw = ib; }
            }
            for (int c = 0; c < 4; c++)
            {
                bool alpha = c == 3;
                int w = Weight(alpha ? aw : cw, alpha ? ai : ci);
                int e0 = ep[(sub * 2) * 4 + c], e1 = ep[(sub * 2 + 1) * 4 + c];
                o[i * 4 + c] = (byte)(((64 - w) * e0 + w * e1 + 32) >> 6);
            }
            switch (rot)
            {
                case 1: (o[i * 4 + 3], o[i * 4]) = (o[i * 4], o[i * 4 + 3]); break;
                case 2: (o[i * 4 + 3], o[i * 4 + 1]) = (o[i * 4 + 1], o[i * 4 + 3]); break;
                case 3: (o[i * 4 + 3], o[i * 4 + 2]) = (o[i * 4 + 2], o[i * 4 + 3]); break;
            }
        }

        static int Weight(int bits, int i) => bits == 2 ? W2[i] : bits == 3 ? W3[i] : W4[i];
    }
}
