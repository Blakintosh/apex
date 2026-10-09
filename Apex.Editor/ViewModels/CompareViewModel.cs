using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Apex.Editor.Models;
using Apex.Editor.Services;

namespace Apex.Editor.ViewModels;

/// <summary>One comparison target (a column) in the N-way diff.</summary>
public sealed partial class CompareColumnViewModel : ObservableObject
{
    private readonly CompareViewModel _owner;

    public CompareColumnViewModel(CompareViewModel owner, AssetRecord record)
    {
        _owner = owner;
        Record = record;
    }

    public AssetRecord Record { get; }
    public string Name => Record.Name;
    public string GdtName => Record.GdtName;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DiffText))]
    private int _diffCount;

    public string DiffText => DiffCount == 1 ? "1 difference" : $"{DiffCount:N0} differences";

    [RelayCommand]
    private void Remove() => _owner.RemoveColumn(this);
}

/// <summary>One cell: a target's effective value for a given key, diffed against the base asset.</summary>
public sealed partial class CompareCellViewModel : ObservableObject
{
    private readonly CompareViewModel _owner;
    private readonly string _key;

    public CompareCellViewModel(CompareViewModel owner, string key, string value, string display, bool isInherited, bool isDifferent)
    {
        _owner = owner;
        _key = key;
        Value = value;
        Display = display;
        IsInherited = isInherited;
        _isDifferent = isDifferent;
    }

    /// <summary>The GDT value (what ◀ copies).</summary>
    public string Value { get; }

    /// <summary>The value as the editor shows it (On/Off, significant digits).</summary>
    public string Display { get; }

    /// <summary>The asset doesn't set this key itself: the value comes from its parent chain or the schema default.</summary>
    public bool IsInherited { get; }

    public string? Tip => IsInherited ? "Inherited from the parent chain or the default" : null;

    [ObservableProperty]
    private bool _isDifferent;

    /// <summary>Copies this target value into the base asset.</summary>
    [RelayCommand]
    private void Take() => _owner.WriteBase(_key, Value);
}

public sealed partial class CompareRowViewModel : ObservableObject
{
    public CompareRowViewModel(string key, string label, string baseDisplay, bool baseInherited, List<CompareCellViewModel> cells)
    {
        Key = key;
        Label = label;
        _baseDisplay = baseDisplay;
        _baseInherited = baseInherited;
        Cells = cells;
    }

    public string Key { get; }
    public string Label { get; }
    public List<CompareCellViewModel> Cells { get; }

    [ObservableProperty]
    private string _baseDisplay;

    [ObservableProperty]
    private bool _baseInherited;
}

/// <summary>
/// N-way asset diff: one base asset against any number of comparison columns — APE's multi-asset
/// diff, minus the lag. Values are what the game sees: an asset's own value, else its parent
/// chain's, else the schema default, shown the way the editor shows them. ◀ copies a value into
/// the base asset as an undo step on its history.
/// </summary>
public sealed partial class CompareViewModel : ObservableObject
{
    private readonly Action<AssetRecord> _onBaseEdited;
    private readonly Action _close;
    private readonly Func<string, string, AssetRecord?> _resolve;
    private readonly Func<AssetRecord, GdtFile?>? _gdtOf;
    private readonly AssetSchema? _schema;
    private readonly Dictionary<AssetRecord, Dictionary<string, string>> _inherited = new();

    /// <summary>The type's extension fields and record tables by key (<c>wtKick#</c> for a table); empty without extensions.</summary>
    private readonly Dictionary<string, (PropertyDef Def, Services.Extensions.ExtensionRecordList? List)> _extension = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="gdtOf">An asset's GDT, whose <c>.gdtx</c> holds its extension values: with it, the diff covers them.</param>
    public CompareViewModel(
        AssetRecord baseAsset,
        IReadOnlyList<AssetRecord> candidates,
        AssetRecord initialTarget,
        Action<AssetRecord> onBaseEdited,
        Action close,
        Func<string, string, AssetRecord?> resolve,
        Func<AssetRecord, GdtFile?>? gdtOf = null)
    {
        Base = baseAsset;
        _onBaseEdited = onBaseEdited;
        _close = close;
        _resolve = resolve;
        _gdtOf = gdtOf;
        _schema = SchemaRegistry.Get(baseAsset.Type);
        if (gdtOf is not null && _schema is not null)
            foreach (var schema in Services.Extensions.ExtensionRegistry.For(baseAsset.Type))
            {
                foreach (var f in schema.Fields)
                    _extension[f.Def.Key] = (f.Def, null);
                foreach (var list in schema.Records)
                    _extension[list.Def.Key] = (list.Def, list);
            }
        AllCandidates = candidates;
        Columns.Add(new CompareColumnViewModel(this, initialTarget));
        Rebuild();
    }

    public AssetRecord Base { get; }
    public string BaseName => Base.Name;
    public string Glyph => TypeStyles.Glyph(Base.Type);
    public IBrush GlyphBrush => TypeStyles.Brush(Base.Type);

    /// <summary>Every same-type asset; the picker searches it.</summary>
    public IReadOnlyList<AssetRecord> AllCandidates { get; }

    public ObservableCollection<CompareColumnViewModel> Columns { get; } = new();
    public RangeObservableCollection<CompareRowViewModel> Rows { get; } = new();

    /// <summary>Width of the base and every compared column; the view shares its room among them.</summary>
    [ObservableProperty]
    private double _columnWidth = 240;

    // ── Adding a column: a searchable picker over thousands of assets ────────
    [ObservableProperty]
    private string _addText = "";

    /// <summary>Assets whose name contains every typed word (as Quick Open matches names), up to 50.</summary>
    public RangeObservableCollection<AssetRecord> AddMatches { get; } = new();

    [ObservableProperty]
    private bool _hasAddMatches;

    [ObservableProperty]
    private AssetRecord? _addSelection;

    partial void OnAddTextChanged(string value)
    {
        var words = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            AddMatches.ReplaceAll(Array.Empty<AssetRecord>());
            AddSelection = null;
            HasAddMatches = false;
            return;
        }
        var used = new HashSet<AssetRecord>(Columns.Select(c => c.Record)) { Base };
        var matches = new List<AssetRecord>(50);
        foreach (var a in AllCandidates)
        {
            if (used.Contains(a) || !words.All(w => a.Name.Contains(w, StringComparison.OrdinalIgnoreCase)))
                continue;
            matches.Add(a);
            if (matches.Count == 50)
                break;
        }
        // Names that start with the query first, then the shortest (the likeliest exact asset).
        matches.Sort((x, y) =>
        {
            var px = x.Name.StartsWith(words[0], StringComparison.OrdinalIgnoreCase);
            var py = y.Name.StartsWith(words[0], StringComparison.OrdinalIgnoreCase);
            return px != py ? (px ? -1 : 1) : x.Name.Length != y.Name.Length ? x.Name.Length - y.Name.Length
                : StringComparer.OrdinalIgnoreCase.Compare(x.Name, y.Name);
        });
        AddMatches.ReplaceAll(matches);
        AddSelection = AddMatches.FirstOrDefault();
        HasAddMatches = AddMatches.Count > 0;
    }

    /// <summary>↑↓ in the search box move the highlighted match.</summary>
    public void MoveAddSelection(int delta)
    {
        if (AddMatches.Count == 0)
            return;
        var i = AddSelection is null ? -1 : AddMatches.IndexOf(AddSelection);
        AddSelection = AddMatches[Math.Clamp(i + delta, 0, AddMatches.Count - 1)];
    }

    /// <summary>Adds the highlighted (or given) match as a column and clears the search.</summary>
    [RelayCommand]
    private void AddColumn(AssetRecord? record)
    {
        record ??= AddSelection;
        if (record is null || Columns.Any(c => c.Record == record) || record == Base)
            return;
        Columns.Add(new CompareColumnViewModel(this, record));
        AddText = "";
        Rebuild();
    }

    [ObservableProperty]
    private bool _diffOnly = true;

    partial void OnDiffOnlyChanged(bool value) => Rebuild();

    internal void RemoveColumn(CompareColumnViewModel column)
    {
        if (Columns.Count <= 1)
        {
            _close();
            return;
        }
        Columns.Remove(column);
        Rebuild();
    }

    /// <summary>◀: copies a value into the base asset as one undo step on its history.</summary>
    internal void WriteBase(string key, string value)
    {
        if (_extension.TryGetValue(key, out var x))
        {
            WriteBaseExtension(key, x.Def, x.List, value);
            return;
        }
        Base.CaptureBaseline();
        var before = EditHistory.Set(Base, key, value);
        if (before == value)
            return;
        Base.History.RecordStep(new EditStep(new[] { new PropertyChange(key, before, value) }));
        _onBaseEdited(Base);
        UpdateRow(key);
    }

    /// <summary>◀ on an extension row: into the base's block (a table's rows numbered 1..N), one undo step.</summary>
    private void WriteBaseExtension(string key, PropertyDef def, Services.Extensions.ExtensionRecordList? list, string value)
    {
        if (_gdtOf?.Invoke(Base) is not { } gdt)
            return;
        var changes = list is not null
            ? Services.Extensions.RecordCodec.Write(gdt, Base.Name, new ExtensionTarget(_gdtOf!, def.Extension), list,
                Services.Extensions.RecordCodec.Keys(list, Services.Extensions.RecordCodec.Split(value)))
            : WriteOne(gdt, def, key, value);
        if (changes.Count == 0)
            return;
        Base.History.RecordStep(new EditStep(changes));
        _onBaseEdited(Base);
        UpdateRow(key);
    }

    private List<PropertyChange> WriteOne(GdtFile gdt, PropertyDef def, string key, string value)
    {
        var sidecar = ExtensionSidecar.Of(gdt);
        var before = sidecar.Get(Base.Name, def.Extension, key);
        if (before == value)
            return new List<PropertyChange>();
        sidecar.Set(Base.Name, def.Extension, key, value);
        return [new PropertyChange(key, before, value) { Extension = new ExtensionTarget(_gdtOf!, def.Extension) }];
    }

    /// <summary>Re-reads every value after the base changed elsewhere (undo).</summary>
    public void Refresh() => Rebuild();

    /// <summary>The value the game sees: the asset's own, else the nearest parent's, else the schema default.</summary>
    private (string Value, bool Inherited) Effective(AssetRecord record, string key)
    {
        if (_extension.TryGetValue(key, out var x))
            return EffectiveExtension(record, key, x.Def, x.List);
        if (record.Properties.TryGetValue(key, out var own))
            return (own, false);
        if (InheritedOf(record).TryGetValue(key, out var parent))
            return (parent, true);
        return (_schema?.Find(key)?.Default ?? "", true);
    }

    /// <summary>
    /// An extension value as the editor shows it: the asset's own block's, else the nearest ancestor's (a record table
    /// whole, from the nearest ancestor with rows), else the manifest default.
    /// </summary>
    private (string Value, bool Inherited) EffectiveExtension(AssetRecord record, string key, PropertyDef def, Services.Extensions.ExtensionRecordList? list)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (AssetRecord? cur = record; cur is not null && seen.Add(cur.Name); cur = cur.Parent is { } p ? _resolve(record.Type, p) : null)
        {
            if (_gdtOf?.Invoke(cur)?.Extensions?.Find(cur.Name, def.Extension) is not { } block)
                continue;
            if (list is not null)
            {
                var rows = Services.Extensions.RecordCodec.Rows(block.Properties, list);
                if (rows.Count > 0)
                    return (Services.Extensions.RecordCodec.Join(rows.Select(r => r.Value)), cur != record);
            }
            else if (block.Properties.TryGetValue(key, out var v))
                return (v, cur != record);
        }
        return (def.Default, true);
    }

    private Dictionary<string, string> InheritedOf(AssetRecord record)
    {
        if (_inherited.TryGetValue(record, out var values))
            return values;
        values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { record.Name };
        var cur = record;
        while (cur.Parent is { } parentName && seen.Add(parentName) && _resolve(record.Type, parentName) is { } parent)
        {
            foreach (var (k, v) in parent.Properties)
                values.TryAdd(k, v);
            cur = parent;
        }
        return _inherited[record] = values;
    }

    private string Show(string key, string value)
    {
        if (_extension.TryGetValue(key, out var x) && x.List is not null)
            return string.Join(" | ", Services.Extensions.RecordCodec.Split(value));
        var def = _schema?.Find(key) ?? (_extension.TryGetValue(key, out var f) ? f.Def : null);
        return def?.Kind switch
        {
            PropertyKind.Toggle => value == "1" ? "On" : "Off",
            PropertyKind.Number => NumberPropertyViewModel.DisplayOf(value),
            _ => value,
        };
    }

    /// <summary>
    /// A take changes one base value, so only that row's diff flags and the per-column counts move;
    /// the row leaves the list if "Differences only" is on and it no longer differs anywhere.
    /// </summary>
    private void UpdateRow(string key)
    {
        var index = -1;
        for (var r = 0; r < Rows.Count; r++)
            if (Rows[r].Key == key)
            {
                index = r;
                break;
            }
        if (index < 0)
        {
            Rebuild();
            return;
        }
        var row = Rows[index];
        var (baseValue, baseInherited) = Effective(Base, key);
        row.BaseDisplay = Show(key, baseValue);
        row.BaseInherited = baseInherited;
        var any = false;
        for (var i = 0; i < Columns.Count; i++)
        {
            var cell = row.Cells[i];
            var different = !AssetQuery.ValuesEqual(baseValue, cell.Value);
            if (different != cell.IsDifferent)
            {
                cell.IsDifferent = different;
                Columns[i].DiffCount += different ? 1 : -1;
            }
            any |= different;
        }
        if (DiffOnly && !any)
            Rows.RemoveAt(index);
    }

    /// <summary>Recomputes every row and swaps the list in with one notification.</summary>
    private void Rebuild()
    {
        var keys = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_schema is not null)
            foreach (var def in _schema.Properties)
                if (seen.Add(def.Key))
                    keys.Add(def.Key);
        foreach (var record in Columns.Select(c => c.Record).Prepend(Base))
        {
            foreach (var key in record.Properties.Keys)
                if (seen.Add(key))
                    keys.Add(key);
            foreach (var key in InheritedOf(record).Keys)
                if (seen.Add(key))
                    keys.Add(key);
        }
        // An extension's fields and tables after the GDT's keys, as the editor lists its sections after the deffile's.
        foreach (var key in _extension.Keys)
            if (seen.Add(key))
                keys.Add(key);

        var perColumnDiffs = new int[Columns.Count];
        var values = new (string Value, bool Inherited)[Columns.Count];
        var diffs = new bool[Columns.Count];
        var rows = new List<CompareRowViewModel>();
        foreach (var key in keys)
        {
            var (baseValue, baseInherited) = Effective(Base, key);
            var any = false;
            for (var i = 0; i < Columns.Count; i++)
            {
                values[i] = Effective(Columns[i].Record, key);
                diffs[i] = !AssetQuery.ValuesEqual(baseValue, values[i].Value);
                if (diffs[i])
                {
                    perColumnDiffs[i]++;
                    any = true;
                }
            }
            if (DiffOnly && !any)
                continue;
            var cells = new List<CompareCellViewModel>(Columns.Count);
            for (var i = 0; i < Columns.Count; i++)
                cells.Add(new CompareCellViewModel(this, key, values[i].Value, Show(key, values[i].Value), values[i].Inherited, diffs[i]));
            var label = _schema?.Find(key)?.Label ?? (_extension.TryGetValue(key, out var x) ? x.Def.Label : key);
            rows.Add(new CompareRowViewModel(key, label, Show(key, baseValue), baseInherited, cells));
        }
        Rows.ReplaceAll(rows);

        for (var i = 0; i < Columns.Count; i++)
            Columns[i].DiffCount = perColumnDiffs[i];
    }

    [RelayCommand]
    private void Close() => _close();
}
