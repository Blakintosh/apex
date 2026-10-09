using System.Collections.Concurrent;
using System.Numerics;
using Apex.Render.Assets;
using Apex.Render.Data;
using Apex.Render.Data.Assets;
using Apex.Render.Data.Lighting;
using Apex.Render.Passes;
using Apex.Render.Scene;

namespace Apex.Render.Presentation;

/// <summary>
/// Everything about APE's preview scene that does not depend on the asset or the device: the four lighting states
/// (assetviewer.led + SSI GDT assets), <c>$env_brdf_generic</c>, and each state's SSI skybox model prepared on the
/// CPU. Thread-safe; one per install, shared by every preview (GPU copies live in <see cref="PreviewDeviceResources"/>).
/// </summary>
public sealed class PreviewEnvironment
{
    private readonly ConcurrentDictionary<PreviewLightState, Lazy<(PreparedPreviewModel? Model, string? Error)>> _skies = new();
    private readonly ConcurrentDictionary<PreviewLightState, SceneInputs> _sceneInputs = new();

    public ToolsGfxData Data { get; }
    public LightingStates Lighting { get; }
    public CachedImage EnvBrdf { get; }

    private PreviewEnvironment(ToolsGfxData data, LightingStates lighting, CachedImage envBrdf)
    {
        Data = data;
        Lighting = lighting;
        EnvBrdf = envBrdf;
    }

    /// <summary>Reads the LED lighting states and the env BRDF (a few hundred ms; call off the UI thread). Throws when
    /// the install lacks them.</summary>
    public static PreviewEnvironment Load(ToolsGfxData data)
    {
        var lighting = LightingStates.Load(data.Install, data.Gdt);
        if (lighting.States.Count == 0)
            throw new InvalidDataException("assetviewer.led has no lighting states");
        var brdf = LightingStates.LoadEnvBrdf(data.Images)
            ?? throw new FileNotFoundException("$env_brdf_generic is not in the image cache (open any asset in APE once)");
        return new PreviewEnvironment(data, lighting, brdf);
    }

    public bool HasState(PreviewLightState state)
    {
        foreach (var s in Lighting.States)
            if (s.State == state)
                return true;
        return false;
    }

    public PreviewLighting State(PreviewLightState state) => Lighting[state];

    /// <summary>The state's SSI skybox xmodel prepared for RENDER_STAGE_EMISSIVE_SKY (null with the reason when it
    /// cannot be loaded — the frame then shows APE's clear colour behind the model). Cached.</summary>
    public (PreparedPreviewModel? Model, string? Error) Sky(PreviewLightState state)
        => _skies.GetOrAdd(state, s => new Lazy<(PreparedPreviewModel?, string?)>(() => LoadSky(s))).Value;

    /// <summary><see cref="Sky"/> on a worker thread.</summary>
    public Task<(PreparedPreviewModel? Model, string? Error)> SkyAsync(PreviewLightState state) => Task.Run(() => Sky(state));

    /// <summary>True once <see cref="Sky"/> has finished for <paramref name="state"/> (so it will not block).</summary>
    public bool IsSkyLoaded(PreviewLightState state) => _skies.TryGetValue(state, out var l) && l.IsValueCreated;

    private (PreparedPreviewModel?, string?) LoadSky(PreviewLightState state)
    {
        var name = State(state).Sun?.SkyboxModel;
        if (string.IsNullOrEmpty(name))
            return (null, "no skybox model in the SSI");
        try
        {
            return (PreviewModelLoader.Prepare(Data, name, new PreviewModelOptions
            {
                Techniques = new HashSet<PreviewTechnique> { PreviewTechnique.Emissive },
                SortDepth = 1f,
            }), null);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or KeyNotFoundException)
        {
            return (null, $"skybox {name}: {ex.Message}");
        }
    }

    /// <summary>A cb9 builder for this state at a camera (APE's per-frame inputs; everything but the camera, size
    /// and sun overrides comes from the LED/SSI data).</summary>
    public SceneConstantsBuilder CreateSceneBuilder(PreviewLightState state, PreviewCamera camera, int width, int height)
    {
        if (!_sceneInputs.TryGetValue(state, out var inputs))
            inputs = _sceneInputs.GetOrAdd(state, SceneInputsOf(state));
        return new SceneConstantsBuilder
        {
            Camera = camera,
            Width = width,
            Height = height,
            Lighting = inputs.Lighting,
            GlobalProbe = inputs.GlobalProbe,
            SunShadowParameters = inputs.SunShadow,
        };
    }

    /// <summary>The camera-independent cb9 inputs of a state (immutable, so built once and shared by every frame).</summary>
    private sealed record SceneInputs(LightingPreset Lighting, GlobalProbeInputs GlobalProbe, SunShadowParameters? SunShadow);

    private SceneInputs SceneInputsOf(PreviewLightState state)
    {
        var pl = State(state);
        var tail = pl.GlobalProbeData(Vector3.Zero).Skip(3).Select(BitConverter.SingleToUInt32Bits).ToArray();
        return new SceneInputs(
            LightingPreset.FromData(pl),
            new GlobalProbeInputs(pl.ProbeConfig.Center, tail),
            pl.Sun is { } sun
                ? new SunShadowParameters(sun.PenumbraInches, Lighting.Led.Volumes[0].ShadowSplitDistance, pl.SunShadowTreeDimensionInTiles)
                : null);
    }
}
