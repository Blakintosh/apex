using System.Globalization;

namespace Apex.Render.Data.Techsets;

/// <summary>Name lookups available to <see cref="HlslExpression"/>.</summary>
public interface IExpressionScope
{
    /// <summary>Value of a named constant (another float1..4 element), or null when unknown.</summary>
    double[]? ResolveName(string name);

    /// <summary>
    /// A texture property used as <c>&lt;texture&gt;.width</c>, <c>.height</c>, <c>.depth</c>, <c>.denormAdd</c>,
    /// <c>.denormMul</c>; null when unknown.
    /// </summary>
    double? ResolveTextureProperty(string texture, string property);
}

/// <summary>
/// Evaluator for techsetdef value expressions (APE <c>hlsl_expr_eval</c>): doubles, vectors of 1-4
/// components, HLSL constructors and swizzles, + - * / %, comparisons, ?:, and the builtin function set
/// including <c>srgb_to_linear</c> (materials.md §2.4).
/// </summary>
public static class HlslExpression
{
    /// <summary>Evaluates <paramref name="text"/>; the result has 1-4 components.</summary>
    /// <exception cref="FormatException">Syntax error, unknown name or function.</exception>
    public static double[] Evaluate(string text, IExpressionScope? scope = null)
    {
        var p = new Parser(text, scope);
        var v = p.Expression();
        p.ExpectEnd();
        return v;
    }

    /// <summary>
    /// <c>srgb_to_linear</c> (elementwise): <c>c = clamp(c, 0, 1); c &lt;= 0.04045 ? c / 12.92 : pow((c + 0.055) / 1.055, 2.4)</c>.
    /// </summary>
    public static double SrgbToLinear(double c)
    {
        c = Math.Clamp(c, 0.0, 1.0);
        return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    private sealed class Parser
    {
        private readonly string _s;
        private readonly IExpressionScope? _scope;
        private int _i;

        public Parser(string s, IExpressionScope? scope)
        {
            _s = s;
            _scope = scope;
        }

        public void ExpectEnd()
        {
            SkipWs();
            if (_i < _s.Length)
                throw Error($"unexpected '{_s[_i..]}'");
        }

        private FormatException Error(string message) => new($"Expression \"{_s}\": {message}");

        private void SkipWs()
        {
            while (_i < _s.Length && char.IsWhiteSpace(_s[_i])) _i++;
        }

        private bool Eat(string token)
        {
            SkipWs();
            if (string.CompareOrdinal(_s, _i, token, 0, token.Length) != 0)
                return false;
            _i += token.Length;
            return true;
        }

        private bool Peek(string token)
        {
            SkipWs();
            return string.CompareOrdinal(_s, _i, token, 0, token.Length) == 0;
        }

        public double[] Expression()
        {
            var c = Or();
            if (!Eat("?"))
                return c;
            var a = Expression();
            if (!Eat(":")) throw Error("expected ':'");
            var b = Expression();
            return Zip(c, a, b, (x, y, z) => x != 0 ? y : z);
        }

        private double[] Or()
        {
            var l = And();
            while (Eat("||")) { var r = And(); l = Zip(l, r, (a, b) => a != 0 || b != 0 ? 1 : 0); }
            return l;
        }

        private double[] And()
        {
            var l = Compare();
            while (Eat("&&")) { var r = Compare(); l = Zip(l, r, (a, b) => a != 0 && b != 0 ? 1 : 0); }
            return l;
        }

        private double[] Compare()
        {
            var l = Additive();
            while (true)
            {
                if (Eat("==")) l = Zip(l, Additive(), (a, b) => a == b ? 1 : 0);
                else if (Eat("!=")) l = Zip(l, Additive(), (a, b) => a != b ? 1 : 0);
                else if (Eat("<=")) l = Zip(l, Additive(), (a, b) => a <= b ? 1 : 0);
                else if (Eat(">=")) l = Zip(l, Additive(), (a, b) => a >= b ? 1 : 0);
                else if (Eat("<")) l = Zip(l, Additive(), (a, b) => a < b ? 1 : 0);
                else if (Eat(">")) l = Zip(l, Additive(), (a, b) => a > b ? 1 : 0);
                else return l;
            }
        }

        private double[] Additive()
        {
            var l = Multiplicative();
            while (true)
            {
                if (Eat("+")) l = Zip(l, Multiplicative(), (a, b) => a + b);
                else if (Eat("-")) l = Zip(l, Multiplicative(), (a, b) => a - b);
                else return l;
            }
        }

        private double[] Multiplicative()
        {
            var l = Unary();
            while (true)
            {
                if (Eat("*")) l = Zip(l, Unary(), (a, b) => a * b);
                else if (Eat("/")) l = Zip(l, Unary(), (a, b) => a / b);
                else if (Eat("%")) l = Zip(l, Unary(), (a, b) => a - b * Math.Truncate(a / b));
                else return l;
            }
        }

        private double[] Unary()
        {
            if (Eat("-")) return Map(Unary(), x => -x);
            if (Eat("+")) return Unary();
            if (Eat("!")) return Map(Unary(), x => x == 0 ? 1 : 0);
            return Postfix();
        }

        private double[] Postfix()
        {
            SkipWs();
            double[] v;
            string? ident = null;
            if (_i < _s.Length && (char.IsDigit(_s[_i]) || (_s[_i] == '.' && _i + 1 < _s.Length && char.IsDigit(_s[_i + 1]))))
            {
                v = [Number()];
            }
            else if (Eat("("))
            {
                v = Expression();
                if (!Eat(")")) throw Error("expected ')'");
            }
            else if (_i < _s.Length && (char.IsLetter(_s[_i]) || _s[_i] == '_'))
            {
                ident = Identifier();
                if (Eat("("))
                {
                    var args = new List<double[]>();
                    if (!Eat(")"))
                    {
                        do args.Add(Expression()); while (Eat(","));
                        if (!Eat(")")) throw Error($"expected ')' after arguments of {ident}");
                    }
                    v = Call(ident, args);
                    ident = null;
                }
                else if (Peek(".") && _scope is not null && TryTextureProperty(ident, out var prop))
                {
                    v = [prop];
                    ident = null;
                }
                else
                {
                    v = ResolveIdentifier(ident);
                }
            }
            else
            {
                throw Error(_i < _s.Length ? $"unexpected '{_s[_i]}'" : "unexpected end");
            }

            while (Peek(".") && !(_i + 1 < _s.Length && char.IsDigit(_s[_i + 1])))
            {
                _i++;
                var sw = Identifier();
                v = Swizzle(v, sw);
            }
            return v;
        }

        private bool TryTextureProperty(string texture, out double value)
        {
            int save = _i;
            _i++; // '.'
            var member = Identifier();
            var r = _scope!.ResolveTextureProperty(texture, member);
            if (r is null)
            {
                _i = save;
                value = 0;
                return false;
            }
            value = r.Value;
            return true;
        }

        private double Number()
        {
            int start = _i;
            while (_i < _s.Length && (char.IsDigit(_s[_i]) || _s[_i] == '.')) _i++;
            if (_i < _s.Length && (_s[_i] == 'e' || _s[_i] == 'E'))
            {
                int save = _i++;
                if (_i < _s.Length && (_s[_i] == '+' || _s[_i] == '-')) _i++;
                if (_i < _s.Length && char.IsDigit(_s[_i]))
                    while (_i < _s.Length && char.IsDigit(_s[_i])) _i++;
                else
                    _i = save;
            }
            var text = _s[start.._i];
            if (_i < _s.Length && (_s[_i] is 'f' or 'F' or 'h' or 'H' or 'u' or 'U' or 'l' or 'L')) _i++;
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                throw Error($"bad number '{text}'");
            return d;
        }

        private string Identifier()
        {
            SkipWs();
            int start = _i;
            while (_i < _s.Length && (char.IsLetterOrDigit(_s[_i]) || _s[_i] == '_')) _i++;
            if (_i == start) throw Error("expected an identifier");
            return _s[start.._i];
        }

        private double[] ResolveIdentifier(string name)
        {
            switch (name)
            {
                case "true": return [1];
                case "false": return [0];
            }
            return _scope?.ResolveName(name) ?? throw Error($"unknown name '{name}'");
        }

        private double[] Swizzle(double[] v, string sw)
        {
            if (sw.Length is 0 or > 4) throw Error($"bad swizzle '.{sw}'");
            var r = new double[sw.Length];
            for (int k = 0; k < sw.Length; k++)
            {
                int idx = sw[k] switch
                {
                    'x' or 'r' => 0,
                    'y' or 'g' => 1,
                    'z' or 'b' => 2,
                    'w' or 'a' => 3,
                    _ => throw Error($"bad swizzle '.{sw}'"),
                };
                if (idx >= v.Length)
                {
                    if (v.Length != 1) throw Error($"swizzle '.{sw}' out of range for a {v.Length}-component value");
                    idx = 0; // scalar.xxxx
                }
                r[k] = v[idx];
            }
            return r;
        }

        private double[] Call(string name, List<double[]> a)
        {
            // Constructors.
            if (TryConstructor(name, a, out var ctor))
                return ctor;

            return name switch
            {
                "abs" => Map(Arg(a, 1, name)[0], Math.Abs),
                "acos" => Map(Arg(a, 1, name)[0], Math.Acos),
                "asin" => Map(Arg(a, 1, name)[0], Math.Asin),
                "atan" => Map(Arg(a, 1, name)[0], Math.Atan),
                "atan2" => Zip(Arg(a, 2, name)[0], a[1], Math.Atan2),
                "ceil" => Map(Arg(a, 1, name)[0], Math.Ceiling),
                "clamp" => Zip(Arg(a, 3, name)[0], a[1], a[2], (x, lo, hi) => Math.Min(Math.Max(x, lo), hi)),
                "cos" => Map(Arg(a, 1, name)[0], Math.Cos),
                "cosh" => Map(Arg(a, 1, name)[0], Math.Cosh),
                "cross" => Cross(Arg(a, 2, name)[0], a[1]),
                "degrees" => Map(Arg(a, 1, name)[0], x => x * 180.0 / Math.PI),
                "distance" => [Length(Zip(Arg(a, 2, name)[0], a[1], (x, y) => x - y))],
                "dot" => [Dot(Arg(a, 2, name)[0], a[1])],
                "exp" => Map(Arg(a, 1, name)[0], Math.Exp),
                "exp2" => Map(Arg(a, 1, name)[0], x => Math.Pow(2, x)),
                "floor" => Map(Arg(a, 1, name)[0], Math.Floor),
                "fmod" => Zip(Arg(a, 2, name)[0], a[1], (x, y) => x - y * Math.Truncate(x / y)),
                "frac" => Map(Arg(a, 1, name)[0], x => x - Math.Floor(x)),
                "length" => [Length(Arg(a, 1, name)[0])],
                "lerp" => Zip(Arg(a, 3, name)[0], a[1], a[2], (x, y, s) => x + (y - x) * s),
                "log" => Map(Arg(a, 1, name)[0], Math.Log),
                "log10" => Map(Arg(a, 1, name)[0], Math.Log10),
                "log2" => Map(Arg(a, 1, name)[0], Math.Log2),
                "mad" => Zip(Arg(a, 3, name)[0], a[1], a[2], (x, y, z) => x * y + z),
                "max" => Zip(Arg(a, 2, name)[0], a[1], Math.Max),
                "min" => Zip(Arg(a, 2, name)[0], a[1], Math.Min),
                "normalize" => Normalize(Arg(a, 1, name)[0]),
                "pow" => Zip(Arg(a, 2, name)[0], a[1], Math.Pow),
                "radians" => Map(Arg(a, 1, name)[0], x => x * Math.PI / 180.0),
                "round" => Map(Arg(a, 1, name)[0], x => Math.Round(x, MidpointRounding.ToEven)),
                "rsqrt" => Map(Arg(a, 1, name)[0], x => 1.0 / Math.Sqrt(x)),
                "saturate" => Map(Arg(a, 1, name)[0], x => Math.Clamp(x, 0.0, 1.0)),
                "sign" => Map(Arg(a, 1, name)[0], x => Math.Sign(x)),
                "sin" => Map(Arg(a, 1, name)[0], Math.Sin),
                "sinh" => Map(Arg(a, 1, name)[0], Math.Sinh),
                "smoothstep" => Zip(Arg(a, 3, name)[0], a[1], a[2], (lo, hi, x) =>
                {
                    var t = Math.Clamp((x - lo) / (hi - lo), 0.0, 1.0);
                    return t * t * (3 - 2 * t);
                }),
                "sqrt" => Map(Arg(a, 1, name)[0], Math.Sqrt),
                "step" => Zip(Arg(a, 2, name)[0], a[1], (y, x) => x >= y ? 1 : 0),
                "tan" => Map(Arg(a, 1, name)[0], Math.Tan),
                "tanh" => Map(Arg(a, 1, name)[0], Math.Tanh),
                "trunc" => Map(Arg(a, 1, name)[0], Math.Truncate),
                "all" => [Arg(a, 1, name)[0].All(x => x != 0) ? 1 : 0],
                "any" => [Arg(a, 1, name)[0].Any(x => x != 0) ? 1 : 0],
                "srgb_to_linear" => Map(Arg(a, 1, name)[0], SrgbToLinear),
                _ => throw Error($"unknown function '{name}'"),
            };
        }

        private bool TryConstructor(string name, List<double[]> a, out double[] result)
        {
            result = [];
            string baseName;
            int n;
            if (name.Length > 0 && char.IsDigit(name[^1]))
            {
                baseName = name[..^1];
                n = name[^1] - '0';
            }
            else
            {
                baseName = name;
                n = 1;
            }
            if (baseName is not ("float" or "half" or "double" or "uint" or "int" or "bool") || n is < 1 or > 4)
                return false;

            var flat = a.SelectMany(x => x).ToList();
            if (flat.Count == 1 && n > 1)
                flat = Enumerable.Repeat(flat[0], n).ToList();
            if (flat.Count != n)
                throw Error($"{name}() takes {n} components, got {flat.Count}");
            result = baseName switch
            {
                "uint" => flat.Select(x => (double)(uint)Math.Max(0, Math.Truncate(x))).ToArray(),
                "int" => flat.Select(Math.Truncate).ToArray(),
                "bool" => flat.Select(x => x != 0 ? 1.0 : 0.0).ToArray(),
                _ => flat.ToArray(),
            };
            return true;
        }

        private List<double[]> Arg(List<double[]> a, int count, string name)
        {
            if (a.Count != count) throw Error($"{name}() takes {count} argument(s), got {a.Count}");
            return a;
        }

        private static double Dot(double[] a, double[] b)
        {
            double s = 0;
            for (int k = 0; k < Math.Min(a.Length, b.Length); k++) s += a[k] * b[k];
            return s;
        }

        private static double Length(double[] a) => Math.Sqrt(Dot(a, a));

        private static double[] Normalize(double[] a)
        {
            var l = Length(a);
            return a.Select(x => x / l).ToArray();
        }

        private double[] Cross(double[] a, double[] b)
        {
            if (a.Length != 3 || b.Length != 3) throw Error("cross() takes two float3");
            return [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]];
        }
    }

    private static double[] Map(double[] a, Func<double, double> f) => a.Select(f).ToArray();

    /// <summary>Componentwise binary op with HLSL-style scalar broadcast (vectors truncate to the shorter).</summary>
    private static double[] Zip(double[] a, double[] b, Func<double, double, double> f)
    {
        int n = a.Length == 1 ? b.Length : b.Length == 1 ? a.Length : Math.Min(a.Length, b.Length);
        var r = new double[n];
        for (int k = 0; k < n; k++)
            r[k] = f(a[a.Length == 1 ? 0 : k], b[b.Length == 1 ? 0 : k]);
        return r;
    }

    private static double[] Zip(double[] a, double[] b, double[] c, Func<double, double, double, double> f)
    {
        int n = new[] { a.Length, b.Length, c.Length }.Where(x => x > 1).DefaultIfEmpty(1).Min();
        var r = new double[n];
        for (int k = 0; k < n; k++)
            r[k] = f(a[a.Length == 1 ? 0 : k], b[b.Length == 1 ? 0 : k], c[c.Length == 1 ? 0 : k]);
        return r;
    }
}
