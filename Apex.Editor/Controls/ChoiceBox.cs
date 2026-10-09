using System;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Apex.Editor.ViewModels;

namespace Apex.Editor.Controls;

/// <summary>
/// The dropdown for a choice property. It writes to the property only when the user picks a value (a click in the list,
/// a key, the wheel over the open list); everything else (another row taking it over, an undo, new options from the deffile) flows the
/// other way, options first and then the value. A two-way bound ComboBox can't promise that: when a recycled row moves
/// to the next property, its options and selection rebind one at a time and the list coerces the selection in between,
/// so a dropdown could write the last property's value into the next one. Rows therefore couldn't recycle dropdowns,
/// and every asset opened built its dropdowns from scratch.
/// </summary>
public sealed class ChoiceBox : ComboBox
{
    private ChoicePropertyViewModel? _row;
    private bool _attached;
    private bool _syncing;
    private int _userInput;

    protected override Type StyleKeyOverride => typeof(ComboBox);

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        Follow(_attached ? DataContext as ChoicePropertyViewModel : null);
    }

    protected override void OnDataContextEndUpdate()
    {
        // The list defers selection changes made while its DataContext changes and applies them here; whatever it
        // settles on, the row's value is what shows.
        base.OnDataContextEndUpdate();
        if (_row is not null)
            Sync();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        Follow(DataContext as ChoicePropertyViewModel);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _attached = false;
        // Unhooked while out of the tree, so a property keeps no dropdown alive that it once had.
        Follow(null);
    }

    private void Follow(ChoicePropertyViewModel? row)
    {
        if (row == _row)
            return;
        if (_row is not null)
            _row.PropertyChanged -= Row_PropertyChanged;
        _row = row;
        if (_row is not null)
        {
            _row.PropertyChanged += Row_PropertyChanged;
            Sync();
        }
    }

    private void Row_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ChoicePropertyViewModel.Value) or nameof(ChoicePropertyViewModel.Choices))
            Sync();
    }

    private void Sync()
    {
        _syncing = true;
        try
        {
            if (_row is { HasLabels: true } labelled)
            {
                // Labelled options are items of their own, so a dropdown moved to another row with the same values but
                // other labels shows the new labels.
                var items = labelled.Items;
                ItemsSource = items;
                SelectedItem = items.FirstOrDefault(i => i.Value.Equals(labelled.Value, StringComparison.Ordinal));
                return;
            }
            ItemsSource = _row?.Choices;
            SelectedItem = _row?.Value;
        }
        finally
        {
            _syncing = false;
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SelectedItemProperty && _userInput > 0 && !_syncing && _row is not null
            && (SelectedItem as string ?? (SelectedItem as ChoiceItem)?.Value) is { } pick && pick != _row.Value)
            _row.Value = pick;
    }

    // ── The user's hand on it: the only times a selection change is a pick ──

    private void AsUser(Action action)
    {
        _userInput++;
        try
        {
            action();
        }
        finally
        {
            _userInput--;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e) => AsUser(() => base.OnKeyDown(e));

    protected override void OnTextInput(TextInputEventArgs e) => AsUser(() => base.OnTextInput(e));

    // The wheel over a closed dropdown scrolls the form, never the value: a ComboBox steps its selection on the wheel
    // once it has focus, so scrolling the form past a dropdown you had just set rewrote it. Left unhandled, the event
    // bubbles on to the form's scroll viewer. Over the open list the wheel scrolls the list, as usual.
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        if (IsDropDownOpen)
            AsUser(() => base.OnPointerWheelChanged(e));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e) => AsUser(() => base.OnPointerReleased(e));

    public override bool UpdateSelectionFromEvent(Control container, RoutedEventArgs eventArgs)
    {
        var handled = false;
        AsUser(() => handled = base.UpdateSelectionFromEvent(container, eventArgs));
        return handled;
    }
}
