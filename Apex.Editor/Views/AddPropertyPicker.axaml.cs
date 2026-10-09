using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Apex.Editor.ViewModels;

namespace Apex.Editor.Views;

/// <summary>The Set view's Add property list (see the XAML).</summary>
public partial class AddPropertyPicker : UserControl
{
    /// <summary>Raised when a property is chosen, before it is revealed: the host closes the list.</summary>
    public event Action? Picked;

    public AddPropertyPicker()
    {
        InitializeComponent();
        Filter.TextChanged += (_, _) => Rebuild();
        Filter.AddHandler(KeyDownEvent, Filter_KeyDown, RoutingStrategies.Tunnel);
        List.PointerReleased += List_PointerReleased;
    }

    private AssetEditorViewModel? Vm => DataContext as AssetEditorViewModel;

    /// <summary>A fresh list and an empty box each time it opens.</summary>
    public void Reset()
    {
        Filter.Text = "";
        Rebuild();
        Dispatcher.UIThread.Post(() => Filter.Focus(), DispatcherPriority.Loaded);
    }

    private void Rebuild()
    {
        var query = Filter.Text?.Trim() ?? "";
        var rows = Vm?.UnsetRows(query) ?? [];
        List.ItemsSource = rows;
        List.SelectedIndex = rows.Count > 0 ? 0 : -1;
        Empty.IsVisible = rows.Count == 0;
        Empty.Text = query.Length > 0 ? "No property without a value matches." : "Every property already has a value.";
    }

    private void Filter_KeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
            case Key.Up:
                if (List.ItemCount > 0)
                    List.SelectedIndex = Math.Clamp(List.SelectedIndex + (e.Key == Key.Down ? 1 : -1), 0, List.ItemCount - 1);
                if (List.SelectedItem is { } item)
                    List.ScrollIntoView(item);
                e.Handled = true;
                break;
            case Key.Enter:
                if (List.SelectedItem is PropertyPick pick)
                    Pick(pick);
                e.Handled = true;
                break;
        }
    }

    private void List_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton == MouseButton.Left && (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>() is { DataContext: PropertyPick pick })
            Pick(pick);
    }

    private void Pick(PropertyPick pick)
    {
        var vm = Vm;
        Picked?.Invoke();
        // After the list has closed, so the focus it hands back doesn't land over the row's.
        Dispatcher.UIThread.Post(() => vm?.RevealProperty(pick.Item.Key), DispatcherPriority.Background);
    }
}
