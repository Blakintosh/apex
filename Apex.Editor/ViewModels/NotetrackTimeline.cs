using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Apex.Editor.Services.Preview.Notetracks;

namespace Apex.Editor.ViewModels;

/// <summary>
/// The notetrack timeline under the xanim preview (read-only): the markers of the loaded clip, from its exported
/// notetracks and its GDT notetrack actions, and the sounds they play as the preview's frame moves past them.
/// <para>
/// How a frame change sounds depends on how the frame moved (<see cref="FrameChanged"/>): the playback clock plays
/// every sound marker it passes (across the loop too, and frame 0's when playing starts there); a step of one frame
/// plays the markers it lands past; a jump (a click on a marker or the track, a key) plays only the markers on the
/// frame it lands on; a drag (<see cref="IsScrubbing"/>) plays each marker at most once per drag, and of several passed
/// in one move only the nearest. Pausing silences.
/// </para>
/// </summary>
public sealed partial class NotetrackTimeline : ObservableObject, IDisposable
{
    private readonly Func<INotetrackAudio> _audioFactory;
    private readonly Random _random;
    private INotetrackAudio? _audio;
    private double? _lastFrame;
    private int _generation;
    private bool _disposed;
    // Markers are replaced as a whole when their aliases resolve (or a file fails to decode), so a drag remembers
    // what it has heard by identity, not by record value.
    private readonly HashSet<(NotetrackSource Source, string Label, int Frame)> _heardThisDrag = new();
    private bool _dragMoved;
    // Files the output could not decode. It reports each once (it caches the failure), so the timeline keeps them and
    // flags any marker built later (a reload, an alias resolving) that would use one.
    private readonly HashSet<string> _undecodable = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The markers, by frame (replaced as a whole: the timeline draws from one snapshot).</summary>
    [ObservableProperty]
    private IReadOnlyList<NotetrackMarker> _markers = Array.Empty<NotetrackMarker>();

    /// <summary>The clip's last frame (the timeline spans 0..LastFrame).</summary>
    [ObservableProperty]
    private int _lastFrameIndex;

    /// <summary>True while the user drags the frame slider or the timeline.</summary>
    [ObservableProperty]
    private bool _isScrubbing;

    /// <param name="audioFactory">Opens the sound output on the first sound (none is opened for an anim that plays none).</param>
    /// <param name="random">Picks among an alias's variants (seeded in the harness).</param>
    public NotetrackTimeline(Func<INotetrackAudio>? audioFactory = null, Random? random = null)
    {
        _audioFactory = audioFactory ?? (() => new NAudioNotetrackAudio());
        _random = random ?? Random.Shared;
    }

    /// <summary>Raised for every sound started (marker, file, volume): the harness listens here.</summary>
    public event Action<NotetrackMarker, SoundVariant, float>? SoundStarted;

    private static (NotetrackSource, string, int) Key(NotetrackMarker m) => (m.Source, m.Label, m.Frame);

    /// <summary>
    /// Shows a clip's markers, the preview standing on <paramref name="currentFrame"/> (the next move is measured from
    /// it; showing them is not a move). Sound markers start pending and are looked up in <paramref name="aliases"/> on
    /// a worker (the index is built on first use); null leaves them unresolved. The returned task never faults.
    /// </summary>
    public Task Load(IEnumerable<NotetrackMarker> markers, int lastFrame, SoundAliasIndex? aliases, double currentFrame = 0)
    {
        var generation = ++_generation;
        Silence();
        _lastFrame = currentFrame;
        _heardThisDrag.Clear();
        LastFrameIndex = lastFrame;
        var list = markers.OrderBy(m => m.Frame).ThenBy(m => m.Source).Select(WithoutUndecodable).ToList();
        if (aliases is null)
            list = list.Select(m => m.SoundState == NotetrackSoundState.Pending ? m with { SoundState = NotetrackSoundState.UnknownAlias } : m).ToList();
        Markers = list;
        if (aliases is null || !list.Any(m => m.SoundState == NotetrackSoundState.Pending))
            return Task.CompletedTask;
        return ResolveAsync(list, aliases, generation);
    }

    /// <summary>The GDT entry's actions changed (an edit): new markers, same clip and frame, nothing reloaded.</summary>
    public Task Reload(IEnumerable<NotetrackMarker> markers, SoundAliasIndex? aliases) =>
        Load(markers, LastFrameIndex, aliases, _lastFrame ?? 0);

    public void Clear()
    {
        _generation++;
        Silence();
        _lastFrame = null;
        Markers = Array.Empty<NotetrackMarker>();
        LastFrameIndex = 0;
    }

    private async Task ResolveAsync(List<NotetrackMarker> list, SoundAliasIndex aliases, int generation)
    {
        Dictionary<string, SoundResolution> found;
        try
        {
            found = await Task.Run(async () =>
            {
                await aliases.ReadyAsync().ConfigureAwait(false);
                return list.Where(m => m.SoundState == NotetrackSoundState.Pending).Select(m => m.Param!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(a => a, aliases.Resolve, StringComparer.OrdinalIgnoreCase);
            });
        }
        catch (Exception)
        {
            // Unreadable aliases leave the sounds silent (and flagged), never an error.
            found = new Dictionary<string, SoundResolution>(StringComparer.OrdinalIgnoreCase);
        }
        void Apply()
        {
            if (generation != _generation)
                return; // a newer clip owns the timeline
            Markers = list.Select(m => m.SoundState != NotetrackSoundState.Pending ? m
                : found.TryGetValue(m.Param!, out var r) ? WithoutUndecodable(m with { SoundState = r.State, Variants = r.Variants })
                : m with { SoundState = NotetrackSoundState.UnknownAlias }).ToList();
        }
        if (Dispatcher.UIThread.CheckAccess())
            Apply();
        else
            await Dispatcher.UIThread.InvokeAsync(Apply);
    }

    partial void OnIsScrubbingChanged(bool value)
    {
        _heardThisDrag.Clear();
        _dragMoved = false;
    }

    /// <summary>The press that began this drag has landed (the frame it set, if any, was a jump): what follows is the drag.</summary>
    public void ScrubPressed() => _dragMoved = true;

    /// <summary>Playback started on <paramref name="frame"/>: from the first frame, its markers play, as they do when the loop wraps there.</summary>
    public void PlaybackStarted(double frame)
    {
        _lastFrame = frame;
        if (Math.Abs(frame) < 1e-3)
            PlayAll(Markers.Where(m => m.Frame == 0));
    }

    /// <summary>
    /// The preview's frame moved to <paramref name="frame"/>. <paramref name="fromClock"/> is true only for the playback
    /// clock's own advance (fractional frames, and a move backwards is the loop wrapping); a key, a click or a drag
    /// while playing is a user move like any other.
    /// </summary>
    public void FrameChanged(double frame, bool fromClock)
    {
        var from = _lastFrame;
        _lastFrame = frame;
        if (from is not { } prev || prev == frame || Markers.Count == 0)
            return;

        if (IsScrubbing && !_dragMoved && Math.Abs(frame - prev) > 1.0 + 1e-6)
        {
            // The press that starts a drag jumps: only what is on the frame it lands on (heard once for the drag).
            _dragMoved = true;
            var landed = Landed(frame).ToList();
            foreach (var m in landed)
                _heardThisDrag.Add(Key(m));
            PlayAll(landed);
            return;
        }
        if (IsScrubbing)
        {
            _dragMoved = true;
            // Of the markers this move passed that this drag has not yet heard, only the nearest to the pointer plays.
            var passed = Passed(prev, frame).Where(m => !_heardThisDrag.Contains(Key(m))).ToList();
            foreach (var m in passed)
                _heardThisDrag.Add(Key(m));
            if (passed.Count == 0)
                return;
            var nearest = passed.Min(m => Math.Abs(m.Frame - frame));
            PlayAll(passed.Where(m => Math.Abs(m.Frame - frame) == nearest));
            return;
        }
        if (fromClock)
        {
            // The clock only runs forwards: a move back is the loop wrapping, which plays the tail and the head.
            PlayAll(frame >= prev ? Passed(prev, frame) : Passed(prev, LastFrameIndex + 0.5).Concat(Passed(-0.5, frame)));
            return;
        }
        if (Math.Abs(frame - prev) <= 1.0 + 1e-6)
        {
            PlayAll(Passed(prev, frame)); // a step
            return;
        }
        PlayAll(Landed(frame)); // a jump: only what is on the frame it lands on
    }

    private IEnumerable<NotetrackMarker> Landed(double frame) =>
        Math.Abs(frame - Math.Round(frame)) < 1e-3 ? Markers.Where(m => m.Frame == (int)Math.Round(frame)) : Enumerable.Empty<NotetrackMarker>();

    /// <summary>Markers strictly after <paramref name="from"/> up to and including <paramref name="to"/>, either direction.</summary>
    private IEnumerable<NotetrackMarker> Passed(double from, double to) =>
        to >= from
            ? Markers.Where(m => m.Frame > from + 1e-6 && m.Frame <= to + 1e-6)
            : Markers.Where(m => m.Frame < from - 1e-6 && m.Frame >= to - 1e-6);

    private void PlayAll(IEnumerable<NotetrackMarker> markers)
    {
        if (_disposed)
            return;
        foreach (var m in markers)
        {
            if (m.SoundState != NotetrackSoundState.Playable || m.Variants.Count == 0)
                continue;
            var variant = m.Variants[_random.Next(m.Variants.Count)];
            var volume = variant.VolumeMin + (float)_random.NextDouble() * (variant.VolumeMax - variant.VolumeMin);
            Audio().Play(variant.Path, volume);
            SoundStarted?.Invoke(m, variant, volume);
        }
    }

    private INotetrackAudio Audio()
    {
        if (_audio is null)
        {
            _audio = _audioFactory();
            _audio.Undecodable += OnUndecodable;
        }
        return _audio;
    }

    /// <summary>
    /// A file the output could not decode (raised on its worker): that variant is dropped, and a marker left with none
    /// is flagged as silent, as an unknown alias is.
    /// </summary>
    private void OnUndecodable(string path) => Dispatcher.UIThread.Post(() =>
    {
        if (_disposed || !_undecodable.Add(path))
            return;
        if (Markers.Any(m => m.Variants.Any(v => _undecodable.Contains(v.Path))))
            Markers = Markers.Select(WithoutUndecodable).ToList();
    });

    /// <summary><paramref name="m"/> without the variants known not to decode; with none left, flagged as silent.</summary>
    private NotetrackMarker WithoutUndecodable(NotetrackMarker m)
    {
        if (_undecodable.Count == 0 || !m.Variants.Any(v => _undecodable.Contains(v.Path)))
            return m;
        var left = m.Variants.Where(v => !_undecodable.Contains(v.Path)).ToList();
        return left.Count > 0 ? m with { Variants = left }
            : m with { SoundState = NotetrackSoundState.Undecodable, Variants = Array.Empty<SoundVariant>() };
    }

    /// <summary>Stops every sound (pause, stop, a new clip).</summary>
    public void Silence() => _audio?.StopAll();

    /// <summary>Releases the sound device (the preview closed); nothing plays afterwards.</summary>
    public void Dispose()
    {
        _disposed = true;
        _generation++;
        if (_audio is not null)
        {
            _audio.Undecodable -= OnUndecodable;
            _audio.Dispose();
        }
        _audio = null;
    }

    /// <summary>The marker before or after <paramref name="frame"/> (keyboard navigation), or null at either end.</summary>
    public NotetrackMarker? Neighbour(double frame, int direction) =>
        direction > 0
            ? Markers.FirstOrDefault(m => m.Frame > frame + 1e-3)
            : Markers.LastOrDefault(m => m.Frame < frame - 1e-3);
}
