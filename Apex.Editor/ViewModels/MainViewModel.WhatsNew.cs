using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading.Tasks;
using Apex.Editor.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Apex.Editor.ViewModels;

/// <summary>
/// What's new: the running version's GitHub release notes in a panel under the Apex mark. It opens from the app menu or
/// the palette, and by itself the first time a version starts (the notes are read from the release, never bundled).
/// </summary>
public sealed partial class MainViewModel
{
    private static readonly string RunningVersion = AppUpdates.CurrentVersion;
    private string? _whatsNewUrl;
    private bool _whatsNewLoaded;
    private int _whatsNewLoad;

    public ObservableCollection<MdBlock> WhatsNewBlocks { get; } = new();

    public static string WhatsNewTitle { get; } = $"What's new in {RunningVersion}";

    [ObservableProperty]
    private bool _isWhatsNewOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWhatsNewMessage))]
    private string? _whatsNewMessage;

    public bool HasWhatsNewMessage => !string.IsNullOrEmpty(WhatsNewMessage);

    [ObservableProperty]
    private bool _hasWhatsNewUrl;

    private void ShowWhatsNew()
    {
        IsWhatsNewOpen = true;
        _ = LoadWhatsNewAsync();
    }

    /// <summary>After the window opens: the notes open once for each version that starts (not for a build with no version).</summary>
    public void ShowWhatsNewIfUpdated()
    {
        if (RunningVersion.StartsWith("0.0.0", StringComparison.Ordinal) || _settings.LastSeenVersion == RunningVersion || IsInstallMissing)
            return;
        ShowWhatsNew();
    }

    private async Task LoadWhatsNewAsync()
    {
        if (_whatsNewLoaded)
            return;
        var load = ++_whatsNewLoad;
        WhatsNewMessage = "Loading the release notes…";
        var (notes, error) = await ReleaseNotesFeed.Fetch(RunningVersion, default);
        if (load != _whatsNewLoad)
            return;
        if (notes is null)
        {
            WhatsNewMessage = error ?? "The release notes couldn't be read.";
            return;
        }
        WhatsNewBlocks.Clear();
        foreach (var block in MarkdownBlocks.Parse(notes.Body, skipTitle: $"Apex {RunningVersion}"))
            WhatsNewBlocks.Add(block);
        _whatsNewUrl = notes.Url;
        HasWhatsNewUrl = notes.Url.StartsWith("https://", StringComparison.Ordinal);
        WhatsNewMessage = null;
        _whatsNewLoaded = true;
        // Seen once they've been shown; a failed read tries again next start.
        if (_settings.LastSeenVersion != RunningVersion)
        {
            _settings.LastSeenVersion = RunningVersion;
            SaveSettings();
        }
    }

    [RelayCommand]
    private void OpenWhatsNewRelease()
    {
        if (_whatsNewUrl is { } url && url.StartsWith("https://", StringComparison.Ordinal))
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
}
