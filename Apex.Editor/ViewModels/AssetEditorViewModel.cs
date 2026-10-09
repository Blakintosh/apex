using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
using Apex.Editor.Commands;

namespace Apex.Editor.ViewModels;

/// <summary>Which rows the editor lists: everything, this session's changes, problems, or overrides of the parent.</summary>
public enum EditorView { All, Changed, Problems, Overrides, Set }

/// <summary>
/// One category group in the editor. It is both a section header in the flat property list and a
/// rail entry; the rail jumps to a section rather than filtering to it, so the form stays one
/// continuous scroll. The "All" sentinel (IsAll) owns every row and is never shown in the rail.
/// </summary>
public sealed partial class CategoryViewModel : ObservableObject
{
    public CategoryViewModel(string name, IReadOnlyList<PropertyItemViewModel> items, bool isAll = false)
    {
        Name = name;
        All = items;
        IsAll = isAll;
        Visible = new List<PropertyItemViewModel>(items);
        _visibleCount = items.Sum(p => p.RowCount);
        foreach (var p in items)
            p.RowCountChanged += () => VisibleCount = Visible.Sum(v => v.RowCount);
        RecountModified();
    }

    public string Name { get; }

    /// <summary>The grid this section's keys make (see <see cref="KeyGridDetector"/>); null for a section that is a list.</summary>
    public Apex.Editor.Services.KeyGrid? Grid { get; internal set; }

    public bool HasGrid => Grid is not null;

    /// <summary>The grid is shown as one (the default where there is one); off, its keys are rows as in any section.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MatrixToggleText))]
    private bool _isMatrix = true;

    public string MatrixToggleText => IsMatrix ? "Show as list" : "Show as matrix";

    /// <summary>Raised when the header's button switches this section between the matrix and its list.</summary>
    public event Action<CategoryViewModel>? MatrixChanged;

    partial void OnIsMatrixChanged(bool value) => MatrixChanged?.Invoke(this);

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void ToggleMatrix() => IsMatrix = !IsMatrix;

    public bool IsAll { get; }
    public IReadOnlyList<PropertyItemViewModel> All { get; }

    /// <summary>The extension whose section this is (its header says so); null for a deffile section.</summary>
    public string? Extension { get; init; }

    public bool IsExtension => Extension is not null;

    /// <summary>Where an extension section's fields come from and where their values are saved.</summary>
    public string? ExtensionTip { get; init; }

    /// <summary>What else the filter finds an extension's section by: its extension's id (as words) and title.</summary>
    public string? ExtensionSearch { get; init; }

    /// <summary>The filter names the section itself: every row of it matches.</summary>
    public bool TitleMatches(string query) =>
        IsExtension && query.Length > 0
        && (Name.Contains(query, StringComparison.OrdinalIgnoreCase) || ExtensionSearch?.Contains(query, StringComparison.OrdinalIgnoreCase) == true);

    /// <summary>Whether a row of this section passes the filter and view (a section the filter names passes all of its rows).</summary>
    public bool Shows(PropertyItemViewModel p, string query, EditorView view) => Passes(p, TitleMatches(query) ? "" : query, view);

    // ── An extension section that folds while it holds nothing (collapsedUnlessSet) ──

    /// <summary>Folds to its header while none of its rows holds a value; the header's ▸ / ▾ opens and folds it.</summary>
    public bool IsCollapsible { get; init; }

    /// <summary>Folded: only its header shows (<see cref="IsExpanded"/>, the xanim panel's card state too: session state of this tab, never saved).</summary>
    public bool IsCollapsed => IsCollapsible && !IsExpanded;

    /// <summary>Raised when the header's toggle opens or folds the section.</summary>
    public event Action<CategoryViewModel>? ExpandedChanged;

    private bool _settingExpanded;

    /// <summary>Opens or folds the section without it counting as the user's toggle.</summary>
    public void SetExpanded(bool expanded)
    {
        _settingExpanded = true;
        try
        {
            IsExpanded = expanded;
        }
        finally
        {
            _settingExpanded = false;
        }
    }

    partial void OnIsExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(IsCollapsed));
        if (!_settingExpanded)
            ExpandedChanged?.Invoke(this);
    }

    /// <summary>Rows holding a value, the asset's own or inherited (the header says how many: a folded section still tells).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SetText))]
    private int _setCount;

    public string SetText => SetCount == 0 ? "none set" : $"{SetCount} set";

    // ── An extension's first header: its export ──

    /// <summary>The extension's export command (its manifest's words) while it is on for the asset; null for none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanExport))]
    private string? _exportLabel;

    public bool CanExport => ExportLabel is not null;

    public string? ExportTip { get; init; }

    public CommunityToolkit.Mvvm.Input.IRelayCommand? ExportCommand { get; init; }

    /// <summary>Rows passing the current filter; spliced into the editor's flat virtualized list.</summary>
    public List<PropertyItemViewModel> Visible { get; }

    public int Count => All.Count;

    [ObservableProperty]
    private int _visibleCount;

    /// <summary>First visible section: no top margin above its header.</summary>
    [ObservableProperty]
    private bool _isFirst;

    /// <summary>Rows edited this session (the rail's dot).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChanges), nameof(RailTip))]
    private int _modifiedCount;

    public bool HasChanges => ModifiedCount > 0;

    /// <summary>Shown rows with a validation problem (the rail's ⚠).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblems), nameof(RailTip))]
    private int _problemCount;

    public bool HasProblems => ProblemCount > 0;

    /// <summary>The rail entry's tooltip: the whole name (the entry trims it), then what its marks mean.</summary>
    public string RailTip
    {
        get
        {
            var tip = Name;
            if (ProblemCount > 0)
                tip += ProblemCount == 1 ? "\n⚠ 1 property with a problem" : $"\n⚠ {ProblemCount} properties with problems";
            if (ModifiedCount > 0)
                tip += ModifiedCount == 1 ? "\n● 1 property changed this session" : $"\n● {ModifiedCount} properties changed this session";
            return tip;
        }
    }

    /// <summary>The rail highlights the section at the top of the viewport.</summary>
    [ObservableProperty]
    private bool _isCurrent;

    public void RecountModified() => ModifiedCount = All.Count(p => p.IsChanged);

    public void ApplyFilter(string query, EditorView view)
    {
        Visible.Clear();
        if (TitleMatches(query))
            query = "";
        foreach (var p in All)
            if (Passes(p, query, view))
                Visible.Add(p);
        VisibleCount = Visible.Sum(p => p.RowCount);
    }

    public static bool Passes(PropertyItemViewModel p, string query, EditorView view, bool ignoreRules = false)
    {
        if (p.IsRuleHidden && !ignoreRules)
            return false;
        if (view == EditorView.Changed && !p.IsChanged)
            return false;
        if (view == EditorView.Problems && !p.HasProblem)
            return false;
        if (view == EditorView.Overrides && !p.IsOverride)
            return false;
        if (view == EditorView.Set && !p.IsHeld && !p.IsRevealed)
            return false;
        return query.Length == 0 || p.MatchesFilter(query);
    }
}

/// <summary>A row the Add property list offers: its label, and where it sits ("LODs · LOD 1").</summary>
public sealed record PropertyPick(PropertyItemViewModel Item, string Label, string Where);

/// <summary>One entry in the per-asset problems list shown by the inspector.</summary>
public sealed record ProblemItem(string Label, string Key, string Message) : Controls.IRowStandIn
{
    object? Controls.IRowStandIn.CreateStandIn() => new ProblemItem("", "", "");
}

/// <summary>One of this session's edits, as the Inspector's "Your changes" list shows it.</summary>
public sealed record ChangeItem(PropertyItemViewModel Item, string Old, string Now)
{
    public string Label => Item.Label;
}

/// <summary>An open asset tab: builds property rows from the asset's schema and tracks edits.</summary>
public sealed partial class AssetEditorViewModel : ObservableObject, IFileProbeListener
{
    private readonly Action<AssetEditorViewModel> _onClose;
    private readonly Action<AssetEditorViewModel> _onEdited;
    private readonly Func<string, string, AssetRecord?> _resolve;
    private IReadOnlyDictionary<string, string> _baseline;
    private readonly List<CategoryViewModel> _categories = new();
    private Dictionary<string, string>? _parentValues;
    private Services.Gdf.SchemaOverlay? _overlay;

    // Set while rows are being brought to values already in the record (undo, sync): the row edit
    // path then neither writes the record nor records history.
    private bool _squelchHistory;
    private bool _rulesPending;

    public AssetEditorViewModel(
        AssetRecord record,
        Action<string, string> navigateToRef,
        Action<AssetEditorViewModel> onClose,
        Action<AssetEditorViewModel> onEdited,
        Func<string, string, AssetRecord?> resolve,
        Action<AssetEditorViewModel>? onRename = null,
        Func<AssetRecord, GdtFile?>? gdtOf = null)
    {
        Record = record;
        _onClose = onClose;
        _onEdited = onEdited;
        _resolve = resolve;
        _onRename = onRename;
        _gdtOf = gdtOf;
        _renameText = record.Name;
        // "Changed" is measured against the values before this session touched the asset, so a tab
        // closed and reopened still shows this session's edits.
        record.CaptureBaseline();
        _baseline = record.SessionBaseline!;

        // The deffile's run for this asset arranges the form: a material's sections follow its techsetdef.
        _parentValues = ResolveParentChainValues();
        var schema = SchemaRegistry.Get(record.Type);
        var opening = schema is null ? null : GdfRuntime.EvaluateOverlay(Record.Type, Record.Name, EffectiveValues());
        if (schema is not null)
            schema = GdfRuntime.Arrange(schema, opening);
        var allProps = new List<PropertyItemViewModel>();

        if (schema is not null)
        {
            foreach (var group in schema.Properties.GroupBy(p => p.Category))
            {
                var items = group
                    .Select(def => Create(def, record.Properties.GetValueOrDefault(def.Key) ?? Unowned(def), navigateToRef, ValueOf, AssetExists))
                    .ToList();
                foreach (var item in items)
                {
                    item.Edited += OnPropertyEdited;
                    allProps.Add(item);
                }
                _categories.Add(new CategoryViewModel(group.Key, items));
            }

            // A section holding only deffile buttons (bonuszmdata's "Add Skipto") goes where the deffile put it: before
            // the section of the next property it adds.
            foreach (var group in opening?.Buttons ?? [])
            {
                if (_categories.Any(c => c.Name == group.Category))
                    continue;
                var before = group.NextKey is { } next ? _categories.FindIndex(c => c.All.Any(p => p.Key.Equals(next, StringComparison.OrdinalIgnoreCase))) : -1;
                _categories.Insert(before < 0 ? _categories.Count : before, new CategoryViewModel(group.Category, []));
            }

            AddExtensionSections(allProps, navigateToRef);

            // Union view (§4.4): real assets — and partial schemas like material — carry keys the
            // schema doesn't define. Surface them as Text rows in a trailing "Other" category so no
            // on-disk data is hidden from the editor.
            var covered = new HashSet<string>(schema.Properties.Select(p => p.Key), StringComparer.OrdinalIgnoreCase);
            var extras = record.Properties
                .Where(kv => !covered.Contains(kv.Key))
                .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .Select(kv => (PropertyItemViewModel)new TextPropertyViewModel(
                    new PropertyDef(kv.Key, Prettify(kv.Key), "Other", PropertyKind.Text,
                        "Asset property present in the GDT but not defined by this type's schema.")
                        { Default = kv.Value },
                    kv.Value))
                .ToList();
            if (extras.Count > 0)
            {
                foreach (var item in extras)
                {
                    item.Edited += OnPropertyEdited;
                    allProps.Add(item);
                }
                _categories.Add(new CategoryViewModel("Other", extras));
            }
        }
        else
        {
            // Generic fallback: raw key/value rows; "default" is the value at open so
            // modified-tracking still highlights this session's edits.
            var items = record.Properties
                .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .Select(kv => (PropertyItemViewModel)new TextPropertyViewModel(
                    new PropertyDef(kv.Key, Prettify(kv.Key), "Properties", PropertyKind.Text,
                        "Raw GDT property (no schema registered for this asset type).")
                        { Default = kv.Value },
                    kv.Value))
                .ToList();
            foreach (var item in items)
            {
                item.Edited += OnPropertyEdited;
                allProps.Add(item);
            }
            _categories.Add(new CategoryViewModel("Properties", items));
        }

        AllSentinel = new CategoryViewModel("All Properties", allProps, isAll: true);
        // An extension's sections lead the rail (the form keeps them after the deffile's): on a weapon's forty sections
        // they would otherwise be the last thing anyone finds.
        _railOrder = _extensions is null
            ? new List<CategoryViewModel>(_categories)
            : _categories.Where(c => c.IsExtension).Concat(_categories.Where(c => !c.IsExtension)).ToList();
        for (var i = 0; i < _railOrder.Count; i++)
            _railIndex[_railOrder[i]] = i;

        // Per-field provenance: effective value from the parent chain (nearest ancestor wins).
        foreach (var item in allProps)
        {
            if (_extensionOf.TryGetValue(item, out var ext))
                InitExtensionRow(item, ext);
            else
            {
                if (_parentValues is not null)
                    item.InitProvenance(_parentValues.GetValueOrDefault(item.Key));
                item.InitBaseline(_baseline.GetValueOrDefault(item.Key) ?? Unowned(item.Def));
            }
            if (item.IsOverride)
                _overrideRows.Add(item);
            _rowByKey.TryAdd(item.Key, item);
        }
        OverrideCount = _overrideRows.Count;

        // Spaced, so a deffile button row can sit between two rows without renumbering the form.
        var order = 0;
        foreach (var c in _categories)
        {
            _rowOrder[c] = order += RowGap;
            _sectionByName.TryAdd(c.Name, c);
            MakeMatrix(c);
            SubsectionRowViewModel? run = null;
            foreach (var p in c.All)
            {
                _categoryOf[p] = c;
                _rowOrder[p] = order += RowGap;
                // A new sub-section's title takes the place just before its first row.
                var title = p.Def.Subgroup;
                if (title.Length == 0)
                    run = null;
                else if (run is null || run.Title != title)
                {
                    run = new SubsectionRowViewModel(title);
                    _rowOrder[run] = order - 1;
                    _subsectioned.Add(c);
                }
                if (run is not null)
                    _subsectionOf[p] = run;
            }
        }
        PlaceExtensionRows();
        GroupVectors();

        // Values are checked once the deffile has run for this asset: a choice is checked against the options it offers
        // this asset, and a property it hides counts no problem.
        EvaluateDisplayRules();
        foreach (var item in allProps)
        {
            item.Problem = Check(item);
            item.IsHeld = HoldsValue(item);
        }
        _checked = true;
        RefreshVisible();
        RecountModified();
        RebuildProblems();
        RebuildChanges();
        FieldFiles.Listen(this);
    }

    // ── Vectors: a deffile vector's components on one row ───────────────────

    private readonly Dictionary<PropertyItemViewModel, VectorRowViewModel> _vectorOf = new();

    /// <summary>
    /// Gathers each deffile vector's components (consecutive rows of one section naming the same vector) into one form
    /// row. The components stay rows of their own everywhere else: problems, changes, undo, the Inspector, the filter.
    /// An xanim's properties are the short panel, which lists its rows as they are.
    /// </summary>
    private void GroupVectors()
    {
        if (IsAnim)
            return;
        foreach (var c in _categories)
        {
            for (var i = 0; i < c.All.Count;)
            {
                var key = c.All[i].Def.VectorKey;
                var end = i + 1;
                while (key is not null && end < c.All.Count && c.All[end].Def.VectorKey == key && c.All[end] is NumberPropertyViewModel)
                    end++;
                if (key is not null && end - i > 1 && c.All[i] is NumberPropertyViewModel)
                {
                    var parts = new List<PropertyItemViewModel>(end - i);
                    for (var k = i; k < end; k++)
                        parts.Add(c.All[k]);
                    var row = new VectorRowViewModel(parts);
                    _rowOrder[row] = _rowOrder[parts[0]];
                    foreach (var part in parts)
                        _vectorOf[part] = row;
                }
                i = end;
            }
        }
    }

    /// <summary>The form row a property shows in: its vector's row for a vector's component, else its own.</summary>
    public object RowFor(PropertyItemViewModel item) => _vectorOf.TryGetValue(item, out var vector) ? vector : item;

    /// <summary>The form shows the property right now (in its own row, or its vector's).</summary>
    public bool IsShown(PropertyItemViewModel item) => FlatRows.Contains(RowFor(item));

    /// <summary>A property's current value on this asset (its row's, else the record's).</summary>
    private string? ValueOf(string key) =>
        _rowByKey.TryGetValue(key, out var row) ? row.RawValue : Record.Properties.GetValueOrDefault(key);

    // ── Per-edit bookkeeping ─────────────────────────────────────────────────
    // A keystroke or scrub tick touches one row, so counts, Problems and Changes are adjusted for
    // that row alone instead of re-walking all ~1,300 (and re-realizing the inspector's lists).

    /// <summary>Position of every header and row in form order; FlatRows and the lists below are sorted by it.</summary>
    private readonly Dictionary<object, int> _rowOrder = new();

    /// <summary>The gap between consecutive rows' <see cref="_rowOrder"/>: deffile button rows take the places in between.</summary>
    private const int RowGap = 64;
    private readonly Dictionary<string, PropertyItemViewModel> _rowByKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<PropertyItemViewModel> _changedRows = new();
    private readonly HashSet<PropertyItemViewModel> _overrideRows = new();
    // Parallel to Problems / Changes: the row each entry belongs to, for binary search by form order.
    private readonly List<PropertyItemViewModel> _problemRows = new();
    private readonly List<PropertyItemViewModel> _changeRows = new();

    /// <summary>Index of <paramref name="item"/> in a form-ordered row list, or the complement of its insertion point.</summary>
    private int FormSearch(List<PropertyItemViewModel> rows, PropertyItemViewModel item)
    {
        var target = _rowOrder[item];
        int lo = 0, hi = rows.Count - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >> 1;
            var at = _rowOrder[rows[mid]];
            if (at == target)
                return mid;
            if (at < target)
                lo = mid + 1;
            else
                hi = mid - 1;
        }
        return ~lo;
    }

    /// <summary>Rows have been checked (the constructor checks them once the deffile's rules are known).</summary>
    private bool _checked;

    /// <summary>
    /// A row's problem counts (in the Problems view and its count, the rail, the Inspector and the status bar) only while
    /// the deffile shows the row: one the deffile hides for this asset can't be seen or fixed here.
    /// </summary>
    private static string? CountedProblem(PropertyItemViewModel item) => item.IsRuleHidden ? null : item.Problem;

    // Rows a rule run showed, hid or re-checked: their problems may have come into the count or left it.
    private readonly List<PropertyItemViewModel> _problemsMoved = new();

    /// <summary>Brings the problem list and counts to the rows a rule run touched (after the rules ran).</summary>
    private void SyncProblems()
    {
        if (_checked)
            foreach (var p in _problemsMoved)
                UpdateProblemRow(p, _categoryOf.GetValueOrDefault(p));
        _problemsMoved.Clear();
    }

    private void UpdateProblemRow(PropertyItemViewModel item, CategoryViewModel? owner)
    {
        var i = FormSearch(_problemRows, item);
        if (CountedProblem(item) is { } message)
        {
            var entry = new ProblemItem(item.Label, item.Key, message);
            if (i < 0)
            {
                _problemRows.Insert(~i, item);
                Problems.Insert(~i, entry);
                if (owner is not null)
                    owner.ProblemCount++;
            }
            else if (Problems[i] != entry)
                Problems[i] = entry;
        }
        else if (i >= 0)
        {
            _problemRows.RemoveAt(i);
            Problems.RemoveAt(i);
            if (owner is not null)
                owner.ProblemCount--;
        }
        ProblemCount = Problems.Count;
    }

    private void UpdateChangeRow(PropertyItemViewModel item)
    {
        var i = FormSearch(_changeRows, item);
        if (item.IsChanged)
        {
            var entry = NewChange(item);
            if (i < 0)
            {
                _changeRows.Insert(~i, item);
                Changes.Insert(~i, entry);
            }
            else if (Changes[i] != entry)
                Changes[i] = entry;
        }
        else if (i >= 0)
        {
            _changeRows.RemoveAt(i);
            Changes.RemoveAt(i);
        }
    }

    /// <summary>Moves the row between the changed / override sets after an edit, adjusting the counts that mirror them.</summary>
    private void UpdateRowCounts(PropertyItemViewModel item, CategoryViewModel? owner)
    {
        if (item.IsChanged ? _changedRows.Add(item) : _changedRows.Remove(item))
        {
            if (owner is not null)
                owner.ModifiedCount += item.IsChanged ? 1 : -1;
            AllSentinel.ModifiedCount = ModifiedCount = _changedRows.Count;
        }
        if (item.IsOverride ? _overrideRows.Add(item) : _overrideRows.Remove(item))
            OverrideCount = _overrideRows.Count;
    }

    /// <summary>
    /// Re-runs this type's GenerateUI against the asset's current effective values and applies the
    /// resulting show/enable/options rules — APE's conditional property logic. Returns true when any
    /// row's visibility changed (caller must RefreshVisible). Types without a deffile program fall
    /// back to the schema's default-state visibility.
    /// </summary>
    private bool EvaluateDisplayRules()
    {
        _overlay = Services.Gdf.GdfRuntime.EvaluateOverlay(Record.Type, Record.Name, EffectiveValues());

        var visibilityChanged = false;
        foreach (var p in AllSentinel.All)
        {
            if (p.Def.Extension.Length > 0)
                continue;
            var rule = _overlay?.Rules.GetValueOrDefault(p.Key);
            // No rule: with an overlay this is an "Other"/raw row (always shown); without one,
            // fall back to the static default-state visibility from the schema.
            var hidden = rule is not null ? !rule.Visible : !p.Def.DefaultVisible;
            if (p.IsRuleHidden != hidden)
            {
                p.IsRuleHidden = hidden;
                if (p.Problem is not null)
                    _problemsMoved.Add(p);
                visibilityChanged = true;
            }
            p.IsRuleDisabled = rule is { Enabled: false };
            // New options for this asset: the value is checked against them, never against the schema's.
            if (rule?.Choices is { } choices && p is ChoicePropertyViewModel choice
                && choice.UpdateChoices(choices, rule.ChoiceLabels) && _checked)
            {
                choice.Problem = Check(choice);
                _problemsMoved.Add(choice);
            }
        }
        var changed = SyncButtonRows() | EvaluateExtensionRules() | visibilityChanged;
        SyncProblems();
        ScheduleEnablers();
        return changed;
    }

    // ── What enables a disabled row ──────────────────────────────────────────
    // APE greys a row the deffile disables and says nothing; the row here says which field enables it. Finding that out
    // means re-running the deffile with each nearby switch flipped, so it is done only for a row someone asks about (its
    // tip opening), for that row alone, off the UI thread. A speculative search of every disabled row after every rule
    // change ran the script a few hundred times and its garbage collections stalled the UI thread past a frame.

    private const int MaxEnablerCandidates = 24;
    private int _enablersRun;

    /// <summary>The last search for what enables a disabled row has finished (for Apex.Shots).</summary>
    public bool EnablersSettled { get; private set; } = true;

    /// <summary>The rules ran again: what enabled a row before may not now, so the rows forget it and ask again.</summary>
    private void ScheduleEnablers()
    {
        Interlocked.Increment(ref _enablersRun);
        foreach (var p in AllSentinel.All)
            if (p.EnabledBy is not null)
                p.EnabledBy = null;
        EnablersSettled = true;
    }

    /// <summary>Finds what enables <paramref name="row"/> (its tip is opening), off the UI thread; the tip updates when found.</summary>
    public void FindEnablers(PropertyItemViewModel row)
    {
        if (_overlay is not { } overlay || !row.IsRuleDisabled || row.IsRuleHidden || row.EnabledBy is not null
            || row.Def.Extension.Length > 0 || !_rowOrder.TryGetValue(row, out var at))
            return;
        var run = Volatile.Read(ref _enablersRun);
        // The switches and choices the deffile reads that the user can set here, nearest the row first: the switch that
        // enables a row usually sits just above it.
        var candidates = AllSentinel.All
            .Where(p => overlay.AffectedBy(p.Key) && !p.IsRuleHidden && !p.IsRuleDisabled && p.Def.Extension.Length == 0
                        && p is TogglePropertyViewModel or ChoicePropertyViewModel)
            .OrderBy(p => Math.Abs(at - _rowOrder[p]))
            .Take(MaxEnablerCandidates)
            .Select(p => (p.Key, Tries: p is TogglePropertyViewModel t ? [t.IsOn ? "0" : "1"]
                : ((ChoicePropertyViewModel)p).Options.Where(o => !o.Equals(p.RawValue, StringComparison.OrdinalIgnoreCase)).Take(8).ToArray()))
            .ToList();
        var effective = new Dictionary<string, string>(EffectiveValues(), StringComparer.OrdinalIgnoreCase);
        var key = row.Key;
        var (type, name) = (Record.Type, Record.Name);
        EnablersSettled = false;
        Task.Run(() =>
        {
            Dictionary<string, (string Key, string Value)> found;
            try
            {
                found = GdfRuntime.FindEnablers(type, name, effective, new[] { key }, candidates, () => Volatile.Read(ref _enablersRun) != run);
            }
            catch (Exception)
            {
                found = new(); // a script that fails here just leaves the row saying what it said
            }
            Dispatcher.UIThread.Post(() =>
            {
                if (Volatile.Read(ref _enablersRun) == run && found.TryGetValue(key, out var by) && _rowByKey.TryGetValue(by.Key, out var control))
                    row.EnabledBy = HowToEnable(control, by.Value);
                EnablersSettled = true;
            });
        });
    }

    /// <summary>"Bullet Impact Explode is Off. Set it to On to edit this."</summary>
    private static string HowToEnable(PropertyItemViewModel control, string value)
    {
        string Shown(string v) => control switch
        {
            TogglePropertyViewModel => v == "1" ? "On" : "Off",
            ChoicePropertyViewModel choice => choice.LabelOf(v),
            _ => v,
        };
        var now = Shown(control.RawValue);
        return $"{control.Label} is {(now.Length == 0 ? "empty" : now)}. Set it to {Shown(value)} to edit this.";
    }


    /// <summary>The asset's own values over its parent chain's: what the deffile sees.</summary>
    private IReadOnlyDictionary<string, string> EffectiveValues()
    {
        if (_parentValues is null)
            return Record.Properties;
        var effective = new Dictionary<string, string>(_parentValues, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in Record.Properties)
            effective[key] = value;
        return effective;
    }

    private Dictionary<string, string>? ResolveParentChainValues()
    {
        if (Record.Parent is null)
            return null;
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Record.Name };
        var cur = Record;
        while (cur.Parent is { } parentName && seen.Add(parentName))
        {
            var parent = _resolve(Record.Type, parentName);
            if (parent is null)
                break;
            foreach (var (key, value) in parent.Properties)
                if (!values.ContainsKey(key))
                    values[key] = value;
            cur = parent;
        }
        return values;
    }

    private bool AssetExists(string type, string name) =>
        _resolve(type, name) is { } found
        && (!type.Equals(Services.Extensions.ExtensionManifest.WeaponFamily, StringComparison.OrdinalIgnoreCase)
            || Services.Extensions.ExtensionManifest.IsWeapon(found.Type));

    /// <summary>True when <paramref name="name"/> is on this asset's parent chain.</summary>
    public bool InheritsFrom(string name)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Record.Name };
        for (var cur = Record; cur.Parent is { } p && seen.Add(p);)
        {
            if (p.Equals(name, StringComparison.OrdinalIgnoreCase))
                return true;
            if (_resolve(Record.Type, p) is not { } parent)
                return false;
            cur = parent;
        }
        return false;
    }

    /// <summary>
    /// An ancestor's values changed (edited in another tab, the table, compare): rows this asset inherits show the new
    /// value. Only rows whose inherited value moved are touched.
    /// </summary>
    public void RefreshInherited(IReadOnlyCollection<string>? keys = null)
    {
        if (Record.Parent is null)
            return;
        if (keys is null)
            _parentValues = ResolveParentChainValues();
        else
        {
            // Just these keys: the nearest ancestor holding each one, without copying the chain's values.
            _parentValues ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in keys)
            {
                string? value = null;
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Record.Name };
                for (var cur = Record; cur.Parent is { } p && seen.Add(p) && _resolve(Record.Type, p) is { } parent; cur = parent)
                    if (parent.Properties.TryGetValue(key, out var v))
                    {
                        value = v;
                        break;
                    }
                if (value is null)
                    _parentValues.Remove(key);
                else
                    _parentValues[key] = value;
            }
        }
        _squelchHistory = true;
        try
        {
            IEnumerable<PropertyItemViewModel> rows = keys is null
                ? AllSentinel.All
                : keys.Select(k => _rowByKey.GetValueOrDefault(k)).OfType<PropertyItemViewModel>();
            if (_extensions is not null)
                rows = rows.Where(p => !_extensionOf.ContainsKey(p));
            foreach (var p in rows)
            {
                var inherited = _parentValues?.GetValueOrDefault(p.Key);
                if (inherited == p.ParentValue)
                    continue;
                p.InitProvenance(inherited);
                if (Record.Properties.ContainsKey(p.Key))
                    continue;
                var shown = Unowned(p.Def);
                if (!_baseline.ContainsKey(p.Key))
                    p.InitBaseline(shown);
                if (p.RawValue != shown)
                    p.RawValue = shown;
            }
            RefreshExtensionInherited(keys);
        }
        finally
        {
            _squelchHistory = false;
        }
        RunPendingExtensionRules();
    }

    /// <summary>
    /// What a row shows for a key the asset doesn't hold: a derived asset's inherited value (what the game sees), else
    /// the schema default. Without this a derived asset's every unset key read as the default, and as an override.
    /// </summary>
    private string Unowned(PropertyDef def) => _parentValues?.GetValueOrDefault(def.Key) ?? def.Default;

    public AssetRecord Record { get; }

    /// <summary>Visual preview (image/material/model/anim types; a weapon while an extension with a preview module is on
    /// for it, which comes and goes with that extension); null for types without one.</summary>
    public PreviewPaneViewModel? PreviewPane
    {
        get => _previewPane;
        set => SetProperty(ref _previewPane, value);
    }

    private PreviewPaneViewModel? _previewPane;

    /// <summary>The shell viewmodel; lets popup menus (which cannot walk the visual tree) reach global commands.</summary>
    public MainViewModel? Owner { get; set; }

    public string Name => Record.Name;
    public bool IsWeapon => TypeStyles.IsWeaponType(Record.Type);
    public string TypeName => Record.Type;
    public string GdtName => Record.GdtName;
    public string? Parent => Record.Parent;
    public bool HasParent => Record.Parent is not null;

    /// <summary>The parent this asset is based on isn't in any loaded GDT: the header says so beside its name.</summary>
    public bool IsParentMissing => Record.Parent is { } parent && _resolve(Record.Type, parent) is null;
    public string Glyph => TypeStyles.Glyph(Record.Type);
    public IBrush TypeBrush => TypeStyles.Brush(Record.Type);
    public IBrush TypeDimBrush => TypeStyles.DimBrush(Record.Type);
    public int PropertyCount => AllSentinel.Count;

    /// <summary>"weapon in zm_weapons.gdt" — the header's second line, before the parent link.</summary>
    public string Provenance => $"{Record.Type} in {Record.GdtName}";

    /// <summary>The header's first line: where the asset lives, then what it is.</summary>
    public string Breadcrumb => $"{Record.GdtName}  /  {Record.Type}";

    public CategoryViewModel AllSentinel { get; }

    /// <summary>
    /// The rail's entries: the sections that hold at least one row the deffile shows for this asset (or one of its
    /// buttons), in rail order. A section that can't apply to the asset's current values isn't listed at all; one a
    /// filter or view empties stays, dimmed. Re-spliced (one notification per run of change) when the rules move.
    /// </summary>
    public RangeObservableCollection<CategoryViewModel> RailItems { get; } = new();

    // Every section in rail order (extensions first), and each one's place in it.
    private readonly List<CategoryViewModel> _railOrder;
    private readonly Dictionary<CategoryViewModel, int> _railIndex = new();

    /// <summary>The deffile shows something in the section for this asset: a row, or one of its buttons.</summary>
    private bool Applies(CategoryViewModel c) =>
        c.All.Any(p => !p.IsRuleHidden)
        || (_buttonsBySection.TryGetValue(c, out var buttons) && buttons.Any(b => b.IsShown && b.Buttons.Count > 0));

    /// <summary>Brings <see cref="RailItems"/> to the sections that apply, removing and inserting only the runs that differ.</summary>
    private void SyncRail()
    {
        var want = _railOrder.Where(Applies).ToList();
        var rail = RailItems;
        if (rail.Count == want.Count && rail.SequenceEqual(want))
            return;
        // Both are subsequences of the rail order, so one merge pass finds the runs.
        int i = 0, j = 0;
        while (i < rail.Count || j < want.Count)
        {
            if (i < rail.Count && j < want.Count && ReferenceEquals(rail[i], want[j]))
            {
                i++;
                j++;
            }
            else if (j == want.Count || (i < rail.Count && _railIndex[rail[i]] < _railIndex[want[j]]))
            {
                var limit = j < want.Count ? _railIndex[want[j]] : int.MaxValue;
                var end = i;
                while (end < rail.Count && _railIndex[rail[end]] < limit)
                    end++;
                rail.RemoveRange(i, end - i);
            }
            else
            {
                var limit = i < rail.Count ? _railIndex[rail[i]] : int.MaxValue;
                var start = j;
                while (j < want.Count && _railIndex[want[j]] < limit)
                    j++;
                rail.InsertRange(i, want.GetRange(start, j - start));
                i += j - start;
            }
        }
    }

    /// <summary>
    /// The flattened, filtered property list the editor renders: per category a
    /// <see cref="CategoryViewModel"/> header row followed by its visible
    /// <see cref="PropertyItemViewModel"/>s. One flat collection lets a single VirtualizingStackPanel
    /// realize only viewport rows — nested per-category ItemsControls can't virtualize, and a
    /// 1,300-property weapon would otherwise build ~15k live controls on open.
    /// </summary>
    public RangeObservableCollection<object> FlatRows { get; } = new();

    private readonly Dictionary<PropertyItemViewModel, CategoryViewModel> _categoryOf = new();

    // Sections whose rows the deffile files under sub-sections (xmodel's LODs), and each such row's title.
    private readonly HashSet<CategoryViewModel> _subsectioned = new();
    private readonly Dictionary<PropertyItemViewModel, SubsectionRowViewModel> _subsectionOf = new();

    /// <summary>The section a property row belongs to.</summary>
    public CategoryViewModel? CategoryOf(PropertyItemViewModel item) => _categoryOf.GetValueOrDefault(item);

    /// <summary>
    /// Rail selection. Selecting a section asks the view to scroll its header to the top
    /// (<see cref="ScrollToRequested"/>); the view reports the section in view back through
    /// <see cref="SetCurrentSection"/>. The All sentinel scrolls to the top of the form.
    /// </summary>
    [ObservableProperty]
    private CategoryViewModel? _selectedRail;

    /// <summary>Raised with the flat-list row to bring to the top of the viewport.</summary>
    public event Action<object>? ScrollToRequested;

    [ObservableProperty]
    private string _searchText = "";

    /// <summary>All / Changed / Problems view tabs above the form.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAllView), nameof(IsChangedView), nameof(IsProblemsView), nameof(IsOverridesView),
        nameof(IsSetView), nameof(ModifiedOnly), nameof(OverridesOnly))]
    private EditorView _view;

    public bool IsAllView { get => View == EditorView.All; set { if (value) View = EditorView.All; } }
    public bool IsChangedView { get => View == EditorView.Changed; set { if (value) View = EditorView.Changed; } }
    public bool IsProblemsView { get => View == EditorView.Problems; set { if (value) View = EditorView.Problems; } }
    public bool IsOverridesView { get => View == EditorView.Overrides; set { if (value) View = EditorView.Overrides; } }
    public bool IsSetView { get => View == EditorView.Set; set { if (value) View = EditorView.Set; } }

    /// <summary>Rows the asset holds a value for (the Set tab's count): shown ones, its own or inherited.</summary>
    [ObservableProperty]
    private int _setPropertyCount;

    /// <summary>
    /// Whether the asset holds a value for the row: a non-empty line in its own GDT, or its parent's (an extension's: in
    /// its sidecar, or inherited). A key the form merely shows at its default holds nothing.
    /// </summary>
    private bool HoldsValue(PropertyItemViewModel item)
    {
        if (_extensionOf.TryGetValue(item, out var ext))
        {
            var own = _extensionGdt?.Extensions?.Find(Record.Name, ext.Schema.Id)?.Properties;
            return Holds(ext, own, item) || ext.Inherited.ContainsKey(item.Key);
        }
        if (Record.Properties.TryGetValue(item.Key, out var value) && value.Length > 0)
            return true;
        return _parentValues?.GetValueOrDefault(item.Key) is { Length: > 0 };
    }

    /// <summary>The Set view's Add property: rows it doesn't list (not held, not put in by hand), in form order.</summary>
    public List<PropertyPick> UnsetRows(string query)
    {
        var picks = new List<PropertyPick>();
        foreach (var c in _categories)
            foreach (var p in c.All)
                if (!p.IsHeld && !p.IsRevealed && !p.IsRuleHidden && (query.Length == 0 || p.MatchesFilter(query)))
                    picks.Add(new PropertyPick(p, p.Label, p.Def.Subgroup.Length > 0 ? c.Name + " · " + p.Def.Subgroup : c.Name));
        return picks;
    }

    /// <summary>The Overrides view (derived assets only) by its older name.</summary>
    public bool OverridesOnly
    {
        get => View == EditorView.Overrides;
        set => View = value ? EditorView.Overrides : EditorView.All;
    }

    /// <summary>The Changed view by its older name (tooling).</summary>
    public bool ModifiedOnly
    {
        get => View == EditorView.Changed;
        set => View = value ? EditorView.Changed : EditorView.All;
    }

    /// <summary>Rows edited this session.</summary>
    [ObservableProperty]
    private int _modifiedCount;

    [ObservableProperty]
    private int _overrideCount;

    /// <summary>VS Code-style preview tab: italic, replaced by the next preview open; pinned by editing or re-opening.</summary>
    [ObservableProperty]
    private bool _isPreview;

    /// <summary>Where the user came from when this tab was opened by following a reference.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOpenedFrom))]
    private string? _openedFrom;

    public bool HasOpenedFrom => OpenedFrom is not null;

    [ObservableProperty]
    private int _visiblePropertyCount;

    /// <summary>The labels the form's label column shows right now (rows the deffile shows, and its button groups).</summary>
    public IEnumerable<string> FormLabels =>
        AllSentinel.All.Where(p => !p.IsRuleHidden && p is not (RecordsPropertyViewModel or PartsPropertyViewModel)).Select(p => p.Label)
            .Concat(_buttonRows.Values.Where(r => r.IsShown).Select(r => r.Title));

    /// <summary>Rows the deffile shows for this asset (the "All" count).</summary>
    public int ShownPropertyCount => AllSentinel.All.Count(p => !p.IsRuleHidden);

    /// <summary>The row whose details the Inspector shows (keyboard focus or last click).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFocusedProperty))]
    private PropertyItemViewModel? _focusedProperty;

    public bool HasFocusedProperty => FocusedProperty is not null;

    partial void OnFocusedPropertyChanged(PropertyItemViewModel? oldValue, PropertyItemViewModel? newValue)
    {
        if (oldValue is not null)
            oldValue.IsFocused = false;
        if (newValue is not null)
            newValue.IsFocused = true;
        Owner?.OnFocusedPropertyChanged(this);
    }

    public ObservableCollection<ProblemItem> Problems { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProblemSummary))]
    private int _problemCount;

    /// <summary>The Inspector's one line about the asset's problems ("58 problems on this asset").</summary>
    public string ProblemSummary => ProblemCount == 1 ? "1 problem on this asset" : $"{ProblemCount:N0} problems on this asset";

    /// <summary>The Inspector's "Show them": the form lists only the rows with problems.</summary>
    [RelayCommand]
    private void ShowProblems() => View = EditorView.Problems;

    /// <summary>This session's edits on this asset (old → new), in form order.</summary>
    public ObservableCollection<ChangeItem> Changes { get; } = new();

    partial void OnSelectedRailChanged(CategoryViewModel? value)
    {
        if (value is null)
            return;
        FlushPendingFilter();
        if (value.IsAll)
        {
            if (FlatRows.Count > 0)
                ScrollToRequested?.Invoke(FlatRows[0]);
            return;
        }
        // Picking a folded section in the rail is asking to see it.
        if (value.IsCollapsed)
        {
            value.SetExpanded(true);
            RefreshVisible();
        }
        if (FlatRows.Contains(value))
            ScrollToRequested?.Invoke(value);
    }

    /// <summary>Called by the view as the list scrolls: marks the section whose header is in view.</summary>
    public void SetCurrentSection(CategoryViewModel? section)
    {
        foreach (var c in _categories)
            c.IsCurrent = c == section;
    }

    /// <summary>Pause in typing before the filter applies; each keystroke would otherwise re-filter ~1,300 rows.</summary>
    private static readonly TimeSpan FilterDelay = TimeSpan.FromMilliseconds(150);

    private DispatcherTimer? _filterDelay;

    partial void OnSearchTextChanged(string value)
    {
        // Clearing (Esc, "Show all", reveal) applies at once; typing waits for a pause.
        if (value.Trim().Length == 0)
        {
            RefreshVisible();
            return;
        }
        if (_filterDelay is null)
        {
            _filterDelay = new DispatcherTimer { Interval = FilterDelay };
            _filterDelay.Tick += (_, _) => RefreshVisible();
        }
        _filterDelay.Stop();
        _filterDelay.Start();
    }

    partial void OnViewChanged(EditorView value) => RefreshVisible();

    /// <summary>Applies a filter still waiting out its typing delay, before anything reads FlatRows.</summary>
    private void FlushPendingFilter()
    {
        if (_filterDelay is { IsEnabled: true })
            RefreshVisible();
    }

    /// <summary>Moves row focus up or down the visible form (↑↓ walk; the Inspector follows).</summary>
    public PropertyItemViewModel? MoveFocus(int delta)
    {
        FlushPendingFilter();
        // One step per form row: a vector's components share a row, so ↓ leaves the vector (Tab walks its boxes).
        var rows = FlatRows.Where(r => r is PropertyItemViewModel or VectorRowViewModel).ToList();
        if (rows.Count == 0)
            return null;
        var index = FocusedProperty is null ? -1 : rows.IndexOf(RowFor(FocusedProperty));
        var next = rows[Math.Clamp(index < 0 && delta < 0 ? 0 : index + delta, 0, rows.Count - 1)];
        if (next is VectorRowViewModel vector)
        {
            // The same component of the next vector, as a spreadsheet's column would.
            var column = FocusedProperty is not null && _vectorOf.TryGetValue(FocusedProperty, out var from)
                ? from.Parts.ToList().IndexOf(FocusedProperty) : 0;
            FocusedProperty = vector.Parts[Math.Clamp(column, 0, vector.Parts.Count - 1)];
        }
        else
            FocusedProperty = (PropertyItemViewModel)next;
        return FocusedProperty;
    }

    /// <summary>Focuses a row by key, clearing the filter or view if either hides it.</summary>
    public PropertyItemViewModel? RevealProperty(string key)
    {
        var item = AllSentinel.All.FirstOrDefault(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (item is null)
            return null;
        FlushPendingFilter();
        if (_categoryOf.GetValueOrDefault(item) is { IsCollapsed: true } folded)
        {
            folded.SetExpanded(true);
            RefreshVisible();
        }
        // The deffile hides it for the asset's current values: no filter or view hides it, so none is cleared for it.
        if (item.IsRuleHidden)
            return null;
        // An unset key asked for in the Set view joins it: the view stays.
        if (View == EditorView.Set && !item.IsHeld && !item.IsRevealed)
        {
            item.IsRevealed = true;
            RefreshVisible();
        }
        if (!IsShown(item) && _categoryOf.GetValueOrDefault(item) is { } section)
        {
            // Only what hides it goes: the view if the row isn't in it, the filter if the row doesn't match it.
            if (!CategoryViewModel.Passes(item, "", View))
                View = EditorView.All;
            if (!section.Shows(item, SearchText.Trim(), View))
                SearchText = "";
        }
        FocusedProperty = item;
        ScrollToRequested?.Invoke(item);
        return item;
    }

    private void OnPropertyEdited(PropertyItemViewModel item)
    {
        var after = item.RawValue;
        _extensionOf.TryGetValue(item, out var extension);
        if (extension is not null)
        {
            if (!_squelchHistory)
                WriteExtensionEdit(item, extension, after);
        }
        else if (!_squelchHistory)
        {
            // A derived asset brought back to its inherited value, on a key it didn't own when the session began,
            // inherits again (no line in the GDT) rather than holding a copy: what the row shows and what is saved agree.
            var write = Record.Parent is not null && !_baseline.ContainsKey(item.Key) && after == Unowned(item.Def) ? null : after;
            // Rapid consecutive scrub ticks and steps on one field coalesce into one step.
            var before = EditHistory.Set(Record, item.Key, write);
            if ((before ?? Unowned(item.Def)) != after)
            {
                Record.History.RecordEdit(item.Key, before, write, item.IsContinuousEdit);
                NotifyHistoryChanged();
                Owner?.OnNewValueEdit();
            }
        }
        PreviewPane?.NotifyEdited(item.Key);
        Owner?.NotifyPreviewEdited(this, item);
        item.Problem = Check(item);
        ValueEdited?.Invoke(item);
        _categoryOf.TryGetValue(item, out var owner);
        // Editing a preview tab commits it, exactly like VS Code.
        IsPreview = false;

        var held = HoldsValue(item);
        if (held != item.IsHeld)
        {
            item.IsHeld = held;
            if (!item.IsRuleHidden)
                SetPropertyCount += held ? 1 : -1;
        }
        // Cleared in the Set view: the row stays where the user is working; it goes at the next refresh of the view.
        if (!held && View == EditorView.Set)
            item.IsRevealed = true;
        UpdateRowCounts(item, owner);
        UpdateProblemRow(item, owner);
        UpdateChangeRow(item);
        // Deffile display rules: re-evaluate only when the edited key is one the script actually
        // reads (its read-set) — for bulletweapon that's a handful of keys, so ordinary typing
        // never pays the ~2 ms re-interpretation.
        if (_overlay is { } overlay && overlay.AffectedBy(item.Key))
        {
            if (_squelchHistory)
                _rulesPending = true; // several keys at once (undo, a deffile button): the rules run once, after
            else if (EvaluateDisplayRules())
                RefreshVisible();
        }
        // An extension's switch and visibleWhen rules read only its own values: a few dozen rows, re-read on its edits.
        if (extension is not null)
        {
            // A folded section that now holds a value opens (an undo or a paste can put one there).
            var opened = owner is { IsCollapsible: true } && RecountSet(extension, owner);
            if (_squelchHistory)
            {
                _extensionRulesPending = true;
                _sectionOpened |= opened;
            }
            else if (EvaluateExtensionRules() | opened)
                RefreshVisible();
        }
        // With a filter or view active an edit can change whether this row still matches, but
        // rebuilding the whole flat list per keystroke re-realizes every viewport control (~160 ms).
        // Only rebuild when this item's visibility actually flips — and never pull the row the
        // user is typing into out from under them.
        if ((View != EditorView.All || SearchText.Length > 0)
            && owner is not null
            && !ReferenceEquals(item, FocusedProperty)
            && owner.Visible.Contains(item) != owner.Shows(item, SearchText.Trim(), View))
            RefreshVisible();
        if (IsAnim && owner is not null && !IsNotetrackSection(owner.Name))
            RefreshPanelCounts();
        if (_squelchHistory)
            return;
        LastEditedKeys = new[] { item.Key };
        _onEdited(this);
    }

    /// <summary>The keys the last edit, undo or redo touched (null: unknown, any of them).</summary>
    public IReadOnlyList<string>? LastEditedKeys { get; private set; }

    private void RebuildChanges()
    {
        Changes.Clear();
        _changeRows.Clear();
        foreach (var p in AllSentinel.All)
            if (p.IsChanged)
            {
                Changes.Add(NewChange(p));
                _changeRows.Add(p);
            }
    }

    /// <summary>
    /// A row's problem: its own table's rows for a record table, else the value against its definition (a choice against
    /// the options the deffile offers this asset).
    /// </summary>
    private string? Check(PropertyItemViewModel item) => item switch
    {
        RecordsPropertyViewModel table => table.Validate(),
        PartsPropertyViewModel parts => parts.Validate(),
        ChoicePropertyViewModel choice => Validator.Check(item.Def, item.RawValue, AssetExists, options: choice.Options),
        _ => Validator.Check(item.Def, item.RawValue, AssetExists),
    };

    private static ChangeItem NewChange(PropertyItemViewModel p)
    {
        if (p is RecordsPropertyViewModel table)
        {
            var (old, now) = table.Describe(p.BaselineValue, p.RawValue);
            return new ChangeItem(p, old, now);
        }
        if (p is PartsPropertyViewModel parts)
        {
            var (old, now) = parts.Describe(p.BaselineValue, p.RawValue);
            return new ChangeItem(p, old, now);
        }
        return new ChangeItem(p, Show(p.BaselineValue), Show(p.RawValue));

        static string Show(string v) => v.Length == 0 ? "(empty)" : v;
    }

    private void RebuildProblems()
    {
        Problems.Clear();
        _problemRows.Clear();
        foreach (var c in _categories)
            c.ProblemCount = 0;
        foreach (var p in AllSentinel.All)
            if (CountedProblem(p) is { } message)
            {
                Problems.Add(new ProblemItem(p.Label, p.Key, message));
                _problemRows.Add(p);
                if (_categoryOf.TryGetValue(p, out var c))
                    c.ProblemCount++;
            }
        ProblemCount = Problems.Count;
    }

    // ── Deffile buttons (APE's AddEntry_ButtonGroup) ─────────────────────────
    // Which groups show, and their buttons, come from this asset's GenerateUI run (the overlay), so they follow edits:
    // a list's "Add Item" moves down as items are added. Each sits right above the property the deffile registered
    // after it, or at the end of its section.

    private readonly Dictionary<string, CategoryViewModel> _sectionByName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ButtonGroupRowViewModel> _buttonRows = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<CategoryViewModel, List<ButtonGroupRowViewModel>> _buttonsBySection = new();
    private readonly Dictionary<ButtonGroupRowViewModel, CategoryViewModel> _sectionOfButton = new();
    private bool _buttonOrderMoved;
    private string? _buttonRunning; // the button running on this asset, if one is

    /// <summary>The last button's script time (off the UI thread), its apply time on the UI thread, and click to applied.</summary>
    public (double ScriptMs, double ApplyMs, double TotalMs) LastButtonTimings { get; private set; }

    /// <summary>Every button group row this asset has shown (some may be hidden now).</summary>
    public IEnumerable<ButtonGroupRowViewModel> ButtonRows => _buttonRows.Values;

    /// <summary>Buttons the form shows right now, in form order (the palette offers them).</summary>
    public IEnumerable<DeffileButtonViewModel> VisibleButtons =>
        FlatRows.OfType<ButtonGroupRowViewModel>().Where(r => r.IsEnabled).SelectMany(r => r.Buttons);

    /// <summary>Brings the button rows to the overlay's groups; true when one appeared, went, or moved.</summary>
    private bool SyncButtonRows()
    {
        var groups = _overlay?.Buttons ?? [];
        if (groups.Count == 0 && _buttonRows.Count == 0)
            return false;
        var changed = false;
        var seen = new HashSet<ButtonGroupRowViewModel>();
        var reorder = new HashSet<CategoryViewModel>();
        foreach (var group in groups)
        {
            // A section titled by a value (scriptbundle's "Object 1 - dragon") keeps the name it opened with: the group
            // goes where the property it sits above is.
            if (!_sectionByName.TryGetValue(group.Category, out var section)
                && !(group.AnchorKey is { } key && _rowByKey.TryGetValue(key, out var anchorRow) && _categoryOf.TryGetValue(anchorRow, out section)))
                continue;
            if (!_buttonRows.TryGetValue(group.Name, out var row))
            {
                row = new ButtonGroupRowViewModel(group.Name, group.Category, (r, b) => _ = RunButtonAsync(r, b));
                _buttonRows[group.Name] = row;
            }
            if (_sectionOfButton.GetValueOrDefault(row) != section)
            {
                if (_sectionOfButton.TryGetValue(row, out var old))
                {
                    _buttonsBySection[old].Remove(row);
                    reorder.Add(old);
                }
                _sectionOfButton[row] = section;
                if (!_buttonsBySection.TryGetValue(section, out var list))
                    _buttonsBySection[section] = list = new();
                list.Add(row);
                reorder.Add(section);
            }
            var wasShown = row.IsShown;
            var anchor = row.AnchorKey;
            row.Update(group);
            seen.Add(row);
            changed |= wasShown != row.IsShown;
            if (!string.Equals(anchor, row.AnchorKey, StringComparison.OrdinalIgnoreCase))
                reorder.Add(section);
        }
        foreach (var row in _buttonRows.Values)
            if (!seen.Contains(row) && row.IsShown)
            {
                row.IsShown = false;
                changed = true;
            }
        foreach (var section in reorder)
            changed |= OrderButtonRows(section);
        return changed;
    }

    /// <summary>Places a section's button rows between its property rows; true when a placed row moved.</summary>
    private bool OrderButtonRows(CategoryViewModel section)
    {
        var end = section.All.Count > 0 ? _rowOrder[section.All[^1]] : _rowOrder[section];
        var next = new Dictionary<int, int>();
        var moved = false;
        foreach (var row in _buttonsBySection[section])
        {
            var at = row.AnchorKey is { } key && _rowByKey.TryGetValue(key, out var anchor) && _categoryOf.GetValueOrDefault(anchor) == section
                ? _rowOrder[anchor] - RowGap / 2
                : end + 1;
            var slot = at + (next[at] = next.GetValueOrDefault(at, -1) + 1);
            if (_rowOrder.TryGetValue(row, out var old) && old != slot && FlatRows.Contains(row))
                moved = true;
            _rowOrder[row] = slot;
        }
        _buttonOrderMoved |= moved;
        return moved;
    }

    /// <summary>A section's button rows the form shows: in the All view, and under a filter only beside its matches.</summary>
    private List<ButtonGroupRowViewModel> VisibleButtonRows(CategoryViewModel section, string query)
    {
        if (!_buttonsBySection.TryGetValue(section, out var rows) || View != EditorView.All
            || (query.Length > 0 && section.Visible.Count == 0))
            return [];
        return rows.Where(r => r.IsShown && r.Buttons.Count > 0).ToList();
    }

    /// <summary>
    /// Runs a deffile button: its callback reads the asset's current values and its writes come back as one undo step,
    /// through the same path as undo (so counts, problems, rules and the preview follow). The script runs off the UI
    /// thread: a question it asks waits there for the answer while the window stays live. Never throws: whatever goes
    /// wrong is said in the banner.
    /// </summary>
    public async Task RunButtonAsync(ButtonGroupRowViewModel row, DeffileButton button)
    {
        if (_buttonRunning is { } running)
        {
            // One click at a time per asset: the one waiting may still change what this one would read.
            if (Owner is { } busy)
                busy.Status = $"{running} is still running. Click {button.Label} again when it's done.";
            return;
        }
        string what = button.Label;
        _buttonRunning = what;
        _buttonApplying = false;
        try
        {
            what = new DeffileButtonViewModel(row, button, null).Title;
            _buttonRunning = what;
            await RunButtonCoreAsync(what, button);
        }
        catch (Exception ex)
        {
            try
            {
                Owner?.ShowNotice(_buttonApplying
                    ? $"{what}: something went wrong while its values were being applied. Check them; {CommandCatalog.Get(CommandCatalog.Undo).GestureText} undoes what landed."
                    : $"{what}: Apex couldn't apply what the deffile did. Nothing changed.", isError: true, detail: ex.Message);
            }
            catch (Exception)
            {
                // The banner itself failed: there is nowhere left to say it.
            }
        }
        finally
        {
            _buttonRunning = null;
        }
    }

    // Set once a click's values start landing on the record: a failure after that can't say "nothing changed".
    private bool _buttonApplying;

    /// <summary>The asset's values and each ancestor's, as they are now (a click applies only if none moved while it ran).</summary>
    private List<(AssetRecord Record, Dictionary<string, string> Values)> ChainSnapshot()
    {
        var chain = new List<(AssetRecord, Dictionary<string, string>)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var cur = Record; cur is not null && seen.Add(cur.Name); cur = cur.Parent is { } p ? _resolve(Record.Type, p) : null)
            chain.Add((cur, new Dictionary<string, string>(cur.Properties, StringComparer.OrdinalIgnoreCase)));
        return chain;
    }

    /// <summary>The first asset in <paramref name="chain"/> whose values differ from now, and a key that did; null when none.</summary>
    private static (AssetRecord Record, string Key)? Moved(List<(AssetRecord Record, Dictionary<string, string> Values)> chain)
    {
        foreach (var (record, values) in chain)
        {
            var now = record.Properties;
            foreach (var (key, value) in values)
                if (!now.TryGetValue(key, out var v) || v != value)
                    return (record, key);
            foreach (var key in now.Keys)
                if (!values.ContainsKey(key))
                    return (record, key);
        }
        return null;
    }

    private async Task RunButtonCoreAsync(string what, DeffileButton button)
    {
        var schema = SchemaRegistry.Get(Record.Type);
        var effective = new Dictionary<string, string>(EffectiveValues(), StringComparer.OrdinalIgnoreCase);
        var parents = _parentValues is null ? null : new Dictionary<string, string>(_parentValues, StringComparer.OrdinalIgnoreCase);
        var unsaved = _overlay?.UnsavedValues;
        var registered = _overlay?.Rules;
        string DefaultOf(string key) => schema?.Find(key)?.Default ?? unsaved?.GetValueOrDefault(key) ?? "";
        string ValueOf(string key) => effective.TryGetValue(key, out var v) ? v : DefaultOf(key);
        string InheritedOf(string key) => parents?.TryGetValue(key, out var v) == true ? v : DefaultOf(key);
        var services = new ButtonServices(Owner);
        var chain = ChainSnapshot();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        double scriptMs = 0;
        var type = Record.Type;
        var name = Record.Name;
        var run = await Task.Run(() =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = GdfRuntime.RunButton(type, name, effective, ValueOf, InheritedOf, button, services);
            scriptMs = sw.Elapsed.TotalMilliseconds;
            return result;
        });
        if (run is null)
            return;
        var applying = System.Diagnostics.Stopwatch.StartNew();
        var owner = Owner;

        // Nothing lands on a tab that closed, or on values that moved while the script waited on a question: it decided
        // from the values it read, and applying them now would overwrite the newer edit.
        if (owner is not null && !owner.OpenTabs.Contains(this))
        {
            owner.ShowNotice($"{what}: the tab closed before the deffile finished, so nothing was applied.");
            return;
        }
        if (services.Refused is { } refused)
        {
            owner?.ShowNotice($"{what}: {refused} Nothing changed; click it again.");
            return;
        }
        // Any change to this asset or an ancestor counts: GenerateUI ran ahead of the callback and its globals (a
        // scriptbundle's object count) came from values the callback never reads itself.
        if (Moved(chain) is { } moved)
        {
            var label = moved.Record == Record ? _rowByKey.GetValueOrDefault(moved.Key)?.Label ?? moved.Key : moved.Record.Name;
            owner?.ShowNotice($"{what}: {label} changed while the deffile was waiting, so nothing was applied. Click it again.");
            return;
        }

        var candidates = new List<(string Key, string? Target)>();
        HashSet<string>? offSchema = null;
        foreach (var key in run.Written.Keys.Concat(run.Cleared.Where(k => !run.Written.ContainsKey(k))))
        {
            var def = schema?.Find(key);
            string? target = null;
            if (run.Written.TryGetValue(key, out var value))
            {
                // A number or switch entry holds a number or a switch, whatever text it's given (lensflare's Remove
                // copies from an image past the last, which reads as nothing).
                target = def?.Kind switch
                {
                    PropertyKind.Number when !double.TryParse(value, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out _) => "0",
                    PropertyKind.Toggle when value is not ("0" or "1") => GdfValue.AsBool(value) ? "1" : "0",
                    _ => value,
                };
                // ClearSpecified then set to what it inherits anyway: it stays un-specified.
                if (run.Cleared.Contains(key) && AssetQuery.ValuesEqual(InheritedOf(key), target))
                    target = null;
            }
            // ClearSpecified alone: the asset stops specifying the key, and inherits it.
            if (target is null ? !Record.Properties.ContainsKey(key) : AssetQuery.ValuesEqual(ValueOf(key), target))
                continue;
            candidates.Add((key, target));
            if (target is not null && def is null && !Record.Properties.ContainsKey(key))
                (offSchema ??= new(StringComparer.OrdinalIgnoreCase)).Add(key);
        }
        // Only values the asset keeps: a group's own value (numLods) or a SetSave(false) entry stays in the run. An entry
        // the schema lacks is kept when the asset's run after the click saves it (a scriptbundle's new second object).
        string? fallbackNote = null;
        if (offSchema is not null)
        {
            var after = new Dictionary<string, string>(effective, StringComparer.OrdinalIgnoreCase);
            foreach (var (key, target) in candidates)
                if (target is null)
                    after.Remove(key);
                else
                    after[key] = target;
            if (GdfRuntime.EvaluateOverlay(Record.Type, Record.Name, after)?.SavedOffSchema is { } saved)
                candidates.RemoveAll(c => offSchema.Contains(c.Key) && !saved.Contains(c.Key));
            else
            {
                // The deffile's run failed after the click, so which of them it saves is unknown: keep what its run
                // before the click had as entries, and say so.
                candidates.RemoveAll(c => offSchema.Contains(c.Key) && registered?.ContainsKey(c.Key) != true);
                var kept = offSchema.Count(k => candidates.Any(c => c.Key == k));
                fallbackNote = "Apex couldn't re-run the deffile to check which of its new values it saves, so " + (kept switch
                {
                    0 => "it kept none of them.",
                    1 => "it kept the one it has an entry for.",
                    _ => $"it kept the {kept} it has entries for.",
                });
            }
        }
        var changes = new List<PropertyChange>(candidates.Count);
        _buttonApplying = candidates.Count > 0;
        try
        {
            foreach (var (key, target) in candidates)
                changes.Add(new PropertyChange(key, EditHistory.Set(Record, key, target), target));
        }
        catch when (changes.Count > 0)
        {
            // What landed before the failure is still one undo step, so the notice's "undoes what landed" holds.
            Record.History.RecordStep(new EditStep(changes));
            throw;
        }
        if (changes.Count > 0)
        {
            Record.History.RecordStep(new EditStep(changes));
            IsPreview = false;
            SyncFromRecord();
            RefreshVisible();
            _onEdited(this);
        }
        LastButtonTimings = (scriptMs, applying.Elapsed.TotalMilliseconds, clock.Elapsed.TotalMilliseconds);
        LastHistoryText = changes.Count == 0
            ? $"{what}: nothing to change"
            : $"{what}: {changes.Count} value{(changes.Count == 1 ? "" : "s")} changed";
        if (owner is not null)
        {
            owner.Status = changes.Count == 0 ? LastHistoryText
                : $"{LastHistoryText}. {CommandCatalog.Get(CommandCatalog.Undo).GestureText} undoes it.";
            // Everything the run has to say, in one banner: why it stopped first, then the deffile's own messages.
            var said = new List<string>();
            if (run.Error is not null)
                said.Add(changes.Count == 0 ? $"{what} stopped: the deffile's script failed." : $"{what} stopped partway: the deffile's script failed. What it did before that is applied.");
            if (services.ShowModelInfo)
                said.Add(owner.CurrentPreview?.Content is ModelPreviewViewModel { Stats: { } stats }
                    ? $"{Name}: {stats}"
                    : $"{Name}: model info shows in the preview once the model has loaded.");
            said.AddRange(services.Notices);
            if (fallbackNote is not null)
                said.Add(fallbackNote);
            if (said.Count > 0)
                owner.ShowNotice(string.Join(" ", said), isError: run.Error is not null, detail: run.Error);
        }
        if (run.ScrollTo is { } scrollTo)
            RevealProperty(scrollTo);
    }

    /// <summary>The most questions or messages one click may put up (a script stuck asking would otherwise never end).</summary>
    public const int MaxButtonMessages = 20;

    /// <summary>The app's side of a running callback: its message boxes and XModel info.</summary>
    private sealed class ButtonServices(MainViewModel? owner) : IDeffileButtonServices
    {
        public readonly List<string> Notices = new();
        private readonly System.Diagnostics.Stopwatch _waiting = new();
        private int _messages;

        public bool ShowModelInfo { get; private set; }

        /// <summary>Why a question couldn't be put (another is open); the run is then dropped.</summary>
        public string? Refused { get; private set; }

        public TimeSpan Waited => _waiting.Elapsed;

        public string MessageBox(string text, string buttons)
        {
            if (++_messages > MaxButtonMessages)
                throw new InvalidOperationException($"the script put up more than {MaxButtonMessages} messages");
            if (!buttons.Equals("YESNO", StringComparison.OrdinalIgnoreCase))
            {
                lock (Notices)
                    Notices.Add(text);
                return "OK";
            }
            // "Are you sure you want to remove Medal 3?": what follows is one undo step, so Apex doesn't ask.
            if (text.StartsWith("Are you sure", StringComparison.OrdinalIgnoreCase))
                return "YES";
            if (owner is null || Dispatcher.UIThread.CheckAccess())
                return "NO";
            var answer = new TaskCompletionSource<bool?>(TaskCreationOptions.RunContinuationsAsynchronously);
            Dispatcher.UIThread.Post(() =>
            {
                // The dialog is the user's (a delete, another tab's question): it is never replaced.
                if (owner.IsConfirmOpen)
                {
                    answer.TrySetResult(null);
                    return;
                }
                owner.AskConfirm(text,
                    $"The deffile asks before it goes on. {CommandCatalog.Get(CommandCatalog.Undo).GestureText} undoes whatever it changes.",
                    "Yes", () => answer.TrySetResult(true), destructive: false,
                    onCancel: replaced => answer.TrySetResult(replaced ? null : false));
            });
            _waiting.Start();
            try
            {
                if (answer.Task.GetAwaiter().GetResult() is { } yes)
                    return yes ? "YES" : "NO";
            }
            finally
            {
                _waiting.Stop();
            }
            Refused = "another question was open when the deffile asked, so it wasn't put.";
            throw new InvalidOperationException(Refused);
        }

        public void ShowXModelInfo(string assetName) => ShowModelInfo = true;
    }

    // ── Undo / redo ──────────────────────────────────────────────────────────
    // The history belongs to the record, not the tab: table cells, bulk apply and compare's take
    // record into it too, and a closed and reopened tab keeps it.
    public bool CanUndo => Record.History.CanUndo;
    public bool CanRedo => Record.History.CanRedo;

    /// <summary>What the last undo or redo did, for the status bar.</summary>
    public string LastHistoryText { get; private set; } = "";

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        var shown = RowOfStep(Record.History.NextUndo);
        if (Record.History.Undo() is { } step)
            AfterHistory(step, undone: true, shown);
    }

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo()
    {
        var shown = RowOfStep(Record.History.NextRedo);
        if (Record.History.Redo() is { } step)
            AfterHistory(step, undone: false, shown);
    }

    /// <summary>The one row every change of a step belongs to (a table's keys, say), with what it shows before the step runs.</summary>
    private (PropertyItemViewModel Row, string Value)? RowOfStep(EditStep? step)
    {
        if (step is null || RowOfKey(step.Changes[0].Key) is not { } row || step.Changes.Any(c => RowOfKey(c.Key) != row))
            return null;
        return (row, row.RawValue);
    }

    /// <summary>Brings the rows to the values the step just wrote, then shows the row it touched.</summary>
    private void AfterHistory(EditStep step, bool undone, (PropertyItemViewModel Row, string Value)? shown = null)
    {
        SyncFromRecord();
        LastEditedKeys = step.Changes.Select(c => c.Key).ToList();
        _onEdited(this);
        var last = step.Changes[^1];
        var row = RowOfKey(last.Key);
        var label = row?.Label ?? last.Key;
        var verb = undone ? "Undid" : "Redid";
        // A table's or a value in parts' step is said in words, never as its stored text.
        var said = shown is { } s && s.Row == row
            ? row.DescribeStep(undone ? row.RawValue : s.Value, undone ? s.Value : row.RawValue)
            : null;
        LastHistoryText = said is not null ? $"{verb} {said}"
            : step.Changes.Count == 1
            ? $"{verb} {label}: {Show(undone ? last.Before : last.After, row)}"
            : $"{verb} {step.Changes.Count} values on {Name}";
        if (row is not null)
            RevealProperty(row.Key);

        static string Show(string? value, PropertyItemViewModel? row)
        {
            value ??= row?.Def.Default ?? "";
            return value.Length == 0 ? "(empty)" : value;
        }
    }

    /// <summary>
    /// After a save wrote this asset: rows show what the GDT holds now (another program's change to a key this session
    /// didn't touch included), and "changed" is measured against it. Undo history stays.
    /// </summary>
    public void RebaseChanges()
    {
        SyncFromRecord();
        _baseline = Record.SessionBaseline ?? _baseline;
        RefreshExtensionBaselines();
        foreach (var p in AllSentinel.All)
        {
            p.InitBaseline(_extensionOf.TryGetValue(p, out var ext)
                ? BaselineOf(ext, p)
                : _baseline.GetValueOrDefault(p.Key) ?? Unowned(p.Def));
            _categoryOf.TryGetValue(p, out var owner);
            UpdateRowCounts(p, owner);
            UpdateChangeRow(p);
        }
        if (View == EditorView.Changed)
            RefreshVisible();
    }

    public void NotifyHistoryChanged()
    {
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
    }

    /// <summary>
    /// Re-reads item values from the record after it changed outside the rows (undo, table mode,
    /// compare, refactors). Each row that differs goes through the ordinary edit path, which keeps
    /// counts, problems, display rules and the preview current for that key — so a record nobody
    /// touched costs one compare per row. Only a key the editor has no row for (added after open)
    /// falls back to re-running the rules and reloading the preview wholesale.
    /// </summary>
    public void SyncFromRecord()
    {
        _squelchHistory = true;
        try
        {
            foreach (var p in AllSentinel.All)
            {
                var v = _extensionOf.TryGetValue(p, out var ext)
                    ? ExtensionValue(ext, p)
                    : Record.Properties.GetValueOrDefault(p.Key) ?? Unowned(p.Def);
                if (p.RawValue != v)
                    p.RawValue = v;
            }
        }
        finally
        {
            _squelchHistory = false;
        }
        RunPendingExtensionRules();
        var rulesRun = false;
        if (_rulesPending)
        {
            _rulesPending = false;
            rulesRun = true;
            if (EvaluateDisplayRules())
                RefreshVisible();
        }
        foreach (var key in Record.Properties.Keys)
        {
            if (_rowByKey.ContainsKey(key))
                continue;
            if (!rulesRun && EvaluateDisplayRules())
                RefreshVisible();
            PreviewPane?.NotifyExternalChange();
            break;
        }
        NotifyHistoryChanged();
    }

    // ── Rename (command lives on the tab so the dialog can bind to it) ───────
    private readonly Action<AssetEditorViewModel>? _onRename;

    [ObservableProperty]
    private string _renameText;

    /// <summary>Asset names are GDT identifiers: lowercase, digits, underscores. Space types an underscore.</summary>
    partial void OnRenameTextChanged(string value)
    {
        var sb = new System.Text.StringBuilder(value.Length);
        foreach (var raw in value)
        {
            var c = char.ToLowerInvariant(raw);
            if (c is ' ' or '-')
                sb.Append('_');
            else if (c is '_' or (>= 'a' and <= 'z') or (>= '0' and <= '9'))
                sb.Append(c);
        }
        var sanitized = sb.ToString();
        if (sanitized != value)
            RenameText = sanitized;
    }

    [RelayCommand]
    private void Rename() => _onRename?.Invoke(this);

    /// <summary>Refreshes name-derived bindings after a rename refactor.</summary>
    public void NotifyRenamed()
    {
        RenameText = Record.Name;
        OnPropertyChanged(nameof(Name));
    }

    /// <summary>The asset moved to another GDT (Move to…, Cut and Paste, or their undo).</summary>
    public void NotifyMoved()
    {
        OnPropertyChanged(nameof(GdtName));
        OnPropertyChanged(nameof(Provenance));
        OnPropertyChanged(nameof(Breadcrumb));
        if (_extensions is not null && _gdtOf?.Invoke(Record) is { } gdt && gdt != _extensionGdt)
        {
            _extensionGdt = gdt;
            RebaseChanges();
        }
    }

    private void RecountModified()
    {
        _changedRows.Clear();
        foreach (var c in _categories)
        {
            c.RecountModified();
            foreach (var p in c.All)
                if (p.IsChanged)
                    _changedRows.Add(p);
        }
        AllSentinel.ModifiedCount = ModifiedCount = _changedRows.Count;
    }

    // A section's grid, as the one row that shows it while the matrix is on (the keys stay its rows underneath).
    private readonly Dictionary<CategoryViewModel, MatrixRowViewModel> _matrixOf = new();

    private void MakeMatrix(CategoryViewModel c)
    {
        // Extensions' sections are the manifest's own layout; a long section is a form, not a table of a few names.
        if (c.IsExtension || c.All.Count > 400)
            return;
        var grid = KeyGridDetector.Detect(c.Name, c.All.Select(p => p.Def).ToList());
        if (grid is null)
            return;
        var byKey = c.All.ToDictionary(p => p.Key, StringComparer.OrdinalIgnoreCase);
        var matrix = new MatrixRowViewModel(grid, key => byKey.GetValueOrDefault(key));
        c.Grid = grid;
        _matrixOf[c] = matrix;
        // Between the section's header and its first row.
        _rowOrder[matrix] = _rowOrder[c] + 1;
        c.MatrixChanged += _ => RefreshVisible();
    }

    /// <summary>The visible rows (a vector's components as its one row) with each sub-section's title before its first.</summary>
    private List<object> WithSubsections(List<PropertyItemViewModel> visible)
    {
        var rows = new List<object>();
        var seen = new HashSet<object>();
        SubsectionRowViewModel? last = null;
        foreach (var p in visible)
        {
            var row = _vectorOf.Count > 0 ? RowFor(p) : p;
            if (!seen.Add(row))
                continue;
            var run = _subsectionOf.GetValueOrDefault(p);
            if (run is not null && !ReferenceEquals(run, last))
                rows.Add(run);
            last = run;
            rows.Add(row);
        }
        return rows;
    }

    private void RefreshVisible()
    {
        _filterDelay?.Stop();
        var query = SearchText.Trim();

        var rows = new List<object>();
        var visibleTotal = 0;
        var buttonTotal = 0;
        var first = true;
        if (_extensions is not null)
            rows.AddRange(ExtensionOffRows());
        foreach (var c in _categories)
        {
            c.ApplyFilter(query, View);
            c.IsFirst = false;
            var buttons = VisibleButtonRows(c, query);
            if (c.Visible.Count == 0 && buttons.Count == 0)
                continue;
            c.IsFirst = first && rows.Count == 0;
            first = false;
            visibleTotal += c.Visible.Count;
            buttonTotal += buttons.Count;
            rows.Add(c);
            if (_extensions is not null && c.IsExtension)
            {
                rows.AddRange(ExtensionNoticeRows(c));
                // Folded only in the plain form: a filter or another view shows every row it finds.
                if (c.IsCollapsed && query.Length == 0 && View == EditorView.All)
                    continue;
            }
            // A grid shows as a matrix, plain (no filter) and whole (the All and Set views); keys outside it stay rows.
            if (_matrixOf.TryGetValue(c, out var matrix) && c.IsMatrix && query.Length == 0 && View is EditorView.All or EditorView.Set)
            {
                matrix.Refresh();
                var inGrid = matrix.Items.ToHashSet();
                rows.Add(matrix);
                rows.AddRange(c.Visible.Where(p => !inGrid.Contains(p)).Select(p => (object)RowFor(p)).Distinct());
                continue;
            }
            // A vector's components show as its one row, where its first shown component would be.
            IEnumerable<object> shown = c.Visible;
            if (_vectorOf.Count > 0)
                shown = c.Visible.Select(RowFor).Distinct();
            if (_subsectioned.Contains(c))
                shown = WithSubsections(c.Visible);
            if (buttons.Count == 0)
                rows.AddRange(shown);
            else
                rows.AddRange(shown.Concat(buttons).OrderBy(r => _rowOrder[r]));
        }
        if (_buttonOrderMoved)
        {
            // A button row changed places: the splice's merge needs every row it keeps where it was.
            _buttonOrderMoved = false;
            FlatRows.RemoveRange(0, FlatRows.Count);
        }
        SpliceFlatRows(rows);
        SyncRail();
        SetPropertyCount = AllSentinel.All.Count(p => p.IsHeld && !p.IsRuleHidden);
        VisiblePropertyCount = visibleTotal;
        IsFormEmpty = visibleTotal + buttonTotal == 0;
        if (visibleTotal == 0)
            EmptyText = DescribeEmpty(query);
        OnPropertyChanged(nameof(ShownPropertyCount));
        if (IsAnim)
            RefreshPanel();
    }

    /// <summary>No row shows at all (a section with only a deffile button still shows).</summary>
    [ObservableProperty]
    private bool _isFormEmpty;

    /// <summary>What the empty form says: why nothing shows, including matches the deffile's rules hide.</summary>
    [ObservableProperty]
    private string _emptyText = "";

    private string DescribeEmpty(string query)
    {
        var hidden = AllSentinel.All.Count(p => p.IsRuleHidden && p.Def.Extension.Length == 0 && CategoryViewModel.Passes(p, query, View, ignoreRules: true));
        var hiddenNote = hidden == 0
            ? ""
            : $" {hidden:N0} hidden propert{(hidden == 1 ? "y matches" : "ies match")}: this type's deffile hides {(hidden == 1 ? "it" : "them")} for the asset's current values.";
        if (_extensions is not null)
            hiddenNote += DescribeHiddenExtensionFields(query);
        if (query.Length > 0)
            return "No properties match the filter." + hiddenNote;
        return View switch
        {
            EditorView.Changed => "Nothing changed on this asset yet in this session.",
            EditorView.Problems => "No validation problems on this asset.",
            EditorView.Overrides => "This asset overrides nothing: every value comes from its parent.",
            EditorView.Set => "Nothing is set on this asset yet. Add a property, or switch to All.",
            _ => "This type's deffile shows no properties for this asset." + hiddenNote,
        };
    }

    /// <summary>Above this many separate runs a splice costs more than rebuilding the list outright.</summary>
    private const int MaxSpliceRuns = 24;

    /// <summary>
    /// Brings FlatRows to <paramref name="rows"/> by removing and inserting only the runs that differ.
    /// Both are subsequences of one form order, so a single merge pass finds them. Rows that stay keep
    /// their realized containers: narrowing a filter by a letter, or one row leaving the Changed view,
    /// no longer re-templates every row in the viewport.
    /// </summary>
    private void SpliceFlatRows(List<object> rows)
    {
        if (CountRuns(FlatRows, rows) > MaxSpliceRuns)
        {
            FlatRows.RemoveRange(0, FlatRows.Count);
            FlatRows.InsertRange(0, rows);
            return;
        }
        int i = 0, j = 0;
        while (i < FlatRows.Count || j < rows.Count)
        {
            if (i < FlatRows.Count && j < rows.Count && ReferenceEquals(FlatRows[i], rows[j]))
            {
                i++;
                j++;
            }
            else if (j == rows.Count || (i < FlatRows.Count && _rowOrder[FlatRows[i]] < _rowOrder[rows[j]]))
            {
                var limit = j < rows.Count ? _rowOrder[rows[j]] : int.MaxValue;
                var end = i;
                while (end < FlatRows.Count && _rowOrder[FlatRows[end]] < limit)
                    end++;
                FlatRows.RemoveRange(i, end - i);
            }
            else
            {
                var limit = i < FlatRows.Count ? _rowOrder[FlatRows[i]] : int.MaxValue;
                var start = j;
                while (j < rows.Count && _rowOrder[rows[j]] < limit)
                    j++;
                FlatRows.InsertRange(i, rows.GetRange(start, j - start));
                i += j - start;
            }
        }
    }

    private int CountRuns(IReadOnlyList<object> old, List<object> rows)
    {
        int i = 0, j = 0, runs = 0;
        while (i < old.Count || j < rows.Count)
        {
            if (i < old.Count && j < rows.Count && ReferenceEquals(old[i], rows[j]))
            {
                i++;
                j++;
                continue;
            }
            runs++;
            if (j == rows.Count || (i < old.Count && _rowOrder[old[i]] < _rowOrder[rows[j]]))
            {
                var limit = j < rows.Count ? _rowOrder[rows[j]] : int.MaxValue;
                while (i < old.Count && _rowOrder[old[i]] < limit)
                    i++;
            }
            else
            {
                var limit = i < old.Count ? _rowOrder[old[i]] : int.MaxValue;
                while (j < rows.Count && _rowOrder[rows[j]] < limit)
                    j++;
            }
        }
        return runs;
    }

    [RelayCommand]
    private void Close() => _onClose(this);

    /// <summary>
    /// Puts every value this session changed back to its baseline, as one undo step: no question
    /// asked, because Ctrl+Z brings them all back.
    /// </summary>
    [RelayCommand]
    private void RevertAll()
    {
        var changes = new List<PropertyChange>();
        foreach (var p in AllSentinel.All)
        {
            if (!p.IsChanged)
                continue;
            if (_extensionOf.TryGetValue(p, out var ext))
            {
                changes.AddRange(RevertExtension(p, ext));
                continue;
            }
            var original = _baseline.TryGetValue(p.Key, out var b) ? b : null;
            changes.Add(new PropertyChange(p.Key, EditHistory.Set(Record, p.Key, original), original));
        }
        if (changes.Count == 0)
            return;
        Record.History.RecordStep(new EditStep(changes));
        SyncFromRecord();
        RefreshVisible();
        LastEditedKeys = changes.Select(c => c.Key).ToList();
        _onEdited(this);
        LastHistoryText = $"Undid {changes.Count} change{(changes.Count == 1 ? "" : "s")} to {Name}";
    }

    /// <param name="valueOf">Another property's value on the same asset (a bone field reads its model's key).</param>
    /// <param name="exists">Whether an asset of a type is in the index (a line list's items show their own ⚠).</param>
    internal static PropertyItemViewModel Create(PropertyDef def, string value, Action<string, string>? navigateToRef,
        Func<string, string?>? valueOf = null, Func<string, string, bool>? exists = null) =>
        def.Kind switch
        {
            PropertyKind.Number => new NumberPropertyViewModel(def, value),
            PropertyKind.Toggle => new TogglePropertyViewModel(def, value),
            PropertyKind.Choice => new ChoicePropertyViewModel(def, value),
            PropertyKind.AssetRef => new RefPropertyViewModel(def, value, navigateToRef is null ? null : r => navigateToRef(r.RefType, r.Value)),
            _ when def.TextEditor == PropertyTextEditor.Lines => new LinesPropertyViewModel(def, value, navigateToRef, valueOf, exists),
            _ => CreateText(def, value, valueOf),
        };

    /// <summary>A text property's editor: a file, colour, file-list or bone field when the deffile says so.</summary>
    private static PropertyItemViewModel CreateText(PropertyDef def, string value, Func<string, string?>? valueOf)
    {
        if (def.FileKind != PropertyFileKind.None)
            return new FilePropertyViewModel(def, value);
        return def.TextEditor switch
        {
            PropertyTextEditor.Color => new ColorPropertyViewModel(def, value),
            PropertyTextEditor.FileList => new FileListPropertyViewModel(def, value),
            PropertyTextEditor.Bone => new BonePropertyViewModel(def, value, valueOf),
            _ => new TextPropertyViewModel(def, value),
        };
    }

    /// <summary>A background file check landed: file fields re-read their ⚠ from it.</summary>
    void IFileProbeListener.FilesProbed()
    {
        var files = false;
        foreach (var item in AllSentinel.All)
        {
            if (item.Def.FileKind == PropertyFileKind.None)
                continue;
            files = true;
            var problem = Validator.Check(item.Def, item.RawValue, AssetExists);
            if (problem == item.Problem)
                continue;
            item.Problem = problem;
            _categoryOf.TryGetValue(item, out var owner);
            UpdateProblemRow(item, owner);
        }
        // The status bar's total counts the asset again from what is known now. Even when no row moved: a check can land
        // between the total's count and this tab's, which then already had it.
        if (files)
            Owner?.OnTabProblemsChanged(this);
    }

    private static string Prettify(string key)
    {
        var sb = new System.Text.StringBuilder(key.Length + 4);
        for (var i = 0; i < key.Length; i++)
        {
            var c = key[i];
            if (i == 0) { sb.Append(char.ToUpperInvariant(c)); continue; }
            if (char.IsUpper(c) && !char.IsUpper(key[i - 1])) sb.Append(' ');
            sb.Append(c);
        }
        return sb.ToString();
    }
}
