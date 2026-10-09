using System;
using System.Collections.Generic;
using System.Numerics;
using Apex.Render.Data.Assets;

namespace Apex.Editor.Services.Preview.ToolsGfx;

/// <summary>Procedural meshes in the ToolsGfx generic vertex layout, for previewing a material on its own.</summary>
public static class PreviewShapes
{
    /// <summary>
    /// A UV sphere (Z up, centred on the origin) with one surface drawn with <paramref name="material"/>: u wraps once
    /// around Z, v runs top to bottom; tangent/bitangent follow the UV directions; white opaque vertex colour.
    /// </summary>
    public static GpuMesh Sphere(string material, float radius = 16f, int segments = 64, int rings = 32)
    {
        var verts = new List<GenericVertex>((segments + 1) * (rings + 1));
        for (int r = 0; r <= rings; r++)
        {
            float v = (float)r / rings;
            float theta = v * MathF.PI;                 // 0 at the top pole
            float st = MathF.Sin(theta), ct = MathF.Cos(theta);
            for (int s = 0; s <= segments; s++)
            {
                float u = (float)s / segments;
                float phi = u * 2f * MathF.PI;
                float sp = MathF.Sin(phi), cp = MathF.Cos(phi);
                var n = new Vector3(st * cp, st * sp, ct);
                var tangent = new Vector3(-sp, cp, 0f);             // d/du
                var bitangent = new Vector3(ct * cp, ct * sp, -st); // d/dv
                if (st < 1e-4f)
                    bitangent = Vector3.Cross(n, tangent);
                bool sign = Vector3.Dot(Vector3.Cross(n, tangent), bitangent) >= 0;
                verts.Add(new GenericVertex
                {
                    Position = n * radius,
                    Color = 0xFFFFFFFFu,
                    TexCoord = new Vector2(u * 2f, v),
                    Normal = Pack(n, 0),
                    Tangent = Pack(tangent, (sbyte)(sign ? 127 : -127)),
                    BoneWeights = GpuMesh.PackWeights(1f, 0f, 0f, 0f),
                });
            }
        }

        var indices = new List<ushort>(segments * rings * 6);
        int stride = segments + 1;
        for (int r = 0; r < rings; r++)
        {
            for (int s = 0; s < segments; s++)
            {
                int a = r * stride + s, b = a + stride;
                // Clockwise seen from outside (BO3 front faces are CW).
                indices.Add((ushort)a); indices.Add((ushort)(a + 1)); indices.Add((ushort)b);
                indices.Add((ushort)(a + 1)); indices.Add((ushort)(b + 1)); indices.Add((ushort)b);
            }
        }

        var surface = new GpuSurface
        {
            Material = material,
            Vertices = verts.ToArray(),
            Indices = indices.ToArray(),
            TriangleCount = indices.Count / 3,
            RigidBone = 0,
            BoundsMin = new Vector3(-radius),
            BoundsMax = new Vector3(radius),
        };
        return new GpuMesh
        {
            Surfaces = new[] { surface },
            Bones = Array.Empty<XMeshBone>(),
            BindPose = Array.Empty<Matrix4x4>(),
            BoundsMin = surface.BoundsMin,
            BoundsMax = surface.BoundsMax,
        };
    }

    private static uint Pack(Vector3 v, sbyte w) =>
        (uint)((byte)GpuMesh.Snorm8(v.X) | (byte)GpuMesh.Snorm8(v.Y) << 8 | (byte)GpuMesh.Snorm8(v.Z) << 16 | (byte)w << 24);
}
