using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11.Shader;

namespace Apex.Render.Shaders;

public enum ShaderStage
{
    Vertex,
    Hull,
    Domain,
    Geometry,
    Pixel,
    Compute,
}

/// <summary>Which D3D11 binding table a reflected resource lives in.</summary>
public enum BindingTable
{
    ConstantBuffer,
    ShaderResource,
    UnorderedAccess,
    Sampler,
}

/// <summary>One bound resource from D3DReflect (<c>D3D11_SHADER_INPUT_BIND_DESC</c>).</summary>
public sealed record ResourceBinding(
    string Name,
    ShaderInputType Type,
    int BindPoint,
    int BindCount,
    ShaderResourceViewDimension Dimension,
    int NumSamplesOrStride,
    ShaderInputFlags Flags)
{
    public BindingTable Table => Type switch
    {
        ShaderInputType.ConstantBuffer => BindingTable.ConstantBuffer,
        ShaderInputType.Sampler => BindingTable.Sampler,
        ShaderInputType.UnorderedAccessViewRWTyped or ShaderInputType.UnorderedAccessViewRWStructured
            or ShaderInputType.UnorderedAccessViewRWByteAddress or ShaderInputType.UnorderedAccessViewAppendStructured
            or ShaderInputType.UnorderedAccessViewConsumeStructured or ShaderInputType.UnorderedAccessViewRWStructuredWithCounter
            => BindingTable.UnorderedAccess,
        _ => BindingTable.ShaderResource,
    };

    public bool IsComparisonSampler => Type == ShaderInputType.Sampler && (Flags & ShaderInputFlags.ComparisonSampler) != 0;
}

/// <summary>One element of an input/output signature.</summary>
public sealed record SignatureElement(string SemanticName, int SemanticIndex, int Register, SystemValueType SystemValue,
    RegisterComponentType ComponentType, byte Mask, byte ReadWriteMask);

/// <summary>
/// Managed snapshot of a DXBC blob's reflection: everything the renderer binds by name.
/// ToolsGfx binds materials and code resources purely by reflected name (materials.md §3); slots differ
/// between variants, so nothing here may be hard-coded per shader.
/// </summary>
public sealed class ShaderReflectionInfo
{
    public required ShaderStage Stage { get; init; }
    public required int ShaderModelMajor { get; init; }
    public required int ShaderModelMinor { get; init; }
    public required string Creator { get; init; }
    public required int InstructionCount { get; init; }
    public (int X, int Y, int Z) ThreadGroupSize { get; init; }
    public required IReadOnlyList<ResourceBinding> Resources { get; init; }
    public required IReadOnlyList<CBufferLayout> ConstantBuffers { get; init; }
    public required IReadOnlyList<SignatureElement> Inputs { get; init; }
    public required IReadOnlyList<SignatureElement> Outputs { get; init; }

    public ResourceBinding? FindResource(string name)
    {
        foreach (var r in Resources)
            if (r.Name == name)
                return r;
        return null;
    }

    public CBufferLayout? FindConstantBuffer(string name)
    {
        foreach (var c in ConstantBuffers)
            if (c.Name == name)
                return c;
        return null;
    }

    public string Profile => Stage switch
    {
        ShaderStage.Vertex => "vs",
        ShaderStage.Hull => "hs",
        ShaderStage.Domain => "ds",
        ShaderStage.Geometry => "gs",
        ShaderStage.Pixel => "ps",
        _ => "cs",
    } + $"_{ShaderModelMajor}_{ShaderModelMinor}";

    /// <summary>Stage encoded in a DXBC version token (D3D11_SHVER_GET_TYPE).</summary>
    public static ShaderStage StageFromVersion(uint version) => (version >> 16) switch
    {
        0 => ShaderStage.Pixel,
        1 => ShaderStage.Vertex,
        2 => ShaderStage.Geometry,
        3 => ShaderStage.Hull,
        4 => ShaderStage.Domain,
        5 => ShaderStage.Compute,
        var t => throw new InvalidDataException($"unknown shader program type {t}"),
    };

    public static ShaderReflectionInfo Reflect(ReadOnlySpan<byte> dxbc)
    {
        using var reflection = Compiler.Reflect<ID3D11ShaderReflection>(dxbc);
        var desc = reflection.Description;
        var stage = StageFromVersion(desc.Version);

        var resources = new List<ResourceBinding>((int)desc.BoundResources);
        for (uint i = 0; i < desc.BoundResources; i++)
        {
            var b = reflection.GetResourceBindingDescription(i);
            resources.Add(new ResourceBinding(b.Name, b.Type, (int)b.BindPoint, (int)b.BindCount, b.Dimension, (int)b.NumSamples, b.Flags));
        }

        var buffers = new List<CBufferLayout>((int)desc.ConstantBuffers);
        for (uint i = 0; i < desc.ConstantBuffers; i++)
        {
            var cb = reflection.GetConstantBufferByIndex(i);
            buffers.Add(CBufferLayout.FromReflection(cb, resources));
        }

        var inputs = new List<SignatureElement>((int)desc.InputParameters);
        for (uint i = 0; i < desc.InputParameters; i++)
            inputs.Add(ToElement(reflection.GetInputParameterDescription(i)));
        var outputs = new List<SignatureElement>((int)desc.OutputParameters);
        for (uint i = 0; i < desc.OutputParameters; i++)
            outputs.Add(ToElement(reflection.GetOutputParameterDescription(i)));

        (int, int, int) threads = default;
        if (stage == ShaderStage.Compute)
        {
            reflection.GetThreadGroupSize(out uint x, out uint y, out uint z);
            threads = ((int)x, (int)y, (int)z);
        }

        return new ShaderReflectionInfo
        {
            Stage = stage,
            ShaderModelMajor = (int)((desc.Version >> 4) & 0xF),
            ShaderModelMinor = (int)(desc.Version & 0xF),
            Creator = desc.Creator ?? "",
            InstructionCount = (int)desc.InstructionCount,
            ThreadGroupSize = threads,
            Resources = resources,
            ConstantBuffers = buffers,
            Inputs = inputs,
            Outputs = outputs,
        };
    }

    private static SignatureElement ToElement(ShaderParameterDescription p)
        => new(p.SemanticName, (int)p.SemanticIndex, (int)p.Register, p.SystemValueType, p.ComponentType,
            (byte)p.UsageMask, (byte)p.ReadWriteMask);
}
