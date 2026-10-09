using System.Buffers.Binary;
using System.Text;

namespace Apex.Render.Data.Shaders;

/// <summary><c>D3D_SHADER_INPUT_TYPE</c> of a bound resource (numeric values match D3D).</summary>
public enum ShaderInputType
{
    CBuffer = 0,
    TBuffer = 1,
    Texture = 2,
    Sampler = 3,
    UavRwTyped = 4,
    Structured = 5,
    UavRwStructured = 6,
    ByteAddress = 7,
    UavRwByteAddress = 8,
    UavAppendStructured = 9,
    UavConsumeStructured = 10,
    UavRwStructuredWithCounter = 11,
}

/// <summary><c>D3D_SRV_DIMENSION</c> of a bound resource (numeric values match D3D).</summary>
public enum ShaderResourceDimension
{
    Unknown = 0,
    Buffer = 1,
    Texture1D = 2,
    Texture1DArray = 3,
    Texture2D = 4,
    Texture2DArray = 5,
    Texture2DMS = 6,
    Texture2DMSArray = 7,
    Texture3D = 8,
    TextureCube = 9,
    TextureCubeArray = 10,
    BufferEx = 11,
}

/// <summary><c>D3D_SHADER_VARIABLE_TYPE</c> subset (numeric values match D3D).</summary>
public enum ShaderVariableType
{
    Void = 0,
    Bool = 1,
    Int = 2,
    Float = 3,
    UInt = 19,
    Double = 39,
}

/// <summary>A resource binding from the RDEF chunk.</summary>
public sealed record ShaderResourceBinding(
    string Name,
    ShaderInputType Type,
    ShaderResourceDimension Dimension,
    int BindPoint,
    int BindCount,
    uint Flags,
    int ReturnType,
    int NumSamples);

/// <summary>A constant-buffer variable. <see cref="IsUsed"/> is <c>D3D_SVF_USED</c>.</summary>
public sealed record ShaderVariable(
    string Name,
    int StartOffset,
    int Size,
    bool IsUsed,
    int TypeClass,
    ShaderVariableType Type,
    int Rows,
    int Columns,
    int Elements);

/// <summary>A constant buffer and its variables.</summary>
public sealed record ShaderConstantBuffer(string Name, int Size, int Type, IReadOnlyList<ShaderVariable> Variables)
{
    public ShaderVariable? Find(string name) => Variables.FirstOrDefault(v => v.Name == name);
}

/// <summary>An input/output signature element (<c>ISGN</c>/<c>OSGN</c>).</summary>
public sealed record ShaderSignatureElement(
    string SemanticName,
    int SemanticIndex,
    int SystemValue,
    int ComponentType,
    int Register,
    byte Mask,
    byte ReadWriteMask);

/// <summary>
/// Managed reader for the parts of a DXBC container Apex needs (RDEF, ISGN/OSGN): resource bindings,
/// constant buffer layouts with the used flags, and signatures. Equivalent to what APE gets from
/// <c>D3DReflect</c> (materials.md §3), but without d3dcompiler.
/// </summary>
public sealed class DxbcReflection
{
    private DxbcReflection() { }

    public int MajorVersion { get; private init; }
    public int MinorVersion { get; private init; }

    /// <summary>D3D program type from RDEF (0xFFFF pixel, 0xFFFE vertex, 0x4353 compute, ...).</summary>
    public int ProgramType { get; private init; }

    public IReadOnlyList<ShaderResourceBinding> Bindings { get; private init; } = [];
    public IReadOnlyList<ShaderConstantBuffer> ConstantBuffers { get; private init; } = [];
    public IReadOnlyList<ShaderSignatureElement> Inputs { get; private init; } = [];
    public IReadOnlyList<ShaderSignatureElement> Outputs { get; private init; } = [];

    /// <summary>The binding named <paramref name="name"/>, if bound.</summary>
    public ShaderResourceBinding? FindBinding(string name) => Bindings.FirstOrDefault(b => b.Name == name);

    /// <summary>The constant buffer named <paramref name="name"/> (e.g. <c>$Globals</c>).</summary>
    public ShaderConstantBuffer? FindConstantBuffer(string name) => ConstantBuffers.FirstOrDefault(c => c.Name == name);

    /// <summary>Parses a DXBC container.</summary>
    /// <exception cref="InvalidDataException">Not a DXBC container or a malformed chunk.</exception>
    public static DxbcReflection Parse(ReadOnlySpan<byte> dxbc)
    {
        if (dxbc.Length < 32 || dxbc[0] != 'D' || dxbc[1] != 'X' || dxbc[2] != 'B' || dxbc[3] != 'C')
            throw new InvalidDataException("Not a DXBC container.");

        int chunkCount = BinaryPrimitives.ReadInt32LittleEndian(dxbc[28..]);
        ReadOnlySpan<byte> rdef = default, isgn = default, osgn = default;
        for (int i = 0; i < chunkCount; i++)
        {
            int off = BinaryPrimitives.ReadInt32LittleEndian(dxbc[(32 + 4 * i)..]);
            var tag = Encoding.ASCII.GetString(dxbc.Slice(off, 4));
            int size = BinaryPrimitives.ReadInt32LittleEndian(dxbc[(off + 4)..]);
            var body = dxbc.Slice(off + 8, size);
            switch (tag)
            {
                case "RDEF": rdef = body; break;
                case "ISGN": isgn = body; break;
                case "OSGN": case "OSG5": osgn = body; break;
            }
        }

        if (rdef.IsEmpty)
            throw new InvalidDataException("DXBC container has no RDEF chunk.");

        int cbCount = I32(rdef, 0), cbOffset = I32(rdef, 4), bindCount = I32(rdef, 8), bindOffset = I32(rdef, 12);
        int minor = rdef[16], major = rdef[17];
        int programType = BinaryPrimitives.ReadUInt16LittleEndian(rdef[18..]);
        bool sm5 = major >= 5;

        var bindings = new List<ShaderResourceBinding>(bindCount);
        for (int i = 0; i < bindCount; i++)
        {
            var b = rdef[(bindOffset + 32 * i)..];
            bindings.Add(new ShaderResourceBinding(
                Str(rdef, I32(b, 0)),
                (ShaderInputType)I32(b, 4),
                (ShaderResourceDimension)I32(b, 12),
                I32(b, 20),
                I32(b, 24),
                (uint)I32(b, 28),
                I32(b, 8),
                I32(b, 16)));
        }

        int varStride = sm5 ? 40 : 24;
        var cbuffers = new List<ShaderConstantBuffer>(cbCount);
        for (int i = 0; i < cbCount; i++)
        {
            var c = rdef[(cbOffset + 24 * i)..];
            int varCount = I32(c, 4), varOffset = I32(c, 8);
            var vars = new List<ShaderVariable>(varCount);
            for (int v = 0; v < varCount; v++)
            {
                var d = rdef[(varOffset + varStride * v)..];
                int typeOff = I32(d, 16);
                var t = rdef[typeOff..];
                vars.Add(new ShaderVariable(
                    Str(rdef, I32(d, 0)),
                    I32(d, 4),
                    I32(d, 8),
                    (I32(d, 12) & 2) != 0,
                    BinaryPrimitives.ReadUInt16LittleEndian(t),
                    (ShaderVariableType)BinaryPrimitives.ReadUInt16LittleEndian(t[2..]),
                    BinaryPrimitives.ReadUInt16LittleEndian(t[4..]),
                    BinaryPrimitives.ReadUInt16LittleEndian(t[6..]),
                    BinaryPrimitives.ReadUInt16LittleEndian(t[8..])));
            }
            cbuffers.Add(new ShaderConstantBuffer(Str(rdef, I32(c, 0)), I32(c, 12), I32(c, 20), vars));
        }

        return new DxbcReflection
        {
            MajorVersion = major,
            MinorVersion = minor,
            ProgramType = programType,
            Bindings = bindings,
            ConstantBuffers = cbuffers,
            Inputs = isgn.IsEmpty ? [] : ParseSignature(isgn),
            Outputs = osgn.IsEmpty ? [] : ParseSignature(osgn),
        };
    }

    private static List<ShaderSignatureElement> ParseSignature(ReadOnlySpan<byte> chunk)
    {
        int count = I32(chunk, 0);
        var list = new List<ShaderSignatureElement>(count);
        for (int i = 0; i < count; i++)
        {
            var e = chunk[(8 + 24 * i)..];
            list.Add(new ShaderSignatureElement(
                Str(chunk, I32(e, 0)),
                I32(e, 4),
                I32(e, 8),
                I32(e, 12),
                I32(e, 16),
                e[20],
                e[21]));
        }
        return list;
    }

    private static int I32(ReadOnlySpan<byte> s, int offset) => BinaryPrimitives.ReadInt32LittleEndian(s[offset..]);

    private static string Str(ReadOnlySpan<byte> s, int offset)
    {
        var tail = s[offset..];
        int z = tail.IndexOf((byte)0);
        return Encoding.ASCII.GetString(z < 0 ? tail : tail[..z]);
    }
}
