using System.Numerics;

namespace Apex.Render.Scene;

/// <summary>
/// APE preview camera (pipeline.md §2.1): BO3 Z-up world (X forward), Quake-style angles in degrees
/// (positive pitch looks down), and a "CoD FOV": the value is a 16:9 horizontal FOV, so
/// <c>tanHalfY = tan(fov/2) · 0.5625</c> regardless of the window, and <c>tanHalfX = aspect · tanHalfY</c>.
/// </summary>
public readonly record struct PreviewCamera(Vector3 Position, float PitchDegrees, float YawDegrees, float RollDegrees = 0f, float FovDegrees = 65f)
{
    /// <summary><c>ToolsGfx_PreviewRenderer_ctor</c> defaults before auto-framing: (-200, 0, 80), 22.5° down.</summary>
    public static PreviewCamera Default => new(new Vector3(-200, 0, 80), 22.5f, 0f);

    public float TanHalfFovY => MathF.Tan(FovDegrees * 0.5f * (MathF.PI / 180f)) * 0.5625f;

    /// <summary>BO3 <c>AngleVectors</c>: forward, right, up.</summary>
    public void GetAxes(out Vector3 forward, out Vector3 right, out Vector3 up)
    {
        const float d2r = MathF.PI / 180f;
        float sp = MathF.Sin(PitchDegrees * d2r), cp = MathF.Cos(PitchDegrees * d2r);
        float sy = MathF.Sin(YawDegrees * d2r), cy = MathF.Cos(YawDegrees * d2r);
        float sr = MathF.Sin(RollDegrees * d2r), cr = MathF.Cos(RollDegrees * d2r);
        forward = new Vector3(cp * cy, cp * sy, -sp);
        right = new Vector3(-sr * sp * cy + cr * sy, -sr * sp * sy - cr * cy, -sr * cp);
        up = new Vector3(cr * sp * cy + sr * sy, cr * sp * sy - sr * cy, cr * cp);
    }

    /// <summary>Recovers angles from a camera-to-world rotation (rows right, up, forward), e.g. a captured cb9.</summary>
    public static PreviewCamera FromAxes(Vector3 position, Vector3 forward, Vector3 right, float fovDegrees = 65f)
    {
        const float r2d = 180f / MathF.PI;
        float pitch = -MathF.Asin(Math.Clamp(forward.Z, -1f, 1f)) * r2d;
        float yaw = MathF.Atan2(forward.Y, forward.X) * r2d;
        // right.z = -sin(roll)·cos(pitch)
        float cp = MathF.Cos(pitch / r2d);
        float roll = cp > 1e-6f ? MathF.Asin(Math.Clamp(-right.Z / cp, -1f, 1f)) * r2d : 0f;
        return new PreviewCamera(position, pitch, yaw, roll, fovDegrees);
    }
}
