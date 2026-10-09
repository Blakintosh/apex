using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Apex.Editor.Models;
using Apex.Editor.Services.Gdt;
using Apex.Editor.Services.Save;

namespace Apex.Editor.ViewModels;

/// <summary>One asset in the save conflict dialog: what differs, and whether to keep the session's version or the file's.</summary>
public sealed partial class SaveConflictItem : ObservableObject
{
    public required string Gdt { get; init; }
    public required string Asset { get; init; }

    /// <summary>What happened, in one line.</summary>
    public required string What { get; init; }

    /// <summary>Per key: "damage · yours 45 · file 50 (was 40)".</summary>
    public required IReadOnlyList<string> Lines { get; init; }

    public bool CanChoose { get; init; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChooseMine), nameof(ChooseFile))]
    private bool _keepMine = true;

    // The two pills act as one choice: clicking either picks it (clicking the picked one keeps it picked).
    public bool ChooseMine
    {
        get => KeepMine;
        set
        {
            KeepMine = true;
            OnPropertyChanged();
        }
    }

    public bool ChooseFile
    {
        get => !KeepMine;
        set
        {
            KeepMine = false;
            OnPropertyChanged();
        }
    }

    internal AssetRecord? Record { get; init; }
    internal AssetConflictKind Kind { get; init; }
}

/// <summary>
/// Save all (Ctrl+S): every GDT the session changed is written in place through <see cref="GdtSaveService"/>, off the
/// UI thread, with no dialog on the common path. A dialog appears only for a conflict (the file changed on disk), and
/// a banner with Retry for a locked, read-only or failed file. After a save the written assets' change marks and
/// journal entries clear; undo still works.
/// </summary>
public sealed partial class MainViewModel
{
    private GdtSaveService? _saveService;
    private BackupStore? _backups;

    /// <summary>Records a reload refreshed (seen, not edited): their open tabs re-read them once the batch is in.</summary>
    private readonly HashSet<AssetRecord> _rebasedOnReload = new();
    private Task? _saveTask;

    /// <summary>Hash of each file as Apex last wrote it, so the watcher can tell Apex's own save from another program's.</summary>
    private readonly ConcurrentDictionary<string, UInt128> _ownWrites = new(StringComparer.OrdinalIgnoreCase);

    private BackupStore Backups => _backups ??= new BackupStore(BackupStore.DefaultRoot);
    private GdtSaveService SaveService => _saveService ??= new GdtSaveService(Backups);

    /// <summary>Set by the property editors: commits typing still in the focused field, so Ctrl+S saves what is on screen.</summary>
    public Action? CommitPendingInput { get; set; }

    /// <summary>A save is running (its "Saving…" line shows only once it passes 150 ms).</summary>
    public bool IsSaveRunning => _saveTask is { IsCompleted: false };

    /// <summary>True once a running save has passed 150 ms: the status line says "Saving…".</summary>
    [ObservableProperty]
    private bool _isSaving;

    /// <summary>The last save's timings: UI-thread planning, background I/O, UI-thread commit (ms).</summary>
    public (double PlanMs, double WriteMs, double CommitMs) LastSaveTimings { get; private set; }

    [RelayCommand]
    private void SaveActive() => _ = SaveAllAsync();

    public Task SaveAllAsync() => SaveAllAsync(skipCheck: null);

    private async Task SaveAllAsync(HashSet<AssetRecord>? skipCheck)
    {
        if (IsSaveRunning)
            return;
        CommitPendingInput?.Invoke();
        if (!_liveMode)
        {
            Status = "This is sample data (no BO3 install was found), so there are no GDT files to save to.";
            return;
        }
        var plan = PlanSave(skipCheck);
        if (plan.Problems.Count > 0)
        {
            Alert(plan.Problems[0], detail: string.Join(Environment.NewLine, plan.Problems));
            return;
        }
        if (plan.Requests.Count == 0)
        {
            Status = WithResolution(null);
            return;
        }
        var task = RunSaveAsync(plan);
        _saveTask = task;
        OnPropertyChanged(nameof(IsSaveRunning));
        try { await task; }
        finally { OnPropertyChanged(nameof(IsSaveRunning)); }
    }

    /// <summary>Perf gate: how long working out what a save would write takes on the UI thread. Nothing is written.</summary>
    public double TimeSavePlan(out int gdts)
    {
        gdts = 0;
        if (!_liveMode)
            return 0;
        var plan = PlanSave(skipCheck: null);
        gdts = plan.Requests.Count;
        return plan.PlanMs;
    }

    private sealed class SavePlan
    {
        public List<GdtSaveRequest> Requests { get; } = new();
        public List<string> Problems { get; } = new();
        public Dictionary<AssetRecord, Dictionary<string, string>> Snapshots { get; } = new();
        public double PlanMs { get; set; }
    }

    /// <summary>
    /// What to write: every record the session can have changed (created, read into memory, renamed or re-parented —
    /// the planner drops the ones that match their file), assets deleted this session, and GDTs created this session.
    /// UI thread; the requests hold copies, so edits made while the save runs don't reach it.
    /// </summary>
    private SavePlan PlanSave(HashSet<AssetRecord>? skipCheck)
    {
        var sw = Stopwatch.StartNew();
        FlushJournalNow();
        var plan = new SavePlan();
        // Only what the session can have touched: records read into memory (every value edit goes through one) and
        // the records of structural edits (new, renamed, re-parented). Never a walk of the whole corpus.
        var candidates = new HashSet<AssetRecord>(_materialized);
        candidates.UnionWith(_changedKeys.Keys);
        foreach (var op in _sessionOps)
        {
            if (op.Record is { } rec && op.Entry.Op != "del")
                candidates.Add(rec);
            if (op.Reparented is { } kids)
                candidates.UnionWith(kids);
        }
        // A record deleted (or dropped by a reload) since is no longer part of any GDT.
        candidates.RemoveWhere(r => !_byName.TryGetValue(r.Name, out var named) || !named.Contains(r));
        // A moved asset is written new in its GDT and deleted from the one it left (its ghost), so two files change.
        var deleted = _sessionOps.Where(op => op.Entry.Op == "del" && op.Record?.Disk is not null).Select(op => op.Record!)
            .Concat(_sessionOps.Where(op => op.Entry.Op == "mov" && op.Ghost?.Disk is not null).Select(op => op.Ghost!));
        var created = _sessionOps.Where(op => op.Entry.Op == "gdt" && op.Gdt is { FullPath: null }).Select(op => op.Gdt!);
        var requests = GdtSavePlanner.Plan(_db.Gdts, candidates, deleted, created, _env.Bo3Root, plan.Problems);
        foreach (var request in requests)
        {
            var edits = request.Edits.Select(e =>
                    skipCheck is not null && e.Tag is AssetRecord r && skipCheck.Contains(r) ? WithoutCheck(e) : e)
                .ToList();
            foreach (var e in edits)
                if (e.Tag is AssetRecord r && r.IsMaterialized && !e.Delete)
                    plan.Snapshots[r] = new Dictionary<string, string>(r.Properties, StringComparer.OrdinalIgnoreCase);
            plan.Requests.Add(new GdtSaveRequest
            {
                Path = request.Path, Expected = request.Expected, Edits = edits, Tag = request.Tag, Sidecar = request.Sidecar,
            });
        }
        plan.PlanMs = sw.Elapsed.TotalMilliseconds;
        return plan;

        static AssetEdit WithoutCheck(AssetEdit e) => new()
        {
            Disk = e.Disk, Delete = e.Delete, Name = e.Name, Type = e.Type, Parent = e.Parent,
            Set = e.Set, Baseline = null, Full = e.Full, Tag = e.Tag,
        };
    }

    private async Task RunSaveAsync(SavePlan plan)
    {
        // "Saving…" only once the save has taken 150 ms: a flash for a fast save is worse than nothing.
        var slow = DispatcherTimer.RunOnce(() =>
        {
            IsSaving = true;
            ShowProgress("Saving…");
        }, TimeSpan.FromMilliseconds(150));
        var sw = Stopwatch.StartNew();
        GdtSaveResult result;
        try
        {
            var service = SaveService;
            result = await Task.Run(() =>
            {
                var r = service.Save(plan.Requests);
                foreach (var f in r.Files)
                    if (f.Status == GdtSaveStatus.Saved && f.Stamp is { } s)
                        _ownWrites[f.Request.Path] = s.Hash;
                return r;
            });
        }
        catch (Exception ex)
        {
            slow.Dispose();
            IsSaving = false;
            _resolution = null;
            Alert("Apex couldn't save. Your changes are still here; nothing was written.",
                detail: $"{ex.GetType().Name}: {ex.Message}", actionLabel: "Retry", action: () => _ = SaveAllAsync());
            Status = NotSavedText;
            return;
        }
        slow.Dispose();
        IsSaving = false;
        var writeMs = sw.Elapsed.TotalMilliseconds;
        sw.Restart();
        ApplySaveResult(plan, result);
        LastSaveTimings = (plan.PlanMs, writeMs, sw.Elapsed.TotalMilliseconds);
    }

    /// <summary>Brings the session in line with what was written, then reports: a status line, a banner, or the conflict dialog.</summary>
    private void ApplySaveResult(SavePlan plan, GdtSaveResult result)
    {
        var committed = result.Files.Where(f => f.Status is GdtSaveStatus.Saved or GdtSaveStatus.Unchanged).ToList();
        var written = new HashSet<AssetRecord>();
        var deletedDone = new HashSet<AssetRecord>();
        var savedGdts = new HashSet<GdtFile>();
        // By name: a moved asset is a delete in one file and an add in another, but one asset to the user.
        var ghostOf = _sessionOps.Where(op => op.Ghost is not null && op.Record is not null).ToDictionary(op => op.Ghost!, op => op.Record!);
        var savedAssets = new HashSet<AssetRecord>();
        foreach (var f in committed)
        {
            GdtSavePlanner.Commit(f, plan.Snapshots);
            if (f.Request.Tag is ExtensionSidecar x)
            {
                // Extension data saved is its asset's change.
                foreach (var e in f.Request.Edits)
                    if (x.Owner.Assets.FirstOrDefault(a => a.Name.Equals(e.Name, StringComparison.OrdinalIgnoreCase)) is { } owner)
                    {
                        written.Add(owner);
                        if (f.Status == GdtSaveStatus.Saved)
                            savedAssets.Add(owner);
                    }
                continue;
            }
            var gdt = (GdtFile)f.Request.Tag!;
            savedGdts.Add(gdt);
            foreach (var e in f.Request.Edits)
            {
                if (e.Tag is not AssetRecord r)
                    continue;
                (e.Delete ? deletedDone : written).Add(r);
                if (f.Status == GdtSaveStatus.Saved)
                    savedAssets.Add(ghostOf.GetValueOrDefault(r) ?? r);
            }
        }

        if (committed.Count > 0)
        {
            // A moved asset's old copy is gone from its file once that file saved.
            foreach (var op in _sessionOps)
                if (op.Entry.Op == "mov" && op.Ghost is { } ghost && deletedDone.Contains(ghost))
                    ghost.Disk = null;
            // The structural edits now in the files leave the session (and so the journal).
            // An asset created or moved and then deleted, never written where it went, leaves nothing to save either.
            var ghostsOnDisk = _sessionOps.Where(m => m.Entry.Op == "mov" && m.Ghost?.Disk is not null && m.Record is not null)
                .Select(m => m.Record!).ToHashSet();
            _sessionOps.RemoveAll(op => op.Entry.Op switch
            {
                "mov" => op.Ghost?.Disk is null && (op.Record is { Disk: not null } || op.Record is { } gone && !IsLive(gone)),
                "flat" => op.Record is { Disk: { } fd } fr && fd.Parent == fr.Parent,
                "gdt" => op.Gdt is { } g && savedGdts.Contains(g),
                "add" => op.Record is { Disk: not null } || op.Record is { Disk: null } added && !IsLive(added),
                // Never written where it went, and no old copy left to delete (a moved asset's ghost may still be in a file
                // that failed to save: the delete must stay, or replaying the move brings the asset back).
                "del" => op.Record is { } r && (deletedDone.Contains(r)
                         || r.Disk is null && !ghostsOnDisk.Contains(r)),
                "ren" => op.Record is { Disk: { } d } rec && d.Name == rec.Name
                         && (op.Reparented ?? new List<AssetRecord>()).All(x => x.Disk is { } xd && xd.Parent == x.Parent),
                _ => false,
            });
            _structuralEdits = _sessionOps.Count;
            // Placement undo (paste, move, derive, underive) lasts until the save that writes it.
            ClearStructuralHistory();
            NoteTargetGdt(savedGdts.FirstOrDefault(g => g.Name == ActiveTab?.Record.GdtName) ?? savedGdts.FirstOrDefault());
            foreach (var r in written)
                MarkMaterializedIfNeeded(r);
            RecountSession(written);
            MarkRecordsChangedInTree(written);
            RefreshGroupChangeMarks();
            foreach (var tab in OpenTabs)
                if (written.Contains(tab.Record))
                    tab.RebaseChanges();
            _journal?.Rewrite(BuildCompactJournal());
            UpdateSessionCount();
            if (ActiveTab is null)
                RefreshStartPage();
        }

        // A GDT and its sidecar are one GDT to the user, saved once every file of it this save wrote is in.
        var saved = result.Files.GroupBy(f => f.Request.Tag is ExtensionSidecar x ? x.Owner : f.Request.Tag)
            .Count(g => g.Any(f => f.Status == GdtSaveStatus.Saved) && g.All(f => f.Succeeded));
        var savedFiles = result.Files.Where(f => f.Status == GdtSaveStatus.Saved).Select(f => f.Request.DisplayName).ToList();
        var conflicts = result.Files.Where(f => f.Status == GdtSaveStatus.Conflict).ToList();
        var failed = result.Files.FirstOrDefault(f => f.Status is GdtSaveStatus.Locked or GdtSaveStatus.ReadOnly
            or GdtSaveStatus.Invalid or GdtSaveStatus.Failed);
        // A new GDT whose file already exists has nothing to choose between: the banner says so.
        failed ??= conflicts.FirstOrDefault(f => !f.ChangedOnDisk && f.Conflicts.Count == 0);
        conflicts.RemoveAll(f => !f.ChangedOnDisk && f.Conflicts.Count == 0);

        // One line that agrees with whatever else this save puts up: what was written, then what wasn't.
        var savedLine = saved > 0 ? SavedText(savedAssets.Count, saved, result.Files.Where(f => f.Status == GdtSaveStatus.Saved).ToList()) : null;
        if (conflicts.Count > 0)
        {
            var names = string.Join(", ", conflicts.Select(f => f.Request.DisplayName));
            Status = WithResolution(savedLine is null ? $"Not saved: {names} changed on disk" : $"{savedLine}. Not saved: {names} changed on disk");
            OpenConflicts(conflicts);
            return;
        }
        if (failed is null)
        {
            Status = WithResolution(savedLine);
            return;
        }
        Status = WithResolution(savedLine is null ? NotSavedText : $"{savedLine}. Not saved: {failed.Request.DisplayName}");
        var retry = failed.Status is GdtSaveStatus.Locked or GdtSaveStatus.ReadOnly or GdtSaveStatus.Failed;
        // Named by file: a .gdtx saved without its GDT is the one case where "GDTs" would say less than happened.
        var text = savedFiles.Count switch
        {
            0 => failed.Message,
            1 => $"Saved {savedFiles[0]}, but not all: {failed.Message}",
            _ => $"Saved {savedFiles.Count:N0} files, but not all: {failed.Message}",
        };
        Alert(text, isError: true,
            actionLabel: retry ? "Retry" : null,
            action: retry ? () => _ = SaveAllAsync() : null,
            detail: string.Join(Environment.NewLine, new[] { failed.Detail }.Concat(failed.Problems).Where(s => !string.IsNullOrEmpty(s))));
    }

    /// <summary>
    /// The save's status line. A save that wrote extension data names its files ("Saved ar_x.gdt and ar_x.gdtx", "Saved
    /// ar_x.gdtx"): a .gdtx is a file of its own, and "1 GDT" would hide that it was the one written. Larger saves count
    /// both. With no .gdtx written it is the line it always was.
    /// </summary>
    private static string SavedText(int assets, int gdts, IReadOnlyList<GdtSaveFileResult> files)
    {
        var sidecars = files.Count(f => f.Request.Sidecar);
        if (sidecars == 0)
            return $"Saved {assets:N0} asset{(assets == 1 ? "" : "s")} in {gdts:N0} GDT{(gdts == 1 ? "" : "s")}";
        if (files.Count <= 2)
            return "Saved " + string.Join(" and ", files.OrderBy(f => f.Request.Sidecar).Select(f => Path.GetFileName(f.Request.DisplayName)));
        var plain = files.Count - sidecars;
        return $"Saved {assets:N0} asset{(assets == 1 ? "" : "s")}: {(plain > 0 ? $"{plain:N0} GDT{(plain == 1 ? "" : "s")} and " : "")}"
            + $"{sidecars:N0} .gdtx file{(sidecars == 1 ? "" : "s")}";
    }

    private const string NotSavedText = "Not saved. Your changes are still here.";
    private const string NothingToSaveText = "Nothing to save: the GDTs already hold every change.";

    /// <summary>What the conflict dialog just took from the files, for the save it runs next to report first.</summary>
    private string? _resolution;

    /// <summary>
    /// The save's line, after what the conflict dialog took ("Took the file's version of wpn_x. Saved 2 assets in 1 GDT"):
    /// a save that follows the user's choices never reads as if they hadn't been made. Null means nothing was written.
    /// </summary>
    private string WithResolution(string? saveLine)
    {
        var taken = _resolution;
        _resolution = null;
        if (taken is null)
            return saveLine ?? NothingToSaveText;
        return saveLine is null ? $"{taken}. Nothing else to save." : $"{taken}. {saveLine}";
    }

    private void MarkMaterializedIfNeeded(AssetRecord r)
    {
        if (r.IsMaterialized)
            MarkMaterialized(r);
    }

    /// <summary>GDT and type rows show the amber dot while any asset under them still has unsaved changes.</summary>
    private void RefreshGroupChangeMarks()
    {
        var changedGdts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var changedTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var typesByGdt = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in _changedKeys.Keys)
        {
            changedGdts.Add(r.GdtName);
            changedTypes.Add(r.Type);
            if (!typesByGdt.TryGetValue(r.GdtName, out var t))
                typesByGdt[r.GdtName] = t = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            t.Add(r.Type);
        }
        foreach (var root in _roots)
        {
            if (root.Gdt is { } gdt)
            {
                root.HasChanges = changedGdts.Contains(gdt.Name);
                var types = typesByGdt.GetValueOrDefault(gdt.Name);
                foreach (var child in root.Children)
                    if (child.GroupType is { } ct)
                        child.HasChanges = types?.Contains(ct) == true;
            }
            else if (root.GroupType is { } t)
                root.HasChanges = changedTypes.Contains(t);
        }
    }

    // ── Conflict dialog ─────────────────────────────────────────────────────

    [ObservableProperty]
    private bool _isConflictOpen;

    [ObservableProperty]
    private string _conflictSummary = "";

    [ObservableProperty]
    private bool _canResolveConflicts;

    public ObservableCollection<SaveConflictItem> ConflictItems { get; } = new();

    private List<GdtSaveFileResult> _conflictFiles = new();

    private void OpenConflicts(List<GdtSaveFileResult> files)
    {
        _conflictFiles = files;
        ConflictItems.Clear();
        foreach (var f in files)
        {
            var gdtName = f.Request.DisplayName;
            foreach (var c in f.Conflicts)
            {
                var record = c.Edit.Tag as AssetRecord;
                var (what, canChoose) = c.Kind switch
                {
                    AssetConflictKind.ValuesChanged => ("Changed on disk too", true),
                    AssetConflictKind.MissingOnDisk => ("No longer in the file", record?.IsMaterialized == true),
                    AssetConflictKind.ChangedBeforeDelete => ("You deleted it; it was changed on disk", true),
                    AssetConflictKind.NameTaken => ($"The file already has an asset named {c.Edit.Name}. Rename yours, then save.", false),
                    _ => ("The file now has several assets with this name. Fix the file, then save.", false),
                };
                var lines = c.Keys.Select(k => c.Kind == AssetConflictKind.ChangedBeforeDelete
                    ? $"{k.Key} · file {Show(k.OnDisk)} (was {Show(k.Was)})"
                    : $"{k.Key} · yours {Show(k.Mine)} · file {Show(k.OnDisk)} (was {Show(k.Was)})").ToList();
                ConflictItems.Add(new SaveConflictItem
                {
                    Gdt = gdtName, Asset = c.Asset, What = what, Lines = lines, CanChoose = canChoose, Record = record, Kind = c.Kind,
                });
            }
        }
        var names = string.Join(", ", files.Select(f => f.Request.DisplayName));
        ConflictSummary = ConflictItems.Count == 0
            ? files.All(f => f.ChangedOnDisk)
                ? $"{names} changed on disk since Apex read {(files.Count == 1 ? "it" : "them")}. None of your changes overlap, so saving keeps both."
                : files[0].Message
            : $"{names} changed on disk since Apex read {(files.Count == 1 ? "it" : "them")}. For each asset below, keep your version or take the file's.";
        CanResolveConflicts = ConflictItems.All(i => i.CanChoose) && files.All(f => f.ChangedOnDisk || f.Conflicts.Count > 0);
        IsConflictOpen = true;

        static string Show(string? v) => v is null ? "(unset)" : v.Length == 0 ? "(empty)" : v;
    }

    [RelayCommand]
    private void CancelConflicts()
    {
        IsConflictOpen = false;
        ConflictItems.Clear();
        _conflictFiles.Clear();
        Status = NotSavedText;
    }

    /// <summary>
    /// Applies the choices and saves again: each GDT that changed on disk is re-read first (so the session points at
    /// the file as it is now), assets taking the file's version drop the session's changes, and the rest are written
    /// over the file's values.
    /// </summary>
    [RelayCommand]
    private void ResolveConflicts()
    {
        if (!CanResolveConflicts)
            return;
        var keepMine = new HashSet<AssetRecord>();
        var tookFile = new List<string>();
        var undeleted = new List<string>();
        foreach (var item in ConflictItems)
        {
            if (item.Record is not { } r)
                continue;
            if (item.KeepMine)
            {
                keepMine.Add(r);
                if (item.Kind == AssetConflictKind.MissingOnDisk)
                    r.Disk = null; // written back as a new asset
            }
            else if (item.Kind == AssetConflictKind.ChangedBeforeDelete)
            {
                UndoDelete(r);
                undeleted.Add(item.Asset);
            }
            else
                tookFile.Add(item.Asset);
        }
        // Said by the save that follows, before what it wrote: those assets are the file's now, by the user's choice.
        var said = new List<string>();
        if (tookFile.Count > 0)
            said.Add($"Took the file's version of {Names(tookFile)}");
        if (undeleted.Count > 0)
            said.Add($"Kept {Names(undeleted)} as the file has {(undeleted.Count == 1 ? "it" : "them")} (not deleted)");
        _resolution = said.Count > 0 ? string.Join("; ", said) : null;
        foreach (var f in _conflictFiles.Where(f => f.ChangedOnDisk))
            if (f.Request.Tag is GdtFile gdt)
                ReloadFromDisk(gdt);
            else if (f.Request.Tag is ExtensionSidecar x)
                ReloadSidecar(x.Owner, f.Request.Path);
        foreach (var item in ConflictItems)
            if (!item.KeepMine && item.Record is { } r && item.Kind == AssetConflictKind.ValuesChanged)
                TakeFileVersion(r);
        IsConflictOpen = false;
        ConflictItems.Clear();
        _conflictFiles.Clear();
        _ = SaveAllAsync(keepMine);
    }

    private static string Names(IReadOnlyList<string> names) =>
        names.Count <= 2 ? string.Join(" and ", names) : $"{names[0]}, {names[1]} and {names.Count - 2:N0} more";

    /// <summary>Re-reads a GDT as the watcher would (the session's edits and renames stay).</summary>
    private void ReloadFromDisk(GdtFile gdt)
    {
        if (gdt.FullPath is not { } path || !File.Exists(path))
            return;
        var entries = GdtIndexer.IndexFile(path, out var stamp);
        ApplyDiskBatch(new() { (gdt.Name, path, entries, stamp) }, new List<string>());
    }

    /// <summary>Re-reads a GDT's sidecar as the watcher would (blocks with edits keep them); gone from disk, it is dropped.</summary>
    private static void ReloadSidecar(GdtFile owner, string path)
    {
        if (!File.Exists(path))
        {
            GdtLoader.RemoveSidecar(owner);
            return;
        }
        var entries = GdtIndexer.IndexFile(path, out var stamp);
        if (stamp is not null)
            GdtLoader.ApplySidecar(owner, path, entries, stamp);
    }

    /// <summary>The asset drops this session's value changes and reads as its file does now.</summary>
    private void TakeFileVersion(AssetRecord r)
    {
        if (r.Source?.Materialize() is not { } now)
            return;
        var props = r.Properties;
        props.Clear();
        foreach (var (k, v) in now)
            props[k] = v;
        r.RebaseAfterSave(now);
        // A sidecar block isn't an asset: no change count, tree mark or tab of its own.
        if (r.GdtName.EndsWith(ExtensionSidecar.Extension, StringComparison.OrdinalIgnoreCase))
            return;
        RecountSession(new[] { r });
        MarkRecordsChangedInTree(new[] { r });
        foreach (var tab in OpenTabs.Where(t => t.Record == r))
            tab.RebaseChanges();
    }

    private void UndoDelete(AssetRecord r)
    {
        var index = _sessionOps.FindIndex(op => op.Entry.Op == "del" && op.Record == r);
        if (index < 0)
            return;
        ReverseOp(_sessionOps[index]);
        _sessionOps.RemoveAt(index);
        _structuralEdits = _sessionOps.Count;
        UpdateSessionCount();
    }

    // ── Restore the previous version of a GDT ───────────────────────────────

    /// <summary>The active asset's GDT when it is a file on disk (whether it has a backup is checked when the command runs).</summary>
    private GdtFile? RestorableGdt()
    {
        if (!_liveMode || ActiveTab is not { } tab)
            return null;
        var gdt = _db.Gdts.FirstOrDefault(g => g.Name == tab.Record.GdtName);
        return gdt is { FullPath: not null, Stamp: not null } ? gdt : null;
    }

    private bool CanRestorePrevious() => RestorableGdt() is not null && !IsSaveRunning;

    /// <summary>
    /// Puts the active asset's GDT back to the version before its last save (the newest backup). It goes through the
    /// same save sequence, so the version it replaces is backed up too: restoring again goes forward.
    /// </summary>
    private async void RestorePrevious()
    {
        if (RestorableGdt() is not { } gdt || gdt.FullPath is not { } path)
            return;
        var name = Path.GetFileName(path);
        if (gdt.Assets.Any(a => a.HasSessionEdits || a.Disk is null || a.Name != a.Disk.Value.Name)
            || _sessionOps.Any(op => op.Entry.Gdt is { } g && g.Equals(gdt.Name, StringComparison.OrdinalIgnoreCase)))
        {
            Alert($"{name} has unsaved changes. Save or discard them before restoring its previous version.", isError: false);
            return;
        }
        if (Backups.List(path) is not { Count: > 0 } backups)
        {
            Alert($"There is no earlier version of {name} to go back to: Apex keeps one each time it saves the file.", isError: false);
            return;
        }
        var backup = backups[0];
        var when = File.GetLastWriteTime(backup);
        var request = new GdtSaveRequest { Path = path, Expected = gdt.Stamp, Replacement = File.ReadAllBytes(backup), Tag = gdt };
        var service = SaveService;
        var task = Task.Run(() => service.Save(new[] { request }));
        _saveTask = task;
        OnPropertyChanged(nameof(IsSaveRunning));
        GdtSaveResult result;
        try { result = await task; }
        catch (Exception ex)
        {
            Alert($"Apex couldn't restore {name}.", detail: ex.Message);
            return;
        }
        finally { OnPropertyChanged(nameof(IsSaveRunning)); }
        var file = result.Files[0];
        if (file.Status != GdtSaveStatus.Saved)
        {
            Alert(file.Message, detail: file.Detail);
            return;
        }
        ReloadFromDisk(gdt);
        foreach (var tab in OpenTabs.Where(t => t.Record.GdtName == gdt.Name))
            tab.SyncFromRecord();
        Status = $"Restored {name} to how it was before the save at {when:HH:mm}. The version it replaced is backed up too.";
    }
}
