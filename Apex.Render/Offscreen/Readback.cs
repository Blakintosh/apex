using Apex.Render.Device;
using Apex.Render.Resources;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Apex.Render.Offscreen;

/// <summary>A tightly packed copy of one texture subresource.</summary>
public sealed record ReadbackImage(Format Format, int Width, int Height, int RowPitch, byte[] Data);

/// <summary>GPU → CPU copies through a staging texture (offscreen rendering, verification tools, thumbnails).</summary>
public static class Readback
{
    /// <summary>Copies subresource (<paramref name="mip"/>, <paramref name="slice"/>) of a 2D texture to the CPU.
    /// Typeless storage is fine; the returned format is the texture's own.</summary>
    public static unsafe ReadbackImage Read(GfxDevice gfx, ID3D11Texture2D texture, int mip = 0, int slice = 0)
    {
        var desc = texture.Description;
        int w = FormatInfo.MipDimension((int)desc.Width, mip), h = FormatInfo.MipDimension((int)desc.Height, mip);
        using var staging = gfx.Device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)w, Height = (uint)h, ArraySize = 1, MipLevels = 1, Format = desc.Format,
            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Staging,
            CPUAccessFlags = CpuAccessFlags.Read, BindFlags = BindFlags.None,
        });
        uint src = (uint)(mip + slice * (int)desc.MipLevels);
        gfx.Context.CopySubresourceRegion(staging, 0, 0, 0, 0, texture, src);

        var (rowPitch, slicePitch, rows) = FormatInfo.Pitch(desc.Format, w, h);
        var data = new byte[slicePitch];
        var mapped = gfx.Context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            for (int y = 0; y < rows; y++)
            {
                var srcRow = new ReadOnlySpan<byte>((byte*)mapped.DataPointer + (long)y * mapped.RowPitch, rowPitch);
                srcRow.CopyTo(data.AsSpan(y * rowPitch, rowPitch));
            }
        }
        finally
        {
            gfx.Context.Unmap(staging, 0);
        }
        return new ReadbackImage(desc.Format, w, h, rowPitch, data);
    }

    public static ReadbackImage Read(GfxDevice gfx, GpuTexture texture, int mip = 0, int slice = 0)
        => Read(gfx, texture.Resource as ID3D11Texture2D ?? throw new ArgumentException("not a 2D texture"), mip, slice);

    /// <summary>Converts an 8-bit RGBA/BGRA or R11G11B10 image to RGBA8 for PNG output. HDR is clamped to [0,1]
    /// (scale first with <paramref name="hdrScale"/>) and written linearly.</summary>
    public static byte[] ToRgba8(ReadbackImage img, float hdrScale = 1f)
    {
        var rgba = new byte[img.Width * img.Height * 4];
        for (int y = 0; y < img.Height; y++)
        {
            for (int x = 0; x < img.Width; x++)
            {
                int o = (y * img.Width + x) * 4;
                switch (img.Format)
                {
                    case Format.R8G8B8A8_UNorm or Format.R8G8B8A8_UNorm_SRgb or Format.R8G8B8A8_Typeless:
                        img.Data.AsSpan(y * img.RowPitch + x * 4, 4).CopyTo(rgba.AsSpan(o, 4));
                        break;
                    case Format.B8G8R8A8_UNorm or Format.B8G8R8A8_UNorm_SRgb or Format.B8G8R8A8_Typeless:
                    {
                        int s = y * img.RowPitch + x * 4;
                        rgba[o] = img.Data[s + 2]; rgba[o + 1] = img.Data[s + 1]; rgba[o + 2] = img.Data[s]; rgba[o + 3] = img.Data[s + 3];
                        break;
                    }
                    case Format.R11G11B10_Float:
                    {
                        var (r, g, b) = PackedFloat.UnpackR11G11B10(BitConverter.ToUInt32(img.Data, y * img.RowPitch + x * 4));
                        rgba[o] = ToByte(r * hdrScale); rgba[o + 1] = ToByte(g * hdrScale); rgba[o + 2] = ToByte(b * hdrScale); rgba[o + 3] = 255;
                        break;
                    }
                    case Format.R8_UNorm:
                        rgba[o] = rgba[o + 1] = rgba[o + 2] = img.Data[y * img.RowPitch + x]; rgba[o + 3] = 255;
                        break;
                    default:
                        throw new NotSupportedException($"ToRgba8 does not handle {img.Format}");
                }
            }
        }
        return rgba;
    }

    private static byte ToByte(float v) => (byte)Math.Clamp((int)MathF.Round(v * 255f), 0, 255);
}
