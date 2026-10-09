using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

namespace Apex.Editor.Services.Extensions.Simulation;

/// <summary>The parts of the motion a module computed (apex_sim.h's APEX_SIM_* bits).</summary>
[Flags]
public enum SimulatorParts : uint
{
    None = 0,
    ViewAngles = 1u << 0,
    ViewOrigin = 1u << 1,
    GunAngles = 1u << 2,
    GunOrigin = 1u << 3,
    All = ViewAngles | ViewOrigin | GunAngles | GunOrigin,
}

/// <summary>
/// One step's motion, in the engine's viewmodel space (degrees, inches), added to the pose the preview already draws:
/// view* moves the camera, gun* only the viewmodel. A part not in <see cref="Supported"/> is zero and must not be
/// drawn as if it were computed; <see cref="Note"/> says why when the module gave a reason. A failed step
/// (<see cref="Ok"/> false) supports nothing and its note says what went wrong.
/// </summary>
public readonly record struct SimulatorFrame(
    bool Ok,
    SimulatorParts Supported,
    Vector3 ViewAngles,
    Vector3 ViewOrigin,
    Vector3 GunAngles,
    Vector3 GunOrigin,
    string Note)
{
    public static SimulatorFrame Failed(string note) => new(false, SimulatorParts.None, default, default, default, default, note);
}

/// <summary>
/// One weapon's simulation (an apex_sim object). UI thread only, like everything here: a call from another thread
/// throws <see cref="InvalidOperationException"/> without reaching the module. Never throws for what the module does.
/// </summary>
public interface ISimulation : IDisposable
{
    string ExtensionId { get; }

    /// <summary>False once disposed, or once its module was turned off for the session (every step then fails).</summary>
    bool IsAlive { get; }

    /// <summary>Back to the state right after it was created (a fresh burst).</summary>
    void Reset();

    /// <param name="dt">Seconds since the previous step; must be above 0 (a step of 0 or less isn't sent).</param>
    /// <param name="ads">0 = hip, 1 = fully aimed down sights (clamped).</param>
    /// <param name="shots">Rounds fired at the start of this step.</param>
    SimulatorFrame Step(float dt, float ads, uint shots);
}

/// <summary>A loaded module. It stays loaded until Apex closes (or the host is disposed).</summary>
public interface ISimulatorModule
{
    string ExtensionId { get; }

    /// <summary>The module's own version (apex_sim_info), for logs and tooltips.</summary>
    string ModuleVersion { get; }

    string Path { get; }

    /// <summary>The SHA-256 of the file Apex loaded, lower-case hex: what consent was given for.</summary>
    string Sha256 { get; }

    /// <summary>True once the module failed too often (or wrote past what it was given) and was turned off for the session.</summary>
    bool IsTurnedOff { get; }

    /// <summary>A simulation of one weapon from its effective extension values in row order (see <see cref="SimulatorRequest"/>).</summary>
    SimulationResult Create(IReadOnlyList<KeyValuePair<string, string>> values);
}

/// <summary>Where a module stands this session.</summary>
public enum SimulatorStatus
{
    /// <summary>The extension names no module (or isn't the one installed).</summary>
    None,

    /// <summary>Nothing has asked for it yet: not hashed, not loaded.</summary>
    NotLoaded,

    /// <summary>Being hashed, asked about or loaded.</summary>
    Pending,

    Loaded,

    /// <summary>The user chose "Don't load" for this build of the module (remembered by its hash).</summary>
    Declined,

    /// <summary>It couldn't be used: missing, not a 64-bit DLL, another ABI, the wrong id, an export missing.</summary>
    Failed,

    /// <summary>It loaded but kept failing, so it is off until Apex restarts.</summary>
    TurnedOff,
}

/// <summary>
/// What a preview asks for: one extension's module, with the asset's effective values of that extension's keys (own,
/// else inherited, else the manifest default), raw, in row order; a record list's rows as their numbered keys
/// (<c>wtKick1</c>, <c>wtKick2</c>…). Made by <c>AssetEditorViewModel.SimulatorRequests()</c>, which makes one only for an
/// extension that is on for the asset.
/// </summary>
public sealed record SimulatorRequest(ExtensionManifest Extension, IReadOnlyList<KeyValuePair<string, string>> Values);

/// <summary>A module, or why there isn't one (<see cref="Message"/>: one plain sentence, fit to show beside the preview).</summary>
public sealed record SimulatorModuleResult(ISimulatorModule? Module, SimulatorStatus Status, string? Message);

/// <summary>
/// A simulation, or why there isn't one. <see cref="Status"/> is the module's: Loaded with no simulation means the
/// module looked at this weapon and said it can't simulate it (<see cref="Message"/> has its reason).
/// </summary>
public sealed record SimulationResult(ISimulation? Simulation, SimulatorStatus Status, string? Message);

/// <summary>What the user is asked before a module is loaded for the first time, or after its file changed.</summary>
/// <param name="Changed">The user answered for another build of this extension's module before.</param>
/// <param name="Size">The file's length in bytes, as hashed.</param>
/// <param name="ModifiedUtc">The file's last write time, as hashed.</param>
/// <param name="Previous">The build answered for most recently, when <paramref name="Changed"/>: what the question compares with.</param>
public sealed record SimulatorConsentRequest(string ExtensionId, string ExtensionVersion, string ModulePath, string Sha256, bool Changed,
    long Size = 0, DateTime? ModifiedUtc = null, SimulatorModuleBuild? Previous = null);

/// <summary>A build of a module the user answered for; size and time are unknown for answers kept by Apex 0.2.0.</summary>
public sealed record SimulatorModuleBuild(string Sha256, long? Size, DateTime? ModifiedUtc, DateTime AnsweredUtc);

/// <summary>
/// Asks the user whether to load a module. Called on the UI thread, once per module at a time. True loads it, false is
/// "Don't load"; both are remembered for this hash. Null is no answer (the question was dismissed for another, or
/// withdrawn), which is remembered nowhere and leaves the module unloaded until something asks again.
/// </summary>
public interface ISimulatorConsent
{
    /// <param name="withdrawn">Cancelled when nothing waits for the answer any more (the last preview asking closed):
    /// the question goes away and the answer is null.</param>
    Task<bool?> AskAsync(SimulatorConsentRequest request, CancellationToken withdrawn);
}
