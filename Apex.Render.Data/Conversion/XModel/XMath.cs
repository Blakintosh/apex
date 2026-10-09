namespace Apex.Render.Data.Conversion.XModel;

/// <summary>
/// Float math exactly as APE's <c>com_math</c> / <c>tangentspace.cpp</c> evaluate it (SSE scalar, no FMA,
/// same operand grouping), so converted meshes round identically.
/// </summary>
internal static class XMath
{
    /// <summary><c>Vec3Normalize</c>: <c>len = sqrtf((x*x + y*y) + z*z); inv = 1 / (len &gt; 0 ? len : 1)</c>. Returns len.</summary>
    public static float Normalize(ref float x, ref float y, ref float z)
    {
        float len = MathF.Sqrt(x * x + y * y + z * z);
        float d = -len >= 0f ? 1.0f : len;
        float inv = 1.0f / d;
        x *= inv;
        y *= inv;
        z *= inv;
        return len;
    }

    /// <summary>
    /// Per-face tangent / bitangent from positions and UVs (0x14031D5C0), both normalised.
    /// </summary>
    public static void FaceTangents(float p0x, float p0y, float p0z, float p1x, float p1y, float p1z, float p2x, float p2y, float p2z,
        float u0, float v0, float u1, float v1, float u2, float v2,
        out float tx, out float ty, out float tz, out float bx, out float by, out float bz)
    {
        float dv1 = v1 - v0;
        float dv2 = v2 - v0;
        float du1 = u1 - u0;
        float du2 = u2 - u0;
        float ex1 = p1x - p0x;
        float ex2 = p2x - p0x;
        // comiss; jbe: the first branch also takes unordered (NaN, e.g. from infinite UVs).
        if (!(dv1 * du2 > dv2 * du1))
        {
            tx = ex1 * dv2 - ex2 * dv1;
            bx = ex2 * du1 - ex1 * du2;
            float ey1 = p1y - p0y, ey2 = p2y - p0y;
            ty = ey1 * dv2 - ey2 * dv1;
            by = ey2 * du1 - ey1 * du2;
            float ez1 = p1z - p0z, ez2 = p2z - p0z;
            tz = ez1 * dv2 - ez2 * dv1;
            bz = ez2 * du1 - ez1 * du2;
        }
        else
        {
            tx = ex2 * dv1 - ex1 * dv2;
            bx = ex1 * du2 - ex2 * du1;
            float ey2 = p2y - p0y, ey1 = p1y - p0y;
            ty = ey2 * dv1 - ey1 * dv2;
            by = ey1 * du2 - ey2 * du1;
            float ez1 = p1z - p0z, ez2 = p2z - p0z;
            tz = ez2 * dv1 - ez1 * dv2;
            bz = ez1 * du2 - ez2 * du1;
        }
        Normalize(ref tx, ref ty, ref tz);
        Normalize(ref bx, ref by, ref bz);
    }

    private static float Acos(float x)
    {
        if (x > -1.0f)
            return x < 1.0f ? CrtMath.Acos(x) : 3.1415927f;
        return -3.1415927f;
    }

    /// <summary>Corner angle weights of a triangle (0x14031E3D0).</summary>
    public static void CornerAngles(float p0x, float p0y, float p0z, float p1x, float p1y, float p1z, float p2x, float p2y, float p2z,
        out float w0, out float w1, out float w2)
    {
        float e0x = p0x - p1x, e0y = p0y - p1y, e0z = p0z - p1z;
        float e1y = p1y - p2y, e2y = p2y - p0y, e1z = p1z - p2z, e2z = p2z - p0z, e1x = p1x - p2x, e2x = p2x - p0x;
        Normalize(ref e0x, ref e0y, ref e0z);
        Normalize(ref e1x, ref e1y, ref e1z);
        Normalize(ref e2x, ref e2y, ref e2z);
        w0 = Acos(e2y * e0y + e2x * e0x + e2z * e0z);
        w1 = Acos(e1y * e0y + e1x * e0x + e1z * e0z);
        w2 = Acos(e2y * e1y + e2x * e1x + e2z * e1z);
    }

    /// <summary><c>Vec3Cross(v0, v1, out)</c> (0x140264DB0).</summary>
    public static void Cross(float ax, float ay, float az, float bx, float by, float bz, out float cx, out float cy, out float cz)
    {
        cx = bz * ay - az * by;
        cy = az * bx - ax * bz;
        cz = ax * by - bx * ay;
    }

    /// <summary><c>PerpendicularVector</c> (0x1402AE9C0): unit vector perpendicular to a unit vector.</summary>
    public static void Perpendicular(float sx, float sy, float sz, out float ox, out float oy, out float oz)
    {
        float xx = sx * sx, yy = sy * sy, zz = sz * sz;
        int k = xx > yy ? 1 : 0;
        float sel = k == 1 ? yy : xx;
        if (sel > zz)
            k = 2;
        float s = k == 0 ? sx : k == 1 ? sy : sz;
        float neg = -s;
        ox = sx * neg;
        oy = neg * sy;
        oz = neg * sz;
        if (k == 0) ox += 1.0f; else if (k == 1) oy += 1.0f; else oz += 1.0f;
        Normalize(ref ox, ref oy, ref oz);
    }

    /// <summary><c>AxisToQuat</c> (0x1402AFBD0): 3x3 rows to quaternion (xyzw), not sign-fixed.</summary>
    public static void AxisToQuat(ReadOnlySpan<float> m, Span<float> q)
    {
        float x, y, z, w;
        float r0x = m[5] - m[7], r0y = m[6] - m[2], r0z = m[1] - m[3], r0w = m[0] + m[4] + m[8] + 1.0f;
        float X = r0x * r0x + r0y * r0y + r0z * r0z + r0w * r0w;
        if (X >= 1.0f) { x = r0x; y = r0y; z = r0z; w = r0w; }
        else
        {
            float r1x = m[2] + m[6], r1y = m[7] + m[5], r1z = m[8] - m[4] - m[0] + 1.0f, r1w = r0z;
            X = r1x * r1x + r1y * r1y + r1z * r1z + r1w * r1w;
            if (X >= 1.0f) { x = r1x; y = r1y; z = r1z; w = r1w; }
            else
            {
                float r2x = m[0] - m[4] - m[8] + 1.0f, r2y = m[3] + m[1], r2z = r1x, r2w = r0x;
                X = r2x * r2x + r2y * r2y + r2z * r2z + r2w * r2w;
                if (X >= 1.0f) { x = r2x; y = r2y; z = r2z; w = r2w; }
                else
                {
                    float r3x = r2y, r3y = m[4] - m[0] - m[8] + 1.0f, r3z = r1y, r3w = r0y;
                    X = r3x * r3x + r3y * r3y + r3z * r3z + r3w * r3w;
                    x = r3x; y = r3y; z = r3z; w = r3w;
                }
            }
        }
        float inv = 1.0f / MathF.Sqrt(X);
        q[0] = inv * x;
        q[1] = inv * y;
        q[2] = inv * z;
        q[3] = inv * w;
    }

    /// <summary>Quaternion with w &gt;= 0 (sign flipped when w &lt; 0, 0x14067BAC0).</summary>
    public static void AxisToQuatPositiveW(ReadOnlySpan<float> m, Span<float> q)
    {
        AxisToQuat(m, q);
        if (q[3] < 0f)
        {
            q[0] *= -1.0f;
            q[1] *= -1.0f;
            q[2] *= -1.0f;
            q[3] *= -1.0f;
        }
    }

    /// <summary><c>MatrixTranspose33</c> (0x1402B13B0).</summary>
    public static void Transpose(ReadOnlySpan<float> a, Span<float> o)
    {
        o[0] = a[0]; o[1] = a[3]; o[2] = a[6];
        o[3] = a[1]; o[4] = a[4]; o[5] = a[7];
        o[6] = a[2]; o[7] = a[5]; o[8] = a[8];
    }

    /// <summary><c>MatrixMultiply33(a, b, out)</c> (0x1402B02A0).</summary>
    public static void Mul33(ReadOnlySpan<float> a, ReadOnlySpan<float> b, Span<float> o)
    {
        o[0] = a[0] * b[0] + b[3] * a[1] + b[6] * a[2];
        o[1] = b[4] * a[1] + b[1] * a[0] + b[7] * a[2];
        o[2] = b[5] * a[1] + b[2] * a[0] + b[8] * a[2];
        o[3] = a[4] * b[3] + a[3] * b[0] + a[5] * b[6];
        o[4] = a[4] * b[4] + a[3] * b[1] + a[5] * b[7];
        o[5] = a[4] * b[5] + a[3] * b[2] + a[5] * b[8];
        o[6] = b[3] * a[7] + b[0] * a[6] + b[6] * a[8];
        o[7] = b[4] * a[7] + b[1] * a[6] + b[7] * a[8];
        o[8] = b[5] * a[7] + b[2] * a[6] + b[8] * a[8];
    }

    /// <summary><c>MatrixTransformVector43(v, m4x3, out)</c> (0x1402B1FE0), <c>m[9..11]</c> = translation.</summary>
    public static void TransformPoint43(float x, float y, float z, ReadOnlySpan<float> m, out float ox, out float oy, out float oz)
    {
        ox = m[3] * y + x * m[0] + m[6] * z + m[9];
        oy = m[1] * x + m[4] * y + m[7] * z + m[10];
        oz = m[2] * x + m[5] * y + m[8] * z + m[11];
    }

    /// <summary><c>QuatMultiply(a, b, out)</c> (0x1402BE9A0).</summary>
    public static void QuatMul(ReadOnlySpan<float> a, ReadOnlySpan<float> b, Span<float> o)
    {
        o[0] = a[0] * b[3] + a[3] * b[0] + b[1] * a[2] - b[2] * a[1];
        o[1] = a[1] * b[3] - a[2] * b[0] + a[3] * b[1] + b[2] * a[0];
        o[2] = a[1] * b[0] + a[2] * b[3] - b[1] * a[0] + a[3] * b[2];
        o[3] = a[3] * b[3] - a[0] * b[0] - b[1] * a[1] - b[2] * a[2];
    }

    /// <summary><c>AnimMatToAxis</c> (0x140144170): DObjAnimMat quaternion scaled by transWeight to 3x3 rows.</summary>
    public static void AnimMatToAxis(ReadOnlySpan<float> mat, Span<float> o)
    {
        float tw = mat[7];
        float yw = mat[1] * tw;
        float qw = mat[3];
        float zyw = mat[2] * yw;
        float y = mat[1];
        float zw = mat[2] * tw;
        float yy = y * yw;
        float xw = mat[0] * tw;
        float wy = qw * yw;
        float xx = mat[0] * xw;
        float zx = mat[2] * xw;
        float yx = y * xw;
        float wx = qw * xw;
        float wz = qw * zw;
        float zz = mat[2] * zw;
        o[0] = 1.0f - (zz + yy);
        o[1] = wz + yx;
        o[2] = zx - wy;
        o[3] = yx - wz;
        o[7] = zyw - wx;
        o[4] = 1.0f - (zz + xx);
        o[5] = zyw + wx;
        o[6] = wy + zx;
        o[8] = 1.0f - (yy + xx);
    }
}
