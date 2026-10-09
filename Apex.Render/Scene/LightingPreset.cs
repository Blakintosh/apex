using System.Numerics;

namespace Apex.Render.Scene;

/// <summary>
/// One APE "Lighting" menu entry = an assetviewer.led lighting state bound to an SSI asset (pipeline.md §3).
/// SSI fields (sun angles, colour, stops, EV clamp) come from <c>source_data/ssi.gdt</c>; the global-probe
/// exposure and average colour come from the LED. Until the data layer parses both, <see cref="Morning"/> …
/// <see cref="Night"/> carry the values read from the RenderDoc captures (all four match pipeline.md exactly).
/// </summary>
public sealed record LightingPreset(
    string Name,
    string SsiAsset,
    /// <summary>SSI sun pitch/yaw in degrees.</summary>
    float SunPitch,
    float SunYaw,
    /// <summary>sRGB→linear SSI sun colour (before intensity).</summary>
    Vector3 SunLinearColor,
    /// <summary>SSI <c>stops</c>: intensity = 2^stops.</summary>
    float SunStops,
    float SpecCompensation,
    float EvMin,
    float EvMax,
    float EvCmp,
    /// <summary>LED global probe exposure P.</summary>
    float GlobalProbeExposure,
    Vector3 AvgGlobalProbeColor,
    string SkyboxModel,
    bool EnableSun = true)
{
    public static LightingPreset Morning { get; } = new("Morning", "default_morning", 165f, 263f,
        new Vector3(1f, 0.775787658207502f, 0.5087641088286351f), 11.299997849428f, 0f, -32f, 31f, 0f,
        442.3148498535156f, new Vector3(0.9580284953117371f, 1.0121933221817017f, 1.0028045177459717f), "skybox_default_day_clear_0700");

    public static LightingPreset Day { get; } = new("Day", "default_day", 125f, 150f,
        new Vector3(1f, 0.9471510648727417f, 0.8878815174102783f), 14f, 0f, 1f, 16f, 0f,
        1941.2540283203125f, new Vector3(0.7713019251823425f, 1.0134860277175903f, 1.5398342609405518f), "skybox_default_day");

    public static LightingPreset Sunset { get; } = new("Sunset", "default_sunset", 158f, 300f,
        new Vector3(1f, 0.551245391368866f, 0.25883036851882935f), 11f, 0f, 8f, 12.5f, 0f,
        219.8245086669922f, new Vector3(0.7664903402328491f, 1.0251152515411377f, 1.4388055801391602f), "skybox_default_sunset");

    public static LightingPreset Night { get; } = new("Night", "default_night", 130f, 140f,
        new Vector3(0.5891835041444221f, 1f, 1f), -2.2f, 0f, 3f, 3.5f, 2.5f,
        0.022296952083706856f, new Vector3(0.3945227861404419f, 1.127380609512329f, 1.5210784673690796f), "skybox_default_night");

    /// <summary>A preset from the data layer (LED lighting state + its SSI GDT asset) — the production path; the
    /// static presets above mirror these values for tests without an install.</summary>
    public static LightingPreset FromData(Apex.Render.Data.Lighting.PreviewLighting state)
    {
        var sun = state.Sun ?? throw new InvalidDataException($"SSI '{state.SsiName}' was not found; no sun parameters.");
        var linear = new Vector3(SrgbToLinear(sun.ColorSrgb.X), SrgbToLinear(sun.ColorSrgb.Y), SrgbToLinear(sun.ColorSrgb.Z));
        return new LightingPreset(state.State.ToString(), state.SsiName, sun.Pitch, sun.Yaw, linear, sun.Stops,
            sun.SpecCompensation, sun.EvMin, sun.EvMax, sun.EvCompensation, state.GlobalProbeExposure, state.AverageGlobalProbeColor,
            sun.SkyboxModel, sun.EnableSun);
    }

    /// <summary>Indexed by APE's <c>Preview/LightState</c> setting (0–3).</summary>
    public static IReadOnlyList<LightingPreset> All { get; } = new[] { Morning, Day, Sunset, Night };

    /// <summary><c>wldDir = −AngleVectors(pitch, yaw).forward = (−cosP·cosY, −cosP·sinY, sinP)</c> — points to the sun.</summary>
    public Vector3 SunDirection
    {
        get
        {
            const float d2r = MathF.PI / 180f;
            float cp = MathF.Cos(SunPitch * d2r), sp = MathF.Sin(SunPitch * d2r);
            float cy = MathF.Cos(SunYaw * d2r), sy = MathF.Sin(SunYaw * d2r);
            return new Vector3(-cp * cy, -cp * sy, sp);
        }
    }

    public float SunIntensity => MathF.Pow(2f, SunStops);
    public Vector3 SunColor => SunLinearColor * SunIntensity;
    public float SpecScale => Math.Clamp(MathF.Pow(2f, SpecCompensation), 0f, 65504f);

    /// <summary>pipeline.md §3.3: <c>2^(clamp(log2(P) + 5, evmin, evmax) − 3)</c>. No GPU auto-exposure feeds the preview.</summary>
    public float Exposure => ComputeExposure(GlobalProbeExposure, EvMin, EvMax);

    public static float ComputeExposure(float probeExposure, float evMin, float evMax)
    {
        float ev = MathF.Log2(probeExposure) + 3f + 2f;
        ev = Math.Clamp(ev, evMin, evMax);
        return MathF.Pow(2f, ev - 3f);
    }

    /// <summary>sRGB → linear for SSI <c>colorSRGB</c> values, in float exactly as <c>Gfx_LoadSSI</c>'s helper
    /// (0x14026F550) — reproduces the captured <c>sun.color</c> bit-exact for all four presets.</summary>
    public static float SrgbToLinear(float c) => c > 0.04045f ? MathF.Pow((c + 0.055f) / 1.055f, 2.4f) : c / 12.92f;
}
