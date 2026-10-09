using System.Runtime.InteropServices;
using Apex.Render.Assets;
using Apex.Render.Data.Assets;
using Apex.Render.Data.Lighting;
using Apex.Render.Passes;
using Apex.Render.Resources;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Apex.Render.Scene;

/// <summary>
/// GPU lighting inputs of one preview lighting state, built from assetviewer.led through the data layer
/// (lighting_data.md): t40 sun shadow tree, t46–48 ambient-cube volumes, t51 reflection cube, t44 env BRDF. The
/// preview has no local lights, probes, decals, cookies or spot/omni shadows (numLights = numProbes = 0), so t21–t24
/// are zero buffers of APE's sizes and t50/t52/t53 small black textures of the right dimension.
/// </summary>
public sealed class LedLightingResources : IDisposable
{
    // APE's code structured buffers (elements × stride): cull constants, lights, probes, probe blends.
    public const int CullConstantCount = 2810, LightCount = 2048, ProbeCount = 250, ProbeBlendCount = 750;

    private readonly List<IDisposable> _owned = new();

    public SceneLightingResources Resources { get; }

    public LedLightingResources(RenderContext rc, PreviewLighting state, CachedImage envBrdf)
    {
        var gfx = rc.Gfx;
        T Own<T>(T d) where T : IDisposable { _owned.Add(d); return d; }
        var unused = rc.Shared(r => new UnusedInputs(r.Gfx));

        var sstBuffer = Own(GpuBuffer.CreateStructured(gfx, MemoryMarshal.AsBytes(state.SunShadowTree.AsSpan()), 4, debugName: "t40 gSunShadowTree"));

        GpuTexture Volume(ProbeVolumeTexture v, string name) => Own(GpuTexture.FromData(gfx,
            TextureData.FromContiguous(Format.BC6H_Uf16, TextureKind.Texture3D, v.Width, v.Height, v.Depth, 1, 1, v.Data), debugName: name));

        var r = state.Reflection;
        var faces = new List<ReadOnlyMemory<byte>>();
        for (int f = 0; f < 6; f++)
            for (int m = 0; m < r.MipCount; m++)
                faces.Add(r.Mips[m][f]);
        var cube = Own(GpuTexture.FromData(gfx, new TextureData
        {
            Format = Format.BC6H_Uf16,
            Width = r.FaceSize,
            Height = r.FaceSize,
            ArraySize = 6,
            MipLevels = r.MipCount,
            Kind = TextureKind.TextureCube,
            Subresources = faces,
        }, debugName: "t51 gReflectionProbeArray"));

        var brdf = Own(GpuTexture.FromData(gfx, MaterialPassFactory.ToTextureData(envBrdf), debugName: "t44 $env_brdf_generic"));

        Resources = new SceneLightingResources
        {
            CullConstants = unused.CullConstants.Srv!,
            Lights = unused.Lights.Srv!,
            Probes = unused.Probes.Srv!,
            ReflectionProbeBlends = unused.ProbeBlends.Srv!,
            SunShadowTree = sstBuffer.Srv!,
            EnvBrdf = brdf.Srv!,
            ProbeVolumeX = Volume(state.ProbeVolumes[0], "t46 gProbeXArray").Srv!,
            ProbeVolumeY = Volume(state.ProbeVolumes[1], "t47 gProbeYArray").Srv!,
            ProbeVolumeZ = Volume(state.ProbeVolumes[2], "t48 gProbeZArray").Srv!,
            CookieArray = unused.Cookies.Srv!,
            ReflectionProbeArray = Own(cube.CreateCubeArraySrv(gfx, Format.BC6H_Uf16)),
            SpotShadowArray = unused.Spot.Srv!,
            OmniShadowArray = unused.OmniArraySrv,
        };
    }

    /// <summary>The zero buffers and black textures standing in for what the preview does not have — the same for every
    /// lighting state, so one set per device (<see cref="RenderContext.Shared{T}"/>).</summary>
    private sealed class UnusedInputs : IDisposable
    {
        public readonly GpuBuffer CullConstants, Lights, Probes, ProbeBlends;
        public readonly GpuTexture Cookies, Spot, Omni;
        public readonly ID3D11ShaderResourceView OmniArraySrv;

        public UnusedInputs(Device.GfxDevice gfx)
        {
            var zeros = new byte[512 * 512 * 4];
            GpuBuffer Zero(int count, int stride, string name) => GpuBuffer.CreateStructured(gfx, zeros.AsSpan(0, count * stride), stride, debugName: name);
            CullConstants = Zero(CullConstantCount, 80, "t21 gCullConstants");
            Lights = Zero(LightCount, 224, "t22 gLights");
            Probes = Zero(ProbeCount, 224, "t23 gProbes");
            ProbeBlends = Zero(ProbeBlendCount, 96, "t24 gReflectionProbeBlends");
            Cookies = GpuTexture.FromData(gfx, new TextureData
            {
                Format = Format.R8G8B8A8_UNorm, Width = 512, Height = 512, ArraySize = 1, Kind = TextureKind.Texture2DArray,
                Subresources = [zeros],
            }, debugName: "t50 gCookieArray (unused)");
            var texel = zeros.AsMemory(0, 2);
            Spot = GpuTexture.FromData(gfx, new TextureData
            {
                Format = Format.R16_UNorm, Width = 1, Height = 1, ArraySize = 1, Kind = TextureKind.Texture2DArray, Subresources = [texel],
            }, debugName: "t52 spot shadows (unused)");
            Omni = GpuTexture.FromData(gfx, new TextureData
            {
                Format = Format.R16_UNorm, Width = 1, Height = 1, ArraySize = 6, Kind = TextureKind.TextureCube,
                Subresources = [texel, texel, texel, texel, texel, texel],
            }, debugName: "t53 omni shadows (unused)");
            OmniArraySrv = Omni.CreateCubeArraySrv(gfx, Format.R16_UNorm);
        }

        public void Dispose()
        {
            OmniArraySrv.Dispose();
            Omni.Dispose();
            Spot.Dispose();
            Cookies.Dispose();
            ProbeBlends.Dispose();
            Probes.Dispose();
            Lights.Dispose();
            CullConstants.Dispose();
        }
    }

    public void Dispose()
    {
        for (int i = _owned.Count - 1; i >= 0; i--)
            _owned[i].Dispose();
        _owned.Clear();
    }
}
