using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Apex.Editor.Services.Stock;

/// <summary>What one copy of a stock sound writes: the audio files and the alias rows that play them.</summary>
public sealed class StockSoundCopyPlan
{
    public required string Alias { get; init; }
    public required string Zone { get; init; }

    /// <summary>"loaded" or "streamed" (what the alias's Storage column says).</summary>
    public required string Storage { get; init; }
    public required bool Looping { get; init; }

    /// <summary>The variants (0-based) to copy; each becomes a file and a row of the same alias name.</summary>
    public required IReadOnlyList<int> Variants { get; init; }

    /// <summary>Where sound_assets is; the audio goes to <c>stock\&lt;zone&gt;</c> under it.</summary>
    public required string SoundAssetsDir { get; init; }

    /// <summary>The soundalias CSV the rows go into (created when it is not there).</summary>
    public required string CsvPath { get; init; }

    public string FolderRelative => Path.Combine("stock", SafeName(Zone));

    /// <summary>The file of a variant, relative to sound_assets: also what the row's FileSpec says.</summary>
    public string FileSpec(int variant) => Path.Combine(FolderRelative, FileName(variant));

    public string FileName(int variant) => SafeName(Alias) + (Variants.Count > 1 ? "_" + variant.ToString("00") : "") + ".flac";

    public string FullPath(int variant) => Path.Combine(SoundAssetsDir, FileSpec(variant));

    /// <summary>A name as a file name: letters, digits, <c>_</c> and <c>-</c>; anything else (a path separator among it) becomes <c>_</c>.</summary>
    public static string SafeName(string name)
    {
        var chars = name.Select(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '_').ToArray();
        return chars.Length == 0 ? "_" : new string(chars);
    }
}

/// <summary>Everything a copy did, enough for <see cref="StockSoundCopier.Undo"/> to take it back.</summary>
public sealed class StockSoundCopyResult
{
    public IReadOnlyList<string> Files { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> DirsCreated { get; init; } = Array.Empty<string>();
    public string CsvPath { get; init; } = "";
    public bool CsvCreated { get; init; }

    /// <summary>The bytes appended to the CSV (the whole file when it was created).</summary>
    public byte[] Appended { get; init; } = Array.Empty<byte>();
    public IReadOnlyList<string> Rows { get; init; } = Array.Empty<string>();
    public long Bytes { get; init; }
}

public sealed record StockSoundUndoResult(int FilesRemoved, int DirsRemoved, bool RowsRemoved, bool CsvDeleted, IReadOnlyList<string> Kept);

public sealed class StockSoundCopyException : Exception
{
    public StockSoundCopyException(string message) : base(message) { }
}

/// <summary>
/// Create local copy for a stock sound: the bank's FLAC streams go to <c>sound_assets\stock\&lt;zone&gt;\</c> (Ultrasound takes
/// <c>.flac</c> as a source) and one soundalias row per variant goes into a CSV the user picks. Only the four columns a row
/// needs are written (Name, Storage, FileSpec, Looping) plus Bus; the bank's mix settings (volume, distance, pan, ducks) are
/// not part of what the helper reads, so they stay the CSV's defaults. An existing CSV keeps every byte it has: the rows are
/// appended in its own columns and line ending. <see cref="Undo"/> takes back exactly what was written.
/// </summary>
public static class StockSoundCopier
{
    public const string NewFileName = "stock_sounds.csv";
    public const string DefaultBus = "bus_fx";
    private static readonly string[] NewHeader = { "Name", "Storage", "FileSpec", "Looping", "Bus" };
    private static readonly string[] Required = { "Name", "Storage", "FileSpec", "Looping" };

    /// <summary>Mapped from the bank's storage word to the Storage column (primed sounds stream; unknown ones load).</summary>
    public static string StorageColumn(string storage) => storage is "streamed" or "primed" ? "streamed" : "loaded";

    // ── Checking ─────────────────────────────────────────────────────────────

    /// <summary>The reasons a copy can't be made (empty when it can): an alias the CSV defines already, a file in the way, a CSV that isn't one.</summary>
    public static IReadOnlyList<string> Problems(StockSoundCopyPlan plan)
    {
        var problems = new List<string>();
        if (plan.Variants.Count == 0)
            problems.Add("There is no variant to copy.");
        if (plan.Variants.Any(v => v < 0) || plan.Variants.Distinct().Count() != plan.Variants.Count)
            problems.Add("The variants to copy are not valid.");
        if (!Path.GetExtension(plan.CsvPath).Equals(".csv", StringComparison.OrdinalIgnoreCase))
            problems.Add("The alias file has to be a .csv.");
        if (File.Exists(plan.CsvPath))
        {
            try
            {
                var text = ReadText(plan.CsvPath, out _);
                var (header, rows) = ReadCsv(text);
                var missing = Required.Where(c => IndexOf(header, c) < 0).ToList();
                if (header.Count == 0 || missing.Count > 0)
                    problems.Add($"{Path.GetFileName(plan.CsvPath)} isn't a soundalias file: it has no {string.Join(", ", missing.Count > 0 ? missing : Required.ToList())} column.");
                else
                {
                    var name = IndexOf(header, "Name");
                    if (rows.Any(r => r.Count > name && r[name].Equals(plan.Alias, StringComparison.OrdinalIgnoreCase)))
                        problems.Add($"{Path.GetFileName(plan.CsvPath)} already defines {plan.Alias}.");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problems.Add($"{Path.GetFileName(plan.CsvPath)} can't be read: {ex.Message}");
            }
        }
        foreach (var v in plan.Variants)
            if (File.Exists(plan.FullPath(v)))
                problems.Add($"{plan.FileSpec(v)} already exists in sound_assets.");
        return problems;
    }

    /// <summary>The rows a copy would add, as they are written (the dialog shows them before anything is).</summary>
    public static IReadOnlyList<string> PreviewRows(StockSoundCopyPlan plan)
    {
        var header = File.Exists(plan.CsvPath) ? TryHeader(plan.CsvPath) : null;
        header ??= NewHeader.ToList();
        return plan.Variants.Select(v => Row(plan, v, header)).ToList();
    }

    // ── Writing ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Writes the audio (one <c>sound_export</c> per variant, straight into sound_assets) and then the rows. A failure takes
    /// back what this call had written. Nothing is overwritten: <see cref="Problems"/> is checked first.
    /// </summary>
    public static async Task<StockSoundCopyResult> CopyAsync(IStockBackend backend, StockSoundCopyPlan plan, Action<StockProgress>? progress, CancellationToken cancel = default)
    {
        if (Problems(plan) is { Count: > 0 } problems)
            throw new StockSoundCopyException(problems[0]);

        var files = new List<string>();
        var dirs = new List<string>();
        long bytes = 0;
        try
        {
            var folder = Path.Combine(plan.SoundAssetsDir, plan.FolderRelative);
            foreach (var dir in MissingDirs(folder).Reverse())
            {
                Directory.CreateDirectory(dir);
                dirs.Add(dir);
            }
            var done = 0;
            foreach (var variant in plan.Variants)
            {
                cancel.ThrowIfCancellationRequested();
                progress?.Invoke(new StockProgress($"{plan.FileName(variant)}" + (variant > 0 ? " (reading the zone)" : ""), done, plan.Variants.Count));
                var path = plan.FullPath(variant);
                var export = await backend.SoundExportAsync(plan.Alias, plan.Zone, variant, "flac", path, cancel).ConfigureAwait(false);
                if (!File.Exists(path))
                    throw new StockSoundCopyException($"The helper did not write {plan.FileName(variant)}.");
                files.Add(path);
                bytes += export.Bytes > 0 ? export.Bytes : new FileInfo(path).Length;
                done++;
            }
            progress?.Invoke(new StockProgress("Adding the alias rows", plan.Variants.Count, plan.Variants.Count));
            var (appended, rows, created) = AppendRows(plan);
            return new StockSoundCopyResult
            {
                Files = files, DirsCreated = dirs, CsvPath = plan.CsvPath, CsvCreated = created, Appended = appended, Rows = rows, Bytes = bytes,
            };
        }
        catch
        {
            Remove(files, dirs);
            throw;
        }
    }

    private static (byte[] Appended, IReadOnlyList<string> Rows, bool Created) AppendRows(StockSoundCopyPlan plan)
    {
        var exists = File.Exists(plan.CsvPath);
        var header = exists ? TryHeader(plan.CsvPath)! : NewHeader.ToList();
        var newline = "\r\n";
        byte[] existing = Array.Empty<byte>();
        if (exists)
        {
            existing = File.ReadAllBytes(plan.CsvPath);
            newline = DetectNewline(existing);
        }
        var rows = plan.Variants.Select(v => Row(plan, v, header)).ToList();
        var text = new StringBuilder();
        if (!exists)
            text.Append(string.Join(",", NewHeader)).Append(newline);
        else if (existing.Length > 0 && existing[^1] != (byte)'\n')
            text.Append(newline);
        foreach (var row in rows)
            text.Append(row).Append(newline);
        var appended = new UTF8Encoding(false).GetBytes(text.ToString());
        var dir = Path.GetDirectoryName(Path.GetFullPath(plan.CsvPath));
        if (dir is { Length: > 0 })
            Directory.CreateDirectory(dir);
        using (var stream = new FileStream(plan.CsvPath, exists ? FileMode.Append : FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            stream.Write(appended);
        return (appended, rows, !exists);
    }

    private static string Row(StockSoundCopyPlan plan, int variant, IReadOnlyList<string> header)
    {
        var cells = new string[header.Count];
        Array.Fill(cells, "");
        void Set(string column, string value)
        {
            var i = IndexOf(header, column);
            if (i >= 0)
                cells[i] = value;
        }
        Set("Name", plan.Alias);
        Set("Storage", StorageColumn(plan.Storage));
        Set("FileSpec", plan.FileSpec(variant));
        Set("Looping", plan.Looping ? "looping" : "nonlooping");
        Set("Bus", DefaultBus);
        return string.Join(",", cells.Select(Quote));
    }

    private static string Quote(string cell) =>
        cell.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0 ? "\"" + cell.Replace("\"", "\"\"") + "\"" : cell;

    // ── Undo ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Takes back a copy: the audio files it wrote, the folders it made (when they are empty), its rows (or the CSV, when the
    /// copy created it and nothing else is in it). Rows the user has edited since stay, and are named.
    /// </summary>
    public static StockSoundUndoResult Undo(StockSoundCopyResult result)
    {
        var kept = new List<string>();
        var (filesRemoved, dirsRemoved) = Remove(result.Files, result.DirsCreated);
        var rowsRemoved = false;
        var csvDeleted = false;
        if (File.Exists(result.CsvPath))
        {
            var bytes = File.ReadAllBytes(result.CsvPath);
            var at = result.Appended.Length == 0 ? -1 : LastIndexOf(bytes, result.Appended);
            if (at < 0)
                kept.Add($"{Path.GetFileName(result.CsvPath)} has changed since the copy, so its rows stay.");
            else
            {
                var rest = new byte[bytes.Length - result.Appended.Length];
                Buffer.BlockCopy(bytes, 0, rest, 0, at);
                Buffer.BlockCopy(bytes, at + result.Appended.Length, rest, at, bytes.Length - at - result.Appended.Length);
                if (result.CsvCreated && rest.All(b => b is (byte)'\r' or (byte)'\n' or (byte)' '))
                {
                    File.Delete(result.CsvPath);
                    csvDeleted = true;
                }
                else
                    File.WriteAllBytes(result.CsvPath, rest);
                rowsRemoved = true;
            }
        }
        foreach (var file in result.Files.Where(File.Exists))
            kept.Add($"{Path.GetFileName(file)} could not be removed.");
        return new StockSoundUndoResult(filesRemoved, dirsRemoved, rowsRemoved, csvDeleted, kept);
    }

    private static (int Files, int Dirs) Remove(IEnumerable<string> files, IEnumerable<string> dirs)
    {
        var f = 0;
        foreach (var file in files)
        {
            try
            {
                if (File.Exists(file))
                {
                    File.Delete(file);
                    f++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        var d = 0;
        foreach (var dir in dirs.OrderByDescending(x => x.Length))
        {
            try
            {
                if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                {
                    Directory.Delete(dir);
                    d++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        return (f, d);
    }

    private static IEnumerable<string> MissingDirs(string folder)
    {
        var missing = new List<string>();
        var dir = Path.GetFullPath(folder);
        while (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            missing.Add(dir);
            dir = Path.GetDirectoryName(dir);
        }
        return missing;
    }

    private static int LastIndexOf(byte[] haystack, byte[] needle)
    {
        for (var i = haystack.Length - needle.Length; i >= 0; i--)
        {
            var j = 0;
            while (j < needle.Length && haystack[i + j] == needle[j])
                j++;
            if (j == needle.Length)
                return i;
        }
        return -1;
    }

    // ── Reading a soundalias CSV ─────────────────────────────────────────────

    private static string DetectNewline(byte[] bytes)
    {
        var lf = Array.IndexOf(bytes, (byte)'\n');
        return lf > 0 && bytes[lf - 1] == (byte)'\r' ? "\r\n" : lf >= 0 ? "\n" : "\r\n";
    }

    private static int IndexOf(IReadOnlyList<string> header, string column)
    {
        for (var i = 0; i < header.Count; i++)
            if (header[i].Trim().Equals(column, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    private static string ReadText(string path, out bool bom)
    {
        var bytes = File.ReadAllBytes(path);
        bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        return Encoding.UTF8.GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
    }

    private static List<string>? TryHeader(string path)
    {
        try
        {
            return ReadCsv(ReadText(path, out _)).Header.ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The header and the rows of a CSV (quoted cells may hold commas, quotes and line breaks). Comment rows (starting with #) are skipped.</summary>
    public static (IReadOnlyList<string> Header, List<List<string>> Rows) ReadCsv(string text)
    {
        var records = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    cell.Append('"');
                    i++;
                }
                else if (c == '"')
                    quoted = false;
                else
                    cell.Append(c);
            }
            else if (c == '"' && cell.Length == 0)
                quoted = true;
            else if (c == ',')
            {
                row.Add(cell.ToString());
                cell.Clear();
            }
            else if (c is '\r' or '\n')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                    i++;
                row.Add(cell.ToString());
                cell.Clear();
                if (row.Count > 1 || row[0].Length > 0)
                    records.Add(row);
                row = new List<string>();
            }
            else
                cell.Append(c);
        }
        if (cell.Length > 0 || row.Count > 0)
        {
            row.Add(cell.ToString());
            records.Add(row);
        }
        if (records.Count == 0)
            return (Array.Empty<string>(), new List<List<string>>());
        var rows = records.Skip(1).Where(r => !(r.Count > 0 && r[0].StartsWith('#'))).ToList();
        return (records[0], rows);
    }
}
