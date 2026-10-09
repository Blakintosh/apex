using System;
using System.Collections.Generic;
using System.Linq;

namespace Apex.Editor.Models;

/// <summary>
/// A GDT's extension data: <c>name.gdtx</c> beside <c>name.gdt</c>. APE drops every key its deffile doesn't declare
/// when it saves a GDT, so extension keys can't live there; they live here, in GDT syntax, one block per asset and
/// extension id, with the id where a GDT has its gdf (<c>"ar_foo_zm" ( "weapon-tech" ) { … }</c>). APE ignores the file.
/// <see cref="File"/> is the sidecar as the save path sees it, so it is saved like any GDT (splice, backup, atomic
/// replace, verify, conflicts); its records are the blocks, Name the asset and Type the extension id.
/// </summary>
public sealed class ExtensionSidecar
{
    public const string Extension = ".gdtx";

    public ExtensionSidecar(GdtFile owner)
    {
        Owner = owner;
        File = new GdtFile { Name = owner.Name + "x" };
    }

    /// <summary>The GDT whose assets the blocks belong to.</summary>
    public GdtFile Owner { get; }

    /// <summary>The sidecar file. Its FullPath is null until a save creates it, and again after a save removes it.</summary>
    public GdtFile File { get; }

    public IReadOnlyList<AssetRecord> Blocks => File.Assets;

    /// <summary>Loading found the file only as the temp file of a removal that didn't finish, and put it back.</summary>
    public bool Recovered { get; set; }

    /// <summary>The sidecar of <paramref name="gdt"/>, made (in memory only) if it has none yet.</summary>
    public static ExtensionSidecar Of(GdtFile gdt) => gdt.Extensions ??= new ExtensionSidecar(gdt);

    /// <summary>Where a GDT's sidecar lives.</summary>
    public static string PathFor(string gdtPath) => gdtPath + "x";

    /// <summary>
    /// The block of <paramref name="asset"/> for <paramref name="extensionId"/>. With several, one that came with the
    /// asset this session (made, copied or renamed to this name) wins over one the file has under the name, which then
    /// belonged to no asset; else the first.
    /// </summary>
    public AssetRecord? Find(string asset, string extensionId)
    {
        AssetRecord? first = null;
        foreach (var b in File.Assets)
        {
            if (b.Parent is not null || !b.Name.Equals(asset, StringComparison.OrdinalIgnoreCase)
                || !b.Type.Equals(extensionId, StringComparison.OrdinalIgnoreCase))
                continue;
            if (b.Disk is not { } disk || !disk.Name.Equals(b.Name, StringComparison.OrdinalIgnoreCase))
                return b;
            first ??= b;
        }
        return first;
    }

    /// <summary>The asset's values for one extension (empty when it has none). Reads that block only.</summary>
    public IReadOnlyDictionary<string, string> Values(string asset, string extensionId) =>
        Find(asset, extensionId)?.Properties ?? (IReadOnlyDictionary<string, string>)Empty;

    public string? Get(string asset, string extensionId, string key) =>
        Find(asset, extensionId)?.Properties.TryGetValue(key, out var v) == true ? v : null;

    /// <summary>
    /// Sets one extension value; null, or a value equal to <paramref name="defaultValue"/>, removes the key, because a
    /// missing key already means the default. A block is made for the first value and dropped from memory again if it
    /// empties before it was ever saved; a saved block that empties is deleted from the file by the next save (and the
    /// file with its last block). Returns whether anything changed.
    /// </summary>
    public bool Set(string asset, string extensionId, string key, string? value, string? defaultValue = null)
    {
        if (CoreType(asset, key) is { } type)
            throw new InvalidOperationException($"{key} is a {type} key: it belongs in the GDT, not in {extensionId}'s data.");
        if (value is not null && defaultValue is not null && Services.AssetQuery.ValuesEqual(value, defaultValue))
            value = null;

        var block = Find(asset, extensionId);
        if (block is null)
        {
            if (value is null)
                return false;
            block = new AssetRecord { Name = asset, Type = extensionId, GdtName = File.Name };
            File.Assets.Add(block);
        }
        block.CaptureBaseline();
        var before = block.Properties.TryGetValue(key, out var b) ? b : null;
        if (string.Equals(before, value, StringComparison.Ordinal))
            return false;
        EditHistory.Set(block, key, value);
        block.HasSessionEdits = true;
        if (block.Disk is null && block.Properties.Count == 0)
            File.Assets.Remove(block);
        return true;
    }

    // ── Blocks following their asset (rename, move, duplicate, delete in Apex) ──

    private readonly List<AssetRecord> _removed = new();

    /// <summary>Saved blocks taken out with their asset (deleted, or moved to another GDT): the next save deletes them from the file.</summary>
    public IReadOnlyList<AssetRecord> Removed => _removed;

    /// <summary>Every block of <paramref name="asset"/> (one per extension).</summary>
    public List<AssetRecord> BlocksOf(string asset) =>
        File.Assets.Where(b => b.Parent is null && b.Name.Equals(asset, StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>
    /// The asset was renamed: its blocks are known by the new name, and a save renames them in the file. A block already
    /// under the new name belongs to no asset (no asset may take a name another has). Where the asset brings a block of
    /// the same extension, that one is replaced: taken out as a deleted asset's is, and returned for
    /// <see cref="Restore"/> (left in, a save would refuse the file over two blocks of one name). Where it brings none,
    /// the block is kept and is the asset's from now on, as a build reading the file by name sees it; that is also what
    /// a journal replaying the rename finds when a save wrote the <c>.gdtx</c> and stopped before the GDT.
    /// </summary>
    public List<AssetRecord> Rename(string from, string to)
    {
        var replaced = new List<AssetRecord>();
        var own = BlocksOf(from);
        if (!from.Equals(to, StringComparison.OrdinalIgnoreCase))
            foreach (var b in BlocksOf(to))
                if (own.Any(o => o.Type.Equals(b.Type, StringComparison.OrdinalIgnoreCase)))
                {
                    Take(b);
                    replaced.Add(b);
                }
        foreach (var b in own)
            b.Name = to;
        return replaced;
    }

    /// <summary>
    /// Takes the asset's blocks out (it was deleted, or moved to another GDT) and returns them for <see cref="Restore"/>.
    /// A saved block is deleted from the file by the next save; one never saved just goes.
    /// </summary>
    public List<AssetRecord> Detach(string asset)
    {
        var blocks = BlocksOf(asset);
        foreach (var b in blocks)
            Take(b);
        return blocks;
    }

    private void Take(AssetRecord block)
    {
        File.Assets.Remove(block);
        if (block.Disk is null)
            return;
        // What Apex read is what the save's delete is checked against, as for a moved asset's ghost.
        block.CaptureBaseline();
        _removed.Add(block);
    }

    /// <summary>Puts blocks <see cref="Detach"/> took out back (the delete or move was undone).</summary>
    public void Restore(IEnumerable<AssetRecord> blocks)
    {
        foreach (var b in blocks)
        {
            _removed.Remove(b);
            if (!File.Assets.Contains(b))
                File.Assets.Add(b);
        }
    }

    /// <summary>A save deleted a removed block from the file: it leaves the session.</summary>
    public bool Forget(AssetRecord block) => _removed.Remove(block);

    /// <summary>The asset's values per extension (its blocks' current values); null when it has no block with a value.</summary>
    public static Dictionary<string, Dictionary<string, string>>? ValuesOf(GdtFile? gdt, string asset)
    {
        Dictionary<string, Dictionary<string, string>>? values = null;
        foreach (var b in gdt?.Extensions?.BlocksOf(asset) ?? new List<AssetRecord>())
            if (b.Properties.Count > 0)
                (values ??= new(StringComparer.OrdinalIgnoreCase))[b.Type] = new Dictionary<string, string>(b.Properties, StringComparer.OrdinalIgnoreCase);
        return values;
    }

    /// <summary>
    /// New blocks for <paramref name="asset"/> holding <paramref name="values"/>, as a duplicate (or a moved asset) starts
    /// with them: they read as unchanged, and a save writes them as new blocks.
    /// </summary>
    public void Add(string asset, IReadOnlyDictionary<string, Dictionary<string, string>> values)
    {
        foreach (var (id, props) in values)
        {
            if (props.Count == 0)
                continue;
            var block = new AssetRecord { Name = asset, Type = id, GdtName = File.Name };
            foreach (var (key, value) in props)
                block.Properties[key] = value;
            block.CaptureBaseline();
            File.Assets.Add(block);
        }
    }

    /// <summary>
    /// <see cref="Add"/> for a journal replaying a copy or a move: a block the file already has under the asset's name is
    /// that asset's (a save wrote the <c>.gdtx</c> and stopped before the GDT), so it takes the values instead of a
    /// second block being made beside it.
    /// </summary>
    public void AddReplayed(string asset, IReadOnlyDictionary<string, Dictionary<string, string>> values)
    {
        foreach (var (id, props) in values)
        {
            var saved = File.Assets.FirstOrDefault(b => b.Parent is null && b.Disk is { } d
                && d.Name.Equals(asset, StringComparison.OrdinalIgnoreCase) && b.Name.Equals(asset, StringComparison.OrdinalIgnoreCase)
                && b.Type.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (saved is null)
            {
                Add(asset, new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase) { [id] = props });
                continue;
            }
            saved.CaptureBaseline();
            var changed = false;
            foreach (var key in saved.Properties.Keys.Where(k => !props.ContainsKey(k)).ToList())
                changed |= EditHistory.Set(saved, key, null) is not null;
            foreach (var (key, value) in props)
                changed |= EditHistory.Set(saved, key, value) != value;
            if (changed)
                saved.HasSessionEdits = true;
        }
    }

    /// <summary>
    /// The type whose deffile declares <paramref name="key"/> for <paramref name="asset"/>, or null. Such a key is the
    /// GDT's; it never goes in a sidecar.
    /// </summary>
    public string? CoreType(string asset, string key)
    {
        var owner = Owner.Assets.FirstOrDefault(a => a.Name.Equals(asset, StringComparison.OrdinalIgnoreCase));
        return owner is not null && SchemaRegistry.Get(owner.Type)?.Find(key) is not null ? owner.Type : null;
    }

    /// <summary>
    /// The session's edits are discarded: each block reads as it did before the session touched it, and blocks the
    /// session made go. The file is untouched; the GDT drops its sidecar again when nothing is left of it.
    /// </summary>
    public void DiscardEdits()
    {
        File.Assets.RemoveAll(b => b.Disk is null);
        Restore(_removed.ToList());
        foreach (var b in File.Assets)
        {
            if (b.Disk is { } disk)
                b.Name = disk.Name;
            if (b.SessionBaseline is { } baseline && b.IsMaterialized)
            {
                b.Properties.Clear();
                foreach (var (k, v) in baseline)
                    b.Properties[k] = v;
            }
            b.HasSessionEdits = false;
        }
        if (File.FullPath is null && File.Assets.Count == 0 && Owner.Extensions == this)
            Owner.Extensions = null;
    }

    /// <summary>
    /// Blocks whose asset isn't in the GDT (renamed or deleted in APE, or the GDT replaced). They are kept as they are,
    /// byte for byte, until the user re-points or removes them.
    /// </summary>
    public IEnumerable<AssetRecord> Orphans()
    {
        var names = new HashSet<string>(Owner.Assets.Select(a => a.Name), StringComparer.OrdinalIgnoreCase);
        return File.Assets.Where(b => !names.Contains(b.Name));
    }

    private static readonly Dictionary<string, string> Empty = new(StringComparer.OrdinalIgnoreCase);
}
