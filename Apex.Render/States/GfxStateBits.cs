namespace Apex.Render.States;

/// <summary>
/// ToolsGfx state words as produced by <c>Techset_ParseStateMap</c> (materials.md §4). The material layer
/// (Apex.Render.Data) builds these from techsetdef <c>State</c> elements; <see cref="StateFactory"/> turns
/// them into D3D11 objects exactly like State.cpp.
/// </summary>
public static class GfxStateBits
{
    // ── Enum tables (0x140AF0C60..) ─────────────────────────────────────────
    public enum BlendFactor : uint { None = 0, Zero = 1, One = 2, SrcColor = 3, SrcAlpha = 4, InvSrcAlpha = 5, InvSrc1Alpha = 6, DestAlpha = 7 }
    public enum Compare : uint { Disable = 0, Always = 1, Equal = 2, NotEqual = 3, Less = 4, LessEqual = 5 }
    public enum StencilOp : uint { Disable = 0, Keep = 1, Zero = 2, Replace = 3 }
    public enum Cull : uint { None = 1, Back = 2, Front = 3 }
    public enum PolygonOffset : uint { None = 0, StaticDecal = 1, WeaponImpact = 2, Shadowmap = 3, Tools = 4 }

    // ── Blend word (one per RT; ToolsGfx_CreateBlendState 0x1406064C0) ──────
    // bits 0-3 write mask RGBA, 4-6 SrcBlend, 7-9 DestBlend, 10 BlendOp(Add), 11-13 SrcBlendAlpha,
    // 14-16 DestBlendAlpha, 17 BlendOpAlpha(Add).
    public const uint WriteR = 1, WriteG = 2, WriteB = 4, WriteA = 8, WriteRgb = 7, WriteRgba = 15;

    public static uint Blend(uint writeMask, BlendFactor src = 0, BlendFactor dst = 0, BlendFactor srcAlpha = 0, BlendFactor dstAlpha = 0)
    {
        uint w = writeMask & 0xF;
        if (src != 0 || dst != 0)
            w |= ((uint)src << 4) | ((uint)dst << 7) | (1u << 10);
        if (srcAlpha != 0 || dstAlpha != 0)
            w |= ((uint)srcAlpha << 11) | ((uint)dstAlpha << 14) | (1u << 17);
        return w;
    }

    /// <summary>Blend off, all channels written.</summary>
    public const uint BlendReplace = WriteRgba;

    // ── Depth-stencil word (ToolsGfx_CreateDepthStencilState 0x140607DB0) ───
    // bit 0 depth write, bits 1-3 depth func, bits 4-6 stencil func, bits 7-8 stencil pass op.
    public static uint DepthStencil(bool depthWrite, Compare depthFunc, Compare stencilFunc = Compare.Disable, StencilOp stencilPass = StencilOp.Disable)
        => (depthWrite ? 1u : 0u) | ((uint)depthFunc << 1) | ((uint)stencilFunc << 4) | ((uint)stencilPass << 7);

    /// <summary>No depth test, no write, no stencil (fullscreen passes).</summary>
    public const uint DepthDisabled = 0;

    // ── Sampler word (Material_ResolveSamplerStates / ToolsGfx_CreateSamplerState 0x140607B90) ──
    // bits 0-2 filter (1 nearest, 2 linear, 3-6 aniso 2/4/8/16, 7 comparison), bit 3 mip nearest, bit 4 mip linear,
    // bits 5-6 / 7-8 / 9-10 address U/V/W {wrap, clamp, border, mirror}, bit 11 white border.
    public const uint TileBoth = 0x000, TileVertical = 0x020, TileHorizontal = 0x080, NoTile = 0x2A0;
    public const uint MirrorBoth = 0x1E0, MirrorVertical = 0x060, MirrorHorizontal = 0x180;
    public const uint BorderWhite = 0x800;

    public const uint FilterNomipNearest = 1, FilterNomipBilinear = 2, FilterNearestMipNearest = 9, FilterLinearMipNearest = 10,
        FilterMipStandard2xBilinear = 11, FilterMipExpensive4xBilinear = 12, FilterTrilinear = 18, FilterAniso2xTrilinear = 19,
        FilterAniso4xTrilinear = 20, FilterAniso8x = 21, FilterAniso16x = 22;

    /// <summary>Code samplers created in Config.cpp (s12–s15), matched to the captured descs.</summary>
    public static class CodeSamplers
    {
        /// <summary>s12 <c>gTrilinearClampSampler</c>: all linear, clamp, LOD 0..FLT_MAX.</summary>
        public const uint TrilinearClamp = FilterTrilinear | NoTile;
        /// <summary>s13 <c>gBilinearClampSampler</c>: linear, mip point, clamp.</summary>
        public const uint BilinearClamp = FilterLinearMipNearest | NoTile;
        /// <summary>s14 <c>gPointClampSampler</c>.</summary>
        public const uint PointClamp = FilterNearestMipNearest | NoTile;
        /// <summary>s15 <c>gCmpBilinearClampSampler</c>: comparison (GREATER, reversed Z), linear, mip point, clamp.</summary>
        public const uint CmpBilinearClamp = 7 | 8 | NoTile;
        /// <summary>Post-FX <c>bilinearClamp</c> (tonemap/exposure/SMAA): linear, no mips (MaxLOD 0), U/V clamp, W wrap (as captured).</summary>
        public const uint PostBilinearClamp = FilterNomipBilinear | TileVertical | TileHorizontal;
        /// <summary>Post-FX point clamp, no mips, U/V clamp, W wrap.</summary>
        public const uint PostPointClamp = FilterNomipNearest | TileVertical | TileHorizontal;
    }
}

/// <summary>Rasterizer inputs (ToolsGfx_CreateRasterizerState 0x140607F90).</summary>
public readonly record struct RasterBits(
    GfxStateBits.Cull Cull = GfxStateBits.Cull.Back,
    bool Wireframe = false,
    bool FrontCounterClockwise = false,
    bool DepthClip = true,
    GfxStateBits.PolygonOffset PolygonOffset = GfxStateBits.PolygonOffset.None)
{
    /// <summary>Fullscreen passes: cull none, solid, depth clip on.</summary>
    public static RasterBits Fullscreen => new(GfxStateBits.Cull.None);

    /// <summary>Sun shadow cascades as captured: cull back, depth clip off, shadowmap bias (-4 / -4).</summary>
    public static RasterBits SunShadow => new(GfxStateBits.Cull.Back, DepthClip: false, PolygonOffset: GfxStateBits.PolygonOffset.Shadowmap);
}
