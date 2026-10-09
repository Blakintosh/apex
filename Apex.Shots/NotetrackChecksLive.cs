using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Apex.Editor.Services.Gdt;
using Apex.Editor.Services.Preview;
using Apex.Editor.Services.Preview.Formats;
using Apex.Editor.Services.Preview.Notetracks;

namespace Apex.Shots;

public partial class Program
{
    /// <summary>
    /// The notetrack timeline's data on the real install (read-only): every xanim entry's GDT notetrack actions, the
    /// alias index's build time and memory, how many of the corpus's sound aliases can be heard, and one anim with
    /// both exported notetracks and GDT sound actions laid out as the timeline shows it.
    /// </summary>
    private static void RunLiveNotetrackChecks()
    {
        var (window, vm) = StartLive(out _);
        if (window is null)
            return;
        try
        {
            var db = DatabaseOf(vm);
            var env = new GameEnvironment();
            var clock = Stopwatch.StartNew();
            int anims = 0, withActions = 0, withSound = 0;
            var actions = new Dictionary<string, int>(StringComparer.Ordinal);
            var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var soundAnims = new List<string>();
            foreach (var a in db.Assets.Where(a => a.Type.Equals("xanim", StringComparison.OrdinalIgnoreCase)))
            {
                anims++;
                var markers = GdtNotetracks.Read(a.ScanProperties, Array.Empty<(string, int)>(), 10_000);
                if (markers.Count == 0)
                    continue;
                withActions++;
                foreach (var m in markers)
                    actions[m.Action!] = actions.GetValueOrDefault(m.Action!) + 1;
                if (markers.Any(m => m.IsSound))
                {
                    withSound++;
                    soundAnims.Add(a.Name);
                    foreach (var m in markers.Where(m => m.IsSound))
                        aliases.Add(m.SoundAlias!);
                }
            }
            Check($"notetracks (live): {withActions:N0} of {anims:N0} xanims have GDT notetrack actions, {withSound:N0} with sounds "
                  + $"({string.Join(", ", actions.OrderByDescending(p => p.Value).Take(5).Select(p => $"{p.Key} {p.Value:N0}"))}) — {clock.ElapsedMilliseconds:N0} ms",
                anims > 0 && withSound > 0);

            GC.Collect(2, GCCollectionMode.Forced, true, true);
            var memBefore = GC.GetTotalMemory(true);
            var index = SoundAliasIndex.ForInstall(env.Bo3Root!);
            index.ReadyAsync().Wait();
            GC.Collect(2, GCCollectionMode.Forced, true, true);
            var held = GC.GetTotalMemory(true) - memBefore;
            var stats = index.Stats;
            clock.Restart();
            var states = aliases.Select(index.Resolve).GroupBy(r => r.State).ToDictionary(g => g.Key, g => g.Count());
            var resolveMs = clock.ElapsedMilliseconds;
            Check($"aliases (live): index of {stats.Names:N0} names ({stats.Rows:N0} rows, {stats.Files} CSVs) built in {stats.Elapsed.TotalMilliseconds:N0} ms off the UI thread, "
                  + $"holding {held / 1024.0 / 1024.0:N1} MB; the corpus's {aliases.Count:N0} sound aliases: "
                  + $"{string.Join(", ", states.Select(p => $"{p.Key} {p.Value:N0}"))} ({resolveMs:N0} ms to resolve all)",
                stats.Names > 0 && states.GetValueOrDefault(NotetrackSoundState.Playable) > 0);

            // One anim as the timeline shows it: its exported notetracks plus its GDT actions with their sound states.
            foreach (var name in soundAnims.Take(400))
            {
                var record = db.Assets.First(a => a.Name == name && a.Type.Equals("xanim", StringComparison.OrdinalIgnoreCase));
                var file = record.ScanProperties.GetValueOrDefault("filename", "");
                if (file.Length == 0 || env.XanimExportDir is null
                    || PreviewFileResolver.ResolveRelative(env, env.XanimExportDir, file, new[] { ".xanim_bin", ".xanim_export" }) is not { } path)
                    continue;
                var anim = AnimReader.LoadAsync(path, CancellationToken.None).GetAwaiter().GetResult();
                if (anim is null || anim.Notetracks.Count == 0 || anim.Frames.Count < 2)
                    continue;
                var last = anim.Frames.Count - 1;
                var markers = GdtNotetracks.Export(anim.Notetracks, last)
                    .Concat(GdtNotetracks.Read(record.ScanProperties, anim.Notetracks, last))
                    .Select(m => m.IsSound ? m with { SoundState = index.Resolve(m.SoundAlias!).State } : m)
                    .OrderBy(m => m.Frame).ToList();
                Console.WriteLine($"  {name} ({last + 1} frames):");
                foreach (var m in markers)
                    Console.WriteLine("    " + m.Describe() + (m.SoundState == NotetrackSoundState.Playable ? "  ·  playable" : ""));
                Check($"notetracks (live): {name} shows {markers.Count(m => m.Source == NotetrackSource.Export)} exported notetracks and "
                      + $"{markers.Count(m => m.Source == NotetrackSource.Gdt)} GDT actions ({markers.Count(m => m.SoundState == NotetrackSoundState.Playable)} playable sounds)",
                    markers.Count > 0);
                break;
            }
        }
        finally
        {
            window.Close();
        }
    }
}
