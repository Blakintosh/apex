using System;
using System.Collections.Generic;
using System.Linq;
using Apex.Editor.Models;

namespace Apex.Editor.ViewModels;

/// <summary>
/// The xanim editor: the preview sits in the editor above a notetracks dock (timeline lanes and one table of every
/// note), and the property form leaves the centre for a short panel on the right.
/// </summary>
public sealed partial class AssetEditorViewModel
{
    /// <summary>An xanim: the editor lays out as preview, notetracks dock and a short properties panel.</summary>
    public bool IsAnim => Record.Type.Equals("xanim", StringComparison.OrdinalIgnoreCase);

    /// <summary>Every value edit on a row (typed, undone, synced from the record): the dock follows notetrack keys here.</summary>
    public event Action<PropertyItemViewModel>? ValueEdited;

    /// <summary>The row for <paramref name="key"/>, or null when the schema has none.</summary>
    public PropertyItemViewModel? FindRow(string key) => _rowByKey.GetValueOrDefault(key);

    /// <summary>
    /// Writes several values as one undo step (adding or removing a note sets its action, frame and parameters together),
    /// then brings the rows, rules and preview to them through the usual sync. Keys already at their value are skipped.
    /// </summary>
    public void SetValues(IReadOnlyList<(string Key, string Value)> values, string description)
    {
        var changes = new List<PropertyChange>();
        foreach (var (key, value) in values)
        {
            var current = _rowByKey.TryGetValue(key, out var row) ? row.RawValue : Record.Properties.GetValueOrDefault(key);
            if (current == value)
                continue;
            changes.Add(new PropertyChange(key, EditHistory.Set(Record, key, value), value));
        }
        if (changes.Count == 0)
            return;
        Record.History.RecordStep(new EditStep(changes));
        IsPreview = false;
        SyncFromRecord();
        LastEditedKeys = changes.Select(c => c.Key).ToList();
        _onEdited(this);
        LastHistoryText = description;
        Owner?.OnNewValueEdit();
    }

    /// <summary>
    /// The preview the xanim editor shows above its dock: this anim's own, unless it is popped out to its own window
    /// (then the dock takes the room). Null for any other type.
    /// </summary>
    public PreviewPaneViewModel? CentrePreview => IsAnim && Owner?.IsPreviewFloating != true ? PreviewPane : null;

    public bool HasCentrePreview => CentrePreview is not null;

    /// <summary>The preview moved between the editor and its own window.</summary>
    public void NotifyPreviewPlacement()
    {
        OnPropertyChanged(nameof(CentrePreview));
        OnPropertyChanged(nameof(HasCentrePreview));
    }

    private NotetrackDockViewModel? _notetracks;

    /// <summary>The notetracks dock under an xanim's preview (built on first use; null for other types).</summary>
    public NotetrackDockViewModel? Notetracks => IsAnim ? _notetracks ??= new NotetrackDockViewModel(this) : null;
}
