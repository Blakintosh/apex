using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Apex.Editor.Commands;
using Apex.Editor.Services.Stock;
using Apex.Editor.ViewModels;
using Apex.Editor.Views;

namespace Apex.Shots;

/// <summary>
/// Using stock assets directly: the helper's <c>status</c> verdict (captured from the real helper) decides whether a stock record
/// leads with Use in map or with Create local copy and a one-line reason; the Use in map dialog (copy the name, or add a weapon's
/// zone line to a zone file after a confirm); the clone dialog's Use directly button, on or off with the reason; the Components
/// view. The zone file is a temp one: nothing touches the install.
/// </summary>
public partial class Program
{
    private static StockStatus StatusFixture(string name)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "stock", name)));
        return StockStatus.Parse(doc.RootElement.Clone());
    }

    private static Button ButtonNamed(MainWindow window, string name) => window.GetVisualDescendants().OfType<Button>().First(b => b.Name == name);

    private static bool Shown(MainWindow window, string name) => window.GetVisualDescendants().OfType<Control>().Any(c => c.Name == name && c.IsEffectivelyVisible);

    private static void SetStockStatus(MainViewModel vm, FakeStockBackend fake, StockStatus status)
    {
        fake.Status = status;
        _ = vm.Stock.RefreshStatusAsync();
        WaitFor(() => ReferenceEquals(vm.Stock.Status, status));
        Pump(150);
    }

    private static void StockUseChecks(MainViewModel vm, MainWindow window, FakeStockBackend fake, string temp, string outDir)
    {
        var ok = StatusFixture("status_ok.json");
        var off = StatusFixture("status_blackbird_off.json");

        // ── the verdicts, as the real helper wrote them ──
        Check($"use directly: the helper's status parses ({ok.StocklibVersion}, protocol {ok.Protocol}, Dragon {ok.DragonVersion} {string.Join("/", ok.DragonCapabilities)})",
            ok.Known && ok.UseDirectly.Ok && ok.Browse.Ok && ok.Clone.Ok && ok.Radiant.Ok && ok.DragonPresent && ok.DragonCapabilities.Contains("stock-carry") && ok.Protocol == 1 && ok.Assets > 400_000);
        Check($"use directly: with Blackbird set to the stock linker it is off, with the reason and the fix ({off.UseDirectly.Note})",
            off.Known && !off.UseDirectly.Ok && off.Browse.Ok && off.Clone.Ok && off.UseDirectly.Reason.Contains("turned off", StringComparison.OrdinalIgnoreCase)
            && off.UseDirectly.Note.Contains("Turn on") && off.UseDirectly.Note.EndsWith("."));

        // ── off: the clone-first screen, with a note ──
        SetStockStatus(vm, fake, off);
        Check("use directly off: the window's verdict follows the helper's", !vm.StockUseOk && vm.HasStockUseNote && vm.StockUseNote == off.UseDirectly.Note);
        var master = FindStockRow(vm, "p7_zm_zod_master_switch");
        vm.OpenAsset(master.Asset!);
        Pump();
        WaitFor(() => vm.ActiveTab?.Record.Stock?.Description is not null && vm.ActiveTab?.Record.Stock?.Deps is not null);
        Pump(100);
        window.UpdateLayout();
        var create = ButtonNamed(window, "CreateLocalCopyButton");
        Check("use directly off: Create local copy leads (accent) and there is no Use in map",
            create.IsEffectivelyVisible && create.Classes.Contains("accent") && !Shown(window, "UseInMapButton"));
        var note = window.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Name == "StockUseNote");
        Check($"use directly off: one line says why and how to fix it ('{note?.Text}')", note is { IsEffectivelyVisible: true } && note.Text == off.UseDirectly.Note);
        Check("use directly off: the banner is the clone-first one", vm.ActiveTab!.StockInfo!.Banner.Contains("needs its own copy"));
        Capture(window, Path.Combine(outDir, "stock-record-use-off.png"));

        // ── on: Use in map leads ──
        SetStockStatus(vm, fake, ok);
        window.UpdateLayout();
        Check("use directly on: the window's verdict follows the helper's, and the note is gone", vm.StockUseOk && !vm.HasStockUseNote && vm.StockUseNote == "");
        var use = ButtonNamed(window, "UseInMapButton");
        create = ButtonNamed(window, "CreateLocalCopyButton");
        Check("use directly on: Use in map leads (accent) and Create local copy is the second action (outline)",
            use.IsEffectivelyVisible && use.Classes.Contains("accent") && create.IsEffectivelyVisible && !create.Classes.Contains("accent") && create.Classes.Contains("outline"));
        Check("use directly on: the note is not shown", !Shown(window, "StockUseNote"));
        Check($"use directly on: the banner says to name it ({vm.ActiveTab!.StockInfo!.Banner[..40]}…)", vm.ActiveTab.StockInfo.Banner.StartsWith("Use this model in your map by name: Dragon takes it"));
        Capture(window, Path.Combine(outDir, "stock-record-use-on.png"));

        // ── Use in map: a model copies its name ──
        var copied = new List<string>();
        vm.UseInMapCommand.Execute(null);
        Pump();
        var model = vm.UseDialog;
        Check("use in map: the button opens the dialog", model is not null && vm.IsUseOpen && vm.IsModalOpen && !model.IsWeapon);
        if (model is null)
            return;
        model.CopyText = text =>
        {
            copied.Add(text);
            return System.Threading.Tasks.Task.CompletedTask;
        };
        WaitFor(() => fake.Calls.Any(c => c.StartsWith("deps WEAPON") && c.EndsWith("carry=True")) || fake.Calls.Any(c => c.Contains("carry=True")));
        Check("use in map: it asks the helper what Dragon would carry (a carry walk), and for the map's warnings",
            fake.Calls.Any(c => c.StartsWith("deps XMODEL p7_zm_zod_master_switch zm_guns") && c.EndsWith("carry=True")) && fake.Calls.Any(c => c == "status zm_guns"));
        model.CopyNameCommand.Execute(null);
        WaitFor(() => copied.Count > 0);
        Pump(100);
        Check($"use in map: Copy name puts the name on the clipboard and closes ({string.Join(",", copied)}; {vm.Status})",
            copied.SequenceEqual(new[] { "p7_zm_zod_master_switch" }) && vm.UseDialog is null && vm.Status.StartsWith("Copied p7_zm_zod_master_switch"));
        vm.Registry[CommandCatalog.CloseTab].Execute();

        // ── Use in map: a weapon and its zone line ──
        var weapon = FindStockRow(vm, "smg_standard_zm");
        vm.OpenAsset(weapon.Asset!);
        Pump();
        WaitFor(() => vm.ActiveTab?.Record.Stock?.Description is not null);
        var bo3 = Path.Combine(temp, "use-install");
        var zoneDir = Path.Combine(bo3, "usermaps", "zm_guns", "zone_source");
        Directory.CreateDirectory(zoneDir);
        var zonePath = Path.Combine(zoneDir, "zm_guns.zone");
        const string original = "// zm_guns\r\n>zm_levelcommon\r\nxmodel,my_model\r\nweapon,other_weapon // mine";
        File.WriteAllText(zonePath, original);
        var modDir = Path.Combine(bo3, "mods", "zm_moreguns", "zone_source");
        Directory.CreateDirectory(modDir);
        File.WriteAllText(Path.Combine(modDir, "mp_mod.zone"), "x\n");
        File.WriteAllText(Path.Combine(modDir, "zm_mod.zone"), "y\n");
        var targets = new[] { new CloneTarget("usermap", "zm_guns", "zm_guns"), new CloneTarget("mod", "zm_moreguns", "mods/zm_moreguns") };
        var closed = false;
        string? said = null;
        var dlg = new StockUseViewModel(weapon.Asset!, vm.Stock, bo3, targets, targets[0], () => closed = true, t => said = t, () => { });
        dlg.CopyText = _ => System.Threading.Tasks.Task.CompletedTask;
        vm.UseDialog = dlg;
        dlg.Begin();
        WaitFor(() => !dlg.CarrySummary.StartsWith("Reading"));
        Pump(100);
        window.UpdateLayout();
        Check($"use in map: a weapon's dialog names the zone line and the file ({dlg.ZoneLine}; {Path.GetFileName(dlg.ZoneFile)})",
            dlg.IsWeapon && dlg.ZoneLine == "weapon,smg_standard_zm" && dlg.ZoneFile == zonePath && dlg.CanAddLine && dlg.Stage == UseStage.Idle);
        Check($"use in map: it says what Dragon carries and what stays silent ('{dlg.CarrySummary}')",
            dlg.CarrySummary.StartsWith("Dragon carries") && dlg.CarrySummary.Contains("assets") && dlg.CarrySummary.Contains("sound"));
        Check("use in map: a mod's zone file is the mode's own (zm_mod.zone before mp_mod.zone)",
            StockUseViewModel.ZoneFileOf(bo3, targets[1]) == Path.Combine(modDir, "zm_mod.zone") && StockUseViewModel.ZoneFileOf(bo3, new CloneTarget("usermap", "nope", "nope")) is null);
        Capture(window, Path.Combine(outDir, "stock-use.png"));

        dlg.AddLineCommand.Execute(null);
        Pump(100);
        window.UpdateLayout();
        Check("use in map: Add to zone file asks first, and writes nothing yet", dlg.Stage == UseStage.ConfirmingWrite && File.ReadAllText(zonePath) == original && Shown(window, "UseConfirm"));
        Capture(window, Path.Combine(outDir, "stock-use-confirm.png"));
        dlg.CancelAddLineCommand.Execute(null);
        Check("use in map: Cancel leaves the file as it was", dlg.Stage == UseStage.Idle && File.ReadAllText(zonePath) == original);

        dlg.AddLineCommand.Execute(null);
        dlg.ConfirmAddLineCommand.Execute(null);
        var after = File.ReadAllText(zonePath);
        Check($"use in map: confirming appends the line in the file's own line endings and leaves the rest alone",
            after == original + "\r\nweapon,smg_standard_zm\r\n" && dlg.Stage == UseStage.Written && said!.Contains("Added weapon,smg_standard_zm"));
        dlg.AddLineCommand.Execute(null);
        dlg.ConfirmAddLineCommand.Execute(null);
        Check("use in map: a line that is already there is not added twice", File.ReadAllText(zonePath) == after && !dlg.CanAddLine);
        Check("use in map: a commented or other-case copy of the line counts as there, a different weapon does not",
            StockUseViewModel.HasLine("weapon,A // x", "weapon,a") && !StockUseViewModel.HasLine("// weapon,a", "weapon,a") && !StockUseViewModel.HasLine("weapon,ab", "weapon,a"));
        var lf = Path.Combine(temp, "lf.zone");
        File.WriteAllText(lf, "a,b\n");
        StockUseViewModel.AppendLine(lf, "weapon,x", out var added);
        Check("use in map: a file with LF endings gets an LF line", added && File.ReadAllText(lf) == "a,b\nweapon,x\n");

        dlg.CreateCopyInsteadCommand.Execute(null);
        Check("use in map: Create local copy instead closes the dialog", closed);
        vm.UseDialog = null;
        vm.Registry[CommandCatalog.CloseTab].Execute();

        // ── the clone dialog: Use directly, off with the reason and on ──
        SetStockStatus(vm, fake, off);
        var view = FindStockRow(vm, "wpn_t7_smg_standard_view");
        vm.OpenAsset(view.Asset!);
        Pump();
        WaitFor(() => vm.ActiveTab?.Record.Stock?.Description is not null);
        vm.CreateLocalCopyCommand.Execute(null);
        Pump();
        var clone = vm.CloneDialog;
        WaitFor(() => clone?.Stage == CloneStage.Ready);
        Pump(100);
        window.UpdateLayout();
        var useButton = ButtonNamed(window, "CloneUseDirectlyButton");
        var cloneNote = window.GetVisualDescendants().OfType<TextBlock>().First(t => t.Name == "CloneUseDirectlyNote");
        Check($"clone: Use directly is there, disabled, and says why ('{cloneNote.Text}')",
            useButton.IsEffectivelyVisible && !useButton.IsEffectivelyEnabled && cloneNote.Text!.StartsWith("Use directly isn't available:") && cloneNote.Text.Contains(off.UseDirectly.Reason));
        Capture(window, Path.Combine(outDir, "stock-clone-use-off.png"));
        clone!.CloseCommand.Execute(null);
        SetStockStatus(vm, fake, ok);
        vm.CreateLocalCopyCommand.Execute(null);
        Pump();
        clone = vm.CloneDialog;
        WaitFor(() => clone?.Stage == CloneStage.Ready);
        Pump(100);
        window.UpdateLayout();
        useButton = ButtonNamed(window, "CloneUseDirectlyButton");
        Check("clone: with the gate passing, Use directly is enabled", useButton.IsEffectivelyEnabled && clone!.UseDirectlyOk);
        Capture(window, Path.Combine(outDir, "stock-clone-use-on.png"));
        clone!.UseDirectlyCommand.Execute(null);
        Pump();
        Check("clone: Use directly closes the copy dialog and opens Use in map for the asset", vm.CloneDialog is null && vm.UseDialog is { AssetName: "wpn_t7_smg_standard_view" });
        vm.UseDialog?.CloseCommand.Execute(null);
        vm.Registry[CommandCatalog.CloseTab].Execute();

        // ── Components ──
        vm.Registry[CommandCatalog.Components].Execute();
        Pump();
        WaitFor(() => vm.Components is { IsChecking: false });
        Pump(100);
        window.UpdateLayout();
        var comps = vm.Components;
        Check("components: the app menu's command opens the view", comps is not null && vm.IsComponentsOpen && vm.IsModalOpen);
        if (comps is not null)
        {
            var names = comps.Rows.Select(r => r.Name).ToList();
            Check($"components: the rows are Apex, Stock Library, Dragon, Blackbird, Radiant integration, Ultrasound ({string.Join(", ", names)})",
                names.SequenceEqual(new[] { "Apex", "Stock Library", "Dragon", "Blackbird", "Radiant integration", "Ultrasound" }));
            Check($"components: the heading says use directly is on ('{comps.Pill}')", comps.PillOn && comps.Pill == "Use directly is on");
            var dragon = comps.Rows.Single(r => r.Name == "Dragon");
            var stockRow = comps.Rows.Single(r => r.Name == "Stock Library");
            Check($"components: Dragon is ok with its version and capability ({dragon.Status}); the library counts its assets ({stockRow.Status})",
                dragon.IsOk && dragon.Status.Contains("stock-carry") && stockRow.IsOk && stockRow.Status.Contains("assets") && comps.Rows.Single(r => r.Name == "Ultrasound").IsOff);
            Check("components: the card and its rows are on screen", Shown(window, "ComponentsCard") && Shown(window, "ComponentRows"));
            Capture(window, Path.Combine(outDir, "stock-components.png"));

            SetStockStatus(vm, fake, off);
            comps.Update();
            Pump(100);
            window.UpdateLayout();
            var dragonOff = comps.Rows.Single(r => r.Name == "Dragon");
            Check($"components: with Blackbird on the stock linker Dragon shows a warning with the fix ('{dragonOff.Detail}'), and the heading is off",
                dragonOff.IsWarn && dragonOff.Detail == off.UseDirectly.Note && !comps.PillOn && comps.Pill == "Use directly is off");
            Capture(window, Path.Combine(outDir, "stock-components-off.png"));
            comps.CloseCommand.Execute(null);
            Check("components: closing returns to the editor", vm.Components is null && !vm.IsModalOpen);
        }
        SetStockStatus(vm, fake, StockStatus.Unknown("This fixture backend has not been told what the install has."));
    }

    private static BrowserNode FindStockRow(MainViewModel vm, string title)
    {
        vm.FilterText = title;
        vm.ApplyFilterNow();
        Pump();
        WaitFor(() => Rows(vm).Any(r => r.IsStock && r.Title == title));
        return Rows(vm).First(r => r.IsStock && r.Title == title);
    }
}
