using System;

namespace Apex.Editor.Services.Extensions.Simulation;

/// <summary>
/// The inputs a weapon preview feeds its simulation, one display frame at a time: the trigger (rounds at the weapon's
/// fire cadence while it is held; a tap fires one), the ADS fraction moving to hip or ADS over the weapon's transition
/// time, and the steps themselves. It asks for frames only while something moves: the trigger is held or a shot is due,
/// ADS is moving, or the motion hasn't come to rest (unchanged for <see cref="SettleSeconds"/>), and never for longer
/// than <see cref="MaxQuietSeconds"/> after the last input (a module whose motion never rests doesn't keep the preview
/// drawing forever). UI thread, like the simulation.
/// </summary>
public sealed class RecoilDriver
{
    /// <summary>The longest step sent: a hitch (a GC, a dialog) is one ordinary step, not a jump through the motion.</summary>
    public const float MaxStep = 0.1f;

    /// <summary>Shorter frames are added to the next one (a module gets dt above 0 and nothing meaninglessly small).</summary>
    public const float MinStep = 0.0005f;

    public const float SettleSeconds = 0.25f;

    public const float MaxQuietSeconds = 10f;

    /// <summary>Largest change of any output (degrees, inches) between steps that still counts as at rest.</summary>
    public const float Stillness = 1e-4f;

    private ISimulation? _simulation;
    private bool _held;
    private bool _tapPending;
    private bool _wasFiring;
    private float _sinceShot = float.PositiveInfinity;
    private float _still = SettleSeconds;
    private float _quiet = MaxQuietSeconds;
    private float _carry;

    /// <summary>The simulation stepped (null: none, the ADS fraction still moves the pose). Setting it starts at rest.</summary>
    public ISimulation? Simulation
    {
        get => _simulation;
        set
        {
            _simulation = value;
            ToRest();
        }
    }

    /// <summary>Seconds between rounds while the trigger is held.</summary>
    public float FireTime { get; set; } = 0.1f;

    /// <summary>Seconds from hip to ADS and back (0: at once).</summary>
    public float AdsInTime { get; set; }

    public float AdsOutTime { get; set; }

    /// <summary>Where the ADS fraction is heading: true for ADS, false for hip.</summary>
    public bool AimDownSights
    {
        get => _aim;
        set
        {
            if (_aim == value)
                return;
            _aim = value;
            Wake();
        }
    }

    private bool _aim;

    /// <summary>0 = hip, 1 = ADS, what the last step sent.</summary>
    public float Ads { get; private set; }

    /// <summary>Rounds fired since the trigger was last pressed.</summary>
    public int Rounds { get; private set; }

    public bool IsTriggerHeld => _held;

    /// <summary>The last step's motion (default before the first).</summary>
    public SimulatorFrame Frame { get; private set; }

    /// <summary>Every step sent: dt, ads, shots and what came back (verification).</summary>
    public event Action<float, float, uint, SimulatorFrame>? Stepped;

    /// <summary>True while the next display frame has something to do.</summary>
    public bool NeedsFrames =>
        Ads != (_aim ? 1f : 0f)
        || _simulation is { IsAlive: true } && (_held || _tapPending || _quiet < MaxQuietSeconds && _still < SettleSeconds);

    /// <summary>The trigger goes down: a round as soon as the cadence allows, then one per <see cref="FireTime"/> while held.</summary>
    public void PressTrigger()
    {
        if (_simulation is not { IsAlive: true } || _held)
            return;
        if (!NeedsFrames)
            _sinceShot = float.PositiveInfinity;
        _held = true;
        _tapPending = true;
        Rounds = 0;
        Wake();
    }

    public void ReleaseTrigger() => _held = false;

    /// <summary>Back to the state right after the simulation was made (a fresh burst); the ADS fraction stays.</summary>
    public void Reset()
    {
        _simulation?.Reset();
        Rounds = 0;
        ToRest();
    }

    private void ToRest()
    {
        _held = _tapPending = _wasFiring = false;
        _sinceShot = float.PositiveInfinity;
        _still = SettleSeconds;
        _quiet = MaxQuietSeconds;
        _carry = 0f;
        Frame = default;
    }

    private void Wake()
    {
        _still = 0f;
        _quiet = 0f;
    }

    /// <summary>
    /// One display frame of <paramref name="seconds"/> (clamped to <see cref="MaxStep"/>): moves the ADS fraction, fires
    /// the rounds that are due at the start of the step, steps the simulation. False when nothing needs another frame.
    /// </summary>
    public bool Step(float seconds)
    {
        if (!float.IsFinite(seconds) || seconds <= 0f)
            return NeedsFrames;
        var dt = MathF.Min(seconds + _carry, MaxStep);
        if (dt < MinStep)
        {
            _carry = dt;
            return NeedsFrames;
        }
        _carry = 0f;

        var target = _aim ? 1f : 0f;
        if (Ads != target)
        {
            var time = _aim ? AdsInTime : AdsOutTime;
            Ads = time <= 0f ? target : _aim ? MathF.Min(Ads + dt / time, 1f) : MathF.Max(Ads - dt / time, 0f);
        }

        var shots = 0u;
        var firing = _held || _tapPending;
        if (firing && _sinceShot >= FireTime)
        {
            // A fresh press starts on the beat; only a held trigger keeps a backlog from a long frame.
            if (!_wasFiring)
                _sinceShot = FireTime;
            while (_sinceShot >= FireTime)
            {
                shots++;
                _sinceShot -= FireTime;
                if (!_held)
                    break;
            }
            _tapPending = false;
            Rounds += (int)shots;
        }
        _wasFiring = _held;

        if (_simulation is { IsAlive: true } sim)
        {
            var before = Frame;
            Frame = sim.Step(dt, Ads, shots);
            _still = !Frame.Ok || Change(before, Frame) < Stillness ? _still + dt : 0f;
            Stepped?.Invoke(dt, Ads, shots, Frame);
        }
        _sinceShot += dt;
        _quiet = shots > 0 || _held || Ads != target ? 0f : _quiet + dt;
        return NeedsFrames;
    }

    private static float Change(in SimulatorFrame a, in SimulatorFrame b)
    {
        var d = System.Numerics.Vector3.Abs(a.ViewAngles - b.ViewAngles);
        d = System.Numerics.Vector3.Max(d, System.Numerics.Vector3.Abs(a.ViewOrigin - b.ViewOrigin));
        d = System.Numerics.Vector3.Max(d, System.Numerics.Vector3.Abs(a.GunAngles - b.GunAngles));
        d = System.Numerics.Vector3.Max(d, System.Numerics.Vector3.Abs(a.GunOrigin - b.GunOrigin));
        return MathF.Max(d.X, MathF.Max(d.Y, d.Z));
    }
}
