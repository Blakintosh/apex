using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Apex.Editor.Models;
using Apex.Render.Data.Gdt;
using Avalonia.Threading;

namespace Apex.Editor.Services.Preview.ToolsGfx;

/// <summary>
/// Serves the renderer's GDT lookups from Apex's asset index instead of re-parsing every GDT: the record's fields
/// with its parent chain merged (root first), including unsaved in-session edits. Deffile defaults are applied by the
/// data layer (<c>ToolsGfxData.MaterialDefaults</c>).
/// <para>
/// The index and its records belong to the UI thread. A lookup the cache cannot answer makes one short trip there to
/// find the record chain and snapshot what the merge needs (the in-memory properties of opened or edited records, the
/// lazy .gdt slice of the rest); the slices are read and merged on the calling thread. Results (misses too) are cached
/// and handed out as the same <see cref="GdtEntry"/> instance until <see cref="Revalidate"/>, which every preview load
/// runs on the UI thread before it starts, finds that a record of the chain was edited, reloaded from disk, renamed or
/// reparented, or that the name now resolves to another record.
/// </para>
/// </summary>
public sealed class ApexGdtLookup : IGdtLookup
{
    /// <summary>Entries kept; the least recently used beyond this are dropped by <see cref="Revalidate"/>.</summary>
    private const int Capacity = 2048;

    private static readonly IReadOnlyDictionary<string, string> NoFields = new Dictionary<string, string>();
    private static readonly AsyncLocal<LookupDependencies?> Recording = new();

    private readonly Func<string, string, AssetRecord?> _resolve;
    private readonly ConcurrentDictionary<string, Resolved> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    // Bumped by every revalidation (UI thread, under _gate): a snapshot taken before it may be stale and is not cached.
    private int _generation;
    private long _clock;

    public ApexGdtLookup(Func<string, string, AssetRecord?> resolve) => _resolve = resolve;

    /// <summary>One record of a resolved chain as it was when snapshotted (UI thread).</summary>
    internal readonly record struct Link(AssetRecord Record, string Name, string Type, string? Parent, IPropertySource? Source,
        Dictionary<string, string>? Properties);

    /// <summary>A cached lookup: the entry handed out and the chain it was merged from (empty for a miss).</summary>
    internal sealed class Resolved(string name, string type, GdtEntry? entry, Link[] chain)
    {
        public string Name { get; } = name;
        public string Type { get; } = type;
        public GdtEntry? Entry { get; } = entry;
        public Link[] Chain { get; } = chain;
        public long LastUsed;
    }

    public GdtEntry? Find(string name) => Find(name, "");

    public GdtEntry? Find(string name, string type)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        var key = type + "|" + name;
        if (!_cache.TryGetValue(key, out var resolved))
            resolved = Resolve(key, name, type);
        Volatile.Write(ref resolved.LastUsed, Interlocked.Increment(ref _clock));
        Recording.Value?.Add(key, resolved);
        return resolved.Entry;
    }

    private Resolved Resolve(string key, string name, string type)
    {
        (int Generation, Link[]? Chain) Capture() => (_generation, Snapshot(name, type));
        var (generation, chain) = Dispatcher.UIThread.CheckAccess()
            ? Capture()
            : Dispatcher.UIThread.Invoke(Capture, DispatcherPriority.Normal);
        var resolved = new Resolved(name, type, chain is null ? null : Merge(chain), chain ?? []);
        lock (_gate)
            return generation == _generation ? _cache.GetOrAdd(key, resolved) : resolved;
    }

    /// <summary>The record chain of <paramref name="name"/>, the record first and its root last; null when it does not
    /// resolve to a record of <paramref name="type"/>. UI thread.</summary>
    private List<AssetRecord>? Chain(string name, string type)
    {
        var record = _resolve(type, name);
        if (record is null || (type.Length > 0 && !record.Type.Equals(type, StringComparison.OrdinalIgnoreCase)))
            return null;
        var chain = new List<AssetRecord>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var cur = record; cur is not null && seen.Add(cur.Name) && chain.Count < 64; cur = cur.Parent is null ? null : _resolve(type, cur.Parent))
            chain.Add(cur);
        return chain;
    }

    private Link[]? Snapshot(string name, string type) =>
        Chain(name, type)?.Select(r => new Link(r, r.Name, r.Type, r.Parent, r.Source,
            r.IsMaterialized ? new Dictionary<string, string>(r.Properties, r.Properties.Comparer) : null)).ToArray();

    /// <summary>Merges the chain root first, so nearer ancestors (and finally the record itself) win.</summary>
    private static GdtEntry Merge(Link[] chain)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = chain.Length - 1; i >= 0; i--)
            foreach (var (k, v) in chain[i].Properties ?? chain[i].Source?.Materialize() ?? NoFields)
                fields[k] = v;
        var head = chain[0];
        return new GdtEntry(head.Name, head.Type + ".gdf", null, fields, head.Record.GdtName);
    }

    /// <summary>
    /// Drops every cached lookup whose records changed since it was taken (UI thread). Call before a preview load
    /// starts, so the load and everything it reuses (<see cref="IsCurrent"/>) sees the records as they are now.
    /// </summary>
    public void Revalidate()
    {
        Dispatcher.UIThread.VerifyAccess();
        lock (_gate)
        {
            _generation++;
            foreach (var (key, resolved) in _cache)
                if (!Matches(resolved))
                    _cache.TryRemove(key, out _);
            if (_cache.Count > Capacity)
                foreach (var (key, _) in _cache.OrderBy(kv => Volatile.Read(ref kv.Value.LastUsed)).Take(_cache.Count - Capacity * 3 / 4).ToList())
                    _cache.TryRemove(key, out _);
        }
    }

    private bool Matches(Resolved resolved)
    {
        var chain = Chain(resolved.Name, resolved.Type);
        if (chain is null || chain.Count != resolved.Chain.Length)
            return chain is null && resolved.Chain.Length == 0;
        for (int i = 0; i < chain.Count; i++)
        {
            var record = chain[i];
            var link = resolved.Chain[i];
            if (!ReferenceEquals(record, link.Record) || record.Name != link.Name || record.Type != link.Type || record.Parent != link.Parent)
                return false;
            if (record.IsMaterialized
                    ? link.Properties is null || !SameFields(link.Properties, record.Properties)
                    : link.Properties is not null || !ReferenceEquals(record.Source, link.Source))
                return false;
        }
        return true;
    }

    private static bool SameFields(Dictionary<string, string> snapshot, Dictionary<string, string> current)
    {
        if (snapshot.Count != current.Count)
            return false;
        foreach (var (k, v) in current)
            if (!snapshot.TryGetValue(k, out var s) || s != v)
                return false;
        return true;
    }

    /// <summary>
    /// Records every lookup made on this async flow until disposed (nested recordings also report to the enclosing
    /// ones), for <see cref="IsCurrent"/>.
    /// </summary>
    internal static LookupRecording Record() => new(Recording);

    /// <summary>True while every lookup in <paramref name="dependencies"/> would still return the same entry (checked
    /// against the cache as of the last <see cref="Revalidate"/>). Any thread.</summary>
    internal bool IsCurrent(LookupDependencies dependencies) =>
        dependencies.All(d => _cache.TryGetValue(d.Key, out var cached) && ReferenceEquals(cached, d.Value));

    /// <summary>Reports reused <paramref name="dependencies"/> to the enclosing recording, as if looked up again.</summary>
    internal static void Replay(LookupDependencies dependencies)
    {
        if (Recording.Value is { } current)
            foreach (var (key, resolved) in dependencies)
                current.Add(key, resolved);
    }
}

/// <summary>The lookups one piece of preview work depended on (<see cref="ApexGdtLookup.Record"/>).</summary>
internal sealed class LookupDependencies : IEnumerable<KeyValuePair<string, ApexGdtLookup.Resolved>>
{
    private readonly ConcurrentDictionary<string, ApexGdtLookup.Resolved> _items = new(StringComparer.OrdinalIgnoreCase);

    internal LookupDependencies(LookupDependencies? parent) => Parent = parent;

    internal LookupDependencies? Parent { get; }

    internal void Add(string key, ApexGdtLookup.Resolved resolved)
    {
        for (var d = this; d is not null; d = d.Parent)
            d._items[key] = resolved;
    }

    public IEnumerator<KeyValuePair<string, ApexGdtLookup.Resolved>> GetEnumerator() => _items.GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>An active <see cref="ApexGdtLookup.Record"/>; dispose to stop recording.</summary>
internal sealed class LookupRecording : IDisposable
{
    private readonly AsyncLocal<LookupDependencies?> _slot;

    internal LookupRecording(AsyncLocal<LookupDependencies?> slot)
    {
        _slot = slot;
        Dependencies = new LookupDependencies(slot.Value);
        slot.Value = Dependencies;
    }

    public LookupDependencies Dependencies { get; }

    public void Dispose() => _slot.Value = Dependencies.Parent;
}
