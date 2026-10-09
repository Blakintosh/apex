using System.Numerics;

namespace Apex.Render.Assets;

/// <summary>
/// A recoil offset for one frame of a viewmodel preview, in the engine's units and signs (docs/plugin-abi/apex_sim.h):
/// angles are pitch, yaw, roll in degrees, negative pitch up and positive yaw left; origins are inches along x forward,
/// y left, z up. <see cref="ViewAngles"/> / <see cref="ViewOrigin"/> move the camera and the viewmodel together (the
/// player's view kicks; the world moves on screen); <see cref="GunAngles"/> / <see cref="GunOrigin"/> move only the
/// viewmodel, about its own origin. The viewmodel's model space is the view space at rest (the game puts its origin at
/// the eye, axes along the view), so both are rigid transforms of model space.
/// </summary>
public readonly record struct PreviewKick(Vector3 ViewAngles, Vector3 ViewOrigin, Vector3 GunAngles, Vector3 GunOrigin)
{
    public bool IsZero => ViewAngles == Vector3.Zero && ViewOrigin == Vector3.Zero && GunAngles == Vector3.Zero && GunOrigin == Vector3.Zero;

    public bool MovesView => ViewAngles != Vector3.Zero || ViewOrigin != Vector3.Zero;

    /// <summary>The view's move (row vectors): a point at rest times this is where the kicked view puts it.</summary>
    public Matrix4x4 View => Transform(ViewAngles, ViewOrigin);

    /// <summary>The viewmodel's own move within the view (row vectors).</summary>
    public Matrix4x4 Gun => Transform(GunAngles, GunOrigin);

    /// <summary>
    /// A rigid transform from engine angles: the rows are the rotated forward, left and up axes (BO3's
    /// <c>AngleVectors</c>, left = -right), the translation <paramref name="origin"/>. Pitch -5 turns +X (forward) up,
    /// yaw +5 turns it left (+Y).
    /// </summary>
    public static Matrix4x4 Transform(Vector3 angles, Vector3 origin)
    {
        const float d2r = MathF.PI / 180f;
        float sp = MathF.Sin(angles.X * d2r), cp = MathF.Cos(angles.X * d2r);
        float sy = MathF.Sin(angles.Y * d2r), cy = MathF.Cos(angles.Y * d2r);
        float sr = MathF.Sin(angles.Z * d2r), cr = MathF.Cos(angles.Z * d2r);
        var forward = new Vector3(cp * cy, cp * sy, -sp);
        var right = new Vector3(-sr * sp * cy + cr * sy, -sr * sp * sy - cr * cy, -sr * cp);
        var up = new Vector3(cr * sp * cy + sr * sy, cr * sp * sy - sr * cy, cr * cp);
        return new Matrix4x4(
            forward.X, forward.Y, forward.Z, 0,
            -right.X, -right.Y, -right.Z, 0,
            up.X, up.Y, up.Z, 0,
            origin.X, origin.Y, origin.Z, 1);
    }
}
