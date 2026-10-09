namespace Apex.Render.Passes;

/// <summary>
/// SMAA precomputed lookup textures, generated at runtime instead of shipped as data. Port of the reference
/// generators from SMAA (Jorge Jimenez et al., MIT licence, github.com/iryoku/smaa, Scripts/AreaTex.py and
/// Scripts/SearchTex.py). ToolsGfx embeds the same tables (areaTex 160×560 R8G8_UNORM, searchTex 66×33 R8_UNORM);
/// both outputs here match the captured textures byte for byte.
/// <para>ToolsGfx ships an older SMAA revision than the current reference headers: searchTex is the full
/// uncropped 66×33 table holding the raw deltas 0/1/2 (not ×127, not cropped to 64×16), and the orthogonal
/// U patterns (3 and 12) are the plain sum of their two halves, without the later <c>smootharea</c> step.</para>
/// </summary>
public static class SmaaLookupTextures
{
    public const int AreaWidth = 160, AreaHeight = 560;
    public const int SearchWidth = 66, SearchHeight = 33;

    private const int SizeOrtho = 16;
    private const int SizeDiag = 20;
    private const int SamplesDiag = 30;

    private static readonly double[] SubsampleOffsetsOrtho = [0.0, -0.25, 0.25, -0.125, 0.125, -0.375, 0.375];
    private static readonly (double X, double Y)[] SubsampleOffsetsDiag =
        [(0.0, 0.0), (0.25, -0.25), (-0.25, 0.25), (0.125, -0.125), (-0.125, 0.125)];

    private static readonly (int X, int Y)[] EdgesOrtho =
    [
        (0, 0), (3, 0), (0, 3), (3, 3), (1, 0), (4, 0), (1, 3), (4, 3),
        (0, 1), (3, 1), (0, 4), (3, 4), (1, 1), (4, 1), (1, 4), (4, 4),
    ];

    private static readonly (int X, int Y)[] EdgesDiag =
    [
        (0, 0), (1, 0), (0, 2), (1, 2), (2, 0), (3, 0), (2, 2), (3, 2),
        (0, 1), (1, 1), (0, 3), (1, 3), (2, 1), (3, 1), (2, 3), (3, 3),
    ];

    private static readonly Lazy<byte[]> s_area = new(GenerateArea);
    private static readonly Lazy<byte[]> s_search = new(GenerateSearch);

    /// <summary><see cref="GenerateArea"/>, computed once per process (the tables are constant).</summary>
    public static ReadOnlyMemory<byte> Area => s_area.Value;

    /// <summary><see cref="GenerateSearch"/>, computed once per process.</summary>
    public static ReadOnlyMemory<byte> Search => s_search.Value;

    /// <summary>areaTex: 160×560, row-major R8G8 (2 bytes per texel, 320-byte rows).</summary>
    public static byte[] GenerateArea()
    {
        var tex = new byte[AreaWidth * AreaHeight * 2];

        // Every subsample slice fills its own block of texels, so the slices run in parallel.
        int orthoSlices = SubsampleOffsetsOrtho.Length;
        Parallel.For(0, orthoSlices + SubsampleOffsetsDiag.Length, s =>
        {
            if (s < orthoSlices)
                AreaOrthoSlice(tex, s);
            else
                AreaDiagSlice(tex, s - orthoSlices);
        });
        return tex;
    }

    private static void AreaOrthoSlice(byte[] tex, int slice)
    {
        double offset = SubsampleOffsetsOrtho[slice];
        int baseY = 5 * SizeOrtho * slice;
        for (int pattern = 0; pattern < 16; pattern++)
        {
            var e = EdgesOrtho[pattern];
            for (int left = 0; left < SizeOrtho; left++)
            for (int right = 0; right < SizeOrtho; right++)
            {
                // Distances are stored quadratically compressed.
                var a = AreaOrtho(pattern, left * left, right * right, offset);
                Put(tex, SizeOrtho * e.X + left, baseY + SizeOrtho * e.Y + right, a);
            }
        }
    }

    private static void AreaDiagSlice(byte[] tex, int slice)
    {
        var offset = SubsampleOffsetsDiag[slice];
        int baseX = 5 * SizeOrtho, baseY = 4 * SizeDiag * slice;
        for (int pattern = 0; pattern < 16; pattern++)
        {
            var e = EdgesDiag[pattern];
            for (int left = 0; left < SizeDiag; left++)
            for (int right = 0; right < SizeDiag; right++)
            {
                var a = AreaDiag(pattern, left, right, offset);
                Put(tex, baseX + SizeDiag * e.X + left, baseY + SizeDiag * e.Y + right, a);
            }
        }
    }

    /// <summary>searchTex: 66×33 R8, row-major. Left half = delta for left searches, right half = right searches.</summary>
    public static byte[] GenerateSearch()
    {
        // Reverse lookup of the bilinear fetch at (-0.25, -0.125) over the 4 edge bits.
        var edge = new Dictionary<double, int[]>();
        for (int a = 0; a <= 1; a++)
        for (int b = 0; b <= 1; b++)
        for (int c = 0; c <= 1; c++)
        for (int d = 0; d <= 1; d++)
            edge[Bilinear(a, b, c, d)] = [a, b, c, d];

        var tex = new byte[SearchWidth * SearchHeight];
        for (int x = 0; x < 33; x++)
        for (int y = 0; y < 33; y++)
        {
            if (!edge.TryGetValue(0.03125 * x, out var left) || !edge.TryGetValue(0.03125 * y, out var top))
                continue;
            tex[y * SearchWidth + x] = (byte)DeltaLeft(left, top);
            tex[y * SearchWidth + 33 + x] = (byte)DeltaRight(left, top);
        }
        return tex;
    }

    private static double Lerp(double a, double b, double p) => a + (b - a) * p;

    private static double Bilinear(double e0, double e1, double e2, double e3)
    {
        double a = Lerp(e0, e1, 1.0 - 0.25);
        double b = Lerp(e2, e3, 1.0 - 0.25);
        return Lerp(a, b, 1.0 - 0.125);
    }

    private static int DeltaLeft(int[] left, int[] top)
    {
        int d = 0;
        if (top[3] == 1) d++;
        if (d == 1 && top[2] == 1 && left[1] != 1 && left[3] != 1) d++;
        return d;
    }

    private static int DeltaRight(int[] left, int[] top)
    {
        int d = 0;
        if (top[3] == 1 && left[1] != 1 && left[3] != 1) d++;
        if (d == 1 && top[2] == 1 && left[0] != 1 && left[2] != 1) d++;
        return d;
    }

    private static void Put(byte[] tex, int x, int y, (double R, double G) a)
    {
        int i = (y * AreaWidth + x) * 2;
        tex[i] = (byte)(int)(255.0 * a.R);     // Python int(): truncation
        tex[i + 1] = (byte)(int)(255.0 * a.G);
    }

    // ---- Orthogonal areas -------------------------------------------------------------------------------------

    // Area under the line p1->p2 for the pixel x..x+1.
    private static (double, double) AreaLine(double p1x, double p1y, double p2x, double p2y, int x)
    {
        double dx = p2x - p1x, dy = p2y - p1y;
        double x1 = x;
        double x2 = x + 1.0;
        double y1 = p1y + dy * (x1 - p1x) / dx;
        double y2 = p1y + dy * (x2 - p1x) / dx;

        bool inside = (x1 >= p1x && x1 < p2x) || (x2 > p1x && x2 <= p2x);
        if (!inside)
            return (0.0, 0.0);

        bool isTrapezoid = Math.CopySign(1.0, y1) == Math.CopySign(1.0, y2) || Math.Abs(y1) < 1e-4 || Math.Abs(y2) < 1e-4;
        if (isTrapezoid)
        {
            double a = (y1 + y2) / 2.0;
            return a < 0.0 ? (Math.Abs(a), 0.0) : (0.0, Math.Abs(a));
        }

        // Two triangles.
        double xc = -p1y * dx / dy + p1x;
        double frac = xc - Math.Truncate(xc); // math.modf(x)[0]
        double a1 = xc > p1x ? y1 * frac / 2.0 : 0.0;
        double a2 = xc < p2x ? y2 * (1.0 - frac) / 2.0 : 0.0;
        double aa = Math.Abs(a1) > Math.Abs(a2) ? a1 : -a2;
        return aa < 0.0 ? (Math.Abs(a1), Math.Abs(a2)) : (Math.Abs(a2), Math.Abs(a1));
    }

    private static (double, double) Avg((double X, double Y) a, (double X, double Y) b) =>
        ((a.X + b.X) / 2.0, (a.Y + b.Y) / 2.0);

    private static (double, double) Add((double X, double Y) a, (double X, double Y) b) => (a.X + b.X, a.Y + b.Y);

    private static (double, double) AreaOrtho(int pattern, int left, int right, double offset)
    {
        double d = left + right + 1;
        double o1 = 0.5 + offset;
        double o2 = 0.5 + offset - 1.0;
        double h = d / 2.0;

        switch (pattern)
        {
            case 1: return left <= right ? AreaLine(0.0, o2, h, 0.0, left) : (0.0, 0.0);
            case 2: return left >= right ? AreaLine(h, 0.0, d, o2, left) : (0.0, 0.0);
            case 3: return Add(AreaLine(0.0, o2, h, 0.0, left), AreaLine(h, 0.0, d, o2, left));
            case 4: return left <= right ? AreaLine(0.0, o1, h, 0.0, left) : (0.0, 0.0);
            case 6:
                if (Math.Abs(offset) > 0.0)
                    return Avg(AreaLine(0.0, o1, d, o2, left),
                               Add(AreaLine(0.0, o1, h, 0.0, left), AreaLine(h, 0.0, d, o2, left)));
                return AreaLine(0.0, o1, d, o2, left);
            case 7: return AreaLine(0.0, o1, d, o2, left);
            case 8: return left >= right ? AreaLine(h, 0.0, d, o1, left) : (0.0, 0.0);
            case 9:
                if (Math.Abs(offset) > 0.0)
                    return Avg(AreaLine(0.0, o2, d, o1, left),
                               Add(AreaLine(0.0, o2, h, 0.0, left), AreaLine(h, 0.0, d, o1, left)));
                return AreaLine(0.0, o2, d, o1, left);
            case 11: return AreaLine(0.0, o2, d, o1, left);
            case 12: return Add(AreaLine(0.0, o1, h, 0.0, left), AreaLine(h, 0.0, d, o1, left));
            case 13: return AreaLine(0.0, o2, d, o1, left);
            case 14: return AreaLine(0.0, o1, d, o2, left);
            default: return (0.0, 0.0); // 0, 5, 10, 15
        }
    }

    // ---- Diagonal areas (brute-force sampled) -----------------------------------------------------------------

    // Fraction of the SamplesDiag² samples of pixel (px, py) lying on the positive side of p1->p2.
    private static double Area1(double p1x, double p1y, double p2x, double p2y, double px, double py)
    {
        if (p1x == p2x && p1y == p2y)
            return 1.0; // inside() always true: count/count

        double xm = (p1x + p2x) / 2.0, ym = (p1y + p2y) / 2.0;
        double a = p2y - p1y;
        double b = p1x - p2x;
        int count = 0;
        for (int x = 0; x < SamplesDiag; x++)
        for (int y = 0; y < SamplesDiag; y++)
        {
            double sx = px + x / (double)(SamplesDiag - 1);
            double sy = py + y / (double)(SamplesDiag - 1);
            double c = a * (sx - xm) + b * (sy - ym);
            if (c > 0) count++;
        }
        return count / (double)(SamplesDiag * SamplesDiag);
    }

    private static (double, double) AreaDiagLine(int pattern, (double X, double Y) p1, (double X, double Y) p2,
                                                 int left, (double X, double Y) offset)
    {
        var e = EdgesDiag[pattern];
        if (e.X > 0) p1 = (p1.X + offset.X, p1.Y + offset.Y);
        if (e.Y > 0) p2 = (p2.X + offset.X, p2.Y + offset.Y);
        double a1 = Area1(p1.X, p1.Y, p2.X, p2.Y, 1.0 + left, 0.0 + left);
        double a2 = Area1(p1.X, p1.Y, p2.X, p2.Y, 1.0 + left, 1.0 + left);
        return (1.0 - a1, a2);
    }

    private static (double, double) AreaDiag(int pattern, int left, int right, (double X, double Y) offset)
    {
        double d = left + right + 1;
        (double, double) A(double ax, double ay, double bx, double by) =>
            AreaDiagLine(pattern, (ax, ay), (bx + d, by + d), left, offset);

        return pattern switch
        {
            0 => Avg(A(1, 1, 1, 1), A(1, 0, 1, 0)),
            1 => Avg(A(1, 0, 0, 0), A(1, 0, 1, 0)),
            2 => Avg(A(0, 0, 1, 0), A(1, 0, 1, 0)),
            3 => A(1, 0, 1, 0),
            4 => Avg(A(1, 1, 0, 0), A(1, 1, 1, 0)),
            5 => Avg(A(1, 1, 0, 0), A(1, 0, 1, 0)),
            6 => A(1, 1, 1, 0),
            7 => Avg(A(1, 1, 1, 0), A(1, 0, 1, 0)),
            8 => Avg(A(0, 0, 1, 1), A(1, 0, 1, 1)),
            9 => A(1, 0, 1, 1),
            10 => Avg(A(0, 0, 1, 1), A(1, 0, 1, 0)),
            11 => Avg(A(1, 0, 1, 1), A(1, 0, 1, 0)),
            12 => A(1, 1, 1, 1),
            13 => Avg(A(1, 1, 1, 1), A(1, 0, 1, 1)),
            14 => Avg(A(1, 1, 1, 1), A(1, 1, 1, 0)),
            _ => Avg(A(1, 1, 1, 1), A(1, 0, 1, 0)), // 15
        };
    }
}
