using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Threading;
using Apex.Editor.ViewModels;

namespace Apex.Editor.Controls;

/// <summary>
/// The colour picker a colour field's swatch opens: a saturation × brightness square and a hue strip to pick by eye, an
/// alpha strip when the entry shows alpha, the colour as it was when the picker opened beside the colour now (a click on
/// the old one goes back), then R, G, B (and A) as scrub fields from 0 to 1 and the colour as hex. Bound to a
/// <see cref="ColorPropertyViewModel"/> through its DataContext; every change goes through the row's value, so it undoes,
/// saves and validates like typing would, and a drag is one undo step.
/// </summary>
public sealed class ColorPanel : Grid
{
    private const double LabelWidth = 32;

    private readonly TextBox _hex;
    private readonly ColorSquare _square = new() { Height = 148 };
    private readonly ColorStrip _hue = new();
    private readonly ColorStrip _alpha = new() { IsAlpha = true };
    private readonly Button _was = new();
    private readonly ColorSwatch _wasSwatch = new();
    private readonly ColorSwatch _nowSwatch = new();
    private ColorPropertyViewModel? _vm;
    // The picker's own hue and saturation: a grey has no hue and a black no saturation, so they stay where the user put
    // them instead of jumping as the value passes through one.
    private double _h, _s, _v;
    private string? _opened;
    private bool _writing;

    public ColorPanel()
    {
        Width = 232;
        RowSpacing = 6;
        ColumnDefinitions = new ColumnDefinitions($"{LabelWidth},*");
        _hex = new TextBox { IsUndoEnabled = false, MaxLength = 7 };
        _hex.Classes.Add("pfield");
        // Not the row: the editor's commit-on-Enter and Ctrl+S/Z hooks act on fields bound to a row, and this one
        // writes through CommitHex instead.
        _hex.DataContext = this;
        Avalonia.Automation.AutomationProperties.SetName(_hex, "Hex colour");
        _hex.KeyDown += Hex_KeyDown;
        _hex.LostFocus += (_, _) => CommitHex(revertIfInvalid: true);
        Avalonia.Automation.AutomationProperties.SetName(_square, "Saturation and brightness");
        Avalonia.Automation.AutomationProperties.SetName(_hue, "Hue");
        Avalonia.Automation.AutomationProperties.SetName(_alpha, "Alpha");
        ToolTip.SetTip(_square, "Drag to pick. Arrow keys nudge (Shift: further)");
        // Every pick is a continuous edit, as a scrub's are: a drag (or a run of picks) is one undo step.
        _square.Picked += (sat, val, _) =>
        {
            _s = sat;
            _v = val;
            WriteRgb();
        };
        _hue.Picked += (pos, _) =>
        {
            _h = pos * 360;
            _square.Hue = _h;
            WriteRgb();
        };
        _alpha.Picked += (pos, _) => Write(() => _vm?.SetAlpha(pos, continuous: true));
        _was.Classes.Add("colorwas");
        _was.Padding = new Thickness(0);
        _was.Content = _wasSwatch;
        _was.Cursor = new Cursor(StandardCursorType.Hand);
        ToolTip.SetTip(_was, "The colour when you opened this. Click to go back to it");
        Avalonia.Automation.AutomationProperties.SetName(_was, "Back to the colour before");
        _was.Click += (_, _) =>
        {
            if (_vm is not null && _opened is not null && _vm.Value != _opened)
                _vm.Value = _opened;
        };
        KeyDown += (_, e) =>
        {
            // Esc that no field claimed (to cancel its typing) closes the picker.
            if (e.Key == Key.Escape && this.FindLogicalAncestorOfType<Popup>() is { IsOpen: true } popup)
            {
                popup.IsOpen = false;
                e.Handled = true;
            }
        };
    }

    /// <summary>The scrub fields, for Apex.Shots.</summary>
    public ScrubNumberBox[] Fields { get; private set; } = Array.Empty<ScrubNumberBox>();

    /// <summary>The visual parts, for Apex.Shots.</summary>
    public ColorSquare Square => _square;
    public ColorStrip HueStrip => _hue;
    public ColorStrip AlphaStrip => _alpha;
    public Button WasButton => _was;

    public TextBox HexBox => _hex;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        var next = DataContext as ColorPropertyViewModel;
        if (next == _vm)
            return;
        // The row this picker belongs to moved on (a recycled row): a picker left open belonged to the previous one.
        if (_open && this.FindLogicalAncestorOfType<Popup>() is { IsOpen: true } popup)
            popup.IsOpen = false;
        Follow(null);
        _vm = next;
        // Fields built for the previous row would keep its view models alive; the next open builds them again.
        if (_builtFor is not null && _builtFor != next)
        {
            _builtFor = null;
            Children.Clear();
            RowDefinitions.Clear();
            Fields = Array.Empty<ScrubNumberBox>();
        }
    }

    private bool _open;
    private ColorPropertyViewModel? _following;

    /// <summary>Listens to the row only while the picker is open; closed pickers on scrolling rows cost nothing.</summary>
    private void Follow(ColorPropertyViewModel? vm)
    {
        if (_following is not null)
            _following.PropertyChanged -= Vm_PropertyChanged;
        _following = vm;
        if (vm is not null)
            vm.PropertyChanged += Vm_PropertyChanged;
    }

    private ColorPropertyViewModel? _builtFor;

    private void Build(ColorPropertyViewModel vm)
    {
        _builtFor = vm;
        Children.Clear();
        RowDefinitions.Clear();
        var row = 0;
        void Whole(Control control, double gapAfter = 0)
        {
            RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            control.Margin = new Thickness(0, 0, 0, gapAfter);
            SetRow(control, row++);
            SetColumn(control, 0);
            SetColumnSpan(control, 2);
            Children.Add(control);
        }
        Whole(_square, 4);
        Whole(_hue, 4);
        if (vm.ShowAlpha)
            Whole(_alpha, 4);
        // Was | now: the colour the picker opened on beside the colour it has made.
        // Built again for another row: the previous build's grid still holds them.
        (_was.Parent as Panel)?.Children.Remove(_was);
        (_nowSwatch.Parent as Panel)?.Children.Remove(_nowSwatch);
        var compare = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), Height = 20 };
        _was.HorizontalAlignment = HorizontalAlignment.Stretch;
        _was.VerticalAlignment = VerticalAlignment.Stretch;
        _was.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        _was.VerticalContentAlignment = VerticalAlignment.Stretch;
        _was.MinHeight = 0;
        compare.Children.Add(_was);
        SetColumn(_nowSwatch, 1);
        compare.Children.Add(_nowSwatch);
        ToolTip.SetTip(_nowSwatch, "The colour now");
        Whole(compare, 4);

        var components = vm.Components;
        Fields = new ScrubNumberBox[components.Count];
        for (var i = 0; i < components.Count; i++)
        {
            RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            AddLabel(components[i].Label, row);
            var field = new ScrubNumberBox { DataContext = components[i] };
            Avalonia.Automation.AutomationProperties.SetName(field, components[i].Label);
            SetRow(field, row++);
            SetColumn(field, 1);
            Children.Add(field);
            Fields[i] = field;
        }
        RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        AddLabel("Hex", row);
        SetRow(_hex, row);
        SetColumn(_hex, 1);
        Children.Add(_hex);
    }

    /// <summary>The square and strips show the value (not while the picker itself is writing it).</summary>
    private void SyncFromValue()
    {
        if (_vm is null)
            return;
        var (r, g, b, a) = _vm.Rgba;
        if (!_writing)
        {
            (_h, _s, _v) = ColorMath.ToHsv(r, g, b, _h, _s);
            _square.Hue = _h;
            _square.Saturation = _s;
            _square.Brightness = _v;
            _hue.Position = _h / 360;
            _alpha.Position = a;
        }
        _alpha.Tint = ColorMath.ToColor(r, g, b);
        _nowSwatch.Color = _vm.Swatch;
    }

    private void WriteRgb()
    {
        var (r, g, b) = ColorMath.FromHsv(_h, _s, _v);
        Write(() => _vm?.SetRgb(r, g, b, continuous: true));
    }

    private void Write(Action write)
    {
        _writing = true;
        try
        {
            write();
        }
        finally
        {
            _writing = false;
        }
        SyncFromValue();
    }

    private void AddLabel(string text, int row)
    {
        var label = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
        label[!TextBlock.FontSizeProperty] = new DynamicResourceExtension("FontSizeSm");
        label[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("TextDimBrush");
        SetRow(label, row);
        Children.Add(label);
    }

    private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ColorPropertyViewModel.Hex) && !_hex.IsKeyboardFocusWithin)
            _hex.Text = _vm?.Hex;
        if (e.PropertyName == nameof(ColorPropertyViewModel.Value))
            SyncFromValue();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _open = true;
        if (_vm is null)
            return;
        // Built when first opened, and again only for another row: reopening on the same row builds nothing.
        if (_vm != _builtFor)
            Build(_vm);
        Follow(_vm);
        _hex.Text = _vm.Hex;
        _opened = _vm.Value;
        _wasSwatch.Color = _vm.Swatch;
        SyncFromValue();
        // Opened from the swatch (the user asked for the picker): the keyboard starts on the square (arrows move the
        // colour; Tab goes on to hue, R, G, B and hex).
        Dispatcher.UIThread.Post(() =>
        {
            if (TopLevel.GetTopLevel(_square) is not null)
                _square.Focus(NavigationMethod.Tab);
        }, DispatcherPriority.Loaded);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _open = false;
        Follow(null);
    }

    private void Hex_KeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                CommitHex(revertIfInvalid: false);
                _hex.SelectAll();
                e.Handled = true;
                break;
            case Key.Escape when _vm is not null && _hex.Text != _vm.Hex:
                _hex.Text = _vm.Hex;
                e.Handled = true;
                break;
        }
    }

    private void CommitHex(bool revertIfInvalid)
    {
        if (_vm is null)
            return;
        var text = _hex.Text ?? "";
        var ok = _vm.CommitHex(text);
        _hex.Classes.Set("invalid", !ok && !revertIfInvalid);
        ToolTip.SetTip(_hex, !ok && !revertIfInvalid ? "Not a colour. Enter six hex digits, like #0A26B4." : null);
        if (ok || revertIfInvalid)
            _hex.Text = _vm.Hex;
    }
}
