using System;
using System.IO;
using System.Linq;
using System.Numerics;
using Apex.Editor.Services.Preview.ToolsGfx;
using Apex.Render.Assets;
using Apex.Render.Presentation;
using Apex.Render.Scene;

namespace Apex.Shots;

public partial class Program
{
    // Materials from the install's source_data: a lit_decal, a plain lit (the control) and a lit_water_sim_flow_normal_color.
    private const string CheckDecal = "tbd_crack1", CheckLit = "pent_art_carpet_grey01", CheckWater = "karelia_water_stream";

    /// <summary>
    /// Material previews of decals and time-driven materials, drawn with real D3D11 from APE's data (read-only): a decal
    /// lands on the underlay instead of vanishing, water reads cb9 time and moves from frame to frame (so the renderer
    /// asks to be drawn continuously), a static material does neither.
    /// </summary>
    private static void RunMaterialRenderChecks(string outDir)
    {
        var install = Apex.Render.Data.ToolsGfxInstall.FromRoot(InstallRoot);
        if (!install.IsAvailable)
        {
            Console.WriteLine("material render: BO3 install not found — skipped");
            return;
        }
        Apex.Render.Device.GfxDevice gfx;
        try
        {
            gfx = Apex.Render.Device.GfxDevice.CreateDefault();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"material render: no D3D11 device ({ex.Message}) — skipped");
            return;
        }
        var data = new Apex.Render.Data.ToolsGfxData(install, Apex.Render.Data.Gdt.GdtIndex.ForInstall(install));
        var missing = new[] { CheckDecal, CheckLit, CheckWater }.Where(m => data.Gdt.Find(m, "material") is null).ToList();
        if (missing.Count > 0)
        {
            Console.WriteLine($"material render: {string.Join(", ", missing)} not in the install's GDTs — skipped");
            return;
        }

        Check("material render: cb9 time wraps every three hours, as APE's QueryPerformanceCounter time does",
            PreviewSceneRenderer.SceneTime(TimeSpan.FromSeconds(12.5)) == 12.5f
            && PreviewSceneRenderer.SceneTime(TimeSpan.FromHours(3) + TimeSpan.FromSeconds(2)) == 2f);

        var env = PreviewEnvironment.Load(data);
        PreparedPreviewModel Sphere(string material, bool underlay = true) =>
            PreviewModelLoader.PrepareMeshes(data, material, new[] { PreviewShapes.Sphere(material) },
                options: new PreviewModelOptions { SkipBrokenMaterials = true, DecalUnderlay = underlay });

        var decal = Sphere(CheckDecal);
        var bareDecal = Sphere(CheckDecal, underlay: false);
        var lit = Sphere(CheckLit);
        var water = Sphere(CheckWater);
        var warnings = decal.Warnings.Concat(lit.Warnings).Concat(water.Warnings).ToList();
        if (warnings.Count > 0)
            Console.WriteLine("info  material render: " + string.Join(" | ", warnings));
        Check("material render: the checked materials build without warnings", warnings.Count == 0);
        Check("material render: a decal's material preview draws the underlay under it; without the option, or for a non-decal, it doesn't",
            decal.Materials.ContainsKey(PreviewModelLoader.DecalUnderlayMaterial) && decal.Lods[0].Surfaces.Count == 2
            && decal.Lods[0].Surfaces[0].Material == PreviewModelLoader.DecalUnderlayMaterial
            && bareDecal.Lods[0].Surfaces.Count == 1 && lit.Lods[0].Surfaces.Count == 1
            && !lit.Materials.ContainsKey(PreviewModelLoader.DecalUnderlayMaterial));
        Check("material render: water reads cb9 time, a plain lit material and a decal don't",
            water.ReadsSceneTime && !lit.ReadsSceneTime && !decal.ReadsSceneTime);

        const int size = 192;
        using var texture = gfx.Device.CreateTexture2D(new Vortice.Direct3D11.Texture2DDescription
        {
            Width = size, Height = size, ArraySize = 1, MipLevels = 1, Format = Vortice.DXGI.Format.B8G8R8A8_UNorm,
            SampleDescription = new Vortice.DXGI.SampleDescription(1, 0), Usage = Vortice.Direct3D11.ResourceUsage.Default,
            BindFlags = Vortice.Direct3D11.BindFlags.RenderTarget | Vortice.Direct3D11.BindFlags.ShaderResource,
        });
        using var rtv = gfx.Device.CreateRenderTargetView(texture);
        var renderer = new PreviewSceneRenderer(env) { LightState = env.Lighting.States[0].State };
        renderer.OnDeviceCreated(gfx);
        renderer.Camera = new PreviewCamera(new Vector3(-50, 0, 10), 10, 0);
        var animatesChanged = 0;
        renderer.AnimatesChanged += () => animatesChanged++;

        byte[] Draw(double seconds)
        {
            renderer.Render(new D3DFrame(gfx, texture, rtv, Vortice.DXGI.Format.B8G8R8A8_UNorm, size, size, 1, TimeSpan.FromSeconds(seconds)));
            return Apex.Render.Offscreen.Readback.Read(gfx, texture).Data;
        }
        // The model's GPU copy and the sky arrive asynchronously: draw until two frames in a row agree.
        byte[]? Show(PreparedPreviewModel? model)
        {
            renderer.Model = model;
            var previous = Draw(0);
            for (var i = 0; i < 300; i++)
            {
                Pump(20);
                var next = Draw(0);
                if ((model is null || renderer.Stats.Triangles > 0) && env.IsSkyLoaded(renderer.LightState) && next.AsSpan().SequenceEqual(previous))
                    return next;
                previous = next;
            }
            return null;
        }
        // The sphere covers the middle of the frame: compare a 32×32 patch there.
        static byte[] Middle(byte[] frame)
        {
            var patch = new byte[32 * 32 * 4];
            for (var y = 0; y < 32; y++)
                Array.Copy(frame, ((size / 2 - 16 + y) * size + size / 2 - 16) * 4, patch, y * 32 * 4, 32 * 4);
            return patch;
        }
        static bool Same(byte[] a, byte[] b) => a.AsSpan().SequenceEqual(b);

        // The sky last: its GPU copy is made after the first frame asks for it, which a model's frame waits for.
        var underlayOnly = Show(Sphere(PreviewModelLoader.DecalUnderlayMaterial));
        var bare = Show(bareDecal);
        var withUnderlay = Show(decal);
        var sky = Show(null);
        if (sky is null || underlayOnly is null || bare is null || withUnderlay is null)
        {
            Check("material render: every frame settled", false);
            return;
        }
        Check("material render: a decal drawn alone lands on nothing (the sky shows through, as in APE)", Same(Middle(bare), Middle(sky)));
        Check("material render: with the underlay the decal shows: the frame differs from the sky and from the underlay alone",
            !Same(Middle(withUnderlay), Middle(sky)) && !Same(withUnderlay, underlayOnly));
        Check("material render: a static preview doesn't ask to be drawn continuously", !renderer.Animates);
        Save(outDir, "98-decal-material-preview.png", withUnderlay);

        var litFrame = Show(lit);
        Check("material render: a static material draws the same frame whatever the time",
            litFrame is not null && Same(Draw(0), Draw(7.25)));
        var before = animatesChanged;
        if (Show(water) is null)
        {
            Check("material render: the water frame settled", false);
            return;
        }
        var t0 = Draw(0);
        var t2 = Draw(2);
        Check("material render: water moves with cb9 time (frames 2 s apart differ; the same time draws the same frame)",
            !Same(Middle(t0), Middle(t2)) && Same(t0, Draw(0)));
        Check("material render: drawing water makes the renderer ask to be drawn continuously, and says so once",
            renderer.Animates && animatesChanged == before + 1);
        Save(outDir, "99-water-material-preview.png", t2);
        Show(lit);
        Check("material render: back on a static material it stops asking", !renderer.Animates && animatesChanged == before + 2);
        renderer.Model = null;
        renderer.OnDeviceDestroyed();
    }

    /// <summary>Writes a BGRA readback as a PNG under <paramref name="outDir"/>.</summary>
    private static void Save(string outDir, string file, byte[] bgra)
    {
        var rgba = new byte[bgra.Length];
        for (var i = 0; i < bgra.Length; i += 4)
        {
            rgba[i] = bgra[i + 2];
            rgba[i + 1] = bgra[i + 1];
            rgba[i + 2] = bgra[i];
            rgba[i + 3] = 255;
        }
        var side = (int)Math.Sqrt(bgra.Length / 4);
        Directory.CreateDirectory(outDir);
        Apex.Render.Offscreen.PngWriter.WriteRgba8(Path.Combine(outDir, file), side, side, rgba);
    }
}
