using Apex.Render.Data.Shaders;

namespace Apex.Render.Data.Techsets;

/// <summary>
/// ToolsGfx technique slots (<c>Techset_InitTechniqueNames</c> 0x1407E8CE0). APE looks up a
/// <c>Technique</c> element with exactly the slot's name; other techniques are ignored by ToolsGfx.
/// </summary>
public enum ToolsGfxTechnique
{
    DepthPrepass = 0,
    Gbuffer = 1,
    Lit = 2,
    Oit = 3,
    Unlit = 4,
    BuildShadowmapDepth = 5,
    BuildShadowmapDepthLayered = 6,
    Wireframe = 7,
    Browser = 8,
    DebugColorTexel = 9,
    TriDensity = 10,
}

/// <summary>Technique slot names.</summary>
public static class ToolsGfxTechniques
{
    private static readonly string[] Names =
    [
        "depth prepass", "gbuffer", "lit", "oit", "unlit", "build shadowmap depth",
        "build shadowmap depth layered", "wireframe", "browser", "debug color texel", "tri density",
    ];

    /// <summary>The techsetdef technique name of a slot (e.g. <c>"gbuffer"</c>).</summary>
    public static string Name(ToolsGfxTechnique slot) => Names[(int)slot];

    /// <summary>The slot a technique name fills, or null for techniques ToolsGfx never uses.</summary>
    public static ToolsGfxTechnique? FromName(string name)
    {
        int i = Array.IndexOf(Names, name);
        return i < 0 ? null : (ToolsGfxTechnique)i;
    }
}

/// <summary>RenderFlags bits (materials.md §2.2). Named combos live in <c>renderFlags.techsetdef</c>.</summary>
[Flags]
public enum MaterialRenderFlags : uint
{
    None = 0,
    Is2D = 0x1,
    IsGbuffer = 0x2,
    IsOpaque = 0x4,
    IsDecal = 0x8,
    IsDoubleSided = 0x10,
    IsTransparent = 0x20,
    IsEmissive = 0x40,
    IsSky = 0x80,
    CastsShadows = 0x100,
    RequiresPrepass = 0x200,
    ShouldSortIndices = 0x400,
    NeedOpaqueResolve = 0x800,
    NeedOpaqueDepthResolve = 0x1000,
    NeedCurrentResolve = 0x2000,
    NeedCurrentDepthResolve = 0x4000,
    ForFX = 0x8000,
    EmissiveFX = 0x10000,
    IsOccluder = 0x20000,
    IsAnimating = 0x40000,
    PlatformOrbis = 0x80000,
    PlatformPC = 0x100000,
    PlatformDurango = 0x200000,
    GlassShard = 0x400000,
    IsVolDecal = 0x800000,
    UseRevealMap = 0x1000000,
    IsSubSurfaceScattering = 0x2000000,
    PostBlur = 0x4000000,
    NoBspCollision = 0x8000000,
    NoDuplicate = 0x10000000,
    IsWater = 0x20000000,
    IsPaintshop = 0x40000000,
}

/// <summary>The techsetdef <c>Globals</c> element.</summary>
public sealed record TechsetGlobals(
    string? Category,
    string? DisplayName,
    string? RenderFlagsName,
    MaterialRenderFlags RenderFlags,
    IReadOnlyList<MaterialType> AvailablePrefixes,
    bool Deprecated,
    bool GameOnly,
    bool RadiantOnly);

/// <summary>One shader stage of a technique: everything needed to find its DXBC in the cache.</summary>
public sealed class TechniqueStage
{
    public required ShaderStage Stage { get; init; }

    /// <summary>Techsetdef key: <c>vs</c>, <c>ls</c>, <c>hs</c>, <c>ds</c>, <c>gs</c>, <c>ps</c> or <c>cs</c>.</summary>
    public required string Key { get; init; }

    /// <summary>Named shader declaration the stage uses (<c>vs_generic</c>), if any.</summary>
    public string? DeclName { get; init; }

    /// <summary>HLSL source (the stage's <c>source</c>, else the technique's).</summary>
    public required string Source { get; init; }

    public required string EntryPoint { get; init; }
    public required string Target { get; init; }

    /// <summary><c>vertexDecl</c> of a VS/LS stage (ToolsGfx meshes use <c>"generic"</c>).</summary>
    public string? VertexDecl { get; init; }

    /// <summary>
    /// Full ordered define list = stage-decl defines + <c>TOOLSGFX 1</c> + material-type defines +
    /// technique defines (parent first, then <c>+=</c>). This is exactly what the cache key hashes.
    /// </summary>
    public required IReadOnlyList<ShaderDefine> Defines { get; init; }

    /// <summary>Cache lookup key for <see cref="ShaderCache.Load"/>.</summary>
    public ShaderVariantKey VariantKey => new(Source, Stage, Defines, EntryPoint, Target);

    public override string ToString() => $"{Key}: {VariantKey}";
}

/// <summary>A resolved <c>Technique</c> element.</summary>
public sealed class TechniqueDefinition
{
    public required string Name { get; init; }

    /// <summary>The ToolsGfx slot this technique fills, or null when ToolsGfx never draws it.</summary>
    public ToolsGfxTechnique? Slot { get; init; }

    public string? Parent { get; init; }

    /// <summary>Name of the State element (or the technique name for an inline State).</summary>
    public string? StateName { get; init; }

    /// <summary>Compiled pipeline state; null when the technique names no state.</summary>
    public StateDescription? State { get; init; }

    /// <summary>The technique's <c>source</c>, the default for every stage.</summary>
    public string? Source { get; init; }

    /// <summary>Technique defines including inherited ones.</summary>
    public required IReadOnlyList<ShaderDefine> TechniqueDefines { get; init; }

    public required IReadOnlyList<TechniqueStage> Stages { get; init; }

    public TechniqueStage? GetStage(ShaderStage stage) => Stages.FirstOrDefault(s => s.Stage == stage);

    public override string ToString() => $"{Name} ({string.Join(", ", Stages.Select(s => s.Key))})";
}

/// <summary>
/// A techsetdef instantiated for one <see cref="Techsets.MaterialType"/>: techniques with their shader
/// variants and states, plus the material elements (textures, samplers, constants) with their GDT bindings.
/// Created by <see cref="TechsetdefLibrary.Load"/>.
/// </summary>
public sealed class Techset
{
    private readonly TechsetUnit _unit;
    private readonly Dictionary<string, StateDescription?> _states = new(StringComparer.Ordinal);

    internal Techset(TechsetUnit unit) => _unit = unit;

    /// <summary>Techsetdef name (the material's GDT <c>materialType</c>, e.g. <c>lit_advanced_fullspec</c>).</summary>
    public required string Name { get; init; }

    public required MaterialType MaterialType { get; init; }

    public required string FilePath { get; init; }

    /// <summary>Files read for this unit, in include order (the techsetdef itself first).</summary>
    public required IReadOnlyList<string> Files { get; init; }

    public required TechsetGlobals Globals { get; init; }

    /// <summary>All <c>Technique</c> elements (templates excluded), in declaration order.</summary>
    public IReadOnlyList<TechniqueDefinition> Techniques { get; internal set; } = [];

    /// <summary>All material elements, in declaration order (see <see cref="MaterialElement"/>).</summary>
    public required IReadOnlyList<MaterialElement> Elements { get; init; }

    /// <summary>Parser/resolver warnings (unresolved includes, unknown names, ...).</summary>
    public IReadOnlyList<string> Warnings => _unit.Warnings;

    internal TechsetUnit Unit => _unit;

    public TechniqueDefinition? GetTechnique(string name) => Techniques.FirstOrDefault(t => t.Name == name);

    /// <summary>The technique filling a ToolsGfx slot (<c>browser</c> falls back to <c>unlit</c> as in APE).</summary>
    public TechniqueDefinition? GetTechnique(ToolsGfxTechnique slot) =>
        GetTechnique(ToolsGfxTechniques.Name(slot))
        ?? (slot == ToolsGfxTechnique.Browser ? GetTechnique(ToolsGfxTechniques.Name(ToolsGfxTechnique.Unlit)) : null);

    public MaterialElement? FindElement(string name) => Elements.FirstOrDefault(e => e.Name == name);

    /// <summary>Compiles any State element of this unit by name (null when it does not exist).</summary>
    public StateDescription? GetState(string name)
    {
        lock (_states)
        {
            if (_states.TryGetValue(name, out var cached))
                return cached;
            var fields = _unit.ResolveNamed("State", name);
            var desc = fields is null ? null : StateCompiler.Compile(_unit, name, fields, _unit.Warnings);
            _states[name] = desc;
            return desc;
        }
    }

    public override string ToString() => $"{MaterialTypes.Prefix(MaterialType)}{Name}";
}
