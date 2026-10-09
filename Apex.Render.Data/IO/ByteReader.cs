using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace Apex.Render.Data.IO;

/// <summary>Bounds-checked little-endian cursor over a decompressed cache payload.</summary>
internal sealed class ByteReader
{
    private readonly byte[] _data;
    private readonly string _label;

    public ByteReader(byte[] data, string label)
    {
        _data = data;
        _label = label;
    }

    public int Position { get; set; }
    public int Length => _data.Length;
    public int Remaining => _data.Length - Position;
    public byte[] Data => _data;

    private ReadOnlySpan<byte> Take(int count)
    {
        if (count < 0 || Position + count > _data.Length)
            throw new InvalidDataException($"{_label}: read of {count} bytes at 0x{Position:x} runs past the end (length {_data.Length}).");
        var span = _data.AsSpan(Position, count);
        Position += count;
        return span;
    }

    public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
    public int I32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));
    public float F32() => BinaryPrimitives.ReadSingleLittleEndian(Take(4));
    public byte U8() => Take(1)[0];

    /// <summary>A u32 element count, sanity-checked against the bytes left.</summary>
    public int Count(int elementSize)
    {
        uint n = U32();
        if (elementSize > 0 && n > (uint)(Remaining / elementSize))
            throw new InvalidDataException($"{_label}: count {n} x {elementSize} B at 0x{Position - 4:x} exceeds the remaining {Remaining} bytes.");
        return (int)n;
    }

    /// <summary>A fixed-size, NUL-padded ASCII string field.</summary>
    public string FixedString(int size)
    {
        var s = Take(size);
        int z = s.IndexOf((byte)0);
        return Encoding.ASCII.GetString(z < 0 ? s : s[..z]);
    }

    public byte[] Bytes(int count) => Take(count).ToArray();

    public ReadOnlySpan<byte> Span(int count) => Take(count);

    public T[] Array<T>(int count) where T : unmanaged
    {
        var bytes = Take(checked(count * Marshal.SizeOf<T>()));
        return MemoryMarshal.Cast<byte, T>(bytes).ToArray();
    }

    public void Skip(int count) => Take(count);

    public void ExpectEnd()
    {
        if (Position != _data.Length)
            throw new InvalidDataException($"{_label}: parsed {Position} of {_data.Length} bytes; layout mismatch.");
    }
}
