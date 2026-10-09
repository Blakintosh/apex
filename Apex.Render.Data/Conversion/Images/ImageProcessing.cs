using System.Collections.Concurrent;

namespace Apex.Render.Data.Conversion.Images;

/// <summary>
/// Float-image operations of APE's ImageProcessing.cpp, reproduced operation-for-operation in single precision (every
/// product/sum is rounded to float in the same order as the x64 SSE code; RyuJIT never fuses into FMA).
/// </summary>
public static class ImageProcessing
{
    private static float F(uint bits) => BitConverter.UInt32BitsToSingle(bits);

    // Constants exactly as stored in the exe.
    private static readonly float SrgbLinThreshold = F(0x3D25AEE6);   // 0.04045
    private static readonly float SrgbLinScale = F(0x3D9E8391);       // 1/12.92
    private static readonly float Srgb055 = F(0x3D6147AE);            // 0.055
    private static readonly float SrgbInv1055 = F(0x3F72A76F);        // 1/1.055
    private static readonly float Srgb24 = F(0x4019999A);             // 2.4
    private static readonly float LinSrgbThreshold = F(0x3B4D2E1C);   // 0.0031308
    private static readonly float LinSrgbScale = F(0x414EB852);       // 12.92
    private static readonly float LinSrgbInvGamma = F(0x3ED55555);    // 1/2.4
    private static readonly float LinSrgb1055 = F(0x3F870A3D);        // 1.055
    private static readonly float NormalMinZ = F(0x3B008081);         // 1/510
    private static readonly float NormalEncode = F(0x3F008081);       // 128/255
    private static readonly float LumR = 0.2126f, LumG = 0.7152f, LumB = 0.0722f;
    private static readonly float OneThird = F(0x3EAAAAAB);

    // -------------------------------------------------------------------------------- per-texel passes
    // Every per-texel pass runs over texel ranges in parallel: each texel's result depends on that texel only, so the
    // output is the same whatever the split.

    /// <summary>Runs <paramref name="body"/> over sub-ranges <c>[from, to)</c> of <c>[0, count)</c> in parallel.</summary>
    internal static void ForRanges(int count, Action<int, int> body)
    {
        const int MinChunk = 1 << 14;
        if (count <= MinChunk)
        {
            body(0, count);
            return;
        }
        int chunk = Math.Max(MinChunk, count / (4 * Environment.ProcessorCount));
        Parallel.ForEach(Partitioner.Create(0, count, chunk), r => body(r.Item1, r.Item2));
    }

    // -------------------------------------------------------------------------------- semantic preparation

    /// <summary>0x140407520 (lambda 0x140409CB0): sRGB -&gt; linear on RGB.</summary>
    public static void SrgbToLinear(FloatImage img)
    {
        var d = img.Data;
        int n = img.Channels;
        ForRanges(d.Length / n, (from, to) =>
        {
            for (int i = from * n; i < to * n; i += n)
                for (int c = 0; c < 3; c++)
                {
                    float v = d[i + c];
                    d[i + c] = SrgbLinThreshold > v ? v * SrgbLinScale : NativeLibs.Powf((v + Srgb055) * SrgbInv1055, Srgb24);
                }
        });
    }

    /// <summary>0x1404078E0 (lambda 0x14040A070): linear -&gt; sRGB on RGB (4-channel images).</summary>
    public static void LinearToSrgb(FloatImage img)
    {
        var d = img.Data;
        ForRanges(d.Length / 4, (from, to) =>
        {
            for (int i = 4 * from; i < 4 * to; i += 4)
                for (int c = 0; c < 3; c++)
                {
                    float v = d[i + c];
                    d[i + c] = v >= LinSrgbThreshold ? NativeLibs.Powf(v, LinSrgbInvGamma) * LinSrgb1055 - Srgb055 : v * LinSrgbScale;
                }
        });
    }

    /// <summary>0x140407600: premultiplied alpha, <c>rgb *= a</c>.</summary>
    public static void Premultiply(FloatImage img)
    {
        var d = img.Data;
        ForRanges(d.Length / 4, (from, to) =>
        {
            for (int i = 4 * from; i < 4 * to; i += 4)
            {
                float a = d[i + 3];
                d[i] = a * d[i];
                d[i + 1] = a * d[i + 1];
                d[i + 2] = a * d[i + 2];
            }
        });
    }

    /// <summary>0x140406960 (scalar): one channel <c>(g*0.5 + r*0.25) + b*0.25</c> of the raw (non-linearised) RGB.</summary>
    public static FloatImage ToScalar(FloatImage img)
    {
        var o = FloatImage.Uninitialized(img.Width, img.Height, 1);
        var s = img.Data;
        var d = o.Data;
        ForRanges(d.Length, (from, to) =>
        {
            for (int j = from; j < to; j++)
                d[j] = s[4 * j + 1] * 0.5f + s[4 * j] * 0.25f + s[4 * j + 2] * 0.25f;
        });
        return o;
    }

    /// <summary>0x140406B80: keep the first <paramref name="channels"/> channels (dualscalar keeps RG).</summary>
    public static FloatImage KeepChannels(FloatImage img, int channels)
    {
        var o = FloatImage.Uninitialized(img.Width, img.Height, channels);
        var s = img.Data;
        var d = o.Data;
        int n = img.Channels;
        ForRanges(img.Width * img.Height, (from, to) =>
        {
            for (int t = from; t < to; t++)
                for (int c = 0; c < channels; c++)
                    d[t * channels + c] = s[t * n + c];
        });
        return o;
    }

    /// <summary>
    /// 0x140407440 (lambda 0x140409A30), normal-map preparation: <c>xyz = clamp(2v - 1, -1, 1)</c>,
    /// <c>z = max(z, 1/510)</c>, <c>a = 0</c>, then normalise (<c>len &lt;= 0 -&gt; 1</c>).
    /// </summary>
    public static void PrepareNormals(FloatImage img)
    {
        var d = img.Data;
        int n = img.Channels;
        ForRanges(d.Length / n, (from, to) => PrepareNormals(d, n, from, to));
    }

    private static void PrepareNormals(float[] d, int n, int from, int to)
    {
        for (int i = from * n; i < to * n; i += n)
        {
            float x = ClampSigned(d[i] * 2.0f - 1.0f);
            float y = ClampSigned(d[i + 1] * 2.0f - 1.0f);
            float z = ClampSigned(d[i + 2] * 2.0f - 1.0f);
            float zc = z - 1.0f < 0.0f ? z : 1.0f;
            if (NormalMinZ - z >= 0.0f)
                zc = NormalMinZ;
            z = zc;
            if (n > 3)
                d[i + 3] = 0.0f;
            float len = MathF.Sqrt(x * x + y * y + z * z);
            if (-len >= 0.0f)
                len = 1.0f;
            float r = 1.0f / len;
            d[i] = x * r;
            d[i + 1] = y * r;
            d[i + 2] = z * r;
        }

        static float ClampSigned(float v)
        {
            float r = v - 1.0f < 0.0f ? v : 1.0f;
            if (-1.0f - v >= 0.0f)
                r = -1.0f;
            return r;
        }
    }

    /// <summary>0x140407A90: xyz of the prepared normals as a 3-channel "variance" image.</summary>
    public static FloatImage NormalXyz(FloatImage img) => KeepChannels(img, 3);

    /// <summary>
    /// 0x140407C90 / second half of 0x140407E90 (lambda 0x14040A610/0x14040AA40): separable-weight 3x3 blur of a
    /// 3-channel image with kernel <c>[w, 1-2w, w]</c> (x) <c>[w, 1-2w, w]</c>; out-of-range taps wrap, or clamp when
    /// <c>clampU</c> (columns) / <c>clampV</c> (rows) is set. Taps are accumulated row-major, <c>acc += (wx*wy)*v</c>.
    /// </summary>
    public static FloatImage Blur3(FloatImage src, bool clampU, bool clampV, float w)
    {
        int W = src.Width, H = src.Height;
        var o = FloatImage.Uninitialized(W, H, 3);
        var s = src.Data;
        var d = o.Data;
        float[] kw = [w, 1.0f - w * 2.0f, w];
        Parallel.For(0, H, y =>
        {
            for (int x = 0; x < W; x++)
            {
                float a0 = 0, a1 = 0, a2 = 0;
                for (int r = 0; r < 3; r++)
                {
                    int yy = Wrap(y - 1 + r, H, clampV);
                    for (int c = 0; c < 3; c++)
                    {
                        int xx = Wrap(x - 1 + c, W, clampU);
                        float wt = kw[c] * kw[r];
                        int si = (yy * W + xx) * 3;
                        a0 = wt * s[si] + a0;
                        a1 = wt * s[si + 1] + a1;
                        a2 = wt * s[si + 2] + a2;
                    }
                }
                int di = (y * W + x) * 3;
                d[di] = a0;
                d[di + 1] = a1;
                d[di + 2] = a2;
            }
        });
        return o;

        static int Wrap(int v, int n, bool clamp)
        {
            if (clamp)
                return v < 0 ? 0 : v > n - 1 ? n - 1 : v;
            while (v < 0) v += n;
            while (v > n - 1) v -= n;
            return v;
        }
    }

    /// <summary>First half of 0x140407E90 (lambda 0x14040A880): 2x2 box of a 3-channel image,
    /// <c>(((upperLeft + upperRight) + lowerLeft) + lowerRight) * 0.25</c>.</summary>
    public static FloatImage Box2(FloatImage src)
    {
        int W = src.Width, w = W >> 1, h = src.Height >> 1;
        var o = FloatImage.Uninitialized(w, h, 3);
        var s = src.Data;
        var d = o.Data;
        Parallel.For(0, h, y =>
        {
            int up = 2 * y * W * 3, lo = (2 * y + 1) * W * 3;
            for (int x = 0; x < w; x++)
            {
                int u0 = up + 6 * x, l0 = lo + 6 * x;
                int di = (y * w + x) * 3;
                for (int c = 0; c < 3; c++)
                    d[di + c] = (s[u0 + c] + s[u0 + 3 + c] + s[l0 + c] + s[l0 + 3 + c]) * 0.25f;
            }
        });
        return o;
    }

    /// <summary>
    /// 0x140407720 (lambda 0x140409EA0), per level of a normal map: <c>t = clamp(1/|v| - 1, 0, 1/3)</c> of the blurred
    /// average normal <c>v</c>, normalise the normal, <c>R,G = clamp01(n.xy * 128/255 + 128/255)</c>,
    /// <c>B = sqrt(t * 1.0 / (1/3))</c>; alpha untouched (0).
    /// </summary>
    public static void ApplyNormalVariance(FloatImage img, FloatImage variance)
    {
        var d = img.Data;
        var v = variance.Data;
        int n = img.Channels;
        ForRanges(v.Length / 3, (from, to) => ApplyNormalVariance(d, v, n, from, to));
    }

    private static void ApplyNormalVariance(float[] d, float[] v, int n, int from, int to)
    {
        const float strength = 1.0f;
        for (int i = from * n, j = 3 * from; j < 3 * to; i += n, j += 3)
        {
            float lenV = MathF.Sqrt(v[j + 1] * v[j + 1] + v[j] * v[j] + v[j + 2] * v[j + 2]);
            float e = 1.0f / lenV - 1.0f;
            float t = e - OneThird >= 0.0f ? OneThird : e;
            if (0.0f - e >= 0.0f)
                t = 0.0f;
            float b = MathF.Sqrt(t * strength / OneThird);

            float x = d[i], y = d[i + 1], z = d[i + 2];
            float len = MathF.Sqrt(x * x + y * y + z * z);
            if (-len >= 0.0f)
                len = 1.0f;
            float r = 1.0f / len;
            x *= r;
            y = r * y;

            d[i] = Clamp01(x * NormalEncode + NormalEncode);
            d[i + 1] = Clamp01(y * NormalEncode + NormalEncode);
            d[i + 2] = b;
        }

        static float Clamp01(float v) => 1.0f > v ? (0.0f > v ? 0.0f : v) : 1.0f;
    }

    // -------------------------------------------------------------------------------- mip filter

    /// <summary>
    /// 0x1404081E0 (lambda 0x14040B060): 2x2 weighted average. Weights per mipMode: 0 avg = 1; 1 lum =
    /// <c>(g*0.7152 + r*0.2126) + b*0.0722</c>; 2 alpha / 5 punch-through = a; 3 = lum*a; 4 max = 1 for the texel whose
    /// key (lum, or channel 0 for 1-channel images) is strictly greater than the other three, else 0. Images with
    /// fewer than 4 channels use alpha = 1 (1-2 channel images replicate channel 0 into the missing RGB for the
    /// luminance). <c>out = (((w0*p00 + w1*p01) + w2*p10) + w3*p11) * inv</c>, <c>inv = sum &gt; 0 ? 1/sum : sum</c>.
    /// No edge handling (dimensions are even).
    /// </summary>
    public static FloatImage Downsample(FloatImage src, int mipMode)
    {
        int n = src.Channels, W = src.Width, w = W >> 1, h = src.Height >> 1;
        var o = FloatImage.Uninitialized(w, h, n);
        var s = src.Data;
        var d = o.Data;
        Parallel.For(0, h, y =>
        {
            Span<float> p00 = stackalloc float[5], p01 = stackalloc float[5], p10 = stackalloc float[5], p11 = stackalloc float[5];
            int r0 = 2 * y * W, r1 = (2 * y + 1) * W;
            for (int x = 0; x < w; x++)
            {
                for (int c = 0; c < n; c++)
                {
                    p00[c] = s[(r0 + 2 * x) * n + c];
                    p01[c] = s[(r0 + 2 * x + 1) * n + c];
                    p10[c] = s[(r1 + 2 * x) * n + c];
                    p11[c] = s[(r1 + 2 * x + 1) * n + c];
                }
                float w0, w1, w2, w3;
                if (n >= 4)
                {
                    w0 = p00[3]; w1 = p01[3]; w2 = p10[3]; w3 = p11[3];
                }
                else
                {
                    for (int c = n; c < 3; c++)
                    {
                        p00[c] = p00[0]; p01[c] = p01[0]; p10[c] = p10[0]; p11[c] = p11[0];
                    }
                    p00[3] = p01[3] = p10[3] = p11[3] = 1.0f;
                    w0 = w1 = w2 = w3 = 1.0f;
                }
                switch (mipMode)
                {
                    case 0:
                        w0 = w1 = w2 = w3 = 1.0f;
                        break;
                    case 1:
                        w0 = Lum(p00); w1 = Lum(p01); w2 = Lum(p10); w3 = Lum(p11);
                        break;
                    case 2:
                    case 5:
                        break;
                    case 3:
                        w0 = Lum(p00) * w0; w1 = Lum(p01) * w1; w2 = Lum(p10) * w2; w3 = Lum(p11) * w3;
                        break;
                    case 4:
                    {
                        int k = 0;
                        if (n > 1)
                        {
                            k = 4;
                            p00[4] = Lum(p00); p01[4] = Lum(p01); p10[4] = Lum(p10); p11[4] = Lum(p11);
                        }
                        float a = p00[k], b = p01[k], c = p10[k], e = p11[k];
                        w0 = a > b && a > c && a > e ? 1.0f : 0.0f;
                        w1 = b > a && b > c && b > e ? 1.0f : 0.0f;
                        w2 = c > b && c > a && c > e ? 1.0f : 0.0f;
                        w3 = e <= b || e <= c || e <= a ? 0.0f : 1.0f;
                        break;
                    }
                    default:
                        throw new ArgumentOutOfRangeException(nameof(mipMode), "Unknown mipMode");
                }
                float sum = w0 + w1 + w2 + w3;
                float inv = sum > 0.0f ? 1.0f / sum : sum;
                int di = (y * w + x) * n;
                for (int c = 0; c < n; c++)
                    d[di + c] = (w0 * p00[c] + w1 * p01[c] + w2 * p10[c] + w3 * p11[c]) * inv;
            }
        });
        return o;

        static float Lum(Span<float> p) => p[1] * LumG + p[0] * LumR + p[2] * LumB;
    }

    // -------------------------------------------------------------------------------- cube faces

    /// <summary>0x140406E10: transpose (square images).</summary>
    public static void Transpose(FloatImage img)
    {
        int n = img.Channels, w = img.Width;
        var d = img.Data;
        for (int y = 1; y < w; y++)
            for (int x = 0; x < y; x++)
                for (int c = 0; c < n; c++)
                    (d[(y * w + x) * n + c], d[(x * w + y) * n + c]) = (d[(x * w + y) * n + c], d[(y * w + x) * n + c]);
    }

    /// <summary>0x140407020: mirror each row (x -&gt; w-1-x).</summary>
    public static void FlipX(FloatImage img)
    {
        int n = img.Channels, w = img.Width, h = img.Height;
        var d = img.Data;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w / 2; x++)
                for (int c = 0; c < n; c++)
                    (d[(y * w + x) * n + c], d[(y * w + w - 1 - x) * n + c]) = (d[(y * w + w - 1 - x) * n + c], d[(y * w + x) * n + c]);
    }

    /// <summary>0x140407230: mirror each column (y -&gt; h-1-y).</summary>
    public static void FlipY(FloatImage img)
    {
        int n = img.Channels, w = img.Width, h = img.Height;
        var d = img.Data;
        for (int y = 0; y < h / 2; y++)
            for (int x = 0; x < w; x++)
                for (int c = 0; c < n; c++)
                    (d[(y * w + x) * n + c], d[((h - 1 - y) * w + x) * n + c]) = (d[((h - 1 - y) * w + x) * n + c], d[(y * w + x) * n + c]);
    }

    /// <summary>Per-face orientation fix applied right after loading a cube face (inside 0x1403ED1F0).</summary>
    public static void OrientCubeFace(FloatImage img, int face)
    {
        switch (face)
        {
            case 0: case 4: case 5: Transpose(img); break;
            case 1: Transpose(img); FlipX(img); FlipY(img); break;
            case 2: FlipY(img); break;
            case 3: FlipX(img); break;
        }
    }

    // -------------------------------------------------------------------------------- alpha type

    /// <summary>0x1404079C0: 0 = every alpha &gt;= 1; 1 = the only non-opaque texels are black with a &lt;= 0;
    /// 2 = anything else.</summary>
    public static int ComputeAlphaType(FloatImage img)
    {
        var d = img.Data;
        int result = 0;
        for (int i = 0; i < d.Length; i += 4)
        {
            float a = d[i + 3];
            if (a < 1.0f)
            {
                if (a <= 0.0f && d[i] <= 0.0f && d[i + 1] <= 0.0f && d[i + 2] <= 0.0f)
                    result = 1;
                else
                    return 2;
            }
        }
        return result;
    }

    // -------------------------------------------------------------------------------- packing

    /// <summary>0x140408370 (lambda 0x14040ACB0): <c>(int)(clamp01(v) * 255 + 0.5)</c> per channel, bpp = 8*channels.</summary>
    internal static PackedLevel ToUnorm8(FloatImage img)
    {
        var s = img.Data;
        var o = GC.AllocateUninitializedArray<byte>(s.Length);
        ForRanges(s.Length, (from, to) =>
        {
            for (int i = from; i < to; i++)
            {
                float v = s[i];
                if (v >= 1.0f) v = 1.0f;
                else if (v < 0.0f) v = 0.0f;
                o[i] = (byte)(int)(v * 255.0f + 0.5f);
            }
        });
        return new PackedLevel(img.Width, img.Height, 8 * img.Channels, o);
    }

    /// <summary>0x140408560 (lambda 0x14040ADA0): R10G10B10A2, <c>(int)(clamp01(v)*1023+0.5)</c>, alpha <c>*3</c>.</summary>
    internal static PackedLevel ToR10G10B10A2(FloatImage img)
    {
        var s = img.Data;
        int n = img.Channels;
        var o = GC.AllocateUninitializedArray<byte>(img.Width * img.Height * 4);
        ForRanges(img.Width * img.Height, (from, to) =>
        {
            for (int t = from; t < to; t++)
            {
                int i = t * n;
                uint a = (uint)(int)(C(s[i + 3]) * 3.0f + 0.5f);
                uint b = (uint)(int)(C(s[i + 2]) * 1023.0f + 0.5f);
                uint g = (uint)(int)(C(s[i + 1]) * 1023.0f + 0.5f);
                uint r = (uint)(int)(C(s[i]) * 1023.0f + 0.5f);
                uint p = (a << 30) | (b << 20) | (g << 10) | r;
                BitConverter.TryWriteBytes(o.AsSpan(4 * t), p);
            }
        });
        return new PackedLevel(img.Width, img.Height, 32, o);

        static float C(float v) => v >= 1.0f ? 1.0f : v < 0.0f ? 0.0f : v;
    }

    /// <summary>0x140408780 (lambda 0x14040AEF0): R16G16B16A16_FLOAT via APE's truncating float-&gt;half
    /// (0x140427BE0). 1 channel -&gt; (v,v,v,1), 2 -&gt; (r,g,0,1), 3 -&gt; (r,g,b,1).</summary>
    internal static PackedLevel ToHalf4(FloatImage img)
    {
        var s = img.Data;
        int n = img.Channels;
        var o = GC.AllocateUninitializedArray<byte>(img.Width * img.Height * 8);
        ForRanges(img.Width * img.Height, (from, to) =>
        {
            Span<float> p = stackalloc float[4];
            for (int t = from; t < to; t++)
            {
                int i = t * n, j = 8 * t;
                switch (n)
                {
                    case 1: p[0] = p[1] = p[2] = s[i]; p[3] = 1.0f; break;
                    case 2: p[0] = s[i]; p[1] = s[i + 1]; p[2] = 0.0f; p[3] = 1.0f; break;
                    case 3: p[0] = s[i]; p[1] = s[i + 1]; p[2] = s[i + 2]; p[3] = 1.0f; break;
                    default: p[0] = s[i]; p[1] = s[i + 1]; p[2] = s[i + 2]; p[3] = s[i + 3]; break;
                }
                for (int c = 0; c < 4; c++)
                    BitConverter.TryWriteBytes(o.AsSpan(j + 2 * c), FloatToHalfTrunc(p[c]));
            }
        });
        return new PackedLevel(img.Width, img.Height, 64, o);
    }

    /// <summary>0x140427BE0: <c>e = clamp(exp8 - 112, 0, 30)</c>; mantissa truncated (<c>bits &gt;&gt; 13</c>) and kept even
    /// when the exponent underflows; exponent overflow gives 0x3FF mantissa (65504).</summary>
    public static ushort FloatToHalfTrunc(float f)
    {
        uint v = BitConverter.SingleToUInt32Bits(f);
        int e = (int)((v >> 23) & 0xFF) - 112;
        int ec = e < 0 ? 0 : e > 30 ? 30 : e;
        uint sign = (v >> 16) & 0x8000;
        uint h = e <= 30 ? sign | ((v >> 13) & 0x3FF) | ((uint)ec << 10) : sign | ((uint)ec << 10) | 0x3FF;
        return (ushort)h;
    }
}
