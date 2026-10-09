using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Apex.Editor.Services.Extensions;
using Apex.Editor.Services.Extensions.Simulation;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Apex.Editor.ViewModels;

/// <summary>
/// One "load this preview module?" question, as the weapon preview shows it in place: the plain sentence first, then
/// the extension and the file's name, what changed when the user answered for another build, and the full path and hash
/// behind Details. Everything from the manifest or the disk is made plain (<see cref="ExtensionLoader.Displayable"/>), so
/// nothing in it reads as Apex's own words. It is answered once, by a person (Load, Don't load) or by going away (no
/// answer); a question that is closed can't be answered.
/// </summary>
public sealed partial class ModuleQuestion : ObservableObject
{
    private readonly TaskCompletionSource<bool?> _answer = new();
    private readonly Action<ModuleQuestion>? _closed;

    public ModuleQuestion(SimulatorConsentRequest request, Action<ModuleQuestion>? closed = null)
    {
        Request = request;
        _closed = closed;
        var id = ExtensionLoader.Displayable(request.ExtensionId);
        var version = ExtensionLoader.Displayable(request.ExtensionVersion).Trim();
        if (version.Length > ExtensionLoader.MaxVersionLength)
            version = version[..ExtensionLoader.MaxVersionLength];
        Title = request.Changed ? $"Load {id}'s changed preview module?" : $"Load {id}'s preview module?";
        Extension = id;
        Version = version.Length > 0 ? version : null;
        FileName = ExtensionLoader.Displayable(Path.GetFileName(request.ModulePath));
        Change = request.Changed ? ChangeOf(request) : null;
        Details = $"{ExtensionLoader.Displayable(request.ModulePath)}\nSHA-256 {request.Sha256}";
    }

    public const string Lead = "It runs inside Apex with your permissions, so load it only if you trust where the extension came from. Editing and saving work either way.";

    public SimulatorConsentRequest Request { get; }

    public string Title { get; }

    public string Extension { get; }

    public string? Version { get; }

    /// <summary>The extension and its version on one line ("weapon-tech 0.4.0").</summary>
    public string ExtensionLine => Version is null ? Extension : $"{Extension} {Version}";

    public string FileName { get; }

    /// <summary>For a module whose file differs from the one answered for: what differs. Null otherwise.</summary>
    public string? Change { get; }

    /// <summary>The full path and hash (behind Details).</summary>
    public string Details { get; }

    [ObservableProperty]
    private bool _showDetails;

    /// <summary>True (Load), false (Don't load), null (closed without an answer).</summary>
    public Task<bool?> Answer => _answer.Task;

    public bool IsOpen => !_answer.Task.IsCompleted;

    [RelayCommand]
    private void Load() => Close(true);

    [RelayCommand]
    private void DontLoad() => Close(false);

    /// <summary>Nothing waits for it any more (every preview asking closed): it goes, and nothing is remembered.</summary>
    public void Withdraw() => Close(null);

    private void Close(bool? answer)
    {
        if (!IsOpen)
            return;
        // Off the screen before the answer runs the load: a question never outlives its answer.
        _closed?.Invoke(this);
        _answer.TrySetResult(answer);
    }

    /// <summary>"Changed since you answered on 1 Oct 2026: SHA-256 1a2b3c4d (was 9f8e7d6c), 412.5 KB (+3.2 KB), modified …".</summary>
    public static string ChangeOf(SimulatorConsentRequest r)
    {
        var inv = CultureInfo.InvariantCulture;
        string When(DateTime utc) => utc.ToLocalTime().ToString("d MMM yyyy HH:mm", inv);
        string Hash(string sha) => sha.Length >= 8 ? sha[..8] : sha;
        string Kb(long bytes) => bytes < 1024 ? $"{bytes.ToString(inv)} bytes" : (bytes / 1024.0).ToString("0.0", inv) + " KB";
        var parts = new List<string>();
        if (r.Previous is not { } p)
        {
            parts.Add($"SHA-256 {Hash(r.Sha256)}");
            if (r.Size > 0)
                parts.Add(Kb(r.Size));
            if (r.ModifiedUtc is { } m)
                parts.Add($"modified {When(m)}");
            return $"The file changed while Apex was loading it: {string.Join(", ", parts)}.";
        }
        parts.Add($"SHA-256 {Hash(r.Sha256)} (was {Hash(p.Sha256)})");
        if (r.Size > 0)
        {
            var delta = p.Size is { } old ? r.Size - old : (long?)null;
            parts.Add(delta switch
            {
                null => Kb(r.Size),
                0 => $"{Kb(r.Size)} (same size)",
                > 0 => $"{Kb(r.Size)} (+{Kb(delta.Value)})",
                _ => $"{Kb(r.Size)} (−{Kb(-delta.Value)})",
            });
        }
        if (r.ModifiedUtc is { } modified)
            parts.Add(p.ModifiedUtc is { } was ? $"modified {When(modified)} (was {When(was)})" : $"modified {When(modified)}");
        return $"Changed since you answered on {p.AnsweredUtc.ToLocalTime().ToString("d MMM yyyy", inv)}: {string.Join(", ", parts)}.";
    }
}

/// <summary>
/// The app's answer to <see cref="ISimulatorConsent"/>: a module's question is asked in the weapon previews that want it
/// (<see cref="Watch"/>), in place, never as a dialog that takes the keyboard. Nothing else asks for a module today; a
/// question with no preview watching for it (the harness asking the host directly) goes to <paramref name="fallback"/>,
/// the shared confirmation dialog, so it is never asked where no one can answer. Either way a question lasts only while
/// something waits for it: withdrawn, it closes with no answer, and nothing is remembered.
/// </summary>
public sealed class ModuleQuestions(ISimulatorConsent fallback) : ISimulatorConsent
{
    private readonly List<ModuleQuestion> _open = new();
    private readonly Dictionary<string, int> _watching = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A question opened or closed.</summary>
    public event Action? Changed;

    /// <summary>The open question about <paramref name="extensionId"/>'s module, if any.</summary>
    public ModuleQuestion? For(string extensionId) => _open.Find(q => q.Request.ExtensionId.Equals(extensionId, StringComparison.OrdinalIgnoreCase));

    /// <summary>A preview that will show <paramref name="extensionId"/>'s question while it lives (dispose when it stops).</summary>
    public IDisposable Watch(string extensionId)
    {
        _watching[extensionId] = _watching.GetValueOrDefault(extensionId) + 1;
        return new Watcher(this, extensionId);
    }

    private sealed class Watcher(ModuleQuestions owner, string id) : IDisposable
    {
        private bool _done;

        public void Dispose()
        {
            if (_done)
                return;
            _done = true;
            if (--owner._watching[id] == 0)
                owner._watching.Remove(id);
        }
    }

    public Task<bool?> AskAsync(SimulatorConsentRequest request, CancellationToken withdrawn)
    {
        if (!_watching.ContainsKey(request.ExtensionId))
            return fallback.AskAsync(request, withdrawn);
        CancellationTokenRegistration registration = default;
        var question = new ModuleQuestion(request, q =>
        {
            _open.Remove(q);
            registration.Dispose();
            Changed?.Invoke();
        });
        _open.Add(question);
        registration = withdrawn.Register(question.Withdraw);
        if (question.IsOpen)
            Changed?.Invoke();
        return question.Answer;
    }
}
