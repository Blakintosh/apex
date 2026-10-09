using System;
using System.Collections.Generic;
using System.Linq;
using Apex.Editor.Models;

namespace Apex.Editor.Services.Extensions;

/// <summary>
/// One installed extension, as its <c>extension.json</c> describes it: data that adds sections and fields to existing
/// asset types. Its values live in the GDT's <c>.gdtx</c> (<see cref="ExtensionSidecar"/>), never in the GDT.
/// </summary>
public sealed class ExtensionManifest
{
    public required string Id { get; init; }
    public string Version { get; init; } = "";

    /// <summary>The extension's name in Apex's own words ("Turn on Weapon tech for 3 assets"); the id when it has none.</summary>
    public string Title { get; init; } = "";

    public string DisplayTitle => Title.Length > 0 ? Title : Id;

    /// <summary>The line at the top of a form whose asset has the extension off (with a Turn on beside it); null for none.</summary>
    public string? OffNotice { get; init; }

    /// <summary>A line under the extension's first section header while it is on; null for none.</summary>
    public string? Notice { get; init; }

    /// <summary>
    /// Where the extension's values are consumed, as a probe of the install (<see cref="ExtensionConsumerProbe"/>): decides
    /// whether <see cref="Notice"/> shows and which line says so; null when the manifest names none, and the notice always shows.
    /// </summary>
    public ExtensionConsumer? Consumer { get; init; }

    /// <summary>Text the extension's values copy out as (a palette command and a button on its first header); null for none.</summary>
    public ExtensionExport? Export { get; init; }

    /// <summary>The manifest file, for the provenance tooltip.</summary>
    public required string Path { get; init; }

    public required IReadOnlyList<string> Targets { get; init; }

    /// <summary>The per-asset on/off key; null when the extension is always on.</summary>
    public string? EnabledBy { get; init; }

    public required IReadOnlyList<ExtensionSection> Sections { get; init; }

    /// <summary>What the loader set aside in this manifest (unknown members, rules that didn't parse).</summary>
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();

    /// <summary>
    /// The preview-simulator module the manifest names (<c>simulator.module</c>), its path already checked to stay inside
    /// the extension's folder; null when it names none or the path was refused. Naming it loads nothing: the file is
    /// hashed, asked about and loaded only when a preview first asks (<see cref="Simulation.SimulatorHost"/>).
    /// </summary>
    public SimulatorModuleRef? Simulator { get; init; }

    /// <summary>
    /// Whether the extension adds to <paramref name="type"/>. A target names a type as the GDTs do (bulletweapon), or is
    /// <c>weapon</c>: every weapon type (bulletweapon, projectileweapon, dualwieldweapon…), so a manifest needn't list
    /// them and a weapon type a later deffile adds is covered.
    /// </summary>
    public bool AddsTo(string type)
    {
        foreach (var t in Targets)
            if (t.Equals(type, StringComparison.OrdinalIgnoreCase)
                || t.Equals(WeaponFamily, StringComparison.OrdinalIgnoreCase) && IsWeapon(type))
                return true;
        return false;
    }

    /// <summary><c>weapon</c>, in a target or an assetRef's refType: every weapon type.</summary>
    public const string WeaponFamily = "weapon";

    /// <summary>A weapon type (bulletweapon, dualwieldweapon, the mock's weapon…), not weaponcamo or sharedweaponsounds.</summary>
    public static bool IsWeapon(string type) => type.EndsWith(WeaponFamily, StringComparison.OrdinalIgnoreCase);
}

/// <summary>A manifest's <c>simulator.module</c>: as written (<see cref="Module"/>) and resolved inside the extension's folder.</summary>
public sealed record SimulatorModuleRef(string Module, string FullPath, string Folder);

/// <summary>
/// A manifest's <c>export</c>: an asset's values as text for the clipboard. <see cref="Format"/> is <c>ini-section</c>
/// (the only one): <see cref="Header"/> with <c>{asset}</c> replaced by the asset's name, then a <c>key = value</c> line per
/// key that has a value. <see cref="Command"/> names the palette command and the header's button.
/// </summary>
public sealed record ExtensionExport(string Format, string Header, string Command)
{
    public const string IniSection = "ini-section";
}

/// <summary>A section of an extension's fields: its own header in the editor, after the deffile's sections.</summary>
public sealed class ExtensionSection
{
    public required string Title { get; init; }

    /// <summary>Null when the section always shows (no rule, or one that didn't parse).</summary>
    public VisibleWhen? Rule { get; init; }

    public required IReadOnlyList<ExtensionField> Fields { get; init; }

    /// <summary>Record lists (numbered keys, one record per key), after the flat fields unless one names a field to follow.</summary>
    public IReadOnlyList<ExtensionRecordList> Records { get; init; } = Array.Empty<ExtensionRecordList>();

    /// <summary>
    /// The section's rows in the form's order: the fields in order, each followed by the lists placed after it
    /// (<see cref="ExtensionRecordList.After"/>), then every other list. A list whose field isn't in this section (left out
    /// for a type whose deffile declares it, say) goes last.
    /// </summary>
    public IEnumerable<(ExtensionField? Field, ExtensionRecordList? List)> Items()
    {
        foreach (var field in Fields)
        {
            yield return (field, null);
            foreach (var list in Records)
                if (list.After is { } after && after.Equals(field.Def.Key, StringComparison.OrdinalIgnoreCase))
                    yield return (null, list);
        }
        foreach (var list in Records)
            if (list.After is not { } after || !Fields.Any(f => f.Def.Key.Equals(after, StringComparison.OrdinalIgnoreCase)))
                yield return (null, list);
    }

    /// <summary>
    /// Shown folded to its header while none of its rows holds a value (own or inherited); it opens by itself when one
    /// does. Folding is the view's, for the session: never saved, never a setting.
    /// </summary>
    public bool CollapsedUnlessSet { get; init; }

    /// <summary>A line under the section's header; null for none.</summary>
    public string? Notice { get; init; }
}

/// <summary>
/// A record list: numbered keys (<c>wtKick1</c>, <c>wtKick2</c>…) whose values are records of comma-joined columns,
/// edited as one table. Row n is key <see cref="Base"/>n; rows keep their numbers' order, and a save numbers them 1..N.
/// </summary>
public sealed class ExtensionRecordList
{
    /// <summary>The table's row: Key is the pattern as the manifest spells it (<c>wtKick#</c>), Category the section title.</summary>
    public required PropertyDef Def { get; init; }

    /// <summary>The keys' stem: <c>wtKick</c> for <c>wtKick#</c>.</summary>
    public required string Base { get; init; }

    /// <summary>The most rows the extension reads; null when it sets none.</summary>
    public int? MaxRows { get; init; }

    public required IReadOnlyList<RecordColumn> Columns { get; init; }

    /// <summary>Rules that, when true for a row, put a problem on it.</summary>
    public IReadOnlyList<RecordCheck> Checks { get; init; } = Array.Empty<RecordCheck>();

    /// <summary>Columns no two rows may share all of (each empty column read as its default); empty for none.</summary>
    public IReadOnlyList<RecordColumn> Unique { get; init; } = Array.Empty<RecordColumn>();

    public VisibleWhen? Rule { get; init; }

    /// <summary>The key of the section's field this table sits right after in the form (<c>after</c>); null: after every field.</summary>
    public string? After { get; init; }

    /// <summary>Groups of stored columns the table shows as one labelled choice (<c>combine</c>); empty for none.</summary>
    public IReadOnlyList<RecordCombine> Combines { get; init; } = Array.Empty<RecordCombine>();

    private IReadOnlyList<RecordDisplayColumn>? _display;

    /// <summary>
    /// The table's columns: each stored column, except that a combined group shows as one choice where its first column
    /// was and a hidden column doesn't show. Without either, one per stored column with its own definition.
    /// </summary>
    public IReadOnlyList<RecordDisplayColumn> DisplayColumns => _display ??= BuildDisplay();

    private List<RecordDisplayColumn> BuildDisplay()
    {
        var display = new List<RecordDisplayColumn>(Columns.Count);
        for (var i = 0; i < Columns.Count; i++)
        {
            if (Columns[i].Hidden)
                continue;
            if (Combines.FirstOrDefault(c => c.Columns.Contains(i)) is { } combine)
            {
                if (combine.Columns[0] == i)
                    display.Add(new RecordDisplayColumn { Def = combine.Def, Combine = combine });
                continue;
            }
            display.Add(new RecordDisplayColumn { Def = Columns[i].Def, Column = i });
        }
        return display;
    }

    /// <summary>Row <paramref name="number"/>'s key.</summary>
    public string KeyOf(int number) => Base + number.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The row number <paramref name="key"/> stands for (any digits after the stem), or null when it isn't one of the list's keys.</summary>
    public int? NumberOf(string key)
    {
        if (key.Length <= Base.Length || !key.StartsWith(Base, StringComparison.OrdinalIgnoreCase))
            return null;
        var n = 0;
        for (var i = Base.Length; i < key.Length; i++)
        {
            if (key[i] is < '0' or > '9' || n > 100_000_000)
                return null;
            n = n * 10 + (key[i] - '0');
        }
        return n;
    }
}

/// <summary>One column of a record: a field between commas, edited like a flat field of its kind.</summary>
public sealed class RecordColumn
{
    /// <summary>The name rules and checks read it by.</summary>
    public required string Name { get; init; }

    /// <summary>The cell's editor: Kind, Label, Description, Default (what an empty field means), range and choices.</summary>
    public required PropertyDef Def { get; init; }

    /// <summary>Text the field starts with in the record (<c>slot:</c>); the cell edits what follows it.</summary>
    public string Prefix { get; init; } = "";

    /// <summary>Found by its prefix anywhere after the positional fields and written last (<c>side:left</c>); omitted when empty.</summary>
    public bool Named { get; init; }

    /// <summary>May be left out at the end of the record (a trailing run of empty optional fields isn't written).</summary>
    public bool Optional { get; init; }

    /// <summary>
    /// Never shown (<c>hidden</c>): a field the record format needs and nobody edits. A new row holds its default; a
    /// stored or pasted value is kept as written. Always has a default.
    /// </summary>
    public bool Hidden { get; init; }
}

/// <summary>
/// Stored columns shown as one choice (<c>combine</c>): two switches that together mean four modes read better as
/// "Applies to: ADS gun". <see cref="Def"/> is that choice: each value is the columns' values joined by commas (a record
/// field can't hold one, so splitting it back is exact), each label what the table shows.
/// </summary>
public sealed class RecordCombine
{
    public required string Name { get; init; }
    public required PropertyDef Def { get; init; }

    /// <summary>The stored columns it stands for, as indexes into the list's columns, in the manifest's order.</summary>
    public required IReadOnlyList<int> Columns { get; init; }
}

/// <summary>One column of the table as shown: a stored column (<see cref="Column"/>), or a combined group.</summary>
public sealed class RecordDisplayColumn
{
    public required PropertyDef Def { get; init; }

    /// <summary>The stored column's index; -1 for a combined group.</summary>
    public int Column { get; init; } = -1;

    public RecordCombine? Combine { get; init; }
}

/// <summary>A row problem: <see cref="Rule"/> reads the row's columns by name; true puts <see cref="Message"/> on the row.</summary>
public sealed record RecordCheck(VisibleWhen Rule, string Message);

/// <summary>One flat extension field: a key with an editor, edited by the same rows as deffile properties.</summary>
public sealed class ExtensionField
{
    /// <summary>Category is the section title; Extension is the extension's id.</summary>
    public required PropertyDef Def { get; init; }

    public VisibleWhen? Rule { get; init; }

    /// <summary>
    /// The named parts of a value that is a comma-joined list (<c>parts</c>), each edited on a row of its own: a part's
    /// Key is its name, and its Label, Kind, range, default and choices are its editor's. Null for a plain field.
    /// </summary>
    public IReadOnlyList<PropertyDef>? Parts { get; init; }
}

public enum ExtensionProblem
{
    /// <summary>Something in a manifest was ignored; everything else loaded.</summary>
    Note,

    /// <summary>A field or section was left out.</summary>
    Skipped,

    /// <summary>The whole extension was left out.</summary>
    Disabled,
}

/// <summary>What loading or merging an extension had to say, for the startup notice.</summary>
public sealed record ExtensionDiagnostic(string Extension, ExtensionProblem Problem, string Message)
{
    public override string ToString() => $"{Extension}: {Message}";
}
