using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Apex.Editor.Services.Components;
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
        var rows = Build(_library, _apexVersion, _installHelper, _buildIndex, Install);
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

    /// <summary>The rows, from the registry and what the helper reported. Public so the harness can check the words.</summary>
    public static IReadOnlyList<ComponentRow> Build(StockLibrary library, string apexVersion, Action installHelper, Action buildIndex, string? install = null)
    {
        var s = library.Status;
        var registry = ComponentRegistry.List(install);
        RegisteredComponent? Entry(string id) => registry.FirstOrDefault(r => r.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
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
            rows.Add(new(ComponentState.Off, "Stock Library", library.Db is { Exists: true } ? "checking" : "no index yet", library.Db is { Exists: true } ? "Asking the helper how the install stands…" : "Built once, when you ask or first open a stock view (about 1-3 minutes).", library.IsRefreshing || library.Db is { Exists: true } ? "" : "Build index", new RelayCommand(buildIndex), primary: true));
        else
        {
            var version = s.StocklibVersion.Length > 0 ? s.StocklibVersion + " · " : "";
            rows.Add(new(ComponentState.Ok, "Stock Library", $"{version}{s.Assets:N0} assets", "Stock assets in search, previews and Create local copy."));
        }

        // Dragon: the helper's verdict when it answered, else the registry entry
        var dragon = Entry("dragon");
        const string dragonAbout = "The linker that can take stock assets straight from the game's files and make the auto LODs the preview shows.";
        if (s.Known && s.DragonPresent && s.UseDirectly.Ok)
            rows.Add(new(ComponentState.Ok, "Dragon", $"{(s.DragonVersion.Length > 0 ? s.DragonVersion + " · " : "installed · ")}{string.Join(", ", s.DragonCapabilities)}",
                "Blackbird links with Dragon, so stock assets can be used directly with no copies." + (s.UseDirectly.Warnings.Count > 0 ? " " + s.UseDirectly.Warnings[0] : "")));
        else if (s.Known && s.DragonPresent)
            rows.Add(new(ComponentState.Warn, "Dragon", s.DragonVersion.Length > 0 ? s.DragonVersion + " · installed" : "installed", s.UseDirectly.Note));
        else if (dragon is { Current: true })
            rows.Add(new(ComponentState.Ok, "Dragon", $"{(dragon.Version.Length > 0 ? dragon.Version + " · " : "installed · ")}{string.Join(", ", dragon.Capabilities)}",
                dragon.Capabilities.Contains(DragonComponent.AutoLodCapability) ? "Previews show the auto LODs Dragon makes." : dragonAbout));
        else if (dragon is not null)
            rows.Add(new(ComponentState.Warn, "Dragon", dragon.Version.Length > 0 ? dragon.Version + " · entry out of date" : "entry out of date",
                "Dragon's entry doesn't match the file on disk. It is written again on Dragon's next link."));
        else
            rows.Add(new(ComponentState.Off, "Dragon", "not installed", s.Known && s.UseDirectly.Note.Length > 0 ? s.UseDirectly.Note : dragonAbout));

        // Blackbird
        var blackbird = Entry("blackbird");
        if (s.Known && s.BlackbirdVersion.Length > 0)
            rows.Add(new(ComponentState.Ok, "Blackbird", s.BlackbirdVersion, "Builds, runs and publishes your mods."));
        else if (blackbird is { Current: true })
            rows.Add(new(ComponentState.Ok, "Blackbird", blackbird.Version.Length > 0 ? blackbird.Version : "installed", "Builds, runs and publishes your mods."));
        else if (s.Known && s.BlackbirdSettingsFound)
            rows.Add(new(ComponentState.Ok, "Blackbird", "installed", "Builds, runs and publishes your mods. This Blackbird doesn't report to the component list yet; Apex goes by its settings."));
        else
            rows.Add(new(ComponentState.Off, "Blackbird", "not installed", "Builds, runs and publishes your mods."));

        // Radiant: only the helper checks the plugin
        if (!s.Known)
            rows.Add(new(ComponentState.Off, "Radiant integration", "unknown", "The Stock Library helper checks the Radiant plugin."));
        else if (s.Radiant.Ok)
            rows.Add(new(ComponentState.Ok, "Radiant integration", "StockLibrary plugin", "Stock models in Radiant's model browser."));
        else
            rows.Add(new(ComponentState.Warn, "Radiant integration", s.RadiantPluginPresent ? "plugin installed" : "plugin not found", s.Radiant.Note));

        // Everything else that wrote an entry (Ultrasound, ...), from the helper and the registry
        var others = s.Others.Select(o => (o.Id, o.Version))
            .Concat(registry.Where(r => r.Current && r.Id is not ("stocklib" or "dragon" or "blackbird")).Select(r => (r.Id, r.Version)))
            .GroupBy(o => o.Id, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        var ultrasound = others.FirstOrDefault(o => o.Id.Equals("ultrasound", StringComparison.OrdinalIgnoreCase));
        // Ultrasound doesn't write its entry yet: not finding one says nothing about whether it is installed
        rows.Add(ultrasound.Id is not null
            ? new(ComponentState.Ok, "Ultrasound", ultrasound.Version.Length > 0 ? ultrasound.Version : "installed", "Replaces the Mod Tools sound converter.")
            : new(ComponentState.Off, "Ultrasound", "not reported", "Replaces the Mod Tools sound converter. It doesn't report itself to the component list yet."));
        foreach (var o in others.Where(o => !o.Id.Equals("ultrasound", StringComparison.OrdinalIgnoreCase)))
            rows.Add(new(ComponentState.Ok, o.Id, o.Version.Length > 0 ? o.Version : "installed", ""));
        return rows;
    }
}
