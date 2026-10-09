using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Apex.Editor.Controls;
using Apex.Editor.Models;
using Apex.Editor.ViewModels;
using Apex.Editor.Views;

namespace Apex.Shots;

/// <summary>
/// The xanim editor in the live app against a temp install: a synthetic anim (55 frames, two exported notetracks) on a
/// small rig copied from the install, and a GDT entry with notetrack actions. Real input on the viewport chips, the
/// notetracks dock and the properties panel; shots in both themes. Nothing here touches the BO3 install.
/// </summary>
public partial class Program
{
    private const int AnimFixtureFrames = 55;

    /// <summary>An xanim_export with three parts that stand still and two exported notetracks (frames 1 and 22).</summary>
    private static string AnimFixtureExport()
    {
        var sb = new StringBuilder();
        sb.Append("// Apex harness fixture\r\nANIMATION\r\nVERSION 3\r\n\r\nNUMPARTS 3\r\n");
        sb.Append("PART 0 \"tag_view\"\r\nPART 1 \"tag_ads\"\r\nPART 2 \"tag_torso\"\r\n\r\n");
        sb.Append(CultureInfo.InvariantCulture, $"FRAMERATE 30\r\nNUMFRAMES {AnimFixtureFrames}\r\n\r\n");
        for (var f = 0; f < AnimFixtureFrames; f++)
        {
            sb.Append(CultureInfo.InvariantCulture, $"FRAME {f}\r\n");
            for (var p = 0; p < 3; p++)
                sb.Append(CultureInfo.InvariantCulture, $"PART {p}\r\nOFFSET 0.000000, 0.000000, {p * 2}.000000\r\nSCALE 1.000000, 1.000000, 1.000000\r\n")
                  .Append("X 1.000000, 0.000000, 0.000000\r\nY 0.000000, 1.000000, 0.000000\r\nZ 0.000000, 0.000000, 1.000000\r\n\r\n");
        }
        sb.Append("NOTETRACKS\r\n\r\nPART 0\r\nNUMTRACKS 1\r\nNOTETRACK 0\r\nNUMKEYS 2\r\n");
        sb.Append("FRAME 1 \"sndnt#wpn_apex_raise\"\r\nFRAME 22 \"rmbnt#reload_small\"\r\n\r\n");
        sb.Append("PART 1\r\nNUMTRACKS 0\r\n\r\nPART 2\r\nNUMTRACKS 0\r\n");
        return sb.ToString();
    }

    private const string AnimFixtureGdt = "{\r\n"
        + "\t\"apex_raise_first\" ( \"xanim.gdf\" )\r\n\t{\r\n"
        + "\t\t\"filename\" \"apex_raise_first.xanim_export\"\r\n"
        + "\t\t\"model\" \"apex_rig.xmodel_export\"\r\n"
        + "\t\t\"type\" \"relative\"\r\n"
        + "\t\t\"customnote0action\" \"Sound\"\r\n\t\t\"customnote0actionparam1\" \"wpn_apex_fpo_charge\"\r\n\t\t\"customnote0frame\" \"3\"\r\n"
        + "\t\t\"customnote1action\" \"Rumble\"\r\n\t\t\"customnote1actionparam1\" \"reload_small\"\r\n\t\t\"customnote1frame\" \"41\"\r\n"
        + "\t\t\"customnote2action\" \"None\"\r\n\t\t\"customnote2frame\" \"1\"\r\n"
        + "\t}\r\n"
        + "\t\"apex_other\" ( \"xanim.gdf\" )\r\n\t{\r\n"
        + "\t\t\"filename\" \"apex_raise_first.xanim_export\"\r\n"
        + "\t\t\"model\" \"apex_rig.xmodel_export\"\r\n"
        + "\t}\r\n"
        + "}\r\n";

    private static void RunAnimLayoutChecks(string outDir)
    {
        var rig = Path.Combine(InstallRoot, "model_export", "tbd", "870mcs_animmodel.xmodel_export");
        if (!Directory.Exists(Path.Combine(InstallRoot, "deffiles")) || !File.Exists(rig))
        {
            Console.WriteLine("anim layout: BO3 not found — skipped");
            return;
        }
        var install = NewScratch("anim-install");
        CopyTree(Path.Combine(InstallRoot, "deffiles"), Path.Combine(install, "deffiles"));
        Directory.CreateDirectory(Path.Combine(install, "source_data"));
        Directory.CreateDirectory(Path.Combine(install, "xanim_export"));
        Directory.CreateDirectory(Path.Combine(install, "model_export"));
        File.WriteAllText(Path.Combine(install, "source_data", "apex_anims.gdt"), AnimFixtureGdt);
        File.WriteAllText(Path.Combine(install, "xanim_export", "apex_raise_first.xanim_export"), AnimFixtureExport());
        File.Copy(rig, Path.Combine(install, "model_export", "apex_rig.xmodel_export"));

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
            Check($"anim layout: Apex loaded the temp install live ('{vm.Status}')", vm.Status.StartsWith("Loaded") && !vm.IsMockData);

            var openMs = TimeOpen(window, vm, "apex_raise_first");
            var tab = vm.ActiveTab!;
            var anim = tab.PreviewPane?.Content as AnimPreviewViewModel;
            WaitUntil(() => anim is null || anim.LastFrame > 0 || anim.ShowError, 20_000);
            Settle(window);
            Check($"anim layout: the fixture anim loads ({anim?.LastFrame + 1} frames, {anim?.Timeline.Markers.Count} markers{(anim?.ShowError == true ? ", error " + anim.Error : "")})",
                anim is { LastFrame: AnimFixtureFrames - 1 } && anim.Timeline.Markers.Count == 4);
            if (anim is null)
                return;
            Console.WriteLine($"info  anim layout: the first xanim of the session opens in {openMs:0.0} ms on the UI thread ({_lastOpenModelMs:0.0} ms of it the tab, the rest its first layout)");
            AnimPlacementChecks(window, vm, tab, anim);
            Capture(window, Path.Combine(outDir, "80-xanim-dark.png"));
            Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
            Settle(window);
            Capture(window, Path.Combine(outDir, "81-xanim-light.png"));
            Avalonia.Application.Current.RequestedThemeVariant = ThemeVariant.Dark;
            Settle(window);

            AnimPanelChecks(window, vm, tab);
            AnimLaneChecks(window, vm, tab, anim, outDir);
            AnimTableChecks(window, vm, tab, anim, outDir);
            AnimDockSizeChecks(window, vm, tab, outDir);
            AnimPopOutChecks(window, vm, tab, anim, outDir);
            AnimDockedChecks(window, vm, tab, anim, outDir);
            AnimAutomationChecks(window);
            AnimBudgets(window, vm, tab, anim);
        }
        catch (Exception ex)
        {
            Check($"anim layout: {ex}", false);
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

    // ── Input helpers: real pointer events at points on the window ──────────

    private static void PointerAt(Window window, Point p)
    {
        // Hit testing reads the last rendered scene: render what layout just changed first.
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        window.MouseMove(p);
    }

    private static void PressAt(Window window, Point p)
    {
        PointerAt(window, p);
        window.MouseDown(p, MouseButton.Left);
    }

    private static void ReleaseAt(Window window, Point p)
    {
        window.MouseUp(p, MouseButton.Left);
        Settle(window);
    }

    private static void ClickPoint(Window window, Point p)
    {
        PressAt(window, p);
        ReleaseAt(window, p);
        System.Threading.Thread.Sleep(550); // past the double-click interval, so the next click is a click
    }

    private static void DoubleClickPoint(Window window, Point p)
    {
        PressAt(window, p);
        window.MouseUp(p, MouseButton.Left);
        window.MouseDown(p, MouseButton.Left);
        window.MouseUp(p, MouseButton.Left);
        Settle(window);
        System.Threading.Thread.Sleep(550);
    }

    private static T Find<T>(Visual root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().First(c => c.Name == name);

    private static NotetrackLanes LanesOf(Window window) =>
        window.GetVisualDescendants().OfType<NotetrackLanes>().First(l => l.IsEffectivelyVisible);

    /// <summary>A point on the lanes: <paramref name="lane"/> -1 is the ruler; <paramref name="dx"/> nudges into a pill.</summary>
    private static Point LanePoint(Window window, NotetrackLanes lanes, double frame, int lane, double dx = 0)
    {
        var x = lanes.XOf(frame) + dx;
        var y = lane < 0 ? 12 : lanes.LaneCenterY(lane);
        return lanes.TranslatePoint(new Point(x, y), window)!.Value;
    }

    /// <summary>A point inside a note's pill, <paramref name="dx"/> from its centre.</summary>
    private static Point MarkerPoint(Window window, NotetrackLanes lanes, string key, double dx = 0) =>
        lanes.TranslatePoint(lanes.CenterOf(key)!.Value + new Point(dx, 0), window)!.Value;

    private static void KeyWith(Window window, Key key, RawInputModifiers mods)
    {
        window.KeyPress(key, mods, PhysicalKey.None, null);
        window.KeyRelease(key, mods, PhysicalKey.None, null);
        Settle(window);
    }

    /// <summary>Whether the keyboard is in an editor of <paramref name="item"/>.</summary>
    private static bool FocusIn(Window window, PropertyItemViewModel? item) =>
        item is not null && window.FocusManager?.GetFocusedElement() is Visual focus
        && focus.GetSelfAndVisualAncestors().OfType<PropertyEditorView>().Any(v => ReferenceEquals(v.DataContext, item));

    private static string? Value(AssetEditorViewModel tab, string key) => tab.Record.Properties.GetValueOrDefault(key);

    private static double TimeOpen(Window window, MainViewModel vm, string name)
    {
        Settle(window);
        var sw = Stopwatch.StartNew();
        vm.OpenByName(name);
        _lastOpenModelMs = sw.Elapsed.TotalMilliseconds;
        Settle(window);
        return sw.Elapsed.TotalMilliseconds;
    }

    private static double _lastOpenModelMs;

    // ── Where things are: preview and dock in the editor, the short panel on the right ──

    private static void AnimPlacementChecks(Window window, MainViewModel vm, AssetEditorViewModel tab, AnimPreviewViewModel anim)
    {
        var editor = window.GetVisualDescendants().OfType<AssetEditorView>().First();
        var animArea = Find<Grid>(editor, "AnimArea");
        var formArea = Find<DockPanel>(editor, "FormArea");
        var centre = Find<PreviewPaneView>(editor, "CentrePreview");
        var dock = Find<NotetrackDockView>(editor, "AnimDock");
        Check($"xanim: the editor holds the preview and the notetracks dock, not the property form (form shown: {formArea.IsVisible})",
            animArea.IsEffectivelyVisible && !formArea.IsVisible && ReferenceEquals(centre.DataContext, tab.PreviewPane)
            && BoundsIn(dock, window).Top >= BoundsIn(centre, window).Bottom);

        var properties = window.FindControl<Border>("PropertiesLayer")!;
        var previewLayer = window.FindControl<Border>("PreviewLayer")!;
        var inspector = window.FindControl<Border>("InspectorLayer")!;
        // The hidden pane is never laid out, so its pane is found through the logical tree.
        var docked = Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(previewLayer).OfType<PreviewPaneView>().First();
        Check($"xanim: the right column is the properties panel alone; the docked preview lets go of the anim (docked shows {docked.DataContext?.GetType().Name ?? "nothing"})",
            properties.IsEffectivelyVisible && !previewLayer.IsVisible && !inspector.IsVisible && docked.DataContext is null && vm.DockedPreview is null
            && BoundsIn(properties, window).Left > BoundsIn(editor, window).Right);

        var popOut = Find<Button>(centre, "PopOutChip");
        var stats = Find<Border>(centre, "StatsChip");
        var view = Find<StackPanel>(centre, "ViewChips");
        var viewport = centre.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("viewport"));
        Check($"xanim: the viewport carries its chips: view and pop-out top right, stats bottom right ('{anim.Stats}')",
            popOut.IsEffectivelyVisible && stats.IsEffectivelyVisible
            && BoundsIn(view, window).Top - BoundsIn(viewport, window).Top < 20 && BoundsIn(viewport, window).Right - BoundsIn(view, window).Right < 20
            && BoundsIn(viewport, window).Bottom - BoundsIn(stats, window).Bottom < 20);
        var transport = centre.GetVisualDescendants().OfType<Slider>().FirstOrDefault(s => s.Name == "FrameSlider");
        Check("xanim: the preview in the editor has no transport of its own (the dock's is the one)", transport is null || !transport.IsEffectivelyVisible);
    }

    // ── The properties panel: four short cards, panel-only counts, real clicks and Ctrl+F ──

    private static void AnimPanelChecks(Window window, MainViewModel vm, AssetEditorViewModel tab)
    {
        var panel = window.FindControl<AnimPropertiesView>("AnimProperties")!;
        var names = string.Join(", ", tab.PanelCards.Select(c => c.Name));
        Check($"panel: the xanim's cards are the short sections ({names}), none of the notetrack sections",
            tab.PanelCards.Count >= 3 && tab.PanelCards.All(c => !AssetEditorViewModel.IsNotetrackSection(c.Name))
            && tab.PanelCards.Any(c => c.Name == "Source Files") && tab.PanelCards.Any(c => c.Name == "Settings"));
        Check($"panel: All counts the panel only ({tab.PanelShownCount} of the form's {tab.ShownPropertyCount})",
            tab.PanelShownCount is > 5 and < 40 && tab.ShownPropertyCount > 100);

        var preview = tab.PanelCards.FirstOrDefault(c => c.Name.StartsWith("AssetViewer", StringComparison.Ordinal));
        Check($"panel: AssetViewer Preview opens closed, saying '{preview?.Summary}'", preview is { IsExpanded: false, Summary: "Defaults" });
        if (preview is not null)
        {
            var header = panel.GetVisualDescendants().OfType<ToggleButton>().First(t => t.Classes.Contains("sectiontoggle") && t.DataContext == preview);
            Click(window, header);
            Settle(window);
            var opened = preview.IsExpanded && panel.GetVisualDescendants().OfType<Border>().Any(b => b.Classes.Contains("cardrow") && b.IsEffectivelyVisible
                && b.DataContext is PropertyItemViewModel p && p.Key == "previewModel");
            Click(window, header);
            Settle(window);
            Check($"panel: a click on a card's header opens it ({opened}) and closes it ({!preview.IsExpanded})", opened && !preview.IsExpanded);
        }

        // A switch, clicked: the value, the panel's Changed count, and the Changed view.
        var looping = panel.GetVisualDescendants().OfType<ToggleButton>()
            .First(t => t.Classes.Contains("miniswitch") && t.DataContext is PropertyItemViewModel { Key: "looping" });
        Click(window, looping);
        Settle(window);
        Check($"panel: clicking the Looping switch sets it (looping={Value(tab, "looping")}), and Changed counts it ({tab.PanelChangedCount})",
            Value(tab, "looping") == "1" && tab.PanelChangedCount == 1);
        var tabs = Find<StackPanel>(panel, "PanelTabs").Children.OfType<RadioButton>().ToList();
        Click(window, tabs[1]);
        Settle(window);
        var changedCards = string.Join(", ", tab.PanelCards.Select(c => $"{c.Name}: {c.VisibleRows.Count}"));
        Check($"panel: the Changed tab shows just that row ({changedCards})",
            tab.View == EditorView.Changed && tab.PanelCards.Count == 1 && tab.PanelCards[0].VisibleRows.Count == 1);
        Click(window, tabs[0]);
        tab.UndoCommand.Execute(null);
        Settle(window);

        // Ctrl+F: the panel's filter, typed into; Esc clears, Esc again folds it.
        Key(window, Avalonia.Input.Key.F, RawInputModifiers.Control);
        Pump(50);
        var filter = Find<TextBox>(panel, "PanelFilter");
        var focused = filter.IsFocused && filter.IsEffectivelyVisible;
        foreach (var ch in "node")
            TypeChar(window, ch);
        Pump(300);
        Settle(window);
        var filtered = string.Join(", ", tab.PanelCards.SelectMany(c => c.VisibleRows.OfType<PropertyItemViewModel>()).Select(p => p.Key));
        Check($"panel: Ctrl+F puts the keyboard in the panel's filter ({focused}); typing filters the cards ({filtered})",
            focused && tab.SearchText == "node" && filtered == "node");
        Key(window, Avalonia.Input.Key.Escape);
        Key(window, Avalonia.Input.Key.Escape);
        Settle(window);
        Check("panel: Esc clears the filter, Esc again folds it to ⌕", tab.SearchText == "" && !filter.IsVisible);
    }

    // ── The lanes: scrub anywhere, select, retime and add with real presses; keys ──

    private static void AnimLaneChecks(Window window, MainViewModel vm, AssetEditorViewModel tab, AnimPreviewViewModel anim, string outDir)
    {
        var dock = tab.Notetracks!;
        var lanes = LanesOf(window);
        Check($"lanes: Notes, From anim (the xanim has notes of its own), FX, Sound ({string.Join(", ", lanes.Lanes ?? Array.Empty<NotetrackLane>())})",
            lanes.Lanes is [NotetrackLane.Notes, NotetrackLane.FromAnim, NotetrackLane.Fx, NotetrackLane.Sound]);

        // Press on the ruler, drag along it: the playhead follows, and the sounds play as a drag.
        PressAt(window, LanePoint(window, lanes, 20, -1));
        var pressed = anim.CurrentFrame;
        var scrubbing = anim.Timeline.IsScrubbing;
        for (var f = 21; f <= 30; f++)
            PointerAt(window, LanePoint(window, lanes, f, -1));
        var dragged = anim.CurrentFrame;
        ReleaseAt(window, LanePoint(window, lanes, 30, -1));
        Check($"lanes: a press on the ruler moves the playhead there ({pressed}) and a drag scrubs it ({dragged}), as a scrub ({scrubbing}, ended {!anim.Timeline.IsScrubbing})",
            pressed == 20 && dragged == 30 && scrubbing && !anim.Timeline.IsScrubbing);
        System.Threading.Thread.Sleep(550);
        ClickPoint(window, LanePoint(window, lanes, 10, 3));
        Check($"lanes: a press on an empty lane scrubs too ({anim.CurrentFrame})", anim.CurrentFrame == 10);

        // A marker: press selects it, and the playhead goes to its frame.
        ClickPoint(window, LanePoint(window, lanes, 3, 0, dx: 10));
        Check($"lanes: a press on a marker selects its note ({dock.Selected?.Key}) and moves the playhead to it ({anim.CurrentFrame}); its row is selected",
            dock.Selected?.Key == "customnote0" && anim.CurrentFrame == 3 && dock.Selected.IsSelected);
        ClickPoint(window, LanePoint(window, lanes, 22, 1, dx: 10));
        Check($"lanes: an exported note selects too ({dock.Selected?.Key}, frame {anim.CurrentFrame})",
            dock.Selected?.IsReadOnly == true && anim.CurrentFrame == 22);

        // Drag a marker: its note is retimed, one undo step.
        var undoBefore = tab.Record.History.NextUndo;
        PressAt(window, LanePoint(window, lanes, 41, 0, dx: 10));
        for (var f = 41.5; f <= 45; f += 0.5)
            PointerAt(window, LanePoint(window, lanes, f, 0, dx: 10));
        ReleaseAt(window, LanePoint(window, lanes, 45, 0, dx: 10));
        var retimed = Value(tab, "customnote1frame");
        var oneStep = tab.Record.History.NextUndo is { } step && !ReferenceEquals(step, undoBefore) && step.Changes.Count == 1;
        var marker = dock.LaneMarkers.FirstOrDefault(m => m.Key == "customnote1");
        Check($"lanes: dragging the Rumble marker retimes its note (customnote1frame {retimed}, marker at {marker?.Frame}), one undo step ({oneStep})",
            retimed == "45" && marker?.Frame == 45 && oneStep);
        tab.UndoCommand.Execute(null);
        Settle(window);
        Check($"lanes: Ctrl+Z puts it back ({Value(tab, "customnote1frame")}, marker at {dock.LaneMarkers.First(m => m.Key == "customnote1").Frame})",
            Value(tab, "customnote1frame") == "41" && dock.LaneMarkers.First(m => m.Key == "customnote1").Frame == 41);
        System.Threading.Thread.Sleep(550);

        // An exported note can't be dragged.
        var stepBefore = tab.Record.History.NextUndo;
        PressAt(window, LanePoint(window, lanes, 22, 1, dx: 10));
        for (var f = 23; f <= 30; f++)
            PointerAt(window, LanePoint(window, lanes, f, 1, dx: 10));
        ReleaseAt(window, LanePoint(window, lanes, 30, 1, dx: 10));
        Check("lanes: dragging an exported note moves nothing (it has no GDT entry)",
            dock.LaneMarkers.Any(m => m.IsReadOnly && m.Frame == 22) && ReferenceEquals(tab.Record.History.NextUndo, stepBefore));
        System.Threading.Thread.Sleep(550);

        // Double-click an empty part of the FX lane: an FX note there.
        var rows = dock.Rows.Count;
        DoubleClickPoint(window, LanePoint(window, lanes, 15, 2));
        var fx = dock.Rows.FirstOrDefault(r => r.Lane == NotetrackLane.Fx);
        Check($"lanes: a double-click on the FX lane adds an FX note at that frame (fx_customnote0: {Value(tab, "fx_customnote0action")} at {Value(tab, "fx_customnote0frame")}; {dock.Rows.Count - rows} row added, selected {fx?.IsSelected})",
            Value(tab, "fx_customnote0action") == "Play Fx" && Value(tab, "fx_customnote0frame") == "15" && dock.Rows.Count == rows + 1 && fx is { IsSelected: true });
        Capture(window, Path.Combine(outDir, "82-xanim-fx-note-added-dark.png"));
        tab.UndoCommand.Execute(null);
        Settle(window);
        Check($"lanes: Ctrl+Z takes the added note out again ({Value(tab, "fx_customnote0action") ?? "∅"}, {dock.Rows.Count} rows)",
            (Value(tab, "fx_customnote0action") ?? "None") is "None" or "" && dock.Rows.Count == rows);

        // Keys on the focused lanes: → steps a frame, Home and End go to the ends, Delete removes the selected note.
        ClickPoint(window, LanePoint(window, lanes, 3, 0, dx: 10));
        Key(window, Avalonia.Input.Key.Right);
        var right = anim.CurrentFrame;
        Key(window, Avalonia.Input.Key.End);
        var end = anim.CurrentFrame;
        Key(window, Avalonia.Input.Key.Home);
        var home = anim.CurrentFrame;
        Check($"lanes: with the keyboard on them, → steps a frame ({right}), End and Home go to the ends ({end}, {home})",
            lanes.IsFocused && right == 4 && end == anim.LastFrame && home == 0);
        ClickPoint(window, LanePoint(window, lanes, 3, 0, dx: 10));
        Key(window, Avalonia.Input.Key.Delete);
        Settle(window);
        var removed = Value(tab, "customnote0action");
        var rowGone = dock.Rows.All(r => r.Key != "customnote0");
        tab.UndoCommand.Execute(null);
        Settle(window);
        Check($"lanes: Delete removes the selected note the way APE does (action {removed}, row gone {rowGone}); Ctrl+Z brings it back ({Value(tab, "customnote0action")})",
            removed == "None" && rowGone && Value(tab, "customnote0action") == "Sound" && dock.Rows.Any(r => r.Key == "customnote0"));
        System.Threading.Thread.Sleep(550);
        AnimLaneReviewChecks(window, tab, anim, outDir);
    }

    // ── The October review's lane items: press vs click, the row in view, the drag, keys, stacks, the end, tips ──

    private static void AnimLaneReviewChecks(Window window, AssetEditorViewModel tab, AnimPreviewViewModel anim, string outDir)
    {
        var dock = tab.Notetracks!;
        var lanes = LanesOf(window);
        var dockView = window.GetVisualDescendants().OfType<NotetrackDockView>().First();
        var scroll = dockView.FindControl<ScrollViewer>("TableScroll")!;

        // N3, N4: a press selects without moving the playhead and brings the row into view; the click then goes there.
        ClickPoint(window, LanePoint(window, lanes, 30, -1));
        scroll.Offset = default;
        Settle(window);
        var target = dock.Rows.First(r => r.Key == "customnote1");
        var hiddenBefore = dockView.ContainerOf(target) is not { } c0 || c0.TranslatePoint(new Point(0, c0.Bounds.Height), scroll)!.Value.Y > scroll.Bounds.Height + 0.5;
        PressAt(window, MarkerPoint(window, lanes, "customnote1"));
        Settle(window);
        var pressedFrame = anim.CurrentFrame;
        var pressedKey = dock.Selected?.Key;
        var container = dockView.ContainerOf(target)!;
        var top = container.TranslatePoint(default, scroll)!.Value.Y;
        var inView = top >= -0.5 && top + container.Bounds.Height <= scroll.Bounds.Height + 0.5;
        ReleaseAt(window, MarkerPoint(window, lanes, "customnote1"));
        Check($"lanes: a press on a marker selects its note ({pressedKey}) and leaves the playhead ({pressedFrame}); its row, out of view before ({hiddenBefore}), scrolls into view ({top:0} px in a {scroll.Bounds.Height:0} px table); the click then moves the playhead to it ({anim.CurrentFrame})",
            pressedKey == "customnote1" && pressedFrame == 30 && hiddenBefore && inView && anim.CurrentFrame == 41);
        System.Threading.Thread.Sleep(550);

        // N5: the drag scrubs the preview, shows its frame, snaps to where the playhead was parked; Alt places it freely.
        ClickPoint(window, LanePoint(window, lanes, 30, -1));
        var undoBefore = tab.Record.History.NextUndo;
        var grabAt = MarkerPoint(window, lanes, "customnote1");
        var grab = grabAt.X - lanes.TranslatePoint(new Point(lanes.XOf(41), 0), window)!.Value.X;
        PressAt(window, grabAt);
        var during = new List<double>();
        for (var f = 40; f >= 32; f--)
        {
            PointerAt(window, LanePoint(window, lanes, f, 0, grab));
            during.Add(anim.CurrentFrame);
        }
        PointerAt(window, LanePoint(window, lanes, 30, 0, grab + 4));
        var snapFrame = lanes.DragFrame;
        var snapTo = lanes.SnappedTo;
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        window.MouseMove(LanePoint(window, lanes, 30, 0, grab + 5), RawInputModifiers.Alt | RawInputModifiers.LeftMouseButton);
        Settle(window);
        var altSnap = lanes.SnappedTo;
        PointerAt(window, LanePoint(window, lanes, 30, 0, grab + 4));
        Capture(window, Path.Combine(outDir, "87-xanim-note-drag-dark.png"));
        ReleaseAt(window, LanePoint(window, lanes, 30, 0, grab + 4));
        var oneStep = tab.Record.History.NextUndo is { } step && !ReferenceEquals(step, undoBefore) && step.Changes.Count == 1;
        Check($"lanes: dragging a marker scrubs the preview with it ({string.Join(",", during)}), shows the frame it would drop on ({snapFrame}), snaps to where the playhead was ({snapTo}; with Alt: {altSnap ?? "free"}); the drop is one undo step (customnote1frame {Value(tab, "customnote1frame")}, {oneStep})",
            during.SequenceEqual(Enumerable.Range(32, 9).Reverse().Select(f => (double)f)) && snapFrame == 30 && snapTo == "playhead" && altSnap is null
            && Value(tab, "customnote1frame") == "30" && oneStep);
        tab.UndoCommand.Execute(null);
        Settle(window);
        System.Threading.Thread.Sleep(550);

        // N5: Shift moves it a quarter as fast; Esc cancels the drag (the marker stays, the playhead goes back).
        ClickPoint(window, LanePoint(window, lanes, 30, -1));
        undoBefore = tab.Record.History.NextUndo;
        grabAt = MarkerPoint(window, lanes, "customnote1");
        PressAt(window, grabAt);
        var perFrame = lanes.XOf(1) - lanes.XOf(0);
        for (var i = 1; i <= 8; i++)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            window.MouseMove(grabAt + new Point(i * perFrame / 2, 0), RawInputModifiers.Shift | RawInputModifiers.LeftMouseButton);
        }
        Settle(window);
        var fine = lanes.DragFrame;
        Key(window, Avalonia.Input.Key.Escape);
        var cancelledFrame = anim.CurrentFrame;
        var dragGone = lanes.DragFrame is null;
        ReleaseAt(window, grabAt + new Point(4 * perFrame, 0));
        Check($"lanes: Shift drags a quarter as fast (4 frames of pointer moved it to {fine}); Esc cancels the drag (playhead back to {cancelledFrame}, nothing written: {ReferenceEquals(tab.Record.History.NextUndo, undoBefore)}, still {Value(tab, "customnote1frame")})",
            fine == 42 && dragGone && cancelledFrame == 30 && ReferenceEquals(tab.Record.History.NextUndo, undoBefore) && Value(tab, "customnote1frame") == "41");
        System.Threading.Thread.Sleep(550);

        // N6: Ctrl+← → go between notes in playing order, Shift+← → nudge, ↑ ↓ change lane, Insert adds there, Esc lets go.
        ClickPoint(window, MarkerPoint(window, lanes, "customnote0"));
        KeyWith(window, Avalonia.Input.Key.Right, RawInputModifiers.Control);
        var next = (dock.Selected?.Key, anim.CurrentFrame);
        KeyWith(window, Avalonia.Input.Key.Left, RawInputModifiers.Control);
        var back = (dock.Selected?.Key, anim.CurrentFrame);
        undoBefore = tab.Record.History.NextUndo;
        KeyWith(window, Avalonia.Input.Key.Right, RawInputModifiers.Shift);
        var nudged = (Value(tab, "customnote0frame"), anim.CurrentFrame, Steps: !ReferenceEquals(tab.Record.History.NextUndo, undoBefore));
        tab.UndoCommand.Execute(null);
        Settle(window);
        Check($"lanes: Ctrl+→ selects the next note in playing order and goes to it ({next.Key} at {next.CurrentFrame}), Ctrl+← back ({back.Key} at {back.CurrentFrame}); Shift+→ nudges the selected note a frame ({nudged.Item1}, playhead {nudged.CurrentFrame}, one step {nudged.Steps})",
            next.Key?.StartsWith("export:rmbnt", StringComparison.Ordinal) == true && next.CurrentFrame == 22 && back is ("customnote0", 3)
            && nudged is ("4", 4, true) && Value(tab, "customnote0frame") == "3");
        ClickPoint(window, MarkerPoint(window, lanes, "customnote0"));
        KeyWith(window, Avalonia.Input.Key.Down, RawInputModifiers.None);
        var fromAnim = dock.Selected?.IsReadOnly == true;
        KeyWith(window, Avalonia.Input.Key.Down, RawInputModifiers.None);
        var fxLane = (lanes.KeyLane, dock.Selected);
        KeyWith(window, Avalonia.Input.Key.Insert, RawInputModifiers.None);
        Pump(50);
        Settle(window);
        var inserted = dock.Rows.FirstOrDefault(r => r.Key == "fx_customnote0");
        var insertFocus = FocusIn(window, inserted?.Param1Item);
        Check($"lanes: ↓ moves to the next lane and its nearest note ({fromAnim}), ↓ again to the empty FX lane ({fxLane.KeyLane}, nothing selected {fxLane.Selected is null}); Insert adds an FX note at the playhead there ({Value(tab, "fx_customnote0action")} at {Value(tab, "fx_customnote0frame")}), the keyboard in its FX field ({insertFocus})",
            fromAnim && fxLane is (NotetrackLane.Fx, null) && Value(tab, "fx_customnote0action") == "Play Fx" && Value(tab, "fx_customnote0frame") == "3" && insertFocus);
        tab.UndoCommand.Execute(null);
        Settle(window);
        ClickPoint(window, MarkerPoint(window, lanes, "customnote0"));
        KeyWith(window, Avalonia.Input.Key.Enter, RawInputModifiers.None);
        Pump(50);
        Settle(window);
        var enterFocus = FocusIn(window, dock.Rows.First(r => r.Key == "customnote0").Param1Item);
        ClickPoint(window, MarkerPoint(window, lanes, "customnote0"));
        KeyWith(window, Avalonia.Input.Key.Escape, RawInputModifiers.None);
        var letGo = dock.Selected is null;
        DoubleClickPoint(window, MarkerPoint(window, lanes, "customnote1"));
        Pump(50);
        Settle(window);
        var doubleFocus = FocusIn(window, dock.Rows.First(r => r.Key == "customnote1").Param1Item);
        Check($"lanes: Enter puts the keyboard in the selected note's first parameter ({enterFocus}), Esc lets go of the note ({letGo}), a double-click on a marker types in its parameter ({doubleFocus})",
            enterFocus && letGo && doubleFocus);

        // N2: notes on one frame stack in rows instead of covering each other.
        tab.SetValues(new[] { ("customnote1frame", "3") }, "probe");
        Settle(window);
        var rows = lanes.RowsIn(NotetrackLane.Notes);
        var a = lanes.CenterOf("customnote0");
        var b = lanes.CenterOf("customnote1");
        Capture(window, Path.Combine(outDir, "88-xanim-notes-stacked-dark.png"));
        tab.UndoCommand.Execute(null);
        Settle(window);
        Check($"lanes: two notes on one frame stack in two rows of their lane ({rows} rows, at y {a?.Y:0} and {b?.Y:0}); back to one row after Ctrl+Z ({lanes.RowsIn(NotetrackLane.Notes)})",
            rows == 2 && a is { } pa && b is { } pb && Math.Abs(pa.Y - pb.Y) >= 22 && lanes.RowsIn(NotetrackLane.Notes) == 1);

        // A crowded anim: stacked lanes taller than the dock scroll in their own area, and the table under them keeps
        // whole rows to scroll; the wheel over the table scrolls the table.
        {
            var added = 0;
            for (var i = 0; i < 4; i++)
                if (dock.Add(NotetrackLane.Notes, 3) is not null)
                    added++;
            for (var i = 0; i < 3; i++)
                if (dock.Add(NotetrackLane.Fx, 3) is not null)
                    added++;
            Settle(window);
            var lanesScroll = dockView.FindControl<ScrollViewer>("LanesScroll")!;
            var table = dockView.FindControl<ScrollViewer>("TableScroll")!;
            var tableRows = table.Bounds.Height / NotetrackDockView.RowHeight;
            var lanesScroll_ = lanesScroll.Extent.Height > lanesScroll.Viewport.Height + 0.5;
            var before = table.Offset.Y;
            var over = table.TranslatePoint(new Point(table.Bounds.Width / 2, table.Bounds.Height / 2), window)!.Value;
            window.MouseMove(over);
            window.MouseWheel(over, new Vector(0, -1));
            Settle(window);
            var scrolled = table.Offset.Y > before;
            Capture(window, Path.Combine(outDir, "88b-xanim-notes-crowded-dark.png"));
            Check($"lanes: a crowded anim ({added} notes added on one frame) leaves the table {tableRows:0.##} whole rows ({NotetrackDockView.MinTableRows}+), the lanes scroll in their own area ({lanesScroll.Viewport.Height:0} of {lanesScroll.Extent.Height:0} px), the wheel scrolls the table ({before:0} → {table.Offset.Y:0})",
                added == 7 && tableRows >= NotetrackDockView.MinTableRows - 0.01 && Math.Abs(tableRows - Math.Round(tableRows)) < 0.01 && lanesScroll_ && scrolled);
            for (var i = 0; i < added; i++)
                tab.UndoCommand.Execute(null);
            Settle(window);
            System.Threading.Thread.Sleep(550);
        }

        // A long file path shows its file's name, the folders trimmed from the front; a click gives the whole path to edit.
        {
            const string longPath = @"tbd\iw8\ar_valpha\a_folder_long_enough_to_overflow\vm_ar_valpha_reload.xanim_bin";
            tab.SetValues(new[] { ("filename", longPath) }, "probe");
            Settle(window);
            var props = window.GetVisualDescendants().OfType<AnimPropertiesView>().First();
            var box = props.GetVisualDescendants().OfType<TextBox>().First(t => t.Classes.Contains("file") && t.Text == longPath);
            var label = props.GetVisualDescendants().OfType<TextBlock>()
                .First(t => t.Text == longPath && t.TextTrimming == Avalonia.Media.TextTrimming.LeadingCharacterEllipsis);
            var trimmed = label.IsEffectivelyVisible && label.TextLayout.TextLines.Any(l => l.HasCollapsed);
            var shown = label.TextLayout.TextLines.Count > 0 ? string.Concat(label.TextLayout.TextLines[0].TextRuns.Select(r => r.Text.ToString())) : "";
            Capture(window, Path.Combine(outDir, "88c-xanim-file-path-dark.png"));
            ClickPoint(window, box.TranslatePoint(new Point(box.Bounds.Width / 3, box.Bounds.Height / 2), window)!.Value);
            Settle(window);
            var editing = box.IsFocused && !label.IsEffectivelyVisible;
            KeyWith(window, Avalonia.Input.Key.Escape, RawInputModifiers.None);
            tab.UndoCommand.Execute(null);
            Settle(window);
            // Off the field, past the tip's delay: the checks after this start with no tip up.
            PointerAt(window, new Point(4, 4));
            Settle(window);
            System.Threading.Thread.Sleep(550);
            Check($"file field: out of editing a long path shows its file's name, trimmed in front ('{shown}'); a click edits the whole path ({editing})",
                trimmed && shown.StartsWith('…') && shown.EndsWith("vm_ar_valpha_reload.xanim_bin") && editing);
        }

        // N1: a note past the last frame keeps its frame, sits at the edge saying so, and is a problem; a click goes to the end.
        tab.SetValues(new[] { ("customnote1frame", "70") }, "probe");
        Settle(window);
        var past = dock.LaneMarkers.First(m => m.Key == "customnote1");
        var at = lanes.CenterOf("customnote1")!.Value;
        Capture(window, Path.Combine(outDir, "89-xanim-note-past-end-dark.png"));
        ClickPoint(window, MarkerPoint(window, lanes, "customnote1"));
        Check($"lanes: a note past the last frame keeps its frame ({past.Frame}), sits against the right edge ({at.X:0} of {lanes.Bounds.Width:0} px), is a problem ('{past.Problem}'); a click goes to the last frame ({anim.CurrentFrame})",
            past.Frame == 70 && past.Problem is not null && at.X > lanes.XOf(anim.LastFrame) - 160 && at.X < lanes.Bounds.Width
            && anim.CurrentFrame == anim.LastFrame);
        tab.UndoCommand.Execute(null);
        Settle(window);
        System.Threading.Thread.Sleep(550);

        // N10: the tip waits the usual delay and opens over the marker it names.
        PointerAt(window, LanePoint(window, lanes, 15, 3));
        Settle(window);
        System.Threading.Thread.Sleep(700);
        PointerAt(window, MarkerPoint(window, lanes, "customnote0"));
        Settle(window);
        var atOnce = ToolTip.GetIsOpen(lanes);
        Pump(ToolTip.GetShowDelay(lanes) + 150);
        var later = ToolTip.GetIsOpen(lanes);
        var offset = ToolTip.GetHorizontalOffset(lanes);
        var tip = ToolTip.GetTip(lanes) as string;
        ToolTip.SetIsOpen(lanes, false);
        PointerAt(window, LanePoint(window, lanes, 15, 3));
        Settle(window);
        Check($"lanes: a marker's tip waits the usual delay (at once {atOnce}, after {ToolTip.GetShowDelay(lanes)} ms {later}) and opens over it (offset {offset:0}, marker at {lanes.XOf(3):0}): '{tip?.Split('\n')[0]}'",
            !atOnce && later && Math.Abs(offset - Math.Round(lanes.XOf(3))) < 2 && tip?.StartsWith("Sound · wpn_apex_fpo_charge  ·  frame 3  ·  Note 1") == true);
    }

    // ── The table: Add at the playhead, typing into the new note, row selection, a problem ──

    private static void AnimTableChecks(Window window, MainViewModel vm, AssetEditorViewModel tab, AnimPreviewViewModel anim, string outDir)
    {
        var dock = tab.Notetracks!;
        var lanes = LanesOf(window);
        ClickPoint(window, LanePoint(window, lanes, 12, -1));
        var add = window.GetVisualDescendants().OfType<Button>().First(b => b.Name == "AddNoteButton");
        var label = (add.Content as TextBlock)?.Text;
        var addTip = ToolTip.GetTip(add) as string;
        Click(window, add);
        Pump(50);
        Settle(window);
        var row = dock.Rows.FirstOrDefault(r => r.Key == "customnote2");
        var focus = window.FocusManager?.GetFocusedElement() as Visual;
        var inParam = focus is not null && row?.Param1Item is { } p1
            && focus.GetSelfAndVisualAncestors().OfType<PropertyEditorView>().Any(v => ReferenceEquals(v.DataContext, p1));
        Check($"table: '{label}' (a fixed label; its tip says where: '{addTip}') adds a sound note at the playhead in the next free note (customnote2: {Value(tab, "customnote2action")} at {Value(tab, "customnote2frame")}), selected, the keyboard in its alias ({inParam})",
            label == "+ Add note" && addTip?.Contains("frame 12") == true && Value(tab, "customnote2action") == "Sound" && Value(tab, "customnote2frame") == "12" && row is { IsSelected: true } && inParam);
        foreach (var ch in "wpn_new_note")
            TypeChar(window, ch);
        Key(window, Avalonia.Input.Key.Enter);
        Settle(window);
        var lane = dock.LaneMarkers.FirstOrDefault(m => m.Key == "customnote2")?.Text;
        Check($"table: typing the alias and Enter writes it ({Value(tab, "customnote2actionparam1")}) and the marker says it ('{lane}')",
            Value(tab, "customnote2actionparam1") == "wpn_new_note" && lane == "Sound · wpn_new_note");
        Capture(window, Path.Combine(outDir, "83-xanim-note-typed-dark.png"));
        tab.UndoCommand.Execute(null);
        tab.UndoCommand.Execute(null);
        Settle(window);
        Check($"table: two Ctrl+Z take the alias and the note back out ({Value(tab, "customnote2action")})",
            Value(tab, "customnote2action") is "None" && dock.Rows.All(r => r.Key != "customnote2"));

        // A press on a row's track cell: its note selected, focus left where it was, and the playhead and playback untouched.
        var target = dock.Rows.First(r => r.Key == "customnote1");
        var container = window.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("ntrow") && b.DataContext == target);
        var track = container.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "Notes");
        var focusBefore = window.FocusManager?.GetFocusedElement();
        var frameBefore = anim.CurrentFrame;
        anim.IsPlaying = true;
        var wasPlaying = anim.IsPlaying;
        Click(window, track);
        Settle(window);
        var stillPlaying = anim.IsPlaying;
        anim.IsPlaying = false;
        anim.GoToFrame((int)Math.Round(frameBefore));
        Settle(window);
        Check($"table: a press on a row selects its note ({dock.Selected?.Key}) without taking the keyboard, moving the playhead ({frameBefore} → {anim.CurrentFrame}) or stopping playback (playing {wasPlaying} → {stillPlaying})",
            dock.Selected == target && anim.CurrentFrame == frameBefore && (!wasPlaying || stillPlaying)
            && ReferenceEquals(window.FocusManager?.GetFocusedElement(), focusBefore));

        AnimTableReviewChecks(window, tab, anim, outDir);
        container = window.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("ntrow") && b.DataContext == target);

        // A frame past the clip's end: ⚠ on the row and the marker.
        target.FrameItem!.RawValue = "70";
        Settle(window);
        var warn = container.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Text == "⚠");
        Check($"table: a note past the anim's end shows ⚠ on its row and marker ('{target.Problem}')",
            target.HasProblem && warn is { IsEffectivelyVisible: true } && dock.LaneMarkers.First(m => m.Key == "customnote1").Problem is not null);
        Capture(window, Path.Combine(outDir, "84-xanim-note-problem-dark.png"));
        tab.UndoCommand.Execute(null);
        Settle(window);
        Check("table: Ctrl+Z clears it", !target.HasProblem && Value(tab, "customnote1frame") == "41");

        // Right-click an exported note's row: Add as notetrack, on its frame (borrowed, as Use Existing Note does).
        var dockView = window.GetVisualDescendants().OfType<NotetrackDockView>().First();
        var export = dock.Rows.First(r => r.IsReadOnly && r.Marker.Label.StartsWith("rmbnt#", StringComparison.Ordinal));
        var exportRow = window.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("ntrow") && b.DataContext == export);
        Click(window, exportRow.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "From anim"), MouseButton.Right);
        var menu = dockView.NoteMenu;
        var addItem = menu?.Items.OfType<MenuItem>().FirstOrDefault(i => i.Header as string == "Add as note");
        Check($"table: right-clicking an exported note opens its menu ({menu?.IsOpen}) with Add as note",
            menu is { IsOpen: true } && addItem is { IsEnabled: true });
        addItem!.Command?.Execute(null);
        menu!.Close();
        Settle(window);
        var added = dock.Rows.FirstOrDefault(r => r.Key == "customnote2");
        Check($"table: Add as note makes a rumble on the exported note's frame ({Value(tab, "customnote2action")} '{Value(tab, "customnote2actionparam1")}', linked to {Value(tab, "customnote2useexistingnote")}, shown '{added?.FrameText}')",
            Value(tab, "customnote2action") == "Rumble" && Value(tab, "customnote2actionparam1") == "reload_small"
            && Value(tab, "customnote2useexistingnote") == export.Marker.Label && added is { IsLinked: true, FrameText: "↪ 22", CanRetime: false });
        var addedRow = window.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("ntrow") && b.DataContext == added);
        Click(window, addedRow.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "Notes"), MouseButton.Right);
        var own = dockView.NoteMenu?.Items.OfType<MenuItem>().FirstOrDefault(i => i.Header as string == "Use its own frame");
        own!.Command?.Execute(null);
        dockView.NoteMenu?.Close();
        Settle(window);
        Check($"table: Use its own frame unlinks it, and its frame field is back ({Value(tab, "customnote2useexistingnote")}, field {added?.ShowFrameField})",
            string.IsNullOrEmpty(Value(tab, "customnote2useexistingnote")) && added is { ShowFrameField: true });
        tab.UndoCommand.Execute(null);
        tab.UndoCommand.Execute(null);
        Settle(window);

        // Loop, clicked: off, playback stops at the end.
        var loop = window.GetVisualDescendants().OfType<ToggleButton>().First(t => t.Name == "LoopToggle");
        Click(window, loop);
        var off = !anim.Loop;
        Click(window, loop);
        Check($"transport: Loop switches off ({off}) and back on ({anim.Loop}) with a click", off && anim.Loop);
    }

    // ── The October review's table items: order, header, parameter names, no ↶ on a new note, the action picker, sizes ──

    private static void AnimTableReviewChecks(Window window, AssetEditorViewModel tab, AnimPreviewViewModel anim, string outDir)
    {
        var dock = tab.Notetracks!;
        var dockView = window.GetVisualDescendants().OfType<NotetrackDockView>().First();
        Border RowOf(string key) => window.GetVisualDescendants().OfType<Border>()
            .First(b => b.Classes.Contains("ntrow") && b.DataContext is NotetrackRowViewModel r && r.Key == key);
        PropertyEditorView EditorOf(Border row, PropertyItemViewModel? item) =>
            row.GetVisualDescendants().OfType<PropertyEditorView>().First(v => ReferenceEquals(v.DataContext, item));
        string? PlaceholderOf(PropertyEditorView editor) => editor.GetVisualDescendants().OfType<TextBox>().FirstOrDefault()?.PlaceholderText;

        // N9: one order for every note, by frame, the entry's and the xanim's together; whole rows in the table.
        var order = string.Join(",", dock.Rows.Select(r => r.Marker.Frame));
        var scroll = dockView.FindControl<ScrollViewer>("TableScroll")!;
        Check($"table: every note in playing order, the entry's and the xanim's together ({order}); the table shows whole rows ({scroll.Bounds.Height:0} px = {scroll.Bounds.Height / NotetrackDockView.RowHeight:0.##} rows)",
            order == "1,3,22,41" && scroll.Bounds.Height > 0 && Math.Abs(scroll.Bounds.Height % NotetrackDockView.RowHeight) < 0.5);

        // N8: the header's count is one number (its tip splits it) and trims rather than clipping.
        var title = dockView.FindControl<TextBlock>("DockTitle")!;
        Check($"header: 'Notetracks {dock.CountText}' ('{ToolTip.GetTip(title)}'), trimmed with an ellipsis when narrow ({title.TextTrimming})",
            dock.CountText == "4" && dock.CountTip == "2 notes in the GDT entry, 2 notes exported in the xanim" && title.TextTrimming == Avalonia.Media.TextTrimming.CharacterEllipsis);

        // N15: each parameter's field is named as the deffile names it for the action; parameters the action doesn't use are gone.
        var sound = dock.Rows.First(r => r.Key == "customnote0");
        var rumble = dock.Rows.First(r => r.Key == "customnote1");
        var soundRow = RowOf("customnote0");
        var rumbleRow = RowOf("customnote1");
        var names = $"{PlaceholderOf(EditorOf(soundRow, sound.Param1Item))} / {PlaceholderOf(EditorOf(soundRow, sound.Param2Item))}; "
                    + $"{PlaceholderOf(EditorOf(rumbleRow, rumble.Param1Item))} / {(EditorOf(rumbleRow, rumble.Param2Item).IsEffectivelyVisible ? "shown" : "hidden")}";
        Check($"table: parameter fields carry the deffile's names for their action ({names})",
            names == "Sound Alias / Bone; Rumble / hidden");

        // N16: a note added this session carries no ↶ on its fields; an edited existing note keeps them.
        var added = dock.Add(NotetrackLane.Notes, 12)!;
        Pump(50);
        Settle(window);
        added.Param1Item!.RawValue = "wpn_x";
        sound.Param1Item!.RawValue = "wpn_edited";
        Settle(window);
        static int Reverts(Border row) => row.GetVisualDescendants().OfType<Button>().Count(b => b.Content as string == "↶" && b.IsEffectivelyVisible);
        var addedRow = RowOf(added.Key);
        var revertsNew = Reverts(addedRow);
        var revertsOld = Reverts(RowOf("customnote0"));
        tab.UndoCommand.Execute(null);
        Settle(window);
        Check($"table: a note added this session shows no ↶ on its fields ({revertsNew}); an edited note that was already there does ({revertsOld})",
            added.IsAdded && revertsNew == 0 && revertsOld == 1);

        // U2: the action picker groups the deffile's actions and filters as you type; Enter takes the highlight, Esc puts it back.
        var picker = addedRow.GetVisualDescendants().OfType<NoteActionPicker>().First();
        Click(window, picker.Box);
        Settle(window);
        foreach (var ch in "rum")
            TypeChar(window, ch);
        Settle(window);
        var typed = (picker.IsOpen, picker.Highlighted, Groups: string.Join(",", picker.ListedGroups));
        Key(window, Avalonia.Input.Key.Enter);
        Settle(window);
        var picked = Value(tab, added.Key + "action");
        Settle(window);
        Pump(50);
        var rumbleNames = (added.Param1Name, added.ShowParam2);
        Check($"picker: typing 'rum' opens the list narrowed to it ({typed.IsOpen}: {typed.Groups}), the match highlighted ({typed.Highlighted}); Enter takes it ({picked}); the parameter follows the action ({rumbleNames.Param1Name}, second shown {rumbleNames.ShowParam2})",
            typed is (true, "Rumble", "Weapon") && picked == "Rumble" && rumbleNames is ("Rumble", false));

        Key(window, Avalonia.Input.Key.Down, RawInputModifiers.Alt);
        Settle(window);
        var groups = string.Join(",", picker.ListedGroups);
        var first = picker.ListedActions.FirstOrDefault();
        var highlighted = picker.Highlighted;
        Key(window, Avalonia.Input.Key.Down);
        var moved = picker.Highlighted;
        Capture(window, Path.Combine(outDir, "90-xanim-action-picker-dark.png"));
        Key(window, Avalonia.Input.Key.Escape);
        Settle(window);
        Check($"picker: Alt+↓ opens every action grouped ({groups}; None first: {first}) on the current one ({highlighted}); ↓ walks it ({moved}); Esc closes it with the action unchanged ({Value(tab, added.Key + "action")})",
            groups.StartsWith("Sound,FX,Exploder,Weapon,Clip,Camera,Model,Notify,Script", StringComparison.Ordinal) && first == "None"
            && highlighted == "Rumble" && moved is not null && moved != "Rumble" && !picker.IsOpen && Value(tab, added.Key + "action") == "Rumble");

        foreach (var ch in "zzz")
            TypeChar(window, ch);
        Key(window, Avalonia.Input.Key.Tab);
        Settle(window);
        Check($"picker: text that names no action is put back on Tab ({Value(tab, added.Key + "action")}, field '{picker.Box.Text}')",
            Value(tab, added.Key + "action") == "Rumble" && picker.Box.Text == "Rumble");

        // A click on an action in the list takes it.
        Click(window, picker.Box);
        Key(window, Avalonia.Input.Key.Down, RawInputModifiers.Alt);
        Settle(window);
        Pump();
        var fxItem = Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(picker).OfType<Border>()
            .First(x => x.Classes.Contains("ntaction") && x.Child is TextBlock { Text: "Play Fx" });
        fxItem.BringIntoView();
        Pump();
        if (TopLevel.GetTopLevel(fxItem) is { } listRoot)
        {
            var at = fxItem.TranslatePoint(new Point(20, fxItem.Bounds.Height / 2), listRoot)!.Value;
            listRoot.MouseMove(at);
            listRoot.MouseDown(at, MouseButton.Left);
            listRoot.MouseUp(at, MouseButton.Left);
        }
        Settle(window);
        Pump(50);
        Check($"picker: a click on an action takes it ({Value(tab, added.Key + "action")}, list closed {!picker.IsOpen}); its parameters are named for it ({added.Param1Name} / {added.Param2Name})",
            Value(tab, added.Key + "action") == "Play Fx" && !picker.IsOpen && added.Param1Name == "FX" && added.Param2Name == "Bone");
        while (Value(tab, added.Key + "action") is { } v && v != "None" && v != "")
        {
            tab.UndoCommand.Execute(null);
            Settle(window);
        }

        // N16: the Frame column fits the largest frame xanim.awi allows (10000) without trimming.
        tab.SetValues(new[] { ("customnote0frame", "10000") }, "probe");
        Settle(window);
        var digits = RowOf("customnote0").GetVisualDescendants().OfType<ScrubNumberBox>().First()
            .GetVisualDescendants().OfType<TextBlock>().First(t => t.Text is { } x && new string(x.Where(char.IsAsciiDigit).ToArray()) == "10000");
        var shown = digits.Bounds.Width;
        digits.Measure(Size.Infinity);
        var wanted = digits.DesiredSize.Width;
        tab.UndoCommand.Execute(null);
        Settle(window);
        Check($"table: the Frame column shows a 5-digit frame whole ({shown:0.#} px for {wanted:0.#} px of text)", wanted > 0 && shown + 0.5 >= wanted);
    }

    // ── The dock's height: drag the splitter, double-click to fold, kept per asset type ──

    private static void AnimDockSizeChecks(Window window, MainViewModel vm, AssetEditorViewModel tab, string outDir)
    {
        var editor = window.GetVisualDescendants().OfType<AssetEditorView>().First();
        var area = Find<Grid>(editor, "AnimArea");
        var splitter = Find<GridSplitter>(editor, "DockSplitter");
        var before = area.RowDefinitions[2].ActualHeight;
        var from = splitter.TranslatePoint(new Point(splitter.Bounds.Width / 2, splitter.Bounds.Height / 2), window)!.Value;
        PressAt(window, from);
        for (var dy = 5; dy <= 60; dy += 5)
            PointerAt(window, from - new Point(0, dy));
        ReleaseAt(window, from - new Point(0, 60));
        var after = area.RowDefinitions[2].ActualHeight;
        var saved = vm.Settings.DockHeights.GetValueOrDefault("xanim");
        Check($"dock: dragging the splitter up grows the dock ({before:0} → {after:0} px) and keeps the height for xanims ({saved:0})",
            Math.Abs(after - before - 60) < 2 && Math.Abs(saved - after) < 1);
        System.Threading.Thread.Sleep(550);

        DoubleClickPoint(window, from - new Point(0, 60));
        var dock = Find<NotetrackDockView>(editor, "AnimDock");
        var folded = area.RowDefinitions[2].ActualHeight;
        Check($"dock: a double-click on the splitter folds it to the transport ({folded:0} px), kept for xanims",
            Math.Abs(folded - NotetrackDockView.CollapsedHeight) < 1 && dock.IsCollapsed && vm.Settings.DocksCollapsed.Contains("xanim"));
        Capture(window, Path.Combine(outDir, "85-xanim-dock-folded-dark.png"));
        var splitterNow = splitter.TranslatePoint(new Point(splitter.Bounds.Width / 2, splitter.Bounds.Height / 2), window)!.Value;
        DoubleClickPoint(window, splitterNow);
        var unfolded = area.RowDefinitions[2].ActualHeight;
        Check($"dock: a second double-click brings back its height ({unfolded:0} px)", Math.Abs(unfolded - after) < 1 && !dock.IsCollapsed);

        // Per type, not per asset: another xanim opens with the same dock.
        vm.OpenByName("apex_other");
        Settle(window);
        var other = area.RowDefinitions[2].ActualHeight;
        vm.OpenByName("apex_raise_first");
        Settle(window);
        Check($"dock: another xanim opens with the same dock height ({other:0} px)", Math.Abs(other - after) < 1 && vm.ActiveTab == tab);
    }

    // ── Pop-out: the chip on the render sends the preview to its window; the dock takes the room ──

    private static void AnimPopOutChecks(Window window, MainViewModel vm, AssetEditorViewModel tab, AnimPreviewViewModel anim, string outDir)
    {
        var editor = window.GetVisualDescendants().OfType<AssetEditorView>().First();
        var centre = Find<PreviewPaneView>(editor, "CentrePreview");
        var area = Find<Grid>(editor, "AnimArea");
        var chip = Find<Button>(centre, "PopOutChip");
        anim.IsPlaying = true;
        Click(window, chip);
        Settle(window);
        Check($"pop-out: the ⧉ chip on the render pops the preview out ({vm.IsPreviewFloating}); the dock takes the editor (preview row {area.RowDefinitions[0].ActualHeight:0} px)",
            vm.IsPreviewFloating && tab.CentrePreview is null && ReferenceEquals(vm.DockedPreview, tab.PreviewPane) && area.RowDefinitions[0].ActualHeight == 0);
        Capture(window, Path.Combine(outDir, "86-xanim-popped-out-dark.png"));
        // N13: the popped-out preview carries the full transport, the notes strip under its slider with its lanes named.
        var popped = typeof(MainWindow).GetField("_previewWindow", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?.GetValue(window) as Window;
        if (popped is not null)
        {
            anim.IsPlaying = false;
            Settle(popped);
            var strip = popped.GetVisualDescendants().OfType<NotetrackTimelineBar>().FirstOrDefault(b => b.IsEffectivelyVisible);
            var transport = strip?.GetVisualAncestors().OfType<Grid>().FirstOrDefault();
            var gutter = strip is not null && transport is not null ? strip.TranslatePoint(default, transport)!.Value.X : 0;
            Capture(popped, Path.Combine(outDir, "86b-xanim-preview-window-dark.png"));
            Check($"strip: the popped-out preview shows the notes strip under its slider ({strip is not null}), {strip?.Bounds.Height:0} px tall, with {gutter:0} px left of it for its lane names",
                strip is { Bounds.Height: >= 24 } && gutter >= 60);
            anim.IsPlaying = true;
        }
        vm.TogglePreviewFloatingCommand.Execute(null);
        Settle(window);
        Check($"pop-out: docking it back returns it above the dock, still playing ({anim.IsPlaying})",
            !vm.IsPreviewFloating && ReferenceEquals(centre.DataContext, tab.PreviewPane) && anim.IsPlaying);
        anim.IsPlaying = false;
    }

    private static void AnimAutomationChecks(Window window)
    {
        var roots = window.GetVisualDescendants().Where(v => v is AssetEditorView or AnimPropertiesView).ToList();
        var unnamed = roots.SelectMany(r => r.GetVisualDescendants()).OfType<Button>()
            .Where(b => b.IsEffectivelyVisible && b.Content is not string)
            .Select(b => (Button: b, Name: NameOfButton(b)))
            .Where(x => x.Name.Length == 0 || x.Name.StartsWith("Avalonia.", StringComparison.Ordinal) || x.Name.StartsWith("Apex.", StringComparison.Ordinal))
            .Select(x => x.Button.Name ?? string.Join(".", x.Button.Classes))
            .ToList();
        var lanes = LanesOf(window);
        var peer = Avalonia.Automation.Peers.ControlAutomationPeer.CreatePeerForElement(lanes);
        Check($"automation: every visible button in the xanim editor and panel has a name ({unnamed.Count} without: {string.Join(", ", unnamed.Take(8))}); the lanes are '{peer.GetName()}': '{peer.GetHelpText()}'",
            unnamed.Count == 0 && peer.GetName() == "Notetracks timeline" && peer.GetHelpText().Contains("notes."));
    }

    // ── Budgets: open, a press on the lanes, Add ──

    private static void AnimBudgets(Window window, MainViewModel vm, AssetEditorViewModel tab, AnimPreviewViewModel anim)
    {
        static (double Median, double P95) Stats(List<double> ms)
        {
            ms.Sort();
            return (ms[ms.Count / 2], ms[Math.Min(ms.Count - 1, (int)Math.Ceiling(ms.Count * 0.95) - 1)]);
        }

        // Timed as the perf gates time: input to idle with layout done, the headless renderer's own frames kept off the clock
        // (a software render of the whole window per input says nothing about the app). Last in the run: it stays silenced.
        // Presses and adds count the UI thread's own time, as the perf gates do: a sample's wall time also holds whatever
        // the OS ran on that core instead (an add of 10 ms measured 28), which moved the p95 from run to run.
        SilenceHeadlessRenderTimer();
        // Collected first, so the collections the timed adds pay for are their own: whatever groups ran before in the
        // process (the timing tier's 125k-asset palette run, say) left a heap that made each one dearer (adds of 35 ms).
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        // Open: the xanim from closed, alternating two (the editor view and the panel are reused, as in use).
        var opens = new List<double>();
        for (var i = 0; i < 12; i++)
        {
            foreach (var t in vm.OpenTabs.ToList())
                t.CloseCommand.Execute(null);
            Settle(window);
            var ms = TimeOpen(window, vm, i % 2 == 0 ? "apex_other" : "apex_raise_first");
            if (i >= 2)
                opens.Add(ms);
        }
        var open = Stats(opens);
        Gate($"budget: opening an xanim (its preview, dock and panel laid out) median {open.Median:0.0} ms, p95 {open.P95:0.0} ms (budget 200 ms)",
            open.Median <= PerfBudgets.OpenAsset && open.P95 <= 2 * PerfBudgets.OpenAsset);

        tab = vm.ActiveTab!;
        anim = (AnimPreviewViewModel)tab.PreviewPane!.Content;
        WaitUntil(() => anim.LastFrame > 0, 10_000);
        Settle(window);
        var lanes = LanesOf(window);
        var presses = new List<double>();
        for (var i = 0; i < 24; i++)
        {
            var p = LanePoint(window, lanes, 5 + i * 2 % 40, -1);
            PointerAt(window, p);
            Settle(window);
            presses.Add(UiThreadMs(() =>
            {
                RawInput.Send(window, "MouseDown", p, MouseButton.Left, RawInputModifiers.None);
                RawInput.Send(window, "MouseUp", p, MouseButton.Left, RawInputModifiers.None);
                Settle(window);
            }));
            System.Threading.Thread.Sleep(5);
        }
        var press = Stats(presses.Skip(4).ToList());
        Gate($"budget: a press on the lanes moves the playhead median {press.Median:0.0} ms, p95 {press.P95:0.0} ms (budget 16 ms)",
            press.Median <= 16 && press.P95 <= 32);

        // A marker drag tick: the ghost, the frame on the ruler and the preview scrubbed to it.
        var ticks = new List<double>();
        var grabAt = MarkerPoint(window, lanes, "customnote1");
        PointerAt(window, grabAt);
        Settle(window);
        RawInput.Send(window, "MouseDown", grabAt, MouseButton.Left, RawInputModifiers.None);
        Settle(window);
        var perFrame = lanes.XOf(1) - lanes.XOf(0);
        for (var i = 1; i <= 24; i++)
        {
            var to = grabAt - new Point(i * perFrame, 0);
            var sw = Stopwatch.StartNew();
            RawInput.Send(window, "MouseMove", to, RawInputModifiers.LeftMouseButton);
            Settle(window);
            ticks.Add(sw.Elapsed.TotalMilliseconds);
        }
        var dragged = anim.CurrentFrame;
        RawInput.Send(window, "MouseUp", grabAt - new Point(24 * perFrame, 0), MouseButton.Left, RawInputModifiers.None);
        Settle(window);
        tab.UndoCommand.Execute(null);
        Settle(window);
        var tick = Stats(ticks.Skip(3).ToList());
        Check($"budget: a marker drag tick (ghost, frame shown, preview scrubbed to {dragged}) median {tick.Median:0.0} ms, p95 {tick.P95:0.0} ms (budget 16 ms)",
            dragged == 41 - 24 && tick.Median <= 16 && tick.P95 <= 32);
        System.Threading.Thread.Sleep(550);

        var add = window.GetVisualDescendants().OfType<Button>().First(b => b.Name == "AddNoteButton");
        var adds = new List<double>();
        for (var i = 0; i < 24; i++)
        {
            var at = add.TranslatePoint(new Point(add.Bounds.Width / 2, add.Bounds.Height / 2), window)!.Value;
            PointerAt(window, at);
            Settle(window);
            adds.Add(UiThreadMs(() =>
            {
                RawInput.Send(window, "MouseDown", at, MouseButton.Left, RawInputModifiers.None);
                RawInput.Send(window, "MouseUp", at, MouseButton.Left, RawInputModifiers.None);
                Settle(window);
            }));
            tab.UndoCommand.Execute(null);
            Settle(window);
            System.Threading.Thread.Sleep(550);
        }
        Console.WriteLine("info  add samples: " + string.Join(" ", adds.Select(a => a.ToString("0.0"))));
        var addStats = Stats(adds.Skip(3).ToList());
        // Where an add's time goes: the values (rules, rows, timeline) and the dock (its rows and markers).
        {
            var sw = Stopwatch.StartNew();
            tab.SetValues(new[] { ("customnote2action", "Sound"), ("customnote2frame", "9") }, "probe");
            var values = sw.Elapsed.TotalMilliseconds;
            sw.Restart();
            tab.Notetracks!.Sync();
            var sync = sw.Elapsed.TotalMilliseconds;
            sw.Restart();
            Settle(window);
            var layout = sw.Elapsed.TotalMilliseconds;
            tab.UndoCommand.Execute(null);
            Settle(window);
            sw.Restart();
            tab.Notetracks!.Add(NotetrackLane.Notes, 9);
            var add2 = sw.Elapsed.TotalMilliseconds;
            sw.Restart();
            Settle(window);
            var settle2 = sw.Elapsed.TotalMilliseconds;
            tab.UndoCommand.Execute(null);
            Settle(window);
            Console.WriteLine($"info  budget: an add is {values:0.0} ms of values (rules, rows, timeline), {sync:0.0} ms of dock sync, {layout:0.0} ms of layout; Add() {add2:0.0} + settle {settle2:0.0} ms");
        }
        Gate($"budget: + Add at frame (the note, its row and marker) median {addStats.Median:0.0} ms, p95 {addStats.P95:0.0} ms (budget 16 ms)",
            addStats.Median <= 16 && addStats.P95 <= 32);
    }
}
