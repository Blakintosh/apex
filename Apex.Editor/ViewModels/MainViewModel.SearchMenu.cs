using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Apex.Editor.Models;
using Apex.Editor.Services;

namespace Apex.Editor.ViewModels;

/// <summary>One asset in the search menu's results: its name with the match picked out, and "type · gdt" under it.</summary>
public sealed class SearchMenuResult
{
    public SearchMenuResult(AssetRecord asset, string match)
    {
        Asset = asset;
        Glyph = TypeStyles.Glyph(asset.Type);
        GlyphBrush = TypeStyles.Brush(asset.Type);
        Detail = $"{asset.Type} · {MainViewModel.ShortGdt(asset.GdtName)}";
        var name = asset.Name;
        var at = match.Length == 0 ? -1 : name.IndexOf(match, StringComparison.OrdinalIgnoreCase);
        if (at < 0)
            Prefix = name;
        else
        {
            Prefix = name[..at];
            Hit = name.Substring(at, match.Length);
            Postfix = name[(at + match.Length)..];
        }
    }

    public AssetRecord Asset { get; }
    public string Glyph { get; }
    public IBrush GlyphBrush { get; }
    public string Prefix { get; } = "";
    public string Hit { get; } = "";
    public string Postfix { get; } = "";
    public string Detail { get; }
    public string AutomationName => $"{Asset.Name}, {Detail}";
}

/// <summary>
/// A line of the search menu's results. The lines are built once and swap what they show, hiding the ones a short
/// result list doesn't need: rebuilding a line's container costs about 1 ms, and a keystroke has 16.
/// </summary>
public sealed class SearchMenuRow : ObservableObject
{
    private SearchMenuResult? _item;

    /// <summary>What the line shows; null while it is hidden.</summary>
    public SearchMenuResult? Item
    {
        get => _item;
        set
        {
            var wasShown = IsShown;
            if (SetProperty(ref _item, value) && wasShown != IsShown)
                OnPropertyChanged(nameof(IsShown));
        }
    }

    public bool IsShown => _item is not null;
}

/// <summary>A type pill in the search menu: on while a <c>type:</c> chip for it is in the box.</summary>
public sealed partial class SearchTypeOption : ObservableObject
{
    private readonly Action<SearchTypeOption> _toggle;

    public SearchTypeOption(string type, Action<SearchTypeOption> toggle)
    {
        Type = type;
        Glyph = TypeStyles.Glyph(type);
        GlyphBrush = TypeStyles.Brush(type);
        _toggle = toggle;
    }

    public string Type { get; }
    public string Glyph { get; }
    public IBrush GlyphBrush { get; }

    private bool _isOn;

    public bool IsOn
    {
        get => _isOn;
        set
        {
            if (_isOn == value)
                return;
            _isOn = value;
            OnPropertyChanged();
            _toggle(this);
        }
    }

    /// <summary>Follows the chips without toggling them back.</summary>
    internal void Show(bool on) => SetProperty(ref _isOn, on, nameof(IsOn));
}

/// <summary>
/// The Explorer's search box, and the search menu that opens over it (the button at the box's end, or Ctrl+Shift+F).
/// The box is one search model: it shows the Explorer's query as chips and words, a click puts the caret in it, and
/// typing filters the Explorer. The menu edits a draft of that query with fields for each filter and lists matching
/// assets as you type: Enter opens the chosen one and leaves the Explorer alone; Ctrl+Enter gives the Explorer the
/// draft, where it stays until cleared; Esc drops the draft; a click elsewhere keeps it for the next time it opens.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>The menu lists this many results; the count above them is the whole match.</summary>
    private const int SearchMenuLimit = 50;

    /// <summary>The Explorer's query as the box shows it, kept in step with <see cref="FilterText"/>.</summary>
    public QueryChips ExplorerQuery { get; } = new();

    /// <summary>The menu's draft query.</summary>
    public QueryChips MenuQuery { get; } = new();

    private bool _syncingExplorerQuery;
    private string _explorerChipKey = "";
    private DispatcherTimer _menuDebounce = null!;
    private int _menuGen;
    private CancellationTokenSource? _menuCts;
    private bool _menuCurrent;
    private bool _menuOpenWhenReady;
    private bool _syncingMenu;
    /// <summary>The draft the results on show were found for.</summary>
    private string? _menuShownQuery;

    private void InitSearchMenu()
    {
        _menuDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _menuDebounce.Tick += (_, _) =>
        {
            _menuDebounce.Stop();
            StartMenuScan(_menuGen, ParseQuery(MenuQuery.Query));
        };
        ExplorerQuery.Changed += ExplorerQueryChanged;
        MenuQuery.Changed += MenuQueryChanged;
    }

    /// <summary>"Search 122,000 assets" while the box is empty; nothing beside chips.</summary>
    public string SearchPlaceholder => ExplorerQuery.HasChips ? "" : $"Search {TotalCount:N0} assets";

    partial void OnTotalCountChanged(int value) => OnPropertyChanged(nameof(SearchPlaceholder));

    private void ExplorerQueryChanged()
    {
        var chipKey = string.Join(' ', ExplorerQuery.Chips.Select(c => c.Raw));
        var chipsChanged = chipKey != _explorerChipKey;
        _explorerChipKey = chipKey;
        _syncingExplorerQuery = true;
        try
        {
            FilterText = ExplorerQuery.Query;
        }
        finally
        {
            _syncingExplorerQuery = false;
        }
        OnPropertyChanged(nameof(SearchPlaceholder));
        OnPropertyChanged(nameof(ReserveFacetRow));
        // A chip added or removed is a click, not typing: it applies at once.
        if (chipsChanged)
            ApplyFilterNow();
    }

    /// <summary>The Explorer's query changed from elsewhere (a command, a pill, Clear): the box shows it.</summary>
    private void SyncExplorerQuery(string filterText)
    {
        if (_syncingExplorerQuery)
            return;
        ExplorerQuery.SetQuery(filterText);
        _explorerChipKey = string.Join(' ', ExplorerQuery.Chips.Select(c => c.Raw));
        OnPropertyChanged(nameof(SearchPlaceholder));
        OnPropertyChanged(nameof(ReserveFacetRow));
    }

    // ── The menu ─────────────────────────────────────────────────────────────

    [ObservableProperty]
    private bool _isSearchMenuOpen;

    /// <summary>The types offered as pills: the most common, plus any the draft already filters by.</summary>
    [ObservableProperty]
    private List<SearchTypeOption> _searchMenuTypes = new();

    /// <summary>The draft's GDT chips, shown again in the GDT field.</summary>
    public RangeObservableCollection<SearchChip> MenuGdtChips { get; } = new();

    /// <summary>The results on show, best first.</summary>
    public IReadOnlyList<SearchMenuResult> SearchMenuResults { get; private set; } = Array.Empty<SearchMenuResult>();

    /// <summary>The menu's result lines (see <see cref="SearchMenuRow"/>).</summary>
    public IReadOnlyList<SearchMenuRow> SearchMenuRows { get; } = Enumerable.Range(0, SearchMenuLimit).Select(_ => new SearchMenuRow()).ToList();

    [ObservableProperty]
    private int _searchMenuSelectedIndex = -1;

    /// <summary>The results shown are the draft's as it stands (no search waiting or running).</summary>
    public bool SearchMenuIsCurrent => _menuCurrent;

    [ObservableProperty]
    private string _searchMenuCountText = "";

    [ObservableProperty]
    private bool _searchMenuIsEmpty;

    /// <summary>The draft as query syntax, for the footer.</summary>
    [ObservableProperty]
    private string _searchMenuQuery = "";

    [ObservableProperty]
    private string _menuGdtText = "";

    [ObservableProperty]
    private string _menuPropKey = "";

    [ObservableProperty]
    private string _menuPropOp = "=";

    [ObservableProperty]
    private string _menuPropValue = "";

    public IReadOnlyList<string> MenuPropOps { get; } = new[] { "=", "!=", ">", ">=", "<", "<=", "~" };

    [ObservableProperty]
    private bool _menuChanged;

    [ObservableProperty]
    private bool _menuOffDefault;

    [ObservableProperty]
    private bool _menuProblems;

    partial void OnMenuChangedChanged(bool value) => ToggleMenuFilter("is:changed", value);
    partial void OnMenuOffDefaultChanged(bool value) => ToggleMenuFilter("is:off-default", value);
    partial void OnMenuProblemsChanged(bool value) => ToggleMenuFilter("is:problems", value);

    private void ToggleMenuFilter(string raw, bool on)
    {
        if (_syncingMenu || MenuQuery.Contains(raw) == on)
            return;
        MenuQuery.Toggle(raw);
    }

    /// <summary>A draft left by a click outside the menu, and the Explorer query it was drafted from.</summary>
    private (string Draft, string From)? _keptDraft;

    /// <summary>
    /// Opens the menu on a draft of the Explorer's query, or on the draft a click outside left, while the Explorer's
    /// query is still the one it was drafted from.
    /// </summary>
    public void OpenSearchMenu()
    {
        if (IsSearchMenuOpen)
            return;
        if (_keptDraft is { } kept && kept.From == FilterText)
            MenuQuery.SetQuery(kept.Draft);
        else
        {
            MenuQuery.SetQuery(FilterText);
            MenuGdtText = "";
            MenuPropKey = "";
            MenuPropOp = "=";
            MenuPropValue = "";
        }
        _keptDraft = null;
        // The busiest types, the order they stay in while the menu is open; the draft's own types join them.
        var types = TypeOptions.OrderByDescending(o => o.Count).ThenBy(o => o.Label, StringComparer.Ordinal)
            .Take(9).Select(o => o.Label).ToList();
        foreach (var chip in MenuQuery.Chips)
            if (chip.Kind == TokenKind.Type && !types.Contains(chip.Token.Value, StringComparer.OrdinalIgnoreCase))
                types.Add(chip.Token.Value);
        // The same pills as last time are kept, not rebuilt: opening is a keystroke and has a frame.
        if (!types.SequenceEqual(SearchMenuTypes.Select(t => t.Type)))
            SearchMenuTypes = types.Select(t => new SearchTypeOption(t, o => ToggleMenuFilter($"type:{o.Type}", o.IsOn))).ToList();
        // Results for another query would be wrong for a moment; the same query's are right until the new ones land.
        if (MenuQuery.Query.Trim() != _menuShownQuery)
        {
            ShowSearchMenuResults(Array.Empty<SearchMenuResult>());
            SearchMenuCountText = "";
            SearchMenuIsEmpty = false;
        }
        RefreshMenuFields();
        RunMenuSearch();
        IsSearchMenuOpen = true;
    }

    /// <summary>Closes the menu; the draft goes with it and the Explorer is as it was (Esc, a result opened, applied).</summary>
    [RelayCommand]
    public void CloseSearchMenu()
    {
        _keptDraft = null;
        IsSearchMenuOpen = false;
    }

    /// <summary>
    /// Closes the menu without a decision (a click elsewhere): the Explorer is as it was, and the draft waits for the menu
    /// to open again, so a stray click never throws a half-built query away.
    /// </summary>
    public void SetSearchMenuAside()
    {
        if (!IsSearchMenuOpen)
            return;
        _keptDraft = (MenuQuery.Query, FilterText);
        IsSearchMenuOpen = false;
    }

    partial void OnIsSearchMenuOpenChanged(bool value)
    {
        if (value)
            return;
        // However it closed (Esc, Enter, a click elsewhere), a search still running is dropped with the draft.
        _menuGen++;
        _menuCts?.Cancel();
        _menuCts = null;
        _menuDebounce.Stop();
        _menuOpenWhenReady = false;
    }

    /// <summary>Ctrl+Enter: the Explorer takes the draft's chips and words, and keeps them until cleared.</summary>
    [RelayCommand]
    public void ApplySearchMenu()
    {
        CommitMenuFields();
        var query = MenuQuery.Query.Trim();
        CloseSearchMenu();
        FilterText = query;
        ApplyFilterNow();
    }

    /// <summary>Enter: opens the chosen result as a kept tab. Results still on their way open when they land.</summary>
    public void OpenSearchMenuSelection()
    {
        if (!_menuCurrent)
        {
            _menuOpenWhenReady = true;
            return;
        }
        if (SearchMenuSelectedIndex >= 0 && SearchMenuSelectedIndex < SearchMenuResults.Count)
            OpenSearchMenuResult(SearchMenuResults[SearchMenuSelectedIndex]);
    }

    public void OpenSearchMenuResult(SearchMenuResult result)
    {
        CloseSearchMenu();
        OpenAsset(result.Asset);
    }

    /// <summary>↑/↓ in the menu: moves the chosen result, stopping at the ends.</summary>
    public void MoveSearchMenuSelection(int delta)
    {
        if (SearchMenuResults.Count > 0)
            SearchMenuSelectedIndex = Math.Clamp(SearchMenuSelectedIndex + delta, 0, SearchMenuResults.Count - 1);
    }

    /// <summary>The GDT field: each word typed (a space or Enter ends it) becomes a <c>gdt:</c> chip.</summary>
    public bool CommitMenuGdt()
    {
        var words = AssetQuery.Tokenize(MenuGdtText).ToList();
        if (words.Count == 0)
            return false;
        MenuGdtText = "";
        foreach (var word in words)
            MenuQuery.Add($"gdt:{ShortGdt(word.StartsWith("gdt:", StringComparison.OrdinalIgnoreCase) ? word[4..] : word)}");
        return true;
    }

    /// <summary>The Property fields: Enter makes them a <c>prop:</c> chip. No key tests any property.</summary>
    public bool CommitMenuProp()
    {
        var key = MenuPropKey.Trim();
        var value = MenuPropValue.Trim();
        if (value.Length == 0 && key.Length == 0)
            return false;
        if (value.Contains(' ') && !value.StartsWith('"'))
            value = $"\"{value}\"";
        MenuQuery.Add(value.Length == 0 ? $"prop:{key}" : $"prop:{key}{MenuPropOp}{value}");
        MenuPropKey = "";
        MenuPropValue = "";
        return true;
    }

    /// <summary>Whatever is half-typed in the GDT and Property fields counts when the menu is applied.</summary>
    private void CommitMenuFields()
    {
        CommitMenuGdt();
        CommitMenuProp();
    }

    private void MenuQueryChanged()
    {
        RefreshMenuFields();
        RunMenuSearch();
    }

    /// <summary>The pills, toggles and GDT field follow the chips, however they got there.</summary>
    private void RefreshMenuFields()
    {
        _syncingMenu = true;
        try
        {
            foreach (var type in SearchMenuTypes)
                type.Show(MenuQuery.Contains($"type:{type.Type}"));
            MenuChanged = MenuQuery.Contains("is:changed");
            MenuOffDefault = MenuQuery.Contains("is:off-default");
            MenuProblems = MenuQuery.Contains("is:problems");
        }
        finally
        {
            _syncingMenu = false;
        }
        MenuGdtChips.ReplaceAll(MenuQuery.Chips.Where(c => c.Kind == TokenKind.Gdt).ToList());
        SearchMenuQuery = MenuQuery.Query.Trim();
    }

    /// <summary>
    /// Every keystroke searches again off the UI thread, superseding the last search. Queries that read GDT files
    /// (prop:, is:off-default) over the install wait out the typing first, as the Explorer's do.
    /// </summary>
    private void RunMenuSearch()
    {
        var gen = ++_menuGen;
        _menuCurrent = false;
        _menuCts?.Cancel();
        _menuCts = null;
        _menuDebounce.Stop();
        var tokens = ParseQuery(MenuQuery.Query);
        if (_liveMode && _db.Assets.Count > 5000 && AssetQuery.TouchesProperties(tokens))
        {
            _menuDebounce.Start();
            return;
        }
        StartMenuScan(gen, tokens);
    }

    private void StartMenuScan(int gen, List<QueryToken> tokens)
    {
        var snapshot = _db.Gdts.Select(GetSnapshot).ToArray();
        var problemSet = tokens.Any(t => t.Kind == TokenKind.Problems)
            ? new HashSet<AssetRecord>(_problemsByAsset.Where(kv => kv.Value > 0).Select(kv => kv.Key))
            : null;
        var cancel = (_menuCts = new CancellationTokenSource()).Token;
        var match = tokens.FirstOrDefault(t => t.Kind == TokenKind.Name)?.Value ?? "";
        var query = MenuQuery.Query.Trim();
        var extensions = ExtensionSearch(tokens);
        Task.Run(() =>
        {
            List<AssetRecord> top;
            int total;
            try
            {
                (top, total) = SearchMenuScan(snapshot, tokens, problemSet, match, cancel, extensions);
            }
            catch
            {
                return; // cancelled by a newer keystroke, or a fault the next keystroke recovers from
            }
            Dispatcher.UIThread.Post(() =>
            {
                if (gen != _menuGen)
                    return; // a newer keystroke's search wins
                FillSearchMenu(top, total, match);
                _menuShownQuery = query;
            });
        }, cancel);
    }

    /// <summary>Counts every match and keeps the best <see cref="SearchMenuLimit"/> by the Explorer's search rank.</summary>
    private static (List<AssetRecord> Top, int Total) SearchMenuScan(IReadOnlyList<AssetRecord>[] snapshot, List<QueryToken> tokens,
        HashSet<AssetRecord>? problemSet, string match, CancellationToken cancel, AssetQuery.ExtensionValues? extensions)
    {
        var perGdt = new List<AssetRecord>[snapshot.Length];
        Parallel.For(0, snapshot.Length, new ParallelOptions { CancellationToken = cancel },
            i => perGdt[i] = AssetQuery.MatchAll(snapshot[i], tokens, problemSet is null ? null : problemSet.Contains, extensions));
        var top = new TopAssets(match, SearchMenuLimit);
        var total = 0;
        foreach (var hits in perGdt)
        {
            total += hits.Count;
            foreach (var a in hits)
                top.Offer(a);
        }
        return (top.Result(), total);
    }

    private void ShowSearchMenuResults(IReadOnlyList<SearchMenuResult> results)
    {
        SearchMenuResults = results;
        for (var i = 0; i < SearchMenuRows.Count; i++)
            SearchMenuRows[i].Item = i < results.Count ? results[i] : null;
    }

    private void FillSearchMenu(List<AssetRecord> top, int total, string match)
    {
        _menuCts = null;
        _menuCurrent = true;
        // The same rows (reopening on the same query, a keystroke that changed nothing) stay as they are.
        var same = top.Count == SearchMenuResults.Count
                   && SearchMenuResults.Select(r => r.Asset).SequenceEqual(top)
                   && SearchMenuResults.All(r => r.Hit.Equals(match, StringComparison.OrdinalIgnoreCase));
        if (!same)
            ShowSearchMenuResults(top.Select(a => new SearchMenuResult(a, match)).ToList());
        SearchMenuSelectedIndex = top.Count > 0 ? 0 : -1;
        SearchMenuCountText = total == 1 ? "1 result" : $"{total:N0} results";
        SearchMenuIsEmpty = total == 0;
        if (_menuOpenWhenReady)
        {
            _menuOpenWhenReady = false;
            OpenSearchMenuSelection();
        }
    }
}
