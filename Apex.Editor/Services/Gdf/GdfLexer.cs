using System;
using System.Collections.Generic;
using System.Globalization;

namespace Apex.Editor.Services.Gdf;

internal enum TokKind
{
    Ident,
    Number,
    String,
    Bool,
    Op,
    Eof,
}

internal sealed class Token
{
    public TokKind Kind;
    public string Text = "";
    public object? Value;
    public int Line;

    public override string ToString() => $"{Kind}:{Text}";
}

/// <summary>
/// Lexer for the AngelScript subset used by BO3 deffiles (.awi / .h).
/// Strips comments, understands triple-quoted heredocs, numeric suffixes and
/// skips preprocessor directives (which the loader resolves separately).
/// </summary>
internal static class GdfLexer
{
    private static readonly string[] MultiCharOps =
    {
        "+=", "-=", "*=", "/=", "==", "!=", "<=", ">=", "&&", "||", "++", "--",
    };

    private const string SingleCharOps = "+-*/%=<>!?:.,;(){}[]&|";

    // One shared string per single-char operator instead of a c.ToString() per occurrence.
    private static readonly string[] SingleCharOpText = Array.ConvertAll(SingleCharOps.ToCharArray(), c => c.ToString());

    private static string? MatchMultiCharOp(char a, char b)
    {
        foreach (var op in MultiCharOps)
            if (op[0] == a && op[1] == b)
                return op;
        return null;
    }

    public static List<Token> Tokenize(string src)
    {
        var toks = new List<Token>();
        var sb = new System.Text.StringBuilder();
        int i = 0, n = src.Length, line = 1;

        while (i < n)
        {
            char c = src[i];

            // Newlines / whitespace
            if (c == '\n') { line++; i++; continue; }
            if (char.IsWhiteSpace(c)) { i++; continue; }

            // Preprocessor directive (#include ...) — skip to end of line.
            if (c == '#')
            {
                while (i < n && src[i] != '\n') i++;
                continue;
            }

            // Comments
            if (c == '/' && i + 1 < n && src[i + 1] == '/')
            {
                while (i < n && src[i] != '\n') i++;
                continue;
            }
            if (c == '/' && i + 1 < n && src[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < n && !(src[i] == '*' && src[i + 1] == '/'))
                {
                    if (src[i] == '\n') line++;
                    i++;
                }
                i += 2;
                continue;
            }

            // Triple-quoted heredoc — must be tested before plain string.
            if (c == '"' && i + 2 < n && src[i + 1] == '"' && src[i + 2] == '"')
            {
                int startLine = line;
                i += 3;
                int start = i;
                while (i + 2 < n && !(src[i] == '"' && src[i + 1] == '"' && src[i + 2] == '"'))
                {
                    if (src[i] == '\n') line++;
                    i++;
                }
                string text = src.Substring(start, Math.Max(0, i - start));
                i += 3;
                toks.Add(new Token { Kind = TokKind.String, Text = text, Value = text, Line = startLine });
                continue;
            }

            // Plain string
            if (c == '"')
            {
                int startLine = line;
                i++;
                sb.Clear();
                while (i < n && src[i] != '"')
                {
                    char ch = src[i];
                    if (ch == '\\' && i + 1 < n)
                    {
                        char e = src[i + 1];
                        sb.Append(e switch
                        {
                            'n' => '\n',
                            'r' => '\r',
                            't' => '\t',
                            '"' => '"',
                            '\\' => '\\',
                            _ => e,
                        });
                        i += 2;
                        continue;
                    }
                    if (ch == '\n') line++;
                    sb.Append(ch);
                    i++;
                }
                i++; // closing quote
                string text = sb.ToString();
                toks.Add(new Token { Kind = TokKind.String, Text = text, Value = text, Line = startLine });
                continue;
            }

            // Number (or leading-dot float)
            if (char.IsDigit(c) || (c == '.' && i + 1 < n && char.IsDigit(src[i + 1])))
            {
                int start = i;
                bool isFloat = false;
                while (i < n && char.IsDigit(src[i])) i++;
                if (i < n && src[i] == '.')
                {
                    isFloat = true;
                    i++;
                    while (i < n && char.IsDigit(src[i])) i++;
                }
                if (i < n && (src[i] == 'e' || src[i] == 'E'))
                {
                    isFloat = true;
                    i++;
                    if (i < n && (src[i] == '+' || src[i] == '-')) i++;
                    while (i < n && char.IsDigit(src[i])) i++;
                }
                string numText = src.Substring(start, i - start);
                if (i < n && (src[i] == 'f' || src[i] == 'F'))
                {
                    isFloat = true;
                    i++;
                }
                object val = isFloat
                    ? double.Parse(numText, CultureInfo.InvariantCulture)
                    : long.TryParse(numText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)
                        ? l
                        : double.Parse(numText, CultureInfo.InvariantCulture);
                toks.Add(new Token { Kind = TokKind.Number, Text = numText, Value = val, Line = line });
                continue;
            }

            // Identifier / keyword / bool
            if (char.IsLetter(c) || c == '_')
            {
                int start = i;
                while (i < n && (char.IsLetterOrDigit(src[i]) || src[i] == '_')) i++;
                string id = src.Substring(start, i - start);
                if (id == "true")
                    toks.Add(new Token { Kind = TokKind.Bool, Text = id, Value = true, Line = line });
                else if (id == "false")
                    toks.Add(new Token { Kind = TokKind.Bool, Text = id, Value = false, Line = line });
                else
                    toks.Add(new Token { Kind = TokKind.Ident, Text = id, Line = line });
                continue;
            }

            // Operators
            if (i + 1 < n && MatchMultiCharOp(c, src[i + 1]) is { } op)
            {
                toks.Add(new Token { Kind = TokKind.Op, Text = op, Line = line });
                i += 2;
                continue;
            }

            var single = SingleCharOps.IndexOf(c);
            if (single >= 0)
            {
                toks.Add(new Token { Kind = TokKind.Op, Text = SingleCharOpText[single], Line = line });
                i++;
                continue;
            }

            // Unknown char — skip defensively.
            i++;
        }

        toks.Add(new Token { Kind = TokKind.Eof, Text = "", Line = line });
        return toks;
    }
}
