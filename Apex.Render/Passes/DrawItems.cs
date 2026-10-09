using Apex.Render.Constants;
using Apex.Render.Shaders;
using Apex.Render.States;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Apex.Render.Passes;

/// <summary>The material techniques the preview frame draws scene items with (ToolsGfx technique slots).</summary>
public enum PreviewTechnique
{
    /// <summary>"build shadowmap depth" — sun shadow cascades.</summary>
    ShadowDepth,
    /// <summary>"gbuffer" — RENDER_STAGE_LIT_DEFERRED_OPAQUE.</summary>
    Gbuffer,
    /// <summary>Emissive (incl. RENDER_STAGE_EMISSIVE_SKY) — forward into the HDR buffer.</summary>
    Emissive,
    /// <summary>"lit" — lit forward opaque.</summary>
    Lit,
    /// <summary>"oit" — RENDER_STAGE_TRANSPARENT through the OIT fragment lists.</summary>
    Oit,
    /// <summary>"depth prepass" of a subsurface-scattering material — RENDER_STAGE_LIT_FORWARD_SSS_OPAQUE before the
    /// G-buffer (render class 4 is in the prepass lists).</summary>
    SssPrepass,
    /// <summary>"depth prepass" of a lit-forward opaque material (render class 3) — the Prepass stage writes its depth
    /// into DS0 before the G-buffer, because the "forward opaque" state itself does not write depth.</summary>
    LitPrepass,
    /// <summary>"lit" of a subsurface-scattering material (renderFlags isSubSurfaceScattering, render class 4) —
    /// RENDER_STAGE_LIT_FORWARD_SSS_OPAQUE after deferred lighting, into the lit buffer, the SSS diffuse target and
    /// the G-buffer albedo, followed by the SSS blur.</summary>
    Sss,
    /// <summary>"gbuffer" of a deferred decal (renderFlags isDecal + isGbuffer, render class 6) —
    /// RENDER_STAGE_LIT_DEFERRED_DECAL ("GBuffer Decal"), blended over the finished opaque G-buffer.</summary>
    GbufferDecal,
    /// <summary>"lit" of a forward decal (renderFlags isDecal without isGbuffer, render class 7) —
    /// RENDER_STAGE_LIT_FORWARD_DECAL, into the lit buffer after deferred lighting.</summary>
    LitDecal,
}

/// <summary>GPU mesh buffers of one draw: the 44-byte generic vertex stream (slot 0) and an index buffer.</summary>
public sealed class MeshGeometry
{
    public required ID3D11Buffer VertexBuffer { get; init; }
    public int VertexStride { get; init; } = GenericVertex.Stride;
    public int VertexOffset { get; init; }
    public required ID3D11Buffer IndexBuffer { get; init; }
    public Format IndexFormat { get; init; } = Format.R16_UInt;
    public int IndexOffset { get; init; }
    public VertexDeclType Decl { get; init; } = VertexDeclType.Generic;
}

/// <summary>Indexed range of <see cref="MeshGeometry"/> (DrawIndexedInstanced arguments).</summary>
public readonly record struct DrawRange(int IndexCount, int StartIndex = 0, int BaseVertex = 0);

/// <summary>Material render state, either as ToolsGfx state words (<see cref="GfxStateBits"/>) or as prebuilt
/// D3D11 objects (techsetdef <c>State</c> compiled by the data layer); null members fall back to the pass defaults
/// captured from APE. Objects win over words.</summary>
public sealed record MaterialState(uint[]? Blend = null, uint? DepthStencil = null, RasterBits? Raster = null)
{
    public ID3D11BlendState? BlendObject { get; init; }
    public ID3D11DepthStencilState? DepthStencilObject { get; init; }
    public ID3D11RasterizerState? RasterizerObject { get; init; }
    public byte? StencilRef { get; init; }
}

/// <summary>
/// One technique of a material, ready to draw: the cached VS/PS variants plus the material's per-stage
/// <c>$Globals</c>, textures and samplers keyed by the names the shaders reflect. Code resources (b9–b11, t18–t20,
/// t30 …) are added by the pass.
/// </summary>
public sealed class MaterialPass
{
    public required ShaderProgram VertexShader { get; init; }
    public ShaderProgram? PixelShader { get; init; }
    /// <summary>Per-stage <c>$Globals</c> constant buffers (they share the name but not the layout).</summary>
    public IReadOnlyDictionary<ShaderStage, ID3D11Buffer> StageGlobals { get; init; } = new Dictionary<ShaderStage, ID3D11Buffer>();
    public IReadOnlyDictionary<string, ID3D11ShaderResourceView> Textures { get; init; } = new Dictionary<string, ID3D11ShaderResourceView>();
    public IReadOnlyDictionary<string, ID3D11SamplerState> Samplers { get; init; } = new Dictionary<string, ID3D11SamplerState>();
    public MaterialState? State { get; init; }

    private ShaderBindings? _bindings;

    /// <summary>By-name bindings of this pass's programs (material resources already assigned).</summary>
    public ShaderBindings Bindings
    {
        get
        {
            if (_bindings != null)
                return _bindings;
            var b = PixelShader != null ? new ShaderBindings(VertexShader, PixelShader) : new ShaderBindings(VertexShader);
            foreach (var (stage, cb) in StageGlobals)
                b.TrySetConstantBuffer(stage, "$Globals", cb);
            foreach (var (name, srv) in Textures)
                b.TrySetResource(name, srv);
            foreach (var (name, s) in Samplers)
                b.TrySetSampler(name, s);
            return _bindings = b;
        }
    }
}

/// <summary>
/// A plain scene draw the preview frame renders — what the data layer (or a capture replay) feeds in.
/// </summary>
public interface IPreviewDrawItem
{
    MeshGeometry Mesh { get; }
    DrawRange Range { get; }
    /// <summary>Per-object constants (world matrix, flags, sortDepth) — uploaded into t30 at this draw's instance index.</summary>
    CodeObjectConsts ObjectConsts { get; }
    /// <summary>b11 skinning matrices (<see cref="CodeObjectBonesConsts"/> bytes); empty = zeros (rigid / bind pose).</summary>
    ReadOnlyMemory<byte> Bones { get; }
    /// <summary>The material technique for a preview stage, or null when the material does not draw there.</summary>
    MaterialPass? GetPass(PreviewTechnique technique);

    /// <summary>Sun-shadow range for a cascade when it differs from <see cref="Range"/>: APE draws the model's shadow
    /// LODs there (the barrel: a 904-triangle LOD in cascade 0, a 32-triangle one in cascades 1–2).</summary>
    DrawRange? ShadowRange(int cascade) => null;

    /// <summary>Bit i set = drawn into sun-shadow cascade i (lets separate per-LOD shadow items share a model).</summary>
    int ShadowCascadeMask => 0b111;
}

/// <summary>Straightforward <see cref="IPreviewDrawItem"/>.</summary>
public sealed class PreviewDrawItem : IPreviewDrawItem
{
    public required MeshGeometry Mesh { get; init; }
    public required DrawRange Range { get; init; }
    public CodeObjectConsts ObjectConsts { get; init; } = CodeObjectConsts.Identity;
    public ReadOnlyMemory<byte> Bones { get; init; }
    public Dictionary<PreviewTechnique, MaterialPass> Passes { get; } = new();
    /// <summary>Per-cascade shadow ranges (index = cascade); missing entries use <see cref="Range"/>.</summary>
    public DrawRange?[]? ShadowRanges { get; init; }
    public int ShadowCascadeMask { get; init; } = 0b111;

    public MaterialPass? GetPass(PreviewTechnique technique) => Passes.GetValueOrDefault(technique);

    public DrawRange? ShadowRange(int cascade) => ShadowRanges is { } r && cascade < r.Length ? r[cascade] : null;
}
