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
