using System;
using System.Collections.Generic;

namespace Apex.Editor.Models;

/// <summary>A single .gdt source file containing asset definitions.</summary>
public sealed class GdtFile
{
    public required string Name { get; init; }
    public List<AssetRecord> Assets { get; } = new();

    /// <summary>The file on disk; null for mock data and for a GDT created this session and not saved yet.</summary>
    public string? FullPath { get; set; }

    /// <summary>The file as Apex last read or wrote it (what a save expects to find). Null when not on disk.</summary>
    public Services.Save.GdtStamp? Stamp { get; set; }

    /// <summary>
    /// The extension data beside this GDT (<c>name.gdtx</c>). Null while there is none: no file on disk and no
    /// extension value set this session.
    /// </summary>
    public ExtensionSidecar? Extensions { get; set; }
}

/// <summary>
/// Lazily produces an asset's property dictionary — e.g. by re-reading a slice of a .gdt on disk.
/// Implemented by the GDT data layer so 430 MB of source text is never fully materialized up front.
/// </summary>
public interface IPropertySource
{
    /// <summary>Reads and decodes the backing property key/values (case-insensitive comparer).</summary>
    Dictionary<string, string> Materialize();
}

/// <summary>One asset definition inside a GDT (name + type + raw key/value properties).</summary>
public sealed class AssetRecord
{
    private Dictionary<string, string>? _properties;

    // Name/Parent are mutable so refactoring ops (rename with reference update) can rewrite them in place.
    public required string Name { get; set; }
    // Type is settable so derived assets can have it resolved from their root ancestor after indexing.
    public required string Type { get; set; }
    // Settable so "Move to…" can move a record between GDTs in place (its tab, history and references stay).
    public required string GdtName { get; set; }
    public string? Parent { get; set; }

    /// <summary>
    /// Optional lazy backing store. Records from mock mode / "new asset" leave this null and behave
    /// exactly as before — <see cref="Properties"/> is an ordinary empty dictionary populated eagerly.
    /// Settable so the read-only watcher can rebind a survivor's body slice when its .gdt changes on
    /// disk and the offsets shift (paired with <see cref="DropMaterialized"/>).
    /// </summary>
    public IPropertySource? Source { get; set; }

    /// <summary>
    /// The asset in its GDT as of the file's <see cref="GdtFile.Stamp"/>: name, parent and body offset there. Null for
    /// an asset created this session and not saved yet. A rename changes <see cref="Name"/> but not this, which is how
    /// a save finds the asset to rename.
    /// </summary>
    public Services.Save.DiskRef? Disk { get; set; }

    /// <summary>
    /// True while this record has unsaved changes. The live watcher uses it to preserve them when a .gdt changes on
    /// disk (edits win until saved).
    /// </summary>
    public bool HasSessionEdits { get; set; }

    /// <summary>A value was written this session (by any edit path). A save only diffs records with this set, or new ones.</summary>
    public bool ValuesTouched { get; set; }

    /// <summary>
    /// The property values as they were before this session touched the record — what "Changed"
    /// is measured against (as opposed to "off-default", which compares with the schema). Captured
    /// once, the first time an editor, table or compare view reaches the record, so closing and
    /// reopening a tab still shows what changed. Null until then.
    /// </summary>
    public IReadOnlyDictionary<string, string>? SessionBaseline { get; private set; }

    /// <summary>Snapshots <see cref="Properties"/> as the session baseline unless one exists already.</summary>
    public void CaptureBaseline() =>
        SessionBaseline ??= new Dictionary<string, string>(Properties, StringComparer.OrdinalIgnoreCase);

    /// <summary>After a save wrote this record: what is in the GDT now becomes what "changed" is measured against.</summary>
    public void RebaseAfterSave(IReadOnlyDictionary<string, string>? saved)
    {
        if (saved is not null && IsMaterialized)
            SessionBaseline = new Dictionary<string, string>(saved, StringComparer.OrdinalIgnoreCase);
        HasSessionEdits = false;
    }

    private EditHistory? _history;

    /// <summary>This record's undo history (every edit path records into it).</summary>
    public EditHistory History => _history ??= new EditHistory(this);

    /// <summary>
    /// Keys whose value differs from the session baseline — the record's share of the session's
    /// "N changes". On a root asset a key missing on one side reads as its schema default, so undoing an edit (or
    /// typing the old value back) brings the count back down. On a derived asset a missing key inherits, so gaining
    /// or losing a key is a change whatever its value (a save writes or removes that line).
    /// </summary>
    public int CountSessionChanges()
    {
        if (SessionBaseline is not { } baseline || !IsMaterialized)
            return 0;
        var count = 0;
        foreach (var (key, value) in Properties)
            if (baseline.TryGetValue(key, out var b) ? !Services.AssetQuery.ValuesEqual(value, b) : !SameAsMissing(key, value, exact: false))
                count++;
        foreach (var (key, value) in baseline)
            if (!Properties.ContainsKey(key) && !SameAsMissing(key, value, exact: false))
                count++;
        return count;
    }

    /// <summary>
    /// True when <paramref name="value"/> is what a missing <paramref name="key"/> means: the schema default on a root
    /// asset; never on a derived one (missing means inherited, a different line in the GDT).
    /// </summary>
    public bool SameAsMissing(string key, string value, bool exact)
    {
        if (Parent is not null)
            return false;
        var def = SchemaRegistry.Get(Type)?.Find(key)?.Default ?? "";
        return exact ? value == def : Services.AssetQuery.ValuesEqual(value, def);
    }

    /// <summary>
    /// Key/value properties, materialized on first access from <see cref="Source"/> (or an empty
    /// case-insensitive dictionary when there is no source). Once materialized the dictionary lives
    /// in memory for the session, so later in-memory edits persist.
    /// </summary>
    public Dictionary<string, string> Properties =>
        _properties ??= Source?.Materialize()
            ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>True once <see cref="Properties"/> has been materialized. Lets callers avoid forcing a lazy load.</summary>
    public bool IsMaterialized => _properties is not null;

    /// <summary>
    /// Properties for read-only bulk scans (e.g. filtering the whole ~95k corpus for
    /// <c>prop:</c>/<c>is:modified</c>) that must NOT permanently retain the result. Already-materialized
    /// records (open/edited assets, mock/new records) return their in-memory dictionary; otherwise the
    /// backing slice is read transiently and left uncached so a single filter can't balloon resident
    /// memory to the full corpus (§3.2). Never mutate the returned dictionary.
    /// </summary>
    public IReadOnlyDictionary<string, string> ScanProperties =>
        _properties ?? Source?.Materialize()
            ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Drops materialized properties so the next access re-reads from <see cref="Source"/>. Used by the
    /// watcher re-index path to refresh stale values. No-op for sourceless (mock/new) records, which
    /// would otherwise lose their in-memory data.
    /// </summary>
    public void DropMaterialized()
    {
        if (Source is not null)
            _properties = null;
    }
}

public sealed class AssetDatabase
{
    public required List<GdtFile> Gdts { get; init; }
    public required List<AssetRecord> Assets { get; init; }
}
