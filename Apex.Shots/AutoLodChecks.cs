using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Apex.Editor.Services.Components;
using Apex.Editor.Services.Preview.ToolsGfx;
using Apex.Editor.Services.Stock;
using Apex.Render.Data;
using Apex.Render.Data.Assets;
using Apex.Render.Data.Conversion;
using Apex.Render.Data.Hashing;

namespace Apex.Shots;

/// <summary>
/// Dragon-gated auto LOD previews: the LOD slots as the linker reads them, the hybrid cache key and file name, the
/// component registry gate (none / stocklib only / Dragon only / both, stale and capability-less entries), the
/// locator's hybrid lookup (never a stock or stale file), and the <c>bb-linker -xmodel-lods</c> contract against fake
/// Dragons written as .cmd scripts (answer, error, wrong algorithm, cancel). With the install present, APE's own autogen
/// caches of a shipped model load for the no-Dragon path and a "Dragon" copy of one is found through its report. The
/// registry is a temp <c>LOCALAPPDATA</c>; the install is only read.
/// </summary>
public partial class Program
{
    private static void RunAutoLodChecks(string outDir)
    {
        var temp = NewScratch("autolod");
        AutoLodSlotChecks();
        AutoLodKeyChecks();
        var savedLocal = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        var savedDir = Environment.GetEnvironmentVariable("BO3MODTOOLS_COMPONENTS_DIR");
        try
        {
            Environment.SetEnvironmentVariable("BO3MODTOOLS_COMPONENTS_DIR", null);
            Environment.SetEnvironmentVariable("LOCALAPPDATA", Path.Combine(temp, "localappdata"));
            InstallHashChecks(temp);
            AutoLodRegistryChecks(temp);
            AutoLodLocatorChecks(temp);
            AutoLodDragonChecks(temp);
            AutoLodInstallChecks(temp);
        }
        finally
        {
            Environment.SetEnvironmentVariable("LOCALAPPDATA", savedLocal);
            Environment.SetEnvironmentVariable("BO3MODTOOLS_COMPONENTS_DIR", savedDir);
            AutoLodPreview.Reset();
            AutoLodPreview.Locate = null;
        }
    }

    private static void AutoLodSlotChecks()
    {
        var aac = new Dictionary<string, string>
        {
            ["filename"] = @"world_war_2\\xmodels\\a\\a_lod0.xmodel_bin",
            ["autogenMediumLod"] = "1", ["autogenMediumLodPercent"] = "50", ["mediumLod"] = "",
            ["autogenLowLod"] = "1", ["autogenLowLodPercent"] = "25",
            ["autogenLowestLod"] = "1", ["autogenLowestLodPercent"] = "12",
            ["autogenLod4"] = "0", ["autogenLod4Percent"] = "13", ["lod4File"] = "",
        };
        var s = XModelLodSlots.Read(aac);
        Check($"auto lod: three auto LODs after LOD0, each from the previous file ({string.Join(", ", s.Select(x => $"{x.Index}:{x.Percent}{(x.Autogen ? "a" : "")}"))})",
            s.Count == 4 && s.Skip(1).All(x => x.Autogen && x.File == aac["filename"]) && s.Select(x => x.Percent).SequenceEqual(new[] { 100, 50, 25, 12 }));

        var mixed = new Dictionary<string, string>
        {
            ["filename"] = "a_lod0.xmodel_bin", ["mediumLod"] = "a_lod1.xmodel_bin",
            ["autogenLowLod"] = "1", ["autogenLowLodPercent"] = "40", ["lowLod"] = "stale.xmodel_bin",
            ["lowestLod"] = "", ["lod4File"] = "a_lod4.xmodel_bin",
        };
        s = XModelLodSlots.Read(mixed);
        Check("auto lod: an auto LOD after an authored one decimates that file, ignoring its own stale file",
            s.Count == 3 && s[2] is { Autogen: true, File: "a_lod1.xmodel_bin", Percent: 40 });
        Check("auto lod: the list stops at the first slot with neither a file nor auto-generate", s.All(x => x.Index < 3));
        Check("auto lod: no filename, no LODs (auto-generate needs a LOD0)",
            XModelLodSlots.Read(new Dictionary<string, string> { ["autogenMediumLod"] = "1" }).Count == 0);
        var defaults = XModelLodSlots.Read(new Dictionary<string, string> { ["filename"] = "a", ["autogenMediumLod"] = "1", ["autogenLowLod"] = "1", ["autogenLowestLod"] = "1", ["autogenLod4"] = "1" });
        Check($"auto lod: a percent the GDT leaves out is xmodel.gdf's default ({string.Join("/", defaults.Skip(1).Select(x => x.Percent))})",
            defaults.Skip(1).Select(x => x.Percent).SequenceEqual(new[] { 50, 25, 13, 13 }));
        Check("auto lod: percents are clamped and rounded",
            XModelLodSlots.Read(new Dictionary<string, string> { ["filename"] = "a", ["autogenMediumLod"] = "1", ["autogenMediumLodPercent"] = "140" })[1].Percent == 100
            && XModelLodSlots.Read(new Dictionary<string, string> { ["filename"] = "a", ["autogenMediumLod"] = "1", ["autogenMediumLodPercent"] = "12.6" })[1].Percent == 13);
    }

    private static void AutoLodKeyChecks()
    {
        var hybrid = new AutoLodAlgo("hybrid-1");
        Check($"auto lod: hybrid-1 is tagged lodalt-hybrid-1 and marked lodh1 ({hybrid.Tag}, {hybrid.Marker})", hybrid.Tag == "lodalt-hybrid-1" && hybrid.Marker == "lodh1");
        Check("auto lod: an unknown algorithm still gets a distinct marker", new AutoLodAlgo("qem").Marker == "lod-qem");
        bool rejected;
        try
        {
            _ = new AutoLodAlgo("../x");
            rejected = false;
        }
        catch (ArgumentException)
        {
            rejected = true;
        }
        Check("auto lod: an algorithm name that could leave the cache folder is refused", rejected);
        Check("auto lod: algorithms compare by name (the prepared-model cache keys on the options)", new AutoLodAlgo("hybrid-1") == hybrid);

        var sig = Md4.Seeded("apex"u8.ToArray());
        var p = XModelLodParameters.Default;
        var stock = CacheKeys.XMeshHash(sig, p);
        var stockNull = CacheKeys.XMeshHash(sig, p, null, default, null);
        var tagged = CacheKeys.XMeshHash(sig, p, algo: hybrid);
        Check("auto lod: the stock key is unchanged without an algorithm", stock.Equals(stockNull));
        Check("auto lod: the hybrid key differs from the stock one and chains the tag after the block",
            !tagged.Equals(stock) && tagged.Equals(Md4.Chain(stock, System.Text.Encoding.ASCII.GetBytes("lodalt-hybrid-1"))));
    }

    /// <summary>
    /// The install hash names the same folder as stocklib, Dragon and Blackbird do: the canonical (final) path, so a
    /// junction to the install reaches the real install's stock.db and registry. A junction stands in for subst and 8.3
    /// names (same resolution); it is removed (the link only) before the scratch folder goes.
    /// </summary>
    private static void InstallHashChecks(string temp)
    {
        Check($"install hash: the real install's folder matches the one Dragon, stocklib and Blackbird wrote ({StockDb.HashCanonical(InstallRoot)})",
            StockDb.HashCanonical(InstallRoot) == "6820b7c7f93ff5b3");
        Check("install hash: case, forward slashes and a trailing slash don't matter",
            StockDb.HashCanonical(InstallRoot.ToUpperInvariant().Replace('\\', '/') + "/") == StockDb.HashCanonical(InstallRoot));

        var real = Path.Combine(temp, "Real Install");
        Directory.CreateDirectory(Path.Combine(real, "zone"));
        var link = Path.Combine(temp, "bo3link");
        var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{real}\"")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        })!;
        mklink.WaitForExit(10_000);
        if (!Directory.Exists(link))
        {
            Console.WriteLine("SKIP  install hash: could not make a junction");
            return;
        }
        try
        {
            Check($"install hash: a junction resolves to the real folder ({StockDb.Canonical(link)})",
                StockDb.Canonical(link) == StockDb.Canonical(real) && !StockDb.Canonical(link).Contains("bo3link"));
            Check("install hash: the junction and the real path name the same stock.db",
                StockDb.DefaultPath(link) == StockDb.DefaultPath(real) && StockDb.InstallHash(link) == StockDb.InstallHash(real));
            Check("install hash: ... and the same registry folder", ComponentRegistry.InstallFolder(link) == ComponentRegistry.InstallFolder(real));
            Check("install hash: the canonical path keeps the real case (the helper is given it as --install)",
                StockDb.Canonical(link.ToUpperInvariant()).EndsWith("Real Install", StringComparison.Ordinal));
            using var library = new StockLibrary(link, Path.Combine(temp, "none", "stocklib.exe"));
            Check($"install hash: the Stock Library works on the real path ({library.Install})",
                library.Install == StockDb.Canonical(real) && library.DbPath == StockDb.DefaultPath(real));
            var missing = Path.Combine(temp, "not there");
            Check("install hash: a folder that isn't there is hashed as given (Dragon and Blackbird do the same)",
                StockDb.Canonical(missing) == missing && StockDb.InstallHash(missing) == StockDb.HashCanonical(missing));
        }
        finally
        {
            Directory.Delete(link); // the junction itself, not what it points at
        }
    }

    /// <summary>A registry entry for an install, as the component writes it.</summary>
    private static void WriteComponent(string install, string id, object entry)
    {
        var dir = ComponentRegistry.InstallFolder(install);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, id + ".json"), JsonSerializer.Serialize(entry));
    }

    private static void RemoveComponent(string install, string id)
    {
        var file = Path.Combine(ComponentRegistry.InstallFolder(install), id + ".json");
        if (File.Exists(file))
            File.Delete(file);
    }

    private static Dictionary<string, object> DragonEntry(string exe, object capabilities, object? autoLod = null)
    {
        var info = new FileInfo(exe);
        var entry = new Dictionary<string, object>
        {
            ["id"] = "dragon", ["version"] = "0.2.0", ["path"] = exe, ["exe_size"] = info.Length,
            ["exe_mtime"] = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds(),
            ["capabilities"] = capabilities, ["carry_contract"] = 1, ["stock_db_schema"] = new[] { 1, 1 },
        };
        if (autoLod is not null)
            entry["auto_lod"] = autoLod;
        return entry;
    }

    private static void AutoLodRegistryChecks(string temp)
    {
        var install = Path.Combine(temp, "Black Ops III");
        Directory.CreateDirectory(install);
        var exe = WriteFakeDragon(temp, "registry", "exit /b 0");
        var stocklibExe = Path.Combine(temp, "stocklib.exe");
        File.WriteAllText(stocklibExe, "fake");
        var stocklib = new Dictionary<string, object>
        {
            ["id"] = "stocklib", ["version"] = "0.1.0", ["path"] = stocklibExe, ["protocol"] = 1, ["cache_contract"] = 2,
            ["capabilities"] = new[] { "search", "deps", "status", "sounds" }, ["stock_db_schema"] = new[] { 1, 1 },
        };
        var dragon = DragonEntry(exe, new[] { "stock-carry", "stock-sound", "auto-lod" }, new { algo = "hybrid-1", gate_faces = 40000 });
        Check($"auto lod: the registry follows LOCALAPPDATA ({ComponentRegistry.Root})",
            ComponentRegistry.Root.StartsWith(Path.Combine(temp, "localappdata"), StringComparison.OrdinalIgnoreCase));

        // The four installs: none, only the Stock Library, only Dragon, both.
        string Matrix(bool withStocklib, bool withDragon)
        {
            RemoveComponent(install, "stocklib");
            RemoveComponent(install, "dragon");
            if (withStocklib)
                WriteComponent(install, "stocklib", stocklib);
            if (withDragon)
                WriteComponent(install, "dragon", dragon);
            var found = AutoLodPreview.Find(install);
            return found is null ? "off" : found.Algo.Name;
        }
        var none = Matrix(false, false);
        var stockOnly = Matrix(true, false);
        var dragonOnly = Matrix(false, true);
        var both = Matrix(true, true);
        Check($"auto lod: none {none}, stocklib only {stockOnly}, Dragon only {dragonOnly}, both {both}",
            none == "off" && stockOnly == "off" && dragonOnly == "hybrid-1" && both == "hybrid-1");
        Check("auto lod: no install, no Dragon", AutoLodPreview.Find(null) is null && AutoLodPreview.Find("") is null);

        // Components without the Stock Library: Dragon and Blackbird come from the registry itself (rule 9).
        Matrix(false, true);
        WriteComponent(install, "blackbird", new Dictionary<string, object> { ["id"] = "blackbird", ["version"] = "0.9.2", ["path"] = exe });
        using (var noHelper = new StockLibrary(install, Path.Combine(temp, "none", "stocklib.exe")))
        {
            var rows = Apex.Editor.ViewModels.ComponentsViewModel.Build(noHelper, "0.4.0", () => { }, () => { }, install);
            string Row(string name) => rows.FirstOrDefault(r => r.Name == name) is { } r ? $"{r.State} {r.Status}" : "missing";
            Check($"components: without stocklib, Dragon ({Row("Dragon")}), Blackbird ({Row("Blackbird")}) and Ultrasound ({Row("Ultrasound")}) come from the registry",
                Row("Dragon").StartsWith("Ok 0.2.0") && Row("Dragon").Contains("auto-lod") && Row("Blackbird") == "Ok 0.9.2" && Row("Ultrasound") == "Off not reported");
            RemoveComponent(install, "dragon");
            rows = Apex.Editor.ViewModels.ComponentsViewModel.Build(noHelper, "0.4.0", () => { }, () => { }, install);
            Check($"components: no dragon.json, Dragon not installed ({Row("Dragon")})", Row("Dragon") == "Off not installed");
            WriteComponent(install, "ultrasound", new Dictionary<string, object> { ["id"] = "ultrasound", ["version"] = "1.1.0", ["path"] = exe });
            rows = Apex.Editor.ViewModels.ComponentsViewModel.Build(noHelper, "0.4.0", () => { }, () => { }, install);
            Check($"components: an ultrasound.json, once Ultrasound writes one, shows ({Row("Ultrasound")})", Row("Ultrasound") == "Ok 1.1.0");
            RemoveComponent(install, "ultrasound");
            RemoveComponent(install, "blackbird");
        }

        // Today's Dragon (stock carry and sound, no auto-lod) gates nothing.
        WriteComponent(install, "dragon", DragonEntry(exe, new[] { "stock-carry", "stock-sound" }));
        Check("auto lod: a Dragon without auto-lod leaves the preview on APE's caches", AutoLodPreview.Find(install) is null);
        WriteComponent(install, "dragon", DragonEntry(exe, new[] { "stock-carry", "auto-lod" }));
        Check("auto lod: auto-lod without an algorithm is not trusted (its caches can't be keyed)", AutoLodPreview.Find(install) is null);
        WriteComponent(install, "dragon", DragonEntry(exe, new object[] { "stock-carry", new { id = "auto-lod", algo = "hybrid-1" } }));
        Check("auto lod: the capability as an object in the list", AutoLodPreview.Find(install)?.Algo.Name == "hybrid-1");
        WriteComponent(install, "dragon", DragonEntry(exe, new[] { "auto-lod" }, new { algo = "hybrid-1", gate_faces = 40000, max_faces = 1000000 }));
        Check($"auto lod: the gate comes with the algorithm ({AutoLodPreview.Find(install)?.Algo.GateFaces})", AutoLodPreview.Find(install)?.Algo.GateFaces == 40000);
        WriteComponent(install, "dragon", DragonEntry(exe, new[] { "auto-lod" }, new { algo = "stock", max_faces = 220000 }));
        Check("auto lod: a Dragon whose auto LODs are stock leaves the preview on APE's caches (they are the same)", AutoLodPreview.Find(install) is null);
        WriteComponent(install, "dragon", DragonEntry(exe, new Dictionary<string, object> { ["auto-lod"] = new { algo = "hybrid-2" } }));
        Check("auto lod: capabilities as an object keyed by name", AutoLodPreview.Find(install)?.Algo.Name == "hybrid-2");

        var stale = DragonEntry(exe, new[] { "auto-lod" }, new { algo = "hybrid-1" });
        stale["exe_size"] = 1L;
        WriteComponent(install, "dragon", stale);
        Check("auto lod: an entry whose exe changed size since Dragon wrote it is ignored", AutoLodPreview.Find(install) is null);
        stale = DragonEntry(exe, new[] { "auto-lod" }, new { algo = "hybrid-1" });
        stale["path"] = Path.Combine(temp, "gone", "bb-linker.exe");
        WriteComponent(install, "dragon", stale);
        Check("auto lod: an entry whose Dragon is gone is ignored", AutoLodPreview.Find(install) is null);
        File.WriteAllText(Path.Combine(ComponentRegistry.InstallFolder(install), "dragon.json"), "{ not json");
        Check("auto lod: a torn entry is ignored, quietly", AutoLodPreview.Find(install) is null);

        // Another install's Dragon is not this one's.
        WriteComponent(Path.Combine(temp, "Other Install"), "dragon", dragon);
        RemoveComponent(install, "dragon");
        Check("auto lod: the registry is per install", AutoLodPreview.Find(install) is null && AutoLodPreview.Find(Path.Combine(temp, "Other Install")) is not null);
        Check("auto lod: the install folder hash ignores case and a trailing slash",
            ComponentRegistry.InstallFolder(install.ToUpperInvariant() + "\\") == ComponentRegistry.InstallFolder(install));

        // The Stock Library's state does not depend on Dragon, and Dragon's gate not on the helper being found.
        var savedPath = Environment.GetEnvironmentVariable("STOCKLIB_PATH");
        try
        {
            Environment.SetEnvironmentVariable("STOCKLIB_PATH", null);
            Check("auto lod: without stocklib.exe the Stock Library finds no helper, Dragon or not", StockLibrary.Locate(install, null) is null);
            Environment.SetEnvironmentVariable("STOCKLIB_PATH", stocklibExe);
            Check("auto lod: with it, the helper is found whatever Dragon's entry says", StockLibrary.Locate(install, null) == stocklibExe);
        }
        finally
        {
            Environment.SetEnvironmentVariable("STOCKLIB_PATH", savedPath);
        }
    }

    /// <summary>A temp install with one fake xmodel_bin (only its bytes matter for the keys) and an empty Apex cache.</summary>
    private static (ModelCacheLocator Locator, string Bin, ToolsGfxInstall Install, ApexToolsGfxCache Cache) AutoLodTempInstall(string temp, string name)
    {
        var root = Path.Combine(temp, name);
        var bin = Path.Combine(root, "model_export", "lodtest", "lodtest_lod0.xmodel_bin");
        Directory.CreateDirectory(Path.GetDirectoryName(bin)!);
        File.WriteAllBytes(bin, Enumerable.Range(0, 4096).Select(i => (byte)(i * 7)).ToArray());
        var install = ToolsGfxInstall.FromRoot(root);
        var cache = new ApexToolsGfxCache(Path.Combine(temp, name + "-apexcache"));
        return (new ModelCacheLocator(install, cache, new CachePolicy()), bin, install, cache);
    }

    private static void WriteBlob(string path, int bytes = 64)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
    }

    private static void AutoLodLocatorChecks(string temp)
    {
        var (locator, bin, install, cache) = AutoLodTempInstall(temp, "locator");
        var hybrid = new AutoLodAlgo("hybrid-1");
        var p = XModelLodParameters.Default;
        var folder = ModelCacheLocator.XModelName(bin);
        var name = ModelCacheLocator.AutoLodFileName(bin, p, 50, hybrid)!.Value;
        var stockHash = CacheKeys.XMeshHash(CacheKeys.FileSignature(bin), p);
        Check($"auto lod: the hybrid file name carries the percent, marker and tagged key ({name.File})",
            name.File == $"pct50_lodh1_{name.Hash.ToFileName()}.lz4" && !name.Hash.Equals(stockHash));
        Check("auto lod: no file name for a missing source", ModelCacheLocator.AutoLodFileName(bin + ".missing", p, 50, hybrid) is null);

        Check("auto lod: nothing cached, nothing found (stock or hybrid)",
            locator.FindAutogenXMesh(bin, p, 50, null) is null && locator.FindAutogenXMesh(bin, p, 50, hybrid) is null);

        // A hybrid file in APE's folder is never APE's stand-in, and a stale stock file is never the hybrid one.
        WriteBlob(Path.Combine(install.XMeshCacheDir, folder, name.File));
        Check("auto lod: APE's newest-by-name fallback skips another decimator's file", locator.FindAutogenXMesh(bin, p, 50, null) is null);
        Check("auto lod: a hybrid file in APE's folder is found for the hybrid", locator.FindAutogenXMesh(bin, p, 50, hybrid) is { Match: CacheMatch.Exact, Origin: CacheOrigin.Ape });
        File.Delete(Path.Combine(install.XMeshCacheDir, folder, name.File));
        WriteBlob(Path.Combine(install.XMeshCacheDir, folder, $"pct50_{"0123456789abcdef0123456789abcdef"}.lz4"));
        Check("auto lod: without Dragon a stale APE autogen file still shows (as today)", locator.FindAutogenXMesh(bin, p, 50, null) is { Match: CacheMatch.ByName });
        Check("auto lod: with Dragon a stock file is never taken for the hybrid", locator.FindAutogenXMesh(bin, p, 50, hybrid) is null);
        WriteBlob(Path.Combine(cache.XMeshCacheDir, folder, name.File));
        Check("auto lod: the hybrid file in Apex's cache is found exactly", locator.FindAutogenXMesh(bin, p, 50, hybrid) is { Match: CacheMatch.Exact, Origin: CacheOrigin.ApexCache });
        Check("auto lod: another percent is another file", locator.FindAutogenXMesh(bin, p, 25, hybrid) is null);
        Check("auto lod: other priorities are another key", locator.FindAutogenXMesh(bin, p with { PositionPriority = 12 }, 50, hybrid) is null);
        WriteBlob(Path.Combine(cache.XMeshCacheDir, folder, ModelCacheLocator.AutoLodFileName(bin, p, 25, hybrid)!.Value.File), 3);
        Check("auto lod: a header-only (interrupted) hybrid file is not a cache", locator.FindAutogenXMesh(bin, p, 25, hybrid) is null);

        // A file Dragon reported elsewhere is found through the report, for exactly these inputs.
        var elsewhere = Path.Combine(temp, "dragon-out", "lod12.lz4");
        WriteBlob(elsewhere);
        ModelCacheLocator.RememberAutoLod(bin, p, 12, hybrid, elsewhere);
        Check("auto lod: a LOD Dragon wrote outside the caches is found through its report", locator.FindAutogenXMesh(bin, p, 12, hybrid)?.Path == elsewhere);
        File.AppendAllText(bin, "edit");
        Check("auto lod: after the source changes, the reported file no longer answers", locator.FindAutogenXMesh(bin, p, 12, hybrid) is null);
    }

    /// <summary>A fake bb-linker: a .cmd whose body is <paramref name="body"/> (it writes its arguments to args.txt first).</summary>
    private static string WriteFakeDragon(string temp, string name, string body)
    {
        var dir = Path.Combine(temp, "fake-dragon", name);
        Directory.CreateDirectory(dir);
        var exe = Path.Combine(dir, "bb-linker.cmd");
        File.WriteAllText(exe, "@echo off\r\necho %*> \"%~dp0args.txt\"\r\n" + body + "\r\n");
        return exe;
    }

    private static void AutoLodDragonChecks(string temp)
    {
        var (locator, bin, _, _) = AutoLodTempInstall(temp, "dragon");
        var root = Path.Combine(temp, "dragon");
        var hybrid = new AutoLodAlgo("hybrid-1");
        var p = XModelLodParameters.Default;
        var outDir = Path.Combine(temp, "fake-dragon", "out");
        Directory.CreateDirectory(outDir);
        var lod1 = Path.Combine(outDir, "lodtest_pct50.lz4");
        var lod2 = Path.Combine(outDir, "lodtest_pct25.lz4");
        var json = JsonSerializer.Serialize(new
        {
            algo = "hybrid-1",
            lods = new object[]
            {
                new { index = 0, faces = 1000, autogen = false, cache = "" },
                new { index = 1, faces = 498, autogen = true, cache = lod1 },
                new { index = 2, faces = 251, autogen = true, cache = lod2 },
            },
            elapsed_ms = 12,
        });
        // The preview reads ToolsGfx xmeshes: the fake hands back a copy of one of APE's (the install, read-only), or a
        // linker-format stand-in without the install.
        var sample = Directory.Exists(Path.Combine(ToolsGfxInstall.FromRoot(InstallRoot).XMeshCacheDir, "aac_armoire_closed_body_lod0"))
            ? Directory.GetFiles(Path.Combine(ToolsGfxInstall.FromRoot(InstallRoot).XMeshCacheDir, "aac_armoire_closed_body_lod0"), "pct50_*.lz4").FirstOrDefault()
            : null;
        var write = sample is null
            ? $"echo xmesh-from-fake-dragon> \"{lod1}\"\r\necho xmesh-from-fake-dragon> \"{lod2}\""
            : $"copy /y \"{sample}\" \"{lod1}\" >nul\r\ncopy /y \"{sample}\" \"{lod2}\" >nul";
        var ok = WriteFakeDragon(temp, "ok", $"{write}\r\necho {json}");
        var request = new AutoLodPreview.Request("lodtest", "lodtest|hybrid-1|ok",
            [(new XModelLodSlot(1, "lodtest\\lodtest_lod0.xmodel_bin", 50, true), bin), (new XModelLodSlot(2, "lodtest\\lodtest_lod0.xmodel_bin", 25, true), bin)], p);
        var dragon = new DragonAutoLod(ok, "0.2.0", hybrid);

        var result = AutoLodPreview.GenerateAsync(dragon, root, request, CancellationToken.None).GetAwaiter().GetResult();
        var args = File.ReadAllText(Path.Combine(Path.GetDirectoryName(ok)!, "args.txt")).Trim();
        Check($"auto lod: Dragon is asked for the model's LODs on the install ({args})", args == $"-xmodel-lods lodtest -root {(root.Contains(' ') ? $"\"{root}\"" : root)} -json");
        if (sample is null)
        {
            Check($"auto lod: LODs in a format the preview can't read are refused, not loaded ({result.Error})",
                !result.Ok && result.Error!.Contains("format") && locator.FindAutogenXMesh(bin, p, 50, hybrid) is null);
            Console.WriteLine("SKIP  auto lod: the answer and its LODs (no APE xmesh to hand back without the install)");
        }
        else
        {
            Check($"auto lod: its answer is read (algo {result.Algo}, faces {string.Join("/", result.Lods.Select(l => l.Faces))}, {result.ElapsedMs} ms)",
                result is { Ok: true, Algo: "hybrid-1", ElapsedMs: 12 } && result.Lods.Select(l => l.Faces).SequenceEqual(new long[] { 1000, 498, 251 }));
            Check("auto lod: the LODs it wrote are found for the preview",
                locator.FindAutogenXMesh(bin, p, 50, hybrid)?.Path == lod1 && locator.FindAutogenXMesh(bin, p, 25, hybrid)?.Path == lod2);
        }
        var v6 = json.Replace("lodtest_pct", "lodtest_v6_pct");
        var linkerFormat = WriteFakeDragon(temp, "linker-format", $"echo linker-xmesh-v6> \"{lod1.Replace("lodtest_pct", "lodtest_v6_pct")}\"\r\necho linker-xmesh-v6> \"{lod2.Replace("lodtest_pct", "lodtest_v6_pct")}\"\r\necho {v6}");
        result = AutoLodPreview.GenerateAsync(dragon with { Exe = linkerFormat }, root, request with { Key = "linker-format" }, CancellationToken.None).GetAwaiter().GetResult();
        Check($"auto lod: caches in the linker's format (what Dragon writes today) are refused, so the preview keeps APE's ({result.Error})",
            !result.Ok && result.Error!.Contains("linker's cache format"));

        var fail = WriteFakeDragon(temp, "fail", "echo {\"error\":\"xmodel 'lodtest' not found in the GDTs\"}\r\nexit /b 3");
        result = AutoLodPreview.GenerateAsync(dragon with { Exe = fail }, root, request with { Key = "fail" }, CancellationToken.None).GetAwaiter().GetResult();
        Check($"auto lod: a failure says Dragon's words ({result.Error})", !result.Ok && result.Error == "xmodel 'lodtest' not found in the GDTs");
        var started = File.GetLastWriteTimeUtc(Path.Combine(Path.GetDirectoryName(fail)!, "args.txt"));
        Thread.Sleep(50);
        var again = AutoLodPreview.GenerateAsync(dragon with { Exe = fail }, root, request with { Key = "fail" }, CancellationToken.None).GetAwaiter().GetResult();
        Check("auto lod: a failure is remembered: the same inputs don't run Dragon again this session",
            !again.Ok && File.GetLastWriteTimeUtc(Path.Combine(Path.GetDirectoryName(fail)!, "args.txt")) == started);

        var crash = WriteFakeDragon(temp, "crash", "echo panicked at src\\main.rs 1>&2\r\nexit /b 101");
        result = AutoLodPreview.GenerateAsync(dragon with { Exe = crash }, root, request with { Key = "crash" }, CancellationToken.None).GetAwaiter().GetResult();
        Check($"auto lod: a crash without JSON reports its first error line ({result.Error})", !result.Ok && result.Error!.Contains("panicked"));

        var wrong = WriteFakeDragon(temp, "wrong", "echo " + json.Replace("hybrid-1", "hybrid-2"));
        result = AutoLodPreview.GenerateAsync(dragon with { Exe = wrong }, root, request with { Key = "wrong" }, CancellationToken.None).GetAwaiter().GetResult();
        Check($"auto lod: LODs from another algorithm than the registry's are refused ({result.Error})", !result.Ok);

        var missing = WriteFakeDragon(temp, "missing", "echo " + json.Replace(lod2.Replace("\\", "\\\\"), "C:\\\\nowhere\\\\x.lz4"));
        result = AutoLodPreview.GenerateAsync(dragon with { Exe = missing }, root, request with { Key = "missing" }, CancellationToken.None).GetAwaiter().GetResult();
        Check($"auto lod: a LOD Dragon claims but didn't write fails the run ({result.Error})", !result.Ok && result.Error!.Contains("did not write"));

        var slow = WriteFakeDragon(temp, "slow", "ping -n 30 127.0.0.1 >nul\r\necho {}");
        using (var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(600)))
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            bool cancelled = false;
            try
            {
                AutoLodPreview.GenerateAsync(dragon with { Exe = slow }, root, request with { Key = "slow" }, cts.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }
            Check($"auto lod: closing the preview cancels the run ({clock.ElapsedMilliseconds} ms)", cancelled && clock.ElapsedMilliseconds < 10_000);
        }
        var saved = AutoLodPreview.Timeout;
        AutoLodPreview.Timeout = TimeSpan.FromMilliseconds(500);
        try
        {
            result = AutoLodPreview.GenerateAsync(dragon with { Exe = slow }, root, request with { Key = "slow-timeout" }, CancellationToken.None).GetAwaiter().GetResult();
            Check($"auto lod: a Dragon that hangs is stopped ({result.Error})", !result.Ok && result.Error!.Contains("longer"));
        }
        finally
        {
            AutoLodPreview.Timeout = saved;
        }
        result = AutoLodPreview.GenerateAsync(new DragonAutoLod(Path.Combine(temp, "no", "bb-linker.exe"), "", hybrid), root, request with { Key = "absent" }, CancellationToken.None).GetAwaiter().GetResult();
        Check($"auto lod: a Dragon that can't start is a failure, not a crash ({result.Error})", !result.Ok);
    }

    /// <summary>On the real install (read-only): a shipped model with three auto LODs in APE's cache.</summary>
    private static void AutoLodInstallChecks(string temp)
    {
        const string rel = @"world_war_2\xmodels\aac_armoire_closed_body\aac_armoire_closed_body_lod0.xmodel_bin";
        var bin = Path.Combine(InstallRoot, "model_export", rel);
        var install = ToolsGfxInstall.FromRoot(InstallRoot);
        if (!File.Exists(bin) || !Directory.Exists(Path.Combine(install.XMeshCacheDir, "aac_armoire_closed_body_lod0")))
        {
            Console.WriteLine("SKIP  auto lod: the install's aac_armoire_closed_body caches are not here");
            return;
        }
        var locator = new ModelCacheLocator(install, new ApexToolsGfxCache(Path.Combine(temp, "install-apexcache")), new CachePolicy(ConvertMissing: false));
        // ww2_house_assets.gdt: customAutogenParams 0, so APE keys with the defaults.
        var p = XModelLodParameters.Default;
        var faces = new List<int>();
        foreach (var pct in new[] { 100, 50, 25, 12 })
        {
            var loc = pct == 100 ? locator.FindXMesh(bin, p) : locator.FindAutogenXMesh(bin, p, pct, null);
            faces.Add(loc is null ? -1 : GpuMesh.FromXMesh(XMeshData.Load(loc.Path)).Surfaces.Sum(s => s.TriangleCount));
        }
        Check($"auto lod: without Dragon, APE's own auto LODs load ({string.Join(" / ", faces)} faces)",
            faces.All(f => f > 0) && faces.Zip(faces.Skip(1)).All(x => x.First > x.Second));

        var hybrid = new AutoLodAlgo("hybrid-1");
        Check("auto lod: with Dragon and nothing made yet, the hybrid LODs are missing (Dragon would be asked)",
            locator.FindAutogenXMesh(bin, p, 50, hybrid) is null);
        var stock50 = locator.FindAutogenXMesh(bin, p, 50, null)!.Path;
        var made = Path.Combine(temp, "dragon-made", "aac_pct50.lz4");
        Directory.CreateDirectory(Path.GetDirectoryName(made)!);
        File.Copy(stock50, made);
        ModelCacheLocator.RememberAutoLod(bin, p, 50, hybrid, made);
        var loaded = locator.FindAutogenXMesh(bin, p, 50, hybrid);
        Check("auto lod: a LOD Dragon made loads through the same xmesh reader",
            loaded?.Path == made && GpuMesh.FromXMesh(XMeshData.Load(loaded.Path)).Surfaces.Sum(s => s.TriangleCount) == faces[1]);
    }
}
