using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Apex.Editor.Services;
using Apex.Editor.Services.Stock;
using Apex.Editor.ViewModels;
using Apex.Editor.Views;

namespace Apex.Shots;

/// <summary>
/// <c>--stock-live</c>: the stock library on the real install with the real helper (STOCKLIB_PATH, else the helper's
/// release build next to its repo). Search, a stock record, the shipped-name check from stock.db, then one real clone of a
/// shipped model into a throwaway target (a zone file in a temp folder; the files go where a clone's files go) and its Undo,
/// checking that undo removed every file it wrote. gdtdb's update runs after each, as the dialog does. The clone writes into
/// the install, so it runs only with APEX_STOCK_LIVE_CLONE=1.
/// </summary>
public partial class Program
{
    private const string DefaultStocklib = @"J:\Github\stocklib\target\release\stocklib.exe";

    private static void RunLiveStockChecks(string outDir)
    {
        if (Environment.GetEnvironmentVariable(StockLibrary.PathVariable) is not { Length: > 0 })
            Environment.SetEnvironmentVariable(StockLibrary.PathVariable, DefaultStocklib);
        MainViewModel vm;
        var saved = Environment.GetEnvironmentVariable("APEX_FORCE_MOCK");
        Environment.SetEnvironmentVariable("APEX_FORCE_MOCK", null);
        try { vm = new MainViewModel(NewScratch("live-session")); }
        finally { Environment.SetEnvironmentVariable("APEX_FORCE_MOCK", saved); }
        if (vm.IsMockData)
        {
            Console.WriteLine("stock live: BO3 not found, skipped");
            return;
        }
        try
        {
            LiveStock(vm, outDir);
        }
        finally
        {
            vm.Dispose();
            Apex.Editor.Models.SchemaRegistry.ResetToMock();
        }
    }

    private static void LiveStock(MainViewModel vm, string outDir)
    {
        var loadUntil = DateTime.UtcNow.AddMinutes(4);
        while (!vm.Status.StartsWith("Loaded", StringComparison.Ordinal) && DateTime.UtcNow < loadUntil)
            Pump(100);
        var window = new MainWindow { DataContext = vm };
        window.Show();
        window.Activate();
        window.UpdateLayout();
        Pump();
        var root = vm.Stock.Install!;
        Check($"stock live: the helper is found ({vm.Stock.ExePath})", vm.Stock.ExePath is not null);
        // APEX_STOCK_LIVE_PREVIEWS_ONLY=1: just the previews (the search, the weapon read and the clone take minutes).
        if (Environment.GetEnvironmentVariable("APEX_STOCK_LIVE_PREVIEWS_ONLY") == "1")
        {
            LiveStockPreviews(vm, window, root, outDir);
            window.Close();
            return;
        }

        // The index: built already, or built now; either way the refresh ends and the library is ready.
        var until = DateTime.UtcNow.AddMinutes(5);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while ((vm.Stock.IsRefreshing || !vm.Stock.CanSearch) && DateTime.UtcNow < until)
            Pump(100);
        Console.WriteLine($"stock live: index ready after {sw.Elapsed.TotalSeconds:0.0} s ({vm.Stock.Db?.AssetCount():N0} assets, state {vm.Stock.State}, status '{vm.Status}')");
        Check("stock live: the library is ready", vm.Stock.State == StockState.Ready && vm.Stock.Db!.AssetCount() > 100_000);

        // Shipped names now come from stock.db.
        Check("stock live: global_invisible ships; a made-up material does not",
            ShippedAssets.Contains("material", "global_invisible") && !ShippedAssets.Contains("material", "no_such_material_apex_zz"));

        // Search, timed: how long until the stock rows are in (the stock query runs off the UI thread).
        foreach (var typed in new[] { "ar_standard", "e", "master_switch" })
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            vm.FilterText = typed;
            vm.ApplyFilterNow();
            for (var i = 0; i < 400 && !vm.FlatRows.Any(r => r.IsStock); i++)
                Pump(10);
            Console.WriteLine($"stock live: '{typed}' shows stock rows after {timer.ElapsedMilliseconds} ms ({vm.SourceStockText}, {vm.SourceLocalText})");
        }
        vm.FilterText = "p7_zm_zod_master_switch";
        vm.ApplyFilterNow();
        var stockRows = new Func<List<BrowserNode>>(() => vm.FlatRows.Where(r => r.IsStock).ToList());
        WaitFor(() => stockRows().Count > 0);
        for (var i = 0; i < 100 && stockRows().Count == 0; i++)
            Pump(50);
        Check($"stock live: searching finds the master switch ({string.Join(", ", stockRows().Take(4).Select(r => r.Title))})",
            stockRows().Any(r => r.Title == "p7_zm_zod_master_switch" && r.Asset!.Stock!.EngineType == "XMODEL"));
        window.UpdateLayout();
        Capture(window, Path.Combine(outDir, "live-stock-search.png"));

        // A stock weapon opens with its real fields.
        // (One the install's own stock GDTs do not already define: a local asset of the same name hides the stock one.)
        vm.FilterText = "type:weapon ar_";
        vm.ApplyFilterNow();
        for (var i = 0; i < 100 && !vm.FlatRows.Any(r => r.IsStock && r.Asset!.Stock!.EngineType == "WEAPON"); i++)
            Pump(50);
        var weapon = vm.FlatRows.First(r => r.IsStock && r.Asset!.Stock!.EngineType == "WEAPON");
        Console.WriteLine($"stock live: opening the stock weapon {weapon.Title} ({weapon.SourceLine})");
        vm.OpenAsset(weapon.Asset!);
        var readBy = DateTime.UtcNow.AddSeconds(60);
        while (vm.ActiveTab?.Record.Stock?.Description is null && DateTime.UtcNow < readBy)
            Pump(100);
        var tab = vm.ActiveTab!;
        Pump(300);
        Check($"stock live: the weapon's fields are read ({tab.Record.Type}, {tab.AllSentinel.All.Count} rows, gunModel {tab.Record.Properties.GetValueOrDefault("gunModel")})",
            tab.IsStock && tab.Record.Properties.ContainsKey("gunModel") && tab.AllSentinel.All.Count > 300);
        Check("stock live: every row is off", tab.AllSentinel.All.All(p => p.IsRuleDisabled) && tab.ProblemCount == 0 && tab.Changes.Count == 0);
        var depsBy = DateTime.UtcNow.AddSeconds(90);
        while (tab.Record.Stock?.Deps is null && DateTime.UtcNow < depsBy)
            Pump(100);
        Check($"stock live: the dependencies are listed ({tab.StockInfo?.Info})", tab.Record.Stock?.Deps is not null && vm.UsesCount > 50);
        window.UpdateLayout();
        Capture(window, Path.Combine(outDir, "live-stock-record.png"));
        vm.Registry[Apex.Editor.Commands.CommandCatalog.CloseTab].Execute();

        LiveStockPreviews(vm, window, root, outDir);

        // One real clone into a throwaway target, then undo. It writes into the install (and undoes it), so it only runs
        // when asked: APEX_STOCK_LIVE_CLONE=1.
        if (Environment.GetEnvironmentVariable("APEX_STOCK_LIVE_CLONE") != "1")
        {
            Console.WriteLine("stock live: the real clone is skipped (set APEX_STOCK_LIVE_CLONE=1 to run it)");
            window.Close();
            return;
        }
        var scratch = NewScratch("stock-e2e");
        var zone = Path.Combine(scratch, "zz_stock_e2e.zone");
        File.WriteAllText(zone, ">class,zm_mod_level\n>group,modtools\n\nxmodel,skybox_default_day\nmaterial,luts_t7_default\n");
        vm.CloneTargetSource = () => new[] { new CloneTarget("usermap", "zz_stock_e2e", zone) };
        var before = Snapshot(root);
        var master = vm.FlatRows.Where(r => r.IsStock).Select(r => r.Asset!).FirstOrDefault(a => a.Name == "p7_zm_zod_master_switch");
        if (master is null)
        {
            vm.FilterText = "p7_zm_zod_master_switch";
            vm.ApplyFilterNow();
            for (var i = 0; i < 100 && !vm.FlatRows.Any(r => r.IsStock && r.Title == "p7_zm_zod_master_switch"); i++)
                Pump(50);
            master = vm.FlatRows.First(r => r.IsStock && r.Title == "p7_zm_zod_master_switch").Asset!;
        }
        vm.CreateLocalCopyOf(master);
        var dlg = vm.CloneDialog!;
        var planBy = DateTime.UtcNow.AddMinutes(3);
        while (dlg.Stage == CloneStage.Planning && DateTime.UtcNow < planBy)
            Pump(100);
        Check($"stock live: the clone plan is ready ({dlg.Stage}; {dlg.Summary}; {dlg.Error})", dlg.Stage == CloneStage.Ready && dlg.CopyCount > 20);
        window.UpdateLayout();
        Capture(window, Path.Combine(outDir, "live-stock-clone.png"));
        if (dlg.Stage != CloneStage.Ready)
            return;
        dlg.CreateCommand.Execute(null);
        var runBy = DateTime.UtcNow.AddMinutes(5);
        while (dlg.Stage == CloneStage.Running && DateTime.UtcNow < runBy)
            Pump(100);
        Pump(500);
        Check($"stock live: the clone is made ({dlg.Stage}; {dlg.ResultTitle}; {dlg.ResultDetail}; {dlg.Error})", dlg.Stage == CloneStage.Done && dlg.Result?.Manifest is { Length: > 0 });
        Console.WriteLine("stock live: zone lines: " + dlg.ZoneLines.Replace("\n", " | "));
        var gdtDone = DateTime.UtcNow.AddMinutes(4);
        while (dlg.AfterText.StartsWith("Updating", StringComparison.Ordinal) && DateTime.UtcNow < gdtDone)
            Pump(100);
        Console.WriteLine("stock live: " + dlg.AfterText);
        Check("stock live: gdt.db was updated after the clone", dlg.AfterText.Contains("gdt.db is up to date"));
        window.UpdateLayout();
        Capture(window, Path.Combine(outDir, "live-stock-clone-done.png"));
        var gdtPath = dlg.Result!.Gdt;
        Check($"stock live: the GDT and files exist ({Path.GetFileName(gdtPath)})", File.Exists(gdtPath) && dlg.Result.Files.All(f => File.Exists(f.Path)) && dlg.Result.Files.Count > 20);

        // Apex's own index follows the GDT: the new local asset is found (and now hides the stock one).
        vm.FilterText = "p7_zm_zod_master_switch";
        var localBy = DateTime.UtcNow.AddSeconds(30);
        var seen = false;
        while (!seen && DateTime.UtcNow < localBy)
        {
            vm.ApplyFilterNow();
            Pump(300);
            seen = vm.FlatRows.Any(r => r.Asset is { IsStock: false } a && a.Name == "p7_zm_zod_master_switch");
        }
        Check("stock live: the new local asset appears in the Explorer by itself", seen);

        dlg.UndoCommand.Execute(null);
        var undoBy = DateTime.UtcNow.AddMinutes(2);
        while (dlg.Stage != CloneStage.Undone && DateTime.UtcNow < undoBy)
            Pump(100);
        var gdtBy = DateTime.UtcNow.AddMinutes(4);
        while (dlg.AfterText.StartsWith("Undoing", StringComparison.Ordinal) | dlg.AfterText.StartsWith("Updating", StringComparison.Ordinal) && DateTime.UtcNow < gdtBy)
            Pump(100);
        Console.WriteLine($"stock live: {dlg.ResultTitle} · {dlg.ResultDetail} · {dlg.AfterText}");
        Check("stock live: undo ran and gdt.db was updated again", dlg.Stage == CloneStage.Undone && dlg.AfterText.Contains("gdt.db is up to date"));
        var after = Snapshot(root);
        var added = after.Except(before).ToList();
        var lost = before.Except(after).ToList();
        Check($"stock live: undo left no file behind ({added.Count} added, {lost.Count} lost{(added.Count > 0 ? ": " + string.Join(", ", added.Take(5)) : "")})", added.Count == 0 && lost.Count == 0);
        dlg.CloseCommand.Execute(null);
        window.Close();
    }

    /// <summary>
    /// Previews of stock assets (p7_zm_zod_master_switch, one of its materials, one of its images) through the real helper:
    /// the first view writes the caches (cold), later views read them. The stock folder is a copy of stock.db in a temp folder,
    /// so the first view really is the first and the caches in the user's own stock folder stay as they are. The window can't
    /// draw the D3D viewport headless, so the model and the sphere are also drawn off-screen from what the pane loaded.
    /// </summary>
    private static void LiveStockPreviews(MainViewModel vm, MainWindow window, string root, string outDir)
    {
        var scratch = NewScratch("stock-previews");
        var db = Path.Combine(scratch, "stock.db");
        File.Copy(vm.Stock.DbPath!, db);
        // APEX_STOCK_LIVE_WARM=1: the helper's summaries and preview packs come along, as they are for a user whose store has been
        // warmed (the caches next to the copied stock.db stay empty: the first view is still the first view).
        if (Environment.GetEnvironmentVariable("APEX_STOCK_LIVE_WARM") == "1")
            foreach (var folder in new[] { "packs", "summaries" })
                if (Path.Combine(Path.GetDirectoryName(vm.Stock.DbPath!)!, folder) is { } src && Directory.Exists(src))
                {
                    Directory.CreateDirectory(Path.Combine(scratch, folder));
                    foreach (var f in Directory.EnumerateFiles(src).Where(p => !Path.GetFileName(p).StartsWith('.')))
                        File.Copy(f, Path.Combine(scratch, folder, Path.GetFileName(f)));
                }
        var library = new StockLibrary(root, vm.Stock.ExePath, db);
        vm.UseStockLibrary(library);
        var cacheRoot = library.CacheRoot!;
        long Bytes() => Directory.Exists(cacheRoot) ? Directory.EnumerateFiles(cacheRoot, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length) : 0;
        Check($"stock preview: the caches go next to the stock.db in use ({cacheRoot})", !Directory.Exists(cacheRoot) && library.CanAsk);

        BrowserNode Row(string query, string name)
        {
            vm.FilterText = query;
            vm.ApplyFilterNow();
            for (var i = 0; i < 200 && !vm.FlatRows.Any(r => r.IsStock && r.Title.EndsWith(name, StringComparison.OrdinalIgnoreCase)); i++)
                Pump(50);
            return vm.FlatRows.First(r => r.IsStock && r.Title.EndsWith(name, StringComparison.OrdinalIgnoreCase));
        }
        T? Content<T>() where T : class => vm.ActiveTab?.PreviewPane?.Content as T;
        // The window can't create the D3D viewport headless: it reports that and the pane falls back, which drops the model. The
        // model the pane prepared is what is checked, so it is remembered as it arrives, and the report is forgotten before each open.
        Apex.Editor.ViewModels.ToolsGfxPreviewState? watched = null;
        Apex.Render.Assets.PreparedPreviewModel? seenModel = null;
        Apex.Render.Presentation.PreviewEnvironment? seenEnv = null;
        void Watch(Apex.Editor.ViewModels.ToolsGfxPreviewState gfx)
        {
            watched = gfx;
            seenModel = gfx.Model;
            seenEnv = gfx.Environment;
            gfx.PropertyChanged += (_, _) =>
            {
                if (gfx.Model is { } m)
                    seenModel = m;
                if (gfx.Environment is { } e)
                    seenEnv = e;
            };
        }
        void Open(Apex.Editor.Models.AssetRecord record)
        {
            Apex.Editor.Services.Preview.ToolsGfx.ToolsGfxPreviewService.InteropUnavailableReason = null;
            seenModel = null;
            vm.OpenAsset(record);
        }
        bool Until(Func<bool> done, int seconds)
        {
            var limit = DateTime.UtcNow.AddSeconds(seconds);
            while (!done() && DateTime.UtcNow < limit)
                Pump(25);
            return done();
        }

        // ── The model ──
        var modelRow = Row("type:xmodel p7_zm_zod_master_switch", "p7_zm_zod_master_switch");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Open(modelRow.Asset!);
        Check("stock preview: the tab has no preview until its fields are read", vm.ActiveTab!.PreviewPane is null);
        Until(() => Content<ModelPreviewViewModel>() is not null, 120);
        var model = Content<ModelPreviewViewModel>();
        Check("stock preview: a stock model gets the model preview", model is not null);
        if (model is null)
            return;
        Watch(model.Gfx);
        Console.WriteLine($"stock live: the model pane appeared after {clock.ElapsedMilliseconds} ms (the fields were read)");
        // read at once: with the helper's packs warm the caches are written in about half a second, so the line is gone soon
        Until(() => model.IsLoading, 5);
        var preparing = (model.IsLoading, model.LoadingText);
        Pump(250);
        window.UpdateLayout();
        Check($"stock preview: the first view says it is preparing ({preparing.LoadingText})", preparing.IsLoading && preparing.LoadingText.Contains("first view"));
        Capture(window, Path.Combine(outDir, "live-stock-preview-loading.png"));
        Until(() => seenModel is not null || model.Error is { Length: > 0 }, 240);
        var cold = clock.Elapsed;
        var drawn = seenModel;
        Console.WriteLine($"stock live: model preview cold {cold.TotalSeconds:0.0} s ({model.Stats}; error '{model.Error}'); caches now {Bytes() / 1024.0 / 1024.0:0.0} MB");
        Check($"stock preview: the model loads from the caches the helper wrote ({model.Stats})", drawn is not null && drawn.TriangleCount(0) > 10_000 && drawn.Lods.Count == 8 && drawn.BrokenMaterialCount == 0);
        var modelBytes = Bytes();
        Check("stock preview: the caches are a manifest per asset and the cache files under toolsgfx",
            File.Exists(Path.Combine(cacheRoot, "manifest", "xmodel", "p7_zm_zod_master_switch.json"))
            && Directory.GetDirectories(Path.Combine(cacheRoot, "xmeshes", "v39")).Length == 8 && modelBytes > 1_000_000);
        window.UpdateLayout();
        Capture(window, Path.Combine(outDir, "live-stock-preview-model.png"));
        // The Inspector's Uses list: a loading line while the walk runs, then the model's direct references (its materials).
        var modelOrigin = vm.ActiveTab!.Record.Stock!;
        vm.LinksTab = Apex.Editor.ViewModels.InspectorLinks.Uses;
        Console.WriteLine($"stock live: Uses while the walk runs: '{vm.ActiveLinksEmptyText}' (loading {modelOrigin.DepsLoading}, {vm.UsesCount} rows)");
        Until(() => modelOrigin.Deps is not null || modelOrigin.DepsNote.Length > 0, 240);
        Pump(300);
        Check($"stock preview: the Uses list shows the model's direct references ({vm.UsesCount} rows; note '{modelOrigin.DepsNote}')",
            vm.UsesCount >= 14 && vm.Uses.Count(u => u.TypeName == "material") >= 14);
        window.UpdateLayout();
        Capture(window, Path.Combine(outDir, "live-stock-preview-model-uses.png"));
        var env = seenEnv;
        if (drawn is not null && env is not null)
        {
            using var shooter = new OffscreenShooter(env);
            var frame = shooter.Shoot(drawn, OffscreenShooter.ThreeQuarter(drawn));
            Check($"stock preview: the model is drawn from the stock caches ({shooter.Triangles} tris)", frame is not null);
            if (frame is not null)
                Save(outDir, "live-stock-model.png", frame);
        }

        // Warm: the same asset again, in this session and as a later session would read it (nothing prepared yet).
        vm.Registry[Apex.Editor.Commands.CommandCatalog.CloseTab].Execute();
        clock.Restart();
        Open(modelRow.Asset!);
        Until(() => Content<ModelPreviewViewModel>() is not null, 120);
        if (Content<ModelPreviewViewModel>() is { } reopened)
            Watch(reopened.Gfx);
        Until(() => seenModel is not null, 120);
        Console.WriteLine($"stock live: model preview warm, reopened in the session: {clock.ElapsedMilliseconds} ms (includes the field read)");
        var install = Apex.Render.Data.ToolsGfxInstall.FromRoot(root);
        clock.Restart();
        var fresh = new Apex.Render.Data.ToolsGfxData(install, new Apex.Render.Data.Gdt.GdtIndex(), stock: new Apex.Render.Data.Assets.StockCache(cacheRoot));
        var again = Apex.Render.Assets.PreviewModelLoader.Prepare(fresh, "p7_zm_zod_master_switch", new Apex.Render.Assets.PreviewModelOptions { SkipBrokenMaterials = true });
        Console.WriteLine($"stock live: model preview warm, as a later session reads the caches: {clock.ElapsedMilliseconds} ms ({again.TriangleCount(0):N0} tris)");
        Check("stock preview: a later session shows the model from the caches alone", again.TriangleCount(0) == drawn?.TriangleCount(0));
        vm.Registry[Apex.Editor.Commands.CommandCatalog.CloseTab].Execute();

        // ── A material (its caches were written with the model: the manifest is there, so the helper is not asked) ──
        var before = Bytes();
        var materialRow = Row("type:material mtl_p7_zm_zod_master_switch_metal_01", "mtl_p7_zm_zod_master_switch_metal_01");
        clock.Restart();
        Open(materialRow.Asset!);
        Until(() => Content<MaterialPreviewViewModel>() is not null, 120);
        var material = Content<MaterialPreviewViewModel>();
        Check("stock preview: a stock material gets the material preview", material is not null);
        if (material is null)
            return;
        Watch(material.Gfx);
        Until(() => seenModel is not null || material.Error is { Length: > 0 }, 120);
        Until(() => material.Slots.Count > 0 && material.Slots.All(sl => sl.Thumbnail is not null || sl.Status is { Length: > 0 } and not "stock image"), 60);
        Console.WriteLine($"stock live: material preview {clock.ElapsedMilliseconds} ms ({material.Slots.Count} slots, {material.Slots.Count(sl => sl.Thumbnail is not null)} thumbnails; error '{material.Error}'); {(Bytes() - before) / 1024.0:0} KB written");
        Check($"stock preview: the material is lit on the sphere ({material.Summary})", seenModel is { BrokenMaterialCount: 0 });
        Check($"stock preview: its texture slots show thumbnails read from the stock image caches ({material.Slots.Count(sl => sl.Thumbnail is not null)} of {material.Slots.Count})",
            material.Slots.Count >= 3 && material.Slots.Count(sl => sl.Thumbnail is not null) >= 3);
        window.UpdateLayout();
        Capture(window, Path.Combine(outDir, "live-stock-preview-material.png"));
        if (seenModel is { } sphere && seenEnv is { } sphereEnv)
        {
            using var shooter = new OffscreenShooter(sphereEnv);
            var frame = shooter.Shoot(sphere, OffscreenShooter.SphereCamera);
            Check($"stock preview: the sphere is drawn from the stock caches ({shooter.Triangles} tris)", frame is not null);
            if (frame is not null)
                Save(outDir, "live-stock-material.png", frame);
        }
        vm.Registry[Apex.Editor.Commands.CommandCatalog.CloseTab].Execute();

        // ── Images: a colour map and a normal map (shipped normals keep gloss variance in blue: the normal-Z view is the default) ──
        foreach (var (name, shot, normal) in new[] { ("i_mtl_p7_zm_zod_master_switch_set_decal_c", "live-stock-preview-image.png", false), ("i_mtl_p7_zm_zod_master_switch_set_01_n", "live-stock-preview-image-normal.png", true) })
        {
            var imageRow = Row("type:image " + name, name);
            clock.Restart();
            Open(imageRow.Asset!);
            Until(() => Content<ImagePreviewViewModel>() is not null, 120);
            var image = Content<ImagePreviewViewModel>();
            Check($"stock preview: a stock image gets the image preview ({name})", image is not null);
            if (image is null)
                return;
            Until(() => image.Bitmap is not null || image.Error is { Length: > 0 }, 60);
            Console.WriteLine($"stock live: image preview {clock.ElapsedMilliseconds} ms ({image.Info}; error '{image.Error}')");
            Check($"stock preview: the image is decoded from its cache ({image.Info})", image.Bitmap is not null && image.Info!.Contains("stock cache"));
            Check($"stock preview: a normal map opens in the normal-Z view, a colour map in RGB (normal {normal}; N chip {image.Modes.First(m => m.IsActive).Label})",
                image.Modes.First(m => m.IsActive).Label == (normal ? "N" : "RGB"));
            window.UpdateLayout();
            Capture(window, Path.Combine(outDir, shot));
            vm.Registry[Apex.Editor.Commands.CommandCatalog.CloseTab].Execute();
        }

        Console.WriteLine($"stock live: stock caches in all: {Bytes() / 1024.0 / 1024.0:0.0} MB, {Directory.EnumerateFiles(cacheRoot, "*.lz4", SearchOption.AllDirectories).Count()} files");
    }

    /// <summary>The files a clone writes into, by relative path: its model, texture and raw folders and the GDT folder's files.</summary>
    private static HashSet<string> Snapshot(string root)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rel in new[] { @"model_export\stock", @"texture_assets\stock", @"share\raw\accuracy" })
        {
            var dir = Path.Combine(root, rel);
            if (!Directory.Exists(dir))
                continue;
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                set.Add(Path.GetRelativePath(root, f));
        }
        foreach (var f in Directory.EnumerateFiles(Path.Combine(root, "source_data"), "*", SearchOption.TopDirectoryOnly))
            set.Add(Path.GetRelativePath(root, f));
        return set;
    }
}
