using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Apex.Editor.Services.Gdf;

/// <summary>Value coercion / formatting shared by the interpreter and host objects.</summary>
internal static class GdfValue
{
    public static string AsString(object? v) => v switch
    {
        null => "",
        string s => s,
        GdfConcat c => c.ToString(),
        bool b => b ? "1" : "0",
        long l => l.ToString(CultureInfo.InvariantCulture),
        double d => FormatDouble(d),
        _ => v.ToString() ?? "",
    };

    public static string FormatDouble(double d)
    {
        if (double.IsNaN(d) || double.IsInfinity(d)) return "0";
        // Integral doubles print without a decimal point (matches script concat).
        if (d == Math.Floor(d) && Math.Abs(d) < 1e15)
            return ((long)d).ToString(CultureInfo.InvariantCulture);
        return d.ToString("0.################", CultureInfo.InvariantCulture);
    }

    public static bool AsBool(object? v) => v switch
    {
        null => false,
        bool b => b,
        long l => l != 0,
        double d => d != 0,
        string s => s.Length > 0 && s != "0" && s != "false",
        GdfConcat c => AsBool(c.ToString()),
        _ => true,
    };

    public static long AsLong(object? v) => v switch
    {
        null => 0,
        bool b => b ? 1 : 0,
        long l => l,
        double d => (long)d,
        string s => ParseLeadingInt(s),
        GdfConcat c => ParseLeadingInt(c.ToString()),
        _ => 0,
    };

    public static double AsDouble(object? v) => v switch
    {
        null => 0,
        bool b => b ? 1 : 0,
        long l => l,
        double d => d,
        string s => double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var r) ? r : 0,
        GdfConcat c => AsDouble(c.ToString()),
        _ => 0,
    };

    private static long ParseLeadingInt(string s)
    {
        s = s.Trim();
        int i = 0, sign = 1;
        if (i < s.Length && (s[i] == '-' || s[i] == '+')) { if (s[i] == '-') sign = -1; i++; }
        long val = 0; bool any = false;
        while (i < s.Length && char.IsDigit(s[i])) { val = val * 10 + (s[i] - '0'); i++; any = true; }
        return any ? sign * val : 0;
    }

    public static bool IsNumeric(object? v) => v is long or double;

    /// <summary>True for a script string, plain or still being built by <c>+=</c>.</summary>
    public static bool IsString(object? v) => v is string or GdfConcat;

    // Script values are immutable boxes, so the common ones are shared rather than re-boxed per
    // operation (a weapon GenerateUI evaluates tens of thousands of comparisons and counters).
    private static readonly object True = true;
    private static readonly object False = false;
    private const long SmallMin = -128, SmallMax = 1023;
    private static readonly object[] SmallLongs = CreateSmallLongs();

    private static object[] CreateSmallLongs()
    {
        var boxes = new object[SmallMax - SmallMin + 1];
        for (var i = 0; i < boxes.Length; i++)
            boxes[i] = SmallMin + i;
        return boxes;
    }

    public static object Box(bool b) => b ? True : False;

    public static object Box(long l) => l is >= SmallMin and <= SmallMax ? SmallLongs[l - SmallMin] : l;
}

/// <summary>
/// A script string built up by repeated <c>s += x</c>. Deffiles build combo option lists that way
/// (xanim's 60-option notetrack list, ~150 times per asset), and copying the whole prefix on every
/// append made each run allocate megabytes of throwaway strings. Appends here are amortised into a
/// shared buffer instead.
/// <para>
/// Each instance is an immutable value: the prefix <c>[0, Length)</c> of a buffer that only ever
/// grows. Extending the newest value appends in place; extending an older one (an alias kept from
/// before a later append) copies, so no value ever observes another's appends.
/// </para>
/// </summary>
internal sealed class GdfConcat
{
    // Below this, a plain string concatenation is cheaper than a builder.
    private const int MinLength = 128;

    /// <summary>The longest string a script may build: past it the run stops, rather than the process running out of memory.</summary>
    public const int MaxChars = 16 * 1024 * 1024;

    /// <summary>Stops the run when joining <paramref name="length"/> characters would pass <see cref="MaxChars"/>.</summary>
    public static void CheckLength(long length)
    {
        if (length > MaxChars)
            throw new InvalidOperationException($"the script built a string over {MaxChars / (1024 * 1024)}M characters");
    }

    private readonly StringBuilder _buffer;
    private string? _flat;

    public int Length { get; }

    private GdfConcat(StringBuilder buffer)
    {
        _buffer = buffer;
        Length = buffer.Length;
    }

    /// <summary><paramref name="head"/> (a script string) followed by <paramref name="tail"/>.</summary>
    public static object Append(object? head, string tail)
    {
        if (head is GdfConcat c)
        {
            CheckLength((long)c.Length + tail.Length);
            if (tail.Length == 0)
                return c;
            if (c.Length == c._buffer.Length)
                return new GdfConcat(c._buffer.Append(tail));
            return new GdfConcat(new StringBuilder(c.Length + tail.Length).Append(c._buffer, 0, c.Length).Append(tail));
        }
        var s = GdfValue.AsString(head);
        CheckLength((long)s.Length + tail.Length);
        if (s.Length + tail.Length < MinLength)
            return s + tail;
        return new GdfConcat(new StringBuilder(s, Math.Max(2 * (s.Length + tail.Length), 256)).Append(tail));
    }

    public override string ToString() => _flat ??= _buffer.ToString(0, Length);
}

/// <summary>Lexical scope chain. Most scopes (blocks, loop bodies) never declare anything, so the
/// variable table is only allocated on the first definition. Scopes never outlive the block or call
/// that opened them (the language has no closures), so the interpreter recycles them.</summary>
internal sealed class Env
{
    private Dictionary<string, object?>? _vars;
    public Env? Parent;
    public Env(Env? parent) => Parent = parent;

    /// <summary>Forgets every variable, ready for reuse as another scope.</summary>
    public void Clear()
    {
        Parent = null;
        if (_vars is { Count: > 0 })
            _vars.Clear();
    }

    public void Define(string name, object? value) => (_vars ??= new(StringComparer.Ordinal))[name] = value;

    public bool TryGet(string name, out object? value)
    {
        for (var e = this; e != null; e = e.Parent)
            if (e._vars is { } vars && vars.TryGetValue(name, out value))
                return true;
        value = null;
        return false;
    }

    public bool Assign(string name, object? value)
    {
        for (var e = this; e != null; e = e.Parent)
        {
            if (e._vars is null)
                continue;
            ref var slot = ref CollectionsMarshal.GetValueRefOrNullRef(e._vars, name);
            if (!Unsafe.IsNullRef(ref slot))
            {
                slot = value;
                return true;
            }
        }
        return false;
    }
}

/// <summary>
/// Tree-walking interpreter that executes GenerateUI against a recording host.
/// Never throws out of <see cref="Run"/>: runtime errors keep whatever entries
/// were recorded so far and surface as a single warning.
/// </summary>
internal sealed class GdfInterpreter
{
    private readonly GdfProgram _prog;
    private readonly IHostCallable _host;
    private readonly Action<string>? _warn;

    // Global host functions (MessageBox, ReadTextFile…) answer only in a deffile button's run; GenerateUI runs leave
    // them unanswered, as they always have.
    private readonly IHostCallable? _functions;
    private bool _quiet; // GenerateUI running ahead of a button's callback: global host calls stay unanswered
    private Func<bool>? _overdue; // a button's run past its time: stop at the next loop step, call or host call
    private int _calls;

    /// <summary>A button's run checks its clock on calls too: recursion without a loop never reaches <see cref="Tick"/>.</summary>
    private void CheckClock(bool always)
    {
        if (_overdue is not null && (always || (++_calls & 63) == 0) && _overdue())
            throw new RuntimeAbort("the script ran past its time limit");
    }

    private Env _global = null!;
    private long _ops;
    private int _depth;
    private const long MaxOps = 2_000_000;
    private const int MaxDepth = 96;

    // Non-linear control flow (return/break/continue) is threaded through statement
    // execution as a Flow result rather than thrown — deffiles execute thousands of
    // returns per load, and exceptions-as-flow are first-chance-exception spam under
    // a debugger and orders of magnitude slower there.
    private enum Flow { Normal, Break, Continue, Return }
    private object? _returnValue;

    private sealed class RuntimeAbort : Exception { public RuntimeAbort(string m) : base(m) { } }

    private GdfInterpreter(GdfProgram prog, IHostCallable host, Action<string>? warn, IHostCallable? functions = null)
    {
        _prog = prog;
        _host = host;
        _warn = warn;
        _functions = functions;
    }

    /// <summary>
    /// Calls one script function (a deffile button's callback) with <paramref name="args"/> after the globals are set
    /// up, against <paramref name="host"/> as its asset, global host calls answered by <paramref name="functions"/>.
    /// Returns null, or why the run stopped.
    /// </summary>
    public static string? Call(GdfProgram prog, string function, IHostCallable host, IHostCallable functions, RecordingAsset? generated,
        Func<bool>? overdue,
        params object?[] args)
    {
        var interp = new GdfInterpreter(prog, host, null, functions) { _overdue = overdue };
        try
        {
            interp._global = new Env(null);
            foreach (var g in prog.Globals)
                interp.ExecVarDecl(g, interp._global);
            if (!prog.Funcs.TryGetValue(function, out var fn))
                return $"no {function} function";
            // APE's script module keeps its globals between calls: GenerateUI has run for the asset before any button
            // is clicked, and callbacks read what it left (scriptbundle's gSceneKeys, gValidObjectCount).
            if (generated is not null && prog.Funcs.TryGetValue("GenerateUI", out var generate))
            {
                interp._quiet = true;
                try
                {
                    interp.CallUser(generate, new List<object?> { generated });
                }
                catch (Exception)
                {
                    // Whatever it set up before it stopped is what the callback gets, as in APE.
                }
                interp._quiet = false;
                interp._ops = 0;
                interp._depth = 0;
            }
            var list = new List<object?> { host };
            list.AddRange(args);
            interp.CallUser(fn, list);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    public static RecordingAsset Run(GdfProgram prog, string entry, Action<string>? warn) =>
        Run(prog, entry, warn, new RecordingAsset());

    /// <summary>Runs against a caller-built host — e.g. one seeded with a real asset's values.</summary>
    public static RecordingAsset Run(GdfProgram prog, string entry, Action<string>? warn, RecordingAsset host)
    {
        var interp = new GdfInterpreter(prog, host, warn);
        try
        {
            interp.Execute(entry);
        }
        catch (Exception ex)
        {
            warn?.Invoke($"runtime error in {entry}: {ex.Message}");
        }
        return host;
    }

    private void Execute(string entry)
    {
        _global = new Env(null);
        foreach (var g in _prog.Globals)
            ExecVarDecl(g, _global);

        if (!_prog.Funcs.TryGetValue(entry, out var fn))
            throw new RuntimeAbort($"no {entry} function");

        var env = new Env(_global);
        if (fn.Params.Count > 0)
            env.Define(fn.Params[0].Name, _host);
        ExecBlock(fn.Body, env);
    }

    // ── Statement execution ──────────────────────────────────────────────────
    private Flow ExecStmt(Stmt s, Env env)
    {
        switch (s)
        {
            case Block b:
            {
                var scope = OpenScope(env);
                var flow = ExecBlock(b, scope);
                CloseScope(scope);
                return flow;
            }
            case VarDecl v: ExecVarDecl(v, env); return Flow.Normal;
            case ExprStmt es: Eval(es.Expr, env); return Flow.Normal;
            case IfStmt f:
                if (GdfValue.AsBool(Eval(f.Cond, env))) return ExecStmt(f.Then, env);
                if (f.Else != null) return ExecStmt(f.Else, env);
                return Flow.Normal;
            case ForStmt fo: return ExecFor(fo, env);
            case SwitchStmt sw: return ExecSwitch(sw, env);
            case ReturnStmt r:
                _returnValue = r.Value != null ? Eval(r.Value, env) : null;
                return Flow.Return;
            case BreakStmt: return Flow.Break;
            case ContinueStmt: return Flow.Continue;
            default: return Flow.Normal;
        }
    }

    // Scopes opened and closed per block, loop iteration and call: a run opens tens of thousands.
    private readonly Stack<Env> _scopes = new();

    private Env OpenScope(Env parent)
    {
        if (_scopes.TryPop(out var scope))
        {
            scope.Parent = parent;
            return scope;
        }
        return new Env(parent);
    }

    private void CloseScope(Env scope)
    {
        scope.Clear();
        _scopes.Push(scope);
    }

    private Flow ExecBlock(Block b, Env env)
    {
        foreach (var s in b.Stmts)
        {
            var flow = ExecStmt(s, env);
            if (flow != Flow.Normal) return flow;
        }
        return Flow.Normal;
    }

    private void ExecVarDecl(VarDecl v, Env env)
    {
        foreach (var (name, init) in v.Vars)
        {
            object? value = init != null ? Eval(init, env) : DefaultFor(v);
            // AngelScript converts on declaration: `string Digit = i;` holds "7", not 7 (material.awi's
            // "cg" + Digit / "colorMap" + Digit keys would otherwise lose their leading zero).
            if (v.TypeName == "string" && !v.IsArray && value is not null and not string)
                value = GdfValue.AsString(value);
            env.Define(name, value);
        }
    }

    private static object? DefaultFor(VarDecl v)
    {
        if (v.IsArray) return new List<object?>();
        return v.TypeName switch
        {
            "int" or "uint" => GdfValue.Box(0L),
            "float" or "double" => 0.0,
            "bool" => GdfValue.Box(false),
            "string" => "",
            _ => null,
        };
    }

    private Flow ExecFor(ForStmt fo, Env env)
    {
        var loopEnv = OpenScope(env);
        if (fo.Init != null) ExecStmt(fo.Init, loopEnv);
        var result = Flow.Normal;
        while (fo.Cond == null || GdfValue.AsBool(Eval(fo.Cond, loopEnv)))
        {
            Tick();
            // A block body opens its own scope; anything else gets a fresh one per iteration.
            Flow flow;
            if (fo.Body is Block)
                flow = ExecStmt(fo.Body, loopEnv);
            else
            {
                var iteration = OpenScope(loopEnv);
                flow = ExecStmt(fo.Body, iteration);
                CloseScope(iteration);
            }
            if (flow == Flow.Break) break;
            if (flow == Flow.Return) { result = Flow.Return; break; }
            if (fo.Update != null) Eval(fo.Update, loopEnv);
        }
        CloseScope(loopEnv);
        return result;
    }

    private Flow ExecSwitch(SwitchStmt sw, Env env)
    {
        object? subject = Eval(sw.Subject, env);
        int start = -1;
        int defaultIdx = -1;
        for (int i = 0; i < sw.Cases.Count && start < 0; i++)
        {
            var c = sw.Cases[i];
            if (c.IsDefault) defaultIdx = i;
            foreach (var ve in c.Values)
                if (AreEqual(subject, Eval(ve, env)))
                {
                    start = i;
                    break;
                }
        }
        if (start < 0) start = defaultIdx;
        if (start < 0) return Flow.Normal;

        for (int i = start; i < sw.Cases.Count; i++)
            foreach (var st in sw.Cases[i].Body)
            {
                var flow = ExecStmt(st, env);
                if (flow == Flow.Break) return Flow.Normal;    // break exits the switch
                if (flow != Flow.Normal) return flow;          // return/continue propagate out
            }
        return Flow.Normal;
    }

    // ── Expression evaluation ────────────────────────────────────────────────
    private object? Eval(Expr e, Env env)
    {
        switch (e)
        {
            case LiteralExpr l: return l.Value;
            case IdentExpr id:
                return env.TryGet(id.Name, out var v) ? v : null;
            case ArrayLitExpr arr:
            {
                var list = new List<object?>(arr.Elements.Count);
                foreach (var el in arr.Elements)
                    list.Add(Eval(el, env));
                return list;
            }
            case UnaryExpr u: return EvalUnary(u, env);
            case BinaryExpr b: return EvalBinary(b, env);
            case TernaryExpr t:
                return GdfValue.AsBool(Eval(t.Cond, env)) ? Eval(t.Then, env) : Eval(t.Else, env);
            case AssignExpr asg: return EvalAssign(asg, env);
            case IndexExpr ix: return EvalIndex(ix, env);
            case MemberExpr m: return EvalMember(m, env);
            case CallExpr c: return EvalCall(c, env);
            default: return null;
        }
    }

    private object? EvalUnary(UnaryExpr u, Env env)
    {
        if (u.Op == "!")
            return GdfValue.Box(!GdfValue.AsBool(Eval(u.Operand, env)));
        if (u.Op == "-")
        {
            var val = Eval(u.Operand, env);
            return val is double d ? -d : -GdfValue.AsLong(val);
        }
        if (u.Op == "+")
            return Eval(u.Operand, env);

        // ++ / -- on an identifier
        if (u.Operand is IdentExpr id)
        {
            long cur = GdfValue.AsLong(Eval(id, env));
            long next = u.Op == "++" ? cur + 1 : cur - 1;
            env.Assign(id.Name, GdfValue.Box(next));
            return GdfValue.Box(u.Prefix ? next : cur);
        }
        return null;
    }

    private object? EvalBinary(BinaryExpr b, Env env)
    {
        if (b.Op == "&&")
            return GdfValue.Box(GdfValue.AsBool(Eval(b.Left, env)) && GdfValue.AsBool(Eval(b.Right, env)));
        if (b.Op == "||")
            return GdfValue.Box(GdfValue.AsBool(Eval(b.Left, env)) || GdfValue.AsBool(Eval(b.Right, env)));

        object? l = Eval(b.Left, env);
        object? r = Eval(b.Right, env);

        switch (b.Op)
        {
            case "+":
                if (l is GdfConcat)
                    return GdfConcat.Append(l, GdfValue.AsString(r));
                if (GdfValue.IsString(l) || GdfValue.IsString(r))
                {
                    string ls = GdfValue.AsString(l), rs = GdfValue.AsString(r);
                    GdfConcat.CheckLength((long)ls.Length + rs.Length);
                    return ls + rs;
                }
                return Arith(l, r, '+');
            case "-": return Arith(l, r, '-');
            case "*": return Arith(l, r, '*');
            case "/": return Arith(l, r, '/');
            case "%": return Arith(l, r, '%');
            case "==": return GdfValue.Box(AreEqual(l, r));
            case "!=": return GdfValue.Box(!AreEqual(l, r));
            case "<": return GdfValue.Box(GdfValue.AsDouble(l) < GdfValue.AsDouble(r));
            case "<=": return GdfValue.Box(GdfValue.AsDouble(l) <= GdfValue.AsDouble(r));
            case ">": return GdfValue.Box(GdfValue.AsDouble(l) > GdfValue.AsDouble(r));
            case ">=": return GdfValue.Box(GdfValue.AsDouble(l) >= GdfValue.AsDouble(r));
            default: return null;
        }
    }

    private static object Arith(object? l, object? r, char op)
    {
        // Treat integers (and bools) as long arithmetic; promote to double otherwise.
        if (l is not double && r is not double)
        {
            long a = GdfValue.AsLong(l), b = GdfValue.AsLong(r);
            return GdfValue.Box(op switch
            {
                '+' => a + b,
                '-' => a - b,
                '*' => a * b,
                '/' => b != 0 ? a / b : 0L,
                '%' => b != 0 ? a % b : 0L,
                _ => 0L,
            });
        }
        double da = GdfValue.AsDouble(l), db = GdfValue.AsDouble(r);
        return op switch
        {
            '+' => da + db,
            '-' => da - db,
            '*' => da * db,
            '/' => db != 0 ? da / db : 0.0,
            '%' => db != 0 ? da % db : 0.0,
            _ => 0.0,
        };
    }

    private static bool AreEqual(object? l, object? r)
    {
        if (GdfValue.IsString(l) || GdfValue.IsString(r))
        {
            // A string being built compares by length first, so `s != ""` in a build loop stays cheap.
            if ((l is GdfConcat || r is GdfConcat) && LengthOf(l) is { } ll && LengthOf(r) is { } rl && ll != rl)
                return false;
            return string.Equals(GdfValue.AsString(l), GdfValue.AsString(r), StringComparison.Ordinal);
        }
        if (l is bool || r is bool)
            return GdfValue.AsBool(l) == GdfValue.AsBool(r);
        return GdfValue.AsDouble(l) == GdfValue.AsDouble(r);
    }

    private static int? LengthOf(object? v) => v switch
    {
        string s => s.Length,
        GdfConcat c => c.Length,
        _ => null,
    };

    private object? EvalAssign(AssignExpr asg, Env env)
    {
        object? rhs = Eval(asg.Value, env);

        if (asg.Op != "=")
        {
            object? cur = Eval(asg.Target, env);
            char op = asg.Op[0];
            if (op == '+' && (GdfValue.IsString(cur) || GdfValue.IsString(rhs)))
                rhs = GdfConcat.Append(cur, GdfValue.AsString(rhs));
            else
                rhs = Arith(cur, rhs, op);
        }

        switch (asg.Target)
        {
            case IdentExpr id:
                if (!env.Assign(id.Name, rhs))
                    env.Define(id.Name, rhs);
                break;
            case IndexExpr ix:
            {
                if (Eval(ix.Target, env) is List<object?> list)
                {
                    int i = (int)GdfValue.AsLong(Eval(ix.Index, env));
                    if (i >= 0 && i < list.Count) list[i] = rhs;
                }
                break;
            }
        }
        return rhs;
    }

    private object? EvalIndex(IndexExpr ix, Env env)
    {
        var target = Eval(ix.Target, env);
        int i = (int)GdfValue.AsLong(Eval(ix.Index, env));
        if (target is List<object?> list && i >= 0 && i < list.Count)
            return list[i];
        return null;
    }

    private object? EvalMember(MemberExpr m, Env env)
    {
        var target = Eval(m.Target, env);
        // Array `.length` accessed as a property (no parens).
        if (target is List<object?> list &&
            string.Equals(m.Name, "length", StringComparison.OrdinalIgnoreCase))
            return GdfValue.Box(list.Count);
        return null;
    }

    private object? EvalCall(CallExpr c, Env env)
    {
        if (c.Callee is MemberExpr m)
        {
            var target = Eval(m.Target, env);
            var args = EvalArgs(c.Args, env);
            var result = CallMethod(target, m.Name, args);
            Recycle(args);
            return result;
        }

        if (c.Callee is IdentExpr id)
        {
            // Cast / conversion builtins.
            switch (id.Name)
            {
                case "int": return GdfValue.Box(c.Args.Count > 0 ? GdfValue.AsLong(Eval(c.Args[0], env)) : 0L);
                case "float": return c.Args.Count > 0 ? GdfValue.AsDouble(Eval(c.Args[0], env)) : 0.0;
                case "uint": return GdfValue.Box(c.Args.Count > 0 ? GdfValue.AsLong(Eval(c.Args[0], env)) : 0L);
                case "string": return c.Args.Count > 0 ? GdfValue.AsString(Eval(c.Args[0], env)) : "";
            }

            if (_prog.Funcs.TryGetValue(id.Name, out var fn))
                return IsPure(fn) ? CallPure(fn, EvalArgs(c.Args, env)) : CallUser(fn, EvalArgs(c.Args, env));

            if (_functions is not null && !_quiet)
            {
                CheckClock(always: true);
                var args = EvalArgs(c.Args, env);
                Finish(args);
                var result = _functions.Invoke(id.Name, args);
                Recycle(args);
                return result;
            }

            // Unknown host/global function — record-and-ignore.
            return null;
        }

        // Fallback: evaluate callee, ignore.
        Eval(c.Callee, env);
        return null;
    }

    // Argument lists are only read during the call they were built for (hosts and user functions copy
    // the values out), so they are recycled rather than allocated per call.
    private readonly Stack<List<object?>> _argLists = new();

    private List<object?> EvalArgs(List<Expr> args, Env env)
    {
        var list = _argLists.Count > 0 ? _argLists.Pop() : new List<object?>(args.Count);
        foreach (var a in args)
            list.Add(Eval(a, env));
        return list;
    }

    private void Recycle(List<object?> args)
    {
        args.Clear();
        _argLists.Push(args);
    }

    // ── Pure calls ───────────────────────────────────────────────────────────
    // A pure function (GdfPurity) called again with equal arguments returns the earlier result. A hit
    // still counts the ops and call depth the call took, so the run's limits trip exactly as before.
    private readonly Dictionary<FuncDecl, bool> _pure = new();
    private Dictionary<FuncDecl, Dictionary<MemoKey, Memo>>? _memo;
    private int _peakDepth;

    private sealed record Memo(object? Result, long Ops, int Depth);

    private bool IsPure(FuncDecl fn)
    {
        if (!_pure.TryGetValue(fn, out var pure))
            _pure[fn] = pure = GdfPurity.IsPure(_prog, fn);
        return pure;
    }

    private object? CallPure(FuncDecl fn, List<object?> args)
    {
        if (MemoKey.TryCreate(args) is not { } key)
            return CallUser(fn, args);
        _memo ??= new();
        if (!_memo.TryGetValue(fn, out var results))
            _memo[fn] = results = new Dictionary<MemoKey, Memo>();
        if (results.TryGetValue(key, out var hit))
        {
            Recycle(args);
            _ops += hit.Ops;
            if (_ops > MaxOps)
                throw new RuntimeAbort("iteration cap exceeded");
            if (_depth + hit.Depth > MaxDepth)
                throw new RuntimeAbort("max call depth exceeded");
            _peakDepth = Math.Max(_peakDepth, _depth + hit.Depth);
            return hit.Result;
        }

        var ops = _ops;
        var depth = _depth;
        var peak = _peakDepth;
        _peakDepth = depth;
        var result = CallUser(fn, args);
        var used = _peakDepth - depth;
        _peakDepth = Math.Max(peak, _peakDepth);
        if (result is GdfConcat built)
            result = built.ToString();
        // Only immutable results are shared; an array result belongs to its caller.
        if (result is null or string or long or double or bool)
            results[key] = new Memo(result, _ops - ops, used);
        return result;
    }

    /// <summary>A call's arguments by value: arrays are snapshotted, so later edits to them can't alias.</summary>
    private sealed class MemoKey : IEquatable<MemoKey>
    {
        private readonly object?[] _values;
        private readonly int _hash;

        private MemoKey(object?[] values)
        {
            _values = values;
            _hash = Hash(values);
        }

        public static MemoKey? TryCreate(List<object?> args) =>
            TrySnapshot(args, out var values) ? new MemoKey(values) : null;

        // Scalars and strings as themselves; arrays as nested object?[]. Anything else (a host
        // object) makes the call unmemoisable.
        private static bool TrySnapshot(List<object?> list, out object?[] values)
        {
            values = new object?[list.Count];
            for (var i = 0; i < list.Count; i++)
            {
                switch (list[i])
                {
                    case null or string or long or double or bool:
                        values[i] = list[i];
                        break;
                    case GdfConcat c:
                        values[i] = c.ToString();
                        break;
                    case List<object?> inner:
                        if (!TrySnapshot(inner, out var nested))
                            return false;
                        values[i] = nested;
                        break;
                    default:
                        return false;
                }
            }
            return true;
        }

        private static int Hash(object?[] values)
        {
            var h = new HashCode();
            h.Add(values.Length);
            foreach (var v in values)
                h.Add(v is object?[] nested ? Hash(nested) : v?.GetHashCode() ?? 0);
            return h.ToHashCode();
        }

        private static bool Same(object?[] a, object?[] b)
        {
            if (a.Length != b.Length)
                return false;
            for (var i = 0; i < a.Length; i++)
            {
                var same = (a[i], b[i]) switch
                {
                    (object?[] x, object?[] y) => Same(x, y),
                    (string x, string y) => string.Equals(x, y, StringComparison.Ordinal),
                    // Bit for bit: -0 and 0 are different arguments (1/x tells them apart), and NaN is itself.
                    (double x, double y) => BitConverter.DoubleToInt64Bits(x) == BitConverter.DoubleToInt64Bits(y),
                    var (x, y) => Equals(x, y), // boxed scalars: equal only with the same type
                };
                if (!same)
                    return false;
            }
            return true;
        }

        public bool Equals(MemoKey? other) => other is not null && other._hash == _hash && Same(_values, other._values);
        public override bool Equals(object? obj) => Equals(obj as MemoKey);
        public override int GetHashCode() => _hash;
    }

    private object? CallUser(FuncDecl fn, List<object?> args)
    {
        CheckClock(always: false);
        if (++_depth > MaxDepth)
        {
            _depth--;
            throw new RuntimeAbort("max call depth exceeded");
        }
        if (_depth > _peakDepth)
            _peakDepth = _depth;
        try
        {
            var env = OpenScope(_global);
            for (int i = 0; i < fn.Params.Count; i++)
            {
                object? v = i < args.Count ? args[i]
                    : fn.Params[i].Default != null ? Eval(fn.Params[i].Default!, env)
                    : DefaultForParam(fn.Params[i]);
                env.Define(fn.Params[i].Name, v);
            }
            Recycle(args);
            var flow = ExecBlock(fn.Body, env);
            CloseScope(env);
            if (flow == Flow.Return)
            {
                var result = _returnValue;
                _returnValue = null;
                return result;
            }
            return null;
        }
        finally
        {
            _depth--;
        }
    }

    private static object? DefaultForParam(Param p)
    {
        if (p.IsArray) return new List<object?>();
        return p.TypeName switch
        {
            "int" or "uint" => GdfValue.Box(0L),
            "float" or "double" => 0.0,
            "bool" => GdfValue.Box(false),
            "string" => "",
            _ => null,
        };
    }

    /// <summary>Turns every built-up string in <paramref name="values"/>, nested arrays included, into its finished string.</summary>
    private static void Finish(List<object?> values)
    {
        for (var i = 0; i < values.Count; i++)
            if (values[i] is GdfConcat c)
                values[i] = c.ToString();
            else if (values[i] is List<object?> nested)
                Finish(nested);
    }

    private object? CallMethod(object? target, string method, List<object?> args)
    {
        switch (target)
        {
            case IHostCallable host:
                CheckClock(always: true);
                // Hosts record what they are given; they only ever see finished strings, arrays' elements included.
                Finish(args);
                return host.Invoke(method, args);
            case string s:
                return StringMethod(s, method, args);
            case GdfConcat c:
                return StringMethod(c.ToString(), method, args);
            case List<object?> list:
                return ArrayMethod(list, method, args);
            default:
                return null;
        }
    }

    private static object? StringMethod(string s, string method, List<object?> args)
    {
        switch (method)
        {
            case "ToInt": return GdfValue.Box(GdfValue.AsLong(s));
            case "Length": return GdfValue.Box(s.Length);
            case "MakeUpper": return s.ToUpperInvariant();
            case "MakeLower": return s.ToLowerInvariant();
            case "Left": return args.Count > 0 ? s.Substring(0, Math.Min(s.Length, (int)GdfValue.AsLong(args[0]))) : s;
            case "Right":
            {
                int n = args.Count > 0 ? (int)GdfValue.AsLong(args[0]) : 0;
                n = Math.Min(n, s.Length);
                return s.Substring(s.Length - n);
            }
            case "Find":
            {
                string needle = args.Count > 0 ? GdfValue.AsString(args[0]) : "";
                int start = args.Count > 1 ? (int)GdfValue.AsLong(args[1]) : 0;
                start = Math.Clamp(start, 0, s.Length);
                return GdfValue.Box(s.IndexOf(needle, start, StringComparison.Ordinal));
            }
            case "Split":
            {
                string sep = args.Count > 0 ? GdfValue.AsString(args[0]) : ",";
                var parts = sep.Length == 0
                    ? new[] { s }
                    : s.Split(new[] { sep }, StringSplitOptions.None);
                var list = new List<object?>(parts.Length);
                foreach (var p in parts) list.Add(p);
                return list;
            }
            case "IsSpecified": return GdfValue.Box(s.Length > 0);
            default: return "";
        }
    }

    private static object? ArrayMethod(List<object?> list, string method, List<object?> args)
    {
        switch (method)
        {
            case "length": return GdfValue.Box(list.Count);
            case "insertLast": list.Add(args.Count > 0 ? args[0] : null); return null;
            case "insertAt":
                if (args.Count >= 2)
                {
                    int i = Math.Clamp((int)GdfValue.AsLong(args[0]), 0, list.Count);
                    list.Insert(i, args[1]);
                }
                return null;
            case "removeAt":
                if (args.Count >= 1)
                {
                    int i = (int)GdfValue.AsLong(args[0]);
                    if (i >= 0 && i < list.Count) list.RemoveAt(i);
                }
                return null;
            case "removeLast":
                if (list.Count > 0) list.RemoveAt(list.Count - 1);
                return null;
            case "resize":
            {
                int n = args.Count > 0 ? Math.Max(0, (int)GdfValue.AsLong(args[0])) : list.Count;
                while (list.Count < n) list.Add(null);
                while (list.Count > n) list.RemoveAt(list.Count - 1);
                return null;
            }
            case "find":
            {
                object? needle = args.Count > 0 ? args[0] : null;
                for (int i = 0; i < list.Count; i++)
                    if (AreEqual(list[i], needle)) return GdfValue.Box(i);
                return GdfValue.Box(-1L);
            }
            default: return null;
        }
    }

    private void Tick()
    {
        if (++_ops > MaxOps)
            throw new RuntimeAbort("iteration cap exceeded");
        if ((_ops & 255) == 0 && _overdue?.Invoke() == true)
            throw new RuntimeAbort("the script ran past its time limit");
    }
}
