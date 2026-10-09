using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;

namespace Apex.Editor.Controls;

/// <summary>
/// Lays its children out left to right in order of importance: the first (a row's name) measures first and takes all
/// the width it needs, each one after it gets what is left, whole if it fits and trimmed if not, and one that would be
/// trimmed to less than <see cref="MinShown"/> drops out rather than showing as a lone ellipsis. A DockPanel or Grid
/// gives the trailing text its width first, which cut the name a modder came for to make room for its GDT.
/// </summary>
public sealed class NameFirstPanel : Panel
{
    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<NameFirstPanel, double>(nameof(Spacing), 8);

    /// <summary>A trailing text trimmed to less than this isn't worth showing.</summary>
    public const double MinShown = 40;

    private readonly HashSet<Control> _dropped = new();

    static NameFirstPanel() => AffectsMeasure<NameFirstPanel>(SpacingProperty);

    public NameFirstPanel() => ClipToBounds = true;

    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _dropped.Clear();
        double used = 0, height = 0;
        var first = true;
        foreach (var child in Children)
        {
            if (!child.IsVisible)
                continue;
            var gap = first ? 0 : Spacing;
            var room = Math.Max(0, availableSize.Width - used - gap);
            child.Measure(new Size(room, availableSize.Height));
            // Measured once, at the room left: a text that fills it was trimmed (or only just fits), and trimmed that
            // short it says nothing.
            if (!first && room < MinShown && child.DesiredSize.Width >= room - 0.5)
            {
                _dropped.Add(child);
                continue;
            }
            if (child.DesiredSize.Width <= 0)
                continue;
            used += gap + child.DesiredSize.Width;
            height = Math.Max(height, child.DesiredSize.Height);
            first = false;
        }
        return new Size(Math.Min(used, availableSize.Width), height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0;
        var first = true;
        foreach (var child in Children)
        {
            if (!child.IsVisible)
                continue;
            var gap = first ? 0 : Spacing;
            var width = Math.Min(child.DesiredSize.Width, Math.Max(0, finalSize.Width - x - gap));
            if (_dropped.Contains(child) || width <= 0)
            {
                // Out of sight past the right edge (the panel clips): a zero-width text would still draw its ellipsis.
                child.Arrange(new Rect(finalSize.Width + 1, 0, 0, finalSize.Height));
                continue;
            }
            child.Arrange(new Rect(x + gap, 0, width, finalSize.Height));
            x += gap + width;
            first = false;
        }
        return finalSize;
    }
}
