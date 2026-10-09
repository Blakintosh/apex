using Apex.Render.Device;
using Vortice.Direct3D11;

namespace Apex.Render.Presentation;

/// <summary>
/// GPU time of a span of work via D3D11 timestamp queries (disjoint + begin/end), read back without stalling: a
/// small ring of query sets, polled with <c>DONOTFLUSH</c>; <see cref="LastMilliseconds"/> updates when a result
/// arrives. UI thread (immediate context) only.
/// </summary>
public sealed class GpuFrameTimer : IDisposable
{
    private sealed class QuerySet
    {
        public required ID3D11Query Disjoint, Begin, End;
        public bool Pending;
    }

    private readonly GfxDevice _gfx;
    private readonly QuerySet[] _sets;
    private int _next;
    private QuerySet? _open;

    public GpuFrameTimer(GfxDevice gfx, int depth = 4)
    {
        _gfx = gfx;
        _sets = new QuerySet[depth];
        for (int i = 0; i < depth; i++)
        {
            _sets[i] = new QuerySet
            {
                Disjoint = gfx.Device.CreateQuery(new QueryDescription(QueryType.TimestampDisjoint)),
                Begin = gfx.Device.CreateQuery(new QueryDescription(QueryType.Timestamp)),
                End = gfx.Device.CreateQuery(new QueryDescription(QueryType.Timestamp)),
            };
        }
    }

    /// <summary>GPU milliseconds of the most recent span whose result has arrived (NaN until then).</summary>
    public double LastMilliseconds { get; private set; } = double.NaN;

    /// <summary>True while some span's result is still outstanding (poll again later).</summary>
    public bool HasPending => Array.Exists(_sets, s => s.Pending);

    public void BeginFrame()
    {
        Poll();
        var set = _sets[_next];
        if (set.Pending)
        {
            _open = null; // ring full: skip timing this frame rather than stall
            return;
        }
        _next = (_next + 1) % _sets.Length;
        var ctx = _gfx.Context;
        ctx.Begin(set.Disjoint);
        ctx.End(set.Begin);
        _open = set;
    }

    public void EndFrame()
    {
        if (_open is not { } set)
            return;
        var ctx = _gfx.Context;
        ctx.End(set.End);
        ctx.End(set.Disjoint);
        set.Pending = true;
        _open = null;
    }

    /// <summary>Collects every finished result; returns true when <see cref="LastMilliseconds"/> changed.</summary>
    public unsafe bool Poll()
    {
        bool changed = false;
        var ctx = _gfx.Context;
        // Oldest first so the last one read is the newest.
        for (int k = 0; k < _sets.Length; k++)
        {
            var set = _sets[(_next + k) % _sets.Length];
            if (!set.Pending)
                continue;
            QueryDataTimestampDisjoint disjoint;
            ulong t0, t1;
            if (ctx.GetData(set.Disjoint, (IntPtr)(&disjoint), (uint)sizeof(QueryDataTimestampDisjoint), AsyncGetDataFlags.DoNotFlush).Code != 0)
                continue;
            if (ctx.GetData(set.Begin, (IntPtr)(&t0), sizeof(ulong), AsyncGetDataFlags.DoNotFlush).Code != 0
                || ctx.GetData(set.End, (IntPtr)(&t1), sizeof(ulong), AsyncGetDataFlags.DoNotFlush).Code != 0)
                continue;
            set.Pending = false;
            if (!disjoint.Disjoint && disjoint.Frequency > 0 && t1 >= t0)
            {
                LastMilliseconds = (t1 - t0) * 1000.0 / disjoint.Frequency;
                changed = true;
            }
        }
        return changed;
    }

    public void Dispose()
    {
        foreach (var s in _sets)
        {
            s.Disjoint.Dispose();
            s.Begin.Dispose();
            s.End.Dispose();
        }
    }
}
