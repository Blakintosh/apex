using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Apex.Editor.ViewModels;

namespace Apex.Editor.Controls;

/// <summary>
/// Number editor for property rows: drag the value left/right to scrub (Shift ×10, Alt ×0.1), click
/// to type, ↑↓ to step, Enter commits and Esc cancels. Replaces the stacked-spinner NumericUpDown
/// plus a separate 150 px slider: on hover or focus − + step buttons appear at the right end (the
/// schema range is in the tooltip and the Inspector). Consecutive scrub and step edits coalesce into
/// one undo step in the record's history. Classes for styling: "focused" (focus within), "editing"
/// (typing), "invalid" (typed text isn't a number); the field's edge and focus ring are
/// <see cref="EdgeProperty"/>, drawn above everything inside the box.
/// Bound to a <see cref="NumberPropertyViewModel"/> through its DataContext.
/// </summary>
public sealed class ScrubNumberBox : Border
{
    private const double PixelsPerStep = 4;
    private const double MinWidthForSteps = 120;

    private readonly TextBlock _value;
    private readonly Button _left;
    private readonly Button _right;
    private readonly StackPanel _steps;
    private readonly Border _edge;
    private readonly TextBox _editor;

    private NumberPropertyViewModel? _vm;
    private bool _committing;
    private bool _pressed;
    private bool _dragging;
    private Point _pressPoint;
    private double _lastX;
    private double _carry;

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<ScrubNumberBox>();

    public static readonly StyledProperty<FontFamily> FontFamilyProperty =
        TextElement.FontFamilyProperty.AddOwner<ScrubNumberBox>();

    public static readonly StyledProperty<double> FontSizeProperty =
        TextElement.FontSizeProperty.AddOwner<ScrubNumberBox>();

    /// <summary>
    /// The field's hairline edge and focus ring (inset shadows). They are drawn by a border laid over the box's content,
    /// not as the box's own shadow: the step buttons' opaque panel painted over that, so the edge and the ring stopped
    /// short of the right end whenever the steps showed.
    /// </summary>
    public static readonly StyledProperty<BoxShadows> EdgeProperty =
        AvaloniaProperty.Register<ScrubNumberBox, BoxShadows>(nameof(Edge));

    public BoxShadows Edge
    {
        get => GetValue(EdgeProperty);
        set => SetValue(EdgeProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public FontFamily FontFamily
    {
        get => GetValue(FontFamilyProperty);
        set => SetValue(FontFamilyProperty, value);
    }

    public double FontSize
    {
        get => GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    public ScrubNumberBox()
    {
        CornerRadius = new CornerRadius(4);
        ClipToBounds = true;
        Focusable = true;
        IsTabStop = true;
        Cursor = new Cursor(StandardCursorType.SizeWestEast);

        _value = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };

        _left = MakeStep("−", -1, "Step down");
        _right = MakeStep("+", 1, "Step up");
        // Both arrows sit at the right end over the field's own fill, so showing them on hover
        // neither shifts the number nor covers its first digits.
        _steps = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { _left, _right },
        };
        _steps[!Panel.BackgroundProperty] = this[!BackgroundProperty];

        _edge = new Border { IsHitTestVisible = false };
        _edge[!Border.CornerRadiusProperty] = this[!CornerRadiusProperty];
        _edge[!Border.BoxShadowProperty] = this[!EdgeProperty];

        _editor = new TextBox
        {
            IsVisible = false,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Padding = new Thickness(6, 0),
            MinHeight = 0,
            VerticalContentAlignment = VerticalAlignment.Center,
            // Ctrl+Z belongs to the editor's history, not the TextBox's own per-keystroke undo.
            IsUndoEnabled = false,
        };
        _editor.Classes.Add("scrubedit");
        _editor.KeyDown += Editor_KeyDown;
        _editor.LostFocus += (_, _) => EndEdit(commit: true);

        Child = new Panel { Children = { _value, _steps, _editor, _edge } };

        _value[!TextBlock.ForegroundProperty] = this[!ForegroundProperty];
        _value[!TextBlock.FontFamilyProperty] = this[!FontFamilyProperty];
        _value[!TextBlock.FontSizeProperty] = this[!FontSizeProperty];
        _editor[!TextBox.FontFamilyProperty] = this[!FontFamilyProperty];
        _editor[!TextBox.FontSizeProperty] = this[!FontSizeProperty];
        _editor[!TextBox.ForegroundProperty] = this[!ForegroundProperty];
        // Typing keeps the field's name: a screen reader says "Pitch row 2" in the box as on the field.
        _editor[!AutomationProperties.NameProperty] = this[!AutomationProperties.NameProperty];
    }

    private Button MakeStep(string glyph, int direction, string name)
    {
        // A row action (24 × 24, the shared hover and press) over the field's 24 px height: the minimum hit area. The
        // glyph is in the UI face, whose − and + share one width and one centre line.
        var b = new Button
        {
            Content = glyph,
            MinWidth = 0,
            Focusable = false,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        b[!Button.FontFamilyProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("UiFont");
        b.Classes.Add("rowaction");
        b.Classes.Add("live");
        b.Classes.Add("scrubstep");
        AutomationProperties.SetName(b, name);
        b.Click += (_, _) =>
        {
            Step(direction);
            Focus();
        };
        return b;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        // A recycled row: whatever was typed belonged to the previous property.
        EndEdit(commit: false);
        Listen(false);
        _vm = DataContext as NumberPropertyViewModel;
        Listen(VisualRoot is not null);
        Refresh();
    }

    // The property is only listened to while the box is on screen: a view model outlives a discarded editor view, and
    // its handler would keep the whole view (and every tab its recycled rows last showed) alive.
    private bool _listening;

    private void Listen(bool on)
    {
        if (on == _listening || _vm is null && on)
            return;
        if (_vm is not null)
        {
            if (on)
                _vm.PropertyChanged += Vm_PropertyChanged;
            else
                _vm.PropertyChanged -= Vm_PropertyChanged;
        }
        _listening = on;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Listen(true);
        // The value may have moved while the box was away.
        Refresh();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Listen(false);
    }

    private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(NumberPropertyViewModel.DisplayText) or nameof(NumberPropertyViewModel.Value)))
            return;
        // The value moved under an open field (undo, another view): the typed text is stale.
        if (e.PropertyName == nameof(NumberPropertyViewModel.Value) && _editor.IsVisible && !_committing)
            EndEdit(commit: false);
        Refresh();
    }

    private void Refresh()
    {
        if (_vm is null)
        {
            _value.Text = "";
            return;
        }
        _value.Text = _vm.DisplayText;
        ToolTip.SetTip(this, _vm.HasRange
            ? $"Drag to scrub (Shift ×10, Alt ×0.1) · click to type · Ctrl+↑↓ step\nRange {_vm.Def.Min:0.###} – {_vm.Def.Max:0.###}"
            : "Drag to scrub (Shift ×10, Alt ×0.1) · click to type · Ctrl+↑↓ step");
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        // In a narrow box (a table cell) the arrows would cover the number, and a click meant to
        // type would step it instead; scrubbing and Ctrl+↑↓ still work there.
        _steps.IsVisible = e.NewSize.Width >= MinWidthForSteps;
    }

    private static double ModifierScale(KeyModifiers mods)
    {
        if (mods.HasFlag(KeyModifiers.Shift))
            return 10;
        if (mods.HasFlag(KeyModifiers.Alt))
            return 0.1;
        return 1;
    }

    private void Step(double steps)
    {
        if (_vm is null || !IsEffectivelyEnabled)
            return;
        _vm.Nudge(steps);
    }

    // ── Pointer: scrub or click-to-type ──────────────────────────────────────

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_editor.IsVisible || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        if ((e.Source as Visual)?.FindAncestorOfType<Button>(includeSelf: true) is { } step && (step == _left || step == _right))
            return;
        _pressed = true;
        _dragging = false;
        _pressPoint = e.GetPosition(this);
        _lastX = _pressPoint.X;
        _carry = 0;
        e.Pointer.Capture(this);
        Focus();
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_pressed)
            return;
        var p = e.GetPosition(this);
        if (!_dragging && Math.Abs(p.X - _pressPoint.X) < 3)
            return;
        _dragging = true;
        var dx = p.X - _lastX;
        _lastX = p.X;
        _carry += dx / PixelsPerStep * ModifierScale(e.KeyModifiers);
        // Fine (Alt) scrubbing moves in tenths of a step (whole steps, just slower, for integers).
        var quantum = e.KeyModifiers.HasFlag(KeyModifiers.Alt) && _vm is { Def.IsInteger: false } ? 0.1 : 1;
        var whole = Math.Truncate(_carry / quantum) * quantum;
        if (whole != 0)
        {
            Step(whole);
            _carry -= whole;
        }
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_pressed)
            return;
        _pressed = false;
        e.Pointer.Capture(null);
        if (!_dragging)
            BeginEdit();
        _dragging = false;
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _pressed = false;
        _dragging = false;
    }

    // ── Keyboard ─────────────────────────────────────────────────────────────

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || _editor.IsVisible)
            return;
        // Plain ↑↓ walk the form's rows (the editor handles them); Ctrl+↑↓ steps the value, and
        // while typing ↑↓ step too (see Editor_KeyDown).
        switch (e.Key)
        {
            case Key.Up when e.KeyModifiers.HasFlag(KeyModifiers.Control):
                Step(ModifierScale(e.KeyModifiers & ~KeyModifiers.Control));
                e.Handled = true;
                break;
            case Key.Down when e.KeyModifiers.HasFlag(KeyModifiers.Control):
                Step(-ModifierScale(e.KeyModifiers & ~KeyModifiers.Control));
                e.Handled = true;
                break;
            case Key.Enter or Key.F2:
                BeginEdit();
                e.Handled = true;
                break;
            case >= Key.D0 and <= Key.D9 or >= Key.NumPad0 and <= Key.NumPad9 or Key.OemMinus or Key.Subtract
                    or Key.OemPeriod or Key.Decimal
                when e.KeyModifiers == KeyModifiers.None:
                // Typing a digit (or "-", or "." for ".5") starts editing with it, like a spreadsheet cell.
                BeginEdit(replaceWith: e.Key is Key.Decimal ? "." : e.KeySymbol);
                e.Handled = true;
                break;
        }
    }

    private void BeginEdit(string? replaceWith = null)
    {
        if (_vm is null || !IsEffectivelyEnabled)
            return;
        SetInvalid(false);
        _editor.Text = replaceWith ?? _vm.RawValue;
        _editor.IsVisible = true;
        Classes.Set("editing", true);
        _value.Opacity = 0;
        _editor.Focus();
        if (replaceWith is null)
            _editor.SelectAll();
        else
            _editor.CaretIndex = _editor.Text?.Length ?? 0;
    }

    /// <summary>
    /// Closes the text editor. Returns false, leaving it open and tinted, when the typed text isn't
    /// a number: dropping it silently would lose what the user typed.
    /// </summary>
    private bool EndEdit(bool commit)
    {
        if (!_editor.IsVisible)
            return true;
        var text = (_editor.Text ?? "").Trim();
        _committing = true;
        var ok = !commit || _vm is null || text.Length == 0 || text == _vm.RawValue || _vm.TryCommitText(text);
        _committing = false;
        if (!ok)
        {
            SetInvalid(true);
            return false;
        }
        SetInvalid(false);
        _editor.IsVisible = false;
        Classes.Set("editing", false);
        _value.Opacity = 1;
        Refresh();
        return true;
    }

    /// <summary>Problem tint on typed text that isn't a number; ".invalid" is the style hook.</summary>
    private void SetInvalid(bool invalid)
    {
        if (Classes.Contains("invalid") == invalid)
            return;
        Classes.Set("invalid", invalid);
        if (invalid)
        {
            _editor[!TextBox.BorderBrushProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("DangerBrush");
            _editor.BorderThickness = new Thickness(1);
            ToolTip.SetTip(_editor, "Not a number. Enter a number, or press Esc to keep the old value.");
        }
        else
        {
            _editor.ClearValue(TextBox.BorderBrushProperty);
            _editor.BorderThickness = new Thickness(0);
            ToolTip.SetTip(_editor, null);
        }
    }

    private void Editor_KeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                if (EndEdit(commit: true))
                    Focus();
                e.Handled = true;
                break;
            case Key.Escape:
                EndEdit(commit: false);
                Focus();
                e.Handled = true;
                break;
            case Key.Up:
            case Key.Down:
                if (EndEdit(commit: true))
                {
                    Step(e.Key == Key.Up ? ModifierScale(e.KeyModifiers) : -ModifierScale(e.KeyModifiers));
                    BeginEdit();
                }
                e.Handled = true;
                break;
        }
    }

    /// <summary>
    /// Undo while typing: closes the text editor without committing, so its stale text can't be
    /// written back over the undone value. Returns true when there was typing to throw away (the
    /// undo stops there), false when there was none (the undo goes on to the history).
    /// </summary>
    public bool CancelTyping()
    {
        if (!_editor.IsVisible)
            return false;
        var typed = _vm is not null && (_editor.Text ?? "").Trim() != _vm.RawValue;
        EndEdit(commit: false);
        Focus();
        return typed;
    }

    /// <summary>Save while typing: commits the typed number (text that isn't a number stays open and tinted, as on Enter).</summary>
    public void CommitTyping()
    {
        if (_editor.IsVisible)
            EndEdit(commit: true);
    }

    // ── Automation ──────────────────────────────────────────────────────────
    // A Border has no peer of its own, so without this a screen reader (or UI Automation) met only the bare number,
    // nameless: the field reads as its name ("Pitch row 2") with its value, and a value set through it commits as typing.

    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    private sealed class Peer(ScrubNumberBox owner) : ControlAutomationPeer(owner), IValueProvider
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Edit;

        protected override bool IsContentElementCore() => true;

        protected override bool IsControlElementCore() => true;

        public bool IsReadOnly => owner._vm is null || !owner.IsEffectivelyEnabled;

        public string? Value => owner._vm?.RawValue;

        public void SetValue(string? value)
        {
            if (IsReadOnly)
                throw new InvalidOperationException("This field can't be changed.");
            owner.EndEdit(commit: false);
            var text = (value ?? "").Trim();
            if (text != owner._vm!.RawValue && !owner._vm.TryCommitText(text))
                throw new ArgumentException($"'{text}' isn't a number.", nameof(value));
        }
    }

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        Classes.Set("focused", true);
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        Classes.Set("focused", IsKeyboardFocusWithin);
    }
}
