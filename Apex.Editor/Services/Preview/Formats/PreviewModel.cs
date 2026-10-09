using System.Collections.Generic;
using System.Numerics;

namespace Apex.Editor.Services.Preview.Formats;

/// <summary>A single skeleton bone in as-authored (Z-up, inches) space.</summary>
public sealed class PreviewBone
{
    public string Name { get; init; } = string.Empty;

    /// <summary>Index into the owning model's bone list, or -1 for a root.</summary>
    public int ParentIndex { get; init; } = -1;

    /// <summary>Absolute (world) bone position as written in the OFFSET token.</summary>
    public Vector3 WorldPosition { get; set; }

    /// <summary>Absolute bone orientation; rows are the X/Y/Z basis vectors, 3x3 stored in a 4x4.</summary>
    public Matrix4x4 WorldRotation { get; set; } = Matrix4x4.Identity;
}

/// <summary>One deduplicated render vertex (position + normal + single UV set + up to 4 skin weights).</summary>
public struct PreviewVertex
{
    public Vector3 Position;
    public Vector3 Normal;
    public Vector2 UV;

    /// <summary>Weight per influence, components 0..3 (unused influences are 0).</summary>
    public Vector4 Weights;

    public int Bone0;
    public int Bone1;
    public int Bone2;
    public int Bone3;
}

/// <summary>Triangle soup for one material. Indices are triangle-list (3 per face) into <see cref="Vertices"/>.</summary>
public sealed class PreviewMeshPart
{
    public string MaterialName { get; set; } = string.Empty;
    public List<PreviewVertex> Vertices { get; } = new();
    public List<int> Indices { get; } = new();
}

/// <summary>A parsed XMODEL ready for preview: skeleton + per-material meshes, left in BO3 space (Z-up, inches).</summary>
public sealed class PreviewModel
{
    public List<PreviewBone> Bones { get; } = new();
    public List<PreviewMeshPart> Parts { get; } = new();
    public List<string> MaterialNames { get; } = new();

    public int TriangleCount { get; set; }

    public Vector3 BoundsMin { get; set; }
    public Vector3 BoundsMax { get; set; }
}
