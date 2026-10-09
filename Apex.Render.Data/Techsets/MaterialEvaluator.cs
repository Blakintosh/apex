using System.Globalization;
using System.Text;
using Apex.Render.Data.Shaders;

namespace Apex.Render.Data.Techsets;

/// <summary>An evaluated constant element.</summary>
public sealed class EvaluatedConstant
{
    public required string Name { get; init; }
    public required MaterialElementKind Kind { get; init; }

    /// <summary>The GDT-expanded expression that was evaluated.</summary>
    public required string Expression { get; init; }

    /// <summary>Evaluated components (length = element component count; Color = 4).</summary>
    public required double[] Values { get; init; }

    public double X => Values.Length > 0 ? Values[0] : 0;
    public double Y => Values.Length > 1 ? Values[1] : 0;
    public double Z => Values.Length > 2 ? Values[2] : 0;
    public double W => Values.Length > 3 ? Values[3] : 0;

    /// <summary>Components as float32, padded with zeros to 4.</summary>
    public float[] AsFloat4() => [(float)X, (float)Y, (float)Z, (float)W];

    /// <summary>First component as a bool (non-zero).</summary>
    public bool AsBool => X != 0;

    /// <summary>First component rounded as APE does for INT/UINT variables: <c>(int)(v + 0.5)</c>.</summary>
    public int AsInt => (int)(X + 0.5);

    public override string ToString() =>
        $"{Kind} {Name} = ({string.Join(", ", Values.Select(v => v.ToString("G6", CultureInfo.InvariantCulture)))})";
}

/// <summary>An evaluated texture element: which image to bind and how.</summary>
public sealed class EvaluatedTexture
{
    public required string Name { get; init; }

    /// <summary>Image asset name (a GDT image asset, or a <c>$builtin</c> such as <c>$white_diffuse</c>).</summary>
    public required string ImageName { get; init; }

    /// <summary>True when the GDT value was empty and the techsetdef default was used.</summary>
    public required bool IsDefault { get; init; }

    /// <summary>True for engine built-in images (names starting with <c>$</c>).</summary>
    public bool IsBuiltin => ImageName.StartsWith('$');

    /// <summary>The primary image class (first semantic), which picks sRGB vs linear views.</summary>
    public required ImageClass Class { get; init; }

    /// <summary>All classes the element's <c>semantic</c> list accepts.</summary>
    public required IReadOnlyList<ImageClass> AllowedClasses { get; init; }

    public override string ToString() => $"Texture {Name} = {ImageName} ({Class}{(IsDefault ? ", default" : "")})";
}

/// <summary>An evaluated sampler element.</summary>
public sealed class EvaluatedSampler
{
    public required string Name { get; init; }
    public required string Tile { get; init; }
    public required string Filter { get; init; }

    /// <summary>ToolsGfx sampler state bits (<c>tile | filter</c>).</summary>
    public required int StateBits { get; init; }

    public required SamplerDescription Description { get; init; }

    public override string ToString() => $"Sampler {Name} = '{Tile}', '{Filter}' -> {Description.Filter}/{Description.AddressU}/{Description.AddressV} aniso {Description.MaxAnisotropy}";
}

/// <summary>
/// Every element of a techset evaluated against one material's GDT fields. Look values up by element
/// name: shader <c>$Globals</c> variables, SRVs and samplers bind to elements with the same name.
/// </summary>
public sealed class EvaluatedMaterial
{
    public required Techset Techset { get; init; }
    public required IReadOnlyDictionary<string, EvaluatedConstant> Constants { get; init; }
    public required IReadOnlyDictionary<string, EvaluatedTexture> Textures { get; init; }
    public required IReadOnlyDictionary<string, EvaluatedSampler> Samplers { get; init; }

    /// <summary>Non-fatal problems (unknown GDT values, expression errors, fallbacks used).</summary>
    public required IReadOnlyList<string> Warnings { get; init; }
}

/// <summary>Optional image metrics for <c>&lt;texture&gt;.width</c>-style expressions.</summary>
public interface ITextureMetrics
{
    /// <summary>Width/height/depth (and denormAdd/denormMul) of the image bound to a texture element.</summary>
    double? GetProperty(EvaluatedTexture texture, string property);
}

/// <summary>
/// Evaluates techset elements against a material's GDT field dictionary (materials.md §2.4):
/// <c>&lt;field, default&gt;</c> expands to the GDT string (default only when empty), Color fields become
/// <c>float4(r, g, b, a)</c>, float/uint elements without <c>value</c> become <c>floatN(x, y, z, w)</c>
/// (an axis may name another float1 element), then <see cref="HlslExpression"/> evaluates the result.
/// </summary>
/// <remarks>
/// Pass the material's GDT fields merged with the <c>material</c> deffile defaults (e.g. <c>colorTint</c>
/// "1 1 1 1", <c>filterColor</c> "aniso2x (mip linear)"); the GDT only stores non-default values for many
/// fields. Missing numeric fields evaluate as 0 and are reported in <see cref="EvaluatedMaterial.Warnings"/>.
/// </remarks>
public static class MaterialEvaluator
{
    /// <summary>Evaluates every element of <paramref name="techset"/>.</summary>
    public static EvaluatedMaterial Evaluate(Techset techset, IReadOnlyDictionary<string, string> gdtFields, ITextureMetrics? metrics = null)
    {
        var ctx = new Context(techset, gdtFields, metrics);
        foreach (var e in techset.Elements)
        {
            switch (e)
            {
                case TextureElement t: ctx.Texture(t); break;
                case SamplerElement s: ctx.Sampler(s); break;
                case ConstantElement c: ctx.Constant(c); break;
            }
        }

        return new EvaluatedMaterial
        {
            Techset = techset,
            Constants = ctx.Constants,
            Textures = ctx.Textures,
            Samplers = ctx.Samplers,
            Warnings = ctx.Warnings,
        };
    }

    private sealed class Context : IExpressionScope
    {
        private readonly Techset _techset;
        private readonly IReadOnlyDictionary<string, string> _fields;
        private readonly ITextureMetrics? _metrics;
        private readonly HashSet<string> _evaluating = new(StringComparer.Ordinal);

        public Context(Techset techset, IReadOnlyDictionary<string, string> fields, ITextureMetrics? metrics)
        {
            _techset = techset;
            _fields = fields;
            _metrics = metrics;
        }

        public Dictionary<string, EvaluatedConstant> Constants { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, EvaluatedTexture> Textures { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, EvaluatedSampler> Samplers { get; } = new(StringComparer.Ordinal);
        public List<string> Warnings { get; } = [];

        private string? Field(string name)
        {
            if (_fields.TryGetValue(name, out var v))
                return v;
            foreach (var (k, val) in _fields)
            {
                if (string.Equals(k, name, StringComparison.OrdinalIgnoreCase))
                    return val;
            }
            return null;
        }

        /// <summary>GDT value of a binding, or its default when the GDT value is empty.</summary>
        private string Expand(string field, string? fallback, string element, out bool usedDefault)
        {
            var v = Field(field)?.Trim() ?? string.Empty;
            usedDefault = false;
            if (v.Length > 0)
                return v;
            if (fallback is not null)
            {
                usedDefault = true;
                return fallback;
            }
            if (Field(field) is null)
                Warnings.Add($"{element}: GDT field '{field}' is missing (no default)");
            return string.Empty;
        }

        public void Texture(TextureElement t)
        {
            string image;
            bool isDefault = false;
            if (t.Image is { } b)
                image = Expand(b.Field, b.Default, t.Name, out isDefault);
            else
                image = t.LiteralImage ?? string.Empty;
            if (image.Length == 0)
                Warnings.Add($"{t.Name}: no image");

            Textures[t.Name] = new EvaluatedTexture
            {
                Name = t.Name,
                ImageName = image,
                IsDefault = isDefault,
                Class = t.Classes[0],
                AllowedClasses = t.Classes,
            };
        }

        public void Sampler(SamplerElement s)
        {
            var tile = SamplerString(s, "tile", 0);
            var filter = SamplerString(s, "filter", 1);

            var tileBits = SamplerStates.TileBits(tile);
            if (tileBits is null)
            {
                Warnings.Add($"{s.Name}: unknown tile '{tile}', using 'tile both'");
                tile = "tile both";
                tileBits = 0;
            }
            var filterBits = SamplerStates.FilterBits(filter);
            if (filterBits is null)
            {
                Warnings.Add($"{s.Name}: unknown filter '{filter}', using 'linear (mip linear)'");
                filter = "linear (mip linear)";
                filterBits = 18;
            }

            int bits = tileBits.Value | filterBits.Value;
            Samplers[s.Name] = new EvaluatedSampler
            {
                Name = s.Name,
                Tile = tile,
                Filter = filter,
                StateBits = bits,
                Description = SamplerStates.FromBits(bits),
            };
        }

        private string SamplerString(SamplerElement s, string key, int argIndex)
        {
            var v = s.Fields.First(key) ?? (s.Args.Count > argIndex ? s.Args[argIndex] : null);
            if (v is null)
                return string.Empty;
            return v.Kind switch
            {
                ValueKind.GdtRef => Expand(v.Text, v.RefDefault?.Text, s.Name, out _),
                ValueKind.String => ExpandTemplate(v.Text, false, s.Name, numeric: false),
                _ => v.Text,
            };
        }

        public void Constant(ConstantElement c)
        {
            if (Constants.ContainsKey(c.Name))
                return;
            if (!_evaluating.Add(c.Name))
            {
                Warnings.Add($"{c.Name}: recursive constant reference");
                return;
            }

            string expr;
            try
            {
                expr = BuildExpression(c);
            }
            finally
            {
                _evaluating.Remove(c.Name);
            }

            double[] values;
            try
            {
                _evaluating.Add(c.Name);
                values = HlslExpression.Evaluate(expr, this);
            }
            catch (FormatException ex)
            {
                Warnings.Add($"{c.Name}: {ex.Message}; using 0");
                values = [0];
            }
            finally
            {
                _evaluating.Remove(c.Name);
            }

            var sized = new double[c.Components];
            for (int i = 0; i < sized.Length; i++)
                sized[i] = values.Length == 1 && c.Components > 1 && c.IsColor ? values[0] : i < values.Length ? values[i] : 0;

            Constants[c.Name] = new EvaluatedConstant { Name = c.Name, Kind = c.Kind, Expression = expr, Values = sized };
        }

        private string BuildExpression(ConstantElement c)
        {
            if (c.Fields.First("value") is { } value)
                return ValueExpression(value, c.IsColor, c.Name);

            var axes = new List<string>();
            foreach (var axis in new[] { "x", "y", "z", "w" }.Take(c.Components))
                axes.Add(c.Fields.First(axis) is { } v ? AxisExpression(v, c.Name) : "0");

            // uint elements are evaluated in doubles too; the cbuffer writer rounds per variable type.
            return $"float{c.Components}( {string.Join(", ", axes)} )";
        }

        private string ValueExpression(TsValue v, bool color, string element) => v.Kind switch
        {
            ValueKind.GdtRef => Wrap(Expand(v.Text, v.RefDefault?.Text, element, out _), color),
            ValueKind.String => ExpandTemplate(v.Text, color, element, numeric: true),
            ValueKind.Ident => v.Text,
            _ => throw new FormatException($"{element}: unsupported value {v}"),
        };

        private string AxisExpression(TsValue v, string element)
        {
            switch (v.Kind)
            {
                case ValueKind.GdtRef:
                    return Wrap(Expand(v.Text, v.RefDefault?.Text, element, out _), false);
                case ValueKind.String:
                    return "(" + ExpandTemplate(v.Text, false, element, numeric: true) + ")";
                case ValueKind.Ident:
                    if (double.TryParse(v.Text.TrimEnd('f', 'F'), NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                        return v.Text.TrimEnd('f', 'F');
                    // An axis may name another float1 element; its x is inlined.
                    if (_techset.FindElement(v.Text) is ConstantElement other)
                    {
                        Constant(other);
                        if (Constants.TryGetValue(other.Name, out var ev))
                            return ev.X.ToString("R", CultureInfo.InvariantCulture);
                    }
                    return v.Text;
                default:
                    throw new FormatException($"{element}: unsupported axis value {v}");
            }
        }

        /// <summary>Replaces every <c>&lt;field[, default]&gt;</c> inside a quoted expression.</summary>
        private string ExpandTemplate(string template, bool color, string element, bool numeric)
        {
            var sb = new StringBuilder();
            int last = 0;
            foreach (System.Text.RegularExpressions.Match m in TechsetdefLibrary.GdtRefRx().Matches(template))
            {
                sb.Append(template, last, m.Index - last);
                var value = Expand(m.Groups[1].Value, m.Groups[2].Success ? m.Groups[2].Value.Trim() : null, element, out _);
                sb.Append(numeric ? Wrap(value, color) : value);
                last = m.Index + m.Length;
            }
            sb.Append(template, last, template.Length - last);
            return sb.ToString();
        }

        /// <summary>
        /// GDT value to expression text: colour strings "r g b a" become <c>float4( r, g, b, a )</c>
        /// (missing components 0; <c>Techset_GdtColorToFloat4</c>); empty numeric values become 0.
        /// </summary>
        private static string Wrap(string value, bool color)
        {
            if (color)
            {
                var parts = value.Split([' ', '\t', ','], StringSplitOptions.RemoveEmptyEntries);
                var comps = new string[4];
                for (int i = 0; i < 4; i++)
                    comps[i] = i < parts.Length ? parts[i] : "0";
                return $"float4( {string.Join(", ", comps)} )";
            }
            if (value.Length == 0)
                return "0";
            return value.Contains(' ') ? "(" + value + ")" : value;
        }

        double[]? IExpressionScope.ResolveName(string name)
        {
            if (_techset.FindElement(name) is ConstantElement c)
            {
                Constant(c);
                return Constants.TryGetValue(name, out var v) ? v.Values : null;
            }
            return null;
        }

        double? IExpressionScope.ResolveTextureProperty(string texture, string property)
        {
            if (_metrics is null)
                return null;
            if (!Textures.TryGetValue(texture, out var t))
            {
                if (_techset.FindElement(texture) is TextureElement te)
                    Texture(te);
                if (!Textures.TryGetValue(texture, out t))
                    return null;
            }
            return _metrics.GetProperty(t, property);
        }
    }
}

/// <summary>
/// Builds a material constant buffer (the shader's default <c>$Globals</c> at b0) from reflection and an
/// evaluated material, as <c>Material_BuildDefaultConstantBuffer</c> (0x140452250) does: zero-filled; each
/// <em>used</em> variable takes the element of the same name; BOOL -> 0/1, INT/UINT -> <c>(int)(v + 0.5)</c>,
/// FLOAT -> the first rows*cols components as float32 at the variable's offset.
/// </summary>
public static class MaterialConstantBuffer
{
    /// <summary>Default constant buffer name the compiler gives material parameters.</summary>
    public const string GlobalsName = "$Globals";

    /// <summary>Builds the buffer bytes; <paramref name="missing"/> lists used variables with no element (APE fails the material).</summary>
    public static byte[] Build(ShaderConstantBuffer buffer, EvaluatedMaterial material, out IReadOnlyList<string> missing)
    {
        var bytes = new byte[buffer.Size];
        var miss = new List<string>();
        foreach (var v in buffer.Variables)
        {
            if (!v.IsUsed)
                continue;
            if (!material.Constants.TryGetValue(v.Name, out var c))
            {
                miss.Add(v.Name);
                continue;
            }

            int count = Math.Max(1, v.Rows * v.Columns);
            count = Math.Min(count, v.Size / 4);
            var span = bytes.AsSpan(v.StartOffset);
            for (int i = 0; i < count; i++)
            {
                double x = i < c.Values.Length ? c.Values[i] : 0;
                var dst = span[(i * 4)..];
                switch (v.Type)
                {
                    case ShaderVariableType.Bool:
                        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(dst, x != 0 ? 1 : 0);
                        break;
                    case ShaderVariableType.Int:
                    case ShaderVariableType.UInt:
                        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(dst, (int)(x + 0.5));
                        break;
                    default:
                        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(dst, (float)x);
                        break;
                }
            }
        }
        missing = miss;
        return bytes;
    }
}
