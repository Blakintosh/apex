using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Apex.Editor.Services.Stock;

namespace Apex.Editor.ViewModels;

/// <summary>One choice of a filter dropdown: what it says, and what the helper is asked (null: no filter).</summary>
public sealed record SoundFilterChoice(string Label, string? Value);

/// <summary>One row of the stock sound list.</summary>
public sealed class StockSoundRow
{
    public StockSoundRow(StockSoundItem item)
    {
        Item = item;
    }

    public StockSoundItem Item { get; }
    public string Name => Item.Name;

    /// <summary>"zm_castle", or "core_ui +1" when several zones define it.</summary>
    public string ZoneText => Item.Zones.Count switch
    {
        0 => "",
        1 => Item.Zones[0],
        _ => $"{Item.Zones[0]} +{Item.Zones.Count - 1}",
    };

    public string ZoneTip => string.Join(", ", Item.Zones);
    public string LangText => Item.Lang == "all" ? "" : Item.Lang;
    public string VariantsText => Item.Variants > 0 ? Item.Variants.ToString("N0", CultureInfo.InvariantCulture) : "";
    public string StorageText => Item.Storage == "unknown" ? "" : Item.Storage;
    public string DurationText => Item.HasAudio ? StockSoundsViewModel.Duration(Item.DurationMs) : "no audio";
    public bool HasAudio => Item.HasAudio;
    public bool IsLooping => Item.Looping;
}

/// <summary>One label and value of the details pane.</summary>
public sealed record SoundField(string Label, string Value);

/// <summary>
/// Stock sounds: every alias of every shipped sound bank (the helper's <c>sounds</c>) in a list that loads as it scrolls, a
/// details pane that shows the alias definition read-only, a play / stop preview and Create local copy. Aliases are never
/// edited here. The helper pages and filters by zone, language and whether the audio was found; Loaded / Streamed is a column
/// of the list, so that filter keeps the rows of each page it matches and asks for more until the page is full.
/// </summary>
public sealed partial class StockSoundsViewModel : ObservableObject, IDisposable
{
    /// <summary>Rows asked of the helper in one request.</summary>
    public const int PageSize = 200;

    /// <summary>Rows a page of the list adds (the helper is asked again until that many pass the filters, or there are no more).</summary>
    public const int PageGoal = 120;

    private readonly StockLibrary _library;
    private readonly Action _close;
    private readonly Action<StockSoundDef, StockSoundItem, int> _openCopy;
    private readonly StockSoundPreview _preview;
    private readonly DispatcherTimer _playTimer = new();
    private CancellationTokenSource _run = new();
    private CancellationTokenSource _details = new();
    private int _generation;
    private int _offset;
    private bool _exhausted;
    private bool _filling;
    private int _playToken;
    private bool _disposed;

    public StockSoundsViewModel(StockLibrary library, Action close, Action<StockSoundDef, StockSoundItem, int> openCopy, StockSoundPreview? preview = null)
    {
        _library = library;
        _close = close;
        _openCopy = openCopy;
        _preview = preview ?? new StockSoundPreview(library);
        _preview.Undecodable += _ => Dispatcher.UIThread.Post(() =>
        {
            if (IsPlaying)
                PlayText = "This sound can't be played here (more than two channels).";
        });
        _playTimer.Tick += (_, _) => EndPlay("Played");

        var (zones, langs) = library.Db?.SoundFilters() ?? (Array.Empty<string>(), Array.Empty<string>());
        Zones = new[] { new SoundFilterChoice("All zones", null) }.Concat(zones.Select(z => new SoundFilterChoice(z, z))).ToList();
        Langs = new[] { new SoundFilterChoice("Any language", null) }
            .Concat(langs.Select(l => new SoundFilterChoice(l == "all" ? "Neutral (all)" : l, l))).ToList();
        _zone = Zones[0];
        _lang = Langs[0];
        _storage = Storages[0];
        if (!library.CanAsk)
            Note = library.Backend is null && library.ExePath is null
                ? "The stock helper (stocklib.exe) isn't installed. Install it from Components to browse the game's sounds."
                : "";
        else
            _ = RunAsync(delayed: false);
    }

    // ── Filters ──────────────────────────────────────────────────────────────

    public IReadOnlyList<SoundFilterChoice> Zones { get; }
    public IReadOnlyList<SoundFilterChoice> Langs { get; }

    public IReadOnlyList<SoundFilterChoice> Storages { get; } = new[]
    {
        new SoundFilterChoice("Loaded and streamed", null),
        new SoundFilterChoice("Loaded", "loaded"),
        new SoundFilterChoice("Streamed", "streamed"),
    };

    [ObservableProperty]
    private string _query = "";

    [ObservableProperty]
    private SoundFilterChoice _zone;

    [ObservableProperty]
    private SoundFilterChoice _lang;

    [ObservableProperty]
    private SoundFilterChoice _storage;

    /// <summary>Only sounds whose audio the reader found (the others can't be heard or copied).</summary>
    [ObservableProperty]
    private bool _onlyWithAudio;

    /// <summary>One row for each zone that defines a name, instead of one row for the name.</summary>
    [ObservableProperty]
    private bool _everyDefinition;

    partial void OnQueryChanged(string value) { if (!_clearing) _ = RunAsync(delayed: true); }
    partial void OnZoneChanged(SoundFilterChoice value) { if (!_clearing) _ = RunAsync(delayed: false); }
    partial void OnLangChanged(SoundFilterChoice value) { if (!_clearing) _ = RunAsync(delayed: false); }
    partial void OnStorageChanged(SoundFilterChoice value) { if (!_clearing) _ = RunAsync(delayed: false); }
    partial void OnOnlyWithAudioChanged(bool value) { if (!_clearing) _ = RunAsync(delayed: false); }
    partial void OnEveryDefinitionChanged(bool value) { if (!_clearing) _ = RunAsync(delayed: false); }

    public bool HasFilters => Query.Length > 0 || Zone.Value is not null || Lang.Value is not null || Storage.Value is not null || OnlyWithAudio || EveryDefinition;

    private bool _clearing;

    [RelayCommand]
    private void ClearFilters()
    {
        _clearing = true;
        try
        {
            Query = "";
            Zone = Zones[0];
            Lang = Langs[0];
            Storage = Storages[0];
            OnlyWithAudio = false;
            EveryDefinition = false;
        }
        finally
        {
            _clearing = false;
        }
        _ = RunAsync(delayed: false);
    }

    // ── The list ─────────────────────────────────────────────────────────────

    public Services.Stock.StockLibrary Library => _library;

    public ObservableCollection<StockSoundRow> Items { get; } = new();

    /// <summary>What the helper counts for the filters it knows (before Loaded / Streamed is applied).</summary>
    [ObservableProperty]
    private int _total;

    [ObservableProperty]
    private bool _isLoading;

    /// <summary>Why the list is empty or can't load (the helper's own words), or "" when it is fine.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNote))]
    private string _note = "";

    public bool HasNote => Note.Length > 0;

    [ObservableProperty]
    private string _countText = "";

    private async Task RunAsync(bool delayed)
    {
        OnPropertyChanged(nameof(HasFilters));
        if (_disposed || !_library.CanAsk)
            return;
        _run.Cancel();
        _run = new CancellationTokenSource();
        var ct = _run.Token;
        var generation = ++_generation;
        _filling = false;
        try
        {
            if (delayed)
                await Task.Delay(180, ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        Items.Clear();
        _offset = 0;
        _exhausted = false;
        Total = 0;
        Note = "";
        await FillAsync(PageGoal, generation, ct).ConfigureAwait(true);
    }

    /// <summary>The list scrolled near its end: add the next page.</summary>
    public void LoadMore()
    {
        if (_exhausted || _filling || _disposed)
            return;
        _ = FillAsync(PageGoal, _generation, _run.Token);
    }

    private async Task FillAsync(int goal, int generation, CancellationToken ct)
    {
        if (_filling)
            return;
        _filling = true;
        IsLoading = true;
        try
        {
            var added = 0;
            while (added < goal && !_exhausted)
            {
                var page = await _library.SoundsAsync(new StockSoundQuery(Query.Trim(), Zone.Value, Lang.Value, OnlyWithAudio ? "ok" : null, !EveryDefinition, PageSize, _offset), ct)
                    .ConfigureAwait(true);
                if (generation != _generation || ct.IsCancellationRequested)
                    return;
                Total = page.Total;
                _offset += page.Items.Count;
                if (page.Items.Count == 0 || _offset >= page.Total)
                    _exhausted = true;
                foreach (var item in page.Items)
                {
                    if (Storage.Value is { } want && StorageClass(item.Storage) != want)
                        continue;
                    Items.Add(new StockSoundRow(item));
                    added++;
                }
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex) when (ex is StockHelperException or TimeoutException)
        {
            if (generation == _generation)
            {
                _exhausted = true;
                Note = Friendly(ex);
            }
        }
        finally
        {
            if (generation == _generation)
            {
                _filling = false;
                IsLoading = false;
                UpdateCount();
            }
        }
    }

    /// <summary>Primed sounds stream (a preloaded start and a stream); unknown ones load.</summary>
    private static string StorageClass(string storage) => storage is "streamed" or "primed" ? "streamed" : "loaded";

    private void UpdateCount()
    {
        if (Note.Length > 0)
        {
            CountText = "";
            return;
        }
        var noun = EveryDefinition ? "definition" : "sound";
        if (Storage.Value is not null)
            CountText = _exhausted
                ? $"{Items.Count:N0} {noun}{(Items.Count == 1 ? "" : "s")}"
                : $"{Items.Count:N0} {Storage.Label.ToLowerInvariant()} so far, of {Total:N0}";
        else
            CountText = Items.Count >= Total
                ? $"{Total:N0} {noun}{(Total == 1 ? "" : "s")}"
                : $"{Items.Count:N0} of {Total:N0} {noun}s";
        if (Items.Count == 0 && _exhausted)
            Note = HasFilters ? "No sound matches. Clear the filters to see them all." : "The index has no sound aliases.";
    }

    private static string Friendly(Exception ex)
    {
        if (StockLibrary.IsUnknownMethod(ex))
            return "This stocklib.exe is older than Apex and can't list sounds. Update it from Components.";
        if (ex.Message.Contains("index", StringComparison.OrdinalIgnoreCase) && ex.Message.Contains("again", StringComparison.OrdinalIgnoreCase))
            return "The index was made before sounds were added. Build the index again from Components.";
        return ex.Message;
    }

    // ── The selected sound ───────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(HasNoSelection))]
    private StockSoundRow? _selected;

    public bool HasSelection => Selected is not null;
    public bool HasNoSelection => Selected is null;

    public ObservableCollection<StockSoundDef> Definitions { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDef), nameof(CanPlay), nameof(CanCopy), nameof(CopyTip), nameof(VariantChoices), nameof(HasManyVariants))]
    private StockSoundDef? _selectedDef;

    public bool HasDef => SelectedDef is not null;

    public bool HasManyDefinitions => Definitions.Count > 1;

    [ObservableProperty]
    private bool _isReadingDefinition;

    [ObservableProperty]
    private string _definitionNote = "";

    public ObservableCollection<SoundField> Fields { get; } = new();

    partial void OnSelectedChanged(StockSoundRow? value)
    {
        StopPlayback();
        _details.Cancel();
        _details = new CancellationTokenSource();
        Definitions.Clear();
        SelectedDef = null;
        Fields.Clear();
        DefinitionNote = "";
        OnPropertyChanged(nameof(HasManyDefinitions));
        if (value is not null)
            _ = ReadDefinitionsAsync(value, _details.Token);
    }

    private async Task ReadDefinitionsAsync(StockSoundRow row, CancellationToken ct)
    {
        IsReadingDefinition = true;
        try
        {
            var defs = await _library.SoundAsync(row.Name, ct).ConfigureAwait(true);
            if (ct.IsCancellationRequested)
                return;
            var mine = defs.Definitions.Where(d => d.Lang == row.Item.Lang).ToList();
            if (mine.Count == 0)
                mine = defs.Definitions.ToList();
            // a row for one definition shows that zone's first
            if (EveryDefinition && row.Item.Zone.Length > 0)
                mine = mine.OrderBy(d => d.Zone.Equals(row.Item.Zone, StringComparison.OrdinalIgnoreCase) ? 0 : 1).ToList();
            foreach (var d in mine)
                Definitions.Add(d);
            OnPropertyChanged(nameof(HasManyDefinitions));
            SelectedDef = (EveryDefinition ? mine.FirstOrDefault() : mine.FirstOrDefault(d => d.HasAudio)) ?? mine.FirstOrDefault();
            if (mine.Count == 0)
                DefinitionNote = "The index holds no definition for this name.";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is StockHelperException or TimeoutException)
        {
            if (!ct.IsCancellationRequested)
                DefinitionNote = Friendly(ex);
        }
        finally
        {
            if (!ct.IsCancellationRequested)
                IsReadingDefinition = false;
        }
    }

    partial void OnSelectedDefChanged(StockSoundDef? value)
    {
        StopPlayback();
        VariantIndex = 0;
        Fields.Clear();
        PlayText = "";
        if (value is null)
            return;
        foreach (var f in FieldsOf(value))
            Fields.Add(f);
    }

    /// <summary>The definition as the bank states it, read-only.</summary>
    public static IEnumerable<SoundField> FieldsOf(StockSoundDef d)
    {
        yield return new("Alias", d.Name);
        yield return new("Name hash", d.Hash);
        yield return new("Zone", d.Zone);
        yield return new("Bank", d.Bank);
        yield return new("Language", d.Lang == "all" ? "neutral (all)" : d.Lang);
        yield return new("Storage", d.Storage);
        yield return new("Looping", d.Looping ? "yes" : "no");
        yield return new("Variants", d.Variants.ToString("N0", CultureInfo.InvariantCulture));
        yield return new("First file", d.File.Length > 0 ? d.File : "none");
        yield return new("Audio", d.HasAudio ? AudioText(d) : StatusText(d.Status));
        if (d.HasAudio)
            yield return new("In bank file", d.BankFile);
        yield return new("Secondary alias", d.Secondary ? "yes" : "no");
        yield return new("Stop alias", d.Stop ? "yes" : "no");
        yield return new("Duck", d.Duck ? "yes" : "no");
        yield return new("Facial animation", d.Facial ? "yes" : "no");
    }

    private static string AudioText(StockSoundDef d)
    {
        var channels = d.Channels switch { 1 => "mono", 2 => "stereo", var n => n + " channels" };
        return $"{Duration(d.DurationMs)} · {channels} · {d.Rate / 1000.0:0.#} kHz · {d.Format} · {Bytes(d.Size)}";
    }

    private static string StatusText(string status) => status switch
    {
        "no_file" => "none: it plays no file",
        "no_audio" => "not found in any bank",
        "unreadable" => "the bank is stored in a form the reader can't open",
        "" => "unknown",
        _ => status,
    };

    public static string Duration(int ms)
    {
        if (ms < 10_000)
            return (ms / 1000.0).ToString("0.0#", CultureInfo.InvariantCulture) + " s";
        var s = (int)Math.Round(ms / 1000.0);
        return s < 60 ? s + " s" : $"{s / 60}:{s % 60:00}";
    }

    private static string Bytes(long n) => n < 1024 ? n + " B" : n < 1024 * 1024 ? (n / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + " KB" : (n / 1048576.0).ToString("0.#", CultureInfo.InvariantCulture) + " MB";

    // ── Preview ──────────────────────────────────────────────────────────────

    /// <summary>The variant to play or copy first (0-based).</summary>
    [ObservableProperty]
    private int _variantIndex;

    public IReadOnlyList<string> VariantChoices =>
        SelectedDef is { Variants: > 1 } d ? Enumerable.Range(1, d.Variants).Select(i => $"Variant {i} of {d.Variants}").ToList() : Array.Empty<string>();

    public bool HasManyVariants => SelectedDef is { Variants: > 1 };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayLabel), nameof(IsBusy))]
    private bool _isPlaying;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayLabel), nameof(IsBusy))]
    private bool _isExporting;

    [ObservableProperty]
    private string _playText = "";

    public bool IsBusy => IsPlaying || IsExporting;

    public string PlayLabel => IsExporting ? "Reading…" : IsPlaying ? "Stop" : "Play";

    public bool CanPlay => SelectedDef is { HasAudio: true };

    [RelayCommand]
    private async Task PlayAsync()
    {
        if (IsPlaying || IsExporting)
        {
            StopPlayback();
            PlayText = "Stopped";
            return;
        }
        if (SelectedDef is not { HasAudio: true } def)
            return;
        var token = ++_playToken;
        var variant = Math.Clamp(VariantIndex, 0, Math.Max(0, def.Variants - 1));
        IsExporting = true;
        PlayText = variant > 0 ? $"Reading variant {variant + 1} from the zone…" : "Reading the sound…";
        try
        {
            var path = await _preview.ExportAsync(def.Name, def.Zone, variant, CancellationToken.None).ConfigureAwait(true);
            if (token != _playToken)
                return;
            _preview.Play(path);
            IsExporting = false;
            IsPlaying = true;
            var ms = Math.Min(def.DurationMs, StockSoundPreview.MaxSeconds * 1000);
            PlayText = def.Looping ? $"Playing {Duration(ms)} (it loops in the game)" : $"Playing {Duration(ms)}";
            _playTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(300, ms + 200));
            _playTimer.Start();
        }
        catch (Exception ex) when (ex is StockHelperException or TimeoutException or System.IO.IOException)
        {
            if (token == _playToken)
            {
                IsExporting = false;
                PlayText = ex.Message;
            }
        }
    }

    private void EndPlay(string text)
    {
        _playTimer.Stop();
        IsPlaying = false;
        IsExporting = false;
        PlayText = text;
    }

    /// <summary>Silences the preview (the selection changed, or the view is closing).</summary>
    public void StopPlayback()
    {
        _playToken++;
        _playTimer.Stop();
        _preview.Stop();
        IsPlaying = false;
        IsExporting = false;
    }

    // ── Create local copy ────────────────────────────────────────────────────

    public bool CanCopy => SelectedDef is { HasAudio: true, Lang: "all" } && Selected is not null;

    public string CopyTip => SelectedDef switch
    {
        null => "",
        { HasAudio: false } => "This sound's audio was not found, so there is nothing to copy.",
        { Lang: not "all" } => "Localized sounds can't be copied yet: their files belong under soundloc_assets.",
        _ => "Copy the audio into sound_assets and add the alias to a soundalias CSV.",
    };

    [RelayCommand]
    private void Copy()
    {
        if (CanCopy && SelectedDef is { } def && Selected is { } row)
            _openCopy(def, row.Item, Math.Clamp(VariantIndex, 0, Math.Max(0, def.Variants - 1)));
    }

    [RelayCommand]
    private void Close() => _close();

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _run.Cancel();
        _details.Cancel();
        StopPlayback();
        _preview.Dispose();
    }
}
