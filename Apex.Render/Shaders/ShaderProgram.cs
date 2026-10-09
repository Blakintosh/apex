using System.Security.Cryptography;
using Apex.Render.Device;
using Vortice.Direct3D11;

namespace Apex.Render.Shaders;

/// <summary>
/// One compiled stage created from raw DXBC (Treyarch's cached bytecode, loaded at runtime from the user's
/// install — never shipped) plus its reflection. The renderer never compiles HLSL for engine passes.
/// </summary>
public sealed class ShaderProgram : IDisposable
{
    public string Name { get; }
    public ShaderStage Stage => Reflection.Stage;
    public ReadOnlyMemory<byte> Bytecode { get; }
    public ShaderReflectionInfo Reflection { get; }
    /// <summary>SHA-1 of the DXBC, lower-case hex (same hash RenderDoc captures record).</summary>
    public string Sha1 { get; }
    public ID3D11DeviceChild Shader { get; }

    public ID3D11VertexShader VertexShader => Shader as ID3D11VertexShader ?? throw WrongStage(ShaderStage.Vertex);
    public ID3D11PixelShader PixelShader => Shader as ID3D11PixelShader ?? throw WrongStage(ShaderStage.Pixel);
    public ID3D11ComputeShader ComputeShader => Shader as ID3D11ComputeShader ?? throw WrongStage(ShaderStage.Compute);
    public ID3D11GeometryShader GeometryShader => Shader as ID3D11GeometryShader ?? throw WrongStage(ShaderStage.Geometry);
    public ID3D11HullShader HullShader => Shader as ID3D11HullShader ?? throw WrongStage(ShaderStage.Hull);
    public ID3D11DomainShader DomainShader => Shader as ID3D11DomainShader ?? throw WrongStage(ShaderStage.Domain);

    private ShaderProgram(string name, ReadOnlyMemory<byte> bytecode, ShaderReflectionInfo reflection, string sha1, ID3D11DeviceChild shader)
    {
        Name = name;
        Bytecode = bytecode;
        Reflection = reflection;
        Sha1 = sha1;
        Shader = shader;
    }

    /// <summary>Creates the stage object from DXBC. The stage comes from the bytecode's version token;
    /// pass <paramref name="expected"/> to assert it.</summary>
    public static ShaderProgram Create(GfxDevice gfx, ReadOnlyMemory<byte> dxbc, string? name = null, ShaderStage? expected = null)
        => Create(gfx, dxbc, Convert.ToHexStringLower(SHA1.HashData(dxbc.Span)), name, expected);

    /// <summary><see cref="Create(GfxDevice, ReadOnlyMemory{byte}, string?, ShaderStage?)"/> with the bytecode's SHA-1
    /// (lower-case hex) already computed.</summary>
    internal static ShaderProgram Create(GfxDevice gfx, ReadOnlyMemory<byte> dxbc, string sha1, string? name, ShaderStage? expected)
    {
        var span = dxbc.Span;
        if (span.Length < 32 || span[0] != (byte)'D' || span[1] != (byte)'X' || span[2] != (byte)'B' || span[3] != (byte)'C')
            throw new InvalidDataException($"{name ?? "shader"}: not a DXBC container");

        var reflection = ShaderReflectionInfo.Reflect(span);
        if (expected is { } e && e != reflection.Stage)
            throw new InvalidDataException($"{name ?? "shader"}: expected a {e} shader, bytecode is {reflection.Stage}");

        var bytes = span.ToArray();
        ID3D11DeviceChild shader = reflection.Stage switch
        {
            ShaderStage.Vertex => gfx.Device.CreateVertexShader(bytes),
            ShaderStage.Pixel => gfx.Device.CreatePixelShader(bytes),
            ShaderStage.Compute => gfx.Device.CreateComputeShader(bytes),
            ShaderStage.Geometry => gfx.Device.CreateGeometryShader(bytes),
            ShaderStage.Hull => gfx.Device.CreateHullShader(bytes),
            ShaderStage.Domain => gfx.Device.CreateDomainShader(bytes),
            _ => throw new NotSupportedException(),
        };
        var label = name ?? $"{reflection.Profile}_{sha1[..12]}";
        shader.DebugName = label;
        return new ShaderProgram(label, bytes, reflection, sha1, shader);
    }

    private InvalidOperationException WrongStage(ShaderStage wanted) => new($"{Name} is a {Stage} shader, not {wanted}");

    public void Dispose() => Shader.Dispose();
}
