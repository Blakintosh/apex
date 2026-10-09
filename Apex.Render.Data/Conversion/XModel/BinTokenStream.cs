using System.Buffers.Binary;
using System.Text;
using K4os.Compression.LZ4;

namespace Apex.Render.Data.Conversion.XModel;

/// <summary>Value layout of a <c>*_BIN</c> token.</summary>
internal enum BinTokenKind
{
    Comment, Section, BoneInfo, Short, UShortString, UShortStringX3, UShort, Int, UInt, Float,
    Vector2, Vector3, Vector316Bit, Vector4, Vector48Bit, BoneWeight, Tri, Tri16, UVSet,
}

/// <summary>
/// Reader of the binary export format (<c>*.xmodel_bin</c>): <c>"*LZ4*" u32 size</c> + one LZ4 block holding a
/// stream of 16-bit token hashes, each followed by its naturally aligned payload. Layout as in the
/// CallOfFile reader; the value conversions (16-bit vectors divided by 32767, 8-bit colours times 1/255)
/// follow APE's token readers (0x140054DB0 / 0x140055DA0) so floats round identically.
/// </summary>
internal sealed class BinTokenStream
{
    private static readonly Dictionary<ushort, (string Name, BinTokenKind Kind)> Tokens = new()
    {
        [0x8738] = (";", BinTokenKind.Comment),
        [0xC355] = ("//", BinTokenKind.Comment),
        [0x37FF] = ("AMBIENTCOLOR", BinTokenKind.Vector4),
        [0x7AAC] = ("ANIMATION", BinTokenKind.Section),
        [0x83C7] = ("BLINN", BinTokenKind.Vector2),
        [0xDD9A] = ("BONE", BinTokenKind.UShort),
        [0xF1AB] = ("BONE", BinTokenKind.BoneWeight),
        [0xF099] = ("BONE", BinTokenKind.BoneInfo),
        [0xEA46] = ("BONES", BinTokenKind.UShort),
        [0xC835] = ("COEFFS", BinTokenKind.Vector2),
        [0x6DD8] = ("COLOR", BinTokenKind.Vector48Bit),
        [0xBCD4] = ("FIRSTFRAME", BinTokenKind.UShort),
        [0xC723] = ("FRAME", BinTokenKind.UInt),
        [0x92D3] = ("FRAMERATE", BinTokenKind.UShort),
        [0xFE0C] = ("GLOW", BinTokenKind.Vector2),
        [0x4265] = ("INCANDESCENCE", BinTokenKind.Vector4),
        [0xA700] = ("MATERIAL", BinTokenKind.UShortStringX3),
        [0x46C8] = ("MODEL", BinTokenKind.Section),
        [0x89EC] = ("NORMAL", BinTokenKind.Vector316Bit),
        [0x4643] = ("NOTETRACK", BinTokenKind.UShort),
        [0xC7F3] = ("NOTETRACKS", BinTokenKind.Section),
        [0x76BA] = ("NUMBONES", BinTokenKind.UShort),
        [0xBE92] = ("NUMFACES", BinTokenKind.UInt),
        [0xB917] = ("NUMFRAMES", BinTokenKind.UInt),
        [0x7A6C] = ("NUMKEYS", BinTokenKind.UShort),
        [0xA1B2] = ("NUMMATERIALS", BinTokenKind.UShort),
        [0x62AF] = ("NUMOBJECTS", BinTokenKind.UShort),
        [0x9279] = ("NUMPARTS", BinTokenKind.UShort),
        [0x9016] = ("NUMTRACKS", BinTokenKind.UShort),
        [0x950D] = ("NUMVERTS", BinTokenKind.UShort),
        [0x2AEC] = ("NUMVERTS32", BinTokenKind.UInt),
        [0x87D4] = ("OBJECT", BinTokenKind.UShortString),
        [0x9383] = ("OFFSET", BinTokenKind.Vector3),
        [0x745A] = ("PART", BinTokenKind.UShort),
        [0x360B] = ("PART", BinTokenKind.UShortString),
        [0x5CD2] = ("PHONG", BinTokenKind.Float),
        [0x7D76] = ("REFLECTIVE", BinTokenKind.Vector2),
        [0xE593] = ("REFLECTIVECOLOR", BinTokenKind.Vector4),
        [0x7E24] = ("REFRACTIVE", BinTokenKind.Vector2),
        [0x1C56] = ("SCALE", BinTokenKind.Vector3),
        [0x317C] = ("SPECULARCOLOR", BinTokenKind.Vector4),
        [0x6DAB] = ("TRANSPARENCY", BinTokenKind.Vector4),
        [0x562F] = ("TRI", BinTokenKind.Tri),
        [0x6711] = ("TRI16", BinTokenKind.Tri16),
        [0x1AD4] = ("UV", BinTokenKind.UVSet),
        [0x24D1] = ("VERSION", BinTokenKind.UShort),
        [0x8F03] = ("VERT", BinTokenKind.UShort),
        [0xB097] = ("VERT32", BinTokenKind.UInt),
        [0xDCFD] = ("X", BinTokenKind.Vector316Bit),
        [0xCCDC] = ("Y", BinTokenKind.Vector316Bit),
        [0xFCBF] = ("Z", BinTokenKind.Vector316Bit),
        [0x1FC2] = ("NUMSBONES", BinTokenKind.Int),
        [0xB35E] = ("NUMSWEIGHTS", BinTokenKind.Int),
        [0xEF69] = ("QUATERNION", BinTokenKind.Vector4),
        [0xA65B] = ("NUMIKPITCHLAYERS", BinTokenKind.Int),
        [0x1D7D] = ("IKPITCHLAYER", BinTokenKind.UInt),
        [0xA58B] = ("ROTATION", BinTokenKind.Vector3),
        [0x7836] = ("NUMCOSMETICBONES", BinTokenKind.Int),
        [0x6EEE] = ("EXTRA", BinTokenKind.Vector4),
    };

    /// <summary><see cref="Tokens"/> flattened: hash -> 1 + index into <see cref="TokenInfos"/>, 0 when unknown.</summary>
    private static readonly byte[] TokenSlots = new byte[65536];
    private static readonly (string Name, BinTokenKind Kind)[] TokenInfos = BuildTokenTable();

    private static (string, BinTokenKind)[] BuildTokenTable()
    {
        var infos = new List<(string, BinTokenKind)>();
        foreach (var (hash, info) in Tokens)
        {
            infos.Add(info);
            TokenSlots[hash] = checked((byte)infos.Count);
        }
        return [.. infos];
    }

    private readonly byte[] _buf;
    private int _pos;
    private readonly string _label;

    private BinTokenStream(byte[] buf, string label)
    {
        _buf = buf;
        _label = label;
    }

    /// <summary>Opens a <c>*_BIN</c> file: <c>"*LZ4*"</c>, <c>u32 size</c>, LZ4 block.</summary>
    public static BinTokenStream Open(string path)
    {
        var raw = File.ReadAllBytes(path);
        if (raw.Length < 9 || raw[0] != (byte)'*' || raw[1] != (byte)'L' || raw[2] != (byte)'Z' || raw[3] != (byte)'4' || raw[4] != (byte)'*')
            throw new InvalidDataException($"Not an LZ4 *_BIN file: {path}");
        int size = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(5));
        var data = new byte[size];
        int got = LZ4Codec.Decode(raw.AsSpan(9), data);
        if (got != size)
            throw new InvalidDataException($"*_BIN block decoded to {got} bytes, header says {size}: {path}");
        return new BinTokenStream(data, path);
    }

    /// <summary>A decoded token.</summary>
    public readonly struct Token
    {
        public string Name { get; init; }
        public BinTokenKind Kind { get; init; }
        public int I0 { get; init; }
        public int I1 { get; init; }
        public float F0 { get; init; }
        public float F1 { get; init; }
        public float F2 { get; init; }
        public float F3 { get; init; }
        public string? S0 { get; init; }
    }

    // The peeked token lives in a field so the hot paths hand it out by reference instead of copying it around.
    private Token _token;
    private PeekState _state;

    private enum PeekState : byte { None, Token, End }

    private bool Fill()
    {
        if (_state == PeekState.None)
            _state = ReadRaw(out _token) ? PeekState.Token : PeekState.End;
        return _state == PeekState.Token;
    }

    /// <summary>Next non-comment token without consuming it; null at end of stream.</summary>
    public Token? Peek() => Fill() ? _token : null;

    /// <summary>Consumes the next non-comment token; null at end of stream.</summary>
    public Token? Next()
    {
        var t = Peek();
        _state = PeekState.None;
        return t;
    }

    /// <summary>
    /// Consumes a token that must have <paramref name="name"/> (and, when given, <paramref name="kind"/>). The reference
    /// is valid until the stream is read again.
    /// </summary>
    public ref readonly Token Expect(string name, BinTokenKind? kind = null)
    {
        if (!Fill() || _token.Name != name || (kind is { } k && _token.Kind != k))
            throw new InvalidDataException($"{_label}: expecting {name} token, found {(_state == PeekState.Token ? _token.Name : "end of file")}.");
        _state = PeekState.None;
        return ref _token;
    }

    /// <summary>True when the next token is <paramref name="name"/> (optionally of <paramref name="kind"/>).</summary>
    public bool NextIs(string name, BinTokenKind? kind = null) =>
        Fill() && _token.Name == name && (kind is not { } k || _token.Kind == k);

    /// <summary>Undecoded bytes left; bounds counts read from the file before sizing collections by them.</summary>
    public int Remaining => _buf.Length - _pos;

    private void Align(int n) => _pos = (_pos + n - 1) & ~(n - 1);

    private ushort U16() { var v = BinaryPrimitives.ReadUInt16LittleEndian(_buf.AsSpan(_pos)); _pos += 2; return v; }
    private short S16() { var v = BinaryPrimitives.ReadInt16LittleEndian(_buf.AsSpan(_pos)); _pos += 2; return v; }
    private int I32() { var v = BinaryPrimitives.ReadInt32LittleEndian(_buf.AsSpan(_pos)); _pos += 4; return v; }
    private float F32() { var v = BinaryPrimitives.ReadSingleLittleEndian(_buf.AsSpan(_pos)); _pos += 4; return v; }
    private byte U8() => _buf[_pos++];

    private string Str()
    {
        int start = _pos;
        while (_buf[_pos] != 0) _pos++;
        var s = Encoding.Latin1.GetString(_buf, start, _pos - start);
        _pos++;
        Align(4);
        return s;
    }

    private bool ReadRaw(out Token token)
    {
        for (;;)
        {
            Align(4);
            if (_pos + 2 > _buf.Length)
            {
                token = default;
                return false;
            }
            ushort hash = U16();
            int slot = TokenSlots[hash];
            if (slot == 0)
                throw new InvalidDataException($"{_label}: unknown token 0x{hash:X4} at {_pos - 2}.");
            var (name, kind) = TokenInfos[slot - 1];
            switch (kind)
            {
                case BinTokenKind.Comment:
                    Align(4);
                    Str();
                    continue;
                case BinTokenKind.Section:
                    token = new Token { Name = name, Kind = kind };
                    return true;
                case BinTokenKind.BoneInfo:
                {
                    Align(4);
                    int a = I32(), b = I32();
                    token = new Token { Name = name, Kind = kind, I0 = a, I1 = b, S0 = Str() };
                    return true;
                }
                case BinTokenKind.Short:
                    Align(2);
                    token = new Token { Name = name, Kind = kind, I0 = S16() };
                    return true;
                case BinTokenKind.UShortString:
                {
                    Align(2);
                    int a = U16();
                    token = new Token { Name = name, Kind = kind, I0 = a, S0 = Str() };
                    return true;
                }
                case BinTokenKind.UShortStringX3:
                {
                    Align(2);
                    int a = U16();
                    var s = Str();
                    Str();
                    Str();
                    token = new Token { Name = name, Kind = kind, I0 = a, S0 = s };
                    return true;
                }
                case BinTokenKind.UShort:
                    Align(2);
                    token = new Token { Name = name, Kind = kind, I0 = U16() };
                    return true;
                case BinTokenKind.Int:
                case BinTokenKind.UInt:
                    Align(4);
                    token = new Token { Name = name, Kind = kind, I0 = I32() };
                    return true;
                case BinTokenKind.Float:
                    Align(4);
                    token = new Token { Name = name, Kind = kind, F0 = F32() };
                    return true;
                case BinTokenKind.Vector2:
                {
                    Align(4);
                    float x = F32(), y = F32();
                    token = new Token { Name = name, Kind = kind, F0 = x, F1 = y };
                    return true;
                }
                case BinTokenKind.Vector3:
                {
                    Align(4);
                    float x = F32(), y = F32(), z = F32();
                    token = new Token { Name = name, Kind = kind, F0 = x, F1 = y, F2 = z };
                    return true;
                }
                case BinTokenKind.Vector316Bit:
                {
                    Align(2);
                    // APE: cvtdq2ps + divss 32767.0 (not a multiply by the reciprocal).
                    float x = S16() / 32767.0f, y = S16() / 32767.0f, z = S16() / 32767.0f;
                    token = new Token { Name = name, Kind = kind, F0 = x, F1 = y, F2 = z };
                    return true;
                }
                case BinTokenKind.Vector4:
                {
                    Align(4);
                    float x = F32(), y = F32(), z = F32(), w = F32();
                    token = new Token { Name = name, Kind = kind, F0 = x, F1 = y, F2 = z, F3 = w };
                    return true;
                }
                case BinTokenKind.Vector48Bit:
                {
                    Align(4);
                    const float k = 0.0039215689f;
                    float r = U8() * k, g = U8() * k, b = U8() * k, a = U8() * k;
                    token = new Token { Name = name, Kind = kind, F0 = r, F1 = g, F2 = b, F3 = a };
                    return true;
                }
                case BinTokenKind.BoneWeight:
                {
                    Align(2);
                    int bone = U16();
                    float w = F32();
                    token = new Token { Name = name, Kind = kind, I0 = bone, F0 = w };
                    return true;
                }
                case BinTokenKind.Tri:
                {
                    int o = U8(), m = U8();
                    token = new Token { Name = name, Kind = kind, I0 = o, I1 = m };
                    return true;
                }
                case BinTokenKind.Tri16:
                {
                    Align(4);
                    int o = U16(), m = U16();
                    token = new Token { Name = name, Kind = kind, I0 = o, I1 = m };
                    return true;
                }
                case BinTokenKind.UVSet:
                {
                    int n = U16();
                    float u = 0, v = 0;
                    for (int i = 0; i < n; i++)
                    {
                        float a = F32(), b = F32();
                        if (i == 0) { u = a; v = b; }
                    }
                    token = new Token { Name = name, Kind = kind, I0 = n, F0 = u, F1 = v };
                    return true;
                }
                default:
                    throw new InvalidDataException($"{_label}: unsupported token kind {kind}.");
            }
        }
    }
}
