using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Apex.Editor.Controls;
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
/// Record lists (wtKick#, wtAdditiveSlot#): the record codec, the manifest's records, the table in the editor (rows to
/// numbered keys, renumbering, order, undo, saving), the journal, extension blocks following rename, delete, duplicate,
/// copy and move, search, the table, compare and the Explorer's problem count, and the live app with real input.
/// Every file written is a temp copy; nothing touches the BO3 install.
/// </summary>
public partial class Program
{
    private static void RunRecordChecks(string outDir)
    {
        var saved = Environment.GetEnvironmentVariable(ExtensionRegistry.DirVariable);
        try
        {
            RecordCodecChecks();
            RecordLoaderChecks();
            RecordEditorChecks();
            RecordSessionChecks();
            RecordGapChecks(outDir);
            RecordUiChecks(outDir);
            RecordTableWidthChecks(outDir);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ExtensionRegistry.DirVariable, saved);
            ExtensionRegistry.Clear();
            SchemaRegistry.ResetToMock();
        }
    }

    /// <summary>A kick row as weapon-tech's docs write wop_kick's fields: ads,gun,bullet,dir,dev,sMin,sMax,pitchScale.</summary>
    private static string KickRow(int i) => $"{i % 2},0,{i},{90 + i},{i * 0.5:0.0},0.{i % 10}5,1.{i % 10},1";

    private static string Block(string asset, IEnumerable<(string Key, string Value)> values) =>
        $"\t\"{asset}\" ( \"weapon-tech\" )\r\n\t{{\r\n" + string.Concat(values.Select(v => $"\t\t\"{v.Key}\" \"{v.Value}\"\r\n")) + "\t}\r\n";

    private static ExtensionRecordList FixtureList(string key)
    {
        var (manifests, _) = ExtensionLoader.LoadAll(FixtureExtensions);
        return manifests.Single().Sections.SelectMany(s => s.Records).First(r => r.Def.Key == key);
    }

    // ═══ The codec ══════════════════════════════════════════════════════════

    private static void RecordCodecChecks()
    {
        var kick = FixtureList("wtKick#");
        var slot = FixtureList("wtAdditiveSlot#");
        bool None(string _, string __) => true;

        // Round trip: a record parsed and formatted again is what it was.
        var plain = new[]
        {
            "1,0,3,93,1.5,0.35,1.3,1", "0,1,0,-90,0,0,0,0", "1,0,3,93,1.50,0.350,1.3,1e0", " 1, 0 ,3,93,1.5,0.35,1.3,1",
        };
        var broken = plain.Where(r => RecordCodec.Format(kick, RecordCodec.Parse(kick, r).Cells, RecordCodec.Parse(kick, r).Extras) != r).ToList();
        Check($"records codec: a record parses into its columns and formats back as written ({string.Join(" | ", broken)})", broken.Count == 0);
        var trailing = RecordCodec.Parse(kick, "1,0,3,93,1.5,0.35,1.3,1,");
        Check("records codec: a trailing comma is an empty extra field, kept, and no problem",
            trailing.Extras.SequenceEqual(new[] { "" }) && RecordCodec.Format(kick, trailing.Cells, trailing.Extras) == "1,0,3,93,1.5,0.35,1.3,1,"
            && RecordCodec.Problems(kick, trailing, None).Count == 0);
        var extra = RecordCodec.Parse(kick, "1,0,3,93,1.5,0.35,1.3,1,# a note");
        Check($"records codec: a field past the columns is kept and said ('{string.Join(" ", RecordCodec.Problems(kick, extra, None))}')",
            extra.Extras.SequenceEqual(new[] { "# a note" }) && RecordCodec.Problems(kick, extra, None).Single().Contains("after Pitch scale"));
        var odd = RecordCodec.Parse(kick, @"1,2,1.5,a\b,,0.1,0.2");
        var oddProblems = RecordCodec.Problems(kick, odd, None);
        Check($"records codec: odd values are problems, never rewritten ({string.Join(" | ", oddProblems)})",
            odd.Cells[3] == @"a\b" && odd.Cells[7] == "" && oddProblems.Contains("Target: ‘2’ isn't one of 0, 1.")
            && oddProblems.Contains("From shot: 1.5 isn't a whole number.") && oddProblems.Contains(@"Direction: ‘a\b’ is not a number.")
            && oddProblems.Contains("Deviation is empty.") && oddProblems.Contains("Pitch scale is empty."));
        var empty = RecordCodec.Parse(kick, "");
        Check("records codec: an empty record is every column empty, and says so",
            empty.Cells.All(c => c == "") && RecordCodec.Problems(kick, empty, None).Count == 8 && RecordCodec.Format(kick, empty.Cells) == ",,,,,,,");

        // Slot lines: a prefixed column, optional trailing numbers and a named side.
        var line = RecordCodec.Parse(slot, @"bullet,slot:bullets,vm\ak47_bullets,1,30,side:left");
        Check("records codec: a slot line reads its kind, purpose (without slot:), xanim (backslashes literal), weight, mag and side",
            line.Cells.SequenceEqual(new[] { "bullet", "bullets", @"vm\ak47_bullets", "1", "30", "left" })
            && RecordCodec.Format(slot, line.Cells, line.Extras) == @"bullet,slot:bullets,vm\ak47_bullets,1,30,side:left");
        var early = RecordCodec.Parse(slot, "empty,slot:empty,vm_empty,side:left,0.5");
        Check("records codec: side: is found wherever it is after the required fields, and written last",
            early.Cells.SequenceEqual(new[] { "empty", "empty", "vm_empty", "0.5", "", "left" })
            && RecordCodec.Format(slot, early.Cells, early.Extras) == "empty,slot:empty,vm_empty,0.5,side:left");
        var shortLine = RecordCodec.Parse(slot, "recoil,slot:recoil,vm_recoil");
        Check("records codec: optional fields left out stay out",
            RecordCodec.Format(slot, shortLine.Cells) == "recoil,slot:recoil,vm_recoil" && RecordCodec.Problems(slot, shortLine, None).Count == 0);
        string Said(string record) => string.Join(" | ", RecordCodec.Problems(slot, RecordCodec.Parse(slot, record), None));
        var checks = new (string Record, string Problem)[]
        {
            ("bullet,slot:bullets,vm_b,1", "A bullet line needs a mag size above 0 after the weight."),
            ("bullet,slot:bullets,vm_b,1,0", "A bullet line needs a mag size above 0 after the weight."),
            ("recoil,slot:recoil,vm_r,1,1,side:left", "side:left works on empty and bullets only: there is no left recoil layer."),
            ("empty,slot:recoil_ads,vm_r", "slot:recoil_ads takes a recoil line."),
            ("bullet,slot:empty_left,vm_e,1,30", "slot:empty_left takes an empty line."),
            ("bullet,slot:bullets,vm_b,,30", "Weight is empty but Mag or rate is set: fill it in, or clear Mag or rate too."),
            ("bullet,bullets,vm_b,1,30", "Purpose should start with slot:"),
            ("bullet,slot:bullets,vm_b,1,30,side:both", "Side: ‘both’ isn't one of right, left."),
            ("melee,slot:bullets,vm_b,1,30", "Kind: ‘melee’ isn't one of empty, bullet, recoil."),
        };
        var missed = checks.Where(c => !Said(c.Record).Contains(c.Problem)).Select(c => $"{c.Record} → {Said(c.Record)}").ToList();
        Check($"records codec: the documented slot-line rules are row problems ({string.Join(" || ", missed)})", missed.Count == 0);
        Check("records codec: a good slot line has no problem", Said(@"bullet,slot:bullets,vm\b,1,30,side:left") == "");
        var repeats = RecordCodec.Repeats(slot, new[]
        {
            RecordCodec.Parse(slot, "bullet,slot:bullets,a,1,30"), RecordCodec.Parse(slot, "bullet,slot:bullets,b,1,30,side:right"),
            RecordCodec.Parse(slot, "bullet,slot:bullets,c,1,30,side:left"), RecordCodec.Parse(slot, "empty,slot:empty,d"),
        });
        Check($"records codec: one line per purpose and side (an empty side is right) ({string.Join(",", repeats)})",
            repeats.SequenceEqual(new[] { -1, 0, -1, -1 }));

        // Numbered keys and the joined value.
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["wtKick10"] = "j", ["wtKick2"] = "b", ["wtKickPct"] = "x", ["wtKick"] = "y", ["WTKICK1"] = "a", ["wtKick01"] = "a0", ["wtKick3a"] = "z",
        };
        var rows = RecordCodec.Rows(values, kick);
        Check($"records codec: a list's keys are its stem and digits, in number order ({string.Join(",", rows.Select(r => r.Key))})",
            rows.Select(r => r.Key).SequenceEqual(new[] { "WTKICK1", "wtKick01", "wtKick2", "wtKick10" }));
        Check("records codec: rows join one per line, each ended, so an empty row still counts",
            RecordCodec.Join(Array.Empty<string>()) == "" && RecordCodec.Join(new[] { "" }) == "\n" && RecordCodec.Split("\n").SequenceEqual(new[] { "" })
            && RecordCodec.Split("a\nb\n").SequenceEqual(new[] { "a", "b" }) && RecordCodec.Split("").Count == 0
            && RecordCodec.Keys(kick, new[] { "a", "b" }).Keys.SequenceEqual(new[] { "wtKick1", "wtKick2" }));
        var order = new[] { "wtKick10", "wtKickMaintain", "wtKick2", "wtKick1", "wtEnabled", "wtKick01", "wtAdditiveSlot12", "wtAdditiveSlot3" }
            .OrderBy(k => k, ExtensionKeyComparer.Instance).ToList();
        Check($"records codec: a .gdtx sorts numbered keys by number ({string.Join(",", order)})",
            order.SequenceEqual(new[] { "wtAdditiveSlot3", "wtAdditiveSlot12", "wtEnabled", "wtKick1", "wtKick01", "wtKick2", "wtKick10", "wtKickMaintain" }));
    }

    // ═══ The manifest ═══════════════════════════════════════════════════════

    private static void RecordLoaderChecks()
    {
        SchemaRegistry.ResetToMock();
        var kick = FixtureList("wtKick#");
        var slot = FixtureList("wtAdditiveSlot#");
        Check("records manifest: the fixture's kick table: 8 typed columns, up to 24 rows",
            kick is { Base: "wtKick", MaxRows: 24, Columns.Count: 8 }
            && kick.Columns.Select(c => c.Name).SequenceEqual(new[] { "ads", "gun", "bullet", "dir", "dev", "sMin", "sMax", "pitchScale" })
            && kick.Columns[0].Def.Kind == PropertyKind.Toggle && kick.Columns[1].Def.Choices.SequenceEqual(new[] { "0", "1" })
            && kick.Columns[2].Def.IsInteger && kick.Columns.Skip(3).All(c => c.Def is { Kind: PropertyKind.Number, HasRange: false })
            && kick.Columns.All(c => !c.Optional && !c.Named));
        Check("records manifest: the fixture's slot table: a prefixed purpose, an xanim, optional numbers, a named side, the rules",
            slot is { Base: "wtAdditiveSlot", MaxRows: null, Columns.Count: 6, Checks.Count: 5, Unique.Count: 2 }
            && slot.Columns[1].Prefix == "slot:" && slot.Columns[2].Def is { Kind: PropertyKind.AssetRef, RefType: "xanim" }
            && slot.Columns[3] is { Optional: true, Def.Default: "1" } && slot.Columns[5] is { Named: true, Prefix: "side:", Def.Default: "right" });

        var json = """
            { "apexSchema": 1, "id": "rec", "version": "1", "targets": ["weapon"], "sections": [
              { "title": "A", "fields": [ { "key": "rcFlat1", "kind": "text" }, { "key": "rcOk", "kind": "text" } ],
                "records": [
                  { "key": "rcList#", "plot": true, "max": -2, "columns": [ { "name": "a", "kind": "number", "unit": "ms" }, { "name": "b", "kind": "slider" },
                    { "name": "a", "kind": "text" }, { "name": "c", "kind": "choice", "named": true }, { "name": "d", "kind": "text", "optional": true },
                    { "name": "e", "kind": "text" } ],
                    "checks": [ { "when": "a ==", "message": "x" }, { "when": "zz > 1", "message": "reads zz" }, { "message": "no rule" } ],
                    "unique": ["a", "nope"] },
                  { "key": "rcNoHash", "columns": [ { "name": "a", "kind": "text" } ] },
                  { "key": "rcFlat#", "columns": [ { "name": "a", "kind": "text" } ] },
                  { "key": "rcEmpty#", "columns": [] },
                  { "key": "rcList1#", "columns": [ { "name": "a", "kind": "text" } ] }
                ] },
              { "title": "B", "fields": [ { "key": "rcList7", "kind": "text" }, { "key": "rcB", "kind": "text", "visibleWhen": "rcList2 != ''" } ], "records": 5 }
            ] }
            """;
        var dir = NewScratch("rec-loader");
        Directory.CreateDirectory(Path.Combine(dir, "rec"));
        File.WriteAllText(Path.Combine(dir, "rec", ExtensionLoader.FileName), json);
        var (manifests, diagnostics) = ExtensionLoader.LoadAll(dir);
        string Said() => string.Join(" | ", diagnostics.Select(d => $"{d.Problem}: {d.Message}"));
        var m = manifests.SingleOrDefault();
        var lists = m?.Sections.SelectMany(s => s.Records).ToList() ?? new();
        var list = lists.FirstOrDefault();
        Check($"records manifest: a bad list or column leaves out that part, the rest loads ({string.Join(",", lists.Select(l => l.Def.Key))}; {Said()})",
            m is not null && lists.Select(l => l.Def.Key).SequenceEqual(new[] { "rcList#" })
            && m.Sections.SelectMany(s => s.Fields).Select(f => f.Def.Key).SequenceEqual(new[] { "rcFlat1", "rcOk", "rcB" })
            && list!.Columns.Select(c => c.Name).SequenceEqual(new[] { "a", "d", "e" }) && list.MaxRows is null
            && list.Checks.Count == 1 && list.Unique.Select(c => c.Name).SequenceEqual(new[] { "a" }));
        string[] expected =
        {
            "Note: rcList#: Apex doesn't know the member 'plot'",
            "Note: rcList#.a: Apex doesn't know the member 'unit'",
            "Note: rcList#'s max isn't a whole number above 0",
            "Skipped: rcList#.b's kind isn't one of number, toggle, choice, text, anim",
            "Skipped: rcList#.a is declared twice",
            "Skipped: rcList#.c is named but has no prefix",
            "Note: rcList#.e isn't optional but follows an optional column, so it is optional too.",
            "Skipped: a check of rcList# isn't valid",
            "Note: a check of rcList# reads zz, which isn't one of its columns",
            "needs a when rule and a message, so it was left out.",
            "Note: rcList#'s unique names \"nope\", which isn't one of its columns",
            "Skipped: a record list in A has no key, or one that isn't a key name ending in #",
            "Skipped: rcFlat#'s keys are also another field's or record list's",
            "Skipped: rcEmpty# has no usable positional column",
            "Skipped: rcList1#'s keys are also another field's or record list's",
            "Skipped: rcList7 is one of rcList#'s numbered keys",
            "Skipped: section B's records isn't a list",
        };
        var said = Said();
        var unsaid = expected.Where(e => !said.Contains(e)).ToList();
        Check($"records manifest: each problem is said, at the level it costs ({string.Join(" || ", unsaid)})", unsaid.Count == 0);
        Check("records manifest: a rule may read a list's numbered key (rcB reads rcList2) without a note",
            !said.Contains("reads rcList2"));
        // An Apex from before records: the same manifest's records member is one it doesn't know, noted, and its fields load.
        Check("records manifest: apexSchema stays 1 (an older Apex notes records and loads the fields; its loader treats any unknown section member so)",
            ExtensionLoader.SchemaVersion == 1);

        // Merge: a list whose numbered keys would hold a deffile key is the GDT's, and the first extension (by id) keeps
        // a stem. The mock weapon has no numbered key, so a type of its own carries one (tsSlot1).
        var clash = NewScratch("rec-clash");
        CopyTree(FixtureExtensions, clash);
        Directory.CreateDirectory(Path.Combine(clash, "zz-other"));
        File.WriteAllText(Path.Combine(clash, "zz-other", ExtensionLoader.FileName), """
            { "apexSchema": 1, "id": "zz-other", "version": "1", "targets": ["weapon", "testweapon"], "sections": [ { "title": "O", "records": [
              { "key": "wtKick#", "columns": [ { "name": "a", "kind": "text" } ] },
              { "key": "tsSlot#", "columns": [ { "name": "a", "kind": "text" } ] } ] } ] }
            """);
        ExtensionRegistry.Load(clash);
        var merged = ExtensionRegistry.For("weapon");
        SchemaRegistry.Populate(new Dictionary<string, AssetSchema>(StringComparer.OrdinalIgnoreCase)
        {
            ["testweapon"] = new AssetSchema
            {
                TypeName = "testweapon", GdfName = "testweapon.gdf",
                Properties = new List<PropertyDef> { new("tsSlot1", "Slot 1", "S", PropertyKind.Text, "") },
            },
        });
        var test = ExtensionRegistry.For("testweapon");
        SchemaRegistry.ResetToMock();
        var mergeSaid = string.Join(" | ", ExtensionRegistry.Diagnostics.Select(d => d.ToString()));
        Check($"records manifest: a list whose keys a deffile or an earlier extension holds is left out of the type ({mergeSaid})",
            merged.First(x => x.Id == "zz-other").Records.Select(r => r.Def.Key).SequenceEqual(new[] { "tsSlot#" })
            && merged.First(x => x.Id == "weapon-tech").Records.Count() == 2
            && test.All(x => x.Records.All(r => r.Def.Key != "tsSlot#"))
            && mergeSaid.Contains("tsSlot# would hold tsSlot1, a testweapon key") && mergeSaid.Contains("wtKick#'s keys are also weapon-tech's wtKick#"));
        ExtensionRegistry.Clear();
    }

    // ═══ The table in the editor: temp GDTs, the planner, temp files only ═══

    private const string RecordChildGdt =
        "{\r\n\t\"base_gun_up\" [ \"base_gun\" ]\r\n\t{\r\n\t\t\"damage\" \"80\"\r\n\t}\r\n"
        + "\t\"solo_gun\" ( \"weapon.gdf\" )\r\n\t{\r\n\t\t\"displayName\" \"Solo\"\r\n\t}\r\n"
        + "\t\"lonely_gun\" ( \"weapon.gdf\" )\r\n\t{\r\n\t\t\"displayName\" \"Lonely\"\r\n\t}\r\n}\r\n";

    private static void RecordEditorChecks()
    {
        SchemaRegistry.ResetToMock();
        var dir = NewScratch("rec-editor");
        string Text(string p) => GdtEncoding.File.GetString(File.ReadAllBytes(p));
        var parentPath = Path.Combine(dir, "parent.gdt");
        var childPath = Path.Combine(dir, "child.gdt");
        var parentx = "{\r\n" + Block("base_gun", new[] { ("wtEnabled", "1"), ("wtKick1", KickRow(1)), ("wtKick2", KickRow(2)) }) + "}\r\n";
        // solo_gun: rows numbered 1, 3, 10 (a gap, file order not numeric), one with spaces and a trailing comma, one
        // with a raw backslash; lonely_gun: a block holding nothing but one row.
        var odd1 = " 1, 0 ,3,93,1.5,0.35,1.3,1,";
        var odd3 = @"0,1,2,a\b,,,,";
        var childx = "{\r\n" + Block("solo_gun", new[] { ("wtEnabled", "1"), ("wtFireTimeMs", "63"), ("wtKick1", odd1), ("wtKick10", KickRow(10)), ("wtKick3", odd3) })
            + Block("lonely_gun", new[] { ("wtKick1", KickRow(4)) }) + "}\r\n";
        File.WriteAllBytes(parentPath, GdtEncoding.GetBytes(ParentGdt));
        File.WriteAllBytes(ExtensionSidecar.PathFor(parentPath), GdtEncoding.GetBytes(parentx));
        File.WriteAllBytes(childPath, GdtEncoding.GetBytes(RecordChildGdt));
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
        string Get(GdtFile g, string asset, string key) => g.Extensions?.Get(asset, "weapon-tech", key) ?? "(unset)";
        List<string> Keys(GdtFile g, string asset) => g.Extensions?.Find(asset, "weapon-tech")?.Properties.Keys
            .Where(k => k.StartsWith("wtKick") && char.IsAsciiDigit(k[^1])).OrderBy(k => k, ExtensionKeyComparer.Instance).ToList() ?? new();
        List<GdtSaveFileResult> Save(string label)
        {
            var problems = new List<string>();
            var requests = GdtSavePlanner.Plan(gdts, all, Array.Empty<AssetRecord>(), Array.Empty<GdtFile>(), null, problems);
            var result = NewSaveService(label).Save(requests);
            foreach (var f in result.Files)
                GdtSavePlanner.Commit(f);
            if (problems.Count > 0 || !result.AllSucceeded)
                Check($"records editor: save ({string.Join("; ", problems)}{string.Join("; ", result.Files.Select(f => f.Message))})", false);
            return result.Files.ToList();
        }

        ExtensionRegistry.Load(PlainTablesDir("rec-editor-fixture"));
        var tab = Open("solo_gun");
        var kicks = (RecordsPropertyViewModel)tab.AllSentinel.All.First(p => p.Key == "wtKick#");
        var slots = (RecordsPropertyViewModel)tab.AllSentinel.All.First(p => p.Key == "wtAdditiveSlot#");
        string[] Rows() => kicks.Rows.Select(r => string.Join(",", r.Cells.Select(c => c.RawValue))).ToArray();
        Check($"records editor: the table reads its rows in number order (1, 3, 10), each cell its column ({kicks.Rows.Count} rows)",
            kicks.Rows.Count == 3 && kicks.Rows[0].Cells[1].RawValue == " 0 " && kicks.Rows[1].Cells[3].RawValue == @"a\b"
            && kicks.Rows.Select(r => r.Number).SequenceEqual(new[] { 1, 2, 3 }) && !kicks.IsChanged && slots.Rows.Count == 0
            && tab.FlatRows.Contains(kicks) && kicks.CountText == "3 of 24");
        Check($"records editor: a row's problems show on it and the table's on its row ('{kicks.Problem}')",
            kicks.Rows[1].HasProblem && !kicks.Rows[0].HasProblem && kicks.Problem?.StartsWith("Row 2: Direction: ‘a\\b’ is not a number.") == true
            && tab.Problems.Any(p => p.Key == "wtKick#"));

        // Saving something else leaves the rows byte for byte.
        tab.AllSentinel.All.First(p => p.Key == "wtFireTimeMs").RawValue = "64";
        Save("rec-other");
        Check("records editor: rows nobody edited are saved byte for byte, out-of-order keys, odd spacing and all",
            Text(ExtensionSidecar.PathFor(childPath)) == childx.Replace("\"63\"", "\"64\"") && Text(childPath) == RecordChildGdt);
        tab.RebaseChanges();

        // A cell edit: its row is written again (spacing and the trailing comma kept), and the rows are numbered 1..N.
        kicks.Rows[0].Cells[3].RawValue = "94";
        Check($"records editor: a cell edit writes its row and numbers the rows 1..3 ({string.Join(",", Keys(child, "solo_gun"))})",
            Get(child, "solo_gun", "wtKick1") == " 1, 0 ,3,94,1.5,0.35,1.3,1," && Get(child, "solo_gun", "wtKick2") == odd3
            && Get(child, "solo_gun", "wtKick3") == KickRow(10) && Get(child, "solo_gun", "wtKick10") == "(unset)"
            && Keys(child, "solo_gun").SequenceEqual(new[] { "wtKick1", "wtKick2", "wtKick3" }) && kicks.IsChanged && tab.Changes.Any(c => c.Item == kicks));
        tab.UndoCommand.Execute(null);
        Check("records editor: Ctrl+Z puts the keys back as they were, numbers and all, in one step",
            Get(child, "solo_gun", "wtKick1") == odd1 && Get(child, "solo_gun", "wtKick3") == odd3 && Get(child, "solo_gun", "wtKick10") == KickRow(10)
            && Get(child, "solo_gun", "wtKick2") == "(unset)" && !kicks.IsChanged && kicks.Rows[0].Cells[3].RawValue == "93");
        tab.RedoCommand.Execute(null);
        Check("records editor: and redo does it again", Get(child, "solo_gun", "wtKick1").Contains(",94,") && kicks.Rows.Count == 3);

        // Steps in one cell coalesce into one undo step (one key).
        var dir0 = (NumberPropertyViewModel)kicks.Rows[2].Cells[3];
        var before = dir0.RawValue;
        dir0.Nudge(1);
        dir0.Nudge(1);
        dir0.Nudge(1);
        tab.UndoCommand.Execute(null);
        Check($"records editor: steps in a cell coalesce into one undo step ({before} → {dir0.RawValue})", dir0.RawValue == before);

        // Add, move, remove.
        kicks.Insert(1);
        Check($"records editor: Add puts a copy of the row above below it ({string.Join(" | ", Rows())})",
            kicks.Rows.Count == 4 && kicks.Rows[1].Cells.Select(c => c.RawValue).SequenceEqual(kicks.Rows[0].Cells.Select(c => c.RawValue))
            && Keys(child, "solo_gun").Count == 4);
        var third = Get(child, "solo_gun", "wtKick3");
        kicks.Move(kicks.Rows[2], -2);
        Check("records editor: a moved row moves its value to the key of its new place",
            Get(child, "solo_gun", "wtKick1") == third && kicks.Rows[0].Number == 1);
        tab.UndoCommand.Execute(null);
        Check("records editor: Ctrl+Z moves it back", Get(child, "solo_gun", "wtKick3") == third);
        var last = Get(child, "solo_gun", "wtKick4");
        kicks.Remove(kicks.Rows[1]);
        Check($"records editor: removing a row shifts the rest up and the last key goes ({string.Join(",", Keys(child, "solo_gun"))})",
            Keys(child, "solo_gun").SequenceEqual(new[] { "wtKick1", "wtKick2", "wtKick3" }) && Get(child, "solo_gun", "wtKick3") == last);
        while (kicks.Insert(kicks.Rows.Count) is not null) { }
        Check($"records editor: the table stops at the manifest's 24 rows ({kicks.Rows.Count}, '{kicks.AddTip}')",
            kicks.Rows.Count == 24 && !kicks.CanAdd && kicks.AddTip == "Kick patterns holds up to 24 rows" && Keys(child, "solo_gun").Count == 24);

        // Saved with 24 rows: keys in row order in the file, the GDT untouched.
        Save("rec-24");
        var text = Text(ExtensionSidecar.PathFor(childPath));
        var at = Enumerable.Range(1, 24).Select(i => text.IndexOf($"\"wtKick{i}\"", StringComparison.Ordinal)).ToList();
        Check("records editor: saved, the 24 keys sit in row order in the .gdtx (wtKick2 before wtKick10), the GDT byte for byte",
            at.All(i => i > 0) && at.Zip(at.Skip(1)).All(p => p.First < p.Second) && Text(childPath) == RecordChildGdt);
        var reread = LoadGdt(childPath);
        Check("records editor: and read back they are the same rows",
            RecordCodec.Join(RecordCodec.Rows(reread.Extensions!.Values("solo_gun", "weapon-tech"), kicks.List).Select(r => r.Value)) == kicks.RawValue);
        tab.RebaseChanges();

        // Undo all puts the whole table back as one step; Ctrl+Z takes that back.
        var saved24 = kicks.RawValue;
        kicks.Remove(kicks.Rows[0]);
        kicks.Rows[0].Cells[0].RawValue = kicks.Rows[0].Cells[0].RawValue == "1" ? "0" : "1";
        tab.RevertAllCommand.Execute(null);
        Check("records editor: Undo all puts the table back", kicks.RawValue == saved24 && Keys(child, "solo_gun").Count == 24 && !kicks.IsChanged);
        tab.UndoCommand.Execute(null);
        Check("records editor: and Ctrl+Z takes Undo all back", kicks.Rows.Count == 23);

        // An empty set removes every key; a block left empty goes, and the file with it.
        var lonelyTab = Open("lonely_gun");
        var lonely = (RecordsPropertyViewModel)lonelyTab.AllSentinel.All.First(p => p.Key == "wtKick#");
        lonely.Remove(lonely.Rows[0]);
        while (kicks.Rows.Count > 0)
            kicks.Remove(kicks.Rows[^1]);
        Check("records editor: no rows, no keys", Keys(child, "solo_gun").Count == 0 && Keys(child, "lonely_gun").Count == 0
            && child.Extensions!.Find("lonely_gun", "weapon-tech")!.Properties.Count == 0);
        Save("rec-empty");
        var after = Text(ExtensionSidecar.PathFor(childPath));
        Check("records editor: saved, the emptied block is deleted and the other loses only its rows",
            !after.Contains("lonely_gun") && !after.Contains("wtKick") && after.Contains("\"wtFireTimeMs\" \"64\"") && Text(childPath) == RecordChildGdt);

        // A derived weapon inherits its parent's table whole.
        var up = Open("base_gun_up");
        var upKicks = (RecordsPropertyViewModel)up.AllSentinel.All.First(p => p.Key == "wtKick#");
        Check($"records editor: a derived weapon shows its parent's table, inherited ({upKicks.Rows.Count} rows)",
            upKicks.Rows.Count == 2 && upKicks.IsInherited && upKicks.ParentValue == RecordCodec.Join(new[] { KickRow(1), KickRow(2) })
            && child.Extensions?.Find("base_gun_up", "weapon-tech") is null);
        upKicks.Rows[1].Cells[4].RawValue = "9";
        Check("records editor: editing it gives the derived weapon its own table (every row), the parent's untouched",
            Keys(child, "base_gun_up").SequenceEqual(new[] { "wtKick1", "wtKick2" }) && Get(child, "base_gun_up", "wtKick1") == KickRow(1)
            && upKicks.IsOverride && Get(parent, "base_gun", "wtKick2") == KickRow(2));
        upKicks.RevertToParentCommand.Execute(null);
        Check("records editor: ↑ (the parent's table) drops the copy, so it inherits again",
            Keys(child, "base_gun_up").Count == 0 && upKicks.IsInherited && upKicks.Rows.Count == 2);
        upKicks.Remove(upKicks.Rows[0]);
        upKicks.Remove(upKicks.Rows[0]);
        Check($"records editor: a derived weapon left with no rows of its own shows its parent's again ({upKicks.Rows.Count})",
            upKicks.Rows.Count == 2 && Keys(child, "base_gun_up").Count == 0 && upKicks.IsInherited);
        ExtensionRegistry.Clear();
    }

    // ═══ The app: dirty marks, problem count, journal, discard (mock data) ══

    private static void RecordSessionChecks()
    {
        var root = NewScratch("rec-session");
        Environment.SetEnvironmentVariable(ExtensionRegistry.DirVariable, PlainTablesDir("rec-session-fixture"));
        var vm = new MainViewModel(root);
        var window = ShowJournalWindow(vm);
        try
        {
            vm.OpenByName("wpn_ar_havoc_zm");
            var tab = vm.ActiveTab!;
            var record = tab.Record;
            var problemsBefore = vm.ProblemsOf(record);
            tab.AllSentinel.All.First(p => p.Key == "wtEnabled").RawValue = "1";
            var kicks = (RecordsPropertyViewModel)tab.AllSentinel.All.First(p => p.Key == "wtKick#");
            kicks.Insert(0);
            foreach (var (cell, value) in kicks.Rows[0].Cells.Zip(KickRow(3).Split(',')))
                cell.RawValue = value;
            kicks.Insert(1);
            Check($"records session: table edits count as the asset's changes ({vm.SessionStateText}, problems {vm.ProblemsOf(record)})",
                vm.SessionEditCount == 3 && record.HasSessionEdits && vm.ProblemsOf(record) == problemsBefore
                && vm.GdtOf(record)!.Extensions!.Get(record.Name, "weapon-tech", "wtKick2") == KickRow(3));
            kicks.Rows[1].Cells[1].RawValue = "7";
            Check($"records session: a bad row adds one to the Explorer's problem count ({problemsBefore} → {vm.ProblemsOf(record)})",
                vm.ProblemsOf(record) == problemsBefore + 1);
            kicks.Rows[1].Cells[1].RawValue = "1";

            vm.FlushSessionToDisk(closing: true);
            window.Close();
            vm.Dispose();
            ExtensionRegistry.Clear();
            vm = new MainViewModel(root);
            window = ShowJournalWindow(vm);
            Pump(200);
            vm.OpenByName("wpn_ar_havoc_zm");
            var reopened = (RecordsPropertyViewModel)vm.ActiveTab!.AllSentinel.All.First(p => p.Key == "wtKick#");
            Check($"records session: unsaved rows are kept across a restart ({vm.SessionStateText}, {reopened.Rows.Count} rows)",
                reopened.Rows.Count == 2 && reopened.Rows[1].Cells[1].RawValue == "1" && reopened.IsChanged);
            vm.DiscardSessionNow();
            Check($"records session: Discard all takes the rows away ({vm.SessionStateText})",
                vm.SessionEditCount == 0 && reopened.Rows.Count == 0 && vm.GdtOf(vm.ActiveTab!.Record)?.Extensions is null);
        }
        catch (Exception ex)
        {
            Check($"records session: {ex}", false);
        }
        finally
        {
            window.Close();
            vm.Dispose();
            Environment.SetEnvironmentVariable(ExtensionRegistry.DirVariable, NewScratch("no-extensions"));
            ExtensionRegistry.Clear();
        }
    }

    // ═══ Blocks follow their asset: duplicate, rename, copy, move, delete; search, table, compare ═══

    private static void RecordGapChecks(string outDir)
    {
        var install = NewScratch("rec-gaps");
        CopyTree(Path.Combine(InstallRoot, "deffiles"), Path.Combine(install, "deffiles"));
        var akRel = @"source_data\ar_ak47_h1.gdt";
        var smgRel = @"source_data\smg_standard.gdt";
        foreach (var rel in new[] { akRel, smgRel })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(install, rel))!);
            File.Copy(Path.Combine(InstallRoot, rel), Path.Combine(install, rel));
        }
        var akPath = Path.Combine(install, akRel);
        var smgPath = Path.Combine(install, smgRel);
        var akx = ExtensionSidecar.PathFor(akPath);
        var smgx = ExtensionSidecar.PathFor(smgPath);
        var weaponName = LoadGdt(akPath).Assets.First(a => a.Parent is null && a.Type == "bulletweapon").Name;
        var values = new List<(string, string)> { ("wtEnabled", "1"), ("wtFireTimeMs", "70") };
        values.AddRange(Enumerable.Range(1, 3).Select(i => ($"wtKick{i}", KickRow(i))));
        var original = "{\r\n" + Block(weaponName, values) + "}\r\n";
        File.WriteAllBytes(akx, GdtEncoding.GetBytes(original));
        var sessionDir = Path.Combine(install, "session");
        string Text(string p) => File.Exists(p) ? GdtEncoding.File.GetString(File.ReadAllBytes(p)) : "(no file)";

        var saved = (Mock: Environment.GetEnvironmentVariable("APEX_FORCE_MOCK"), Root: Environment.GetEnvironmentVariable("APEX_BO3_ROOT"),
            Persist: Environment.GetEnvironmentVariable("APEX_NO_PERSIST"));
        Environment.SetEnvironmentVariable("APEX_FORCE_MOCK", null);
        Environment.SetEnvironmentVariable("APEX_BO3_ROOT", install);
        Environment.SetEnvironmentVariable("APEX_NO_PERSIST", "1");
        var extensions = PlainTablesDir("rec-gaps-ext");
        Environment.SetEnvironmentVariable(ExtensionRegistry.DirVariable, extensions);
        MainViewModel? vm = null;
        MainWindow? window = null;
        void Start()
        {
            vm = new MainViewModel(sessionDir);
            window = ShowJournalWindow(vm);
            WaitUntil(() => vm.Status.StartsWith("Loaded") || vm.Status.StartsWith("Restored"), 60_000);
            Pump(100);
            if (vm.IsAlertOpen)
                vm.DismissAlertCommand.Execute(null);
        }
        void Stop()
        {
            vm!.FlushSessionToDisk(closing: true);
            window!.Close();
            vm.Dispose();
            ExtensionRegistry.Clear();
        }
        void SaveAll()
        {
            window!.KeyPress(K.S, RawInputModifiers.Control, PhysicalKey.S, "s");
            window!.KeyRelease(K.S, RawInputModifiers.Control, PhysicalKey.S, "s");
            WaitUntil(() => !vm!.IsSaveRunning, 10_000);
            Pump();
        }
        RecordsPropertyViewModel Kicks() => (RecordsPropertyViewModel)vm!.ActiveTab!.AllSentinel.All.First(p => p.Key == "wtKick#");
        try
        {
            Start();
            var db = DatabaseOf(vm!);
            var ak = db.Gdts.First(g => g.Name.EndsWith("ar_ak47_h1.gdt", StringComparison.OrdinalIgnoreCase));
            var smg = db.Gdts.First(g => g.Name.EndsWith("smg_standard.gdt", StringComparison.OrdinalIgnoreCase));
            var weapon = db.Assets.First(a => a.Name == weaponName);
            var akGdt = File.ReadAllBytes(akPath);

            // ── Search, the table and compare see extension values ──
            vm!.FilterText = "prop:wtKick2";
            vm.ApplyFilterNow();
            var hit = vm.FlatRows.Any(n => n.Asset == weapon);
            vm.FilterText = $"prop:wtKick~{90 + 3}";
            vm.ApplyFilterNow();
            var valueHit = vm.FlatRows.Any(n => n.Asset == weapon);
            vm.FilterText = "prop:wtKick~nothing_like_this";
            vm.ApplyFilterNow();
            var miss = !vm.FlatRows.Any(n => n.Asset == weapon);
            ExtensionRegistry.Clear();
            vm.FilterText = "prop:wtKick2";
            vm.ApplyFilterNow();
            var without = !vm.FlatRows.Any(n => n.Asset == weapon);
            ExtensionRegistry.Load(extensions);
            vm.FilterText = "";
            vm.ApplyFilterNow();
            Check($"records gaps: prop: finds extension keys and values (key {hit}, value {valueHit}, miss {miss}; with none installed it doesn't: {without})",
                hit && valueHit && miss && without);

            // ── Duplicate: the copy starts with the source's blocks ──
            vm.OpenByName(weaponName);
            Pump();
            vm.DuplicateActiveCommand.Execute(null);
            Pump();
            var copy = vm.ActiveTab!.Record;
            Check($"records gaps: Duplicate gives the copy the source's extension data ({copy.Name}: {Kicks().Rows.Count} rows, {vm.SessionStateText})",
                copy.Name == weaponName + "_copy" && Kicks().Rows.Count == 3 && !Kicks().IsChanged
                && ak.Extensions!.Get(copy.Name, "weapon-tech", "wtFireTimeMs") == "70" && vm.SessionEditCount == 1);
            // ...and its own: edit the copy's table.
            Kicks().Rows[2].Cells[3].RawValue = "120";

            // ── Compare: the table is one row, and ◀ takes it ──
            var cmp = new CompareViewModel(weapon, new[] { copy }, copy, _ => { }, () => { }, (t, n) => db.Assets.FirstOrDefault(a => a.Name == n), vm.GdtOf);
            var row = cmp.Rows.FirstOrDefault(r => r.Key == "wtKick#");
            Check($"records gaps: compare lists the table as one row with the difference ({cmp.Rows.Count} rows: {string.Join(",", cmp.Rows.Select(r => r.Key).Take(6))})",
                row is not null && row.Label == "Kick patterns" && row.Cells[0].IsDifferent && row.Cells[0].Display.Contains(",120,")
                && cmp.Rows.All(r => r.Key != "wtFireTimeMs"));
            ExtensionRegistry.Clear();
            var cmpPlain = new CompareViewModel(weapon, new[] { copy }, copy, _ => { }, () => { }, (t, n) => db.Assets.FirstOrDefault(a => a.Name == n));
            var cmpNone = new CompareViewModel(weapon, new[] { copy }, copy, _ => { }, () => { }, (t, n) => db.Assets.FirstOrDefault(a => a.Name == n), vm.GdtOf);
            Check("records gaps: with no extension installed, compare is row for row what it was",
                cmpPlain.Rows.Select(r => r.Key + r.BaseDisplay + r.Cells[0].Display).SequenceEqual(cmpNone.Rows.Select(r => r.Key + r.BaseDisplay + r.Cells[0].Display)));
            ExtensionRegistry.Load(extensions);

            // ── The table: extension fields are columns to pick, written to the block, undone ──
            var table = new TableViewModel("bulletweapon", new[] { weapon, copy }, 2, _ => { }, () => { },
                (t, n) => db.Assets.FirstOrDefault(a => a.Name == n), vm.GdtOf);
            table.ColumnFilter = "wtFire";
            var fire = table.ColumnChoices.FirstOrDefault(c => c.Key == "wtFireTimeMs");
            Check($"records gaps: the table's picker offers extension fields ({table.ColumnChoices.Count} match), never as a default column",
                fire is not null && table.Columns.All(c => c.Def.Extension.Length == 0) && table.ColumnChoices.All(c => c.Key != "wtKick#"));
            if (fire is not null)
                fire.IsShown = true;
            var cell = table.Rows.First(r => r.Record == copy).Cells.Last();
            cell.Editor.RawValue = "55";
            Check("records gaps: a table cell writes the extension value to the asset's block",
                cell.Editor.RawValue == "55" && ak.Extensions!.Get(copy.Name, "weapon-tech", "wtFireTimeMs") == "55" && copy.Properties.All(p => p.Key != "wtFireTimeMs"));
            table.Undo();
            Check("records gaps: and the table's Ctrl+Z takes it back", ak.Extensions!.Get(copy.Name, "weapon-tech", "wtFireTimeMs") == "70" && cell.Editor.RawValue == "70");
            ExtensionRegistry.Clear();
            var plainTable = new TableViewModel("bulletweapon", new[] { weapon, copy }, 2, _ => { }, () => { }, (t, n) => null);
            var noneTable = new TableViewModel("bulletweapon", new[] { weapon, copy }, 2, _ => { }, () => { }, (t, n) => null, vm.GdtOf);
            plainTable.RefreshColumnChoices();
            noneTable.RefreshColumnChoices();
            Check("records gaps: with no extension installed, the table's columns and picker are what they were",
                plainTable.Columns.Select(c => c.Key).SequenceEqual(noneTable.Columns.Select(c => c.Key))
                && plainTable.ColumnChoices.Select(c => c.Key).SequenceEqual(noneTable.ColumnChoices.Select(c => c.Key)));
            ExtensionRegistry.Load(extensions);

            // ── Rename the copy: its block follows ──
            Rename(vm, copy, weaponName + "_renamed");
            var renamed = weaponName + "_renamed";
            Check($"records gaps: renaming carries the blocks to the new name ({copy.Name})",
                copy.Name == renamed && ak.Extensions!.Find(renamed, "weapon-tech") is not null && ak.Extensions.Find(weaponName + "_copy", "weapon-tech") is null
                && !ak.Extensions.Orphans().Any());
            SaveAll();
            var akText = Text(akx);
            Check($"records gaps: saved, the copy's block is in the .gdtx under its new name, its edit in it, no orphan ('{vm.Status}')",
                akText.StartsWith(original[..^3]) && akText.Contains($"\"{renamed}\" ( \"weapon-tech\" )") && !akText.Contains("_copy\"")
                && akText.Contains(",120,") && !LoadGdt(akPath).Extensions!.Orphans().Any());

            // Renaming a saved block renames it in the file.
            Rename(vm, copy, weaponName + "_second");
            SaveAll();
            akText = Text(akx);
            Check("records gaps: renaming a saved asset renames its block in the .gdtx, in place",
                akText.Contains($"\"{weaponName}_second\" ( \"weapon-tech\" )") && !akText.Contains(renamed) && !LoadGdt(akPath).Extensions!.Orphans().Any());

            // ── Copy into another GDT: the copy's blocks go to that GDT's .gdtx; Ctrl+Z before saving takes them back ──
            vm.CopyAssetsInto(new[] { weapon }, smg);
            Pump(50);
            var pasted = smg.Assets.FirstOrDefault(a => a.Name.StartsWith(weaponName + "_copy", StringComparison.Ordinal));
            Check($"records gaps: a copy pasted into another GDT takes its blocks there ({pasted?.Name})",
                pasted is not null && smg.Extensions?.Find(pasted.Name, "weapon-tech")?.Properties.Count == values.Count);
            Key(window!, K.Z, RawInputModifiers.Control);
            Check($"records gaps: Ctrl+Z on the paste takes its blocks too ('{vm.Status}')",
                pasted is not null && !smg.Assets.Contains(pasted) && smg.Extensions?.Find(pasted.Name, "weapon-tech") is null);
            Key(window!, K.Y, RawInputModifiers.Control);
            Check("records gaps: and Ctrl+Y puts them back", pasted is not null && smg.Extensions?.Find(pasted.Name, "weapon-tech") is not null);

            // ── Move the renamed copy to the other GDT: its block leaves the old .gdtx for the new one ──
            vm.MoveAssetsInto(new[] { copy }, smg);
            Pump(50);
            Check("records gaps: moving carries the blocks to the new GDT's .gdtx",
                ak.Extensions!.Find(copy.Name, "weapon-tech") is null && smg.Extensions?.Find(copy.Name, "weapon-tech")?.Properties.GetValueOrDefault("wtKick3")?.Contains(",120,") == true);
            Key(window!, K.Z, RawInputModifiers.Control);
            Check("records gaps: Ctrl+Z on the move brings them back in place", ak.Extensions.Find(copy.Name, "weapon-tech") is { Disk: not null }
                && smg.Extensions?.Find(copy.Name, "weapon-tech") is null);
            Key(window!, K.Y, RawInputModifiers.Control);
            SaveAll();
            var smgText = Text(smgx);
            akText = Text(akx);
            Check($"records gaps: saved, the moved and pasted assets' blocks are in the new .gdtx, gone from the old ('{vm.Status}')",
                smgText.Contains($"\"{copy.Name}\" ( \"weapon-tech\" )") && pasted is not null && smgText.Contains($"\"{pasted.Name}\" ( \"weapon-tech\" )")
                && !akText.Contains(copy.Name) && akText == original);

            // ── Delete: the block goes with the save; Discard all before it puts it back ──
            vm.OpenByName(copy.Name);
            vm.DeleteActiveCommand.Execute(null);
            WaitUntil(() => !vm.IsDeletePending, 5_000);
            Pump();
            Check("records gaps: deleting an asset takes its blocks out of the session's .gdtx",
                smg.Extensions!.Find(copy.Name, "weapon-tech") is null && smg.Extensions.Removed.Any(b => b.Name == copy.Name));
            vm.DiscardSessionNow();
            Check("records gaps: Discard all before saving puts them back", smg.Extensions!.Find(copy.Name, "weapon-tech") is not null && smg.Extensions.Removed.Count == 0);

            // ── Restarts: rename, duplicate and delete come back from the journal with their blocks ──
            vm.OpenByName(pasted!.Name);
            Pump();
            vm.DuplicateActiveCommand.Execute(null);
            Pump();
            var second = vm.ActiveTab!.Record.Name;
            Rename(vm, vm.ActiveTab.Record, weaponName + "_journal");
            vm.OpenByName(copy.Name);
            vm.DeleteActiveCommand.Execute(null);
            WaitUntil(() => !vm.IsDeletePending, 5_000);
            Pump();
            Stop();
            Start();
            db = DatabaseOf(vm!);
            smg = db.Gdts.First(g => g.Name.EndsWith("smg_standard.gdt", StringComparison.OrdinalIgnoreCase));
            Check($"records gaps: after a restart the duplicate (renamed) has its blocks and the deleted asset's are out ({second}; {vm!.SessionStateText})",
                smg.Extensions?.Find(weaponName + "_journal", "weapon-tech")?.Properties.Count == values.Count
                && smg.Extensions.Find(second, "weapon-tech") is null && smg.Extensions.Find(copy.Name, "weapon-tech") is null
                && smg.Extensions.Removed.Any(b => b.Name == copy.Name));
            SaveAll();
            smgText = Text(smgx);
            Check($"records gaps: saved, the .gdtx holds the duplicate's block and not the deleted one's, the GDTs only what changed ('{vm.Status}')",
                smgText.Contains($"\"{weaponName}_journal\" ( \"weapon-tech\" )") && !smgText.Contains($"\"{copy.Name}\"")
                && File.ReadAllBytes(akPath).AsSpan().SequenceEqual(akGdt) && Text(akx) == original);
        }
        catch (Exception ex)
        {
            Check($"records gaps: {ex}", false);
        }
        finally
        {
            window?.Close();
            vm?.Dispose();
            Environment.SetEnvironmentVariable("APEX_FORCE_MOCK", saved.Mock);
            Environment.SetEnvironmentVariable("APEX_BO3_ROOT", saved.Root);
            Environment.SetEnvironmentVariable("APEX_NO_PERSIST", saved.Persist);
            ExtensionRegistry.Clear();
            SchemaRegistry.ResetToMock();
        }
    }

    // ═══ The live app on a temp install, real input ═════════════════════════

    private static void RecordUiChecks(string outDir)
    {
        var install = NewScratch("rec-install");
        CopyTree(Path.Combine(InstallRoot, "deffiles"), Path.Combine(install, "deffiles"));
        var weaponRel = @"source_data\ar_ak47_h1.gdt";
        Directory.CreateDirectory(Path.Combine(install, "source_data"));
        File.Copy(Path.Combine(InstallRoot, weaponRel), Path.Combine(install, weaponRel));
        var weaponPath = Path.Combine(install, weaponRel);
        var weaponName = LoadGdt(weaponPath).Assets.First(a => a.Parent is null && a.Type == "bulletweapon").Name;
        var values = new List<(string, string)> { ("wtEnabled", "1") };
        values.AddRange(Enumerable.Range(1, 3).Select(i => ($"wtKick{i}", KickRow(i))));
        values.Add(("wtAdditiveSlot1", @"bullet,slot:bullets,vm_ak47_bullets,1,30"));
        values.Add(("wtAdditiveSlot2", @"empty,slot:empty,vm_ak47_empty,1,side:left"));
        var sidecarPath = ExtensionSidecar.PathFor(weaponPath);
        File.WriteAllBytes(sidecarPath, GdtEncoding.GetBytes("{\r\n" + Block(weaponName, values) + "}\r\n"));

        var saved = (Mock: Environment.GetEnvironmentVariable("APEX_FORCE_MOCK"), Root: Environment.GetEnvironmentVariable("APEX_BO3_ROOT"),
            Persist: Environment.GetEnvironmentVariable("APEX_NO_PERSIST"));
        Environment.SetEnvironmentVariable("APEX_FORCE_MOCK", null);
        Environment.SetEnvironmentVariable("APEX_BO3_ROOT", install);
        Environment.SetEnvironmentVariable("APEX_NO_PERSIST", "1");
        var extensions = PlainTablesDir("rec-ui");
        Environment.SetEnvironmentVariable(ExtensionRegistry.DirVariable, extensions);
        MainViewModel? vm = null;
        MainWindow? window = null;
        try
        {
            vm = new MainViewModel(Path.Combine(install, "session"));
            window = ShowJournalWindow(vm);
            WaitUntil(() => vm.Status.StartsWith("Loaded"), 60_000);
            Pump(100);
            if (vm.IsAlertOpen)
                vm.DismissAlertCommand.Execute(null);
            vm.OpenByName(weaponName);
            Pump(300);
            var tab = vm.ActiveTab!;
            var kicks = (RecordsPropertyViewModel)tab.AllSentinel.All.First(p => p.Key == "wtKick#");
            var gdt = vm.GdtOf(tab.Record)!;
            string Get(string key) => gdt.Extensions?.Get(weaponName, "weapon-tech", key) ?? "(unset)";
            RecordTableEditor Table() => window.GetVisualDescendants().OfType<RecordTableEditor>().First(t => t.DataContext == kicks && t.IsEffectivelyVisible);
            void Reveal(string key)
            {
                tab.RevealProperty(key);
                Pump(50);
                window.UpdateLayout();
                Pump(50);
            }
            Control CellEditor(int row, int column)
            {
                var table = Table();
                var grid = table.GetVisualDescendants().OfType<Grid>().Where(g => g.Classes.Contains("rrow") && g.IsEffectivelyVisible).ElementAt(row);
                var cell = grid.GetVisualDescendants().OfType<ContentControl>().Where(c => c.Classes.Contains("rcell")).ElementAt(column);
                return cell.GetVisualDescendants().OfType<InputElement>().OfType<Control>()
                    .First(c => c is ScrubNumberBox or TextBox or ComboBox or ToggleButton && c.Focusable);
            }
            (int Row, int Column)? FocusedCell()
            {
                var focused = window.FocusManager?.GetFocusedElement() as Visual;
                var grid = focused?.GetSelfAndVisualAncestors().OfType<Grid>().FirstOrDefault(g => g.Classes.Contains("rrow"));
                if (grid?.DataContext is not RecordRowViewModel r)
                    return null;
                var cell = focused!.GetSelfAndVisualAncestors().TakeWhile(v => v != grid).OfType<ContentControl>().LastOrDefault(c => c.Classes.Contains("rcell"));
                return (kicks.Rows.IndexOf(r), cell is null ? -1 : Grid.GetColumn(cell));
            }

            Reveal("wtKick#");
            Capture(window, Path.Combine(outDir, "64-records-dark.png"));
            Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
            Pump(100);
            Capture(window, Path.Combine(outDir, "65-records-light.png"));
            Avalonia.Application.Current.RequestedThemeVariant = ThemeVariant.Dark;
            Pump();

            // ── Keyboard: Ctrl+Enter adds, Alt+↓ / Alt+↑ move, ↑↓ walk a column, Tab walks a row, Alt+Delete removes ──
            CellEditor(0, 3).Focus(NavigationMethod.Directional);
            Pump();
            Key(window, K.Enter, RawInputModifiers.Control);
            Pump(50);
            Check($"records ui: Ctrl+Enter adds a copy of the row below it and puts the keyboard in it ({kicks.Rows.Count} rows, focus {FocusedCell()})",
                kicks.Rows.Count == 4 && Get("wtKick2") == KickRow(1) && Get("wtKick4") == KickRow(3) && FocusedCell() == (1, 3));
            Key(window, K.Down, RawInputModifiers.Alt);
            Pump(50);
            Check($"records ui: Alt+↓ moves the row down, and the keyboard with it (focus {FocusedCell()})",
                Get("wtKick2") == KickRow(2) && Get("wtKick3") == KickRow(1) && FocusedCell() == (2, 3));
            Key(window, K.Up, RawInputModifiers.Alt);
            Pump(50);
            Check($"records ui: Alt+↑ moves it back up (focus {FocusedCell()})", Get("wtKick2") == KickRow(1) && FocusedCell() == (1, 3));
            Key(window, K.Down);
            Pump(50);
            Check($"records ui: ↓ goes to the same column in the next row (focus {FocusedCell()})", FocusedCell() == (2, 3));
            Key(window, K.Up);
            Pump(50);
            Check($"records ui: ↑ comes back (focus {FocusedCell()})", FocusedCell() == (1, 3));
            Key(window, K.Tab);
            Pump(50);
            Check($"records ui: Tab goes to the next cell of the row (focus {FocusedCell()})", FocusedCell() == (1, 4));
            Key(window, K.Tab, RawInputModifiers.Shift);
            Pump(50);
            Key(window, K.Up, RawInputModifiers.Control);
            Check($"records ui: Ctrl+↑ steps a number cell by its step ({Get("wtKick2")})", Get("wtKick2").Split(',')[3] == "91.1");
            Key(window, K.Z, RawInputModifiers.Control);
            Check($"records ui: Ctrl+Z undoes the step ({Get("wtKick2")})", Get("wtKick2") == KickRow(1));
            Key(window, K.Delete, RawInputModifiers.Alt);
            Pump(50);
            Check($"records ui: Alt+Delete removes the row, the keyboard stays in the table (focus {FocusedCell()})",
                kicks.Rows.Count == 3 && Get("wtKick2") == KickRow(2) && Get("wtKick4") == "(unset)" && FocusedCell() == (1, 3));
            Key(window, K.Z, RawInputModifiers.Control);
            Check($"records ui: Ctrl+Z brings the removed row back ({kicks.Rows.Count})", kicks.Rows.Count == 4 && Get("wtKick2") == KickRow(1));
            for (var i = 0; i < 3; i++)
                Key(window, K.Z, RawInputModifiers.Control);
            Check($"records ui: and Ctrl+Z takes back the moves and the added row ({kicks.Rows.Count})",
                kicks.Rows.Count == 3 && Get("wtKick4") == "(unset)" && kicks.RawValue == RecordCodec.Join(Enumerable.Range(1, 3).Select(KickRow)));
            Key(window, K.Y, RawInputModifiers.Control);
            Check($"records ui: Ctrl+Y redoes the add ({kicks.Rows.Count})", kicks.Rows.Count == 4 && Get("wtKick4") == KickRow(3));
            Key(window, K.Z, RawInputModifiers.Control);

            // Space flips a switch cell; Tab from the last cell of the last row reaches + Add row, and Enter there adds.
            var ads = (ToggleButton)CellEditor(1, 0);
            ads.Focus(NavigationMethod.Directional);
            Pump();
            Key(window, K.Space);
            Check($"records ui: Space flips a switch cell ({Get("wtKick2")})", Get("wtKick2").StartsWith("1,"));
            Key(window, K.Z, RawInputModifiers.Control);
            CellEditor(2, 7).Focus(NavigationMethod.Directional);
            Pump();
            Key(window, K.Tab);
            var focusedAdd = window.FocusManager?.GetFocusedElement() is Button { Name: "AddButton" };
            Key(window, K.Enter);
            Pump(50);
            Check($"records ui: Tab from the last cell reaches + Add row, and Enter on it adds a row ({focusedAdd}, {kicks.Rows.Count})",
                focusedAdd && kicks.Rows.Count == 4);
            Key(window, K.Z, RawInputModifiers.Control);
            Check("records ui: and the table is as it was", kicks.RawValue == RecordCodec.Join(Enumerable.Range(1, 3).Select(KickRow)));

            // Typing a number: a digit starts it, Enter commits, Esc throws typing away.
            void TypeDigit(char c)
            {
                // The key starts the typing with its symbol (a number box takes no text input until it is typing).
                window.KeyPress(K.D0 + (c - '0'), RawInputModifiers.None, PhysicalKey.None, c.ToString());
                window.KeyRelease(K.D0 + (c - '0'), RawInputModifiers.None, PhysicalKey.None, c.ToString());
                Pump();
            }
            var number = (ScrubNumberBox)CellEditor(0, 4);
            number.Focus(NavigationMethod.Directional);
            Pump();
            TypeDigit('7');
            Key(window, K.Enter);
            Check($"records ui: typing a number into a cell and Enter writes it ({Get("wtKick1")})", Get("wtKick1").Split(',')[4] == "7");
            TypeDigit('8');
            Key(window, K.Escape);
            Check($"records ui: Esc throws away typing ({Get("wtKick1")})", Get("wtKick1").Split(',')[4] == "7");
            Key(window, K.Z, RawInputModifiers.Control);

            // ── Mouse: + Add row, ↑ ↓ ✕ on a row ──
            Reveal("wtKick#");
            var add = Table().GetVisualDescendants().OfType<Button>().First(b => b.Name == "AddButton");
            Click(window, add);
            Pump(50);
            Check($"records ui: a click on + Add row adds a copy of the last row ({kicks.Rows.Count})", kicks.Rows.Count == 4 && Get("wtKick4") == KickRow(3));
            Button Action(int row, string glyph) => Table().GetVisualDescendants().OfType<Grid>().Where(g => g.Classes.Contains("rrow") && g.IsEffectivelyVisible).ElementAt(row)
                .GetVisualDescendants().OfType<Button>().First(b => b.Content as string == glyph);
            Click(window, Action(0, "↓"));
            Check("records ui: ↓ on a row moves it down", Get("wtKick1") == KickRow(2) && Get("wtKick2") == KickRow(1));
            Click(window, Action(1, "↑"));
            Check("records ui: ↑ moves it back", Get("wtKick1") == KickRow(1));
            Click(window, Action(3, "✕"));
            Check($"records ui: ✕ removes the row ({kicks.Rows.Count})", kicks.Rows.Count == 3 && Get("wtKick4") == "(unset)");
            Check("records ui: the table is back as it started", kicks.RawValue == RecordCodec.Join(Enumerable.Range(1, 3).Select(KickRow)));

            // A problem shows on its row (a reserved column: the cells don't move).
            var before = CellEditor(0, 7).TranslatePoint(default, window);
            kicks.Rows[0].Cells[1].RawValue = "5";
            Pump();
            window.UpdateLayout();
            Check($"records ui: a bad cell puts ⚠ on its row and the table's line, and nothing moves ('{kicks.Problem}')",
                kicks.Rows[0].HasProblem && kicks.HasProblem && CellEditor(0, 7).TranslatePoint(default, window) == before);
            Capture(window, Path.Combine(outDir, "67-records-problem.png"));
            kicks.Rows[0].Cells[1].RawValue = "0";

            Reveal("wtAdditiveSlot#");
            Capture(window, Path.Combine(outDir, "66-records-slots.png"));

            // ── Budgets ──
            const int warm = 5, runs = 30;
            SilenceHeadlessRenderTimer();
            Reveal("wtKick#");
            window.CaptureRenderedFrame();
            CellEditor(1, 3).Focus(NavigationMethod.Directional);
            Pump();
            var up = true;
            var step = Time(window, "Record cell: Ctrl+arrow step", PerfBudgets.Frame, warm, runs,
                _ => KeyStroke(window, up ? K.Up : K.Down, RawInputModifiers.Control), after: i => up = i % 2 == 1);
            // Adding a row (its eight editors built, the keys written) and removing one, each from a cell of row 2.
            void FocusRow2()
            {
                CellEditor(1, 3).Focus(NavigationMethod.Directional);
                Pump();
            }
            var addRow = Time(window, "Record row: Ctrl+Enter adds", PerfBudgets.Frame, warm, runs,
                _ => KeyStroke(window, K.Enter, RawInputModifiers.Control),
                before: _ => FocusRow2(), after: _ => { kicks.Remove(kicks.Rows[2]); Pump(); });
            var removeRow = Time(window, "Record row: Alt+Delete removes", PerfBudgets.Frame, warm, runs,
                _ => KeyStroke(window, K.Delete, RawInputModifiers.Alt),
                before: _ => { kicks.Insert(2); Pump(); FocusRow2(); }, after: _ => Pump());
            var moving = true;
            var move = Time(window, "Record row: Alt+arrow move", PerfBudgets.Frame, warm, runs,
                _ => KeyStroke(window, moving ? K.Down : K.Up, RawInputModifiers.Alt), after: _ => { moving = !moving; Pump(); });
            Check($"records ui: the timed keys left the table as it was ({kicks.Rows.Count} rows)", kicks.Rows.Count is 3 or 4);

            // Opening a weapon with 24 rows, with and without extensions, and bringing its table into view.
            var opens = (With: new List<double>(), Without: new List<double>());
            while (kicks.Insert(kicks.Rows.Count) is not null) { }
            var fullWeapon = tab.Record;
            {
                for (var i = 0; i < 2 * (warm + 15); i++)
                {
                    var with = i % 2 == 0;
                    if (with)
                        ExtensionRegistry.Load(extensions);
                    else
                        ExtensionRegistry.Clear();
                    var one = Time(window, "open", PerfBudgets.OpenAsset, 0, 1, _ => vm.OpenAsset(fullWeapon), before: _ =>
                    {
                        while (vm.OpenTabs.Count > 0)
                            vm.CloseActiveTabCommand.Execute(null);
                    });
                    if (i >= 2 * warm)
                        (with ? opens.With : opens.Without).Add(one.Median);
                }
                ExtensionRegistry.Load(extensions);
                while (vm.OpenTabs.Count > 0)
                    vm.CloseActiveTabCommand.Execute(null);
                vm.OpenAsset(fullWeapon);
                Pump();
                var fullTab = vm.ActiveTab!;
                var fullKicks = (RecordsPropertyViewModel)fullTab.AllSentinel.All.First(p => p.Key == "wtKick#");
                Check($"records ui: a weapon with 24 kick rows opens with its table full ({fullKicks.Rows.Count})", fullKicks.Rows.Count == 24 && !fullKicks.CanAdd);
                // Working on kicks across weapons: the weapon and one based on it (which shows the same 24 rows, inherited)
                // open in tabs, each switch landing on the other's table, so the table rebinds rather than shows again.
                AssetRecord? derived = null;
                var reveal = Time(window, "Switch tabs to another weapon's 24-row table", PerfBudgets.OpenAsset, warm, 15,
                    i =>
                    {
                        vm.ActiveTab = vm.OpenTabs[i % 2];
                        window.UpdateLayout();
                        vm.ActiveTab.RevealProperty("wtKick#");
                        window.UpdateLayout();
                    },
                    before: i =>
                    {
                        if (i > 0)
                            return;
                        while (vm.OpenTabs.Count > 0)
                            vm.CloseActiveTabCommand.Execute(null);
                        vm.OpenAsset(fullWeapon);
                        vm.ActiveTab!.IsPreview = false;
                        vm.DeriveAsset(fullWeapon);
                        derived = vm.ActiveTab!.Record;
                        vm.ActiveTab.View = EditorView.All;
                        Pump();
                    });
                var shownRows = window.GetVisualDescendants().OfType<Grid>().Count(g => g.Classes.Contains("rrow") && g.IsEffectivelyVisible);
                var lastCore = vm.ActiveTab!.FlatRows.OfType<PropertyItemViewModel>().Last(p => p.Def.Extension.Length == 0).Key;
                var switchFar = Time(window, "Switch tabs to another weapon's last deffile row, for scale", PerfBudgets.OpenAsset, warm, 15,
                    i =>
                    {
                        vm.ActiveTab = vm.OpenTabs[i % 2];
                        window.UpdateLayout();
                        vm.ActiveTab.RevealProperty(lastCore);
                        window.UpdateLayout();
                    });
                Console.WriteLine($"info  {switchFar.Name}: median {switchFar.Median:0.0} ms, p95 {switchFar.P95:0.0} ms");
                Check($"records ui: the switches landed on a table with its 24 rows ({derived?.Name}, {vm.OpenTabs.Count} tabs, {shownRows} rows shown)",
                    derived is not null && vm.OpenTabs.Count == 2 && shownRows >= 24);
                void Reopen()
                {
                    while (vm.OpenTabs.Count > 0)
                        vm.CloseActiveTabCommand.Execute(null);
                    vm.OpenAsset(fullWeapon);
                    window.UpdateLayout();
                    Pump();
                }
                var jumpTable = Time(window, "Jump to the 24-row table (just opened)", PerfBudgets.OpenAsset, warm, 15,
                    _ => { vm.ActiveTab!.RevealProperty("wtKick#"); window.UpdateLayout(); }, before: _ => Reopen());
                var far = vm.ActiveTab!.FlatRows.OfType<PropertyItemViewModel>().Last(p => p.Def.Extension.Length == 0);
                var jumpFar = Time(window, $"Jump to the form's last deffile row (just opened), for scale", PerfBudgets.OpenAsset, warm, 15,
                    _ => { vm.ActiveTab!.RevealProperty(far.Key); window.UpdateLayout(); }, before: _ => Reopen());
                Console.WriteLine($"info  {jumpTable.Name}: median {jumpTable.Median:0.0} ms, p95 {jumpTable.P95:0.0} ms; {jumpFar.Name}: median {jumpFar.Median:0.0} ms, p95 {jumpFar.P95:0.0} ms");
                var openWith = Summarize("Open a weapon with 24 kick rows", PerfBudgets.OpenAsset, opens.With);
                var openWithout = Summarize("Open the same weapon without extensions", PerfBudgets.OpenAsset, opens.Without);
                foreach (var r in new[] { step, addRow, removeRow, move, openWith, openWithout, reveal })
                    Console.WriteLine($"info  {r.Name}: median {r.Median:0.0} ms, p95 {r.P95:0.0} ms (wall {r.WallMedian:0.0} ms; budget {r.Budget:0} ms)");
                // Opening against its budget is the open-asset perf gate's (on the real install); here, what 24 rows add.
                Gate($"perf (records): a 24-row kick table adds less than a frame to opening its weapon (median {openWith.Median:0.0} ms with, {openWithout.Median:0.0} ms without extensions)",
                    openWith.Median <= openWithout.Median + PerfBudgets.Frame);
                Gate($"perf (records): switching to another weapon's 24-row table median {reveal.Median:0.0} ms, p95 {reveal.P95:0.0} ms (budget {PerfBudgets.OpenAsset:0} ms)", reveal.Pass);
                // Cold (a fresh editor view, nothing to recycle): the table's first build over a jump as far without it.
                // Reported, not gated: it moves with the machine's load more than a gate allows.
                Console.WriteLine($"info  perf (records): on a fresh view the table's first build adds {jumpTable.Median - jumpFar.Median:0} ms to a jump that far ({jumpTable.Median:0} ms with it, {jumpFar.Median:0} ms to the last deffile row)");
            }
            foreach (var r in new[] { step, addRow, removeRow, move })
                Gate($"perf (records): {r.Name} median {r.Median:0.0} ms, p95 {r.P95:0.0} ms (budget {r.Budget:0} ms)", r.Pass);
        }
        catch (Exception ex)
        {
            Check($"records ui: {ex}", false);
        }
        finally
        {
            window?.Close();
            vm?.Dispose();
            Environment.SetEnvironmentVariable("APEX_FORCE_MOCK", saved.Mock);
            Environment.SetEnvironmentVariable("APEX_BO3_ROOT", saved.Root);
            Environment.SetEnvironmentVariable("APEX_NO_PERSIST", saved.Persist);
            ExtensionRegistry.Clear();
            SchemaRegistry.ResetToMock();
        }
    }
}
