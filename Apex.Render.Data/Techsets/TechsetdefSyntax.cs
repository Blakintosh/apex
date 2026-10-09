using System.Text;

namespace Apex.Render.Data.Techsets;

internal enum ValueKind
{
    String,
    Ident,
    GdtRef,
    Call,
}

/// <summary>
/// A techsetdef value: <c>"string"</c>, a bareword, a GDT reference <c>&lt;field[, default]&gt;</c>, or a
/// constructor call <c>Type(args) [: "parent"] [{ body }]</c>.
/// </summary>
internal sealed class TsValue
{
    public ValueKind Kind;
    public string Text = string.Empty;       // String/Ident text, GdtRef field, Call type
    public TsValue? RefDefault;              // <field, default>
    public List<TsValue> Args = [];          // Call args
    public string? Parent;                   // Call(...) : "parent"
    public List<TsStatement>? Body;          // Call(...) { body }

    public bool IsText => Kind is ValueKind.String or ValueKind.Ident;

    public override string ToString() => Kind switch
    {
        ValueKind.String => $"\"{Text}\"",
        ValueKind.Ident => Text,
        ValueKind.GdtRef => RefDefault is null ? $"<{Text}>" : $"<{Text}, {RefDefault}>",
        _ => $"{Text}({string.Join(", ", Args)})" + (Parent is null ? "" : $" : \"{Parent}\"") + (Body is null ? "" : " {...}"),
    };
}

/// <summary><c>key = v[, v...]</c> or <c>key += ...</c>; or a nested block statement.</summary>
internal sealed class TsStatement
{
    public string Key = string.Empty;
    public bool Append;
    public List<TsValue> Values = [];
    public TsBlock? Block;
}

/// <summary>A top-level element: <c>Type("name"[, args]) [: "parent"] { ... }</c>.</summary>
internal sealed class TsBlock
{
    public string Type = string.Empty;
    public List<TsValue> Args = [];
    public string Name => Args.Count > 0 ? Args[0].Text : string.Empty;
    public string? Parent;
    public List<TsStatement> Body = [];

    /// <summary><c>Type("name").key = value</c>: a property patch applied to an earlier block.</summary>
    public bool IsPatch;
}

/// <summary>Tokenizer + recursive-descent parser for preprocessed techsetdef text.</summary>
internal static class TechsetdefParser
{
    private readonly record struct Token(char Kind, string Text, int Line); // 's' string, 'i' ident, 'p' punct, 'e' eof

    private static List<Token> Lex(string src)
    {
        var toks = new List<Token>();
        int i = 0, line = 1;
        while (i < src.Length)
        {
            char c = src[i];
            if (c == '\n') { line++; i++; continue; }
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '/' && i + 1 < src.Length && src[i + 1] == '/')
            {
                while (i < src.Length && src[i] != '\n') i++;
                continue;
            }
            if (c == '/' && i + 1 < src.Length && src[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < src.Length && !(src[i] == '*' && src[i + 1] == '/'))
                {
                    if (src[i] == '\n') line++;
                    i++;
                }
                i += 2;
                continue;
            }
            if (c == '"')
            {
                int s = ++i;
                while (i < src.Length && src[i] != '"' && src[i] != '\n') i++;
                toks.Add(new Token('s', src[s..i], line));
                i++;
                continue;
            }
            if (c == '+' && i + 1 < src.Length && src[i + 1] == '=')
            {
                toks.Add(new Token('p', "+=", line));
                i += 2;
                continue;
            }
            if ("(){},=:<>;".Contains(c))
            {
                toks.Add(new Token('p', c.ToString(), line));
                i++;
                continue;
            }
            if (char.IsLetterOrDigit(c) || c is '_' or '$' or '.' or '-' or '+' or '/')
            {
                int s = i;
                while (i < src.Length && (char.IsLetterOrDigit(src[i]) || src[i] is '_' or '$' or '.' or '-' or '/' or '+')) i++;
                toks.Add(new Token('i', src[s..i], line));
                continue;
            }
            toks.Add(new Token('p', c.ToString(), line));
            i++;
        }
        toks.Add(new Token('e', string.Empty, line));
        return toks;
    }

    public static List<TsBlock> Parse(string src, string label, List<string> warnings)
    {
        var t = Lex(src);
        int p = 0;
        var blocks = new List<TsBlock>();
        while (t[p].Kind != 'e')
        {
            if (Is(";")) { p++; continue; }
            int start = p;
            try
            {
                blocks.Add(Block());
            }
            catch (FormatException ex)
            {
                warnings.Add($"{label}: parse error {ex.Message}");
                // Resync past the next balanced top-level '}'.
                p = start;
                int depth = 0;
                while (t[p].Kind != 'e')
                {
                    if (Is("{")) depth++;
                    else if (Is("}") && --depth <= 0) { p++; break; }
                    p++;
                }
            }
        }
        return blocks;

        bool Is(string s) => t[p].Kind == 'p' && t[p].Text == s;

        void Expect(string s)
        {
            if (!Is(s))
                throw new FormatException($"line {t[p].Line}: expected '{s}', got '{t[p].Text}'");
            p++;
        }

        TsBlock Block()
        {
            if (t[p].Kind != 'i')
                throw new FormatException($"line {t[p].Line}: expected an element type, got '{t[p].Text}'");
            var b = new TsBlock { Type = t[p++].Text };
            Expect("(");
            b.Args = ArgList(")");
            if (t[p].Kind == 'i' && t[p].Text.StartsWith('.'))
            {
                // Type("name").key = value
                var key = t[p++].Text[1..];
                if (key.Length == 0 && t[p].Kind == 'i') key = t[p++].Text;
                var st = new TsStatement { Key = key };
                if (Is("=")) p++;
                else if (Is("+=")) { st.Append = true; p++; }
                else throw new FormatException($"line {t[p].Line}: expected '=' after .{key}");
                st.Values.Add(Value());
                while (Is(",")) { p++; st.Values.Add(Value()); }
                b.IsPatch = true;
                b.Body.Add(st);
                return b;
            }
            if (Is(":")) { p++; b.Parent = t[p++].Text; }
            Expect("{");
            b.Body = Body();
            Expect("}");
            return b;
        }

        List<TsValue> ArgList(string close)
        {
            var args = new List<TsValue>();
            while (!Is(close))
            {
                if (t[p].Kind == 'e')
                    throw new FormatException($"line {t[p].Line}: unterminated argument list");
                args.Add(Value());
                if (Is(",")) p++;
                else if (!Is(close)) throw new FormatException($"line {t[p].Line}: expected ',' or '{close}', got '{t[p].Text}'");
            }
            p++;
            return args;
        }

        List<TsStatement> Body()
        {
            var body = new List<TsStatement>();
            while (!Is("}") && t[p].Kind != 'e')
            {
                if (Is(";")) { p++; continue; }
                if (t[p].Kind != 'i')
                    throw new FormatException($"line {t[p].Line}: expected a key, got '{t[p].Text}'");
                if (t[p + 1].Kind == 'p' && t[p + 1].Text == "(")
                {
                    body.Add(new TsStatement { Key = t[p].Text, Block = Block() });
                    continue;
                }
                var st = new TsStatement { Key = t[p++].Text };
                if (Is("=")) p++;
                else if (Is("+=")) { st.Append = true; p++; }
                else throw new FormatException($"line {t[p].Line}: expected '=' after {st.Key}, got '{t[p].Text}'");
                st.Values.Add(Value());
                while (Is(",")) { p++; st.Values.Add(Value()); }
                body.Add(st);
            }
            return body;
        }

        TsValue Value()
        {
            var tk = t[p];
            if (tk.Kind == 's')
            {
                p++;
                return new TsValue { Kind = ValueKind.String, Text = tk.Text };
            }
            if (tk.Kind == 'p' && tk.Text == "<")
            {
                p++;
                var sb = new StringBuilder();
                while (t[p].Kind != 'e' && !Is(",") && !Is(">")) sb.Append(t[p++].Text);
                var v = new TsValue { Kind = ValueKind.GdtRef, Text = sb.ToString() };
                if (Is(",")) { p++; v.RefDefault = Value(); }
                Expect(">");
                return v;
            }
            if (tk.Kind == 'i')
            {
                p++;
                if (Is("("))
                {
                    p++;
                    var call = new TsValue { Kind = ValueKind.Call, Text = tk.Text, Args = ArgList(")") };
                    if (Is(":")) { p++; call.Parent = t[p++].Text; }
                    if (Is("{")) { p++; call.Body = Body(); Expect("}"); }
                    return call;
                }
                return new TsValue { Kind = ValueKind.Ident, Text = tk.Text };
            }
            throw new FormatException($"line {tk.Line}: unexpected '{tk.Text}'");
        }
    }
}

/// <summary>
/// Field set of an element after inheritance: <c>: "parent"</c> copies the parent's fields, then the child's
/// statements apply in order (<c>=</c> replaces, <c>+=</c> appends).
/// </summary>
internal sealed class TsFields
{
    private readonly List<string> _keys = [];
    private readonly Dictionary<string, List<TsValue>> _values = new(StringComparer.Ordinal);

    public IReadOnlyList<string> Keys => _keys;

    public TsFields Clone()
    {
        var c = new TsFields();
        foreach (var k in _keys)
        {
            c._keys.Add(k);
            c._values[k] = [.. _values[k]];
        }
        return c;
    }

    public void Apply(IEnumerable<TsStatement> statements)
    {
        foreach (var s in statements)
        {
            if (s.Block is not null)
                continue;
            if (!_values.TryGetValue(s.Key, out var list))
            {
                _keys.Add(s.Key);
                _values[s.Key] = list = [];
            }
            if (!s.Append)
                list.Clear();
            list.AddRange(s.Values);
        }
    }

    public bool Has(string key) => _values.TryGetValue(key, out var l) && l.Count > 0;

    public IReadOnlyList<TsValue> All(string key) => _values.TryGetValue(key, out var l) ? l : [];

    public TsValue? First(string key) => _values.TryGetValue(key, out var l) && l.Count > 0 ? l[0] : null;

    public string? Text(string key) => First(key) is { IsText: true } v ? v.Text : null;

    public List<string> Strings(string key) =>
        All(key).Where(v => v.IsText).Select(v => v.Text).ToList();
}
