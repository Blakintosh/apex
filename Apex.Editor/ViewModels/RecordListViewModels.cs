using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Apex.Editor.Services.Extensions;

namespace Apex.Editor.ViewModels;

/// <summary>One row of a record table: a cell editor per column, over the record it was read from.</summary>
public sealed partial class RecordRowViewModel : ObservableObject
{
    internal RecordRowViewModel(string record, RecordFields fields, IReadOnlyList<PropertyItemViewModel> cells)
    {
        Record = record;
        Fields = fields;
        Cells = cells;
    }

    public IReadOnlyList<PropertyItemViewModel> Cells { get; }

    /// <summary>1-based, as the key it is saved under (row 3 is wtKick3).</summary>
    [ObservableProperty]
    private int _number;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    private string? _problem;

    public bool HasProblem => Problem is not null;

    /// <summary>In the rows Shift+↑↓ marked, which Ctrl+C copies and Ctrl+V pastes over.</summary>
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>The record as the block holds it; an untouched row keeps it byte for byte.</summary>
    internal string Record { get; set; }

    internal RecordFields Fields { get; set; }
}

/// <summary>
/// A record list (<c>wtKick#</c>): its numbered keys as one table row in the form. The rows are the only state the
/// editor sees: <see cref="RawValue"/> is them joined (<see cref="RecordCodec.Join"/>), so a cell edit, an added,
/// removed or moved row is one edit through the ordinary path, and change tracking, undo, the journal and the Changes
/// list treat the table as one property. The editor turns that value into the keys (numbered 1..N in row order).
/// </summary>
public sealed partial class RecordsPropertyViewModel : PropertyItemViewModel
{
    private readonly Action<string, string>? _navigateToRef;
    private readonly Func<string, string, bool> _exists;
    private bool _writing;
    private bool _loading;

    public RecordsPropertyViewModel(ExtensionRecordList list, string value, Action<string, string>? navigateToRef,
        Func<string, string, bool>? exists) : base(list.Def)
    {
        List = list;
        _navigateToRef = navigateToRef;
        _exists = exists ?? ((_, _) => true);
        _value = value;
        Rows = new RangeObservableCollection<RecordRowViewModel>(RecordCodec.Split(value).Select(MakeRow));
        Renumber();
        RefreshModified();
    }

    public ExtensionRecordList List { get; }

    public IReadOnlyList<RecordColumn> Columns => List.Columns;

    public RangeObservableCollection<RecordRowViewModel> Rows { get; }

    public override bool IsMultiLine => true;

    public bool IsEmpty => Rows.Count == 0;

    public bool CanAdd => List.MaxRows is not { } max || Rows.Count < max;

    /// <summary>"3 of 24" beside the label (or "3 rows" with no limit).</summary>
    public string CountText => List.MaxRows is { } max ? $"{Rows.Count} of {max}" : RowsText(Rows.Count);

    /// <summary>What the Add button's tooltip and a full table say.</summary>
    public string AddTip => CanAdd
        ? "Add a row (Ctrl+Enter in a row adds a copy of it below)"
        : $"{Label} holds up to {List.MaxRows} rows";

    [ObservableProperty]
    private string _value;

    partial void OnValueChanged(string value)
    {
        // Undo, redo, revert or a reload moved the value: the rows follow it. An edit made here already did.
        if (!_writing)
            Load(value);
        OnEdited();
    }

    public override string RawValue
    {
        get => Value;
        set => Value = value ?? "";
    }

    public override string DisplayValue => RowsText(Rows.Count);

    private static string RowsText(int n) => n == 1 ? "1 row" : $"{n} rows";

    /// <summary>A change between two values of the table, as the Inspector's Changes list says it.</summary>
    public (string Old, string Now) Describe(string before, string after)
    {
        var a = RecordCodec.Split(before);
        var b = RecordCodec.Split(after);
        if (a.Count != b.Count)
            return (RowsText(a.Count), RowsText(b.Count));
        for (var i = 0; i < a.Count; i++)
            if (a[i] != b[i])
                return ($"row {i + 1}: {Show(a[i])}", Show(b[i]));
        return (RowsText(a.Count), RowsText(b.Count));

        static string Show(string v) => v.Length == 0 ? "(empty)" : v;
    }

    // ── Rows ─────────────────────────────────────────────────────────────────

    private RecordRowViewModel MakeRow(string record)
    {
        var fields = RecordCodec.Parse(List, record);
        var display = List.DisplayColumns;
        var cells = new PropertyItemViewModel[display.Count];
        RecordRowViewModel? row = null;
        for (var i = 0; i < cells.Length; i++)
        {
            var column = i;
            var cell = AssetEditorViewModel.Create(display[i].Def, RecordCodec.DisplayCell(List, fields, display[i]), _navigateToRef, null, _exists);
            cell.Edited += c => CellEdited(row!, column, c);
            cells[i] = cell;
        }
        row = new RecordRowViewModel(record, fields, cells);
        return row;
    }

    private void CellEdited(RecordRowViewModel row, int column, PropertyItemViewModel cell)
    {
        if (_loading || !Rows.Contains(row))
            return;
        // Only the edited cell is read back: a switch reads an empty field as 0, and the others keep their text.
        var cells = row.Fields.Cells;
        var display = List.DisplayColumns[column];
        if (RecordCodec.DisplayCell(List, row.Fields, display) == cell.RawValue)
            return;
        if (display.Combine is { } combine)
        {
            // One of the group's choices: each of its columns takes its part of the value.
            var values = cell.RawValue.Split(',');
            if (values.Length != combine.Columns.Count)
                return;
            for (var i = 0; i < values.Length; i++)
                cells[combine.Columns[i]] = values[i];
        }
        else
            cells[display.Column] = cell.RawValue;
        row.Record = RecordCodec.FillHidden(List, RecordCodec.Format(List, cells, row.Fields.Extras), newRow: false);
        row.Fields = RecordCodec.Parse(List, row.Record);
        IsContinuousEdit = cell.IsContinuousEdit;
        try
        {
            Write();
        }
        finally
        {
            IsContinuousEdit = false;
        }
    }

    /// <summary>
    /// A row at <paramref name="index"/>: a copy of the row before it (kick patterns and slot lines are mostly the row
    /// above with a value or two changed), or, as the first row, each column's default; a hidden column holds its default
    /// either way. Null when the table is full.
    /// </summary>
    public RecordRowViewModel? Insert(int index)
    {
        if (!CanAdd)
            return null;
        index = Math.Clamp(index, 0, Rows.Count);
        var record = RecordCodec.FillHidden(List, index > 0
            ? Rows[index - 1].Record
            : RecordCodec.Format(List, Columns.Select(c => c.Optional ? "" : c.Def.Default).ToArray()), newRow: true);
        var row = MakeRow(record);
        Rows.Insert(index, row);
        Write();
        return row;
    }

    public void Remove(RecordRowViewModel row)
    {
        if (!Rows.Remove(row))
            return;
        Write();
    }

    /// <summary>
    /// Pasted rows: <paramref name="records"/> replace the rows from <paramref name="at"/> down and add rows past the
    /// last, as a spreadsheet's paste does (at the end of the table, they add). One edit, so one undo step. Rows past the
    /// list's limit are left out. Returns how many rows were replaced, added and left out.
    /// </summary>
    public (int Replaced, int Added, int LeftOut) Paste(int at, IReadOnlyList<string> records)
    {
        at = Math.Clamp(at, 0, Rows.Count);
        var rows = Rows.Select(r => r.Record).ToList();
        var room = (List.MaxRows ?? int.MaxValue) - at;
        var take = Math.Min(records.Count, Math.Max(0, room));
        var replaced = Math.Min(take, rows.Count - at);
        for (var i = 0; i < take; i++)
        {
            var record = RecordCodec.FillHidden(List, records[i], newRow: false);
            if (at + i < rows.Count)
                rows[at + i] = record;
            else
                rows.Add(record);
        }
        if (take > 0)
        {
            Load(RecordCodec.Join(rows));
            Write();
        }
        return (replaced, take - replaced, records.Count - take);
    }

    /// <summary>The rows as stored, one per line: what Ctrl+C copies and Ctrl+V reads back.</summary>
    public static string Copy(IEnumerable<RecordRowViewModel> rows) => string.Join(Environment.NewLine, rows.Select(r => r.Record));

    /// <summary>"Kick patterns row 2", "adding row 3 to Kick patterns": what a table edit did, never its records.</summary>
    public override string? DescribeStep(string before, string after)
    {
        var a = RecordCodec.Split(before);
        var b = RecordCodec.Split(after);
        if (a.Count == b.Count)
        {
            var changed = Enumerable.Range(0, a.Count).Where(i => a[i] != b[i]).Select(i => i + 1).ToList();
            return changed.Count switch
            {
                0 => Label,
                1 => $"{Label} row {changed[0]}",
                2 => $"{Label} rows {changed[0]} and {changed[1]}",
                _ => $"{Label}, {changed.Count} rows",
            };
        }
        // One row in or out, the rest as they were: say which.
        var (longer, shorter) = b.Count > a.Count ? (b, a) : (a, b);
        if (longer.Count == shorter.Count + 1)
        {
            var i = 0;
            while (i < shorter.Count && longer[i] == shorter[i])
                i++;
            if (longer.Skip(i + 1).SequenceEqual(shorter.Skip(i)))
                return b.Count > a.Count ? $"adding row {i + 1} to {Label}" : $"removing row {i + 1} from {Label}";
        }
        return $"{Label} from {RowsText(a.Count)} to {RowsText(b.Count)}";
    }

    protected override bool MatchesMore(string query) =>
        List.DisplayColumns.Any(c => c.Def.Label.Contains(query, StringComparison.OrdinalIgnoreCase))
        || Columns.Any(c => !c.Hidden && c.Name.Contains(query, StringComparison.OrdinalIgnoreCase));

    /// <summary>Moves <paramref name="row"/> by <paramref name="delta"/> places; false at either end.</summary>
    public bool Move(RecordRowViewModel row, int delta)
    {
        var from = Rows.IndexOf(row);
        var to = from + delta;
        if (from < 0 || to < 0 || to >= Rows.Count)
            return false;
        // The values move, not the rows: the rows in between take their neighbours' values, and nothing is built again.
        var records = Rows.Select(r => r.Record).ToList();
        var record = records[from];
        records.RemoveAt(from);
        records.Insert(to, record);
        Load(RecordCodec.Join(records));
        Write();
        return true;
    }

    /// <summary>The rows written back as one edit.</summary>
    private void Write()
    {
        Renumber();
        var joined = RecordCodec.Join(Rows.Select(r => r.Record));
        if (joined == Value)
            return;
        _writing = true;
        try
        {
            Value = joined;
        }
        finally
        {
            _writing = false;
        }
        // The editor can answer an edit with another value (a derived weapon left with no rows inherits its parent's).
        if (Value != joined)
            Load(Value);
    }

    /// <summary>Brings the rows to <paramref name="value"/>, keeping the rows (and their cells' controls) that didn't change.</summary>
    private void Load(string value)
    {
        var records = RecordCodec.Split(value);
        // Rows past the shorter of the two go or come as one change; the rest take their new values in place, so a paste
        // or an undo of one rebuilds only the rows it adds.
        if (records.Count < Rows.Count)
            Rows.RemoveRange(records.Count, Rows.Count - records.Count);
        else if (records.Count > Rows.Count)
            Rows.InsertRange(Rows.Count, records.Skip(Rows.Count).Select(MakeRow).ToList());
        _loading = true;
        try
        {
            for (var i = 0; i < records.Count; i++)
            {
                var row = Rows[i];
                if (row.Record == records[i])
                    continue;
                row.Record = records[i];
                row.Fields = RecordCodec.Parse(List, records[i]);
                for (var c = 0; c < row.Cells.Count; c++)
                    if (RecordCodec.DisplayCell(List, row.Fields, List.DisplayColumns[c]) is var shown && row.Cells[c].RawValue != shown)
                        row.Cells[c].RawValue = shown;
            }
        }
        finally
        {
            _loading = false;
        }
        Renumber();
    }

    private void Renumber()
    {
        for (var i = 0; i < Rows.Count; i++)
        {
            Rows[i].Number = i + 1;
            for (var c = 0; c < Rows[i].Cells.Count; c++)
                Rows[i].Cells[c].AccessibleName = $"{List.DisplayColumns[c].Def.Label} row {i + 1}";
        }
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(CanAdd));
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(AddTip));
    }

    // ── Problems ─────────────────────────────────────────────────────────────

    /// <summary>Checks every row (a repeat needs them all) and returns the table's problem: its first row's, and how many more.</summary>
    public string? Validate()
    {
        var fields = Rows.Select(r => r.Fields).ToList();
        var repeats = RecordCodec.Repeats(List, fields);
        string? first = null;
        var bad = 0;
        for (var i = 0; i < Rows.Count; i++)
        {
            var problems = RecordCodec.Problems(List, fields[i], _exists);
            if (repeats[i] >= 0)
                problems.Add($"Same {RecordCodec.UniqueLabels(List)} as row {repeats[i] + 1}: {List.Def.Extension} uses the first.");
            var problem = problems.Count == 0 ? null : string.Join("\n", problems);
            Rows[i].Problem = problem;
            if (problem is null)
                continue;
            bad++;
            first ??= $"Row {i + 1}: {problems[0]}";
        }
        if (List.MaxRows is { } max && Rows.Count > max)
            return $"{RowsText(Rows.Count)}; {List.Def.Extension} reads the first {max}." + (first is null ? "" : " " + first);
        return bad switch
        {
            0 => null,
            1 => first,
            _ => $"{first} ({bad - 1} more row{(bad == 2 ? "" : "s")} with problems)",
        };
    }
}
