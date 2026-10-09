namespace Apex.Editor.Controls;

/// <summary>
/// A row item that can make a stand-in of its own kind which belongs to nothing (no asset, no tab, no callbacks). A
/// <see cref="RowPanel"/> parks a pooled row on it once the row's item has left the list, instead of throwing the row
/// away when the new list has no item of that kind: switching between asset types then reuses every property, problem
/// and link row it ever built (lists whose items offer no stand-in still drop such rows).
/// </summary>
public interface IRowStandIn
{
    /// <summary>A new item of exactly this item's type, holding nothing of this item's list.</summary>
    object? CreateStandIn();
}
