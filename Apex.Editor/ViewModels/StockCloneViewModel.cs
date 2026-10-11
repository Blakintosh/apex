using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Apex.Editor.Models;
using Apex.Editor.Services.Stock;

namespace Apex.Editor.ViewModels;

/// <summary>One row of the clone dialog's dependency tree: a group of assets, or one.</summary>
public sealed partial class CloneDepRow : ObservableObject
{
    private readonly Action<CloneDepRow>? _toggle;

    public CloneDepRow(int level, string kind, string name, string meta, string zone, StockClass cls, string actionLabel, string tip,
        bool isGroup, Action<CloneDepRow>? toggle)
    {
        Level = level;
        Kind = kind;
        Name = name;
        Meta = meta;
        Zone = zone;
        Class = cls;
        ActionLabel = actionLabel;
        Tip = tip;
        IsGroup = isGroup;
        _toggle = toggle;
    }

    public int Level { get; }
    public string Kind { get; }
    public string Name { get; }
    public string Meta { get; }
    public string Zone { get; }
    public StockClass Class { get; }
    public string ActionLabel { get; }
    public string Tip { get; }
    public bool IsGroup { get; }

    public List<CloneDepRow> Children { get; } = new();

    public Thickness Indent => new(Level * 20, 0, 0, 0);
    public IBrush DotBrush => TypeStyles.Brush(Kind);
    public string Chevron => !IsGroup ? "" : IsExpanded ? "▾" : "▸";

    public bool IsCopy => Class == StockClass.Copy;
    public bool IsShip => Class is StockClass.UseShipped or StockClass.Local;
    public bool IsRebuilt => Class == StockClass.Rebuilt;
    public bool IsOdd => Class is StockClass.Unsupported or StockClass.Missing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Chevron))]
    private bool _isExpanded;

    [RelayCommand]
    private void Toggle() => _toggle?.Invoke(this);
}

/// <summary>A GDT a copy can be added to.</summary>
public sealed record CloneGdtChoice(string Label, string? FullPath, string Detail = "");

/// <summary>Where the dialog is.</summary>
public enum CloneStage { Planning, Ready, Running, Done, Failed, Undone }

/// <summary>
/// Create local copy: which map or mod the copy goes into, what it is called, which GDT holds its entries, what comes with
/// it (the helper's dependency tree with what a copy does with each node), then the clone itself with its progress, the
/// zone lines to add, and Undo. The helper decides everything; this only asks and shows (docs/classify.md, docs/extract.md).
/// </summary>
public sealed partial class StockCloneViewModel : ObservableObject
{
    private readonly AssetRecord _record;
    private readonly StockLibrary _library;
    private readonly string? _bo3Root;
    private readonly Action _close;
    private readonly Action<StockExtractResult, CloneTarget> _finished;
    private readonly Action<string> _status;
    private readonly Dispatcher _ui = Dispatcher.UIThread;
    private int _planGen;
    private StockDeps? _deps;
    private bool _planned;

    public StockCloneViewModel(AssetRecord record, StockLibrary library, string? bo3Root, IReadOnlyList<CloneTarget> targets,
        IReadOnlyList<CloneGdtChoice> gdts, Action close, Action<StockExtractResult, CloneTarget> finished, Action<string> status,
        CloneTarget? preselected = null)
    {
        _record = record;
        _library = library;
        _bo3Root = bo3Root;
        _close = close;
        _finished = finished;
        _status = status;
        Origin = record.Stock!;
        Targets = targets;
        Gdts = gdts;
        _gdt = gdts.FirstOrDefault();
        _target = preselected ?? targets.FirstOrDefault();
        Header = StockHeader.Build(Origin, library.Db);
        Subtitle = Header.AlwaysLoaded
            ? $"always loaded ({Header.Zone}.ff)"
            : Origin.Zones.Count == 1 ? $"only shipped in {Origin.Zone}.ff" : $"shipped in {Origin.Zones.Count:N0} zones, first {Origin.Zone}.ff";
        IsWeapon = Origin.Kind == "weapon";
        _planned = false;
        if (_target is null)
        {
            Stage = CloneStage.Failed;
            Error = "There is no map or mod to copy into. Create one first: a local copy goes into a map or mod's zone.";
        }
    }

    public StockOrigin Origin { get; }
    public StockHeader Header { get; }
    public string AssetName => Origin.Name;
    public string Kind => Origin.Kind;
    public string Subtitle { get; }
    public bool IsWeapon { get; }
    public IReadOnlyList<CloneTarget> Targets { get; }
    public IReadOnlyList<CloneGdtChoice> Gdts { get; }

    [ObservableProperty]
    private CloneTarget? _target;

    [ObservableProperty]
    private CloneGdtChoice? _gdt;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPlanning), nameof(IsReady), nameof(IsRunning), nameof(IsDone), nameof(IsFailed), nameof(IsUndone), nameof(ShowPlan), nameof(CanCreate), nameof(ShowResult))]
    private CloneStage _stage = CloneStage.Planning;

    public bool IsPlanning => Stage == CloneStage.Planning;
    public bool IsReady => Stage == CloneStage.Ready;
    public bool IsRunning => Stage == CloneStage.Running;
    public bool IsDone => Stage == CloneStage.Done;
    public bool IsFailed => Stage == CloneStage.Failed;
    public bool IsUndone => Stage == CloneStage.Undone;

    /// <summary>The options and the tree show until the copy is made.</summary>
    public bool ShowPlan => Stage is CloneStage.Planning or CloneStage.Ready or CloneStage.Running;
    public bool ShowResult => Stage is CloneStage.Done or CloneStage.Undone;

    public bool CanCreate => Stage == CloneStage.Ready && _deps is not null && CopyCount > 0 && Gdt is not null && NameValid;

    [ObservableProperty]
    private string _error = "";

    [ObservableProperty]
    private string _planNote = "Reading dependencies…";

    // ── Name ─────────────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KeepName), nameof(NewName))]
    private bool _keepNameChosen = true;

    public bool KeepName { get => KeepNameChosen; set { if (value) KeepNameChosen = true; } }
    public bool NewName { get => !KeepNameChosen; set { if (value) KeepNameChosen = false; } }

    [ObservableProperty]
    private string _prefix = "";

    [ObservableProperty]
    private bool _keepAllowed = true;

    /// <summary>The line under the name options: why the name can or cannot stay (the helper's reason).</summary>
    [ObservableProperty]
    private string _nameNote = "";

    /// <summary>"(yours overrides stock in this map)": what keeping the name does to the map.</summary>
    [ObservableProperty]
    private string _keepSuffix = "";

    public bool NameValid => KeepNameChosen ? KeepAllowed : Prefix.Trim().Length > 0 && Prefix.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '_');

    partial void OnKeepNameChosenChanged(bool value) => Replan(delayed: false);

    partial void OnPrefixChanged(string value)
    {
        OnPropertyChanged(nameof(CanCreate));
        if (!KeepNameChosen)
            Replan(delayed: true);
    }

    partial void OnTargetChanged(CloneTarget? value)
    {
        if (value is not null)
            Replan(delayed: false);
    }

    partial void OnGdtChanged(CloneGdtChoice? value) => OnPropertyChanged(nameof(CanCreate));

    // ── Camo ─────────────────────────────────────────────────────────────────

    /// <summary>Some node is a camo: a copy can leave it out (a camo the map does not define is "not a valid weaponcamo asset").</summary>
    [ObservableProperty]
    private bool _hasCamo;

    [ObservableProperty]
    private bool _clearCamo;

    partial void OnClearCamoChanged(bool value) => Replan(delayed: false);

    // ── Where the source files go ────────────────────────────────────────────

    public System.Collections.ObjectModel.ObservableCollection<string> SourceFolders { get; } = new();

    // ── The plan ─────────────────────────────────────────────────────────────

    public RangeObservableCollection<CloneDepRow> Rows { get; } = new();
    private readonly List<CloneDepRow> _top = new();

    [ObservableProperty]
    private string _summary = "";

    [ObservableProperty]
    private string _countText = "";

    [ObservableProperty]
    private string _createLabel = "Create local copy";

    [ObservableProperty]
    private string _notes = "";

    public bool HasNotes => Notes.Length > 0;

    [ObservableProperty]
    private int _copyCount;

    /// <summary>Starts planning: asks the helper for the dependencies against the picked target.</summary>
    public void Begin() => Replan(delayed: false);

    private void Replan(bool delayed)
    {
        if (Target is null || Stage is CloneStage.Running or CloneStage.Done or CloneStage.Undone)
            return;
        var gen = ++_planGen;
        var target = Target;
        var keep = KeepNameChosen;
        var prefix = Prefix.Trim();
        Stage = CloneStage.Planning;
        PlanNote = "Reading dependencies…";
        Error = "";
        OnPropertyChanged(nameof(CanCreate));
        _ = Task.Run(async () =>
        {
            try
            {
                if (delayed)
                    await Task.Delay(450).ConfigureAwait(false);
                if (gen != _planGen)
                    return;
                // Keeping the name: the helper proposes names (keeps where it can). A new name: every copy gets the prefix.
                var deps = await _library.DepsAsync(Origin.EngineType, Origin.Name, target.Argument, null,
                    keep || prefix.Length == 0 ? null : prefix, renameAll: !keep).ConfigureAwait(false);
                _ui.Post(() =>
                {
                    if (gen == _planGen)
                        ApplyPlan(deps);
                });
            }
            catch (Exception ex) when (ex is StockHelperException or TimeoutException or OperationCanceledException)
            {
                var message = ex.Message;
                _ui.Post(() =>
                {
                    if (gen != _planGen)
                        return;
                    _deps = null;
                    Rows.ReplaceAll(Array.Empty<CloneDepRow>());
                    Error = message;
                    Stage = CloneStage.Failed;
                });
            }
        });
    }

    private void ApplyPlan(StockDeps deps)
    {
        _deps = deps;
        var root = deps.RootNode;
        var naming = root.Naming;
        KeepAllowed = naming?.KeepAllowed ?? true;
        if (!_planned)
        {
            _planned = true;
            Prefix = deps.Prefix;
            if (!KeepAllowed)
                KeepNameChosen = false;
        }
        KeepSuffix = KeepAllowed ? (naming?.OverridesStock == true ? "(yours overrides stock in this map)" : "(no clash in this map)") : "";
        NameNote = naming?.Why is { Length: > 0 } why
            ? (KeepAllowed ? char.ToUpperInvariant(why[0]) + why[1..] + "." : "Can't keep it: " + why + ". Copies get a prefix instead.")
            : "";
        HasCamo = deps.Nodes.Any(n => n.Options.Contains("clear"));

        var nodes = deps.Nodes.Where(n => n.Id != deps.Root && n.Class != StockClass.Embedded).ToList();
        _top.Clear();
        var rootRow = new CloneDepRow(0, root.Kind, ProposedName(root), RootMeta(root, deps), root.Zone, root.Class, ClassLabel(root.Class), root.Reason, false, null);
        _top.Add(rootRow);
        var groups = nodes
            .GroupBy(n => (n.Kind, n.Class))
            .OrderBy(g => ClassOrder(g.Key.Class)).ThenBy(g => UsesOrderOf(g.Key.Kind)).ThenBy(g => g.Key.Kind, StringComparer.Ordinal);
        var openAll = nodes.Count <= 60;
        foreach (var g in groups)
        {
            var members = g.OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase).ToList();
            if (members.Count == 1)
            {
                _top.Add(MemberRow(1, members[0], deps));
                continue;
            }
            var first = members[0];
            var row = new CloneDepRow(1, g.Key.Kind, GroupTitle(g.Key.Kind, g.Key.Class), GroupMeta(members), ZonesOf(members), g.Key.Class,
                ClassLabel(g.Key.Class), first.Reason, true, ToggleRow)
            {
                IsExpanded = g.Key.Class == StockClass.Copy && openAll,
            };
            foreach (var m in members.Take(300))
                row.Children.Add(MemberRow(2, m, deps));
            if (members.Count > 300)
                row.Children.Add(new CloneDepRow(2, g.Key.Kind, $"…and {members.Count - 300:N0} more", "", "", g.Key.Class, ClassLabel(g.Key.Class), "", false, null));
            _top.Add(row);
        }
        Flatten();

        var copies = deps.Nodes.Count(n => n.Class == StockClass.Copy);
        CopyCount = copies;
        var shipped = deps.Count(StockClass.UseShipped);
        var rebuilt = deps.Count(StockClass.Rebuilt);
        var local = deps.Count(StockClass.Local);
        var odd = deps.Count(StockClass.Unsupported) + deps.Count(StockClass.Missing);
        var images = deps.Nodes.Count(n => n.Class == StockClass.Copy && n.Kind == "image");
        var meshes = deps.Count(StockClass.Embedded);
        var parts = new List<string> { $"Copies {copies:N0}" };
        if (shipped > 0)
            parts.Add($"uses {shipped:N0} shipped");
        if (rebuilt > 0)
            parts.Add($"{rebuilt:N0} rebuilt");
        if (local > 0)
            parts.Add($"{local:N0} already local");
        if (odd > 0)
            parts.Add($"{odd:N0} unsupported in v1");
        var writes = new List<string>();
        if (meshes > 0)
            writes.Add($"{meshes:N0} xmodel_export");
        if (images > 0)
            writes.Add($"{images:N0} PNG");
        writes.Add($"{copies:N0} GDT entries");
        Summary = string.Join(" · ", parts) + " · writes " + string.Join(", ", writes);
        CountText = $"{nodes.Count + 1:N0} assets found by walking the dependencies";
        CreateLabel = copies == 1 ? "Create local copy" : $"Create {copies:N0} local assets";

        var lines = new List<string>();
        if (images > 0)
            lines.Add("Textures come out at the size the game ships them and are compressed again when you link. Normal maps get their Z rebuilt. Original LOD source files and smoothing groups can't be recovered.");
        if (Origin.Description?.Unrecoverable is { Count: > 0 } lost)
            lines.Add($"{lost.Count:N0} field{(lost.Count == 1 ? "" : "s")} can't be recovered from the stock zone and keep their defaults.");
        Notes = string.Join("\n", lines);
        OnPropertyChanged(nameof(HasNotes));
        BuildSourceFolders(deps, root);
        Stage = CloneStage.Ready;
        OnPropertyChanged(nameof(CanCreate));
        OnPropertyChanged(nameof(NameValid));
    }

    private void BuildSourceFolders(StockDeps deps, StockDepNode root)
    {
        SourceFolders.Clear();
        var zone = root.Zone.Length > 0 ? root.Zone : Origin.Zone;
        var name = root.Naming?.Proposed is { Length: > 0 } p ? p : root.Name;
        var slash = name.IndexOf('/');
        if (slash > 0)
            name = name[(slash + 1)..];
        if (deps.Nodes.Any(n => n.Class == StockClass.Copy && n.Kind == "xmodel"))
            SourceFolders.Add($@"model_export\stock\{zone}\{name}\");
        if (deps.Nodes.Any(n => n.Class == StockClass.Copy && n.Kind == "image"))
            SourceFolders.Add($@"texture_assets\stock\{zone}\{name}\");
        if (deps.Nodes.Any(n => n.Class == StockClass.Copy && n.Kind == "weapon"))
            SourceFolders.Add(@"share\raw\accuracy\");
        OnPropertyChanged(nameof(HasSourceFolders));
    }

    public bool HasSourceFolders => SourceFolders.Count > 0;

    private string ProposedName(StockDepNode node) =>
        node.Naming is { Proposed: { Length: > 0 } p } && !p.Equals(node.Name, StringComparison.Ordinal) ? $"{node.Name} → {p}" : node.Name;

    private CloneDepRow MemberRow(int level, StockDepNode node, StockDeps deps)
    {
        var meta = "";
        var facts = node.Facts;
        if (facts.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            var w = StockDepNode.Int(facts, "width");
            var h = StockDepNode.Int(facts, "height");
            meta = w > 0 ? $"{w}×{h} · {FormatName(StockDepNode.Int(facts, "format"))}" : "";
        }
        else if (node.Reason.Length > 0 && node.Class is StockClass.Unsupported or StockClass.Missing)
            meta = node.Reason;
        var label = node.Class == StockClass.Copy && node.Naming is { Proposed: { Length: > 0 } p } && !p.Equals(node.Name, StringComparison.Ordinal)
            ? $"{node.Name} → {p}"
            : node.Name;
        return new CloneDepRow(level, node.Kind, label, meta, node.Zone, node.Class, ClassLabel(node.Class), node.Reason, false, null);
    }

    /// <summary>A DXGI format number as the shipped image names it (BC1, BC7, ...); the number when unknown.</summary>
    private static string FormatName(int dxgi) => dxgi switch
    {
        70 or 71 or 72 => "BC1",
        73 or 74 or 75 => "BC2",
        76 or 77 or 78 => "BC3",
        79 or 80 => "BC4",
        81 or 82 => "BC4",
        83 or 84 => "BC5",
        94 or 95 or 96 => "BC6H",
        97 or 98 or 99 => "BC7",
        28 or 29 => "RGBA8",
        61 => "R8",
        0 => "",
        _ => "DXGI " + dxgi,
    };

    private static string RootMeta(StockDepNode root, StockDeps deps)
    {
        if (root.Kind == "xmodel" && root.Facts.ValueKind == System.Text.Json.JsonValueKind.Object && StockDepNode.Strings(root.Facts, "lods") is { Count: > 0 } lods)
            return $"{lods.Count} LODs";
        return root.Kind == "weapon" ? $"{deps.Nodes.Count(n => n.Kind == "attachmentunique" && n.Class == StockClass.Copy):N0} attachment uniques copied" : "";
    }

    private static string ClassLabel(StockClass c) => c switch
    {
        StockClass.Copy => "Copy, editable",
        StockClass.UseShipped => "Use shipped",
        StockClass.Rebuilt => "Rebuilt by linker",
        StockClass.Local => "Already local",
        StockClass.Embedded => "Part of the model",
        StockClass.Unsupported => "Unsupported in v1",
        _ => "Not found",
    };

    private static int ClassOrder(StockClass c) => c switch
    {
        StockClass.Copy => 0,
        StockClass.Rebuilt => 1,
        StockClass.UseShipped => 2,
        StockClass.Local => 3,
        StockClass.Unsupported => 4,
        _ => 5,
    };

    private static int UsesOrderOf(string kind) => kind switch
    {
        "xmodel" => 0,
        "material" => 1,
        "image" => 2,
        "weapon" => 3,
        "attachmentunique" => 4,
        "xanim" => 8,
        _ => 5,
    };

    private static string GroupTitle(string kind, StockClass c) => kind switch
    {
        "xmodel" => "Models",
        "xanim" => "Animations",
        "material" => "Materials",
        "image" => c == StockClass.Copy ? "Textures for these materials" : "Shared textures",
        "techniqueset" => "Techsets",
        "attachmentunique" => "Attachment uniques",
        "attachment" => "Attachments",
        "attachmentcosmeticvariant" => "Cosmetic variants",
        "weaponcamo" => "Camos",
        "fx" => "Effects",
        "soundalias" => "Sound aliases",
        "weapon" => "Weapons",
        _ => kind.Length > 0 ? char.ToUpperInvariant(kind[0]) + kind[1..] + "s" : "Other",
    };

    private static string GroupMeta(List<StockDepNode> members) =>
        $"{members.Count:N0} · " + string.Join(", ", members.Take(2).Select(m => m.Name)) + (members.Count > 2 ? " …" : "");

    private static string ZonesOf(List<StockDepNode> members)
    {
        var zones = members.Select(m => m.Zone).Where(z => z.Length > 0).GroupBy(z => z).OrderByDescending(g => g.Count()).Select(g => g.Key).ToList();
        return zones.Count == 0 ? "" : zones.Count == 1 ? zones[0] : $"{zones[0]} +{zones.Count - 1}";
    }

    private void ToggleRow(CloneDepRow row)
    {
        row.IsExpanded = !row.IsExpanded;
        Flatten();
    }

    private void Flatten()
    {
        var flat = new List<CloneDepRow>();
        foreach (var row in _top)
        {
            flat.Add(row);
            if (row.IsExpanded)
                flat.AddRange(row.Children);
        }
        Rows.ReplaceAll(flat);
    }

    // ── Making the copy ──────────────────────────────────────────────────────

    [ObservableProperty]
    private string _progressText = "";

    [ObservableProperty]
    private double _progressFraction;

    [ObservableProperty]
    private bool _progressKnown;

    public StockExtractResult? Result { get; private set; }

    /// <summary>Puts text on the clipboard (set by the window).</summary>
    public Func<string, Task>? CopyText { get; set; }

    [RelayCommand]
    private async Task CopyZoneLinesAsync()
    {
        if (CopyText is not null && ZoneLines.Length > 0)
            await CopyText(ZoneLines).ConfigureAwait(true);
    }

    [ObservableProperty]
    private string _resultTitle = "";

    [ObservableProperty]
    private string _resultDetail = "";

    [ObservableProperty]
    private string _zoneLines = "";

    public bool HasZoneLines => ZoneLines.Length > 0;

    [ObservableProperty]
    private string _resultNotes = "";

    public bool HasResultNotes => ResultNotes.Length > 0;

    [ObservableProperty]
    private bool _canUndo;

    [ObservableProperty]
    private string _afterText = "";

    [RelayCommand]
    private async Task CreateAsync()
    {
        if (!CanCreate || Target is null)
            return;
        var target = Target;
        Stage = CloneStage.Running;
        Error = "";
        ProgressText = "Starting…";
        ProgressKnown = false;
        ProgressFraction = 0;
        var request = new StockExtractRequest
        {
            Type = Origin.EngineType,
            Name = Origin.Name,
            Map = target.Argument,
            Gdt = Gdt?.FullPath,
            Prefix = KeepNameChosen ? null : Prefix.Trim(),
            KeepName = KeepNameChosen,
            RenameAll = !KeepNameChosen,
            ClearCamo = HasCamo && ClearCamo,
        };
        try
        {
            var result = await Task.Run(() => _library.ExtractAsync(request, p => _ui.Post(() =>
            {
                ProgressText = p.Total > 0 ? $"{p.What} ({p.Done:N0} of {p.Total:N0})" : p.What;
                ProgressKnown = p.Total > 0;
                ProgressFraction = p.Total > 0 ? (double)p.Done / p.Total : 0;
            }))).ConfigureAwait(true);
            Result = result;
            ShowResultOf(result);
            Stage = CloneStage.Done;
            _finished(result, target);
            await UpdateGdtDbAsync("Created").ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is StockHelperException or TimeoutException or OperationCanceledException)
        {
            Error = ex.Message;
            Stage = CloneStage.Ready;
        }
    }

    private void ShowResultOf(StockExtractResult result)
    {
        var assets = result.Entries;
        ResultTitle = $"Created {assets:N0} local asset{(assets == 1 ? "" : "s")}";
        var byKind = result.Files.GroupBy(f => f.Kind).OrderByDescending(g => g.Count()).Select(g => $"{g.Count():N0} {KindWord(g.Key)}").ToList();
        var gdtName = System.IO.Path.GetFileName(result.Gdt);
        ResultDetail = (gdtName.Length > 0 ? $"Entries are in {gdtName}. " : "") + (byKind.Count > 0 ? "Wrote " + string.Join(", ", byKind) + "." : "");
        ZoneLines = string.Join("\n", result.Zone);
        OnPropertyChanged(nameof(HasZoneLines));
        var notes = new List<string>();
        if (result.Renames.Count > 0)
            notes.Add($"{result.Renames.Count:N0} renamed, for example {result.Renames[0].From} → {result.Renames[0].To}.");
        foreach (var s in result.Skipped.Take(4))
            notes.Add($"Not copied: {s.Type} {s.Name} ({s.Why}).");
        if (result.Skipped.Count > 4)
            notes.Add($"…and {result.Skipped.Count - 4:N0} more not copied.");
        foreach (var s in result.Unresolved.Take(3))
            notes.Add($"Not found: {s.Type} {s.Name} ({s.Why}).");
        foreach (var n in result.Notes.Take(3))
            notes.Add(n);
        ResultNotes = string.Join("\n", notes);
        OnPropertyChanged(nameof(HasResultNotes));
        CanUndo = result.Manifest is { Length: > 0 };
        AfterText = "";
    }

    private static string KindWord(string kind) => kind switch
    {
        "image" => "textures",
        "dds" => "DDS files",
        "export" => "model exports",
        "xmodel_bin" => "model bins",
        "accu" => "accuracy graphs",
        "gdt" => "GDT file",
        _ => kind,
    };

    private async Task UpdateGdtDbAsync(string verb)
    {
        AfterText = "Updating gdt.db so the linker sees the change…";
        var (ok, message) = await GdtDbUpdate.RunAsync(_bo3Root).ConfigureAwait(true);
        AfterText = ok ? $"{verb}. gdt.db is up to date; add the zone lines to your zone file and link." : message;
        if (verb == "Undone")
            AfterText = ok ? "Undone. gdt.db is up to date." : message;
    }

    [RelayCommand]
    private async Task UndoAsync()
    {
        if (Result?.Manifest is not { Length: > 0 } manifest || !CanUndo)
            return;
        CanUndo = false;
        AfterText = "Undoing…";
        try
        {
            var undone = await Task.Run(() => _library.UndoAsync(manifest)).ConfigureAwait(true);
            ResultTitle = $"Undone: {undone.Deleted:N0} file{(undone.Deleted == 1 ? "" : "s")} removed" + (undone.Restored > 0 ? $", {undone.Restored:N0} restored" : "");
            ResultDetail = undone.KeptChanged.Count > 0
                ? $"{undone.KeptChanged.Count:N0} file{(undone.KeptChanged.Count == 1 ? " was" : "s were")} changed since and kept: {string.Join(", ", undone.KeptChanged.Take(3))}."
                : "Everything the copy wrote is gone.";
            ZoneLines = "";
            OnPropertyChanged(nameof(HasZoneLines));
            ResultNotes = "";
            OnPropertyChanged(nameof(HasResultNotes));
            Stage = CloneStage.Undone;
            await UpdateGdtDbAsync("Undone").ConfigureAwait(true);
            _status($"Undid the local copy of {Origin.Name}");
        }
        catch (Exception ex) when (ex is StockHelperException or TimeoutException or OperationCanceledException)
        {
            AfterText = "Undo failed: " + ex.Message;
            CanUndo = true;
        }
    }

    [RelayCommand]
    private void Close()
    {
        if (Stage == CloneStage.Running)
            return;
        _planGen++;
        _close();
    }
}
