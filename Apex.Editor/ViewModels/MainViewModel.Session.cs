using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using Avalonia.Threading;
using Apex.Editor.Models;
using Apex.Editor.Services;
using Apex.Editor.Services.Session;

namespace Apex.Editor.ViewModels;

/// <summary>
/// Hot exit: the session (every changed value, every new, duplicated, renamed and deleted asset, new GDTs and the
/// open tabs) is journaled to <c>%LOCALAPPDATA%\Apex\session\</c> as it happens, and put back on the next launch,
/// until a save writes those changes to the GDTs (MainViewModel.Save.cs clears them from the journal). Nothing here writes a GDT. Undo history is not journaled: after a restart the restored values show as changes
/// (and "Undo all changes" puts an asset back), but Ctrl+Z starts empty.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>A structural edit as it happened, kept so discard can reverse it and compaction can rewrite it.</summary>
    private sealed class SessionOp
    {
        public required JournalEntry Entry { get; init; }
        public AssetRecord? Record { get; init; }
        public GdtFile? Gdt { get; init; }
        public string? OldName { get; init; }
        public List<AssetRecord>? Reparented { get; init; }

        /// <summary><c>mov</c>: the GDT the asset left.</summary>
        public GdtFile? From { get; init; }

        /// <summary><c>mov</c>: the asset as it still sits in its old GDT's file, which a save deletes there (null: it was never saved).</summary>
        public AssetRecord? Ghost { get; init; }

        /// <summary>
        /// <c>del</c>, <c>mov</c>: the asset's extension blocks taken out of its (old) GDT's sidecar; <c>ren</c>: blocks of
        /// no asset its new name replaced. For undo.
        /// </summary>
        public List<AssetRecord>? Blocks { get; set; }

        /// <summary><c>flat</c>: the parent the asset had and its own values before Underive.</summary>
        public string? OldParent { get; init; }
        public Dictionary<string, string>? OldValues { get; init; }
    }

    /// <summary>Writes land at most this long after the first edit they carry; typing never waits for the disk.</summary>
    private static readonly TimeSpan JournalDelay = TimeSpan.FromMilliseconds(250);

    /// <summary>Past this size the journal is rewritten from the session as it stands.</summary>
    private const long CompactAbove = 8L * 1024 * 1024;

    private SessionJournal? _journal;
    private string _sessionKey = "";
    private SessionJournal.ReadResult? _pendingRestore;
    private string? _journalUnavailable;
    private readonly HashSet<AssetRecord> _journalDirty = new();
    // Extension blocks the journal last saw with values: one that has gone since must be journaled as empty.
    private readonly HashSet<(AssetRecord Asset, string Id)> _journaledExtension = new();
    private bool _tabsDirty;
    private DispatcherTimer? _journalTimer;
    private readonly List<SessionOp> _sessionOps = new();
    private readonly List<JournalEntry> _unapplied = new();
    private bool _replaying;
    private readonly HashSet<AssetRecord> _replayFlattened = new();

    /// <summary>True while changes are being kept on disk (so closing loses nothing).</summary>
    public bool IsSessionKept => _journal is { IsHealthy: true };

    /// <summary>The journal's folder, or null when this window's changes aren't kept (tests, diagnostics).</summary>
    public string? SessionDirectory => _journal?.Directory;

    /// <summary>The start page's second line under the change count: how long they last (the title bar's chip,
    /// right above, already says they aren't in the GDTs yet).</summary>
    public string SessionKeptText => IsSessionKept
        ? "Kept across restarts."
        : "Held in memory. Closing Apex discards them.";

    /// <summary>The title bar change chip's tooltip.</summary>
    public string SessionChipTip => IsSessionKept
        ? "Changes not saved to the GDTs yet (Ctrl+S saves them). Apex keeps them across restarts until then. Click to list the changed assets."
        : "Changes not saved to the GDTs yet (Ctrl+S saves them). They are held in memory only. Click to list the changed assets.";

    // ── Startup ───────────────────────────────────────────────────────────────

    /// <summary>Opens this install's journal and reads what the last run left (applied by <see cref="RestoreSession"/>).</summary>
    private void InitSession(string? sessionRoot)
    {
        OpenTabs.CollectionChanged += OnTabsChangedForJournal;
        if (sessionRoot is not null)
            OpenSessionJournal(sessionRoot);
    }

    /// <summary>Opens the journal of the install this window uses (at startup, or once Locate… found it).</summary>
    private void OpenSessionJournal(string sessionRoot)
    {
        _sessionKey = SessionJournal.KeyFor(_env);
        var dir = System.IO.Path.Combine(sessionRoot, _sessionKey);
        _journal = SessionJournal.Open(dir, out var previous, out var error);
        if (_journal is null)
        {
            _journalUnavailable = error?.Message ?? "unknown";
            return;
        }
        _journal.WriteFailed += ex => Dispatcher.UIThread.Post(() => OnJournalWriteFailed(ex));
        if (previous.Entries.Count > 0)
            _pendingRestore = previous;
        else
            _journal.Append(HeadEntry());
    }

    private JournalEntry HeadEntry() => new() { Op = "head", Version = SessionJournal.FormatVersion, Env = _sessionKey };

    private void OnJournalWriteFailed(Exception ex)
    {
        UpdateSessionCount();
        OnPropertyChanged(nameof(IsSessionKept));
        OnPropertyChanged(nameof(SessionKeptText));
        OnPropertyChanged(nameof(SessionChipTip));
        Alert("Apex couldn't write its session file, so changes from now on aren't kept after closing.",
            detail: ex.Message);
    }

    /// <summary>
    /// Puts the previous run's session back: replays its structural edits in order, applies each asset's latest
    /// values over what its GDT holds now, reopens its tabs, then rewrites the journal compactly. Runs once the
    /// catalog is loaded (mock: at construction; live: when ingestion finishes).
    /// </summary>
    private void RestoreSession()
    {
        if (_journalUnavailable is { } why)
        {
            _journalUnavailable = null;
            Alert("Another Apex window has this install's session open, so changes made here aren't kept after closing.",
                isError: false, detail: why);
            return;
        }
        if (_journal is null || _pendingRestore is not { } previous)
            return;
        _pendingRestore = null;

        var conflicts = new List<string>();
        var unappliedAssets = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        JournalEntry? tabs;
        _replaying = true;
        try
        {
            tabs = Replay(previous.Entries, conflicts, unappliedAssets);
        }
        finally
        {
            _replaying = false;
        }

        SumProblems();
        ApplyFilterNow();
        UpdateSessionCount();
        if (ActiveTab is null)
            RefreshStartPage();
        foreach (var tab in OpenTabs)
            tab.SyncFromRecord();

        // Start the new run from one tidy file: this session as it now stands, torn records and all dropped.
        _journal.Rewrite(BuildCompactJournal());
        if (tabs is not null)
            RestoreTabs(tabs);

        var restored = SessionEditCount;
        if (restored > 0)
            Status = previous.EndedCleanly
                ? $"Restored {restored:N0} change{(restored == 1 ? "" : "s")} from your last session"
                : $"Restored {restored:N0} change{(restored == 1 ? "" : "s")} after Apex closed unexpectedly";

        if (unappliedAssets.Count > 0)
        {
            var first = unappliedAssets.First();
            var who = unappliedAssets.Count == 1 ? first : $"{first} and {unappliedAssets.Count - 1:N0} more";
            Alert($"Couldn't restore changes to {who}: not in any loaded GDT. Apex keeps them and tries again next launch.",
                isError: false,
                detail: string.Join(Environment.NewLine, unappliedAssets.Take(40)));
        }
        else if (conflicts.Count > 0)
        {
            Alert(conflicts.Count == 1
                    ? "1 restored value changed in its GDT since you edited it. Your value is kept."
                    : $"{conflicts.Count:N0} restored values changed in their GDTs since you edited them. Your values are kept.",
                isError: false, actionLabel: "Review", action: ReviewChanges,
                detail: string.Join(Environment.NewLine, conflicts.Take(40)));
        }
    }

    /// <summary>
    /// Applies journal records in order: structural edits as they come, each asset's newest <c>set</c> at the end
    /// (it is the asset's whole difference, so older ones add nothing). Records that can't apply are kept, never
    /// dropped. Returns the newest tabs record.
    /// </summary>
    private JournalEntry? Replay(IReadOnlyList<JournalEntry> entries, List<string> conflicts, SortedSet<string> unappliedAssets)
    {
        var latest = new Dictionary<AssetRecord, JournalEntry>();
        var latestExtension = new Dictionary<(object Asset, string Id), JournalEntry>(new ExtensionSetComparer());

        // A deleted asset's extension values go with it; a moved one's travel in its move's record.
        void ForgetExtensionSets(AssetRecord asset)
        {
            foreach (var k in latestExtension.Keys.Where(k => k.Asset == asset).ToList())
                latestExtension.Remove(k);
        }
        var unappliedSets = new Dictionary<string, JournalEntry>(StringComparer.OrdinalIgnoreCase);
        JournalEntry? tabs = null;

        void Unapplied(JournalEntry e)
        {
            if (e.Op == "set")
                unappliedSets[e.Gdt + "|" + e.Name] = e;
            else
                _unapplied.Add(e);
            if (e.Name is { } n)
                unappliedAssets.Add(n);
        }

        foreach (var e in entries)
        {
            switch (e.Op)
            {
                case "gdt" when e.Gdt is { } gdtName:
                    if (_db.Gdts.Any(g => g.Name.Equals(gdtName, StringComparison.OrdinalIgnoreCase)))
                        break;
                    var gdt = new GdtFile { Name = gdtName };
                    _db.Gdts.Add(gdt);
                    _db.Gdts.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
                    RecordOp(new SessionOp { Entry = e, Gdt = gdt });
                    break;

                case "add" when e.Gdt is not null && e.Name is not null && e.Type is not null:
                    var into = _db.Gdts.FirstOrDefault(g => g.Name.Equals(e.Gdt, StringComparison.OrdinalIgnoreCase));
                    if (into is null || FindAsset("", e.Name) is not null)
                    {
                        Unapplied(e);
                        break;
                    }
                    var rec = new AssetRecord { Name = e.Name, Type = e.Type, GdtName = into.Name, Parent = e.Parent };
                    if (e.Props is { } props)
                        foreach (var (k, v) in props)
                            rec.Properties[k] = v;
                    rec.CaptureBaseline();
                    InsertByName(into.Assets, rec);
                    _db.Assets.Add(rec);
                    IndexAdd(rec);
                    MarkMaterialized(rec);
                    InvalidateSnapshot(into);
                    _leafCache[rec] = BrowserNode.ForAsset(rec, ToggleNode);
                    if (e.ExtensionProps is { } added)
                        ExtensionSidecar.Of(into).AddReplayed(rec.Name, added);
                    UpdateProblemEntry(rec);
                    RecordOp(new SessionOp { Entry = e, Record = rec, Gdt = into });
                    break;

                case "ren" when e.To is not null:
                    if (FindInGdt(e.Gdt, e.Name) is not { } renamed || FindAsset("", e.To) is not null)
                    {
                        Unapplied(e);
                        break;
                    }
                    var oldName = renamed.Name;
                    renamed.Name = e.To;
                    IndexRename(renamed, oldName);
                    var replaced = GdtOf(renamed)?.Extensions?.Rename(oldName, e.To);
                    var reparented = new List<AssetRecord>();
                    if (_byParent.TryGetValue(oldName, out var derivers))
                        foreach (var d in derivers.ToArray())
                        {
                            if (ReferenceEquals(d, renamed))
                                continue;
                            var op = d.Parent;
                            d.Parent = e.To;
                            IndexReparent(d, op);
                            reparented.Add(d);
                        }
                    _leafCache[renamed] = BrowserNode.ForAsset(renamed, ToggleNode);
                    RecordOp(new SessionOp { Entry = e, Record = renamed, OldName = oldName, Reparented = reparented, Blocks = replaced });
                    break;

                case "del":
                    if (FindInGdt(e.Gdt, e.Name) is not { } doomed)
                        break; // already gone: nothing left to delete
                    var from = _db.Gdts.FirstOrDefault(g => g.Name.Equals(doomed.GdtName, StringComparison.OrdinalIgnoreCase));
                    from?.Assets.Remove(doomed);
                    _db.Assets.Remove(doomed);
                    _leafCache.Remove(doomed);
                    _problemsByAsset.Remove(doomed);
                    _changedKeys.Remove(doomed);
                    IndexRemove(doomed);
                    if (from is not null)
                        InvalidateSnapshot(from);
                    latest.Remove(doomed);
                    ForgetExtensionSets(doomed);
                    RecordOp(new SessionOp { Entry = e, Record = doomed, Gdt = from, Blocks = from?.Extensions?.Detach(doomed.Name) });
                    break;

                case "mov" when e.To is not null:
                    var dest = _db.Gdts.FirstOrDefault(g => g.Name.Equals(e.To, StringComparison.OrdinalIgnoreCase));
                    if (dest is not null && FindInGdt(e.Gdt, e.Name) is { } moving)
                    {
                        // What it held before the move is in the move's own record of its extension values.
                        ForgetExtensionSets(moving);
                        RecordOp(MoveOp(moving, dest, e));
                        break;
                    }
                    // The old file was saved without it and the new one wasn't: the move's values bring it back, new in
                    // its new GDT.
                    if (dest is not null && e.Name is not null && e.Type is not null && e.Props is not null && FindAsset("", e.Name) is null)
                    {
                        var back = new AssetRecord { Name = e.Name, Type = e.Type, GdtName = dest.Name, Parent = e.Parent };
                        foreach (var (k, v) in e.Props)
                            back.Properties[k] = v;
                        back.CaptureBaseline();
                        AttachRecord(back, dest);
                        if (e.ExtensionProps is { } carried)
                            ExtensionSidecar.Of(dest).AddReplayed(back.Name, carried);
                        RecordOp(new SessionOp { Entry = e, Record = back, Gdt = dest });
                        break;
                    }
                    Unapplied(e);
                    break;

                case "flat":
                    if (FindInGdt(e.Gdt, e.Name) is not { } flat || flat.Parent is null || e.Props is null)
                    {
                        Unapplied(e);
                        break;
                    }
                    // Its values carry everything up to the underive; a set from before it is already in them.
                    latest.Remove(flat);
                    RecordOp(UnderiveOp(flat, e.Props, e));
                    _replayFlattened.Add(flat);
                    break;

                case "set":
                    if (FindInGdt(e.Gdt, e.Name) is { } target)
                    {
                        latest[target] = e;
                        unappliedSets.Remove(e.Gdt + "|" + e.Name);
                    }
                    else
                        Unapplied(e);
                    break;

                // Like a set, the newest per asset and extension is the whole difference. Kept by the asset it names
                // now, so a rename or move replayed after it still finds it; by name when no asset has it (an orphan).
                case "xset" when e.Gdt is not null && e.Name is not null && e.Type is not null:
                    latestExtension[(FindInGdt(e.Gdt, e.Name) as object ?? e.Gdt + "|" + e.Name, e.Type)] = e;
                    break;

                case "tabs":
                    tabs = e;
                    break;
            }
        }
        _unapplied.AddRange(unappliedSets.Values);

        foreach (var (record, e) in latest)
            ApplyRestoredValues(record, e, conflicts);
        _replayFlattened.Clear();
        var restored = new HashSet<AssetRecord>(latest.Keys);
        foreach (var ((asset, _), e) in latestExtension)
        {
            // An asset renamed or moved since the entry was written is where it is now.
            if (asset is AssetRecord owner)
            {
                e.Gdt = owner.GdtName;
                e.Name = owner.Name;
            }
            if (_db.Gdts.FirstOrDefault(g => g.Name.Equals(e.Gdt, StringComparison.OrdinalIgnoreCase)) is not { } gdt)
            {
                Unapplied(e);
                continue;
            }
            ApplyRestoredExtension(gdt, e, conflicts);
            if (FindInGdt(e.Gdt, e.Name) is { } found)
                restored.Add(found);
        }
        RecountSession(restored);
        MarkRecordsChangedInTree(restored.ToList());
        return tabs;
    }

    /// <summary>An asset by reference, an orphan's "gdt|name" case-insensitively; the extension id case-insensitively.</summary>
    private sealed class ExtensionSetComparer : IEqualityComparer<(object Asset, string Id)>
    {
        public bool Equals((object Asset, string Id) x, (object Asset, string Id) y) =>
            (x.Asset is string a && y.Asset is string b ? a.Equals(b, StringComparison.OrdinalIgnoreCase) : ReferenceEquals(x.Asset, y.Asset))
            && x.Id.Equals(y.Id, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((object Asset, string Id) k) =>
            HashCode.Combine(k.Asset is string s ? StringComparer.OrdinalIgnoreCase.GetHashCode(s) : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(k.Asset),
                StringComparer.OrdinalIgnoreCase.GetHashCode(k.Id));
    }

    /// <summary>Puts an asset's journaled extension values back into its block, as <see cref="ApplyRestoredValues"/> does for its GDT values.</summary>
    private static void ApplyRestoredExtension(GdtFile gdt, JournalEntry e, List<string> conflicts)
    {
        var x = ExtensionSidecar.Of(gdt);
        foreach (var c in e.Changes ?? new List<JournalChange>())
        {
            var now = x.Get(e.Name!, e.Type!, c.Key);
            if (now != c.Old && now != c.New)
                conflicts.Add($"{e.Name} · {c.Key} ({e.Type}): yours {Show(c.New)}, {System.IO.Path.GetFileName(gdt.Name)}x now {Show(now)}, was {Show(c.Old)}");
            try
            {
                x.Set(e.Name!, e.Type!, c.Key, c.New);
            }
            catch (InvalidOperationException)
            {
                // The key is the deffile's now (the deffiles changed since): it can't be extension data any more.
                conflicts.Add($"{e.Name} · {c.Key} ({e.Type}): not restored: its type's deffile declares it now, so it belongs in the GDT");
            }
        }

        static string Show(string? v) => v is null ? "(unset)" : v.Length == 0 ? "(empty)" : v;
    }

    /// <summary>
    /// An asset's extension blocks' whole differences from their baselines, compared exactly (an empty list: back to
    /// it). Journaled beside the asset's <c>set</c>, so unsaved extension values are kept across restarts too.
    /// </summary>
    private static IEnumerable<JournalEntry> ExtensionEntriesFor(AssetRecord r, Dictionary<string, ExtensionSidecar>? sidecars)
    {
        if (sidecars?.GetValueOrDefault(r.GdtName) is not { } x)
            yield break;
        foreach (var b in x.Blocks)
        {
            if (!b.IsMaterialized || b.SessionBaseline is not { } baseline || !b.Name.Equals(r.Name, StringComparison.OrdinalIgnoreCase))
                continue;
            var changes = new List<JournalChange>();
            foreach (var (key, value) in b.Properties)
                if (!baseline.TryGetValue(key, out var old) || old != value)
                    changes.Add(new JournalChange { Key = key, Old = old, New = value });
            foreach (var (key, value) in baseline)
                if (!b.Properties.ContainsKey(key))
                    changes.Add(new JournalChange { Key = key, Old = value, New = null });
            yield return new JournalEntry { Op = "xset", Gdt = r.GdtName, Name = r.Name, Type = b.Type, Changes = changes };
        }
    }

    /// <summary>
    /// Puts an asset's journaled values back over what it holds now. The user's value always wins; where the GDT no
    /// longer holds the value the edit was made against, the conflict is reported. A key the user already changed in
    /// this run keeps this run's value.
    /// </summary>
    private void ApplyRestoredValues(AssetRecord record, JournalEntry e, List<string> conflicts)
    {
        record.CaptureBaseline();
        var baseline = record.SessionBaseline!;
        var schema = SchemaRegistry.Get(record.Type);
        string DefaultOf(string key) => schema?.Find(key)?.Default ?? "";
        foreach (var c in e.Changes ?? new List<JournalChange>())
        {
            // An asset Underive replayed holds its flattened values, not its baseline: the newest set is its whole truth.
            if (_replayFlattened.Contains(record))
            {
                EditHistory.Set(record, c.Key, c.New);
                continue;
            }
            var now = baseline.TryGetValue(c.Key, out var b) ? b : null;
            var current = record.Properties.TryGetValue(c.Key, out var cur) ? cur : null;
            if ((current ?? DefaultOf(c.Key)) != (now ?? DefaultOf(c.Key)))
                continue; // edited again since this launch
            if ((now ?? DefaultOf(c.Key)) != (c.Old ?? DefaultOf(c.Key)) && (now ?? DefaultOf(c.Key)) != (c.New ?? DefaultOf(c.Key)))
                conflicts.Add($"{record.Name} · {c.Key}: yours {Show(c.New)}, GDT now {Show(now)}, was {Show(c.Old)}");
            EditHistory.Set(record, c.Key, c.New);
        }
        MarkMaterialized(record);
        UpdateProblemEntry(record);

        static string Show(string? v) => v is null ? "(unset)" : v.Length == 0 ? "(empty)" : v;
    }

    private void RestoreTabs(JournalEntry tabs)
    {
        var wanted = new List<(AssetRecord Record, bool Preview)>();
        foreach (var t in tabs.Tabs ?? new List<JournalTab>())
            if (FindInGdt(t.Gdt, t.Name) is { } rec && wanted.All(w => w.Record != rec))
                wanted.Add((rec, t.Preview));
        if (wanted.Count == 0)
            return;
        var active = tabs.Active is int a && a >= 0 && a < (tabs.Tabs?.Count ?? 0)
            ? FindInGdt(tabs.Tabs![a].Gdt, tabs.Tabs[a].Name)
            : null;

        // One tab per dispatcher turn: opening an editor costs up to ~200 ms, and the window stays live between them.
        var next = 0;
        void OpenNext()
        {
            if (next < wanted.Count)
            {
                var (rec, preview) = wanted[next++];
                if (_db.Assets.Count > 0 && FindInGdt(rec.GdtName, rec.Name) == rec)
                    OpenAsset(rec, preview);
                Dispatcher.UIThread.Post(OpenNext, DispatcherPriority.Background);
                return;
            }
            if (active is not null && OpenTabs.FirstOrDefault(t => t.Record == active) is { } tab)
                ActiveTab = tab;
        }
        OpenNext();
    }

    /// <summary>The record named <paramref name="name"/> in GDT <paramref name="gdt"/> (O(1) via the name index).</summary>
    private AssetRecord? FindInGdt(string? gdt, string? name)
    {
        if (name is null || !_byName.TryGetValue(name, out var list))
            return null;
        foreach (var a in list)
            if (a.GdtName.Equals(gdt, StringComparison.OrdinalIgnoreCase))
                return a;
        return null;
    }

    private void RecordOp(SessionOp op)
    {
        _sessionOps.Add(op);
        _structuralEdits++;
    }

    // ── Recording ─────────────────────────────────────────────────────────────

    /// <summary>These records' values changed: journal them after <see cref="JournalDelay"/>. Costs a set insert.</summary>
    private void JournalDirty(IEnumerable<AssetRecord> records)
    {
        if (_journal is null || _replaying)
            return;
        foreach (var r in records)
            _journalDirty.Add(r);
        ScheduleJournal();
    }

    private void ScheduleJournal()
    {
        if (_journalTimer is null)
        {
            _journalTimer = new DispatcherTimer { Interval = JournalDelay };
            _journalTimer.Tick += (_, _) =>
            {
                _journalTimer.Stop();
                FlushJournalNow();
            };
        }
        // Started once, not restarted: a long scrub still reaches the disk every quarter second.
        if (!_journalTimer.IsEnabled)
            _journalTimer.Start();
    }

    private void OnTabsChangedForJournal(object? sender, NotifyCollectionChangedEventArgs e) => MarkTabsDirty();

    private void MarkTabsDirty()
    {
        if (_journal is null || _replaying)
            return;
        _tabsDirty = true;
        ScheduleJournal();
    }

    /// <summary>
    /// Journals a structural edit. Pending value changes go first so the file's order is the order things happened
    /// in; the op itself is queued at once.
    /// </summary>
    private void JournalOp(SessionOp op)
    {
        _sessionOps.Add(op);
        if (_journal is null)
            return;
        FlushJournalNow();
        _journal.Append(op.Entry);
        MarkTabsDirty();
    }

    private static JournalEntry AddEntry(AssetRecord rec, string? from) => new()
    {
        Op = "add",
        Gdt = rec.GdtName,
        Name = rec.Name,
        Type = rec.Type,
        Parent = rec.Parent,
        From = from,
        Props = new Dictionary<string, string>(rec.Properties, StringComparer.OrdinalIgnoreCase),
    };

    /// <summary>Queues everything not yet journaled. UI thread; the disk write happens on the journal's writer.</summary>
    public void FlushJournalNow()
    {
        _journalTimer?.Stop();
        if (_journal is null)
            return;
        var entries = new List<JournalEntry>(_journalDirty.Count + 1);
        var sidecars = _journalDirty.Count > 0 ? SidecarsByGdt() : null;
        foreach (var r in _journalDirty)
        {
            if (SetEntryFor(r) is { } e)
                entries.Add(e);
            AddExtensionEntries(r, sidecars, entries);
        }
        _journalDirty.Clear();
        if (_tabsDirty)
        {
            entries.Add(TabsEntry());
            _tabsDirty = false;
        }
        if (entries.Count > 0)
            _journal.Append(entries.ToArray());
        if (_journal.Length > CompactAbove)
            _journal.Rewrite(BuildCompactJournal());
    }

    /// <summary>
    /// The asset's <see cref="ExtensionEntriesFor"/>, and an empty one (back to the baseline) for each block the journal
    /// last saw with values that has gone since: a block never saved is dropped when it empties (an undo of the first
    /// value), and a replay would otherwise bring back its last journaled values.
    /// </summary>
    private void AddExtensionEntries(AssetRecord r, Dictionary<string, ExtensionSidecar>? sidecars, List<JournalEntry> entries)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in ExtensionEntriesFor(r, sidecars))
        {
            entries.Add(e);
            seen.Add(e.Type!);
            if (e.Changes!.Count > 0)
                _journaledExtension.Add((r, e.Type!));
            else
                _journaledExtension.Remove((r, e.Type!));
        }
        if (_journaledExtension.Count == 0)
            return;
        foreach (var gone in _journaledExtension.Where(k => k.Asset == r && !seen.Contains(k.Id)).ToList())
        {
            entries.Add(new JournalEntry { Op = "xset", Gdt = r.GdtName, Name = r.Name, Type = gone.Id, Changes = new List<JournalChange>() });
            _journaledExtension.Remove(gone);
        }
    }

    /// <summary>
    /// A record's whole difference from its baseline, compared exactly (the change count's numeric equality would
    /// lose "1" → "1.0"). An empty list is meaningful: the record is back to its baseline.
    /// </summary>
    private static JournalEntry? SetEntryFor(AssetRecord r)
    {
        if (r.SessionBaseline is not { } baseline || !r.IsMaterialized)
            return null;
        // Compared as CountSessionChanges compares (a derived asset's missing key inherits), but exactly.
        var changes = new List<JournalChange>();
        foreach (var (key, value) in r.Properties)
        {
            var had = baseline.TryGetValue(key, out var b);
            if (had ? value != b : !r.SameAsMissing(key, value, exact: true))
                changes.Add(new JournalChange { Key = key, Old = had ? b : null, New = value });
        }
        foreach (var (key, value) in baseline)
            if (!r.Properties.ContainsKey(key) && !r.SameAsMissing(key, value, exact: true))
                changes.Add(new JournalChange { Key = key, Old = value, New = null });
        return new JournalEntry { Op = "set", Gdt = r.GdtName, Name = r.Name, Changes = changes };
    }

    private JournalEntry TabsEntry()
    {
        var tabs = OpenTabs.Select(t => new JournalTab { Gdt = t.Record.GdtName, Name = t.Record.Name, Preview = t.IsPreview }).ToList();
        return new JournalEntry { Op = "tabs", Tabs = tabs, Active = ActiveTab is { } a ? OpenTabs.IndexOf(a) : -1 };
    }

    /// <summary>The session as it stands, as the shortest journal that replays to it.</summary>
    private List<JournalEntry> BuildCompactJournal()
    {
        var list = new List<JournalEntry> { HeadEntry() };
        foreach (var op in _sessionOps)
            list.Add(op.Entry);
        var records = new HashSet<AssetRecord>(_materialized);
        records.UnionWith(_changedKeys.Keys);
        var sidecars = SidecarsByGdt();
        _journaledExtension.Clear();
        foreach (var r in records)
        {
            if (SetEntryFor(r) is { Changes.Count: > 0 } e)
                list.Add(e);
            foreach (var x in ExtensionEntriesFor(r, sidecars).Where(x => x.Changes!.Count > 0))
            {
                list.Add(x);
                _journaledExtension.Add((r, x.Type!));
            }
        }
        list.AddRange(_unapplied);
        list.Add(TabsEntry());
        _journalDirty.Clear();
        _tabsDirty = false;
        return list;
    }

    /// <summary>
    /// Everything to disk now and wait for it (window close, app exit, crash). <paramref name="closing"/> marks the
    /// run as ended normally, which is how the next launch tells a crash from a close.
    /// </summary>
    public bool FlushSessionToDisk(bool closing = false)
    {
        if (_journal is null)
            return true;
        FlushJournalNow();
        if (closing)
            _journal.Append(new JournalEntry { Op = "bye" });
        return _journal.Flush(TimeSpan.FromSeconds(3));
    }

    /// <summary>For <see cref="CrashGuard.FlushSession"/>: capture pending edits when on the UI thread, then wait for the disk.</summary>
    public void FlushForCrash(bool onUiThread)
    {
        if (_journal is null)
            return;
        if (onUiThread)
            FlushJournalNow();
        _journal.Flush(TimeSpan.FromSeconds(2));
    }

    /// <summary>
    /// For <see cref="CrashGuard.Recovered"/>: one plain line naming what stopped (the command just run, else the part of
    /// the window the failure came from); the exception is in the tooltip and the log.
    /// </summary>
    public void ReportRecovered(Exception error, string? logPath)
    {
        var kept = IsSessionKept ? " Your changes are kept." : "";
        var text = FailedAction(error) is { } what
            ? $"{what} stopped: something went wrong inside Apex.{kept}"
            : $"Something went wrong and Apex stopped that action.{kept}";
        Alert(text,
            detail: $"{error.GetType().Name}: {error.Message}{(logPath is null ? "" : Environment.NewLine + logPath)}",
            actionLabel: logPath is null ? null : "Show log",
            action: logPath is null ? null : () => ShowLog(logPath));
    }

    /// <summary>A command run in the last two seconds ("Rename…"), else the area of the app the stack comes from.</summary>
    private string? FailedAction(Exception error)
    {
        if (_registry?.LastRun is { } last && DateTime.UtcNow - last.At < TimeSpan.FromSeconds(2))
            return $"“{last.Command.Name.TrimEnd('…')}”";
        foreach (var frame in new System.Diagnostics.StackTrace(error, false).GetFrames())
        {
            var type = frame.GetMethod()?.DeclaringType;
            while (type?.DeclaringType is { } outer)
                type = outer; // lambdas and iterators live in nested compiler types
            if (type?.Namespace?.StartsWith("Apex.Editor", StringComparison.Ordinal) != true)
                continue;
            var area = type.Name switch
            {
                var n when n.Contains("Preview") || n.Contains("Viewport") || n.Contains("Recoil") || n.Contains("ToolsGfx") => "The preview",
                var n when n.Contains("Notetrack") => "The notetracks",
                var n when n.StartsWith("Table") => "The table",
                var n when n.StartsWith("Compare") => "Compare",
                var n when n.StartsWith("AssetBrowser") || n.StartsWith("SearchMenu") => "The Explorer",
                var n when n.StartsWith("Inspector") => "The Inspector",
                var n when n.Contains("Save") => "Saving",
                var n when n.StartsWith("AssetEditor") || n.StartsWith("PropertyEditor") || n.Contains("Record") || n.Contains("LineList")
                    || n.Contains("Field") || n.Contains("ScrubNumberBox") || n.Contains("ChoiceBox") || n.Contains("SuggestBox") => "The editor",
                _ => null,
            };
            if (area is not null)
                return area;
        }
        return null;
    }

    private static void ShowLog(string path)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception) { /* no shell: the path is in the tooltip */ }
    }

    // ── Discard ───────────────────────────────────────────────────────────────

    /// <summary>"Discard all changes": asks, because nothing brings a discarded session back.</summary>
    private void DiscardSession()
    {
        if (SessionEditCount == 0 && _unapplied.Count == 0)
            return;
        var assets = _db.Assets.Count(a => a.HasSessionEdits);
        AskConfirm(
            "Discard all changes?",
            $"This puts back {SessionEditCount:N0} change{(SessionEditCount == 1 ? "" : "s")} across {assets:N0} asset{(assets == 1 ? "" : "s")}, "
            + "and removes the assets and GDTs you created. Undo can't bring them back.",
            Commands.CommandCatalog.Get(Commands.CommandCatalog.DiscardAll).Name,
            DiscardSessionNow);
    }

    /// <summary>Reverses every structural edit (newest first), puts every value back to its baseline, clears the journal.</summary>
    public void DiscardSessionNow()
    {
        var count = SessionEditCount;
        // A delete still waiting on its reference check would land on the session just put back.
        CancelDeleteChecks();
        _journalTimer?.Stop();
        _journalDirty.Clear();
        _journaledExtension.Clear();
        _tabsDirty = false;

        for (var i = _sessionOps.Count - 1; i >= 0; i--)
            ReverseOp(_sessionOps[i]);

        var touched = new HashSet<AssetRecord>(_materialized);
        touched.UnionWith(_changedKeys.Keys);
        foreach (var r in touched)
        {
            if (r.SessionBaseline is not { } baseline)
                continue;
            if (r.IsMaterialized)
            {
                var props = r.Properties;
                props.Clear();
                foreach (var (k, v) in baseline)
                    props[k] = v;
            }
            r.HasSessionEdits = false;
            r.History.Clear();
            UpdateProblemEntry(r);
        }
        foreach (var gdt in _db.Gdts)
            gdt.Extensions?.DiscardEdits();
        _changedKeys.Clear();
        _structuralEdits = 0;
        _sessionOps.Clear();
        _unapplied.Clear();
        ClearStructuralHistory();

        foreach (var tab in OpenTabs.ToList())
        {
            if (FindInGdt(tab.Record.GdtName, tab.Record.Name) != tab.Record)
            {
                CloseTab(tab);
                continue;
            }
            tab.SyncFromRecord();
            tab.NotifyHistoryChanged();
        }
        SumProblems();
        ApplyFilterNow();
        UpdateSessionCount();
        NotifyUndoRedo();
        RefreshReferences();
        if (ActiveTab is null)
            RefreshStartPage();
        _journal?.Rewrite(new List<JournalEntry> { HeadEntry(), TabsEntry() });
        Status = count == 1 ? "Discarded 1 change" : $"Discarded {count:N0} changes";
    }

    private void ReverseOp(SessionOp op)
    {
        switch (op.Entry.Op)
        {
            case "add" when op.Record is { } rec:
                var gdt = _db.Gdts.FirstOrDefault(g => g.Name == rec.GdtName);
                gdt?.Assets.Remove(rec);
                gdt?.Extensions?.Detach(rec.Name);
                _db.Assets.Remove(rec);
                _leafCache.Remove(rec);
                _problemsByAsset.Remove(rec);
                _changedKeys.Remove(rec);
                IndexRemove(rec);
                if (gdt is not null)
                    InvalidateSnapshot(gdt);
                break;

            case "gdt" when op.Gdt is { } file:
                if (file.Assets.Count == 0)
                {
                    _db.Gdts.Remove(file);
                    InvalidateSnapshot(file);
                }
                break;

            case "ren" when op.Record is { } rec && op.OldName is { } oldName:
                var newName = rec.Name;
                rec.Name = oldName;
                IndexRename(rec, newName);
                var x = GdtOf(rec)?.Extensions;
                x?.Rename(newName, oldName);
                if (op.Blocks is { } replacedBlocks)
                    x?.Restore(replacedBlocks);
                foreach (var d in op.Reparented ?? new List<AssetRecord>())
                {
                    var p = d.Parent;
                    d.Parent = oldName;
                    IndexReparent(d, p);
                }
                _leafCache[rec] = BrowserNode.ForAsset(rec, ToggleNode);
                foreach (var tab in OpenTabs.Where(t => t.Record == rec))
                    tab.NotifyRenamed();
                break;

            case "del" when op.Record is { } rec:
                if (op.Gdt is { } home && _db.Gdts.Contains(home))
                {
                    InsertByName(home.Assets, rec);
                    InvalidateSnapshot(home);
                    if (op.Blocks is { } blocks)
                        home.Extensions?.Restore(blocks);
                }
                _db.Assets.Add(rec);
                IndexAdd(rec);
                MarkMaterialized(rec);
                _leafCache[rec] = BrowserNode.ForAsset(rec, ToggleNode);
                break;

            case "mov" when op.Record is { } moved:
                if (op.From is null)
                {
                    // Brought back from the journal with no old copy anywhere: discarding removes it.
                    var into = GdtOf(moved);
                    into?.Assets.Remove(moved);
                    _db.Assets.Remove(moved);
                    _leafCache.Remove(moved);
                    _problemsByAsset.Remove(moved);
                    _changedKeys.Remove(moved);
                    IndexRemove(moved);
                    if (into is not null)
                        InvalidateSnapshot(into);
                    break;
                }
                UndoMoveOp(op);
                foreach (var tab in OpenTabs.Where(t => t.Record == moved))
                    tab.NotifyMoved();
                break;

            case "flat" when op.Record is { } flattened:
                UndoUnderiveOp(op);
                RebuildTab(flattened);
                break;
        }
    }
}
