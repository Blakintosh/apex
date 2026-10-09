using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Apex.Editor.Commands;
using Apex.Editor.Controls;
using Apex.Editor.ViewModels;

namespace Apex.Editor.Views;

public partial class NotetrackDockView : UserControl
{
    /// <summary>A table row's height (its 1 px top line included): the table shows a whole number of them.</summary>
    public const double RowHeight = 32;

    /// <summary>Rows of the table the lanes always leave room for, however many notes stack up in them.</summary>
    public const int MinTableRows = 3;

    /// <summary>The table's column headings, above its rows.</summary>
    private const double TableHeaderHeight = 28;

    /// <summary>The ruler and a lane at least, when the dock is too short for the lanes and the table's rows.</summary>
    private const double MinLanesHeight = 64;

    private NotetrackDockViewModel? _vm;
    private bool _resumeAfterScrub;

    public NotetrackDockView()
    {
        InitializeComponent();
        // The lanes: a press selects (and shows the row), a click also moves the playhead, a drag retimes.
        Lanes.MarkerPressed += m => _vm?.SelectFromLanes(m.Key, seek: false);
        Lanes.MarkerClicked += m => _vm?.SelectKey(m.Key, seek: true);
        Lanes.MarkerDoubleClicked += m => _vm?.Edit(m.Key);
        Lanes.MarkerRetimed += (m, frame) => _vm?.Retime(m.Key, frame, seek: false);
        Lanes.AddRequested += (lane, frame) => _vm?.Add(lane, frame);
        Lanes.DeleteRequested += key => _vm?.Remove(key);
        Lanes.SelectRequested += m =>
        {
            if (m is null)
                _vm?.Select(null);
            else
                _vm?.SelectFromLanes(m.Key, seek: false);
        };
        Lanes.NeighbourRequested += direction => _vm?.SelectNeighbour(direction);
        Lanes.NudgeRequested += delta => _vm?.Nudge(delta);
        Lanes.InsertRequested += lane => _vm?.AddOnLane(lane);
        Lanes.EditRequested += key => _vm?.Edit(key);
        Lanes.Scrubbing += Lanes_Scrubbing;
        // A press anywhere on a row selects its note without taking focus from the field that was pressed, and without
        // moving the playhead; a field reached by Tab selects its row the same way.
        NoteRows.AddHandler(PointerPressedEvent, Rows_PointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        NoteRows.AddHandler(GotFocusEvent, Rows_GotFocus, RoutingStrategies.Bubble);
        AddHandler(KeyDownEvent, Dock_KeyDown, RoutingStrategies.Bubble);
        DataContextChanged += (_, _) => Attach(DataContext as NotetrackDockViewModel);
        // Right-click a note (its row or its marker): what xanim.awi offers beyond the columns.
        NoteRows.ContextRequested += (_, e) => ShowNoteMenu(RowOf(e.Source), e);
        Lanes.ContextRequested += (_, e) =>
        {
            var at = e.TryGetPosition(Lanes, out var p) ? Lanes.MarkerAt(p) : null;
            ShowNoteMenu(at is null ? null : _vm?.RowOfKey(at.Key), e);
        };
        // Whole rows only: the table is cut to a multiple of the row height, the rest of the card left empty below it.
        TableHost.SizeChanged += (_, e) =>
            TableScroll.Height = Math.Max(RowHeight, Math.Floor((e.NewSize.Height + 0.5) / RowHeight) * RowHeight);
        // The lanes never take the table's room: whatever the dock's height, the table keeps MinTableRows rows.
        DockCard.SizeChanged += (_, e) => LanesScroll.MaxHeight = Math.Max(MinLanesHeight,
            e.NewSize.Height - 2 - TransportRow.Height - TableHeaderHeight - 1 - MinTableRows * RowHeight);
    }

    /// <summary>
    /// A press on the lanes that scrubs (the ruler, an empty lane, a marker dragged): playback pauses for it and picks up
    /// again on release, so the clock never fights the pointer; the sounds play as a drag.
    /// </summary>
    private void Lanes_Scrubbing(bool on)
    {
        if (_vm?.Timeline is not { } timeline || _vm.Anim is not { } anim)
            return;
        if (on)
        {
            _resumeAfterScrub = anim.IsPlaying;
            if (_resumeAfterScrub)
                anim.IsPlaying = false;
            timeline.IsScrubbing = true;
            timeline.ScrubPressed();
            return;
        }
        timeline.IsScrubbing = false;
        if (_resumeAfterScrub)
        {
            _resumeAfterScrub = false;
            anim.IsPlaying = true;
        }
    }

    /// <summary>
    /// A note's menu: an entry note can borrow an exported note's frame (Use Existing Note) or go back to its own, or be
    /// removed; an exported note can be added as an entry note on its frame. Opened by right-click or the menu key.
    /// </summary>
    private void ShowNoteMenu(NotetrackRowViewModel? row, ContextRequestedEventArgs e)
    {
        if (_vm is not { } vm || row is null)
            return;
        e.Handled = true;
        vm.Select(row);
        var menu = new ContextMenu();
        if (row.IsReadOnly)
        {
            menu.Items.Add(new MenuItem
            {
                Header = "Add as note",
                InputGesture = new KeyGesture(Key.Insert),
                IsEnabled = vm.CanAdd(NotetrackLane.Notes),
                Command = new RelayAction(() => vm.AddFromExport(row)),
            });
        }
        else
        {
            if (row.LinkItem is not null)
            {
                var use = new MenuItem { Header = "Use existing note" };
                var names = vm.ExportNoteNames;
                use.IsEnabled = names.Count > 0;
                foreach (var name in names)
                    use.Items.Add(new MenuItem
                    {
                        Header = new TextBlock { Text = name }, // asset-style names: no access-key underscores
                        ToggleType = MenuItemToggleType.Radio,
                        IsChecked = string.Equals(row.LinkItem.RawValue.Trim(), name, StringComparison.OrdinalIgnoreCase),
                        Command = new RelayAction(() => vm.LinkTo(row, name)),
                    });
                menu.Items.Add(use);
                menu.Items.Add(new MenuItem
                {
                    Header = "Use its own frame",
                    IsEnabled = row.IsLinked,
                    Command = new RelayAction(() => vm.LinkTo(row, null)),
                });
                menu.Items.Add(new Separator());
            }
            menu.Items.Add(new MenuItem
            {
                Header = "Remove note",
                InputGesture = new KeyGesture(Key.Delete),
                Command = new RelayAction(() => vm.Remove(row.Key)),
            });
        }
        NoteMenu = menu;
        menu.Open(e.Source as Control ?? this);
    }

    /// <summary>The note menu last opened (the harness invokes its items as UI Automation would).</summary>
    public ContextMenu? NoteMenu { get; private set; }

    /// <summary>A menu item's action.</summary>
    private sealed class RelayAction(Action run) : System.Windows.Input.ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => run();
    }

    /// <summary>Raised when the transport row is double-clicked (the editor collapses or restores the dock).</summary>
    public event Action? CollapseToggled;

    /// <summary>Height of the dock folded to its transport row (the row and the card's border).</summary>
    public const double CollapsedHeight = 40;

    /// <summary>Folded to the transport: the lanes and table leave the layout and the Tab order.</summary>
    public bool IsCollapsed
    {
        get => !Lanes.IsVisible;
        set
        {
            Lanes.IsVisible = !value;
            TableArea.IsVisible = !value;
            TransportRow.BorderThickness = value ? default : new Thickness(0, 0, 0, 1);
        }
    }

    private void Attach(NotetrackDockViewModel? vm)
    {
        if (_vm is not null)
        {
            _vm.NoteAdded -= FocusFirstField;
            _vm.EditRequested -= FocusFirstField;
            _vm.RevealRequested -= BringRowIntoView;
        }
        _vm = vm;
        if (vm is not null)
        {
            vm.NoteAdded += FocusFirstField;
            vm.EditRequested += FocusFirstField;
            vm.RevealRequested += BringRowIntoView;
        }
    }

    private static NotetrackRowViewModel? RowOf(object? source) =>
        (source as Visual)?.GetSelfAndVisualAncestors().OfType<Border>().FirstOrDefault(x => x.Classes.Contains("ntrow"))?.DataContext
            as NotetrackRowViewModel;

    /// <summary>The row of <paramref name="row"/> on screen (null while it isn't realised).</summary>
    public Control? ContainerOf(NotetrackRowViewModel row) => NoteRows.ContainerFromItem(row) as Control;

    private void Rows_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_vm is not null && RowOf(e.Source) is { } row && row != _vm.Selected)
            _vm.Select(row);
    }

    private void Rows_GotFocus(object? sender, FocusChangedEventArgs e)
    {
        if (_vm is not null && RowOf(e.Source) is { } row && row != _vm.Selected)
            _vm.Select(row);
    }

    /// <summary>A note picked on the lanes (or reached by a jump): its row scrolled into view.</summary>
    private void BringRowIntoView(NotetrackRowViewModel row) =>
        Dispatcher.UIThread.Post(() => ContainerOf(row)?.BringIntoView(), DispatcherPriority.Loaded);

    /// <summary>
    /// A note just added, or one asked to edit: its row in view and the keyboard in its first parameter (the sound alias),
    /// or in its action when the action takes none.
    /// </summary>
    private void FocusFirstField(NotetrackRowViewModel row)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (ContainerOf(row) is not { } container)
                return;
            container.BringIntoView();
            var editors = container.GetVisualDescendants().OfType<PropertyEditorView>().Where(v => v.DataContext == row.Param1Item && v.IsEffectivelyVisible);
            var field = editors.SelectMany(v => v.GetVisualDescendants()).OfType<InputElement>()
                .FirstOrDefault(c => c is TextBox or ComboBox or ScrubNumberBox && c.Focusable && c.IsEffectivelyVisible)
                ?? container.GetVisualDescendants().OfType<NoteActionPicker>().FirstOrDefault()?.Box;
            field?.Focus(NavigationMethod.Tab);
        }, DispatcherPriority.Loaded);
    }

    /// <summary>Selects the note a property key belongs to (a palette jump or an undo landing on it) and shows its row.</summary>
    public bool Reveal(string propertyKey)
    {
        if (_vm?.RowForKey(propertyKey) is not { } row)
            return false;
        _vm.Select(row);
        BringRowIntoView(row);
        return true;
    }

    private void TransportRow_DoubleTapped(object? sender, TappedEventArgs e)
    {
        // Only the empty row: a double-click on Play or Add is two clicks on that button.
        for (var v = e.Source as Visual; v is not null && v != TransportRow; v = v.GetVisualParent())
            if (v is Button or ToggleButton)
                return;
        CollapseToggled?.Invoke();
    }

    /// <summary>
    /// The dock's keys from anywhere in it outside a field: Insert adds a note at the playhead; Space plays, ← → and , .
    /// step a frame, Home / End go to the ends (the preview's own keys, so they work wherever the anim is).
    /// </summary>
    private void Dock_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || _vm is not { } vm)
            return;
        for (var v = e.Source as Visual; v is not null && v != this; v = v.GetVisualParent())
            if (v is TextBox or ComboBox or ScrubNumberBox or ChoiceBox or SuggestBox or NoteActionPicker)
                return;
        if (e.Key == Key.Insert && e.KeyModifiers == KeyModifiers.None)
        {
            vm.AddAtPlayheadCommand.Execute(null);
            e.Handled = true;
            return;
        }
        if (vm.Anim is not { } anim || CommandCatalog.Match(CommandScope.AnimPreview, e) is not { } command || CommandCatalog.IsTyping(command, e))
            return;
        MainViewModel.RunAnimCommand(command.Id, anim);
        e.Handled = true;
    }
}
