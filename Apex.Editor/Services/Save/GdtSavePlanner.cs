using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Apex.Editor.Models;
using Apex.Editor.Services.Gdt;

namespace Apex.Editor.Services.Save;

/// <summary>
/// Turns the session's records into save requests (what changed, per GDT) and, after a save, brings the records back
/// in line with the files written. No UI: the app and the tests use it the same way.
/// </summary>
public static class GdtSavePlanner
{
    /// <summary>
    /// Where a GDT lives: its loaded path, or for a GDT created this session, <c>source_data\name</c> under the install
    /// (a name that already has a folder, "source_data/x.gdt", is taken as relative to the install root).
    /// </summary>
    public static string? PathFor(GdtFile gdt, string? bo3Root)
    {
        if (gdt.FullPath is { } path)
            return path;
        if (bo3Root is null)
            return null;
        var rel = gdt.Name.Replace('/', Path.DirectorySeparatorChar);
        return rel.Contains(Path.DirectorySeparatorChar)
            ? Path.Combine(bo3Root, rel)
            : Path.Combine(bo3Root, "source_data", rel);
    }

    /// <summary>
    /// One request per GDT that the session changed. <paramref name="candidates"/> is every record the session may
    /// have changed (edited, read into memory, created, renamed or re-parented); records it didn't change are left
    /// out. <paramref name="deleted"/> are records deleted this session. <paramref name="newGdts"/> are GDTs created
    /// this session: they are written even while empty.
    /// </summary>
    public static List<GdtSaveRequest> Plan(IEnumerable<GdtFile> gdts, IEnumerable<AssetRecord> candidates,
        IEnumerable<AssetRecord> deleted, IEnumerable<GdtFile> newGdts, string? bo3Root, List<string> problems)
    {
        var byGdt = new Dictionary<string, List<AssetEdit>>(StringComparer.OrdinalIgnoreCase);
        void Add(string gdt, AssetEdit e)
        {
            if (!byGdt.TryGetValue(gdt, out var list))
                byGdt[gdt] = list = new List<AssetEdit>();
            list.Add(e);
        }

        var seen = new HashSet<AssetRecord>();
        foreach (var r in candidates)
            if (seen.Add(r) && EditFor(r) is { } e)
                Add(r.GdtName, e);
        foreach (var r in deleted)
            if (r.Disk is { } disk && seen.Add(r))
                Add(r.GdtName, new AssetEdit
                {
                    Disk = disk, Delete = true, Name = r.Name, Type = r.Type, Parent = r.Parent,
                    Baseline = r.SessionBaseline, Tag = r,
                });

        var created = new HashSet<GdtFile>(newGdts);
        var requests = new List<GdtSaveRequest>();
        // Extension data before any GDT: a save that stops partway (a file locked at its swap, a failed check) leaves a
        // .gdtx ahead of its GDT, never a GDT ahead of its .gdtx. The renames, copies and moves the GDT hasn't saved
        // stay in the session and journal, and replay onto the .gdtx as it now is. A GDT saved first would drop them
        // while its .gdtx was still behind, orphaning the blocks they carried.
        foreach (var gdt in gdts)
            if (gdt.Extensions is { } x && PlanSidecar(x, bo3Root, problems) is { } request)
                requests.Add(request);
        foreach (var gdt in gdts)
        {
            var edits = byGdt.GetValueOrDefault(gdt.Name);
            if (edits is null && !created.Contains(gdt))
                continue;
            if (gdt.FullPath is not null && gdt.Stamp is null)
            {
                problems.Add($"Apex couldn't read {Path.GetFileName(gdt.FullPath)} when it loaded, so it won't write to it.");
                continue;
            }
            if (PathFor(gdt, bo3Root) is not { } path)
            {
                problems.Add($"{gdt.Name} is sample data: there is no GDT file to save it to.");
                continue;
            }
            requests.Add(new GdtSaveRequest
            {
                Path = path,
                Expected = gdt.Stamp,
                Edits = (IReadOnlyList<AssetEdit>?)edits ?? Array.Empty<AssetEdit>(),
                Tag = gdt,
            });
        }
        return requests;
    }

    /// <summary>
    /// The save of one GDT's <c>.gdtx</c>, or null when the session left its extension data as the file has it. A block
    /// is written only when it holds a value: a new block with none is never written (so a GDT without extension data
    /// never gets a file), and a saved block emptied this session is deleted (the file with its last block). Blocks the
    /// session didn't touch, orphans included, are left byte for byte.
    /// </summary>
    public static GdtSaveRequest? PlanSidecar(ExtensionSidecar x, string? bo3Root, List<string> problems)
    {
        List<AssetEdit>? edits = null;
        foreach (var b in x.File.Assets)
        {
            AssetEdit? e;
            if (b.Disk is null)
                e = b.Properties.Count > 0 ? EditFor(b) : null;
            else if (b.IsMaterialized && b.ValuesTouched && b.Properties.Count == 0)
                e = new AssetEdit { Disk = b.Disk, Delete = true, Name = b.Name, Type = b.Type, Baseline = b.SessionBaseline, Tag = b };
            else
                e = EditFor(b);
            if (e is null)
                continue;
            var keys = e.Set?.Keys ?? e.Full?.Keys ?? Enumerable.Empty<string>();
            foreach (var key in keys)
                if (x.CoreType(b.Name, key) is { } type)
                    problems.Add($"{b.Name}: {key} is a {type} key, so it belongs in {Path.GetFileName(x.Owner.Name)}, not in its extension data.");
            (edits ??= new List<AssetEdit>()).Add(e);
        }
        // Blocks whose asset was deleted or moved away go with it.
        foreach (var b in x.Removed)
            if (b.Disk is { } disk)
                (edits ??= new List<AssetEdit>()).Add(new AssetEdit
                {
                    Disk = disk, Delete = true, Name = b.Name, Type = b.Type, Baseline = b.SessionBaseline, Tag = b,
                });
        if (edits is null)
            return null;
        if (x.File.FullPath is not null && x.File.Stamp is null)
        {
            problems.Add($"Apex couldn't read {Path.GetFileName(x.File.FullPath)} when it loaded, so it won't write to it.");
            return null;
        }
        if ((x.File.FullPath ?? (PathFor(x.Owner, bo3Root) is { } gdtPath ? ExtensionSidecar.PathFor(gdtPath) : null)) is not { } path)
        {
            problems.Add($"{x.Owner.Name} is sample data: there is no GDT file to save its extension data beside.");
            return null;
        }
        return new GdtSaveRequest { Path = path, Expected = x.File.Stamp, Edits = edits, Tag = x, Sidecar = true };
    }

    /// <summary>What saving <paramref name="r"/> means, or null when the file already says it.</summary>
    public static AssetEdit? EditFor(AssetRecord r)
    {
        if (r.Disk is not { } disk)
        {
            return new AssetEdit
            {
                Name = r.Name, Type = r.Type, Parent = r.Parent, Tag = r,
                Full = new Dictionary<string, string>(r.Properties, StringComparer.OrdinalIgnoreCase),
            };
        }

        Dictionary<string, string?>? set = null;
        // Only a record some edit path wrote to can differ from its baseline (skips the hundreds merely viewed).
        if (r.IsMaterialized && r.ValuesTouched && r.SessionBaseline is { } baseline)
        {
            foreach (var (key, value) in r.Properties)
                if (!baseline.TryGetValue(key, out var was) || !string.Equals(was, value, StringComparison.Ordinal))
                    (set ??= new(StringComparer.OrdinalIgnoreCase))[key] = value;
            foreach (var key in baseline.Keys)
                if (!r.Properties.ContainsKey(key))
                    (set ??= new(StringComparer.OrdinalIgnoreCase))[key] = null;
        }
        var header = !string.Equals(r.Name, disk.Name, StringComparison.Ordinal)
                     || !string.Equals(r.Parent, disk.Parent, StringComparison.Ordinal);
        if (set is null && !header)
            return null;
        return new AssetEdit
        {
            Disk = disk, Name = r.Name, Type = r.Type, Parent = r.Parent, Tag = r,
            Set = set, Baseline = r.SessionBaseline,
        };
    }

    /// <summary>
    /// After <paramref name="file"/> saved <paramref name="gdt"/>: every record of the file points at its new offset,
    /// written records read as what was written (the file wins where another program changed a key the session
    /// didn't), their baselines move to the saved values, and the GDT expects the new stamp.
    /// </summary>
    public static void Commit(GdtFile gdt, GdtSaveFileResult file,
        IReadOnlyDictionary<AssetRecord, Dictionary<string, string>>? planned = null)
    {
        if (file.Status is not (GdtSaveStatus.Saved or GdtSaveStatus.Unchanged) || file.Splice is not { Bytes: { } bytes } splice)
            return;
        var path = file.Request.Path;
        gdt.FullPath = path;
        gdt.Stamp = file.Stamp;

        var written = new Dictionary<AssetRecord, GdtAssetSpan>();
        foreach (var (edit, span) in splice.Placed)
            if (edit.Tag is AssetRecord r)
                written[r] = span;

        foreach (var r in gdt.Assets)
        {
            GdtAssetSpan span;
            if (written.TryGetValue(r, out var placed))
                span = placed;
            else if (r.Disk is { } disk && splice.Survivors.TryGetValue(disk.BodyOffset, out var moved))
                span = moved;
            else
                continue;
            Rebind(r, path, span);
            if (!written.ContainsKey(r))
                continue;
            Dictionary<string, string>? saved = null;
            if (r.IsMaterialized)
            {
                saved = GdtParser.ParseBody(bytes.AsSpan(span.BodyStart, span.BodyLength));
                // A key edited again while the save ran keeps its newer value (it shows as changed); every other key
                // reads as the file now holds it.
                var props = r.Properties;
                var snapshot = planned?.GetValueOrDefault(r);
                var keys = new HashSet<string>(props.Keys, StringComparer.OrdinalIgnoreCase);
                keys.UnionWith(saved.Keys);
                if (snapshot is not null)
                    keys.UnionWith(snapshot.Keys);
                foreach (var key in keys)
                {
                    var current = props.TryGetValue(key, out var c) ? c : null;
                    if (snapshot is not null && current != (snapshot.TryGetValue(key, out var p) ? p : null))
                        continue;
                    if (saved.TryGetValue(key, out var s))
                    {
                        if (current != s)
                            props[key] = s;
                    }
                    else if (current is not null)
                        props.Remove(key);
                }
            }
            r.RebaseAfterSave(saved);
        }
    }

    /// <summary>After a save: commits whatever the request was for, a GDT or a GDT's sidecar.</summary>
    public static void Commit(GdtSaveFileResult file, IReadOnlyDictionary<AssetRecord, Dictionary<string, string>>? planned = null)
    {
        if (file.Request.Tag is ExtensionSidecar x)
            Commit(x, file, planned);
        else if (file.Request.Tag is GdtFile gdt)
            Commit(gdt, file, planned);
    }

    /// <summary>
    /// After <paramref name="file"/> saved a sidecar: as for a GDT, plus deleted blocks leave the session (one given a
    /// value again while the save ran stays, to be written as new) and a removed file leaves no path or stamp behind.
    /// </summary>
    public static void Commit(ExtensionSidecar x, GdtSaveFileResult file, IReadOnlyDictionary<AssetRecord, Dictionary<string, string>>? planned = null)
    {
        Commit(x.File, file, planned);
        if (file.Status is not (GdtSaveStatus.Saved or GdtSaveStatus.Unchanged))
            return;
        if (file.Removed || file.Request.IsNewFile && file.Status == GdtSaveStatus.Unchanged)
        {
            x.File.FullPath = null;
            x.File.Stamp = null;
        }
        foreach (var e in file.Request.Edits)
        {
            if (!e.Delete || e.Tag is not AssetRecord b)
                continue;
            b.Disk = null;
            b.Source = null;
            if (x.Forget(b))
                continue;
            if (b.Properties.Count == 0)
            {
                x.File.Assets.Remove(b);
                b.RebaseAfterSave(b.Properties);
            }
        }
        if (x.File.FullPath is null && x.File.Assets.Count == 0 && x.Owner.Extensions == x)
            x.Owner.Extensions = null;
    }

    /// <summary>Points <paramref name="r"/> at its body in the file as it is now.</summary>
    public static void Rebind(AssetRecord r, string path, GdtAssetSpan span)
    {
        r.Disk = new DiskRef(span.Name, span.IsDerived ? span.TypeOrParent : null, span.BodyStart);
        r.Source = span.BodyLength > 0 ? new GdtPropertySource(path, span.BodyStart, span.BodyLength) : null;
    }
}
