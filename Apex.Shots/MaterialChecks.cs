using System;
using System.Collections.Generic;
using System.Linq;
using Apex.Editor.Models;
using Apex.Editor.ViewModels;

namespace Apex.Shots;

public partial class Program
{
    /// <summary>Material types the live material checks open, one per techsetdef category they cover.</summary>
    private static readonly string[] MaterialCheckTypes =
    {
        "lit",                          // Geometry
        "lit_advanced_fullspec",        // Geometry Advanced: the corpus' most common type
        "lit_decal",                    // Decal
        "effect_lit_emissive_blend",    // Effect
    };

    /// <summary>--materials-live: the material checks alone, on the real install (read-only).</summary>
    private static void RunLiveMaterialChecks()
    {
        var env = new Apex.Editor.Services.Gdt.GameEnvironment();
        if (!env.IsAvailable)
        {
            Console.WriteLine("material (live): BO3 not found — skipped");
            return;
        }
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var schemas = Apex.Editor.Services.Gdf.GdfSchemaLoader.LoadAll(env.DeffilesDir!);
        Console.WriteLine($"info  material (live): {schemas.Count} schemas in {clock.ElapsedMilliseconds} ms");
        SchemaRegistry.Populate(schemas);
        var db = Apex.Editor.Services.Gdt.GdtLoader.LoadAll(env);
        LiveDisplayRules(schemas, db);
        LiveArrangeKeepsPlace(db);
        LiveMaterialRules(db);
    }

    /// <summary>The form as it shows: each section with its visible rows' labels, in order.</summary>
    private static List<(string Section, List<string> Rows)> FormLayout(AssetEditorViewModel tab)
    {
        var layout = new List<(string, List<string>)>();
        foreach (var row in tab.FlatRows)
        {
            if (row is CategoryViewModel c)
                layout.Add((c.Name, new List<string>()));
            else if (row is PropertyItemViewModel p && layout.Count > 0)
                layout[^1].Item2.Add(p.Label);
        }
        return layout;
    }

    private static string Describe(List<(string Section, List<string> Rows)> layout) =>
        string.Join(" · ", layout.Select(s => $"{s.Section} {s.Rows.Count}"));

    private static AssetRecord? MaterialOfType(AssetDatabase db, string materialType) =>
        db.Assets.FirstOrDefault(a => a.Type.Equals("material", StringComparison.OrdinalIgnoreCase)
            && a.Parent is null
            && string.Equals(a.ScanProperties.GetValueOrDefault("materialType"), materialType, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// material.awi's techsetdef host calls on the real install: the Material Category and Material Type lists come
    /// from the techsetdefs, a material shows the fields its type's techsetdef tweaks (in the tweak's section and sort
    /// order, titled by it), and changing the type re-runs the deffile so the fields follow. Read-only: every edit made
    /// here is undone.
    /// </summary>
    private static void LiveMaterialRules(AssetDatabase db)
    {
        var schema = SchemaRegistry.Get("material");
        if (schema is null)
        {
            Check("material (live): the material schema loads", false);
            return;
        }
        var byName = db.Assets
            .GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var categories = schema.Find("materialCategory")?.Choices ?? Array.Empty<string>();
        Console.WriteLine($"info  material (live): {categories.Length} material categories: {string.Join(", ", categories)}");
        Check("material (live): Material Category lists the techsetdef categories (Geometry, Decal, Effect, 2d)",
            new[] { "Geometry", "Geometry Advanced", "Decal", "Effect", "2d" }.All(c => categories.Contains(c)));
        Check("material (live): Material Category defaults to Geometry", schema.Find("materialCategory")?.Default == "Geometry");
        Console.WriteLine($"info  material (live): schema sections of colorMap/normalMap/blendFunc/playerClip: {schema.Find("colorMap")?.Category}/{schema.Find("normalMap")?.Category}/{schema.Find("blendFunc")?.Category}/{schema.Find("playerClip")?.Category}");
        Check("material (live): before its type is known, the schema files techsetdef fields under Material, titled as most techsetdefs title them",
            schema.Find("colorMap") is { Category: "Material", Label: "Color Map" } && schema.Find("heatmap") is { Category: "Material", Label: "Heatmap" });
        Check("material (live): the schema keeps no static Material Type list (each category has its own; the asset's run supplies it)",
            schema.Find("materialType")?.Choices.Length == 0);
        Check("material (live): the schema files tiling under Material and clip under Special Properties (ShowEntry)",
            schema.Find("tilingWidth")?.Category == "Material" && schema.Find("playerClip")?.Category == "Special Properties");
        Check("material (live): the schema keeps material.awi's two-digit keys (string Digit = i; then a leading 0)",
            schema.Find("cg00_x") is not null && schema.Find("colorMap05") is not null && schema.Find("cg0_x") is null);

        var env = new Apex.Editor.Services.Gdt.GameEnvironment();
        var techsetRoot = System.IO.Path.Combine(env.DeffilesDir ?? "", "..", "share", "raw", "techsetdefs_stable_toolsgfx");
        var techsets = System.IO.Directory.Exists(techsetRoot) ? new Apex.Render.Data.Techsets.TechsetdefLibrary(techsetRoot) : null;
        if (techsets is not null)
        {
            // What the techsetdef reads cost, cold (a fresh library): the type lists, then the every-type layout.
            var clock = System.Diagnostics.Stopwatch.StartNew();
            _ = techsets.MaterialCategories;
            var typesMs = clock.Elapsed.TotalMilliseconds;
            clock.Restart();
            var union = techsets.TweakUnion;
            Console.WriteLine($"info  material (live): techsetdef type index {typesMs:0} ms, every-type layout {clock.Elapsed.TotalMilliseconds:0} ms ({union.Count} fields; both read in the background as the deffiles load)");
            // A field whose own layout differs from the element it was read with (texTile/filter beside colorMap).
            var apart = union.Where(kv => kv.Value.Fields.Any(g => !string.Equals(g, kv.Key, StringComparison.OrdinalIgnoreCase)
                && union.TryGetValue(g, out var own) && (own.Category != kv.Value.Category || own.Element != kv.Value.Element))).Count();
            Console.WriteLine($"info  material (live): {apart} fields share an element with a field laid out elsewhere (each is now placed by its own layout)");
            var order = schema.Properties.Where(p => p.Category == "Material").Select(p => p.Key).ToList();
            // A vector entry (glossRangeMin/Max, cg00_x..w) is one row group placed by its first field: compare entries.
            bool SameVector(string a, string b)
            {
                string Base(string key) => schema.Find(key)?.Label is { } l && l.LastIndexOf(' ') is > 0 and var sp ? l[..sp] : key;
                return Base(a) == Base(b) && Base(a) != a;
            }
            var groups = new List<(string Key, double Sort)>();
            for (var i = 0; i < order.Count; i++)
            {
                var sort = union.TryGetValue(order[i], out var u) ? u.SortIndex : double.MaxValue;
                if (i > 0 && SameVector(order[i - 1], order[i]))
                    groups[^1] = (groups[^1].Key, Math.Min(groups[^1].Sort, sort));
                else
                    groups.Add((order[i], sort));
            }
            groups.RemoveAll(g => g.Sort == double.MaxValue);
            var outOfOrder = groups.Zip(groups.Skip(1)).Where(p => p.First.Sort > p.Second.Sort)
                .Select(p => $"{p.First.Key} ({p.First.Sort}) before {p.Second.Key} ({p.Second.Sort})").ToList();
            Check($"material (live): the schema's Material section follows each field's own sort index ({outOfOrder.Count} out of order{string.Concat(outOfOrder.Take(3).Select(o => "; " + o))})",
                outOfOrder.Count == 0);
        }
        foreach (var type in MaterialCheckTypes)
        {
            var material = MaterialOfType(db, type);
            if (material is null)
            {
                Console.WriteLine($"info  material (live): no {type} material in the install — skipped");
                continue;
            }
            var tab = new AssetEditorViewModel(material, (_, _) => { }, _ => { }, _ => { }, (_, name) => byName.GetValueOrDefault(name));
            var layout = FormLayout(tab);
            var sections = layout.Select(s => s.Section).ToList();
            Console.WriteLine($"info  material (live): {material.Name} ({type}): {tab.VisiblePropertyCount} fields — {Describe(layout)}");
            if (Environment.GetEnvironmentVariable("APEX_MATERIAL_DUMP") == "1")
                foreach (var (section, rows) in layout)
                    Console.WriteLine($"        {section}: {string.Join(", ", rows)}");

            var categoryRow = tab.AllSentinel.All.OfType<ChoicePropertyViewModel>().First(p => p.Key == "materialCategory");
            var typeRow = tab.AllSentinel.All.OfType<ChoicePropertyViewModel>().First(p => p.Key == "materialType");
            Check($"material (live): {material.Name}: Material Type lists every type of its category ({typeRow.Choices.Length}, '{categoryRow.Value}')",
                typeRow.Choices.Length > 1 && typeRow.Choices.Contains(type, StringComparer.OrdinalIgnoreCase));
            Check($"material (live): {material.Name}: Material Category lists every category ({categoryRow.Choices.Length})",
                categoryRow.Choices.Length == categories.Length);
            Check($"material (live): {material.Name}: its type and category are not flagged ({typeRow.Problem ?? categoryRow.Problem ?? "no problem"})",
                typeRow.Problem is null && categoryRow.Problem is null);
            Check($"material (live): {material.Name}: the rail lists only the sections it shows, plus the schema's ({tab.RailItems.Count(c => c.VisibleCount == 0)} empty: {string.Join(", ", tab.RailItems.Where(c => c.VisibleCount == 0).Select(c => c.Name))})",
                tab.RailItems.Count(c => c.VisibleCount == 0) <= 3);
            Check($"material (live): {material.Name}: shows its techsetdef's fields ({tab.VisiblePropertyCount} visible, 15 before the host calls)",
                tab.VisiblePropertyCount > 30);
            Check($"material (live): {material.Name}: sections in APE's order — General first, the techsetdef's sections, then Special Properties last",
                sections.FirstOrDefault() == "General" && sections.LastOrDefault(s => s != "Other") == "Special Properties"
                && sections.IndexOf("Color") is > 0 and var color && color < sections.IndexOf("Special Properties"));
            // matError is a label APE writes only for an invalid type; labels have no row, so its key shows raw.
            Check($"material (live): {material.Name}: no raw 'Other' rows — every key it saves is a material.awi entry",
                layout.FirstOrDefault(s => s.Section == "Other").Rows is not { } other || other.All(r => r == "Mat Error"));
            // Where this type's own techsetdef files each shown field, against the section the form puts it in.
            var tweaks = techsets?.TweaksOf(type) ?? Array.Empty<Apex.Render.Data.Techsets.TechsetdefTweak>();
            var shown = tab.AllSentinel.All.Where(p => !p.IsRuleHidden).ToDictionary(p => p.Key, p => p.Def.Category, StringComparer.OrdinalIgnoreCase);
            var elsewhere = tweaks.SelectMany(t => t.Fields.Select(f => (Field: f, Section: t.Category.Split('.')[0].Trim())))
                .Where(f => shown.TryGetValue(f.Field, out var at) && at != f.Section)
                .Select(f => $"{f.Field} in {shown[f.Field]}, its techsetdef says {f.Section}").Distinct().ToList();
            Console.WriteLine($"info  material (live): {material.Name}: {elsewhere.Count} fields in another section than {type}.techsetdef gives{string.Concat(elsewhere.Take(4).Select(e => "; " + e))}");
            Check($"material (live): {material.Name}: the colour map shows under Color, first, titled by the techsetdef",
                layout.FirstOrDefault(s => s.Section == "Color").Rows?.FirstOrDefault() is "Color Map" or "Color Map 1");
        }

        // Across every material type: how many of the fields its techsetdef tweaks land in another section than it says.
        if (techsets is not null)
        {
            int fields = 0, moved = 0, typesMoved = 0;
            var examples = new List<string>();
            foreach (var category in techsets.MaterialCategories)
                foreach (var type in techsets.MaterialTypesIn(category))
                {
                    var any = false;
                    foreach (var t in techsets.TweaksOf(type))
                        foreach (var f in t.Fields)
                        {
                            if (schema.Find(f) is not { } def)
                                continue;
                            fields++;
                            if (def.Category == t.Category.Split('.')[0].Trim() || def.Category == "Material")
                                continue;
                            moved++;
                            any = true;
                            if (examples.Count < 5)
                                examples.Add($"{type}: {f} in {def.Category}, says {t.Category}");
                        }
                    typesMoved += any ? 1 : 0;
                }
            Console.WriteLine($"info  material (live): before a material's type is known (the schema), {moved:N0} of {fields:N0} tweaked fields over every type sit in another section than their type's techsetdef gives — an open material is arranged by its own type ({typesMoved} types){string.Concat(examples.Select(e => "; " + e))}");
        }

        // Changing the type re-runs material.awi: lit's fields give way to lit_advanced_fullspec's.
        if (MaterialOfType(db, "lit") is { } lit)
        {
            var tab = new AssetEditorViewModel(lit, (_, _) => { }, _ => { }, _ => { }, (_, name) => byName.GetValueOrDefault(name));
            var before = FormLayout(tab).SelectMany(s => s.Rows.Select(r => $"{s.Section}/{r}")).ToList();
            var typeRow = tab.AllSentinel.All.OfType<ChoicePropertyViewModel>().First(p => p.Key == "materialType");
            var categoryRow = tab.AllSentinel.All.OfType<ChoicePropertyViewModel>().First(p => p.Key == "materialCategory");
            categoryRow.Value = "Geometry Advanced";
            Check($"material (live): changing the category lists its types ({typeRow.Choices.Length}, lit_advanced_fullspec among them)",
                typeRow.Choices.Contains("lit_advanced_fullspec"));
            typeRow.Value = "lit_advanced_fullspec";
            var after = FormLayout(tab).SelectMany(s => s.Rows.Select(r => $"{s.Section}/{r}")).ToList();
            var gained = after.Except(before).ToList();
            var lost = before.Except(after).ToList();
            Console.WriteLine($"info  material (live): {lit.Name} lit → lit_advanced_fullspec: {before.Count} → {after.Count} fields (+{gained.Count}: {string.Join(", ", gained.Take(6))}; −{lost.Count}: {string.Join(", ", lost.Take(6))})");
            Check("material (live): changing the type reshows the fields (lit_advanced_fullspec adds its own)", gained.Count > 0);
            typeRow.Value = "not_a_techsetdef";
            Check($"material (live): an unknown type shows no techsetdef fields ({FormLayout(tab).Sum(s => s.Rows.Count)} visible)",
                !FormLayout(tab).Any(s => s.Section is "Color" or "FrameBuffer Operations"));
            while (lit.History.CanUndo)
                tab.UndoCommand.Execute(null);
            Check("material (live): the type edits undo back to the asset as it was",
                typeRow.Value == "lit" && FormLayout(tab).SelectMany(s => s.Rows.Select(r => $"{s.Section}/{r}")).SequenceEqual(before));
        }
    }

    /// <summary>
    /// The Material Type dropdown with real input in the live app: a click opens it on every type of the material's
    /// category, arrowing to another type and Enter picks it, and the form follows (fields of the new type show).
    /// </summary>
    private static void LiveMaterialUi(Avalonia.Controls.Window window, MainViewModel vm, AssetDatabase db, List<PerfResult> results)
    {
        if (MaterialOfType(db, "lit") is not { } lit)
            return;
        vm.OpenAsset(lit);
        Settle(window);
        var tab = vm.ActiveTab!;
        var typeRow = tab.AllSentinel.All.OfType<ChoicePropertyViewModel>().First(p => p.Key == "materialType");
        var view = FieldView(window, tab, typeRow);
        var combo = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(view).OfType<Avalonia.Controls.ComboBox>().First();
        Click(window, combo);
        Settle(window);
        Check($"material ui (live): a click opens Material Type on every Geometry type ({combo.ItemCount} items)",
            combo.IsDropDownOpen && combo.ItemCount == typeRow.Choices.Length && combo.ItemCount > 100);
        var before = FormLayout(tab).SelectMany(s => s.Rows.Select(r => $"{s.Section}/{r}")).ToList();
        var index = Array.IndexOf(typeRow.Choices, "lit");
        var target = typeRow.Choices[index + 1];
        var clock = System.Diagnostics.Stopwatch.StartNew();
        KeyStroke(window, Avalonia.Input.Key.Down);
        KeyStroke(window, Avalonia.Input.Key.Enter);
        Settle(window);
        var ms = clock.Elapsed.TotalMilliseconds;
        var after = FormLayout(tab).SelectMany(s => s.Rows.Select(r => $"{s.Section}/{r}")).ToList();
        Console.WriteLine($"info  material ui (live): lit → {target} by keyboard: {before.Count} → {after.Count} fields in {ms:0} ms (with layout)");
        Check($"material ui (live): Down + Enter picks the next type ({typeRow.Value}, wanted {target}) and closes the list",
            typeRow.Value == target && !combo.IsDropDownOpen);
        var expected = new AssetEditorViewModel(lit, (_, _) => { }, _ => { }, _ => { }, (_, _) => null);
        var expectedKeys = expected.AllSentinel.All.Where(p => !p.IsRuleHidden).Select(p => p.Key).OrderBy(k => k).ToList();
        var shownKeys = tab.AllSentinel.All.Where(p => !p.IsRuleHidden).Select(p => p.Key).OrderBy(k => k).ToList();
        Check($"material ui (live): the open form shows the fields {target} shows when opened fresh ({shownKeys.Count} vs {expectedKeys.Count})",
            shownKeys.SequenceEqual(expectedKeys, StringComparer.OrdinalIgnoreCase));
        while (lit.History.CanUndo)
            tab.UndoCommand.Execute(null);
        Settle(window);
        Check("material ui (live): undo puts the type back", typeRow.Value == "lit");
    }

    /// <summary>
    /// The form keeps the schema's order for everything the asset's run did not move: a field a choice or checkbox
    /// reveals stays beside its controller, not at the end of its section. A weapon, a helicopter (its run hides the
    /// tread fields) and its tread fields revealed by a type change, an image; and image's Map section keeps
    /// registration order (semantic, then baseImage). Also: a new material gets its category's first type.
    /// </summary>
    private static void LiveArrangeKeepsPlace(AssetDatabase db)
    {
        var byName = db.Assets
            .GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var heli = db.Assets.FirstOrDefault(a => a.Type.Equals("vehicle", StringComparison.OrdinalIgnoreCase)
            && a.ScanProperties.GetValueOrDefault("type") == "helicopter");
        var picks = new List<AssetRecord?>
        {
            db.Assets.FirstOrDefault(a => a.Type.Equals("bulletweapon", StringComparison.OrdinalIgnoreCase)),
            heli,
            db.Assets.FirstOrDefault(a => a.Type.Equals("image", StringComparison.OrdinalIgnoreCase)),
        };
        foreach (var asset in picks.OfType<AssetRecord>())
        {
            var schema = SchemaRegistry.Get(asset.Type)!;
            var tab = new AssetEditorViewModel(asset, (_, _) => { }, _ => { }, _ => { }, (_, name) => byName.GetValueOrDefault(name));
            var rows = tab.AllSentinel.All.Where(p => schema.Find(p.Key) is not null).Select(p => p.Key).ToList();
            var schemaOrder = schema.Properties.Select(p => p.Key).ToList();
            var hidden = tab.AllSentinel.All.Count(p => p.IsRuleHidden);
            Check($"arrange (live): {asset.Type} {asset.Name} keeps the schema's order for every row ({hidden} hidden at open)",
                rows.SequenceEqual(schemaOrder, StringComparer.OrdinalIgnoreCase));
            if (asset != heli)
                continue;
            // Reveal the tread fields: they show where they belong, in Wheels between their neighbours.
            var scroll = tab.AllSentinel.All.First(p => p.Key == "texureScrollScale");
            var typeRow = tab.AllSentinel.All.OfType<ChoicePropertyViewModel>().First(p => p.Key == "type");
            var wasHidden = scroll.IsRuleHidden;
            typeRow.Value = "tank";
            var wheels = FormLayout(tab).FirstOrDefault(s => s.Section == "Wheels").Rows ?? new List<string>();
            var shown = tab.FlatRows.OfType<PropertyItemViewModel>().Select(p => p.Key).ToList();
            var at = shown.IndexOf("texureScrollScale");
            Check($"arrange (live): a helicopter turned tank shows its tread fields first in Wheels, beside each other (hidden before: {wasHidden}; Wheels: {string.Join(", ", wheels.Take(3))})",
                wasHidden && !scroll.IsRuleHidden && at >= 0 && at + 1 < shown.Count && shown[at + 1] == "wheelRotRate"
                && wheels.Count > 0 && wheels[0] == scroll.Label);
            while (heli.History.CanUndo)
                tab.UndoCommand.Execute(null);
        }
        if (heli is null)
            Console.WriteLine("info  arrange (live): no helicopter vehicle in the install — reveal not checked");
        if (SchemaRegistry.Get("image") is { } image)
        {
            var map = image.Properties.Where(p => p.Category == "Map").Select(p => p.Key).ToList();
            Check($"arrange (live): image's Map section keeps registration order ({string.Join(", ", map.Take(4))})",
                map.IndexOf("semantic") >= 0 && map.IndexOf("semantic") < map.IndexOf("baseImage"));
        }

        // A new material is created with a stored type (lit) and shows that type's fields.
        var created = new AssetRecord { Name = "apex_check_new_material", Type = "material", GdtName = "apex_check.gdt" };
        Apex.Editor.Services.Gdf.Techsetdefs.SeedNewAsset(created);
        Check($"material (live): a new material is created as lit, stored ('{created.Properties.GetValueOrDefault("materialType")}')",
            created.Properties.GetValueOrDefault("materialType") == "lit");
        var fresh = new AssetEditorViewModel(created, (_, _) => { }, _ => { }, _ => { }, (_, _) => null);
        var materialType = fresh.AllSentinel.All.OfType<ChoicePropertyViewModel>().First(p => p.Key == "materialType");
        var sections = FormLayout(fresh).Select(s => s.Section).ToList();
        Check($"material (live): a new material shows its stored type ({materialType.Value}) and its fields ({fresh.VisiblePropertyCount}: {string.Join(", ", sections)})",
            materialType.Value == "lit" && materialType.Choices.Length > 1 && sections.Contains("Special Properties"));
        // A material saved without a type shows none: the form never shows a type it wouldn't save.
        var untyped = new AssetRecord { Name = "apex_check_untyped_material", Type = "material", GdtName = "apex_check.gdt" };
        var blank = new AssetEditorViewModel(untyped, (_, _) => { }, _ => { }, _ => { }, (_, _) => null);
        Check("material (live): a material without a type shows none",
            blank.AllSentinel.All.OfType<ChoicePropertyViewModel>().First(p => p.Key == "materialType").Value == "");
    }
}
