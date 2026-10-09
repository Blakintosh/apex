using System.Numerics;
using Apex.Render.Constants;

namespace Apex.Render.Scene;

/// <summary>Sun-shadow and sun-shadow-tree inputs. Cascades come from the shadow pass (not built yet) and the
/// SST from assetviewer.led; <see cref="SstWldToPin"/> is the camera-independent part (cb9 stores it off-space).</summary>
public sealed record SunShadowInputs
{
    public Vector4 SplitPinTransform0 { get; init; }
    public Vector4 SplitPinTransform1 { get; init; }
    public Vector4 SplitPinTransform2 { get; init; }
    public float SplitDepthOffset { get; init; }
    public int SplitArrayOffset { get; init; }
    public Vector2 SstDimensionInTiles { get; init; }
    public float SstInchesPerTexel { get; init; }
    public float SstSpanInInches { get; init; }
    public Matrix4x4 SstWldToPin { get; init; }
    public float SstCoordScale { get; init; } = 1f;
    public uint SstRootOffset { get; init; }
}

/// <summary>LED/SSI inputs from which the sun-shadow cascades and SST constants are computed each frame
/// (<see cref="SunShadowCascades"/>): SSI penumbra, LED <c>volumes[0].shadowSplitDistance</c> and the lighting
/// state's SST <c>dimensionInTiles</c>.</summary>
public sealed record SunShadowParameters(float PenumbraInches, float SplitDistance, Vector2 SstDimensionInTiles);

/// <summary>LED global probe: world origin (cb9 stores it camera-relative) plus the rest of the 48-byte pack.</summary>
public sealed record GlobalProbeInputs(Vector3 WorldOrigin, uint[] PackTail)
{
    /// <summary>Unpacks a captured <c>globalProbe.data</c> given the camera it was captured with.</summary>
    public static GlobalProbeInputs FromPacked(ReadOnlySpan<uint> data12, Vector3 cameraPosition)
    {
        var rel = new Vector3(BitConverter.UInt32BitsToSingle(data12[0]), BitConverter.UInt32BitsToSingle(data12[1]), BitConverter.UInt32BitsToSingle(data12[2]));
        return new GlobalProbeInputs(rel + cameraPosition, data12[3..].ToArray());
    }
}

/// <summary>
/// Builds <see cref="CodeSceneConsts"/> (cb9) for the preview exactly as <c>Gfx_SetViewParms</c> /
/// <c>Gfx_BeginScene</c> / <c>ToolsGfx_PreviewRenderFrame</c> do (pipeline.md §2–3), including the quirks the
/// captures revealed: reversed-Z infinite projection with near = 1, <c>farClipScale = −0</c>, the
/// aspect/FOV-independent <c>viewSpaceScaleBias</c>, and the non-zero weather defaults.
/// </summary>
public sealed class SceneConstantsBuilder
{
    public PreviewCamera Camera { get; set; } = PreviewCamera.Default;
    public int Width { get; set; } = 1600;
    public int Height { get; set; } = 861;
    public LightingPreset Lighting { get; set; } = LightingPreset.Morning;
    /// <summary>Preview "Lighting" toggle; false = "No Lighting" (UNLIT scene path).</summary>
    public bool LightingEnabled { get; set; } = true;
    /// <summary>User sun rotation overrides (degrees), written back each frame like renderer fields +46404/+46408.</summary>
    public float? SunPitchOverride { get; set; }
    public float? SunYawOverride { get; set; }
    public float SkyRotationOffset { get; set; }
    public float SkySize { get; set; } = 8000f;
    public float Time { get; set; }
    /// <summary>cb9 <c>siegeTime</c>: the playing xanim's time in seconds (0 when no animation plays; the vm_drop capture
    /// holds the clip time there).</summary>
    public float SiegeTime { get; set; }
    public GlobalProbeInputs? GlobalProbe { get; set; }
    /// <summary>Explicit (e.g. captured) split/SST values; overrides <see cref="SunShadowParameters"/>.</summary>
    public SunShadowInputs? SunShadow { get; set; }
    /// <summary>Inputs of APE's per-frame cascade computation (normal path).</summary>
    public SunShadowParameters? SunShadowParameters { get; set; }
    /// <summary><c>gSpotShadowResolutionAndRcp</c>.x — the spot shadow atlas size (lighting RT0 is 2048²).</summary>
    public float SpotShadowResolution { get; set; } = 2048f;

    /// <summary>sceneDesc tan-half term (+400) and aspect (+404) feeding viewSpaceScaleBias. The preview leaves
    /// them at 1 and 16/9, so the captured value is (3.5556, −2, 1.7778, −1) at every window size.</summary>
    public float SceneDescTanHalf { get; set; } = 1f;
    public float SceneDescAspect { get; set; } = 16f / 9f;

    public float TanHalfFovY => Camera.TanHalfFovY;
    public float TanHalfFovX => (float)Width / Height * Camera.TanHalfFovY;

    /// <summary>Reversed-Z infinite projection, row-vector convention (pipeline.md §2.2).</summary>
    public static Matrix4x4 Projection(float tanHalfX, float tanHalfY) => new(
        1f / tanHalfX, 0, 0, 0,
        0, 1f / tanHalfY, 0, 0,
        0, 0, 0, 1,
        0, 0, 1, 0);

    /// <summary>World→camera: view x = right, y = up, z = forward (clip.w = depth along forward).</summary>
    public static Matrix4x4 ViewMatrix(Vector3 position, Vector3 forward, Vector3 right, Vector3 up) => new(
        right.X, up.X, forward.X, 0,
        right.Y, up.Y, forward.Y, 0,
        right.Z, up.Z, forward.Z, 0,
        -Vector3.Dot(position, right), -Vector3.Dot(position, up), -Vector3.Dot(position, forward), 1);

    public CodeSceneConsts Build() => Build(out _);

    /// <summary><see cref="Build()"/>, also returning <see cref="ComputeSunShadow()"/> of the same inputs (the camera
    /// and cascades are computed once for both).</summary>
    public CodeSceneConsts Build(out SunShadowSetup? sunShadow)
    {
        var c = new CodeSceneConsts();
        var cam = Camera;
        var pos = cam.Position;

        // ── Transforms: APE's camera math (PreviewViewMath; bit-exact vs all captures given the raw origin) ──
        var view = PreviewViewMath.Build(pos, cam.PitchDegrees, cam.YawDegrees, cam.RollDegrees, cam.FovDegrees, Width, Height);
        var t = view.Transforms;
        c.Transforms = new CodeSceneTransforms
        {
            WldToCam = t.WldToCam,
            CamToOff = t.CamToOff,
            OffToCam = t.OffToCam,
            CamToClp = t.CamToClp,
            CamToWld = t.CamToWld,
            WldToClp = t.WldToClp,
            OffToClp = t.OffToClp,
            ClpToCam = t.ClpToCam,
        };
        // cb9 wldCameraPosition is row 3 of the inverted view (1–2 ulp off the raw origin, which everything else uses).
        c.WldCameraPosition = t.WldCameraPosition;
        c.NearClip = view.NearClip;
        c.FarClipScale = view.FarClipScale;
        c.ViewSpaceScaleBias = view.ViewSpaceScaleBias;
        c.RenderTargetWidth = (uint)Width;
        c.RenderTargetHeight = (uint)Height;
        c.RenderTargetInvSize = new Vector2(1f / Width, 1f / Height);
        c.Time = Time;
        c.SiegeTime = SiegeTime;

        // ── Fog: only the colours are set in the preview ──
        c.Fog.FogColor = new Vector4(1, 1, 1, 0);
        c.Fog.SunFogColor = new Vector4(1, 1, 1, 0);

        // ── Weather defaults as captured (pipeline.md said zero; the preview sceneDesc holds these) ──
        c.Weather.Rain = 1f;
        c.Weather.WindSpeed = 5f;
        c.Weather.WeatherTile = 4f;
        c.Weather.WeatherVector = new Vector3(0, 1, 0);
        c.Weather.WeatherVector2 = new Vector3(1, 0, 0);
        c.Weather.WeatherTint = new Vector3(1, 1, 1);
        c.Weather.WeatherTint2 = new Vector3(1, 0, 0);

        // ── Sun (Gfx_BuildSunParams / Gfx_BuildCoreSunConstants) ──
        var lighting = Lighting;
        var sunPreset = SunLighting;
        var sunDir = sunPreset.SunDirection;
        c.Sun.WldDir = sunDir;
        c.Sun.Color = sunPreset.SunColor;
        c.Sun.Intensity = sunPreset.SunIntensity;
        c.Sun.SpecScale = sunPreset.SpecScale;
        c.Sun.GlobalProbeExposure = lighting.GlobalProbeExposure;
        c.Sun.AvgGlobalProbeColor = lighting.AvgGlobalProbeColor;
        c.Sun.ToolsGfxDisableSunShadow = lighting.EnableSun ? 0u : 1u;
        sunShadow = SunShadowParameters is not null && lighting.EnableSun ? ComputeSunShadow(view.ShadowCamera, sunDir) : null;
        if (SunShadow is null && sunShadow is { } setup)
        {
            c.Sun.SplitPinTransform[0] = setup.SplitPinTransform0;
            c.Sun.SplitPinTransform[1] = setup.SplitPinTransform1;
            c.Sun.SplitPinTransform[2] = setup.SplitPinTransform2;
            c.Sun.SplitDepthOffset = setup.SplitDepthOffset;
            c.Sun.SplitArrayOffset = setup.SplitArrayOffset;
            c.Sun.SstLightingConstants = new SstLightingConstants
            {
                DimensionInTiles = setup.SstDimensionInTiles,
                InchesPerTexel = setup.SstInchesPerTexel,
                SpanInInches = setup.SstSpanInInches,
                OffToPinTransform = setup.OffToPin(pos),
                CoordScale = setup.SstCoordScale,
                RootOffset = setup.SstRootOffset,
            };
        }
        else if (SunShadow is { } sh)
        {
            c.Sun.SplitPinTransform[0] = sh.SplitPinTransform0;
            c.Sun.SplitPinTransform[1] = sh.SplitPinTransform1;
            c.Sun.SplitPinTransform[2] = sh.SplitPinTransform2;
            c.Sun.SplitDepthOffset = sh.SplitDepthOffset;
            c.Sun.SplitArrayOffset = sh.SplitArrayOffset;
            c.Sun.SstLightingConstants = new SstLightingConstants
            {
                DimensionInTiles = sh.SstDimensionInTiles,
                InchesPerTexel = sh.SstInchesPerTexel,
                SpanInInches = sh.SstSpanInInches,
                OffToPinTransform = Matrix4x4.CreateTranslation(pos) * sh.SstWldToPin,
                CoordScale = sh.SstCoordScale,
                RootOffset = sh.SstRootOffset,
            };
        }

        // ── Global probe (LED), stored relative to cb9 wldCameraPosition (the view-derived position, not the raw origin) ──
        if (GlobalProbe is { } gp)
        {
            var rel = gp.WorldOrigin - c.WldCameraPosition;
            c.GlobalProbe.Data[0] = BitConverter.SingleToUInt32Bits(rel.X);
            c.GlobalProbe.Data[1] = BitConverter.SingleToUInt32Bits(rel.Y);
            c.GlobalProbe.Data[2] = BitConverter.SingleToUInt32Bits(rel.Z);
            for (int i = 0; i < Math.Min(9, gp.PackTail.Length); i++)
                c.GlobalProbe.Data[3 + i] = gp.PackTail[i];
        }

        // ── Exposure (ToolsGfx_PreviewRenderFrame @0x140145d5a), single precision as APE ──
        float exposure = PreviewViewMath.Exposure(lighting.GlobalProbeExposure, lighting.EvMin, lighting.EvMax);
        c.Exposure = exposure;
        c.InvExposure = 1f / exposure;
        c.ExposureClamped = exposure;

        // ── Gfx_BeginScene scalars ──
        c.LightingMode = LightingEnabled ? LightingMode.Real : LightingMode.Unlit;
        c.SpotShadowResolutionAndRcp = new Vector2(SpotShadowResolution, 1f / SpotShadowResolution);
        c.SkyRotation = PreviewViewMath.SkyRotation(sunDir, SkyRotationOffset);
        c.SkySize = SkySize;
        c.SkyTransition = 0f;
        c.FullHdr = 0;
        return c;
    }

    /// <summary>The camera as APE's sun-shadow code sees it (forward, left, up; tan half FOVs).</summary>
    public SunShadowCamera ShadowCamera =>
        PreviewViewMath.Build(Camera.Position, Camera.PitchDegrees, Camera.YawDegrees, Camera.RollDegrees, Camera.FovDegrees, Width, Height).ShadowCamera;

    /// <summary><see cref="Lighting"/> with the sun overrides applied.</summary>
    private LightingPreset SunLighting => SunPitchOverride is null && SunYawOverride is null
        ? Lighting
        : Lighting with
        {
            SunPitch = SunPitchOverride ?? Lighting.SunPitch,
            SunYaw = SunYawOverride ?? Lighting.SunYaw,
        };

    private SunShadowSetup ComputeSunShadow(in SunShadowCamera camera, Vector3 sunDir)
    {
        var p = SunShadowParameters ?? throw new InvalidOperationException("SunShadowParameters not set");
        return SunShadowCascades.Compute(camera, new SunShadowCascadeInputs(sunDir, p.PenumbraInches, p.SplitDistance, p.SstDimensionInTiles));
    }

    /// <summary>The current frame's cascade setup (null when the sun or its shadow parameters are absent).</summary>
    public SunShadowSetup? ComputeSunShadow()
    {
        var lighting = SunLighting;
        return SunShadowParameters is null || !lighting.EnableSun ? null : ComputeSunShadow(ShadowCamera, lighting.SunDirection);
    }

    /// <summary>
    /// The b9 of each sun-shadow cascade draw ("sun shadow dynamics (i)"): the main view's constants with the
    /// cascade's eight transforms and camera position, a 1024² render target, <c>nearClip = −0</c>,
    /// <c>farClipScale = 1/16384</c>, exposure 1, <c>lightingMode</c> 0, and the global probe and weather blocks
    /// zeroed. The sun block (splits, SST) is byte-identical to the main view's.
    /// </summary>
    public static CodeSceneConsts[] BuildSunShadowViews(in CodeSceneConsts main, SunShadowSetup setup)
    {
        var views = new CodeSceneConsts[setup.Cascades.Length];
        BuildSunShadowViews(main, setup, views);
        return views;
    }

    /// <summary><see cref="BuildSunShadowViews(in CodeSceneConsts, SunShadowSetup)"/> into <paramref name="views"/> (one per cascade).</summary>
    public static void BuildSunShadowViews(in CodeSceneConsts main, SunShadowSetup setup, Span<CodeSceneConsts> views)
    {
        if (views.Length != setup.Cascades.Length)
            throw new ArgumentException($"{setup.Cascades.Length} views expected");
        for (int i = 0; i < views.Length; i++)
        {
            var t = setup.Cascades[i].Transforms;
            var v = main;
            v.Transforms = new CodeSceneTransforms
            {
                WldToCam = t.WldToCam,
                CamToOff = t.CamToOff,
                OffToCam = t.OffToCam,
                CamToClp = t.CamToClp,
                CamToWld = t.CamToWld,
                WldToClp = t.WldToClp,
                OffToClp = t.OffToClp,
                ClpToCam = t.ClpToCam,
            };
            v.WldCameraPosition = t.WldCameraPosition;
            v.RenderTargetWidth = SunShadowCascades.ShadowRenderTargetSize;
            v.RenderTargetHeight = SunShadowCascades.ShadowRenderTargetSize;
            v.RenderTargetInvSize = new Vector2(1f / SunShadowCascades.ShadowRenderTargetSize, 1f / SunShadowCascades.ShadowRenderTargetSize);
            v.NearClip = SunShadowCascades.ShadowNearClip;
            v.FarClipScale = SunShadowCascades.ShadowFarClipScale;
            v.Exposure = 1f;
            v.InvExposure = 1f;
            v.ExposureClamped = 1f;
            v.LightingMode = 0;
            v.GlobalProbe = default;
            v.Weather = default;
            views[i] = v;
        }
    }
}
