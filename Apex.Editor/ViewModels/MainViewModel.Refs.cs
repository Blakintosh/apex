using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using Apex.Editor.Models;

namespace Apex.Editor.ViewModels;

/// <summary>
/// What a reference field can suggest: every asset of the referenced type, by name. The per-type lists are built from
/// the asset index the first time a reference field asks, and thrown away whenever the index changes (an asset added,
/// removed, renamed, re-typed or reloaded from disk); the next ask builds them again. On the real install (122k assets)
/// the build runs off the UI thread and the field is told when it is ready; a small corpus builds in place.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>The most suggestions a typed query lists, best first (the popup shows ten at a time and scrolls).</summary>
    public const int RefSuggestLimit = 50;

    // type → its assets sorted by name; null until asked for, and again after any index change.
    private Dictionary<string, AssetRecord[]>? _refCatalog;
    // Bumped by every index change: a build finished for an older version is stale and is thrown away.
    private int _refCatalogVersion;
    private int _refCatalogBuilding = -1;

    // Typing a name one letter at a time only narrows it, so the next keystroke searches the last one's matches. Kept
    // only while that list is short: a one-letter query over tens of thousands of images isn't worth holding.
    private (Dictionary<string, AssetRecord[]> Catalog, string Type, string Query, List<AssetRecord> Matches)? _refNarrow;
    private const int RefNarrowLimit = 4096;

    /// <summary>Raised on the UI thread when the reference catalog a field asked for becomes ready.</summary>
    public event Action? RefCatalogReady;

    /// <summary>The index changed: the next reference suggestion rebuilds the per-type lists.</summary>
    private void InvalidateRefCatalog()
    {
        _refCatalogVersion++;
        _refCatalog = null;
        _refNarrow = null;
    }

    /// <summary>A reference field took the keyboard: have the catalog ready by the first keystroke.</summary>
    public void WarmRefCatalog() => TryGetRefCatalog(out _);

    /// <summary>
    /// Assets of <paramref name="refType"/> for a reference field: with nothing typed, all of them by name; otherwise
    /// those whose name contains <paramref name="query"/>, ranked like Quick Open (exact, starts with, shorter,
    /// contains) and capped at <see cref="RefSuggestLimit"/>. Null while the catalog is still being built; the field
    /// hears <see cref="RefCatalogReady"/> when it can ask again.
    /// </summary>
    public IReadOnlyList<AssetRecord>? MatchRefs(string refType, string query)
    {
        if (!TryGetRefCatalog(out var catalog))
            return null;
        var all = catalog.TryGetValue(refType, out var ofType) ? ofType : Array.Empty<AssetRecord>();
        if (query.Length == 0)
            return all;

        IReadOnlyList<AssetRecord> source = all;
        if (_refNarrow is { } n && ReferenceEquals(n.Catalog, catalog) && n.Type.Equals(refType, StringComparison.OrdinalIgnoreCase)
            && query.StartsWith(n.Query, StringComparison.OrdinalIgnoreCase))
            source = n.Matches;
        List<AssetRecord>? matches = new();
        var top = new TopAssets(query, RefSuggestLimit);
        foreach (var a in source)
        {
            var at = a.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase);
            if (at < 0)
                continue;
            if (matches is { Count: < RefNarrowLimit })
                matches.Add(a);
            else
                matches = null;
            top.Offer(a, RankAt(at, a.Name.Length, query.Length));
        }
        _refNarrow = matches is null ? null : (catalog, refType, query, matches);
        return top.Result();
    }

    /// <summary>True when an asset of <paramref name="refType"/> named <paramref name="name"/> is in the index now.</summary>
    public bool RefExists(string refType, string name) =>
        FindAsset(refType, name) is { } a && (a.Type.Equals(refType, StringComparison.OrdinalIgnoreCase)
            || IsWeaponFamilyRef(refType) && Services.Extensions.ExtensionManifest.IsWeapon(a.Type));

    /// <summary>
    /// An extension's reference to any weapon (refType <c>weapon</c>, as its targets say it): every weapon type, while no
    /// type of that name is loaded (mock data has one, and it is that type).
    /// </summary>
    private bool IsWeaponFamilyRef(string refType) =>
        refType.Equals(Services.Extensions.ExtensionManifest.WeaponFamily, StringComparison.OrdinalIgnoreCase)
        && SchemaRegistry.Get(refType) is null;

    private bool TryGetRefCatalog(out Dictionary<string, AssetRecord[]> catalog)
    {
        if (_refCatalog is { } ready)
        {
            catalog = ready;
            return true;
        }
        catalog = null!;
        // Mock data and a small install build in place (well under a frame); the real install builds off the thread.
        if (!_liveMode || _db.Assets.Count <= 5000)
        {
            catalog = _refCatalog = BuildRefCatalog(Snapshot(_db.Assets));
            return true;
        }
        if (_refCatalogBuilding != _refCatalogVersion)
            StartRefCatalogBuild();
        return false;
    }

    private void StartRefCatalogBuild()
    {
        var version = _refCatalogBuilding = _refCatalogVersion;
        // Names and types are read here, on the UI thread that changes them, so the build sorts a fixed snapshot.
        var snapshot = Snapshot(_db.Assets);
        Task.Run(() =>
        {
            Dictionary<string, AssetRecord[]>? built = null;
            try
            {
                built = BuildRefCatalog(snapshot);
            }
            finally
            {
                // Posted even if the build threw, so a failed build is never mistaken for one still running: nothing is
                // installed and the next keystroke builds again.
                Dispatcher.UIThread.Post(() => FinishRefCatalogBuild(version, built));
            }
        });
    }

    private void FinishRefCatalogBuild(int version, Dictionary<string, AssetRecord[]>? built)
    {
        if (_refCatalogBuilding == version)
            _refCatalogBuilding = -1;
        if (built is null)
            return;
        if (version != _refCatalogVersion)
        {
            // The index moved on while this built; build again if a field is still waiting.
            if (RefCatalogReady is not null && _refCatalogBuilding != _refCatalogVersion)
                StartRefCatalogBuild();
            return;
        }
        _refCatalog = built;
        RefCatalogReady?.Invoke();
    }

    private static (string Type, string Name, AssetRecord Asset)[] Snapshot(List<AssetRecord> assets)
    {
        var snapshot = new (string, string, AssetRecord)[assets.Count];
        for (var i = 0; i < snapshot.Length; i++)
            snapshot[i] = (assets[i].Type, assets[i].Name, assets[i]);
        return snapshot;
    }

    private static Dictionary<string, AssetRecord[]> BuildRefCatalog((string Type, string Name, AssetRecord Asset)[] assets)
    {
        var byType = new Dictionary<string, List<(string Name, AssetRecord Asset)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (type, name, asset) in assets)
        {
            if (!byType.TryGetValue(type, out var list))
                byType[type] = list = new List<(string, AssetRecord)>();
            list.Add((name, asset));
        }
        var catalog = new Dictionary<string, AssetRecord[]>(byType.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (type, list) in byType)
        {
            list.Sort(static (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            var sorted = new AssetRecord[list.Count];
            for (var i = 0; i < sorted.Length; i++)
                sorted[i] = list[i].Asset;
            catalog[type] = sorted;
        }
        // An extension's weapon reference picks from every weapon type at once.
        var family = Services.Extensions.ExtensionManifest.WeaponFamily;
        if (!catalog.ContainsKey(family))
            catalog[family] = catalog.Where(t => Services.Extensions.ExtensionManifest.IsWeapon(t.Key)).SelectMany(t => t.Value)
                .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        return catalog;
    }
}
