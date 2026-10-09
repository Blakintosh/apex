using System.Numerics;

namespace Apex.Render.Scene;

/// <summary>Main preview view: camera axes, the 8 cb9 transforms and the view-dependent cb9 scalars.</summary>
public sealed class PreviewView
{
    /// <summary>Raw camera origin (the value APE's camera stores; cb9 <c>wldCameraPosition</c> is recomputed from the view).</summary>
    public required Vector3 Origin { get; init; }
    public required Vector3 Forward { get; init; }
    public required Vector3 Right { get; init; }
    public required Vector3 Up { get; init; }
    public required float TanHalfFovX { get; init; }
    public required float TanHalfFovY { get; init; }
    /// <summary>cb9 transforms 0..511 and <c>wldCameraPosition</c> (= camToWld row 3).</summary>
    public required ShadowViewTransforms Transforms { get; init; }
    /// <summary>cb9 <c>nearClip</c> (1).</summary>
    public required float NearClip { get; init; }
    /// <summary>cb9 <c>farClipScale</c> (−0 for the infinite projection).</summary>
    public required float FarClipScale { get; init; }
    /// <summary>cb9 <c>viewSpaceScaleBias</c> (scene-desc constants, not the camera: (3.5555556, −2, 1.7777778, −1)).</summary>
    public required Vector4 ViewSpaceScaleBias { get; init; }

    /// <summary>Input for <see cref="SunShadowCascades.Compute"/> (raw origin and axes, as APE passes them).</summary>
    public SunShadowCamera ShadowCamera => new(Origin, Forward, -Right, Up, TanHalfFovX, TanHalfFovY);
}

/// <summary>
/// APE preview view math with the binary's float operation order:
/// <c>ToolsGfx_PreviewRenderFrame</c> 0x140145740 → camera update 0x1403373e0 (AngleVectors 0x1402bf4c0),
/// <c>Camera_GetViewParms</c> 0x140338a60, view 0x1403681b0 (look-at × Z-roll), <c>Gfx_SetPerspectiveReversedInf</c>
/// 0x140368150, <c>Gfx_SetViewParms</c> 0x14055e510 → 0x140564580, near/far 0x140605d80, <c>Gfx_BeginScene</c> 0x14055ec30.
/// </summary>
public static class PreviewViewMath
{
    private static readonly float Deg2Rad = BitConverter.Int32BitsToSingle(0x3C8EFA35);   // 0.017453292
    private static readonly float InvLn2 = BitConverter.Int32BitsToSingle(0x3FB8AA3B);    // 1.442695

    /// <param name="position">Raw camera origin (not a recomputed cb9 <c>wldCameraPosition</c>).</param>
    /// <param name="pitchDeg">AngleVectors pitch, positive looks down (APE stores −22.5 and negates it: 22.5).</param>
    /// <param name="yawDeg">Yaw in degrees.</param>
    /// <param name="rollDeg">Roll in degrees (0 in the preview).</param>
    /// <param name="fovDeg">"CoD FOV" (65).</param>
    /// <param name="width">Viewport width (camera +0).</param>
    /// <param name="height">Viewport height (camera +4).</param>
    public static PreviewView Build(Vector3 position, float pitchDeg, float yawDeg, float rollDeg, float fovDeg, int width, int height)
    {
        AngleVectors(pitchDeg, yawDeg, rollDeg, out var forward, out var right, out var up);

        // Camera_GetViewParms
        float tanY = MathF.Tan(fovDeg * Deg2Rad * 0.5f) * 0.5625f;
        float tanX = (float)width / (float)height * tanY;
        var left = new Vector3(-right.X, -right.Y, -right.Z);

        // 0x1403681b0: look-at(origin, forward, left, up) × Z-roll(0)
        float sr = MathF.Sin(0f), cr = MathF.Cos(0f);
        var roll = Matrix4x4.Identity;
        roll.M21 = sr;
        roll.M11 = cr;
        roll.M22 = cr;
        roll.M12 = -sr;
        var view = SunShadowCascades.Mul(SunShadowCascades.LookAt(position, forward, left, up), roll);

        // Gfx_SetPerspectiveReversedInf(tanX, tanY)
        var proj = default(Matrix4x4);
        proj.M34 = 1f;
        proj.M43 = 1f;
        proj.M22 = 1f / tanY;
        proj.M11 = 1f / tanX;

        NearFar(proj, out float near, out float farScale);

        // Gfx_BeginScene: viewSpaceScaleBias from scene desc +400 (1.0) / +404 (1.7777778) set by sub_14055e3a0
        float d400 = 1f, d404 = BitConverter.Int32BitsToSingle(0x3FE38E39);
        float v11 = 1f / d400;
        var vssb = new Vector4(d404 / v11 * 2f, 1f / v11 * -2f, d404 / v11, 1f / v11 * -1f);

        return new PreviewView
        {
            Origin = position,
            Forward = forward,
            Right = right,
            Up = up,
            TanHalfFovX = tanX,
            TanHalfFovY = tanY,
            Transforms = SunShadowCascades.BuildViewTransforms(view, proj),
            NearClip = near,
            FarClipScale = farScale,
            ViewSpaceScaleBias = vssb,
        };
    }

    /// <summary>BO3 <c>AngleVectors</c> (0x1402bf4c0).</summary>
    public static void AngleVectors(float pitchDeg, float yawDeg, float rollDeg, out Vector3 forward, out Vector3 right, out Vector3 up)
    {
        float ay = yawDeg * Deg2Rad;
        float sy = MathF.Sin(ay), cy = MathF.Cos(ay);
        float ap = pitchDeg * Deg2Rad;
        float sp = MathF.Sin(ap), cp = MathF.Cos(ap);
        float ar = rollDeg * Deg2Rad;
        float sr = MathF.Sin(ar), cr = MathF.Cos(ar);
        forward = new Vector3(cp * cy, cp * sy, -sp);
        float srsp = sr * sp;
        right = new Vector3(cr * sy - srsp * cy, cr * -1f * cy - srsp * sy, sr * -1f * cp);
        float crsp = cr * sp;
        up = new Vector3(crsp * cy + sr * sy, crsp * sy - sr * cy, cr * cp);
    }

    /// <summary>cb9 <c>nearClip</c>/<c>farClipScale</c> from camToClp (0x140605d80).</summary>
    public static void NearFar(in Matrix4x4 camToClp, out float nearClip, out float farClipScale)
    {
        float m22 = camToClp.M33, m32 = camToClp.M43;
        float v0, v1;
        if (camToClp.M14 == 0f && camToClp.M24 == 0f && camToClp.M34 == 0f)
        {
            v0 = (1f - m32) * m22;
            v1 = v0 - 1f / m22;
        }
        else
        {
            v1 = -1f / m22 * m32;
            v0 = 1f / (1f - m22) * m32;
        }
        nearClip = v0;
        farClipScale = 1f / (v1 - v0);
    }

    /// <summary>
    /// cb9 <c>exposure</c> (ToolsGfx_PreviewRenderFrame @0x140145d5a):
    /// <c>powf(2, max(evMin, min(evMax, logf(P)·1.442695 + 3 + 2)) − 3)</c>.
    /// <c>invExposure = 1/exposure</c>; <c>exposureClamped = exposure</c> (scene-desc override ≤ 0).
    /// </summary>
    public static float Exposure(float probeExposure, float evMin, float evMax)
    {
        float x = MathF.Log(probeExposure) * InvLn2 + 3f + 2f;
        if (!(evMax > x)) x = evMax;
        float y = evMin > x ? evMin : x;
        return MathF.Pow(2f, y - 3f);
    }

    public static float InvExposure(float exposure) => 1f / exposure;

    /// <summary>cb9 <c>skyRotation</c> (Gfx_BeginScene): angle = atan2f(sun.y, sun.x) + scene-desc sky offset (0 in the preview).</summary>
    public static Vector2 SkyRotation(Vector3 sunWorldDirection, float skyOffsetRadians = 0f)
    {
        float a = MathF.Atan2(sunWorldDirection.Y, sunWorldDirection.X) + skyOffsetRadians;
        return new Vector2(MathF.Sin(a), MathF.Cos(a));
    }
}
