using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Apex.Editor.Controls;
using Apex.Editor.Models;
using Apex.Editor.Services.Extensions;
using Apex.Editor.ViewModels;
using Apex.Editor.Views;
using GdtEncoding = Apex.Render.Data.Gdt.GdtEncoding;
using K = Avalonia.Input.Key;

namespace Apex.Shots;

/// <summary>
/// The editor pass in the live app on a temp install (copies of three install GDTs, 25 weapons, 18 of them derived) with
/// the fixture manifest, driven by real keys and clicks: the off line, values in parts, the combined column, paste and
/// copy, folding sections, the export, turning weapon-tech on for 25 assets from the Explorer and the table, save status
/// lines, screenshots in both themes and timings.
/// </summary>
public partial class Program
{
    private static void EditorPassUiChecks(string outDir)
    {
        var install = NewScratch("pass-install");
        CopyTree(Path.Combine(InstallRoot, "deffiles"), Path.Combine(install, "deffiles"));
        Directory.CreateDirectory(Path.Combine(install, "source_data"));
        string Copy(string rel)
        {
            File.Copy(Path.Combine(InstallRoot, rel), Path.Combine(install, rel));
            return Path.Combine(install, rel);
        }
        var akPath = Copy(@"source_data\ar_ak47_h1.gdt");
        var ak = LoadGdt(akPath).Assets.First(a => a.Parent is null && a.Type == "bulletweapon").Name;
        // 25 weapons: three families of a weapon and its variants (the install's only GDTs with derived weapons), the AK,
        // and eight of the stock zombies weapons.
        List<AssetRecord> WeaponsOf(string rel)
        {
            var assets = LoadGdt(Copy(rel)).Assets.ToList();
            Apex.Editor.Services.Gdt.GdtLoader.ResolveParents(assets);
            return assets.Where(a => a.Type == "bulletweapon").ToList();
        }
        var families = new[] { @"source_data\pistol_usp_h1.gdt", @"source_data\smg_psd9_h1.gdt", @"source_data\pistol_prokolot_h1.gdt" }
            .Select(WeaponsOf).ToList();
        var stock = WeaponsOf(@"source_data\wpn_t7_zm_stock.gdt").Where(a => a.Parent is null).OrderBy(a => a.Name).Take(8).ToList();
        var selectionNames = families.SelectMany(f => f).Concat(stock).Select(a => a.Name).Append(ak).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var famPath = Path.Combine(install, @"source_data\pistol_prokolot_h1.gdt");
        var famRoot = families[2].First(a => a.Parent is null).Name;
        var famKids = families[2].Where(a => a.Parent is not null).Select(a => a.Name).ToList();
        File.WriteAllBytes(ExtensionSidecar.PathFor(akPath), GdtEncoding.GetBytes("{\r\n" + Block(ak, new[]
        {
            ("wtEnabled", "1"), ("wtEmptyLastShot", "iw"), ("wtKick1", KickRow(1)), ("wtKick2", KickRow(2)), ("wtKick3", KickRow(3)),
            ("wtSpringViewHip", "30,1,1,4,2"),
        }) + "}\r\n"));
        // One variant switched off on its own, one on on its own: turning the family on writes the root and drops the
        // off one's copy; the on one is left alone.
        File.WriteAllBytes(ExtensionSidecar.PathFor(famPath), GdtEncoding.GetBytes("{\r\n"
            + Block(famKids[0], new[] { ("wtEnabled", "0") }) + Block(famKids[1], new[] { ("wtEnabled", "1") }) + "}\r\n"));

        var saved = (Mock: Environment.GetEnvironmentVariable("APEX_FORCE_MOCK"), Root: Environment.GetEnvironmentVariable("APEX_BO3_ROOT"),
            Persist: Environment.GetEnvironmentVariable("APEX_NO_PERSIST"));
        Environment.SetEnvironmentVariable("APEX_FORCE_MOCK", null);
        Environment.SetEnvironmentVariable("APEX_BO3_ROOT", install);
        Environment.SetEnvironmentVariable("APEX_NO_PERSIST", "1");
        var extensions = ExtensionsDir("pass-ui");
        Environment.SetEnvironmentVariable(ExtensionRegistry.DirVariable, extensions);
        MainViewModel? vm = null;
        MainWindow? window = null;
        try
        {
            vm = new MainViewModel(Path.Combine(install, "session"));
            window = ShowJournalWindow(vm);
            window.Width = 1600;
            window.Height = 1000;
            WaitUntil(() => vm.Status.StartsWith("Loaded"), 60_000);
            Pump(100);
            if (vm.IsAlertOpen)
                vm.DismissAlertCommand.Execute(null);
            var shell = (Apex.Editor.Commands.IShellView)window;
            string Clipboard() => shell.GetClipboardTextAsync().GetAwaiter().GetResult().Text ?? "";
            void SetClipboard(string text) =>
                Avalonia.Input.Platform.ClipboardExtensions.SetValueAsync(window.Clipboard!, DataFormat.Text, text).GetAwaiter().GetResult();
            void Shot(string name)
            {
                foreach (var (theme, suffix) in new[] { (ThemeVariant.Dark, "dark"), (ThemeVariant.Light, "light") })
                {
                    Application.Current!.RequestedThemeVariant = theme;
                    Pump(60);
                    Capture(window!, Path.Combine(outDir, $"96-pass-{name}-{suffix}.png"));
                }
                Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
                Pump(60);
            }
            T? Find<T>(Func<T, bool> where) where T : Control =>
                window!.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.IsEffectivelyVisible && where(c));
            object? Focused() => window!.FocusManager?.GetFocusedElement() is StyledElement e ? e.DataContext : null;

            // ── The off line, Turn on with a real click ──
            var pistol = FindRecord(vm, "pistol_usp_h1.gdt", "bulletweapon")!;
            vm.OpenByName(pistol.Name);
            Pump(300);
            var tab = vm.ActiveTab!;
            var offRow = tab.FlatRows.FirstOrDefault() as ExtensionOffRowViewModel;
            Check($"ui off line: a weapon weapon-tech is off for starts with the manifest's line ('{offRow?.Text}')", offRow is not null);
            Shot("off-line");
            var turnOn = Find<Button>(b => b.Content as string == "Turn on");
            if (turnOn is not null)
                Click(window, turnOn);
            Pump(100);
            Check($"ui off line: a real click on Turn on turns it on, the keyboard on the switch ({tab.AllSentinel.All.First(p => p.Key == "wtEnabled").RawValue})",
                turnOn is not null && tab.AllSentinel.All.First(p => p.Key == "wtEnabled").RawValue == "1" && tab.FlatRows[0] is not ExtensionOffRowViewModel
                && Focused() is TogglePropertyViewModel { Key: "wtEnabled" });
            Shot("on-notice");
            Key(window, K.Z, RawInputModifiers.Control);
            Check("ui off line: Ctrl+Z turns it off again", tab.FlatRows[0] is ExtensionOffRowViewModel);

            // ── Values in parts, with the keyboard ──
            vm.OpenByName(ak);
            Pump(300);
            tab = vm.ActiveTab!;
            var spring = (PartsPropertyViewModel)tab.AllSentinel.All.First(p => p.Key == "wtSpringViewHip");
            tab.RevealProperty(spring.Key);
            Pump(100);
            window.UpdateLayout();
            Check($"ui parts: revealing the value puts the keyboard in its first part ({(Focused() as PropertyItemViewModel)?.AccessibleName})",
                ReferenceEquals(Focused(), spring.Parts[0]));
            Key(window, K.Up, RawInputModifiers.Control);
            Check($"ui parts: Ctrl+↑ steps a part, the value's other parts as they were ({spring.RawValue})",
                spring.RawValue == "31,1,1,4,2"
                && spring.Parts[0].IsChanged && !spring.Parts[1].IsChanged);
            Key(window, K.Down);
            var second = Focused();
            Key(window, K.Up);
            var backUp = Focused();
            for (var i = 0; i < 5; i++)
                Key(window, K.Down);
            Check($"ui parts: ↑↓ walk the parts, then on to the next row ({(second as PropertyItemViewModel)?.Label}, {(Focused() as PropertyItemViewModel)?.Key})",
                ReferenceEquals(second, spring.Parts[1]) && ReferenceEquals(backUp, spring.Parts[0]) && Focused() is PropertyItemViewModel { Key: "wtSpringViewAds" or "accel" } next
                && !spring.Parts.Contains(next));
            tab.RevealProperty(spring.Key);
            Pump(100);
            tab.FocusedProperty = spring;
            Shot("parts");
            Check($"ui parts: the Inspector's changes say the part ({string.Join("; ", tab.Changes.Select(c => $"{c.Label}: {c.Old} → {c.Now}"))})",
                tab.Changes.Any(c => c.Item == spring && c.Old == "Stiffness: 30"));
            Key(window, K.Z, RawInputModifiers.Control);
            Check($"ui parts: Ctrl+Z says the part, never the stored text ('{vm.Status}')", vm.Status == "Undid Spring, view, hip: Stiffness" && spring.RawValue == "30,1,1,4,2");

            // A whole value pasted into a later part, as a modder pastes the spring line from a cfg.
            tab.RevealProperty(spring.Key);
            Pump(100);
            Key(window, K.Down);
            Key(window, K.Down);
            Key(window, K.Enter);
            window.KeyTextInput("220,0.06,1,10,10");
            Key(window, K.Enter);
            Check($"ui parts: a whole value typed or pasted into the third part fills all five ({spring.RawValue})",
                spring.RawValue == "220,0.06,1,10,10" && spring.Parts.Select(p => p.RawValue).SequenceEqual(new[] { "220", "0.06", "1", "10", "10" })
                && spring.Parts.All(p => !p.HasProblem));
            Key(window, K.Z, RawInputModifiers.Control);
            Check($"ui parts: and one Ctrl+Z takes it all back ({spring.RawValue})", spring.RawValue == "30,1,1,4,2");

            // ── Recoil from: a reference to any weapon ──
            var source = (RefPropertyViewModel)tab.AllSentinel.All.First(p => p.Key == "wtSource");
            var offered = vm.MatchRefs("weapon", "")!;
            var image = FindRecord(vm, "pistol_usp_h1.gdt", "image")!;
            Check($"ui weapon ref: Recoil from offers every weapon type's assets and only those ({offered.Count}: {string.Join(", ", offered.Select(a => a.Type).Distinct())})",
                offered.Count >= 25 && offered.All(a => a.Type.EndsWith("weapon")) && offered.Any(a => a.Name == pistol.Name)
                && vm.RefExists("weapon", pistol.Name) && !vm.RefExists("weapon", image.Name) && source.RefType == "weapon");
            source.RawValue = image.Name;
            var notWeapon = source.Problem;
            source.RawValue = pistol.Name;
            Check($"ui weapon ref: an asset that isn't a weapon is a problem, a weapon isn't ('{notWeapon}', '{source.Problem}')",
                notWeapon == $"No weapon named ‘{image.Name}’" && source.Problem is null && source.CanGoTo);
            Key(window, K.Z, RawInputModifiers.Control);
            Key(window, K.Z, RawInputModifiers.Control);

            // ── The combined column and its labels ──
            var kicks = (RecordsPropertyViewModel)tab.AllSentinel.All.First(p => p.Key == "wtKick#");
            tab.RevealProperty("wtKick#");
            Pump(100);
            window.UpdateLayout();
            var table = Find<RecordTableEditor>(t => t.DataContext == kicks)!;
            var header = table.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Classes.Contains("rhead") && t.IsEffectivelyVisible).Select(t => t.Text).ToList();
            var applies = table.GetVisualDescendants().OfType<ChoiceBox>().First(c => c.IsEffectivelyVisible && c.DataContext == kicks.Rows[0].Cells[0]);
            Check($"ui combine: the table shows Applies to where ADS and Target were, its rows by label ({string.Join(", ", header)}; '{applies.SelectedItem}')",
                header.SequenceEqual(new[] { "Applies to", "From shot", "Direction", "Deviation", "Strength min", "Strength max", "Pitch scale" })
                && applies.SelectedItem?.ToString() == "ADS view");
            // In a table ↓ walks the column, so the dropdown opens with F4 (or a click); ↓ and Enter pick in it.
            applies.Focus(NavigationMethod.Tab);
            Pump();
            Key(window, K.F4);
            Pump(50);
            Key(window, K.Down);
            Key(window, K.Enter);
            Pump(50);
            Check($"ui combine: ↓ on the closed dropdown picks the next label and writes both columns ({Rec(kicks, 0)})",
                Rec(kicks, 0) == "1,1,1,91,0.5,0.15,1.1,1" && applies.SelectedItem?.ToString() == "ADS gun");
            Key(window, K.Z, RawInputModifiers.Control);
            Check($"ui combine: Ctrl+Z says the row ('{vm.Status}')", vm.Status == "Undid Kick patterns row 1" && Rec(kicks, 0) == KickRow(1));
            Shot("combine");

            // ── Paste and copy ──
            var number = table.GetVisualDescendants().OfType<ScrubNumberBox>().First(b => b.IsEffectivelyVisible && b.DataContext == kicks.Rows[1].Cells[1]);
            number.Focus(NavigationMethod.Tab);
            Pump();
            SetClipboard("0\t0\t5\t95\t0.5\t0.1\t0.2\t1\r\n0,1,6,96,0.5,0.1,0.2,1\r\n1,0,x,97,0.5,0.1,0.2,1\r\n");
            Key(window, K.V, RawInputModifiers.Control);
            Pump(100);
            Check($"ui paste: Ctrl+V pastes the rows from the row it is in, replacing and adding, as one change ('{vm.Status}')",
                kicks.Rows.Count == 4 && Rec(kicks, 1) == "0,0,5,95,0.5,0.1,0.2,1" && Rec(kicks, 3) == "1,0,x,97,0.5,0.1,0.2,1"
                && Rec(kicks, 0) == KickRow(1) && vm.Status.StartsWith("Pasted into Kick patterns: replaced rows 2–3 and added 1. 1 of them has a problem (⚠).")
                && kicks.Rows[3].HasProblem && kicks.Rows.Skip(1).All(r => r.IsSelected));
            Shot("paste");
            Key(window, K.Z, RawInputModifiers.Control);
            Check($"ui paste: one Ctrl+Z takes it all back ('{vm.Status}')", kicks.Rows.Count == 3 && Rec(kicks, 1) == KickRow(2));
            number = table.GetVisualDescendants().OfType<ScrubNumberBox>().First(b => b.IsEffectivelyVisible && b.DataContext == kicks.Rows[0].Cells[1]);
            number.Focus(NavigationMethod.Tab);
            Pump();
            Key(window, K.Down, RawInputModifiers.Shift);
            Pump(50);
            Key(window, K.C, RawInputModifiers.Control);
            Pump(50);
            Check($"ui copy: Shift+↓ marks rows and Ctrl+C copies them as stored ('{Clipboard().Replace("\r\n", "⏎")}', '{vm.Status}')",
                Clipboard() == KickRow(1) + Environment.NewLine + KickRow(2) && kicks.Rows[0].IsSelected && kicks.Rows[1].IsSelected
                && vm.Status == "Copied Kick patterns rows 1–2. Ctrl+V in a table pastes them.");
            Key(window, K.Escape);
            Check("ui copy: Esc unmarks them", kicks.Rows.All(r => !r.IsSelected));
            var add = Find<Button>(b => b.Name == "AddButton" && b.DataContext == kicks)!;
            add.Focus(NavigationMethod.Tab);
            Pump();
            SetClipboard($"\"wtKick1\" \"{KickRow(7)}\"\r\nwtKick2 = {KickRow(8)}");
            Key(window, K.V, RawInputModifiers.Control);
            Pump(100);
            Check($"ui paste: on + Add row, Ctrl+V adds the rows at the end, a .gdtx's or a cfg's lines read as rows ('{vm.Status}')",
                kicks.Rows.Count == 5 && Rec(kicks, 3) == KickRow(7) && Rec(kicks, 4) == KickRow(8));
            Key(window, K.Z, RawInputModifiers.Control);

            // ── A section folded while empty, opened by a real click and by Space ──
            var ik = tab.RailItems.First(c => c.Name == "Hand IK");
            tab.SelectedRail = null;
            tab.SearchText = "";
            Pump(50);
            Check("ui folding: Hand IK holds nothing here, so it starts folded", ik.IsCollapsed);
            // The section after it, revealed, brings its header into view.
            tab.RevealProperty("wtInspect");
            Pump(100);
            window.UpdateLayout();
            Pump(50);
            var ikToggle = Find<ToggleButton>(t => t.Classes.Contains("sectiontoggle") && t.DataContext == ik);
            Shot("folded");
            if (ikToggle is not null)
                Click(window, ikToggle);
            Pump(50);
            Check($"ui folding: a click on the header opens it ({ik.IsExpanded})", ikToggle is not null && !ik.IsCollapsed
                && tab.FlatRows.Contains(tab.AllSentinel.All.First(p => p.Key == "wtIk")));
            ikToggle?.Focus(NavigationMethod.Tab);
            Key(window, K.Space);
            Check("ui folding: Space on the focused header folds it again, the keyboard staying on it",
                ik.IsCollapsed && ikToggle?.IsFocused == true);

            // ── Export: the header's button and the palette ──
            tab.SelectedRail = tab.RailItems[0];
            Pump(100);
            window.UpdateLayout();
            var export = Find<Button>(b => b.Content is TextBlock { Text: "Copy as weapon_tech.cfg" });
            if (export is not null)
                Click(window, export);
            Pump(100);
            var expected = tab.ExportText("weapon-tech")!;
            Check($"ui export: the header's button copies the weapon's section ('{Clipboard().Split("\r\n")[0]}…', '{vm.Status}')",
                export is not null && Clipboard() == expected && expected.StartsWith($"[weapon:{ak}]\r\nwtFireTimeMs = ", StringComparison.Ordinal) == false
                && expected.StartsWith($"[weapon:{ak}]\r\n", StringComparison.Ordinal) && expected.Contains("wtKick3 = ")
                && vm.Status == $"Copied [weapon:{ak}]: 5 values, its own and the ones it inherits.");
            vm.OpenCommandPaletteCommand.Execute(null);
            vm.PaletteText = ">copy as";
            var copyItem = vm.PaletteResults.FirstOrDefault(r => r.Name == "Copy as weapon_tech.cfg");
            Check($"ui export: the palette offers it under the manifest's name ({copyItem?.Detail})", copyItem is not null);
            vm.IsPaletteOpen = false;
            Shot("export");

            // ── Save status names the .gdtx ──
            tab.AllSentinel.All.First(p => p.Key == "wtFireTimeMs").RawValue = "77";
            window.KeyPress(K.S, RawInputModifiers.Control, PhysicalKey.S, "s");
            WaitUntil(() => !vm.IsSaveRunning, 10_000);
            Pump();
            var gdtxName = Path.GetFileName(ExtensionSidecar.PathFor(akPath));
            Check($"ui save: a save of extension data alone names the .gdtx ('{vm.Status}')", vm.Status == $"Saved {gdtxName}");
            tab.AllSentinel.All.First(p => p.Key == "wtFireTimeMs").RawValue = "78";
            tab.AllSentinel.All.First(p => p.Key == "displayName").RawValue = "AK editor pass";
            window.KeyPress(K.S, RawInputModifiers.Control, PhysicalKey.S, "s");
            WaitUntil(() => !vm.IsSaveRunning, 10_000);
            Pump();
            Check($"ui save: a save of both names both ('{vm.Status}')", vm.Status == $"Saved ar_ak47_h1.gdt and {gdtxName}");

            // ── Turning it on for 25 assets: the Explorer's selection, then the table's ──
            var weapons = new[] { "ar_ak47_h1.gdt", "pistol_usp_h1.gdt", "smg_psd9_h1.gdt", "pistol_prokolot_h1.gdt", "wpn_t7_zm_stock.gdt" }
                .SelectMany(g => vm.GdtOf(FindRecord(vm, g, null)!)!.Assets.Where(a => selectionNames.Contains(a.Name))).ToList();
            var byName = weapons.ToDictionary(w => w.Name, StringComparer.OrdinalIgnoreCase);
            bool On(string name) => EffectiveOn(vm, byName, name);
            // The Explorer's own selection: the weapons the filter lists, selected in its list as Ctrl+clicks would.
            var tree = window.GetVisualDescendants().OfType<AssetBrowserView>().First().FindControl<ListBox>("Tree")!;
            vm.FilterText = "type:bulletweapon";
            vm.ApplyFilterNow();
            WaitUntil(() => vm.FlatRows.Count(n => n.Asset is { } a && byName.ContainsKey(a.Name)) >= 25, 5_000);
            Pump(50);
            var rows = vm.FlatRows.Where(n => n.Asset is { } a && byName.ContainsKey(a.Name)).ToList();
            tree.SelectedItems!.Clear();
            foreach (var r in rows)
                tree.SelectedItems.Add(r);
            Pump(50);
            vm.OpenCommandPaletteCommand.Execute(null);
            vm.PaletteText = ">turn on weapon";
            var bulk = vm.PaletteResults.FirstOrDefault(r => r.Name.StartsWith("Turn on Weapon tech for", StringComparison.Ordinal));
            var offBefore = weapons.Count(w => !On(w.Name));
            Check($"ui bulk: the palette offers turning it on for the {rows.Count} weapons selected, counting those it changes ('{bulk?.Name}', {bulk?.Detail})",
                rows.Count == 25 && bulk?.Name == $"Turn on Weapon tech for {offBefore} assets" && offBefore == 23);
            var sw = Stopwatch.StartNew();
            if (bulk is not null)
                vm.ConfirmPaletteItem(bulk);
            var firstMs = sw.Elapsed.TotalMilliseconds;
            Pump(50);
            var famx = vm.GdtOf(FindRecord(vm, "pistol_prokolot_h1.gdt", null)!)!.Extensions!;
            Check($"ui bulk: all 25 on; only the roots written, the variant switched off on its own made to follow its parent ('{vm.Status}', {firstMs:0.0} ms)",
                weapons.All(w => On(w.Name)) && famx.Get(famRoot, "weapon-tech", "wtEnabled") == "1" && famx.Get(famKids[0], "weapon-tech", "wtEnabled") is null
                && famx.Get(famKids[1], "weapon-tech", "wtEnabled") == "1" && famx.Get(famKids[2], "weapon-tech", "wtEnabled") is null
                && vm.Status == "Turned on Weapon tech for 23 assets (11 by following their parents). Ctrl+Z undoes it.");
            // Warm (the first run paid for its code's first use): each run on, then undone.
            var manifest = ExtensionRegistry.Manifests.Single();
            var selected = rows.Select(r => r.Asset!).ToList();
            var bulkRuns = new List<double>();
            for (var i = 0; i < 9; i++)
            {
                Key(window, K.Z, RawInputModifiers.Control);
                var run = Stopwatch.StartNew();
                vm.SetExtensionOn(selected, manifest, on: true);
                bulkRuns.Add(run.Elapsed.TotalMilliseconds);
                Pump();
            }
            var bulkMs = bulkRuns.Order().ElementAt(bulkRuns.Count / 2);
            Console.WriteLine($"info  editor pass: turning weapon-tech on for 25 selected weapons (12 written, the tree, tabs and counts after) median {bulkMs:0.0} ms, first {firstMs:0.0} ms, on the UI thread");
            Gate($"perf (editor pass): turning it on for 25 assets is one frame (median {bulkMs:0.0} ms; the first, with its code's first use, {firstMs:0.0} ms)", bulkMs <= PerfBudgets.Frame);
            Key(window, K.Z, RawInputModifiers.Control);
            Check($"ui bulk: one Ctrl+Z turns them all back ('{vm.Status}')",
                weapons.Count(w => !On(w.Name)) == offBefore && famx.Get(famKids[0], "weapon-tech", "wtEnabled") == "0" && famx.Get(famRoot, "weapon-tech", "wtEnabled") is null);
            vm.OpenTableFor(weapons);
            Pump(200);
            foreach (var r in vm.Table!.Rows)
                r.IsSelected = true;
            vm.OpenCommandPaletteCommand.Execute(null);
            vm.PaletteText = ">turn on weapon";
            bulk = vm.PaletteResults.FirstOrDefault(r => r.Name.StartsWith("Turn on Weapon tech for", StringComparison.Ordinal));
            Check($"ui bulk: with the table open its checked rows are the selection ('{bulk?.Name}', {bulk?.Detail})",
                bulk is { Name: "Turn on Weapon tech for 23 assets", Detail: "the table's selected rows" });
            if (bulk is not null)
                vm.ConfirmPaletteItem(bulk);
            Pump(50);
            var allOn = weapons.All(w => On(w.Name));
            Key(window, K.Z, RawInputModifiers.Control);
            Check($"ui bulk: from the table it is the table's step, which its Ctrl+Z takes back ('{vm.Status}')",
                allOn && weapons.Count(w => !On(w.Name)) == offBefore && vm.Status.StartsWith("Undid: Turned on Weapon tech for 23 assets", StringComparison.Ordinal));
            vm.Table = null;
            Pump(50);

            // ── Timings: opening the weapon with the fixture (parts, combine, folding) ──
            SilenceHeadlessRenderTimer();
            var opens = new List<double>();
            for (var i = 0; i < 20; i++)
            {
                while (vm.OpenTabs.Count > 0)
                    vm.CloseActiveTabCommand.Execute(null);
                Pump();
                var one = Time(window, "open", PerfBudgets.OpenAsset, 0, 1, _ => vm.OpenByName(ak));
                if (i >= 5)
                    opens.Add(one.Median);
            }
            var open = Summarize("Open a weapon with the weapon-tech fixture (parts, combine, folding)", PerfBudgets.OpenAsset, opens);
            Console.WriteLine($"info  {open.Name}: median {open.Median:0.0} ms, p95 {open.P95:0.0} ms (budget {open.Budget:0} ms)");
            tab = vm.ActiveTab!;
            spring = (PartsPropertyViewModel)tab.AllSentinel.All.First(p => p.Key == "wtSpringViewHip");
            tab.RevealProperty(spring.Key);
            Pump(100);
            var up = true;
            var step = Time(window, "Part: Ctrl+arrow step", PerfBudgets.Frame, 5, 30,
                _ => KeyStroke(window, up ? K.Up : K.Down, RawInputModifiers.Control), after: i => up = i % 2 == 1);
            Console.WriteLine($"info  {step.Name}: median {step.Median:0.0} ms, p95 {step.P95:0.0} ms (budget {step.Budget:0} ms)");
            Gate($"perf (editor pass): a part's Ctrl+↑ step median {step.Median:0.0} ms, p95 {step.P95:0.0} ms (budget {step.Budget:0} ms)", step.Pass);
            kicks = (RecordsPropertyViewModel)tab.AllSentinel.All.First(p => p.Key == "wtKick#");
            var many = Enumerable.Range(1, 24).Select(KickRow).ToList();
            tab.RevealProperty("wtKick#");
            Pump(100);
            var pastes = new List<double>();
            var states = new List<double>();
            for (var i = 0; i < 12; i++)
            {
                var s2 = Stopwatch.StartNew();
                kicks.Paste(0, many);
                states.Add(s2.Elapsed.TotalMilliseconds);
                window.UpdateLayout();
                Pump();
                pastes.Add(s2.Elapsed.TotalMilliseconds);
                vm.UndoActiveCommand.Execute(null);
                window.UpdateLayout();
                Pump();
            }
            var paste = Summarize("Paste 24 kick rows into a 3-row table (to its rows laid out)", PerfBudgets.Frame, pastes.Skip(2).ToList());
            var pasteState = Summarize("the same, to the rows' values (before layout)", PerfBudgets.Frame, states.Skip(2).ToList());
            Console.WriteLine($"info  {paste.Name}: median {paste.Median:0.0} ms, p95 {paste.P95:0.0} ms; {pasteState.Name}: median {pasteState.Median:0.0} ms");
        }
        catch (Exception ex)
        {
            Check($"editor pass ui: {ex}", false);
        }
        finally
        {
            window?.Close();
            vm?.Dispose();
            Environment.SetEnvironmentVariable("APEX_FORCE_MOCK", saved.Mock);
            Environment.SetEnvironmentVariable("APEX_BO3_ROOT", saved.Root);
            Environment.SetEnvironmentVariable("APEX_NO_PERSIST", saved.Persist);
            ExtensionRegistry.Clear();
            SchemaRegistry.ResetToMock();
        }
    }

    private static string Rec(RecordsPropertyViewModel table, int row) => RecordCodec.Split(table.RawValue)[row];

    /// <summary>weapon-tech's switch on an asset as a build tool reads it: its own value, else its nearest ancestor's, else off.</summary>
    private static bool EffectiveOn(MainViewModel vm, Dictionary<string, AssetRecord> assets, string name)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var asset = assets.GetValueOrDefault(name); asset is not null && seen.Add(asset.Name);
             asset = asset.Parent is { } p ? assets.GetValueOrDefault(p) : null)
            if (vm.GdtOf(asset)?.Extensions?.Get(asset.Name, "weapon-tech", "wtEnabled") is { } v)
                return v == "1";
        return false;
    }
}
