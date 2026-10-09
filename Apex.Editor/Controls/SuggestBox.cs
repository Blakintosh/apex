using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Apex.Editor.ViewModels;

namespace Apex.Editor.Controls;

/// <summary>
/// A text field with a list of suggestions (a FileCombo's files, a BoneCombo's bones), like an editable Windows combo
/// box: ▾ or Alt+↓ / F4 opens the list, ↑↓ walk it and put the highlighted value in the field, Enter commits it, Esc
/// closes; typing while it is open narrows it. A click on a suggestion commits it. Free text is always allowed, and the
/// field commits exactly like a plain text field (Enter or focus loss, through the editor's handlers), so the list never
/// writes a value the user didn't pick. The keyboard stays in the field: the list never takes focus. Bound to a
/// <see cref="SuggestPropertyViewModel"/> through its DataContext.
/// </summary>
public sealed class SuggestBox : Grid
{
    private const double MinWidthForArrow = 110;
    private static readonly TimeSpan LoadingDelay = TimeSpan.FromMilliseconds(150);

    private readonly TextBox _box;
    private readonly Button _arrow;
    private readonly Popup _popup;
    private readonly ListBox _list;
    private readonly TextBlock _note;
    private readonly DispatcherTimer _loadingTimer;

    private SuggestPropertyViewModel? _vm;
    private bool _wantOpen;
    private bool _syncing;

    public SuggestBox()
    {
        _box = new TextBox { IsUndoEnabled = false, Padding = new Thickness(8, 0, 22, 0) };
        _box.Classes.Add("pfield");
        _box[!TextBox.TextProperty] = new Binding(nameof(SuggestPropertyViewModel.Value)) { Mode = BindingMode.OneWay };
        // An empty value shows what it means (the default), as every field in the form does.
        _box[!TextBox.PlaceholderTextProperty] = new Binding(nameof(PropertyItemViewModel.Placeholder)) { Mode = BindingMode.OneWay };
        _box.PropertyChanged += Box_PropertyChanged;
        _box.GotFocus += (_, _) => Prefetch();

        _arrow = new Button
        {
            // The chevron every dropdown in Apex draws (a choice's, a reference's).
            Content = FieldGlyphs.DropDown(),
            Width = 20,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 0, 1, 0),
            // The keyboard stays in the field; a focusable button would also cancel its own click on focus loss.
            Focusable = false,
        };
        _arrow.Classes.Add("rowaction");
        Avalonia.Automation.AutomationProperties.SetName(_arrow, "Show suggestions");
        ToolTip.SetTip(_arrow, "Suggestions (Alt+↓)");
        _arrow.Click += (_, _) => Toggle();

        _list = new ListBox
        {
            MaxHeight = 240,
            Focusable = false,
            SelectionMode = SelectionMode.Single,
            Background = Avalonia.Media.Brushes.Transparent,
        };
        _list.Styles.Add(new Style(x => x.OfType<ListBoxItem>())
        {
            Setters =
            {
                new Setter(InputElement.FocusableProperty, false),
                new Setter(MinHeightProperty, 24.0),
                new Setter(TemplatedControl.PaddingProperty, new Thickness(8, 2)),
            },
        });
        _list.AddHandler(PointerReleasedEvent, List_PointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);

        _note = new TextBlock { Margin = new Thickness(10, 6), IsVisible = false };
        _note[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("TextDimBrush");
        _note[!TextBlock.FontSizeProperty] = new DynamicResourceExtension("FontSizeSm");

        var card = new Border
        {
            Padding = new Thickness(0, 4),
            BorderThickness = new Thickness(1),
            Child = new Panel { Children = { _list, _note } },
        };
        card[!Border.BackgroundProperty] = new DynamicResourceExtension("BgCardBrush");
        card[!Border.BorderBrushProperty] = new DynamicResourceExtension("LineBrush");
        card[!Border.CornerRadiusProperty] = new DynamicResourceExtension("RadiusOverlay");
        card[!Border.BoxShadowProperty] = new DynamicResourceExtension("ShadowLayer");
        card[!TextBlock.FontFamilyProperty] = new DynamicResourceExtension("MonoFont");
        card[!TextBlock.FontSizeProperty] = new DynamicResourceExtension("FontSizeSm");

        _popup = new Popup
        {
            PlacementTarget = this,
            Placement = PlacementMode.BottomEdgeAlignedLeft,
            IsLightDismissEnabled = true,
            Child = card,
        };
        _popup.Closed += (_, _) => _wantOpen = false;

        _loadingTimer = new DispatcherTimer { Interval = LoadingDelay };
        _loadingTimer.Tick += (_, _) =>
        {
            _loadingTimer.Stop();
            if (_wantOpen && !_popup.IsOpen)
                Show();
        };

        Children.Add(_box);
        Children.Add(_arrow);
        Children.Add(_popup);

        AddHandler(KeyDownEvent, Box_KeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        PointerEntered += (_, _) => Prefetch();
    }

    /// <summary>The text field, for Apex.Shots.</summary>
    public TextBox Box => _box;

    public Button Arrow => _arrow;

    public ListBox List => _list;

    public bool IsOpen => _popup.IsOpen;

    /// <summary>
    /// Whether a key from <paramref name="source"/> belongs to an open suggestion list (the form's ↑↓ row walk leaves
    /// those arrows to the list, as it does for an open dropdown).
    /// </summary>
    public static bool OwnsArrows(object? source) =>
        (source as Visual)?.FindAncestorOfType<SuggestBox>(includeSelf: true) is { IsOpen: true };

    // ── Binding to the row ───────────────────────────────────────────────────

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        // A recycled row: whatever list was open belonged to the previous property.
        Close();
        if (_vm is not null)
            _vm.PropertyChanged -= Vm_PropertyChanged;
        _vm = DataContext as SuggestPropertyViewModel;
        if (_vm is not null)
            _vm.PropertyChanged += Vm_PropertyChanged;
        UpdateArrow();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_vm is not null)
        {
            _vm.PropertyChanged -= Vm_PropertyChanged;
            _vm.PropertyChanged += Vm_PropertyChanged;
            UpdateArrow();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Close();
        // A row kept by its view model outlives this control: the control mustn't stay subscribed to it.
        if (_vm is not null)
            _vm.PropertyChanged -= Vm_PropertyChanged;
    }

    private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SuggestPropertyViewModel.CanSuggest):
                UpdateArrow();
                break;
            case nameof(SuggestPropertyViewModel.Suggestions) when _wantOpen:
                _loadingTimer.Stop();
                Show();
                break;
        }
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        UpdateArrow();
    }

    // In a narrow table cell the ▾ would crowd the value; Alt+↓ still opens the list there.
    private void UpdateArrow() =>
        _arrow.IsVisible = _vm is { CanSuggest: true } && Bounds.Width >= MinWidthForArrow;

    private void Prefetch() => _vm?.Refresh();

    // ── Opening and closing ──────────────────────────────────────────────────

    private void Toggle()
    {
        if (_wantOpen || _popup.IsOpen)
            Close();
        else
            Open();
    }

    /// <summary>Opens the list: at once with what is loaded, else when the load lands ("Loading…" after 150 ms).</summary>
    public void Open()
    {
        if (_vm is null || !Services.FieldFiles.HasInstall)
            return;
        _wantOpen = true;
        var loaded = _vm.Suggestions is not null;
        _ = _vm.LoadSuggestionsAsync(); // cached: refreshes a changed folder or model, usually within a frame
        if (_wantOpen && loaded && !_popup.IsOpen)
            Show();
        else if (!_popup.IsOpen)
            _loadingTimer.Start();
    }

    public void Close()
    {
        _wantOpen = false;
        _loadingTimer.Stop();
        if (_popup.IsOpen)
            _popup.IsOpen = false;
    }

    private void Show()
    {
        if (_vm is null)
            return;
        Fill(filter: null);
        if (_popup.Child is Control card)
            card.MinWidth = Math.Max(Bounds.Width, 180);
        _popup.IsOpen = true;
    }

    /// <summary>Lists the suggestions (those containing <paramref name="filter"/>, when given) and highlights the value.</summary>
    private void Fill(string? filter)
    {
        if (_vm is null)
            return;
        var all = _vm.Suggestions ?? Array.Empty<string>();
        IReadOnlyList<string> shown = string.IsNullOrEmpty(filter)
            ? all
            : all.Where(s => s.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
        _syncing = true;
        try
        {
            _list.ItemsSource = shown;
            var current = shown.FirstOrDefault(s => string.Equals(s, _box.Text, StringComparison.OrdinalIgnoreCase));
            _list.SelectedItem = current;
            if (current is not null)
                _list.ScrollIntoView(current);
        }
        finally
        {
            _syncing = false;
        }
        _list.IsVisible = shown.Count > 0;
        _note.IsVisible = shown.Count == 0;
        _note.Text = _vm.Suggestions is null ? "Loading…"
            : all.Count == 0 ? _vm.EmptyText
            : "No matches";
    }

    // ── Keyboard: the field keeps it ─────────────────────────────────────────

    private void Box_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Source != _box)
            return;
        var open = _popup.IsOpen;
        switch (e.Key)
        {
            case Key.Down when e.KeyModifiers == KeyModifiers.Alt:
            case Key.F4 when e.KeyModifiers == KeyModifiers.None:
                Toggle();
                e.Handled = true;
                break;
            case Key.Down or Key.Up when open && e.KeyModifiers == KeyModifiers.None:
                Step(e.Key == Key.Down ? 1 : -1);
                e.Handled = true;
                break;
            case Key.Enter when open:
                // The editor's own handler (it runs first) has committed the field; the list's job is done.
                Close();
                break;
            case Key.Escape when open:
                Close();
                e.Handled = true;
                break;
        }
    }

    /// <summary>Moves the highlight and puts it in the field, uncommitted (Enter commits, Esc reverts).</summary>
    private void Step(int delta)
    {
        if (_list.ItemsSource is not IReadOnlyList<string> { Count: > 0 } items)
            return;
        var index = _list.SelectedItem is string s ? IndexOf(items, s) : -1;
        index = index < 0 ? (delta > 0 ? 0 : items.Count - 1) : Math.Clamp(index + delta, 0, items.Count - 1);
        _syncing = true;
        try
        {
            _list.SelectedItem = items[index];
            _list.ScrollIntoView(items[index]);
            _box.Text = items[index];
            _box.CaretIndex = items[index].Length;
        }
        finally
        {
            _syncing = false;
        }
    }

    private static int IndexOf(IReadOnlyList<string> items, string value)
    {
        for (var i = 0; i < items.Count; i++)
            if (items[i] == value)
                return i;
        return -1;
    }

    private void Box_PropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        // Typing while the list is open narrows it; the value moving under it (undo) just re-highlights.
        if (e.Property != TextBox.TextProperty || _syncing || !_popup.IsOpen || _vm is null)
            return;
        var text = _box.Text ?? "";
        Fill(text == _vm.RawValue ? null : text);
    }

    // ── Pointer: a click on a suggestion commits it ──────────────────────────

    private void List_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left
            || (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true) is not { Content: string pick })
            return;
        Pick(pick);
        e.Handled = true;
    }

    private void Pick(string value)
    {
        if (_vm is null)
            return;
        Close();
        _box.Text = value;
        if (_vm.RawValue != value)
            _vm.RawValue = value;
        _box.CaretIndex = value.Length;
    }
}
