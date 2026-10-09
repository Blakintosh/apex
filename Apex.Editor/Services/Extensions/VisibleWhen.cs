using System;
using System.Collections.Generic;
using System.Globalization;

namespace Apex.Editor.Services.Extensions;

/// <summary>
/// An extension's <c>visibleWhen</c> rule: key references, literals (number, "string", true, false), == != &lt; &lt;=
/// &gt; &gt;=, &amp;&amp; || ! and parentheses. Nothing else: no functions, no assignment, nothing that runs. A rule
/// that doesn't parse is reported and the field shows, so a typo in a manifest can never hide data.
/// </summary>
public sealed class VisibleWhen
{
    /// <summary>Longest rule read; anything longer is a manifest mistake, not a condition.</summary>
    public const int MaxLength = 1000;

    private const int MaxDepth = 32;

    private readonly Node _root;

    private VisibleWhen(string text, Node root, HashSet<string> keys)
    {
        Text = text;
        _root = root;
        Keys = keys;
    }

    public string Text { get; }

    /// <summary>The keys the rule reads: only an edit to one of these can change its answer.</summary>
    public IReadOnlyCollection<string> Keys { get; }

    /// <summary>The rule, or null with <paramref name="error"/> saying where it went wrong.</summary>
    public static VisibleWhen? Parse(string text, out string? error)
    {
        error = null;
        if (text.Length > MaxLength)
        {
            error = $"it is longer than {MaxLength} characters";
            return null;
        }
        try
        {
            var parser = new Parser(text);
            var root = parser.Expression(0);
            parser.ExpectEnd();
            return new VisibleWhen(text, root, parser.Keys);
        }
        catch (FormatException ex)
        {
            error = ex.Message;
            return null;
        }
    }

    /// <summary>Whether the rule holds; <paramref name="valueOf"/> gives a key's effective value (null reads as empty).</summary>
    public bool Evaluate(Func<string, string?> valueOf) => Truthy(_root.Eval(valueOf));

    /// <summary>
    /// How a bare value reads as a condition: numbers are true unless 0; text is true unless empty, "false", "off" or
    /// "no" (GDT switches hold 0/1, weapon-tech's modes on/off).
    /// </summary>
    public static bool Truthy(string value)
    {
        value = value.Trim();
        if (TryNumber(value, out var n))
            return n != 0;
        return value.Length > 0
               && !value.Equals("false", StringComparison.OrdinalIgnoreCase)
               && !value.Equals("off", StringComparison.OrdinalIgnoreCase)
               && !value.Equals("no", StringComparison.OrdinalIgnoreCase);
    }

    private static bool Truthy(Value v) => v.Kind == ValueKind.Bool ? v.Bool : Truthy(v.Text);

    private static bool TryNumber(string s, out double d) =>
        double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out d);

    // ── Values and nodes ──────────────────────────────────────────────────

    private enum ValueKind { Text, Bool }

    private readonly record struct Value(ValueKind Kind, string Text, bool Bool)
    {
        public static Value Of(bool b) => new(ValueKind.Bool, b ? "1" : "0", b);
        public static Value Of(string s) => new(ValueKind.Text, s, false);
    }

    private abstract class Node
    {
        public abstract Value Eval(Func<string, string?> valueOf);
    }

    private sealed class Literal(Value value) : Node
    {
        public override Value Eval(Func<string, string?> valueOf) => value;
    }

    private sealed class KeyRef(string key) : Node
    {
        public override Value Eval(Func<string, string?> valueOf) => Value.Of(valueOf(key) ?? "");
    }

    private sealed class Not(Node operand) : Node
    {
        public override Value Eval(Func<string, string?> valueOf) => Value.Of(!Truthy(operand.Eval(valueOf)));
    }

    private sealed class Logic(bool and, Node left, Node right) : Node
    {
        public override Value Eval(Func<string, string?> valueOf) =>
            Value.Of(and
                ? Truthy(left.Eval(valueOf)) && Truthy(right.Eval(valueOf))
                : Truthy(left.Eval(valueOf)) || Truthy(right.Eval(valueOf)));
    }

    private sealed class Compare(string op, Node left, Node right) : Node
    {
        public override Value Eval(Func<string, string?> valueOf)
        {
            var l = left.Eval(valueOf);
            var r = right.Eval(valueOf);
            if (op is "==" or "!=")
            {
                bool equal;
                // true/false compare as switches: wtInspect == true holds for "1" and "on" alike.
                if (l.Kind == ValueKind.Bool || r.Kind == ValueKind.Bool)
                    equal = Truthy(l) == Truthy(r);
                else if (TryNumber(l.Text, out var ln) && TryNumber(r.Text, out var rn))
                    equal = ln == rn;
                else
                    equal = string.Equals(l.Text, r.Text, StringComparison.OrdinalIgnoreCase);
                return Value.Of(op == "==" ? equal : !equal);
            }
            // Ordering needs two numbers; an empty or non-numeric value is never less or greater than anything.
            if (!TryNumber(l.Text, out var a) || !TryNumber(r.Text, out var b))
                return Value.Of(false);
            return Value.Of(op switch
            {
                "<" => a < b,
                "<=" => a <= b,
                ">" => a > b,
                _ => a >= b,
            });
        }
    }

    // ── Parsing ───────────────────────────────────────────────────────────
    // Precedence as in C: ! binds tightest, then comparisons, then &&, then ||. Comparisons don't chain.
    //   or      := and ('||' and)*
    //   and     := compare ('&&' compare)*
    //   compare := unary (('==' | '!=' | '<' | '<=' | '>' | '>=') unary)?
    //   unary   := '!' unary | primary
    //   primary := number | string | 'true' | 'false' | key | '(' or ')'

    private enum TokenKind { Number, String, Bool, Key, Op, Open, Close, End }

    private readonly record struct Token(TokenKind Kind, string Text, int At);

    private sealed class Parser
    {
        private readonly List<Token> _tokens;
        private int _next;

        public readonly HashSet<string> Keys = new(StringComparer.OrdinalIgnoreCase);

        public Parser(string text) => _tokens = Tokenize(text);

        private Token Peek => _tokens[_next];

        public void ExpectEnd()
        {
            if (Peek.Kind != TokenKind.End)
                throw Error($"unexpected '{Peek.Text}'", Peek);
        }

        public Node Expression(int depth)
        {
            if (depth > MaxDepth)
                throw Error("it nests too deeply", Peek);
            var left = And(depth);
            while (IsOp("||"))
            {
                _next++;
                left = new Logic(false, left, And(depth));
            }
            return left;
        }

        private Node And(int depth)
        {
            var left = Comparison(depth);
            while (IsOp("&&"))
            {
                _next++;
                left = new Logic(true, left, Comparison(depth));
            }
            return left;
        }

        private Node Comparison(int depth)
        {
            var left = Unary(depth);
            if (Peek.Kind == TokenKind.Op && Peek.Text is "==" or "!=" or "<" or "<=" or ">" or ">=")
            {
                var op = _tokens[_next++].Text;
                return new Compare(op, left, Unary(depth));
            }
            return left;
        }

        private Node Unary(int depth)
        {
            if (!IsOp("!"))
                return Primary(depth);
            if (depth > MaxDepth)
                throw Error("it nests too deeply", Peek);
            _next++;
            return new Not(Unary(depth + 1));
        }

        private Node Primary(int depth)
        {
            var t = Peek;
            switch (t.Kind)
            {
                case TokenKind.Number:
                case TokenKind.String:
                    _next++;
                    return new Literal(Value.Of(t.Text));
                case TokenKind.Bool:
                    _next++;
                    return new Literal(Value.Of(t.Text == "true"));
                case TokenKind.Key:
                    _next++;
                    Keys.Add(t.Text);
                    return new KeyRef(t.Text);
                case TokenKind.Open:
                    _next++;
                    var inner = Expression(depth + 1);
                    if (Peek.Kind != TokenKind.Close)
                        throw Error(Peek.Kind == TokenKind.End ? "a '(' is never closed" : $"expected ')' but found '{Peek.Text}'", Peek);
                    _next++;
                    return inner;
                case TokenKind.End:
                    throw Error(_tokens.Count == 1 ? "it is empty" : "it ends where a value should be", t);
                default:
                    throw Error($"expected a value but found '{t.Text}'", t);
            }
        }

        private bool IsOp(string op) => Peek.Kind == TokenKind.Op && Peek.Text == op;

        private static FormatException Error(string what, Token at) => new($"{what} (at character {at.At + 1})");

        private static List<Token> Tokenize(string s)
        {
            var tokens = new List<Token>();
            var i = 0;
            while (i < s.Length)
            {
                var c = s[i];
                if (char.IsWhiteSpace(c))
                {
                    i++;
                    continue;
                }
                var start = i;
                if (c is '"' or '\'')
                {
                    // No escapes: a value is matched as the GDT holds it, backslashes and all.
                    var end = s.IndexOf(c, i + 1);
                    if (end < 0)
                        throw Error("a string is never closed", new Token(TokenKind.String, "", start));
                    tokens.Add(new Token(TokenKind.String, s[(i + 1)..end], start));
                    i = end + 1;
                    continue;
                }
                if (char.IsAsciiDigit(c) || (c is '-' or '.' && i + 1 < s.Length && (char.IsAsciiDigit(s[i + 1]) || s[i + 1] == '.')))
                {
                    i++;
                    while (i < s.Length && (char.IsAsciiDigit(s[i]) || s[i] == '.'))
                        i++;
                    var number = s[start..i];
                    if (!TryNumber(number, out _))
                        throw Error($"'{number}' isn't a number", new Token(TokenKind.Number, number, start));
                    tokens.Add(new Token(TokenKind.Number, number, start));
                    continue;
                }
                if (char.IsAsciiLetter(c) || c == '_')
                {
                    while (i < s.Length && (char.IsAsciiLetterOrDigit(s[i]) || s[i] == '_'))
                        i++;
                    var word = s[start..i];
                    tokens.Add(new Token(word is "true" or "false" ? TokenKind.Bool : TokenKind.Key, word, start));
                    continue;
                }
                var two = i + 1 < s.Length ? s.Substring(i, 2) : "";
                if (two is "==" or "!=" or "<=" or ">=" or "&&" or "||")
                {
                    tokens.Add(new Token(TokenKind.Op, two, start));
                    i += 2;
                    continue;
                }
                switch (c)
                {
                    case '<' or '>' or '!':
                        tokens.Add(new Token(TokenKind.Op, c.ToString(), start));
                        break;
                    case '(':
                        tokens.Add(new Token(TokenKind.Open, "(", start));
                        break;
                    case ')':
                        tokens.Add(new Token(TokenKind.Close, ")", start));
                        break;
                    case '=':
                        throw Error("'=' compares as '=='", new Token(TokenKind.Op, "=", start));
                    case '&' or '|':
                        throw Error($"'{c}' combines as '{c}{c}'", new Token(TokenKind.Op, c.ToString(), start));
                    default:
                        throw Error($"'{c}' isn't part of a rule", new Token(TokenKind.Op, c.ToString(), start));
                }
                i++;
            }
            tokens.Add(new Token(TokenKind.End, "", s.Length));
            return tokens;
        }
    }
}
