using System;
using System.Collections.Generic;
using System.Linq;
using Apex.Editor.Models;
using Apex.Editor.Services;
using Apex.Editor.Services.Extensions;
using Apex.Editor.Services.Extensions.Simulation;

namespace Apex.Editor.ViewModels;

/// <summary>
/// Extension fields (see <see cref="ExtensionRegistry"/>): their sections follow the deffile's, and their rows are the
/// same rows as any property, but their values are the asset's block in its GDT's <c>.gdtx</c>, never the record or the
/// GDT. A derived asset inherits them from its parents' blocks, wherever those parents' GDTs are. Without extensions
/// none of this runs: <see cref="_extensions"/> stays null and no row is an extension row.
/// </summary>
public sealed partial class AssetEditorViewModel
{
    private readonly Func<AssetRecord, GdtFile?>? _gdtOf;

    /// <summary>The GDT whose sidecar holds this asset's extension data.</summary>
    private GdtFile? _extensionGdt;

    private List<ExtensionRows>? _extensions;
    private readonly Dictionary<PropertyItemViewModel, ExtensionRows> _extensionOf = new();
    private bool _extensionRulesPending;

    /// <summary>One extension on this asset: its sections, rows, baseline and what the parent chain gives it.</summary>
    private sealed class ExtensionRows(ExtensionSchema schema)
    {
        public readonly ExtensionSchema Schema = schema;
        public readonly List<(CategoryViewModel Section, ExtensionSection Def)> Sections = new();
        public readonly Dictionary<PropertyItemViewModel, ExtensionField> Fields = new();
        public readonly Dictionary<PropertyItemViewModel, ExtensionRecordList> Records = new();
        public PropertyItemViewModel? Switch;

        /// <summary>On for the asset (its switch, own or inherited), as the rules last read it; true with no switch.</summary>
        public bool IsOn = true;

        /// <summary>The line at the top of the form while the extension is off; null when the manifest has none.</summary>
        public ExtensionOffRowViewModel? OffRow;

        /// <summary>Notice lines under section headers (the manifest's, then a section's own), shown while it is on.</summary>
        public readonly List<(CategoryViewModel Section, ExtensionNoticeRowViewModel Row)> Notices = new();

        /// <summary>Every row of the extension, flat fields and record tables.</summary>
        public IEnumerable<PropertyItemViewModel> Rows => Fields.Keys.Concat(Records.Keys);

        public VisibleWhen? RuleOf(PropertyItemViewModel item) =>
            Fields.TryGetValue(item, out var f) ? f.Rule : Records.TryGetValue(item, out var r) ? r.Rule : null;

        /// <summary>Whether <paramref name="key"/> is one of this extension's rows' keys (a record list's numbered keys too).</summary>
        public bool Covers(string key) =>
            Rows.Any(r => r.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) || Records.Values.Any(l => l.NumberOf(key) is not null);

        /// <summary>The block's values before this session touched them (empty without a block).</summary>
        public IReadOnlyDictionary<string, string> Baseline = NoValues;

        /// <summary>
        /// Each key's value on the nearest ancestor whose block has it; a record list (under its <c>wtKick#</c> key)
        /// is the nearest ancestor's whole table.
        /// </summary>
        public Dictionary<string, string> Inherited = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>What a key the block doesn't hold reads as: inherited, else the manifest's default.</summary>
        public string Unowned(PropertyDef def) => Inherited.GetValueOrDefault(def.Key) ?? def.Default;
    }

    private static readonly Dictionary<string, string> NoValues = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Adds a section per extension section for this asset's type, with a row per field (constructor).</summary>
    private void AddExtensionSections(List<PropertyItemViewModel> allProps, Action<string, string> navigateToRef)
    {
        if (_gdtOf is null || ExtensionRegistry.For(Record.Type) is not { Count: > 0 } schemas || _gdtOf(Record) is not { } gdt)
            return;
        _extensionGdt = gdt;
        _extensions = new List<ExtensionRows>(schemas.Count);
        var sidecar = ExtensionSidecar.PathFor(System.IO.Path.GetFileName(gdt.Name));
        foreach (var schema in schemas)
        {
            var ext = new ExtensionRows(schema);
            var block = gdt.Extensions?.Find(Record.Name, schema.Id);
            block?.CaptureBaseline();
            ext.Baseline = block?.SessionBaseline ?? NoValues;
            ext.Inherited = InheritedExtensionValues(schema);
            var tip = ExtensionTip(schema.Manifest, sidecar);
            var manifest = schema.Manifest;
            foreach (var section in schema.Sections)
            {
                var items = new List<PropertyItemViewModel>(section.Fields.Count + section.Records.Count);
                foreach (var (field, list) in section.Items())
                {
                    PropertyItemViewModel item;
                    if (field is not null)
                    {
                        var value = block?.Properties.GetValueOrDefault(field.Def.Key) ?? ext.Unowned(field.Def);
                        item = field.Parts is { } parts
                            ? new PartsPropertyViewModel(field.Def, parts, value, AssetExists)
                            : Create(field.Def, value, navigateToRef, ValueOf, AssetExists);
                        ext.Fields[item] = field;
                        if (field.Def.Key.Equals(schema.EnabledBy, StringComparison.OrdinalIgnoreCase))
                            ext.Switch = item;
                    }
                    else
                    {
                        item = new RecordsPropertyViewModel(list!, OwnRecords(block?.Properties, list!) ?? ext.Unowned(list!.Def), navigateToRef, AssetExists);
                        ext.Records[item] = list!;
                    }
                    item.Edited += OnPropertyEdited;
                    allProps.Add(item);
                    items.Add(item);
                    _extensionOf[item] = ext;
                }
                var first = ext.Sections.Count == 0;
                var category = new CategoryViewModel(section.Title, items)
                {
                    Extension = schema.Id,
                    ExtensionTip = tip,
                    ExtensionSearch = $"{schema.Id} {schema.Id.Replace('-', ' ').Replace('_', ' ')} {manifest.Title}",
                    IsCollapsible = section.CollapsedUnlessSet,
                    ExportCommand = first && manifest.Export is not null
                        ? new CommunityToolkit.Mvvm.Input.RelayCommand(() => Owner?.CopyExtensionExport(this, manifest))
                        : null,
                    ExportTip = first && manifest.Export is { } export
                        ? $"Copies {Record.Name}'s values as {export.Header.Replace("{asset}", Record.Name)}: its own and the ones it inherits."
                        : null,
                };
                category.ExpandedChanged += _ => RefreshVisible();
                _categories.Add(category);
                ext.Sections.Add((category, section));
                if (first && ExtensionConsumerProbe.NoticeFor(manifest, FieldFiles.Root) is { } notice)
                    ext.Notices.Add((category, new ExtensionNoticeRowViewModel(notice)));
                if (section.Notice is { } own)
                    ext.Notices.Add((category, new ExtensionNoticeRowViewModel(own)));
            }
            if (ext.Switch is { } toggle && manifest.OffNotice is { } off)
                ext.OffRow = new ExtensionOffRowViewModel(off, () => TurnOn(toggle));
            _extensions.Add(ext);
        }
    }

    /// <summary>Turn on (the off line): the switch goes on as if clicked, and the keyboard lands on it, where the extension's rows now are.</summary>
    private void TurnOn(PropertyItemViewModel toggle)
    {
        toggle.RawValue = "1";
        RevealProperty(toggle.Key);
    }

    /// <summary>The off lines the form starts with: one per extension that is off for the asset, in the plain form only.</summary>
    private IEnumerable<object> ExtensionOffRows()
    {
        if (View != EditorView.All)
            yield break;
        foreach (var ext in _extensions!)
            if (!ext.IsOn && ext.OffRow is { } row)
                yield return row;
    }

    /// <summary>The notice lines under a section's header, while its extension is on.</summary>
    private IEnumerable<object> ExtensionNoticeRows(CategoryViewModel section)
    {
        foreach (var ext in _extensions!)
            if (ext.IsOn)
                foreach (var (owner, row) in ext.Notices)
                    if (owner == section)
                        yield return row;
    }

    /// <summary>
    /// After the rows are in place: the off and notice lines get places in the form's order, sections that fold while
    /// empty start folded unless they hold something, and the export follows the switch.
    /// </summary>
    private void PlaceExtensionRows()
    {
        if (_extensions is null)
            return;
        var order = 1;
        foreach (var ext in _extensions)
        {
            if (ext.OffRow is { } off)
                _rowOrder[off] = order++;
            var offsets = new Dictionary<CategoryViewModel, int>();
            foreach (var (section, row) in ext.Notices)
                _rowOrder[row] = _rowOrder[section] + (offsets[section] = offsets.GetValueOrDefault(section) + 1);
            foreach (var (section, _) in ext.Sections)
                if (section.IsCollapsible)
                {
                    section.SetCount = SetCount(ext, section);
                    section.SetExpanded(section.SetCount > 0);
                }
        }
    }

    /// <summary>Rows of a section that hold a value, the asset's own or inherited.</summary>
    private int SetCount(ExtensionRows ext, CategoryViewModel section)
    {
        var own = _extensionGdt?.Extensions?.Find(Record.Name, ext.Schema.Id)?.Properties;
        return section.All.Count(p => Holds(ext, own, p) || ext.Inherited.ContainsKey(p.Key));
    }

    /// <summary>
    /// Counts again what a folding section holds; one that had nothing and now has something opens by itself (a value
    /// that appears is never left folded away). True when one opened.
    /// </summary>
    private bool RecountSet(ExtensionRows ext, CategoryViewModel? only = null)
    {
        var opened = false;
        foreach (var (section, _) in ext.Sections)
        {
            if (!section.IsCollapsible || only is not null && section != only)
                continue;
            var was = section.SetCount;
            section.SetCount = SetCount(ext, section);
            if (was == 0 && section.SetCount > 0 && section.IsCollapsed)
            {
                section.SetExpanded(true);
                opened = true;
            }
        }
        return opened;
    }

    /// <summary>An extension's values as its export writes them (null when it has no export or isn't on this asset's type).</summary>
    public string? ExportText(string extensionId)
    {
        var ext = _extensions?.FirstOrDefault(e => e.Schema.Id.Equals(extensionId, StringComparison.OrdinalIgnoreCase));
        if (ext?.Schema.Manifest.Export is not { } export)
            return null;
        var own = _extensionGdt?.Extensions?.Find(Record.Name, ext.Schema.Id)?.Properties;
        return ExtensionExporter.Write(export, Record.Name, ext.Schema, ext.Schema.EnabledBy,
            key => own?.GetValueOrDefault(key) ?? ext.Inherited.GetValueOrDefault(key),
            list => OwnRecords(own, list) ?? ext.Inherited.GetValueOrDefault(list.Def.Key));
    }

    /// <summary>The extensions with an export that are on for this asset (the palette offers each).</summary>
    public IEnumerable<ExtensionManifest> Exports =>
        _extensions?.Where(e => e.IsOn && e.Schema.Manifest.Export is not null).Select(e => e.Schema.Manifest) ?? Enumerable.Empty<ExtensionManifest>();

    private static string ExtensionTip(ExtensionManifest m, string sidecar)
    {
        var version = m.Version.Length > 0 ? $" {m.Version}" : "";
        var tip = $"Added by the {m.Id}{version} extension ({m.Path}). These values are saved in {sidecar} beside the GDT; APE doesn't read that file.";
        if (m.Notes.Count > 0)
            tip += "\n\nApex set aside part of its manifest:\n" + ExtensionLoader.Listed(m.Notes.Select(n => "· " + n).ToList());
        return tip;
    }

    private void InitExtensionRow(PropertyItemViewModel item, ExtensionRows ext)
    {
        if (Record.Parent is not null)
            item.InitProvenance(ext.Inherited.GetValueOrDefault(item.Key));
        item.InitBaseline(BaselineOf(ext, item));
    }

    /// <summary>The row's value when the session began: the block's then, else what it inherits, else the default.</summary>
    private static string BaselineOf(ExtensionRows ext, PropertyItemViewModel item) =>
        (ext.Records.TryGetValue(item, out var list) ? OwnRecords(ext.Baseline, list) : ext.Baseline.GetValueOrDefault(item.Key))
        ?? ext.Unowned(item.Def);

    /// <summary>
    /// A block's rows of <paramref name="list"/>, joined; null when it has none of its keys. A table is inherited
    /// whole: an asset with any row of its own has exactly its own rows.
    /// </summary>
    private static string? OwnRecords(IReadOnlyDictionary<string, string>? values, ExtensionRecordList list)
    {
        if (values is null)
            return null;
        var rows = RecordCodec.Rows(values, list);
        return rows.Count == 0 ? null : RecordCodec.Join(rows.Select(r => r.Value));
    }

    /// <summary>Whether a block's values hold the row's key (any of a record list's keys).</summary>
    private static bool Holds(ExtensionRows ext, IReadOnlyDictionary<string, string>? values, PropertyItemViewModel item) =>
        values is not null && (ext.Records.TryGetValue(item, out var list) ? OwnRecords(values, list) is not null : values.ContainsKey(item.Key));

    /// <summary>
    /// Each key's value on the nearest ancestor that has it in its own GDT's sidecar, and each record list's nearest
    /// table. Reads only the blocks of the assets on the chain, never a whole sidecar or GDT.
    /// </summary>
    private Dictionary<string, string> InheritedExtensionValues(ExtensionSchema schema)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lists = schema.Records.ToList();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Record.Name };
        for (var cur = Record; cur.Parent is { } p && seen.Add(p) && _resolve(Record.Type, p) is { } parent; cur = parent)
            if (_gdtOf?.Invoke(parent)?.Extensions?.Find(parent.Name, schema.Id) is { } block)
            {
                foreach (var (key, value) in block.Properties)
                    values.TryAdd(key, value);
                foreach (var list in lists)
                    if (!values.ContainsKey(list.Def.Key) && OwnRecords(block.Properties, list) is { } rows)
                        values[list.Def.Key] = rows;
            }
        return values;
    }

    /// <summary>What a row shows: the asset's own value, else what it inherits, else the default.</summary>
    private string ExtensionValue(ExtensionRows ext, PropertyItemViewModel item)
    {
        if (!ext.Records.TryGetValue(item, out var list))
            return _extensionGdt?.Extensions?.Get(Record.Name, ext.Schema.Id, item.Key) ?? ext.Unowned(item.Def);
        return OwnRecords(_extensionGdt?.Extensions?.Find(Record.Name, ext.Schema.Id)?.Properties, list) ?? ext.Unowned(item.Def);
    }

    /// <summary>
    /// Writes an edit to the asset's block and records it in the asset's history (so Ctrl+Z reaches it). As for a GDT
    /// key on a derived asset, a value brought back to what the key would read without it, on a key the block didn't
    /// hold when the session began, is dropped rather than kept as a copy: a sidecar holds only what differs.
    /// </summary>
    private void WriteExtensionEdit(PropertyItemViewModel item, ExtensionRows ext, string after)
    {
        if (ext.Records.TryGetValue(item, out var list))
        {
            WriteRecords((RecordsPropertyViewModel)item, list, ext, after);
            return;
        }
        var key = item.Key;
        var unowned = ext.Unowned(item.Def);
        var write = !ext.Baseline.ContainsKey(key) && after == unowned ? null : after;
        var sidecar = ExtensionSidecar.Of(_extensionGdt!);
        var before = sidecar.Get(Record.Name, ext.Schema.Id, key);
        sidecar.Set(Record.Name, ext.Schema.Id, key, write);
        if ((before ?? unowned) == after)
            return;
        Record.History.RecordEdit(key, before, write, item.IsContinuousEdit, new ExtensionTarget(_gdtOf!, ext.Schema.Id));
        NotifyHistoryChanged();
        Owner?.OnNewValueEdit();
    }

    /// <summary>
    /// A table's edit: its rows become the list's keys, numbered 1..N in row order, and keys past the last row go. One
    /// undo step, which a run of steps in one cell (one key) coalesces into like any field's. As for a field, a derived
    /// asset brought back to the table it inherits, on a list it held no row of when the session began, inherits again.
    /// </summary>
    private void WriteRecords(RecordsPropertyViewModel item, ExtensionRecordList list, ExtensionRows ext, string after)
    {
        var unowned = ext.Unowned(item.Def);
        var rows = !Holds(ext, ext.Baseline, item) && after == unowned ? new List<string>() : RecordCodec.Split(after);
        var changes = SetRecords(ext, list, RecordCodec.Keys(list, rows));
        if (changes.Count == 1)
            Record.History.RecordEdit(changes[0].Key, changes[0].Before, changes[0].After, item.IsContinuousEdit, changes[0].Extension);
        else if (changes.Count > 1)
            Record.History.RecordStep(new EditStep(changes));
        if (changes.Count > 0)
        {
            NotifyHistoryChanged();
            Owner?.OnNewValueEdit();
        }
        // No rows of its own on a derived asset is no table of its own: it shows what it inherits again.
        var shown = ExtensionValue(ext, item);
        if (shown == after)
            return;
        _squelchHistory = true;
        try
        {
            item.RawValue = shown;
        }
        finally
        {
            _squelchHistory = false;
        }
        if (Owner is { } owner)
            owner.Status = $"{item.Label} has no rows of its own now, so it shows {Record.Parent}'s again.";
    }

    /// <summary>Makes the block's keys of <paramref name="list"/> exactly <paramref name="target"/>; returns what changed.</summary>
    private List<PropertyChange> SetRecords(ExtensionRows ext, ExtensionRecordList list, IReadOnlyDictionary<string, string> target) =>
        RecordCodec.Write(_extensionGdt!, Record.Name, new ExtensionTarget(_gdtOf!, ext.Schema.Id), list, target);

    /// <summary>Puts one extension row back to its baseline (Undo all) and returns the changes for the undo step.</summary>
    private IEnumerable<PropertyChange> RevertExtension(PropertyItemViewModel item, ExtensionRows ext)
    {
        if (ext.Records.TryGetValue(item, out var list))
            return SetRecords(ext, list, RecordCodec.Rows(ext.Baseline, list).ToDictionary(r => r.Key, r => r.Value, StringComparer.OrdinalIgnoreCase));
        var original = ext.Baseline.TryGetValue(item.Key, out var b) ? b : null;
        var sidecar = ExtensionSidecar.Of(_extensionGdt!);
        var before = sidecar.Get(Record.Name, ext.Schema.Id, item.Key);
        sidecar.Set(Record.Name, ext.Schema.Id, item.Key, original);
        return [new PropertyChange(item.Key, before, original) { Extension = new ExtensionTarget(_gdtOf!, ext.Schema.Id) }];
    }

    /// <summary>
    /// Applies each extension's switch and visibleWhen rules; true when a row's visibility changed. Off hides every
    /// field but the switch itself, and never touches a stored value. A rule reads the extension's own rows.
    /// </summary>
    private bool EvaluateExtensionRules()
    {
        if (_extensions is null)
            return false;
        var changed = false;
        foreach (var ext in _extensions)
        {
            var on = ext.Schema.EnabledBy is not { } flag || VisibleWhen.Truthy(ExtensionRuleValue(flag) ?? "");
            // The off line comes and goes with the switch, and the export with it.
            if (ext.IsOn != on && (ext.OffRow is not null || ext.Notices.Count > 0))
                changed = true;
            ext.IsOn = on;
            if (ext.Sections.Count > 0)
                ext.Sections[0].Section.ExportLabel = on ? ext.Schema.Manifest.Export?.Command : null;
            foreach (var (section, def) in ext.Sections)
            {
                var sectionShown = on && (def.Rule?.Evaluate(ExtensionRuleValue) ?? true);
                foreach (var item in section.All)
                {
                    var shown = item == ext.Switch || sectionShown && (ext.RuleOf(item)?.Evaluate(ExtensionRuleValue) ?? true);
                    if (item.IsRuleHidden != !shown)
                    {
                        item.IsRuleHidden = !shown;
                        if (item.Problem is not null)
                            _problemsMoved.Add(item);
                        changed = true;
                    }
                }
            }
        }
        // A field a rule hides counts no problem; one it shows again does.
        SyncProblems();
        return changed;
    }

    /// <summary>A rule's key: a row's value, or a record list's numbered key (that row of its table).</summary>
    private string? ExtensionRuleValue(string key)
    {
        if (_rowByKey.TryGetValue(key, out var row) && _extensionOf.ContainsKey(row))
            return row.RawValue;
        foreach (var ext in _extensions!)
            foreach (var (item, list) in ext.Records)
                if (list.NumberOf(key) is { } n)
                    return RecordCodec.Split(item.RawValue) is var rows && n >= 1 && n <= rows.Count ? rows[n - 1] : null;
        return null;
    }

    /// <summary>The row an edited key belongs to: its own, or the record table whose numbered key it is.</summary>
    private PropertyItemViewModel? RowOfKey(string key)
    {
        if (_rowByKey.TryGetValue(key, out var row))
            return row;
        if (_extensions is not null)
            foreach (var ext in _extensions)
                foreach (var (item, list) in ext.Records)
                    if (list.NumberOf(key) is not null)
                        return item;
        return null;
    }

    private void RunPendingExtensionRules()
    {
        if (!_extensionRulesPending && !_sectionOpened)
            return;
        _extensionRulesPending = false;
        var opened = _sectionOpened;
        _sectionOpened = false;
        if (EvaluateExtensionRules() | opened)
            RefreshVisible();
    }

    /// <summary>A folded section opened while edits were landing together (an undo, a sync): the form is laid out after.</summary>
    private bool _sectionOpened;

    /// <summary>An ancestor's extension data changed: rows that inherit show the new value (squelched by the caller).</summary>
    private void RefreshExtensionInherited(IReadOnlyCollection<string>? keys)
    {
        if (_extensions is null)
            return;
        foreach (var ext in _extensions)
        {
            if (keys is not null && !keys.Any(ext.Covers))
                continue;
            ext.Inherited = InheritedExtensionValues(ext.Schema);
            _sectionOpened |= RecountSet(ext);
            var own = _extensionGdt?.Extensions?.Find(Record.Name, ext.Schema.Id)?.Properties;
            foreach (var p in ext.Rows)
            {
                var inherited = ext.Inherited.GetValueOrDefault(p.Key);
                if (inherited == p.ParentValue)
                    continue;
                p.InitProvenance(inherited);
                if (Holds(ext, own, p))
                    continue;
                var shown = ext.Unowned(p.Def);
                if (!Holds(ext, ext.Baseline, p))
                    p.InitBaseline(shown);
                if (p.RawValue != shown)
                    p.RawValue = shown;
            }
            _extensionRulesPending = true;
        }
    }

    /// <summary>After a save or a reload of the sidecar: "changed" is measured against what the file holds now.</summary>
    private void RefreshExtensionBaselines()
    {
        if (_extensions is null)
            return;
        foreach (var ext in _extensions)
        {
            var block = _extensionGdt?.Extensions?.Find(Record.Name, ext.Schema.Id);
            block?.CaptureBaseline();
            ext.Baseline = block?.SessionBaseline ?? NoValues;
        }
    }

    /// <summary>
    /// What a preview asks a simulator for (<see cref="SimulatorHost.CreateAsync"/>): one
    /// request per extension on this asset's type that names a module and is on for this asset (its enabledBy value,
    /// own or inherited), with every key's value as the form shows it (own, else inherited, else the default), in row
    /// order; a record table as its numbered keys, rows 1..N. Empty when no such extension is on, so nothing about a
    /// module is touched for this asset. Call again after an edit: the values are a copy.
    /// </summary>
    public IReadOnlyList<SimulatorRequest> SimulatorRequests()
    {
        if (_extensions is null)
            return Array.Empty<SimulatorRequest>();
        List<SimulatorRequest>? requests = null;
        foreach (var ext in _extensions)
        {
            if (ext.Schema.Manifest.Simulator is null
                || ext.Schema.EnabledBy is { } flag && !VisibleWhen.Truthy(ExtensionRuleValue(flag) ?? ""))
                continue;
            var values = new List<KeyValuePair<string, string>>();
            foreach (var (section, _) in ext.Sections)
                foreach (var item in section.All)
                {
                    if (ext.Fields.ContainsKey(item))
                        values.Add(new(item.Key, item.RawValue));
                    else if (ext.Records.TryGetValue(item, out var list))
                    {
                        var rows = RecordCodec.Split(item.RawValue);
                        for (var i = 0; i < rows.Count; i++)
                            values.Add(new(list.KeyOf(i + 1), rows[i]));
                    }
                }
            (requests ??= new()).Add(new(ext.Schema.Manifest, values));
        }
        return requests ?? (IReadOnlyList<SimulatorRequest>)Array.Empty<SimulatorRequest>();
    }

    /// <summary>An extension with a preview module targets this asset's type (whether or not it is on for the asset).</summary>
    public bool HasSimulatorExtension => _extensions?.Any(e => e.Schema.Manifest.Simulator is not null) == true;

    /// <summary>An extension key's value when this asset or an ancestor sets it; null when only the default would apply.</summary>
    public string? SetExtensionValue(string extensionId, string key)
    {
        var ext = _extensions?.FirstOrDefault(e => e.Schema.Id.Equals(extensionId, StringComparison.OrdinalIgnoreCase));
        if (ext is null)
            return null;
        var value = _extensionGdt?.Extensions?.Get(Record.Name, ext.Schema.Id, key) ?? ext.Inherited.GetValueOrDefault(key);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private string DescribeHiddenExtensionFields(string query)
    {
        var note = "";
        foreach (var ext in _extensions!)
        {
            var n = ext.Rows.Count(p => p.IsRuleHidden && CategoryViewModel.Passes(p, query, View, ignoreRules: true));
            if (n > 0)
                note += $" {n:N0} hidden {ext.Schema.Id} field{(n == 1 ? " matches" : "s match")}: {ext.Schema.Id} is off for this asset, or its rules hide {(n == 1 ? "it" : "them")}.";
        }
        return note;
    }
}
