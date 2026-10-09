using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using Apex.Editor.ViewModels;
using Apex.Editor.Views;
using K = Avalonia.Input.Key;

namespace Apex.Shots;

/// <summary>
/// The Set view: only the properties the asset holds a value for, with Add property to bring in another. Driven with
/// real input: the tab, the button, the keys in the list's box.
/// </summary>
public partial class Program
{
    private static void RunSetViewChecks(string outDir)
    {
        var vm = new MainViewModel();
        var window = new MainWindow { DataContext = vm, Width = 1600, Height = 1000 };
        window.Show();
        window.Activate();
        Settle(window);

        // The mock weapons hold a value for every property they show, or inherit it: take one with no parent and drop a few.
        vm.OpenByName("wpn_ar_havoc_zm_upgraded");
        Settle(window);
        var top = vm.ActiveTab!;
        for (var guard = 0; top.Record.Parent is { } parentName && guard < 4; guard++)
        {
            vm.OpenByName(parentName);
            Settle(window);
            top = vm.ActiveTab!;
        }
        var name = top.Name;
        foreach (var key in top.Record.Properties.Keys.Take(6).ToList())
            top.Record.Properties.Remove(key);
        top.CloseCommand.Execute(null);
        Settle(window);
        vm.OpenByName(name);
        Settle(window);
        var tab = vm.ActiveTab!;
        var view = window.GetVisualDescendants().OfType<AssetEditorView>().First(v => v.IsEffectivelyVisible);
        Check($"set view: opens on All, and the Set tab counts fewer than All shows ({tab.SetPropertyCount} of {tab.ShownPropertyCount})",
            tab.View == EditorView.All && tab.SetPropertyCount > 0 && tab.SetPropertyCount < tab.ShownPropertyCount);

        var tabButton = view.GetVisualDescendants().OfType<RadioButton>().First(b => Avalonia.Automation.AutomationProperties.GetName(b)?.StartsWith("Set,") == true);
        var addButton = view.FindControl<Button>("AddPropertyButton")!;
        Check("set view: Add property isn't there in All", !addButton.IsEffectivelyVisible);
        Click(window, tabButton);
        Settle(window);
        var rows = tab.FlatRows.OfType<PropertyItemViewModel>().ToList();
        Check($"set view: clicking Set lists only what the asset holds ({rows.Count} rows)",
            tab.View == EditorView.Set && rows.Count > 0 && rows.All(r => r.IsHeld || r.IsRevealed) && rows.Count <= tab.SetPropertyCount);
        Check("set view: Add property shows beside the tabs", addButton.IsEffectivelyVisible);
        Capture(window, System.IO.Path.Combine(outDir, "70-set-view.png"));

        var before = tab.SetPropertyCount;
        var target = tab.UnsetRows("").FirstOrDefault(p => p.Item is TextPropertyViewModel or NumberPropertyViewModel);
        Check("set view: the asset has an unset property to add", target is not null);
        if (target is null)
        {
            window.Close();
            return;
        }
        Click(window, addButton);
        Settle(window);
        var picker = view.Picker;
        var popup = picker is null ? null : TopLevel.GetTopLevel(picker);
        Check("set view: Add property opens its list", picker is { IsEffectivelyVisible: true } && popup is not null);
        if (picker is null || popup is null)
        {
            window.Close();
            return;
        }
        var box = picker.FindControl<TextBox>("Filter")!;
        Check("set view: the box has the keyboard, so typing narrows the list", box.IsFocused || box.IsKeyboardFocusWithin);
        var list = picker.FindControl<ListBox>("List")!;
        var all = list.ItemCount;
        popup.KeyTextInput(target.Label);
        Pump(40);
        Check($"set view: typing a label narrows the list ({all} → {list.ItemCount})", list.ItemCount > 0 && list.ItemCount < all);
        popup.KeyPress(K.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        popup.KeyRelease(K.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Settle(window);
        Check("set view: Enter puts the row in the view and focuses it, the view unchanged",
            tab.View == EditorView.Set && tab.FlatRows.Contains(tab.RowFor(target.Item)) && tab.FocusedProperty is { } f && tab.RowFor(f) == tab.RowFor(target.Item));
        Check("set view: the added row isn't counted as set until it has a value", tab.SetPropertyCount == before);

        target.Item.RawValue = target.Item is NumberPropertyViewModel ? "7" : "added";
        Check($"set view: giving it a value counts it ({before} → {tab.SetPropertyCount})", tab.SetPropertyCount == before + 1);
        target.Item.RawValue = "";
        Check("set view: clearing it leaves the row where it is, and uncounts it",
            tab.SetPropertyCount == before && tab.FlatRows.Contains(tab.RowFor(target.Item)));
        tab.View = EditorView.All;
        Settle(window);
        tab.View = EditorView.Set;
        Settle(window);
        Check("set view: the row you worked on stays for the session", tab.FlatRows.Contains(tab.RowFor(target.Item)));
        Capture(window, System.IO.Path.Combine(outDir, "71-set-view-added.png"));
        window.Close();
    }
}
