using System;
using System.Collections.Generic;

namespace Apex.Editor.Models;

/// <summary>One property write: the key's value before and after (null = the key was absent).</summary>
public sealed record PropertyChange(string Key, string? Before, string? After)
{
    /// <summary>Where the value lives when it is extension data (the asset's block in its GDT's <c>.gdtx</c>); null for the GDT.</summary>
    public ExtensionTarget? Extension { get; init; }
}

/// <summary>
/// An asset's extension data: how to find the GDT whose sidecar holds it, and the extension's id. The GDT is looked up
/// when the change is undone or redone, not kept from the edit: the asset may have moved to another GDT since, and its
/// blocks with it.
/// </summary>
public sealed record ExtensionTarget(Func<AssetRecord, GdtFile?> GdtOf, string Id);

/// <summary>
/// One undo step on one record. Operations that touch several records at once (a table bulk
/// apply) push a step onto each record's history sharing one <see cref="Batch"/> token, so the
/// view that made them can undo the operation as a whole.
/// </summary>
public sealed class EditStep
{
    public EditStep(IReadOnlyList<PropertyChange> changes, object? batch = null)
    {
        Changes = changes;
        Batch = batch;
    }

    public IReadOnlyList<PropertyChange> Changes { get; private set; }
    public object? Batch { get; }

    /// <summary>The row a run of typing or scrubbing is coalescing into; null once the step is closed.</summary>
    internal string? CoalesceKey { get; set; }
    internal DateTime At { get; set; }

    internal void Extend(string? after) => Changes = new[] { Changes[0] with { After = after } };
}

/// <summary>
/// A record's undo history. Every edit path — the editor, table cells, bulk apply, compare's take,
/// undo all — goes through here, so Ctrl+Z reaches all of them and a closed tab keeps its history.
/// </summary>
public sealed class EditHistory
{
    /// <summary>Consecutive edits to one key closer together than this (typing, scrubbing) are one step.</summary>
    private static readonly TimeSpan CoalesceWindow = TimeSpan.FromMilliseconds(1200);

    private readonly AssetRecord _record;
    private readonly List<EditStep> _undo = new();
    private readonly List<EditStep> _redo = new();

    public EditHistory(AssetRecord record) => _record = record;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public EditStep? NextUndo => _undo.Count > 0 ? _undo[^1] : null;
    public EditStep? NextRedo => _redo.Count > 0 ? _redo[^1] : null;

    /// <summary>When a step was last undone here (Redo then takes back whichever undo was newest).</summary>
    public DateTime LastUndoAt { get; private set; }

    /// <summary>
    /// Records one key's edit (already written to the record). A <paramref name="continuous"/> edit
    /// (a scrub tick, an arrow step) merges into a run of such edits to the same key; anything else
    /// (a typed commit, a toggle, a pick) is a step of its own. Returns the step it landed in.
    /// </summary>
    public EditStep RecordEdit(string key, string? before, string? after, bool continuous = false, ExtensionTarget? extension = null)
    {
        var now = DateTime.UtcNow;
        _redo.Clear();
        if (continuous && _undo.Count > 0 && _undo[^1] is { CoalesceKey: { } k } last
            && k.Equals(key, StringComparison.OrdinalIgnoreCase) && now - last.At < CoalesceWindow)
        {
            last.Extend(after);
            last.At = now;
            return last;
        }
        var change = new PropertyChange(key, before, after) { Extension = extension };
        var step = new EditStep(new[] { change }) { CoalesceKey = continuous ? key : null, At = now };
        _undo.Add(step);
        return step;
    }

    /// <summary>Records a multi-key step (already written to the record) that never merges with its neighbours.</summary>
    public void RecordStep(EditStep step)
    {
        if (step.Changes.Count == 0)
            return;
        step.At = DateTime.UtcNow;
        _undo.Add(step);
        _redo.Clear();
    }

    /// <summary>Forgets every step (the session was discarded: there is nothing left to undo to).</summary>
    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
    }

    /// <summary>Ends the current typing/scrubbing run so the next edit starts a new step.</summary>
    public void CloseRun()
    {
        if (_undo.Count > 0)
            _undo[^1].CoalesceKey = null;
    }

    /// <summary>Reverts the newest step on the record and returns it (null when there is none).</summary>
    public EditStep? Undo()
    {
        if (_undo.Count == 0)
            return null;
        var step = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        LastUndoAt = DateTime.UtcNow;
        step.CoalesceKey = null;
        for (var i = step.Changes.Count - 1; i >= 0; i--)
            Write(step.Changes[i], step.Changes[i].Before);
        _redo.Add(step);
        return step;
    }

    /// <summary>Re-applies the newest undone step and returns it (null when there is none).</summary>
    public EditStep? Redo()
    {
        if (_redo.Count == 0)
            return null;
        var step = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        foreach (var change in step.Changes)
            Write(change, change.After);
        _undo.Add(step);
        return step;
    }

    /// <summary>Writes (or, for null, removes) one key and returns what it held before.</summary>
    public static string? Set(AssetRecord record, string key, string? value)
    {
        var before = record.Properties.TryGetValue(key, out var v) ? v : null;
        record.ValuesTouched = true;
        if (value is null)
            record.Properties.Remove(key);
        else
            record.Properties[key] = value;
        return before;
    }

    private void Write(PropertyChange change, string? value)
    {
        if (change.Extension is not { } x)
            Set(_record, change.Key, value);
        else if (x.GdtOf(_record) is { } gdt)
            ExtensionSidecar.Of(gdt).Set(_record.Name, x.Id, change.Key, value);
    }
}
