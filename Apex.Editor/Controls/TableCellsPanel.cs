using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Apex.Editor.ViewModels;

namespace Apex.Editor.Controls;

/// <summary>
/// One table row's cells, left to right, built only where the table is scrolled to. Every row of a table has the same
/// columns, so a recycled row takes its next asset's cells position by position as DataContext and keeps the editors it
/// built: scrolling rebinds cells instead of building them again (an ItemsControl here throws away and rebuilds every
/// cell of every row that scrolls in). A shown or hidden column is one cell per row on screen, none when it is off to
/// the side. Cells within the widest column's width of the view stay built, so Tab reaches the next column and scrolls it in.
/// </summary>
public sealed class TableCellsPanel : Panel
{
    public static readonly StyledProperty<IList<TableCellViewModel>?> CellsProperty =
        AvaloniaProperty.Register<TableCellsPanel, IList<TableCellViewModel>?>(nameof(Cells));

    public static readonly StyledProperty<IDataTemplate?> CellTemplateProperty =
        AvaloniaProperty.Register<TableCellsPanel, IDataTemplate?>(nameof(CellTemplate));

    /// <summary>Cells kept built past each side of the view: at least this, and at least the widest column.</summary>
    private const double Buffer = 240;

    /// <summary>Built cells a row keeps for editor kinds its current columns don't use (see <see cref="Reassign"/>).</summary>
    private const int MaxSpare = 16;

    // The built cell for each column, in column order; null where none was built yet. Children holds the same
    // controls in the same order, so Tab moves through them left to right, and then the spares.
    private readonly List<Control?> _slots = new();
    // Hidden cells of editor kinds the current columns had no place for, kept for the next table that has.
    private readonly List<Control> _spare = new();
    private INotifyCollectionChanged? _watched;
    private ScrollViewer? _scroller;
    // Cells in view were left blank for the next frame (see Slice).
    private bool _waiting;

    public IList<TableCellViewModel>? Cells
    {
        get => GetValue(CellsProperty);
        set => SetValue(CellsProperty, value);
    }

    public IDataTemplate? CellTemplate
    {
        get => GetValue(CellTemplateProperty);
        set => SetValue(CellTemplateProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == CellsProperty)
        {
            if (_watched is not null)
                _watched.CollectionChanged -= Cells_CollectionChanged;
            _watched = Cells as INotifyCollectionChanged;
            if (_watched is not null)
                _watched.CollectionChanged += Cells_CollectionChanged;
            Reassign();
        }
        else if (change.Property == CellTemplateProperty)
        {
            Children.Clear();
            _slots.Clear();
            _spare.Clear();
            Resize();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // The scroller that moves the table sideways (the rows' own scroller only scrolls down).
        foreach (var ancestor in this.GetVisualAncestors())
            if (ancestor is ScrollViewer { HorizontalScrollBarVisibility: not ScrollBarVisibility.Disabled } sv)
            {
                _scroller = sv;
                sv.ScrollChanged += Scroller_ScrollChanged;
                break;
            }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_scroller is not null)
            _scroller.ScrollChanged -= Scroller_ScrollChanged;
        _scroller = null;
    }

    private void Scroller_ScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (e.OffsetDelta.X != 0 || e.ViewportDelta.X != 0)
            InvalidateMeasure();
    }

    private void Cells_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add when e.NewItems is { } added && e.NewStartingIndex >= 0 && e.NewStartingIndex <= _slots.Count:
                for (var i = 0; i < added.Count; i++)
                    _slots.Insert(e.NewStartingIndex + i, null);
                break;
            case NotifyCollectionChangedAction.Remove when e.OldItems is { } removed && e.OldStartingIndex >= 0
                                                          && e.OldStartingIndex + removed.Count <= _slots.Count:
                for (var i = 0; i < removed.Count; i++)
                {
                    if (_slots[e.OldStartingIndex] is { } cell)
                        Children.Remove(cell);
                    _slots.RemoveAt(e.OldStartingIndex);
                }
                break;
            default:
                Reassign();
                break;
        }
        InvalidateMeasure();
    }

    /// <summary>One slot per cell. Cells already built keep their place and are rebound as they come into view.</summary>
    private void Resize()
    {
        var count = Cells?.Count ?? 0;
        while (_slots.Count > count)
        {
            if (_slots[^1] is { } cell)
                Children.Remove(cell);
            _slots.RemoveAt(_slots.Count - 1);
        }
        while (_slots.Count < count)
            _slots.Add(null);
        InvalidateMeasure();
    }

    /// <summary>The editor kind a built cell shows: its editor's type, which picks the editor's template.</summary>
    private static Type? KindOf(Control? cell) => (cell?.DataContext as TableCellViewModel)?.Editor.GetType();

    /// <summary>
    /// A new set of cells (another row, or another table): each column takes a built cell of its editor's kind. A cell
    /// rebound to an editor of another kind throws its editor away and builds the new one from its template, which is
    /// most of what a cell costs; tables of different types put different kinds in each position, so rows kept
    /// positions-only rebuilt nearly every editor on each open. Cells first stay where they are, then move to a
    /// column of their kind; the rest wait as hidden spares (a few per row), and only a column with no cell of its kind
    /// left takes one of another kind.
    /// </summary>
    private void Reassign()
    {
        var cells = Cells;
        var count = cells?.Count ?? 0;
        var free = new List<Control>(_slots.Count + _spare.Count);
        foreach (var slot in _slots)
            if (slot is not null)
                free.Add(slot);
        free.AddRange(_spare);
        _spare.Clear();
        var slots = new Control?[count];
        // Where it is, when its kind still fits (a sort, or the same table again: everything stays).
        for (var i = 0; i < count && i < _slots.Count; i++)
            if (_slots[i] is { } at && (at.IsKeyboardFocusWithin || KindOf(at) == cells![i].Editor.GetType()))
            {
                slots[i] = at;
                free.Remove(at);
            }
        // Then any cell of its kind.
        for (var i = 0; i < count; i++)
            if (slots[i] is null && free.Find(c => KindOf(c) == cells![i].Editor.GetType()) is { } match)
            {
                slots[i] = match;
                free.Remove(match);
            }
        // The rest stay as spares, up to the cap; past it, a cell of another kind beats building one from nothing.
        for (var i = 0; i < count && free.Count > MaxSpare; i++)
            if (slots[i] is null)
            {
                slots[i] = free[0];
                free.RemoveAt(0);
            }
        // Past the cap the newest spares go. A row between items (no cells for now) keeps them all for the next table,
        // so only there can a row hold more than the cap.
        while (count > 0 && free.Count > MaxSpare)
        {
            Children.Remove(free[^1]);
            free.RemoveAt(free.Count - 1);
        }
        _slots.Clear();
        _slots.AddRange(slots);
        _spare.AddRange(free);
        // Children in column order, then the spares. A move keeps a cell attached, so it costs no restyle.
        var index = 0;
        foreach (var cell in _slots)
            if (cell is not null)
                Place(cell, index++);
        foreach (var cell in _spare)
        {
            cell.IsVisible = false;
            Place(cell, index++);
        }
        InvalidateMeasure();

        void Place(Control cell, int to)
        {
            var from = Children.IndexOf(cell);
            if (from != to)
                Children.Move(from, to);
        }
    }

    /// <summary>The part of this row the view shows, in the row's own coordinates, widened by the buffer.</summary>
    private (double From, double To) InView(IList<TableCellViewModel>? cells)
    {
        if (_scroller is not { Viewport.Width: > 0 } sv || this.TranslatePoint(default, sv) is not { } left)
            return (double.NegativeInfinity, double.PositiveInfinity);
        // A column wider than the buffer would leave its neighbour unbuilt, and Tab would have nowhere to go.
        var buffer = Buffer;
        if (cells is not null)
            foreach (var cell in cells)
                buffer = Math.Max(buffer, cell.Width);
        return (-left.X - buffer, -left.X + sv.Viewport.Width + buffer);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var cells = Cells;
        var (from, to) = InView(cells);
        double x = 0, height = 0;
        var built = 0;
        _waiting = false;
        for (var i = 0; i < _slots.Count; i++)
        {
            var item = cells![i];
            var width = item.Width;
            var slot = _slots[i];
            var focused = slot is { IsKeyboardFocusWithin: true };
            if (x + width >= from && x <= to || focused)
            {
                if (slot is not null && ReferenceEquals(slot.DataContext, item) || focused || !Slice.Spent())
                {
                    Slice.Start();
                    if (slot is null && CellTemplate?.Build(item) is { } made)
                    {
                        slot = _slots[i] = made;
                        made.DataContext = item;
                        Children.Insert(built, made);
                    }
                    if (slot is not null)
                    {
                        if (!ReferenceEquals(slot.DataContext, item))
                            slot.DataContext = item;
                        slot.IsVisible = true;
                        Blank(slot, false);
                        slot.Measure(new Size(width, availableSize.Height));
                        height = Math.Max(height, slot.DesiredSize.Height);
                    }
                }
                else
                {
                    // Past this frame's share: the cell is blank until the next frame (never another row's value, and
                    // out of the pointer's reach). Blanked, not hidden: hiding walks the cell's whole tree.
                    if (slot is not null)
                        Blank(slot, true);
                    _waiting = true;
                    Slice.Wait(this);
                }
            }
            else if (slot is not null)
                slot.IsVisible = false;
            if (slot is not null)
                built++;
            x += width;
        }
        return new Size(x, height);
    }

    private static void Blank(Control cell, bool blank)
    {
        cell.Opacity = blank ? 0 : 1;
        cell.IsHitTestVisible = !blank;
        // Still bound to the row it last showed: Tab must not land in it and type into that row.
        cell.IsEnabled = !blank;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var cells = Cells;
        double x = 0;
        for (var i = 0; i < _slots.Count; i++)
        {
            var width = cells![i].Width;
            if (_slots[i] is { IsVisible: true } slot)
                slot.Arrange(new Rect(x, 0, width, finalSize.Height));
            x += width;
        }
        // A sideways scroll measured before this row knew where it sits: look again now that it does.
        var (from, to) = InView(Cells);
        if (!_waiting && !double.IsInfinity(from) && NeedsCells(from, to))
            InvalidateMeasure();
        return finalSize;
    }

    /// <summary>Whether a cell in view has none built or shows another row's cell.</summary>
    private bool NeedsCells(double from, double to)
    {
        var cells = Cells;
        double x = 0;
        for (var i = 0; i < _slots.Count; i++)
        {
            var width = cells![i].Width;
            if (x + width >= from && x <= to && (_slots[i] is not { IsVisible: true } slot || !ReferenceEquals(slot.DataContext, cells[i])))
                return true;
            x += width;
        }
        return false;
    }

    /// <summary>
    /// The UI time cells may take per frame, shared by every row. Building a cell's editor costs about a millisecond and
    /// a half and rebinding one a tenth of that, so a column shown on 30 rows, or a jump to the bottom, would hold the
    /// frame for 50 ms; past this share the rest wait for the next frame, which draws first and takes input between.
    /// </summary>
    private static class Slice
    {
        private const double ShareMs = 4;

        private static long s_start;
        private static readonly List<TableCellsPanel> s_waiting = new();

        public static void Start()
        {
            if (s_start != 0)
                return;
            s_start = Stopwatch.GetTimestamp();
            // Ends once this frame's layout and render are done, at background priority, so input goes first.
            Dispatcher.UIThread.Post(End, DispatcherPriority.Background);
        }

        public static bool Spent() => s_start != 0 && Stopwatch.GetElapsedTime(s_start).TotalMilliseconds > ShareMs;

        public static void Wait(TableCellsPanel panel)
        {
            if (!s_waiting.Contains(panel))
                s_waiting.Add(panel);
        }

        private static void End()
        {
            s_start = 0;
            foreach (var panel in s_waiting)
                panel.InvalidateMeasure();
            s_waiting.Clear();
        }
    }
}
