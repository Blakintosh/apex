using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Apex.Editor.Models;

namespace Apex.Editor.Services.Gdf;

/// <summary>Per-asset display rule for one property: whether APE would show/enable it right now.</summary>
public sealed record PropertyRule(bool Visible, bool Enabled, string[]? Choices)
{
    /// <summary>What APE shows for each of <see cref="Choices"/> (the deffile's "Display{value}"); null when the values are shown as they are.</summary>
    public string[]? ChoiceLabels { get; init; }
}

/// <summary>Where the script put one entry for this asset: its section, its title (null: the schema's label), its place in the form.</summary>
public readonly record struct EntryLayout(string Category, string? Title, long Order, bool Placed, string Subgroup = "");

/// <summary>
/// The result of re-running a type's <c>GenerateUI</c> against a specific asset's values: a rule per
/// schema key, plus the set of keys the script read (the read-set) — edits outside that set cannot
/// change any rule, so callers skip re-evaluation for them.
/// </summary>
public sealed class SchemaOverlay
{
    public required IReadOnlyDictionary<string, PropertyRule> Rules { get; init; }
    public required IReadOnlySet<string> QueriedKeys { get; init; }

    /// <summary>
    /// Where this run put each entry. A material's techsetdef files its fields per material type (ShowEntry and
    /// AddTechsetdefMaterialEntries move entries), so the form is arranged from this rather than the type's schema.
    /// </summary>
    public IReadOnlyDictionary<string, EntryLayout>? Layout { get; init; }

    /// <summary>The deffile's button groups for this asset (AddEntry_ButtonGroup), in form order.</summary>
    public IReadOnlyList<DeffileButtonGroup> Buttons { get; init; } = [];

    /// <summary>
    /// What this run's entries hold that the schema doesn't define (a ButtonGroup's own value: xmodel's numLods), so a
    /// button's callback reads them as APE would.
    /// </summary>
    public IReadOnlyDictionary<string, string> UnsavedValues { get; init; } = new Dictionary<string, string>();

    /// <summary>Entries the schema doesn't define that this run saves (a scriptbundle's second object, once it has a name).</summary>
    public IReadOnlySet<string> SavedOffSchema { get; init; } = new HashSet<string>();

    /// <summary>True when editing <paramref name="key"/> could change any rule in this overlay.</summary>
    public bool AffectedBy(string key) => QueriedKeys.Contains(key);
}

/// <summary>
/// Holds the parsed <see cref="GdfProgram"/> per asset type so <c>GenerateUI</c> can be re-executed
/// per asset — APE's model for conditional property visibility: the deffile script reads the live
/// asset (<c>GetEntryValue</c>…) and decides what shows, enables, and which combo options exist.
/// Programs are immutable after parse; each evaluation uses a fresh host, so this is thread-safe.
/// </summary>
public static class GdfRuntime
{
    private static readonly System.Text.RegularExpressions.Regex Markup =
        new(@"<[^>]*>", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// A deffile title as a label: APE's layout padding, markup and trailing colons dropped ("Script Type:" → "Script
    /// Type", "Turn Rate &lt;small&gt;(degrees / sec)&lt;/small&gt;" → "Turn Rate (degrees / sec)", " Gibbable Character" →
    /// "Gibbable Character"); null when nothing is left (a " " title), so the caller falls back to the key.
    /// </summary>
    public static string? CleanTitle(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
            return null;
        var text = raw.IndexOf('<') >= 0 ? Markup.Replace(raw, "") : raw;
        var sb = new System.Text.StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                if (sb.Length > 0 && sb[^1] != ' ')
                    sb.Append(' ');
            }
            else
                sb.Append(c);
        }
        var clean = sb.ToString().Trim().TrimEnd(':').TrimEnd();
        return clean.Length == 0 ? null : clean;
    }

    private static readonly ConcurrentDictionary<string, GdfProgram> Programs =
        new(StringComparer.OrdinalIgnoreCase);

    // An overlay is a pure function of the program, the schema, and what the run observed of the
    // asset (the seeded values it read, and its name if it asked). Weapon scripts are ~190 KB, so
    // re-opening an asset, or opening one that agrees on those values, reuses the previous result.
    private const int CachedPerType = 8;
    private static readonly ConcurrentDictionary<string, List<CachedOverlay>> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    // Per type, the entries its schema run placed (ShowEntry, a techset tweak) rather than left where registered.
    private static readonly ConcurrentDictionary<string, IReadOnlySet<string>> SchemaPlaced =
        new(StringComparer.OrdinalIgnoreCase);

    internal static void Register(string typeName, GdfProgram program, IEnumerable<string>? schemaPlaced = null)
    {
        SchemaPlaced[typeName] = new HashSet<string>(schemaPlaced ?? [], StringComparer.OrdinalIgnoreCase);
        Programs[typeName] = program;
        Cache.TryRemove(typeName, out _);
    }

    /// <summary>
    /// Diagnostics only (Apex.Shots measures what the read-before-add rule changes): a key read before its entry is
    /// added answers "" as it did before, and overlays skip the cache. Never set in the app.
    /// </summary>
    public static volatile bool LegacyUnaddedReads;

    /// <summary>True when this type's deffile is available for per-asset evaluation.</summary>
    public static bool Has(string typeName) => Programs.ContainsKey(typeName);

    /// <summary>Removes all registered programs (mock mode / tests).</summary>
    public static void Reset()
    {
        Programs.Clear();
        SchemaPlaced.Clear();
        Cache.Clear();
        Techsetdefs.Reset();
    }

    /// <summary>
    /// Re-runs GenerateUI with the host seeded from <paramref name="effectiveValues"/> (the asset's
    /// own properties overlaid on its parent chain). Returns null — meaning "show everything", the
    /// pre-existing behavior — when the type has no program or the run fails, so a script error can
    /// never hide real data. Results are shared between callers and must not be mutated.
    /// </summary>
    public static SchemaOverlay? EvaluateOverlay(
        string typeName, string assetName, IReadOnlyDictionary<string, string> effectiveValues)
    {
        if (!Programs.TryGetValue(typeName, out var prog))
            return null;
        var schema = SchemaRegistry.Get(typeName);
        if (schema is null)
            return null;

        // Read once: one run is either mode, never a mix.
        var legacy = LegacyUnaddedReads;
        var cached = Cache.GetOrAdd(typeName, static _ => new List<CachedOverlay>());
        lock (cached)
        {
            for (var i = 0; i < cached.Count && !legacy; i++)
            {
                var c = cached[i];
                if (!c.Matches(prog, schema, assetName, effectiveValues))
                    continue;
                cached.RemoveAt(i);
                cached.Insert(0, c);
                return c.Overlay;
            }
        }

        var failed = false;
        var host = new RecordingAsset(assetName, effectiveValues) { LegacyUnaddedReads = legacy };
        GdfInterpreter.Run(prog, "GenerateUI", _ => failed = true, host);
        var overlay = failed ? null : BuildOverlay(schema, host);
        if (legacy)
            return overlay;

        var entry = new CachedOverlay(prog, schema, host, assetName, effectiveValues, overlay);
        lock (cached)
        {
            cached.Insert(0, entry);
            if (cached.Count > CachedPerType)
                cached.RemoveAt(cached.Count - 1);
        }
        return overlay;
    }

    private static SchemaOverlay? BuildOverlay(AssetSchema schema, RecordingAsset host)
    {
        var rules = new Dictionary<string, PropertyRule>(schema.Properties.Count, StringComparer.OrdinalIgnoreCase);
        var layout = new Dictionary<string, EntryLayout>(schema.Properties.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var e in host.Entries)
        {
            if (e.Dead || e.Kind is EntryKind.Label or EntryKind.ButtonGroup)
                continue;
            // A vector's label is its title plus a component suffix, which the schema already composed.
            var place = new EntryLayout(e.Category, e.Names.Count == 1 ? CleanTitle(e.Title) : null, host.SortKey(e), e.Placed, e.Subgroup);
            foreach (var name in e.Names)
                if (name.Length > 0)
                    layout.TryAdd(name, place);
            // A conditional SetSave(false) means APE would neither display nor persist the entry
            // for this asset state — treat it as hidden.
            var options = e.Kind == EntryKind.Combo ? SchemaMapper.ParseOptions(e.ComboOptions) : null;
            var rule = new PropertyRule(e.Visible && e.Save, e.Enabled, options?.Values) { ChoiceLabels = options?.Labels };
            foreach (var name in e.Names)
                if (name.Length > 0)
                    rules.TryAdd(name, rule);
        }

        // Entries the script did not create for this asset state (an if-branch not taken) are
        // hidden in APE. Sanity guard: if the run produced drastically fewer entries than the
        // static schema, something went off the rails — fail open rather than mass-hide.
        if (rules.Count < schema.Properties.Count / 2)
            return null;
        var hidden = new PropertyRule(false, true, null);
        foreach (var def in schema.Properties)
            rules.TryAdd(def.Key, hidden);

        // The read-set: entries queried by name, and those read through the control a factory returned
        // (vehicle.awi's `string Type = Asset.AddEntry_Combo( "type", ... ).GetValue();`).
        var queried = new HashSet<string>(host.QueriedNames, StringComparer.OrdinalIgnoreCase);
        foreach (var e in host.Entries)
            if (e.ValueRead && !e.Dead)
                queried.UnionWith(e.Names);
        var (buttons, unsaved, kept) = DeffileButtons.Collect(schema, host);
        return new SchemaOverlay
        {
            Rules = rules, QueriedKeys = queried, Layout = layout, Buttons = buttons, UnsavedValues = unsaved, SavedOffSchema = kept,
        };
    }

    /// <summary>
    /// Runs a deffile button's callback for <paramref name="typeName"/> off the UI thread (it may wait on a question
    /// the services put to the user). Null when the type has no program.
    /// </summary>
    internal static DeffileButtonRun? RunButton(string typeName, string assetName, IReadOnlyDictionary<string, string> values,
        Func<string, string> valueOf, Func<string, string> inheritedOf, DeffileButton button, IDeffileButtonServices services) =>
        Programs.TryGetValue(typeName, out var prog) ? DeffileButtons.Run(prog, assetName, values, valueOf, inheritedOf, button, services) : null;

    /// <summary>
    /// For entries the deffile disables for this asset, a property and a value that would enable each: the form names
    /// it on the disabled row ("Set Bullet Impact Explode to On to edit it"), where APE only greys the row out. Found by
    /// trying it: each candidate switch is flipped, and each candidate choice set to its other options (a few), and the
    /// script re-run on that copy of the values. Runs off the UI thread; stops when <paramref name="cancelled"/> says so.
    /// </summary>
    /// <param name="candidates">The keys to try, in the order to prefer them (the form's: the nearest controls first).</param>
    internal static Dictionary<string, (string Key, string Value)> FindEnablers(string typeName, string assetName,
        IReadOnlyDictionary<string, string> effective, IReadOnlyCollection<string> disabled, IReadOnlyList<(string Key, string[] Tries)> candidates,
        Func<bool> cancelled)
    {
        var found = new Dictionary<string, (string Key, string Value)>(StringComparer.OrdinalIgnoreCase);
        if (!Programs.TryGetValue(typeName, out var prog) || disabled.Count == 0)
            return found;
        var wanted = new HashSet<string>(disabled, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, tries) in candidates)
        {
            foreach (var value in tries)
            {
                if (found.Count == wanted.Count || cancelled())
                    return found;
                var seed = new Dictionary<string, string>(effective, StringComparer.OrdinalIgnoreCase) { [key] = value };
                var host = new RecordingAsset(assetName, seed);
                var failed = false;
                GdfInterpreter.Run(prog, "GenerateUI", _ => failed = true, host);
                if (failed)
                    continue;
                foreach (var e in host.Entries)
                {
                    if (e.Dead || !e.Enabled || !e.Visible || !e.Save || e.Kind is EntryKind.Label or EntryKind.ButtonGroup)
                        continue;
                    foreach (var name in e.Names)
                        if (wanted.Contains(name))
                            found.TryAdd(name, (key, value));
                }
            }
        }
        return found;
    }

    private static readonly ConditionalWeakTable<SchemaOverlay, AssetSchema> Arranged = new();

    /// <summary>
    /// <paramref name="schema"/> arranged as <paramref name="overlay"/>'s run laid the form out: each property in the
    /// section, order and title the script gave it for this asset (a lit material's normalMap under Normal, a 2d one's
    /// colorMap00 under Maps). A property the schema run placed (ShowEntry, a techset tweak) that this run left where
    /// it was registered keeps the schema's section and title and goes after the rest, so one an edit reveals (a new
    /// material type) lands at the end of its schema section; every other property keeps its place beside the entries
    /// registered around it. The schema itself when nothing moves; shared between assets with the same overlay.
    /// </summary>
    public static AssetSchema Arrange(AssetSchema schema, SchemaOverlay? overlay)
    {
        if (overlay?.Layout is not { } layout)
            return schema;
        var placedBySchema = SchemaPlaced.GetValueOrDefault(schema.TypeName) ?? new HashSet<string>();
        return Arranged.GetValue(overlay, _ =>
        {
            var props = new List<(PropertyDef Def, long Order, int Index)>(schema.Properties.Count);
            var changed = false;
            var last = long.MinValue;
            for (var i = 0; i < schema.Properties.Count; i++)
            {
                var def = schema.Properties[i];
                if (!layout.TryGetValue(def.Key, out var place) || (!place.Placed && placedBySchema.Contains(def.Key)))
                {
                    var tail = long.MaxValue / 2 + i;
                    props.Add((def, tail, i));
                    changed |= tail < last;
                    last = Math.Max(last, tail);
                    continue;
                }
                var moved = def;
                if (place.Category != def.Category)
                    moved = moved with { Category = place.Category };
                if (place.Subgroup != def.Subgroup)
                    moved = moved with { Subgroup = place.Subgroup };
                if (place.Title is { } title && title != def.Label)
                    moved = moved with { Label = title };
                changed |= !ReferenceEquals(moved, def) || place.Order < last;
                last = Math.Max(last, place.Order);
                props.Add((moved, place.Order, i));
            }
            if (!changed)
                return schema;
            props.Sort((a, b) => a.Order != b.Order ? a.Order.CompareTo(b.Order) : a.Index.CompareTo(b.Index));
            return new AssetSchema
            {
                TypeName = schema.TypeName,
                GdfName = schema.GdfName,
                Properties = props.ConvertAll(p => p.Def),
            };
        });
    }

    /// <summary>
    /// One evaluation and the inputs it observed: for each entry whose value the script read, whether
    /// the seed had that key and its value (the seed reaches the script only through those reads).
    /// </summary>
    private sealed class CachedOverlay
    {
        private readonly GdfProgram _program;
        private readonly AssetSchema _schema;
        private readonly string? _assetName; // null when the script never asked for the name
        private readonly (string Key, bool Present, string? Value)[] _reads;

        public CachedOverlay(GdfProgram program, AssetSchema schema, RecordingAsset host, string assetName,
            IReadOnlyDictionary<string, string> seed, SchemaOverlay? overlay)
        {
            _program = program;
            _schema = schema;
            _assetName = host.NameRead ? assetName : null;
            var reads = new List<(string, bool, string?)>();
            foreach (var e in host.Entries)
            {
                if (!e.ValueRead)
                    continue;
                var present = seed.TryGetValue(e.PrimaryName, out var value);
                reads.Add((e.PrimaryName, present, value));
            }
            foreach (var key in host.UnregisteredReads)
            {
                var present = seed.TryGetValue(key, out var value);
                reads.Add((key, present, value));
            }
            _reads = reads.ToArray();
            Overlay = overlay;
        }

        public SchemaOverlay? Overlay { get; }

        public bool Matches(GdfProgram program, AssetSchema schema, string assetName, IReadOnlyDictionary<string, string> seed)
        {
            if (!ReferenceEquals(program, _program) || !ReferenceEquals(schema, _schema))
                return false;
            if (_assetName is not null && !string.Equals(assetName, _assetName, StringComparison.Ordinal))
                return false;
            foreach (var (key, present, value) in _reads)
            {
                if (seed.TryGetValue(key, out var v) != present)
                    return false;
                if (present && !string.Equals(v, value, StringComparison.Ordinal))
                    return false;
            }
            return true;
        }
    }
}
