using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Apex.Editor.Controls;
using Apex.Editor.Models;
using Apex.Editor.Services.Extensions;
using Apex.Editor.Services.Gdt;
using Apex.Editor.Services.Save;
using Apex.Editor.ViewModels;
using GdtEncoding = Apex.Render.Data.Gdt.GdtEncoding;

namespace Apex.Shots;

/// <summary>
/// What a <c>.gdtx</c> means, as the linker brief states it: numbered keys in row order (by number, then fewer digits),
/// <c>//</c> comments read past and kept byte for byte by a save, the refusals that remain, and a table no edit touched
/// staying as it is. Then the manifest's hidden columns and tables placed among a section's fields. Pure checks and the
/// editor on temp GDTs; the app with real input is <see cref="GrammarUiChecks"/>.
/// </summary>
public partial class Program
{
    private static ExtensionRecordList PlainList(string stem) => new()
    {
        Def = new PropertyDef(stem + "#", stem, "S", PropertyKind.Text, ""),
        Base = stem,
        Columns = [new RecordColumn { Name = "v", Def = new PropertyDef("v", "v", "S", PropertyKind.Text, "") }],
    };

    // ═══ Reading: row order and comments ═══════════════════════════════════

    private static void GdtxReadChecks()
    {
        var kick = PlainList("wtKick");
        var keys = new[] { "wtKick001", "wtKick3", "wtKick01", "wtKick1", "wtKick0" };
        var expected = new[] { "wtKick0", "wtKick1", "wtKick01", "wtKick001", "wtKick3" };
        foreach (var (insertion, what) in new[] { (keys, "as written"), (keys.Reverse().ToArray(), "reversed") })
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var k in insertion)
                values[k] = k;
            var rows = RecordCodec.Rows(values, kick).Select(r => r.Key).ToList();
            Check($"gdtx order: rows by number, one number spelled twice by fewer digits first, whatever the file order ({what}: {string.Join(",", rows)})",
                rows.SequenceEqual(expected));
        }
        Check("gdtx order: the order a save writes keys in is the order rows are read in",
            keys.OrderBy(k => k, ExtensionKeyComparer.Instance).SequenceEqual(expected));

        // Comments: read past wherever whitespace may be, to the end of the line, quotes and braces in them included.
        const string commented =
            "{\r\n" +
            "// a \"quote\" and a } brace between blocks\r\n" +
            "\t\"ar_c_zm\" ( \"weapon-tech\" ) // after the header\r\n" +
            "\t{\r\n" +
            "\t\t// first line\r\n" +
            "\t\t\"wtEnabled\" \"1\" // trailing, with \" and }\r\n" +
            "\t\t\"wtFireTimeMs\" // between a key and its value\r\n" +
            "\t\t\"80\"\r\n" +
            "\t\t// \"wtKick9\" \"commented out\" }\r\n" +
            "\t\t\"wtKick1\" \"a//b\"\r\n" +
            "\t}\r\n" +
            "\t\"ar_d_zm\" ( \"weapon-tech\" )\r\n\t{\r\n\t\t\"wtKick1\" \"b\"\r\n\t}\r\n" +
            "}\r\n";
        var bytes = GdtEncoding.GetBytes(commented);
        var entries = GdtIndexer.Index(bytes);
        var layout = GdtLayout.Scan(bytes);
        var c = entries.Count > 0 ? GdtParser.ParseProperties(bytes, entries[0].BodyOffset, entries[0].BodyLength) : new();
        Check($"gdtx comments: every key after a // comment is read, a commented-out key isn't, // inside a value is text ({string.Join(" ", c.Select(kv => $"{kv.Key}={kv.Value}"))})",
            entries.Select(e => e.Name).SequenceEqual(new[] { "ar_c_zm", "ar_d_zm" }) && c.Count == 3 && c["wtEnabled"] == "1"
            && c["wtFireTimeMs"] == "80" && c["wtKick1"] == "a//b" && !c.ContainsKey("wtKick9"));
        Check("gdtx comments: the save path's layout finds the blocks where the loader does",
            layout.Assets.Select(a => (a.Name, (long)a.BodyStart, a.BodyLength)).SequenceEqual(entries.Select(e => (e.Name, e.BodyOffset, e.BodyLength))));
        var data = Apex.Render.Data.Gdt.GdtFile.Parse(commented, "x.gdtx");
        Check("gdtx comments: the data layer's GDT reader reads the same blocks and values",
            data.Select(d => d.Name).SequenceEqual(new[] { "ar_c_zm", "ar_d_zm" })
            && data[0].Fields.Count == 3 && data[0].Fields.All(kv => c.TryGetValue(kv.Key, out var v) && v == kv.Value));
        var junkBytes = GdtEncoding.GetBytes("\"a\" \"1\" / \"b\" \"2\" # \"c\" \"3\"");
        var junk = GdtParser.ParseProperties(junkBytes, 0, junkBytes.Length);
        Check("gdtx comments: only // is a comment; any other text still ends what Apex reads of a block",
            junk.Count == 1 && junk["a"] == "1");
    }

    // ═══ Saving around comments ═════════════════════════════════════════════

    private static void GdtxCommentSaveChecks()
    {
        SpliceResult Splice(string text, bool sidecar, Dictionary<string, string?> set)
        {
            var src = GdtEncoding.GetBytes(text);
            var a = GdtLayout.Scan(src).Assets[0];
            var edit = new AssetEdit { Disk = new DiskRef(a.Name, null, a.BodyStart), Name = a.Name, Type = a.TypeOrParent, Set = set };
            return GdtSplicer.Splice(src, new[] { edit }, sidecar);
        }
        string Out(SpliceResult r) => r.Bytes is null ? "(refused: " + string.Join("; ", r.Problems) + ")" : GdtEncoding.File.GetString(r.Bytes);

        // Out of key order and with comments: spliced in place (a sorted rewrite would drop the comments).
        const string block =
            "{\r\n\t\"ar_c_zm\" ( \"weapon-tech\" )\r\n\t{\r\n" +
            "\t\t// tuned by hand\r\n" +
            "\t\t\"wtEnabled\" \"1\"\r\n" +
            "\t\t\"wtKick3\" \"c\" // keep me\r\n" +
            "\t\t// rows below\r\n" +
            "\t\t\"wtKick1\" \"a\"\r\n" +
            "\t\t\"wtKick2\" \"b\"\r\n" +
            "\t}\r\n}\r\n";
        var r = Splice(block, true, new() { ["wtKick1"] = "A", ["wtKick3"] = null, ["wtKick2"] = null, ["wtKick4"] = "d" });
        const string want =
            "{\r\n\t\"ar_c_zm\" ( \"weapon-tech\" )\r\n\t{\r\n" +
            "\t\t// tuned by hand\r\n" +
            "\t\t\"wtEnabled\" \"1\"\r\n" +
            "\t\t// keep me\r\n" +
            "\t\t// rows below\r\n" +
            "\t\t\"wtKick1\" \"A\"\r\n" +
            "\t\t\"wtKick4\" \"d\"\r\n" +
            "\t}\r\n}\r\n";
        Check($"gdtx comments save: values change in place, removed keys go, a new key is added, every comment stays byte for byte ({Out(r).Replace("\r\n", "⏎")})",
            Out(r) == want);

        // The same block without comments is still put in row order (weapon-tech reads numbered keys in file order).
        var clean = block.Replace("\t\t// tuned by hand\r\n", "").Replace(" // keep me", "").Replace("\t\t// rows below\r\n", "");
        var sorted = Out(Splice(clean, true, new() { ["wtKick1"] = "A" }));
        Check($"gdtx comments save: a block with no comment is still rewritten in row order ({sorted.Replace("\r\n", "⏎")})",
            sorted.IndexOf("wtKick1", StringComparison.Ordinal) < sorted.IndexOf("wtKick3", StringComparison.Ordinal));

        // A comment between a key and its value: the value changes in place; removing the key would take the comment.
        const string split = "{\r\n\t\"ar_c_zm\" ( \"weapon-tech\" )\r\n\t{\r\n\t\t\"wtKick1\" // note\r\n\t\t\"a\"\r\n\t\t\"wtKick2\" \"b\"\r\n\t}\r\n}\r\n";
        var edited = Splice(split, true, new() { ["wtKick1"] = "A" });
        var removed = Splice(split, true, new() { ["wtKick1"] = null });
        Check($"gdtx comments save: a comment between a key and its value: the value is spliced, removing the key is refused in plain words ({Out(removed)})",
            Out(edited) == split.Replace("\"a\"", "\"A\"") && removed.Bytes is null
            && removed.Problems.SequenceEqual(new[] { "ar_c_zm: Apex can't remove wtKick1 without the comment between it and its value. Move the comment off that line in a text editor." }));
        var other = Splice(split, true, new() { ["wtKick1"] = "A", ["wtKick2"] = null, ["wtKick3"] = "c" });
        Check($"gdtx comments save: and the block's other keys still save ({Out(other).Replace("\r\n", "⏎")})",
            Out(other) == "{\r\n\t\"ar_c_zm\" ( \"weapon-tech\" )\r\n\t{\r\n\t\t\"wtKick1\" // note\r\n\t\t\"A\"\r\n\t\t\"wtKick3\" \"c\"\r\n\t}\r\n}\r\n");

        // Text that isn't a pair or a comment: refused, saying so.
        var junk = Splice("{\r\n\t\"ar_c_zm\" ( \"weapon-tech\" )\r\n\t{\r\n\t\t\"wtKick1\" \"a\"\r\n\t\t# note\r\n\t\t\"wtKick2\" \"b\"\r\n\t}\r\n}\r\n",
            true, new() { ["wtKick1"] = "A" });
        Check($"gdtx comments save: other text in a block is still refused, in plain words ('{string.Join("; ", junk.Problems)}')",
            junk.Bytes is null && junk.Problems.SequenceEqual(new[]
            {
                "ar_c_zm's weapon-tech values in the .gdtx hold text that isn't a \"key\" \"value\" pair or a // comment, so Apex can't change them safely. Edit that block in a text editor.",
            }));

        // A GDT reads and keeps a comment the same way.
        const string gdt = "{\r\n\t\"gun\" ( \"weapon.gdf\" )\r\n\t{\r\n\t\t// by hand\r\n\t\t\"damage\" \"10\"\r\n\t\t\"displayName\" \"Gun\"\r\n\t}\r\n}\r\n";
        var g = Out(Splice(gdt, false, new() { ["damage"] = "11", ["clipSize"] = "30" }));
        Check($"gdt comments save: a GDT asset with a // comment saves around it ({g.Replace("\r\n", "⏎")})",
            g == "{\r\n\t\"gun\" ( \"weapon.gdf\" )\r\n\t{\r\n\t\t// by hand\r\n\t\t\"clipSize\" \"30\"\r\n\t\t\"damage\" \"11\"\r\n\t\t\"displayName\" \"Gun\"\r\n\t}\r\n}\r\n");
    }

    // ═══ The manifest: hidden columns and a table among the fields ══════════

    private const string GrammarManifest = """
        { "apexSchema": 1, "id": "grammar", "version": "1", "targets": ["weapon"], "enabledBy": "grOn",
          "export": { "format": "ini-section", "header": "[g:{asset}]", "command": "Copy" },
          "sections": [ { "title": "Grammar",
            "fields": [
              { "key": "grOn", "kind": "toggle", "label": "On", "default": 1 },
              { "key": "grBefore", "kind": "number", "label": "Before the table", "default": 0 },
              { "key": "grAfter", "kind": "number", "label": "After the table", "default": 0 } ],
            "records": [
              { "key": "grTail#", "label": "Tail", "columns": [ { "name": "t", "kind": "text" } ] },
              { "key": "grRow#", "label": "Rows", "max": 8, "after": "grBefore",
                "columns": [ { "name": "a", "kind": "number", "label": "A", "default": 0, "integer": true },
                             { "name": "b", "kind": "number", "label": "B", "default": 0, "integer": true },
                             { "name": "scale", "kind": "number", "label": "Scale", "default": 1, "hidden": true } ] } ] } ] }
        """;

    private static string GrammarDir(string label)
    {
        var dir = NewScratch(label);
        Directory.CreateDirectory(Path.Combine(dir, "grammar"));
        File.WriteAllText(Path.Combine(dir, "grammar", ExtensionLoader.FileName), GrammarManifest);
        return dir;
    }

    private static void ManifestGrammarChecks()
    {
        SchemaRegistry.ResetToMock();
        var (loaded, said) = ExtensionLoader.LoadAll(GrammarDir("grammar-load"));
        var section = loaded.Single().Sections.Single();
        var rows = section.Records.First(r => r.Def.Key == "grRow#");
        Check($"manifest grammar: a hidden column is stored, read and checked, never shown ({string.Join(", ", rows.DisplayColumns.Select(c => c.Def.Label))}; {string.Join("; ", said)})",
            said.Count == 0 && rows.Columns.Count == 3 && rows.Columns[2] is { Hidden: true, Def.Default: "1" }
            && rows.DisplayColumns.Select(c => c.Def.Label).SequenceEqual(new[] { "A", "B" }));
        Check($"manifest grammar: after puts a table right after that field; the others follow the fields ({string.Join(", ", section.Items().Select(i => i.Field?.Def.Key ?? i.List!.Def.Key))})",
            section.Items().Select(i => i.Field?.Def.Key ?? i.List!.Def.Key).SequenceEqual(new[] { "grOn", "grBefore", "grRow#", "grAfter", "grTail#" }));

        var bad = """
            { "apexSchema": 1, "id": "badg", "version": "1", "targets": ["weapon"],
              "sections": [ { "title": "B", "fields": [ { "key": "bgA", "kind": "number" } ],
                "records": [ { "key": "bgRow#", "after": "bgNope",
                  "columns": [ { "name": "a", "kind": "toggle" }, { "name": "b", "kind": "toggle", "hidden": true },
                               { "name": "c", "kind": "toggle", "hidden": true, "default": 0 } ],
                  "combine": [ { "label": "AC", "columns": ["a", "c"], "choices": [ { "value": ["0", "0"], "label": "Off" } ] } ] } ] } ] }
            """;
        var badDir = NewScratch("grammar-bad");
        Directory.CreateDirectory(Path.Combine(badDir, "badg"));
        File.WriteAllText(Path.Combine(badDir, "badg", ExtensionLoader.FileName), bad);
        var (badLoaded, badSaid) = ExtensionLoader.LoadAll(badDir);
        var badList = badLoaded.Single().Sections.Single().Records.Single();
        var notes = badLoaded.Single().Notes.Concat(badSaid.Select(d => d.Message)).ToList();
        Check($"manifest grammar: mistakes never hide a stored value: hidden without a default shows, a combine can't take a hidden column, after naming no field of the section goes after the fields ({string.Join(" | ", notes)})",
            badList.DisplayColumns.Select(c => c.Def.Key).SequenceEqual(new[] { "a", "b" }) && badList.Combines.Count == 0
            && notes.Any(n => n.Contains("bgRow#.b is hidden but has no default to write, so it shows."))
            && notes.Any(n => n.Contains("names \"c\", a hidden column, so its columns show as they are."))
            && notes.Any(n => n.Contains("bgRow#'s after names bgNope, which isn't a field of section B, so the table goes after its fields."))
            && badLoaded.Single().Sections.Single().Items().Last().List == badList);

        // The editor on a temp GDT.
        var dir = NewScratch("grammar-editor");
        var gdtPath = Path.Combine(dir, "g.gdt");
        const string gdt = "{\r\n\t\"gun\" ( \"weapon.gdf\" )\r\n\t{\r\n\t\t\"displayName\" \"Gun\"\r\n\t}\r\n}\r\n";
        var gdtx = "{\r\n\t\"gun\" ( \"grammar\" )\r\n\t{\r\n\t\t\"grBefore\" \"2\"\r\n"
                   + "\t\t\"grRow1\" \"1,2,0.5\"\r\n\t\t\"grRow2\" \"3,4\"\r\n\t\t\"grTail01\" \"y\"\r\n\t\t\"grTail1\" \"x\"\r\n\t\t\"grTail3\" \"z\"\r\n\t}\r\n}\r\n";
        File.WriteAllBytes(gdtPath, GdtEncoding.GetBytes(gdt));
        File.WriteAllBytes(ExtensionSidecar.PathFor(gdtPath), GdtEncoding.GetBytes(gdtx));
        ExtensionRegistry.Load(GrammarDir("grammar-editor-ext"));
        var file = LoadGdt(gdtPath);
        var gun = file.Assets.Single();
        string Text(string p) => GdtEncoding.File.GetString(File.ReadAllBytes(p));
        void Save(string label)
        {
            var problems = new List<string>();
            var requests = GdtSavePlanner.Plan(new[] { file }, file.Assets, Array.Empty<AssetRecord>(), Array.Empty<GdtFile>(), null, problems);
            var result = NewSaveService(label).Save(requests);
            foreach (var f in result.Files)
                GdtSavePlanner.Commit(f);
            if (problems.Count > 0 || !result.AllSucceeded)
                Check($"manifest grammar: save ({string.Join("; ", problems)}{string.Join("; ", result.Files.Select(x => x.Message))})", false);
        }
        var tab = new AssetEditorViewModel(gun, (_, _) => { }, _ => { }, _ => { }, (_, n) => file.Assets.FirstOrDefault(a => a.Name == n), null, _ => file);
        var category = tab.RailItems.First(c => c.Name == "Grammar");
        Check($"manifest grammar editor: the form puts the table between the two fields ({string.Join(", ", category.All.Select(p => p.Key))})",
            category.All.Select(p => p.Key).SequenceEqual(new[] { "grOn", "grBefore", "grRow#", "grAfter", "grTail#" }));
        var table = (RecordsPropertyViewModel)category.All.First(p => p.Key == "grRow#");
        var tail = (RecordsPropertyViewModel)category.All.First(p => p.Key == "grTail#");
        table.Validate();
        Check($"manifest grammar editor: cells for the shown columns only; a stored row missing the hidden field says so ('{table.Rows[1].Problem}')",
            table.Rows.All(r => r.Cells.Count == 2) && table.Rows[0].Problem is null
            && table.Rows[1].Problem == "Scale is empty. The table doesn't show it; changing any cell of the row writes 1.");
        Check("manifest grammar editor: the filter doesn't find a table by a hidden column's name", !table.MatchesFilter("scale") && table.MatchesFilter("grRow"));

        // An edit elsewhere leaves both tables' keys as they are, gaps and the 01 spelling included.
        category.All.First(p => p.Key == "grAfter").RawValue = "5";
        Save("grammar-flat");
        Check("manifest grammar editor: a field's edit leaves every table as written (no renumbering, no reordering)",
            Text(ExtensionSidecar.PathFor(gdtPath)) == gdtx.Replace("\t\t\"grTail3\" \"z\"\r\n", "\t\t\"grTail3\" \"z\"\r\n\t\t\"grAfter\" \"5\"\r\n"));
        tab.RebaseChanges();

        // A cell edit: the row keeps its hidden value; a row missing it gets the default.
        table.Rows[0].Cells[0].RawValue = "7";
        table.Rows[1].Cells[1].RawValue = "9";
        Check($"manifest grammar editor: a cell edit keeps the row's hidden value; a row missing it gets the default ({table.RawValue.Replace("\n", "⏎")})",
            table.RawValue == "7,2,0.5\n3,9,1\n");
        // New rows: the default, also when copying a row whose hidden value differs.
        table.Insert(1);
        var added = Rec(table, 1);
        Check($"manifest grammar editor: a new row copies the row above but holds the hidden default ({added})", added == "7,2,1");
        table.Remove(table.Rows[1]);
        var empty = new RecordsPropertyViewModel(rows, "", null, null);
        empty.Insert(0);
        var first = Rec(empty, 0);
        Check($"manifest grammar editor: the first row is each column's default, the hidden one included ('{first}')",
            first == "0,0,1" && table.RawValue == "7,2,0.5\n3,9,1\n");
        // Paste: a pasted hidden value is kept; a pasted row without one gets the default.
        var (_, addedRows, _) = table.Paste(table.Rows.Count, RecordCodec.ParseRows(rows, "5\t6\t0.25\r\n8,9\r\n"));
        Check($"manifest grammar editor: pasted rows keep their hidden value, and get the default when they have none ({table.RawValue.Replace("\n", "⏎")})",
            addedRows == 2 && table.RawValue == "7,2,0.5\n3,9,1\n5,6,0.25\n8,9,1\n");
        Save("grammar-rows");
        var saved = Text(ExtensionSidecar.PathFor(gdtPath));
        Check($"manifest grammar editor: saved, the edited table is rows 1..N and the other table is untouched ({saved.Replace("\r\n", "⏎")})",
            saved.Contains("\"grRow1\" \"7,2,0.5\"") && saved.Contains("\"grRow4\" \"8,9,1\"")
            && saved.Contains("\"grTail01\" \"y\"\r\n\t\t\"grTail1\" \"x\"\r\n\t\t\"grTail3\" \"z\""));
        Check($"manifest grammar editor: the tail reads 1, 01, 3 ({tail.RawValue.Replace("\n", "⏎")})", tail.RawValue == "x\ny\nz\n");

        // Editing the tail renumbers it 1..N in that order and removes the other spellings.
        tab.RebaseChanges();
        tail.Rows[2].Cells[0].RawValue = "Z";
        Save("grammar-tail");
        saved = Text(ExtensionSidecar.PathFor(gdtPath));
        var reread = LoadGdt(gdtPath).Extensions!.Values("gun", "grammar");
        var tailKeys = RecordCodec.Rows(reread, PlainList("grTail")).Select(r => $"{r.Key}={r.Value}").ToList();
        Check($"manifest grammar editor: an edited table is written 1..N in the order it was read ({string.Join(" ", tailKeys)}; {saved.Replace("\r\n", "⏎")})",
            tailKeys.SequenceEqual(new[] { "grTail1=x", "grTail2=y", "grTail3=Z" }) && !saved.Contains("grTail01")
            && RecordCodec.Rows(reread, PlainList("grRow")).Select(r => r.Value).SequenceEqual(new[] { "7,2,0.5", "3,9,1", "5,6,0.25", "8,9,1" }));

        // The export follows the form's order.
        var export = tab.ExportText("grammar") ?? "";
        Check($"manifest grammar editor: the export follows the form's order ({export.Replace("\r\n", "⏎")})",
            export == "[g:gun]\r\ngrBefore = 2\r\ngrRow1 = 7,2,0.5\r\ngrRow2 = 3,9,1\r\ngrRow3 = 5,6,0.25\r\ngrRow4 = 8,9,1\r\ngrAfter = 5\r\ngrTail1 = x\r\ngrTail2 = y\r\ngrTail3 = Z\r\n");
        ExtensionRegistry.Clear();
    }

    /// <summary>
    /// The same manifest in the live app on a temp install (a copy of one install GDT), driven by real keys: the table sits
    /// between the two fields for ↓ as for the eye, shows no hidden column, and Ctrl+Enter, a step and Ctrl+V on + Add row
    /// write the hidden field as the rules say. Screenshots in both themes.
    /// </summary>
    private static void GrammarUiChecks(string outDir)
    {
        var install = NewScratch("grammar-install");
        CopyTree(Path.Combine(InstallRoot, "deffiles"), Path.Combine(install, "deffiles"));
        Directory.CreateDirectory(Path.Combine(install, "source_data"));
        var akPath = Path.Combine(install, @"source_data\ar_ak47_h1.gdt");
        File.Copy(Path.Combine(InstallRoot, @"source_data\ar_ak47_h1.gdt"), akPath);
        var ak = LoadGdt(akPath).Assets.First(a => a.Parent is null && a.Type == "bulletweapon").Name;
        File.WriteAllBytes(ExtensionSidecar.PathFor(akPath), GdtEncoding.GetBytes("{\r\n\t\"" + ak + "\" ( \"grammar\" )\r\n\t{\r\n"
            + "\t\t\"grRow1\" \"1,2,0.5\"\r\n\t\t\"grRow2\" \"3,4\"\r\n\t}\r\n}\r\n"));

        var saved = (Mock: Environment.GetEnvironmentVariable("APEX_FORCE_MOCK"), Root: Environment.GetEnvironmentVariable("APEX_BO3_ROOT"),
            Persist: Environment.GetEnvironmentVariable("APEX_NO_PERSIST"));
        Environment.SetEnvironmentVariable("APEX_FORCE_MOCK", null);
        Environment.SetEnvironmentVariable("APEX_BO3_ROOT", install);
        Environment.SetEnvironmentVariable("APEX_NO_PERSIST", "1");
        Environment.SetEnvironmentVariable(ExtensionRegistry.DirVariable, GrammarDir("grammar-ui"));
        MainViewModel? vm = null;
        Apex.Editor.Views.MainWindow? window = null;
        try
        {
            vm = new MainViewModel(Path.Combine(install, "session"));
            window = ShowJournalWindow(vm);
            window.Width = 1600;
            window.Height = 1000;
            WaitUntil(() => vm.Status.StartsWith("Loaded"), 60_000);
            Pump(100);
            if (vm.IsAlertOpen)
                vm.DismissAlertCommand.Execute(null);
            object? Focused() => window.FocusManager?.GetFocusedElement() is Avalonia.StyledElement e ? e.DataContext : null;
            void SetClipboard(string text) =>
                Avalonia.Input.Platform.ClipboardExtensions.SetValueAsync(window.Clipboard!, Avalonia.Input.DataFormat.Text, text).GetAwaiter().GetResult();

            vm.OpenByName(ak);
            Pump(300);
            var tab = vm.ActiveTab!;
            var rows = (RecordsPropertyViewModel)tab.AllSentinel.All.First(p => p.Key == "grRow#");
            var before = tab.AllSentinel.All.First(p => p.Key == "grBefore");
            var after = tab.AllSentinel.All.First(p => p.Key == "grAfter");
            tab.RevealProperty("grBefore");
            Pump(150);
            window.UpdateLayout();
            var order = new[] { before, rows, after }.Select(p => tab.FlatRows.IndexOf(p)).ToList();
            var table = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).OfType<Apex.Editor.Views.RecordTableEditor>().FirstOrDefault(t => t.DataContext == rows);
            var header = table is null ? new List<string?>() : Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(table).OfType<Avalonia.Controls.TextBlock>()
                .Where(t => t.Classes.Contains("rhead") && t.IsEffectivelyVisible).Select(t => t.Text).ToList();
            Check($"ui grammar: the table sits between the fields it was placed between, and its header has no hidden column ({string.Join(",", order)}; {string.Join(", ", header)})",
                order[0] >= 0 && order[0] < order[1] && order[1] < order[2] && header.SequenceEqual(new[] { "A", "B" }));
            Check($"ui grammar: a stored row missing the hidden field shows ⚠ ('{rows.Rows[1].Problem}')",
                rows.Rows[1].HasProblem && !rows.Rows[0].HasProblem);

            // ↓ from the field above walks into the table, down its rows, and out to the field below.
            var walk = new List<object?>();
            // Revealing the field put the keyboard in it.
            if (ReferenceEquals(Focused(), before))
            {
                for (var i = 0; i < 3; i++)
                {
                    Key(window, Avalonia.Input.Key.Down);
                    Pump(80);
                    walk.Add(Focused());
                }
            }
            Check($"ui grammar: ↓ goes from Before the table into row 1, row 2, then After the table ({string.Join(" → ", walk.Select(w => (w as PropertyItemViewModel)?.AccessibleName ?? (w as PropertyItemViewModel)?.Key ?? "?"))})",
                walk.Count == 3 && ReferenceEquals(walk[0], rows.Rows[0].Cells[0]) && ReferenceEquals(walk[1], rows.Rows[1].Cells[0])
                && ReferenceEquals(walk[2], after));
            Shot("grammar");

            // Ctrl+Enter in row 1 adds a copy below holding the hidden default, not row 1's 0.5.
            Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).OfType<ScrubNumberBox>()
                .FirstOrDefault(b => b.IsEffectivelyVisible && b.DataContext == rows.Rows[0].Cells[0])?.Focus(Avalonia.Input.NavigationMethod.Tab);
            Pump(50);
            Key(window, Avalonia.Input.Key.Enter, Avalonia.Input.RawInputModifiers.Control);
            Pump(100);
            Check($"ui grammar: Ctrl+Enter adds a copy of the row with the hidden field at its default ({rows.RawValue.Replace("\n", "⏎")})",
                rows.RawValue == "1,2,0.5\n1,2,1\n3,4\n" && ReferenceEquals(Focused(), rows.Rows[1].Cells[0]));
            Key(window, Avalonia.Input.Key.Z, Avalonia.Input.RawInputModifiers.Control);
            Pump(80);

            // Ctrl+↑ on B of the row missing the hidden field steps it and fills the field in.
            if (Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).OfType<ScrubNumberBox>().FirstOrDefault(b => b.IsEffectivelyVisible && b.DataContext == rows.Rows[1].Cells[1]) is { } b2)
            {
                b2.Focus(Avalonia.Input.NavigationMethod.Tab);
                Pump(50);
                Key(window, Avalonia.Input.Key.Up, Avalonia.Input.RawInputModifiers.Control);
                Pump(80);
            }
            Check($"ui grammar: a step on that row writes the hidden default and clears its ⚠ ({rows.RawValue.Replace("\n", "⏎")})",
                rows.RawValue == "1,2,0.5\n3,5,1\n" && !rows.Rows[1].HasProblem);

            // Ctrl+V on + Add row: a pasted hidden value is kept, a row without one gets the default.
            if (Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).OfType<Avalonia.Controls.Button>().FirstOrDefault(b => b.Name == "AddButton" && b.DataContext == rows) is { } add)
            {
                add.Focus(Avalonia.Input.NavigationMethod.Tab);
                Pump();
                SetClipboard("5\t6\t0.25\r\n8,9");
                Key(window, Avalonia.Input.Key.V, Avalonia.Input.RawInputModifiers.Control);
                Pump(100);
            }
            Check($"ui grammar: Ctrl+V on + Add row keeps a pasted hidden value and fills a missing one ({rows.RawValue.Replace("\n", "⏎")})",
                rows.RawValue == "1,2,0.5\n3,5,1\n5,6,0.25\n8,9,1\n");

            void Shot(string name)
            {
                foreach (var (theme, suffix) in new[] { (Avalonia.Styling.ThemeVariant.Dark, "dark"), (Avalonia.Styling.ThemeVariant.Light, "light") })
                {
                    Avalonia.Application.Current!.RequestedThemeVariant = theme;
                    Pump(60);
                    Capture(window!, Path.Combine(outDir, $"103-{name}-{suffix}.png"));
                }
                Avalonia.Application.Current!.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
                Pump(60);
            }
        }
        catch (Exception ex)
        {
            Check($"grammar ui: {ex}", false);
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
