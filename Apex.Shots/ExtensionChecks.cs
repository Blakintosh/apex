using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Apex.Editor.Models;
using Apex.Editor.Services.Extensions;
using Apex.Editor.Services.Gdt;
using Apex.Editor.Services.Save;
using Apex.Editor.ViewModels;
using Apex.Editor.Views;
using GdtEncoding = Apex.Render.Data.Gdt.GdtEncoding;
using K = Avalonia.Input.Key;

namespace Apex.Shots;

/// <summary>
/// Schema extensions: the visibleWhen evaluator, the manifest loader, the merge into an asset's editor (sidecar reads
/// and writes, inheritance, undo, save), the session journal, and the live app with real input on a temp install. The
/// weapon-tech fixture manifest is a copy of Fixtures\extensions; nothing reads the user's own extensions folder.
/// </summary>
public partial class Program
{
    private static string FixtureExtensions => Path.Combine(AppContext.BaseDirectory, "Fixtures", "extensions");

    private static void RunExtensionChecks(string outDir)
    {
        var saved = Environment.GetEnvironmentVariable(ExtensionRegistry.DirVariable);
        try
        {
            VisibleWhenChecks();
            ExtensionLoaderChecks();
            ExtensionEditorChecks();
            ExtensionSessionChecks();
            ExtensionUiChecks(outDir);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ExtensionRegistry.DirVariable, saved);
            ExtensionRegistry.Clear();
            SchemaRegistry.ResetToMock();
        }
    }

    /// <summary>A copy of the fixture extensions (plus any extra manifests) in a scratch folder.</summary>
    private static string ExtensionsDir(string label, params (string Folder, string Json)[] extra)
    {
        var dir = NewScratch(label);
        CopyTree(FixtureExtensions, dir);
        foreach (var (folder, json) in extra)
        {
            Directory.CreateDirectory(Path.Combine(dir, folder));
            File.WriteAllText(Path.Combine(dir, folder, ExtensionLoader.FileName), json);
        }
        return dir;
    }

    /// <summary>
    /// The fixture with its kick table's columns shown one by one (no <c>combine</c>): the record checks written before
    /// combine test a table cell per stored column, which a manifest without combine still gets.
    /// </summary>
    private static string PlainTablesDir(string label)
    {
        var dir = ExtensionsDir(label);
        var path = Path.Combine(dir, "weapon-tech", ExtensionLoader.FileName);
        var json = File.ReadAllText(path);
        var start = json.IndexOf("\"combine\"", StringComparison.Ordinal);
        var end = json.IndexOf("] } ],", start, StringComparison.Ordinal) + "] } ],".Length;
        File.WriteAllText(path, json.Remove(start, end - start));
        return dir;
    }

    // ═══ visibleWhen ═══════════════════════════════════════════════════════

    private static void VisibleWhenChecks()
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["on1"] = "1", ["off0"] = "0", ["empty"] = "", ["word"] = "on", ["no"] = "off", ["n5"] = "5", ["n05"] = "0.5",
            ["neg"] = "-2", ["text"] = "abc", ["path"] = @"a\b\c", ["upper"] = "HOLD",
        };
        string? Of(string k) => values.GetValueOrDefault(k);
        var cases = new (string Rule, bool Expected)[]
        {
            ("on1", true), ("off0", false), ("empty", false), ("word", true), ("no", false), ("text", true), ("missing", false),
            ("!off0", true), ("!!on1", true), ("on1 && off0", false), ("on1 || off0", true), ("off0 || empty || word", true),
            ("on1 && (off0 || n5 > 4)", true), ("!(on1 && off0)", true), ("!on1 == false", true), ("!off0 && on1", true),
            ("n5 == 5", true), ("n5 == 5.0", true), ("n05 < 1", true), ("n05 <= 0.5", true), ("neg >= -2", true), ("neg > -2", false),
            ("n5 != 4", true), ("text == \"ABC\"", true), ("text != 'abc'", true == false), ("upper == \"hold\"", true),
            ("path == \"a\\b\\c\"", true), ("empty == \"\"", true), ("missing == \"\"", true), ("empty > 0", false),
            ("text > 0", false), ("text < 0", false), ("word == true", true), ("no == false", true), ("on1 == true", true),
            ("true", true), ("false", false), ("1", true), ("0", false), ("  on1   ==1 ", true), ("off0 || on1 && off0", false),
        };
        var wrong = new List<string>();
        foreach (var (rule, expected) in cases)
        {
            var parsed = VisibleWhen.Parse(rule, out var error);
            if (parsed is null)
                wrong.Add($"{rule}: didn't parse ({error})");
            else if (parsed.Evaluate(Of) != expected)
                wrong.Add($"{rule}: {!expected}");
        }
        Check($"visibleWhen: {cases.Length} rules evaluate as written ({string.Join("; ", wrong)})", wrong.Count == 0);
        var keys = VisibleWhen.Parse("wtA && (wtB > 1 || !wtC) && wtA == \"x\"", out _)!.Keys;
        Check($"visibleWhen: the keys a rule reads ({string.Join(",", keys)})", keys.Count == 3 && keys.Contains("wtB") && keys.Contains("WTC"));

        var malformed = new[]
        {
            "", "   ", "wtA ==", "(wtA", "wtA)", "wtA = 1", "wtA & wtB", "wtA | wtB", "\"open", "wtA @ 1", "1.2.3", "wtA wtB",
            "== 1", "wtA == 1 == 1", "()", "!", "wtA &&", "wtA()", new string('(', 40) + "wtA" + new string(')', 40),
            new string('!', 40) + "wtA", "wtA == " + new string('1', VisibleWhen.MaxLength),
        };
        var accepted = malformed.Where(m => VisibleWhen.Parse(m, out var e) is not null || string.IsNullOrEmpty(e)).ToList();
        Check($"visibleWhen: {malformed.Length} malformed rules are refused with a reason, never thrown ({string.Join(" | ", accepted)})", accepted.Count == 0);
        VisibleWhen.Parse("(wtA", out var why);
        Check($"visibleWhen: the reason says where ('{why}')", why == "a '(' is never closed (at character 5)");
    }

    // ═══ Loader and merge ══════════════════════════════════════════════════

    private const string MinimalManifest =
        "{ \"apexSchema\": 1, \"id\": \"{ID}\", \"version\": \"1\", \"targets\": [\"weapon\"], \"sections\": [ { \"title\": \"S\", \"fields\": [ {FIELDS} ] } ] }";

    private static string Manifest(string id, string fields) => MinimalManifest.Replace("{ID}", id).Replace("{FIELDS}", fields);

    private static void ExtensionLoaderChecks()
    {
        SchemaRegistry.ResetToMock();
        var sw = Stopwatch.StartNew();
        var (fixture, fixtureDiagnostics) = ExtensionLoader.LoadAll(FixtureExtensions);
        var coldMs = sw.Elapsed.TotalMilliseconds;
        sw.Restart();
        for (var i = 0; i < 20; i++)
            ExtensionLoader.LoadAll(FixtureExtensions);
        var warmMs = sw.Elapsed.TotalMilliseconds / 20;
        Console.WriteLine($"info  extensions: loading the weapon-tech fixture takes {coldMs:0.0} ms the first time (JSON code warming up), {warmMs:0.00} ms after; it runs off the UI thread beside the deffiles");
        var wt = fixture.SingleOrDefault();
        var fields = wt?.Sections.SelectMany(s => s.Fields).ToList() ?? new();
        Check($"extensions: the weapon-tech fixture loads clean ({fields.Count} fields in {wt?.Sections.Count} sections; {string.Join("; ", fixtureDiagnostics)})",
            wt is { Id: "weapon-tech", EnabledBy: "wtEnabled", Notes.Count: 0 } && fixtureDiagnostics.Count == 0 && fields.Count >= 60
            && fields.All(f => f.Def.Extension == "weapon-tech" && f.Def.Key.StartsWith("wt")));
        PropertyDef F(string key) => fields.First(f => f.Def.Key == key).Def;
        Check("extensions: kinds, defaults and ranges map onto the editor's field kinds",
            F("wtEnabled") is { Kind: PropertyKind.Toggle, Default: "0" } && F("wtRecoil").Default == "1"
            && F("wtFireTimeMs") is { Kind: PropertyKind.Number, Default: "100", Step: 1, HasRange: false }
            && F("wtLocoWalkStrides") is { IsInteger: true, Min: 1, Max: 64, HasRange: true }
            && F("wtIdleActiveAnim") is { Kind: PropertyKind.AssetRef, RefType: "xanim" }
            && F("wtIkHands") is { Kind: PropertyKind.Choice, Default: "lr" } && F("wtIkHands").Choices.SequenceEqual(new[] { "l", "r", "lr" })
            && F("wtCamShakeAngles").Default == "-1" && F("wtSource") is { Kind: PropertyKind.AssetRef, RefType: "weapon" }
            && F("wtKickMaintain") is { Min: 0, Max: 1 } && F("wtSwayAdvFire").Default == "300,200,200,0.5,0.5");
        Check("extensions: the weapon target covers every weapon type and nothing else",
            wt is not null && new[] { "weapon", "bulletweapon", "projectileweapon", "dualwieldweapon", "dualwieldprojectileweapon", "grenadeweapon", "meleeweapon", "gasweapon" }.All(wt.AddsTo)
            && !new[] { "weaponcamo", "sharedweaponsounds", "xmodel", "attachment", "characterweaponcustomsettings" }.Any(wt.AddsTo));

        // ── Tolerance: one bad manifest leaves out that one alone ──
        var dir = ExtensionsDir("ext-tolerance",
            ("broken", "{ \"apexSchema\": 1, \"id\": \"broken\", "),
            ("future", "{ \"apexSchema\": 2, \"id\": \"future\", \"targets\": [\"weapon\"], \"sections\": [] }"),
            ("unversioned", "{ \"id\": \"unversioned\", \"targets\": [\"weapon\"], \"sections\": [] }"),
            ("notarget", "{ \"apexSchema\": 1, \"id\": \"notarget\", \"targets\": [], \"sections\": [] }"),
            ("badid", "{ \"apexSchema\": 1, \"id\": \"bad id\", \"targets\": [\"weapon\"], \"sections\": [] }"),
            ("dupe", "{ \"apexSchema\": 1, \"id\": \"weapon-tech\", \"targets\": [\"weapon\"], \"sections\": [] }"),
            ("newer", Manifest("newer", "{ \"key\": \"nwA\", \"kind\": \"number\", \"unit\": \"ms\" }").Replace("\"sections\"", "\"groups\": [], \"sections\"")),
            ("parts", Manifest("parts",
                "{ \"key\": \"ptOk\", \"kind\": \"toggle\" }, { \"kind\": \"text\" }, { \"key\": \"pt Bad\", \"kind\": \"text\" }, "
                + "{ \"key\": \"ptKind\", \"kind\": \"slider\" }, { \"key\": \"ptRef\", \"kind\": \"assetRef\" }, { \"key\": \"ptOk\", \"kind\": \"text\" }, "
                + "{ \"key\": \"ptRule\", \"kind\": \"text\", \"visibleWhen\": \"ptOk ==\" }, { \"key\": \"ptReads\", \"kind\": \"text\", \"visibleWhen\": \"ptNope\" }, "
                + "{ \"key\": \"ptDamage\", \"kind\": \"number\", \"min\": 5 }, 42")),
            ("switchless", "{ \"apexSchema\": 1, \"id\": \"switchless\", \"targets\": [\"xmodel\"], \"enabledBy\": \"slOn\", \"sections\": [ { \"title\": \"Model extras\", \"fields\": [ { \"key\": \"slA\", \"kind\": \"text\" } ] } ] }"),
            ("nofile", "") );
        File.Delete(Path.Combine(dir, "nofile", ExtensionLoader.FileName));
        var (manifests, diagnostics) = ExtensionLoader.LoadAll(dir);
        string Said(string ext) => string.Join(" | ", diagnostics.Where(d => d.Extension == ext).Select(d => $"{d.Problem}: {d.Message}"));
        Check($"extensions: one manifest's problems leave out only it ({string.Join(", ", manifests.Select(m => m.Id))})",
            manifests.Select(m => m.Id).SequenceEqual(new[] { "newer", "parts", "switchless", "weapon-tech" }));
        Check($"extensions: invalid JSON disables it with the line ('{Said("broken")}')",
            diagnostics.Any(d => d is { Extension: "broken", Problem: ExtensionProblem.Disabled } && d.Message.Contains("isn't valid JSON (line")));
        Check($"extensions: another apexSchema, or none, disables it ('{Said("future")}' · '{Said("unversioned")}')",
            Said("future").Contains("apexSchema 2; this Apex reads apexSchema 1") && Said("unversioned").Contains("has no apexSchema"));
        Check($"extensions: no target, a bad id or a taken id disables it ('{Said("notarget")}' · '{Said("badid")}' · '{Said("weapon-tech")}')",
            Said("notarget").Contains("targets no asset type") && Said("badid").Contains("isn't a plain name")
            && Said("weapon-tech").Contains("same id as another extension"));
        Check($"extensions: unknown members are noted and ignored ('{Said("newer")}')",
            diagnostics.Where(d => d.Extension == "newer").All(d => d.Problem == ExtensionProblem.Note)
            && Said("newer").Contains("'groups'") && Said("newer").Contains("'unit'") && manifests.First(m => m.Id == "newer").Notes.Count == 2);
        var parts = manifests.First(m => m.Id == "parts");
        var partKeys = parts.Sections.SelectMany(s => s.Fields).Select(f => f.Def.Key).ToList();
        Check($"extensions: a bad field is left out and the rest load ({string.Join(",", partKeys)}; '{Said("parts")}')",
            partKeys.SequenceEqual(new[] { "ptOk", "ptRule", "ptReads", "ptDamage" })
            && diagnostics.Count(d => d is { Extension: "parts", Problem: ExtensionProblem.Skipped }) == 7);
        var ptRule = parts.Sections[0].Fields.First(f => f.Def.Key == "ptRule");
        Check($"extensions: a rule that doesn't parse is reported and its field always shows ('{diagnostics.First(d => d.Message.StartsWith("ptRule")).Message}')",
            ptRule.Rule is null && Said("parts").Contains("ptRule's visibleWhen isn't valid") && Said("parts").Contains("ptReads's visibleWhen reads ptNope"));
        Check("extensions: a one-sided range is noted and dropped", parts.Sections[0].Fields.First(f => f.Def.Key == "ptDamage").Def.HasRange == false
            && Said("parts").Contains("only one of min and max"));
        var switchless = manifests.First(m => m.Id == "switchless");
        Check("extensions: an enabledBy key without a field gets a switch at the top of the first section",
            switchless.Sections[0].Fields[0].Def is { Key: "slOn", Kind: PropertyKind.Toggle, Default: "0", Category: "Model extras", Extension: "switchless" });
        var (none, noneSaid) = ExtensionLoader.LoadAll(Path.Combine(dir, "no-such-folder"));
        Check("extensions: no extensions folder is no extensions, silently", none.Count == 0 && noneSaid.Count == 0);

        // ── Merge: deffile keys stay the deffile's ──
        var clash = ExtensionsDir("ext-clash",
            ("clash", Manifest("clash", "{ \"key\": \"damage\", \"kind\": \"number\" }, { \"key\": \"clOwn\", \"kind\": \"text\" }, { \"key\": \"wtFireTimeMs\", \"kind\": \"number\" }")),
            ("flagclash", "{ \"apexSchema\": 1, \"id\": \"flagclash\", \"targets\": [\"weapon\"], \"enabledBy\": \"clipSize\", \"sections\": [ { \"title\": \"F\", \"fields\": [ { \"key\": \"fcA\", \"kind\": \"text\" } ] } ] }"));
        ExtensionRegistry.Load(clash);
        var merged = ExtensionRegistry.For("weapon");
        var mergedKeys = merged.SelectMany(m => m.Sections).SelectMany(s => s.Fields).Select(f => f.Def.Key).ToList();
        var mergeSaid = string.Join(" | ", ExtensionRegistry.Diagnostics.Select(d => d.ToString()));
        Check($"extensions: a field whose key the deffile declares is left out, with a note saying it belongs in the GDT ({mergeSaid})",
            !mergedKeys.Contains("damage") && mergedKeys.Contains("clOwn")
            && ExtensionRegistry.Diagnostics.Any(d => d is { Extension: "clash", Problem: ExtensionProblem.Skipped } && d.Message.StartsWith("damage is a weapon key")));
        Check("extensions: a key two extensions declare goes to the first (by id); the second's is left out",
            merged.First(m => m.Id == "weapon-tech").Sections.SelectMany(s => s.Fields).Any(f => f.Def.Key == "wtFireTimeMs") == false
            && merged.First(m => m.Id == "clash").Sections.SelectMany(s => s.Fields).Any(f => f.Def.Key == "wtFireTimeMs")
            && ExtensionRegistry.Diagnostics.Any(d => d.Extension == "weapon-tech" && d.Message.StartsWith("wtFireTimeMs is also a field of clash")));
        Check("extensions: an extension switched by a deffile key is left out of that type whole",
            merged.All(m => m.Id != "flagclash") && ExtensionRegistry.Diagnostics.Any(d => d is { Extension: "flagclash", Problem: ExtensionProblem.Disabled }));
        Check("extensions: a merge is remembered and a type nothing targets gets nothing",
            ReferenceEquals(merged, ExtensionRegistry.For("weapon")) && ExtensionRegistry.For("xmodel").Count == 0 && ExtensionRegistry.For("no_such_type").Count == 0);
        ExtensionRegistry.Clear();
    }

    // ═══ The editor: sidecar values, rules, inheritance, undo, save ═════════

    private const string ParentGdt =
        "{\r\n\t\"base_gun\" ( \"weapon.gdf\" )\r\n\t{\r\n\t\t\"displayName\" \"Base\"\r\n\t\t\"damage\" \"40\"\r\n\t}\r\n}\r\n";

    private const string ParentGdtx =
        "{\r\n\t\"base_gun\" ( \"weapon-tech\" )\r\n\t{\r\n\t\t\"wtEnabled\" \"1\"\r\n\t\t\"wtFireTimeMs\" \"80\"\r\n\t}\r\n"
        + "\t\"other_gun\" ( \"weapon-tech\" )\r\n\t{\r\n\t\t\"wtTilt\" \"1,2\"\r\n\t}\r\n}\r\n";

    private const string ChildGdt =
        "{\r\n\t\"base_gun_up\" [ \"base_gun\" ]\r\n\t{\r\n\t\t\"damage\" \"80\"\r\n\t}\r\n"
        + "\t\"solo_gun\" ( \"weapon.gdf\" )\r\n\t{\r\n\t\t\"displayName\" \"Solo\"\r\n\t}\r\n}\r\n";

    /// <summary>What a user sees in a tab: sections, rows, values and their marks, in form order.</summary>
    private static string EditorSignature(AssetEditorViewModel t) => string.Join("\n",
        t.RailItems.Select(c => $"[{c.Name}|{c.Extension}|{c.VisibleCount}]")
            .Concat(t.FlatRows.Select(r => r switch
            {
                CategoryViewModel c => "#" + c.Name,
                PropertyItemViewModel p => $"{p.Key}={p.RawValue}|{p.IsRuleHidden}|{p.IsChanged}|{p.IsOverride}|{p.ParentValue}|{p.Problem}|{p.Tooltip}",
                _ => r.ToString() ?? "",
            }))
            .Append($"{t.VisiblePropertyCount}/{t.PropertyCount}/{t.ModifiedCount}/{t.OverrideCount}/{t.ProblemCount}"));

    private static void ExtensionEditorChecks()
    {
        SchemaRegistry.ResetToMock();
        var dir = NewScratch("ext-editor");
        string Text(string p) => GdtEncoding.File.GetString(File.ReadAllBytes(p));
        var parentPath = Path.Combine(dir, "parent.gdt");
        var childPath = Path.Combine(dir, "child.gdt");
        File.WriteAllBytes(parentPath, GdtEncoding.GetBytes(ParentGdt));
        File.WriteAllBytes(ExtensionSidecar.PathFor(parentPath), GdtEncoding.GetBytes(ParentGdtx));
        File.WriteAllBytes(childPath, GdtEncoding.GetBytes(ChildGdt));
        var parent = LoadGdt(parentPath);
        var child = LoadGdt(childPath);
        var gdts = new List<GdtFile> { parent, child };
        var all = gdts.SelectMany(g => g.Assets).ToList();
        GdtLoader.ResolveParents(all);
        AssetRecord Asset(string name) => all.First(a => a.Name == name);
        GdtFile? GdtOf(AssetRecord r) => gdts.FirstOrDefault(g => g.Name == r.GdtName);
        AssetRecord? Resolve(string type, string name) => all.FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        AssetEditorViewModel Open(string name, bool extensions = true) =>
            new(Asset(name), (_, _) => { }, _ => { }, _ => { }, Resolve, null, extensions ? GdtOf : null);

        // ── Nothing installed: the editor is the editor it was ──
        ExtensionRegistry.Clear();
        var without = EditorSignature(Open("solo_gun", extensions: false));
        var withNone = EditorSignature(Open("solo_gun"));
        var onlyModels = NewScratch("ext-models-only");
        Directory.CreateDirectory(Path.Combine(onlyModels, "models"));
        File.WriteAllText(Path.Combine(onlyModels, "models", ExtensionLoader.FileName),
            "{ \"apexSchema\": 1, \"id\": \"models\", \"targets\": [\"xmodel\"], \"sections\": [ { \"title\": \"M\", \"fields\": [ { \"key\": \"mA\", \"kind\": \"text\" } ] } ] }");
        ExtensionRegistry.Load(onlyModels);
        var withOther = EditorSignature(Open("solo_gun"));
        Check("extensions: with none installed, or none for this type, a tab is row for row the tab without extensions",
            without == withNone && without == withOther && parent.Extensions!.Blocks.Count == 2 && child.Extensions is null);

        // ── The fixture on a root weapon ──
        ExtensionRegistry.Load(ExtensionsDir("ext-editor-fixture"));
        var tab = Open("solo_gun");
        var solo = tab.Record;
        // The form keeps them after the deffile's; the rail lists them first, where they are found, but only those that show
        // something: off, that is the switch's section alone.
        var formOrder = tab.AllSentinel.All.Select(tab.CategoryOf).OfType<CategoryViewModel>().Distinct().ToList();
        var fromFirst = formOrder.SkipWhile(c => !c.IsExtension).ToList();
        var extSections = formOrder.Where(c => c.IsExtension).ToList();
        List<CategoryViewModel> Applying(AssetEditorViewModel t) => extSections.Where(c => c.All.Any(p => !p.IsRuleHidden)).ToList();
        Check($"extensions: weapon-tech's sections follow the deffile's in the form and lead the rail, named for their extension ({string.Join(", ", tab.RailItems.Where(c => c.IsExtension).Select(c => c.Name))})",
            extSections.Count == 9 && extSections.All(c => c.Extension == "weapon-tech" && c.ExtensionTip!.Contains("child.gdtx"))
            && fromFirst.Take(9).SequenceEqual(extSections) && fromFirst.Skip(9).All(c => c.Name == "Other")
            && tab.RailItems.TakeWhile(c => c.IsExtension).SequenceEqual(Applying(tab)) && Applying(tab).Count == 1);
        PropertyItemViewModel Row(AssetEditorViewModel t, string key) => t.AllSentinel.All.First(p => p.Key == key);
        bool Shown(AssetEditorViewModel t, string key) => !Row(t, key).IsRuleHidden && t.FlatRows.Contains(Row(t, key));
        var extRows = tab.AllSentinel.All.Where(p => p.Def.Extension.Length > 0).ToList();
        Check($"extensions: off by default, only the switch shows ({extRows.Count(p => !p.IsRuleHidden)} of {extRows.Count} shown)",
            extRows.Count(p => !p.IsRuleHidden) == 1 && Shown(tab, "wtEnabled") && Row(tab, "wtEnabled").RawValue == "0");

        var gdtBefore = new Dictionary<string, string>(solo.Properties, StringComparer.OrdinalIgnoreCase);
        Row(tab, "wtEnabled").RawValue = "1";
        Check("extensions: turning it on shows its sections and writes the switch to the asset's .gdtx block, not the GDT",
            Shown(tab, "wtFireTimeMs") && Shown(tab, "wtSwayAdv") && child.Extensions?.Get("solo_gun", "weapon-tech", "wtEnabled") == "1"
            && !solo.ValuesTouched && solo.Properties.Count == gdtBefore.Count && !solo.Properties.ContainsKey("wtEnabled"));
        Check($"extensions: on, its sections join the rail, ahead of the deffile's ({tab.RailItems.TakeWhile(c => c.IsExtension).Count()} of 9)",
            tab.RailItems.TakeWhile(c => c.IsExtension).SequenceEqual(Applying(tab)) && Applying(tab).Count > 1);
        Check("extensions: field rules follow the values they read",
            !Shown(tab, "wtLocoJogWeight") && !Shown(tab, "wtIkHands") && !Shown(tab, "wtAdditiveBulletMag"));
        Row(tab, "wtLocoJogLeaf").RawValue = "186";
        Row(tab, "wtIk").RawValue = "1";
        Check("extensions: a rule's key edited shows the fields it gates", Shown(tab, "wtLocoJogWeight") && Shown(tab, "wtIkHands") && Shown(tab, "wtIkOrient"));
        Row(tab, "wtRecoil").RawValue = "0";
        Check("extensions: a section rule hides the whole section (wtRecoil off hides Kick), and the rail no longer lists it",
            !Shown(tab, "wtFireTimeMs") && !Shown(tab, "wtTilt") && Shown(tab, "wtRecoil") && child.Extensions!.Get("solo_gun", "weapon-tech", "wtRecoil") == "0"
            && !tab.RailItems.Contains(tab.CategoryOf(Row(tab, "wtFireTimeMs"))!));
        Row(tab, "wtRecoil").RawValue = "1";
        Check("extensions: back to its default, a key the block didn't have goes again (a missing key is the default); the section is back in the rail",
            Shown(tab, "wtFireTimeMs") && child.Extensions!.Get("solo_gun", "weapon-tech", "wtRecoil") is null
            && tab.RailItems.Contains(tab.CategoryOf(Row(tab, "wtFireTimeMs"))!));

        var fire = Row(tab, "wtFireTimeMs");
        fire.RawValue = "63";
        Check($"extensions: an edit marks the row changed and lists it ({tab.Changes.Count} changes)",
            fire.IsChanged && tab.Changes.Any(c => c.Item == fire) && child.Extensions!.Get("solo_gun", "weapon-tech", "wtFireTimeMs") == "63"
            && solo.CountSessionChanges() == 0);
        tab.UndoCommand.Execute(null);
        Check($"extensions: Ctrl+Z undoes an extension edit ({fire.RawValue})",
            fire.RawValue == "100" && child.Extensions!.Get("solo_gun", "weapon-tech", "wtFireTimeMs") is null && !fire.IsChanged);
        tab.RedoCommand.Execute(null);
        Check("extensions: and redo puts it back", fire.RawValue == "63" && child.Extensions!.Get("solo_gun", "weapon-tech", "wtFireTimeMs") == "63");

        // A rule that doesn't parse shows its field.
        var badRule = NewScratch("ext-bad-rule");
        Directory.CreateDirectory(Path.Combine(badRule, "bad"));
        File.WriteAllText(Path.Combine(badRule, "bad", ExtensionLoader.FileName), Manifest("bad",
            "{ \"key\": \"bdGate\", \"kind\": \"toggle\" }, { \"key\": \"bdShown\", \"kind\": \"text\", \"visibleWhen\": \"bdGate &&\" }, { \"key\": \"bdHidden\", \"kind\": \"text\", \"visibleWhen\": \"bdGate\" }"));
        ExtensionRegistry.Load(badRule);
        var badTab = Open("solo_gun");
        Check("extensions: a field whose rule doesn't parse shows, a valid rule still hides",
            Shown(badTab, "bdShown") && !Shown(badTab, "bdHidden"));
        ExtensionRegistry.Load(ExtensionsDir("ext-editor-fixture-2"));

        // ── Inheritance across GDTs ──
        var up = Open("base_gun_up");
        var upFire = Row(up, "wtFireTimeMs");
        Check($"extensions: a derived weapon inherits its parent's extension data from the parent's GDT ({upFire.RawValue}, inherited {upFire.IsInherited})",
            Shown(up, "wtFireTimeMs") && upFire.RawValue == "80" && upFire.IsInherited && upFire.ParentValue == "80" && !upFire.IsChanged
            && Row(up, "wtEnabled") is { RawValue: "1", IsInherited: true } && child.Extensions!.Find("base_gun_up", "weapon-tech") is null);
        Check("extensions: inheriting reads only the parent's own block",
            parent.Extensions!.Find("other_gun", "weapon-tech") is { IsMaterialized: false });
        var overridesBefore = up.OverrideCount;
        upFire.RawValue = "70";
        Check("extensions: overriding writes the derived weapon's own block in its GDT's .gdtx",
            upFire.IsOverride && up.OverrideCount == overridesBefore + 1 && child.Extensions!.Get("base_gun_up", "weapon-tech", "wtFireTimeMs") == "70"
            && parent.Extensions.Get("base_gun", "weapon-tech", "wtFireTimeMs") == "80");
        upFire.RevertToParentCommand.Execute(null);
        Check("extensions: reverting to the parent drops the copy, so it inherits again",
            upFire.RawValue == "80" && !upFire.IsOverride && child.Extensions!.Get("base_gun_up", "weapon-tech", "wtFireTimeMs") is null);
        parent.Extensions.Set("base_gun", "weapon-tech", "wtFireTimeMs", "90");
        up.RefreshInherited(new[] { "wtFireTimeMs" });
        Check($"extensions: an edit to the parent's data reaches the derived tab ({upFire.RawValue})", upFire.RawValue == "90" && upFire.IsInherited);
        parent.Extensions.Set("base_gun", "weapon-tech", "wtEnabled", "0");
        up.RefreshInherited(new[] { "wtEnabled" });
        Check("extensions: the parent turned off turns the derived weapon's sections off, values kept",
            !Shown(up, "wtFireTimeMs") && parent.Extensions.Get("base_gun", "weapon-tech", "wtFireTimeMs") == "90");
        parent.Extensions.Set("base_gun", "weapon-tech", "wtEnabled", "1");
        up.RefreshInherited(null);
        Check("extensions: and back on", Shown(up, "wtFireTimeMs"));

        // ── Save: through the planner, temp files only ──
        Row(tab, "wtSource").RawValue = @"ar_base\copy";
        var problems = new List<string>();
        var requests = GdtSavePlanner.Plan(gdts, all, Array.Empty<AssetRecord>(), Array.Empty<GdtFile>(), null, problems);
        var result = NewSaveService("ext-editor").Save(requests);
        foreach (var f in result.Files)
            GdtSavePlanner.Commit(f);
        tab.RebaseChanges();
        var childx = ExtensionSidecar.PathFor(childPath);
        const string soloBlock = "{\r\n\t\"solo_gun\" ( \"weapon-tech\" )\r\n\t{\r\n\t\t\"wtEnabled\" \"1\"\r\n\t\t\"wtFireTimeMs\" \"63\"\r\n\t\t\"wtIk\" \"1\"\r\n\t\t\"wtLocoJogLeaf\" \"186\"\r\n\t\t\"wtSource\" \"ar_base\\copy\"\r\n\t}\r\n}\r\n";
        Check($"extensions: saving writes the .gdtx beside the GDT and leaves the GDTs byte for byte ({string.Join(", ", result.Files.Select(f => $"{Path.GetFileName(f.Request.Path)} {f.Status}"))}{string.Join("; ", problems)})",
            problems.Count == 0 && result.AllSucceeded && Text(childx) == soloBlock && Text(childPath) == ChildGdt && Text(parentPath) == ParentGdt
            && Text(ExtensionSidecar.PathFor(parentPath)) == ParentGdtx.Replace("\"80\"", "\"90\""));
        Check("extensions: after the save nothing reads as changed", !tab.AllSentinel.All.Any(p => p.IsChanged) && tab.Changes.Count == 0);
        var reread = LoadGdt(childPath);
        Check("extensions: the saved values read back raw (backslashes literal)",
            reread.Extensions?.Get("solo_gun", "weapon-tech", "wtSource") == @"ar_base\copy" && reread.Extensions.Get("solo_gun", "weapon-tech", "wtFireTimeMs") == "63");

        // Undo all puts every extension value back, as one step.
        fire.RawValue = "64";
        Row(tab, "wtIk").RawValue = "0";
        tab.RevertAllCommand.Execute(null);
        Check("extensions: Undo all reverts extension edits too",
            fire.RawValue == "63" && Row(tab, "wtIk").RawValue == "1" && child.Extensions!.Find("solo_gun", "weapon-tech")!.CountSessionChanges() == 0);
        tab.UndoCommand.Execute(null);
        Check("extensions: and Ctrl+Z takes Undo all back", fire.RawValue == "64" && Row(tab, "wtIk").RawValue == "0");
        ExtensionRegistry.Clear();
    }

    // ═══ The app: dirty marks, journal, discard (mock data) ═════════════════

    private static void ExtensionSessionChecks()
    {
        var root = NewScratch("ext-session");
        Environment.SetEnvironmentVariable(ExtensionRegistry.DirVariable, ExtensionsDir("ext-session-fixture"));
        var vm = new MainViewModel(root);
        var window = ShowJournalWindow(vm);
        try
        {
            vm.OpenByName("wpn_ar_havoc_zm");
            var tab = vm.ActiveTab!;
            var record = tab.Record;
            var gdt = vm.GdtOf(record)!;
            var before = vm.SessionEditCount;
            tab.AllSentinel.All.First(p => p.Key == "wtEnabled").RawValue = "1";
            tab.AllSentinel.All.First(p => p.Key == "wtInspect").RawValue = "1";
            Check($"extensions: extension edits count as the asset's changes and mark it ({vm.SessionStateText})",
                vm.SessionEditCount == before + 2 && record.HasSessionEdits && gdt.Extensions?.Get(record.Name, "weapon-tech", "wtInspect") == "1");

            // The derived weapon's open tab follows its parent's extension data.
            vm.OpenByName("wpn_ar_havoc_zm_upgraded");
            var up = vm.ActiveTab!;
            Check("extensions: a derived weapon's tab shows what it inherits",
                up.AllSentinel.All.First(p => p.Key == "wtInspect") is { RawValue: "1", IsInherited: true });

            vm.FlushSessionToDisk(closing: true);
            window.Close();
            vm.Dispose();
            ExtensionRegistry.Clear();
            vm = new MainViewModel(root);
            window = ShowJournalWindow(vm);
            Pump(200);
            vm.OpenByName("wpn_ar_havoc_zm");
            var reopened = vm.ActiveTab!;
            Check($"extensions: unsaved extension values are kept across a restart ({vm.SessionStateText})",
                vm.SessionEditCount == 2 && reopened.AllSentinel.All.First(p => p.Key == "wtInspect").RawValue == "1"
                && vm.GdtOf(reopened.Record)?.Extensions?.Get("wpn_ar_havoc_zm", "weapon-tech", "wtEnabled") == "1");

            vm.DiscardSessionNow();
            Check($"extensions: Discard all puts extension data back too ({vm.SessionStateText})",
                vm.SessionEditCount == 0 && vm.GdtOf(reopened.Record)?.Extensions is null
                && reopened.AllSentinel.All.First(p => p.Key == "wtInspect").RawValue == "0" && !reopened.Record.HasSessionEdits);

            // A first value journaled and then taken back: its unsaved block goes, and a restart must not bring it back.
            var inspect = reopened.AllSentinel.All.First(p => p.Key == "wtInspect");
            inspect.RawValue = "1";
            vm.FlushJournalNow();
            inspect.RawValue = "0";
            vm.FlushSessionToDisk(closing: true);
            window.Close();
            vm.Dispose();
            ExtensionRegistry.Clear();
            vm = new MainViewModel(root);
            window = ShowJournalWindow(vm);
            Pump(200);
            vm.OpenByName("wpn_ar_havoc_zm");
            var again = vm.ActiveTab!;
            Check($"extensions: a value set and taken back before closing stays gone after a restart ({vm.SessionStateText})",
                vm.SessionEditCount == 0 && vm.GdtOf(again.Record)?.Extensions?.Get("wpn_ar_havoc_zm", "weapon-tech", "wtInspect") is null
                && again.AllSentinel.All.First(p => p.Key == "wtInspect").RawValue == "0");
        }
        catch (Exception ex)
        {
            Check($"extensions: session checks: {ex}", false);
        }
        finally
        {
            window.Close();
            vm.Dispose();
            Environment.SetEnvironmentVariable(ExtensionRegistry.DirVariable, NewScratch("no-extensions"));
            ExtensionRegistry.Clear();
        }
    }

    // ═══ The live app on a temp install, real input ═════════════════════════

    private static void ExtensionUiChecks(string outDir)
    {
        var install = NewScratch("ext-install");
        CopyTree(Path.Combine(InstallRoot, "deffiles"), Path.Combine(install, "deffiles"));
        var weaponRel = @"source_data\ar_ak47_h1.gdt";
        Directory.CreateDirectory(Path.Combine(install, "source_data"));
        File.Copy(Path.Combine(InstallRoot, weaponRel), Path.Combine(install, weaponRel));
        var weaponPath = Path.Combine(install, weaponRel);
        var sidecarPath = ExtensionSidecar.PathFor(weaponPath);
        // An orphan block (its asset isn't in the GDT) and a broken second extension: the startup notice says both.
        const string orphan = "{\r\n\t\"ghost_gun\" ( \"weapon-tech\" )\r\n\t{\r\n\t\t\"wtTilt\" \"1,2,3\"\r\n\t}\r\n}\r\n";
        File.WriteAllBytes(sidecarPath, GdtEncoding.GetBytes(orphan));
        var extensions = ExtensionsDir("ext-ui", ("broken", "{ \"apexSchema\": 1, "));
        var gdtBytes = File.ReadAllBytes(weaponPath);

        var saved = (Mock: Environment.GetEnvironmentVariable("APEX_FORCE_MOCK"), Root: Environment.GetEnvironmentVariable("APEX_BO3_ROOT"),
            Persist: Environment.GetEnvironmentVariable("APEX_NO_PERSIST"), Ext: Environment.GetEnvironmentVariable(ExtensionRegistry.DirVariable));
        Environment.SetEnvironmentVariable("APEX_FORCE_MOCK", null);
        Environment.SetEnvironmentVariable("APEX_BO3_ROOT", install);
        Environment.SetEnvironmentVariable("APEX_NO_PERSIST", "1");
        Environment.SetEnvironmentVariable(ExtensionRegistry.DirVariable, extensions);
        MainViewModel? vm = null;
        MainWindow? window = null;
        string Text(string p) => GdtEncoding.File.GetString(File.ReadAllBytes(p));
        try
        {
            vm = new MainViewModel(Path.Combine(install, "session"));
            window = ShowJournalWindow(vm);
            WaitUntil(() => vm.Status.StartsWith("Loaded"), 60_000);
            Pump(100);
            // Two unrelated problems, two calm notices: the broken extension's (the parser's line number in its tooltip,
            // not its text) and the orphan's.
            var broken = vm.Alerts.FirstOrDefault(a => a.Text.Contains("broken"));
            var orphaned = vm.Alerts.FirstOrDefault(a => a.Text.Contains("ghost_gun"));
            Check($"extensions ui: one calm notice per problem, the broken extension and the orphan, and nothing else happens ({string.Join(" | ", vm.Alerts.Select(a => a.Text))})",
                vm.Alerts.Count == 2 && broken is { IsError: false } && orphaned is { IsError: false } && !vm.IsConfirmOpen
                && !broken.Text.Contains("ghost_gun") && !orphaned.Text.Contains("broken")
                && orphaned.Text.Contains("keeps it as it is") && orphaned.Detail?.Contains("ar_ak47_h1.gdtx: ghost_gun (weapon-tech)") == true);
            Check($"extensions ui: the parser's detail is in the tooltip, not the banner ('{broken?.Text}' / '{broken?.Detail}')",
                broken is not null && !broken.Text.Contains("line ") && broken.Detail?.StartsWith("line ") == true);
            Capture(window, Path.Combine(outDir, "60-extensions-notice.png"));
            while (vm.IsAlertOpen)
                vm.DismissAlertCommand.Execute(null);

            var weapon = FindRecord(vm, "ar_ak47_h1.gdt", "bulletweapon");
            if (weapon is null)
            {
                Check("extensions ui: a real weapon to edit", false);
                return;
            }
            vm.OpenByName(weapon.Name);
            Pump(300);
            window.UpdateLayout();
            var tab = vm.ActiveTab!;
            var enabled = tab.AllSentinel.All.First(p => p.Key == "wtEnabled");
            ToggleButton? SwitchOf(PropertyItemViewModel row) => window!.GetVisualDescendants().OfType<PropertyEditorView>()
                .Where(v => ReferenceEquals(v.DataContext, row) && v.IsEffectivelyVisible)
                .SelectMany(v => v.GetVisualDescendants().OfType<ToggleButton>()).FirstOrDefault(b => b.IsEffectivelyVisible);
            var form = window.GetVisualDescendants().OfType<ScrollViewer>().First(s => s.Name == "FormScroll" && s.IsEffectivelyVisible);
            bool InView(Control c) => c.TranslatePoint(new Point(0, c.Bounds.Height), form) is { } bottom
                && c.TranslatePoint(default, form) is { Y: >= 0 } && bottom.Y <= form.Viewport.Height;
            void Reveal(string key)
            {
                tab.RevealProperty(key);
                Pump(50);
                window.UpdateLayout();
            }
            Reveal("wtEnabled");
            Check("extensions ui: revealing the form's last row (the switch, while off) brings all of it into view",
                SwitchOf(enabled) is { } shown && InView(shown));
            Capture(window, Path.Combine(outDir, "61-extensions-off.png"));

            // A real click on the switch.
            var toggle = SwitchOf(enabled);
            if (toggle is not null)
                Click(window, toggle);
            Pump(50);
            Check($"extensions ui: a real click on Enabled turns weapon-tech on ({enabled.RawValue}, {vm.SessionStateText})",
                toggle is not null && enabled.RawValue == "1" && !tab.AllSentinel.All.First(p => p.Key == "wtFireTimeMs").IsRuleHidden
                && vm.SessionEditCount == 1 && weapon.HasSessionEdits);

            // Keyboard: Ctrl+↑ in an extension number field, then Space on a switch.
            var fire = tab.AllSentinel.All.First(p => p.Key == "wtFireTimeMs");
            FocusNumber(window, vm, "wtFireTimeMs");
            Key(window, K.Up, RawInputModifiers.Control);
            var stepped = fire.RawValue;
            Check($"extensions ui: Ctrl+↑ steps an extension number (100 → {stepped})", stepped != "100" && fire.IsChanged);
            var inspect = tab.AllSentinel.All.First(p => p.Key == "wtInspect");
            Reveal("wtInspect");
            var inspectSwitch = window.FocusManager?.GetFocusedElement() as ToggleButton;
            if (inspectSwitch is not null)
                Key(window, K.Space);
            Check($"extensions ui: Space on a focused extension switch flips it ({inspect.RawValue})", inspectSwitch is not null && inspect.RawValue == "1");
            tab.FocusedProperty = fire;
            Pump(50);
            Check($"extensions ui: the Inspector says where the value lives ('{vm.InspectorOriginText}')",
                vm.InspectorOriginText == "From the weapon-tech extension. Saved in ar_ak47_h1.gdtx, beside the GDT.");
            Reveal("wtFireTimeMs");
            Capture(window, Path.Combine(outDir, "62-extensions-on.png"));

            // ── Ctrl+S: the .gdtx gets the block, the GDT stays byte for byte ──
            window.KeyPress(K.S, RawInputModifiers.Control, PhysicalKey.S, "s");
            WaitUntil(() => !vm.IsSaveRunning, 10_000);
            Pump();
            var block = $"\t\"{weapon.Name}\" ( \"weapon-tech\" )\r\n\t{{\r\n\t\t\"wtEnabled\" \"1\"\r\n\t\t\"wtFireTimeMs\" \"{stepped}\"\r\n\t\t\"wtInspect\" \"1\"\r\n\t}}\r\n";
            Check($"extensions ui: Ctrl+S writes the .gdtx and leaves the GDT alone ('{vm.Status}', chip '{vm.SessionStateText}')",
                Text(sidecarPath) == orphan[..^3] + block + "}\r\n" && File.ReadAllBytes(weaponPath).AsSpan().SequenceEqual(gdtBytes)
                && vm.Status == "Saved ar_ak47_h1.gdtx" && vm.SessionEditCount == 0 && !fire.IsChanged && !weapon.HasSessionEdits);

            // Ctrl+Z after the save: unsaved again, and saving writes it.
            Reveal("wtFireTimeMs");
            Key(window, K.Z, RawInputModifiers.Control);
            Key(window, K.Z, RawInputModifiers.Control);
            Check($"extensions ui: Ctrl+Z undoes extension edits after a save ({fire.RawValue}, {inspect.RawValue}; {vm.SessionStateText})",
                fire.RawValue == "100" && inspect.RawValue == "0" && vm.SessionEditCount == 2);
            Key(window, K.Z, RawInputModifiers.Control);
            window.KeyPress(K.S, RawInputModifiers.Control, PhysicalKey.S, "s");
            WaitUntil(() => !vm.IsSaveRunning, 10_000);
            Pump();
            Check($"extensions ui: undone back to nothing, the block goes and the orphan stays ('{vm.Status}')",
                Text(sidecarPath) == orphan && File.ReadAllBytes(weaponPath).AsSpan().SequenceEqual(gdtBytes) && vm.SessionEditCount == 0
                && enabled.RawValue == "0" && tab.AllSentinel.All.Count(p => p.Def.Extension.Length > 0 && !p.IsRuleHidden) == 1);

            // ── Light theme, on ──
            enabled.RawValue = "1";
            Reveal("wtEnabled");
            Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
            Pump(100);
            Capture(window, Path.Combine(outDir, "63-extensions-light.png"));
            Avalonia.Application.Current.RequestedThemeVariant = ThemeVariant.Dark;
            Pump();

            // ── Budgets: an extension field's keystroke and switch, and opening a weapon with and without them ──
            const int warm = 5, runs = 30;
            SilenceHeadlessRenderTimer();
            FocusNumber(window, vm, "wtFireTimeMs");
            var up = true;
            var step = Time(window, "Extension number: Ctrl+arrow step", PerfBudgets.Frame, warm, runs,
                _ => KeyStroke(window, up ? K.Up : K.Down, RawInputModifiers.Control), after: i => up = i % 2 == 1);
            // Where a click on a row's switch lands, the row revealed. Hit testing reads the last drawn scene, and the perf
            // gates park the headless render timer, so the form is drawn as it is now first. Alternate ends of the switch,
            // so two quick clicks never pair into a double-click.
            Point Aim(PropertyItemViewModel row, int n)
            {
                Reveal(row.Key);
                window.CaptureRenderedFrame();
                var sw = SwitchOf(row)!;
                return CentreOf(window, sw, (n % 2 == 0 ? -1 : 1) * Math.Min(10, sw.Bounds.Width / 3));
            }
            // One of weapon-tech's switches and one of the deffile's, clicked in turn: both meet the same heap and the same
            // machine, so a slow moment of the harness's own (a full collection of its large heap) lands on both alike.
            var core = tab.AllSentinel.All.First(p => p is TogglePropertyViewModel && p.Def.Extension.Length == 0 && !p.IsRuleHidden && !p.IsRuleDisabled);
            var clicks = (Extension: new List<double>(), Deffile: new List<double>());
            for (var i = 0; i < 2 * (warm + runs); i++)
            {
                var ext = i % 2 == 0;
                var row = ext ? inspect : core;
                var point = default(Point);
                var one = Time(window, "switch", PerfBudgets.Frame, 0, 1, _ => ClickAt(window, point), before: _ => point = Aim(row, i / 2));
                if (i >= 2 * warm)
                    (ext ? clicks.Extension : clicks.Deffile).Add(one.Median);
            }
            var click = Summarize("Extension switch (click)", PerfBudgets.Frame, clicks.Extension);
            var coreClick = Summarize($"Deffile switch (click), {core.Key}", PerfBudgets.Frame, clicks.Deffile);
            Gate($"perf (extensions): an extension switch click median {click.Median:0.0} ms, p95 {click.P95:0.0} ms (budget {PerfBudgets.Frame:0} ms; a deffile switch alongside: {coreClick.Median:0.0} / {coreClick.P95:0.0} ms)", click.Pass);
            Check($"perf (extensions): the switch clicks all landed ({inspect.RawValue})", inspect.RawValue == "1");
            // Turning it off shortens the form, which can scroll the switch: each click aims where it is now.
            var at = default(Point);
            var rules = Time(window, "Extension on/off (shows its rows)", PerfBudgets.Frame, warm, runs,
                _ => ClickAt(window, at), before: i => at = Aim(enabled, i));
            Check($"perf (extensions): the on/off clicks all landed ({enabled.RawValue} after {warm + runs})", enabled.RawValue == ((warm + runs) % 2 == 0 ? "1" : "0"));
            // Opening with and without, alternated so neither pays for the other's garbage or warm caches.
            var opens = (With: new List<double>(), Without: new List<double>());
            for (var i = 0; i < 2 * (warm + 15); i++)
            {
                var with = i % 2 == 0;
                if (with)
                    ExtensionRegistry.Load(extensions);
                else
                    ExtensionRegistry.Clear();
                var one = Time(window, "open", PerfBudgets.OpenAsset, 0, 1, _ => vm.OpenAsset(weapon), before: _ =>
                {
                    while (vm.OpenTabs.Count > 0)
                        vm.CloseActiveTabCommand.Execute(null);
                });
                if (i >= 2 * warm)
                    (with ? opens.With : opens.Without).Add(one.Median);
            }
            var openWith = Summarize("Open a weapon with weapon-tech", PerfBudgets.OpenAsset, opens.With);
            var openWithout = Summarize("Open the same weapon without extensions", PerfBudgets.OpenAsset, opens.Without);
            foreach (var r in new[] { step, coreClick, click, rules, openWith, openWithout }.OfType<PerfResult>())
                Console.WriteLine($"info  {r.Name}: median {r.Median:0.0} ms, p95 {r.P95:0.0} ms (wall {r.WallMedian:0.0} ms; budget {r.Budget:0} ms)");
            foreach (var r in new[] { step, rules })
                Gate($"perf (extensions): {r.Name} median {r.Median:0.0} ms, p95 {r.P95:0.0} ms (budget {r.Budget:0} ms)", r.Pass);
            // Opening against its budget is the open-asset perf gate's; here, what weapon-tech adds to it.
            Gate($"perf (extensions): weapon-tech adds less than a frame to opening a weapon (median {openWith.Median:0} ms with, {openWithout.Median:0} ms without)",
                openWith.Median <= openWithout.Median + PerfBudgets.Frame);
                    }
        catch (Exception ex)
        {
            Check($"extensions ui: {ex}", false);
        }
        finally
        {
            window?.Close();
            vm?.Dispose();
            Environment.SetEnvironmentVariable("APEX_FORCE_MOCK", saved.Mock);
            Environment.SetEnvironmentVariable("APEX_BO3_ROOT", saved.Root);
            Environment.SetEnvironmentVariable("APEX_NO_PERSIST", saved.Persist);
            Environment.SetEnvironmentVariable(ExtensionRegistry.DirVariable, saved.Ext);
            ExtensionRegistry.Clear();
            SchemaRegistry.ResetToMock();
        }
    }
}
