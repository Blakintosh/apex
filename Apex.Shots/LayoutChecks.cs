using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Apex.Editor.Services.Preview.Notetracks;
using Apex.Editor.ViewModels;
using Apex.Editor.Views;

namespace Apex.Shots;

/// <summary>
/// The workspace at the narrowest editor pane (420 px) and with pane widths saved in a wider window: nothing overlaps,
/// nothing runs off the window, and what a screen reader hears is a name, not a type.
/// </summary>
public partial class Program
{
    private static void RunLayoutChecks(string outDir)
    {
        var sessionRoot = Path.Combine(Path.GetTempPath(), "apex-shots-layout-" + Guid.NewGuid().ToString("N")[..8]);
        PaneFitChecks(sessionRoot);
        PaneHandleChecks(sessionRoot + "-handles");
        var vm = new MainViewModel(sessionRoot);
        // The window's 1,100 px minimum: the narrowest editor there is (475 px beside the default Explorer, 248, and right
        // column, 360), which the tab row and header have to fit.
        var window = new MainWindow { DataContext = vm, Width = 1100, Height = 760 };
        window.Show();
        window.Activate();
        try
        {
            HeaderAndTabRowChecks(window, vm, outDir);
            AutomationNameChecks(window, vm);
        }
        finally
        {
            Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
            window.Close();
            foreach (var dir in new[] { sessionRoot, sessionRoot + "-panes", sessionRoot + "-handles" })
                try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
        LimiterChecks();
    }

    private static string NameOfButton(Control c) => ControlAutomationPeer.CreatePeerForElement(c).GetName() ?? "";

    private static Rect BoundsIn(Visual v, Visual root) =>
        v.TransformToVisual(root) is { } m ? new Rect(v.Bounds.Size).TransformToAABB(m) : default;

    /// <summary>Pane widths saved in a wider window (the Explorer dragged out, the right column at its 900 px cap) still fit.</summary>
    private static void PaneFitChecks(string sessionRoot)
    {
        // A window of its own: it is closed and opened again, as quitting and relaunching does (settings stay in memory).
        var vm = new MainViewModel(sessionRoot + "-panes");
        vm.OpenByName("wpn_ar_havoc_zm_upgraded");
        vm.Settings.ExplorerWidth = 466;
        vm.Settings.RightColumnWidth = 900;
        var window = new MainWindow { DataContext = vm, Width = 1600, Height = 760 };
        window.Show();
        Settle(window);
        var right = window.FindControl<Grid>("RightStack")!;
        var editor = window.GetVisualDescendants().OfType<AssetEditorView>().First();
        var rightEdge = BoundsIn(right, window).Right;
        Check($"layout: saved widths wider than the window are fitted: the right column ends at {rightEdge:0} of {window.Bounds.Width:0} px, the editor keeps {editor.Bounds.Width:0} px",
            rightEdge <= window.Bounds.Width - 6 + 0.5 && editor.Bounds.Width >= 410 && right.Bounds.Width < 900);

        // Drag the Explorer's splitter 40 px left with the right column fitted: only the Explorer's width is saved.
        var splitter = window.FindControl<GridSplitter>("ExplorerSplitter")!;
        var from = splitter.TranslatePoint(new Point(splitter.Bounds.Width / 2, splitter.Bounds.Height / 2), window)!.Value;
        window.MouseMove(from);
        window.MouseDown(from, MouseButton.Left);
        for (var dx = 5; dx <= 40; dx += 5)
            window.MouseMove(from - new Point(dx, 0));
        window.MouseUp(from - new Point(40, 0), MouseButton.Left);
        Settle(window);
        Check($"layout: dragging the Explorer saves its width ({vm.Settings.ExplorerWidth:0}) and leaves the fitted right column's saved width alone ({vm.Settings.RightColumnWidth:0})",
            Math.Abs(vm.Settings.ExplorerWidth - 426) < 2 && vm.Settings.RightColumnWidth == 900);

        // Shrink, close, reopen wide: the saved widths are what the user set, not what the small window fitted.
        window.Width = 1110;
        Settle(window);
        window.Close();
        Settle(window);
        Check($"layout: closing a small window keeps the saved widths ({vm.Settings.ExplorerWidth:0}, {vm.Settings.RightColumnWidth:0})",
            Math.Abs(vm.Settings.ExplorerWidth - 426) < 2 && vm.Settings.RightColumnWidth == 900);
        var wide = new MainWindow { DataContext = vm, Width = 1900, Height = 760 };
        wide.Show();
        Settle(wide);
        // The sidebar is the saved width; the Explorer pane sits inside it.
        var explorer = wide.FindControl<DockPanel>("Sidebar")!;
        right = wide.FindControl<Grid>("RightStack")!;
        Check($"layout: reopened wide, the saved widths are back (Explorer {explorer.Bounds.Width:0}, right column {right.Bounds.Width:0} px)",
            Math.Abs(explorer.Bounds.Width - 426) < 2 && Math.Abs(right.Bounds.Width - 900) < 1);
        wide.Close();
    }

    /// <summary>
    /// The resize handles take the pointer a few pixels either side of their line, and the preview's corner sizes both axes.
    /// Real input at offsets from each hairline: what the pointer lands on, and what a drag from there does.
    /// </summary>
    private static void PaneHandleChecks(string sessionRoot)
    {
        var vm = new MainViewModel(sessionRoot);
        vm.OpenByName("mtl_marble_03");
        var window = new MainWindow { DataContext = vm, Width = 1400, Height = 800 };
        window.Show();
        Settle(window);
        try
        {
            var right = window.FindControl<Grid>("RightStack")!;
            var rightSplitter = window.FindControl<GridSplitter>("RightSplitter")!;
            var previewSplitter = window.FindControl<GridSplitter>("PreviewSplitter")!;
            var explorerSplitter = window.FindControl<GridSplitter>("ExplorerSplitter")!;
            var corner = window.FindControl<Border>("PreviewCorner")!;
            var row = right.RowDefinitions[0];

            Check($"handles: a fresh preview is 16:9 of its column (render {right.Bounds.Width - 20:0} x {row.ActualHeight - 48:0})",
                Math.Abs((right.Bounds.Width - 20) * 9 / 16 - (row.ActualHeight - 48)) < 1.5);

            static Point Centre(Visual v, Visual root) => BoundsIn(v, root).Center;
            bool Lands(Point at, Visual handle) => window.InputHitTest(at) is Visual hit && (hit == handle || handle.IsVisualAncestorOf(hit));

            var rc = Centre(rightSplitter, window);
            var pc = Centre(previewSplitter, window);
            var ec = Centre(explorerSplitter, window);
            foreach (var d in new[] { -3, 3 })
            {
                Check($"handles: {d:+0;-0} px from the editor's edge is its handle", Lands(rc + new Point(d, 40), rightSplitter));
                Check($"handles: {d:+0;-0} px from the preview's edge is its handle", Lands(pc + new Point(-60, d), previewSplitter));
                Check($"handles: {d * 2 / 3:+0;-0} px from the Explorer's edge is its handle", Lands(ec + new Point(d * 2 / 3, 40), explorerSplitter));
            }
            Check("handles: the corner is what the pointer lands on at the junction", Lands(Centre(corner, window), corner));

            var width = right.Bounds.Width;
            var from = rc + new Point(-3, 40);
            window.MouseMove(from);
            window.MouseDown(from, MouseButton.Left);
            for (var dx = -5; dx >= -50; dx -= 5)
                window.MouseMove(from + new Point(dx, 0));
            window.MouseUp(from + new Point(-50, 0), MouseButton.Left);
            Settle(window);
            Check($"handles: dragging 3 px off the editor's edge widens the column ({width:0} -> {right.Bounds.Width:0})", right.Bounds.Width > width + 40);

            var height = row.ActualHeight;
            var start = Centre(corner, window);
            window.MouseMove(start);
            window.MouseDown(start, MouseButton.Left);
            for (var i = 1; i <= 10; i++)
                window.MouseMove(start + new Point(-6 * i, 5 * i));
            window.MouseUp(start + new Point(-60, 50), MouseButton.Left);
            Settle(window);
            Check($"handles: dragging the corner sizes both ({width + 50:0} x {height:0} -> {right.Bounds.Width:0} x {row.ActualHeight:0})",
                right.Bounds.Width > width + 90 && row.ActualHeight > height + 40);
            Check($"handles: the corner's sizes are saved ({vm.Settings.RightColumnWidth:0}, {vm.Settings.PreviewPaneHeight:0})",
                Math.Abs(vm.Settings.RightColumnWidth - right.Bounds.Width) < 2 && vm.Settings.PreviewPaneHeight is { } h && Math.Abs(h - row.ActualHeight) < 2);

            corner = window.FindControl<Border>("PreviewCorner")!;
            var again = Centre(corner, window);
            DoubleClickPoint(window, again);
            Settle(window);
            Check("handles: double-clicking the corner goes back to 16:9",
                vm.Settings.PreviewPaneHeight is null && Math.Abs((right.Bounds.Width - 20) * 9 / 16 - (row.ActualHeight - 48)) < 1.5);
        }
        finally
        {
            window.Close();
        }
    }

    private static void HeaderAndTabRowChecks(Window window, MainViewModel vm, string outDir)
    {
        // A long name (as real assets have) on a derived asset, so all four view tabs show.
        vm.OpenByName("wpn_ar_havoc_zm_upgraded");
        vm.DuplicateActiveCommand.Execute(null);
        var tab = vm.ActiveTab!;
        tab.RenameText = "wpn_ar_havoc_zm_upgraded_with_a_long_name_for_layout";
        tab.RenameCommand.Execute(null);
        Settle(window);
        var view = window.GetVisualDescendants().OfType<AssetEditorView>().First();
        Check($"layout: the editor pane is at its narrowest ({view.Bounds.Width:0} px)", view.Bounds.Width is >= 430 and <= 485);

        foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
        {
            Application.Current!.RequestedThemeVariant = theme;
            Settle(window);
            var name = theme == ThemeVariant.Dark ? "dark" : "light";
            HeaderChecks(window, view, tab, name);
            TabRowChecks(window, view, name);
            Capture(window, Path.Combine(outDir, $"70-narrow-editor-{name}.png"));
        }
        Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;

        // The folded filter, with real input: Ctrl+F opens it over the tabs, Esc folds it, a click on ⌕ opens it again.
        var filter = view.FindControl<TextBox>("PropertyFilter")!;
        var button = view.FindControl<Button>("FilterButton")!;
        var tabs = view.FindControl<StackPanel>("ViewTabs")!;
        Key(window, Avalonia.Input.Key.F, RawInputModifiers.Control);
        Settle(window);
        var row = view.FindControl<Grid>("ViewTabRow")!;
        Check($"filter: Ctrl+F opens the folded box across the row ({filter.Bounds.Width:0} of {row.Bounds.Width:0} px), focused, over the tabs",
            view.IsFilterFolded && filter.IsEffectivelyVisible && filter.IsFocused && !tabs.IsVisible && filter.Bounds.Width >= row.Bounds.Width - 1);
        TypeChar(window, 'd');
        Pump(300);
        Capture(window, Path.Combine(outDir, "71-narrow-filter-open-dark.png"));
        Check($"filter: typing filters ({tab.SearchText}, {tab.VisiblePropertyCount} rows)", tab.SearchText == "d");

        // Clicking away folds it back to ⌕, marked as filtering, and the tabs are usable with the filter still on.
        var rail = view.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("railitem") && b.IsEffectivelyVisible);
        Click(window, rail);
        Settle(window);
        var dot = view.FindControl<Avalonia.Controls.Shapes.Ellipse>("FilterActiveDot")!;
        var tip = ToolTip.GetTip(button) as string ?? "";
        Check($"filter: clicking away folds it to ⌕ with a dot, the filter kept ('{tip}'), the tabs back",
            !filter.IsVisible && button.IsEffectivelyVisible && dot.IsEffectivelyVisible && tip.Contains("“d”") && tabs.IsVisible && tab.SearchText == "d"
            && NameOfButton(button).Contains("filtering by d"));
        Capture(window, Path.Combine(outDir, "72-narrow-filter-active-dark.png"));
        var changedTab = tabs.Children.OfType<RadioButton>().First(r => Avalonia.Automation.AutomationProperties.GetName(r)?.StartsWith("Changed") == true);
        Click(window, changedTab);
        Settle(window);
        Check($"filter: with a filter on, a click on Changed switches the view ({tab.View}, filter '{tab.SearchText}')",
            tab.View == EditorView.Changed && tab.SearchText == "d" && !filter.IsVisible);
        Click(window, tabs.Children.OfType<RadioButton>().First());
        Settle(window);
        Key(window, Avalonia.Input.Key.F, RawInputModifiers.Control);
        Settle(window);
        Check($"filter: Ctrl+F opens it again on its text ('{filter.SelectedText}')", filter.IsFocused && filter.IsEffectivelyVisible && filter.SelectedText == "d");
        Key(window, Avalonia.Input.Key.Escape);
        Settle(window);
        Check("filter: Esc clears it; the open box stays while it has focus", tab.SearchText == "" && filter.IsFocused && filter.IsEffectivelyVisible);
        Key(window, Avalonia.Input.Key.Escape);
        Settle(window);
        Check("filter: Esc again folds it, the keyboard on ⌕, the tabs back", button.IsEffectivelyVisible && button.IsFocused && tabs.IsVisible && !filter.IsVisible);
        Click(window, button);
        Settle(window);
        Check("filter: a click on ⌕ opens it", filter.IsEffectivelyVisible && filter.IsFocused);
        Key(window, Avalonia.Input.Key.Escape);
        Settle(window);

        // A wider pane: the box comes back beside the tabs, between 140 and 220 px; the keyboard on ⌕ moves into it.
        Check("filter: ⌕ has the keyboard before the widening", button.IsFocused);
        window.Width = 1500;
        Settle(window);
        Check("filter: widening unfolds the box and the keyboard on ⌕ goes into it", filter.IsFocused);
        Check($"filter: with room, the box sits beside the tabs ({filter.Bounds.Width:0} px)",
            !view.IsFilterFolded && filter.IsEffectivelyVisible && !button.IsVisible && filter.Bounds.Width is >= 140 and <= 220
            && BoundsIn(tabs, row).Right <= BoundsIn(filter, row).Left);
        window.Width = 1110;
        Settle(window);
    }

    private static void HeaderChecks(Window window, AssetEditorView view, AssetEditorViewModel tab, string theme)
    {
        var title = view.FindControl<SelectableTextBlock>("AssetTitle")!;
        var provenance = view.FindControl<TextBlock>("ProvenanceText")!;
        var header = view.FindControl<StackPanel>("HeaderTitle")!;
        var compare = view.GetVisualDescendants().OfType<Button>().First(b => b.Content as string == "Compare");
        var titleBox = BoundsIn(title, window);
        var compareBox = BoundsIn(compare, window);
        var trimmed = title.TextLayout.TextLines.Any(l => l.HasCollapsed);
        Check($"header ({theme}): the long name ends at {titleBox.Right:0} px, before Compare at {compareBox.Left:0}, with an ellipsis and the name in its tooltip",
            titleBox.Right <= compareBox.Left && trimmed && ToolTip.GetTip(title) as string == tab.Name);
        Check($"header ({theme}): the provenance line trims inside the header ({BoundsIn(provenance, window).Right:0} ≤ {BoundsIn(header, window).Right:0})",
            BoundsIn(provenance, window).Right <= BoundsIn(header, window).Right + 0.5);
    }

    private static void TabRowChecks(Window window, AssetEditorView view, string theme)
    {
        var row = view.FindControl<Grid>("ViewTabRow")!;
        var tabs = view.FindControl<StackPanel>("ViewTabs")!;
        var shown = tabs.Children.Where(t => t.IsVisible).ToList();
        var rowBox = BoundsIn(row, window);
        var boxes = shown.Select(t => BoundsIn(t, window)).ToList();
        var filter = new Control[] { view.FindControl<TextBox>("PropertyFilter")!, view.FindControl<Button>("FilterButton")! }
            .Where(c => c.IsEffectivelyVisible).Select(c => BoundsIn(c, window)).ToList();
        var overlaps = boxes.SelectMany((a, i) => boxes.Skip(i + 1).Concat(filter).Where(b => a.Intersects(b))).Count();
        Check($"tab row ({theme}): {shown.Count} tabs and the filter{(view.IsFilterFolded ? " (folded to ⌕)" : "")} don't overlap and stay inside the row ({overlaps} overlaps)",
            shown.Count >= 4 && overlaps == 0 && boxes.Concat(filter).All(b => b.Left >= rowBox.Left - 0.5 && b.Right <= rowBox.Right + 0.5));
    }

    private static void AutomationNameChecks(Window window, MainViewModel vm)
    {
        Settle(window);
        static string NameOf(Control c) => ControlAutomationPeer.CreatePeerForElement(c).GetName() ?? "";
        static bool IsTypeName(string name) => name.StartsWith("Avalonia.", StringComparison.Ordinal) || name.StartsWith("Apex.", StringComparison.Ordinal);

        var tree = window.GetVisualDescendants().OfType<AssetBrowserView>().First().FindControl<ListBox>("Tree")!;
        var rows = tree.GetVisualDescendants().OfType<ListBoxItem>().Where(i => i.IsEffectivelyVisible && i.DataContext is BrowserNode).ToList();
        var rowNames = rows.Select(r => (Node: (BrowserNode)r.DataContext!, Name: NameOf(r))).ToList();
        var asset = rowNames.FirstOrDefault(r => r.Node.Asset is not null);
        var group = rowNames.FirstOrDefault(r => r.Node.Gdt is not null);
        Check($"automation: Explorer rows are named by what they show ('{group.Name}', '{asset.Name}')",
            rowNames.Count > 0 && rowNames.All(r => !IsTypeName(r.Name) && r.Name == r.Node.AutomationName)
            && group.Name.StartsWith(group.Node.DisplayName + ", ") && group.Name.Contains(" asset")
            && (asset.Node is null || asset.Name.StartsWith(asset.Node.Title + ", " + asset.Node.Asset!.Type)));

        var unnamed = window.GetVisualDescendants().OfType<Button>()
            .Where(b => b.IsEffectivelyVisible && b.Content is not string && b.GetVisualAncestors().All(a => a.Opacity > 0)) // not the warm-up controls
            .Select(b => (Button: b, Name: NameOf(b)))
            .Where(x => x.Name.Length == 0 || IsTypeName(x.Name))
            .Select(x => $"{x.Button.Name ?? string.Join(".", x.Button.Classes)} in {x.Button.FindAncestorOfType<UserControl>()?.GetType().Name} ({x.Button.DataContext?.GetType().Name}): '{x.Name}'")
            .ToList();
        Check($"automation: every visible button has a name ({unnamed.Count} without: {string.Join("; ", unnamed.Distinct().Take(12))})", unnamed.Count == 0);

        // The notetrack strip: a peer with its name and the marker under (or nearest) the playhead.
        var timeline = new NotetrackTimeline(() => new FakeAudio(), new Random(1));
        var bar = new Apex.Editor.Controls.NotetrackTimelineBar { Timeline = timeline };
        AutomationProperties.SetName(bar, "Notetracks");
        var peer = ControlAutomationPeer.CreatePeerForElement(bar);
        var empty = peer.GetHelpText();
        timeline.Load(new[]
        {
            new NotetrackMarker(4, "fire", NotetrackSource.Export),
            new NotetrackMarker(12, "Note 1", NotetrackSource.Gdt, "Rumble", "reload_medium"),
        }, 20, null);
        bar.Frame = 12;
        var at = peer.GetHelpText();
        bar.Frame = 6;
        var near = peer.GetHelpText();
        Check($"automation: the notetrack strip is '{peer.GetName()}' ({peer.GetAutomationControlType()}): '{empty}' / '{at}' / '{near}'",
            peer is not NoneAutomationPeer && peer.GetName() == "Notetracks" && empty.StartsWith("No notes")
            && at.StartsWith("2 notes. At the playhead: Rumble · reload_medium  ·  frame 12") && near.StartsWith("2 notes. Nearest: fire  ·  frame 4"));
        timeline.Dispose();

        // The Explorer's context menu: each item can be invoked through UI Automation.
        var menu = window.GetVisualDescendants().OfType<AssetBrowserView>().First().FindControl<ContextMenu>("TreeMenu")!;
        var items = menu.Items.OfType<MenuItem>().ToList();
        var notInvokable = items.Where(i => ControlAutomationPeer.CreatePeerForElement(i) is not IInvokeProvider).Select(i => i.Name).ToList();
        Check($"automation: Explorer context-menu items support Invoke ({items.Count - notInvokable.Count} of {items.Count}: {string.Join(", ", notInvokable.Take(3))})",
            items.Count > 0 && notInvokable.Count == 0);

        // Invoking a real item runs its Click handler, once: Pin to Explorer on the selected asset pins it.
        var node = vm.FlatRows.First(n => n.Asset is not null && !n.IsPinnedEntry && !vm.IsPinned(n.Asset));
        tree.SelectedItems!.Clear();
        tree.SelectedItem = node;
        Settle(window);
        var pin = items.Single(i => i.Name == "MenuPin");
        (ControlAutomationPeer.CreatePeerForElement(pin) as IInvokeProvider)?.Invoke();
        Settle(window);
        var pinned = vm.IsPinned(node.Asset!);
        Check($"automation: invoking 'Pin to Explorer' through UI Automation pins {node.Asset!.Name} ({pinned})", pinned);
        if (pinned)
            vm.TogglePinned(node.Asset!);
    }

    /// <summary>The notetrack mixer's soft clip: inside full scale whatever is summed, untouched below its knee, no step.</summary>
    private static void LimiterChecks()
    {
        // Eight full-scale sines and square waves, summed as the mixer sums them (peaks up to ±8).
        const int n = 44100;
        var mix = new float[n * 2];
        for (var voice = 0; voice < 8; voice++)
            for (var i = 0; i < n; i++)
            {
                var s = voice % 2 == 0 ? MathF.Sin(i * (0.01f + voice * 0.003f)) : (i / (40 + voice) % 2 == 0 ? 1f : -1f);
                mix[2 * i] += s;
                mix[2 * i + 1] += -s;
            }
        var inputPeak = mix.Max(MathF.Abs);
        SoftClipSampleProvider.Process(mix);
        var peak = mix.Max(MathF.Abs);
        var quiet = new[] { 0f, 0.1f, -0.5f, 0.79f, SoftClipSampleProvider.Knee };
        var untouched = quiet.All(x => SoftClipSampleProvider.Shape(x) == x);
        // Continuous and monotonic across the knee: no click where the curve starts, louder in is never quieter out.
        var steps = Enumerable.Range(0, 4000).Select(i => -4f + i * 0.002f).Select(SoftClipSampleProvider.Shape).ToList();
        var monotonic = steps.Zip(steps.Skip(1)).All(p => p.Second >= p.First);
        var maxJump = steps.Zip(steps.Skip(1)).Max(p => p.Second - p.First);
        Check($"audio: mixing 8 full-scale buffers (peak {inputPeak:0.00}) stays within full scale after the soft clip (peak {peak:0.0000}); below the knee is untouched ({untouched}); smooth ({monotonic}, max step {maxJump:0.0000})",
            peak <= 1f && untouched && monotonic && maxJump <= 0.0021f && SoftClipSampleProvider.Shape(float.MaxValue) <= 1f
            && SoftClipSampleProvider.Shape(-1.12f) is > -1f and < -0.9f);
    }
}
