using System.Collections.Generic;

namespace Apex.Editor.Services.Gdf;

// ── Expressions ──────────────────────────────────────────────────────────────
internal abstract class Expr { }

internal sealed class LiteralExpr : Expr { public object? Value; }
internal sealed class IdentExpr : Expr { public string Name = ""; }
internal sealed class ArrayLitExpr : Expr { public List<Expr> Elements = new(); }
internal sealed class UnaryExpr : Expr { public string Op = ""; public Expr Operand = null!; public bool Prefix; }
internal sealed class BinaryExpr : Expr { public string Op = ""; public Expr Left = null!; public Expr Right = null!; }
internal sealed class TernaryExpr : Expr { public Expr Cond = null!; public Expr Then = null!; public Expr Else = null!; }
internal sealed class AssignExpr : Expr { public Expr Target = null!; public string Op = ""; public Expr Value = null!; }
internal sealed class CallExpr : Expr { public Expr Callee = null!; public List<Expr> Args = new(); }
internal sealed class MemberExpr : Expr { public Expr Target = null!; public string Name = ""; }
internal sealed class IndexExpr : Expr { public Expr Target = null!; public Expr Index = null!; }

// ── Statements ───────────────────────────────────────────────────────────────
internal abstract class Stmt { }

internal sealed class Block : Stmt { public List<Stmt> Stmts = new(); }
internal sealed class VarDecl : Stmt
{
    public string TypeName = "";
    public bool IsArray;
    public List<(string Name, Expr? Init)> Vars = new();
}
internal sealed class ExprStmt : Stmt { public Expr Expr = null!; }
internal sealed class IfStmt : Stmt { public Expr Cond = null!; public Stmt Then = null!; public Stmt? Else; }
internal sealed class ForStmt : Stmt { public Stmt? Init; public Expr? Cond; public Expr? Update; public Stmt Body = null!; }
internal sealed class SwitchStmt : Stmt { public Expr Subject = null!; public List<CaseClause> Cases = new(); }
internal sealed class CaseClause { public List<Expr> Values = new(); public bool IsDefault; public List<Stmt> Body = new(); }
internal sealed class ReturnStmt : Stmt { public Expr? Value; }
internal sealed class BreakStmt : Stmt { }
internal sealed class ContinueStmt : Stmt { }

// ── Declarations / program ───────────────────────────────────────────────────
internal sealed class Param
{
    public string TypeName = "";
    public bool IsArray;
    public bool ByRef;
    public string Name = "";
    public Expr? Default;
}

internal sealed class FuncDecl
{
    public string Name = "";
    public List<Param> Params = new();
    public Block Body = null!;
}

/// <summary>A parsed program: a merged view of a deffile and its includes.</summary>
internal sealed class GdfProgram
{
    public Dictionary<string, FuncDecl> Funcs = new();
    public List<VarDecl> Globals = new();
}
