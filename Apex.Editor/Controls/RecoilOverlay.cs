using System;
using Apex.Editor.Services.Extensions.Simulation;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Apex.Editor.Controls;

/// <summary>
/// The recoil preview's spray overlay, drawn over the viewport as UI so APE's frame underneath is never touched: a
/// reticle with a degree ruler at the centre, the view's path through the last burst, and one dot per round where its
/// kick peaked (<see cref="SprayTrace"/>). Angles map to the screen as the first-person camera projects them (the
/// game's <c>cg_fov</c>, Hor+), so a dot sits where that view direction is in the scene at rest and the pattern scales
/// with the pane. Redrawn only when the trace changes: at rest it costs nothing.
/// </summary>
public sealed class RecoilOverlay : Control
{
    public static readonly StyledProperty<SprayTrace?> TraceProperty =
        AvaloniaProperty.Register<RecoilOverlay, SprayTrace?>(nameof(Trace));

    public static readonly StyledProperty<double> FieldOfViewProperty =
        AvaloniaProperty.Register<RecoilOverlay, double>(nameof(FieldOfView), 65.0);

    public static readonly StyledProperty<IBrush?> MarkBrushProperty =
        AvaloniaProperty.Register<RecoilOverlay, IBrush?>(nameof(MarkBrush));

    public static readonly StyledProperty<IBrush?> LatestBrushProperty =
        AvaloniaProperty.Register<RecoilOverlay, IBrush?>(nameof(LatestBrush));

    public static readonly StyledProperty<IBrush?> TraceBrushProperty =
        AvaloniaProperty.Register<RecoilOverlay, IBrush?>(nameof(TraceBrush));

    public static readonly StyledProperty<IBrush?> OutlineBrushProperty =
        AvaloniaProperty.Register<RecoilOverlay, IBrush?>(nameof(OutlineBrush));

    static RecoilOverlay()
    {
        AffectsRender<RecoilOverlay>(FieldOfViewProperty, MarkBrushProperty, LatestBrushProperty, TraceBrushProperty, OutlineBrushProperty);
    }

    public RecoilOverlay()
    {
        IsHitTestVisible = false;
        ClipToBounds = true;
    }

    public SprayTrace? Trace
    {
        get => GetValue(TraceProperty);
        set => SetValue(TraceProperty, value);
    }

    /// <summary>The first-person camera's <c>cg_fov</c> (horizontal degrees at 4:3), as the viewport is given it.</summary>
    public double FieldOfView
    {
        get => GetValue(FieldOfViewProperty);
        set => SetValue(FieldOfViewProperty, value);
    }

    /// <summary>The reticle and the dots.</summary>
    public IBrush? MarkBrush
    {
        get => GetValue(MarkBrushProperty);
        set => SetValue(MarkBrushProperty, value);
    }

    /// <summary>The latest round's dot.</summary>
    public IBrush? LatestBrush
    {
        get => GetValue(LatestBrushProperty);
        set => SetValue(LatestBrushProperty, value);
    }

    /// <summary>The path and the ruler.</summary>
    public IBrush? TraceBrush
    {
        get => GetValue(TraceBrushProperty);
        set => SetValue(TraceBrushProperty, value);
    }

    /// <summary>A dot's edge, so it reads on a bright sky as on the dark backdrop.</summary>
    public IBrush? OutlineBrush
    {
        get => GetValue(OutlineBrushProperty);
        set => SetValue(OutlineBrushProperty, value);
    }

    private const int RulerDegrees = 10;

    /// <summary>Times drawn (verification: a preview at rest draws nothing).</summary>
    public long Renders { get; private set; }

    private SprayTrace? _subscribed;
    private Pen? _markPen, _tracePen, _outlinePen;
    private double _penScale;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TraceProperty)
            Subscribe(VisualRoot is not null ? Trace : null);
        else if (change.Property == MarkBrushProperty || change.Property == TraceBrushProperty || change.Property == OutlineBrushProperty)
            _markPen = _tracePen = _outlinePen = null;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Subscribe(Trace);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Subscribe(null);
    }

    private void Subscribe(SprayTrace? trace)
    {
        if (ReferenceEquals(trace, _subscribed))
            return;
        if (_subscribed is not null)
            _subscribed.Changed -= InvalidateVisual;
        _subscribed = trace;
        if (trace is not null)
            trace.Changed += InvalidateVisual;
        InvalidateVisual();
    }

    /// <summary>Screen pixels from the centre per unit of tan(angle): the first-person camera's focal length.</summary>
    public static double FocalLength(double height, double cgFov)
    {
        var half = Math.Clamp(cgFov, 1.0, 170.0) * Math.PI / 360.0;
        return height * 0.5 / (0.75 * Math.Tan(half));
    }

    /// <summary>Where a view direction of (pitch, yaw) degrees lands, from the centre: up for negative pitch, left for positive yaw.</summary>
    public static Vector Offset(float pitch, float yaw, double focal) =>
        new(-focal * Math.Tan(yaw * (Math.PI / 180.0)), focal * Math.Tan(pitch * (Math.PI / 180.0)));

    public override void Render(DrawingContext context)
    {
        Renders++;
        var size = Bounds.Size;
        if (size.Width < 1 || size.Height < 1 || MarkBrush is null || TraceBrush is null)
            return;
        var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        // One device pixel wide (whole pixels at fractional scales), centred on a pixel: hairlines stay sharp at any DPI.
        var hair = Math.Max(1.0, Math.Round(scale)) / scale;
        if (_markPen is null || _penScale != scale)
        {
            _penScale = scale;
            _markPen = new Pen(MarkBrush, hair);
            _tracePen = new Pen(TraceBrush, hair);
            _outlinePen = OutlineBrush is null ? null : new Pen(OutlineBrush, hair);
        }
        double Snap(double v) => (Math.Floor(v * scale) + 0.5 * Math.Max(1.0, Math.Round(scale))) / scale;
        var cx = Snap(size.Width / 2);
        var cy = Snap(size.Height / 2);
        var focal = FocalLength(size.Height, FieldOfView);

        // The ruler: a short tick a degree (only 5° and 10° when degrees are crowded), longer at 5° and 10°, along both
        // axes out to 10°, where a burst's climb usually ends: a scale to read the dots by, not a grid.
        var perDegree = focal * Math.Tan(Math.PI / 180.0);
        var step = perDegree >= 6 ? 1 : 5;
        for (var d = step; d <= RulerDegrees; d += step)
        {
            var r = focal * Math.Tan(d * (Math.PI / 180.0));
            if (r > Math.Max(size.Width, size.Height))
                break;
            var len = d % 5 == 0 ? 4.0 : 2.0;
            var up = Snap(cy - r);
            var down = Snap(cy + r);
            var left = Snap(cx - r);
            var right = Snap(cx + r);
            context.DrawLine(_tracePen!, new Point(cx - len, up), new Point(cx + len, up));
            context.DrawLine(_tracePen!, new Point(cx - len, down), new Point(cx + len, down));
            context.DrawLine(_tracePen!, new Point(left, cy - len), new Point(left, cy + len));
            context.DrawLine(_tracePen!, new Point(right, cy - len), new Point(right, cy + len));
        }
        // The reticle: a cross with an open centre, so the first dot (often right there) stays visible.
        const double gap = 4, arm = 9;
        context.DrawLine(_markPen!, new Point(cx - gap - arm, cy), new Point(cx - gap, cy));
        context.DrawLine(_markPen!, new Point(cx + gap, cy), new Point(cx + gap + arm, cy));
        context.DrawLine(_markPen!, new Point(cx, cy - gap - arm), new Point(cx, cy - gap));
        context.DrawLine(_markPen!, new Point(cx, cy + gap), new Point(cx, cy + gap + arm));

        if (Trace is not { ViewSupported: true } trace)
            return;
        var centre = new Point(size.Width / 2, size.Height / 2);
        if (trace.TrailCount > 1)
        {
            // Points closer than a pixel to the last one drawn are skipped (the last is always drawn): a long burst on a
            // small pane costs what can be seen.
            var prev = centre + Offset(trace.TrailPoint(0).X, trace.TrailPoint(0).Y, focal);
            for (var i = 1; i < trace.TrailCount; i++)
            {
                var p = trace.TrailPoint(i);
                var next = centre + Offset(p.X, p.Y, focal);
                if (i < trace.TrailCount - 1 && Math.Abs(next.X - prev.X) < 1 && Math.Abs(next.Y - prev.Y) < 1)
                    continue;
                context.DrawLine(_tracePen!, prev, next);
                prev = next;
            }
        }
        const double radius = 2.5;
        for (var i = 0; i < trace.DotCount; i++)
        {
            var d = trace.Dot(i);
            var latest = i == trace.DotCount - 1;
            context.DrawEllipse(latest ? LatestBrush ?? MarkBrush : MarkBrush, _outlinePen, centre + Offset(d.X, d.Y, focal),
                latest ? radius + 0.5 : radius, latest ? radius + 0.5 : radius);
        }
    }
}
