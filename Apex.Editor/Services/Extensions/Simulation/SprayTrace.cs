using System;
using System.Numerics;

namespace Apex.Editor.Services.Extensions.Simulation;

/// <summary>
/// What the recoil preview's overlay draws and its readout says, measured from the frames the preview already steps
/// (<see cref="RecoilDriver.Stepped"/>): the view angles only, and only while the module says it computes them. Every
/// value is an engine angle in degrees, pitch negative up, yaw positive left, as the module reports it.
///
/// <list type="bullet">
/// <item><b>Burst</b>: starts with a round fired while no burst is open (the first, or the last one settled or came to
/// rest); rounds fired before then join it, so quick taps are one burst. A new burst clears the last one.</item>
/// <item><b>Shot peak</b> (a dot): from the step that fires a round until the step before the next round (or the burst's
/// end), the sample farthest from rest (largest √(pitch² + yaw²)). Rounds fired in the same step share it.</item>
/// <item><b>Climb</b>: the most the view went up during the burst (−pitch, never below 0).</item>
/// <item><b>Drift</b>: the yaw farthest from 0 during the burst, signed (positive left).</item>
/// <item><b>Settle time</b>: from the start of the step that fired the burst's last round to the first sample from
/// which the view stays within <see cref="SettleDegrees"/> of rest for <see cref="SettleHoldSeconds"/>, or from which it
/// stays there until the motion comes to rest. A burst whose motion comes to rest away from rest, or stops being
/// stepped, before that "doesn't return".</item>
/// <item><b>Trail</b>: the view's samples during the burst and its settling, the last <see cref="TrailCapacity"/>.</item>
/// </list>
/// Time is the sum of the steps' dt, so a burst measures the same however the frames fell. Fixed buffers: adding a
/// step allocates nothing. UI thread, like the driver.
/// </summary>
public sealed class SprayTrace
{
    /// <summary>Within this of rest (degrees) the view counts as back.</summary>
    public const float SettleDegrees = 0.05f;

    /// <summary>How long it must stay there (seconds): a spring passing through 0 hasn't settled.</summary>
    public const float SettleHoldSeconds = 0.1f;

    public const int DotCapacity = 512;
    public const int TrailCapacity = 2048;

    /// <summary>Closer than this (degrees) to the last trail point adds no point: a resting view adds nothing.</summary>
    private const float TrailStep = 0.002f;

    private readonly Vector2[] _dots = new Vector2[DotCapacity];
    private readonly float[] _dotPeak = new float[DotCapacity];
    private readonly Vector2[] _trail = new Vector2[TrailCapacity];
    private int _dotStart, _dotCount, _trailStart, _trailCount;

    // The rounds fired by the latest firing step: their peaks follow the samples until the next round.
    private int _groupFirst, _groupSize;

    private bool _open;
    private float _time, _lastShotAt, _within = float.NaN;

    /// <summary>A burst has started since the last <see cref="Clear"/>.</summary>
    public bool HasBurst { get; private set; }

    /// <summary>Rounds fired in the burst.</summary>
    public int Shots { get; private set; }

    /// <summary>False when the last good step said the module doesn't compute view angles: nothing is measured.</summary>
    public bool ViewSupported { get; private set; } = true;

    public float Climb { get; private set; }

    public float Drift { get; private set; }

    /// <summary>Milliseconds the view took to settle after the last round; null until it has.</summary>
    public float? SettleMs { get; private set; }

    /// <summary>The burst came to rest away from rest, or stopped being stepped, before it settled.</summary>
    public bool DoesNotReturn { get; private set; }

    /// <summary>The latest sample (pitch, yaw).</summary>
    public Vector2 Current { get; private set; }

    /// <summary>Changes with every step that changed something (the overlay redraws only then).</summary>
    public int Version { get; private set; }

    public event Action? Changed;

    /// <summary>Dots shown: the last <see cref="DotCapacity"/> rounds of the burst.</summary>
    public int DotCount => _dotCount;

    /// <summary>Dot <paramref name="i"/> (0 = oldest shown) as (pitch, yaw).</summary>
    public Vector2 Dot(int i) => _dots[(_dotStart + i) % DotCapacity];

    public int TrailCount => _trailCount;

    public Vector2 TrailPoint(int i) => _trail[(_trailStart + i) % TrailCapacity];

    /// <summary>One step of the preview: <paramref name="shots"/> rounds fired at its start, <paramref name="frame"/> what came back.</summary>
    public void Add(float dt, uint shots, in SimulatorFrame frame)
    {
        if (!frame.Ok || !float.IsFinite(dt) || dt <= 0f)
            return;
        // Not HasFlag: unoptimised (tier-0) code boxes it, and this runs every frame of a burst.
        var view = (frame.Supported & SimulatorParts.ViewAngles) != 0;
        if (view != ViewSupported)
        {
            ViewSupported = view;
            if (!view)
                ClearMarks();
            Touch();
        }
        _time += dt;
        if (shots > 0)
        {
            if (!_open)
                StartBurst();
            Shots = (int)Math.Min((long)Shots + shots, int.MaxValue);
            _lastShotAt = _time - dt;
            SettleMs = null;
            DoesNotReturn = false;
            _within = float.NaN;
            if (view)
                AddDots((int)Math.Min(shots, (uint)DotCapacity));
            Touch();
        }
        if (!view || !_open)
            return;

        var s = new Vector2(frame.ViewAngles.X, frame.ViewAngles.Y);
        Current = s;
        var reach = s.Length();
        for (var i = 0; i < _groupSize; i++)
        {
            var at = (_groupFirst + i) % DotCapacity;
            if (reach > _dotPeak[at])
            {
                _dotPeak[at] = reach;
                _dots[at] = s;
            }
        }
        AddTrail(s);
        Climb = MathF.Max(Climb, -s.X);
        if (MathF.Abs(s.Y) > MathF.Abs(Drift))
            Drift = s.Y;
        if (SettleMs is null)
        {
            if (reach <= SettleDegrees)
            {
                if (float.IsNaN(_within))
                    _within = _time;
                if (_time - _within >= SettleHoldSeconds - 1e-6f)
                {
                    SettleMs = (_within - _lastShotAt) * 1000f;
                    _open = false;
                }
            }
            else
            {
                _within = float.NaN;
            }
        }
        Touch();
    }

    /// <summary>
    /// The preview stopped stepping (the motion came to rest, or the quiet limit): a burst resting within
    /// <see cref="SettleDegrees"/> of rest has settled however short its hold (one that never moved the view), any
    /// other unsettled burst doesn't return.
    /// </summary>
    public void Rest()
    {
        if (!_open)
            return;
        _open = false;
        if (SettleMs is null && ViewSupported && Shots > 0)
        {
            if (float.IsNaN(_within))
                DoesNotReturn = true;
            else
                SettleMs = (_within - _lastShotAt) * 1000f;
            Touch();
        }
    }

    /// <summary>Back to nothing measured (Reset, a new simulation).</summary>
    public void Clear()
    {
        ClearMarks();
        _open = HasBurst = DoesNotReturn = false;
        Shots = 0;
        Climb = Drift = 0f;
        SettleMs = null;
        Current = default;
        _time = _lastShotAt = 0f;
        _within = float.NaN;
        ViewSupported = true;
        Touch();
    }

    private void StartBurst()
    {
        ClearMarks();
        _open = HasBurst = true;
        Shots = 0;
        Climb = Drift = 0f;
    }

    private void ClearMarks()
    {
        _dotStart = _dotCount = _trailStart = _trailCount = _groupFirst = _groupSize = 0;
    }

    private void AddDots(int count)
    {
        _groupFirst = (_dotStart + _dotCount) % DotCapacity;
        _groupSize = count;
        for (var i = 0; i < count; i++)
        {
            var at = (_dotStart + _dotCount) % DotCapacity;
            _dots[at] = default;
            _dotPeak[at] = -1f;
            if (_dotCount < DotCapacity)
                _dotCount++;
            else
                _dotStart = (_dotStart + 1) % DotCapacity;
        }
    }

    private void AddTrail(Vector2 s)
    {
        if (_trailCount > 0 && Vector2.DistanceSquared(TrailPoint(_trailCount - 1), s) < TrailStep * TrailStep)
            return;
        _trail[(_trailStart + _trailCount) % TrailCapacity] = s;
        if (_trailCount < TrailCapacity)
            _trailCount++;
        else
            _trailStart = (_trailStart + 1) % TrailCapacity;
    }

    private void Touch()
    {
        Version++;
        Changed?.Invoke();
    }
}
