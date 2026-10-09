using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Apex.Render.Data.Conversion.XModel;

/// <summary>A bone of an xmodel_bin (APE's 192-byte <c>XModelBone</c>: name, parent, offset, scale, 3x3 axis rows X/Y/Z).</summary>
internal sealed class SrcBone
{
    public byte[] Name = new byte[128];
    public int Parent;
    public float OX, OY, OZ;
    public float SX = 1, SY = 1, SZ = 1;

    /// <summary>Row-major 3x3: rows X, Y, Z (from the 16-bit X/Y/Z tokens).</summary>
    public float[] M = new float[9];

    public string NameString => Encoding.Latin1.GetString(Name, 0, Math.Max(0, Array.IndexOf(Name, (byte)0) is var i && i >= 0 ? i : 128));
}

/// <summary>
/// A vertex of an xmodel_bin (APE's 144-byte record: up to 16 weights sorted by bone, then position). A struct, as
/// is <see cref="SrcFace"/>: big models have millions of each, which as objects the GC would keep copying.
/// </summary>
internal struct SrcVert
{
    public int NumWeights;
    public Weights16<uint> Bones;
    public Weights16<float> Weights;
    public float X, Y, Z;
}

[InlineArray(16)]
internal struct Weights16<T>
{
    private T _element;
}

/// <summary>One corner of a face (28 bytes in APE: vertex, normal, RGBA8 colour, first UV set).</summary>
internal struct SrcCorner
{
    public int Vert;
    public float NX, NY, NZ;
    public uint Color;
    public float U, V;
}

/// <summary>A face (APE's 116-byte record): object, material, 3 corners, face tangent + bitangent.</summary>
internal struct SrcFace
{
    public int Object;
    public int Material;
    public SrcCorner C0, C1, C2;
    public float TX, TY, TZ, BX, BY, BZ;

    [UnscopedRef]
    public ref SrcCorner Corner(int i)
    {
        switch (i)
        {
            case 0: return ref C0;
            case 1: return ref C1;
            default: return ref C2;
        }
    }
}

/// <summary>
/// An xmodel_bin parsed the way APE's loader does (<c>XModelBin_Parse</c> 0x14064F900 and its section readers
/// 0x14064E1B0..0x14064F390): names lower-cased, normals re-normalised, colours round-tripped through floats,
/// weights sorted, per-face tangent frames computed from the unscaled positions.
/// </summary>
internal sealed class XModelSource
{
    public int Version;
    public bool Siege;
    public List<SrcBone> Bones = new();
    public List<SrcVert> Verts = new();
    public List<SrcFace> Faces = new();
    public List<byte[]> Objects = new();
    public List<byte[]> Materials = new();

    public ref SrcVert Vert(int i) => ref CollectionsMarshal.AsSpan(Verts)[i];

    public ref SrcFace Face(int i) => ref CollectionsMarshal.AsSpan(Faces)[i];

    public static byte[] Name128(string s)
    {
        var b = new byte[128];
        var src = Encoding.Latin1.GetBytes(s.ToLowerInvariant());
        Array.Copy(src, b, Math.Min(127, src.Length));
        return b;
    }

    public static string NameOf(byte[] b)
    {
        int n = Array.IndexOf(b, (byte)0);
        return Encoding.Latin1.GetString(b, 0, n < 0 ? b.Length : n);
    }

    public static XModelSource Load(string path, bool bonesOnly = false)
    {
        var ts = BinTokenStream.Open(path);
        var m = new XModelSource();
        ts.Expect("MODEL");
        m.Version = ts.Expect("VERSION").I0;
        if (m.Version > 7)
            throw new InvalidDataException($"{path}: expecting version 7 but found version {m.Version}.");

        // Bones (0x14064E1B0)
        int numBones = ts.Expect("NUMBONES").I0;
        if (numBones > 0x2000)
            throw new InvalidDataException($"{path}: too many bones.");
        if (ts.NextIs("NUMCOSMETICBONES"))
            ts.Next();
        for (int i = 0; i < numBones; i++)
        {
            var t = ts.Expect("BONE", BinTokenKind.BoneInfo);
            if (t.I0 != i)
                throw new InvalidDataException($"{path}: bone number {t.I0} out of sync.");
            if (t.I1 < -1 || t.I1 >= numBones)
                throw new InvalidDataException($"{path}: parent bone out of range for bone {i}.");
            m.Bones.Add(new SrcBone { Name = Name128(t.S0!), Parent = t.I1 });
        }
        // Bone transforms (0x14064E340)
        for (int i = 0; i < numBones; i++)
        {
            var b = m.Bones[i];
            if (ts.Expect("BONE", BinTokenKind.UShort).I0 != i)
                throw new InvalidDataException($"{path}: bone number out of sync.");
            var o = ts.Expect("OFFSET");
            b.OX = o.F0; b.OY = o.F1; b.OZ = o.F2;
            if (ts.NextIs("SCALE"))
            {
                var s = ts.Next()!.Value;
                b.SX = s.F0; b.SY = s.F1; b.SZ = s.F2;
            }
            var x = ts.Expect("X"); var y = ts.Expect("Y"); var z = ts.Expect("Z");
            b.M = [x.F0, x.F1, x.F2, y.F0, y.F1, y.F2, z.F0, z.F1, z.F2];
        }
        if (bonesOnly)
            return m;

        // Vertices (0x14064E5A0)
        int numVerts = ts.NextIs("NUMVERTS32") ? ts.Next()!.Value.I0 : ts.Expect("NUMVERTS").I0;
        m.Verts.Capacity = Math.Clamp(numVerts, 0, ts.Remaining / 16);
        for (int i = 0; i < numVerts; i++)
        {
            var vt = ts.Next();
            if (vt is null || (vt.Value.Name != "VERT" && vt.Value.Name != "VERT32"))
                throw new InvalidDataException($"{path}: expecting VERT or VERT32 token.");
            if (vt.Value.I0 != i)
                throw new InvalidDataException($"{path}: vertex number out of sync.");
            var o = ts.Expect("OFFSET");
            var v = new SrcVert { X = o.F0, Y = o.F1, Z = o.F2 };
            v.NumWeights = ts.Expect("BONES").I0;
            if (v.NumWeights >= 16)
                throw new InvalidDataException($"{path}: vertex {i} has too many bones.");
            for (int k = 0; k < v.NumWeights; k++)
            {
                var w = ts.Expect("BONE", BinTokenKind.BoneWeight);
                if (w.I0 >= numBones)
                    throw new InvalidDataException($"{path}: vertex {k} has bad bone.");
                if (w.F0 < 0)
                    throw new InvalidDataException($"{path}: vertex {k} has negative weight.");
                v.Bones[k] = (uint)w.I0;
                v.Weights[k] = w.F0;
            }
            SortWeights(ref v);
            m.Verts.Add(v);
        }

        // Faces (0x14064E930)
        int numFaces = ts.Expect("NUMFACES").I0;
        m.Faces.Capacity = Math.Clamp(numFaces, 0, ts.Remaining / 16);
        for (int f = 0; f < numFaces; f++)
        {
            var tt = ts.Next();
            if (tt is null || (tt.Value.Name != "TRI" && tt.Value.Name != "TRI16"))
                throw new InvalidDataException($"{path}: expecting TRI or TRI16 token.");
            var face = new SrcFace { Object = tt.Value.I0, Material = tt.Value.I1 };
            for (int c = 0; c < 3; c++)
            {
                ref var cr = ref face.Corner(c);
                var vt = ts.Next();
                if (vt is null || (vt.Value.Name != "VERT" && vt.Value.Name != "VERT32"))
                    throw new InvalidDataException($"{path}: expecting VERT or VERT32 token.");
                cr.Vert = vt.Value.I0;
                if ((uint)cr.Vert >= (uint)numVerts)
                    throw new InvalidDataException($"{path}: vertex index out of range.");
                var n = ts.Expect("NORMAL");
                float len = MathF.Sqrt(n.F1 * n.F1 + n.F0 * n.F0 + n.F2 * n.F2);
                float d = -len < 0f ? len : 1.0f;
                float inv = 1.0f / d;
                if (len == 0f)
                    throw new InvalidDataException($"{path}: vertex normal is 0.");
                cr.NX = n.F0 * inv; cr.NY = n.F1 * inv; cr.NZ = n.F2 * inv;
                var col = ts.Expect("COLOR");
                cr.Color = PackColor(col.F0) | (PackColor(col.F1) << 8) | (PackColor(col.F2) << 16) | (PackColor(col.F3) << 24);
                var uv = ts.Expect("UV");
                if (uv.I0 == 0)
                    throw new InvalidDataException($"{path}: expecting at least one UV channel.");
                cr.U = uv.F0; cr.V = uv.F1;
            }
            m.Faces.Add(face);
        }
        var verts = CollectionsMarshal.AsSpan(m.Verts);
        foreach (ref var face in CollectionsMarshal.AsSpan(m.Faces))
        {
            ref readonly var p0 = ref verts[face.C0.Vert];
            ref readonly var p1 = ref verts[face.C1.Vert];
            ref readonly var p2 = ref verts[face.C2.Vert];
            XMath.FaceTangents(p0.X, p0.Y, p0.Z, p1.X, p1.Y, p1.Z, p2.X, p2.Y, p2.Z,
                face.C0.U, face.C0.V, face.C1.U, face.C1.V, face.C2.U, face.C2.V,
                out face.TX, out face.TY, out face.TZ, out face.BX, out face.BY, out face.BZ);
        }

        // Objects (0x14064EE10)
        int numObjects = ts.Expect("NUMOBJECTS").I0;
        for (int i = 0; i < numObjects; i++)
        {
            var t = ts.Expect("OBJECT");
            if (t.I0 != i)
                throw new InvalidDataException($"{path}: object number out of sync.");
            m.Objects.Add(Name128(t.S0!));
        }

        // Materials (0x14064EF40): name + 12 appearance tokens that the mesh does not use.
        int numMaterials = ts.Expect("NUMMATERIALS").I0;
        for (int i = 0; i < numMaterials; i++)
        {
            var t = ts.Expect("MATERIAL");
            if (t.I0 != i)
                throw new InvalidDataException($"{path}: material number out of sync.");
            m.Materials.Add(Name128(t.S0!));
            foreach (var name in new[] { "COLOR", "TRANSPARENCY", "AMBIENTCOLOR", "INCANDESCENCE", "COEFFS", "GLOW", "REFRACTIVE",
                                         "SPECULARCOLOR", "REFLECTIVECOLOR", "REFLECTIVE", "BLINN", "PHONG" })
                ts.Expect(name);
        }

        // Siege bones (0x14064F390): optional.
        if (ts.NextIs("NUMSBONES"))
            ReadSiege(ts, m, path);
        return m;
    }

    private static void ReadSiege(BinTokenStream ts, XModelSource m, string path)
    {
        int n = ts.Next()!.Value.I0;
        m.Siege = true;
        if (n > 0x2000)
            throw new InvalidDataException($"{path}: too many bones.");
        if (m.Bones.Count != 1)
            throw new InvalidDataException($"{path}: this model already has bones; you can't have both regular and siege bones.");
        // The bone array is resized in place: bone 0 keeps its 128-byte name buffer, so the siege bone 0 name is
        // copied over the old one and the old name's tail survives after the terminator (it reaches the cache).
        var oldName0 = m.Bones[0].Name;
        m.Bones.Clear();
        for (int i = 0; i < n; i++)
        {
            var b = new SrcBone { M = [1, 0, 0, 0, 1, 0, 0, 0, 1] };
            var t = ts.Expect("BONE", BinTokenKind.BoneInfo);
            if (t.I0 != i)
                throw new InvalidDataException($"{path}: bone number {t.I0} out of sync.");
            b.Name = i == 0 ? OverwriteName(oldName0, t.S0!) : Name128(t.S0!);
            b.Parent = t.I1;
            if (ts.NextIs("OFFSET"))
            {
                ts.Next();
                ts.Expect("QUATERNION");
            }
            m.Bones.Add(b);
        }
        int nw = ts.Expect("NUMSWEIGHTS").I0;
        if (nw != m.Verts.Count)
            throw new InvalidDataException($"{path}: number of model verts and siege weights out of sync.");
        for (int i = 0; i < nw; i++)
        {
            var vt = ts.Next();
            if (vt is null || vt.Value.I0 != i)
                throw new InvalidDataException($"{path}: vert out of sync.");
            ref var v = ref CollectionsMarshal.AsSpan(m.Verts)[i];
            v.NumWeights = ts.Expect("BONES").I0;
            for (int k = 0; k < v.NumWeights; k++)
            {
                var w = ts.Expect("BONE", BinTokenKind.BoneWeight);
                v.Bones[k] = (uint)w.I0;
                v.Weights[k] = w.F0;
            }
            SortWeights(ref v, siege: true);
        }
    }

    private static byte[] OverwriteName(byte[] old, string s)
    {
        var b = (byte[])old.Clone();
        var src = Encoding.Latin1.GetBytes(s.ToLowerInvariant());
        // The token reader copies the string with its NUL padding to a 4-byte boundary.
        int n = Math.Min(127, src.Length);
        Array.Copy(src, b, n);
        int padded = Math.Min(128, (n + 1 + 3) & ~3);
        for (int i = n; i < padded; i++)
            b[i] = 0;
        return b;
    }

    /// <summary>
    /// <c>std::sort</c> of the weights: regular models by (bone ascending, weight descending) (0x140652280),
    /// siege weights by (weight descending, bone ascending) (0x140652360). With at most 15 entries MSVC's sort is a
    /// plain insertion sort.
    /// </summary>
    private static void SortWeights(ref SrcVert v, bool siege = false)
    {
        int n = v.NumWeights;
        for (int i = 1; i < n; i++)
        {
            uint b = v.Bones[i];
            float w = v.Weights[i];
            int j = i;
            while (j > 0 && (siege
                       ? (w == v.Weights[j - 1] ? b < v.Bones[j - 1] : w > v.Weights[j - 1])
                       : (b == v.Bones[j - 1] ? w > v.Weights[j - 1] : b < v.Bones[j - 1])))
            {
                v.Bones[j] = v.Bones[j - 1];
                v.Weights[j] = v.Weights[j - 1];
                j--;
            }
            v.Bones[j] = b;
            v.Weights[j] = w;
        }
    }

    /// <summary>Float colour channel to byte: <c>clamp((int)floorf(x*255 + 0.5), 0, 255)</c> (0x1403ACEB0).</summary>
    private static uint PackColor(float x)
    {
        int v = (int)MathF.Floor(x * 255.0f + 0.5f);
        if (v >= 255) v = 255;
        if (v <= 0) v = 0;
        return (uint)v;
    }
}
