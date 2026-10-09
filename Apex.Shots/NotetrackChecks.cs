using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Styling;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Apex.Editor.Controls;
using Apex.Editor.Models;
using Apex.Editor.Services.Gdt;
using Apex.Editor.Services.Preview.Notetracks;
using Apex.Editor.ViewModels;
using K = Avalonia.Input.Key;

namespace Apex.Shots;

/// <summary>
/// The notetrack timeline under the xanim preview: markers from the xanim's exported notetracks and from the GDT's
/// notetrack actions (keys as deffiles/xanim.awi lays them out), the sound alias index on a fixture install, and the
/// timeline itself driven with real pointer and key input in a window, listening through a fake sound output.
/// </summary>
public partial class Program
{
    /// <summary>The sound output the harness listens through: what was played, stopped and released.</summary>
    private sealed class FakeAudio : INotetrackAudio
    {
        public List<(string Path, float Volume)> Played { get; } = new();
        public int Stops { get; private set; }
        public bool Disposed { get; private set; }
        public void Play(string path, float volume) => Played.Add((path, volume));
        public void StopAll() => Stops++;
        public void Dispose() => Disposed = true;
        public event Action<string>? Undecodable;
        public void Fail(string path) => Undecodable?.Invoke(path);
    }

    /// <summary>Stands in for the anim preview: its frame drives the timeline as AnimPreviewViewModel's does.</summary>
    private sealed partial class TimelineHost : ObservableObject
    {
        public TimelineHost(NotetrackTimeline timeline)
        {
            Timeline = timeline;
            timeline.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(NotetrackTimeline.LastFrameIndex))
                    OnPropertyChanged(nameof(LastFrame));
            };
        }

        public NotetrackTimeline Timeline { get; }

        [ObservableProperty]
        private double _currentFrame;

        public int LastFrame => Timeline.LastFrameIndex;

        partial void OnCurrentFrameChanged(double value) => Timeline.FrameChanged(value, fromClock: false);
    }

    private static void RunNotetrackChecks(string outDir)
    {
        var root = Path.Combine(Path.GetTempPath(), "apex-shots-notetracks-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var index = FixtureAliases(root);
            NotetrackMarkerChecks();
            AliasIndexChecks(index, root);
            TimelineInputChecks(index, outDir);
            NotetrackAuditChecks(index, root);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    // ── Fixture: an install with one alias CSV, a shadowing backup copy and a few WAVs ──

    private static SoundAliasIndex FixtureAliases(string root)
    {
        var aliases = Path.Combine(root, "share", "raw", "sound", "aliases");
        var assets = Path.Combine(root, "sound_assets");
        Directory.CreateDirectory(Path.Combine(aliases, "backup"));
        Directory.CreateDirectory(Path.Combine(assets, "fixture"));
        const string header = "Name,Behavior,Storage,FileSpec,FileSpecSustain,FileSpecRelease,Template,Loadspec,Secondary,SustainAlias,"
                              + "ReleaseAlias,Bus,VolumeGroup,DuckGroup,Duck,ReverbSend,CenterSend,VolMin,VolMax,DistMin,PitchMin,PitchMax";
        static string Row(string name, string spec, string volMin, string volMax) =>
            $"{name},,,{spec},,,,,,,,BUS_FX,,,,,,{volMin},{volMax},,,";
        File.WriteAllText(Path.Combine(aliases, "fixture.csv"), string.Join("\r\n", new[]
        {
            header,
            "# a comment row",
            Row("snd_a", @"fixture\a1.wav", "50", "50"),
            Row("snd_c", @"fixture\c.wav", "", ""),
            Row("snd_a", @"fixture\a2.wav", "80", "100"),
            Row("snd_missing_file", @"fixture\not_there.wav", "100", "100"),
            "\"snd_quoted\",,,\"fixture\\c.wav\",,,,,,,,BUS_FX,,,,,,90,90,,,",
        }) + "\r\n");
        // A copy in a subfolder never shadows the live file.
        File.WriteAllText(Path.Combine(aliases, "backup", "fixture.csv"), header + "\r\n" + Row("snd_a", @"fixture\old.wav", "10", "10") + "\r\n");
        foreach (var wav in new[] { "a1.wav", "a2.wav", "c.wav", "old.wav" })
            WriteWav(Path.Combine(assets, "fixture", wav));
        // Excel saves CSVs with a UTF-8 byte order mark; 20 of the install's alias files have one.
        File.WriteAllText(Path.Combine(aliases, "bom.csv"), header + "\r\n" + Row("snd_bom", @"fixture\c.wav", "", "") + "\r\n"
            + Row("snd_mp3", @"fixture\x.mp3", "", "") + "\r\n" + Row("snd_escape", @"..\..\outside.wav", "", "") + "\r\n",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        File.WriteAllBytes(Path.Combine(assets, "fixture", "x.mp3"), new byte[64]);
        WriteWav(Path.Combine(root, "outside.wav"));
        return new SoundAliasIndex(aliases, assets);
    }

    /// <summary>A tenth of a second of silence: 16-bit mono PCM at 22.05 kHz.</summary>
    private static void WriteWav(string path)
    {
        const int rate = 22050, samples = rate / 10;
        using var w = new BinaryWriter(File.Create(path));
        w.Write(Encoding.ASCII.GetBytes("RIFF"));
        w.Write(36 + samples * 2);
        w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        w.Write(16);
        w.Write((short)1);
        w.Write((short)1);
        w.Write(rate);
        w.Write(rate * 2);
        w.Write((short)2);
        w.Write((short)16);
        w.Write(Encoding.ASCII.GetBytes("data"));
        w.Write(samples * 2);
        w.Write(new byte[samples * 2]);
    }

    private const int FixtureLastFrame = 30;

    private static readonly (string Name, int Frame)[] FixtureExportNotes = { ("fire", 7), ("end_note", 30) };

    /// <summary>A GDT entry's notetrack keys, shaped as xanim.awi saves them.</summary>
    private static Dictionary<string, string> FixtureNotetrackFields() => new(StringComparer.Ordinal)
    {
        ["customnote0action"] = "Sound", ["customnote0actionparam1"] = "snd_a", ["customnote0frame"] = "12",
        ["customnote0useexistingnote"] = "",
        ["customnote1action"] = "Sound", ["customnote1actionparam1"] = "snd_c", ["customnote1frame"] = "14",
        ["customnote2action"] = "Rumble", ["customnote2actionparam1"] = "reload_medium", ["customnote2frame"] = "12",
        ["customnote3action"] = "Sound", ["customnote3actionparam1"] = "snd_missing_file", ["customnote3frame"] = "22",
        ["customnote4action"] = "2D Sound", ["customnote4actionparam1"] = "snd_unknown", ["customnote4frame"] = "25",
        // Piggybacks on the exported "fire" notetrack: its own frame is ignored.
        ["customnote5action"] = "Self Notify", ["customnote5actionparam1"] = "shell_out", ["customnote5frame"] = "3",
        ["customnote5useexistingnote"] = "fire",
        ["customnote6action"] = "None", ["customnote6frame"] = "9",
        ["startupnote0action"] = "2D Sound", ["startupnote0actionparam1"] = "snd_a",
        ["shutdownnote0action"] = "Rumble", ["shutdownnote0actionparam1"] = "reload_large",
        ["sound_customnote0action"] = "Sound", ["sound_customnote0actionparam1"] = "snd_quoted", ["sound_customnote0frame"] = "18",
        ["filename"] = @"fixture\vm_fixture.xanim_bin",
    };

    private static List<NotetrackMarker> FixtureMarkers() =>
        GdtNotetracks.Export(FixtureExportNotes, FixtureLastFrame)
            .Concat(GdtNotetracks.Read(FixtureNotetrackFields(), FixtureExportNotes, FixtureLastFrame)).ToList();

    private static void NotetrackMarkerChecks()
    {
        var markers = FixtureMarkers();
        string Placed(Func<NotetrackMarker, bool> which) =>
            string.Join(",", markers.Where(which).Select(m => $"{m.Label}@{m.Frame}"));

        var export = Placed(m => m.Source == NotetrackSource.Export);
        Check($"notetracks: exported notetracks sit on their frames ({export})", export == "fire@7,end_note@30");

        var gdt = Placed(m => m.Source == NotetrackSource.Gdt);
        const string expected = "Note 1@12,Note 2@14,Note 3@12,Note 4@22,Note 5@25,Note 6@7,Startup note 1@0,Shutdown note 1@30,Sound note 1@18";
        Check($"notetracks: GDT actions placed from <id>frame, useexistingnote, startup (first frame) and shutdown (last frame); None skipped ({gdt})",
            gdt == expected);

        var sounds = string.Join(",", markers.Where(m => m.IsSound).Select(m => m.SoundAlias));
        Check($"notetracks: Sound, 2D Sound actions are sound markers with their alias, Rumble and Self Notify are not ({sounds})",
            sounds == "snd_a,snd_c,snd_missing_file,snd_unknown,snd_a,snd_quoted"
            && markers.Single(m => m.Label == "Note 6").Describe().Contains("Self Notify · shell_out"));
    }

    private static void AliasIndexChecks(SoundAliasIndex index, string root)
    {
        var clock = Stopwatch.StartNew();
        index.ReadyAsync().Wait();
        var a = index.Resolve("snd_a");
        var files = string.Join(",", a.Variants.Select(v => Path.GetFileName(v.Path)).OrderBy(f => f));
        Check($"aliases: rows sharing a name are variants, a copy in a subfolder is ignored ({a.State}: {files}; {index.Stats.Names} names, {index.Stats.Files} files, {clock.ElapsedMilliseconds} ms)",
            a.State == NotetrackSoundState.Playable && files == "a1.wav,a2.wav" && index.Stats.Files == 3);
        var a1 = a.Variants.Single(v => v.Path.EndsWith("a1.wav"));
        var a2 = a.Variants.Single(v => v.Path.EndsWith("a2.wav"));
        Check($"aliases: volumes come from VolMin / VolMax as 0..100 (a1 {a1.VolumeMin}-{a1.VolumeMax}, a2 {a2.VolumeMin}-{a2.VolumeMax}); blank is full",
            a1.VolumeMin == 0.5f && a1.VolumeMax == 0.5f && a2.VolumeMin == 0.8f && a2.VolumeMax == 1f
            && index.Resolve("snd_c").Variants.Single().VolumeMax == 1f);
        Check("aliases: a quoted row reads, and names match regardless of case",
            index.Resolve("snd_quoted").State == NotetrackSoundState.Playable && index.Resolve("SND_A").Variants.Count == 2);
        Check($"aliases: an unknown name and a row without its raw file are told apart ({index.Resolve("snd_unknown").State}, {index.Resolve("snd_missing_file").State})",
            index.Resolve("snd_unknown").State == NotetrackSoundState.UnknownAlias
            && index.Resolve("snd_missing_file").State == NotetrackSoundState.NoRawFile);

        // Picking: random among the variants, the volume drawn inside the row's range.
        var audio = new FakeAudio();
        var timeline = new NotetrackTimeline(() => audio, new Random(7));
        Pump(timeline.Load(new[] { new NotetrackMarker(5, "Note 1", NotetrackSource.Gdt, "Sound", "snd_a") { SoundState = NotetrackSoundState.Pending } }, 10, index));
        for (var i = 0; i < 40; i++)
        {
            timeline.FrameChanged(5, fromClock: false); // a jump onto the marker's frame plays it
            timeline.FrameChanged(0, fromClock: false);
        }
        var picked = audio.Played.GroupBy(p => Path.GetFileName(p.Path)).ToDictionary(g => g.Key, g => g.ToList());
        var inRange = audio.Played.All(p => p.Path.EndsWith("a1.wav") ? p.Volume == 0.5f : p.Volume is >= 0.8f and <= 1f);
        Check($"aliases: each play picks one variant at random ({string.Join(", ", picked.Select(p => $"{p.Key} ×{p.Value.Count}"))}), volume within its row ({inRange})",
            audio.Played.Count == 40 && picked.Count == 2 && inRange);
        timeline.Dispose();
        Check("audio: closing the timeline releases the sound output", audio.Disposed);
    }

    /// <summary>Runs the UI thread until <paramref name="task"/> is done (alias lookups post back to it).</summary>
    private static void Pump(Task task)
    {
        var clock = Stopwatch.StartNew();
        while (!task.IsCompleted && clock.ElapsedMilliseconds < 10_000)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(1);
        }
        Dispatcher.UIThread.RunJobs();
    }

    private static void TimelineInputChecks(SoundAliasIndex index, string outDir)
    {
        var audio = new FakeAudio();
        var timeline = new NotetrackTimeline(() => audio, new Random(3));
        var host = new TimelineHost(timeline);
        // As in the preview's transport: the strip under the slider's track, its lane labels in the gutter to its left.
        var bar = new NotetrackTimelineBar { Margin = new Thickness(80, 2, 20, 0) };
        bar.Bind(NotetrackTimelineBar.TimelineProperty, new Avalonia.Data.Binding(nameof(TimelineHost.Timeline)));
        bar.Bind(NotetrackTimelineBar.FrameProperty, new Avalonia.Data.Binding(nameof(TimelineHost.CurrentFrame)) { Mode = Avalonia.Data.BindingMode.TwoWay });
        var slider = new Slider { Minimum = 0, Margin = new Thickness(70, 0, 10, 0) };
        slider.Bind(Slider.MaximumProperty, new Avalonia.Data.Binding(nameof(TimelineHost.LastFrame)));
        slider.Bind(Slider.ValueProperty, new Avalonia.Data.Binding(nameof(TimelineHost.CurrentFrame)) { Mode = Avalonia.Data.BindingMode.TwoWay });
        var panel = new StackPanel { Children = { slider, bar }, Margin = new Thickness(12) };
        var window = new Window { Width = 680, Height = 110, DataContext = host, Content = panel };
        window.Bind(Window.BackgroundProperty, window.GetResourceObservable("BgPaneBrush"));
        window.Show();
        window.Activate();

        var load = timeline.Load(FixtureMarkers(), FixtureLastFrame, index);
        var pendingAtOpen = timeline.Markers.Count(m => m.SoundState == NotetrackSoundState.Pending);
        Pump(load);
        Settle(window);
        var states = string.Join(",", timeline.Markers.Where(m => m.IsSound).Select(m => $"{m.SoundAlias}:{m.SoundState}"));
        Check($"timeline: sound markers open pending ({pendingAtOpen}) and resolve off the UI thread ({states})",
            pendingAtOpen == 6 && states == "snd_a:Playable,snd_a:Playable,snd_c:Playable,snd_quoted:Playable,snd_missing_file:NoRawFile,snd_unknown:UnknownAlias");

        Point At(double frame, int lane) =>
            bar.TranslatePoint(new Point(frame / FixtureLastFrame * bar.Bounds.Width, lane == 0 ? 6 : 22), window)!.Value;
        void Move(Point p)
        {
            // Hit testing reads the last rendered scene; earlier checks may have silenced the headless render timer.
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            RawInput.Send(window, "MouseMove", p, RawInputModifiers.None);
        }
        void Down(Point p) => RawInput.Send(window, "MouseDown", p, MouseButton.Left, RawInputModifiers.None);
        void Up(Point p) => RawInput.Send(window, "MouseUp", p, MouseButton.Left, RawInputModifiers.None);
        string Tip()
        {
            // Read, then close: a headless popup lands over the window and would take the next pointer event.
            var tip = ToolTip.GetTip(bar) as string ?? "";
            ToolTip.SetIsOpen(bar, false);
            return tip;
        }
        string Heard(int from) => string.Join(",", audio.Played.Skip(from).Select(p => Path.GetFileNameWithoutExtension(p.Path)));

        // ── Placement, seen through hovering: each lane's marker is where its frame is ──
        Move(new Point(2, 2)); // the pointer enters the window
        Settle(window);
        Move(At(7, 0));
        Settle(window);
        var exportTip = Tip();
        Move(At(7, 1));
        Settle(window);
        var gdtTip = Tip();
        Move(At(22, 1));
        Settle(window);
        var silentTip = Tip();
        Move(At(12, 1));
        Settle(window);
        var stackTip = Tip();
        Check($"timeline: hovering names the marker, its frame and action ('{exportTip}' / '{gdtTip}')",
            exportTip.StartsWith("fire  ·  frame 7") && gdtTip.Contains("Self Notify · shell_out  ·  frame 7  ·  Note 6"));
        Check($"timeline: markers on one frame stack and list together ({stackTip.Replace(Environment.NewLine, " | ")})",
            stackTip.Split(Environment.NewLine).Length == 2 && stackTip.Contains("Sound · snd_a") && stackTip.Contains("Rumble · reload_medium"));
        Check($"timeline: a sound that cannot play says why ('{silentTip}')", silentTip.Contains("silent: no raw file"));
        Capture(window, Path.Combine(outDir, "40-notetrack-timeline-dark.png"));

        // ── Click to seek ──
        host.CurrentFrame = 3;
        var before = audio.Played.Count;
        Move(At(12, 1));
        Down(At(12, 1));
        Up(At(12, 1));
        Settle(window);
        Check($"timeline: clicking a marker seeks the preview to its frame ({host.CurrentFrame}) and the slider follows ({slider.Value}); it plays what is there ({Heard(before)})",
            host.CurrentFrame == 12 && slider.Value == 12 && Heard(before) is "a1" or "a2" && !timeline.IsScrubbing);
        host.CurrentFrame = 3;
        before = audio.Played.Count;
        Move(At(5, 1));
        Down(At(5, 1));
        Up(At(5, 1));
        Settle(window);
        Check($"timeline: clicking the track between markers seeks there ({host.CurrentFrame}) and plays nothing it jumped over ({Heard(before)})",
            host.CurrentFrame == 5 && audio.Played.Count == before);

        // ── Scrub: each marker at most once per drag pass ──
        host.CurrentFrame = 1;
        before = audio.Played.Count;
        Move(At(1, 1));
        Down(At(1, 1));
        for (var f = 2; f <= 28; f++)
            Move(At(f, 1));
        for (var f = 27; f >= 1; f--)
            Move(At(f, 1));
        for (var f = 2; f <= 28; f++)
            Move(At(f, 1));
        var duringDrag = timeline.IsScrubbing;
        Up(At(28, 1));
        Settle(window);
        var firstPass = Heard(before);
        Check($"timeline: a drag back and forth plays each sound marker once ({firstPass}), never the silent ones, and ends the drag on release",
            duringDrag && !timeline.IsScrubbing && audio.Played.Count - before == 3
            && firstPass.Split(',') is [var s1, "c", "c"] && s1 is "a1" or "a2" && host.CurrentFrame == 28);

        host.CurrentFrame = 1;
        before = audio.Played.Count;
        Move(At(1, 1));
        Down(At(1, 1));
        Move(At(20, 1)); // one fast move across frames 12 and 14
        Up(At(20, 1));
        Settle(window);
        Check($"timeline: a fast drag across several markers plays only the nearest, never a burst ({Heard(before)})",
            Heard(before) == "c");

        // The slider drag: the view marks it as a scrub the same way.
        host.CurrentFrame = 1;
        before = audio.Played.Count;
        timeline.IsScrubbing = true;
        for (var round = 0; round < 3; round++)
            for (var f = 1.0; f <= 16; f += 0.5)
                host.CurrentFrame = f;
        timeline.IsScrubbing = false;
        Check($"timeline: scrubbing the slider back and forth plays each marker once per drag ({Heard(before)})", audio.Played.Count - before == 2);

        // ── Keyboard: Ctrl+← → go between notes; plain ← → are the preview's (a frame step), as on the slider and the lanes ──
        host.CurrentFrame = 12;
        bar.Focus(NavigationMethod.Tab);
        before = audio.Played.Count;
        KeyStroke(window, K.Right, RawInputModifiers.Control);
        Settle(window);
        var right = host.CurrentFrame;
        KeyStroke(window, K.Left, RawInputModifiers.Control);
        KeyStroke(window, K.Left, RawInputModifiers.Control);
        Settle(window);
        var back = host.CurrentFrame;
        var plainHandled = false;
        void Probe(object? s, KeyEventArgs e) => plainHandled = e.Handled;
        window.AddHandler(InputElement.KeyDownEvent, Probe, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
        KeyStroke(window, K.Right);
        window.RemoveHandler(InputElement.KeyDownEvent, Probe);
        Check($"timeline: with keyboard focus, Ctrl+→ goes to the next note ({right}) and Ctrl+← back ({back}), playing what it lands on ({Heard(before)}); plain → is left to the preview's frame step (handled by the strip: {plainHandled})",
            bar.IsFocused && right == 14 && back == 7 && Heard(before).StartsWith("c,") && !plainHandled);
        Capture(window, Path.Combine(outDir, "41-notetrack-timeline-focus-dark.png"));

        // ── Hit area and tooltip: a note answers 12 px either side; its tip waits the usual delay and sits over it ──
        ToolTip.SetIsOpen(bar, false);
        Move(new Point(2, 2));
        Settle(window);
        Thread.Sleep(700);
        Move(At(18, 1) + new Point(10, 0));
        Settle(window);
        var openAtOnce = ToolTip.GetIsOpen(bar);
        var hitTip = ToolTip.GetTip(bar) as string ?? "";
        Pump(ToolTip.GetShowDelay(bar) + 150);
        var openLater = ToolTip.GetIsOpen(bar);
        var offset = ToolTip.GetHorizontalOffset(bar);
        var tickX = 18.0 / FixtureLastFrame * bar.Bounds.Width;
        ToolTip.SetIsOpen(bar, false);
        Move(new Point(2, 2));
        Settle(window);
        Check($"timeline: a note 10 px from the pointer still answers ('{hitTip}'); its tip waits the usual delay (open at once {openAtOnce}, after {ToolTip.GetShowDelay(bar)} ms {openLater}) and opens over the note (offset {offset:0} px, note at {tickX:0})",
            hitTip.StartsWith("Sound · snd_quoted") && !openAtOnce && openLater && Math.Abs(offset - tickX) < 12);

        // ── Playback: everything passed, across the loop; pausing silences ──
        host.CurrentFrame = 26;
        before = audio.Played.Count;
        timeline.FrameChanged(28.5, fromClock: true);
        timeline.FrameChanged(0.4, fromClock: true); // wrapped past the last frame
        timeline.FrameChanged(13.2, fromClock: true);
        Check($"timeline: playback plays the markers it passes, including the startup note across the loop ({Heard(before)})",
            Heard(before).Split(',') is [var p0, var p1] && p0 is "a1" or "a2" && p1 is "a1" or "a2");
        var stops = audio.Stops;
        timeline.Silence();
        Check("timeline: pausing silences the sounds", audio.Stops == stops + 1);

        // ── Light theme and a narrow width: stacks stay readable ──
        Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
        host.CurrentFrame = 12;
        Settle(window);
        Capture(window, Path.Combine(outDir, "42-notetrack-timeline-light.png"));
        window.Width = 210;
        Settle(window);
        Move(At(12, 1));
        Settle(window);
        var narrowTip = Tip();
        Capture(window, Path.Combine(outDir, "43-notetrack-timeline-narrow-light.png"));
        Check($"timeline: at a narrow width neighbouring markers merge into one column whose tip lists them all ({narrowTip.Split(Environment.NewLine).Length} listed)",
            narrowTip.Split(Environment.NewLine).Length >= 3);
        Application.Current.RequestedThemeVariant = ThemeVariant.Dark;

        // ── Empty ──
        timeline.Clear();
        window.Width = 680;
        Settle(window);
        Capture(window, Path.Combine(outDir, "44-notetrack-timeline-empty-dark.png"));
        window.Close();
        timeline.Dispose();
        Check("timeline: closing releases the audio output", audio.Disposed);
    }

    /// <summary>The audit's findings, each held to its fix.</summary>
    private static void NotetrackAuditChecks(SoundAliasIndex index, string root)
    {
        static string Name(string path) => Path.GetFileNameWithoutExtension(path);

        // 1: the frame a clip opens on is where the next move starts from, so the first click plays.
        var audio = new FakeAudio();
        var timeline = new NotetrackTimeline(() => audio, new Random(5));
        Pump(timeline.Load(FixtureMarkers(), FixtureLastFrame, index, currentFrame: 0));
        timeline.FrameChanged(12, fromClock: false);
        Check($"timeline: the first seek after opening plays what it lands on ({string.Join(",", audio.Played.Select(p => Name(p.Path)))})",
            audio.Played.Count == 1);

        // 2: a user move back while playing is a jump, not the loop wrapping (which would play the tail and the head).
        timeline.FrameChanged(26, fromClock: true);
        var before = audio.Played.Count;
        timeline.FrameChanged(12, fromClock: false);
        Check($"timeline: a key or click back while playing plays only where it lands, not a loop's worth ({audio.Played.Count - before} played)",
            audio.Played.Count - before == 1);
        timeline.FrameChanged(29, fromClock: true);
        before = audio.Played.Count;
        timeline.FrameChanged(1, fromClock: true);
        Check($"timeline: the clock wrapping still plays the loop's head ({audio.Played.Count - before} played)", audio.Played.Count - before == 1);

        // 13: playing from the first frame plays its markers, as the wrap onto it does.
        before = audio.Played.Count;
        timeline.FrameChanged(0, fromClock: false);
        var landed = audio.Played.Count - before;
        timeline.PlaybackStarted(0);
        Check($"timeline: Play from frame 0 plays the startup notes there ({audio.Played.Count - before - landed} played)",
            audio.Played.Count - before - landed == 1);

        // 3: notetrack keys are told apart, and a reload keeps the clip and the frame.
        var keys = new[] { "customnote3action", "customnote49frame", "sound_customnote0actionparam1", "fx_startupnote2action",
            "customnote0useexistingnote", "shutdownnote9actionparam2" };
        var notKeys = new[] { "customnote50action", "customnote3", "filename", "notetrack", "note3name", "customnoteXaction" };
        Check("timeline: GDT notetrack keys are recognised (and nothing else), so an edit to one redraws the timeline",
            keys.All(GdtNotetracks.IsNotetrackKey) && !notKeys.Any(GdtNotetracks.IsNotetrackKey));
        var fields = FixtureNotetrackFields();
        fields["customnote1frame"] = "20";
        fields["customnote6action"] = "Sound";
        fields["customnote6actionparam1"] = "snd_bom";
        fields["customnote6frame"] = "9";
        timeline.FrameChanged(8, fromClock: false);
        Pump(timeline.Reload(GdtNotetracks.Export(FixtureExportNotes, FixtureLastFrame)
            .Concat(GdtNotetracks.Read(fields, FixtureExportNotes, FixtureLastFrame)), index));
        before = audio.Played.Count;
        timeline.FrameChanged(9, fromClock: false);
        var moved = timeline.Markers.Single(m => m.Label == "Note 2").Frame;
        Check($"timeline: an edited action shows at once (Note 2 now on {moved}; the new Note 7 plays from the step onto it: {string.Join(",", audio.Played.Skip(before).Select(p => Name(p.Path)))})",
            moved == 20 && timeline.LastFrameIndex == FixtureLastFrame && audio.Played.Count - before == 1);

        // 4, 7, 18: a CSV with a byte order mark, a non-WAV raw file, a FileSpec that climbs out of sound_assets.
        Check($"aliases: a CSV saved with a byte order mark is read ({index.Resolve("snd_bom").State})",
            index.Resolve("snd_bom").State == NotetrackSoundState.Playable);
        Check($"aliases: a raw file the preview can't decode is flagged, not 'playable' ({index.Resolve("snd_mp3").State})",
            index.Resolve("snd_mp3").State == NotetrackSoundState.Undecodable);
        Check($"aliases: a FileSpec pointing outside sound_assets is refused ({index.Resolve("snd_escape").State})",
            index.Resolve("snd_escape").State == NotetrackSoundState.NoRawFile);

        // 7: a WAV the output fails to decode flags its marker.
        audio.Fail(timeline.Markers.First(m => m.SoundAlias == "snd_c").Variants[0].Path);
        Dispatcher.UIThread.RunJobs();
        var flagged = timeline.Markers.First(m => m.SoundAlias == "snd_c");
        Check($"timeline: a sound whose file fails to decode is flagged on its marker ({flagged.SoundState}: '{flagged.Describe()}')",
            flagged.SoundState == NotetrackSoundState.Undecodable && flagged.Describe().Contains("can't be decoded"));

        // Re-audit 3: the output reports a bad file once; markers built afterwards (a GDT edit, an alias resolving) keep the flag.
        Pump(timeline.Reload(GdtNotetracks.Export(FixtureExportNotes, FixtureLastFrame)
            .Concat(GdtNotetracks.Read(fields, FixtureExportNotes, FixtureLastFrame)), index));
        var rebuilt = timeline.Markers.First(m => m.SoundAlias == "snd_c");
        Check($"timeline: a file known not to decode stays flagged when the markers are rebuilt ({rebuilt.SoundState})",
            rebuilt.SoundState == NotetrackSoundState.Undecodable);

        // 14: markers replaced mid-drag (an alias resolving, a decode failing) are not heard twice in that drag.
        timeline.IsScrubbing = true;
        timeline.FrameChanged(11, fromClock: false);
        timeline.FrameChanged(12, fromClock: false);
        before = audio.Played.Count;
        var replaced = timeline.Markers;
        // One of snd_a's two files fails to decode: every marker using it is replaced by a new record mid-drag.
        audio.Fail(timeline.Markers.First(m => m.SoundAlias == "snd_a" && m.Frame == 12).Variants.First(v => v.Path.EndsWith("a1.wav")).Path);
        Dispatcher.UIThread.RunJobs();
        var wasReplaced = !ReferenceEquals(replaced, timeline.Markers);
        timeline.FrameChanged(11, fromClock: false);
        timeline.FrameChanged(12, fromClock: false);
        timeline.IsScrubbing = false;
        Check($"timeline: a drag hears a marker once even when the markers are replaced mid-drag (replaced {wasReplaced}, {audio.Played.Count - before} replayed)",
            wasReplaced && audio.Played.Count - before == 0);

        // 12: nothing opens a sound output once the timeline is closed.
        var opened = 0;
        var closed = new NotetrackTimeline(() => { opened++; return new FakeAudio(); });
        Pump(closed.Load(FixtureMarkers(), FixtureLastFrame, index));
        closed.Dispose();
        closed.FrameChanged(12, fromClock: false);
        closed.PlaybackStarted(0);
        Check($"timeline: once closed, moving the frame opens no sound output ({opened})", opened == 0);
        timeline.Dispose();

        // 5: a CSV rewritten (rows moved, an alias added) is re-read before the next lookup.
        var csv = Path.Combine(root, "share", "raw", "sound", "aliases", "fixture.csv");
        var builds = index.Builds;
        var lines = File.ReadAllLines(csv).ToList();
        lines.Insert(1, "snd_new,,,fixture\\c.wav,,,,,,,,BUS_FX,,,,,,70,70,,,");
        File.WriteAllLines(csv, lines);
        Thread.Sleep(1100); // past the index's freshness interval
        var fresh = index.Resolve("snd_new");
        var shifted = index.Resolve("snd_a");
        Check($"aliases: an alias CSV changed on disk is re-read (new alias {fresh.State}, shifted rows {shifted.Variants.Count} variants, {index.Builds - builds} rebuild)",
            fresh.State == NotetrackSoundState.Playable && shifted.Variants.Count == 2 && index.Builds == builds + 1);

        // Re-audit 4: rows moved inside the freshness window are caught by the row read back, never played as another alias.
        index.Resolve("snd_c"); // a lookup now: the next one falls inside the window
        builds = index.Builds;
        lines = File.ReadAllLines(csv).ToList();
        lines.Insert(1, "snd_shift,,,fixture\\c.wav,,,,,,,,BUS_FX,,,,,,70,70,,,");
        lines.Insert(1, "snd_shift_too,,,fixture\\c.wav,,,,,,,,BUS_FX,,,,,,70,70,,,");
        File.WriteAllLines(csv, lines);
        var movedRows = index.Resolve("snd_a");
        Check($"aliases: rows that moved within the freshness window are re-read, not misread ({string.Join(",", movedRows.Variants.Select(v => Name(v.Path)))}; {index.Builds - builds} rebuild)",
            movedRows.Variants.Select(v => Name(v.Path)).OrderBy(n => n).SequenceEqual(new[] { "a1", "a2" }) && index.Builds == builds + 1);

        // Re-audit 2: a CSV held open by another program (Excel shares it for reading) is still read, and doesn't
        // rebuild the index on every check.
        var bom = Path.Combine(root, "share", "raw", "sound", "aliases", "bom.csv");
        using (new FileStream(bom, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
        {
            File.AppendAllText(csv, "snd_held,,,fixture\\c.wav,,,,,,,,BUS_FX,,,,,,70,70,,,\r\n");
            Thread.Sleep(1100);
            var held = index.Resolve("snd_bom");
            builds = index.Builds;
            Thread.Sleep(1100);
            index.Resolve("snd_bom");
            Check($"aliases: a CSV another program holds open is still read ({held.State}) and causes no rebuild loop ({index.Builds - builds} more)",
                held.State == NotetrackSoundState.Playable && index.Builds == builds);
        }

        // Re-audit 1: the decoded-sound cache keys paths case-insensitively throughout and stays within its bounds.
        var cache = new DecodedSoundCache(budgetBytes: 3 * (DecodedSoundCache.EntryOverheadBytes + 400), maxEntries: 8);
        cache.Add(@"C:\s\Foo.wav", new float[100]);
        cache.Add(@"C:\s\foo.wav", new float[100]);
        var sameEntry = cache.Count == 1 && cache.TryGet(@"C:\S\FOO.WAV", out var foo) && foo!.Length == 100;
        Exception? thrown = null;
        try
        {
            for (var i = 0; i < 40; i++)
                cache.Add($@"C:\s\{(i % 2 == 0 ? "Bar" : "bar")}{i / 2}.wav", i % 3 == 0 ? null : new float[100]);
        }
        catch (Exception ex)
        {
            thrown = ex;
        }
        Check($"audio: the decoded-sound cache treats Foo.wav and foo.wav as one file and evicts within its bounds ({cache.Count} entries, {cache.Bytes} bytes{(thrown is null ? "" : ", " + thrown.GetType().Name)})",
            sameEntry && thrown is null && cache.Count <= 3 && cache.Bytes <= 3 * (DecodedSoundCache.EntryOverheadBytes + 400));
        var bounded = new DecodedSoundCache(budgetBytes: long.MaxValue, maxEntries: 8);
        for (var i = 0; i < 100; i++)
            bounded.Add($"bad{i}.wav", null);
        Check($"audio: files that won't decode are remembered within a bound ({bounded.Count} of 100 kept)", bounded.Count == 8);
    }
}
