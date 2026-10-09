using Apex.Render.Resources;
using Vortice.DXGI;

namespace Apex.Render.Targets;

/// <summary>
/// One entry of ToolsGfx's <c>gRenderTargets[85]</c> (pipeline.md §4.1, <c>Gfx_InitRenderTargetFormatTable</c>
/// 0x1407e9d20), cross-checked against the rigid_morning capture's resource list where the preview creates it.
/// Sizes are relative to the window: divisor n → ceil(W/n) × ceil(H/n), minimum 1.
/// </summary>
public sealed record RenderTargetInfo(
    int Id,
    string Role,
    Format TextureFormat,
    Format RtvFormat,
    Format SrvFormat,
    Format UavFormat,
    int Divisor,
    TextureKind Kind = TextureKind.Texture2D,
    int DepthOrArraySize = 1,
    /// <summary>Size and format seen in the rigid_morning capture (false = from IDA only / role inferred).</summary>
    bool Verified = false)
{
    public (int Width, int Height) SizeFor(int windowWidth, int windowHeight)
        => (Math.Max(1, (windowWidth + Divisor - 1) / Divisor), Math.Max(1, (windowHeight + Divisor - 1) / Divisor));

    public TargetDesc DescFor(int windowWidth, int windowHeight)
    {
        var (w, h) = SizeFor(windowWidth, windowHeight);
        return new TargetDesc(TextureFormat, w, h, RtvFormat, SrvFormat, UavFormat, Format.Unknown, Kind, DepthOrArraySize);
    }
}

/// <summary>Well-known render target ids used by the preview frame graph.</summary>
public static class RenderTargetId
{
    public const int GBufferAlbedo = 0;
    public const int GBufferReflectanceOcclusion = 1;
    public const int GBufferNormalGloss = 2;
    public const int GBufferExtra = 3;
    public const int LightCull = 4;
    public const int ProbeCull = 5;
    public const int DecalCull = 6;
    public const int Ssao = 7;
    public const int HemiAoDepth2x = 8;
    public const int HemiAoDepth4x = 9;
    public const int HemiAoDepth2xAtlas = 12;
    public const int HemiAoDepth4xAtlas = 13;
    public const int HemiAoOcclusion2xInterleaved = 16;
    public const int HemiAoOcclusion4xInterleaved = 17;
    public const int HemiAoOcclusion4x = 21;
    public const int OitHead = 27;
    public const int OitFragments = 28;
    public const int OitAccum = 29;
    public const int TonemapOutput = 30;
    public const int SmaaEdges = 31;
    public const int SmaaBlendWeights = 32;
    public const int Hdr = 33;
    public const int HdrCopy = 34;
    public const int HdrResolved = 35;
    public const int ExposureChainFirst = 36; // 36..43 = /2 … /256
    public const int LensFlare = 55;
    public const int Count = 85;
}

public static class RenderTargetTable
{
    private const Format R11G11B10 = Format.R11G11B10_Float;

    public static IReadOnlyList<RenderTargetInfo> Entries { get; } = Build();

    public static RenderTargetInfo Get(int id) => Entries[id] ?? throw new ArgumentException($"render target {id} is not defined");

    /// <summary>Depth-stencil DS0: D32_FLOAT_S8X24 (texture R32G8X24_TYPELESS, SRV R32_FLOAT_X8X24), window size.</summary>
    public static TargetDesc SceneDepth(int width, int height) => new(Format.R32G8X24_Typeless, width, height,
        SrvFormat: Format.R32_Float_X8X24_Typeless, DsvFormat: Format.D32_Float_S8X24_UInt);

    /// <summary><c>gLightingRenderTargets[3]</c> (R16_TYPELESS / R16_UNORM, RTV+SRV): spot 2048², omni 256² cube, sun cascades 1024² ×3.</summary>
    public static TargetDesc LightingTarget(int index) => index switch
    {
        0 => new(Format.R16_Typeless, 2048, 2048, Format.R16_UNorm, Format.R16_UNorm),
        1 => new(Format.R16_Typeless, 256, 256, Format.R16_UNorm, Format.R16_UNorm, Kind: TextureKind.TextureCube, DepthOrArraySize: 6),
        2 => new(Format.R16_Typeless, 1024, 1024, Format.R16_UNorm, Format.R16_UNorm, Kind: TextureKind.Texture2DArray, DepthOrArraySize: 3),
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    /// <summary>Sun-shadow cascade depth buffer (captured: D16 at 1024²).</summary>
    public static TargetDesc SunShadowDepth => new(Format.R16_Typeless, 1024, 1024, DsvFormat: Format.D16_UNorm);

    private static RenderTargetInfo[] Build()
    {
        var t = new RenderTargetInfo[RenderTargetId.Count];
        void Add(RenderTargetInfo e) => t[e.Id] = e;

        Add(new(0, "gbuffer albedo (t0)", Format.R8G8B8A8_Typeless, Format.R8G8B8A8_UNorm_SRgb, Format.R8G8B8A8_UNorm_SRgb, Format.R32_UInt, 1, Verified: true));
        Add(new(1, "gbuffer reflectance/occlusion (t2)", Format.R10G10B10A2_Typeless, Format.R10G10B10A2_UNorm, Format.R10G10B10A2_UNorm, Format.R10G10B10A2_UNorm, 1, Verified: true));
        Add(new(2, "gbuffer normal/gloss (t1)", Format.R10G10B10A2_Typeless, Format.R10G10B10A2_UNorm, Format.R10G10B10A2_UNorm, Format.R10G10B10A2_UNorm, 1, Verified: true));
        // The "GBuffer Decal" stage copies RT2 into it and reads it as t55 gResolvedNormalTexture (sub_1405669D0).
        Add(new(3, "gbuffer extra / resolved normal (decals)", Format.R10G10B10A2_Typeless, Format.R10G10B10A2_UNorm, Format.R10G10B10A2_UNorm, Format.Unknown, 1));
        for (int i = 4; i <= 6; i++)
            Add(new(i, i switch { 4 => "light cull", 5 => "probe cull", _ => "forward-decal cull" }, Format.R32_UInt, Format.Unknown, Format.R32_UInt, Format.R32_UInt, 64, TextureKind.Texture3D, 256, Verified: true));
        Add(new(7, "SSAO result", Format.R8_UNorm, Format.R8_UNorm, Format.R8_UNorm, Format.R8_UNorm, 2, Verified: true));

        // HemiAO depth pyramid. Id ↔ role verified from RenderDoc resource-creation order (ids advance by 4 for
        // RTV-capable 2D targets, by 3 for UAV-only ones): RT8 DS2x 800×431, RT9 DS4x 400×216, RT12 DS2xAtlas
        // 200×108×16 and RT13 DS4xAtlas 100×54×16 (no RTV) are the ones the preview's HemiAO chain touches.
        // RT10/11 (/8, /16 linear) and RT14/15 (/32, /64 atlases, "HemiAOFragmentData") follow the same pattern (unverified).
        Add(new(8, "HemiAO DS2x", Format.R16_Float, Format.R16_Float, Format.R16_Float, Format.R16_Float, 2, Verified: true));
        Add(new(9, "HemiAO DS4x", Format.R16_Float, Format.R16_Float, Format.R16_Float, Format.R16_Float, 4, Verified: true));
        Add(new(10, "HemiAO DS8x", Format.R16_Float, Format.R16_Float, Format.R16_Float, Format.R16_Float, 8));
        Add(new(11, "HemiAO DS16x", Format.R16_Float, Format.R16_Float, Format.R16_Float, Format.R16_Float, 16));
        Add(new(12, "HemiAO DS2xAtlas", Format.R16_Float, Format.Unknown, Format.R16_Float, Format.R16_Float, 8, TextureKind.Texture2DArray, 16, Verified: true));
        Add(new(13, "HemiAO DS4xAtlas", Format.R16_Float, Format.Unknown, Format.R16_Float, Format.R16_Float, 16, TextureKind.Texture2DArray, 16, Verified: true));
        Add(new(14, "HemiAO DS8xAtlas", Format.R16_Float, Format.Unknown, Format.R16_Float, Format.R16_Float, 32, TextureKind.Texture2DArray, 16));
        Add(new(15, "HemiAO DS16xAtlas (HemiAOFragmentData)", Format.R16_Float, Format.Unknown, Format.R16_Float, Format.R16_Float, 64, TextureKind.Texture2DArray, 16));
        // HemiAO AO pyramid: R8_UNORM /2, /4, /8, /16 cycling. Used: RT16 (/2, interleaved AO of DS2xAtlas),
        // RT17 (/4, interleaved AO of DS4xAtlas), RT21 (/4, AO of DS4x); RT7 is the final AoResult.
        int[] aoDiv = { 2, 4, 8, 16 };
        for (int i = 16; i <= 26; i++)
            Add(new(i, $"HemiAO AO /{aoDiv[(i - 16) % 4]}", Format.R8_UNorm, Format.R8_UNorm, Format.R8_UNorm, Format.R8_UNorm, aoDiv[(i - 16) % 4],
                Verified: i is 16 or 17 or 21));

        Add(new(27, "OIT head", Format.R32_UInt, Format.Unknown, Format.R32_UInt, Format.R32_UInt, 1, Verified: true));
        // RT28 is created by the OIT code, not the table: captured as R32G32_UINT full ×9 slices.
        Add(new(28, "OIT fragments (created by OIT)", Format.R32G32_UInt, Format.Unknown, Format.R32G32_UInt, Format.R32G32_UInt, 1, TextureKind.Texture2DArray, 9, Verified: true));
        Add(new(29, "OIT accumulation", Format.R16G16B16A16_Float, Format.R16G16B16A16_Float, Format.R16G16B16A16_Float, Format.R16G16B16A16_Float, 1, Verified: true));
        Add(new(30, "tonemap output / SMAA input", Format.R8G8B8A8_UNorm, Format.R8G8B8A8_UNorm, Format.R8G8B8A8_UNorm, Format.Unknown, 1, Verified: true));
        Add(new(31, "SMAA edges", Format.R8G8_UNorm, Format.R8G8_UNorm, Format.R8G8_UNorm, Format.Unknown, 1, Verified: true));
        Add(new(32, "SMAA blend weights", Format.R8G8B8A8_UNorm, Format.R8G8B8A8_UNorm, Format.R8G8B8A8_UNorm, Format.Unknown, 1, Verified: true));
        Add(new(33, "HDR lit buffer", R11G11B10, R11G11B10, R11G11B10, R11G11B10, 1, Verified: true));
        Add(new(34, "HDR copy (t49 refraction source)", R11G11B10, R11G11B10, R11G11B10, R11G11B10, 1, Verified: true));
        Add(new(35, "HDR OIT/SSS result", R11G11B10, R11G11B10, R11G11B10, R11G11B10, 1, Verified: true));
        for (int i = 0; i < 8; i++)
            Add(new(36 + i, $"exposure downsample /{2 << i}", R11G11B10, R11G11B10, R11G11B10, Format.Unknown, 2 << i, Verified: true));
        for (int i = 0; i < 8; i++)
            Add(new(44 + i, $"second downsample chain /{2 << i}", R11G11B10, R11G11B10, R11G11B10, Format.Unknown, 2 << i));
        Add(new(52, "underwater", Format.R8_UNorm, Format.R8_UNorm, Format.R8_UNorm, Format.Unknown, 2));
        Add(new(53, "underwater", Format.R10G10B10A2_UNorm, Format.R10G10B10A2_UNorm, Format.R10G10B10A2_UNorm, Format.Unknown, 2));
        Add(new(54, "underwater", Format.R10G10B10A2_UNorm, Format.R10G10B10A2_UNorm, Format.R10G10B10A2_UNorm, Format.Unknown, 2));
        Add(new(55, "lens flare / bloom0", R11G11B10, R11G11B10, R11G11B10, Format.Unknown, 2, Verified: true));
        // Bloom mips: R16G16B16A16_FLOAT / R11G11B10 triples at /4 … /64 (bloom is off in the preview).
        int[] bloomDiv = { 4, 8, 16, 32, 64 };
        for (int i = 56; i <= 70; i++)
        {
            int level = (i - 56) / 3;
            var fmt = (i - 56) % 3 == 0 ? Format.R16G16B16A16_Float : R11G11B10;
            Add(new(i, $"bloom /{bloomDiv[level]}", fmt, fmt, fmt, Format.Unknown, bloomDiv[level]));
        }
        Add(new(71, "volumetrics", Format.R16G16B16A16_Float, Format.R16G16B16A16_Float, Format.R16G16B16A16_Float, Format.R16G16B16A16_Float, 4));
        Add(new(72, "volumetrics", Format.R16G16B16A16_Float, Format.R16G16B16A16_Float, Format.R16G16B16A16_Float, Format.R16G16B16A16_Float, 4));
        Add(new(73, "VolumetricLightVolume", Format.R16G16B16A16_Float, Format.Unknown, Format.R16G16B16A16_Float, Format.R16G16B16A16_Float, 8, TextureKind.Texture3D, 64));
        Add(new(74, "volumetric depth", Format.R32_Float, Format.R32_Float, Format.R32_Float, Format.R32_Float, 4));
        Add(new(75, "low-res gbuffer albedo", Format.R8G8B8A8_Typeless, Format.R8G8B8A8_UNorm_SRgb, Format.R8G8B8A8_UNorm_SRgb, Format.Unknown, 4));
        Add(new(76, "low-res gbuffer reflectance/occlusion", Format.R10G10B10A2_Typeless, Format.R10G10B10A2_UNorm, Format.R10G10B10A2_UNorm, Format.Unknown, 4));
        Add(new(77, "low-res gbuffer normal/gloss", Format.R10G10B10A2_Typeless, Format.R10G10B10A2_UNorm, Format.R10G10B10A2_UNorm, Format.Unknown, 4));
        Add(new(78, "low-res gbuffer extra", Format.R10G10B10A2_Typeless, Format.R10G10B10A2_UNorm, Format.R10G10B10A2_UNorm, Format.Unknown, 4));
        for (int i = 79; i <= 81; i++)
            Add(new(i, "secondary cull", Format.R32_UInt, Format.Unknown, Format.R32_UInt, Format.R32_UInt, 64, TextureKind.Texture3D, 256));
        for (int i = 82; i <= 84; i++)
            Add(new(i, "cull aux", Format.R32G32B32A32_Float, Format.Unknown, Format.R32G32B32A32_Float, Format.R32G32B32A32_Float, 64));
        return t;
    }
}
