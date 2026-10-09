using System;
using System.Collections.Generic;

namespace Apex.Editor.Services.Gdf;

internal sealed class GdfParseException : Exception
{
    public GdfParseException(string message) : base(message) { }
}

/// <summary>
/// Recursive-descent parser for the AngelScript subset. Merges declarations
/// into a shared <see cref="GdfProgram"/> so a file and its includes share one
/// global scope.
/// </summary>
internal sealed class GdfParser
{
    private readonly List<Token> _t;
    private int _p;

    private static readonly HashSet<string> TypeStarters = new(StringComparer.Ordinal)
    {
        "void", "int", "float", "double", "bool", "string", "uint", "array",
        "const", "asset", "entryControl",
    };

    private GdfParser(List<Token> tokens) => _t = tokens;

    public static void ParseInto(List<Token> tokens, GdfProgram prog)
    {
        var parser = new GdfParser(tokens);
        parser.ParseTopLevel(prog);
    }

    // ── Token helpers ────────────────────────────────────────────────────────
    private Token Cur => _t[_p];
    private Token Next => _t[Math.Min(_p + 1, _t.Count - 1)];
    private bool IsEof => Cur.Kind == TokKind.Eof;
    private Token Advance() => _t[_p++];
    private bool CheckOp(string s) => Cur.Kind == TokKind.Op && Cur.Text == s;
    private bool CheckIdent(string s) => Cur.Kind == TokKind.Ident && Cur.Text == s;

    private Token Expect(string op)
    {
        if (!CheckOp(op))
            throw new GdfParseException($"Expected '{op}' but found '{Cur.Text}' (line {Cur.Line})");
        return Advance();
    }

    private bool Accept(string op)
    {
        if (CheckOp(op)) { Advance(); return true; }
        return false;
    }

    // ── Top level ────────────────────────────────────────────────────────────
    private void ParseTopLevel(GdfProgram prog)
    {
        while (!IsEof)
        {
            // Only declarations at top level: function or global var.
            var (typeName, isArray) = ParseType();
            string name = Advance().Text; // declaration identifier

            if (CheckOp("("))
            {
                var fn = ParseFunctionRest(name);
                prog.Funcs[fn.Name] = fn;
            }
            else
            {
                var decl = new VarDecl { TypeName = typeName, IsArray = isArray };
                ParseDeclaratorsRest(decl, name);
                prog.Globals.Add(decl);
            }
        }
    }

    private FuncDecl ParseFunctionRest(string name)
    {
        var fn = new FuncDecl { Name = name };
        Expect("(");
        if (!CheckOp(")"))
        {
            do
            {
                var (ptype, pIsArray) = ParseType();
                bool byRef = Accept("&");
                string pname = Advance().Text;
                Expr? def = null;
                if (Accept("="))
                    def = ParseAssignment();
                fn.Params.Add(new Param { TypeName = ptype, IsArray = pIsArray, ByRef = byRef, Name = pname, Default = def });
            }
            while (Accept(","));
        }
        Expect(")");
        fn.Body = ParseBlock();
        return fn;
    }

    // ── Types ────────────────────────────────────────────────────────────────
    private (string name, bool isArray) ParseType()
    {
        if (CheckIdent("const")) Advance();
        string baseName = Advance().Text;
        bool isArray = false;
        if (baseName == "array" && CheckOp("<"))
        {
            Advance(); // <
            ParseType(); // element type (ignored)
            Expect(">");
            isArray = true;
        }
        return (baseName, isArray);
    }

    private bool LooksLikeDeclaration()
    {
        if (Cur.Kind != TokKind.Ident || !TypeStarters.Contains(Cur.Text))
            return false;
        // `int(` is a cast expression, not a declaration.
        if (Cur.Text == "array")
            return true; // array<...> is always a declaration here
        if (Cur.Text == "const")
            return true;
        // A base type immediately followed by an identifier => declaration.
        return Next.Kind == TokKind.Ident;
    }

    // ── Statements ───────────────────────────────────────────────────────────
    private Block ParseBlock()
    {
        var block = new Block();
        Expect("{");
        while (!CheckOp("}") && !IsEof)
            block.Stmts.Add(ParseStatement());
        Expect("}");
        return block;
    }

    private Stmt ParseStatement()
    {
        if (CheckOp("{")) return ParseBlock();
        if (CheckOp(";")) { Advance(); return new Block(); }

        if (CheckIdent("if")) return ParseIf();
        if (CheckIdent("for")) return ParseFor();
        if (CheckIdent("while")) return ParseWhile();
        if (CheckIdent("switch")) return ParseSwitch();
        if (CheckIdent("return"))
        {
            Advance();
            Expr? val = CheckOp(";") ? null : ParseAssignment();
            Accept(";");
            return new ReturnStmt { Value = val };
        }
        if (CheckIdent("break")) { Advance(); Accept(";"); return new BreakStmt(); }
        if (CheckIdent("continue")) { Advance(); Accept(";"); return new ContinueStmt(); }

        if (LooksLikeDeclaration())
            return ParseVarDeclStatement();

        var expr = ParseAssignment();
        Accept(";");
        return new ExprStmt { Expr = expr };
    }

    private Stmt ParseVarDeclStatement()
    {
        var (typeName, isArray) = ParseType();
        string name = Advance().Text;
        var decl = new VarDecl { TypeName = typeName, IsArray = isArray };
        ParseDeclaratorsRest(decl, name);
        return decl;
    }

    private void ParseDeclaratorsRest(VarDecl decl, string firstName)
    {
        Expr? init = null;
        if (Accept("="))
            init = ParseInitializer();
        decl.Vars.Add((firstName, init));

        while (Accept(","))
        {
            string nm = Advance().Text;
            Expr? more = null;
            if (Accept("="))
                more = ParseInitializer();
            decl.Vars.Add((nm, more));
        }
        Accept(";");
    }

    private Expr ParseInitializer()
    {
        if (CheckOp("{"))
            return ParseArrayLiteral();
        return ParseAssignment();
    }

    private Expr ParseArrayLiteral()
    {
        Expect("{");
        var arr = new ArrayLitExpr();
        if (!CheckOp("}"))
        {
            do
            {
                if (CheckOp("}")) break; // trailing comma
                arr.Elements.Add(ParseInitializer());
            }
            while (Accept(","));
        }
        Expect("}");
        return arr;
    }

    private Stmt ParseIf()
    {
        Advance(); // if
        Expect("(");
        var cond = ParseAssignment();
        Expect(")");
        var then = ParseStatement();
        Stmt? els = null;
        if (CheckIdent("else"))
        {
            Advance();
            els = ParseStatement();
        }
        return new IfStmt { Cond = cond, Then = then, Else = els };
    }

    private Stmt ParseFor()
    {
        Advance(); // for
        Expect("(");
        Stmt? init;
        if (CheckOp(";")) { Advance(); init = null; }
        else if (LooksLikeDeclaration()) { init = ParseVarDeclStatement(); }
        else { init = new ExprStmt { Expr = ParseAssignment() }; Accept(";"); }

        Expr? cond = CheckOp(";") ? null : ParseAssignment();
        Expect(";");
        Expr? update = CheckOp(")") ? null : ParseAssignment();
        Expect(")");
        var body = ParseStatement();
        return new ForStmt { Init = init, Cond = cond, Update = update, Body = body };
    }

    private Stmt ParseWhile()
    {
        // Modelled as a for-loop with only a condition (rare in corpus).
        Advance(); // while
        Expect("(");
        var cond = ParseAssignment();
        Expect(")");
        var body = ParseStatement();
        return new ForStmt { Init = null, Cond = cond, Update = null, Body = body };
    }

    private Stmt ParseSwitch()
    {
        Advance(); // switch
        Expect("(");
        var subject = ParseAssignment();
        Expect(")");
        Expect("{");
        var sw = new SwitchStmt { Subject = subject };
        while (!CheckOp("}") && !IsEof)
        {
            var clause = new CaseClause();
            // Collect consecutive case/default labels sharing one body.
            while (CheckIdent("case") || CheckIdent("default"))
            {
                if (CheckIdent("default")) { Advance(); clause.IsDefault = true; }
                else { Advance(); clause.Values.Add(ParseAssignment()); }
                Expect(":");
            }
            while (!CheckIdent("case") && !CheckIdent("default") && !CheckOp("}") && !IsEof)
                clause.Body.Add(ParseStatement());
            sw.Cases.Add(clause);
        }
        Expect("}");
        return sw;
    }

    // ── Expressions (precedence climbing) ────────────────────────────────────
    private Expr ParseAssignment()
    {
        var left = ParseTernary();
        if (Cur.Kind == TokKind.Op &&
            (Cur.Text == "=" || Cur.Text == "+=" || Cur.Text == "-=" || Cur.Text == "*=" || Cur.Text == "/="))
        {
            string op = Advance().Text;
            var value = ParseAssignment();
            return new AssignExpr { Target = left, Op = op, Value = value };
        }
        return left;
    }

    private Expr ParseTernary()
    {
        var cond = ParseBinary(0);
        if (CheckOp("?"))
        {
            Advance();
            var then = ParseAssignment();
            Expect(":");
            var els = ParseAssignment();
            return new TernaryExpr { Cond = cond, Then = then, Else = els };
        }
        return cond;
    }

    // Precedence tiers, lowest first.
    private static readonly string[][] BinaryTiers =
    {
        new[] { "||" },
        new[] { "&&" },
        new[] { "==", "!=" },
        new[] { "<", "<=", ">", ">=" },
        new[] { "+", "-" },
        new[] { "*", "/", "%" },
    };

    private Expr ParseBinary(int tier)
    {
        if (tier >= BinaryTiers.Length)
            return ParseUnary();

        var left = ParseBinary(tier + 1);
        while (Cur.Kind == TokKind.Op && Array.IndexOf(BinaryTiers[tier], Cur.Text) >= 0)
        {
            string op = Advance().Text;
            var right = ParseBinary(tier + 1);
            left = new BinaryExpr { Op = op, Left = left, Right = right };
        }
        return left;
    }

    private Expr ParseUnary()
    {
        if (CheckOp("!") || CheckOp("-") || CheckOp("+"))
        {
            string op = Advance().Text;
            var operand = ParseUnary();
            return new UnaryExpr { Op = op, Operand = operand, Prefix = true };
        }
        if (CheckOp("++") || CheckOp("--"))
        {
            string op = Advance().Text;
            var operand = ParseUnary();
            return new UnaryExpr { Op = op, Operand = operand, Prefix = true };
        }
        return ParsePostfix();
    }

    private Expr ParsePostfix()
    {
        var expr = ParsePrimary();
        while (true)
        {
            if (CheckOp("."))
            {
                Advance();
                string name = Advance().Text;
                if (CheckOp("("))
                {
                    var args = ParseArgs();
                    expr = new CallExpr { Callee = new MemberExpr { Target = expr, Name = name }, Args = args };
                }
                else
                {
                    expr = new MemberExpr { Target = expr, Name = name };
                }
            }
            else if (CheckOp("("))
            {
                var args = ParseArgs();
                expr = new CallExpr { Callee = expr, Args = args };
            }
            else if (CheckOp("["))
            {
                Advance();
                var idx = ParseAssignment();
                Expect("]");
                expr = new IndexExpr { Target = expr, Index = idx };
            }
            else if (CheckOp("++") || CheckOp("--"))
            {
                string op = Advance().Text;
                expr = new UnaryExpr { Op = op, Operand = expr, Prefix = false };
            }
            else
            {
                break;
            }
        }
        return expr;
    }

    private List<Expr> ParseArgs()
    {
        Expect("(");
        var args = new List<Expr>();
        if (!CheckOp(")"))
        {
            do
            {
                args.Add(ParseAssignment());
            }
            while (Accept(","));
        }
        Expect(")");
        return args;
    }

    private Expr ParsePrimary()
    {
        var t = Cur;
        switch (t.Kind)
        {
            case TokKind.Number:
                Advance();
                return new LiteralExpr { Value = t.Value };
            case TokKind.String:
            {
                // Adjacent string literals concatenate (C / AngelScript).
                Advance();
                string joined = (string)(t.Value ?? "");
                while (Cur.Kind == TokKind.String)
                    joined += (string)(Advance().Value ?? "");
                return new LiteralExpr { Value = joined };
            }
            case TokKind.Bool:
                Advance();
                return new LiteralExpr { Value = t.Value };
            case TokKind.Ident:
                Advance();
                return new IdentExpr { Name = t.Text };
            case TokKind.Op when t.Text == "(":
                Advance();
                var inner = ParseAssignment();
                Expect(")");
                return inner;
            case TokKind.Op when t.Text == "{":
                return ParseArrayLiteral();
            default:
                throw new GdfParseException($"Unexpected token '{t.Text}' (line {t.Line})");
        }
    }
}
