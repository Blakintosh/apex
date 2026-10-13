using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Microsoft.Data.Sqlite;
using Apex.Editor.Commands;
using Apex.Editor.Services.Preview.Notetracks;
using Apex.Editor.Services.Stock;
using Apex.Editor.ViewModels;
using Apex.Editor.Views;

namespace Apex.Shots;

/// <summary>
/// Stock sounds: the list that loads as it scrolls and is virtualized, the search and the filters (zone, language,
/// loaded / streamed), the read-only definition, the play / stop preview, and Create local copy with its Undo, on the
/// helper's real <c>sounds</c> / <c>sound</c> output captured into a fixture (a fake backend answers from it, filters and
/// pages as the helper does). The audio comes from a real captured FLAC and a generated wav; nothing is played (a fake output
/// stands in) and nothing is written outside a temp folder. Screenshots: the list, the details, playing, the copy dialog.
/// </summary>
public partial class Program
{
    private sealed class FakeSoundAudio : INotetrackAudio
    {
        public readonly List<string> Played = new();
        public int Stops;
        public void Play(string path, float volume) => Played.Add(path);
        public void StopAll() => Stops++;
        public event Action<string>? Undecodable { add { } remove { } }
        public void Dispose() { }
    }

    private sealed partial class FakeStockBackend
    {
        private static readonly Lazy<(List<StockSoundItem> Items, Dictionary<string, List<StockSoundDef>> Defs)> SoundData = new(() =>
        {
            using var doc = JsonDocument.Parse(Fixture("sounds.json"));
            var items = doc.RootElement.GetProperty("items").EnumerateArray().Select(StockSoundItem.Parse).ToList();
            var defs = doc.RootElement.GetProperty("definitions").EnumerateObject()
                .ToDictionary(p => p.Name, p => p.Value.EnumerateArray().Select(StockSoundDef.Parse).ToList(), StringComparer.OrdinalIgnoreCase);
            return (items, defs);
        });

        public static IReadOnlyList<StockSoundItem> SoundItems => SoundData.Value.Items;

        public Task<StockSoundPage> SoundsAsync(StockSoundQuery query, CancellationToken cancel = default)
        {
            Calls.Add($"sounds q={query.Query} zone={query.Zone} lang={query.Lang} status={query.Status} group={query.Group} offset={query.Offset}");
            IEnumerable<StockSoundItem> items = SoundData.Value.Items;
            if (query.Query.Length > 0)
            {
                var pattern = query.Query.Contains('*') || query.Query.Contains('?')
                    ? new Regex("^" + Regex.Escape(query.Query).Replace("\\*", ".*").Replace("\\?", ".") + "$", RegexOptions.IgnoreCase)
                    : new Regex(Regex.Escape(query.Query), RegexOptions.IgnoreCase);
                items = items.Where(i => pattern.IsMatch(i.Name))
                    .OrderBy(i => i.Name.Equals(query.Query, StringComparison.OrdinalIgnoreCase) ? 0 : i.Name.StartsWith(query.Query, StringComparison.OrdinalIgnoreCase) ? 1 : 2)
                    .ThenBy(i => i.Name.Length);
            }
            if (query.Zone is { Length: > 0 })
                items = items.Where(i => i.Zones.Contains(query.Zone, StringComparer.OrdinalIgnoreCase));
            if (query.Lang is { Length: > 0 })
                items = items.Where(i => i.Lang == query.Lang);
            if (query.Status == "ok")
                items = items.Where(i => i.HasAudio);
            var all = items.ToList();
            if (!query.Group)
                all = all.SelectMany(i => i.Zones.Select(z => new StockSoundItem
                {
                    Name = i.Name, Lang = i.Lang, Hash = i.Hash, Definitions = 1, Zones = new[] { z }, Variants = i.Variants, Looping = i.Looping,
                    Storage = i.Storage, DurationMs = i.DurationMs, Channels = i.Channels, Rate = i.Rate, HasAudio = i.HasAudio,
                })).ToList();
            return Task.FromResult(new StockSoundPage(all.Count, all.Skip(query.Offset).Take(query.Limit).ToList()));
        }

        public Task<StockSoundDefs> SoundAsync(string name, CancellationToken cancel = default)
        {
            Calls.Add("sound " + name);
            if (SoundData.Value.Defs.TryGetValue(name, out var known))
                return Task.FromResult(new StockSoundDefs(name, known));
            var made = SoundData.Value.Items.Where(i => i.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).SelectMany(i => i.Zones.Select(z => new StockSoundDef
            {
                Name = i.Name, Zone = z, Bank = z + "." + (i.Lang == "all" ? "all" : i.Lang), Lang = i.Lang, Hash = i.Hash, Variants = i.Variants, Storage = i.Storage,
                Looping = i.Looping, File = "fixture\\" + i.Name + ".snd", Status = i.HasAudio ? "ok" : "no_audio", BankFile = z + ".all.sabl", DurationMs = i.DurationMs,
                Channels = i.Channels, Rate = i.Rate, Format = "flac", Size = 4096,
            })).ToList();
            return Task.FromResult(new StockSoundDefs(name, made));
        }

        /// <summary>The variant whose export writes part of a file and then fails (-1: none).</summary>
        public int FailExportAt { get; set; } = -1;

        public Task<StockSoundExport> SoundExportAsync(string name, string? zone, int variant, string format, string outPath, CancellationToken cancel = default)
        {
            Calls.Add($"sound_export {name} zone={zone} variant={variant} format={format}");
            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
            if (variant == FailExportAt)
            {
                File.WriteAllText(outPath, "half a stream");
                throw new StockHelperException("the disk is full");
            }
            if (format == "flac")
                File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "stock", "uin_up.flac"), outPath, overwrite: true);
            else
                File.WriteAllBytes(outPath, SineWav(0.4));
            return Task.FromResult(new StockSoundExport(outPath, format, zone ?? "", zone + ".all", "fixture.snd", variant, 8, new FileInfo(outPath).Length, 400, 1, 48000));
        }
    }

    /// <summary>A mono 16-bit 48 kHz wav of a tone.</summary>
    private static byte[] SineWav(double seconds)
    {
        const int rate = 48000;
        var n = (int)(rate * seconds);
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(Encoding.ASCII.GetBytes("RIFF"));
        w.Write(36 + n * 2);
        w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        w.Write(16);
        w.Write((short)1);
        w.Write((short)1);
        w.Write(rate);
        w.Write(rate * 2);
        w.Write((short)2);
        w.Write((short)16);
        w.Write(Encoding.ASCII.GetBytes("data"));
        w.Write(n * 2);
        for (var i = 0; i < n; i++)
            w.Write((short)(Math.Sin(i * 2 * Math.PI * 440 / rate) * 8000));
        return ms.ToArray();
    }

    /// <summary>The sound tables of the helper's schema (the columns Apex reads) on the fixture database, for the filters' zones and languages.</summary>
    private static void AddSoundFixture(string dbPath)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        db.Open();
        void Run(string sql)
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
        Run("CREATE TABLE sound_alias(id INTEGER PRIMARY KEY, zone_id INTEGER NOT NULL, bank TEXT, lang TEXT NOT NULL, name TEXT NOT NULL COLLATE NOCASE)");
        foreach (var item in FakeStockBackend.SoundItems)
            foreach (var zone in item.Zones)
            {
                Run($"INSERT OR IGNORE INTO zone(name, lang, has_assetlist) VALUES('{zone}', NULL, 0)");
                Run($"INSERT INTO sound_alias(zone_id, bank, lang, name) VALUES((SELECT id FROM zone WHERE name='{zone}'), '{zone}.all', '{item.Lang}', '{item.Name}')");
            }
    }

    private static ListBox SoundsList(MainWindow window) => window.GetVisualDescendants().OfType<ListBox>().First(l => l.Name == "SoundsList");

    private static string Text(MainWindow window, string name) => window.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Name == name)?.Text ?? "";

    private static void StockSoundChecks(MainViewModel vm, MainWindow window, FakeStockBackend fake, string temp, string outDir, string dbPath)
    {
        var audio = new FakeSoundAudio();
        // the offer checks before this replaced the library: back to the fixture database and the fake helper
        vm.UseStockLibrary(StockLibrary.ForDatabase(dbPath, fake));
        var aliases = Path.Combine(temp, "sounds-install", "share", "raw", "sound", "aliases");
        var assets = Path.Combine(temp, "sounds-install", "sound_assets");
        Directory.CreateDirectory(aliases);
        vm.SoundAliasesSource = () => aliases;
        vm.SoundAssetsSource = () => assets;
        vm.SoundCsvPicker = () => Task.FromResult<string?>(null);
        vm.SoundPreviewFactory = _ => new StockSoundPreview(fake, () => audio, Path.Combine(temp, "preview"));

        var (zones, langs) = new StockDb(dbPath).SoundFilters();
        Check($"stock sounds: the index lists the zones and languages that define sounds ({zones.Count} zones, {string.Join("/", langs)})",
            zones.Contains("core_ui") && zones.Contains("zm_castle") && langs.Contains("all") && langs.Contains("en"));

        // ── the entry: the command (and so the palette and the app menu) ──
        vm.OpenCommandPaletteCommand.Execute(null);
        vm.PaletteText = ">stock sounds";
        Pump();
        var entry = vm.PaletteResults.FirstOrDefault(r => r.CommandId == CommandCatalog.StockSounds);
        Check($"stock sounds: the command palette finds it ('{entry?.Name}')", entry is not null && entry.Name.EndsWith("Stock sounds"));
        vm.ClosePaletteCommand.Execute(null);
        var searchBefore = fake.Calls.Count(c => c.StartsWith("sounds "));
        Check("stock sounds: the Explorer's search never asks for sounds", searchBefore == 0);
        vm.Registry[CommandCatalog.StockSounds].Execute();
        Pump();
        var view = vm.StockSounds;
        Check("stock sounds: the command opens the view over the card", view is not null && vm.IsStockSoundsOpen && vm.IsAnyLayerOpen);
        if (view is null)
            return;
        WaitFor(() => view.Items.Count > 0);
        Pump(100);
        window.UpdateLayout();

        // ── the list ──
        var total = FakeStockBackend.SoundItems.Count;
        Check($"stock sounds: the first page lists the names ({view.Items.Count} of {view.Total}; '{view.CountText}')",
            view.Items.Count == StockSoundsViewModel.PageSize && view.Total == total && view.CountText == $"{view.Items.Count:N0} of {total:N0} sounds");
        var list = SoundsList(window);
        var realized = list.GetVisualDescendants().OfType<ListBoxItem>().Count();
        Check($"stock sounds: the rows are virtualized (the list holds {view.Items.Count} rows, {realized} are on screen)", realized > 5 && realized < 60);
        // scroll to the end: the next page arrives
        list.ScrollIntoView(view.Items[^1]);
        Pump(100);
        WaitFor(() => view.Items.Count > StockSoundsViewModel.PageSize);
        Pump(50);
        Check($"stock sounds: scrolling to the end loads the next page ({view.Items.Count} rows)", view.Items.Count == 2 * StockSoundsViewModel.PageSize && view.CountText == $"{view.Items.Count:N0} of {total:N0} sounds");
        list.ScrollIntoView(view.Items[^1]);
        Pump(100);
        WaitFor(() => view.Items.Count == total);
        Pump(50);
        Check($"stock sounds: and the last page closes the count ({view.Items.Count} rows; '{view.CountText}')", view.Items.Count == total && view.CountText == $"{total:N0} sounds");
        list.ScrollIntoView(view.Items[0]);
        Pump(50);

        // ── search and filters ──
        view.Query = "wpn_elemental_bow_fire";
        Pump(400);
        WaitFor(() => view.Items.Count == 8);
        Check($"stock sounds: typing searches, best match first ({view.Items.Count}: {string.Join(", ", view.Items.Take(3).Select(r => r.Name))}) and asks the helper once ({fake.Calls.Count(c => c.StartsWith("sounds q=wpn_elemental_bow_fire"))} calls)",
            view.Items.Count == 8 && fake.Calls.Count(c => c.StartsWith("sounds q=wpn_elemental_bow_fire")) == 1 && view.Items[0].Name.Length <= view.Items[^1].Name.Length);
        view.Query = "wpn_elemental_bow_*_npc";
        Pump(400);
        WaitFor(() => view.Items.Count == 2);
        Check($"stock sounds: a pattern with * works ({string.Join(", ", view.Items.Select(r => r.Name))})",
            view.Items.Count == 2 && view.Items.All(r => r.Name.EndsWith("_npc")));
        view.Query = "";
        Pump(400);
        WaitFor(() => view.Items.Count == StockSoundsViewModel.PageSize);

        view.Storage = view.Storages.First(s => s.Value == "streamed");
        WaitFor(() => !view.IsLoading && view.Items.Count > 0);
        Pump(50);
        Check($"stock sounds: the Streamed filter keeps streamed sounds, asking for more pages to fill ({view.Items.Count} rows; '{view.CountText}')",
            view.Items.Count > 0 && view.Items.All(r => r.Item.Storage is "streamed" or "primed") && view.HasFilters);
        view.Storage = view.Storages[0];
        view.Zone = view.Zones.First(z => z.Value == "zm_castle");
        WaitFor(() => view.Items.Count > 0 && view.Items.All(r => r.Item.Zones.Contains("zm_castle")));
        Pump(50);
        Check($"stock sounds: the zone filter lists that zone's sounds ({view.Items.Count})", view.Items.Count > 0 && view.Items.All(r => r.Item.Zones.Contains("zm_castle")));
        view.Zone = view.Zones[0];
        view.Lang = view.Langs.First(l => l.Value == "en");
        WaitFor(() => view.Items.Count > 0 && view.Items.All(r => r.Item.Lang == "en"));
        Pump(50);
        Check($"stock sounds: the language filter lists a bank language ({view.Items.Count}, tagged '{view.Items[0].LangText}')", view.Items.Count > 0 && view.Items.All(r => r.Item.Lang == "en") && view.Items[0].LangText == "en");
        view.OnlyWithAudio = true;
        WaitFor(() => !view.IsLoading);
        Pump(50);
        Check("stock sounds: Has audio drops the sounds the reader could not read", view.Items.Count > 0 && view.Items.All(r => r.HasAudio));
        view.ClearFiltersCommand.Execute(null);
        Pump(100);
        WaitFor(() => view.Items.Count == StockSoundsViewModel.PageSize);
        Check("stock sounds: Clear filters puts them all back", !view.HasFilters && view.Items.Count == StockSoundsViewModel.PageSize && view.Zone.Value is null);
        view.EveryDefinition = true;
        WaitFor(() => view.Items.Count > 0 && view.Items.All(r => r.Item.Zones.Count == 1));
        Pump(50);
        Check("stock sounds: Each zone lists a definition per zone", view.Items.All(r => r.Item.Zones.Count == 1) && view.Total > total);
        view.EveryDefinition = false;
        WaitFor(() => view.Items.Count == StockSoundsViewModel.PageSize);

        // ── the list, shot ──
        view.Query = "wpn_";
        Pump(400);
        WaitFor(() => view.Items.Count > 10);
        Pump(100);
        window.UpdateLayout();
        Capture(window, Path.Combine(outDir, "stock-sounds-list.png"));

        // ── the details ──
        view.Query = "wpn_elemental_bow_fire";
        Pump(400);
        WaitFor(() => view.Items.Count == 8);
        var row = view.Items.First(r => r.Name == "wpn_elemental_bow_fire_npc");
        list = SoundsList(window);
        list.SelectedItem = row;
        WaitFor(() => view.SelectedDef is not null);
        Pump(150);
        window.UpdateLayout();
        var fields = view.Fields.ToDictionary(f => f.Label, f => f.Value);
        Check($"stock sounds: the details show the definition read-only ({fields.GetValueOrDefault("Zone")}, {fields.GetValueOrDefault("Storage")}, {fields.GetValueOrDefault("Variants")} variants, {fields.GetValueOrDefault("Audio")})",
            view.SelectedDef is { Zone: "zm_castle" } && fields["Alias"] == "wpn_elemental_bow_fire_npc" && fields["Bank"] == "zm_castle.all" && fields["Storage"] == "loaded"
            && fields["Variants"] == "8" && fields["Audio"].Contains("stereo") && fields["Secondary alias"] == "yes");
        var fieldBlocks = window.GetVisualDescendants().OfType<SelectableTextBlock>().Where(t => t.IsEffectivelyVisible).ToList();
        Check("stock sounds: no field is an editor (text blocks only, no text box in the pane)",
            !window.GetVisualDescendants().OfType<StockSoundsView>().First().GetVisualDescendants().OfType<TextBox>().Any(t => t.IsEffectivelyVisible && t.Name != "SoundsSearchBox") && fieldBlocks.Count > 10);
        var variantText = window.GetVisualDescendants().OfType<TextBlock>().First(t => t.Name == "SoundVariantText");
        Check($"stock sounds: the variant stepper shows the first variant ('{variantText.Text}')", variantText.IsEffectivelyVisible && variantText.Text == "Variant 1 of 8" && view.VariantIndex == 0);
        Check("stock sounds: Create local copy is offered", ButtonNamed(window, "SoundCopyButton").IsEffectivelyVisible && ButtonNamed(window, "SoundCopyButton").IsEnabled);
        Capture(window, Path.Combine(outDir, "stock-sounds-details.png"));

        // a name in several zones lists them, and picking one changes the fields
        view.Query = "uin_up";
        Pump(400);
        WaitFor(() => view.Items.Any(r => r.Name == "uin_up"));
        list.SelectedItem = view.Items.First(r => r.Name == "uin_up");
        WaitFor(() => view.Definitions.Count == 2);
        Pump(100);
        Check($"stock sounds: a name two zones define lists both ({string.Join(", ", view.Definitions.Select(d => d.Bank))})",
            view.Definitions.Count == 2 && view.HasManyDefinitions);
        view.SelectedDef = view.Definitions[1];
        Check("stock sounds: choosing the other zone changes the fields", view.Fields.First(f => f.Label == "Zone").Value == "mp_common");

        // a sound with no audio can't be played or copied, and says why
        view.Query = "";
        view.OnlyWithAudio = false;
        view.Zone = view.Zones.First(z => z.Value == "en_zm_genesis");
        Pump(300);
        WaitFor(() => view.Items.Any(r => !r.HasAudio));
        list.SelectedItem = view.Items.First(r => !r.HasAudio);
        WaitFor(() => view.SelectedDef is not null);
        Pump(100);
        Check($"stock sounds: a sound whose audio is unreadable can't be played or copied ('{view.CopyTip}')",
            !view.CanPlay && !view.CanCopy && view.CopyTip.Contains("not found") && view.Fields.First(f => f.Label == "Audio").Value.Contains("can't open"));
        view.ClearFiltersCommand.Execute(null);
        Pump(200);

        // ── play and stop ──
        view.Query = "wpn_elemental_bow_fire_npc";
        Pump(400);
        WaitFor(() => view.Items.Count == 1);
        SoundsList(window).SelectedItem = view.Items[0];
        WaitFor(() => view.SelectedDef is not null);
        Pump(100);
        window.UpdateLayout();
        ButtonNamed(window, "SoundNextVariant").Command!.Execute(null);
        ButtonNamed(window, "SoundNextVariant").Command!.Execute(null);
        Check($"stock sounds: the stepper chooses a variant ({view.VariantText})", view.VariantIndex == 2 && view.VariantText == "Variant 3 of 8");
        ButtonNamed(window, "SoundPrevVariant").Command!.Execute(null);
        ButtonNamed(window, "SoundPrevVariant").Command!.Execute(null);
        ButtonNamed(window, "SoundPrevVariant").Command!.Execute(null);
        Check($"stock sounds: it wraps round ({view.VariantText})", view.VariantIndex == 7);
        view.VariantIndex = 2;
        var play = ButtonNamed(window, "SoundPlayButton");
        Check("stock sounds: Play is enabled for a sound with audio", play.IsEffectivelyVisible && play.IsEnabled && view.PlayLabel == "Play");
        view.PlayCommand.Execute(null);
        WaitFor(() => view.IsPlaying);
        Pump(100);
        window.UpdateLayout();
        Check($"stock sounds: playing exports the variant as a wav and plays it ('{view.PlayText}'; {string.Join(", ", fake.Calls.Where(c => c.StartsWith("sound_export")))})",
            view.IsPlaying && view.PlayLabel == "Stop" && audio.Played.Count == 1 && audio.Played[0].EndsWith("_2.wav") && File.Exists(audio.Played[0])
            && fake.Calls.Contains("sound_export wpn_elemental_bow_fire_npc zone=zm_castle variant=2 format=wav"));
        Capture(window, Path.Combine(outDir, "stock-sounds-playing.png"));
        var stops = audio.Stops;
        view.PlayCommand.Execute(null);
        Pump(50);
        Check($"stock sounds: Stop silences it ('{view.PlayText}')", !view.IsPlaying && view.PlayLabel == "Play" && audio.Stops > stops && view.PlayText == "Stopped");
        view.PlayCommand.Execute(null);
        WaitFor(() => view.IsPlaying);
        Check("stock sounds: playing again uses the exported file (one export)", audio.Played.Count == 2 && fake.Calls.Count(c => c.StartsWith("sound_export") && c.EndsWith("variant=2 format=wav")) == 1);
        view.StopPlayback();

        // ── Create local copy ──
        view.CopyCommand.Execute(null);
        Pump();
        WaitFor(() => vm.SoundCopyDialog is not null);
        var dialog = vm.SoundCopyDialog;
        Check("stock sounds: Create local copy opens its dialog", dialog is not null && vm.IsSoundCopyOpen && vm.IsModalOpen);
        if (dialog is null)
            return;
        window.UpdateLayout();
        Pump(100);
        Check($"copy dialog: a new file in the aliases folder is the first choice ({dialog.Csv.Label}); all 8 variants are the default",
            dialog.Csv.Label == "New file: stock_sounds.csv" && dialog.AllVariants && dialog.HasManyVariants && dialog.CanCreate);
        Check($"copy dialog: it shows the rows it will add ({dialog.RowsText.Split('\n').Length} rows) and where the audio goes ({dialog.FilesText})",
            dialog.RowsText.Split('\n').Length == 8 && dialog.RowsText.StartsWith("wpn_elemental_bow_fire_npc,loaded,stock\\zm_castle\\wpn_elemental_bow_fire_npc_00.flac,nonlooping,bus_fx")
            && dialog.FilesText.Contains("_00.flac") && dialog.AudioFolder == Path.Combine("sound_assets", "stock", "zm_castle") + "\\");
        Capture(window, Path.Combine(outDir, "stock-sounds-copy-dialog.png"));

        dialog.CreateCommand.Execute(null);
        WaitFor(() => dialog.Stage == SoundCopyStage.Done);
        Pump(100);
        window.UpdateLayout();
        var csv = Path.Combine(aliases, "stock_sounds.csv");
        var audioDir = Path.Combine(assets, "stock", "zm_castle");
        var csvText = File.Exists(csv) ? File.ReadAllText(csv) : "";
        Check($"copy: eight FLAC files land in sound_assets\\stock\\zm_castle ({(Directory.Exists(audioDir) ? Directory.GetFiles(audioDir, "*.flac").Length : 0)})",
            Directory.Exists(audioDir) && Directory.GetFiles(audioDir, "*.flac").Length == 8 && File.Exists(Path.Combine(audioDir, "wpn_elemental_bow_fire_npc_07.flac")));
        Check("copy: a FLAC stream is what is written (the bank's, untouched)", File.ReadAllBytes(Path.Combine(audioDir, "wpn_elemental_bow_fire_npc_00.flac")).Take(4).SequenceEqual(Encoding.ASCII.GetBytes("fLaC")));
        Check($"copy: a new soundalias CSV has the header and a row per variant ({csvText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length} lines)",
            csvText.StartsWith("Name,Storage,FileSpec,Looping,Bus\r\nwpn_elemental_bow_fire_npc,loaded,stock\\zm_castle\\wpn_elemental_bow_fire_npc_00.flac,nonlooping,bus_fx\r\n")
            && csvText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length == 9);
        Check($"copy: the dialog says what it did ('{dialog.ResultTitle}')", dialog.ResultTitle == "Created 8 local sounds" && dialog.CanUndo && dialog.ShowResult && vm.Status.StartsWith("Copied wpn_elemental_bow_fire_npc into stock_sounds.csv"));
        Capture(window, Path.Combine(outDir, "stock-sounds-copy-done.png"));
        dialog.UndoCommand.Execute(null);
        Pump(100);
        Check($"copy undo: the files, the folders and the new CSV are gone ('{dialog.ResultTitle}')",
            !Directory.Exists(audioDir) && !Directory.Exists(Path.Combine(assets, "stock")) && !File.Exists(csv) && dialog.Stage == SoundCopyStage.Undone && !dialog.CanUndo);
        dialog.CloseCommand.Execute(null);
        Pump();
        Check("copy: Done closes the dialog", vm.SoundCopyDialog is null && !vm.IsModalOpen);

        StockSoundCopierChecks(fake, temp);

        // ── Esc unwinds: the view closes ──
        Check("stock sounds: Escape closes the view", vm.DismissTopLayer() && vm.StockSounds is null && !vm.IsStockSoundsOpen);
        window.UpdateLayout();
    }

    /// <summary>The CSV writing on its own: an existing file keeps every byte, rows come in its columns, Undo gives the bytes back.</summary>
    private static void StockSoundCopierChecks(FakeStockBackend fake, string temp)
    {
        var root = Path.Combine(temp, "copier");
        var assets = Path.Combine(root, "sound_assets");
        Directory.CreateDirectory(root);
        var header = "Name,Behavior,Storage,FileSpec,FileSpecSustain,FileSpecRelease,Template,Loadspec,Secondary,SustainAlias,ReleaseAlias,Bus,VolumeGroup,Looping,Comment";
        var existing = $"{header}\r\n#PERKSFX,,,,,,,,,,,,,,\r\nevt_belch,,,zombie\\perks\\burp.wav,,,UIN_MOD,,,,,BUS_FX,,nonlooping,\"it has, a comma\"";
        var csv = Path.Combine(root, "mine.csv");
        File.WriteAllText(csv, existing);
        var before = File.ReadAllBytes(csv);
        StockSoundCopyPlan Plan(string alias, string storage, bool looping, int[] variants, string path) => new()
        {
            Alias = alias, Zone = "zm_castle", Storage = storage, Looping = looping, Variants = variants, SoundAssetsDir = assets, CsvPath = path,
        };

        var plan = Plan("amb_buoy", "streamed", true, new[] { 0 }, csv);
        Check($"copier: rows go in the file's own columns, a streamed looping sound says so ({string.Join(" | ", StockSoundCopier.PreviewRows(plan))})",
            StockSoundCopier.PreviewRows(plan).Single() == "amb_buoy,,streamed,stock\\zm_castle\\amb_buoy.flac,,,,,,,,bus_fx,,looping,");
        var result = StockSoundCopier.CopyAsync(fake, plan, null).GetAwaiter().GetResult();
        var after = File.ReadAllBytes(csv);
        Check("copier: an existing CSV keeps every byte it had; a line break is added before the row, in its own style",
            after.Take(before.Length).SequenceEqual(before) && Encoding.UTF8.GetString(after, before.Length, after.Length - before.Length).StartsWith("\r\namb_buoy,") && !result.CsvCreated);
        var (head, rows) = StockSoundCopier.ReadCsv(File.ReadAllText(csv));
        Check($"copier: the file still reads as a CSV with the quoted comma intact ({rows.Count} rows)", head.Count == 15 && rows.Count == 2 && rows[0][14] == "it has, a comma" && rows[1][0] == "amb_buoy");

        var duplicate = StockSoundCopier.Problems(plan);
        Check($"copier: an alias the file defines already is refused ('{duplicate.FirstOrDefault()}')", duplicate.Count == 2 && duplicate[0].Contains("already defines"));
        File.WriteAllText(Path.Combine(root, "other.csv"), "Foo,Bar\r\n1,2\r\n");
        var notAlias = StockSoundCopier.Problems(Plan("amb_x", "loaded", false, new[] { 0 }, Path.Combine(root, "other.csv")));
        Check($"copier: a CSV without the alias columns is refused ('{notAlias.FirstOrDefault()}')", notAlias.Any(p => p.Contains("isn't a soundalias file")));

        // someone adds a line between the copy and its undo: it stays
        File.AppendAllText(csv, "\r\nmine_added,,loaded,x.wav");
        var undone = StockSoundCopier.Undo(result);
        var rest = File.ReadAllText(csv);
        Check($"copier: undo takes out its row and leaves a line added since ({undone.Kept.Count} notes)",
            undone.RowsRemoved && !rest.Contains("amb_buoy") && rest.Contains("mine_added") && !File.Exists(result.Files[0]) && !Directory.Exists(Path.Combine(assets, "stock")));

        // a file without a final line break, LF line endings, undone to the same bytes
        var lf = Path.Combine(root, "lf.csv");
        File.WriteAllText(lf, "Name,Storage,FileSpec,Looping\nkeep_me,loaded,a.wav,nonlooping");
        var lfBefore = File.ReadAllBytes(lf);
        var lfResult = StockSoundCopier.CopyAsync(fake, Plan("amb_x", "loaded", false, new[] { 0, 1 }, lf), null).GetAwaiter().GetResult();
        var lfText = File.ReadAllText(lf);
        Check($"copier: LF files get LF, variants get numbered files ({lfResult.Files.Count} files)",
            !lfText.Contains('\r') && lfText.Contains("\namb_x,loaded,stock\\zm_castle\\amb_x_00.flac,nonlooping\n") && File.Exists(Path.Combine(assets, "stock", "zm_castle", "amb_x_01.flac")));
        StockSoundCopier.Undo(lfResult);
        Check("copier: undo gives the file back byte for byte", File.ReadAllBytes(lf).SequenceEqual(lfBefore) && !Directory.Exists(Path.Combine(assets, "stock")));

        // a file already there is never overwritten
        Directory.CreateDirectory(Path.Combine(assets, "stock", "zm_castle"));
        File.WriteAllText(Path.Combine(assets, "stock", "zm_castle", "amb_y.flac"), "mine");
        var clash = StockSoundCopier.Problems(Plan("amb_y", "loaded", false, new[] { 0 }, Path.Combine(root, "new.csv")));
        Check($"copier: an audio file already in sound_assets is refused, not overwritten ('{clash.FirstOrDefault()}')", clash.Any(p => p.Contains("already exists")));
        Directory.Delete(Path.Combine(assets, "stock"), recursive: true);

        // the helper fails part way through a variant: what it half-wrote goes with the rest
        fake.FailExportAt = 1;
        var failed = false;
        try
        {
            StockSoundCopier.CopyAsync(fake, Plan("amb_z", "loaded", false, new[] { 0, 1, 2 }, Path.Combine(root, "fail.csv")), null).GetAwaiter().GetResult();
        }
        catch (StockHelperException)
        {
            failed = true;
        }
        finally
        {
            fake.FailExportAt = -1;
        }
        Check("copier: a variant the helper fails part way leaves no file, folder or CSV behind",
            failed && !Directory.Exists(Path.Combine(assets, "stock")) && !File.Exists(Path.Combine(root, "fail.csv")));
    }
}
