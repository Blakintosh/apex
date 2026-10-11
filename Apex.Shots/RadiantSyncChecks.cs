using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Apex.Editor.Models;
using Apex.Editor.Services;
using Apex.Editor.ViewModels;
using Apex.Editor.Views;
using Avalonia.Headless;
using Avalonia.Input;

namespace Apex.Shots;

/// <summary>
/// Telling Radiant about changes, as APE does: each changed value a line in the install's gdtDB\live\changes.log, and
/// gdtdb.exe /update after a save. On a temp install with its own gdtDB\live and a stand-in gdtdb.exe.
/// </summary>
public partial class Program
{
    private static void RunRadiantSyncChecks(string outDir)
    {
        // ── The line, as APE's fprintf("%s 0 %s %s %s %s\n") writes it ──
        Check("radiant sync: the sender is APE's %016.16llX of (time << 32 | process id)",
            RadiantSync.SenderId(DateTimeOffset.FromUnixTimeSeconds(0x5F3A1C00), 0x1234) == "5F3A1C0000001234");
        Check("radiant sync: a line is '<sender> 0 <type> <asset> <key> <value>', the value's spaces kept and line breaks not",
            RadiantSync.Line("5F3A1C0000001234", "bulletweapon", "ar_x", "displayName", "Lynx CQ300\r\nZM")
            == "5F3A1C0000001234 0 bulletweapon ar_x displayName Lynx CQ300  ZM");

        var install = NewScratch("radiant-install");
        CopyTree(Path.Combine(InstallRoot, "deffiles"), Path.Combine(install, "deffiles"));
        var rel = @"source_data\ar_cq300_h1.gdt";
        var path = Path.Combine(install, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.Copy(Path.Combine(InstallRoot, rel), path);
        var live = Path.Combine(install, "gdtDB", "live");
        Directory.CreateDirectory(live);
        var log = Path.Combine(live, "changes.log");
        // A stand-in for gdtdb.exe that exits at once whatever it is asked.
        Directory.CreateDirectory(Path.Combine(install, "gdtdb"));
        File.Copy(Path.Combine(Environment.SystemDirectory, "whoami.exe"), Path.Combine(install, "gdtdb", "gdtdb.exe"));

        var saved = (Mock: Environment.GetEnvironmentVariable("APEX_FORCE_MOCK"), Root: Environment.GetEnvironmentVariable("APEX_BO3_ROOT"),
            Persist: Environment.GetEnvironmentVariable("APEX_NO_PERSIST"), Sync: Environment.GetEnvironmentVariable("APEX_NO_RADIANT_SYNC"));
        Environment.SetEnvironmentVariable("APEX_FORCE_MOCK", null);
        Environment.SetEnvironmentVariable("APEX_BO3_ROOT", install);
        Environment.SetEnvironmentVariable("APEX_NO_PERSIST", "1");
        Environment.SetEnvironmentVariable("APEX_NO_RADIANT_SYNC", null);
        MainViewModel? vm = null;
        MainWindow? window = null;
        try
        {
            vm = new MainViewModel(Path.Combine(install, "session"));
            window = ShowJournalWindow(vm);
            WaitUntil(() => vm.Status.StartsWith("Loaded"), 60_000);
            var sync = vm.RadiantSync;
            Check("radiant sync: an install's window tells Radiant (on by default, like APE's EnableSync)",
                sync is { Enabled: true } && vm.SyncWithRadiant);
            if (sync is null)
                return;
            string[] Lines() => File.Exists(log) ? File.ReadAllLines(log) : [];

            var root = DatabaseOf(vm).Assets.Single(x => x.Name == "ar_cq300_h1_zm");
            vm.OpenAsset(root);
            Pump();
            var tab = vm.ActiveTab!;
            var name = tab.AllSentinel.All.OfType<PropertyItemViewModel>().First(p => p.Key == "displayName");
            var before = name.RawValue;
            name.RawValue = "Lynx CQ300 Apex";
            Pump(400);
            var lines = Lines();
            Check($"radiant sync: an edit is one line in gdtDB\\live\\changes.log ({string.Join(" | ", lines)})",
                lines.Length == 1 && lines[0] == $"{sync.Sender} 0 bulletweapon ar_cq300_h1_zm displayName Lynx CQ300 Apex");
            Check($"radiant sync: the sender is this process ({sync.Sender})",
                Regex.IsMatch(sync.Sender, "^[0-9A-F]{16}$") && Convert.ToUInt32(sync.Sender[8..], 16) == (uint)Environment.ProcessId);

            // Typing (or a slider's scrub) sends where it ended, once.
            name.RawValue = "L";
            name.RawValue = "Ly";
            name.RawValue = "Lyn";
            Pump(400);
            lines = Lines();
            Check($"radiant sync: a burst of edits sends its last value once ({lines.Length} lines)",
                lines.Length == 2 && lines[1].EndsWith(" displayName Lyn"));

            tab.UndoCommand.Execute(null);
            Pump(400);
            lines = Lines();
            // Each set above was its own step: undo takes back the last one.
            Check($"radiant sync: undo is a change too ('{lines.LastOrDefault()}')",
                lines.Length == 3 && lines[2].EndsWith(" displayName Ly"));

            window.KeyPress(Avalonia.Input.Key.S, RawInputModifiers.Control, PhysicalKey.S, "s");
            WaitUntil(() => !vm.IsSaveRunning, 10_000);
            Pump();
            Check($"radiant sync: a save runs gdtdb.exe /update once, and sends no line for what it wrote ({sync.UpdatesStarted}, {sync.LastError})",
                sync.UpdatesStarted == 1 && sync.LastError is null && Lines().Length == 3);

            // The app menu's "Sync changes with Radiant" (a toggle, like APE's checkbox).
            var toggle = vm.Registry.All.First(c => c.Id == Apex.Editor.Commands.CommandCatalog.SyncWithRadiant);
            var shownOn = toggle.CanRun && toggle.CurrentlyChecked;
            toggle.Execute();
            Check($"radiant sync: the menu's toggle shows the setting and turns it off ({shownOn} → {toggle.CurrentlyChecked})",
                shownOn && !toggle.CurrentlyChecked && !vm.SyncWithRadiant && !sync.Enabled);
            name.RawValue = before;
            Pump(400);
            window.KeyPress(Avalonia.Input.Key.S, RawInputModifiers.Control, PhysicalKey.S, "s");
            WaitUntil(() => !vm.IsSaveRunning, 10_000);
            Pump();
            Check("radiant sync: switched off, an edit and its save tell nobody", Lines().Length == 3 && sync.UpdatesStarted == 1);
        }
        finally
        {
            window?.Close();
            vm?.Dispose();
            Environment.SetEnvironmentVariable("APEX_FORCE_MOCK", saved.Mock);
            Environment.SetEnvironmentVariable("APEX_BO3_ROOT", saved.Root);
            Environment.SetEnvironmentVariable("APEX_NO_PERSIST", saved.Persist);
            Environment.SetEnvironmentVariable("APEX_NO_RADIANT_SYNC", saved.Sync);
            SchemaRegistry.ResetToMock();
            try { Directory.Delete(install, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
