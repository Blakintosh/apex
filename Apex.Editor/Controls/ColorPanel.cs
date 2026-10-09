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
/// The colour picker a colour field's swatch opens: R, G, B (and A when the entry shows alpha) as scrub fields from 0 to
/// 1, and the colour as hex. Bound to a <see cref="ColorPropertyViewModel"/> through its DataContext; every change goes
/// through the row's value, so it undoes, saves and validates like typing would.
/// </summary>
public sealed class ColorPanel : Grid
{
    private const double LabelWidth = 32;

    private readonly TextBox _hex;
    private ColorPropertyViewModel? _vm;

    public ColorPanel()
    {
        Width = 200;
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
        var components = vm.Components;
        Fields = new ScrubNumberBox[components.Count];
        for (var i = 0; i < components.Count; i++)
        {
            RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            AddLabel(components[i].Label, i);
            var field = new ScrubNumberBox { DataContext = components[i] };
            Avalonia.Automation.AutomationProperties.SetName(field, components[i].Label);
            SetRow(field, i);
            SetColumn(field, 1);
            Children.Add(field);
            Fields[i] = field;
        }
        RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        AddLabel("Hex", components.Count);
        SetRow(_hex, components.Count);
        SetColumn(_hex, 1);
        Children.Add(_hex);
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
        // Opened from the swatch (the user asked for the picker): the keyboard starts on R.
        Dispatcher.UIThread.Post(() =>
        {
            if (Fields.Length > 0 && TopLevel.GetTopLevel(Fields[0]) is not null)
                Fields[0].Focus(NavigationMethod.Tab);
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
