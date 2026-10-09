using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Templates;
using Avalonia.Media;

namespace Apex.Editor.Controls;

/// <summary>
/// One icon from Apex's line set: 24-unit paths stroked at 1.75 with round caps, scaled to <see cref="Size"/> and drawn in
/// the inherited foreground. <see cref="Glyph"/> takes the glyph the view models already hand out (TypeStyles' ⌖ ◇ ▨…,
/// the command catalog's ↶ ⌕ ⋯…) or an icon name ("search"), so no view model changes to get the line set; a glyph with
/// no path is drawn as text, which keeps the catalog's rarer symbols working.
/// </summary>
public sealed class GlyphIcon : Control
{
    public static readonly StyledProperty<string?> GlyphProperty =
        AvaloniaProperty.Register<GlyphIcon, string?>(nameof(Glyph));

    public static readonly StyledProperty<double> SizeProperty =
        AvaloniaProperty.Register<GlyphIcon, double>(nameof(Size), 14);

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<GlyphIcon>();

    static GlyphIcon()
    {
        AffectsMeasure<GlyphIcon>(SizeProperty);
        AffectsRender<GlyphIcon>(GlyphProperty, SizeProperty, ForegroundProperty);
    }

    public string? Glyph
    {
        get => GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    public double Size
    {
        get => GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    // The design's paths (Apex Redesign, turns 5-6) plus the asset types it doesn't draw, in the same hand.
    private static readonly Dictionary<string, string> Paths = new()
    {
        // Asset families
        ["⌖"] = "M12 3a9 9 0 1 0 0 18a9 9 0 1 0 0-18zM12 2v5M12 17v5M2 12h5M17 12h5",          // weapon
        ["⊕"] = "M12 3a9 9 0 1 0 0 18a9 9 0 1 0 0-18zM12 8v8M8 12h8",                          // attachment
        ["◒"] = "M12 3a9 9 0 1 0 0 18a9 9 0 1 0 0-18zM3 12h18M7 16l3-4M13 16l4-4",              // camo
        ["◇"] = "M12 2l9 5v10l-9 5-9-5V7zM3 7l9 5 9-5M12 12v10",                               // xmodel
        ["♟"] = "M12 3a3.5 3.5 0 1 0 0 7a3.5 3.5 0 1 0 0-7zM5 21v-1a7 7 0 0 1 14 0v1",           // character
        ["⬢"] = "M3 16v-3l2-5h14l2 5v3zM3 16h18M7 16v3M17 16v3M7 12h.01M17 12h.01",            // vehicle
        ["▤"] = "M4 4h16v16H4zM4 9.5h16M4 14.5h16M9 4v16M15 4v16",                             // zbarrier
        ["▨"] = "M4 4h16v16H4zM4 14L14 4M10 20L20 10",                                         // material
        ["▦"] = "M4 4h16v16H4zM4 16l5-5 4 4 2-2 5 5M15 8.5h.01",                               // image
        ["⌁"] = "M3 12h4l3-7 4 14 3-7h4",                                                      // xanim
        ["✺"] = "M12 3c1 4 5 5 5 10a5 5 0 0 1-10 0c0-3 2-4 2-6 1 1 2 2 3 2 0-2-1-4 0-6z",          // fx
        ["◉"] = "M4 9v6M8 6v12M12 3v18M16 7v10M20 10v4",                                       // sound
        ["◈"] = "M12 3l9 9-9 9-9-9z",                                                          // anything else

        // Actions and chrome
        ["undo"] = "M9 14L4 9l5-5M4 9h10a6 6 0 0 1 0 12h-3",
        ["redo"] = "M15 14l5-5-5-5M20 9H10a6 6 0 0 0 0 12h3",
        ["plus"] = "M12 5v14M5 12h14",
        ["search"] = "M11 4a7 7 0 1 0 0 14a7 7 0 1 0 0-14zM20 20l-4-4",
        ["chevron-down"] = "M6 9l6 6 6-6",
        ["chevron-right"] = "M9 6l6 6-6 6",
        ["more"] = "M5 12h.01M12 12h.01M19 12h.01",
        ["expand"] = "M15 3h6v6M9 21H3v-6M21 3l-7 7M3 21l7-7",
        ["popout"] = "M14 3h7v7M21 3l-9 9M19 14v5a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V7a2 2 0 0 1 2-2h5",
        ["minimize"] = "M5 12h14",
        ["maximize"] = "M5 5h14v14H5z",
        ["restore"] = "M8 8h11v11H8zM5 16V5h11",
        ["close"] = "M6 6l12 12M18 6L6 18",
        ["play"] = "M7 4l13 8-13 8z",
        ["stop"] = "M6 6h12v12H6z",
        ["layout"] = "M4 4h16v16H4zM9 4v16M9 11h11",
        ["filter"] = "M4 5h16M7 12h10M10 19h4",
        ["arrow-up"] = "M12 19V5M6 11l6-6 6 6",
        ["arrow-right"] = "M5 12h14M13 6l6 6-6 6",
        ["warning"] = "M12 3l10 18H2zM12 10v5M12 18h.01",
        ["arrow-down"] = "M12 5v14M6 13l6 6 6-6",
        ["check"] = "M12 3a9 9 0 1 0 0 18a9 9 0 1 0 0-18zM8 12.5l3 3 5-6",
    };

    // The older Unicode glyphs buttons and the command catalog use, onto the line set.
    private static readonly Dictionary<string, string> Aliases = new()
    {
        ["↶"] = "undo", ["↷"] = "redo", ["+"] = "plus", ["⌕"] = "search", ["▾"] = "chevron-down", ["▸"] = "chevron-right",
        ["⋯"] = "more", ["⤢"] = "expand", ["⧉"] = "popout", ["─"] = "minimize", ["▢"] = "maximize", ["❐"] = "restore",
        ["✕"] = "close", ["▶"] = "play", ["■"] = "stop", ["↑"] = "arrow-up", ["→"] = "arrow-right", ["⚠"] = "warning",
    };

    private static readonly Dictionary<string, Geometry?> Cache = new();

    /// <summary>True when <paramref name="glyph"/> draws as a line icon rather than text.</summary>
    public static bool HasPath(string? glyph) => glyph is not null && GeometryOf(glyph) is not null;

    private static Geometry? GeometryOf(string glyph)
    {
        if (Cache.TryGetValue(glyph, out var cached))
            return cached;
        var key = Aliases.TryGetValue(glyph, out var alias) ? alias : glyph;
        var geometry = Paths.TryGetValue(key, out var data) ? StreamGeometry.Parse(data) : null;
        Cache[glyph] = geometry;
        return geometry;
    }

    protected override Size MeasureOverride(Size availableSize) => new(Size, Size);

    public override void Render(DrawingContext context)
    {
        if (Glyph is not { Length: > 0 } glyph || Foreground is not { } brush)
            return;
        var font = this.TryFindResource("UiFont", ActualThemeVariant, out var f) && f is FontFamily family ? family : FontFamily.Default;
        Draw(context, glyph, brush, new Rect(Bounds.Size), Size, font);
    }

    /// <summary>
    /// Draws <paramref name="glyph"/> centred in <paramref name="box"/> at <paramref name="size"/>, exactly as the control
    /// does: for drawings that stand in for one (the table's cells).
    /// </summary>
    public static void Draw(DrawingContext context, string glyph, IBrush brush, Rect box, double size, FontFamily font)
    {
        var origin = new Point(box.X + (box.Width - size) / 2, box.Y + (box.Height - size) / 2);
        if (GeometryOf(glyph) is { } geometry)
        {
            var scale = size / 24;
            using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(origin.X, origin.Y)))
                context.DrawGeometry(null, new Pen(brush, 1.75, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), geometry);
            return;
        }
        var text = new FormattedText(glyph, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface(font), size * 0.9, brush);
        context.DrawText(text, new Point(box.X + (box.Width - text.Width) / 2, box.Y + (box.Height - text.Height) / 2));
    }
}

/// <summary>
/// A button's content template for the icon classes: a string content (↶, ⋯, ✕, "search") draws as a <see cref="GlyphIcon"/>;
/// anything else (a swatch, a panel) is left to show itself.
/// </summary>
public sealed class GlyphTemplate : IDataTemplate
{
    public double Size { get; set; } = 14;

    // A ContentTemplate is used whether or not it matches, so content that is already a control is handed back as it
    // is, the way the default template shows it.
    public Control? Build(object? param) => param as Control ?? new GlyphIcon { Glyph = param as string, Size = Size };

    public bool Match(object? data) => data is string or Control;
}
