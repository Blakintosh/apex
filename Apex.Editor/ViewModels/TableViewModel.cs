using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Apex.Editor.Models;
using Apex.Editor.Services;

namespace Apex.Editor.ViewModels;

/// <summary>One table column: a property of the table's type.</summary>
public sealed partial class TableColumnViewModel : ObservableObject
{
    private readonly TableViewModel _owner;

    public TableColumnViewModel(TableViewModel owner, PropertyDef def)
    {
        _owner = owner;
        Def = def;
    }

    public PropertyDef Def { get; }
    public string Label => Def.Label;
    public string Key => Def.Key;

    /// <summary>The key line under the label, only when the label isn't just the key again.</summary>
    public bool ShowKey => !Normalize(Def.Label).Equals(Normalize(Def.Key), StringComparison.OrdinalIgnoreCase);

    public string Tooltip => (Def.Description.Length == 0 ? Def.Key : $"{Def.Key}\n\n{Def.Description}")
        + (Def.Extension.Length > 0 ? $"\n\nFrom the {Def.Extension} extension: saved in the .gdtx beside the GDT." : "");

    /// <summary>Sized to what the column holds: switches are narrow, names and paths wide.</summary>
    public double Width => Def.Kind switch
    {
        PropertyKind.Toggle => 104,
        PropertyKind.Number => 150,
        PropertyKind.Choice => 168,
        _ => 232,
    };

    /// <summary>"▲" / "▼" on the column the rows are sorted by.</summary>
    [ObservableProperty]
    private string _sortGlyph = "";

    [RelayCommand]
    private void Sort() => _owner.SortBy(this);

    [RelayCommand]
    private void Remove() => _owner.RemoveColumn(this);

    private static string Normalize(string s) => s.Replace(" ", "").Replace("_", "");
}

/// <summary>A property in the column picker: checked when it is a column.</summary>
public sealed partial class TableColumnChoice : ObservableObject
{
    private readonly TableViewModel _owner;

    public TableColumnChoice(TableViewModel owner, PropertyDef def, bool isShown)
    {
        _owner = owner;
        Def = def;
        _isShown = isShown;
    }

    public PropertyDef Def { get; }
    public string Label => Def.Label;
    public string Key => Def.Key;
    public string Category => Def.Category;

    [ObservableProperty]
    private bool _isShown;

    partial void OnIsShownChanged(bool value) => _owner.SetColumnShown(Def, value);
}

/// <summary>One table cell: the same editor a property row uses, writing through to its record.</summary>
public sealed class TableCellViewModel
{
    public TableCellViewModel(TableRowViewModel row, TableColumnViewModel column, PropertyItemViewModel editor)
    {
        Row = row;
        Column = column;
        Editor = editor;
    }

    public TableRowViewModel Row { get; }
    public TableColumnViewModel Column { get; }
    public PropertyItemViewModel Editor { get; }
    public double Width => Column.Width;
}

public sealed partial class TableRowViewModel : ObservableObject
{
    private readonly Func<TableRowViewModel, IEnumerable<TableCellViewModel>> _makeCells;
    private RangeObservableCollection<TableCellViewModel>? _cells;

    public TableRowViewModel(AssetRecord record, Func<TableRowViewModel, IEnumerable<TableCellViewModel>> makeCells)
    {
        Record = record;
        _makeCells = makeCells;
        record.CaptureBaseline();
    }

    public AssetRecord Record { get; }
    public string Name => Record.Name;
    public string Glyph => TypeStyles.Glyph(Record.Type);
    public IBrush GlyphBrush => TypeStyles.Brush(Record.Type);

    /// <summary>
    /// One cell per column, in column order. Built the first time the row is shown, so opening a table builds the
    /// editors of the rows on screen, not of all 400; a shown or hidden column then adds or drops one cell.
    /// </summary>
    public RangeObservableCollection<TableCellViewModel> Cells => _cells ??= new(_makeCells(this));

    /// <summary>The cells, when the row has been shown; null when they were never built.</summary>
    public RangeObservableCollection<TableCellViewModel>? BuiltCells => _cells;

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>
/// Spreadsheet mode: the current query result as an editable grid with multi-select bulk apply —
/// the batch-editing workflow APE's one-asset-at-a-time modals can't express. Cells are the
/// property rows' own editors (scrub numbers, switches, dropdowns, validated references), and every
/// edit — a cell or a bulk apply — is one undo step on the records' histories, so Ctrl+Z in the
/// table takes back the table's last edit.
/// </summary>
public sealed partial class TableViewModel : ObservableObject, IFileProbeListener
{
    /// <summary>Rows beyond this are left out (the notice under the header says so).</summary>
    public const int RowCap = 400;

    private const int DefaultColumnCount = 8;

    private readonly Action<IReadOnlyList<AssetRecord>> _onEdited;
    private readonly Action _close;
    private readonly Func<string, string, AssetRecord?> _resolve;
    private readonly Func<AssetRecord, GdtFile?>? _gdtOf;
    private readonly AssetSchema? _schema;
    private readonly List<PropertyDef> _allDefs;

    // The table's own edits, newest last; each token tags the steps it pushed onto record histories.
    private readonly List<(object Token, List<AssetRecord> Records, string Text)> _undo = new();
    private readonly List<(object Token, List<AssetRecord> Records, string Text)> _redo = new();
    private bool _reloading;
    private bool _selectingAll;

    /// <param name="gdtOf">An asset's GDT, whose <c>.gdtx</c> holds its extension values: with it, the picker offers the
    /// type's extension fields as columns too (record tables stay in the editor: a cell can't hold a table).</param>
    public TableViewModel(string typeName, IReadOnlyList<AssetRecord> assets, int totalCount,
        Action<IReadOnlyList<AssetRecord>> onEdited, Action close, Func<string, string, AssetRecord?> resolve,
        Func<AssetRecord, GdtFile?>? gdtOf = null)
    {
        TypeName = typeName;
        TotalCount = totalCount;
        _onEdited = onEdited;
        _close = close;
        _resolve = resolve;
        _gdtOf = gdtOf;
        _schema = SchemaRegistry.Get(typeName);
        _allDefs = _schema is not null
            ? _schema.Properties.ToList()
            : assets.SelectMany(a => a.Properties.Keys).Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
                .Select(k => new PropertyDef(k, k, "Properties", PropertyKind.Text, "Raw GDT property."))
                .ToList();
        if (gdtOf is not null && _schema is not null)
            _allDefs.AddRange(Services.Extensions.ExtensionRegistry.For(typeName).SelectMany(s => s.Fields).Select(f => f.Def));

        Rows = new RangeObservableCollection<TableRowViewModel>(assets.Select(a => new TableRowViewModel(a, MakeCells)));
        foreach (var row in Rows)
            row.PropertyChanged += (sender, e) =>
            {
                if (e.PropertyName == nameof(TableRowViewModel.IsSelected) && !_selectingAll)
                    SelectedCount += ((TableRowViewModel)sender!).IsSelected ? 1 : -1;
            };

        foreach (var def in PickColumns(assets))
            Columns.Add(new TableColumnViewModel(this, def));
        SelectedColumn = Columns.FirstOrDefault();
        FieldFiles.Listen(this);
    }

    /// <summary>A background file check landed: file cells re-read their ⚠ from it.</summary>
    void IFileProbeListener.FilesProbed()
    {
        foreach (var row in Rows)
            foreach (var cell in row.BuiltCells ?? Enumerable.Empty<TableCellViewModel>())
                if (cell.Editor.Def.FileKind != PropertyFileKind.None)
                    cell.Editor.Problem = Validator.Check(cell.Editor.Def, cell.Editor.RawValue, AssetExists);
    }

    public string TypeName { get; }
    public int TotalCount { get; }
    public string Glyph => TypeStyles.Glyph(TypeName);
    public IBrush GlyphBrush => TypeStyles.Brush(TypeName);
    public RangeObservableCollection<TableColumnViewModel> Columns { get; } = new();
    public RangeObservableCollection<TableRowViewModel> Rows { get; }

    public string Summary => $"{Rows.Count:N0} {TypeName} asset{(Rows.Count == 1 ? "" : "s")} · {Columns.Count} column{(Columns.Count == 1 ? "" : "s")}";

    public bool IsCapped => TotalCount > Rows.Count;

    /// <summary>The source (a filter or a selection) held other asset types too, which a one-type table leaves out.</summary>
    public bool OtherTypesLeftOut { get; init; }

    /// <summary>What the table leaves out, said on the table rather than in the status bar.</summary>
    public bool HasNotice => IsCapped || OtherTypesLeftOut;

    public string Notice => (IsCapped, OtherTypesLeftOut) switch
    {
        (true, true) => $"Showing the first {Rows.Count:N0} of {TotalCount:N0} {TypeName} assets; other asset types are left out. Narrow the Explorer filter to reach the rest.",
        (true, false) => $"Showing the first {Rows.Count:N0} of {TotalCount:N0} {TypeName} assets. Narrow the Explorer filter to reach the rest.",
        _ => $"Showing only the {TypeName} assets: a table holds one asset type.",
    };

    // ── Selection ────────────────────────────────────────────────────────────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanBulkApply), nameof(BulkApplyLabel), nameof(AllSelected))]
    private int _selectedCount;

    /// <summary>The header checkbox: all, none, or (null) some rows selected.</summary>
    public bool? AllSelected
    {
        get => SelectedCount == 0 ? false : SelectedCount == Rows.Count ? true : null;
        set
        {
            // A click on the indeterminate box selects everything, like a spreadsheet's.
            var target = value != false || SelectedCount < Rows.Count && SelectedCount > 0;
            _selectingAll = true;
            foreach (var r in Rows)
                r.IsSelected = target;
            _selectingAll = false;
            SelectedCount = target ? Rows.Count : 0;
            OnPropertyChanged();
        }
    }

    // ── Columns ──────────────────────────────────────────────────────────────
    [ObservableProperty]
    private string _columnFilter = "";

    /// <summary>The picker's list: the type's properties matching the filter (up to 200), shown ones first.</summary>
    public RangeObservableCollection<TableColumnChoice> ColumnChoices { get; } = new();

    partial void OnColumnFilterChanged(string value) => RefreshColumnChoices();

    public void RefreshColumnChoices()
    {
        var q = ColumnFilter.Trim();
        var shown = new HashSet<PropertyDef>(Columns.Select(c => c.Def));
        ColumnChoices.ReplaceAll(_allDefs
            .Where(d => q.Length == 0 || d.Label.Contains(q, StringComparison.OrdinalIgnoreCase)
                || d.Key.Contains(q, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(d => shown.Contains(d))
            .Take(200)
            .Select(d => new TableColumnChoice(this, d, shown.Contains(d))));
    }

    public void SetColumnShown(PropertyDef def, bool shown)
    {
        var existing = Columns.FirstOrDefault(c => c.Def == def);
        if (shown == existing is not null)
            return;
        // Only that column changes: rows already shown gain or lose one cell; the rest build theirs when shown.
        if (shown)
        {
            var column = new TableColumnViewModel(this, def);
            Columns.Add(column);
            foreach (var row in Rows)
                row.BuiltCells?.Add(MakeCell(row, column));
        }
        else
        {
            var index = Columns.IndexOf(existing!);
            Columns.RemoveAt(index);
            foreach (var row in Rows)
                row.BuiltCells?.RemoveAt(index);
        }
        if (SelectedColumn is null || !Columns.Contains(SelectedColumn))
            SelectedColumn = Columns.FirstOrDefault();
        OnPropertyChanged(nameof(Summary));
    }

    internal void RemoveColumn(TableColumnViewModel column) => SetColumnShown(column.Def, false);

    private TableColumnViewModel? _sortColumn;
    private bool _sortDescending;

    /// <summary>Header click: sort by that column, again to reverse. Numbers sort as numbers.</summary>
    internal void SortBy(TableColumnViewModel column)
    {
        _sortDescending = _sortColumn == column && !_sortDescending;
        _sortColumn = column;
        foreach (var c in Columns)
            c.SortGlyph = c == column ? (_sortDescending ? "▼" : "▲") : "";
        var keyed = Rows.Select(r => (Row: r, Value: ValueOf(r.Record, column.Def))).ToList();
        Comparison<string> compare = column.Def.Kind is PropertyKind.Number or PropertyKind.Toggle
            ? (a, b) => ParseOrMin(a).CompareTo(ParseOrMin(b))
            : (a, b) => StringComparer.OrdinalIgnoreCase.Compare(a, b);
        keyed.Sort((x, y) =>
        {
            var c = compare(x.Value, y.Value);
            if (c == 0)
                c = StringComparer.OrdinalIgnoreCase.Compare(x.Row.Name, y.Row.Name);
            return _sortDescending ? -c : c;
        });
        Rows.ReplaceAll(keyed.Select(k => k.Row));

        static double ParseOrMin(string s) =>
            double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : double.MinValue;
    }

    private IEnumerable<TableCellViewModel> MakeCells(TableRowViewModel row) => Columns.Select(col => MakeCell(row, col));

    // ── Values: a record's own (else the default), from its GDT or, for an extension field, its .gdtx block ──

    private string ValueOf(AssetRecord record, PropertyDef def) => def.Extension.Length == 0
        ? record.Properties.GetValueOrDefault(def.Key, def.Default)
        : _gdtOf?.Invoke(record)?.Extensions?.Get(record.Name, def.Extension, def.Key) ?? def.Default;

    private string BaselineOf(AssetRecord record, PropertyDef def)
    {
        if (def.Extension.Length == 0)
            return record.SessionBaseline?.GetValueOrDefault(def.Key, def.Default) ?? def.Default;
        return _gdtOf?.Invoke(record)?.Extensions?.Find(record.Name, def.Extension) is { SessionBaseline: { } baseline }
            ? baseline.GetValueOrDefault(def.Key, def.Default)
            : ValueOf(record, def);
    }

    /// <summary>
    /// Writes one value and returns the change for the undo step (null: nothing changed). As in the editor, an extension
    /// value set to its default on a key the block didn't hold when the session began is removed rather than written.
    /// </summary>
    private PropertyChange? Write(AssetRecord record, PropertyDef def, string value)
    {
        if (def.Extension.Length == 0)
        {
            var was = EditHistory.Set(record, def.Key, value);
            return (was ?? def.Default) == value ? null : new PropertyChange(def.Key, was, value);
        }
        if (_gdtOf?.Invoke(record) is not { } gdt)
            return null;
        var sidecar = ExtensionSidecar.Of(gdt);
        var held = sidecar.Find(record.Name, def.Extension)?.SessionBaseline?.ContainsKey(def.Key) == true;
        var write = !held && value == def.Default ? null : value;
        var before = sidecar.Get(record.Name, def.Extension, def.Key);
        if (before == write)
            return null;
        sidecar.Set(record.Name, def.Extension, def.Key, write);
        return new PropertyChange(def.Key, before, write) { Extension = new ExtensionTarget(_gdtOf!, def.Extension) };
    }

    private TableCellViewModel MakeCell(TableRowViewModel row, TableColumnViewModel column)
    {
        var def = column.Def;
        var editor = AssetEditorViewModel.Create(def, ValueOf(row.Record, def), null,
            key => row.Record.Properties.GetValueOrDefault(key), AssetExists);
        editor.InitBaseline(BaselineOf(row.Record, def));
        editor.Problem = Validator.Check(def, editor.RawValue, AssetExists);
        var cell = new TableCellViewModel(row, column, editor);
        editor.Edited += _ => OnCellEdited(cell);
        return cell;
    }

    private bool AssetExists(string type, string name) => _resolve(type, name) is not null;

    /// <summary>A committed cell edit: write the record, record one undo step, tell the shell.</summary>
    private void OnCellEdited(TableCellViewModel cell)
    {
        var editor = cell.Editor;
        editor.Problem = Validator.Check(editor.Def, editor.RawValue, AssetExists);
        if (_reloading)
            return;
        var record = cell.Row.Record;
        if (Write(record, editor.Def, editor.RawValue) is not { } change)
            return;
        // A scrub is many edits to one cell; they coalesce into one step, and one table entry.
        var step = record.History.RecordEdit(change.Key, change.Before, change.After, editor.IsContinuousEdit, change.Extension);
        if (_undo.Count == 0 || _undo[^1].Token != step)
            Push(step, new List<AssetRecord> { record }, $"{editor.Label} on {record.Name}");
        else
            _redo.Clear();
        _onEdited(new[] { record });
    }

    // ── Bulk apply ───────────────────────────────────────────────────────────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanBulkApply))]
    private TableColumnViewModel? _selectedColumn;

    /// <summary>The "to" value: a typed editor for the chosen column, validated like a cell.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanBulkApply))]
    private PropertyItemViewModel? _bulkEditor;

    partial void OnSelectedColumnChanged(TableColumnViewModel? value)
    {
        if (value is null)
        {
            BulkEditor = null;
            return;
        }
        var editor = AssetEditorViewModel.Create(value.Def, value.Def.Default, null);
        editor.InitBaseline(editor.RawValue);
        editor.Problem = Validator.Check(value.Def, editor.RawValue, AssetExists);
        editor.Edited += e =>
        {
            e.Problem = Validator.Check(e.Def, e.RawValue, AssetExists);
            OnPropertyChanged(nameof(CanBulkApply));
        };
        BulkEditor = editor;
    }

    public bool CanBulkApply => SelectedCount > 0 && SelectedColumn is not null && BulkEditor is { HasProblem: false };

    /// <summary>The button states its own blast radius before the click.</summary>
    public string BulkApplyLabel => SelectedCount switch
    {
        0 => "Apply",
        1 => "Apply to 1 asset",
        _ => $"Apply to {SelectedCount:N0} assets",
    };

    /// <summary>The status line for the last table action (the shell shows it).</summary>
    [ObservableProperty]
    private string _lastActionText = "";

    /// <summary>
    /// Writes the bulk value into the chosen column of every selected row: one undo step across
    /// them all, so it applies straight away and Ctrl+Z takes it back.
    /// </summary>
    [RelayCommand]
    private void BulkApply()
    {
        if (SelectedColumn is not { } col || BulkEditor is not { HasProblem: false } editor)
            return;
        var value = editor.RawValue;
        var index = Columns.IndexOf(col);
        var token = new object();
        var edited = new List<AssetRecord>();
        foreach (var row in Rows)
        {
            if (!row.IsSelected)
                continue;
            if (AssetQuery.ValuesEqual(ValueOf(row.Record, col.Def), value) || Write(row.Record, col.Def, value) is not { } change)
                continue;
            row.Record.History.RecordStep(new EditStep(new[] { change }, token));
            edited.Add(row.Record);
            if (row.BuiltCells is { } cells)
                Reload(cells[index]);
        }
        var shown = value.Length == 0 ? "(empty)" : value;
        if (edited.Count == 0)
        {
            LastActionText = $"All {SelectedCount:N0} selected assets already have {col.Label} set to {shown}";
            return;
        }
        var text = $"{col.Label} set to {shown} on {edited.Count:N0} asset{(edited.Count == 1 ? "" : "s")}";
        Push(token, edited, text);
        _onEdited(edited);
        LastActionText = $"{text} · {Commands.CommandCatalog.Get(Commands.CommandCatalog.Undo).GestureText} undoes it";
    }

    /// <summary>
    /// An edit made for the table's selection from outside it (the palette turning an extension on): its batch joins the
    /// table's history, so Ctrl+Z in the table takes it back, and the cells it touched show it.
    /// </summary>
    public void RecordExternal(object token, List<AssetRecord> records, string text)
    {
        Push(token, records, text);
        foreach (var row in Rows)
            if (row.BuiltCells is { } cells && records.Contains(row.Record))
                foreach (var cell in cells)
                    Reload(cell);
    }

    // ── Undo / redo (the table's own edits) ──────────────────────────────────
    private void Push(object token, List<AssetRecord> records, string text)
    {
        _undo.Add((token, records, text));
        _redo.Clear();
    }

    /// <summary>Takes back the table's last edit; returns the status text, or "" when there is none.</summary>
    public string Undo() => Step(_undo, _redo, undo: true);

    public string Redo() => Step(_redo, _undo, undo: false);

    private string Step(List<(object Token, List<AssetRecord> Records, string Text)> from,
        List<(object Token, List<AssetRecord> Records, string Text)> to, bool undo)
    {
        if (from.Count == 0)
            return "";
        var entry = from[^1];
        from.RemoveAt(from.Count - 1);
        var done = new List<AssetRecord>();
        foreach (var record in entry.Records)
        {
            var history = record.History;
            // Only a step still on top is this table's: an edit made since (in an editor tab) wins.
            var next = undo ? history.NextUndo : history.NextRedo;
            if (next is null || (next != entry.Token && next.Batch != entry.Token))
                continue;
            if (undo)
                history.Undo();
            else
                history.Redo();
            done.Add(record);
        }
        to.Add(entry);
        foreach (var row in Rows)
            if (row.BuiltCells is { } cells && done.Contains(row.Record))
                foreach (var cell in cells)
                    Reload(cell);
        if (done.Count > 0)
            _onEdited(done);
        LastActionText = $"{(undo ? "Undid" : "Redid")}: {entry.Text}";
        return LastActionText;
    }

    private void Reload(TableCellViewModel cell)
    {
        _reloading = true;
        cell.Editor.RawValue = ValueOf(cell.Row.Record, cell.Column.Def);
        _reloading = false;
    }

    [RelayCommand]
    private void Close() => _close();

    /// <summary>
    /// The columns a table opens with: the properties whose values actually differ across these
    /// rows (the ones worth a spreadsheet), in form order; the type's first properties when too few
    /// differ. The picker adds or removes any of the rest.
    /// </summary>
    private List<PropertyDef> PickColumns(IReadOnlyList<AssetRecord> assets)
    {
        // An extension's fields are offered in the picker, never chosen for a new table.
        var candidates = _allDefs.Where(d => d.DefaultVisible && d.Extension.Length == 0).ToList();
        var picked = new List<PropertyDef>();
        if (assets.Count > 1)
            foreach (var def in candidates)
            {
                var first = assets[0].Properties.GetValueOrDefault(def.Key, def.Default);
                if (assets.Any(a => !AssetQuery.ValuesEqual(a.Properties.GetValueOrDefault(def.Key, def.Default), first)))
                    picked.Add(def);
                if (picked.Count == DefaultColumnCount)
                    return picked;
            }
        foreach (var def in candidates)
        {
            if (picked.Count == DefaultColumnCount)
                break;
            if (!picked.Contains(def))
                picked.Add(def);
        }
        return picked;
    }
}
