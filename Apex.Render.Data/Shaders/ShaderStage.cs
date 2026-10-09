namespace Apex.Render.Data.Shaders;

/// <summary>Pipeline stage of a cached shader. Numeric values follow APE's stage enum.</summary>
public enum ShaderStage
{
    Vertex = 0,
    Geometry = 1,
    Hull = 2,
    Domain = 3,
    Pixel = 4,
    Compute = 5,
}

/// <summary>Entry-point and profile conventions of the ToolsGfx cache.</summary>
public static class ShaderStages
{
    /// <summary>Shader model 5 profile for a stage (<c>vs_5_0</c>, <c>ps_5_0</c>, ...; APE <c>off_140B01700</c>).</summary>
    public static string Target(ShaderStage stage) => stage switch
    {
        ShaderStage.Vertex => "vs_5_0",
        ShaderStage.Geometry => "gs_5_0",
        ShaderStage.Hull => "hs_5_0",
        ShaderStage.Domain => "ds_5_0",
        ShaderStage.Pixel => "ps_5_0",
        ShaderStage.Compute => "cs_5_0",
        _ => throw new ArgumentOutOfRangeException(nameof(stage)),
    };

    /// <summary>
    /// Default entry point: <c>vs_main</c>, <c>gs_main</c>, ... Tessellated techsetdef vertex shaders
    /// (<c>ls = ...</c>) use <c>ls_main</c> instead.
    /// </summary>
    public static string DefaultEntry(ShaderStage stage) => stage switch
    {
        ShaderStage.Vertex => "vs_main",
        ShaderStage.Geometry => "gs_main",
        ShaderStage.Hull => "hs_main",
        ShaderStage.Domain => "ds_main",
        ShaderStage.Pixel => "ps_main",
        ShaderStage.Compute => "cs_main",
        _ => throw new ArgumentOutOfRangeException(nameof(stage)),
    };

    /// <summary>Two-letter techsetdef key (<c>vs</c>, <c>ps</c>, ...).</summary>
    public static string Key(ShaderStage stage) => DefaultEntry(stage)[..2];
}
