using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Apex.Editor.Services.Preview.Notetracks;

namespace Apex.Editor.ViewModels;

/// <summary>The timeline lanes of the notetracks dock, top to bottom.</summary>
public enum NotetrackLane { Notes, FromAnim, Fx, Sound }

/// <summary>One marker as the dock's lanes draw it. <see cref="Frame"/> is the note's own, even past the clip's end.</summary>
public sealed record LaneMarker(string Key, int Frame, NotetrackLane Lane, string Text, bool IsReadOnly, bool CanRetime,
    string? Problem, bool IsSilent, string Tip);

/// <summary>
/// One row of the dock's table: a GDT note (its frame, action and parameters edit through the asset's own rows, so they
/// validate and undo like any field) or a note exported in the xanim (read-only).
/// </summary>
public sealed partial class NotetrackRowViewModel : ObservableObject
{
    private readonly AssetEditorViewModel _tab;

    public NotetrackRowViewModel(string key, NotetrackMarker marker, NotetrackLane lane, AssetEditorViewModel tab)
    {
        Key = key;
        _marker = marker;
        _tab = tab;
        Lane = lane;
        if (marker.Id is { } id)
        {
            if (GdtNotetracks.HasFrame(id))
                FrameItem = tab.FindRow(id + "frame");
            ActionItem = tab.FindRow(id + "action");
            Param1Item = tab.FindRow(id + "actionparam1");
            Param2Item = tab.FindRow(id + "actionparam2");
            NameParams();
            LinkItem = GdtNotetracks.HasFrame(id) ? tab.FindRow(id + "useexistingnote") : null;
            if (LinkItem is not null)
                LinkItem.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(PropertyItemViewModel.RawValue))
                    {
                        OnPropertyChanged(nameof(IsLinked));
                        OnPropertyChanged(nameof(ShowFrameField));
                        OnPropertyChanged(nameof(CanRetime));
                        OnPropertyChanged(nameof(LinkTip));
                        OnPropertyChanged(nameof(FrameText));
                    }
                };
        }
    }

    /// <summary>The note borrows an exported note's frame (xanim.awi's Use Existing Note): its own frame is set aside.</summary>
    public bool IsLinked => !string.IsNullOrWhiteSpace(LinkItem?.RawValue);

    /// <summary>The frame field shows for a custom note on its own frame; a linked one shows the frame it borrows.</summary>
    public bool ShowFrameField => HasFrameItem && !IsLinked;

    public string? LinkTip => IsLinked ? $"On the frame of the exported note {LinkItem!.RawValue.Trim()}. Right-click to use its own frame." : null;

    public string Key { get; }

    /// <summary>The marker this row shows (replaced as the timeline is rebuilt; the row itself stays).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FrameText), nameof(ExportAction), nameof(ExportParam), nameof(Name))]
    private NotetrackMarker _marker;

    public NotetrackLane Lane { get; }

    public string? Id => Marker.Id;

    public bool IsReadOnly => Lane == NotetrackLane.FromAnim;
    public bool IsEditable => !IsReadOnly;

    public PropertyItemViewModel? FrameItem { get; }
    public PropertyItemViewModel? ActionItem { get; }
    public PropertyItemViewModel? Param1Item { get; }
    public PropertyItemViewModel? Param2Item { get; }
    public PropertyItemViewModel? LinkItem { get; }

    /// <summary>A custom note with an editable frame (startup and shutdown notes sit on the clip's ends).</summary>
    public bool HasFrameItem => FrameItem is not null;

    /// <summary>Whether a drag on the lanes can retime it: its own frame, not one borrowed from an exported note.</summary>
    public bool CanRetime => FrameItem is { IsRuleDisabled: false } && string.IsNullOrWhiteSpace(LinkItem?.RawValue);

    /// <summary>The note's one name (<see cref="NotetrackMarker.Name"/>): "Sound · wpn_fire", or an exported note's own.</summary>
    public string Name => Marker.Name;

    public string TrackText => Lane switch
    {
        NotetrackLane.Fx => "FX",
        NotetrackLane.Sound => "Sound",
        NotetrackLane.FromAnim => "From anim",
        _ => "Notes",
    };

    /// <summary>The frame where there is no field for it: an exported note's, or a startup / shutdown note's end.</summary>
    public string FrameText => Id is { } id && id.Contains("startupnote", StringComparison.Ordinal) ? "Start"
        : Id is { } end && end.Contains("shutdownnote", StringComparison.Ordinal) ? "End"
        : IsLinked ? $"↪ {Marker.Frame.ToString(CultureInfo.InvariantCulture)}"
        : Marker.Frame.ToString(CultureInfo.InvariantCulture);

    /// <summary>An exported note's kind: the action its prefix stands for (Sound, Rumble), else the part before '#', or its whole name.</summary>
    public string ExportAction => NotetrackMarker.ExportKind(Marker.Label)
        ?? (Marker.Label.IndexOf('#') is var hash and >= 0 ? Marker.Label[..(hash + 1)] : Marker.Label);

    /// <summary>An exported note's parameter: what follows '#'.</summary>
    public string ExportParam => Marker.Label.IndexOf('#') is var hash and >= 0 && hash + 1 < Marker.Label.Length ? Marker.Label[(hash + 1)..] : "";

    /// <summary>A parameter the note's action uses (xanim.awi shows it for this action); with no deffile run, both show.</summary>
    private bool Uses(PropertyItemViewModel? item) => item is not null && (!_tab.HasDisplayRules || !item.IsRuleHidden);

    public bool ShowParam1 => IsEditable && Uses(Param1Item);

    public bool ShowParam2 => IsEditable && Uses(Param2Item);

    /// <summary>What the deffile calls the first parameter for this action ("Sound Alias", "FX", "Rumble"): the field's placeholder.</summary>
    public string Param1Name => (Id is { } id ? _tab.EntryTitle(id + "actionparam1") : null) ?? "Parameter 1";

    public string Param2Name => (Id is { } id ? _tab.EntryTitle(id + "actionparam2") : null) ?? "Parameter 2";

    /// <summary>
    /// A note this session added (its action was empty or None when the asset opened): its fields carry no ↶ each, since
    /// undoing the add is one Ctrl+Z (or Remove note) and every field would otherwise say "changed".
    /// </summary>
    public bool IsAdded => ActionItem is { } action && action.BaselineValue.Trim() is "" or "None";

    [ObservableProperty]
    private bool _isSelected;

    /// <summary>What is wrong with this note (a field's problem, or a frame past the clip's end), or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    private string? _problem;

    public bool HasProblem => Problem is not null;

    public IEnumerable<PropertyItemViewModel> Items =>
        new[] { FrameItem, ActionItem, Param1Item, Param2Item, LinkItem }.OfType<PropertyItemViewModel>();

    /// <summary>Each parameter field says what the deffile calls it for the current action.</summary>
    private void NameParams()
    {
        if (Param1Item is not null) Param1Item.PlaceholderName = Param1Name;
        if (Param2Item is not null) Param2Item.PlaceholderName = Param2Name;
    }

    /// <summary>The rule-driven parts changed (an action picks which parameters show, and what the deffile calls them).</summary>
    public void RefreshRules()
    {
        OnPropertyChanged(nameof(ShowParam1));
        OnPropertyChanged(nameof(ShowParam2));
        OnPropertyChanged(nameof(Param1Name));
        OnPropertyChanged(nameof(Param2Name));
        NameParams();
        OnPropertyChanged(nameof(CanRetime));
        OnPropertyChanged(nameof(IsAdded));
    }
}

/// <summary>
/// The notetracks dock under an xanim's preview: a transport, one timeline with a lane per kind of note (Notes, the
/// xanim's own exported notes, FX, Sound) and one table of every note. Selection is shared between the lanes and the
/// table; only the lanes move the playhead (selecting a row never seeks, pauses or plays). Adding, retiming and removing
/// notes write the GDT keys xanim.awi lays out, each as one undo step.
/// </summary>
public sealed partial class NotetrackDockViewModel : ObservableObject
{
    private readonly AssetEditorViewModel _tab;
    private readonly Dictionary<string, NotetrackRowViewModel> _rows = new(StringComparer.Ordinal);
    private bool _rulesRefreshPosted;

    public NotetrackDockViewModel(AssetEditorViewModel tab)
    {
        _tab = tab;
        Anim = tab.PreviewPane?.Content as AnimPreviewViewModel;
        if (Anim is not null)
        {
            Anim.PropertyChanged += Anim_PropertyChanged;
            Anim.Timeline.PropertyChanged += Timeline_PropertyChanged;
        }
        tab.ValueEdited += Tab_ValueEdited;
        Sync();
    }

    /// <summary>The anim the transport drives (null without the install: the table still edits the notes).</summary>
    public AnimPreviewViewModel? Anim { get; }

    public bool HasAnim => Anim is not null;

    public NotetrackTimeline? Timeline => Anim?.Timeline;

    public RangeObservableCollection<NotetrackRowViewModel> Rows { get; } = new();

    /// <summary>The lanes shown: From anim only when the xanim has notes of its own.</summary>
    [ObservableProperty]
    private IReadOnlyList<NotetrackLane> _lanes = new[] { NotetrackLane.Notes, NotetrackLane.Fx, NotetrackLane.Sound };

    [ObservableProperty]
    private IReadOnlyList<LaneMarker> _laneMarkers = Array.Empty<LaneMarker>();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedKey))]
    private NotetrackRowViewModel? _selected;

    public string? SelectedKey => Selected?.Key;

    /// <summary>How many notes there are, the entry's and the xanim's own together (the header's count; its tip splits it).</summary>
    [ObservableProperty]
    private string _countText = "";

    [ObservableProperty]
    private string _countTip = "";

    [ObservableProperty]
    private bool _isEmpty = true;

    /// <summary>The last frame (0 while no clip is loaded: the lanes then say so).</summary>
    public int LastFrame => Anim?.LastFrame ?? 0;

    /// <summary>Where Add puts a note: the playhead, at least frame 1 (xanim.awi's lowest).</summary>
    public int AddFrame => Math.Max(1, (int)Math.Round(Anim?.CurrentFrame ?? 1));

    /// <summary>The Add button's tip: where the note will land (the label stays put while the playhead moves).</summary>
    public string AddTip => (Anim?.CurrentFrame ?? 1) < 0.5
        ? "Add a sound note at frame 1, the first a note can use (Insert). Double-click a lane to add one at that frame."
        : $"Add a sound note at the playhead, frame {AddFrame.ToString(CultureInfo.InvariantCulture)} (Insert). Double-click a lane to add one at that frame.";

    /// <summary>Raised after a note is added: the view puts the keyboard on its first parameter.</summary>
    public event Action<NotetrackRowViewModel>? NoteAdded;

    /// <summary>Raised to put the keyboard in a note's first parameter (a double-click on its marker, Enter on the lanes).</summary>
    public event Action<NotetrackRowViewModel>? EditRequested;

    /// <summary>Raised when a note is selected from the lanes: the view brings its row into view.</summary>
    public event Action<NotetrackRowViewModel>? RevealRequested;

    private void Anim_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AnimPreviewViewModel.CurrentFrame):
                OnPropertyChanged(nameof(AddFrame));
                OnPropertyChanged(nameof(AddTip));
                break;
            case nameof(AnimPreviewViewModel.LastFrame):
                OnPropertyChanged(nameof(LastFrame));
                break;
        }
    }

    private void Timeline_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(NotetrackTimeline.Markers) or nameof(NotetrackTimeline.LastFrameIndex))
            Sync();
    }

    private void Tab_ValueEdited(PropertyItemViewModel item)
    {
        if (!GdtNotetracks.IsNotetrackKey(item.Key))
            return;
        // With a clip loaded the timeline rebuilds its markers from this edit and Sync follows; without one, the rows
        // are read from the entry here.
        if (Timeline is not { LastFrameIndex: > 0 })
            Sync();
        else
            RefreshProblems();
        // The deffile re-runs after this edit is told (an action picks its parameters' names): read them once it has.
        PostRulesRefresh();
    }

    private void PostRulesRefresh()
    {
        if (_rulesRefreshPosted)
            return;
        _rulesRefreshPosted = true;
        Dispatcher.UIThread.Post(() =>
        {
            _rulesRefreshPosted = false;
            foreach (var row in _rows.Values)
                row.RefreshRules();
        });
    }

    private void Row_ItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PropertyItemViewModel.Problem))
            RefreshProblems();
        else if (e.PropertyName is nameof(PropertyItemViewModel.IsRuleHidden) or nameof(PropertyItemViewModel.IsRuleDisabled))
            PostRulesRefresh();
    }

    /// <summary>
    /// Brings the rows and lane markers to the timeline's markers (or, with no clip, the entry's notes). Rows are kept by
    /// note: an edit never rebuilds the row being typed in, and the order changes only when notes come or go.
    /// </summary>
    public void Sync()
    {
        IReadOnlyList<NotetrackMarker> markers;
        if (Timeline is { LastFrameIndex: > 0 } timeline)
            markers = timeline.Markers;
        else
            markers = GdtNotetracks.Read(_tab.Record.Properties, Array.Empty<(string, int)>(), 10_000);

        var wanted = new List<(string Key, NotetrackMarker Marker, NotetrackLane Lane)>();
        var exportSeen = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var m in markers)
        {
            if (m.Source == NotetrackSource.Gdt && m.Id is { } id)
                wanted.Add((id, m, LaneOf(id)));
            else if (m.Source == NotetrackSource.Export)
            {
                var key = $"export:{m.Label}@{m.Frame.ToString(CultureInfo.InvariantCulture)}";
                var n = exportSeen[key] = exportSeen.GetValueOrDefault(key) + 1;
                wanted.Add((n == 1 ? key : $"{key}#{n}", m, NotetrackLane.FromAnim));
            }
        }

        var sameSet = wanted.Count == _rows.Count && wanted.All(w => _rows.ContainsKey(w.Key));
        foreach (var (key, marker, lane) in wanted)
        {
            if (_rows.TryGetValue(key, out var row))
                row.Marker = marker;
            else
            {
                row = new NotetrackRowViewModel(key, marker, lane, _tab);
                foreach (var item in row.Items)
                    item.PropertyChanged += Row_ItemChanged;
                _rows[key] = row;
            }
        }
        if (!sameSet)
        {
            var keep = wanted.Select(w => w.Key).ToHashSet(StringComparer.Ordinal);
            foreach (var gone in _rows.Keys.Where(k => !keep.Contains(k)).ToList())
            {
                foreach (var item in _rows[gone].Items)
                    item.PropertyChanged -= Row_ItemChanged;
                _rows.Remove(gone);
            }
            // Every note by frame, the entry's and the xanim's together (the lanes' order breaks a tie), so the table
            // reads in the order the anim plays them.
            var ordered = wanted
                .OrderBy(w => w.Marker.Frame)
                .ThenBy(w => w.Lane)
                .Select(w => _rows[w.Key])
                .ToList();
            if (Rows.Count == 0 || ordered.Count == 0)
                Rows.ReplaceAll(ordered);
            else
                SpliceRows(ordered);
            if (Selected is { } selected && !_rows.ContainsKey(selected.Key))
                Selected = null;
            PostRulesRefresh();
        }

        var gdt = wanted.Count(w => w.Lane != NotetrackLane.FromAnim);
        var export = wanted.Count - gdt;
        CountText = wanted.Count > 0 ? wanted.Count.ToString(CultureInfo.InvariantCulture) : "";
        CountTip = $"{Count(gdt, "note")} in the GDT entry, {Count(export, "note")} exported in the xanim";
        IsEmpty = wanted.Count == 0;
        AddAtPlayheadCommand.NotifyCanExecuteChanged();
        Lanes = export > 0
            ? new[] { NotetrackLane.Notes, NotetrackLane.FromAnim, NotetrackLane.Fx, NotetrackLane.Sound }
            : new[] { NotetrackLane.Notes, NotetrackLane.Fx, NotetrackLane.Sound };
        RefreshProblems();
    }

    private static string Count(int n, string word) => n == 1 ? $"1 {word}" : $"{n.ToString(CultureInfo.InvariantCulture)} {word}s";

    /// <summary>
    /// A note came or went: only its row is inserted or removed, the others keep their place (and their editors, one of
    /// which may have the keyboard). A new row goes where its frame puts it.
    /// </summary>
    private void SpliceRows(List<NotetrackRowViewModel> ordered)
    {
        var keep = ordered.ToHashSet();
        for (var i = Rows.Count - 1; i >= 0; i--)
            if (!keep.Contains(Rows[i]))
                Rows.RemoveAt(i);
        var present = Rows.ToHashSet();
        foreach (var row in ordered)
        {
            if (present.Contains(row))
                continue;
            var at = 0;
            while (at < Rows.Count && (Rows[at].Marker.Frame < row.Marker.Frame
                                       || (Rows[at].Marker.Frame == row.Marker.Frame && Rows[at].Lane <= row.Lane)))
                at++;
            Rows.Insert(at, row);
            present.Add(row);
        }
    }

    private static NotetrackLane LaneOf(string id) =>
        id.StartsWith("fx_", StringComparison.Ordinal) ? NotetrackLane.Fx
        : id.StartsWith("sound_", StringComparison.Ordinal) ? NotetrackLane.Sound
        : NotetrackLane.Notes;

    /// <summary>Each note's ⚠ (a field's problem, or a frame past the clip's end), then the lane markers that show them.</summary>
    private void RefreshProblems()
    {
        var last = LastFrame;
        foreach (var row in _rows.Values)
        {
            string? problem = row.Items.Select(i => i.Problem).FirstOrDefault(p => p is not null);
            if (problem is null && last > 0 && !row.IsReadOnly && row.Marker.Frame > last)
                problem = $"Frame {row.Marker.Frame.ToString(CultureInfo.InvariantCulture)} is past the anim's last frame ({last.ToString(CultureInfo.InvariantCulture)})";
            row.Problem = problem;
        }
        LaneMarkers = Rows.Select(r => new LaneMarker(r.Key, r.Marker.Frame, r.Lane, r.Name, r.IsReadOnly, r.CanRetime, r.Problem,
            r.Marker.IsSilent, r.Problem is { } p ? $"{r.Marker.Describe()}\n⚠ {p}" : r.Marker.Describe())).ToList();
    }

    partial void OnSelectedChanged(NotetrackRowViewModel? oldValue, NotetrackRowViewModel? newValue)
    {
        if (oldValue is not null)
            oldValue.IsSelected = false;
        if (newValue is not null)
            newValue.IsSelected = true;
    }

    /// <summary>
    /// Selects a note. <paramref name="seek"/> also puts the playhead on its frame (paused): the lanes do, the table never
    /// does (picking a row to edit must not stop playback or play its sound).
    /// </summary>
    public void Select(NotetrackRowViewModel? row, bool seek = false)
    {
        Selected = row;
        if (seek && row is not null && Anim is { LastFrame: > 0 } anim)
            anim.GoToFrame(Math.Min(row.Marker.Frame, anim.LastFrame));
    }

    public void SelectKey(string? key, bool seek = false) =>
        Select(key is not null && _rows.TryGetValue(key, out var row) ? row : null, seek);

    public NotetrackRowViewModel? RowOfKey(string key) => _rows.GetValueOrDefault(key);

    /// <summary>A note picked on the lanes: selected, its row brought into view.</summary>
    public void SelectFromLanes(string key, bool seek)
    {
        SelectKey(key, seek);
        if (Selected is { } row)
            RevealRequested?.Invoke(row);
    }

    /// <summary>
    /// The note before or after the selected one (or, with none selected, the playhead) in playing order: selected, its row
    /// shown and the playhead on it. Ctrl+← → on the lanes.
    /// </summary>
    public void SelectNeighbour(int direction)
    {
        if (Rows.Count == 0)
            return;
        var ordered = Rows.OrderBy(r => r.Marker.Frame).ThenBy(r => r.Lane).ToList();
        NotetrackRowViewModel? next;
        if (Selected is { } current && ordered.IndexOf(current) is var at and >= 0)
            next = direction > 0 ? ordered.ElementAtOrDefault(at + 1) : at > 0 ? ordered[at - 1] : null;
        else
        {
            var frame = Anim?.CurrentFrame ?? 0;
            next = direction > 0 ? ordered.FirstOrDefault(r => r.Marker.Frame > frame + 1e-3) : ordered.LastOrDefault(r => r.Marker.Frame < frame - 1e-3);
        }
        if (next is not null)
            SelectFromLanes(next.Key, seek: true);
    }

    /// <summary>The selected note's first parameter takes the keyboard (Enter on the lanes, a double-click on its marker).</summary>
    public void Edit(string key)
    {
        if (_rows.TryGetValue(key, out var row))
        {
            Select(row);
            if (row.IsEditable)
                EditRequested?.Invoke(row);
            else
                RevealRequested?.Invoke(row);
        }
    }

    /// <summary>The row for a GDT note id, if it is in the table.</summary>
    public NotetrackRowViewModel? RowForKey(string propertyKey) =>
        Rows.FirstOrDefault(r => r.Id is { } id && propertyKey.StartsWith(id, StringComparison.Ordinal)
                                 && propertyKey.Length > id.Length && !char.IsAsciiDigit(propertyKey[id.Length]));

    /// <summary>The first free note of <paramref name="lane"/>'s kind (APE's next empty slot), or null when all are used.</summary>
    private string? FreeId(NotetrackLane lane)
    {
        var prefix = lane switch
        {
            NotetrackLane.Fx => "fx_customnote",
            NotetrackLane.Sound => "sound_customnote",
            _ => "customnote",
        };
        for (var i = 0; i < GdtNotetracks.Capacity(prefix); i++)
        {
            var id = prefix + i.ToString(CultureInfo.InvariantCulture);
            var action = (_tab.FindRow(id + "action")?.RawValue ?? _tab.Record.Properties.GetValueOrDefault(id + "action", "")).Trim();
            if (action.Length == 0 || action == "None")
                return id;
        }
        return null;
    }

    public bool CanAdd(NotetrackLane lane) => lane != NotetrackLane.FromAnim && FreeId(lane) is not null;

    /// <summary>
    /// Adds a note on <paramref name="lane"/> at <paramref name="frame"/> in the next free slot, as one undo step: a sound
    /// for Notes and Sound, an effect for FX (the action is a picker on the row, one click to change).
    /// </summary>
    public NotetrackRowViewModel? Add(NotetrackLane lane, int frame)
    {
        if (lane == NotetrackLane.FromAnim || FreeId(lane) is not { } id)
            return null;
        frame = Math.Clamp(frame, 1, 10_000);
        var action = lane == NotetrackLane.Fx ? "Play Fx" : "Sound";
        _tab.SetValues(new[]
        {
            (id + "action", action),
            (id + "frame", frame.ToString(CultureInfo.InvariantCulture)),
            (id + "actionparam1", ""),
            (id + "actionparam2", ""),
            (id + "useexistingnote", ""),
        }, $"Added a {action} note at frame {frame.ToString(CultureInfo.InvariantCulture)}");
        Sync();
        if (!_rows.TryGetValue(id, out var row))
            return null;
        Select(row);
        NoteAdded?.Invoke(row);
        return row;
    }

    /// <summary>
    /// Insert on the lanes: a note at the playhead on <paramref name="lane"/>; on From anim, the selected exported note
    /// made into a note on its frame (Add as note), or a note on Notes when none is selected there.
    /// </summary>
    public NotetrackRowViewModel? AddOnLane(NotetrackLane lane)
    {
        if (lane == NotetrackLane.FromAnim)
            return Selected is { IsReadOnly: true } export ? AddFromExport(export) : Add(NotetrackLane.Notes, AddFrame);
        return Add(lane, AddFrame);
    }

    [RelayCommand(CanExecute = nameof(CanAddNote))]
    private void AddAtPlayhead() => Add(NotetrackLane.Notes, AddFrame);

    private bool CanAddNote() => CanAdd(NotetrackLane.Notes);

    /// <summary>The xanim's own notes, by name, for borrowing a frame (APE's Use Existing Note list).</summary>
    public IReadOnlyList<string> ExportNoteNames =>
        Rows.Where(r => r.IsReadOnly).Select(r => r.Marker.Label).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Puts a note on an exported note's frame (or, with null, back on its own), one undo step.</summary>
    public void LinkTo(NotetrackRowViewModel row, string? exportName)
    {
        if (row.Id is not { } id || row.LinkItem is null)
            return;
        _tab.SetValues(new[] { (id + "useexistingnote", exportName ?? "") },
            exportName is null ? $"{row.Name} on its own frame" : $"{row.Name} on {exportName}'s frame");
        Sync();
    }

    /// <summary>
    /// An exported note made into an entry note on its frame (the entry's note borrows it, so it follows the export):
    /// a sound for sndnt#, a rumble for rmbnt#, with the name after '#' as its parameter.
    /// </summary>
    public NotetrackRowViewModel? AddFromExport(NotetrackRowViewModel export)
    {
        if (!export.IsReadOnly || FreeId(NotetrackLane.Notes) is not { } id)
            return null;
        var label = export.Marker.Label;
        var action = NotetrackMarker.ExportKind(label) ?? "Sound";
        _tab.SetValues(new[]
        {
            (id + "action", action),
            (id + "frame", Math.Max(1, export.Marker.Frame).ToString(CultureInfo.InvariantCulture)),
            (id + "actionparam1", export.ExportParam),
            (id + "actionparam2", ""),
            (id + "useexistingnote", label),
        }, $"Added {label} as a note");
        Sync();
        if (!_rows.TryGetValue(id, out var row))
            return null;
        Select(row);
        NoteAdded?.Invoke(row);
        return row;
    }

    /// <summary>
    /// A marker dragged (or nudged) to <paramref name="frame"/>: its frame field takes it (one undo step). A nudge moves the
    /// playhead with it (<paramref name="seek"/>); a drag has already scrubbed the preview there.
    /// </summary>
    public void Retime(string key, int frame, bool seek = true)
    {
        if (!_rows.TryGetValue(key, out var row) || !row.CanRetime)
            return;
        var value = Math.Clamp(frame, 1, 10_000).ToString(CultureInfo.InvariantCulture);
        if (row.FrameItem!.RawValue != value)
            row.FrameItem.RawValue = value;
        Select(row, seek);
    }

    /// <summary>Shift+← → on the lanes: the selected note one frame earlier or later.</summary>
    public void Nudge(int delta)
    {
        if (Selected is not { CanRetime: true } row)
            return;
        var to = Math.Clamp(row.Marker.Frame + delta, 1, Math.Max(1, LastFrame > 0 ? Math.Max(LastFrame, row.Marker.Frame) : 10_000));
        if (to != row.Marker.Frame)
            Retime(row.Key, to);
    }

    /// <summary>Removes a GDT note the way APE does: its action back to None (one undo step; Ctrl+Z brings it back).</summary>
    public void Remove(string key)
    {
        if (!_rows.TryGetValue(key, out var row) || row.IsReadOnly || row.Id is not { } id)
            return;
        _tab.SetValues(new[] { (id + "action", "None") }, $"Removed {row.Name}");
        Sync();
    }
}
