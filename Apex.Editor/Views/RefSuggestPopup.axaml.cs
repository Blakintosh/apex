using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.ComponentModel;
using Apex.Editor.Models;
using Apex.Editor.ViewModels;

namespace Apex.Editor.Views;

/// <summary>One line of the suggestion list: an asset's name split around the typed part, and its GDT.</summary>
public sealed partial class RefSuggestLine : ObservableObject
{
    [ObservableProperty] private AssetRecord? _record;
    [ObservableProperty] private string _glyph = "";
    [ObservableProperty] private IBrush? _glyphBrush;
    [ObservableProperty] private string _prefix = "";
    [ObservableProperty] private string _hit = "";
    [ObservableProperty] private string _postfix = "";
    [ObservableProperty] private string _gdt = "";
    [ObservableProperty] private bool _isShown;

    public void Show(AssetRecord? record, string query)
    {
        Record = record;
        IsShown = record is not null;
        if (record is null)
            return;
        Glyph = TypeStyles.Glyph(record.Type);
        GlyphBrush = TypeStyles.Brush(record.Type);
        Gdt = MainViewModel.ShortGdt(record.GdtName);
        var name = record.Name;
        var at = query.Length == 0 ? -1 : name.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (at < 0)
        {
            Prefix = name;
            Hit = "";
            Postfix = "";
            return;
        }
        Prefix = name[..at];
        Hit = name.Substring(at, query.Length);
        Postfix = name[(at + query.Length)..];
    }
}

/// <summary>
/// The suggestion list a <see cref="RefField"/> opens: ten lines windowed over any number of results, a highlight the
/// field moves with the keyboard, and a click that picks. It never takes the keyboard.
/// </summary>
public partial class RefSuggestPopup : UserControl
{
    public const int VisibleLines = 10;

    private readonly RefSuggestLine[] _lines = new RefSuggestLine[VisibleLines];
    private IReadOnlyList<AssetRecord> _items = Array.Empty<AssetRecord>();
    private string _query = "";
    private int _top;
    // Wheel movement not yet worth a whole line (precision wheels, touchpads).
    private double _wheel;
    private int _selected = -1;
    private bool _scrolling;

    public RefSuggestPopup()
    {
        InitializeComponent();
        for (var i = 0; i < _lines.Length; i++)
            _lines[i] = new RefSuggestLine();
        Lines.ItemsSource = _lines;
        // A press anywhere in the list is the list's own: it neither selects nor moves focus out of the field.
        Lines.AddHandler(PointerPressedEvent, (_, e) => e.Handled = true, RoutingStrategies.Tunnel, handledEventsToo: true);
        Lines.AddHandler(PointerReleasedEvent, Lines_PointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        Card.AddHandler(PointerWheelChangedEvent, Card_Wheel, RoutingStrategies.Tunnel, handledEventsToo: true);
        Scroll.ValueChanged += (_, e) =>
        {
            if (_scrolling)
                return;
            _top = (int)Math.Round(e.NewValue);
            Render();
        };
    }

    /// <summary>A line was clicked.</summary>
    public event Action<AssetRecord>? Picked;

    /// <summary>What the list holds (every result, not just the ten on show).</summary>
    public IReadOnlyList<AssetRecord> Items => _items;

    /// <summary>The highlighted result's index in <see cref="Items"/>, or -1 when nothing is highlighted.</summary>
    public int SelectedIndex => _selected;

    public AssetRecord? Selected => _selected >= 0 && _selected < _items.Count ? _items[_selected] : null;

    /// <summary>The ten lines, for checks that want to see what each one shows.</summary>
    public IReadOnlyList<RefSuggestLine> ShownLines => _lines;

    /// <summary>The first result on show.</summary>
    public int Top => _top;

    /// <summary>Shows <paramref name="items"/> with <paramref name="query"/> marked in each name; <paramref name="selected"/> is highlighted and in view.</summary>
    public void ShowResults(IReadOnlyList<AssetRecord> items, string query, int selected, int anchor = 0)
    {
        _items = items;
        _query = query;
        _selected = selected < items.Count ? selected : -1;
        _top = 0;
        Message.IsVisible = false;
        Results.IsVisible = true;
        Reveal(_selected >= 0 ? _selected : anchor, centre: true);
        Render();
    }

    /// <summary>One line of plain copy in place of results (nothing matched, or still loading).</summary>
    public void ShowMessage(string text)
    {
        _items = Array.Empty<AssetRecord>();
        _selected = -1;
        _top = 0;
        Message.Text = text;
        Message.IsVisible = true;
        Results.IsVisible = false;
        Render();
    }

    /// <summary>Moves the highlight by <paramref name="delta"/>; above the first result nothing is highlighted.</summary>
    public void Move(int delta)
    {
        if (_items.Count == 0)
            return;
        _selected = Math.Clamp(_selected + delta, -1, _items.Count - 1);
        if (_selected >= 0)
            Reveal(_selected, centre: false);
        Render();
    }

    private void Reveal(int index, bool centre)
    {
        var maxTop = Math.Max(0, _items.Count - VisibleLines);
        if (centre)
            _top = index - VisibleLines / 2;
        else if (index < _top)
            _top = index;
        else if (index >= _top + VisibleLines)
            _top = index - VisibleLines + 1;
        _top = Math.Clamp(_top, 0, maxTop);
    }

    private void Render()
    {
        for (var i = 0; i < _lines.Length; i++)
            _lines[i].Show(_top + i < _items.Count ? _items[_top + i] : null, _query);
        var line = _selected - _top;
        Lines.SelectedIndex = line >= 0 && line < VisibleLines ? line : -1;

        _scrolling = true;
        var overflow = _items.Count > VisibleLines;
        Scroll.IsVisible = overflow;
        if (overflow)
        {
            Scroll.Maximum = _items.Count - VisibleLines;
            Scroll.ViewportSize = VisibleLines;
            Scroll.SmallChange = 1;
            Scroll.LargeChange = VisibleLines;
            Scroll.Value = _top;
        }
        _scrolling = false;
    }

    private void Card_Wheel(object? sender, PointerWheelEventArgs e)
    {
        if (_items.Count <= VisibleLines)
            return;
        // Precision wheels and touchpads send fractions of a notch: they add up until they make a whole line.
        _wheel += e.Delta.Y * 3;
        var lines = (int)_wheel;
        if (lines == 0)
        {
            e.Handled = true;
            return;
        }
        _wheel -= lines;
        _top = Math.Clamp(_top - lines, 0, _items.Count - VisibleLines);
        Render();
        e.Handled = true;
    }

    private void Lines_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left)
            return;
        if ((e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext is RefSuggestLine { Record: { } record })
        {
            e.Handled = true;
            Picked?.Invoke(record);
        }
    }
}
