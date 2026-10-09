using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Hashing;
using System.Linq;
using System.Text;

namespace Apex.Editor.Services.Save;

/// <summary>
/// The version of each GDT from before every save, outside the install: <c>%LOCALAPPDATA%\Apex\backups\</c>, one folder
/// per file, newest <see cref="Keep"/> kept. Any save can be undone this way, even after Apex has closed.
/// <c>APEX_BACKUP_DIR</c> moves it (tests keep it in their temp folder).
/// </summary>
public sealed class BackupStore
{
    public const int DefaultKeep = 10;

    public BackupStore(string root, int keep = DefaultKeep)
    {
        Root = root;
        Keep = keep;
    }

    public string Root { get; }
    public int Keep { get; }

    public static string DefaultRoot =>
        Environment.GetEnvironmentVariable("APEX_BACKUP_DIR") is { Length: > 0 } dir
            ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Apex", "backups");

    /// <summary>
    /// The folder holding <paramref name="gdtPath"/>'s backups: its file name plus a hash of its full path (a <c>.gdtx</c>
    /// keeps its extension in the name, so it isn't mistaken for its GDT's folder).
    /// </summary>
    public string FolderFor(string gdtPath)
    {
        var full = Path.GetFullPath(gdtPath);
        var hash = XxHash64.HashToUInt64(Encoding.UTF8.GetBytes(full.ToLowerInvariant()));
        var name = IsSidecar(full) ? Path.GetFileName(full) : Path.GetFileNameWithoutExtension(full);
        return Path.Combine(Root, $"{name}-{hash:x16}");
    }

    /// <summary>Writes <paramref name="bytes"/> as the newest backup of <paramref name="gdtPath"/>, flushed to disk, and prunes old ones.</summary>
    public string Store(string gdtPath, ReadOnlySpan<byte> bytes)
    {
        var folder = FolderFor(gdtPath);
        WriteGuard.Check(Path.Combine(folder, "x"));
        Directory.CreateDirectory(folder);
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff");
        var ext = ExtensionOf(gdtPath);
        var path = Path.Combine(folder, $"{stamp}{ext}");
        for (var n = 2; File.Exists(path); n++)
            path = Path.Combine(folder, $"{stamp}-{n}{ext}");
        WriteGuard.Check(path);
        using (var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            fs.Write(bytes);
            fs.Flush(flushToDisk: true);
        }
        File.WriteAllText(Path.Combine(folder, "source.txt"), Path.GetFullPath(gdtPath));
        Prune(folder, ext);
        return path;
    }

    /// <summary><paramref name="gdtPath"/>'s backups, newest first.</summary>
    public IReadOnlyList<string> List(string gdtPath)
    {
        var folder = FolderFor(gdtPath);
        if (!Directory.Exists(folder))
            return Array.Empty<string>();
        return Directory.GetFiles(folder, "*" + ExtensionOf(gdtPath)).OrderByDescending(Path.GetFileName, StringComparer.Ordinal).ToList();
    }

    private static bool IsSidecar(string path) => path.EndsWith(Models.ExtensionSidecar.Extension, StringComparison.OrdinalIgnoreCase);

    /// <summary>A backup has its source's extension, so a copy taken by hand opens as what it is.</summary>
    private static string ExtensionOf(string path) => IsSidecar(path) ? Models.ExtensionSidecar.Extension : ".gdt";

    private void Prune(string folder, string ext)
    {
        foreach (var old in Directory.GetFiles(folder, "*" + ext).OrderByDescending(Path.GetFileName, StringComparer.Ordinal).Skip(Keep))
        {
            WriteGuard.Check(old);
            try { File.Delete(old); }
            catch (IOException) { /* next save prunes it */ }
        }
    }
}
