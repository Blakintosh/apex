// In-app updates for the gscode tools. This file is shared: Blackbird and Apex keep byte-identical copies
// (Blackbird/Services/Updater.cs, Apex.Editor/Services/Updater.cs), so change both or neither. It follows
// gscode-installer/docs/release-contract.md: the release asset names, the digest check, the .old swap and the
// GSCODE_UPDATE_FEED test feed are defined there.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;

namespace Gscode.Updates;

/// <summary>What the app is and where it runs. Everything app-specific the updater needs.</summary>
public sealed record UpdaterOptions
{
    /// <summary>The release id: "blackbird" or "apex" (asset <c>&lt;id&gt;-X.Y.Z-win-x64.zip</c>).</summary>
    public required string ProductId { get; init; }
    public required string DisplayName { get; init; }
    /// <summary>"owner/repo" on GitHub.</summary>
    public required string Repository { get; init; }
    public required string CurrentVersion { get; init; }
    /// <summary>The bundle path of the app's own exe ("bin/modlauncher.exe"); it installs under the running exe's name.</summary>
    public required string ExeBundlePath { get; init; }
    /// <summary>Every bundle path this app installs, the exe included. Other existing files are never replaced.</summary>
    public required IReadOnlyList<string> BundleFiles { get; init; }
    /// <summary>The running exe. The BO3 folder is the folder above its own.</summary>
    public string ExePath { get; init; } = Environment.ProcessPath ?? "";
    /// <summary>A file to append failures to (a failed automatic check reports nothing on screen).</summary>
    public string? LogFile { get; init; }
}

public enum UpdateState { Idle, Checking, UpToDate, CheckFailed, Downloading, DownloadFailed, Ready, InstallOnClose, Installed, InstallFailed }

/// <summary>
/// Checks GitHub for a newer release, downloads and verifies its bundle, and swaps it in. One instance per app,
/// driven from the UI thread. Two things bind to it: the title bar's update control, shown only while there is an
/// update for the user, and the Updates submenu, which says how the last check went and checks again.
/// </summary>
public sealed class Updater : INotifyPropertyChanged
{
    public const string FeedVariable = "GSCODE_UPDATE_FEED";
    private const string ParentVariable = "GSCODE_UPDATE_PARENT";
    private static readonly TimeSpan CheckInterval = TimeSpan.FromDays(1);
    private static readonly TimeSpan ApiTimeout = TimeSpan.FromSeconds(15);
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    private readonly UpdaterOptions _options;
    private Release? _release;
    private string? _bundlePath;
    private string _failTitle = "";
    private string _failReason = "";
    private string _failBrief = "";
    private bool _userWatching;
    private bool _relaunchOnExit;
    private long _received;
    private long _total;

    public Updater(UpdaterOptions options)
    {
        _options = options;
        BlockedReason = FindBlockedReason(options);
        InstallNowCommand = new Command(() => _ = InstallNowAsync(), () => CanInstall && !IsInstalling);
        InstallOnCloseCommand = new Command(() => SetInstallOnClose(true), () => CanInstall && !IsInstalling);
        CancelInstallOnCloseCommand = new Command(() => SetInstallOnClose(false));
        RestartCommand = new Command(() => RestartRequested?.Invoke(this, EventArgs.Empty));
        RetryCommand = new Command(() => _ = RetryDownloadAsync());
        CheckNowCommand = new Command(() => _ = CheckAsync(userAsked: true), () => State != UpdateState.Checking);
        OpenReleaseCommand = new Command(() => OpenUrl(_release?.PageUrl));
        OpenInstallerCommand = new Command(() => OpenUrl(_release?.InstallerUrl ?? _release?.PageUrl));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised when the update is installed and the app should close (it relaunches on <see cref="OnExit"/>).</summary>
    public event EventHandler? RestartRequested;

    // ── What the control shows ──────────────────────────────────────────────

    public UpdateState State { get; private set; }

    /// <summary>The title bar control is shown only while there is an update for the user: on its way, waiting, or failed.</summary>
    public bool IsShown => State is UpdateState.Downloading or UpdateState.DownloadFailed or UpdateState.Ready
        or UpdateState.InstallOnClose or UpdateState.Installed or UpdateState.InstallFailed;

    /// <summary>An update is waiting on the user: the control takes the accent.</summary>
    public bool IsPending => State is UpdateState.Ready or UpdateState.InstallOnClose or UpdateState.Installed;

    public string ChipText => State switch
    {
        UpdateState.Downloading => "Downloading…",
        UpdateState.Ready => "Update ready",
        UpdateState.InstallOnClose => "Updates on close",
        UpdateState.Installed => "Restart to update",
        UpdateState.DownloadFailed or UpdateState.InstallFailed => "Update failed",
        _ => "",
    };

    public string Title => State switch
    {
        UpdateState.Downloading => $"Downloading {Product}…",
        UpdateState.Ready or UpdateState.InstallOnClose => $"{Product} is ready",
        UpdateState.Installed => $"{Product} is installed",
        UpdateState.DownloadFailed or UpdateState.InstallFailed => _failTitle,
        _ => "",
    };

    public string Detail => State switch
    {
        UpdateState.Downloading => _total > 0 ? $"{Megabytes(_received)} of {Megabytes(_total)} MB" : $"{Megabytes(_received)} MB",
        UpdateState.Ready => $"You have {_options.CurrentVersion}.",
        UpdateState.InstallOnClose => $"It installs when you close {_options.DisplayName}.",
        UpdateState.Installed => $"Restart {_options.DisplayName} to start using it.",
        UpdateState.DownloadFailed or UpdateState.InstallFailed => _failReason,
        _ => "",
    };

    public bool HasDetail => Detail.Length > 0;

    /// <summary>The first lines of the release notes, as plain text.</summary>
    public string Notes => State is UpdateState.Ready or UpdateState.InstallOnClose ? _release?.NotesPreview ?? "" : "";
    public bool HasNotes => Notes.Length > 0;

    public double Progress => _total > 0 ? Math.Clamp((double)_received / _total, 0, 1) : 0;
    public bool IsProgressKnown => _total > 0;

    public bool IsDownloading => State == UpdateState.Downloading;
    public bool IsUpToDate => State == UpdateState.UpToDate;
    public bool IsReady => State == UpdateState.Ready;
    public bool IsInstallOnClose => State == UpdateState.InstallOnClose;
    public bool IsInstalled => State == UpdateState.Installed;
    /// <summary>The update couldn't be downloaded or installed: the control shows a warning in place of its arrow.</summary>
    public bool IsFailed => State is UpdateState.DownloadFailed or UpdateState.InstallFailed;
    public bool ShowsArrow => !IsFailed;
    /// <summary>A failed download can be tried again from the control's flyout.</summary>
    public bool CanRetry => State == UpdateState.DownloadFailed;
    /// <summary>The flyout has a row of links and buttons under its text.</summary>
    public bool HasActions => State is UpdateState.Ready or UpdateState.InstallOnClose or UpdateState.Installed
        or UpdateState.DownloadFailed or UpdateState.InstallFailed;
    public bool HasInstallerLink => IsFailed && _release is not null;
    public string InstallerLinkText => _release?.InstallerUrl is not null ? "Download the installer" : "Open the release page";
    public bool HasReleaseLink => State is UpdateState.Ready or UpdateState.InstallOnClose && _release?.PageUrl is not null;

    // ── What the Updates submenu shows ──────────────────────────────────────

    /// <summary>
    /// The submenu's first line: the version, a check under way, how the last one went, or the update the title bar
    /// control holds. Only in that last case (<see cref="IsShown"/>) can it be clicked, to open the control's flyout.
    /// </summary>
    public string MenuStatus => State switch
    {
        UpdateState.Checking => "Checking…",
        UpdateState.UpToDate => $"Up to date · {_options.CurrentVersion}",
        UpdateState.CheckFailed => $"Couldn't check · {_failBrief}",
        UpdateState.Downloading => $"Downloading {Product}…",
        UpdateState.DownloadFailed => $"Couldn't download {Product}",
        UpdateState.Ready => $"{Product} is ready",
        UpdateState.InstallOnClose => $"{Product} installs on close",
        UpdateState.Installed => $"{Product} is installed",
        UpdateState.InstallFailed => $"Couldn't install {Product}",
        _ => $"Version {_options.CurrentVersion}",
    };

    /// <summary>The status line's glyph: the control's arrow while there is an update, a warning when something failed.</summary>
    public bool MenuShowsArrow => State is UpdateState.Downloading or UpdateState.Ready or UpdateState.InstallOnClose or UpdateState.Installed;
    public bool MenuShowsWarning => State is UpdateState.CheckFailed or UpdateState.DownloadFailed or UpdateState.InstallFailed;

    /// <summary>The whole reason a check failed, as the status line's tooltip; null otherwise.</summary>
    public string? MenuStatusTip => State == UpdateState.CheckFailed ? _failReason : null;

    /// <summary>The submenu's second item checks again. It gives way to the status line while there is an update.</summary>
    public bool ShowsCheckNow => !IsShown;
    public string CheckNowText => State == UpdateState.CheckFailed ? "Retry" : "Check now";

    /// <summary>
    /// The outcome in one line, for a status bar (the palette's Check for updates): checking, up to date, why it
    /// couldn't check, or the update the control now shows.
    /// </summary>
    public string ResultText => State switch
    {
        UpdateState.Idle => "",
        UpdateState.Checking => "Checking for updates…",
        UpdateState.UpToDate => $"You're up to date · {_options.CurrentVersion}",
        UpdateState.CheckFailed => $"{_failTitle}. {_failReason}",
        _ => MenuStatus,
    };

    /// <summary>Install is off for a copy that isn't in a Black Ops III bin folder (a dev build); this says why.</summary>
    public string? BlockedReason { get; }
    public bool CanInstall => BlockedReason is null;
    public bool IsInstallBlocked => BlockedReason is not null && State is UpdateState.Ready or UpdateState.InstallOnClose;
    public bool IsInstalling { get; private set; }

    public string CurrentVersion => _options.CurrentVersion;
    public string? LatestVersion => _release?.Version;

    public ICommand InstallNowCommand { get; }
    public ICommand InstallOnCloseCommand { get; }
    public ICommand CancelInstallOnCloseCommand { get; }
    public ICommand RestartCommand { get; }
    public ICommand RetryCommand { get; }
    public ICommand CheckNowCommand { get; }
    public ICommand OpenReleaseCommand { get; }
    public ICommand OpenInstallerCommand { get; }

    private string Product => $"{_options.DisplayName} {_release?.Version}";

    // ── Checking ────────────────────────────────────────────────────────────

    /// <summary>The automatic check runs at most once a day.</summary>
    public static bool IsCheckDue(DateTime? lastCheckUtc) =>
        lastCheckUtc is not { } last || DateTime.UtcNow - last >= CheckInterval || DateTime.UtcNow < last;

    /// <summary>
    /// Looks for a newer release and downloads it. <paramref name="userAsked"/>: the user wants to know how it went, so
    /// a failed check shows (in the Updates submenu); otherwise it is only logged. Up to date shows either way, and a
    /// newer release appears in the title bar control as it downloads.
    /// </summary>
    public async Task CheckAsync(bool userAsked)
    {
        // Something is already under way or waiting: the submenu and the control just show it.
        if (State is UpdateState.Checking or UpdateState.Downloading or UpdateState.Ready or UpdateState.InstallOnClose or UpdateState.Installed)
        {
            _userWatching |= userAsked;
            Changed();
            return;
        }

        _userWatching = userAsked;
        // What an unseen failure leaves showing: an earlier up to date is still true.
        var settled = State == UpdateState.UpToDate ? UpdateState.UpToDate : UpdateState.Idle;
        SetState(UpdateState.Checking);
        Release release;
        try
        {
            release = await FetchLatestAsync();
        }
        catch (UpdateException ex)
        {
            Log($"{(_userWatching ? "Check" : "Automatic check")} failed: {ex.Message}{(ex.InnerException is { } inner ? $" ({inner.GetType().Name}: {inner.Message})" : "")}");
            if (_userWatching)
                Fail(UpdateState.CheckFailed, ex.Title, ex.Message, ex.Brief);
            else
                SetState(settled);
            return;
        }

        if (CompareVersions(release.Version, _options.CurrentVersion) <= 0)
        {
            SetState(UpdateState.UpToDate);
            return;
        }

        _release = release;
        await DownloadReleaseAsync();
    }

    /// <summary>
    /// The palette's Check for updates: a check the user asked for, reported through <paramref name="result"/> in one
    /// line once its outcome is known (a newer release goes on downloading in the control). If the answer takes more
    /// than 150 ms, <paramref name="progress"/> says it is checking first.
    /// </summary>
    public async Task CheckAndReportAsync(Action<string> progress, Action<string> result)
    {
        var answered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(State) && State != UpdateState.Checking)
                answered.TrySetResult();
        }

        PropertyChanged += OnChanged;
        try
        {
            var run = CheckAsync(userAsked: true);
            if (State != UpdateState.Checking)
                answered.TrySetResult();
            var outcome = Task.WhenAny(answered.Task, run);
            if (await Task.WhenAny(outcome, Task.Delay(150)) != outcome)
                progress(ResultText);
            await outcome;
        }
        finally
        {
            PropertyChanged -= OnChanged;
        }
        result(ResultText);
    }

    /// <summary>
    /// The control's flyout closed: a failed download or install goes away (a downloaded bundle is kept, and the next
    /// check offers it again); a waiting update stays.
    /// </summary>
    public void Dismiss()
    {
        if (State is UpdateState.DownloadFailed or UpdateState.InstallFailed)
            SetState(UpdateState.Idle);
        else
            Changed();
    }

    private async Task<Release> FetchLatestAsync()
    {
        var feed = Environment.GetEnvironmentVariable(FeedVariable) is { Length: > 0 } custom
            ? custom
            : $"https://api.github.com/repos/{_options.Repository}/releases/latest";
        string json;
        try
        {
            var uri = new Uri(feed);
            if (uri.IsFile)
            {
                json = await File.ReadAllTextAsync(uri.LocalPath);
            }
            else
            {
                using var timeout = new CancellationTokenSource(ApiTimeout);
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.UserAgent.ParseAdd($"{_options.ProductId}/{_options.CurrentVersion}");
                request.Headers.Accept.ParseAdd("application/vnd.github+json");
                using var response = await Http.SendAsync(request, timeout.Token);
                if (response.StatusCode == HttpStatusCode.NotFound)
                    throw new UpdateException("Couldn't check for updates", "No release has been published yet.", "no release published yet");
                if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                    throw new UpdateException("Couldn't check for updates", "GitHub is limiting requests. Try again in a while.", "GitHub is limiting requests");
                if (!response.IsSuccessStatusCode)
                    throw new UpdateException("Couldn't check for updates", $"GitHub answered {(int)response.StatusCode}. Try again in a while.", $"GitHub answered {(int)response.StatusCode}");
                json = await response.Content.ReadAsStringAsync(timeout.Token);
            }
        }
        catch (OperationCanceledException ex)
        {
            throw new UpdateException("Couldn't check for updates", "GitHub didn't answer in time.", "GitHub didn't answer", ex);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or UriFormatException)
        {
            throw new UpdateException("Couldn't check for updates", "GitHub couldn't be reached. Check your connection.", "can't reach GitHub", ex);
        }

        try
        {
            return Release.Parse(json, _options);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            throw new UpdateException("Couldn't check for updates", "GitHub's answer couldn't be read.", "GitHub's answer couldn't be read", ex);
        }
    }

    // ── Downloading ─────────────────────────────────────────────────────────

    /// <summary>Downloads the newer release just found; the title bar control shows it from here on.</summary>
    private async Task DownloadReleaseAsync()
    {
        var release = _release!;
        var progress = new Progress<(long Received, long Total)>(p =>
        {
            if (State != UpdateState.Downloading)
                return;
            (_received, _total) = p;
            Changed(nameof(Detail), nameof(Progress), nameof(IsProgressKnown));
        });
        _received = 0;
        _total = release.BundleSize;
        SetState(UpdateState.Downloading);
        try
        {
            _bundlePath = await Task.Run(() => DownloadAsync(release, progress));
            SetState(UpdateState.Ready);
        }
        catch (UpdateException ex)
        {
            Log($"Download failed: {ex.Message}{(ex.InnerException is { } inner ? $" ({inner.GetType().Name}: {inner.Message})" : "")}");
            Fail(UpdateState.DownloadFailed, ex.Title, ex.Message);
        }
    }

    /// <summary>The flyout's Retry: the same release again, without checking first.</summary>
    private async Task RetryDownloadAsync()
    {
        if (State == UpdateState.DownloadFailed && _release is not null)
            await DownloadReleaseAsync();
    }

    private static string DownloadFolder(UpdaterOptions options) =>
        Path.Combine(Path.GetTempPath(), "gscode-updates", options.ProductId);

    private async Task<string> DownloadAsync(Release release, IProgress<(long, long)> progress)
    {
        if (release.Digest is null)
            throw new UpdateException("Couldn't download the update", $"{release.BundleName} has no checksum, so it can't be installed.");

        var folder = DownloadFolder(_options);
        var path = Path.Combine(folder, release.BundleName);
        // Downloaded before (the update waited, or install wasn't possible): use it if it still checks out.
        if (File.Exists(path) && HashOf(path) == release.Digest)
        {
            ValidateBundle(path);
            return path;
        }

        var part = path + ".part";
        try
        {
            Directory.CreateDirectory(folder);
            var uri = new Uri(release.BundleUrl);
            await using (var output = File.Create(part))
            {
                if (uri.IsFile)
                {
                    await using var input = File.OpenRead(uri.LocalPath);
                    await CopyAsync(input, output, input.Length, progress);
                }
                else
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                    request.Headers.UserAgent.ParseAdd($"{_options.ProductId}/{_options.CurrentVersion}");
                    using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                    if (!response.IsSuccessStatusCode)
                        throw new UpdateException("Couldn't download the update", $"GitHub answered {(int)response.StatusCode}. Try again in a while.");
                    await using var input = await response.Content.ReadAsStreamAsync();
                    await CopyAsync(input, output, response.Content.Headers.ContentLength ?? release.BundleSize, progress);
                }
            }

            if (HashOf(part) != release.Digest)
                throw new UpdateException("Couldn't download the update", "The download didn't match its checksum, so it wasn't kept.");
            File.Move(part, path, overwrite: true);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException)
        {
            throw new UpdateException("Couldn't download the update", "The download stopped. Check your connection and try again.", inner: ex);
        }
        finally
        {
            TryDelete(part);
        }

        ValidateBundle(path);
        return path;
    }

    private static async Task CopyAsync(Stream input, Stream output, long total, IProgress<(long, long)> progress)
    {
        // Reported every 1 % (or 64 KB), so the bar moves smoothly without flooding the UI thread.
        var step = Math.Max(total / 100, 65536);
        var buffer = new byte[81920];
        long received = 0, reported = 0;
        int read;
        while ((read = await input.ReadAsync(buffer)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read));
            received += read;
            if (received - reported >= step)
            {
                reported = received;
                progress.Report((received, total));
            }
        }
        progress.Report((received, total));
    }

    private static string HashOf(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    /// <summary>A bundle holds the app's exe and only relative paths inside the BO3 folder.</summary>
    private void ValidateBundle(string zipPath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            var names = zip.Entries.Where(e => e.Name.Length > 0).Select(e => Normalize(e.FullName)).ToList();
            if (names.Any(n => Path.IsPathRooted(n) || n.Split('/').Contains("..")))
                throw new UpdateException("Couldn't download the update", "The download has files outside the Black Ops III folder.");
            if (!names.Contains(Normalize(_options.ExeBundlePath), StringComparer.OrdinalIgnoreCase))
                throw new UpdateException("Couldn't download the update", $"The download doesn't contain {_options.ExeBundlePath}.");
        }
        catch (InvalidDataException ex)
        {
            throw new UpdateException("Couldn't download the update", "The download isn't a readable zip.", inner: ex);
        }
    }

    // ── Installing ──────────────────────────────────────────────────────────

    private async Task InstallNowAsync()
    {
        if (_bundlePath is null || !CanInstall || IsInstalling)
            return;
        IsInstalling = true;
        Changed();
        var bundle = _bundlePath;
        var error = await Task.Run(() => Swap(bundle));
        IsInstalling = false;
        if (error is not null)
        {
            Fail(UpdateState.InstallFailed, "Couldn't install the update", error);
            return;
        }

        _relaunchOnExit = true;
        SetState(UpdateState.Installed);
        RestartRequested?.Invoke(this, EventArgs.Empty);
    }

    private void SetInstallOnClose(bool on)
    {
        if (State is UpdateState.Ready or UpdateState.InstallOnClose && CanInstall)
            SetState(on ? UpdateState.InstallOnClose : UpdateState.Ready);
    }

    /// <summary>
    /// Call as the app exits: installs an update scheduled for close, and starts the new version after Install now.
    /// </summary>
    public void OnExit()
    {
        if (State == UpdateState.InstallOnClose && _bundlePath is not null)
        {
            if (Swap(_bundlePath) is { } error)
                Log($"Install on close failed: {error}");
            return;
        }

        if (!_relaunchOnExit)
            return;
        try
        {
            var start = new ProcessStartInfo(_options.ExePath)
            {
                UseShellExecute = false,
                WorkingDirectory = Environment.CurrentDirectory,
            };
            foreach (var arg in Environment.GetCommandLineArgs().Skip(1))
                start.ArgumentList.Add(arg);
            start.Environment[ParentVariable] = Environment.ProcessId.ToString();
            Process.Start(start)?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            Log($"Relaunch failed: {ex.Message}");
        }
    }

    /// <summary>The BO3 folder: as many folders above the exe as its bundle path is deep.</summary>
    private static string Bo3Root(UpdaterOptions options)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(options.ExePath))!;
        var depth = Normalize(options.ExeBundlePath).Count(c => c == '/');
        for (var i = 0; i < depth; i++)
            dir = Path.GetDirectoryName(dir) ?? dir;
        return dir;
    }

    /// <summary>
    /// Updating writes into the folder the app runs from, so it only happens where that is a Black Ops III bin folder
    /// (BlackOps3.exe or the Mod Tools' share folder beside it), never in a build output or a copy somewhere else.
    /// </summary>
    private static string? FindBlockedReason(UpdaterOptions options)
    {
        if (options.ExePath.Length == 0)
            return $"{options.DisplayName} can't tell where it's running from, so it can't update itself.";
        var exeFolder = Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(options.ExePath)));
        var bundleFolder = Normalize(options.ExeBundlePath).Split('/')[0];
        var root = Bo3Root(options);
        var looksLikeBo3 = File.Exists(Path.Combine(root, "BlackOps3.exe")) || Directory.Exists(Path.Combine(root, "share"));
        return string.Equals(exeFolder, bundleFolder, StringComparison.OrdinalIgnoreCase) && looksLikeBo3
            ? null
            : $"This copy isn't in a Black Ops III {bundleFolder} folder, so it can't update itself.";
    }

    /// <summary>Where each file in the bundle goes. The app's exe goes where the running exe is, under its name.</summary>
    private static string TargetOf(UpdaterOptions options, string root, string entry) =>
        string.Equals(Normalize(entry), Normalize(options.ExeBundlePath), StringComparison.OrdinalIgnoreCase)
            ? Path.GetFullPath(options.ExePath)
            : Path.GetFullPath(Path.Combine(root, Normalize(entry)));

    /// <summary>
    /// Puts the bundle's files in place: each new file is written beside its target as <c>.new</c>, then every existing
    /// file is renamed to <c>.old</c> and the new one moved in. Any failure puts every file back. Returns null, or why it
    /// failed in one line.
    /// </summary>
    private string? Swap(string zipPath)
    {
        var root = Bo3Root(_options);
        var placed = new List<(string Target, string New, bool HadOld, bool Moved)>();
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            var entries = zip.Entries.Where(e => e.Name.Length > 0).Select(e => (Entry: e, Target: TargetOf(_options, root, e.FullName))).ToList();
            foreach (var (entry, target) in entries)
            {
                if (!target.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    return "The update has files outside the Black Ops III folder. Nothing was changed.";
                var known = _options.BundleFiles.Any(f => string.Equals(Normalize(f), Normalize(entry.FullName), StringComparison.OrdinalIgnoreCase));
                if (!known && File.Exists(target))
                    return $"The update would replace {Path.GetFileName(target)}, which {_options.DisplayName} didn't install. Nothing was changed.";
            }

            foreach (var (entry, target) in entries)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var staged = target + ".new";
                entry.ExtractToFile(staged, overwrite: true);
                placed.Add((target, staged, File.Exists(target), false));
            }

            for (var i = 0; i < placed.Count; i++)
            {
                var (target, staged, hadOld, _) = placed[i];
                if (hadOld)
                {
                    if (File.Exists(target + ".old"))
                        File.Delete(target + ".old");
                    File.Move(target, target + ".old");
                }
                File.Move(staged, target);
                placed[i] = placed[i] with { Moved = true };
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            var rolledBack = RollBack(placed);
            Log($"Install failed: {ex.GetType().Name}: {ex.Message}{(rolledBack ? "" : " (some files couldn't be put back)")}");
            if (!rolledBack)
                return $"Some files couldn't be put back. Reinstall {_options.DisplayName} from the release page.";
            return ex is UnauthorizedAccessException
                ? $"{_options.DisplayName} can't write to its folder in Black Ops III. Nothing was changed."
                : $"A file was in use or couldn't be replaced. Nothing was changed.";
        }

        return null;
    }

    private static bool RollBack(List<(string Target, string New, bool HadOld, bool Moved)> placed)
    {
        var ok = true;
        foreach (var (target, staged, hadOld, moved) in Enumerable.Reverse(placed))
        {
            try
            {
                if (moved)
                    File.Delete(target);
                if (hadOld && !File.Exists(target) && File.Exists(target + ".old"))
                    File.Move(target + ".old", target);
                TryDelete(staged);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ok = false;
            }
        }
        return ok;
    }

    // ── Start-up ────────────────────────────────────────────────────────────

    /// <summary>
    /// Call first thing in Main. After Install now, waits for the version that started this one to exit (it may hold
    /// the single-instance lock), then removes the <c>.old</c> files the swap left. Never throws.
    /// </summary>
    public static void FinishPreviousUpdate(UpdaterOptions options)
    {
        try
        {
            if (Environment.GetEnvironmentVariable(ParentVariable) is { Length: > 0 } parent)
            {
                // Not passed on: tools this app starts aren't part of the update.
                Environment.SetEnvironmentVariable(ParentVariable, null);
                if (int.TryParse(parent, out var pid))
                {
                    try
                    {
                        using var process = Process.GetProcessById(pid);
                        process.WaitForExit(TimeSpan.FromSeconds(15));
                    }
                    catch (ArgumentException)
                    {
                        // Already gone.
                    }
                }
            }

            if (FindBlockedReason(options) is not null)
                return;
            var root = Bo3Root(options);
            foreach (var file in options.BundleFiles)
                TryDelete(TargetOf(options, root, file) + ".old");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // A leftover .old is harmless; the next start tries again.
        }
    }

    // ── Versions ────────────────────────────────────────────────────────────

    /// <summary>Semantic Version order: 0.9.1 &gt; 0.9.0 &gt; 0.9.0-beta.2 &gt; 0.9.0-beta.1. A leading v and build metadata are ignored.</summary>
    public static int CompareVersions(string a, string b)
    {
        var (coreA, preA) = ParseVersion(a);
        var (coreB, preB) = ParseVersion(b);
        for (var i = 0; i < 3; i++)
            if (coreA[i] != coreB[i])
                return coreA[i].CompareTo(coreB[i]);
        if (preA.Length == 0 || preB.Length == 0)
            return (preA.Length == 0).CompareTo(preB.Length == 0);
        for (var i = 0; i < Math.Min(preA.Length, preB.Length); i++)
        {
            var numA = int.TryParse(preA[i], out var na);
            var numB = int.TryParse(preB[i], out var nb);
            var order = numA && numB ? na.CompareTo(nb)
                : numA != numB ? (numA ? -1 : 1)
                : string.CompareOrdinal(preA[i], preB[i]);
            if (order != 0)
                return Math.Sign(order);
        }
        return preA.Length.CompareTo(preB.Length);
    }

    private static (int[] Core, string[] Pre) ParseVersion(string text)
    {
        var clean = text.Trim().TrimStart('v', 'V');
        var plus = clean.IndexOf('+');
        if (plus >= 0)
            clean = clean[..plus];
        var dash = clean.IndexOf('-');
        var pre = dash >= 0 ? clean[(dash + 1)..].Split('.') : [];
        var core = (dash >= 0 ? clean[..dash] : clean).Split('.');
        var numbers = new int[3];
        for (var i = 0; i < 3 && i < core.Length; i++)
            numbers[i] = int.TryParse(core[i], out var n) ? n : 0;
        return (numbers, pre);
    }

    // ── The release ─────────────────────────────────────────────────────────

    private sealed record Release(
        string Version, string PageUrl, string? NotesPreview,
        string BundleName, string BundleUrl, long BundleSize, string? Digest, string? InstallerUrl)
    {
        public static Release Parse(string json, UpdaterOptions options)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var version = root.GetProperty("tag_name").GetString()!.Trim().TrimStart('v', 'V');
            var page = Text(root, "html_url") ?? $"https://github.com/{options.Repository}/releases/latest";
            var bundleName = $"{options.ProductId}-{version}-win-x64.zip";
            JsonElement? bundle = null;
            string? installer = null;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = Text(asset, "name");
                    if (string.Equals(name, bundleName, StringComparison.OrdinalIgnoreCase))
                        bundle = asset;
                    else if (string.Equals(name, $"{options.ProductId}-setup.exe", StringComparison.OrdinalIgnoreCase))
                        installer = Text(asset, "browser_download_url");
                }
            }

            if (bundle is not { } b || Text(b, "browser_download_url") is not { } url)
            {
                // Not newer: nothing to download, so a missing bundle doesn't matter.
                if (CompareVersions(version, options.CurrentVersion) <= 0)
                    return new Release(version, page, null, bundleName, "", 0, null, installer);
                throw new UpdateException("Couldn't check for updates", $"Release {version} has no {bundleName} to install.", $"release {version} has nothing to install");
            }

            var digest = Text(b, "digest") is { } d && d.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                ? d["sha256:".Length..].Trim().ToLowerInvariant()
                : null;
            var size = b.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt64() : 0;
            return new Release(version, page, Preview(Text(root, "body")), bundleName, url, size, digest, installer);
        }

        private static string? Text(JsonElement element, string name) =>
            element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? text.Trim() : null;

        /// <summary>The release notes' first few lines, with the Markdown taken out.</summary>
        private static string? Preview(string? body)
        {
            if (string.IsNullOrWhiteSpace(body))
                return null;
            var lines = body.Replace("\r\n", "\n").Split('\n')
                .Select(l => Regex.Replace(l.Trim(), @"\[([^\]]*)\]\([^)]*\)", "$1"))
                .Select(l => l.Replace("**", "").Replace("__", "").Replace("`", ""))
                .Select(l => Regex.Replace(l, @"^(#+|[-*+]|\d+\.)\s+", m => m.Value.TrimStart().StartsWith('#') ? "" : "• "))
                .Where(l => l.Length > 0)
                .Take(4)
                .ToList();
            return lines.Count == 0 ? null : string.Join("\n", lines);
        }
    }

    private sealed class UpdateException(string title, string message, string? brief = null, Exception? inner = null) : Exception(message, inner)
    {
        public string Title { get; } = title;
        /// <summary>The reason in a few words, for the status line after "Couldn't check · ".</summary>
        public string Brief { get; } = brief ?? message;
    }

    // ── Plumbing ────────────────────────────────────────────────────────────

    private void Fail(UpdateState state, string title, string reason, string brief = "")
    {
        _failTitle = title;
        _failReason = reason;
        _failBrief = brief;
        SetState(state);
    }

    private void SetState(UpdateState state)
    {
        State = state;
        Changed();
    }

    private static readonly string[] Bound =
    [
        nameof(State), nameof(IsShown), nameof(IsPending), nameof(ChipText), nameof(Title), nameof(Detail), nameof(HasDetail),
        nameof(Notes), nameof(HasNotes), nameof(Progress), nameof(IsProgressKnown), nameof(IsDownloading),
        nameof(IsUpToDate), nameof(IsReady), nameof(IsInstallOnClose), nameof(IsInstalled), nameof(IsFailed),
        nameof(ShowsArrow), nameof(CanRetry), nameof(HasActions), nameof(HasInstallerLink), nameof(InstallerLinkText),
        nameof(HasReleaseLink), nameof(IsInstallBlocked), nameof(IsInstalling), nameof(LatestVersion),
        nameof(MenuStatus), nameof(MenuShowsArrow), nameof(MenuShowsWarning), nameof(MenuStatusTip), nameof(ShowsCheckNow), nameof(CheckNowText), nameof(ResultText),
    ];

    private void Changed(params string[] names)
    {
        foreach (var name in names.Length > 0 ? names : Bound)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        foreach (var command in new[] { InstallNowCommand, InstallOnCloseCommand, CheckNowCommand })
            ((Command)command).Refresh();
    }

    private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');

    private static string Megabytes(long bytes) => (bytes / 1048576.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left for the next start.
        }
    }

    private static void OpenUrl(string? url)
    {
        if (url is null || !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // No browser to open it with; nothing else to do.
        }
    }

    private void Log(string message)
    {
        if (_options.LogFile is not { } file)
            return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.AppendAllText(file, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {_options.DisplayName} {_options.CurrentVersion}: {message}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Logging is best-effort.
        }
    }

    private sealed class Command(Action run, Func<bool>? canRun = null) : ICommand
    {
        public event EventHandler? CanExecuteChanged;
        public bool CanExecute(object? parameter) => canRun?.Invoke() ?? true;
        public void Execute(object? parameter)
        {
            if (CanExecute(parameter))
                run();
        }
        public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
