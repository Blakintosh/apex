namespace Apex.Render.Data.Techsets;

/// <summary>Kind of a material element (techsetdef element type).</summary>
public enum MaterialElementKind
{
    Texture,
    Sampler,
    Float1,
    Float2,
    Float3,
    Float4,
    UInt1,
    UInt2,
    UInt3,
    UInt4,
    Color,
    Bool,
    Int,
}

/// <summary>
/// Image class of a texture (materials.md §3.2): chooses the image cache <c>Texture_&lt;class&gt;_...</c>
/// directory and the sRGB-ness of the view. Values follow APE's semantic table.
/// </summary>
public enum ImageClass
{
    /// <summary>sRGB colour (colorMap, diffuseMap, effectMap, specularMap, 2d).</summary>
    Color = 0,

    /// <summary>Linear HDR colour (HDR, Eye Caustic, Custom).</summary>
    HdrColor = 1,

    /// <summary>Two-channel normal map; the shader reconstructs Z.</summary>
    Normal = 2,

    /// <summary>One channel (specularMask, glossMap, occlusionMap, revealMap, thicknessMap, One Channel).</summary>
    Scalar = 3,

    /// <summary>Two channels (Two Channel).</summary>
    DualScalar = 4,

    /// <summary>Four channels (multipleMask).</summary>
    QuadScalar = 5,

    /// <summary>Lookup table (LutTpage).</summary>
    Lut = 6,
}

/// <summary>Image class helpers.</summary>
public static class ImageClasses
{
    /// <summary>Maps a techsetdef/GDT image <c>semantic</c> string to its class (null when unknown).</summary>
    public static ImageClass? FromSemantic(string semantic) => semantic.Trim().ToLowerInvariant() switch
    {
        "colormap" or "diffusemap" or "effectmap" or "specularmap" or "2d" => ImageClass.Color,
        "hdr" or "eye caustic" or "custom" => ImageClass.HdrColor,
        "normalmap" => ImageClass.Normal,
        "specularmask" or "glossmap" or "occlusionmap" or "revealmap" or "thicknessmap" or "one channel" => ImageClass.Scalar,
        "two channel" => ImageClass.DualScalar,
        "multiplemask" => ImageClass.QuadScalar,
        "luttpage" => ImageClass.Lut,
        _ => null,
    };

    /// <summary>The class token used in image cache directory names (<c>color</c>, <c>hdrcolor</c>, ...).</summary>
    public static string CacheToken(ImageClass c) => c switch
    {
        ImageClass.Color => "color",
        ImageClass.HdrColor => "hdrcolor",
        ImageClass.Normal => "normal",
        ImageClass.Scalar => "scalar",
        ImageClass.DualScalar => "dualscalar",
        ImageClass.QuadScalar => "quadscalar",
        ImageClass.Lut => "lut",
        _ => throw new ArgumentOutOfRangeException(nameof(c)),
    };
}

/// <summary>A <c>&lt;field[, default]&gt;</c> GDT binding.</summary>
/// <param name="Field">GDT field name of the material asset.</param>
/// <param name="Default">Value used when the GDT value is empty (e.g. <c>$white_diffuse</c>).</param>
public sealed record GdtBinding(string Field, string? Default);

/// <summary>A material element declared by a techset (texture, sampler or constant).</summary>
public abstract class MaterialElement
{
    internal MaterialElement(string name, MaterialElementKind kind, TsFields fields)
    {
        Name = name;
        Kind = kind;
        Fields = fields;
    }

    /// <summary>Element name; shader resources and <c>$Globals</c> variables bind to elements by this name.</summary>
    public string Name { get; }

    public MaterialElementKind Kind { get; }

    /// <summary>Every GDT field the element reads.</summary>
    public IReadOnlyList<GdtBinding> GdtBindings { get; internal init; } = [];

    internal TsFields Fields { get; }

    public override string ToString() => $"{Kind} {Name}";
}

/// <summary>A <c>Texture</c> element: <c>image = Image(&lt;field, $default&gt;)</c>, <c>semantic</c>, <c>usage</c>.</summary>
public sealed class TextureElement : MaterialElement
{
    internal TextureElement(string name, TsFields fields) : base(name, MaterialElementKind.Texture, fields) { }

    /// <summary>The image binding (GDT field + default image), or null for a literal image name.</summary>
    public GdtBinding? Image { get; internal init; }

    /// <summary>Literal image name when the element does not read a GDT field.</summary>
    public string? LiteralImage { get; internal init; }

    /// <summary>The <c>semantic</c> strings (allowed image semantics).</summary>
    public IReadOnlyList<string> Semantics { get; internal init; } = [];

    /// <summary>Allowed image classes (default <see cref="ImageClass.Color"/> when no semantic is given).</summary>
    public IReadOnlyList<ImageClass> Classes { get; internal init; } = [ImageClass.Color];

    public string? Usage { get; internal init; }

    /// <summary>The techsetdef <c>ref</c> flag.</summary>
    public bool IsRef { get; internal init; }
}

/// <summary>A <c>Sampler</c> element: <c>tile</c> and <c>filter</c> strings (usually GDT fields).</summary>
public sealed class SamplerElement : MaterialElement
{
    internal SamplerElement(string name, TsFields fields, IReadOnlyList<TsValue> args)
        : base(name, MaterialElementKind.Sampler, fields) => Args = args;

    internal IReadOnlyList<TsValue> Args { get; }
}

/// <summary>A constant element (<c>float1..4</c>, <c>uint1..4</c>, <c>Color</c>, <c>Bool</c>, <c>Int</c>).</summary>
public sealed class ConstantElement : MaterialElement
{
    internal ConstantElement(string name, MaterialElementKind kind, TsFields fields) : base(name, kind, fields) { }

    /// <summary>Number of components the element produces (Color = 4, Bool/Int = 1).</summary>
    public int Components => Kind switch
    {
        MaterialElementKind.Float1 or MaterialElementKind.UInt1 or MaterialElementKind.Bool or MaterialElementKind.Int => 1,
        MaterialElementKind.Float2 or MaterialElementKind.UInt2 => 2,
        MaterialElementKind.Float3 or MaterialElementKind.UInt3 => 3,
        _ => 4,
    };

    /// <summary>True for Color elements, whose GDT colour strings are rewritten to <c>float4(r, g, b, a)</c>.</summary>
    public bool IsColor => Kind == MaterialElementKind.Color;

    /// <summary>The raw value expression with GDT references unexpanded (for display/debugging).</summary>
    public string ExpressionTemplate { get; internal init; } = string.Empty;
}
