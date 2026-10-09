using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Apex.Editor.Models;
using Apex.Editor.Services.Gdt;
using Apex.Editor.Services.Save;
using GdtEncoding = Apex.Render.Data.Gdt.GdtEncoding;

namespace Apex.Shots;

/// <summary>
/// Extension data in <c>.gdtx</c> sidecars, through the same save path as GDTs, on temp files only: the header form,
/// byte-exact splices, a file only while there is data for it, orphans kept, conflicts and multi-file saves.
/// </summary>
public partial class Program
{
    private const string WeaponGdt =
        "{\r\n" +
        "\t\"ar_foo_zm\" ( \"weapon.gdf\" )\r\n\t{\r\n\t\t\"displayName\" \"Foo\"\r\n\t\t\"weaponClass\" \"rifle\"\r\n\t}\r\n" +
        "\t\"ar_bar_zm\" ( \"weapon.gdf\" )\r\n\t{\r\n\t\t\"displayName\" \"Bar\"\r\n\t}\r\n" +
        "}\r\n";

    // Two extension ids on one asset, an orphan (no such asset in the GDT) and a raw backslash value.
    private const string WeaponGdtx =
        "{\r\n" +
        "\t\"ar_foo_zm\" ( \"weapon-tech\" )\r\n\t{\r\n\t\t\"wtEnabled\" \"1\"\r\n\t\t\"wtKick1\" \"0.5\"\r\n\t}\r\n" +
        "\t\"ar_foo_zm\" ( \"other-ext\" )\r\n\t{\r\n\t\t\"oxPath\" \"a\\\\b\\c\"\r\n\t}\r\n" +
        "\t\"ghost_gun\" ( \"weapon-tech\" )\r\n\t{\r\n\t\t\"wtKick1\" \"0.9\"\r\n\t}\r\n" +
        "}\r\n";

    private static void ExtensionChecks()
    {
        var dir = NewScratch("gdtx");
        var service = NewSaveService("gdtx");
        var n = 0;
        string Text(string p) => GdtEncoding.File.GetString(File.ReadAllBytes(p));
        (GdtFile Gdt, string Path, string Sidecar) Setup(string? gdtx = WeaponGdtx, string gdt = WeaponGdt)
        {
            var p = Path.Combine(dir, $"w{n++}.gdt");
            File.WriteAllBytes(p, GdtEncoding.GetBytes(gdt));
            if (gdtx is not null)
                File.WriteAllBytes(ExtensionSidecar.PathFor(p), GdtEncoding.GetBytes(gdtx));
            return (LoadGdt(p), p, ExtensionSidecar.PathFor(p));
        }
        GdtSaveResult Save(GdtFile g, GdtSaveService? svc = null)
        {
            var problems = new List<string>();
            var requests = GdtSavePlanner.Plan(new[] { g }, g.Assets, Array.Empty<AssetRecord>(), Array.Empty<GdtFile>(), null, problems);
            if (problems.Count > 0)
                throw new InvalidOperationException(string.Join("; ", problems));
            var result = (svc ?? service).Save(requests);
            foreach (var f in result.Files)
                GdtSavePlanner.Commit(f);
            return result;
        }

        // ── Reading ─────────────────────────────────────────────────────────
        {
            var entries = GdtIndexer.Index(GdtEncoding.GetBytes(WeaponGdtx));
            var layout = GdtLayout.Scan(GdtEncoding.GetBytes(WeaponGdtx));
            Check("gdtx: the header ( \"weapon-tech\" ) indexes as a root block tagged weapon-tech, never as a parent",
                entries.Count == 3 && entries.All(e => !e.IsDerived) && entries[0].TypeOrParent == "weapon-tech" && entries[1].TypeOrParent == "other-ext"
                && layout.Assets.Select(a => (a.TypeOrParent, a.IsDerived)).SequenceEqual(entries.Select(e => (e.TypeOrParent, false))));

            var (g, p, _) = Setup();
            var x = g.Extensions;
            Check("gdtx: a .gdtx is never listed as a GDT (*.gdt doesn't match it)",
                Directory.EnumerateFiles(dir, "*.gdt", new EnumerationOptions { MatchType = MatchType.Win32 }).All(f => f.EndsWith(".gdt")));
            Check("gdtx: loading a GDT loads its sidecar's blocks without reading their values",
                x is not null && x.Blocks.Count == 3 && x.Blocks.All(b => !b.IsMaterialized) && x.File.Stamp is not null
                && g.Assets.Count == 2 && g.Assets.All(a => a.Parent is null));
            Check("gdtx: values by asset and extension id, raw (backslashes literal)",
                x!.Get("ar_foo_zm", "weapon-tech", "wtKick1") == "0.5" && x.Get("AR_FOO_ZM", "other-ext", "oxPath") == "a\\\\b\\c"
                && x.Values("ar_bar_zm", "weapon-tech").Count == 0 && x.Find("ar_foo_zm", "weapon-tech")!.Properties.Count == 2);
            Check("gdtx: reading one block reads only that block", x.Blocks.Count(b => b.IsMaterialized) == 2);
            Check("gdtx: an orphan block (its asset isn't in the GDT) is listed",
                x.Orphans().Select(b => b.Name).SequenceEqual(new[] { "ghost_gun" }));

            var none = Setup(gdtx: null).Gdt;
            Check("gdtx: a GDT without a sidecar has no extension data", none.Extensions is null);
        }

        // ── Round trip ──────────────────────────────────────────────────────
        {
            var (g, p, s) = Setup();
            _ = g.Extensions!.Values("ar_foo_zm", "weapon-tech");
            var nothing = GdtSavePlanner.Plan(new[] { g }, g.Assets, Array.Empty<AssetRecord>(), Array.Empty<GdtFile>(), null, new List<string>());
            var forced = new GdtSaveService(new BackupStore(Path.Combine(SaveRoot, "backups", "gdtx-noop"))) { WriteEvenIfUnchanged = true };
            var r = forced.Save(new[] { new GdtSaveRequest { Path = s, Expected = g.Extensions.File.Stamp, Sidecar = true, Tag = g.Extensions } }).Files[0];
            Check($"gdtx: read but not edited → nothing to save; a forced no-op save writes the same bytes ({r.Status})",
                nothing.Count == 0 && r.Status == GdtSaveStatus.Saved && Text(s) == WeaponGdtx && Text(p) == WeaponGdt);
        }

        // ── Only when needed ────────────────────────────────────────────────
        {
            var (g, p, s) = Setup(gdtx: null);
            var x = ExtensionSidecar.Of(g);
            x.Set("ar_foo_zm", "weapon-tech", "wtKick1", "0.5");
            x.Set("ar_foo_zm", "weapon-tech", "wtKick1", null);
            x.Set("ar_bar_zm", "weapon-tech", "wtKick2", "0", defaultValue: "0.0");
            var r = Save(g);
            Check($"gdtx: no file when nothing is set (set then cleared, or set to the default) ({r.Files.Count} files planned)",
                r.Files.Count == 0 && !File.Exists(s) && x.Blocks.Count == 0);

            x.Set("ar_foo_zm", "weapon-tech", "wtKick1", "0.5");
            x.Set("ar_foo_zm", "weapon-tech", "wtEnabled", "1");
            x.Set("ar_foo_zm", "weapon-tech", "wtCurve", "curves\\kick.graph");
            var r2 = Save(g);
            const string created = "{\r\n\t\"ar_foo_zm\" ( \"weapon-tech\" )\r\n\t{\r\n\t\t\"wtCurve\" \"curves\\kick.graph\"\r\n\t\t\"wtEnabled\" \"1\"\r\n\t\t\"wtKick1\" \"0.5\"\r\n\t}\r\n}\r\n";
            Check($"gdtx: the first value creates the sidecar: CRLF, tabs, keys in APE's order, the id where the gdf goes ({r2.Files.Single().Status})",
                File.Exists(s) && Text(s) == created && Text(p) == WeaponGdt && !File.ReadAllBytes(s).AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }));
            Check("gdtx: after creating it the block is bound to the file and no backup was made of a file that didn't exist",
                x.File.FullPath == s && x.File.Stamp is not null && x.Blocks.Single().Disk is not null && !x.Blocks.Single().HasSessionEdits
                && r2.Files[0].BackupPath is null);
            Check("gdtx: a core (deffile) key is refused",
                Throws<InvalidOperationException>(() => x.Set("ar_foo_zm", "weapon-tech", "displayName", "x")) && Text(s) == created);
            EditHistory.Set(x.Find("ar_foo_zm", "weapon-tech")!, "weaponClass", "smg");
            var problems = new List<string>();
            GdtSavePlanner.Plan(new[] { g }, g.Assets, Array.Empty<AssetRecord>(), Array.Empty<GdtFile>(), null, problems);
            Check($"gdtx: a core key that reached a block anyway is refused by the planner ('{problems.FirstOrDefault()}')", problems.Count == 1);
        }

        // ── Splice: one block changes, every other byte stays ───────────────
        {
            var (g, p, s) = Setup();
            var x = g.Extensions!;
            x.Set("ar_foo_zm", "weapon-tech", "wtKick1", "0.75");
            x.Set("ar_bar_zm", "weapon-tech", "wtEnabled", "1");
            var r = Save(g);
            Check($"gdtx: an edit changes only its value; a new block is appended; other ids and the orphan keep their bytes ({r.Files.Single().Status})",
                Text(s) == WeaponGdtx.Replace("\"wtKick1\" \"0.5\"", "\"wtKick1\" \"0.75\"")[..^3]
                    + "\t\"ar_bar_zm\" ( \"weapon-tech\" )\r\n\t{\r\n\t\t\"wtEnabled\" \"1\"\r\n\t}\r\n}\r\n"
                && Text(p) == WeaponGdt && x.Orphans().Single().Name == "ghost_gun");

            const string odd = "{\n  \"ar_foo_zm\" ( \"weapon-tech\" )\n  {\n    \"wtKick1\" \"1\"\n  }\n  \"ar_foo_zm\" [ \"strange\" ]\n  {\n  }\n}";
            var (g2, _, s2) = Setup(odd);
            g2.Extensions!.Set("ar_foo_zm", "weapon-tech", "wtKick1", "2");
            Save(g2);
            Check("gdtx: an LF sidecar with spaces, no final newline and content Apex doesn't write keeps all of it",
                Text(s2) == odd.Replace("\"wtKick1\" \"1\"", "\"wtKick1\" \"2\""));
        }

        // ── Clearing: the block goes, then the file (backed up) ─────────────
        {
            var (g, p, s) = Setup();
            var x = g.Extensions!;
            x.Set("ar_foo_zm", "other-ext", "oxPath", null);
            Save(g);
            var afterBlock = WeaponGdtx.Replace("\t\"ar_foo_zm\" ( \"other-ext\" )\r\n\t{\r\n\t\t\"oxPath\" \"a\\\\b\\c\"\r\n\t}\r\n", "");
            Check("gdtx: clearing a block's last value removes exactly its block", Text(s) == afterBlock && x.Find("ar_foo_zm", "other-ext") is null);

            x.Set("ar_foo_zm", "weapon-tech", "wtEnabled", null);
            x.Set("ar_foo_zm", "weapon-tech", "wtKick1", null);
            x.Set("ghost_gun", "weapon-tech", "wtKick1", null);
            var store = new BackupStore(Path.Combine(SaveRoot, "backups", "gdtx-clear"));
            var r = Save(g, new GdtSaveService(store)).Files.Single();
            var backups = store.List(s);
            Check($"gdtx: clearing the last block removes the file, after backing it up ({r.Status}, '{r.Message}')",
                r.Status == GdtSaveStatus.Saved && r.Removed && !File.Exists(s) && !File.Exists(s + GdtSaveService.TempSuffix)
                && backups.Count == 1 && Text(backups[0]) == afterBlock && backups[0].EndsWith(ExtensionSidecar.Extension)
                && Path.GetFileName(Path.GetDirectoryName(backups[0])!).StartsWith(Path.GetFileName(s) + "-")
                && g.Extensions is null && Text(p) == WeaponGdt);

            ExtensionSidecar.Of(g).Set("ar_bar_zm", "weapon-tech", "wtEnabled", "1");
            Save(g);
            Check("gdtx: a value set after the file was removed creates it again",
                Text(s) == "{\r\n\t\"ar_bar_zm\" ( \"weapon-tech\" )\r\n\t{\r\n\t\t\"wtEnabled\" \"1\"\r\n\t}\r\n}\r\n");

            // Removing never deletes what another program wrote meanwhile.
            var (g2, _, s2) = Setup("{\r\n\t\"ar_foo_zm\" ( \"weapon-tech\" )\r\n\t{\r\n\t\t\"wtKick1\" \"1\"\r\n\t}\r\n}\r\n");
            g2.Extensions!.Set("ar_foo_zm", "weapon-tech", "wtKick1", null);
            var svc = NewSaveService("gdtx-clear");
            svc.Hooks.AfterSwap = path => File.WriteAllText(path, "{\r\n}\r\n");
            var r2 = Save(g2, svc).Files.Single();
            Check($"gdtx: a write in the removal's swap window is kept, and reported ('{r2.Message}')",
                r2.Status == GdtSaveStatus.Failed && !r2.Removed && File.Exists(s2) && Text(s2) == "{\r\n}\r\n" && r2.BackupPath is not null);
        }

        // ── Orphans are kept ────────────────────────────────────────────────
        {
            var (g, _, s) = Setup();
            g.Extensions!.Set("ar_foo_zm", "weapon-tech", "wtKick1", "0.6");
            var orphanBlock = "\t\"ghost_gun\" ( \"weapon-tech\" )\r\n\t{\r\n\t\t\"wtKick1\" \"0.9\"\r\n\t}\r\n";
            Save(g);
            var reloaded = LoadGdt(Path.ChangeExtension(s, ".gdt"));
            Check("gdtx: saves keep an orphan's bytes and it is still listed after a reload",
                Text(s).Contains(orphanBlock) && reloaded.Extensions!.Orphans().Single().Name == "ghost_gun"
                && reloaded.Extensions.Get("ghost_gun", "weapon-tech", "wtKick1") == "0.9");
        }

        // ── Conflicts, as for a GDT ─────────────────────────────────────────
        {
            var (g, _, s) = Setup();
            g.Extensions!.Set("ar_foo_zm", "weapon-tech", "wtKick1", "0.7");
            var external = WeaponGdtx.Replace("\"wtKick1\" \"0.5\"", "\"wtKick1\" \"0.6\"");
            File.WriteAllText(s, external);
            var r = Save(g).Files.Single();
            var c = r.Conflicts.FirstOrDefault();
            Check($"gdtx: changed on disk since load → conflict listing ar_foo_zm · wtKick1 (yours 0.7, disk 0.6), file untouched ('{r.Message}')",
                r.Status == GdtSaveStatus.Conflict && r.ChangedOnDisk && c is { Kind: AssetConflictKind.ValuesChanged, Asset: "ar_foo_zm" }
                && c.Keys.Single() is { Key: "wtKick1", Was: "0.5", OnDisk: "0.6", Mine: "0.7" } && Text(s) == external);

            var (g2, _, s2) = Setup();
            g2.Extensions!.Set("ar_foo_zm", "weapon-tech", "wtKick1", "0.7");
            var elsewhere = WeaponGdtx.Replace("\"wtKick1\" \"0.9\"", "\"wtKick1\" \"0.95\"");
            File.WriteAllText(s2, elsewhere);
            var r2 = Save(g2).Files.Single();
            GdtLoader.ApplySidecar(g2, s2, GdtIndexer.IndexFile(s2, out var stamp), stamp);
            var r3 = Save(g2).Files.Single();
            Check("gdtx: a change elsewhere is a conflict with nothing overlapping; after a reload the edit is kept and saving keeps both",
                r2.Status == GdtSaveStatus.Conflict && r2.Conflicts.Count == 0 && r3.Status == GdtSaveStatus.Saved
                && Text(s2) == elsewhere.Replace("\"wtKick1\" \"0.5\"", "\"wtKick1\" \"0.7\"")
                && g2.Extensions.Get("ghost_gun", "weapon-tech", "wtKick1") == "0.95");

            var (g3, _, s3) = Setup();
            g3.Extensions!.Set("ar_foo_zm", "weapon-tech", "wtKick1", "0.7");
            File.Delete(s3);
            var r4 = Save(g3).Files.Single();
            Check($"gdtx: deleted on disk since load → conflict, not recreated ('{r4.Message}')", r4.Status == GdtSaveStatus.Conflict && !File.Exists(s3));

            var (g4, _, s4) = Setup(gdtx: null);
            ExtensionSidecar.Of(g4).Set("ar_foo_zm", "weapon-tech", "wtKick1", "1");
            File.WriteAllText(s4, WeaponGdtx);
            var r5 = Save(g4).Files.Single();
            Check($"gdtx: a sidecar another program created meanwhile is never overwritten ('{r5.Message}')",
                r5.Status == GdtSaveStatus.Conflict && Text(s4) == WeaponGdtx);
            GdtLoader.ApplySidecar(g4, s4, GdtIndexer.IndexFile(s4, out var stamp4), stamp4);
            var r6 = Save(g4).Files.Single();
            Check("gdtx: once it is read, the session's block is that block and its value is a conflict to choose, not a duplicate",
                g4.Extensions!.Blocks.Count == 3 && r6.Status == GdtSaveStatus.Conflict
                && r6.Conflicts.Single() is { Kind: AssetConflictKind.ValuesChanged } c6 && c6.Keys.Any(k => k is { Key: "wtKick1", OnDisk: "0.5", Mine: "1" }));
        }

        // ── One save, two files ─────────────────────────────────────────────
        {
            var (g, p, s) = Setup();
            Edit(g.Assets.First(a => a.Name == "ar_bar_zm"), "displayName", "Bar II");
            g.Extensions!.Set("ar_bar_zm", "weapon-tech", "wtEnabled", "1");
            var svc = NewSaveService("gdtx-multi");
            var stagedAtFirstSwap = false;
            svc.Hooks.BeforeSwap = path => stagedAtFirstSwap |= File.Exists(p + GdtSaveService.TempSuffix) && File.Exists(s + GdtSaveService.TempSuffix);
            var r = Save(g, svc);
            Check($"gdtx: a GDT and its sidecar changed together are one save, both staged before either swaps ({string.Join(", ", r.Files.Select(f => f.Status))})",
                r.Files.Count == 2 && r.AllSucceeded && stagedAtFirstSwap && Text(p) == WeaponGdt.Replace("\"Bar\"", "\"Bar II\"")
                && Text(s).EndsWith("\t\"ar_bar_zm\" ( \"weapon-tech\" )\r\n\t{\r\n\t\t\"wtEnabled\" \"1\"\r\n\t}\r\n}\r\n"));

            var (g2, p2, s2) = Setup();
            Edit(g2.Assets.First(a => a.Name == "ar_bar_zm"), "displayName", "Bar II");
            g2.Extensions!.Set("ar_bar_zm", "weapon-tech", "wtEnabled", "1");
            GdtSaveResult r2;
            using (new FileStream(s2, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
                r2 = Save(g2, NewSaveService("gdtx-multi"));
            Check($"gdtx: the sidecar locked → neither file written ({string.Join(", ", r2.Files.Select(f => f.Status))})",
                r2.Files.Count == 2 && r2.Files[0].Status == GdtSaveStatus.Locked && r2.Files[1].Status == GdtSaveStatus.NotWritten
                && Text(p2) == WeaponGdt && Text(s2) == WeaponGdtx);
        }

        // ── Reload keeps edits ──────────────────────────────────────────────
        {
            var (g, _, s) = Setup();
            var x = g.Extensions!;
            x.Set("ar_foo_zm", "weapon-tech", "wtKick1", "0.8");
            _ = x.Values("ar_foo_zm", "other-ext");
            File.WriteAllText(s, WeaponGdtx.Replace("\"oxPath\" \"a\\\\b\\c\"", "\"oxPath\" \"d\"").Replace("\t\"ghost_gun\" ( \"weapon-tech\" )\r\n\t{\r\n\t\t\"wtKick1\" \"0.9\"\r\n\t}\r\n", ""));
            GdtLoader.ApplySidecar(g, s, GdtIndexer.IndexFile(s, out var stamp), stamp);
            Check("gdtx: a reload keeps an edited block's edit, reads a seen block as the file now and drops a vanished one",
                x.Get("ar_foo_zm", "weapon-tech", "wtKick1") == "0.8" && x.Get("ar_foo_zm", "other-ext", "oxPath") == "d"
                && x.Find("ar_foo_zm", "other-ext")!.CountSessionChanges() == 0 && x.Find("ghost_gun", "weapon-tech") is null && x.Blocks.Count == 2);
        }
    }
}
