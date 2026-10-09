using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Apex.Editor.Models;
using Apex.Editor.Services;
using Apex.Editor.Services.Gdf;
using Apex.Editor.Services.Gdt;

namespace Apex.Editor.ViewModels;

public sealed partial class RefItemViewModel : ObservableObject
{
    private readonly Action<AssetRecord> _open;

    public RefItemViewModel(AssetRecord record, Action<AssetRecord> open)
    {
        Record = record;
        _open = open;
    }

    public AssetRecord Record { get; }
    public string Name => Record.Name;
    public string TypeName => Record.Type;
    public string Glyph => TypeStyles.Glyph(Record.Type);
    public IBrush GlyphBrush => TypeStyles.Brush(Record.Type);

    [RelayCommand]
    private void Open() => _open(Record);
}

/// <summary>
/// One row of the Filter menu's Type and GDT lists: checked while its <c>type:</c> or <c>gdt:</c> chip is in the box, and a
/// click puts the chip in or takes it out.
/// </summary>
public sealed class FilterOption : ObservableObject
{
    private readonly Action<FilterOption> _toggle;

    public FilterOption(string label, string glyph, IBrush glyphBrush, int count, string token, Action<FilterOption> toggle)
    {
        Label = label;
        Glyph = glyph;
        GlyphBrush = glyphBrush;
        Count = count;
        Token = token;
        Value = AssetQuery.Parse(token) is [{ } parsed] ? parsed.Value : "";
        _toggle = toggle;
    }

    /// <summary>The token's value as a query holds it (unquoted, lower case), to tell whether the box filters by it.</summary>
    public string Value { get; }

    /// <summary>The type, or the GDT's file name.</summary>
    public string Label { get; }

    /// <summary>A GDT's folder, shown dim after its name.</summary>
    public string Folder { get; init; } = "";

    public bool HasFolder => Folder.Length > 0;

    /// <summary>A GDT's full path; null for a type.</summary>
    public string? Tip { get; init; }

    public string Glyph { get; }
    public IBrush GlyphBrush { get; }
    public int Count { get; }

    /// <summary>The filter the row puts in the box: <c>type:xanim</c>, <c>gdt:zm_weapons</c>.</summary>
    public string Token { get; }

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

    /// <summary>Follows the box without toggling it back.</summary>
    internal void Show(bool on) => SetProperty(ref _isOn, on, nameof(IsOn));
}

/// <summary>A section's label in the Filter menu's list ("Type", "GDT").</summary>
public sealed record FilterMenuHeading(string Text);

/// <summary>A line of explanation in the Filter menu's list (the GDT list is capped, or nothing matched).</summary>
public sealed record FilterMenuNote(string Text);

/// <summary>
/// A line of the palette list. The list keeps its lines and swaps what each one shows, hiding the ones it doesn't need:
/// a line whose content changes updates a few text bindings, where a new line builds and styles a container (about
/// 1 ms each, against a 16 ms keystroke budget).
/// </summary>
public sealed class PaletteRow : ObservableObject
{
    private PaletteItemViewModel? _item;

    /// <summary>What the line shows; null while it is hidden.</summary>
    public PaletteItemViewModel? Item
    {
        get => _item;
        set
        {
            var wasShown = IsShown;
            if (!SetProperty(ref _item, value))
                return;
            OnPropertyChanged(nameof(AccessibleName));
            if (wasShown != IsShown)
                OnPropertyChanged(nameof(IsShown));
        }
    }

    public bool IsShown => _item is not null;

    /// <summary>What a screen reader announces for the line (empty while it is hidden).</summary>
    public string AccessibleName => _item?.Name ?? "";
}

/// <summary>
/// One result row in the Ctrl+P palette: an asset, or (after a ">" / "@" prefix) a command or a
/// property of the active asset to jump to.
/// </summary>
public sealed class PaletteItemViewModel
{
    public PaletteItemViewModel(AssetRecord record)
    {
        Record = record;
        Name = record.Name;
        Glyph = TypeStyles.Glyph(record.Type);
        GlyphBrush = TypeStyles.Brush(record.Type);
        TypeName = record.Type;
        GdtName = MainViewModel.ShortGdt(record.GdtName);
    }

    public PaletteItemViewModel(string title, string shortcut, string glyph, Action run)
    {
        Name = title;
        Shortcut = shortcut;
        Glyph = glyph;
        GlyphBrush = TypeStyles.NeutralBrush;
        Run = run;
    }

    public AssetRecord? Record { get; }
    public Action? Run { get; }
    public string Name { get; }
    public string TypeName { get; } = "";
    public string GdtName { get; } = "";
    public string Shortcut { get; } = "";
    public string Detail { get; init; } = "";
    /// <summary>The catalog command a ">" row runs (null for assets and properties).</summary>
    public string? CommandId { get; init; }
    public string Glyph { get; }
    public IBrush GlyphBrush { get; }
    public bool IsAsset => Record is not null;
    public bool IsCommand => Record is null;
}

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly AssetDatabase _db;
    private GameEnvironment _env;
    private readonly Dictionary<AssetRecord, BrowserNode> _leafCache = new();
    private readonly Dictionary<AssetRecord, int> _problemsByAsset = new();
    private readonly DispatcherTimer _debounce;
    private List<QueryToken> _tokens = new();

    // ── WI-4: O(1) lookups + reverse-reference index (built at wave end, updated incrementally) ──
    // Names are NOT guaranteed unique across ~2k GDTs (derived/base pairs, model sidecars), so the
    // name index maps to a list; FindAsset preserves the old "first occurrence, preferring type" order.
    private readonly Dictionary<string, List<AssetRecord>> _byName = new(StringComparer.OrdinalIgnoreCase);
    // parent-name → records deriving from it: lets rename/delete/reference scans avoid a 121k parent walk.
    private readonly Dictionary<string, List<AssetRecord>> _byParent = new(StringComparer.OrdinalIgnoreCase);
    // Registry of records whose Properties have been materialized (open/edited/mock). Reference scans
    // that inspect property *values* only ever match materialized records, so iterating this set keeps
    // per-tab-open reference work O(materialized) as a session ages instead of O(121k).
    private readonly HashSet<AssetRecord> _materialized = new();

    // ── WI-8: per-GDT immutable snapshot cache for off-thread scans (invalidated on structural change) ──
    // The expensive prop:/is:modified scan enumerates each GDT's asset list on a thread-pool thread while
    // the UI thread may mutate those lists (watcher / new / duplicate / delete). A cached ToArray() per
    // GDT gives the scan an isolated view without re-copying unchanged lists every keystroke.
    private readonly Dictionary<GdtFile, IReadOnlyList<AssetRecord>> _snapshotCache = new();

    // ── WI-3: palette off-thread scan debounce + generation guard ──
    private readonly DispatcherTimer _paletteDebounce;
    private int _paletteGen;

    // ── WI-9: schemas may still be loading while the first GDT wave indexes; gate asset-open on this. ──
    private volatile bool _schemasReady;

    /// <summary>Live ingestion (real BO3 GDTs) vs deterministic mock data. Set at construction, or when Locate… finds an install.</summary>
    private bool _liveMode;
    private GdtWatcher? _watcher;

    /// <summary>Sample data (the harness, or a developer's --mock): the status bar marks it so it isn't mistaken for the game's.</summary>
    public bool IsMockData => !_liveMode && !IsInstallMissing;

    public MainViewModel() : this(Services.Session.SessionJournal.DefaultRoot)
    {
    }

    /// <param name="sessionRoot">Where sessions are kept (one folder per install inside it); null keeps this
    /// window's changes in memory only.</param>
    public MainViewModel(string? sessionRoot)
    {
        _env = new GameEnvironment(_settings.Bo3Root);
        _sessionRoot = sessionRoot;
        // Sample data only when asked for (the harness, or --mock); a user without a detected install gets Locate….
        IsInstallMissing = !_env.IsAvailable && !GameEnvironment.MockForced;
        UseInstallRoots();
        FieldFiles.Resolve = FindAsset;
        InitWorkspace();
        InitSession(IsInstallMissing ? null : sessionRoot);
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); ApplyFilter(); };
        _paletteDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _paletteDebounce.Tick += (_, _) => { _paletteDebounce.Stop(); RunPaletteScan(); };
        InitSearchMenu();

        if (IsInstallMissing)
        {
            // Nothing to show until the user points at the install: an empty catalog behind the not-found state.
            _db = new AssetDatabase { Gdts = new List<GdtFile>(), Assets = new List<AssetRecord>() };
            ApplyFilter();
            return;
        }

        if (!_env.IsAvailable)
        {
            // ── Mock mode: exactly as before — synchronous, deterministic (Apex.Shots depends on this). ──
            _liveMode = false;
            _schemasReady = true;
            Services.Extensions.ExtensionRegistry.Load();
            _db = MockDatabase.Generate();
            TotalCount = _db.Assets.Count;
            GdtCount = _db.Gdts.Count;

            RebuildIndexes();
            foreach (var asset in _db.Assets)
            {
                _leafCache[asset] = BrowserNode.ForAsset(asset, ToggleNode);
                MarkMaterialized(asset);
            }

            foreach (var asset in _db.Assets)
                UpdateProblemEntry(asset);
            SumProblems();

            BuildFilterOptions();

            ApplyFilter();
            RefreshStartPage();
            // No "ready" line: the status bar's "mock data" already says which database this is.
            ReportExtensions();
            RestoreSession();
            return;
        }

        // ── Live mode: show the window instantly on an empty database, then ingest in the background. ──
        _db = new AssetDatabase { Gdts = new List<GdtFile>(), Assets = new List<AssetRecord>() };
        StartLive();
    }

    private void StartLive()
    {
        _liveMode = true;
        _schemasReady = false; // until this install's deffiles are read (RunLiveLoadAsync)
        BeginExplorerLoading();
        ApplyFilter();
        ShowProgress("Loading GDTs…");
        _ = RunLiveLoadAsync();
    }

    /// <summary>Points the process-wide file lookups at the install this window uses.</summary>
    private void UseInstallRoots()
    {
        // File, file-list and bone fields resolve under the install (none with mock data).
        FieldFiles.Root = _env.IsAvailable ? _env.Bo3Root : null;
        // What the game ships is no missing reference (global_invisible has no GDT); read while the GDTs index.
        ShippedAssets.Use(_env.IsAvailable ? _env.Bo3Root : null);
        // A deffile button's ReadTextFile reads only under the install GameEnvironment found. Only a live window sets it: a
        // mock-mode one (tests, no install) leaves the process's install alone.
        if (_env.IsAvailable && _env.Bo3Root is { } installRoot)
            Services.Gdf.DeffileButtons.InstallRoot = installRoot;
    }

    // ── No install found: Locate… ─────────────────────────────────────────────
    private readonly string? _sessionRoot;

    /// <summary>No Black Ops III install was found: the workspace shows the not-found state and its Locate… button.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMockData))]
    private bool _isInstallMissing;

    /// <summary>Why the last folder picked with Locate… isn't an install ("" when there is nothing to say).</summary>
    [ObservableProperty]
    private string _locateError = "";

    /// <summary>
    /// Uses <paramref name="folder"/> as the install if it is one: remembers it in the settings and loads it straight away
    /// (the not-found window has nothing loaded, so this is the same background load a launch runs). Otherwise says
    /// which folders it is missing. True when the install is loading.
    /// </summary>
    public bool TryUseInstall(string folder)
    {
        if (!IsInstallMissing)
            return false;
        // Picked a level or two inside the install, Check finds it above; picked the folder that holds it (steamapps\common,
        // a Steam library), it is found below. Only when neither has it is the folder refused.
        if ((GameEnvironment.Check(folder, out var missing) ?? (missing.Count > 0 ? InstallBelow(folder) : null)) is not { } root)
        {
            var name = Path.GetFileName(folder.TrimEnd('\\', '/'));
            if (name.Length == 0)
                name = folder;
            if (missing.Count == 0)
            {
                LocateError = $"Apex couldn't reach {name}. Check the drive is connected, or choose another folder.";
                return false;
            }
            LocateError =$"No {string.Join(" or ", missing)} folder in {name}. Choose the Black Ops III folder the mod tools are installed in.";
            return false;
        }

        var env = new GameEnvironment(root);
        if (!env.IsAvailable)
        {
            LocateError = "Apex couldn't read that folder. Choose the Black Ops III folder the mod tools are installed in.";
            return false;
        }

        _settings.Bo3Root = root;
        _settings.Save();
        _env = env;
        UseInstallRoots();
        LocateError = "";
        _liveMode = true; // before the state flips, so the status bar never reads the window as sample data
        IsInstallMissing = false;
        if (_sessionRoot is not null)
        {
            OpenSessionJournal(_sessionRoot);
            OnPropertyChanged(nameof(IsSessionKept));
            OnPropertyChanged(nameof(SessionKeptText));
            OnPropertyChanged(nameof(SessionChipTip));
        }
        StartLive();
        return true;
    }

    /// <summary>
    /// The install inside <paramref name="folder"/>, up to three levels down (a Steam library holds it at
    /// steamapps\common\Call of Duty Black Ops III), or null. Gives up past a few hundred folders or the probe timeout.
    /// </summary>
    private static string? InstallBelow(string folder)
    {
        var search = Task.Run(() =>
        {
            var level = new List<string> { folder };
            var looked = 0;
            for (var depth = 0; depth < 3 && level.Count > 0; depth++)
            {
                var next = new List<string>();
                foreach (var dir in level)
                {
                    IEnumerable<string> children;
                    try { children = Directory.EnumerateDirectories(dir).ToList(); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                    foreach (var child in children)
                    {
                        if (++looked > 400)
                            return null;
                        if (Directory.Exists(Path.Combine(child, "deffiles")) && Directory.Exists(Path.Combine(child, "source_data")))
                            return child;
                        next.Add(child);
                    }
                }
                level = next;
            }
            return (string?)null;
        });
        if (!search.Wait(GameEnvironment.ProbeTimeout) || search.Result is not { } found)
            return null;
        return GameEnvironment.Check(found, out _);
    }

    // ── Live ingestion (§4.1) ─────────────────────────────────────────────────
    /// <summary>
    /// Background pipeline: parse deffile schemas, then index the GDT catalog in priority waves,
    /// publishing to the browser in batches. source_data indexes first (then parents resolve), then
    /// the xanim_export / model_export sidecars. The read-only watcher starts only once loading ends.
    /// </summary>
    private async Task RunLiveLoadAsync()
    {
        try
        {
            // a+b overlapped (WI-9): schema parsing needs no GDT data and the index wave needs no
            // schemas, so kick the schema load and start indexing source_data concurrently instead of
            // blocking the first visible GDT group on the serial schema parse. Schemas are populated
            // (and gate asset-open) the instant they are ready — usually well before the wave finishes.
            // The extensions come with the schemas they add to, so an asset opened as soon as it can be has them.
            var schemaTask = Task.Run(() =>
            {
                Services.Extensions.ExtensionRegistry.Load();
                return GdfSchemaLoader.LoadAll(_env.DeffilesDir!, _ => { });
            });
            _ = schemaTask.ContinueWith(t => Dispatcher.UIThread.Post(() =>
                {
                    SchemaRegistry.Populate(t.Result);
                    _schemasReady = true;
                }), TaskContinuationOptions.OnlyOnRanToCompletion);

            // Catalog in priority order, split into waves.
            var sources = GdtCatalog.Enumerate(_env);
            var sourceData = new List<GdtSource>();
            var sidecars = new List<GdtSource>();
            foreach (var s in sources)
            {
                if (s.RelativeName.StartsWith("source_data/", StringComparison.OrdinalIgnoreCase))
                    sourceData.Add(s);
                else
                    sidecars.Add(s);
            }

            await IndexWaveAsync(sourceData, "source_data");
            // WI-9: GDT indexing is independent of schema loading. Observe the schema fault here so
            // an enumeration/IO fault in LoadAll can't short-circuit ResolveParents, the sidecar wave,
            // or StartWatcher — the fully-indexed source_data tree must still finalise and go live.
            try { await schemaTask; }
            catch (Exception schemaEx)
            {
                await Dispatcher.UIThread.InvokeAsync(() => Alert(
                    "Couldn't read the asset definitions (deffiles). Assets are still browsable, but property "
                    + "labels, validation and typed editors are unavailable.", isError: false, detail: schemaEx.Message));
            }
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                // c. Resolve derived-asset parents once source_data is fully loaded. Types resolve in
                // place; collapsed groups build their rows lazily on expand so no tree rebuild is needed.
                ResolveAllParents();
                RefreshOpenPreviews();
            });

            if (sidecars.Count > 0)
            {
                await IndexWaveAsync(sidecars, "sidecar GDTs");
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    ResolveAllParents();
                    RefreshOpenPreviews();
                });
            }

            // Finalise: quick-filter options, status/mode chips, ONE full browser rebuild, then watch.
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                EndExplorerLoading(finished: true);
                BuildFilterOptions();
                Status = $"Loaded {Plural(GdtCount, "GDT")} · {Plural(TotalCount, "asset")} from Black Ops III";
                ApplyFilter();
                RefreshStartPage();
                StartWatcher();
                ReportExtensions();
                RestoreSession();
                // Debug hook: APEX_STARTUP_OPEN=name[,name…] opens assets once ingestion lands.
                // Used by scripted screenshot verification (no UI automation exists); inert otherwise.
                if (Environment.GetEnvironmentVariable("APEX_STARTUP_OPEN") is { Length: > 0 } startupOpen)
                    foreach (var wanted in startupOpen.Split(','))
                        if (_db.Assets.FirstOrDefault(a => a.Name.Equals(wanted.Trim(), StringComparison.OrdinalIgnoreCase)) is { } rec)
                            OpenAsset(rec);
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() => EndExplorerLoading(finished: true));
            await Dispatcher.UIThread.InvokeAsync(() => Alert(
                "Loading the GDTs stopped partway, so the asset list may be incomplete. Restart Apex to try again.",
                detail: ex.Message));
        }
    }

    /// <summary>
    /// Indexes one wave of GDT sources on the thread pool while a UI-thread consumer drains completed
    /// files in batches (~120 ms / ≤200 files), keeping any single UI update well under the 50 ms budget.
    /// </summary>
    private async Task IndexWaveAsync(IReadOnlyList<GdtSource> sources, string waveLabel)
    {
        if (sources.Count == 0)
            return;

        var queue = new ConcurrentQueue<GdtFile>();
        var produced = 0;

        var producer = Task.Run(() =>
            Parallel.For(0, sources.Count,
                new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount) },
                i =>
                {
                    GdtFile file;
                    try { file = GdtLoader.IndexToGdtFile(sources[i]); }
                    catch { file = new GdtFile { Name = sources[i].RelativeName }; }
                    queue.Enqueue(file);
                    Interlocked.Increment(ref produced);
                }));

        while (!producer.IsCompleted || !queue.IsEmpty)
        {
            await Task.Delay(120);
            var batch = new List<GdtFile>(256);
            while (batch.Count < 200 && queue.TryDequeue(out var f))
                batch.Add(f);
            if (batch.Count == 0)
                continue;
            var done = Volatile.Read(ref produced);
            await Dispatcher.UIThread.InvokeAsync(() => PublishBatch(batch, done, sources.Count, waveLabel));
        }

        await producer; // surface any Parallel.For fault
        var tail = new List<GdtFile>();
        while (queue.TryDequeue(out var f))
            tail.Add(f);
        if (tail.Count > 0)
            await Dispatcher.UIThread.InvokeAsync(() => PublishBatch(tail, sources.Count, sources.Count, waveLabel));
    }

    /// <summary>
    /// Appends a batch of freshly-indexed files to the database (UI thread). WI-5: instead of a full
    /// tree rebuild per batch (which cleared/reallocated every GDT node and replaced FlatRows ~10×,
    /// collapsing user-expanded groups), append the new GDT group rows in place with a single range
    /// insert and tick the counts. If a filter is active the tree refresh is deferred to wave end
    /// (counts still update); the finalise pass runs the one canonical ApplyFilter.
    /// </summary>
    private void PublishBatch(List<GdtFile> files, int done, int total, string waveLabel)
    {
        var appendable = _liveMode && _tokens.Count == 0 && Grouping == ExplorerGrouping.Gdt;
        var newRows = appendable ? new List<BrowserNode>(files.Count) : null;
        BeginMarkHints();
        foreach (var f in files)
        {
            _db.Gdts.Add(f);
            _db.Assets.AddRange(f.Assets);
            foreach (var a in f.Assets)
                IndexAdd(a);
            if (newRows is not null)
            {
                var node = MakeCollapsedGroupNode(f, () => BuildGdtChildren(new List<AssetRecord>(f.Assets), expandTypes: true), f.Assets);
                _roots.Add(node);
                newRows.Add(node);
            }
        }

        _markHints = null;
        TotalCount = _db.Assets.Count;
        GdtCount = _db.Gdts.Count;
        if (newRows is { Count: > 0 })
            FlatRows.InsertRange(FlatRows.Count, newRows);
        if (FlatRows.Count > 0)
            EndExplorerLoading(finished: false);
        // Only reflect the growing totals in the summary when no filter is active; a filtered view keeps
        // its match counts (the deferred wave-end ApplyFilter refreshes them).
        if (_tokens.Count == 0)
        {
            MatchCount = TotalCount;
            BrowserSummary = $"{TotalCount:N0} assets · {GdtCount:N0} GDTs";
        }
        ShowProgress($"Indexing {waveLabel} · {done:N0} of {total:N0} files · {Plural(TotalCount, "asset")}");
    }

    /// <summary>Creates a collapsed GDT group row whose type/asset children are built lazily on first expand.</summary>
    private BrowserNode MakeCollapsedGroupNode(GdtFile gdt, Func<List<BrowserNode>> childFactory, IReadOnlyList<AssetRecord> members)
    {
        var node = NewGdtNode(gdt, members, expanded: false);
        node.SetLazyChildren(childFactory);
        return node;
    }

    /// <summary>
    /// A GDT's row: its file name leads ("zm_weapons"), its folder follows dim, and the full path is the tooltip and the
    /// key its expansion is kept by. The marks roll up from <paramref name="members"/>.
    /// </summary>
    private BrowserNode NewGdtNode(GdtFile gdt, IReadOnlyList<AssetRecord> members, bool expanded)
    {
        var shortName = ShortGdt(gdt.Name);
        var node = new BrowserNode(0, ToggleNode)
        {
            Title = gdt.Name,
            DisplayName = shortName,
            Prefix = shortName,
            Folder = AssetQuery.GdtFolder(gdt.Name),
            Tip = gdt.Name,
            Glyph = "▣",
            GlyphBrush = TypeStyles.Brush("gdt"),
            Badge = members.Count.ToString("N0"),
            Gdt = gdt,
            Members = members,
            IsExpanded = expanded,
        };
        node.RefreshMarks(HasProblems, _markHints is not { } hints || hints.Gdts.Contains(gdt.Name));
        return node;
    }

    /// <summary>
    /// While a rebuild makes its group rows: the GDTs and types holding any asset with problems. A group outside them
    /// skips the problem lookups for its members (~125k on the install), leaving one quick pass for the changed dot.
    /// </summary>
    private (HashSet<string> Gdts, HashSet<string> Types)? _markHints;

    private void BeginMarkHints()
    {
        var gdts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var types = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (asset, count) in _problemsByAsset)
            if (count > 0)
            {
                gdts.Add(asset.GdtName);
                types.Add(asset.Type);
            }
        _markHints = (gdts, types);
    }

    /// <summary>A type's group row (under a GDT, or a root when grouping by type).</summary>
    private BrowserNode NewTypeNode(string type, int level, IReadOnlyList<AssetRecord> members)
    {
        var node = new BrowserNode(level, ToggleNode)
        {
            Title = type,
            Prefix = type,
            Glyph = TypeStyles.Glyph(type),
            GlyphBrush = TypeStyles.Brush(type),
            Badge = members.Count.ToString("N0"),
            GroupType = type,
            Members = members,
        };
        node.RefreshMarks(HasProblems, _markHints is not { } hints || hints.Types.Contains(type));
        return node;
    }

    private bool HasProblems(AssetRecord asset) => _problemsByAsset.GetValueOrDefault(asset) > 0;

    /// <summary>Stops the live file watcher and pending debounce timers, frees preview modules and closes the session journal (app shutdown).</summary>
    public void Dispose()
    {
        _debounce.Stop();
        _paletteDebounce.Stop();
        _simulators?.Dispose();
        FlushSettings();
        FlushSessionToDisk(closing: true);
        _journal?.Dispose();
        _journal = null;
        _watcher?.Dispose();
        _watcher = null;
    }

    // ── Read-only live watcher (§3.4) ─────────────────────────────────────────
    private void StartWatcher()
    {
        if (_watcher is not null || !_liveMode)
            return;
        _watcher = new GdtWatcher();
        _watcher.Changed += OnGdtsChangedOnDisk;
        _watcher.WatchAll(_env);
    }

    private void OnGdtsChangedOnDisk(GdtChangeBatch batch)
    {
        // Background thread: re-index the whole burst in parallel, then marshal it as one UI pass.
        var indexed = new List<GdtIndexEntry>?[batch.Changed.Count];
        var stamps = new Services.Save.GdtStamp?[batch.Changed.Count];
        Parallel.For(0, indexed.Length, i =>
        {
            try { indexed[i] = GdtIndexer.IndexFile(batch.Changed[i], out stamps[i]); }
            catch { /* skip this file; the rest of the burst still applies */ }
        });

        var changes = new List<(string Rel, string FullPath, List<GdtIndexEntry> Entries, Services.Save.GdtStamp? Stamp)>(indexed.Length);
        for (var i = 0; i < indexed.Length; i++)
        {
            // Unreadable right now (another program is still writing it): look again shortly rather than read it as empty.
            if (indexed[i] is not { } entries || stamps[i] is null)
            {
                if (File.Exists(batch.Changed[i]))
                    _watcher?.Retry(batch.Changed[i]);
                continue;
            }
            // Apex's own save: the file holds exactly what Apex wrote, and the session already points at it.
            if (stamps[i] is { } s && _ownWrites.TryGetValue(batch.Changed[i], out var own) && own == s.Hash)
                continue;
            changes.Add((ToRelativeName(batch.Changed[i]), batch.Changed[i], entries, stamps[i]));
        }
        if (changes.Count == 0 && batch.Deleted.Count == 0)
            return;
        var deletions = batch.Deleted.Select(ToRelativeName).ToList();
        Dispatcher.UIThread.Post(() => ApplyDiskBatch(changes, deletions));
    }

    /// <summary>
    /// Applies one settled burst of on-disk changes (UI thread): every file's diff first, then the
    /// counts, problem total and browser refresh once — a 300-file sync costs one filter pass, not 300.
    /// </summary>
    private void ApplyDiskBatch(
        List<(string Rel, string FullPath, List<GdtIndexEntry> Entries, Services.Save.GdtStamp? Stamp)> changes, List<string> deletions)
    {
        var removed = new List<string>(deletions.Count);
        foreach (var rel in deletions)
            if (!IsSidecarName(rel) && ApplyDiskDeletion(rel))
                removed.Add(rel);
        var reloaded = new List<(string Rel, GdtFile Gdt)>(changes.Count);
        foreach (var (rel, fullPath, entries, stamp) in changes)
        {
            if (IsSidecarName(rel))
                continue;
            // Already in step (a save's commit got here first): nothing to reload.
            if (stamp is { } s && _db.Gdts.FirstOrDefault(g => g.Name.Equals(rel, StringComparison.OrdinalIgnoreCase)) is { Stamp: { } have }
                && have.SameContent(s))
                continue;
            reloaded.Add((rel, ApplyDiskChange(rel, fullPath, entries, stamp)));
        }

        // Sidecars after their GDTs, so a GDT and its .gdtx arriving together find each other. Extension data isn't
        // in the browser or the counts.
        var sidecars = new List<string>();
        foreach (var rel in deletions)
            if (IsSidecarName(rel) && OwnerOfSidecar(rel) is { Extensions.File.FullPath: not null } owner)
            {
                GdtLoader.RemoveSidecar(owner);
                sidecars.Add(rel);
            }
        foreach (var (rel, fullPath, entries, stamp) in changes)
        {
            if (!IsSidecarName(rel) || OwnerOfSidecar(rel) is not { } owner
                || stamp is { } s && owner.Extensions?.File.Stamp is { } have && have.SameContent(s))
                continue;
            GdtLoader.ApplySidecar(owner, fullPath, entries, stamp);
            sidecars.Add(rel);
        }
        // A GDT arriving after its .gdtx (a pull delivering them in two bursts: the first had no GDT to give it to).
        foreach (var (rel, gdt) in reloaded)
        {
            if (gdt.FullPath is not { } gdtPath || gdt.Extensions?.File.FullPath is not null)
                continue;
            var sidecarPath = ExtensionSidecar.PathFor(gdtPath);
            if (!File.Exists(sidecarPath))
                continue;
            try
            {
                var entries = GdtIndexer.IndexFile(sidecarPath, out var stamp);
                GdtLoader.ApplySidecar(gdt, sidecarPath, entries, stamp);
                sidecars.Add(rel + "x");
            }
            catch (IOException)
            {
                // Still being written: its own change event brings it in.
            }
        }
        // Open tabs of those GDTs' assets show the extension data as the file now has it (their edits stay).
        foreach (var rel in sidecars)
            foreach (var tab in OpenTabs)
                if (tab.Record.GdtName.Equals(rel[..^1], StringComparison.OrdinalIgnoreCase))
                    tab.RebaseChanges();
        if (reloaded.Count == 0 && removed.Count == 0)
        {
            if (sidecars.Count > 0)
                Status = sidecars.Count == 1 ? $"Reloaded {sidecars[0]}: it changed on disk" : $"Reloaded {sidecars.Count:N0} extension files changed on disk";
            return;
        }

        TotalCount = _db.Assets.Count;
        GdtCount = _db.Gdts.Count;
        SumProblems();
        foreach (var tab in OpenTabs)
            if (_rebasedOnReload.Contains(tab.Record))
                tab.RebaseChanges();
        _rebasedOnReload.Clear();
        // Open previews of records the burst reloaded draw them as they are now.
        var reloadedGdts = new HashSet<string>(reloaded.Select(r => r.Gdt.Name), StringComparer.OrdinalIgnoreCase);
        RefreshPreviewsOf(r => !r.HasSessionEdits && reloadedGdts.Contains(r.GdtName));

        // WI-7: with no active filter, touch only the affected GDT group rows (after every file has
        // applied, so cross-file type resolution has settled); a filter can change hit membership,
        // so fall back to a full rebuild only in that case.
        if (_tokens.Count == 0 && Grouping == ExplorerGrouping.Gdt)
        {
            foreach (var rel in removed)
                RemoveGroupNode(rel);
            foreach (var (_, gdt) in reloaded)
                RefreshGroupNodeInPlace(gdt);
        }
        else
        {
            ApplyFilterNow();
        }

        Status = (reloaded.Count, removed.Count) switch
        {
            (1, 0) => $"Reloaded {reloaded[0].Rel}: it changed on disk ({Plural(reloaded[0].Gdt.Assets.Count, "asset")})",
            (0, 1) => $"Removed {removed[0]}: it was deleted on disk",
            (_, 0) => $"Reloaded {Plural(reloaded.Count, "GDT")} that changed on disk",
            (0, _) => $"Removed {Plural(removed.Count, "GDT")} that were deleted on disk",
            _ => $"Reloaded {Plural(reloaded.Count, "GDT")} that changed on disk and removed {Plural(removed.Count, "GDT")} deleted there",
        };
    }

    /// <summary>
    /// Once the catalog is in: a neutral banner for each problem there is, never one banner for several unrelated
    /// ones: one per extension Apex left parts of out, one for extension files put back after an interrupted save, one
    /// for extension data whose asset isn't in its GDT any more (renamed or deleted outside Apex). Nothing is changed or
    /// removed; each banner's tooltip lists the detail, parser messages included. Notes about members Apex doesn't know
    /// (a manifest for a newer Apex) aren't announced: they are in the extension's section tooltip.
    /// </summary>
    private void ReportExtensions()
    {
        Services.Extensions.ExtensionRegistry.CheckTargets();
        var dropped = Services.Extensions.ExtensionRegistry.Diagnostics
            .Where(d => d.Problem != Services.Extensions.ExtensionProblem.Note).ToList();
        foreach (var extension in dropped.GroupBy(d => d.Extension, StringComparer.OrdinalIgnoreCase))
        {
            var problems = extension.ToList();
            var (said, parser) = SplitParserDetail(problems[0].Message);
            var text = problems.Count == 1
                ? $"The {extension.Key} extension: {said}"
                : $"Apex left out {Plural(problems.Count, "part")} of the {extension.Key} extension. Hover for what and why.";
            var detail = problems.Count == 1
                ? parser
                : Services.Extensions.ExtensionLoader.Listed(problems.Select(d => d.Message).ToList(), 40, Environment.NewLine);
            Alert(text, isError: false, detail: detail);
        }

        var recovered = _db.Gdts.Where(g => g.Extensions is { Recovered: true }).ToList();
        if (recovered.Count > 0)
            Alert(recovered.Count == 1
                    ? $"Apex put back {Path.GetFileName(recovered[0].Name)}x: a save that was removing it didn't finish. Your unsaved changes are restored on top of it."
                    : $"Apex put back {recovered.Count:N0} extension files that a save was removing when it didn't finish. Your unsaved changes are restored on top of them.",
                isError: false,
                detail: Services.Extensions.ExtensionLoader.Listed(recovered.Select(g => g.Extensions!.File.FullPath ?? g.Name).ToList(), 40, Environment.NewLine));

        var orphans = new List<(GdtFile Gdt, AssetRecord Block)>();
        foreach (var gdt in _db.Gdts)
            if (gdt.Extensions is { } x)
                foreach (var block in x.Orphans())
                    orphans.Add((gdt, block));
        if (orphans.Count > 0)
        {
            var (gdt, block) = orphans[0];
            var file = Path.GetFileName(gdt.Name);
            Alert(orphans.Count == 1
                    ? $"{file}x has {block.Type} data for {block.Name}, which isn't in {file} (renamed or deleted outside Apex?). Apex keeps it as it is."
                    : $"{orphans.Count:N0} extension data blocks belong to assets that aren't in their GDTs (renamed or deleted outside Apex?). Apex keeps them as they are.",
                isError: false,
                detail: Services.Extensions.ExtensionLoader.Listed(orphans.Select(o => $"{Path.GetFileName(o.Gdt.Name)}x: {o.Block.Name} ({o.Block.Type})").ToList(), 40, Environment.NewLine));
        }
    }

    /// <summary>
    /// "its manifest.json isn't valid JSON (line 12), so it was left out." → the sentence without the parenthesis, and
    /// the parenthesis (a parser's or the OS's words) for the tooltip. A message without one comes back whole.
    /// </summary>
    private static (string Said, string? Detail) SplitParserDetail(string message)
    {
        var open = message.IndexOf(" (", StringComparison.Ordinal);
        var close = open < 0 ? -1 : message.IndexOf(')', open);
        if (open < 0 || close < 0)
            return (message, null);
        return (message[..open] + message[(close + 1)..], message[(open + 2)..close]);
    }

    private static bool IsSidecarName(string rel) => rel.EndsWith(ExtensionSidecar.Extension, StringComparison.OrdinalIgnoreCase);

    /// <summary>The loaded GDT a <c>.gdtx</c> belongs to ("x.gdtx" → "x.gdt"), or null.</summary>
    private GdtFile? OwnerOfSidecar(string rel) =>
        _db.Gdts.FirstOrDefault(g => g.Name.Equals(rel[..^1], StringComparison.OrdinalIgnoreCase));

    private string ToRelativeName(string fullPath)
    {
        if (_env.Bo3Root is { } root)
        {
            try { return Path.GetRelativePath(root, fullPath).Replace('\\', '/'); }
            catch { /* fall through */ }
        }
        return fullPath.Replace('\\', '/');
    }

    /// <summary>
    /// Applies a re-indexed file to the in-memory database (never writes back): add new records,
    /// remove vanished ones, refresh type/parent for survivors and drop their stale materialized
    /// properties — but records with unsaved in-session edits keep their edits (edits win).
    /// Counts and the browser are refreshed by the caller once per batch.
    /// </summary>
    private GdtFile ApplyDiskChange(string rel, string fullPath, List<GdtIndexEntry> entries, Services.Save.GdtStamp? stamp)
    {
        var gdt = _db.Gdts.FirstOrDefault(g => g.Name.Equals(rel, StringComparison.OrdinalIgnoreCase));
        if (gdt is null)
        {
            gdt = new GdtFile { Name = rel };
            _db.Gdts.Add(gdt);
        }
        gdt.FullPath = fullPath;
        gdt.Stamp = stamp;

        // Records are matched by the name they have in the file, so an asset renamed this session (and not saved
        // yet) keeps its new name and its edits. Assets created this session aren't in the file and stay as they are.
        var existing = new Dictionary<string, AssetRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in gdt.Assets)
            if (a.Disk is { } d)
                existing[d.Name] = a;
            else if (!_liveMode || gdt.Stamp is null)
                existing[a.Name] = a;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var structural = false; // any add/remove — only then do we re-sort / touch the flat asset list
        var movedAway = MovedAwayFrom(gdt.Name);
        foreach (var e in entries)
        {
            seen.Add(e.Name);
            // An asset moved to another GDT and not saved yet is still in this file: it stays moved (the save deletes
            // it here), and its old copy is found where it now sits.
            if (movedAway?.GetValueOrDefault(e.Name) is { } ghost)
            {
                if (stamp is not null)
                    ghost.Disk = new Services.Save.DiskRef(e.Name, e.IsDerived ? e.TypeOrParent : null, e.BodyOffset);
                continue;
            }
            IPropertySource? source = e.BodyLength > 0
                ? new GdtPropertySource(fullPath, e.BodyOffset, e.BodyLength)
                : null;
            var parent = e.IsDerived ? e.TypeOrParent : null;
            var type = e.IsDerived ? GdtLoader.UnknownType : e.TypeOrParent;
            var disk = stamp is null ? (Services.Save.DiskRef?)null : new Services.Save.DiskRef(e.Name, parent, e.BodyOffset);

            if (existing.TryGetValue(e.Name, out var rec))
            {
                // A rename or re-parent this session wins over the file's header until it is saved.
                var headerEdited = rec.Disk is { } was
                    && (!string.Equals(rec.Name, was.Name, StringComparison.Ordinal) || !string.Equals(rec.Parent, was.Parent, StringComparison.Ordinal));
                rec.Disk = disk;
                if (!headerEdited)
                {
                    // WI-7: keep the reverse-parent index in step when a survivor's parent changed on disk.
                    var oldParent = rec.Parent;
                    rec.Parent = parent;
                    rec.Type = type;
                    if (!string.Equals(oldParent, parent, StringComparison.OrdinalIgnoreCase))
                        IndexReparent(rec, oldParent);
                }
                // Point at the body as the file has it now; an edited record keeps its values (edits win).
                rec.Source = source;
                if (!rec.HasSessionEdits)
                {
                    if (rec.SessionBaseline is not null && rec.IsMaterialized)
                    {
                        // Seen this session but not edited: it reads as the file now, and that is what "changed" is
                        // measured against (another program's edit is not this session's change).
                        var now = source?.Materialize() ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        var props = rec.Properties;
                        props.Clear();
                        foreach (var (k, v) in now)
                            props[k] = v;
                        rec.RebaseAfterSave(now);
                        _rebasedOnReload.Add(rec);
                    }
                    else
                    {
                        rec.DropMaterialized();
                        _materialized.Remove(rec);
                    }
                }
            }
            else
            {
                var added = new AssetRecord
                {
                    Name = e.Name, Type = type, GdtName = rel, Parent = parent, Source = source, Disk = disk,
                };
                gdt.Assets.Add(added);
                _db.Assets.Add(added);
                IndexAdd(added);
                structural = true;
            }
        }

        // Drop records that vanished from the file.
        var eligible = new HashSet<AssetRecord>(existing.Values);
        // An asset with unsaved edits stays even if the file no longer has it: saving then asks what to do (never a silent loss).
        var removed = gdt.Assets.Where(a => eligible.Contains(a) && !a.HasSessionEdits && !seen.Contains(a.Disk?.Name ?? a.Name)).ToList();
        if (removed.Count > 0)
        {
            var removeSet = new HashSet<AssetRecord>(removed);
            gdt.Assets.RemoveAll(removeSet.Contains);
            _db.Assets.RemoveAll(removeSet.Contains);
            foreach (var a in removed)
            {
                _leafCache.Remove(a);
                _problemsByAsset.Remove(a);
                IndexRemove(a);
            }
            structural = true;
        }

        // Re-sort only when membership actually changed (a plain re-save re-indexes the same names in
        // the same order, so the common case skips an O(n log n) sort over a large file's assets).
        if (structural)
            gdt.Assets.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        InvalidateSnapshot(gdt);

        // WI-7: resolve types only for this file's records and their derivation descendants (via the
        // reverse-parent index) instead of rebuilding a fresh 121k name dictionary over the whole corpus.
        // Also re-seed the cross-file derivers of any REMOVED root/parent name: they are reachable only
        // via _byParent[removedName] (never in gdt.Assets), and with their parent gone their resolved
        // Type must revert toward 'unknown' instead of staying stale. IndexRemove above cleared each
        // removed record from _byName, so ResolveRootTypeLocal now yields UnknownType for these derivers.
        var seeds = gdt.Assets;
        if (removed.Count > 0)
        {
            var seedList = new List<AssetRecord>(gdt.Assets);
            foreach (var a in removed)
                if (_byParent.TryGetValue(a.Name, out var orphanKids))
                    seedList.AddRange(orphanKids);
            seeds = seedList;
        }
        ResolveTypesTargeted(seeds);
        return gdt;
    }

    /// <summary>Drops a GDT removed on disk; false when it wasn't loaded. The caller refreshes counts/browser.</summary>
    private bool ApplyDiskDeletion(string rel)
    {
        var gdt = _db.Gdts.FirstOrDefault(g => g.Name.Equals(rel, StringComparison.OrdinalIgnoreCase));
        if (gdt is null)
            return false;

        var removeSet = new HashSet<AssetRecord>(gdt.Assets);
        _db.Gdts.Remove(gdt);
        _db.Assets.RemoveAll(removeSet.Contains);
        InvalidateSnapshot(gdt);
        foreach (var a in removeSet)
        {
            _leafCache.Remove(a);
            _problemsByAsset.Remove(a);
            IndexRemove(a);
        }

        // WI-7: cross-file assets derived from a root in the deleted file are reachable only via
        // _byParent[rootName]. After the IndexRemove loop those buckets hold just the surviving
        // cross-file derivers (same-file derivers were removed too), and _byName no longer has the
        // deleted roots — so re-resolving them reverts their now-orphaned Type toward 'unknown'.
        var orphanSeeds = new List<AssetRecord>();
        foreach (var a in removeSet)
            if (_byParent.TryGetValue(a.Name, out var kids))
                orphanSeeds.AddRange(kids);
        if (orphanSeeds.Count > 0)
            ResolveTypesTargeted(orphanSeeds);
        return true;
    }

    /// <summary>
    /// Re-resolves derived-asset types for the given seed records and their transitive derivation
    /// descendants (via the reverse-parent index), using the incremental name index instead of a fresh
    /// full-corpus dictionary. Roots keep their authoritative gdf type set at index time.
    /// </summary>
    private void ResolveTypesTargeted(IReadOnlyList<AssetRecord> seeds)
    {
        InvalidateRefCatalog();
        var visited = new HashSet<AssetRecord>(seeds);
        var queue = new Queue<AssetRecord>(seeds);
        while (queue.Count > 0)
        {
            var r = queue.Dequeue();
            if (r.Parent is not null)
                r.Type = ResolveRootTypeLocal(r);
            if (_byParent.TryGetValue(r.Name, out var kids))
                foreach (var k in kids)
                    if (visited.Add(k))
                        queue.Enqueue(k);
        }
    }

    /// <summary>
    /// Wave-end type resolution for every derived record, walking parents through the name index the
    /// batches already maintain instead of building a fresh ~120k-entry dictionary on the UI thread.
    /// Same result as <see cref="GdtLoader.ResolveParents"/>: each name's index list is in
    /// <c>_db.Assets</c> order (records are appended to both together), so its first entry is the
    /// first occurrence.
    /// </summary>
    private void ResolveAllParents()
    {
        InvalidateRefCatalog();
        foreach (var r in _db.Assets)
            if (r.Parent is not null)
                r.Type = ResolveRootTypeLocal(r);
    }

    private string ResolveRootTypeLocal(AssetRecord rec)
    {
        var cur = rec;
        for (var depth = 0; depth < 64; depth++)
        {
            if (cur.Parent is null)
                return cur.Type;
            if (!_byName.TryGetValue(cur.Parent, out var pl) || pl.Count == 0)
                return GdtLoader.UnknownType;
            var parent = pl[0];
            if (ReferenceEquals(parent, cur))
                return GdtLoader.UnknownType;
            cur = parent;
        }
        return GdtLoader.UnknownType;
    }

    /// <summary>Replaces (or appends) a single GDT's group row + descendant rows in place (unfiltered, WI-7).</summary>
    private void RefreshGroupNodeInPlace(GdtFile gdt)
    {
        var oldIndex = _roots.FindIndex(r => r.Title.Equals(gdt.Name, StringComparison.OrdinalIgnoreCase));
        var fresh = MakeCollapsedGroupNode(gdt,
            () => BuildGdtChildren(new List<AssetRecord>(gdt.Assets), expandTypes: true), gdt.Assets);

        if (oldIndex < 0)
        {
            _roots.Add(fresh);
            FlatRows.InsertRange(FlatRows.Count, new[] { fresh });
            return;
        }

        var old = _roots[oldIndex];
        var wasExpanded = old.IsExpanded;
        _roots[oldIndex] = fresh;

        var rowIndex = FlatRows.IndexOf(old);
        if (rowIndex < 0)
            return; // group not currently visible in the flat list — nothing to splice
        var count = 1;
        while (rowIndex + count < FlatRows.Count && FlatRows[rowIndex + count].Level > old.Level)
            count++;

        if (wasExpanded)
        {
            fresh.EnsureChildren();
            fresh.IsExpanded = true;
        }
        var newRows = new List<BrowserNode> { fresh };
        CollectVisible(fresh, newRows);

        FlatRows.RemoveRange(rowIndex, count);
        FlatRows.InsertRange(rowIndex, newRows);
    }

    /// <summary>Removes a single GDT's group row + descendant rows from the flat list (unfiltered, WI-7).</summary>
    private void RemoveGroupNode(string rel)
    {
        var oldIndex = _roots.FindIndex(r => r.Title.Equals(rel, StringComparison.OrdinalIgnoreCase));
        if (oldIndex < 0)
            return;
        var old = _roots[oldIndex];
        _roots.RemoveAt(oldIndex);
        var rowIndex = FlatRows.IndexOf(old);
        if (rowIndex < 0)
            return;
        var count = 1;
        while (rowIndex + count < FlatRows.Count && FlatRows[rowIndex + count].Level > old.Level)
            count++;
        FlatRows.RemoveRange(rowIndex, count);
    }

    /// <summary>Returns a browser leaf node for an asset, creating and caching one on first use.</summary>
    private BrowserNode GetOrCreateLeaf(AssetRecord asset)
    {
        if (!_leafCache.TryGetValue(asset, out var node))
            _leafCache[asset] = node = BrowserNode.ForAsset(asset, ToggleNode);
        return node;
    }

    /// <summary>
    /// Re-resolves every open preview (each tab's and the cached referenced-asset ones) after the catalog grows (wave
    /// end). A tab opened mid-load can build its scene through <see cref="FindAsset"/> before the GDT that owns its
    /// materials/images is indexed, leaving parts permanently untextured — this settles them once the references become
    /// resolvable. No-op outside load (tabs opened later see a full index).
    /// </summary>
    private void RefreshOpenPreviews() => RefreshPreviewsOf(_ => true);

    /// <summary>Finds an asset by name, preferring the given type when supplied. O(1) via the name index (WI-4).</summary>
    private AssetRecord? FindAsset(string type, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || !_byName.TryGetValue(name, out var list) || list.Count == 0)
            return null;
        // Preserve the old semantics: a name+type match anywhere in the (rare) duplicate list wins,
        // otherwise the first record indexed for that name (which is catalog/priority order).
        if (type.Length > 0)
        {
            foreach (var a in list)
                if (a.Type.Equals(type, StringComparison.OrdinalIgnoreCase))
                    return a;
            if (IsWeaponFamilyRef(type))
                foreach (var a in list)
                    if (Services.Extensions.ExtensionManifest.IsWeapon(a.Type))
                        return a;
        }
        return list[0];
    }

    // ── WI-4: index maintenance ────────────────────────────────────────────────
    /// <summary>Adds a record to the name and reverse-parent indexes. Idempotent per (name, record).</summary>
    private void IndexAdd(AssetRecord a)
    {
        InvalidateRefCatalog();
        if (!_byName.TryGetValue(a.Name, out var list))
            _byName[a.Name] = list = new List<AssetRecord>(1);
        if (!list.Contains(a))
            list.Add(a);
        if (a.Parent is { } p)
        {
            if (!_byParent.TryGetValue(p, out var kids))
                _byParent[p] = kids = new List<AssetRecord>(1);
            if (!kids.Contains(a))
                kids.Add(a);
        }
    }

    /// <summary>Removes a record from every index (name, parent, materialized registry).</summary>
    private void IndexRemove(AssetRecord a)
    {
        InvalidateRefCatalog();
        if (_byName.TryGetValue(a.Name, out var list))
        {
            list.Remove(a);
            if (list.Count == 0)
                _byName.Remove(a.Name);
        }
        if (a.Parent is { } p && _byParent.TryGetValue(p, out var kids))
        {
            kids.Remove(a);
            if (kids.Count == 0)
                _byParent.Remove(p);
        }
        _materialized.Remove(a);
    }

    /// <summary>Re-keys a record after its <see cref="AssetRecord.Name"/> changed (rename).</summary>
    private void IndexRename(AssetRecord a, string oldName)
    {
        InvalidateRefCatalog();
        if (_byName.TryGetValue(oldName, out var list))
        {
            list.Remove(a);
            if (list.Count == 0)
                _byName.Remove(oldName);
        }
        if (!_byName.TryGetValue(a.Name, out var newList))
            _byName[a.Name] = newList = new List<AssetRecord>(1);
        if (!newList.Contains(a))
            newList.Add(a);
    }

    /// <summary>Moves a record between reverse-parent buckets after its <see cref="AssetRecord.Parent"/> changed.</summary>
    private void IndexReparent(AssetRecord a, string? oldParent)
    {
        InvalidateRefCatalog();
        if (oldParent is { } op && _byParent.TryGetValue(op, out var oldKids))
        {
            oldKids.Remove(a);
            if (oldKids.Count == 0)
                _byParent.Remove(op);
        }
        if (a.Parent is { } np)
        {
            if (!_byParent.TryGetValue(np, out var kids))
                _byParent[np] = kids = new List<AssetRecord>(1);
            if (!kids.Contains(a))
                kids.Add(a);
        }
    }

    /// <summary>Rebuilds the name/parent indexes from scratch (once, at wave end / mock load).</summary>
    private void RebuildIndexes()
    {
        _byName.Clear();
        _byParent.Clear();
        foreach (var a in _db.Assets)
            IndexAdd(a);
    }

    /// <summary>Records that an asset's properties are now materialized so reference scans can find it.</summary>
    private void MarkMaterialized(AssetRecord a)
    {
        if (a.IsMaterialized)
            _materialized.Add(a);
    }

    // ── WI-8: per-GDT immutable snapshots for off-thread scans ─────────────────
    /// <summary>Returns an isolated snapshot of a GDT's assets, reusing the cached copy until invalidated.</summary>
    private IReadOnlyList<AssetRecord> GetSnapshot(GdtFile g)
    {
        if (!_snapshotCache.TryGetValue(g, out var snap))
            _snapshotCache[g] = snap = g.Assets.ToArray();
        return snap;
    }

    /// <summary>Drops a GDT's cached snapshot after its asset list was structurally mutated (UI thread).</summary>
    private void InvalidateSnapshot(GdtFile g)
    {
        _snapshotCache.Remove(g);
        _narrowMatches = null; // an asset came or went: the palette's next search starts from the whole corpus
    }

    // ── Browser ──────────────────────────────────────────────────────────────
    private readonly List<BrowserNode> _roots = new();

    /// <summary>Flattened visible tree rows; the ListBox virtualizes over this. Range-splice capable (WI-6).</summary>
    [ObservableProperty]
    private RangeObservableCollection<BrowserNode> _flatRows = new();

    [ObservableProperty]
    private string _filterText = "";

    [ObservableProperty]
    private int _matchCount;

    [ObservableProperty]
    private int _totalCount;

    [ObservableProperty]
    private int _gdtCount;

    [ObservableProperty]
    private string _browserSummary = "";

    /// <summary>The empty Explorer's one line: nothing matched the filter, or there are no GDTs to list at all.</summary>
    [ObservableProperty]
    private string _explorerEmptyText = "";

    /// <summary>The install's GDTs are still being read and none has landed yet.</summary>
    private bool _explorerLoading;

    /// <summary>"Loading GDTs…" in the Explorer: only once the list has stayed empty past 150 ms of loading.</summary>
    [ObservableProperty]
    private bool _showExplorerLoading;

    private DispatcherTimer? _loadingDelay;

    /// <summary>Starts the Explorer's loading state; it shows if no GDT has landed 150 ms from now.</summary>
    private void BeginExplorerLoading()
    {
        _explorerLoading = true;
        _loadingDelay ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _loadingDelay.Tick += LoadingDelayTick;
        _loadingDelay.Start();
    }

    private void LoadingDelayTick(object? sender, EventArgs e)
    {
        _loadingDelay!.Stop();
        _loadingDelay.Tick -= LoadingDelayTick;
        ShowExplorerLoading = _explorerLoading && FlatRows.Count == 0;
    }

    /// <summary>Rows are in (the first batch), or the load has ended: the loading line goes.</summary>
    private void EndExplorerLoading(bool finished)
    {
        if (finished)
        {
            _explorerLoading = false;
            _loadingDelay?.Stop();
        }
        ShowExplorerLoading = false;
    }

    private void SetBrowserEmpty(int matches, bool hasFilter)
    {
        // While the GDTs are still arriving an empty, unfiltered list is the loading state, not "no GDTs".
        BrowserIsEmpty = matches == 0 && !IsInstallMissing && (hasFilter || !_explorerLoading);
        ExplorerEmptyText = hasFilter || _db.Assets.Count > 0
            ? "No assets match. Shorten the name or remove a filter."
            : $"No GDTs to list. Make one with New GDT ({Commands.CommandCatalog.Get(Commands.CommandCatalog.NewGdt).GestureText}).";
    }

    partial void OnFilterTextChanged(string value)
    {
        _openTopWhenReady = false; // Enter meant the query as it was then
        SyncExplorerQuery(value);
        // Clearing the field (Esc, ✕, "Clear all filters", backspacing to nothing) applies at once:
        // the unfiltered tree is where the user is going back to, not a query still being typed.
        if (value.Trim().Length == 0)
        {
            _facetType = null;
            ApplyFilterNow();
            return;
        }
        _debounce.Stop();
        _debounce.Start();
    }

    private int _filterGen;
    private CancellationTokenSource? _filterScanCts;

    /// <summary>
    /// Per-GDT hits of the last completed filter pass, stamped with the exact token list they were
    /// computed for. OpenTable reuses them instead of re-scanning the corpus (a prop: query re-scan
    /// costs ~1 s on the UI thread). Safe because _tokens is a fresh list per ApplyFilter (reference
    /// stamp mismatches on any filter change), the cache is cleared at ApplyFilter entry (so a pending
    /// off-thread scan can't be shadowed by stale hits), and every asset mutation while a filter is
    /// active funnels through ApplyFilterNow (watcher, new/duplicate/delete).
    /// </summary>
    private List<(GdtFile Gdt, List<AssetRecord> Hits)>? _hitCache;
    private List<QueryToken>? _hitCacheTokens;

    private void ApplyFilter()
    {
        _hitCache = null;
        _hitCacheTokens = null;
        _tokens = ParseQuery(FilterText);
        SyncFilterPills();

        var tokens = _tokens;
        var hasFilter = tokens.Count > 0;
        var wantProblems = tokens.Any(t => t.Kind == TokenKind.Problems);
        var gen = ++_filterGen;
        // Any newer filter supersedes an in-flight scan: stop it reading files, not just discard it.
        _filterScanCts?.Cancel();
        _filterScanCts = null;

        // §4.3: a text scan over ~95k live assets that has to read property values (prop:/is:modified)
        // can exceed the 30 ms UI budget — run it off the UI thread and marshal the result back.
        var expensive = _liveMode && hasFilter && _db.Assets.Count > 5000
            && AssetQuery.TouchesProperties(tokens);
        if (expensive)
        {
            var cancel = (_filterScanCts = new CancellationTokenSource()).Token;
            // Snapshot each GDT's asset list (not just the outer Gdts collection): the background scan
            // enumerates these on a thread-pool thread while the UI thread's read-only watcher and
            // new/duplicate/delete commands structurally mutate the live gdt.Assets lists. WI-8: the
            // per-GDT copies are cached and only re-taken for GDTs whose list changed since last scan,
            // so a steady-state repeated prop: keystroke re-copies nothing instead of ~121k refs/GDT.
            var gdtSnapshot = _db.Gdts
                .Select(g => (Gdt: g, Assets: GetSnapshot(g)))
                .ToArray();
            var problemSet = wantProblems
                ? new HashSet<AssetRecord>(_problemsByAsset.Where(kv => kv.Value > 0).Select(kv => kv.Key))
                : null;
            var extensions = ExtensionSearch(tokens);
            Task.Run(() =>
            {
                try
                {
                    var per = ComputeHits(gdtSnapshot, tokens, problemSet is null ? null : problemSet.Contains, cancel, extensions);
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (gen != _filterGen)
                            return; // a newer filter superseded this scan
                        BuildRootsFromHits(per, hasFilter);
                    });
                }
                catch
                {
                    // Never leave a background scan's fault unobserved; a fresh filter/watcher pass recovers.
                }
            });
            return;
        }

        Func<AssetRecord, bool>? problemGate = wantProblems
            ? a => _problemsByAsset.GetValueOrDefault(a) > 0
            : null;
        var live = _db.Gdts
            .Select(g => (Gdt: g, Assets: (IReadOnlyList<AssetRecord>)g.Assets))
            .ToList();
        BuildRootsFromHits(ComputeHits(live, tokens, problemGate, extensions: ExtensionSearch(tokens)), hasFilter);
    }

    /// <summary>
    /// What <c>prop:</c> reads besides an asset's GDT values: its installed extensions' blocks in its GDT's
    /// <c>.gdtx</c>, looked up from a snapshot taken here (the scan may run off the UI thread). Null when the query has
    /// no <c>prop:</c> or no extension data is loaded, so search is exactly as without extensions.
    /// </summary>
    private AssetQuery.ExtensionValues? ExtensionSearch(IReadOnlyList<QueryToken> tokens)
    {
        if (!tokens.Any(t => t.Kind == TokenKind.Prop) || Services.Extensions.ExtensionRegistry.Manifests is not { Count: > 0 } manifests
            || SidecarsByGdt() is not { } sidecars)
            return null;
        var ids = new HashSet<string>(manifests.Select(m => m.Id), StringComparer.OrdinalIgnoreCase);
        // By GDT, then asset: two lookups per asset the scan asks about, no string built.
        var blocks = new Dictionary<string, Dictionary<string, List<AssetRecord>>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (gdt, x) in sidecars)
            foreach (var b in x.Blocks)
                if (b.Parent is null && ids.Contains(b.Type))
                {
                    if (!blocks.TryGetValue(gdt, out var byName))
                        blocks[gdt] = byName = new Dictionary<string, List<AssetRecord>>(StringComparer.OrdinalIgnoreCase);
                    if (!byName.TryGetValue(b.Name, out var list))
                        byName[b.Name] = list = new List<AssetRecord>(1);
                    list.Add(b);
                }
        if (blocks.Count == 0)
            return null;
        return asset => blocks.TryGetValue(asset.GdtName, out var byName) && byName.TryGetValue(asset.Name, out var list)
            ? list.Select(b => b.ScanProperties)
            : Array.Empty<IReadOnlyDictionary<string, string>>();
    }

    /// <summary>Auto-expand the filtered hierarchy only for reasonably small result sets (WI-1); above
    /// this, matched GDT groups stay collapsed with a match-count badge and build rows lazily on expand.</summary>
    private const int AutoExpandHitLimit = 2000;

    /// <summary>
    /// Computes, per GDT, the assets matching the query. Pure over the supplied asset snapshots —
    /// safe to run off the UI thread as long as the caller passes isolated <c>Assets</c> lists.
    /// </summary>
    private static List<(GdtFile Gdt, List<AssetRecord> Hits)> ComputeHits(
        IReadOnlyList<(GdtFile Gdt, IReadOnlyList<AssetRecord> Assets)> gdts,
        List<QueryToken> tokens, Func<AssetRecord, bool>? problemGate, CancellationToken cancel = default,
        AssetQuery.ExtensionValues? extensions = null)
    {
        var hasFilter = tokens.Count > 0;
        var perGdt = new List<AssetRecord>[gdts.Count];
        if (!hasFilter)
        {
            for (var i = 0; i < gdts.Count; i++)
            {
                var assets = gdts[i].Assets;
                var hits = assets as List<AssetRecord> ?? assets.ToList();
                if (problemGate is not null)
                    hits = hits.Where(problemGate).ToList();
                perGdt[i] = hits;
            }
        }
        else
        {
            // WI-2 adoption: MatchAll opens a GdtFileScan so a prop:/is:modified query reads this
            // GDT's backing .gdt exactly once (one file read per GDT) instead of once per asset —
            // the order-of-magnitude scan win. Non-property tokens take its cheap in-memory path.
            // GDTs are independent (the scan scope is per thread), so they fan out across cores;
            // results land by index, keeping GDT order. A superseded query cancels between GDTs.
            Parallel.For(0, gdts.Count, new ParallelOptions { CancellationToken = cancel },
                i => perGdt[i] = AssetQuery.MatchAll(gdts[i].Assets, tokens, problemGate, extensions));
        }

        var result = new List<(GdtFile, List<AssetRecord>)>();
        for (var i = 0; i < gdts.Count; i++)
        {
            var hits = perGdt[i];
            if (hits.Count == 0 && !(gdts[i].Assets.Count == 0 && !hasFilter))
                continue;
            result.Add((gdts[i].Gdt, hits));
        }
        return result;
    }

    /// <summary>
    /// Builds the browser rows from computed hits (UI thread). Three shapes, chosen by the query and
    /// the Group menu:
    ///   · a free-text query → flat, ranked results with the match underlined and type facets (a tree
    ///     hides matches inside collapsed nodes);
    ///   · Group: GDT file → GDT → type → asset, where a GDT holding a single type skips the type level;
    ///   · Group: Type → type → asset (GDT as a faint suffix).
    /// Live mode keeps groups collapsed and builds their rows lazily on first expand (WI-1).
    /// </summary>
    private void BuildRootsFromHits(List<(GdtFile Gdt, List<AssetRecord> Hits)> perGdt, bool hasFilter)
    {
        _hitCache = perGdt;
        _hitCacheTokens = _tokens;

        // WI-5: preserve which groups were expanded across an *unfiltered* rebuild (load waves and
        // returning to the no-filter view). A filtered rebuild deliberately starts from the collapse/
        // auto-expand rules instead, so a broad filter stays lazy (WI-1) rather than inheriting a prior
        // view's expansions.
        HashSet<string>? wasExpanded = null;
        if (!hasFilter)
            foreach (var r in _roots)
                if (r.IsExpanded)
                    (wasExpanded ??= new HashSet<string>(StringComparer.Ordinal)).Add(r.Title);

        _roots.Clear();
        var matches = 0;
        foreach (var (_, hits) in perGdt)
            matches += hits.Count;
        BeginMarkHints();

        var rows = new List<BrowserNode>(Math.Min(matches, 8192) + 64);
        var search = _tokens.FirstOrDefault(t => t.Kind == TokenKind.Name)?.Value;
        IsSearchResults = search is not null;
        var shown = matches;

        if (search is not null)
        {
            shown = BuildSearchRows(perGdt, search, rows);
        }
        else
        {
            _facetType = null; // a type facet narrows a search; with the search gone it goes too
            SearchFacets.ReplaceAll(Array.Empty<SearchFacet>());
            ResultsNote = "";
            if (!hasFilter && _pinned.Count > 0)
            {
                rows.Add(BrowserNode.Heading("Pinned"));
                foreach (var name in _pinned)
                    if (FindAsset("", name) is { } pinnedAsset)
                        rows.Add(MakePinnedRow(pinnedAsset));
                rows.Add(BrowserNode.Heading("All assets"));
            }

            // WI-1: live mode keeps groups collapsed even when a filter is active — matched groups
            // show a match-count badge and build their rows lazily on expand, so a broad filter never
            // eagerly allocates tens of thousands of BrowserNodes. Only reasonably small filtered sets
            // auto-expand into the full hierarchy.
            var autoExpand = hasFilter && matches <= AutoExpandHitLimit;
            var collapseGroups = _liveMode && !autoExpand;
            var expandTypes = matches > 0 && matches <= 400;

            switch (Grouping)
            {
                case ExplorerGrouping.Type:
                    BuildTypeRoots(perGdt, collapseGroups, expandTypes || autoExpand, wasExpanded);
                    break;
                default:
                    BuildGdtRoots(perGdt, collapseGroups, expandTypes, wasExpanded, hasFilter);
                    break;
            }

            foreach (var root in _roots)
            {
                rows.Add(root);
                if (root.IsExpanded)
                    CollectVisible(root, rows);
            }
        }

        _markHints = null;
        FlatRows = new RangeObservableCollection<BrowserNode>(rows);

        MatchCount = matches;
        // Drives the browser's empty state: a blank pane with no explanation and no way out is the
        // worst possible result of a mistyped query.
        SetBrowserEmpty(matches, hasFilter);
        BrowserSummary = _tokens.Count == 0
            ? $"{TotalCount:N0} assets · {GdtCount:N0} GDTs"
            : $"{matches:N0} of {TotalCount:N0} assets";
        // What the list shows, a type facet included, out of everything loaded: the count and the rows agree.
        ResultCountText = $"{shown:N0} of {TotalCount:N0} assets";
        OpenTableIfAwaited();
        if (_openTopWhenReady)
            OpenTopResultNow();
    }

    private void BuildGdtRoots(List<(GdtFile Gdt, List<AssetRecord> Hits)> perGdt, bool collapseGroups,
        bool expandTypes, HashSet<string>? wasExpanded, bool hasFilter)
    {
        foreach (var (gdt, hits) in perGdt)
            _roots.Add(MakeGdtRoot(gdt, hits, collapseGroups, expandTypes, wasExpanded?.Contains(gdt.Name) ?? false, hasFilter));
    }

    private BrowserNode MakeGdtRoot(GdtFile gdt, List<AssetRecord> hits, bool collapseGroups, bool expandTypes,
        bool wasExpanded, bool hasFilter)
    {
        BrowserNode gdtNode;
        if (collapseGroups)
        {
            var captured = hits;
            gdtNode = MakeCollapsedGroupNode(gdt, () => BuildGdtChildren(captured, expandTypes: true), hits);
            if (wasExpanded)
            {
                gdtNode.EnsureChildren();
                gdtNode.IsExpanded = true;
            }
        }
        else
        {
            // A filtered tree opens every matching file; the unfiltered one starts closed
            // (one row per GDT) and keeps whatever the user opened, plus the active asset's.
            gdtNode = NewGdtNode(gdt, hits, expanded: hasFilter
                || wasExpanded
                || string.Equals(ActiveTab?.Record.GdtName, gdt.Name, StringComparison.OrdinalIgnoreCase));
            gdtNode.Children.AddRange(BuildGdtChildren(hits, expandTypes));
            gdtNode.RefreshStructure();
        }
        gdtNode.Suffix = SingleTypeOf(hits) ?? "";
        return gdtNode;
    }

    /// <summary>
    /// After a single-asset change (new, duplicate, rename, delete) in the plain unfiltered GDT tree,
    /// rebuilds only that GDT's row — the same node a full rebuild would make for it — and splices its
    /// rows into the list, so the other ~2k groups, their expansion and the scroll position stay put.
    /// Falls back to the full rebuild whenever the result could differ elsewhere: an active filter or
    /// search, another grouping, or a pinned name involved (the Pinned section lists by name).
    /// </summary>
    private void RefreshAfterAssetChange(IReadOnlyCollection<GdtFile> gdts, params string[] names)
    {
        // A query still waiting out its typing delay is applied now, as the full rebuild always did.
        if (_tokens.Count > 0 || _debounce.IsEnabled || Grouping != ExplorerGrouping.Gdt
            || names.Any(n => _pinned.Contains(n, StringComparer.OrdinalIgnoreCase)))
        {
            ApplyFilterNow();
            return;
        }

        var matches = 0;
        foreach (var g in _db.Gdts)
            matches += g.Assets.Count;
        foreach (var gdt in gdts)
        {
            var index = _roots.FindIndex(r => r.Gdt == gdt);
            var old = index >= 0 ? _roots[index] : null;
            var fresh = MakeGdtRoot(gdt, gdt.Assets, collapseGroups: _liveMode, expandTypes: matches > 0 && matches <= 400,
                wasExpanded: old?.IsExpanded ?? false, hasFilter: false);
            var rows = new List<BrowserNode> { fresh };
            if (fresh.IsExpanded)
                CollectVisible(fresh, rows);

            if (old is null)
            {
                // A GDT created for this asset comes last, as it does in _db.Gdts.
                _roots.Add(fresh);
                FlatRows.InsertRange(FlatRows.Count, rows);
                continue;
            }
            _roots[index] = fresh;
            var rowIndex = FlatRows.IndexOf(old);
            if (rowIndex < 0)
                continue;
            var count = 1;
            while (rowIndex + count < FlatRows.Count && FlatRows[rowIndex + count].Level > old.Level)
                count++;
            FlatRows.RemoveRange(rowIndex, count);
            FlatRows.InsertRange(rowIndex, rows);
        }

        MatchCount = matches;
        SetBrowserEmpty(matches, hasFilter: false);
        ResultCountText = $"{matches:N0} of {TotalCount:N0} assets";
        // Splicing dropped any selection inside these rows; the Explorer re-selects the active
        // asset's row on this notification, as it does after a full rebuild.
        OnPropertyChanged(nameof(FlatRows));
    }

    /// <summary>Adds an asset in name order — what re-sorting would give — without re-sorting the GDT.</summary>
    private static void InsertByName(List<AssetRecord> assets, AssetRecord asset)
    {
        var byName = Comparer<AssetRecord>.Create((a, b) => string.CompareOrdinal(a.Name, b.Name));
        for (var i = 1; i < assets.Count; i++)
        {
            // A rename leaves its GDT out of order; restore the full sort exactly as before.
            if (byName.Compare(assets[i - 1], assets[i]) > 0)
            {
                assets.Add(asset);
                assets.Sort(byName);
                return;
            }
        }
        var at = assets.BinarySearch(asset, byName);
        assets.Insert(at < 0 ? ~at : at, asset);
    }

    private void BuildTypeRoots(List<(GdtFile Gdt, List<AssetRecord> Hits)> perGdt, bool collapseGroups,
        bool expand, HashSet<string>? wasExpanded)
    {
        var byType = new SortedDictionary<string, List<AssetRecord>>(StringComparer.Ordinal);
        foreach (var (_, hits) in perGdt)
            foreach (var a in hits)
            {
                if (!byType.TryGetValue(a.Type, out var list))
                    byType[a.Type] = list = new List<AssetRecord>();
                list.Add(a);
            }

        foreach (var (type, hits) in byType)
        {
            var captured = hits;
            var node = NewTypeNode(type, 0, hits);
            var open = (wasExpanded?.Contains(type) ?? false) || (!collapseGroups && expand);
            if (collapseGroups && !open)
            {
                node.SetLazyChildren(() =>
                {
                    var kids = new List<BrowserNode>();
                    BuildAssetHierarchy(kids, SortByName(captured), 1, showGdt: true);
                    return kids;
                });
            }
            else
            {
                BuildAssetHierarchy(node.Children, SortByName(captured), 1, showGdt: true);
                node.IsExpanded = open;
                node.RefreshStructure();
            }
            _roots.Add(node);
        }
    }

    /// <summary>Flat, ranked search results: names starting with the query first, then contains. Returns how many the list shows.</summary>
    private int BuildSearchRows(List<(GdtFile Gdt, List<AssetRecord> Hits)> perGdt, string query, List<BrowserNode> rows)
    {
        // A one-letter query matches most of the corpus, so the hits are counted and ranked where they lie: no
        // corpus-sized copies, groupings or sorts (each would be a large-object allocation and a gen 2 collection).
        var total = 0;
        var typeCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (_, hits) in perGdt)
        {
            total += hits.Count;
            foreach (var a in hits)
                System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(typeCounts, a.Type, out _)++;
        }

        // Facets count the whole result set; a selected facet narrows the list, not the query.
        // Nothing to narrow when nothing matched: the empty state speaks instead of an "All 0".
        // Labels use the GDT type names (xmodel, xanim, fx) modders know from APE.
        var byType = typeCounts.OrderByDescending(t => t.Value).ThenBy(t => t.Key, StringComparer.Ordinal).ToList();
        if (_facetType is { } ft && byType.All(t => !t.Key.Equals(ft, StringComparison.OrdinalIgnoreCase)))
            _facetType = null;
        // One type: "All 3 · weapon 3" says nothing the list doesn't. Every type is offered; the row draws what fits,
        // the picked one always, and puts the rest behind "+N".
        var facets = new List<SearchFacet>(byType.Count + 1);
        if (byType.Count > 1)
        {
            facets.Add(new SearchFacet($"All {total:N0}", null, _facetType is null, SelectFacet));
            foreach (var (type, count) in byType)
                facets.Add(new SearchFacet($"{type} {count:N0}", type, type.Equals(_facetType, StringComparison.OrdinalIgnoreCase), SelectFacet));
        }
        SearchFacets.ReplaceAll(facets);

        var facet = _facetType;
        var shown = facet is null ? total : byType.Where(t => t.Key.Equals(facet, StringComparison.OrdinalIgnoreCase)).Sum(t => t.Value);
        foreach (var a in TopRanked(perGdt, facet, query, FlatResultLimit))
        {
            var leaf = PrepareLeaf(a, 0, derived: false, showGdt: true);
            leaf.SetHighlight(query);
            rows.Add(leaf);
        }
        ResultsNote = shown > FlatResultLimit
            ? $"Showing the first {FlatResultLimit:N0} of {shown:N0}. Refine the search to see the rest."
            : "";
        return shown;
    }

    /// <summary>
    /// The first <paramref name="limit"/> hits (of <paramref name="facet"/>'s type, when set) in search rank: names
    /// starting with the query, then shorter names, then by name, ties in hit order. The first two keys are small
    /// integers, so a counting pass finds the (prefix, length) bucket the limit falls in and only the hits up to that
    /// bucket are sorted, never every hit.
    /// </summary>
    private static List<AssetRecord> TopRanked(List<(GdtFile Gdt, List<AssetRecord> Hits)> perGdt, string? facet, string query, int limit)
    {
        var maxLength = 0;
        foreach (var (_, hits) in perGdt)
            foreach (var a in hits)
                maxLength = Math.Max(maxLength, a.Name.Length);
        int Bucket(string name) => (name.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 0 : maxLength + 1) + name.Length;
        bool Shown(AssetRecord a) => facet is null || a.Type.Equals(facet, StringComparison.OrdinalIgnoreCase);

        var counts = new int[2 * (maxLength + 1)];
        foreach (var (_, hits) in perGdt)
            foreach (var a in hits)
                if (Shown(a))
                    counts[Bucket(a.Name)]++;
        var cutoff = counts.Length - 1;
        for (int b = 0, taken = 0; b < counts.Length; b++)
            if ((taken += counts[b]) >= limit)
            {
                cutoff = b;
                break;
            }

        var picked = new List<(AssetRecord Asset, int Bucket, int Order)>(limit + 64);
        var order = 0;
        foreach (var (_, hits) in perGdt)
            foreach (var a in hits)
                if (Shown(a) && Bucket(a.Name) is var bucket && bucket <= cutoff)
                    picked.Add((a, bucket, order++));
        picked.Sort((x, y) =>
        {
            var c = x.Bucket.CompareTo(y.Bucket);
            if (c == 0)
                c = string.Compare(x.Asset.Name, y.Asset.Name, StringComparison.OrdinalIgnoreCase);
            return c != 0 ? c : x.Order.CompareTo(y.Order);
        });
        var result = new List<AssetRecord>(Math.Min(picked.Count, limit));
        for (var i = 0; i < picked.Count && i < limit; i++)
            result.Add(picked[i].Asset);
        return result;
    }

    /// <summary>Result lists stop here; past it a query is too broad to scan by eye anyway.</summary>
    private const int FlatResultLimit = 5000;

    private string? _facetType;

    private void SelectFacet(SearchFacet facet)
    {
        _facetType = facet.Type;
        ApplyFilterNow();
    }

    private static List<AssetRecord> SortByName(List<AssetRecord> list)
    {
        list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return list;
    }

    /// <summary>The one type a GDT's hits share, or null when they span several.</summary>
    private static string? SingleTypeOf(List<AssetRecord> hits)
    {
        if (hits.Count == 0)
            return null;
        var t = hits[0].Type;
        for (var i = 1; i < hits.Count; i++)
            if (!hits[i].Type.Equals(t, StringComparison.OrdinalIgnoreCase))
                return null;
        return t;
    }

    /// <summary>
    /// A GDT's rows: per-type groups with nested assets — or, when the GDT holds a single type, the
    /// assets directly (the type layer would carry no information).
    /// </summary>
    private List<BrowserNode> BuildGdtChildren(List<AssetRecord> hits, bool expandTypes)
    {
        var result = new List<BrowserNode>();
        if (SingleTypeOf(hits) is not null)
        {
            BuildAssetHierarchy(result, hits, 1, showGdt: false);
            return result;
        }
        foreach (var typeGroup in hits.GroupBy(a => a.Type).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var list = typeGroup.ToList();
            var typeNode = NewTypeNode(typeGroup.Key, 1, list);
            typeNode.IsExpanded = expandTypes;
            BuildAssetHierarchy(typeNode.Children, list, 2, showGdt: false);
            typeNode.RefreshStructure();
            result.Add(typeNode);
        }
        return result;
    }

    /// <summary>
    /// Nests derived assets under their parent template (when both are in the result set) so
    /// derivation chains are visible; orphans and templates sit at <paramref name="baseLevel"/>.
    /// </summary>
    private void BuildAssetHierarchy(List<BrowserNode> into, List<AssetRecord> hits, int baseLevel, bool showGdt)
    {
        var byName = new Dictionary<string, AssetRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in hits)
            byName[a.Name] = a;

        var children = new Dictionary<AssetRecord, List<AssetRecord>>();
        var roots = new List<AssetRecord>();
        foreach (var a in hits)
        {
            if (a.Parent is { } p && byName.TryGetValue(p, out var parent) && parent != a)
            {
                if (!children.TryGetValue(parent, out var list))
                    children[parent] = list = new List<AssetRecord>();
                list.Add(a);
            }
            else
            {
                roots.Add(a);
            }
        }

        void Attach(List<BrowserNode> target, AssetRecord asset, int level)
        {
            var node = PrepareLeaf(asset, level, derived: level > baseLevel, showGdt);
            target.Add(node);
            if (children.TryGetValue(asset, out var kids))
            {
                node.IsExpanded = true;
                foreach (var kid in kids)
                    Attach(node.Children, kid, level + 1);
            }
            node.RefreshStructure();
        }

        foreach (var root in roots)
            Attach(into, root, baseLevel);
    }

    /// <summary>Resets a cached leaf for its position in the current view.</summary>
    private BrowserNode PrepareLeaf(AssetRecord asset, int level, bool derived, bool showGdt)
    {
        var node = GetOrCreateLeaf(asset);
        node.ClearChildren();
        node.IsExpanded = false;
        node.Level = level;
        node.IsDerived = derived;
        node.HasProblem = HasProblems(asset);
        node.HasChanges = asset.HasSessionEdits;
        node.Suffix = showGdt ? ShortGdt(asset.GdtName) : "";
        node.SetHighlight("");
        node.RefreshStructure();
        return node;
    }

    private BrowserNode MakePinnedRow(AssetRecord asset)
    {
        var node = new BrowserNode(0)
        {
            Title = asset.Name,
            Prefix = asset.Name,
            Glyph = TypeStyles.Glyph(asset.Type),
            GlyphBrush = TypeStyles.Brush(asset.Type),
            Asset = asset,
            IsPinnedEntry = true,
        };
        node.HasProblem = HasProblems(asset);
        node.HasChanges = asset.HasSessionEdits;
        // Pinned names come from any GDT: say which, as search results do.
        node.Suffix = ShortGdt(asset.GdtName);
        return node;
    }

    /// <summary>"zm_weapons.gdt" → "zm_weapons"; nested catalog paths keep only the file name.</summary>
    public static string ShortGdt(string gdtName) => AssetQuery.ShortGdt(gdtName);

    /// <summary>
    /// The query as typed, with each <c>type:</c> and <c>gdt:</c> that names a type or GDT that exists marked exact: a
    /// picked or fully typed name means that one (<c>type:weapon</c> is not also the weapon camos), so the counts beside
    /// the pickers are what the filter shows. A partial name (<c>type:weap</c>) still matches any part.
    /// </summary>
    private List<QueryToken> ParseQuery(string text)
    {
        var tokens = AssetQuery.Parse(text);
        for (var i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.Exact)
                continue;
            if (t.Kind == TokenKind.Type && TypeOptions.Any(o => o.Label.Equals(t.Value, StringComparison.OrdinalIgnoreCase)))
                tokens[i] = t with { Exact = true };
            else if (t.Kind == TokenKind.Gdt && _db.Gdts.Any(g => ShortGdt(g.Name).Equals(t.Value, StringComparison.OrdinalIgnoreCase)
                                                             || g.Name.Equals(t.Value, StringComparison.OrdinalIgnoreCase)))
                tokens[i] = t with { Exact = true };
        }
        return tokens;
    }

    private static void CollectVisible(BrowserNode node, List<BrowserNode> into)
    {
        foreach (var child in node.Children)
        {
            into.Add(child);
            if (child.IsExpanded && child.ChildCount > 0)
                CollectVisible(child, into);
        }
    }

    /// <summary>Expands/collapses in place so ListBox scroll position is preserved.</summary>
    public void ToggleNode(BrowserNode node)
    {
        var index = FlatRows.IndexOf(node);
        if (index < 0)
            return;

        if (node.IsExpanded)
        {
            node.IsExpanded = false;
            // Count the contiguous descendant rows, then splice them out in one range removal (WI-6).
            var count = 0;
            while (index + 1 + count < FlatRows.Count && FlatRows[index + 1 + count].Level > node.Level)
                count++;
            if (count > RangeSpliceThreshold)
                FlatRows.RemoveRange(index + 1, count);
            else
                for (var i = 0; i < count; i++)
                    FlatRows.RemoveAt(index + 1);
        }
        else
        {
            // Build a collapsed live group's rows the first time it is opened.
            node.EnsureChildren();
            if (node.ChildCount == 0)
                return;
            node.IsExpanded = true;
            var insert = new List<BrowserNode>();
            CollectVisible(node, insert);
            // WI-6: expanding a several-thousand-asset group inserts its rows as one ranged event
            // instead of N per-item CollectionChanged events (which visibly hitched the ListBox).
            if (insert.Count > RangeSpliceThreshold)
                FlatRows.InsertRange(index + 1, insert);
            else
                for (var i = 0; i < insert.Count; i++)
                    FlatRows.Insert(index + 1 + i, insert[i]);
        }
    }

    /// <summary>Above this many spliced rows, use a single ranged FlatRows op; below it, per-item (WI-6).</summary>
    private const int RangeSpliceThreshold = 50;

    /// <summary>Some group in the tree is open (a flat search has none to close).</summary>
    public bool CanCollapseAll => _roots.Any(r => r.IsExpanded);

    /// <summary>
    /// Closes every GDT and type group: the tree goes back to one row per group in a single list replacement, with the
    /// Pinned section above it as it was. Returns the row the keyboard was on's group, for the view to select.
    /// </summary>
    public BrowserNode? CollapseAllGroups(BrowserNode? from)
    {
        var owner = from is null ? null : _roots.FirstOrDefault(r => r == from || (r.ChildCount > 0 && Holds(r, from)));
        if (!CanCollapseAll)
            return owner;
        // Each open group's rows come out in one splice, last group first so the indexes ahead stay good: the list keeps
        // its rows' controls (a new list would rebuild every row in view).
        var end = FlatRows.Count;
        for (var i = FlatRows.Count - 1; i >= 0; i--)
        {
            var row = FlatRows[i];
            if (row.Level != 0)
                continue;
            if (row.IsExpanded && end > i + 1)
                FlatRows.RemoveRange(i + 1, end - i - 1);
            end = i;
        }
        foreach (var root in _roots)
        {
            root.IsExpanded = false;
            if (root.ChildCount > 0)
                foreach (var child in root.Children)
                    if (child.IsGroup)
                        child.IsExpanded = false;
        }
        return owner;

        static bool Holds(BrowserNode group, BrowserNode row)
        {
            if (group.ChildCount == 0)
                return false;
            foreach (var child in group.Children)
                if (child == row || Holds(child, row))
                    return true;
            return false;
        }
    }

    private void CollapseAll() => CollapseAllGroups(null);

    [RelayCommand]
    private void ClearFilter() => FilterText = "";

    // ── Quick filters: discoverable dropdowns/pills that write query tokens ──
    // Observable so live mode can rebuild them once ingestion finishes.
    [ObservableProperty]
    private List<FilterOption> _typeOptions = new();

    [ObservableProperty]
    private List<FilterOption> _gdtOptions = new();

    [ObservableProperty]
    private bool _modifiedFilter;

    [ObservableProperty]
    private bool _problemsFilter;

    [ObservableProperty]
    private bool _changedFilter;

    public bool HasFilter => FilterText.Trim().Length > 0;

    private bool _syncingPills;

    /// <summary>The GDT list shows this many at once; typing in the menu's box finds the rest.</summary>
    private const int GdtOptionLimit = 40;

    /// <summary>Every GDT as a Filter menu row, busiest first; <see cref="GdtOptions"/> is what the box lets through.</summary>
    private List<FilterOption> _allGdtOptions = new();

    /// <summary>Narrows the Filter menu: types by name, GDTs by any part of the path.</summary>
    [ObservableProperty]
    private string _filterMenuText = "";

    partial void OnFilterMenuTextChanged(string value) => FilterMenuOptions();

    /// <summary>
    /// The Filter menu's lists: every type and every GDT, most assets first then by name (the order every type picker
    /// uses), each row checked while its chip is in the box.
    /// </summary>
    private void BuildFilterOptions()
    {
        TypeOptions = _db.Assets.GroupBy(a => a.Type)
            .Select(g => (Type: g.Key, Count: g.Count()))
            .OrderByDescending(t => t.Count).ThenBy(t => t.Type, StringComparer.Ordinal)
            .Select(t => new FilterOption(t.Type, TypeStyles.Glyph(t.Type), TypeStyles.Brush(t.Type), t.Count, $"type:{t.Type}", ToggleFilterOption))
            .ToList();

        // A GDT's chip is its short name unless another GDT shares it, when only the path says which.
        var shortCounts = _db.Gdts.GroupBy(g => ShortGdt(g.Name), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        _allGdtOptions = _db.Gdts
            .OrderByDescending(g => g.Assets.Count).ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var shortName = ShortGdt(g.Name);
                var value = shortCounts[shortName] > 1 ? g.Name : shortName;
                if (value.Contains(' '))
                    value = $"\"{value}\"";
                return new FilterOption(shortName, "▣", TypeStyles.Brush("gdt"), g.Assets.Count, $"gdt:{value}", ToggleFilterOption)
                {
                    Folder = AssetQuery.GdtFolder(g.Name),
                    Tip = g.Name,
                };
            })
            .ToList();
        SyncFilterOptions();
        FilterMenuOptions();
    }

    /// <summary>
    /// The Filter menu's Type and GDT sections as one virtualized list under its box: one scroller for the whole menu,
    /// and only the lines in view are built (an install has ~65 types and ~1,700 GDTs).
    /// </summary>
    public RangeObservableCollection<object> FilterMenuRows { get; } = new();

    /// <summary>
    /// The rows the menu's box lets through: every matching type, and the matching GDTs (checked ones first) up to
    /// <see cref="GdtOptionLimit"/>, with a line saying so when there are more.
    /// </summary>
    private void FilterMenuOptions()
    {
        var typed = FilterMenuText.Trim();
        var types = typed.Length == 0 ? TypeOptions : TypeOptions.Where(o => o.Label.Contains(typed, StringComparison.OrdinalIgnoreCase)).ToList();
        var matching = typed.Length == 0
            ? _allGdtOptions
            : _allGdtOptions.Where(o => o.Tip!.Contains(typed, StringComparison.OrdinalIgnoreCase)).ToList();
        GdtOptions = matching.Where(o => o.IsOn).Concat(matching.Where(o => !o.IsOn)).Take(GdtOptionLimit).ToList();

        var rows = new List<object>(types.Count + GdtOptions.Count + 3);
        if (types.Count > 0)
        {
            rows.Add(new FilterMenuHeading("Type"));
            rows.AddRange(types);
        }
        if (GdtOptions.Count > 0)
        {
            rows.Add(new FilterMenuHeading("GDT"));
            rows.AddRange(GdtOptions);
        }
        if (matching.Count > GdtOptions.Count)
            rows.Add(new FilterMenuNote($"{GdtOptions.Count} of {matching.Count:N0} GDTs. Type more of a name to find the rest."));
        if (rows.Count == 0)
            rows.Add(new FilterMenuNote($"No type or GDT has '{typed}' in its name."));
        FilterMenuRows.ReplaceAll(rows);
    }

    /// <summary>A Filter menu row clicked: its chip goes into the box, or comes out of it.</summary>
    private void ToggleFilterOption(FilterOption option) => ExplorerQuery.Toggle(option.Token);

    /// <summary>The Filter menu's rows show which of them the box filters by.</summary>
    private void SyncFilterOptions()
    {
        HashSet<string> Values(TokenKind kind) =>
            new(_tokens.Where(t => t.Kind == kind).Select(t => t.Value), StringComparer.OrdinalIgnoreCase);
        var types = Values(TokenKind.Type);
        var gdts = Values(TokenKind.Gdt);
        foreach (var o in TypeOptions)
            o.Show(types.Contains(o.Value));
        foreach (var o in _allGdtOptions)
            o.Show(gdts.Contains(o.Value));
    }

    /// <summary>Appends a query token unless it is already present.</summary>
    public void AddFilterToken(string token)
    {
        var parts = AssetQuery.Tokenize(FilterText).ToList();
        // Already there in any spelling (t:fx for type:fx, is:modified for is:off-default): one chip, not two.
        if (AssetQuery.Parse(token) is [{ } added] && AssetQuery.Parse(FilterText).Any(t => t.Kind == added.Kind
                && (t.Kind is TokenKind.Changed or TokenKind.Modified or TokenKind.Problems
                    || (t.Key.Equals(added.Key, StringComparison.OrdinalIgnoreCase) && t.Op == added.Op
                        && t.Value.Equals(added.Value, StringComparison.OrdinalIgnoreCase)))))
            return;
        parts.Add(token);
        FilterText = string.Join(' ', parts);
        ApplyFilterNow();
    }

    /// <summary>Drops every token meaning the same as <paramref name="token"/> (is:modified = is:off-default).</summary>
    private void RemoveFilterToken(string token)
    {
        var kind = AssetQuery.Parse(token)[0].Kind;
        var parts = AssetQuery.Tokenize(FilterText)
            .Where(p => AssetQuery.Parse(p) is not [{ } parsed] || parsed.Kind != kind);
        FilterText = string.Join(' ', parts);
        ApplyFilterNow();
    }

    partial void OnModifiedFilterChanged(bool value)
    {
        if (_syncingPills)
            return;
        // The pill is labelled "Off-default values", so it writes the token with the same name.
        if (value) AddFilterToken("is:off-default");
        else RemoveFilterToken("is:off-default");
    }

    partial void OnChangedFilterChanged(bool value)
    {
        if (_syncingPills)
            return;
        if (value) AddFilterToken("is:changed");
        else RemoveFilterToken("is:changed");
    }

    partial void OnProblemsFilterChanged(bool value)
    {
        if (_syncingPills)
            return;
        if (value) AddFilterToken("is:problems");
        else RemoveFilterToken("is:problems");
    }

    /// <summary>Keeps the pill toggles in step with whatever the user typed by hand.</summary>
    private void SyncFilterPills()
    {
        _syncingPills = true;
        ModifiedFilter = _tokens.Any(t => t.Kind == TokenKind.Modified);
        ProblemsFilter = _tokens.Any(t => t.Kind == TokenKind.Problems);
        ChangedFilter = _tokens.Any(t => t.Kind == TokenKind.Changed);
        _syncingPills = false;
        SyncFilterOptions();
        OnPropertyChanged(nameof(HasFilter));
    }

    /// <summary>Applies the current filter immediately, bypassing the debounce (used by tooling/tests).</summary>
    public void ApplyFilterNow()
    {
        _debounce.Stop();
        ApplyFilter();
    }

    /// <summary>Opens an asset by exact name (tooling, tests, an alert's "Open …" action); false when there is none.</summary>
    public bool OpenByName(string name, bool preview = false)
    {
        if (FindAsset("", name) is not { } asset)
            return false;
        OpenAsset(asset, preview);
        return true;
    }

    // ── Tabs / editor ────────────────────────────────────────────────────────
    public ObservableCollection<AssetEditorViewModel> OpenTabs { get; } = new();

    [ObservableProperty]
    private AssetEditorViewModel? _activeTab;

    /// <summary>
    /// One line of news ("Renamed a → b"). A routine line fades after a few seconds, so a stale one never reads as the
    /// result of whatever the user did next; a line of work in progress (<see cref="ShowProgress"/>: "Saving…") stays
    /// until the work says how it went.
    /// </summary>
    [ObservableProperty]
    private string _status = "";

    private DispatcherTimer? _statusClear;
    private bool _progressPending;
    private bool _statusIsProgress;

    partial void OnStatusChanged(string value)
    {
        _statusClear ??= CreateStatusClearTimer();
        _statusClear.Stop();
        _statusIsProgress = _progressPending;
        _progressPending = false;
        if (value.Length > 0 && !_statusIsProgress)
            _statusClear.Start();
    }

    /// <summary>A line for work still running ("Loading GDTs…"): it stays until the next line replaces it.</summary>
    private void ShowProgress(string text)
    {
        if (Status == text)
        {
            _statusClear?.Stop();
            _statusIsProgress = true;
            return;
        }
        _progressPending = true;
        Status = text;
    }

    /// <summary>The work a progress line was about ended without news of its own (the banner says it): the line goes.</summary>
    private void ClearProgress()
    {
        if (_statusIsProgress)
            Status = "";
    }

    private DispatcherTimer CreateStatusClearTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Status = "";
        };
        return timer;
    }

    // ── Alerts ───────────────────────────────────────────────────────────────
    // Failures used to be written to Status, where they rendered identically to successes in dim
    // 11.5px text at the bottom of a 960px-tall window — frequently metres from the control the
    // user was looking at. Anything the user needs to act on now raises a banner instead, with an
    // optional recovery action attached.

    /// <summary>The banners up now, newest first. One per problem: unrelated news never shares a banner.</summary>
    public ObservableCollection<AlertItem> Alerts { get; } = new();

    /// <summary>More than this many and the oldest notice (else the oldest error) makes room: a stack taller than this is noise.</summary>
    private const int MaxAlerts = 4;

    public bool IsAlertOpen => Alerts.Count > 0;

    // The newest banner, for whatever reads one at a time (the harness, a screen reader's last announcement).
    public string AlertText => Alerts.Count > 0 ? Alerts[0].Text : "";
    public bool AlertIsError => Alerts.Count > 0 && Alerts[0].IsError;
    public string? AlertDetail => Alerts.Count > 0 ? Alerts[0].Detail : null;
    public string AlertActionLabel => Alerts.Count > 0 ? Alerts[0].ActionLabel : "";
    public bool HasAlertAction => Alerts.Count > 0 && Alerts[0].HasAction;

    /// <summary>
    /// Raises a banner. An error stays until dismissed; a notice goes by itself. The same words again bring the existing
    /// banner back to the top rather than stacking a copy. An error also ends any progress line, which would otherwise
    /// go on saying "Saving…" under a banner that says it failed. The status line stays for routine news.
    /// </summary>
    private void Alert(string text, bool isError = true, string? actionLabel = null, Action? action = null, string? detail = null)
    {
        if (Alerts.FirstOrDefault(a => a.Text == text && a.IsError == isError) is { } same)
        {
            same.StopExpiry();
            Alerts.Remove(same);
        }
        var item = new AlertItem(text, isError, actionLabel, action, detail, RemoveAlert);
        Alerts.Insert(0, item);
        while (Alerts.Count > MaxAlerts)
            Alerts.Remove(Alerts.LastOrDefault(a => !a.IsError) ?? Alerts[^1]);
        item.StartExpiry();
        if (isError)
            ClearProgress();
        RaiseAlertChanged();
    }

    private void RemoveAlert(AlertItem item)
    {
        item.StopExpiry();
        if (Alerts.Remove(item))
            RaiseAlertChanged();
    }

    private void RaiseAlertChanged()
    {
        OnPropertyChanged(nameof(IsAlertOpen));
        OnPropertyChanged(nameof(AlertText));
        OnPropertyChanged(nameof(AlertIsError));
        OnPropertyChanged(nameof(AlertDetail));
        OnPropertyChanged(nameof(AlertActionLabel));
        OnPropertyChanged(nameof(HasAlertAction));
    }

    /// <summary>The banner, for what an editor tab has to say (a deffile button's message).</summary>
    public void ShowNotice(string text, bool isError = false, string? detail = null) => Alert(text, isError, detail: detail);

    /// <summary>Dismisses <paramref name="item"/>, or the newest banner (Esc, the harness).</summary>
    [RelayCommand]
    private void DismissAlert(AlertItem? item = null)
    {
        if ((item ?? Alerts.FirstOrDefault()) is { } target)
            RemoveAlert(target);
    }

    /// <summary>The newest banner's action (its button runs its own).</summary>
    [RelayCommand]
    private void RunAlertAction()
    {
        if (Alerts.FirstOrDefault() is { } item)
            item.RunCommand.Execute(null);
    }

    // ── Confirmation dialog ──────────────────────────────────────────────────
    // Only for what undo can't bring back: a table's bulk apply, discarding the session, a deffile's own question.

    [ObservableProperty]
    private bool _isConfirmOpen;

    [ObservableProperty]
    private string _confirmTitle = "";

    [ObservableProperty]
    private string _confirmBody = "";

    [ObservableProperty]
    private string _confirmActionLabel = "";

    [ObservableProperty]
    private bool _confirmIsDestructive = true;

    [ObservableProperty]
    private string _confirmCancelLabel = "Cancel";

    private Action? _confirmAction;

    /// <summary>
    /// Asks before an irreversible action. <paramref name="body"/> must state the blast radius
    /// concretely (how many assets, what happens) — "are you sure?" is not a disclosure.
    /// </summary>
    public void AskConfirm(string title, string body, string actionLabel, Action onConfirm, bool destructive = true, Action<bool>? onCancel = null,
        string cancelLabel = "Cancel")
    {
        // A question this one replaces counts as turned down.
        var replaced = _confirmCancel;
        _confirmCancel = null;
        replaced?.Invoke(true);
        ConfirmTitle = title;
        ConfirmBody = body;
        ConfirmActionLabel = actionLabel;
        ConfirmCancelLabel = cancelLabel;
        ConfirmIsDestructive = destructive;
        _confirmAction = onConfirm;
        _confirmCancel = onCancel;
        IsConfirmOpen = true;
    }

    // Told when the question is turned down (a deffile button's script waits for its answer either way).
    private Action<bool>? _confirmCancel; // true: another question took its place

    [RelayCommand]
    private void CancelConfirm()
    {
        IsConfirmOpen = false;
        _confirmAction = null;
        var cancel = _confirmCancel;
        _confirmCancel = null;
        cancel?.Invoke(false);
    }

    [RelayCommand]
    private void AcceptConfirm()
    {
        var action = _confirmAction;
        IsConfirmOpen = false;
        _confirmAction = null;
        _confirmCancel = null;
        action?.Invoke();
    }

    // ── Session durability ───────────────────────────────────────────────────
    // Changes reach the GDTs only when the user saves (Ctrl+S, MainViewModel.Save.cs); mock mode has no files.
    // Until then the session journal (MainViewModel.Session.cs) keeps every change across restarts outside the
    // install, so closing loses nothing and doesn't ask. The copy says exactly that: kept, not in GDTs yet. When
    // the journal can't be kept (another window holds it, the disk refused a write) the old rule returns: the
    // edits live in memory and closing asks first.

    [ObservableProperty]
    private string _sessionStateText = "No changes";

    /// <summary>The session pill's short form ("3 unsaved"); the full sentence is its name and tooltip.</summary>
    public string SessionPillText => SessionEditCount == 0 ? "No changes" : $"{SessionEditCount:N0} unsaved";

    [ObservableProperty]
    private int _sessionEditCount;

    /// <summary>Set once the user has confirmed discarding the session, so close can proceed.</summary>
    public bool AllowClose { get; private set; }

    // "N changes" is derived, not counted per event: property values that differ from each record's
    // session baseline (so undoing or retyping the old value brings it back down), plus the
    // structural edits (new, duplicate, rename, delete) that have no baseline to return to.
    private readonly Dictionary<AssetRecord, int> _changedKeys = new();
    private int _structuralEdits;

    /// <summary>A new, duplicated, renamed or deleted asset, or a new GDT.</summary>
    private void MarkSessionEdited()
    {
        _structuralEdits++;
        UpdateSessionCount();
    }

    /// <summary>Re-derives the records' share of the session count after their values changed.</summary>
    private void RecountSession(IEnumerable<AssetRecord> records)
    {
        var sidecars = SidecarsByGdt();
        foreach (var record in records)
        {
            var n = record.CountSessionChanges();
            // An asset's extension data is part of the asset: its unsaved values count, mark it and are saved with it.
            if (sidecars?.GetValueOrDefault(record.GdtName) is { } x)
                foreach (var block in x.Blocks)
                    if (block.IsMaterialized && block.Name.Equals(record.Name, StringComparison.OrdinalIgnoreCase))
                        n += block.CountSessionChanges();
            record.HasSessionEdits = n > 0;
            if (n > 0)
                _changedKeys[record] = n;
            else
                _changedKeys.Remove(record);
        }
        JournalDirty(records);
        UpdateSessionCount();
    }

    /// <summary>The loaded GDTs' extension data by GDT name; null when no GDT has any (the common case, one pass).</summary>
    private Dictionary<string, ExtensionSidecar>? SidecarsByGdt()
    {
        Dictionary<string, ExtensionSidecar>? byGdt = null;
        foreach (var gdt in _db.Gdts)
            if (gdt.Extensions is { } x)
                (byGdt ??= new(StringComparer.OrdinalIgnoreCase))[gdt.Name] = x;
        return byGdt;
    }

    private void UpdateSessionCount()
    {
        var wasClean = SessionEditCount == 0;
        var count = _structuralEdits;
        foreach (var n in _changedKeys.Values)
            count += n;
        SessionEditCount = count;
        OnPropertyChanged(nameof(SessionPillText));
        var where = IsSessionKept ? "not in GDTs yet" : "in memory";
        SessionStateText = count switch
        {
            0 => "No changes",
            1 => $"1 change · {where}",
            _ => $"{count:N0} changes · {where}",
        };
        if (wasClean != (count == 0))
            OnPropertyChanged(nameof(HasSessionChanges));
        // The summary counts edited assets across the whole corpus and only the start page shows
        // it; RefreshStartPage raises it when the start page comes back.
        if (ActiveTab is null)
            OnPropertyChanged(nameof(SessionSummaryText));
    }

    /// <summary>
    /// Brings the changed dot and problem mark up to date on these assets' tree rows and on the groups holding them: a
    /// collapsed group shows what's inside it, and stops showing it once its assets are clean again.
    /// </summary>
    private void MarkRecordsChangedInTree(IReadOnlyCollection<AssetRecord> records)
    {
        var gdts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var types = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in records)
        {
            if (_leafCache.TryGetValue(record, out var node))
            {
                node.HasProblem = HasProblems(record);
                node.HasChanges = record.HasSessionEdits;
            }
            gdts.Add(record.GdtName);
            types.Add(record.Type);
        }
        foreach (var root in _roots)
        {
            if (root.Gdt is { } gdt ? !gdts.Contains(gdt.Name) : root.GroupType is not { } t || !types.Contains(t))
                continue;
            root.RefreshMarks(HasProblems);
            foreach (var child in root.Children)
                if (child.GroupType is { } ct && types.Contains(ct))
                    child.RefreshMarks(HasProblems);
        }
        var edited = records as ISet<AssetRecord> ?? new HashSet<AssetRecord>(records);
        foreach (var row in FlatRows)
            if (row.IsPinnedEntry && row.Asset is { } asset && edited.Contains(asset))
            {
                row.HasChanges = asset.HasSessionEdits;
                row.HasProblem = HasProblems(asset);
            }
    }

    /// <summary>
    /// Called from the window's closing handler. Returns true when it is safe to close: always while
    /// the session is kept on disk (the journal gets the last edits now). Otherwise it puts up the
    /// discard confirmation and the caller cancels the close.
    /// </summary>
    public bool RequestClose(Action close)
    {
        FlushJournalNow();
        if (AllowClose || SessionEditCount == 0 || IsSessionKept)
            return true;
        AskConfirm(
            "Close and lose this session's edits?",
            $"Apex couldn't keep this session on disk, so closing loses the {SessionEditCount:N0} unsaved "
            + $"change{(SessionEditCount == 1 ? "" : "s")}. Save them to the GDTs first with {Commands.CommandCatalog.Get(Commands.CommandCatalog.SaveAll).GestureText}.",
            "Close and lose them",
            () => { AllowClose = true; close(); });
        return false;
    }

    /// <summary>
    /// Opens an asset. With <paramref name="preview"/> the tab is a VS Code-style preview:
    /// it reuses the current preview slot; opening the same asset again (or editing it) pins it.
    /// </summary>
    public void OpenAsset(AssetRecord asset, bool preview = false)
    {
        var existing = OpenTabs.FirstOrDefault(t => t.Record == asset);
        if (existing is not null)
        {
            if (!preview)
                existing.IsPreview = false;
            ActiveTab = existing;
            return;
        }

        // Lazy problem counting (§4.3): live mode never validates all 95k assets up front — an asset's
        // problems are computed the first time it is opened (materializing just that one record). WI-9:
        // only once schemas are populated, otherwise validation would run against an empty schema set.
        if (_liveMode && _schemasReady && !_problemsByAsset.ContainsKey(asset))
        {
            UpdateProblemEntry(asset);
            // Its row and the groups above it learn of the problems (collapsed groups roll them up).
            if (HasProblems(asset))
                MarkRecordsChangedInTree(new[] { asset });
        }

        var tab = new AssetEditorViewModel(asset, NavigateToRef, CloseTab, OnEditorEdited, FindAsset, RenameTab, GdtOf)
        {
            Owner = this,
            IsPreview = preview,
            PreviewPane = PreviewPaneViewModel.Create(asset, _env, FindAsset, NavigateToRef),
        };
        // Opening the editor materializes the record's properties — register it so reference scans
        // (WI-4) can find any property-value references it holds without walking the whole corpus.
        MarkMaterialized(asset);

        if (preview && OpenTabs.FirstOrDefault(t => t.IsPreview) is { } slot)
        {
            // In beside the old preview, made active, and only then the old one out: replacing it in place (or removing
            // it first) has the tab strip write a null selection into ActiveTab, and the editor view is torn down and
            // rebuilt instead of rebinding from one asset to the next.
            OpenTabs.Insert(OpenTabs.IndexOf(slot), tab);
            ActiveTab = tab;
            OpenTabs.Remove(slot);
            ForgetTab(slot);
            slot.PreviewPane?.Dispose();
            return;
        }

        OpenTabs.Add(tab);
        ActiveTab = tab;
    }

    private void CloseTab(AssetEditorViewModel tab)
    {
        var index = OpenTabs.IndexOf(tab);
        _tabCycle = null;
        ForgetTab(tab);
        if (tab.FocusedProperty is not null)
            tab.FocusedProperty = null;
        // The neighbour becomes active before the tab leaves the strip: removing the selected tab would have the strip
        // write a null selection into ActiveTab, and the editor view would be torn down and rebuilt for the neighbour.
        // Closing isn't a visit, so the closed asset doesn't go on the Back list.
        if (ActiveTab == tab)
        {
            _closingTab = true;
            try { ActiveTab = index + 1 < OpenTabs.Count ? OpenTabs[index + 1] : index > 0 ? OpenTabs[index - 1] : null; }
            finally { _closingTab = false; }
        }
        OpenTabs.Remove(tab);
        tab.PreviewPane?.Dispose();
    }

    private bool _closingTab;

    /// <summary>A tab leaving the strip (closed, or a preview replaced): nothing of the workspace may keep it.</summary>
    private void ForgetTab(AssetEditorViewModel tab)
    {
        _mru.Remove(tab);
        _chosenSubject.Remove(tab);
    }

    private void OnEditorEdited(AssetEditorViewModel tab)
    {
        RefreshInheritors(new[] { tab.Record }, tab.LastEditedKeys);
        UpdateProblemEntry(tab.Record, tab);
        RecountSession(new[] { tab.Record });
        MarkRecordsChangedInTree(new[] { tab.Record });
        // No status line: the edit shows in the field itself (and its bar), so echoing it at the
        // bottom of the window would say it twice.
    }

    /// <summary>
    /// Follows a reference. Every way in (a row's →, F12, the parent link, a material's texture slot, an Inspector
    /// link) is only offered when the target resolves; a missing one is said where the reference is (the row's ⚠,
    /// the header, the slot), so there is nothing to announce here.
    /// </summary>
    private void NavigateToRef(string refType, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || FindAsset(refType, name) is not { } target)
            return;
        var from = ActiveTab?.Name;
        OpenAsset(target, preview: true);
        if (ActiveTab is { } opened && from is not null && opened.Name != from)
            opened.OpenedFrom = from;
    }

    // ── New asset / new GDT (the dialog lives in MainViewModel.Shell.cs) ──
    private void CreateAsset(string name, string type, GdtFile gdt)
    {
        var rec = new AssetRecord { Name = name, Type = type, GdtName = gdt.Name, Parent = null };
        if (SchemaRegistry.Get(type) is { } schema)
            foreach (var def in schema.Properties)
                rec.Properties[def.Key] = def.Default;
        Services.Gdf.Techsetdefs.SeedNewAsset(rec);

        InsertByName(gdt.Assets, rec);
        _db.Assets.Add(rec);
        IndexAdd(rec);
        MarkMaterialized(rec);
        InvalidateSnapshot(gdt);
        _leafCache[rec] = BrowserNode.ForAsset(rec, ToggleNode);
        UpdateProblemEntry(rec);
        RefreshAfterAssetChange(new[] { gdt }, name);
        OpenAsset(rec);
        MarkSessionEdited();
        JournalOp(new SessionOp { Entry = AddEntry(rec, from: null), Record = rec, Gdt = gdt });
        Status = $"Created {name} in {ShortGdt(rec.GdtName)}";
    }

    private GdtFile CreateGdt(string name)
    {
        // Live, a new GDT goes in source_data (where its file is written on save), named as the loader names it.
        var gdt = new GdtFile { Name = _liveMode ? "source_data/" + name : name };
        _db.Gdts.Add(gdt);
        _db.Gdts.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        ApplyFilterNow();
        MarkSessionEdited();
        JournalOp(new SessionOp { Entry = new() { Op = "gdt", Gdt = gdt.Name }, Gdt = gdt });
        Status = $"Created {name}. {Commands.CommandCatalog.Get(Commands.CommandCatalog.NewAsset).TipFor("New asset")} adds assets to it.";
        return gdt;
    }

    // ── Undo / redo routing (keyboard + top bar) ─────────────────────────────
    // An open table undoes its own edits; compare undoes its base asset (the active tab's record);
    // otherwise the active tab. All of them are the same per-record history underneath.
    [RelayCommand]
    private void UndoActive() => StepHistory(undo: true);

    [RelayCommand]
    private void RedoActive() => StepHistory(undo: false);

    /// <summary>
    /// Set by the property editors: throws away typing not yet committed in the focused field and
    /// returns true if there was any. The window's Ctrl+Z binding sees the key before the field
    /// does, so this is how undo-while-typing takes back the typing first, like any text box.
    /// </summary>
    public Func<bool>? DiscardPendingInput { get; set; }

    private void StepHistory(bool undo)
    {
        if (undo && DiscardPendingInput?.Invoke() == true)
            return;
        if (Table is { } table)
        {
            var text = undo ? table.Undo() : table.Redo();
            if (text.Length > 0)
                Status = text;
            return;
        }
        if (Compare is { } compare)
        {
            // The editor sits behind the overlay: undo the record directly (the tab re-syncs) and
            // leave focus where it is.
            if ((undo ? compare.Base.History.Undo() : compare.Base.History.Redo()) is { } step)
            {
                OnRecordEditedExternally(compare.Base);
                compare.Refresh();
                Status = $"{(undo ? "Undid" : "Redid")} {step.Changes.Count} value{(step.Changes.Count == 1 ? "" : "s")} on {compare.BaseName}";
            }
            return;
        }
        // A paste, move, derive or underive newer than the tab's last edit goes first.
        if (TryStepPlacement(undo))
            return;
        if (ActiveTab is { } tab && (undo ? tab.CanUndo : tab.CanRedo))
        {
            (undo ? tab.UndoCommand : tab.RedoCommand).Execute(null);
            Status = tab.LastHistoryText;
        }
    }

    // The title bar's ↶ ↷ grey out when there is nothing to undo. The commands themselves always run: Ctrl+Z also
    // takes back uncommitted typing, and an open table or compare keeps its own history, so gating the command
    // would swallow those. While an overlay is open the buttons stay live and the command decides.
    public bool CanUndoActive => IsTableOpen || IsCompareOpen || ActiveTab?.CanUndo == true || HasPlacementUndo;
    public bool CanRedoActive => IsTableOpen || IsCompareOpen || ActiveTab?.CanRedo == true || HasPlacementRedo;

    private void ActiveTab_HistoryChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AssetEditorViewModel.CanUndo) or nameof(AssetEditorViewModel.CanRedo))
            NotifyUndoRedo();
    }

    private void NotifyUndoRedo()
    {
        OnPropertyChanged(nameof(CanUndoActive));
        OnPropertyChanged(nameof(CanRedoActive));
    }

    // ── Tab keyboard commands ────────────────────────────────────────────────

    [RelayCommand]
    private void CloseActiveTab()
    {
        if (ActiveTab is { } tab)
            CloseTab(tab);
    }

    // Ctrl+Tab walks tabs by recency, as in Visual Studio: one press flips to the previous tab,
    // further presses with Ctrl held go further back, and letting go of Ctrl makes the landing tab
    // the most recent. The order is frozen for the whole walk so each press moves one step.
    private List<AssetEditorViewModel>? _tabCycle;
    private int _tabCycleIndex;
    private AssetEditorViewModel? _tabCycleOrigin;
    private bool _cyclingTabs;

    /// <summary>Ctrl+Tab / Ctrl+Shift+Tab — steps through tabs, most recently used first, wrapping.</summary>
    public void CycleTab(int delta)
    {
        if (OpenTabs.Count < 2 || ActiveTab is not { } tab)
            return;
        if (_tabCycle is null)
        {
            _tabCycle = TabsByRecency.ToList();
            _tabCycleIndex = Math.Max(0, _tabCycle.IndexOf(tab));
            _tabCycleOrigin = tab;
        }
        _tabCycleIndex = (_tabCycleIndex + delta + _tabCycle.Count) % _tabCycle.Count;
        _cyclingTabs = true;
        try { ActiveTab = _tabCycle[_tabCycleIndex]; }
        finally { _cyclingTabs = false; }
    }

    /// <summary>Ctrl released: the tab the walk landed on becomes the most recent one.</summary>
    public void EndTabCycle()
    {
        if (_tabCycle is null)
            return;
        var origin = _tabCycleOrigin;
        _tabCycle = null;
        _tabCycleOrigin = null;
        if (ActiveTab is not { } landed)
            return;
        // Cycling through a tab is a deliberate visit, so it stops being a throwaway preview.
        landed.IsPreview = false;
        RecordVisit(origin != landed ? origin : null, landed);
    }

    /// <summary>Escape unwinds one layer of UI at a time: dialog → palette → overlay mode.</summary>
    public bool IsAnyLayerOpen =>
        IsConfirmOpen || IsConflictOpen || IsRenameOpen || IsNewOpen || IsPaletteOpen || IsCompareOpen || IsTableOpen || IsAlertOpen;

    public bool DismissTopLayer()
    {
        if (IsConfirmOpen) { CancelConfirm(); return true; }
        if (IsConflictOpen) { CancelConflicts(); return true; }
        if (IsRenameOpen) { CancelRename(); return true; }
        if (IsNewOpen) { CancelNew(); return true; }
        if (IsPaletteOpen) { IsPaletteOpen = false; return true; }
        if (IsCompareOpen) { Compare = null; return true; }
        if (IsTableOpen) { Table = null; return true; }
        if (IsAlertOpen) { DismissAlert(null); return true; }
        return false;
    }

    // ── Refactoring ops: rename with reference update · duplicate · safe delete ──
    private void RenameTab(AssetEditorViewModel tab) => RenameTab(tab, null);

    /// <param name="scanned">Referrers found by the dialog's corpus scan, including ones in GDTs nobody opened.</param>
    private void RenameTab(AssetEditorViewModel tab, IReadOnlyList<AssetRecord>? scanned)
    {
        var newName = tab.RenameText.Trim();
        var oldName = tab.Record.Name;
        if (newName.Length == 0 || newName.Equals(oldName, StringComparison.OrdinalIgnoreCase))
            return;
        if (FindAsset("", newName) is not null)
        {
            Alert($"Can't rename to “{newName}” — an asset with that name already exists.",
                isError: true,
                actionLabel: $"Open {newName}",
                action: () => OpenByName(newName, preview: true));
            return;
        }

        // Values still waiting for the journal were changed under the old name: write them under it.
        FlushJournalNow();
        LoadScannedReferrers(scanned);
        tab.Record.Name = newName;
        IndexRename(tab.Record, oldName);
        // Its extension blocks are known by its name: they follow it, and the save renames them in the .gdtx. Blocks
        // under the new name that belonged to no asset are replaced (the dialog asked).
        var replaced = GdtOf(tab.Record)?.Extensions?.Rename(oldName, newName);
        var updated = 0;
        // GDTs whose tree rows can change: the renamed asset's, and any holding a deriver (it nests by parent name).
        var touchedGdts = new HashSet<string>(StringComparer.Ordinal) { tab.Record.GdtName };

        // WI-4: rewrite derivation-parent references via the reverse index (O(derivers)), not a 121k walk.
        var reparented = new List<AssetRecord>();
        if (_byParent.TryGetValue(oldName, out var derivers))
        {
            foreach (var asset in derivers.ToArray())
            {
                if (ReferenceEquals(asset, tab.Record))
                    continue;
                var op = asset.Parent;
                asset.Parent = newName;
                IndexReparent(asset, op);
                reparented.Add(asset);
                touchedGdts.Add(asset.GdtName);
                updated++;
            }
        }
        // Journaled before the value rewrites below, which reach the journal as the referrers' own changes.
        JournalOp(new SessionOp
        {
            Entry = new() { Op = "ren", Gdt = tab.Record.GdtName, Name = oldName, To = newName },
            Record = tab.Record, OldName = oldName, Reparented = reparented, Blocks = replaced,
        });

        // §4.3 / WI-4: rewrite property-value references only within records already materialized
        // (open/edited/mock) — tracked by the registry, so no forced materialization of the corpus.
        List<AssetRecord>? rewritten = null;
        foreach (var asset in _materialized)
        {
            if (ReferenceEquals(asset, tab.Record) || !asset.IsMaterialized)
                continue;
            List<string>? keys = null;
            foreach (var kv in asset.Properties)
                if (kv.Value.Equals(oldName, StringComparison.OrdinalIgnoreCase))
                    (keys ??= new List<string>()).Add(kv.Key);
            if (keys is not null)
            {
                asset.CaptureBaseline();
                foreach (var key in keys)
                {
                    asset.Properties[key] = newName;
                    asset.ValuesTouched = true;
                    updated++;
                }
                (rewritten ??= new List<AssetRecord>()).Add(asset);
            }
        }
        if (rewritten is not null)
            RecountSession(rewritten);

        _leafCache[tab.Record] = BrowserNode.ForAsset(tab.Record, ToggleNode);
        var gdts = _db.Gdts.Where(g => touchedGdts.Contains(g.Name)).ToList();
        if (gdts.Count == touchedGdts.Count)
            RefreshAfterAssetChange(gdts, oldName, newName);
        else
            ApplyFilterNow();
        tab.NotifyRenamed();
        RefreshReferences();
        foreach (var other in OpenTabs.Where(t => t != tab))
            other.SyncFromRecord();
        if (scanned is { Count: > 0 })
            MarkRecordsChangedInTree(scanned);
        MarkSessionEdited();
        Status = $"Renamed {oldName} → {newName} · updated {updated} reference{(updated == 1 ? "" : "s")}";
    }

    [RelayCommand]
    private void DuplicateActive()
    {
        if (ActiveTab is { } tab)
            DuplicateAsset(tab.Record);
    }

    public void DuplicateAsset(AssetRecord source)
    {
        var name = FreeName(source.Name + "_copy");

        var clone = new AssetRecord { Name = name, Type = source.Type, GdtName = source.GdtName, Parent = source.Parent };
        foreach (var (key, value) in source.Properties)
            clone.Properties[key] = value;

        // The backing GDT may have been removed by the read-only watcher while this tab stayed open.
        var gdt = _db.Gdts.FirstOrDefault(g => g.Name == clone.GdtName);
        if (gdt is null)
        {
            Alert($"Can't duplicate {source.Name}: its GDT “{clone.GdtName}” is no longer loaded.");
            return;
        }
        InsertByName(gdt.Assets, clone);
        _db.Assets.Add(clone);
        IndexAdd(clone);
        MarkMaterialized(clone);
        InvalidateSnapshot(gdt);
        _leafCache[clone] = BrowserNode.ForAsset(clone, ToggleNode);
        // The copy starts with the source's extension data too.
        var extension = ExtensionSidecar.ValuesOf(GdtOf(source), source.Name);
        if (extension is not null)
            ExtensionSidecar.Of(gdt).Add(clone.Name, extension);
        RefreshAfterAssetChange(new[] { gdt }, name);
        OpenAsset(clone);
        MarkSessionEdited();
        var entry = AddEntry(clone, from: source.Name);
        entry.ExtensionProps = extension;
        JournalOp(new SessionOp { Entry = entry, Record = clone, Gdt = gdt });
        Status = $"Duplicated {source.Name} → {name}";
    }

    /// <summary>
    /// Takes <paramref name="rec"/> out of the session (the save deletes it from its GDT, and its extension data from the
    /// .gdtx) as one undo step: Ctrl+Z puts it back with its values, its extension data and its tab, until a save.
    /// </summary>
    private void DeleteNow(AssetRecord rec)
    {
        var name = rec.Name;
        // The backing GDT may have been removed by the read-only watcher (ApplyDiskDeletion) while the asset stayed
        // open: there is nothing in a file to delete, and nothing to put back.
        if (GdtOf(rec) is not { } gdt)
        {
            JournalOp(new SessionOp { Entry = new() { Op = "del", Gdt = rec.GdtName, Name = name }, Record = rec });
            DetachRecord(rec);
            SumProblems();
            ApplyFilterNow();
            MarkSessionEdited();
            Alert($"Closed {name}: its GDT was removed on disk, so there was nothing left to delete.", isError: false);
            return;
        }

        // What Ctrl+Z restores besides the asset: its tab, and whether it was the one in front.
        var hadTab = false;
        var wasActive = false;
        var wasPreview = false;
        SessionOp? op = null;
        void Delete()
        {
            var tab = OpenTabs.FirstOrDefault(t => t.Record == rec);
            hadTab = tab is not null;
            wasActive = tab is not null && tab == ActiveTab;
            wasPreview = tab?.IsPreview == true;
            op = new SessionOp
            {
                Entry = new() { Op = "del", Gdt = rec.GdtName, Name = name }, Record = rec, Gdt = gdt,
                // Its extension blocks go with it: the save deletes them from the .gdtx (and undo or Discard all changes
                // puts them back).
                Blocks = gdt.Extensions?.Detach(name),
            };
            JournalOp(op);
            DetachRecord(rec);
            MarkSessionEdited();
            RefreshPlacement(new[] { gdt }, new[] { name });
        }
        Delete();
        var done = $"Deleted {name} · {Commands.CommandCatalog.Get(Commands.CommandCatalog.Undo).GestureText} restores it";
        Status = done;
        PushPlacement(new PlacementStep
        {
            DoneText = done,
            UndoneText = $"Restored {name}",
            Gone = () => !_db.Gdts.Contains(gdt) ? $"Nothing to restore: {ShortGdt(gdt.Name)} is no longer loaded."
                : FindAsset("", name) is not null ? $"Can't restore {name}: another asset has that name now."
                : op is null || !_sessionOps.Contains(op) ? $"Nothing to restore: {name} isn't deleted." : null,
            Undo = () =>
            {
                if (op is null || !_sessionOps.Contains(op) || FindAsset("", name) is not null)
                    return;
                var active = ActiveTab;
                ReverseOp(op);
                ForgetOps(new[] { op });
                op = null;
                RecountSession(new[] { rec });
                UpdateProblemEntry(rec);
                MarkRecordsChangedInTree(new[] { rec });
                RefreshPlacement(new[] { gdt }, new[] { name });
                if (!hadTab)
                    return;
                OpenAsset(rec, wasPreview);
                if (!wasActive && active is not null && OpenTabs.Contains(active))
                    ActiveTab = active;
            },
            Redo = () =>
            {
                if (IsLive(rec) && _db.Gdts.Contains(gdt))
                    Delete();
            },
        });
    }

    /// <summary>
    /// Records referencing <paramref name="name"/> — as a derivation parent (via the reverse-parent
    /// index) or by a property value (only among materialized records, the existing rule). Deduped.
    /// Never walks the whole corpus, so tab-switch/rename/delete reference work stays O(references).
    /// </summary>
    private List<AssetRecord> ReferencesTo(string name, AssetRecord? exclude, int cap = int.MaxValue)
    {
        var result = new List<AssetRecord>();
        var seen = new HashSet<AssetRecord>();
        if (_byParent.TryGetValue(name, out var kids))
        {
            foreach (var a in kids)
            {
                if (ReferenceEquals(a, exclude) || !seen.Add(a))
                    continue;
                result.Add(a);
                if (result.Count >= cap)
                    return result;
            }
        }
        foreach (var a in _materialized)
        {
            if (ReferenceEquals(a, exclude) || seen.Contains(a) || !a.IsMaterialized)
                continue;
            foreach (var kv in a.Properties)
            {
                if (kv.Value.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    seen.Add(a);
                    result.Add(a);
                    if (result.Count >= cap)
                        return result;
                    break;
                }
            }
        }
        return result;
    }

    // ── Table (spreadsheet) mode ─────────────────────────────────────────────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTableOpen), nameof(CanUndoActive), nameof(CanRedoActive))]
    private TableViewModel? _table;

    public bool IsTableOpen => Table is not null;

    /// <summary>The query a table request is waiting on (its off-thread scan hadn't landed yet).</summary>
    private List<QueryToken>? _tableAwaitsHits;

    /// <summary>Called once browser hits land: opens a table that was asked for while they were being scanned.</summary>
    private void OpenTableIfAwaited()
    {
        if (_tableAwaitsHits is not { } awaited)
            return;
        _tableAwaitsHits = null;
        // A different query landing means the filter changed meanwhile; that table request is stale.
        if (ReferenceEquals(awaited, _tokens))
            OpenTable();
    }

    [RelayCommand]
    private void OpenTable()
    {
        // Same batched per-GDT scan the browser filter uses (one file read per GDT for prop:/
        // is:modified queries) — the per-asset AssetQuery.Matches path re-opens the backing .gdt
        // for every one of the 121k records.
        List<AssetRecord> matches;
        if (_tokens.Count == 0)
            matches = _db.Assets;
        else if (_hitCache is { } cache && ReferenceEquals(_hitCacheTokens, _tokens))
            matches = cache.SelectMany(h => h.Hits).ToList();
        else if (_tableAwaitsHits is null && _liveMode && _db.Assets.Count > 5000
            && _tokens.Any(t => t.Kind is TokenKind.Prop or TokenKind.Modified))
        {
            // The browser's off-thread scan for this query is still reading GDTs; re-running it
            // here would block the UI for as long. Open the table when its hits land (asking again
            // while waiting scans synchronously, as before).
            _tableAwaitsHits = _tokens;
            Status = "The table opens when the current filter finishes scanning";
            return;
        }
        else
        {
            Func<AssetRecord, bool>? problemGate = _tokens.Any(t => t.Kind == TokenKind.Problems)
                ? a => _problemsByAsset.GetValueOrDefault(a) > 0
                : null;
            var live = _db.Gdts
                .Select(g => (Gdt: g, Assets: (IReadOnlyList<AssetRecord>)g.Assets))
                .ToList();
            matches = ComputeHits(live, _tokens, problemGate, extensions: ExtensionSearch(_tokens)).SelectMany(h => h.Hits).ToList();
        }
        _tableAwaitsHits = null;
        if (matches.Count == 0)
        {
            Alert("No assets match the current query, so there's nothing to open as a table.", isError: false);
            return;
        }
        // The most common type (ties: the one met first, as GroupBy ordered them), without grouping every match.
        var counts = new Dictionary<string, int>();
        var firstSeen = new List<string>();
        foreach (var a in matches)
        {
            if (counts.TryGetValue(a.Type, out var n))
                counts[a.Type] = n + 1;
            else
            {
                counts[a.Type] = 1;
                firstSeen.Add(a.Type);
            }
        }
        string? dominantType = null;
        var dominantCount = 0;
        foreach (var type in firstSeen)
            if (counts[type] > dominantCount)
            {
                dominantCount = counts[type];
                dominantType = type;
            }
        var rows = new List<AssetRecord>(Math.Min(dominantCount, TableViewModel.RowCap));
        foreach (var a in matches)
        {
            if (a.Type != dominantType)
                continue;
            rows.Add(a);
            if (rows.Count == TableViewModel.RowCap)
                break;
        }
        ShowTable(dominantType!, rows, dominantCount, otherTypesLeftOut: counts.Count > 1);
    }

    private void ShowTable(string type, List<AssetRecord> rows, int total, bool otherTypesLeftOut)
    {
        // Constructing the table reads each row's Properties, materializing them — inside a scan scope
        // so rows sharing a .gdt reuse one whole-file read instead of opening a stream per record.
        // Register AFTER that so MarkMaterialized (which is a no-op while IsMaterialized is false)
        // actually records each row — value-reference scans (WI-4) must find references these
        // now-resident rows hold, exactly as the old IsMaterialized walk did.
        TableViewModel table;
        using (GdtFileScan.Begin())
            table = new TableViewModel(type, rows, total, OnRecordsEditedExternally, () => Table = null, FindAsset, GdtOf)
            {
                OtherTypesLeftOut = otherTypesLeftOut,
            };
        foreach (var r in rows)
            MarkMaterialized(r);
        // The status line follows the table's last action; "Table opened" must not outlive an edit.
        table.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TableViewModel.LastActionText) && table.LastActionText.Length > 0)
                Status = table.LastActionText;
        };
        Table = table;
    }

    // ── Compare mode ─────────────────────────────────────────────────────────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCompareOpen), nameof(CanUndoActive), nameof(CanRedoActive))]
    private CompareViewModel? _compare;

    public bool IsCompareOpen => Compare is not null;

    [RelayCommand]
    private void OpenCompare()
    {
        if (ActiveTab is not { } tab)
            return;
        var candidates = _db.Assets
            .Where(a => a != tab.Record && a.Type.Equals(tab.Record.Type, StringComparison.OrdinalIgnoreCase))
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (candidates.Count == 0)
        {
            Alert($"There are no other {tab.Record.Type} assets loaded to compare against.", isError: false);
            return;
        }
        var initial = tab.Record.Parent is { } p && FindAsset(tab.Record.Type, p) is { } parent
            ? parent
            : candidates[0];
        Compare = new CompareViewModel(tab.Record, candidates, initial, OnRecordEditedExternally, () => Compare = null, FindAsset, GdtOf);
    }

    /// <summary>Table/compare write straight to records; keep tabs, problem badges and the inspector in sync.</summary>
    private void OnRecordEditedExternally(AssetRecord record) => OnRecordsEditedExternally(new[] { record });

    /// <summary>A bulk apply: every record at once, with one status, inspector and tree pass for the lot.</summary>
    private void OnRecordsEditedExternally(IReadOnlyList<AssetRecord> records)
    {
        if (records.Count == 0)
            return;
        // Same contract as the property editor: RecountSession sets HasSessionEdits, so session
        // edits survive a disk re-save of the backing GDT (ApplyDiskChange only reloads records
        // without them).
        RecountSession(records);
        foreach (var record in records)
        {
            MarkMaterialized(record);
            OpenTabs.FirstOrDefault(t => t.Record == record)?.SyncFromRecord();
            UpdateProblemEntry(record);
        }
        var edited = new HashSet<AssetRecord>(records);
        RefreshInheritors(edited);
        NotifySubjectPreviewsChanged(edited.Contains);
        MarkRecordsChangedInTree(records);
    }

    // ── Quick Open (⌘P): search-with-the-same-grammar, Enter opens a preview tab ──
    [ObservableProperty]
    private bool _browserIsEmpty;

    [ObservableProperty]
    private bool _isPaletteOpen;

    [ObservableProperty]
    private string _paletteText = "";

    private readonly List<PaletteItemViewModel> _paletteResults = new();

    /// <summary>What the palette lists, best first.</summary>
    public IReadOnlyList<PaletteItemViewModel> PaletteResults => _paletteResults;

    /// <summary>The list's lines, one per result (see <see cref="PaletteRow"/>).</summary>
    public ObservableCollection<PaletteRow> PaletteRows { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PaletteSelection))]
    private int _paletteSelectedIndex = -1;

    public PaletteItemViewModel? PaletteSelection
    {
        get => PaletteSelectedIndex >= 0 && PaletteSelectedIndex < _paletteResults.Count ? _paletteResults[PaletteSelectedIndex] : null;
        set => PaletteSelectedIndex = value is null ? -1 : _paletteResults.IndexOf(value);
    }

    /// <summary>Ctrl+E: with nothing typed the palette lists recently opened assets instead of open tabs.</summary>
    private bool _paletteRecent;

    [RelayCommand]
    private void OpenPalette()
    {
        _gdtPick = null;
        _paletteRecent = false;
        _paletteKeepsTab = false;
        PaletteText = "";
        RefreshPaletteResults();
        IsPaletteOpen = true;
    }

    [RelayCommand]
    private void ClosePalette() => IsPaletteOpen = false;

    [RelayCommand]
    private void ConfirmPalette()
    {
        if (PaletteSelection is not { } pick)
            return;
        IsPaletteOpen = false;
        if (pick.Run is { } run)
            run();
        else if (pick.Record is { } record)
        {
            OpenAsset(record, preview: !_paletteKeepsTab);
            RequestEditorFocus();
        }
    }

    [RelayCommand]
    private void PaletteNext() => MovePaletteSelection(1);

    [RelayCommand]
    private void PalettePrev() => MovePaletteSelection(-1);

    private void MovePaletteSelection(int delta)
    {
        if (_paletteResults.Count > 0)
            PaletteSelectedIndex = Math.Clamp(PaletteSelectedIndex + delta, 0, _paletteResults.Count - 1);
    }

    partial void OnPaletteTextChanged(string value)
    {
        // Any keystroke supersedes an in-flight off-thread scan (generation guard, WI-3).
        _paletteGen++;
        _paletteDebounce.Stop();
        if (TryFillModalPalette(value))
            return;

        var tokens = AssetQuery.Parse(value);
        // Disk-touching tokens (prop:/is:modified) over the live corpus must never run on the UI thread:
        // debounce (150 ms) then scan off-thread. Name/type/gdt queries are cheap in-memory — run them
        // immediately so results stay synchronous exactly as before (no behaviour change for name queries).
        var disk = _liveMode && _db.Assets.Count > 5000
            && tokens.Any(t => t.Kind is TokenKind.Prop or TokenKind.Modified);
        if (disk)
            _paletteDebounce.Start();
        else
            RefreshPaletteResults(tokens);
    }

    private void RefreshPaletteResults(List<QueryToken>? tokens = null)
    {
        if (tokens is null && TryFillModalPalette(PaletteText))
            return;
        tokens ??= AssetQuery.Parse(PaletteText);
        var wantProblems = tokens.Any(t => t.Kind == TokenKind.Problems);
        FillPalette(CollectPaletteSync(tokens, wantProblems));
    }

    private const int PaletteLimit = 12;

    /// <summary>
    /// Synchronous, in-memory palette collection (name/type/gdt). Nothing typed: open tabs, most
    /// recent first (Ctrl+E: recently opened assets). Otherwise every match is ranked like the
    /// Explorer's search — exact, starts with, shorter, contains — and the best 12 kept.
    /// </summary>
    private List<AssetRecord> CollectPaletteSync(List<QueryToken> tokens, bool wantProblems)
    {
        if (tokens.Count == 0)
        {
            IEnumerable<AssetRecord> seed = _paletteRecent
                ? _settings.Recent.Select(r => FindAsset("", r.Name)).OfType<AssetRecord>().Where(a => a != ActiveTab?.Record)
                : TabsByRecency.Select(t => t.Record);
            return seed.Distinct().Take(PaletteLimit).ToList();
        }

        // prop:/is:modified here only runs over the small early-load/mock set; the scan scope still
        // turns it into one read per .gdt (records arrive grouped by file) instead of one per asset.
        var matches = AssetQuery.Compile(tokens, ExtensionSearch(tokens));
        var top = new TopAssets(PaletteQuery(tokens), PaletteLimit);
        using var scan = AssetQuery.TouchesProperties(tokens) ? GdtFileScan.Begin() : null;
        var (source, kept) = PaletteNarrowing(tokens);
        foreach (var a in source)
        {
            if (!matches(a))
                continue;
            kept?.Add(a);
            if (wantProblems && _problemsByAsset.GetValueOrDefault(a) == 0)
                continue;
            top.Offer(a);
        }
        return top.Result();
    }

    // Typing a name one letter at a time only ever narrows it: every asset matching "wpn_ar" also matched "wpn_a".
    // So the next keystroke searches the last one's matches, not all 122k assets.
    private string? _narrowText;
    private List<AssetRecord>? _narrowMatches;
    private int _narrowAssetCount;

    /// <summary>What to scan for this palette query, and the list to collect its matches into for the next keystroke.</summary>
    private (IReadOnlyList<AssetRecord> Source, List<AssetRecord>? Kept) PaletteNarrowing(List<QueryToken> tokens)
    {
        var text = PaletteText;
        // Name words only: "is:chang" → "is:changed" turns a name into a filter, which does not narrow.
        var narrowable = !text.Contains('"') && tokens.All(t => t.Kind == TokenKind.Name);
        IReadOnlyList<AssetRecord> source = _db.Assets;
        if (narrowable && _narrowMatches is { } previous && _narrowText is { } prev
            && _narrowAssetCount == _db.Assets.Count && text.StartsWith(prev, StringComparison.OrdinalIgnoreCase))
            source = previous;
        _narrowText = narrowable ? text : null;
        _narrowAssetCount = _db.Assets.Count;
        _narrowMatches = narrowable ? new List<AssetRecord>() : null;
        return (source, _narrowMatches);
    }

    /// <summary>The free text ranked against; filters alone (type:, gdt:) rank shorter names first.</summary>
    private static string PaletteQuery(List<QueryToken> tokens) =>
        tokens.FirstOrDefault(t => t.Kind == TokenKind.Name)?.Value ?? "";

    /// <summary>Debounced off-thread palette scan for disk-touching queries (WI-3). Reads each GDT once
    /// (via <see cref="AssetQuery.MatchAll"/>) over cached snapshots, ranks every hit like the sync
    /// path, marshals back under a generation guard so a newer keystroke's results always win.</summary>
    private void RunPaletteScan()
    {
        var tokens = AssetQuery.Parse(PaletteText);
        var wantProblems = tokens.Any(t => t.Kind == TokenKind.Problems);
        var gen = _paletteGen;
        var snapshot = _db.Gdts.Select(GetSnapshot).ToArray();
        var problemSet = wantProblems
            ? new HashSet<AssetRecord>(_problemsByAsset.Where(kv => kv.Value > 0).Select(kv => kv.Key))
            : null;
        var extensions = ExtensionSearch(tokens);

        Task.Run(() =>
        {
            List<AssetRecord> results;
            try
            {
                // GDTs are scanned in chunks, each chunk fanned out across cores, and every hit is
                // ranked (a best match can sit in the last GDT). A newer keystroke stops the scan
                // between chunks.
                const int chunkSize = 64;
                var top = new TopAssets(PaletteQuery(tokens), PaletteLimit);
                for (var start = 0; start < snapshot.Length; start += chunkSize)
                {
                    if (gen != Volatile.Read(ref _paletteGen))
                        return;
                    var first = start;
                    var chunk = new List<AssetRecord>[Math.Min(chunkSize, snapshot.Length - first)];
                    Parallel.For(0, chunk.Length, i => chunk[i] =
                        AssetQuery.MatchAll(snapshot[first + i], tokens, problemSet is null ? null : problemSet.Contains, extensions));
                    foreach (var hits in chunk)
                        foreach (var h in hits)
                            top.Offer(h);
                }
                results = top.Result();
            }
            catch
            {
                return; // a fresh keystroke recovers; never leave a scan fault unobserved
            }
            Dispatcher.UIThread.Post(() =>
            {
                if (gen != _paletteGen)
                    return; // superseded by a newer keystroke
                FillPalette(results);
            });
        });
    }

    private void FillPalette(List<AssetRecord> results)
    {
        ShowPaletteRows(results.Select(a => new PaletteItemViewModel(a)).ToList());
    }

    /// <summary>Click-to-open from the palette list.</summary>
    public void ConfirmPaletteItem(PaletteItemViewModel item)
    {
        PaletteSelection = item;
        ConfirmPalette();
    }

    // ── Global problem count (status bar + tree badges) ──────────────────────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProblemTotalText))]
    private int _problemTotal;

    /// <summary>The status bar ⚠ button's accessible name ("8 problems").</summary>
    public string ProblemTotalText => ProblemTotal == 1 ? "1 problem" : $"{ProblemTotal:N0} problems";

    /// <summary>
    /// Recounts one asset's problems and moves the running total by the difference. <see cref="Validator.CountProblems"/>
    /// counts by the rule the editor's own count follows, so an open asset's tab and this total agree; a tab that has just
    /// brought its rows up to date (an edit in it, a file check landing) passes its own count instead of having every
    /// row checked again on each keystroke.
    /// </summary>
    private void UpdateProblemEntry(AssetRecord asset, AssetEditorViewModel? current = null)
    {
        Func<string, string, bool> exists = (type, name) => FindAsset(type, name) is not null;
        int count;
        if (current is not null)
            count = current.ProblemCount;
        else
        {
            count = Validator.CountProblems(asset, FindAsset, exists);
            if (Services.Extensions.ExtensionRegistry.Manifests.Count > 0)
                count += Services.Extensions.ExtensionRegistry.CountProblems(asset, GdtOf(asset)?.Extensions, exists);
        }
        var previous = _problemsByAsset.GetValueOrDefault(asset);
        _problemsByAsset[asset] = count;
        ProblemTotal += count - previous;
    }

    private void SumProblems() => ProblemTotal = _problemsByAsset.Values.Sum();

    /// <summary>An open tab's problems moved without an edit (a file check landed): the total counts the asset again.</summary>
    internal void OnTabProblemsChanged(AssetEditorViewModel tab) => UpdateProblemEntry(tab.Record, tab);

    /// <summary>Problem count for an asset (used by tree badges and the is:problems filter).</summary>
    public int ProblemsOf(AssetRecord asset) => _problemsByAsset.GetValueOrDefault(asset);

    [RelayCommand]
    private void GoToParent()
    {
        if (ActiveTab?.Parent is { } parent)
            NavigateToRef(ActiveTab.TypeName, parent);
    }

    // ── Inspector ────────────────────────────────────────────────────────────
    public RangeObservableCollection<RefItemViewModel> References { get; } = new();

    [ObservableProperty]
    private int _referenceCount;

    partial void OnActiveTabChanged(AssetEditorViewModel? oldValue, AssetEditorViewModel? newValue)
    {
        if (oldValue is not null)
            oldValue.PropertyChanged -= ActiveTab_HistoryChanged;
        if (newValue is not null)
            newValue.PropertyChanged += ActiveTab_HistoryChanged;
        NotifyUndoRedo();
        TrackWindowTitle(oldValue, newValue);
        MarkTabsDirty();
        RecordVisit(oldValue, newValue);
        RefreshReferences();
        RefreshPreviewSubjects();
        NotifyPreviewPlacement();
        RefreshInspectorProperty();
        OnPropertyChanged(nameof(HasActiveTab));
        OnPropertyChanged(nameof(HasNoActiveTab));
        OnPropertyChanged(nameof(IsActivePinned));
        if (newValue is null)
            RefreshStartPage();
    }

    public bool HasActiveTab => ActiveTab is not null;
    public bool HasNoActiveTab => ActiveTab is null;

    private void RefreshReferences()
    {
        if (ActiveTab is null)
        {
            References.ReplaceAll(Array.Empty<RefItemViewModel>());
            ReferenceCount = 0;
            RefreshLinks();
            return;
        }
        // WI-4: tab switching does zero full-corpus scans — derivation refs come from the reverse-parent
        // index and value refs only from the materialized registry (the pre-existing rule), never 121k.
        var refs = ReferencesTo(ActiveTab.Name, ActiveTab.Record);
        ReferenceCount = refs.Count;
        References.ReplaceAll(refs.Take(200).Select(asset => new RefItemViewModel(asset, a => OpenAsset(a, preview: true))));
        RefreshLinks();
    }
}
