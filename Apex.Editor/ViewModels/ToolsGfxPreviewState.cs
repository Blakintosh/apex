using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Apex.Editor.Models;
using Apex.Editor.Services.Gdt;
using Apex.Editor.Services.Preview.ToolsGfx;
using Apex.Render.Assets;
using Apex.Render.Data;
using Apex.Render.Data.Lighting;
using Apex.Render.Presentation;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Apex.Editor.ViewModels;

/// <summary>One lighting-state pill (Morning / Day / Sunset / Night).</summary>
public sealed partial class LightStateOption : ObservableObject
{
    private readonly PreviewLighting _owner;

    public string Label { get; }
    public PreviewLightState State { get; }

    [ObservableProperty]
    private bool _isActive;

    public LightStateOption(PreviewLighting owner, PreviewLightState state)
    {
        _owner = owner;
        State = state;
        Label = state.ToString();
    }

    [RelayCommand]
    private void Select() => _owner.LightState = State;
}

/// <summary>
/// The lighting state every ToolsGfx preview draws with (APE's <c>Preview/LightState</c>): one for the app, so picking
/// Night on one preview lights them all, and remembered across sessions (<see cref="Services.UiSettings"/>).
/// </summary>
public sealed partial class PreviewLighting : ObservableObject
{
    public static PreviewLighting Shared { get; } = new();

    [ObservableProperty]
    private PreviewLightState _lightState = ToolsGfxPreviewService.LightState;

    public ObservableCollection<LightStateOption> LightOptions { get; }

    private PreviewLighting()
    {
        LightOptions = new ObservableCollection<LightStateOption>(
            Enum.GetValues<PreviewLightState>().Select(s => new LightStateOption(this, s)));
        SyncOptions();
    }

    partial void OnLightStateChanged(PreviewLightState value)
    {
        ToolsGfxPreviewService.LightState = value;
        SyncOptions();
        // Warm the state's skybox; the viewport draws without it until it is ready.
        ToolsGfxPreviewService.WarmSky(value);
    }

    private void SyncOptions()
    {
        // Always re-assert: the two-way IsChecked binding unchecks an active pill on re-click.
        foreach (var o in LightOptions)
            o.IsActive = o.State == LightState;
    }
}

/// <summary>
/// Which renderer a 3D preview uses and why. The ToolsGfx renderer (APE's own frame, <see cref="Controls.ToolsGfxPreviewViewport"/>)
/// is used whenever the install's ToolsGfx data, the asset's xmesh cache and D3D11 interop are all available; otherwise
/// the preview falls back to the OpenGL approximation and <see cref="FallbackStatus"/> says so (<see cref="FallbackDetail"/>
/// why).
/// </summary>
public sealed partial class ToolsGfxPreviewState : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsActive))]
    private PreviewEnvironment? _environment;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsActive))]
    private PreparedPreviewModel? _model;

    /// <summary>One plain line while the fallback renderer is in use; <see cref="FallbackDetail"/> says why.</summary>
    [ObservableProperty]
    private string? _fallbackStatus;

    /// <summary>Why the ToolsGfx renderer could not be used (tooltip).</summary>
    [ObservableProperty]
    private string? _fallbackDetail;

    /// <summary>The app-wide lighting state (<see cref="PreviewLighting.Shared"/>).</summary>
    public PreviewLighting Lighting => PreviewLighting.Shared;

    /// <summary>True while the ToolsGfx viewport is shown.</summary>
    public bool IsActive => Environment is not null && Model is not null;

    /// <summary>
    /// Prepares the preview model on a worker thread. Returns true (and shows the ToolsGfx viewport) on success; false
    /// with <see cref="FallbackStatus"/> set when the renderer or the asset's caches are unavailable.
    /// </summary>
    public async Task<bool> TryLoadAsync(GameEnvironment env, Func<string, string, AssetRecord?> resolve,
        Func<ToolsGfxData, PreparedPreviewModel> prepare, CancellationToken ct)
    {
        string? reason = ToolsGfxPreviewService.UnavailableReason;
        if (reason is null)
        {
            var environment = await ToolsGfxPreviewService.GetEnvironmentAsync(env, resolve);
            if (ct.IsCancellationRequested)
                return false;
            if (environment is null)
            {
                reason = ToolsGfxPreviewService.UnavailableReason ?? "ToolsGfx data unavailable";
            }
            else
            {
                ToolsGfxPreviewService.BeginLoad(resolve);
                try
                {
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    var prepared = await Task.Run(() => prepare(environment.Data), ct);
                    if (ToolsGfxPreviewService.IsLogging)
                        ToolsGfxPreviewService.Log($"{prepared.Name} | prepared in {clock.ElapsedMilliseconds} ms: {prepared.Materials.Count} materials "
                            + $"({prepared.BrokenMaterialCount} broken), {prepared.Warnings.Count} warnings{(prepared.Warnings.Count > 0 ? ": " + string.Join("; ", prepared.Warnings.Distinct().Take(3)) : "")}");
                    if (ct.IsCancellationRequested)
                        return false;
                    if (prepared.Materials.Count > 0 && prepared.UndrawnMaterialCount == prepared.Materials.Count)
                    {
                        reason = "no material could be built: " + prepared.Warnings.FirstOrDefault();
                    }
                    else
                    {
                        Environment = environment;
                        Model = prepared;
                        FallbackStatus = null;
                        FallbackDetail = null;
                        return true;
                    }
                }
                catch (OperationCanceledException)
                {
                    return false;
                }
                catch (Exception ex) when (Services.CrashGuard.IsRecoverable(ex))
                {
                    // Any failure preparing the model (a malformed file, a conversion bug) falls back to the
                    // approximate renderer for this asset only; the next asset starts clean.
                    if (ct.IsCancellationRequested)
                        return false;
                    if (ex is not (IOException or InvalidDataException or InvalidOperationException or ArgumentException
                            or FormatException or System.Collections.Generic.KeyNotFoundException))
                        Services.CrashGuard.WriteLog(ex, "ToolsGfx preview preparation");
                    reason = ex.Message;
                }
            }
        }
        if (ToolsGfxPreviewService.IsLogging)
            ToolsGfxPreviewService.Log($"fallback to OpenGL: {reason}");
        Fallback(reason);
        return false;
    }

    /// <summary>Switches to the OpenGL approximation (the ToolsGfx viewport reported <paramref name="reason"/>).</summary>
    public void Fallback(string reason)
    {
        Model = null;
        FallbackStatus = "Approximate preview, without the game's shaders";
        FallbackDetail = reason;
    }

    /// <summary>Forgets the loaded model (e.g. the asset lost its model file).</summary>
    public void Clear()
    {
        Model = null;
        FallbackStatus = null;
        FallbackDetail = null;
    }
}
