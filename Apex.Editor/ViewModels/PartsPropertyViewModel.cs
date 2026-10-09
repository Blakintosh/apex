using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Apex.Editor.Models;
using Apex.Editor.Services.Extensions;

namespace Apex.Editor.ViewModels;

/// <summary>
/// A field whose one stored value is several named parts joined by commas (an extension field's <c>parts</c>), shown as
/// a row per part under the field's label. To the editor it is one property: <see cref="RawValue"/> is the stored value,
/// so change tracking, undo, revert, provenance and the journal work on the key as for any field. A part's edit writes
/// that part alone (<see cref="PartsCodec.Set"/>): an untouched value keeps every byte.
/// </summary>
public sealed partial class PartsPropertyViewModel : PropertyItemViewModel
{
    private readonly Func<string, string, bool> _exists;
    private bool _loading;

    public PartsPropertyViewModel(PropertyDef def, IReadOnlyList<PropertyDef> parts, string value, Func<string, string, bool>? exists)
        : base(def)
    {
        PartDefs = parts;
        _exists = exists ?? ((_, _) => true);
        _value = value;
        var fields = PartsCodec.Split(value);
        var cells = new PropertyItemViewModel[parts.Count];
        for (var i = 0; i < parts.Count; i++)
        {
            var index = i;
            var cell = AssetEditorViewModel.Create(parts[i], PartsCodec.Cell(fields, i), null, null, _exists);
            cell.AccessibleName = $"{def.Label}: {parts[i].Label}";
            cell.Edited += c => PartEdited(index, c);
            if (cell is NumberPropertyViewModel number)
                number.ListPaste = text => FillFrom(index, text, cell);
            cells[i] = cell;
        }
        Parts = cells;
        RefreshModified();
    }

    public IReadOnlyList<PropertyDef> PartDefs { get; }

    /// <summary>One editor per part, in order: the form's own number, switch, dropdown and text editors.</summary>
    public IReadOnlyList<PropertyItemViewModel> Parts { get; }

    public override bool IsMultiLine => true;

    [ObservableProperty]
    private string _value;

    partial void OnValueChanged(string value)
    {
        // Undo, revert or a reload moved the value: the parts follow it. A part's own edit already did.
        if (!_loading)
            Load(value);
        OnEdited();
    }

    public override string RawValue
    {
        get => Value;
        set => Value = value ?? "";
    }

    private void PartEdited(int index, PropertyItemViewModel cell)
    {
        if (_loading)
            return;
        if (cell.RawValue.Contains(','))
        {
            FillFrom(index, cell.RawValue, cell);
            return;
        }
        var next = PartsCodec.Set(Value, index, cell.RawValue, PartDefs);
        if (next == Value)
            return;
        IsContinuousEdit = cell.IsContinuousEdit;
        _loading = true;
        try
        {
            Value = next;
        }
        finally
        {
            _loading = false;
            IsContinuousEdit = false;
        }
        // The editor can answer with another value (a derived asset brought back to what it inherits).
        if (Value != next)
            Load(Value);
    }

    /// <summary>
    /// A comma-separated list typed or pasted into part <paramref name="index"/> fills the parts: a whole value (as many
    /// fields as there are parts) lands from the first part whichever part it was pasted into, a shorter run fills on from
    /// the part it was pasted into. One undo step. A list that doesn't fit changes nothing and says so on that part.
    /// Always handled, so the cell never keeps the list as its own text.
    /// </summary>
    private bool FillFrom(int index, string text, PropertyItemViewModel cell)
    {
        var fields = text.Split(',').Select(f => f.Trim()).ToArray();
        var start = fields.Length >= PartDefs.Count ? 0 : index;
        if (start + fields.Length > PartDefs.Count)
        {
            Load(Value);
            cell.Problem = fields.Length > PartDefs.Count
                ? $"That's {fields.Length} values; {Label} has {PartDefs.Count} parts."
                : $"That's {fields.Length} values; only {PartDefs.Count - index} parts from {PartDefs[index].Label} on.";
            return true;
        }
        var next = Value;
        for (var i = 0; i < fields.Length; i++)
            next = PartsCodec.Set(next, start + i, fields[i], PartDefs);
        _loading = true;
        try
        {
            if (next != Value)
                Value = next;
        }
        finally
        {
            _loading = false;
        }
        Load(Value);
        return true;
    }

    /// <summary>Each part's editor shows its field of <paramref name="value"/>, and its baseline and parent's the same.</summary>
    private void Load(string value)
    {
        var fields = PartsCodec.Split(value);
        _loading = true;
        try
        {
            for (var i = 0; i < Parts.Count; i++)
                if (Parts[i].RawValue != PartsCodec.Cell(fields, i))
                    Parts[i].RawValue = PartsCodec.Cell(fields, i);
        }
        finally
        {
            _loading = false;
        }
    }

    protected override void OnMarksChanged() => RefreshParts();

    /// <summary>
    /// The parts' own marks: changed against the field's baseline, inherited against its parent value, so ↶ and ↑ beside
    /// a part act on that part.
    /// </summary>
    public void RefreshParts()
    {
        var baseline = PartsCodec.Split(BaselineValue);
        var parent = ParentValue is null ? null : PartsCodec.Split(ParentValue);
        for (var i = 0; i < Parts.Count; i++)
        {
            Parts[i].InitBaseline(PartsCodec.Cell(baseline, i));
            Parts[i].InitProvenance(parent is null ? null : PartsCodec.Cell(parent, i));
        }
    }

    /// <summary>Checks every part and returns the field's problem: its first part's, and how many more.</summary>
    public string? Validate()
    {
        var problems = PartsCodec.Problems(PartDefs, Value, _exists);
        for (var i = 0; i < Parts.Count; i++)
            Parts[i].Problem = problems.FirstOrDefault(p => p.Part == i).Message;
        return problems.Count switch
        {
            0 => null,
            1 => problems[0].Message,
            _ => $"{problems[0].Message} ({problems.Count - 1} more)",
        };
    }

    /// <summary>A change between two values in words: which parts, and from what to what when it is one.</summary>
    public (string Old, string Now) Describe(string before, string after)
    {
        var a = PartsCodec.Split(before);
        var b = PartsCodec.Split(after);
        var changed = Enumerable.Range(0, PartDefs.Count).Where(i => PartsCodec.Cell(a, i) != PartsCodec.Cell(b, i)).ToList();
        if (changed.Count == 1)
            return ($"{PartDefs[changed[0]].Label}: {Show(PartsCodec.Cell(a, changed[0]))}", Show(PartsCodec.Cell(b, changed[0])));
        return (Show(before), Show(after));

        static string Show(string v) => v.Length == 0 ? "(empty)" : v;
    }

    /// <summary>What an undo or redo did, in words ("Spring, view, hip: Stiffness"), never the stored value.</summary>
    public override string? DescribeStep(string before, string after) =>
        PartsCodec.Changed(PartDefs, before, after) is { } parts ? $"{Label}: {parts}" : null;

    protected override bool MatchesMore(string query) =>
        PartDefs.Any(p => p.Label.Contains(query, StringComparison.OrdinalIgnoreCase) || p.Key.Contains(query, StringComparison.OrdinalIgnoreCase));
}
