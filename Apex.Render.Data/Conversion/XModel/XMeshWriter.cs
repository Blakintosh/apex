using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Apex.Render.Data.Assets;

namespace Apex.Render.Data.Conversion.XModel;

/// <summary>Serialises a <see cref="BuildMesh"/> as the xmesh v39 payload (writer lambda 0x14067DD60).</summary>
internal static class XMeshWriter
{
    public static byte[] Write(BuildMesh m)
    {
        var ms = new MemoryStream(PayloadSize(m));
        var w = new BinaryWriter(ms);

        w.Write((uint)m.BoneNames.Length);
        for (int i = 0; i < m.BoneNames.Length; i++)
            WriteBone(w, m.BoneNames[i], m.BoneParents[i], m.BoneQuats[i], m.BoneTrans[i]);

        w.Write((uint)m.Surfaces.Count);
        foreach (var s in m.Surfaces)
        {
            w.Write(s.MaterialIndex);
            w.Write(s.Material, 0, 128);
            w.Write(s.RigidBone);
            w.Write(s.Deformed);
            w.Write(s.MinX); w.Write(s.MinY); w.Write(s.MinZ);
            w.Write(s.MaxX); w.Write(s.MaxY); w.Write(s.MaxZ);
            w.Write(s.Radius);
            w.Write((uint)s.Verts.Count);
            w.Write(MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(s.Verts)));
            w.Write((uint)s.Tris.Count);
            w.Write(MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(s.Tris)));
            for (int k = 0; k < 3; k++)
                w.Write(MemoryMarshal.AsBytes(s.Sorted[k].AsSpan()));
            w.Write((uint)s.Edges.Length);
            w.Write(MemoryMarshal.AsBytes(s.Edges.AsSpan()));
        }

        w.Write((uint)m.LocalNames.Length);
        for (int i = 0; i < m.LocalNames.Length; i++)
            WriteBone(w, m.LocalNames[i], m.LocalParents[i], m.LocalQuats[i], m.LocalTrans[i]);

        w.Write((uint)m.BaseMats.Length);
        foreach (var b in m.BaseMats)
            foreach (var f in b)
                w.Write(f);

        w.Write(m.AvgTriArea);
        w.Write(m.Siege);

        w.Write((uint)m.BoneBounds.Count);
        foreach (var (mn, mx, bone) in m.BoneBounds)
        {
            w.Write(mn[0]); w.Write(mn[1]); w.Write(mn[2]);
            w.Write(mx[0]); w.Write(mx[1]); w.Write(mx[2]);
            w.Write(bone);
        }
        w.Flush();
        return ms.Length == ms.Capacity ? ms.GetBuffer() : ms.ToArray();
    }

    /// <summary>Byte size of <see cref="Write"/>'s output, so the stream never regrows.</summary>
    private static int PayloadSize(BuildMesh m)
    {
        const int bone = 128 + 4 + 16 + 12;
        long size = 4 + (long)bone * m.BoneNames.Length + 4;
        foreach (var s in m.Surfaces)
            size += 4 + 128 + 4 + 4 + 28 + 4 + 104L * s.Verts.Count + 4 + 16L * 4 * s.Tris.Count + 4 + 8L * s.Edges.Length;
        size += 4 + (long)bone * m.LocalNames.Length + 4 + 32L * m.BaseMats.Length + 4 + 4 + 4 + 28L * m.BoneBounds.Count;
        return (int)Math.Min(size, Array.MaxLength);
    }


    private static void WriteBone(BinaryWriter w, byte[] name, int parent, float[] q, float[] t)
    {
        w.Write(name, 0, 128);
        w.Write(parent);
        w.Write(q[0]); w.Write(q[1]); w.Write(q[2]); w.Write(q[3]);
        w.Write(t[0]); w.Write(t[1]); w.Write(t[2]);
    }

    static XMeshWriter()
    {
        if (Marshal.SizeOf<XMeshVertex>() != 104 || Marshal.SizeOf<BuildTri>() != 16)
            throw new InvalidOperationException("xmesh vertex/triangle layout mismatch.");
    }
}
