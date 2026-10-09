using System;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Apex.Editor.ViewModels;

namespace Apex.Editor.Views;

public partial class CompareView : UserControl
{
    // Property column (196) and the take slot (24) per value column, plus the row padding.
    private const double PropertyColumn = 196 + 14 + 16;
    private const double TakeSlot = 24;
    private const double MinColumn = 200;
    private const double MaxColumn = 420;

    /// <summary>
    /// The value columns' width, for the rows' templates. Kept on the view, not read through its DataContext, so rows
    /// still being torn down when compare closes (DataContext already null) bind to a value instead of failing.
    /// </summary>
    public static readonly StyledProperty<double> ColumnWidthProperty =
        AvaloniaProperty.Register<CompareView, double>(nameof(ColumnWidth), 240);

    public double ColumnWidth
    {
        get => GetValue(ColumnWidthProperty);
        set => SetValue(ColumnWidthProperty, value);
    }

    private CompareViewModel? _vm;

    public CompareView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as CompareViewModel);
        SizeChanged += (_, _) => FitColumns();
        AddBox.AddHandler(KeyDownEvent, AddBox_KeyDown, RoutingStrategies.Tunnel);
        // Leaving the search for anything but its own list closes the list. Focus has not moved
        // yet when LostFocus fires, so the check waits for it to land.
        AddBox.LostFocus += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Avalonia.Visual;
            if (_vm is not null && focused != AddBox && focused?.FindAncestorOfType<ListBox>(includeSelf: true) != AddList)
                _vm.AddText = "";
        }, DispatcherPriority.Input);
        // A click on a match adds it and puts the keyboard back in the search for the next one.
        AddList.AddHandler(PointerReleasedEvent, (_, e) =>
        {
            if ((e.Source as Avalonia.Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext is Models.AssetRecord record)
            {
                _vm?.AddColumnCommand.Execute(record);
                AddBox.Focus();
            }
        }, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private void Attach(CompareViewModel? vm)
    {
        if (_vm is not null)
            _vm.Columns.CollectionChanged -= Columns_Changed;
        _vm = vm;
        if (_vm is not null)
        {
            _vm.Columns.CollectionChanged += Columns_Changed;
            ColumnWidth = _vm.ColumnWidth;
        }
        FitColumns();
    }

    private void Columns_Changed(object? sender, NotifyCollectionChangedEventArgs e) => FitColumns();

    /// <summary>The base and compared columns share the room; past a few columns they scroll.</summary>
    private void FitColumns()
    {
        if (_vm is null || Bounds.Width <= 0)
            return;
        var n = _vm.Columns.Count + 1;
        var width = (Bounds.Width - PropertyColumn) / n - TakeSlot;
        _vm.ColumnWidth = ColumnWidth = Math.Clamp(Math.Floor(width), MinColumn, MaxColumn);
    }

    private void AddBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm is null)
            return;
        switch (e.Key)
        {
            case Key.Down or Key.Up when _vm.HasAddMatches:
                _vm.MoveAddSelection(e.Key == Key.Down ? 1 : -1);
                e.Handled = true;
                break;
            case Key.Enter when _vm.HasAddMatches:
                _vm.AddColumnCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Escape when _vm.AddText.Length > 0:
                // Clears the search first; the next Esc closes compare.
                _vm.AddText = "";
                e.Handled = true;
                break;
        }
    }
}
