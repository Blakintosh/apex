namespace Apex.Render.Data.Techsets;

/// <summary><c>D3D11_FILTER</c> values ToolsGfx produces.</summary>
public enum TextureFilter
{
    MinMagMipPoint = 0x00,
    MinMagPointMipLinear = 0x01,
    MinMagLinearMipPoint = 0x14,
    MinMagMipLinear = 0x15,
    Anisotropic = 0x55,
    ComparisonMinMagLinearMipPoint = 0x94,
    ComparisonMinMagMipLinear = 0x95,
}

/// <summary><c>D3D11_TEXTURE_ADDRESS_MODE</c> values.</summary>
public enum TextureAddressMode
{
    Wrap = 1,
    Mirror = 2,
    Clamp = 3,
    Border = 4,
}

/// <summary>A <c>D3D11_SAMPLER_DESC</c> built the way <c>ToolsGfx_CreateSamplerState</c> (0x140607B90) does.</summary>
public sealed record SamplerDescription(
    TextureFilter Filter,
    TextureAddressMode AddressU,
    TextureAddressMode AddressV,
    TextureAddressMode AddressW,
    float MipLodBias,
    int MaxAnisotropy,
    ComparisonFunction ComparisonFunction,
    float BorderR,
    float BorderG,
    float BorderB,
    float BorderA,
    float MinLod,
    float MaxLod);

/// <summary>
/// Techsetdef sampler strings -> ToolsGfx sampler state bits -> <see cref="SamplerDescription"/> (materials.md §3.3).
/// </summary>
public static class SamplerStates
{
    /// <summary>Address bits of a <c>tile</c> string, or null when unknown.</summary>
    public static int? TileBits(string tile) => tile.Trim().TrimEnd('*').Trim().ToLowerInvariant() switch
    {
        "tile both" => 0x000,
        "tile vertical" => 0x020,
        "tile horizontal" => 0x080,
        "no tile" => 0x2A0,
        "mirror both" => 0x1E0,
        "mirror vertical" => 0x060,
        "mirror horizontal" => 0x180,
        _ => null,
    };

    /// <summary>Filter code of a <c>filter</c> string, or null when unknown.</summary>
    public static int? FilterBits(string filter) => filter.Trim().TrimEnd('*').Trim().ToLowerInvariant() switch
    {
        "nomip nearest" or "nearest (mip none)" => 1,
        "nomip bilinear" or "linear (mip none)" => 2,
        "nearest (mip nearest)" => 9,
        "mip (1x bilinear)" or "linear (mip nearest)" => 10,
        "mip standard (2x bilinear)" => 11,
        "mip expensive (4x bilinear)" => 12,
        "mip (1x trilinear)" or "linear (mip linear)" => 18,
        "mip more expensive (2x trilinear)" or "aniso2x (mip linear)" => 19,
        "mip most expensive (4x trilinear)" or "aniso4x (mip linear)" => 20,
        "aniso8x (mip linear)" => 21,
        "aniso16x (mip linear)" => 22,
        _ => null,
    };

    /// <summary>
    /// Builds the sampler description from state bits (<c>tileBits | filterCode</c>):
    /// <c>f = bits &amp; 7</c> selects point/linear/aniso2..16; <c>bits &amp; 0x10</c> = mip linear;
    /// <c>bits &amp; 0x18</c> = any mips; address modes at bits 5/7/9; border colour bit 11; comparison GREATER.
    /// </summary>
    /// <param name="bits">Combined state bits.</param>
    /// <param name="mipLodBias">APE's global MipLODBias dvar (0x1416AB8C0, presumably 0).</param>
    public static SamplerDescription FromBits(int bits, float mipLodBias = 0f)
    {
        int f = bits & 7;
        bool mipLinear = (bits & 0x10) != 0;
        bool mipAny = (bits & 0x18) != 0;

        ReadOnlySpan<TextureFilter> table =
        [
            TextureFilter.MinMagMipPoint, TextureFilter.MinMagPointMipLinear,             // f = 0
            TextureFilter.MinMagMipPoint, TextureFilter.MinMagPointMipLinear,             // f = 1 nearest
            TextureFilter.MinMagLinearMipPoint, TextureFilter.MinMagMipLinear,            // f = 2 linear
            TextureFilter.MinMagLinearMipPoint, TextureFilter.Anisotropic,                // f = 3 aniso2
            TextureFilter.MinMagLinearMipPoint, TextureFilter.Anisotropic,                // f = 4 aniso4
            TextureFilter.MinMagLinearMipPoint, TextureFilter.Anisotropic,                // f = 5 aniso8
            TextureFilter.MinMagLinearMipPoint, TextureFilter.Anisotropic,                // f = 6 aniso16
            TextureFilter.ComparisonMinMagLinearMipPoint, TextureFilter.ComparisonMinMagMipLinear, // f = 7
        ];
        ReadOnlySpan<int> aniso = [1, 1, 1, 2, 4, 8, 16, 1];
        ReadOnlySpan<TextureAddressMode> address =
            [TextureAddressMode.Wrap, TextureAddressMode.Clamp, TextureAddressMode.Border, TextureAddressMode.Mirror];

        float border = (bits & 0x800) != 0 ? 1f : 0f;
        return new SamplerDescription(
            table[2 * f + (mipLinear ? 1 : 0)],
            address[(bits >> 5) & 3],
            address[(bits >> 7) & 3],
            address[(bits >> 9) & 3],
            mipLodBias,
            aniso[f],
            ComparisonFunction.Greater,
            border, border, border, border,
            0f,
            mipAny ? float.MaxValue : 0f);
    }
}
