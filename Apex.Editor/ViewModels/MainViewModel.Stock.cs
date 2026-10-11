using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Apex.Editor.Models;
using Apex.Editor.Services;
using Apex.Editor.Services.Stock;

namespace Apex.Editor.ViewModels;

/// <summary>Where search results come from: both, the GDTs, or the game's own assets.</summary>
public enum SearchSource { All, Local, Stock }

/// <summary>
/// The stock library in the window: every shipped Black Ops III asset next to the local ones. Search merges stock rows in
/// after the local ones (a local asset of the same type and name wins), a stock asset opens read-only, and Create local
/// copy turns one into ordinary GDT entries. Stock never appears in the GDT tree.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>The stock library of this window's install (a library with nothing behind it in mock mode).</summary>
    public StockLibrary Stock { get; private set; } = StockLibrary.None;

    /// <summary>Stock rows a search lists at first; "Show more" adds this many each time.</summary>
    public const int StockPageSize = 200;

    private readonly Dictionary<string, AssetRecord> _stockRecords = new(StringComparer.OrdinalIgnoreCase);

    // ── The library ──────────────────────────────────────────────────────────

    /// <summary>Opens the stock library of the current install; called whenever the install is (re)selected.</summary>
    private void UseStock()
    {
        UseStockLibrary(_env.IsAvailable && _env.Bo3Root is { } root ? new StockLibrary(root, _settings.StocklibPath) : StockLibrary.None);
    }

    /// <summary>Uses <paramref name="library"/> (the harness hands in one over a fixture database).</summary>
    public void UseStockLibrary(StockLibrary library)
    {
        if (!ReferenceEquals(Stock, StockLibrary.None))
        {
            Stock.Changed -= OnStockChanged;
            Stock.Dispose();
        }
        Stock = library;
        Services.Preview.ToolsGfx.ToolsGfxPreviewService.UseStockRoot(library.CacheRoot);
        _stockRecords.Clear();
        _stockResult = null;
        if (!ReferenceEquals(library, StockLibrary.None))
            library.Changed += OnStockChanged;
        ShippedAssets.UseDatabase(library.Db);
        OnPropertyChanged(nameof(Stock));
        NotifyStockState();
    }

    /// <summary>The window opened its install: bring the index up to date in the background (cheap when nothing changed).</summary>
    private void StartStockRefresh()
    {
        if (Stock.ExePath is not null)
            Stock.StartRefresh();
        // what the install has (Dragon, Blackbird, the Radiant plugin) decides whether stock assets can be used directly
        Stock.StartStatusRefresh();
        NotifyStockState();
    }

    private void OnStockChanged() => Dispatcher.UIThread.Post(StockChangedOnUi);

    private bool _stockWasSearchable;
    private bool _stockProgressShown;

    private void StockChangedOnUi()
    {
        NotifyStockState();
        if (Stock.Progress.Length > 0)
        {
            _stockProgressShown = true;
            ShowProgress(Stock.Progress);
        }
        else if (_stockProgressShown)
        {
            _stockProgressShown = false;
            if (Status.StartsWith("Indexing stock", StringComparison.Ordinal) || Status.StartsWith("Refreshing stock", StringComparison.Ordinal))
                ClearProgress();
        }
        var searchable = Stock.CanSearch;
        if (searchable != _stockWasSearchable)
        {
            _stockWasSearchable = searchable;
            _stockResult = null;
            if (_tokens.Count > 0)
                ApplyFilterNow();
        }
    }

    private void NotifyStockState()
    {
        OnPropertyChanged(nameof(StockSearchable));
        OnPropertyChanged(nameof(ShowStockOffer));
        OnPropertyChanged(nameof(StockOfferText));
        OnPropertyChanged(nameof(ShowSourceFilter));
        OnPropertyChanged(nameof(StockNote));
        OnPropertyChanged(nameof(HasStockNote));
        NotifyStockUse();
    }

    /// <summary>Stock rows can be searched (an index exists).</summary>
    public bool StockSearchable => Stock.CanSearch;

    /// <summary>The Explorer's results carry the All / Local / Stock control while there is a stock index to merge in.</summary>
    public bool ShowSourceFilter => StockSearchable && ReserveFacetRow;

    /// <summary>Helper missing: the offer card shows under a search, calmly, until it is dismissed or the helper is found.</summary>
    public bool ShowStockOffer => Stock.State == StockState.HelperMissing && !_stockOfferDismissed && ReserveFacetRow;

    public string StockOfferText => "Stock library needs the BO3 Mod Tools stock helper. It reads the game's own assets so you can search them next to yours and copy any of them into a map.";

    private bool _stockOfferDismissed;

    [RelayCommand]
    private void DismissStockOffer()
    {
        _stockOfferDismissed = true;
        OnPropertyChanged(nameof(ShowStockOffer));
    }

    /// <summary>Why the library has nothing to search yet or failed, in a line under the results; empty when all is well.</summary>
    public string StockNote => Stock.State switch
    {
        StockState.Indexing when Stock.ExePath is not null => "Stock assets are being indexed. They appear here when it is done.",
        StockState.Failed when Stock.Problem.Length > 0 => "The stock library couldn't start: " + Stock.Problem,
        _ => "",
    };

    public bool HasStockNote => StockNote.Length > 0 && ReserveFacetRow;

    // ── Install the helper ───────────────────────────────────────────────────

    /// <summary>The "Stock helper" panel (the Install button opens it).</summary>
    [ObservableProperty]
    private bool _isStockHelperOpen;

    [ObservableProperty]
    private string _stockHelperPath = "";

    [ObservableProperty]
    private string _stockHelperError = "";

    [RelayCommand]
    private void InstallStockHelper()
    {
        // TODO(release feed): the helper has no release feed yet (gscode-installer/docs/release-contract.md will define
        // it). When it exists this downloads and unpacks the helper into <BO3>\bin\stocklib and refreshes the library;
        // until then the panel points Apex at a stocklib.exe that is already on this machine.
        StockHelperPath = _settings.StocklibPath ?? Stock.ExePath ?? "";
        StockHelperError = "";
        IsStockHelperOpen = true;
    }

    [RelayCommand]
    private void CancelStockHelper() => IsStockHelperOpen = false;

    [RelayCommand]
    private void AcceptStockHelper()
    {
        var path = StockHelperPath.Trim().Trim('"');
        if (path.Length == 0)
        {
            _settings.StocklibPath = null;
            _settings.Save();
            Stock.UseHelper(null);
            IsStockHelperOpen = false;
            NotifyStockState();
            return;
        }
        if (StockLibrary.Locate(null, path) is not { } found)
        {
            StockHelperError = "There is no stocklib.exe at that path.";
            return;
        }
        _settings.StocklibPath = found;
        _settings.Save();
        _stockOfferDismissed = false;
        Stock.UseHelper(found);
        IsStockHelperOpen = false;
        NotifyStockState();
        Status = "Using the stock helper at " + found;
    }

    // ── Source filter ────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSourceAll), nameof(IsSourceLocal), nameof(IsSourceStock))]
    private SearchSource _source = SearchSource.All;

    public bool IsSourceAll => Source == SearchSource.All;
    public bool IsSourceLocal => Source == SearchSource.Local;
    public bool IsSourceStock => Source == SearchSource.Stock;

    partial void OnSourceChanged(SearchSource value)
    {
        if (_tokens.Count > 0)
            ApplyFilterNow();
    }

    [RelayCommand]
    private void SetSource(string source) =>
        Source = Enum.TryParse<SearchSource>(source, true, out var s) ? s : SearchSource.All;

    [ObservableProperty]
    private string _sourceAllText = "All";

    [ObservableProperty]
    private string _sourceLocalText = "Local";

    [ObservableProperty]
    private string _sourceStockText = "Stock";

    // ── Search: merging stock rows in ────────────────────────────────────────

    /// <summary>What one stock fetch answered, and for which search.</summary>
    private sealed record StockSearchState(string Key, int Limit, StockResult Result, HashSet<string> LocalOverrides, int AssetCount);

    private StockSearchState? _stockResult;
    private int _stockPages = 1;
    private string _stockWantedKey = "";
    private int _stockGen;

    // The stock side of the last search the rows were built for.
    private int _stockRowsShown;
    private bool _stockMore;

    public bool HasMoreStock => _stockMore;

    /// <summary>
    /// The stock query a search maps to: the name words, and the types its <c>type:</c> tokens name. Null when the stock
    /// library does not apply: nothing to search, or the query uses what only GDT assets have (gdt:, prop:, is:).
    /// </summary>
    private StockQuery? StockQueryFor(IReadOnlyList<QueryToken> tokens, string? facet)
    {
        if (!StockSearchable || tokens.Any(t => t.Kind is TokenKind.Gdt or TokenKind.Prop or TokenKind.Modified or TokenKind.Problems or TokenKind.Changed))
            return null;
        var words = tokens.Where(t => t.Kind == TokenKind.Name).Select(t => t.Value).ToList();
        if (words.Count == 0)
            return null;
        var kinds = TypeKinds(tokens);
        if (facet is not null)
            kinds = kinds.Count == 0 || kinds.Contains(facet) ? new List<string> { facet.ToLowerInvariant() } : new List<string> { "\0" };
        return new StockQuery(words, kinds, StockPageSize * _stockPages);
    }

    private static List<string> TypeKinds(IReadOnlyList<QueryToken> tokens) =>
        tokens.Where(t => t.Kind == TokenKind.Type).Select(t => StockTypes.KindOfApex(t.Value)).Distinct().ToList();

    /// <summary>
    /// The stock answer for this search if one stands for it (the key and the page both match); null when none does yet.
    /// </summary>
    private StockSearchState? CachedStock(StockQuery? query)
    {
        if (query is null)
        {
            _stockWantedKey = "";
            return null;
        }
        _stockWantedKey = query.Key;
        return _stockResult is { } have && have.Key == query.Key && have.Limit >= query.Limit ? have : null;
    }

    /// <summary>
    /// Asks the database for this search off the UI thread. When the answer lands (and is still the one wanted) the rows
    /// are built again from the hits already in hand, so the local rows are never searched twice.
    /// </summary>
    private void RequestStock(StockQuery query, IReadOnlyList<AssetRecord> localShown)
    {
        var key = query.Key;
        var gen = Interlocked.Increment(ref _stockGen);
        var library = Stock;
        var sample = localShown.Take(300).Select(a => (Kind: StockTypes.KindOfApex(a.Type), a.Name)).ToList();
        var typeKinds = TypeKinds(_tokens);
        _ = Task.Run(() =>
        {
            StockSearchState state;
            try
            {
                var db = library.Db!;
                var result = db.Search(query);
                var overrides = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var group in sample.GroupBy(s => s.Kind))
                    foreach (var name in db.NamesOfKind(group.Key, group.Select(g => g.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList()))
                        overrides.Add(group.Key + "\0" + name);
                state = new StockSearchState(key, query.Limit, Narrowed(result, typeKinds), overrides, db.AssetCount());
            }
            catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or System.IO.IOException or NullReferenceException)
            {
                // The database is being replaced or is unreadable: this search lists local assets alone.
                state = new StockSearchState(key, query.Limit, new StockResult(Array.Empty<StockHit>(), 0, new Dictionary<string, int>()), new HashSet<string>(), 0);
            }
            Dispatcher.UIThread.Post(() =>
            {
                if (gen != Volatile.Read(ref _stockGen) || key != _stockWantedKey || !ReferenceEquals(library, Stock))
                    return;
                _stockResult = state;
                if (_hitCache is { } cached && _tokens.Count > 0)
                    BuildRootsFromHits(cached, hasFilter: true);
            });
        });
    }

    /// <summary>The facet counts a search shows count only the kinds its type: tokens allow (the result is per kind already).</summary>
    private static StockResult Narrowed(StockResult result, List<string> typeKinds) =>
        typeKinds.Count == 0
            ? result
            : result with { KindCounts = result.KindCounts.Where(c => typeKinds.Contains(c.Key)).ToDictionary(c => c.Key, c => c.Value) };

    /// <summary>True when a local asset has this stock asset's kind and name (it wins, and the stock one is not listed).</summary>
    private bool HasLocal(StockHit hit)
    {
        var name = StockTypes.BareName(hit.EngineType, hit.Name);
        if (!_byName.TryGetValue(name, out var list))
            return false;
        var kind = hit.Kind;
        foreach (var a in list)
            if (StockTypes.KindOfApex(a.Type) == kind)
                return true;
        return false;
    }

    private BrowserNode StockRow(StockHit hit, string query)
    {
        var record = StockRecordFor(hit);
        var node = new BrowserNode(0)
        {
            Title = hit.Name,
            Glyph = TypeStyles.Glyph(record.Type),
            GlyphBrush = TypeStyles.Brush(record.Type),
            Asset = record,
            Prefix = hit.Name,
            SourceLine = SourceLineOf(hit),
        };
        node.SetHighlight(query);
        return node;
    }

    private static string SourceLineOf(StockHit hit)
    {
        var zone = hit.Zone.Length > 0 ? hit.Zone + ".ff" : "stock";
        if (hit.Zones.Count > 1)
            zone += " +" + (hit.Zones.Count - 1);
        return hit.Kind switch
        {
            "weapon" or "xmodel" or "xanim" or "material" or "image" => zone,
            "attachmentunique" => zone + " · attachment unique",
            "attachmentcosmeticvariant" => zone + " · cosmetic variant",
            _ => zone + " · " + hit.Kind,
        };
    }

    /// <summary>The record that stands for one stock asset: one per engine type and name, so a tab opened twice is one tab.</summary>
    private AssetRecord StockRecordFor(StockHit hit)
    {
        var key = hit.EngineType + "\0" + hit.Name;
        if (_stockRecords.TryGetValue(key, out var known))
            return known;
        var record = new AssetRecord
        {
            Name = hit.Name,
            Type = hit.Kind,
            GdtName = "",
            Stock = new StockOrigin(hit.EngineType, hit.Name, hit.Zones),
        };
        _stockRecords[key] = record;
        return record;
    }

    /// <summary>The stock record for a name and engine type the helper reported (a dependency in the Uses list).</summary>
    private AssetRecord StockRecordFor(string engineType, string name, IReadOnlyList<string> zones) =>
        StockRecordFor(new StockHit(engineType, name, zones));

    [RelayCommand]
    private void ShowMoreStock()
    {
        _stockPages++;
        ApplyFilterNow();
    }

    // ── Opening a stock asset ────────────────────────────────────────────────

    /// <summary>Reads what a stock tab shows (its fields, then its dependencies) from the helper, and fills the tab in.</summary>
    private void BeginStockRead(AssetRecord record)
    {
        if (record.Stock is not { } origin || origin.Description is not null || origin.Problem.Length > 0 && !origin.Loading)
            return;
        var library = Stock;
        if (!library.CanAsk)
        {
            origin.Loading = false;
            origin.Problem = "The stock helper isn't installed.";
            RefreshStockTab(record);
            return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                // Stepping through results opens one preview tab after another: a read starts only for a tab that stays.
                await Task.Delay(250).ConfigureAwait(false);
                if (!await Dispatcher.UIThread.InvokeAsync(() => OpenTabs.Any(t => t.Record == record)))
                    return;
                var description = await library.DescribeAsync(origin.EngineType, origin.Name, null).ConfigureAwait(false);
                Dispatcher.UIThread.Post(() => ApplyStockDescription(record, description));
            }
            catch (Exception ex) when (ex is StockHelperException or TimeoutException or OperationCanceledException)
            {
                var message = ex.Message;
                Dispatcher.UIThread.Post(() =>
                {
                    origin.Loading = false;
                    origin.Problem = message;
                    RefreshStockTab(record);
                });
                return;
            }

            if (!await Dispatcher.UIThread.InvokeAsync(() => OpenTabs.Any(t => t.Record == record)))
                return;
            if (StockLibrary.HasPreviewCache(origin.Kind))
            {
                // The preview pane asks for the same caches; the helper answers once and the other waits for it. They come before
                // the dependencies: what the tab shows first should not wait behind a closure walk.
                try
                {
                    await library.CacheAsync(origin.EngineType, origin.Name).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is StockHelperException or TimeoutException or OperationCanceledException)
                {
                    // The pane says why it has nothing to draw.
                }
            }
            if (DefaultCloneTarget() is not { } map)
            {
                Dispatcher.UIThread.Post(() => SetDepsState(record, false, "What this uses is read against a map or mod: there is none to read it against."));
                return;
            }
            Dispatcher.UIThread.Post(() => SetDepsState(record, true, ""));
            try
            {
                var deps = await library.DepsAsync(origin.EngineType, origin.Name, map.Argument, null, null, false).ConfigureAwait(false);
                Dispatcher.UIThread.Post(() => ApplyStockDeps(record, deps));
            }
            catch (Exception ex) when (ex is StockHelperException or TimeoutException or OperationCanceledException)
            {
                // The Uses list stays empty; the fields above are what matters.
                var why = ex.Message;
                Dispatcher.UIThread.Post(() => SetDepsState(record, false, "Couldn't read what this uses: " + why));
            }
        });
    }

    private void ApplyStockDescription(AssetRecord record, StockDescription description)
    {
        if (record.Stock is not { } origin)
            return;
        origin.Description = description;
        origin.Loading = false;
        if (description.Gdf.Length > 0)
            record.Type = System.IO.Path.GetFileNameWithoutExtension(description.Gdf);
        foreach (var (key, value) in description.Fields)
            record.Properties[key] = value;
        // The first tab was built before the fields were read: what "changed" is measured against is what was read.
        record.RebaseAfterSave(record.Properties);
        RefreshStockTab(record);
    }

    private void ApplyStockDeps(AssetRecord record, StockDeps deps)
    {
        if (record.Stock is not { } origin)
            return;
        origin.Deps = deps;
        origin.DepsLoading = false;
        foreach (var tab in OpenTabs.Where(t => t.Record == record))
            tab.RefreshStockInfo(Stock.Db);
        if (ActiveTab?.Record == record)
            RefreshLinks();
    }

    /// <summary>Rebuilds a stock asset's tab from what has been read: the same tab, in the same place, with its fields.</summary>
    private void RefreshStockTab(AssetRecord record)
    {
        var old = OpenTabs.FirstOrDefault(t => t.Record == record);
        if (old is null)
            return;
        var tab = new AssetEditorViewModel(record, NavigateToRef, CloseTab, OnEditorEdited, FindAsset, RenameTab, GdtOf)
        {
            Owner = this,
            IsPreview = old.IsPreview,
            // The pane starts once the fields are read (a material's slots come from them) and moves to each rebuilt tab.
            PreviewPane = old.PreviewPane ?? StockPane(record),
        };
        old.PreviewPane = null;
        tab.RefreshStockInfo(Stock.Db);
        var index = OpenTabs.IndexOf(old);
        var wasActive = ActiveTab == old;
        OpenTabs.Insert(index, tab);
        if (wasActive)
            ActiveTab = tab;
        OpenTabs.Remove(old);
        ForgetTab(old);
        DisposeTabPreview(old);
    }

    /// <summary>
    /// The preview of a stock model, material or image: drawn from the caches the helper writes on the first view, so it needs
    /// the helper only then. Null for the other kinds, and when there is nothing to draw it from.
    /// </summary>
    private PreviewPaneViewModel? StockPane(AssetRecord record)
    {
        if (record.Stock is not { } origin || origin.Problem.Length > 0 || !StockLibrary.HasPreviewCache(origin.Kind))
            return null;
        if (!Stock.CanAsk && !Stock.IsCached(origin.EngineType, origin.Name))
            return null;
        return PreviewPaneViewModel.CreateStock(record, _env, new StockPreviewSource(Stock, origin), FindAsset, NavigateToRef);
    }

    private void SetDepsState(AssetRecord record, bool loading, string note)
    {
        if (record.Stock is not { } origin)
            return;
        origin.DepsLoading = loading;
        origin.DepsNote = note;
        if (ActiveTab?.Record == record)
        {
            RefreshLinks();
            OnPropertyChanged(nameof(ActiveLinksEmptyText));
        }
    }

    /// <summary>The Inspector's Uses list of a stock asset: what it references directly (the helper's walk, the root's edges), nearest the top.</summary>
    private List<InspectorLinkViewModel> StockUses(AssetRecord record)
    {
        var uses = new List<InspectorLinkViewModel>();
        if (record.Stock?.Deps is not { } deps)
            return uses;
        var direct = deps.RootNode.Refs.Select(r => r.Id).ToHashSet();
        foreach (var node in deps.Nodes.Where(n => n.Id != deps.Root && direct.Contains(n.Id) && n.Class != StockClass.Embedded).OrderBy(n => UsesOrder(n.Kind)).ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (uses.Count >= 200)
                break;
            var captured = node;
            var local = captured.Class == StockClass.Local ? FindAsset(StockTypes.KindOfEngine(captured.Type), StockTypes.BareName(captured.Type, captured.Name)) : null;
            uses.Add(new InspectorLinkViewModel(captured.Name, StockTypes.DisplayType(captured.Type), captured.Zone.Length > 0 ? captured.Zone : "shipped", false, false,
                () => OpenAsset(local ?? StockRecordFor(captured.Type, captured.Name, captured.Zones), preview: true)));
        }
        return uses;
    }

    private static int UsesOrder(string kind) => kind switch
    {
        "xmodel" => 0,
        "material" => 1,
        "image" => 2,
        "weapon" => 3,
        "attachmentunique" => 4,
        "xanim" => 8,
        _ => 5,
    };

    /// <summary>Opens the clone dialog for the active stock asset (the header's button).</summary>
    [RelayCommand]
    private void CreateLocalCopy()
    {
        if (ActiveTab?.Record is { IsStock: true } record)
            OpenCloneDialog(record);
    }

    /// <summary>The stock asset behind a row or tab, for commands that act on it.</summary>
    public void CreateLocalCopyOf(AssetRecord record) => OpenCloneDialog(record);
}
