using Apex.Render.Constants;
using Apex.Render.Resources;
using Apex.Render.Shaders;
using Apex.Render.States;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.Mathematics;

namespace Apex.Render.Passes;

/// <summary>Pass-level fixed-function state of a scene stage (as captured from APE), used where the material
/// technique does not override it.</summary>
public readonly record struct StageState(uint Blend, uint DepthStencil, byte StencilRef, RasterBits Raster, int RenderTargetCount = 1);

/// <summary>
/// Draws <see cref="IPreviewDrawItem"/>s the way ToolsGfx's scene lists do: instance data for every draw of the
/// pass uploaded to t30 first (draw i uses StartInstanceLocation i + 1), the engine buffers b9/b10/b11 and the
/// dummy GPU-skin textures bound by name, then the material technique's own resources.
/// <para>Code resources are assigned once per pass to each distinct material pipeline, and a pipeline's registers are
/// unbound at the end of the pass rather than after each draw (every draw binds all the registers its shaders
/// declare, so what an earlier draw left behind is never read). State and input-assembler calls that would repeat the
/// previous draw's are skipped.</para>
/// </summary>
public sealed class SceneDrawer
{
    private readonly RenderContext _rc;
    private readonly List<(IPreviewDrawItem Item, MaterialPass Pass)> _drawable = new();
    private readonly List<ShaderBindings> _pipelines = new();
    private CodeObjectConsts[] _objects = new CodeObjectConsts[16];
    private readonly ID3D11Buffer[] _vertexBuffers = new ID3D11Buffer[2];
    private readonly uint[] _strides = new uint[2];
    private readonly uint[] _offsets = new uint[2];

    public SceneDrawer(RenderContext rc) => _rc = rc;

    /// <summary>True when some item has <paramref name="technique"/> (a pass with it would draw).</summary>
    public static bool Draws(IReadOnlyList<IPreviewDrawItem> items, PreviewTechnique technique)
    {
        for (int i = 0; i < items.Count; i++)
            if (items[i].GetPass(technique) != null)
                return true;
        return false;
    }

    /// <summary>Draws every item that has <paramref name="technique"/>; returns the number of draws.</summary>
    public int Draw(IReadOnlyList<IPreviewDrawItem> items, PreviewTechnique technique, GpuBuffer sceneCb,
        ReadOnlySpan<ID3D11RenderTargetView> rtvs, ID3D11DepthStencilView? dsv, Viewport viewport, in StageState stage,
        Action<ShaderBindings>? bindPassResources = null, int cascade = -1)
    {
        var ctx = _rc.Gfx.Context;
        var drawable = _drawable;
        drawable.Clear();
        bool anySkinned = false;
        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (cascade >= 0 && (item.ShadowCascadeMask & (1 << cascade)) == 0)
                continue;
            if (item.GetPass(technique) is { } p)
            {
                drawable.Add((item, p));
                anySkinned |= !item.Bones.IsEmpty;
            }
        }
        if (drawable.Count == 0)
            return 0;

        if (_objects.Length < drawable.Count)
            _objects = new CodeObjectConsts[Math.Max(drawable.Count, _objects.Length * 2)];
        for (int i = 0; i < drawable.Count; i++)
            _objects[i] = drawable[i].Item.ObjectConsts;
        _rc.UploadInstances(_objects.AsSpan(0, drawable.Count));
        _rc.BeginScenePass();

        ctx.OMSetRenderTargets(rtvs, dsv);
        ctx.RSSetViewport(viewport);
        ctx.RSSetScissorRect(0, 0, 4096, 4096);
        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);

        // Code resources: the same for every draw of the pass, so assigned once per material pipeline.
        _pipelines.Clear();
        foreach (var (_, pass) in drawable)
        {
            var b = pass.Bindings;
            if (_pipelines.Contains(b))
                continue;
            _pipelines.Add(b);
            b.TrySetConstantBuffer(CodeBuffer.SceneName, sceneCb);
            b.TrySetConstantBuffer(CodeBuffer.ObjectName, _rc.ObjectCb);
            b.TrySetConstantBuffer(CodeBuffer.BonesName, _rc.BonesCb);
            b.TrySetResource("gObjectInstanceData", _rc.InstanceData.Srv);
            b.TrySetResource("gpuSkinBase", _rc.GpuSkinBase.Srv);
            b.TrySetResource("gpuSkinQuat", _rc.GpuSkinQuat.Srv);
            b.TrySetResource("gpuSkinPos", _rc.GpuSkinPos.Srv);
            bindPassResources?.Invoke(b);
        }

        // b11 is zeros except for a skinned item, which binds its uploaded bones (items sharing bone memory share the
        // upload). APE draws a posed model non-instanced from its own cb10 (hasBones = 1) and cb11 — the dual-quaternion
        // skinning shaders read hasBones from cb10 only — so skinned items take instance id 0 and their object constants
        // go to b10; b10 is back to identity afterwards.
        bool objectDirty = false;
        ShaderBindings? applied = null;
        ID3D11BlendState? stageBlend = null, lastBlend = null;
        ID3D11DepthStencilState? lastDepth = null;
        byte lastStencilRef = 0;
        ID3D11RasterizerState? lastRaster = null;
        ID3D11InputLayout? lastLayout = null;
        MeshGeometry? lastMesh = null;
        for (int i = 0; i < drawable.Count; i++)
        {
            var (item, pass) = drawable[i];
            var b = pass.Bindings;

            bool skinned = !item.Bones.IsEmpty;
            if (anySkinned)
                b.TrySetConstantBuffer(CodeBuffer.BonesName, skinned ? _rc.BonesFor(item.Bones) : _rc.BonesCb);
            if (skinned)
            {
                _rc.ObjectCb.Update(ctx, item.ObjectConsts);
                objectDirty = true;
            }
            else if (objectDirty)
            {
                _rc.ObjectCb.Update(ctx, CodeObjectConsts.Identity);
                objectDirty = false;
            }

            var st = pass.State;
            var blend = st?.BlendObject
                        ?? (st?.Blend is { Length: > 0 } words ? _rc.States.GetBlend(words) : stageBlend ??= StageBlend(stage));
            if (blend != lastBlend)
            {
                ctx.OMSetBlendState(blend, new Color4(1, 1, 1, 1), uint.MaxValue);
                lastBlend = blend;
            }
            var depth = st?.DepthStencilObject ?? _rc.States.GetDepthStencil(st?.DepthStencil ?? stage.DepthStencil);
            byte stencilRef = st?.StencilRef ?? stage.StencilRef;
            if (depth != lastDepth || stencilRef != lastStencilRef)
            {
                ctx.OMSetDepthStencilState(depth, stencilRef);
                (lastDepth, lastStencilRef) = (depth, stencilRef);
            }
            var raster = st?.RasterizerObject ?? _rc.States.GetRasterizer(st?.Raster ?? stage.Raster);
            if (raster != lastRaster)
            {
                ctx.RSSetState(raster);
                lastRaster = raster;
            }

            var mesh = item.Mesh;
            var layout = _rc.Layouts.Get(mesh.Decl, pass.VertexShader);
            if (layout != lastLayout)
            {
                ctx.IASetInputLayout(layout);
                lastLayout = layout;
            }
            if (mesh != lastMesh)
            {
                _vertexBuffers[0] = mesh.VertexBuffer;
                _vertexBuffers[1] = _rc.Quad.InstanceIds;
                _strides[0] = (uint)mesh.VertexStride;
                _strides[1] = 4;
                _offsets[0] = (uint)mesh.VertexOffset;
                _offsets[1] = 0;
                ctx.IASetVertexBuffers(0, 2, _vertexBuffers, _strides, _offsets);
                ctx.IASetIndexBuffer(mesh.IndexBuffer, mesh.IndexFormat, (uint)mesh.IndexOffset);
                lastMesh = mesh;
            }

            b.Apply(ctx, applied);
            applied = b;
            var range = cascade >= 0 ? item.ShadowRange(cascade) ?? item.Range : item.Range;
            ctx.DrawIndexedInstanced((uint)range.IndexCount, 1, (uint)range.StartIndex, range.BaseVertex, skinned ? 0u : (uint)(i + 1));
        }
        if (objectDirty)
            _rc.ObjectCb.Update(ctx, CodeObjectConsts.Identity);
        foreach (var b in _pipelines)
            b.Unbind(ctx);
        ctx.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>(), null);
        return drawable.Count;
    }

    private ID3D11BlendState StageBlend(in StageState stage)
    {
        Span<uint> words = stackalloc uint[Math.Max(1, stage.RenderTargetCount)];
        words.Fill(stage.Blend);
        return _rc.States.GetBlend(words);
    }
}
