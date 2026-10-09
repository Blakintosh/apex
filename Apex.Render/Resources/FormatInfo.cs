using Vortice.DXGI;

namespace Apex.Render.Resources;

/// <summary>Size/compression facts about the DXGI formats ToolsGfx uses (render targets, image cache, captures).</summary>
public static class FormatInfo
{
    /// <summary>True for BC1–BC7 (4×4 blocks).</summary>
    public static bool IsBlockCompressed(Format format) => format switch
    {
        >= Format.BC1_Typeless and <= Format.BC5_SNorm => true,
        >= Format.BC6H_Typeless and <= Format.BC7_UNorm_SRgb => true,
        _ => false,
    };

    /// <summary>Bytes per 4×4 block for BC formats, bytes per pixel otherwise.</summary>
    public static int BytesPerElement(Format format) => format switch
    {
        Format.BC1_Typeless or Format.BC1_UNorm or Format.BC1_UNorm_SRgb
            or Format.BC4_Typeless or Format.BC4_UNorm or Format.BC4_SNorm => 8,
        _ when IsBlockCompressed(format) => 16,

        Format.R32G32B32A32_Typeless or Format.R32G32B32A32_Float or Format.R32G32B32A32_UInt or Format.R32G32B32A32_SInt => 16,
        Format.R32G32B32_Typeless or Format.R32G32B32_Float or Format.R32G32B32_UInt or Format.R32G32B32_SInt => 12,
        Format.R16G16B16A16_Typeless or Format.R16G16B16A16_Float or Format.R16G16B16A16_UNorm or Format.R16G16B16A16_UInt
            or Format.R16G16B16A16_SNorm or Format.R16G16B16A16_SInt or Format.R32G32_Typeless or Format.R32G32_Float
            or Format.R32G32_UInt or Format.R32G32_SInt or Format.R32G8X24_Typeless or Format.D32_Float_S8X24_UInt
            or Format.R32_Float_X8X24_Typeless or Format.X32_Typeless_G8X24_UInt => 8,
        Format.R8G8_Typeless or Format.R8G8_UNorm or Format.R8G8_UInt or Format.R8G8_SNorm or Format.R8G8_SInt
            or Format.R16_Typeless or Format.R16_Float or Format.D16_UNorm or Format.R16_UNorm or Format.R16_UInt
            or Format.R16_SNorm or Format.R16_SInt or Format.B5G6R5_UNorm or Format.B5G5R5A1_UNorm => 2,
        Format.R8_Typeless or Format.R8_UNorm or Format.R8_UInt or Format.R8_SNorm or Format.R8_SInt or Format.A8_UNorm => 1,
        _ => 4,
    };

    /// <summary>Row pitch and slice pitch (bytes) of one tightly packed mip level.</summary>
    public static (int RowPitch, int SlicePitch, int Rows) Pitch(Format format, int width, int height)
    {
        if (IsBlockCompressed(format))
        {
            int bw = Math.Max(1, (width + 3) / 4);
            int bh = Math.Max(1, (height + 3) / 4);
            int row = bw * BytesPerElement(format);
            return (row, row * bh, bh);
        }
        int rowPitch = Math.Max(1, width) * BytesPerElement(format);
        return (rowPitch, rowPitch * Math.Max(1, height), Math.Max(1, height));
    }

    /// <summary>Typeless parent, used when a texture needs both an sRGB and a linear (or UAV) view.</summary>
    public static Format ToTypeless(Format format) => format switch
    {
        Format.R8G8B8A8_UNorm or Format.R8G8B8A8_UNorm_SRgb or Format.R8G8B8A8_UInt => Format.R8G8B8A8_Typeless,
        Format.B8G8R8A8_UNorm or Format.B8G8R8A8_UNorm_SRgb => Format.B8G8R8A8_Typeless,
        Format.R10G10B10A2_UNorm or Format.R10G10B10A2_UInt => Format.R10G10B10A2_Typeless,
        Format.R16_UNorm or Format.R16_Float or Format.D16_UNorm => Format.R16_Typeless,
        Format.R32_Float or Format.D32_Float or Format.R32_UInt => Format.R32_Typeless,
        Format.D32_Float_S8X24_UInt or Format.R32_Float_X8X24_Typeless => Format.R32G8X24_Typeless,
        Format.D24_UNorm_S8_UInt or Format.R24_UNorm_X8_Typeless => Format.R24G8_Typeless,
        Format.BC1_UNorm or Format.BC1_UNorm_SRgb => Format.BC1_Typeless,
        Format.BC2_UNorm or Format.BC2_UNorm_SRgb => Format.BC2_Typeless,
        Format.BC3_UNorm or Format.BC3_UNorm_SRgb => Format.BC3_Typeless,
        Format.BC7_UNorm or Format.BC7_UNorm_SRgb => Format.BC7_Typeless,
        _ => format,
    };

    public static int MipDimension(int size, int mip) => Math.Max(1, size >> mip);
}
