using System.Collections.Concurrent;
using Apex.Render.Data.Assets;
using Apex.Render.Data.Conversion;
using Apex.Render.Data.Conversion.Images;
using Apex.Render.Data.Gdt;
using Apex.Render.Data.Shaders;
using Apex.Render.Data.Techsets;

namespace Apex.Render.Data;

/// <summary>
/// Entry point bundling the ToolsGfx data sources of one install: shader cache, techsetdefs, image cache,
/// model caches and a GDT index. All members are thread-safe and cache what they load.
/// </summary>
public sealed class ToolsGfxData
{
    private readonly Lazy<TechsetdefLibrary> _techsets;
    private readonly Lazy<IReadOnlyDictionary<string, string>> _materialDefaults;

    // Loaded cache images by their files, held weakly: every technique of a material resolves the same images, and a
    // released image (see ResolveImage) must still be free to go.
    private readonly ConcurrentDictionary<string, (string Stamp, WeakReference<CachedImage> Image)> _loaded = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="install">The install to read from.</param>
    /// <param name="gdt">Asset lookup; null = <see cref="GdtIndex.ForInstall"/> (the install's GDTs, parsed on first use).
    /// Either way <see cref="GdtIndex.ForInstallExtras"/> answers the names it does not know.</param>
    /// <param name="apexCache">Apex's own cache for converted assets; null = <see cref="ApexToolsGfxCache.Default"/>.</param>
    /// <param name="policy">Which caches to use; null = <see cref="CachePolicy.Default"/>.</param>
    public ToolsGfxData(ToolsGfxInstall install, IGdtLookup? gdt = null, ApexToolsGfxCache? apexCache = null, CachePolicy? policy = null)
    {
        Install = install;
        ApexCache = apexCache ?? ApexToolsGfxCache.Default;
        Policy = policy ?? CachePolicy.Default;
        Shaders = new ShaderCache(install);
        Images = new ImageCache(install);
        Models = new ModelCacheLocator(install, ApexCache, Policy);
        // APE resolves assets through gdtdb, which also covers texture_assets / art_assets / xanim_export GDTs; those
        // are consulted when the primary lookup misses (skin_pore_detail lives in texture_assets\images.gdt).
        Gdt = new LayeredGdtLookup(gdt ?? GdtIndex.ForInstall(install), GdtIndex.ForInstallExtras(install));
        _techsets = new Lazy<TechsetdefLibrary>(() => new TechsetdefLibrary(install));
        _materialDefaults = new Lazy<IReadOnlyDictionary<string, string>>(() => DeffileDefaults.ForType(install, "material"));
    }

    /// <summary>Opens the install found by <see cref="ToolsGfxInstall.Locate"/>; null when unavailable.</summary>
    public static ToolsGfxData? Open()
    {
        var install = ToolsGfxInstall.Locate();
        return install is { IsAvailable: true } ? new ToolsGfxData(install) : null;
    }

    public ToolsGfxInstall Install { get; }

    /// <summary>Apex's cache of assets it converted itself (conversion.md §4).</summary>
    public ApexToolsGfxCache ApexCache { get; }

    /// <summary>Which caches lookups may use (APE's, Apex's, convert on demand).</summary>
    public CachePolicy Policy { get; }

    public ShaderCache Shaders { get; }
    public TechsetdefLibrary Techsets => _techsets.Value;
    public ImageCache Images { get; }
    public ModelCacheLocator Models { get; }

    /// <summary>GDT lookups (xmodels, materials, images, SSIs).</summary>
    public IGdtLookup Gdt { get; }

    /// <summary>
    /// Defaults of the <c>material</c> GDF (<c>deffiles\material.awi</c>, <see cref="DeffileDefaults"/>): a material's
    /// GDT fields go on top of these before <see cref="MaterialEvaluator"/> runs, since GDTs omit default values.
    /// </summary>
    public IReadOnlyDictionary<string, string> MaterialDefaults => _materialDefaults.Value;

    /// <summary>
    /// Resolves a GDT file path value (e.g. an image's <c>baseImage</c>) to an existing absolute path:
    /// as given under the root, then under <c>texture_assets</c> and <c>model_export</c>. Null when missing.
    /// </summary>
    public string? ResolveSourcePath(string gdtValue)
    {
        var v = gdtValue.Trim().Trim('"').Replace('/', '\\');
        while (v.Contains(@"\\", StringComparison.Ordinal)) v = v.Replace(@"\\", @"\");
        v = v.TrimStart('\\');
        if (v.Length == 0)
            return null;
        if (Path.IsPathRooted(v))
            return File.Exists(v) ? v : null;
        foreach (var root in new[] { Install.Root, Install.TextureAssetsDir, Install.ModelExportDir })
        {
            var p = Path.Combine(root, v);
            if (File.Exists(p))
                return Path.GetFullPath(p);
        }
        return null;
    }

    /// <summary>
    /// Loads the image an evaluated texture element names: built-ins via <see cref="BuiltinImages"/>, GDT image
    /// assets via their <c>baseImage</c> source file and the image cache (cube maps from six faces).
    /// </summary>
    public ResolvedImage ResolveImage(EvaluatedTexture texture)
    {
        var name = texture.ImageName;
        if (name.Length == 0)
            return new ResolvedImage(texture, null, null, null, "No image name.");

        // Built-ins the table does not map ($reveal, $black_gloss, $occlusion, ...) are ordinary image assets of
        // texture_assets\$defaults.gdt ($default: code.gdt), resolved below like any other.
        if (texture.IsBuiltin && BuiltinImages.TryResolve(Install, name, out _, out _))
        {
            var img = BuiltinImages.Load(Images, name, texture.Class);
            BuiltinImages.TryResolve(Install, name, out var src, out _);
            return img is null
                ? new ResolvedImage(texture, null, src, null, $"Built-in image '{name}' is not mapped or not cached.")
                : new ResolvedImage(texture, img, src, CacheMatch.Exact, null);
        }

        var entry = Gdt.Find(name, "image");
        if (entry is null)
            return new ResolvedImage(texture, null, null, null, $"Image asset '{name}' not found in the GDTs.") { Missing = true };

        var baseImage = entry.Fields.GetValueOrDefault("baseImage") ?? string.Empty;
        var source = ResolveSourcePath(baseImage);
        var cls = entry.Fields.GetValueOrDefault("semantic") is { } sem && ImageClasses.FromSemantic(sem) is { } c ? c : texture.Class;
        var imageType = entry.Fields.GetValueOrDefault("imageType") ?? "Texture";
        var probe = source ?? Path.Combine(Install.Root, baseImage.Replace("\\\\", "\\"));
        // Without a source file APE cannot convert the image either (cube faces are resolved per face below).
        bool missing = source is null && !imageType.StartsWith("cube", StringComparison.OrdinalIgnoreCase);

        if (Policy.UseApeCache || Policy.UseApexCache || Policy.ConvertMissing)
        {
            if (ResolveExact(entry.Fields, baseImage, texture) is { } exactImage)
                return exactImage;
        }
        if (!Policy.UseApeCache)
            return new ResolvedImage(texture, null, source, null, $"Image '{name}' ({baseImage}) could not be converted.") { Missing = missing };

        if (imageType.StartsWith("cube", StringComparison.OrdinalIgnoreCase))
        {
            var cube = Images.LoadCubeFromSource(probe, cls);
            return cube is null
                ? new ResolvedImage(texture, null, source, null, $"Cube image '{name}' ({baseImage}) is not in the image cache.")
                : new ResolvedImage(texture, cube, source, CacheMatch.Exact, null);
        }

        var hit = Images.Find(probe, cls, out bool exact);
        if (hit is null)
            return new ResolvedImage(texture, null, source, null, $"Image '{name}' ({baseImage}) is not in the image cache; open it in APE once to convert it.") { Missing = missing };
        return new ResolvedImage(texture, Load([hit.Path]), source, exact ? CacheMatch.Exact : CacheMatch.ByName, null);
    }

    /// <summary>
    /// <see cref="ImageCache.LoadFile"/> (one path) or <see cref="ImageCache.LoadCube(IReadOnlyList{string})"/> (six),
    /// sharing the image with earlier callers while it is still alive and its files are unchanged. A face whose payload
    /// was just converted (<paramref name="payloads"/>) is parsed from memory instead of read back.
    /// </summary>
    private CachedImage Load(string[] paths, byte[]?[]? payloads = null)
    {
        var key = string.Join('|', paths);
        var stamp = string.Join('|', paths.Select(p => new FileInfo(p) is var f && f.Exists ? $"{f.LastWriteTimeUtc.Ticks}:{f.Length}" : "-"));
        if (_loaded.TryGetValue(key, out var e) && e.Stamp == stamp && e.Image.TryGetTarget(out var shared))
            return shared;
        var img = paths.Length == 6 ? ImageCache.LoadCube(paths, payloads)
            : payloads?[0] is { } payload ? ImageCache.FromPayload(payload, paths[0]) : ImageCache.LoadFile(paths[0]);
        _loaded[key] = (stamp, new WeakReference<CachedImage>(img));
        return img;
    }

    /// <summary>
    /// The image exactly as APE would convert it (conversion.md §2/§4): settings from the GDT entry, per face the file
    /// <c>&lt;settingsDir&gt;\&lt;src&gt;_&lt;face&gt;_&lt;Sig(src)&gt;</c> from APE's cache, else Apex's cache, else converted now into
    /// Apex's cache. Null when the settings or a source file are unusable, or conversion failed (callers then fall
    /// back to a name-only match in APE's cache).
    /// </summary>
    private ResolvedImage? ResolveExact(IReadOnlyDictionary<string, string> fields, string baseImage, EvaluatedTexture texture)
    {
        ImageConversionSettings settings;
        try
        {
            settings = ImageConversionSettings.FromGdt(fields);
        }
        catch (NotSupportedException)
        {
            return null;
        }
        var cut = baseImage.Split(',')[0];
        var source = ResolveSourcePath(cut);
        if (source is null)
            return null;
        bool cube = settings.ImageType == ToolsGfxImageType.Cube;
        int faces = cube ? 6 : 1;
        var paths = new string[faces];
        var payloads = new byte[]?[faces];
        var origin = CacheOrigin.Ape;
        try
        {
            // A cube's faces are independent (six sources to sign and maybe convert): resolve them in parallel, then
            // take the results in face order so the first failing face decides, as when done one by one.
            var results = new Task<(string Path, CacheOrigin Origin, byte[]? Payload)?>[faces];
            for (int face = 0; face < faces; face++)
            {
                int f = face;
                results[f] = cube ? Task.Run(() => ResolveFace(f)) : Task.FromResult(ResolveFace(f));
            }
            for (int face = 0; face < faces; face++)
            {
                if (results[face].GetAwaiter().GetResult() is not { } r)
                    return null;
                paths[face] = r.Path;
                payloads[face] = r.Payload;
                var o = r.Origin;
                if (o == CacheOrigin.ApexConverted)
                    origin = o;
                else if (origin == CacheOrigin.Ape)
                    origin = o;
            }
            return new ResolvedImage(texture, Load(paths, payloads), source, CacheMatch.Exact, null) { Origin = origin };
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or InvalidOperationException
                                       or ArgumentException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException
                                       or DllNotFoundException or EntryPointNotFoundException or IndexOutOfRangeException)
        {
            return null;
        }

        // The face's cache file, where it came from and, when converted just now, its payload; null when its source is
        // missing or it may not be converted.
        (string Path, CacheOrigin Origin, byte[]? Payload)? ResolveFace(int face)
        {
            var src = cube ? ImageConverter.CubeFacePath(source, face) : source;
            if (src is null || !File.Exists(src))
                return null;
            var rel = ImageConverter.CacheRelativePath(src, settings, face);
            var ape = Path.Combine(Install.ImageCacheDir, rel);
            if (Policy.UseApeCache && File.Exists(ape) && new FileInfo(ape).Length > 4)
                return (ape, CacheOrigin.Ape, null);
            var apex = Path.Combine(ApexCache.ImageCacheDir, rel);
            var label = $"{Path.GetFileName(src)} ({settings.SettingsDirectory})";
            if (Policy.UseApexCache && File.Exists(apex) && new FileInfo(apex).Length > 4)
            {
                ConversionScope.Report(new ConversionEvent(label, apex, CacheOrigin.ApexCache, 0, null));
                return (apex, CacheOrigin.ApexCache, null);
            }
            if (!Policy.ConvertMissing)
                return null;
            var path = ApexCache.GetOrConvert(apex, label, () => ImageConverter.ConvertFace(Install, src, settings, face), out var o,
                out var payload, reconvert: !Policy.UseApexCache);
            return (path, o, payload);
        }
    }
}


/// <summary>Result of <see cref="ToolsGfxData.ResolveImage"/>.</summary>
/// <param name="Texture">The texture element the image is for.</param>
/// <param name="Image">The loaded image, or null on failure.</param>
/// <param name="SourcePath">Source file the cache entry was matched against, when known.</param>
/// <param name="Match">Whether the cache file matched the current source hash.</param>
/// <param name="Error">Why loading failed.</param>
public sealed record ResolvedImage(EvaluatedTexture Texture, CachedImage? Image, string? SourcePath, CacheMatch? Match, string? Error)
{
    /// <summary>APE's cache, Apex's cache, or converted by Apex just now (<see cref="CacheOrigin"/>).</summary>
    public CacheOrigin Origin { get; init; } = CacheOrigin.Ape;

    /// <summary>APE could not load the image either (no GDT entry, or no source file to convert): it then fails the
    /// material (<c>Material_ResolveTextures</c>, "Failed to load image '%s' for material '%s'").</summary>
    public bool Missing { get; init; }
}
