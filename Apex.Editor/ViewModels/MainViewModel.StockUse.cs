using System;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Apex.Editor.Models;
using Apex.Editor.Services;
using Apex.Editor.Services.Stock;

namespace Apex.Editor.ViewModels;

/// <summary>
/// Using a stock asset directly: when Dragon carries stock assets for this install and Blackbird builds with it (the helper's
/// <c>use_directly</c> verdict), a stock record's primary action is Use in map and Create local copy is the second one. When
/// it isn't, today's clone-first screen stays and a one-line note says why and what fixes it. The Components view shows the
/// whole picture.
/// </summary>
public sealed partial class MainViewModel
{
    // ── The verdict ──────────────────────────────────────────────────────────

    /// <summary>Stock assets can be used by name, with no copy (Dragon carries them).</summary>
    public bool StockUseOk => Stock.Status.UseDirectly.Ok;

    /// <summary>Why not, and what fixes it, once the helper has answered; empty when it can or has not answered.</summary>
    public string StockUseNote => Stock.Status.Known && !Stock.Status.UseDirectly.Ok ? Stock.Status.UseDirectly.Note : "";

    public bool HasStockUseNote => StockUseNote.Length > 0;

    private string _stockUseSeen = "";

    /// <summary>The library's status changed: the buttons, the note and the banners of open stock tabs follow.</summary>
    private void NotifyStockUse()
    {
        var seen = (Stock.Status.Known ? "k" : "u") + (Stock.Status.UseDirectly.Ok ? "1" : "0") + Stock.Status.UseDirectly.Reason + Stock.Status.UseDirectly.Fix;
        if (seen == _stockUseSeen)
            return;
        _stockUseSeen = seen;
        OnPropertyChanged(nameof(StockUseOk));
        OnPropertyChanged(nameof(StockUseNote));
        OnPropertyChanged(nameof(HasStockUseNote));
        foreach (var tab in OpenTabs.Where(t => t.IsStock))
            tab.RefreshStockInfo(Stock.Db);
        Components?.Update();
    }

    // ── Use in map ───────────────────────────────────────────────────────────

    /// <summary>The Use in map dialog while it is open.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUseOpen), nameof(IsModalOpen))]
    private StockUseViewModel? _useDialog;

    public bool IsUseOpen => UseDialog is not null;

    /// <summary>Opens Use in map for the active stock asset (the header's button).</summary>
    [RelayCommand]
    private void UseInMap()
    {
        if (ActiveTab?.Record is { IsStock: true } record)
            OpenUseDialog(record);
    }

    /// <summary>The stock asset behind a row or tab, for commands that act on it.</summary>
    public void UseInMapOf(AssetRecord record) => OpenUseDialog(record);

    private void OpenUseDialog(AssetRecord record)
    {
        if (record.Stock is null || UseDialog is not null || CloneDialog is not null)
            return;
        if (!StockUseOk)
        {
            // Not available now: the same words as the note, and the clone dialog is the way forward.
            Status = StockUseNote.Length > 0 ? StockUseNote : "Using stock assets directly isn't available yet.";
            OpenCloneDialog(record);
            return;
        }
        var targets = CloneTargets();
        var dialog = new StockUseViewModel(record, Stock, _env.IsAvailable ? _env.Bo3Root : null, targets, targets.FirstOrDefault(),
            () => UseDialog = null, text => Status = text, () => OpenCloneDialog(record));
        dialog.CopyText = text => Shell?.CopyTextAsync(text) ?? System.Threading.Tasks.Task.CompletedTask;
        UseDialog = dialog;
        dialog.Begin();
    }

    // ── Components ───────────────────────────────────────────────────────────

    /// <summary>The Components view while it is open.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsComponentsOpen), nameof(IsModalOpen))]
    private ComponentsViewModel? _components;

    public bool IsComponentsOpen => Components is not null;

    private void ShowComponents()
    {
        if (Components is not null)
            return;
        var install = _env.IsAvailable && _env.Bo3Root is { } root ? root : Stock.Install ?? "";
        Components = new ComponentsViewModel(Stock, install, AppUpdates.CurrentVersion, () => Components = null,
            () =>
            {
                Components = null;
                InstallStockHelper();
            },
            () =>
            {
                Stock.StartRefresh(userAsked: true);
                Components?.Update();
            });
        // what the page shows is checked again now: the install may have changed since Apex started
        Components.RefreshCommand.Execute(null);
    }
}
