using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;

namespace Apex.Editor.Controls;

/// <summary>
/// One line of choices that never wraps or scrolls: it draws the children that fit, left to right, keeps any child with
/// the <c>active</c> class in view whatever its place, and shows its last child (the "+N" button) only when some didn't
/// fit (or when <see cref="MoreElsewhere"/> says there are choices without a child), reporting which through
/// <see cref="Hidden"/>. Children that aren't visible are skipped.
/// </summary>
public sealed class FacetRowPanel : Panel
{
    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<FacetRowPanel, double>(nameof(Spacing), 10);

    public static readonly StyledProperty<int> MoreElsewhereProperty =
        AvaloniaProperty.Register<FacetRowPanel, int>(nameof(MoreElsewhere));

    private readonly List<Control> _hidden = new();
    private readonly HashSet<Control> _shown = new();

    static FacetRowPanel() => AffectsMeasure<FacetRowPanel>(SpacingProperty, MoreElsewhereProperty);

    public FacetRowPanel() => ClipToBounds = true;

    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    /// <summary>Choices the row has no child for at all (only "+N" offers them): with any, "+N" always shows.</summary>
    public int MoreElsewhere
    {
        get => GetValue(MoreElsewhereProperty);
        set => SetValue(MoreElsewhereProperty, value);
    }

    /// <summary>The choices left out at the last layout, in order.</summary>
    public IReadOnlyList<Control> Hidden => _hidden;

    /// <summary>Raised after a layout that left out different choices.</summary>
    public event Action<FacetRowPanel>? HiddenChanged;

    protected override Size MeasureOverride(Size availableSize)
    {
        var infinite = new Size(double.PositiveInfinity, availableSize.Height);
        var items = new List<Control>(Children.Count);
        Control? overflow = Children.Count > 0 ? Children[^1] : null;
        double height = 0;
        foreach (var child in Children)
        {
            child.Measure(infinite);
            height = Math.Max(height, child.DesiredSize.Height);
            if (child != overflow && child.IsVisible)
                items.Add(child);
        }

        var before = new List<Control>(_hidden);
        _shown.Clear();
        _hidden.Clear();
        double Width(Control c) => c.DesiredSize.Width;
        double all = 0;
        foreach (var c in items)
            all += Width(c) + (all > 0 ? Spacing : 0);
        var overflowShown = MoreElsewhere > 0 || all > availableSize.Width;
        if (!overflowShown)
        {
            foreach (var c in items)
                _shown.Add(c);
        }
        else
        {
            // Room for "+N" first, then the first choice ("All"), the active one, and the rest in order while they fit.
            var used = overflow is null ? 0 : Width(overflow) + Spacing;
            void Take(Control c)
            {
                if (_shown.Add(c))
                    used += Width(c) + Spacing;
            }
            if (items.Count > 0)
                Take(items[0]);
            foreach (var c in items)
                if (c.Classes.Contains("active"))
                    Take(c);
            foreach (var c in items)
                if (!_shown.Contains(c) && used + Width(c) <= availableSize.Width)
                    Take(c);
            foreach (var c in items)
                if (!_shown.Contains(c))
                    _hidden.Add(c);
        }
        if (!before.SequenceEqual(_hidden))
            Avalonia.Threading.Dispatcher.UIThread.Post(() => HiddenChanged?.Invoke(this));
        return new Size(Math.Min(all, availableSize.Width), height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0;
        Control? overflow = Children.Count > 0 ? Children[^1] : null;
        foreach (var child in Children)
        {
            if (child == overflow)
                continue;
            if (!child.IsVisible || !_shown.Contains(child))
            {
                child.Arrange(new Rect(finalSize.Width + 1, 0, child.DesiredSize.Width, finalSize.Height));
                continue;
            }
            child.Arrange(new Rect(x, 0, child.DesiredSize.Width, finalSize.Height));
            x += child.DesiredSize.Width + Spacing;
        }
        if (overflow is not null)
            overflow.Arrange(_hidden.Count > 0 || MoreElsewhere > 0
                ? new Rect(x, 0, overflow.DesiredSize.Width, finalSize.Height)
                : new Rect(finalSize.Width + 1, 0, overflow.DesiredSize.Width, finalSize.Height));
        return finalSize;
    }
}
