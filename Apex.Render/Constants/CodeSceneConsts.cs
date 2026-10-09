using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Apex.Render.Constants;

// Engine ("code") constant buffers, byte-for-byte as reflected from every cached variant
// (gfxcache/docs/code_buffers.md). Matrices are HLSL row_major (shaders compiled with
// D3DCOMPILE_PACK_MATRIX_ROW_MAJOR), so System.Numerics.Matrix4x4 memory maps straight onto them.

[InlineArray(3)]
public struct Vector4Array3 { private Vector4 _e0; }

[InlineArray(2)]
public struct Vector4Array2 { private Vector4 _e0; }

[InlineArray(12)]
public struct UIntArray12 { private uint _e0; }

/// <summary><c>CodeSceneTransforms</c> (512 B). "off" space = camera-relative world (world − camPos).</summary>
[StructLayout(LayoutKind.Explicit, Size = 512)]
public struct CodeSceneTransforms
{
    [FieldOffset(0)] public Matrix4x4 WldToCam;
    [FieldOffset(64)] public Matrix4x4 CamToOff;
    [FieldOffset(128)] public Matrix4x4 OffToCam;
    [FieldOffset(192)] public Matrix4x4 CamToClp;
    [FieldOffset(256)] public Matrix4x4 CamToWld;
    [FieldOffset(320)] public Matrix4x4 WldToClp;
    [FieldOffset(384)] public Matrix4x4 OffToClp;
    [FieldOffset(448)] public Matrix4x4 ClpToCam;
}

/// <summary><c>CoreFogConstants</c> (224 B). The preview only sets the two colours; the rest is stale memory in
/// APE (captures show NaN/1e±30), so no preview shader may depend on it.</summary>
[StructLayout(LayoutKind.Explicit, Size = 224)]
public struct CoreFogConstants
{
    [FieldOffset(0)] public Vector4 FogColor;
    [FieldOffset(16)] public Vector4 SunFogColor;
    [FieldOffset(32)] public float K0;
    [FieldOffset(36)] public float SkyK0;
    [FieldOffset(40)] public float ExpMul;
    [FieldOffset(44)] public float ExpAdd;
    [FieldOffset(48)] public float HeightFalloff;
    [FieldOffset(52)] public float SkyHeightFalloff;
    [FieldOffset(56)] public float K0b;
    [FieldOffset(64)] public float SkyK0b;
    [FieldOffset(68)] public Vector3 WldSunFogDir;
    [FieldOffset(80)] public Vector2 SunFogAngles;
    [FieldOffset(88)] public float AtmosphereSunStrength;
    [FieldOffset(92)] public float AtmosphereMieSchlickK;
    [FieldOffset(96)] public Vector2 AtmosphereSkyFogDensityAtCamera;
    [FieldOffset(104)] public float AtmosphereExtinctionIntensity;
    [FieldOffset(108)] public float AtmosphereInScatterIntensity;
    [FieldOffset(112)] public Vector3 AtmosphereRayleighDensity;
    [FieldOffset(124)] public float AtmosphereHazeBaseDist;
    [FieldOffset(128)] public Vector3 AtmosphereMieDensity;
    [FieldOffset(140)] public float AtmosphereHazeFadeDist;
    [FieldOffset(144)] public Vector3 AtmosphereTotalDensity;
    [FieldOffset(156)] public float WorldFogSkySize;
    [FieldOffset(160)] public Vector3 AtmosphereInScatterIntensityOverTotalDensity;
    [FieldOffset(172)] public float BlendAmount;
    [FieldOffset(176)] public Vector2 AtmosphereSkyFogHeightDensityScale;
    [FieldOffset(184)] public Vector2 AtmosphereFogDistanceOffset;
    [FieldOffset(192)] public Vector2 AtmosphereFogDistanceDensityScale;
    [FieldOffset(200)] public Vector2 AtmosphereFogHeightDensityScale;
    [FieldOffset(208)] public Vector2 AtmosphereFogDensityAtCamera;
}

/// <summary><c>SSTLightingConstants</c> (96 B): sun-shadow-tree / outdoor-occlusion lookup constants.</summary>
[StructLayout(LayoutKind.Explicit, Size = 96)]
public struct SstLightingConstants
{
    [FieldOffset(0)] public Vector2 DimensionInTiles;
    [FieldOffset(8)] public float InchesPerTexel;
    [FieldOffset(12)] public float SpanInInches;
    [FieldOffset(16)] public Matrix4x4 OffToPinTransform;
    [FieldOffset(80)] public float CoordScale;
    [FieldOffset(84)] public uint RootOffset;
}

/// <summary><c>CoreSunConstants</c> (256 B, cb9 @736).</summary>
[StructLayout(LayoutKind.Explicit, Size = 256)]
public struct CoreSunConstants
{
    /// <summary>Unit vector pointing <em>to</em> the sun.</summary>
    [FieldOffset(0)] public Vector3 WldDir;
    [FieldOffset(12)] public float SplitDepthOffset;
    [FieldOffset(16)] public Vector3 Color;
    [FieldOffset(28)] public float SpecScale;
    [FieldOffset(32)] public float GlobalProbeExposure;
    [FieldOffset(36)] public Vector3 AvgGlobalProbeColor;
    [FieldOffset(48)] public Vector4Array3 SplitPinTransform;
    [FieldOffset(96)] public uint SunCookieIndex;
    [FieldOffset(100)] public float SunCookieIntensity;
    [FieldOffset(104)] public float SunVolumetricCookieIntensity;
    [FieldOffset(108)] public uint ToolsGfxDisableSunShadow;
    [FieldOffset(112)] public Vector4Array2 SunCookieTransform;
    [FieldOffset(144)] public float Intensity;
    [FieldOffset(148)] public int SplitArrayOffset;
    [FieldOffset(152)] public Vector2 Pad0;
    [FieldOffset(160)] public SstLightingConstants SstLightingConstants;
}

/// <summary><c>CoreGlobalProbePack</c>: uint4[3]. f32[0..2] = global probe origin relative to the camera.</summary>
[StructLayout(LayoutKind.Explicit, Size = 48)]
public struct CoreGlobalProbePack
{
    [FieldOffset(0)] public UIntArray12 Data;
}

[StructLayout(LayoutKind.Explicit, Size = 80)]
public struct CoreWeatherConsts
{
    [FieldOffset(0)] public float Rain;
    [FieldOffset(4)] public float WindSpeed;
    [FieldOffset(8)] public float WindDirSin;
    [FieldOffset(12)] public float WindDirCos;
    [FieldOffset(16)] public float WeatherTile;
    [FieldOffset(20)] public Vector3 WeatherVector;
    [FieldOffset(32)] public Vector3 WeatherVector2;
    [FieldOffset(48)] public Vector3 WeatherTint;
    [FieldOffset(64)] public Vector3 WeatherTint2;
}

[StructLayout(LayoutKind.Explicit, Size = 32)]
public struct TriDensitySettings
{
    [FieldOffset(0)] public uint Flags0, Flags1, Flags2, Flags3;
    [FieldOffset(16)] public Vector4 Params;
}

[StructLayout(LayoutKind.Explicit, Size = 96)]
public struct ShaderDebugConstants
{
    [FieldOffset(0)] public Vector4 DebugTunable0;
    [FieldOffset(16)] public Vector4 DebugTunable1;
    [FieldOffset(32)] public Vector4 DebugTunable2;
    [FieldOffset(48)] public Vector4 DebugTunable3;
    [FieldOffset(64)] public Vector4 DebugSliders;
    [FieldOffset(80)] public uint DebugToggle0, DebugToggle1, DebugToggle2, DebugToggle3;
}

/// <summary><c>CodeSceneConsts</c> — cbuffer <c>CodeSceneConstBuffer</c> at b9, 1552 bytes.</summary>
[StructLayout(LayoutKind.Explicit, Size = Size)]
public struct CodeSceneConsts
{
    public const int Size = 1552;

    [FieldOffset(0)] public CodeSceneTransforms Transforms;
    [FieldOffset(512)] public CoreFogConstants Fog;
    [FieldOffset(736)] public CoreSunConstants Sun;
    [FieldOffset(992)] public CoreGlobalProbePack GlobalProbe;
    [FieldOffset(1040)] public CoreWeatherConsts Weather;
    [FieldOffset(1120)] public TriDensitySettings TriDensity;
    [FieldOffset(1152)] public SstLightingConstants OutdoorOcclusionTreeConstants;
    [FieldOffset(1248)] public Vector4 OutdoorPinToWorldZ;
    [FieldOffset(1264)] public ShaderDebugConstants ShaderDebugConstants;
    [FieldOffset(1360)] public Vector3 WldCameraPosition;
    [FieldOffset(1376)] public Vector4 ViewSpaceScaleBias;
    [FieldOffset(1392)] public uint RenderTargetWidth;
    [FieldOffset(1396)] public uint RenderTargetHeight;
    [FieldOffset(1400)] public Vector2 RenderTargetInvSize;
    [FieldOffset(1408)] public float NearClip;
    [FieldOffset(1412)] public float FarClipScale;
    [FieldOffset(1416)] public float Time;
    [FieldOffset(1420)] public float SiegeTime;
    [FieldOffset(1424)] public float Exposure;
    [FieldOffset(1428)] public float InvExposure;
    [FieldOffset(1432)] public float ExposureClamped;
    [FieldOffset(1440)] public uint NumLights;
    [FieldOffset(1444)] public uint NumProbes;
    [FieldOffset(1448)] public uint NumShadowedLights;
    [FieldOffset(1452)] public uint NumOverrideProbes;
    [FieldOffset(1456)] public uint LightingMode;
    [FieldOffset(1460)] public uint NumLitFogVolumes;
    [FieldOffset(1464)] public uint NumComputeSprites;
    [FieldOffset(1472)] public Vector2 SpotShadowResolutionAndRcp;
    [FieldOffset(1480)] public uint ComputeSpritesDebug;
    [FieldOffset(1484)] public uint NumComputeLmaps;
    [FieldOffset(1488)] public Vector2 SkyRotation;
    [FieldOffset(1496)] public float SkySize;
    [FieldOffset(1500)] public float SkyTransition;
    [FieldOffset(1504)] public uint ForceViewToNormal;
    [FieldOffset(1508)] public uint NumForwardDecals;
    [FieldOffset(1512)] public uint NumAttenuationVolumes;
    [FieldOffset(1516)] public uint FullHdr;
    [FieldOffset(1520)] public Vector4 ExtraClipPlane0;
    [FieldOffset(1536)] public Vector4 ExtraClipPlane1;
}

/// <summary><c>lightingMode</c> values.</summary>
public static class LightingMode
{
    public const uint Unlit = 0;
    /// <summary>Full deferred lighting (preview "Lighting" on).</summary>
    public const uint Real = 3;
}
