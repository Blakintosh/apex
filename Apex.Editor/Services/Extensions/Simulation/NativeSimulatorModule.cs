using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using static Apex.Editor.Services.Extensions.Simulation.SimulatorNative;

namespace Apex.Editor.Services.Extensions.Simulation;

/// <summary>
/// A module that passed every check: its exports resolved, its ABI is 1 and its id is the manifest's. Every call into
/// it is made here, on the host's thread, with buffers Apex owns and sizes it set; nothing the module returns is kept
/// as a pointer.
/// </summary>
internal sealed unsafe class NativeSimulatorModule : ISimulatorModule
{
    private readonly SimulatorHost _host;
    private readonly Exports _exports;
    private readonly List<NativeSimulation> _live = new();
    private int _failuresInARow;

    public NativeSimulatorModule(SimulatorHost host, Exports exports, string id, string version, string path, string sha256)
    {
        _host = host;
        _exports = exports;
        ExtensionId = id;
        ModuleVersion = version;
        Path = path;
        Sha256 = sha256;
    }

    public string ExtensionId { get; }
    public string ModuleVersion { get; }
    public string Path { get; }
    public string Sha256 { get; }
    public bool IsTurnedOff { get; private set; }

    /// <summary>Why every step now fails; set when the module is turned off.</summary>
    internal string OffNote { get; private set; } = "";

    public SimulationResult Create(IReadOnlyList<KeyValuePair<string, string>> values)
    {
        _host.VerifyThread();
        if (IsTurnedOff)
            return new SimulationResult(null, SimulatorStatus.TurnedOff, OffNote);

        // One block: the pairs, then every string NUL-terminated. Freed before this returns: the module may not keep it.
        var count = values.Count;
        var bytes = 0;
        foreach (var (key, value) in values)
            bytes += Encoding.UTF8.GetByteCount(key) + Encoding.UTF8.GetByteCount(value) + 2;
        var kvBytes = count * sizeof(Kv);
        var block = (byte*)NativeMemory.AllocZeroed((nuint)Math.Max(1, kvBytes + bytes));
        var err = Alloc(ErrorCapacity);
        try
        {
            var kv = (Kv*)block;
            var text = block + kvBytes;
            var end = text + bytes;
            for (var i = 0; i < count; i++)
            {
                kv[i].Key = text;
                text += Encoding.UTF8.GetBytes(values[i].Key, new Span<byte>(text, (int)(end - text))) + 1;
                kv[i].Value = text;
                text += Encoding.UTF8.GetBytes(values[i].Value, new Span<byte>(text, (int)(end - text))) + 1;
            }
            var fp = FloatingPoint();
            var started = Stopwatch.GetTimestamp();
            var sim = _exports.Create(count == 0 ? null : kv, (uint)count, err, ErrorCapacity);
            var fpChanged = FloatingPointChanged(fp);
            var ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (!CanaryIntact(err, ErrorCapacity))
            {
                TurnOff("it wrote past the end of the message space Apex gave it");
                // What it made is never used, but its memory is its own to free.
                if (sim != null)
                    _exports.Destroy(sim);
                return new SimulationResult(null, SimulatorStatus.TurnedOff, OffNote);
            }
            var slow = ms > SimulatorHost.SlowCreateMs;
            if (slow)
                SaySlow($"took {ms:0} ms to start a simulation; create should take well under a second, since Apex's window waits for it");
            if (fpChanged)
                SayFloatingPoint();
            if ((fpChanged || slow) && Counted(fpChanged ? "a create that changed the floating-point settings" : $"a create that took {ms:0} ms"))
            {
                if (sim != null)
                    _exports.Destroy(sim);
                return new SimulationResult(null, SimulatorStatus.TurnedOff, OffNote);
            }
            if (sim == null)
            {
                var why = Text(err, ErrorCapacity);
                return new SimulationResult(null, SimulatorStatus.Loaded,
                    why.Length > 0 ? $"{ExtensionId} can't preview this weapon: {why}" : $"{ExtensionId} can't preview this weapon.");
            }
            var simulation = new NativeSimulation(this, sim);
            _live.Add(simulation);
            return new SimulationResult(simulation, SimulatorStatus.Loaded, null);
        }
        finally
        {
            NativeMemory.Free(err);
            NativeMemory.Free(block);
        }
    }

    internal void Reset(void* sim)
    {
        _host.VerifyThread();
        if (IsTurnedOff)
            return;
        var fp = FloatingPoint();
        _exports.Reset(sim);
        if (FloatingPointChanged(fp))
        {
            SayFloatingPoint();
            Counted("a reset that changed the floating-point settings");
        }
    }

    internal void VerifyThread() => _host.VerifyThread();

    internal SimulatorFrame Step(void* sim, Input* input, Output* output)
    {
        if (IsTurnedOff)
            return SimulatorFrame.Failed(OffNote);
        new Span<byte>(output, sizeof(Output)).Clear();
        output->Size = (uint)sizeof(Output);
        var fp = FloatingPoint();
        var started = Stopwatch.GetTimestamp();
        var result = _exports.Step(sim, input, output);
        var fpChanged = FloatingPointChanged(fp);
        var ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        if (!CanaryIntact((byte*)output, sizeof(Output)))
        {
            TurnOff("it wrote past the end of the output Apex gave it");
            return SimulatorFrame.Failed(OffNote);
        }
        if (fpChanged)
        {
            SayFloatingPoint();
            return Failure("The preview module changed the floating-point settings and didn't put them back; Apex did.");
        }
        var note = Text(output->Note, NoteCapacity);
        if (result != 0)
            return Failure(note.Length > 0 ? note : "The preview module reported a problem with this step.");

        var supported = (SimulatorParts)output->Supported & SimulatorParts.All;
        var frame = new SimulatorFrame(true, supported,
            Part(supported, SimulatorParts.ViewAngles, output->ViewAngles),
            Part(supported, SimulatorParts.ViewOrigin, output->ViewOrigin),
            Part(supported, SimulatorParts.GunAngles, output->GunAngles),
            Part(supported, SimulatorParts.GunOrigin, output->GunOrigin),
            note);
        if (!(IsFinite(frame.ViewAngles) && IsFinite(frame.ViewOrigin) && IsFinite(frame.GunAngles) && IsFinite(frame.GunOrigin)))
            return Failure("The preview module returned motion that isn't a number.");
        if (ms > SimulatorHost.SlowStepMs)
            return Slow(frame, ms);
        _failuresInARow = 0;
        return frame;
    }

    /// <summary>
    /// A step that held the UI thread past <see cref="SimulatorHost.SlowStepMs"/>: drawn, but counted with the failures
    /// (so a module that keeps stalling Apex is turned off like one that keeps failing) and named once.
    /// </summary>
    private SimulatorFrame Slow(SimulatorFrame frame, double ms)
    {
        SaySlow($"took {ms:0} ms for one step; a step should take about a millisecond, since Apex's window waits for it");
        return Counted($"a step that took {ms:0} ms") ? SimulatorFrame.Failed(OffNote) : frame;
    }

    /// <summary>
    /// A call that returned but broke the header's rules (too slow, or the floating-point state left changed): counted
    /// with the failed steps; true when that turned the module off.
    /// </summary>
    private bool Counted(string last)
    {
        if (++_failuresInARow < SimulatorHost.MaxFailuresInARow)
            return false;
        TurnOff($"it was too slow, failed or broke the interface's rules {SimulatorHost.MaxFailuresInARow} times in a row (the last: {last})");
        return true;
    }

    private bool _saidSlow, _saidFloatingPoint;

    private void SaySlow(string what)
    {
        if (_saidSlow)
            return;
        _saidSlow = true;
        _host.ModuleFault(this, what);
    }

    private void SayFloatingPoint()
    {
        if (_saidFloatingPoint)
            return;
        _saidFloatingPoint = true;
        _host.ModuleFault(this, "changed Apex's floating-point settings (rounding or flush-to-zero) and didn't put them back; Apex did");
    }

    /// <summary>A failed step; <see cref="SimulatorHost.MaxFailuresInARow"/> of them in a row turn the module off.</summary>
    private SimulatorFrame Failure(string note)
    {
        if (++_failuresInARow >= SimulatorHost.MaxFailuresInARow)
        {
            TurnOff($"it failed {SimulatorHost.MaxFailuresInARow} steps in a row (the last: {note.TrimEnd('.')})");
            return SimulatorFrame.Failed(OffNote);
        }
        return SimulatorFrame.Failed(note);
    }

    private void TurnOff(string why)
    {
        if (IsTurnedOff)
            return;
        IsTurnedOff = true;
        OffNote = $"Apex turned off the {ExtensionId} preview module until it restarts. Editing and saving work as usual.";
        _host.ModuleTurnedOff(this, why);
    }

    internal void Destroy(NativeSimulation simulation, void* sim)
    {
        _host.VerifyThread();
        _live.Remove(simulation);
        // A module turned off for writing out of bounds is still asked to free its own memory: destroy reads nothing back.
        var fp = FloatingPoint();
        _exports.Destroy(sim);
        if (FloatingPointChanged(fp) && !IsTurnedOff)
        {
            SayFloatingPoint();
            Counted("a destroy that changed the floating-point settings");
        }
    }

    /// <summary>Destroys what consumers left alive (host shutdown), so the module is idle before it is freed.</summary>
    internal void DestroyAll()
    {
        foreach (var s in _live.ToArray())
            s.Dispose();
    }

    private static Vector3 Part(SimulatorParts supported, SimulatorParts part, float* v) =>
        (supported & part) != 0 ? new Vector3(v[0], v[1], v[2]) : default;

    private static bool IsFinite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}

/// <summary>One apex_sim object, with its input and output buffers allocated once and reused for every step.</summary>
internal sealed unsafe class NativeSimulation : ISimulation
{
    private readonly NativeSimulatorModule _module;
    private void* _sim;
    private readonly Input* _input;
    private readonly Output* _output;

    public NativeSimulation(NativeSimulatorModule module, void* sim)
    {
        _module = module;
        _sim = sim;
        _input = (Input*)Alloc(sizeof(Input));
        _output = (Output*)Alloc(sizeof(Output));
    }

    public string ExtensionId => _module.ExtensionId;

    public bool IsAlive => _sim != null && !_module.IsTurnedOff;

    // After Dispose (or the host's shutdown, which disposes what is left) a call is a no-op, never a call into a module
    // that may be gone: a preview stepping during app exit mustn't throw.
    public void Reset()
    {
        if (_sim != null)
            _module.Reset(_sim);
    }

    public SimulatorFrame Step(float dt, float ads, uint shots)
    {
        if (_sim == null)
            return SimulatorFrame.Failed("This simulation was closed.");
        _module.VerifyThread();
        if (!(dt > 0) || !float.IsFinite(dt))
            return SimulatorFrame.Failed("A step needs a time above zero.");
        *_input = new Input
        {
            Size = (uint)sizeof(Input),
            Dt = dt,
            Ads = float.IsFinite(ads) ? Math.Clamp(ads, 0f, 1f) : 0f,
            Shots = shots,
        };
        return _module.Step(_sim, _input, _output);
    }

    public void Dispose()
    {
        if (_sim == null)
            return;
        _module.Destroy(this, _sim);
        _sim = null;
        NativeMemory.Free(_input);
        NativeMemory.Free(_output);
    }
}
