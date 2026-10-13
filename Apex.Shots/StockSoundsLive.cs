using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Avalonia.Controls;
using Avalonia.VisualTree;
using NAudio.Wave;
using Apex.Editor.Commands;
using Apex.Editor.Services.Preview.Notetracks;
using Apex.Editor.Services.Stock;
using Apex.Editor.ViewModels;
using Apex.Editor.Views;

namespace Apex.Shots;

/// <summary>
/// <c>--stock-sounds-live</c>: the Stock sounds view on the real install with the real helper (STOCKLIB_PATH, else the helper's
/// release build next to its repo, which has the sound methods). The list over every shipped alias, a timed search and filters,
/// a definition read, a preview exported by the helper (decoded to check it is the sound it says), and one Create local copy
/// of a two-variant sound into a throwaway sound_assets and CSV (not the install's), its FLACs checked and then Undone, with
/// the files and the CSV verified gone. The dialog's default folders are checked to resolve to the install's, and closed unused.
/// </summary>
public partial class Program
{
    private static void RunLiveStockSounds(string outDir)
    {
        if (Environment.GetEnvironmentVariable(StockLibrary.PathVariable) is not { Length: > 0 })
            Environment.SetEnvironmentVariable(StockLibrary.PathVariable, DefaultStocklib);
        MainViewModel vm;
        var saved = Environment.GetEnvironmentVariable("APEX_FORCE_MOCK");
        Environment.SetEnvironmentVariable("APEX_FORCE_MOCK", null);
        try { vm = new MainViewModel(NewScratch("live-sounds-session")); }
        finally { Environment.SetEnvironmentVariable("APEX_FORCE_MOCK", saved); }
        if (vm.IsMockData)
        {
            Console.WriteLine("stock sounds live: BO3 not found, skipped");
            return;
        }
        try
        {
            LiveStockSounds(vm, outDir);
        }
        finally
        {
            vm.Dispose();
            Apex.Editor.Models.SchemaRegistry.ResetToMock();
        }
    }

    private static void LiveStockSounds(MainViewModel vm, string outDir)
    {
        var loadUntil = DateTime.UtcNow.AddMinutes(4);
        while (!vm.Status.StartsWith("Loaded", StringComparison.Ordinal) && DateTime.UtcNow < loadUntil)
            Pump(100);
        var window = new MainWindow { DataContext = vm };
        window.Show();
        window.Activate();
        window.UpdateLayout();
        Pump();
        var root = vm.Stock.Install!;
        Check($"stock sounds live: the helper is found ({vm.Stock.ExePath})", vm.Stock.ExePath is not null);
        var until = DateTime.UtcNow.AddMinutes(5);
        while ((vm.Stock.IsRefreshing || !vm.Stock.CanSearch) && DateTime.UtcNow < until)
            Pump(100);
        Check("stock sounds live: the library is ready", vm.Stock.State == StockState.Ready);

        var audio = new LiveRecordingAudio();
        vm.SoundPreviewFactory = lib => new StockSoundPreview(lib, () => audio);
        var clock = Stopwatch.StartNew();
        vm.Registry[CommandCatalog.StockSounds].Execute();
        Pump();
        var view = vm.StockSounds;
        Check("stock sounds live: the command opens the view", view is not null);
        if (view is null)
            return;
        Wait(() => view.Items.Count > 0 || view.HasNote, 60);
        Console.WriteLine($"stock sounds live: the first page after {clock.ElapsedMilliseconds} ms ({view.Items.Count} rows of {view.Total:N0}; '{view.Note}')");
        Check($"stock sounds live: every shipped alias is listed ({view.Total:N0} names, {view.Items.Count} on the first page; '{view.CountText}')",
            view.Total > 40_000 && view.Items.Count >= StockSoundsViewModel.PageGoal);
        Check($"stock sounds live: the zone and language dropdowns are filled ({view.Zones.Count - 1} zones, {string.Join("/", view.Langs.Skip(1).Select(l => l.Value))})",
            view.Zones.Count > 150 && view.Langs.Any(l => l.Value == "en"));
        window.UpdateLayout();
        var list = window.GetVisualDescendants().OfType<ListBox>().First(l => l.Name == "SoundsList");
        Console.WriteLine($"stock sounds live: {list.GetVisualDescendants().OfType<ListBoxItem>().Count()} rows realized of {view.Items.Count}");

        // paging: scrolling to the end of the loaded rows asks for the next page
        var loaded = view.Items.Count;
        clock.Restart();
        list.ScrollIntoView(view.Items[^1]);
        Wait(() => view.Items.Count > loaded, 30);
        Console.WriteLine($"stock sounds live: the next page after {clock.ElapsedMilliseconds} ms ({loaded} -> {view.Items.Count} rows)");
        Check("stock sounds live: scrolling to the end loads the next page", view.Items.Count > loaded);

        // search, timed
        foreach (var typed in new[] { "bow_fire", "wpn_elemental_bow_fire_npc", "ui" })
        {
            clock.Restart();
            view.Query = typed;
            Pump(200);
            Wait(() => !view.IsLoading && view.Items.Count > 0, 30);
            Console.WriteLine($"stock sounds live: '{typed}' lists after {clock.ElapsedMilliseconds} ms (includes the 180 ms debounce): {view.Items.Count} rows, '{view.CountText}'");
        }
        view.Query = "wpn_elemental_bow_fire_npc";
        Wait(() => view.Items.Count == 1 && !view.IsLoading, 30);
        Check($"stock sounds live: an exact name finds it ({string.Join(", ", view.Items.Select(r => r.Name))})", view.Items.Count == 1 && view.Items[0].Name == "wpn_elemental_bow_fire_npc");

        // filters
        view.Query = "";
        view.Zone = view.Zones.First(z => z.Value == "zm_castle");
        Wait(() => !view.IsLoading && view.Items.Count > 0, 30);
        Check($"stock sounds live: the zone filter lists zm_castle's sounds ({view.Total:N0})", view.Items.Count > 0 && view.Items.All(r => r.Item.Zones.Contains("zm_castle")));
        view.Storage = view.Storages.First(s => s.Value == "streamed");
        Wait(() => !view.IsLoading, 60);
        Check($"stock sounds live: Streamed keeps the streamed ones ({view.Items.Count} so far; '{view.CountText}')", view.Items.Count > 0 && view.Items.All(r => r.Item.Storage is "streamed" or "primed"));
        view.ClearFiltersCommand.Execute(null);
        view.Lang = view.Langs.First(l => l.Value == "en");
        Wait(() => !view.IsLoading && view.Items.Count > 0, 30);
        Check($"stock sounds live: the English bank's dialog is listed ({view.Total:N0}; e.g. {view.Items[0].Name})", view.Total > 20_000 && view.Items.All(r => r.Item.Lang == "en"));
        view.ClearFiltersCommand.Execute(null);

        // the definition and a real preview
        view.Query = "wpn_elemental_bow_fire_npc";
        Wait(() => view.Items.Count == 1 && !view.IsLoading, 30);
        list.SelectedItem = view.Items[0];
        Wait(() => view.SelectedDef is not null, 30);
        Pump(150);
        window.UpdateLayout();
        var def = view.SelectedDef!;
        Check($"stock sounds live: the definition is read ({def.Zone} {def.Bank}, {def.Storage}, {def.Variants} variants, {def.DurationMs} ms {def.Format})",
            def.Zone == "zm_castle" && def.Variants == 8 && def.HasAudio && def.Format == "flac" && def.Rate == 48000);
        Capture(window, Path.Combine(outDir, "live-stock-sounds.png"));

        clock.Restart();
        view.PlayCommand.Execute(null);
        Wait(() => view.IsPlaying || (!view.IsExporting && view.PlayText.Length > 0), 60);
        Console.WriteLine($"stock sounds live: Play after {clock.ElapsedMilliseconds} ms ('{view.PlayText}')");
        Check("stock sounds live: Play exports the sound and starts it", view.IsPlaying && audio.Played.Count == 1);
        if (audio.Played.Count == 1)
        {
            using var reader = new WaveFileReader(audio.Played[0]);
            var ms = (int)reader.TotalTime.TotalMilliseconds;
            Check($"stock sounds live: the export is a wav that lasts what the alias says ({ms} ms of {def.DurationMs} ms, {reader.WaveFormat.Channels} ch, {reader.WaveFormat.SampleRate} Hz)",
                Math.Abs(ms - def.DurationMs) < 40 && reader.WaveFormat.Channels == def.Channels && reader.WaveFormat.SampleRate == def.Rate);
            Capture(window, Path.Combine(outDir, "live-stock-sounds-playing.png"));
        }
        view.PlayCommand.Execute(null);
        Check("stock sounds live: Stop ends it", !view.IsPlaying);
        view.VariantIndex = 3;
        clock.Restart();
        view.PlayCommand.Execute(null);
        Wait(() => view.IsPlaying || (!view.IsExporting && view.PlayText.Length > 0), 90);
        Console.WriteLine($"stock sounds live: another variant after {clock.ElapsedMilliseconds} ms ('{view.PlayText}')");
        Check("stock sounds live: another variant (read from the zone) plays too", view.IsPlaying && audio.Played.Count == 2 && audio.Played[1].EndsWith("_3.wav"));
        view.StopPlayback();

        // a sound with no audio says so
        view.Query = "";
        view.Zone = view.Zones.First(z => z.Value == "en_zm_genesis");
        view.OnlyWithAudio = false;
        Wait(() => !view.IsLoading && view.Items.Count > 0, 30);
        list.SelectedItem = view.Items[0];
        Wait(() => view.SelectedDef is not null, 30);
        Check($"stock sounds live: an unreadable bank's sound can't be played or copied ('{view.Fields.FirstOrDefault(f => f.Label == "Audio")?.Value}')", !view.CanPlay && !view.CanCopy);
        view.ClearFiltersCommand.Execute(null);

        // the dialog's own folders are the install's (nothing is written: it is closed unused)
        view.Query = "wpn_c96_lfe";
        Wait(() => view.Items.Count == 1 && !view.IsLoading, 30);
        list.SelectedItem = view.Items[0];
        Wait(() => view.SelectedDef is not null, 30);
        view.CopyCommand.Execute(null);
        Pump();
        var dialog = vm.SoundCopyDialog;
        Check($"stock sounds live: the copy dialog's defaults are the install's folders ({dialog?.Csv.FullPath}; {dialog?.AudioFolder})",
            dialog is not null && dialog.Csv.FullPath.StartsWith(Path.Combine(root, "share", "raw", "sound", "aliases"), StringComparison.OrdinalIgnoreCase)
            && dialog.AudioFolder == Path.Combine("sound_assets", "stock", dialog.Zone) + "\\");
        dialog?.CloseCommand.Execute(null);
        Pump();

        // one real copy, into a throwaway sound_assets and CSV, then Undo
        var scratch = NewScratch("live-sounds-copy");
        var assets = Path.Combine(scratch, "sound_assets");
        var aliases = Path.Combine(scratch, "aliases");
        Directory.CreateDirectory(aliases);
        vm.SoundAssetsSource = () => assets;
        vm.SoundAliasesSource = () => aliases;
        view.CopyCommand.Execute(null);
        Pump();
        dialog = vm.SoundCopyDialog;
        if (dialog is null)
        {
            Check("stock sounds live: the copy dialog opens", false);
            return;
        }
        window.UpdateLayout();
        Check($"stock sounds live: the dialog plans a copy of {dialog.Alias} ({dialog.Variants} variants, {dialog.RowsText.Split('\n').Length} rows)", dialog.CanCreate && dialog.Variants == 2 && dialog.RowsText.Split('\n').Length == 2);
        Capture(window, Path.Combine(outDir, "live-stock-sounds-copy.png"));
        clock.Restart();
        dialog.CreateCommand.Execute(null);
        Wait(() => dialog.Stage is SoundCopyStage.Done || dialog.Error.Length > 0, 120);
        Console.WriteLine($"stock sounds live: the copy took {clock.ElapsedMilliseconds} ms ('{dialog.ResultTitle}'{(dialog.Error.Length > 0 ? ", error " + dialog.Error : "")})");
        Pump(100);
        var folder = Path.Combine(assets, "stock", dialog.Zone);
        var flacs = Directory.Exists(folder) ? Directory.GetFiles(folder, "*.flac").OrderBy(f => f).ToList() : new();
        Check($"stock sounds live: two FLAC files are written under stock\\{dialog.Zone} ({string.Join(", ", flacs.Select(f => $"{Path.GetFileName(f)} {new FileInfo(f).Length} B"))})",
            flacs.Count == 2 && flacs.All(f => File.ReadAllBytes(f).Take(4).SequenceEqual(Encoding.ASCII.GetBytes("fLaC")) && new FileInfo(f).Length > 1000));
        var csv = Path.Combine(aliases, "stock_sounds.csv");
        var lines = File.Exists(csv) ? File.ReadAllLines(csv) : Array.Empty<string>();
        Console.WriteLine("stock sounds live: the CSV: " + string.Join(" | ", lines));
        Check("stock sounds live: the CSV has the header and one row per variant, each naming its file",
            lines.Length == 3 && lines[0] == "Name,Storage,FileSpec,Looping,Bus" && lines[1].StartsWith("wpn_c96_lfe,") && lines[1].Contains(Path.Combine("stock", dialog.Zone, "wpn_c96_lfe_00.flac"))
            && lines[2].Contains("wpn_c96_lfe_01.flac"));
        Capture(window, Path.Combine(outDir, "live-stock-sounds-copy-done.png"));
        dialog.UndoCommand.Execute(null);
        Pump(100);
        Check($"stock sounds live: Undo removes the files, the folders and the CSV ('{dialog.ResultTitle}')",
            !Directory.Exists(Path.Combine(assets, "stock")) && !File.Exists(csv) && dialog.Stage == SoundCopyStage.Undone);
        Check("stock sounds live: nothing is left in the throwaway folders (the sound_assets this copy created goes too)", !Directory.EnumerateFileSystemEntries(aliases).Any() && (!Directory.Exists(assets) || !Directory.EnumerateFileSystemEntries(assets).Any()));
        dialog.CloseCommand.Execute(null);
        Directory.Delete(scratch, recursive: true);
        vm.DismissTopLayer();
        window.Close();
    }

    private static void Wait(Func<bool> done, int seconds)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!done() && DateTime.UtcNow < until)
            Pump(20);
        Pump(20);
    }

    private sealed class LiveRecordingAudio : INotetrackAudio
    {
        public readonly System.Collections.Generic.List<string> Played = new();
        public void Play(string path, float volume) => Played.Add(path);
        public void StopAll() { }
        public event Action<string>? Undecodable { add { } remove { } }
        public void Dispose() { }
    }
}
