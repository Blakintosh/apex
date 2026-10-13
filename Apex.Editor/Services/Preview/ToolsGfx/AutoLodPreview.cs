using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Apex.Editor.Services.Components;
using Apex.Render.Data;
using Apex.Render.Data.Assets;
using Apex.Render.Data.Hashing;

namespace Apex.Editor.Services.Preview.ToolsGfx;

/// <summary>One LOD in Dragon's <c>-xmodel-lods</c> answer.</summary>
public sealed record DragonLod(int Index, long Faces, bool Autogen, string Cache);

/// <summary>Dragon's <c>-xmodel-lods … -json</c> answer: the LODs it made, or why it could not.</summary>
public sealed record DragonLodResult(string Algo, IReadOnlyList<DragonLod> Lods, long ElapsedMs, string? Error)
{
    public bool Ok => Error is null;

    public static DragonLodResult Failed(string error) => new("", Array.Empty<DragonLod>(), 0, error);

    /// <summary>
    /// Parses Dragon's stdout: one JSON object, <c>{"algo","lods":[{"index","faces","autogen","cache"}],"elapsed_ms"}</c>, or
    /// <c>{"error"}</c> (with a non-zero exit). A line of other output before the object is tolerated.
    /// </summary>
    public static DragonLodResult Parse(string stdout, int exitCode, string stderr)
    {
        JsonDocument? doc = null;
        var text = stdout.Trim();
        try
        {
            doc = JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            var last = text.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith('{'));
            try
            {
                doc = last is null ? null : JsonDocument.Parse(last);
            }
            catch (JsonException)
            {
            }
        }
        using (doc)
        {
            var root = doc?.RootElement;
            if (root is { ValueKind: JsonValueKind.Object } r && r.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.String)
                return Failed(err.GetString() ?? "Dragon failed.");
            if (exitCode != 0)
                return Failed(FirstLine(stderr) is { Length: > 0 } line ? line : $"Dragon exited with code {exitCode}.");
            if (root is not { ValueKind: JsonValueKind.Object } o || !o.TryGetProperty("lods", out var lods) || lods.ValueKind != JsonValueKind.Array)
                return Failed("Dragon's answer could not be read.");
            var list = new List<DragonLod>();
            foreach (var l in lods.EnumerateArray())
            {
                if (l.ValueKind != JsonValueKind.Object)
                    continue;
                list.Add(new DragonLod(
                    l.TryGetProperty("index", out var i) && i.TryGetInt32(out var index) ? index : -1,
                    l.TryGetProperty("faces", out var f) && f.TryGetInt64(out var faces) ? faces : 0,
                    l.TryGetProperty("autogen", out var a) && a.ValueKind == JsonValueKind.True,
                    l.TryGetProperty("cache", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() ?? "" : ""));
            }
            return new DragonLodResult(
                o.TryGetProperty("algo", out var algo) && algo.ValueKind == JsonValueKind.String ? algo.GetString() ?? "" : "",
                list,
                o.TryGetProperty("elapsed_ms", out var ms) && ms.TryGetInt64(out var elapsed) ? elapsed : 0,
                null);
        }
    }

    private static string FirstLine(string text) => text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
}

/// <summary>
/// Auto LOD previews through Dragon. Apex never decimates: when the install's Dragon offers <c>auto-lod</c>
/// (<see cref="DragonComponent.AutoLod"/>) and an xmodel's auto LODs have no cache made by Dragon's algorithm, Dragon is
/// asked to make them (<c>dragon -xmodel-lods &lt;name&gt; -root &lt;BO3&gt; -json</c>) and the preview loads its caches
/// like any other (<see cref="ModelCacheLocator.FindAutogenXMesh"/>). Without Dragon nothing here runs and the preview
/// reads APE's own autogen caches, as before.
/// </summary>
public static class AutoLodPreview
{
    /// <summary>The overlay's words while Dragon works.</summary>
    public const string GeneratingText = "Generating LODs…";

    /// <summary>Longest Dragon may take for one model (big models take seconds; this is a hang guard).</summary>
    public static TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Replaces the registry lookup (checks); null: <see cref="DragonComponent.AutoLod"/>.</summary>
    public static Func<string?, DragonAutoLod?>? Locate { get; set; }

    /// <summary>Dragon's auto LOD for the install, or null. Null too for a Dragon whose caches turned out unreadable to the
    /// preview this session (<see cref="UnreadableDragon"/>): the preview stays on APE's caches without asking again.</summary>
    public static DragonAutoLod? Find(string? installPath) =>
        (Locate ?? DragonComponent.AutoLod)(installPath) is { } dragon && !string.Equals(dragon.Exe, UnreadableDragon, StringComparison.OrdinalIgnoreCase) ? dragon : null;

    /// <summary>The Dragon whose caches were in a format the preview can't read (the linker's <c>xmesh\v6</c>); null while none was.</summary>
    public static string? UnreadableDragon { get; set; }

    /// <summary>What one model needs from Dragon: its auto LOD slots and their inputs, keyed for the session memo.</summary>
    public sealed record Request(string XModel, string Key, IReadOnlyList<(XModelLodSlot Slot, string SourceBin)> Missing, XModelLodParameters Parameters);

    /// <summary>
    /// The auto LODs of <paramref name="xmodelName"/>, with those that have no cache made by <paramref name="algo"/> in
    /// <see cref="Request.Missing"/>; null when the model has no auto LODs (or can't be read, or a source file is missing:
    /// the preview reports that itself). Call on a worker thread (hashes source files).
    /// </summary>
    public static Request? Missing(ToolsGfxData data, string xmodelName, AutoLodAlgo algo)
    {
        if (data.Gdt.Find(xmodelName, "xmodel") is not { } entry)
            return null;
        var parameters = XModelLodParameters.FromGdt(entry.Fields);
        var missing = new List<(XModelLodSlot, string)>();
        var key = new StringBuilder(xmodelName).Append('|').Append(algo.Name);
        bool any = false;
        int sourceFaces = 0;
        foreach (var slot in XModelLodSlots.Read(entry.Fields))
        {
            if (!slot.Autogen)
            {
                sourceFaces = algo.GateFaces > 0 ? SourceFaces(data, slot.File, parameters) : 0;
                continue;
            }
            if (sourceFaces < algo.GateFaces)
                continue; // below the gate Dragon decimates as the stock tools do: APE's caches are its LODs
            any = true;
            var bin = data.Models.ResolveModelExportPath(slot.File);
            if (ModelCacheLocator.AutoLodFileName(bin, parameters, slot.Percent, algo) is not { } name)
                return null; // the source file is missing: Dragon can't help, the preview says what's wrong
            key.Append('|').Append(name.File);
            if (data.Models.FindAutogenXMesh(bin, parameters, slot.Percent, algo) is null)
                missing.Add((slot, bin));
        }
        return any ? new Request(xmodelName, key.ToString(), missing, parameters) : null;
    }

    /// <summary>Faces of an authored LOD (its cache, as the preview loads it); 0 when it has none.</summary>
    private static int SourceFaces(ToolsGfxData data, string file, XModelLodParameters parameters)
    {
        try
        {
            var loc = data.Models.FindXMesh(data.Models.ResolveModelExportPath(file), parameters);
            if (loc is null)
                return 0;
            var mesh = loc.Payload is { } payload ? XMeshData.Parse(payload, loc.Path) : XMeshData.Load(loc.Path);
            return GpuMesh.FromXMesh(mesh).Surfaces.Sum(s => s.TriangleCount);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or IndexOutOfRangeException or ArgumentException or NotSupportedException)
        {
            return 0;
        }
    }

    /// <summary>True when <paramref name="path"/> is an xmesh the preview can read (ToolsGfx <c>xmeshes\v39</c>).</summary>
    public static bool IsPreviewXMesh(string path)
    {
        try
        {
            _ = XMeshData.Load(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or IndexOutOfRangeException or ArgumentException or NotSupportedException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // One run per model and inputs at a time; a failure is remembered for the session, so editing other fields or
    // reopening the asset doesn't run Dragon again until the inputs change.
    private static readonly ConcurrentDictionary<string, Lazy<Task<DragonLodResult>>> Runs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Forgets remembered runs (checks).</summary>
    public static void Reset()
    {
        Runs.Clear();
        UnreadableDragon = null;
    }

    /// <summary>
    /// Asks Dragon for the model's LODs (once per <see cref="Request.Key"/>; concurrent callers share the run), then
    /// records where it wrote the auto LODs. Cancelling stops waiting; the run itself is killed only when nobody else
    /// waits for it and it was this caller's.
    /// </summary>
    public static async Task<DragonLodResult> GenerateAsync(DragonAutoLod dragon, string installPath, Request request, CancellationToken ct)
    {
        using var own = new CancellationTokenSource();
        var lazy = Runs.GetOrAdd(request.Key, _ => new Lazy<Task<DragonLodResult>>(() => RunAsync(dragon, installPath, request, own.Token)));
        var run = lazy.Value;
        try
        {
            var result = await run.WaitAsync(ct).ConfigureAwait(false);
            if (result.Ok)
                Runs.TryRemove(new KeyValuePair<string, Lazy<Task<DragonLodResult>>>(request.Key, lazy)); // the caches answer from now on
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // a cancelled run is not a result to remember
            own.Cancel();
            Runs.TryRemove(new KeyValuePair<string, Lazy<Task<DragonLodResult>>>(request.Key, lazy));
            throw;
        }
        catch (OperationCanceledException)
        {
            // another preview started this run and was closed: this one shows APE's LODs and asks again next time
            Runs.TryRemove(new KeyValuePair<string, Lazy<Task<DragonLodResult>>>(request.Key, lazy));
            return DragonLodResult.Failed("Generating the LODs was cancelled.");
        }
    }

    private static async Task<DragonLodResult> RunAsync(DragonAutoLod dragon, string installPath, Request request, CancellationToken ct)
    {
        var result = await RunDragonAsync(dragon.Exe, request.XModel, installPath, ct).ConfigureAwait(false);
        if (!result.Ok)
            return result;
        if (result.Algo.Length > 0 && !string.Equals(result.Algo, dragon.Algo.Name, StringComparison.Ordinal))
            return DragonLodResult.Failed($"Dragon made {result.Algo} LODs, but its registry entry says {dragon.Algo.Name}.");
        foreach (var (slot, bin) in request.Missing)
        {
            var lod = result.Lods.FirstOrDefault(l => l.Index == slot.Index && l.Autogen);
            if (lod is not { Cache.Length: > 0 } || !File.Exists(lod.Cache))
                return result with { Error = $"Dragon did not write LOD {slot.Index}." };
            // Dragon writes the linker's cache (xmesh\v6); the preview reads ToolsGfx xmeshes (v39)
            if (!IsPreviewXMesh(lod.Cache))
            {
                UnreadableDragon = dragon.Exe; // don't run it again for other models this session: each run is seconds of work
                return result with { Error = $"Dragon's LOD {slot.Index} is in the linker's cache format, which the preview can't read yet." };
            }
            ModelCacheLocator.RememberAutoLod(bin, request.Parameters, slot.Percent, dragon.Algo, lod.Cache);
        }
        return result;
    }

    /// <summary>Runs <c>exe -xmodel-lods name -root install -json</c> and reads its answer; killed (with its children) on cancel or timeout.</summary>
    public static async Task<DragonLodResult> RunDragonAsync(string exe, string xmodelName, string installPath, CancellationToken ct)
    {
        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(exe) ?? "",
        };
        foreach (var arg in new[] { "-xmodel-lods", xmodelName, "-root", installPath, "-json" })
            start.ArgumentList.Add(arg);
        Process? process;
        try
        {
            process = Process.Start(start);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return DragonLodResult.Failed("Dragon could not start: " + ex.Message);
        }
        if (process is null)
            return DragonLodResult.Failed("Dragon could not start.");
        using (process)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(Timeout);
            var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
            try
            {
                await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                }
                ct.ThrowIfCancellationRequested();
                return DragonLodResult.Failed($"Dragon took longer than {Timeout.TotalSeconds:0} s.");
            }
            return DragonLodResult.Parse(await stdout.ConfigureAwait(false), process.ExitCode, await stderr.ConfigureAwait(false));
        }
    }
}
