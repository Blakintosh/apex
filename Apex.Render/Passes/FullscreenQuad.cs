using Apex.Render.Device;
using Apex.Render.Resources;
using Apex.Render.Shaders;
using Apex.Render.States;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Apex.Render.Passes;

/// <summary>
/// ToolsGfx draws fullscreen passes as a 6-index quad through the generic 44-byte vertex (positions already in
/// clip space, the post VS passes them through) with the instance stream bound at slot 1. State as captured:
/// blend off (RGBA), depth/stencil off, cull none, scissor on, viewport = target size, depth range 0–1.
/// </summary>
public sealed class FullscreenQuad : IDisposable
{
    private readonly GpuBuffer _vertices;
    private readonly GpuBuffer _indices;
    private readonly GpuBuffer _instances;
    private readonly ID3D11Buffer[] _streams;
    private static readonly uint[] s_strides = [GenericVertex.Stride, 4];
    private static readonly uint[] s_offsets = [0, 0];

    public FullscreenQuad(GfxDevice gfx)
    {
        Span<GenericVertex> v = stackalloc GenericVertex[4];
        // Vertex order, winding and diagonal exactly as APE's quad (RenderDoc: VB 176 B, IB 3,0,2, 2,0,1).
        v[0] = Vertex(-1, -1, 0, 1);
        v[1] = Vertex(1, -1, 1, 1);
        v[2] = Vertex(1, 1, 1, 0);
        v[3] = Vertex(-1, 1, 0, 0);
        _vertices = GpuBuffer.CreateVertex<GenericVertex>(gfx, v, "fullscreen quad VB");
        _indices = GpuBuffer.CreateIndex(gfx, stackalloc ushort[] { 3, 0, 2, 2, 0, 1 }, "fullscreen quad IB");
        // Stream 1 (per-instance INSTANCEID) = 0, 1, 2, … as in APE: a draw with StartInstanceLocation k reads id k,
        // which indexes gObjectInstanceData (t30).
        var ids = new uint[256];
        for (uint i = 0; i < ids.Length; i++)
            ids[i] = i;
        _instances = GpuBuffer.CreateVertex<uint>(gfx, ids, "instance ids");
        _streams = [_vertices.Buffer, _instances.Buffer];
    }

    /// <summary>The shared per-instance id stream (vertex slot 1, 4-byte stride).</summary>
    public ID3D11Buffer InstanceIds => _instances.Buffer;

    private static GenericVertex Vertex(float x, float y, float u, float v) => new()
    {
        PositionX = x, PositionY = y, PositionZ = 0f, Color = 0xFFFFFFFF, TexCoordU = u, TexCoordV = v,
        Normal = 0x007F0000, Tangent = 0x7F7F0000,
    };

    /// <summary>Binds the quad's vertex/index buffers and the input layout for <paramref name="vertexShader"/>.</summary>
    public void BindGeometry(ID3D11DeviceContext ctx, InputLayoutCache layouts, ShaderProgram vertexShader)
    {
        ctx.IASetInputLayout(layouts.Get(VertexDeclType.Generic, vertexShader));
        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        ctx.IASetVertexBuffers(0, 2, _streams, s_strides, s_offsets);
        ctx.IASetIndexBuffer(_indices.Buffer, Format.R16_UInt, 0);
    }

    /// <summary>
    /// Draws one fullscreen pass into <paramref name="rtvs"/> (sized <paramref name="width"/>×<paramref name="height"/>)
    /// with the captured fullscreen state, then unbinds the pipeline's resources.
    /// </summary>
    public void Draw(GfxDevice gfx, StateFactory states, InputLayoutCache layouts, ShaderBindings pipeline,
        ReadOnlySpan<ID3D11RenderTargetView> rtvs, int width, int height, ID3D11DepthStencilView? dsv = null,
        uint blendWord = GfxStateBits.BlendReplace, uint depthStencilWord = GfxStateBits.DepthDisabled, byte stencilRef = 255)
    {
        var ctx = gfx.Context;
        var vs = pipeline.Get(ShaderStage.Vertex) ?? throw new InvalidOperationException("fullscreen pipeline has no vertex shader");
        BindGeometry(ctx, layouts, vs);
        ctx.RSSetState(states.GetRasterizer(RasterBits.Fullscreen));
        ctx.RSSetViewport(new Viewport(0, 0, width, height, 0f, 1f));
        ctx.RSSetScissorRect(0, 0, 4096, 4096);
        ctx.OMSetBlendState(states.GetBlend(blendWord), new Color4(1, 1, 1, 1), uint.MaxValue);
        ctx.OMSetDepthStencilState(states.GetDepthStencil(depthStencilWord), stencilRef);
        ctx.OMSetRenderTargets(rtvs, dsv);
        pipeline.Apply(ctx);
        ctx.DrawIndexed(6, 0, 0);
        pipeline.Unbind(ctx);
        ctx.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>(), null);
    }

    public void Dispose()
    {
        _vertices.Dispose();
        _indices.Dispose();
        _instances.Dispose();
    }
}

/// <summary>Compute dispatch helper: apply by-name bindings, dispatch, unbind (so outputs can be read next).</summary>
public static class ComputePass
{
    public static void Dispatch(ID3D11DeviceContext ctx, ShaderBindings pipeline, int groupsX, int groupsY, int groupsZ = 1)
    {
        pipeline.Apply(ctx);
        ctx.Dispatch((uint)groupsX, (uint)groupsY, (uint)groupsZ);
        pipeline.Unbind(ctx);
    }

    /// <summary>Groups needed to cover <paramref name="width"/>×<paramref name="height"/> with the shader's thread group
    /// (ToolsGfx: DL = ceil(W/8)×ceil(H/8), light culling = ceil(W/64)×ceil(H/64)).</summary>
    public static (int X, int Y) GroupsFor(ShaderProgram cs, int width, int height)
    {
        var (tx, ty, _) = cs.Reflection.ThreadGroupSize;
        return ((width + tx - 1) / tx, (height + ty - 1) / ty);
    }
}
