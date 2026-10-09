using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Apex.Editor.Controls;
using Apex.Editor.Services;
using Apex.Editor.Services.Session;
using Apex.Editor.ViewModels;
using Apex.Editor.Views;

namespace Apex.Shots;

/// <summary>
/// Hot exit and the crash net, driven the way a user meets them: real keystrokes into a number field, the
/// structural commands, then the window closed (or the process "killed") and a fresh view model built from the
/// journal. Every journal lives in a temp folder, never the real %LOCALAPPDATA%.
/// </summary>
public partial class Program
{
    private static void RunJournalChecks(string outDir)
    {
        var temp = Path.Combine(Path.GetTempPath(), "apex-journal-checks-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(temp);
        Environment.SetEnvironmentVariable("APEX_LOG_DIR", Path.Combine(temp, "logs"));
        try
        {
            RoundTrip(Path.Combine(temp, "roundtrip"), outDir);
            CrashAndTornRecord(Path.Combine(temp, "crash"), Path.Combine(temp, "torn"));
            ConflictAndMissing(Path.Combine(temp, "conflict"), Path.Combine(temp, "missing"));
            HandlerException(Path.Combine(temp, "handler"), outDir);
            KeystrokeCost(Path.Combine(temp, "perf"));
            if (Environment.GetEnvironmentVariable("APEX_JOURNAL_LIVE_PERF") == "1")
                LiveKeystrokeCost(Path.Combine(temp, "perf-live"));
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); }
            catch (IOException) { /* a journal still open in an abandoned view model */ }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static MainWindow ShowJournalWindow(MainViewModel vm)
    {
        var window = new MainWindow { DataContext = vm };
        window.Show();
        window.Activate();
        window.UpdateLayout();
        Pump();
        return window;
    }

    /// <summary>The number box for <paramref name="key"/> in the active editor, focused for keystrokes.</summary>
    private static ScrubNumberBox FocusNumber(Window window, MainViewModel vm, string key)
    {
        vm.ActiveTab!.RevealProperty(key);
        Pump();
        window.UpdateLayout();
        Pump();
        var box = window.GetVisualDescendants().OfType<ScrubNumberBox>()
            .First(b => (b.DataContext as NumberPropertyViewModel)?.Key == key && b.IsEffectivelyVisible);
        box.Focus();
        Pump();
        return box;
    }

    private static string? Value(MainViewModel vm, string asset, string key)
    {
        vm.OpenByName(asset, preview: true);
        return vm.ActiveTab?.Name == asset ? vm.ActiveTab.Record.Properties.GetValueOrDefault(key) : null;
    }

    private static bool Exists(MainViewModel vm, string asset)
    {
        vm.OpenByName(asset, preview: true);
        var found = vm.ActiveTab?.Name == asset;
        vm.DismissAlertCommand.Execute(null);
        return found;
    }

    private static string JournalPath(string root) =>
        Path.Combine(root, "mock", SessionJournal.FileName);

    // ── Edits → close → relaunch → the same session ─────────────────────────────────────────
    private static void RoundTrip(string root, string outDir)
    {
        var vm = new MainViewModel(root);
        var window = ShowJournalWindow(vm);
        Check("journal: the session is kept", vm.IsSessionKept && vm.SessionDirectory is not null);

        // Real keystrokes: Ctrl+↑ three times in the damage box.
        vm.OpenByName("wpn_snp_ballista");
        var damageBefore = vm.ActiveTab!.Record.Properties.GetValueOrDefault("damage");
        FocusNumber(window, vm, "damage");
        for (var i = 0; i < 3; i++)
            Key(window, Avalonia.Input.Key.Up, RawInputModifiers.Control);
        var damageAfter = vm.ActiveTab!.Record.Properties.GetValueOrDefault("damage");
        Check($"journal: Ctrl+↑ in the number box changed damage ({damageBefore} → {damageAfter})", damageAfter != damageBefore);
        vm.ActiveTab.AllSentinel.All.First(p => p.Key == "displayName").RawValue = "Ballista of the journal";

        // Structural edits: rename, duplicate (and edit the copy), delete, new GDT, new asset in it.
        vm.OpenByName("wpn_ar_kestrel");
        vm.ActiveTab!.RenameText = "wpn_ar_falcon";
        vm.ActiveTab.RenameCommand.Execute(null);
        vm.DuplicateActiveCommand.Execute(null);
        var copyName = vm.ActiveTab!.Name;
        vm.ActiveTab.AllSentinel.All.First(p => p.Key == "clipSize").RawValue = "77";
        // Delete the first loaded asset nothing references (most mock weapons are referenced).
        string? doomed = null;
        foreach (var candidate in new[] { "zm_castle_barrier_01", "zm_castle_barrier_02", "wpn_snp_locus","wpn_ar_vireo", "wpn_smg_riot", "wpn_smg_needle", "wpn_ar_havoc", "mtl_marble_03" })
        {
            vm.OpenByName(candidate);
            vm.DeleteActiveCommand.Execute(null);
            if (!vm.IsDeletePending)
            {
                vm.DismissAlertCommand.Execute(null);
                continue;
            }
            WaitFor(() => !vm.IsDeletePending);
            if (vm.Status.StartsWith("Deleted"))
            {
                doomed = candidate;
                break;
            }
            vm.DismissAlertCommand.Execute(null);
        }
        var deleted = doomed is not null;
        vm.NewGdtCommand.Execute(null);
        vm.NewName = "zm_journal";
        vm.CommitNewCommand.Execute(null);
        vm.NewAssetCommand.Execute(null);
        vm.NewName = "wpn_journal_new";
        vm.NewGdtName = "zm_journal.gdt";
        vm.NewType = vm.NewTypeChoices.FirstOrDefault(t => t == "weapon") ?? vm.NewType;
        Console.WriteLine($"info  journal: deleted {doomed ?? "nothing"}, new asset type {vm.NewType}");
        vm.CommitNewCommand.Execute(null);
        var created = vm.ActiveTab?.Name == "wpn_journal_new";
        if (created)
            vm.ActiveTab!.AllSentinel.All.First(p => p.Key == "damage").RawValue = "42";
        Check($"journal: structural edits made (rename, duplicate {copyName}, delete {deleted}, new GDT + asset {created})",
            copyName == "wpn_ar_falcon_copy" && deleted && created);

        vm.OpenByName("wpn_snp_ballista");
        var tabs = vm.OpenTabs.Select(t => t.Name).ToList();
        var active = vm.ActiveTab!.Name;
        var count = vm.SessionEditCount;
        Check($"journal: chip says where the changes are ('{vm.SessionStateText}')", vm.SessionStateText.EndsWith("not in GDTs yet"));

        // Close: no question asked, and the journal says the run ended normally.
        var closed = false;
        Check("journal: closing with changes doesn't ask", vm.RequestClose(() => closed = true) && !vm.IsConfirmOpen && !closed);
        vm.Dispose();
        window.Close();
        Pump();
        var onDisk = SessionJournal.Read(JournalPath(root));
        Check($"journal: {onDisk.Entries.Count} records on disk, closed cleanly", onDisk.EndedCleanly && onDisk.DamagedRegions == 0);

        // Relaunch from the journal.
        var vm2 = new MainViewModel(root);
        var window2 = ShowJournalWindow(vm2);
        Pump(50);
        var restoredStatus = vm2.Status;
        Check($"restore: tabs back in order, same active ({string.Join(",", vm2.OpenTabs.Select(t => t.Name))} · {vm2.ActiveTab?.Name})",
            vm2.OpenTabs.Select(t => t.Name).SequenceEqual(tabs) && vm2.ActiveTab?.Name == active);
        Check($"restore: same change count ({vm2.SessionEditCount} vs {count}) and says so ('{restoredStatus}')",
            vm2.SessionEditCount == count && restoredStatus.StartsWith("Restored") && restoredStatus.Contains("last session"));
        Check("restore: the keystroke edits are back",
            Value(vm2, "wpn_snp_ballista", "damage") == damageAfter
            && Value(vm2, "wpn_snp_ballista", "displayName") == "Ballista of the journal");
        Check("restore: rename, duplicate with its edit, delete",
            Value(vm2, "wpn_ar_falcon_copy", "clipSize") == "77"
            && vm2.FlatRows.Count > 0
            && Value(vm2, "wpn_ar_falcon", "damage") is not null
            && Value(vm2, "wpn_ar_kestrel", "damage") is null
            && doomed is not null && !Exists(vm2, doomed) && vm2.OpenTabs.All(t => t.Name != doomed));
        vm2.DismissAlertCommand.Execute(null);
        Check("restore: new GDT and its new asset",
            vm2.GdtOptions.Count >= 0 && Value(vm2, "wpn_journal_new", "damage") == "42"
            && vm2.ActiveTab?.GdtName == "zm_journal.gdt");

        // Undo all still works on a restored asset (against the baseline, not history).
        vm2.OpenByName("wpn_snp_ballista");
        Check("restore: Ctrl+Z history starts empty", !vm2.ActiveTab!.CanUndo);
        vm2.UndoAllChangesCommand.Execute(null);
        Check("restore: Undo all puts a restored asset back",
            vm2.ActiveTab.Record.Properties.GetValueOrDefault("damage") == damageBefore);
        vm2.UndoActiveCommand.Execute(null);

        // Discard all: asks, then puts everything back and empties the journal.
        for (var guard = 0; vm2.OpenTabs.Count > 0 && guard < 50; guard++)
            vm2.CloseActiveTabCommand.Execute(null);
        Pump();
        var discard = window2.GetVisualDescendants().OfType<Button>().First(b => b.Name == "DiscardSessionButton");
        Capture(window2, Path.Combine(outDir, "40-start-page-kept.png"));
        Click(window2, discard, MouseButton.Left);
        Check($"discard: the start page button asks first ('{vm2.ConfirmTitle}')", vm2.IsConfirmOpen && vm2.ConfirmTitle == "Discard all changes?");
        Capture(window2, Path.Combine(outDir, "41-discard-confirm.png"));
        vm2.AcceptConfirmCommand.Execute(null);
        Check($"discard: nothing left ({vm2.SessionEditCount}, '{vm2.Status}')", vm2.SessionEditCount == 0 && !vm2.HasSessionChanges);
        Check("discard: structural edits reversed",
            Value(vm2, "wpn_ar_kestrel", "damage") is not null && Value(vm2, "wpn_ar_falcon", "damage") is null
            && Value(vm2, "wpn_ar_falcon_copy", "damage") is null && doomed is not null && Exists(vm2, doomed)
            && Value(vm2, "wpn_journal_new", "damage") is null
            && Value(vm2, "wpn_snp_ballista", "damage") == damageBefore);
        vm2.DismissAlertCommand.Execute(null);
        vm2.Dispose();
        window2.Close();
        Pump();

        var vm3 = new MainViewModel(root);
        Check($"discard: a relaunch restores nothing ({vm3.SessionEditCount})", vm3.SessionEditCount == 0 && !vm3.Status.StartsWith("Restored"));
        vm3.Dispose();
    }

    // ── Killed mid-session, and a torn last record ─────────────────────────────────────────
    private static void CrashAndTornRecord(string root, string tornRoot)
    {
        var vm = new MainViewModel(root);
        var window = ShowJournalWindow(vm);
        vm.OpenByName("wpn_snp_ballista");
        FocusNumber(window, vm, "damage");
        Key(window, Avalonia.Input.Key.Up, RawInputModifiers.Control);
        var ballista = vm.ActiveTab!.Record.Properties["damage"];
        vm.OpenByName("wpn_smg_wasp");
        vm.ActiveTab!.AllSentinel.All.First(p => p.Key == "clipSize").RawValue = "31";
        vm.OpenByName("wpn_ar_vireo");
        Pump(400); // the journal's own delay writes these, no explicit flush
        vm.ActiveTab!.AllSentinel.All.First(p => p.Key == "damage").RawValue = "777";
        Pump(400);

        // "Kill": copy the journal as it is on disk now, never disposing the view model.
        byte[] bytes;
        using (var s = new FileStream(JournalPath(root), FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            bytes = new byte[s.Length];
            s.ReadExactly(bytes);
        }
        var full = SessionJournal.Parse(bytes);
        var last = full.Entries.LastOrDefault();
        Check($"crash: the last record on disk is vireo's edit ({last?.Op} {last?.Name})", last is { Op: "set", Name: "wpn_ar_vireo" });

        // Tear it: the last 5 bytes never reached the disk.
        var torn = bytes[..^5];
        var parsed = SessionJournal.Parse(torn);
        Check($"torn: the reader skips the torn record and keeps {parsed.Entries.Count} of {full.Entries.Count}",
            parsed.Entries.Count == full.Entries.Count - 1 && parsed.DamagedRegions == 1);
        Directory.CreateDirectory(Path.GetDirectoryName(JournalPath(tornRoot))!);
        File.WriteAllBytes(JournalPath(tornRoot), torn);
        window.Close();

        var vm2 = new MainViewModel(tornRoot);
        Check($"torn: earlier edits restored after the crash ('{vm2.Status}')",
            vm2.Status.Contains("closed unexpectedly")
            && Value(vm2, "wpn_snp_ballista", "damage") == ballista && Value(vm2, "wpn_smg_wasp", "clipSize") == "31");
        Check("torn: the torn record's edit is the only one missing", Value(vm2, "wpn_ar_vireo", "damage") != "777");
        vm2.FlushSessionToDisk();
        Check("torn: the journal is rewritten without the damage", SessionJournal.Read(JournalPath(tornRoot)).DamagedRegions == 0);
        vm2.Dispose();
    }

    // ── The GDT changed under an edit; an asset is gone ─────────────────────────────────────
    private static void ConflictAndMissing(string conflictRoot, string missingRoot)
    {
        var probe = new MainViewModel(sessionRoot: null);
        var disk = Value(probe, "wpn_snp_ballista", "damage");

        WriteJournal(conflictRoot,
            new JournalEntry { Op = "set", Gdt = "t7_weapons.gdt", Name = "wpn_snp_ballista",
                Changes = new() { new JournalChange { Key = "damage", Old = "1", New = "555" } } });
        var vm = new MainViewModel(conflictRoot);
        Check($"conflict: the user's value wins over the GDT's {disk}", Value(vm, "wpn_snp_ballista", "damage") == "555");
        Check($"conflict: said plainly, detail in the tooltip ('{vm.AlertText}')",
            vm.IsAlertOpen && !vm.AlertIsError && vm.AlertText.Contains("changed in its GDT") && vm.AlertText.Contains("Your value is kept")
            && vm.AlertDetail?.Contains("wpn_snp_ballista · damage") == true && vm.HasAlertAction);
        vm.Dispose();

        WriteJournal(missingRoot,
            new JournalEntry { Op = "set", Gdt = "t7_weapons.gdt", Name = "ghost_asset",
                Changes = new() { new JournalChange { Key = "damage", Old = "10", New = "20" } } },
            new JournalEntry { Op = "set", Gdt = "t7_weapons.gdt", Name = "wpn_smg_wasp",
                Changes = new() { new JournalChange { Key = "clipSize", Old = null, New = "44" } } });
        var vm2 = new MainViewModel(missingRoot);
        Check($"missing: the asset that's gone is named, not dropped ('{vm2.AlertText}')",
            vm2.IsAlertOpen && vm2.AlertText.Contains("ghost_asset") && vm2.AlertText.Contains("tries again next launch"));
        Check("missing: the rest still restores", Value(vm2, "wpn_smg_wasp", "clipSize") == "44");
        vm2.FlushSessionToDisk();
        Check("missing: its change stays in the journal",
            SessionJournal.Read(JournalPath(missingRoot)).Entries.Any(e => e.Op == "set" && e.Name == "ghost_asset"));
        vm2.Dispose();
    }

    private static void WriteJournal(string root, params JournalEntry[] entries)
    {
        var journal = SessionJournal.Open(Path.Combine(root, "mock"), out _, out _)!;
        journal.Append(new JournalEntry { Op = "head", Version = SessionJournal.FormatVersion, Env = "mock" });
        journal.Append(entries);
        journal.Flush(TimeSpan.FromSeconds(3));
        journal.Dispose();
    }

    // ── An exception in a UI handler ────────────────────────────────────────────────────────
    private static void HandlerException(string root, string outDir)
    {
        CrashGuard.Install();
        var vm = new MainViewModel(root);
        CrashGuard.FlushSession = vm.FlushForCrash;
        CrashGuard.Recovered = vm.ReportRecovered;
        var window = ShowJournalWindow(vm);
        vm.OpenByName("wpn_snp_ballista");
        FocusNumber(window, vm, "damage");
        // A step down and back first, journaled, so the step below and the read after it run warm: run cold (the group
        // alone, or under load) they took longer than the journal's delay, which wrote the edit before the crash could.
        Key(window, Avalonia.Input.Key.Down, RawInputModifiers.Control);
        Key(window, Avalonia.Input.Key.Up, RawInputModifiers.Control);
        Pump(400);
        SessionJournal.Read(JournalPath(root));
        Key(window, Avalonia.Input.Key.Up, RawInputModifiers.Control);
        var edited = vm.ActiveTab!.Record.Properties["damage"];
        // Not flushed yet: the journal's delay hasn't passed.
        var before = SessionJournal.Read(JournalPath(root));
        var pending = !before.Entries.Any(e => e.Op == "set" && e.Changes!.Any(c => c.Key == "damage" && c.New == edited));

        // A posted handler (timer tick, continuation, async void) throws.
        var escaped = false;
        Dispatcher.UIThread.Post(() => throw new InvalidOperationException("boom from a handler"));
        try { Dispatcher.UIThread.RunJobs(); }
        catch (Exception) { escaped = true; }
        Check("crash net: a UI-thread handler exception doesn't take the app down", !escaped);
        var after = SessionJournal.Read(JournalPath(root));
        Check("crash net: it flushed the edit that was still waiting for the journal",
            pending && after.Entries.Any(e => e.Op == "set" && e.Name == "wpn_snp_ballista"
                && e.Changes!.Any(c => c.Key == "damage" && c.New == edited)));
        Check($"crash net: one plain line, no raw message ('{vm.AlertText}')",
            vm.IsAlertOpen && !vm.AlertText.Contains("boom") && vm.AlertText.Contains("Your changes are kept")
            && vm.AlertDetail?.Contains("boom from a handler") == true);
        Check($"crash net: crash log written ({CrashGuard.LastLogPath})",
            CrashGuard.LastLogPath is { } log && File.Exists(log) && File.ReadAllText(log).Contains("boom from a handler")
            && log.StartsWith(Environment.GetEnvironmentVariable("APEX_LOG_DIR")!));
        Capture(window, Path.Combine(outDir, "42-recovered-error.png"));
        vm.DismissAlertCommand.Execute(null);

        // A click whose handler throws, with real input.
        var review = window.GetVisualDescendants().OfType<Button>().First(b => b.Command == vm.ReviewChangesCommand && b.IsEffectivelyVisible);
        void Throw(object? s, Avalonia.Interactivity.RoutedEventArgs e) => throw new InvalidOperationException("boom from a click");
        review.Click += Throw;
        escaped = false;
        try { Click(window, review, MouseButton.Left); }
        catch (Exception) { escaped = true; }
        review.Click -= Throw;
        Console.WriteLine($"info  crash net: a throwing click handler under headless input {(escaped ? "propagates to the input caller (Win32 routes it through the dispatcher; check by eye)" : "is caught by the dispatcher")}");

        // Still editing.
        FocusNumber(window, vm, "damage");
        Key(window, Avalonia.Input.Key.Up, RawInputModifiers.Control);
        Check("crash net: editing carries on afterwards", vm.ActiveTab!.Record.Properties["damage"] != edited);
        CrashGuard.FlushSession = null;
        CrashGuard.Recovered = null;
        vm.Dispose();
        window.Close();
    }

    /// <summary>
    /// APEX_JOURNAL_LIVE_PERF=1: the keystroke timing on the real install (122k assets, a real bulletweapon's form),
    /// journal off vs on, interleaved. Opt-in because it ingests the corpus twice.
    /// </summary>
    private static void LiveKeystrokeCost(string root)
    {
        var saved = Environment.GetEnvironmentVariable("APEX_FORCE_MOCK");
        Environment.SetEnvironmentVariable("APEX_FORCE_MOCK", null);
        var runs = new List<(string Label, MainViewModel Vm, MainWindow Window, List<double> Times)>();
        try
        {
            foreach (var (label, r) in new[] { ("journal off", (string?)null), ("journal on", (string?)root) })
            {
                var vm = new MainViewModel(r);
                runs.Add((label, vm, ShowJournalWindow(vm), new List<double>()));
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("APEX_FORCE_MOCK", saved);
        }
        if (runs.Any(r => r.Vm.IsMockData))
        {
            Console.WriteLine("perf  live keystroke: no BO3 install, skipped");
            return;
        }
        for (var i = 0; i < 3000 && runs.Any(r => !r.Vm.Status.StartsWith("Loaded")); i++)
        {
            System.Threading.Thread.Sleep(20);
            Dispatcher.UIThread.RunJobs();
        }
        foreach (var run in runs)
        {
            run.Vm.PaletteText = "type:bulletweapon ar_standard";
            var pick = run.Vm.PaletteResults.FirstOrDefault(p => p.Record is not null)?.Record;
            if (pick is null)
            {
                Console.WriteLine("perf  live keystroke: no bulletweapon found, skipped");
                return;
            }
            run.Vm.OpenAsset(pick);
            Pump(300);
        }
        var sw = new Stopwatch();
        for (var i = 0; i < 220; i++)
            // Alternate which window goes first: whichever keys first each round measures faster.
            foreach (var run in i % 4 < 2 ? runs : Enumerable.Reverse(runs))
            {
                if (i == 0)
                    FocusNumber(run.Window, run.Vm, "damage");
                sw.Restart();
                Key(run.Window, i % 2 == 0 ? Avalonia.Input.Key.Up : Avalonia.Input.Key.Down, RawInputModifiers.Control);
                Dispatcher.UIThread.RunJobs();
                if (i >= 20)
                    run.Times.Add(sw.Elapsed.TotalMilliseconds);
            }
        static double Q(List<double> v, double q) => v.OrderBy(x => x).ElementAt(Math.Min(v.Count - 1, (int)(v.Count * q)));
        var sw2 = Stopwatch.StartNew();
        runs[1].Vm.FlushJournalNow();
        var tick = sw2.Elapsed.TotalMilliseconds;
        foreach (var run in runs)
            Console.WriteLine($"perf  keystroke (Ctrl+↑ in damage on {run.Vm.ActiveTab?.Name}, live install, headless) {run.Label}: "
                + $"median {Q(run.Times, 0.5):0.000} ms, p95 {Q(run.Times, 0.95):0.000} ms");
        Console.WriteLine($"perf  live journal flush tick: {tick:0.000} ms");
        foreach (var run in runs)
        {
            run.Vm.Dispose();
            run.Window.Close();
        }
        Pump();
    }

    /// <summary>
    /// Live only: a model that fails to prepare falls back for that asset alone, and a load that fails unexpectedly
    /// shows the pane's one-line error, with nothing escaping to the caller.
    /// </summary>
    private static void LivePreviewIsolation(Apex.Editor.Services.Gdt.GameEnvironment env, Apex.Editor.Models.AssetDatabase db)
    {
        var byName = db.Assets.GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        Apex.Editor.Models.AssetRecord? Resolve(string _, string name) => byName.GetValueOrDefault(name);

        var state = new ToolsGfxPreviewState();
        var escaped = false;
        var ok = true;
        try
        {
            ok = state.TryLoadAsync(env, Resolve, _ => throw new NullReferenceException("a converter bug"),
                System.Threading.CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception) { escaped = true; }
        Check($"live preview: a model that throws while preparing falls back ('{state.FallbackStatus}' · {state.FallbackDetail})",
            !escaped && !ok && state.FallbackStatus is not null);

        var broken = new Apex.Editor.Models.AssetRecord { Name = "apex_broken_model", Type = "xmodel", GdtName = "apex_test.gdt" };
        broken.Properties["filename"] = @"apex_no_such_folder\broken";
        var preview = new ModelPreviewViewModel(broken, env, Resolve);
        escaped = false;
        try { preview.RefreshAsync().GetAwaiter().GetResult(); }
        catch (Exception) { escaped = true; }
        // Run off the UI thread, the load hits an unexpected exception partway (the GDT lookup's thread check): the
        // failure nobody anticipated. It must end in the pane's error line, not escape or leave "Loading…".
        Check($"live preview: a failed load ends in the pane's error line, nothing escapes ('{preview.Error}')",
            !escaped && preview.Error is { Length: > 0 } && !preview.IsLoading);
        preview.Dispose();
    }

    // ── Per-keystroke UI-thread cost, journal off (the old path) vs on ─────────────────────
    private static void KeystrokeCost(string root)
    {
        // Both windows at once, one keystroke each in turn, so machine noise (JIT, GC, other builds) lands on both.
        var runs = new[] { (Label: "journal off", Root: (string?)null), (Label: "journal on", Root: (string?)root) }
            .Select(r =>
            {
                var vm = new MainViewModel(r.Root);
                var window = ShowJournalWindow(vm);
                vm.OpenByName("wpn_snp_ballista");
                return (r.Label, Vm: vm, Window: window, Times: new List<double>());
            }).ToList();
        var flush = new List<double>();
        var sw = new Stopwatch();
        for (var i = 0; i < 420; i++)
        {
            // Alternate which window goes first: whichever keys first each round measures faster.
            foreach (var run in i % 4 < 2 ? runs : Enumerable.Reverse(runs))
            {
                if (i == 0)
                    FocusNumber(run.Window, run.Vm, "damage");
                sw.Restart();
                Key(run.Window, i % 2 == 0 ? Avalonia.Input.Key.Up : Avalonia.Input.Key.Down, RawInputModifiers.Control);
                Dispatcher.UIThread.RunJobs();
                if (i >= 20) // warm-up
                    run.Times.Add(sw.Elapsed.TotalMilliseconds);
                if (run.Label == "journal on" && i % 10 == 9)
                {
                    // The journal's timer tick: diff the dirty asset, queue the record. Its only other UI-thread work.
                    sw.Restart();
                    run.Vm.FlushJournalNow();
                    flush.Add(sw.Elapsed.TotalMilliseconds);
                }
            }
        }
        static double Q(List<double> v, double q) => v.OrderBy(x => x).ElementAt(Math.Min(v.Count - 1, (int)(v.Count * q)));
        foreach (var run in runs)
            Console.WriteLine($"perf  keystroke (Ctrl+↑ in a number field, mock, headless) {run.Label}: median {Q(run.Times, 0.5):0.000} ms, p95 {Q(run.Times, 0.95):0.000} ms");
        Console.WriteLine($"perf  journal flush tick (UI thread, 1 dirty weapon): median {Q(flush, 0.5):0.000} ms, p95 {Q(flush, 0.95):0.000} ms");
        var off = Q(runs.First(r => r.Label == "journal off").Times, 0.5);
        var on = Q(runs.First(r => r.Label == "journal on").Times, 0.5);
        // Its own per-keystroke work is a set insert; anything past noise (1 ms or 10%) would be a regression.
        Gate($"perf: the journal adds no measurable keystroke cost (median off {off:0.000} ms, on {on:0.000} ms)",
            on - off < Math.Max(1.0, off * 0.1));
        Gate($"perf: the journal's flush tick stays under 1 ms (p95 {Q(flush, 0.95):0.000} ms)", Q(flush, 0.95) < 1.0);
        foreach (var run in runs)
        {
            run.Vm.Dispose();
            run.Window.Close();
        }
        Pump();
    }
}