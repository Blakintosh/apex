using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Apex.Editor.Models;
using Apex.Editor.Services.Gdt;
using Apex.Editor.Services.Save;
using Apex.Editor.ViewModels;
using Apex.Editor.Views;

namespace Apex.Shots;

/// <summary>
/// Copy, cut and paste between GDTs, Duplicate to…, Move to…, Derive and Underive in the live app, with real input, on
/// a temp install (real deffiles and two real GDTs copied under the write root), saved and read back byte for byte.
/// Nothing here touches the BO3 install.
/// </summary>
public partial class Program
{
    private static void RunPlacementChecks(string outDir)
    {
        var install = NewScratch("placement-install");
        CopyTree(Path.Combine(InstallRoot, "deffiles"), Path.Combine(install, "deffiles"));
        var smgRel = @"source_data\smg_standard.gdt";
        var akRel = @"source_data\ar_ak47_h1.gdt";
        foreach (var rel in new[] { smgRel, akRel })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(install, rel))!);
            File.Copy(Path.Combine(InstallRoot, rel), Path.Combine(install, rel));
        }
        var smgPath = Path.Combine(install, smgRel);
        var akPath = Path.Combine(install, akRel);
        var sessionDir = Path.Combine(install, "session");
        // A third GDT that already has an asset named like one in smg_standard: a move there must be refused.
        var clashPath = Path.Combine(install, @"source_data\clash_test.gdt");
        File.WriteAllText(clashPath, "{\r\n\t\"au_smg_standard_stalker\" ( \"attachmentunique.gdf\" )\r\n\t{\r\n\t}\r\n}\r\n", Encoding.ASCII);

        var saved = (Mock: Environment.GetEnvironmentVariable("APEX_FORCE_MOCK"), Root: Environment.GetEnvironmentVariable("APEX_BO3_ROOT"),
            Persist: Environment.GetEnvironmentVariable("APEX_NO_PERSIST"));
        Environment.SetEnvironmentVariable("APEX_FORCE_MOCK", null);
        Environment.SetEnvironmentVariable("APEX_BO3_ROOT", install);
        Environment.SetEnvironmentVariable("APEX_NO_PERSIST", "1");
        MainViewModel? vm = null;
        MainWindow? window = null;
        try
        {
            vm = new MainViewModel(sessionDir);
            window = ShowJournalWindow(vm);
            WaitUntil(() => vm.Status.StartsWith("Loaded"), 60_000);
            Check($"placement: Apex loaded the temp install live ('{vm.Status}')", vm.Status.StartsWith("Loaded") && !vm.IsMockData);
            var db = DatabaseOf(vm);
            var smg = db.Gdts.First(g => g.Name.EndsWith("smg_standard.gdt", StringComparison.OrdinalIgnoreCase));
            var ak = db.Gdts.First(g => g.Name.EndsWith("ar_ak47_h1.gdt", StringComparison.OrdinalIgnoreCase));
            AssetRecord Find(string name) => db.Assets.Single(a => a.Name == name);
            int CountNamed(string name) => db.Assets.Count(a => a.Name == name);
            ListBox Tree() => window!.GetVisualDescendants().OfType<AssetBrowserView>().First().FindControl<ListBox>("Tree")!;
            var tree = Tree();

            void FocusRow(AssetRecord asset)
            {
                tree = Tree();
                vm.RevealInTree(asset);
                Pump();
                var row = vm.FlatRows.First(n => n.Asset == asset && !n.IsPinnedEntry);
                RowContainer(tree, vm, row).Focus(NavigationMethod.Directional);
                Pump();
            }
            void ClickGdtRow(GdtFile gdt)
            {
                tree = Tree();
                var row = vm.FlatRows.First(n => n.Gdt == gdt);
                Click(window, RowContainer(tree, vm, row), MouseButton.Left);
            }

            // ── Copy a derived asset into another GDT: Ctrl+C on its row, Ctrl+V on the GDT's row ──
            var acog = Find("au_smg_standard_acog");
            var acogOwn = new Dictionary<string, string>(acog.Properties, StringComparer.OrdinalIgnoreCase);
            var akBefore = File.ReadAllBytes(akPath);
            var smgBefore = File.ReadAllBytes(smgPath);
            // For scale: opening an asset of this kind (what a paste ends with: the copy opens, as Duplicate's does).
            var openSw = Stopwatch.StartNew();
            vm.OpenByName("au_smg_standard_gmod6");
            Pump();
            Console.WriteLine($"info  placement: for scale, opening an asset (to state, UI thread) {openSw.Elapsed.TotalMilliseconds:0.0} ms");
            FocusRow(acog);
            Key(window, Avalonia.Input.Key.C, RawInputModifiers.Control);
            Check($"placement: Ctrl+C on an asset row copies it ('{vm.Status}')", vm.Status.StartsWith("Copied au_smg_standard_acog") && vm.CanPasteAssets);
            ClickGdtRow(ak);
            // The pointer rests on the row: its tip comes due while the click's wait held the dispatcher. Let it open now,
            // off the clock; it is not the paste's work.
            Pump(50);
            // Key down to the paste being in the session (its status line), before the copy's editor is built.
            var sw = Stopwatch.StartNew();
            var pasteMs = -1.0;
            void OnPasted(object? s, System.ComponentModel.PropertyChangedEventArgs e)
            {
                if (e.PropertyName == nameof(MainViewModel.Status) && pasteMs < 0 && vm!.Status.StartsWith("Copied au_smg_standard_acog into"))
                    pasteMs = sw.Elapsed.TotalMilliseconds;
            }
            vm.PropertyChanged += OnPasted;
            window.KeyPress(Avalonia.Input.Key.V, RawInputModifiers.Control, PhysicalKey.V, "v");
            vm.PropertyChanged -= OnPasted;
            Check($"placement: the paste is in the session before the copy's editor opens ({pasteMs:0.0} ms, first paste of the run)",
                db.Assets.Any(a => a.Name == "au_smg_standard_acog_copy") && pasteMs >= 0);
            window.KeyRelease(Avalonia.Input.Key.V, RawInputModifiers.Control, PhysicalKey.V, "v");
            Pump();
            var copy = db.Assets.FirstOrDefault(a => a.Name == "au_smg_standard_acog_copy");
            Check($"placement: Ctrl+V on a GDT row pastes a copy there, named as Duplicate names it ('{vm.Status}', {copy?.GdtName})",
                copy is not null && copy.GdtName == ak.Name && ak.Assets.Contains(copy) && vm.ActiveTab?.Record == copy);
            Check($"placement: the copy of a derived asset keeps its parent and only its own values ({copy?.Parent}, {copy?.Properties.Count} keys)",
                copy is { Parent: "au_smg_standard_none" } && copy.Properties.Count == acogOwn.Count
                && acogOwn.All(kv => copy.Properties.GetValueOrDefault(kv.Key) == kv.Value));
            Console.WriteLine($"info  placement: paste (key handler, UI thread; the copy's editor opens after) {pasteMs:0.0} ms");
            Capture(window, Path.Combine(outDir, "60-placement-pasted.png"));

            sw.Restart();
            Key(window, Avalonia.Input.Key.Z, RawInputModifiers.Control);
            Console.WriteLine($"info  placement: undo paste (key to state, UI thread) {sw.Elapsed.TotalMilliseconds:0.0} ms");
            Check($"placement: Ctrl+Z takes the paste back as one step ('{vm.Status}')",
                CountNamed("au_smg_standard_acog_copy") == 0 && !ak.Assets.Any(a => a.Name == "au_smg_standard_acog_copy") && vm.SessionEditCount == 0);
            sw.Restart();
            Key(window, Avalonia.Input.Key.Y, RawInputModifiers.Control);
            Console.WriteLine($"info  placement: redo paste (key to state, UI thread) {sw.Elapsed.TotalMilliseconds:0.0} ms");
            copy = db.Assets.FirstOrDefault(a => a.Name == "au_smg_standard_acog_copy");
            Check($"placement: Ctrl+Y pastes it again ('{vm.Status}')", copy is not null && copy.GdtName == ak.Name && vm.SessionEditCount == 1);

            // Warm: take it back and paste again with Ctrl+V, timed from the key to the paste being in the session.
            Key(window, Avalonia.Input.Key.Z, RawInputModifiers.Control);
            ClickGdtRow(ak);
            sw.Restart();
            pasteMs = -1;
            vm.PropertyChanged += OnPasted;
            window.KeyPress(Avalonia.Input.Key.V, RawInputModifiers.Control, PhysicalKey.V, "v");
            vm.PropertyChanged -= OnPasted;
            window.KeyRelease(Avalonia.Input.Key.V, RawInputModifiers.Control, PhysicalKey.V, "v");
            Pump();
            copy = db.Assets.FirstOrDefault(a => a.Name == "au_smg_standard_acog_copy");
            Console.WriteLine($"info  placement: paste, warm (key to state, UI thread) {pasteMs:0.0} ms");
            Gate($"placement: a paste lands within a frame of the key ({pasteMs:0.0} ms; the copy's editor opens after)",
                copy is not null && copy.GdtName == ak.Name && vm.SessionEditCount == 1, pasteMs is >= 0 and < 16);

            window.KeyPress(Avalonia.Input.Key.S, RawInputModifiers.Control, PhysicalKey.S, "s");
            WaitUntil(() => !vm.IsSaveRunning, 10_000);
            Pump();
            var akAfter = File.ReadAllBytes(akPath);
            var last = GdtLayout.Scan(akAfter).Assets.Last();
            var withoutNew = akAfter.AsSpan(0, last.BlockStart).ToArray().Concat(akAfter.AsSpan(last.BlockEnd).ToArray()).ToArray();
            var lastValues = GdtParser.ParseProperties(akAfter, last.BodyStart, last.BodyLength);
            Check($"placement: saving writes the copy at the end of its GDT as APE writes a derived asset ('{vm.Status}')",
                last is { Name: "au_smg_standard_acog_copy", IsDerived: true, TypeOrParent: "au_smg_standard_none" }
                && withoutNew.AsSpan().SequenceEqual(akBefore) && GdtSplicer.SameValues(lastValues, acogOwn)
                && Encoding.Latin1.GetString(akAfter, last.BlockStart, last.BlockEnd - last.BlockStart).Contains("\"au_smg_standard_acog_copy\" [ \"au_smg_standard_none\" ]"));
            Check("placement: the source GDT is untouched by a copy", File.ReadAllBytes(smgPath).AsSpan().SequenceEqual(smgBefore));
            Check($"placement: after the save, the paste is no longer an undo step (Ctrl+Z can't unsave it) [{vm.CanUndoActive}]",
                vm.SessionEditCount == 0);

            // ── Move with Ctrl+X / Ctrl+V: the name stays, so references stay valid; undo puts it back ──
            akBefore = File.ReadAllBytes(akPath);
            var dual = Find("au_smg_standard_dualoptic");
            var dualValues = new Dictionary<string, string>(dual.Properties, StringComparer.OrdinalIgnoreCase);
            var dualDisk = dual.Disk;
            FocusRow(dual);
            Key(window, Avalonia.Input.Key.X, RawInputModifiers.Control);
            Check($"placement: Ctrl+X cuts ('{vm.Status}')", vm.Status.StartsWith("Cut au_smg_standard_dualoptic"));
            ClickGdtRow(ak);
            sw.Restart();
            Key(window, Avalonia.Input.Key.V, RawInputModifiers.Control);
            var moveMs = sw.Elapsed.TotalMilliseconds;
            Check($"placement: Ctrl+V after a cut moves it, same name and parent ('{vm.Status}')",
                dual.GdtName == ak.Name && ak.Assets.Contains(dual) && !smg.Assets.Contains(dual) && CountNamed(dual.Name) == 1
                && dual.Parent == "au_smg_standard_none" && vm.Status == "Moved au_smg_standard_dualoptic to ar_ak47_h1");
            Console.WriteLine($"info  placement: move (key to state, UI thread) {moveMs:0.0} ms");
            Capture(window, Path.Combine(outDir, "61-placement-moved.png"));
            Key(window, Avalonia.Input.Key.Z, RawInputModifiers.Control);
            Check($"placement: Ctrl+Z puts the moved asset back where it was ('{vm.Status}')",
                dual.GdtName == smg.Name && smg.Assets.Contains(dual) && !ak.Assets.Contains(dual) && dual.Disk == dualDisk && vm.SessionEditCount == 0);
            Check($"placement: an undone move leaves nothing to save ({vm.TimeSavePlan(out var pending):0.0} ms, {pending} GDTs)", pending == 0);
            Key(window, Avalonia.Input.Key.Y, RawInputModifiers.Control);
            Check("placement: Ctrl+Y moves it again", dual.GdtName == ak.Name && ak.Assets.Contains(dual));

            window.KeyPress(Avalonia.Input.Key.S, RawInputModifiers.Control, PhysicalKey.S, "s");
            WaitUntil(() => !vm.IsSaveRunning, 10_000);
            Pump();
            var smgSpan = GdtLayout.Scan(smgBefore).Assets.Single(a => a.Name == "au_smg_standard_dualoptic");
            var smgExpected = smgBefore.AsSpan(0, smgSpan.BlockStart).ToArray().Concat(smgBefore.AsSpan(smgSpan.BlockEnd).ToArray()).ToArray();
            akAfter = File.ReadAllBytes(akPath);
            last = GdtLayout.Scan(akAfter).Assets.Last();
            withoutNew = akAfter.AsSpan(0, last.BlockStart).ToArray().Concat(akAfter.AsSpan(last.BlockEnd).ToArray()).ToArray();
            Check($"placement: saving a move changes two files: the asset leaves one ('{vm.Status}')",
                File.ReadAllBytes(smgPath).AsSpan().SequenceEqual(smgExpected) && vm.Status == "Saved 1 asset in 2 GDTs");
            Check("placement: … and is appended to the other, values intact",
                last.Name == "au_smg_standard_dualoptic" && last.IsDerived && withoutNew.AsSpan().SequenceEqual(akBefore)
                && GdtSplicer.SameValues(GdtParser.ParseProperties(akAfter, last.BodyStart, last.BodyLength), dualValues));
            Check($"placement: both files were backed up ({new BackupStore(BackupStore.DefaultRoot).List(smgPath).Count}, {new BackupStore(BackupStore.DefaultRoot).List(akPath).Count})",
                new BackupStore(BackupStore.DefaultRoot).List(smgPath).Count >= 1 && new BackupStore(BackupStore.DefaultRoot).List(akPath).Count >= 2);
            Check($"placement: the moved asset reads from its new file, nothing left to save (chip '{vm.SessionStateText}')",
                vm.SessionEditCount == 0 && dual.Disk is not null);

            // ── Move to… through the palette and the GDT picker, by keys; a conflicting change on disk stops the save ──
            var ext = Find("au_smg_standard_extbarrel");
            vm.OpenByName(ext.Name);
            Pump();
            window.KeyPress(Avalonia.Input.Key.P, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.P, "P");
            Pump();
            window.KeyTextInput("move to");
            Pump();
            Key(window, Avalonia.Input.Key.Enter);
            Check($"placement: “Move to…” opens the GDT picker ('{vm.PaletteHint}')",
                vm.IsPaletteOpen && vm.PaletteHint.StartsWith("Move au_smg_standard_extbarrel to…") && vm.PaletteResults.All(r => r.Name != "smg_standard"));
            Check($"placement: the picker offers the last GDT saved into first ({vm.PaletteResults.FirstOrDefault()?.Name} · {vm.PaletteResults.FirstOrDefault()?.Detail})",
                vm.PaletteResults.FirstOrDefault() is { Name: "ar_ak47_h1" } first && first.Detail.StartsWith("last used"));
            Capture(window, Path.Combine(outDir, "62-placement-move-to-picker.png"));
            Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
            Capture(window, Path.Combine(outDir, "62b-light-placement-move-to-picker.png"));
            Application.Current.RequestedThemeVariant = ThemeVariant.Dark;
            window.KeyTextInput("ak47");
            Pump();
            Key(window, Avalonia.Input.Key.Enter);
            Check($"placement: Enter on a GDT moves it there ('{vm.Status}')", ext.GdtName == ak.Name && !vm.IsPaletteOpen);
            var smgNow = File.ReadAllBytes(smgPath);
            var akNow = File.ReadAllBytes(akPath);
            File.WriteAllBytes(smgPath, WithValue(smgNow, ext.Name, ext.SessionBaseline!.Keys.First(), "changed_elsewhere"));
            var changedSmg = File.ReadAllBytes(smgPath);
            WaitUntil(() => vm.Status.StartsWith("Reloaded"), 5_000);
            Check($"placement: a reload of the old GDT keeps the moved asset moved ({CountNamed(ext.Name)} named, in {ext.GdtName})",
                CountNamed(ext.Name) == 1 && ext.GdtName == ak.Name);
            window.KeyPress(Avalonia.Input.Key.S, RawInputModifiers.Control, PhysicalKey.S, "s");
            WaitUntil(() => !vm.IsSaveRunning, 10_000);
            Pump();
            Check($"placement: the asset changed on disk before the move was saved: the conflict dialog, nothing written ({vm.ConflictSummary})",
                vm.IsConflictOpen && File.ReadAllBytes(smgPath).AsSpan().SequenceEqual(changedSmg) && File.ReadAllBytes(akPath).AsSpan().SequenceEqual(akNow));
            ClickNamed(window, "ConflictCancelButton");
            Pump();
            Key(window, Avalonia.Input.Key.Z, RawInputModifiers.Control);
            Check($"placement: undo puts it back after the cancelled save ('{vm.Status}')", ext.GdtName == smg.Name && smg.Assets.Contains(ext));

            // ── Derive (palette, by keys): Parent = the source, no values of its own, open on Overrides ──
            var none = Find("au_smg_standard_none");
            vm.OpenByName(none.Name);
            Pump();
            window.KeyPress(Avalonia.Input.Key.P, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.P, "P");
            Pump();
            window.KeyTextInput("derive");
            Pump();
            Key(window, Avalonia.Input.Key.Enter);
            var derived = db.Assets.FirstOrDefault(a => a.Name == "au_smg_standard_none_derived");
            var dtab = vm.ActiveTab;
            Check($"placement: Derive makes a child with no values of its own ('{vm.Status}')",
                derived is { Parent: "au_smg_standard_none", Properties.Count: 0 } && derived.GdtName == smg.Name && dtab?.Record == derived);
            Check($"placement: … opened on its Overrides view, which is empty ({dtab?.View}, {dtab?.OverrideCount} overrides, {dtab?.VisiblePropertyCount} rows)",
                dtab is { View: EditorView.Overrides, OverrideCount: 0, VisiblePropertyCount: 0 });
            var inheritedRow = dtab?.AllSentinel.All.FirstOrDefault(p => none.Properties.ContainsKey(p.Key) && none.Properties[p.Key].Length > 0);
            Check($"placement: its rows show the inherited values ({inheritedRow?.Key} = {inheritedRow?.RawValue})",
                inheritedRow is not null && inheritedRow.RawValue == none.Properties[inheritedRow.Key] && inheritedRow.IsInherited);
            Capture(window, Path.Combine(outDir, "63-placement-derived.png"));
            Key(window, Avalonia.Input.Key.Z, RawInputModifiers.Control);
            Check($"placement: Ctrl+Z removes the derived asset ('{vm.Status}')", CountNamed("au_smg_standard_none_derived") == 0);

            // ── Underive: the effective values don't change; the header becomes a root asset's on save ──
            var fmj = Find("au_smg_standard_fmj");
            vm.OpenByName(fmj.Name);
            Pump();
            var effectiveBefore = Effective(fmj, db);
            var fromDefaults = effectiveBefore.Keys.Count(k => !fmj.Properties.ContainsKey(k)
                && !db.Assets.First(a => a.Name == "au_smg_standard_none").Properties.ContainsKey(k));
            Console.WriteLine($"info  placement: underive of {fmj.Name}: {fmj.Properties.Count} own keys, "
                              + $"{effectiveBefore.Count - fmj.Properties.Count - fromDefaults} from its parent, {fromDefaults} schema defaults the chain lacks");
            var ownBefore = new Dictionary<string, string>(fmj.Properties, StringComparer.OrdinalIgnoreCase);
            smgBefore = File.ReadAllBytes(smgPath);
            window.KeyPress(Avalonia.Input.Key.P, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.P, "P");
            Pump();
            window.KeyTextInput("underive");
            Pump();
            sw.Restart();
            Key(window, Avalonia.Input.Key.Enter);
            var underiveMs = sw.Elapsed.TotalMilliseconds;
            var effectiveAfter = Effective(fmj, db);
            var differ = effectiveBefore.Keys.Union(effectiveAfter.Keys, StringComparer.OrdinalIgnoreCase)
                .Where(k => effectiveBefore.GetValueOrDefault(k) != effectiveAfter.GetValueOrDefault(k)).ToList();
            Check($"placement: Underive clears the parent and keeps every effective value ({effectiveBefore.Count} keys, {differ.Count} differ: {string.Join(", ", differ.Take(3))})",
                fmj.Parent is null && differ.Count == 0 && fmj.Properties.Count >= effectiveBefore.Count && vm.ActiveTab is { Record: var ur, HasParent: false } && ur == fmj);
            Console.WriteLine($"info  placement: underive (key to rebuilt editor, UI thread) {underiveMs:0.0} ms");
            Capture(window, Path.Combine(outDir, "64-placement-underived.png"));
            Key(window, Avalonia.Input.Key.Z, RawInputModifiers.Control);
            Check($"placement: Ctrl+Z derives it again with exactly its own values ('{vm.Status}')",
                fmj.Parent == "au_smg_standard_none" && GdtSplicer.SameValues(fmj.Properties, ownBefore) && vm.ActiveTab?.HasParent == true);
            Key(window, Avalonia.Input.Key.Y, RawInputModifiers.Control);
            Check("placement: Ctrl+Y underives it again", fmj.Parent is null);

            // Hot exit: a pending move and underive come back after a restart.
            var grip = Find("au_smg_standard_grip");
            sw.Restart();
            vm.MoveAssetsInto(new[] { grip }, ak);
            Console.WriteLine($"info  placement: Move to… itself (no layout), UI thread {sw.Elapsed.TotalMilliseconds:0.0} ms");
            vm.FlushSessionToDisk(closing: true);
            window.Close();
            vm.Dispose();
            vm = new MainViewModel(sessionDir);
            window = ShowJournalWindow(vm);
            WaitUntil(() => vm.Status.StartsWith("Restored") || vm.Status.StartsWith("Loaded"), 60_000);
            WaitUntil(() => vm.Status.StartsWith("Restored"), 5_000);
            db = DatabaseOf(vm);
            var fmj2 = db.Assets.Single(a => a.Name == "au_smg_standard_fmj");
            var gripAfterRestart = db.Assets.Single(a => a.Name == "au_smg_standard_grip");
            var effectiveRestored = new Dictionary<string, string>(fmj2.Properties, StringComparer.OrdinalIgnoreCase);
            Check($"placement: after a restart the move and the underive are still there ('{vm.Status}', grip in {gripAfterRestart.GdtName})",
                gripAfterRestart.GdtName.EndsWith("ar_ak47_h1.gdt") && fmj2.Parent is null
                && effectiveAfter.All(kv => effectiveRestored.GetValueOrDefault(kv.Key) == kv.Value) && effectiveRestored.Count == effectiveAfter.Count);

            smgNow = File.ReadAllBytes(smgPath);
            window.KeyPress(Avalonia.Input.Key.S, RawInputModifiers.Control, PhysicalKey.S, "s");
            WaitUntil(() => !vm.IsSaveRunning, 10_000);
            Pump();
            var smgSaved = File.ReadAllBytes(smgPath);
            var fmjSpan = GdtLayout.Scan(smgSaved).Assets.Single(a => a.Name == "au_smg_standard_fmj");
            var fmjKeys = GdtLayout.ScanProps(smgSaved, fmjSpan, out _).Select(p => p.Key).ToList();
            var header = Encoding.Latin1.GetString(smgSaved, fmjSpan.BlockStart, fmjSpan.BodyStart - fmjSpan.BlockStart);
            Check($"placement: an underived asset saves as a root asset, header as APE writes it ('{header.Trim().Split('\n')[0].Trim()}', '{vm.Status}')",
                !fmjSpan.IsDerived && fmjSpan.TypeOrParent == "attachmentunique" && header.Contains("\"au_smg_standard_fmj\" ( \"attachmentunique.gdf\" )"));
            Check($"placement: … holding every effective value, keys in APE's order ({fmjKeys.Count} keys)",
                GdtSplicer.SameValues(GdtParser.ParseProperties(smgSaved, fmjSpan.BodyStart, fmjSpan.BodyLength), effectiveAfter)
                && fmjKeys.SequenceEqual(fmjKeys.OrderBy(k => k, ApeKeyComparer.Instance)));
            var untouched = GdtLayout.Scan(smgNow).Assets.Where(a => a.Name is not ("au_smg_standard_fmj" or "au_smg_standard_grip")).ToList();
            var savedByName = GdtLayout.Scan(smgSaved).Assets.Where(a => a.Name is not ("au_smg_standard_fmj" or "au_smg_standard_grip")).ToList();
            Check($"placement: every other asset in that file is byte-identical ({untouched.Count})",
                untouched.Count == savedByName.Count && untouched.Zip(savedByName).All(p =>
                    smgNow.AsSpan(p.First.BlockStart, p.First.BlockEnd - p.First.BlockStart).SequenceEqual(smgSaved.AsSpan(p.Second.BlockStart, p.Second.BlockEnd - p.Second.BlockStart))));
            Check($"placement: the restored move saved into the other GDT (chip '{vm.SessionStateText}')",
                GdtLayout.Scan(File.ReadAllBytes(akPath)).Assets.Any(a => a.Name == "au_smg_standard_grip")
                && !GdtLayout.Scan(smgSaved).Assets.Any(a => a.Name == "au_smg_standard_grip") && vm.SessionEditCount == 0);

            void Restart()
            {
                vm.FlushSessionToDisk(closing: true);
                window.Close();
                vm.Dispose();
                vm = new MainViewModel(sessionDir);
                window = ShowJournalWindow(vm);
                WaitUntil(() => vm.Status.StartsWith("Restored") || vm.Status.StartsWith("Loaded"), 60_000);
                WaitUntil(() => vm.Status.StartsWith("Restored"), 5_000);
                db = DatabaseOf(vm);
                smg = db.Gdts.First(g => g.Name.EndsWith("smg_standard.gdt", StringComparison.OrdinalIgnoreCase));
                ak = db.Gdts.First(g => g.Name.EndsWith("ar_ak47_h1.gdt", StringComparison.OrdinalIgnoreCase));
            }
            bool Unapplied() => vm.IsAlertOpen && vm.AlertText.StartsWith("Couldn't restore");
            AssetRecord InSmg(string name) => db.Assets.First(a => a.Name == name && a.GdtName == smg.Name);

            // The window and catalog were replaced by the restart above.
            smg = db.Gdts.First(g => g.Name.EndsWith("smg_standard.gdt", StringComparison.OrdinalIgnoreCase));
            ak = db.Gdts.First(g => g.Name.EndsWith("ar_ak47_h1.gdt", StringComparison.OrdinalIgnoreCase));

            // ── 1. A move into a GDT that already has that name is refused, nothing moves ──
            var clashGdt = db.Gdts.First(g => g.Name.EndsWith("clash_test.gdt", StringComparison.OrdinalIgnoreCase));
            var stalker = InSmg("au_smg_standard_stalker");
            var refused = !vm.MoveAssetsInto(new[] { stalker }, clashGdt);
            Check($"placement: a move into a GDT that already has the name is refused ('{vm.AlertText}')",
                refused && stalker.GdtName == smg.Name && clashGdt.Assets.Count == 1 && vm.IsAlertOpen
                && vm.AlertText.Contains("already has an asset by that name") && vm.SessionEditCount == 0);
            vm.DismissAlertCommand.Execute(null);
            var stalkerToo = clashGdt.Assets.Single();
            refused = !vm.MoveAssetsInto(new[] { stalker, stalkerToo }, ak);
            Check($"placement: two assets of one name moved together are refused ('{vm.AlertText}')",
                refused && stalker.GdtName == smg.Name && stalkerToo.GdtName == clashGdt.Name && !ak.Assets.Any(a => a.Name == stalker.Name)
                && vm.AlertText.Contains("two of them are named"));
            vm.DismissAlertCommand.Execute(null);

            // ── 3. A clipboard that can't be read keeps the copied assets, and says to try again ──
            var realShell = vm.Shell!;
            var holoC = InSmg("au_smg_standard_holo");
            FocusRow(holoC);
            Key(window, Avalonia.Input.Key.C, RawInputModifiers.Control);
            vm.Shell = new UnreadableClipboard(realShell);
            ClickGdtRow(ak);
            Key(window, Avalonia.Input.Key.V, RawInputModifiers.Control);
            Pump(50);
            Check($"placement: an unreadable clipboard pastes nothing but keeps the copied assets ('{vm.Status}')",
                vm.CanPasteAssets && vm.Status.StartsWith("Couldn't read the clipboard") && !db.Assets.Any(a => a.Name == "au_smg_standard_holo_copy"));
            vm.Shell = realShell;

            // ── 7. Copying something else since Ctrl+C: the assets are no longer what Ctrl+V pastes ──
            var holo = InSmg("au_smg_standard_holo");
            FocusRow(holo);
            Key(window, Avalonia.Input.Key.C, RawInputModifiers.Control);
            vm.Shell!.CopyText("text from another program");
            Pump();
            ClickGdtRow(ak);
            Key(window, Avalonia.Input.Key.V, RawInputModifiers.Control);
            Pump(50);
            Check($"placement: once the clipboard holds something else, Ctrl+V pastes no assets ('{vm.Status}')",
                !db.Assets.Any(a => a.Name == "au_smg_standard_holo_copy") && vm.Status.StartsWith("The clipboard holds something else") && !vm.CanPasteAssets);

            // ── 5a. A derived asset's inherited key set to the schema default is a change, counted and journaled ──
            var steady = InSmg("au_smg_standard_steadyaim");
            vm.OpenByName(steady.Name);
            Pump();
            var srow = vm.ActiveTab!.AllSentinel.All.FirstOrDefault(p => !steady.Properties.ContainsKey(p.Key) && p.ParentValue is { } pv
                && pv != p.Def.Default && p is TextPropertyViewModel or NumberPropertyViewModel);
            var inheritedValue = srow?.RawValue;
            if (srow is not null)
            {
                srow.RawValue = srow.Def.Default;
                Check($"placement: a derived asset's inherited {srow.Key} set to the default counts as a change ({vm.SessionEditCount}, row changed {srow.IsChanged})",
                    vm.SessionEditCount == 1 && srow.IsChanged && steady.Properties.GetValueOrDefault(srow.Key) == srow.Def.Default);
                srow.RawValue = inheritedValue!;
                Check($"placement: … and set back to the inherited value it inherits again: no line, no change ({vm.SessionEditCount})",
                    vm.SessionEditCount == 0 && !srow.IsChanged && !steady.Properties.ContainsKey(srow.Key));
                srow.RawValue = srow.Def.Default;
            }
            else
                Check("placement: a derived row with an inherited value that isn't the default", false);

            // ── 5b. Editing the parent in another tab shows in a derived tab's inherited rows ──
            var parentRec = InSmg("au_smg_standard_none");
            var steadyTab = vm.ActiveTab!;
            var follow = steadyTab.AllSentinel.All.FirstOrDefault(p => !steady.Properties.ContainsKey(p.Key) && p.ParentValue is not null
                && p is TextPropertyViewModel && p.Key != srow?.Key);
            vm.OpenByName(parentRec.Name);
            Pump();
            vm.ActiveTab!.AllSentinel.All.First(p => p.Key == follow!.Key).RawValue = "parent_edit";
            Pump();
            Check($"placement: a parent edited in another tab shows in the derived tab ({follow?.Key} = {follow?.RawValue})",
                follow is { RawValue: "parent_edit", ParentValue: "parent_edit", IsChanged: false });
            vm.ActiveTab!.UndoCommand.Execute(null);

            // ── 2. Journal stays where the asset is: move, rename, undo the move; move, rename, move back ──
            var holo2 = InSmg("au_smg_standard_holo");
            vm.MoveAssetsInto(new[] { holo2 }, ak);
            Rename(vm, holo2, "au_smg_standard_holo_ren");
            Key(window, Avalonia.Input.Key.Z, RawInputModifiers.Control);
            Check($"placement: undoing a move after a rename puts the renamed asset back ('{vm.Status}')",
                holo2.Name == "au_smg_standard_holo_ren" && holo2.GdtName == smg.Name);
            var quick = InSmg("au_smg_standard_quickdraw");
            vm.MoveAssetsInto(new[] { quick }, ak);
            Rename(vm, quick, "au_smg_standard_quickdraw_ren");
            vm.MoveAssetsInto(new[] { quick }, smg);
            Check($"placement: moving it back cancels the move ({quick.GdtName}, on disk {quick.Disk?.Name})",
                quick.GdtName == smg.Name && quick.Disk is not null);

            // ── 10. A value removed before an underive doesn't strip what the underive took in ──
            var reddot = InSmg("au_smg_standard_reddot");
            var dropKey = reddot.Properties.Keys.First(k => parentRec.Properties.ContainsKey(k) && parentRec.Properties[k] != reddot.Properties[k]);
            EditHistory.Set(reddot, dropKey, null);
            typeof(MainViewModel).GetMethod("OnRecordEditedExternally", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .Invoke(vm, new object[] { reddot });
            vm.FlushJournalNow();
            vm.UnderiveAsset(reddot);
            var reddotFlat = new Dictionary<string, string>(reddot.Properties, StringComparer.OrdinalIgnoreCase);

            // ── 3. Move, then delete: Ctrl+Z takes the delete back first (the asset returns where it was moved to), Ctrl+Y
            //       deletes it again; the save leaves nothing behind ──
            var rf = InSmg("au_smg_standard_rf");
            vm.MoveAssetsInto(new[] { rf }, ak);
            vm.OpenByName(rf.Name);
            Pump();
            vm.DeleteActiveCommand.Execute(null);
            WaitUntil(() => !vm.IsDeletePending, 5_000);
            Pump();
            Check($"placement: the delete says how to take it back ('{vm.Status}')",
                db.Assets.Count(a => a.Name == rf.Name) == 0 && vm.Status == "Deleted au_smg_standard_rf · Ctrl+Z restores it");
            Key(window, Avalonia.Input.Key.Z, RawInputModifiers.Control);
            Check($"placement: Ctrl+Z after a moved asset was deleted restores it where it was moved to ('{vm.Status}')",
                ak.Assets.Contains(rf) && !smg.Assets.Contains(rf) && db.Assets.Contains(rf) && vm.Status == "Restored au_smg_standard_rf");
            Key(window, Avalonia.Input.Key.Y, RawInputModifiers.Control);
            Check($"placement: Ctrl+Y deletes it again ('{vm.Status}')",
                db.Assets.Count(a => a.Name == rf.Name) == 0 && !smg.Assets.Contains(rf) && !ak.Assets.Contains(rf));

            Restart();
            var holoBack = db.Assets.FirstOrDefault(a => a.Name == "au_smg_standard_holo_ren");
            var quickBack = db.Assets.FirstOrDefault(a => a.Name == "au_smg_standard_quickdraw_ren");
            Check($"placement: after a restart, the rename made while moved is kept where the asset is ('{vm.Status}', alert '{vm.AlertText}')",
                holoBack?.GdtName == smg.Name && quickBack?.GdtName == smg.Name && quickBack.Disk is not null && !Unapplied());
            var steadyBack = db.Assets.First(a => a.Name == "au_smg_standard_steadyaim");
            Check($"placement: after a restart, the inherited key set to the default is still set ({srow?.Key} = {steadyBack.Properties.GetValueOrDefault(srow?.Key ?? "")})",
                srow is not null && steadyBack.Properties.GetValueOrDefault(srow.Key) == srow.Def.Default);
            var reddotBack = db.Assets.First(a => a.Name == "au_smg_standard_reddot");
            Check($"placement: after a restart, an underive keeps every value it took, a value removed before it included ({dropKey} = {reddotBack.Properties.GetValueOrDefault(dropKey)})",
                reddotBack.Parent is null && reddotFlat.All(kv => reddotBack.Properties.GetValueOrDefault(kv.Key) == kv.Value) && reddotBack.Properties.Count == reddotFlat.Count);
            Check($"placement: the deleted moved asset is still deleted after a restart", !db.Assets.Any(a => a.Name == "au_smg_standard_rf"));

            window.KeyPress(Avalonia.Input.Key.S, RawInputModifiers.Control, PhysicalKey.S, "s");
            WaitUntil(() => !vm.IsSaveRunning, 10_000);
            Pump();
            var smgFinal = GdtLayout.Scan(File.ReadAllBytes(smgPath)).Assets;
            var akFinal = GdtLayout.Scan(File.ReadAllBytes(akPath)).Assets;
            Check($"placement: saving writes the renames in place and removes the deleted asset from both files ('{vm.Status}')",
                smgFinal.Any(a => a.Name == "au_smg_standard_holo_ren") && smgFinal.Any(a => a.Name == "au_smg_standard_quickdraw_ren")
                && !smgFinal.Any(a => a.Name == "au_smg_standard_rf") && !akFinal.Any(a => a.Name == "au_smg_standard_rf")
                && !akFinal.Any(a => a.Name.StartsWith("au_smg_standard_holo")) && !akFinal.Any(a => a.Name.StartsWith("au_smg_standard_quickdraw")));
            Check($"placement: after that save nothing is left in the session (chip '{vm.SessionStateText}')", vm.SessionEditCount == 0);
            Restart();
            Check($"placement: … nor after a restart (chip '{vm.SessionStateText}', alert '{vm.AlertText}')", vm.SessionEditCount == 0 && !Unapplied());

            // ── Move, delete, then a save where the old GDT's swap fails after the new one's succeeded: the delete survives ──
            // Requests (and so swaps) go in catalog order; the old GDT is whichever of the two is swapped second.
            var service = (GdtSaveService)typeof(MainViewModel).GetProperty("SaveService", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(vm)!;
            var order = new List<string>();
            var oldGdt = db.Gdts.IndexOf(smg) < db.Gdts.IndexOf(ak) ? ak : smg;
            var newGdt = oldGdt == ak ? smg : ak;
            var oldPath = oldGdt == ak ? akPath : smgPath;
            var newPath = oldGdt == ak ? smgPath : akPath;
            // Something nothing references (no derivers, a weapon or attachment), so the delete goes ahead.
            var victim = oldGdt.Assets.First(a => a.Type is "bulletweapon" or "attachmentunique" && a.Properties.Count > 0
                && !db.Assets.Any(d => string.Equals(d.Parent, a.Name, StringComparison.OrdinalIgnoreCase))
                && a.Name is "ar_ak47_h1_zm" or "au_smg_standard_gmod7" or "au_smg_standard_extclip");
            var victimName = victim.Name;
            vm.MoveAssetsInto(new[] { victim }, newGdt);
            vm.OpenByName(victimName);
            Pump();
            vm.DeleteActiveCommand.Execute(null);
            WaitUntil(() => !vm.IsDeletePending, 5_000);
            Pump();
            Check($"placement: the moved asset {victimName} was deleted", !db.Assets.Any(a => a.Name == victimName));
            var newEdit = newGdt.Assets.First(a => a.Parent is null && a.Properties.Count > 0);
            vm.OpenByName(newEdit.Name);
            Pump();
            var newRowEdit = vm.ActiveTab!.AllSentinel.All.OfType<TextPropertyViewModel>().First(p => !p.IsRuleHidden && !p.IsRuleDisabled);
            newRowEdit.RawValue += "_x";
            var oldBytes = File.ReadAllBytes(oldPath);
            FileStream? held = null;
            service.Hooks.BeforeSwap = path =>
            {
                order.Add(Path.GetFileName(path));
                if (path.Equals(oldPath, StringComparison.OrdinalIgnoreCase))
                    held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            };
            try
            {
                window.KeyPress(Avalonia.Input.Key.S, RawInputModifiers.Control, PhysicalKey.S, "s");
                WaitUntil(() => !vm.IsSaveRunning, 15_000);
                Pump();
            }
            finally
            {
                service.Hooks.BeforeSwap = null;
                held?.Dispose();
            }
            Check($"placement: the save wrote the new GDT and failed on the old one (swaps {string.Join(", ", order)}; '{vm.AlertText}')",
                vm.IsAlertOpen && File.ReadAllBytes(oldPath).AsSpan().SequenceEqual(oldBytes)
                && ValueOnDisk(newPath, newEdit.Name, newRowEdit.Key) == newRowEdit.RawValue);
            vm.DismissAlertCommand.Execute(null);
            Restart();
            Check($"placement: after a restart the deleted asset is still deleted, its delete still to save ({vm.SessionEditCount} changes, alert '{vm.AlertText}')",
                !db.Assets.Any(a => a.Name == victimName) && vm.SessionEditCount > 0 && !Unapplied());
            window.KeyPress(Avalonia.Input.Key.S, RawInputModifiers.Control, PhysicalKey.S, "s");
            WaitUntil(() => !vm.IsSaveRunning, 10_000);
            Pump();
            Check($"placement: saving then removes it from the old GDT, nothing left ('{vm.Status}', chip '{vm.SessionStateText}')",
                !GdtLayout.Scan(File.ReadAllBytes(oldPath)).Assets.Any(a => a.Name == victimName)
                && !GdtLayout.Scan(File.ReadAllBytes(newPath)).Assets.Any(a => a.Name == victimName) && vm.SessionEditCount == 0);
            var grip2 = db.Assets.Single(a => a.Name == "au_smg_standard_grip");

            // The context menu lists the new items for an asset row (real right-click).
            vm.RevealInTree(grip2);
            Pump();
            var akRow = vm.FlatRows.First(n => n.Asset == grip2 && !n.IsPinnedEntry);
            Click(window, RowContainer(tree = window.GetVisualDescendants().OfType<AssetBrowserView>().First().FindControl<ListBox>("Tree")!, vm, akRow), MouseButton.Right);
            var menu = tree.ContextMenu!;
            string[] want = { "MenuCutAssets", "MenuCopyAssets", "MenuDuplicateTo", "MenuMoveTo", "MenuDerive", "MenuUnderive" };
            var visible = menu.Items.OfType<MenuItem>().Where(i => i.IsVisible).Select(i => i.Name).ToList();
            Check($"placement: an asset row's menu has Cut, Copy, Duplicate to…, Move to…, Derive and Underive ({string.Join(", ", visible)})",
                menu.IsOpen && want.All(visible.Contains));
            Capture(window, Path.Combine(outDir, "65-placement-menu.png"));
            menu.Close();
            Pump();
            Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
            Click(window, RowContainer(tree, vm, akRow), MouseButton.Right);
            Capture(window, Path.Combine(outDir, "65b-light-placement-menu.png"));
            tree.ContextMenu!.Close();
            Application.Current.RequestedThemeVariant = ThemeVariant.Dark;
            Pump();
        }
        catch (Exception ex)
        {
            Check($"placement: {ex}", false);
        }
        finally
        {
            window?.Close();
            vm?.Dispose();
            Environment.SetEnvironmentVariable("APEX_FORCE_MOCK", saved.Mock);
            Environment.SetEnvironmentVariable("APEX_BO3_ROOT", saved.Root);
            Environment.SetEnvironmentVariable("APEX_NO_PERSIST", saved.Persist);
            SchemaRegistry.ResetToMock();
            try { Directory.Delete(install, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>The window's shell, except that the clipboard can't be read (another program holding it open).</summary>
    private sealed class UnreadableClipboard(Apex.Editor.Commands.IShellView inner) : Apex.Editor.Commands.IShellView
    {
        public void FocusAssetSearch() => inner.FocusAssetSearch();
        public void FocusPropertyFilter() => inner.FocusPropertyFilter();
        public void OpenAddProperty() => inner.OpenAddProperty();
        public void ShowAllTabs() => inner.ShowAllTabs();
        public void CyclePaneFocus(int delta) => inner.CyclePaneFocus(delta);
        public void FramePreview() => inner.FramePreview();
        public void CopyText(string text) => inner.CopyText(text);
        public void ShowAbout() => inner.ShowAbout();
        public System.Threading.Tasks.Task CopyTextAsync(string text) => inner.CopyTextAsync(text);
        public System.Threading.Tasks.Task<(bool Read, string? Text)> GetClipboardTextAsync() =>
            System.Threading.Tasks.Task.FromResult((false, (string?)null));
    }

    /// <summary>F2's rename on <paramref name="rec"/>, committed once its reference check is in.</summary>
    private static void Rename(MainViewModel vm, AssetRecord rec, string name)
    {
        vm.OpenByName(rec.Name);
        Pump();
        vm.OpenRenameCommand.Execute(null);
        vm.ActiveTab!.RenameText = name;
        vm.CommitRenameCommand.Execute(null);
        WaitUntil(() => !vm.IsRenameOpen, 5_000);
        Pump();
    }

    /// <summary>
    /// What the game sees for <paramref name="rec"/>, worked out here independently of the app: its own value, else the
    /// nearest ancestor's, else the schema default, for every key any of them has.
    /// </summary>
    private static Dictionary<string, string> Effective(AssetRecord rec, AssetDatabase db)
    {
        var values = new Dictionary<string, string>(rec.Properties, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { rec.Name };
        for (var cur = rec; cur.Parent is { } p && seen.Add(p);)
        {
            cur = db.Assets.First(a => a.Name.Equals(p, StringComparison.OrdinalIgnoreCase));
            foreach (var (k, v) in cur.ScanProperties)
                values.TryAdd(k, v);
        }
        if (SchemaRegistry.Get(rec.Type) is { } schema)
            foreach (var def in schema.Properties)
                values.TryAdd(def.Key, def.Default);
        return values;
    }
}
