using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Apex.Editor.Services.Preview.Notetracks;

/// <summary>
/// Where notetrack sounds go. <see cref="Play"/> and <see cref="StopAll"/> are called on the UI thread and must return
/// at once: decoding and the device live elsewhere. Injectable so the harness can listen without a sound device.
/// </summary>
public interface INotetrackAudio : IDisposable
{
    /// <summary>Plays <paramref name="path"/> once at <paramref name="volume"/> (0..1), mixed over anything already playing.</summary>
    void Play(string path, float volume);

    /// <summary>Silences everything playing or about to play.</summary>
    void StopAll();

    /// <summary>A file that exists but can't be decoded (raised once per file, on any thread).</summary>
    event Action<string>? Undecodable;
}

/// <summary>
/// The default output: one shared-mode WASAPI stream (opened on the first sound, closed on dispose) mixing every
/// playing sound. Files are decoded to the mixer's format on a worker and cached (by bytes), so a marker heard again
/// starts immediately. A file that will not decode stays silent and is reported once; a file that is only locked or
/// briefly missing is tried again next time. A device that goes away (unplugged, the default changed) is reopened on
/// the next sound; one that can't be opened is not retried for a few seconds.
/// </summary>
public sealed class NAudioNotetrackAudio : INotetrackAudio
{
    private static readonly WaveFormat MixFormat = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);
    // Notetrack sounds are short; anything longer is cut here, so one odd file can't hold hundreds of megabytes.
    private const int MaxSeconds = 20;
    private const long CacheBudgetBytes = 96L * 1024 * 1024;
    private const int CacheMaxEntries = 512;
    private static readonly TimeSpan DeviceRetry = TimeSpan.FromSeconds(5);

    private readonly object _gate = new();
    private readonly DecodedSoundCache _cache = new(CacheBudgetBytes, CacheMaxEntries);

    /// <summary>When each file that failed to decode was last seen failing (size, write time). Worker only, like the cache.</summary>
    private readonly Dictionary<string, (long, DateTime)> _failedAt = new(StringComparer.OrdinalIgnoreCase);
    private Task _worker = Task.CompletedTask;
    private MixingSampleProvider? _mixer;
    private WasapiPlayer? _device;
    private readonly Stopwatch _sinceDeviceFailed = new();
    private int _generation;
    private bool _disposed;

    public event Action<string>? Undecodable;

    public void Play(string path, float volume)
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            var generation = _generation;
            _worker = _worker.ContinueWith(_ => Start(path, volume, generation), TaskScheduler.Default);
        }
    }

    public void StopAll()
    {
        lock (_gate)
        {
            _generation++;
            _mixer?.RemoveAllMixerInputs();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _generation++;
            _mixer?.RemoveAllMixerInputs();
            // Closing the device can take a few milliseconds: on the worker, after anything queued.
            _worker = _worker.ContinueWith(_ => CloseDevice(), TaskScheduler.Default);
        }
    }

    private void CloseDevice()
    {
        WasapiPlayer? device;
        lock (_gate)
        {
            device = _device;
            _device = null;
            _mixer = null;
        }
        if (device is null || !OperatingSystem.IsWindows())
            return;
        device.PlaybackStopped -= Device_PlaybackStopped;
        try
        {
            device.Stop();
        }
        catch (Exception)
        {
            // A device already gone can refuse Stop; it is released below regardless.
        }
        finally
        {
            try { device.Dispose(); } catch (Exception) { }
        }
    }

    /// <summary>The stream ended on its own (device unplugged or the default device changed): reopen on the next sound.</summary>
    private void Device_PlaybackStopped(object? sender, StoppedEventArgs e)
    {
        lock (_gate)
        {
            if (_disposed || !ReferenceEquals(sender, _device))
                return;
            _worker = _worker.ContinueWith(_ => CloseDevice(), TaskScheduler.Default);
        }
    }

    private void Start(string path, float volume, int generation)
    {
        try
        {
            var samples = Decoded(path);
            if (samples is null || !OperatingSystem.IsWindows())
                return;
            lock (_gate)
            {
                if (generation != _generation || _disposed)
                    return;
            }
            var mixer = EnsureDevice();
            lock (_gate)
            {
                if (mixer is not null && generation == _generation && !_disposed)
                    mixer.AddMixerInput(new BufferProvider(samples, volume));
            }
        }
        catch (Exception)
        {
            // The preview stays silent rather than failing.
        }
    }

    /// <summary>The open device's mixer, opening the device first (null while none can be opened).</summary>
    private MixingSampleProvider? EnsureDevice()
    {
        if (!OperatingSystem.IsWindows())
            return null;
        lock (_gate)
            if (_device is not null)
                return _mixer;
        if (_sinceDeviceFailed.IsRunning && _sinceDeviceFailed.Elapsed < DeviceRetry)
            return null;
        WasapiPlayer? device = null;
        try
        {
            var mixer = new MixingSampleProvider(MixFormat) { ReadFully = true };
            device = new WasapiPlayerBuilder().WithSharedMode().WithEventSync().WithLatency(40).Build();
            device.Init(new SoftClipSampleProvider(mixer).ToWaveProvider());
            // Known as the device before it plays, so a stop arriving the moment it starts is not ignored.
            lock (_gate)
            {
                _device = device;
                _mixer = mixer;
            }
            device.PlaybackStopped += Device_PlaybackStopped;
            device.Play();
            _sinceDeviceFailed.Reset();
            return mixer;
        }
        catch (Exception)
        {
            // No device (or it refused the format): release what was opened and back off before trying again.
            if (device is not null)
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_device, device))
                    {
                        _device = null;
                        _mixer = null;
                    }
                }
                device.PlaybackStopped -= Device_PlaybackStopped;
                try { device.Dispose(); } catch (Exception) { }
            }
            _sinceDeviceFailed.Restart();
            return null;
        }
    }

    private float[]? Decoded(string path)
    {
        // A file that failed is tried again once it changes (re-exported); until then it is reported again, so a
        // timeline built since still hears that it is silent.
        if (_cache.TryGet(path, out var cached))
        {
            if (cached is not null)
                return cached;
            if (_failedAt.TryGetValue(path, out var failed) && failed == StampOf(path))
            {
                Undecodable?.Invoke(path);
                return null;
            }
        }
        float[]? samples;
        try
        {
            samples = Decode(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException
                                       || (ex is IOException && ex is not EndOfStreamException))
        {
            return null; // locked or briefly missing: try again next time
        }
        catch (Exception)
        {
            samples = null; // not a format the decoder reads
        }
        _cache.Add(path, samples);
        if (samples is null)
        {
            if (_failedAt.Count >= CacheMaxEntries)
                _failedAt.Clear();
            _failedAt[path] = StampOf(path);
            Undecodable?.Invoke(path);
        }
        else
        {
            _failedAt.Remove(path);
        }
        return samples;
    }

    private static (long, DateTime) StampOf(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? (info.Length, info.LastWriteTimeUtc) : default;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return default;
        }
    }

    /// <summary>The file at the mixer's format (stereo, 44.1 kHz float), cut at <see cref="MaxSeconds"/>; null if it has more than two channels.</summary>
    private static float[]? Decode(string path)
    {
        using var reader = new WaveFileReader(path);
        ISampleProvider source = reader.ToSampleProvider();
        if (source.WaveFormat.Channels == 1)
            source = new MonoToStereoSampleProvider(source);
        if (source.WaveFormat.Channels != 2)
            return null;
        if (source.WaveFormat.SampleRate != MixFormat.SampleRate)
            source = new WdlResamplingSampleProvider(source, MixFormat.SampleRate);
        var max = MixFormat.SampleRate * 2 * MaxSeconds;
        var all = new List<float>(Math.Min(max, MixFormat.SampleRate * 2));
        var buffer = new float[MixFormat.SampleRate];
        int read;
        while (all.Count < max && (read = source.Read(buffer)) > 0)
            all.AddRange(new ArraySegment<float>(buffer, 0, Math.Min(read, max - all.Count)));
        return all.ToArray();
    }

    /// <summary>A decoded sound read once into the mixer at a fixed volume.</summary>
    private sealed class BufferProvider(float[] samples, float volume) : ISampleProvider
    {
        private int _position;

        public WaveFormat WaveFormat => MixFormat;

        public int Read(Span<float> buffer)
        {
            var n = Math.Min(buffer.Length, samples.Length - _position);
            for (var i = 0; i < n; i++)
                buffer[i] = samples[_position + i] * volume;
            _position += n;
            return n;
        }
    }
}

/// <summary>
/// Keeps the mix inside full scale. Overlapping notetrack sounds sum past ±1 (a reload's foley and mag sounds land on
/// the same frames), which the device would hard-clip. Samples under <see cref="Knee"/> pass untouched; above it a tanh
/// curve bends them towards ±1 without reaching it. It is per sample with no envelope, so quiet sounds are never ducked
/// by loud ones (no pumping), and the curve meets the straight line with the same slope, so the bend adds no click.
/// </summary>
public sealed class SoftClipSampleProvider(ISampleProvider source) : ISampleProvider
{
    public const float Knee = 0.8f;

    public WaveFormat WaveFormat => source.WaveFormat;

    public int Read(Span<float> buffer)
    {
        var n = source.Read(buffer);
        Process(buffer[..n]);
        return n;
    }

    public static void Process(Span<float> samples)
    {
        for (var i = 0; i < samples.Length; i++)
            samples[i] = Shape(samples[i]);
    }

    public static float Shape(float x)
    {
        var a = Math.Abs(x);
        if (a <= Knee || float.IsNaN(x))
            return x;
        const float room = 1 - Knee;
        // tanh never exceeds 1, so this never exceeds Knee + room = 1 (it reaches it only where float rounding says so).
        return MathF.CopySign(Knee + room * MathF.Tanh((a - Knee) / room), x);
    }
}
