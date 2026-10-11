using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using Apex.Editor.Models;

namespace Apex.Editor.Services;

/// <summary>
/// Tells Radiant (and APE, and any other tool on the GDT database) about Apex's changes, as APE does:
/// <list type="bullet">
/// <item>Every changed value is a line appended to <c>&lt;BO3&gt;\gdtDB\live\changes.log</c>:
/// <c>&lt;sender&gt; 0 &lt;type&gt; &lt;asset&gt; &lt;key&gt; &lt;value&gt;</c>. The sender is APE's
/// <c>%016.16llX</c> of (start time &lt;&lt; 32 | process id); readers skip their own lines and split only the first five
/// spaces, so a value may hold spaces (apeAssetDataModel 0x140071560, writer 0x140081CD0, gated by the
/// <c>EnableSync</c> setting, on by default). Radiant monitors the file (<c>gdt_db_monitor_enabled</c>).</item>
/// <item>After a save, <c>&lt;BO3&gt;\gdtdb\gdtdb.exe /update</c> brings <c>gdt.db</c>, which Radiant looks assets up in,
/// up to date with the GDTs on disk (Save All 0x140079DD0, Save 0x1400B19B0; started and not waited for).</item>
/// </list>
/// Nothing here is created: without the install's <c>gdtDB\live</c> folder or <c>gdtdb.exe</c> it does nothing.
/// UI thread only.
/// </summary>
public sealed class RadiantSync
{
    private readonly string _log;
    private readonly string _gdtdb;
    private readonly Func<AssetRecord, string, string> _valueOf;
    private readonly Dictionary<AssetRecord, Dictionary<string, string>> _sent = new();
    private Process? _update;
    private bool _updateAgain;

    /// <param name="bo3Root">The install's root folder.</param>
    /// <param name="valueOf">What a key an asset doesn't set reads as (its parent's value, else the default): a key
    /// removed from a derived asset is sent as the value it now inherits.</param>
    public RadiantSync(string bo3Root, Func<AssetRecord, string, string> valueOf)
    {
        _log = Path.Combine(bo3Root, "gdtDB", "live", "changes.log");
        _gdtdb = Path.Combine(bo3Root, "gdtdb", "gdtdb.exe");
        _valueOf = valueOf;
        Sender = SenderId(DateTimeOffset.UtcNow, Environment.ProcessId);
    }

    /// <summary>This process's sender field: APE's <c>%016.16llX</c> of (Unix seconds &lt;&lt; 32 | process id).</summary>
    public string Sender { get; }

    /// <summary>False stops both kinds of notice (the user's setting).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Lines written so far, and why the last write failed (null when it didn't).</summary>
    public int LinesWritten { get; private set; }
    public string? LastError { get; private set; }

    /// <summary>Times <c>gdtdb.exe /update</c> was started.</summary>
    public int UpdatesStarted { get; private set; }

    public static string SenderId(DateTimeOffset now, int processId) =>
        (((ulong)(uint)now.ToUnixTimeSeconds() << 32) | (uint)processId).ToString("X16");

    /// <summary>One <c>changes.log</c> line (without its newline). Line breaks in a value become spaces: a reader takes one
    /// line per change.</summary>
    public static string Line(string sender, string type, string asset, string key, string value) =>
        $"{sender} 0 {type} {asset} {key} {value.Replace('\r', ' ').Replace('\n', ' ')}";

    /// <summary>
    /// These records' values may have changed: append a line for each key whose value differs from what was last sent
    /// (the first time, from the record's value before the session's first edit of it).
    /// </summary>
    public void Changed(IEnumerable<AssetRecord> records)
    {
        if (!Enabled)
            return;
        var lines = new StringBuilder();
        foreach (var r in records)
        {
            if (!r.IsMaterialized)
                continue;
            if (!_sent.TryGetValue(r, out var sent))
                _sent[r] = sent = new Dictionary<string, string>(r.SessionBaseline ?? r.Properties, StringComparer.OrdinalIgnoreCase);
            var now = r.Properties;
            foreach (var (key, value) in now)
                if (!sent.TryGetValue(key, out var was) || was != value)
                {
                    lines.Append(Line(Sender, r.Type, r.Name, key, value)).Append('\n');
                    sent[key] = value;
                }
            List<string>? gone = null;
            foreach (var key in sent.Keys)
                if (!now.ContainsKey(key))
                    (gone ??= new()).Add(key);
            if (gone is not null)
                foreach (var key in gone)
                {
                    var value = _valueOf(r, key);
                    if (value != sent[key])
                        lines.Append(Line(Sender, r.Type, r.Name, key, value)).Append('\n');
                    sent.Remove(key);
                }
        }
        if (lines.Length > 0)
            Append(lines.ToString());
    }

    /// <summary>Forgets what was sent for <paramref name="r"/> (it was reloaded, renamed or deleted).</summary>
    public void Forget(AssetRecord r) => _sent.Remove(r);

    /// <summary>GDTs were written: bring <c>gdt.db</c> up to date. One update at a time; a save during one runs another after it.</summary>
    public void Saved()
    {
        if (!Enabled || !File.Exists(_gdtdb))
            return;
        if (_update is { HasExited: false })
        {
            _updateAgain = true;
            return;
        }
        StartUpdate();
    }

    private void StartUpdate()
    {
        _updateAgain = false;
        try
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo(_gdtdb, "/update")
                {
                    WorkingDirectory = Path.GetDirectoryName(_gdtdb)!,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
                EnableRaisingEvents = true,
            };
            process.Exited += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                process.Dispose();
                if (ReferenceEquals(_update, process))
                    _update = null;
                if (_updateAgain && Enabled)
                    StartUpdate();
            });
            process.Start();
            _update = process;
            UpdatesStarted++;
            LastError = null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            LastError = $"gdtdb.exe /update: {ex.Message}";
        }
    }

    private void Append(string text)
    {
        // The folder is the install's (APE's fopen "a+b" doesn't create it either).
        if (!Directory.Exists(Path.GetDirectoryName(_log)))
            return;
        try
        {
            using var stream = new FileStream(_log, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            var bytes = Encoding.UTF8.GetBytes(text);
            stream.Write(bytes, 0, bytes.Length);
            LinesWritten += CountLines(text);
            LastError = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LastError = $"{_log}: {ex.Message}";
        }
    }

    private static int CountLines(string text)
    {
        var n = 0;
        foreach (var c in text)
            if (c == '\n')
                n++;
        return n;
    }
}
