using Apex.Render.Device;
using Vortice.Direct3D11;
using Vortice.Mathematics;
using static Apex.Render.States.GfxStateBits;

namespace Apex.Render.States;

/// <summary>
/// Converts ToolsGfx state words into cached D3D11 state objects, mirroring State.cpp. ToolsGfx renders
/// with reversed Z (depth cleared to 0): its "less"/"lessEqual" become GREATER/GREATER_EQUAL here and every
/// comparison sampler uses GREATER.
/// </summary>
public sealed class StateFactory : IDisposable
{
    private readonly GfxDevice _gfx;
    private readonly Dictionary<BlendKey, ID3D11BlendState> _blend = new();
    private readonly Dictionary<uint, ID3D11DepthStencilState> _depth = new();
    private readonly Dictionary<RasterBits, ID3D11RasterizerState> _raster = new();
    private readonly Dictionary<uint, ID3D11SamplerState> _samplers = new();

    public StateFactory(GfxDevice gfx) => _gfx = gfx;

    // ── Blend ───────────────────────────────────────────────────────────────

    private readonly record struct BlendKey(uint Rt0, uint Rt1, uint Rt2, uint Rt3, uint Rt4, uint Rt5, uint Rt6, uint Rt7, int Count);

    /// <summary>Blend state for up to 8 per-RT words (blendState0..7). IndependentBlendEnable = MRT count &gt; 1.</summary>
    public ID3D11BlendState GetBlend(ReadOnlySpan<uint> perTarget)
    {
        if (perTarget.Length is 0 or > 8)
            throw new ArgumentException("1..8 blend words expected");
        Span<uint> w = stackalloc uint[8];
        perTarget.CopyTo(w);
        var key = new BlendKey(w[0], w[1], w[2], w[3], w[4], w[5], w[6], w[7], perTarget.Length);
        if (_blend.TryGetValue(key, out var state))
            return state;

        var desc = new BlendDescription { AlphaToCoverageEnable = false, IndependentBlendEnable = perTarget.Length > 1 };
        for (int i = 0; i < 8; i++)
            desc.RenderTarget[i] = ToTargetBlend(i < perTarget.Length ? w[i] : (perTarget.Length > 1 ? 0 : w[0]));
        state = _gfx.Device.CreateBlendState(desc);
        _blend[key] = state;
        return state;
    }

    public ID3D11BlendState GetBlend(uint word) => GetBlend(stackalloc uint[] { word });

    public static RenderTargetBlendDescription ToTargetBlend(uint word)
    {
        uint src = (word >> 4) & 7, dst = (word >> 7) & 7, srcA = (word >> 11) & 7, dstA = (word >> 14) & 7;
        bool enable = (word & 0x3FFF0) != 0; // any of bits 4..17
        // A zero colour or alpha half becomes ONE / ZERO / ADD.
        if (src == 0 && dst == 0) { src = (uint)BlendFactor.One; dst = (uint)BlendFactor.Zero; }
        if (srcA == 0 && dstA == 0) { srcA = (uint)BlendFactor.One; dstA = (uint)BlendFactor.Zero; }
        return new RenderTargetBlendDescription
        {
            BlendEnable = enable,
            SourceBlend = MapBlend(src),
            DestinationBlend = MapBlend(dst),
            BlendOperation = BlendOperation.Add,
            SourceBlendAlpha = MapBlend(srcA),
            DestinationBlendAlpha = MapBlend(dstA),
            BlendOperationAlpha = BlendOperation.Add,
            RenderTargetWriteMask = (ColorWriteEnable)(word & 0xF),
        };
    }

    private static Vortice.Direct3D11.Blend MapBlend(uint f) => f switch
    {
        1 => Vortice.Direct3D11.Blend.Zero,
        2 => Vortice.Direct3D11.Blend.One,
        3 => Vortice.Direct3D11.Blend.SourceColor,
        4 => Vortice.Direct3D11.Blend.SourceAlpha,
        5 => Vortice.Direct3D11.Blend.InverseSourceAlpha,
        6 => Vortice.Direct3D11.Blend.InverseSource1Alpha,
        7 => Vortice.Direct3D11.Blend.DestinationAlpha,
        _ => Vortice.Direct3D11.Blend.One,
    };

    // ── Depth-stencil ───────────────────────────────────────────────────────

    public ID3D11DepthStencilState GetDepthStencil(uint word)
    {
        if (_depth.TryGetValue(word, out var state))
            return state;
        state = _gfx.Device.CreateDepthStencilState(ToDepthStencil(word));
        _depth[word] = state;
        return state;
    }

    public static DepthStencilDescription ToDepthStencil(uint word)
    {
        uint depthFunc = (word >> 1) & 7, stencilFunc = (word >> 4) & 7, pass = (word >> 7) & 3;
        var face = new DepthStencilOperationDescription
        {
            StencilFailOp = StencilOperation.Keep,
            StencilDepthFailOp = StencilOperation.Keep,
            StencilPassOp = pass switch { 2 => StencilOperation.Zero, 3 => StencilOperation.Replace, _ => StencilOperation.Keep },
            StencilFunc = MapCompare(stencilFunc),
        };
        return new DepthStencilDescription
        {
            DepthEnable = (word & 0xF) != 0,
            DepthWriteMask = (word & 1) != 0 ? DepthWriteMask.All : DepthWriteMask.Zero,
            DepthFunc = MapCompare(depthFunc),
            StencilEnable = stencilFunc != 0,
            StencilReadMask = 0xFF,
            StencilWriteMask = 0xFF,
            FrontFace = face,
            BackFace = face,
        };
    }

    /// <summary>Reversed-Z compare map: Less → GREATER, LessEqual → GREATER_EQUAL.</summary>
    public static ComparisonFunction MapCompare(uint c) => c switch
    {
        2 => ComparisonFunction.Equal,
        3 => ComparisonFunction.NotEqual,
        4 => ComparisonFunction.Greater,
        5 => ComparisonFunction.GreaterEqual,
        _ => ComparisonFunction.Always,
    };

    // ── Rasterizer ──────────────────────────────────────────────────────────

    public ID3D11RasterizerState GetRasterizer(RasterBits bits)
    {
        if (_raster.TryGetValue(bits, out var state))
            return state;
        state = _gfx.Device.CreateRasterizerState(ToRasterizer(bits));
        _raster[bits] = state;
        return state;
    }

    public static RasterizerDescription ToRasterizer(RasterBits bits)
    {
        var (bias, slope, clamp) = DepthBias(bits.PolygonOffset);
        return new RasterizerDescription
        {
            FillMode = bits.Wireframe ? FillMode.Wireframe : FillMode.Solid,
            CullMode = bits.Cull switch { Cull.None => CullMode.None, Cull.Front => CullMode.Front, _ => CullMode.Back },
            FrontCounterClockwise = bits.FrontCounterClockwise,
            DepthBias = bias,
            SlopeScaledDepthBias = slope,
            DepthBiasClamp = clamp,
            DepthClipEnable = bits.DepthClip,
            ScissorEnable = true,
            MultisampleEnable = false,
            AntialiasedLineEnable = false,
        };
    }

    /// <summary>ToolsGfx_PolygonOffsetToDepthBias (0x140251760).</summary>
    public static (int Bias, float Slope, float Clamp) DepthBias(PolygonOffset offset) => offset switch
    {
        PolygonOffset.StaticDecal => (250, 2.0f, 1e-6f),
        PolygonOffset.WeaponImpact => (500, 4.0f, 2e-6f),
        PolygonOffset.Shadowmap => (-4, -4.0f, -0.1f),
        PolygonOffset.Tools => (-250, -2.0f, -1e-6f),
        _ => (0, 0f, 0f),
    };

    // ── Samplers ────────────────────────────────────────────────────────────

    /// <summary>Global MipLODBias dvar (0x1416AB8C0); 0 until shown otherwise.</summary>
    public float MipLodBias { get; init; }

    public ID3D11SamplerState GetSampler(uint bits)
    {
        if (_samplers.TryGetValue(bits, out var state))
            return state;
        state = _gfx.Device.CreateSamplerState(ToSampler(bits, MipLodBias));
        _samplers[bits] = state;
        return state;
    }

    public static SamplerDescription ToSampler(uint bits, float mipLodBias = 0f)
    {
        uint f = bits & 7;
        bool mipLinear = (bits & 0x10) != 0;
        bool mipAny = (bits & 0x18) != 0;
        return new SamplerDescription
        {
            Filter = FilterFor(f, mipLinear),
            AddressU = MapAddress((bits >> 5) & 3),
            AddressV = MapAddress((bits >> 7) & 3),
            AddressW = MapAddress((bits >> 9) & 3),
            MipLODBias = mipLodBias,
            MaxAnisotropy = f switch { 3 => 2u, 4 => 4u, 5 => 8u, 6 => 16u, _ => 1u },
            // Reversed Z: comparison samplers test GREATER. Captures show NEVER in every non-comparison desc.
            ComparisonFunc = f == 7 ? ComparisonFunction.Greater : ComparisonFunction.Never,
            BorderColor = (bits & 0x800) != 0 ? new Color4(1, 1, 1, 1) : new Color4(0, 0, 0, 0),
            MinLOD = 0,
            MaxLOD = mipAny ? float.MaxValue : 0,
        };
    }

    /// <summary>Filter table T[2f + mipLinear] from State.cpp.</summary>
    private static Filter FilterFor(uint f, bool mipLinear) => f switch
    {
        0 or 1 => mipLinear ? Filter.MinMagPointMipLinear : Filter.MinMagMipPoint,
        2 => mipLinear ? Filter.MinMagMipLinear : Filter.MinMagLinearMipPoint,
        3 or 4 or 5 or 6 => mipLinear ? Filter.Anisotropic : Filter.MinMagLinearMipPoint,
        _ => mipLinear ? Filter.ComparisonMinMagMipLinear : Filter.ComparisonMinMagLinearMipPoint,
    };

    private static TextureAddressMode MapAddress(uint a) => a switch
    {
        1 => TextureAddressMode.Clamp,
        2 => TextureAddressMode.Border,
        3 => TextureAddressMode.Mirror,
        _ => TextureAddressMode.Wrap,
    };

    public void Dispose()
    {
        foreach (var s in _blend.Values) s.Dispose();
        foreach (var s in _depth.Values) s.Dispose();
        foreach (var s in _raster.Values) s.Dispose();
        foreach (var s in _samplers.Values) s.Dispose();
        _blend.Clear();
        _depth.Clear();
        _raster.Clear();
        _samplers.Clear();
    }
}
