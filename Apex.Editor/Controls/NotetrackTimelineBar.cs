using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Apex.Editor.Services.Preview.Notetracks;
using Apex.Editor.ViewModels;

namespace Apex.Editor.Controls;

/// <summary>
/// The notetrack strip under the anim frame slider, spanning the same frames: two labelled lanes, the xanim's own
/// exported notes ("From anim") over the GDT entry's notes ("GDT"). Each note is a tick on its frame: solid when it can be
/// heard or isn't a sound, hollow for a sound the preview can't play. Ticks within a few pixels of each other (the same
/// frame, or neighbours at a narrow width) share one column, named "+N"; a name is drawn beside a column only where it
/// fits. Hovering a column names everything in it, just above it, after the usual tooltip delay. Click a note to go to
/// its frame, or press and drag anywhere to scrub; each note answers a press within 12 px either side. With keyboard
/// focus, ← → step a frame (as everywhere in the preview) and Ctrl+← → go to the previous / next note.
/// Draw-only (no child controls), so playback redraws cost one pass over the markers.
/// </summary>
public sealed class NotetrackTimelineBar : Control
{
    public static readonly StyledProperty<NotetrackTimeline?> TimelineProperty =
        AvaloniaProperty.Register<NotetrackTimelineBar, NotetrackTimeline?>(nameof(Timeline));

    public static readonly StyledProperty<double> FrameProperty =
        AvaloniaProperty.Register<NotetrackTimelineBar, double>(nameof(Frame), defaultBindingMode: BindingMode.TwoWay);

    /// <summary>Each lane's height; a press anywhere in the strip answers, so a note's target is 24 px wide and the strip tall.</summary>
    public const double LaneHeight = 13, LaneGap = 2, HitSlop = 12;

    private const double ClusterSlop = 7, TickWidth = 2, LabelGap = 8;

    /// <summary>A column of markers drawn at one x in one lane.</summary>
    private sealed class Cluster(double x, int lane, NotetrackMarker first)
    {
        public double X { get; } = x;
        public int Lane { get; } = lane;
        public List<NotetrackMarker> Markers { get; } = new() { first };
        /// <summary>Where the next column in this lane starts (the strip's end for the last): the room for this one's name.</summary>
        public double NextX { get; set; }
    }

    /// <summary>The brushes and pens of the current theme, looked up once (not per tick per frame).</summary>
    private sealed class Paint(NotetrackTimelineBar bar)
    {
        public readonly IBrush Faint = bar.Brush("TextFaintBrush"), Dim = bar.Brush("TextDimBrush"), Accent = bar.Brush("AccentBrush"),
            Hover = bar.Brush("BgHoverBrush");
        public readonly IPen Line = new Pen(bar.Brush("LineBrush"), 1), Focus = new Pen(bar.Brush("FocusBrush"), 1),
            Head = new Pen(bar.Brush("AccentBrush"), 1), Silent = new Pen(bar.Brush("TextDimBrush"), 1);
    }

    private Paint? _paint;

    private List<Cluster>? _clusters;
    private double _clustersWidth;
    private Cluster? _hover;
    private bool _dragging;
    private bool _focusVisible;
    private readonly Dictionary<(string, IBrush), FormattedText> _labels = new();
    private NotetrackTimeline? _subscribed;
    private readonly DispatcherTimer _tipTimer;
    private long _tipClosedAt;

    static NotetrackTimelineBar()
    {
        FocusableProperty.OverrideDefaultValue<NotetrackTimelineBar>(true);
        AffectsRender<NotetrackTimelineBar>(TimelineProperty, FrameProperty);
    }

    public NotetrackTimelineBar()
    {
        ActualThemeVariantChanged += (_, _) =>
        {
            _labels.Clear();
            _paint = null;
            InvalidateVisual();
        };
        Height = LaneHeight * 2 + LaneGap;
        // The tip is placed by hand just above the column it names, after the usual delay.
        ToolTip.SetServiceEnabled(this, false);
        ToolTip.SetPlacement(this, PlacementMode.TopEdgeAlignedLeft);
        _tipTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ToolTip.GetShowDelay(this)) };
        _tipTimer.Tick += (_, _) =>
        {
            _tipTimer.Stop();
            OpenTip();
        };
    }

    public NotetrackTimeline? Timeline
    {
        get => GetValue(TimelineProperty);
        set => SetValue(TimelineProperty, value);
    }

    /// <summary>The preview's frame (two-way: clicks and drags here move the preview).</summary>
    public double Frame
    {
        get => GetValue(FrameProperty);
        set => SetValue(FrameProperty, value);
    }

    private IReadOnlyList<NotetrackMarker> Markers => Timeline?.Markers ?? Array.Empty<NotetrackMarker>();

    private int LastFrame => Timeline?.LastFrameIndex ?? 0;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TimelineProperty)
        {
            // Listen only while on screen, so a detached strip never keeps a timeline (or is kept by one).
            Subscribe(VisualRoot is not null ? change.GetNewValue<NotetrackTimeline?>() : null);
            Invalidate();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Subscribe(null);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Subscribe(Timeline);
        Invalidate();
    }

    private void Subscribe(NotetrackTimeline? timeline)
    {
        if (ReferenceEquals(timeline, _subscribed))
            return;
        if (_subscribed is not null)
            _subscribed.PropertyChanged -= Timeline_PropertyChanged;
        _subscribed = timeline;
        if (timeline is not null)
            timeline.PropertyChanged += Timeline_PropertyChanged;
    }

    private void Timeline_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(NotetrackTimeline.Markers) or nameof(NotetrackTimeline.LastFrameIndex))
            Invalidate();
    }

    private void Invalidate()
    {
        _clusters = null;
        _hover = null;
        _labels.Clear();
        InvalidateVisual();
        CloseTip();
    }

    // ── Geometry ────────────────────────────────────────────────────────────

    private double XOf(double frame) => LastFrame > 0 ? Math.Clamp(frame / LastFrame, 0, 1) * Bounds.Width : 0;

    private double FrameAt(double x) => LastFrame > 0 && Bounds.Width > 0 ? Math.Clamp(x / Bounds.Width, 0, 1) * LastFrame : 0;

    private static double LaneTop(int lane) => lane * (LaneHeight + LaneGap);

    private static int LaneOf(NotetrackMarker m) => m.Source == NotetrackSource.Export ? 0 : 1;

    private List<Cluster> Clusters()
    {
        if (_clusters is not null && _clustersWidth == Bounds.Width)
            return _clusters;
        var clusters = new List<Cluster>();
        foreach (var lane in new[] { 0, 1 })
        {
            Cluster? open = null;
            foreach (var m in Markers.Where(m => LaneOf(m) == lane))
            {
                var x = XOf(m.Frame);
                if (open is not null && x - open.X <= ClusterSlop)
                {
                    open.Markers.Add(m);
                    continue;
                }
                if (open is not null)
                    open.NextX = x;
                open = new Cluster(x, lane, m);
                clusters.Add(open);
            }
            if (open is not null)
                open.NextX = Bounds.Width;
        }
        _clustersWidth = Bounds.Width;
        return _clusters = clusters;
    }

    /// <summary>The column nearest <paramref name="p"/> within <see cref="HitSlop"/> px either side, the lane under the pointer first.</summary>
    private Cluster? HitTest(Point p)
    {
        var lane = p.Y < LaneTop(1) - LaneGap / 2 ? 0 : 1;
        Cluster? best = null;
        var bestDistance = double.MaxValue;
        foreach (var c in Clusters())
        {
            var d = Math.Abs(c.X - p.X);
            // Prefer the lane under the pointer; the other lane's column still answers when this one has nothing near.
            var weighted = c.Lane == lane ? d : d + HitSlop;
            if (d <= HitSlop && weighted < bestDistance)
            {
                best = c;
                bestDistance = weighted;
            }
        }
        return best;
    }

    // ── Input ───────────────────────────────────────────────────────────────

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        if (_dragging)
        {
            Frame = Math.Round(FrameAt(p.X));
            return;
        }
        SetHover(HitTest(p));
    }

    private void SetHover(Cluster? hit)
    {
        if (ReferenceEquals(hit, _hover))
            return;
        _hover = hit;
        InvalidateVisual();
        var tip = hit is null ? null : string.Join(Environment.NewLine, hit.Markers.Select(m => m.Describe()));
        ToolTip.SetTip(this, tip);
        var wasOpen = ToolTip.GetIsOpen(this);
        CloseTip();
        if (hit is null)
            return;
        // From one column's tip to the next it shows at once, as Windows does between neighbouring tips.
        if (wasOpen || Environment.TickCount64 - _tipClosedAt < ToolTip.GetBetweenShowDelay(this) + 200)
            OpenTip();
        else
            _tipTimer.Start();
    }

    /// <summary>Opens the hover tip just above the column it names (not above the whole strip, far from it).</summary>
    private void OpenTip()
    {
        if (_hover is null || _dragging)
            return;
        ToolTip.SetHorizontalOffset(this, Math.Round(_hover.X) - 8);
        ToolTip.SetVerticalOffset(this, LaneTop(_hover.Lane) - 4);
        ToolTip.SetIsOpen(this, true);
    }

    private void CloseTip()
    {
        _tipTimer.Stop();
        if (!ToolTip.GetIsOpen(this))
            return;
        ToolTip.SetIsOpen(this, false);
        _tipClosedAt = Environment.TickCount64;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        // A tooltip popup opening under the pointer reads as an exit while the pointer is still over the strip.
        if (!_dragging && !new Rect(Bounds.Size).Contains(e.GetPosition(this)))
            SetHover(null);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || LastFrame <= 0)
            return;
        e.Handled = true;
        Focus(NavigationMethod.Pointer);
        _focusVisible = false;
        CloseTip();
        var p = e.GetPosition(this);
        if (Timeline is { } timeline)
            timeline.IsScrubbing = true;
        _dragging = true;
        e.Pointer.Capture(this);
        // On a note: its frame. Elsewhere: the frame under the pointer, and the drag scrubs from there.
        Frame = HitTest(p) is { } hit ? Math.Min(hit.Markers[0].Frame, LastFrame) : Math.Round(FrameAt(p.X));
        Timeline?.ScrubPressed();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_dragging)
            return;
        e.Handled = true;
        EndDrag();
        e.Pointer.Capture(null);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        EndDrag();
    }

    private void EndDrag()
    {
        if (!_dragging)
            return;
        _dragging = false;
        if (Timeline is { } timeline)
            timeline.IsScrubbing = false;
    }

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        _focusVisible = e.NavigationMethod is NavigationMethod.Tab or NavigationMethod.Directional;
        InvalidateVisual();
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        _focusVisible = false;
        InvalidateVisual();
    }

    /// <summary>
    /// Ctrl+← → go to the previous / next note and name it. Plain ← → are left to the preview, which steps a frame: the
    /// same keys mean the same thing on this strip, the slider and the notetracks dock's lanes.
    /// </summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || Timeline is not { } timeline)
            return;
        _focusVisible = true; // driven from the keyboard (even after a click focused it): show where the keys go
        InvalidateVisual();
        var direction = (e.Key, e.KeyModifiers) switch
        {
            (Key.Left, KeyModifiers.Control) => -1,
            (Key.Right, KeyModifiers.Control) => 1,
            _ => 0,
        };
        if (direction == 0)
            return;
        e.Handled = true;
        if (timeline.Neighbour(Math.Round(Frame), direction) is not { } next || (direction > 0 && Frame >= LastFrame && next.Frame > LastFrame))
            return;
        Frame = Math.Min(next.Frame, LastFrame);
        // Name what it landed on, as hovering would.
        var cluster = Clusters().FirstOrDefault(c => c.Markers.Contains(next));
        _hover = null;
        SetHover(cluster);
    }

    // ── Automation ──────────────────────────────────────────────────────────
    // The strip is drawn, so a screen reader has nothing to read but what the peer says: its name, how many notes
    // there are, and the note at the playhead (or the nearest one), the same words the hover tip uses.

    protected override AutomationPeer OnCreateAutomationPeer() => new TimelinePeer(this);

    /// <summary>"12 notes. At the playhead: fire  ·  frame 7  ·  exported in the xanim", or "Nearest: …" when none sits on the current frame.</summary>
    public string AutomationHelpText()
    {
        var markers = Markers;
        if (markers.Count == 0)
            return EmptyText;
        var frame = Math.Round(Frame);
        var nearest = markers.MinBy(m => Math.Abs(m.Frame - frame))!;
        var count = markers.Count == 1 ? "1 note" : $"{markers.Count.ToString(CultureInfo.CurrentCulture)} notes";
        return $"{count}. {(nearest.Frame == frame ? "At the playhead" : "Nearest")}: {nearest.Describe()}";
    }

    private const string EmptyText = "No notes in this xanim or its GDT entry";

    private sealed class TimelinePeer(NotetrackTimelineBar owner) : ControlAutomationPeer(owner)
    {
        protected override string? GetNameCore() => base.GetNameCore() is { Length: > 0 } name ? name : "Notetracks";

        protected override string? GetHelpTextCore() => owner.AutomationHelpText();

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Group;

        protected override bool IsContentElementCore() => true;

        protected override bool IsControlElementCore() => true;
    }

    // ── Drawing ─────────────────────────────────────────────────────────────

    private IBrush Brush(string key) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush ? brush : Brushes.Gray;

    public override void Render(DrawingContext context)
    {
        if (Bounds.Width <= 0 || Bounds.Height <= 0)
            return;
        var size = Bounds.Size;
        // Hit-testable across the whole strip, so the thin ticks can be hovered and the track pressed.
        context.FillRectangle(Brushes.Transparent, new Rect(size));

        var paint = _paint ??= new Paint(this);
        for (var lane = 0; lane < 2; lane++)
        {
            var y = Math.Round(LaneTop(lane) + LaneHeight) - 0.5;
            context.DrawLine(paint.Line, new Point(0, y), new Point(size.Width, y));
            // Each lane says what it holds, in the gutter left of the strip (under the step buttons, beside the slider's
            // track), so the ticks keep the frames to themselves.
            var name = Text(lane == 0 ? "From anim" : "GDT", paint.Faint);
            context.DrawText(name, new Point(-LabelGap - name.Width, LaneTop(lane) + (LaneHeight - name.Height) / 2));
        }

        if (_focusVisible && IsFocused)
            context.DrawRectangle(paint.Focus, new Rect(-3.5, -1.5, size.Width + 7, size.Height + 2), 3);

        if (Markers.Count == 0)
        {
            var empty = Text(EmptyText, paint.Faint);
            context.DrawText(empty, new Point(Math.Max(0, (size.Width - empty.Width) / 2), (size.Height - empty.Height) / 2));
            return;
        }

        var clusters = Clusters();
        if (_hover is { } hover && clusters.Contains(hover))
            context.FillRectangle(paint.Hover, new Rect(Math.Round(hover.X) - 5, LaneTop(hover.Lane), 10, LaneHeight), 2);

        // Playhead under the ticks, so a tick it sits on stays visible.
        var head = Math.Round(XOf(Frame)) + 0.5;
        context.DrawLine(paint.Head, new Point(head, 0), new Point(head, size.Height));

        foreach (var c in clusters)
        {
            var left = Math.Round(c.X - TickWidth / 2);
            var tick = new Rect(left, LaneTop(c.Lane) + 2, TickWidth, LaneHeight - 4);
            // Two encodings only: the lane says where a note comes from; solid or hollow, whether it can be heard.
            if (c.Markers.All(m => m.IsSilent))
                context.DrawRectangle(paint.Silent, new Rect(left - 1, tick.Y, TickWidth + 2, tick.Height).Deflate(0.5));
            else
                context.FillRectangle(paint.Dim, tick);

            // The name beside the column, only where it fits before the next column in this lane.
            var textLeft = left + TickWidth + 4;
            var room = c.NextX - textLeft - 4;
            if (room < 24)
                continue;
            var name = c.Markers[0].Name;
            if (c.Markers.Count > 1)
                name += $" +{(c.Markers.Count - 1).ToString(CultureInfo.InvariantCulture)}";
            var text = Text(name, paint.Faint);
            if (text.Width <= room)
                context.DrawText(text, new Point(textLeft, LaneTop(c.Lane) + (LaneHeight - text.Height) / 2));
        }
    }

    private FormattedText Text(string s, IBrush brush)
    {
        if (_labels.TryGetValue((s, brush), out var cached))
            return cached;
        var font = this.TryFindResource("UiFont", ActualThemeVariant, out var f) && f is FontFamily family ? family : FontFamily.Default;
        var em = this.TryFindResource("FontSizeXs", ActualThemeVariant, out var e) && e is double d ? d : 11;
        var text = new FormattedText(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(font), em, brush);
        if (_labels.Count > 512)
            _labels.Clear();
        _labels[(s, brush)] = text;
        return text;
    }
}
