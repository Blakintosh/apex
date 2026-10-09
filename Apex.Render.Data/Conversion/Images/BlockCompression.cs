namespace Apex.Render.Data.Conversion.Images;

/// <summary>
/// Block compression as APE does it (0x1404089A0, ImageProcessing.cpp): BC4/BC5 with the statically linked real-time
/// DXT library (J.M.P. van Waveren style, "LDXT", 0x140717790/0x1407179A0, refinement level 0) and everything else with
/// the install's ispc_texcomp64r.dll (BC1/BC3: no settings; BC6H: <c>GetProfile_bc6h_fast</c>; BC7:
/// <c>GetProfile_alpha_fast</c> when the image's alphaType != 0, else <c>GetProfile_fast</c>). The ISPC calls go one
/// row of 4x4 blocks at a time (<c>rgba_surface{ptr=row, width, height=4, stride=width*bytesPerPixel}</c>), exactly
/// like APE's parallel_for body 0x14040A3F0.
/// </summary>
internal static unsafe class BlockCompression
{
    public static int BitsPerPixel(int dxgi) => dxgi is 71 or 72 or 80 ? 4 : 8;

    public static PackedLevel Compress(ToolsGfxInstall install, PackedLevel src, int dxgi, int alphaType)
    {
        int w = src.Width, h = src.Height;
        if ((w & 3) != 0 || (h & 3) != 0)
            throw new InvalidOperationException("(width % 4 == 0 && height % 4 == 0)");
        int bpp = BitsPerPixel(dxgi);
        int bw = w >> 2, bh = h >> 2;
        var dst = new byte[(long)w * h * bpp >> 3];
        int blockBytes = 2 * bpp;

        if (dxgi is 80 or 83)
        {
            var s = src.Data;
            Parallel.For(0, bw * bh, b =>
            {
                int bx = b % bw, by = b / bw;
                Span<byte> blk = stackalloc byte[64];
                blk.Clear();
                if (dxgi == 80)
                {
                    // 1-channel source; values go to the "alpha" lane (bytes 48..63).
                    for (int y = 0; y < 4; y++)
                        for (int x = 0; x < 4; x++)
                            blk[48 + 4 * y + x] = s[(4 * by + y) * w + 4 * bx + x];
                    Ldxt.EncodeBC4(blk[48..], dst.AsSpan(8 * b, 8));
                }
                else
                {
                    // 2-channel source; R -> lane 0, G -> lane 1, lanes 2/3 zero.
                    for (int y = 0; y < 4; y++)
                        for (int x = 0; x < 4; x++)
                        {
                            int si = 2 * ((4 * by + y) * w + 4 * bx + x);
                            blk[4 * y + x] = s[si];
                            blk[16 + 4 * y + x] = s[si + 1];
                        }
                    Ldxt.EncodeBC5(blk, dst.AsSpan(16 * b, 16));
                }
            });
            return new PackedLevel(w, h, bpp, dst);
        }

        var ispc = NativeLibs.GetIspc(install);
        int bytesPerPixel = src.BitsPerPixel >> 3;
        byte[] settings = dxgi == 95 ? ispc.Bc6hFast : alphaType != 0 ? ispc.Bc7AlphaFast : ispc.Bc7Fast;
        fixed (byte* sp = src.Data)
        fixed (byte* dp = dst)
        fixed (byte* setp = settings)
        {
            nint srcAddr = (nint)sp, dstAddr = (nint)dp, setAddr = (nint)setp;
            Parallel.For(0, bh, row =>
            {
                var surf = new NativeLibs.RgbaSurface
                {
                    Ptr = (byte*)srcAddr + (long)4 * row * w * bytesPerPixel,
                    Width = w,
                    Height = 4,
                    Stride = w * bytesPerPixel,
                };
                byte* outp = (byte*)dstAddr + (long)row * bw * blockBytes;
                switch (dxgi)
                {
                    case 71: case 72: ispc.BC1(&surf, outp); break;
                    case 77: case 78: ispc.BC3(&surf, outp); break;
                    case 95: ispc.BC6H(&surf, outp, (void*)setAddr); break;
                    case 98: case 99: ispc.BC7(&surf, outp, (void*)setAddr); break;
                    default: throw new NotSupportedException($"DXGI format {dxgi} is not a supported compressed format.");
                }
            });
        }
        return new PackedLevel(w, h, bpp, dst);
    }
}

/// <summary>
/// Port of the BC4/BC5 ("DXT5 alpha") block encoder linked into APE (0x140717790 / 0x1407179A0, SSE2/SSE4.1 paths
/// 0x140738AF0/0x140738A40 + 0x140737350/0x140736F70 give identical results). Refinement is disabled (APE passes 0).
/// </summary>
internal static class Ldxt
{
    /// <summary>BC4 block from 16 bytes (row-major 4x4).</summary>
    public static void EncodeBC4(ReadOnlySpan<byte> px, Span<byte> dst)
    {
        ChooseEndpoints(px, out byte a0, out byte a1);
        dst[0] = a0;
        dst[1] = a1;
        WriteIndices(dst, Indices(a0, a1, px));
    }

    /// <summary>
    /// BC5 block from the 64-byte lane buffer (R lane 0..15, G lane 16..31, lanes 32..63 zero). Faithful to the library:
    /// the endpoints come from R/G but the <b>indices of both halves are computed against lane 3 (all zero)</b>.
    /// </summary>
    public static void EncodeBC5(ReadOnlySpan<byte> lanes, Span<byte> dst)
    {
        var zero = lanes[48..64];
        ChooseEndpoints(lanes[..16], out byte r0, out byte r1);
        dst[0] = r0;
        dst[1] = r1;
        WriteIndices(dst, Indices(r0, r1, zero));
        ChooseEndpoints(lanes[16..32], out byte g0, out byte g1);
        dst[8] = g0;
        dst[9] = g1;
        WriteIndices(dst[8..], Indices(g0, g1, zero));
    }

    private static void WriteIndices(Span<byte> dst, ulong idx)
    {
        for (int i = 0; i < 6; i++)
            dst[2 + i] = (byte)(idx >> (8 * i));
    }

    /// <summary>0x140717790 head + 0x140738AF0.</summary>
    private static void ChooseEndpoints(ReadOnlySpan<byte> px, out byte a0, out byte a1)
    {
        int min = 255, max = 0, minNz = 255, max255 = 0;
        for (int i = 0; i < 16; i++)
        {
            int v = px[i];
            if (v < min) min = v;
            if (v > max) max = v;
            int vz = v == 0 ? 255 : v;
            if (vz < minNz) minNz = vz;
            int vf = v == 255 ? 0 : v;
            if (vf > max255) max255 = vf;
        }
        if ((uint)(max - min) >= 8)
        {
            uint err = Error(max, min, px);
            a0 = (byte)max;
            a1 = (byte)min;
            if (err != 0 && minNz < max255)
            {
                uint err2 = Error(minNz, max255, px);
                if (err2 < err)
                {
                    a0 = (byte)minNz;
                    a1 = (byte)max255;
                }
            }
        }
        else
        {
            a1 = (byte)min;
            a0 = (byte)max;
        }
    }

    private static void Palette(int a0, int a1, Span<int> p)
    {
        p[0] = a0;
        p[1] = a1;
        if (a0 <= a1)
        {
            p[2] = (a1 + 4 * a0) / 5;
            p[3] = (3 * a0 + 2 * a1) / 5;
            p[4] = (3 * a1 + 2 * a0) / 5;
            p[5] = (a0 + 4 * a1) / 5;
            p[6] = 0;
            p[7] = 255;
        }
        else
        {
            p[2] = (a1 + 6 * a0) / 7;
            p[3] = (5 * a0 + 2 * a1) / 7;
            p[4] = (3 * a1 + 4 * a0) / 7;
            p[5] = (3 * a0 + 4 * a1) / 7;
            p[6] = (5 * a1 + 2 * a0) / 7;
            p[7] = (a0 + 6 * a1) / 7;
        }
    }

    /// <summary>0x140737350: sum over the block of the squared distance to the nearest palette entry.</summary>
    private static uint Error(int a0, int a1, ReadOnlySpan<byte> px)
    {
        Span<int> p = stackalloc int[8];
        Palette(a0, a1, p);
        uint sum = 0;
        for (int i = 0; i < 16; i++)
        {
            int best = int.MaxValue;
            for (int k = 0; k < 8; k++)
            {
                int d = px[i] - p[k];
                d *= d;
                if (d < best) best = d;
            }
            sum += (uint)best;
        }
        return sum;
    }

    /// <summary>0x140719230: 3-bit index per texel, first strictly-smaller squared error wins (order a0,a1,p2..p7).</summary>
    private static ulong Indices(int a0, int a1, ReadOnlySpan<byte> px)
    {
        Span<int> p = stackalloc int[8];
        Palette(a0, a1, p);
        ulong bits = 0;
        for (int i = 0; i < 16; i++)
        {
            int best = 0;
            int bestErr = (a0 - px[i]) * (a0 - px[i]);
            for (int k = 1; k < 8; k++)
            {
                int d = (p[k] - px[i]) * (p[k] - px[i]);
                if (d < bestErr)
                {
                    best = k;
                    bestErr = d;
                }
            }
            bits |= (ulong)best << (3 * i);
        }
        return bits;
    }
}
