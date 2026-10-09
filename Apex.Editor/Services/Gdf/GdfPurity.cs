using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Apex.Editor.Services.Gdf;

/// <summary>
/// Which script functions are pure: their result depends only on their arguments, and calling them
/// changes nothing a caller can see. Such a call can be answered from an earlier call with equal
/// arguments in the same run — xanim builds the same 60-option notetrack list ~150 times per asset.
/// <para>
/// The test is conservative and static. A pure function reads only its own parameters and locals (never
/// a global, and no local shadows one), assigns only to those locals (never into an array, which may
/// be a caller's), calls only casts, the non-mutating string/array methods and other pure functions,
/// and takes nothing by reference. Host objects reach script only through arguments and globals, so
/// with host-free arguments (checked per call) a pure function cannot touch the asset.
/// </para>
/// </summary>
internal static class GdfPurity
{
    // The same parsed header can be shared between programs, so purity is per program: callees and
    // globals resolve against the program that runs it.
    private static readonly ConditionalWeakTable<GdfProgram, Dictionary<FuncDecl, bool>> Tables = new();

    private static readonly HashSet<string> ReadOnlyMethods = new(StringComparer.Ordinal)
    {
        // string
        "ToInt", "Length", "MakeUpper", "MakeLower", "Left", "Right", "Find", "Split", "IsSpecified",
        // array
        "length", "find",
    };

    public static bool IsPure(GdfProgram program, FuncDecl fn)
    {
        var table = Tables.GetValue(program, static _ => new Dictionary<FuncDecl, bool>());
        lock (table)
            return Check(program, fn, table);
    }

    private static bool Check(GdfProgram program, FuncDecl fn, Dictionary<FuncDecl, bool> table)
    {
        if (table.TryGetValue(fn, out var known))
            return known;
        table[fn] = false; // while in progress: recursion counts as impure
        var pure = new Analysis(program, table).Function(fn);
        table[fn] = pure;
        return pure;
    }

    private sealed class Analysis(GdfProgram program, Dictionary<FuncDecl, bool> table)
    {
        private readonly HashSet<string> _locals = new(StringComparer.Ordinal);

        public bool Function(FuncDecl fn)
        {
            foreach (var p in fn.Params)
            {
                if (p.ByRef)
                    return false;
                _locals.Add(p.Name);
            }
            CollectLocals(fn.Body);
            foreach (var g in program.Globals)
                foreach (var (name, _) in g.Vars)
                    if (_locals.Contains(name))
                        return false;
            foreach (var p in fn.Params)
                if (p.Default is { } d && !Expr(d))
                    return false;
            return Stmt(fn.Body);
        }

        private void CollectLocals(Stmt? s)
        {
            switch (s)
            {
                case Block b:
                    foreach (var x in b.Stmts) CollectLocals(x);
                    break;
                case VarDecl v:
                    foreach (var (name, _) in v.Vars) _locals.Add(name);
                    break;
                case IfStmt f:
                    CollectLocals(f.Then);
                    CollectLocals(f.Else);
                    break;
                case ForStmt fo:
                    CollectLocals(fo.Init);
                    CollectLocals(fo.Body);
                    break;
                case SwitchStmt sw:
                    foreach (var c in sw.Cases)
                        foreach (var x in c.Body) CollectLocals(x);
                    break;
            }
        }

        private bool Stmt(Stmt? s)
        {
            switch (s)
            {
                case null:
                case BreakStmt:
                case ContinueStmt:
                    return true;
                case Block b:
                    foreach (var x in b.Stmts)
                        if (!Stmt(x)) return false;
                    return true;
                case VarDecl v:
                    foreach (var (_, init) in v.Vars)
                        if (init is not null && !Expr(init)) return false;
                    return true;
                case ExprStmt es: return Expr(es.Expr);
                case IfStmt f: return Expr(f.Cond) && Stmt(f.Then) && Stmt(f.Else);
                case ForStmt fo:
                    return Stmt(fo.Init) && (fo.Cond is null || Expr(fo.Cond)) &&
                           (fo.Update is null || Expr(fo.Update)) && Stmt(fo.Body);
                case SwitchStmt sw:
                    if (!Expr(sw.Subject)) return false;
                    foreach (var c in sw.Cases)
                    {
                        foreach (var v in c.Values)
                            if (!Expr(v)) return false;
                        foreach (var x in c.Body)
                            if (!Stmt(x)) return false;
                    }
                    return true;
                case ReturnStmt r: return r.Value is null || Expr(r.Value);
                default: return false;
            }
        }

        private bool Expr(Expr e)
        {
            switch (e)
            {
                case LiteralExpr: return true;
                case IdentExpr id: return _locals.Contains(id.Name);
                case ArrayLitExpr arr:
                    foreach (var x in arr.Elements)
                        if (!Expr(x)) return false;
                    return true;
                case UnaryExpr u: return Expr(u.Operand);
                case BinaryExpr b: return Expr(b.Left) && Expr(b.Right);
                case TernaryExpr t: return Expr(t.Cond) && Expr(t.Then) && Expr(t.Else);
                case AssignExpr a: return a.Target is IdentExpr target && Expr(target) && Expr(a.Value);
                case MemberExpr m: return Expr(m.Target);
                case IndexExpr ix: return Expr(ix.Target) && Expr(ix.Index);
                case CallExpr c:
                    foreach (var x in c.Args)
                        if (!Expr(x)) return false;
                    return c.Callee switch
                    {
                        MemberExpr m => ReadOnlyMethods.Contains(m.Name) && Expr(m.Target),
                        IdentExpr { Name: "int" or "float" or "uint" or "string" } => true,
                        IdentExpr id => program.Funcs.TryGetValue(id.Name, out var callee) && Check(program, callee, table),
                        _ => false,
                    };
                default: return false;
            }
        }
    }
}
