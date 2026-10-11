using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Avalonia.Headless;
using Avalonia.Input;
using Apex.Editor.Models;
using Apex.Editor.Services;
using Apex.Editor.Services.Gdt;
using Apex.Editor.Services.Save;
using Apex.Editor.ViewModels;
using Apex.Editor.Views;

namespace Apex.Shots;

/// <summary>
/// Change weapon class: the plan (what carries over, what needs a new value, what the new class has no field for), the
/// save's header rewrite, and the live app on a temp install (a real bulletweapon with two derived assets) through
/// undo, redo, a restart and a save read back. Nothing here touches the BO3 install.
/// </summary>
public partial class Program
{
    private static void RunWeaponClassChecks(string outDir)
    {
        WeaponClassPlanChecks();
        WeaponClassSpliceChecks();
        WeaponClassLiveChecks(outDir);
    }

    private static void WeaponClassPlanChecks()
    {
        SchemaRegistry.ResetToMock();
        var from = SchemaRegistry.Get("weapon")!;
        var to = SchemaRegistry.Get("projectileweapon")!;
        Check($"class: the mock's weapon can change to projectileweapon ({string.Join(", ", WeaponClassRemap.Targets("weapon"))})",
            WeaponClassRemap.Targets("weapon").SequenceEqual(new[] { "projectileweapon" }));

        var root = new AssetRecord { Name = "wpn_test", Type = "weapon", GdtName = "t.gdt" };
        foreach (var (k, v) in new Dictionary<string, string>
                 {
                     ["displayName"] = "Test", ["weaponClass"] = "smg", ["impactType"] = "bullet_small",
                     ["penetrateType"] = "medium", ["fireType"] = "Full Auto", ["damage"] = "40", ["someStrayKey"] = "x",
                 })
            root.Properties[k] = v;
        var kid = new AssetRecord { Name = "wpn_test_up", Type = "weapon", GdtName = "t.gdt", Parent = "wpn_test" };
        kid.Properties["fireType"] = "4-Round Burst";
        kid.Properties["penetrateType"] = "large";
        kid.Properties["impactType"] = "bullet_small";

        var plan = WeaponClassRemap.Plan(new[] { root, kid }, from, to);
        var choices = plan.Choices.Select(c => $"{c.Def.Key}={c.OldValue}×{c.Assets}").ToList();
        Check($"class: the plan asks for a value where the new class lacks the choice ({string.Join(", ", choices)})",
            choices.SequenceEqual(new[] { "fireType=4-Round Burst×1", "impactType=bullet_small×2", "weaponClass=smg×1" }));
        Check($"class: … lists the keys it has no field for, with how many assets hold them ({string.Join(", ", plan.Orphans.Select(o => $"{o.Key}×{o.Assets}"))})",
            plan.Orphans.Select(o => (o.Key, o.Assets)).SequenceEqual(new[] { ("penetrateType", 2), ("someStrayKey", 1) }));
        Check($"class: … and carries the rest ({plan.Carried} carried, {plan.NewFields} new fields)",
            plan.Carried == 3 && plan.NewFields == 3);

        var impact = plan.Choices.First(c => c.Def.Key == "impactType");
        var values = WeaponClassRemap.Apply(plan, to, new Dictionary<RemapChoice, string> { [impact] = "bolt" }, keepOrphans: false);
        Check($"class: applying takes the picks, the new class's default elsewhere, and drops what it has no field for ({Show(values.GetValueOrDefault(root))})",
            values[root]["impactType"] == "bolt" && values[root]["weaponClass"] == "rocketlauncher" && !values[root].ContainsKey("penetrateType")
            && !values[root].ContainsKey("someStrayKey") && values[root]["damage"] == "40" && values[kid]["fireType"] == "Single Shot"
            && values[kid]["impactType"] == "bolt" && !values[kid].ContainsKey("penetrateType"));
        var kept = WeaponClassRemap.Apply(plan, to, null, keepOrphans: true);
        Check("class: … or keeps those keys when asked",
            kept[root]["penetrateType"] == "medium" && kept[kid]["penetrateType"] == "large" && kept[root]["impactType"] == "rocket_explode");
        var clean = new AssetRecord { Name = "wpn_clean", Type = "weapon", GdtName = "t.gdt" };
        clean.Properties["damage"] = "50";
        Check("class: an asset whose values all carry over isn't in the result (nothing of it changes)",
            WeaponClassRemap.Apply(WeaponClassRemap.Plan(new[] { clean }, from, to), to, null, false).Count == 0);

        static string Show(Dictionary<string, string>? d) => d is null ? "none" : string.Join(" ", d.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}"));
    }

    private static void WeaponClassSpliceChecks()
    {
        const string gdt = "{\r\n\t\"wpn_a\" ( \"bulletweapon.gdf\" )\r\n\t{\r\n\t\t\"damage\" \"40\"\r\n\t\t\"penetrateType\" \"medium\"\r\n\t}\r\n"
                           + "\t\"wpn_a_up\" [ \"wpn_a\" ]\r\n\t{\r\n\t\t\"damage\" \"80\"\r\n\t}\r\n}\r\n";
        var bytes = Encoding.ASCII.GetBytes(gdt);
        var layout = GdtLayout.Scan(bytes);
        var a = layout.Assets.First(x => x.Name == "wpn_a");
        var root = new AssetRecord
        {
            Name = "wpn_a", Type = "projectileweapon", GdtName = "t.gdt",
            Disk = new DiskRef("wpn_a", null, a.BodyStart, "bulletweapon"),
        };
        Check("class: a root whose type isn't its GDT header's is a change to save, values or not",
            GdtSavePlanner.TypeChanged(root) && GdtSavePlanner.EditFor(root) is { Type: "projectileweapon" });
        var edit = new AssetEdit
        {
            Disk = root.Disk, Name = "wpn_a", Type = "projectileweapon",
            Set = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["penetrateType"] = null },
        };
        var result = GdtSplicer.Splice(bytes, new[] { edit });
        var text = Encoding.ASCII.GetString(result.Bytes ?? Array.Empty<byte>());
        var expected = gdt.Replace("bulletweapon.gdf", "projectileweapon.gdf").Replace("\t\t\"penetrateType\" \"medium\"\r\n", "");
        Check($"class: the save rewrites only the root's ( \"type.gdf\" ) and its values; the derived asset's header and the rest stay ({result.Problems.Count} problems)",
            result.Problems.Count == 0 && text == expected);
    }

    private static void WeaponClassLiveChecks(string outDir)
    {
        var install = NewScratch("class-install");
        CopyTree(Path.Combine(InstallRoot, "deffiles"), Path.Combine(install, "deffiles"));
        var rel = @"source_data\ar_cq300_h1.gdt";
        var path = Path.Combine(install, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.Copy(Path.Combine(InstallRoot, rel), path);
        var sessionDir = Path.Combine(install, "session");
        var original = File.ReadAllBytes(path);

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
            var db = DatabaseOf(vm);
            AssetRecord Find(string name) => db.Assets.Single(x => x.Name == name);
            var root = Find("ar_cq300_h1_zm");
            var kid = Find("ar_cq300_h1_btw_zm");
            Check($"class (live): the temp install loaded, with a bulletweapon and its derived assets ({root.Type}, {kid.Type})",
                !vm.IsMockData && root.Type == "bulletweapon" && kid.Type == "bulletweapon");

            // Opened on a derived asset: the dialog is about its root, and the whole family.
            vm.OpenAsset(kid);
            Pump();
            window.KeyPress(Avalonia.Input.Key.P, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.P, "P");
            Pump();
            window.KeyTextInput("weapon class");
            Pump();
            Key(window, Avalonia.Input.Key.Enter);
            Pump();
            var dialog = vm.ClassDialog;
            Check($"class (live): the palette command opens the dialog on the root, with its family ({dialog?.Root.Name}, {dialog?.Family.Count} assets, to {dialog?.Target})",
                dialog is { Target: "projectileweapon", Family.Count: 3 } && dialog.Root == root && vm.IsModalOpen);
            Check($"class (live): … offering every other weapon type and not the one it is ({string.Join(" ", dialog?.Targets ?? Array.Empty<string>())})",
                dialog is not null && dialog.Targets.Contains("grenadeweapon") && !dialog.Targets.Contains("bulletweapon")
                && dialog.Targets.All(Apex.Editor.Services.Extensions.ExtensionManifest.IsWeapon));
            Check($"class (live): … and saying what moves ({dialog?.CarriedText} · {dialog?.ChoicesText} · {dialog?.OrphansText} · {dialog?.NewFieldsText})",
                dialog is { Plan.Carried: > 0, HasChoices: true, HasOrphans: true });
            Capture(window, Path.Combine(outDir, "96-weapon-class-dialog.png"));
            var weaponType = dialog!.Choices.FirstOrDefault(c => c.Key.Equals("weaponType", StringComparison.OrdinalIgnoreCase));
            Check($"class (live): bullet isn't a projectileweapon's Type, so it needs a value ({weaponType?.OldValue} → {weaponType?.Selected})",
                weaponType is { OldValue: "bullet" } && weaponType.Options.Contains(weaponType.Selected));
            var orphan = dialog.Orphans.Select(o => o.Key).FirstOrDefault();

            var rootBefore = new Dictionary<string, string>(root.Properties, StringComparer.OrdinalIgnoreCase);
            var kidBefore = new Dictionary<string, string>(kid.Properties, StringComparer.OrdinalIgnoreCase);
            Key(window, Avalonia.Input.Key.Escape);
            Check("class (live): Esc closes it and changes nothing", vm.ClassDialog is null && root.Type == "bulletweapon");

            vm.OpenChangeWeaponClass(kid);
            var bomb = vm.ClassDialog!.Choices.First(c => c.Key.Equals("weaponType", StringComparison.OrdinalIgnoreCase));
            bomb.Selected = "bomb";
            vm.ClassDialog!.AcceptCommand.Execute(null);
            Pump();
            Check($"class (live): Change takes the root and its derived assets to the new class ('{vm.Status}')",
                vm.ClassDialog is null && root.Type == "projectileweapon" && kid.Type == "projectileweapon"
                && Find("ar_cq300_h1_ttf_zm").Type == "projectileweapon" && vm.Status.StartsWith("Changed ar_cq300_h1_zm and 2 derived"));
            Check($"class (live): … the values moved as planned (weaponType {root.Properties.GetValueOrDefault("weaponType")}, the pick; {orphan} gone)",
                root.Properties.GetValueOrDefault("weaponType") == "bomb" && (orphan is null || !root.Properties.ContainsKey(orphan)));
            Check($"class (live): … the open tab is rebuilt on the new deffile ({vm.ActiveTab?.Record.Type})",
                vm.ActiveTab is { Record: var r } && r == kid && r.Type == "projectileweapon");
            Check($"class (live): … and it counts as a change ({vm.SessionEditCount})", vm.SessionEditCount > 0);
            Capture(window, Path.Combine(outDir, "97-weapon-class-changed.png"));

            Key(window, Avalonia.Input.Key.Z, RawInputModifiers.Control);
            Check($"class (live): Ctrl+Z puts the class and every value back ('{vm.Status}')",
                root.Type == "bulletweapon" && kid.Type == "bulletweapon"
                && GdtSplicer.SameValues(root.Properties, rootBefore) && GdtSplicer.SameValues(kid.Properties, kidBefore));
            Key(window, Avalonia.Input.Key.Y, RawInputModifiers.Control);
            Check($"class (live): Ctrl+Y changes it again, with the same pick ({root.Properties.GetValueOrDefault("weaponType")})",
                root.Type == "projectileweapon" && kid.Type == "projectileweapon" && root.Properties.GetValueOrDefault("weaponType") == "bomb");
            var after = new Dictionary<string, string>(root.Properties, StringComparer.OrdinalIgnoreCase);

            // Hot exit: the change comes back after a restart, values and all.
            vm.FlushSessionToDisk(closing: true);
            window.Close();
            vm.Dispose();
            vm = new MainViewModel(sessionDir);
            window = ShowJournalWindow(vm);
            WaitUntil(() => vm.Status.StartsWith("Restored"), 60_000);
            db = DatabaseOf(vm);
            root = Find("ar_cq300_h1_zm");
            kid = Find("ar_cq300_h1_btw_zm");
            var diff = after.Keys.Union(root.Properties.Keys, StringComparer.OrdinalIgnoreCase)
                .Where(k => after.GetValueOrDefault(k) != root.Properties.GetValueOrDefault(k)).ToList();
            Console.WriteLine($"info  class: restored differs on {diff.Count}: " + string.Join(", ", diff.Take(8).Select(k => $"{k}: {after.GetValueOrDefault(k) ?? "(none)"} vs {root.Properties.GetValueOrDefault(k) ?? "(none)"}")));
            Check($"class (live): after a restart the family is still a projectileweapon, with the same values ('{vm.Status}')",
                root.Type == "projectileweapon" && kid.Type == "projectileweapon" && GdtSplicer.SameValues(root.Properties, after));

            window.KeyPress(Avalonia.Input.Key.S, RawInputModifiers.Control, PhysicalKey.S, "s");
            WaitUntil(() => !vm.IsSaveRunning, 10_000);
            Pump();
            var text = File.ReadAllText(path, Encoding.ASCII);
            Check($"class (live): the save writes the new class into the root's header and leaves the derived ones ('{vm.Status}')",
                text.Contains("\"ar_cq300_h1_zm\" ( \"projectileweapon.gdf\" )") && text.Contains("\"ar_cq300_h1_btw_zm\" [ \"ar_cq300_h1_zm\" ]")
                && !text.Contains("bulletweapon.gdf"));
            var reread = LoadGdt(path).Assets.First(x => x.Name == "ar_cq300_h1_zm");
            Check($"class (live): … and reads back as a projectileweapon with its new values ({reread.Type})",
                reread.Type == "projectileweapon" && GdtSplicer.SameValues(reread.Properties, after));
            Check($"class (live): … and the session has nothing left to save ({vm.SessionEditCount})", vm.SessionEditCount == 0);
            var now = File.ReadAllBytes(path);
            var before = GdtLayout.Scan(original).Assets;
            var others = GdtLayout.Scan(now).Assets.Where(x => x.Name != "ar_cq300_h1_zm").ToList();
            var untouched = others.Count == before.Count - 1 && others.All(x =>
                before.First(b => b.Name == x.Name) is var b && original.AsSpan(b.BlockStart, b.BlockEnd - b.BlockStart)
                    .SequenceEqual(now.AsSpan(x.BlockStart, x.BlockEnd - x.BlockStart)));
            Check($"class (live): every other asset in the file is byte for byte as it was ({others.Count} assets)", untouched);
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
}
