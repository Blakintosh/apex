using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Apex.Editor.Services.Stock;

namespace Apex.Editor.ViewModels;

/// <summary>A soundalias CSV a copy can add its rows to.</summary>
public sealed record SoundCsvChoice(string Label, string FullPath, string Detail = "");

/// <summary>Where the sound copy dialog is.</summary>
public enum SoundCopyStage { Ready, Running, Done, Undone }

/// <summary>
/// Create local copy for a stock sound: which variants, which soundalias CSV the rows go into, then the copy (the audio into
/// <c>sound_assets\stock\&lt;zone&gt;</c>, one alias row per variant) and Undo. The writing is <see cref="StockSoundCopier"/>'s.
/// </summary>
public sealed partial class StockSoundCopyViewModel : ObservableObject
{
    private readonly StockSoundDef _def;
    private readonly StockLibrary _library;
    private readonly string _soundAssetsDir;
    private readonly int _previewed;
    private readonly Action _close;
    private readonly Action<string> _status;
    private readonly Action<string> _remember;
    private readonly Func<Task<string?>>? _pickCsv;
    private StockSoundCopyResult? _result;

    public StockSoundCopyViewModel(StockSoundDef def, int previewedVariant, StockLibrary library, string soundAssetsDir, string aliasesDir,
        string? lastCsv, Action close, Action<string> status, Action<string> remember, Func<Task<string?>>? pickCsv = null)
    {
        _def = def;
        _library = library;
        _soundAssetsDir = soundAssetsDir;
        _previewed = Math.Clamp(previewedVariant, 0, Math.Max(0, def.Variants - 1));
        _close = close;
        _status = status;
        _remember = remember;
        _pickCsv = pickCsv;
        Choices = new List<SoundCsvChoice>(CsvChoices(aliasesDir, lastCsv));
        _csv = Choices.FirstOrDefault(c => lastCsv is not null && SamePath(c.FullPath, lastCsv)) ?? Choices.First();
        AudioFolder = Path.Combine("sound_assets", "stock", StockSoundCopyPlan.SafeName(def.Zone)) + Path.DirectorySeparatorChar;
        Refresh();
    }

    public string Alias => _def.Name;
    public string Zone => _def.Zone;
    public int Variants => Math.Max(1, _def.Variants);
    public bool HasManyVariants => Variants > 1;
    public string Subtitle => $"{_def.Zone}  ·  {_def.Storage}{(_def.Looping ? "  ·  looping" : "")}  ·  {StockSoundsViewModel.Duration(_def.DurationMs)}";
    public string AudioFolder { get; }
    public string AllVariantsText => $"All {Variants:N0} variants";
    public string OneVariantText => $"Only variant {_previewed + 1}, the one selected";

    public IReadOnlyList<SoundCsvChoice> Choices { get; private set; }

    [ObservableProperty]
    private SoundCsvChoice _csv;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OnlyOne))]
    private bool _allVariants = true;

    public bool OnlyOne { get => !AllVariants; set { if (value) AllVariants = false; } }

    partial void OnCsvChanged(SoundCsvChoice value) => Refresh();
    partial void OnAllVariantsChanged(bool value) => Refresh();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReady), nameof(IsRunning), nameof(ShowPlan), nameof(ShowResult), nameof(CanCreate))]
    private SoundCopyStage _stage = SoundCopyStage.Ready;

    public bool IsReady => Stage == SoundCopyStage.Ready;
    public bool IsRunning => Stage == SoundCopyStage.Running;
    public bool ShowPlan => Stage is SoundCopyStage.Ready or SoundCopyStage.Running;
    public bool ShowResult => Stage is SoundCopyStage.Done or SoundCopyStage.Undone;
    public bool CanCreate => Stage == SoundCopyStage.Ready && Problem.Length == 0;

    /// <summary>What the rows would look like, as they would be written.</summary>
    [ObservableProperty]
    private string _rowsText = "";

    [ObservableProperty]
    private string _filesText = "";

    /// <summary>Why a copy can't be made with these choices, or "".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCreate), nameof(HasProblem))]
    private string _problem = "";

    public bool HasProblem => Problem.Length > 0;

    [ObservableProperty]
    private string _error = "";

    public string CreateLabel => HasManyVariants && AllVariants ? $"Create {Variants:N0} local sounds" : "Create local copy";

    private StockSoundCopyPlan Plan() => new()
    {
        Alias = _def.Name,
        Zone = _def.Zone,
        Storage = _def.Storage,
        Looping = _def.Looping,
        Variants = AllVariants ? Enumerable.Range(0, Variants).ToList() : new[] { _previewed },
        SoundAssetsDir = _soundAssetsDir,
        CsvPath = Csv.FullPath,
    };

    private void Refresh()
    {
        OnPropertyChanged(nameof(CreateLabel));
        if (Stage != SoundCopyStage.Ready)
            return;
        Error = "";
        var plan = Plan();
        var problems = StockSoundCopier.Problems(plan);
        Problem = problems.Count > 0 ? problems[0] : "";
        FilesText = plan.Variants.Count == 1
            ? plan.FileSpec(plan.Variants[0])
            : $"{plan.FileSpec(plan.Variants[0])} … {plan.FileName(plan.Variants[^1])}  ({plan.Variants.Count:N0} files)";
        RowsText = string.Join("\n", StockSoundCopier.PreviewRows(plan));
        OnPropertyChanged(nameof(CanCreate));
    }

    private static IEnumerable<SoundCsvChoice> CsvChoices(string aliasesDir, string? lastCsv)
    {
        var list = new List<SoundCsvChoice>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var newPath = Path.Combine(aliasesDir, StockSoundCopier.NewFileName);
        if (!File.Exists(newPath))
        {
            list.Add(new SoundCsvChoice($"New file: {StockSoundCopier.NewFileName}", newPath, "aliases"));
            seen.Add(newPath);
        }
        if (lastCsv is { Length: > 0 } && File.Exists(lastCsv) && seen.Add(lastCsv))
            list.Add(Existing(aliasesDir, lastCsv));
        try
        {
            if (Directory.Exists(aliasesDir))
                foreach (var path in Directory.EnumerateFiles(aliasesDir, "*.csv", SearchOption.AllDirectories)
                             .OrderByDescending(p => File.GetLastWriteTimeUtc(p)).Take(400))
                    if (seen.Add(path))
                        list.Add(Existing(aliasesDir, path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        if (list.Count == 0)
            list.Add(new SoundCsvChoice($"New file: {StockSoundCopier.NewFileName}", newPath, "aliases"));
        return list;
    }

    private static SoundCsvChoice Existing(string aliasesDir, string path) =>
        new(Path.GetRelativePath(aliasesDir, path).StartsWith("..", StringComparison.Ordinal) ? path : Path.GetRelativePath(aliasesDir, path), path);

    private static bool SamePath(string a, string b) => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    [RelayCommand]
    private async Task BrowseAsync()
    {
        if (_pickCsv is null || Stage != SoundCopyStage.Ready)
            return;
        string? picked;
        try { picked = await _pickCsv().ConfigureAwait(true); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or NotSupportedException) { picked = null; }
        if (picked is null)
            return;
        var choice = Choices.FirstOrDefault(c => SamePath(c.FullPath, picked));
        if (choice is null)
        {
            choice = new SoundCsvChoice(picked, picked);
            Choices = new[] { choice }.Concat(Choices).ToList();
            OnPropertyChanged(nameof(Choices));
        }
        Csv = choice;
    }

    // ── The copy ─────────────────────────────────────────────────────────────

    [ObservableProperty]
    private double _progressFraction;

    [ObservableProperty]
    private string _progressText = "";

    [ObservableProperty]
    private string _resultTitle = "";

    [ObservableProperty]
    private string _resultDetail = "";

    [ObservableProperty]
    private string _afterText = "";

    [ObservableProperty]
    private bool _canUndo;

    public StockSoundCopyResult? Result => _result;

    [RelayCommand]
    private async Task CreateAsync()
    {
        if (!CanCreate)
            return;
        var plan = Plan();
        Stage = SoundCopyStage.Running;
        Error = "";
        ProgressText = "Starting…";
        try
        {
            var result = await StockSoundCopier.CopyAsync(_library, plan, p =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    ProgressText = p.Total > 0 ? $"{p.What} ({p.Done:N0} of {p.Total:N0})" : p.What;
                    ProgressFraction = p.Total > 0 ? (double)p.Done / p.Total : 0;
                });
            }).ConfigureAwait(true);
            _result = result;
            var files = result.Files.Count;
            ResultTitle = $"Created {files:N0} local sound{(files == 1 ? "" : "s")}";
            ResultDetail = $"{files:N0} file{(files == 1 ? "" : "s")} in {AudioFolder}, and {result.Rows.Count:N0} alias row{(result.Rows.Count == 1 ? "" : "s")} for {Alias} in {Path.GetFileName(result.CsvPath)}"
                           + (result.CsvCreated ? " (a new file)." : ".");
            AfterText = "Mix settings (volume, distance, pan, ducks) are not read from the game's banks: the rows use the soundalias defaults, with Bus " + StockSoundCopier.DefaultBus + ". Set them in the CSV.";
            CanUndo = true;
            Stage = SoundCopyStage.Done;
            _remember(result.CsvPath);
            _status($"Copied {Alias} into {Path.GetFileName(result.CsvPath)}");
        }
        catch (Exception ex) when (ex is StockSoundCopyException or StockHelperException or IOException or UnauthorizedAccessException or TimeoutException)
        {
            Stage = SoundCopyStage.Ready;
            Error = ex.Message;
            Refresh();
        }
    }

    [RelayCommand]
    private void Undo()
    {
        if (_result is null || !CanUndo)
            return;
        CanUndo = false;
        try
        {
            var undone = StockSoundCopier.Undo(_result);
            ResultTitle = $"Undone: {undone.FilesRemoved:N0} file{(undone.FilesRemoved == 1 ? "" : "s")} removed"
                          + (undone.CsvDeleted ? $", {Path.GetFileName(_result.CsvPath)} removed" : undone.RowsRemoved ? ", the alias rows removed" : "");
            ResultDetail = undone.Kept.Count > 0 ? string.Join(" ", undone.Kept) : "Everything this copy wrote is gone.";
            AfterText = "";
            Stage = SoundCopyStage.Undone;
            _status($"Undid the copy of {Alias}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AfterText = "Undo failed: " + ex.Message;
            CanUndo = true;
        }
    }

    [RelayCommand]
    private void Close()
    {
        if (!IsRunning)
            _close();
    }
}
