using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Apex.Editor.Models;
using Apex.Editor.Services.Stock;

namespace Apex.Editor.ViewModels;

/// <summary>Where the Use in map dialog is with the zone file.</summary>
public enum UseStage { Idle, ConfirmingWrite, Written, AlreadyThere, Failed }

/// <summary>
/// Use in map: a stock asset used by name, with no copy. Dragon takes it (with everything it needs) from the game's own files
/// when the map is built, so what is left to do is naming it: Apex copies the name, or for a weapon adds the zone line
/// (<c>weapon,name</c>) to the chosen map's zone file, after asking. Create local copy stays one click away.
/// </summary>
public sealed partial class StockUseViewModel : ObservableObject
{
    private readonly StockLibrary _library;
    private readonly string? _bo3Root;
    private readonly Action _close;
    private readonly Action<string> _status;
    private readonly Action _createCopy;
    private readonly Dispatcher _ui = Dispatcher.UIThread;
    private int _planGen;

    public StockUseViewModel(AssetRecord record, StockLibrary library, string? bo3Root, IReadOnlyList<CloneTarget> targets, CloneTarget? preselected,
        Action close, Action<string> status, Action createCopy)
    {
        Origin = record.Stock!;
        _library = library;
        _bo3Root = bo3Root;
        _close = close;
        _status = status;
        _createCopy = createCopy;
        Targets = targets;
        _target = preselected ?? targets.FirstOrDefault();
        IsWeapon = Origin.Kind == "weapon";
        Subtitle = Origin.Zones.Count == 1 ? $"only shipped in {Origin.Zone}.ff" : $"shipped in {Origin.Zones.Count:N0} zones, first {Origin.Zone}.ff";
        Intro = $"Name this {StockHeader.Noun(Origin.Kind)} in your map. Dragon takes it, with everything it needs, from the game's own files when you build. Nothing is copied into your map.";
        UpdateTargetState();
    }

    public StockOrigin Origin { get; }
    public string AssetName => Origin.Name;
    public string Kind => Origin.Kind;
    public string Subtitle { get; }
    public string Intro { get; }
    public bool IsWeapon { get; }
    public IReadOnlyList<CloneTarget> Targets { get; }

    /// <summary>Set by the shell: puts text on the clipboard.</summary>
    public Func<string, Task>? CopyText { get; set; }

    [ObservableProperty]
    private CloneTarget? _target;

    /// <summary>What a build with this target does with the asset, from the helper's carry walk; empty until read.</summary>
    [ObservableProperty]
    private string _carrySummary = "Reading what it needs…";

    /// <summary>Things the helper warns about for this map (the last build fell back to the stock linker, a carried asset failed).</summary>
    public System.Collections.ObjectModel.ObservableCollection<string> Warnings { get; } = new();

    public bool HasWarnings => Warnings.Count > 0;

    // ── The zone line ────────────────────────────────────────────────────────

    /// <summary>The line a weapon needs in the zone file. Carry takes <c>weapon,</c>; <c>weaponfull,</c> is the clone flow's and is not carried.</summary>
    public string ZoneLine => "weapon," + Origin.Name;

    [ObservableProperty]
    private string _zoneFile = "";

    [ObservableProperty]
    private string _zoneNote = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConfirming), nameof(CanAddLine), nameof(ShowAddLine))]
    private UseStage _stage = UseStage.Idle;

    public bool IsConfirming => Stage == UseStage.ConfirmingWrite;
    public bool CanAddLine => IsWeapon && ZoneFile.Length > 0 && Stage is UseStage.Idle or UseStage.Failed;
    public bool ShowAddLine => IsWeapon && Stage != UseStage.ConfirmingWrite;

    [ObservableProperty]
    private string _error = "";

    partial void OnZoneFileChanged(string value) => OnPropertyChanged(nameof(CanAddLine));

    partial void OnTargetChanged(CloneTarget? value)
    {
        Stage = UseStage.Idle;
        Error = "";
        UpdateTargetState();
        if (value is not null)
            Plan();
    }

    /// <summary>The zone file a map or mod keeps its asset lines in; null when there is none to write to.</summary>
    public static string? ZoneFileOf(string? bo3Root, CloneTarget? target)
    {
        if (bo3Root is null || target is null)
            return null;
        if (target.Kind != "mod")
        {
            var path = Path.Combine(bo3Root, "usermaps", target.Name, "zone_source", target.Name + ".zone");
            return File.Exists(path) ? path : null;
        }
        var dir = Path.Combine(bo3Root, "mods", target.Name, "zone_source");
        if (!Directory.Exists(dir))
            return null;
        // the zone the helper reads first for a mod: the mode's, then the shared one
        foreach (var name in new[] { "zm_mod", "mp_mod", "cp_mod", "core_mod" })
            if (File.Exists(Path.Combine(dir, name + ".zone")))
                return Path.Combine(dir, name + ".zone");
        return Directory.EnumerateFiles(dir, "*.zone", SearchOption.TopDirectoryOnly).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
    }

    /// <summary>True when the zone text already has this line (comments and case ignored).</summary>
    public static bool HasLine(string zoneText, string line)
    {
        foreach (var raw in zoneText.Split('\n'))
        {
            var t = raw;
            var slashes = t.IndexOf("//", StringComparison.Ordinal);
            if (slashes >= 0)
                t = t[..slashes];
            if (t.Trim().Equals(line, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private void UpdateTargetState()
    {
        ZoneFile = ZoneFileOf(_bo3Root, Target) ?? "";
        ZoneNote = "";
        if (!IsWeapon)
            return;
        if (Target is null)
            ZoneNote = "There is no map or mod to add it to.";
        else if (ZoneFile.Length == 0)
            ZoneNote = $"{Target.Name} has no zone file to add a line to.";
        else
        {
            try
            {
                if (HasLine(File.ReadAllText(ZoneFile), ZoneLine))
                {
                    Stage = UseStage.AlreadyThere;
                    ZoneNote = $"{Path.GetFileName(ZoneFile)} already has this line.";
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ZoneNote = "Couldn't read " + Path.GetFileName(ZoneFile) + ": " + ex.Message;
            }
        }
    }

    // ── Planning: what Dragon carries ────────────────────────────────────────

    /// <summary>Asks the helper what a build with Dragon does with this asset against the chosen map (and for that map's warnings).</summary>
    public void Begin() => Plan();

    private void Plan()
    {
        if (Target is not { } target)
        {
            CarrySummary = "";
            return;
        }
        var gen = ++_planGen;
        CarrySummary = "Reading what it needs…";
        Warnings.Clear();
        OnPropertyChanged(nameof(HasWarnings));
        _ = Task.Run(async () =>
        {
            string summary;
            List<string> warnings = new();
            try
            {
                var status = await _library.RefreshStatusAsync(target.Name).ConfigureAwait(false);
                warnings.AddRange(status.UseDirectly.Warnings);
                if (!status.UseDirectly.Ok)
                    warnings.Insert(0, "Using it directly isn't available now: " + status.UseDirectly.Note);
                var deps = await _library.DepsAsync(Origin.EngineType, Origin.Name, target.Argument, null, null, false, default, carry: true).ConfigureAwait(false);
                summary = Describe(deps);
            }
            catch (Exception ex) when (ex is StockHelperException or TimeoutException or OperationCanceledException)
            {
                summary = "Couldn't read what it needs: " + ex.Message;
            }
            _ui.Post(() =>
            {
                if (gen != _planGen)
                    return;
                CarrySummary = summary;
                Warnings.Clear();
                foreach (var w in warnings)
                    Warnings.Add(w);
                OnPropertyChanged(nameof(HasWarnings));
            });
        });
    }

    /// <summary>"Dragon carries 128 assets with it (…). 7 sounds aren't carried, so it plays without them."</summary>
    public static string Describe(StockDeps deps)
    {
        var carried = deps.Nodes.Count(n => n.Class == StockClass.Carried);
        var local = deps.Nodes.Count(n => n.Class == StockClass.Local && n.Id != deps.Root);
        var stubs = deps.Nodes.Count(n => n.Class == StockClass.UseShipped);
        var silent = deps.Nodes.Count(n => n.Class == StockClass.Unsupported && n.Type == "SOUND_ALIAS");
        var other = deps.Nodes.Count(n => n.Class is StockClass.Copy or StockClass.Missing || (n.Class == StockClass.Unsupported && n.Type != "SOUND_ALIAS"));
        var root = deps.RootNode;
        var sb = new StringBuilder();
        if (root.Class == StockClass.Local)
            sb.Append("Your map already has its own version of this asset, so that is the one a build uses.");
        else if (root.Class == StockClass.UseShipped)
            sb.Append("The game loads this one itself: the map only names it.");
        else
        {
            sb.Append(carried == 1 ? "Dragon carries 1 asset" : $"Dragon carries {carried:N0} assets");
            sb.Append(" from the game's files with it");
            if (stubs + local > 0)
                sb.Append($"; {(stubs + local):N0} more the game already has or your map defines");
            sb.Append('.');
        }
        if (silent > 0)
            sb.Append(silent == 1 ? " 1 sound isn't carried, so it plays without it." : $" {silent:N0} sounds aren't carried, so it plays without them.");
        if (other > 0)
            sb.Append($" {other:N0} asset{(other == 1 ? "" : "s")} can't be carried: use Create local copy for this one.");
        return sb.ToString();
    }

    // ── Actions ──────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task CopyNameAsync()
    {
        if (CopyText is not null)
            await CopyText(Origin.Name).ConfigureAwait(true);
        _status($"Copied {Origin.Name}. Name it in your map and Dragon carries it from the game's files.");
        _close();
    }

    /// <summary>The first step: shows the file and the line; nothing is written until <see cref="ConfirmAddLine"/>.</summary>
    [RelayCommand]
    private void AddLine()
    {
        if (!CanAddLine)
            return;
        Error = "";
        Stage = UseStage.ConfirmingWrite;
    }

    [RelayCommand]
    private void CancelAddLine() => Stage = UseStage.Idle;

    /// <summary>Appends the line to the end of the zone file (never rewrites the rest of it).</summary>
    [RelayCommand]
    private void ConfirmAddLine()
    {
        if (Stage != UseStage.ConfirmingWrite || ZoneFile.Length == 0)
            return;
        try
        {
            AppendLine(ZoneFile, ZoneLine, out var added);
            Stage = added ? UseStage.Written : UseStage.AlreadyThere;
            ZoneNote = added ? $"Added {ZoneLine} to {Path.GetFileName(ZoneFile)}." : $"{Path.GetFileName(ZoneFile)} already has this line.";
            _status(ZoneNote);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Stage = UseStage.Failed;
            Error = "Couldn't write the zone file: " + ex.Message;
        }
    }

    /// <summary>Adds <paramref name="line"/> after the last line of the zone file, in the file's own line ending; no-op when it is there.</summary>
    public static void AppendLine(string path, string line, out bool added)
    {
        var bytes = File.ReadAllBytes(path);
        var text = Encoding.UTF8.GetString(bytes);
        if (HasLine(text, line))
        {
            added = false;
            return;
        }
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var prefix = bytes.Length == 0 || bytes[^1] == (byte)'\n' ? "" : newline;
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        var add = Encoding.UTF8.GetBytes(prefix + line + newline);
        stream.Write(add, 0, add.Length);
        added = true;
    }

    [RelayCommand]
    private void CreateCopyInstead()
    {
        _close();
        _createCopy();
    }

    [RelayCommand]
    private void Close() => _close();
}
