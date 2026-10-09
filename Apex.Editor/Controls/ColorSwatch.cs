using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Apex.Editor.Controls;

/// <summary>
/// A colour chip: the colour painted over a checkerboard, so a translucent alpha reads as translucent. The checker and
/// the hairline edge come from the theme's tokens (the preview pane's checker).
/// </summary>
public sealed class ColorSwatch : Control
{
    private const double Cell = 4;

    public static readonly StyledProperty<IBrush?> ColorProperty =
        AvaloniaProperty.Register<ColorSwatch, IBrush?>(nameof(Color));

    static ColorSwatch()
    {
        AffectsRender<ColorSwatch>(ColorProperty);
    }

    public IBrush? Color
    {
        get => GetValue(ColorProperty);
        set => SetValue(ColorProperty, value);
    }

    public ColorSwatch()
    {
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    private IBrush? Token(string key) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) ? value as IBrush : null;

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return;
        var shape = new RoundedRect(bounds, 2);
        using (context.PushClip(shape))
        {
            context.FillRectangle(Token("CheckerBaseBrush") ?? Brushes.Transparent, bounds);
            if (Token("CheckerAltBrush") is { } dark)
                for (var y = 0; y * Cell < bounds.Height; y++)
                    for (var x = y % 2; x * Cell < bounds.Width; x += 2)
                        context.FillRectangle(dark, new Rect(x * Cell, y * Cell, Cell, Cell));
            if (Color is { } color)
                context.FillRectangle(color, bounds);
        }
        if (Token("LineBrush") is { } line)
            context.DrawRectangle(null, new Pen(line, 1), shape.Deflate(0.5, 0.5));
    }
}
