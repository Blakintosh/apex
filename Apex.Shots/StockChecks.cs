using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;
using Microsoft.Data.Sqlite;
using Apex.Editor.Commands;
using Apex.Editor.Models;
using Apex.Editor.Services;
using Apex.Editor.Services.Stock;
using Apex.Editor.ViewModels;
using Apex.Editor.Views;

namespace Apex.Shots;

/// <summary>
/// The stock library: the stock.db query layer on a small fixture database (words, paging, ranking, kinds, zones, shipped
/// names, read-only use), the merge of stock rows into search after the local ones (a local asset of the same type and
/// name wins), the read-only record, the Create local copy dialog from plan to Undo, and the helper-missing offer. The
/// helper is answered by a fake that returns real <c>deps</c> output captured from the helper. Screenshots: search with
/// stock rows, a stock record, the clone dialog. Nothing touches the install.
/// </summary>
public partial class Program
{
    private static void RunStockChecks(string outDir)
    {
        var temp = NewScratch("stock");
        var dbPath = Path.Combine(temp, "stock.db");
        BuildStockFixture(dbPath);
        StockDbChecks(dbPath);
        StockUiChecks(dbPath, temp, outDir);
    }

    // ═══ The fixture database: the helper's schema (docs/schema.md), the columns Apex reads ════════════════════════════

    private static void BuildStockFixture(string path)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
        db.Open();
        void Run(string sql)
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
        Run("PRAGMA journal_mode=DELETE");
        Run("CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT)");
        Run("CREATE TABLE zone(id INTEGER PRIMARY KEY, name TEXT NOT NULL UNIQUE COLLATE NOCASE, lang TEXT, has_assetlist INTEGER NOT NULL DEFAULT 0)");
        Run("CREATE TABLE asset(id INTEGER PRIMARY KEY, type TEXT NOT NULL, name TEXT NOT NULL COLLATE NOCASE, UNIQUE(type, name))");
        Run("CREATE INDEX asset_name ON asset(name)");
        Run("CREATE TABLE def(asset_id INTEGER NOT NULL, zone_id INTEGER NOT NULL, size INTEGER NOT NULL DEFAULT 0, nested INTEGER NOT NULL DEFAULT 0, PRIMARY KEY(asset_id, zone_id)) WITHOUT ROWID");
        Run("INSERT INTO meta VALUES('schema_version','1'),('install_path','X:\\fixture\\Black Ops III'),('built_at','1791724285'),('reader_version','fixture')");
        Run("INSERT INTO zone VALUES(1,'core_common',NULL,1),(2,'zm_levelcommon',NULL,1),(3,'zm_zod',NULL,0),(4,'en_core_common','english',1),(5,'mp_common',NULL,1)");

        var assets = new (string Type, string Name, string[] Zones, int[] Nested)[]
        {
            ("WEAPON", "wpn_smg_wasp", new[] { "zm_levelcommon" }, new[] { 0 }),
            ("WEAPON", "smg_standard_zm", new[] { "zm_levelcommon", "mp_common" }, new[] { 0, 0 }),
            ("WEAPON", "smg_standard_upgraded_zm", new[] { "zm_levelcommon" }, new[] { 0 }),
            ("WEAPON", "smg_fastburst_zm", new[] { "zm_levelcommon" }, new[] { 0 }),
            ("WEAPON", "smg_capacity_zm", new[] { "zm_levelcommon" }, new[] { 0 }),
            ("ATTACHMENT_UNIQUE", "au_smg_standard_extclip", new[] { "core_common" }, new[] { 1 }),
            ("ATTACHMENT_UNIQUE", "au_smg_standard_acog", new[] { "en_core_common", "core_common" }, new[] { 0, 1 }),
            ("XMODEL", "wpn_t7_smg_standard_view", new[] { "core_common" }, new[] { 0 }),
            ("XMODEL", "p7_zm_zod_master_switch", new[] { "zm_zod" }, new[] { 0 }),
            ("XMODELMESH", "p7_zm_zod_master_switch_lod0abc", new[] { "zm_zod" }, new[] { 1 }),
            ("XANIMPARTS", "vm_smg_standard_idle", new[] { "core_common" }, new[] { 0 }),
            ("IMAGE", "t7_icon_weapon_smg_standard_pu", new[] { "zm_levelcommon" }, new[] { 0 }),
            ("IMAGE", "$white", new[] { "core_common" }, new[] { 0 }),
            ("MATERIAL", "mc/global_invisible", new[] { "core_common" }, new[] { 0 }),
            ("MATERIAL", "mc/mtl_p7_zm_zod_master_switch_glass", new[] { "zm_zod" }, new[] { 0 }),
            ("FX", "weapon/fx_smg_shell", new[] { "core_common" }, new[] { 0 }),
            // A decoy for the underscore: LIKE would match it for "smg_s" if the underscore were a wildcard.
            ("RAWFILE", "smgxstandard_decoy", new[] { "zm_zod" }, new[] { 0 }),
        };
        long id = 0;
        foreach (var (type, name, zones, nested) in assets)
        {
            id++;
            Run($"INSERT INTO asset VALUES({id},'{type}','{name.Replace("'", "''")}')");
            for (var i = 0; i < zones.Length; i++)
                Run($"INSERT INTO def VALUES({id},(SELECT id FROM zone WHERE name='{zones[i]}'),100,{nested[i]})");
        }
    }

    // ═══ The query layer ════════════════════════════════════════════════════════════════════════════════════════════════

    private static void StockDbChecks(string dbPath)
    {
        // The folder the helper names for an install: FNV-1a 64 of the lowered path (the value is the real install's).
        const string real = @"I:\SteamLibrary\steamapps\common\Call of Duty Black Ops III";
        Check($"stock db: the install hash is the helper's folder name ({StockDb.InstallHash(real)})",
            StockDb.InstallHash(real) == "6820b7c7f93ff5b3"
            && StockDb.InstallHash(real + @"\") == StockDb.InstallHash(real.ToUpperInvariant().Replace('\\', '/'))
            && StockDb.InstallHash(@"\\?\" + real) == "6820b7c7f93ff5b3");

        var before = File.ReadAllBytes(dbPath);
        var db = new StockDb(dbPath);
        var meta = db.Meta();
        Check($"stock db: meta reads ({meta?.InstallPath}, {meta?.Zones} zones, {meta?.Assets} assets)",
            meta is { Zones: 5, Assets: 17 } && meta.InstallPath.StartsWith("X:"));

        var smg = db.Search(new StockQuery(new[] { "smg" }, Array.Empty<string>(), 100));
        Check($"stock db: a word is a substring of the name ({smg.Total} hits)",
            smg.Items.All(h => h.Name.Contains("smg", StringComparison.OrdinalIgnoreCase)) && smg.Total == smg.Items.Count && smg.Total == 12);
        Check("stock db: the LOD meshes inside models are never listed",
            db.Search(new StockQuery(new[] { "master_switch" }, Array.Empty<string>(), 50)).Items.All(h => h.EngineType != "XMODELMESH")
            && db.AssetCount() == 16);

        var words = db.Search(new StockQuery(new[] { "smg", "standard" }, Array.Empty<string>(), 100));
        Check($"stock db: every word must be in the name ({string.Join(", ", words.Items.Select(h => h.Name))})",
            words.Items.All(h => h.Name.Contains("standard")) && words.Total == 8);

        var under = db.Search(new StockQuery(new[] { "smg_s" }, Array.Empty<string>(), 100));
        Check($"stock db: an underscore is an underscore, not a wildcard ({under.Items.Count} hits, decoy {under.Items.Any(h => h.Name == "smgxstandard_decoy")})",
            under.Items.Count > 0 && under.Items.All(h => h.Name.Contains("smg_s")) && under.Items.All(h => h.Name != "smgxstandard_decoy"));

        var ranked = db.Search(new StockQuery(new[] { "smg_standard_zm" }, Array.Empty<string>(), 10));
        Check($"stock db: an exact name leads ({ranked.Items.FirstOrDefault()?.Name})", ranked.Items.FirstOrDefault()?.Name == "smg_standard_zm");
        var prefix = db.Search(new StockQuery(new[] { "smg_standard" }, Array.Empty<string>(), 10)).Items.Select(h => h.Name).ToList();
        Check($"stock db: names that start with the word come before names that contain it, shorter first ({string.Join(", ", prefix.Take(4))})",
            prefix.IndexOf("smg_standard_zm") < prefix.IndexOf("au_smg_standard_extclip") && prefix.IndexOf("smg_standard_zm") < prefix.IndexOf("smg_standard_upgraded_zm"));

        var page1 = db.Search(new StockQuery(new[] { "smg" }, Array.Empty<string>(), 5, 0));
        var page2 = db.Search(new StockQuery(new[] { "smg" }, Array.Empty<string>(), 5, 5));
        Check($"stock db: a page is a limit and an offset; the total is the whole result ({page1.Items.Count} + {page2.Items.Count} of {page1.Total})",
            page1.Items.Count == 5 && page2.Items.Count == 5 && page1.Total == page2.Total
            && !page1.Items.Select(h => h.Name).Intersect(page2.Items.Select(h => h.Name)).Any());

        var weapons = db.Search(new StockQuery(new[] { "smg" }, new[] { "weapon" }, 100));
        Check($"stock db: kinds narrow the hits, and the counts still cover every kind ({weapons.Total} weapons; kinds {string.Join(", ", weapons.KindCounts.OrderBy(k => k.Key).Select(k => k.Key + " " + k.Value))})",
            weapons.Total == 5 && weapons.Items.All(h => h.Kind == "weapon") && weapons.KindCounts["xanim"] == 1 && weapons.KindCounts["attachmentunique"] == 2);
        Check("stock db: a kind is the GDT's spelling (XANIMPARTS is xanim, ATTACHMENT_UNIQUE attachmentunique, every weapon deffile weapon)",
            StockTypes.KindOfEngine("XANIMPARTS") == "xanim" && StockTypes.KindOfEngine("ATTACHMENT_UNIQUE") == "attachmentunique"
            && StockTypes.KindOfApex("bulletweapon") == "weapon" && StockTypes.KindOfApex("dualwieldweapon") == "weapon" && StockTypes.KindOfApex("weaponcamo") == "weaponcamo");

        var acog = db.Search(new StockQuery(new[] { "acog" }, Array.Empty<string>(), 5)).Items.Single();
        Check($"stock db: zones list the asset's own definitions before nested ones and a language zone last ({string.Join(", ", acog.Zones)})",
            acog.Zones.SequenceEqual(new[] { "core_common", "en_core_common" }));
        Check("stock db: ZonesOf is the same list", db.ZonesOf("ATTACHMENT_UNIQUE", "au_smg_standard_acog").SequenceEqual(acog.Zones));

        Check("stock db: shipped names are those in a zone every map loads (core_common yes, zm_zod no)",
            db.Ships("image", "$white") && db.Ships("fx", "weapon/fx_smg_shell") && !db.Ships("xmodel", "p7_zm_zod_master_switch") && !db.Ships("xmodel", "no_such_model"));
        Check("stock db: a material is shipped under its techset folder, named without it",
            db.Ships("material", "global_invisible") && !db.Ships("material", "mtl_p7_zm_zod_master_switch_glass") && !db.Ships("material", "no_such_material"));
        Check("stock db: a weapon of any deffile is a shipped weapon", db.Ships("bulletweapon", "smg_standard_zm") && !db.Ships("bulletweapon", "smg_missing"));
        Check("stock db: always-loaded zones", db.AlwaysLoadedZones().SetEquals(new[] { "core_common", "zm_levelcommon", "mp_common" }));

        var hidden = db.NamesOfKind("weapon", new[] { "wpn_smg_wasp", "wpn_smg_riot" });
        Check($"stock db: NamesOfKind finds the local names that hide a stock asset ({string.Join(", ", hidden)})",
            hidden.SetEquals(new[] { "wpn_smg_wasp" }) && db.NamesOfKind("material", new[] { "global_invisible" }).Contains("global_invisible"));

        // Reading changes nothing, and holds nothing: a writer (the helper's refresh) commits at once between queries.
        Check("stock db: reading leaves the file as it was", File.ReadAllBytes(dbPath).AsSpan().SequenceEqual(before));
        var writer = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadWrite, Pooling = false, DefaultTimeout = 1 }.ToString());
        writer.Open();
        db.Search(new StockQuery(new[] { "smg" }, Array.Empty<string>(), 50));
        db.Ships("fx", "weapon/fx_smg_shell");
        var committed = false;
        try
        {
            using var cmd = writer.CreateCommand();
            cmd.CommandText = "BEGIN IMMEDIATE; UPDATE meta SET value='1791724286' WHERE key='built_at'; COMMIT;";
            cmd.ExecuteNonQuery();
            committed = true;
        }
        catch (SqliteException)
        {
        }
        writer.Dispose();
        Check("stock db: a refresh commits at once after a search (no reader holds a transaction)", committed && db.Meta()?.BuiltAt.ToUnixTimeSeconds() == 1791724286);
        // Put the fixture back as the UI checks expect.
        File.WriteAllBytes(dbPath, before);
    }

    // ═══ The helper, answered from captured output ═════════════════════════════════════════════════════════════════════

    private sealed class FakeStockBackend : IStockBackend
    {
        public readonly List<string> Calls = new();
        public string? ExtractedName;
        public StockExtractRequest? LastRequest;

        private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "stock", name));

        public Task<StockDeps> DepsAsync(string type, string name, string map, string? zone, string? prefix, bool renameAll, CancellationToken cancel = default)
        {
            Calls.Add($"deps {type} {name} {map} prefix={prefix} all={renameAll}");
            using var doc = JsonDocument.Parse(Fixture(type.Equals("WEAPON", StringComparison.OrdinalIgnoreCase) ? "deps_weapon.json" : "deps_model.json"));
            return Task.FromResult(StockDeps.Parse(doc.RootElement.Clone()));
        }

        public async Task<StockExtractResult> ExtractAsync(StockExtractRequest request, Action<StockProgress>? progress, CancellationToken cancel = default)
        {
            Calls.Add($"extract {request.Type} {request.Name} {request.Map} keep={request.KeepName} all={request.RenameAll} gdt={request.Gdt}");
            LastRequest = request;
            ExtractedName = request.Name;
            for (var i = 1; i <= 3; i++)
            {
                progress?.Invoke(new StockProgress("p7_zm_zod_master_switch_image_" + i, i, 3));
                await Task.Delay(20, cancel);
            }
            var manifest = Path.Combine(Path.GetTempPath(), "apex-stock-fixture-manifest.json");
            var json = $$"""
                {"root":"XMODEL {{request.Name}}","name":"{{request.Name}}","dry_run":false,"gdt":"X:\\fixture\\source_data\\stock_{{request.Name}}.gdt",
                 "entries":[{"gdf":"xmodel","name":"{{request.Name}}"},{"gdf":"material","name":"mtl_a"},{"gdf":"image","name":"i_a"}],
                 "files":[{"path":"a.png","kind":"image","bytes":10},{"path":"b.png","kind":"image","bytes":10},{"path":"c.xmodel_export","kind":"export","bytes":10},{"path":"s.gdt","kind":"gdt","bytes":10}],
                 "renames":[{"type":"IMAGE","from":"i_t7_micro","to":"zm_guns_i_t7_micro"}],
                 "zone":["xmodel,{{request.Name}}"],"skipped":[],"unresolved":[],"notes":[],"manifest":"{{manifest.Replace("\\", "\\\\")}}"}
                """;
            using var doc = JsonDocument.Parse(json);
            return StockExtractResult.Parse(doc.RootElement);
        }

        public Task<StockUndoResult> UndoAsync(string manifest, CancellationToken cancel = default)
        {
            Calls.Add("undo " + manifest);
            using var doc = JsonDocument.Parse("""{"deleted":["a","b","c","d"],"restored":[],"kept_changed":[],"missing":[],"dirs_removed":[]}""");
            return Task.FromResult(StockUndoResult.Parse(doc.RootElement));
        }

        public Task<StockDescription> DescribeAsync(string type, string name, string? zone, CancellationToken cancel = default)
        {
            Calls.Add($"describe {type} {name}");
            var keys = SchemaRegistry.Get("weapon")!.Properties.Take(60).ToList();
            var fields = string.Join(",", keys.Select((k, i) => $$"""{"key":"{{k.Key}}","value":"{{(k.Kind == PropertyKind.Number ? (i * 7 % 90 + 1).ToString() : k.Default.Replace("\\", "\\\\").Replace("\"", "\\\""))}}"}"""));
            var json = type.Equals("WEAPON", StringComparison.OrdinalIgnoreCase)
                ? $$"""{"type":"WEAPON","name":"{{name}}","zone":"zm_levelcommon","zones":["zm_levelcommon"],"gdf":"weapon.gdf","parent":null,"fields":[{{fields}}],"unrecoverable":[{"key":"flameBeamLength","why":"raw"}],"supported":true,"note":null}"""
                : $$"""{"type":"{{type}}","name":"{{name}}","zone":"core_common","zones":["core_common"],"gdf":null,"fields":[],"unrecoverable":[],"supported":false,"note":"no writer for {{type}} in v1"}""";
            using var doc = JsonDocument.Parse(json);
            return Task.FromResult(StockDescription.Parse(doc.RootElement));
        }
    }

    // ═══ Search, the record, the dialog, the offer ═════════════════════════════════════════════════════════════════════

    private static void StockUiChecks(string dbPath, string temp, string outDir)
    {
        var savedPath = Environment.GetEnvironmentVariable(StockLibrary.PathVariable);
        Environment.SetEnvironmentVariable(StockLibrary.PathVariable, null);
        var fake = new FakeStockBackend();
        var vm = new MainViewModel(null);
        var window = ShowJournalWindow(vm);
        try
        {
            vm.UseStockLibrary(StockLibrary.ForDatabase(dbPath, fake));
            vm.CloneTargetSource = () => new[] { new CloneTarget("usermap", "zm_guns", "zm_guns"), new CloneTarget("mod", "zm_moreguns", "mods/zm_moreguns") };
            StockSearchChecks(vm, window, outDir);
            StockRecordChecks(vm, window, fake, outDir);
            StockCloneChecks(vm, window, fake, outDir);
            StockOfferChecks(window, vm, temp, outDir);
        }
        finally
        {
            window.Close();
            vm.Dispose();
            Environment.SetEnvironmentVariable(StockLibrary.PathVariable, savedPath);
        }
    }

    private static List<BrowserNode> Rows(MainViewModel vm) => vm.FlatRows.Where(r => r.Asset is not null).ToList();

    private static bool Settled(MainViewModel vm) => vm.FlatRows.Any(r => r.IsStock);

    private static void StockSearchChecks(MainViewModel vm, MainWindow window, string outDir)
    {
        vm.FilterText = "smg";
        vm.ApplyFilterNow();
        Pump();
        WaitFor(() => Settled(vm));
        Pump(200);
        var rows = Rows(vm);
        var firstStock = rows.FindIndex(r => r.IsStock);
        var lastLocal = rows.FindLastIndex(r => !r.IsStock);
        Check($"stock search: stock rows come in ({rows.Count(r => r.IsStock)} of {rows.Count})", firstStock > 0 && rows.Count(r => r.IsStock) == 11);
        Check("stock search: stock rows come after the local ones", lastLocal >= 0 && lastLocal < firstStock);
        Check("stock search: a local asset of the same name hides the stock one (wpn_smg_wasp once, local)",
            rows.Count(r => r.Title == "wpn_smg_wasp") == 1 && rows.Single(r => r.Title == "wpn_smg_wasp").IsStock == false);
        Check("stock search: the local asset says it overrides stock",
            rows.Single(r => r.Title == "wpn_smg_wasp").OverridesStock && !rows.First(r => r.Title == "wpn_smg_riot").OverridesStock);
        var smgStandard = rows.Single(r => r.Title == "smg_standard_zm");
        Check($"stock search: a stock row names its zone ('{smgStandard.SourceLine}') and is read-only italic data",
            smgStandard.SourceLine == "mp_common.ff +1" && smgStandard.IsStock && smgStandard.Asset!.Stock!.EngineType == "WEAPON" && smgStandard.RowHeight > 28);
        Check("stock search: other types say theirs (attachment unique)",
            rows.Single(r => r.Title == "au_smg_standard_extclip").SourceLine == "core_common.ff · attachment unique");
        Check($"stock search: the source control counts both sides ({vm.SourceAllText} | {vm.SourceLocalText} | {vm.SourceStockText})",
            vm.SourceStockText == "Stock 12" && vm.SourceLocalText.StartsWith("Local ") && vm.ShowSourceFilter);
        Check($"stock search: results say what they are out of: the GDTs' assets and the game's ('{vm.ResultCountText}')", vm.ResultCountText == $"83 of {vm.TotalCount + 16:N0} assets");
        Check("stock search: the type facets carry the stock kinds", vm.SearchFacets.Any(f => f.Type == "xanim") && vm.SearchFacets.Any(f => f.Type == "attachmentunique"));
        window.UpdateLayout();
        // The shot shows where the local rows end and the stock rows begin.
        Tree(window).ScrollIntoView(vm.FlatRows[Math.Min(vm.FlatRows.Count - 1, vm.FlatRows.IndexOf(rows[firstStock]) + 5)]);
        Pump(100);
        window.UpdateLayout();
        Capture(window, Path.Combine(outDir, "stock-search.png"));

        vm.Source = SearchSource.Local;
        Pump(100);
        Check("stock search: Local lists no stock rows", Rows(vm).Count > 0 && Rows(vm).All(r => !r.IsStock));
        vm.Source = SearchSource.Stock;
        Pump(100);
        WaitFor(() => Rows(vm).Count > 0);
        Check("stock search: Stock lists only stock rows", Rows(vm).Count == 11 && Rows(vm).All(r => r.IsStock));
        vm.Source = SearchSource.All;
        Pump(100);

        // A kind narrows the stock side too.
        var xanim = vm.SearchFacets.First(f => f.Type == "xanim");
        xanim.SelectCommand.Execute(null);
        Pump(100);
        WaitFor(() => Rows(vm).All(r => r.Title.StartsWith("vm_")) && Rows(vm).Count > 0);
        Check($"stock search: a type facet narrows stock rows ({string.Join(", ", Rows(vm).Select(r => r.Title))})", Rows(vm).Count == 1 && Rows(vm)[0].Title == "vm_smg_standard_idle");
        vm.FilterText = "smg";
        vm.ApplyFilterNow();

        // Stock is not in the GDT tree, and a query only a GDT asset can answer leaves it out.
        vm.FilterText = "";
        vm.ApplyFilterNow();
        Pump(100);
        Check("stock search: the GDT tree never lists stock", vm.FlatRows.All(r => !r.IsStock) && !vm.ShowSourceFilter);
        vm.FilterText = "smg gdt:zm_weapons";
        vm.ApplyFilterNow();
        Pump(200);
        Check("stock search: gdt: and prop: queries are about GDT assets and list no stock", Rows(vm).Count > 0 && Rows(vm).All(r => !r.IsStock));
        vm.FilterText = "smg";
        vm.ApplyFilterNow();
        WaitFor(() => Settled(vm));
        Pump(100);
    }

    private static void StockRecordChecks(MainViewModel vm, MainWindow window, FakeStockBackend fake, string outDir)
    {
        var node = Rows(vm).First(r => r.Title == "smg_standard_zm");
        vm.OpenAsset(node.Asset!);
        Pump();
        WaitFor(() => vm.ActiveTab?.Record.Stock?.Description is not null && vm.ActiveTab?.Record.Stock?.Deps is not null);
        Pump(100);
        window.UpdateLayout();
        var tab = vm.ActiveTab!;
        Check($"stock record: it opens as a stock tab ({tab.Name}, stock {tab.IsStock})", tab.IsStock && tab.Name == "smg_standard_zm" && vm.OpenTabs.Count(t => t.Record == node.Asset) == 1);
        Check($"stock record: the fields come from describe and show in the grid ({tab.AllSentinel.All.Count} rows, type {tab.Record.Type})",
            tab.Record.Type == "weapon" && tab.AllSentinel.All.Count > 40 && fake.Calls.Any(c => c.StartsWith("describe WEAPON smg_standard_zm")));
        Check("stock record: every row is off, with the reason", tab.AllSentinel.All.All(p => p.IsRuleDisabled && p.DisabledTip == AssetEditorViewModel.StockReadOnlyReason));
        var info = tab.StockInfo!;
        Check($"stock record: the header says where it ships ({info.ZoneFile} · {info.Kind} · {info.Info})",
            info.ZoneFile == "mp_common.ff" && info.Kind == "weapon" && info.Info.StartsWith("Shipped in 2 zones") && info.Info.Contains("always loaded in multiplayer and zombies maps (mp_common, zm_levelcommon)") && info.Info.Contains("dependencies"));
        Check("stock record: the banner says to create a local copy", info.Banner.StartsWith("You can use this weapon in your map without copying it.") && info.Banner.Contains("create a local copy"));
        Check($"stock record: Uses lists what it depends on ({vm.UsesCount})", vm.Uses.Count > 10 && vm.Uses.Any(u => u.TypeName == "xmodel"));
        var reg = vm.Registry;
        Check("stock record: editing commands do not apply (rename, delete, duplicate, move, derive, compare, paste target)",
            !reg[CommandCatalog.Rename].CanRun && !reg[CommandCatalog.Delete].CanRun && !reg[CommandCatalog.Duplicate].CanRun
            && !reg[CommandCatalog.MoveTo].CanRun && !reg[CommandCatalog.Derive].CanRun && !reg[CommandCatalog.Compare].CanRun
            && !MainViewModel.CanChangeWeaponClass(tab.Record));
        Check("stock record: it has no preview (a later phase) and is no change", tab.PreviewPane is null && tab.Changes.Count == 0);
        Check("stock record: the header has the title italic, a read-only tag and Create local copy",
            window.GetVisualDescendants().OfType<Border>().Any(b => b.Name == "ReadOnlyTag" && b.IsEffectivelyVisible)
            && window.GetVisualDescendants().OfType<Button>().Any(b => b.Name == "CreateLocalCopyButton" && b.IsEffectivelyVisible)
            && window.GetVisualDescendants().OfType<SelectableTextBlock>().Any(t => t.Name == "AssetTitle" && t.Classes.Contains("stock")));
        Capture(window, Path.Combine(outDir, "stock-record.png"));

        // A type with no fields to show says so, and still offers a copy.
        var anim = Rows(vm).First(r => r.Title == "vm_smg_standard_idle");
        vm.OpenAsset(anim.Asset!, preview: true);
        WaitFor(() => vm.ActiveTab?.Record.Stock?.Description is not null);
        Pump(100);
        Check($"stock record: a type without fields says why ('{vm.ActiveTab!.EmptyText}')",
            vm.ActiveTab.IsFormEmpty && vm.ActiveTab.EmptyText.Contains("o writer for XANIMPARTS"));
        vm.Registry[CommandCatalog.CloseTab].Execute();
    }

    private static void StockCloneChecks(MainViewModel vm, MainWindow window, FakeStockBackend fake, string outDir)
    {
        var node = Rows(vm).First(r => r.Title == "wpn_t7_smg_standard_view");
        vm.OpenAsset(node.Asset!);
        Pump();
        WaitFor(() => vm.ActiveTab?.Record.Stock?.Description is not null);
        vm.CreateLocalCopyCommand.Execute(null);
        Pump();
        var dlg = vm.CloneDialog;
        Check("clone: Create local copy opens the dialog", dlg is not null && vm.IsCloneOpen && vm.IsModalOpen);
        if (dlg is null)
            return;
        WaitFor(() => dlg.Stage == CloneStage.Ready);
        Pump(100);
        window.UpdateLayout();
        Check($"clone: the plan comes from deps against the first target ({dlg.Target?.Name}; {fake.Calls.LastOrDefault(c => c.StartsWith("deps"))})",
            dlg.Stage == CloneStage.Ready && dlg.Target!.Argument == "zm_guns" && fake.Calls.Any(c => c.StartsWith("deps XMODEL wpn_t7_smg_standard_view zm_guns")));
        Check($"clone: the summary counts what a copy does ({dlg.Summary})",
            dlg.Summary.StartsWith("Copies 30") && dlg.Summary.Contains("26 already local") && dlg.Summary.Contains("7 rebuilt") && dlg.Summary.Contains("15 PNG") && dlg.Summary.Contains("8 xmodel_export"));
        Check($"clone: the button says how many assets ({dlg.CreateLabel}) and the folders show", dlg.CreateLabel == "Create 30 local assets" && dlg.SourceFolders.Count == 2 && dlg.SourceFolders[0].StartsWith(@"model_export\stock\zm_zod\"));
        var rowKinds = dlg.Rows.Select(r => r.ActionLabel).Distinct().ToList();
        Check($"clone: each row has the helper's class ({string.Join(", ", rowKinds)})",
            rowKinds.Contains("Copy, editable") && rowKinds.Contains("Rebuilt by linker") && dlg.Rows[0].Name.StartsWith("p7_zm_zod_master_switch"));
        Check("clone: the name can be kept when the helper allows it, and says why", dlg.KeepAllowed && dlg.KeepName && dlg.NameNote.Length > 0);
        dlg.NewName = true;
        WaitFor(() => dlg.Stage == CloneStage.Ready && fake.Calls.Any(c => c.Contains("all=True")));
        Check($"clone: a new name asks for every copy to be renamed with the prefix ({dlg.Prefix})", fake.Calls.Any(c => c.Contains("all=True")) && dlg.Prefix == "zm_guns_");
        dlg.KeepName = true;
        WaitFor(() => dlg.Stage == CloneStage.Ready);
        Pump(100);
        window.UpdateLayout();
        var keepShipped = window.GetVisualDescendants().OfType<CheckBox>().FirstOrDefault(c => c.Name == "CloneKeepShipped");
        Check("clone: 'Keep the shipped compiled data' is there, greyed and inert, and says it comes with the Rust linker",
            keepShipped is { IsEffectivelyVisible: true, IsEnabled: false, IsHitTestVisible: false, Focusable: false, IsChecked: false, Opacity: < 0.6 }
            && keepShipped.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text?.Contains("coming with the Rust linker") == true));
        Capture(window, Path.Combine(outDir, "stock-clone.png"));

        dlg.CreateCommand.Execute(null);
        Check("clone: Create runs the extract with progress", dlg.Stage == CloneStage.Running || dlg.Stage == CloneStage.Done);
        WaitFor(() => dlg.Stage == CloneStage.Done);
        Pump(300);
        Check($"clone: the result says what was made ({dlg.ResultTitle}; {dlg.ResultDetail})",
            dlg.Stage == CloneStage.Done && dlg.ResultTitle == "Created 3 local assets" && dlg.ResultDetail.Contains("stock_wpn_t7_smg_standard_view.gdt"));
        Check($"clone: the zone lines to add are shown ({dlg.ZoneLines})", dlg.ZoneLines == "xmodel,wpn_t7_smg_standard_view" && dlg.HasZoneLines);
        Check("clone: the extract got the target, the name rule and the GDT",
            fake.LastRequest is { Map: "zm_guns", KeepName: true, RenameAll: false, Gdt: null, Type: "XMODEL" });
        window.UpdateLayout();
        Capture(window, Path.Combine(outDir, "stock-clone-done.png"));

        dlg.UndoCommand.Execute(null);
        WaitFor(() => dlg.Stage == CloneStage.Undone);
        Pump(100);
        Check($"clone: Undo removes what was written ({dlg.ResultTitle})", dlg.Stage == CloneStage.Undone && dlg.ResultTitle.Contains("4 files removed") && fake.Calls.Any(c => c.StartsWith("undo ")));
        dlg.CloseCommand.Execute(null);
        Check("clone: closing returns to the editor", vm.CloneDialog is null && !vm.IsModalOpen);
        vm.Registry[CommandCatalog.CloseTab].Execute();

        // Radiant's hand-off: the link opens the stock record and the dialog with the link's map as the target.
        vm.OpenLink("asset://wpn_t7_smg_standard_view.xmodel?stock=clone&map=zm_moreguns");
        Pump();
        var linked = vm.CloneDialog;
        Check($"link: asset://...?stock=clone&map=... opens the stock asset and the Create local copy dialog on it ({linked?.AssetName})",
            linked is not null && vm.ActiveTab?.Record is { IsStock: true, Name: "wpn_t7_smg_standard_view" } && linked.AssetName == "wpn_t7_smg_standard_view" && vm.IsModalOpen);
        if (linked is null)
            return;
        WaitFor(() => linked.Stage == CloneStage.Ready);
        Pump(100);
        window.UpdateLayout();
        Check($"link: the map from the link is the target ({linked.Target?.Name}) and the plan was made against it",
            linked.Target is { Name: "zm_moreguns" } && fake.Calls.Any(c => c.StartsWith("deps XMODEL wpn_t7_smg_standard_view mods/zm_moreguns")));
        Capture(window, Path.Combine(outDir, "stock-link-clone.png"));
        linked.CloseCommand.Execute(null);
        vm.Registry[CommandCatalog.CloseTab].Execute();
        vm.OpenLink("asset://no_such_stock_asset_zz.xmodel?stock=clone&map=zm_guns");
        Check($"link: a link to an asset the game doesn't ship says so and opens nothing ('{vm.AlertText}')",
            vm.IsAlertOpen && vm.AlertText.Contains("doesn't ship") && !vm.IsCloneOpen);
        vm.DismissAlertCommand.Execute(null);
        vm.OpenLink("asset://p7_zm_zod_master_switch.xmodel?stock=clone&map=not_a_map");
        Pump();
        Check($"link: a map Apex doesn't know leaves the first target and says so ({vm.Status})",
            vm.CloneDialog is { Target.Name: "zm_guns" } && vm.Status.Contains("not_a_map"));
        vm.CloneDialog?.CloseCommand.Execute(null);
        vm.Registry[CommandCatalog.CloseTab].Execute();
    }

    private static void StockOfferChecks(MainWindow window, MainViewModel vm, string temp, string outDir)
    {
        // No helper and no index: the library offers to find it; nothing breaks.
        var empty = Path.Combine(temp, "no-helper");
        Directory.CreateDirectory(empty);
        var library = new StockLibrary(empty) { Backend = new FakeStockBackend() };
        vm.UseStockLibrary(library);
        vm.FilterText = "smg";
        vm.ApplyFilterNow();
        Pump(200);
        Check($"stock offer: with no helper the library is {library.State} and offers it under a search", library.State == StockState.HelperMissing && vm.ShowStockOffer && !vm.ShowSourceFilter);
        Check("stock offer: the results are the local ones, unchanged", Rows(vm).Count > 0 && Rows(vm).All(r => !r.IsStock));
        window.UpdateLayout();
        Check("stock offer: the card and its Install button are on screen",
            window.GetVisualDescendants().OfType<Border>().Any(b => b.Name == "StockOffer" && b.IsEffectivelyVisible)
            && window.GetVisualDescendants().OfType<Button>().Any(b => b.Name == "StockInstallButton" && b.IsEffectivelyVisible));
        Capture(window, Path.Combine(outDir, "stock-offer.png"));

        vm.InstallStockHelperCommand.Execute(null);
        Pump();
        Check("stock offer: Install opens the helper path panel (no feed yet)", vm.IsStockHelperOpen && vm.IsModalOpen);
        vm.StockHelperPath = Path.Combine(temp, "missing", "stocklib.exe");
        vm.AcceptStockHelperCommand.Execute(null);
        Check($"stock offer: a path with no stocklib.exe is refused ({vm.StockHelperError})", vm.IsStockHelperOpen && vm.StockHelperError.Length > 0);
        var fakeExe = Path.Combine(temp, "stocklib.exe");
        File.WriteAllText(fakeExe, "not a program");
        vm.StockHelperPath = fakeExe;
        vm.AcceptStockHelperCommand.Execute(null);
        Pump();
        Check($"stock offer: a path that exists is used and remembered ({library.ExePath})", !vm.IsStockHelperOpen && library.ExePath == fakeExe && !vm.ShowStockOffer);
        Check("stock offer: the helper is found by the setting, then STOCKLIB_PATH, then the install's bin folder",
            StockLibrary.Locate(empty, fakeExe) == fakeExe && StockLibrary.Locate(temp, null) is null);
        var bin = Path.Combine(empty, "bin", "stocklib");
        Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(bin, "stocklib.exe"), "x");
        Check("stock offer: the default place is <BO3>\\bin\\stocklib\\stocklib.exe", StockLibrary.Locate(empty, null) == Path.Combine(bin, "stocklib.exe"));
        Environment.SetEnvironmentVariable(StockLibrary.PathVariable, temp);
        Check("stock offer: STOCKLIB_PATH may name the folder", StockLibrary.Locate(empty, null) == fakeExe);
        Environment.SetEnvironmentVariable(StockLibrary.PathVariable, null);
    }
}
