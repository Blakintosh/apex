using System.Text;
using System.Text.RegularExpressions;

namespace Apex.Render.Data.Techsets;

/// <summary>
/// C-like line preprocessor applied to techsetdef text (materials.md §2.1): <c>#include "name"</c> (each
/// file once per translation), <c>#if/#elif/#else/#endif</c> with string comparisons, <c>#ifdef/#ifndef</c>,
/// <c>#define</c>. Undefined names compare as empty strings, so <c>PLAT_ORBIS == "1"</c> is false.
/// </summary>
internal static partial class TechsetdefPreprocessor
{
    [GeneratedRegex("\"([^\"]+)\"|<([^>]+)>")]
    private static partial Regex IncludeRx();

    [GeneratedRegex("\"[^\"]*\"|==|!=|&&|\\|\\||[()!]|[A-Za-z_][A-Za-z0-9_]*|\\d+")]
    private static partial Regex ExprTokenRx();

    public static string Run(
        string file,
        Dictionary<string, string> defines,
        Func<string, string, string?> resolveInclude,
        Func<string, string[]> readLines,
        List<string> includeOrder,
        List<string> warnings)
    {
        var sb = new StringBuilder();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Process(file);
        return sb.ToString();

        void Process(string f)
        {
            if (!seen.Add(f))
                return;
            includeOrder.Add(f);
            var stack = new Stack<(bool ParentActive, bool Taken)>();
            bool active = true;
            foreach (var raw in readLines(f))
            {
                var line = raw.TrimStart();
                if (!line.StartsWith('#'))
                {
                    sb.AppendLine(active ? raw : string.Empty);
                    continue;
                }

                var dir = line[1..].TrimStart();
                int wl = 0;
                while (wl < dir.Length && char.IsLetter(dir[wl])) wl++;
                var word = dir[..wl];
                var rest = dir[wl..].Trim();
                if (word != "include")
                {
                    int cmt = rest.IndexOf("//", StringComparison.Ordinal);
                    if (cmt >= 0) rest = rest[..cmt].Trim();
                }

                switch (word)
                {
                    case "include":
                        if (!active) break;
                        var m = IncludeRx().Match(rest);
                        var name = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                        var inc = resolveInclude(name, f);
                        if (inc is null) warnings.Add($"{Path.GetFileName(f)}: unresolved #include \"{name}\"");
                        else Process(inc);
                        break;
                    case "if" or "ifdef" or "ifndef":
                    {
                        bool v = word switch
                        {
                            "ifdef" => defines.ContainsKey(rest),
                            "ifndef" => !defines.ContainsKey(rest),
                            _ => Evaluate(rest, defines),
                        };
                        stack.Push((active, v));
                        active = active && v;
                        break;
                    }
                    case "elif" or "else" or "endif" when stack.Count == 0:
                        warnings.Add($"{Path.GetFileName(f)}: stray #{word}");
                        active = true;
                        break;
                    case "elif":
                    {
                        var (pa, taken) = stack.Pop();
                        bool v = !taken && Evaluate(rest, defines);
                        stack.Push((pa, taken || v));
                        active = pa && v;
                        break;
                    }
                    case "else":
                    {
                        var (pa, taken) = stack.Pop();
                        stack.Push((pa, true));
                        active = pa && !taken;
                        break;
                    }
                    case "endif":
                        active = stack.Pop().ParentActive;
                        break;
                    case "define":
                        if (active)
                        {
                            var parts = rest.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
                            if (parts.Length > 0) defines[parts[0]] = parts.Length > 1 ? parts[1].Trim('"') : "1";
                        }
                        break;
                    default:
                        warnings.Add($"{Path.GetFileName(f)}: unknown directive '{line}'");
                        break;
                }
                sb.AppendLine();
            }
            if (stack.Count != 0)
                warnings.Add($"{Path.GetFileName(f)}: unterminated #if");
        }
    }

    /// <summary>Evaluates a <c>#if</c> expression: names, "strings", ==, !=, !, &amp;&amp;, ||, parens, integers, defined().</summary>
    public static bool Evaluate(string expr, IReadOnlyDictionary<string, string> defs)
    {
        var toks = ExprTokenRx().Matches(expr).Select(m => m.Value).ToList();
        int p = 0;
        return Truthy(Or());

        string Or()
        {
            var l = And();
            while (p < toks.Count && toks[p] == "||")
            {
                p++;
                var r = And();
                l = Truthy(l) || Truthy(r) ? "1" : "0";
            }
            return l;
        }

        string And()
        {
            var l = Cmp();
            while (p < toks.Count && toks[p] == "&&")
            {
                p++;
                var r = Cmp();
                l = Truthy(l) && Truthy(r) ? "1" : "0";
            }
            return l;
        }

        string Cmp()
        {
            var l = Unary();
            while (p < toks.Count && (toks[p] == "==" || toks[p] == "!="))
            {
                var op = toks[p++];
                var r = Unary();
                l = (l == r) == (op == "==") ? "1" : "0";
            }
            return l;
        }

        string Unary()
        {
            if (p >= toks.Count) return string.Empty;
            var t = toks[p++];
            if (t == "!") return Truthy(Unary()) ? "0" : "1";
            if (t == "(")
            {
                var x = Or();
                if (p < toks.Count && toks[p] == ")") p++;
                return x;
            }
            if (t.StartsWith('"')) return t[1..^1];
            if (char.IsDigit(t[0])) return t;
            if (t == "defined")
            {
                bool paren = p < toks.Count && toks[p] == "(";
                if (paren) p++;
                var id = p < toks.Count ? toks[p++] : string.Empty;
                if (paren && p < toks.Count && toks[p] == ")") p++;
                return defs.ContainsKey(id) ? "1" : "0";
            }
            return defs.TryGetValue(t, out var val) ? val : string.Empty;
        }

        static bool Truthy(string s) => s.Length > 0 && s != "0";
    }
}
