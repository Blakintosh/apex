using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using Apex.Editor.Controls;
using Apex.Editor.Models;
using Apex.Editor.Services;
using Apex.Editor.ViewModels;
using Apex.Editor.Views;
using K = Avalonia.Input.Key;

namespace Apex.Shots;

/// <summary>
/// Line-list fields (the deffile's Text entries: hideTags, xmodel materials and skinOverride): one item per line, edited
/// with real input in the real window, written back in the value's own shape. The schema comes from a deffile written
/// for the run and parsed by the real loader; nothing is saved.
/// </summary>
public partial class Program
{
    private const string LineDeffile = """
        void GenerateUI( asset Asset )
        {
        	Asset.BeginCategory( "Lists" );
        	{
        		Asset.AddEntry_AssetCombo( "gunModel", "xmodel" ).SetTitle( "View Gun" );
        		Asset.AddEntry_Text( "hideTags", "" ).SetTitle( "Hide Tags" ).SetToolTip( "name of tags to hide on this model.  one per line" );
        		Asset.AddEntry_Text( "materials", "" ).SetTitle( "Materials" );
        		Asset.AddEntry_Text( "skinOverride", "" ).SetTitle( "Skin Override" );
        		Asset.AddEntry_Text( "comments", "" ).SetTitle( "Comments" );
        	}
        }
        """;

    private static readonly string Pad32 = string.Concat(Enumerable.Repeat(@"\r\n", 32));
    /// <summary>
    /// skinOverride on a real model: the section counts every material the model has, each name opens its material, and
    /// the columns say which side is the override. Read-only on the install; skipped without one.
    /// </summary>
    private static void SurfaceListChecks()
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
        if (!env.IsAvailable || env.ModelExportDir is null || !Directory.Exists(env.ModelExportDir))
        {
            Console.WriteLine("lists surfaces: BO3 not found — skipped");
            return;
        }
        var rootBefore = FieldFiles.Root;
        FieldFiles.Root = env.Bo3Root;
        try
        {
            string? file = null;
            string[]? names = null;
            foreach (var path in Directory.EnumerateFiles(env.ModelExportDir, "*.xmodel_bin", SearchOption.AllDirectories).Take(300))
            {
                var relative = Path.GetRelativePath(env.ModelExportDir, path);
                names = FieldFiles.SurfaceMaterialsAsync(relative).GetAwaiter().GetResult();
                if (names is { Length: >= 2 })
                {
                    file = relative;
                    break;
                }
            }
            if (file is null || names is null)
            {
                Console.WriteLine("lists surfaces: no model with two materials in model_export — skipped");
                return;
            }
            var def = new PropertyDef("skinOverride", "Skin Override", "Materials", PropertyKind.Text, "") { TextEditor = PropertyTextEditor.Lines };
            string? opened = null;
            var list = new LinesPropertyViewModel(def, "", (_, name) => opened = name, k => k == "filename" ? file : "", (_, _) => true);
            Check("lists surfaces: before the model is read the section counts one and the left column is a surface material to type",
                list.RowCount == 1 && list.SurfaceTitle == "Surface material");
            for (var i = 0; i < 100 && list.CanAdd; i++)
                Pump(30);
            Check($"lists surfaces: the model's {names.Length} materials are listed and counted ({list.RowCount})",
                !list.CanAdd && list.Items.Count == names.Length && list.RowCount == names.Length);
            Check("lists surfaces: the left column says it is the model's, the right that it overrides",
                list.SurfaceTitle == "Material in model" && list.ReplacementTitle == "Override");
            list.Items[0].GoToCommand?.Execute(null);
            Check($"lists surfaces: a material's name opens it ('{opened}')",
                list.Items.All(i => i.CanGoTo) && opened is not null && opened.Equals(list.Items[0].Surface, StringComparison.Ordinal));
            var missing = new LinesPropertyViewModel(def, "", (_, _) => { }, k => k == "filename" ? file : "", (_, _) => false);
            for (var i = 0; i < 100 && missing.CanAdd; i++)
                Pump(30);
            Check("lists surfaces: a material with no asset is plain text, not a link", !missing.CanAdd && missing.Items.All(i => i.IsPlainSurface));
        }
        finally
        {
            FieldFiles.Root = rootBefore;
        }
    }


    private static void RunLineChecks(string outDir)
    {
        LineListLogicChecks();
        SurfaceListChecks();

        var temp = Path.Combine(Path.GetTempPath(), "apex-shots-lines-" + Guid.NewGuid().ToString("N")[..8]);
        var deffiles = Directory.CreateDirectory(Path.Combine(temp, "deffiles")).FullName;
        File.WriteAllText(Path.Combine(deffiles, "linetest.awi"), LineDeffile);
        var schema = Apex.Editor.Services.Gdf.GdfSchemaLoader.LoadAll(deffiles).GetValueOrDefault("linetest");
        Check("lists: the test deffile parses", schema is not null);
        if (schema is null)
            return;
        Check("lists schema: Text entries are line lists (comments stay one prose field); hideTags offers the gun model's bones",
            schema.Find("hideTags") is { TextEditor: PropertyTextEditor.Lines, ModelKeys: ["gunModel", ..] }
            && schema.Find("materials") is { TextEditor: PropertyTextEditor.Lines }
            && schema.Find("comments") is { TextEditor: not PropertyTextEditor.Lines }
            && LineList.ShapeOf(schema.Find("hideTags")!) == LineShape.Bone
            && LineList.ShapeOf(schema.Find("skinOverride")!) == LineShape.SkinOverride
            && LineList.ShapeOf(schema.Find("materials")!) == LineShape.Material);

        SchemaRegistry.Populate(new Dictionary<string, AssetSchema>(StringComparer.OrdinalIgnoreCase)
        {
            ["weapon"] = SchemaRegistry.Get("weapon")!,
            ["xmodel"] = SchemaRegistry.Get("xmodel")!,
            ["material"] = SchemaRegistry.Get("material")!,
            ["linetest"] = schema,
        });
        var vm = new MainViewModel(null);
        var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
        try
        {
            window.Show();
            window.Activate();
            Pump();
            LineChecksInWindow(window, vm, outDir);
        }
        finally
        {
            window.Close();
            SchemaRegistry.ResetToMock();
            try { Directory.Delete(temp, recursive: true); }
            catch (IOException) { }
        }
    }

    private static void LineListLogicChecks()
    {
        Check("lists logic: items are the non-blank lines, as written",
            LineList.Items(@"a\r\n\r\nb c\r\n").SequenceEqual(new[] { "a", "b c" }) && LineList.Items(Pad32).Count == 0);
        Check("lists logic: a closing separator stays closing",
            LineList.Compose(new[] { "a", "b", "c" }, @"a\r\nb\r\n", LineShape.Material) == @"a\r\nb\r\nc\r\n");
        Check("lists logic: no closing separator stays none",
            LineList.Compose(new[] { "a", "c" }, @"a\r\nb", LineShape.Bone) == @"a\r\nc");
        Check("lists logic: 32-line padding is kept after the items (and is all that's left when they go)",
            LineList.Compose(new[] { "tag_x" }, Pad32, LineShape.Bone) == "tag_x" + Pad32
            && LineList.Compose(Array.Empty<string>(), "tag_x" + Pad32, LineShape.Bone) == Pad32
            && LineList.Compose(Array.Empty<string>(), @"tag_x\r\n", LineShape.Bone) == "");
        Check("lists logic: a value that starts empty takes the field's usual shape",
            LineList.Compose(new[] { "a b" }, "", LineShape.SkinOverride) == @"a b\r\n"
            && LineList.Compose(new[] { "m" }, "", LineShape.Material) == @"m\r\n"
            && LineList.Compose(new[] { "tag" }, "", LineShape.Bone) == "tag");
        Check("lists logic: skinOverride lines split at the first space",
            LineList.SplitPair("surf_a  mtl_b") == ("surf_a", "mtl_b") && LineList.SplitPair("surf_only") == ("surf_only", ""));
    }

    private static List<TextBox> LineBoxes(Visual row) =>
        row.GetVisualDescendants().OfType<TextBox>().Where(b => b.IsEffectivelyVisible).ToList();

    private static Control ItemRow(LineListEditor list, int index) =>
        list.GetVisualDescendants().OfType<Grid>().Where(g => g.Classes.Contains("litem") && g.DataContext is LineItemViewModel { IsAdd: false })
            .ElementAt(index);

    private static Control AddRowOf(LineListEditor list) =>
        list.GetVisualDescendants().OfType<Grid>().First(g => g.Classes.Contains("litem") && g.DataContext is LineItemViewModel { IsAdd: true });

    private static object? Focused(Window window) => window.FocusManager?.GetFocusedElement();

    private static void LineChecksInWindow(Window window, MainViewModel vm, string outDir)
    {
        var db = DatabaseOf(vm);
        var mats = db.Assets.Where(a => a.Type == "material").Select(a => a.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
        var mtlA = mats[0];
        var record = new AssetRecord { Name = "linetest_gun", Type = "linetest", GdtName = "linetest.gdt" };
        var hideOriginal = @"tag_sights_off\r\ntag_flash" + Pad32;
        record.Properties["hideTags"] = hideOriginal;
        record.Properties["materials"] = $@"{mtlA}\r\nno_such_material\r\n";
        record.Properties["skinOverride"] = $@"surf_a {mtlA}\r\nsurf_b nodraw\r\n";
        vm.OpenAsset(record);
        Pump();
        var tab = vm.ActiveTab!;
        PropertyItemViewModel Row(string key) => tab.AllSentinel.All.First(p => p.Key == key);
        var tags = (LinesPropertyViewModel)Row("hideTags");
        Check("lists: opening changes nothing (the value stays byte for byte, nothing to undo)",
            tags.RawValue == hideOriginal && !tab.CanUndo && !tags.IsChanged);
        Check($"lists: blank lines aren't items ({tags.Items.Count} tags)", tags.Items.Count == 2);
        Check("lists: hideTags items are bone fields of the gun model",
            tags.Items[0].Main is BonePropertyViewModel { Def.ModelKeys: ["gunModel", ..] });

        var view = FieldView(window, tab, tags);
        var list = view.GetVisualDescendants().OfType<LineListEditor>().First();
        var rowBorder = view.GetVisualAncestors().OfType<Border>().First(b => b.Classes.Contains("prow"));
        Check($"lists: the editor row grows to show every item ({rowBorder.Bounds.Height:0} px)", rowBorder.Bounds.Height > 3 * 26);
        Capture(window, Path.Combine(outDir, "60-lists-editor.png"));
        Application.Current!.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
        Pump();
        Capture(window, Path.Combine(outDir, "60-lists-editor-light.png"));
        Application.Current.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
        Pump();

        // ── Edit an item: Enter commits it (one undo step) and moves to the next ──
        var first = LineBoxes(ItemRow(list, 0))[0];
        Click(window, first);
        first.SelectAll();
        TypeText(window, "tag_sights_on");
        Check("lists: typing alone writes nothing", tags.RawValue == hideOriginal);
        Key(window, K.Enter);
        Check($"lists: Enter writes the item, keeping the padding ('{tags.RawValue[..Math.Min(40, tags.RawValue.Length)]}…')",
            tags.RawValue == @"tag_sights_on\r\ntag_flash" + Pad32 && record.Properties["hideTags"] == tags.RawValue);
        Pump(20);
        Check("lists: and moves to the next item", Focused(window) == LineBoxes(ItemRow(list, 1))[0]);
        Check($"lists: the Inspector follows the list's row, not the item ({tab.FocusedProperty?.GetType().Name})", tab.FocusedProperty == tags);
        tab.UndoCommand.Execute(null);
        Pump();
        Check("lists: one Ctrl+Z restores the original exactly, and the items follow",
            tags.RawValue == hideOriginal && tags.Items[0].Main.RawValue == "tag_sights_off" && !tab.CanUndo);

        // ── ↑/↓ walk the items; past the ends they walk the rows ──
        Click(window, LineBoxes(ItemRow(list, 0))[0]);
        Key(window, K.Down);
        Check("lists: ↓ moves to the next item", Focused(window) == LineBoxes(ItemRow(list, 1))[0]);
        Key(window, K.Down);
        Check("lists: ↓ from the last item reaches the Add field", Focused(window) == LineBoxes(AddRowOf(list))[0]);
        Key(window, K.Up);
        Key(window, K.Up);
        Check("lists: ↑ walks back", Focused(window) == LineBoxes(ItemRow(list, 0))[0]);
        Key(window, K.Up);
        Pump(20);
        Check($"lists: ↑ from the first item goes on to the row above ({(Focused(window) as Control)?.DataContext?.GetType().Name})",
            Focused(window) is Control { DataContext: RefPropertyViewModel { Key: "gunModel" } });

        // ── Add: Enter appends, the Add field stays ready for the next ──
        list = FieldView(window, tab, tags).GetVisualDescendants().OfType<LineListEditor>().First();
        var add = LineBoxes(AddRowOf(list))[0];
        Click(window, add);
        Check($"lists: the Add field says what it adds ('{add.PlaceholderText}')", add.PlaceholderText == "Add a tag");
        TypeText(window, "tag_new");
        Key(window, K.Enter);
        Pump(20);
        Check($"lists: Enter in Add appends the item ('{tags.RawValue[..Math.Min(50, tags.RawValue.Length)]}…')",
            tags.RawValue == @"tag_sights_off\r\ntag_flash\r\ntag_new" + Pad32 && tags.Items.Count == 3);
        add = LineBoxes(AddRowOf(list))[0];
        Check("lists: and the Add field is empty and still focused", Focused(window) == add && string.IsNullOrEmpty(add.Text));

        // ── Delete in an empty item removes it; ✕ removes one too; each is one undo step ──
        var second = LineBoxes(ItemRow(list, 1))[0];
        Click(window, second);
        second.SelectAll();
        Key(window, K.Back);
        Key(window, K.Delete);
        Pump(20);
        Check($"lists: Delete in an emptied item removes it ({tags.Items.Count} left)",
            tags.Items.Count == 2 && tags.RawValue == @"tag_sights_off\r\ntag_new" + Pad32);
        Check("lists: and the keyboard lands on the item that took its place", Focused(window) == LineBoxes(ItemRow(list, 1))[0]);
        var remove = ItemRow(list, 0).GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("lremove"));
        Check($"lists: ✕ has a 24 px target ({remove.Bounds.Width:0}×{remove.Bounds.Height:0})", remove.Bounds.Width >= 24 && remove.Bounds.Height >= 24);
        Click(window, remove);
        Check("lists: ✕ removes its item", tags.Items.Count == 1 && tags.RawValue == "tag_new" + Pad32);
        Pump();
        Click(window, ItemRow(list, 0).GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("lremove")));
        Check($"lists: removing every item leaves the padding the value had ({tags.Items.Count} items, {LineList.TrailingSeparators(tags.RawValue)} separators, starts '{tags.RawValue[..Math.Min(12, tags.RawValue.Length)]}')", tags.RawValue == Pad32 && tags.Items.Count == 0);
        while (tab.CanUndo)
            tab.UndoCommand.Execute(null);
        Pump();
        Check($"lists: undo takes each step back to the original ({tags.Items.Count} tags)", tags.RawValue == hideOriginal && tags.Items.Count == 2);

        // ── Esc reverts typing in an item ──
        list = FieldView(window, tab, tags).GetVisualDescendants().OfType<LineListEditor>().First();
        first = LineBoxes(ItemRow(list, 0))[0];
        Click(window, first);
        first.SelectAll();
        TypeText(window, "oops");
        Key(window, K.Escape);
        Check("lists: Esc puts the item back", first.Text == "tag_sights_off" && tags.RawValue == hideOriginal);

        // ── materials: material suggestions per item, ⚠ on the missing one ──
        var materials = (LinesPropertyViewModel)Row("materials");
        Check($"materials: the missing material has its own ⚠ ('{materials.Items[1].Main.Problem}')",
            materials.Items[0].Main is RefPropertyViewModel { HasProblem: false } && materials.Items[1].Main.HasProblem);
        Check($"materials: and the field says which ('{materials.Problem}')", materials.Problem == "No material named ‘no_such_material’");
        var matList = FieldView(window, tab, materials).GetVisualDescendants().OfType<LineListEditor>().First();
        var matAdd = LineBoxes(AddRowOf(matList))[0] as RefField;
        Check("materials: the Add field is a reference field", matAdd is not null);
        if (matAdd is not null)
        {
            var typed = mats[1][..Math.Min(6, mats[1].Length)];
            ReplaceText(window, matAdd, typed);
            Check($"materials: typing suggests materials ({matAdd.Suggestions?.Items.Count} for '{typed}')",
                matAdd.IsSuggesting && matAdd.Suggestions!.Items.Count > 0 && matAdd.Suggestions.Items.All(a => a.Type == "material"));
            Capture(window, Path.Combine(outDir, "61-lists-material-suggest.png"));
            Key(window, K.Down);
            var picked = matAdd.Suggestions!.Selected!.Name;
            Key(window, K.Enter);
            Pump(20);
            Check($"materials: ↓ Enter adds the suggestion, closing separator kept ('{materials.RawValue}')",
                materials.RawValue == $@"{mtlA}\r\nno_such_material\r\n{picked}\r\n");
            Check("materials: the keyboard stays in the Add field, now empty",
                Focused(window) is RefField { Text: null or "" } f && f.DataContext is RefPropertyViewModel r && r == materials.AddItem.Main);
            tab.UndoCommand.Execute(null);
            Pump();
        }

        // ── skinOverride: surface → replacement, nodraw is not a missing material ──
        var skin = (LinesPropertyViewModel)Row("skinOverride");
        Check("skinOverride: two columns per line, nodraw has no ⚠",
            skin.Items.Count == 2 && skin.Items.All(i => i.IsPair) && skin.Items[1].Replacement!.RawValue == "nodraw"
            && !skin.Items[1].Replacement!.HasProblem && skin.Problem is null);
        var skinList = FieldView(window, tab, skin).GetVisualDescendants().OfType<LineListEditor>().First();
        var repl = LineBoxes(ItemRow(skinList, 0))[1];
        Check("skinOverride: the replacement is a reference field", repl is RefField);
        Click(window, repl);
        repl.SelectAll();
        TypeText(window, "nodraw");
        Key(window, K.Escape); // closes the suggestion list (no material is named nodraw); the second Esc would revert
        Key(window, K.Enter);
        Check($"skinOverride: editing the replacement rewrites only its line ('{skin.RawValue}')",
            skin.RawValue == @"surf_a nodraw\r\nsurf_b nodraw\r\n");
        var addPair = LineBoxes(AddRowOf(skinList));
        Click(window, addPair[0]);
        TypeText(window, "surf_c");
        Key(window, K.Enter);
        Pump(20);
        Check("skinOverride: Enter in the new surface moves to its replacement, writing nothing yet",
            Focused(window) == LineBoxes(AddRowOf(skinList))[1] && skin.Items.Count == 2);
        TypeText(window, mtlA);
        Key(window, K.Escape);
        Key(window, K.Enter);
        Pump(20);
        Check($"skinOverride: Enter in the replacement adds the line ('{skin.RawValue}')",
            skin.RawValue == $@"surf_a nodraw\r\nsurf_b nodraw\r\nsurf_c {mtlA}\r\n" && skin.Items.Count == 3);
        Capture(window, Path.Combine(outDir, "62-lists-skinoverride.png"));
        // A pair without a surface is no line: emptying a surface removes the pair (its replacement doesn't become one).
        var lastPair = LineBoxes(skinList.FindControl<ItemsControl>("ItemsList")!.ContainerFromIndex(2)!);
        Click(window, lastPair[0]);
        lastPair[0].SelectAll();
        Key(window, K.Back);
        Key(window, K.Enter);
        Pump(20);
        Check($"skinOverride: emptying a surface removes its pair ('{skin.RawValue}')",
            skin.RawValue == @"surf_a nodraw\r\nsurf_b nodraw\r\n" && skin.Items.Count == 2);
        // And a replacement typed in the Add row with no surface writes nothing.
        var addOnly = LineBoxes(AddRowOf(skinList));
        Click(window, addOnly[1]);
        TypeText(window, mtlA);
        Key(window, K.Escape);
        Key(window, K.Enter);
        Pump(20);
        Check($"skinOverride: a replacement with no surface adds nothing ('{skin.RawValue}')",
            skin.RawValue == @"surf_a nodraw\r\nsurf_b nodraw\r\n" && skin.Items.Count == 2);
        var lefts = new[] { tags, skin, Row("gunModel") }.Select(r => FieldView(window, tab, r).TranslatePoint(default, window)?.X ?? -1).ToList();
        Check($"skinOverride: its two columns keep to the value column every row lines up on ({string.Join(", ", lefts.Select(x => x.ToString("0")))})",
            lefts.Distinct().Count() == 1);
        while (tab.CanUndo)
            tab.UndoCommand.Execute(null);
        Pump();
        Check("lists: every edit undone", record.CountSessionChanges() == 0);

        // ── A table cell: a one-line count that opens the list ──
        vm.OpenTableFor(new[] { record });
        Pump();
        var table = vm.Table!;
        table.SetColumnShown(SchemaRegistry.Get("linetest")!.Find("hideTags")!, true);
        Pump(50);
        var tableView = window.GetVisualDescendants().OfType<Apex.Editor.Views.TableView>().First();
        // At rest the cell draws the count; the pointer on it brings the list's summary button.
        // The new column is the last: scrolled to, as a user would.
        if (tableView.FindControl<ItemsControl>("Rows")?.FindAncestorOfType<ScrollViewer>() is { } sideways)
        {
            sideways.Offset = new Vector(sideways.Extent.Width, 0);
            Pump();
        }
        var linesHost = CellHost(tableView, p => p is LinesPropertyViewModel { Key: "hideTags" });
        var drawnCount = linesHost?.Display?.Text;
        var cellList = linesHost is null ? null : HoverCell(window, linesHost)?.GetVisualDescendants().OfType<LineListEditor>()
            .FirstOrDefault(l => l.IsEffectivelyVisible && l.DataContext is LinesPropertyViewModel { Key: "hideTags" });
        Check($"lists table: a hideTags cell shows its count, not the list (drawn '{drawnCount}', hovered {cellList?.IsCompact})",
            drawnCount == "2 tags" && cellList is { IsCompact: true } && cellList.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "2 tags"));
        if (cellList is not null)
        {
            var summary = cellList.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("lsummary"));
            Click(window, summary);
            Pump(30);
            Check("lists table: a click opens the list under the cell", cellList.IsExpanded && cellList.Expanded is { } e && LineBoxes(e).Count >= 3);
            if (cellList.Expanded is { } expanded && TopLevel.GetTopLevel(expanded) is { } popup)
            {
                Check("lists table: the keyboard starts on the first item", popup.FocusManager?.GetFocusedElement() == LineBoxes(expanded)[0]);
                var box = LineBoxes(expanded)[0];
                box.SelectAll();
                popup.KeyTextInput("tag_cell");
                Pump();
                popup.KeyPress(K.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
                popup.KeyRelease(K.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
                Pump(20);
                Check($"lists table: Enter in the open list writes the row's own asset ('{record.Properties["hideTags"][..20]}…')",
                    record.Properties["hideTags"] == @"tag_cell\r\ntag_flash" + Pad32);
                Capture(window, Path.Combine(outDir, "63-lists-table-cell.png"));
                popup.KeyPress(K.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
                Pump();
            }
        }
        table.CloseCommand.Execute(null);
        Pump();
        TableCellLook(window, vm, new[] { record }, new[] { SchemaRegistry.Get("linetest")!.Find("hideTags")! }, "lists table cells");
    }
}
