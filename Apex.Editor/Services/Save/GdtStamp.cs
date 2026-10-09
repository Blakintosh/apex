using System;
using System.IO;
using System.IO.Hashing;

namespace Apex.Editor.Services.Save;

/// <summary>
/// What a GDT held when Apex last read or wrote it: size, last-write time and a content hash. The hash decides;
/// size and time only make the "nothing changed" answer cheap to explain in a message.
/// </summary>
public readonly record struct GdtStamp(long Length, DateTime LastWriteUtc, UInt128 Hash)
{
    public static UInt128 HashOf(ReadOnlySpan<byte> bytes) => XxHash128.HashToUInt128(bytes);

    public static GdtStamp Of(ReadOnlySpan<byte> bytes, DateTime lastWriteUtc) => new(bytes.Length, lastWriteUtc, HashOf(bytes));

    /// <summary>Same content. Time is not compared: a copy or a checkout can restore identical bytes with a new time.</summary>
    public bool SameContent(GdtStamp other) => Length == other.Length && Hash == other.Hash;

    /// <summary>Reads the file (sharing with everyone, never locking) and stamps it.</summary>
    public static GdtStamp Read(string path, out byte[] bytes)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
        bytes = ReadAll(fs);
        return Of(bytes, File.GetLastWriteTimeUtc(fs.SafeFileHandle));
    }

    /// <summary>Reads a whole stream from its start.</summary>
    internal static byte[] ReadAll(FileStream fs)
    {
        fs.Position = 0;
        var length = fs.Length;
        if (length > Array.MaxLength)
            throw new IOException("The file is too large to be a GDT.");
        var bytes = new byte[length];
        var read = 0;
        while (read < bytes.Length)
        {
            var r = fs.Read(bytes, read, bytes.Length - read);
            if (r <= 0)
                throw new IOException("The file got shorter while it was being read.");
            read += r;
        }
        return bytes;
    }
}
