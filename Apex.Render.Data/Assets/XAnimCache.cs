using System.Numerics;
using Apex.Render.Data.IO;

namespace Apex.Render.Data.Assets;

/// <summary>One animated bone of an xanim cache: parent-relative samples per frame.</summary>
public sealed class XAnimBone
{
    public required string Name { get; init; }
    public required Quaternion[] Rotations { get; init; }
    public required Vector3[] Translations { get; init; }

    /// <summary>16 bytes per sample, all zero in every file seen (unknown channel).</summary>
    public required byte[] Extra { get; init; }
}

/// <summary>A notetrack (frame, name).</summary>
public readonly record struct XAnimNote(uint Frame, string Name);

/// <summary>A parsed xanim v11 cache file (an anim retargeted onto one model's skeleton).</summary>
public sealed class XAnimData
{
    public required float Framerate { get; init; }

    /// <summary>Authored frame count; looping anims store one extra sample.</summary>
    public required uint FrameCount { get; init; }

    /// <summary>Root-motion (tag_origin delta) rotations per sample.</summary>
    public required Quaternion[] DeltaRotations { get; init; }

    /// <summary>Root-motion translations per sample.</summary>
    public required Vector3[] DeltaTranslations { get; init; }

    /// <summary>Bones in the model's local-bone order (model bones minus tag_origin).</summary>
    public required IReadOnlyList<XAnimBone> Bones { get; init; }

    public required IReadOnlyList<XAnimNote> Notetracks { get; init; }

    public int SampleCount => DeltaRotations.Length;

    /// <summary>Parses an xanim v11 payload.</summary>
    public static XAnimData Parse(byte[] data, string label)
    {
        var r = new ByteReader(data, label);
        float fps = r.F32();
        uint frames = r.U32();
        int n = r.Count(28);
        var dq = ReadQuats(r, n);
        var dt = ReadVecs(r, n);

        int boneCount = r.Count(132);
        var bones = new List<XAnimBone>(boneCount);
        for (int b = 0; b < boneCount; b++)
        {
            var name = r.FixedString(128);
            int samples = r.Count(44);
            bones.Add(new XAnimBone
            {
                Name = name,
                Rotations = ReadQuats(r, samples),
                Translations = ReadVecs(r, samples),
                Extra = r.Bytes(samples * 16),
            });
        }

        int noteCount = r.Count(132);
        var notes = new List<XAnimNote>(noteCount);
        for (int i = 0; i < noteCount; i++)
        {
            uint frame = r.U32();
            notes.Add(new XAnimNote(frame, r.FixedString(128)));
        }
        r.ExpectEnd();

        return new XAnimData
        {
            Framerate = fps,
            FrameCount = frames,
            DeltaRotations = dq,
            DeltaTranslations = dt,
            Bones = bones,
            Notetracks = notes,
        };
    }

    /// <summary>Loads and parses an xanim cache file.</summary>
    public static XAnimData Load(string path) => Parse(Lz4Container.ReadFile(path), path);

    private static Quaternion[] ReadQuats(ByteReader r, int n)
    {
        var q = new Quaternion[n];
        for (int i = 0; i < n; i++) q[i] = new Quaternion(r.F32(), r.F32(), r.F32(), r.F32());
        return q;
    }

    private static Vector3[] ReadVecs(ByteReader r, int n)
    {
        var v = new Vector3[n];
        for (int i = 0; i < n; i++) v[i] = new Vector3(r.F32(), r.F32(), r.F32());
        return v;
    }
}

/// <summary>The xbin v1 cache: an xmodel_bin's material list (<c>u32 n; char name[n][128]</c>).</summary>
public static class XBinCache
{
    public static IReadOnlyList<string> Parse(byte[] data, string label)
    {
        var r = new ByteReader(data, label);
        int n = r.Count(128);
        var list = new string[n];
        for (int i = 0; i < n; i++) list[i] = r.FixedString(128);
        r.ExpectEnd();
        return list;
    }

    public static IReadOnlyList<string> Load(string path) => Parse(Lz4Container.ReadFile(path), path);
}
