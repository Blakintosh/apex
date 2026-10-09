using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Apex.Editor.Controls;
using Apex.Editor.ViewModels;

namespace Apex.Editor.Views;

/// <summary>
/// A property's value drawn the way its editor draws it at rest: the field, its text, a switch, the dropdown's glyph,
/// a reference's type glyph, a colour swatch, a list's summary. One element that draws itself, where the editor is
/// dozens: a screen of table cells is a few hundred of these, and each element costs its styling when it is first
/// shown. Kept in step with <see cref="PropertyEditorView"/>'s templates and the styles in Workspace.axaml (sizes,
/// paddings, the same tokens): TableChecks compares the two pixel by pixel in both themes.
/// </summary>
public sealed class CellDisplay : Control
{
    // SuggestBox hides its ▾ below this width.
    private const double NarrowArrow = 110;
    private const double FieldHeight = 24;

    // Tokens by theme: they never change within a theme, and a table draws hundreds of these.
    private static readonly Dictionary<(ThemeVariant, string), object> s_tokens = new();

    private readonly TableCellValue _owner;
    private readonly ColorSwatch? _swatch;
    private PropertyItemViewModel? _row;
    private bool _listening;

    public CellDisplay(TableCellValue owner, Type kind)
    {
        _owner = owner;
        Kind = kind;
        Focusable = true;
        Height = FieldHeight;
        if (kind == typeof(ColorPropertyViewModel))
        {
            // The swatch draws itself (checker, colour, edge): the editor's own.
            _swatch = new ColorSwatch { Width = 16, Height = 16 };
            LogicalChildren.Add(_swatch);
            VisualChildren.Add(_swatch);
        }
        ActualThemeVariantChanged += (_, _) =>
        {
            _mono = _ui = null;
            InvalidateVisual();
        };
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextElement.FontFamilyProperty)
        {
            _ui = null;
            InvalidateVisual();
        }
    }

    private CellDisplayAutomationPeer? _peer;

    protected override AutomationPeer OnCreateAutomationPeer() => _peer = new CellDisplayAutomationPeer(this);

    /// <summary>The column (the property's label), the value as drawn, and the problem, for assistive tech.</summary>
    internal string AutomationName => _row is null ? "" : $"{_row.Label}: {Text}";

    internal string? Problem => _row?.Problem;

    /// <summary>Flips a switch, as a click on it does.</summary>
    internal void Toggle()
    {
        if (IsEffectivelyEnabled && _row is TogglePropertyViewModel { IsRuleDisabled: false } toggle)
            toggle.IsOn = !toggle.IsOn;
    }

    /// <summary>The editor kind (property view model type) this draws.</summary>
    public Type Kind { get; }

    /// <summary>The text drawn: the value, the switch's On/Off, or the list's summary.</summary>
    public string? Text { get; private set; }

    /// <summary>A switch's drawn state; null for other kinds.</summary>
    public bool? IsOn { get; private set; }

    public void Show(PropertyItemViewModel row)
    {
        if (ReferenceEquals(row, _row))
            return;
        Listen(false);
        _row = row;
        Listen(this.IsAttachedToVisualTree());
        Refresh();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Listen(true);
        Refresh();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Listen(false);
    }

    // Listened to only while on screen, so a property never keeps a drawing alive that a closed table left behind.
    private void Listen(bool on)
    {
        if (on == _listening || _row is null)
            return;
        if (on)
            _row.PropertyChanged += Row_PropertyChanged;
        else
            _row.PropertyChanged -= Row_PropertyChanged;
        _listening = on;
    }

    private void Row_PropertyChanged(object? sender, PropertyChangedEventArgs e) => Refresh();

    private void Refresh()
    {
        var (name, help, on) = (AutomationName, Problem, IsOn);
        IsOn = null;
        Text = _row switch
        {
            TogglePropertyViewModel toggle => toggle.StateText,
            NumberPropertyViewModel number => number.DisplayText,
            // A labelled option reads by its name, as the dropdown shows it ("Best color compression").
            ChoicePropertyViewModel choice => Array.IndexOf(choice.Choices, choice.Value) < 0 ? ""
                : choice.HasLabels ? choice.LabelOf(choice.Value ?? "") : choice.Value,
            RefPropertyViewModel reference => reference.Value,
            ColorPropertyViewModel color => color.Value,
            LinesPropertyViewModel lines => lines.Summary,
            FilePropertyViewModel file => file.Value,
            SuggestPropertyViewModel suggest => suggest.Value,
            TextPropertyViewModel text => text.Value,
            _ => null,
        };
        if (_row is TogglePropertyViewModel t)
            IsOn = t.IsOn;
        if (_swatch is not null && _row is ColorPropertyViewModel c)
            _swatch.Color = c.Swatch;
        // A screen reader keeps what it was told: it hears the new value, problem and switch state as they change.
        _peer?.Changed(name, help, on);
        InvalidateVisual();
    }

    /// <summary>The ↶ / ↑ column beside the editor's value (24 px and a 2 px gap): reserved even while it is hidden, so the
    /// drawing leaves it too.</summary>
    private const double ActionsWidth = 26;

    private double ValueWidth(double available) =>
        Math.Max(0, Math.Min(available - ActionsWidth, _row?.EditorMaxWidth ?? double.PositiveInfinity));

    protected override Size MeasureOverride(Size availableSize)
    {
        _swatch?.Measure(new Size(16, 16));
        var width = ValueWidth(availableSize.Width);
        return new Size(double.IsInfinity(width) ? 0 : width + ActionsWidth, FieldHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        // The swatch button is 24 square with 4 padding, its glyph 16 square.
        _swatch?.Arrange(new Rect(4, (finalSize.Height - 16) / 2, 16, 16));
        return finalSize;
    }

    // ── Drawing ──────────────────────────────────────────────────────────────

    /// <summary>
    /// A token of <paramref name="anchor"/>'s theme, looked up once per theme. A key not found (yet) is looked up again
    /// next time; the cache starts over when the app's theme changes.
    /// </summary>
    internal static T? Token<T>(StyledElement anchor, string key)
    {
        var variant = anchor.ActualThemeVariant;
        if (!s_tokens.TryGetValue((variant, key), out var value) && anchor.TryFindResource(key, variant, out value) && value is not null)
        {
            if (!s_clearsOnThemeChange && Application.Current is { } app)
            {
                app.ActualThemeVariantChanged += (_, _) => s_tokens.Clear();
                s_clearsOnThemeChange = true;
            }
            s_tokens[(variant, key)] = value;
        }
        return value is T t ? t : default;
    }

    private static bool s_clearsOnThemeChange;

    private T? Token<T>(string key) => Token<T>(this, key);

    private double Size(string key, double fallback) => Token<double?>(key) ?? fallback;

    // The typefaces drawn with: the theme's mono, and the UI font the cell inherits.
    private Typeface? _mono, _ui;

    private Typeface Mono => _mono ??= new Typeface(Token<FontFamily>("MonoFont") ?? FontFamily.Default);

    private Typeface Ui => _ui ??= new Typeface(GetValue(TextElement.FontFamilyProperty));

    // The lines the last render drew, kept for the next while their text, font, size, brush and width hold: a cell
    // redraws on every hover out and theme change, and shaping text is most of a redraw.
    private List<(LineKey Key, TextLayout Line)> _lines = new(), _drawn = new();

    private readonly record struct LineKey(string Text, Typeface Face, double Size, IBrush? Brush, double MaxWidth, TextTrimming? Trimming);

    /// <summary>A line of text as a TextBlock lays it out.</summary>
    private TextLayout Line(string text, Typeface face, double size, IBrush? brush, double maxWidth = double.PositiveInfinity,
        TextTrimming? trimming = null)
    {
        var key = new LineKey(text, face, size, brush, Math.Max(0, maxWidth), trimming);
        var at = -1;
        for (var i = 0; i < _lines.Count; i++)
            if (_lines[i].Key == key)
            {
                at = i;
                break;
            }
        TextLayout line;
        if (at >= 0)
        {
            line = _lines[at].Line;
            _lines.RemoveAt(at);
        }
        else
            line = new TextLayout(text, face, size, brush, TextAlignment.Left, TextWrapping.NoWrap, trimming, null,
                FlowDirection.LeftToRight, key.MaxWidth);
        _drawn.Add((key, line));
        return line;
    }

    /// <summary>
    /// Where a TextBlock centred in <paramref name="height"/> puts its text: its height rounded up to the pixel, its
    /// offset rounded to the nearest (layout rounding).
    /// </summary>
    private static double Centred(TextLayout line, double height) => Math.Round((height - Math.Ceiling(line.Height)) / 2);

    /// <summary>Draws a line inside its own box, as a TextBlock's visual is cut to its bounds (a glyph's fringe stays in).</summary>
    private static void Draw(DrawingContext context, TextLayout line, Point at)
    {
        using (context.PushClip(new Rect(at, new Size(Math.Ceiling(line.WidthIncludingTrailingWhitespace), Math.Ceiling(line.Height)))))
            line.Draw(context, at);
    }

    public override void Render(DrawingContext context)
    {
        _drawn.Clear();
        try
        {
            RenderValue(context);
        }
        finally
        {
            // Lines this render didn't draw again are dropped, not disposed: the last frame's drawing may still be in the
            // compositor's hands, glyph runs and all.
            _lines.Clear();
            (_lines, _drawn) = (_drawn, _lines);
        }
    }

    private void RenderValue(DrawingContext context)
    {
        var width = ValueWidth(Bounds.Width);
        var height = Bounds.Height;
        // The whole cell takes the pointer, as the editor's field does.
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        if (_row is null || width <= 0)
            return;
        switch (_row)
        {
            case TogglePropertyViewModel:
                DrawSwitch(context, height);
                break;
            case LinesPropertyViewModel:
                DrawSummary(context, width, height);
                break;
            default:
                DrawField(context, width, height);
                break;
        }
    }

    /// <summary>The miniswitch at rest: a 24×14 track 2 px in (accent when on), its knob 2 px from the end, On/Off 8 px after.</summary>
    private void DrawSwitch(DrawingContext context, double height)
    {
        var on = IsOn == true;
        var track = new Rect(2, Math.Round((height - 14) / 2), 24, 14);
        context.DrawRectangle(Token<IBrush>(on ? "AccentBrush" : "LineStrongBrush"), null, new RoundedRect(track, 7));
        var knob = new Rect(on ? track.Right - 2 - 10 : track.X + 2, track.Y + 2, 10, 10);
        context.DrawRectangle(Token<IBrush>(on ? "OnAccentBrush" : "TextFaintBrush"), null, new RoundedRect(knob, 5));
        // On / Off in the values' face, as the editor's switch says it.
        var text = Line(Text ?? "", Mono, Size("FontSizeSm", 12), Token<IBrush>("TextBrush"));
        Draw(context, text, new Point(track.Right + 8, Centred(text, height)));
    }

    /// <summary>A line list's summary button: the field fill, the button's edge, the count trimmed to fit.</summary>
    private void DrawSummary(DrawingContext context, double width, double height)
    {
        var rect = new Rect(0, Math.Round((height - FieldHeight) / 2), width, FieldHeight);
        var edge = Token<Thickness>("ButtonBorderThemeThickness").Left;
        var radius = Token<CornerRadius>("ControlCornerRadius").TopLeft;
        var pen = edge > 0 && Token<IBrush>("ButtonBorderBrush") is { } border ? new Pen(border, edge) : null;
        context.DrawRectangle(Token<IBrush>("BgFieldBrush"), pen, new RoundedRect(pen is null ? rect : rect.Deflate(edge / 2), radius));
        var inner = rect.Deflate(new Thickness(edge + 8, edge, edge + 8, edge));
        var text = Line(Text ?? "", Mono, Size("FontSizeSm", 12), Token<IBrush>("TextBrush"), inner.Width, TextTrimming.CharacterEllipsis);
        using (context.PushClip(inner))
            text.Draw(context, new Point(inner.X, inner.Y + Centred(text, inner.Height)));
    }

    /// <summary>
    /// A flat field (text, file, reference, number, dropdown, colour, suggestions) at rest: no box (the field shows on
    /// hover), the value in mono at the editor's padding, and the kind's glyph.
    /// </summary>
    private void DrawField(DrawingContext context, double width, double height)
    {
        var color = _row is ColorPropertyViewModel;
        var left = color ? 26 : 0;
        var field = new Rect(left, Math.Round((height - FieldHeight) / 2), Math.Max(0, width - left), FieldHeight);
        var box = new RoundedRect(field, 5);

        var size = Size("FontSizeBase", 13);
        // A box that opens a list keeps its edge at rest (the editors' dropfield style): the field fill, an inset hairline.
        if (_row is ChoicePropertyViewModel or RefPropertyViewModel)
        {
            context.DrawRectangle(Token<IBrush>("BgFieldBrush"), null, box);
            if (Token<BoxShadows>("ShadowFieldEdge") is { Count: > 0 } edge)
                context.DrawRectangle(null, new Pen(new SolidColorBrush(edge[0].Color), 1), new RoundedRect(field.Deflate(0.5), 4.5));
        }
        switch (_row)
        {
            case NumberPropertyViewModel:
            {
                // ScrubNumberBox: 6 px in, clipped to its rounded box.
                var text = Line(Text ?? "", Mono, size, Token<IBrush>("TextBrush"));
                using (context.PushClip(box))
                    text.Draw(context, new Point(field.X + 6, field.Y + Centred(text, FieldHeight)));
                return;
            }
            case ChoicePropertyViewModel:
            {
                // ComboBox: the selection 8 px in; the glyph 12 px square, 10 px from the right end, unrounded.
                var text = Line(Text ?? "", Mono, size, Token<IBrush>("TextBrush"));
                Draw(context, text, new Point(field.X + 8, field.Y + Centred(text, FieldHeight)));
                DrawChevron(context, new Point(field.Right - 10 - 12, field.Y + (FieldHeight - 12) / 2),
                    Token<IBrush>("ComboBoxDropDownGlyphForeground"));
                return;
            }
        }

        // TextBox: the text inside its padding, clipped there. A reference's buttons (RefField: ▾, and the open arrow while
        // the name resolves, 20 px each, after its 2 px padding) and a suggestion list's ▾ take their room from the text.
        var reference = _row is RefPropertyViewModel;
        var refButtons = _row is RefPropertyViewModel { CanGoTo: true } ? 2 : 1;
        if (field.Width < RefField.MinWidthForButtons)
            refButtons = 0;
        var padLeft = reference ? 28 : 8;
        var padRight = _row switch
        {
            RefPropertyViewModel => 2 + 20 * refButtons,
            FilePropertyViewModel => 26,
            SuggestPropertyViewModel => 22,
            _ => 8,
        };
        // A file path, as its field shows it out of editing: trimmed from the front, so the file's own name shows.
        var value = _row is FilePropertyViewModel
            ? Line(Text ?? "", Mono, size, Token<IBrush>("TextControlForeground"), Math.Max(0, field.Width - padLeft - padRight), TextTrimming.LeadingCharacterEllipsis)
            : Line(Text ?? "", Mono, size, Token<IBrush>("TextControlForeground"));
        var top = field.Y + Centred(value, FieldHeight);
        using (context.PushClip(new Rect(field.X + padLeft, top, Math.Max(0, field.Width - padLeft - padRight), Math.Ceiling(value.Height))))
            value.Draw(context, new Point(field.X + padLeft, top));

        if (_row is RefPropertyViewModel r && r.RefBrush is { } refBrush)
        {
            // The referenced type's icon, 13 px, 8 px in, centred on the field.
            GlyphIcon.Draw(context, r.RefGlyph, refBrush, new Rect(field.X + 8, field.Y + Math.Round((FieldHeight - 13) / 2, MidpointRounding.ToEven), 13, 13), 13,
                Token<FontFamily>("UiFont") ?? FontFamily.Default);
            var dim = Token<IBrush>("TextBrush");
            var y = field.Y + (FieldHeight - 12) / 2;
            // Each glyph 12 px square, centred in its 20 px button; the buttons end at the field's edge (a TextBox's inner
            // right content sits outside its padding).
            if (refButtons > 0)
                DrawChevron(context, new Point(field.Right - 20 + 4, y), dim);
            if (refButtons > 1)
                using (context.PushTransform(Matrix.CreateTranslation(field.Right - 40 + 4, y)))
                    context.DrawGeometry(null, new Pen(dim, 1.3, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), FieldGlyphs.OpenArrow);
        }
        else if (_row is SuggestPropertyViewModel { CanSuggest: true } && field.Width >= NarrowArrow)
        {
            // SuggestBox's ▾: the dropdown chevron, centred in a 20 px button 1 px from the right end.
            DrawChevron(context, new Point(field.Right - 1 - 20 + 4, field.Y + (FieldHeight - 12) / 2), Token<IBrush>("TextDimBrush"));
        }
    }

    /// <summary>The dropdown chevron in its 12 px box at <paramref name="at"/>, as a PathIcon draws it (uniformly scaled, unrounded).</summary>
    private void DrawChevron(DrawingContext context, Point at, IBrush? brush)
    {
        var glyph = FieldGlyphs.Chevron(this);
        var bounds = glyph.Bounds;
        var scale = Math.Min(12 / bounds.Width, 12 / bounds.Height);
        var origin = new Point(at.X + (12 - bounds.Width * scale) / 2, at.Y + (12 - bounds.Height * scale) / 2);
        using (context.PushTransform(Matrix.CreateTranslation(-bounds.X, -bounds.Y) * Matrix.CreateScale(scale, scale)
                                     * Matrix.CreateTranslation(origin.X, origin.Y)))
            context.DrawGeometry(brush, null, glyph);
    }

    // ── Input: the editor takes over ─────────────────────────────────────────

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        _owner.DisplayFocused(e);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        // Also when the editor has already taken this drawing's place: the scene the press was aimed at is a frame old.
        _owner.DisplayPressed(e);
    }
}

/// <summary>A drawn cell to assistive tech: named by its column and value, its problem as help, a switch toggleable.</summary>
internal sealed class CellDisplayAutomationPeer : ControlAutomationPeer, IToggleProvider
{
    public CellDisplayAutomationPeer(CellDisplay owner) : base(owner) { }

    private new CellDisplay Owner => (CellDisplay)base.Owner;

    protected override string GetNameCore() => Owner.AutomationName;

    protected override string? GetHelpTextCore() => Owner.Problem ?? base.GetHelpTextCore();

    protected override AutomationControlType GetAutomationControlTypeCore() =>
        Owner.IsOn is null ? AutomationControlType.Edit : AutomationControlType.CheckBox;

    protected override bool IsContentElementCore() => true;

    protected override bool IsControlElementCore() => true;

    protected override object? GetProviderCore(Type providerType) =>
        providerType == typeof(IToggleProvider) && Owner.IsOn is null ? null : base.GetProviderCore(providerType);

    public ToggleState ToggleState => Owner.IsOn == true ? ToggleState.On : ToggleState.Off;

    public void Toggle() => Owner.Toggle();

    /// <summary>Raises what changed since <paramref name="name"/>, <paramref name="help"/> and <paramref name="on"/>.</summary>
    internal void Changed(string name, string? help, bool? on)
    {
        if (name != Owner.AutomationName)
            RaisePropertyChangedEvent(AutomationElementIdentifiers.NameProperty, name, Owner.AutomationName);
        if (help != Owner.Problem)
            RaisePropertyChangedEvent(AutomationElementIdentifiers.HelpTextProperty, help, Owner.Problem);
        if (on != Owner.IsOn && on is not null && Owner.IsOn is not null)
            RaisePropertyChangedEvent(TogglePatternIdentifiers.ToggleStateProperty,
                on == true ? ToggleState.On : ToggleState.Off, ToggleState);
    }
}
