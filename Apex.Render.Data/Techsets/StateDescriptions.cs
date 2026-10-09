namespace Apex.Render.Data.Techsets;

// Plain descriptions of the pipeline state a techsetdef State element compiles to (materials.md §4).
// Enum values equal the corresponding D3D11 enum values so the GPU side can cast directly.

/// <summary><c>D3D11_BLEND</c> values.</summary>
public enum BlendFactor
{
    Zero = 1,
    One = 2,
    SrcColor = 3,
    InvSrcColor = 4,
    SrcAlpha = 5,
    InvSrcAlpha = 6,
    DestAlpha = 7,
    InvDestAlpha = 8,
    DestColor = 9,
    InvDestColor = 10,
    Src1Alpha = 18,
    InvSrc1Alpha = 19,
}

/// <summary><c>D3D11_BLEND_OP</c> values (ToolsGfx only ever uses Add).</summary>
public enum BlendOperation
{
    Add = 1,
    Subtract = 2,
    ReverseSubtract = 3,
    Min = 4,
    Max = 5,
}

/// <summary><c>D3D11_COLOR_WRITE_ENABLE</c> bits.</summary>
[Flags]
public enum ColorWriteMask : byte
{
    None = 0,
    Red = 1,
    Green = 2,
    Blue = 4,
    Alpha = 8,
    All = 15,
}

/// <summary><c>D3D11_COMPARISON_FUNC</c> values.</summary>
public enum ComparisonFunction
{
    Never = 1,
    Less = 2,
    Equal = 3,
    LessEqual = 4,
    Greater = 5,
    NotEqual = 6,
    GreaterEqual = 7,
    Always = 8,
}

/// <summary><c>D3D11_STENCIL_OP</c> values.</summary>
public enum StencilOperation
{
    Keep = 1,
    Zero = 2,
    Replace = 3,
    IncrementSaturate = 4,
    DecrementSaturate = 5,
    Invert = 6,
    Increment = 7,
    Decrement = 8,
}

/// <summary><c>D3D11_CULL_MODE</c> values.</summary>
public enum CullMode
{
    None = 1,
    Front = 2,
    Back = 3,
}

/// <summary><c>D3D11_FILL_MODE</c> values.</summary>
public enum FillMode
{
    Wireframe = 2,
    Solid = 3,
}

/// <summary>Techsetdef <c>polygonOffset</c> names (depth bias presets).</summary>
public enum PolygonOffset
{
    None = 0,
    StaticDecal = 1,
    WeaponImpact = 2,
    Shadowmap = 3,
    Tools = 4,
}

/// <summary>One render target's blend (a <c>D3D11_RENDER_TARGET_BLEND_DESC</c>).</summary>
public sealed record RenderTargetBlend(
    bool BlendEnable,
    BlendFactor SrcBlend,
    BlendFactor DestBlend,
    BlendOperation BlendOp,
    BlendFactor SrcBlendAlpha,
    BlendFactor DestBlendAlpha,
    BlendOperation BlendOpAlpha,
    ColorWriteMask WriteMask)
{
    /// <summary>Blending off, all channels written.</summary>
    public static RenderTargetBlend Opaque { get; } = new(false, BlendFactor.One, BlendFactor.Zero, BlendOperation.Add,
        BlendFactor.One, BlendFactor.Zero, BlendOperation.Add, ColorWriteMask.All);
}

/// <summary>A <c>D3D11_BLEND_DESC</c>: always 8 render targets.</summary>
public sealed record BlendDescription(bool AlphaToCoverage, bool IndependentBlend, IReadOnlyList<RenderTargetBlend> RenderTargets);

/// <summary>One stencil face (<c>D3D11_DEPTH_STENCILOP_DESC</c>).</summary>
public sealed record StencilFaceDescription(
    StencilOperation FailOp,
    StencilOperation DepthFailOp,
    StencilOperation PassOp,
    ComparisonFunction Function);

/// <summary>A <c>D3D11_DEPTH_STENCIL_DESC</c>. Depth functions are already mapped to reversed Z.</summary>
public sealed record DepthStencilDescription(
    bool DepthEnable,
    bool DepthWrite,
    ComparisonFunction DepthFunction,
    bool StencilEnable,
    byte StencilReadMask,
    byte StencilWriteMask,
    StencilFaceDescription FrontFace,
    StencilFaceDescription BackFace);

/// <summary>A <c>D3D11_RASTERIZER_DESC</c>.</summary>
public sealed record RasterizerDescription(
    FillMode FillMode,
    CullMode CullMode,
    bool FrontCounterClockwise,
    int DepthBias,
    float DepthBiasClamp,
    float SlopeScaledDepthBias,
    bool DepthClipEnable,
    bool ScissorEnable,
    bool MultisampleEnable,
    bool AntialiasedLineEnable);

/// <summary>A compiled techsetdef <c>State</c> element.</summary>
/// <param name="Name">State name as referenced by the technique (inline states get the technique's name).</param>
/// <param name="Blend">Blend state for RT0..7.</param>
/// <param name="DepthStencil">Depth/stencil state (reversed Z).</param>
/// <param name="Rasterizer">Rasterizer state (front face clockwise, scissor on, as APE creates it).</param>
/// <param name="StencilRef">Stencil reference value (techsetdef <c>stencilRef</c>, default 255).</param>
/// <param name="PolygonOffset">The depth-bias preset the rasterizer bias came from.</param>
public sealed record StateDescription(
    string Name,
    BlendDescription Blend,
    DepthStencilDescription DepthStencil,
    RasterizerDescription Rasterizer,
    int StencilRef,
    PolygonOffset PolygonOffset)
{
    /// <summary>True when any render target blends (APE's opaque/transparent validation uses this).</summary>
    public bool IsBlending => Blend.RenderTargets.Any(r => r.BlendEnable);
}

/// <summary>
/// Compiles State/BlendState/StencilState elements to descriptions following APE's GFXS bit path
/// (<c>Techset_ParseStateMap</c> 0x140468240 then State.cpp creators).
/// </summary>
internal static class StateCompiler
{
    public static StateDescription Compile(TechsetUnit unit, string name, TsFields state, List<string> warnings)
    {
        // Blend: "blendState" applies to every RT, "blendStateN" overrides RT N.
        var rts = new RenderTargetBlend[8];
        var common = state.First("blendState") is { } bs ? CompileBlend(unit, bs, warnings) : RenderTargetBlend.Opaque;
        for (int i = 0; i < 8; i++)
            rts[i] = state.First($"blendState{i}") is { } bi ? CompileBlend(unit, bi, warnings) : common;
        bool independent = rts.Skip(1).Any(r => r != rts[0]);
        var blend = new BlendDescription(false, independent, rts);

        // Depth: DepthEnable = depthWrite || depthTest != disable; functions mapped to reversed Z.
        var depthTest = ParseCompare(state.Text("depthTest") ?? "disable", warnings);
        bool depthWrite = ParseBool(state.Text("depthWrite"));
        bool depthEnable = depthWrite || depthTest != TechsetCompare.Disable;

        var front = state.First("stencilFront") is { } sf ? CompileStencil(unit, sf, warnings) : null;
        var back = state.First("stencilBack") is { } sb ? CompileStencil(unit, sb, warnings) : null;
        // APE's D3D path uses one stencil word for both faces (back face = front face).
        var stencil = front ?? back;
        bool stencilEnable = stencil is not null;
        var face = stencil ?? new StencilFaceDescription(StencilOperation.Keep, StencilOperation.Keep, StencilOperation.Keep, ComparisonFunction.Always);
        var depthStencil = new DepthStencilDescription(
            depthEnable, depthWrite, ToReversedZ(depthTest), stencilEnable, 0xFF, 0xFF, face, face);

        // Rasterizer.
        var cull = (state.Text("cull") ?? "back").ToLowerInvariant() switch
        {
            "none" => CullMode.None,
            "front" => CullMode.Front,
            "back" => CullMode.Back,
            var other => Warn(CullMode.Back, $"state '{name}': unknown cull '{other}'"),
        };
        var offset = ParsePolygonOffset(state.Text("polygonOffset"), warnings);
        var (bias, slope, clamp) = DepthBias(offset);
        var raster = new RasterizerDescription(
            ParseBool(state.Text("wireframe")) ? FillMode.Wireframe : FillMode.Solid,
            cull, false, bias, clamp, slope, true, true, false, false);

        int stencilRef = 255;
        if (state.Text("stencilRef") is { } sr && int.TryParse(sr, out var parsed))
            stencilRef = parsed;

        return new StateDescription(name, blend, depthStencil, raster, stencilRef, offset);

        T Warn<T>(T value, string message)
        {
            warnings.Add(message);
            return value;
        }
    }

    /// <summary>Depth bias for a polygonOffset preset (<c>ToolsGfx_PolygonOffsetToDepthBias</c> 0x140251760).</summary>
    public static (int Bias, float Slope, float Clamp) DepthBias(PolygonOffset offset) => offset switch
    {
        PolygonOffset.StaticDecal => (250, 2.0f, 1e-6f),
        PolygonOffset.WeaponImpact => (500, 4.0f, 2e-6f),
        PolygonOffset.Shadowmap => (-4, -4.0f, -0.1f),
        PolygonOffset.Tools => (-250, -2.0f, -1e-6f),
        _ => (0, 0f, 0f),
    };

    private enum TechsetCompare
    {
        Disable,
        Always,
        Equal,
        NotEqual,
        Less,
        LessEqual,
    }

    /// <summary>ToolsGfx renders with reversed Z: Less -> Greater, LessEqual -> GreaterEqual, Disable/Always -> Always.</summary>
    private static ComparisonFunction ToReversedZ(TechsetCompare c) => c switch
    {
        TechsetCompare.Equal => ComparisonFunction.Equal,
        TechsetCompare.NotEqual => ComparisonFunction.NotEqual,
        TechsetCompare.Less => ComparisonFunction.Greater,
        TechsetCompare.LessEqual => ComparisonFunction.GreaterEqual,
        _ => ComparisonFunction.Always,
    };

    private static TechsetCompare ParseCompare(string s, List<string> warnings) => s.ToLowerInvariant() switch
    {
        "disable" or "" => TechsetCompare.Disable,
        "always" => TechsetCompare.Always,
        "equal" => TechsetCompare.Equal,
        "notequal" => TechsetCompare.NotEqual,
        "less" => TechsetCompare.Less,
        "lessequal" => TechsetCompare.LessEqual,
        _ => WarnCompare(s, warnings),
    };

    private static TechsetCompare WarnCompare(string s, List<string> warnings)
    {
        warnings.Add($"unknown compare function '{s}'");
        return TechsetCompare.Disable;
    }

    private static bool ParseBool(string? s) =>
        s is not null && (s.Equals("true", StringComparison.OrdinalIgnoreCase) || s == "1");

    private static PolygonOffset ParsePolygonOffset(string? s, List<string> warnings) => (s ?? "").ToLowerInvariant() switch
    {
        "" or "0" or "none" => PolygonOffset.None,
        "static decal" => PolygonOffset.StaticDecal,
        "weapon impact" => PolygonOffset.WeaponImpact,
        "shadowmap" => PolygonOffset.Shadowmap,
        "tools" => PolygonOffset.Tools,
        _ => WarnOffset(s!, warnings),
    };

    private static PolygonOffset WarnOffset(string s, List<string> warnings)
    {
        warnings.Add($"unknown polygonOffset '{s}'");
        return PolygonOffset.None;
    }

    private static TsFields? ResolveRef(TechsetUnit unit, TsValue v, string type, List<string> warnings)
    {
        if (v.IsText)
        {
            var f = unit.ResolveNamed(type, v.Text);
            if (f is null) warnings.Add($"{type} '{v.Text}' not found");
            return f;
        }
        if (v.Kind == ValueKind.Call)
            return unit.ResolveInline(type, v);
        return null;
    }

    private static RenderTargetBlend CompileBlend(TechsetUnit unit, TsValue v, List<string> warnings)
    {
        var f = ResolveRef(unit, v, "BlendState", warnings);
        if (f is null)
            return RenderTargetBlend.Opaque;

        var mask = ColorWriteMask.None;
        foreach (var ch in (f.Text("writeChannels") ?? "rgba").ToLowerInvariant())
        {
            mask |= ch switch
            {
                'r' => ColorWriteMask.Red,
                'g' => ColorWriteMask.Green,
                'b' => ColorWriteMask.Blue,
                'a' => ColorWriteMask.Alpha,
                _ => ColorWriteMask.None,
            };
        }

        var color = Half(f, "color", warnings);
        var alpha = Half(f, "alpha", warnings);
        bool enable = color is not null || alpha is not null;
        // A disabled half becomes ONE/ZERO/ADD (ToolsGfx_CreateBlendState 0x1406064C0).
        var (cs, cd) = color ?? (BlendFactor.One, BlendFactor.Zero);
        var (asrc, adst) = alpha ?? (BlendFactor.One, BlendFactor.Zero);
        return new RenderTargetBlend(enable, cs, cd, BlendOperation.Add, asrc, adst, BlendOperation.Add, mask);
    }

    private static (BlendFactor Src, BlendFactor Dst)? Half(TsFields f, string prefix, List<string> warnings)
    {
        var func = (f.Text(prefix + "BlendFunc") ?? "disable").ToLowerInvariant();
        if (func == "disable")
            return null;
        if (func != "add")
            warnings.Add($"unsupported blend func '{func}' (ToolsGfx only has Add)");
        return (Factor(f.Text(prefix + "BlendSrc"), BlendFactor.One, warnings),
                Factor(f.Text(prefix + "BlendDst"), BlendFactor.Zero, warnings));
    }

    private static BlendFactor Factor(string? s, BlendFactor fallback, List<string> warnings) => (s ?? "").ToLowerInvariant() switch
    {
        "" => fallback,
        "zero" => BlendFactor.Zero,
        "one" => BlendFactor.One,
        "srccolor" => BlendFactor.SrcColor,
        "srcalpha" => BlendFactor.SrcAlpha,
        "invsrcalpha" => BlendFactor.InvSrcAlpha,
        "invsrc1alpha" => BlendFactor.InvSrc1Alpha,
        "destalpha" => BlendFactor.DestAlpha,
        // Not in APE's factor table (used by "invmultiply"); mapped to the obvious D3D factor.
        "invsrccolor" => BlendFactor.InvSrcColor,
        _ => WarnFactor(s!, fallback, warnings),
    };

    private static BlendFactor WarnFactor(string s, BlendFactor fallback, List<string> warnings)
    {
        warnings.Add($"unknown blend factor '{s}'");
        return fallback;
    }

    private static StencilFaceDescription? CompileStencil(TechsetUnit unit, TsValue v, List<string> warnings)
    {
        var f = ResolveRef(unit, v, "StencilState", warnings);
        if (f is null || !ParseBool(f.Text("enable")))
            return null;
        var func = ParseCompare(f.Text("func") ?? "disable", warnings);
        if (func == TechsetCompare.Disable)
            return null;
        // Only the pass op is carried in the GFXS word ({KEEP, KEEP, ZERO, REPLACE}); fail/zfail are KEEP.
        var pass = (f.Text("opPass") ?? "keep").ToLowerInvariant() switch
        {
            "zero" => StencilOperation.Zero,
            "replace" => StencilOperation.Replace,
            _ => StencilOperation.Keep,
        };
        return new StencilFaceDescription(StencilOperation.Keep, StencilOperation.Keep, pass, ToReversedZ(func));
    }
}
