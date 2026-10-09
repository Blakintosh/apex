using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace Apex.Editor.Services.Save;

/// <summary>One GDT to save: where it is, what Apex last read there, and the edits.</summary>
public sealed class GdtSaveRequest
{
    public required string Path { get; init; }

    /// <summary>How messages name the file ("zm_weapons.gdt").</summary>
    public string DisplayName => System.IO.Path.GetFileName(Path);

    /// <summary>The file as Apex last read or wrote it; null for a GDT the session created (the file must not exist yet).</summary>
    public GdtStamp? Expected { get; init; }

    public IReadOnlyList<AssetEdit> Edits { get; init; } = Array.Empty<AssetEdit>();

    /// <summary>Restoring a backup: the whole new content, instead of edits.</summary>
    public byte[]? Replacement { get; init; }

    /// <summary>The caller's object for this file, carried through to the result.</summary>
    public object? Tag { get; init; }

    /// <summary>
    /// A <c>.gdtx</c> (see <see cref="Models.ExtensionSidecar"/>): its blocks are told apart by asset and extension id,
    /// and when the edits leave it with no blocks the file is removed (after a backup) instead of kept as an empty shell.
    /// </summary>
    public bool Sidecar { get; init; }

    public bool IsNewFile => Expected is null;
}

public enum GdtSaveStatus
{
    /// <summary>Still in progress (in a returned result only when a step threw).</summary>
    Pending,
    /// <summary>Written and verified.</summary>
    Saved,
    /// <summary>The edits leave the file as it is; nothing was written.</summary>
    Unchanged,
    /// <summary>The file changed on disk since Apex read it (or a new GDT's file exists). Nothing written.</summary>
    Conflict,
    /// <summary>Another program has the file open for writing. Nothing written.</summary>
    Locked,
    /// <summary>The file is read-only. Nothing written.</summary>
    ReadOnly,
    /// <summary>An edit can't be written (a value a GDT can't hold, …). Nothing written.</summary>
    Invalid,
    /// <summary>Something went wrong; see the message. Whether the file was replaced is in <see cref="GdtSaveFileResult.Replaced"/>.</summary>
    Failed,
    /// <summary>Not attempted because another file in the same save couldn't be saved.</summary>
    NotWritten,
}

public sealed class GdtSaveFileResult
{
    public GdtSaveFileResult(GdtSaveRequest request) => Request = request;

    public GdtSaveRequest Request { get; }
    public GdtSaveStatus Status { get; internal set; } = GdtSaveStatus.Pending;

    /// <summary>One plain sentence for the user.</summary>
    public string Message { get; internal set; } = "";

    /// <summary>Technical detail for a tooltip or log.</summary>
    public string? Detail { get; internal set; }

    /// <summary>The file after the save (Saved), or as it is on disk now (Conflict), for the next save to expect.</summary>
    public GdtStamp? Stamp { get; internal set; }

    /// <summary>Where the version from before this save was kept.</summary>
    public string? BackupPath { get; internal set; }

    /// <summary>True once the file on disk was replaced (even if the check after it then failed).</summary>
    public bool Replaced { get; internal set; }

    /// <summary>A sidecar left with no blocks was removed from disk (Saved, with no stamp: there is no file now).</summary>
    public bool Removed { get; internal set; }

    /// <summary>A conflict because the file changed on disk since Apex read it.</summary>
    public bool ChangedOnDisk { get; internal set; }

    public List<AssetConflict> Conflicts { get; } = new();
    public List<string> Problems { get; } = new();

    /// <summary>The splice that was written (layout and where each asset landed), for rebinding the session to the new file.</summary>
    public SpliceResult? Splice { get; internal set; }

    public bool Succeeded => Status is GdtSaveStatus.Saved or GdtSaveStatus.Unchanged;
}

public sealed class GdtSaveResult
{
    public List<GdtSaveFileResult> Files { get; } = new();
    public bool AllSucceeded => Files.All(f => f.Succeeded);
    public IEnumerable<GdtSaveFileResult> Saved => Files.Where(f => f.Status == GdtSaveStatus.Saved);
}

/// <summary>
/// Test seams in the save sequence. Each runs on the saving thread at its point in the sequence; a throw from one
/// behaves like the process dying there (locks are released, nothing is cleaned up).
/// </summary>
public sealed class GdtSaveHooks
{
    /// <summary>After every file's preflight, before any lock.</summary>
    public Action<string>? AfterPreflight { get; set; }

    /// <summary>After a file is staged (backup and temp file written), right before its swap.</summary>
    public Action<string>? BeforeSwap { get; set; }

    /// <summary>Right after a file's swap, before its verification.</summary>
    public Action<string>? AfterSwap { get; set; }
}

/// <summary>
/// Saves GDTs in place, file by file, as docs/save-path-design.md lays out: preflight (stamp) → lock (FileShare.Read)
/// → re-check under the lock → splice → stage (backup, temp file, flush, read back) → swap (File.Replace, retried
/// briefly) → verify → report. Every file is checked, locked, built and staged before any is swapped, so a save that
/// can't go through writes nothing. Thread-agnostic and UI-free: the app calls it off the UI thread.
/// </summary>
public sealed class GdtSaveService
{
    public const string TempSuffix = ".apex-tmp";

    private readonly BackupStore _backups;

    public GdtSaveService(BackupStore backups) => _backups = backups;

    public GdtSaveHooks Hooks { get; } = new();

    /// <summary>How long a swap keeps retrying while another program (antivirus, indexer) briefly holds the file.</summary>
    public TimeSpan SwapRetryBudget { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Tests: run the whole sequence (stage, swap, verify) even when the edits leave the bytes as they are.</summary>
    public bool WriteEvenIfUnchanged { get; set; }

    public GdtSaveResult Save(IReadOnlyList<GdtSaveRequest> requests)
    {
        foreach (var r in requests)
        {
            WriteGuard.Check(r.Path);
            WriteGuard.Check(r.Path + TempSuffix);
            WriteGuard.Check(Path.Combine(_backups.FolderFor(r.Path), "x"));
        }
        var result = new GdtSaveResult();
        var files = requests.Select(r => new FileWork(r, new GdtSaveFileResult(r))).ToList();
        result.Files.AddRange(files.Select(f => f.Result));
        try
        {
            foreach (var f in files)
                Preflight(f);
            foreach (var f in files)
                Hooks.AfterPreflight?.Invoke(f.Request.Path);
            if (Abandon(files))
                return result;

            foreach (var f in files)
                if (!Lock(f))
                    break;
            if (Abandon(files))
                return result;

            foreach (var f in files)
                ReCheckUnderLock(f);
            if (Abandon(files))
                return result;

            foreach (var f in files)
                Build(f);
            if (Abandon(files))
                return result;

            var toWrite = files.Where(f => f.Result.Status != GdtSaveStatus.Unchanged).ToList();
            foreach (var f in toWrite)
            {
                if (Stage(f))
                    continue;
                foreach (var g in toWrite)
                    DeleteTemp(g);
                Abandon(files);
                return result;
            }

            for (var i = 0; i < toWrite.Count; i++)
            {
                var f = toWrite[i];
                Hooks.BeforeSwap?.Invoke(f.Request.Path);
                var swapped = Swap(f);
                if (swapped)
                {
                    Hooks.AfterSwap?.Invoke(f.Request.Path);
                    VerifyAfterSwap(f);
                }
                // A file that didn't end up as staged stops the rest: requests come in an order (extension data before
                // its GDT) that a later file must not get ahead of.
                if (swapped && f.Result.Status == GdtSaveStatus.Saved)
                    continue;
                for (var j = i + 1; j < toWrite.Count; j++)
                {
                    DeleteTemp(toWrite[j]);
                    toWrite[j].Result.Status = GdtSaveStatus.NotWritten;
                    toWrite[j].Result.Message = $"{toWrite[j].Request.DisplayName} wasn't saved because {f.Request.DisplayName} couldn't be.";
                }
                break;
            }
            return result;
        }
        finally
        {
            foreach (var f in files)
                f.Lock?.Dispose();
        }
    }

    // ── Steps ───────────────────────────────────────────────────────────────

    private sealed class FileWork(GdtSaveRequest request, GdtSaveFileResult result)
    {
        public GdtSaveRequest Request { get; } = request;
        public GdtSaveFileResult Result { get; } = result;
        public GdtStamp? Seen;
        public FileStream? Lock;
        public byte[]? Original;
        public byte[]? Output;

        /// <summary>A sidecar the edits leave empty: removed rather than written.</summary>
        public bool Remove;

        public string Temp => Request.Path + TempSuffix;
    }

    /// <summary>1. No lock yet: the file must be as Apex last read it, writable, and (new GDT) not there yet.</summary>
    private static void Preflight(FileWork f)
    {
        var r = f.Result;
        var name = f.Request.DisplayName;
        try
        {
            if (f.Request.IsNewFile)
            {
                if (File.Exists(f.Request.Path))
                {
                    r.Status = GdtSaveStatus.Conflict;
                    r.Message = $"{name} already exists on disk, so Apex didn't overwrite it with the new {(f.Request.Sidecar ? "extension data" : "GDT")}.";
                    // Extension data another program wrote meanwhile can be read in and merged, as for a changed file.
                    r.ChangedOnDisk = f.Request.Sidecar;
                }
                return;
            }
            if (!File.Exists(f.Request.Path))
            {
                r.Status = GdtSaveStatus.Conflict;
                r.ChangedOnDisk = true;
                r.Message = $"{name} was deleted or moved since Apex read it.";
                return;
            }
            if (File.GetAttributes(f.Request.Path).HasFlag(FileAttributes.ReadOnly))
            {
                r.Status = GdtSaveStatus.ReadOnly;
                r.Message = $"{name} is read-only. Clear its read-only attribute (or check it out) and save again.";
                return;
            }
            f.Seen = GdtStamp.Read(f.Request.Path, out var bytes);
            if (!f.Seen.Value.SameContent(f.Request.Expected!.Value))
                MarkChanged(f, bytes, f.Seen.Value);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Fail(r, $"Apex couldn't read {name}.", ex);
        }
    }

    /// <summary>2. Nobody else may write while Apex builds: open with FileShare.Read (a writer already there means a sharing violation).</summary>
    private static bool Lock(FileWork f)
    {
        if (f.Request.IsNewFile)
            return true;
        try
        {
            f.Lock = new FileStream(f.Request.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan);
            return true;
        }
        catch (IOException ex) when (IsSharingViolation(ex))
        {
            f.Result.Status = GdtSaveStatus.Locked;
            f.Result.Message = $"{f.Request.DisplayName} is open in another program. Close it there (or wait for the build to finish) and save again.";
            f.Result.Detail = ex.Message;
        }
        catch (UnauthorizedAccessException ex)
        {
            f.Result.Status = GdtSaveStatus.ReadOnly;
            f.Result.Message = $"Windows doesn't allow Apex to open {f.Request.DisplayName}.";
            f.Result.Detail = ex.Message;
        }
        catch (IOException ex)
        {
            Fail(f.Result, $"Apex couldn't open {f.Request.DisplayName}.", ex);
        }
        return false;
    }

    /// <summary>3. Read through the locked handle: the bytes the splice builds on, and proof nothing changed since the preflight.</summary>
    private static void ReCheckUnderLock(FileWork f)
    {
        if (f.Request.IsNewFile)
        {
            f.Original = GdtSplicer.EmptyFile;
            return;
        }
        try
        {
            var bytes = GdtStamp.ReadAll(f.Lock!);
            var now = GdtStamp.Of(bytes, File.GetLastWriteTimeUtc(f.Lock!.SafeFileHandle));
            if (!now.SameContent(f.Seen!.Value) || !now.SameContent(f.Request.Expected!.Value))
            {
                MarkChanged(f, bytes, now);
                return;
            }
            f.Original = bytes;
        }
        catch (IOException ex)
        {
            Fail(f.Result, $"Apex couldn't read {f.Request.DisplayName}.", ex);
        }
    }

    /// <summary>4. Splice the edits into the bytes read under the lock.</summary>
    private void Build(FileWork f)
    {
        if (f.Request.Replacement is { } replacement)
        {
            f.Output = replacement;
            if (!f.Request.IsNewFile && !WriteEvenIfUnchanged && replacement.AsSpan().SequenceEqual(f.Original))
                f.Result.Status = GdtSaveStatus.Unchanged;
            return;
        }
        try
        {
            var splice = GdtSplicer.Splice(f.Original!, f.Request.Edits, f.Request.Sidecar);
            f.Result.Splice = splice;
            if (splice.Problems.Count > 0)
            {
                f.Result.Status = GdtSaveStatus.Invalid;
                f.Result.Problems.AddRange(splice.Problems);
                f.Result.Message = splice.Problems[0];
                return;
            }
            if (splice.Conflicts.Count > 0)
            {
                f.Result.Status = GdtSaveStatus.Conflict;
                f.Result.Conflicts.AddRange(splice.Conflicts);
                f.Result.Message = $"{f.Request.DisplayName} has changes on disk that overlap yours.";
                return;
            }
            f.Output = splice.Bytes;
            if (f.Request.Sidecar && splice.Layout!.Assets.Count == 0 && GdtSplicer.IsEmptyShell(splice.Bytes))
            {
                // No extension data left: no file, rather than an empty one. A new one is simply never created.
                if (f.Request.IsNewFile)
                    f.Result.Status = GdtSaveStatus.Unchanged;
                else
                    f.Remove = true;
                return;
            }
            if (!splice.Changed && !f.Request.IsNewFile && !WriteEvenIfUnchanged)
            {
                f.Result.Status = GdtSaveStatus.Unchanged;
                f.Result.Stamp = f.Seen;
            }
        }
        catch (SpliceVerificationException ex)
        {
            Fail(f.Result, $"Apex stopped saving {f.Request.DisplayName}: its check of the new file failed. Nothing was written.", ex);
        }
    }

    /// <summary>5. Backup of the original, then the new bytes to a temp file beside it, flushed and read back (a sidecar being removed: the backup only).</summary>
    private bool Stage(FileWork f)
    {
        var name = f.Request.DisplayName;
        try
        {
            DeleteTemp(f); // a crash between stage and swap leaves one; the original was never touched
            if (!f.Request.IsNewFile)
                f.Result.BackupPath = _backups.Store(f.Request.Path, f.Original);
            else
            {
                var dir = Path.GetDirectoryName(f.Request.Path)!;
                WriteGuard.Check(Path.Combine(dir, "x"));
                Directory.CreateDirectory(dir);
            }
            if (f.Remove)
                return true;

            WriteGuard.Check(f.Temp);
            using (var fs = new FileStream(f.Temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                fs.Write(f.Output);
                fs.Flush(flushToDisk: true);
            }
            byte[] back;
            using (var fs = new FileStream(f.Temp, FileMode.Open, FileAccess.Read, FileShare.Read))
                back = GdtStamp.ReadAll(fs);
            if (!back.AsSpan().SequenceEqual(f.Output))
            {
                Fail(f.Result, $"Apex stopped saving {name}: the temporary copy didn't read back the same. Nothing was written.", null);
                return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Fail(f.Result, $"Apex couldn't prepare {name} for saving (disk full or no access?). Nothing was written.", ex);
            return false;
        }
    }

    /// <summary>6. Release the lock and atomically replace the file with the staged one, retrying briefly.</summary>
    private bool Swap(FileWork f)
    {
        f.Lock?.Dispose();
        f.Lock = null;
        var path = f.Request.Path;
        WriteGuard.Check(path);
        var deadline = DateTime.UtcNow + SwapRetryBudget;
        var wait = 25;
        while (true)
        {
            try
            {
                // Removing: the file is moved aside (atomic, like a replace) and only deleted once the check after
                // the swap proves it is what Apex read.
                if (f.Remove)
                    File.Move(path, f.Temp, overwrite: false);
                else if (f.Request.IsNewFile)
                    File.Move(f.Temp, path, overwrite: false);
                else
                    File.Replace(f.Temp, path, destinationBackupFileName: null);
                f.Result.Replaced = true;
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (f.Remove && !File.Exists(path))
                {
                    f.Result.Status = GdtSaveStatus.Conflict;
                    f.Result.ChangedOnDisk = true;
                    f.Result.Message = $"{f.Request.DisplayName} was deleted or moved while Apex was saving.";
                    return false;
                }
                if (f.Request.IsNewFile && File.Exists(path))
                {
                    DeleteTemp(f);
                    f.Result.Status = GdtSaveStatus.Conflict;
                    f.Result.ChangedOnDisk = f.Request.Sidecar;
                    f.Result.Message = $"{f.Request.DisplayName} appeared on disk while Apex was saving, so Apex didn't overwrite it.";
                    return false;
                }
                if (DateTime.UtcNow >= deadline)
                {
                    DeleteTemp(f);
                    f.Result.Status = GdtSaveStatus.Locked;
                    f.Result.Message = $"{f.Request.DisplayName} is in use by another program, so Apex couldn't replace it. The file is unchanged; save again in a moment.";
                    f.Result.Detail = ex.Message;
                    return false;
                }
                Thread.Sleep(wait);
                wait = Math.Min(wait * 2, 200);
            }
        }
    }

    /// <summary>7. The file on disk must now be exactly what was staged; if not, something wrote in the swap window.</summary>
    private static void VerifyAfterSwap(FileWork f)
    {
        if (f.Remove)
        {
            VerifyRemoved(f);
            return;
        }
        try
        {
            var stamp = GdtStamp.Read(f.Request.Path, out var bytes);
            if (!bytes.AsSpan().SequenceEqual(f.Output))
            {
                f.Result.Status = GdtSaveStatus.Failed;
                f.Result.Stamp = stamp;
                f.Result.Message = $"{f.Request.DisplayName} was changed by another program just as Apex saved it. "
                    + "Your changes are still in Apex, and the previous version is kept as a backup.";
                f.Result.Detail = f.Result.BackupPath;
                return;
            }
            f.Result.Stamp = stamp;
            f.Result.Status = GdtSaveStatus.Saved;
            f.Result.Message = $"Saved {f.Request.DisplayName}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Fail(f.Result, $"Apex saved {f.Request.DisplayName} but couldn't read it back to check it.", ex);
        }
    }

    /// <summary>
    /// 7 (removing). The file moved aside must be exactly what Apex read under the lock before it is deleted. If another
    /// program wrote it in the swap window, its version goes back where it was; nothing it wrote is deleted.
    /// </summary>
    private static void VerifyRemoved(FileWork f)
    {
        var name = f.Request.DisplayName;
        try
        {
            WriteGuard.Check(f.Temp);
            byte[] aside;
            using (var fs = new FileStream(f.Temp, FileMode.Open, FileAccess.Read, FileShare.Read))
                aside = GdtStamp.ReadAll(fs);
            if (!aside.AsSpan().SequenceEqual(f.Original))
            {
                f.Result.Status = GdtSaveStatus.Failed;
                try
                {
                    File.Move(f.Temp, f.Request.Path, overwrite: false);
                    f.Result.Replaced = false;
                    f.Result.Stamp = GdtStamp.Read(f.Request.Path, out _);
                    f.Result.Message = $"{name} was changed by another program just as Apex removed it, so Apex put it back. "
                        + "Your changes are still in Apex.";
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    f.Result.Message = $"{name} was changed by another program just as Apex removed it, and Apex couldn't put it back: "
                        + $"that version is in {Path.GetFileName(f.Temp)} beside it. Your changes are still in Apex.";
                }
                f.Result.Detail = f.Result.BackupPath;
                return;
            }
            File.Delete(f.Temp);
            if (File.Exists(f.Request.Path))
            {
                f.Result.Status = GdtSaveStatus.Failed;
                f.Result.Stamp = GdtStamp.Read(f.Request.Path, out _);
                f.Result.Message = $"Another program wrote {name} just as Apex removed it. Your changes are still in Apex, and the previous version is kept as a backup.";
                f.Result.Detail = f.Result.BackupPath;
                return;
            }
            f.Result.Removed = true;
            f.Result.Status = GdtSaveStatus.Saved;
            f.Result.Message = $"Removed {name}: it has no extension data left.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Fail(f.Result, $"Apex removed {name} but couldn't check it. The previous version is kept as a backup.", ex);
        }
    }

    /// <summary>
    /// A sidecar removal killed after moving the file aside and before deleting it (step 7) leaves only
    /// <c>x.gdtx.apex-tmp</c>. The save never committed, so the session journal still holds the edits that emptied it:
    /// the file goes back where it was and they apply again on top. With both files there, the <c>.gdtx</c> is the one
    /// Apex reads and the next save deletes the temp file. Returns whether the file was put back.
    /// </summary>
    public static bool RecoverRemovedSidecar(string path)
    {
        var temp = path + TempSuffix;
        if (File.Exists(path) || !File.Exists(temp))
            return false;
        try
        {
            WriteGuard.Check(path);
            File.Move(temp, path, overwrite: false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WriteOutsideRootException)
        {
            return false;
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The file isn't what Apex read: a conflict. When the edits can be laid over what is there now, the conflicts
    /// list says which assets overlap (possibly none), so the caller can show them and save again against
    /// <see cref="GdtSaveFileResult.Stamp"/>.
    /// </summary>
    private static void MarkChanged(FileWork f, byte[] bytes, GdtStamp now)
    {
        var r = f.Result;
        r.Status = GdtSaveStatus.Conflict;
        r.ChangedOnDisk = true;
        r.Stamp = now;
        r.Message = $"{f.Request.DisplayName} changed on disk since Apex read it.";
        if (f.Request.Replacement is not null)
            return;
        try
        {
            var probe = GdtSplicer.Splice(bytes, f.Request.Edits, f.Request.Sidecar);
            r.Conflicts.AddRange(probe.Conflicts);
            r.Problems.AddRange(probe.Problems);
        }
        catch (SpliceVerificationException ex)
        {
            r.Problems.Add(ex.Message);
        }
    }

    /// <summary>When any file failed so far, every other file is marked not written; returns whether to stop.</summary>
    private static bool Abandon(List<FileWork> files)
    {
        var failed = files.FirstOrDefault(f => f.Result.Status is not (GdtSaveStatus.Pending or GdtSaveStatus.Unchanged or GdtSaveStatus.NotWritten));
        if (failed is null)
            return false;
        foreach (var f in files)
        {
            f.Lock?.Dispose();
            f.Lock = null;
            if (f.Result.Status is GdtSaveStatus.Pending or GdtSaveStatus.Unchanged or GdtSaveStatus.NotWritten)
            {
                f.Result.Status = GdtSaveStatus.NotWritten;
                f.Result.Message = $"{f.Request.DisplayName} wasn't saved because {failed.Request.DisplayName} couldn't be.";
            }
        }
        return true;
    }

    private static void DeleteTemp(FileWork f)
    {
        try
        {
            if (File.Exists(f.Temp))
            {
                WriteGuard.Check(f.Temp);
                File.Delete(f.Temp);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left behind; the next save of this file deletes it before staging.
        }
    }

    private static void Fail(GdtSaveFileResult r, string message, Exception? ex)
    {
        r.Status = GdtSaveStatus.Failed;
        r.Message = message;
        r.Detail = ex?.Message;
    }

    /// <summary>ERROR_SHARING_VIOLATION (32) or ERROR_LOCK_VIOLATION (33).</summary>
    private static bool IsSharingViolation(IOException ex) => (ex.HResult & 0xFFFF) is 32 or 33;
}
