using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using Apex.Editor.Commands;
using Apex.Editor.ViewModels;
using Apex.Editor.Views;
using K = Avalonia.Input.Key;

namespace Apex.Shots;

/// <summary>
/// The shell after the October design review, driven with real input: Save all on the title bar's change chip, the
/// window title, banners (one per problem, under the tabs, notices that go and errors that stay), the tab strip's close
/// buttons and overflow, the new-asset dialog's GDT search, the start page, and the crash banner's wording.
/// </summary>
public partial class Program
{
    private static void RunShellReviewChecks(string outDir)
    {
        var vm = new MainViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1600, Height = 1000 };
        window.Show();
        window.Activate();
        window.UpdateLayout();
        Pump();

        // ── Start page: no second command box, names first, no second Pinned list, Save all among the keys ──
        var start = window.FindControl<StackPanel>("StartPage")!;
        Check("start page: the title bar's command box isn't repeated on it",
            !start.GetVisualDescendants().OfType<Button>().Any(b => b.Classes.Contains("commandbox")));
        Check("start page: no Pinned list (the Explorer has it)",
            !start.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "Pinned"));
        Check($"window title: 'Apex' with nothing open ('{window.Title}')", window.Title == "Apex");
        var boxText = window.FindControl<TextBlock>("CommandBoxText")!;
        boxText.Measure(Size.Infinity);
        Check($"palette: the command box's text fits its box, and the full sentence is the palette's placeholder ('{boxText.Text}')",
            boxText.Text == CommandCatalog.PaletteBoxText && boxText.DesiredSize.Width <= boxText.Bounds.Width + 0.5
            && vm.PalettePlaceholder == CommandCatalog.PaletteSentence);

        // ── Chip: Save all is the change chip's main action; the count lists the changed assets ──
        var chipSave = window.FindControl<Button>("ChipSaveButton")!;
        Check("chip: no Save all while there is nothing to save", !chipSave.IsEffectivelyVisible);
        vm.OpenByName("wpn_snp_ballista");
        Pump(50);
        Check($"window title: the open asset's name ('{window.Title}')", window.Title == "wpn_snp_ballista — Apex");
        FocusNumber(window, vm, "damage");
        Key(window, K.Up, RawInputModifiers.Control);
        Pump();
        window.UpdateLayout();
        Check($"window title: marked while the asset has unsaved changes ('{window.Title}')", window.Title == "● wpn_snp_ballista — Apex");
        Check($"chip: Save all shows once there is something to save ({vm.SessionStateText})", chipSave.IsEffectivelyVisible && vm.HasSessionChanges);
        Click(window, chipSave);
        Check($"chip: a real click on Save all saves (sample data says it can't: '{vm.Status}')", vm.Status.StartsWith("This is sample data"));
        Capture(window, Path.Combine(outDir, "45-chip-save-all.png"));
        var count = window.FindControl<Button>("SessionCountButton")!;
        Click(window, count);
        Check($"chip: a real click on the count lists the changed assets ('{vm.FilterText}')", vm.FilterText == "is:changed");
        vm.FilterText = "";
        vm.ApplyFilterNow();

        // ── Banners: one per problem, newest on top, under the tab strip; notices go, errors stay ──
        var lifetime = AlertItem.NoticeLifetime;
        AlertItem.NoticeLifetime = TimeSpan.FromMilliseconds(400);
        try
        {
            vm.ShowNotice("A notice that goes by itself.");
            vm.ShowNotice("An error that stays until dismissed.", isError: true);
            vm.ShowNotice("An error that stays until dismissed.", isError: true); // the same words again: no copy
            Pump();
            window.UpdateLayout();
            var host = window.FindControl<ItemsControl>("AlertHost")!;
            var tabs = window.FindControl<ListBox>("TabList")!;
            var editorHead = window.GetVisualDescendants().OfType<AssetEditorView>().First();
            var hostTop = host.TranslatePoint(new Point(0, 0), window)!.Value.Y;
            var tabsBottom = editorHead.TranslatePoint(new Point(0, 48), window)!.Value.Y;
            Check($"banners: two problems, two banners, newest on top ({string.Join(" | ", vm.Alerts.Select(a => a.Text))})",
                vm.Alerts.Count == 2 && vm.Alerts[0].IsError && !vm.Alerts[1].IsError);
            Check($"banners: clear of the tab strip and the editor's header, which stay reachable (banners at {hostTop:F0}, header ends at {tabsBottom:F0})", hostTop >= tabsBottom);
            Capture(window, Path.Combine(outDir, "46-banners.png"));
            Pump(700);
            Check($"banners: the notice went by itself, the error stayed ({string.Join(" | ", vm.Alerts.Select(a => a.Text))})",
                vm.Alerts.Count == 1 && vm.Alerts[0].IsError);

            // A notice under the pointer stays while it is read.
            vm.ShowNotice("A notice being read.");
            Pump();
            window.UpdateLayout();
            var reading = host.GetVisualDescendants().OfType<Border>()
                .First(b => b.DataContext is AlertItem { Text: "A notice being read." } && b.Classes.Contains("alert"));
            var over = reading.TranslatePoint(new Point(12, reading.Bounds.Height / 2), window)!.Value;
            window.MouseMove(over);
            Pump(700);
            var held = vm.Alerts.Any(a => a.Text == "A notice being read.");
            window.MouseMove(new Point(4, 990));
            Pump(700);
            Check($"banners: a notice stays while the pointer rests on it ({held}) and goes once it leaves",
                held && vm.Alerts.All(a => a.Text != "A notice being read."));

            // The error's ✕, clicked for real.
            var dismiss = host.GetVisualDescendants().OfType<Button>().First(b => b.Name == "AlertDismiss" && b.IsEffectivelyVisible);
            Click(window, dismiss);
            Check("banners: a real click on ✕ dismisses the error", !vm.IsAlertOpen);
        }
        finally
        {
            AlertItem.NoticeLifetime = lifetime;
        }

        // ── Crash banner: names what stopped ──
        vm.Registry[CommandCatalog.CopyName].Execute();
        vm.ReportRecovered(new InvalidOperationException("boom"), null);
        Check($"crash banner: names the command that stopped ('{vm.AlertText}')",
            vm.AlertText.StartsWith("“Copy name” stopped") && !vm.AlertText.Contains("boom") && vm.AlertDetail?.Contains("boom") == true);
        vm.DismissAlertCommand.Execute(null);

        // ── Tab strip: inactive tabs keep no room for ✕; hovering one shows it over its end without moving anything ──
        foreach (var name in new[] { "wpn_ar_havoc_zm", "wpn_smg_riot", "wpn_snp_locus", "wpn_ar_kestrel_zm" })
            vm.OpenByName(name);
        Pump(50);
        window.UpdateLayout();
        var strip = window.FindControl<ListBox>("TabList")!;
        ListBoxItem TabOf(string name) => strip.GetRealizedContainers().OfType<ListBoxItem>().First(c => (c.DataContext as AssetEditorViewModel)?.Name == name);
        Button Slot(ListBoxItem tab) => tab.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("tabclose"));
        var inactive = TabOf("wpn_smg_riot");
        var next = TabOf("wpn_snp_locus");
        var active = TabOf("wpn_ar_kestrel_zm");
        Check("tabs: no ✕ shows until the pointer is on its tab, current or not",
            !Slot(active).IsEffectivelyVisible && !Slot(inactive).IsEffectivelyVisible);
        // Clicking a tab makes it current without changing any tab's width, so its neighbours stay under the cursor.
        var widths = strip.GetRealizedContainers().OfType<ListBoxItem>().Select(c => c.Bounds.Width).ToList();
        Click(window, inactive);
        Pump();
        window.UpdateLayout();
        var widthsAfter = strip.GetRealizedContainers().OfType<ListBoxItem>().Select(c => c.Bounds.Width).ToList();
        Check($"tabs: clicking a tab makes it current and no tab changes width ({string.Join(",", widths.Select(w => w.ToString("F0")))} → {string.Join(",", widthsAfter.Select(w => w.ToString("F0")))})",
            vm.ActiveTab?.Name == "wpn_smg_riot" && widths.Count == widthsAfter.Count && widths.Zip(widthsAfter).All(p => Math.Abs(p.First - p.Second) < 0.5));
        Click(window, active);
        Pump();
        window.UpdateLayout();
        var widthBefore = inactive.Bounds.Width;
        var nextBefore = next.TranslatePoint(new Point(0, 0), window)!.Value.X;
        window.MouseMove(inactive.TranslatePoint(new Point(inactive.Bounds.Width / 2, inactive.Bounds.Height / 2), window)!.Value);
        Pump();
        window.UpdateLayout();
        var close = Slot(inactive);
        Check($"tabs: hovering an inactive tab shows its ✕ over its end, nothing moves (width {widthBefore:F0} → {inactive.Bounds.Width:F0}, next tab at {nextBefore:F0} → {next.TranslatePoint(new Point(0, 0), window)!.Value.X:F0})",
            Slot(inactive).IsEffectivelyVisible && close.IsEffectivelyVisible && Math.Abs(inactive.Bounds.Width - widthBefore) < 0.5
            && Math.Abs(next.TranslatePoint(new Point(0, 0), window)!.Value.X - nextBefore) < 0.5);
        Capture(window, Path.Combine(outDir, "47-tab-hover-close.png"));
        var at = close.TranslatePoint(new Point(close.Bounds.Width / 2, close.Bounds.Height / 2), window)!.Value;
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        Pump();
        Check($"tabs: a real click on that ✕ closes that tab, not the active one ({string.Join(", ", vm.OpenTabs.Select(t => t.Name))})",
            vm.OpenTabs.All(t => t.Name != "wpn_smg_riot") && vm.ActiveTab?.Name == "wpn_ar_kestrel_zm");

        // ── Overflow: which side, and the list in strip order ──
        foreach (var a in DatabaseOf(vm).Assets.Where(a => a.Type == "weapon").Take(16))
            vm.OpenAsset(a);
        vm.ActivateTabCommand.Execute(vm.OpenTabs[vm.OpenTabs.Count / 2]);
        Pump(50);
        window.UpdateLayout();
        Pump();
        var overflow = window.FindControl<Button>("TabOverflowButton")!;
        var overflowText = window.FindControl<TextBlock>("TabOverflowText")!.Text ?? "";
        Check($"tab overflow: the button says which side the hidden tabs are on ('{overflowText}')",
            overflow.IsEffectivelyVisible && (overflowText.Contains('‹') || overflowText.Contains('›')));
        Click(window, overflow);
        var menu = FlyoutBase.GetAttachedFlyout(overflow) as MenuFlyout;
        var listed = menu?.Items.OfType<MenuItem>().Select(i => (i.CommandParameter as AssetEditorViewModel)?.Name).ToList() ?? new();
        Check($"tab overflow: the list is every tab in strip order ({listed.Count} of {vm.OpenTabs.Count})",
            listed.SequenceEqual(vm.OpenTabs.Select(t => (string?)t.Name)));
        Capture(window, Path.Combine(outDir, "48-tab-overflow-menu.png"));
        menu?.Hide();
        Pump();

        // ── New asset: the GDT is searched by its short name ──
        Key(window, K.N, RawInputModifiers.Control);
        Pump();
        Check("new asset: Ctrl+N opens it", vm.IsNewOpen);
        window.KeyTextInput("apex_new_from_search");
        var search = window.FindControl<TextBox>("NewGdtSearch")!;
        Click(window, search);
        window.KeyTextInput("zm_weap");
        Pump();
        var gdtList = window.FindControl<ListBox>("NewGdtList")!;
        window.UpdateLayout();
        var firstRow = gdtList.ContainerFromIndex(0);
        var firstTexts = firstRow?.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList() ?? new();
        Check($"new asset: typing narrows the GDTs to short-name matches ({string.Join(", ", vm.NewGdtMatches.Select(m => m.Short))})",
            vm.NewGdtMatches.Count > 0 && vm.NewGdtMatches.All(m => m.Name.Contains("zm_weap", StringComparison.OrdinalIgnoreCase))
            && vm.NewGdtMatches[0].Short.StartsWith("zm_weap") && vm.NewGdtName == vm.NewGdtMatches[0].Name);
        Check($"new asset: a row leads with the GDT's short name ({string.Join(" | ", firstTexts)})",
            firstTexts.FirstOrDefault() == vm.NewGdtMatches[0].Short);
        Capture(window, Path.Combine(outDir, "49-new-asset-gdt-search.png"));
        search.Text = "";
        Pump();
        // Cleared, every GDT is listed again and the highlight stays on the GDT the search found.
        var chosen = vm.NewGdtName;
        var index = vm.NewGdtMatches.Select(m => m.Name).ToList().IndexOf(chosen!);
        var (there, back) = index == vm.NewGdtMatches.Count - 1 ? (K.Up, K.Down) : (K.Down, K.Up);
        Key(window, there);
        var moved = vm.NewGdtName;
        var expected = vm.NewGdtMatches[index + (there == K.Down ? 1 : -1)].Name;
        Key(window, back);
        Check($"new asset: ↓ and ↑ in the search walk the list ({chosen} → {moved} → {vm.NewGdtName})",
            index >= 0 && moved == expected && vm.NewGdtName == chosen && gdtList.SelectedItem == vm.NewGdtChoice);
        Key(window, K.Enter);
        Pump();
        Check($"new asset: Enter creates it in the GDT picked ({vm.ActiveTab?.Name} in {vm.ActiveTab?.GdtName})",
            !vm.IsNewOpen && vm.ActiveTab?.Name == "apex_new_from_search" && vm.ActiveTab.Record.GdtName == chosen);

        window.Close();
    }
}
