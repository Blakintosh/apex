using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Apex.Editor.ViewModels;

namespace Apex.Editor.Views;

public partial class StockSoundsView : UserControl
{
    public StockSoundsView()
    {
        InitializeComponent();
        // The list loads its next page when the scroll comes within a screen of the end.
        AddHandler(ScrollViewer.ScrollChangedEvent, OnScrolled, RoutingStrategies.Bubble);
    }

    private StockSoundsViewModel? Vm => DataContext as StockSoundsViewModel;

    private void OnScrolled(object? sender, ScrollChangedEventArgs e)
    {
        if (e.Source is not ScrollViewer sv || Vm is not { } vm || !IsInList(sv))
            return;
        if (sv.Extent.Height - (sv.Offset.Y + sv.Viewport.Height) < sv.Viewport.Height)
            vm.LoadMore();
    }

    private static bool IsInList(Visual v)
    {
        for (Visual? p = v; p is not null; p = p.GetVisualParent())
            if (p is ListBox { Name: "SoundsList" })
                return true;
        return false;
    }
}
