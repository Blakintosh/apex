using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
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
/// Extension data where a save, a reload or an undo goes wrong halfway: a save that writes one file of two, a restart
/// after it, undo across a move, names an orphan block holds, a reload while a delete is pending, a killed removal, a
/// GDT arriving after its sidecar. Each case was first reproduced failing (an adversarial review); temp files only.
/// </summary>
public partial class Program
{
    private static void RunHardeningChecks()
    {
        HardeningModel();
        try { HardeningApp(); }
        catch (Exception ex) { Check("hardening app: " + ex, false); }
    }

    private static void HardeningModel()
    {
        var dir = NewScratch("hardening");
        var n = 0;
        string Text(string p) => File.Exists(p) ? GdtEncoding.File.GetString(File.ReadAllBytes(p)) : "(no file)";
        byte[] B(string s) => GdtEncoding.GetBytes(s);
        byte[] Bytes(string p) => File.Exists(p) ? File.ReadAllBytes(p) : Array.Empty<byte>();
        (GdtFile Gdt, string Path, string Sidecar) Setup(byte[] gdtx, string gdt = WeaponGdt)
        {
            var p = Path.Combine(dir, $"h{n++}.gdt");
            File.WriteAllBytes(p, GdtEncoding.GetBytes(gdt));
            File.WriteAllBytes(ExtensionSidecar.PathFor(p), gdtx);
            return (LoadGdt(p), p, ExtensionSidecar.PathFor(p));
        }
        GdtSaveResult Save(GdtFile g, GdtSaveService? svc = null, IEnumerable<AssetRecord>? deleted = null)
        {
            var problems = new List<string>();
            var requests = GdtSavePlanner.Plan(new[] { g }, g.Assets, deleted ?? Array.Empty<AssetRecord>(), Array.Empty<GdtFile>(), null, problems);
            if (problems.Count > 0)
                throw new InvalidOperationException(string.Join("; ", problems));
            var result = (svc ?? NewSaveService("hardening")).Save(requests);
            foreach (var f in result.Files)
                GdtSavePlanner.Commit(f);
            return result;
        }
        string Statuses(GdtSaveResult r) => string.Join(", ", r.Files.Select(f => $"{f.Request.DisplayName} {f.Status}"));

        // ── A block of an orphan's name: the asset's own block wins ─────────
        {
            var (g, _, _) = Setup(B(WeaponGdtx.Replace("ghost_gun", "ar_foo_zm_copy")));
            var x = g.Extensions!;
            var clone = new AssetRecord { Name = "ar_foo_zm_copy", Type = "weapon", GdtName = g.Name };
            clone.CaptureBaseline();
            g.Assets.Add(clone);
            x.Add(clone.Name, ExtensionSidecar.ValuesOf(g, "ar_foo_zm")!);
            Check($"hardening: a copy whose name an orphan block has reads its own values, not the orphan's ({x.Get(clone.Name, "weapon-tech", "wtKick1")})",
                x.Get(clone.Name, "weapon-tech", "wtKick1") == "0.5");
        }
        {
            var (g, _, s) = Setup(B(WeaponGdtx));
            var x = g.Extensions!;
            x.Set("ar_bar_zm", "weapon-tech", "wtEnabled", "1");
            Save(g);
            var bar = g.Assets.First(a => a.Name == "ar_bar_zm");
            bar.CaptureBaseline();
            bar.Name = "ghost_gun";
            x.Rename("ar_bar_zm", "ghost_gun");
            var shown = x.Get("ghost_gun", "weapon-tech", "wtEnabled");
            var r = Save(g);
            Check($"hardening: renaming onto an orphan's name replaces the orphan: the asset reads its own block and the save goes through ({Statuses(r)})",
                shown == "1" && r.AllSucceeded && !Text(s).Contains("\"wtKick1\" \"0.9\"") && Text(s).Contains("\"ghost_gun\" ( \"weapon-tech\" )")
                && x.BlocksOf("ghost_gun").Count == 1 && LoadGdt(Path.ChangeExtension(s, ".gdt")).Extensions!.Orphans().All(o => o.Name != "ghost_gun"));
        }

        // ── A block with a key written twice isn't sorted (that would drop a line) ──
        {
            var gdtx = "{\r\n\t\"ar_foo_zm\" ( \"weapon-tech\" )\r\n\t{\r\n\t\t\"wtKick2\" \"b\"\r\n\t\t\"wtKick1\" \"a\"\r\n\t\t\"wtKick2\" \"c\"\r\n\t}\r\n}\r\n";
            var (g, _, s) = Setup(B(gdtx));
            g.Extensions!.Set("ar_foo_zm", "weapon-tech", "wtKick3", "d");
            Save(g);
            Check("hardening: a block holding a key twice keeps every line when a numbered key is added",
                Text(s) == gdtx.Replace("\t\t\"wtKick2\" \"c\"\r\n", "\t\t\"wtKick2\" \"c\"\r\n\t\t\"wtKick3\" \"d\"\r\n"));
        }

        // ── A sorted rewrite keeps an untouched value's bytes ───────────────
        {
            var head = B("{\r\n\t\"ar_foo_zm\" ( \"weapon-tech\" )\r\n\t{\r\n\t\t\"wtKick2\" \"x\"\r\n\t\t\"wtName\" \"");
            var odd = new byte[] { 0x81, 0x8D, 0x8F, 0x90, 0x9D, 0xE9, 0xC3, 0xA9 };
            var tail = B("\"\r\n\t\t\"wtKick1\" \"a\"\r\n\t}\r\n}\r\n");
            var (g, _, s) = Setup(head.Concat(odd).Concat(tail).ToArray());
            g.Extensions!.Set("ar_foo_zm", "weapon-tech", "wtKick3", "d");
            Save(g);
            Check("hardening: a sorted rewrite keeps the bytes of a value it didn't change", Bytes(s).AsSpan().IndexOf(odd) >= 0);
        }

        // ── A removal killed after the move aside: the next load puts the file back ──
        {
            var (g, p, s) = Setup(B("{\r\n\t\"ar_foo_zm\" ( \"weapon-tech\" )\r\n\t{\r\n\t\t\"wtKick1\" \"1\"\r\n\t}\r\n}\r\n"));
            var before = File.ReadAllBytes(s);
            g.Extensions!.Set("ar_foo_zm", "weapon-tech", "wtKick1", null);
            var svc = NewSaveService("hardening-kill");
            svc.Hooks.AfterSwap = _ => throw new InvalidOperationException("killed");
            try { Save(g, svc); } catch (InvalidOperationException) { }
            var killedState = !File.Exists(s) && File.Exists(s + GdtSaveService.TempSuffix);
            var reloaded = LoadGdt(p);
            Check("hardening: a .gdtx left only as .apex-tmp by a killed removal is put back on load and read",
                killedState && File.Exists(s) && !File.Exists(s + GdtSaveService.TempSuffix) && Bytes(s).AsSpan().SequenceEqual(before)
                && reloaded.Extensions?.Get("ar_foo_zm", "weapon-tech", "wtKick1") == "1");

            File.WriteAllText(s + GdtSaveService.TempSuffix, "{\r\n}\r\n");
            var both = LoadGdt(p);
            Check("hardening: with both the .gdtx and a leftover .apex-tmp, the .gdtx is kept and read, the temp file left for the next save",
                Bytes(s).AsSpan().SequenceEqual(before) && File.Exists(s + GdtSaveService.TempSuffix)
                && both.Extensions?.Get("ar_foo_zm", "weapon-tech", "wtKick1") == "1");
        }

        // ── A .gdtx reloaded while a delete is pending ──────────────────────
        {
            var (g, _, s) = Setup(B(WeaponGdtx));
            var x = g.Extensions!;
            _ = x.Values("ar_foo_zm", "weapon-tech");
            _ = x.Values("ar_foo_zm", "other-ext");
            var foo = g.Assets.First(a => a.Name == "ar_foo_zm");
            foo.CaptureBaseline();
            var taken = x.Detach("ar_foo_zm");
            g.Assets.Remove(foo);
            File.WriteAllText(s, WeaponGdtx.Replace("\"0.9\"", "\"0.95\""));
            GdtLoader.ApplySidecar(g, s, GdtIndexer.IndexFile(s, out var st), st);
            var back = x.BlocksOf("ar_foo_zm").Count;
            x.Restore(taken);
            var undone = x.BlocksOf("ar_foo_zm");
            Check($"hardening: a reload during a pending delete doesn't bring the deleted blocks back, and undoing the delete restores exactly them ({back} back, {undone.Count} after undo)",
                back == 0 && x.Removed.Count == 0 && undone.Count == 2 && undone.All(b => taken.Contains(b))
                && x.Get("ar_foo_zm", "weapon-tech", "wtKick1") == "0.5");

            x.Detach("ar_foo_zm");
            var r = Save(g, deleted: new[] { foo });
            Check($"hardening: the delete saved after that reload removes the blocks from the file and keeps the other's change ({Statuses(r)})",
                r.AllSucceeded && !Text(s).Contains("ar_foo_zm") && Text(s).Contains("\"0.95\"") && x.Removed.Count == 0);

            var (g2, _, s2) = Setup(B(WeaponGdtx));
            var x2 = g2.Extensions!;
            _ = x2.Values("ar_foo_zm", "weapon-tech");
            _ = x2.Values("ar_foo_zm", "other-ext");
            var foo2 = g2.Assets.First(a => a.Name == "ar_foo_zm");
            foo2.CaptureBaseline();
            x2.Detach("ar_foo_zm");
            g2.Assets.Remove(foo2);
            var changed = WeaponGdtx.Replace("\"0.5\"", "\"0.55\"");
            File.WriteAllText(s2, changed);
            GdtLoader.ApplySidecar(g2, s2, GdtIndexer.IndexFile(s2, out var st2), st2);
            var r2 = Save(g2, deleted: new[] { foo2 });
            Check($"hardening: a deleted block changed on disk by that reload is a conflict, not deleted unseen ({Statuses(r2)})",
                r2.Files.Any(f => f.Request.Sidecar && f.Status == GdtSaveStatus.Conflict
                    && f.Conflicts.Any(c => c.Kind == AssetConflictKind.ChangedBeforeDelete)) && Text(s2) == changed);
        }

        // ── One save, two files, the second can't be replaced ───────────────
        {
            var (g, p, s) = Setup(B(WeaponGdtx));
            var x = g.Extensions!;
            _ = x.Values("ar_foo_zm", "weapon-tech");
            var foo = g.Assets.First(a => a.Name == "ar_foo_zm");
            foo.CaptureBaseline();
            foo.Name = "ar_foo2_zm";
            x.Rename("ar_foo_zm", "ar_foo2_zm");
            var svc = NewSaveService("hardening-partial");
            FileStream? reader = null;
            // A reader without delete sharing (an editor, an indexer) makes File.Replace fail after the lock let go.
            svc.Hooks.BeforeSwap = path => { if (path == s) reader ??= new FileStream(s, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); };
            var r = Save(g, svc);
            reader?.Dispose();
            Check($"hardening: the sidecar can't be replaced → its GDT isn't either, so the two never disagree on disk ({Statuses(r)})",
                Text(p) == WeaponGdt && Text(s) == WeaponGdtx);

            var svc2 = NewSaveService("hardening-partial");
            FileStream? reader2 = null;
            svc2.Hooks.BeforeSwap = path => { if (path == p) reader2 ??= new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); };
            var r2 = Save(g, svc2);
            reader2?.Dispose();
            Check($"hardening: extension data is replaced before its GDT: the GDT locked → the .gdtx saved alone, the GDT's rename still pending ({Statuses(r2)})",
                r2.Files[0].Request.Sidecar && r2.Files[0].Status == GdtSaveStatus.Saved && Text(p) == WeaponGdt
                && Text(s).Contains("\"ar_foo2_zm\" ( \"weapon-tech\" )") && foo.Disk!.Value.Name == "ar_foo_zm"
                && x.Find("ar_foo2_zm", "weapon-tech")!.Disk!.Value.Name == "ar_foo2_zm");
            var r3 = Save(g);
            Check($"hardening: saving again then writes the GDT, and the two agree ({Statuses(r3)})",
                r3.AllSucceeded && Text(p).Contains("\"ar_foo2_zm\"") && LoadGdt(p).Extensions!.Orphans().All(o => o.Name == "ghost_gun"));

            var (g3, p3, s3) = Setup(B(WeaponGdtx));
            Edit(g3.Assets.First(a => a.Name == "ar_bar_zm"), "displayName", "Bar II");
            g3.Extensions!.Set("ar_bar_zm", "weapon-tech", "wtEnabled", "1");
            var svc3 = NewSaveService("hardening-partial");
            svc3.Hooks.AfterSwap = path => { if (path == s3) File.AppendAllText(path, "\r\n"); };
            var r4 = Save(g3, svc3);
            Check($"hardening: a sidecar whose check after the swap fails stops the save before its GDT ({Statuses(r4)})",
                r4.Files.Single(f => f.Request.Sidecar).Status == GdtSaveStatus.Failed && Text(p3) == WeaponGdt
                && r4.Files.Single(f => !f.Request.Sidecar).Status == GdtSaveStatus.NotWritten);
        }
    }

    private static void HardeningApp()
    {
        var akRel = @"source_data\ar_ak47_h1.gdt";
        var smgRel = @"source_data\smg_standard.gdt";
        var weaponNames = LoadGdt(Path.Combine(InstallRoot, akRel)).Assets.Where(a => a.Parent is null && a.Type == "bulletweapon").Select(a => a.Name).Take(2).ToList();
        var values = new List<(string, string)> { ("wtEnabled", "1"), ("wtFireTimeMs", "70") };
        values.AddRange(Enumerable.Range(1, 3).Select(i => ($"wtKick{i}", KickRow(i))));
        string Text(string p) => File.Exists(p) ? GdtEncoding.File.GetString(File.ReadAllBytes(p)) : "(no file)";

        // A temp install with two real GDTs, the first with a .gdtx for its first two weapons (plus any extra blocks).
        (string Install, string Ak, string Akx, string Smg, string Smgx) NewInstall(string label, string extraBlocks = "")
        {
            var install = NewScratch(label);
            CopyTree(Path.Combine(InstallRoot, "deffiles"), Path.Combine(install, "deffiles"));
            foreach (var rel in new[] { akRel, smgRel })
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(install, rel))!);
                File.Copy(Path.Combine(InstallRoot, rel), Path.Combine(install, rel));
            }
            var ak = Path.Combine(install, akRel);
            var smg = Path.Combine(install, smgRel);
            File.WriteAllBytes(ExtensionSidecar.PathFor(ak),
                GdtEncoding.GetBytes("{\r\n" + string.Concat(weaponNames.Select(w => Block(w, values))) + extraBlocks + "}\r\n"));
            return (install, ak, ExtensionSidecar.PathFor(ak), smg, ExtensionSidecar.PathFor(smg));
        }

        MainViewModel? vm = null;
        MainWindow? window = null;
        void Start(string install, string label)
        {
            Environment.SetEnvironmentVariable("APEX_FORCE_MOCK", null);
            Environment.SetEnvironmentVariable("APEX_BO3_ROOT", install);
            Environment.SetEnvironmentVariable("APEX_NO_PERSIST", "1");
            Environment.SetEnvironmentVariable(ExtensionRegistry.DirVariable, ExtensionsDir(label));
            vm = new MainViewModel(Path.Combine(install, "session"));
            window = ShowJournalWindow(vm);
            WaitUntil(() => vm.Status.StartsWith("Loaded") || vm.Status.StartsWith("Restored"), 60_000);
            Pump(100);
        }
        void Stop()
        {
            try
            {
                vm?.FlushSessionToDisk(closing: true);
                window?.Close();
                vm?.Dispose();
            }
            finally
            {
                vm = null;
                window = null;
                ExtensionRegistry.Clear();
            }
        }
        void SaveAll()
        {
            window!.KeyPress(K.S, RawInputModifiers.Control, PhysicalKey.S, "s");
            window!.KeyRelease(K.S, RawInputModifiers.Control, PhysicalKey.S, "s");
            WaitUntil(() => !vm!.IsSaveRunning, 15_000);
            Pump();
        }
        AssetDatabase Db() => DatabaseOf(vm!);
        GdtFile Gdt(string fileName) => Db().Gdts.First(g => g.Name.EndsWith(fileName, StringComparison.OrdinalIgnoreCase));
        void Dismiss()
        {
            if (vm!.IsAlertOpen)
                vm.DismissAlertCommand.Execute(null);
        }

        try
        {
            // ── Undo and redo across a move write to the asset's GDT now, not the one it had at the edit ──
            {
                var (install, _, _, _, _) = NewInstall("hardening-move");
                Start(install, "hardening-move-ext");
                Dismiss();
                var ak = Gdt("ar_ak47_h1.gdt");
                var smg = Gdt("smg_standard.gdt");
                var w0 = Db().Assets.First(a => a.Name == weaponNames[0]);
                var w1 = smg.Assets.First(a => a.Parent is null && a.Type == "bulletweapon");
                PropertyItemViewModel Fire() => vm!.ActiveTab!.AllSentinel.All.First(p => p.Key == "wtFireTimeMs");

                vm!.OpenByName(w0.Name);
                Pump(100);
                Fire().RawValue = "55";
                Pump();
                vm.UndoActiveCommand.Execute(null);
                Pump();
                vm.MoveAssetsInto(new[] { w0 }, smg);
                Pump(50);
                vm.OpenByName(w0.Name);
                Pump(50);
                vm.RedoActiveCommand.Execute(null);
                Pump();
                var inSmg = smg.Extensions?.Get(w0.Name, "weapon-tech", "wtFireTimeMs");
                var inAk = ak.Extensions?.Find(w0.Name, "weapon-tech");
                Check($"hardening: redo after a move lands in the asset's new GDT's sidecar (smg {inSmg}, ak block {(inAk is null ? "none" : "left")})",
                    inSmg == "55" && inAk is null);

                vm.OpenByName(w1.Name);
                Pump(100);
                Fire().RawValue = "61";
                Pump();
                vm.MoveAssetsInto(new[] { w1 }, ak);
                Pump(50);
                w1.History.Undo();
                var after = ak.Extensions?.Get(w1.Name, "weapon-tech", "wtFireTimeMs");
                Check($"hardening: undoing an edit made before a move reverts the moved asset's value ({after ?? "(unset)"})",
                    after is null && smg.Extensions?.Find(w1.Name, "weapon-tech") is null);
                Stop();
            }

            // ── A save that writes one file of two, then a restart: nothing lost, nothing orphaned ──
            foreach (var lockGdt in new[] { false, true })
            {
                var which = lockGdt ? "the GDT" : "the .gdtx";
                var (install, akPath, akx, _, _) = NewInstall("hardening-partial");
                Start(install, "hardening-partial-ext");
                Dismiss();
                var w0 = Db().Assets.First(a => a.Name == weaponNames[0]);
                var renamed = weaponNames[0] + "_ren";
                vm!.OpenByName(weaponNames[0]);
                Pump();
                vm.DuplicateActiveCommand.Execute(null);
                Pump();
                var dup = vm.ActiveTab!.Record.Name;
                Rename(vm, w0, renamed);
                // Read without delete sharing: the lock step goes through, the swap can't replace the file.
                using (new FileStream(lockGdt ? akPath : akx, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    SaveAll();
                var alert = vm.IsAlertOpen ? vm.AlertText : "";
                var gdtHas = Text(akPath).Contains(renamed);
                var gdtxHas = Text(akx).Contains(renamed) && Text(akx).Contains(dup);
                Check($"hardening ({which} held): a partial save leaves the GDT and its .gdtx agreeing or the .gdtx ahead, never the GDT ahead (gdt {gdtHas}, gdtx {gdtxHas})",
                    !gdtHas && (lockGdt == gdtxHas));
                Check($"hardening ({which} held): the unsaved work still counts ({vm.SessionStateText}) and the banner says what wasn't saved ('{alert}')",
                    vm.SessionEditCount > 0 && alert.Contains(Path.GetFileName(lockGdt ? akPath : akx)));
                Dismiss();
                Stop();

                Start(install, "hardening-partial-ext2");
                var ak = Gdt("ar_ak47_h1.gdt");
                var orphans = ak.Extensions?.Orphans().Select(b => b.Name).ToList() ?? new List<string>();
                Check($"hardening ({which} held): after a restart the renamed asset and the duplicate both have their extension data and nothing is orphaned (orphans: {string.Join(",", orphans)})",
                    ak.Extensions?.Find(renamed, "weapon-tech")?.Properties.GetValueOrDefault("wtFireTimeMs") == "70"
                    && ak.Extensions?.Find(dup, "weapon-tech")?.Properties.GetValueOrDefault("wtFireTimeMs") == "70"
                    && orphans.Count == 0 && ak.Extensions!.BlocksOf(dup).Count == 1);
                Dismiss();
                SaveAll();
                var reread = LoadGdt(akPath);
                Check($"hardening ({which} held): the next save completes it: both files hold the rename and the duplicate ('{vm!.Status}')",
                    !vm.IsAlertOpen && reread.Assets.Any(a => a.Name == renamed) && reread.Assets.Any(a => a.Name == dup)
                    && reread.Extensions!.Orphans().Count() == 0 && reread.Extensions.BlocksOf(dup).Count == 1 && vm.SessionEditCount == 0);
                Stop();
            }

            // ── Names an orphan block holds ─────────────────────────────────
            {
                var orphanCopy = weaponNames[0] + "_copy";
                var (install, akPath, akx, _, _) = NewInstall("hardening-orphan",
                    Block(orphanCopy, new[] { ("wtFireTimeMs", "999") }) + Block("ghost_gun_zm", new[] { ("wtFireTimeMs", "888") }));
                Start(install, "hardening-orphan-ext");
                Dismiss();
                var ak = Gdt("ar_ak47_h1.gdt");
                vm!.OpenByName(weaponNames[0]);
                Pump();
                vm.DuplicateActiveCommand.Execute(null);
                Pump();
                var dup = vm.ActiveTab!.Record.Name;
                Check($"hardening: a duplicate skips a name an orphan block has, and reads the source's values ({dup}: {ak.Extensions?.Get(dup, "weapon-tech", "wtFireTimeMs")})",
                    dup != orphanCopy && ak.Extensions?.Get(dup, "weapon-tech", "wtFireTimeMs") == "70");

                // F2 onto the orphan's name: one line says what it is; Enter again replaces it, anything else is another name.
                vm.OpenByName(dup);
                Pump();
                Key(window!, K.F2);
                var box = window!.FindControl<TextBox>("RenameBox")!;
                box.SelectAll();
                window!.KeyTextInput("ghost_gun_zm");
                Key(window!, K.Enter);
                WaitUntil(() => vm.RenameError.Length > 0 || !vm.IsRenameOpen, 5_000);
                var first = vm.RenameError;
                Check($"hardening: renaming onto an orphan block's name stops once and says so ('{first}')",
                    vm.IsRenameOpen && first.Contains("ghost_gun_zm") && vm.ActiveTab!.Record.Name == dup);
                Key(window!, K.Enter);
                WaitUntil(() => !vm.IsRenameOpen, 5_000);
                Pump();
                var x = ak.Extensions!;
                Check($"hardening: Enter again renames, and the asset keeps its own values in place of the orphan's ({x.Get("ghost_gun_zm", "weapon-tech", "wtFireTimeMs")})",
                    !vm.IsRenameOpen && vm.ActiveTab!.Record.Name == "ghost_gun_zm" && x.BlocksOf("ghost_gun_zm").Count == 1
                    && x.Get("ghost_gun_zm", "weapon-tech", "wtFireTimeMs") == "70");
                SaveAll();
                Check($"hardening: that rename saves, the orphan's block replaced ('{vm.Status}')",
                    !vm.IsAlertOpen && !Text(akx).Contains("\"888\"") && LoadGdt(akPath).Extensions!.Get("ghost_gun_zm", "weapon-tech", "wtFireTimeMs") == "70");
                Stop();
            }

            // ── The watcher: a GDT arriving after its .gdtx, and a .gdtx another program created ──
            {
                var (install, _, _, _, _) = NewInstall("hardening-watch");
                var newRel = @"source_data\hardening_new.gdt";
                var newPath = Path.Combine(install, newRel);
                Start(install, "hardening-watch-ext");
                Dismiss();
                File.WriteAllText(ExtensionSidecar.PathFor(newPath), "{\r\n\t\"ar_foo_zm\" ( \"weapon-tech\" )\r\n\t{\r\n\t\t\"wtFireTimeMs\" \"42\"\r\n\t}\r\n}\r\n");
                Pump(1500);
                File.WriteAllText(newPath, WeaponGdt.Replace("weapon.gdf", "bulletweapon.gdf"));
                WaitUntil(() => Db().Gdts.Any(g => g.Name.EndsWith("hardening_new.gdt", StringComparison.OrdinalIgnoreCase)), 5_000);
                Pump(100);
                var arrived = Db().Gdts.FirstOrDefault(g => g.Name.EndsWith("hardening_new.gdt", StringComparison.OrdinalIgnoreCase));
                Check($"hardening: a GDT the watcher brings in after its .gdtx reads that .gdtx ({arrived?.Extensions?.Get("ar_foo_zm", "weapon-tech", "wtFireTimeMs") ?? "none"})",
                    arrived?.Extensions?.Get("ar_foo_zm", "weapon-tech", "wtFireTimeMs") == "42");

                var smg = Gdt("smg_standard.gdt");
                var w = smg.Assets.First(a => a.Parent is null && a.Type == "bulletweapon");
                var smgx = ExtensionSidecar.PathFor(smg.FullPath!);
                vm!.OpenByName(w.Name);
                Pump(100);
                vm.ActiveTab!.AllSentinel.All.First(p => p.Key == "wtEnabled").RawValue = "1";
                Pump();
                var theirs = "{\r\n" + Block("ghost_gun_zm", new[] { ("wtFireTimeMs", "5") }) + "}\r\n";
                File.WriteAllText(smgx, theirs);
                SaveAll();
                Check($"hardening: a .gdtx another program created before the save opens the conflict dialog, not a dead end ('{vm.ConflictSummary}')",
                    vm.IsConflictOpen && vm.CanResolveConflicts && Text(smgx) == theirs);
                if (vm.IsConflictOpen)
                {
                    vm.ResolveConflictsCommand.Execute(null);
                    WaitUntil(() => !vm.IsSaveRunning, 15_000);
                    Pump();
                }
                Check($"hardening: saving from that dialog re-reads the .gdtx and keeps both ('{vm.Status}')",
                    !vm.IsConflictOpen && Text(smgx).Contains("\"5\"") && Text(smgx).Contains(w.Name));
                Stop();
            }

            // ── A .gdtx a killed removal left as .apex-tmp is put back at startup, and the user is told ──
            {
                var (install, _, akx, _, _) = NewInstall("hardening-tmp");
                File.Move(akx, akx + GdtSaveService.TempSuffix);
                Start(install, "hardening-tmp-ext");
                var told = vm!.IsAlertOpen ? vm.AlertText : "";
                Check($"hardening: startup puts back a .gdtx left as .apex-tmp and says so ('{told}')",
                    File.Exists(akx) && !File.Exists(akx + GdtSaveService.TempSuffix) && told.Contains(Path.GetFileName(akx))
                    && Gdt("ar_ak47_h1.gdt").Extensions?.Get(weaponNames[0], "weapon-tech", "wtFireTimeMs") == "70");
                Stop();
            }
        }
        catch (Exception ex)
        {
            Check("hardening app: " + ex, false);
            try { Stop(); } catch { }
        }
        finally
        {
            Environment.SetEnvironmentVariable("APEX_FORCE_MOCK", "1");
            Environment.SetEnvironmentVariable("APEX_BO3_ROOT", null);
            ExtensionRegistry.Clear();
            SchemaRegistry.ResetToMock();
        }
    }
}
