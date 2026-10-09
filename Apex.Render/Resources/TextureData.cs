using Vortice.DXGI;

namespace Apex.Render.Resources;

public enum TextureKind
{
    Texture2D,
    /// <summary>2D array; <see cref="TextureData.ArraySize"/> slices.</summary>
    Texture2DArray,
    /// <summary>Cube or cube array; <see cref="TextureData.ArraySize"/> is 6 × cube count.</summary>
    TextureCube,
    Texture3D,
}

/// <summary>
/// CPU-side texture contents in the form D3D11 consumes — the seam between the data layer (image cache,
/// DDS/EXR, LED probe data) and the GPU. Subresources are tightly packed and ordered the D3D way:
/// for each array slice, every mip from largest to smallest (3D: one entry per mip holding all depth slices).
/// </summary>
public sealed class TextureData
{
    public required Format Format { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public int Depth { get; init; } = 1;
    public int ArraySize { get; init; } = 1;
    public int MipLevels { get; init; } = 1;
    public TextureKind Kind { get; init; } = TextureKind.Texture2D;
    public required IReadOnlyList<ReadOnlyMemory<byte>> Subresources { get; init; }

    public int SubresourceCount => (Kind == TextureKind.Texture3D ? 1 : ArraySize) * MipLevels;

    /// <summary>Wraps one tightly packed 2D image (single mip, single slice).</summary>
    public static TextureData Single2D(Format format, int width, int height, ReadOnlyMemory<byte> pixels) => new()
    {
        Format = format,
        Width = width,
        Height = height,
        Subresources = new[] { pixels },
    };

    /// <summary>Splits one contiguous blob (DDS layout: slice-major, mips inner) into subresources.</summary>
    public static TextureData FromContiguous(Format format, TextureKind kind, int width, int height, int depth,
        int arraySize, int mipLevels, ReadOnlyMemory<byte> data)
    {
        var subs = new List<ReadOnlyMemory<byte>>();
        int offset = 0;
        int slices = kind == TextureKind.Texture3D ? 1 : arraySize;
        for (int s = 0; s < slices; s++)
        {
            for (int m = 0; m < mipLevels; m++)
            {
                var (_, slicePitch, _) = FormatInfo.Pitch(format, FormatInfo.MipDimension(width, m), FormatInfo.MipDimension(height, m));
                int size = slicePitch * (kind == TextureKind.Texture3D ? FormatInfo.MipDimension(depth, m) : 1);
                if (offset + size > data.Length)
                    throw new InvalidDataException($"texture data too short: need {offset + size} bytes, have {data.Length}");
                subs.Add(data.Slice(offset, size));
                offset += size;
            }
        }
        return new TextureData
        {
            Format = format,
            Kind = kind,
            Width = width,
            Height = height,
            Depth = depth,
            ArraySize = arraySize,
            MipLevels = mipLevels,
            Subresources = subs,
        };
    }
}
