using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Apex.Editor.Commands;
using Apex.Editor.Services;
using Apex.Editor.ViewModels;
using Apex.Editor.Views;
using Gscode.Updates;
using K = Avalonia.Input.Key;

namespace Apex.Shots;

/// <summary>
/// Updates over fake releases (GSCODE_UPDATE_FEED) and a fake Black Ops III folder where Apex runs as
/// asseteditor_modtools.exe, in all three themes: the app menu's Updates (under the Apex mark) through each outcome of Check now (the menu stays open
/// and its line follows the check), the palette's check in the status line, and the top row's update control, which
/// appears only for an update, with real clicks on its flyout's buttons and the exe swapped by Install now.
/// Blackbird.Shots shoots the same states of its sibling menu and control.
/// </summary>
public partial class Program
{
    private static void RunUpdateChecks(string outDir)
    {
        var root = NewScratch("updates");
        var bo3 = Path.Combine(root, "Call of Duty Black Ops III");
        var bin = Path.Combine(bo3, "bin");
        var feeds = Path.Combine(root, "feeds");
        Directory.CreateDirectory(bin);
        Directory.CreateDirectory(Path.Combine(bo3, "share"));
        File.WriteAllText(Path.Combine(bo3, "BlackOps3.exe"), "game");
        // Installed in place of the Asset Property Editor: the update goes to this name, never bin\Apex.exe.
        File.WriteAllText(Path.Combine(bin, "asseteditor_modtools.exe"), "OLD-EXE");
        var (oldTmp, oldTemp) = (Environment.GetEnvironmentVariable("TMP"), Environment.GetEnvironmentVariable("TEMP"));
        // Downloads go under the scratch folder, never the user's own %TEMP%\gscode-updates.
        Environment.SetEnvironmentVariable("TMP", Path.Combine(root, "temp"));
        Environment.SetEnvironmentVariable("TEMP", Path.Combine(root, "temp"));
        Directory.CreateDirectory(Path.Combine(root, "temp"));
        UpdaterOptions Options(string exe) => AppUpdates.Options with { ExePath = exe, LogFile = Path.Combine(root, "updates.log") };
        var next = NextVersion(AppUpdates.CurrentVersion);

        var app = Application.Current!;
        app.RequestedThemeVariant = ThemeVariant.Dark;
        var sessionRoot = Path.Combine(root, "session");
        var vm = new MainViewModel(sessionRoot);
        var updates = vm.Updates = new Updater(Options(Path.Combine(bin, "asseteditor_modtools.exe")));
        var window = new MainWindow { DataContext = vm, Width = 1600, Height = 1000 };
        window.Show();
        window.Activate();
        Pump(100);
        using var server = new ReleaseServer();
        var button = window.FindControl<Button>("UpdateButton")!;
        var flyout = (Flyout)button.Flyout!;
        // Drawn in the window's overlay layer so the window capture includes it.
        if (typeof(PopupFlyoutBase).GetProperty("Popup", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(flyout) is Popup popup)
            popup.ShouldUseOverlayLayer = true;

        // Every state in the three themes: Graphite, Slate and Light.
        void Shoot(string name)
        {
            foreach (var (choice, theme) in new[] { (ThemeChoice.Graphite, "graphite"), (ThemeChoice.Slate, "slate"), (ThemeChoice.Light, "light") })
            {
                vm.Theme = choice;
                Pump(60);
                Capture(window, Path.Combine(outDir, $"125-update-{name}-{theme}.png"));
            }
            vm.Theme = ThemeChoice.Graphite;
            Pump();
        }

        try
        {
            var status = window.FindControl<MenuItem>("UpdateStatusItem")!;
            var checkNow = window.FindControl<MenuItem>("CheckNowItem")!;
            var appFlyout = (MenuFlyout)AppMenuButton(window).Flyout!;
            bool MenuOpen() => appFlyout.IsOpen && window.FindControl<MenuItem>("UpdatesMenu")!.IsSubMenuOpen;

            // Before any check: the top row has nothing, and Apex > Updates says which version this is.
            Check($"updates: nothing in the top row before a check ({button.IsVisible})", !button.IsVisible);
            OpenUpdatesMenu(window);
            Check($"updates: Apex > Updates opens on the version and Check now ('{status.Header}', '{checkNow.Header}', line enabled {status.IsEnabled})",
                MenuOpen() && status.Header as string == $"Version {AppUpdates.CurrentVersion}"
                && checkNow.Header as string == "Check now" && checkNow.IsEffectivelyVisible && !status.IsEnabled);
            Shoot("menu-version");

            // Check now, with real clicks: the menu stays open and the line follows the check in place.
            server.HoldFeed();
            Environment.SetEnvironmentVariable(Updater.FeedVariable, server.Url("feed"));
            ClickInPopup(checkNow);
            WaitUntil(() => updates.State == UpdateState.Checking, 2000);
            Check($"updates: Check now keeps the menu open and says it's checking ('{status.Header}', open {MenuOpen()}, Check now enabled {checkNow.IsEffectivelyEnabled})",
                MenuOpen() && status.Header as string == "Checking…" && !checkNow.IsEffectivelyEnabled && !button.IsVisible);
            Shoot("menu-checking");
            Feed(feeds, "same", AppUpdates.CurrentVersion);
            server.Serve(File.ReadAllText(Path.Combine(feeds, "same", "latest.json")), []);
            Environment.SetEnvironmentVariable(Updater.FeedVariable, server.Url("feed"));
            WaitUntil(() => updates.State == UpdateState.UpToDate, 4000);
            Pump(60);
            Check($"updates: nothing newer: the line says up to date in place, the menu stays open, the top row stays empty ('{status.Header}', chip {button.IsVisible})",
                MenuOpen() && status.Header as string == $"Up to date · {AppUpdates.CurrentVersion}"
                && checkNow.Header as string == "Check now" && checkNow.IsEffectivelyEnabled && !button.IsVisible);
            Shoot("menu-up-to-date");

            // From the keyboard too: Enter on Check now checks, and the menu stays open.
            var keyed = false;
            void OnKeyed(object? sender, System.ComponentModel.PropertyChangedEventArgs e) =>
                keyed |= e.PropertyName == nameof(Updater.State) && updates.State == UpdateState.Checking;
            updates.PropertyChanged += OnKeyed;
            checkNow.Focus(NavigationMethod.Directional);
            checkNow.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = K.Enter, Source = checkNow });
            WaitUntil(() => keyed && updates.State == UpdateState.UpToDate, 4000);
            updates.PropertyChanged -= OnKeyed;
            Check($"updates: Enter on Check now checks again and keeps the menu open (checked {keyed}, open {MenuOpen()}, '{status.Header}')",
                keyed && MenuOpen() && status.Header as string == $"Up to date · {AppUpdates.CurrentVersion}");

            // Couldn't check: GitHub answers 404 while the repo is private. The line says why in brief, the tooltip in full.
            Environment.SetEnvironmentVariable(Updater.FeedVariable, server.Url("missing"));
            ClickInPopup(checkNow);
            WaitUntil(() => updates.State == UpdateState.CheckFailed, 4000);
            Pump(60);
            Check($"updates: a failed check reads as a calm line with Retry, and no update control ('{status.Header}', '{checkNow.Header}', tip '{ToolTip.GetTip(status)}', chip {button.IsVisible})",
                MenuOpen() && status.Header as string == "Couldn't check · no release published yet"
                && checkNow.Header as string == "Retry" && ToolTip.GetTip(status) as string == "No release has been published yet."
                && !button.IsVisible);
            Shoot("menu-couldnt-check");
            appFlyout.Hide();
            Pump(60);

            // The palette's Check for updates reports in the status line; the top row stays empty.
            Feed(feeds, "same", AppUpdates.CurrentVersion);
            vm.Registry[CommandCatalog.CheckForUpdates].Execute();
            WaitUntil(() => vm.Status == $"You're up to date · {AppUpdates.CurrentVersion}", 4000);
            Check($"updates: the palette's check says up to date in the status line ('{vm.Status}', chip {button.IsVisible})",
                vm.Status == $"You're up to date · {AppUpdates.CurrentVersion}" && !button.IsVisible && updates.State == UpdateState.UpToDate);
            Environment.SetEnvironmentVariable(Updater.FeedVariable, server.Url("missing"));
            vm.Registry[CommandCatalog.CheckForUpdates].Execute();
            WaitUntil(() => updates.State == UpdateState.CheckFailed && vm.Status.StartsWith("Couldn't", StringComparison.Ordinal), 4000);
            Check($"updates: the palette's failed check says why in the status line ('{vm.Status}', chip {button.IsVisible})",
                vm.Status == "Couldn't check for updates. No release has been published yet." && !button.IsVisible);

            // Retry finds a newer release whose bundle doesn't match its checksum: the menu closes, and the control
            // appears and stays to say the download failed, with Retry and the installer link.
            Feed(feeds, "corrupt", next, digest: new string('0', 64));
            OpenUpdatesMenu(window);
            ClickInPopup(checkNow);
            WaitUntil(() => updates.State == UpdateState.DownloadFailed, 4000);
            Pump(60);
            Check($"updates: a newer release closes the menu, and a failed download stays in the top row ({updates.State}, chip '{updates.ChipText}', menu open {appFlyout.IsOpen})",
                !appFlyout.IsOpen && button.IsVisible && updates.ChipText == "Update failed" && !flyout.IsOpen);
            Shoot("chip-failed");
            // The menu's hover delay from the pointer's last move in it (to Retry) runs out before it opens again.
            Pump(1000);
            OpenUpdatesMenu(window);
            Check($"updates: Apex > Updates names the failed download ('{status.Header}', enabled {status.IsEnabled}, Check now shown {checkNow.IsVisible})",
                status.Header as string == $"Couldn't download Apex {next}" && status.IsEnabled && !checkNow.IsVisible);
            Shoot("menu-couldnt-download");
            ClickInPopup(status);
            WaitUntil(() => flyout.IsOpen, 2000);
            Check($"updates: clicking the line closes the menu and opens the control's flyout ('{UpdateTitle(window)}', flyout {flyout.IsOpen}, menu {appFlyout.IsOpen})",
                flyout.IsOpen && !appFlyout.IsOpen && UpdateTitle(window) == "Couldn't download the update" && updates.HasInstallerLink);
            Shoot("couldnt-download");
            var retried = false;
            void OnRetry(object? sender, System.ComponentModel.PropertyChangedEventArgs e) =>
                retried |= e.PropertyName == nameof(Updater.State) && updates.State == UpdateState.Downloading;
            updates.PropertyChanged += OnRetry;
            ClickInPopup(FlyoutButton(window, "Retry"));
            WaitUntil(() => retried && updates.State == UpdateState.DownloadFailed, 4000);
            updates.PropertyChanged -= OnRetry;
            Check($"updates: the flyout's Retry downloads the same release again, without a check (downloaded again {retried}, {updates.State})",
                retried && updates.State == UpdateState.DownloadFailed);
            flyout.Hide();
            Pump(60);
            Check($"updates: closing the flyout puts a failed download away ({updates.State}, chip {button.IsVisible}, line '{updates.MenuStatus}')",
                updates.State == UpdateState.Idle && !button.IsVisible && updates.MenuStatus == $"Version {AppUpdates.CurrentVersion}");

            // Check now again, and the release is good: the control downloads it (the bundle comes halfway and waits).
            var zip = Feed(feeds, "next", next, bundleUrl: server.Url("zip"));
            server.HoldFeed();
            server.Serve(File.ReadAllText(Path.Combine(feeds, "next", "latest.json")), File.ReadAllBytes(zip));
            Environment.SetEnvironmentVariable(Updater.FeedVariable, server.Url("feed"));
            OpenUpdatesMenu(window);
            ClickInPopup(checkNow);
            WaitUntil(() => updates.State == UpdateState.Downloading && updates.Progress > 0.4, 4000);
            Pump(60);
            Check($"updates: a newer release closes the menu and shows the control downloading ({updates.State}, chip '{updates.ChipText}', menu open {appFlyout.IsOpen})",
                !appFlyout.IsOpen && button.IsVisible && updates.ChipText == "Downloading…" && !flyout.IsOpen);
            Shoot("chip-downloading");
            Pump(1000);
            OpenUpdatesMenu(window);
            Check($"updates: while it downloads the line names it and Check now steps aside ('{status.Header}', enabled {status.IsEnabled}, Check now shown {checkNow.IsVisible})",
                status.Header as string == $"Downloading Apex {next}…" && status.IsEnabled && !checkNow.IsVisible);
            Shoot("menu-downloading");
            ClickInPopup(status);
            WaitUntil(() => flyout.IsOpen, 2000);
            Check($"updates: the line opens the downloading flyout too (flyout {flyout.IsOpen}, menu {appFlyout.IsOpen})",
                flyout.IsOpen && !appFlyout.IsOpen);
            Check($"updates: downloading shows how far ({updates.Detail}, {updates.Progress:P0})",
                updates.State == UpdateState.Downloading && updates.Progress is > 0.4 and < 0.6);
            Shoot("downloading");
            server.ReleaseZip();
            WaitUntil(() => updates.State == UpdateState.Ready, 8000);
            Check($"updates: the verified download is ready ('{UpdateTitle(window)}', chip '{updates.ChipText}')",
                updates.State == UpdateState.Ready && UpdateTitle(window) == $"Apex {next} is ready" && updates.ChipText == "Update ready");
            Shoot("ready");

            ClickInPopup(FlyoutButton(window, "Install on close"));
            Check($"updates: Install on close schedules it ({updates.State}, chip '{updates.ChipText}') and writes nothing yet",
                updates.State == UpdateState.InstallOnClose && updates.ChipText == "Updates on close"
                && File.ReadAllText(Path.Combine(bin, "asseteditor_modtools.exe")) == "OLD-EXE");
            Shoot("install-on-close");
            ClickInPopup(FlyoutButton(window, "Don't install on close"));
            Check($"updates: Don't install on close takes it back ({updates.State})", updates.State == UpdateState.Ready);

            flyout.Hide();
            Pump(60);
            Check($"updates: a ready update keeps its control in the top row ({button.IsVisible})", button.IsVisible && updates.State == UpdateState.Ready);
            Shoot("chip-ready");
            OpenUpdatesMenu(window);
            Check($"updates: Apex > Updates names the ready update ('{status.Header}')",
                status.Header as string == $"Apex {next} is ready" && status.IsEnabled && !checkNow.IsVisible);
            Shoot("menu-ready");
            appFlyout.Hide();
            Pump(60);
            Click(window, button);
            Pump(120);
            Check("updates: clicking the control opens its flyout", flyout.IsOpen);

            ClickInPopup(FlyoutButton(window, "Install now"));
            WaitUntil(() => updates.State != UpdateState.Ready, 4000);
            Check($"updates: Install now puts bin/Apex.exe in as asseteditor_modtools.exe, the old one kept as .old ({updates.State})",
                updates.State == UpdateState.Installed
                && File.ReadAllText(Path.Combine(bin, "asseteditor_modtools.exe")).StartsWith("NEW-EXE")
                && File.ReadAllText(Path.Combine(bin, "asseteditor_modtools.exe.old")) == "OLD-EXE"
                && !File.Exists(Path.Combine(bin, "Apex.exe")));
            // The harness has no desktop lifetime, so nothing closes: the panel shows what it would if the close were cancelled.
            Shoot("installed");
            Updater.FinishPreviousUpdate(Options(Path.Combine(bin, "asseteditor_modtools.exe")));
            Check("updates: the next start removes asseteditor_modtools.exe.old", !File.Exists(Path.Combine(bin, "asseteditor_modtools.exe.old")));

            // Couldn't install: the bin folder can't be written (BO3 under Program Files).
            var readOnly = Path.Combine(root, "readonly", "bin");
            Directory.CreateDirectory(readOnly);
            File.WriteAllText(Path.Combine(readOnly, "..", "BlackOps3.exe"), "game");
            File.WriteAllText(Path.Combine(readOnly, "Apex.exe"), "OLD-EXE");
            var user = Environment.UserName;
            Icacls(readOnly, "/deny", $"{user}:(W,D,DC)");
            try
            {
                var blocked = ShowOtherUpdater(window, vm, Options(Path.Combine(readOnly, "Apex.exe")), feeds, "next");
                ClickInPopup(FlyoutButton(window, "Install now"));
                WaitUntil(() => blocked.State == UpdateState.InstallFailed, 4000);
                Check($"updates: a folder it can't write to says so, with the installer link ('{blocked.Title}', '{blocked.Detail}')",
                    blocked.Title == "Couldn't install the update" && blocked.HasInstallerLink
                    && File.ReadAllText(Path.Combine(readOnly, "Apex.exe")) == "OLD-EXE");
                Shoot("couldnt-install");
            }
            finally
            {
                Icacls(readOnly, "/remove:d", user);
            }

            // A dev build: the update shows, install is off and says why.
            var dev = ShowOtherUpdater(window, vm, Options(Path.Combine(AppContext.BaseDirectory, "Apex.exe")), feeds, "next");
            var install = FlyoutButton(window, "Install now");
            Check($"updates: outside a BO3 bin folder install is off and says why ('{dev.BlockedReason}', enabled {install.IsEffectivelyEnabled})",
                dev.IsInstallBlocked && !install.IsEffectivelyEnabled && ToolTip.GetTip(install) as string == dev.BlockedReason);
            Shoot("ready-dev-build");
            flyout.Hide();
            Pump(60);
        }
        finally
        {
            Environment.SetEnvironmentVariable(Updater.FeedVariable, null);
            Environment.SetEnvironmentVariable("TMP", oldTmp);
            Environment.SetEnvironmentVariable("TEMP", oldTemp);
            app.RequestedThemeVariant = ThemeVariant.Dark;
            window.Close();
        }
    }

    /// <summary>
    /// The app menu (the Apex mark), then Updates: two real clicks, each menu drawn in the window's overlay layer so the
    /// capture includes it.
    /// </summary>
    private static void OpenUpdatesMenu(Window window)
    {
        var mark = AppMenuButton(window);
        var menu = (MenuFlyout)mark.Flyout!;
        if (typeof(PopupFlyoutBase).GetProperty("Popup", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(menu) is Popup popup)
            popup.ShouldUseOverlayLayer = true;
        Click(window, mark);
        Pump(60);
        var updates = window.FindControl<MenuItem>("UpdatesMenu")!;
        if (updates.GetVisualDescendants().OfType<Popup>().FirstOrDefault() is { } sub)
            sub.ShouldUseOverlayLayer = true;
        ClickInPopup(updates);
        Pump(60);
    }

    private static string UpdateTitle(Window window) =>
        window.GetVisualDescendants().OfType<UpdatePanel>().FirstOrDefault()?.FindControl<TextBlock>("UpdateTitle")?.Text ?? "";

    private static Button FlyoutButton(Window window, string text) =>
        window.GetVisualDescendants().OfType<UpdatePanel>().SelectMany(p => p.GetVisualDescendants().OfType<Button>())
            .First(b => b.IsEffectivelyVisible && b.Content as string == text);

    /// <summary>Shows another updater's state in the open flyout (the window's own is spent once installed).</summary>
    private static Updater ShowOtherUpdater(MainWindow window, MainViewModel vm, UpdaterOptions options, string feeds, string feed)
    {
        Environment.SetEnvironmentVariable(Updater.FeedVariable, new Uri(Path.Combine(feeds, feed, "latest.json")).AbsoluteUri);
        var other = new Updater(options);
        var check = other.CheckAsync(userAsked: true);
        WaitUntil(() => check.IsCompleted, 8000);
        var button = window.FindControl<Button>("UpdateButton")!;
        if (!button.Flyout!.IsOpen)
            button.Flyout.ShowAt(button);
        Pump(120);
        window.GetVisualDescendants().OfType<UpdatePanel>().First().DataContext = other;
        Pump(120);
        return other;
    }

    private static void Icacls(string path, string verb, string who)
    {
        using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("icacls", [path, verb, who])
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        p.WaitForExit();
    }

    /// <summary>Writes a release (latest.json and its bundle) and points GSCODE_UPDATE_FEED at it. Returns the zip.</summary>
    private static string Feed(string feeds, string name, string version, string? bundleUrl = null, string? digest = null)
    {
        var folder = Path.Combine(feeds, name);
        Directory.CreateDirectory(folder);
        var zipName = $"apex-{version}-win-x64.zip";
        var zipPath = Path.Combine(folder, zipName);
        File.Delete(zipPath);
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            using var s = zip.CreateEntry("bin/Apex.exe").Open();
            s.Write(Encoding.UTF8.GetBytes("NEW-EXE"));
            // Enough bytes (that don't compress) for the download to have a middle to stop in.
            var filler = new byte[1024 * 1024];
            new Random(1).NextBytes(filler);
            s.Write(filler);
        }
        var json = JsonSerializer.Serialize(new
        {
            tag_name = "v" + version,
            html_url = $"https://github.com/Blakintosh/apex/releases/tag/v{version}",
            body = "## What's new\n\n- Compare opens beside the editor\n- Quick Open finds properties by their APE names\n"
                   + "- The xanim preview keeps its frame when you switch tabs\n- Fixes for derived weapons",
            assets = new object[]
            {
                new
                {
                    name = zipName,
                    browser_download_url = bundleUrl ?? new Uri(zipPath).AbsoluteUri,
                    size = new FileInfo(zipPath).Length,
                    digest = "sha256:" + (digest ?? Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(zipPath)))),
                },
                new { name = "apex-setup.exe", browser_download_url = $"https://github.com/Blakintosh/apex/releases/download/v{version}/apex-setup.exe", size = 1 },
            },
        });
        File.WriteAllText(Path.Combine(folder, "latest.json"), json);
        Environment.SetEnvironmentVariable(Updater.FeedVariable, new Uri(Path.Combine(folder, "latest.json")).AbsoluteUri);
        return zipPath;
    }

    private static string NextVersion(string version)
    {
        var parts = version.Split('-')[0].Split('.').Select(int.Parse).ToArray();
        return $"{parts[0]}.{parts[1]}.{parts[2] + 1}";
    }

    /// <summary>
    /// A local stand-in for GitHub that can hold its answers: /feed waits until <see cref="Serve"/>, /zip sends half the
    /// bundle and waits for <see cref="ReleaseZip"/>, anything else is 404.
    /// </summary>
    private sealed class ReleaseServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private TaskCompletionSource<(string Feed, byte[] Zip)> _content = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _zipGate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ReleaseServer()
        {
            _listener.Start();
            _ = Task.Run(AcceptAsync);
        }

        public string Url(string path) => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/{path}";
        public void HoldFeed() => _content = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Serve(string feed, byte[] zip) => _content.TrySetResult((feed, zip));
        public void ReleaseZip() => _zipGate.TrySetResult();

        private async Task AcceptAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
                catch (Exception) { return; }
                _ = Task.Run(() => HandleAsync(client));
            }
        }

        private async Task HandleAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    var buffer = new byte[4096];
                    var read = await stream.ReadAsync(buffer, _stop.Token);
                    var path = Encoding.ASCII.GetString(buffer, 0, read).Split(' ')[1];
                    if (path == "/feed")
                    {
                        var (feed, _) = await _content.Task.WaitAsync(_stop.Token);
                        await Send(stream, "200 OK", "application/json", Encoding.UTF8.GetBytes(feed));
                    }
                    else if (path == "/zip")
                    {
                        var (_, zip) = await _content.Task.WaitAsync(_stop.Token);
                        await stream.WriteAsync(Encoding.ASCII.GetBytes(
                            $"HTTP/1.1 200 OK\r\nContent-Type: application/zip\r\nContent-Length: {zip.Length}\r\nConnection: close\r\n\r\n"), _stop.Token);
                        await stream.WriteAsync(zip.AsMemory(0, zip.Length / 2), _stop.Token);
                        await stream.FlushAsync(_stop.Token);
                        await _zipGate.Task.WaitAsync(_stop.Token);
                        await stream.WriteAsync(zip.AsMemory(zip.Length / 2), _stop.Token);
                    }
                    else
                    {
                        await Send(stream, "404 Not Found", "application/json", "{\"message\":\"Not Found\"}"u8.ToArray());
                    }
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException)
                {
                    // The harness moved on.
                }
            }
        }

        private async Task Send(NetworkStream stream, string status, string type, byte[] body)
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                $"HTTP/1.1 {status}\r\nContent-Type: {type}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"), _stop.Token);
            await stream.WriteAsync(body, _stop.Token);
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
        }
    }
}
