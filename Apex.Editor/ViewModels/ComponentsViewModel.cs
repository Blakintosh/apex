using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Apex.Editor.Services.Stock;

namespace Apex.Editor.ViewModels;

/// <summary>How a component stands in the Components list.</summary>
public enum ComponentState { Ok, Warn, Off }

/// <summary>One row of the Components view: a Mod Tools component, how it stands, and what to do about it.</summary>
public sealed class ComponentRow
{
    public ComponentRow(ComponentState state, string name, string status, string detail, string action = "", ICommand? command = null, bool primary = false)
    {
        State = state;
        Name = name;
        Status = status;
        Detail = detail;
        Action = action;
        Command = command;
        IsPrimary = primary && action.Length > 0;
        IsLink = !primary && action.Length > 0;
    }

    public ComponentState State { get; }
    public string Name { get; }
    public string Status { get; }
    public string Detail { get; }
    public string Action { get; }
    public ICommand? Command { get; }
    public bool IsPrimary { get; }
    public bool IsLink { get; }
    public bool IsOk => State == ComponentState.Ok;
    public bool IsWarn => State == ComponentState.Warn;
    public bool IsOff => State == ComponentState.Off;
}

/// <summary>
/// Components: which Mod Tools components this install has and how they stand, from the helper's <c>status</c> (the per-install
/// component registry checked against the disk). Every Mod Tools app shows the same list; the heading says whether stock
/// assets can be used directly.
/// </summary>
public sealed partial class ComponentsViewModel : ObservableObject
{
    private readonly StockLibrary _library;
    private readonly string _apexVersion;
    private readonly Action _close;
    private readonly Action _installHelper;
    private readonly Action _buildIndex;

    public ComponentsViewModel(StockLibrary library, string install, string apexVersion, Action close, Action installHelper, Action buildIndex)
    {
        _library = library;
        _apexVersion = apexVersion;
        _close = close;
        _installHelper = installHelper;
        _buildIndex = buildIndex;
        Install = install.Length > 0 ? install : library.Status.Install;
        Update();
    }

    public string Install { get; }

    public System.Collections.ObjectModel.ObservableCollection<ComponentRow> Rows { get; } = new();

    /// <summary>"Use directly is on" / "Use directly is off": the pill beside the title.</summary>
    [ObservableProperty]
    private string _pill = "";

    [ObservableProperty]
    private bool _pillOn;

    [ObservableProperty]
    private bool _isChecking;

    /// <summary>Rebuilds the rows from the library's current status.</summary>
    public void Update()
    {
        var rows = Build(_library, _apexVersion, _installHelper, _buildIndex);
        Rows.Clear();
        foreach (var r in rows)
            Rows.Add(r);
        var on = _library.Status.UseDirectly.Ok;
        PillOn = on;
        Pill = on ? "Use directly is on" : "Use directly is off";
    }

    [RelayCommand]
    private async System.Threading.Tasks.Task RefreshAsync()
    {
        IsChecking = true;
        try
        {
            await _library.RefreshStatusAsync().ConfigureAwait(true);
        }
        finally
        {
            IsChecking = false;
        }
        Update();
    }

    [RelayCommand]
    private void Close() => _close();

    /// <summary>The rows, from what the helper reported. Public so the harness can check the words.</summary>
    public static IReadOnlyList<ComponentRow> Build(StockLibrary library, string apexVersion, Action installHelper, Action buildIndex)
    {
        var s = library.Status;
        var rows = new List<ComponentRow>
        {
            new(ComponentState.Ok, "Apex", $"{apexVersion} · this app", "Edit GDT assets with live previews."),
        };

        // Stock Library: the helper and its index
        if (library.ExePath is null && library.Backend is null)
            rows.Add(new(ComponentState.Off, "Stock Library", "helper not installed", "Search the game's own assets next to yours, preview them and copy them into a map. It needs the BO3 Mod Tools stock helper (stocklib.exe).", "Install", new RelayCommand(installHelper), primary: true));
        else if (!s.Browse.Ok && s.Known)
            rows.Add(new(ComponentState.Warn, "Stock Library", s.StocklibVersion.Length > 0 ? s.StocklibVersion + " · no usable index" : "no usable index", s.Browse.Note, library.IsRefreshing ? "" : "Build index", new RelayCommand(buildIndex), primary: true));
        else if (!s.Known)
            rows.Add(new(ComponentState.Off, "Stock Library", library.Db is { Exists: true } ? "checking" : "no index yet", library.Db is { Exists: true } ? "Asking the helper how the install stands…" : "The index is built the first time Apex starts with the helper installed.", library.IsRefreshing || library.Db is { Exists: true } ? "" : "Build index", new RelayCommand(buildIndex), primary: true));
        else
        {
            var version = s.StocklibVersion.Length > 0 ? s.StocklibVersion + " · " : "";
            rows.Add(new(ComponentState.Ok, "Stock Library", $"{version}{s.Assets:N0} assets", "Stock assets in search, previews and Create local copy."));
        }

        // Dragon
        if (!s.Known)
            rows.Add(new(ComponentState.Off, "Dragon", "unknown", "Needs the Stock Library helper to look."));
        else if (!s.DragonPresent)
            rows.Add(new(ComponentState.Off, "Dragon", "not installed", s.UseDirectly.Note.Length > 0 ? s.UseDirectly.Note : "The linker that can take stock assets straight from the game's files."));
        else if (s.UseDirectly.Ok)
            rows.Add(new(ComponentState.Ok, "Dragon", $"{(s.DragonVersion.Length > 0 ? s.DragonVersion + " · " : "installed · ")}{string.Join(", ", s.DragonCapabilities)}",
                "Blackbird links with Dragon, so stock assets can be used directly with no copies." + (s.UseDirectly.Warnings.Count > 0 ? " " + s.UseDirectly.Warnings[0] : "")));
        else
            rows.Add(new(ComponentState.Warn, "Dragon", s.DragonVersion.Length > 0 ? s.DragonVersion + " · installed" : "installed", s.UseDirectly.Note));

        // Blackbird
        if (!s.Known)
            rows.Add(new(ComponentState.Off, "Blackbird", "unknown", "Needs the Stock Library helper to look."));
        else if (s.BlackbirdVersion.Length > 0)
            rows.Add(new(ComponentState.Ok, "Blackbird", s.BlackbirdVersion, "Builds, runs and publishes your mods."));
        else if (s.BlackbirdSettingsFound)
            rows.Add(new(ComponentState.Ok, "Blackbird", "installed", "Builds, runs and publishes your mods. This Blackbird doesn't report to the component list yet; Apex goes by its settings."));
        else
            rows.Add(new(ComponentState.Off, "Blackbird", "not installed", "Builds, runs and publishes your mods."));

        // Radiant
        if (!s.Known)
            rows.Add(new(ComponentState.Off, "Radiant integration", "unknown", "Needs the Stock Library helper to look."));
        else if (s.Radiant.Ok)
            rows.Add(new(ComponentState.Ok, "Radiant integration", "StockLibrary plugin", "Stock models in Radiant's model browser."));
        else
            rows.Add(new(ComponentState.Warn, "Radiant integration", s.RadiantPluginPresent ? "plugin installed" : "plugin not found", s.Radiant.Note));

        // Everything else that wrote an entry (Ultrasound, ...)
        var ultrasound = s.Others.FirstOrDefault(o => o.Id.Equals("ultrasound", StringComparison.OrdinalIgnoreCase));
        if (ultrasound is not null)
            rows.Add(new(ComponentState.Ok, "Ultrasound", ultrasound.Version.Length > 0 ? ultrasound.Version : "installed", "Replaces the Mod Tools sound converter."));
        else if (s.Known)
            rows.Add(new(ComponentState.Off, "Ultrasound", "not installed", "Replaces the Mod Tools sound converter."));
        foreach (var o in s.Others.Where(o => !o.Id.Equals("ultrasound", StringComparison.OrdinalIgnoreCase)))
            rows.Add(new(ComponentState.Ok, o.Id, o.Version.Length > 0 ? o.Version : "installed", ""));
        return rows;
    }
}
