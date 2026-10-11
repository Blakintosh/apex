using System;
using System.IO;
using System.Linq;
using System.Numerics;
using Apex.Editor.Services.Preview.ToolsGfx;
using Apex.Render.Assets;
using Apex.Render.Data.Assets;
using Apex.Render.Presentation;
using Apex.Render.Scene;

namespace Apex.Shots;

public partial class Program
{
    internal static void PumpFor(int ms) => Pump(ms);

    /// <summary>
    /// --stock-shot &lt;stockRoot&gt; &lt;outDir&gt; [xmodel] [material]: renders a stock model and one of its materials from a
    /// <see cref="StockCache"/> alone (no xmodel_bin / image sources on disk), with real D3D11 and APE's shaders.
    /// </summary>
    private static void RunStockShot(string stockRoot, string outDir, string xmodel, string material)
    {
        var install = Apex.Render.Data.ToolsGfxInstall.FromRoot(InstallRoot);
        var stock = new StockCache(stockRoot);
        var gfx = Apex.Render.Device.GfxDevice.CreateDefault();
        // No source_data / model_export GDTs: only the install's built-in roots (texture_assets etc., the extras layer)
        // and the stock layer, so nothing a real source file backs can stand in for the stock asset.
        var data = new Apex.Render.Data.ToolsGfxData(install, new Apex.Render.Data.Gdt.GdtIndex(), stock: stock);

        // The point of the exercise: nothing the lookups could hash exists.
        var entry = data.Gdt.Find(xmodel, "xmodel");
        Check($"stock: {xmodel} resolves through the stock GDT layer ({entry?.SourceFile})", entry is not null);
        if (entry is null)
            return;
        var bin = data.Models.ResolveModelExportPath(entry.Fields["filename"]);
        Check($"stock: its xmodel_bin does not exist ({bin})", !File.Exists(bin));
        var loc = data.Models.FindXMesh(bin, Apex.Render.Data.Hashing.XModelLodParameters.FromGdt(entry.Fields));
        Console.WriteLine($"info  stock: xmesh {loc?.Path} ({loc?.Match})");
        Check("stock: the xmesh comes from the stock root, by name", loc is not null && loc.Path.StartsWith(stock.Root, StringComparison.OrdinalIgnoreCase));

        var options = new PreviewModelOptions { SkipBrokenMaterials = true };
        var model = PreviewModelLoader.Prepare(data, xmodel, options);
        foreach (var w in model.Warnings)
            Console.WriteLine("warn  stock model: " + w);
        Console.WriteLine($"info  stock model: {model.Lods.Count} LOD(s), {model.TriangleCount(0)} tris, {model.Materials.Count} materials, " +
                          $"{model.BrokenMaterialCount} broken, bounds {model.BoundsMin} .. {model.BoundsMax}");
        var bound = model.Materials.Values.Where(m => m is not null).SelectMany(m => m!.Values).SelectMany(p => p.Images)
            .Where(i => i.Image is not null).GroupBy(i => i.Image!.CachePaths[0], StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        bool InStock(PreparedImage i) => i.Image!.CachePaths[0].StartsWith(stock.Root, StringComparison.OrdinalIgnoreCase);
        Console.WriteLine($"info  stock model: {bound.Count} distinct images bound, {bound.Count(InStock)} from the stock root");
        foreach (var i in bound)
            Console.WriteLine($"        {(i.Texture.IsBuiltin ? "builtin" : "asset  ")} {i.Texture.ImageName,-48} {(InStock(i) ? "stock: " + Path.GetRelativePath(stock.Root, i.Image!.CachePaths[0]) : i.Image!.CachePaths[0])}");
        // Images the install defines itself (texture_assets: the shared t7_micro_* library, with real sources) win over
        // the stock layer, as they should; every other one must come from the stock root.
        bool StockEntry(PreparedImage i) => data.Gdt.Find(i.Texture.ImageName, "image")?.SourceFile.StartsWith(stock.Root, StringComparison.OrdinalIgnoreCase) == true;
        Console.WriteLine($"info  stock model: {bound.Count(i => !i.Texture.IsBuiltin && !StockEntry(i))} images resolved through the install's own GDTs (real sources), not the stock layer");
        Check("stock model: every image the stock GDT defines is drawn from the stock root", bound.Where(StockEntry).All(InStock) && bound.Any(StockEntry));
        var missing = model.Materials.Values.Where(m => m is not null).SelectMany(m => m!.Values).SelectMany(p => p.MissingImages).Distinct().ToList();
        Check($"stock model: no image missing ({string.Join(", ", missing)})", missing.Count == 0);
        Check("stock model: every material builds (none defaulted)", model.BrokenMaterialCount == 0);

        var sphere = PreviewModelLoader.PrepareMeshes(data, material, new[] { PreviewShapes.Sphere(material) },
            options: new PreviewModelOptions { SkipBrokenMaterials = true, DecalUnderlay = true });
        foreach (var w in sphere.Warnings)
            Console.WriteLine("warn  stock material: " + w);
        Check($"stock material: {material} builds on the preview sphere", sphere.BrokenMaterialCount == 0);

        using var shooter = new OffscreenShooter(PreviewEnvironment.Load(data), gfx);
        var c = (model.BoundsMin + model.BoundsMax) * 0.5f;
        var r = (model.BoundsMax - model.BoundsMin).Length() * 0.5f;
        var shots = new (string File, PreparedPreviewModel Model, PreviewCamera Camera)[]
        {
            ("stock-model-front.png", model, OffscreenShooter.LookAt(c + new Vector3(2.0f * r, 0.35f * r, 0.25f * r), c)),
            ("stock-model-three-quarter.png", model, OffscreenShooter.LookAt(c + new Vector3(1.4f * r, -1.3f * r, 0.6f * r), c)),
            ($"stock-material-{material}.png", sphere, OffscreenShooter.SphereCamera),
        };
        foreach (var (file, m, camera) in shots)
        {
            var frame = shooter.Shoot(m, camera);
            Check($"stock: {file} rendered ({shooter.Triangles} tris)", frame is not null);
            if (frame is not null)
                Save(outDir, file, frame);
        }
        Console.WriteLine($"info  stock: shots in {Path.GetFullPath(outDir)}");
    }
}

/// <summary>A prepared preview model drawn with real D3D11 and APE's shaders into an off-screen texture (the window's own viewport can't draw headless).</summary>
internal sealed class OffscreenShooter : IDisposable
{
    public const int Size = 768;

    private readonly Apex.Render.Device.GfxDevice _gfx;
    private readonly PreviewEnvironment _env;
    private readonly Vortice.Direct3D11.ID3D11Texture2D _texture;
    private readonly Vortice.Direct3D11.ID3D11RenderTargetView _rtv;
    private readonly PreviewSceneRenderer _renderer;

    public OffscreenShooter(PreviewEnvironment env, Apex.Render.Device.GfxDevice? gfx = null)
    {
        _env = env;
        _gfx = gfx ?? Apex.Render.Device.GfxDevice.CreateDefault();
        _texture = _gfx.Device.CreateTexture2D(new Vortice.Direct3D11.Texture2DDescription
        {
            Width = Size, Height = Size, ArraySize = 1, MipLevels = 1, Format = Vortice.DXGI.Format.B8G8R8A8_UNorm,
            SampleDescription = new Vortice.DXGI.SampleDescription(1, 0), Usage = Vortice.Direct3D11.ResourceUsage.Default,
            BindFlags = Vortice.Direct3D11.BindFlags.RenderTarget | Vortice.Direct3D11.BindFlags.ShaderResource,
        });
        _rtv = _gfx.Device.CreateRenderTargetView(_texture);
        _renderer = new PreviewSceneRenderer(env) { LightState = env.Lighting.States[0].State };
        _renderer.OnDeviceCreated(_gfx);
    }

    public static PreviewCamera SphereCamera => new(new Vector3(-50, 0, 10), 10, 0);

    public int Triangles => _renderer.Stats.Triangles;

    public static PreviewCamera LookAt(Vector3 eye, Vector3 target)
    {
        var d = Vector3.Normalize(target - eye);
        return new PreviewCamera(eye, -MathF.Asin(d.Z) * 180f / MathF.PI, MathF.Atan2(d.Y, d.X) * 180f / MathF.PI);
    }

    /// <summary>A three-quarter view of a model.</summary>
    public static PreviewCamera ThreeQuarter(PreparedPreviewModel model)
    {
        var c = (model.BoundsMin + model.BoundsMax) * 0.5f;
        var r = (model.BoundsMax - model.BoundsMin).Length() * 0.5f;
        return LookAt(c + new Vector3(1.4f * r, -1.3f * r, 0.6f * r), c);
    }

    private byte[] Draw()
    {
        _renderer.Render(new D3DFrame(_gfx, _texture, _rtv, Vortice.DXGI.Format.B8G8R8A8_UNorm, Size, Size, 1, TimeSpan.Zero));
        return Apex.Render.Offscreen.Readback.Read(_gfx, _texture).Data;
    }

    /// <summary>The frame once it stops changing (textures and the sky stream in over a few frames); null when it never settles.</summary>
    public byte[]? Shoot(PreparedPreviewModel model, PreviewCamera camera)
    {
        _renderer.Model = model;
        _renderer.Camera = camera;
        var previous = Draw();
        for (var i = 0; i < 300; i++)
        {
            Program.PumpFor(20);
            var next = Draw();
            if (_renderer.Stats.Triangles > 0 && _env.IsSkyLoaded(_renderer.LightState) && next.AsSpan().SequenceEqual(previous))
                return next;
            previous = next;
        }
        return null;
    }

    public void Dispose()
    {
        _renderer.Model = null;
        _renderer.OnDeviceDestroyed();
        _rtv.Dispose();
        _texture.Dispose();
    }
}
