using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Apex.Editor.Models;
using Apex.Editor.Services.Gdt;
using Apex.Render.Assets;
using Apex.Render.Data;
using Apex.Render.Data.Conversion;
using Apex.Render.Data.Gdt;
using Apex.Render.Data.Lighting;
using Apex.Render.Presentation;

namespace Apex.Editor.Services.Preview.ToolsGfx;

/// <summary>
/// App-wide entry to the ToolsGfx preview renderer: decides whether it can be used (install caches present, D3D
/// interop working, not disabled) and owns the CPU-side data shared by every preview — <see cref="ToolsGfxData"/> over
/// Apex's asset index (<see cref="ApexGdtLookup"/>) and the <see cref="PreviewEnvironment"/> (LED lighting states,
/// skyboxes). Device-side resources are shared per device by <see cref="PreviewDeviceResources"/>.
/// Set <c>APEX_PREVIEW_RENDERER=gl</c> to force the OpenGL approximation.
/// </summary>
public static class ToolsGfxPreviewService
{
    private static readonly object Gate = new();
    private static ApexGdtLookup? _lookup;
    private static ToolsGfxData? _data;
    private static Task<PreviewEnvironment?>? _environment;
    private static string? _unavailableReason;

    /// <summary>The lighting state previews draw with (APE's <c>Preview/LightState</c>; Morning by default). Set through
    /// <c>PreviewLighting.Shared</c>, which the UI binds and the settings persist.</summary>
    public static PreviewLightState LightState { get; set; } = InitialLightState();

    /// <summary>True when <c>APEX_PREVIEW_LIGHTSTATE</c> fixes the lighting state (a saved preference must not override it).</summary>
    public static bool LightStateFromEnvironment => Environment.GetEnvironmentVariable("APEX_PREVIEW_LIGHTSTATE") is { Length: > 0 };

    /// <summary>Starts loading <paramref name="state"/>'s skybox once the shared environment is loaded (no-op before).</summary>
    public static void WarmSky(PreviewLightState state)
    {
        Task<PreviewEnvironment?>? task;
        lock (Gate)
            task = _environment;
        if (task is { IsCompletedSuccessfully: true, Result: { } env } && !env.IsSkyLoaded(state))
            _ = env.SkyAsync(state);
    }

    // Debug hooks for scripted screenshot/perf verification (inert otherwise):
    //   APEX_PREVIEW_LIGHTSTATE=Morning|Day|Sunset|Night  initial lighting state
    //   APEX_PREVIEW_STATS_LOG=<file>                     append each ToolsGfx status line
    //   APEX_PREVIEW_SPIN=<frames>                        orbit continuously for N frames, then log frame-time stats
    private static PreviewLightState InitialLightState() =>
        Enum.TryParse<PreviewLightState>(Environment.GetEnvironmentVariable("APEX_PREVIEW_LIGHTSTATE"), true, out var s) ? s : PreviewLightState.Morning;

    /// <summary>Frames to spin for <c>APEX_PREVIEW_SPIN</c> (0 = off).</summary>
    public static int SpinFrames { get; } = int.TryParse(Environment.GetEnvironmentVariable("APEX_PREVIEW_SPIN"), out var n) ? n : 0;

    private static readonly string? StatsLogPath = Environment.GetEnvironmentVariable("APEX_PREVIEW_STATS_LOG") is { Length: > 0 } p ? p : null;

    /// <summary>True when <c>APEX_PREVIEW_STATS_LOG</c> is set; check it before building a line for <see cref="Log"/>.</summary>
    public static bool IsLogging => StatsLogPath is not null;

    /// <summary>Appends a line to <c>APEX_PREVIEW_STATS_LOG</c> when set.</summary>
    public static void Log(string line)
    {
        if (StatsLogPath is not { } path)
            return;
        try
        {
            File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff} {line}{Environment.NewLine}");
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Why D3D11 composition interop cannot be used in this process (first viewport to find out records it),
    /// or null while it is unknown or working.</summary>
    public static string? InteropUnavailableReason { get; set; }

    /// <summary>Why the ToolsGfx renderer is unavailable, or null when it can be tried.</summary>
    public static string? UnavailableReason
    {
        get
        {
            if (string.Equals(Environment.GetEnvironmentVariable("APEX_PREVIEW_RENDERER"), "gl", StringComparison.OrdinalIgnoreCase))
                return "OpenGL forced (APEX_PREVIEW_RENDERER=gl)";
            if (!OperatingSystem.IsWindows())
                return "ToolsGfx preview needs Direct3D 11 (Windows)";
            if (InteropUnavailableReason is { } interop)
                return "D3D11 interop unavailable: " + interop;
            lock (Gate)
                return _unavailableReason;
        }
    }

    /// <summary>The lookup over Apex's asset index every preview resolves GDT assets through (created with the first
    /// resolver, like <see cref="GetData"/>).</summary>
    public static ApexGdtLookup Lookup(Func<string, string, AssetRecord?> resolve)
    {
        lock (Gate)
            return _lookup ??= new ApexGdtLookup(resolve);
    }

    /// <summary>Readies <see cref="Lookup"/> for a preview load that starts now (UI thread): lookups of records edited or
    /// reloaded since they were cached are dropped, so the load sees the records as they are.</summary>
    public static ApexGdtLookup BeginLoad(Func<string, string, AssetRecord?> resolve)
    {
        var lookup = Lookup(resolve);
        lookup.Revalidate();
        return lookup;
    }

    /// <summary>Points the service at the install and Apex's asset index (once, when the first preview needs it).</summary>
    public static ToolsGfxData? GetData(GameEnvironment env, Func<string, string, AssetRecord?> resolve)
    {
        lock (Gate)
        {
            if (_data != null || _unavailableReason != null)
                return _data;
            if (!env.IsAvailable || env.Bo3Root is null)
            {
                _unavailableReason = "BO3 install not available";
                return null;
            }
            var install = ToolsGfxInstall.FromRoot(env.Bo3Root);
            if (!install.IsAvailable)
            {
                _unavailableReason = "ToolsGfx shader cache / techsetdefs not found under share\\";
                return null;
            }
            return _data = new ToolsGfxData(install, Lookup(resolve));
        }
    }

    private static readonly ConditionalWeakTable<PreparedPreviewModel, string> ConversionNotes = new();

    /// <summary>
    /// <see cref="PreviewModelLoader.Prepare"/> for the model preview (call on a worker thread). Caches APE never wrote
    /// are converted by Apex on the way (conversion.md §4); <see cref="ConversionNote"/> then says so for the status line.
    /// Throws like <see cref="PreviewModelLoader.Prepare"/> when the model can neither be found nor converted.
    /// </summary>
    public static PreparedPreviewModel PrepareModel(ToolsGfxData data, string xmodelName, PreviewModelOptions? options = null,
        CancellationToken ct = default)
    {
        options ??= new PreviewModelOptions();
        return Reuse(data, "xmodel|" + xmodelName, options, () =>
        {
            using var scope = ConversionScope.Begin();
            var prepared = PreviewModelLoader.Prepare(data, xmodelName, options, cancellationToken: ct);
            if (IsLogging)
                foreach (var e in scope.Events)
                    Log($"{xmodelName} | {e.Origin} {e.Asset}{(e.Error is null ? $" ({e.Milliseconds:F0} ms)" : " failed: " + e.Error)}");
            if (scope.Summary() is { } note)
                ConversionNotes.AddOrUpdate(prepared, note);
            return prepared;
        }, ct);
    }

    // ── Prepared models, reused while nothing they were built from changed ──

    private const int PreparedCapacity = 6;
    private static readonly object PreparedGate = new();
    private static readonly LinkedList<PreparedEntry> Prepared = new(); // most recently used first

    private sealed record PreparedEntry(string Key, PreviewModelOptions Options, PreparedPreviewModel Model, LookupDependencies Dependencies,
        IReadOnlyList<(string Path, long Ticks)> Files);

    // xmodel fields naming the export files its LODs are read from.
    private static readonly string[] LodFields = ["filename", "mediumLod", "lowLod", "lowestLod", "lod4File", "lod5File", "lod6File", "lod7File"];

    /// <summary>
    /// <paramref name="prepare"/>'s model, or the very instance it returned before for <paramref name="key"/> and
    /// <paramref name="options"/> while nothing it was built from changed: every GDT record it looked up (as of the load's
    /// <see cref="BeginLoad"/>), the xmodel exports and image sources those name, and <paramref name="files"/>. Handing
    /// back the same instance lets the device keep its GPU copy (<see cref="PreviewDeviceResources"/>), so reopening an
    /// asset costs neither the prepare nor the upload. Call on a worker thread; <paramref name="ct"/> (the load's) stops
    /// before the prepare starts.
    /// </summary>
    public static PreparedPreviewModel Reuse(ToolsGfxData data, string key, PreviewModelOptions options, Func<PreparedPreviewModel> prepare,
        CancellationToken ct, IReadOnlyList<string>? files = null)
    {
        ct.ThrowIfCancellationRequested();
        if (data.Gdt is not LayeredGdtLookup { Primary: ApexGdtLookup lookup })
            return prepare();
        PreparedEntry? hit;
        lock (PreparedGate)
            hit = Prepared.FirstOrDefault(e => e.Key.Equals(key, StringComparison.OrdinalIgnoreCase) && e.Options.Equals(options));
        if (hit is not null && lookup.IsCurrent(hit.Dependencies) && hit.Files.All(f => Ticks(f.Path) == f.Ticks))
        {
            lock (PreparedGate)
                if (Prepared.Find(hit) is { } node)
                {
                    Prepared.Remove(node);
                    Prepared.AddFirst(node);
                }
            ApexGdtLookup.Replay(hit.Dependencies);
            return hit.Model;
        }

        using var recording = ApexGdtLookup.Record();
        var model = prepare();
        var entry = new PreparedEntry(key, options, model, recording.Dependencies, SourceFiles(data, recording.Dependencies, files));
        lock (PreparedGate)
        {
            if (hit is not null)
                Prepared.Remove(hit);
            Prepared.AddFirst(entry);
            while (Prepared.Count > PreparedCapacity)
                Prepared.RemoveLast();
        }
        return model;
    }

    private static long Ticks(string path) => File.GetLastWriteTimeUtc(path).Ticks;

    private static List<(string Path, long Ticks)> SourceFiles(ToolsGfxData data, LookupDependencies dependencies, IReadOnlyList<string>? files)
    {
        var paths = new HashSet<string>(files ?? [], StringComparer.OrdinalIgnoreCase);
        foreach (var (_, resolved) in dependencies)
        {
            if (resolved.Entry is not { Gdf: { } gdf } entry)
                continue;
            if (GdtEntry.IsType(gdf, "xmodel"))
            {
                foreach (var field in LodFields)
                    if (entry.Fields.GetValueOrDefault(field) is { } file && !string.IsNullOrWhiteSpace(file))
                        paths.Add(data.Models.ResolveModelExportPath(file));
            }
            else if (GdtEntry.IsType(gdf, "image") && entry.Fields.GetValueOrDefault("baseImage") is { Length: > 0 } baseImage
                     && data.ResolveSourcePath(baseImage.Split(',')[0]) is { } source)
            {
                paths.Add(source);
            }
        }
        return paths.Select(p => (p, Ticks(p))).ToList();
    }

    /// <summary>"converted by Apex (…)" when part of <paramref name="prepared"/> came from Apex's converter, else null.</summary>
    public static string? ConversionNote(PreparedPreviewModel? prepared) =>
        prepared is not null && ConversionNotes.TryGetValue(prepared, out var note) ? note : null;

    /// <summary>The shared preview environment (loaded once, off the UI thread); null when unavailable
    /// (<see cref="UnavailableReason"/> says why).</summary>
    public static Task<PreviewEnvironment?> GetEnvironmentAsync(GameEnvironment env, Func<string, string, AssetRecord?> resolve)
    {
        lock (Gate)
        {
            if (_environment != null)
                return _environment;
            var data = GetData(env, resolve);
            if (data is null)
                return Task.FromResult<PreviewEnvironment?>(null);
            return _environment = Task.Run<PreviewEnvironment?>(() =>
            {
                try
                {
                    var loaded = PreviewEnvironment.Load(data);
                    // Warm the default state's skybox so the first frame has it.
                    loaded.Sky(LightState);
                    return loaded;
                }
                catch (Exception ex) when (ex is System.IO.IOException or InvalidOperationException or System.IO.InvalidDataException)
                {
                    lock (Gate)
                        _unavailableReason = "preview lighting unavailable: " + ex.Message;
                    return null;
                }
            });
        }
    }
}
