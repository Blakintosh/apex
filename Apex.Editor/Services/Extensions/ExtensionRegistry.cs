using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Apex.Editor.Models;

namespace Apex.Editor.Services.Extensions;

/// <summary>An extension's sections as one asset type gets them: fields its deffile already declares are left out.</summary>
public sealed class ExtensionSchema
{
    public required ExtensionManifest Manifest { get; init; }
    public required IReadOnlyList<ExtensionSection> Sections { get; init; }
    public string Id => Manifest.Id;
    public string? EnabledBy => Manifest.EnabledBy;
    public IEnumerable<ExtensionField> Fields => Sections.SelectMany(s => s.Fields);
    public IEnumerable<ExtensionRecordList> Records => Sections.SelectMany(s => s.Records);
}

/// <summary>
/// The installed extensions (<c>%AppData%\Apex\extensions</c>, or <c>APEX_EXTENSIONS_DIR</c>) and what each asset type
/// gets from them. Loaded once per window off the UI thread, alongside the deffiles; merged per type on first use and
/// remembered. With nothing installed every lookup is an empty list and the editor is exactly as without extensions.
/// </summary>
public static class ExtensionRegistry
{
    public const string DirVariable = "APEX_EXTENSIONS_DIR";

    /// <summary>Where extensions are installed: <c>APEX_EXTENSIONS_DIR</c> (tests, the harness), else the user's own.</summary>
    public static string Directory =>
        Environment.GetEnvironmentVariable(DirVariable) is { Length: > 0 } dir
            ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Apex", "extensions");

    private sealed class Loaded(List<ExtensionManifest> manifests, List<ExtensionDiagnostic> diagnostics)
    {
        public readonly List<ExtensionManifest> Manifests = manifests;
        public readonly List<ExtensionDiagnostic> Diagnostics = diagnostics;

        // UI thread only. Keyed by the core schema too: live schemas replace the mock ones once the deffiles are read.
        public readonly Dictionary<string, (AssetSchema Core, IReadOnlyList<ExtensionSchema> Merged)> ByType = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> Reported = new();
    }

    private static Loaded _loaded = new(new(), new());

    public static IReadOnlyList<ExtensionManifest> Manifests => _loaded.Manifests;

    /// <summary>What loading and merging had to say, in the order it was found.</summary>
    public static IReadOnlyList<ExtensionDiagnostic> Diagnostics => _loaded.Diagnostics;

    /// <summary>Reads every manifest under <paramref name="dir"/> (default <see cref="Directory"/>). Any thread; never throws.</summary>
    public static void Load(string? dir = null)
    {
        var (manifests, diagnostics) = ExtensionLoader.LoadAll(dir ?? Directory);
        Volatile.Write(ref _loaded, new Loaded(manifests, diagnostics));
    }

    /// <summary>No extensions (tests).</summary>
    public static void Clear() => Volatile.Write(ref _loaded, new Loaded(new(), new()));

    /// <summary>
    /// The extensions that add to <paramref name="type"/>, merged with its deffile schema: a field whose key the deffile
    /// declares is left out (that key belongs in the GDT, which APE rewrites without extension keys), as is a field
    /// another extension already adds. An extension whose on/off key is a deffile key is left out of the type whole.
    /// UI thread.
    /// </summary>
    public static IReadOnlyList<ExtensionSchema> For(string type)
    {
        var loaded = Volatile.Read(ref _loaded);
        if (loaded.Manifests.Count == 0 || SchemaRegistry.Get(type) is not { } core)
            return Array.Empty<ExtensionSchema>();
        if (loaded.ByType.TryGetValue(type, out var cached) && ReferenceEquals(cached.Core, core))
            return cached.Merged;

        var merged = new List<ExtensionSchema>();
        var taken = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var takenLists = new List<(ExtensionRecordList List, string Id)>();
        foreach (var m in loaded.Manifests)
        {
            if (!m.AddsTo(type))
                continue;
            if (m.EnabledBy is { } flag && core.Find(flag) is not null)
            {
                Report(loaded, m.Id, ExtensionProblem.Disabled,
                    $"its enabledBy key {flag} is a {type} key (its deffile declares it), so the extension is left out of {type} assets.");
                continue;
            }
            var sections = new List<ExtensionSection>();
            foreach (var s in m.Sections)
            {
                var fields = new List<ExtensionField>(s.Fields.Count);
                foreach (var f in s.Fields)
                {
                    var other = taken.GetValueOrDefault(f.Def.Key) ?? takenLists.FirstOrDefault(t => t.List.NumberOf(f.Def.Key) is not null).Id;
                    if (core.Find(f.Def.Key) is not null)
                        Report(loaded, m.Id, ExtensionProblem.Skipped,
                            $"{f.Def.Key} is a {type} key (its deffile declares it), so its field is left out of {type} assets: that value belongs in the GDT.");
                    else if (other is not null)
                        Report(loaded, m.Id, ExtensionProblem.Skipped,
                            $"{f.Def.Key} is also a field of {other}, so {m.Id}'s is left out of {type} assets.");
                    else
                        fields.Add(f);
                }
                var records = new List<ExtensionRecordList>(s.Records.Count);
                foreach (var r in s.Records)
                {
                    // A deffile key the list would number (bulletweapon's own "foo1" for a "foo#") belongs to the GDT.
                    if (core.Properties.FirstOrDefault(p => r.NumberOf(p.Key) is not null) is { } clash)
                        Report(loaded, m.Id, ExtensionProblem.Skipped,
                            $"{r.Def.Key} would hold {clash.Key}, a {type} key (its deffile declares it), so the list is left out of {type} assets.");
                    else if (taken.Keys.FirstOrDefault(k => r.NumberOf(k) is not null) is { } field)
                        Report(loaded, m.Id, ExtensionProblem.Skipped,
                            $"{r.Def.Key} would hold {field}, a field of {taken[field]}, so {m.Id}'s list is left out of {type} assets.");
                    else if (takenLists.FirstOrDefault(t => t.List.Base.Equals(r.Base, StringComparison.OrdinalIgnoreCase)
                                 || r.NumberOf(t.List.Base) is not null || t.List.NumberOf(r.Base) is not null) is { List: not null } list)
                        Report(loaded, m.Id, ExtensionProblem.Skipped,
                            $"{r.Def.Key}'s keys are also {list.Id}'s {list.List.Def.Key}, so {m.Id}'s list is left out of {type} assets.");
                    else
                        records.Add(r);
                }
                if (fields.Count > 0 || records.Count > 0)
                    sections.Add(fields.Count == s.Fields.Count && records.Count == s.Records.Count
                        ? s
                        : new ExtensionSection
                        {
                            Title = s.Title, Rule = s.Rule, Fields = fields, Records = records,
                            CollapsedUnlessSet = s.CollapsedUnlessSet, Notice = s.Notice,
                        });
            }
            foreach (var s in sections)
            {
                foreach (var f in s.Fields)
                    taken[f.Def.Key] = m.Id;
                foreach (var r in s.Records)
                    takenLists.Add((r, m.Id));
            }
            if (sections.Count > 0)
                merged.Add(new ExtensionSchema { Manifest = m, Sections = sections });
        }
        loaded.ByType[type] = (core, merged);
        return merged;
    }

    /// <summary>
    /// Problems in the asset's own extension values, counted as the editor lists them (a field with a bad value is one,
    /// a record table with any bad row is one), for the Explorer's ⚠ count beside the deffile's. Reads only the asset's
    /// blocks. Like <see cref="Validator.CountProblems"/>, inherited values aren't the asset's and don't count. UI thread.
    /// </summary>
    public static int CountProblems(AssetRecord asset, ExtensionSidecar? sidecar, Func<string, string, bool> exists)
    {
        if (sidecar is null || Volatile.Read(ref _loaded).Manifests.Count == 0)
            return 0;
        var count = 0;
        foreach (var schema in For(asset.Type))
        {
            if (sidecar.Find(asset.Name, schema.Id) is not { } block)
                continue;
            var values = block.Properties;
            foreach (var f in schema.Fields)
                if (values.TryGetValue(f.Def.Key, out var v)
                    && (f.Parts is { } parts ? PartsCodec.Problems(parts, v, exists).Count > 0 : Validator.Check(f.Def, v, exists, files: false) is not null))
                    count++;
            foreach (var list in schema.Records)
            {
                var rows = RecordCodec.Rows(values, list).Select(r => RecordCodec.Parse(list, r.Value)).ToList();
                if (rows.Count > 0 && (list.MaxRows is { } max && rows.Count > max
                        || RecordCodec.Repeats(list, rows).Any(r => r >= 0) || rows.Any(r => RecordCodec.Problems(list, r, exists).Count > 0)))
                    count++;
            }
        }
        return count;
    }

    /// <summary>Merges every type an extension targets now, so what merging leaves out is known before an asset opens. UI thread.</summary>
    public static void CheckTargets()
    {
        if (Volatile.Read(ref _loaded).Manifests.Count == 0)
            return;
        foreach (var type in SchemaRegistry.TypeNames.ToList())
            For(type);
    }

    private static void Report(Loaded loaded, string id, ExtensionProblem problem, string message)
    {
        // A field left out of bulletweapon and of projectileweapon is two lines, but the same line is said once.
        if (loaded.Reported.Add(id + "|" + message))
            loaded.Diagnostics.Add(new ExtensionDiagnostic(id, problem, message));
    }
}
