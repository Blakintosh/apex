using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using Apex.Editor.Models;
using Apex.Editor.Services.Gdt;
using Apex.Editor.Services.Session;

namespace Apex.Editor.ViewModels;

/// <summary>
/// Where assets live and what they inherit (APE's Copy/Cut/Paste Asset, Move Asset, Derive and Underive): copies and
/// moves between GDTs, deriving a new asset from another and flattening a derived one. Everything happens in the
/// session (journaled like any structural edit) and reaches the files only through Save. Each action is one undo step
/// on Ctrl+Z, until the save that writes it.
/// </summary>
public sealed partial class MainViewModel
{
    // ══ Placement undo ═══════════════════════════════════════════════════════
    // Value edits live in each record's history; a paste, move, derive, underive or delete touches the session's
    // structure (and often several records), so it is a step of its own here. Ctrl+Z takes whichever is newer: the active tab's
    // last value edit or the last placement step.

    private sealed class PlacementStep
    {
        public required string DoneText { get; init; }
        public required string UndoneText { get; init; }
        public required Action Undo { get; init; }
        public required Action Redo { get; init; }
        public DateTime At { get; set; }

        /// <summary>
        /// Why undoing this step can't do anything any more (every asset it placed is gone, or a deleted asset's name was
        /// taken since), or null. Such a step leaves the history instead of blocking every step under it.
        /// </summary>
        public Func<string?>? Gone { get; init; }
    }

    private readonly List<PlacementStep> _placementUndo = new();
    private readonly List<PlacementStep> _placementRedo = new();

    /// <summary>A new value edit: as with any undo history, what was undone before it can't be redone after it.</summary>
    public void OnNewValueEdit()
    {
        if (_placementRedo.Count == 0)
            return;
        _placementRedo.Clear();
        NotifyUndoRedo();
    }

    /// <summary>
    /// A paste, move, derive, underive or delete changes what a running save is committing, so it waits for the save
    /// (the status line says so). Saves take well under a second.
    /// </summary>
    private bool PlacementBlocked()
    {
        if (!IsSaveRunning)
            return false;
        Status = "Wait for the save to finish, then try again.";
        return true;
    }

    private void PushPlacement(PlacementStep step)
    {
        step.At = DateTime.UtcNow;
        _placementUndo.Add(step);
        _placementRedo.Clear();
        NotifyUndoRedo();
    }

    /// <summary>Saved or discarded: there is no placement left to take back.</summary>
    private void ClearStructuralHistory()
    {
        if (_placementUndo.Count == 0 && _placementRedo.Count == 0)
            return;
        _placementUndo.Clear();
        _placementRedo.Clear();
        NotifyUndoRedo();
    }

    /// <summary>Undoes or redoes the newest placement step when it is newer than the active tab's value step.</summary>
    private bool TryStepPlacement(bool undo)
    {
        var stack = undo ? _placementUndo : _placementRedo;
        if (stack.Count == 0)
            return false;
        var step = stack[^1];
        if (undo && ActiveTab?.Record.History.NextUndo is { } valueStep && valueStep.At > step.At)
            return false;
        if (!undo && ActiveTab is { CanRedo: true } t && t.Record.History.LastUndoAt > step.At)
            return false;
        if (PlacementBlocked())
            return true;
        stack.RemoveAt(stack.Count - 1);
        if (undo && step.Gone?.Invoke() is { } gone)
        {
            Status = gone;
            NotifyUndoRedo();
            return true;
        }
        if (undo)
            step.Undo();
        else
            step.Redo();
        step.At = DateTime.UtcNow;
        (undo ? _placementRedo : _placementUndo).Add(step);
        Status = undo ? step.UndoneText : step.DoneText;
        NotifyUndoRedo();
        return true;
    }

    private bool HasPlacementUndo => _placementUndo.Count > 0;
    private bool HasPlacementRedo => _placementRedo.Count > 0;

    /// <summary>The ops left the session: count and journal follow (the journal is rewritten, it can't un-append).</summary>
    private void ForgetOps(IReadOnlyCollection<SessionOp> ops)
    {
        foreach (var op in ops)
            if (_sessionOps.Remove(op))
                _structuralEdits--;
        _journal?.Rewrite(BuildCompactJournal());
        UpdateSessionCount();
    }

    /// <summary>Journals several structural edits at once: one flush, one append.</summary>
    private void JournalOps(IReadOnlyCollection<SessionOp> ops)
    {
        if (ops.Count == 0)
            return;
        _sessionOps.AddRange(ops);
        _structuralEdits += ops.Count;
        if (_journal is not null)
        {
            FlushJournalNow();
            _journal.Append(ops.Select(o => o.Entry).ToArray());
            MarkTabsDirty();
        }
        UpdateSessionCount();
    }

    /// <summary>
    /// A move of <paramref name="rec"/> left the session (undone, or cancelled by moving back): the ops after it that
    /// name the record by its GDT (rename, underive) happened, as far as the journal is concerned, in
    /// <paramref name="gdt"/>. Returns what to put back if the move returns.
    /// </summary>
    private List<(JournalEntry Entry, string? Gdt)> Relocate(AssetRecord rec, SessionOp move, string gdt)
    {
        var changed = new List<(JournalEntry, string?)>();
        var at = _sessionOps.IndexOf(move);
        if (at < 0)
            return changed;
        for (var i = at + 1; i < _sessionOps.Count; i++)
        {
            var op = _sessionOps[i];
            if (op.Record != rec || op.Entry.Op is "mov" or "add" || string.Equals(op.Entry.Gdt, gdt, StringComparison.OrdinalIgnoreCase))
                continue;
            changed.Add((op.Entry, op.Entry.Gdt));
            op.Entry.Gdt = gdt;
        }
        return changed;
    }

    // ══ Records in and out of GDTs ═══════════════════════════════════════════

    private void AttachRecord(AssetRecord rec, GdtFile gdt)
    {
        InsertByName(gdt.Assets, rec);
        _db.Assets.Add(rec);
        IndexAdd(rec);
        MarkMaterialized(rec);
        InvalidateSnapshot(gdt);
        _leafCache[rec] = BrowserNode.ForAsset(rec, ToggleNode);
        UpdateProblemEntry(rec);
    }

    private void DetachRecord(AssetRecord rec)
    {
        foreach (var tab in OpenTabs.Where(t => t.Record == rec).ToList())
            CloseTab(tab);
        var gdt = GdtOf(rec);
        gdt?.Assets.Remove(rec);
        _db.Assets.Remove(rec);
        _leafCache.Remove(rec);
        _problemsByAsset.Remove(rec);
        _changedKeys.Remove(rec);
        IndexRemove(rec);
        if (gdt is not null)
            InvalidateSnapshot(gdt);
    }

    public GdtFile? GdtOf(AssetRecord rec) => _db.Gdts.FirstOrDefault(g => g.Name == rec.GdtName);

    private GdtFile[] GdtsOf(IEnumerable<AssetRecord> records) =>
        records.Select(GdtOf).OfType<GdtFile>().Distinct().ToArray();

    /// <summary>True while the record is still in the session (not deleted, not dropped by a reload).</summary>
    private bool IsLive(AssetRecord rec) => FindInGdt(rec.GdtName, rec.Name) == rec;

    /// <summary>
    /// A free name for a copy, as Duplicate names it: name_copy, name_copy2, … A name only extension data still has (its
    /// asset renamed or deleted outside Apex) isn't free either: the copy would read that data as its own.
    /// </summary>
    private string FreeName(string baseName)
    {
        var name = baseName;
        var n = 2;
        while (FindAsset("", name) is not null || OrphanBlockFile(name) is not null)
            name = $"{baseName}{n++}";
        return name;
    }

    /// <summary>The first <c>.gdtx</c> holding extension data under <paramref name="name"/> (which no asset has), or null.</summary>
    private string? OrphanBlockFile(string name)
    {
        foreach (var gdt in _db.Gdts)
            if (gdt.Extensions is { } x && x.BlocksOf(name).Count > 0)
                return System.IO.Path.GetFileName(gdt.Name) + "x";
        return null;
    }

    /// <summary>The GDT that "Duplicate to…", "Move to…", paste and New asset offer first.</summary>
    private void NoteTargetGdt(GdtFile? gdt)
    {
        if (gdt is not null)
            _lastNewGdt = gdt;
    }

    /// <summary>Rebuilds an open tab of <paramref name="rec"/> in place (its parent changed: provenance is rebuilt).</summary>
    private void RebuildTab(AssetRecord rec)
    {
        if (OpenTabs.FirstOrDefault(t => t.Record == rec) is not { } old)
            return;
        var wasActive = ActiveTab == old;
        var tab = new AssetEditorViewModel(rec, NavigateToRef, CloseTab, OnEditorEdited, FindAsset, RenameTab, GdtOf)
        {
            Owner = this,
            IsPreview = old.IsPreview,
            PreviewPane = PreviewPaneViewModel.Create(rec, _env, FindAsset, NavigateToRef),
        };
        tab.View = old.View == EditorView.Overrides && !tab.HasParent ? EditorView.All : old.View;
        OpenTabs.Insert(OpenTabs.IndexOf(old), tab);
        if (wasActive)
            ActiveTab = tab;
        OpenTabs.Remove(old);
        ForgetTab(old);
        old.PreviewPane?.Dispose();
    }

    /// <summary>Edited assets (by name) whose derived tabs haven't caught up yet, with the keys edited (null: any).</summary>
    private readonly Dictionary<string, HashSet<string>?> _inheritPending = new(StringComparer.OrdinalIgnoreCase);
    private bool _inheritPosted;

    /// <summary>
    /// Open tabs of assets derived from an edited one show its new values where they inherit them. Batched to once per
    /// dispatcher turn, after the edit has shown, so typing in a parent never pays for its children's rows.
    /// </summary>
    private void RefreshInheritors(IEnumerable<AssetRecord> edited, IReadOnlyList<string>? keys = null)
    {
        var derivedOpen = false;
        foreach (var tab in OpenTabs)
            if (tab.Record.Parent is not null)
            {
                derivedOpen = true;
                break;
            }
        if (!derivedOpen)
            return;
        foreach (var r in edited)
        {
            if (keys is null)
                _inheritPending[r.Name] = null;
            else if (!_inheritPending.TryGetValue(r.Name, out var set))
                _inheritPending[r.Name] = new HashSet<string>(keys, StringComparer.OrdinalIgnoreCase);
            else
                set?.UnionWith(keys);
        }
        if (_inheritPosted)
            return;
        _inheritPosted = true;
        Dispatcher.UIThread.Post(FlushInheritors, DispatcherPriority.Background);
    }

    private void FlushInheritors()
    {
        _inheritPosted = false;
        if (_inheritPending.Count == 0)
            return;
        foreach (var tab in OpenTabs)
        {
            HashSet<string>? keys = null;
            var hit = false;
            var all = false;
            // The tab's parent chain against the pending names: O(depth) per tab, however many assets were edited.
            var cur = tab.Record;
            for (var depth = 0; depth < 32 && cur.Parent is { } p; depth++)
            {
                if (_inheritPending.TryGetValue(p, out var k))
                {
                    hit = true;
                    if (k is null)
                        all = true;
                    else
                        (keys ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase)).UnionWith(k);
                }
                if (FindAsset(tab.Record.Type, p) is not { } parent || parent == tab.Record)
                    break;
                cur = parent;
            }
            if (hit)
                tab.RefreshInherited(all ? null : keys);
        }
        _inheritPending.Clear();
    }

    private void RefreshPlacement(IEnumerable<GdtFile> gdts, IEnumerable<string> names)
    {
        RefreshAfterAssetChange(gdts.Where(g => _db.Gdts.Contains(g)).Distinct().ToArray(), names.ToArray());
        SumProblems();
        if (ActiveTab is null)
            RefreshStartPage();
    }

    // ══ Clipboard: Copy, Cut, Paste ══════════════════════════════════════════

    private List<AssetRecord> _assetClipboard = new();
    private bool _clipboardIsCut;
    private string _clipText = "";
    private Task _clipWrite = Task.CompletedTask;

    /// <summary>True when Paste has something to place.</summary>
    public bool CanPasteAssets => _assetClipboard.Any(IsLive);

    /// <summary>Ctrl+C on asset rows: Paste makes copies of them. Their names also go to the system clipboard.</summary>
    public void CopyAssets(IReadOnlyList<AssetRecord> assets) => Clip(assets, cut: false);

    /// <summary>Ctrl+X on asset rows: Paste moves them (their names stay, so every reference stays valid).</summary>
    public void CutAssets(IReadOnlyList<AssetRecord> assets) => Clip(assets, cut: true);

    private void Clip(IReadOnlyList<AssetRecord> assets, bool cut)
    {
        if (assets.Count == 0)
            return;
        _assetClipboard = assets.ToList();
        _clipboardIsCut = cut;
        _clipText = string.Join('\n', assets.Select(a => a.Name));
        _clipWrite = Shell?.CopyTextAsync(_clipText) ?? Task.CompletedTask;
        var what = assets.Count == 1 ? assets[0].Name : $"{assets.Count:N0} assets";
        var paste = Commands.CommandCatalog.Get(Commands.CommandCatalog.PasteAssets).GestureText;
        Status = cut
            ? $"Cut {what}. Select a GDT and press {paste} to move {(assets.Count == 1 ? "it" : "them")} there."
            : $"Copied {what}. Select a GDT and press {paste} to paste {(assets.Count == 1 ? "a copy" : "copies")}.";
    }

    /// <summary>
    /// Ctrl+V: cut assets move into <paramref name="gdt"/>, copied ones are copied into it. Only while the system
    /// clipboard still holds what Apex put there: copying anything else since means the assets aren't what's pasted.
    /// </summary>
    public async Task PasteAssetsAsync(GdtFile gdt)
    {
        if (_assetClipboard.Count == 0)
            return;
        if (Shell is { } shell)
        {
            // A Ctrl+V right after Ctrl+C reads what Apex wrote, not what was there before.
            await _clipWrite;
            var (read, text) = await shell.GetClipboardTextAsync();
            if (!read)
            {
                Status = "Couldn't read the clipboard (another program may have it open). Try again.";
                return;
            }
            if (Normalize(text) != Normalize(_clipText))
            {
                _assetClipboard = new List<AssetRecord>();
                Status = "The clipboard holds something else now. Copy or cut the assets again to paste them.";
                return;
            }
        }
        var assets = _assetClipboard.Where(IsLive).ToList();
        if (assets.Count == 0 || PlacementBlocked())
            return;
        if (_clipboardIsCut)
        {
            if (MoveAssetsInto(assets, gdt))
                _assetClipboard = new List<AssetRecord>();
        }
        else
            CopyAssetsInto(assets, gdt);

        static string Normalize(string? text) => (text ?? "").Replace("\r\n", "\n").Trim();
    }

    /// <summary>The palette's Paste: there is no row to paste onto, so it asks where.</summary>
    private void PasteFromPalette()
    {
        var assets = _assetClipboard.Where(IsLive).ToList();
        if (assets.Count == 0)
            return;
        var what = assets.Count == 1 ? assets[0].Name : $"{assets.Count:N0} assets";
        OpenGdtPicker(new GdtPick($"Paste {what} into", _clipboardIsCut ? "move" : "paste", null, gdt => _ = PasteAssetsAsync(gdt)));
    }

    // ══ Duplicate to… / Move to… ═════════════════════════════════════════════

    public void DuplicateTo(IReadOnlyList<AssetRecord> assets)
    {
        if (assets.Count == 0)
            return;
        var what = assets.Count == 1 ? assets[0].Name : $"{assets.Count:N0} assets";
        OpenGdtPicker(new GdtPick($"Duplicate {what} to", "duplicate", null, gdt => CopyAssetsInto(assets, gdt)));
    }

    public void MoveTo(IReadOnlyList<AssetRecord> assets)
    {
        if (assets.Count == 0)
            return;
        var what = assets.Count == 1 ? assets[0].Name : $"{assets.Count:N0} assets";
        var from = GdtsOf(assets);
        OpenGdtPicker(new GdtPick($"Move {what} to", "move", from.Length == 1 ? from[0] : null, gdt => MoveAssetsInto(assets, gdt)));
    }

    /// <summary>Copies of <paramref name="sources"/> in <paramref name="gdt"/>, named as Duplicate names them. One undo step.</summary>
    public void CopyAssetsInto(IReadOnlyList<AssetRecord> sources, GdtFile gdt)
    {
        if (PlacementBlocked())
            return;
        var clones = new List<(AssetRecord Clone, string From)>();
        var extensions = new Dictionary<AssetRecord, Dictionary<string, Dictionary<string, string>>>();
        foreach (var source in sources.Where(IsLive))
        {
            // A derived asset stays derived: its parent and only its own values come along.
            var clone = new AssetRecord { Name = FreeName(source.Name + "_copy"), Type = source.Type, GdtName = gdt.Name, Parent = source.Parent };
            foreach (var (key, value) in source.ScanProperties)
                clone.Properties[key] = value;
            // The copy starts with the source's extension data, in its own GDT's .gdtx (there before it is counted).
            if (ExtensionSidecar.ValuesOf(GdtOf(source), source.Name) is { } extension)
            {
                extensions[clone] = extension;
                ExtensionSidecar.Of(gdt).Add(clone.Name, extension);
            }
            AttachRecord(clone, gdt);
            clones.Add((clone, source.Name));
        }
        if (clones.Count == 0)
            return;

        var ops = new List<SessionOp>();
        void Journal()
        {
            ops.Clear();
            foreach (var (clone, from) in clones.Where(c => IsLive(c.Clone)))
            {
                var entry = AddEntry(clone, from);
                entry.ExtensionProps = extensions.GetValueOrDefault(clone);
                ops.Add(new SessionOp { Entry = entry, Record = clone, Gdt = gdt });
            }
            JournalOps(ops);
        }
        Journal();
        NoteTargetGdt(gdt);
        var names = clones.Select(c => c.Clone.Name).ToList();
        RefreshPlacement(new[] { gdt }, names);
        OpenSoon(clones[0].Clone);
        var short_ = ShortGdt(gdt.Name);
        var done = clones.Count == 1
            ? $"Copied {clones[0].From} into {short_} as {names[0]}"
            : $"Copied {clones.Count:N0} assets into {short_}";
        Status = done;
        PushPlacement(new PlacementStep
        {
            DoneText = done,
            UndoneText = clones.Count == 1 ? $"Removed the copy {names[0]}" : $"Removed the {clones.Count:N0} copies",
            Gone = () => clones.Any(c => IsLive(c.Clone)) ? null
                : clones.Count == 1 ? $"Nothing to take back: the copy {names[0]} was already deleted." : "Nothing to take back: the copies were already deleted.",
            Undo = () =>
            {
                // A copy deleted since stays deleted (its delete is still in the session).
                var live = clones.Where(c => IsLive(c.Clone)).Select(c => c.Clone).ToList();
                foreach (var clone in Enumerable.Reverse(live))
                {
                    DetachRecord(clone);
                    gdt.Extensions?.Detach(clone.Name);
                }
                ForgetOps(ops.Where(o => live.Contains(o.Record!)).ToList());
                RefreshPlacement(new[] { gdt }, names);
            },
            Redo = () =>
            {
                if (!_db.Gdts.Contains(gdt))
                    return;
                foreach (var (clone, _) in clones)
                    if (FindAsset("", clone.Name) is null)
                    {
                        if (extensions.GetValueOrDefault(clone) is { } extension)
                            ExtensionSidecar.Of(gdt).Add(clone.Name, extension);
                        AttachRecord(clone, gdt);
                    }
                Journal();
                RefreshPlacement(new[] { gdt }, names);
                OpenSoon(clones[0].Clone);
            },
        });
    }

    /// <summary>
    /// Opens a pasted asset as the preview tab once the paste has shown: building its editor is an open's cost (up to
    /// ~200 ms), not the paste's.
    /// </summary>
    private void OpenSoon(AssetRecord rec) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (IsLive(rec))
                OpenAsset(rec, preview: true);
        }, DispatcherPriority.Background);

    /// <summary>
    /// Moves <paramref name="records"/> into <paramref name="to"/> under the same names, so every reference to them stays
    /// valid. On save the asset is written in its new GDT and removed from its old one. One undo step. False when there
    /// was nothing to move.
    /// </summary>
    public bool MoveAssetsInto(IReadOnlyList<AssetRecord> records, GdtFile to)
    {
        if (PlacementBlocked())
            return false;
        var moving = records.Where(r => IsLive(r) && r.GdtName != to.Name).ToList();
        if (moving.Count == 0)
        {
            Status = records.Count == 1 ? $"{records[0].Name} is already in {ShortGdt(to.Name)}" : $"They're already in {ShortGdt(to.Name)}";
            return false;
        }
        // A GDT can't hold two assets by one name (APE and the linker would take one of them): refuse before anything
        // moves. Moving an asset back to the file that still holds it is not a clash; it cancels the move.
        var ghosts = MovedAwayFrom(to.Name);
        var clash = moving.FirstOrDefault(r => to.Assets.Any(a => a != r && a.Name.Equals(r.Name, StringComparison.OrdinalIgnoreCase))
                                               || ghosts?.GetValueOrDefault(r.Name) is { } g && !IsGhostOf(g, r))
                    // Two of the assets being moved share a name (from different GDTs): they can't both land there.
                    ?? moving.GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(n => n.Count() > 1)?.First();
        if (clash is not null)
        {
            Alert(moving.Count(r => r.Name.Equals(clash.Name, StringComparison.OrdinalIgnoreCase)) > 1
                ? $"Can't move these together: two of them are named {clash.Name}, and one GDT can't hold both. Rename one of them first."
                : $"Can't move {clash.Name}: {ShortGdt(to.Name)} already has an asset by that name. Rename one of them first.");
            return false;
        }
        var touched = GdtsOf(moving).Append(to).ToList();
        var undos = MoveAll(moving, to);
        NoteTargetGdt(to);
        var names = moving.Select(r => r.Name).ToList();
        RefreshPlacement(touched, names);
        foreach (var tab in OpenTabs.Where(t => moving.Contains(t.Record)))
            tab.NotifyMoved();
        var short_ = ShortGdt(to.Name);
        var done = moving.Count == 1 ? $"Moved {names[0]} to {short_}" : $"Moved {moving.Count:N0} assets to {short_}";
        Status = done;
        PushPlacement(new PlacementStep
        {
            DoneText = done,
            UndoneText = moving.Count == 1
                ? $"Moved {names[0]} back to {ShortGdt(touched[0].Name)}"
                : $"Moved {moving.Count:N0} assets back",
            Gone = () => moving.Any(IsLive) ? null
                : moving.Count == 1 ? $"Nothing to move back: {moving[0].Name} was deleted after it moved."
                : "Nothing to move back: those assets were deleted after they moved.",
            Undo = () =>
            {
                // An asset deleted since its move stays deleted where it is (its delete is still in the session).
                for (var i = undos.Count - 1; i >= 0; i--)
                    if (IsLive(moving[i]))
                        undos[i]();
                _journal?.Rewrite(BuildCompactJournal());
                UpdateSessionCount();
                RefreshPlacement(touched, names);
                foreach (var tab in OpenTabs.Where(t => moving.Contains(t.Record)))
                    tab.NotifyMoved();
            },
            Redo = () =>
            {
                undos = MoveAll(moving, to);
                RefreshPlacement(touched, names);
                foreach (var tab in OpenTabs.Where(t => moving.Contains(t.Record)))
                    tab.NotifyMoved();
            },
        });
        return true;
    }

    /// <summary>
    /// Moves each record and returns how to put each one back, one per record in the same order (a record no longer
    /// live is skipped, and its entry does nothing).
    /// </summary>
    private List<Action> MoveAll(IReadOnlyList<AssetRecord> records, GdtFile to)
    {
        // Values edited before the move are journaled under the GDT they were made in, ahead of the move.
        FlushJournalNow();
        var movesOf = new Dictionary<AssetRecord, List<SessionOp>>();
        foreach (var op in _sessionOps)
            if (op.Entry.Op == "mov" && op.Record is { } r)
            {
                if (!movesOf.TryGetValue(r, out var list))
                    movesOf[r] = list = new List<SessionOp>();
                list.Add(op);
            }
        var undos = new List<Action>(records.Count);
        var added = new List<SessionOp>();
        var rewrite = false;
        foreach (var rec in records)
        {
            if (!IsLive(rec) || rec.GdtName == to.Name)
            {
                undos.Add(() => { });
                continue;
            }
            var back = GdtOf(rec);
            // Back to the GDT whose file still holds it (A → B → A): the earlier moves cancel out, so the save leaves
            // that file alone instead of deleting the asset and appending it again.
            if (movesOf.TryGetValue(rec, out var moves) && moves[0].Ghost is { } ghost && ghost.GdtName == to.Name)
            {
                var relocated = Relocate(rec, moves[0], to.Name);
                var cancelled = moves.Select(op => (op, i: _sessionOps.IndexOf(op))).ToList();
                var carried = CarryBlocks(rec.Name, back, to, moves[0].Blocks);
                back?.Assets.Remove(rec);
                rec.GdtName = to.Name;
                rec.Disk = ghost.Disk;
                InsertByName(to.Assets, rec);
                Invalidate(back, to, rec);
                foreach (var (op, _) in cancelled)
                    _sessionOps.Remove(op);
                _structuralEdits -= cancelled.Count;
                rewrite = true;
                undos.Add(() =>
                {
                    var now = GdtOf(rec);
                    if (now is not null && back is not null)
                        moves[0].Blocks = CarryBlocks(rec.Name, now, back, carried);
                    now?.Assets.Remove(rec);
                    rec.GdtName = back?.Name ?? rec.GdtName;
                    rec.Disk = null;
                    if (back is not null)
                        InsertByName(back.Assets, rec);
                    Invalidate(now, back, rec);
                    foreach (var (op, i) in cancelled)
                        _sessionOps.Insert(Math.Min(i, _sessionOps.Count), op);
                    foreach (var (entry, gdt) in relocated)
                        entry.Gdt = gdt;
                    _structuralEdits += cancelled.Count;
                    JournalDirty(new[] { rec });
                });
                continue;
            }

            var moved = MoveOp(rec, to, entry: null);
            added.Add(moved);
            undos.Add(() =>
            {
                Relocate(rec, moved, moved.From?.Name ?? rec.GdtName);
                UndoMoveOp(moved);
                if (_sessionOps.Remove(moved))
                    _structuralEdits--;
                JournalDirty(new[] { rec });
            });
        }
        if (rewrite)
        {
            _sessionOps.AddRange(added);
            _structuralEdits += added.Count;
            _journal?.Rewrite(BuildCompactJournal());
            UpdateSessionCount();
        }
        else
            JournalOps(added);
        JournalDirty(records.Where(IsLive));
        return undos;
    }

    /// <summary>True when <paramref name="ghost"/> is the old copy of <paramref name="rec"/> (its own pending move).</summary>
    private bool IsGhostOf(AssetRecord ghost, AssetRecord rec) =>
        _sessionOps.Any(op => op.Entry.Op == "mov" && op.Ghost == ghost && op.Record == rec);

    private void Invalidate(GdtFile? a, GdtFile? b, AssetRecord rec)
    {
        if (a is not null)
            InvalidateSnapshot(a);
        if (b is not null)
            InvalidateSnapshot(b);
        _leafCache[rec] = BrowserNode.ForAsset(rec, ToggleNode);
    }

    /// <summary>
    /// Moves one record into <paramref name="to"/> (also used when a journal replays a move). The record becomes new to
    /// its GDT (a save writes all its values there); a ghost keeps its place in the old file so the save deletes it there.
    /// </summary>
    private SessionOp MoveOp(AssetRecord rec, GdtFile to, JournalEntry? entry)
    {
        var from = GdtOf(rec);
        // Everything it holds must come along, and what Apex read is what the old file's delete is checked against.
        rec.CaptureBaseline();
        MarkMaterialized(rec);
        AssetRecord? ghost = null;
        if (rec.Disk is { } disk && from is not null)
        {
            ghost = new AssetRecord { Name = disk.Name, Type = rec.Type, GdtName = from.Name, Parent = disk.Parent, Disk = disk };
            foreach (var (key, value) in rec.SessionBaseline!)
                ghost.Properties[key] = value;
            ghost.CaptureBaseline();
        }
        var fromName = from?.Name ?? rec.GdtName;
        // Its extension blocks move with it: deleted from the old GDT's .gdtx by the save, written new in the new one's.
        // A replayed move takes what it carried then (edits made before it are in it), not the old file's blocks.
        var extension = entry is null ? ExtensionSidecar.ValuesOf(from, rec.Name) : entry.ExtensionProps;
        var blocks = from?.Extensions?.Detach(rec.Name);
        if (extension is not null && entry is not null)
            ExtensionSidecar.Of(to).AddReplayed(rec.Name, extension);
        else if (extension is not null)
            ExtensionSidecar.Of(to).Add(rec.Name, extension);
        from?.Assets.Remove(rec);
        rec.GdtName = to.Name;
        rec.Disk = null;
        InsertByName(to.Assets, rec);
        Invalidate(from, to, rec);
        return new SessionOp
        {
            // The values ride along: if the old file is saved and the new one isn't, a restart still has the asset.
            Entry = entry ?? new JournalEntry
            {
                Op = "mov", Gdt = fromName, Name = rec.Name, To = to.Name, Type = rec.Type, Parent = rec.Parent,
                Props = new Dictionary<string, string>(rec.Properties, StringComparer.OrdinalIgnoreCase),
                ExtensionProps = extension,
            },
            Record = rec,
            Gdt = to,
            From = from,
            Ghost = ghost,
            Blocks = blocks,
        };
    }

    /// <summary>
    /// Takes <paramref name="asset"/>'s extension blocks from <paramref name="from"/>'s sidecar into
    /// <paramref name="to"/>'s with the values they hold now: into <paramref name="originals"/> (blocks an earlier move
    /// took out of <paramref name="to"/>, so the file keeps them in place) when given, else as new blocks. Returns the
    /// blocks taken out of <paramref name="from"/>, for going back.
    /// </summary>
    private static List<AssetRecord>? CarryBlocks(string asset, GdtFile? from, GdtFile to, List<AssetRecord>? originals)
    {
        var values = ExtensionSidecar.ValuesOf(from, asset);
        var taken = from?.Extensions?.Detach(asset);
        if (originals is { Count: > 0 })
        {
            var x = ExtensionSidecar.Of(to);
            x.Restore(originals);
            foreach (var block in originals)
            {
                var now = values?.GetValueOrDefault(block.Type);
                foreach (var key in block.Properties.Keys.ToList())
                    if (now?.ContainsKey(key) != true)
                        x.Set(block.Name, block.Type, key, null);
                foreach (var (key, value) in now ?? new Dictionary<string, string>())
                    x.Set(block.Name, block.Type, key, value);
                values?.Remove(block.Type);
            }
        }
        if (values is { Count: > 0 })
            ExtensionSidecar.Of(to).Add(asset, values);
        return taken;
    }

    /// <summary>Assets moved out of <paramref name="gdtName"/> whose old copy a save still has to delete there, by name.</summary>
    private Dictionary<string, AssetRecord>? MovedAwayFrom(string gdtName)
    {
        Dictionary<string, AssetRecord>? ghosts = null;
        foreach (var op in _sessionOps)
            if (op.Entry.Op == "mov" && op.Ghost is { Disk: { } disk } ghost && ghost.GdtName.Equals(gdtName, StringComparison.OrdinalIgnoreCase))
                (ghosts ??= new(StringComparer.OrdinalIgnoreCase))[disk.Name] = ghost;
        return ghosts;
    }

    /// <summary>Puts a moved record back in the GDT it came from, at its old place in that file.</summary>
    private void UndoMoveOp(SessionOp op)
    {
        if (op.Record is not { } rec || op.From is not { } from)
            return;
        var now = GdtOf(rec);
        if (now is not null)
            CarryBlocks(rec.Name, now, from, op.Blocks);
        now?.Assets.Remove(rec);
        rec.GdtName = from.Name;
        rec.Disk = op.Ghost?.Disk;
        if (_db.Gdts.Contains(from))
            InsertByName(from.Assets, rec);
        Invalidate(now, from, rec);
    }

    // ══ Derive / Underive ════════════════════════════════════════════════════

    /// <summary>
    /// APE's Derive: a new asset in the source's GDT with the source as its parent and no values of its own, opened on
    /// its Overrides view (empty until something is changed). One undo step.
    /// </summary>
    public void DeriveAsset(AssetRecord source)
    {
        if (!IsLive(source) || GdtOf(source) is not { } gdt || PlacementBlocked())
            return;
        if (source.Type.Equals(GdtLoader.UnknownType, StringComparison.OrdinalIgnoreCase))
        {
            Alert($"Can't derive from {source.Name}: Apex doesn't know its type, because its parent isn't in any loaded GDT.");
            return;
        }
        var rec = new AssetRecord { Name = FreeName(source.Name + "_derived"), Type = source.Type, GdtName = gdt.Name, Parent = source.Name };
        SessionOp? op = null;
        void Add()
        {
            AttachRecord(rec, gdt);
            op = new SessionOp { Entry = AddEntry(rec, from: null), Record = rec, Gdt = gdt };
            JournalOp(op);
            MarkSessionEdited();
            RefreshPlacement(new[] { gdt }, new[] { rec.Name });
            OpenAsset(rec);
            if (ActiveTab is { Record: var r } tab && r == rec)
                tab.View = EditorView.Overrides;
        }
        Add();
        var done = $"Derived {rec.Name} from {source.Name}. {Commands.CommandCatalog.Get(Commands.CommandCatalog.Rename).GestureText} renames it.";
        Status = done;
        PushPlacement(new PlacementStep
        {
            DoneText = done,
            UndoneText = $"Removed {rec.Name}",
            Gone = () => IsLive(rec) ? null : $"Nothing to take back: {rec.Name} was already deleted.",
            Undo = () =>
            {
                if (!IsLive(rec))
                    return; // deleted since: the delete stands
                DetachRecord(rec);
                if (op is not null)
                    ForgetOps(new[] { op });
                RefreshPlacement(new[] { gdt }, new[] { rec.Name });
            },
            Redo = () =>
            {
                if (_db.Gdts.Contains(gdt) && FindAsset("", rec.Name) is null)
                    Add();
            },
        });
    }

    /// <summary>
    /// The values the game sees for <paramref name="rec"/>: its own, else the nearest ancestor's, else the schema
    /// default, for every key any of them has. What Underive writes into the asset.
    /// </summary>
    public Dictionary<string, string>? EffectiveValuesOf(AssetRecord rec)
    {
        var values = new Dictionary<string, string>(rec.Properties, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { rec.Name };
        var cur = rec;
        while (cur.Parent is { } parentName)
        {
            if (!seen.Add(parentName) || FindAsset(rec.Type, parentName) is not { } parent)
                return null;
            foreach (var (key, value) in parent.ScanProperties)
                values.TryAdd(key, value);
            cur = parent;
        }
        if (SchemaRegistry.Get(rec.Type) is { } schema)
            foreach (var def in schema.Properties)
                values.TryAdd(def.Key, def.Default);
        return values;
    }

    /// <summary>
    /// APE's Underive: the asset takes every value it inherits as its own and no longer has a parent. What the game
    /// sees doesn't change. On save its header becomes a root asset's. One undo step.
    /// </summary>
    public void UnderiveAsset(AssetRecord rec)
    {
        if (!IsLive(rec) || rec.Parent is not { } parent || PlacementBlocked())
            return;
        if (rec.Type.Equals(GdtLoader.UnknownType, StringComparison.OrdinalIgnoreCase) || EffectiveValuesOf(rec) is not { } flat)
        {
            Alert($"Can't underive {rec.Name}: its parent {parent} isn't in any loaded GDT, so Apex can't read what it inherits.");
            return;
        }
        var op = UnderiveOp(rec, flat, entry: null);
        JournalOp(op);
        MarkSessionEdited();
        AfterUnderive(rec);
        var done = $"Underived {rec.Name}: it holds every value it inherited from {parent} and has no parent";
        Status = done;
        PushPlacement(new PlacementStep
        {
            DoneText = done,
            UndoneText = $"{rec.Name} derives from {parent} again",
            Gone = () => !IsLive(rec) ? $"Nothing to take back: {rec.Name} was deleted after it was underived."
                : rec.Parent is not null || !_sessionOps.Contains(op) ? $"Nothing to take back: {rec.Name} isn't underived." : null,
            Undo = () =>
            {
                // Only the underive this step made: a redo that found nothing to underive left nothing to undo.
                if (!IsLive(rec) || rec.Parent is not null || !_sessionOps.Contains(op))
                    return;
                UndoUnderiveOp(op);
                ForgetOps(new[] { op });
                AfterUnderive(rec);
            },
            Redo = () =>
            {
                // What it inherits now: the parent may have been edited since the undo.
                if (!IsLive(rec) || rec.Parent != parent || EffectiveValuesOf(rec) is not { } now)
                    return;
                op = UnderiveOp(rec, now, entry: null);
                JournalOp(op);
                MarkSessionEdited();
                AfterUnderive(rec);
            },
        });
    }

    private void AfterUnderive(AssetRecord rec)
    {
        RecountSession(new[] { rec });
        UpdateProblemEntry(rec);
        SumProblems();
        MarkRecordsChangedInTree(new[] { rec });
        RebuildTab(rec);
        RefreshPlacement(GdtsOf(new[] { rec }), new[] { rec.Name });
        NotifyUndoRedo();
    }

    /// <summary>Flattens one record (also used when a journal replays an underive).</summary>
    private SessionOp UnderiveOp(AssetRecord rec, IReadOnlyDictionary<string, string> flat, JournalEntry? entry)
    {
        rec.CaptureBaseline();
        var oldParent = rec.Parent;
        var oldValues = new Dictionary<string, string>(rec.Properties, StringComparer.OrdinalIgnoreCase);
        rec.Parent = null;
        IndexReparent(rec, oldParent);
        var props = rec.Properties;
        props.Clear();
        foreach (var (key, value) in flat)
            props[key] = value;
        rec.ValuesTouched = true;
        MarkMaterialized(rec);
        return new SessionOp
        {
            Entry = entry ?? new JournalEntry
            {
                Op = "flat", Gdt = rec.GdtName, Name = rec.Name, Parent = oldParent,
                Props = new Dictionary<string, string>(flat, StringComparer.OrdinalIgnoreCase),
            },
            Record = rec,
            OldParent = oldParent,
            OldValues = oldValues,
        };
    }

    /// <summary>Makes an underived record derived again, with exactly the values it had.</summary>
    private void UndoUnderiveOp(SessionOp op)
    {
        if (op.Record is not { } rec || op.OldParent is null)
            return;
        rec.Parent = op.OldParent;
        IndexReparent(rec, null);
        if (op.OldValues is { } old)
        {
            var props = rec.Properties;
            props.Clear();
            foreach (var (key, value) in old)
                props[key] = value;
        }
    }

    // ══ GDT picker (the palette, listing GDTs) ═══════════════════════════════

    private sealed record GdtPick(string Title, string Verb, GdtFile? Exclude, Action<GdtFile> Pick);

    private GdtPick? _gdtPickField;

    private GdtPick? _gdtPick
    {
        get => _gdtPickField;
        set
        {
            if (_gdtPickField == value)
                return;
            _gdtPickField = value;
            OnPropertyChanged(nameof(PalettePlaceholder));
        }
    }

    /// <summary>The palette box's placeholder: what to type in the mode it is in.</summary>
    public string PalettePlaceholder => _gdtPick is not null
        ? "Type a GDT name"
        : Commands.CommandCatalog.PaletteSentence;

    private void OpenGdtPicker(GdtPick pick)
    {
        OpenPalette();
        _gdtPick = pick;
        RefreshPaletteResults();
    }

    /// <summary>The GDTs matching what was typed; with nothing typed, the last GDT used first.</summary>
    private bool TryFillGdtPicker(string text)
    {
        if (_gdtPick is not { } pick)
            return false;
        var q = text.Trim();
        var gdts = _db.Gdts.Where(g => g != pick.Exclude);
        List<GdtFile> top;
        if (q.Length == 0)
        {
            var first = new[] { _lastNewGdt, ExplorerGdt, ActiveTab is { } t ? GdtOf(t.Record) : null }
                .OfType<GdtFile>().Where(g => g != pick.Exclude && _db.Gdts.Contains(g)).Distinct();
            top = first.Concat(gdts.OrderBy(g => ShortGdt(g.Name), StringComparer.OrdinalIgnoreCase)).Distinct().Take(PaletteLimit).ToList();
        }
        else
        {
            top = gdts
                .Select(g => (Gdt: g, Rank: NameRank(ShortGdt(g.Name), q)))
                .Where(x => x.Rank is not null)
                .OrderBy(x => x.Rank!.Value.Rank).ThenBy(x => x.Rank!.Value.Length).ThenBy(x => ShortGdt(x.Gdt.Name), StringComparer.OrdinalIgnoreCase)
                .Take(PaletteLimit)
                .Select(x => x.Gdt)
                .ToList();
        }
        ShowPaletteRows(top.Select(g =>
        {
            var count = g.Assets.Count == 1 ? "1 asset" : $"{g.Assets.Count:N0} assets";
            return new PaletteItemViewModel(ShortGdt(g.Name), "", "▣", () => pick.Pick(g))
            {
                Detail = g == _lastNewGdt ? $"last used · {count}" : count,
            };
        }).ToList());
        PaletteHint = top.Count == 0
            ? $"{pick.Title}… · no GDT matches · Esc dismiss"
            : $"{pick.Title}… · ↑↓ navigate · ⏎ {pick.Verb} · Esc dismiss";
        return true;
    }
}
