using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using Apex.Editor.Services.Preview.ToolsGfx;
using Apex.Render.Assets;
using Apex.Render.Data;
using Apex.Render.Data.Conversion;
using Apex.Render.Data.Gdt;
using Apex.Render.Data.Shaders;
using Apex.Render.Passes;
using Apex.Render.Presentation;
using Apex.Render.Scene;

namespace Apex.Shots;

/// <summary>
/// Custom techset shaders: compiled from their source on a cache miss, as APE does (shader-compile, on a scratch tree),
/// and a transparent one that samples the opaque scene (custom-shader: blak_frosted_glass on the real install, read-only,
/// shot to the shots folder; skipped when the test asset isn't there).
/// </summary>
public partial class Program
{
    // ═══ shader-compile: compile on a miss, recompile on an edit, errors that say why ═══════════

    private static void RunShaderCompileChecks()
    {
        var root = NewScratch("shader-compile");
        var src = Path.Combine(root, "share", "raw", "shaders_stable_toolsgfx");
        Directory.CreateDirectory(Path.Combine(src, "test", "Include"));
        Directory.CreateDirectory(Path.Combine(src, "Include"));
        File.WriteAllText(Path.Combine(src, "Include", "Tint.hlsl"), "float4 Tint() { return float4(1, 0, 0, 1); }\n");
        var shader = Path.Combine(src, "test", "solid.hlsl");
        void Write(string body) => File.WriteAllText(shader, "#include \"Include/Tint.hlsl\"\n" +
            "#if !TOOLSGFX || !USING_HLSL || !IS_PIXEL_SHADER || IS_VERTEX_SHADER\n#error implicit macros\n#endif\n" +
            "float4 gain;\nfloat4 ps_main(float4 p : SV_POSITION) : SV_TARGET0 { " + body + " }\n");
        Write("return Tint() * gain;");
        var install = ToolsGfxInstall.FromRoot(root);
        var apexCache = new ApexToolsGfxCache(Path.Combine(root, "apex-cache"));
        var key = new ShaderVariantKey("test/solid.hlsl", ShaderStage.Pixel, ShaderDefine.ParseList(["TOOLSGFX 1", "USE_OIT"]));

        var cache = new ShaderCache(install, apexCache, new CachePolicy());
        ShaderBinary first;
        using (var recording = ShaderCache.RecordSources())
        {
            first = cache.Load(key);
            Check("shader compile: a variant APE never built is compiled from shaders_stable_toolsgfx, with APE's implicit macros",
                first.Origin == CacheOrigin.ApexConverted && first.Reflection.ConstantBuffers.Any(c => c.Name == "$Globals"));
            Check("shader compile: the source and its include are recorded for whoever keeps what it built",
                recording.Files.Count == 2 && recording.Files.Any(f => f.EndsWith("solid.hlsl", StringComparison.OrdinalIgnoreCase))
                && recording.Files.Any(f => f.EndsWith("Tint.hlsl", StringComparison.OrdinalIgnoreCase)));
        }
        Check("shader compile: the result goes to Apex's cache only, in APE's _deps + blob layout",
            first.DepsPath.StartsWith(apexCache.ShaderCacheDir, StringComparison.OrdinalIgnoreCase) && File.Exists(first.DepsPath)
            && File.Exists(first.BlobPath) && first.Deps.Includes.Count == 1);
        Check("shader compile: the same load again is the memoised binary", ReferenceEquals(cache.Load(key), first));
        var fresh = new ShaderCache(install, apexCache, new CachePolicy()).Load(key);
        Check("shader compile: a new session reads it back from Apex's cache", fresh.Origin == CacheOrigin.ApexCache && fresh.CodeKey == first.CodeKey
            && fresh.Dxbc.AsSpan().SequenceEqual(first.Dxbc));

        void Touch(string path) => File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5 + Random.Shared.Next(1000)));
        Write("return Tint() * gain * 0.5;");
        Touch(shader);
        var edited = cache.Load(key);
        Check("shader compile: an edit to the source recompiles on the next load", edited.Origin == CacheOrigin.ApexConverted && edited.CodeKey != first.CodeKey);
        File.WriteAllText(Path.Combine(src, "Include", "Tint.hlsl"), "float4 Tint() { return float4(0, 1, 0, 1); }\n");
        Touch(Path.Combine(src, "Include", "Tint.hlsl"));
        var include = cache.Load(key);
        Check("shader compile: an edit to an include recompiles too", include.CodeKey != edited.CodeKey);
        File.WriteAllText(Path.Combine(src, "Include", "Tint.hlsl"), "float4 Tint() { return float4(1, 0, 0, 1); }\n");
        Write("return Tint() * gain;");
        Touch(shader);
        Touch(Path.Combine(src, "Include", "Tint.hlsl"));
        var reverted = new ShaderCache(install, apexCache, new CachePolicy()).Load(key);
        Check("shader compile: reverting the edits finds the first build in Apex's cache (blobs are named by their sources)",
            reverted.CodeKey == first.CodeKey && reverted.Origin == CacheOrigin.ApexCache);

        Write("return Tint() * undefinedThing;");
        Touch(shader);
        ShaderCompileException? error = null;
        using (var recording = ShaderCache.RecordSources())
        {
            try
            {
                cache.Load(key);
            }
            catch (ShaderCompileException ex)
            {
                error = ex;
            }
            Check("shader compile: a failed compile is recorded too, so fixing it reloads",
                recording.Files.Any(f => f.EndsWith("solid.hlsl", StringComparison.OrdinalIgnoreCase)));
        }
        Check("shader compile: a compile error names the source, the variant, the line and the compiler's message",
            error is not null && error.Message.Contains("test/solid.hlsl") && error.Message.Contains("ps_main/ps_5_0")
            && error.Message.Contains("solid.hlsl(") && error.Message.Contains("undefinedThing"));
        Check("shader compile: TryLoad reports it instead of throwing", !cache.TryLoad(key, out _, out var tryError) && tryError == error?.Message);

        var noCompile = new ShaderCache(install, new ApexToolsGfxCache(Path.Combine(root, "apex-cache-2")), new CachePolicy(UseApexCache: false, ConvertMissing: false));
        Write("return Tint() * gain;");
        Touch(shader);
        Check("shader compile: ape-only never compiles (a miss, as before)",
            Throws<ShaderCacheMissException>(() => noCompile.Load(key)));
        Check("shader compile: a core shader with no source is still a plain miss",
            Throws<ShaderCacheMissException>(() => cache.Load(new ShaderVariantKey("gbuffer_lit.hlsl", ShaderStage.Pixel, []))));
    }

    // ═══ custom-shader: blak_frosted_glass previews the scene behind it ═════════

    private const string FrostGdt = @"source_data\blak_frostglass\frostglass_test.gdt";
    private const string FrostModel = "wpn_iw8_ar_mcharlie_mag_view_v2_frost";
    private const string FrostMaterial = "weapon_vm_ar_mcharlie_v2_mag_frost";
    private const string FrostGun = "wpn_iw8_ar_mcharlie_view_v2";

    private static void RunCustomShaderChecks(string outDir)
    {
        var install = ToolsGfxInstall.FromRoot(InstallRoot);
        var frostGdt = Path.Combine(InstallRoot, FrostGdt);
        var gunGdts = Path.Combine(InstallRoot, "source_data", "blak_iw8", "ar_mcharlie");
        if (!install.IsAvailable || !File.Exists(frostGdt) || !Directory.Exists(gunGdts)
            || ShaderSourceCompiler.FindSource(install.ShaderSourceDir, "blak/blak_frosted_glass.hlsl") is null)
        {
            Console.WriteLine("custom shader: the blak_frosted_glass test asset isn't on this install — skipped");
            return;
        }
        Apex.Render.Device.GfxDevice gfx;
        try
        {
            gfx = Apex.Render.Device.GfxDevice.CreateDefault();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"custom shader: no D3D11 device ({ex.Message}) — skipped");
            return;
        }
        var shots = Path.Combine(outDir, "custom-shader");
        Directory.CreateDirectory(shots);
        // Compiled shaders and converted caches go to a scratch Apex cache, so the run proves compile-on-miss.
        var apexCache = new ApexToolsGfxCache(NewScratch("custom-shader-cache"));

        // The material's debug view (cg03_w) and the techset's other fields, on temp copies of the GDTs.
        ToolsGfxData Data(string label, float? debugView)
        {
            var gdts = NewScratch("custom-shader-gdt-" + label);
            foreach (var f in Directory.EnumerateFiles(gunGdts, "*.gdt"))
                File.Copy(f, Path.Combine(gdts, Path.GetFileName(f)));
            var text = File.ReadAllText(frostGdt);
            if (debugView is { } v)
                text = SetMaterialField(text, FrostMaterial, "cg03_w", v.ToString(System.Globalization.CultureInfo.InvariantCulture));
            File.WriteAllText(Path.Combine(gdts, "frostglass_test.gdt"), text);
            return new ToolsGfxData(install, new GdtIndex(gdts), apexCache);
        }

        var options = new PreviewModelOptions { SkipBrokenMaterials = true };
        var plain = Data("plain", null);
        var env = PreviewEnvironment.Load(plain);
        const int width = 1280, height = 720;
        using var texture = gfx.Device.CreateTexture2D(new Vortice.Direct3D11.Texture2DDescription
        {
            Width = width, Height = height, ArraySize = 1, MipLevels = 1, Format = Vortice.DXGI.Format.B8G8R8A8_UNorm,
            SampleDescription = new Vortice.DXGI.SampleDescription(1, 0), Usage = Vortice.Direct3D11.ResourceUsage.Default,
            BindFlags = Vortice.Direct3D11.BindFlags.RenderTarget | Vortice.Direct3D11.BindFlags.ShaderResource,
        });
        using var rtv = gfx.Device.CreateRenderTargetView(texture);

        byte[]? Shoot(string name, PreparedPreviewModel model, PreparedPreviewModel? behind, PreviewCamera camera, bool empty = false)
        {
            var renderer = new PreviewSceneRenderer(env) { LightState = env.Lighting.States[0].State, Camera = camera };
            renderer.OnDeviceCreated(gfx);
            try
            {
                renderer.Model = model;
                if (behind is not null)
                    renderer.Pose = new StillPose(model, behind);
                byte[] Draw()
                {
                    renderer.Render(new D3DFrame(gfx, texture, rtv, Vortice.DXGI.Format.B8G8R8A8_UNorm, width, height, 1, TimeSpan.Zero));
                    return Apex.Render.Offscreen.Readback.Read(gfx, texture).Data;
                }
                var previous = Draw();
                for (var i = 0; i < 300; i++)
                {
                    Pump(20);
                    var next = Draw();
                    if ((empty || renderer.Stats.Triangles > 0) && env.IsSkyLoaded(renderer.LightState) && next.AsSpan().SequenceEqual(previous))
                    {
                        var img = Apex.Render.Offscreen.Readback.Read(gfx, texture);
                        Apex.Render.Offscreen.PngWriter.WriteRgba8(Path.Combine(shots, name + ".png"), width, height,
                            Apex.Render.Offscreen.Readback.ToRgba8(img));
                        return next;
                    }
                    previous = next;
                }
                Console.WriteLine($"custom shader: {name} never settled");
                return null;
            }
            finally
            {
                renderer.OnDeviceDestroyed();
            }
        }

        // The mag's distortion and interior shells are skinOverridden to global_invisible, a shipped material no GDT
        // defines: the preview draws it as $default, so the shots hide those shells to show the glass over the gun.
        string[] shells = [FrostMaterial.Replace("_frost", "_distortion"), FrostMaterial.Replace("_frost", "_interior")];
        // Only _trans is skinOverridden to the frost: the mag's own material (its rounds and follower) stays opaque, and is
        // part of the scene behind the glass.
        string[] glass = [FrostMaterial.Replace("_frost", "_trans")];
        PreparedPreviewModel Model(ToolsGfxData data, string name, bool noGlass = false) => PreviewModelLoader.Prepare(data, name,
            options with { HiddenMaterials = new HashSet<string>(noGlass ? [.. shells, .. glass] : shells, StringComparer.OrdinalIgnoreCase) });
        PreparedPreviewModel Sphere(ToolsGfxData data, bool hidden = false) =>
            PreviewModelLoader.PrepareMeshes(data, FrostMaterial, [PreviewShapes.Sphere(FrostMaterial)],
                options: hidden ? options with { HiddenMaterials = new HashSet<string>([FrostMaterial]) } : options);

        var mag = Model(plain, FrostModel);
        Check("custom shader: the frosted material builds (its shaders compiled from source), not the default material",
            !mag.DefaultedMaterials.Contains(FrostMaterial) && mag.Materials.GetValueOrDefault(FrostMaterial) is { } passes
            && passes.ContainsKey(PreviewTechnique.Oit));
        foreach (var w in mag.Warnings)
            Console.WriteLine($"info  custom shader: {FrostModel}: {w}");
        Check("custom shader: no warnings building it (contiguous material slots, every element found, inline samplers)",
            mag.Warnings.Count == 0);

        // Through the mag at the gun body behind it (the gun drawn as an attached model at the same origin, as in a
        // viewmodel), the mag alone against the sky, and the material preview's sphere. The same frames without the
        // glass are the scene it must vanish into with debug view 6.
        var gun = Model(plain, FrostGun);
        var magCenter = (mag.BoundsMin + mag.BoundsMax) * 0.5f;
        var magSize = (mag.BoundsMax - mag.BoundsMin).Length();
        var through = Orbit(magCenter, magSize * 1.6f, pitch: 12, yaw: 70);
        var close = Orbit(magCenter, magSize * 0.85f, pitch: 0, yaw: 215);
        var sphereCam = Orbit(Vector3.Zero, 52, pitch: 4, yaw: 0);

        var withGun = Shoot("frost-mag-over-gun", mag, gun, through);
        var withGunClose = Shoot("frost-mag-over-gun-close", mag, gun, close);
        var sphere = Shoot("frost-material-sphere", Sphere(plain), null, sphereCam);
        var noGlass = Shoot("frost-mag-over-gun-noglass", Model(plain, FrostModel, noGlass: true), gun, through);
        var noGlassClose = Shoot("frost-mag-over-gun-close-noglass", Model(plain, FrostModel, noGlass: true), gun, close);
        var noSphere = Shoot("frost-material-sphere-noglass", Sphere(plain, hidden: true), null, sphereCam, empty: true);
        var debug6Data = Data("debug6", 6);
        var debug6 = Shoot("frost-mag-over-gun-debug6", Model(debug6Data, FrostModel), Model(debug6Data, FrostGun), through);
        var debug6Sphere = Shoot("frost-material-sphere-debug6", Sphere(debug6Data), null, sphereCam);
        var debug7Data = Data("debug7", 7);
        var debug7 = Shoot("frost-mag-over-gun-debug7", Model(debug7Data, FrostModel), Model(debug7Data, FrostGun), through);
        var debug7Close = Shoot("frost-mag-over-gun-close-debug7", Model(debug7Data, FrostModel), Model(debug7Data, FrostGun), close);

        if (withGun is null || withGunClose is null || sphere is null || noGlass is null || noGlassClose is null || noSphere is null
            || debug6 is null || debug6Sphere is null || debug7 is null || debug7Close is null)
        {
            Check("custom shader: every frame settled", false);
            return;
        }
        var dPlain = Diff(withGun, noGlass);
        var d6 = Diff(debug6, noGlass);
        var d6Sphere = Diff(debug6Sphere, noSphere);
        var d7 = Diff(debug7, noGlass);
        var d7Close = Diff(debug7Close, noGlassClose);
        Console.WriteLine($"info  custom shader: against the frame without the glass: the glass {dPlain}; debug 6 {d6}; " +
                          $"debug 6 sphere {d6Sphere}; debug 7 {d7}; debug 7 close {d7Close}");
        Check("custom shader: the glass changes the frame", dPlain.Changed > 2000);
        // A few pixels on a silhouette behind the glass may go past it: the glass samples the scene copy at the pixel's
        // centre, where the frame without it has that edge anti-aliased.
        Check("custom shader: debug view 6 (raw scene) vanishes into what is behind it, on the mag and on the sphere " +
              "(within the OIT fragment's 8-bit mantissa: ≤ 2/255, but for a few edge pixels)",
            d6.Over2 <= 32 && d6Sphere.Max <= 2 && d6.Changed < dPlain.Changed / 4 && d6Sphere.Mean < 0.05);
        Check("custom shader: debug view 7 (blurred scene) differs from the raw scene behind", d7Close.Changed > 1000 && d7Close.Max > 2);
        Console.WriteLine($"info  custom shader: frames in {shots}");
    }

    private sealed record FrameDiff(int Changed, int Max, double Mean, int Over2)
    {
        public override string ToString() => $"{Changed:N0} pixels differ ({Over2:N0} by more than 2/255), max {Max}/255, mean {Mean:F4}/255";
    }

    private static FrameDiff Diff(byte[] a, byte[] b)
    {
        int changed = 0, max = 0, over2 = 0;
        long sum = 0;
        for (int i = 0; i < a.Length; i += 4)
        {
            int m = 0;
            for (int c = 0; c < 3; c++)
                m = Math.Max(m, Math.Abs(a[i + c] - b[i + c]));
            if (m > 0)
                changed++;
            if (m > 2)
                over2++;
            max = Math.Max(max, m);
            sum += m;
        }
        return new FrameDiff(changed, max, sum / (double)(a.Length / 4), over2);
    }

    private static PreviewCamera Orbit(Vector3 target, float distance, float pitch, float yaw)
    {
        var camera = new PreviewCamera(Vector3.Zero, pitch, yaw);
        camera.GetAxes(out var forward, out _, out _);
        return camera with { Position = target - forward * distance };
    }

    /// <summary>The value of one field of one asset in GDT text replaced (the field must be present).</summary>
    private static string SetMaterialField(string gdt, string asset, string field, string value)
    {
        int start = gdt.IndexOf($"\"{asset}\" (", StringComparison.Ordinal);
        int end = gdt.IndexOf('}', start);
        var block = gdt[start..end];
        var replaced = Regex.Replace(block, $"(\"{Regex.Escape(field)}\"\\s+)\"[^\"]*\"", m => m.Groups[1].Value + $"\"{value}\"");
        if (replaced == block)
            throw new InvalidDataException($"{asset} has no {field}");
        return gdt[..start] + replaced + gdt[end..];
    }

    /// <summary>A bind pose of the model with another model drawn with it, unposed.</summary>
    private sealed class StillPose(PreparedPreviewModel model, PreparedPreviewModel attached) : IPreviewPose
    {
        private sealed class Bare(PreparedPreviewModel model) : IPreviewPose
        {
            public PreparedPreviewModel? Model => model;
            public bool Animates(int lod) => false;
            public ReadOnlyMemory<byte> Bones(int lod) => ReadOnlyMemory<byte>.Empty;
            public event Action? Changed { add { } remove { } }
        }

        private readonly IReadOnlyList<IPreviewPose> _attached = [new Bare(attached)];

        public PreparedPreviewModel? Model => model;
        public bool Animates(int lod) => false;
        public ReadOnlyMemory<byte> Bones(int lod) => ReadOnlyMemory<byte>.Empty;
        public event Action? Changed { add { } remove { } }
        public IReadOnlyList<IPreviewPose> Attached => _attached;
    }
}
