using System.Buffers.Binary;
using K4os.Compression.LZ4;

namespace Apex.Render.Data.IO;

/// <summary>
/// Every <c>*.lz4</c> under <c>share\assetconvert\ToolsGfx</c> is <c>u32 uncompressedSize</c> followed by
/// one raw LZ4 block (no frame header). The payload is a plain little-endian stream with no magic or
/// version field; the version lives in the directory name.
/// </summary>
public static class Lz4Container
{
    /// <summary>Reads and decompresses a ToolsGfx cache file.</summary>
    /// <exception cref="InvalidDataException">The file is truncated or the block does not decode to the stated size.</exception>
    public static byte[] ReadFile(string path)
    {
        var raw = File.ReadAllBytes(path);
        return Decompress(raw, path);
    }

    /// <summary>Decompresses an in-memory container.</summary>
    public static byte[] Decompress(ReadOnlySpan<byte> raw, string? label = null)
    {
        if (raw.Length < 4)
            throw new InvalidDataException($"LZ4 container too short: {label}");
        int size = BinaryPrimitives.ReadInt32LittleEndian(raw);
        if (size < 0)
            throw new InvalidDataException($"LZ4 container has a negative size: {label}");
        if (raw.Length == 4 && size > 0)
            throw new InvalidDataException($"Truncated cache file (header only, {size} bytes expected; an interrupted APE write): {label}");
        var output = new byte[size];
        int got = LZ4Codec.Decode(raw[4..], output);
        if (got != size)
            throw new InvalidDataException($"LZ4 block decoded to {got} bytes, header says {size}: {label}");
        return output;
    }

    /// <summary>Compresses a payload into the container format (<c>u32 size</c> + one raw LZ4 block).</summary>
    public static byte[] Compress(ReadOnlySpan<byte> payload) => CompressUntrimmed(payload, out int length).AsSpan(0, length).ToArray();

    /// <summary>The container in a buffer sized for the worst case; the first <paramref name="length"/> bytes are it.</summary>
    private static byte[] CompressUntrimmed(ReadOnlySpan<byte> payload, out int length)
    {
        var buf = new byte[4 + LZ4Codec.MaximumOutputSize(payload.Length)];
        BinaryPrimitives.WriteInt32LittleEndian(buf, payload.Length);
        int n = LZ4Codec.Encode(payload, buf.AsSpan(4));
        if (n < 0 && payload.Length > 0)
            throw new InvalidOperationException("LZ4 compression failed.");
        length = 4 + Math.Max(n, 0);
        return buf;
    }

    /// <summary>
    /// Writes a container atomically (temp file + move) so a crash never leaves a truncated cache entry
    /// that a reader would mistake for a real one.
    /// </summary>
    public static void WriteFile(string path, ReadOnlySpan<byte> payload)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + "." + Environment.ProcessId + "." + Environment.CurrentManagedThreadId + ".tmp";
        var buf = CompressUntrimmed(payload, out int length);
        File.WriteAllBytes(tmp, buf.AsSpan(0, length));
        File.Move(tmp, path, overwrite: true);
    }
}
