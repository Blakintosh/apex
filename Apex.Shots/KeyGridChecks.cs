using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using Apex.Editor.ViewModels;
using Apex.Editor.Views;
using K = Avalonia.Input.Key;
using Apex.Editor.Models;
using Apex.Editor.Services;

namespace Apex.Shots;

/// <summary>
/// The Matrix view's grid detection: labels that are a row name and a column name become a grid, and a section that
/// merely resembles one stays a list. The real deffiles are read for which asset types have a grid at all.
/// </summary>
public partial class Program
{
    private static readonly string[] FxSurfaces = ["Zombie", "Flesh", "Flesh Corpse", "Armor Light", "Armor Heavy", "Robot Light", "Robot Heavy"];
    private static readonly string[] FxHits = ["Head", "Head Fatal", "Body", "Body Fatal", "Limb", "Limb Fatal"];

    private static PropertyDef GridProp(string key, string label, PropertyKind kind = PropertyKind.Text) =>
        new(key, label, "Impacts", kind, "");

    private static void RunKeyGridChecks(string outDir)
    {
        var fx = FxSurfaces.SelectMany(s => FxHits.Select(h => GridProp(s.Replace(" ", "") + h.Replace(" ", ""), s + " " + h))).ToList();
        var grid = KeyGridDetector.Detect("Impacts", fx);
        Check($"key grid: surfaces × hit types is a {FxSurfaces.Length} × {FxHits.Length} grid ({grid?.Rows.Count} × {grid?.Columns.Count})",
            grid is not null && grid.Rows.SequenceEqual(FxSurfaces) && grid.Columns.SequenceEqual(FxHits) && grid.Filled == fx.Count);
        Check("key grid: each cell holds its key", grid is not null && grid.Keys[2, 3] == "FleshCorpseBodyFatal" && grid.Keys[0, 0] == "ZombieHead");

        // A missing key leaves a hole, not a different grid.
        var holey = fx.Where(p => p.Key != "ArmorLightLimb").ToList();
        var holes = KeyGridDetector.Detect("Impacts", holey);
        Check("key grid: a missing key is an empty cell", holes is not null && holes.Cells == fx.Count && holes.Filled == fx.Count - 1 && holes.Keys[3, 4] is null);

        // Labels that only look alike aren't one.
        var plain = new[] { "Magazine Size", "Reload Time", "Fire Mode", "Damage", "Min Damage", "Max Damage", "Ammo Pool", "Impact Type" }
            .Select(l => GridProp(l.Replace(" ", ""), l)).ToList();
        Check("key grid: ordinary properties stay a list", KeyGridDetector.Detect("Weapon", plain) is null);
        Check("key grid: a section of mixed kinds stays a list",
            KeyGridDetector.Detect("Impacts", fx.Select((p, i) => i == 0 ? p with { Kind = PropertyKind.Number } : p).ToList()) is null);
        Check("key grid: too few rows stay a list", KeyGridDetector.Detect("Impacts", fx.Take(12).ToList()) is null);

        LiveKeyGridSurvey();
        MatrixViewChecks(outDir);
    }

    /// <summary>Which asset types the real deffiles make grids in (information, for choosing where the Matrix view is offered).</summary>
    private static void LiveKeyGridSurvey()
    {
        var mock = Environment.GetEnvironmentVariable("APEX_FORCE_MOCK");
        Environment.SetEnvironmentVariable("APEX_FORCE_MOCK", null);
        Apex.Editor.Services.Gdt.GameEnvironment env;
        try
        {
            env = new Apex.Editor.Services.Gdt.GameEnvironment();
        }
        finally
        {
            Environment.SetEnvironmentVariable("APEX_FORCE_MOCK", mock);
        }
        if (!env.IsAvailable)
        {
            Console.WriteLine("key grid (live): BO3 not found — skipped");
            return;
        }
        var schemas = Apex.Editor.Services.Gdf.GdfSchemaLoader.LoadAll(env.DeffilesDir!);
        var found = 0;
        foreach (var (type, schema) in schemas.OrderBy(s => s.Key, StringComparer.OrdinalIgnoreCase))
            foreach (var section in schema.Properties.GroupBy(p => p.Category))
                if (KeyGridDetector.Detect(section.Key, section.ToList()) is { } g)
                {
                    found++;
                    Console.WriteLine($"info  key grid (live): {type} / {section.Key}: {g.Rows.Count} rows × {g.Columns.Count} columns, {g.Filled} of {g.Cells} cells — {string.Join(", ", g.Rows.Take(4))}… × {string.Join(", ", g.Columns.Take(4))}…");
                }
        Console.WriteLine($"info  key grid (live): {found} grids in {schemas.Count} asset types");
    }

    private const string GridDeffile = """
        void GenerateUI( asset Asset )
        {
        	Asset.BeginCategory( "Impacts" );
        	{
        		array<string> Surfaces = { "Zombie", "Flesh", "Armor Light", "Armor Heavy" };
        		array<string> Keys = { "zombie", "flesh", "armorLight", "armorHeavy" };
        		array<string> Hits = { "Head", "Head Fatal", "Body", "Body Fatal" };
        		array<string> HitKeys = { "Head", "HeadFatal", "Body", "BodyFatal" };
        		for (int s = 0; s < 4; s++)
        			for (int h = 0; h < 4; h++)
        				Asset.AddEntry_String( Keys[s] + HitKeys[h], "" ).SetTitle( Surfaces[s] + " " + Hits[h] );
        		Asset.AddEntry_String( "notes", "" ).SetTitle( "Notes" );
        	}
        }
        """;

    private static void MatrixViewChecks(string outDir)
    {
        var temp = Path.Combine(Path.GetTempPath(), "apex-shots-grid-" + Guid.NewGuid().ToString("N")[..8]);
        var deffiles = Directory.CreateDirectory(Path.Combine(temp, "deffiles")).FullName;
        File.WriteAllText(Path.Combine(deffiles, "gridtest.awi"), GridDeffile);
        var schema = Apex.Editor.Services.Gdf.GdfSchemaLoader.LoadAll(deffiles).GetValueOrDefault("gridtest");
        Check("matrix: the test deffile parses", schema is not null);
        if (schema is null)
            return;
        SchemaRegistry.Populate(new Dictionary<string, AssetSchema>(StringComparer.OrdinalIgnoreCase)
        {
            ["weapon"] = SchemaRegistry.Get("weapon")!,
            ["xmodel"] = SchemaRegistry.Get("xmodel")!,
            ["material"] = SchemaRegistry.Get("material")!,
            ["gridtest"] = schema,
        });
        var vm = new MainViewModel(null);
        var window = new MainWindow { DataContext = vm, Width = 1500, Height = 900 };
        try
        {
            window.Show();
            window.Activate();
            Pump();
            var record = new AssetRecord { Name = "gridtest_fx", Type = "gridtest", GdtName = "gridtest.gdt" };
            record.Properties["zombieHead"] = @"blood\fx_a.efx";
            record.Properties["zombieBodyFatal"] = @"blood\fx_b.efx";
            record.Properties["armorHeavyHead"] = @"impacts\fx_c.efx";
            record.Properties["notes"] = "kept";
            vm.OpenAsset(record);
            Pump(60);
            var tab = vm.ActiveTab!;
            var view = window.GetVisualDescendants().OfType<AssetEditorView>().First(v => v.IsEffectivelyVisible);
            var matrix = tab.FlatRows.OfType<MatrixRowViewModel>().FirstOrDefault();
            Check($"matrix: a grid section shows as one matrix row, its keys not rows ({tab.FlatRows.Count} rows)",
                matrix is not null && matrix.Grid.Rows.Count == 4 && matrix.Grid.Columns.Count == 4
                && !tab.FlatRows.OfType<PropertyItemViewModel>().Any(p => matrix.Items.Contains(p)));
            Check("matrix: a key outside the grid stays a row", tab.FlatRows.OfType<PropertyItemViewModel>().Any(p => p.Key == "notes"));
            if (matrix is null)
                return;
            var shown = view.GetVisualDescendants().OfType<KeyMatrixView>().FirstOrDefault();
            Check("matrix: the form draws it", shown is not null);
            if (shown is null)
                return;
            Check("matrix: squares show the file's name, not its folder",
                matrix.Cells.Any(c => c.Text == "fx_a.efx") && matrix.Cells.Any(c => c.Text == "fx_b.efx"));
            Check("matrix: it starts on the first square with a value", matrix.Selected is { Row: 0, Column: 0 });

            var square = shown.GetVisualDescendants().OfType<Border>().First(b => b.DataContext is MatrixCell { Row: 1, Column: 2 });
            Click(window, square);
            Pump(30);
            Check("matrix: a click selects a square and the keyboard goes to the grid", matrix.Selected is { Row: 1, Column: 2 } && shown.IsKeyboardFocusWithin);
            var editor = shown.GetVisualDescendants().OfType<PropertyEditorView>().First();
            Check("matrix: the editor under it is that property's", editor.DataContext == matrix.SelectedItem && matrix.SelectedItem?.Key == "fleshBody");
            Key(window, K.Down);
            Key(window, K.Right);
            Check("matrix: arrow keys move the selection", matrix.Selected is { Row: 2, Column: 3 });
            Key(window, K.Left);
            Key(window, K.Up);
            Check("matrix: and back", matrix.Selected is { Row: 1, Column: 2 });

            Key(window, K.Enter);
            Pump(30);
            Check("matrix: Enter goes to the editor", editor.IsKeyboardFocusWithin);
            TypeText(window, @"x\fx_new.efx");
            Key(window, K.Enter);
            Pump(30);
            Check($"matrix: typing there sets the square's property ('{record.Properties.GetValueOrDefault("fleshBody")}')",
                record.Properties.GetValueOrDefault("fleshBody") == @"x\fx_new.efx" && matrix.Selected!.Text == "fx_new.efx");
            Check("matrix: the edit is an undo step", tab.CanUndo);
            Key(window, K.Z, RawInputModifiers.Control);
            Pump(30);
            Check("matrix: Ctrl+Z undoes it and the square follows", !record.Properties.ContainsKey("fleshBody") && matrix.Selected!.Text == "");

            Capture(window, Path.Combine(outDir, "72-matrix.png"));
            Avalonia.Application.Current!.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
            Pump(60);
            Capture(window, Path.Combine(outDir, "73-light-matrix.png"));
            Avalonia.Application.Current.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
            Pump(60);

            var toggle = view.GetVisualDescendants().OfType<Button>().First(b => b.DataContext is CategoryViewModel { HasGrid: true } c && b.Command == c.ToggleMatrixCommand);
            Click(window, toggle);
            Pump(60);
            Check("matrix: Show as list swaps it for the section's rows",
                !tab.FlatRows.OfType<MatrixRowViewModel>().Any() && tab.FlatRows.OfType<PropertyItemViewModel>().Count(p => p.Key.StartsWith("zombie")) == 4);
            toggle = view.GetVisualDescendants().OfType<Button>().First(b => b.DataContext is CategoryViewModel { HasGrid: true } c && b.Command == c.ToggleMatrixCommand);
            Click(window, toggle);
            Pump(60);
            Check("matrix: and Show as matrix brings it back", tab.FlatRows.OfType<MatrixRowViewModel>().Any());
            tab.SearchText = "head";
            Pump(200);
            Check("matrix: a filter shows the rows that match, not the grid", !tab.FlatRows.OfType<MatrixRowViewModel>().Any());
            tab.SearchText = "";
            Pump(200);
            Check("matrix: clearing it brings the grid back", tab.FlatRows.OfType<MatrixRowViewModel>().Any());
        }
        finally
        {
            window.Close();
            SchemaRegistry.ResetToMock();
            try { Directory.Delete(temp, recursive: true); }
            catch (IOException) { }
        }
    }
}
