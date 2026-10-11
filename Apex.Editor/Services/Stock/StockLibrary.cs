using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Apex.Editor.Services.Stock;

/// <summary>Where the stock library stands.</summary>
public enum StockState
{
    /// <summary>No install to index (mock data, or the install was not found).</summary>
    Unavailable,

    /// <summary>The helper (stocklib.exe) is not installed or not found: the library offers to install it.</summary>
    HelperMissing,

    /// <summary>The helper is there and the first index is being built (or has not started).</summary>
    Indexing,

    /// <summary>stock.db can be searched. A refresh may still be running; the old index answers meanwhile.</summary>
    Ready,

    /// <summary>The helper could not run or the index failed (see <see cref="StockLibrary.Problem"/>).</summary>
    Failed,
}

/// <summary>What a clone should do, as the dialog collects it.</summary>
public sealed class StockExtractRequest
{
    public required string Type { get; init; }
    public required string Name { get; init; }
    public required string Map { get; init; }
    public string? Gdt { get; init; }
    public string? Zone { get; init; }
    public string? Prefix { get; init; }
    public bool KeepName { get; init; }
    public bool RenameAll { get; init; }
    public bool ClearCamo { get; init; }
    public bool DryRun { get; init; }
}

/// <summary>Progress of a clone: the asset being written and how far along.</summary>
public sealed record StockProgress(string What, int Done, int Total);

/// <summary>The stock helper's work that needs the game's fastfiles. Behind an interface so the checks can answer without the helper.</summary>
public interface IStockBackend
{
    Task<StockDeps> DepsAsync(string type, string name, string map, string? zone, string? prefix, bool renameAll, CancellationToken cancel = default);
    Task<StockExtractResult> ExtractAsync(StockExtractRequest request, Action<StockProgress>? progress, CancellationToken cancel = default);
    Task<StockUndoResult> UndoAsync(string manifest, CancellationToken cancel = default);
    Task<StockDescription> DescribeAsync(string type, string name, string? zone, CancellationToken cancel = default);
}

/// <summary>
/// The stock library of one install: finds the helper, opens <c>stock.db</c> read-only, runs <c>stocklib serve</c> for
/// everything the database cannot answer (dependencies, field reads, clones, undo) and refreshes the index in the
/// background when Apex starts. The helper's code is never loaded here; the contract is its protocol (docs/protocol.md).
/// </summary>
public sealed class StockLibrary : IStockBackend, IDisposable
{
    /// <summary>The environment variable that points at stocklib.exe (or the folder holding it).</summary>
    public const string PathVariable = "STOCKLIB_PATH";

    private readonly string? _install;
    private readonly object _gate = new();
    private StockHelper? _helper;
    private int _refreshing;

    public StockLibrary(string? bo3Root, string? configuredExe = null, string? dbPath = null)
    {
        _install = bo3Root;
        DbPath = dbPath ?? (bo3Root is null ? null : StockDb.DefaultPath(bo3Root));
        ExePath = Locate(bo3Root, configuredExe);
        Db = DbPath is not null && File.Exists(DbPath) ? new StockDb(DbPath) : null;
        State = bo3Root is null && Db is null ? StockState.Unavailable
            : Db is not null ? StockState.Ready
            : ExePath is null ? StockState.HelperMissing
            : StockState.Indexing;
    }

    /// <summary>A library with no install behind it (mock data): nothing to search, nothing to offer.</summary>
    public static StockLibrary None { get; } = new(null);

    /// <summary>A library that answers from a database and a backend given to it (checks).</summary>
    public static StockLibrary ForDatabase(string dbPath, IStockBackend? backend = null, string? bo3Root = null) =>
        new(bo3Root, null, dbPath) { Backend = backend };

    public string? DbPath { get; }

    public string? ExePath { get; private set; }

    public StockDb? Db { get; private set; }

    public StockState State { get; private set; }

    /// <summary>Why the helper failed (shown in the library's note); empty otherwise.</summary>
    public string Problem { get; private set; } = "";

    /// <summary>"Indexing stock assets… 42%" while a refresh runs; empty otherwise.</summary>
    public string Progress { get; private set; } = "";

    /// <summary>0..1 while a refresh runs and knows its size; otherwise 0.</summary>
    public double ProgressFraction { get; private set; }

    public bool IsRefreshing => Volatile.Read(ref _refreshing) != 0;

    /// <summary>Anything about the library changed (state, progress, the index): raised on a background thread.</summary>
    public event Action? Changed;

    /// <summary>Answers deps / extract / undo / describe instead of the helper (checks); null: the helper.</summary>
    public IStockBackend? Backend { get; set; }

    /// <summary>The BO3 install this library belongs to.</summary>
    public string? Install => _install;

    public bool CanSearch => Db is { Exists: true };

    // ── Finding the helper ──────────────────────────────────────────────────

    /// <summary>
    /// stocklib.exe: the path in the settings, then <c>STOCKLIB_PATH</c> (a file, or a folder holding it), then the default
    /// install location <c>&lt;BO3&gt;\bin\stocklib\stocklib.exe</c>. Null when none of them exists.
    /// </summary>
    public static string? Locate(string? bo3Root, string? configured)
    {
        foreach (var candidate in Candidates(bo3Root, configured))
            if (File.Exists(candidate))
                return candidate;
        return null;
    }

    private static IEnumerable<string> Candidates(string? bo3Root, string? configured)
    {
        foreach (var raw in new[] { configured, Environment.GetEnvironmentVariable(PathVariable) })
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;
            var path = raw.Trim().Trim('"');
            yield return Directory.Exists(path) ? Path.Combine(path, "stocklib.exe") : path;
        }
        if (bo3Root is not null)
            yield return Path.Combine(bo3Root, "bin", "stocklib", "stocklib.exe");
    }

    /// <summary>Points the library at another helper (the settings field): re-reads where things stand and refreshes.</summary>
    public void UseHelper(string? exe)
    {
        var located = Locate(_install, exe);
        lock (_gate)
        {
            _helper?.Dispose();
            _helper = null;
        }
        ExePath = located;
        Problem = "";
        State = Db is not null ? StockState.Ready : located is null ? StockState.HelperMissing : StockState.Indexing;
        Changed?.Invoke();
        if (located is not null)
            StartRefresh();
    }

    // ── The helper process ──────────────────────────────────────────────────

    private StockHelper? EnsureHelper()
    {
        lock (_gate)
        {
            if (_helper is { IsRunning: true })
                return _helper;
            _helper?.Dispose();
            _helper = null;
            if (ExePath is null)
                return null;
            _helper = StockHelper.TryStart(ExePath, _install, DbPath, out var error);
            if (_helper is null)
            {
                Problem = error ?? "the helper did not start";
                State = Db is null ? StockState.Failed : StockState.Ready;
            }
            return _helper;
        }
    }

    private async Task<JsonElement> CallAsync(string method, Dictionary<string, object?>? args, Action<JsonElement>? onEvent, CancellationToken cancel)
    {
        var helper = EnsureHelper() ?? throw new StockHelperException(ExePath is null
            ? "The stock helper is not installed."
            : "The stock helper could not start" + (Problem.Length > 0 ? ": " + Problem : "."));
        await helper.Ready.WaitAsync(TimeSpan.FromSeconds(20), cancel).ConfigureAwait(false);
        return await helper.CallAsync(method, args, onEvent, cancel).ConfigureAwait(false);
    }

    // ── Index refresh ───────────────────────────────────────────────────────

    /// <summary>
    /// Builds or refreshes the index in the background (cheap when nothing changed: the helper compares the fastfiles'
    /// fingerprints and writes nothing). Searches keep reading the old index until the refresh commits.
    /// </summary>
    public void StartRefresh()
    {
        if (ExePath is null || Backend is not null || Interlocked.CompareExchange(ref _refreshing, 1, 0) != 0)
            return;
        _ = Task.Run(RefreshAsync);
    }

    private async Task RefreshAsync()
    {
        try
        {
            if (Db is null)
            {
                State = StockState.Indexing;
                Progress = "Indexing stock assets…";
            }
            Changed?.Invoke();
            var lastReport = Environment.TickCount64;
            await CallAsync("index", null, e =>
            {
                var kind = e.TryGetProperty("event", out var k) ? k.GetString() : null;
                if (kind != "zone" || Environment.TickCount64 - lastReport < 250)
                    return;
                lastReport = Environment.TickCount64;
                var done = StockDepNode.Long(e, "bytes_done");
                var total = StockDepNode.Long(e, "bytes_total");
                ProgressFraction = total > 0 ? Math.Min(1, (double)done / total) : 0;
                Progress = Db is null ? $"Indexing stock assets… {ProgressFraction:P0}" : $"Refreshing stock assets… {ProgressFraction:P0}";
                Changed?.Invoke();
            }, CancellationToken.None).ConfigureAwait(false);
            Problem = "";
        }
        catch (Exception ex) when (ex is StockHelperException or TimeoutException or OperationCanceledException)
        {
            Problem = ex.Message;
        }
        finally
        {
            Progress = "";
            ProgressFraction = 0;
            // The refresh may have made the database (or replaced it): a new reader forgets what the old one cached.
            Db = DbPath is not null && File.Exists(DbPath) ? new StockDb(DbPath) : null;
            ShippedAssets.UseDatabase(Db);
            State = Db is not null ? StockState.Ready : Problem.Length > 0 || ExePath is null ? StockState.Failed : StockState.Indexing;
            Volatile.Write(ref _refreshing, 0);
            Changed?.Invoke();
        }
    }

    // ── IStockBackend ───────────────────────────────────────────────────────

    public async Task<StockDeps> DepsAsync(string type, string name, string map, string? zone, string? prefix, bool renameAll, CancellationToken cancel = default)
    {
        if (Backend is { } fake)
            return await fake.DepsAsync(type, name, map, zone, prefix, renameAll, cancel).ConfigureAwait(false);
        var args = new Dictionary<string, object?> { ["type"] = type, ["name"] = name, ["map"] = map };
        if (zone is { Length: > 0 })
            args["zone"] = zone;
        if (prefix is { Length: > 0 })
            args["prefix"] = prefix;
        if (renameAll)
            args["rename_all"] = true;
        return StockDeps.Parse(await CallAsync("deps", args, null, cancel).ConfigureAwait(false));
    }

    public async Task<StockExtractResult> ExtractAsync(StockExtractRequest request, Action<StockProgress>? progress, CancellationToken cancel = default)
    {
        if (Backend is { } fake)
            return await fake.ExtractAsync(request, progress, cancel).ConfigureAwait(false);
        var args = new Dictionary<string, object?> { ["type"] = request.Type, ["name"] = request.Name, ["map"] = request.Map };
        if (request.Gdt is { Length: > 0 })
            args["gdt"] = request.Gdt;
        if (request.Zone is { Length: > 0 })
            args["zone"] = request.Zone;
        if (request.Prefix is { Length: > 0 })
            args["prefix"] = request.Prefix;
        if (request.KeepName)
            args["keep_name"] = true;
        if (request.RenameAll)
            args["rename_all"] = true;
        if (request.ClearCamo)
            args["clear_camo"] = true;
        if (request.DryRun)
            args["dry_run"] = true;
        var result = await CallAsync("extract", args, e =>
        {
            if (progress is null || !e.TryGetProperty("event", out var kind))
                return;
            switch (kind.GetString())
            {
                case "asset":
                    progress(new StockProgress(StockDepNode.Str(e, "name"), StockDepNode.Int(e, "done"), StockDepNode.Int(e, "total")));
                    break;
                case "convert":
                    progress(new StockProgress("Converting " + StockDepNode.Str(e, "what"), 0, 0));
                    break;
            }
        }, cancel).ConfigureAwait(false);
        return StockExtractResult.Parse(result);
    }

    public async Task<StockUndoResult> UndoAsync(string manifest, CancellationToken cancel = default)
    {
        if (Backend is { } fake)
            return await fake.UndoAsync(manifest, cancel).ConfigureAwait(false);
        var args = new Dictionary<string, object?> { ["manifest"] = manifest };
        return StockUndoResult.Parse(await CallAsync("undo", args, null, cancel).ConfigureAwait(false));
    }

    public async Task<StockDescription> DescribeAsync(string type, string name, string? zone, CancellationToken cancel = default)
    {
        if (Backend is { } fake)
            return await fake.DescribeAsync(type, name, zone, cancel).ConfigureAwait(false);
        var args = new Dictionary<string, object?> { ["type"] = type, ["name"] = name };
        if (zone is { Length: > 0 })
            args["zone"] = zone;
        return StockDescription.Parse(await CallAsync("describe", args, null, cancel).ConfigureAwait(false));
    }

    /// <summary>True when the dependencies, a clone or the fields can be asked for: a backend is set, or the helper is installed.</summary>
    public bool CanAsk => Backend is not null || ExePath is not null;

    public void Dispose()
    {
        lock (_gate)
        {
            _helper?.Dispose();
            _helper = null;
        }
    }
}
