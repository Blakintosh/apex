using System.Numerics;
using System.Runtime.InteropServices;
using Apex.Render.Data.Shaders;

namespace Apex.Render.Data.Assets;

/// <summary>
/// The ToolsGfx <c>generic</c> vertex (vertex declaration 0), 44 bytes, stream 0 per-vertex
/// (asset_caches.md §5.1). Packed fields store their components in memory order x, y, z, w.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct GenericVertex
{
    /// <summary>Byte size of one vertex (the VB stride).</summary>
    public const int Stride = 44;

    /// <summary>POSITION, R32G32B32_FLOAT @0.</summary>
    public Vector3 Position;

    /// <summary>COLOR, R8G8B8A8_UNORM @12 (RGBA bytes).</summary>
    public uint Color;

    /// <summary>TEXCOORD0, R32G32_FLOAT @16.</summary>
    public Vector2 TexCoord;

    /// <summary>NORMAL, R8G8B8A8_SNORM @24 (w = 0).</summary>
    public uint Normal;

    /// <summary>TANGENT, R8G8B8A8_SNORM @28 (w = bitangent sign, +/-127).</summary>
    public uint Tangent;

    /// <summary>BLENDINDICES, R16G16B16A16_UINT @32.</summary>
    public ushort BoneIndex0, BoneIndex1, BoneIndex2, BoneIndex3;

    /// <summary>BLENDWEIGHT, R8G8B8A8_UNORM @40; the four bytes sum to 255.</summary>
    public uint BoneWeights;
}

/// <summary>One element of the generic vertex declaration (numeric DXGI formats).</summary>
public sealed record VertexElementDescription(
    string SemanticName,
    int SemanticIndex,
    int DxgiFormat,
    int InputSlot,
    int AlignedByteOffset,
    bool PerInstance);

/// <summary>
/// The <c>generic</c> vertex declaration table (<c>gVertDeclElems</c>). APE declares only the elements the
/// vertex shader actually reads, looked up from its input signature.
/// </summary>
public static class GenericVertexLayout
{
    /// <summary>Stream 1: per-instance <c>INSTANCEID</c> (R32_UINT, stride 4) indexing <c>gObjectInstanceData</c> (t30).</summary>
    public const int InstanceStride = 4;

    public static IReadOnlyList<VertexElementDescription> All { get; } =
    [
        new("POSITION", 0, 6, 0, 0, false),
        new("COLOR", 0, 28, 0, 12, false),
        new("TEXCOORD", 0, 16, 0, 16, false),
        new("NORMAL", 0, 31, 0, 24, false),
        new("TANGENT", 0, 31, 0, 28, false),
        new("BLENDINDICES", 0, 12, 0, 32, false),
        new("BLENDWEIGHT", 0, 28, 0, 40, false),
        new("INSTANCEID", 0, 42, 1, 0, true),
    ];

    /// <summary>
    /// The elements a vertex shader reads (from its <c>ISGN</c>), sorted by (slot, offset) as APE does.
    /// </summary>
    /// <exception cref="InvalidOperationException">The shader reads a semantic the generic declaration lacks.</exception>
    public static IReadOnlyList<VertexElementDescription> ForShader(DxbcReflection vertexShader)
    {
        var list = new List<VertexElementDescription>();
        foreach (var input in vertexShader.Inputs)
        {
            if (input.SystemValue != 0)
                continue; // SV_VertexID etc.
            var e = All.FirstOrDefault(x => string.Equals(x.SemanticName, input.SemanticName, StringComparison.OrdinalIgnoreCase)
                                            && x.SemanticIndex == input.SemanticIndex)
                    ?? throw new InvalidOperationException(
                        $"Vertex shader is trying to access '{input.SemanticName}{input.SemanticIndex}' which is not part of the vert decl 'generic'.");
            list.Add(e);
        }
        return list.OrderBy(e => e.InputSlot).ThenBy(e => e.AlignedByteOffset).ToList();
    }
}

/// <summary>A GPU-ready surface: 44-byte vertices and 16-bit indices.</summary>
public sealed class GpuSurface
{
    public required string Material { get; init; }
    public required GenericVertex[] Vertices { get; init; }

    /// <summary>
    /// Seven concatenated triangle lists of <see cref="TriangleCount"/>*3 indices each:
    /// authored order, then sorted order k and its reverse for k = 0..2. Draw list 0 for opaque surfaces.
    /// </summary>
    public required ushort[] Indices { get; init; }

    public required int TriangleCount { get; init; }

    /// <summary>Index count of one list (<c>3 * TriangleCount</c>).</summary>
    public int IndicesPerList => TriangleCount * 3;

    /// <summary>First index of list <paramref name="list"/> (0..6).</summary>
    public int ListStart(int list) => list * IndicesPerList;

    public required int RigidBone { get; init; }
    public required Vector3 BoundsMin { get; init; }
    public required Vector3 BoundsMax { get; init; }

    /// <summary>The vertices as raw bytes (for buffer upload).</summary>
    public ReadOnlySpan<byte> VertexBytes => MemoryMarshal.AsBytes(Vertices.AsSpan());

    /// <summary>The indices as raw bytes (DXGI_FORMAT_R16_UINT).</summary>
    public ReadOnlySpan<byte> IndexBytes => MemoryMarshal.AsBytes(Indices.AsSpan());
}

/// <summary>A GPU-ready mesh: surfaces plus the skeleton.</summary>
public sealed class GpuMesh
{
    public required IReadOnlyList<GpuSurface> Surfaces { get; init; }
    public required IReadOnlyList<XMeshBone> Bones { get; init; }

    /// <summary>Model-space bind pose per bone (bone-to-model matrices).</summary>
    public required IReadOnlyList<Matrix4x4> BindPose { get; init; }

    /// <summary>The xmesh's parent-relative bones (all bones minus the root); empty for meshes not from a cache.</summary>
    public IReadOnlyList<XMeshBone> LocalBones { get; init; } = Array.Empty<XMeshBone>();

    /// <summary>The xmesh's model-space bind pose in <c>DObjAnimMat</c> form (quaternion, translation, transWeight).</summary>
    public IReadOnlyList<XMeshBaseMat> BaseMats { get; init; } = Array.Empty<XMeshBaseMat>();

    /// <summary>The xmesh's per-bone bind-space boxes (APE frames an animated model on them, posed).</summary>
    public IReadOnlyList<XMeshBoneBounds> BoneBounds { get; init; } = Array.Empty<XMeshBoneBounds>();

    public required Vector3 BoundsMin { get; init; }
    public required Vector3 BoundsMax { get; init; }

    public bool HasBones => Bones.Count > 1;

    /// <summary>
    /// Builds the GPU form of an xmesh exactly as <c>ToolsGfx_XMesh_LoadOrConvert</c> does after reading
    /// the cache (asset_caches.md §6.2): positions/colours/uvs copied, normal and tangent packed to SNORM8
    /// (tangent.w = bitangent sign), weights quantised to UNORM8 summing to 255, and the u16 index buffer
    /// with 7 triangle lists.
    /// </summary>
    /// <exception cref="InvalidDataException">A surface has more than 65534 vertices.</exception>
    public static GpuMesh FromXMesh(XMeshData mesh)
    {
        var surfaces = new List<GpuSurface>(mesh.Surfaces.Count);
        var bmin = new Vector3(float.MaxValue);
        var bmax = new Vector3(float.MinValue);
        foreach (var s in mesh.Surfaces)
        {
            if (s.Vertices.Length > 65534)
                throw new InvalidDataException($"Surface '{s.Material}' has {s.Vertices.Length} vertices; 16-bit indices allow 65534.");

            var verts = new GenericVertex[s.Vertices.Length];
            for (int i = 0; i < verts.Length; i++)
            {
                ref readonly var v = ref s.Vertices[i];
                verts[i] = PackVertex(v);
                bmin = Vector3.Min(bmin, v.Position);
                bmax = Vector3.Max(bmax, v.Position);
            }

            int n = s.Triangles.Length;
            var indices = new ushort[n * 3 * 7];
            int o = 0;
            Append(s.Triangles, false);
            for (int k = 0; k < 3; k++)
            {
                Append(s.SortedTriangles[k], false);
                Append(s.SortedTriangles[k], true);
            }

            surfaces.Add(new GpuSurface
            {
                Material = s.Material,
                Vertices = verts,
                Indices = indices,
                TriangleCount = n,
                RigidBone = s.RigidBone,
                BoundsMin = s.BoundsMin,
                BoundsMax = s.BoundsMax,
            });

            void Append(XMeshTriangle[] tris, bool reversed)
            {
                for (int t = 0; t < tris.Length; t++)
                {
                    var tri = tris[reversed ? tris.Length - 1 - t : t];
                    indices[o++] = (ushort)tri.I0;
                    indices[o++] = (ushort)tri.I1;
                    indices[o++] = (ushort)tri.I2;
                }
            }
        }

        if (surfaces.Count == 0)
        {
            bmin = Vector3.Zero;
            bmax = Vector3.Zero;
        }

        return new GpuMesh
        {
            Surfaces = surfaces,
            Bones = mesh.Bones,
            BindPose = mesh.BaseMats.Select(b => b.ToMatrix()).ToArray(),
            LocalBones = mesh.LocalBones,
            BaseMats = mesh.BaseMats,
            BoneBounds = mesh.BoneBounds,
            BoundsMin = bmin,
            BoundsMax = bmax,
        };
    }

    /// <summary>Packs one cache vertex into the generic layout.</summary>
    public static GenericVertex PackVertex(in XMeshVertex v)
    {
        bool sign = Vector3.Dot(Vector3.Cross(v.Normal, v.Tangent), v.Bitangent) >= 0;
        return new GenericVertex
        {
            Position = v.Position,
            Color = (uint)(v.ColorR | v.ColorG << 8 | v.ColorB << 16 | v.ColorA << 24),
            TexCoord = v.TexCoord,
            Normal = Pack4(Snorm8(v.Normal.X), Snorm8(v.Normal.Y), Snorm8(v.Normal.Z), 0),
            Tangent = Pack4(Snorm8(v.Tangent.X), Snorm8(v.Tangent.Y), Snorm8(v.Tangent.Z), (sbyte)(sign ? 127 : -127)),
            BoneIndex0 = (ushort)v.Bone0,
            BoneIndex1 = (ushort)v.Bone1,
            BoneIndex2 = (ushort)v.Bone2,
            BoneIndex3 = (ushort)v.Bone3,
            BoneWeights = PackWeights(v.Weight0, v.Weight1, v.Weight2, v.Weight3),
        };
    }

    /// <summary><c>Gfx_PackNormalSnorm8</c> rounding: half away from zero.</summary>
    public static sbyte Snorm8(float x) =>
        x < 0 ? (sbyte)-(int)(0.5f - x * 127f) : (sbyte)(int)(x * 127f + 0.5f);

    private static uint Pack4(sbyte x, sbyte y, sbyte z, sbyte w) =>
        (uint)((byte)x | (byte)y << 8 | (byte)z << 16 | (byte)w << 24);

    /// <summary><c>Gfx_PackBlendIndicesWeights</c>: round each to UNORM8, then force the sum to 255 via the first byte.</summary>
    public static uint PackWeights(float w0, float w1, float w2, float w3)
    {
        int a = (int)(w0 * 255f + 0.5f), b = (int)(w1 * 255f + 0.5f), c = (int)(w2 * 255f + 0.5f), d = (int)(w3 * 255f + 0.5f);
        a += 255 - (a + b + c + d);
        return (uint)((byte)a | (byte)b << 8 | (byte)c << 16 | (byte)d << 24);
    }
}

/// <summary>
/// Layout helpers for <c>CodeObjectBonesConstBuffer</c> (b11): 1024 x { row_major float3x4 objMatrixT; float4 extra; }
/// = 64 KiB. The VS computes <c>mul(M, float4(pos, 1))</c> with <c>M = Σ wᵢ · objMatrixT[idxᵢ]</c>.
/// </summary>
public static class BoneBuffer
{
    public const int MaxBones = 1024;
    public const int BytesPerBone = 64;
    public const int SizeInBytes = MaxBones * BytesPerBone;

    /// <summary>
    /// Packs skinning matrices (row-vector <see cref="Matrix4x4"/>: vertex' = vertex * M, i.e.
    /// <c>inverse(bindPose) * animatedPose</c>) into the b11 layout. Row r of the float3x4 is column r of M
    /// (<c>M1r, M2r, M3r, M4r</c>); <c>extra</c> is zero. Unused bones stay zero.
    /// </summary>
    public static float[] Pack(IReadOnlyList<Matrix4x4> skinMatrices)
    {
        if (skinMatrices.Count > MaxBones)
            throw new ArgumentException($"At most {MaxBones} bones fit the bone constant buffer.", nameof(skinMatrices));
        var f = new float[MaxBones * 16];
        for (int i = 0; i < skinMatrices.Count; i++)
        {
            var m = skinMatrices[i];
            int o = i * 16;
            f[o + 0] = m.M11; f[o + 1] = m.M21; f[o + 2] = m.M31; f[o + 3] = m.M41;
            f[o + 4] = m.M12; f[o + 5] = m.M22; f[o + 6] = m.M32; f[o + 7] = m.M42;
            f[o + 8] = m.M13; f[o + 9] = m.M23; f[o + 10] = m.M33; f[o + 11] = m.M43;
        }
        return f;
    }

    /// <summary>Identity skinning for <paramref name="boneCount"/> bones (renders the bind pose).</summary>
    public static float[] Identity(int boneCount) => Pack(Enumerable.Repeat(Matrix4x4.Identity, Math.Min(boneCount, MaxBones)).ToArray());

    /// <summary>Skinning matrices <c>inverse(bind) * pose</c> for model-space poses (row-vector convention).</summary>
    public static Matrix4x4[] SkinningMatrices(IReadOnlyList<Matrix4x4> bindPose, IReadOnlyList<Matrix4x4> modelSpacePose)
    {
        var r = new Matrix4x4[bindPose.Count];
        for (int i = 0; i < r.Length; i++)
        {
            Matrix4x4.Invert(bindPose[i], out var inv);
            r[i] = inv * modelSpacePose[i];
        }
        return r;
    }
}
