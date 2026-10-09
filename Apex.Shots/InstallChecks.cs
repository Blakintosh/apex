using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Apex.Editor.Services;
using Apex.Editor.Services.Gdt;
using Apex.Editor.ViewModels;
using Apex.Editor.Views;

namespace Apex.Shots;

/// <summary>
/// First run without an install: detection finds nothing (APEX_SIMULATE_NO_INSTALL), the workspace says so and offers
/// Locate…, a wrong folder is explained, a right one loads at once and is remembered for the next launch. Real clicks and
/// keys on the Locate… button; the folder picker is answered by the harness. Settings go to a temp folder
/// (APEX_SETTINGS_DIR), never the user's.
/// </summary>
public partial class Program
{
    private static void RunInstallChecks(string outDir)
    {
        // What detection costs at startup (UI thread, before the window shows), with the probes and the Steam lookup.
        var savedMock = Environment.GetEnvironmentVariable("APEX_FORCE_MOCK");
        Environment.SetEnvironmentVariable("APEX_FORCE_MOCK", null);
        var detectMs = new System.Collections.Generic.List<double>();
        GameEnvironment? detected = null;
        for (var i = 0; i < 7; i++)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            detected = new GameEnvironment(null);
            detectMs.Add(sw.Elapsed.TotalMilliseconds);
        }
        Environment.SetEnvironmentVariable("APEX_FORCE_MOCK", savedMock);
        detectMs.Sort();
        Console.WriteLine($"perf  install detection: median {detectMs[3]:F2} ms, max {detectMs[^1]:F2} ms (found: {detected?.Bo3Root ?? "none"})");

        // Mock data is only ever what the harness asked for.
        var mockVm = new MainViewModel(sessionRoot: null);
        Check("install: APEX_FORCE_MOCK gives sample data, not the not-found state", mockVm.IsMockData && !mockVm.IsInstallMissing);
        mockVm.Dispose();

        var scratch = NewScratch("locate");
        var install = Path.Combine(scratch, "Call of Duty Black Ops III");
        Directory.CreateDirectory(Path.Combine(install, "deffiles"));
        Directory.CreateDirectory(Path.Combine(install, "source_data"));
        Directory.CreateDirectory(Path.Combine(install, "bin"));
        File.WriteAllText(Path.Combine(install, "source_data", "apex_locate.gdt"),
            "{\r\n\t\"apex_locate_model\" ( \"xmodel.gdf\" )\r\n\t{\r\n\t\t\"filename\" \"apex\\\\locate.xmodel_bin\"\r\n\t}\r\n}\r\n");
        var wrong = Path.Combine(scratch, "mods");
        Directory.CreateDirectory(Path.Combine(wrong, "source_data"));
        var empty = Path.Combine(scratch, "Downloads");
        Directory.CreateDirectory(empty);
        var settingsDir = Path.Combine(scratch, "settings");

        var saved = (Mock: Environment.GetEnvironmentVariable("APEX_FORCE_MOCK"),
            NoInstall: Environment.GetEnvironmentVariable("APEX_SIMULATE_NO_INSTALL"),
            Settings: Environment.GetEnvironmentVariable("APEX_SETTINGS_DIR"));
        var realPicker = MainWindow.PickInstallFolder;
        string? nextPick = null;
        var picks = 0;
        MainWindow.PickInstallFolder = _ => { picks++; return Task.FromResult(nextPick); };
        Environment.SetEnvironmentVariable("APEX_FORCE_MOCK", null);
        Environment.SetEnvironmentVariable("APEX_SIMULATE_NO_INSTALL", "1");
        Environment.SetEnvironmentVariable("APEX_SETTINGS_DIR", settingsDir);
        MainViewModel? vm = null;
        MainWindow? window = null;
        try
        {
            // Folder checks, as Locate… makes them.
            Check("install check: the install folder itself", GameEnvironment.Check(install, out _) == install);
            Check("install check: a folder inside it (bin) resolves to the install",
                GameEnvironment.Check(Path.Combine(install, "bin"), out _) == install);
            Check("install check: a folder with only source_data names deffiles as missing",
                GameEnvironment.Check(wrong, out var m1) is null && m1.SequenceEqual(new[] { "deffiles" }));
            Check("install check: an unrelated folder names both",
                GameEnvironment.Check(empty, out var m2) is null && m2.SequenceEqual(new[] { "deffiles", "source_data" }));

            // Paths: a drive root keeps its separator (D: alone is drive-relative), anything else loses a trailing one.
            Check($"install path: a drive root stays a root ({GameEnvironment.Normalize(@"D:\")})",
                GameEnvironment.Normalize(@"D:\") == @"D:\" && Path.Combine(GameEnvironment.Normalize(@"D:\")!, "deffiles") == @"D:\deffiles");
            Check("install path: a folder loses its trailing separator and stray spaces",
                GameEnvironment.Normalize(@" D:\Games\Call of Duty Black Ops III\ ") == @"D:\Games\Call of Duty Black Ops III"
                && GameEnvironment.Normalize(@"\\nas\games\bo3\") == @"\\nas\games\bo3");
            Check("install path: nothing is not a path", GameEnvironment.Normalize("  ") is null && GameEnvironment.Normalize(null) is null);

            // Steam's library list, in today's format and the one before mid-2021.
            const string modernVdf = "\"libraryfolders\"\n{\n\t\"0\"\n\t{\n\t\t\"path\"\t\t\"C:\\\\Program Files (x86)\\\\Steam\"\n\t\t\"label\"\t\t\"\"\n\t}\n\t\"1\"\n\t{\n\t\t\"path\"\t\t\"D:\\\\SteamLibrary\"\n\t}\n}\n";
            const string legacyVdf = "\"LibraryFolders\"\n{\n\t\"TimeNextStatsReport\"\t\t\"1600000000\"\n\t\"ContentStatsID\"\t\t\"-123\"\n\t\"1\"\t\t\"D:\\\\SteamLibrary\"\n\t\"2\"\t\t\"E:\\\\Games\\\\Steam\"\n}\n";
            Check("steam: today's libraryfolders.vdf lists its libraries",
                GameEnvironment.ParseLibraryFolders(modernVdf).SequenceEqual(new[] { @"C:\Program Files (x86)\Steam", @"D:\SteamLibrary" }));
            Check("steam: a pre-2021 libraryfolders.vdf lists its libraries",
                GameEnvironment.ParseLibraryFolders(legacyVdf).SequenceEqual(new[] { @"D:\SteamLibrary", @"E:\Games\Steam" }));

            // A folder on a network share that doesn't answer is given up on quickly, not after the SMB timeout.
            var unreachable = System.Diagnostics.Stopwatch.StartNew();
            var noShare = GameEnvironment.Check(@"\\apex-no-such-host-7f3e\games\bo3", out _);
            unreachable.Stop();
            Check($"install check: an unreachable share answers within the probe timeout ({unreachable.ElapsedMilliseconds} ms)",
                noShare is null && unreachable.ElapsedMilliseconds < GameEnvironment.ProbeTimeout.TotalMilliseconds + 500);

            // Locate starts the live load with the schemas not ready, so nothing validates against an empty schema set.
            var early = new MainViewModel(sessionRoot: null);
            var started = early.TryUseInstall(install);
            var schemasReady = (bool)typeof(MainViewModel)
                .GetField("_schemasReady", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(early)!;
            Check("install: right after Locate the schemas count as not loaded yet", started && !schemasReady);
            WaitUntil(() => early.Status.StartsWith("Loaded", StringComparison.Ordinal), 30_000);
            early.Dispose();
            UiSettings.Write("{}"); // forget that root again: the window below starts not found

            vm = new MainViewModel(Path.Combine(scratch, "session"));
            window = ShowJournalWindow(vm);
            var locate = window.FindControl<Button>("LocateButton")!;
            var layer = window.FindControl<Border>("InstallMissingLayer")!;
            Pump(50);
            Check("install: not found shows the not-found state, not sample data",
                vm.IsInstallMissing && !vm.IsMockData && layer.IsEffectivelyVisible && vm.TotalCount == 0);
            Check("install: the window opens on Locate… (keyboard first)", locate.IsFocused);
            Check("install: no command applies without an install", vm.Registry.All.All(c => !c.CanRun));
            var deadControls = new[] { "CommandBox", "SessionChip", "UndoButton", "RedoButton" }
                .Where(n => window.FindControl<Control>(n)?.IsEffectivelyVisible != false).ToList();
            Check($"install: the title bar shows nothing that can't run yet ({string.Join(", ", deadControls)})", deadControls.Count == 0);

            // Modal: Ctrl+P opens nothing, and Tab cycles through Locate… and the window's own buttons only.
            Key(window, Avalonia.Input.Key.P, RawInputModifiers.Control);
            Pump();
            Check("install: Ctrl+P does nothing behind the not-found state", !vm.IsPaletteOpen);
            var reached = new System.Collections.Generic.List<string>();
            var leaked = false;
            for (var i = 0; i < 20; i++)
            {
                Key(window, Avalonia.Input.Key.Tab);
                Pump();
                if (window.FocusManager?.GetFocusedElement() is Control focused)
                {
                    reached.Add(focused.Name ?? focused.GetType().Name);
                    leaked |= focused != locate && !focused.GetVisualAncestors().Contains(window.FindControl<Grid>("TopBar"));
                }
            }
            Check($"install: Tab never reaches the panes behind ({string.Join(", ", reached.Distinct())})",
                !leaked && reached.Contains("LocateButton"));
            locate.Focus(NavigationMethod.Tab);
            Capture(window, Path.Combine(outDir, "70-install-missing.png"));

            // Cancelling the picker changes nothing.
            nextPick = null;
            Click(window, locate);
            Check("install: cancelling the picker leaves the state as it was",
                picks == 1 && vm.IsInstallMissing && vm.LocateError.Length == 0);

            // A wrong folder, picked with the keyboard: Enter on the focused button.
            var errorText = window.FindControl<TextBlock>("LocateErrorText")!;
            window.UpdateLayout();
            var buttonTop = locate.TranslatePoint(new Point(0, 0), window)?.Y;
            nextPick = wrong;
            locate.Focus(NavigationMethod.Tab);
            Key(window, Avalonia.Input.Key.Enter);
            Pump();
            window.UpdateLayout();
            Check($"install: Enter on Locate… with a wrong folder says what's missing ('{vm.LocateError}')",
                picks == 2 && vm.IsInstallMissing && vm.LocateError.Contains("deffiles") && !vm.LocateError.Contains("source_data")
                && vm.LocateError.StartsWith("No deffiles folder in mods.", StringComparison.Ordinal));
            Check($"install: the explanation moves nothing (its space was held; button at {buttonTop} then {locate.TranslatePoint(new Point(0, 0), window)?.Y})",
                Math.Abs((locate.TranslatePoint(new Point(0, 0), window)?.Y ?? -100) - (buttonTop ?? 0)) < 0.5 && errorText.IsEffectivelyVisible);
            Capture(window, Path.Combine(outDir, "71-install-wrong-folder.png"));

            nextPick = empty;
            Click(window, locate);
            Check($"install: an unrelated folder names both folders ('{vm.LocateError}')",
                vm.LocateError.Contains("deffiles or source_data"));

            Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
            Capture(window, Path.Combine(outDir, "72-light-install-wrong-folder.png"));
            nextPick = null;
            Application.Current.RequestedThemeVariant = ThemeVariant.Dark;

            // The folder that holds the install (a Steam library's common folder): found below it, loads straight away,
            // no restart. (A folder picked inside the install resolves upward: the "bin" check above.)
            nextPick = scratch;
            Click(window, locate);
            Check("install: a valid folder hides the not-found state at once",
                !vm.IsInstallMissing && !layer.IsEffectivelyVisible && vm.LocateError.Length == 0);
            Pump(50);
            Check($"install: the keyboard lands in the Explorer's search ({(window.FocusManager?.GetFocusedElement() as Control)?.Name})",
                (window.FocusManager?.GetFocusedElement() as Control)?.Name == "SearchBox");
            Check("install: commands apply again", vm.Registry[Apex.Editor.Commands.CommandCatalog.QuickOpen].CanRun);
            WaitUntil(() => vm.Status.StartsWith("Loaded", StringComparison.Ordinal), 30_000);
            Check($"install: and loads its GDTs in this window ('{vm.Status}')",
                vm.Status.StartsWith("Loaded", StringComparison.Ordinal) && vm.TotalCount == 1 && !vm.IsMockData);
            Check("install: the status bar doesn't call it sample data", !window.FindControl<TextBlock>("MockDataText")!.IsVisible);
            Check("install: the session is kept for the install once it is found", vm.IsSessionKept);
            vm.OpenByName("apex_locate_model");
            Check("install: its assets open", vm.ActiveTab?.Name == "apex_locate_model");
            Capture(window, Path.Combine(outDir, "73-install-located.png"));

            var remembered = UiSettings.Load().Bo3Root;
            Check($"install: the root is remembered in the settings ({remembered})", remembered == install);
            window.Close();
            vm.Dispose();
            vm = null;

            // Next launch: detection still finds nothing, the remembered root is used.
            var next = new MainViewModel(sessionRoot: null);
            Check("install: the next launch uses the remembered root", !next.IsInstallMissing && !next.IsMockData);
            next.Dispose();

            // The remembered folder is gone: back to detection, which (here) finds nothing.
            Directory.Move(install, install + " (moved)");
            var gone = new MainViewModel(sessionRoot: null);
            Check("install: a remembered root that has gone falls back to detection", gone.IsInstallMissing);
            window = ShowJournalWindow(gone);
            Application.Current.RequestedThemeVariant = ThemeVariant.Light;
            Capture(window, Path.Combine(outDir, "74-light-install-missing.png"));
            Application.Current.RequestedThemeVariant = ThemeVariant.Dark;
            window.Close();
            window = null;
            gone.Dispose();
        }
        catch (Exception ex)
        {
            Check($"install: {ex}", false);
        }
        finally
        {
            Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
            window?.Close();
            vm?.Dispose();
            MainWindow.PickInstallFolder = realPicker;
            Environment.SetEnvironmentVariable("APEX_FORCE_MOCK", saved.Mock);
            Environment.SetEnvironmentVariable("APEX_SIMULATE_NO_INSTALL", saved.NoInstall);
            Environment.SetEnvironmentVariable("APEX_SETTINGS_DIR", saved.Settings);
            Apex.Editor.Models.SchemaRegistry.ResetToMock();
            try { Directory.Delete(scratch, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
