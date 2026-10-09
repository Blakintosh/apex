using System;
using Apex.Editor.Services;
using Gscode.Updates;

namespace Apex.Editor.ViewModels;

/// <summary>
/// Updates: the shared updater behind the top row's update control and the app menu's Updates, and Check for updates;
/// and About Apex.
/// </summary>
public sealed partial class MainViewModel
{
    private Updater _updates = new(AppUpdates.Options);

    /// <summary>About Apex's title: the name and the version the updater compares against.</summary>
    public static string AboutTitle { get; } = $"Apex {AppUpdates.CurrentVersion}";

    /// <summary>About Apex's one line: what Apex is.</summary>
    public const string AboutLine = "Replaces the Asset Property Editor in the Black Ops III mod tools.";

    /// <summary>The update check and the update control's state. The harness swaps in one run from a fake install.</summary>
    public Updater Updates
    {
        get => _updates;
        set => SetProperty(ref _updates, value);
    }

    /// <summary>
    /// The palette's Check for updates: the outcome goes to the status line (up to date, or why it couldn't check); a
    /// newer release shows up in the update control. The app menu's Updates shows the same outcome in its menu.
    /// </summary>
    private void CheckForUpdates() => _ = Updates.CheckAndReportAsync(ShowProgress, text => Status = text);

    /// <summary>The automatic check: at most once a day, in the background; a failure is only logged.</summary>
    public void CheckForUpdatesIfDue()
    {
        if (!Updater.IsCheckDue(_settings.LastUpdateCheckUtc))
            return;
        _settings.LastUpdateCheckUtc = DateTime.UtcNow;
        SaveSettings();
        _ = Updates.CheckAsync(userAsked: false);
    }
}
