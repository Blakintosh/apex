using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Apex.Editor.Models;

namespace Apex.Editor.Services.Gdt;

/// <summary>
/// Turns indexed .gdt files into lazy <see cref="AssetRecord"/>s and resolves derived-asset types.
/// Pure data layer — no UI dependencies. Callers (e.g. MainViewModel) drive batching/threading.
/// </summary>
public static class GdtLoader
{
    /// <summary>Type assigned to derived assets whose parent can't be resolved in the loaded set.</summary>
    public const string UnknownType = "unknown";

    /// <summary>Indexes one source file into a <see cref="GdtFile"/> of lazily-materialized records.</summary>
    public static GdtFile IndexToGdtFile(GdtSource source)
    {
        var entries = GdtIndexer.IndexFile(source.FullPath, out var stamp);
        var file = new GdtFile { Name = source.RelativeName, FullPath = source.FullPath, Stamp = stamp };
        foreach (var e in entries)
        {
            file.Assets.Add(new AssetRecord
            {
                Name = e.Name,
                Type = e.IsDerived ? UnknownType : e.TypeOrParent,
                GdtName = source.RelativeName,
                Parent = e.IsDerived ? e.TypeOrParent : null,
                Source = e.BodyLength > 0
                    ? new GdtPropertySource(source.FullPath, e.BodyOffset, e.BodyLength)
                    : null,
                Disk = stamp is null ? null : new Save.DiskRef(e.Name, e.IsDerived ? e.TypeOrParent : null, e.BodyOffset),
            });
        }

        var sidecar = ExtensionSidecar.PathFor(source.FullPath);
        var recovered = Save.GdtSaveService.RecoverRemovedSidecar(sidecar);
        if (File.Exists(sidecar))
        {
            ApplySidecar(file, sidecar, GdtIndexer.IndexFile(sidecar, out var sidecarStamp), sidecarStamp);
            file.Extensions!.Recovered = recovered;
        }
        return file;
    }

    /// <summary>
    /// Brings <paramref name="gdt"/>'s extension data in line with its indexed <c>.gdtx</c>, as a reload does for a GDT:
    /// blocks are matched by the asset and extension id they have in the file, a block with unsaved edits keeps them,
    /// one seen but not edited reads as the file now, and blocks no longer in the file go unless they have edits.
    /// Bodies stay on disk until read.
    /// </summary>
    public static void ApplySidecar(GdtFile gdt, string path, List<GdtIndexEntry> entries, Save.GdtStamp? stamp)
    {
        var x = ExtensionSidecar.Of(gdt);
        x.File.FullPath = path;
        x.File.Stamp = stamp;

        // In file order per (asset, id), so a file holding the same block twice matches its records one to one.
        var existing = new Dictionary<(string, string), Queue<AssetRecord>>(BlockKey.Instance);
        var onDisk = new HashSet<AssetRecord>();
        // Blocks taken out with a deleted or moved asset are still in the file until a save deletes them there: they are
        // matched like the rest, so the file's copy isn't read back in as a new block.
        var removed = new HashSet<AssetRecord>(x.Removed);
        foreach (var b in x.File.Assets.Concat(x.Removed))
            if (b.Disk is { } d)
            {
                if (!existing.TryGetValue((d.Name, b.Type), out var q))
                    existing[(d.Name, b.Type)] = q = new Queue<AssetRecord>(1);
                q.Enqueue(b);
                onDisk.Add(b);
            }

        foreach (var e in entries)
        {
            // A [ "parent" ] block isn't one Apex writes; it is kept (and preserved by saves) but never read as data.
            var type = e.IsDerived ? "" : e.TypeOrParent;
            var parent = e.IsDerived ? e.TypeOrParent : null;
            IPropertySource? source = e.BodyLength > 0 ? new GdtPropertySource(path, e.BodyOffset, e.BodyLength) : null;
            var disk = stamp is null ? (Save.DiskRef?)null : new Save.DiskRef(e.Name, parent, e.BodyOffset);
            // A block the session made that the file now has too (another program wrote it meanwhile) is that block:
            // the session's values are then its edits, and a save asks about every key the two disagree on.
            var rec = existing.GetValueOrDefault((e.Name, type)) is { Count: > 0 } matches
                ? matches.Dequeue()
                : x.File.Assets.Find(b => b.Disk is null && b.Parent == parent
                    && b.Name.Equals(e.Name, StringComparison.OrdinalIgnoreCase) && b.Type.Equals(type, StringComparison.OrdinalIgnoreCase));
            if (rec is not null)
            {
                onDisk.Remove(rec);
                rec.Disk = disk;
                rec.Source = source;
                // Its values stay what Apex read, so the delete is checked against them ("changed before delete").
                if (rec.HasSessionEdits || removed.Contains(rec))
                    continue;
                if (rec.SessionBaseline is not null && rec.IsMaterialized)
                {
                    var now = source?.Materialize() ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    rec.Properties.Clear();
                    foreach (var (k, v) in now)
                        rec.Properties[k] = v;
                    rec.RebaseAfterSave(now);
                }
                else
                    rec.DropMaterialized();
                continue;
            }
            x.File.Assets.Add(new AssetRecord
            {
                Name = e.Name, Type = type, Parent = parent, GdtName = x.File.Name, Source = source, Disk = disk,
            });
        }

        // Left in onDisk: blocks the file no longer has. One with unsaved edits stays (saving then asks, as for a GDT).
        x.File.Assets.RemoveAll(b => onDisk.Contains(b) && !b.HasSessionEdits);
        // A removed block the file no longer has leaves nothing to delete; undoing its delete writes it again, as new.
        foreach (var b in removed.Where(onDisk.Contains))
        {
            x.Forget(b);
            b.Disk = null;
            b.Source = null;
        }
    }

    /// <summary>
    /// <paramref name="gdt"/>'s <c>.gdtx</c> is gone from disk: blocks without edits go; edited ones stay, as new, so the
    /// next save writes them to a new file rather than losing them.
    /// </summary>
    public static void RemoveSidecar(GdtFile gdt)
    {
        if (gdt.Extensions is not { } x)
            return;
        x.File.FullPath = null;
        x.File.Stamp = null;
        x.File.Assets.RemoveAll(b => b.Disk is not null && !b.HasSessionEdits);
        foreach (var b in x.File.Assets)
        {
            b.Disk = null;
            b.Source = null;
        }
        if (x.File.Assets.Count == 0)
            gdt.Extensions = null;
    }

    /// <summary>Blocks are told apart by asset name and extension id, both case-insensitive.</summary>
    private sealed class BlockKey : IEqualityComparer<(string, string)>
    {
        public static readonly BlockKey Instance = new();

        public bool Equals((string, string) x, (string, string) y) =>
            StringComparer.OrdinalIgnoreCase.Equals(x.Item1, y.Item1) && StringComparer.OrdinalIgnoreCase.Equals(x.Item2, y.Item2);

        public int GetHashCode((string, string) k) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(k.Item1), StringComparer.OrdinalIgnoreCase.GetHashCode(k.Item2));
    }

    /// <summary>
    /// Resolves each derived asset's <see cref="AssetRecord.Type"/> by walking its parent chain to the
    /// root ancestor within the loaded set. Unresolved / cyclic chains resolve to <see cref="UnknownType"/>.
    /// First occurrence of a name wins (priority order), matching catalog enumeration.
    /// </summary>
    public static void ResolveParents(IReadOnlyList<AssetRecord> all)
    {
        var byName = new Dictionary<string, AssetRecord>(all.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var r in all)
            byName.TryAdd(r.Name, r);

        foreach (var r in all)
        {
            if (r.Parent is null)
                continue;
            r.Type = ResolveRootType(r, byName);
        }
    }

    private static string ResolveRootType(AssetRecord rec, Dictionary<string, AssetRecord> byName)
    {
        var cur = rec;
        for (var depth = 0; depth < 64; depth++)
        {
            if (cur.Parent is null)
                return cur.Type; // reached a root — its gdf type is authoritative
            if (!byName.TryGetValue(cur.Parent, out var parent) || ReferenceEquals(parent, cur))
                return UnknownType;
            cur = parent;
        }

        return UnknownType; // depth cap → treat as cycle
    }

    /// <summary>
    /// Convenience end-to-end load used by scratch/smoke harnesses: enumerate the catalog, index all
    /// files in parallel, build the database, then resolve derived parents. Never throws for a single
    /// bad file (it's reported via <paramref name="warn"/> and skipped).
    /// </summary>
    public static AssetDatabase LoadAll(GameEnvironment env, Action<string>? warn = null)
    {
        var sources = GdtCatalog.Enumerate(env);
        var files = new GdtFile[sources.Count];

        Parallel.For(0, sources.Count, i =>
        {
            try
            {
                files[i] = IndexToGdtFile(sources[i]);
            }
            catch (Exception ex)
            {
                warn?.Invoke($"{sources[i].RelativeName}: {ex.Message}");
                files[i] = new GdtFile { Name = sources[i].RelativeName };
            }
        });

        var gdts = new List<GdtFile>(files);
        var assets = new List<AssetRecord>();
        foreach (var f in files)
            assets.AddRange(f.Assets);

        ResolveParents(assets);
        return new AssetDatabase { Gdts = gdts, Assets = assets };
    }
}
