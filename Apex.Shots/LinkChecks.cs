using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Apex.Editor.Services;
using Apex.Editor.ViewModels;

namespace Apex.Shots;

/// <summary>
/// <c>asset://name.type</c> links (Radiant's asset hyperlinks): parsing, handing a link to the Apex already running, and
/// opening the asset (mock data).
/// </summary>
public partial class Program
{
    private static void RunLinkChecks(string outDir)
    {
        // ── Parsing: the type is what follows the last '.' ──
        static bool Parses(string text, string name, string type) => AssetLink.Parse(text) is { } l && l.Name == name && l.Type == type;
        Check("links: asset://name.type parses into the name and the type after the last '.'",
            Parses("asset://ar_standard.bulletweapon", "ar_standard", "bulletweapon")
            && Parses("asset://c_t7_mp_ruin.v2.xmodel", "c_t7_mp_ruin.v2", "xmodel"));
        Check("links: the scheme is case-insensitive, a trailing '/' is ignored and %-escapes are decoded",
            Parses("ASSET://mtl_wall.material/", "mtl_wall", "material") && Parses("asset://my%20asset.material", "my asset", "material"));
        Check("links: a link without a '.' names any type",
            Parses("asset://wpn_smg_riot", "wpn_smg_riot", ""));
        var cloneLink = AssetLink.Parse("asset://p7_zm_zod_master_switch.xmodel?stock=clone&map=zm_repl");
        Check("links: ?stock=clone&map=<map> asks for the stock asset's Create local copy dialog on that map",
            cloneLink is { Name: "p7_zm_zod_master_switch", Type: "xmodel", IsStockClone: true, Map: "zm_repl" }
            && AssetLink.Parse("asset://a.xmodel?map=mods%2Fzm_x&stock=CLONE") is { IsStockClone: true, Map: "mods/zm_x" }
            && AssetLink.Parse("asset://a.xmodel?stock=nonsense") is { IsStockClone: false, Map: "" }
            && AssetLink.Parse("asset://a.xmodel") is { IsStockClone: false });
        Check("links: a link's query is not part of the name, and a link prints as it was written",
            AssetLink.Parse("asset://my%20asset.material?stock=clone") is { Name: "my asset", Type: "material" }
            && cloneLink?.ToString() == "asset://p7_zm_zod_master_switch.xmodel?stock=clone&map=zm_repl");
        Check("links: no name, another scheme or a path is no link",
            AssetLink.Parse("asset://") is null && AssetLink.Parse("asset://.material") is null && AssetLink.Parse("https://x.y") is null
            && AssetLink.Parse("asset://a/b.xmodel") is null && AssetLink.Parse(null) is null
            && AssetLink.IsLink("asset://") && !AssetLink.IsLink("--mock"));

        // ── Handing over: a later start sends its links to the running Apex ──
        var pipe = $"Apex.Links.Shots.{Environment.ProcessId}.{Guid.NewGuid():N}";
        Check("links: with no Apex listening, a start keeps its link (and gives up quickly)", Quick(() => !LinkInstance.TryForward(["asset://a.b"], pipe)));
        var received = new BlockingCollection<string>();
        using var stop = new CancellationTokenSource();
        Check("links: the running Apex listens", LinkInstance.Listen(received.Add, stop.Token, pipe));
        // A thread of its own stands in for another process: the owner mutex is re-entrant on the thread that took it,
        // and a pool thread may be that one.
        var secondListened = true;
        var second = new Thread(() => secondListened = LinkInstance.Listen(_ => { }, stop.Token, pipe));
        second.Start();
        second.Join();
        Check("links: a second Apex doesn't take the pipe over", !secondListened);
        var sent = LinkInstance.TryForward(["asset://ar_standard.bulletweapon", "asset://mtl_wall.material"], pipe);
        var first = received.TryTake(out var a, 2000) ? a : null;
        var next = received.TryTake(out var b, 2000) ? b : null;
        Check($"links: a later start's links reach the running Apex, in order ('{first}', '{next}')",
            sent && first == "asset://ar_standard.bulletweapon" && next == "asset://mtl_wall.material");
        // Clicks in a row: each start finds a listener.
        var all = Enumerable.Range(0, 5).All(i => LinkInstance.TryForward([$"asset://n{i}.xmodel"], pipe));
        var got = Enumerable.Range(0, 5).Select(_ => received.TryTake(out var l, 2000) ? l : null).ToList();
        Check($"links: clicks in quick succession all arrive ({string.Join(", ", got)})",
            all && got.SequenceEqual(Enumerable.Range(0, 5).Select(i => $"asset://n{i}.xmodel")));
        stop.Cancel();

        // ── Opening (mock data) ──
        var lookup = new MainViewModel();
        lookup.OpenByName("wpn_smg_riot");
        var type = lookup.ActiveTab!.Record.Type;
        var vm = new MainViewModel();
        var arrived = 0;
        vm.LinkArrived += () => arrived++;
        vm.OpenLink($"asset://wpn_smg_riot.{type}");
        Pump();
        Check($"links: a link opens its asset and brings the window forward (asset://wpn_smg_riot.{type})",
            vm.ActiveTab?.Record is { Name: "wpn_smg_riot" } r && r.Type == type && arrived == 1 && !vm.IsAlertOpen);
        vm.OpenLink("asset://wpn_smg_riot.material");
        Check($"links: a link whose type doesn't match says so and opens nothing ('{vm.AlertText}')",
            vm.IsAlertOpen && vm.AlertText.Contains("no material named wpn_smg_riot") && vm.OpenTabs.Count == 1);
        vm.OpenLink("asset://.xmodel");
        Check($"links: a malformed link says what a link looks like ('{vm.AlertText}')", vm.AlertText.Contains("asset://<name>.<type>"));
    }

    private static bool Quick(Func<bool> run)
    {
        var clock = Stopwatch.StartNew();
        return run() && clock.ElapsedMilliseconds < 2000;
    }
}
