using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace Apex.Render.Data.Techsets;

/// <summary>
/// One material-editor entry group a techsetdef declares: an element's <c>tweak = Tweak(...)</c> and the GDT fields
/// the element reads, in the order it reads them. APE's material editor shows these fields under
/// <c>Material.&lt;Category&gt;</c>, ordered by <see cref="SortIndex"/> (a sampler's tile and filter fields take
/// consecutive indices: "need 2 indices - tile + filter", <c>include\color_base.techsetdef</c>).
/// </summary>
/// <param name="Element">Element name (<c>colorMap</c>, <c>colorSampler</c>, <c>glossRange</c>).</param>
/// <param name="Category">The tweak's <c>category</c>, possibly dotted (<c>Style Mask.Swatch 1 (Red mask)</c>).</param>
/// <param name="Title">The tweak's <c>title</c>; null when it gives none (samplers keep the deffile's titles).</param>
/// <param name="SortIndex">The tweak's <c>sortindex</c>.</param>
/// <param name="Fields">GDT fields the element reads, first to last.</param>
public sealed record TechsetdefTweak(string Element, string Category, string? Title, double SortIndex, IReadOnlyList<string> Fields);

/// <summary>What the material editor needs from the techsetdefs: the material types by category, and their tweaks.</summary>
public sealed partial class TechsetdefLibrary
{
    // Built once (concurrent askers wait for the one build); a failed build (a file mid-save) is dropped so the next
    // ask reads again, instead of the failure being cached for the session.
    private Lazy<TypeIndex> _types;
    private readonly ConcurrentDictionary<string, Lazy<IReadOnlyList<TechsetdefTweak>>> _tweaks = new(StringComparer.OrdinalIgnoreCase);
    private Lazy<IReadOnlyDictionary<string, TechsetdefTweak>> _union;

    private static T Value<T>(ref Lazy<T> lazy, Func<T> build)
    {
        var current = lazy;
        try
        {
            return current.Value;
        }
        catch (Exception)
        {
            Interlocked.CompareExchange(ref lazy, new Lazy<T>(build), current);
            throw;
        }
    }

    private TypeIndex Types => Value(ref _types, BuildTypeIndex);

    /// <summary>
    /// The material categories APE's "Material Category" list offers: every <c>Globals.category</c> declared by a
    /// techsetdef outside <c>include\</c>, sorted.
    /// </summary>
    public IReadOnlyList<string> MaterialCategories => Types.Categories;

    /// <summary>The material types (techsetdef names) of <paramref name="category"/>, sorted; empty when none.</summary>
    public IReadOnlyList<string> MaterialTypesIn(string category) =>
        Types.ByCategory.TryGetValue(category.Trim(), out var list) ? list : [];

    /// <summary>True when <paramref name="name"/> is a material type: a techsetdef that declares a category.</summary>
    public bool IsMaterialType(string name) => Types.Category.ContainsKey(name.Trim());

    /// <summary>The category of material type <paramref name="name"/>, or null.</summary>
    public string? CategoryOf(string name) => Types.Category.GetValueOrDefault(name.Trim());

    /// <summary>
    /// The tweaks of material type <paramref name="name"/> (its file with every include), sorted by sort index then
    /// declaration order. Empty for an unknown type. Parsed once per type.
    /// </summary>
    public IReadOnlyList<TechsetdefTweak> TweaksOf(string name)
    {
        if (!IsMaterialType(name))
            return [];
        var key = name.Trim();
        var lazy = _tweaks.GetOrAdd(key, n => new Lazy<IReadOnlyList<TechsetdefTweak>>(() => ParseTweaks(n)));
        try
        {
            return lazy.Value;
        }
        catch (Exception)
        {
            _tweaks.TryRemove(new KeyValuePair<string, Lazy<IReadOnlyList<TechsetdefTweak>>>(key, lazy));
            throw;
        }
    }

    /// <summary>
    /// For every GDT field any techsetdef file tweaks, the tweak most files agree on — the layout a material editor
    /// uses before it knows the type. Each file is parsed on its own (no includes), so this reads every file once.
    /// </summary>
    public IReadOnlyDictionary<string, TechsetdefTweak> TweakUnion => Value(ref _union, BuildUnion);

    private sealed record TypeIndex(
        IReadOnlyList<string> Categories,
        IReadOnlyDictionary<string, IReadOnlyList<string>> ByCategory,
        IReadOnlyDictionary<string, string> Category);

    private Dictionary<string, string> UiDefines() => new(StringComparer.Ordinal)
    {
        [MaterialTypes.ToolsGfxDefine.Name] = MaterialTypes.ToolsGfxDefine.Value,
    };

    private bool IsIncludeFile(string file)
    {
        var rel = Path.GetRelativePath(Root, file).Replace('\\', '/');
        return rel.StartsWith("include/", StringComparison.OrdinalIgnoreCase) || !rel.Contains('/');
    }

    /// <summary>The blocks one file declares itself, its includes left out.</summary>
    private List<TsBlock> ParseOwn(string file)
    {
        var warnings = new List<string>();
        var text = TechsetdefPreprocessor.Run(file, UiDefines(), static (_, _) => null, ReadLines, [], warnings);
        return TechsetdefParser.Parse(text, Path.GetFileName(file), warnings);
    }

    private TypeIndex BuildTypeIndex()
    {
        var files = Files.Where(f => f.EndsWith(".techsetdef", StringComparison.OrdinalIgnoreCase) && !IsIncludeFile(f)).ToList();
        var found = new (string Name, string? Category)[files.Count];
        Parallel.For(0, files.Count, i =>
        {
            var blocks = ParseOwn(files[i]);
            var globals = blocks.LastOrDefault(b => b.Type == "Globals" && !b.IsPatch);
            string? category = null;
            if (globals is not null)
            {
                var g = new TsFields();
                g.Apply(globals.Body);
                category = g.Text("category")?.Trim();
            }
            found[i] = (Path.GetFileNameWithoutExtension(files[i]), string.IsNullOrEmpty(category) ? null : category);
        });

        var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, category) in found)
            if (category is not null)
                byName.TryAdd(name, category);
        var byCategory = byName
            .GroupBy(kv => kv.Value, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<string>)g.Select(kv => kv.Key).Order(StringComparer.OrdinalIgnoreCase).ToList(),
                StringComparer.OrdinalIgnoreCase);
        var categories = byCategory.Keys.Order(StringComparer.OrdinalIgnoreCase).ToList();
        return new TypeIndex(categories, byCategory, byName);
    }

    private IReadOnlyList<TechsetdefTweak> ParseTweaks(string name)
    {
        var file = FindFile(name);
        if (file is null)
            return [];
        var warnings = new List<string>();
        var text = TechsetdefPreprocessor.Run(file, UiDefines(), ResolveInclude, ReadLines, [], warnings);
        var unit = new TechsetUnit(TechsetdefParser.Parse(text, Path.GetFileName(file), warnings), warnings);
        return Collect(unit);
    }

    private static List<TechsetdefTweak> Collect(TechsetUnit unit)
    {
        var list = new List<(TechsetdefTweak Tweak, int Order)>();
        foreach (var b in unit.Declarations())
        {
            if (b.Name.Length == 0 || b.IsPatch)
                continue;
            var fields = unit.Resolve(b);
            if (fields.First("tweak") is not { Kind: ValueKind.Call } tv)
                continue;
            var tweak = unit.ResolveInline("Tweak", tv);
            // Tweak( "category", "title", "sortindex" ): the positional form sets the same three.
            string? Arg(int i) => i < tv.Args.Count && tv.Args[i].IsText ? tv.Args[i].Text : null;
            var category = (tweak.Text("category") ?? Arg(0))?.Trim();
            if (string.IsNullOrEmpty(category))
                continue;
            var title = (tweak.Text("title") ?? Arg(1))?.Trim();
            var sortText = tweak.Text("sortindex") ?? Arg(2);
            var sort = double.TryParse(sortText, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var s) ? s : 0;
            var refs = FieldRefs(fields);
            if (refs.Count == 0)
                continue;
            list.Add((new TechsetdefTweak(b.Name, category, string.IsNullOrEmpty(title) ? null : title, sort, refs), list.Count));
        }
        return list.OrderBy(t => t.Tweak.SortIndex).ThenBy(t => t.Order).Select(t => t.Tweak).ToList();
    }

    /// <summary>The GDT fields an element reads through its value fields, in order, once each (its tweak excluded).</summary>
    private static List<string> FieldRefs(TsFields f)
    {
        var result = new List<string>();
        foreach (var key in f.Keys)
        {
            if (key == "tweak") continue;
            foreach (var v in f.All(key)) Walk(v);
        }
        return result;

        void Walk(TsValue v)
        {
            switch (v.Kind)
            {
                case ValueKind.GdtRef:
                    Add(v.Text);
                    break;
                case ValueKind.String:
                    foreach (Match m in GdtRefRx().Matches(v.Text))
                        Add(m.Groups[1].Value);
                    break;
                case ValueKind.Call when v.Text != "Tweak":
                    foreach (var a in v.Args) Walk(a);
                    // The block form: image = Image() { map = <field, $default> }.
                    if (v.Body is { } body)
                        foreach (var st in body)
                            foreach (var sv in st.Values) Walk(sv);
                    break;
            }
        }

        void Add(string field)
        {
            field = field.Trim();
            if (field.Length > 0 && !result.Contains(field, StringComparer.OrdinalIgnoreCase))
                result.Add(field);
        }
    }

    [GeneratedRegex(@"^\s*#\s*include\s+[""<]([^"">]+)["">]")]
    private static partial Regex IncludeLineRx();

    private IReadOnlyDictionary<string, TechsetdefTweak> BuildUnion()
    {
        var files = Files.Where(f => f.EndsWith(".techsetdef", StringComparison.OrdinalIgnoreCase)).ToList();
        var perFile = new List<TechsetdefTweak>[files.Count];
        var includes = new List<string>[files.Count];
        Parallel.For(0, files.Count, i =>
        {
            var warnings = new List<string>();
            perFile[i] = Collect(new TechsetUnit(ParseOwn(files[i]), warnings));
            includes[i] = ReadLines(files[i])
                .Select(line => IncludeLineRx().Match(line))
                .Where(m => m.Success)
                .Select(m => ResolveInclude(m.Groups[1].Value.Trim(), files[i]))
                .OfType<string>()
                .ToList();
        });

        // A file's say is the number of material types that include it (itself included): normal_base's normalMap
        // speaks for every lit type, not once against each refraction type that redeclares it.
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < files.Count; i++)
            index[Path.GetFullPath(files[i])] = i;
        var weight = new int[files.Count];
        var types = Types.Category;
        for (var i = 0; i < files.Count; i++)
        {
            if (!types.ContainsKey(Path.GetFileNameWithoutExtension(files[i])) || IsIncludeFile(files[i]))
                continue;
            var seen = new HashSet<int>();
            var stack = new Stack<int>();
            stack.Push(i);
            while (stack.Count > 0)
            {
                var f = stack.Pop();
                if (!seen.Add(f))
                    continue;
                weight[f]++;
                foreach (var inc in includes[f])
                    if (index.TryGetValue(Path.GetFullPath(inc), out var j))
                        stack.Push(j);
            }
        }

        // Per field, the (category, title, sort) with the most say; ties go to the first file in path order.
        var votes = new Dictionary<string, Dictionary<(string, string?, double), (int Count, int First, TechsetdefTweak Tweak)>>(StringComparer.OrdinalIgnoreCase);
        var seq = 0;
        for (var i = 0; i < files.Count; i++)
            foreach (var t in perFile[i])
                for (var k = 0; k < t.Fields.Count; k++)
                {
                    var field = t.Fields[k];
                    if (!votes.TryGetValue(field, out var byLayout))
                        votes[field] = byLayout = new();
                    var key = (t.Category, t.Title, t.SortIndex + k);
                    var say = Math.Max(weight[i], 1);
                    byLayout[key] = byLayout.TryGetValue(key, out var c) ? (c.Count + say, c.First, c.Tweak) : (say, seq++, t);
                }

        var union = new Dictionary<string, TechsetdefTweak>(StringComparer.OrdinalIgnoreCase);
        foreach (var (field, byLayout) in votes)
        {
            var best = byLayout.OrderByDescending(kv => kv.Value.Count).ThenBy(kv => kv.Value.First).First();
            var (category, title, sort) = best.Key;
            union[field] = new TechsetdefTweak(best.Value.Tweak.Element, category, title, sort, best.Value.Tweak.Fields);
        }
        return union;
    }
}
