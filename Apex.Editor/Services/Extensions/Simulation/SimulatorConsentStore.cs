using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Apex.Editor.Services.Extensions.Simulation;

/// <summary>
/// The user's answers about modules: per extension id and SHA-256 of the file, Load or Don't load. Kept in
/// <c>extension-modules.dat</c> in <see cref="UiSettings.LocalFolder"/> (%LocalAppData%\Apex, or APEX_SETTINGS_DIR),
/// not %AppData%\Apex, which holds the extensions folder an archive could be unpacked over; and sealed with DPAPI for
/// the current Windows user, so answers can't be supplied by dropping a file. Read on first use, never at startup. A
/// missing, unreadable or unsealable file means nothing was answered, so the user is asked again: the safe direction.
/// In runs that keep no files the answers live for the session only.
/// </summary>
public sealed class SimulatorConsentStore
{
    public const string FileName = "extension-modules.dat";

    /// <summary>Answers kept per extension: enough to switch between a few builds without being asked each time.</summary>
    private const int KeptPerExtension = 8;

    private readonly string? _path;
    private List<Entry>? _entries;

    /// <param name="path">The file; null keeps answers in memory only.</param>
    public SimulatorConsentStore(string? path) => _path = path;

    /// <summary>The user's own store, or a memory-only one in runs that keep no files (the harness without APEX_SETTINGS_DIR).</summary>
    public static SimulatorConsentStore ForUser() =>
        new(UiSettings.LocalFolder is { } dir ? Path.Combine(dir, FileName) : null);

    public string? FilePath => _path;

    private sealed class Entry
    {
        public string Id { get; set; } = "";
        public string Sha256 { get; set; } = "";
        public bool Load { get; set; }
        public string File { get; set; } = "";
        public long? Size { get; set; }
        public DateTime? ModifiedUtc { get; set; }
        public DateTime AnsweredUtc { get; set; }
    }

    private sealed class Document
    {
        public List<Entry> Modules { get; set; } = new();
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private List<Entry> Entries
    {
        get
        {
            if (_entries is not null)
                return _entries;
            _entries = new List<Entry>();
            if (_path is null)
                return _entries;
            try
            {
                if (System.IO.File.Exists(_path) && Dpapi.Unprotect(System.IO.File.ReadAllBytes(_path)) is { } json
                    && JsonSerializer.Deserialize<Document>(json, Json)?.Modules is { } modules)
                    _entries = modules.Where(e => e is { Id.Length: > 0, Sha256.Length: 64 }).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            {
                // Unreadable: as if never answered. The next answer writes a good file over it.
            }
            return _entries;
        }
    }

    /// <summary>True (Load), false (Don't load), or null when this build of the module hasn't been answered.</summary>
    public bool? AnswerFor(string id, string sha256) =>
        Entries.FirstOrDefault(e => Same(e, id, sha256))?.Load;

    /// <summary>The other build of this extension's module answered for most recently: the question then says what changed.</summary>
    public SimulatorModuleBuild? LastOtherBuild(string id, string sha256) =>
        Entries.Where(e => e.Id.Equals(id, StringComparison.OrdinalIgnoreCase) && !e.Sha256.Equals(sha256, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => e.AnsweredUtc).Select(e => new SimulatorModuleBuild(e.Sha256, e.Size, e.ModifiedUtc, e.AnsweredUtc)).FirstOrDefault();

    public void Remember(string id, string sha256, bool load, string file, long? size = null, DateTime? modifiedUtc = null)
    {
        var entries = Entries;
        entries.RemoveAll(e => Same(e, id, sha256));
        entries.Add(new Entry { Id = id, Sha256 = sha256.ToLowerInvariant(), Load = load, File = file, Size = size, ModifiedUtc = modifiedUtc, AnsweredUtc = DateTime.UtcNow });
        var mine = entries.Where(e => e.Id.Equals(id, StringComparison.OrdinalIgnoreCase)).OrderByDescending(e => e.AnsweredUtc).ToList();
        foreach (var old in mine.Skip(KeptPerExtension))
            entries.Remove(old);
        Write();
    }

    /// <summary>Drops the answer for this build, so the next ask asks the user again.</summary>
    public void Forget(string id, string sha256)
    {
        if (Entries.RemoveAll(e => Same(e, id, sha256)) > 0)
            Write();
    }

    private static bool Same(Entry e, string id, string sha256) =>
        e.Id.Equals(id, StringComparison.OrdinalIgnoreCase) && e.Sha256.Equals(sha256, StringComparison.OrdinalIgnoreCase);

    /// <summary>Written whole and swapped in, so a crash mid-write leaves the old answers, never half a file.</summary>
    private void Write()
    {
        if (_path is null)
            return;
        try
        {
            if (Dpapi.Protect(JsonSerializer.SerializeToUtf8Bytes(new Document { Modules = _entries! }, Json)) is not { } sealedBytes)
                return;
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            System.IO.File.WriteAllBytes(temp, sealedBytes);
            System.IO.File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not kept: the answer still holds for this session, and the user is asked again next time.
        }
    }

    /// <summary>
    /// CryptProtectData for the current user, with Apex's own entropy: only this user, through Apex, makes a file that
    /// reads back. (Code already running as the user could forge one; nothing unpacked from an archive can.)
    /// </summary>
    private static unsafe class Dpapi
    {
        private const int UiForbidden = 0x1;

        private static readonly byte[] Entropy = "Apex preview-module answers v1"u8.ToArray();

        [StructLayout(LayoutKind.Sequential)]
        private struct Blob
        {
            public int Size;
            public byte* Data;
        }

        [DllImport("crypt32.dll", SetLastError = true, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptProtectData(Blob* data, char* description, Blob* entropy, nint reserved, nint prompt, int flags, Blob* result);

        [DllImport("crypt32.dll", SetLastError = true, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptUnprotectData(Blob* data, char** description, Blob* entropy, nint reserved, nint prompt, int flags, Blob* result);

        [DllImport("kernel32.dll", ExactSpelling = true)]
        private static extern nint LocalFree(nint memory);

        public static byte[]? Protect(byte[] plain) => Run(plain, protect: true);

        public static byte[]? Unprotect(byte[] sealedBytes) => Run(sealedBytes, protect: false);

        private static byte[]? Run(byte[] input, bool protect)
        {
            fixed (byte* p = input, e = Entropy)
            {
                var data = new Blob { Size = input.Length, Data = p };
                var entropy = new Blob { Size = Entropy.Length, Data = e };
                var result = default(Blob);
                var ok = protect
                    ? CryptProtectData(&data, null, &entropy, 0, 0, UiForbidden, &result)
                    : CryptUnprotectData(&data, null, &entropy, 0, 0, UiForbidden, &result);
                if (!ok)
                    return null;
                try
                {
                    return new ReadOnlySpan<byte>(result.Data, result.Size).ToArray();
                }
                finally
                {
                    LocalFree((nint)result.Data);
                }
            }
        }
    }
}
