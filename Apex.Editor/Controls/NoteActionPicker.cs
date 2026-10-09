using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Apex.Editor.Services.Preview.Notetracks;
using Apex.Editor.ViewModels;

namespace Apex.Editor.Controls;

/// <summary>
/// A note's action, picked from xanim.awi's ~55 actions grouped the way their names group them (Sound, FX, Weapon,
/// Clip, Camera…), filtered as you type. The field shows the action; typing opens the list narrowed to actions (or
/// groups) containing every word typed, the best match highlighted. ↑ ↓ walk the list, Enter or Tab takes the
/// highlighted action, Esc puts the action back; ▾, Alt+↓ or F4 open the whole list. A click on an action takes it.
/// Only an action from the list is ever written (typed text that matches none is put back), and the keyboard stays in
/// the field: the list never takes focus. Bound to the note's action row (a <see cref="ChoicePropertyViewModel"/>)
/// through its DataContext.
/// </summary>
public sealed class NoteActionPicker : Grid
{
    private const double ItemHeight = 24, HeaderHeight = 22, ListMaxHeight = 320, MinListWidth = 240;

    private readonly TextBox _box;
    private readonly Button _arrow;
    private readonly Popup _popup;
    private readonly StackPanel _items;
    private readonly ScrollViewer _scroll;
    private readonly TextBlock _note;
    private readonly List<(Border Row, string Action)> _rows = new();
    private ChoicePropertyViewModel? _row;
    private int _highlight = -1;
    private bool _syncing;
    private bool _typed;

    public NoteActionPicker()
    {
        _box = new TextBox { IsUndoEnabled = false, Padding = new Thickness(8, 0, 26, 0) };
        _box.Classes.Add("pfield");
        Avalonia.Automation.AutomationProperties.SetName(_box, "Action");
        _box.AddHandler(KeyDownEvent, Box_KeyDown, RoutingStrategies.Tunnel);
        _box.PropertyChanged += Box_PropertyChanged;
        _box.GotFocus += (_, _) => Dispatcher_SelectAll();
        _box.LostFocus += (_, _) => CommitTyped(keepOpen: false);

        var chevron = new Path
        {
            Data = Geometry.Parse("M0,0 L4,4 L8,0"),
            StrokeThickness = 1.3,
            Width = 8,
            Height = 5,
            Stretch = Stretch.None,
        };
        _arrow = new Button
        {
            Content = chevron,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 0, 1, 0),
            // The keyboard stays in the field; a focusable button would also cancel its own click on focus loss.
            Focusable = false,
        };
        chevron[!Shape.StrokeProperty] = _arrow[!Button.ForegroundProperty];
        _arrow.Classes.Add("rowaction");
        Avalonia.Automation.AutomationProperties.SetName(_arrow, "Show actions");
        ToolTip.SetTip(_arrow, "Actions (Alt+↓)");
        _arrow.Click += (_, _) =>
        {
            if (_popup!.IsOpen)
                Close();
            else
            {
                _box.Focus(NavigationMethod.Pointer);
                Open(filter: null);
            }
        };

        _items = new StackPanel();
        _scroll = new ScrollViewer
        {
            Content = _items,
            MaxHeight = ListMaxHeight,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        _note = new TextBlock { Margin = new Thickness(10, 6), IsVisible = false, Text = "No action matches" };
        _note[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("TextDimBrush");

        var card = new Border
        {
            Padding = new Thickness(4),
            BorderThickness = new Thickness(1),
            Child = new Panel { Children = { _scroll, _note } },
        };
        card[!Border.BackgroundProperty] = new DynamicResourceExtension("BgCardBrush");
        card[!Border.BorderBrushProperty] = new DynamicResourceExtension("LineBrush");
        card[!Border.CornerRadiusProperty] = new DynamicResourceExtension("RadiusMd");
        card[!Border.BoxShadowProperty] = new DynamicResourceExtension("ShadowLayer");
        card[!TextBlock.FontFamilyProperty] = new DynamicResourceExtension("UiFont");
        card[!TextBlock.FontSizeProperty] = new DynamicResourceExtension("FontSizeSm");
        card.Styles.Add(new Style(x => x.OfType<Border>().Class("ntaction"))
        {
            Setters = { new Setter(Border.CornerRadiusProperty, new CornerRadius(3)), new Setter(Border.BackgroundProperty, Brushes.Transparent) },
        });
        card.Styles.Add(new Style(x => x.OfType<Border>().Class("ntaction").Class(":pointerover"))
        {
            Setters = { new Setter(Border.BackgroundProperty, new DynamicResourceExtension("BgHoverBrush")) },
        });
        card.Styles.Add(new Style(x => x.OfType<Border>().Class("ntaction").Class("hl"))
        {
            Setters = { new Setter(Border.BackgroundProperty, new DynamicResourceExtension("BgActiveBrush")) },
        });

        _popup = new Popup
        {
            PlacementTarget = this,
            Placement = PlacementMode.BottomEdgeAlignedLeft,
            IsLightDismissEnabled = true,
            Child = card,
        };
        _popup.Closed += (_, _) => _highlight = -1;

        Children.Add(_box);
        Children.Add(_arrow);
        Children.Add(_popup);
    }

    /// <summary>The text field, for Apex.Shots.</summary>
    public TextBox Box => _box;

    public Button Arrow => _arrow;

    public bool IsOpen => _popup.IsOpen;

    /// <summary>The actions listed right now, in order (Apex.Shots reads the grouping and the filter here).</summary>
    public IReadOnlyList<string> ListedActions => _rows.Select(r => r.Action).ToList();

    /// <summary>The group headers listed right now, in order.</summary>
    public IReadOnlyList<string> ListedGroups => _items.Children.OfType<TextBlock>().Select(t => t.Text ?? "").ToList();

    /// <summary>The highlighted action (Enter or Tab takes it), or null.</summary>
    public string? Highlighted => _highlight >= 0 && _highlight < _rows.Count ? _rows[_highlight].Action : null;

    // ── Binding to the row ───────────────────────────────────────────────────

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        Close();
        Follow(VisualRoot is not null ? DataContext as ChoicePropertyViewModel : null);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Follow(DataContext as ChoicePropertyViewModel);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Close();
        // Unhooked while out of the tree, so a note's action keeps no picker alive that it once had.
        Follow(null);
    }

    private void Follow(ChoicePropertyViewModel? row)
    {
        if (row == _row)
            return;
        if (_row is not null)
            _row.PropertyChanged -= Row_PropertyChanged;
        _row = row;
        if (_row is not null)
            _row.PropertyChanged += Row_PropertyChanged;
        ShowValue();
    }

    private void Row_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChoicePropertyViewModel.Value) && !_box.IsFocused)
            ShowValue();
    }

    private string Current => _row?.Value ?? "";

    private void ShowValue()
    {
        _syncing = true;
        try
        {
            _box.Text = Current;
        }
        finally
        {
            _syncing = false;
        }
        _typed = false;
    }

    private void Dispatcher_SelectAll() =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (_box.IsFocused && !_typed)
                _box.SelectAll();
        });

    // ── The list ─────────────────────────────────────────────────────────────

    /// <summary>Opens the list (every action with no filter, the current one highlighted), or narrows the open one.</summary>
    public void Open(string? filter)
    {
        if (_row is null)
            return;
        Fill(filter);
        if (!_popup.IsOpen)
        {
            // One width for as long as it is open: narrowing the list never resizes it under the pointer.
            if (_popup.Child is Border card)
                card.Width = Math.Max(Bounds.Width, MinListWidth);
            _popup.IsOpen = true;
            if (Highlighted is not null)
                Dispatcher.UIThread.Post(RevealHighlight, DispatcherPriority.Loaded);
            return;
        }
        if (Highlighted is not null)
            _rows[_highlight].Row.BringIntoView();
    }

    /// <summary>
    /// The list opens on the current action with a few above it for context, its top edge on a whole row: scrolled
    /// only as far as needed to bring the action in, it would open with half a line cut off at the top.
    /// </summary>
    private void RevealHighlight()
    {
        if (_highlight < 0 || _highlight >= _rows.Count)
            return;
        var top = _rows[Math.Max(0, _highlight - 3)].Row;
        if (top.TranslatePoint(new Point(0, 0), _items) is { } at)
            _scroll.Offset = new Vector(0, Math.Min(at.Y, Math.Max(0, _scroll.Extent.Height - _scroll.Viewport.Height)));
    }

    public void Close()
    {
        if (_popup.IsOpen)
            _popup.IsOpen = false;
    }

    private void Fill(string? filter)
    {
        _items.Children.Clear();
        _rows.Clear();
        _highlight = -1;
        var groups = NoteActions.Grouped(_row?.Choices ?? Array.Empty<string>(), filter);
        foreach (var (group, actions) in groups)
        {
            if (group is not null)
            {
                var header = new TextBlock
                {
                    Text = group,
                    Height = HeaderHeight,
                    Padding = new Thickness(8, 6, 8, 0),
                    FontSize = 11,
                    IsHitTestVisible = false,
                };
                header[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("TextFaintBrush");
                header[!TextBlock.FontSizeProperty] = new DynamicResourceExtension("FontSizeXs");
                _items.Children.Add(header);
            }
            foreach (var action in actions)
            {
                var label = new TextBlock
                {
                    Text = _row?.Def.ChoiceLabel(action) ?? action,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                };
                if (action == Current)
                    label.FontWeight = FontWeight.SemiBold;
                var row = new Border { Height = ItemHeight, Padding = new Thickness(8, 0), Child = label, Cursor = new Cursor(StandardCursorType.Hand) };
                row.Classes.Add("ntaction");
                var pick = action;
                row.PointerReleased += (_, e) =>
                {
                    if (e.InitialPressMouseButton != MouseButton.Left)
                        return;
                    e.Handled = true;
                    Pick(pick);
                };
                _items.Children.Add(row);
                _rows.Add((row, action));
            }
        }
        _note.IsVisible = _rows.Count == 0;
        _scroll.IsVisible = _rows.Count > 0;
        // Typed: the action that is exactly it, else the first that starts with it, else the first listed.
        // Whole list: the current action.
        var index = string.IsNullOrWhiteSpace(filter)
            ? _rows.FindIndex(r => r.Action == Current)
            : FirstOf(r => r.Action.Equals(filter.Trim(), StringComparison.OrdinalIgnoreCase),
                r => r.Action.StartsWith(filter.Trim(), StringComparison.OrdinalIgnoreCase), _ => true);
        SetHighlight(index);
    }

    private int FirstOf(params Predicate<(Border Row, string Action)>[] tests)
    {
        foreach (var test in tests)
            if (_rows.FindIndex(test) is var i and >= 0)
                return i;
        return -1;
    }

    private void SetHighlight(int index)
    {
        if (_highlight >= 0 && _highlight < _rows.Count)
            _rows[_highlight].Row.Classes.Remove("hl");
        _highlight = index;
        if (index >= 0 && index < _rows.Count)
        {
            _rows[index].Row.Classes.Add("hl");
            _rows[index].Row.BringIntoView();
        }
    }

    // ── Keyboard: the field keeps it ─────────────────────────────────────────

    private void Box_KeyDown(object? sender, KeyEventArgs e)
    {
        var open = _popup.IsOpen;
        switch (e.Key)
        {
            case Key.Down when e.KeyModifiers == KeyModifiers.Alt:
            case Key.F4 when e.KeyModifiers == KeyModifiers.None:
                if (open)
                    Close();
                else
                    Open(filter: null);
                e.Handled = true;
                break;
            case Key.Down or Key.Up when e.KeyModifiers == KeyModifiers.None:
                if (!open)
                    Open(_typed ? _box.Text : null);
                else if (_rows.Count > 0)
                    SetHighlight(_highlight < 0 ? (e.Key == Key.Down ? 0 : _rows.Count - 1)
                        : Math.Clamp(_highlight + (e.Key == Key.Down ? 1 : -1), 0, _rows.Count - 1));
                e.Handled = true;
                break;
            case Key.Enter when e.KeyModifiers == KeyModifiers.None:
                if (open && Highlighted is { } picked)
                    Pick(picked);
                else
                    CommitTyped(keepOpen: false);
                _box.SelectAll();
                e.Handled = true;
                break;
            case Key.Tab:
                // Tab takes what is highlighted only when the list was narrowed by typing (an exact name always counts);
                // the keyboard moves on either way.
                if (open && _typed && Highlighted is { } tabbed)
                    Pick(tabbed);
                else
                    CommitTyped(keepOpen: false);
                break;
            case Key.Escape:
                if (open || _box.Text != Current)
                {
                    Close();
                    ShowValue();
                    _box.SelectAll();
                    e.Handled = true;
                }
                break;
        }
    }

    private void Box_PropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != TextBox.TextProperty || _syncing || !_box.IsFocused)
            return;
        _typed = true;
        Open(_box.Text);
    }

    /// <summary>The field's text, if it names an action exactly (any case), becomes the action; anything else is put back.</summary>
    private void CommitTyped(bool keepOpen)
    {
        if (!keepOpen)
            Close();
        var text = (_box.Text ?? "").Trim();
        var match = _row?.Choices.FirstOrDefault(c => c.Equals(text, StringComparison.OrdinalIgnoreCase));
        if (match is not null && match != Current)
            Pick(match);
        else
            ShowValue();
    }

    private void Pick(string action)
    {
        Close();
        if (_row is not null && _row.Value != action)
            _row.Value = action;
        ShowValue();
    }
}
