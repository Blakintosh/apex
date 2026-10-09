using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Apex.Shots;

/// <summary>
/// The full run as a list of check groups, so a run can be cut by tier (<c>--fast</c>, <c>--timing</c>), by shard
/// (<c>--shard i/N</c>) or by name (<c>--group a,b</c>), and timed (<c>--timings</c>, <c>--results</c>). A no-arg run is
/// every group in this order, as it always was. docs/testing.md says which tier a new check goes in.
/// </summary>
public partial class Program
{
    /// <summary>
    /// Fast: headless, mock data or a small temp install; Full: needs the whole install (indexed or timed) or real D3D.
    /// A group's timing gates (<see cref="Gate(string, bool, bool)"/>) are the timing tier's, whatever its tier.
    /// </summary>
    private enum Tier { Fast, Full }

    /// <param name="Failure">What an exception escaping the group is reported as (the words the full run always used);
    /// null lets it escape, as the main flow's always has.</param>
    /// <param name="Gates">The group asserts timing gates, so the timing tier runs it.</param>
    /// <param name="AfterSession">Runs after the headless session's checks, outside its dispatcher (the live smoke).</param>
    private sealed record Group(string Name, Tier Tier, bool Gates, string? Failure, Action<string> Run, bool AfterSession = false);

    /// <summary>The full run, in order.</summary>
    private static readonly Group[] Groups =
    [
        new("main", Tier.Fast, false, null, RunMainFlow),
        new("shell", Tier.Fast, false, "shell checks", RunShellChecks),
        new("explorer", Tier.Fast, false, "explorer checks", RunExplorerChecks),
        new("ref", Tier.Fast, false, "ref checks", RunRefChecks),
        new("journal", Tier.Fast, true, "journal checks", RunJournalChecks),
        new("command", Tier.Fast, false, "command checks", RunCommandChecks),
        new("shell-review", Tier.Fast, false, "shell review checks", RunShellReviewChecks),
        // The palette and search menu timed on the real install (122k assets): command checks' tail, its own group.
        new("live-timing", Tier.Full, true, "command checks", _ => { LivePaletteTiming(); LiveSearchMenuTiming(); }),
        new("save-ui", Tier.Fast, true, "save ui checks", RunSaveUiChecks),
        new("placement", Tier.Fast, true, "placement checks", RunPlacementChecks),
        new("buttons", Tier.Fast, true, "button checks", RunButtonChecks),
        new("save", Tier.Fast, false, "save checks", _ => RunSaveChecks()),
        new("perf", Tier.Full, true, "perf gates", _ => RunMockPerfGates()),
        new("scroll", Tier.Fast, false, "scroll checks", _ => RunScrollChecks()),
        new("tabs", Tier.Fast, false, "tab checks", _ => RunTabChecks()),
        new("styles", Tier.Fast, false, "style checks", RunStyleChecks),
        new("fields", Tier.Fast, true, "field checks", RunFieldChecks),
        new("lists", Tier.Fast, false, "line list checks", RunLineChecks),
        new("set-view", Tier.Fast, false, "set view checks", RunSetViewChecks),
        new("key-grid", Tier.Fast, false, "key grid checks", RunKeyGridChecks),
        new("notetracks", Tier.Fast, false, "notetrack checks", RunNotetrackChecks),
        new("install", Tier.Fast, false, "install checks", RunInstallChecks),
        new("layout", Tier.Fast, false, "layout checks", RunLayoutChecks),
        new("themes", Tier.Fast, false, "theme checks", RunThemeChecks),
        new("updates", Tier.Fast, false, "update checks", RunUpdateChecks),
        new("appmenu", Tier.Fast, false, "app menu checks", RunAppMenuChecks),
        new("anim-layout", Tier.Fast, true, "anim layout checks", RunAnimLayoutChecks),
        new("search", Tier.Fast, false, "search menu checks", RunSearchMenuChecks),
        new("extensions", Tier.Fast, true, "extension checks", RunExtensionChecks),
        new("records", Tier.Fast, true, "record checks", RunRecordChecks),
        new("hardening", Tier.Fast, false, "hardening checks", _ => RunHardeningChecks()),
        new("simulator", Tier.Fast, true, "simulator checks", RunSimulatorChecks),
        // Recoil in four, in the order it always ran: the timing tier needs only the overlay's draw cost.
        new("recoil", Tier.Fast, false, "recoil checks", _ => RecoilPart(RecoilLogicChecks)),
        new("recoil-draw", Tier.Fast, true, "recoil checks", _ => RecoilPart(RecoilOverlayDrawChecks)),
        new("recoil-app", Tier.Fast, false, "recoil checks", o => RecoilPart(() => RecoilAppPartChecks(o))),
        new("recoil-render", Tier.Full, false, "recoil checks", _ => RecoilPart(RecoilRenderChecks)),
        new("editor-pass", Tier.Fast, true, "editor pass checks", RunEditorPassChecks),
        new("editor-review", Tier.Fast, false, "editor review checks", RunEditorReviewChecks),
        new("live-smoke", Tier.Full, false, null, _ => LiveSmoke(), AfterSession: true),
    ];

    /// <summary>
    /// Seconds each group took as a process of its own (<c>--group</c>), Release, quiet machine; <c>--timings</c> prints the
    /// in-process numbers in this shape. Only
    /// shard balance reads it: a group missing here costs <see cref="DefaultCost"/>, and a stale number costs time, never
    /// coverage.
    /// </summary>
    private static readonly Dictionary<string, double> Costs = new()
    {
        ["main"] = 10, ["shell"] = 10, ["ref"] = 6, ["journal"] = 29, ["command"] = 6, ["live-timing"] = 13, ["save-ui"] = 6,
        ["placement"] = 20, ["buttons"] = 25, ["save"] = 30, ["perf"] = 30, ["scroll"] = 4, ["tabs"] = 2, ["fields"] = 6,
        ["lists"] = 5, ["set-view"] = 6, ["key-grid"] = 4, ["notetracks"] = 4, ["install"] = 5, ["layout"] = 4, ["themes"] = 4, ["appmenu"] = 4, ["anim-layout"] = 31, ["search"] = 12,
        ["extensions"] = 29, ["records"] = 64, ["hardening"] = 18, ["simulator"] = 8, ["recoil"] = 1, ["recoil-draw"] = 1,
        ["recoil-app"] = 37, ["recoil-render"] = 2, ["editor-pass"] = 25, ["live-smoke"] = 2,
    };

    private const double DefaultCost = 20;

    // ═══ Selection ══════════════════════════════════════════════════════════

    /// <summary>The groups this run's arguments pick, in run order; null when none of the suite's options is given.</summary>
    private static List<Group>? SelectGroups(string[] args, out string? error)
    {
        error = null;
        IEnumerable<Group> picked = Groups;
        var any = false;
        if (Arg(args, "--group") is { } names)
        {
            any = true;
            var wanted = names.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var unknown = wanted.Where(n => Groups.All(g => g.Name != n)).ToList();
            if (unknown.Count > 0)
            {
                error = $"no such group: {string.Join(", ", unknown)} (--list-groups lists them)";
                return null;
            }
            picked = picked.Where(g => wanted.Contains(g.Name));
        }
        if (Arg(args, "--skip") is { } skipped)
        {
            any = true;
            var left = skipped.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var unknown = left.Where(n => Groups.All(g => g.Name != n)).ToList();
            if (unknown.Count > 0)
            {
                error = $"no such group: {string.Join(", ", unknown)} (--list-groups lists them)";
                return null;
            }
            picked = picked.Where(g => !left.Contains(g.Name));
        }
        if (args.Contains("--fast"))
        {
            any = true;
            picked = picked.Where(g => g.Tier == Tier.Fast);
        }
        if (args.Contains("--timing"))
        {
            any = true;
            picked = picked.Where(g => g.Gates);
        }
        if (args.Contains("--untimed"))
        {
            any = true;
            picked = picked.Where(g => !g.Gates);
        }
        if (Arg(args, "--shard") is { } shard)
        {
            any = true;
            var parts = shard.Split('/');
            if (parts.Length != 2 || !int.TryParse(parts[0], out var i) || !int.TryParse(parts[1], out var n) || n < 1 || i < 1 || i > n)
            {
                error = $"--shard takes i/N with 1 <= i <= N (got '{shard}')";
                return null;
            }
            var mine = Shards(picked.ToList(), n)[i - 1];
            picked = picked.Where(mine.Contains);
        }
        return any ? picked.ToList() : null;
    }

    /// <summary>
    /// Splits <paramref name="groups"/> into <paramref name="n"/> shards of about equal cost: the most costly first, each
    /// to the shard with least so far (ties to the lower shard). Stable for a given cost table, so every shard process
    /// computes the same split.
    /// </summary>
    private static List<HashSet<Group>> Shards(List<Group> groups, int n)
    {
        var shards = Enumerable.Range(0, n).Select(_ => new HashSet<Group>()).ToList();
        var load = new double[n];
        foreach (var g in groups.OrderByDescending(Cost).ThenBy(g => g.Name, StringComparer.Ordinal))
        {
            var least = Array.IndexOf(load, load.Min());
            shards[least].Add(g);
            load[least] += Cost(g);
        }
        return shards;
    }

    private static double Cost(Group g) => Costs.GetValueOrDefault(g.Name, DefaultCost);

    private static readonly string[] ValueOptions = ["--group", "--skip", "--shard", "--results"];

    private static string? Arg(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    /// <summary>The first argument that is neither an option nor an option's value (the shots folder).</summary>
    private static string? Positional(string[] args) =>
        args.Where((a, i) => !a.StartsWith("--", StringComparison.Ordinal) && (i == 0 || !ValueOptions.Contains(args[i - 1]))).FirstOrDefault();

    /// <summary>--list-groups: one line per selected group: name, tier, gates, cost, and its shard when --shard is given.</summary>
    private static int ListGroups(string[] args)
    {
        var groups = SelectGroups(args, out var error) ?? Groups.ToList();
        if (error is not null)
        {
            Console.WriteLine(error);
            return 2;
        }
        foreach (var g in groups)
            Console.WriteLine($"GROUP {g.Name} {g.Tier.ToString().ToLowerInvariant()} {(g.Gates ? "gates" : "-")} {Cost(g):0}");
        return 0;
    }

    // ═══ Recording: per-check timings and results ══════════════════════════

    /// <summary>Timing gates print as TIME lines and count neither way (the timing tier asserts them).</summary>
    private static bool _deferGates;

    private static Group? _group;
    private static long _lastMark;
    private static List<CheckRecord>? _records;
    private static readonly List<GroupRecord> _groupRecords = new();

    private sealed record CheckRecord(string Group, string Label, string Status, double Ms);

    private sealed record GroupRecord(string Name, double Ms, int Checks, int Failures, bool Completed);

    /// <summary>
    /// A timing gate: <paramref name="ok"/> is what the check asserts besides time, <paramref name="inBudget"/> the
    /// measured time against its budget. In the full run and every subset flag it is a check like any other; in a run that
    /// defers gates (the fast tier, the parallel shards) only <paramref name="ok"/> is checked and the time is printed,
    /// for the timing tier to assert alone on a quiet machine.
    /// </summary>
    private static void Gate(string label, bool ok, bool inBudget)
    {
        if (_group is { Gates: false } g)
            Check($"suite: '{label}' is a timing gate, so its group '{g.Name}' needs Gates: true in Suite.cs (or the timing tier never asserts it)", false);
        if (!_deferGates || !ok)
        {
            Check(label, ok && inBudget);
            return;
        }
        Console.WriteLine($"TIME  {label}{(inBudget ? "" : "  [over budget here: the timing tier gates it]")}");
        Record(label, "deferred");
    }

    private static void Gate(string label, bool inBudget) => Gate(label, true, inBudget);

    private static void Record(string label, string status)
    {
        if (_records is null)
            return;
        var now = Stopwatch.GetTimestamp();
        _records.Add(new CheckRecord(_group?.Name ?? "", label, status, Stopwatch.GetElapsedTime(_lastMark, now).TotalMilliseconds));
        _lastMark = now;
    }

    /// <summary>Runs one group, reporting an escaped exception as the full run always has, and times it when recording.</summary>
    private static void RunGroup(Group g, string outDir)
    {
        _group = g;
        var start = Stopwatch.GetTimestamp();
        _lastMark = start;
        var failuresBefore = _failures;
        var checksBefore = _records?.Count ?? 0;
        var completed = false;
        try
        {
            if (g.Failure is null)
                g.Run(outDir);
            else
            {
                try { g.Run(outDir); }
                catch (Exception ex) { Check($"{g.Failure}: {ex}", false); }
            }
            completed = true;
        }
        finally
        {
            if (_records is not null)
                _groupRecords.Add(new GroupRecord(g.Name, Stopwatch.GetElapsedTime(start).TotalMilliseconds,
                    _records.Count - checksBefore, _failures - failuresBefore, completed));
            _group = null;
        }
    }

    private static double _mainEnteredMs;
    private static double _sessionReadyMs;
    /// <summary>Set first thing in Main.</summary>
    private static long _mainEntered;

    /// <summary>Starts recording when --timings or --results asks for it.</summary>
    private static void StartRecording(string[] args)
    {
        if (!args.Contains("--timings") && Arg(args, "--results") is null)
            return;
        _records = new List<CheckRecord>();
        _mainEnteredMs = (DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds;
    }

    private static void SessionReady()
    {
        if (_records is not null)
            _sessionReadyMs = Stopwatch.GetElapsedTime(_mainEntered).TotalMilliseconds;
    }

    /// <summary>Prints the slowest checks and each group's total (--timings) and writes the results file.</summary>
    private static void ReportRecording(string[] args, List<Group> ran)
    {
        if (_records is null)
            return;
        var total = Stopwatch.GetElapsedTime(_mainEntered).TotalMilliseconds;
        var results = new
        {
            args,
            pid = Environment.ProcessId,
            build = IsOptimizedBuild ? "Release" : "Debug",
            deferGates = _deferGates,
            processStartToMainMs = _mainEnteredMs,
            sessionStartMs = _sessionReadyMs,
            totalMs = total,
            failures = _failures,
            plannedGroups = ran.Select(g => g.Name).ToList(),
            groups = _groupRecords,
            checks = _records,
        };
        var path = Arg(args, "--results")
                   ?? Path.Combine(Path.GetTempPath(), $"apex-shots-timings-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(results, JsonOptions));

        if (!args.Contains("--timings"))
            return;
        Console.WriteLine();
        Console.WriteLine($"TIMINGS  {total / 1000:0.0} s in Main, {_mainEnteredMs / 1000:0.0} s from process start to Main, " +
                          $"{_sessionReadyMs / 1000:0.0} s to the headless session; {_records.Count} checks ({(IsOptimizedBuild ? "Release" : "Debug")})");
        Console.WriteLine("TIMINGS  slowest checks (time since the check before it in its group):");
        foreach (var c in _records.OrderByDescending(c => c.Ms).Take(40))
            Console.WriteLine($"TIMINGS  {c.Ms / 1000,7:0.00} s  {c.Group,-12} {(c.Label.Length > 110 ? c.Label[..110] + "…" : c.Label)}");
        Console.WriteLine("TIMINGS  groups:");
        foreach (var g in _groupRecords.OrderByDescending(g => g.Ms))
            Console.WriteLine($"TIMINGS  {g.Ms / 1000,7:0.0} s  {g.Name,-12} {g.Checks,4} checks{(g.Completed ? "" : "  (did not complete)")}");
        Console.WriteLine("TIMINGS  cost table for Suite.cs: " +
                          string.Join(", ", _groupRecords.Select(g => $"[\"{g.Name}\"] = {Math.Max(1, Math.Round(g.Ms / 1000))}")));
        Console.WriteLine($"TIMINGS  written to {Path.GetFullPath(path)}");
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };
}
