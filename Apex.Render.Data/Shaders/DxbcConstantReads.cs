using System.Buffers.Binary;

namespace Apex.Render.Data.Shaders;

/// <summary>
/// Whether a shader's bytecode reads one component of a constant buffer. RDEF only says whether a top-level variable
/// is used, and cb9 is one struct (<c>gScene</c>), so this looks at the program itself (the <c>SHEX</c>/<c>SHDR</c>
/// chunk) for a constant-buffer operand naming that register and vec4 with the component selected.
/// </summary>
public static class DxbcConstantReads
{
    private const int OperandConstantBuffer = 8;

    /// <summary>
    /// True when <paramref name="dxbc"/> reads component <paramref name="component"/> (0 = x … 3 = w) of
    /// <c>cb<paramref name="register"/>[<paramref name="vec4"/>]</c> at an immediate index. False for a container
    /// without a program chunk.
    /// </summary>
    public static bool Reads(ReadOnlySpan<byte> dxbc, int register, int vec4, int component)
    {
        var program = ProgramChunk(dxbc);
        // Version and length tokens, then the instructions.
        for (int i = 8; i + 12 <= program.Length; i += 4)
        {
            uint token = BinaryPrimitives.ReadUInt32LittleEndian(program[i..]);
            if (!IsConstantBufferOperand(token)
                || BinaryPrimitives.ReadUInt32LittleEndian(program[(i + 4)..]) != (uint)register)
                continue;
            // cb[r][rN + k] indexes one of the buffer's arrays (lights, cascades), not a scalar such as time.
            if (((token >> 25) & 7) != 0)
                continue;
            if (BinaryPrimitives.ReadUInt32LittleEndian(program[(i + 8)..]) == (uint)vec4 && Selects(token, component))
                return true;
        }
        return false;
    }

    /// <summary>A 4-component, 2D-indexed constant-buffer operand whose register index is an immediate.</summary>
    private static bool IsConstantBufferOperand(uint token) =>
        (token & 3) == 2
        && ((token >> 12) & 0xFF) == OperandConstantBuffer
        && ((token >> 20) & 3) == 2
        && ((token >> 22) & 7) == 0;

    private static bool Selects(uint token, int component)
    {
        switch ((token >> 2) & 3)
        {
            case 0: // mask
                return (token & (1u << (4 + component))) != 0;
            case 1: // swizzle
                for (int lane = 0; lane < 4; lane++)
                    if (((token >> (4 + 2 * lane)) & 3) == component)
                        return true;
                return false;
            case 2: // select 1
                return ((token >> 4) & 3) == component;
            default:
                return false;
        }
    }

    private static ReadOnlySpan<byte> ProgramChunk(ReadOnlySpan<byte> dxbc)
    {
        if (dxbc.Length < 32)
            return default;
        int count = BinaryPrimitives.ReadInt32LittleEndian(dxbc[28..]);
        for (int i = 0; i < count && 32 + 4 * i + 4 <= dxbc.Length; i++)
        {
            int offset = BinaryPrimitives.ReadInt32LittleEndian(dxbc[(32 + 4 * i)..]);
            if (offset < 0 || offset + 8 > dxbc.Length)
                continue;
            var tag = dxbc.Slice(offset, 4);
            if (tag.SequenceEqual("SHEX"u8) || tag.SequenceEqual("SHDR"u8))
            {
                int size = BinaryPrimitives.ReadInt32LittleEndian(dxbc[(offset + 4)..]);
                return dxbc.Slice(offset + 8, Math.Min(size, dxbc.Length - offset - 8));
            }
        }
        return default;
    }
}
