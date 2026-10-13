using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Apex.Editor.Services.Stock;

namespace Apex.Shots;

/// <summary>
/// Shutting the helper down: <see cref="StockHelper.Dispose"/> returns at once (the UI thread never waits for the
/// helper), an idle helper is killed after the grace period, and one with a write in flight (extract, undo,
/// sound_export) is left to finish. The helper is a fake .cmd that writes a marker when it would have finished.
/// </summary>
public partial class Program
{
    private static void StockHelperShutdownChecks(string temp)
    {
        var saved = StockHelper.ShutdownGrace;
        StockHelper.ShutdownGrace = TimeSpan.FromMilliseconds(300);
        try
        {
            foreach (var writing in new[] { false, true })
            {
                var dir = Path.Combine(temp, "fake-helper-" + (writing ? "writing" : "idle"));
                Directory.CreateDirectory(dir);
                var marker = Path.Combine(dir, "finished.txt");
                var exe = Path.Combine(dir, "stocklib.cmd");
                File.WriteAllText(exe, "@echo off\r\nping -n 3 127.0.0.1 >nul\r\necho done> \"" + marker + "\"\r\n");
                var helper = StockHelper.TryStart(exe, null, null, out var error);
                if (helper is null)
                {
                    Check($"helper shutdown: the fake helper starts ({error})", false);
                    return;
                }
                Task? call = null;
                if (writing)
                    call = helper.CallAsync("extract", null, null, CancellationToken.None);
                Check($"helper shutdown: a pending {(writing ? "extract" : "nothing")} is {(writing ? "" : "not ")}a write", helper.IsWriting == writing);
                var clock = Stopwatch.StartNew();
                helper.Dispose();
                var ms = clock.ElapsedMilliseconds;
                Check($"helper shutdown ({(writing ? "writing" : "idle")}): Dispose doesn't wait for the helper ({ms} ms)", ms < 250);
                Thread.Sleep(4000);
                Check(writing
                        ? "helper shutdown: a helper that is writing is left to finish"
                        : "helper shutdown: an idle helper that doesn't exit is killed after the grace period",
                    File.Exists(marker) == writing);
                call?.ContinueWith(_ => { }, TaskScheduler.Default);
            }
        }
        finally
        {
            StockHelper.ShutdownGrace = saved;
        }

        // Cancelling a request tells the helper (protocol "cancel"); a write is never cancelled that way.
        var echoDir = Path.Combine(temp, "fake-helper-echo");
        Directory.CreateDirectory(echoDir);
        var heard = Path.Combine(echoDir, "in.txt");
        var echo = Path.Combine(echoDir, "stocklib.cmd");
        File.WriteAllText(echo, "@echo off\r\nfindstr \"^\" > \"" + heard + "\"\r\n");
        var listener = StockHelper.TryStart(echo, null, null, out _);
        if (listener is null)
        {
            Check("helper cancel: the fake helper starts", false);
            return;
        }
        using (var cts = new CancellationTokenSource())
        {
            var describe = listener.CallAsync("describe", null, null, cts.Token);
            var extract = listener.CallAsync("extract", null, null, cts.Token);
            cts.Cancel();
            describe.ContinueWith(_ => { }, TaskScheduler.Default);
            extract.ContinueWith(_ => { }, TaskScheduler.Default);
        }
        listener.Dispose();
        var deadline = Stopwatch.StartNew();
        string text = "";
        while (deadline.ElapsedMilliseconds < 5000 && !text.Contains("shutdown"))
        {
            Thread.Sleep(100);
            try
            {
                text = File.Exists(heard) ? File.ReadAllText(heard) : "";
            }
            catch (IOException)
            {
            }
        }
        Check($"helper cancel: a cancelled read sends cancel for its id, a cancelled write doesn't ({text.Replace("\r\n", " | ").Trim()})",
            text.Contains("\"method\":\"cancel\",\"params\":{\"id\":1}") && !text.Contains("\"params\":{\"id\":2}"));
    }
}
