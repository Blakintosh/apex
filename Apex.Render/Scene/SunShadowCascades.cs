using System.Numerics;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Apex.Render.Scene;

/// <summary>Main view as APE's sun-shadow code sees it (<c>Camera_GetViewParms</c> 0x140338a60 output).</summary>
/// <param name="Position">World camera position (cb9 <c>wldCameraPosition</c>).</param>
/// <param name="Forward">Camera forward axis.</param>
/// <param name="Left">Camera left axis (= −right; APE's view basis is forward, left, up).</param>
/// <param name="Up">Camera up axis.</param>
/// <param name="TanHalfFovX"><c>(W/H) · tanHalfFovY</c>.</param>
/// <param name="TanHalfFovY"><c>tan(fov/2) · 0.5625</c>.</param>
public readonly record struct SunShadowCamera(Vector3 Position, Vector3 Forward, Vector3 Left, Vector3 Up, float TanHalfFovX, float TanHalfFovY);

/// <summary>Sun and lighting-state inputs of the cascade setup.</summary>
/// <param name="SunDirection">cb9 <c>sun.wldDir</c> (−AngleVectors(pitch, yaw) of the current sun; points toward the sun).</param>
/// <param name="PenumbraInches">SSI penumbra (<c>SunParameters.PenumbraInches</c>).</param>
/// <param name="SplitDistance">LED <c>volumes[0].shadowSplitDistance</c> (<c>LedVolume.ShadowSplitDistance</c>, 3000).</param>
/// <param name="SstDimensionInTiles">LED <c>lightStates[s].sst.dimensionInTiles</c> (<c>LedSunShadowTree.DimensionInTiles</c>).</param>
public readonly record struct SunShadowCascadeInputs(Vector3 SunDirection, float PenumbraInches, float SplitDistance, Vector2 SstDimensionInTiles);

/// <summary>cb9 transforms of one shadow view as <c>sub_140564580</c> builds them (camera = cascade).</summary>
public readonly record struct ShadowViewTransforms(
    Matrix4x4 WldToCam, Matrix4x4 CamToOff, Matrix4x4 OffToCam, Matrix4x4 CamToClp,
    Matrix4x4 CamToWld, Matrix4x4 WldToClp, Matrix4x4 OffToClp, Matrix4x4 ClpToCam,
    Vector3 WldCameraPosition);

/// <summary>One sun cascade (shadow struct slots i and i+3, origin at +408, texel at +492).</summary>
public sealed class SunShadowCascade
{
    /// <summary>Distance along the camera forward the cascade covers (<c>d/8, d/2, d</c>).</summary>
    public required float SplitDistance { get; init; }
    /// <summary>World inches per shadow-map texel (ratios 1:4:8).</summary>
    public required float TexelSize { get; init; }
    /// <summary>Snapped pin-space corner (min x, max y) of the 1024² map.</summary>
    public required Vector2 Origin { get; init; }
    /// <summary>Origin in SST tiles (<c>origin/(4·128)</c>, y flipped, + dims/2); not used by cb9.</summary>
    public required Vector2 SstTileOrigin { get; init; }
    /// <summary>View matrix: wldToPin with the translation reduced by (centre.xy, splitDepthOffset).</summary>
    public required Matrix4x4 View { get; init; }
    /// <summary>Reversed-Z ortho: width = height = 1024·texel, near 0, far 16384.</summary>
    public required Matrix4x4 Projection { get; init; }

    /// <summary>All cb9 transforms of this cascade's shadow view.</summary>
    public ShadowViewTransforms Transforms => SunShadowCascades.BuildViewTransforms(View, Projection);
}

/// <summary>
/// Result of <see cref="SunShadowCascades.Compute"/>: the three cascades plus the cb9 <c>CoreSunConstants</c>
/// split/SST fields, which are identical in the main and the shadow-view cb9s.
/// </summary>
public sealed class SunShadowSetup
{
    public required SunShadowCascade[] Cascades { get; init; }
    public required Vector4 SplitPinTransform0 { get; init; }
    public required Vector4 SplitPinTransform1 { get; init; }
    public required Vector4 SplitPinTransform2 { get; init; }
    /// <summary>cb9 <c>sun.splitDepthOffset</c>: <c>floor((minZ − 0.25)/4095.8125)·4095.8125</c> of the camera frustum in pin space.</summary>
    public required float SplitDepthOffset { get; init; }
    /// <summary>cb9 <c>sun.splitArrayOffset</c> (3·viewInfo+100, always 0 in the preview).</summary>
    public required int SplitArrayOffset { get; init; }
    public required Vector2 SstDimensionInTiles { get; init; }
    /// <summary>Hard-coded 4 by the preview path (<c>sub_14024DE30</c>); not the LED value.</summary>
    public required float SstInchesPerTexel { get; init; }
    /// <summary>Hard-coded 16384 by the preview path; not the LED value.</summary>
    public required float SstSpanInInches { get; init; }
    /// <summary>Camera-independent world-to-pin transform (sun basis, origin −8192·d).</summary>
    public required Matrix4x4 WldToPin { get; init; }
    public required float SstCoordScale { get; init; }
    public required uint SstRootOffset { get; init; }

    /// <summary>cb9 <c>sstLightingConstants.offToPinTransform</c> = T(camPos) · wldToPin (APE's op order).</summary>
    public Matrix4x4 OffToPin(Vector3 cameraPosition) =>
        SunShadowCascades.Mul(SunShadowCascades.Translation(cameraPosition), WldToPin);
}

/// <summary>
/// APE sun-shadow cascades (asseteditor_modtools: preview path <c>sub_14060D8F0</c> → <c>sub_14024DE30</c> →
/// <c>sub_140253CE0</c>; cb9 via <c>Gfx_BuildCoreSunConstants</c> 0x14024ea00). Float operation order follows the
/// binary. Row-vector convention, row-major <see cref="Matrix4x4"/> (M11..M14 = row 0).
/// </summary>
public static class SunShadowCascades
{
    public const int MapResolution = 1024;          // 32 · viewInfo+96 (=32)
    public const float PreviewSstInchesPerTexel = 4f;
    public const float PreviewSstSpanInInches = 16384f;
    public const float ShadowFar = 16384f;

    /// <summary>cb9 fields of a shadow view that are not transforms (same for all three cascades).</summary>
    public const uint ShadowRenderTargetSize = 1024;
    public const float ShadowNearClip = -0f;
    public const float ShadowFarClipScale = 1f / 16384f;

    private static readonly float KA = BitConverter.Int32BitsToSingle(0x3F595B5F); // 0.84905046
    private static readonly float KB = BitConverter.Int32BitsToSingle(0x3EF486CB); // 0.47759089

    public static SunShadowSetup Compute(in SunShadowCamera camera, in SunShadowCascadeInputs sun)
    {
        // ---- sub_140253900: split distances and texel sizes ----
        float ka = KA, kb = KB; // not const: the binary evaluates sqrtf(ka² + kb²) at run time in float
        float res = MapResolution;
        float s = MathF.Sqrt(ka * ka + kb * kb);
        float split = sun.SplitDistance;
        float sSplit = s * split;
        float diff = split * ka - sSplit;
        float absZ = MathF.Abs(sun.SunDirection.Z);
        float far = absZ * diff * 2f + sSplit * 2f;
        float q = far / res;
        float step = q * 4f;
        float c = MathF.Ceiling((q * 32f + far) / step);
        float texel2 = c * step / res;
        float[] splits = [far * 0.125f, far * 0.5f, far];
        float[] texels = [texel2 * 0.125f, texel2 * 0.5f, texel2];

        // ---- sub_14024DE30: pin basis (d = −sunDir) ----
        var d = -sun.SunDirection;
        var upRef = MathF.Abs(d.Z) <= 0.1f ? new Vector3(0, 0, 1) : new Vector3(1, 0, 0);
        var yAxis = Normalize(Cross(d, upRef));
        var xAxis = Normalize(Cross(yAxis, d));
        var origin = new Vector3(-0.5f * (d.X * 16384f), d.Y * 16384f * -0.5f, d.Z * 16384f * -0.5f);
        var wldToPin = LookAt(origin, d, xAxis, yAxis);

        // ---- sub_140253CE0 ----
        float fovDiag = texel2 * res * 1.4142135f;
        var view = Mul(LookAt(camera.Position, camera.Forward, camera.Left, camera.Up), Matrix4x4.Identity);
        var pinToView = Mul(Inverse(wldToPin), view);

        MinMaxZ(pinToView, camera, fovDiag, out float minZ, out float maxZ);
        float far2 = 12287.75f / (maxZ - minZ) * fovDiag;
        MinMaxZ(pinToView, camera, far2, out float minZ2, out _);
        float depthOffset = MathF.Floor((minZ2 - 0.25000381f) / 4095.8125f) * 4095.8125f;

        var pos = camera.Position;
        var fwd = camera.Forward;
        var p0 = TransformPoint(new Vector3(pos.X + fwd.X * 0f, pos.Y + fwd.Y * 0f, pos.Z + fwd.Z * 0f), wldToPin);
        var p1 = TransformPoint(new Vector3(pos.X + fwd.X * 1f, pos.Y + fwd.Y * 1f, pos.Z + fwd.Z * 1f), wldToPin);
        float fwdPinX = p1.X - p0.X, fwdPinY = p1.Y - p0.Y;
        float signX = fwdPinX >= 0f ? -1f : 1f;
        float signY = fwdPinY >= 0f ? -1f : 1f;

        float tileScale = 1f / (PreviewSstInchesPerTexel * 128f);
        var cascades = new SunShadowCascade[3];
        for (int i = 0; i < 3; i++)
        {
            float texel = texels[i];
            float block = texel * 32f;
            float size = texel * res;
            float half = size * 0.5f;
            float t = (splits[i] + 1f) * 0.5f;
            var pc = TransformPoint(new Vector3(pos.X + fwd.X * t, pos.Y + fwd.Y * t, pos.Z + fwd.Z * t), wldToPin);
            float gx = (pc.X - half) / block + signX + 0.5f;
            float gy = (pc.Y + half) / block + signY + 0.5f;
            float ox = (gx - gx % 1f) * block;
            float oy = (gy - gy % 1f) * block;
            float cx = ox + half, cy = oy - half;

            var v = wldToPin;
            v.M41 -= cx;
            v.M42 -= cy;
            v.M43 -= depthOffset;

            cascades[i] = new SunShadowCascade
            {
                SplitDistance = splits[i],
                TexelSize = texel,
                Origin = new Vector2(ox, oy),
                SstTileOrigin = new Vector2(ox * tileScale + sun.SstDimensionInTiles.X * 0.5f,
                                            oy * -tileScale + sun.SstDimensionInTiles.Y * 0.5f),
                View = v,
                Projection = Ortho(size, size, 0f, ShadowFar),
            };
        }

        // ---- Gfx_BuildCoreSunConstants ----
        var pins = new Vector4[3];
        float r = MapResolution;
        float halfRes = r * 0.5f;
        float invRes = 1f / r;
        for (int i = 0; i < 3; i++)
        {
            float texel = cascades[i].TexelSize;
            float ext = halfRes * texel;
            float pen = sun.PenumbraInches / texel;
            if (1f - pen >= 0f)
                pen = 1f;
            pins[i] = new Vector4(ext + cascades[i].Origin.X, cascades[i].Origin.Y - ext, 1f / ext, invRes * pen);
        }

        return new SunShadowSetup
        {
            Cascades = cascades,
            SplitPinTransform0 = pins[0],
            SplitPinTransform1 = pins[1],
            SplitPinTransform2 = pins[2],
            SplitDepthOffset = depthOffset,
            SplitArrayOffset = 0,
            SstDimensionInTiles = sun.SstDimensionInTiles,
            SstInchesPerTexel = PreviewSstInchesPerTexel,
            SstSpanInInches = PreviewSstSpanInInches,
            WldToPin = wldToPin,
            SstCoordScale = 1f,
            SstRootOffset = 0,
        };
    }

    /// <summary>cb9 transforms for a view/projection pair (<c>sub_140564580</c>; off space = world − camPos).</summary>
    public static ShadowViewTransforms BuildViewTransforms(Matrix4x4 wldToCam, Matrix4x4 camToClp)
    {
        var camToWld = Inverse(wldToCam);
        var cp = new Vector3(camToWld.M41, camToWld.M42, camToWld.M43);
        var toOff = Translation(new Vector3(-cp.X, -cp.Y, -cp.Z));
        var fromOff = Translation(new Vector3(-(-cp.X), -(-cp.Y), -(-cp.Z)));
        var wldToClp = Mul(wldToCam, camToClp);
        return new ShadowViewTransforms(
            wldToCam,
            Mul(camToWld, toOff),
            Mul(fromOff, wldToCam),
            camToClp,
            camToWld,
            wldToClp,
            Mul(fromOff, wldToClp),
            Inverse(camToClp),
            cp);
    }

    // ---------------------------------------------------------------- helpers (binary op order)

    /// <summary>com_vector.h cross (<c>sub_140264DB0</c>).</summary>
    private static Vector3 Cross(Vector3 a, Vector3 b) => new(
        b.Z * a.Y - a.Z * b.Y,
        a.Z * b.X - a.X * b.Z,
        a.X * b.Y - b.X * a.Y);

    private static Vector3 Normalize(Vector3 v)
    {
        float l2 = v.X * v.X + v.Y * v.Y + v.Z * v.Z;
        float inv = l2 <= 0f ? 0f : 1f / MathF.Sqrt(l2);
        return new Vector3(inv * v.X, v.Y * inv, v.Z * inv);
    }

    /// <summary>
    /// <c>sub_1402B3DD0</c>: view matrix from origin and axes (forward, left, up) using the exact 3×3 inverse
    /// (<c>sub_1402BD510</c>); columns = (−left', up', forward').
    /// </summary>
    internal static Matrix4x4 LookAt(Vector3 o, Vector3 ax0, Vector3 ax1, Vector3 ax2)
    {
        float a0 = ax0.X, a1 = ax0.Y, a2 = ax0.Z, a3 = ax1.X, a4 = ax1.Y, a5 = ax1.Z, a6 = ax2.X, a7 = ax2.Y, a8 = ax2.Z;
        float det = (a4 * a8 - a5 * a7) * a0 - (a1 * a8 - a2 * a7) * a3 + (a1 * a5 - a2 * a4) * a6;
        float r = 1f / det;
        float w0 = (a4 * a8 - a5 * a7) * r;
        float w1 = -((a1 * a8 - a2 * a7) * r);
        float w2 = (a1 * a5 - a2 * a4) * r;
        float w3 = -((a8 * a3 - a5 * a6) * r);
        float w4 = (a0 * a8 - a2 * a6) * r;
        float w5 = -((a0 * a5 - a2 * a3) * r);
        float w6 = (a7 * a3 - a4 * a6) * r;
        float w7 = -((a0 * a7 - a1 * a6) * r);
        float w8 = (a4 * a0 - a1 * a3) * r;
        float n1 = -w1, n4 = -w4, n7 = -w7;
        var m = new Matrix4x4(
            n1, w2, w0, 0f,
            n4, w5, w3, 0f,
            n7, w8, w6, 0f,
            0f, 0f, 0f, 1f);
        m.M41 = -(n1 * o.X + n4 * o.Y + n7 * o.Z);
        m.M42 = -(w2 * o.X + w5 * o.Y + w8 * o.Z);
        m.M43 = -(w0 * o.X + w3 * o.Y + w6 * o.Z);
        return m;
    }

    /// <summary><c>sub_1402688E0</c>: reversed-Z ortho.</summary>
    private static Matrix4x4 Ortho(float width, float height, float near, float far)
    {
        var m = default(Matrix4x4);
        m.M44 = 1f;
        m.M22 = 2f / height;
        m.M11 = 2f / width;
        m.M43 = near / (far - near) + 1f;
        m.M33 = -1f / (far - near);
        return m;
    }

    /// <summary><c>sub_140268A60</c>: reversed-Z perspective (near clamped to ≥ 1, far ≥ near + 1).</summary>
    private static Matrix4x4 Perspective(float tanHalfX, float tanHalfY, float near, float far)
    {
        if (near < 1f) near = 1f;
        float f = far;
        if (near + 1f > far) f = near + 1f;
        var m = default(Matrix4x4);
        m.M34 = 1f;
        m.M22 = 1f / tanHalfY;
        m.M11 = 1f / tanHalfX;
        m.M43 = near * f / (f - near);
        m.M33 = -(near / (f - near));
        return m;
    }

    internal static Matrix4x4 Translation(Vector3 t) => new(
        1f, 0f, 0f, 0f,
        0f, 1f, 0f, 0f,
        0f, 0f, 1f, 0f,
        t.X, t.Y, t.Z, 1f);

    /// <summary><c>sub_1402B11E0</c>: a · b, row i = b3·a.w + ((b0·a.x + b1·a.y) + b2·a.z).</summary>
    internal static Matrix4x4 Mul(in Matrix4x4 a, in Matrix4x4 b)
    {
        static Vector4 Row(float x, float y, float z, float w, in Matrix4x4 b) => new(
            b.M41 * w + (b.M11 * x + b.M21 * y + b.M31 * z),
            b.M42 * w + (b.M12 * x + b.M22 * y + b.M32 * z),
            b.M43 * w + (b.M13 * x + b.M23 * y + b.M33 * z),
            b.M44 * w + (b.M14 * x + b.M24 * y + b.M34 * z));
        var r0 = Row(a.M11, a.M12, a.M13, a.M14, b);
        var r1 = Row(a.M21, a.M22, a.M23, a.M24, b);
        var r2 = Row(a.M31, a.M32, a.M33, a.M34, b);
        var r3 = Row(a.M41, a.M42, a.M43, a.M44, b);
        return new Matrix4x4(r0.X, r0.Y, r0.Z, r0.W, r1.X, r1.Y, r1.Z, r1.W, r2.X, r2.Y, r2.Z, r2.W, r3.X, r3.Y, r3.Z, r3.W);
    }

    /// <summary><c>sub_1402B1F60</c> with w = 1: ((m0·x + m2·z) + (m3·w + m1·y)).</summary>
    private static Vector3 TransformPoint(Vector3 p, in Matrix4x4 m) => new(
        (m.M11 * p.X + 0f + m.M31 * p.Z) + (m.M41 * 1f + (m.M21 * p.Y + 0f)),
        (m.M12 * p.X + 0f + m.M32 * p.Z) + (m.M42 * 1f + (m.M22 * p.Y + 0f)),
        (m.M13 * p.X + 0f + m.M33 * p.Z) + (m.M43 * 1f + (m.M23 * p.Y + 0f)));

    /// <summary>Pin-space z range of the camera frustum (near 1, given far): planes + 3-plane intersections.</summary>
    private static void MinMaxZ(in Matrix4x4 pinToView, in SunShadowCamera cam, float far, out float min, out float max)
    {
        var clip = Mul(pinToView, Perspective(cam.TanHalfFovX, cam.TanHalfFovY, 1f, far));
        var planes = FrustumPlanes(clip);
        ReadOnlySpan<int> idx = [4, 2, 0, 4, 2, 1, 4, 3, 0, 4, 3, 1, 5, 2, 0, 5, 2, 1, 5, 3, 0, 5, 3, 1];
        min = float.MaxValue;
        max = -float.MaxValue;
        for (int k = 0; k < 8; k++)
        {
            if (!Intersect(planes[idx[3 * k]], planes[idx[3 * k + 1]], planes[idx[3 * k + 2]], out var p))
                continue;
            if (p.Z - min < 0f) min = p.Z;
            if (max - p.Z < 0f) max = p.Z;
        }
    }

    /// <summary><c>sub_140269450</c>: left, right, top, bottom, near (w − z), far (z); n·p = d, normalised.</summary>
    private static Vector4[] FrustumPlanes(in Matrix4x4 m)
    {
        var p = new Vector4[6];
        p[0] = new(m.M11 + m.M14, m.M24 + m.M21, m.M31 + m.M34, m.M44 + m.M41);
        p[1] = new(m.M14 - m.M11, m.M24 - m.M21, m.M34 - m.M31, m.M44 - m.M41);
        p[2] = new(m.M14 - m.M12, m.M24 - m.M22, m.M34 - m.M32, m.M44 - m.M42);
        p[3] = new(m.M12 + m.M14, m.M24 + m.M22, m.M32 + m.M34, m.M44 + m.M42);
        p[4] = new(m.M14 - m.M13, m.M24 - m.M23, m.M34 - m.M33, m.M44 - m.M43);
        p[5] = new(m.M13, m.M23, m.M33, m.M43);
        for (int i = 0; i < 6; i++)
        {
            var v = p[i];
            float len = MathF.Sqrt(v.Y * v.Y + v.X * v.X + v.Z * v.Z);
            float inv = len <= 0.0000001f ? 0f : 1f / len;
            p[i] = new Vector4(v.X * -inv, v.Y * -inv, v.Z * -inv, v.W * inv);
        }
        return p;
    }

    /// <summary><c>sub_1402B5490</c>: 3-plane intersection in double.</summary>
    private static bool Intersect(Vector4 a, Vector4 b, Vector4 c, out Vector3 p)
    {
        double ax = a.X, ay = a.Y, az = a.Z, aw = a.W;
        double bx = b.X, by = b.Y, bz = b.Z, bw = b.W;
        double cx = c.X, cy = c.Y, cz = c.Z, cw = c.W;
        double v14 = cz * by - cy * bz;
        double v17 = cy * az - cz * ay;
        double det = v17 * bx + v14 * ax + (bz * ay - by * az) * cx;
        if (Math.Abs(det) < 0.001000000047497451)
        {
            p = default;
            return false;
        }
        double inv = 1.0 / det;
        p = new Vector3(
            (float)((v17 * bw + v14 * aw + (bz * ay - by * az) * cw) * inv),
            (float)(((cz * ax - cx * az) * bw + (cx * bz - cz * bx) * aw + (bx * az - bz * ax) * cw) * inv),
            (float)(((cx * ay - cy * ax) * bw + (cy * bx - cx * by) * aw + (by * ax - bx * ay) * cw) * inv));
        return true;
    }

    // ---- sub_1402B1B90: SSE 4x4 inverse (Cramer, Intel AP-928 layout), emulated lane by lane ----

    private readonly record struct F4(float X, float Y, float Z, float W)
    {
        public float this[int i] => i switch { 0 => X, 1 => Y, 2 => Z, _ => W };
        public static F4 operator *(F4 a, F4 b) => new(a.X * b.X, a.Y * b.Y, a.Z * b.Z, a.W * b.W);
        public static F4 operator +(F4 a, F4 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z, a.W + b.W);
        public static F4 operator -(F4 a, F4 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z, a.W - b.W);
    }

    private static F4 Shuf(F4 a, F4 b, int imm) => new(a[imm & 3], a[(imm >> 2) & 3], b[(imm >> 4) & 3], b[(imm >> 6) & 3]);
    private static F4 HAdd(F4 a, F4 b) => new(a.X + a.Y, a.Z + a.W, b.X + b.Y, b.Z + b.W);

    /// <summary>
    /// Emulation of <c>sub_1402B1B90</c>. The binary refines the hardware estimate <c>rcpss(det)</c> with one Newton
    /// step; this uses the same instruction when available (so it matches APE run on the same CPU vendor) and the
    /// correctly rounded reciprocal otherwise (then results can differ by ~1 ulp).
    /// </summary>
    internal static Matrix4x4 Inverse(in Matrix4x4 m)
    {
        var row0 = new F4(m.M11, m.M12, m.M13, m.M14);
        var row1 = new F4(m.M21, m.M22, m.M23, m.M24);
        var row2 = new F4(m.M31, m.M32, m.M33, m.M34);
        var row3 = new F4(m.M41, m.M42, m.M43, m.M44);
        var v6 = Shuf(row0, row1, 238);
        var v7 = Shuf(row0, row1, 68);
        var v9 = Shuf(row2, row3, 238);
        var v10 = Shuf(row2, row3, 68);
        var v11 = Shuf(v7, v10, 136);
        var v12 = Shuf(v10, v7, 221);
        var v13 = Shuf(v6, v9, 136);
        var v14 = Shuf(v9, v6, 221);
        var v15 = v14 * v13;
        var v16 = Shuf(v15, v15, 177);
        var v17 = v11 * v16;
        var v18 = v12 * v16;
        var v19 = Shuf(v16, v16, 78);
        var v20 = v11 * v19 - v17;
        var v21 = v12 * v19 - v18;
        var v22 = v13 * v12;
        var v23 = Shuf(v13, v13, 78);
        var v24 = Shuf(v22, v22, 177);
        var v25 = v11 * v24;
        var v26 = v14 * v24;
        var v27 = Shuf(v24, v24, 78);
        var v28 = (v21 + v26) - v14 * v27;
        var v67 = v11 * v27 - v25;
        var v29 = Shuf(v12, v12, 78) * v14;
        var v30 = Shuf(v29, v29, 177);
        var v31 = v23 * v30;
        var v32 = v11 * v30;
        var v33 = Shuf(v30, v30, 78);
        var v34 = (v28 + v31) - v23 * v33;
        var v35 = v11 * v33 - v32;
        var v36 = v12 * v11;
        var v37 = Shuf(v36, v36, 177);
        var v39 = v14 * v37;
        var v40 = v23 * v37;
        var v41 = Shuf(v37, v37, 78);
        var v45 = v14 * v41;
        var v46 = v23 * v11;
        var v47 = v23 * v41;
        var v50 = v14 * v11;
        var v51 = Shuf(v46, v46, 177);
        var v52 = v12 * v51;
        var v53 = Shuf(v50, v50, 177);
        var v54 = v12 * v53;
        var v55 = v23 * v53;
        var v56 = Shuf(v53, v53, 78);
        var v57 = v14 * v51;
        var v58 = Shuf(v51, v51, 78);
        var v64 = v12 * v58;
        var v59 = v34 * v11;
        var v60 = HAdd(v59, v59);
        var v61 = HAdd(v60, v60);
        float det = v61.X;
        float rcp = Sse.IsSupported ? Sse.ReciprocalScalar(Vector128.CreateScalar(det)).ToScalar() : 1f / det;
        float rr = rcp * rcp;
        float inv = (rcp + rcp) - det * rr;
        var s = new F4(inv, inv, inv, inv);
        var o0 = s * v34;
        var o1 = ((((Shuf(v20, v20, 78) - v55) + v23 * v56) + v57) - v14 * v58) * s;
        var o2 = (((v45 - (Shuf(v35, v35, 78) + v39)) + v54) - v12 * v56) * s;
        var o3 = ((((v40 - Shuf(v67, v67, 78)) - v47) - v52) + v64) * s;
        return new Matrix4x4(o0.X, o0.Y, o0.Z, o0.W, o1.X, o1.Y, o1.Z, o1.W, o2.X, o2.Y, o2.Z, o2.W, o3.X, o3.Y, o3.Z, o3.W);
    }
}
