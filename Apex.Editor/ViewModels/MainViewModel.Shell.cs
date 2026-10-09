using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Apex.Editor.Models;
using Apex.Editor.Services.Gdt;
using GdtEncoding = Apex.Render.Data.Gdt.GdtEncoding;

namespace Apex.Editor.ViewModels;

/// <summary>
/// Shell dialogs (new, rename, delete), the corpus-wide reference scan behind them, and the ranking
/// shared by Quick Open and the command palette.
/// </summary>
public sealed partial class MainViewModel
{
    // ══ Modal state ══════════════════════════════════════════════════════════

    /// <summary>A confirm / rename / new / save-conflict dialog is up: the window suspends its shortcuts behind it.</summary>
    public bool IsModalOpen => IsConfirmOpen || IsRenameOpen || IsNewOpen || IsConflictOpen;

    partial void OnIsConfirmOpenChanged(bool value) => OnPropertyChanged(nameof(IsModalOpen));
    partial void OnIsRenameOpenChanged(bool value) => OnPropertyChanged(nameof(IsModalOpen));
    partial void OnIsNewOpenChanged(bool value) => OnPropertyChanged(nameof(IsModalOpen));
    partial void OnIsConflictOpenChanged(bool value) => OnPropertyChanged(nameof(IsModalOpen));

    /// <summary>Raised after Quick Open opens an asset: the window moves keyboard focus into its editor.</summary>
    public event Action? EditorFocusRequested;

    private void RequestEditorFocus() => EditorFocusRequested?.Invoke();

    // ══ Window title ═════════════════════════════════════════════════════════

    /// <summary>
    /// "wpn_ar_x — Apex", with "● " in front while that asset has unsaved changes (the tab's own mark): the taskbar and
    /// Alt+Tab say what is open and whether it is saved. "Apex" with nothing open.
    /// </summary>
    public string WindowTitle => ActiveTab is { } tab ? $"{(tab.Changes.Count > 0 ? "● " : "")}{tab.Name} — Apex" : "Apex";

    private void TrackWindowTitle(AssetEditorViewModel? oldTab, AssetEditorViewModel? newTab)
    {
        if (oldTab is not null)
        {
            oldTab.Changes.CollectionChanged -= ActiveTabChanges_Changed;
            oldTab.PropertyChanged -= ActiveTabName_Changed;
        }
        if (newTab is not null)
        {
            newTab.Changes.CollectionChanged += ActiveTabChanges_Changed;
            newTab.PropertyChanged += ActiveTabName_Changed;
        }
        OnPropertyChanged(nameof(WindowTitle));
    }

    private void ActiveTabChanges_Changed(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) =>
        OnPropertyChanged(nameof(WindowTitle));

    private void ActiveTabName_Changed(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AssetEditorViewModel.Name))
            OnPropertyChanged(nameof(WindowTitle));
    }

    // ══ Reference scan ═══════════════════════════════════════════════════════

    /// <summary>
    /// Every asset referencing <paramref name="target"/> by name — as its derivation parent or in any
    /// property value — across the whole corpus, not just the assets opened this session. Values are
    /// read raw from each .gdt (one read per file, files in parallel) on the thread pool; records
    /// already in memory are checked in memory, so session edits count. Results come in GDT order.
    /// Call on the UI thread; cancel to abandon (a closed dialog, a newer request).
    /// </summary>
    public Task<IReadOnlyList<AssetRecord>> FindReferencesAsync(AssetRecord target, CancellationToken cancel = default)
    {
        var name = target.Name;
        var files = _db.Gdts.Select(GetSnapshot).ToArray();
        return Task.Run(() => ScanReferences(files, name, target, cancel), cancel);
    }

    private static IReadOnlyList<AssetRecord> ScanReferences(
        IReadOnlyList<AssetRecord>[] files, string name, AssetRecord exclude, CancellationToken cancel)
    {
        // A name the GDT code page can't hold can't be written in any GDT, so nothing refers to it.
        if (!GdtEncoding.CanHold(name, out _))
            return Array.Empty<AssetRecord>();
        var needle = GdtEncoding.GetBytes(name);
        var perFile = new List<AssetRecord>?[files.Length];
        Parallel.For(0, files.Length, new ParallelOptions { CancellationToken = cancel }, i =>
        {
            using var scan = GdtFileScan.Begin();
            List<AssetRecord>? hits = null;
            foreach (var a in files[i])
            {
                if (ReferenceEquals(a, exclude))
                    continue;
                if (RefersTo(a, name, needle, scan))
                    (hits ??= new List<AssetRecord>()).Add(a);
            }
            perFile[i] = hits;
        });
        var result = new List<AssetRecord>();
        foreach (var hits in perFile)
            if (hits is not null)
                result.AddRange(hits);
        return result;
    }

    private static bool RefersTo(AssetRecord a, string name, byte[] needle, GdtFileScan scan)
    {
        if (string.Equals(a.Parent, name, StringComparison.OrdinalIgnoreCase))
            return true;
        var body = scan.GetBody(a, out var hasBody);
        if (hasBody)
        {
            // Asset names are plain ASCII identifiers, so the raw (still-escaped) bytes compare as-is.
            var reader = new GdtPropertyReader(body);
            while (reader.MoveNext())
                if (reader.ValueRaw.Length == needle.Length && AsciiEqualsIgnoreCase(reader.ValueRaw, needle))
                    return true;
            return false;
        }
        foreach (var value in scan.ScanProperties(a).Values)
            if (value.Equals(name, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static bool AsciiEqualsIgnoreCase(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        for (var i = 0; i < a.Length; i++)
        {
            int x = a[i], y = b[i];
            if (x == y)
                continue;
            if ((x | 0x20) != (y | 0x20) || (x | 0x20) is < 'a' or > 'z')
                return false;
        }
        return true;
    }

    /// <summary>A reference scan tied to one dialog: "Checking references…" only once it passes 150 ms.</summary>
    private sealed class ReferenceCheck
    {
        private readonly CancellationTokenSource _cancel = new();
        private readonly DispatcherTimer _slow = new() { Interval = TimeSpan.FromMilliseconds(150) };

        public ReferenceCheck(MainViewModel owner, AssetRecord target, Action slow, Action<IReadOnlyList<AssetRecord>> done)
        {
            _slow.Tick += (_, _) =>
            {
                _slow.Stop();
                slow();
            };
            _slow.Start();
            Run(owner, target, done);
        }

        public IReadOnlyList<AssetRecord>? Result { get; private set; }
        public bool IsDone => Result is not null;

        private async void Run(MainViewModel owner, AssetRecord target, Action<IReadOnlyList<AssetRecord>> done)
        {
            IReadOnlyList<AssetRecord> refs;
            try { refs = await owner.FindReferencesAsync(target, _cancel.Token); }
            catch (OperationCanceledException) { return; }
            catch
            {
                // Unreadable files are skipped inside the scan; anything else falls back to what's indexed.
                refs = owner.ReferencesTo(target.Name, target);
            }
            _slow.Stop();
            if (_cancel.IsCancellationRequested)
                return;
            Result = refs;
            done(refs);
        }

        public void Cancel()
        {
            _slow.Stop();
            _cancel.Cancel();
        }
    }

    /// <summary>"1 asset", "1,204 assets": a count with its noun, singular when it is one.</summary>
    private static string Plural(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n:N0} {noun}s";

    private static string ListNames(IReadOnlyList<AssetRecord> refs, int max = 3)
    {
        var names = string.Join(", ", refs.Take(max).Select(r => r.Name));
        return refs.Count > max ? $"{names} and {refs.Count - max:N0} more" : names;
    }

    // ══ Delete ═══════════════════════════════════════════════════════════════
    // No question first: a delete is one undo step (Ctrl+Z puts the asset back, values, extension data and tab with it)
    // until the save that writes it. What it does refuse is a delete that would leave a dangling reference, and that
    // check covers the whole corpus, so it runs off the UI thread and the delete lands the moment it comes back clean.

    private readonly Dictionary<AssetRecord, ReferenceCheck> _deleteChecks = new();

    /// <summary>A delete is waiting on its corpus-wide reference check (tests wait on this).</summary>
    public bool IsDeletePending => _deleteChecks.Count > 0;

    /// <summary>Deletes the active asset (the ⋯ menu, the palette).</summary>
    [RelayCommand]
    private void DeleteActive()
    {
        if (ActiveTab is { } tab)
            DeleteAsset(tab.Record);
    }

    /// <summary>
    /// Deletes <paramref name="asset"/> once nothing references it: refused at once when a deriver or an open asset
    /// does, otherwise after the corpus scan (its "Checking…" line only past 150 ms). The asset stays where it is until
    /// then, and a second request for it while the scan runs is the same delete.
    /// </summary>
    public void DeleteAsset(AssetRecord asset)
    {
        if (!IsLive(asset) || _deleteChecks.ContainsKey(asset) || PlacementBlocked())
            return;
        var name = asset.Name;
        // Derivers and anything already open are known at once; refuse without waiting for the scan.
        var known = ReferencesTo(name, asset);
        if (known.Count > 0)
        {
            AlertDeleteBlocked(name, known);
            return;
        }
        _deleteChecks[asset] = new ReferenceCheck(this, asset,
            slow: () => ShowProgress($"Checking what references {name}…"),
            done: refs =>
            {
                _deleteChecks.Remove(asset);
                if (refs.Count > 0)
                {
                    // A reference turned up in a GDT nobody has opened: the delete is off.
                    ClearProgress();
                    AlertDeleteBlocked(name, refs);
                    return;
                }
                if (!IsLive(asset) || PlacementBlocked())
                    return;
                DeleteNow(asset);
            });
    }

    private void CancelDeleteChecks()
    {
        foreach (var check in _deleteChecks.Values)
            check.Cancel();
        _deleteChecks.Clear();
    }

    private void AlertDeleteBlocked(string name, IReadOnlyList<AssetRecord> referrers)
    {
        // Refusing is the right call, but a refusal is only useful if the user can get to the
        // thing blocking them — so offer the referrer directly (the Inspector lists them all).
        var first = referrers[0];
        Alert(
            referrers.Count == 1
                ? $"Can't delete {name}: {first.Name} still references it."
                : $"Can't delete {name}: {referrers.Count:N0} assets still reference it. The Inspector's “Used by” list has all of them.",
            isError: true,
            actionLabel: $"Open {first.Name}",
            action: () => OpenAsset(first, preview: true));
    }

    // ══ Rename ═══════════════════════════════════════════════════════════════

    [ObservableProperty]
    private bool _isRenameOpen;

    /// <summary>Why the typed name can't be used ("wpn_x already exists"); the dialog stays open.</summary>
    [ObservableProperty]
    private string _renameError = "";

    /// <summary>What renaming will touch, from the reference scan ("3 assets reference it…").</summary>
    [ObservableProperty]
    private string _renameNote = "";

    private AssetEditorViewModel? _renameTab;
    private ReferenceCheck? _renameCheck;
    private bool _commitWhenChecked;

    /// <summary>The name an orphan's extension data already has that Enter was pressed on once: a second Enter replaces it.</summary>
    private string? _replaceOrphanOf;

    [RelayCommand]
    private void OpenRename()
    {
        if (ActiveTab is not { } tab)
            return;
        EndRename();
        _renameTab = tab;
        tab.RenameText = tab.Name;
        tab.PropertyChanged += RenameTab_PropertyChanged;
        RenameError = "";
        RenameNote = "";
        _renameCheck = new ReferenceCheck(this, tab.Record,
            slow: () => RenameNote = "Checking references…",
            done: refs =>
            {
                RenameNote = refs.Count switch
                {
                    0 => "Nothing references this asset.",
                    1 => $"{refs[0].Name} references it and will be updated to the new name.",
                    _ => $"{refs.Count:N0} assets reference it and will be updated to the new name.",
                };
                if (_commitWhenChecked)
                    CommitRename();
            });
        IsRenameOpen = true;
    }

    private void RenameTab_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AssetEditorViewModel.RenameText))
        {
            RenameError = "";
            _replaceOrphanOf = null;
        }
    }

    [RelayCommand]
    private void CommitRename()
    {
        if (_renameTab is not { } tab)
            return;
        var newName = tab.RenameText.Trim();
        if (newName.Length == 0)
        {
            RenameError = "Type a name.";
            return;
        }
        if (newName.Equals(tab.Record.Name, StringComparison.OrdinalIgnoreCase))
        {
            CancelRename();
            return;
        }
        if (FindAsset("", newName) is not null)
        {
            RenameError = $"{newName} already exists.";
            return;
        }
        // Extension data left under the name by an asset renamed or deleted outside Apex: the asset would read it as its
        // own, so say so once; Enter again takes the name and replaces that data (a save backs it up, Discard all changes
        // brings it back).
        if (OrphanBlockFile(newName) is { } file && !newName.Equals(_replaceOrphanOf, StringComparison.OrdinalIgnoreCase))
        {
            _replaceOrphanOf = newName;
            RenameError = $"{file} has extension data named {newName} that belongs to no asset. Press Enter again to rename; this asset's own data replaces it.";
            return;
        }
        if (_renameCheck is { IsDone: false })
        {
            // Enter pressed while the scan is still reading: rename the moment it lands.
            _commitWhenChecked = true;
            RenameNote = "Checking references…";
            return;
        }
        var refs = _renameCheck?.Result;
        EndRename();
        IsRenameOpen = false;
        RenameTab(tab, refs);
    }

    [RelayCommand]
    private void CancelRename()
    {
        EndRename();
        IsRenameOpen = false;
    }

    private void EndRename()
    {
        _renameCheck?.Cancel();
        _renameCheck = null;
        _commitWhenChecked = false;
        _replaceOrphanOf = null;
        if (_renameTab is not null)
            _renameTab.PropertyChanged -= RenameTab_PropertyChanged;
        _renameTab = null;
    }

    /// <summary>
    /// Rewrites references found in GDTs nobody opened. They are read into memory and marked as
    /// edited so they show as changed and survive an on-disk reload; the rename's own pass then
    /// updates their values along with every other in-memory record.
    /// </summary>
    private void LoadScannedReferrers(IReadOnlyList<AssetRecord>? scanned)
    {
        if (scanned is null)
            return;
        using (GdtFileScan.Begin())
            foreach (var a in scanned)
            {
                if (a.IsMaterialized)
                    continue;
                a.CaptureBaseline();
                a.HasSessionEdits = true;
                MarkMaterialized(a);
            }
    }

    // ══ New asset / new GDT ══════════════════════════════════════════════════

    [ObservableProperty]
    private bool _isNewOpen;

    /// <summary>New GDT (name only) rather than new asset (name, type, GDT).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NewTitle), nameof(NewIsAsset))]
    private bool _newIsGdt;

    public bool NewIsAsset => !NewIsGdt;
    public string NewTitle => NewIsGdt ? "New GDT" : "New asset";

    [ObservableProperty]
    private string _newName = "";

    [ObservableProperty]
    private string? _newType;

    [ObservableProperty]
    private string? _newGdtName;

    [ObservableProperty]
    private string _newError = "";

    [ObservableProperty]
    private List<string> _newTypeChoices = new();

    /// <summary>What is typed in the GDT search; the list under it narrows to the GDTs whose name holds it.</summary>
    [ObservableProperty]
    private string _newGdtFilter = "";

    /// <summary>The GDTs the search matches: short names first, the last used and the one in view on top.</summary>
    public RangeObservableCollection<GdtChoice> NewGdtMatches { get; } = new();

    private GdtChoice? _newGdtChoice;

    /// <summary>
    /// The highlighted GDT in the list, which is the one the asset goes in (<see cref="NewGdtName"/>). The list clears
    /// its selection when its items are replaced (each keystroke in the search); that null is refused while the GDT is
    /// still listed, so the highlight stays where it was.
    /// </summary>
    public GdtChoice? NewGdtChoice
    {
        get => _newGdtChoice;
        set
        {
            if (value is null && _newGdtChoice is not null && NewGdtMatches.Contains(_newGdtChoice))
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() => OnPropertyChanged(nameof(NewGdtChoice)));
                return;
            }
            if (ReferenceEquals(value, _newGdtChoice))
                return;
            _newGdtChoice = value;
            OnPropertyChanged();
            OnNewGdtChoiceChanged(value);
        }
    }

    /// <summary>Every GDT, in the order the list offers them with nothing typed (built when the dialog opens).</summary>
    private List<GdtChoice> _newGdtAll = new();

    private bool _syncingGdt;

    /// <summary>The GDT the Explorer's selection sits in; New asset defaults to it.</summary>
    public GdtFile? ExplorerGdt { get; set; }

    private GdtFile? _lastNewGdt;

    [RelayCommand]
    private void NewAsset() => OpenNew(isGdt: false, ExplorerGdt);

    [RelayCommand]
    private void NewGdt() => OpenNew(isGdt: true, null);

    /// <summary>A GDT row's "New asset in this GDT…".</summary>
    public void NewAssetIn(GdtFile gdt) => OpenNew(isGdt: false, gdt);

    private void OpenNew(bool isGdt, GdtFile? gdt)
    {
        NewIsGdt = isGdt;
        NewName = "";
        NewError = "";
        if (!isGdt)
        {
            NewTypeChoices = TypeOptions.Select(o => o.Label)
                .Where(t => !t.Equals(GdtLoader.UnknownType, StringComparison.OrdinalIgnoreCase))
                .ToList();
            // Default to what the user is looking at: the open asset's type, the selected GDT.
            var type = ActiveTab?.Record.Type;
            NewType = type is not null && NewTypeChoices.Contains(type, StringComparer.OrdinalIgnoreCase)
                ? NewTypeChoices.First(t => t.Equals(type, StringComparison.OrdinalIgnoreCase))
                : NewTypeChoices.FirstOrDefault(TypeStyles.IsWeaponType) ?? NewTypeChoices.FirstOrDefault();
            var target = gdt ?? _lastNewGdt ?? (ActiveTab is { } t ? _db.Gdts.FirstOrDefault(g => g.Name == t.GdtName) : null);
            // The likely ones first (the target, the last used, the Explorer's, the open asset's), then by short name.
            var first = new[] { target, _lastNewGdt, ExplorerGdt, ActiveTab is { } a ? GdtOf(a.Record) : null }
                .OfType<GdtFile>().Where(_db.Gdts.Contains).Distinct();
            _newGdtAll = first.Concat(_db.Gdts.OrderBy(g => ShortGdt(g.Name), StringComparer.OrdinalIgnoreCase))
                .Distinct().Select(g => new GdtChoice(g, g == _lastNewGdt)).ToList();
            _syncingGdt = true;
            NewGdtFilter = "";
            _syncingGdt = false;
            NewGdtMatches.ReplaceAll(_newGdtAll);
            NewGdtName = target?.Name ?? _newGdtAll.FirstOrDefault()?.Name;
        }
        IsNewOpen = true;
    }

    partial void OnNewGdtFilterChanged(string value)
    {
        if (_syncingGdt)
            return;
        var q = value.Trim();
        var matches = q.Length == 0
            ? _newGdtAll
            : _newGdtAll
                .Select((c, order) => (Choice: c, Order: order, Rank: NameRank(c.Short, q) ?? (c.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ? (3, c.Short.Length) : null)))
                .Where(x => x.Rank is not null)
                .OrderBy(x => x.Rank!.Value.Rank).ThenBy(x => x.Rank!.Value.Length).ThenBy(x => x.Order)
                .Select(x => x.Choice)
                .ToList();
        NewGdtMatches.ReplaceAll(matches);
        // The highlight follows the search: it stays on its GDT while that still matches, else it goes to the best match,
        // so Enter after typing puts the asset where the user was looking.
        if (NewGdtChoice is null || !matches.Contains(NewGdtChoice))
            NewGdtChoice = matches.FirstOrDefault();
    }

    private void OnNewGdtChoiceChanged(GdtChoice? value)
    {
        if (_syncingGdt || value is null)
            return;
        _syncingGdt = true;
        NewGdtName = value.Name;
        _syncingGdt = false;
    }

    partial void OnNewGdtNameChanged(string? value)
    {
        if (_syncingGdt)
            return;
        _syncingGdt = true;
        NewGdtChoice = NewGdtMatches.FirstOrDefault(c => c.Name == value) ?? _newGdtAll.FirstOrDefault(c => c.Name == value);
        _syncingGdt = false;
    }

    /// <summary>↓ in the GDT search: the next GDT in the list.</summary>
    [RelayCommand]
    private void NewGdtNext() => NewGdtStep(1);

    /// <summary>↑ in the GDT search: the previous GDT in the list.</summary>
    [RelayCommand]
    private void NewGdtPrevious() => NewGdtStep(-1);

    private void NewGdtStep(int delta)
    {
        if (NewGdtMatches.Count == 0)
            return;
        var at = NewGdtChoice is null ? -1 : NewGdtMatches.IndexOf(NewGdtChoice);
        NewGdtChoice = NewGdtMatches[Math.Clamp(at + delta, 0, NewGdtMatches.Count - 1)];
    }

    partial void OnNewNameChanged(string value)
    {
        var sanitized = SanitizeIdentifier(value, allowDot: NewIsGdt);
        if (sanitized != value)
        {
            NewName = sanitized;
            return;
        }
        NewError = NewNameProblem(value) ?? "";
    }

    /// <summary>Asset and GDT names are identifiers: lowercase, digits, underscores. Space types "_".</summary>
    private static string SanitizeIdentifier(string value, bool allowDot)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var raw in value)
        {
            var c = char.ToLowerInvariant(raw);
            if (c is ' ' or '-')
                sb.Append('_');
            else if (c is '_' or (>= 'a' and <= 'z') or (>= '0' and <= '9') || (allowDot && c == '.'))
                sb.Append(c);
        }
        return sb.ToString();
    }

    private string NewGdtFileName(string name) =>
        name.EndsWith(".gdt", StringComparison.OrdinalIgnoreCase) ? name : name + ".gdt";

    private string? NewNameProblem(string name)
    {
        if (name.Length == 0)
            return null;
        if (NewIsGdt)
        {
            var file = NewGdtFileName(name);
            return _db.Gdts.Any(g => ShortGdt(g.Name).Equals(ShortGdt(file), StringComparison.OrdinalIgnoreCase))
                ? $"{file} already exists."
                : null;
        }
        return FindAsset("", name) is not null ? $"{name} already exists." : null;
    }

    [RelayCommand]
    private void CommitNew()
    {
        var name = NewName.Trim();
        if (name.Length == 0)
        {
            NewError = NewIsGdt ? "Type a name for the GDT." : "Type a name for the asset.";
            return;
        }
        if (NewNameProblem(name) is { } problem)
        {
            NewError = problem;
            return;
        }
        if (NewIsGdt)
        {
            IsNewOpen = false;
            _lastNewGdt = CreateGdt(NewGdtFileName(name));
            return;
        }
        if (NewType is not { Length: > 0 } type)
        {
            NewError = "Pick a type.";
            return;
        }
        if (_db.Gdts.FirstOrDefault(g => g.Name == NewGdtName) is not { } gdt)
        {
            NewError = NewGdtMatches.Count == 0 ? $"No GDT is named like “{NewGdtFilter.Trim()}”." : "Pick a GDT.";
            return;
        }
        IsNewOpen = false;
        CreateAsset(name, type, gdt);
    }

    [RelayCommand]
    private void CancelNew() => IsNewOpen = false;

    // ══ Explorer search: Enter opens the top result ══════════════════════════

    private bool _openTopWhenReady;

    /// <summary>
    /// Enter in the Explorer's search box: open the first result as a kept tab. A query still inside
    /// its typing delay is applied first; one still scanning GDTs opens its top hit when it lands.
    /// </summary>
    public void OpenTopResult()
    {
        if (_debounce.IsEnabled)
            ApplyFilterNow();
        if (_filterScanCts is not null && !ReferenceEquals(_hitCacheTokens, _tokens))
        {
            _openTopWhenReady = true;
            return;
        }
        OpenTopResultNow();
    }

    private void OpenTopResultNow()
    {
        _openTopWhenReady = false;
        if (FlatRows.FirstOrDefault(n => n.Asset is not null && !n.IsPinnedEntry)?.Asset is { } top)
            OpenAsset(top);
    }

    // ══ Explorer selection actions (tree context menu, F2, Del) ═════════════

    private void Activate(AssetRecord asset)
    {
        if (ActiveTab?.Record != asset)
            OpenAsset(asset, preview: true);
    }

    public void RenameAsset(AssetRecord asset)
    {
        Activate(asset);
        OpenRename();
    }

    // ══ Ranking: Quick Open, commands, properties ═══════════════════════════

    /// <summary>
    /// Orders a name against a query the way the Explorer's search does: exact, then starts with,
    /// then contains; shorter names first within each. Null when it doesn't contain the query.
    /// </summary>
    private static (int Rank, int Length)? NameRank(string name, string query)
    {
        var at = name.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (at < 0)
            return null;
        return (RankAt(at, name.Length, query.Length), name.Length);
    }

    /// <summary><see cref="NameRank"/>'s rank for a name already found to contain the query at <paramref name="at"/>.</summary>
    private static int RankAt(int at, int nameLength, int queryLength) =>
        at == 0 ? (nameLength == queryLength ? 0 : 1) : 2;

    /// <summary>
    /// Scores a command title or property label against what was typed (lower is better): starts
    /// with it, then a word starting with it, then contains it, then its letters in order. Null when
    /// the letters aren't all there.
    /// </summary>
    private static int? TextScore(string text, string query)
    {
        if (query.Length == 0)
            return 0;
        var at = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (at == 0)
            return 0;
        if (at > 0)
        {
            // A later word starting with the query ("Explorer: Group by type" for "group").
            for (var i = at; i >= 0; i = i + 1 < text.Length ? text.IndexOf(query, i + 1, StringComparison.OrdinalIgnoreCase) : -1)
                if (!char.IsLetterOrDigit(text[i - 1]))
                    return 1;
            return 2;
        }
        // Letters in order ("gbt" → Group By Type); fewer skipped characters rank higher.
        var q = 0;
        var gaps = 0;
        var last = -1;
        for (var i = 0; i < text.Length && q < query.Length; i++)
        {
            if (char.ToLowerInvariant(text[i]) != char.ToLowerInvariant(query[q]))
                continue;
            if (last >= 0)
                gaps += i - last - 1;
            last = i;
            q++;
        }
        return q == query.Length ? 3 + Math.Min(gaps, 1000) : null;
    }

    /// <summary>Keeps the best <paramref name="limit"/> assets by <see cref="NameRank"/>, without sorting every match.</summary>
    private sealed class TopAssets
    {
        private readonly string _query;
        private readonly int _limit;
        private readonly List<(int Rank, int Length, AssetRecord Asset)> _best;

        public TopAssets(string query, int limit)
        {
            _query = query;
            _limit = limit;
            _best = new List<(int, int, AssetRecord)>(limit + 1);
        }

        public void Offer(AssetRecord a)
        {
            if (NameRank(a.Name, _query) is not { } r)
                return;
            Offer(a, r.Rank);
        }

        /// <summary>Offers a name already found to contain the query, with its <see cref="NameRank"/> rank.</summary>
        public void Offer(AssetRecord a, int rank)
        {
            var item = (rank, a.Name.Length, a);
            if (_best.Count == _limit && Compare(item, _best[^1]) >= 0)
                return;
            var at = _best.BinarySearch(item, Comparer<(int, int, AssetRecord)>.Create(Compare));
            _best.Insert(at < 0 ? ~at : at, item);
            if (_best.Count > _limit)
                _best.RemoveAt(_best.Count - 1);
        }

        private static int Compare((int Rank, int Length, AssetRecord Asset) x, (int Rank, int Length, AssetRecord Asset) y)
        {
            var c = x.Rank.CompareTo(y.Rank);
            if (c != 0) return c;
            c = x.Length.CompareTo(y.Length);
            return c != 0 ? c : string.Compare(x.Asset.Name, y.Asset.Name, StringComparison.OrdinalIgnoreCase);
        }

        public List<AssetRecord> Result() => _best.Select(b => b.Asset).ToList();
    }
}

/// <summary>A GDT in the new-asset dialog's list: its short name leads, its folder follows, dim.</summary>
public sealed class GdtChoice
{
    public GdtChoice(GdtFile gdt, bool lastUsed)
    {
        Name = gdt.Name;
        Short = MainViewModel.ShortGdt(gdt.Name);
        var slash = gdt.Name.LastIndexOfAny(new[] { '/', '\\' });
        Folder = slash >= 0 ? gdt.Name[..(slash + 1)] : "";
        var count = gdt.Assets.Count == 1 ? "1 asset" : $"{gdt.Assets.Count:N0} assets";
        Detail = lastUsed ? $"last used · {count}" : count;
    }

    /// <summary>The GDT's name as loaded (its relative path), which the asset records.</summary>
    public string Name { get; }

    public string Short { get; }
    public string Folder { get; }
    public string Detail { get; }

    public override string ToString() => Short;
}
