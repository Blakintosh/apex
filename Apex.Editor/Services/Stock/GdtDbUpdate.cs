using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace Apex.Editor.Services.Stock;

/// <summary>
/// <c>gdtdb /update</c>: the mod tools' GDT database (what the linker reads) learns of the GDT entries a clone wrote or
/// an undo took away. Apex's own index follows the files on its own (the GDT watcher); the linker does not.
/// </summary>
public static class GdtDbUpdate
{
    /// <summary>Runs the update; the text says how it went ("" when there was nothing to run it with).</summary>
    public static async Task<(bool Ok, string Message)> RunAsync(string? bo3Root, int timeoutSeconds = 240)
    {
        if (bo3Root is null || Path.Combine(bo3Root, "gdtdb", "gdtdb.exe") is not { } exe || !File.Exists(exe))
            return (false, "gdtdb.exe isn't in the install, so run its update before linking.");
        try
        {
            var start = new ProcessStartInfo(exe, "/update")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? "",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var process = Process.Start(start);
            if (process is null)
                return (false, "gdtdb didn't start.");
            // Both pipes are drained so a chatty update can't fill one and stall.
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            using var timeout = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return (false, "gdtdb took too long. Run its update before linking.");
            }
            await Task.WhenAll(output, errors).ConfigureAwait(false);
            return process.ExitCode == 0 ? (true, "") : (false, $"gdtdb stopped with code {process.ExitCode}.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return (false, ex.Message);
        }
    }
}
