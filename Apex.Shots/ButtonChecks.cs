using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Apex.Editor.Models;
using Apex.Editor.ViewModels;
using Apex.Editor.Views;

namespace Apex.Shots;

/// <summary>
/// Deffile buttons (APE's AddEntry_ButtonGroup) in the live app against a temp install: real clicks on the buttons the
/// form draws, the values their callbacks write (checked against what the deffile's own algorithm gives), one undo step
/// per click, and a save of the result into the temp GDT, byte for byte. Nothing here touches the BO3 install.
/// </summary>
public partial class Program
{
    private const string ButtonTestDeffile = """
        void AskReset( asset Asset, const string& param )
        {
        	if ( MessageBox( "Reset the speed to " + param + "?", "YESNO" ) == "YES" )
        		Asset.GetEntryVariable( "speed" ).SetInt( param.ToInt() );
        }

        void SayValue( asset Asset, const string& param )
        {
        	MessageBox( "Speed is " + Asset.GetEntryValue( "speed" ) + ".", "OK" );
        }

        void ClearNotes( asset Asset, const string& param )
        {
        	Asset.GetEntryVariable( "notes" ).ClearSpecified();
        }

        void ResetSpeed( asset Asset, const string& param )
        {
        	Asset.GetEntryVariable( "speed" ).ClearSpecified().SetInt( param.ToInt() );
        }

        void Spin( asset Asset, const string& param )
        {
        	int n = 0;
        	for ( int i = 0; i < 1900000; i++ )
        		n = n + 1;
        	Asset.GetEntryVariable( "speed" ).SetInt( 50 );
        }

        void Nag( asset Asset, const string& param )
        {
        	Asset.GetEntryVariable( "speed" ).SetInt( 60 );
        	for ( int i = 0; i < 100; i++ )
        		MessageBox( "Nag " + i + ".", "OK" );
        }

        int Fan( asset Asset, int depth )
        {
        	Asset.GetEntryValue( "speed" );
        	if ( depth == 0 )
        		return 1;
        	return Fan( Asset, depth - 1 ) + Fan( Asset, depth - 1 );
        }

        void Recurse( asset Asset, const string& param )
        {
        	Asset.GetEntryVariable( "speed" ).SetInt( Fan( Asset, 40 ) );
        }

        void Huge( asset Asset, const string& param )
        {
        	string s = "x";
        	for ( int i = 0; i < 25; i++ )
        		s = s + s;
        	Asset.GetEntryVariable( "notes" ).SetValue( s );
        }

        void GenerateUI( asset Asset )
        {
        	Asset.BeginCategory( "General" );
        	Asset.AddEntry_Int( "speed", 1, 0, 100 ).SetTitle( "Speed" );
        	Asset.AddEntry_ButtonGroup( "resetButtons" ).SetTitle( "Reset:" ).AddButton( "To 5", "", "void AskReset( asset Asset, const string& param )", "5" );
        	Asset.AddEntry_ButtonGroup( "resetButtons" ).AddButton( "Say", "", "void SayValue( asset Asset, const string& param )", "" );
        	Asset.AddEntry_String( "notes", "" ).SetTitle( "Notes" );
        	Asset.AddEntry_ButtonGroup( "moreButtons" ).SetTitle( "More" )
        		.AddButton( "Clear notes", "", "void ClearNotes( asset Asset, const string& param )", "" )
        		.AddButton( "Speed to default", "", "void ResetSpeed( asset Asset, const string& param )", "1" )
        		.AddButton( "Speed to 9", "", "void ResetSpeed( asset Asset, const string& param )", "9" )
        		.AddButton( "Spin", "", "void Spin( asset Asset, const string& param )", "" )
        		.AddButton( "Nag", "", "void Nag( asset Asset, const string& param )", "" )
        		.AddButton( "Huge", "", "void Huge( asset Asset, const string& param )", "" )
        		.AddButton( "Recurse", "", "void Recurse( asset Asset, const string& param )", "" );
        }
        """;

    private const string ButtonTestGdt = "{\r\n\t\"apextest_one\" ( \"apextest.gdf\" )\r\n\t{\r\n\t\t\"speed\" \"1\"\r\n\t\t\"notes\" \"hello\"\r\n\t}\r\n"
        + "\t\"apextest_child\" [ \"apextest_one\" ]\r\n\t{\r\n\t\t\"notes\" \"child\"\r\n\t}\r\n}\r\n";

    /// <summary>
    /// Types whose deffiles have buttons but which have no asset in the install: the sweep makes one bare asset of each
    /// (its values all the deffile's defaults), the way New asset would.
    /// </summary>
    private static readonly string[] SweepBareTypes =
    {
        "ainames", "bonuszmdata", "customizationcolor", "destructiblecharacterdef", "destructibledef", "fxcharacterdef",
        "lensflare", "maptable", "maptableloadingimages", "medaltable", "objectivelist", "accoladelist", "collectiblelist",
        "gallery_imagelist", "medalcase", "trainingsimratinglist", "tagfx",
    };

    /// <summary>Real assets from the install for the types that have them: (GDT, asset).</summary>
    private static readonly (string Gdt, string Type)[] SweepRealAssets =
    {
        (@"source_data\dragon_shit.gdt", "scriptbundle"),
        (@"source_data\genesis_zombie.gdt", "aitype"),
        (@"source_data\mp_specialists_t7.gdt", "playerbodytype"),
        (@"source_data\wpn_t7_camo_dlc1_base76.gdt", "weaponcamo"),
        (@"source_data\bo3_tiger_tank.gdt", "vehicleriders"),
        (@"source_data\c_t8_wz_characters.gdt", "charactercustomizationtable"),
        (@"source_data\mwr_lsr2_model_files.gdt", "xmodel"),
        (@"source_data\zm_ai_parasite.gdt", "xanim"),
    };

    /// <summary>
    /// Every button of every type, on a real asset where the install has one and a bare one otherwise: the "Add" buttons
    /// first (so the lists have items for the rest to act on), then each visible button from that state, undone after.
    /// Prints what each did; a script error or a click that never lands fails.
    /// </summary>
    private static void ButtonSweep(MainViewModel vm)
    {
        var subjects = new List<(string Type, string Asset)>();
        foreach (var (gdt, type) in SweepRealAssets)
            if (FindRecord(vm, Path.GetFileName(gdt), type) is { } record)
                subjects.Add((type, record.Name));
        subjects.AddRange(SweepBareTypes.Select(t => (t, "apex_sweep_" + t)));
        subjects.Add(("scriptbundle", "apex_sweep_fxanim"));

        var sw = Stopwatch.StartNew();
        var clicks = 0;
        var failures = new List<string>();
        foreach (var (type, asset) in subjects)
        {
            vm.OpenByName(asset);
            Pump(50);
            var tab = vm.ActiveTab;
            if (tab?.Name != asset)
            {
                failures.Add($"{asset} didn't open");
                continue;
            }
            string Run(DeffileButtonViewModel b)
            {
                vm.DismissAlertCommand.Execute(null);
                var before = tab.Record.History.NextUndo;
                var task = tab.RunButtonAsync(b.Group, b.Button);
                for (var i = 0; i < 400 && !task.IsCompleted; i++)
                {
                    if (vm.IsConfirmOpen)
                        vm.CancelConfirmCommand.Execute(null);
                    System.Threading.Thread.Sleep(1);
                    Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                }
                clicks++;
                if (!task.IsCompleted)
                    return "never finished";
                var step = tab.Record.History.NextUndo;
                var changed = !ReferenceEquals(step, before) && step is not null;
                var note = vm.IsAlertOpen ? $" · {(vm.AlertIsError ? "error" : "says")} '{vm.AlertText}'" : "";
                return (changed ? $"{step!.Changes.Count} value(s): {string.Join(", ", step.Changes.Take(6).Select(c => $"{c.Key}={c.After ?? "∅"}"))}{(step.Changes.Count > 6 ? ", …" : "")}"
                    : "no change") + note;
            }

            Console.WriteLine($"info  sweep {type,-28} {asset}: {tab.ButtonRows.Count()} groups, {tab.ButtonRows.Count(r => r.IsShown)} shown, {tab.VisibleButtons.Count()} buttons on the form");
            // Grow the lists: each visible Add button once, twice over.
            for (var round = 0; round < 2; round++)
                foreach (var add in tab.VisibleButtons.Where(b => b.Label.StartsWith("Add", StringComparison.OrdinalIgnoreCase)).ToList())
                    Console.WriteLine($"info  sweep {type,-28} {add.Title,-46} (setup) {Run(add)}");

            foreach (var button in tab.VisibleButtons.ToList())
            {
                if (!tab.VisibleButtons.Contains(button))
                    continue;
                var before = tab.Record.History.NextUndo;
                var outcome = Run(button);
                Console.WriteLine($"info  sweep {type,-28} {button.Title,-46} {outcome}");
                if (outcome.Contains("never finished") || outcome.Contains("· error"))
                    failures.Add($"{asset}: {button.Title}: {outcome}");
                if (!ReferenceEquals(tab.Record.History.NextUndo, before))
                    tab.UndoCommand.Execute(null);
            }
            vm.DismissAlertCommand.Execute(null);
        }
        Check($"sweep: {clicks} button clicks over {subjects.Count} assets ({sw.ElapsedMilliseconds} ms), none failed{(failures.Count > 0 ? ": " + string.Join("; ", failures) : "")}",
            failures.Count == 0 && clicks > 0);
    }

    private static void RunButtonChecks(string outDir)
    {
        if (!Directory.Exists(Path.Combine(InstallRoot, "deffiles")))
        {
            Console.WriteLine("buttons: BO3 not found — skipped");
            return;
        }
        var install = NewScratch("buttons-install");
        CopyTree(Path.Combine(InstallRoot, "deffiles"), Path.Combine(install, "deffiles"));
        File.WriteAllText(Path.Combine(install, "deffiles", "apextest.awi"), ButtonTestDeffile);
        Directory.CreateDirectory(Path.Combine(install, "AssetWorks", "scripts"));
        var csv = Path.Combine(InstallRoot, "AssetWorks", "scripts", "assetpreviewer_lookuptable.csv");
        File.Copy(csv, Path.Combine(install, "AssetWorks", "scripts", Path.GetFileName(csv)));
        var listRel = @"source_data\c_t8_wz_characters.gdt";
        var modelRel = @"source_data\mwr_lsr2_model_files.gdt";
        var animRel = @"source_data\zm_ai_parasite.gdt";
        foreach (var rel in new[] { listRel, modelRel, animRel }.Concat(SweepRealAssets.Select(a => a.Gdt)).Distinct())
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(install, rel))!);
            File.Copy(Path.Combine(InstallRoot, rel), Path.Combine(install, rel));
        }
        File.WriteAllText(Path.Combine(install, "source_data", "apextest.gdt"), ButtonTestGdt);
        var sweepGdt = new System.Text.StringBuilder("{\r\n");
        foreach (var type in SweepBareTypes)
            sweepGdt.Append($"\t\"apex_sweep_{type}\" ( \"{type}.gdf\" )\r\n\t{{\r\n\t}}\r\n");
        sweepGdt.Append("\t\"apex_sweep_fxanim\" ( \"scriptbundle.gdf\" )\r\n\t{\r\n\t\t\"sceneType\" \"fxanim\"\r\n\t}\r\n}\r\n");
        File.WriteAllText(Path.Combine(install, "source_data", "apex_sweep.gdt"), sweepGdt.ToString());
        var listPath = Path.Combine(install, listRel);

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
            Check($"buttons: Apex loaded the temp install live ('{vm.Status}')", vm.Status.StartsWith("Loaded") && !vm.IsMockData);

            ModelLodButtons(window, vm, outDir);
            ListHelperButtons(window, vm, outDir, listPath);
            PopulateButton(window, vm, install);
            QuestionButtons(window, vm, outDir);
            ButtonRobustness(window, vm);
            CamoCopy(vm);
            DestructibleButtons(vm);
            ButtonSweep(vm);
        }
        catch (Exception ex)
        {
            Check($"buttons: {ex}", false);
        }
        finally
        {
            Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
            window?.Close();
            vm?.Dispose();
            Environment.SetEnvironmentVariable("APEX_FORCE_MOCK", saved.Mock);
            Environment.SetEnvironmentVariable("APEX_BO3_ROOT", saved.Root);
            Environment.SetEnvironmentVariable("APEX_NO_PERSIST", saved.Persist);
            SchemaRegistry.ResetToMock();
            try { Directory.Delete(install, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>The Button the form draws for <paramref name="label"/> in group <paramref name="group"/>, scrolled into view.</summary>
    private static Button? DeffileButton(Window window, AssetEditorViewModel tab, string group, string label)
    {
        var row = tab.ButtonRows.FirstOrDefault(r => r.Name.Equals(group, StringComparison.OrdinalIgnoreCase));
        if (row is null)
            return null;
        var index = tab.FlatRows.IndexOf(row);
        if (index < 0)
            return null;
        var form = window.GetVisualDescendants().OfType<AssetEditorView>().First().FindControl<ItemsControl>("Form")!;
        form.ScrollIntoView(index);
        Pump();
        window.UpdateLayout();
        Pump();
        return window.GetVisualDescendants().OfType<Button>()
            .FirstOrDefault(b => b.DataContext is DeffileButtonViewModel d && ReferenceEquals(d.Group, row) && d.Label == label && b.IsEffectivelyVisible);
    }

    /// <summary>A real click on a deffile button, then waits for its callback's edit to land. Returns the milliseconds it took.</summary>
    private static double ClickButton(Window window, AssetEditorViewModel tab, string group, string label, Func<bool>? landed = null)
    {
        var button = DeffileButton(window, tab, group, label);
        if (button is null)
        {
            Check($"buttons: '{label}' of {group} is on screen", false);
            return double.NaN;
        }
        var steps = tab.LastHistoryText;
        var sw = Stopwatch.StartNew();
        Click(window, button);
        WaitUntil(() => landed?.Invoke() ?? tab.LastHistoryText != steps, 5_000);
        var t = tab.LastButtonTimings;
        Console.WriteLine($"info  buttons: {tab.Name} {label}: script {t.ScriptMs:0.0} ms off the UI thread, apply {t.ApplyMs:0.0} ms on it, click to applied {t.TotalMs:0.0} ms");
        return t.TotalMs;
    }

    private static string Prop(AssetEditorViewModel tab, string key) => tab.Record.Properties.GetValueOrDefault(key) ?? "(absent)";

    private static bool Shown(AssetEditorViewModel tab, string key) => tab.FlatRows.OfType<PropertyItemViewModel>().Any(p => p.Key == key);

    private static void ModelLodButtons(Window window, MainViewModel vm, string outDir)
    {
        vm.OpenByName("mwr_lsr_foliage_branch_06");
        Pump(300);
        var tab = vm.ActiveTab!;
        var row = tab.ButtonRows.FirstOrDefault(r => r.Name == "numLods");
        Check($"xmodel: the Number of LODs buttons show in LODs ({string.Join(" ", row?.Buttons.Select(b => b.Label) ?? [])})",
            row is { Title: "Number of LODs", IsShown: true } && tab.FlatRows.Contains(row)
            && row.Buttons.Select(b => b.Label).SequenceEqual(new[] { "1", "2", "3", "4", "5", "6", "7", "8", "XModel Info" }));
        Check("xmodel: it sits in the LODs section, right above the next property the deffile adds",
            row is not null && tab.FlatRows.IndexOf(row) is var at && at > 0
            && tab.FlatRows[at + 1] is SubsectionRowViewModel { Title: "Custom LOD Parameters" }
            && tab.FlatRows[at + 2] is PropertyItemViewModel { Key: "customAutogenParams" });
        var titles = tab.FlatRows.OfType<SubsectionRowViewModel>().Select(r => r.Title).ToList();
        Check($"xmodel: each LOD is titled, in order ({string.Join(", ", titles.Take(5))})",
            titles.Take(5).SequenceEqual(new[] { "Custom LOD Parameters", "LOD 0", "LOD 1", "LOD 2", "LOD 3" }));
        Check("xmodel: every LOD's File row follows its title",
            new[] { "filename", "mediumLod", "lowLod", "lowestLod" }.Select(k => tab.FlatRows.OfType<PropertyItemViewModel>().First(p => p.Key == k))
                .Zip(titles.Skip(1), (file, title) => tab.FlatRows[tab.FlatRows.IndexOf(file) - 1] is SubsectionRowViewModel t && t.Title == title).All(ok => ok));
        Check("xmodel: four LODs to start (three auto-generated)", Shown(tab, "lowestLod") && !Shown(tab, "lod4File"));
        Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
        tab.RevealProperty("customAutogenParams");
        Pump(100);
        Capture(window, Path.Combine(outDir, "60-buttons-xmodel-lods.png"));
        Avalonia.Application.Current.RequestedThemeVariant = ThemeVariant.Light;
        Capture(window, Path.Combine(outDir, "61-light-buttons-xmodel-lods.png"));
        Avalonia.Application.Current.RequestedThemeVariant = ThemeVariant.Dark;

        var ms = ClickButton(window, tab, "numLods", "1");
        var step = tab.Record.History.NextUndo;
        Check($"xmodel: clicking 1 leaves LOD0 only, in one undo step ({Describe(step)}, {ms:0} ms)",
            Prop(tab, "autogenMediumLod") == "0" && Prop(tab, "autogenLowLod") == "0" && Prop(tab, "autogenLowestLod") == "0"
            && step is { Changes.Count: 3 } && !Shown(tab, "mediumLod") && !Shown(tab, "autogenMediumLod"));
        Check("xmodel: numLods (APE never saves it) isn't written to the asset", !tab.Record.Properties.ContainsKey("numLods"));

        ms = ClickButton(window, tab, "numLods", "3");
        step = tab.Record.History.NextUndo;
        Check($"xmodel: clicking 3 adds LOD1 and LOD2, auto-generated at 50% and 25% ({Describe(step)}, {ms:0} ms)",
            Prop(tab, "autogenMediumLod") == "1" && Prop(tab, "autogenMediumLodPercent") == "50"
            && Prop(tab, "autogenLowLod") == "1" && Prop(tab, "autogenLowLodPercent") == "25" && Prop(tab, "autogenLowestLod") == "0"
            && step is { Changes.Count: 4 } && Shown(tab, "mediumLod") && Shown(tab, "lowLod") && !Shown(tab, "lowestLod"));
        Console.WriteLine($"info  buttons: xmodel LOD click → edit applied in {ms:0.0} ms (script off the UI thread, apply on it)");
        Check($"xmodel: the status names the group and the button once ('{vm.Status}')",
            vm.Status.StartsWith("Number of LODs → 3: 4 values changed.") && !vm.Status.Contains(": 3:"));

        Key(window, Avalonia.Input.Key.Z, RawInputModifiers.Control);
        Check($"xmodel: Ctrl+Z undoes the click as one step ('{vm.Status}')",
            Prop(tab, "autogenMediumLod") == "0" && Prop(tab, "autogenMediumLodPercent") == "90" && !Shown(tab, "mediumLod"));
        Key(window, Avalonia.Input.Key.Z, RawInputModifiers.Control);
        Check("xmodel: and the one before it", Prop(tab, "autogenLowestLod") == "1" && Shown(tab, "lowestLod"));

        ClickButton(window, tab, "numLods", "XModel Info", () => vm.IsAlertOpen);
        Check($"xmodel: XModel Info says what it can ('{vm.AlertText}')", vm.IsAlertOpen && vm.AlertText.StartsWith(tab.Name));
        vm.DismissAlertCommand.Execute(null);

        // Keyboard: Tab from the row above reaches the buttons; Enter presses one.
        var first = DeffileButton(window, tab, "numLods", "2");
        first?.Focus(NavigationMethod.Tab);
        Pump();
        Key(window, Avalonia.Input.Key.Enter);
        WaitUntil(() => Prop(tab, "autogenLowLod") == "0", 3_000);
        Check($"xmodel: Enter on a focused button runs it ({Prop(tab, "autogenLowLod")})", first is { IsFocused: true } && Prop(tab, "autogenLowLod") == "0");
        Key(window, Avalonia.Input.Key.Z, RawInputModifiers.Control);

        // The palette lists the open asset's buttons.
        vm.OpenCommandPaletteCommand.Execute(null);
        vm.PaletteText = ">number of lods 2";
        Pump();
        Check($"palette: lists the asset's deffile buttons ('{vm.PaletteResults.FirstOrDefault()?.Name}')",
            vm.PaletteResults.Any(r => r.Name == "Number of LODs → 2"));
        vm.ClosePaletteCommand.Execute(null);

        // At the narrowest editor pane the nine buttons wrap inside the value column: none past its edge, none on another.
        var width = window.Width;
        window.Width = 1110;
        Pump();
        foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
        {
            Avalonia.Application.Current!.RequestedThemeVariant = theme;
            var one = DeffileButton(window, tab, "numLods", "1");
            var panel = one?.FindAncestorOfType<ItemsControl>();
            var buttons = panel?.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible).ToList() ?? new();
            var view = window.GetVisualDescendants().OfType<AssetEditorView>().First();
            var column = panel is null ? default : BoundsIn(panel, window);
            var boxes = buttons.Select(b => BoundsIn(b, window)).ToList();
            var overlaps = boxes.SelectMany((a, i) => boxes.Skip(i + 1).Where(b => a.Intersects(b))).Count();
            var lines = boxes.Select(b => Math.Round(b.Top)).Distinct().Count();
            var label = one?.GetVisualAncestors().OfType<Border>().FirstOrDefault(b => b.Classes.Contains("prow")) is { } rowBorder ? rowBorder.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Name == "GroupLabel") : null;
            Check($"xmodel ({theme}): at a {view.Bounds.Width:0} px editor the {buttons.Count} LOD buttons wrap onto {lines} lines inside the value column ({overlaps} overlaps), the title on the first line",
                buttons.Count == 9 && lines >= 2 && overlaps == 0 && boxes.All(b => b.Left >= column.Left - 0.5 && b.Right <= column.Right + 0.5)
                && label is not null && Math.Abs(BoundsIn(label, window).Center.Y - boxes[0].Center.Y) < 4);
            Capture(window, Path.Combine(outDir, theme == ThemeVariant.Dark ? "62-buttons-narrow-dark.png" : "63-buttons-narrow-light.png"));
        }
        Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
        window.Width = width;
        Pump();
    }

    private static string Describe(EditStep? step) =>
        step is null ? "no step" : string.Join(", ", step.Changes.Select(c => $"{c.Key} {c.Before ?? "∅"}→{c.After ?? "∅"}"));

    private static void ListHelperButtons(Window window, MainViewModel vm, string outDir, string listPath)
    {
        const string asset = "zm_character_customization_t8wz";
        vm.OpenByName(asset);
        Pump(300);
        var tab = vm.ActiveTab!;
        string Items() => string.Join(",", Enumerable.Range(1, 7).Select(i => Prop(tab, $"bodyType{i:00}") is var v && v.StartsWith("pbt_t8_wz_") ? v[10..] : v)) + " #" + Prop(tab, "bodyTypeCount");
        var original = Items();
        Check($"list: {asset} starts with five body types ({original})", original == "mason,reznov,woods,menendez,hudson,, #5");
        Check("list: each item has its Edit buttons, the next free one Add Item",
            tab.ButtonRows.FirstOrDefault(r => r.Name == "bodyTypebuttonGroup3") is { IsShown: true } g3
            && g3.Buttons.Select(b => b.Label).SequenceEqual(new[] { "Insert Before", "Move Up", "Move Down", "Delete" })
            && tab.ButtonRows.FirstOrDefault(r => r.Name == "bodyTypebuttonGroup6") is { IsShown: true } g6
            && g6.Buttons.Select(b => b.Label).SequenceEqual(new[] { "Add Item" })
            && tab.ButtonRows.All(r => r.Name != "bodyTypebuttonGroup7" || !r.IsShown));
        var bytes = File.ReadAllBytes(listPath);

        tab.RevealProperty("bodyType03");
        Pump(100);
        Capture(window, Path.Combine(outDir, "62-buttons-list.png"));
        Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
        Capture(window, Path.Combine(outDir, "63-light-buttons-list.png"));
        Avalonia.Application.Current.RequestedThemeVariant = ThemeVariant.Dark;

        var ms = ClickButton(window, tab, "bodyTypebuttonGroup3", "Insert Before");
        Check($"list: Insert Before 3 opens a gap at 3 ({Items()}, {ms:0} ms)", Items() == "mason,reznov,,woods,menendez,hudson, #6");
        Check("list: one undo step", tab.Record.History.NextUndo is { Changes.Count: 5 });
        Check("list: the new last item now has its Edit buttons and Add Item moved down",
            tab.ButtonRows.First(r => r.Name == "bodyTypebuttonGroup6").Buttons.Any(b => b.Label == "Delete")
            && tab.ButtonRows.First(r => r.Name == "bodyTypebuttonGroup7") is { IsShown: true } add && add.Buttons.Single().Label == "Add Item"
            && tab.FlatRows.Contains(add));

        // Save: the file holds exactly the five values, the hidden count among them.
        window.KeyPress(Avalonia.Input.Key.S, RawInputModifiers.Control, PhysicalKey.S, "s");
        WaitUntil(() => !vm.IsSaveRunning, 10_000);
        Pump();
        var expected = bytes;
        foreach (var (key, value) in new[] { ("bodyType03", ""), ("bodyType04", "pbt_t8_wz_woods"), ("bodyType05", "pbt_t8_wz_menendez"),
                     ("bodyType06", "pbt_t8_wz_hudson"), ("bodyTypeCount", "6") })
            expected = WithValue(expected, asset, key, value);
        Check($"list: Ctrl+S writes the inserted item and the hidden count, byte for byte ('{vm.Status}')",
            File.ReadAllBytes(listPath).AsSpan().SequenceEqual(expected));

        Key(window, Avalonia.Input.Key.Z, RawInputModifiers.Control);
        Check($"list: Ctrl+Z puts the list back ({Items()})", Items() == original);
        window.KeyPress(Avalonia.Input.Key.S, RawInputModifiers.Control, PhysicalKey.S, "s");
        WaitUntil(() => !vm.IsSaveRunning, 10_000);
        Pump();
        Check("list: saving the undo puts the file back byte for byte", File.ReadAllBytes(listPath).AsSpan().SequenceEqual(bytes));

        ClickButton(window, tab, "bodyTypebuttonGroup4", "Move Up");
        Check($"list: Move Up on 4 swaps 3 and 4 ({Items()})", Items() == "mason,reznov,menendez,woods,hudson,, #5");
        Gate($"list: a warm click holds the UI thread under a frame (apply {tab.LastButtonTimings.ApplyMs:0.0} ms)", tab.LastButtonTimings.ApplyMs < 16);
        ClickButton(window, tab, "bodyTypebuttonGroup1", "Move Down");
        Check($"list: Move Down on 1 swaps 1 and 2 ({Items()})", Items() == "reznov,mason,menendez,woods,hudson,, #5");
        ClickButton(window, tab, "bodyTypebuttonGroup2", "Delete");
        Check($"list: Delete 2 closes the gap, no question asked ({Items()})",
            Items() == "reznov,menendez,woods,hudson,,, #4" && !vm.IsConfirmOpen);
        // Add Item splices the new item's row and button row into the form: the rows already there stay (no rebuild).
        var removed = 0;
        var before = tab.FlatRows.Count;
        void Count(object? s, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) =>
            removed += e.OldItems?.Count ?? (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset ? before : 0);
        tab.FlatRows.CollectionChanged += Count;
        ClickButton(window, tab, "bodyTypebuttonGroup5", "Add Item");
        tab.FlatRows.CollectionChanged -= Count;
        Gate($"list: Add Item splices the form ({removed} of {before} rows removed, apply {tab.LastButtonTimings.ApplyMs:0.0} ms on the UI thread)",
            removed <= 2, tab.LastButtonTimings.ApplyMs < 16);
        Check($"list: Add Item on 5 adds an empty fifth ({Items()})", Items() == "reznov,menendez,woods,hudson,,, #5" && Shown(tab, "bodyType05"));
        for (var i = 0; i < 4; i++)
            Key(window, Avalonia.Input.Key.Z, RawInputModifiers.Control);
        Check($"list: four Ctrl+Z undo the four clicks ({Items()})", Items() == original);
    }

    private static void PopulateButton(Window window, MainViewModel vm, string install)
    {
        const string asset = "ai_zombie_zod_parasite_attack_acid";
        vm.OpenByName(asset);
        Pump(300);
        var tab = vm.ActiveTab!;
        // What the deffile's PopulateButtonClicked gives: every line of the lookup table whose pattern is in the name.
        var expected = new Dictionary<string, string>();
        foreach (var line in File.ReadAllLines(Path.Combine(install, "AssetWorks", "scripts", "assetpreviewer_lookuptable.csv")))
            if (line.Split(',') is { Length: 3 } f && asset.Contains(f[0]))
                expected[f[1]] = f[2];
        var ms = ClickButton(window, tab, "populateButton", "Populate");
        var got = string.Join(", ", expected.Keys.Select(k => $"{k}={Prop(tab, k)}"));
        Check($"xanim: Populate fills the preview fields from the install's lookup table ({got}, {ms:0} ms)",
            expected.Count == 4 && expected.All(kv => Prop(tab, kv.Key) == kv.Value) && tab.Record.History.NextUndo is { Changes.Count: 4 });
        Key(window, Avalonia.Input.Key.Z, RawInputModifiers.Control);
        Check($"xanim: Ctrl+Z restores them ({Prop(tab, "previewModel")})", Prop(tab, "previewModel") == "c_zom_parasite_fb");
    }

    /// <summary>
    /// The real install, read-only: a deffile reading a key before adding its entry now gets the saved value (it got ""
    /// before). For the common types that must change nothing: the run only differs where such a key holds a value,
    /// so this counts the assets where one does. Also spot-checks vehiclecustomsettings' meleedamage, which a
    /// GetEntryVariable(...).Show now hides for anything but a spider or a parasite, as the deffile says.
    /// </summary>
    private static void RunLiveButtonRegression()
    {
        var env = new Apex.Editor.Services.Gdt.GameEnvironment();
        if (!env.IsAvailable)
        {
            Console.WriteLine("buttons (live): BO3 not found — skipped");
            return;
        }
        var schemas = Apex.Editor.Services.Gdf.GdfSchemaLoader.LoadAll(env.DeffilesDir!);
        SchemaRegistry.Populate(schemas);
        var db = Apex.Editor.Services.Gdt.GdtLoader.LoadAll(env);
        string[] common = { "bulletweapon", "vehicle", "material", "image", "xmodel", "xanim" };
        string[] intended = { "scriptbundle", "vehicleriders" };
        foreach (var type in common.Concat(intended))
        {
            var sample = db.Assets.Where(a => a.Type.Equals(type, StringComparison.OrdinalIgnoreCase) && a.Parent is null).Take(60).ToList();
            var differ = 0;
            var keys = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var asset in sample)
            {
                var values = asset.ScanProperties;
                var now = Apex.Editor.Services.Gdf.GdfRuntime.EvaluateOverlay(type, asset.Name, values);
                Apex.Editor.Services.Gdf.SchemaOverlay? before;
                Apex.Editor.Services.Gdf.GdfRuntime.LegacyUnaddedReads = true;
                try
                {
                    before = Apex.Editor.Services.Gdf.GdfRuntime.EvaluateOverlay(type, asset.Name, values);
                }
                finally
                {
                    Apex.Editor.Services.Gdf.GdfRuntime.LegacyUnaddedReads = false;
                }
                var changed = (now?.Rules ?? new Dictionary<string, Apex.Editor.Services.Gdf.PropertyRule>()).Keys
                    .Union(before?.Rules.Keys ?? [])
                    .Where(k => Describe(now?.Rules.GetValueOrDefault(k)) != Describe(before?.Rules.GetValueOrDefault(k)))
                    .ToList();
                if (changed.Count > 0 || (now is null) != (before is null))
                    differ++;
                foreach (var k in changed)
                    keys[k] = keys.GetValueOrDefault(k) + 1;
            }
            static string Describe(Apex.Editor.Services.Gdf.PropertyRule? r) =>
                r is null ? "-" : $"{r.Visible}/{r.Enabled}/{(r.Choices is null ? "" : string.Join("|", r.Choices))}";
            var note = $"{type}: {sample.Count} real assets, rules differ on {differ} ({string.Join(", ", keys.OrderByDescending(k => k.Value).Take(8).Select(k => $"{k.Key}×{k.Value}"))})";
            // xmodel's GenerateUI counts the LODs from filename and the autogen flags before adding them; read as "" that
            // was always 8, so Max Rendered LOD offered LOD1-7 on every model. Now it offers the model's own, as APE does.
            if (type == "xmodel")
                Check($"read before add (live): {note} — only Max Rendered LOD's options, now the model's own LODs",
                    sample.Count > 0 && keys.Keys.All(k => k == "dropLOD"));
            else if (common.Contains(type))
                Check($"read before add (live): {note}", sample.Count > 0 && differ == 0);
            else
                Console.WriteLine($"info  read before add (live): {note} — intended");
        }

        foreach (var asset in db.Assets.Where(a => a.Type.Equals("vehiclecustomsettings", StringComparison.OrdinalIgnoreCase)).Take(12))
        {
            var values = asset.ScanProperties;
            var kind = values.GetValueOrDefault("vehicletype") ?? "";
            var rule = Apex.Editor.Services.Gdf.GdfRuntime.EvaluateOverlay("vehiclecustomsettings", asset.Name, values)?.Rules.GetValueOrDefault("meleedamage");
            Check($"vehiclecustomsettings (live): {asset.Name} ({kind}) shows meleedamage only for a spider or parasite ({(rule?.Visible == true ? "shown" : "hidden")})",
                rule is not null && rule.Visible == (kind is "spider" or "parasite"));
        }
    }

    /// <summary>Waits for a button run started without a click (the sweep and the palette path) to land.</summary>
    private static void Await(System.Threading.Tasks.Task task)
    {
        for (var i = 0; i < 2000 && !task.IsCompleted; i++)
        {
            System.Threading.Thread.Sleep(1);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>weaponcamo's Copy buttons on a real camo table: its camos share their base materials, so make one differ first.</summary>
    private static void CamoCopy(MainViewModel vm)
    {
        if (FindRecord(vm, "wpn_t7_camo_dlc1_base76.gdt", "weaponcamo") is not { } camo)
            return;
        vm.OpenByName(camo.Name);
        Pump(50);
        var tab = vm.ActiveTab!;
        void Set(string key, string value) => tab.AllSentinel.All.First(p => p.Key == key).RawValue = value;
        Set("material1_1_numBaseMaterials", "1");
        var source = tab.AllSentinel.All.First(p => p.Key == "material1_1_base_material_1");
        source.RawValue = "apex_test_mat";
        var copy = tab.ButtonRows.First(r => r.Name == "copyFromFirst3").Buttons.First(b => b.Label == "Copy from First");
        Await(tab.RunButtonAsync(copy.Group, copy.Button));
        Check($"weaponcamo: Copy from First copies camo 1's base material to camo 3 ({Prop(tab, "material1_3_base_material_1")}; {Describe(tab.Record.History.NextUndo)})",
            Prop(tab, "material1_3_base_material_1") == "apex_test_mat" && Prop(tab, "material1_2_base_material_1") != "apex_test_mat");
        tab.UndoCommand.Execute(null);
        var all = tab.ButtonRows.First(r => r.Name == "copyFromFirst2").Buttons.First(b => b.Label == "Copy to all next");
        Set("material1_2_numBaseMaterials", "1");
        Set("material1_2_base_material_1", "apex_test_two");
        Await(tab.RunButtonAsync(all.Group, all.Button));
        var numCamos = int.Parse(Prop(tab, "numCamos"));
        Check($"weaponcamo: Copy to all next copies camo 2's to camos 3 to 75 ({tab.Record.History.NextUndo?.Changes.Count} values)",
            Enumerable.Range(3, 73).All(c => Prop(tab, $"material1_{c}_base_material_1") == "apex_test_two") && Prop(tab, "material1_1_base_material_1") == "apex_test_mat");
        while (tab.CanUndo)
            tab.UndoCommand.Execute(null);
        Check("weaponcamo: undone", Prop(tab, "material1_3_base_material_1") != "apex_test_two" && numCamos > 0);
    }

    /// <summary>
    /// What can go wrong around a click: values edited while its question waits, the tab closing, the dialog already in
    /// use, a second click, ClearSpecified, and scripts that run away (time, messages, size).
    /// </summary>
    private static void ButtonRobustness(Window window, MainViewModel vm)
    {
        vm.OpenByName("apextest_one");
        Pump(100);
        var tab = vm.ActiveTab!;
        var speed = tab.AllSentinel.All.First(p => p.Key == "speed");
        DeffileButtonViewModel B(string label) => tab.ButtonRows.SelectMany(r => r.Buttons).First(b => b.Label == label);
        void Yes()
        {
            var yes = window.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Command == vm.AcceptConfirmCommand && b.IsEffectivelyVisible);
            if (yes is not null)
                Click(window, yes);
        }
        while (tab.CanUndo)
            tab.UndoCommand.Execute(null);

        // 1. An edit while the question waits wins; the click applies nothing and says why.
        var task = tab.RunButtonAsync(B("To 5").Group, B("To 5").Button);
        WaitUntil(() => vm.IsConfirmOpen, 3_000);
        speed.RawValue = "7";
        var steps = tab.Record.History.NextUndo;
        Yes();
        Await(task);
        Check($"stale: an edit made while the question waited is kept, nothing applied ('{vm.AlertText}')",
            Prop(tab, "speed") == "7" && ReferenceEquals(tab.Record.History.NextUndo, steps) && vm.IsAlertOpen && vm.AlertText.Contains("changed while"));
        vm.DismissAlertCommand.Execute(null);

        // 1b. The tab closes while the question waits: nothing lands on it.
        task = tab.RunButtonAsync(B("To 5").Group, B("To 5").Button);
        WaitUntil(() => vm.IsConfirmOpen, 3_000);
        vm.CloseActiveTabCommand.Execute(null);
        Pump();
        Yes();
        Await(task);
        Check($"closed: nothing applied to a tab closed while its question waited (speed {Prop(tab, "speed")}, '{vm.AlertText}')",
            Prop(tab, "speed") == "7" && vm.AlertText.Contains("tab closed"));
        vm.DismissAlertCommand.Execute(null);
        vm.OpenByName("apextest_one");
        Pump(100);
        tab = vm.ActiveTab!;
        speed = tab.AllSentinel.All.First(p => p.Key == "speed");

        // 2. The dialog is the user's: a button's question never takes its place.
        var deleted = false;
        vm.AskConfirm("Delete something?", "A question of the user's own.", "Delete", () => deleted = true);
        task = tab.RunButtonAsync(B("To 5").Group, B("To 5").Button);
        Await(task);
        Check($"confirm: a question already open stays, the button's run is dropped and says so ('{vm.ConfirmTitle}', '{vm.AlertText}')",
            vm.IsConfirmOpen && vm.ConfirmTitle == "Delete something?" && Prop(tab, "speed") == "7" && vm.AlertText.Contains("another question"));
        vm.AcceptConfirmCommand.Execute(null);
        Check("confirm: and the user's own question still does what it said", deleted);
        vm.DismissAlertCommand.Execute(null);

        // 8. A second click while one waits gets an answer in the status line.
        task = tab.RunButtonAsync(B("To 5").Group, B("To 5").Button);
        WaitUntil(() => vm.IsConfirmOpen, 3_000);
        Await(tab.RunButtonAsync(B("Say").Group, B("Say").Button));
        Check($"busy: a click while another button waits says so ('{vm.Status}')", vm.Status.Contains("still running"));
        vm.CancelConfirmCommand.Execute(null);
        Await(task);

        // 4. ClearSpecified: alone, the key goes (inherits); then set to what it inherits, the key goes; set otherwise, it stays.
        Await(tab.RunButtonAsync(B("Clear notes").Group, B("Clear notes").Button));
        Check($"clear: ClearSpecified alone removes the key ({Prop(tab, "notes")})", !tab.Record.Properties.ContainsKey("notes"));
        Await(tab.RunButtonAsync(B("Speed to default").Group, B("Speed to default").Button));
        Check($"clear: ClearSpecified then the default removes the key ({Prop(tab, "speed")})", !tab.Record.Properties.ContainsKey("speed"));
        Await(tab.RunButtonAsync(B("Speed to 9").Group, B("Speed to 9").Button));
        Check($"clear: ClearSpecified then another value sets it ({Prop(tab, "speed")})", Prop(tab, "speed") == "9");
        tab.UndoCommand.Execute(null);
        tab.UndoCommand.Execute(null);
        tab.UndoCommand.Execute(null);
        Check($"clear: undone, the keys are back ({Prop(tab, "notes")}, {Prop(tab, "speed")})", Prop(tab, "notes") == "hello" && Prop(tab, "speed") == "7");

        // 7. Runaway scripts stop, say why, and what they did first is one undo step.
        var limit = Apex.Editor.Services.Gdf.DeffileButtons.TimeLimit;
        Apex.Editor.Services.Gdf.DeffileButtons.TimeLimit = TimeSpan.FromMilliseconds(1);
        try
        {
            Await(tab.RunButtonAsync(B("Spin").Group, B("Spin").Button));
        }
        finally
        {
            Apex.Editor.Services.Gdf.DeffileButtons.TimeLimit = limit;
        }
        Check($"runaway: a script past its time limit stops ('{vm.AlertText}')",
            vm.AlertIsError && vm.AlertText.Contains("stopped") && Prop(tab, "speed") == "7");
        vm.DismissAlertCommand.Execute(null);
        Await(tab.RunButtonAsync(B("Nag").Group, B("Nag").Button));
        Check($"runaway: a script putting up message after message stops at {AssetEditorViewModel.MaxButtonMessages}, and the messages before still show ('{vm.AlertText[..Math.Min(80, vm.AlertText.Length)]}…')",
            vm.AlertIsError && vm.AlertText.Contains("stopped partway") && vm.AlertText.Contains("Nag 0.") && Prop(tab, "speed") == "60");
        vm.DismissAlertCommand.Execute(null);
        tab.UndoCommand.Execute(null);
        Await(tab.RunButtonAsync(B("Huge").Group, B("Huge").Button));
        Check($"runaway: a string built past 16M characters stops the script cleanly ({Prop(tab, "notes").Length} characters kept, '{vm.AlertDetail}')",
            Prop(tab, "notes") == "hello" && vm.AlertIsError && vm.AlertDetail?.Contains("16M characters") == true);
        vm.DismissAlertCommand.Execute(null);

        // 2. Recursion with no loop (2^40 calls) is held to the time limit by the calls themselves.
        Apex.Editor.Services.Gdf.DeffileButtons.TimeLimit = TimeSpan.FromMilliseconds(50);
        var clock = Stopwatch.StartNew();
        try
        {
            Await(tab.RunButtonAsync(B("Recurse").Group, B("Recurse").Button));
        }
        finally
        {
            Apex.Editor.Services.Gdf.DeffileButtons.TimeLimit = limit;
        }
        Check($"runaway: recursion without a loop stops at the time limit ({clock.ElapsedMilliseconds} ms, '{vm.AlertDetail}')",
            vm.AlertIsError && vm.AlertDetail?.Contains("time limit") == true && Prop(tab, "speed") == "7" && clock.ElapsedMilliseconds < 2_000);
        vm.DismissAlertCommand.Execute(null);
        Await(tab.RunButtonAsync(B("Say").Group, B("Say").Button));
        Check($"runaway: and the tab takes clicks again ('{vm.AlertText}')", vm.AlertText == "Speed is 7.");
        vm.DismissAlertCommand.Execute(null);

        // 1. An edit to the parent while a child's question waits: nothing applied (GenerateUI's globals and what the
        // child inherits both came from it).
        var parentRecord = tab.Record;
        vm.OpenByName("apextest_child");
        Pump(100);
        var child = vm.ActiveTab!;
        var childStep = child.Record.History.NextUndo;
        var reset = child.ButtonRows.SelectMany(r => r.Buttons).First(b => b.Label == "To 5");
        task = child.RunButtonAsync(reset.Group, reset.Button);
        WaitUntil(() => vm.IsConfirmOpen, 3_000);
        Apex.Editor.Models.EditHistory.Set(parentRecord, "notes", "edited in the parent");
        Yes();
        Await(task);
        Check($"stale: an edit to the parent while the child's question waited stops the apply ('{vm.AlertText}')",
            !child.Record.Properties.ContainsKey("speed") && ReferenceEquals(child.Record.History.NextUndo, childStep)
            && vm.AlertText.Contains("apextest_one changed while"));
        vm.DismissAlertCommand.Execute(null);
        Apex.Editor.Models.EditHistory.Set(parentRecord, "notes", "hello");
    }

    /// <summary>destructibledef: Show Parent goes to the parent's row, Copy and Paste carry a piece, Reset's value isn't saved.</summary>
    private static void DestructibleButtons(MainViewModel vm)
    {
        vm.OpenByName("apex_sweep_destructibledef");
        Pump(50);
        var tab = vm.ActiveTab!;
        void Set(string key, string value) => tab.AllSentinel.All.First(p => p.Key == key).RawValue = value;
        DeffileButtonViewModel? Find(string group, string label) =>
            tab.ButtonRows.FirstOrDefault(r => r.Name == group && r.IsShown)?.Buttons.FirstOrDefault(b => b.Label == label);
        var reset = Find("liveUpdate", "Reset");
        if (reset is not null)
            Await(tab.RunButtonAsync(reset.Group, reset.Button));
        Check($"destructibledef: Reset's live-update flag (SetSave false) isn't written ({Prop(tab, "liveupdatereset")})",
            !tab.Record.Properties.ContainsKey("liveupdatereset") && reset is not null);

        Set("piece0", "dp_base");
        Set("piece1", "dp_arm");
        Set("parentPiece1", "0");
        Set("piece1Health", "250");
        Pump(50);
        var parent = Find("buttonGroup0", "Show Parent");
        if (parent is not null)
            Await(tab.RunButtonAsync(parent.Group, parent.Button));
        Check($"destructibledef: Show Parent goes to the parent's row ({tab.FocusedProperty?.Key})", tab.FocusedProperty?.Key == "piece0");

        var copy = Find("buttonGroup0", "Copy");
        var paste = Find("buttonGroup1", "Paste");
        if (copy is not null && paste is not null)
        {
            Await(tab.RunButtonAsync(copy.Group, copy.Button));
            var copied = tab.Record.History.NextUndo;
            Await(tab.RunButtonAsync(paste.Group, paste.Button));
            Check($"destructibledef: Copy changes nothing, Paste puts piece 1 into piece 2 ({Prop(tab, "piece2")}, health {Prop(tab, "piece2Health")})",
                ReferenceEquals(copied, tab.Record.History.NextUndo) is false && Prop(tab, "piece2") == "dp_arm" && Prop(tab, "piece2Health") == "250");
        }
        else
            Check("destructibledef: piece buttons show once a piece is set", false);
        while (tab.CanUndo)
            tab.UndoCommand.Execute(null);
    }

    private static void QuestionButtons(Window window, MainViewModel vm, string outDir)
    {
        vm.OpenByName("apextest_one");
        Pump(300);
        var tab = vm.ActiveTab!;
        var button = DeffileButton(window, tab, "resetButtons", "To 5");
        Check("question: one group added twice keeps both buttons",
            tab.ButtonRows.FirstOrDefault(r => r.Name == "resetButtons")?.Buttons.Select(b => b.Label).SequenceEqual(new[] { "To 5", "Say" }) == true);
        if (button is null)
        {
            Check("question: the button is on screen", false);
            return;
        }
        Click(window, button);
        WaitUntil(() => vm.IsConfirmOpen, 3_000);
        Check($"question: a YESNO the deffile asks opens Apex's confirm dialog and waits ('{vm.ConfirmTitle}')",
            vm.IsConfirmOpen && vm.ConfirmTitle == "Reset the speed to 5?" && Prop(tab, "speed") == "1");
        Pump(200);
        Check("question: the window stays live while it waits", Prop(tab, "speed") == "1" && vm.IsConfirmOpen);
        Capture(window, Path.Combine(outDir, "64-buttons-question.png"));
        Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
        Capture(window, Path.Combine(outDir, "65-light-buttons-question.png"));
        Avalonia.Application.Current.RequestedThemeVariant = ThemeVariant.Dark;
        ClickNamed(window, "ConfirmCancelButton");
        WaitUntil(() => tab.LastHistoryText.Contains("nothing"), 3_000);
        Check($"question: Cancel answers NO: nothing changes ('{tab.LastHistoryText}')",
            !vm.IsConfirmOpen && Prop(tab, "speed") == "1" && !tab.CanUndo);

        Click(window, DeffileButton(window, tab, "resetButtons", "To 5")!);
        WaitUntil(() => vm.IsConfirmOpen, 3_000);
        var yes = window.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Command == vm.AcceptConfirmCommand && b.IsEffectivelyVisible);
        if (yes is not null)
            Click(window, yes);
        WaitUntil(() => Prop(tab, "speed") == "5", 3_000);
        Check($"question: Yes answers YES: the callback goes on ({Prop(tab, "speed")})", Prop(tab, "speed") == "5" && tab.CanUndo);

        Click(window, DeffileButton(window, tab, "resetButtons", "Say")!);
        WaitUntil(() => vm.IsAlertOpen, 3_000);
        Check($"question: an OK message shows in the banner ('{vm.AlertText}')", vm.IsAlertOpen && vm.AlertText == "Speed is 5." && !vm.AlertIsError);
        vm.DismissAlertCommand.Execute(null);
    }
}
