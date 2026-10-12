using System;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Apex.Editor.Services.Stock;

namespace Apex.Editor.ViewModels;

/// <summary>
/// The Stock sounds view: every alias of every shipped sound bank, searched and heard, kept out of the Explorer's search. It
/// takes the card like Table and Compare do. Create local copy opens its dialog over it.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>The Stock sounds view while it is open.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStockSoundsOpen), nameof(IsAnyLayerOpen))]
    private StockSoundsViewModel? _stockSounds;

    public bool IsStockSoundsOpen => StockSounds is not null;

    /// <summary>The Create local copy dialog of a stock sound while it is open.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSoundCopyOpen), nameof(IsModalOpen), nameof(IsAnyLayerOpen))]
    private StockSoundCopyViewModel? _soundCopyDialog;

    public bool IsSoundCopyOpen => SoundCopyDialog is not null;

    /// <summary>Where the install keeps sound_assets; the harness points it at a temp folder.</summary>
    public Func<string?>? SoundAssetsSource { get; set; }

    /// <summary>The folder of the install's soundalias CSVs; the harness points it at a temp folder.</summary>
    public Func<string?>? SoundAliasesSource { get; set; }

    /// <summary>Asks for a soundalias CSV (the window's file picker); null: no picker.</summary>
    public Func<Task<string?>>? SoundCsvPicker { get; set; }

    private string? SoundAssetsDir => SoundAssetsSource?.Invoke() ?? (_env.IsAvailable && _env.Bo3Root is { } root ? Path.Combine(root, "sound_assets") : null);

    private string? SoundAliasesDir => SoundAliasesSource?.Invoke() ?? (_env.IsAvailable && _env.Bo3Root is { } root ? Path.Combine(root, "share", "raw", "sound", "aliases") : null);

    /// <summary>Whether the view can open: the helper is there (or a backend stands in for it).</summary>
    public bool CanShowStockSounds => Stock.CanAsk;

    private void ShowStockSounds()
    {
        if (StockSounds is not null)
            return;
        if (!Stock.CanAsk)
        {
            InstallStockHelper();
            return;
        }
        StockSounds = new StockSoundsViewModel(Stock, CloseStockSounds, OpenSoundCopy);
    }

    private void CloseStockSounds()
    {
        var view = StockSounds;
        StockSounds = null;
        view?.Dispose();
    }

    private void OpenSoundCopy(StockSoundDef def, StockSoundItem item, int variant)
    {
        if (SoundCopyDialog is not null)
            return;
        var assets = SoundAssetsDir;
        var aliases = SoundAliasesDir;
        if (assets is null || aliases is null)
        {
            Status = "There is no Black Ops III install to copy the sound into.";
            return;
        }
        SoundCopyDialog = new StockSoundCopyViewModel(def, variant, Stock, assets, aliases, _settings.SoundAliasFile,
            () => SoundCopyDialog = null, text => Status = text,
            path =>
            {
                _settings.SoundAliasFile = path;
                _settings.Save();
            }, SoundCsvPicker is null ? null : () => SoundCsvPicker());
    }
}
