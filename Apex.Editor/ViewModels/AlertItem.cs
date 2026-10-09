using System;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Apex.Editor.ViewModels;

/// <summary>
/// One banner: something the user must notice or act on. An error (it blocked what the user asked for) stays until it
/// is dismissed; a notice goes by itself after a few seconds, unless the pointer is resting on it.
/// </summary>
public sealed partial class AlertItem : ObservableObject
{
    /// <summary>How long a notice stays up once the pointer isn't on it.</summary>
    public static TimeSpan NoticeLifetime { get; set; } = TimeSpan.FromSeconds(8);

    private readonly Action<AlertItem> _dismiss;
    private readonly Action? _action;
    private DispatcherTimer? _expiry;

    internal AlertItem(string text, bool isError, string? actionLabel, Action? action, string? detail, Action<AlertItem> dismiss)
    {
        Text = text;
        IsError = isError;
        Detail = string.IsNullOrEmpty(detail) ? null : detail;
        _action = action;
        ActionLabel = actionLabel ?? "";
        HasAction = action is not null && ActionLabel.Length > 0;
        _dismiss = dismiss;
    }

    public string Text { get; }

    /// <summary>Error (rose, stays) vs. notice (neutral, goes by itself).</summary>
    public bool IsError { get; }

    public string ActionLabel { get; }
    public bool HasAction { get; }

    /// <summary>Technical detail (an exception or parser message) for the banner's tooltip, never its text.</summary>
    public string? Detail { get; }

    [RelayCommand]
    private void Dismiss() => _dismiss(this);

    [RelayCommand]
    private void Run()
    {
        _dismiss(this);
        _action?.Invoke();
    }

    /// <summary>Starts (or restarts) a notice's countdown. Errors have none.</summary>
    internal void StartExpiry()
    {
        if (IsError)
            return;
        if (_expiry is null)
        {
            _expiry = new DispatcherTimer { Interval = NoticeLifetime };
            _expiry.Tick += (_, _) =>
            {
                _expiry.Stop();
                _dismiss(this);
            };
        }
        _expiry.Stop();
        _expiry.Start();
    }

    /// <summary>The pointer rests on the banner (true) or left it: a notice being read doesn't go from under the reader.</summary>
    public void Hold(bool held)
    {
        if (_expiry is null)
            return;
        if (held)
            _expiry.Stop();
        else
            _expiry.Start();
    }

    internal void StopExpiry() => _expiry?.Stop();
}
