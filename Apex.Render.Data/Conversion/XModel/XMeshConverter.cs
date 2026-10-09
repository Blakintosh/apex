using System.Runtime.InteropServices;
using Apex.Render.Data.Assets;

namespace Apex.Render.Data.Conversion.XModel;

/// <summary>A triangle while building (16 bytes on disk: three indices and the triangle's rigid bone).</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct BuildTri
{
    public uint I0, I1, I2, Bone;

    public uint this[int i] => i == 0 ? I0 : i == 1 ? I1 : I2;
}

/// <summary>APE's in-memory <c>XModelSurface</c> during conversion (the first 168 bytes are the cache header).</summary>
internal sealed class BuildSurface
{
    public int MaterialIndex;
    public byte[] Material = new byte[128];
    public int RigidBone;
    public int Deformed;
    public float MinX = float.MaxValue, MinY = float.MaxValue, MinZ = float.MaxValue;
    public float MaxX = -float.MaxValue, MaxY = -float.MaxValue, MaxZ = -float.MaxValue;
    public float Radius;

    public List<XMeshVertex> Verts = new();
    public List<BuildTri> Tris = new();
    public BuildTri[][] Sorted = new BuildTri[3][];
    public ulong[] Edges = [];

    /// <summary>Weld chains: the previous vertex per vertex; see <see cref="Head"/>.</summary>
    public List<int> Next = new();

    // Last vertex created per source vertex, -1 for none, over the window of source vertices this surface has seen
    // (a surface usually covers a small range of a big model's vertices).
    private int[] _head = [];
    private int _headBase;

    public BuildSurface(int materialIndex, byte[] name, int rigidBone)
    {
        MaterialIndex = materialIndex;
        Array.Copy(name, Material, 128);
        RigidBone = rigidBone;
        Deformed = rigidBone < 0 ? 1 : 0;
    }

    /// <summary>The newest vertex made from <paramref name="sourceVert"/> (head of its weld chain), -1 for none.</summary>
    public ref int Head(int sourceVert)
    {
        if ((uint)(sourceVert - _headBase) >= (uint)_head.Length)
            GrowHead(sourceVert);
        return ref _head[sourceVert - _headBase];
    }

    private void GrowHead(int v)
    {
        int slack = Math.Max(64, _head.Length / 2);
        int lo = v, hi = v + 1 + slack;
        if (_head.Length > 0)
        {
            lo = v < _headBase ? Math.Max(0, v - slack) : _headBase;
            hi = v < _headBase ? _headBase + _head.Length : hi;
        }
        var grown = new int[hi - lo];
        Array.Fill(grown, -1);
        if (_head.Length > 0)
            Array.Copy(_head, 0, grown, _headBase - lo, _head.Length);
        _head = grown;
        _headBase = lo;
    }
}

/// <summary>The converted mesh, ready to serialise (<see cref="XMeshWriter"/>).</summary>
internal sealed class BuildMesh
{
    public required byte[][] BoneNames;
    public required int[] BoneParents;
    public required float[][] BoneQuats;
    public required float[][] BoneTrans;
    public required List<BuildSurface> Surfaces;
    public required byte[][] LocalNames;
    public required int[] LocalParents;
    public required float[][] LocalQuats;
    public required float[][] LocalTrans;
    public required float[][] BaseMats;
    public required float AvgTriArea;
    public required uint Siege;
    public required List<(float[] Min, float[] Max, uint Bone)> BoneBounds;
}

/// <summary>
/// Converts a parsed xmodel_bin to APE's cached CPU mesh (xmesh v39), reproducing
/// <c>XModel_ConvertMesh</c> (0x14067AE20) and the steps around it in <c>ToolsGfx_XMesh_LoadOrConvert</c>
/// (0x14067EBF0). See conversion.md §1.
/// </summary>
internal static class XMeshConverter
{
    public static BuildMesh Convert(XModelSource src, float scale) => Convert(src, scale, skeletonOnly: false);

    /// <summary>
    /// The skeleton half of <see cref="Convert(XModelSource, float)"/> (bones, local bones, base mats) for an xmodel_bin
    /// without surfaces — e.g. a <c>*_skeleton.xmodel_bin</c> an xanim is exported against. APE refuses to cache such a
    /// file ("no surfaces"); Apex still needs its skeleton to play the anim.
    /// </summary>
    public static BuildMesh ConvertSkeleton(XModelSource src, float scale) => Convert(src, scale, skeletonOnly: true);

    private static BuildMesh Convert(XModelSource src, float scale, bool skeletonOnly)
    {
        if (scale != 1.0f)
        {
            foreach (var b in src.Bones)
            {
                b.OX = scale * b.OX; b.OY = scale * b.OY; b.OZ = scale * b.OZ;
            }
            foreach (ref var v in CollectionsMarshal.AsSpan(src.Verts))
            {
                v.X = scale * v.X; v.Y = scale * v.Y; v.Z = scale * v.Z;
            }
        }

        // Bones (model space): name, parent, quat from the axis (w >= 0), offset.
        int nb = src.Bones.Count;
        var boneNames = new byte[nb][];
        var boneParents = new int[nb];
        var boneQuats = new float[nb][];
        var boneTrans = new float[nb][];
        for (int i = 0; i < nb; i++)
        {
            var b = src.Bones[i];
            boneNames[i] = b.Name;
            boneParents[i] = b.Parent;
            boneQuats[i] = new float[4];
            XMath.AxisToQuatPositiveW(b.M, boneQuats[i]);
            boneTrans[i] = [b.OX, b.OY, b.OZ];
        }

        var surfaces = skeletonOnly ? new List<BuildSurface>() : BuildSurfaces(src);
        if (surfaces.Count == 0 && !skeletonOnly)
            throw new InvalidDataException("Failed converting xmodel file: no surfaces.");

        // Surfaces only read the source and write themselves.
        Parallel.ForEach(surfaces, s => ProcessSurface(src, s));

        BuildLocalBones(src, out var localNames, out var localParents, out var localQuats, out var localTrans, out var baseMats);
        var bounds = BuildBoneBounds(baseMats.Length, surfaces);
        float area = surfaces.Count == 0 ? 0f : AverageTriangleArea(surfaces);

        return new BuildMesh
        {
            BoneNames = boneNames,
            BoneParents = boneParents,
            BoneQuats = boneQuats,
            BoneTrans = boneTrans,
            Surfaces = surfaces,
            LocalNames = localNames,
            LocalParents = localParents,
            LocalQuats = localQuats,
            LocalTrans = localTrans,
            BaseMats = baseMats,
            AvgTriArea = area,
            Siege = src.Siege ? 1u : 0u,
            BoneBounds = bounds,
        };
    }

    // ---------------------------------------------------------------------------------------------
    // Surface construction (0x14067AE20 first half, 0x140679C70, 0x14067A7F0)

    private static List<BuildSurface> BuildSurfaces(XModelSource src)
    {
        var surfaces = new List<BuildSurface>();
        if (src.Materials.Count == 0)
            return surfaces;
        // The exe scans every face per (material, object) pair; the pairs' faces are bucketed once, in face order.
        var buckets = new Dictionary<long, List<int>>();
        for (int f = 0; f < src.Faces.Count; f++)
        {
            ref readonly var face = ref src.Face(f);
            ref var bucket = ref CollectionsMarshal.GetValueRefOrAddDefault(buckets, BucketKey(face.Object, face.Material), out _);
            (bucket ??= []).Add(f);
        }
        for (int mat = 0; mat < src.Materials.Count; mat++)
        {
            int start = surfaces.Count;
            for (int obj = 0; obj < src.Objects.Count; obj++)
                if (buckets.TryGetValue(BucketKey(obj, mat), out var faces))
                    AddObjectMaterial(src, surfaces, buckets, faces, obj, mat);
            MergeSurfaces(src, surfaces, start);
        }
        return surfaces;
    }

    private static long BucketKey(int obj, int mat) => (long)obj << 32 | (uint)mat;

    private static void AddObjectMaterial(XModelSource src, List<BuildSurface> surfaces, Dictionary<long, List<int>> buckets,
        List<int> faces, int obj, int mat)
    {
        int firstSurface = surfaces.Count;
        var temp = new BuildSurface(mat, src.Materials[mat], 0);
        var loadIndices = new int[faces.Count];
        int objectCount = src.Objects.Count;
        List<int>? moved = null;
        for (int i = 0; i < faces.Count; i++)
        {
            ref var face = ref src.Face(faces[i]);
            if (temp.Verts.Count + 3 > 0xFFFE || temp.Tris.Count + 1 > 0x8000)
            {
                // Overflow goes to a new object, which this material's object loop reaches later.
                face.Object = objectCount;
                (moved ??= []).Add(faces[i]);
            }
            else
                loadIndices[i] = AddFace(temp, src, face, mat, face.TX, face.TY, face.TZ, face.BX, face.BY, face.BZ);
        }
        if (moved is not null)
        {
            var name = XModelSource.Name128($"{XModelSource.NameOf(src.Objects[obj])}:{objectCount}");
            src.Objects.Add(name);
            ref var bucket = ref CollectionsMarshal.GetValueRefOrAddDefault(buckets, BucketKey(objectCount, mat), out bool exists);
            if (exists)
            {
                bucket!.AddRange(moved);
                bucket.Sort();
            }
            else
                bucket = moved;
        }

        // A triangle whose vertices disagree on their rigid bone makes all three vertices skinned; repeat to a fixpoint.
        var tv = CollectionsMarshal.AsSpan(temp.Verts);
        bool changed;
        do
        {
            changed = false;
            foreach (var t in temp.Tris)
            {
                ref var a = ref tv[(int)t.I0];
                ref var b = ref tv[(int)t.I1];
                ref var c = ref tv[(int)t.I2];
                if (a.RigidBone != b.RigidBone || a.RigidBone != c.RigidBone)
                {
                    a.RigidBone = -1;
                    b.RigidBone = -1;
                    c.RigidBone = -1;
                    changed = true;
                }
            }
        } while (changed);

        for (int i = 0; i < faces.Count; i++)
        {
            ref readonly var face = ref src.Face(faces[i]);
            if (face.Object != obj)
                continue;
            int rb = tv[(int)temp.Tris[loadIndices[i]].I0].RigidBone;
            int si = firstSurface;
            while (si < surfaces.Count && surfaces[si].RigidBone != rb)
                si++;
            if (si == surfaces.Count)
                surfaces.Add(new BuildSurface(mat, src.Materials[mat], rb));
            AddFace(surfaces[si], src, face, mat, face.TX, face.TY, face.TZ, face.BX, face.BY, face.BZ);
        }

        for (int i = firstSurface; i < surfaces.Count; i++)
        {
            var s = surfaces[i];
            string name = XModelSource.NameOf(s.Material);
            if (s.Verts.Count < 3)
                throw new InvalidDataException($"Surface '{name}' doesn't have enough vertices.");
            if (s.Verts.Count > 0xFFFE)
                throw new InvalidDataException($"Surface '{name}' has {s.Verts.Count} vertices, which exceeds the maximum allowed of 65534.");
            if (3 * s.Tris.Count < 3)
                throw new InvalidDataException($"Surface '{name}' doesn't have enough indices.");
            if (3 * s.Tris.Count > 0x18000)
                throw new InvalidDataException($"Surface '{name}' has {3 * s.Tris.Count} indices, which exceeds the maximum allowed of 98304.");
        }
    }

    /// <summary>Adds a face (0x140679AD0): three welded vertices, triangle bone -1. Returns the triangle index.</summary>
    private static int AddFace(BuildSurface s, XModelSource src, in SrcFace face, int mat,
        float tx, float ty, float tz, float bx, float by, float bz)
    {
        int ti = s.Tris.Count;
        var t = new BuildTri { Bone = 0xFFFFFFFF };
        t.I0 = (uint)AddVertex(s, src, face.C0, tx, ty, tz, bx, by, bz);
        t.I1 = (uint)AddVertex(s, src, face.C1, tx, ty, tz, bx, by, bz);
        t.I2 = (uint)AddVertex(s, src, face.C2, tx, ty, tz, bx, by, bz);
        s.Tris.Add(t);
        return ti;
    }

    /// <summary>
    /// Finds or creates the surface vertex for a face corner (0x140679470). Candidates are the vertices made from
    /// the same source vertex, newest first; a match needs the exact UV, normal dot &gt;= 0.996, the exact colour,
    /// non-negative tangent and bitangent dots and the same rigid bone.
    /// </summary>
    private static int AddVertex(BuildSurface s, XModelSource src, in SrcCorner c,
        float tx, float ty, float tz, float bx, float by, float bz)
    {
        ref readonly var sv = ref src.Vert(c.Vert);
        int rigid = sv.NumWeights == 1 ? (int)sv.Bones[0] : -1;
        var verts = CollectionsMarshal.AsSpan(s.Verts);
        ref int head = ref s.Head(c.Vert);
        for (int k = head; k >= 0; k = s.Next[k])
        {
            ref var v = ref verts[k];
            float du = c.U - v.TexCoord.X, dv = c.V - v.TexCoord.Y;
            // Each test is `comiss; ja reject` in the exe: a NaN (e.g. from -inf UVs) never rejects.
            if (!(dv * dv + du * du > 0f)
                && !(0.99599999f > v.Normal.Y * c.NY + c.NX * v.Normal.X + v.Normal.Z * c.NZ)
                && Color(v) == c.Color
                && !(0f > v.Tangent.Y * ty + v.Tangent.X * tx + v.Tangent.Z * tz)
                && !(0f > v.Bitangent.Y * by + v.Bitangent.X * bx + v.Bitangent.Z * bz)
                && v.RigidBone == rigid)
                return k;
        }

        int index = s.Verts.Count;
        var nv = new XMeshVertex
        {
            SourceIndex = (uint)c.Vert,
            RigidBone = rigid,
            Position = new(sv.X, sv.Y, sv.Z),
            TexCoord = new(c.U, c.V),
            Flag = 1,
        };
        SetColor(ref nv, c.Color);
        float nx = c.NX, ny = c.NY, nz = c.NZ;
        XMath.Normalize(ref nx, ref ny, ref nz);
        nv.Normal = new(nx, ny, nz);
        nv.Tangent = new(tx, ty, tz);
        nv.Bitangent = new(bx, by, bz);

        Span<uint> bones = stackalloc uint[4];
        Span<float> weights = stackalloc float[4];
        int n = 0;
        float sum = 0f;
        for (int i = 0; i < sv.NumWeights; i++)
        {
            float w = sv.Weights[i];
            if (w >= 0.0099999998f)
            {
                bones[n] = sv.Bones[i];
                sum += w;
                weights[n] = w;
                n++;
                if (n == 4)
                    break;
            }
        }
        for (int i = n; i < 4; i++)
        {
            bones[i] = 0;
            weights[i] = 0f;
        }
        for (int i = 0; i < 4; i++)
            weights[i] = weights[i] / sum;
        nv.Bone0 = bones[0]; nv.Weight0 = weights[0];
        nv.Bone1 = bones[1]; nv.Weight1 = weights[1];
        nv.Bone2 = bones[2]; nv.Weight2 = weights[2];
        nv.Bone3 = bones[3]; nv.Weight3 = weights[3];

        s.Verts.Add(nv);
        s.Next.Add(head);
        head = index;
        return index;
    }

    private static uint Color(in XMeshVertex v) => v.ColorR | ((uint)v.ColorG << 8) | ((uint)v.ColorB << 16) | ((uint)v.ColorA << 24);

    private static void SetColor(ref XMeshVertex v, uint c)
    {
        v.ColorR = (byte)c;
        v.ColorG = (byte)(c >> 8);
        v.ColorB = (byte)(c >> 16);
        v.ColorA = (byte)(c >> 24);
    }

    /// <summary>
    /// Per material: sort its surfaces by rigid bone (std::sort), then fold later surfaces into earlier ones
    /// where <see cref="TryMerge"/> allows (0x14067A7F0).
    /// </summary>
    private static void MergeSurfaces(XModelSource src, List<BuildSurface> surfaces, int start)
    {
        if (surfaces.Count - start < 2)
            return;
        var span = CollectionsMarshal.AsSpan(surfaces)[start..];
        MsvcSort.Sort(span, static (in BuildSurface a, in BuildSurface b) => a.RigidBone < b.RigidBone);
        for (int i = start; i < surfaces.Count; i++)
        {
            int j = i + 1;
            while (j < surfaces.Count)
            {
                if (TryMerge(src, surfaces[i], surfaces[j]))
                {
                    for (int k = j; k < surfaces.Count - 1; k++)
                        (surfaces[k], surfaces[k + 1]) = (surfaces[k + 1], surfaces[k]);
                    surfaces.RemoveAt(surfaces.Count - 1);
                }
                else
                    j++;
            }
        }
    }

    /// <summary>Appends surface <paramref name="b"/> to <paramref name="a"/> when compatible (0x14067A430).</summary>
    private static bool TryMerge(XModelSource src, BuildSurface a, BuildSurface b)
    {
        if (a.MaterialIndex != b.MaterialIndex)
            return false;
        long tb = b.Tris.Count, ta = a.Tris.Count;
        if (3 * (ta + tb) > 0x18000 || b.Verts.Count + a.Verts.Count > 0xFFFE)
            return false;
        if (a.RigidBone != b.RigidBone)
        {
            if ((a.Deformed != 0 && tb > 0x100) || (b.Deformed != 0 && ta > 0x100))
                return false;
            a.RigidBone = -1;
            b.RigidBone = -1;
        }
        var bv = b.Verts;
        foreach (var t in b.Tris)
        {
            var v0 = bv[(int)t.I0]; var v1 = bv[(int)t.I1]; var v2 = bv[(int)t.I2];
            XMath.FaceTangents(v0.Position.X, v0.Position.Y, v0.Position.Z, v1.Position.X, v1.Position.Y, v1.Position.Z,
                v2.Position.X, v2.Position.Y, v2.Position.Z, v0.TexCoord.X, v0.TexCoord.Y, v1.TexCoord.X, v1.TexCoord.Y,
                v2.TexCoord.X, v2.TexCoord.Y, out float tx, out float ty, out float tz, out float bx, out float by, out float bz);
            var face = new SrcFace { C0 = Corner(v0), C1 = Corner(v1), C2 = Corner(v2) };
            AddFace(a, src, face, a.MaterialIndex, tx, ty, tz, bx, by, bz);
        }
        return true;
    }

    private static SrcCorner Corner(in XMeshVertex v) => new()
    {
        Vert = (int)v.SourceIndex,
        NX = v.Normal.X, NY = v.Normal.Y, NZ = v.Normal.Z,
        Color = Color(v),
        U = v.TexCoord.X, V = v.TexCoord.Y,
    };

    // ---------------------------------------------------------------------------------------------
    // Per-surface processing (0x14067AE20 second half)

    private static void ProcessSurface(XModelSource src, BuildSurface s)
    {
        // 1. Tangent space from the u16-truncated index list (tangentspace.cpp, 0x14031D8C0).
        var idx16 = new ushort[3 * s.Tris.Count];
        for (int i = 0; i < s.Tris.Count; i++)
        {
            idx16[3 * i] = (ushort)s.Tris[i].I0;
            idx16[3 * i + 1] = (ushort)s.Tris[i].I1;
            idx16[3 * i + 2] = (ushort)s.Tris[i].I2;
        }
        TangentSpace(CollectionsMarshal.AsSpan(s.Verts), idx16);

        // 2. Rigid triangles (0x14067A9F0).
        var verts = CollectionsMarshal.AsSpan(s.Verts);
        var tris = CollectionsMarshal.AsSpan(s.Tris);
        for (int i = 0; i < tris.Length; i++)
        {
            ref var t = ref tris[i];
            ref readonly var s0 = ref src.Vert((int)verts[(int)t.I0].SourceIndex);
            ref readonly var s1 = ref src.Vert((int)verts[(int)t.I1].SourceIndex);
            ref readonly var s2 = ref src.Vert((int)verts[(int)t.I2].SourceIndex);
            int bone;
            if ((uint)s0.NumWeights > 1 || (uint)s1.NumWeights > 1 || (uint)s2.NumWeights > 1
                || s0.Bones[0] != s1.Bones[0] || s0.Bones[0] != s2.Bones[0])
                bone = -1;
            else
                bone = (int)s0.Bones[0];
            t.Bone = (uint)bone;
            if (bone >= 0)
            {
                verts[(int)t.I0].Flag = 0;
                verts[(int)t.I1].Flag = 0;
                verts[(int)t.I2].Flag = 0;
            }
        }

        // 3. Vertex order: (flag, source weight count, first source bone), std::sort (0x14067AB00).
        ReorderVertices(src, s);

        // 4. Into the space of bone 0.
        TransformToRootBone(src, s);

        // 5. Triangle order (0x14021E2F0); only the index triplets move, the per-triangle bone stays in place.
        tris = CollectionsMarshal.AsSpan(s.Tris);
        var flat = new uint[3 * tris.Length];
        for (int i = 0; i < tris.Length; i++)
        {
            flat[3 * i] = tris[i].I0;
            flat[3 * i + 1] = tris[i].I1;
            flat[3 * i + 2] = tris[i].I2;
        }
        var opt = VertexCacheOptimizer.Optimize(flat, tris.Length, 1.0f, 0.5f, 1.3f, 256);
        for (int i = 0; i < tris.Length; i++)
        {
            tris[i].I0 = opt[3 * i];
            tris[i].I1 = opt[3 * i + 1];
            tris[i].I2 = opt[3 * i + 2];
        }

        // 6. Three copies sorted by the minimum X / Y / Z of their vertices.
        var vv = s.Verts.ToArray();
        var keyed = new KeyedTri[tris.Length];
        for (int axis = 0; axis < 3; axis++)
        {
            for (int i = 0; i < tris.Length; i++)
                keyed[i] = new KeyedTri(MinAxis(vv, tris[i], axis), tris[i]);
            MsvcSort.Sort(keyed.AsSpan(), new MinAxisLess());
            var sorted = new BuildTri[tris.Length];
            for (int i = 0; i < tris.Length; i++)
                sorted[i] = keyed[i].Tri;
            s.Sorted[axis] = sorted;
        }

        // 7. Unique edges (std::set<u64> of a | b << 32, reverse direction counts as present), ascending.
        var set = new HashSet<ulong>(3 * s.Tris.Count, EdgeKeyComparer.Instance);
        foreach (var t in s.Tris)
        {
            for (int n = 0; n < 3; n++)
            {
                ulong a = t[n], b = t[(n + 1) % 3];
                ulong k1 = a | (b << 32), k2 = b | (a << 32);
                if (!set.Contains(k2))
                    set.Add(k1);
            }
        }
        s.Edges = [.. set];
        Array.Sort(s.Edges);
    }

    private static float Pos(in XMeshVertex v, int axis) => axis == 0 ? v.Position.X : axis == 1 ? v.Position.Y : v.Position.Z;

    private static float MinAxis(XMeshVertex[] v, in BuildTri t, int axis)
    {
        float m = Pos(v[t.I0], axis);
        float p1 = Pos(v[t.I1], axis);
        if (p1 - m < 0f) m = p1;
        float p2 = Pos(v[t.I2], axis);
        if (p2 - m < 0f) m = p2;
        return m;
    }

    /// <summary><c>ulong.GetHashCode</c> is lo ^ hi, which collides for most edges of a 16-bit index mesh.</summary>
    private sealed class EdgeKeyComparer : IEqualityComparer<ulong>
    {
        public static readonly EdgeKeyComparer Instance = new();

        public bool Equals(ulong a, ulong b) => a == b;

        public int GetHashCode(ulong k) => (int)((k * 0x9E3779B97F4A7C15ul) >> 32);
    }

    /// <summary>A triangle with its <see cref="MinAxis"/> key, computed once instead of per comparison.</summary>
    private readonly record struct KeyedTri(float Key, BuildTri Tri);

    private readonly struct MinAxisLess : ILessThan<KeyedTri>
    {
        public bool Less(in KeyedTri a, in KeyedTri b) => !(b.Key <= a.Key);
    }

    /// <summary>Per vertex: flag, source weight count and first source bone.</summary>
    private readonly record struct VertexOrderKey(uint Flag, uint NumWeights, uint Bone0);

    private readonly struct VertexOrderLess(VertexOrderKey[] keys) : ILessThan<uint>
    {
        public bool Less(in uint a, in uint b)
        {
            ref readonly var ka = ref keys[a];
            ref readonly var kb = ref keys[b];
            if (ka.Flag != kb.Flag)
                return ka.Flag < kb.Flag;
            if (ka.NumWeights != kb.NumWeights)
                return ka.NumWeights < kb.NumWeights;
            return ka.Bone0 < kb.Bone0;
        }
    }

    /// <summary>Per-vertex tangent frames: angle-weighted face tangents, Gram-Schmidt against the normal (0x14031D8C0).</summary>
    private static void TangentSpace(Span<XMeshVertex> v, ushort[] idx)
    {
        int n = v.Length;
        var tan = new float[n * 3];
        var bit = new float[n * 3];
        for (int i = 0; i + 2 < idx.Length; i += 3)
        {
            int i0 = idx[i], i1 = idx[i + 1], i2 = idx[i + 2];
            var p0 = v[i0].Position; var p1 = v[i1].Position; var p2 = v[i2].Position;
            XMath.FaceTangents(p0.X, p0.Y, p0.Z, p1.X, p1.Y, p1.Z, p2.X, p2.Y, p2.Z,
                v[i0].TexCoord.X, v[i0].TexCoord.Y, v[i1].TexCoord.X, v[i1].TexCoord.Y, v[i2].TexCoord.X, v[i2].TexCoord.Y,
                out float tx, out float ty, out float tz, out float bx, out float by, out float bz);
            XMath.CornerAngles(p0.X, p0.Y, p0.Z, p1.X, p1.Y, p1.Z, p2.X, p2.Y, p2.Z, out float w0, out float w1, out float w2);
            tan[3 * i0] = tx * w0 + tan[3 * i0]; tan[3 * i0 + 1] = ty * w0 + tan[3 * i0 + 1]; tan[3 * i0 + 2] = tz * w0 + tan[3 * i0 + 2];
            tan[3 * i1] = w1 * tx + tan[3 * i1]; tan[3 * i1 + 1] = w1 * ty + tan[3 * i1 + 1]; tan[3 * i1 + 2] = w1 * tz + tan[3 * i1 + 2];
            tan[3 * i2] = w2 * tx + tan[3 * i2]; tan[3 * i2 + 1] = w2 * ty + tan[3 * i2 + 1]; tan[3 * i2 + 2] = w2 * tz + tan[3 * i2 + 2];
            bit[3 * i0] = bx * w0 + bit[3 * i0]; bit[3 * i0 + 1] = by * w0 + bit[3 * i0 + 1]; bit[3 * i0 + 2] = bz * w0 + bit[3 * i0 + 2];
            bit[3 * i1] = bx * w1 + bit[3 * i1]; bit[3 * i1 + 1] = by * w1 + bit[3 * i1 + 1]; bit[3 * i1 + 2] = bz * w1 + bit[3 * i1 + 2];
            bit[3 * i2] = bx * w2 + bit[3 * i2]; bit[3 * i2 + 1] = by * w2 + bit[3 * i2 + 1]; bit[3 * i2 + 2] = bz * w2 + bit[3 * i2 + 2];
        }

        for (int k = 0; k < n; k++)
        {
            ref var vx = ref v[k];
            float nx = vx.Normal.X, ny = vx.Normal.Y, nz = vx.Normal.Z;
            float tx = tan[3 * k], ty = tan[3 * k + 1], tz = tan[3 * k + 2];
            float bx = bit[3 * k], by = bit[3 * k + 1], bz = bit[3 * k + 2];
            float d = -(tx * nx + ty * ny + nz * tz);
            tx = nx * d + tx;
            ty = d * ny + ty;
            tz = d * nz + tz;
            float len = XMath.Normalize(ref tx, ref ty, ref tz);
            if (len < 0.001f)
            {
                XMath.Cross(bx, by, bz, nx, ny, nz, out tx, out ty, out tz);
                float len2 = XMath.Normalize(ref tx, ref ty, ref tz);
                if (len2 < 0.001f)
                    XMath.Perpendicular(nx, ny, nz, out tx, out ty, out tz);
            }
            float cx = ny * tz - nz * ty;
            float cy = nz * tx - nx * tz;
            float cz = nx * ty - ny * tx;
            if (cx * bx + cy * by + cz * bz < 0f)
            {
                cx = -cx; cy = -cy; cz = -cz;
            }
            XMath.Normalize(ref nx, ref ny, ref nz);
            XMath.Normalize(ref tx, ref ty, ref tz);
            XMath.Normalize(ref cx, ref cy, ref cz);
            vx.Normal = new(nx, ny, nz);
            vx.Tangent = new(tx, ty, tz);
            vx.Bitangent = new(cx, cy, cz);
        }
    }

    private static void ReorderVertices(XModelSource src, BuildSurface s)
    {
        int n = s.Verts.Count;
        var old = s.Verts.ToArray();
        var perm = new uint[n];
        var keys = new VertexOrderKey[n];
        for (int i = 0; i < n; i++)
        {
            perm[i] = (ushort)i;
            ref readonly var sv = ref src.Vert((int)old[i].SourceIndex);

            keys[i] = new VertexOrderKey(old[i].Flag, (uint)sv.NumWeights, sv.Bones[0]);
        }
        MsvcSort.Sort(perm.AsSpan(), new VertexOrderLess(keys));
        var remap = new uint[n];
        for (int i = 0; i < n; i++)
        {
            s.Verts[i] = old[perm[i]];
            remap[perm[i]] = (ushort)i;
        }
        var tris = CollectionsMarshal.AsSpan(s.Tris);
        for (int i = 0; i < tris.Length; i++)
        {
            tris[i].I0 = remap[tris[i].I0];
            tris[i].I1 = remap[tris[i].I1];
            tris[i].I2 = remap[tris[i].I2];
        }
        // The triangle std::sort that follows uses a comparator that always returns false: a no-op.
    }

    private static void TransformToRootBone(XModelSource src, BuildSurface s)
    {
        var root = src.Bones[0];
        Span<float> t = stackalloc float[9];
        XMath.Transpose(root.M, t);
        float ox = root.OX, oy = root.OY, oz = root.OZ;
        var verts = CollectionsMarshal.AsSpan(s.Verts);
        for (int i = 0; i < verts.Length; i++)
        {
            ref var v = ref verts[i];
            float dx = v.Position.X - ox, dy = v.Position.Y - oy, dz = v.Position.Z - oz;
            float px = t[3] * dy + t[0] * dx + t[6] * dz;
            float py = t[4] * dy + t[1] * dx + t[7] * dz;
            float pz = t[5] * dy + t[2] * dx + t[8] * dz;
            v.Position = new(px, py, pz);
            v.Normal = Rot(t, v.Normal.X, v.Normal.Y, v.Normal.Z);
            v.Bitangent = Rot(t, v.Bitangent.X, v.Bitangent.Y, v.Bitangent.Z);
            v.Tangent = Rot(t, v.Tangent.X, v.Tangent.Y, v.Tangent.Z);

            float m = s.MinX; if (px <= m) m = px; s.MinX = m;
            m = s.MinY; if (py <= m) m = py; s.MinY = m;
            m = s.MinZ; if (pz <= m) m = pz; s.MinZ = m;
            m = s.MaxX; if (m <= px) m = px; s.MaxX = m;
            m = s.MaxY; if (m <= py) m = py; s.MaxY = m;
            m = s.MaxZ; if (m <= pz) m = pz; s.MaxZ = m;
            float len = MathF.Sqrt(px * px + py * py + pz * pz);
            float r = s.Radius;
            if (r - len < 0f) r = len;
            s.Radius = r;
        }
    }

    private static System.Numerics.Vector3 Rot(ReadOnlySpan<float> t, float x, float y, float z) =>
        new(t[0] * x + y * t[3] + t[6] * z, t[1] * x + y * t[4] + t[7] * z, t[2] * x + y * t[5] + t[8] * z);

    // ---------------------------------------------------------------------------------------------
    // Skeleton (0x14067BB20), bone bounds (0x14067C040), mean triangle area (0x14067E110)

    private static void BuildLocalBones(XModelSource src, out byte[][] names, out int[] parents, out float[][] quats,
        out float[][] trans, out float[][] baseMats)
    {
        int nb = src.Bones.Count;
        int roots = 0;
        while (roots < nb && src.Bones[roots].Parent == -1)
            roots++;
        names = new byte[nb - roots][];
        parents = new int[nb - roots];
        quats = new float[nb - roots][];
        trans = new float[nb - roots][];
        baseMats = new float[nb][];
        for (int i = 0; i < roots; i++)
            baseMats[i] = [0, 0, 0, 1, 0, 0, 0, 2];

        Span<float> inv = stackalloc float[12];
        Span<float> local = stackalloc float[9];
        for (int j = roots; j < nb; j++)
        {
            var b = src.Bones[j];
            int li = j - roots;
            var nm = new byte[128];
            int len = Array.IndexOf(b.Name, (byte)0);
            Array.Copy(b.Name, nm, len < 0 ? 128 : len);
            names[li] = nm;
            int p = b.Parent;
            if ((uint)p >= (uint)j)
                throw new InvalidDataException($"Bone {j}: parent {p} does not precede it.");
            parents[li] = p - roots;
            var pb = src.Bones[p];

            XMath.Transpose(pb.M, inv);
            float nx = pb.OX * -1.0f, ny = pb.OY * -1.0f, nz = pb.OZ * -1.0f;
            inv[9] = inv[3] * ny + inv[0] * nx + inv[6] * nz;
            inv[10] = inv[4] * ny + inv[1] * nx + inv[7] * nz;
            inv[11] = inv[5] * ny + inv[2] * nx + inv[8] * nz;

            XMath.Mul33(b.M, inv, local);
            var q = new float[4];
            XMath.AxisToQuatPositiveW(local, q);
            quats[li] = q;
            XMath.TransformPoint43(b.OX, b.OY, b.OZ, inv, out float lx, out float ly, out float lz);
            trans[li] = [lx, ly, lz];

            var pm = baseMats[p];
            var bm = new float[8];
            XMath.QuatMul(q, pm, bm);
            float sq = bm[0] * bm[0] + bm[1] * bm[1] + bm[2] * bm[2] + bm[3] * bm[3];
            if (sq <= 0.001f)
            {
                bm[3] = 1.0f;
                bm[7] = 2.0f;
            }
            else
                bm[7] = 2.0f / sq;
            Span<float> axis = stackalloc float[9];
            XMath.AnimMatToAxis(pm, axis);
            bm[4] = axis[0] * lx + axis[3] * ly + axis[6] * lz + pm[4];
            bm[5] = axis[1] * lx + axis[4] * ly + axis[7] * lz + pm[5];
            bm[6] = axis[2] * lx + axis[5] * ly + axis[8] * lz + pm[6];
            baseMats[j] = bm;
        }
    }

    private static List<(float[] Min, float[] Max, uint Bone)> BuildBoneBounds(int count, List<BuildSurface> surfaces)
    {
        var mins = new float[count][];
        var maxs = new float[count][];
        var ids = new uint[count];
        for (int i = 0; i < count; i++)
        {
            mins[i] = [131072f, 131072f, 131072f];
            maxs[i] = [-131072f, -131072f, -131072f];
            ids[i] = 0xFFFFFFFF;
        }
        foreach (var s in surfaces)
        {
            foreach (var v in s.Verts)
            {
                Span<(uint B, float W)> w = [(v.Bone0, v.Weight0), (v.Bone1, v.Weight1), (v.Bone2, v.Weight2), (v.Bone3, v.Weight3)];
                foreach (var (bone, weight) in w)
                {
                    if (weight == 0f)
                        continue;
                    ids[bone] = bone;
                    var mn = mins[bone];
                    var mx = maxs[bone];
                    float px = v.Position.X, py = v.Position.Y, pz = v.Position.Z;
                    mn[0] = mn[0] <= px ? mn[0] : px;
                    mn[1] = mn[1] <= py ? mn[1] : py;
                    mn[2] = mn[2] <= pz ? mn[2] : pz;
                    mx[0] = px <= mx[0] ? mx[0] : px;
                    mx[1] = py <= mx[1] ? mx[1] : py;
                    mx[2] = pz <= mx[2] ? mx[2] : pz;
                }
            }
        }
        var list = new List<(float[], float[], uint)>();
        for (int i = 0; i < count; i++)
            if (ids[i] != 0xFFFFFFFF)
                list.Add((mins[i], maxs[i], ids[i]));
        return list;
    }

    private static float AverageTriangleArea(List<BuildSurface> surfaces)
    {
        double sum = 0.0;
        long count = 0;
        foreach (var s in surfaces)
        {
            var v = s.Verts;
            foreach (var t in s.Tris)
            {
                var a = v[(int)t.I0].Position; var b = v[(int)t.I1].Position; var c = v[(int)t.I2].Position;
                float e2y = c.Y - a.Y, e2z = c.Z - a.Z, e2x = c.X - a.X;
                float e1y = b.Y - a.Y, e1z = b.Z - a.Z, e1x = b.X - a.X;
                float cx = e2x * e1z - e2z * e1x;
                float cy = e2z * e1y - e2y * e1z;
                float cz = e2y * e1x - e2x * e1y;
                float area = MathF.Sqrt(cx * cx + cy * cy + cz * cz) * 0.5f;
                float x = area >= 0.000099999997f ? area : 0.000099999997f;
                sum += CrtMath.Log(x);
                count++;
            }
        }
        return (float)CrtMath.Exp(sum / count);
    }
}
