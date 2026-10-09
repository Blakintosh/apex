using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Apex.Editor.Models;
using Apex.Editor.Services;
using Apex.Editor.Services.Extensions;
using Apex.Editor.Services.Gdt;
using Apex.Editor.Services.Save;
using Apex.Editor.ViewModels;
using GdtEncoding = Apex.Render.Data.Gdt.GdtEncoding;

namespace Apex.Shots;

/// <summary>
/// The extension editor pass: values in named parts, labelled choices, combined columns, pasting and copying rows, sections
/// that fold while empty, the filter finding sections, the off line, notices and the export, turning an extension on or
/// off for many assets, and the status lines that name what changed. Pure checks, the editor on temp GDTs, the app on
/// mock data and on a temp install (EditorPassUiChecks). Every file written is a temp copy.
/// </summary>
public partial class Program
{
    private static void RunEditorPassChecks(string outDir)
    {
        var saved = Environment.GetEnvironmentVariable(ExtensionRegistry.DirVariable);
        try
        {
            PartsCodecChecks();
            EditorPassLoaderChecks();
            EditorPassEditorChecks();
            EditorPassSessionChecks();
            ManifestGrammarChecks();
            ConsumerChecks();
            EditorPassUiChecks(outDir);
            GrammarUiChecks(outDir);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ExtensionRegistry.DirVariable, saved);
            ExtensionRegistry.Clear();
            SchemaRegistry.ResetToMock();
        }
    }

    private static IReadOnlyList<PropertyDef> SpringParts() =>
        ExtensionLoader.LoadAll(FixtureExtensions).Manifests.Single().Sections.SelectMany(s => s.Fields)
            .First(f => f.Def.Key == "wtSpringViewHip").Parts!;

    // ═══ Parts: the codec ═══════════════════════════════════════════════════

    private static void PartsCodecChecks()
    {
        var parts = SpringParts();
        bool None(string _, string __) => true;
        string Set(string value, int index, string part) => PartsCodec.Set(value, index, part, parts);

        Check($"parts: the fixture's spring is five named number parts ({string.Join(", ", parts.Select(p => p.Label))})",
            parts.Select(p => p.Key).SequenceEqual(new[] { "accel", "retAccelScale", "retSpeedCurveScale", "maxPitch", "maxYaw" })
            && parts.All(p => p.Kind == PropertyKind.Number) && parts[0] is { HasRange: true, Min: 0, Max: 1000 });

        // An edit writes the part edited and nothing else; spaces, empty fields, extras and a trailing comma stay.
        var cases = new (string Value, int Index, string Part, string Expected, string What)[]
        {
            (" 30, 1.5 ,,4", 1, "2", " 30,2,,4", "odd spacing kept around the part edited"),
            (" 30, 1.5 ,,4", 1, "1.5", " 30, 1.5 ,,4", "the same number, spaced, is no edit"),
            ("30,1", 3, "9", "30,1,,9", "a part past the end fills the parts between, empty without a default"),
            ("1,2,3,4,5,6,# note", 0, "9", "9,2,3,4,5,6,# note", "values past the last part are kept"),
            ("1,2,", 0, "9", "9,2,", "a trailing comma is kept"),
            ("1,2,3", 2, "", "1,2", "emptying the last part ends the value there"),
            ("1,2,3", 1, "", "1,,3", "emptying a middle part leaves its place"),
            ("1,2,,", 1, "", "1", "emptying a part with nothing set after it drops the empty fields after it too"),
            ("", 0, "7", "7", "an unset value starts with the part"),
            ("", 2, "", "", "emptying a part an unset value doesn't have changes nothing"),
            (@"a\b,2", 1, "3", @"a\b,3", "backslashes are literal"),
        };
        var wrong = cases.Where(c => Set(c.Value, c.Index, c.Part) != c.Expected)
            .Select(c => $"{c.What}: '{c.Value}' part {c.Index + 1} = '{c.Part}' gave '{Set(c.Value, c.Index, c.Part)}', not '{c.Expected}'").ToList();
        Check($"parts: an edit writes its part alone, every other byte as it was ({string.Join(" | ", wrong)})", wrong.Count == 0);
        var fire = ExtensionLoader.LoadAll(FixtureExtensions).Manifests.Single().Sections.SelectMany(s => s.Fields).First(f => f.Def.Key == "wtSwayAdvFire").Parts!;
        Check("parts: a part past the end fills the parts between with their defaults",
            PartsCodec.Set("300", 4, "0.7", fire) == "300,,,,0.7" && PartsCodec.Set("300,200,200,0.5", 4, "", fire) == "300,200,200,0.5");

        string Said(string value) => string.Join(" | ", PartsCodec.Problems(parts, value, None).Select(p => $"{p.Part}:{p.Message}"));
        Check($"parts: an unset value has nothing wrong; a set one needs every part without a default ('{Said("30,1")}')",
            Said("") == "" && Said("30,1") == "2:Return curve is empty. | 3:Max climb is empty. | 4:Max drift is empty." && Said("30,1,1,4,2") == "");
        Check($"parts: each part is checked as its kind, and values past the last are said ('{Said("2000,x,1,4,2,9")}')",
            Said("2000,x,1,4,2,9") == "0:Stiffness: 2000 is outside 0–1000. | 1:Return speed: ‘x’ is not a number. | -1:There is a value after Max drift that isn't one of its parts; it is kept as written."
            && Said("30,1,1,4,2,") == "");
        Check($"parts: an integer part refuses a fraction, a defaulted part may be missing ('{string.Join(" | ", PartsCodec.Problems(fire, "300.5,200,200,0.5", None).Select(p => p.Message))}')",
            PartsCodec.Problems(fire, "300.5,200,200,0.5", None).Select(p => p.Message).SequenceEqual(new[] { "Fire (ms): 300.5 isn't a whole number." }));
        Check($"parts: which parts changed, in words ('{PartsCodec.Changed(parts, "30,1,1,4,2", "30,2,1,4,2")}', '{PartsCodec.Changed(parts, "30,1,1,4,2", "31,2,1,4,2")}', '{PartsCodec.Changed(parts, "1", "2,2,2")}')",
            PartsCodec.Changed(parts, "30,1,1,4,2", "30,2,1,4,2") == "Return speed" && PartsCodec.Changed(parts, "30,1,1,4,2", "31,2,1,4,2") == "Stiffness and Return speed"
            && PartsCodec.Changed(parts, "1", "2,2,2") == "3 parts" && PartsCodec.Changed(parts, " 30", "30") is null);
    }

    // ═══ The manifest: the new members, their mistakes, and older manifests ═══

    private static void EditorPassLoaderChecks()
    {
        SchemaRegistry.ResetToMock();
        var (fixture, fixtureSaid) = ExtensionLoader.LoadAll(FixtureExtensions);
        var wt = fixture.Single();
        var fields = wt.Sections.SelectMany(s => s.Fields).ToDictionary(f => f.Def.Key, StringComparer.OrdinalIgnoreCase);
        var kick = wt.Sections.SelectMany(s => s.Records).First(r => r.Def.Key == "wtKick#");
        Check($"manifest: the fixture uses every new member and loads clean ({string.Join("; ", fixtureSaid)})",
            fixtureSaid.Count == 0 && wt is { Title: "Weapon tech", OffNotice: "Weapon tech is off for this weapon.", Notes.Count: 0 }
            && wt.Notice!.StartsWith("The game doesn't read these yet.")
            && wt.Export == new ExtensionExport("ini-section", "[weapon:{asset}]", "Copy as weapon_tech.cfg")
            && wt.Sections.Where(s => s.CollapsedUnlessSet).Select(s => s.Title).SequenceEqual(new[] { "Ammo hide", "Hand IK" })
            && wt.Sections.First(s => s.Title == "Hand IK").Notice == "Experimental."
            && fields["wtSpringGunAds"].Parts?.Count == 5 && fields["wtSwayAdvFire"].Parts?.Count == 5
            && fields["wtSource"].Def is { Kind: PropertyKind.AssetRef, RefType: "weapon", Label: "Recoil from" });
        var shot = fields["wtEmptyLastShot"].Def;
        Check($"manifest: labelled choices store the value and show the label; a plain string in the list is its own label ({string.Join(", ", shot.ChoiceLabels ?? Array.Empty<string>())})",
            shot.Choices.SequenceEqual(new[] { "auto", "iw", "hold" }) && shot.ChoiceLabels!.SequenceEqual(new[] { "Automatic", "As IW does", "hold" })
            && shot.ChoiceLabel("IW") == "As IW does" && shot.ChoiceLabel("fast") == "custom: fast" && shot.ChoiceLabel("") == ""
            && fields["wtIkHands"].Def.ChoiceLabels is null);
        var applies = kick.DisplayColumns[0];
        Check($"manifest: combine shows ads and gun as one choice where ads was, the other columns as they are ({string.Join(", ", kick.DisplayColumns.Select(c => c.Def.Label))})",
            kick.Columns.Count == 8 && kick.DisplayColumns.Count == 7 && applies.Combine is { Columns: [0, 1] }
            && applies.Def is { Label: "Applies to", Kind: PropertyKind.Choice } && applies.Def.Choices.SequenceEqual(new[] { "0,0", "0,1", "1,0", "1,1" })
            && applies.Def.ChoiceLabels!.SequenceEqual(new[] { "Hip view", "Hip gun", "ADS view", "ADS gun" })
            && kick.DisplayColumns.Skip(1).Select(c => c.Column).SequenceEqual(Enumerable.Range(2, 6)));

        // Mistakes: each is said at the level it costs, and never hides a stored value.
        var json = """
            { "apexSchema": 1, "id": "pass", "version": "1", "targets": ["weapon"], "title": 5, "offNotice": "Off.",
              "export": { "format": "csv", "header": "x", "command": "Copy" },
              "sections": [ { "title": "A", "collapsedUnlessSet": "yes", "notice": "",
                "fields": [
                  { "key": "psNum", "kind": "number", "parts": [ { "name": "a", "kind": "number" } ] },
                  { "key": "psDup", "kind": "text", "parts": [ { "name": "a", "kind": "number" }, { "name": "a", "kind": "text" } ] },
                  { "key": "psKind", "kind": "text", "parts": [ { "name": "a", "kind": "slider" } ] },
                  { "key": "psOk", "kind": "text", "parts": [ { "name": "a", "kind": "toggle", "default": 2, "unit": "ms" }, { "name": "b", "kind": "choice", "choices": [ { "value": 1, "label": "One" }, { "label": "No value" }, 1 ] } ] },
                  { "key": "psChoice", "kind": "choice", "choices": [ "x", { "value": "y", "label": "Why", "tip": "t" } ] } ],
                "records": [ { "key": "psRow#", "columns": [ { "name": "a", "kind": "toggle" }, { "name": "b", "kind": "toggle" }, { "name": "c", "kind": "number" } ],
                  "combine": [
                    { "label": "AB", "columns": ["a", "b"], "choices": [ { "value": ["0", "0"], "label": "Off" }, { "value": ["1"], "label": "Short" } ] },
                    { "label": "AC", "columns": ["a", "nope"], "choices": [ { "value": ["0", "0"], "label": "Off" } ] },
                    { "columns": ["a", "b"], "choices": [ { "value": ["0", "0"], "label": "Off" } ] },
                    { "label": "Fine", "columns": ["a", "b"], "choices": [ { "value": ["0", "1"], "label": "B" }, { "value": [1, 1], "label": "Both" } ] },
                    { "label": "Again", "columns": ["b", "c"], "choices": [ { "value": ["0", "0"], "label": "Off" } ] } ] } ] } ] }
            """;
        var dir = NewScratch("pass-loader");
        Directory.CreateDirectory(Path.Combine(dir, "pass"));
        File.WriteAllText(Path.Combine(dir, "pass", ExtensionLoader.FileName), json);
        var (manifests, diagnostics) = ExtensionLoader.LoadAll(dir);
        string Said() => string.Join(" | ", diagnostics.Select(d => $"{d.Problem}: {d.Message}"));
        var m = manifests.Single();
        var f = m.Sections[0].Fields.ToDictionary(x => x.Def.Key);
        var row = m.Sections[0].Records[0];
        string[] expected =
        {
            "Note: its title isn't a line of text",
            "Note: its offNotice needs an enabledBy key",
            "Skipped: its export's format (csv) isn't ini-section",
            "Note: section A: collapsedUnlessSet isn't true or false",
            "Note: section A's notice isn't a line of text",
            "Note: psNum: parts apply to text fields only",
            "Skipped: psDup part 2 has no name, one that isn't a plain name, or one another part has, so psDup is edited as one value.",
            "Skipped: psKind.a's kind isn't one of number, choice, toggle, text, so psKind is edited as one value.",
            "Note: psOk.a: Apex doesn't know the member 'unit'",
            "Note: psOk.a's default (2) isn't 0 or 1, so it has none.",
            "Note: psOk.b: a choice ({ \"label\": \"No value\" }) isn't a string, a number or a { value, label }",
            "Note: psOk.b lists the choice 1 twice; the first is used.",
            "Note: psChoice: Apex doesn't know the choice member 'tip'",
            "needs a value with one entry per column (no commas) and a label, so its columns show as they are.",
            "Skipped: a combine of psRow# names \"nope\"",
            "Skipped: psRow#'s combine of a and b has no label",
            "Skipped: a combine of psRow# names \"b\", which isn't one of its columns or is in another group",
        };
        var said = Said();
        var unsaid = expected.Where(e => !said.Contains(e)).ToList();
        Check($"manifest: each mistake in the new members is said at the level it costs ({string.Join(" || ", unsaid)}; all: {said})", unsaid.Count == 0);
        Check("manifest: a mistake leaves that member out and the rest as written: no parts, a plain field; no group, its columns",
            m is { Title: "", OffNotice: null, Export: null } && !m.Sections[0].CollapsedUnlessSet && m.Sections[0].Notice is null
            && f["psNum"].Parts is null && f["psDup"].Parts is null && f["psKind"].Parts is null
            && f["psOk"].Parts is [{ Kind: PropertyKind.Toggle, Default: "" }, { Kind: PropertyKind.Choice } b] && b.Choices.SequenceEqual(new[] { "1" })
            && b.ChoiceLabels!.SequenceEqual(new[] { "One" }) && f["psChoice"].Def.ChoiceLabels!.SequenceEqual(new[] { "x", "Why" })
            && row.Combines.Count == 1 && row.Combines[0].Def.Label == "Fine" && row.Combines[0].Def.Choices.SequenceEqual(new[] { "0,1", "1,1" })
            && row.DisplayColumns.Select(c => c.Def.Label).SequenceEqual(new[] { "Fine", "c" }));

        // Older manifests: none of the new members, and every editor exactly as before them.
        var plainKick = ExtensionLoader.LoadAll(PlainTablesDir("pass-plain")).Manifests.Single().Sections.SelectMany(s => s.Records).First(r => r.Def.Key == "wtKick#");
        Check("manifest: without combine, a table shows a column per stored column with its own definition",
            plainKick.DisplayColumns.Count == 8 && plainKick.DisplayColumns.Select((c, i) => c.Column == i && ReferenceEquals(c.Def, plainKick.Columns[i].Def)).All(x => x));
        Check("manifest: the new members are additions within apexSchema 1 (an older Apex notes each as a member it doesn't know and loads the rest)",
            ExtensionLoader.SchemaVersion == 1);
    }

    // ═══ The editor: temp GDTs, the planner, temp files only ════════════════

    private const string PassParentGdt =
        "{\r\n\t\"base_gun\" ( \"weapon.gdf\" )\r\n\t{\r\n\t\t\"displayName\" \"Base\"\r\n\t}\r\n}\r\n";

    private const string PassChildGdt =
        "{\r\n\t\"base_gun_up\" [ \"base_gun\" ]\r\n\t{\r\n\t\t\"damage\" \"80\"\r\n\t}\r\n"
        + "\t\"solo_gun\" ( \"weapon.gdf\" )\r\n\t{\r\n\t\t\"displayName\" \"Solo\"\r\n\t}\r\n"
        + "\t\"off_gun\" ( \"weapon.gdf\" )\r\n\t{\r\n\t\t\"displayName\" \"Off\"\r\n\t}\r\n}\r\n";

    private static readonly (string Key, string Value)[] PassParentValues =
    {
        ("wtEnabled", "1"), ("wtIk", "1"), ("wtKick1", "1,0,3,93,1.5,0.35,1.3,1"), ("wtSpringViewHip", "30,1,1,4,2"),
    };

    // solo_gun: a spring in odd spacing with a part empty and two missing; one with a value past its parts and a trailing
    // comma; a choice value the list lacks; a kick row spaced oddly and one whose ads and gun no choice names.
    private static readonly (string Key, string Value)[] PassSoloValues =
    {
        ("wtEnabled", "1"), ("wtEmptyLastShot", "fast"), ("wtFireTimeMs", "63"), ("wtKick1", " 1 , 0 ,3,93,1.5,0.35,1.3,1"),
        ("wtKick2", "2,0,4,90,1,0.3,1.2,1"), ("wtSpringGunHip", "1,2,3,4,5,6,"), ("wtSpringViewHip", " 30, 1.5 ,,4"),
    };

    private static void EditorPassEditorChecks()
    {
        SchemaRegistry.ResetToMock();
        var dir = NewScratch("pass-editor");
        string Text(string p) => GdtEncoding.File.GetString(File.ReadAllBytes(p));
        var parentPath = Path.Combine(dir, "parent.gdt");
        var childPath = Path.Combine(dir, "child.gdt");
        var parentx = "{\r\n" + Block("base_gun", PassParentValues) + "}\r\n";
        var childx = "{\r\n" + Block("solo_gun", PassSoloValues) + "}\r\n";
        File.WriteAllBytes(parentPath, GdtEncoding.GetBytes(PassParentGdt));
        File.WriteAllBytes(ExtensionSidecar.PathFor(parentPath), GdtEncoding.GetBytes(parentx));
        File.WriteAllBytes(childPath, GdtEncoding.GetBytes(PassChildGdt));
        File.WriteAllBytes(ExtensionSidecar.PathFor(childPath), GdtEncoding.GetBytes(childx));
        var parent = LoadGdt(parentPath);
        var child = LoadGdt(childPath);
        var gdts = new List<GdtFile> { parent, child };
        var all = gdts.SelectMany(g => g.Assets).ToList();
        GdtLoader.ResolveParents(all);
        AssetRecord Asset(string name) => all.First(a => a.Name == name);
        GdtFile? GdtOf(AssetRecord r) => gdts.FirstOrDefault(g => g.Name == r.GdtName);
        AssetRecord? Resolve(string type, string name) => all.FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        AssetEditorViewModel Open(string name) => new(Asset(name), (_, _) => { }, _ => { }, _ => { }, Resolve, null, GdtOf);
        PropertyItemViewModel Row(AssetEditorViewModel t, string key) => t.AllSentinel.All.First(p => p.Key == key);
        string Get(string asset, string key) => child.Extensions?.Get(asset, "weapon-tech", key) ?? "(unset)";
        void Save(string label)
        {
            var problems = new List<string>();
            var requests = GdtSavePlanner.Plan(gdts, all, Array.Empty<AssetRecord>(), Array.Empty<GdtFile>(), null, problems);
            var result = NewSaveService(label).Save(requests);
            foreach (var file in result.Files)
                GdtSavePlanner.Commit(file);
            if (problems.Count > 0 || !result.AllSucceeded)
                Check($"editor pass: save ({string.Join("; ", problems)}{string.Join("; ", result.Files.Select(x => x.Message))})", false);
        }

        ExtensionRegistry.Load(ExtensionsDir("pass-editor-fixture"));
        var tab = Open("solo_gun");

        // ── Parts ──
        var spring = (PartsPropertyViewModel)Row(tab, "wtSpringViewHip");
        Check($"parts editor: a value in parts is one row with an editor per part, each showing its field ({string.Join("|", spring.Parts.Select(p => p.RawValue))})",
            spring.Parts.Select(p => p.RawValue).SequenceEqual(new[] { "30", "1.5", "", "4", "" }) && spring.Parts[0] is NumberPropertyViewModel
            && spring.RawValue == " 30, 1.5 ,,4" && !spring.IsChanged && tab.FlatRows.Contains(spring) && spring.Parts[1].AccessibleName == "Spring, view, hip: Return speed");
        Check($"parts editor: its problems show on the parts and the row ('{spring.Problem}')",
            spring.Problem == "Return curve is empty. (1 more)" && spring.Parts[2].HasProblem && spring.Parts[4].HasProblem && !spring.Parts[0].HasProblem
            && tab.Problems.Any(p => p.Key == "wtSpringViewHip"));
        var gun = (PartsPropertyViewModel)Row(tab, "wtSpringGunHip");
        Check($"parts editor: values past the parts are kept and said ('{gun.Problem}')",
            gun.Problem?.StartsWith("There is a value after Max drift") == true && gun.Parts[4].RawValue == "5");

        // Saving something else leaves every value in parts byte for byte.
        Row(tab, "wtFireTimeMs").RawValue = "64";
        Save("pass-other");
        Check("parts editor: values nobody edited are saved byte for byte (spacing, empty and missing parts, extras, a trailing comma)",
            Text(ExtensionSidecar.PathFor(childPath)) == childx.Replace("\"63\"", "\"64\"") && Text(childPath) == PassChildGdt);
        tab.RebaseChanges();

        spring.Parts[1].RawValue = "2";
        Check($"parts editor: a part's edit writes that part into the key, the rest as it was ({Get("solo_gun", "wtSpringViewHip")})",
            Get("solo_gun", "wtSpringViewHip") == " 30,2,,4" && spring.IsChanged && spring.Parts[1].IsChanged && !spring.Parts[0].IsChanged
            && tab.Changes.Single(c => c.Item == spring) is { Old: "Return speed: 1.5", Now: "2" });
        spring.Parts[4].RawValue = "3";
        Check($"parts editor: a part past the end fills the gap ({Get("solo_gun", "wtSpringViewHip")})", Get("solo_gun", "wtSpringViewHip") == " 30,2,,4,3");
        tab.UndoCommand.Execute(null);
        Check($"parts editor: Ctrl+Z puts the key back and says which part ('{tab.LastHistoryText}')",
            Get("solo_gun", "wtSpringViewHip") == " 30,2,,4" && spring.Parts[4].RawValue == "" && tab.LastHistoryText == "Undid Spring, view, hip: Max drift");
        tab.RedoCommand.Execute(null);
        Check($"parts editor: and redo, said the same way ('{tab.LastHistoryText}')",
            Get("solo_gun", "wtSpringViewHip") == " 30,2,,4,3" && tab.LastHistoryText == "Redid Spring, view, hip: Max drift");
        spring.Parts[1].RevertChangeCommand.Execute(null);
        Check($"parts editor: ↶ beside a part takes back that part alone ({Get("solo_gun", "wtSpringViewHip")})",
            Get("solo_gun", "wtSpringViewHip") == " 30,1.5,,4,3" && spring.Parts[4].RawValue == "3");
        spring.RevertChangeCommand.Execute(null);
        Check($"parts editor: ↶ on the row takes back the whole value, bytes and all ({Get("solo_gun", "wtSpringViewHip")})",
            Get("solo_gun", "wtSpringViewHip") == " 30, 1.5 ,,4" && !spring.IsChanged);
        // A comma list typed or pasted into a part fills the parts.
        const string Baseline = " 30, 1.5 ,,4";
        var number = (NumberPropertyViewModel)spring.Parts[3];
        Check("parts paste: a whole value pasted into a number part is taken as the list, not one number with its commas dropped",
            number.TryCommitText("2200,0.06,1,10,10") && Get("solo_gun", "wtSpringViewHip") == "2200,0.06,1,10,10"
            && spring.Parts.Select(p => p.RawValue).SequenceEqual(new[] { "2200", "0.06", "1", "10", "10" }));
        tab.UndoCommand.Execute(null);
        Check($"parts paste: one Ctrl+Z puts the whole value back ({Get("solo_gun", "wtSpringViewHip")}, '{tab.LastHistoryText}')",
            Get("solo_gun", "wtSpringViewHip") == Baseline && tab.LastHistoryText!.StartsWith("Undid Spring, view, hip"));
        spring.Parts[0].RawValue = "7,8,9,10,11";
        Check($"parts paste: the same through a text part's own text ({Get("solo_gun", "wtSpringViewHip")})",
            Get("solo_gun", "wtSpringViewHip") == "7,8,9,10,11" && spring.Parts[0].RawValue == "7" && spring.Parts[0].Problem is null);
        spring.RevertChangeCommand.Execute(null);
        Check("parts paste: a shorter run fills on from the part it was pasted into, the rest untouched",
            ((NumberPropertyViewModel)spring.Parts[1]).TryCommitText("7,8") && Get("solo_gun", "wtSpringViewHip") == " 30,7,8,4");
        spring.RevertChangeCommand.Execute(null);
        var tooMany = ((NumberPropertyViewModel)spring.Parts[2]).TryCommitText("1,2,3,4,5,6");
        Check($"parts paste: more values than parts changes nothing and says so ('{spring.Parts[2].Problem}')",
            tooMany && Get("solo_gun", "wtSpringViewHip") == Baseline
            && spring.Parts[2].Problem == "That's 6 values; Spring, view, hip has 5 parts.");
        var tooLong = number.TryCommitText("1,2,3");
        Check($"parts paste: a run that runs past the last part changes nothing and says so ('{spring.Parts[3].Problem}')",
            tooLong && Get("solo_gun", "wtSpringViewHip") == Baseline
            && spring.Parts[3].Problem == "That's 3 values; only 2 parts from Max climb on.");
        Check("parts paste: a number with thousands separators is still one number in a part",
            ((NumberPropertyViewModel)spring.Parts[0]).TryCommitText("1,234") && Get("solo_gun", "wtSpringViewHip") == "1234, 1.5 ,,4");
        spring.RevertChangeCommand.Execute(null);
        var fireTime = (NumberPropertyViewModel)Row(tab, "wtFireTimeMs");
        var fireBefore = fireTime.RawValue;
        Check("parts paste: a number field that is not a part keeps reading a comma as a thousands separator",
            fireTime.TryCommitText("6,4") && fireTime.RawValue == "64");
        fireTime.RawValue = fireBefore;

        // Inherited: the parts are the parent's, dim; a part edited makes the whole value the asset's own.
        var up = Open("base_gun_up");
        var upSpring = (PartsPropertyViewModel)Row(up, "wtSpringViewHip");
        Check("parts editor: a derived weapon's parts are its parent's, inherited",
            upSpring.IsInherited && upSpring.Parts.Select(p => p.RawValue).SequenceEqual(new[] { "30", "1", "1", "4", "2" }) && upSpring.Parts.All(p => p.IsInherited));
        upSpring.Parts[3].RawValue = "6";
        Check($"parts editor: overriding a part writes the derived weapon's own value, the part marked as its own ({Get("base_gun_up", "wtSpringViewHip")})",
            Get("base_gun_up", "wtSpringViewHip") == "30,1,1,6,2" && upSpring.IsOverride && upSpring.Parts[3].IsOverride && !upSpring.Parts[0].IsOverride);
        upSpring.Parts[3].RevertToParentCommand.Execute(null);
        Check("parts editor: ↑ beside the part takes the parent's part, and with nothing else its own the weapon inherits again",
            Get("base_gun_up", "wtSpringViewHip") == "(unset)" && upSpring.IsInherited);

        // ── Labelled choices ──
        var last = (ChoicePropertyViewModel)Row(tab, "wtEmptyLastShot");
        Check($"labelled choices: the stored value the list lacks is kept and shown as custom ({string.Join(", ", last.Items.Select(i => i.Label))})",
            last.RawValue == "fast" && last.Items.Select(i => i.Label).SequenceEqual(new[] { "Automatic", "As IW does", "hold", "custom: fast" })
            && last.DisplayValue == "custom: fast" && last.HasProblem);
        last.RawValue = "iw";
        Check($"labelled choices: picking one stores its value and shows its label ({Get("solo_gun", "wtEmptyLastShot")}, '{last.DisplayValue}')",
            Get("solo_gun", "wtEmptyLastShot") == "iw" && last.DisplayValue == "As IW does" && !last.HasProblem);
        tab.UndoCommand.Execute(null);
        Check("labelled choices: undo brings the custom value back as it was", Get("solo_gun", "wtEmptyLastShot") == "fast" && last.RawValue == "fast");

        // ── Combined columns ──
        var kicks = (RecordsPropertyViewModel)Row(tab, "wtKick#");
        string Applies(int row) => kicks.Rows[row].Cells[0].RawValue;
        Check($"combine: a row's ads and gun show as the choice they match, spacing and all; one no choice names is custom ({Applies(0)}, {Applies(1)})",
            Applies(0) == "1,0" && Applies(1) == "2,0" && kicks.Rows[0].Cells[0] is ChoicePropertyViewModel { DisplayValue: "ADS view" }
            && ((ChoicePropertyViewModel)kicks.Rows[1].Cells[0]).DisplayValue == "custom: 2,0" && kicks.Rows[0].Cells.Count == 7
            && kicks.Rows[0].Cells[1].AccessibleName == "From shot row 1");
        kicks.Rows[0].Cells[0].RawValue = "0,1";
        Check($"combine: a choice writes each of its columns, the rest of the row as it was ({Get("solo_gun", "wtKick1")})",
            Get("solo_gun", "wtKick1") == "0,1,3,93,1.5,0.35,1.3,1" && kicks.Rows[0].Cells[1].RawValue == "3");
        tab.UndoCommand.Execute(null);
        Check($"combine: undo restores the row byte for byte and says the row ('{tab.LastHistoryText}')",
            Get("solo_gun", "wtKick1") == " 1 , 0 ,3,93,1.5,0.35,1.3,1" && Applies(0) == "1,0" && tab.LastHistoryText == "Undid Kick patterns row 1");
        Check($"combine: the custom row is kept through it all ({Get("solo_gun", "wtKick2")})", Get("solo_gun", "wtKick2") == "2,0,4,90,1,0.3,1.2,1");

        // ── Paste ──
        var parsed = RecordCodec.ParseRows(kicks.List,
            "0,0,1,90,0.5,0.1,0.2,1\r\n\r\n1\t1\t2\t91\t0.5\t0.1\t0.2\t1\n\"wtKick7\" \"0,1,3,92,0,0,0,1\"\nwtKick2 = 1,1,4,93,0,0,0,1\nwtTilt = 1,2\n");
        Check($"paste: lines are records as stored, tab-separated cells, or a .gdtx or cfg line of the list's key ({string.Join(" | ", parsed)})",
            parsed.SequenceEqual(new[] { "0,0,1,90,0.5,0.1,0.2,1", "1,1,2,91,0.5,0.1,0.2,1", "0,1,3,92,0,0,0,1", "1,1,4,93,0,0,0,1", "wtTilt = 1,2" }));
        var before = kicks.RawValue;
        var pasted = kicks.Paste(1, parsed.Take(3).ToList());
        Check($"paste: rows replace from the row pasted at and add past the last, as one edit ({pasted}; {kicks.Rows.Count} rows)",
            pasted == (1, 2, 0) && kicks.Rows.Count == 4 && Get("solo_gun", "wtKick1") == " 1 , 0 ,3,93,1.5,0.35,1.3,1"
            && Get("solo_gun", "wtKick2") == "0,0,1,90,0.5,0.1,0.2,1" && Get("solo_gun", "wtKick4") == "0,1,3,92,0,0,0,1");
        tab.UndoCommand.Execute(null);
        Check($"paste: one Ctrl+Z takes the whole paste back ('{tab.LastHistoryText}')",
            kicks.RawValue == before && Get("solo_gun", "wtKick3") == "(unset)" && tab.LastHistoryText == "Undid Kick patterns from 2 rows to 4 rows");
        var pasteClock = System.Diagnostics.Stopwatch.StartNew();
        var full = kicks.Paste(2, Enumerable.Range(0, 30).Select(i => $"0,0,{i},90,0,0,0,1").ToList());
        Console.WriteLine($"info  editor pass: pasting 22 rows into a table no view shows takes {pasteClock.Elapsed.TotalMilliseconds:0.0} ms (rows, keys, checks, undo step)");
        Check($"paste: rows past the table's limit are left out and counted ({full})", full == (0, 22, 8) && kicks.Rows.Count == 24);
        tab.UndoCommand.Execute(null);
        var bad = kicks.Paste(2, new[] { "1,0,x,90,0,0,0,1" });
        Check($"paste: a pasted row is checked like any row ('{kicks.Rows[2].Problem}')",
            bad == (0, 1, 0) && kicks.Rows[2].HasProblem && kicks.Rows[2].Problem!.Contains("From shot"));
        tab.UndoCommand.Execute(null);
        Check($"copy: rows go out as stored, one per line ('{RecordsPropertyViewModel.Copy(kicks.Rows)}')",
            RecordsPropertyViewModel.Copy(kicks.Rows) == " 1 , 0 ,3,93,1.5,0.35,1.3,1" + Environment.NewLine + "2,0,4,90,1,0.3,1.2,1");
        kicks.Move(kicks.Rows[1], -1);
        Check($"undo text: a move says the rows ('{UndoText(tab)}')", UndoText(tab) == "Undid Kick patterns rows 1 and 2");
        kicks.Insert(2);
        Check($"undo text: an added row says which ('{UndoText(tab)}')", UndoText(tab) == "Undid adding row 3 to Kick patterns");
        kicks.Remove(kicks.Rows[0]);
        Check($"undo text: a removed row says which ('{UndoText(tab)}')", UndoText(tab) == "Undid removing row 1 from Kick patterns");

        // ── Folding sections ──
        var ammo = tab.RailItems.First(c => c.Name == "Ammo hide");
        var ik = tab.RailItems.First(c => c.Name == "Hand IK");
        Check($"folding: a section that holds nothing starts folded to its header ({ammo.SetText}, {ammo.IsCollapsed})",
            ammo is { IsCollapsible: true, IsCollapsed: true, SetCount: 0, SetText: "none set" } && tab.FlatRows.Contains(ammo)
            && !tab.FlatRows.Contains(Row(tab, "wtAmmoHide")) && ammo.VisibleCount == 5);
        Check($"folding: a section holding a value starts open; inherited values count ({Open("base_gun_up").RailItems.First(c => c.Name == "Hand IK").SetText})",
            Open("base_gun_up").RailItems.First(c => c.Name == "Hand IK") is { IsCollapsed: false, SetCount: 1 } && ik.IsCollapsed);
        Row(tab, "wtAmmoHideAuto").RawValue = "off";
        Check($"folding: a value appearing opens it, and the header counts it ({ammo.SetText})",
            !ammo.IsCollapsed && ammo.SetText == "1 set" && tab.FlatRows.Contains(Row(tab, "wtAmmoHide")));
        tab.UndoCommand.Execute(null);
        Check("folding: the value gone, it stays open (nothing folds away under the hand)", !ammo.IsCollapsed && ammo.SetCount == 0);
        ammo.IsExpanded = false;
        Check("folding: the header's toggle folds it", ammo.IsCollapsed && !tab.FlatRows.Contains(Row(tab, "wtAmmoHide")) && tab.FlatRows.Contains(ammo));
        tab.SearchText = "Spend";
        Pump(200);
        Check("folding: a filter shows what it finds in a folded section", tab.FlatRows.Contains(Row(tab, "wtAmmoHideSpend")) && ammo.IsCollapsed);
        tab.SearchText = "";
        tab.View = EditorView.Changed;
        tab.View = EditorView.All;
        tab.RevealProperty("wtIk");
        Check("folding: going to a row in a folded section opens it", !ik.IsCollapsed && tab.FlatRows.Contains(Row(tab, "wtIk")));
        tab.SelectedRail = ammo;
        Check("folding: picking a folded section in the rail opens it", !ammo.IsCollapsed);

        // ── The filter finds sections ──
        tab.SearchText = "Ammo hide";
        Pump(200);
        Check($"filter: a section's title finds all of its rows ({tab.VisiblePropertyCount} rows)",
            tab.VisiblePropertyCount == 5 && tab.FlatRows.Contains(Row(tab, "wtAmmoHideAuto")) && !tab.FlatRows.Contains(Row(tab, "wtFireTimeMs")));
        var shownRows = tab.AllSentinel.All.Count(p => p.Def.Extension.Length > 0 && !p.IsRuleHidden);
        foreach (var query in new[] { "weapon tech", "weapon-tech", "WEAPON TECH" })
        {
            tab.SearchText = query;
            Pump(200);
            var wtRows = tab.FlatRows.OfType<PropertyItemViewModel>().ToList();
            Check($"filter: '{query}' (the extension's title or id) finds every row of it that shows ({wtRows.Count} of {shownRows})",
                wtRows.Count == shownRows && wtRows.All(p => p.Def.Extension == "weapon-tech"));
        }
        tab.SearchText = "Max climb";
        Pump(200);
        Check("filter: a part's label finds its value in parts, a combined column's its table",
            tab.FlatRows.Contains(spring) && Filtered(tab, "Applies to").Contains(kicks));
        tab.SearchText = "";

        // ── The off line, notices, export ──
        var off = Open("off_gun");
        Check($"off line: a weapon the extension is off for starts with the manifest's line ({(off.FlatRows.FirstOrDefault() as ExtensionOffRowViewModel)?.Text})",
            off.FlatRows[0] is ExtensionOffRowViewModel { Text: "Weapon tech is off for this weapon." } && !off.FlatRows.OfType<ExtensionNoticeRowViewModel>().Any()
            && off.RailItems[0].Name == "Weapon tech" && !off.RailItems[0].CanExport && !off.Exports.Any());
        ((ExtensionOffRowViewModel)off.FlatRows[0]).TurnOnCommand.Execute(null);
        Check("off line: Turn on turns it on (the switch as a click would) and the line goes",
            Get("off_gun", "wtEnabled") == "1" && off.FlatRows[0] is not ExtensionOffRowViewModel && off.FocusedProperty?.Key == "wtEnabled");
        var notices = off.FlatRows.OfType<ExtensionNoticeRowViewModel>().Select(n => n.Text).ToList();
        Check($"notices: on, the manifest's notice sits under its first header and a section's under its own ({string.Join(" | ", notices)})",
            notices.Count == 2 && notices[0].StartsWith("The game doesn't read these yet.") && notices[1] == "Experimental."
            && off.FlatRows.IndexOf(off.FlatRows.OfType<ExtensionNoticeRowViewModel>().First()) == off.FlatRows.IndexOf(off.RailItems[0]) + 1
            && off.RailItems[0] is { CanExport: true, ExportLabel: "Copy as weapon_tech.cfg" });

        // The notice follows the consumer: it goes while the install carries the marker of the tool that reads the values.
        var consumerRoot = NewScratch("pass-consumer-editor");
        Directory.CreateDirectory(Path.Combine(consumerRoot, "bin"));
        var rootBefore = FieldFiles.Root;
        try
        {
            FieldFiles.Root = consumerRoot;
            ExtensionConsumerProbe.Reset();
            string[] NoticesOf(string name) => Open(name).FlatRows.OfType<ExtensionNoticeRowViewModel>().Select(n => n.Text).ToArray();
            var missing = NoticesOf("solo_gun");
            Check($"consumer: with the tool's file missing the editor shows the manifest's notice ({string.Join(" | ", missing)})",
                missing.Length == 2 && missing[0].StartsWith("The game doesn't read these yet.") && missing[1] == "Experimental.");
            var marked = new byte[2048];
            System.Text.Encoding.ASCII.GetBytes("generated:apex-gdtx").CopyTo(marked, 500);
            var hookFile = Path.Combine(consumerRoot, "bin", "linkerhook.dll");
            File.WriteAllBytes(hookFile, marked);
            var present = NoticesOf("solo_gun");
            Check($"consumer: with the tool installed the notice is gone and a section's own stays ({string.Join(" | ", present)})",
                present.SequenceEqual(new[] { "Experimental." }));
            File.Delete(hookFile);
            Check("consumer: and back when the tool goes", NoticesOf("solo_gun").Length == 2);
        }
        finally
        {
            FieldFiles.Root = rootBefore;
            ExtensionConsumerProbe.Reset();
        }
        off.UndoCommand.Execute(null);
        Check("off line: Ctrl+Z turns it off again and the line is back", off.FlatRows[0] is ExtensionOffRowViewModel && Get("off_gun", "wtEnabled") == "(unset)");

        var upTab = Open("base_gun_up");
        Row(upTab, "wtFireTimeMs").RawValue = "70";
        const string golden = "[weapon:base_gun_up]\r\nwtFireTimeMs = 70\r\nwtSpringViewHip = 30,1,1,4,2\r\nwtKick1 = 1,0,3,93,1.5,0.35,1.3,1\r\nwtIk = 1\r\n";
        Check($"export: the header, then each key with a value, own or inherited, in the manifest's order, a table as its numbered keys, the switch left out ('{upTab.ExportText("weapon-tech")?.Replace("\r\n", "⏎")}')",
            upTab.ExportText("weapon-tech") == golden);
        var soloText = tab.ExportText("weapon-tech")!;
        Check("export: values are raw, as stored (spacing, empty and extra parts)",
            soloText.Contains("wtSpringViewHip =  30, 1.5 ,,4\r\n") && soloText.Contains("wtSpringGunHip = 1,2,3,4,5,6,\r\n") && soloText.Contains("wtEmptyLastShot = fast\r\n"));
        ExtensionRegistry.Clear();
    }

    private static string UndoText(AssetEditorViewModel tab)
    {
        tab.UndoCommand.Execute(null);
        var text = tab.LastHistoryText;
        tab.RedoCommand.Execute(null);
        return text;
    }

    private static List<object> Filtered(AssetEditorViewModel tab, string query)
    {
        tab.SearchText = query;
        Pump(200);
        return tab.FlatRows.ToList();
    }

    // ═══ The app (mock data): journal, save status ══════════════════════════

    private static void EditorPassSessionChecks()
    {
        var root = NewScratch("pass-session");
        Environment.SetEnvironmentVariable(ExtensionRegistry.DirVariable, ExtensionsDir("pass-session-fixture"));
        var vm = new MainViewModel(root);
        var window = ShowJournalWindow(vm);
        try
        {
            vm.OpenByName("wpn_ar_havoc_zm");
            var tab = vm.ActiveTab!;
            tab.AllSentinel.All.First(p => p.Key == "wtEnabled").RawValue = "1";
            var spring = (PartsPropertyViewModel)tab.AllSentinel.All.First(p => p.Key == "wtSpringViewHip");
            spring.Parts[0].RawValue = "25";
            spring.Parts[3].RawValue = "4";
            var kicks = (RecordsPropertyViewModel)tab.AllSentinel.All.First(p => p.Key == "wtKick#");
            kicks.Paste(0, new[] { "1,1,0,90,0.5,0.1,0.2,1", "0,0,2,88,0.5,0.1,0.2,1" });
            vm.FlushSessionToDisk(closing: true);
            window.Close();
            vm.Dispose();
            ExtensionRegistry.Clear();
            vm = new MainViewModel(root);
            window = ShowJournalWindow(vm);
            Pump(200);
            vm.OpenByName("wpn_ar_havoc_zm");
            var reopened = vm.ActiveTab!;
            var springAgain = (PartsPropertyViewModel)reopened.AllSentinel.All.First(p => p.Key == "wtSpringViewHip");
            var kicksAgain = (RecordsPropertyViewModel)reopened.AllSentinel.All.First(p => p.Key == "wtKick#");
            Check($"journal: parts and pasted rows are kept across a restart ({springAgain.RawValue}; {kicksAgain.Rows.Count} rows)",
                springAgain.RawValue == "25,,,4" && kicksAgain.Rows.Count == 2 && kicksAgain.Rows[0].Cells[0].RawValue == "1,1"
                && springAgain.Parts[0].RawValue == "25" && springAgain.Parts[0].IsChanged);
            vm.DiscardSessionNow();
        }
        catch (Exception ex)
        {
            Check($"editor pass session: {ex}", false);
        }
        finally
        {
            window.Close();
            vm.Dispose();
            Environment.SetEnvironmentVariable(ExtensionRegistry.DirVariable, NewScratch("no-extensions"));
            ExtensionRegistry.Clear();
        }
    }
}
