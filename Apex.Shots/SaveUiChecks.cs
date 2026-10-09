using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Apex.Editor.Models;
using Apex.Editor.Services.Gdt;
using Apex.Editor.Services.Save;
using Apex.Editor.ViewModels;
using Apex.Editor.Views;
using GdtEncoding = Apex.Render.Data.Gdt.GdtEncoding;

namespace Apex.Shots;

/// <summary>
/// Saving in the real app, with real input: Apex runs live against a temp install (deffiles and a few real GDTs
/// copied under the write root), and Ctrl+S, the conflict dialog and a locked file are driven by key presses and
/// clicks on the actual controls. Nothing here touches the BO3 install.
/// </summary>
public partial class Program
{
    private static void RunSaveUiChecks(string outDir)
    {
        var install = NewScratch("live-install");
        CopyTree(Path.Combine(InstallRoot, "deffiles"), Path.Combine(install, "deffiles"));
        var weaponRel = @"source_data\ar_ak47_h1.gdt";
        var bigRel = @"source_data\ww2_paris_assets.gdt";
        foreach (var rel in new[] { weaponRel, bigRel })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(install, rel))!);
            File.Copy(Path.Combine(InstallRoot, rel), Path.Combine(install, rel));
        }
        var weaponPath = Path.Combine(install, weaponRel);
        var bigPath = Path.Combine(install, bigRel);

        var saved = (Mock: Environment.GetEnvironmentVariable("APEX_FORCE_MOCK"), Root: Environment.GetEnvironmentVariable("APEX_BO3_ROOT"),
            Persist: Environment.GetEnvironmentVariable("APEX_NO_PERSIST"));
        Environment.SetEnvironmentVariable("APEX_FORCE_MOCK", null);
        Environment.SetEnvironmentVariable("APEX_BO3_ROOT", install);
        Environment.SetEnvironmentVariable("APEX_NO_PERSIST", "1");
        MainViewModel? vm = null;
        MainWindow? window = null;
        try
        {
            vm = new MainViewModel(Path.Combine(install, "session"));
            window = ShowJournalWindow(vm);
            WaitUntil(() => vm.Status.StartsWith("Loaded"), 60_000);
            Check($"save ui: Apex loaded the temp install live ('{vm.Status}')", vm.Status.StartsWith("Loaded") && !vm.IsMockData);

            var weapon = FindRecord(vm, "ar_ak47_h1.gdt", "bulletweapon");
            Check($"save ui: a real weapon to edit ({weapon?.Name})", weapon is not null);
            if (weapon is null)
                return;

            // ── Ctrl+S with an edit: written, marks cleared, status says what ──
            vm.OpenByName(weapon.Name);
            Pump(500);
            window.UpdateLayout();
            Pump(100);
            var before = File.ReadAllBytes(weaponPath);
            var oldDamage = weapon.Properties["damage"];
            FocusNumber(window, vm, "damage");
            Key(window, Avalonia.Input.Key.Up, RawInputModifiers.Control);
            var newDamage = weapon.Properties["damage"];
            Check($"save ui: Ctrl+↑ edited damage ({oldDamage} → {newDamage}), chip '{vm.SessionStateText}'",
                newDamage != oldDamage && vm.SessionEditCount == 1 && vm.SessionStateText.Contains("not in GDTs yet"));
            window.KeyPress(Avalonia.Input.Key.S, RawInputModifiers.Control, PhysicalKey.S, "s");
            WaitUntil(() => !vm.IsSaveRunning, 10_000);
            Pump();
            var after = File.ReadAllBytes(weaponPath);
            var expected = WithValue(before, weapon.Name, "damage", newDamage);
            Check($"save ui: Ctrl+S wrote the one value ('{vm.Status}')",
                after.AsSpan().SequenceEqual(expected) && vm.Status == "Saved 1 asset in 1 GDT");
            Check($"save ui: marks cleared (chip '{vm.SessionStateText}', tab changes {vm.ActiveTab?.Changes.Count})",
                vm.SessionEditCount == 0 && vm.SessionStateText == "No changes" && vm.ActiveTab?.Changes.Count == 0 && !weapon.HasSessionEdits);
            Check("save ui: a backup of the previous version was kept",
                new BackupStore(BackupStore.DefaultRoot).List(weaponPath) is { Count: 1 } b && File.ReadAllBytes(b[0]).AsSpan().SequenceEqual(before));
            var timings = vm.LastSaveTimings;
            Console.WriteLine($"info  save ui: small GDT save — plan {timings.PlanMs:0.0} ms + commit {timings.CommitMs:0.0} ms on the UI thread, {timings.WriteMs:0} ms of I/O off it");

            // The watcher ignores Apex's own write but still sees another program's.
            Pump(1500);
            Check($"save ui: the watcher ignores Apex's own save ('{vm.Status}')", vm.Status == "Saved 1 asset in 1 GDT");

            // Undo still works in-session, and saves back.
            Check("save ui: undo still works after a save", vm.ActiveTab!.CanUndo);
            vm.ActiveTab.UndoCommand.Execute(null);
            Check($"save ui: undo brings the change back as unsaved ({vm.SessionStateText})",
                weapon.Properties["damage"] == oldDamage && vm.SessionEditCount == 1);
            window.KeyPress(Avalonia.Input.Key.S, RawInputModifiers.Control, PhysicalKey.S, "s");
            WaitUntil(() => !vm.IsSaveRunning, 10_000);
            Pump();
            Check("save ui: saving the undo puts the file back byte for byte", File.ReadAllBytes(weaponPath).AsSpan().SequenceEqual(before));

            // Another program changes an asset nobody edited: the watcher reloads it.
            var other = FindRecord(vm, "ar_ak47_h1.gdt", "bulletweapon", skip: weapon);
            if (other is not null)
            {
                var external = Encoding.Latin1.GetString(File.ReadAllBytes(weaponPath)).Replace("\"displayName\" \"", "\"displayName\" \"X");
                File.WriteAllText(weaponPath, external, Encoding.Latin1);
                WaitUntil(() => vm.Status.StartsWith("Reloaded"), 5_000);
                Check($"save ui: another program's change is still picked up by the watcher ('{vm.Status}')", vm.Status.StartsWith("Reloaded"));
                before = File.ReadAllBytes(weaponPath);
            }

            // ── Conflict: the same value changed on disk; keep mine with a real click on Save ──
            vm.OpenByName(weapon.Name);
            var tab = vm.ActiveTab!;
            tab.AllSentinel.All.First(p => p.Key == "damage").RawValue = "111";
            File.WriteAllBytes(weaponPath, WithValue(File.ReadAllBytes(weaponPath), weapon.Name, "damage", "222"));
            window.KeyPress(Avalonia.Input.Key.S, RawInputModifiers.Control, PhysicalKey.S, "s");
            WaitUntil(() => !vm.IsSaveRunning, 10_000);
            Pump();
            var item = vm.ConflictItems.FirstOrDefault();
            Check($"save ui: a conflicting change on disk opens the conflict dialog ('{vm.ConflictSummary}' · {item?.Lines.FirstOrDefault()})",
                vm.IsConflictOpen && item is { KeepMine: true } && item.Asset == weapon.Name && item.Lines.Any(l => l.Contains("yours 111") && l.Contains("file 222"))
                && ValueOnDisk(weaponPath, weapon.Name, "damage") == "222");
            Capture(window, Path.Combine(outDir, "50-save-conflict.png"));
            ClickNamed(window, "ConflictSaveButton");
            WaitUntil(() => !vm.IsSaveRunning && !vm.IsConflictOpen, 10_000);
            Pump();
            Check($"save ui: Keep mine writes yours over the file's ('{vm.Status}')",
                !vm.IsConflictOpen && ValueOnDisk(weaponPath, weapon.Name, "damage") == "111" && vm.SessionEditCount == 0);

            // Take the file's: a real click on the pill, then Save.
            tab.AllSentinel.All.First(p => p.Key == "damage").RawValue = "333";
            File.WriteAllBytes(weaponPath, WithValue(File.ReadAllBytes(weaponPath), weapon.Name, "damage", "444"));
            window.KeyPress(Avalonia.Input.Key.S, RawInputModifiers.Control, PhysicalKey.S, "s");
            WaitUntil(() => !vm.IsSaveRunning, 10_000);
            Pump();
            var pill = window.GetVisualDescendants().OfType<ToggleButton>().FirstOrDefault(t => t.Name == "TakeFilePill" && t.IsEffectivelyVisible);
            if (pill is not null)
                Click(window, pill);
            Check("save ui: clicking “Take the file's” picks it", vm.ConflictItems.FirstOrDefault() is { KeepMine: false });
            ClickNamed(window, "ConflictSaveButton");
            WaitUntil(() => !vm.IsSaveRunning && !vm.IsConflictOpen, 10_000);
            Pump();
            Check($"save ui: Take the file's keeps the file's value and drops yours ({weapon.Properties["damage"]}, chip '{vm.SessionStateText}')",
                ValueOnDisk(weaponPath, weapon.Name, "damage") == "444" && weapon.Properties["damage"] == "444"
                && vm.SessionEditCount == 0 && tab.AllSentinel.All.First(p => p.Key == "damage").RawValue == "444");

            // ── Locked: open for writing elsewhere → banner with Retry; the file untouched; Retry saves ──
            tab.AllSentinel.All.First(p => p.Key == "damage").RawValue = "555";
            var lockedBefore = File.ReadAllBytes(weaponPath);
            var focusBefore = window.FocusManager?.GetFocusedElement()?.GetType().Name ?? "none";
            using (new FileStream(weaponPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
            {
                window.KeyPress(Avalonia.Input.Key.S, RawInputModifiers.Control, PhysicalKey.S, "s");
                WaitUntil(() => !vm.IsSaveRunning, 10_000);
                Pump();
                Check($"save ui: a locked GDT raises a banner with Retry ('{vm.AlertText}'), file untouched [edits {vm.SessionEditCount}, damage {weapon.Properties["damage"]}, conflict {vm.IsConflictOpen}, running {vm.IsSaveRunning}, status '{vm.Status}', tab {vm.ActiveTab?.Name}, focus {focusBefore}, modal {vm.IsModalOpen}, palette {vm.IsPaletteOpen}]",
                    vm.IsAlertOpen && vm.AlertIsError && vm.HasAlertAction && vm.AlertActionLabel == "Retry"
                    && vm.AlertText.Contains("open in another program") && vm.SessionEditCount == 1);
                Capture(window, Path.Combine(outDir, "51-save-locked.png"));
            }
            Check("save ui: nothing was written while locked", File.ReadAllBytes(weaponPath).AsSpan().SequenceEqual(lockedBefore));
            var retry = window.GetVisualDescendants().OfType<Button>()
                .FirstOrDefault(b => b.Name == "AlertAction" && b.DataContext is AlertItem { ActionLabel: "Retry" } && b.IsEffectivelyVisible);
            if (retry is not null)
                Click(window, retry);
            WaitUntil(() => !vm.IsSaveRunning, 10_000);
            Pump();
            Check($"save ui: Retry saves once the file is free ('{vm.Status}') [retry button {retry is not null}, alert {vm.IsAlertOpen} '{vm.AlertText}', running {vm.IsSaveRunning}, edits {vm.SessionEditCount}, damage {weapon.Properties["damage"]}, baseline {weapon.SessionBaseline?["damage"]}, disk {ValueOnDisk(weaponPath, weapon.Name, "damage")}]",
                ValueOnDisk(weaponPath, weapon.Name, "damage") == "555" && vm.SessionEditCount == 0 && !vm.IsAlertOpen);

            // ── Restore the previous version (palette command) ──
            vm.OpenCommandPaletteCommand.Execute(null);
            vm.PaletteText = ">restore previous";
            var listed = vm.PaletteResults.Any(r => r.CommandId == Apex.Editor.Commands.CommandCatalog.RestorePrevious);
            vm.ConfirmPaletteCommand.Execute(null);
            WaitUntil(() => !vm.IsSaveRunning && vm.Status.StartsWith("Restored"), 10_000);
            Pump();
            Check($"save ui: “Restore previous version” is in the palette and puts the last version back ('{vm.Status}')",
                listed && ValueOnDisk(weaponPath, weapon.Name, "damage") == "444" && weapon.Properties["damage"] == "444"
                && vm.SessionEditCount == 0);

            // ── Perf: one edit in the largest GDT (~11 MB) ──
            var big = new[] { "xmodel", "material", "image" }.Select(t => FindRecord(vm, "ww2_paris_assets.gdt", t)).FirstOrDefault(r => r is not null);
            if (big is not null)
            {
                vm.OpenByName(big.Name);
                var row = vm.ActiveTab!.AllSentinel.All.OfType<TextPropertyViewModel>().FirstOrDefault(p => !p.IsRuleHidden && !p.IsRuleDisabled);
                if (row is not null)
                {
                    row.RawValue = row.RawValue + "_x";
                    var sw = Stopwatch.StartNew();
                    window.KeyPress(Avalonia.Input.Key.S, RawInputModifiers.Control, PhysicalKey.S, "s");
                    WaitUntil(() => !vm.IsSaveRunning, 20_000);
                    Pump();
                    var t = vm.LastSaveTimings;
                    Console.WriteLine($"info  save ui: one edit in {bigRel} ({new FileInfo(bigPath).Length / 1048576.0:0.0} MB): "
                                      + $"plan {t.PlanMs:0.0} ms + commit {t.CommitMs:0.0} ms on the UI thread; "
                                      + $"{t.WriteMs:0} ms of I/O off it; total {sw.ElapsedMilliseconds} ms");
                    Gate($"save ui: saving the largest GDT holds the UI thread under a frame (plan {t.PlanMs:0.0} + commit {t.CommitMs:0.0} ms, '{vm.Status}')",
                        vm.Status == "Saved 1 asset in 1 GDT", t.PlanMs + t.CommitMs < 16);
                }
            }
        }
        catch (Exception ex)
        {
            Check($"save ui: {ex}", false);
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

    private static AssetRecord? FindRecord(MainViewModel vm, string gdtFile, string? type, AssetRecord? skip = null)
    {
        vm.FilterText = $"gdt:{Path.GetFileNameWithoutExtension(gdtFile)}" + (type is null ? "" : $" type:{type}");
        vm.ApplyFilterNow();
        WaitUntil(() => vm.FlatRows.Any(n => n.Asset is not null), 5_000);
        var found = vm.FlatRows.Select(n => n.Asset).FirstOrDefault(a => a is not null && a != skip && a.Parent is null && a.Properties.Count > 0);
        vm.FilterText = "";
        vm.ApplyFilterNow();
        return found;
    }

    /// <summary>What the loader reads for <paramref name="asset"/> · <paramref name="key"/> in the file now.</summary>
    private static string? ValueOnDisk(string path, string asset, string key)
    {
        var bytes = File.ReadAllBytes(path);
        var e = Apex.Editor.Services.Gdt.GdtIndexer.Index(bytes).First(x => x.Name == asset);
        return Apex.Editor.Services.Gdt.GdtParser.ParseProperties(bytes, e.BodyOffset, e.BodyLength).GetValueOrDefault(key);
    }

    /// <summary>The file with one value of one asset replaced between its quotes (what another program would write).</summary>
    private static byte[] WithValue(byte[] bytes, string asset, string key, string value)
    {
        var span = GdtLayout.Scan(bytes).Assets.First(a => a.Name == asset);
        var prop = GdtLayout.ScanProps(bytes, span, out _).Last(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        var v = GdtEncoding.GetBytes(value);
        return bytes.AsSpan(0, prop.ValueStart).ToArray().Concat(v).Concat(bytes.AsSpan(prop.ValueEnd).ToArray()).ToArray();
    }

    private static void ClickNamed(Window window, string name)
    {
        Pump();
        var button = window.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Name == name && b.IsEffectivelyVisible);
        if (button is not null)
            Click(window, button);
    }

    /// <summary>Pumps the dispatcher (timers included) until <paramref name="done"/> holds or the time runs out.</summary>
    private static void WaitUntil(Func<bool> done, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (!done() && sw.ElapsedMilliseconds < timeoutMs)
            Pump(25);
    }

    private static void CopyTree(string from, string to)
    {
        foreach (var dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(dir.Replace(from, to));
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, file.Replace(from, to));
    }
}
