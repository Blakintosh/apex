using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Apex.Editor.Services.Extensions.Simulation;

namespace Apex.Editor.ViewModels;

/// <summary>
/// Extensions' preview-simulator modules: the host, made on first use (nothing at startup), the question asked before a
/// module first loads, and the notice when one can't be used. The weapon preview asks for a module, through
/// <see cref="Simulators"/>, and shows its question in place (<see cref="ModuleQuestions"/>).
/// </summary>
public sealed partial class MainViewModel
{
    private SimulatorHost? _simulators;
    private ModuleQuestions? _moduleQuestions;

    /// <summary>The window's preview modules. UI thread.</summary>
    public SimulatorHost Simulators
    {
        get
        {
            if (_simulators is not null)
                return _simulators;
            _simulators = new SimulatorHost(ModuleQuestions, SimulatorConsentStore.ForUser(),
                // A crash inside a module can't be caught: the journal goes to disk before a load and before every create.
                beforeNativeCall: () => FlushSessionToDisk());
            _simulators.Reported += (d, detail) =>
                Alert($"The {d.Extension} extension: {d.Message} Editing and saving work as usual.", isError: false, detail: detail);
            return _simulators;
        }
    }

    /// <summary>Module questions, asked in the weapon previews that want them.</summary>
    public ModuleQuestions ModuleQuestions => _moduleQuestions ??= new ModuleQuestions(new ConsentPrompt(this));

    /// <summary>Closes the confirmation if it is still the question <paramref name="onCancel"/> was asked with.</summary>
    private void WithdrawConfirm(Action<bool> onCancel)
    {
        if (!IsConfirmOpen || !ReferenceEquals(_confirmCancel, onCancel))
            return;
        _confirmAction = null;
        _confirmCancel = null;
        IsConfirmOpen = false;
    }

    /// <summary>
    /// A question no weapon preview is there to show (nothing in the app asks that way; the harness asks the host
    /// directly): the shared confirmation dialog, asked the moment no other dialog is open. It opens on "Don't load",
    /// which Esc and a click outside also choose. Withdrawn (every asker gone), it closes, or is never shown, and the
    /// answer is none.
    /// </summary>
    private sealed class ConsentPrompt(MainViewModel vm) : ISimulatorConsent
    {
        public Task<bool?> AskAsync(SimulatorConsentRequest request, CancellationToken withdrawn)
        {
            var answer = new TaskCompletionSource<bool?>();
            // Replaced by another question is no answer: nothing is remembered.
            Action<bool> onCancel = replaced => answer.TrySetResult(replaced ? null : false);
            void Closed(object? sender, PropertyChangedEventArgs e)
            {
                if (e.PropertyName != nameof(IsModalOpen) || vm.IsModalOpen)
                    return;
                vm.PropertyChanged -= Closed;
                Ask();
            }
            var registration = withdrawn.Register(() =>
            {
                vm.PropertyChanged -= Closed;
                vm.WithdrawConfirm(onCancel);
                answer.TrySetResult(null);
            });
            answer.Task.ContinueWith(_ => registration.Dispose(), TaskScheduler.Default);
            if (answer.Task.IsCompleted)
                return answer.Task;
            if (!vm.IsModalOpen)
            {
                Ask();
                return answer.Task;
            }
            // Another dialog is up: ask when it closes, never on top of it.
            vm.PropertyChanged += Closed;
            return answer.Task;

            void Ask()
            {
                // The same words as the preview's own question, everything from the manifest or the disk on a line of
                // its own and made plain, so nothing it holds can read as Apex's own words.
                var q = new ModuleQuestion(request);
                vm.AskConfirm(q.Title,
                    $"{ModuleQuestion.Lead}\n\nExtension: {q.Extension}\n{(q.Version is { } v ? $"Version: {v}\n" : "")}File: {q.FileName}"
                    + (q.Change is { } change ? $"\n{change}" : ""),
                    "Load",
                    () => answer.TrySetResult(true),
                    destructive: false,
                    onCancel: onCancel,
                    cancelLabel: "Don't load");
            }
        }
    }
}
