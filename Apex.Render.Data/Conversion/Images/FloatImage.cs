namespace Apex.Render.Data.Conversion.Images;

/// <summary>
/// APE's <c>FloatImage</c> (ImageProcessing.cpp): <c>width x height x numChannels</c> floats, row-major, channels
/// interleaved. Every processing step before quantisation works on this.
/// </summary>
public sealed class FloatImage
{
    public FloatImage(int width, int height, int channels)
    {
        Width = width;
        Height = height;
        Channels = channels;
        Data = new float[checked(width * height * channels)];
    }

    public FloatImage(int width, int height, int channels, float[] data)
    {
        if (data.Length != width * height * channels)
            throw new ArgumentException("Pixel buffer size does not match the dimensions.", nameof(data));
        Width = width;
        Height = height;
        Channels = channels;
        Data = data;
    }

    public int Width { get; }
    public int Height { get; }
    public int Channels { get; }
    public float[] Data { get; }

    public FloatImage Clone() => new(Width, Height, Channels, (float[])Data.Clone());

    /// <summary>An image whose pixels are left unzeroed; only for producers that write every element.</summary>
    internal static FloatImage Uninitialized(int width, int height, int channels) =>
        new(width, height, channels, GC.AllocateUninitializedArray<float>(checked(width * height * channels)));
}

/// <summary>An 8/16/32/64-bit-per-pixel or block-compressed level, as stored in the cache (<c>bpp</c> field).</summary>
internal sealed record PackedLevel(int Width, int Height, int BitsPerPixel, byte[] Data);
