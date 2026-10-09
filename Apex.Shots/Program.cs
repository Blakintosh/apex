using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Apex.Editor;
using Apex.Editor.ViewModels;
using Apex.Editor.Views;

namespace Apex.Shots;

/// <summary>Renders the real APEX window headlessly to PNGs so the UI can be verified without a display.</summary>
public partial class Program
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .WithInterFont();

    public static async Task<int> Main(string[] args)
    {
        _mainEntered = System.Diagnostics.Stopwatch.GetTimestamp();
        // --list-groups answers before anything is set up (run-suite.ps1 asks it for the shards' split).
        if (args.Contains("--list-groups"))
            return ListGroups(args);
        // First thing, in every mode: the save path may write only under this run's temp folder, never the install.
        Console.WriteLine($"GDT writes allowed only under {SaveRoot}");
        // Never the user's own extensions: none unless a run (or a check) points the folder somewhere.
        if (Environment.GetEnvironmentVariable(Apex.Editor.Services.Extensions.ExtensionRegistry.DirVariable) is not { Length: > 0 })
            Environment.SetEnvironmentVariable(Apex.Editor.Services.Extensions.ExtensionRegistry.DirVariable, NewScratch("no-extensions"));

        // --save-corpus: every GDT in the install, copied to temp, round-tripped and edited (slow; opt-in).
        // --save: only the fast save checks.
        if (args.Contains("--save-corpus"))
            return RunSaveCorpus();
        if (args.Contains("--save"))
        {
            RunSaveChecks();
            Console.WriteLine(_failures == 0 ? "ALL CHECKS PASSED" : $"{_failures} CHECK(S) FAILED");
            return _failures == 0 ? 0 : 1;
        }

        // --perf-live (or APEX_PERF_LIVE=1): only the perf gates, on the real install, in a process of their own.
        // --perf-startup: one launch timed for the live gates, which run it several times.
        if (args.Contains("--perf-startup"))
            return await RunLiveOnly(RunStartupOnce);
        // --fields-live: the tailored field editors on the real install (read-only: nothing is saved).
        // --buttons-live: the deffile rule changes behind the buttons, measured on the real install (read-only).
        if (args.Contains("--buttons-live"))
            return await RunLiveOnly(RunLiveButtonRegression);
        // --search-live: the search menu's keystrokes on the real install (read-only).
        if (args.Contains("--search-live"))
            return await RunLiveOnly(LiveSearchMenuTiming);
        if (args.Contains("--fields-live"))
            return await RunLiveOnly(RunLiveFieldChecks);
        // --notetracks-live: the install's xanim notetrack actions and their sound aliases (read-only).
        if (args.Contains("--notetracks-live"))
            return await RunLiveOnly(RunLiveNotetrackChecks);
        // --materials-live: material.awi's techsetdef host calls on the real install (read-only).
        if (args.Contains("--materials-live"))
            return await RunLiveOnly(RunLiveMaterialChecks);
        // --material-shots: a material in the live app, both themes (read-only).
        if (args.Contains("--material-shots"))
        {
            var shotsDir = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal)) ?? "shots";
            return await RunLiveOnly(() => RunMaterialShots(shotsDir));
        }
        // --viewhands-live: first person's inferred viewhands on the real install (read-only).
        if (args.Contains("--viewhands-live"))
            return await RunLiveOnly(RunLiveViewhands);
        if (args.Contains("--perf-live") || Environment.GetEnvironmentVariable("APEX_PERF_LIVE") == "1")
            return await RunLiveOnly(RunLivePerfGates);

        // §4.5: force mock mode so the harness is deterministic regardless of a local BO3 install.
        // GameEnvironment honours APEX_FORCE_MOCK=1 by reporting IsAvailable=false.
        Environment.SetEnvironmentVariable("APEX_FORCE_MOCK", "1");
        Apex.Editor.Models.SchemaRegistry.ResetToMock();

        // The suite's own options pick groups of the full run (Suite.cs); without them it is all of it, in order.
        var groups = SelectGroups(args, out var selectError) ?? Groups.ToList();
        if (selectError is not null)
        {
            Console.WriteLine(selectError);
            return 2;
        }
        if (groups.Count == 0)
        {
            Console.WriteLine("no groups selected");
            return 2;
        }
        _deferGates = args.Contains("--defer-gates") || args.Contains("--fast");
        StartRecording(args);

        var outDir = Positional(args) ?? "shots";
        Directory.CreateDirectory(outDir);

        using var session = HeadlessUnitTestSession.StartNew(typeof(Program));
        // --save-ui: only saving in the live app (a temp install), with real input.
        if (args.Contains("--save-ui"))
        {
            await session.Dispatch(() =>
            {
                Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
                try { RunSaveUiChecks(outDir); }
                catch (Exception ex) { Check($"save ui checks: {ex}", false); }
                return Task.CompletedTask;
            }, default);
            Console.WriteLine(_failures == 0 ? "ALL CHECKS PASSED" : $"{_failures} CHECK(S) FAILED");
            Environment.Exit(_failures == 0 ? 0 : 1);
        }
        // --placement: only copy/cut/paste, Move to…, Derive and Underive in the live app (a temp install), with real input.
        if (args.Contains("--placement"))
        {
            await session.Dispatch(() =>
            {
                Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
                try { RunPlacementChecks(outDir); }
                catch (Exception ex) { Check($"placement checks: {ex}", false); }
                return Task.CompletedTask;
            }, default);
            Console.WriteLine(_failures == 0 ? "ALL CHECKS PASSED" : $"{_failures} CHECK(S) FAILED");
            Environment.Exit(_failures == 0 ? 0 : 1);
        }
        // --buttons: only the deffile buttons, live on a temp install, with real clicks.
        if (args.Contains("--buttons"))
        {
            await session.Dispatch(() =>
            {
                Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
                try { RunButtonChecks(outDir); }
                catch (Exception ex) { Check($"button checks: {ex}", false); }
                return Task.CompletedTask;
            }, default);
            Console.WriteLine(_failures == 0 ? "ALL CHECKS PASSED" : $"{_failures} CHECK(S) FAILED");
            Environment.Exit(_failures == 0 ? 0 : 1);
        }
        // --fields: only the tailored field editors (file, colour, file list, bone, vector labels).
        if (args.Contains("--fields"))
        {
            await session.Dispatch(() =>
            {
                Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
                try { RunFieldChecks(outDir); }
                catch (Exception ex) { Check($"field checks: {ex}", false); }
                return Task.CompletedTask;
            }, default);
            Console.WriteLine(_failures == 0 ? "ALL CHECKS PASSED" : $"{_failures} CHECK(S) FAILED");
            Environment.Exit(_failures == 0 ? 0 : 1);
        }
        // --lists: only the line-list fields (hideTags, materials, skinOverride).
        if (args.Contains("--lists"))
        {
            await session.Dispatch(() =>
            {
                Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
                try { RunLineChecks(outDir); }
                catch (Exception ex) { Check($"line list checks: {ex}", false); }
                return Task.CompletedTask;
            }, default);
            Console.WriteLine(_failures == 0 ? "ALL CHECKS PASSED" : $"{_failures} CHECK(S) FAILED");
            Environment.Exit(_failures == 0 ? 0 : 1);
        }
        // --notetracks: only the notetrack timeline (markers, alias index, real input on the timeline).
        if (args.Contains("--notetracks"))
        {
            await session.Dispatch(() =>
            {
                Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
                try { RunNotetrackChecks(outDir); }
                catch (Exception ex) { Check($"notetrack checks: {ex}", false); }
                return Task.CompletedTask;
            }, default);
            Console.WriteLine(_failures == 0 ? "ALL CHECKS PASSED" : $"{_failures} CHECK(S) FAILED");
            Environment.Exit(_failures == 0 ? 0 : 1);
        }
        // --anim-layout: only the xanim editor (viewport chips, notetracks dock, properties panel) on a temp install.
        if (args.Contains("--anim-layout"))
        {
            await session.Dispatch(() =>
            {
                Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
                try { RunAnimLayoutChecks(outDir); }
                catch (Exception ex) { Check($"anim layout checks: {ex}", false); }
                return Task.CompletedTask;
            }, default);
            Console.WriteLine(_failures == 0 ? "ALL CHECKS PASSED" : $"{_failures} CHECK(S) FAILED");
            Environment.Exit(_failures == 0 ? 0 : 1);
        }
        // --install: only the first-run not-found state and Locate… (a temp install, real input).
        if (args.Contains("--install"))
        {
            await session.Dispatch(() =>
            {
                Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
                try { RunInstallChecks(outDir); }
                catch (Exception ex) { Check($"install checks: {ex}", false); }
                return Task.CompletedTask;
            }, default);
            Console.WriteLine(_failures == 0 ? "ALL CHECKS PASSED" : $"{_failures} CHECK(S) FAILED");
            Environment.Exit(_failures == 0 ? 0 : 1);
        }
        // --search: only the Explorer's search box and the search menu over it, with real input.
        if (args.Contains("--search"))
        {
            await session.Dispatch(() =>
            {
                Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
                try { RunSearchMenuChecks(outDir); }
                catch (Exception ex) { Check($"search menu checks: {ex}", false); }
                return Task.CompletedTask;
            }, default);
            Console.WriteLine(_failures == 0 ? "ALL CHECKS PASSED" : $"{_failures} CHECK(S) FAILED");
            Environment.Exit(_failures == 0 ? 0 : 1);
        }
        // --explorer: only the Explorer's rows, counts, facets, keys and Filter menu, with real input.
        if (args.Contains("--explorer"))
        {
            await session.Dispatch(() =>
            {
                Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
                try { RunExplorerChecks(outDir); }
                catch (Exception ex) { Check($"explorer checks: {ex}", false); }
                return Task.CompletedTask;
            }, default);
            Console.WriteLine(_failures == 0 ? "ALL CHECKS PASSED" : $"{_failures} CHECK(S) FAILED");
            Environment.Exit(_failures == 0 ? 0 : 1);
        }
        // --layout: only the narrow-pane layout, automation names and the audio soft clip.
        if (args.Contains("--layout"))
        {
            await session.Dispatch(() =>
            {
                Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
                try { RunLayoutChecks(outDir); }
                catch (Exception ex) { Check($"layout checks: {ex}", false); }
                return Task.CompletedTask;
            }, default);
            Console.WriteLine(_failures == 0 ? "ALL CHECKS PASSED" : $"{_failures} CHECK(S) FAILED");
            Environment.Exit(_failures == 0 ? 0 : 1);
        }
        // --extensions: only schema extensions (rules, loader, editor, journal, the live app on a temp install).
        if (args.Contains("--extensions"))
        {
            await session.Dispatch(() =>
            {
                Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
                try { RunExtensionChecks(outDir); }
                catch (Exception ex) { Check($"extension checks: {ex}", false); }
                return Task.CompletedTask;
            }, default);
            Console.WriteLine(_failures == 0 ? "ALL CHECKS PASSED" : $"{_failures} CHECK(S) FAILED");
            Environment.Exit(_failures == 0 ? 0 : 1);
        }
        // --records: only record lists (wtKick#: codec, manifest, the table in the editor, the gap fixes, the live app).
        if (args.Contains("--records"))
        {
            await session.Dispatch(() =>
            {
                Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
                try { RunRecordChecks(outDir); }
                catch (Exception ex) { Check($"record checks: {ex}", false); }
                return Task.CompletedTask;
            }, default);
            Console.WriteLine(_failures == 0 ? "ALL CHECKS PASSED" : $"{_failures} CHECK(S) FAILED");
            Environment.Exit(_failures == 0 ? 0 : 1);
        }
        // --hardening: only extension data under partial saves, restarts, undo across moves, orphan names and reloads.
        if (args.Contains("--hardening"))
        {
            await session.Dispatch(() =>
            {
                Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
                try { RunHardeningChecks(); }
                catch (Exception ex) { Check($"hardening checks: {ex}", false); }
                return Task.CompletedTask;
            }, default);
            Console.WriteLine(_failures == 0 ? "ALL CHECKS PASSED" : $"{_failures} CHECK(S) FAILED");
            Environment.Exit(_failures == 0 ? 0 : 1);
        }
        // --simulator: only preview-simulator modules (manifest, consent, failures, exact steps, the prompt in the app).
        if (args.Contains("--simulator"))
        {
            await session.Dispatch(() =>
            {
                Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
                try { RunSimulatorChecks(outDir); }
                catch (Exception ex) { Check($"simulator checks: {ex}", false); }
                return Task.CompletedTask;
            }, default);
            Console.WriteLine(_failures == 0 ? "ALL CHECKS PASSED" : $"{_failures} CHECK(S) FAILED");
            Environment.Exit(_failures == 0 ? 0 : 1);
        }
        // --recoil: only the weapon recoil preview (signs, driver, pose mix, resolving, the app, the renderer's plain frame).
        if (args.Contains("--recoil"))
        {
            await session.Dispatch(() =>
            {
                Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
                try { RunRecoilChecks(outDir); }
                catch (Exception ex) { Check($"recoil checks: {ex}", false); }
                return Task.CompletedTask;
            }, default);
            Console.WriteLine(_failures == 0 ? "ALL CHECKS PASSED" : $"{_failures} CHECK(S) FAILED");
            Environment.Exit(_failures == 0 ? 0 : 1);
        }
        // --editor-pass: only the extension editor pass (parts, labelled choices, combine, paste, folding, the off line,
        // notices and export, bulk on/off, status lines), pure, on temp GDTs and in the live app on a temp install.
        if (args.Contains("--editor-pass"))
        {
            await session.Dispatch(() =>
            {
                Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
                try { RunEditorPassChecks(outDir); }
                catch (Exception ex) { Check($"editor pass checks: {ex}", false); }
                return Task.CompletedTask;
            }, default);
            Console.WriteLine(_failures == 0 ? "ALL CHECKS PASSED" : $"{_failures} CHECK(S) FAILED");
            Environment.Exit(_failures == 0 ? 0 : 1);
        }
        // --editor-review: only the design review's editor items (mock data, then the real deffiles on a temp install).
        if (args.Contains("--editor-review"))
        {
            await session.Dispatch(() =>
            {
                Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
                try { RunEditorReviewChecks(outDir); }
                catch (Exception ex) { Check($"editor review checks: {ex}", false); }
                return Task.CompletedTask;
            }, default);
            Console.WriteLine(_failures == 0 ? "ALL CHECKS PASSED" : $"{_failures} CHECK(S) FAILED");
            Environment.Exit(_failures == 0 ? 0 : 1);
        }
        // --styles: only the shared visual system (hover, dropdowns, menus, state colours, the accent), both themes.
        if (args.Contains("--styles"))
        {
            await session.Dispatch(() =>
            {
                try { RunStyleChecks(outDir); }
                catch (Exception ex) { Check($"style checks: {ex}", false); }
                return Task.CompletedTask;
            }, default);
            Console.WriteLine(_failures == 0 ? "ALL CHECKS PASSED" : $"{_failures} CHECK(S) FAILED");
            Environment.Exit(_failures == 0 ? 0 : 1);
        }
        // --perf: only the mock-data perf gates and the scroll checks (a quick loop while working on speed).
        if (args.Contains("--perf"))
        {
            await session.Dispatch(() =>
            {
                Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
                try { RunMockPerfGates(); }
                catch (Exception ex) { Check($"perf gates: {ex}", false); }
                try { RunScrollChecks(); }
                catch (Exception ex) { Check($"scroll checks: {ex}", false); }
                return Task.CompletedTask;
            }, default);
            Console.WriteLine(_failures == 0 ? "ALL CHECKS PASSED" : $"{_failures} CHECK(S) FAILED");
            Environment.Exit(_failures == 0 ? 0 : 1);
        }
        SessionReady();
        await session.Dispatch(() =>
        {
            // A run without the main flow starts where it leaves the theme: dark.
            if (groups[0].Name != "main")
                Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
            foreach (var g in groups.Where(g => !g.AfterSession))
                RunGroup(g, outDir);
            return Task.CompletedTask;
        }, default);

        Console.WriteLine($"Shots written to {Path.GetFullPath(outDir)}");

        // §4.5: optional live smoke — only runs when a real BO3 install is present; skipped otherwise.
        foreach (var g in groups.Where(g => g.AfterSession))
            RunGroup(g, outDir);

        ReportRecording(args, groups);
        Console.WriteLine(_failures == 0 ? "ALL CHECKS PASSED" : $"{_failures} CHECK(S) FAILED");
        Environment.Exit(_failures == 0 ? 0 : 1); // the shown window keeps the headless session's dispatcher alive
        return 0;
    }

    /// <summary>The main flow: the app on mock data, shot by shot, every surface once (first in the full run).</summary>
    private static void RunMainFlow(string outDir)
    {
        // The app follows the Windows light/dark setting; headless has none, so the harness
        // shoots dark by default and light explicitly (the light pass at the end).
        Check("theme: app follows the Windows setting", Application.Current!.RequestedThemeVariant == ThemeVariant.Default);
        Application.Current.RequestedThemeVariant = ThemeVariant.Dark;

        // The session journal runs as it does for users, in a throwaway folder (never %LOCALAPPDATA%).
        var sessionRoot = Path.Combine(Path.GetTempPath(), "apex-shots-session-" + Guid.NewGuid().ToString("N")[..8]);
        var vm = new MainViewModel(sessionRoot);
        var window = new MainWindow { DataContext = vm };
        window.Show();
        Capture(window, Path.Combine(outDir, "01-startup.png"));

        // Open a zombies weapon (has a parent template) and dirty a few values.
        vm.OpenByName("wpn_ar_havoc_zm_upgraded");
        if (vm.ActiveTab is { } tab)
        {
            foreach (var prop in tab.AllSentinel.All)
            {
                if (prop.Key is "clipSize") prop.RawValue = "115";
                if (prop.Key is "damage") prop.RawValue = "275";
                if (prop.Key is "displayName") prop.RawValue = "Havoc of the Dead";
            }
        }
        vm.OpenByName("wpn_snp_locus");
        vm.OpenByName("mtl_marble_03");
        vm.OpenByName("wpn_ar_havoc_zm_upgraded");
        Capture(window, Path.Combine(outDir, "02-weapon-editor.png"));

        // Mock mode has no deffile programs, so no display rule may hide anything.
        if (vm.ActiveTab is { } ruleTab)
            Check("rules: inert without deffile programs",
                ruleTab.VisiblePropertyCount == ruleTab.PropertyCount
                && ruleTab.AllSentinel.All.All(p => !p.IsRuleHidden && !p.IsRuleDisabled));

        // Category rail focus + modified only.
        if (vm.ActiveTab is { } t2)
        {
            t2.SelectedRail = t2.RailItems.FirstOrDefault(r => r.Name == "Damage & Range");
            Capture(window, Path.Combine(outDir, "03-category.png"));
            t2.SelectedRail = t2.AllSentinel;
            t2.ModifiedOnly = true;
            Capture(window, Path.Combine(outDir, "04-modified-only.png"));
            t2.ModifiedOnly = false;
        }

        // Browser query.
        vm.FilterText = "type:weapon prop:damage>=100 gdt:zm";
        vm.ApplyFilterNow();
        Capture(window, Path.Combine(outDir, "05-filtered.png"));

        vm.FilterText = "";
        vm.ApplyFilterNow();

        // ── Undo/redo smoke test ─────────────────────────────────────────
        if (vm.ActiveTab is { } undoTab)
        {
            // Use a field the earlier steps did not touch so this is a fresh undo step.
            var damageProp = undoTab.AllSentinel.All.First(p => p.Key == "meleeDamage");
            var before = damageProp.RawValue;
            damageProp.RawValue = "999";
            Check("undo: edit recorded", undoTab.CanUndo);
            undoTab.UndoCommand.Execute(null);
            Check($"undo: value restored ({damageProp.RawValue})", damageProp.RawValue == before);
            Check("redo: available", undoTab.CanRedo);
            undoTab.RedoCommand.Execute(null);
            Check($"redo: value reapplied ({damageProp.RawValue})", damageProp.RawValue == "999");
            undoTab.UndoCommand.Execute(null);
        }

        // ── Inheritance provenance: overrides-only view of a derived weapon ──
        if (vm.ActiveTab is { } provTab)
        {
            Check($"provenance: override count {provTab.OverrideCount}", provTab.OverrideCount > 0);
            provTab.OverridesOnly = true;
            Capture(window, Path.Combine(outDir, "06-overrides-only.png"));
            provTab.OverridesOnly = false;
        }

        // ── Validation: an asset with seeded problems ────────────────────
        vm.OpenByName("wpn_smg_wasp");
        if (vm.ActiveTab is { } probTab)
        {
            Check($"validation: {probTab.ProblemCount} problems on wpn_smg_wasp", probTab.ProblemCount >= 2);
            Capture(window, Path.Combine(outDir, "07-problems.png"));
            // Rendering the editor must not clamp/mutate out-of-range data.
            Check("validation: hipSpread=35 survives rendering",
                probTab.Record.Properties["hipSpread"] == "35" && probTab.ProblemCount >= 2);
        }
        Check($"validation: global total {vm.ProblemTotal}", vm.ProblemTotal >= 4);
        MissingReferenceChecks(window, vm);

        // ── Compare mode: N-way diff (upgraded vs parent vs base weapon) ──
        vm.OpenByName("wpn_ar_havoc_zm_upgraded");
        vm.OpenCompareCommand.Execute(null);
        Check("compare: opened vs parent", vm.Compare?.Columns.FirstOrDefault()?.Name == "wpn_ar_havoc_zm");
        if (vm.Compare is { } cmp)
        {
            cmp.AddText = "havoc";
            var third = cmp.AddMatches.FirstOrDefault(a => a.Name == "wpn_ar_havoc");
            Check($"compare: the picker searches by name ({cmp.AddMatches.Count} matches for 'havoc')", third is not null);
            cmp.AddColumnCommand.Execute(third);
            Check($"compare: {cmp.Columns.Count} comparison columns", cmp.Columns.Count == 2);
            Check($"compare: {cmp.Columns.Sum(c => c.DiffCount)} total diffs", cmp.Columns.Sum(c => c.DiffCount) > 0);
        }
        Capture(window, Path.Combine(outDir, "08-compare.png"));
        vm.Compare?.CloseCommand.Execute(null);

        // ── Table (spreadsheet) mode with a bulk edit ────────────────────
        vm.FilterText = "type:weapon gdt:t7";
        vm.ApplyFilterNow();
        vm.OpenTableCommand.Execute(null);
        if (vm.Table is { } table)
        {
            foreach (var row in table.Rows.Take(5))
                row.IsSelected = true;
            var damageDef = Apex.Editor.Models.SchemaRegistry.Get(table.TypeName)!.Find("damage")!;
            table.SetColumnShown(damageDef, true);
            table.SelectedColumn = table.Columns.First(c => c.Key == "damage");
            table.BulkEditor!.RawValue = "55";
            Capture(window, Path.Combine(outDir, "09-table.png"));
            var damageBefore = table.Rows.Take(5).Select(r => r.Record.Properties.GetValueOrDefault("damage")).ToList();
            // Bulk apply is one undo step, so it applies at once: no confirmation.
            table.BulkApplyCommand.Execute(null);
            Check("table: bulk apply writes straight away",
                !vm.IsConfirmOpen && table.Rows.Take(5).All(r => r.Record.Properties["damage"] == "55"));
            Check($"table: status follows the apply ('{vm.Status}')", vm.Status.Contains("Ctrl+Z"));
            Capture(window, Path.Combine(outDir, "10-table-bulk-applied.png"));
            vm.UndoActiveCommand.Execute(null);
            Check("table: Ctrl+Z undoes the whole bulk apply",
                table.Rows.Take(5).Select(r => r.Record.Properties.GetValueOrDefault("damage")).SequenceEqual(damageBefore));
            vm.RedoActiveCommand.Execute(null);
            Check("table: redo reapplies it", table.Rows.Take(5).All(r => r.Record.Properties["damage"] == "55"));
            table.CloseCommand.Execute(null);
        }
        vm.FilterText = "";
        vm.ApplyFilterNow();

        // ── Refactors: rename with reference update · duplicate · safe delete ──
        vm.OpenByName("wpn_smg_wasp");
        if (vm.ActiveTab is { } renameTab)
        {
            renameTab.RenameText = "wpn_smg_hornet";
            renameTab.RenameCommand.Execute(null);
            Check($"rename: {vm.Status}", renameTab.Name == "wpn_smg_hornet" && vm.Status.Contains("updated"));
            vm.DuplicateActiveCommand.Execute(null);
            Check($"duplicate: {vm.ActiveTab?.Name}", vm.ActiveTab?.Name == "wpn_smg_hornet_copy");

            // Delete asks nothing: it is one undo step. The corpus reference scan runs first, off the UI thread.
            vm.DeleteActiveCommand.Execute(null);
            Check("delete: no question first", !vm.IsConfirmOpen);
            WaitFor(() => vm.Status.StartsWith("Deleted"));
            Check($"delete: says how to take it back — '{vm.Status}'",
                vm.Status == "Deleted wpn_smg_hornet_copy · Ctrl+Z restores it" && !DatabaseOf(vm).Assets.Any(a => a.Name == "wpn_smg_hornet_copy")
                && vm.OpenTabs.All(t => t.Name != "wpn_smg_hornet_copy"));
            Capture(window, Path.Combine(outDir, "10b-deleted.png"));
            vm.UndoActiveCommand.Execute(null);
            Check($"delete: Ctrl+Z restores the asset and its tab — '{vm.Status}'",
                DatabaseOf(vm).Assets.Any(a => a.Name == "wpn_smg_hornet_copy") && vm.ActiveTab?.Name == "wpn_smg_hornet_copy" && vm.Status == "Restored wpn_smg_hornet_copy");
            vm.RedoActiveCommand.Execute(null);
            Check($"delete: Ctrl+Y deletes it again — '{vm.Status}'", !DatabaseOf(vm).Assets.Any(a => a.Name == "wpn_smg_hornet_copy"));
            Capture(window, Path.Combine(outDir, "11-refactored.png"));
        }

        // ── A blocked delete raises an actionable alert, not a status-bar line ──
        vm.OpenByName("wpn_ar_havoc_zm");
        if (vm.ActiveTab is not null)
        {
            vm.DeleteActiveCommand.Execute(null);
            Check($"blocked delete: alert raised — '{vm.AlertText}'",
                vm.IsAlertOpen && vm.AlertIsError && !vm.IsConfirmOpen && vm.HasAlertAction);
            Capture(window, Path.Combine(outDir, "11b-delete-blocked.png"));
            vm.DismissAlertCommand.Execute(null);
            Check("blocked delete: alert dismisses", !vm.IsAlertOpen);
        }

        // ── Session state: edits are kept across restarts, not in GDTs, and the UI says so ───
        vm.OpenByName("wpn_snp_ballista");
        if (vm.ActiveTab is { } autoTab)
        {
            var before = vm.SessionEditCount;
            var damage = autoTab.AllSentinel.All.First(p => p.Key == "damage");
            var original = damage.RawValue;
            var statusBefore = vm.Status;
            damage.RawValue = "222";
            // The field shows the edit and the session chip says where edits are: the status bar repeats neither.
            Check($"session: an edit doesn't echo into the status bar ('{vm.Status}')",
                vm.Status == statusBefore && autoTab.CanUndo);
            Check($"session: edit counted ({vm.SessionStateText})",
                vm.SessionEditCount == before + 1 && vm.SessionStateText.Contains("not in GDTs yet"));
            damage.RawValue = "223";
            damage.RawValue = "224";
            Check($"session: more edits to one field are still one change ({vm.SessionEditCount})", vm.SessionEditCount == before + 1);
            damage.RawValue = original;
            Check($"session: typing the old value back uncounts it ({vm.SessionEditCount})", vm.SessionEditCount == before);
            damage.RawValue = "222";
            vm.SaveActiveCommand.Execute(null);
            Check($"session: Ctrl+S on sample data says there are no GDT files to save to ('{vm.Status}')", vm.Status.Contains("sample data") && vm.SessionEditCount > 0);
        }

        // ── Closing loses nothing now, so it doesn't ask ─────────────────
        var closed = false;
        Check("close: a kept session closes without a question",
            vm.RequestClose(() => closed = true) && !vm.IsConfirmOpen && !closed);

        // ── Rename input sanitisation ────────────────────────────────────
        if (vm.ActiveTab is { } sanTab)
        {
            sanTab.RenameText = "My Cool Gun 2!";
            Check($"rename sanitised: '{sanTab.RenameText}'", sanTab.RenameText == "my_cool_gun_2");
        }

        // ── Quick filters: problems pill writes tokens, tree shows ⚠ ─────
        vm.FilterText = "";
        vm.ApplyFilterNow();
        vm.AddFilterToken("type:weapon");
        vm.ProblemsFilter = true;
        Check($"quick filter: '{vm.FilterText}'",
            vm.FilterText.Contains("type:weapon") && vm.FilterText.Contains("is:problems"));
        Check($"quick filter: {vm.MatchCount} weapons with problems", vm.MatchCount >= 2);
        var problemNode = vm.FlatRows.FirstOrDefault(n => n.Asset?.Name == "wpn_smg_hornet");
        Check("tree: ⚠ badge on problem asset", problemNode?.HasProblem == true);
        Capture(window, Path.Combine(outDir, "12-problem-filter.png"));

        // ── Browser empty state: a query that matches nothing ────────────
        vm.ProblemsFilter = false;
        vm.FilterText = "type:weapon zzzz_no_such_asset";
        vm.ApplyFilterNow();
        Check($"browser: empty state for a no-match query ({vm.MatchCount} matches)",
            vm.BrowserIsEmpty && vm.MatchCount == 0 && vm.HasFilter);
        Capture(window, Path.Combine(outDir, "18-browser-empty.png"));

        // ── Derivation visible in the tree ───────────────────────────────
        vm.FilterText = "type:weapon gdt:zm_weapons";
        vm.ApplyFilterNow();
        var derived = vm.FlatRows.FirstOrDefault(n => n.Asset?.Name == "wpn_ar_havoc_zm_upgraded");
        // zm_weapons.gdt holds a single type, so its assets sit directly under the GDT row.
        Check($"tree: derived asset nested at level {derived?.Level}", derived is { Level: >= 2, IsDerived: true });
        Capture(window, Path.Combine(outDir, "13-derivation-tree.png"));

        // ── + Add: new asset & new GDT ───────────────────────────────────
        vm.FilterText = "";
        vm.ApplyFilterNow();
        vm.NewAssetCommand.Execute(null);
        vm.NewName = "wpn_new_weapon";
        Capture(window, Path.Combine(outDir, "14a-new-asset-dialog.png"));
        vm.CommitNewCommand.Execute(null);
        Check($"new asset: {vm.ActiveTab?.Name} in {vm.ActiveTab?.GdtName}",
            vm.ActiveTab?.Name == "wpn_new_weapon" && !vm.IsNewOpen);
        vm.NewGdtCommand.Execute(null);
        vm.NewName = "zm_my_mod";
        vm.CommitNewCommand.Execute(null);
        Check($"new gdt: {vm.Status}", vm.Status.StartsWith("Created zm_my_mod.gdt"));
        Capture(window, Path.Combine(outDir, "14-new-asset.png"));

        // ── Preview tabs (VS Code semantics) ─────────────────────────────
        var tabsBefore = vm.OpenTabs.Count;
        vm.OpenByName("wpn_smg_riot", preview: true);
        Check("preview: single-click opens preview tab",
            vm.ActiveTab is { IsPreview: true } && vm.OpenTabs.Count == tabsBefore + 1);
        vm.OpenByName("wpn_smg_needle", preview: true);
        Check($"preview: next preview replaces in place ({vm.OpenTabs.Count} tabs)",
            vm.ActiveTab?.Name == "wpn_smg_needle" && vm.OpenTabs.Count == tabsBefore + 1);
        Capture(window, Path.Combine(outDir, "15-preview-tab.png"));
        vm.ActiveTab!.AllSentinel.All.First(p => p.Key == "damage").RawValue = "31";
        Check("preview: editing pins the tab", vm.ActiveTab is { IsPreview: false });
        vm.OpenByName("wpn_smg_riot", preview: true);
        vm.OpenByName("wpn_smg_riot");
        Check("preview: re-opening pins the tab", vm.ActiveTab is { IsPreview: false } t && t.Name == "wpn_smg_riot");

        // ── Theme variants ───────────────────────────────────────────────
        // Every themed brush is a {DynamicResource} over Resources/Tokens.axaml, so flipping
        // the variant must re-theme the live window with no reload and no stale colours.
        vm.FilterText = "";
        vm.ApplyFilterNow();
        vm.OpenByName("wpn_ar_havoc_zm_upgraded");
        Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
        Check("theme: switched to light", Application.Current.ActualThemeVariant == ThemeVariant.Light);
        Capture(window, Path.Combine(outDir, "19-light-editor.png"));
        vm.OpenTableCommand.Execute(null);
        Capture(window, Path.Combine(outDir, "20-light-table.png"));
        vm.Table?.CloseCommand.Execute(null);
        Application.Current.RequestedThemeVariant = ThemeVariant.Dark;
        Check("theme: switched back to dark", Application.Current.ActualThemeVariant == ThemeVariant.Dark);

        // ── Quick Open palette (⌘P) ──────────────────────────────────────
        vm.OpenPaletteCommand.Execute(null);
        Check("palette: opens with seed results", vm.IsPaletteOpen && vm.PaletteResults.Count > 0);
        vm.PaletteText = "type:weapon maul";
        Check($"palette: query filters ({vm.PaletteResults.Count} results)",
            vm.PaletteResults.Count > 0 && vm.PaletteResults.All(r => r.Name.Contains("maul")));
        Capture(window, Path.Combine(outDir, "16-palette.png"));
        vm.ConfirmPaletteCommand.Execute(null);
        Check($"palette: enter opens preview ({vm.ActiveTab?.Name})",
            !vm.IsPaletteOpen && vm.ActiveTab is { IsPreview: true } p && p.Name.Contains("maul"));

        // ── Workspace redesign: Inspector, Explorer modes, layouts, palette, history ──
        vm.FilterText = "";
        vm.ApplyFilterNow();
        vm.OpenByName("wpn_ar_havoc_zm_upgraded");
        if (vm.ActiveTab is { } focusTab)
        {
            focusTab.RevealProperty("damage");
            Dispatcher.UIThread.RunJobs();
            Check($"inspector: follows the focused row ({vm.InspectorProperty?.Key})", vm.InspectorProperty?.Key == "damage");
            var inspectorTexts = window.GetVisualDescendants().OfType<InspectorView>().Single()
                .GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
            Check("inspector: doesn't list this session's changes again (the editor's Changed view does)",
                focusTab.Changes.Count > 0 && !inspectorTexts.Contains("Your changes") && !inspectorTexts.Contains("Undo all"));
            Check($"inspector: uses {vm.UsesCount}, family {vm.FamilyCount}", vm.UsesCount > 0 && vm.FamilyCount > 0);
            Capture(window, Path.Combine(outDir, "21-inspector-focused.png"));

            focusTab.View = EditorView.Changed;
            Check($"editor: Changed view lists this session's edits ({focusTab.VisiblePropertyCount})",
                focusTab.VisiblePropertyCount == focusTab.Changes.Count && focusTab.Changes.Count > 0);
            focusTab.View = EditorView.All;
        }

        vm.Grouping = ExplorerGrouping.Type;
        Check("explorer: grouped by type", vm.FlatRows.Any(n => n.GroupType == "weapon") && !vm.FlatRows.Any(n => n.Gdt is not null));
        Capture(window, Path.Combine(outDir, "23-explorer-by-type.png"));
        vm.Grouping = ExplorerGrouping.Gdt;
        Check("explorer: single-type GDTs skip the type row",
            vm.FlatRows.First(n => n.Gdt?.Name == "zm_weapons.gdt").Suffix == "weapon"
            && !vm.FlatRows.Any(n => n.Level == 1 && n.GroupType == "weapon" && n.Gdt is null
                && vm.FlatRows.IndexOf(n) > vm.FlatRows.IndexOf(vm.FlatRows.First(g => g.Gdt?.Name == "zm_weapons.gdt"))
                && vm.FlatRows.IndexOf(n) == vm.FlatRows.IndexOf(vm.FlatRows.First(g => g.Gdt?.Name == "zm_weapons.gdt")) + 1));

        vm.FilterText = "havoc";
        vm.ApplyFilterNow();
        Check($"explorer: search goes flat with facets ({vm.SearchFacets.Count})",
            vm.IsSearchResults && vm.SearchFacets.Count > 1 && vm.FlatRows.All(n => n.IsAssetRow && n.HasHit));
        Capture(window, Path.Combine(outDir, "25-explorer-search.png"));
        vm.FilterText = "";
        vm.ApplyFilterNow();
        Check("explorer: clearing the search restores the tree", !vm.IsSearchResults && vm.FlatRows.Any(n => n.Gdt is not null));

        if (vm.ActiveTab is { } pinTab)
        {
            vm.TogglePinned(pinTab.Record);
            Check("explorer: pinned section", vm.FlatRows.FirstOrDefault()?.IsHeader == true && vm.FlatRows.Any(n => n.IsPinnedEntry));
        }

        vm.Layout = WorkspaceLayout.Preview;
        Check("layout: preview hides explorer and inspector", !vm.ShowExplorer && !vm.ShowInspector);
        Capture(window, Path.Combine(outDir, "26-preview-layout.png"));
        vm.Layout = WorkspaceLayout.Edit;

        PreviewPaneChecks(window, vm, outDir);

        // The preview pops out to its own window and docks back.
        var popButton = window.GetVisualDescendants().OfType<Button>().First(b => b.Name == "PopButton" && b.IsEffectivelyVisible);
        Check($"preview: ⧉ offers to pop out ('{ToolTip.GetTip(popButton)}')", ToolTip.GetTip(popButton) as string == "Preview in its own window (Ctrl+Shift+O)");
        vm.TogglePreviewFloatingCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var floating = window.OwnedWindows.OfType<PreviewWindow>().FirstOrDefault();
        Check("preview: pops out to its own window", vm.IsPreviewFloating && floating is { IsVisible: true });
        Capture(window, Path.Combine(outDir, "26c-preview-floated-main.png"));
        if (floating is not null)
        {
            Capture(floating, Path.Combine(outDir, "26b-preview-window.png"));
            var floatPop = floating.GetVisualDescendants().OfType<Button>().First(b => b.Name == "PopButton");
            Check($"preview window: ⧉ now docks back ('{ToolTip.GetTip(floatPop)}')",
                ToolTip.GetTip(floatPop) as string == "Dock preview (Ctrl+Shift+O)");
            Check($"preview window: title names the subject ('{floating.Title}')",
                vm.PreviewSubject is { } shownSubject && floating.Title?.StartsWith(shownSubject.Record.Name) == true);
            // The main window's shortcuts work from the popped-out window: real key presses on it.
            floating.KeyPress(Avalonia.Input.Key.P, Avalonia.Input.RawInputModifiers.Control, Avalonia.Input.PhysicalKey.P, "p");
            Dispatcher.UIThread.RunJobs();
            Check("preview window: Ctrl+P opens Quick Open", vm.IsPaletteOpen);
            vm.ClosePaletteCommand.Execute(null);
            floating.KeyPress(Avalonia.Input.Key.O, Avalonia.Input.RawInputModifiers.Control | Avalonia.Input.RawInputModifiers.Shift,
                Avalonia.Input.PhysicalKey.O, "O");
            Dispatcher.UIThread.RunJobs();
            Check("preview window: Ctrl+Shift+O docks the preview back", !vm.IsPreviewFloating);
        }
        if (vm.IsPreviewFloating)
            vm.TogglePreviewFloatingCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Check("preview: docks back", !vm.IsPreviewFloating && !window.OwnedWindows.OfType<PreviewWindow>().Any());

        // History: following a reference records where you came from; Back returns there.
        vm.OpenByName("wpn_ar_havoc_zm_upgraded");
        var from = vm.ActiveTab!.Name;
        vm.OpenByName("wpn_smg_riot");
        vm.GoBackCommand.Execute(null);
        Check($"history: back returns to {from} ({vm.ActiveTab?.Name})", vm.ActiveTab?.Name == from && vm.CanGoForward);
        vm.GoForwardCommand.Execute(null);
        Check("history: forward", vm.ActiveTab?.Name == "wpn_smg_riot");
        // The mouse's side buttons walk the same history, over any part of the window.
        var overEditor = new Avalonia.Point(window.Bounds.Width * 0.6, window.Bounds.Height * 0.5);
        window.MouseDown(overEditor, Avalonia.Input.MouseButton.XButton1);
        window.MouseUp(overEditor, Avalonia.Input.MouseButton.XButton1);
        Dispatcher.UIThread.RunJobs();
        Check($"history: the mouse's back button returns to {from} ({vm.ActiveTab?.Name})", vm.ActiveTab?.Name == from);
        window.MouseDown(overEditor, Avalonia.Input.MouseButton.XButton2);
        window.MouseUp(overEditor, Avalonia.Input.MouseButton.XButton2);
        Dispatcher.UIThread.RunJobs();
        Check("history: the mouse's forward button", vm.ActiveTab?.Name == "wpn_smg_riot");
        Check("choices: a stored '<error>' is a problem that says what to do",
            Apex.Editor.Services.Validator.Check(new Apex.Editor.Models.PropertyDef("surfaceType", "Surface Type", "", Apex.Editor.Models.PropertyKind.Choice, "") { Choices = new[] { "<none>" } }, "<error>", (_, _) => true) is { } err
            && err.Contains("Pick another"));

        foreach (var name in new[] { "wpn_snp_locus", "wpn_smg_needle", "wpn_smg_wasp_copy", "wpn_ar_havoc", "wpn_ar_havoc_zm",
                     "mtl_marble_03", "wpn_smg_hornet", "wpn_snp_ballista", "wpn_ar_kestrel", "wpn_ar_vireo" })
            if (vm.OpenTabs.All(t => t.Name != name))
                vm.OpenByName(name);
        vm.DismissAlertCommand.Execute(null);
        Capture(window, Path.Combine(outDir, "27-tab-overflow.png"));

        vm.OpenCommandPaletteCommand.Execute(null);
        vm.PaletteText = ">explorer";
        Check($"palette: > lists commands ({vm.PaletteResults.Count})",
            vm.PaletteResults.Count >= 2 && vm.PaletteResults.All(r => r.IsCommand));
        Capture(window, Path.Combine(outDir, "28-command-palette.png"));
        vm.PaletteText = ">group by type";
        vm.ConfirmPaletteCommand.Execute(null);
        Check("palette: running a command", vm.Grouping == ExplorerGrouping.Type && !vm.IsPaletteOpen);
        vm.Grouping = ExplorerGrouping.Gdt;

        for (var guard = 0; vm.OpenTabs.Count > 0 && guard < 100; guard++)
            vm.CloseActiveTabCommand.Execute(null);
        Check("start page: nothing open", vm.HasNoActiveTab && vm.HasSessionChanges);
        Capture(window, Path.Combine(outDir, "29-start-page.png"));

        // ── Light pass: the surfaces the dark shots cover, in the light variant ──
        Application.Current.RequestedThemeVariant = ThemeVariant.Light;
        Capture(window, Path.Combine(outDir, "30-light-start-page.png"));
        vm.OpenByName("wpn_ar_havoc_zm");
        vm.DeleteActiveCommand.Execute(null);
        Check("light: blocked delete raises the alert", vm.IsAlertOpen);
        Capture(window, Path.Combine(outDir, "31-light-alert.png"));
        vm.DismissAlertCommand.Execute(null);
        vm.Registry[Apex.Editor.Commands.CommandCatalog.DiscardAll].Execute();
        Check("light: Discard all changes asks first", vm.IsConfirmOpen);
        Capture(window, Path.Combine(outDir, "32-light-confirm.png"));
        vm.CancelConfirmCommand.Execute(null);
        vm.OpenPaletteCommand.Execute(null);
        vm.PaletteText = "type:weapon havoc";
        Capture(window, Path.Combine(outDir, "33-light-palette.png"));
        vm.ClosePaletteCommand.Execute(null);
        vm.OpenByName("wpn_ar_havoc_zm_upgraded");
        vm.OpenCompareCommand.Execute(null);
        Capture(window, Path.Combine(outDir, "34-light-compare.png"));
        vm.Compare?.CloseCommand.Execute(null);
        vm.Layout = WorkspaceLayout.Preview;
        Capture(window, Path.Combine(outDir, "35-light-preview.png"));
        vm.Layout = WorkspaceLayout.Edit;
        Application.Current.RequestedThemeVariant = ThemeVariant.Dark;

        window.Close();
    }

    /// <summary>The real-install perf gates alone: no screenshots, no mock checks, nothing persisted.</summary>
    private static async Task<int> RunLiveOnly(Action run)
    {
        Environment.SetEnvironmentVariable("APEX_FORCE_MOCK", null);
        Environment.SetEnvironmentVariable("APEX_NO_PERSIST", "1");
        // The session journal too: in a throwaway folder (the timed launches inherit it), never the user's own. Reading
        // it would restore the last run's test edits into this one; writing it would leave them for the user.
        if (Environment.GetEnvironmentVariable("APEX_SESSION_DIR") is not { Length: > 0 })
            Environment.SetEnvironmentVariable("APEX_SESSION_DIR",
                Path.Combine(Path.GetTempPath(), "apex-perf-live-session-" + Guid.NewGuid().ToString("N")[..8]));
        using var session = HeadlessUnitTestSession.StartNew(typeof(Program));
        await session.Dispatch(() =>
        {
            Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
            try { run(); }
            catch (Exception ex) { Check($"perf gates (live): {ex}", false); }
            return Task.CompletedTask;
        }, default);
        Console.WriteLine(_failures == 0 ? "ALL CHECKS PASSED" : $"{_failures} CHECK(S) FAILED");
        Environment.Exit(_failures == 0 ? 0 : 1);
        return 0;
    }

    /// <summary>
    /// Guarded end-to-end smoke over the real ingestion data layer (schemas + GDT index). Does not
    /// touch the UI and never runs in CI without a BO3 install. Failures here are reported but do not
    /// fail the harness, so machines without the game still pass.
    /// </summary>
    private static void LiveSmoke()
    {
        var saved = Environment.GetEnvironmentVariable("APEX_FORCE_MOCK");
        Environment.SetEnvironmentVariable("APEX_FORCE_MOCK", null);
        try
        {
            var env = new Apex.Editor.Services.Gdt.GameEnvironment();
            if (!env.IsAvailable)
            {
                Console.WriteLine("live smoke: BO3 not found — skipped (mock-only machine)");
                return;
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var schemas = Apex.Editor.Services.Gdf.GdfSchemaLoader.LoadAll(env.DeffilesDir!);
            var schemaMs = sw.ElapsedMilliseconds;
            Console.WriteLine($"live smoke: {schemas.Count} schemas in {schemaMs} ms " +
                $"(xmodel filename={(schemas.TryGetValue("xmodel", out var xm) && xm.Find("filename") is not null)}, " +
                $"bulletweapon props={(schemas.TryGetValue("bulletweapon", out var bw) ? bw.Properties.Count : 0)})");

            sw.Restart();
            var db = Apex.Editor.Services.Gdt.GdtLoader.LoadAll(env);
            var indexMs = sw.ElapsedMilliseconds;
            var derived = db.Assets.Count(a => a.Parent is not null);
            Console.WriteLine($"live smoke: {db.Gdts.Count:N0} GDTs · {db.Assets.Count:N0} assets " +
                $"({derived:N0} derived) indexed in {indexMs} ms");

            LiveDisplayRules(schemas, db);
            LiveArrangeKeepsPlace(db);
            LiveMaterialRules(db);
            LivePreviewIsolation(env, db);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"live smoke: error {ex.Message}");
        }
        finally
        {
            Environment.SetEnvironmentVariable("APEX_FORCE_MOCK", saved);
        }
    }

    /// <summary>
    /// Live-only checks for APE's conditional property rules: the deffile AngelScript re-executed
    /// per asset must drive hidden/disabled state and respond to edits. Reported like the rest of
    /// the live smoke — informative on machines without BO3, but real FAILs where the data exists.
    /// </summary>
    private static void LiveDisplayRules(
        System.Collections.Generic.IReadOnlyDictionary<string, Apex.Editor.Models.AssetSchema> schemas,
        Apex.Editor.Models.AssetDatabase db)
    {
        Apex.Editor.Models.SchemaRegistry.Populate(schemas);
        var bw = schemas["bulletweapon"];
        Check("live rules: 'mods' filed under Weapon Perks (ShowEntry recategorize)",
            bw.Find("mods")?.Category == "Weapon Perks");
        Check("live rules: internal entries hidden by default (.Show(false))",
            bw.Find("twoHanded") is { DefaultVisible: false } && bw.Find("adsTransBlendTime") is { DefaultVisible: false });

        // Full editor path on a real weapon: hidden internals, enable-gating, and live re-evaluation.
        var byName = db.Assets
            .GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var target = db.Assets.First(a =>
            a.Type.Equals("bulletweapon", StringComparison.OrdinalIgnoreCase)
            && a.ScanProperties.GetValueOrDefault("bulletImpactExplode", "0") == "0");
        var tab = new AssetEditorViewModel(target, (_, _) => { }, _ => { }, _ => { },
            (_, name) => byName.GetValueOrDefault(name));

        var all = tab.AllSentinel.All;
        Check("live rules: twoHanded row hidden in editor",
            all.First(p => p.Key == "twoHanded").IsRuleHidden);
        Check($"live rules: hidden rows excluded from visible count ({tab.VisiblePropertyCount}/{tab.PropertyCount})",
            tab.VisiblePropertyCount < tab.PropertyCount);

        var explosion = all.First(p => p.Key == "explosionRadius");
        var gate = all.First(p => p.Key == "bulletImpactExplode");
        Check("live rules: explosionRadius disabled while bulletImpactExplode off",
            explosion.IsRuleDisabled && !explosion.IsRuleHidden);
        gate.RawValue = "1";
        Check("live rules: enabling bulletImpactExplode re-enables explosionRadius",
            !explosion.IsRuleDisabled);
        gate.RawValue = "0";
        Check("live rules: disabling gates it again", explosion.IsRuleDisabled);
    }

    /// <summary>
    /// The Preview pane in mock mode, driven with real input: the subject picker menu, image channel pills, the empty
    /// state without the install, the debounced subject rebuild and referenced-asset previews following edits.
    /// </summary>
    private static void PreviewPaneChecks(Window window, MainViewModel vm, string outDir)
    {
        // Subject picker: a menu, the shown subject checked, arrow keys + Enter pick another.
        vm.OpenByName("mtl_marble_03");
        Dispatcher.UIThread.RunJobs();
        var dock = window.GetVisualDescendants().OfType<PreviewDockView>().First();
        var subjectButton = dock.GetVisualDescendants().OfType<Button>().First(b => b.Name == "SubjectButton");
        Check($"preview picker: {vm.PreviewSubjects.Count} subjects for a material", vm.HasPreviewChoices && subjectButton.IsEffectivelyEnabled);
        Click(window, subjectButton);
        var menu = typeof(PreviewDockView).GetField("_subjectMenu", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?.GetValue(dock) as MenuFlyout;
        var items = menu?.Items.OfType<MenuItem>().ToList() ?? new();
        Check($"preview picker: click opens a menu of {items.Count}, current checked",
            menu is { IsOpen: true } && items.Count == vm.PreviewSubjects.Count && items[0].IsChecked && items.Skip(1).All(i => !i.IsChecked));
        Capture(window, Path.Combine(outDir, "26e-preview-subject-menu.png"));
        if (items.Count > 1 && TopLevel.GetTopLevel(items[0]) is { } popup)
        {
            // The menu opens on its first item (the subject shown); Down moves to the next.
            popup.KeyPress(Avalonia.Input.Key.Down, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.ArrowDown, null);
            popup.KeyPress(Avalonia.Input.Key.Enter, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.Enter, null);
            Dispatcher.UIThread.RunJobs();
            Check($"preview picker: Down Enter shows '{vm.PreviewSubject?.Label}'",
                vm.PreviewSubject == vm.PreviewSubjects[1] && menu?.IsOpen == false);
        }
        menu?.Hide();

        // Image preview (the picked texture): channel pills click, "N" says what it is, the error line is plain.
        if (vm.CurrentPreview?.Content is ImagePreviewViewModel image)
        {
            WaitFor(() => image.ShowError);
            Check($"image preview: plain error without the install ('{image.Error}')", image.Error == "Image previews need the BO3 install.");
            var pills = dock.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ToggleButton>().Where(t => t.DataContext is ChannelModeOption).ToList();
            var normal = pills.FirstOrDefault(p => ((ChannelModeOption)p.DataContext!).Label == "N");
            if (normal is not null)
            {
                Click(window, normal);
                Check($"image preview: clicking N shows normal Z ('{ToolTip.GetTip(normal)}')",
                    image.Modes.Single(m => m.IsActive).Label == "N" && ToolTip.GetTip(normal) is string tip && tip.StartsWith("Normal Z"));
                Console.WriteLine($"info  channel pill hit area {normal.Bounds.Width:0}×{normal.Bounds.Height:0} (24 px minimum comes from the shared pill.small style)");
            }
            Capture(window, Path.Combine(outDir, "26f-preview-image.png"));
        }

        // Nothing renders without the install: the empty state says why instead of "nothing to render".
        vm.OpenByName("t7_weapon_smg_needle_view");
        Dispatcher.UIThread.RunJobs();
        Check($"preview empty state: '{vm.PreviewEmptyText}'", !vm.HasPreview && vm.PreviewEmptyText.Contains("BO3 install"));

        // Reference edits rebuild the subject list once, after typing stops.
        vm.OpenByName("wpn_smg_needle");
        var weapon = vm.ActiveTab!;
        var camo = weapon.AllSentinel.All.First(p => p.Key == "camoMaterial");
        var original = camo.RawValue;
        int rebuilds = 0;
        void Count(object? s, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => rebuilds++;
        vm.PreviewSubjects.CollectionChanged += Count;
        foreach (var value in new[] { "mtl_marble_0", "mtl_marble_", "mtl_marble_03" })
            camo.RawValue = value;
        var immediate = rebuilds;
        Pump(300);
        vm.PreviewSubjects.CollectionChanged -= Count;
        Check($"preview subjects: 3 reference keystrokes → {immediate} rebuilds at once, {rebuilds} after 150 ms ({vm.PreviewSubject?.Record.Name})",
            immediate == 0 && rebuilds == 1 && vm.PreviewSubject?.Record.Name == "mtl_marble_03");

        // A referenced-asset preview follows edits made to that asset in its own tab.
        var shown = vm.CurrentPreview?.Content as MaterialPreviewViewModel;
        vm.OpenByName("mtl_marble_03");
        vm.ActiveTab!.AllSentinel.All.First(p => p.Key == "materialType").RawValue = "lit_emissive";
        vm.OpenByName("wpn_smg_needle");
        Pump(300);
        Check($"preview subjects: the weapon's material preview follows an edit in the material's tab ('{shown?.Summary}')",
            shown?.Summary?.Contains("lit_emissive") == true && vm.CurrentPreview?.Content == shown);
        camo.RawValue = original;
        Pump(300);

        // ⤢ says what it will do.
        var maximize = dock.GetVisualDescendants().OfType<Button>().First(b => b.Name == "MaximizeButton");
        vm.Layout = WorkspaceLayout.Preview;
        Dispatcher.UIThread.RunJobs();
        Check($"preview: ⤢ offers to restore while maximized ('{ToolTip.GetTip(maximize)}')", (ToolTip.GetTip(maximize) as string)?.StartsWith("Restore") == true);
        vm.Layout = WorkspaceLayout.Edit;
    }

    /// <summary>
    /// A reference to nothing is said on its own row (the ⚠ and its message) and nowhere else: it offers no →, F12 on
    /// it does nothing, and no window-wide alert appears. A reference that resolves still opens with a real click on →.
    /// </summary>
    private static void MissingReferenceChecks(Window window, MainViewModel vm)
    {
        vm.OpenByName("wpn_smg_wasp");
        var tab = vm.ActiveTab!;
        var refs = tab.AllSentinel.All.OfType<RefPropertyViewModel>().ToList();
        var missing = refs.First(r => r.Key == "camoMaterial");
        Check($"missing reference: its row carries the message ('{missing.Problem}')", missing.HasProblem && !missing.CanGoTo);
        tab.RevealProperty(missing.Key);
        Pump();
        Check("missing reference: its row offers no →", RowArrow(window, missing) is null);
        Key(window, Avalonia.Input.Key.F12);
        Check("missing reference: F12 on it stays put and raises no alert", !vm.IsAlertOpen && vm.ActiveTab == tab);

        var live = refs.First(r => r.CanGoTo);
        tab.RevealProperty(live.Key);
        Pump();
        var go = RowArrow(window, live);
        if (go is not null)
            Click(window, go);
        Check($"reference: a real click on {live.Key}'s open arrow opens {live.Value} ({vm.ActiveTab?.Name})",
            go is not null && string.Equals(vm.ActiveTab?.Name, live.Value, StringComparison.OrdinalIgnoreCase) && !vm.IsAlertOpen);
        // It opened as a preview tab; close it so the preview-tab checks further on start from none.
        if (vm.ActiveTab is { IsPreview: true })
            vm.CloseActiveTabCommand.Execute(null);

        // A Ctrl+click on the name follows it too, as in a code editor; a plain click only puts the caret there.
        vm.OpenAsset(tab.Record);
        tab.RevealProperty(live.Key);
        Pump();
        var name = RefFieldOf(window, live);
        if (name is not null)
        {
            var at = name.TranslatePoint(new Point(40, name.Bounds.Height / 2), window)!.Value;
            window.MouseDown(at, Avalonia.Input.MouseButton.Left);
            window.MouseUp(at, Avalonia.Input.MouseButton.Left);
            Pump();
            Check($"reference: a plain click on the name stays put ({vm.ActiveTab?.Name})", vm.ActiveTab == tab);
            System.Threading.Thread.Sleep(600);
            window.MouseDown(at, Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.Control);
            window.MouseUp(at, Avalonia.Input.MouseButton.Left, Avalonia.Input.RawInputModifiers.Control);
            Pump();
        }
        Check($"reference: Ctrl+click on {live.Key}'s name opens {live.Value} ({vm.ActiveTab?.Name})",
            name is not null && string.Equals(vm.ActiveTab?.Name, live.Value, StringComparison.OrdinalIgnoreCase));
        if (vm.ActiveTab is { IsPreview: true })
            vm.CloseActiveTabCommand.Execute(null);
    }

    private static Apex.Editor.Views.RefField? RefFieldOf(Window window, RefPropertyViewModel item) =>
        window.GetVisualDescendants().OfType<Apex.Editor.Views.RefField>()
            .FirstOrDefault(f => ReferenceEquals(f.DataContext, item) && f.IsEffectivelyVisible);

    /// <summary>The reference field's open arrow, while it shows (a name that resolves).</summary>
    private static Button? RowArrow(Window window, RefPropertyViewModel item) =>
        RefFieldOf(window, item)?.GoToButton is { IsEffectivelyVisible: true } go ? go : null;

    /// <summary>A real left click (press + release) at the centre of <paramref name="control"/>.</summary>
    private static void Click(Window window, Control control)
    {
        Dispatcher.UIThread.RunJobs();
        var at = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)
                 ?? throw new InvalidOperationException($"{control} is not in the window");
        window.MouseDown(at, Avalonia.Input.MouseButton.Left);
        window.MouseUp(at, Avalonia.Input.MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Runs the dispatcher for <paramref name="ms"/> with timers live (RunJobs alone never fires a DispatcherTimer
    /// in the headless session), e.g. to let a 150 ms debounce settle.</summary>
    /// <summary>Runs pending dispatcher work; with <paramref name="ms"/>, keeps the dispatcher running (timers included —
    /// RunJobs alone never fires a DispatcherTimer) for that long.</summary>
    private static void Pump(int ms = 0)
    {
        Dispatcher.UIThread.RunJobs();
        if (ms <= 0)
            return;
        var frame = new DispatcherFrame();
        DispatcherTimer.RunOnce(() => frame.Continue = false, TimeSpan.FromMilliseconds(ms));
        Dispatcher.UIThread.PushFrame(frame);
    }

    /// <summary>Pumps the dispatcher until <paramref name="done"/> holds (background work marshalled back), up to ~2 s.</summary>
    private static void WaitFor(Func<bool> done)
    {
        for (var i = 0; i < 100 && !done(); i++)
        {
            System.Threading.Thread.Sleep(20);
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static int _failures;

    private static void Check(string label, bool ok)
    {
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {label}");
        if (!ok)
            _failures++;
        Record(label, ok ? "pass" : "fail");
    }

    private static void Capture(Window window, string path)
    {
        Dispatcher.UIThread.RunJobs();
        var frame = window.CaptureRenderedFrame();
        if (frame is null)
        {
            Console.WriteLine($"WARN: no frame for {path}");
            return;
        }
        // Saved at once: the bitmap is the window's last frame, and a later render replaces it.
        frame.Save(path, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        Console.WriteLine($"wrote {path}");
        // Every shot also proves the recycled rows in it show their own item (reported only when one doesn't).
        var shot = Path.GetFileName(path);
        if (window.GetVisualDescendants().OfType<AssetBrowserView>().FirstOrDefault()?.FindControl<ListBox>("Tree") is { } tree
            && TreeRowsShowingOthers(tree) is { Count: > 0 } wrongTree)
            Check($"{shot}: Explorer rows show their own names ({string.Join("; ", wrongTree.Take(3))})", false);
        if (window.GetVisualDescendants().OfType<AssetEditorView>().Any() && RowsShowingOthers(window) is { Count: > 0 } wrongRows)
            Check($"{shot}: editor rows show their own properties ({string.Join("; ", wrongRows.Take(3))})", false);
    }
}
