namespace Apex.Render.Offscreen;

/// <summary>Conversions for the packed float formats ToolsGfx's HDR targets use (R11G11B10_FLOAT, half).</summary>
public static class PackedFloat
{
    /// <summary>Unpacks one R11G11B10_FLOAT texel (R bits 0-10, G 11-21, B 22-31).</summary>
    public static (float R, float G, float B) UnpackR11G11B10(uint v)
        => (Unpack(v & 0x7FF, 6), Unpack((v >> 11) & 0x7FF, 6), Unpack((v >> 22) & 0x3FF, 5));

    /// <summary>Packs RGB into R11G11B10_FLOAT with round-to-nearest-even (matches D3D conversion for
    /// representable values; negatives clamp to 0, NaN → NaN, overflow → max finite).</summary>
    public static uint PackR11G11B10(float r, float g, float b)
        => Pack(r, 6) | (Pack(g, 6) << 11) | (Pack(b, 5) << 22);

    private static float Unpack(uint bits, int mantissaBits)
    {
        uint exp = bits >> mantissaBits;
        uint man = bits & ((1u << mantissaBits) - 1);
        if (exp == 0)
            return man * MathF.Pow(2f, -14 - mantissaBits);
        if (exp == 31)
            return man == 0 ? float.PositiveInfinity : float.NaN;
        return BitConverter.Int32BitsToSingle((int)(((exp - 15 + 127) << 23) | (man << (23 - mantissaBits))));
    }

    private static uint Pack(float f, int mantissaBits)
    {
        uint maxExp = 31u << mantissaBits;
        if (float.IsNaN(f))
            return maxExp | 1;
        if (f <= 0f)
            return 0;
        if (float.IsPositiveInfinity(f))
            return maxExp;
        uint u = BitConverter.SingleToUInt32Bits(f);
        int exp = (int)((u >> 23) & 0xFF) - 127 + 15;
        int shift = 23 - mantissaBits;
        if (exp >= 31)
            return maxExp - 1; // largest finite
        if (exp <= 0)
        {
            // Denormal in the small format.
            uint mant = (u & 0x7FFFFF) | 0x800000;
            int s = shift + 1 - exp;
            if (s > 24)
                return 0;
            uint q = mant >> s;
            uint rem = mant & ((1u << s) - 1), half = 1u << (s - 1);
            if (rem > half || (rem == half && (q & 1) != 0)) q++;
            return q;
        }
        uint m = u & 0x7FFFFF;
        uint result = ((uint)exp << mantissaBits) | (m >> shift);
        uint r = m & ((1u << shift) - 1), h = 1u << (shift - 1);
        if (r > h || (r == h && (result & 1) != 0))
            result++;
        return Math.Min(result, maxExp - 1);
    }
}
