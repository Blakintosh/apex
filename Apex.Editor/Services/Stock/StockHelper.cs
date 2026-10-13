using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Apex.Editor.Services.Stock;

/// <summary>The helper answered a request with an error, or went away before it answered.</summary>
public sealed class StockHelperException : Exception
{
    public StockHelperException(string message) : base(message) { }
}

/// <summary>
/// <c>stocklib serve</c> as a child process: JSON lines over stdio (docs/protocol.md of the helper). Apex never links the
/// helper's code; everything that needs the game's fastfiles goes through here. Requests run in the order the helper takes
/// them (an <c>index</c> answers later, in the background); progress events come back on the thread that reads the pipe.
/// </summary>
public sealed class StockHelper : IDisposable
{
    private sealed class Pending
    {
        public readonly TaskCompletionSource<JsonElement> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Action<JsonElement>? OnEvent;
    }

    private readonly Process _process;
    private readonly object _write = new();
    private readonly ConcurrentDictionary<long, Pending> _pending = new();
    private readonly TaskCompletionSource<string> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly StringBuilder _stderr = new();
    private long _nextId;
    private bool _disposed;

    private StockHelper(Process process)
    {
        _process = process;
    }

    /// <summary>The helper's version, once it has said it is ready.</summary>
    public Task<string> Ready => _ready.Task;

    public bool IsRunning => !_disposed && !_process.HasExited;

    /// <summary>The tail of what the helper wrote to its error pipe (for a message when it fails).</summary>
    public string Diagnostics
    {
        get
        {
            lock (_stderr)
                return _stderr.ToString();
        }
    }

    /// <summary>Starts <c>exe serve</c> for the install. Null (with the reason) when the process can't start.</summary>
    public static StockHelper? TryStart(string exe, string? install, string? db, out string? error)
    {
        error = null;
        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(exe) ?? "",
        };
        start.ArgumentList.Add("serve");
        if (install is { Length: > 0 })
        {
            start.ArgumentList.Add("--install");
            start.ArgumentList.Add(install);
        }
        if (db is { Length: > 0 })
        {
            start.ArgumentList.Add("--db");
            start.ArgumentList.Add(db);
        }
        Process? process;
        try
        {
            process = Process.Start(start);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            error = ex.Message;
            return null;
        }
        if (process is null)
        {
            error = "the process did not start";
            return null;
        }
        var helper = new StockHelper(process);
        helper.StartReaders();
        return helper;
    }

    private void StartReaders()
    {
        _process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
                return;
            lock (_stderr)
            {
                _stderr.AppendLine(e.Data);
                if (_stderr.Length > 8192)
                    _stderr.Remove(0, _stderr.Length - 8192);
            }
        };
        _process.BeginErrorReadLine();
        new Thread(ReadLoop) { IsBackground = true, Name = "stocklib reader" }.Start();
    }

    private void ReadLoop()
    {
        try
        {
            while (_process.StandardOutput.ReadLine() is { } line)
            {
                if (line.Length == 0)
                    continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    Dispatch(doc.RootElement.Clone());
                }
                catch (JsonException)
                {
                    // A line that is not protocol (the helper keeps stdout for protocol only; this is a stray).
                }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
        }
        var gone = new StockHelperException("The stock helper stopped" + (Diagnostics.Trim() is { Length: > 0 } d ? ": " + d.Trim() : "."));
        _ready.TrySetException(gone);
        foreach (var (id, pending) in _pending)
            if (_pending.TryRemove(id, out _))
                pending.Done.TrySetException(gone);
    }

    private void Dispatch(JsonElement message)
    {
        if (!message.TryGetProperty("id", out var idElement))
        {
            if (message.TryGetProperty("event", out var e) && e.GetString() == "ready")
                _ready.TrySetResult(message.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "");
            return;
        }
        if (!idElement.TryGetInt64(out var id) || !_pending.TryGetValue(id, out var pending))
            return;
        if (message.TryGetProperty("event", out _))
        {
            pending.OnEvent?.Invoke(message);
            return;
        }
        _pending.TryRemove(id, out _);
        if (message.TryGetProperty("error", out var error))
            pending.Done.TrySetException(new StockHelperException(error.TryGetProperty("message", out var m) ? m.GetString() ?? "error" : error.ToString()));
        else
            pending.Done.TrySetResult(message.TryGetProperty("result", out var result) ? result : default);
    }

    /// <summary>
    /// Sends a request and waits for its answer. <paramref name="onEvent"/> gets the progress events the helper sends with
    /// the request's id, on the thread that reads the pipe. Cancelling stops the wait; the helper still finishes the work.
    /// </summary>
    public async Task<JsonElement> CallAsync(string method, IReadOnlyDictionary<string, object?>? args = null,
        Action<JsonElement>? onEvent = null, CancellationToken cancel = default)
    {
        if (_disposed)
            throw new StockHelperException("The stock helper is closed.");
        var id = Interlocked.Increment(ref _nextId);
        var pending = new Pending { OnEvent = onEvent };
        _pending[id] = pending;
        var request = new Dictionary<string, object?> { ["id"] = id, ["method"] = method };
        if (args is { Count: > 0 })
            request["params"] = args;
        try
        {
            lock (_write)
            {
                _process.StandardInput.WriteLine(JsonSerializer.Serialize(request));
                _process.StandardInput.Flush();
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            _pending.TryRemove(id, out _);
            throw new StockHelperException("The stock helper is not answering.");
        }
        await using var registration = cancel.Register(() => pending.Done.TrySetCanceled(cancel));
        return await pending.Done.Task.ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        try
        {
            lock (_write)
            {
                _process.StandardInput.WriteLine("{\"id\":0,\"method\":\"shutdown\"}");
                _process.StandardInput.Close();
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
        }
        try
        {
            // A refresh still running finishes first; Apex closing does not wait for it.
            if (!_process.WaitForExit(1500))
                _process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
        _process.Dispose();
    }
}
