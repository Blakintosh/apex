using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Apex.Editor.Services.Preview.Notetracks;

namespace Apex.Editor.Services.Stock;

/// <summary>
/// Hearing a stock sound: the helper writes one variant to a temp wav (<c>sound_export</c>, decoded to 16 bit) and the
/// notetrack audio output, the one Apex already plays notetrack sounds through (NAudio, shared-mode WASAPI), plays it. A
/// variant exported once is kept for the session, so playing it again starts at once. The output cuts a sound at 20 s.
/// </summary>
public sealed class StockSoundPreview : IDisposable
{
    /// <summary>The longest the output plays one sound (it decodes no more than this).</summary>
    public const int MaxSeconds = 20;

    private readonly IStockBackend _backend;
    private readonly Func<INotetrackAudio> _audioFactory;
    private readonly string _folder;
    private INotetrackAudio? _audio;

    /// <summary>The output could not decode a file it was given (more than two channels): nothing plays.</summary>
    public event Action<string>? Undecodable;

    public StockSoundPreview(IStockBackend backend, Func<INotetrackAudio>? audioFactory = null, string? folder = null)
    {
        _backend = backend;
        _audioFactory = audioFactory ?? (() => new NAudioNotetrackAudio());
        _folder = folder ?? Path.Combine(Path.GetTempPath(), "apex-stock-sounds", Environment.ProcessId.ToString());
    }

    /// <summary>The wav of a variant, exported now unless an earlier play already did.</summary>
    public async Task<string> ExportAsync(string name, string? zone, int variant, CancellationToken cancel = default)
    {
        Directory.CreateDirectory(_folder);
        var path = Path.Combine(_folder, $"{StockSoundCopyPlan.SafeName(name)}_{StockSoundCopyPlan.SafeName(zone ?? "any")}_{variant}.wav");
        if (File.Exists(path) && new FileInfo(path).Length > 44)
            return path;
        var export = await _backend.SoundExportAsync(name, zone, variant, "wav", path, cancel).ConfigureAwait(false);
        var written = export.Path.Length > 0 ? export.Path : path;
        if (!File.Exists(written))
            throw new StockHelperException("The helper did not write the sound.");
        if (!string.Equals(Path.GetFullPath(written), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
            File.Move(written, path, overwrite: true);
        return path;
    }

    /// <summary>Starts a file playing (stopping any other first). Returns at once.</summary>
    public void Play(string path)
    {
        if (_audio is null)
        {
            _audio = _audioFactory();
            _audio.Undecodable += p => Undecodable?.Invoke(p);
        }
        _audio.StopAll();
        _audio.Play(path, 1f);
    }

    public void Stop() => _audio?.StopAll();

    public void Dispose()
    {
        _audio?.Dispose();
        _audio = null;
        try
        {
            if (Directory.Exists(_folder))
                Directory.Delete(_folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A file the output still holds; the temp folder is the system's to clean.
        }
    }
}
