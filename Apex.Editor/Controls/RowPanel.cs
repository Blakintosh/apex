using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Apex.Editor.Controls;

/// <summary>
/// A vertical virtualizing panel for long lists of rows (the editor's property list and rail, the Explorer, the
/// Inspector's lists). It realizes only the rows in view, like VirtualizingStackPanel, but a row that leaves the view
/// stays in the tree, hidden, in a pool for its kind. VirtualizingStackPanel takes pooled rows out of the tree and puts
/// them back, and every re-attach re-applies every style to every control in the row: opening an asset or switching tabs
/// spent half its time there. Here the next item of the same kind shows the hidden row and rebinds it.
///
/// Heights come from the rows themselves: a realized row's measured height, otherwise the last measured height of its
/// kind (the editor's rows and section headers are fixed-height, so the estimate is exact but for the first header).
/// Use under a ScrollViewer, with an items control whose rows keep what they built (<see cref="RowItemsControl"/>,
/// <see cref="RowListBox"/>).
/// </summary>
public class RowPanel : VirtualizingPanel
{
    /// <summary>Rows realized above and below the viewport, so small scrolls don't re-realize.</summary>
    private const double Buffer = 200;

    private const double FallbackHeight = 30;

    private readonly Dictionary<object, Stack<Control>> _pool = new();
    private readonly Dictionary<object, double> _kindHeight = new();
    private readonly Dictionary<Control, object> _keyOf = new();
    // index → container for the rows in view, in index order.
    private readonly SortedDictionary<int, Control> _realized = new();
    private readonly Dictionary<Control, int> _indexOf = new();
    // The item each realized row shows (a ListBoxItem's DataContext isn't it, so the panel keeps its own record).
    private readonly Dictionary<Control, object> _itemOf = new();
    // Every row measured since the list was last replaced, so scrolled-away rows keep their true height (the form's
    // first section header is shorter than the rest).
    private readonly Dictionary<object, double> _heightOf = new(ReferenceEqualityComparer.Instance);
    private Rect _viewport;
    // The viewport the realized rows were chosen for. Scrolls are judged against it, not the previous scroll event:
    // wheel ticks are each smaller than the buffer, so comparing tick to tick never re-realized and a few ticks
    // scrolled past the last realized row into blank space.
    private Rect _realizedFor;
    private bool _reset;
    private bool _sweep;
    private double[] _offsets = Array.Empty<double>();

    /// <summary>
    /// A replaced list (a Reset: another asset's rows, a rebuilt tree) shows from its top. Ranged adds and removes
    /// (expanding a group, narrowing a filter) keep the scroll position. Leave off for a list nested in a larger
    /// scrolling pane, which the reset would scroll.
    /// </summary>
    public bool StartAtTopOnReset { get; set; }

    /// <summary>
    /// Whether pooled rows let go of items the list no longer has (see <see cref="ReleaseStaleRows"/>). The table turns
    /// it off: rebinding a table row builds its cells and dropping one tears a whole row of editors down, which cost
    /// half a second on every open and close, while what a stale table row holds is one closed table's rows (no tab).
    /// </summary>
    public bool ReleasesStaleRows { get; set; } = true;

    public RowPanel()
    {
        EffectiveViewportChanged += (_, e) =>
        {
            _viewport = e.EffectiveViewport;
            if (!CoversViewport())
                InvalidateMeasure();
        };
    }

    // ── A pane that hides the list (the Preview layout hides the Explorer) ──
    // Rows can go stale while hidden (see RelayoutStale), and the layout pass that shows them again skips a panel whose
    // own measure is still valid. So the panel watches its ancestors and checks every row in view when one reappears.

    private readonly List<Visual> _watched = new();

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        foreach (var ancestor in this.GetVisualAncestors())
        {
            ancestor.PropertyChanged += Ancestor_PropertyChanged;
            _watched.Add(ancestor);
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        foreach (var ancestor in _watched)
            ancestor.PropertyChanged -= Ancestor_PropertyChanged;
        _watched.Clear();
    }

    private void Ancestor_PropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == IsVisibleProperty && e.NewValue is true)
        {
            _sweep = true;
            InvalidateMeasure();
        }
    }

    /// <summary>True while the rows realized at the last measure still cover the current viewport.</summary>
    private bool CoversViewport()
    {
        var old = _realizedFor;
        if (_realized.Count == 0 || old.Height <= 0)
            return false;
        var top = Math.Max(0, old.Top - Buffer);
        var bottom = old.Bottom + Buffer;
        return _viewport.Top >= top && _viewport.Bottom <= bottom && Math.Abs(_viewport.Width - old.Width) < 0.5;
    }

    protected override void OnItemsChanged(IReadOnlyList<object?> items, NotifyCollectionChangedEventArgs e)
    {
        base.OnItemsChanged(items, e);
        // A removed item's measured height goes with it: the table would otherwise keep every item the list ever showed.
        if (e.OldItems is { } old && e.Action is NotifyCollectionChangedAction.Remove or NotifyCollectionChangedAction.Replace)
            foreach (var item in old)
                if (item is not null)
                    _heightOf.Remove(item);
        if (ReleasesStaleRows && e.Action is not (NotifyCollectionChangedAction.Add or NotifyCollectionChangedAction.Move) && !_releaseQueued)
        {
            _releaseQueued = true;
            Dispatcher.UIThread.Post(ReleaseStaleRows, DispatcherPriority.Background);
        }
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            _heightOf.Clear();
            _reset = true;
            if (StartAtTopOnReset && this.FindAncestorOfType<ScrollViewer>() is { } scroll)
                scroll.Offset = scroll.Offset.WithY(0);
        }
        InvalidateMeasure();
    }

    private double KindHeight(object? key) => key is not null && _kindHeight.TryGetValue(key, out var h) ? h : FallbackHeight;

    private object? KeyOf(object? item, int index)
    {
        var generator = ItemContainerGenerator!;
        return generator.NeedsContainer(item, index, out var key) ? key : null;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var items = Items;
        var generator = ItemContainerGenerator;
        if (generator is null)
            return default;
        var width = double.IsInfinity(availableSize.Width) ? _viewport.Width : availableSize.Width;
        var rowSize = new Size(width, double.PositiveInfinity);

        // Offsets by the heights we know: measured rows where realized, their kind's height elsewhere.
        if (_offsets.Length != items.Count + 1)
            _offsets = new double[items.Count + 1];
        var keys = new object?[items.Count];
        var y = 0.0;
        for (var i = 0; i < items.Count; i++)
        {
            _offsets[i] = y;
            keys[i] = KeyOf(items[i], i);
            y += items[i] is { } item && _heightOf.TryGetValue(item, out var h) ? h : KindHeight(keys[i]);
        }
        _offsets[items.Count] = y;

        // A collapsed pane (or no viewport yet) has no width to lay rows out in. Measuring them at zero anyway would
        // trim every name to nothing, and a text block keeps its shaped text across a later, wider measure: the rows
        // would come back blank. Leave them as they are until there is room.
        if (!(width > 0))
        {
            _sweep = true;
            return new Size(0, _offsets[items.Count]);
        }

        // The range in view (plus a buffer). Before the first viewport arrives, a screenful from the top.
        _realizedFor = _viewport;
        var viewTop = Math.Max(0, (_viewport.Height > 0 ? _viewport.Top : 0) - Buffer);
        var viewBottom = (_viewport.Height > 0 ? _viewport.Bottom : 1000) + Buffer;
        var first = Math.Max(0, UpperBound(viewTop) - 1);

        // Keep containers whose item is still at hand; everything else goes back to its pool (hidden, still attached).
        // After a Reset every row is prepared again, as VirtualizingStackPanel does, even for an item still in the list.
        var previous = new Dictionary<object, Control>(ReferenceEqualityComparer.Instance);
        if (!_reset)
            foreach (var c in _realized.Values)
                if (_itemOf.GetValueOrDefault(c) is { } it)
                    previous.TryAdd(it, c);
        var keep = new HashSet<Control>();
        var realized = new SortedDictionary<int, Control>();
        var focused = _realized.Values.FirstOrDefault(c => c.IsKeyboardFocusWithin);
        // Rows whose item won't be in view go back to their pools before the rows coming into view are realized, so those
        // rent them. Returned after, the new rows found the pools empty and built a screenful more: another list (a
        // table's next open) built every row, and every cell, again. Bottom first, so the top row takes the top row's
        // container back.
        var staying = new HashSet<object>(ReferenceEqualityComparer.Instance);
        if (!_reset)
            for (int i = first, end = items.Count; i < end && _offsets[i] < viewBottom; i++)
                if (items[i] is { } inView && previous.ContainsKey(inView))
                    staying.Add(inView);
        foreach (var (i, c) in _realized.Reverse().ToList())
        {
            if (c == focused || _itemOf.GetValueOrDefault(c) is { } kept && staying.Contains(kept))
                continue;
            if (_itemOf.GetValueOrDefault(c) is { } gone)
                previous.Remove(gone);
            _realized.Remove(i);
            _indexOf.Remove(c);
            Return(c);
        }
        _reset = false;

        y = _offsets[first];
        for (var i = first; i < items.Count && y < viewBottom; i++)
        {
            var item = items[i];
            Control container;
            if (item is not null && previous.Remove(item, out var existing) && Equals(_keyOf.GetValueOrDefault(existing), keys[i]))
            {
                container = existing;
                if (_indexOf.GetValueOrDefault(existing, -1) != i)
                    generator.ItemContainerIndexChanged(container, _indexOf.GetValueOrDefault(existing, -1), i);
                // After the list was hidden, a row kept in view may hold stale layout.
                if (_sweep)
                    RelayoutStale(container);
            }
            else
            {
                container = Rent(item, i, keys[i], out var pooled);
                // A row back from the pool may have gone stale while it waited: settle that before it takes the item,
                // so the rebind's own changes invalidate it the ordinary way.
                if (pooled)
                    RelayoutStale(container);
                // Visible before it takes the item, so what the rebind invalidates gets laid out.
                container.IsVisible = true;
                generator.PrepareItemContainer(container, item, i);
                if (item is not null)
                    _itemOf[container] = item;
                generator.ItemContainerPrepared(container, item, i);
            }
            keep.Add(container);
            realized[i] = container;
            container.Measure(rowSize);
            var h = container.DesiredSize.Height;
            if (keys[i] is { } key)
                _kindHeight[key] = h;
            if (item is not null)
                _heightOf[item] = h;
            // Correct the offsets below this row by what measuring it taught us.
            var delta = h - (_offsets[i + 1] - _offsets[i]);
            if (Math.Abs(delta) > 0.01)
                for (var j = i + 1; j <= items.Count; j++)
                    _offsets[j] += delta;
            y = _offsets[i + 1];
        }
        _sweep = false;

        // The focused row stays realized wherever it is, so the keyboard isn't dropped by a scroll.
        if (focused is not null && !keep.Contains(focused) && _itemOf.GetValueOrDefault(focused) is { } focusedItem)
        {
            var fi = _indexOf.GetValueOrDefault(focused, -1);
            if (fi < 0 || fi >= items.Count || !ReferenceEquals(items[fi], focusedItem))
                fi = IndexOf(items, focusedItem);
            if (fi >= 0 && !realized.ContainsKey(fi))
            {
                if (_indexOf.GetValueOrDefault(focused, -1) != fi)
                    generator.ItemContainerIndexChanged(focused, _indexOf.GetValueOrDefault(focused, -1), fi);
                keep.Add(focused);
                realized[fi] = focused;
                focused.Measure(rowSize);
            }
        }

        // Rows leaving the view are unindexed before they are cleared, so a list that hears the container change
        // (a ListBox clearing IsSelected) can't map it back to an index and deselect whatever item sits there now.
        var leaving = _realized.Values.Where(c => !realized.ContainsValue(c)).ToList();
        _realized.Clear();
        _indexOf.Clear();
        foreach (var (i, c) in realized)
        {
            _realized[i] = c;
            _indexOf[c] = i;
        }
        foreach (var c in leaving)
            Return(c);

        // The rows in view lead the children, top to bottom. Pooled rows are reused in whatever order they come back, and
        // Tab, and anything else that walks the tree for "the first field", meets rows in child order. A move keeps a
        // row attached, so it costs no restyle.
        var at = 0;
        foreach (var c in _realized.Values)
        {
            var current = Children.IndexOf(c);
            if (current != at)
                Children.Move(current, at);
            at++;
        }

        var desiredWidth = _realized.Values.Select(c => c.DesiredSize.Width).DefaultIfEmpty(0).Max();
        return new Size(double.IsInfinity(availableSize.Width) ? desiredWidth : availableSize.Width, _offsets[items.Count]);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (var (i, c) in _realized)
            if (i + 1 < _offsets.Length)
                c.Arrange(new Rect(0, _offsets[i], finalSize.Width, c.DesiredSize.Height));
        return finalSize;
    }

    /// <summary>First index whose top is below <paramref name="y"/>.</summary>
    private int UpperBound(double y)
    {
        int lo = 0, hi = _offsets.Length - 1;
        while (lo < hi)
        {
            var mid = (lo + hi) >> 1;
            if (_offsets[mid] <= y)
                lo = mid + 1;
            else
                hi = mid;
        }
        return Math.Min(lo, Math.Max(0, _offsets.Length - 2));
    }

    /// <summary>
    /// Avalonia 12.1's TextBlock caches shaped text (TextRunCache, a preview API) and a block whose text changed while it
    /// was hidden can measure from the stale cache when it shows again: a recycled Explorer row came back with its name
    /// laid out as nothing. Dropping the cache of any text block about to be re-measured avoids it (a re-measure reshapes
    /// anyway). Remove when the TextBlock invalidates its cache itself; if the field is gone this does nothing.
    /// </summary>
    private static readonly System.Reflection.FieldInfo? TextRunCacheField =
        typeof(TextBlock).GetField("_textRunCache", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

    /// <summary>
    /// A row's items can change while the row is hidden (a pooled row stays bound; a whole pane can be hidden by the
    /// layout): a name re-highlighted by a filter, a value undone. The layout pass skips hidden controls, so those left
    /// invalid have nothing queued to measure them, and the row, still valid itself, would keep the old layout (a name
    /// laid out empty). Invalidating their ancestors up to the row puts them back in this pass.
    /// </summary>
    private static void RelayoutStale(Control row)
    {
        foreach (var visual in row.GetVisualDescendants())
        {
            if (visual is not Layoutable { IsMeasureValid: false } and not Layoutable { IsArrangeValid: false })
                continue;
            var measure = !((Layoutable)visual).IsMeasureValid;
            if (measure && visual is TextBlock text)
            {
                (TextRunCacheField?.GetValue(text) as Avalonia.Media.TextFormatting.TextRunCache)?.Invalidate();
                // A text block also keeps the layout it last built until its measure goes from valid to invalid; one
                // that was already invalid when its text changed would measure the old text at the old size. A pass
                // at another size (unbounded, so nothing is trimmed) makes it build anew, and invalidating it after
                // drops that layout before the real measure.
                text.Measure(Size.Infinity);
                text.InvalidateMeasure();
            }
            for (var up = visual.GetVisualParent(); up is Layoutable layoutable; up = up.GetVisualParent())
            {
                if (measure)
                    layoutable.InvalidateMeasure();
                else
                    layoutable.InvalidateArrange();
                if (up == row)
                    break;
            }
        }
    }

    // The item each pooled row is still bound to.
    private readonly Dictionary<Control, object> _pooledItem = new();
    // Per kind, the item-less stand-in pooled rows park on (see IRowStandIn).
    private readonly Dictionary<object, object> _parking = new();
    private bool _releaseQueued;

    /// <summary>
    /// Pooled rows stay bound to the item they last showed, which is what makes reusing them cheap. Once the list is
    /// replaced (or the item removed), though, that item can belong to the old list (a closed tab's property, a link whose click goes to its
    /// asset) and the hidden row would keep it, and everything it reaches, alive for as long as the list lives. So when
    /// the new list has settled, each such row parks on its kind's stand-in (<see cref="IRowStandIn"/>), which holds
    /// nothing and keeps the row built for the next list that has its kind: without it, opening an xanim after a rumble
    /// built a dozen rows from their templates again, most of the open. A kind without a stand-in takes an item of its
    /// kind from the current list, as it would on its next use, and leaves the pool if the current list has none.
    /// </summary>
    private void ReleaseStaleRows()
    {
        _releaseQueued = false;
        var generator = ItemContainerGenerator;
        if (generator is null || _pooledItem.Count == 0)
            return;
        var items = Items;
        var present = new HashSet<object>(items.Count, ReferenceEqualityComparer.Instance);
        var standIn = new Dictionary<object, (object Item, int Index)>();
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i] is not { } item)
                continue;
            present.Add(item);
            if (KeyOf(item, i) is { } key)
                standIn.TryAdd(key, (item, i));
        }
        foreach (var (key, stack) in _pool)
        {
            var rows = stack.ToArray();
            stack.Clear();
            // ToArray is top first; push back bottom first so the order holds.
            for (var r = rows.Length - 1; r >= 0; r--)
            {
                var row = rows[r];
                if (_pooledItem.TryGetValue(row, out var last) && !present.Contains(last)
                    && !(_parking.TryGetValue(key, out var parked) && ReferenceEquals(parked, last)))
                {
                    object item;
                    int index;
                    if (ParkingFor(key, last) is { } park)
                        (item, index) = (park, 0);
                    else if (standIn.TryGetValue(key, out var s))
                        (item, index) = s;
                    else
                    {
                        _pooledItem.Remove(row);
                        _keyOf.Remove(row);
                        RemoveInternalChild(row);
                        continue;
                    }
                    generator.PrepareItemContainer(row, item, index);
                    generator.ClearItemContainer(row);
                    _pooledItem[row] = item;
                }
                stack.Push(row);
            }
        }
    }

    /// <summary>The stand-in rows of <paramref name="key"/>'s kind park on, made from the first item that offers one.</summary>
    private object? ParkingFor(object key, object item)
    {
        if (_parking.TryGetValue(key, out var parked))
            return parked;
        if (item is not IRowStandIn offer || offer.CreateStandIn() is not { } made || made.GetType() != item.GetType())
            return null;
        _parking[key] = made;
        return made;
    }

    private Control Rent(object? item, int index, object? key, out bool pooled)
    {
        pooled = key is not null && _pool.TryGetValue(key, out var stack) && stack.Count > 0;
        if (pooled)
        {
            var row = _pool[key!].Pop();
            _pooledItem.Remove(row);
            return row;
        }
        var container = ItemContainerGenerator!.CreateContainer(item, index, key);
        if (key is not null)
            _keyOf[container] = key;
        AddInternalChild(container);
        return container;
    }

    private static int IndexOf(IReadOnlyList<object?> items, object item)
    {
        for (var i = 0; i < items.Count; i++)
            if (ReferenceEquals(items[i], item))
                return i;
        return -1;
    }

    private void Return(Control container)
    {
        container.IsVisible = false;
        _itemOf.Remove(container, out var item);
        if (_keyOf.TryGetValue(container, out var key))
        {
            ItemContainerGenerator!.ClearItemContainer(container);
            if (item is not null)
                _pooledItem[container] = item;
            if (!_pool.TryGetValue(key, out var stack))
                _pool[key] = stack = new Stack<Control>();
            stack.Push(container);
        }
        else
        {
            ItemContainerGenerator!.ClearItemContainer(container);
            RemoveInternalChild(container);
        }
    }

    protected override Control? ScrollIntoView(int index)
    {
        if (index < 0 || index >= Items.Count)
            return null;
        if (_offsets.Length != Items.Count + 1)
            UpdateLayout();
        var top = _offsets[Math.Min(index, _offsets.Length - 1)];
        var height = index + 1 < _offsets.Length ? _offsets[index + 1] - top : FallbackHeight;
        this.BringIntoView(new Rect(0, top, Bounds.Width, height));
        UpdateLayout();
        return ContainerFromIndex(index);
    }

    protected override Control? ContainerFromIndex(int index) => _realized.GetValueOrDefault(index);

    protected override int IndexFromContainer(Control container) => _indexOf.GetValueOrDefault(container, -1);

    protected override IEnumerable<Control>? GetRealizedContainers() => _realized.Values;

    /// <summary>Arrow, Page and Home/End navigation for lists (a ListBox asks its panel for the next row).</summary>
    protected override IInputElement? GetControl(NavigationDirection direction, IInputElement? from, bool wrap)
    {
        var count = Items.Count;
        if (count == 0)
            return null;
        var fromIndex = -1;
        for (var v = from as Visual; v is not null && v != this; v = v.GetVisualParent())
            if (v is Control c && _indexOf.TryGetValue(c, out var i))
            {
                fromIndex = i;
                break;
            }
        var average = _offsets.Length == count + 1 && count > 0 ? _offsets[count] / count : FallbackHeight;
        var page = Math.Max(1, (int)(_viewport.Height / Math.Max(1, average)) - 1);
        var to = direction switch
        {
            NavigationDirection.First => 0,
            NavigationDirection.Last => count - 1,
            NavigationDirection.Down or NavigationDirection.Next => fromIndex + 1,
            NavigationDirection.Up or NavigationDirection.Previous => fromIndex < 0 ? count - 1 : fromIndex - 1,
            NavigationDirection.PageDown => Math.Min(count - 1, fromIndex + page),
            NavigationDirection.PageUp => Math.Max(0, fromIndex - page),
            _ => -1,
        };
        if (to >= count)
            to = wrap ? 0 : -1;
        else if (to < 0 && direction is NavigationDirection.Up or NavigationDirection.Previous && wrap)
            to = count - 1;
        return to < 0 ? null : ScrollIntoView(to);
    }
}
