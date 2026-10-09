using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Apex.Editor.ViewModels;

namespace Apex.Editor.Controls;

/// <summary>
/// The notetracks dock's timeline: a frame ruler over one lane per kind of note (Notes, the xanim's own notes, FX,
/// Sound), the playhead across them all. Notes that would cover each other stack in rows within their lane; a note past
/// the clip's last frame sits at the right edge, labelled with its frame.
/// <para>
/// Pointer: press or drag the ruler or an empty part of a lane to scrub. Press a marker to select it (its row comes into
/// view); click it to also move the playhead there; double-click it to type in its first parameter. Drag a marker to
/// retime its note: the preview scrubs with it, the frame shows on the ruler, it snaps to where the playhead was and to
/// the xanim's own notes (hold Alt to place it freely), and Shift moves it a quarter as fast for fine placement; Esc
/// cancels. Double-click an empty part of a lane to add a note there.
/// </para>
/// <para>
/// Keys: ← → step a frame (the transport's meaning, as everywhere in the preview); Ctrl+← → go to the previous / next
/// note; Shift+← → nudge the selected note a frame; ↑ ↓ move between lanes; Insert adds a note at the playhead on the
/// current lane; Enter types in the selected note's first parameter; Delete removes it; Esc lets go of it.
/// </para>
/// Draw-only (no child controls), so playback redraws cost one pass over the markers.
/// </summary>
public sealed class NotetrackLanes : Control
{
    public static readonly StyledProperty<IReadOnlyList<NotetrackLane>?> LanesProperty =
        AvaloniaProperty.Register<NotetrackLanes, IReadOnlyList<NotetrackLane>?>(nameof(Lanes));

    public static readonly StyledProperty<IReadOnlyList<LaneMarker>?> MarkersProperty =
        AvaloniaProperty.Register<NotetrackLanes, IReadOnlyList<LaneMarker>?>(nameof(Markers));

    public static readonly StyledProperty<int> LastFrameProperty =
        AvaloniaProperty.Register<NotetrackLanes, int>(nameof(LastFrame));

    public static readonly StyledProperty<double> FrameProperty =
        AvaloniaProperty.Register<NotetrackLanes, double>(nameof(Frame), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<string?> SelectedKeyProperty =
        AvaloniaProperty.Register<NotetrackLanes, string?>(nameof(SelectedKey));

    /// <summary>The label column, then the track; the right inset keeps the last frame's label inside.</summary>
    public const double LabelWidth = 84, RightInset = 14;

    private const double Top = 6, RulerHeight = 16, LaneGap = 4, Bottom = 8, PillHeight = 22, RowGap = 2, LanePad = 3,
        DragSlop = 3, SnapPixels = 6, FineFactor = 0.25, MinPillSpan = 64, MaxRows = 3;

    /// <summary>A marker was pressed: select it (and show its row), without moving the playhead.</summary>
    public event Action<LaneMarker>? MarkerPressed;

    /// <summary>A marker was clicked (pressed and released without dragging): the playhead goes to it.</summary>
    public event Action<LaneMarker>? MarkerClicked;

    /// <summary>A marker was double-clicked: type in its first parameter.</summary>
    public event Action<LaneMarker>? MarkerDoubleClicked;

    /// <summary>A marker was dragged to a new frame.</summary>
    public event Action<LaneMarker, int>? MarkerRetimed;

    /// <summary>An empty part of an editable lane was double-clicked.</summary>
    public event Action<NotetrackLane, int>? AddRequested;

    /// <summary>Delete with a note selected.</summary>
    public event Action<string>? DeleteRequested;

    /// <summary>A press that scrubs began (true) or ended (false): the sounds play as a drag, not as jumps.</summary>
    public event Action<bool>? Scrubbing;

    /// <summary>↑ ↓ landed on a note (selected, the playhead left where it is), or on an empty lane (null: nothing selected).</summary>
    public event Action<LaneMarker?>? SelectRequested;

    /// <summary>Ctrl+← → : the previous (-1) or next (+1) note.</summary>
    public event Action<int>? NeighbourRequested;

    /// <summary>Shift+← → : the selected note a frame earlier (-1) or later (+1).</summary>
    public event Action<int>? NudgeRequested;

    /// <summary>Insert: a note at the playhead on this lane.</summary>
    public event Action<NotetrackLane>? InsertRequested;

    /// <summary>Enter: type in the selected note's first parameter.</summary>
    public event Action<string>? EditRequested;

    private sealed class Paint(NotetrackLanes c)
    {
        public readonly IBrush Lane = c.Brush("BgFieldBrush"), Pill = c.Brush("BgHoverBrush"), PillHover = c.Brush("BgActiveBrush"),
            Selected = c.Brush("AccentDimBrush"), Text = c.Brush("TextBrush"), Dim = c.Brush("TextDimBrush"), Faint = c.Brush("TextFaintBrush"),
            Accent = c.Brush("AccentBrush"), OnAccent = c.Brush("OnAccentBrush"), Danger = c.Brush("DangerBrush"),
            DangerText = c.Brush("DangerTextBrush");
        public readonly IPen LaneEdge = new Pen(c.Brush("LineBrush"), 1), PillEdge = new Pen(c.Brush("TickBrush"), 1),
            SelectedPen = new Pen(c.Brush("AccentBrush"), 1), Focus = new Pen(c.Brush("FocusBrush"), 2),
            Parked = new Pen(c.Brush("TextFaintBrush"), 1, new DashStyle(new double[] { 2, 2 }, 0));
    }

    /// <summary>A marker's place: its lane, its row within the lane, its pill, and whether its text fits.</summary>
    private readonly record struct Placed(LaneMarker Marker, int Lane, int Row, Rect Pill, bool ShowText, bool PastEnd);

    private sealed class Layout
    {
        public required List<Placed> Pills { get; init; }
        public required int[] Rows { get; init; }
        public required double[] LaneTops { get; init; }
    }

    private Paint? _paint;
    private Layout? _layout;
    private double _layoutWidth = -1;
    private readonly Dictionary<(string, IBrush, bool), FormattedText> _texts = new();
    private LaneMarker? _hover;
    private Rect _hoverPill;
    private LaneMarker? _pressed;
    private Point _pressPoint;
    private double _dragX, _lastX;
    private double _parked;
    private int? _dragFrame;
    private string? _snappedTo;
    private bool _scrubbing;
    private bool _focusVisible;
    private int _keyLane;
    private readonly DispatcherTimer _tipTimer;
    private long _tipClosedAt;

    static NotetrackLanes()
    {
        FocusableProperty.OverrideDefaultValue<NotetrackLanes>(true);
        AffectsRender<NotetrackLanes>(FrameProperty, SelectedKeyProperty);
        AffectsMeasure<NotetrackLanes>(LanesProperty, MarkersProperty, LastFrameProperty);
    }

    public NotetrackLanes()
    {
        ActualThemeVariantChanged += (_, _) =>
        {
            _paint = null;
            _texts.Clear();
            _layout = null;
            InvalidateVisual();
        };
        // The tip is placed by hand over the marker it names, after the usual delay (the service would open it over the
        // whole control at once).
        ToolTip.SetServiceEnabled(this, false);
        ToolTip.SetPlacement(this, PlacementMode.TopEdgeAlignedLeft);
        _tipTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ToolTip.GetShowDelay(this)) };
        _tipTimer.Tick += (_, _) =>
        {
            _tipTimer.Stop();
            OpenTip();
        };
        ClipToBounds = true;
    }

    public IReadOnlyList<NotetrackLane>? Lanes { get => GetValue(LanesProperty); set => SetValue(LanesProperty, value); }
    public IReadOnlyList<LaneMarker>? Markers { get => GetValue(MarkersProperty); set => SetValue(MarkersProperty, value); }
    public int LastFrame { get => GetValue(LastFrameProperty); set => SetValue(LastFrameProperty, value); }
    public double Frame { get => GetValue(FrameProperty); set => SetValue(FrameProperty, value); }
    public string? SelectedKey { get => GetValue(SelectedKeyProperty); set => SetValue(SelectedKeyProperty, value); }

    private IReadOnlyList<NotetrackLane> ShownLanes => Lanes ?? Array.Empty<NotetrackLane>();

    private IEnumerable<LaneMarker> AllMarkers => Markers ?? Array.Empty<LaneMarker>();

    /// <summary>The frame a marker drag would drop on right now (null while nothing is dragged).</summary>
    public int? DragFrame => _dragFrame;

    /// <summary>What the dragged marker is snapped to ("playhead", or an exported note's name), or null.</summary>
    public string? SnappedTo => _snappedTo;

    /// <summary>The lane ↑ ↓ and Insert act on.</summary>
    public NotetrackLane KeyLane => ShownLanes.Count > 0 ? ShownLanes[Math.Clamp(_keyLane, 0, ShownLanes.Count - 1)] : NotetrackLane.Notes;

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsFinite(availableSize.Width) ? availableSize.Width : Bounds.Width;
        var layout = LayoutFor(width);
        return new Size(0, LaneBottom(layout, ShownLanes.Count - 1) + Bottom);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == MarkersProperty || change.Property == LanesProperty || change.Property == LastFrameProperty)
        {
            _layout = null;
            _hover = null;
            CloseTip();
            InvalidateVisual();
        }
        else if (change.Property == SelectedKeyProperty && change.GetNewValue<string?>() is { } key
                 && AllMarkers.FirstOrDefault(m => m.Key == key) is { } selected && LaneIndex(selected.Lane) is var lane and >= 0)
            _keyLane = lane;
    }

    // ── Geometry ────────────────────────────────────────────────────────────

    private double TrackLeft => LabelWidth;
    private double TrackWidthFor(double width) => Math.Max(1, width - LabelWidth - RightInset);
    private double TrackWidth => TrackWidthFor(Bounds.Width);
    private double TrackRight => TrackLeft + TrackWidth;

    private double XOf(double frame, double width) =>
        TrackLeft + (LastFrame > 0 ? Math.Clamp(frame / LastFrame, 0, 1) : 0) * TrackWidthFor(width);

    /// <summary>The x of <paramref name="frame"/> on the track (in this control's coordinates; past the end, the end).</summary>
    public double XOf(double frame) => XOf(frame, Bounds.Width);

    private double FrameAt(double x) => LastFrame > 0 ? Math.Clamp((x - TrackLeft) / TrackWidth, 0, 1) * LastFrame : 0;

    private static double LaneHeight(int rows) => LanePad * 2 + rows * PillHeight + (rows - 1) * RowGap;

    private static double LaneBottom(Layout layout, int index) =>
        index < 0 ? Top + RulerHeight : layout.LaneTops[index] + LaneHeight(layout.Rows[index]);

    private int LaneIndex(NotetrackLane lane)
    {
        var lanes = ShownLanes;
        for (var i = 0; i < lanes.Count; i++)
            if (lanes[i] == lane)
                return i;
        return -1;
    }

    private int? LaneAt(double y)
    {
        var layout = CurrentLayout();
        for (var i = 0; i < ShownLanes.Count; i++)
            if (y >= layout.LaneTops[i] - LaneGap / 2 && y < LaneBottom(layout, i) + LaneGap / 2)
                return i;
        return null;
    }

    /// <summary>The vertical centre of lane <paramref name="index"/>'s first row (Apex.Shots aims its presses here).</summary>
    public double LaneCenterY(int index) => CurrentLayout().LaneTops[index] + LanePad + PillHeight / 2;

    /// <summary>The centre of a marker's pill, or null when it isn't drawn.</summary>
    public Point? CenterOf(string key) =>
        CurrentLayout().Pills.FirstOrDefault(p => p.Marker.Key == key) is { Marker: not null } placed ? placed.Pill.Center : null;

    /// <summary>How many rows a lane's notes need so that none covers another.</summary>
    public int RowsIn(NotetrackLane lane) => LaneIndex(lane) is var i and >= 0 ? CurrentLayout().Rows[i] : 0;

    private Layout CurrentLayout() => LayoutFor(Bounds.Width);

    /// <summary>
    /// Places every marker: in its lane, in the first row where its pill (at least <see cref="MinPillSpan"/> wide, so a
    /// name starts to read) clears the pill before it, up to <see cref="MaxRows"/> rows; then each pill runs to its text's
    /// end or the next pill in its row. A note past the last frame is placed against the right edge.
    /// </summary>
    private Layout LayoutFor(double width)
    {
        if (_layout is not null && _layoutWidth == width)
            return _layout;
        var lanes = ShownLanes;
        var rows = new int[lanes.Count];
        var pills = new List<Placed>();
        var right = TrackLeft + TrackWidthFor(width);
        var byLane = new List<(LaneMarker Marker, double X, double Want, int Row, bool PastEnd)>[lanes.Count];
        for (var i = 0; i < lanes.Count; i++)
            byLane[i] = new();
        foreach (var m in AllMarkers.OrderBy(m => m.Frame).ThenBy(m => m.Key, StringComparer.Ordinal))
        {
            var lane = LaneIndex(m.Lane);
            if (lane < 0)
                continue;
            var pastEnd = LastFrame > 0 && m.Frame > LastFrame;
            var want = Text(Label(m, pastEnd), Brushes.Black).Width + 15;
            byLane[lane].Add((m, Math.Round(XOf(m.Frame, width)), want, 0, pastEnd));
        }
        for (var lane = 0; lane < lanes.Count; lane++)
        {
            var list = byLane[lane];
            var rowEnds = new List<double>();
            for (var k = 0; k < list.Count; k++)
            {
                var (m, x, want, _, pastEnd) = list[k];
                var span = Math.Min(want, MinPillSpan);
                var start = pastEnd ? right - span : x;
                var row = rowEnds.FindIndex(end => end + 2 <= start);
                if (row < 0)
                {
                    if (rowEnds.Count < MaxRows)
                    {
                        rowEnds.Add(double.MinValue);
                        row = rowEnds.Count - 1;
                    }
                    else
                        row = rowEnds.IndexOf(rowEnds.Min());
                }
                rowEnds[row] = Math.Max(rowEnds[row], start + span);
                list[k] = (m, x, want, row, pastEnd);
            }
            rows[lane] = Math.Max(1, rowEnds.Count);
        }
        var tops = new double[lanes.Count];
        var y = Top + RulerHeight + LaneGap;
        for (var lane = 0; lane < lanes.Count; lane++)
        {
            tops[lane] = y;
            y += LaneHeight(rows[lane]) + LaneGap;
        }
        for (var lane = 0; lane < lanes.Count; lane++)
        {
            var list = byLane[lane];
            for (var k = 0; k < list.Count; k++)
            {
                var (m, x, want, row, pastEnd) = list[k];
                var top = tops[lane] + LanePad + row * (PillHeight + RowGap);
                Rect pill;
                if (pastEnd)
                {
                    // Against the right edge, after whatever precedes it in its row.
                    var before = list.Take(k).Where(o => o.Row == row).Select(o => o.X).DefaultIfEmpty(TrackLeft - 2).Max();
                    var w = Math.Max(6, Math.Min(want, right + RightInset - 2 - (before + 2)));
                    pill = new Rect(right + RightInset - 2 - w, top, w, PillHeight);
                }
                else
                {
                    var next = list.Skip(k + 1).Where(o => o.Row == row).Select(o => o.PastEnd ? right - Math.Min(o.Want, MinPillSpan) : o.X)
                        .DefaultIfEmpty(width - 2).Min() - 2;
                    pill = new Rect(x, top, Math.Max(6, Math.Min(want, next - x)), PillHeight);
                }
                pills.Add(new Placed(m, lane, row, pill, pill.Width >= 26, pastEnd));
            }
        }
        _layoutWidth = width;
        return _layout = new Layout { Pills = pills, Rows = rows, LaneTops = tops };
    }

    private static string Label(LaneMarker m, bool pastEnd)
    {
        var text = m.Problem is not null ? "⚠ " + m.Text : m.Text;
        return pastEnd ? $"{text}  → {m.Frame.ToString(CultureInfo.InvariantCulture)}" : text;
    }

    /// <summary>The marker drawn at <paramref name="p"/> (in this control's coordinates), or null.</summary>
    public LaneMarker? MarkerAt(Point p) => HitTest(p)?.Marker;

    private Placed? HitTest(Point p)
    {
        Placed? best = null;
        foreach (var placed in CurrentLayout().Pills)
            if (placed.Pill.Inflate(new Thickness(3, 1)).Contains(p))
                best = placed; // later (rightward) pills draw on top
        return best;
    }

    // ── Input ───────────────────────────────────────────────────────────────

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed)
            return;
        e.Handled = true;
        Focus(NavigationMethod.Pointer);
        _focusVisible = false;
        CloseTip();
        var p = e.GetPosition(this);
        var hit = HitTest(p);

        if (e.ClickCount == 2)
        {
            if (hit is { } twice)
            {
                MarkerDoubleClicked?.Invoke(twice.Marker);
                return;
            }
            if (LaneAt(p.Y) is { } laneIndex && ShownLanes[laneIndex] != NotetrackLane.FromAnim && p.X >= TrackLeft)
            {
                AddRequested?.Invoke(ShownLanes[laneIndex], (int)Math.Round(FrameAt(p.X)));
                return;
            }
        }

        e.Pointer.Capture(this);
        _pressPoint = p;
        if (hit is { } placed)
        {
            _pressed = placed.Marker;
            _dragFrame = null;
            _snappedTo = null;
            _parked = Frame;
            _dragX = _lastX = p.X;
            _keyLane = placed.Lane;
            MarkerPressed?.Invoke(placed.Marker);
            return;
        }
        if (LastFrame <= 0)
            return;
        if (LaneAt(p.Y) is { } lane)
            _keyLane = lane;
        _scrubbing = true;
        Scrubbing?.Invoke(true);
        Frame = Math.Round(FrameAt(p.X));
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        if (_scrubbing)
        {
            Frame = Math.Round(FrameAt(p.X));
            return;
        }
        if (_pressed is { } pressed)
        {
            if (!pressed.CanRetime || LastFrame <= 0 || (_dragFrame is null && Math.Abs(p.X - _pressPoint.X) < DragSlop))
                return;
            if (_dragFrame is null)
                Scrubbing?.Invoke(true); // the preview follows the drag: its sounds play as a scrub
            // Shift moves the marker a quarter as fast as the pointer, for frames packed closer than a pixel or two.
            var dx = p.X - _lastX;
            _lastX = p.X;
            _dragX += e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? dx * FineFactor : dx;
            // The marker keeps the spot it was grabbed by: its frame moves with the pointer, not to it.
            var grab = _pressPoint.X - XOf(Math.Min(pressed.Frame, LastFrame));
            var frame = Math.Clamp((int)Math.Round(FrameAt(_dragX - grab)), 1, Math.Max(1, LastFrame));
            string? snapped = null;
            if (!e.KeyModifiers.HasFlag(KeyModifiers.Alt))
                (frame, snapped) = Snap(frame, _dragX - grab);
            if (frame != _dragFrame || snapped != _snappedTo)
            {
                _dragFrame = frame;
                _snappedTo = snapped;
                Frame = frame;
                InvalidateVisual();
            }
            return;
        }
        SetHover(HitTest(p));
    }

    /// <summary>
    /// The frame a drag at <paramref name="x"/> lands on: the frame the playhead was parked on, or an exported note's,
    /// when one is within a few pixels; otherwise <paramref name="frame"/>.
    /// </summary>
    private (int Frame, string? To) Snap(int frame, double x)
    {
        var targets = new List<(int Frame, string Name)>();
        var parked = (int)Math.Round(_parked);
        if (parked >= 1)
            targets.Add((parked, "playhead"));
        foreach (var m in AllMarkers)
            if (m.IsReadOnly && m.Frame >= 1 && m.Frame <= LastFrame)
                targets.Add((m.Frame, m.Text));
        var best = targets.Select(t => (t.Frame, t.Name, Distance: Math.Abs(XOf(t.Frame) - x)))
            .Where(t => t.Distance <= SnapPixels).OrderBy(t => t.Distance).FirstOrDefault();
        return best.Name is null ? (frame, null) : (best.Frame, best.Name);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        var pressed = _pressed;
        var dropped = _dragFrame;
        EndPress();
        e.Pointer.Capture(null);
        if (pressed is null)
            return;
        if (dropped is null)
            MarkerClicked?.Invoke(pressed);
        else if (dropped != pressed.Frame)
            MarkerRetimed?.Invoke(pressed, dropped.Value);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        // Capture taken away mid-drag (another window, a menu): the drag never happened.
        if (_dragFrame is not null)
            Frame = _parked;
        EndPress();
    }

    /// <summary>Esc during a drag: the marker stays where it was and the playhead goes back to where it was parked.</summary>
    private void CancelDrag()
    {
        Frame = _parked;
        EndPress();
    }

    private void EndPress()
    {
        if (_scrubbing || _dragFrame is not null)
        {
            _scrubbing = false;
            Scrubbing?.Invoke(false);
        }
        if (_pressed is not null || _dragFrame is not null)
        {
            _pressed = null;
            _dragFrame = null;
            _snappedTo = null;
            InvalidateVisual();
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (!new Rect(Bounds.Size).Contains(e.GetPosition(this)))
            SetHover(null);
    }

    private void SetHover(Placed? hit)
    {
        if (hit?.Marker == _hover)
            return;
        _hover = hit?.Marker;
        _hoverPill = hit?.Pill ?? default;
        InvalidateVisual();
        ToolTip.SetTip(this, hit?.Marker.Tip);
        var wasOpen = ToolTip.GetIsOpen(this);
        CloseTip();
        if (hit is null)
            return;
        // Moving from one marker's tip to the next shows it at once, as Windows does between neighbouring tips.
        if (wasOpen || Environment.TickCount64 - _tipClosedAt < ToolTip.GetBetweenShowDelay(this) + 200)
            OpenTip();
        else
            _tipTimer.Start();
    }

    /// <summary>Opens the hover tip just above the marker it names.</summary>
    private void OpenTip()
    {
        if (_hover is null || _pressed is not null || _scrubbing)
            return;
        ToolTip.SetHorizontalOffset(this, _hoverPill.X);
        ToolTip.SetVerticalOffset(this, _hoverPill.Y - 4);
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

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled)
            return;
        var selected = SelectedKey is { } key ? AllMarkers.FirstOrDefault(m => m.Key == key) : null;
        var handled = (e.Key, e.KeyModifiers) switch
        {
            (Key.Escape, KeyModifiers.None) when _dragFrame is not null => Do(CancelDrag),
            (Key.Escape, KeyModifiers.None) when selected is not null => Do(() => SelectRequested?.Invoke(null)),
            (Key.Delete or Key.Back, KeyModifiers.None) when selected is { IsReadOnly: false } => Do(() => DeleteRequested?.Invoke(selected.Key)),
            (Key.Enter, KeyModifiers.None) when selected is not null => Do(() => EditRequested?.Invoke(selected.Key)),
            (Key.Insert, KeyModifiers.None) => Do(() => InsertRequested?.Invoke(KeyLane)),
            (Key.Up or Key.Down, KeyModifiers.None) when ShownLanes.Count > 0 => Do(() => MoveLane(e.Key == Key.Down ? 1 : -1)),
            (Key.Left or Key.Right, KeyModifiers.Control) => Do(() => NeighbourRequested?.Invoke(e.Key == Key.Right ? 1 : -1)),
            (Key.Left or Key.Right, KeyModifiers.Shift) when selected is { CanRetime: true } => Do(() => NudgeRequested?.Invoke(e.Key == Key.Right ? 1 : -1)),
            _ => false,
        };
        // Driven from the keyboard (even after a click focused it): show where the keys go. The transport's keys
        // (← → Home End Space) go on to the dock.
        _focusVisible = true;
        InvalidateVisual();
        e.Handled = handled;

        static bool Do(Action action)
        {
            action();
            return true;
        }
    }

    /// <summary>↑ ↓ : the lane above or below, and on it the note nearest the playhead (none on an empty lane).</summary>
    private void MoveLane(int delta)
    {
        _keyLane = Math.Clamp(_keyLane + delta, 0, ShownLanes.Count - 1);
        var lane = ShownLanes[_keyLane];
        var nearest = AllMarkers.Where(m => m.Lane == lane).OrderBy(m => Math.Abs(Math.Min(m.Frame, Math.Max(LastFrame, 0)) - Frame)).FirstOrDefault();
        SelectRequested?.Invoke(nearest);
        InvalidateVisual();
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

    // ── Automation ──────────────────────────────────────────────────────────

    protected override AutomationPeer OnCreateAutomationPeer() => new LanesPeer(this);

    /// <summary>"3 notes. Frame 12 of 54. Selected: Sound · wpn_fire  ·  frame 3  ·  Note 1".</summary>
    public string AutomationHelpText()
    {
        var count = AllMarkers.Count();
        var notes = count == 1 ? "1 note" : $"{count.ToString(CultureInfo.CurrentCulture)} notes";
        var at = LastFrame > 0 ? $" Frame {Math.Round(Frame).ToString(CultureInfo.CurrentCulture)} of {LastFrame.ToString(CultureInfo.CurrentCulture)}." : "";
        var selected = AllMarkers.FirstOrDefault(m => m.Key == SelectedKey) is { } s ? $" Selected: {s.Tip.Split('\n')[0]}" : "";
        return $"{notes}.{at}{selected}";
    }

    private sealed class LanesPeer(NotetrackLanes owner) : ControlAutomationPeer(owner)
    {
        protected override string? GetNameCore() => base.GetNameCore() is { Length: > 0 } name ? name : "Notetracks timeline";
        protected override string? GetHelpTextCore() => owner.AutomationHelpText();
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Group;
        protected override bool IsContentElementCore() => true;
        protected override bool IsControlElementCore() => true;
    }

    // ── Drawing ─────────────────────────────────────────────────────────────

    private IBrush Brush(string key) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush ? brush : Brushes.Gray;

    private static string LaneName(NotetrackLane lane) => lane switch
    {
        NotetrackLane.FromAnim => "From anim",
        NotetrackLane.Fx => "FX",
        NotetrackLane.Sound => "Sound",
        _ => "Notes",
    };

    public override void Render(DrawingContext context)
    {
        if (Bounds.Width <= LabelWidth || Bounds.Height <= 0)
            return;
        var paint = _paint ??= new Paint(this);
        var size = Bounds.Size;
        var layout = CurrentLayout();
        // Hit-testable everywhere: the whole area scrubs.
        context.FillRectangle(Brushes.Transparent, new Rect(size));

        if (_focusVisible && IsFocused)
            context.DrawRectangle(paint.Focus, new Rect(1, 1, size.Width - 2, size.Height - 2), 4);

        DrawRuler(context, paint);

        var lanes = ShownLanes;
        for (var i = 0; i < lanes.Count; i++)
        {
            var top = layout.LaneTops[i];
            var height = LaneHeight(layout.Rows[i]);
            var current = _focusVisible && IsFocused && i == Math.Clamp(_keyLane, 0, lanes.Count - 1);
            var brush = current ? paint.Text : lanes[i] == NotetrackLane.FromAnim ? paint.Faint : paint.Dim;
            var name = Text(LaneName(lanes[i]), brush, small: false);
            context.DrawText(name, new Point(14, top + LanePad + (PillHeight - name.Height) / 2));
            if (current)
                context.FillRectangle(paint.Accent, new Rect(6, top + LanePad + 4, 2, PillHeight - 8), 1);
            // A recessed well with an edge, so the lanes read in the light theme too.
            var well = new Rect(TrackLeft, top, TrackWidth, height);
            context.FillRectangle(paint.Lane, well, 4);
            context.DrawRectangle(paint.LaneEdge, well.Deflate(0.5), 4);
        }

        // Where the playhead was parked when a drag began: the drag snaps to it.
        if (_dragFrame is not null && LastFrame > 0 && Math.Abs(_parked - _dragFrame.Value) > 0.5)
        {
            var x = Math.Round(XOf(_parked)) + 0.5;
            context.DrawLine(paint.Parked, new Point(x, Top + RulerHeight), new Point(x, size.Height - Bottom));
        }

        foreach (var placed in layout.Pills)
        {
            var marker = placed.Marker;
            var selected = marker.Key == SelectedKey;
            var dragging = _pressed is { } p && p.Key == marker.Key && _dragFrame is not null;
            using (dragging ? context.PushOpacity(0.45) : default(DrawingContext.PushedState?))
                DrawPill(context, paint, placed, placed.Pill, selected, marker == _hover && !dragging);
            if (dragging)
            {
                var ghost = placed.Pill.WithX(Math.Round(XOf(_dragFrame!.Value)));
                DrawPill(context, paint, placed with { PastEnd = false }, ghost, true, false);
            }
        }

        // Playhead over the lanes, its handle on the ruler.
        if (LastFrame > 0)
        {
            var head = Math.Round(XOf(Frame));
            context.FillRectangle(paint.Accent, new Rect(head - 1, Top + 2, 2, size.Height - Top - Bottom));
            context.FillRectangle(paint.Accent, new Rect(head - 6, Top, 12, 8), 2);
        }

        // The frame a drag would drop on, on the ruler above it (and what it snapped to).
        if (_dragFrame is { } drop)
        {
            var label = _snappedTo is { } to ? $"{drop.ToString(CultureInfo.InvariantCulture)} · {to}" : drop.ToString(CultureInfo.InvariantCulture);
            var text = Text(label, paint.OnAccent);
            var w = text.Width + 10;
            var x = Math.Clamp(Math.Round(XOf(drop)) - w / 2, 2, size.Width - w - 2);
            context.FillRectangle(paint.Accent, new Rect(x, Top - 2, w, RulerHeight), 3);
            context.DrawText(text, new Point(x + 5, Top - 2 + (RulerHeight - text.Height) / 2));
        }
    }

    private void DrawRuler(DrawingContext context, Paint paint)
    {
        if (LastFrame <= 0)
        {
            var none = Text("The anim isn't loaded: notes edit in the table below", paint.Faint);
            context.DrawText(none, new Point(TrackLeft, Top + (RulerHeight - none.Height) / 2));
            return;
        }
        // Labels at a round step that leaves room between them; the last frame always at the end.
        var widest = Text(LastFrame.ToString(CultureInfo.InvariantCulture), paint.Faint).Width + 24;
        var step = new[] { 1, 2, 5, 10, 20, 25, 50, 100, 200, 250, 500, 1000, 2000, 5000 }
            .FirstOrDefault(s => s * TrackWidth / LastFrame >= widest, 10_000);
        var last = Text(LastFrame.ToString(CultureInfo.InvariantCulture), paint.Faint);
        var lastX = TrackLeft + TrackWidth - last.Width;
        for (var f = 0; f < LastFrame; f += step)
        {
            var label = Text(f.ToString(CultureInfo.InvariantCulture), paint.Faint);
            var x = XOf(f);
            if (x + label.Width + 8 > lastX)
                break;
            context.DrawText(label, new Point(x, Top));
        }
        context.DrawText(last, new Point(lastX, Top));
    }

    private void DrawPill(DrawingContext context, Paint paint, Placed placed, Rect pill, bool selected, bool hover)
    {
        var m = placed.Marker;
        if (m.IsReadOnly)
        {
            if (selected || hover)
                context.FillRectangle(selected ? paint.Selected : paint.Pill, pill, 4);
            context.DrawRectangle(selected ? paint.SelectedPen : paint.PillEdge, pill.Deflate(0.5), 4);
        }
        else
        {
            context.FillRectangle(selected ? paint.Selected : hover ? paint.PillHover : paint.Pill, pill, 4);
            context.DrawRectangle(selected ? paint.SelectedPen : paint.PillEdge, pill.Deflate(0.5), 4);
            // The edge on the note's frame: accent, rose for a problem, faint for a sound that can't play here. A note
            // past the last frame carries it on its right, pointing off the end.
            var edge = m.Problem is not null ? paint.Danger : m.IsSilent ? paint.Faint : paint.Accent;
            context.FillRectangle(edge, new Rect(placed.PastEnd ? pill.Right - 2 : pill.X, pill.Y, 2, pill.Height), 1);
        }
        if (!placed.ShowText)
            return;
        var brush = m.Problem is not null ? paint.DangerText : m.IsReadOnly ? paint.Dim : paint.Text;
        var text = Text(Label(m, placed.PastEnd), brush);
        using (context.PushClip(pill.Deflate(new Thickness(6, 0, 4, 0))))
            context.DrawText(text, new Point(pill.X + 7, pill.Y + (pill.Height - text.Height) / 2));
    }

    private FormattedText Text(string s, IBrush brush, bool small = true)
    {
        if (_texts.TryGetValue((s, brush, small), out var cached))
            return cached;
        var font = this.TryFindResource("UiFont", ActualThemeVariant, out var f) && f is FontFamily family ? family : FontFamily.Default;
        var em = this.TryFindResource(small ? "FontSizeXs" : "FontSizeSm", ActualThemeVariant, out var e) && e is double d ? d : 11;
        var text = new FormattedText(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(font), em, brush);
        if (_texts.Count > 512)
            _texts.Clear();
        _texts[(s, brush, small)] = text;
        return text;
    }
}
