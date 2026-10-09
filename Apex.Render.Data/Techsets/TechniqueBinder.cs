using Apex.Render.Data.Shaders;

namespace Apex.Render.Data.Techsets;

/// <summary>A material SRV slot and the texture element bound to it.</summary>
public sealed record TextureSlot(ShaderResourceBinding Binding, EvaluatedTexture Texture);

/// <summary>A material sampler slot and the sampler element bound to it.</summary>
public sealed record SamplerSlot(ShaderResourceBinding Binding, EvaluatedSampler Sampler);

/// <summary>One stage of a technique bound to a material: DXBC, reflection, <c>$Globals</c> bytes, SRVs, samplers.</summary>
public sealed class StageBinding
{
    public required TechniqueStage Stage { get; init; }
    public required ShaderBinary Binary { get; init; }
    public required DxbcReflection Reflection { get; init; }

    /// <summary>Contents of the material constant buffer (<c>$Globals</c>, b0), or null when the stage has none.</summary>
    public byte[]? MaterialConstants { get; init; }

    /// <summary>Material SRVs (bind point &lt; 18) in bind-point order.</summary>
    public required IReadOnlyList<TextureSlot> Textures { get; init; }

    /// <summary>Material samplers (bind point &lt; 12) in bind-point order.</summary>
    public required IReadOnlyList<SamplerSlot> Samplers { get; init; }

    /// <summary>Problems that make APE reject the material (missing element, non-contiguous slots, ...).</summary>
    public required IReadOnlyList<string> Errors { get; init; }
}

/// <summary>
/// Binds a technique's shader variants to an evaluated material the way APE does (materials.md §3):
/// everything by name through reflection; material SRVs are those with bind point &lt; 18, material samplers
/// &lt; 12 (both must be contiguous from 0); the one non-code cbuffer is <c>$Globals</c> at b0.
/// </summary>
public static class TechniqueBinder
{
    /// <summary>First SRV slot owned by code (t18+).</summary>
    public const int FirstCodeSrvSlot = 18;

    /// <summary>First sampler slot owned by code (s12+).</summary>
    public const int FirstCodeSamplerSlot = 12;

    private static readonly HashSet<string> CodeConstantBuffers = new(StringComparer.Ordinal)
    {
        "CodeSceneConstBuffer", "CodeObjectConstBuffer", "CodeObjectBonesConstBuffer", "CodeSSAOConstBuffer", "CodePostFxConstBuffer",
    };

    /// <summary>Loads and binds every stage of <paramref name="technique"/>.</summary>
    /// <exception cref="ShaderCacheMissException">A stage's variant is not cached.</exception>
    public static IReadOnlyList<StageBinding> Bind(ShaderCache shaders, TechniqueDefinition technique, EvaluatedMaterial material) =>
        technique.Stages.Select(s => BindStage(shaders.Load(s.VariantKey), s, material)).ToList();

    /// <summary>Binds one already-loaded stage.</summary>
    public static StageBinding BindStage(ShaderBinary binary, TechniqueStage stage, EvaluatedMaterial material)
    {
        var refl = binary.Reflection;
        var errors = new List<string>();

        byte[]? constants = null;
        foreach (var cb in refl.ConstantBuffers)
        {
            if (CodeConstantBuffers.Contains(cb.Name) || cb.Type != 0)
                continue;
            if (cb.Name != MaterialConstantBuffer.GlobalsName)
            {
                errors.Add($"{stage.Key}: unexpected constant buffer '{cb.Name}'");
                continue;
            }
            var slot = refl.FindBinding(cb.Name);
            if (slot is not null && slot.BindPoint != 0)
                errors.Add($"{stage.Key}: $Globals is at b{slot.BindPoint}, expected b0");
            constants = MaterialConstantBuffer.Build(cb, material, out var missing);
            foreach (var m in missing)
                errors.Add($"{stage.Key}: Can't find constant param name '{m}'");
        }

        var textures = new List<TextureSlot>();
        var samplers = new List<SamplerSlot>();
        foreach (var b in refl.Bindings.OrderBy(b => b.BindPoint))
        {
            bool isSrv = b.Type is ShaderInputType.Texture or ShaderInputType.TBuffer or ShaderInputType.Structured or ShaderInputType.ByteAddress;
            if (isSrv && b.BindPoint < FirstCodeSrvSlot)
            {
                if (b.BindPoint != textures.Count)
                    errors.Add($"{stage.Key}: texture '{b.Name}' at t{b.BindPoint} breaks contiguous material slots");
                if (material.Textures.TryGetValue(b.Name, out var t))
                    textures.Add(new TextureSlot(b, t));
                else
                    errors.Add($"{stage.Key}: no Texture element named '{b.Name}'");
            }
            else if (b.Type == ShaderInputType.Sampler && b.BindPoint < FirstCodeSamplerSlot)
            {
                if (b.BindPoint != samplers.Count)
                    errors.Add($"{stage.Key}: sampler '{b.Name}' at s{b.BindPoint} breaks contiguous material slots");
                if (material.Samplers.TryGetValue(b.Name, out var s))
                    samplers.Add(new SamplerSlot(b, s));
                else
                    errors.Add($"{stage.Key}: no Sampler element named '{b.Name}'");
            }
        }

        return new StageBinding
        {
            Stage = stage,
            Binary = binary,
            Reflection = refl,
            MaterialConstants = constants,
            Textures = textures,
            Samplers = samplers,
            Errors = errors,
        };
    }
}
