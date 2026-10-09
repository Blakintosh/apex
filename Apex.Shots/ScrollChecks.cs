using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.VisualTree;
using Apex.Editor.Controls;
using Apex.Editor.ViewModels;
using Apex.Editor.Views;

namespace Apex.Shots;

/// <summary>
/// Wheel-scrolling the virtualized lists one tick at a time with real input: the rows on screen must always cover the
/// viewport. Each tick is smaller than RowPanel's buffer, so a panel that judged scrolls tick to tick never realized
/// the next rows and scrolled into blank space.
/// </summary>
public partial class Program
{
    private static void RunScrollChecks()
    {
        var vm = new MainViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1600, Height = 1000 };
        window.Show();
        window.Activate();
        Settle(window);

        var browser = window.GetVisualDescendants().OfType<AssetBrowserView>().First();
        var tree = browser.FindControl<ListBox>("Tree")!;
        var big = vm.FlatRows.First(n => n.Gdt?.Name == "images_env.gdt"); // the mock's largest GDT (480 images)
        vm.ToggleNode(big);
        Settle(window);
        WheelThrough(window, tree, $"Explorer ({big.Title}, {vm.FlatRows.Count} rows)");

        // The whole strip beside the list is the scrollbar's, not only its hairline thumb: a point on the track, well away
        // from the thumb (the list is scrolled to its end, so the thumb is at the bottom), hits the bar.
        var bar = tree.GetVisualDescendants().OfType<ScrollBar>().First(b => b.Orientation == Avalonia.Layout.Orientation.Vertical);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        var onTrack = bar.TranslatePoint(new Point(bar.Bounds.Width / 2, 12), window)!.Value;
        var hit = window.InputHitTest(onTrack) as Visual;
        Check($"scroll: the scrollbar's track takes the pointer across its width ({bar.Bounds.Width:0} px wide; hit {hit?.GetType().Name})",
            hit is not null && (hit == bar || hit.GetVisualAncestors().Contains(bar)));

        vm.OpenByName("wpn_ar_havoc_zm_upgraded");
        Settle(window);
        var form = window.GetVisualDescendants().OfType<AssetEditorView>().First().FindControl<ItemsControl>("Form")!;
        WheelThrough(window, form, $"editor form ({vm.ActiveTab?.Name})");

        window.Close();
    }

    /// <summary>Wheels down to the end of <paramref name="list"/> one tick at a time, checking coverage after each tick.</summary>
    private static void WheelThrough(Window window, Control list, string what)
    {
        var scroll = list.GetVisualDescendants().OfType<ScrollViewer>().First();
        var panel = list.GetVisualDescendants().OfType<RowPanel>().First();
        var at = list.TranslatePoint(new Point(list.Bounds.Width / 2, list.Bounds.Height / 2), window)!.Value;
        if (scroll.Extent.Height <= scroll.Viewport.Height + 1)
        {
            Check($"scroll: {what} is long enough to scroll (extent {scroll.Extent.Height:0}, viewport {scroll.Viewport.Height:0})", false);
            return;
        }

        // Headless hit-testing reads the last drawn frame, so draw one before each input.
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        RawInput.Send(window, "MouseMove", at, RawInputModifiers.None);
        Settle(window);
        int ticks = 0, gaps = 0;
        string? firstGap = null;
        var lastOffset = -1.0;
        while (ticks < 2000 && scroll.Offset.Y > lastOffset)
        {
            lastOffset = scroll.Offset.Y;
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            RawInput.Send(window, "MouseWheel", at, new Vector(0, -1), RawInputModifiers.None);
            // The panel re-realizes from the viewport change the scroll raises, in a layout pass of its own: the next
            // frame in the app, a second pass here.
            Settle(window);
            Settle(window);
            ticks++;
            // In the scroll viewer's own coordinates the view is 0 to its viewport height (or to the list's end).
            var origin = panel.TranslatePoint(default, scroll)!.Value;
            var top = 0.0;
            var bottom = Math.Min(scroll.Viewport.Height, origin.Y + panel.Bounds.Height);
            var shown = panel.Children.Where(c => c.IsVisible).Select(c => c.Bounds.Translate(origin)).ToList();
            var covered = shown.Count > 0 && shown.Min(b => b.Top) <= Math.Max(top, origin.Y) + 0.5 && shown.Max(b => b.Bottom) >= bottom - 0.5;
            if (!covered)
            {
                gaps++;
                firstGap ??= $"tick {ticks} at {scroll.Offset.Y:0}: view {top:0}–{bottom:0}, rows {(shown.Count == 0 ? "none" : $"{shown.Min(b => b.Top):0}–{shown.Max(b => b.Bottom):0}")}";
            }
        }
        Check($"scroll: wheeling {what} tick by tick keeps rows under the whole viewport ({ticks} ticks, reached {scroll.Offset.Y:0} of {scroll.Extent.Height - scroll.Viewport.Height:0}{(firstGap is null ? "" : $"; {gaps} gaps, first at {firstGap}")})",
            gaps == 0 && ticks > 3 && scroll.Offset.Y >= scroll.Extent.Height - scroll.Viewport.Height - 1);
    }
}
