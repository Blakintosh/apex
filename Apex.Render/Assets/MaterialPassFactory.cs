using Apex.Render.Data;
using Apex.Render.Data.Assets;
using Apex.Render.Data.Techsets;
using Apex.Render.Passes;
using Apex.Render.Resources;
using Apex.Render.Shaders;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using D = Apex.Render.Data.Techsets;
using DataStage = Apex.Render.Data.Shaders.ShaderStage;

namespace Apex.Render.Assets;

/// <summary>
/// Turns the data layer's bound techniques (<see cref="TechniqueBinder"/>) into <see cref="MaterialPass"/>es:
/// creates the cached VS/PS variants on the device, uploads per-stage <c>$Globals</c>, resolves and uploads images
/// (deduplicated by cache file across every factory of the device), creates samplers and the techsetdef <c>State</c>
/// as D3D11 objects. Creates device objects only (never touches the immediate context), so a factory can build on a
/// worker thread; one factory is used by one thread at a time.
/// </summary>
public sealed class MaterialPassFactory : IDisposable
{
    private readonly RenderContext _rc;
    private readonly ToolsGfxData _data;
    private readonly DeviceImageCache _imageCache;
    private readonly Dictionary<string, GpuTexture> _images = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<D.SamplerDescription, ID3D11SamplerState> _samplers = new();
    private readonly List<IDisposable> _owned = new();

    public MaterialPassFactory(RenderContext rc, ToolsGfxData data)
    {
        _rc = rc;
        _data = data;
        _imageCache = rc.Shared(_ => new DeviceImageCache());
    }

    /// <summary>Problems met while building passes (missing images, binder errors).</summary>
    public List<string> Warnings { get; } = new();

    /// <summary>The pass for <paramref name="technique"/> of an evaluated material, or null when absent.</summary>
    public MaterialPass? Create(EvaluatedMaterial material, TechniqueDefinition? technique)
        => technique is null ? null : Create(Prepare(_data, material, technique));

    /// <summary>
    /// The CPU half of <see cref="Create(EvaluatedMaterial, TechniqueDefinition?)"/>: binds the technique's stages
    /// (cached DXBC, <c>$Globals</c> bytes) and loads every image from the image cache. Touches no device, so it can
    /// run on a worker thread; <see cref="Create(PreparedPass)"/> then only creates GPU objects.
    /// </summary>
    public static PreparedPass Prepare(ToolsGfxData data, EvaluatedMaterial material, TechniqueDefinition technique,
        CancellationToken cancellationToken = default)
    {
        var stages = TechniqueBinder.Bind(data.Shaders, technique, material);
        var textures = stages.SelectMany(st => st.Textures).ToList();
        // Images load in parallel; warnings and bindings keep the stage/texture order.
        var resolved = new ResolvedImage[textures.Count];
        PreviewModelLoader.ForEachInOrder(textures.Count, cancellationToken, i => resolved[i] = data.ResolveImage(textures[i].Texture));

        var warnings = new List<string>();
        var images = new List<PreparedImage>();
        var missing = new List<string>();
        foreach (var st in stages)
        {
            foreach (var e in st.Errors)
                warnings.Add($"{technique.Name} {st.Stage.Key}: {e}");
            foreach (var t in st.Textures)
            {
                var r = resolved[images.Count];
                // An empty slot (e.g. an unused tintMask) stays unbound, as in APE; only real misses are reported.
                if (r.Image is null && t.Texture.ImageName.Length > 0)
                    warnings.Add($"{t.Texture.Name} = {t.Texture.ImageName}: {r.Error}");
                if (r.Missing)
                    missing.Add(t.Texture.ImageName);
                images.Add(new PreparedImage(t.Binding.Name, t.Texture, r.Image));
            }
        }
        return new PreparedPass(technique, stages, images, warnings) { MissingImages = missing };
    }

    /// <summary>GPU objects for a <see cref="Prepare"/>d pass (call on the thread that owns the device context).</summary>
    public MaterialPass Create(PreparedPass prepared)
    {
        var technique = prepared.Technique;
        Warnings.AddRange(prepared.Warnings);
        ShaderProgram? vs = null, ps = null;
        var globals = new Dictionary<ShaderStage, ID3D11Buffer>();
        var textures = new Dictionary<string, ID3D11ShaderResourceView>();
        var samplers = new Dictionary<string, ID3D11SamplerState>();
        foreach (var st in prepared.Stages)
        {
            var program = _rc.Programs.GetOrCreate($"{st.Binary.Key.CacheFolder}/{st.Binary.Key.EntryPoint}/{st.Binary.CodeKey}", () => st.Binary.Dxbc);
            switch (st.Stage.Stage)
            {
                case DataStage.Vertex: vs = program; break;
                case DataStage.Pixel: ps = program; break;
                default: Warnings.Add($"{technique.Name}: {st.Stage.Stage} stage ignored"); break;
            }
            if (st.MaterialConstants is { } bytes)
            {
                var cb = GpuBuffer.CreateImmutableConstant(_rc.Gfx, bytes, $"{technique.Name} {st.Stage.Stage} $Globals");
                _owned.Add(cb);
                globals[program.Stage] = cb.Buffer;
            }
            foreach (var s in st.Samplers)
                samplers[s.Binding.Name] = Sampler(s.Sampler.Description);
        }
        // Released image data (PreparedPreviewModel.ReleaseImageData) is read from the image cache again, in parallel.
        var images = new CachedImage?[prepared.Images.Count];
        PreviewModelLoader.ForEachInOrder(images.Length, default, i =>
        {
            var img = prepared.Images[i];
            images[i] = img.Image ?? (img.IsReleased ? _data.ResolveImage(img.Texture).Image : null);
        });
        for (int i = 0; i < images.Length; i++)
        {
            var img = prepared.Images[i];
            if (images[i] is { } image)
                textures[img.BindingName] = Image(image, img.Texture.ImageName).Srv!;
        }
        if (vs is null)
            throw new InvalidDataException($"technique '{technique.Name}' has no vertex shader");
        return new MaterialPass
        {
            VertexShader = vs,
            PixelShader = ps,
            StageGlobals = globals,
            Textures = textures,
            Samplers = samplers,
            State = technique.State is { } state ? State(state) : null,
        };
    }

    private GpuTexture Image(CachedImage image, string name)
    {
        var key = string.Join("|", image.CachePaths);
        if (_images.TryGetValue(key, out var cached))
            return cached;
        var gpu = _imageCache.Acquire(key, () => GpuTexture.FromData(_rc.Gfx, ToTextureData(image), debugName: name));
        _images[key] = gpu;
        return gpu;
    }

    /// <summary>Image-cache image → upload layout (faces × mips).</summary>
    public static TextureData ToTextureData(CachedImage image)
    {
        var subs = new List<ReadOnlyMemory<byte>>();
        foreach (var face in image.Faces)
            foreach (var level in face)
                subs.Add(level.Data);
        return new TextureData
        {
            Format = (Format)image.DxgiFormat,
            Width = image.Width,
            Height = image.Height,
            ArraySize = image.ArraySize,
            MipLevels = image.MipCount,
            Kind = image.IsCube ? TextureKind.TextureCube : image.ArraySize > 1 ? TextureKind.Texture2DArray : TextureKind.Texture2D,
            Subresources = subs,
        };
    }

    private ID3D11SamplerState Sampler(D.SamplerDescription d)
    {
        if (_samplers.TryGetValue(d, out var s))
            return s;
        s = _rc.Gfx.Device.CreateSamplerState(new Vortice.Direct3D11.SamplerDescription
        {
            Filter = (Filter)(int)d.Filter,
            AddressU = (Vortice.Direct3D11.TextureAddressMode)(int)d.AddressU,
            AddressV = (Vortice.Direct3D11.TextureAddressMode)(int)d.AddressV,
            AddressW = (Vortice.Direct3D11.TextureAddressMode)(int)d.AddressW,
            MipLODBias = d.MipLodBias,
            MaxAnisotropy = (uint)d.MaxAnisotropy,
            ComparisonFunc = (Vortice.Direct3D11.ComparisonFunction)(int)d.ComparisonFunction,
            BorderColor = new Color4(d.BorderR, d.BorderG, d.BorderB, d.BorderA),
            MinLOD = d.MinLod,
            MaxLOD = d.MaxLod,
        });
        _samplers[d] = s;
        return s;
    }

    /// <summary>Compiled techsetdef State → D3D11 state objects.</summary>
    public MaterialState State(StateDescription d)
    {
        var bd = new Vortice.Direct3D11.BlendDescription
        {
            AlphaToCoverageEnable = d.Blend.AlphaToCoverage,
            IndependentBlendEnable = d.Blend.IndependentBlend,
        };
        for (int i = 0; i < 8 && i < d.Blend.RenderTargets.Count; i++)
        {
            var rt = d.Blend.RenderTargets[i];
            bd.RenderTarget[i] = new RenderTargetBlendDescription
            {
                BlendEnable = rt.BlendEnable,
                SourceBlend = (Blend)(int)rt.SrcBlend,
                DestinationBlend = (Blend)(int)rt.DestBlend,
                BlendOperation = (Vortice.Direct3D11.BlendOperation)(int)rt.BlendOp,
                SourceBlendAlpha = (Blend)(int)rt.SrcBlendAlpha,
                DestinationBlendAlpha = (Blend)(int)rt.DestBlendAlpha,
                BlendOperationAlpha = (Vortice.Direct3D11.BlendOperation)(int)rt.BlendOpAlpha,
                RenderTargetWriteMask = (ColorWriteEnable)(int)rt.WriteMask,
            };
        }
        static DepthStencilOperationDescription Face(StencilFaceDescription f) => new()
        {
            StencilFailOp = (Vortice.Direct3D11.StencilOperation)(int)f.FailOp,
            StencilDepthFailOp = (Vortice.Direct3D11.StencilOperation)(int)f.DepthFailOp,
            StencilPassOp = (Vortice.Direct3D11.StencilOperation)(int)f.PassOp,
            StencilFunc = (Vortice.Direct3D11.ComparisonFunction)(int)f.Function,
        };
        var ds = d.DepthStencil;
        var dsd = new Vortice.Direct3D11.DepthStencilDescription
        {
            DepthEnable = ds.DepthEnable,
            DepthWriteMask = ds.DepthWrite ? DepthWriteMask.All : DepthWriteMask.Zero,
            DepthFunc = (Vortice.Direct3D11.ComparisonFunction)(int)ds.DepthFunction,
            StencilEnable = ds.StencilEnable,
            StencilReadMask = ds.StencilReadMask,
            StencilWriteMask = ds.StencilWriteMask,
            FrontFace = Face(ds.FrontFace),
            BackFace = Face(ds.BackFace),
        };
        var r = d.Rasterizer;
        var rd = new Vortice.Direct3D11.RasterizerDescription
        {
            FillMode = (Vortice.Direct3D11.FillMode)(int)r.FillMode,
            CullMode = (Vortice.Direct3D11.CullMode)(int)r.CullMode,
            FrontCounterClockwise = r.FrontCounterClockwise,
            DepthBias = r.DepthBias,
            DepthBiasClamp = r.DepthBiasClamp,
            SlopeScaledDepthBias = r.SlopeScaledDepthBias,
            DepthClipEnable = r.DepthClipEnable,
            ScissorEnable = r.ScissorEnable,
            MultisampleEnable = r.MultisampleEnable,
            AntialiasedLineEnable = r.AntialiasedLineEnable,
        };
        var blend = _rc.Gfx.Device.CreateBlendState(bd);
        var depth = _rc.Gfx.Device.CreateDepthStencilState(dsd);
        var raster = _rc.Gfx.Device.CreateRasterizerState(rd);
        _owned.Add(blend);
        _owned.Add(depth);
        _owned.Add(raster);
        return new MaterialState
        {
            BlendObject = blend,
            DepthStencilObject = depth,
            RasterizerObject = raster,
            StencilRef = (byte)d.StencilRef,
        };
    }

    public void Dispose()
    {
        foreach (var key in _images.Keys) _imageCache.Release(key);
        foreach (var s in _samplers.Values) s.Dispose();
        foreach (var o in _owned) o.Dispose();
        _images.Clear();
        _samplers.Clear();
        _owned.Clear();
    }
}

/// <summary>
/// The loaded images of one device, shared by every <see cref="MaterialPassFactory"/> on it (models using the same
/// image-cache files upload them once) and reference counted: a texture goes with the last factory using it.
/// Thread-safe.
/// </summary>
internal sealed class DeviceImageCache : IDisposable
{
    private sealed class Entry(GpuTexture texture)
    {
        public readonly GpuTexture Texture = texture;
        public int Refs;
    }

    private readonly Dictionary<string, Entry> _images = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The texture of <paramref name="key"/> (the image's cache files), created on first use.</summary>
    public GpuTexture Acquire(string key, Func<GpuTexture> create)
    {
        lock (_images)
        {
            if (!_images.TryGetValue(key, out var e))
                _images[key] = e = new Entry(create());
            e.Refs++;
            return e.Texture;
        }
    }

    public void Release(string key)
    {
        lock (_images)
        {
            if (!_images.TryGetValue(key, out var e) || --e.Refs > 0)
                return;
            _images.Remove(key);
            e.Texture.Dispose();
        }
    }

    public void Dispose()
    {
        lock (_images)
        {
            foreach (var e in _images.Values)
                e.Texture.Dispose();
            _images.Clear();
        }
    }
}

/// <summary>An image of a <see cref="PreparedPass"/>: the shader binding it goes to and the loaded cache image
/// (null when it could not be resolved; the binding then stays empty).</summary>
public sealed class PreparedImage(string bindingName, EvaluatedTexture texture, CachedImage? image)
{
    public string BindingName { get; } = bindingName;
    public EvaluatedTexture Texture { get; } = texture;
    /// <summary>The loaded image; null when it could not be resolved or after <see cref="Release"/>.</summary>
    public CachedImage? Image => Volatile.Read(ref _image);
    /// <summary>The data was dropped after upload; <see cref="MaterialPassFactory.Create(PreparedPass)"/> reloads it.</summary>
    public bool IsReleased => Volatile.Read(ref _released);

    private CachedImage? _image = image;
    private bool _released;

    // A shared prepared model can be released on the UI thread while a worker creates it for another device: the flag
    // is published before the image is dropped, so a reader that sees no image also sees the flag.
    internal void Release()
    {
        if (Image is null)
            return;
        Volatile.Write(ref _released, true);
        Volatile.Write(ref _image, null);
    }
}

/// <summary>A material technique bound and loaded on the CPU (<see cref="MaterialPassFactory.Prepare"/>), ready for
/// <see cref="MaterialPassFactory.Create(PreparedPass)"/>.</summary>
public sealed record PreparedPass(
    TechniqueDefinition Technique,
    IReadOnlyList<StageBinding> Stages,
    IReadOnlyList<PreparedImage> Images,
    IReadOnlyList<string> Warnings)
{
    /// <summary>Images APE could not load either (<see cref="ResolvedImage.Missing"/>); APE then fails the material.</summary>
    public IReadOnlyList<string> MissingImages { get; init; } = [];
}
