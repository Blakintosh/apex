using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Apex.Editor.Services;

/// <summary>
/// Hands <c>asset://</c> links to the Apex that is already running. Radiant starts a new process for every link it
/// opens; that process sends its links down a pipe (this user, this Windows session) and exits, so one click doesn't
/// mean one more window. Plain starts of Apex (no link) are unaffected and still open their own window.
/// </summary>
public static class LinkInstance
{
    private const int ConnectTimeoutMs = 400;
    private const int ASFW_ANY = -1;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int processId);

    /// <summary>The pipe a running Apex listens on (per user and per Windows session).</summary>
    public static string PipeName => $"Apex.Links.{Environment.UserName}.{Process.GetCurrentProcess().SessionId}";

    /// <summary>
    /// Sends <paramref name="links"/> to a running Apex. True when one took them (this process should then exit); false
    /// when none is listening.
    /// </summary>
    public static bool TryForward(IReadOnlyList<string> links, string? pipeName = null)
    {
        if (links.Count == 0)
            return false;
        try
        {
            using var pipe = new NamedPipeClientStream(".", pipeName ?? PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            pipe.Connect(ConnectTimeoutMs);
            // This process got the click; let the running one come to the front with the asset.
            if (OperatingSystem.IsWindows())
                AllowSetForegroundWindow(ASFW_ANY);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false));
            foreach (var link in links)
                writer.WriteLine(link.Replace('\r', ' ').Replace('\n', ' '));
            writer.Flush();
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Listens for links from later starts until <paramref name="stop"/> is cancelled; <paramref name="received"/> runs
    /// on a pool thread with each link. False when the pipe could not be created (another Apex of this user owns it).
    /// </summary>
    public static bool Listen(Action<string> received, CancellationToken stop, string? pipeName = null)
    {
        var name = pipeName ?? PipeName;
        // One owner per pipe name: the first Apex keeps it for its lifetime; later plain starts don't listen.
        var owner = new Mutex(initiallyOwned: false, @"Local\" + name);
        try
        {
            if (!owner.WaitOne(0))
            {
                owner.Dispose();
                return false;
            }
        }
        catch (AbandonedMutexException)
        {
            // The previous owner crashed: the name is free again.
        }
        NamedPipeServerStream first;
        try
        {
            first = Create(name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            owner.ReleaseMutex();
            owner.Dispose();
            return false;
        }
        _ = Task.Run(async () =>
        {
            var pipe = first;
            while (!stop.IsCancellationRequested)
            {
                NamedPipeServerStream? next = null;
                try
                {
                    await pipe.WaitForConnectionAsync(stop);
                    // The next instance waits before this one is read, so a sender never finds no listener.
                    next = Create(name);
                    using var reader = new StreamReader(pipe, Encoding.UTF8);
                    // A sender writes a few short lines and closes; cap what one may send.
                    for (int i = 0; i < 64 && await reader.ReadLineAsync(stop) is { } line; i++)
                        if (line.Length > 0)
                            received(line);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (IOException)
                {
                    // The sender went away mid-message: wait for the next one.
                }
                finally
                {
                    pipe.Dispose();
                }
                try
                {
                    pipe = next ?? Create(name);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    break;
                }
            }
            GC.KeepAlive(owner);
        }, CancellationToken.None);
        return true;
    }

    private static NamedPipeServerStream Create(string name) =>
        new(name, PipeDirection.In, 2, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
}
