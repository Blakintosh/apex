using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace Apex.Editor.ViewModels;

/// <summary>
/// An <see cref="ObservableCollection{T}"/> that can splice many rows in/out with a single
/// <see cref="NotifyCollectionChangedAction.Add"/>/<see cref="NotifyCollectionChangedAction.Remove"/>
/// notification instead of one event per item (WI-6). Expanding a several-thousand-asset GDT group in
/// the flattened browser therefore costs one CollectionChanged event, not N — no per-item event storm,
/// and (unlike a Reset) Avalonia keeps the ListBox scroll anchor because the change is expressed as a
/// contiguous ranged Add/Remove with a starting index rather than a wholesale reset.
/// </summary>
public sealed class RangeObservableCollection<T> : ObservableCollection<T>
{
    public RangeObservableCollection() { }

    public RangeObservableCollection(IEnumerable<T> items) : base(items) { }

    /// <summary>Inserts <paramref name="items"/> at <paramref name="index"/> as one ranged Add event.</summary>
    public void InsertRange(int index, IReadOnlyList<T> items)
    {
        if (items.Count == 0)
            return;
        if (items.Count == 1)
        {
            Insert(index, items[0]);
            return;
        }

        var backing = (List<T>)Items;
        backing.InsertRange(index, items);
        RaiseReset(new List<T>(items), index, NotifyCollectionChangedAction.Add);
    }

    /// <summary>Removes <paramref name="count"/> rows from <paramref name="index"/> as one ranged Remove event.</summary>
    public void RemoveRange(int index, int count)
    {
        if (count <= 0)
            return;
        if (count == 1)
        {
            RemoveAt(index);
            return;
        }

        var backing = (List<T>)Items;
        var removed = backing.GetRange(index, count);
        backing.RemoveRange(index, count);
        RaiseReset(removed, index, NotifyCollectionChangedAction.Remove);
    }

    /// <summary>Replaces the whole contents with one Reset notification.</summary>
    public void ReplaceAll(IEnumerable<T> items)
    {
        var backing = (List<T>)Items;
        backing.Clear();
        backing.AddRange(items);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    private void RaiseReset(IList changed, int index, NotifyCollectionChangedAction action)
    {
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(action, changed, index));
    }
}
