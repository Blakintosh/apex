using System;
using Avalonia.Controls;
using Avalonia.Controls.Templates;

namespace Apex.Editor.Controls;

/// <summary>
/// An ItemsControl for long virtualized forms whose recycled rows keep what they built. The stock container is a
/// ContentPresenter, which throws its row away whenever the virtualizing panel recycles it (content is swapped while the
/// container is out of the tree, and re-attaching forgets the template), so every row that scrolls in and every asset
/// that opens loaded its rows from the templates again — on a weapon, most of the time it took to open. Here each row
/// kind (its view model type) has its own pool, and a container builds its row once and afterwards only takes the next
/// item as its DataContext.
/// </summary>
public class RowItemsControl : ItemsControl
{
    protected override Type StyleKeyOverride => typeof(ItemsControl);

    protected override bool NeedsContainerOverride(object? item, int index, out object? recycleKey)
    {
        recycleKey = item?.GetType();
        return true;
    }

    protected override Control CreateContainerForItemOverride(object? item, int index, object? recycleKey) => new RowHost();

    protected override void PrepareContainerForItemOverride(Control container, object? item, int index)
    {
        if (container is not RowHost host)
        {
            base.PrepareContainerForItemOverride(container, item, index);
            return;
        }
        host.DataContext = item;
        // Pools are per item type, so a host that already has a row has the right one.
        host.Child ??= this.FindDataTemplate(item, ItemTemplate)?.Build(item);
    }

    protected override void ClearContainerForItemOverride(Control container)
    {
        // A pooled host keeps its row and stays bound to its last item until it takes the next one, which is what makes
        // reuse cheap: unbinding through null would drop the row's templated editor. Rows are safe to rebind straight
        // from one item to the next (the dropdown, which wasn't, is a ChoiceBox).
        if (container is not RowHost)
            base.ClearContainerForItemOverride(container);
    }

    /// <summary>The row container: holds one row built from its item's template.</summary>
    private sealed class RowHost : Decorator
    {
    }
}
