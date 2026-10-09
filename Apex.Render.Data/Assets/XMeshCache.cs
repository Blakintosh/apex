using System.Numerics;
using System.Runtime.InteropServices;
using Apex.Render.Data.IO;

namespace Apex.Render.Data.Assets;

/// <summary>A bone of an xmesh cache (160 bytes on disk).</summary>
/// <param name="Name">Bone name (e.g. <c>tag_origin</c>, <c>j_mainroot</c>).</param>
/// <param name="Parent">Parent index (-1 = root; for local bones -1 = child of the root).</param>
/// <param name="Rotation">Quaternion (xyzw); model space for <see cref="XMeshData.Bones"/>, parent-relative for <see cref="XMeshData.LocalBones"/>.</param>
/// <param name="Translation">Translation in the same space.</param>
public sealed record XMeshBone(string Name, int Parent, Quaternion Rotation, Vector3 Translation);

/// <summary>Model-space bind pose of a bone (CoD <c>DObjAnimMat</c>, 32 bytes).</summary>
/// <param name="Rotation">Quaternion (xyzw), not necessarily normalised.</param>
/// <param name="Translation">Model-space translation.</param>
/// <param name="TransWeight"><c>2 / dot(q, q)</c> (about 2).</param>
public readonly record struct XMeshBaseMat(Quaternion Rotation, Vector3 Translation, float TransWeight)
{
    /// <summary>The bind-pose bone-to-model transform (row-vector convention, translation in M41..M43).</summary>
    public Matrix4x4 ToMatrix()
    {
        var m = Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(Rotation));
        m.Translation = Translation;
        return m;
    }
}

/// <summary>Per-bone bounds (28 bytes; inferred meaning).</summary>
public readonly record struct XMeshBoneBounds(Vector3 Min, Vector3 Max, uint BoneIndex);

/// <summary>A converted CPU vertex (104 bytes, the xmesh cache layout).</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct XMeshVertex
{
    public uint SourceIndex;
    public int RigidBone;
    public Vector3 Position;
    public Vector3 Normal;
    public byte ColorR, ColorG, ColorB, ColorA;
    public Vector2 TexCoord;
    public Vector3 Tangent;
    public Vector3 Bitangent;
    public uint Flag;
    public uint Bone0;
    public float Weight0;
    public uint Bone1;
    public float Weight1;
    public uint Bone2;
    public float Weight2;
    public uint Bone3;
    public float Weight3;
}

/// <summary>A triangle of an xmesh surface: three vertex indices plus its rigid bone (0xFFFFFFFF when mixed).</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly record struct XMeshTriangle(uint I0, uint I1, uint I2, uint Bone);

/// <summary>One surface (one material) of an xmesh.</summary>
public sealed class XMeshSurface
{
    /// <summary>Material asset name, e.g. <c>mtl_barrel_rust_01_tan</c>.</summary>
    public required string Material { get; init; }

    /// <summary>Bone index when every vertex is bound to one bone, else -1.</summary>
    public required int RigidBone { get; init; }

    /// <summary>0 rigid / 1 weighted (inferred).</summary>
    public required uint IsSkinned { get; init; }

    public required Vector3 BoundsMin { get; init; }
    public required Vector3 BoundsMax { get; init; }
    public required float Radius { get; init; }
    public required uint HeaderUnknown0 { get; init; }

    public required XMeshVertex[] Vertices { get; init; }

    /// <summary>Triangles in authored order.</summary>
    public required XMeshTriangle[] Triangles { get; init; }

    /// <summary>The same triangle set in three other orders (per-axis depth sorts, inferred).</summary>
    public required XMeshTriangle[][] SortedTriangles { get; init; }

    /// <summary>Unique edges as (vA, vB) pairs, flattened.</summary>
    public required uint[] Edges { get; init; }
}

/// <summary>A parsed xmesh v39 cache file (the converted CPU mesh; positions already scaled).</summary>
public sealed class XMeshData
{
    public required IReadOnlyList<XMeshBone> Bones { get; init; }
    public required IReadOnlyList<XMeshSurface> Surfaces { get; init; }

    /// <summary>Bones minus the root, parent-relative.</summary>
    public required IReadOnlyList<XMeshBone> LocalBones { get; init; }

    /// <summary>Model-space bind pose per bone (same count as <see cref="Bones"/>).</summary>
    public required IReadOnlyList<XMeshBaseMat> BaseMats { get; init; }

    public required float Unknown120 { get; init; }
    public required uint Unknown124 { get; init; }
    public required IReadOnlyList<XMeshBoneBounds> BoneBounds { get; init; }

    public required string CachePath { get; init; }

    public int TotalVertices => Surfaces.Sum(s => s.Vertices.Length);
    public int TotalTriangles => Surfaces.Sum(s => s.Triangles.Length);

    /// <summary>
    /// Parses an xmesh v39 payload: bones, surfaces (168-byte header, 104-byte vertices, 4 triangle lists,
    /// edges), local bones, base mats, two unknown words, bone bounds.
    /// </summary>
    public static XMeshData Parse(byte[] data, string label)
    {
        var r = new ByteReader(data, label);
        var bones = ReadBones(r);

        int surfaceCount = r.Count(168);
        var surfaces = new List<XMeshSurface>(surfaceCount);
        for (int s = 0; s < surfaceCount; s++)
        {
            uint unk0 = r.U32();
            var material = r.FixedString(128);
            int rigidBone = r.I32();
            uint isSkinned = r.U32();
            var min = new Vector3(r.F32(), r.F32(), r.F32());
            var max = new Vector3(r.F32(), r.F32(), r.F32());
            float radius = r.F32();

            int vcount = r.Count(104);
            var verts = r.Array<XMeshVertex>(vcount);
            int tcount = r.Count(16 * 4);
            var tris = r.Array<XMeshTriangle>(tcount);
            var sorted = new XMeshTriangle[3][];
            for (int k = 0; k < 3; k++)
                sorted[k] = r.Array<XMeshTriangle>(tcount);
            int ecount = r.Count(8);
            var edges = r.Array<uint>(ecount * 2);

            surfaces.Add(new XMeshSurface
            {
                Material = material,
                RigidBone = rigidBone,
                IsSkinned = isSkinned,
                BoundsMin = min,
                BoundsMax = max,
                Radius = radius,
                HeaderUnknown0 = unk0,
                Vertices = verts,
                Triangles = tris,
                SortedTriangles = sorted,
                Edges = edges,
            });
        }

        var localBones = ReadBones(r);
        int baseCount = r.Count(32);
        var baseMats = new XMeshBaseMat[baseCount];
        for (int i = 0; i < baseCount; i++)
        {
            var q = new Quaternion(r.F32(), r.F32(), r.F32(), r.F32());
            var t = new Vector3(r.F32(), r.F32(), r.F32());
            baseMats[i] = new XMeshBaseMat(q, t, r.F32());
        }
        float unk120 = r.F32();
        uint unk124 = r.U32();
        int boundsCount = r.Count(28);
        var bounds = new XMeshBoneBounds[boundsCount];
        for (int i = 0; i < boundsCount; i++)
            bounds[i] = new XMeshBoneBounds(new Vector3(r.F32(), r.F32(), r.F32()), new Vector3(r.F32(), r.F32(), r.F32()), r.U32());
        r.ExpectEnd();

        return new XMeshData
        {
            Bones = bones,
            Surfaces = surfaces,
            LocalBones = localBones,
            BaseMats = baseMats,
            Unknown120 = unk120,
            Unknown124 = unk124,
            BoneBounds = bounds,
            CachePath = label,
        };
    }

    /// <summary>Loads and parses an xmesh cache file.</summary>
    public static XMeshData Load(string path) => Parse(Lz4Container.ReadFile(path), path);

    private static List<XMeshBone> ReadBones(ByteReader r)
    {
        int n = r.Count(160);
        var list = new List<XMeshBone>(n);
        for (int i = 0; i < n; i++)
        {
            var name = r.FixedString(128);
            int parent = r.I32();
            var q = new Quaternion(r.F32(), r.F32(), r.F32(), r.F32());
            var t = new Vector3(r.F32(), r.F32(), r.F32());
            list.Add(new XMeshBone(name, parent, q, t));
        }
        return list;
    }
}
