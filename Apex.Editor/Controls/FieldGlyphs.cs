using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using System.Linq;

namespace Apex.Editor.Controls;

/// <summary>
/// The glyphs a field's own buttons draw, as shapes (a font glyph renders as a smudge at this size). The dropdown chevron
/// is the one the theme's dropdowns draw, so a choice, a suggestion list and a reference all open with the same mark.
/// </summary>
public static class FieldGlyphs
{
    private static Geometry? s_chevron;

    /// <summary>
    /// The dropdown's own glyph, read from the ComboBox template the theme gives it (built once, never shown), so every
    /// list opener draws exactly what a choice's dropdown draws.
    /// </summary>
    public static Geometry Chevron(StyledElement? anchor = null)
    {
        if (s_chevron is not null)
            return s_chevron;
        object? found = null;
        if (anchor?.TryFindResource(typeof(ComboBox), out found) != true)
            Application.Current?.TryFindResource(typeof(ComboBox), out found);
        for (var theme = found as ControlTheme; theme is not null; theme = theme.BasedOn)
            if (theme.Setters.OfType<Setter>().FirstOrDefault(x => x.Property == TemplatedControl.TemplateProperty)?.Value is Avalonia.Controls.Templates.IControlTemplate template
                && template.Build(new ComboBox()) is { } built && built.NameScope.Find<PathIcon>("DropDownGlyph")?.Data is { } data)
                return s_chevron = data;
        return s_chevron = Geometry.Parse("M1939 486L2029 576L1024 1581L19 576L109 486L1024 1401L1939 486Z");
    }

    /// <summary>An arrow leaving for a corner, in a 12 px box: "open what this names".</summary>
    public static readonly Geometry OpenArrow = Geometry.Parse("M3.5,8.5 L8.5,3.5 M4.5,3.5 L8.5,3.5 L8.5,7.5");

    /// <summary>The chevron that opens a field's list, in its button's foreground.</summary>
    public static Control DropDown() => new PathIcon
    {
        Data = Chevron(),
        Width = 12,
        Height = 12,
        UseLayoutRounding = false,
        IsHitTestVisible = false,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>The arrow that opens the asset a reference names, stroked in its button's foreground.</summary>
    public static Control GoTo()
    {
        var path = new Path
        {
            Data = OpenArrow,
            Width = 12,
            Height = 12,
            StrokeThickness = 1.3,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
            IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        path.Bind(Shape.StrokeProperty, new Binding(nameof(TemplatedControl.Foreground))
        {
            RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor) { AncestorType = typeof(Button) },
        });
        return path;
    }
}
