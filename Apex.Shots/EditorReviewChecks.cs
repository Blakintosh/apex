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
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Apex.Editor.Controls;
using Apex.Editor.Models;
using Apex.Editor.Services;
using Apex.Editor.Services.Gdf;
using Apex.Editor.ViewModels;
using Apex.Editor.Views;
using K = Avalonia.Input.Key;

namespace Apex.Shots;

/// <summary>
/// The October 2026 design review's editor items, checked in the app with real input: values shown as the GDT spells
/// them, one value width and one fitted label column, a mark slot that never moves the field, placeholders, section
/// headers without counts, the rail's tooltips and ⚠, the Inspector's one problem line, and one problem count everywhere
/// (mock data); then on a temp install with the real deffiles: a choice checked against the options the asset's deffile
/// run offers (image compression), APE's option names and clean labels, a property the deffile hides counting no problem,
/// the rail listing only sections that apply, vectors on one row, and a disabled row naming what enables it.
/// </summary>
public partial class Program
{
    private static void RunEditorReviewChecks(string outDir)
    {
        ReviewMockChecks(outDir);
        ReviewLiveChecks(outDir);
    }

    /// <summary>The window as it is now, in the light theme (then back to dark).</summary>
    private static void CaptureLight(Window window, string path)
    {
        Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
        Pump(60);
        Capture(window, path);
        Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
        Pump(60);
    }

    // ═══ Mock data ════════════════════════════════════════════════════════════

    private static void ReviewMockChecks(string outDir)
    {
        var vm = new MainViewModel(sessionRoot: null);
        var window = new MainWindow { DataContext = vm, Width = 1500, Height = 950 };
        window.Show();
        window.Activate();
        window.UpdateLayout();
        Pump();
        try
        {
            vm.OpenByName("wpn_smg_wasp");
            Pump(100);
            var tab = vm.ActiveTab!;
            var editor = window.GetVisualDescendants().OfType<AssetEditorView>().First();
            List<PropertyEditorView> Views() => window.GetVisualDescendants().OfType<PropertyEditorView>()
                .Where(v => v.IsEffectivelyVisible && v.ShowMarks && v.DataContext is PropertyItemViewModel).ToList();

            // ── E5: a number shows the GDT's own text: no rounding, no thousands separators ──
            var damage = (NumberPropertyViewModel)tab.AllSentinel.All.First(p => p.Key == "maxDamageRange");
            const string raw = "12345.678901234";
            damage.RawValue = raw;
            tab.RevealProperty(damage.Key);
            Pump(50);
            window.UpdateLayout();
            var box = window.GetVisualDescendants().OfType<ScrubNumberBox>().First(b => b.DataContext == damage && b.IsEffectivelyVisible);
            var shown = box.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).FirstOrDefault(t => !string.IsNullOrEmpty(t));
            Check($"review E5: a number shows the GDT's text digit for digit ('{shown}', the GDT holds '{raw}')",
                shown == raw && damage.DisplayText == raw);
            tab.UndoCommand.Execute(null);

            // ── U10: one value width for every single-line editor kind ──
            tab.SearchText = "";
            tab.View = EditorView.All;
            Pump(50);
            window.UpdateLayout();
            var widths = Views().Where(v => v.DataContext is not LinesPropertyViewModel)
                .Select(v => (Kind: v.DataContext!.GetType().Name, Width: v.FindControl<Grid>("Root")!.ColumnDefinitions[0].ActualWidth))
                .Where(w => w.Width > 0).ToList();
            var kinds = widths.Select(w => w.Kind).Distinct().ToList();
            Check($"review U10: every editor kind in view gets one value width ({string.Join(", ", widths.GroupBy(w => w.Kind).Select(g => $"{g.Key[..^"PropertyViewModel".Length]} {g.Max(w => w.Width):0}"))})",
                kinds.Count >= 3 && widths.Max(w => w.Width) - widths.Min(w => w.Width) < 0.5
                && Math.Abs(widths[0].Width - PropertyItemViewModel.ValueWidth) < 1.5);

            // ── E7: one label column, fitted to the asset's labels and clamped ──
            var labelCells = window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Name == "PropLabel" && t.IsEffectivelyVisible)
                .Select(t => t.FindAncestorOfType<DockPanel>()!).Where(d => d.Classes.Contains("pcell")).ToList();
            var labelWidths = labelCells.Select(d => Math.Round(d.Bounds.Width)).Distinct().ToList();
            Check($"review E7: the label column is one width down the form ({string.Join(", ", labelWidths)} px; fitted {editor.LabelWidth:0}, clamp {AssetEditorView.LabelMin:0}–{AssetEditorView.LabelMax:0})",
                labelCells.Count > 5 && labelWidths.Count == 1 && labelWidths[0] >= AssetEditorView.LabelMin && labelWidths[0] <= AssetEditorView.LabelMax);
            var wide = window.Width;
            window.Width = 1000;
            Pump(50);
            window.UpdateLayout();
            Pump(50);
            var narrowLabel = editor.LabelWidth;
            var formWidth = editor.FindControl<ItemsControl>("Form")!.Bounds.Width;
            Check($"review E7: in a narrow pane the labels give way first ({narrowLabel:0} px of a {formWidth:0} px form)",
                narrowLabel <= formWidth * 0.45 && narrowLabel >= 96);
            window.Width = wide;
            Pump(50);
            window.UpdateLayout();

            // ── E8: section headers name their section; a count, if any, is faint and after the name ──
            var headers = window.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("psection") && b.IsEffectivelyVisible).ToList();
            var loud = headers.SelectMany(h => h.GetVisualDescendants().OfType<TextBlock>())
                .Where(t => t.IsEffectivelyVisible && t.Text is { Length: > 0 } x && x.All(char.IsDigit) && !t.Classes.Contains("faint"))
                .Select(t => t.Text).ToList();
            Check($"review E8: section headers name their section, any count faint ({headers.Count} headers, loud counts: {string.Join(", ", loud)})",
                headers.Count > 1 && loud.Count == 0);

            // ── E9: the rail names a trimmed section in its tooltip and marks problems with ⚠ ──
            var rail = window.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("railitem") && b.IsEffectivelyVisible).ToList();
            var handling = rail.FirstOrDefault(b => (b.DataContext as CategoryViewModel)?.Name == "Handling");
            var tip = handling is null ? null : ToolTip.GetTip(handling) as string;
            var warn = handling?.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Ellipse>().FirstOrDefault(e => Grid.GetColumn(e) == 1);
            Check($"review E9: a rail entry's tooltip names it and its problems ('{tip?.Replace("\n", " / ")}'), its dot shown",
                tip is not null && tip.StartsWith("Handling") && tip.Contains("problem") && warn is { IsEffectivelyVisible: true });
            var clean = rail.FirstOrDefault(b => (b.DataContext as CategoryViewModel) is { HasProblems: false });
            var cleanWarn = clean?.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Ellipse>().FirstOrDefault(e => Grid.GetColumn(e) == 1);
            var nameBox = clean?.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Text == (clean.DataContext as CategoryViewModel)?.Name);
            var markedName = handling?.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Text == "Handling");
            Check($"review E9: the ⚠ has its own slot: names end at one edge with or without it ({nameBox?.Bounds.Width:0} / {markedName?.Bounds.Width:0} px)",
                cleanWarn is { IsVisible: false } && nameBox is not null && markedName is not null && Math.Abs(nameBox.Bounds.Width - markedName.Bounds.Width) < 0.5);

            // ── E13: the Inspector says the problems in one line, and the focused row's own problem ──
            var inspector = window.GetVisualDescendants().OfType<InspectorView>().First();
            var summary = inspector.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.IsEffectivelyVisible && t.Text == tab.ProblemSummary);
            var goTos = inspector.GetVisualDescendants().OfType<Button>().Count(b => b.IsEffectivelyVisible && b.Content as string == "Go to field");
            Check($"review E13: the Inspector says '{tab.ProblemSummary}' in one line, no card per problem ({goTos} cards)",
                summary is not null && goTos == 0 && tab.ProblemCount >= 2);
            var hip = tab.AllSentinel.All.First(p => p.Key == "hipSpread");
            tab.RevealProperty(hip.Key);
            Pump(50);
            window.UpdateLayout();
            var own = inspector.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.IsEffectivelyVisible && t.Text == hip.Problem);
            Check($"review E13: the focused row's problem shows with its details ('{hip.Problem}')", own is not null && vm.InspectorProperty == hip);
            var showThem = inspector.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.IsEffectivelyVisible && b.Content as string == "Show them");
            if (showThem is not null)
                Click(window, showThem);
            Check($"review E13: a real click on Show them lists only the problems ({tab.View})", showThem is not null && tab.View == EditorView.Problems);
            Capture(window, Path.Combine(outDir, "97-review-problems.png"));
            CaptureLight(window, Path.Combine(outDir, "97-review-problems-light.png"));
            tab.View = EditorView.All;
            Pump(50);

            // ── E3: Go to field clears only what hides the row ──
            tab.SearchText = "spread";
            Pump(200);
            var revealed = tab.RevealProperty("hipSpread");
            Check($"review E3: going to a row the filter shows keeps the filter ('{tab.SearchText}')", revealed == hip && tab.SearchText == "spread");
            tab.View = EditorView.Changed;
            Pump(50);
            tab.RevealProperty("hipSpread");
            Check($"review E3: a row the view hides brings the view back to All, the filter kept ({tab.View}, '{tab.SearchText}')",
                tab.View == EditorView.All && tab.SearchText == "spread" && tab.IsShown(hip));
            tab.SearchText = "";
            Pump(50);

            // ── E10: a problem appearing never narrows the field being typed in ──
            var clip = (NumberPropertyViewModel)tab.AllSentinel.All.First(p => p.Key == "clipSize");
            var numberBox = FocusNumber(window, vm, "clipSize");
            var before = numberBox.Bounds;
            Check("review E10: the field starts without a problem", !clip.HasProblem);
            Key(window, K.Enter);
            window.KeyTextInput("999");
            Key(window, K.Enter);
            window.UpdateLayout();
            Pump();
            var after = numberBox.Bounds;
            var view = numberBox.FindAncestorOfType<PropertyEditorView>();
            var mark = view?.GetVisualDescendants().OfType<GlyphIcon>().FirstOrDefault(g => g.Glyph == "warning");
            Check($"review E10: typing an out-of-range value shows ⚠ and the field keeps its bounds ({before.Width:0} → {after.Width:0} px, {clip.RawValue})",
                clip.HasProblem && mark is { IsEffectivelyVisible: true } && Math.Abs(before.Width - after.Width) < 0.5 && Math.Abs(before.X - after.X) < 0.5);
            Key(window, K.Z, RawInputModifiers.Control);

            // ── E14: an empty value shows what it means ──
            var ammo = tab.AllSentinel.All.First(p => p.Key == "ammoName");
            ammo.RawValue = "";
            tab.RevealProperty(ammo.Key);
            Pump(50);
            window.UpdateLayout();
            var ammoBox = window.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(t => t.DataContext == ammo && t.Classes.Contains("pfield"));
            Check($"review E14: an empty value shows its default as the placeholder ('{ammoBox?.PlaceholderText}')",
                ammoBox is { Text: "" } && ammoBox.PlaceholderText == ammo.Def.Default && ammo.Def.Default.Length > 0);
            tab.UndoCommand.Execute(null);

            // ── E4: one problem rule: each open tab's count is the count the status bar and the Explorer use ──
            var mismatched = new List<string>();
            foreach (var name in new[] { "wpn_smg_wasp", "wpn_pst_talon", "wpn_snp_locus_zm", "wpn_ar_havoc_zm_upgraded", "wpn_ar_havoc" })
            {
                vm.OpenByName(name);
                Pump();
                var open = vm.ActiveTab!;
                if (open.ProblemCount != vm.ProblemsOf(open.Record))
                    mismatched.Add($"{name}: tab {open.ProblemCount}, total {vm.ProblemsOf(open.Record)}");
            }
            Check($"review E4: an open asset's tab, the Explorer and the status bar count its problems alike ({string.Join("; ", mismatched)})",
                mismatched.Count == 0);
        }
        finally
        {
            window.Close();
        }
    }

    // ═══ The real deffiles, on a temp install ═════════════════════════════════

    private static void ReviewLiveChecks(string outDir)
    {
        if (!Directory.Exists(Path.Combine(InstallRoot, "deffiles")))
        {
            Console.WriteLine("info  review (live): no BO3 install, skipped");
            return;
        }
        var install = NewScratch("review-install");
        CopyTree(Path.Combine(InstallRoot, "deffiles"), Path.Combine(install, "deffiles"));
        Directory.CreateDirectory(Path.Combine(install, "source_data"));
        foreach (var rel in new[] { @"source_data\ar_ak47_h1.gdt", @"source_data\mwr_ch_model_files.gdt" })
            File.Copy(Path.Combine(InstallRoot, rel), Path.Combine(install, rel));
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
            window.Width = 1600;
            window.Height = 1000;
            WaitUntil(() => vm.Status.StartsWith("Loaded"), 60_000);
            Pump(100);
            if (vm.IsAlertOpen)
                vm.DismissAlertCommand.Execute(null);

            // ── E2: labels as APE shows them, minus its colons, padding and markup ──
            var bad = SchemaRegistry.TypeNames.Select(SchemaRegistry.Get).OfType<AssetSchema>()
                .SelectMany(s => s.Properties.Select(p => (s.TypeName, p)))
                .Where(x => x.p.Label.EndsWith(':') || x.p.Label.Contains('<') || x.p.Label != x.p.Label.Trim() || x.p.Label.Length == 0
                            || x.p.Category.EndsWith(':') || x.p.Category != x.p.Category.Trim())
                .Select(x => $"{x.TypeName}.{x.p.Key} '{x.p.Label}'").ToList();
            Check($"review E2: no deffile label or section keeps a colon, padding or markup ({bad.Count}: {string.Join(", ", bad.Take(4))})", bad.Count == 0);
            Check($"review E2: a title's markup goes and its words stay ('{GdfRuntime.CleanTitle("Turn Rate <small>(degrees / sec)</small>")}', '{GdfRuntime.CleanTitle("Script Type:")}', {GdfRuntime.CleanTitle(" ") ?? "null"})",
                GdfRuntime.CleanTitle("Turn Rate <small>(degrees / sec)</small>") == "Turn Rate (degrees / sec)"
                && GdfRuntime.CleanTitle("Script Type:") == "Script Type" && GdfRuntime.CleanTitle(" ") is null);

            // ── U9 + E1: a diffuse map's compression is checked against the options its deffile run offers ──
            const string image = "i_h1_street_light_01_e";
            var record = DatabaseOf(vm).Assets.First(a => a.Name == image);
            var staticChoices = SchemaRegistry.Get("image")!.Find("compressionMethod")!.Choices;
            vm.OpenByName(image);
            Pump(100);
            var tab = vm.ActiveTab!;
            var compression = (ChoicePropertyViewModel)tab.AllSentinel.All.First(p => p.Key == "compressionMethod");
            Check($"review U9: '{compression.RawValue}' on a diffuse map is no problem (the schema's own list: {string.Join(", ", staticChoices)}; this asset's: {string.Join(", ", compression.Options)})",
                compression.RawValue == "compressed high color" && !staticChoices.Contains("compressed high color")
                && compression.Options.Contains("compressed high color") && !compression.HasProblem
                && !tab.Problems.Any(p => p.Key == "compressionMethod"));
            WaitUntil(() => Apex.Editor.Services.FieldFiles.ProbesIdle, 5_000);
            Pump(100);
            Check($"review U9: the count the status bar uses agrees ({vm.ProblemsOf(record)} vs the tab's {tab.ProblemCount}: {string.Join("; ", tab.Problems.Select(p => $"{p.Key}: {p.Message}"))})",
                vm.ProblemsOf(record) == tab.ProblemCount && !tab.Problems.Any(p => p.Key == "compressionMethod"));
            Check($"review E1: the dropdown lists APE's option names and shows the value by its name ('{compression.DisplayValue}'; {string.Join(", ", compression.Items.Select(i => i.Label))})",
                compression.HasLabels && compression.DisplayValue == "Best color compression"
                && compression.Items.Any(i => i is { Value: "compressed low color", Label: "Better alpha compression" }));
            tab.RevealProperty("compressionMethod");
            Pump(50);
            window.UpdateLayout();
            var combo = window.GetVisualDescendants().OfType<ChoiceBox>().FirstOrDefault(c => c.DataContext == compression && c.IsEffectivelyVisible);
            Check($"review E1: the field itself reads 'Best color compression' ({(combo?.SelectedItem as ChoiceItem)?.Label})",
                combo?.SelectedItem is ChoiceItem { Label: "Best color compression", Value: "compressed high color" });
            Capture(window, Path.Combine(outDir, "97-review-image.png"));
            CaptureLight(window, Path.Combine(outDir, "97-review-image-light.png"));
            // Switching the image to a normal map takes the sRGB list away: then the same value is a problem, said once.
            var semantic = tab.AllSentinel.All.First(p => p.Key == "semantic");
            semantic.RawValue = "normalMap";
            Pump(50);
            Check($"review U9: a normal map's list lacks it, so it becomes a problem, checked again as the options change ({compression.Problem ?? "no problem"})",
                compression.HasProblem && tab.Problems.Any(p => p.Key == "compressionMethod") && vm.ProblemsOf(record) == tab.ProblemCount);
            tab.UndoCommand.Execute(null);
            Pump(50);
            Check("review U9: Ctrl+Z (the diffuse map again) takes the problem away", !compression.HasProblem && vm.ProblemsOf(record) == tab.ProblemCount);

            // ── The AK: hidden rows, the rail, vectors, a disabled row ──
            var ak = DatabaseOf(vm).Assets.First(a => a.Type == "bulletweapon" && a.Parent is null && a.Name.Contains("ak47", StringComparison.OrdinalIgnoreCase));
            vm.OpenAsset(ak);
            Pump(200);
            tab = vm.ActiveTab!;

            // E3: a property the deffile hides for this asset counts no problem, whatever it holds.
            var hidden = tab.AllSentinel.All.OfType<NumberPropertyViewModel>().FirstOrDefault(p => p.IsRuleHidden && p.Def.HasRange && p.Def.Extension.Length == 0);
            if (hidden is not null)
            {
                var problems = tab.ProblemCount;
                hidden.RawValue = (hidden.Def.Max + 1000).ToString(System.Globalization.CultureInfo.InvariantCulture);
                Pump();
                Check($"review E3: a value the deffile hides ({hidden.Key} = {hidden.RawValue}) is checked but counts no problem ({tab.ProblemCount} vs {problems})",
                    hidden.HasProblem && tab.ProblemCount == problems && !tab.Problems.Any(p => p.Key == hidden.Key)
                    && vm.ProblemsOf(ak) == tab.ProblemCount);
                tab.UndoCommand.Execute(null);
            }
            else
                Check("review E3: the AK has a hidden number to try", false);

            // U8: the rail lists exactly the sections something shows in.
            var empty = tab.RailItems.Where(c => c.All.All(p => p.IsRuleHidden)
                && !tab.ButtonRows.Any(b => b.IsShown && b.Category == c.Name)).Select(c => c.Name).ToList();
            var sections = tab.FlatRows.OfType<CategoryViewModel>().Count();
            Check($"review U8: the rail lists only sections the deffile shows something in ({tab.RailItems.Count} listed; {empty.Count} empty: {string.Join(", ", empty)})",
                empty.Count == 0 && tab.RailItems.Count == sections);

            // E6: a vector is one row, a box per component.
            var vector = tab.FlatRows.OfType<VectorRowViewModel>().FirstOrDefault(v => v.Parts.Count >= 2 && !v.IsRuleDisabled);
            Check($"review E6: a vector is one row ({vector?.Label}: {string.Join(", ", vector?.Parts.Select(p => p.Def.VectorPart) ?? [])}), its components never rows of their own",
                vector is not null && !tab.FlatRows.OfType<PropertyItemViewModel>().Any(p => p.Def.VectorKey is not null));
            if (vector is not null)
            {
                tab.RevealProperty(vector.Parts[0].Key);
                Pump(50);
                window.UpdateLayout();
                Pump(50);
                var boxes = window.GetVisualDescendants().OfType<ScrubNumberBox>()
                    .Where(b => b.IsEffectivelyVisible && vector.Parts.Contains(b.DataContext)).ToList();
                var tops = boxes.Select(b => Math.Round(BoundsIn(b, window).Top)).Distinct().Count();
                Check($"review E6: its {boxes.Count} boxes sit side by side on one line ({tops} line)", boxes.Count == vector.Parts.Count && tops == 1);
                Check($"review E6: revealing a component puts the keyboard in its box, the Inspector on it ({vm.InspectorProperty?.Key})",
                    vm.InspectorProperty == vector.Parts[0] && vector.IsFocused);
                Key(window, K.Tab);
                Check($"review E6: Tab walks to the next component's box ({vm.InspectorProperty?.Key})", vm.InspectorProperty == vector.Parts[1]);
                var index = tab.FlatRows.IndexOf(vector);
                Key(window, K.Down);
                var next = vm.InspectorProperty is { } focused ? tab.RowFor(focused) : null;
                Check($"review E6: ↓ leaves the vector for the next row ({vm.InspectorProperty?.Key})",
                    next is not null && next != vector && tab.FlatRows.IndexOf(next) > index);
                Capture(window, Path.Combine(outDir, "97-review-vector.png"));
                CaptureLight(window, Path.Combine(outDir, "97-review-vector-light.png"));
            }

            // E17: a disabled row names the field that enables it.
            var explosion = tab.AllSentinel.All.FirstOrDefault(p => p.Key == "explosionRadius");
            var flag = tab.AllSentinel.All.FirstOrDefault(p => p.Key == "bulletImpactExplode");
            if (explosion is { IsRuleDisabled: true } && flag is not null)
            {
                // Nothing is searched until someone asks: the row's tip opening is what starts it.
                Check($"review E17: no search runs before the tip opens ({explosion.EnabledBy ?? "none"})", explosion.EnabledBy is null);
                tab.RevealProperty(explosion.Key);
                Pump();
                window.UpdateLayout();
                var mark = window.GetVisualDescendants().OfType<GlyphIcon>().FirstOrDefault(g => g.Glyph == "⊘" && ReferenceEquals(g.DataContext, explosion) && g.IsEffectivelyVisible);
                var sw = Stopwatch.StartNew();
                if (mark is not null)
                    ToolTip.SetIsOpen(mark, true);
                WaitUntil(() => explosion.EnabledBy is not null || sw.ElapsedMilliseconds > 30_000, 31_000);
                if (mark is not null)
                    ToolTip.SetIsOpen(mark, false);
                Check($"review E17: opening a disabled row's tip finds what enables it ('{explosion.DisabledTip}', {sw.ElapsedMilliseconds} ms off the UI thread; mark found: {mark is not null})",
                    explosion.DisabledTip?.Contains(flag.Label) == true && explosion.EnabledBy is not null);
            }
            else
                Check($"review E17: the AK's explosion radius is disabled ({explosion?.IsRuleDisabled})", false);
        }
        finally
        {
            window?.Close();
            Environment.SetEnvironmentVariable("APEX_FORCE_MOCK", saved.Mock);
            Environment.SetEnvironmentVariable("APEX_BO3_ROOT", saved.Root);
            Environment.SetEnvironmentVariable("APEX_NO_PERSIST", saved.Persist);
            SchemaRegistry.ResetToMock();
            try { Directory.Delete(install, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
