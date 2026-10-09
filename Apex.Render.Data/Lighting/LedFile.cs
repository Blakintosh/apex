using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace Apex.Render.Data.Lighting;

/// <summary>Sun shadow tree of one lighting state (LED <c>lightStates[s].sst</c>).</summary>
public sealed class LedSunShadowTree
{
    public required float[] WorldToSstTransform { get; init; }

    /// <summary>→ cb9 <c>sun.sstLightingConstants.dimensionInTiles</c>.</summary>
    public required Vector2 DimensionInTiles { get; init; }

    /// <summary>LED values; not the runtime cb values (those come from the runtime SST object).</summary>
    public required float InchesPerTexel { get; init; }
    public required float SpanInches { get; init; }

    /// <summary>SSI sun yaw/pitch the tree was baked for.</summary>
    public required float Yaw { get; init; }
    public required float Pitch { get; init; }

    /// <summary>u32 mips; <c>Mips[0]</c> is the t40 <c>gSunShadowTree</c> structured buffer (stride 4).</summary>
    public required IReadOnlyList<uint[]> Mips { get; init; }

    public required byte[] TileInfo { get; init; }
    public required uint MinMaxWidth { get; init; }
    public required uint MinMaxHeight { get; init; }
    public required uint[] MinMaxMins { get; init; }
    public required uint[] MinMaxMaxs { get; init; }
}

/// <summary>One lighting state of a volume (<c>lightStates[s]</c>).</summary>
public sealed class LedLightState
{
    /// <summary>SSI asset name (<c>default_morning</c>, ...).</summary>
    public required string Ssi { get; init; }

    public required LedSunShadowTree SunShadowTree { get; init; }
}

/// <summary>A probe's data for one lighting state.</summary>
public sealed class LedProbeState
{
    /// <summary>Uncompressed half3 ambient-cube arrays (X/Y/Z), 2·N.x·N.y·N.z voxels each, layout [2][z][y][x].</summary>
    public required byte[][] IrradianceArrays { get; init; }

    /// <summary>BC6H_UF16 blocks of the three 3D textures (N.x × N.y × 2N.z) → t46/t47/t48.</summary>
    public required byte[][] IrradianceTextures { get; init; }

    /// <summary>half arrays, not used by the preview.</summary>
    public required byte[][] ExposureArrays { get; init; }

    public required uint[] ExposureDimensions { get; init; }

    /// <summary>Reflection cube: [mip][face] BC6H_UF16 blocks, face order +X,−X,+Y,−Y,+Z,−Z → t51.</summary>
    public required byte[][][] ReflectionMips { get; init; }

    /// <summary>→ cb9 <c>sun.globalProbeExposure</c>.</summary>
    public required float Exposure { get; init; }

    /// <summary>→ cb9 <c>sun.avgGlobalProbeColor</c>.</summary>
    public required Vector3 AverageCubeColor { get; init; }
}

/// <summary>The 188-byte LED probe config (lighting_data.md §1.2).</summary>
public sealed class LedProbeConfig
{
    public required byte[] Raw { get; init; }
    public required Vector3 Origin { get; init; }
    public required Vector3[] Axes { get; init; }
    public required Vector3 Extents { get; init; }
    public required Vector3 Center { get; init; }
    public required float Radius { get; init; }
    public required uint ReflectionFaceSize { get; init; }
    public required byte ProbeBlendCount { get; init; }

    /// <summary>Diffuse volume voxel dims N (textures are N.x × N.y × 2N.z).</summary>
    public required (int X, int Y, int Z) VoxelDimensions { get; init; }

    /// <summary>Box +side relative to the origin: <c>extents + (centre − origin)</c>.</summary>
    public Vector3 ExtentPositive => Extents + (Center - Origin);

    /// <summary>Box −side relative to the origin: <c>extents − (centre − origin)</c>.</summary>
    public Vector3 ExtentNegative => Extents - (Center - Origin);

    internal static LedProbeConfig Parse(byte[] b)
    {
        Vector3 V(int o) => new(F(o), F(o + 4), F(o + 8));
        float F(int o) => BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(o));
        return new LedProbeConfig
        {
            Raw = b,
            Origin = V(0),
            Axes = [V(12), V(24), V(36)],
            Extents = V(48),
            Center = V(60),
            Radius = F(72),
            ReflectionFaceSize = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(76)),
            ProbeBlendCount = b[82],
            VoxelDimensions = (b[83], b[84], b[85]),
        };
    }
}

/// <summary>A probe of a volume.</summary>
public sealed class LedProbe
{
    public required LedProbeConfig Config { get; init; }

    /// <summary>Per lighting state (null where the state mask bit is clear).</summary>
    public required IReadOnlyList<LedProbeState?> States { get; init; }
}

/// <summary>A lighting volume (the <c>volumes</c> lump holds one for assetviewer.led).</summary>
public sealed class LedVolume
{
    public required uint LightExportFlags { get; init; }
    public required uint LightStateMask { get; init; }
    public required float ShadowSplitDistance { get; init; }
    public required float ShadowBiasScale { get; init; }
    public required float TransitionTime { get; init; }
    public required bool StreamLighting { get; init; }
    public required IReadOnlyList<Vector4> WorldClipPlanes { get; init; }

    /// <summary>Per lighting state 0..3 (null where the mask bit is clear).</summary>
    public required IReadOnlyList<LedLightState?> LightStates { get; init; }

    public required IReadOnlyList<(string Ssi, uint BakeIndex)> LightStateInfo { get; init; }
    public required IReadOnlyList<LedProbe> Probes { get; init; }
    public required int ProbeBlendCount { get; init; }
    public required IReadOnlyList<Vector4> WorldVistaClipPlanes { get; init; }
}

/// <summary>
/// Reader for Treyarch LED ("lighting export data") files, version 29 (lighting_data.md §1;
/// <c>LightingExportData.cpp</c>). All little-endian; <c>str</c> = u32 length + chars, <c>vec&lt;T&gt;</c> = u32 count + data.
/// Only the <c>volumes</c> lump is decoded; other lumps are kept raw.
/// </summary>
public sealed class LedFile
{
    public required uint Version { get; init; }
    public required uint SubVersion { get; init; }
    public required string ExportInfo { get; init; }

    /// <summary>All lumps by name: (lump version, raw bytes).</summary>
    public required IReadOnlyDictionary<string, (uint Version, byte[] Data)> Lumps { get; init; }

    public required IReadOnlyList<LedVolume> Volumes { get; init; }

    /// <summary>Reads and parses an LED file.</summary>
    /// <exception cref="InvalidDataException">Wrong tag/version or a layout mismatch.</exception>
    public static LedFile Load(string path)
    {
        var d = File.ReadAllBytes(path);
        var r = new Reader(d, path);
        if (d.Length < 12 || d[0] != 'L' || d[1] != 'E' || d[2] != 'D' || d[3] != 0)
            throw new InvalidDataException($"{path}: not an LED file.");
        r.Pos = 4;
        uint version = r.U32();
        if (version != 29)
            throw new InvalidDataException($"{path}: LED version {version}, expected 29.");
        uint sub = r.U32();
        var info = r.Str();
        uint lumpCount = r.U32();
        var lumps = new Dictionary<string, (uint, byte[])>(StringComparer.Ordinal);
        for (int i = 0; i < lumpCount; i++)
        {
            var name = r.Str();
            uint lver = r.U32();
            lumps[name] = (lver, r.Vec(1));
        }

        var volumes = lumps.TryGetValue("volumes", out var v) ? ReadVolumes(v.Item2, v.Item1, path) : [];
        return new LedFile { Version = version, SubVersion = sub, ExportInfo = info, Lumps = lumps, Volumes = volumes };
    }

    private static List<LedVolume> ReadVolumes(byte[] data, uint lumpVersion, string label)
    {
        var r = new Reader(data, label + ":volumes");
        var list = new List<LedVolume>();
        uint count = r.U32();
        for (int vi = 0; vi < count; vi++)
        {
            uint flags = r.U32();
            uint mask = r.U32();
            float split = r.F32(), bias = r.F32(), transition = r.F32();
            bool stream = r.U8() != 0;
            var clip = r.Vec4List();

            var states = new LedLightState?[4];
            for (int s = 0; s < 4; s++)
            {
                if (((mask >> s) & 1) == 0)
                    continue;
                var ssi = r.Str();
                var xf = r.Floats(16);
                var dim = new Vector2(r.F32(), r.F32());
                float ipt = r.F32(), span = r.F32(), yaw = r.F32(), pitch = r.F32();
                uint mipCount = r.U32();
                var mips = new List<uint[]>((int)mipCount);
                for (int m = 0; m < mipCount; m++)
                    mips.Add(r.U32Vec());
                var tile = r.Vec(1);
                uint w = r.U32(), h = r.U32();
                var mins = r.U32Vec();
                var maxs = r.U32Vec();
                states[s] = new LedLightState
                {
                    Ssi = ssi,
                    SunShadowTree = new LedSunShadowTree
                    {
                        WorldToSstTransform = xf,
                        DimensionInTiles = dim,
                        InchesPerTexel = ipt,
                        SpanInches = span,
                        Yaw = yaw,
                        Pitch = pitch,
                        Mips = mips,
                        TileInfo = tile,
                        MinMaxWidth = w,
                        MinMaxHeight = h,
                        MinMaxMins = mins,
                        MinMaxMaxs = maxs,
                    },
                };
            }

            uint infoCount = r.U32();
            var infos = new List<(string, uint)>();
            for (int i = 0; i < infoCount; i++)
            {
                var ssi = r.Str();
                uint bake = r.U32();
                if (lumpVersion >= 2)
                    r.Pos += 16; // lens flare uuid
                infos.Add((ssi, bake));
            }

            uint probeCount = r.U32();
            var probes = new List<LedProbe>();
            for (int p = 0; p < probeCount; p++)
            {
                var config = LedProbeConfig.Parse(r.Bytes(188));
                var pstates = new LedProbeState?[4];
                for (int s = 0; s < 4; s++)
                {
                    if (((mask >> s) & 1) == 0)
                        continue;
                    var irrArrays = new[] { r.Vec(6), r.Vec(6), r.Vec(6) };
                    var irrTex = new[] { r.Vec(1), r.Vec(1), r.Vec(1) };
                    var expArrays = new[] { r.Vec(2), r.Vec(2), r.Vec(2) };
                    var expDim = new[] { r.U32(), r.U32(), r.U32() };
                    uint mipCount = r.U32();
                    uint n = r.U32();
                    if (n != mipCount)
                        throw new InvalidDataException($"{label}: reflection mip count mismatch ({n} vs {mipCount}).");
                    var refl = new byte[mipCount][][];
                    for (int m = 0; m < mipCount; m++)
                    {
                        refl[m] = new byte[6][];
                        for (int f = 0; f < 6; f++)
                            refl[m][f] = r.Vec(1);
                    }
                    float exposure = r.F32();
                    var avg = new Vector3(r.F32(), r.F32(), r.F32());
                    pstates[s] = new LedProbeState
                    {
                        IrradianceArrays = irrArrays,
                        IrradianceTextures = irrTex,
                        ExposureArrays = expArrays,
                        ExposureDimensions = expDim,
                        ReflectionMips = refl,
                        Exposure = exposure,
                        AverageCubeColor = avg,
                    };
                }
                probes.Add(new LedProbe { Config = config, States = pstates });
            }

            int blendCount = (int)r.U32();
            r.Pos += blendCount * 584;
            var vista = r.Vec4List();
            if (lumpVersion > 2)
            {
                uint ext = r.U32();
                for (int i = 0; i < ext; i++)
                    r.Vec4List();
            }

            list.Add(new LedVolume
            {
                LightExportFlags = flags,
                LightStateMask = mask,
                ShadowSplitDistance = split,
                ShadowBiasScale = bias,
                TransitionTime = transition,
                StreamLighting = stream,
                WorldClipPlanes = clip,
                LightStates = states,
                LightStateInfo = infos,
                Probes = probes,
                ProbeBlendCount = blendCount,
                WorldVistaClipPlanes = vista,
            });
        }
        if (r.Pos != data.Length)
            throw new InvalidDataException($"{label}: volumes lump parsed {r.Pos} of {data.Length} bytes.");
        return list;
    }

    private sealed class Reader(byte[] data, string label)
    {
        public int Pos;

        private ReadOnlySpan<byte> Take(int n)
        {
            if (n < 0 || Pos + n > data.Length)
                throw new InvalidDataException($"{label}: read of {n} bytes at 0x{Pos:x} runs past the end.");
            var s = data.AsSpan(Pos, n);
            Pos += n;
            return s;
        }

        public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
        public float F32() => BinaryPrimitives.ReadSingleLittleEndian(Take(4));
        public byte U8() => Take(1)[0];
        public byte[] Bytes(int n) => Take(n).ToArray();
        public string Str() => Encoding.Latin1.GetString(Take((int)U32()));

        public byte[] Vec(int elementSize)
        {
            uint n = U32();
            return Take(checked((int)n * elementSize)).ToArray();
        }

        public uint[] U32Vec()
        {
            var b = Vec(4);
            var r = new uint[b.Length / 4];
            Buffer.BlockCopy(b, 0, r, 0, b.Length);
            return r;
        }

        public float[] Floats(int n)
        {
            var f = new float[n];
            for (int i = 0; i < n; i++) f[i] = F32();
            return f;
        }

        public List<Vector4> Vec4List()
        {
            uint n = U32();
            var l = new List<Vector4>((int)n);
            for (int i = 0; i < n; i++) l.Add(new Vector4(F32(), F32(), F32(), F32()));
            return l;
        }
    }
}
