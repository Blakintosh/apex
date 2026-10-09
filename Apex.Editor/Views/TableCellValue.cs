using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Apex.Editor.Controls;
using Apex.Editor.ViewModels;

namespace Apex.Editor.Views;

/// <summary>
/// A table cell's value. At rest it is a drawing of the value that looks exactly like the property's editor at rest
/// (<see cref="CellDisplay"/>); the cell under the pointer or holding the keyboard swaps in the real editor
/// (<see cref="PropertyEditorView"/>), so a click lands on it and Tab types into it. A screen of cells is a few hundred
/// editors, and building one costs milliseconds (templates, bindings, styles), which made a table's first open take a
/// second; a drawing costs a tenth of that. Editors are borrowed from the table's pool by kind and handed back when the
/// pointer and keyboard leave, so hovering across the table rebinds a few editors instead of building one per cell.
/// The cell's change bar (left) and ⚠ (right) are this element's too: the bar drawn, the ⚠ built only for a cell that
/// has a problem, since every element a cell holds is styled when the table first shows it.
/// </summary>
public sealed class TableCellValue : Control
{
    // Cells holding an editor: an editor stays while a popup it opened (suggestions, colour picker, list) is open, and
    // a popup closing is not an event of the cell, so these look again when any popup closes.
    private static readonly List<TableCellValue> s_live = new();

    static TableCellValue()
    {
        Popup.IsOpenProperty.Changed.AddClassHandler<Popup>((_, e) =>
        {
            if (e.NewValue is false)
                foreach (var cell in s_live.ToArray())
                    cell.Recheck();
        });
    }

    /// <summary>
    /// Every cell holds its editor, as cells did before they drew their values: for the checks that compare the two
    /// looks and timings. Applies to cells as they next rebind (a table opened after setting it).
    /// </summary>
    public static bool EditorsEverywhere { get; set; }

    private PropertyEditorView? _editor;
    private CellDisplay? _display;
    private Border? _cell;
    private TableView? _table;
    private bool _recheckQueued;
    // The drawing is taking the keyboard back from an editor with nothing to focus (see Update).
    private bool _drawingKeepsKeyboard;

    // The change bar's column and the gap after it (3 + 4), as the cell's grid had them.
    private const double MarkColumn = 7;

    private Control? _child;
    private TextBlock? _warn;
    private PropertyItemViewModel? _watched;

    /// <summary>The value: the drawing, or the editor while the cell is in use.</summary>
    private Control? Child
    {
        get => _child;
        set
        {
            if (ReferenceEquals(value, _child))
                return;
            if (_child is not null)
            {
                LogicalChildren.Remove(_child);
                VisualChildren.Remove(_child);
            }
            _child = value;
            if (value is not null)
            {
                LogicalChildren.Add(value);
                VisualChildren.Add(value);
            }
            InvalidateMeasure();
        }
    }

    /// <summary>The ⚠ beside the value, with the problem as its tooltip; null while the property has none.</summary>
    public TextBlock? ProblemMark => _warn;

    /// <summary>The real editor while the cell is in use; null while it shows a drawing of its value.</summary>
    public PropertyEditorView? LiveEditor => _editor;

    /// <summary>The drawing of the value while the cell is at rest.</summary>
    public CellDisplay? Display => _editor is null ? _display : null;

    public TableCellValue()
    {
        // The change bar's colour is the theme's.
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _table = this.FindAncestorOfType<TableView>();
        // The cell's own border: its hover (the whole cell, as the row highlight shows) is what brings the editor.
        _cell = this.FindAncestorOfType<Border>();
        if (_cell is not null)
        {
            _cell.PropertyChanged += Cell_PropertyChanged;
            _cell.AddHandler(PointerPressedEvent, Cell_PointerPressed);
        }
        if (_editor is not null)
            s_live.Add(this);
        Watch(DataContext as PropertyItemViewModel);
        Update(focus: null);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_cell is not null)
        {
            _cell.PropertyChanged -= Cell_PropertyChanged;
            _cell.RemoveHandler(PointerPressedEvent, Cell_PointerPressed);
        }
        _cell = null;
        Watch(null);
        s_live.Remove(this);
        // The editor goes back to the pool once the detach is over (the tree is mid-detach now), unless the cell came
        // straight back.
        if (_editor is not null)
            Dispatcher.UIThread.Post(() =>
            {
                if (!this.IsAttachedToVisualTree())
                    Rest();
            }, DispatcherPriority.Background);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        // A cell never moves to another property holding the keyboard: the table keeps the keyboard's row and cell
        // where they are, and a row or column that goes takes the keyboard with it, which commits any typing to its own
        // property first (TableCellChecks: typing, then the rows replaced, sorted, or the column hidden).
        if (this.IsAttachedToVisualTree())
            Watch(DataContext as PropertyItemViewModel);
        Update(focus: null);
    }

    // The property's change and problem, listened to only while on screen (a property outlives a closed table's cells).
    private void Watch(PropertyItemViewModel? row)
    {
        if (ReferenceEquals(row, _watched))
            return;
        if (_watched is not null)
            _watched.PropertyChanged -= Row_PropertyChanged;
        _watched = row;
        if (row is not null)
            row.PropertyChanged += Row_PropertyChanged;
        ShowMarks();
    }

    private void Row_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PropertyItemViewModel.IsChanged) or nameof(PropertyItemViewModel.HasProblem)
            or nameof(PropertyItemViewModel.Problem))
            ShowMarks();
    }

    private void ShowMarks()
    {
        InvalidateVisual();
        var problem = _watched is { HasProblem: true } p ? p.Problem : null;
        if (problem is null && _warn is null)
            return;
        if (_warn is null)
        {
            _warn = new TextBlock { Text = "⚠", Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            _warn.Classes.Add("problem");
            _warn[!TextBlock.FontSizeProperty] = new DynamicResourceExtension("FontSizeSm");
            LogicalChildren.Add(_warn);
            VisualChildren.Add(_warn);
        }
        _warn.IsVisible = problem is not null;
        ToolTip.SetTip(_warn, problem);
        InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var warn = 0.0;
        if (_warn is { IsVisible: true })
        {
            _warn.Measure(availableSize);
            warn = _warn.DesiredSize.Width;
        }
        var height = 0.0;
        if (_child is not null)
        {
            _child.Measure(new Size(Math.Max(0, availableSize.Width - MarkColumn - warn), availableSize.Height));
            height = _child.DesiredSize.Height;
        }
        return new Size(0, Math.Max(height, _warn?.DesiredSize.Height ?? 0));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var warn = _warn is { IsVisible: true } ? _warn.DesiredSize.Width : 0;
        if (_warn is { IsVisible: true })
            _warn.Arrange(new Rect(finalSize.Width - warn, 0, warn, finalSize.Height));
        if (_child is not null)
        {
            // The value centred in the cell's height, as it was in the cell's grid.
            var h = Math.Min(_child.DesiredSize.Height, finalSize.Height);
            _child.Arrange(new Rect(MarkColumn, Math.Round((finalSize.Height - h) / 2), Math.Max(0, finalSize.Width - MarkColumn - warn), h));
        }
        return finalSize;
    }

    /// <summary>The change bar: 2×18, rounded, at the left edge of the cell's first 3 px, centred in its height.</summary>
    public override void Render(DrawingContext context)
    {
        if (_watched is not { IsChanged: true } || CellDisplay.Token<IBrush>(this, "MarkBrush") is not { } brush)
            return;
        context.DrawRectangle(brush, null, new RoundedRect(new Rect(0, Math.Round((Bounds.Height - 18) / 2), 2, 18), 1));
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsKeyboardFocusWithinProperty)
        {
            if (change.GetNewValue<bool>())
                Update(focus: null);
            else
                Recheck();
        }
    }

    private void Cell_PropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != IsPointerOverProperty)
            return;
        // Before the press: the pointer is over the cell a frame or more before it clicks.
        if (e.GetNewValue<bool>())
            Update(focus: null);
        else
            Recheck();
    }

    /// <summary>Back to the drawing once nothing holds the cell, looked at after the input that let go of it settles.</summary>
    private void Recheck()
    {
        if (_recheckQueued || _editor is null)
            return;
        _recheckQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _recheckQueued = false;
            Update(focus: null);
        }, DispatcherPriority.Background);
    }

    private bool InUse =>
        EditorsEverywhere
        || _cell is { IsPointerOver: true } && IsEffectivelyEnabled
        // The drawing holding the keyboard keeps the drawing (its editor had nothing to focus).
        || IsKeyboardFocusWithin && _display is not { IsFocused: true }
        || _editor is not null && HasOpenPopup(_editor);

    private static bool HasOpenPopup(Control editor) =>
        editor.GetLogicalDescendants().OfType<Popup>().Any(p => p.IsOpen)
        || editor.GetVisualDescendants().OfType<Popup>().Any(p => p.IsOpen);

    /// <summary>The editor when the cell is in use, the drawing when not. <paramref name="focus"/>: move the keyboard into the editor (true: its last field).</summary>
    private void Update(bool? focus)
    {
        var row = DataContext as PropertyItemViewModel;
        if (row is not null && _table is not null && (focus is not null || InUse))
        {
            if (_editor is not null && _editor.DataContext?.GetType() != row.GetType())
                Rest();
            if (_editor is null)
            {
                _editor = _table.CellEditors.Rent(row.GetType());
                s_live.Add(this);
            }
            else if (!s_live.Contains(this))
                s_live.Add(this);
            if (!ReferenceEquals(_editor.DataContext, row))
                _editor.DataContext = row;
            Child = _editor;
            if (focus is not { } last || FocusEditor(last))
                return;
            // Nothing in the editor takes the keyboard: the drawing keeps it, so Tab never drops it.
            _drawingKeepsKeyboard = true;
        }
        Rest();
        if (row is null)
        {
            Child = null;
            return;
        }
        if (_display is null || _display.Kind != row.GetType())
            _display = new CellDisplay(this, row.GetType());
        _display.Show(row);
        Child = _display;
        if (_drawingKeepsKeyboard)
        {
            _display.Focus(NavigationMethod.Tab);
            _drawingKeepsKeyboard = false;
        }
    }

    /// <summary>Hands the editor back to the table's pool.</summary>
    private void Rest()
    {
        if (_editor is null)
            return;
        var editor = _editor;
        _editor = null;
        s_live.Remove(this);
        if (ReferenceEquals(Child, editor))
            Child = null;
        (_table ?? this.FindAncestorOfType<TableView>())?.CellEditors.Return(editor);
    }

    /// <summary>Moves the keyboard into the editor's first (or last) field; false when it has none that can take it.</summary>
    private bool FocusEditor(bool last)
    {
        // Built now, so there is a field to take the keyboard before this key's handling ends.
        if (_editor is not { } editor)
            return false;
        editor.Measure(Bounds.Size);
        var fields = editor.GetVisualDescendants().OfType<InputElement>()
            .Where(e => e.Focusable && e.IsTabStop && e.IsEffectivelyVisible && e.IsEffectivelyEnabled).ToList();
        return (last ? fields.LastOrDefault() : fields.FirstOrDefault())?.Focus(NavigationMethod.Tab) == true;
    }

    /// <summary>The keyboard arrived on the drawing (Tab, Shift+Tab, an arrow): the editor takes it.</summary>
    internal void DisplayFocused(FocusChangedEventArgs e)
    {
        if (_drawingKeepsKeyboard)
            return;
        if (e.NavigationMethod == NavigationMethod.Pointer)
        {
            // A press: the press itself is passed on to the editor (see DisplayPressed), which takes the keyboard.
            Update(focus: null);
            return;
        }
        Update(focus: e.NavigationMethod == NavigationMethod.Tab && e.KeyModifiers.HasFlag(KeyModifiers.Shift));
    }

    // The drawn scene can still hold the drawing this cell just swapped out: the press then lands on the cell itself.
    private void Cell_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Visual source && (_editor?.IsVisualAncestorOf(source) == true || source == _editor))
            return;
        if (new Rect(Bounds.Size).Contains(e.GetPosition(this)))
            DisplayPressed(e);
    }

    /// <summary>
    /// A press on the drawing: the pointer came onto the cell less than a frame ago (or the table moved under a still
    /// pointer), so the editor swapped in is not on screen yet. It is laid out now and the press handed to the part of
    /// it under the pointer, so the click still takes one press.
    /// </summary>
    internal void DisplayPressed(PointerPressedEventArgs e)
    {
        if (e.Handled || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        Update(focus: null);
        if (_editor is not { } editor || TopLevel.GetTopLevel(this) is not { } top)
            return;
        // Laid out now, this cell alone: the rest of the window waits for the next frame as usual.
        if (LayoutInformation.GetPreviousMeasureConstraint(this) is { } constraint && LayoutInformation.GetPreviousArrangeBounds(this) is { } bounds)
        {
            Measure(constraint);
            Arrange(bounds);
        }
        else
            UpdateLayout();
        var target = HitByBounds(editor, e.GetPosition(editor));
        if (target is null)
            return;
        e.Handled = true;
        for (var at = (Visual?)target; at is not null && at != this; at = at.GetVisualParent())
            if (at is InputElement { Focusable: true } focusable)
            {
                focusable.Focus(NavigationMethod.Pointer, e.KeyModifiers);
                break;
            }
        var point = e.GetCurrentPoint(target);
        e.Pointer.Capture(target);
        target.RaiseEvent(new PointerPressedEventArgs(target, e.Pointer, top, e.GetPosition(top), e.Timestamp,
            point.Properties, e.KeyModifiers, e.ClickCount));
    }

    /// <summary>The innermost enabled, hit-testable element under <paramref name="p"/> by layout (the drawn scene lags a frame).</summary>
    private static InputElement? HitByBounds(Visual visual, Point p)
    {
        foreach (var child in visual.GetVisualChildren().Reverse())
        {
            if (!child.IsVisible || child is InputElement { IsHitTestVisible: false } || child.Opacity == 0)
                continue;
            if (visual.TranslatePoint(p, child) is not { } q || !new Rect(child.Bounds.Size).Contains(q))
                continue;
            if (HitByBounds(child, q) is { } inner)
                return inner;
        }
        // A panel or presenter with no background lets the pointer through, as hit testing does.
        var seeThrough = visual is Panel { Background: null } or ContentPresenter { Background: null };
        return visual is InputElement { IsEffectivelyEnabled: true } element && !seeThrough ? element : null;
    }
}

/// <summary>
/// The table's spare editors by kind (the property's view model type): a cell borrows one while it is in use. Each is
/// built once and moves from cell to cell, so hovering across the table rebinds editors instead of building them.
/// </summary>
public sealed class CellEditorPool
{
    // More than a few cells are in use at once only while every cell holds its editor (EditorsEverywhere).
    private const int MaxSparePerKind = 8;

    private readonly Dictionary<Type, Stack<PropertyEditorView>> _spare = new();

    public PropertyEditorView Rent(Type kind) =>
        _spare.TryGetValue(kind, out var stack) && stack.Count > 0 ? stack.Pop() : new PropertyEditorView();

    /// <summary>Kept bound to its last property: rebinding to another of the same kind reuses its editor, where a null would throw it away.</summary>
    public void Return(PropertyEditorView editor)
    {
        if (editor.DataContext is not { } row)
            return;
        if (!_spare.TryGetValue(row.GetType(), out var stack))
            _spare[row.GetType()] = stack = new Stack<PropertyEditorView>();
        if (stack.Count < MaxSparePerKind)
            stack.Push(editor);
    }
}
