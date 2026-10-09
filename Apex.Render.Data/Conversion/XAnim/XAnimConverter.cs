using System.Buffers.Binary;
using System.Text;
using Apex.Render.Data.Hashing;

namespace Apex.Render.Data.Conversion.XAnim;

/// <summary>The GDT xanim fields that drive conversion (and the anim half of the cache hash).</summary>
/// <param name="Type">GDT <c>type</c>, matched case-sensitively: absolute, relative, delta, delta3d, additive,
/// mp_torso, mp_legs, mp_fullbody.</param>
/// <param name="Looping">GDT <c>looping</c> != 0.</param>
/// <param name="UseBones">GDT <c>useBones</c> != 0.</param>
/// <param name="Node">GDT <c>node</c> (usually empty).</param>
public sealed record XAnimSettings(string Type, bool Looping, bool UseBones, string Node = "")
{
    /// <summary>Reads the fields from a GDT xanim entry (missing -> "", 0).</summary>
    public static XAnimSettings FromGdt(IReadOnlyDictionary<string, string> fields)
    {
        static bool B(IReadOnlyDictionary<string, string> f, string k) =>
            f.TryGetValue(k, out var v) && int.TryParse(v.Trim(), out var i) && i != 0;
        return new XAnimSettings(
            fields.GetValueOrDefault("type") ?? "",
            B(fields, "looping"),
            B(fields, "useBones"),
            fields.GetValueOrDefault("node") ?? "");
    }
}

/// <summary>APE would log an error and write no cache for this input.</summary>
public sealed class XAnimConversionException(string message) : Exception(message);

/// <summary>
/// Reimplementation of APE's <c>XAnim_Convert</c> (0x140635590): xanim_bin + target model skeleton ->
/// the decompressed ToolsGfx <c>xanims\v11</c> payload (conversion.md §3). Pure and thread-safe; never writes.
/// </summary>
public static class XAnimConverter
{
    private static readonly string[] TypeNames =
        ["absolute", "relative", "delta", "delta3d", "additive", "mp_torso", "mp_legs", "mp_fullbody"];

    /// <summary>Loads both source files and converts (APE loads the xmodel_bin bones-only).</summary>
    public static byte[] ConvertFiles(string xanimBinPath, string xmodelBinPath, XAnimSettings settings) =>
        Convert(XAnimSource.Load(xanimBinPath), XModelSkeleton.Load(xmodelBinPath), settings, xanimBinPath);

    /// <summary>
    /// Converts a parsed anim for one target model skeleton. Returns the exact decompressed v11 payload
    /// (wrap with <see cref="IO.Lz4Container.Compress"/> for the on-disk form).
    /// </summary>
    /// <exception cref="XAnimConversionException">Unknown type, 1-frame additive, bad delta root.</exception>
    public static byte[] Convert(XAnimSource anim, XModelSkeleton model, XAnimSettings settings, string? label = null)
    {
        label ??= "xanim";
        int typeIdx = Array.IndexOf(TypeNames, settings.Type);
        if (typeIdx < 0)
            throw new XAnimConversionException($"Unknown xanim type '{settings.Type}' in '{label}'");
        bool isDelta = typeIdx is 2 or 7, isDelta3d = typeIdx == 3;
        bool skipRoot = typeIdx is >= 1 and <= 4 or 7, additive = typeIdx == 4;
        bool looping = settings.Looping;

        var frames = anim.Frames;
        int count = frames.Length;
        int n = count;
        if (additive)
        {
            n--;
            if (n == 0)
                throw new XAnimConversionException($"Additive xanim '{label}' has only 1 frame.");
        }
        if (looping && n > 1)
            n--;
        int numSamples = looping ? n + 1 : n;

        var mb = model.Bones;
        int numModelBones = mb.Length;

        // Name maps (std::map: duplicates -> last index wins).
        var modelIdx = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int j = 0; j < numModelBones; j++)
            modelIdx[mb[j].Name] = j;
        var animIdx = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < anim.PartNames.Length; i++)
            animIdx[anim.PartNames[i]] = i;
        int nodePart = animIdx.TryGetValue(settings.Node ?? "", out var np) ? np : -1;

        var m2a = new int[numModelBones];
        Array.Fill(m2a, -1);
        for (int i = 0; i < anim.PartNames.Length; i++)
        {
            var name = anim.PartNames[i];
            if (name == "tag_origin_dummy")
            {
                if (numModelBones > 0) m2a[0] = i;
            }
            else if (modelIdx.TryGetValue(name, out var j))
            {
                m2a[j] = i;
            }
        }

        int start = 0, rootIndex = -1;
        if (skipRoot)
        {
            int roots = 0, last = -1;
            for (int j = 0; j < numModelBones; j++)
                if (m2a[j] >= 0 && mb[j].Parent < 0) { roots++; last = j; }
            if ((isDelta || isDelta3d) && roots != 1)
                throw new XAnimConversionException($"Not exactly one root bone for xanim '{label}' with delta or delta3d node.");
            rootIndex = last;
            start = last + 1;
        }

        // Delta (root motion).
        var deltaQ = Array.Empty<float>();
        var deltaT = Array.Empty<float>();
        if (isDelta || isDelta3d)
        {
            int rootPart = m2a[rootIndex];
            if (rootPart < 0)
                throw new XAnimConversionException($"Root part '{mb[rootIndex].Name}' in delta xanim '{label}' was not animated.");
            deltaQ = new float[4 * numSamples];
            deltaT = new float[3 * numSamples];
            Xf inv = default;
            if (nodePart < 0)
                inv = Inv(frames[0][rootPart]);
            for (int f = 0; f < n; f++)
            {
                if (nodePart >= 0)
                    inv = Inv(frames[f][nodePart]);
                ref readonly var root = ref frames[f][rootPart];
                XformPt(root.Ox, root.Oy, root.Oz, in inv, deltaT.AsSpan(3 * f));
                var m = Mul33(in root.M, in inv.R);
                MatToQuat(in m, deltaQ.AsSpan(4 * f));
            }
            if (looping)
                CopySample0(deltaQ, deltaT, null, n);
        }

        // Bones.
        var outBones = new List<(string Name, float[] Q, float[] T, float[] E)>();
        Span<float> b = stackalloc float[3];
        for (int j = start; j < numModelBones; j++)
        {
            int part = m2a[j];
            if (part < 0)
                continue;
            var q = new float[4 * numSamples];
            var t = new float[3 * numSamples];
            var e = new float[4 * numSamples];
            int parent = mb[j].Parent;
            int parentPart = parent >= 0 ? m2a[parent] : -1;
            if (additive)
                ConvertBoneAdditive(anim, in mb[j], part, parentPart, numSamples, q, t);
            else
                ConvertBone(anim, in mb[j], part, parentPart, numSamples, q, t, e);

            if (settings.UseBones && parent >= 0)
            {
                var pinv = Inv(mb[parent].Ox, mb[parent].Oy, mb[parent].Oz, in mb[parent].M);
                XformPt(mb[j].Ox, mb[j].Oy, mb[j].Oz, in pinv, b);
                for (int f = 0; f < n; f++)
                {
                    t[3 * f] = t[3 * f] - b[0];
                    t[3 * f + 1] = t[3 * f + 1] - b[1];
                    t[3 * f + 2] = t[3 * f + 2] - b[2];
                }
            }
            if (looping)
                CopySample0(q, t, e, n);
            outBones.Add((anim.PartNames[part], q, t, e));
        }

        // Serialise (ToolsGfx_XAnim_WriteCache 0x140634940; asset_caches.md §6.3).
        int size = 12 + 28 * (deltaQ.Length / 4) + 4 + 4 + anim.Notes.Count * 132;
        foreach (var bone in outBones)
            size += 128 + 4 + numSamples * (16 + 12 + 16);
        var buf = new byte[size];
        var w = new Writer(buf);
        w.F32((float)anim.Framerate);
        w.U32((uint)n);
        w.U32((uint)(deltaQ.Length / 4));
        w.Floats(deltaQ);
        w.Floats(deltaT);
        w.U32((uint)outBones.Count);
        foreach (var (name, q, t, e) in outBones)
        {
            w.Name(name);
            w.U32((uint)numSamples);
            w.Floats(q);
            w.Floats(t);
            w.Floats(e);
        }
        w.U32((uint)anim.Notes.Count);
        uint frame0 = anim.FrameNumbers[0];
        foreach (var note in anim.Notes)
        {
            w.U32(unchecked(note.Frame - frame0));
            w.Name(note.Name);
        }
        if (w.Pos != size)
            throw new InvalidOperationException("xanim payload size mismatch");
        return buf;
    }

    /// <summary>Cache file name: <c>&lt;anim&gt;_&lt;animHash&gt;_&lt;model&gt;_&lt;modelHash&gt;.lz4</c> (asset_caches.md §6.3).</summary>
    public static string CacheFileName(string animName, CacheHash animHash, string modelName, CacheHash modelHash) =>
        $"{animName}_{animHash.ToFileName()}_{modelName}_{modelHash.ToFileName()}.lz4";

    /// <summary>
    /// Path relative to <c>share\assetconvert</c>: <c>ToolsGfx\xanims\v11\&lt;anim&gt;\&lt;file&gt;</c>, hashes computed
    /// from the source files. <paramref name="modelName"/> defaults to the xmodel_bin file stem.
    /// </summary>
    public static string CacheRelativePath(string animName, string xanimBinPath, XAnimSettings settings,
        string xmodelBinPath, string? modelName = null)
    {
        var animHash = CacheKeys.XAnimHash(CacheKeys.FileSignature(xanimBinPath), settings.Type, settings.Looping,
            settings.UseBones, settings.Node);
        var modelHash = CacheKeys.FileSignature(xmodelBinPath);
        modelName ??= Path.GetFileNameWithoutExtension(xmodelBinPath);
        return Path.Combine("ToolsGfx", "xanims", "v" + ToolsGfxInstall.XAnimCacheVersion, animName,
            CacheFileName(animName, animHash, modelName, modelHash));
    }

    // ---------------------------------------------------------------------------------------------
    // Per-bone conversion

    /// <summary>XAnim_ConvertBone 0x140634D90: parent-relative transform per source frame.</summary>
    private static void ConvertBone(XAnimSource anim, in XModelSkeletonBone bone, int part, int parentPart,
        int numSamples, float[] q, float[] t, float[] e)
    {
        var frames = anim.Frames;
        int m = Math.Min(frames.Length, numSamples);
        for (int f = 0; f < m; f++)
        {
            ref readonly var c = ref frames[f][part];
            // Unanimated / missing parent: the bone's OWN bind world transform (sic).
            var inv = parentPart >= 0 ? Inv(frames[f][parentPart]) : Inv(bone.Ox, bone.Oy, bone.Oz, in bone.M);
            XformPt(c.Ox, c.Oy, c.Oz, in inv, t.AsSpan(3 * f));
            var lm = Mul33(in c.M, in inv.R);
            MatToQuat(in lm, q.AsSpan(4 * f));
            e[4 * f] = c.E0; e[4 * f + 1] = c.E1; e[4 * f + 2] = c.E2; e[4 * f + 3] = c.E3;
        }
    }

    /// <summary>XAnim_ConvertBoneAdditive 0x1406350B0: frames 1..N-1 relative to frame 0.</summary>
    private static void ConvertBoneAdditive(XAnimSource anim, in XModelSkeletonBone bone, int part, int parentPart,
        int numSamples, float[] q, float[] t)
    {
        var frames = anim.Frames;
        ref readonly var c0 = ref frames[0][part];
        var v38 = Inv(c0);
        var v39 = parentPart >= 0 ? World(frames[0][parentPart]) : new Xf(bone.M, bone.Ox, bone.Oy, bone.Oz);
        var bas = Mul43(in v39, in v38);
        var basT = Transpose(in bas.R);
        int m = Math.Min(frames.Length - 1, numSamples);
        for (int f = 1; f <= m; f++)
        {
            var v40 = World(frames[f][part]);
            var v37 = parentPart >= 0 ? Inv(frames[f][parentPart]) : Inv(bone.Ox, bone.Oy, bone.Oz, in bone.M);
            var v45 = Mul43(in v40, in v37);
            var v42 = Mul43(in v45, in bas);
            XformDir(v42.Tx, v42.Ty, v42.Tz, in basT, t.AsSpan(3 * (f - 1)));
            MatToQuat(in v42.R, q.AsSpan(4 * (f - 1)));
        }
    }

    private static void CopySample0(float[] q, float[] t, float[]? e, int n)
    {
        Array.Copy(q, 0, q, 4 * n, 4);
        Array.Copy(t, 0, t, 3 * n, 3);
        if (e is not null)
            Array.Copy(e, 0, e, 4 * n, 4);
    }

    // ---------------------------------------------------------------------------------------------
    // com_math.cpp helpers, reproduced with APE's exact float evaluation order.

    private readonly struct Xf(Mat3 r, float tx, float ty, float tz)
    {
        public readonly Mat3 R = r;
        public readonly float Tx = tx, Ty = ty, Tz = tz;
    }

    private static Xf World(in XAnimPartFrame p) => new(p.M, p.Ox, p.Oy, p.Oz);

    private static Xf Inv(in XAnimPartFrame p) => Inv(p.Ox, p.Oy, p.Oz, in p.M);

    /// <summary>transpose (0x1402B13B0) + <c>t = (-T) * Rt</c> (0x1402BD770 / inlined).</summary>
    private static Xf Inv(float ox, float oy, float oz, in Mat3 m)
    {
        var r = Transpose(in m);
        float x = ox * -1.0f, y = oy * -1.0f, z = oz * -1.0f;
        return new Xf(r,
            (x * r.M0 + y * r.M3) + z * r.M6,
            (x * r.M1 + y * r.M4) + z * r.M7,
            (x * r.M2 + y * r.M5) + z * r.M8);
    }

    private static Mat3 Transpose(in Mat3 a) => new()
    {
        M0 = a.M0, M1 = a.M3, M2 = a.M6,
        M3 = a.M1, M4 = a.M4, M5 = a.M7,
        M6 = a.M2, M7 = a.M5, M8 = a.M8,
    };

    /// <summary>0x1402B1FE0: <c>out = p * R + t</c>.</summary>
    private static void XformPt(float x, float y, float z, in Xf m, Span<float> o)
    {
        var r = m.R;
        o[0] = ((x * r.M0 + y * r.M3) + z * r.M6) + m.Tx;
        o[1] = ((x * r.M1 + y * r.M4) + z * r.M7) + m.Ty;
        o[2] = ((x * r.M2 + y * r.M5) + z * r.M8) + m.Tz;
    }

    /// <summary>0x1402BD770: <c>out = p * R</c>.</summary>
    private static void XformDir(float x, float y, float z, in Mat3 r, Span<float> o)
    {
        o[0] = (x * r.M0 + y * r.M3) + z * r.M6;
        o[1] = (x * r.M1 + y * r.M4) + z * r.M7;
        o[2] = (x * r.M2 + y * r.M5) + z * r.M8;
    }

    /// <summary>0x1402B02A0: 3x3 product <c>A * B</c>.</summary>
    private static Mat3 Mul33(in Mat3 a, in Mat3 b) => new()
    {
        M0 = (a.M0 * b.M0 + a.M1 * b.M3) + a.M2 * b.M6,
        M1 = (a.M0 * b.M1 + a.M1 * b.M4) + a.M2 * b.M7,
        M2 = (a.M0 * b.M2 + a.M1 * b.M5) + a.M2 * b.M8,
        M3 = (a.M3 * b.M0 + a.M4 * b.M3) + a.M5 * b.M6,
        M4 = (a.M3 * b.M1 + a.M4 * b.M4) + a.M5 * b.M7,
        M5 = (a.M3 * b.M2 + a.M4 * b.M5) + a.M5 * b.M8,
        M6 = (a.M6 * b.M0 + a.M7 * b.M3) + a.M8 * b.M6,
        M7 = (a.M6 * b.M1 + a.M7 * b.M4) + a.M8 * b.M7,
        M8 = (a.M6 * b.M2 + a.M7 * b.M5) + a.M8 * b.M8,
    };

    /// <summary>0x1402B0F30: 4x3 product <c>A * B</c>.</summary>
    private static Xf Mul43(in Xf a, in Xf b)
    {
        var r = Mul33(in a.R, in b.R);
        var br = b.R;
        return new Xf(r,
            ((a.Tx * br.M0 + a.Ty * br.M3) + a.Tz * br.M6) + b.Tx,
            ((a.Tx * br.M1 + a.Ty * br.M4) + a.Tz * br.M7) + b.Ty,
            ((a.Tx * br.M2 + a.Ty * br.M5) + a.Tz * br.M8) + b.Tz);
    }

    /// <summary>0x1402AFBD0 (AxisToQuat) followed by the caller's <c>w &lt; 0 -> negate</c>.</summary>
    private static void MatToQuat(in Mat3 m, Span<float> o)
    {
        float a = m.M5 - m.M7, b = m.M6 - m.M2, c = m.M1 - m.M3;
        float w0 = ((m.M0 + m.M4) + m.M8) + 1.0f;
        float r0 = a, r1 = b, r2 = c, r3 = w0;
        float x = ((a * a + b * b) + c * c) + w0 * w0;
        if (x < 1.0f)
        {
            float d = m.M2 + m.M6, e = m.M7 + m.M5, f1 = ((m.M8 - m.M4) - m.M0) + 1.0f;
            r0 = d; r1 = e; r2 = f1; r3 = c;
            x = ((d * d + e * e) + f1 * f1) + c * c;
            if (x < 1.0f)
            {
                float g = m.M3 + m.M1, h = ((m.M0 - m.M4) - m.M8) + 1.0f;
                r0 = h; r1 = g; r2 = d; r3 = a;
                x = ((h * h + g * g) + d * d) + a * a;
                if (x < 1.0f)
                {
                    float k = ((m.M4 - m.M0) - m.M8) + 1.0f;
                    r0 = g; r1 = k; r2 = e; r3 = b;
                    x = ((k * k + g * g) + e * e) + b * b;
                }
            }
        }
        float inv = 1.0f / MathF.Sqrt(x);
        float qx = inv * r0, qy = inv * r1, qz = inv * r2, qw = inv * r3;
        if (qw < 0.0f)
        {
            qx *= -1.0f; qy *= -1.0f; qz *= -1.0f; qw *= -1.0f;
        }
        o[0] = qx; o[1] = qy; o[2] = qz; o[3] = qw;
    }

    private ref struct Writer(byte[] buf)
    {
        private readonly Span<byte> _b = buf;
        public int Pos;

        public void U32(uint v) { BinaryPrimitives.WriteUInt32LittleEndian(_b[Pos..], v); Pos += 4; }
        public void F32(float v) { BinaryPrimitives.WriteSingleLittleEndian(_b[Pos..], v); Pos += 4; }

        public void Floats(float[] v)
        {
            foreach (var f in v) F32(f);
        }

        /// <summary>strncpy(dst, name, 128) into a zeroed 128-byte field.</summary>
        public void Name(string s)
        {
            var bytes = Encoding.Latin1.GetBytes(s);
            bytes.AsSpan(0, Math.Min(bytes.Length, 128)).CopyTo(_b[Pos..]);
            Pos += 128;
        }
    }
}
