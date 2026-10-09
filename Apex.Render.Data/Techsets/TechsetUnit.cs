namespace Apex.Render.Data.Techsets;

/// <summary>
/// One preprocessed + parsed techsetdef translation unit (a file with its includes, for one material
/// type) and its symbol table. Later declarations of the same (type, name) replace earlier ones;
/// <c>Type("name").key = v</c> patches are merged into the target block.
/// </summary>
internal sealed class TechsetUnit
{
    private readonly Dictionary<(string Category, string Name), TsBlock> _symbols = new();

    public TechsetUnit(List<TsBlock> blocks, List<string> warnings)
    {
        Warnings = warnings;
        var byKey = new Dictionary<(string, string), TsBlock>();
        foreach (var b in blocks)
        {
            if (b.IsPatch)
            {
                if (byKey.TryGetValue((b.Type, b.Name), out var target))
                    target.Body.AddRange(b.Body);
                else
                    warnings.Add($"{b.Type}(\"{b.Name}\").{b.Body[0].Key}: patch target not declared");
                continue;
            }
            byKey[(b.Type, b.Name)] = b;
            Blocks.Add(b);
            _symbols[(Category(b.Type), b.Name)] = b;
        }
    }

    public List<TsBlock> Blocks { get; } = [];
    public List<string> Warnings { get; }

    /// <summary>Technique and TechniqueTemplate share one namespace for inheritance.</summary>
    public static string Category(string type) => type is "Technique" or "TechniqueTemplate" ? "Technique" : type;

    public TsBlock? Find(string type, string name) =>
        _symbols.TryGetValue((Category(type), name), out var b) ? b : null;

    /// <summary>The last declaration of each (type, name), in declaration order.</summary>
    public IEnumerable<TsBlock> Declarations() => Blocks.Where(b => ReferenceEquals(Find(b.Type, b.Name), b));

    /// <summary>Fields of a block after applying its parent chain.</summary>
    public TsFields Resolve(TsBlock block) => Resolve(block, 0);

    private TsFields Resolve(TsBlock block, int depth)
    {
        TsFields fields;
        if (block.Parent is not null && depth < 32 && Find(block.Type, block.Parent) is { } parent && !ReferenceEquals(parent, block))
        {
            fields = Resolve(parent, depth + 1).Clone();
        }
        else
        {
            if (block.Parent is not null)
                Warnings.Add($"{block.Type}(\"{block.Name}\"): parent \"{block.Parent}\" not found");
            fields = new TsFields();
        }
        fields.Apply(block.Body);
        return fields;
    }

    /// <summary>Resolves a named element of <paramref name="type"/>, or null.</summary>
    public TsFields? ResolveNamed(string type, string name) =>
        Find(type, name) is { } b ? Resolve(b) : null;

    /// <summary>Resolves an inline constructor value <c>Type() [: "parent"] { body }</c>.</summary>
    public TsFields ResolveInline(string type, TsValue call)
    {
        TsFields fields;
        if (call.Parent is not null && ResolveNamed(type, call.Parent) is { } parent)
        {
            fields = parent.Clone();
        }
        else
        {
            if (call.Parent is not null)
                Warnings.Add($"{type}(): parent \"{call.Parent}\" not found");
            fields = new TsFields();
        }
        if (call.Body is not null)
            fields.Apply(call.Body);
        return fields;
    }
}
