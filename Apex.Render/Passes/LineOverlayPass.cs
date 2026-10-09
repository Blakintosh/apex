using System.Numerics;
using System.Runtime.InteropServices;
using Apex.Render.Device;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Apex.Render.Passes;

/// <summary>
/// Draws model-space line segments and joint markers over a finished frame (after SMAA, straight into the output):
/// points are projected on the CPU with the view's <c>wldToClp</c>, segments clipped at the near plane and expanded
/// into screen-space quads, so the pass needs only a pass-through shader. Not part of APE's frame — a preview aid
/// (skeletons without geometry, joint display).
/// </summary>
public sealed class LineOverlayPass : IDisposable
{
    private const string Source = """
        struct V { float4 pos : POSITION; float4 col : COLOR; };
        struct P { float4 pos : SV_Position; float4 col : COLOR; };
        P vs(V v) { P p; p.pos = v.pos; p.col = v.col; return p; }
        float4 ps(P p) : SV_Target { return p.col; }
        """;

    [StructLayout(LayoutKind.Sequential)]
    private struct Vertex
    {
        public Vector4 Position;
        public Vector4 Color;
    }

    private readonly GfxDevice _gfx;
    private readonly ID3D11VertexShader _vs;
    private readonly ID3D11PixelShader _ps;
    private readonly ID3D11InputLayout _layout;
    private readonly ID3D11BlendState _blend;
    private readonly ID3D11DepthStencilState _depth;
    private readonly ID3D11RasterizerState _raster;
    private ID3D11Buffer? _vb;
    private int _capacity;
    private readonly List<Vertex> _verts = new();

    public LineOverlayPass(GfxDevice gfx)
    {
        _gfx = gfx;
        var dev = gfx.Device;
        var vsCode = Compiler.Compile(Source, "vs", "overlay", "vs_5_0");
        var psCode = Compiler.Compile(Source, "ps", "overlay", "ps_5_0");
        _vs = dev.CreateVertexShader(vsCode.Span);
        _ps = dev.CreatePixelShader(psCode.Span);
        _layout = dev.CreateInputLayout(new[]
        {
            new InputElementDescription("POSITION", 0, Format.R32G32B32A32_Float, 0, 0),
            new InputElementDescription("COLOR", 0, Format.R32G32B32A32_Float, 16, 0),
        }, vsCode.Span);
        var blend = new BlendDescription();
        blend.RenderTarget[0] = new RenderTargetBlendDescription
        {
            BlendEnable = true,
            SourceBlend = Blend.SourceAlpha,
            DestinationBlend = Blend.InverseSourceAlpha,
            BlendOperation = BlendOperation.Add,
            SourceBlendAlpha = Blend.Zero,
            DestinationBlendAlpha = Blend.One,
            BlendOperationAlpha = BlendOperation.Add,
            RenderTargetWriteMask = ColorWriteEnable.Red | ColorWriteEnable.Green | ColorWriteEnable.Blue,
        };
        _blend = dev.CreateBlendState(blend);
        _depth = dev.CreateDepthStencilState(DepthStencilDescription.None);
        _raster = dev.CreateRasterizerState(RasterizerDescription.CullNone);
    }

    /// <summary>Line colour (sRGB-ish, written to the 8-bit output as is) and joint colour.</summary>
    public Vector4 LineColor { get; set; } = new(1f, 0.78f, 0.2f, 0.9f);
    public Vector4 JointColor { get; set; } = new(1f, 1f, 1f, 0.95f);

    /// <param name="segments">Model-space endpoints, two per segment.</param>
    /// <param name="joints">Model-space joint positions.</param>
    /// <param name="worldToClip">Row-vector world→clip (cb9 <c>wldToClp</c>).</param>
    public void Draw(ID3D11RenderTargetView output, int width, int height, in Matrix4x4 worldToClip, ReadOnlySpan<Vector3> segments,
        ReadOnlySpan<Vector3> joints, float thicknessPx = 1.5f, float jointPx = 2.5f)
    {
        _verts.Clear();
        var px = new Vector2(2f / Math.Max(width, 1), 2f / Math.Max(height, 1));
        for (int i = 0; i + 1 < segments.Length; i += 2)
        {
            var a = Vector4.Transform(new Vector4(segments[i], 1f), worldToClip);
            var b = Vector4.Transform(new Vector4(segments[i + 1], 1f), worldToClip);
            // Near-plane clip (reversed-Z infinite projection: w = view depth).
            const float near = 0.5f;
            if (a.W < near && b.W < near)
                continue;
            if (a.W < near)
                a = Vector4.Lerp(a, b, (near - a.W) / (b.W - a.W));
            else if (b.W < near)
                b = Vector4.Lerp(b, a, (near - b.W) / (a.W - b.W));
            var sa = new Vector2(a.X / a.W, a.Y / a.W);
            var sb = new Vector2(b.X / b.W, b.Y / b.W);
            var dir = (sb - sa) / px;
            if (dir.LengthSquared() < 1e-8f)
                continue;
            dir = Vector2.Normalize(dir);
            var n = new Vector2(-dir.Y, dir.X) * thicknessPx * 0.5f * px;
            Quad(sa + n, sa - n, sb - n, sb + n, LineColor);
        }
        foreach (var j in joints)
        {
            var c = Vector4.Transform(new Vector4(j, 1f), worldToClip);
            if (c.W < 0.5f)
                continue;
            var s = new Vector2(c.X / c.W, c.Y / c.W);
            var dx = new Vector2(jointPx * px.X, 0);
            var dy = new Vector2(0, jointPx * px.Y);
            Quad(s - dx - dy, s + dx - dy, s + dx + dy, s - dx + dy, JointColor);
        }
        if (_verts.Count == 0)
            return;

        var ctx = _gfx.Context;
        if (_vb is null || _capacity < _verts.Count)
        {
            _vb?.Dispose();
            _capacity = Math.Max(_verts.Count, 1024) * 2;
            _vb = _gfx.Device.CreateBuffer(new BufferDescription((uint)(_capacity * Marshal.SizeOf<Vertex>()), BindFlags.VertexBuffer,
                ResourceUsage.Dynamic, CpuAccessFlags.Write));
        }
        var mapped = ctx.Map(_vb, 0, MapMode.WriteDiscard);
        CollectionsMarshal.AsSpan(_verts).CopyTo(mapped.AsSpan<Vertex>(_verts.Count));
        ctx.Unmap(_vb, 0);

        ctx.IASetInputLayout(_layout);
        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        ctx.IASetVertexBuffer(0, _vb, (uint)Marshal.SizeOf<Vertex>());
        ctx.VSSetShader(_vs);
        ctx.PSSetShader(_ps);
        ctx.RSSetState(_raster);
        ctx.RSSetViewport(new Viewport(0, 0, width, height, 0f, 1f));
        ctx.RSSetScissorRect(0, 0, width, height);
        ctx.OMSetBlendState(_blend);
        ctx.OMSetDepthStencilState(_depth);
        ctx.OMSetRenderTargets(output);
        ctx.Draw((uint)_verts.Count, 0);
        ctx.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>(), null);
    }

    private void Quad(Vector2 a, Vector2 b, Vector2 c, Vector2 d, Vector4 color)
    {
        Add(a, color); Add(b, color); Add(c, color);
        Add(a, color); Add(c, color); Add(d, color);
    }

    private void Add(Vector2 p, Vector4 color) => _verts.Add(new Vertex { Position = new Vector4(p, 0f, 1f), Color = color });

    public void Dispose()
    {
        _vb?.Dispose();
        _vs.Dispose();
        _ps.Dispose();
        _layout.Dispose();
        _blend.Dispose();
        _depth.Dispose();
        _raster.Dispose();
    }
}
