using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Apex.Editor.Controls;

/// <summary>HSV ↔ RGB, all components 0–1 except hue (degrees, 0–360).</summary>
public static class ColorMath
{
    public static (double R, double G, double B) FromHsv(double h, double s, double v)
    {
        h = ((h % 360) + 360) % 360 / 60;
        var c = v * s;
        var x = c * (1 - Math.Abs(h % 2 - 1));
        var (r, g, b) = (int)h switch
        {
            0 => (c, x, 0d),
            1 => (x, c, 0d),
            2 => (0d, c, x),
            3 => (0d, x, c),
            4 => (x, 0d, c),
            _ => (c, 0d, x),
        };
        var m = v - c;
        return (r + m, g + m, b + m);
    }

    /// <summary>The hue, saturation and brightness of a colour. A grey has no hue: <paramref name="keepHue"/> stays, as a
    /// black keeps <paramref name="keepSaturation"/>, so dragging through them doesn't lose where the user was.</summary>
    public static (double H, double S, double V) ToHsv(double r, double g, double b, double keepHue = 0, double keepSaturation = 0)
    {
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var d = max - min;
        if (max <= 0)
            return (keepHue, keepSaturation, 0);
        var s = d / max;
        if (d <= 1e-9)
            return (keepHue, s, max);
        double h;
        if (max == r)
            h = 60 * (((g - b) / d) % 6);
        else if (max == g)
            h = 60 * ((b - r) / d + 2);
        else
            h = 60 * ((r - g) / d + 4);
        return (h < 0 ? h + 360 : h, s, max);
    }

    public static Color ToColor(double r, double g, double b, double a = 1) =>
        Color.FromArgb(Byte(a), Byte(r), Byte(g), Byte(b));

    private static byte Byte(double v) => (byte)Math.Round(Math.Clamp(v, 0, 1) * 255);
}

/// <summary>
/// The picker's saturation (left to right) × brightness (bottom to top) square at <see cref="Hue"/>. A press or drag
/// picks; the arrow keys move by 1% (Shift: 10%). <see cref="Picked"/> says the first move of a press from the rest.
/// </summary>
public sealed class ColorSquare : Control
{
    public static readonly StyledProperty<double> HueProperty = AvaloniaProperty.Register<ColorSquare, double>(nameof(Hue));
    public static readonly StyledProperty<double> SaturationProperty = AvaloniaProperty.Register<ColorSquare, double>(nameof(Saturation));
    public static readonly StyledProperty<double> BrightnessProperty = AvaloniaProperty.Register<ColorSquare, double>(nameof(Brightness));

    static ColorSquare()
    {
        AffectsRender<ColorSquare>(HueProperty, SaturationProperty, BrightnessProperty);
        FocusableProperty.OverrideDefaultValue<ColorSquare>(true);
    }

    public ColorSquare()
    {
        Cursor = new Cursor(StandardCursorType.Cross);
    }

    public double Hue { get => GetValue(HueProperty); set => SetValue(HueProperty, value); }
    public double Saturation { get => GetValue(SaturationProperty); set => SetValue(SaturationProperty, value); }
    public double Brightness { get => GetValue(BrightnessProperty); set => SetValue(BrightnessProperty, value); }

    /// <summary>(saturation, brightness, first move of this press or key).</summary>
    public event Action<double, double, bool>? Picked;

    private bool _dragging;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        Focus(NavigationMethod.Pointer);
        e.Pointer.Capture(this);
        _dragging = true;
        PickAt(e.GetPosition(this), first: true);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragging)
            PickAt(e.GetPosition(this), first: false);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragging = false;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _dragging = false;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 0.1 : 0.01;
        var (ds, dv) = e.Key switch
        {
            Key.Left => (-step, 0d),
            Key.Right => (step, 0d),
            Key.Up => (0d, step),
            Key.Down => (0d, -step),
            _ => (0d, 0d),
        };
        if (ds == 0 && dv == 0)
            return;
        Pick(Saturation + ds, Brightness + dv, first: !_keyRun);
        _keyRun = true;
        e.Handled = true;
    }

    // Key repeats join one undo step, like a scrub; the next press after a key-up starts another.
    private bool _keyRun;

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        _keyRun = false;
    }

    private void PickAt(Point p, bool first)
    {
        var w = Math.Max(1, Bounds.Width);
        var h = Math.Max(1, Bounds.Height);
        Pick(p.X / w, 1 - p.Y / h, first);
    }

    private void Pick(double s, double v, bool first)
    {
        s = Math.Clamp(s, 0, 1);
        v = Math.Clamp(v, 0, 1);
        Saturation = s;
        Brightness = v;
        Picked?.Invoke(s, v, first);
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return;
        var shape = new RoundedRect(bounds, 4);
        using (context.PushClip(shape))
        {
            var (r, g, b) = ColorMath.FromHsv(Hue, 1, 1);
            context.FillRectangle(new SolidColorBrush(ColorMath.ToColor(r, g, b)), bounds);
            context.FillRectangle(new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Colors.White, 0), new GradientStop(Color.FromArgb(0, 255, 255, 255), 1) },
            }, bounds);
            context.FillRectangle(new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.FromArgb(0, 0, 0, 0), 0), new GradientStop(Colors.Black, 1) },
            }, bounds);
        }
        var at = new Point(Saturation * bounds.Width, (1 - Brightness) * bounds.Height);
        var (cr, cg, cb) = ColorMath.FromHsv(Hue, Saturation, Brightness);
        context.DrawEllipse(new SolidColorBrush(ColorMath.ToColor(cr, cg, cb)), new Pen(Brushes.White, 2), at, 6, 6);
        context.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(140, 0, 0, 0)), 1), at, 7.5, 7.5);
        if (IsFocused)
            context.DrawRectangle(null, new Pen(TryBrush("FocusBrush") ?? Brushes.White, 2), shape.Deflate(0.5, 0.5));
    }

    private IBrush? TryBrush(string key) => this.TryFindResource(key, ActualThemeVariant, out var v) ? v as IBrush : null;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != IsFocusedProperty)
            return;
        if (!IsFocused)
            _keyRun = false;
        InvalidateVisual();
    }
}

/// <summary>
/// A horizontal strip the picker slides along: the hue (0–360°, as 0–1) or the alpha over a checkerboard, toward
/// <see cref="Tint"/>. Left/Right move by 1% (Shift: 10%).
/// </summary>
public sealed class ColorStrip : Control
{
    private const double Cell = 4;

    public static readonly StyledProperty<double> PositionProperty = AvaloniaProperty.Register<ColorStrip, double>(nameof(Position));
    public static readonly StyledProperty<bool> IsAlphaProperty = AvaloniaProperty.Register<ColorStrip, bool>(nameof(IsAlpha));
    public static readonly StyledProperty<Color> TintProperty = AvaloniaProperty.Register<ColorStrip, Color>(nameof(Tint), Colors.White);

    static ColorStrip()
    {
        AffectsRender<ColorStrip>(PositionProperty, IsAlphaProperty, TintProperty);
        FocusableProperty.OverrideDefaultValue<ColorStrip>(true);
    }

    public ColorStrip()
    {
        Cursor = new Cursor(StandardCursorType.Hand);
        Height = 12;
    }

    public double Position { get => GetValue(PositionProperty); set => SetValue(PositionProperty, value); }
    public bool IsAlpha { get => GetValue(IsAlphaProperty); set => SetValue(IsAlphaProperty, value); }
    public Color Tint { get => GetValue(TintProperty); set => SetValue(TintProperty, value); }

    /// <summary>(position 0–1, first move of this press or key).</summary>
    public event Action<double, bool>? Picked;

    private bool _dragging, _keyRun;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        Focus(NavigationMethod.Pointer);
        e.Pointer.Capture(this);
        _dragging = true;
        Pick(e.GetPosition(this).X / Math.Max(1, Bounds.Width), first: true);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragging)
            Pick(e.GetPosition(this).X / Math.Max(1, Bounds.Width), first: false);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragging = false;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _dragging = false;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 0.1 : 0.01;
        var d = e.Key switch { Key.Left => -step, Key.Right => step, _ => 0 };
        if (d == 0)
            return;
        Pick(Position + d, first: !_keyRun);
        _keyRun = true;
        e.Handled = true;
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        _keyRun = false;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != IsFocusedProperty)
            return;
        if (!IsFocused)
            _keyRun = false;
        InvalidateVisual();
    }

    private void Pick(double position, bool first)
    {
        position = Math.Clamp(position, 0, 1);
        Position = position;
        Picked?.Invoke(position, first);
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return;
        var shape = new RoundedRect(bounds, bounds.Height / 2);
        using (context.PushClip(shape))
        {
            if (IsAlpha)
            {
                context.FillRectangle(TryBrush("CheckerBaseBrush") ?? Brushes.White, bounds);
                if (TryBrush("CheckerAltBrush") is { } dark)
                    for (var y = 0; y * Cell < bounds.Height; y++)
                        for (var x = y % 2; x * Cell < bounds.Width; x += 2)
                            context.FillRectangle(dark, new Rect(x * Cell, y * Cell, Cell, Cell));
                context.FillRectangle(new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                    EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
                    GradientStops = { new GradientStop(Color.FromArgb(0, Tint.R, Tint.G, Tint.B), 0), new GradientStop(Color.FromArgb(255, Tint.R, Tint.G, Tint.B), 1) },
                }, bounds);
            }
            else
            {
                var hue = new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                    EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
                };
                for (var i = 0; i <= 6; i++)
                {
                    var (r, g, b) = ColorMath.FromHsv(i * 60, 1, 1);
                    hue.GradientStops.Add(new GradientStop(ColorMath.ToColor(r, g, b), i / 6.0));
                }
                context.FillRectangle(hue, bounds);
            }
        }
        var x0 = Math.Clamp(Position * bounds.Width, 3, bounds.Width - 3);
        var marker = new RoundedRect(new Rect(x0 - 3, -1, 6, bounds.Height + 2), 3);
        context.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(140, 0, 0, 0)), 3), marker);
        context.DrawRectangle(null, new Pen(Brushes.White, 1.5), marker);
        if (IsFocused)
            context.DrawRectangle(null, new Pen(TryBrush("FocusBrush") ?? Brushes.White, 2), shape.Deflate(0.5, 0.5));
    }

    private IBrush? TryBrush(string key) => this.TryFindResource(key, ActualThemeVariant, out var v) ? v as IBrush : null;
}
