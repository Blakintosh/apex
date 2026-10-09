using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;

namespace Apex.Editor.Controls;

/// <summary>
/// Horizontal items panel for the editor's tab strip. Lays tabs out left to right and, when they
/// don't all fit, shows a contiguous run of them that always includes the selected tab (tabs keep
/// their order; the run slides) and reports how many are hidden on each side, which the strip's
/// overflow button shows ("‹ 2 · 3 ›"); the full list, in strip order, lives behind it.
/// </summary>
public sealed class TabStripPanel : Panel
{
    public static readonly DirectProperty<TabStripPanel, int> OverflowCountProperty =
        AvaloniaProperty.RegisterDirect<TabStripPanel, int>(nameof(OverflowCount), o => o.OverflowCount);

    private int _overflowCount;
    private readonly HashSet<Control> _hidden = new();
    private int _start;
    private (int Before, int After) _sides;

    /// <summary>Index of the last tab that fits after <paramref name="start"/> (at least <paramref name="start"/>).</summary>
    private static int RunEnd(double[] widths, int start, double available)
    {
        var used = widths[start];
        var end = start;
        while (end + 1 < widths.Length && used + widths[end + 1] <= available)
            used += widths[++end];
        return end;
    }

    /// <summary>Tabs that did not fit.</summary>
    public int OverflowCount
    {
        get => _overflowCount;
        private set => SetAndRaise(OverflowCountProperty, ref _overflowCount, value);
    }

    /// <summary>Tabs hidden before the shown run (to its left) and after it (to its right).</summary>
    public (int Before, int After) HiddenSides => _sides;

    /// <summary>Raised when the hidden tabs on either side change, for hosts that can't bind to an items panel.</summary>
    public event Action<int, int>? OverflowChanged;

    protected override Size MeasureOverride(Size availableSize)
    {
        var height = 0.0;
        var widths = new double[Children.Count];
        for (var i = 0; i < Children.Count; i++)
        {
            var child = Children[i];
            child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
            widths[i] = child.DesiredSize.Width;
            height = Math.Max(height, child.DesiredSize.Height);
        }

        _hidden.Clear();
        var sides = (Before: 0, After: 0);
        var available = availableSize.Width;
        var total = 0.0;
        foreach (var w in widths)
            total += w;

        if (!double.IsInfinity(available) && total > available)
        {
            var selected = -1;
            for (var i = 0; i < Children.Count; i++)
                if (Children[i] is ListBoxItem { IsSelected: true })
                    selected = i;

            // Show one contiguous run of tabs, in strip order, like a strip scrolled sideways: keep the
            // run where it was while the selected tab is inside it, otherwise slide it just far enough
            // to reach the selected tab. Tabs never swap places, so a tab stays where it was seen.
            var start = Math.Clamp(_start, 0, Children.Count - 1);
            if (selected >= 0 && selected < start)
                start = selected;
            var end = RunEnd(widths, start, available);
            if (selected > end)
            {
                // Fill backwards from the selected tab.
                start = selected;
                var width = widths[selected];
                while (start > 0 && width + widths[start - 1] <= available)
                    width += widths[--start];
                end = RunEnd(widths, start, available);
            }
            // Room left at the end (tabs closed): pull earlier tabs back in.
            var used = 0.0;
            for (var i = start; i <= end; i++)
                used += widths[i];
            while (start > 0 && end == Children.Count - 1 && used + widths[start - 1] <= available)
                used += widths[--start];
            _start = start;
            for (var i = 0; i < Children.Count; i++)
                if (i < start || i > end)
                    _hidden.Add(Children[i]);
            sides = (start, Children.Count - 1 - end);
            total = used;
        }
        else
        {
            _start = 0;
        }

        OverflowCount = _hidden.Count;
        if (sides != _sides)
        {
            _sides = sides;
            OverflowChanged?.Invoke(sides.Before, sides.After);
        }
        return new Size(double.IsInfinity(available) ? total : Math.Min(total, available), height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var x = 0.0;
        foreach (var child in Children)
        {
            if (_hidden.Contains(child))
            {
                child.Arrange(new Rect(-10000, 0, child.DesiredSize.Width, finalSize.Height));
                continue;
            }
            child.Arrange(new Rect(x, 0, child.DesiredSize.Width, finalSize.Height));
            x += child.DesiredSize.Width;
        }
        return finalSize;
    }
}
