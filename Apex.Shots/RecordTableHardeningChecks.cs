using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
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

namespace Apex.Shots;

/// <summary>
/// The record table at the window widths people use (1100 to 1600 logical pixels: 1920 wide screens at 100 to 175 %,
/// 2560 at 150 to 200 %): every cell and the row controls inside the editor, nothing clipped, and each cell named for a
/// screen reader with its column and row. A temp copy of one install GDT, the fixture weapon-tech manifest.
/// </summary>
public partial class Program
{
    private static IEnumerable<ContentControl> Cells(Visual row) =>
        row.GetVisualDescendants().OfType<ContentControl>().Where(c => c.Classes.Contains("rcell"));

    private static Control? Editor(ContentControl cell) =>
        cell.GetVisualDescendants().OfType<Control>().FirstOrDefault(x => x is ScrubNumberBox or TextBox or ComboBox or ToggleButton && x.Focusable);

    /// <summary>
    /// A switch focused from the keyboard, in a table cell and in the form: its ring (2 px round the track) is drawn, lies
    /// inside everything that clips it and clear of On/Off, and no second, square focus visual covers the switch.
    /// </summary>
    private static void SwitchFocusRingCheck(Window window, RecordTableEditor table, string theme)
    {
        var row = table.GetVisualDescendants().OfType<Grid>().First(g => g.Classes.Contains("rrow") && g.IsEffectivelyVisible);
        var inCell = Cells(row).Select(Editor).OfType<ToggleButton>().First();
        var inForm = window.GetVisualDescendants().OfType<ToggleButton>()
            .First(t => t.Classes.Contains("miniswitch") && t.IsEffectivelyVisible && t.FindAncestorOfType<RecordTableEditor>() is null
                && t.DataContext is PropertyItemViewModel);
        foreach (var (where, toggle) in new[] { ("table cell", inCell), ("form row", inForm) })
        {
            toggle.Focus(NavigationMethod.Tab);
            Pump(50);
            window.UpdateLayout();
            var track = toggle.GetVisualDescendants().OfType<Border>().First(b => b.Name == "Track");
            var label = toggle.GetVisualDescendants().OfType<TextBlock>().First(t => t.IsEffectivelyVisible);
            Rect In(Visual v, Visual to) => new(v.TranslatePoint(default, to)!.Value, v.Bounds.Size);
            var problems = new List<string>();
            foreach (var clip in track.GetVisualAncestors().OfType<Control>().Where(c => c.ClipToBounds))
            {
                var ring = In(track, clip).Inflate(2);
                if (!new Rect(clip.Bounds.Size).Contains(ring))
                    problems.Add($"ring {ring} cut by {clip.GetType().Name} {clip.Bounds.Size}");
            }
            if (In(track, toggle).Inflate(2).Intersects(In(label, toggle)))
                problems.Add($"ring overlaps '{label.Text}'");
            if (AdornerLayer.GetAdornerLayer(toggle)?.Children.Any(a => AdornerLayer.GetAdornedElement(a) == toggle) == true)
                problems.Add("a square focus adorner covers it");
            Check($"focus ring ({theme}): a focused switch in a {where} rings its track, uncut and clear of '{label.Text}'{(problems.Count > 0 ? " (" + string.Join("; ", problems) + ")" : "")}",
                toggle.IsFocused && track.BoxShadow.Count > 0 && problems.Count == 0);
        }
        inCell.Focus(NavigationMethod.Tab);
        Pump(50);
    }

    private static void RecordTableWidthChecks(string outDir)
    {
        var install = NewScratch("rec-width-install");
        CopyTree(Path.Combine(InstallRoot, "deffiles"), Path.Combine(install, "deffiles"));
        var weaponRel = @"source_data\ar_ak47_h1.gdt";
        Directory.CreateDirectory(Path.Combine(install, "source_data"));
        File.Copy(Path.Combine(InstallRoot, weaponRel), Path.Combine(install, weaponRel));
        var weaponPath = Path.Combine(install, weaponRel);
        var weaponName = LoadGdt(weaponPath).Assets.First(a => a.Parent is null && a.Type == "bulletweapon").Name;
        var values = new List<(string, string)> { ("wtEnabled", "1") };
        values.AddRange(Enumerable.Range(1, 3).Select(i => ($"wtKick{i}", KickRow(i))));
        File.WriteAllBytes(ExtensionSidecar.PathFor(weaponPath), GdtEncoding.GetBytes("{\r\n" + Block(weaponName, values) + "}\r\n"));

        var saved = (Mock: Environment.GetEnvironmentVariable("APEX_FORCE_MOCK"), Root: Environment.GetEnvironmentVariable("APEX_BO3_ROOT"),
            Persist: Environment.GetEnvironmentVariable("APEX_NO_PERSIST"));
        Environment.SetEnvironmentVariable("APEX_FORCE_MOCK", null);
        Environment.SetEnvironmentVariable("APEX_BO3_ROOT", install);
        Environment.SetEnvironmentVariable("APEX_NO_PERSIST", "1");
        Environment.SetEnvironmentVariable(ExtensionRegistry.DirVariable, PlainTablesDir("rec-width-ui"));
        MainViewModel? vm = null;
        MainWindow? window = null;
        try
        {
            vm = new MainViewModel(Path.Combine(install, "session"));
            window = ShowJournalWindow(vm);
            WaitUntil(() => vm.Status.StartsWith("Loaded"), 60_000);
            Pump(100);
            if (vm.IsAlertOpen)
                vm.DismissAlertCommand.Execute(null);
            vm.OpenByName(weaponName);
            Pump(300);
            var tab = vm.ActiveTab!;
            var kicks = (RecordsPropertyViewModel)tab.AllSentinel.All.First(p => p.Key == "wtKick#");
            double Right(Visual v) => v.TranslatePoint(new Point(v.Bounds.Width, 0), window)!.Value.X;

            foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
            {
                Application.Current!.RequestedThemeVariant = theme;
                foreach (var (w, h) in new[] { (1600, 1000), (1440, 900), (1280, 800), (1100, 700) })
                {
                    window.Width = w;
                    window.Height = h;
                    Pump(100);
                    tab.RevealProperty("wtKick#");
                    Pump(50);
                    window.UpdateLayout();
                    Pump(50);
                    var table = window.GetVisualDescendants().OfType<RecordTableEditor>().First(t => t.DataContext == kicks && t.IsEffectivelyVisible);
                    var editor = window.GetVisualDescendants().OfType<AssetEditorView>().First(v => v.IsEffectivelyVisible);
                    var editorRight = Right(editor);
                    var problems = new List<string>();
                    foreach (var row in table.GetVisualDescendants().OfType<Grid>().Where(g => g.Classes.Contains("rrow") && g.IsEffectivelyVisible))
                    {
                        foreach (var b in row.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("raction")))
                            if (Right(b) > editorRight + 0.5)
                                problems.Add($"{b.Content} at {Right(b):0} past {editorRight:0}");
                        // Every cell's editor inside its cell, and a switch shown whole: a cell may scroll out of sight, never shrink.
                        foreach (var cell in Cells(row))
                        {
                            var content = cell.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c is ScrubNumberBox or TextBox or ComboBox or ToggleButton);
                            if (content is null)
                                continue;
                            if (Right(content) > Right(cell) + 0.5)
                                problems.Add($"cell {Grid.GetColumn(cell)} spills {Right(content) - Right(cell):0} px");
                            if (content is ToggleButton t && t.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(x => x.IsEffectivelyVisible) is { } text
                                && text.DesiredSize.Width > text.Bounds.Width + 0.5)
                                problems.Add($"switch text clipped ('{text.Text}' {text.Bounds.Width:0} of {text.DesiredSize.Width:0})");
                        }
                    }
                    var name = theme == ThemeVariant.Dark ? "dark" : "light";
                    var bar = table.GetVisualDescendants().OfType<ScrollBar>().First(b => b.Name == "HScroll");
                    if (w == 1600)
                        SwitchFocusRingCheck(window, table, name);
                    var lastCell = Cells(table.GetVisualDescendants().OfType<Grid>().First(g => g.Classes.Contains("rrow") && g.IsEffectivelyVisible)).Last();
                    var clip = lastCell.FindAncestorOfType<Canvas>()!;
                    Capture(window, Path.Combine(outDir, $"95-record-table-{w}-{name}.png"));
                    Check($"record table at {w} px ({name}): every row control and cell inside the editor, no switch clipped, the cells scroll when they don't fit (table {table.Bounds.Width:0} px, cells {((Grid)lastCell.Parent!).Bounds.Width:0} in {clip.Bounds.Width:0}, scroll bar {(bar.IsVisible ? "shown" : "hidden")};{string.Join("; ", problems.Distinct())})",
                        problems.Count == 0 && bar.IsVisible == (((Grid)lastCell.Parent!).Bounds.Width > clip.Bounds.Width + 0.5));

                    if (w == 1100 && theme == ThemeVariant.Dark)
                    {
                        // The bar is there to be dragged: its thumb at least the 24 px a hit area needs.
                        var thumb = bar.GetVisualDescendants().OfType<Thumb>().FirstOrDefault(t => t.IsEffectivelyVisible);
                        var thumbWidth = thumb?.Bounds.Width ?? 0;
                        var restore = bar.Value;
                        bar.Value = bar.Maximum;
                        Pump(50);
                        window.UpdateLayout();
                        var track = thumb?.Parent as Visual;
                        var thumbEnd = thumb is null || track is null ? double.NaN : thumb.TranslatePoint(new Point(thumb.Bounds.Width, 0), track)!.Value.X;
                        Check($"record table at {w} px: the sideways bar's thumb can be grabbed and reaches the end ({thumbWidth:0} px of a {bar.Bounds.Width:0} px bar; at the end, its right edge {thumbEnd:0} of {track?.Bounds.Width ?? 0:0})",
                            thumbWidth >= 24 && track is not null && Math.Abs(thumbEnd - track.Bounds.Width) <= 1);
                        bar.Value = restore;
                        Pump(50);

                        // The keyboard reaches what doesn't fit: Tab into the last cell scrolls it into sight.
                        Editor(lastCell)!.Focus(NavigationMethod.Tab);
                        Pump(50);
                        window.UpdateLayout();
                        var inSight = lastCell.TranslatePoint(new Point(0, 0), clip)!.Value.X;
                        Check($"record table at {w} px: focusing a cell out of sight scrolls it in (its left edge {inSight:0} in a {clip.Bounds.Width:0} px window)",
                            bar.IsVisible && inSight >= -0.5 && inSight + lastCell.Bounds.Width <= clip.Bounds.Width + 0.5 && bar.Value > 0);
                        Capture(window, Path.Combine(outDir, $"95-record-table-{w}-scrolled-dark.png"));
                        var at = bar.Value;
                        var point = table.TranslatePoint(new Point(table.Bounds.Width / 2, 40), window)!.Value;
                        window.MouseWheel(point, new Vector(0, 1), RawInputModifiers.Shift);
                        Pump(50);
                        Check($"record table at {w} px: Shift+wheel scrolls the cells ({at:0} → {bar.Value:0})", bar.Value < at);
                        Editor(Cells(table.GetVisualDescendants().OfType<Grid>().First(g => g.Classes.Contains("rrow") && g.IsEffectivelyVisible)).First())!.Focus(NavigationMethod.Tab);
                        Pump(50);
                    }
                }
            }
            Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;

            // Names: a cell says its column and its row.
            tab.RevealProperty("wtKick#");
            Pump(100);
            var second = window.GetVisualDescendants().OfType<RecordTableEditor>().First(t => t.DataContext == kicks && t.IsEffectivelyVisible)
                .GetVisualDescendants().OfType<Grid>().Where(g => g.Classes.Contains("rrow") && g.IsEffectivelyVisible).ElementAt(1);
            var names = Cells(second).Select(Editor).Where(c => c is not null).Select(c => AutomationProperties.GetName(c!)).ToList();
            var labels = kicks.List.DisplayColumns.Select(c => c.Def.Label).ToList();
            Check($"record table: each cell is named for its column and row ({string.Join(", ", names)})",
                names.Count == labels.Count && names.Zip(labels).All(p => p.First == $"{p.Second} row 2"));
            // A number cell is in the automation tree as an editable field with that name and its value, and a value set
            // through it lands in the row as typing would (and undoes the same way).
            var number = Cells(second).Select(Editor).OfType<ScrubNumberBox>().First();
            var peer = ControlAutomationPeer.CreatePeerForElement(number);
            var field = peer.GetProvider<IValueProvider>();
            var before = field?.Value;
            field?.SetValue("7.5");
            Pump(50);
            var after = ((NumberPropertyViewModel)number.DataContext!).RawValue;
            Check($"record table: a number cell is a named field to assistive tech, and a value set through it is the cell's ('{peer.GetName()}', {peer.GetAutomationControlType()}, '{before}' → '{after}')",
                peer.IsControlElement() && peer.GetName() == AutomationProperties.GetName(number) && peer.GetAutomationControlType() == AutomationControlType.Edit
                && before is not null && after == "7.5" && field!.Value == "7.5");
            vm.UndoActiveCommand.Execute(null);
            Pump(50);
            kicks.Move(kicks.Rows[1], -1);
            Pump(100);
            var moved = Cells(second).Select(Editor).First(c => c is not null);
            var top = window.GetVisualDescendants().OfType<Grid>().First(g => g.Classes.Contains("rrow") && g.IsEffectivelyVisible && g.DataContext == kicks.Rows[0]);
            var topName = AutomationProperties.GetName(Cells(top).Select(Editor).First(c => c is not null)!);
            Check($"record table: names follow rows as they move and are numbered again ('{topName}', '{AutomationProperties.GetName(moved!)}')",
                topName == $"{labels[0]} row 1" && AutomationProperties.GetName(moved!) == $"{labels[0]} row 2");
            vm.UndoActiveCommand.Execute(null);
        }
        catch (Exception ex)
        {
            Check($"record table widths: {ex}", false);
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
}
