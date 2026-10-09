using Apex.Render.Data.Shaders;

namespace Apex.Render.Data.Techsets;

/// <summary>
/// ToolsGfx material type (APE <c>techsetdef_mtl_types.cpp</c>, table 0x14087A3C0). Selects the define
/// prefix a techset is instantiated with; the defines are both techsetdef preprocessor defines and part of
/// every shader define list.
/// </summary>
public enum MaterialType
{
    /// <summary>No prefix, no defines.</summary>
    None = 0,

    /// <summary><c>mc/</c>: model, GPU skinned (<c>USE_GPU_SKIN 1, USE_SIEGE_SKINNING 0</c>).</summary>
    Model = 1,

    /// <summary><c>mcs/</c>: model, siege skinning (<c>USE_GPU_SKIN 1, USE_SIEGE_SKINNING 1</c>).</summary>
    ModelSiege = 2,

    /// <summary><c>wc/</c>: world geometry with vertex colour.</summary>
    World = 3,

    /// <summary><c>vd/</c>: volume decal.</summary>
    VolumeDecal = 4,

    /// <summary><c>vdd/</c>: dynamic volume decal.</summary>
    DynamicVolumeDecal = 5,

    /// <summary><c>ei/</c>: instanced effect vertex format.</summary>
    EffectInstanced = 6,

    /// <summary><c>el/</c>: legacy effect vertex format.</summary>
    EffectLegacy = 7,

    /// <summary><c>ec/</c>: cloud effect vertex format.</summary>
    EffectCloud = 8,
}

/// <summary>Prefix strings and define lists of <see cref="MaterialType"/>.</summary>
public static class MaterialTypes
{
    /// <summary>The ToolsGfx global techsetdef define (APE <c>Techset_InitGlobals</c> 0x140466B80).</summary>
    public static readonly ShaderDefine ToolsGfxDefine = new("TOOLSGFX", "1");

    /// <summary>Prefix as written in techsetdef <c>availablePrefixes</c> (<c>mc/</c>, ...; empty for None).</summary>
    public static string Prefix(MaterialType type) => type switch
    {
        MaterialType.None => "",
        MaterialType.Model => "mc/",
        MaterialType.ModelSiege => "mcs/",
        MaterialType.World => "wc/",
        MaterialType.VolumeDecal => "vd/",
        MaterialType.DynamicVolumeDecal => "vdd/",
        MaterialType.EffectInstanced => "ei/",
        MaterialType.EffectLegacy => "el/",
        MaterialType.EffectCloud => "ec/",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    /// <summary>Parses a prefix string (<c>"mc/"</c>, <c>"mc"</c>, or empty).</summary>
    public static MaterialType FromPrefix(string prefix)
    {
        var p = prefix.Trim().TrimEnd('/') + "/";
        foreach (var t in Enum.GetValues<MaterialType>())
        {
            if (string.Equals(Prefix(t), p, StringComparison.OrdinalIgnoreCase))
                return t;
        }
        if (p == "/")
            return MaterialType.None;
        throw new ArgumentException($"Unknown material type prefix '{prefix}'.", nameof(prefix));
    }

    /// <summary>Defines appended after <c>TOOLSGFX 1</c> for a material type, in APE's order.</summary>
    public static IReadOnlyList<ShaderDefine> Defines(MaterialType type) => type switch
    {
        MaterialType.None => [],
        MaterialType.Model => [new("USE_GPU_SKIN", "1"), new("USE_SIEGE_SKINNING", "0")],
        MaterialType.ModelSiege => [new("USE_GPU_SKIN", "1"), new("USE_SIEGE_SKINNING", "1")],
        MaterialType.World =>
        [
            new("RECEIVE_ONLY_STATIC_FORWARD_DECALS", "1"), new("USE_GPU_SKIN", "0"),
            new("USE_SIEGE_SKINNING", "0"), new("USE_VERTEX_COLOR", "1"),
        ],
        MaterialType.VolumeDecal => [new("MTL_TYPE_VOL_DECAL", "1"), new("MTL_TYPE_DYN_VOL_DECAL", "0"), new("USE_VERTEX_COLOR", "1")],
        MaterialType.DynamicVolumeDecal => [new("MTL_TYPE_VOL_DECAL", "1"), new("MTL_TYPE_DYN_VOL_DECAL", "1"), new("USE_VERTEX_COLOR", "1")],
        MaterialType.EffectInstanced => [new("EFFECT_INSTANCED_VERTEX_FORMAT", "1")],
        MaterialType.EffectLegacy => [new("EFFECT_LEGACY_VERTEX_FORMAT", "1")],
        MaterialType.EffectCloud => [new("EFFECT_CLOUD_VERTEX_FORMAT", "1")],
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };
}
