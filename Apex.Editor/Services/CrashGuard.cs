using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace Apex.Editor.Services;

/// <summary>
/// The app's last line of defence. Every exception nothing else caught lands here: it is written to a crash log under
/// <c>%LOCALAPPDATA%\Apex\logs\</c>, the session journal is flushed, and when the exception came out of a UI-thread
/// handler (a click, a timer, a posted continuation) Apex keeps running and the session shows one plain line instead
/// of dying. A fault in the runtime itself, or on a background thread, still ends the process, but only after the
/// journal is on disk.
/// </summary>
public static class CrashGuard
{
    private static int _installed;
    private static int _logged;
    private const int MaxLogsPerRun = 50;
    private const int LogsKept = 30;

    /// <summary>Where crash logs go. <c>APEX_LOG_DIR</c> overrides it (tests).</summary>
    public static string LogDirectory =>
        Environment.GetEnvironmentVariable("APEX_LOG_DIR") is { Length: > 0 } dir
            ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Apex", "logs");

    /// <summary>
    /// Set by whoever owns the session: persists every change to disk now. Called on the thread the exception was
    /// raised on; <c>onUiThread</c> says whether in-memory state may be read to capture edits still waiting for the
    /// journal's coalescing delay.
    /// </summary>
    public static Action<bool>? FlushSession { get; set; }

    /// <summary>Set by the UI: Apex recovered from <paramref name="error"/>; tell the user once, plainly.</summary>
    public static Action<Exception, string?>? Recovered { get; set; }

    /// <summary>The most recent crash log written this run (tests, the "Show log" action).</summary>
    public static string? LastLogPath { get; private set; }

    /// <summary>Whether <see cref="Install"/> has run (the startup handler steps aside once it has).</summary>
    public static bool IsInstalled => Volatile.Read(ref _installed) == 1;

    /// <summary>Hooks the three global handlers. Safe to call more than once.</summary>
    public static void Install()
    {
        if (Interlocked.Exchange(ref _installed, 1) == 1)
            return;
        Dispatcher.UIThread.UnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTask;
    }

    private static void OnDispatcherException(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var recoverable = IsRecoverable(e.Exception);
        var log = WriteLog(e.Exception, recoverable ? "ui handler, recovered" : "ui handler, fatal");
        SafeFlush(onUiThread: true);
        if (!recoverable)
            return;
        e.Handled = true;
        try { Recovered?.Invoke(e.Exception, log); }
        catch (Exception) { /* the notice itself failing must not take the app down */ }
    }

    private static void OnDomainException(object? sender, UnhandledExceptionEventArgs e)
    {
        // The process is going down: record why and get the journal onto disk. Only the UI thread may read
        // session state; anywhere else the journal flushes what it already has.
        var log = WriteLog(e.ExceptionObject as Exception, "fatal");
        SafeFlush(onUiThread: Dispatcher.UIThread.CheckAccess());
        TellUser(log, started: true);
    }

    /// <summary>
    /// Apex failed before its window could open (or before these handlers were hooked): log it and say so, so the
    /// app never just fails to appear.
    /// </summary>
    public static void StartupFailed(Exception ex) => TellUser(WriteLog(ex, "startup, fatal"), started: false);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);

    private const uint MbIconError = 0x10;
    private const uint MbTaskModal = 0x2000;

    /// <summary>The window is about to vanish: one native message box saying what was kept and where the log is.</summary>
    private static void TellUser(string? log, bool started)
    {
        try
        {
            var what = started
                ? "Apex ran into a problem it can't recover from and has to close. Your unsaved changes were kept and come back the next time you open Apex."
                : "Apex couldn't start.";
            var where = log is null ? $"Crash logs are in {LogDirectory}." : $"The details are in:\n{log}";
            MessageBoxW(IntPtr.Zero, $"{what}\n\n{where}", "Apex", MbIconError | MbTaskModal);
        }
        catch (Exception) { /* nothing left to tell the user with */ }
    }

    private static void OnUnobservedTask(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        // A background task nobody awaited faulted; the work it did is lost but nothing else is. Log it and move on.
        WriteLog(e.Exception, "unobserved task");
        e.SetObserved();
    }

    /// <summary>
    /// Whether the app can carry on after <paramref name="ex"/>. Runtime corruption (out of memory, bad IL, a torn
    /// native call) is not recoverable; an ordinary bug in a handler is: the handler's work is lost, the app's
    /// state is still consistent enough to keep editing and to save the journal.
    /// </summary>
    public static bool IsRecoverable(Exception ex) => ex switch
    {
        OutOfMemoryException or InsufficientExecutionStackException or AccessViolationException
            or InvalidProgramException or BadImageFormatException or TypeInitializationException => false,
        AggregateException agg => agg.InnerExceptions.All(IsRecoverable),
        _ => true,
    };

    private static void SafeFlush(bool onUiThread)
    {
        try { FlushSession?.Invoke(onUiThread); }
        catch (Exception ex) { WriteLog(ex, "flushing the session after a crash"); }
    }

    /// <summary>Writes one crash log and returns its path (null when logging failed or the per-run cap was hit).</summary>
    public static string? WriteLog(Exception? ex, string context)
    {
        if (Interlocked.Increment(ref _logged) > MaxLogsPerRun)
            return null;
        try
        {
            var dir = LogDirectory;
            Directory.CreateDirectory(dir);
            var now = DateTime.Now;
            var path = Path.Combine(dir, $"crash-{now:yyyyMMdd-HHmmss-fff}-{Environment.ProcessId}.log");
            var text =
                $"Apex {typeof(CrashGuard).Assembly.GetName().Version} · {now:yyyy-MM-dd HH:mm:ss.fff} · {context}{Environment.NewLine}"
                + $"{Environment.OSVersion} · .NET {Environment.Version} · thread {Environment.CurrentManagedThreadId}"
                + $"{(Thread.CurrentThread.Name is { } n ? " (" + n + ")" : "")}{Environment.NewLine}{Environment.NewLine}"
                + (ex?.ToString() ?? "(no exception object)") + Environment.NewLine;
            File.WriteAllText(path, text);
            LastLogPath = path;
            PruneLogs(dir);
            return path;
        }
        catch (Exception)
        {
            return null; // nowhere to report a failure to report
        }
    }

    private static void PruneLogs(string dir)
    {
        var old = new DirectoryInfo(dir).GetFiles("crash-*.log")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Skip(LogsKept);
        foreach (var f in old)
        {
            try { f.Delete(); }
            catch (IOException) { }
        }
    }
}
