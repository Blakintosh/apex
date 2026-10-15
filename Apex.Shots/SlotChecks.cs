using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Apex.Editor.Models;
using Apex.Editor.Services;
using Apex.Editor.ViewModels;
using Apex.Editor.Views;
using K = Avalonia.Input.Key;

namespace Apex.Shots;

/// <summary>
/// skinOverride's material slots, with real input in the real window: each surface of the model shows what draws there
/// (its swatch, its name, where it comes from) and a Replace menu; a click on the slot or Choose material… shows its
/// field with every material listed, Enter writes the pick and Esc puts it back. Reads a model from the install;
/// skipped without one.
/// </summary>
public partial class Program
{
    private static void SlotChecksInWindow(Window window, MainViewModel vm, string outDir)
    {
        var mock = Environment.GetEnvironmentVariable("APEX_FORCE_MOCK");
        Environment.SetEnvironmentVariable("APEX_FORCE_MOCK", null);
        var env = new Apex.Editor.Services.Gdt.GameEnvironment();
        Environment.SetEnvironmentVariable("APEX_FORCE_MOCK", mock);
        if (!env.IsAvailable || env.ModelExportDir is null || !Directory.Exists(env.ModelExportDir))
        {
            Console.WriteLine("slots: BO3 not found — skipped");
            return;
        }
        var rootBefore = FieldFiles.Root;
        FieldFiles.Root = env.Bo3Root;
        try
        {
            if (ModelWithSurfaces(env.ModelExportDir) is not var (file, _))
            {
                Console.WriteLine("slots: no model with two materials in model_export — skipped");
                return;
            }
            var mats = DatabaseOf(vm).Assets.Where(a => a.Type == "material").Select(a => a.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
            var record = new AssetRecord { Name = "slottest_model", Type = "xmodel", GdtName = "linetest.gdt" };
            record.Properties["filename"] = file.Replace(@"\", @"\\");
            record.Properties["skinOverride"] = "";
            vm.OpenAsset(record);
            Pump();
            var tab = vm.ActiveTab!;
            var skin = (LinesPropertyViewModel)tab.AllSentinel.All.First(p => p.Key == "skinOverride");
            for (var i = 0; i < 100 && skin.CanAdd; i++)
                Pump(30);
            var list = FieldView(window, tab, skin).GetVisualDescendants().OfType<LineListEditor>().First();
            Pump(50);
            Control Slot(int index) => list.GetVisualDescendants().OfType<Grid>()
                .Where(g => g.Classes.Contains("lslot") && g.IsEffectivelyVisible).ElementAt(index);
            Button Face(int index) => Slot(index).GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("lslotface"));
            Button Replace(int index) => Slot(index).GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("lreplace"));
            TextBox? Field(int index) => Slot(index).GetVisualDescendants().OfType<TextBox>().FirstOrDefault(b => b.IsEffectivelyVisible);

            Check($"slots (window): every surface of the model is a slot, no field showing, no column titles ({skin.Items.Count} slots)",
                skin.Items.Count >= 2 && Enumerable.Range(0, skin.Items.Count).All(i => Field(i) is null)
                && !list.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "Material in model" && t.IsEffectivelyVisible));
            var first = skin.Items[0];
            var surface = first.Surface;
            string[] Shown(int index) => Slot(index).GetVisualDescendants().OfType<TextBlock>()
                .Where(t => t.IsEffectivelyVisible && t.Classes.Any(c => c is "lslotname" or "lslotoriginal")).Select(t => t.Text ?? "").ToArray();
            Check($"slots (window): a slot names what draws, one line, with Replace ▾ beside it ({string.Join(" / ", Shown(0))})",
                Shown(0).SequenceEqual(new[] { surface }) && Replace(0).IsEffectivelyVisible && Replace(0).Flyout is MenuFlyout);
            Check("slots (window): a slot isn't a field: it doesn't fill under the pointer",
                Face(0).GetVisualDescendants().OfType<Avalonia.Controls.Presenters.ContentPresenter>().First().Background is null
                    or Avalonia.Media.ISolidColorBrush { Color.A: 0 });
            var scroll = list.FindControl<ScrollViewer>("ItemsScroll")!;
            Check($"slots (window): up to twenty slots show without scrolling ({skin.Items.Count} slots, {scroll.Extent.Height:0} of {scroll.Viewport.Height:0} px shown)",
                skin.Items.Count > 20 || scroll.Extent.Height <= scroll.Viewport.Height + 0.5);
            Capture(window, Path.Combine(outDir, "64-slots-rest.png"));

            // ── Replace ▾ › Choose material… shows its field, focused, every material listed; Enter writes the pick ──
            void Choose(int index)
            {
                Click(window, Replace(index));
                Pump(20);
                var flyout = (MenuFlyout)Replace(index).Flyout!;
                flyout.Items.OfType<MenuItem>().First(i => i.Header as string == "Choose material…").Command?.Execute(null);
                flyout.Hide();
                Pump(50);
            }
            Choose(0);
            var field = Field(0) as RefField;
            Check($"slots (window): Choose material… shows the slot's material field, focused, its list open ({field?.IsSuggesting})",
                first.IsChoosing && field is not null && field.IsKeyboardFocusWithin && field.IsSuggesting);
            Capture(window, Path.Combine(outDir, "65-slots-choosing.png"));
            field!.SelectAll();
            TypeText(window, mats[0]);
            Key(window, K.Escape); // closes the list; Enter then takes the typing
            Key(window, K.Enter);
            Pump(50);
            Check($"slots (window): Enter writes the material as the surface's override ('{skin.RawValue}')",
                skin.RawValue == $@"{surface} {mats[0]}\r\n" && record.Properties["skinOverride"] == skin.RawValue);
            Check($"slots (window): and the slot shows it, the model's material struck through under it, its Replace button focused ({string.Join(" / ", Shown(0))})",
                !first.IsChoosing && Field(0) is null && Shown(0).SequenceEqual(new[] { mats[0], surface })
                && Slot(0).GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == surface && t.TextDecorations == Avalonia.Media.TextDecorations.Strikethrough)
                && Slot(0).GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "Override" && t.IsEffectivelyVisible)
                && Replace(0).IsFocused);
            Capture(window, Path.Combine(outDir, "66-slots-overridden.png"));

            // ── A click on the slot opens what draws there ──
            var tabs = vm.OpenTabs.Count;
            Click(window, Face(0));
            Pump(50);
            Check($"slots (window): a click on the slot opens its material ({vm.ActiveTab?.Name}), choosing nothing",
                vm.ActiveTab?.Name == mats[0] && vm.OpenTabs.Count == tabs + 1 && !first.IsChoosing);
            vm.Registry[Apex.Editor.Commands.CommandCatalog.CloseTab].Execute();
            Pump();
            list = FieldView(window, tab, skin).GetVisualDescendants().OfType<LineListEditor>().First();
            Pump(50);

            // ── Esc puts the field back as it was ──
            Choose(0);
            Field(0)!.SelectAll();
            TypeText(window, "mtl_apex_not_kept");
            Key(window, K.Escape);
            Key(window, K.Escape);
            Pump(50);
            Check($"slots (window): Esc leaves the override as it was and shows the slot again ('{skin.RawValue}')",
                skin.RawValue == $@"{surface} {mats[0]}\r\n" && !first.IsChoosing && Field(0) is null);

            // ── Replace ▾: Hide, Use the model's material; Delete resets ──
            Click(window, Replace(1));
            Pump(20);
            var menu = (MenuFlyout)Replace(1).Flyout!;
            var items = menu.Items.OfType<MenuItem>().ToList();
            Check($"slots (window): Replace ▾ opens Choose, Hide, Use the model's, Open ({string.Join(", ", items.Select(i => i.Header))})",
                menu.IsOpen && items.Select(i => i.Header as string).SequenceEqual(new[] { "Choose material…", "Hide (nodraw)", "Use the model's material", "Open material" })
                && items[1].IsEffectivelyEnabled && !items[2].IsEffectivelyEnabled);
            items[1].Command?.Execute(null);
            menu.Hide();
            Pump(20);
            var second = skin.Items[1];
            Check($"slots (window): Hide writes nodraw and the slot shows nodraw over the model's material, hidden ({string.Join(" / ", Shown(1))})",
                skin.RawValue == $@"{surface} {mats[0]}\r\n{second.Surface} nodraw\r\n" && second.IsHidden
                && Shown(1).SequenceEqual(new[] { "nodraw", second.Surface })
                && Slot(1).GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "Hidden" && t.IsEffectivelyVisible));
            Capture(window, Path.Combine(outDir, "67-slots-hidden.png"));
            Replace(1).Focus(NavigationMethod.Directional);
            Key(window, K.Delete);
            Pump(20);
            Check($"slots (window): Delete on a slot resets it to the model's material ('{skin.RawValue}')",
                skin.RawValue == $@"{surface} {mats[0]}\r\n" && !second.IsOverridden);
            Key(window, K.Up);
            Check("slots (window): ↑ moves to the slot above", Replace(0).IsFocused);

            while (tab.CanUndo)
                tab.UndoCommand.Execute(null);
            Pump();
            Check($"slots (window): every edit undoes ('{skin.RawValue}')", skin.RawValue == "" && record.CountSessionChanges() == 0);
            vm.Registry[Apex.Editor.Commands.CommandCatalog.CloseTab].Execute();
            Pump();
        }
        finally
        {
            FieldFiles.Root = rootBefore;
        }
    }
}
