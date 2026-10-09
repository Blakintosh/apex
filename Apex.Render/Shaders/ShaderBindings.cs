using Apex.Render.Resources;
using Vortice.Direct3D11;

namespace Apex.Render.Shaders;

/// <summary>
/// Binds constant buffers, SRVs, UAVs and samplers to one pipeline (VS+PS[+GS/HS/DS] or a CS) purely by
/// reflected name, the way ToolsGfx does it: each name is looked up in every stage's reflection and bound
/// to whatever register that variant's compiler assigned. Setting a name no stage declares is an error
/// (<c>Set…</c>) or a no-op (<c>TrySet…</c>), so engine code can offer every code resource and let
/// the variant pick what it reads.
/// <para>Names are resolved to registers once, at construction: <see cref="Apply(ID3D11DeviceContext)"/> writes every
/// declared register (null where nothing is assigned, so a missing image reads zeros whatever was bound before) with
/// one call per contiguous register run of a stage and table.</para>
/// </summary>
public sealed class ShaderBindings
{
    private readonly record struct Slot(ShaderStage Stage, BindingTable Table, int Register, int Count, int Stride);

    private const int StageCount = (int)ShaderStage.Compute + 1;

    /// <summary>One reflected name: where each stage binds it and what has been assigned to it.</summary>
    private sealed class Entry(BindingTable table)
    {
        public readonly BindingTable Table = table;
        public readonly List<Slot> Slots = new();
        public int Count;
        public object?[]? Values;
        public uint UavCount = uint.MaxValue;
        /// <summary>Per-stage constant buffers (<see cref="TrySetConstantBuffer(ShaderStage, string, ID3D11Buffer?)"/>),
        /// indexed by stage; <see cref="StageMask"/> says which are set.</summary>
        public ID3D11Buffer?[]? StageBuffers;
        public int StageMask;
    }

    /// <summary>Registers <see cref="Start"/>… of one stage and table; cell i lists the (name, element) pairs bound to
    /// register Start + i (one, unless two names alias a register).</summary>
    private sealed class Run(ShaderStage stage, BindingTable table, int start, (Entry Entry, int Element)[][] cells)
    {
        public readonly ShaderStage Stage = stage;
        public readonly BindingTable Table = table;
        public readonly int Start = start;
        public readonly (Entry Entry, int Element)[][] Cells = cells;
        public readonly ID3D11Buffer?[]? Buffers = table == BindingTable.ConstantBuffer ? new ID3D11Buffer?[cells.Length] : null;
        public readonly ID3D11ShaderResourceView?[]? Views = table == BindingTable.ShaderResource ? new ID3D11ShaderResourceView?[cells.Length] : null;
        public readonly ID3D11SamplerState?[]? Samplers = table == BindingTable.Sampler ? new ID3D11SamplerState?[cells.Length] : null;
        public readonly ID3D11UnorderedAccessView?[]? Uavs = table == BindingTable.UnorderedAccess ? new ID3D11UnorderedAccessView?[cells.Length] : null;
        public readonly uint[]? Counts = table == BindingTable.UnorderedAccess ? new uint[cells.Length] : null;
    }

    // Null arrays for unbinding (D3D11 has at most 128 input slots per stage).
    private static readonly ID3D11Buffer?[] s_nullBuffers = new ID3D11Buffer?[128];
    private static readonly ID3D11ShaderResourceView?[] s_nullViews = new ID3D11ShaderResourceView?[128];
    private static readonly ID3D11SamplerState?[] s_nullSamplers = new ID3D11SamplerState?[128];
    private static readonly ID3D11UnorderedAccessView?[] s_nullUavs = new ID3D11UnorderedAccessView?[128];
    private static readonly uint[] s_noCounts = Enumerable.Repeat(uint.MaxValue, 128).ToArray();

    private readonly ShaderProgram[] _programs;
    private readonly ShaderProgram?[] _byStage = new ShaderProgram?[StageCount];
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Run[] _runs;
    /// <summary>PS UAVs share the output-merger slots and are set as one block (first … last declared register).</summary>
    private readonly Run? _pixelUavs;
    private int _version, _appliedVersion = -1;

    public IReadOnlyList<ShaderProgram> Programs => _programs;

    public ShaderBindings(params ShaderProgram[] programs)
    {
        if (programs.Length == 0)
            throw new ArgumentException("at least one program is required");
        var stages = new HashSet<ShaderStage>();
        foreach (var p in programs)
        {
            if (!stages.Add(p.Stage))
                throw new ArgumentException($"two {p.Stage} shaders in one pipeline");
        }
        if (stages.Contains(ShaderStage.Compute) && stages.Count > 1)
            throw new ArgumentException("a compute shader cannot share a pipeline with graphics stages");

        _programs = programs;
        foreach (var p in programs)
        {
            _byStage[(int)p.Stage] = p;
            foreach (var r in p.Reflection.Resources)
            {
                if (!_entries.TryGetValue(r.Name, out var entry))
                    _entries[r.Name] = entry = new Entry(r.Table);
                foreach (var existing in entry.Slots)
                {
                    if (existing.Table != r.Table)
                        throw new InvalidDataException($"'{r.Name}' is a {existing.Table} in {existing.Stage} but a {r.Table} in {p.Stage}");
                }
                entry.Slots.Add(new Slot(p.Stage, r.Table, r.BindPoint, r.BindCount, r.NumSamplesOrStride));
                entry.Count = Math.Max(entry.Count, r.BindCount);
            }
        }
        (_runs, _pixelUavs) = BuildRuns();
    }

    private (Run[] Runs, Run? PixelUavs) BuildRuns()
    {
        var registers = new SortedDictionary<(ShaderStage Stage, BindingTable Table, int Register), List<(Entry, int)>>();
        foreach (var entry in _entries.Values)
            foreach (var slot in entry.Slots)
                for (int i = 0; i < slot.Count; i++)
                {
                    var key = (slot.Stage, slot.Table, slot.Register + i);
                    if (!registers.TryGetValue(key, out var cell))
                        registers[key] = cell = new List<(Entry, int)>();
                    cell.Add((entry, i));
                }

        var runs = new List<Run>();
        Run? pixelUavs = null;
        var pending = new List<(Entry, int)[]>();
        (ShaderStage Stage, BindingTable Table, int Start)? open = null;
        void Close()
        {
            if (open is not { } o)
                return;
            runs.Add(new Run(o.Stage, o.Table, o.Start, pending.ToArray()));
            pending.Clear();
            open = null;
        }
        foreach (var ((stage, table, register), cell) in registers)
        {
            if (stage == ShaderStage.Pixel && table == BindingTable.UnorderedAccess)
                continue;
            if (open is not { } o || o.Stage != stage || o.Table != table || o.Start + pending.Count != register)
            {
                Close();
                open = (stage, table, register);
            }
            pending.Add(cell.ToArray());
        }
        Close();

        var ps = registers.Where(r => r.Key.Stage == ShaderStage.Pixel && r.Key.Table == BindingTable.UnorderedAccess).ToList();
        if (ps.Count > 0)
        {
            int first = ps[0].Key.Register, last = ps[^1].Key.Register;
            var cells = new (Entry, int)[last - first + 1][];
            for (int i = 0; i < cells.Length; i++)
                cells[i] = [];
            foreach (var (key, cell) in ps)
                cells[key.Register - first] = cell.ToArray();
            pixelUavs = new Run(ShaderStage.Pixel, BindingTable.UnorderedAccess, first, cells);
        }
        return (runs.ToArray(), pixelUavs);
    }

    public ShaderProgram? Get(ShaderStage stage) => _byStage[(int)stage];

    /// <summary>True if any stage reflects a resource of this name.</summary>
    public bool Uses(string name) => _entries.ContainsKey(name);

    public IEnumerable<string> Names => _entries.Keys;

    /// <summary>Reflected layout of a constant buffer (checked identical across stages that share it).</summary>
    public CBufferLayout GetConstantBufferLayout(string name)
    {
        CBufferLayout? found = null;
        foreach (var p in _programs)
        {
            var cb = p.Reflection.FindConstantBuffer(name);
            if (cb == null || !cb.IsConstantBuffer)
                continue;
            if (found != null && found.Size != cb.Size)
                throw new InvalidDataException($"cbuffer '{name}' differs between stages ({found.Size} vs {cb.Size} bytes)");
            found ??= cb;
        }
        return found ?? throw new KeyNotFoundException($"no stage declares cbuffer '{name}'");
    }

    /// <summary>A zero-filled writer over the reflected layout of cbuffer <paramref name="name"/>.</summary>
    public ConstantBufferWriter CreateWriter(string name) => new(GetConstantBufferLayout(name));

    // ── Setters ─────────────────────────────────────────────────────────────

    public bool TrySetConstantBuffer(string name, ID3D11Buffer? buffer) => TrySet(name, BindingTable.ConstantBuffer, buffer, 0);
    public bool TrySetConstantBuffer(string name, GpuBuffer? buffer) => TrySetConstantBuffer(name, buffer?.Buffer);
    public bool TrySetResource(string name, ID3D11ShaderResourceView? srv, int arrayIndex = 0) => TrySet(name, BindingTable.ShaderResource, srv, arrayIndex);
    public bool TrySetSampler(string name, ID3D11SamplerState? sampler, int arrayIndex = 0) => TrySet(name, BindingTable.Sampler, sampler, arrayIndex);

    public bool TrySetUnorderedAccess(string name, ID3D11UnorderedAccessView? uav, uint initialCount = uint.MaxValue)
    {
        if (!TrySet(name, BindingTable.UnorderedAccess, uav, 0))
            return false;
        var e = _entries[name];
        if (e.UavCount != initialCount)
        {
            e.UavCount = initialCount;
            _version++;
        }
        return true;
    }

    /// <summary>Binds a constant buffer for ONE stage only — material <c>$Globals</c> differ per stage (VS vs PS)
    /// although they share the name. Takes precedence over a name-wide <see cref="SetConstantBuffer(string, ID3D11Buffer?)"/>.</summary>
    public bool TrySetConstantBuffer(ShaderStage stage, string name, ID3D11Buffer? buffer)
    {
        if (!_entries.TryGetValue(name, out var e) || !e.Slots.Exists(s => s.Stage == stage && s.Table == BindingTable.ConstantBuffer))
            return false;
        e.StageBuffers ??= new ID3D11Buffer?[StageCount];
        e.StageBuffers[(int)stage] = buffer;
        e.StageMask |= 1 << (int)stage;
        _version++;
        return true;
    }

    public void SetConstantBuffer(string name, ID3D11Buffer? buffer) => Require(TrySetConstantBuffer(name, buffer), name, BindingTable.ConstantBuffer);
    public void SetConstantBuffer(string name, GpuBuffer? buffer) => SetConstantBuffer(name, buffer?.Buffer);
    public void SetResource(string name, ID3D11ShaderResourceView? srv, int arrayIndex = 0) => Require(TrySetResource(name, srv, arrayIndex), name, BindingTable.ShaderResource);
    public void SetSampler(string name, ID3D11SamplerState? sampler, int arrayIndex = 0) => Require(TrySetSampler(name, sampler, arrayIndex), name, BindingTable.Sampler);
    public void SetUnorderedAccess(string name, ID3D11UnorderedAccessView? uav, uint initialCount = uint.MaxValue)
        => Require(TrySetUnorderedAccess(name, uav, initialCount), name, BindingTable.UnorderedAccess);

    private bool TrySet(string name, BindingTable table, object? value, int arrayIndex)
    {
        if (!_entries.TryGetValue(name, out var e))
            return false;
        if (e.Table != table)
            throw new InvalidOperationException($"'{name}' is a {e.Table}, not a {table}");
        if (arrayIndex < 0 || arrayIndex >= e.Count)
            throw new ArgumentOutOfRangeException(nameof(arrayIndex), $"'{name}' has {e.Count} element(s)");
        var values = e.Values ??= new object?[e.Count];
        if (!ReferenceEquals(values[arrayIndex], value))
        {
            values[arrayIndex] = value;
            _version++;
        }
        return true;
    }

    private static void Require(bool ok, string name, BindingTable table)
    {
        if (!ok)
            throw new KeyNotFoundException($"no stage of this pipeline declares {table} '{name}'");
    }

    /// <summary>Reflected names nothing has been assigned to (a missing binding reads zeros / black).</summary>
    public IReadOnlyList<string> Unassigned()
    {
        var list = new List<string>();
        foreach (var (name, e) in _entries)
        {
            if ((e.Values is null || Array.TrueForAll(e.Values, v => v == null))
                && !e.Slots.TrueForAll(s => (e.StageMask & (1 << (int)s.Stage)) != 0 && e.StageBuffers![(int)s.Stage] != null))
                list.Add(name);
        }
        return list;
    }

    public void ClearValues()
    {
        foreach (var e in _entries.Values)
        {
            e.Values = null;
            e.UavCount = uint.MaxValue;
            e.StageBuffers = null;
            e.StageMask = 0;
        }
        _version++;
    }

    // ── Apply ───────────────────────────────────────────────────────────────

    /// <summary>Sets the pipeline's shaders (other graphics stages are cleared) and binds every declared register.</summary>
    public void Apply(ID3D11DeviceContext ctx) => Apply(ctx, null);

    /// <summary>
    /// <see cref="Apply(ID3D11DeviceContext)"/> right after <paramref name="previous"/> was applied with nothing else
    /// touching the shader stages since: shaders it already set are not set again, and re-applying the same unchanged
    /// pipeline does nothing.
    /// </summary>
    internal void Apply(ID3D11DeviceContext ctx, ShaderBindings? previous)
    {
        if (ReferenceEquals(previous, this) && _appliedVersion == _version)
            return;
        var prev = previous?._byStage;
        if (Get(ShaderStage.Compute) is { } cs)
        {
            if (prev?[(int)ShaderStage.Compute] != cs)
                ctx.CSSetShader(cs.ComputeShader);
        }
        else
        {
            // A compute pipeline before this one says nothing about the graphics stages.
            if (prev?[(int)ShaderStage.Compute] != null)
                prev = null;
            bool Changed(ShaderStage s) => prev is null || prev[(int)s] != _byStage[(int)s];
            if (Changed(ShaderStage.Vertex)) ctx.VSSetShader(Get(ShaderStage.Vertex)?.VertexShader);
            if (Changed(ShaderStage.Hull)) ctx.HSSetShader(Get(ShaderStage.Hull)?.HullShader);
            if (Changed(ShaderStage.Domain)) ctx.DSSetShader(Get(ShaderStage.Domain)?.DomainShader);
            if (Changed(ShaderStage.Geometry)) ctx.GSSetShader(Get(ShaderStage.Geometry)?.GeometryShader);
            if (Changed(ShaderStage.Pixel)) ctx.PSSetShader(Get(ShaderStage.Pixel)?.PixelShader);
        }
        foreach (var run in _runs)
            BindRun(ctx, run);
        if (_pixelUavs is { } ps)
        {
            for (int i = 0; i < ps.Cells.Length; i++)
                (ps.Uavs![i], ps.Counts![i]) = ResolveUav(ps.Cells[i]);
            SetPixelUavs(ctx, (uint)ps.Start, ps.Uavs!, ps.Counts!);
        }
        _appliedVersion = _version;
    }

    /// <summary>Unbinds every register this pipeline declares (avoids SRV/RTV/UAV hazards for the next pass).</summary>
    public void Unbind(ID3D11DeviceContext ctx)
    {
        foreach (var run in _runs)
        {
            uint start = (uint)run.Start, n = (uint)run.Cells.Length;
            switch (run.Table)
            {
                case BindingTable.ConstantBuffer: SetCbs(ctx, run.Stage, start, n, s_nullBuffers); break;
                case BindingTable.ShaderResource: SetSrvs(ctx, run.Stage, start, n, s_nullViews); break;
                case BindingTable.Sampler: SetSamplers(ctx, run.Stage, start, n, s_nullSamplers); break;
                case BindingTable.UnorderedAccess: SetUavs(ctx, run.Stage, start, n, s_nullUavs, s_noCounts); break;
            }
        }
        if (_pixelUavs is { } ps)
            SetPixelUavs(ctx, (uint)ps.Start, s_nullUavs.AsSpan(0, ps.Cells.Length), s_noCounts);
        _appliedVersion = -1;
    }

    private void BindRun(ID3D11DeviceContext ctx, Run run)
    {
        uint start = (uint)run.Start, n = (uint)run.Cells.Length;
        switch (run.Table)
        {
            case BindingTable.ConstantBuffer:
                for (int i = 0; i < run.Cells.Length; i++)
                    run.Buffers![i] = (ID3D11Buffer?)Resolve(run.Cells[i], run.Stage);
                SetCbs(ctx, run.Stage, start, n, run.Buffers!);
                break;
            case BindingTable.ShaderResource:
                for (int i = 0; i < run.Cells.Length; i++)
                    run.Views![i] = (ID3D11ShaderResourceView?)Resolve(run.Cells[i], run.Stage);
                SetSrvs(ctx, run.Stage, start, n, run.Views!);
                break;
            case BindingTable.Sampler:
                for (int i = 0; i < run.Cells.Length; i++)
                    run.Samplers![i] = (ID3D11SamplerState?)Resolve(run.Cells[i], run.Stage);
                SetSamplers(ctx, run.Stage, start, n, run.Samplers!);
                break;
            case BindingTable.UnorderedAccess:
                for (int i = 0; i < run.Cells.Length; i++)
                    (run.Uavs![i], run.Counts![i]) = ResolveUav(run.Cells[i]);
                SetUavs(ctx, run.Stage, start, n, run.Uavs!, run.Counts!);
                break;
        }
    }

    /// <summary>The value bound to a register: a stage's own constant buffer first, else the name's value; when names
    /// alias the register, the last one with a value wins.</summary>
    private static object? Resolve((Entry Entry, int Element)[] cell, ShaderStage stage)
    {
        object? value = null;
        foreach (var (e, element) in cell)
        {
            object? v = (e.StageMask & (1 << (int)stage)) != 0 ? e.StageBuffers![(int)stage] : e.Values?[element];
            if (v != null)
                value = v;
        }
        return value;
    }

    private static (ID3D11UnorderedAccessView?, uint) ResolveUav((Entry Entry, int Element)[] cell)
    {
        (ID3D11UnorderedAccessView?, uint) value = (null, uint.MaxValue);
        foreach (var (e, element) in cell)
        {
            if (e.Values?[element] is ID3D11UnorderedAccessView uav)
                value = (uav, e.UavCount);
        }
        return value;
    }

    private static unsafe void SetPixelUavs(ID3D11DeviceContext ctx, uint first, ReadOnlySpan<ID3D11UnorderedAccessView?> list, ReadOnlySpan<uint> initialCounts)
    {
        int n = list.Length;
        var uavs = stackalloc IntPtr[n];
        var counts = stackalloc uint[n];
        for (int i = 0; i < n; i++)
        {
            uavs[i] = list[i]?.NativePointer ?? IntPtr.Zero;
            counts[i] = initialCounts[i];
        }
        // PS UAVs share the output-merger slots with RTVs; keep the bound targets (D3D11_KEEP_RENDER_TARGETS_AND_DEPTH_STENCIL).
        const uint KeepRenderTargetsAndDepthStencil = 0xFFFFFFFF;
        // Called through the vtable (slot 34): Vortice's array overload dereferences the RTV array even when told to keep it.
        var self = ctx.NativePointer;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, IntPtr, uint, uint, IntPtr*, uint*, void>)(*(IntPtr**)self)[34];
        fn(self, KeepRenderTargetsAndDepthStencil, IntPtr.Zero, IntPtr.Zero, first, (uint)n, uavs, counts);
    }

    private static void SetCbs(ID3D11DeviceContext ctx, ShaderStage stage, uint start, uint n, ID3D11Buffer?[] b)
    {
        switch (stage)
        {
            case ShaderStage.Vertex: ctx.VSSetConstantBuffers(start, n, b!); break;
            case ShaderStage.Hull: ctx.HSSetConstantBuffers(start, n, b!); break;
            case ShaderStage.Domain: ctx.DSSetConstantBuffers(start, n, b!); break;
            case ShaderStage.Geometry: ctx.GSSetConstantBuffers(start, n, b!); break;
            case ShaderStage.Pixel: ctx.PSSetConstantBuffers(start, n, b!); break;
            case ShaderStage.Compute: ctx.CSSetConstantBuffers(start, n, b!); break;
        }
    }

    private static void SetSrvs(ID3D11DeviceContext ctx, ShaderStage stage, uint start, uint n, ID3D11ShaderResourceView?[] v)
    {
        switch (stage)
        {
            case ShaderStage.Vertex: ctx.VSSetShaderResources(start, n, v!); break;
            case ShaderStage.Hull: ctx.HSSetShaderResources(start, n, v!); break;
            case ShaderStage.Domain: ctx.DSSetShaderResources(start, n, v!); break;
            case ShaderStage.Geometry: ctx.GSSetShaderResources(start, n, v!); break;
            case ShaderStage.Pixel: ctx.PSSetShaderResources(start, n, v!); break;
            case ShaderStage.Compute: ctx.CSSetShaderResources(start, n, v!); break;
        }
    }

    private static void SetSamplers(ID3D11DeviceContext ctx, ShaderStage stage, uint start, uint n, ID3D11SamplerState?[] s)
    {
        switch (stage)
        {
            case ShaderStage.Vertex: ctx.VSSetSamplers(start, n, s!); break;
            case ShaderStage.Hull: ctx.HSSetSamplers(start, n, s!); break;
            case ShaderStage.Domain: ctx.DSSetSamplers(start, n, s!); break;
            case ShaderStage.Geometry: ctx.GSSetSamplers(start, n, s!); break;
            case ShaderStage.Pixel: ctx.PSSetSamplers(start, n, s!); break;
            case ShaderStage.Compute: ctx.CSSetSamplers(start, n, s!); break;
        }
    }

    private static void SetUavs(ID3D11DeviceContext ctx, ShaderStage stage, uint start, uint n, ID3D11UnorderedAccessView?[] u, uint[] initialCounts)
    {
        if (stage != ShaderStage.Compute)
            throw new NotSupportedException($"UAVs are only bindable to CS and PS in D3D11 (got {stage})");
        ctx.CSSetUnorderedAccessViews(start, n, u!, initialCounts);
    }
}
