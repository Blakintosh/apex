using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Apex.Editor.Controls;
using Apex.Editor.ViewModels;

namespace Apex.Editor.Views;

public partial class LineListEditor : UserControl
{
    private Popup? _flyout;
    private LineListEditor? _expanded;

    public LineListEditor()
    {
        InitializeComponent();
        // Handled keys too: the row editor's own Enter (commit) runs first, and the move to the next item follows it.
        AddHandler(KeyDownEvent, List_KeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(LostFocusEvent, List_LostFocus, RoutingStrategies.Bubble);
        AddHandler(Button.ClickEvent, List_Click, RoutingStrategies.Bubble);
        AddRow.TemplateApplied += (_, _) => Dispatcher.UIThread.Post(SetPlaceholders, DispatcherPriority.Loaded);
        ItemsScroll.ScrollChanged += (_, _) => KeepScrollBarClear();
    }

    /// <summary>The scrollbar's width, kept clear of the items while the list scrolls.</summary>
    private const double ScrollGutter = 14;

    /// <summary>
    /// A list long enough to scroll draws its scrollbar over the items' right edge, where ✕ sits: while it scrolls, the
    /// items (and the Add field, so its columns stay under theirs) stop short of it.
    /// </summary>
    private void KeepScrollBarClear()
    {
        var scrolls = ItemsScroll.Extent.Height > ItemsScroll.Viewport.Height + 0.5;
        var margin = new Thickness(0, 0, scrolls ? ScrollGutter : 0, 0);
        if (ItemsList.Margin != margin)
        {
            ItemsList.Margin = margin;
            AddRow.Margin = margin;
        }
    }

    private LinesPropertyViewModel? Vm => DataContext as LinesPropertyViewModel;

    /// <summary>Shown as a one-line summary (a table cell) rather than the whole list.</summary>
    public bool IsCompact { get; private set; }

    /// <summary>The list a summary opened (null until first opened).</summary>
    public LineListEditor? Expanded => _expanded;

    public bool IsExpanded => _flyout?.IsOpen == true;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // A table cell is one 30 px line: the list lives in a popup under it there.
        IsCompact = this.FindAncestorOfType<TableView>() is not null;
        SummaryButton.IsVisible = IsCompact;
        Full.IsVisible = !IsCompact;
    }

    /// <summary>
    /// The list takes the width its row gives it and never asks for more: its fields scroll their text, as every other
    /// row's field does. (Asking would let skinOverride's two halves widen the value column and shift it off the
    /// column every other row lines up on.)
    /// </summary>
    /// <summary>The width the list was last laid out in (0 before its first layout).</summary>
    private double _laidOutWidth;

    /// <remarks>
    /// The row measures the list without a width (it takes the value column's), and a control laid out narrower than
    /// it asked is drawn at what it asked, spilling past the column: a material slot asks for its whole name. So the
    /// content is measured in the width the list was last laid out in, and a new width measures it again.
    /// </remarks>
    protected override Size MeasureOverride(Size availableSize)
    {
        var within = !IsCompact && _laidOutWidth > 0 ? availableSize.WithWidth(Math.Min(availableSize.Width, _laidOutWidth)) : availableSize;
        var size = base.MeasureOverride(within);
        return IsCompact ? size : size.WithWidth(0);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (!IsCompact && finalSize.Width != _laidOutWidth)
        {
            _laidOutWidth = finalSize.Width;
            InvalidateMeasure();
        }
        return base.ArrangeOverride(finalSize);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        // A table cell scrolled away or recycled: its popup mustn't stay open, showing a row that isn't on screen.
        if (_flyout is { IsOpen: true })
            _flyout.IsOpen = false;
    }

    private LinesPropertyViewModel? _subscribed;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_flyout is { IsOpen: true })
            _flyout.IsOpen = false;
        if (_subscribed is not null)
            _subscribed.ChooseRequested -= Choose;
        _subscribed = Vm;
        if (_subscribed is not null)
            _subscribed.ChooseRequested += Choose;
        Dispatcher.UIThread.Post(SetPlaceholders, DispatcherPriority.Loaded);
    }

    // ── Material slots: choosing shows the field, focused, its list open ─────

    /// <summary>A slot's Choose material…: its field shows (once laid out), takes focus and lists every material.</summary>
    private void Choose(LineItemViewModel item)
    {
        // The list a table cell opened chooses in its own copy; the cell's summary has no rows.
        if (IsCompact || Vm is not { } vm)
            return;
        Dispatcher.UIThread.Post(() =>
        {
            if (ItemsList.ContainerFromIndex(vm.Items.IndexOf(item)) is not { } row || Boxes(row) is not [RefField field, ..])
            {
                item.IsChoosing = false;
                return;
            }
            row.BringIntoView();
            field.Focus(NavigationMethod.Directional);
            field.SelectAll();
            field.OpenAll();
        }, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// A slot's field let go (picked, typed and left, or Esc): the slot shows what draws again. Waits for the field's own
    /// commit, and for focus to settle (a pick from its list returns focus to it).
    /// </summary>
    private void EndChoosing(LineItemViewModel item, RefField field) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (item.IsChoosing && !field.IsKeyboardFocusWithin && !field.IsSuggesting)
                item.IsChoosing = false;
        }, DispatcherPriority.Input);

    /// <summary>Focuses a slot's Replace button (the slot's stop while it isn't choosing).</summary>
    private static void FocusReplace(Control row)
    {
        if (row.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Classes.Contains("lreplace")) is { } replace)
            replace.Focus(NavigationMethod.Directional);
    }

    /// <summary>The Add field says what it adds ("Add a material"); skinOverride's halves say which is which.</summary>
    private void SetPlaceholders()
    {
        if (Vm is not { } vm)
            return;
        var boxes = AddRow.GetVisualDescendants().OfType<TextBox>().ToList();
        if (boxes.Count > 0)
            boxes[0].PlaceholderText = vm.AddItem.Placeholder;
        if (boxes.Count > 1)
            boxes[1].PlaceholderText = "Replacement";
    }

    // ── The summary (table cells) ────────────────────────────────────────────

    private void List_Click(object? sender, RoutedEventArgs e)
    {
        if (e.Source is not Button button)
            return;
        if (button == SummaryButton)
        {
            e.Handled = true;
            ToggleFlyout();
        }
        else if (button.Classes.Contains("lremove") && button.DataContext is LineItemViewModel item && Vm is { } vm)
        {
            e.Handled = true;
            var index = vm.Items.IndexOf(item);
            vm.Remove(item);
            FocusItem(index, last: false);
        }
    }

    private void ToggleFlyout()
    {
        if (Vm is not { } vm)
            return;
        if (_flyout is null)
        {
            _expanded = new LineListEditor();
            var card = new Border { Padding = new Thickness(8, 6, 8, 4), BorderThickness = new Thickness(1), Child = _expanded };
            card.Bind(Border.BackgroundProperty, this.GetResourceObservable("BgCardBrush"));
            card.Bind(Border.BorderBrushProperty, this.GetResourceObservable("LineBrush"));
            card.Bind(Border.CornerRadiusProperty, this.GetResourceObservable("RadiusOverlay"));
            card.Bind(Border.BoxShadowProperty, this.GetResourceObservable("ShadowLayer"));
            _flyout = new Popup
            {
                PlacementTarget = SummaryButton,
                Placement = PlacementMode.BottomEdgeAlignedLeft,
                VerticalOffset = 2,
                IsLightDismissEnabled = true,
                Child = card,
            };
            _flyout.Closed += (_, _) =>
            {
                if (SummaryButton.IsAttachedToVisualTree() && SummaryButton.IsVisible
                    && (_flyout.Child?.IsKeyboardFocusWithin == true || TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is null))
                    SummaryButton.Focus();
            };
            // In the cell's logical tree, so the list takes the app's styles and theme.
            LogicalChildren.Add(_flyout);
        }
        if (_flyout.IsOpen)
        {
            _flyout.IsOpen = false;
            return;
        }
        _expanded!.DataContext = vm;
        _expanded.Width = vm.IsPairList ? 520 : 360;
        _flyout.IsOpen = true;
        Dispatcher.UIThread.Post(() => _expanded.FocusItem(0, last: false), DispatcherPriority.Loaded);
    }

    // ── Keys: Enter moves on, Delete removes an empty item, ↑↓ walk the items ──

    /// <summary>The item (and which of its fields, 0 or 1) a control sits in; null outside any item.</summary>
    private (LineItemViewModel Item, int Field)? ItemOf(object? source)
    {
        if (source is not Visual v)
            return null;
        var row = v.GetSelfAndVisualAncestors().TakeWhile(a => a != this).OfType<Grid>()
            .FirstOrDefault(g => g.Classes.Contains("litem") || g.Classes.Contains("lslot"));
        if (row?.DataContext is not LineItemViewModel item)
            return null;
        var boxes = Boxes(row);
        var field = boxes.FindIndex(b => b == v || b.IsVisualAncestorOf(v));
        return (item, Math.Max(0, field));
    }

    private static System.Collections.Generic.List<TextBox> Boxes(Visual row) =>
        row.GetVisualDescendants().OfType<TextBox>().Where(b => b.IsEffectivelyVisible).ToList();

    /// <summary>The ↑/↓ belongs to the list (there is an item that way); otherwise it walks the editor's rows.</summary>
    public static bool OwnsArrows(object? source, Key key)
    {
        if ((source as Visual)?.FindAncestorOfType<LineListEditor>() is not { } list || list.Vm is not { } vm
            || list.ItemOf(source) is not var (item, _))
            return false;
        var index = item.IsAdd ? vm.Items.Count : vm.Items.IndexOf(item);
        return key == Key.Up ? index > 0 : key == Key.Down && index < vm.Items.Count;
    }

    private void List_KeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm is not { } vm || e.KeyModifiers != KeyModifiers.None || ItemOf(e.Source) is not var (item, field))
            return;
        // An item's own suggestion list (a hideTags bone list) takes its keys first: ↑↓ walk it, Enter picks from it.
        if ((e.Source as Visual)?.FindAncestorOfType<SuggestBox>() is { IsOpen: true })
            return;
        var index = item.IsAdd ? vm.Items.Count : vm.Items.IndexOf(item);
        // A slot's Replace button: Enter and Space open its menu, Delete resets the slot, ↑↓ walk the slots.
        if (item.IsModelSurface && e.Source is not TextBox)
        {
            switch (e.Key)
            {
                case Key.Delete when !e.Handled && item.IsOverridden:
                    vm.Remove(item);
                    e.Handled = true;
                    break;
                case Key.Up when !e.Handled && index > 0:
                case Key.Down when !e.Handled && index < vm.Items.Count:
                    e.Handled = true;
                    FocusItem(index + (e.Key == Key.Down ? 1 : -1), last: false);
                    break;
            }
            return;
        }
        // Esc in a slot's field puts back what it held and shows the slot again; an Esc that closed the field's list is
        // the list's (the row editor may have handled it too, putting the text back).
        if (item.IsModelSurface && item.IsChoosing && e.Key == Key.Escape && e.Source is RefField choosing
            && !ReferenceEquals(choosing.ClosingKey, e))
        {
            PropertyEditorView.DiscardPending(choosing);
            e.Handled = true;
            item.IsChoosing = false;
            if (ItemsList.ContainerFromIndex(index) is { } row)
                Dispatcher.UIThread.Post(() => FocusReplace(row), DispatcherPriority.Loaded);
            return;
        }
        switch (e.Key)
        {
            case Key.Enter when item.IsModelSurface:
                // A slot's field: the pick (or the typing) is committed and the slot shows it, its Replace button focused.
                PropertyEditorView.CommitPending(e.Source);
                e.Handled = true;
                item.IsChoosing = false;
                if (ItemsList.ContainerFromIndex(index) is { } slot)
                    Dispatcher.UIThread.Post(() => FocusReplace(slot), DispatcherPriority.Loaded);
                break;
            case Key.Enter:
                // A field the row editor didn't see (the table's popup) commits here; otherwise this is a no-op.
                PropertyEditorView.CommitPending(e.Source);
                e.Handled = true;
                if (item.IsPair && !item.IsModelSurface && field == 0)
                {
                    FocusField(item, 1);
                    break;
                }
                var stays = !item.IsAdd && vm.Items.Contains(item);
                // After an add the Add field stays, ready for the next; an emptied item is gone, so its index is the next.
                Dispatcher.UIThread.Post(() => FocusItem(item.IsAdd ? vm.Items.Count : stays ? index + 1 : index, last: false),
                    DispatcherPriority.Loaded);
                break;
            case Key.Escape when !e.Handled:
                if (PropertyEditorView.DiscardPending(e.Source))
                    e.Handled = true;
                break;
            case Key.Delete when !e.Handled && !item.IsAdd && e.Source is TextBox { Text: null or "" }:
                vm.Remove(item);
                e.Handled = true;
                Dispatcher.UIThread.Post(() => FocusItem(index, last: false), DispatcherPriority.Loaded);
                break;
            case Key.Up when !e.Handled && index > 0:
            case Key.Down when !e.Handled && index < vm.Items.Count:
                PropertyEditorView.CommitPending(e.Source);
                e.Handled = true;
                FocusItem(index + (e.Key == Key.Down ? 1 : -1), last: field > 0);
                break;
        }
    }

    /// <summary>A field the row editor didn't see (the table's popup) commits when it loses focus.</summary>
    private void List_LostFocus(object? sender, RoutedEventArgs e)
    {
        PropertyEditorView.CommitPending(e.Source);
        if (e.Source is RefField field && ItemOf(field) is ({ IsChoosing: true } item, _))
            EndChoosing(item, field);
    }

    /// <summary>Focuses item <paramref name="index"/> (the Add field past the last item), scrolling it into view.</summary>
    public void FocusItem(int index, bool last)
    {
        if (Vm is not { } vm)
            return;
        Control? row = index >= 0 && index < vm.Items.Count ? ItemsList.ContainerFromIndex(index) : AddRow;
        if (row is null)
        {
            ItemsList.ScrollIntoView(index);
            UpdateLayout();
            row = ItemsList.ContainerFromIndex(index);
        }
        if (row is null)
            return;
        row.BringIntoView();
        var boxes = Boxes(row);
        if (boxes.Count == 0)
        {
            FocusReplace(row);
            return;
        }
        var box = last ? boxes[^1] : boxes[0];
        box.Focus(NavigationMethod.Directional);
        box.SelectAll();
    }

    private void FocusField(LineItemViewModel item, int field)
    {
        if (Vm is not { } vm)
            return;
        Control? row = item.IsAdd ? AddRow : ItemsList.ContainerFromIndex(vm.Items.IndexOf(item));
        if (row is null || Boxes(row) is not { Count: > 0 } boxes)
            return;
        var box = boxes[Math.Min(field, boxes.Count - 1)];
        box.Focus(NavigationMethod.Directional);
        box.SelectAll();
    }
}
