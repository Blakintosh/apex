using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Apex.Editor.ViewModels;

internal static class CollectionSync
{
    /// <summary>
    /// Makes <paramref name="target"/> equal <paramref name="source"/> slot by slot: equal items are
    /// left alone (their realized containers survive), differing slots are replaced, and the tail is
    /// added or trimmed. For small value-typed lists (records) that are recomputed on every edit.
    /// </summary>
    public static void SyncTo<T>(this ObservableCollection<T> target, IReadOnlyList<T> source)
    {
        var common = System.Math.Min(target.Count, source.Count);
        for (var i = 0; i < common; i++)
            if (!EqualityComparer<T>.Default.Equals(target[i], source[i]))
                target[i] = source[i];
        while (target.Count > source.Count)
            target.RemoveAt(target.Count - 1);
        for (var i = target.Count; i < source.Count; i++)
            target.Add(source[i]);
    }
}
