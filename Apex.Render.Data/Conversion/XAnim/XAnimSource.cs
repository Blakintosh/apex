namespace Apex.Render.Data.Conversion.XAnim;

/// <summary>
/// One part (bone) transform of one frame of an xanim_bin, as APE's loader stores it (76 bytes):
/// world/object-space offset, scale, 3x3 rotation rows X,Y,Z (row-vector convention) and the optional
/// <c>EXTRA</c> channel (written verbatim into the cache's 16-byte per-sample block).
/// </summary>
public struct XAnimPartFrame
{
    public float Ox, Oy, Oz;
    public float Sx, Sy, Sz;
    public Mat3 M;
    public float E0, E1, E2, E3;
}

/// <summary>A 3x3 matrix as 9 floats, row-major (rows = X, Y, Z axes).</summary>
public struct Mat3
{
    public float M0, M1, M2, M3, M4, M5, M6, M7, M8;
}

/// <summary>A notetrack key of an xanim_bin (only the first notetrack's keys are read, as in APE).</summary>
public readonly record struct XAnimSourceNote(uint Frame, string Name);

/// <summary>
/// A parsed xanim_bin (APE <c>XAnim_ParseBin</c> 0x1406500B0; asset conversion.md §3.2). Part names are
/// lower-cased. Immutable after load; safe to share between threads.
/// </summary>
public sealed class XAnimSource
{
    public required int Framerate { get; init; }

    /// <summary>Lower-cased part names (index = part number).</summary>
    public required string[] PartNames { get; init; }

    /// <summary>FRAME numbers as authored (notetrack frames are made relative to the first).</summary>
    public required uint[] FrameNumbers { get; init; }

    /// <summary><c>Frames[f][part]</c>.</summary>
    public required XAnimPartFrame[][] Frames { get; init; }

    public required IReadOnlyList<XAnimSourceNote> Notes { get; init; }

    public int FrameCount => Frames.Length;

    public static XAnimSource Load(string xanimBinPath) => Parse(BinTokenReader.ReadFile(xanimBinPath), xanimBinPath);

    public static XAnimSource Parse(byte[] decompressed, string label)
    {
        var r = new BinTokenReader(decompressed, label);
        r.Section("ANIMATION");
        var version = r.UShort("VERSION");
        if (version > 4)
            throw new InvalidDataException($"Expecting version 4 but found version {version} ({label})");

        int numParts = r.UShort("NUMPARTS");
        var names = new string[numParts];
        for (int i = 0; i < numParts; i++)
        {
            var (idx, name) = r.UShortString("PART");
            if (idx != i)
                throw new InvalidDataException($"Part number {idx} out of sync ({label})");
            names[i] = XModelSkeleton.Lower(name);
        }

        int framerate = r.UShort("FRAMERATE");
        uint numFrames = r.UInt("NUMFRAMES");
        if (numFrames == 0)
            throw new InvalidDataException($"NUMFRAMES must be non-zero ({label})");

        var frameNumbers = new uint[numFrames];
        var frames = new XAnimPartFrame[numFrames][];
        Span<float> v = stackalloc float[4];
        for (int f = 0; f < numFrames; f++)
        {
            frameNumbers[f] = r.UInt("FRAME");
            var parts = new XAnimPartFrame[numParts];
            for (int p = 0; p < numParts; p++)
            {
                int idx = r.UShort("PART");
                if (idx != p)
                    throw new InvalidDataException($"Part number {idx} out of sync ({label})");
                ref var pf = ref parts[p];
                r.Vector3("OFFSET", v);
                pf.Ox = v[0]; pf.Oy = v[1]; pf.Oz = v[2];
                if (r.PeekIs("SCALE", BinTokenReader.DataType.Vector3))
                {
                    r.Vector3("SCALE", v);
                    pf.Sx = v[0]; pf.Sy = v[1]; pf.Sz = v[2];
                }
                else
                {
                    pf.Sx = pf.Sy = pf.Sz = 1f;
                }
                r.Vector316("X", v); pf.M.M0 = v[0]; pf.M.M1 = v[1]; pf.M.M2 = v[2];
                r.Vector316("Y", v); pf.M.M3 = v[0]; pf.M.M4 = v[1]; pf.M.M5 = v[2];
                r.Vector316("Z", v); pf.M.M6 = v[0]; pf.M.M7 = v[1]; pf.M.M8 = v[2];
                if (r.PeekIs("EXTRA", BinTokenReader.DataType.Vector4))
                {
                    r.Vector4("EXTRA", v);
                    pf.E0 = v[0]; pf.E1 = v[1]; pf.E2 = v[2]; pf.E3 = v[3];
                }
            }
            frames[f] = parts;
        }

        // XAnim_ParseNotetracks 0x14064FFC0: NOTETRACKS -> skip to the first NUMKEYS; only that track is read.
        var notes = new List<XAnimSourceNote>();
        bool haveKeys = r.PeekIs("NUMKEYS", BinTokenReader.DataType.UShort);
        if (!haveKeys && r.PeekIs("NOTETRACKS", BinTokenReader.DataType.Section))
            haveKeys = r.SkipTo("NUMKEYS", BinTokenReader.DataType.UShort);
        if (haveKeys)
        {
            int numKeys = r.UShort("NUMKEYS");
            for (int k = 0; k < numKeys; k++)
            {
                var (frame, name) = r.FrameNote("FRAME");
                notes.Add(new XAnimSourceNote(frame, name.Length > 127 ? name[..127] : name));
            }
        }

        return new XAnimSource
        {
            Framerate = framerate,
            PartNames = names,
            FrameNumbers = frameNumbers,
            Frames = frames,
            Notes = notes,
        };
    }
}
