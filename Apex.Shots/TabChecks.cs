using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using Apex.Editor.ViewModels;
using Apex.Editor.Views;

namespace Apex.Shots;

/// <summary>
/// Tabs that leave the strip (closed, or a preview replaced) must be let go of entirely: by the workspace's own lists,
/// by the editor's recycled rows and by the Inspector's. And the editor rows' details on demand (− + steps,
/// ↶) show on the hovered row only, driven with real pointer input.
/// </summary>
public partial class Program
{
    private static void RunTabChecks()
    {
        var vm = new MainViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1600, Height = 1000 };
        window.Show();
        window.Activate();
        Settle(window);

        // ── Details on demand: a number row's step arrows show under the pointer and hide when it leaves ──
        vm.OpenByName("wpn_ar_havoc_zm_upgraded");
        Settle(window);
        var form = window.GetVisualDescendants().OfType<AssetEditorView>().First().FindControl<ItemsControl>("Form")!;
        var rows = form.GetRealizedContainers().Where(c => c.IsEffectivelyVisible && c.DataContext is NumberPropertyViewModel).Take(2).ToList();
        if (rows.Count < 2)
            Check($"details on demand: the weapon shows two number rows ({rows.Count})", false);
        else
        {
            List<Button> Steps(Control row) => row.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("scrubstep")).ToList();
            var away = CentreOf(window, window.GetVisualDescendants().OfType<AssetBrowserView>().First());
            void MoveTo(Point p)
            {
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                RawInput.Send(window, "MouseMove", p, RawInputModifiers.None);
                Settle(window);
            }
            MoveTo(away);
            var hiddenAtRest = rows.All(r => Steps(r).All(b => !b.IsEffectivelyVisible));
            MoveTo(CentreOf(window, rows[0]));
            var shownOnHover = Steps(rows[0]).Count == 2 && Steps(rows[0]).All(b => b.IsEffectivelyVisible);
            var otherHidden = Steps(rows[1]).All(b => !b.IsEffectivelyVisible);
            MoveTo(away);
            var hiddenAfter = Steps(rows[0]).All(b => !b.IsEffectivelyVisible);
            Check($"details on demand: step arrows hidden at rest ({hiddenAtRest}), shown on the hovered row ({shownOnHover}) only ({otherHidden}), hidden once the pointer leaves ({hiddenAfter})",
                hiddenAtRest && shownOnHover && otherHidden && hiddenAfter);
        }

        // ── Closed and replaced tabs are let go of ──
        // One pinned tab stays open throughout, so the editor view (and its pooled rows) live on, as in the app.
        var anchor = vm.ActiveTab!;
        var assets = DatabaseOf(vm).Assets.Where(a => a != anchor.Record).Take(24).ToList();
        var gone = Cycle(vm, window, assets, anchor);
        Settle(window);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Settle(window);
        for (var i = 0; i < 3; i++)
        {
            GC.Collect(2, GCCollectionMode.Forced, true, true);
            GC.WaitForPendingFinalizers();
        }
        var alive = gone.Count(w => w.IsAlive);
        Check($"tabs: every closed or replaced tab is let go of ({alive} of {gone.Count} still reachable after a full collection{string.Concat(gone.Where(w => w.IsAlive).Take(3).Select(w => "; " + (w.Target as AssetEditorViewModel)?.Name))})",
            alive == 0 && gone.Count > 12);

        window.Close();
    }

    /// <summary>
    /// Opens <paramref name="assets"/> in turn, every other one into the preview tab and the rest pinned then closed by
    /// their ✕, and returns weak references to every tab that left the strip. Kept out of line so no local roots one.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static List<WeakReference> Cycle(MainViewModel vm, Window window, List<Apex.Editor.Models.AssetRecord> assets, AssetEditorViewModel anchor)
    {
        var gone = new List<WeakReference>();
        var tabList = window.FindControl<ListBox>("TabList")!;
        for (var i = 0; i < assets.Count; i++)
        {
            var before = vm.OpenTabs.ToList();
            if (i % 2 == 0)
            {
                vm.OpenAsset(assets[i], preview: true);
                Settle(window);
                gone.AddRange(before.Where(t => !vm.OpenTabs.Contains(t)).Select(t => new WeakReference(t)));
                continue;
            }
            vm.OpenAsset(assets[i]);
            Settle(window);
            var tab = vm.ActiveTab!;
            // Hit-testing reads the drawn scene, a frame behind the strip that just changed.
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Settle(window);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Settle(window);
            // The ✕ shows with the pointer on its tab.
            var row = tabList.ContainerFromItem(tab)!;
            window.MouseMove(CentreOf(window, row));
            Settle(window);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Settle(window);
            var close = row.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("tabclose"));
            ClickAt(window, CentreOf(window, close));
            Settle(window);
            if (!vm.OpenTabs.Contains(tab))
                gone.Add(new WeakReference(tab));
            else
                Check($"tabs: the ✕ on {tab.Name} closed it", false);
        }
        foreach (var t in vm.OpenTabs.Where(t => t != anchor).ToList())
        {
            gone.Add(new WeakReference(t));
            t.CloseCommand.Execute(null);
        }
        return gone;
    }
}
