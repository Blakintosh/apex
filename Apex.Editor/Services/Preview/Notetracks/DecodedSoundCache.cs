using System;
using System.Collections.Generic;

namespace Apex.Editor.Services.Preview.Notetracks;

/// <summary>
/// Decoded sounds by file path (case-insensitive, as Windows paths are), least recently used first out. Bounded by
/// bytes and by entries: a file that would not decode is remembered as null and still costs an entry, so a corpus of
/// bad files can't grow it without limit. Not thread-safe: the audio worker is its only user.
/// </summary>
public sealed class DecodedSoundCache(long budgetBytes, int maxEntries)
{
    /// <summary>What an entry costs beyond its samples (also the whole cost of a remembered failure).</summary>
    public const long EntryOverheadBytes = 4096;

    private readonly Dictionary<string, (float[]? Samples, LinkedListNode<string> Node)> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _order = new();

    public long Bytes { get; private set; }

    public int Count => _entries.Count;

    private static long Cost(float[]? samples) => EntryOverheadBytes + (samples?.LongLength ?? 0) * sizeof(float);

    /// <summary>True when <paramref name="path"/> is cached (its samples, or null for a file that won't decode); marks it recently used.</summary>
    public bool TryGet(string path, out float[]? samples)
    {
        if (!_entries.TryGetValue(path, out var entry))
        {
            samples = null;
            return false;
        }
        _order.Remove(entry.Node);
        _order.AddLast(entry.Node);
        samples = entry.Samples;
        return true;
    }

    /// <summary>Caches <paramref name="samples"/> for <paramref name="path"/> (replacing any entry), then evicts the oldest past the bounds.</summary>
    public void Add(string path, float[]? samples)
    {
        if (_entries.TryGetValue(path, out var old))
        {
            _order.Remove(old.Node);
            Bytes -= Cost(old.Samples);
        }
        var node = _order.AddLast(path);
        _entries[path] = (samples, node);
        Bytes += Cost(samples);
        while ((Bytes > budgetBytes || _entries.Count > maxEntries) && _order.First is { } oldest && !ReferenceEquals(oldest, node))
        {
            _order.RemoveFirst();
            if (_entries.TryGetValue(oldest.Value, out var gone) && ReferenceEquals(gone.Node, oldest))
            {
                _entries.Remove(oldest.Value);
                Bytes -= Cost(gone.Samples);
            }
        }
    }
}
