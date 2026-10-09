using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Apex.Render.Data.Techsets;

namespace Apex.Editor.Services.Gdf;

/// <summary>A host object callable from interpreted script (asset / entryControl / entryVariable).</summary>
internal interface IHostCallable
{
    object? Invoke(string method, IReadOnlyList<object?> args);
}

internal enum EntryKind
{
    String, Float, Int, CheckBox, Combo, AssetCombo, Path, XModel, Texture,
    Label, Text, BoneCombo, FileCombo, Color, Vector2, Vector3, Vector4,
    ButtonGroup, LensFlare,
}

/// <summary>A single UI entry recorded while executing GenerateUI.</summary>
internal sealed class RecordedEntry
{
    public EntryKind Kind;
    public List<string> Names = new();      // 1 entry, or N for vector components
    public List<object?> Defaults = new();  // parallel to Names for vectors; else single
    public bool HasRange;
    public double Min;
    public double Max;
    public double Step = 1;
    public bool StepSet;
    public string ComboOptions = "";
    public string AssetType = "";
    public string RelativePath = ""; // .SetRelativePath("model_export/") — root-relative dir for file entries
    public string FileFilter = "";   // .SetFileFilter("... (*.ext)") — extension filter text, if any
    public string ModelKeys = "";    // AddEntry_BoneCombo(id, "gunModel") — the key(s) naming the model whose bones it lists
    public bool ShowAlpha = true;    // .SetShowAlpha(false) on a Color entry
    public List<string>? Labels;     // .SetLabels("Forward", "Right", "Up", "") — vector component labels
    public string? Title;
    public string? ToolTip;
    public string Category = "General";
    public string Subgroup = "";
    public bool Save = true;
    public bool Visible = true; // .Show(...) / Asset.ShowEntry(...)
    public bool Enabled = true; // .Enable(...)
    public bool Seeded;         // Value came from a real asset — SetDefaultValue must not clobber it
    public object? Value;   // current value used to answer queries
    public bool Dead;       // duplicate / throwaway — never mapped
    public bool ValueRead;  // the script observed Value (so the seeded value can steer the run)
    public int Order;       // place within its section: registration, or its techset tweak's sort order
    public bool Placed;     // ShowEntry or a techset tweak filed it (rather than where it was registered)
    public bool OptionsPerAsset; // combo options the host computes per asset (GetTechsetdefTypes): no static list
    public List<RecordedButton>? Buttons; // a ButtonGroup's .AddButton(label, icon, callback, param) calls, in order

    public string PrimaryName => Names.Count > 0 ? Names[0] : "";

    /// <summary><see cref="Value"/> as the script reads it; records that it was read.</summary>
    public object? Read()
    {
        ValueRead = true;
        return Value;
    }
}

/// <summary>One button of a deffile ButtonGroup: <c>.AddButton( "Add Item", "", "void AddItem( asset Asset, const string&amp; params )", "medal,4" )</c>.</summary>
internal sealed record RecordedButton(string Label, string Callback, string Param);

/// <summary>The recording <c>asset</c> host passed to GenerateUI.</summary>
internal sealed class RecordingAsset : IHostCallable
{
    public readonly List<RecordedEntry> Entries = new();
    private readonly Dictionary<string, RecordedEntry> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _name;
    private readonly IReadOnlyDictionary<string, string>? _seed;
    private string _categoryPath = "";
    private int _order;

    // When each section first received an entry: sections appear in that order (a section ShowEntry fills late, like
    // material's Special Properties, comes after the ones before it), entries within one by Order.
    private readonly Dictionary<string, int> _sectionRank = new(StringComparer.Ordinal);

    /// <summary>Form position: the entry's section in order of first use, then its order within the section.</summary>
    public long SortKey(RecordedEntry e) =>
        ((long)(_sectionRank.TryGetValue(e.Category, out var rank) ? rank : int.MaxValue) << 32) | (uint)e.Order;

    private void File(RecordedEntry e, string path)
    {
        string category = FirstSegment(path);
        e.Category = category;
        e.Subgroup = SecondSegment(path);
        _sectionRank.TryAdd(category, _sectionRank.Count);
    }

    // Option strings GetTechsetdefTypes handed out: a combo built from one lists a category's types, which differ
    // per asset, so the schema keeps no static list for it (the overlay supplies each asset's).
    private readonly HashSet<string> _perAssetOptions = new(ReferenceEqualityComparer.Instance);

    // The default-state run builds the type's static schema: it has no real material type, so the techsetdef host
    // calls answer for every type at once (see AddTechsetdefMaterialEntries).
    private bool SchemaRun => _seed is null;

    /// <summary>
    /// Entry names the script queried (GetEntryValue/Bool/Int/Float, GetEntryControl/Variable) —
    /// the read-set. An edit to a key outside this set cannot change the script's control flow,
    /// so callers can skip re-evaluating the overlay for it.
    /// </summary>
    public readonly HashSet<string> QueriedNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// True once the script called <c>GetName</c>. With <see cref="RecordedEntry.ValueRead"/> this is
    /// everything a run observed of its inputs: the seed only reaches the script through the values
    /// of registered entries, so two seeds that agree on every read entry's key run identically.
    /// </summary>
    public bool NameRead { get; private set; }

    /// <summary>Default-state host: entry values are the script's factory defaults.</summary>
    public RecordingAsset() : this("default", null) { }

    /// <summary>
    /// Host seeded from a real asset: entries whose key has a saved (own or inherited) value answer
    /// queries with that value instead of the factory default, and <c>GetName</c> returns the real
    /// asset name — so conditional Show/Enable/flow evaluates exactly as APE would for this asset.
    /// </summary>
    public RecordingAsset(string name, IReadOnlyDictionary<string, string>? seed)
    {
        _name = name;
        _seed = seed;
    }

    private static string SecondSegment(string path)
    {
        int dot = path.IndexOf('.');
        return dot < 0 ? "" : GdfRuntime.CleanTitle(path.Substring(dot + 1)) ?? "";
    }

    private static string FirstSegment(string path)
    {
        if (string.IsNullOrEmpty(path)) return "General";
        int dot = path.IndexOf('.');
        string seg = dot >= 0 ? path.Substring(0, dot) : path;
        return GdfRuntime.CleanTitle(seg) ?? "General";
    }

    public object? Invoke(string method, IReadOnlyList<object?> a)
    {
        switch (method)
        {
            case "BeginCategory":
                _categoryPath = a.Count > 0 ? GdfValue.AsString(a[0]) : "";
                return null;

            case "SetGraphColor":
            case "ScrollToEntry":
            case "AddCompositeImageEntries":
                return null;

            case "AddTechsetdefMaterialEntries":
                AddTechsetdefMaterialEntries(a.Count > 1 ? GdfValue.AsString(a[1]) : "");
                return null;

            // Shows a (typically hidden) entry and re-files it under the current category —
            // e.g. weapon "mods" is added hidden, then ShowEntry'd inside BeginCategory("Weapon Perks").
            case "ShowEntry":
            {
                if (a.Count > 0 && _byName.GetValueOrDefault(GdfValue.AsString(a[0])) is { } shown)
                {
                    shown.Visible = true;
                    shown.Placed = true;
                    File(shown, _categoryPath);
                }
                return null;
            }

            // ── Factories ────────────────────────────────────────────────
            case "AddEntry_String": return Simple(EntryKind.String, a, defaultIndex: 1);
            case "AddEntry_Text": return Simple(EntryKind.Text, a, 1);
            case "AddEntry_Path": return Simple(EntryKind.Path, a, 1);
            case "AddEntry_XModel": return Simple(EntryKind.XModel, a, 1);
            case "AddEntry_Texture": return Simple(EntryKind.Texture, a, 1);
            case "AddEntry_BoneCombo": return BoneCombo(a);
            case "AddEntry_FileCombo": return FileCombo(a);
            case "AddEntry_LensFlare": return Simple(EntryKind.LensFlare, a, -1);
            case "AddEntry_Label": return Simple(EntryKind.Label, a, 1);
            // Adding a group again adds to it (ainames adds "rankButtons" once per button).
            case "AddEntry_ButtonGroup":
                return a.Count > 0 && _byName.GetValueOrDefault(GdfValue.AsString(a[0])) is { Kind: EntryKind.ButtonGroup } group
                    ? new EntryControl(group)
                    : Simple(EntryKind.ButtonGroup, a, -1);

            case "AddEntry_Float": return Numeric(EntryKind.Float, a);
            case "AddEntry_Int": return Numeric(EntryKind.Int, a);

            case "AddEntry_CheckBox": return CheckBox(a);
            case "AddEntry_Combo": return Combo(a);
            case "AddEntry_AssetCombo": return AssetCombo(a);
            case "AddEntry_Color": return Color(a);

            case "AddEntry_Vector2": return Vector(EntryKind.Vector2, 2, a);
            case "AddEntry_Vector3": return Vector(EntryKind.Vector3, 3, a);
            case "AddEntry_Vector4": return Vector(EntryKind.Vector4, 4, a);

            // ── Controls / queries ───────────────────────────────────────
            case "GetEntryControl":
            {
                var e = Lookup(a);
                return new EntryControl(e); // dummy when null
            }
            case "GetEntryVariable":
            {
                var e = Lookup(a);
                return new EntryVariable(e);
            }
            // An entry not added yet still has the asset's saved value (scriptbundle and vehicleriders read an object's
            // name before they add its entries, to title its section).
            case "GetEntryValue":
            {
                var e = Lookup(a);
                return e is null ? Unregistered(a) ?? "" : GdfValue.AsString(e.Read());
            }
            case "GetEntryBool":
            {
                var e = Lookup(a);
                return GdfValue.Box(e is null ? GdfValue.AsBool(Unregistered(a)) : GdfValue.AsBool(e.Read()));
            }
            case "GetEntryInt":
            {
                var e = Lookup(a);
                return GdfValue.Box(e is null ? GdfValue.AsLong(Unregistered(a)) : GdfValue.AsLong(e.Read()));
            }
            case "GetEntryFloat":
            {
                var e = Lookup(a);
                return e is null ? GdfValue.AsDouble(Unregistered(a)) : GdfValue.AsDouble(e.Read());
            }
            case "GetName":
                NameRead = true;
                return _name;

            // ── Material host (techsetdefs) ──────────────────────────────
            case "IsTechsetdefMaterial":
                return GdfValue.Box(Techsetdefs.Library is { } lib
                    && (SchemaRun || lib.IsMaterialType(a.Count > 0 ? GdfValue.AsString(a[0]) : "")));
            case "GetTechsetdefCategories":
                return Techsetdefs.Library is { } categories ? string.Join(" | ", categories.MaterialCategories) : "";
            case "GetTechsetdefTypes":
            {
                var list = Techsetdefs.Library is { } types && a.Count > 0
                    ? string.Join(" | ", types.MaterialTypesIn(GdfValue.AsString(a[0])))
                    : "";
                _perAssetOptions.Add(list);
                return list;
            }
            case "GetMaterialCompositeImages": return new List<object?>();

            default:
                return null; // record-and-ignore unknown host calls
        }
    }

    /// <summary>
    /// APE's <c>Asset.AddTechsetdefMaterialEntries( "Material", materialType )</c>: shows every entry the material
    /// type's techsetdef tweaks, filed under the tweak's category and titled by it, in sort-index order. A tweak
    /// that reads several fields of one deffile entry (glossRange: glossRangeMin/Max) shows that entry once.
    /// The schema run lays out the fields of every type under "Material" (in the order most techsetdefs give them,
    /// titled as most do) without showing them.
    /// </summary>
    private void AddTechsetdefMaterialEntries(string materialType)
    {
        if (Techsetdefs.Library is not { } lib)
            return;
        // The schema run places each field by its own vote (Field set); a type's run places an element's fields together.
        IEnumerable<(string? Field, TechsetdefTweak Tweak)> tweaks = SchemaRun
            ? Techsetdefs.UnionLayout(lib).Select(kv => ((string?)kv.Key, kv.Value))
            : lib.TweaksOf(materialType).Select(t => ((string?)null, t));
        var placed = new HashSet<RecordedEntry>();
        foreach (var (own, tweak) in tweaks)
        {
            RecordedEntry? only = null;
            var single = true;
            foreach (var field in tweak.Fields)
            {
                if (_byName.GetValueOrDefault(field) is not { } e)
                    continue;
                single &= only is null || ReferenceEquals(only, e);
                only ??= e;
                if (own is not null && !string.Equals(own, field, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!placed.Add(e))
                    continue;
                // The schema files every techsetdef field under the category APE opens ("Material"); an asset's own
                // run files each under its tweak's section, which the editor arranges the form by.
                File(e, SchemaRun ? _categoryPath : tweak.Category);
                e.Order = _order++;
                e.Placed = true;
                if (!SchemaRun)
                    e.Visible = true;
            }
            if (single && only is not null && tweak.Title is { } title)
                only.Title = title;
        }
    }

    /// <summary>Diagnostics (<see cref="GdfRuntime.LegacyUnaddedReads"/>): a key read before it is added answers "".</summary>
    public bool LegacyUnaddedReads { get; init; }

    /// <summary>Keys the script read before any entry for them was added: the run depends on their saved values too.</summary>
    public readonly HashSet<string> UnregisteredReads = new(StringComparer.OrdinalIgnoreCase);

    private string? Unregistered(IReadOnlyList<object?> a)
    {
        if (_seed is null || a.Count == 0 || LegacyUnaddedReads)
            return null;
        var name = GdfValue.AsString(a[0]);
        UnregisteredReads.Add(name);
        return _seed.GetValueOrDefault(name);
    }

    private RecordedEntry? Lookup(IReadOnlyList<object?> a)
    {
        if (a.Count == 0) return null;
        string name = GdfValue.AsString(a[0]);
        QueriedNames.Add(name);
        return _byName.GetValueOrDefault(name);
    }

    private EntryControl Register(RecordedEntry e)
    {
        // First registration wins; later duplicates become dead throwaways.
        if (_byName.ContainsKey(e.PrimaryName))
        {
            e.Dead = true;
            return new EntryControl(e);
        }
        File(e, _categoryPath);
        e.Order = _order++;
        Entries.Add(e);
        foreach (var n in e.Names)
            if (!_byName.ContainsKey(n))
                _byName[n] = e;
        // A saved value (own or inherited) overrides the factory default, exactly as APE loads it.
        if (_seed is not null && _seed.TryGetValue(e.PrimaryName, out var saved))
        {
            e.Value = saved;
            e.Seeded = true;
        }
        return new EntryControl(e);
    }

    private EntryControl Simple(EntryKind kind, IReadOnlyList<object?> a, int defaultIndex)
    {
        var e = new RecordedEntry { Kind = kind };
        e.Names.Add(a.Count > 0 ? GdfValue.AsString(a[0]) : "");
        object? def = defaultIndex >= 0 && a.Count > defaultIndex ? a[defaultIndex] : "";
        e.Defaults.Add(def);
        e.Value = def is null ? "" : GdfValue.AsString(def);
        return Register(e);
    }

    // AddEntry_BoneCombo(ID, XModelKeys): the second argument names the key(s) holding the model.
    private EntryControl BoneCombo(IReadOnlyList<object?> a)
    {
        var control = Simple(EntryKind.BoneCombo, a, -1);
        if (control.Entry is { Dead: false } e && a.Count > 1)
            e.ModelKeys = GdfValue.AsString(a[1]);
        return control;
    }

    // AddEntry_FileCombo(ID, RelativePath, FileTypes): the folder whose files it lists, and their types.
    private EntryControl FileCombo(IReadOnlyList<object?> a)
    {
        var control = Simple(EntryKind.FileCombo, a, -1);
        if (control.Entry is { Dead: false } e)
        {
            if (a.Count > 1)
                e.RelativePath = GdfValue.AsString(a[1]);
            if (a.Count > 2)
                e.FileFilter = GdfValue.AsString(a[2]);
        }
        return control;
    }

    private EntryControl Numeric(EntryKind kind, IReadOnlyList<object?> a)
    {
        var e = new RecordedEntry { Kind = kind };
        e.Names.Add(a.Count > 0 ? GdfValue.AsString(a[0]) : "");
        object? def = a.Count > 1 ? a[1] : GdfValue.Box(0L);
        e.Defaults.Add(def);
        e.Value = def;
        if (a.Count >= 4)
        {
            e.Min = GdfValue.AsDouble(a[2]);
            e.Max = GdfValue.AsDouble(a[3]);
            e.HasRange = e.Max > e.Min;
        }
        if (kind == EntryKind.Int) e.Step = 1;
        return Register(e);
    }

    private EntryControl CheckBox(IReadOnlyList<object?> a)
    {
        var e = new RecordedEntry { Kind = EntryKind.CheckBox };
        e.Names.Add(a.Count > 0 ? GdfValue.AsString(a[0]) : "");
        bool def = a.Count > 1 && GdfValue.AsBool(a[1]);
        e.Defaults.Add(def);
        e.Value = def;
        return Register(e);
    }

    private EntryControl Combo(IReadOnlyList<object?> a)
    {
        var e = new RecordedEntry { Kind = EntryKind.Combo };
        e.Names.Add(a.Count > 0 ? GdfValue.AsString(a[0]) : "");
        e.ComboOptions = a.Count > 1 ? GdfValue.AsString(a[1]) : "";
        e.OptionsPerAsset = e.ComboOptions.Length > 0 && _perAssetOptions.Contains(e.ComboOptions);
        e.Defaults.Add("");
        e.Value = "";
        return Register(e);
    }

    private EntryControl AssetCombo(IReadOnlyList<object?> a)
    {
        var e = new RecordedEntry { Kind = EntryKind.AssetCombo };
        e.Names.Add(a.Count > 0 ? GdfValue.AsString(a[0]) : "");
        e.AssetType = a.Count > 1 ? GdfValue.AsString(a[1]) : "";
        e.Defaults.Add("");
        e.Value = "";
        return Register(e);
    }

    private EntryControl Color(IReadOnlyList<object?> a)
    {
        var e = new RecordedEntry { Kind = EntryKind.Color };
        e.Names.Add(a.Count > 0 ? GdfValue.AsString(a[0]) : "");
        string r = a.Count > 1 ? GdfValue.AsString(a[1]) : "0";
        string g = a.Count > 2 ? GdfValue.AsString(a[2]) : "0";
        string b = a.Count > 3 ? GdfValue.AsString(a[3]) : "0";
        string al = a.Count > 4 ? GdfValue.AsString(a[4]) : "1";
        string def = $"{r} {g} {b} {al}";
        e.Defaults.Add(def);
        e.Value = def;
        return Register(e);
    }

    private EntryControl Vector(EntryKind kind, int n, IReadOnlyList<object?> a)
    {
        var e = new RecordedEntry { Kind = kind };
        for (int i = 0; i < n && i < a.Count; i++)
            e.Names.Add(GdfValue.AsString(a[i]));
        for (int i = 0; i < n; i++)
        {
            int di = n + i;
            e.Defaults.Add(di < a.Count ? a[di] : 0L);
        }
        int minIdx = 2 * n;
        if (a.Count > minIdx + 1)
        {
            e.Min = GdfValue.AsDouble(a[minIdx]);
            e.Max = GdfValue.AsDouble(a[minIdx + 1]);
            e.HasRange = e.Max > e.Min;
        }
        return Register(e);
    }
}

/// <summary>Chainable control returned by AddEntry_* / GetEntryControl.</summary>
internal sealed class EntryControl : IHostCallable
{
    private readonly RecordedEntry? _e;
    public EntryControl(RecordedEntry? e) => _e = e;

    /// <summary>The entry this control records into (null for a dummy).</summary>
    internal RecordedEntry? Entry => _e;

    public object? Invoke(string method, IReadOnlyList<object?> a)
    {
        switch (method)
        {
            case "SetShowAlpha":
                if (_e is not null && a.Count > 0) _e.ShowAlpha = GdfValue.AsBool(a[0]);
                return this;
            case "SetLabels":
                if (_e is not null)
                {
                    _e.Labels = new List<string>(a.Count);
                    foreach (var label in a)
                        _e.Labels.Add(GdfValue.AsString(label));
                }
                return this;
            case "SetTitle":
                if (_e is not null) _e.Title = a.Count > 0 ? GdfValue.AsString(a[0]) : "";
                return this;
            case "SetToolTip":
                if (_e is not null) _e.ToolTip = a.Count > 0 ? GdfValue.AsString(a[0]) : "";
                return this;
            case "SetStep":
                if (_e is not null && a.Count > 0) { _e.Step = GdfValue.AsDouble(a[0]); _e.StepSet = true; }
                return this;
            case "SetSave":
                if (_e is not null && a.Count > 0) _e.Save = GdfValue.AsBool(a[0]);
                return this;
            case "SetRelativePath":
                if (_e is not null && a.Count > 0) _e.RelativePath = GdfValue.AsString(a[0]);
                return this;
            case "SetFileFilter":
                if (_e is not null && a.Count > 0) _e.FileFilter = GdfValue.AsString(a[0]);
                return this;
            case "AddButton":
                if (_e is { Kind: EntryKind.ButtonGroup } && a.Count > 2)
                    (_e.Buttons ??= new()).Add(new RecordedButton(GdfValue.AsString(a[0]), GdfValue.AsString(a[2]),
                        a.Count > 3 ? GdfValue.AsString(a[3]) : ""));
                return this;
            case "SetDefaultValue":
                // Establishes the default — must not override a value seeded from a real asset.
                if (_e is not null && !_e.Seeded && a.Count > 0) _e.Value = GdfValue.AsString(a[0]);
                return this;
            case "SetValue":
            case "UpdateSavedValue":
                if (_e is not null && a.Count > 0) _e.Value = GdfValue.AsString(a[0]);
                return this;
            case "Show":
                if (_e is not null) _e.Visible = a.Count == 0 || GdfValue.AsBool(a[0]);
                return this;
            case "Enable":
                if (_e is not null) _e.Enabled = a.Count == 0 || GdfValue.AsBool(a[0]);
                return this;
            case "SetBool":
                if (_e is not null && a.Count > 0) _e.Value = GdfValue.Box(GdfValue.AsBool(a[0]));
                return this;
            case "SetInt":
                if (_e is not null && a.Count > 0) _e.Value = GdfValue.Box(GdfValue.AsLong(a[0]));
                return this;
            case "SetFloat":
                if (_e is not null && a.Count > 0) _e.Value = GdfValue.AsDouble(a[0]);
                return this;

            // Non-chaining queries.
            case "GetTitle": return _e?.Title ?? "";
            case "GetToolTip": return _e?.ToolTip ?? "";
            case "GetSave": return GdfValue.Box(_e?.Save ?? true);
            case "GetShow": return GdfValue.Box(_e?.Visible ?? true);
            case "GetEnable": return GdfValue.Box(_e?.Enabled ?? true);
            case "IsValid": return GdfValue.Box(true);
            case "IsSpecified": return GdfValue.Box(false);
            case "GetValue": return _e is null ? "" : GdfValue.AsString(_e.Read());
            case "GetBool": return GdfValue.Box(_e is not null && GdfValue.AsBool(_e.Read()));
            case "GetInt": return GdfValue.Box(_e is null ? 0L : GdfValue.AsLong(_e.Read()));
            case "GetFloat": return _e is null ? 0.0 : GdfValue.AsDouble(_e.Read());
            case "ToInt": return GdfValue.Box(_e is null ? 0L : GdfValue.AsLong(_e.Read()));

            default:
                return this; // record-and-ignore every other modifier
        }
    }
}

/// <summary>A mutable variable handle (GetEntryVariable) answering GetValue/SetValue/etc.</summary>
internal sealed class EntryVariable : IHostCallable
{
    private readonly RecordedEntry? _e;
    private object? _floating;
    public EntryVariable(RecordedEntry? e) => _e = e;

    private object? Val
    {
        get => _e is not null ? _e.Read() : _floating;
        set { if (_e is not null) _e.Value = value; else _floating = value; }
    }

    public object? Invoke(string method, IReadOnlyList<object?> a)
    {
        switch (method)
        {
            case "GetValue": return GdfValue.AsString(Val);
            case "GetBool": return GdfValue.Box(GdfValue.AsBool(Val));
            case "GetInt": return GdfValue.Box(GdfValue.AsLong(Val));
            case "GetFloat": return GdfValue.AsDouble(Val);
            case "ToInt": return GdfValue.Box(GdfValue.AsLong(Val));
            case "SetValue": Val = a.Count > 0 ? GdfValue.AsString(a[0]) : ""; return null;
            case "SetInt": Val = GdfValue.Box(a.Count > 0 ? GdfValue.AsLong(a[0]) : 0L); return null;
            case "SetBool": Val = GdfValue.Box(a.Count > 0 && GdfValue.AsBool(a[0])); return null;
            case "SetFloat": Val = a.Count > 0 ? GdfValue.AsDouble(a[0]) : 0.0; return null;
            // A variable is its entry: .Show / .Enable / .SetSave on it (destructiblecharacterdef's ShowPiece) act on the entry.
            default: return _e is null ? null : new EntryControl(_e).Invoke(method, a);
        }
    }
}
