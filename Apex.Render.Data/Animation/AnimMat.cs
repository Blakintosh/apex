using System.Numerics;
using System.Runtime.CompilerServices;

namespace Apex.Render.Data.Animation;

/// <summary>
/// CoD's <c>DObjAnimMat</c> (32 bytes): a bone transform as quaternion + translation + <c>transWeight</c>
/// (<c>2 / |q|²</c>, so non-unit quaternions still give a pure rotation). APE's preview evaluates animations in this
/// form (animation.md §4); the helpers below reproduce its com_math routines operation for operation so the b11 bone
/// matrices match APE's to the float.
/// </summary>
public struct AnimMat
{
    public Quaternion Quat;
    public Vector3 Trans;
    public float TransWeight;

    public AnimMat(Quaternion quat, Vector3 trans, float transWeight)
    {
        Quat = quat;
        Trans = trans;
        TransWeight = transWeight;
    }

    /// <summary>Identity rotation, zero translation, <c>transWeight</c> 2 (what APE uses for a root without root motion).</summary>
    public static AnimMat Identity => new(Quaternion.Identity, Vector3.Zero, 2f);
}

/// <summary>A row-vector 4×3 affine matrix as com_math stores it: rows 0–2 the rotated basis, row 3 the translation.</summary>
public struct AnimMatrix43
{
    public float M00, M01, M02;
    public float M10, M11, M12;
    public float M20, M21, M22;
    public float T0, T1, T2;
}

/// <summary>APE's animation math (com_math.cpp / com_math.h), in the exact operation order of the shipped binary.</summary>
public static class AnimMath
{
    /// <summary>
    /// <c>QuatSlerp</c> (0x1402B2940): shortest arc; plain lerp (no renormalisation) when |dot| &gt; 0.95.
    /// </summary>
    public static Quaternion Slerp(in Quaternion from, in Quaternion to, float frac)
    {
        float dot = ((to.Y * from.Y + from.X * to.X) + to.Z * from.Z) + to.W * from.W;
        bool neg = dot < 0f;
        if (neg)
            dot *= -1f;
        float scaleFrom, scaleTo;
        if (dot <= 0.95f)
        {
            float angle = MathF.Acos(dot);
            float s = MathF.Sin(angle);
            scaleFrom = MathF.Sin((1f - frac) * angle) / s;
            scaleTo = MathF.Sin(angle * frac) / s;
        }
        else
        {
            scaleFrom = 1f - frac;
            scaleTo = frac;
        }
        if (neg)
        {
            return new Quaternion(
                (scaleTo * to.X) * -1f + scaleFrom * from.X,
                (scaleTo * to.Y) * -1f + scaleFrom * from.Y,
                (scaleTo * to.Z) * -1f + scaleFrom * from.Z,
                (scaleTo * to.W) * -1f + scaleFrom * from.W);
        }
        return new Quaternion(
            scaleFrom * from.X + scaleTo * to.X,
            scaleTo * to.Y + scaleFrom * from.Y,
            scaleTo * to.Z + scaleFrom * from.Z,
            scaleTo * to.W + scaleFrom * from.W);
    }

    /// <summary><c>(b - a) * frac + a</c>, the lerp APE uses for translations and the extra channel.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Lerp(float a, float b, float frac) => (b - a) * frac + a;

    public static Vector3 Lerp(in Vector3 a, in Vector3 b, float frac) =>
        new(Lerp(a.X, b.X, frac), Lerp(a.Y, b.Y, frac), Lerp(a.Z, b.Z, frac));

    public static Vector4 Lerp(in Vector4 a, in Vector4 b, float frac) =>
        new(Lerp(a.X, b.X, frac), Lerp(a.Y, b.Y, frac), Lerp(a.Z, b.Z, frac), Lerp(a.W, b.W, frac));

    /// <summary>
    /// 0x14048A1D0: <c>child = parent ⊗ child</c> (Hamilton product, parent on the left: the child rotation is
    /// applied first).
    /// </summary>
    public static Quaternion MultiplyParent(in Quaternion p, in Quaternion c)
    {
        float x = ((c.X * p.W + p.X * c.W) + c.Z * p.Y) - c.Y * p.Z;
        float y = ((c.Y * p.W - c.Z * p.X) + p.Y * c.W) + p.Z * c.X;
        float w = ((p.W * c.W - c.X * p.X) - c.Y * p.Y) - p.Z * c.Z;
        float z = ((c.Z * p.W + c.Y * p.X) - p.Y * c.X) + p.Z * c.W;
        return new Quaternion(x, y, z, w);
    }

    /// <summary><c>MatToAxis</c> (0x140144170): the 3×3 rotation of an <see cref="AnimMat"/>, rows = rotated axes.</summary>
    public static void ToAxis(in AnimMat m, out AnimMatrix43 r)
    {
        float s = m.TransWeight;
        float x = m.Quat.X, y = m.Quat.Y, z = m.Quat.Z, w = m.Quat.W;
        float ys = y * s, zs = z * s, xs = x * s;
        float yz = z * ys, yy = y * ys, wy = w * ys;
        float xx = x * xs, xz = z * xs, xy = y * xs, wx = w * xs;
        float wz = w * zs, zz = z * zs;
        r = default;
        r.M00 = 1f - (zz + yy);
        r.M01 = wz + xy;
        r.M02 = xz - wy;
        r.M10 = xy - wz;
        r.M21 = yz - wx;
        r.M11 = 1f - (zz + xx);
        r.M12 = yz + wx;
        r.M20 = wy + xz;
        r.M22 = 1f - (yy + xx);
    }

    /// <summary><c>MatToInverseMatrix43</c> (0x140489CC0): inverse of the rigid transform (transposed rotation,
    /// <c>-t · Rᵀ</c>).</summary>
    public static void ToInverseMatrix(in AnimMat m, out AnimMatrix43 r)
    {
        float s = m.TransWeight;
        float x = m.Quat.X, y = m.Quat.Y, z = m.Quat.Z, w = m.Quat.W;
        float zs = z * s;
        float zz = z * zs;
        float ys = y * s;
        float xs = x * s;
        float yz = z * ys;
        float yy = y * ys;
        float zzyy = zz + yy;
        float xy = y * xs;
        float xz = z * xs;
        float xx = x * xs;
        float yyxx = yy + xx;
        float wx = w * xs;
        float wz = w * zs;
        float wy = w * ys;
        r = default;
        r.M22 = 1f - yyxx;
        float xyMwz = xy - wz;
        float wzPxy = wz + xy;
        r.M10 = wzPxy;
        r.M01 = xyMwz;
        float m11 = 1f - (zz + xx);
        r.M11 = m11;
        float wyPxz = wy + xz;
        float yzMwx = yz - wx;
        r.M12 = yzMwx;
        r.M02 = wyPxz;
        r.M00 = 1f - zzyy;
        float xzMwy = xz - wy;
        r.M20 = xzMwy;
        float yzPwx = yz + wx;
        r.M21 = yzPwx;
        float tx = m.Trans.X, ty = m.Trans.Y, tz = m.Trans.Z;
        r.T0 = -((wzPxy * ty + (1f - zzyy) * tx) + xzMwy * tz);
        r.T1 = -((m11 * ty + xyMwz * tx) + yzPwx * tz);
        r.T2 = -((yzMwx * ty + wyPxz * tx) + (1f - yyxx) * tz);
    }

    /// <summary><c>MatrixMultiply43</c> (0x1402B0F30): <c>out = a · b</c> (row vectors: <paramref name="a"/> first).</summary>
    public static void Multiply(in AnimMatrix43 a, in AnimMatrix43 b, out AnimMatrix43 o)
    {
        o.M00 = (a.M00 * b.M00 + b.M10 * a.M01) + b.M20 * a.M02;
        o.M10 = (a.M11 * b.M10 + a.M10 * b.M00) + a.M12 * b.M20;
        o.M20 = (a.M21 * b.M10 + a.M20 * b.M00) + a.M22 * b.M20;
        o.M01 = (b.M11 * a.M01 + b.M01 * a.M00) + b.M21 * a.M02;
        o.M11 = (a.M11 * b.M11 + a.M10 * b.M01) + a.M12 * b.M21;
        o.M21 = (a.M21 * b.M11 + a.M20 * b.M01) + a.M22 * b.M21;
        o.M02 = (a.M00 * b.M02 + a.M01 * b.M12) + b.M22 * a.M02;
        o.M12 = (a.M11 * b.M12 + a.M10 * b.M02) + a.M12 * b.M22;
        o.M22 = (a.M21 * b.M12 + a.M20 * b.M02) + a.M22 * b.M22;
        o.T0 = ((a.T1 * b.M10 + a.T0 * b.M00) + a.T2 * b.M20) + b.T0;
        o.T1 = ((a.T1 * b.M11 + a.T0 * b.M01) + a.T2 * b.M21) + b.T1;
        o.T2 = ((a.T1 * b.M12 + a.T0 * b.M02) + a.T2 * b.M22) + b.T2;
    }

    /// <summary>
    /// <c>StoreTransposed3x4</c> (0x1402B0200, scale 1): one b11 <c>CodeObjectBonesConst.objMatrixT</c> — row r is
    /// column r of the rotation followed by translation component r.
    /// </summary>
    public static void StoreTransposed(in AnimMatrix43 m, float tx, float ty, float tz, Span<float> dst)
    {
        dst[0] = m.M00; dst[1] = m.M10; dst[2] = m.M20; dst[3] = tx;
        dst[4] = m.M01; dst[5] = m.M11; dst[6] = m.M21; dst[7] = ty;
        dst[8] = m.M02; dst[9] = m.M12; dst[10] = m.M22; dst[11] = tz;
    }
}
