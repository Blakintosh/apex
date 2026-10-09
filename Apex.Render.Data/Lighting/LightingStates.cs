using System.Globalization;
using System.Numerics;
using Apex.Render.Data.Assets;
using Apex.Render.Data.Gdt;
using Apex.Render.Data.Techsets;

namespace Apex.Render.Data.Lighting;

/// <summary>APE's preview lighting presets (<c>HKCU\Software\Treyarch\APE\Preview\LightState</c>).</summary>
public enum PreviewLightState
{
    Morning = 0,
    Day = 1,
    Sunset = 2,
    Night = 3,
}

/// <summary>
/// Sun and exposure parameters of an SSI asset (<c>Gfx_LoadSSI</c> 0x140270AA0; pipeline.md §3.1-3.3).
/// </summary>
public sealed class SunParameters
{
    public required string Name { get; init; }
    public required float Pitch { get; init; }
    public required float Yaw { get; init; }

    /// <summary>GDT <c>colorSRGB</c> rgb.</summary>
    public required Vector3 ColorSrgb { get; init; }

    public required float Stops { get; init; }
    public required float SpecCompensation { get; init; }
    public required float PenumbraInches { get; init; }
    public required string SkyboxModel { get; init; }
    public required bool EnableSun { get; init; }
    public required bool DynamicShadow { get; init; }
    public required float EvCompensation { get; init; }
    public required float EvMin { get; init; }
    public required float EvMax { get; init; }
    public required int BounceCount { get; init; }

    /// <summary><c>2^stops</c> → cb9 <c>sun.intensity</c>.</summary>
    public float Intensity => MathF.Pow(2f, Stops);

    /// <summary><c>srgb_to_linear(colorSRGB)</c>.</summary>
    public Vector3 LinearColor => new(
        (float)HlslExpression.SrgbToLinear(ColorSrgb.X),
        (float)HlslExpression.SrgbToLinear(ColorSrgb.Y),
        (float)HlslExpression.SrgbToLinear(ColorSrgb.Z));

    /// <summary><c>linear(colorSRGB) · 2^stops</c> → cb9 <c>sun.color</c>.</summary>
    public Vector3 Color => LinearColor * Intensity;

    /// <summary><c>clamp(2^spec_comp, 0, 65504)</c> → cb9 <c>sun.specScale</c>.</summary>
    public float SpecScale => Math.Clamp(MathF.Pow(2f, SpecCompensation), 0f, 65504f);

    /// <summary>Direction to the sun: <c>(−cosP·cosY, −cosP·sinY, sinP)</c> → cb9 <c>sun.wldDir</c>.</summary>
    public Vector3 WorldDirection => WorldDirectionFor(Pitch, Yaw);

    /// <summary>Sun direction for an arbitrary (user-rotated) pitch/yaw in degrees.</summary>
    public static Vector3 WorldDirectionFor(float pitchDegrees, float yawDegrees)
    {
        double p = pitchDegrees * Math.PI / 180.0, y = yawDegrees * Math.PI / 180.0;
        return new Vector3((float)(-Math.Cos(p) * Math.Cos(y)), (float)(-Math.Cos(p) * Math.Sin(y)), (float)Math.Sin(p));
    }

    /// <summary>cb9 <c>skyRotation</c> = (sin yaw, cos yaw).</summary>
    public Vector2 SkyRotation => new((float)Math.Sin(Yaw * Math.PI / 180.0), (float)Math.Cos(Yaw * Math.PI / 180.0));

    /// <summary>
    /// Preview exposure (pipeline.md §3.3): <c>2^(clamp(log2(P) + 5, evmin, evmax) − 3)</c> with
    /// P = the global probe exposure.
    /// </summary>
    public float ExposureFor(float globalProbeExposure)
    {
        double ev = Math.Log2(globalProbeExposure) + 5.0;
        ev = Math.Clamp(ev, EvMin, EvMax);
        return (float)Math.Pow(2.0, ev - 3.0);
    }

    /// <summary>Reads the SSI fields of a GDT entry (<c>ssi.gdf</c>).</summary>
    public static SunParameters FromGdt(string name, IReadOnlyDictionary<string, string> f)
    {
        float F(string k, float d = 0) =>
            f.TryGetValue(k, out var s) && float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : d;
        var rgb = (f.GetValueOrDefault("colorSRGB") ?? "1 1 1 1").Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => float.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0f).ToArray();
        return new SunParameters
        {
            Name = name,
            Pitch = F("pitch"),
            Yaw = F("yaw"),
            ColorSrgb = new Vector3(rgb.ElementAtOrDefault(0), rgb.ElementAtOrDefault(1), rgb.ElementAtOrDefault(2)),
            Stops = F("stops"),
            SpecCompensation = F("spec_comp"),
            PenumbraInches = F("penumbra_inches"),
            SkyboxModel = f.GetValueOrDefault("skyboxmodel") ?? string.Empty,
            EnableSun = F("enablesun") != 0,
            DynamicShadow = F("dynamicShadow") != 0,
            EvCompensation = F("evcmp"),
            EvMin = F("evmin"),
            EvMax = F("evmax"),
            BounceCount = Math.Clamp((int)F("bounceCount", 1), 1, 10),
        };
    }
}

/// <summary>A BC6H 3D texture of a diffuse probe volume (t46/t47/t48).</summary>
public sealed record ProbeVolumeTexture(int Width, int Height, int Depth, byte[] Data)
{
    public const int DxgiFormat = DxgiFormats.BC6HUf16;

    /// <summary>Bytes per row of 4x4 blocks.</summary>
    public int RowPitch => Math.Max(1, Width / 4) * 16;

    /// <summary>Bytes per depth slice.</summary>
    public int SlicePitch => RowPitch * Math.Max(1, Height / 4);
}

/// <summary>The BC6H reflection cube (t51, bound as a 1-cube TextureCubeArray).</summary>
public sealed class ReflectionCube
{
    public const int DxgiFormat = DxgiFormats.BC6HUf16;

    public required int FaceSize { get; init; }

    /// <summary>[mip][face] BC6H blocks; subresource index = <c>mip + face * MipCount</c>.</summary>
    public required byte[][][] Mips { get; init; }

    public int MipCount => Mips.Length;

    public int RowPitch(int mip) => Math.Max(1, (FaceSize >> mip) / 4) * 16;
}

/// <summary>Everything one preview lighting preset needs, ready for upload.</summary>
public sealed class PreviewLighting
{
    public required PreviewLightState State { get; init; }

    /// <summary>SSI asset name from the LED (<c>default_morning</c>, ...).</summary>
    public required string SsiName { get; init; }

    /// <summary>Sun parameters from the SSI GDT asset (null when the asset was not found).</summary>
    public required SunParameters? Sun { get; init; }

    /// <summary>t40 <c>gSunShadowTree</c> (StructuredBuffer&lt;uint&gt;).</summary>
    public required uint[] SunShadowTree { get; init; }

    /// <summary>cb9 <c>sun.sstLightingConstants.dimensionInTiles</c>.</summary>
    public required Vector2 SunShadowTreeDimensionInTiles { get; init; }

    public required LedSunShadowTree SunShadowTreeSource { get; init; }

    public required LedProbeConfig ProbeConfig { get; init; }

    /// <summary>t46/t47/t48: X, Y, Z ambient-cube volumes.</summary>
    public required IReadOnlyList<ProbeVolumeTexture> ProbeVolumes { get; init; }

    /// <summary>t51 reflection cube.</summary>
    public required ReflectionCube Reflection { get; init; }

    /// <summary>cb9 <c>sun.globalProbeExposure</c>.</summary>
    public required float GlobalProbeExposure { get; init; }

    /// <summary>cb9 <c>sun.avgGlobalProbeColor</c>.</summary>
    public required Vector3 AverageGlobalProbeColor { get; init; }

    /// <summary>cb9 <c>exposure</c> (= <c>exposureClamped</c>); null without SSI data.</summary>
    public float? Exposure => Sun?.ExposureFor(GlobalProbeExposure);

    /// <summary>
    /// cb9 <c>globalProbe.data</c> (float[12] @992; lighting_data.md §2.4): box centre − camera,
    /// <c>1/(extentPos+extentNeg)</c>, <c>(N−1)/T</c>, <c>0.5/T</c> with T = (N.x, N.y, 2N.z).
    /// </summary>
    public float[] GlobalProbeData(Vector3 cameraWorldPosition)
    {
        var c = ProbeConfig;
        var (nx, ny, nz) = c.VoxelDimensions;
        var n = new Vector3(nx, ny, nz);
        var t = new Vector3(nx, ny, 2 * nz);
        var rel = c.Center - cameraWorldPosition;
        var inv = Vector3.One / (c.ExtentPositive + c.ExtentNegative);
        var scale = (n - Vector3.One) / t;
        var bias = new Vector3(0.5f) / t;
        return [rel.X, rel.Y, rel.Z, inv.X, inv.Y, inv.Z, scale.X, scale.Y, scale.Z, bias.X, bias.Y, bias.Z];
    }
}

/// <summary>
/// The four preview lighting presets APE builds from <c>share\raw\maps\mp\assetviewer.led</c> (volume 0,
/// probe 0) plus the SSI assets in <c>source_data\ssi.gdt</c> (lighting_data.md §5).
/// </summary>
public sealed class LightingStates
{
    public required LedFile Led { get; init; }
    public required IReadOnlyList<PreviewLighting> States { get; init; }

    public PreviewLighting this[PreviewLightState state] => States.First(s => s.State == state);

    /// <summary>Path of the LED APE reads.</summary>
    public static string LedPath(ToolsGfxInstall install) =>
        Path.Combine(install.Root, "share", "raw", "maps", "mp", "assetviewer.led");

    /// <summary>Loads the LED and resolves each state's SSI (from <c>source_data\ssi.gdt</c>, then <paramref name="gdt"/>).</summary>
    public static LightingStates Load(ToolsGfxInstall install, IGdtLookup? gdt = null)
    {
        var led = LedFile.Load(LedPath(install));
        if (led.Volumes.Count == 0 || led.Volumes[0].Probes.Count == 0)
            throw new InvalidDataException("assetviewer.led has no volume/probe.");
        var volume = led.Volumes[0];
        var probe = volume.Probes[0];

        Dictionary<string, GdtEntry>? ssiEntries = null;
        var ssiGdt = Path.Combine(install.Root, "source_data", "ssi.gdt");
        if (File.Exists(ssiGdt))
            ssiEntries = GdtFile.Load(ssiGdt).GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);

        var states = new List<PreviewLighting>();
        for (int s = 0; s < 4; s++)
        {
            var ls = volume.LightStates[s];
            var ps = probe.States[s];
            if (ls is null || ps is null)
                continue;

            var entry = ssiEntries?.GetValueOrDefault(ls.Ssi) ?? gdt?.Find(ls.Ssi, "ssi");
            var (nx, ny, nz) = probe.Config.VoxelDimensions;
            states.Add(new PreviewLighting
            {
                State = (PreviewLightState)s,
                SsiName = ls.Ssi,
                Sun = entry is null ? null : SunParameters.FromGdt(ls.Ssi, entry.Fields),
                SunShadowTree = ls.SunShadowTree.Mips.Count > 0 ? ls.SunShadowTree.Mips[0] : [],
                SunShadowTreeDimensionInTiles = ls.SunShadowTree.DimensionInTiles,
                SunShadowTreeSource = ls.SunShadowTree,
                ProbeConfig = probe.Config,
                ProbeVolumes = ps.IrradianceTextures.Select(t => new ProbeVolumeTexture(nx, ny, 2 * nz, t)).ToArray(),
                Reflection = new ReflectionCube { FaceSize = (int)probe.Config.ReflectionFaceSize, Mips = ps.ReflectionMips },
                GlobalProbeExposure = ps.Exposure,
                AverageGlobalProbeColor = ps.AverageCubeColor,
            });
        }
        return new LightingStates { Led = led, States = states };
    }

    /// <summary>
    /// t44 <c>gEnvBRDFGeneric</c>: the cached <c>$env_brdf_generic</c> image
    /// (<c>Texture_dualscalar_uncomp_mipNone\env_brdf_generic.tif_0_*.lz4</c>, R8G8_UNORM 64x64).
    /// </summary>
    public static CachedImage? LoadEnvBrdf(ImageCache cache) =>
        BuiltinImages.Load(cache, "$env_brdf_generic", ImageClass.DualScalar);
}
