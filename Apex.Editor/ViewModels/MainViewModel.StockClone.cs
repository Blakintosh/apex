using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Apex.Editor.Models;
using Apex.Editor.Services.Stock;

namespace Apex.Editor.ViewModels;

public sealed partial class MainViewModel
{
    /// <summary>The Create local copy dialog while it is open.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCloneOpen), nameof(IsModalOpen))]
    private StockCloneViewModel? _cloneDialog;

    public bool IsCloneOpen => CloneDialog is not null;

    partial void OnIsStockHelperOpenChanged(bool value) => OnPropertyChanged(nameof(IsModalOpen));

    /// <summary>The maps and mods a copy can go into; the harness replaces this to answer without an install.</summary>
    public Func<IReadOnlyList<CloneTarget>>? CloneTargetSource { get; set; }

    private IReadOnlyList<CloneTarget> CloneTargets() =>
        CloneTargetSource?.Invoke() ?? StockTargets.Enumerate(_env.IsAvailable ? _env.Bo3Root : null, _settings.CloneTarget);

    /// <summary>The target a copy goes to unless the dialog says otherwise: the last one used, else the first map or mod.</summary>
    private CloneTarget? DefaultCloneTarget() => CloneTargets().FirstOrDefault();

    /// <summary>Opens Create local copy for a stock asset.</summary>
    private void OpenCloneDialog(AssetRecord record)
    {
        if (record.Stock is null || CloneDialog is not null)
            return;
        if (!Stock.CanAsk)
        {
            // The helper is what reads the game's files: without it the library offers to find it.
            InstallStockHelper();
            return;
        }
        var targets = CloneTargets();
        var dialog = new StockCloneViewModel(record, Stock, _env.IsAvailable ? _env.Bo3Root : null, targets, CloneGdtChoices(record),
            () => CloneDialog = null, OnCloneFinished, text => Status = text);
        dialog.CopyText = text => Shell?.CopyTextAsync(text) ?? System.Threading.Tasks.Task.CompletedTask;
        CloneDialog = dialog;
        if (targets.Count > 0)
            dialog.Begin();
    }

    private IReadOnlyList<CloneGdtChoice> CloneGdtChoices(AssetRecord record)
    {
        var name = record.Name;
        var slash = name.IndexOf('/');
        if (slash > 0)
            name = name[(slash + 1)..];
        var choices = new List<CloneGdtChoice> { new($"New file: stock_{name}.gdt", null, "source_data") };
        foreach (var gdt in _db.Gdts.Where(g => g.FullPath is not null && g.Name.StartsWith("source_data/", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase))
            choices.Add(new CloneGdtChoice(AssetShortGdt(gdt.Name) + ".gdt", gdt.FullPath, Services.AssetQuery.GdtFolder(gdt.Name)));
        return choices;
    }

    private static string AssetShortGdt(string gdtName) => Services.AssetQuery.ShortGdt(gdtName);

    private void OnCloneFinished(StockExtractResult result, CloneTarget target)
    {
        _settings.CloneTarget = target.Key;
        _settings.Save();
        Status = $"Created {result.Entries:N0} local asset{(result.Entries == 1 ? "" : "s")} from {result.Name} in {target.Name}";
    }
}
