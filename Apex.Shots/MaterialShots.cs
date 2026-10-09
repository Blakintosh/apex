using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Apex.Editor.Models;
using Apex.Editor.ViewModels;
using Apex.Editor.Views;

namespace Apex.Shots;

public partial class Program
{
    /// <summary>A lit material open in the live app, shot in both themes (<paramref name="prefix"/>-dark/-light.png).</summary>
    private static AssetEditorViewModel? ShootMaterial(Window window, MainViewModel vm, AssetDatabase db, string outDir, string prefix)
    {
        var material = db.Assets.FirstOrDefault(a => a.Type.Equals("material", StringComparison.OrdinalIgnoreCase)
            && a.Parent is null
            && string.Equals(a.ScanProperties.GetValueOrDefault("materialType"), "lit_advanced_fullspec", StringComparison.OrdinalIgnoreCase));
        if (material is null)
        {
            Console.WriteLine("info  material (live): no lit_advanced_fullspec material — not shot");
            return null;
        }
        vm.OpenAsset(material);
        Settle(window);
        Directory.CreateDirectory(outDir);
        Capture(window, Path.Combine(outDir, $"{prefix}-dark.png"));
        Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
        Settle(window);
        Capture(window, Path.Combine(outDir, $"{prefix}-light.png"));
        Application.Current.RequestedThemeVariant = ThemeVariant.Dark;
        Settle(window);
        Console.WriteLine($"info  material (live): {material.Name} shot to {Path.GetFullPath(outDir)}\\{prefix}-*.png ({vm.ActiveTab?.VisiblePropertyCount} rows)");
        return vm.ActiveTab;
    }

    /// <summary>--material-shots: the material shots alone, on the real install (read-only).</summary>
    private static void RunMaterialShots(string outDir)
    {
        var (window, vm) = StartLive(out _);
        if (window is null)
            return;
        try
        {
            ShootMaterial(window, vm, DatabaseOf(vm), outDir, "material");
        }
        finally
        {
            window.Close();
        }
    }
}
