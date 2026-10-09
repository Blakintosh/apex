using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Apex.Editor.ViewModels;
using Apex.Editor.Views;

namespace Apex.Shots;

/// <summary>
/// The shell's keyboard, pointer and focus behaviour driven with real input (headless key and mouse
/// events on the actual controls), on a fresh window so earlier scenarios can't colour the result.
/// </summary>
public partial class Program
{
    private static void RunShellChecks(string outDir)
    {
        var vm = new MainViewModel();
        var window = new MainWindow { DataContext = vm };
        window.Show();
        window.Activate();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        var browser = window.GetVisualDescendants().OfType<AssetBrowserView>().First();
        var tree = browser.FindControl<ListBox>("Tree")!;
        var search = browser.FindControl<TextBox>("SearchBox")!;
        BrowserNode? Selected() => tree.SelectedItem as BrowserNode;

        // ── F-2: arrowing through collapsed groups moves, never expands ─────────
        browser.FocusTree();
        Pump();
        var rowsBefore = vm.FlatRows.Count;
        Key(window, Avalonia.Input.Key.Down);
        Key(window, Avalonia.Input.Key.Down);
        Key(window, Avalonia.Input.Key.Down);
        var third = vm.FlatRows.Where(n => n.IsGroup).ElementAt(3);
        Check($"explorer keys: ↓↓↓ selects the 4th GDT row ({Selected()?.Title})", Selected() == third);
        Check($"explorer keys: arrowing expands nothing ({vm.FlatRows.Count} rows, was {rowsBefore})",
            vm.FlatRows.Count == rowsBefore && vm.FlatRows.All(n => !n.IsExpanded));
        Key(window, Avalonia.Input.Key.Right);
        Check("explorer keys: → expands the group", third.IsExpanded && vm.FlatRows.Count > rowsBefore);
        Key(window, Avalonia.Input.Key.Right);
        var child = Selected();
        Check($"explorer keys: → again steps into its first row ({child?.Title})",
            child is not null && child != third && vm.FlatRows.IndexOf(child) == vm.FlatRows.IndexOf(third) + 1);
        Key(window, Avalonia.Input.Key.Left);
        Check($"explorer keys: ← on a row jumps to its group ({Selected()?.Title})", Selected() == third && third.IsExpanded);
        Key(window, Avalonia.Input.Key.Left);
        Check("explorer keys: ← on the open group collapses it", !third.IsExpanded && vm.FlatRows.Count == rowsBefore);
        Key(window, Avalonia.Input.Key.Enter);
        Check("explorer keys: Enter opens a group", third.IsExpanded);
        Key(window, Avalonia.Input.Key.Enter);
        Check("explorer keys: Enter closes it again", !third.IsExpanded);

        // ── F-2: a click on a group row opens it; a double-click leaves it open ──
        Click(window, RowContainer(tree, vm, third), MouseButton.Left);
        Check("explorer click: a group row opens on click", third.IsExpanded);
        Click(window, RowContainer(tree, vm, third), MouseButton.Left);
        Check("explorer click: and closes on the next click", !third.IsExpanded);

        // ── F-14: right-click selects for the context menu, never opens or toggles ──
        var weapons = vm.FlatRows.First(n => n.Gdt?.Name == "t7_weapons.gdt");
        Click(window, RowContainer(tree, vm, weapons), MouseButton.Right);
        Check("explorer right-click: a group row neither opens nor closes", !weapons.IsExpanded && Selected() == weapons);
        var menu = tree.ContextMenu!;
        Check("explorer right-click: groups get a menu with New asset in this GDT…",
            menu.IsOpen && menu.Items.OfType<MenuItem>().Any(i => i.Name == "MenuNewIn" && i.IsVisible));
        menu.Close();
        Pump();
        vm.ToggleNode(weapons);
        Pump();
        var assetRow = vm.FlatRows.First(n => n.Asset?.Name == "wpn_ar_kestrel");
        Click(window, RowContainer(tree, vm, assetRow), MouseButton.Right);
        Pump(250); // past the keyboard open delay, so a deferred open would have fired too
        Check($"explorer right-click: an asset row is selected, not opened (active: {vm.ActiveTab?.Name ?? "none"})",
            Selected() == assetRow && vm.ActiveTab is null && tree.ContextMenu!.IsOpen);
        Check("explorer right-click: asset menu offers Rename…, Duplicate, Delete",
            tree.ContextMenu!.Items.OfType<MenuItem>().Where(i => i.IsVisible).Select(i => i.Header as string)
                .Intersect(new[] { "Rename…", "Duplicate", "Delete" }).Count() == 3);
        tree.ContextMenu!.Close();
        Pump();
        Click(window, RowContainer(tree, vm, assetRow), MouseButton.Left);
        Check($"explorer click: a left click opens the asset ({vm.ActiveTab?.Name})", vm.ActiveTab?.Record == assetRow.Asset);

        // ── F-13: F2 and Delete on the tree act on the selected asset ─────────
        RowContainer(tree, vm, assetRow).Focus();
        Key(window, Avalonia.Input.Key.F2);
        var renameBox = window.FindControl<TextBox>("RenameBox")!;
        Check("explorer F2: opens Rename with its box focused", vm.IsRenameOpen && renameBox.IsFocused);

        // ── F-9: a taken name keeps the dialog open with one line, and the typed name ──
        renameBox.SelectAll();
        window.KeyTextInput("wpn_ar_vireo");
        Key(window, Avalonia.Input.Key.Enter);
        Pump(100);
        Check($"rename: taken name keeps the dialog open — '{vm.RenameError}'",
            vm.IsRenameOpen && vm.RenameError == "wpn_ar_vireo already exists." && renameBox.Text == "wpn_ar_vireo");

        // ── F-4: Tab stays inside the dialog; window shortcuts wait behind it ──
        var renameCard = window.FindControl<Border>("RenameCard")!;
        var escaped = false;
        for (var i = 0; i < 8; i++)
        {
            Key(window, Avalonia.Input.Key.Tab);
            escaped |= window.FocusManager?.GetFocusedElement() is not Visual f || !renameCard.IsVisualAncestorOf(f);
        }
        Check("dialog: Tab cycles inside the rename card", !escaped);
        var tabs = vm.OpenTabs.Count;
        Key(window, Avalonia.Input.Key.W, RawInputModifiers.Control);
        Key(window, Avalonia.Input.Key.N, RawInputModifiers.Control);
        Check("dialog: Ctrl+W and Ctrl+N do nothing behind it", vm.OpenTabs.Count == tabs && !vm.IsNewOpen && vm.IsRenameOpen);

        // ── F-5: Esc returns focus to the row the dialog was opened from ──
        Key(window, Avalonia.Input.Key.Escape);
        Pump();
        var focused = window.FocusManager?.GetFocusedElement() as Visual;
        Check($"dialog: Esc hands focus back to the Explorer ({focused?.GetType().Name})",
            !vm.IsRenameOpen && focused is not null && tree.IsVisualAncestorOf(focused));
        Key(window, Avalonia.Input.Key.N, RawInputModifiers.Control);
        Check("dialog: shortcuts come back after it closes (Ctrl+N)", vm.IsNewOpen);
        Key(window, Avalonia.Input.Key.Escape);
        Pump();

        // Delete on a tree row: no question, one undo step. Nothing references the copy, so once the corpus check is back
        // it is gone; Ctrl+Z puts it back in the tree.
        vm.DuplicateAsset(assetRow.Asset!);
        Pump();
        var lone = vm.FlatRows.First(n => n.Asset?.Name == "wpn_ar_kestrel_copy");
        var loneName = lone.Asset!.Name;
        Click(window, RowContainer(tree, vm, lone), MouseButton.Left);
        Key(window, Avalonia.Input.Key.Delete);
        Check("explorer Del: no confirmation", !vm.IsConfirmOpen && !vm.IsModalOpen);
        WaitFor(() => !vm.IsDeletePending);
        Pump();
        Check($"explorer Del: the selected asset is deleted and the status line says how to take it back ('{vm.Status}')",
            vm.FlatRows.All(n => n.Asset?.Name != loneName) && vm.OpenTabs.All(t => t.Name != loneName)
            && vm.Status == $"Deleted {loneName} · Ctrl+Z restores it");
        Key(window, Avalonia.Input.Key.Z, RawInputModifiers.Control);
        vm.ApplyFilterNow();
        Pump();
        Check($"explorer Del: Ctrl+Z restores it, row and tab ('{vm.Status}')",
            vm.FlatRows.Any(n => n.Asset?.Name == loneName) && vm.OpenTabs.Any(t => t.Name == loneName) && vm.Status == $"Restored {loneName}");

        // A blocked delete (something derives from it) says so at once and offers the referrer; nothing is removed.
        var parentRow = vm.FlatRows.FirstOrDefault(n => n.Asset?.Name == "wpn_ar_havoc_zm");
        if (parentRow is not null)
        {
            Click(window, RowContainer(tree, vm, parentRow), MouseButton.Left);
            Key(window, Avalonia.Input.Key.Delete);
            Pump();
            Check($"explorer Del: a referenced asset isn't deleted, and the banner offers the referrer ('{vm.AlertText}', '{vm.AlertActionLabel}')",
                !vm.IsDeletePending && vm.AlertIsError && vm.AlertText.StartsWith("Can't delete wpn_ar_havoc_zm") && vm.HasAlertAction
                && vm.FlatRows.Any(n => n.Asset?.Name == "wpn_ar_havoc_zm"));
            Key(window, Avalonia.Input.Key.Escape);
            Pump();
        }

        // ── F-12: Enter in the search box opens the top result as a kept tab ──
        search.Focus();
        window.KeyTextInput("havoc_zm");
        Key(window, Avalonia.Input.Key.Enter);
        Pump();
        var top = vm.FlatRows.FirstOrDefault(n => n.Asset is not null)?.Asset;
        Check($"search Enter: opens the top result ({vm.ActiveTab?.Name}, top {top?.Name})",
            top is not null && vm.ActiveTab?.Record == top && vm.ActiveTab is { IsPreview: false });

        // ── F-10: clearing the search applies without waiting for the debounce ──
        Key(window, Avalonia.Input.Key.Escape);
        Check("search Esc: the tree is back at once", !vm.IsSearchResults && vm.FlatRows.Any(n => n.Gdt is not null));

        // ── F-5: Esc out of Quick Open returns focus to the search box ──
        search.Focus();
        Key(window, Avalonia.Input.Key.P, RawInputModifiers.Control);
        Pump();
        var paletteBox = window.FindControl<TextBox>("PaletteBox")!;
        Check("palette: Ctrl+P focuses its box", vm.IsPaletteOpen && paletteBox.IsFocused);
        Key(window, Avalonia.Input.Key.Escape);
        Pump();
        Check("palette: Esc gives focus back to where it was", !vm.IsPaletteOpen && search.IsFocused);

        // ── F-22: only a left click on a row opens it ─────────────────────────
        Key(window, Avalonia.Input.Key.P, RawInputModifiers.Control);
        Pump();
        window.KeyTextInput("wpn_snp");
        Pump();
        var paletteList = window.FindControl<ListBox>("PaletteList")!;
        var firstItem = paletteList.ContainerFromIndex(0)!;
        var active = vm.ActiveTab;
        Click(window, firstItem, MouseButton.Right);
        Check("palette: right-click on a row doesn't open it", vm.IsPaletteOpen && vm.ActiveTab == active);
        var target = vm.PaletteResults[0].Record;
        Click(window, paletteList.ContainerFromIndex(0)!, MouseButton.Left);
        Pump();
        var inEditor = window.FocusManager?.GetFocusedElement() as Visual;
        Check($"palette: a left click opens it ({vm.ActiveTab?.Name})", !vm.IsPaletteOpen && vm.ActiveTab?.Record == target);
        Check($"palette: the opened asset's editor has the keyboard ({inEditor?.GetType().Name})",
            inEditor?.FindAncestorOfType<AssetEditorView>() is not null);

        // ── F-24: Ctrl+Tab by recency, Visual Studio style ─────────────────────
        vm.OpenByName("wpn_ar_havoc");
        vm.OpenByName("wpn_smg_riot");
        vm.OpenByName("wpn_snp_locus");
        Pump();
        CtrlTab(window, 1);
        Check($"Ctrl+Tab: flips to the previous tab ({vm.ActiveTab?.Name})", vm.ActiveTab?.Name == "wpn_smg_riot");
        CtrlTab(window, 1);
        Check($"Ctrl+Tab: again flips back ({vm.ActiveTab?.Name})", vm.ActiveTab?.Name == "wpn_snp_locus");
        CtrlTab(window, 2);
        Check($"Ctrl+Tab ×2 with Ctrl held: two back ({vm.ActiveTab?.Name})", vm.ActiveTab?.Name == "wpn_ar_havoc");

        // ── F-15: title-bar undo follows the active tab's history ─────────────
        var undo = window.FindControl<Button>("UndoButton")!;
        Check("undo button: disabled with nothing to undo", !undo.IsEffectivelyEnabled);
        vm.ActiveTab!.AllSentinel.All.First(p => p.Key == "damage").RawValue = "77";
        Pump();
        Check("undo button: enabled after an edit", undo.IsEffectivelyEnabled);
        Click(window, undo, MouseButton.Left);
        var damage = vm.ActiveTab.AllSentinel.All.First(p => p.Key == "damage").RawValue;
        Check($"undo button: a click undoes ({damage}), then it disables again (enabled: {undo.IsEffectivelyEnabled}, can undo: {vm.ActiveTab.CanUndo})",
            damage != "77" && !undo.IsEffectivelyEnabled);

        // ── F-18: no Ctrl+Alt shortcuts; the replacements work ────────────────
        Check("shortcuts: none use Ctrl+Alt (AltGr)",
            Apex.Editor.Commands.CommandCatalog.All.SelectMany(c => c.Gestures)
                .All(g => !(g.KeyModifiers.HasFlag(KeyModifiers.Control) && g.KeyModifiers.HasFlag(KeyModifiers.Alt))));
        Key(window, Avalonia.Input.Key.D2, RawInputModifiers.Alt);
        Check("shortcuts: Alt+2 groups by type", vm.Grouping == ExplorerGrouping.Type);
        Key(window, Avalonia.Input.Key.D1, RawInputModifiers.Alt);
        var inspector = vm.IsInspectorVisible;
        Key(window, Avalonia.Input.Key.B, RawInputModifiers.Control | RawInputModifiers.Shift);
        Check("shortcuts: Ctrl+Shift+B toggles the Inspector", vm.IsInspectorVisible != inspector);
        Key(window, Avalonia.Input.Key.B, RawInputModifiers.Control | RawInputModifiers.Shift);

        // ── F-3: Ctrl+N asks for a name, defaulting to what's open ─────────────
        Key(window, Avalonia.Input.Key.N, RawInputModifiers.Control);
        Pump();
        var newBox = window.FindControl<TextBox>("NewNameBox")!;
        Check($"new asset: dialog defaults to the open asset's type and GDT ({vm.NewType}, {vm.NewGdtName})",
            vm.IsNewOpen && newBox.IsFocused && vm.NewType == vm.ActiveTab!.Record.Type && vm.NewGdtName == vm.ActiveTab.GdtName);
        window.KeyTextInput("wpn_riot_shield");
        Capture(window, Path.Combine(outDir, "30-new-asset-dialog.png"));
        Key(window, Avalonia.Input.Key.Enter);
        Check($"new asset: Enter creates it ({vm.ActiveTab?.Name} in {vm.ActiveTab?.GdtName})",
            !vm.IsNewOpen && vm.ActiveTab?.Name == "wpn_riot_shield" && vm.ActiveTab.GdtName == "t7_weapons.gdt");

        // ── F-7: the reference scan covers the corpus ─────────────────────────
        var used = vm.FindReferencesAsync(vm.OpenTabs.First(t => t.Name == "wpn_ar_havoc").Record).GetAwaiter().GetResult();
        Check($"references: wpn_ar_havoc is found as a parent ({used.Count}: {string.Join(", ", used.Take(3).Select(a => a.Name))})",
            used.Any(a => a.Name == "wpn_ar_havoc_zm"));

        // ── F-1 / F-8 / P-4: ranking ──────────────────────────────────────────
        vm.OpenPaletteCommand.Execute(null);
        vm.PaletteText = "wpn_ar_havoc";
        Check($"quick open: the exact name ranks first ({vm.PaletteResults.FirstOrDefault()?.Name})",
            vm.PaletteResults.FirstOrDefault()?.Name == "wpn_ar_havoc");
        vm.PaletteText = "";
        Check("quick open: nothing typed lists the open tabs, newest first",
            vm.PaletteResults.Select(r => r.Record).SequenceEqual(vm.TabsByRecency.Select(t => t.Record).Take(12)));
        vm.ClosePaletteCommand.Execute(null);
        vm.OpenRecentPaletteCommand.Execute(null);
        Check($"Ctrl+E: lists recently opened assets instead ({vm.PaletteResults.Count}, '{vm.PaletteHint}')",
            vm.PaletteHint.StartsWith("Recently opened") && vm.PaletteResults.All(r => r.Record != vm.ActiveTab?.Record));
        vm.PaletteText = ">group";
        Check($"commands: a word start beats a scattered match ({vm.PaletteResults.FirstOrDefault()?.Name})",
            vm.PaletteResults.FirstOrDefault()?.Name.StartsWith("Explorer: Group") == true);
        vm.ClosePaletteCommand.Execute(null);

        // ── F-20: facets use GDT type names and stay on one line ──────────────
        vm.FilterText = "havoc";
        vm.ApplyFilterNow();
        Check($"facets: GDT type names ({string.Join(" · ", vm.SearchFacets.Select(f => f.Label))})",
            vm.SearchFacets.All(f => !f.Label.EndsWith("models") && !f.Label.EndsWith("anims") && !f.Label.EndsWith("fxs")));
        vm.FilterText = "zzzz_nothing";
        vm.ApplyFilterNow();
        Check("facets: none when nothing matched", vm.SearchFacets.Count == 0);
        vm.FilterText = "";

        Capture(window, Path.Combine(outDir, "36-shell-after-input.png"));
        window.Close();
    }

    private static void Key(Window window, Key key, RawInputModifiers mods = RawInputModifiers.None)
    {
        window.KeyPress(key, mods, PhysicalKey.None, null);
        window.KeyRelease(key, mods, PhysicalKey.None, null);
        Pump();
    }

    /// <summary>Holds Ctrl, presses Tab <paramref name="presses"/> times, lets go of Ctrl.</summary>
    private static void CtrlTab(Window window, int presses)
    {
        window.KeyPress(Avalonia.Input.Key.LeftCtrl, RawInputModifiers.Control, PhysicalKey.ControlLeft, null);
        for (var i = 0; i < presses; i++)
        {
            window.KeyPress(Avalonia.Input.Key.Tab, RawInputModifiers.Control, PhysicalKey.Tab, null);
            window.KeyRelease(Avalonia.Input.Key.Tab, RawInputModifiers.Control, PhysicalKey.Tab, null);
            Pump();
        }
        window.KeyRelease(Avalonia.Input.Key.LeftCtrl, RawInputModifiers.None, PhysicalKey.ControlLeft, null);
        Pump();
    }

    private static int _clickNudge;

    /// <summary>A real click in the middle of <paramref name="target"/> (nudged each time so two clicks never pair into a double-click).</summary>
    private static void Click(Window window, Control target, MouseButton button)
    {
        Pump();
        // The nudge stays on the target: on a narrow one (the 27 px ⋯ button) 18 px missed it, so whether a click landed
        // hung on how many clicks the process had made before (the full run's order happened to land it; a shard didn't).
        var reach = Math.Max(0, target.Bounds.Width / 2 - 2);
        var nudge = Math.Clamp(((_clickNudge++ % 7) - 3) * 6, -reach, reach);
        var point = target.TranslatePoint(new Point(target.Bounds.Width / 2 + nudge, target.Bounds.Height / 2), window)!.Value;
        window.MouseMove(point);
        window.MouseDown(point, button);
        window.MouseUp(point, button);
        Pump();
        System.Threading.Thread.Sleep(600); // past the double-click interval
    }

    private static Control RowContainer(ListBox tree, MainViewModel vm, BrowserNode row)
    {
        tree.ScrollIntoView(row);
        Pump();
        return tree.ContainerFromItem(row)!;
    }
}
