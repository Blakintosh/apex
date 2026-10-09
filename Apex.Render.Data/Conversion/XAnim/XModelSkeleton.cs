namespace Apex.Render.Data.Conversion.XAnim;

/// <summary>One bone of an xmodel_bin in bind pose (world/object space), as APE's loader stores it (192 bytes).</summary>
public struct XModelSkeletonBone
{
    public string Name;
    public int Parent;
    public float Ox, Oy, Oz;
    public float Sx, Sy, Sz;
    public Mat3 M;
}

/// <summary>
/// The bone section of an xmodel_bin (APE <c>XModel_ParseBones</c> 0x14064E1B0 + <c>XModel_ParseBoneXforms</c>
/// 0x14064E340, loaded with the bones-only flag for xanim conversion). Names are lower-cased. Immutable.
/// </summary>
public sealed class XModelSkeleton
{
    public required XModelSkeletonBone[] Bones { get; init; }

    public static XModelSkeleton Load(string xmodelBinPath) => Parse(BinTokenReader.ReadFile(xmodelBinPath), xmodelBinPath);

    public static XModelSkeleton Parse(byte[] decompressed, string label)
    {
        var r = new BinTokenReader(decompressed, label);
        r.Section("MODEL");
        var version = r.UShort("VERSION");
        if (version > 7)
            throw new InvalidDataException($"Expecting version 7 but found version {version} ({label})");
        int n = r.UShort("NUMBONES");
        if (n > 0x2000)
            throw new InvalidDataException($"Too many bones in model ({label})");
        if (r.PeekIs("NUMCOSMETICBONES", BinTokenReader.DataType.Int))
            r.Int("NUMCOSMETICBONES");

        var bones = new XModelSkeletonBone[n];
        for (int i = 0; i < n; i++)
        {
            var (idx, parent, name) = r.BoneInfo("BONE");
            if (idx != i)
                throw new InvalidDataException($"Bone number {idx} out of sync ({label})");
            if (parent < -1 || parent >= n)
                throw new InvalidDataException($"Parent bone out of range for bone {idx} ({label})");
            bones[i].Name = Lower(name.Length > 127 ? name[..127] : name);
            bones[i].Parent = parent;
        }

        Span<float> v = stackalloc float[3];
        for (int i = 0; i < n; i++)
        {
            int idx = r.UShort("BONE");
            if (idx != i)
                throw new InvalidDataException($"Bone number out of sync ({label})");
            ref var b = ref bones[i];
            r.Vector3("OFFSET", v);
            b.Ox = v[0]; b.Oy = v[1]; b.Oz = v[2];
            if (r.PeekIs("SCALE", BinTokenReader.DataType.Vector3))
            {
                r.Vector3("SCALE", v);
                b.Sx = v[0]; b.Sy = v[1]; b.Sz = v[2];
            }
            else
            {
                b.Sx = b.Sy = b.Sz = 1f;
            }
            r.Vector316("X", v); b.M.M0 = v[0]; b.M.M1 = v[1]; b.M.M2 = v[2];
            r.Vector316("Y", v); b.M.M3 = v[0]; b.M.M4 = v[1]; b.M.M5 = v[2];
            r.Vector316("Z", v); b.M.M6 = v[0]; b.M.M7 = v[1]; b.M.M8 = v[2];
        }
        return new XModelSkeleton { Bones = bones };
    }

    /// <summary>C <c>strlwr</c> in the "C" locale: ASCII A-Z only.</summary>
    internal static string Lower(string s)
    {
        foreach (var c in s)
            if (c is >= 'A' and <= 'Z')
                return string.Create(s.Length, s, static (span, src) =>
                {
                    for (int i = 0; i < src.Length; i++)
                        span[i] = src[i] is >= 'A' and <= 'Z' ? (char)(src[i] + 32) : src[i];
                });
        return s;
    }
}
