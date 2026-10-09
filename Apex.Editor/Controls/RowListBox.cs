using System;
using Avalonia.Controls;

namespace Apex.Editor.Controls;

/// <summary>
/// A ListBox whose recycled rows keep what they built, for use with <see cref="RowPanel"/>. The stock ListBox empties a
/// row's content when the row leaves the view, which throws away the row its template built, so every row coming into
/// view (expanding a GDT, filtering, scrolling) built and styled its controls again. Here a row keeps its content while
/// it waits in the pool and takes the next item in place of the old one; the item template recycles the controls it
/// built and only the bindings change.
/// </summary>
public class RowListBox : ListBox
{
    protected override Type StyleKeyOverride => typeof(ListBox);

    protected override void PrepareContainerForItemOverride(Control container, object? item, int index)
    {
        if (container is ListBoxItem row && row.IsSet(ContentControl.ContentProperty) && !ReferenceEquals(row.Content, item))
            row.SetCurrentValue(ContentControl.ContentProperty, item);
        base.PrepareContainerForItemOverride(container, item, index);
    }

    protected override void ClearContainerForItemOverride(Control container)
    {
        if (container is not ListBoxItem row)
        {
            base.ClearContainerForItemOverride(container);
            return;
        }
        // Everything the base does but empty the row. RowPanel has already unindexed the row, so the list can't map
        // this change back to an item and deselect it.
        row.ClearValue(IsSelectedProperty);
    }
}
