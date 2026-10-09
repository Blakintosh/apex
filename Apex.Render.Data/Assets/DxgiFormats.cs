namespace Apex.Render.Data.Assets;

/// <summary>DXGI format helpers for the formats the ToolsGfx image cache contains (numeric DXGI_FORMAT values).</summary>
public static class DxgiFormats
{
    public const int R16G16B16A16Float = 10;
    public const int R10G10B10A2Unorm = 24;
    public const int R8G8B8A8Unorm = 28;
    public const int R8G8B8A8UnormSrgb = 29;
    public const int R8G8Unorm = 49;
    public const int R8Unorm = 61;
    public const int BC1Unorm = 71;
    public const int BC1UnormSrgb = 72;
    public const int BC2Unorm = 74;
    public const int BC2UnormSrgb = 75;
    public const int BC3Unorm = 77;
    public const int BC3UnormSrgb = 78;
    public const int BC4Unorm = 80;
    public const int BC5Unorm = 83;
    public const int BC6HUf16 = 95;
    public const int BC7Unorm = 98;
    public const int BC7UnormSrgb = 99;

    /// <summary>True for BC1..BC7.</summary>
    public static bool IsBlockCompressed(int format) => format is >= 70 and <= 84 or >= 94 and <= 99;

    /// <summary>Bytes per 4x4 block (BC1/BC4 = 8, others 16); 0 when not block compressed.</summary>
    public static int BlockBytes(int format) => format switch
    {
        >= 70 and <= 72 or >= 79 and <= 81 => 8,
        _ when IsBlockCompressed(format) => 16,
        _ => 0,
    };

    /// <summary>True for the *_SRGB formats.</summary>
    public static bool IsSrgb(int format) => format is 29 or 72 or 75 or 78 or 91 or 93 or 99;

    /// <summary>The linear (UNORM) twin of an sRGB format, else the format itself.</summary>
    public static int ToLinear(int format) => format switch
    {
        29 => 28,
        72 => 71,
        75 => 74,
        78 => 77,
        99 => 98,
        _ => format,
    };

    /// <summary>The sRGB twin of a UNORM format, else the format itself.</summary>
    public static int ToSrgb(int format) => format switch
    {
        28 => 29,
        71 => 72,
        74 => 75,
        77 => 78,
        98 => 99,
        _ => format,
    };

    /// <summary>Short display name.</summary>
    public static string Name(int format) => format switch
    {
        R16G16B16A16Float => "R16G16B16A16_FLOAT",
        R10G10B10A2Unorm => "R10G10B10A2_UNORM",
        R8G8B8A8Unorm => "R8G8B8A8_UNORM",
        R8G8B8A8UnormSrgb => "R8G8B8A8_UNORM_SRGB",
        R8G8Unorm => "R8G8_UNORM",
        R8Unorm => "R8_UNORM",
        BC1Unorm => "BC1_UNORM",
        BC1UnormSrgb => "BC1_UNORM_SRGB",
        BC2Unorm => "BC2_UNORM",
        BC2UnormSrgb => "BC2_UNORM_SRGB",
        BC3Unorm => "BC3_UNORM",
        BC3UnormSrgb => "BC3_UNORM_SRGB",
        BC4Unorm => "BC4_UNORM",
        BC5Unorm => "BC5_UNORM",
        BC6HUf16 => "BC6H_UF16",
        BC7Unorm => "BC7_UNORM",
        BC7UnormSrgb => "BC7_UNORM_SRGB",
        _ => $"DXGI_{format}",
    };
}
