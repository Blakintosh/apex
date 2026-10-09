using System.Runtime.InteropServices;
using Apex.Render.Device;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Apex.Render.Shaders;

/// <summary>ToolsGfx vertex declarations (asset_caches.md §5.1, <c>gVertDeclElems</c>).</summary>
public enum VertexDeclType
{
    Generic = 0,
    ParticleSprite = 1,
    ParticleCloud = 2,
}

/// <summary>44-byte <c>GfxGenericVertex</c> (stream 0) — every mesh and ToolsGfx's fullscreen quad use it.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 44)]
public struct GenericVertex
{
    public float PositionX, PositionY, PositionZ; // POSITION     R32G32B32_FLOAT
    public uint Color;                              // COLOR        R8G8B8A8_UNORM
    public float TexCoordU, TexCoordV;              // TEXCOORD0    R32G32_FLOAT
    public uint Normal;                             // NORMAL       R8G8B8A8_SNORM
    public uint Tangent;                            // TANGENT      R8G8B8A8_SNORM (w = bitangent sign)
    public ushort BlendIndex0, BlendIndex1, BlendIndex2, BlendIndex3; // BLENDINDICES R16G16B16A16_UINT
    public uint BlendWeights;                       // BLENDWEIGHT  R8G8B8A8_UNORM

    public const int Stride = 44;
}

/// <summary>
/// Builds input layouts per (declaration, VS input signature) like <c>Gfx_VertexDecl_CreateInputLayout</c>:
/// only the elements the VS actually reads are declared; buffer strides stay fixed (44 B stream 0,
/// 4 B per-instance <c>INSTANCEID</c> stream 1).
/// </summary>
public sealed class InputLayoutCache : IDisposable
{
    private readonly record struct Element(int Slot, int Offset, Format Format, bool PerInstance);

    private static readonly Dictionary<(string, int), Element> s_generic = new()
    {
        [("POSITION", 0)] = new(0, 0, Format.R32G32B32_Float, false),
        [("COLOR", 0)] = new(0, 12, Format.R8G8B8A8_UNorm, false),
        [("TEXCOORD", 0)] = new(0, 16, Format.R32G32_Float, false),
        [("NORMAL", 0)] = new(0, 24, Format.R8G8B8A8_SNorm, false),
        [("TANGENT", 0)] = new(0, 28, Format.R8G8B8A8_SNorm, false),
        [("BLENDINDICES", 0)] = new(0, 32, Format.R16G16B16A16_UInt, false),
        [("BLENDWEIGHT", 0)] = new(0, 40, Format.R8G8B8A8_UNorm, false),
        [("INSTANCEID", 0)] = new(1, 0, Format.R32_UInt, true),
    };

    // particle_sprite: all per-instance, slot 0, stride 60.
    private static readonly Dictionary<(string, int), Element> s_particleSprite = new()
    {
        [("POSITION", 0)] = new(0, 0, Format.R32G32B32_Float, true),
        [("COLOR", 0)] = new(0, 12, Format.R8G8B8A8_UNorm, true),
        [("NORMAL", 0)] = new(0, 16, Format.R8G8B8A8_SNorm, true),
        [("TANGENT", 0)] = new(0, 20, Format.R8G8B8A8_SNorm, true),
        [("TEXCOORD", 0)] = new(0, 24, Format.R32G32B32_UInt, true),
        [("TEXCOORD", 1)] = new(0, 36, Format.R32G32B32A32_Float, true),
        [("TEXCOORD", 2)] = new(0, 52, Format.R16G16B16A16_UInt, true),
    };

    private static readonly Dictionary<(string, int), Element> s_particleCloud = new()
    {
        [("POSITION", 0)] = new(0, 0, Format.R16G16B16A16_SNorm, true),
    };

    public static int StreamStride(VertexDeclType decl, int slot) => (decl, slot) switch
    {
        (VertexDeclType.Generic, 0) => GenericVertex.Stride,
        (VertexDeclType.Generic, 1) => 4,
        (VertexDeclType.ParticleSprite, 0) => 60,
        (VertexDeclType.ParticleCloud, 0) => 8,
        _ => 0,
    };

    private readonly GfxDevice _gfx;
    private readonly Dictionary<(VertexDeclType, string), ID3D11InputLayout> _cache = new();
    /// <summary>Per program object, so the per-draw lookup does not hash the SHA-1 string.</summary>
    private readonly Dictionary<(VertexDeclType, ShaderProgram), ID3D11InputLayout> _byProgram = new();

    public InputLayoutCache(GfxDevice gfx) => _gfx = gfx;

    public ID3D11InputLayout Get(VertexDeclType decl, ShaderProgram vertexShader)
    {
        if (_byProgram.TryGetValue((decl, vertexShader), out var layout))
            return layout;
        layout = GetBySignature(decl, vertexShader);
        _byProgram[(decl, vertexShader)] = layout;
        return layout;
    }

    private ID3D11InputLayout GetBySignature(VertexDeclType decl, ShaderProgram vertexShader)
    {
        if (vertexShader.Stage != ShaderStage.Vertex)
            throw new ArgumentException($"{vertexShader.Name} is not a vertex shader");
        var key = (decl, vertexShader.Sha1);
        if (_cache.TryGetValue(key, out var layout))
            return layout;

        var table = decl switch
        {
            VertexDeclType.Generic => s_generic,
            VertexDeclType.ParticleSprite => s_particleSprite,
            _ => s_particleCloud,
        };
        var elements = new List<InputElementDescription>();
        foreach (var input in vertexShader.Reflection.Inputs)
        {
            if (input.SystemValue != SystemValueType.Undefined)
                continue; // SV_VertexID / SV_InstanceID are generated, not fetched
            if (!table.TryGetValue((input.SemanticName.ToUpperInvariant(), input.SemanticIndex), out var e))
                throw new InvalidDataException($"Vertex shader is trying to access '{input.SemanticName}{input.SemanticIndex}' which is not part of the vert decl '{decl}'");
            elements.Add(new InputElementDescription(input.SemanticName, (uint)input.SemanticIndex, e.Format, (uint)e.Offset, (uint)e.Slot,
                e.PerInstance ? InputClassification.PerInstanceData : InputClassification.PerVertexData, e.PerInstance ? 1u : 0u));
        }
        elements.Sort((a, b) => a.Slot != b.Slot ? a.Slot.CompareTo(b.Slot) : a.AlignedByteOffset.CompareTo(b.AlignedByteOffset));
        layout = _gfx.Device.CreateInputLayout(elements.ToArray(), vertexShader.Bytecode.Span);
        _cache[key] = layout;
        return layout;
    }

    public void Dispose()
    {
        foreach (var l in _cache.Values)
            l.Dispose();
        _cache.Clear();
        _byProgram.Clear();
    }
}
