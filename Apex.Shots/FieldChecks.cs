using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Apex.Editor.Controls;
using Apex.Editor.Models;
using Apex.Editor.Services;
using Apex.Editor.ViewModels;
using Apex.Editor.Views;

namespace Apex.Shots;

/// <summary>
/// The tailored field editors — file (browse, missing-file ⚠), colour (swatch, picker), file list, bone list and labelled
/// vectors — driven with real input in the real window. The schema comes from a deffile written for the run (parsed by
/// the real loader); the "install" is a temp folder, so nothing outside this run's temp files is read or written.
/// </summary>
public partial class Program
{
    private const string FieldDeffile = """
        void GenerateUI( asset Asset )
        {
        	Asset.BeginCategory( "Files" );
        	{
        		Asset.AddEntry_XModel( "modelFile", "" ).SetTitle( "Model File" ).SetRelativePath( "model_export/" );
        		Asset.AddEntry_Path( "flashEffect", "" ).SetTitle( "Flash FX" ).SetRelativePath( "share/raw" );
        		Asset.AddEntry_Path( "collisionMap", "" ).SetTitle( "Collision Map" ).SetRelativePath( "map_source/" ).SetFileFilter( "Collision Map Files (*.map)" );
        		Asset.AddEntry_FileCombo( "aiVsAiAccuracyGraph", "share/raw/accuracy/aivsai/", "" ).SetTitle( "AI Vs. AI Accuracy" );
        		Asset.AddEntry_AssetCombo( "gunModel", "xmodel" ).SetTitle( "View Gun" );
        		Asset.AddEntry_BoneCombo( "attachViewModelTag1", "gunModel" ).SetHints( "NOWARNINGS" ).SetTitle( "View Model Tag" );
        	}
        	Asset.BeginCategory( "Colours" );
        	{
        		Asset.AddEntry_Color( "startColor", 1, 1, 1, 1 ).SetTitle( "Start Color" ).SetShowAlpha( true );
        		Asset.AddEntry_Color( "fogcolor", 0, 0, 0, 1.0 ).SetTitle( "Fog color" ).SetShowAlpha( false );
        	}
        	Asset.BeginCategory( "Vectors" );
        	{
        		Asset.AddEntry_Vector3( "offsetX", "offsetY", "offsetZ", 0, 0, 0, -10000, 10000 ).SetTitle( "View Model Offset Position" ).SetLabels( "Forward", "Right", "Up", "" );
        		Asset.AddEntry_Vector3( "anglePitch", "angleYaw", "angleRoll", 0, 0, 0, -180, 180 ).SetTitle( "View Model Offset Angles" ).SetLabels( "Pitch  ", "Yaw", "Roll", "" );
        		Asset.AddEntry_Vector2( "spreadA", "spreadB", 0, 0, 0, 10 ).SetTitle( "Spread" );
        	}
        }
        """;

    private static void RunFieldChecks(string outDir)
    {
        // A colour edit replaces only the component touched; every other character stays as written.
        var withComponent = typeof(ColorPropertyViewModel).GetMethod("WithComponent",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        string With(string value, int index, string text) => (string)withComponent.Invoke(null, new object[] { value, index, text })!;
        foreach (var (value, index, text, want) in new[]
        {
            ("0.0392157 0.14902 0.705882 1", 1, "0.5", "0.0392157 0.5 0.705882 1"),
            ("1 0 0", 0, "0.25", "0.25 0 0"),          // three components stay three
            ("1  0.5   0  1", 2, "1", "1  0.5   1  1"), // spacing kept
            ("1 1 1 1 extra", 3, "0", "1 1 1 0 extra"), // trailing tokens kept
            ("1 0", 3, "0.5", "1 0 0 0.5"),            // padded only as far as needed
            ("", 0, "1", "1"),
        })
            Check($"colour: component {index} of '{value}' → '{With(value, index, text)}'", With(value, index, text) == want);

        var temp = Path.Combine(Path.GetTempPath(), "apex-shots-fields-" + Guid.NewGuid().ToString("N")[..8]);
        var deffiles = Directory.CreateDirectory(Path.Combine(temp, "deffiles")).FullName;
        File.WriteAllText(Path.Combine(deffiles, "fieldtest.awi"), FieldDeffile);
        // A stand-in install: one model, three accuracy graphs (and a file of another kind beside them).
        var install = Path.Combine(temp, "install");
        Directory.CreateDirectory(Path.Combine(install, "model_export", "weapons"));
        File.WriteAllText(Path.Combine(install, "model_export", "weapons", "gun.xmodel_bin"), "");
        var graphs = Directory.CreateDirectory(Path.Combine(install, "share", "raw", "accuracy", "aivsai")).FullName;
        foreach (var name in new[] { "smg.accu", "default.accu", "assault_rifle.accu", "notes.txt" })
            File.WriteAllText(Path.Combine(graphs, name), "");
        Directory.CreateDirectory(Path.Combine(install, "share", "raw", "fx"));

        var schemas = Apex.Editor.Services.Gdf.GdfSchemaLoader.LoadAll(deffiles);
        var schema = schemas.GetValueOrDefault("fieldtest");
        Check("fields: the test deffile parses", schema is not null);
        if (schema is null)
            return;

        // ── Schema: what the deffile says reaches the editor ──
        var labels = schema.Properties.Where(p => p.Category == "Vectors").Select(p => p.Label).ToList();
        Check($"vectors: .SetLabels names the components ({string.Join(", ", labels)})",
            labels.SequenceEqual(new[]
            {
                "View Model Offset Position Forward", "View Model Offset Position Right", "View Model Offset Position Up",
                "View Model Offset Angles Pitch", "View Model Offset Angles Yaw", "View Model Offset Angles Roll",
                "Spread X", "Spread Y",
            }));
        Check("schema: Color entries map to the colour editor, alpha per .SetShowAlpha",
            schema.Find("startColor") is { TextEditor: PropertyTextEditor.Color, ShowAlpha: true, Default: "1 1 1 1" }
            && schema.Find("fogcolor") is { TextEditor: PropertyTextEditor.Color, ShowAlpha: false });
        Check("schema: FileCombo keeps its folder, BoneCombo its model key, Path its file filter",
            schema.Find("aiVsAiAccuracyGraph") is { TextEditor: PropertyTextEditor.FileList, RelativeRoot: "share/raw/accuracy/aivsai/" }
            && schema.Find("attachViewModelTag1") is { TextEditor: PropertyTextEditor.Bone, ModelKeys: ["gunModel"] }
            && schema.Find("collisionMap") is { FileKind: PropertyFileKind.Path, FileFilter: "Collision Map Files (*.map)" });

        var merged = new Dictionary<string, AssetSchema>(StringComparer.OrdinalIgnoreCase)
        {
            ["weapon"] = SchemaRegistry.Get("weapon")!,
            ["xmodel"] = SchemaRegistry.Get("xmodel")!,
            ["material"] = SchemaRegistry.Get("material")!,
            ["fieldtest"] = schema,
        };
        SchemaRegistry.Populate(merged);
        var pickRequests = new List<FieldFiles.PickRequest>();
        string? nextPick = null;
        var realPick = FieldFiles.PickFile;
        FieldFiles.PickFile = (_, request) =>
        {
            pickRequests.Add(request);
            return Task.FromResult(nextPick);
        };
        var vm = new MainViewModel(null);
        var window = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
        try
        {
            window.Show();
            window.Activate();
            Pump();
            FieldFiles.Root = install;
            FieldFiles.ResetProbes();
            FieldChecksInWindow(window, vm, install, pickRequests, p => nextPick = p, outDir);
            FieldNarrowChecks(window);
            // With the temp install: file lists offer their files, so their cells show ▾.
            FieldTableLook(window, vm);
            FieldMockChecks(window, vm);
        }
        finally
        {
            window.Close();
            FieldFiles.PickFile = realPick;
            FieldFiles.Root = null;
            FieldFiles.ResetProbes();
            SchemaRegistry.ResetToMock();
            try { Directory.Delete(temp, recursive: true); }
            catch (IOException) { }
        }
    }

    /// <summary>File, colour and suggestion cells drawn at rest look exactly like their editors.</summary>
    private static void FieldTableLook(Window window, MainViewModel vm)
    {
        var records = Enumerable.Range(0, 4).Select(i =>
        {
            var r = FieldRecord($"fieldtest_cell_{i}");
            r.Properties["modelFile"] = i % 2 == 0 ? @"weapons\gun.xmodel_bin" : "";
            r.Properties["startColor"] = i % 2 == 0 ? "1 0 0 1" : "0.2 0.4 0.6 0.5";
            r.Properties["aiVsAiAccuracyGraph"] = i % 2 == 0 ? "smg.accu" : "";
            return r;
        }).ToList();
        TableCellLook(window, vm, records, SchemaRegistry.Get("fieldtest")!.Properties.ToList(), "field table cells");
    }

    private static AssetRecord FieldRecord(string name) => new()
    {
        Name = name,
        Type = "fieldtest",
        GdtName = "fieldtest.gdt",
    };

    /// <summary>The value editor showing <paramref name="row"/> in the open editor (scrolled into view).</summary>
    private static PropertyEditorView FieldView(Window window, AssetEditorViewModel tab, PropertyItemViewModel row)
    {
        tab.RevealProperty(row.Key);
        Pump();
        window.UpdateLayout();
        Pump();
        return window.GetVisualDescendants().OfType<PropertyEditorView>()
            .First(v => ReferenceEquals(v.DataContext, row) && v.IsEffectivelyVisible);
    }

    private static Popup PickerOf(PropertyEditorView view) =>
        view.GetVisualDescendants().OfType<Popup>().First(p => p.Classes.Contains("colorpicker"));

    private static void TypeText(Window window, string text)
    {
        window.KeyTextInput(text);
        Pump();
    }

    private static void FieldChecksInWindow(Window window, MainViewModel vm, string install,
        List<FieldFiles.PickRequest> picks, Action<string?> setPick, string outDir)
    {
        var record = FieldRecord("fieldtest_gun");
        record.Properties["modelFile"] = @"weapons\\gun.xmodel_bin";
        record.Properties["startColor"] = "0.0392157 0.14902 0.705882 1";
        record.Properties["fogcolor"] = "0.571954 0.751095 0.992157 1";
        record.Properties["aiVsAiAccuracyGraph"] = "default.accu";
        record.Properties["gunModel"] = "no_such_model";
        vm.OpenAsset(record);
        Pump();
        var tab = vm.ActiveTab!;
        PropertyItemViewModel Row(string key) => tab.AllSentinel.All.First(p => p.Key == key);
        Check("fields: each kind gets its editor (file, colour, file list, bone)",
            Row("modelFile") is FilePropertyViewModel && Row("flashEffect") is FilePropertyViewModel
            && Row("startColor") is ColorPropertyViewModel && Row("aiVsAiAccuracyGraph") is FileListPropertyViewModel
            && Row("attachViewModelTag1") is BonePropertyViewModel);

        // ── Files: … browses from the field's folder and writes the GDT spelling ──
        var model = (FilePropertyViewModel)Row("modelFile");
        WaitFor(() => FieldFiles.ProbesIdle);
        Pump();
        Check($"file: an existing file has no ⚠ ({model.Problem ?? "none"})", !model.HasProblem);
        var view = FieldView(window, tab, model);
        var box = view.GetVisualDescendants().OfType<TextBox>().First();
        Click(window, box);
        var browse = view.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("browse"));
        Check("file: … shows on the focused row, enabled with the install", browse.IsEffectivelyVisible && browse.IsEffectivelyEnabled);
        setPick(Path.Combine(install, "model_export", "weapons", "sub", "other.xmodel_bin"));
        Click(window, browse);
        Check($"file: … opens the picker in the current file's folder, offering models first ({picks.LastOrDefault()?.StartDir})",
            picks.Count == 1 && picks[0].StartDir == Path.Combine(install, "model_export", "weapons")
            && picks[0].Types[0].Patterns!.Contains("*.xmodel_bin") && picks[0].Types[^1].Name == "All files");
        Check($"file: the pick is written relative to model_export, backslashes escaped ('{model.Value}')",
            model.Value == @"weapons\\sub\\other.xmodel_bin" && box.Text == model.Value);
        tab.UndoCommand.Execute(null);
        Pump();
        Check("file: Ctrl+Z takes the pick back", model.Value == @"weapons\\gun.xmodel_bin");

        setPick(Path.Combine(Path.GetTempPath(), "elsewhere.xmodel_bin"));
        Click(window, browse);
        Check($"file: a file outside model_export is refused with one line ('{vm.Status}')",
            model.Value == @"weapons\\gun.xmodel_bin" && vm.Status == "elsewhere.xmodel_bin isn't inside model_export. Pick a file in that folder.");
        var collision = (FilePropertyViewModel)Row("collisionMap");
        Check("file: the deffile's filter becomes the picker's file type",
            FieldFiles.RequestFor(collision.Def, "").Types[0] is { Name: "Collision Map Files", Patterns: ["*.map"] });
        var texture = new PropertyDef("baseImage", "Base Image", "Image", PropertyKind.Text, "") { FileKind = PropertyFileKind.Texture };
        Check("file: an image outside the install keeps its absolute path, as APE writes them",
            FieldFiles.ValueFor(texture, @"D:\art\wall.tif").Value == @"D:\\art\\wall.tif");

        // ── Missing files: typed, committed, checked off the UI thread, ⚠ beside the value ──
        Click(window, box);
        box.SelectAll();
        TypeText(window, @"weapons\\missing.xmodel_bin");
        Key(window, Avalonia.Input.Key.Enter);
        var unseen = @"weapons\\never_" + Guid.NewGuid().ToString("N")[..6] + ".xmodel_bin";
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var firstAnswer = FieldFiles.MissingProblem(model.Def, unseen);
        var firstMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        Gate($"file: validation never waits on the disk (an unseen path answers 'not known yet' in {firstMs:0.000} ms)",
            firstAnswer is null, firstMs < 1);
        WaitFor(() => FieldFiles.MissingProblem(model.Def, unseen) is not null);
        Check("file: and the background check fills it in", FieldFiles.MissingProblem(model.Def, unseen) is not null);
        WaitFor(() => model.HasProblem);
        Check($"file: a missing file gets the ⚠ line ('{model.Problem}')", model.Problem == @"No file at ‘model_export\weapons\missing.xmodel_bin’");
        Check("file: and it is listed with the asset's problems", tab.Problems.Any(p => p.Key == "modelFile"));
        var flash = (FilePropertyViewModel)Row("flashEffect");
        flash.RawValue = "weapon/fx_muz_energy_pistol_1p";
        WaitFor(() => FieldFiles.ProbesIdle);
        Pump();
        Check("file: a name without an extension (an fx name the game resolves) is never flagged", !flash.HasProblem);
        tab.UndoCommand.Execute(null);
        tab.UndoCommand.Execute(null);
        WaitFor(() => !model.HasProblem);
        Check("file: undoing back to the real file clears the ⚠", model.Value == @"weapons\\gun.xmodel_bin" && !model.HasProblem);

        // ── Colour: swatch → picker; a component edit keeps the others' exact text ──
        var color = (ColorPropertyViewModel)Row("startColor");
        var colorView = FieldView(window, tab, color);
        var swatch = colorView.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("swatch"));
        Check($"colour: the swatch paints the value ({color.Hex})", color.Hex == "#0A26B4");
        Click(window, swatch);
        var picker = PickerOf(colorView);
        var panel = picker is { IsOpen: true, Child: Border { Child: ColorPanel p } } ? p : null;
        Check("colour: a click on the swatch opens the picker on this row's colour",
            panel is not null && ReferenceEquals(panel.DataContext, color) && panel.Fields.Length == 4);
        if (panel is not null && TopLevel.GetTopLevel(panel) is { } popup)
        {
            Pump(50);
            var focused = popup.FocusManager?.GetFocusedElement();
            Check($"colour: the keyboard starts on R ({focused?.GetType().Name})", ReferenceEquals(focused, panel.Fields[0]));
            Check($"colour: the Inspector stays on the colour's row, not a component ({tab.FocusedProperty?.Key})", tab.FocusedProperty == color);
            Capture(window, Path.Combine(outDir, "50-colour-picker.png"));
            popup.KeyPress(Avalonia.Input.Key.Up, RawInputModifiers.Control, PhysicalKey.ArrowUp, null);
            popup.KeyRelease(Avalonia.Input.Key.Up, RawInputModifiers.Control, PhysicalKey.ArrowUp, null);
            Pump();
            Check($"colour: Ctrl+↑ on R steps it and leaves G, B, A as written ('{color.Value}')",
                color.Value == "0.0492157 0.14902 0.705882 1");
            panel.HexBox.Focus();
            panel.HexBox.SelectAll();
            popup.KeyTextInput("#FF8000");
            popup.KeyPress(Avalonia.Input.Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Pump();
            Check($"colour: hex sets R, G, B in the GDT spelling, alpha untouched ('{color.Value}')", color.Value == "1 0.501961 0 1");
            Check("colour: the picker's fields follow the value", panel.Fields[1].DataContext is NumberPropertyViewModel { RawValue: "0.501961" });
            popup.KeyPress(Avalonia.Input.Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Pump();
            Check($"colour: Esc closes the picker and the keyboard goes back to the swatch ({window.FocusManager?.GetFocusedElement()?.GetType().Name})",
                !picker.IsOpen && window.FocusManager?.GetFocusedElement() == swatch);
        }
        tab.UndoCommand.Execute(null);
        tab.UndoCommand.Execute(null);
        Pump();
        Check($"colour: two undos restore the exact original ('{color.Value}')", color.Value == "0.0392157 0.14902 0.705882 1");
        var colorBox = colorView.GetVisualDescendants().OfType<TextBox>().First();
        Click(window, colorBox);
        colorBox.SelectAll();
        TypeText(window, "1 1 1 0.5");
        Key(window, Avalonia.Input.Key.Enter);
        Check($"colour: the text stays editable and the swatch follows ({color.Hex}, alpha {((Avalonia.Media.ISolidColorBrush)color.Swatch).Color.A})",
            color.Value == "1 1 1 0.5" && color.Hex == "#FFFFFF" && ((Avalonia.Media.ISolidColorBrush)color.Swatch).Color.A == 128);
        var fog = (ColorPropertyViewModel)Row("fogcolor");
        Check("colour: no alpha field where the deffile hides alpha", fog.Components.Count == 3);

        // ── File list: ▾ / Alt+↓ lists the folder; arrows walk it; typing narrows; a click commits ──
        var list = (FileListPropertyViewModel)Row("aiVsAiAccuracyGraph");
        var listView = FieldView(window, tab, list);
        var suggest = listView.GetVisualDescendants().OfType<SuggestBox>().First();
        Check("file list: ▾ shows with the install", suggest.Arrow.IsEffectivelyVisible);
        Click(window, suggest.Box);
        var rowBefore = tab.FocusedProperty;
        Click(window, suggest.Arrow);
        WaitFor(() => suggest.IsOpen);
        var items = (suggest.List.ItemsSource as IEnumerable<string>)?.ToList() ?? new();
        Check($"file list: ▾ lists the folder's files ({string.Join(", ", items)})",
            suggest.IsOpen && items.SequenceEqual(new[] { "assault_rifle.accu", "default.accu", "notes.txt", "smg.accu" })
            && (string?)suggest.List.SelectedItem == "default.accu");
        Capture(window, Path.Combine(outDir, "51-file-list.png"));
        Check("file list: the keyboard stays in the field while the list is open", window.FocusManager?.GetFocusedElement() == suggest.Box);
        Key(window, Avalonia.Input.Key.Down);
        Check($"file list: ↓ walks the list into the field, not the form's rows ('{suggest.Box.Text}')",
            suggest.Box.Text == "notes.txt" && list.Value == "default.accu" && tab.FocusedProperty == rowBefore);
        Key(window, Avalonia.Input.Key.Enter);
        Check($"file list: Enter commits the highlighted file and closes ('{list.Value}')", list.Value == "notes.txt" && !suggest.IsOpen);
        Key(window, Avalonia.Input.Key.Down, RawInputModifiers.Alt);
        WaitFor(() => suggest.IsOpen);
        suggest.Box.SelectAll();
        TypeText(window, "smg");
        items = (suggest.List.ItemsSource as IEnumerable<string>)?.ToList() ?? new();
        Check($"file list: typing narrows the list ({string.Join(", ", items)})", items.SequenceEqual(new[] { "smg.accu" }) && list.Value == "notes.txt");
        Pump();
        var item = suggest.List.ContainerFromIndex(0) as Control;
        if (item is not null && TopLevel.GetTopLevel(item) is { } listRoot)
        {
            var at = item.TranslatePoint(new Point(item.Bounds.Width / 2, item.Bounds.Height / 2), listRoot)!.Value;
            listRoot.MouseMove(at);
            listRoot.MouseDown(at, MouseButton.Left);
            listRoot.MouseUp(at, MouseButton.Left);
            Pump();
        }
        Check($"file list: a click on a suggestion commits it, as one edit ('{list.Value}')",
            list.Value == "smg.accu" && !suggest.IsOpen && record.History.CanUndo);
        Check("file list: the keyboard stayed in the field", window.FocusManager?.GetFocusedElement() == suggest.Box);
        Key(window, Avalonia.Input.Key.Down, RawInputModifiers.Alt);
        WaitFor(() => suggest.IsOpen);
        TypeText(window, "x");
        Key(window, Avalonia.Input.Key.Escape);
        Key(window, Avalonia.Input.Key.Escape);
        Check($"file list: Esc reverts the typing and closes the list ('{suggest.Box.Text}')",
            !suggest.IsOpen && suggest.Box.Text == "smg.accu" && list.Value == "smg.accu");

        // ── Bones: a model that can't be resolved leaves a plain text field, without noise ──
        var bone = (BonePropertyViewModel)Row("attachViewModelTag1");
        var boneView = FieldView(window, tab, bone);
        var boneBox = boneView.GetVisualDescendants().OfType<SuggestBox>().First();
        Click(window, boneBox.Box);
        WaitFor(() => bone.Suggestions is not null);
        Pump();
        Check($"bones: an unresolvable model ('{bone.Model}') leaves a plain text field (no ▾, no ⚠)",
            bone.Model == "no_such_model" && !boneBox.Arrow.IsVisible && !bone.HasProblem);
        TypeText(window, "tag_flash");
        Key(window, Avalonia.Input.Key.Enter);
        Check("bones: free text is still a value", bone.Value == "tag_flash");

        Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
        Capture(window, Path.Combine(outDir, "52-light-fields.png"));
        Application.Current.RequestedThemeVariant = ThemeVariant.Dark;
        Capture(window, Path.Combine(outDir, "53-fields.png"));
    }

    /// <summary>
    /// The field editors on the real install (<c>--fields-live</c>): the real deffiles' mapping, bones read from a real
    /// weapon's view model, a real FileCombo folder, the missing-file ⚠ against real values, and keystroke → response on
    /// the full corpus. Read-only: edits stay in memory and are undone, and nothing is saved.
    /// </summary>
    private static void RunLiveFieldChecks()
    {
        var (window, vm) = StartLive(out _);
        if (window is null)
            return;
        var db = DatabaseOf(vm);
        var results = new List<PerfResult>();
        try
        {
            LiveFieldSchema();
            LiveMissingFileSweep(db);
            LiveWeaponFields(window, vm, db, results);
            LiveColourField(window, vm, db, results);
            LiveLineFields(window, vm, db, results);
            LiveMaterialUi(window, vm, db, results);
            GateResults("real install, fields", results);
            var materialized = db.Assets.Count(a => a.IsMaterialized);
            Check($"fields (live): laziness holds — {materialized:N0} records materialized", materialized < 1000);
        }
        finally
        {
            window.Close();
        }
    }

    private static void LiveFieldSchema()
    {
        var bw = SchemaRegistry.Get("bulletweapon")!;
        Check("fields (live): bulletweapon's BoneCombos name gunModel",
            bw.Find("persistentViewModelTag1") is { TextEditor: PropertyTextEditor.Bone, ModelKeys: ["gunModel"] }
            && bw.Find("attachWorldModelTag1") is { TextEditor: PropertyTextEditor.Bone, ModelKeys: ["worldModel"] });
        Check("fields (live): aiVsAiAccuracyGraph lists share/raw/accuracy/aivsai/",
            bw.Find("aiVsAiAccuracyGraph") is { TextEditor: PropertyTextEditor.FileList, RelativeRoot: "share/raw/accuracy/aivsai/" });
        var beam = SchemaRegistry.Get("beam");
        Check("fields (live): beam's colours show alpha (.SetShowAlpha(true))", beam?.Find("startColor") is { TextEditor: PropertyTextEditor.Color, ShowAlpha: true });
        var types = new[] { "bulletweapon", "projectileweapon", "attachmentunique", "vehicle", "xmodel", "xanim", "beam", "fx", "material", "image" };
        int files = 0, colours = 0, lists = 0, bones = 0, labelled = 0;
        foreach (var type in types)
            if (SchemaRegistry.Get(type) is { } s)
                foreach (var p in s.Properties)
                {
                    files += p.FileKind != PropertyFileKind.None ? 1 : 0;
                    colours += p.TextEditor == PropertyTextEditor.Color ? 1 : 0;
                    lists += p.TextEditor == PropertyTextEditor.FileList ? 1 : 0;
                    bones += p.TextEditor == PropertyTextEditor.Bone ? 1 : 0;
                    labelled += p.Label.EndsWith(" Forward", StringComparison.Ordinal) || p.Label.EndsWith(" Pitch", StringComparison.Ordinal) ? 1 : 0;
                }
        Console.WriteLine($"info  fields (live): in {types.Length} common types — {files} file, {colours} colour, {lists} file-list, {bones} bone fields; {labelled} vector components named by .SetLabels");
        Check("fields (live): .SetLabels names vector components in the real deffiles", labelled > 0);
    }

    /// <summary>The ⚠ against real values: model files of the first xmodels, checked in the background, then counted.</summary>
    private static void LiveMissingFileSweep(AssetDatabase db)
    {
        var def = SchemaRegistry.Get("xmodel")?.Find("filename");
        if (def is null)
            return;
        var values = db.Assets.Where(a => a.Type.Equals("xmodel", StringComparison.OrdinalIgnoreCase)).Take(400)
            .Select(a => a.ScanProperties.GetValueOrDefault("filename", "")).Where(v => v.Length > 0).ToList();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        foreach (var v in values)
            FieldFiles.MissingProblem(def, v);
        var queueMs = clock.Elapsed.TotalMilliseconds;
        WaitFor(() => FieldFiles.ProbesIdle);
        var missing = values.Select(v => FieldFiles.MissingProblem(def, v)).OfType<string>().ToList();
        Console.WriteLine($"info  fields (live): {missing.Count} of {values.Count} xmodel files missing on disk{string.Concat(missing.Take(3).Select(m => " · " + m))}");
        Check($"fields (live): asking about {values.Count} model files costs the UI thread {queueMs:0.0} ms (checks run in the background)",
            queueMs < 16 * 4);
    }

    private static void LiveWeaponFields(Window window, MainViewModel vm, AssetDatabase db, List<PerfResult> results)
    {
        // A weapon whose view model resolves to an xmodel_bin on disk.
        AssetRecord? weapon = null;
        foreach (var a in db.Assets.Where(a => a.Type.Equals("bulletweapon", StringComparison.OrdinalIgnoreCase)).Take(400))
        {
            var gun = a.ScanProperties.GetValueOrDefault("gunModel", "");
            if (gun.Length == 0 || FieldFiles.Resolve?.Invoke("xmodel", gun) is not { } xm)
                continue;
            var lod0 = xm.ScanProperties.GetValueOrDefault("filename", "");
            if (lod0.Length > 0 && File.Exists(Path.Combine(FieldFiles.Root!, "model_export", lod0.Replace(@"\\", @"\").Replace(".xmodel_export", ".xmodel_bin"))))
            {
                weapon = a;
                break;
            }
        }
        Check($"fields (live): a weapon with a readable view model ({weapon?.Name})", weapon is not null);
        if (weapon is null)
            return;
        vm.OpenAsset(weapon);
        Settle(window);
        var tab = vm.ActiveTab!;
        var bone = tab.AllSentinel.All.OfType<BonePropertyViewModel>().FirstOrDefault(b => !b.IsRuleHidden && !b.IsRuleDisabled && b.Def.ModelKeys.Contains("gunModel"));
        Check($"fields (live): {weapon.Name} has a visible gunModel bone field ({bone?.Key})", bone is not null);
        if (bone is null)
            return;
        var view = FieldView(window, tab, bone);
        var suggest = view.GetVisualDescendants().OfType<SuggestBox>().First();
        Click(window, suggest.Box);
        var opened = System.Diagnostics.Stopwatch.StartNew();
        Key(window, Avalonia.Input.Key.Down, RawInputModifiers.Alt);
        WaitFor(() => suggest.IsOpen);
        var firstMs = opened.Elapsed.TotalMilliseconds;
        var bones = (suggest.List.ItemsSource as IEnumerable<string>)?.ToList() ?? new();
        Check($"bones (live): Alt+↓ lists the bones of {bone.Model} ({bones.Count}: {string.Join(", ", bones.Take(5))}…; first read {firstMs:0} ms, off the UI thread)",
            suggest.IsOpen && (bones.Contains("tag_weapon") || bones.Contains("tag_origin"))
            && bones.All(b => b == b.ToLowerInvariant()));
        Key(window, Avalonia.Input.Key.Escape);

        results.Add(Time(window, "Bone field: Alt+↓ opens the list", PerfBudgets.Frame, 3, 15,
            _ => KeyStroke(window, Avalonia.Input.Key.Down, RawInputModifiers.Alt),
            after: _ => { KeyStroke(window, Avalonia.Input.Key.Escape); Settle(window); }));
        results.Add(Time(window, "Bone field: keystroke", PerfBudgets.Frame, 3, 25,
            i => TypeChar(window, (char)('a' + i % 26)),
            before: i =>
            {
                if (i % 10 == 0)
                    suggest.Box.Text = bone.RawValue;
            }));
        suggest.Box.Text = bone.RawValue;

        var list = tab.AllSentinel.All.OfType<FileListPropertyViewModel>().FirstOrDefault(p => !p.IsRuleHidden && !p.IsRuleDisabled);
        if (list is not null)
        {
            var listBox = FieldView(window, tab, list).GetVisualDescendants().OfType<SuggestBox>().First();
            Click(window, listBox.Box);
            Key(window, Avalonia.Input.Key.Down, RawInputModifiers.Alt);
            WaitFor(() => listBox.IsOpen);
            var files = (listBox.List.ItemsSource as IEnumerable<string>)?.ToList() ?? new();
            Check($"file list (live): {list.Key} lists {FieldFiles.RootName(list.Def)} ({files.Count}: {string.Join(", ", files.Take(4))}…)", files.Count > 0);
            Key(window, Avalonia.Input.Key.Escape);
        }

        var file = tab.AllSentinel.All.OfType<FilePropertyViewModel>().FirstOrDefault(p => !p.IsRuleHidden && !p.IsRuleDisabled);
        if (file is not null)
        {
            var box = FieldView(window, tab, file).GetVisualDescendants().OfType<TextBox>().First();
            Click(window, box);
            results.Add(Time(window, "File field: keystroke", PerfBudgets.Frame, 3, 25,
                i => TypeChar(window, (char)('a' + i % 26)),
                before: i =>
                {
                    if (i % 10 == 0)
                        box.Text = file.RawValue;
                }));
            results.Add(Time(window, "File field: Enter commits (⚠ checked off-thread)", PerfBudgets.Frame, 3, 15,
                _ => KeyStroke(window, Avalonia.Input.Key.Enter),
                before: i => box.Text = $@"fx\\apex_check\\missing_{i}_" + Guid.NewGuid().ToString("N")[..6] + ".efx"));
            WaitFor(() => file.HasProblem);
            Check($"file (live): a missing file gets its ⚠ ('{file.Problem}')", file.Problem?.StartsWith("No file at", StringComparison.Ordinal) == true);
            tab.UndoCommand.Execute(null);
            Settle(window);
        }
        while (tab.CanUndo)
            tab.UndoCommand.Execute(null);
        Settle(window);
        Check("fields (live): every edit undone (nothing to save)", weapon.CountSessionChanges() == 0);
    }

    private static void LiveColourField(Window window, MainViewModel vm, AssetDatabase db, List<PerfResult> results)
    {
        var beam = db.Assets.FirstOrDefault(a => a.Type.Equals("beam", StringComparison.OrdinalIgnoreCase));
        if (beam is null)
        {
            Console.WriteLine("info  fields (live): no beam asset — colour field not timed");
            return;
        }
        vm.OpenAsset(beam);
        Settle(window);
        var tab = vm.ActiveTab!;
        var color = tab.AllSentinel.All.OfType<ColorPropertyViewModel>().FirstOrDefault(p => !p.IsRuleHidden && !p.IsRuleDisabled);
        Check($"colour (live): {beam.Name} has a colour field ({color?.Key} = '{color?.Value}')", color is not null);
        if (color is null)
            return;
        var view = FieldView(window, tab, color);
        var box = view.GetVisualDescendants().OfType<TextBox>().First();
        var swatch = view.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("swatch"));
        Click(window, box);
        results.Add(Time(window, "Colour field: keystroke", PerfBudgets.Frame, 3, 25,
            i => TypeChar(window, (char)('0' + i % 10)),
            before: i =>
            {
                if (i % 10 == 0)
                    box.Text = color.RawValue;
            }));
        box.Text = color.RawValue;
        var swatchAt = CentreOf(window, swatch);
        var picker = PickerOf(view);
        var opened = 0;
        results.Add(Time(window, "Colour swatch: click opens the picker", PerfBudgets.Frame, 2, 10,
            _ => ClickAt(window, swatchAt),
            after: _ =>
            {
                opened += picker.IsOpen ? 1 : 0;
                picker.IsOpen = false;
                Settle(window);
            }));
        Check($"colour (live): every timed click opened the picker ({opened} of 12)", opened == 12);
        while (tab.CanUndo)
            tab.UndoCommand.Execute(null);
        Settle(window);
        Check("colour (live): nothing changed", beam.CountSessionChanges() == 0);
    }

    /// <summary>Narrow table cells: the value keeps the room — browse and ▾ go, the swatch stays.</summary>
    private static void FieldNarrowChecks(Window window)
    {
        var schema = SchemaRegistry.Get("fieldtest")!;
        var file = new FilePropertyViewModel(schema.Find("modelFile")!, @"weapons\\gun.xmodel_bin");
        var color = new ColorPropertyViewModel(schema.Find("startColor")!, "1 0 0 1");
        var list = new FileListPropertyViewModel(schema.Find("aiVsAiAccuracyGraph")!, "smg.accu");
        var host = new StackPanel { Width = 100, Spacing = 4 };
        foreach (var row in new PropertyItemViewModel[] { file, color, list })
            host.Children.Add(new PropertyEditorView { DataContext = row });
        var narrow = new Window { Width = 300, Height = 200, Content = host };
        narrow.Show();
        Pump();
        narrow.UpdateLayout();
        var views = host.Children.OfType<PropertyEditorView>().ToList();
        Check("narrow cell: no browse button over a file path",
            !views[0].GetVisualDescendants().OfType<Button>().Any(b => b.Classes.Contains("browse") && b.IsEffectivelyVisible));
        Check("narrow cell: the colour swatch stays",
            views[1].GetVisualDescendants().OfType<Button>().Any(b => b.Classes.Contains("swatch") && b.IsEffectivelyVisible));
        Check("narrow cell: no ▾ over a file list",
            views[2].GetVisualDescendants().OfType<SuggestBox>().First() is { Arrow.IsVisible: false });
        narrow.Close();
    }

    /// <summary>Mock data has no install: files can't be browsed (and say why), lists offer nothing, nothing is flagged.</summary>
    private static void FieldMockChecks(Window window, MainViewModel vm)
    {
        FieldFiles.Root = null;
        var record = FieldRecord("fieldtest_mock");
        record.Properties["modelFile"] = @"weapons\\nowhere.xmodel_bin";
        vm.OpenAsset(record);
        Pump();
        var tab = vm.ActiveTab!;
        var model = (FilePropertyViewModel)tab.AllSentinel.All.First(p => p.Key == "modelFile");
        var view = FieldView(window, tab, model);
        Click(window, view.GetVisualDescendants().OfType<TextBox>().First());
        var browse = view.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("browse"));
        Check($"mock: … is disabled and says why ('{model.BrowseTip}')",
            browse.IsVisible && !browse.IsEffectivelyEnabled && model.BrowseTip == "Browsing needs the BO3 install. This is mock data.");
        Check("mock: no missing-file ⚠ without an install", !model.HasProblem);
        var list = (FileListPropertyViewModel)tab.AllSentinel.All.First(p => p.Key == "aiVsAiAccuracyGraph");
        var suggest = FieldView(window, tab, list).GetVisualDescendants().OfType<SuggestBox>().First();
        Check("mock: a file list is a plain text field (no ▾)", !suggest.Arrow.IsVisible);
    }
}
