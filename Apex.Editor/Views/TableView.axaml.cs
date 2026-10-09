using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Apex.Editor.ViewModels;

namespace Apex.Editor.Views;

public partial class TableView : UserControl
{
    public TableView()
    {
        InitializeComponent();
    }

    /// <summary>The editors cells borrow while in use (see <see cref="TableCellValue"/>).</summary>
    public CellEditorPool CellEditors { get; } = new();

    /// <summary>
    /// The view outlives its tables: a new table starts at its first row and column, not wherever the last one was
    /// scrolled to (which also realized the last table's bottom rows for the new one first).
    /// </summary>
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is not TableViewModel)
            return;
        // The sideways scroller around the grid, and the rows' own.
        if (Rows.FindAncestorOfType<ScrollViewer>() is { } sideways && this.IsVisualAncestorOf(sideways))
            sideways.Offset = default;
        if (Rows.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault() is { } down)
            down.Offset = default;
    }

    /// <summary>The picker lists shown columns first, so it's rebuilt each time it opens; typing goes to its filter.</summary>
    private void ColumnsFlyout_Opening(object? sender, EventArgs e)
    {
        if (DataContext is not TableViewModel vm)
            return;
        vm.ColumnFilter = "";
        vm.RefreshColumnChoices();
        Dispatcher.UIThread.Post(() => ColumnFilterBox.Focus(), DispatcherPriority.Loaded);
    }
}
