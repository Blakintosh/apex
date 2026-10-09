using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Apex.Render.Data.Shaders;

namespace Apex.Render.Data.Techsets;

/// <summary>
/// Loads ToolsGfx techsetdefs (<c>share\raw\techsetdefs_stable_toolsgfx</c>) by material type name and
/// instantiates them per <see cref="MaterialType"/> (materials.md §2). Thread-safe; instances are cached.
/// </summary>
public sealed partial class TechsetdefLibrary
{
    private static readonly string[] StageKeys = ["vs", "ls", "hs", "ds", "gs", "ps", "cs"];

    private static readonly HashSet<string> ConstantTypes = new(StringComparer.Ordinal)
    {
        "float1", "float2", "float3", "float4", "uint1", "uint2", "uint3", "uint4", "Color", "Bool", "Int",
    };

    private readonly Dictionary<string, string> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<(string, MaterialType), Lazy<Techset>> _cache = new();

    // Every instance reads the shared includes again: their lines by path, re-read only when the file changes.
    private readonly ConcurrentDictionary<string, (DateTime Stamp, string[] Lines)> _lines = new(StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex(@"<\s*([A-Za-z_][A-Za-z0-9_]*)\s*(?:,\s*([^>]+?))?\s*>")]
    internal static partial Regex GdtRefRx();

    /// <summary>Indexes the techsetdef directory of <paramref name="install"/>.</summary>
    public TechsetdefLibrary(ToolsGfxInstall install) : this(install.TechsetdefDir) { }

    /// <summary>Indexes a techsetdef root directory (e.g. <c>techsetdefs_stable_toolsgfx</c>).</summary>
    public TechsetdefLibrary(string root)
    {
        Root = Path.GetFullPath(root);
        if (!Directory.Exists(Root))
            throw new DirectoryNotFoundException($"Techsetdef directory not found: {Root}");

        var files = Directory.EnumerateFiles(Root, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".techsetdef", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".csdef", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        Files = files;
        _types = new Lazy<TypeIndex>(BuildTypeIndex);
        _union = new Lazy<IReadOnlyDictionary<string, TechsetdefTweak>>(BuildUnion);

        // Bare include names resolve to include/ first, then the root, then any sub-folder.
        foreach (var f in files.OrderBy(Rank))
        {
            var fn = Path.GetFileName(f);
            _byName.TryAdd(fn, f);
            _byName.TryAdd(Path.GetFileNameWithoutExtension(fn), f);
        }

        int Rank(string f)
        {
            var rel = Path.GetRelativePath(Root, f).Replace('\\', '/');
            if (rel.StartsWith("include/", StringComparison.OrdinalIgnoreCase)) return 0;
            return rel.Contains('/') ? 2 : 1;
        }
    }

    public string Root { get; }

    /// <summary>Every <c>.techsetdef</c>/<c>.csdef</c> file under <see cref="Root"/>.</summary>
    public IReadOnlyList<string> Files { get; }

    /// <summary>True when a techsetdef of that name exists.</summary>
    public bool Contains(string materialTypeName) => FindFile(materialTypeName) is not null;

    /// <summary>Path of the techsetdef named <paramref name="materialTypeName"/>, or null.</summary>
    public string? FindFile(string materialTypeName)
    {
        var name = materialTypeName.Trim();
        return _byName.TryGetValue(name, out var f) ? f : _byName.TryGetValue(name + ".techsetdef", out f) ? f : null;
    }

    /// <summary>
    /// Loads the techsetdef named by a material's GDT <c>materialType</c> (e.g. <c>lit</c>) for a material type.
    /// </summary>
    /// <exception cref="FileNotFoundException">No techsetdef of that name.</exception>
    public Techset Load(string materialTypeName, MaterialType materialType)
    {
        var key = (materialTypeName.Trim().ToLowerInvariant(), materialType);
        var lazy = _cache.GetOrAdd(key, k => new Lazy<Techset>(() => Build(k.Item1, k.Item2)));
        return lazy.Value;
    }

    private string[] ReadLines(string path)
    {
        var stamp = File.GetLastWriteTimeUtc(path);
        if (_lines.TryGetValue(path, out var e) && e.Stamp == stamp)
            return e.Lines;
        var lines = File.ReadAllLines(path);
        _lines[path] = (stamp, lines);
        return lines;
    }

    private string? ResolveInclude(string name, string fromFile)
    {
        var local = Path.Combine(Path.GetDirectoryName(fromFile)!, name);
        if (File.Exists(local)) return Path.GetFullPath(local);
        if (File.Exists(local + ".techsetdef")) return Path.GetFullPath(local + ".techsetdef");
        if (_byName.TryGetValue(name, out var f)) return f;
        return _byName.TryGetValue(Path.GetFileName(name), out f) ? f : null;
    }

    private Techset Build(string name, MaterialType materialType)
    {
        var file = FindFile(name) ?? throw new FileNotFoundException($"No techsetdef named '{name}' under {Root}.");

        var defines = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [MaterialTypes.ToolsGfxDefine.Name] = MaterialTypes.ToolsGfxDefine.Value,
        };
        foreach (var d in MaterialTypes.Defines(materialType))
            defines[d.Name] = d.Value;

        var warnings = new List<string>();
        var includeOrder = new List<string>();
        var text = TechsetdefPreprocessor.Run(file, defines, ResolveInclude, ReadLines, includeOrder, warnings);
        var unit = new TechsetUnit(TechsetdefParser.Parse(text, Path.GetFileName(file), warnings), warnings);

        var techset = new Techset(unit)
        {
            Name = Path.GetFileNameWithoutExtension(file),
            MaterialType = materialType,
            FilePath = file,
            Files = includeOrder,
            Globals = BuildGlobals(unit),
            Elements = BuildElements(unit),
        };

        // Techniques are built after the techset exists so named states compile through its cache.
        techset.Techniques = unit.Declarations()
            .Where(b => b.Type == "Technique")
            .Select(b => BuildTechnique(unit, techset, b, materialType))
            .ToList();
        return techset;
    }

    private static TechsetGlobals BuildGlobals(TechsetUnit unit)
    {
        var g = unit.Declarations().LastOrDefault(b => b.Type == "Globals") is { } gb ? unit.Resolve(gb) : new TsFields();
        var flagsName = g.Text("renderFlags");
        var flags = MaterialRenderFlags.None;
        if (flagsName is not null)
        {
            var rf = unit.ResolveNamed("RenderFlags", flagsName);
            if (rf is null)
            {
                unit.Warnings.Add($"RenderFlags '{flagsName}' not found");
            }
            else
            {
                foreach (var key in rf.Keys)
                {
                    if (!Enum.TryParse<MaterialRenderFlags>(key, true, out var bit))
                    {
                        unit.Warnings.Add($"RenderFlags '{flagsName}': unknown flag '{key}'");
                        continue;
                    }
                    if (IsTrue(rf.Text(key))) flags |= bit;
                    else flags &= ~bit;
                }
            }
        }

        var prefixes = new List<MaterialType>();
        foreach (var p in (g.Text("availablePrefixes") ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            try { prefixes.Add(MaterialTypes.FromPrefix(p)); }
            catch (ArgumentException) { unit.Warnings.Add($"Globals: unknown prefix '{p}'"); }
        }

        return new TechsetGlobals(g.Text("category"), g.Text("displayName"), flagsName, flags, prefixes,
            IsTrue(g.Text("deprecated")), IsTrue(g.Text("gameOnly")), IsTrue(g.Text("radiantOnly")));
    }

    private static TechniqueDefinition BuildTechnique(TechsetUnit unit, Techset techset, TsBlock block, MaterialType materialType)
    {
        var body = unit.Resolve(block);
        var techniqueDefines = ShaderDefine.ParseList(body.Strings("defines"));
        var techniqueSource = body.Text("source");

        string? stateName = null;
        StateDescription? state = null;
        if (body.First("state") is { } sv)
        {
            if (sv.IsText)
            {
                stateName = sv.Text;
                state = techset.GetState(sv.Text);
                if (state is null) unit.Warnings.Add($"Technique '{block.Name}': State '{sv.Text}' not found");
            }
            else if (sv.Kind == ValueKind.Call)
            {
                stateName = block.Name;
                state = StateCompiler.Compile(unit, block.Name, unit.ResolveInline("State", sv), unit.Warnings);
            }
        }

        var stages = new List<TechniqueStage>();
        foreach (var key in StageKeys)
        {
            var v = body.First(key);
            if (v is null)
                continue;
            var declType = key switch
            {
                "vs" => "VertexShader",
                "ls" => "LocalShader",
                "hs" => "HullShader",
                "ds" => "DomainShader",
                "gs" => "GeometryShader",
                "ps" => "PixelShader",
                _ => "ComputeShader",
            };

            string? decl;
            TsFields sb;
            if (v.IsText)
            {
                decl = v.Text;
                sb = unit.ResolveNamed(declType, decl) ?? new TsFields();
            }
            else if (v.Kind == ValueKind.Call)
            {
                decl = v.Parent;
                sb = unit.ResolveInline(declType, v);
            }
            else
            {
                continue;
            }

            var source = sb.Text("source") ?? techniqueSource;
            if (source is null)
            {
                unit.Warnings.Add($"Technique '{block.Name}' stage {key}: no source");
                continue;
            }

            var stage = key switch
            {
                "vs" or "ls" => ShaderStage.Vertex,
                "hs" => ShaderStage.Hull,
                "ds" => ShaderStage.Domain,
                "gs" => ShaderStage.Geometry,
                "ps" => ShaderStage.Pixel,
                _ => ShaderStage.Compute,
            };

            var defines = new List<ShaderDefine>();
            defines.AddRange(ShaderDefine.ParseList(sb.Strings("defines")));
            defines.Add(MaterialTypes.ToolsGfxDefine);
            defines.AddRange(MaterialTypes.Defines(materialType));
            defines.AddRange(techniqueDefines);

            stages.Add(new TechniqueStage
            {
                Stage = stage,
                Key = key,
                DeclName = decl,
                Source = source,
                EntryPoint = key == "ls" ? "ls_main" : ShaderStages.DefaultEntry(stage),
                Target = ShaderStages.Target(stage),
                VertexDecl = sb.Text("vertexDecl"),
                Defines = defines,
            });
        }

        return new TechniqueDefinition
        {
            Name = block.Name,
            Slot = ToolsGfxTechniques.FromName(block.Name),
            Parent = block.Parent,
            StateName = stateName,
            State = state,
            Source = techniqueSource,
            TechniqueDefines = techniqueDefines,
            Stages = stages,
        };
    }

    private static List<MaterialElement> BuildElements(TechsetUnit unit)
    {
        var list = new List<MaterialElement>();
        foreach (var b in unit.Declarations())
        {
            if (b.Name.Length == 0)
                continue;
            var fields = unit.Resolve(b);
            MaterialElement? e = b.Type switch
            {
                "Texture" => BuildTexture(b.Name, fields),
                "Sampler" => new SamplerElement(b.Name, fields, b.Args.Skip(1).ToList()) { GdtBindings = Bindings(fields, b.Args.Skip(1)) },
                _ when ConstantTypes.Contains(b.Type) => new ConstantElement(b.Name, KindOf(b.Type), fields)
                {
                    GdtBindings = Bindings(fields, []),
                    ExpressionTemplate = Template(fields),
                },
                _ => null,
            };
            if (e is not null)
                list.Add(e);
        }
        return list;
    }

    private static TextureElement BuildTexture(string name, TsFields fields)
    {
        GdtBinding? image = null;
        string? literal = null;
        var iv = fields.First("image");
        if (iv is { Kind: ValueKind.Call, Args.Count: > 0 })
            iv = iv.Args[0];
        // The block form: image = Image() { map = <field, $default> } (tint masks, reveal maps).
        else if (iv is { Kind: ValueKind.Call, Body: { } body })
            iv = body.LastOrDefault(s => s.Key == "map")?.Values.FirstOrDefault();
        if (iv is { Kind: ValueKind.GdtRef })
            image = new GdtBinding(iv.Text, iv.RefDefault?.Text);
        else if (iv is { IsText: true })
            literal = iv.Text;

        var semantics = fields.Strings("semantic");
        var classes = semantics.Select(ImageClasses.FromSemantic).Where(c => c.HasValue).Select(c => c!.Value).Distinct().ToList();
        if (classes.Count == 0)
            classes.Add(ImageClass.Color);

        return new TextureElement(name, fields)
        {
            Image = image,
            LiteralImage = literal,
            Semantics = semantics,
            Classes = classes,
            Usage = fields.Text("usage"),
            IsRef = IsTrue(fields.Text("ref")),
            GdtBindings = image is null ? [] : [image],
        };
    }

    private static MaterialElementKind KindOf(string type) => type switch
    {
        "float1" => MaterialElementKind.Float1,
        "float2" => MaterialElementKind.Float2,
        "float3" => MaterialElementKind.Float3,
        "float4" => MaterialElementKind.Float4,
        "uint1" => MaterialElementKind.UInt1,
        "uint2" => MaterialElementKind.UInt2,
        "uint3" => MaterialElementKind.UInt3,
        "uint4" => MaterialElementKind.UInt4,
        "Color" => MaterialElementKind.Color,
        "Bool" => MaterialElementKind.Bool,
        _ => MaterialElementKind.Int,
    };

    private static string Template(TsFields f)
    {
        if (f.First("value") is { } v)
            return v.ToString();
        var axes = new[] { "x", "y", "z", "w" }.Where(f.Has).Select(a => $"{a} = {f.First(a)}");
        return string.Join(", ", axes);
    }

    /// <summary>All GDT field references reachable from an element's value fields (tweak blocks excluded).</summary>
    private static List<GdtBinding> Bindings(TsFields f, IEnumerable<TsValue> extra)
    {
        var result = new List<GdtBinding>();
        foreach (var key in f.Keys)
        {
            if (key == "tweak") continue;
            foreach (var v in f.All(key)) Walk(v);
        }
        foreach (var v in extra) Walk(v);
        return result.Distinct().ToList();

        void Walk(TsValue v)
        {
            switch (v.Kind)
            {
                case ValueKind.GdtRef:
                    result.Add(new GdtBinding(v.Text, v.RefDefault?.Text));
                    break;
                case ValueKind.String:
                    foreach (Match m in GdtRefRx().Matches(v.Text))
                        result.Add(new GdtBinding(m.Groups[1].Value, m.Groups[2].Success ? m.Groups[2].Value.Trim() : null));
                    break;
                case ValueKind.Call when v.Text != "Tweak":
                    foreach (var a in v.Args) Walk(a);
                    break;
            }
        }
    }

    internal static bool IsTrue(string? s) =>
        s is not null && (s.Equals("true", StringComparison.OrdinalIgnoreCase) || s == "1");
}
