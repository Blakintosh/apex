using System.Buffers;
using Apex.Render.Device;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Apex.Render.Resources;

/// <summary>
/// A texture plus the views ToolsGfx needs on it. Render targets follow the engine's table convention
/// (typeless storage + separate RTV/SRV/UAV formats, see <c>RenderTargetTable</c>); loaded images are immutable.
/// </summary>
public sealed class GpuTexture : IDisposable
{
    public ID3D11Resource Resource { get; }
    public ID3D11ShaderResourceView? Srv { get; }
    public ID3D11RenderTargetView? Rtv { get; }
    public ID3D11UnorderedAccessView? Uav { get; }
    public ID3D11DepthStencilView? Dsv { get; }
    public TextureKind Kind { get; }
    public Format StorageFormat { get; }
    public int Width { get; }
    public int Height { get; }
    public int DepthOrArraySize { get; }
    public int MipLevels { get; }
    public string? DebugName { get; }

    private GpuTexture(ID3D11Resource resource, TextureKind kind, Format storageFormat, int width, int height,
        int depthOrArraySize, int mipLevels, ID3D11ShaderResourceView? srv, ID3D11RenderTargetView? rtv,
        ID3D11UnorderedAccessView? uav, ID3D11DepthStencilView? dsv, string? debugName)
    {
        Resource = resource;
        Kind = kind;
        StorageFormat = storageFormat;
        Width = width;
        Height = height;
        DepthOrArraySize = depthOrArraySize;
        MipLevels = mipLevels;
        Srv = srv;
        Rtv = rtv;
        Uav = uav;
        Dsv = dsv;
        DebugName = debugName;
    }

    // ── Loaded (immutable) textures ─────────────────────────────────────────

    /// <summary>Uploads <paramref name="data"/> as an immutable texture with an SRV of
    /// <paramref name="srvFormat"/> (default: the data's format; pass the _SRGB variant for colour images).</summary>
    public static GpuTexture FromData(GfxDevice gfx, TextureData data, Format? srvFormat = null, string? debugName = null,
        BindFlags extraBind = BindFlags.None)
    {
        var viewFormat = srvFormat ?? data.Format;
        var storage = viewFormat != data.Format ? FormatInfo.ToTypeless(data.Format) : data.Format;
        var usage = extraBind == BindFlags.None ? ResourceUsage.Immutable : ResourceUsage.Default;
        var bind = BindFlags.ShaderResource | extraBind;

        var handles = new MemoryHandle[data.Subresources.Count];
        var init = new SubresourceData[data.Subresources.Count];
        try
        {
            int index = 0;
            int slices = data.Kind == TextureKind.Texture3D ? 1 : data.ArraySize;
            for (int s = 0; s < slices; s++)
            {
                for (int m = 0; m < data.MipLevels; m++, index++)
                {
                    var (row, slice, _) = FormatInfo.Pitch(data.Format, FormatInfo.MipDimension(data.Width, m), FormatInfo.MipDimension(data.Height, m));
                    handles[index] = data.Subresources[index].Pin();
                    unsafe { init[index] = new SubresourceData((IntPtr)handles[index].Pointer, (uint)row, (uint)slice); }
                }
            }

            ID3D11Resource resource;
            if (data.Kind == TextureKind.Texture3D)
            {
                resource = gfx.Device.CreateTexture3D(new Texture3DDescription
                {
                    Width = (uint)data.Width, Height = (uint)data.Height, Depth = (uint)data.Depth,
                    MipLevels = (uint)data.MipLevels, Format = storage, Usage = usage, BindFlags = bind,
                }, init);
            }
            else
            {
                resource = gfx.Device.CreateTexture2D(new Texture2DDescription
                {
                    Width = (uint)data.Width, Height = (uint)data.Height, ArraySize = (uint)data.ArraySize,
                    MipLevels = (uint)data.MipLevels, Format = storage, Usage = usage, BindFlags = bind,
                    SampleDescription = new SampleDescription(1, 0),
                    MiscFlags = data.Kind == TextureKind.TextureCube ? ResourceOptionFlags.TextureCube : ResourceOptionFlags.None,
                }, init);
            }
            SetName(resource, debugName);

            var srv = gfx.Device.CreateShaderResourceView(resource, SrvDesc(data.Kind, viewFormat, data.MipLevels,
                data.Kind == TextureKind.Texture3D ? 1 : data.ArraySize));
            return new GpuTexture(resource, data.Kind, storage, data.Width, data.Height,
                data.Kind == TextureKind.Texture3D ? data.Depth : data.ArraySize, data.MipLevels, srv, null, null, null, debugName);
        }
        finally
        {
            foreach (var h in handles)
                h.Dispose();
        }
    }

    // ── Render targets / UAV targets ────────────────────────────────────────

    /// <summary>Creates a GPU-written texture. Any of the view formats may be <see cref="Format.Unknown"/> to skip that view.</summary>
    public static GpuTexture CreateTarget(GfxDevice gfx, in TargetDesc desc, string? debugName = null)
    {
        var bind = BindFlags.None;
        if (desc.SrvFormat != Format.Unknown) bind |= BindFlags.ShaderResource;
        if (desc.RtvFormat != Format.Unknown) bind |= BindFlags.RenderTarget;
        if (desc.UavFormat != Format.Unknown) bind |= BindFlags.UnorderedAccess;
        if (desc.DsvFormat != Format.Unknown) bind |= BindFlags.DepthStencil;

        ID3D11Resource resource;
        if (desc.Kind == TextureKind.Texture3D)
        {
            resource = gfx.Device.CreateTexture3D(new Texture3DDescription
            {
                Width = (uint)desc.Width, Height = (uint)desc.Height, Depth = (uint)desc.DepthOrArraySize,
                MipLevels = (uint)desc.MipLevels, Format = desc.TextureFormat, Usage = ResourceUsage.Default, BindFlags = bind,
            });
        }
        else
        {
            resource = gfx.Device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)desc.Width, Height = (uint)desc.Height, ArraySize = (uint)desc.DepthOrArraySize,
                MipLevels = (uint)desc.MipLevels, Format = desc.TextureFormat, Usage = ResourceUsage.Default, BindFlags = bind,
                SampleDescription = new SampleDescription(1, 0),
                MiscFlags = (desc.Kind == TextureKind.TextureCube ? ResourceOptionFlags.TextureCube : ResourceOptionFlags.None) | desc.MiscFlags,
            });
        }
        SetName(resource, debugName);

        ID3D11ShaderResourceView? srv = null;
        ID3D11RenderTargetView? rtv = null;
        ID3D11UnorderedAccessView? uav = null;
        ID3D11DepthStencilView? dsv = null;
        if (desc.SrvFormat != Format.Unknown)
            srv = gfx.Device.CreateShaderResourceView(resource, SrvDesc(desc.Kind, desc.SrvFormat, desc.MipLevels, desc.DepthOrArraySize));
        if (desc.RtvFormat != Format.Unknown)
            rtv = gfx.Device.CreateRenderTargetView(resource, RtvDesc(desc.Kind, desc.RtvFormat, desc.DepthOrArraySize));
        if (desc.UavFormat != Format.Unknown)
            uav = gfx.Device.CreateUnorderedAccessView(resource, UavDesc(desc.Kind, desc.UavFormat, desc.DepthOrArraySize));
        if (desc.DsvFormat != Format.Unknown)
        {
            dsv = gfx.Device.CreateDepthStencilView(resource, new DepthStencilViewDescription
            {
                Format = desc.DsvFormat,
                ViewDimension = DepthStencilViewDimension.Texture2D,
            });
        }
        return new GpuTexture(resource, desc.Kind, desc.TextureFormat, desc.Width, desc.Height, desc.DepthOrArraySize,
            desc.MipLevels, srv, rtv, uav, dsv, debugName);
    }

    /// <summary>Takes ownership of a created 2D texture and adds the requested views.</summary>
    public static GpuTexture Wrap(GfxDevice gfx, ID3D11Texture2D texture, Format srvFormat = Format.Unknown, Format rtvFormat = Format.Unknown,
        Format dsvFormat = Format.Unknown, string? debugName = null)
    {
        var d = texture.Description;
        var kind = (d.MiscFlags & ResourceOptionFlags.TextureCube) != 0 ? TextureKind.TextureCube : d.ArraySize > 1 ? TextureKind.Texture2DArray : TextureKind.Texture2D;
        var srv = srvFormat != Format.Unknown ? gfx.Device.CreateShaderResourceView(texture, SrvDesc(kind, srvFormat, (int)d.MipLevels, (int)d.ArraySize)) : null;
        var rtv = rtvFormat != Format.Unknown ? gfx.Device.CreateRenderTargetView(texture, RtvDesc(kind, rtvFormat, (int)d.ArraySize)) : null;
        var dsv = dsvFormat != Format.Unknown
            ? gfx.Device.CreateDepthStencilView(texture, new DepthStencilViewDescription { Format = dsvFormat, ViewDimension = DepthStencilViewDimension.Texture2D })
            : null;
        return new GpuTexture(texture, kind, d.Format, (int)d.Width, (int)d.Height, (int)d.ArraySize, (int)d.MipLevels, srv, rtv, null, dsv, debugName);
    }

    /// <summary>Wraps an externally created 2D texture (e.g. a shared swap image) with an RTV.</summary>
    public static GpuTexture WrapRenderTarget(GfxDevice gfx, ID3D11Texture2D texture, Format rtvFormat, string? debugName = null)
    {
        var d = texture.Description;
        texture.AddRef();
        var rtv = gfx.Device.CreateRenderTargetView(texture, RtvDesc(TextureKind.Texture2D, rtvFormat, 1));
        return new GpuTexture(texture, TextureKind.Texture2D, d.Format, (int)d.Width, (int)d.Height, 1, 1, null, rtv, null, null, debugName);
    }

    private static ShaderResourceViewDescription SrvDesc(TextureKind kind, Format format, int mips, int depthOrArray)
    {
        var d = new ShaderResourceViewDescription { Format = format };
        switch (kind)
        {
            case TextureKind.Texture3D:
                d.ViewDimension = ShaderResourceViewDimension.Texture3D;
                d.Texture3D = new Texture3DShaderResourceView { MostDetailedMip = 0, MipLevels = (uint)mips };
                break;
            case TextureKind.TextureCube when depthOrArray > 6:
                d.ViewDimension = ShaderResourceViewDimension.TextureCubeArray;
                d.TextureCubeArray = new TextureCubeArrayShaderResourceView { MostDetailedMip = 0, MipLevels = (uint)mips, First2DArrayFace = 0, NumCubes = (uint)(depthOrArray / 6) };
                break;
            case TextureKind.TextureCube:
                d.ViewDimension = ShaderResourceViewDimension.TextureCube;
                d.TextureCube = new TextureCubeShaderResourceView { MostDetailedMip = 0, MipLevels = (uint)mips };
                break;
            case TextureKind.Texture2DArray:
                d.ViewDimension = ShaderResourceViewDimension.Texture2DArray;
                d.Texture2DArray = new Texture2DArrayShaderResourceView { MostDetailedMip = 0, MipLevels = (uint)mips, FirstArraySlice = 0, ArraySize = (uint)depthOrArray };
                break;
            default:
                d.ViewDimension = ShaderResourceViewDimension.Texture2D;
                d.Texture2D = new Texture2DShaderResourceView { MostDetailedMip = 0, MipLevels = (uint)mips };
                break;
        }
        return d;
    }

    /// <summary>SRV of a cube texture viewed as a cube <em>array</em> (ToolsGfx binds its single global probe as TextureCubeArray t51).</summary>
    public ID3D11ShaderResourceView CreateCubeArraySrv(GfxDevice gfx, Format format)
    {
        return gfx.Device.CreateShaderResourceView(Resource, new ShaderResourceViewDescription
        {
            Format = format,
            ViewDimension = ShaderResourceViewDimension.TextureCubeArray,
            TextureCubeArray = new TextureCubeArrayShaderResourceView
            {
                MostDetailedMip = 0, MipLevels = (uint)MipLevels, First2DArrayFace = 0, NumCubes = (uint)Math.Max(1, DepthOrArraySize / 6),
            },
        });
    }

    /// <summary>SRV of a 2D texture viewed as a one-slice 2D array.</summary>
    public ID3D11ShaderResourceView CreateArraySrv(GfxDevice gfx, Format format)
    {
        return gfx.Device.CreateShaderResourceView(Resource, new ShaderResourceViewDescription
        {
            Format = format,
            ViewDimension = ShaderResourceViewDimension.Texture2DArray,
            Texture2DArray = new Texture2DArrayShaderResourceView { MostDetailedMip = 0, MipLevels = (uint)MipLevels, FirstArraySlice = 0, ArraySize = (uint)DepthOrArraySize },
        });
    }

    /// <summary>RTV of one slice of a 2D array (e.g. one sun-shadow cascade of lighting RT2).</summary>
    public ID3D11RenderTargetView CreateSliceRtv(GfxDevice gfx, Format format, int slice)
    {
        return gfx.Device.CreateRenderTargetView(Resource, new RenderTargetViewDescription
        {
            Format = format,
            ViewDimension = RenderTargetViewDimension.Texture2DArray,
            Texture2DArray = new Texture2DArrayRenderTargetView { MipSlice = 0, FirstArraySlice = (uint)slice, ArraySize = 1 },
        });
    }

    /// <summary>Read-only (depth + stencil) DSV of a 2D depth target, so the same resource can be sampled while its
    /// stencil is tested (ToolsGfx's SSS blur binds DS0 this way).</summary>
    public ID3D11DepthStencilView CreateReadOnlyDsv(GfxDevice gfx, Format dsvFormat)
        => gfx.Device.CreateDepthStencilView(Resource, new DepthStencilViewDescription
        {
            Format = dsvFormat,
            ViewDimension = DepthStencilViewDimension.Texture2D,
            Flags = DepthStencilViewFlags.ReadOnlyDepth | DepthStencilViewFlags.ReadOnlyStencil,
        });

    /// <summary>Additional SRV with another format over the whole resource (same dimension as the default view).</summary>
    public ID3D11ShaderResourceView CreateSrv(GfxDevice gfx, Format format)
        => gfx.Device.CreateShaderResourceView(Resource, SrvDesc(Kind, format, MipLevels, DepthOrArraySize));

    private static RenderTargetViewDescription RtvDesc(TextureKind kind, Format format, int depthOrArray)
    {
        var d = new RenderTargetViewDescription { Format = format };
        if (kind == TextureKind.Texture3D)
        {
            d.ViewDimension = RenderTargetViewDimension.Texture3D;
            d.Texture3D = new Texture3DRenderTargetView { MipSlice = 0, FirstWSlice = 0, WSize = unchecked((uint)-1) };
        }
        else if (depthOrArray > 1)
        {
            d.ViewDimension = RenderTargetViewDimension.Texture2DArray;
            d.Texture2DArray = new Texture2DArrayRenderTargetView { MipSlice = 0, FirstArraySlice = 0, ArraySize = (uint)depthOrArray };
        }
        else
        {
            d.ViewDimension = RenderTargetViewDimension.Texture2D;
        }
        return d;
    }

    private static UnorderedAccessViewDescription UavDesc(TextureKind kind, Format format, int depthOrArray)
    {
        var d = new UnorderedAccessViewDescription { Format = format };
        if (kind == TextureKind.Texture3D)
        {
            d.ViewDimension = UnorderedAccessViewDimension.Texture3D;
            d.Texture3D = new Texture3DUnorderedAccessView { MipSlice = 0, FirstWSlice = 0, WSize = unchecked((uint)-1) };
        }
        else if (depthOrArray > 1)
        {
            d.ViewDimension = UnorderedAccessViewDimension.Texture2DArray;
            d.Texture2DArray = new Texture2DArrayUnorderedAccessView { MipSlice = 0, FirstArraySlice = 0, ArraySize = (uint)depthOrArray };
        }
        else
        {
            d.ViewDimension = UnorderedAccessViewDimension.Texture2D;
        }
        return d;
    }

    internal static void SetName(ID3D11DeviceChild child, string? name)
    {
        if (!string.IsNullOrEmpty(name))
            child.DebugName = name;
    }

    public void Dispose()
    {
        Srv?.Dispose();
        Rtv?.Dispose();
        Uav?.Dispose();
        Dsv?.Dispose();
        Resource.Dispose();
    }
}

/// <summary>Creation parameters for a GPU-written texture.</summary>
public readonly record struct TargetDesc(
    Format TextureFormat,
    int Width,
    int Height,
    Format RtvFormat = Format.Unknown,
    Format SrvFormat = Format.Unknown,
    Format UavFormat = Format.Unknown,
    Format DsvFormat = Format.Unknown,
    TextureKind Kind = TextureKind.Texture2D,
    int DepthOrArraySize = 1,
    int MipLevels = 1,
    ResourceOptionFlags MiscFlags = ResourceOptionFlags.None);
