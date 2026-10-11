using System;
using System.Collections.Generic;
using Apex.Editor.Models;
using Apex.Editor.Services;
using Avalonia.Threading;

namespace Apex.Editor.ViewModels;

/// <summary>
/// Radiant hears about Apex's changes as it hears about APE's (<see cref="RadiantSync"/>): changed values go to the
/// install's <c>gdtDB\live\changes.log</c> a moment after they are made, and a save runs <c>gdtdb.exe /update</c>.
/// Only with an install; the user's <see cref="UiSettings.SyncWithRadiant"/> turns it off, APEX_NO_RADIANT_SYNC=1 too.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>A scrub sends its last value, not every step: changes go out this long after the last one.</summary>
    private static readonly TimeSpan RadiantSyncDelay = TimeSpan.FromMilliseconds(250);

    private RadiantSync? _radiantSync;
    private readonly HashSet<AssetRecord> _radiantDirty = new();
    private DispatcherTimer? _radiantTimer;

    /// <summary>The notices this window sends (null without an install).</summary>
    public RadiantSync? RadiantSync => _radiantSync;

    /// <summary>Sync changes with Radiant and other applications (APE's EnableSync, on by default).</summary>
    public bool SyncWithRadiant
    {
        get => _settings.SyncWithRadiant;
        set
        {
            if (_settings.SyncWithRadiant == value)
                return;
            _settings.SyncWithRadiant = value;
            if (_radiantSync is not null)
                _radiantSync.Enabled = value;
            _settings.Save();
            OnPropertyChanged();
        }
    }

    /// <summary>Points the notices at this window's install (none without one).</summary>
    private void UseRadiantSync()
    {
        FlushRadiantSync();
        // APEX_NO_RADIANT_SYNC=1 (the test harness): an install's Radiant never hears of a test run's edits.
        _radiantSync = _env.IsAvailable && _env.Bo3Root is { } root && Environment.GetEnvironmentVariable("APEX_NO_RADIANT_SYNC") != "1"
            ? new RadiantSync(root, InheritedValue) { Enabled = _settings.SyncWithRadiant }
            : null;
    }

    /// <summary>These records' values changed: tell Radiant shortly.</summary>
    private void RadiantDirty(IEnumerable<AssetRecord> records)
    {
        if (_radiantSync is null || _replaying)
            return;
        foreach (var r in records)
            _radiantDirty.Add(r);
        if (_radiantTimer is null)
        {
            _radiantTimer = new DispatcherTimer { Interval = RadiantSyncDelay };
            _radiantTimer.Tick += (_, _) => FlushRadiantSync();
        }
        _radiantTimer.Stop();
        _radiantTimer.Start();
    }

    /// <summary>Sends the changes waiting for <see cref="RadiantSyncDelay"/> now.</summary>
    public void FlushRadiantSync()
    {
        _radiantTimer?.Stop();
        if (_radiantDirty.Count == 0)
            return;
        var records = new List<AssetRecord>(_radiantDirty);
        _radiantDirty.Clear();
        _radiantSync?.Changed(records.FindAll(IsLive));
    }

    /// <summary>GDTs were written: Radiant's database follows (values first, so a reader sees them before the update).</summary>
    private void RadiantSaved()
    {
        FlushRadiantSync();
        _radiantSync?.Saved();
    }

    /// <summary>What <paramref name="key"/> reads as on <paramref name="r"/> when it doesn't set it: the nearest
    /// ancestor's value, else the deffile's default.</summary>
    private string InheritedValue(AssetRecord r, string key)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { r.Name };
        for (var cur = r; cur.Parent is { } p && seen.Add(p) && FindAsset(r.Type, p) is { } parent; cur = parent)
            if (parent.Properties.TryGetValue(key, out var value))
                return value;
        return SchemaRegistry.Get(r.Type)?.Find(key)?.Default ?? "";
    }
}
