using System.Buffers.Binary;
using System.Text;
using K4os.Compression.LZ4;

namespace Apex.Render.Data.Conversion.XAnim;

/// <summary>
/// Minimal sequential reader for the Call of Duty <c>*_BIN</c> token format (xmodel_bin / xanim_bin):
/// <c>"*LZ4*" u32 size</c> + one raw LZ4 block, then a stream of tokens, each starting 4-aligned with a
/// u16 CRC-16 hash that encodes (name, data type). Token table and layouts follow CallOfFile
/// (<c>ThirdParty\CallOfFile</c>), which APE uses in the same order (token ids = table indices).
/// </summary>
internal sealed class BinTokenReader
{
    internal enum DataType
    {
        Comment, Section, Short, UShort, UInt, Int, Vector48Bit, Vector316Bit, Float, Vector2, Vector3, Vector4,
        BoneWeight, UVSet, UShortString, UShortStringX3, FrameNote, BoneInfo, Tri, Tri16,
    }

    internal readonly record struct TokenDef(string Name, DataType Type);

    private static readonly Dictionary<ushort, TokenDef> Tokens = new()
    {
        [0x8738] = new(";", DataType.Comment),
        [0xC355] = new("//", DataType.Comment),
        [0x37FF] = new("AMBIENTCOLOR", DataType.Vector4),
        [0x7AAC] = new("ANIMATION", DataType.Section),
        [0x83C7] = new("BLINN", DataType.Vector2),
        [0xDD9A] = new("BONE", DataType.UShort),
        [0xF1AB] = new("BONE", DataType.BoneWeight),
        [0xF099] = new("BONE", DataType.BoneInfo),
        [0xEA46] = new("BONES", DataType.UShort),
        [0xC835] = new("COEFFS", DataType.Vector2),
        [0x6DD8] = new("COLOR", DataType.Vector48Bit),
        [0xBCD4] = new("FIRSTFRAME", DataType.UShort),
        [0xC723] = new("FRAME", DataType.UInt),
        [0x1675] = new("FRAME", DataType.FrameNote),
        [0x92D3] = new("FRAMERATE", DataType.UShort),
        [0xFE0C] = new("GLOW", DataType.Vector2),
        [0x4265] = new("INCANDESCENCE", DataType.Vector4),
        [0xA700] = new("MATERIAL", DataType.UShortStringX3),
        [0x46C8] = new("MODEL", DataType.Section),
        [0x89EC] = new("NORMAL", DataType.Vector316Bit),
        [0x4643] = new("NOTETRACK", DataType.UShort),
        [0xC7F3] = new("NOTETRACKS", DataType.Section),
        [0x76BA] = new("NUMBONES", DataType.UShort),
        [0xBE92] = new("NUMFACES", DataType.UInt),
        [0xB917] = new("NUMFRAMES", DataType.UInt),
        [0x7A6C] = new("NUMKEYS", DataType.UShort),
        [0xA1B2] = new("NUMMATERIALS", DataType.UShort),
        [0x62AF] = new("NUMOBJECTS", DataType.UShort),
        [0x9279] = new("NUMPARTS", DataType.UShort),
        [0x9016] = new("NUMTRACKS", DataType.UShort),
        [0x950D] = new("NUMVERTS", DataType.UShort),
        [0x2AEC] = new("NUMVERTS32", DataType.UInt),
        [0x87D4] = new("OBJECT", DataType.UShortString),
        [0x9383] = new("OFFSET", DataType.Vector3),
        [0x745A] = new("PART", DataType.UShort),
        [0x360B] = new("PART", DataType.UShortString),
        [0x5CD2] = new("PHONG", DataType.Float),
        [0x7D76] = new("REFLECTIVE", DataType.Vector2),
        [0xE593] = new("REFLECTIVECOLOR", DataType.Vector4),
        [0x7E24] = new("REFRACTIVE", DataType.Vector2),
        [0x1C56] = new("SCALE", DataType.Vector3),
        [0x317C] = new("SPECULARCOLOR", DataType.Vector4),
        [0x6DAB] = new("TRANSPARENCY", DataType.Vector4),
        [0x562F] = new("TRI", DataType.Tri),
        [0x6711] = new("TRI16", DataType.Tri16),
        [0x1AD4] = new("UV", DataType.UVSet),
        [0x24D1] = new("VERSION", DataType.UShort),
        [0x8F03] = new("VERT", DataType.UShort),
        [0xB097] = new("VERT32", DataType.UInt),
        [0xDCFD] = new("X", DataType.Vector316Bit),
        [0xCCDC] = new("Y", DataType.Vector316Bit),
        [0xFCBF] = new("Z", DataType.Vector316Bit),
        [0x1FC2] = new("NUMSBONES", DataType.Int),
        [0xB35E] = new("NUMSWEIGHTS", DataType.Int),
        [0xEF69] = new("QUATERNION", DataType.Vector4),
        [0xA65B] = new("NUMIKPITCHLAYERS", DataType.Int),
        [0x1D7D] = new("IKPITCHLAYER", DataType.UInt),
        [0xA58B] = new("ROTATION", DataType.Vector3),
        [0x7836] = new("NUMCOSMETICBONES", DataType.Int),
        [0x6EEE] = new("EXTRA", DataType.Vector4),
    };

    /// <summary><see cref="Tokens"/> flattened: hash -> 1 + index into <see cref="TokenDefs"/>, 0 when unknown.</summary>
    private static readonly byte[] TokenSlots = new byte[65536];
    private static readonly TokenDef[] TokenDefs = BuildTokenTable();

    private static TokenDef[] BuildTokenTable()
    {
        var defs = new List<TokenDef>();
        foreach (var (hash, def) in Tokens)
        {
            defs.Add(def);
            TokenSlots[hash] = checked((byte)defs.Count);
        }
        return [.. defs];
    }

    private readonly byte[] _buf;
    private int _pos;
    private readonly string _label;

    public BinTokenReader(byte[] decompressed, string label)
    {
        _buf = decompressed;
        _label = label;
    }

    /// <summary>Reads and decompresses a <c>*_BIN</c> file (<c>"*LZ4*"</c>, u32 size, raw LZ4 block).</summary>
    public static byte[] ReadFile(string path)
    {
        var raw = File.ReadAllBytes(path);
        if (raw.Length < 9 || Encoding.ASCII.GetString(raw, 0, 5) != "*LZ4*")
            throw new InvalidDataException($"Not an LZ4 *_BIN file: {path}");
        int size = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(5));
        var output = new byte[size];
        int got = LZ4Codec.Decode(raw.AsSpan(9), output);
        if (got != size)
            throw new InvalidDataException($"*_BIN LZ4 block decoded to {got} bytes, header says {size}: {path}");
        return output;
    }

    /// <summary>The next non-comment token's (name, type) without consuming it, or null at end of data.</summary>
    public TokenDef? Peek()
    {
        SkipComments();
        int p = Align(_pos, 4);
        if (p + 2 > _buf.Length)
            return null;
        return Lookup(BinaryPrimitives.ReadUInt16LittleEndian(_buf.AsSpan(p)), p);
    }

    public bool PeekIs(string name, DataType type) => Peek() is { } t && t.Name == name && t.Type == type;

    /// <summary>Consumes the next token, which must be (name, type); positions the reader at its data.</summary>
    public void Expect(string name, DataType type)
    {
        var t = Peek() ?? throw Error($"Expecting {name} token, found end of data");
        if (t.Name != name || t.Type != type)
            throw Error($"Expecting {name} token, found {t.Name}");
        _pos = Align(_pos, 4) + 2;
    }

    /// <summary>Skips whole tokens until (name, type) is next; false at end of data.</summary>
    public bool SkipTo(string name, DataType type)
    {
        while (Peek() is { } t)
        {
            if (t.Name == name && t.Type == type)
                return true;
            _pos = Align(_pos, 4) + 2;
            SkipData(t.Type);
        }
        return false;
    }

    public void Section(string name) => Expect(name, DataType.Section);

    public ushort UShort(string name)
    {
        Expect(name, DataType.UShort);
        return ReadU16();
    }

    public uint UInt(string name)
    {
        Expect(name, DataType.UInt);
        _pos = Align(_pos, 4);
        return ReadU32();
    }

    public int Int(string name)
    {
        Expect(name, DataType.Int);
        _pos = Align(_pos, 4);
        return (int)ReadU32();
    }

    public void Vector3(string name, Span<float> dst)
    {
        Expect(name, DataType.Vector3);
        _pos = Align(_pos, 4);
        for (int i = 0; i < 3; i++) dst[i] = ReadF32();
    }

    public void Vector4(string name, Span<float> dst)
    {
        Expect(name, DataType.Vector4);
        _pos = Align(_pos, 4);
        for (int i = 0; i < 4; i++) dst[i] = ReadF32();
    }

    /// <summary>A 3 x i16 vector; APE dequantises with <c>(float)s / 32767.0f</c> (divss).</summary>
    public void Vector316(string name, Span<float> dst)
    {
        Expect(name, DataType.Vector316Bit);
        _pos = Align(_pos, 2);
        for (int i = 0; i < 3; i++)
        {
            short s = (short)ReadU16();
            dst[i] = (float)s / 32767.0f;
        }
    }

    public (ushort Index, string Name) UShortString(string name)
    {
        Expect(name, DataType.UShortString);
        var idx = ReadU16();
        return (idx, ReadString());
    }

    public (int Index, int Parent, string Name) BoneInfo(string name)
    {
        Expect(name, DataType.BoneInfo);
        _pos = Align(_pos, 4);
        int idx = (int)ReadU32();
        int parent = (int)ReadU32();
        return (idx, parent, ReadString());
    }

    public (uint Frame, string Name) FrameNote(string name)
    {
        Expect(name, DataType.FrameNote);
        _pos = Align(_pos, 4);
        uint frame = ReadU32();
        return (frame, ReadString());
    }

    private TokenDef Lookup(ushort hash, int at) =>
        TokenSlots[hash] is var slot and > 0 ? TokenDefs[slot - 1] : throw Error($"Unrecognized token hash 0x{hash:X4} @ {at}");

    private void SkipComments()
    {
        while (true)
        {
            int p = Align(_pos, 4);
            if (p + 2 > _buf.Length)
                return;
            var t = Lookup(BinaryPrimitives.ReadUInt16LittleEndian(_buf.AsSpan(p)), p);
            if (t.Type != DataType.Comment)
                return;
            _pos = p + 2;
            SkipData(DataType.Comment);
        }
    }

    private void SkipData(DataType type)
    {
        switch (type)
        {
            case DataType.Comment: _pos = Align(_pos, 4); ReadString(); break;
            case DataType.Section: break;
            case DataType.BoneInfo: _pos = Align(_pos, 4); _pos += 8; ReadString(); break;
            case DataType.Short or DataType.UShort: _pos = Align(_pos, 2) + 2; break;
            case DataType.UShortString: _pos = Align(_pos, 2) + 2; ReadString(); break;
            case DataType.UShortStringX3: _pos = Align(_pos, 2) + 2; ReadString(); ReadString(); ReadString(); break;
            case DataType.Int or DataType.UInt or DataType.Float: _pos = Align(_pos, 4) + 4; break;
            case DataType.Vector2: _pos = Align(_pos, 4) + 8; break;
            case DataType.Vector3: _pos = Align(_pos, 4) + 12; break;
            case DataType.Vector4: _pos = Align(_pos, 4) + 16; break;
            case DataType.Vector316Bit: _pos = Align(_pos, 2) + 6; break;
            case DataType.Vector48Bit: _pos = Align(_pos, 4) + 4; break;
            case DataType.BoneWeight: _pos = Align(_pos, 2) + 6; break;
            case DataType.Tri: _pos += 2; break;
            case DataType.Tri16: _pos = Align(_pos, 4) + 4; break;
            case DataType.UVSet: { int n = ReadU16(); _pos += 8 * n; break; }
            case DataType.FrameNote: _pos = Align(_pos, 4) + 4; ReadString(); break;
            default: throw Error($"Cannot skip token data type {type}");
        }
    }

    private static int Align(int p, int a) => (p + a - 1) & ~(a - 1);

    private ushort ReadU16()
    {
        if (_pos + 2 > _buf.Length) throw Error("Unexpected end of data");
        var v = BinaryPrimitives.ReadUInt16LittleEndian(_buf.AsSpan(_pos));
        _pos += 2;
        return v;
    }

    private uint ReadU32()
    {
        if (_pos + 4 > _buf.Length) throw Error("Unexpected end of data");
        var v = BinaryPrimitives.ReadUInt32LittleEndian(_buf.AsSpan(_pos));
        _pos += 4;
        return v;
    }

    private float ReadF32() => BitConverter.UInt32BitsToSingle(ReadU32());

    /// <summary>NUL-terminated string, then aligned to 4 (APE reads it in 4-byte chunks).</summary>
    private string ReadString()
    {
        int end = Array.IndexOf(_buf, (byte)0, _pos);
        if (end < 0) throw Error("Unterminated string");
        var s = Encoding.Latin1.GetString(_buf, _pos, end - _pos);
        _pos = Align(end + 1, 4);
        return s;
    }

    private InvalidDataException Error(string msg) => new($"{msg} ({_label})");
}
