using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.VisualTree;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Apex.Editor.Controls;
using Apex.Editor.Services.Extensions;
using Apex.Editor.Services.Extensions.Simulation;
using Apex.Editor.ViewModels;
using Apex.Editor.Views;
using K = Avalonia.Input.Key;

namespace Apex.Shots;

/// <summary>
/// The recoil preview pass: what the spray overlay and its readout measure (exact, from scripted frames), how it draws
/// and that it draws nothing at rest, the module question asked in place (the broker, its words, what a changed module
/// says), and a module's notes said in the form's labels.
/// </summary>
public partial class Program
{
    private static SimulatorFrame View(float pitch, float yaw, SimulatorParts parts = SimulatorParts.All) =>
        new(true, parts, new Vector3(pitch, yaw, 0), default, default, default, "");

    // ═══ What the overlay and readout measure ═════════════════════════════

    private static void RecoilTraceChecks()
    {
        const float dt = 0.01f;
        static bool Near(float a, float b, float eps = 1e-4f) => MathF.Abs(a - b) <= eps;

        // One tap on a spring: up and a little left, back, and resting within 0.05° from the fifth step on.
        var t = new SprayTrace();
        t.Add(dt, 1, View(-1.0f, 0.1f));
        t.Add(dt, 0, View(-2.0f, 0.3f));
        t.Add(dt, 0, View(-1.5f, 0.2f));
        t.Add(dt, 0, View(-0.5f, 0.05f));
        var settledAfter = -1;
        for (var i = 0; i < 11; i++)
        {
            t.Add(dt, 0, View(-0.04f, 0f));
            if (settledAfter < 0 && t.SettleMs is not null)
                settledAfter = i;
        }
        Check($"recoil overlay: one tap is one dot, where its kick peaked (farthest from rest) ({t.DotCount} dots, {(t.DotCount > 0 ? t.Dot(0) : default)})",
            t.DotCount == 1 && t.Dot(0) == new Vector2(-2.0f, 0.3f) && t.Shots == 1 && t.HasBurst);
        Check($"recoil overlay: climb is how far up it went, drift the farthest sideways, signed left ({t.Climb}, {t.Drift})",
            Near(t.Climb, 2.0f) && Near(t.Drift, 0.3f));
        Check($"recoil overlay: settle time runs from the round to the first sample that stays within {SprayTrace.SettleDegrees}° for {SprayTrace.SettleHoldSeconds} s ({t.SettleMs} ms, known on step {settledAfter + 5})",
            t.SettleMs is { } ms && Near(ms, 50f, 0.01f) && settledAfter == 10 && !t.DoesNotReturn);
        Check($"recoil overlay: the path is the view's samples, a resting view adding none ({t.TrailCount} points)",
            t.TrailCount == 5 && t.TrailPoint(0) == new Vector2(-1.0f, 0.1f) && t.TrailPoint(4) == new Vector2(-0.04f, 0f));
        Check($"recoil overlay: the readout's texts ({WeaponPreviewViewModel.Degrees(t.Climb)}, {WeaponPreviewViewModel.DriftOf(t.Drift)}, {WeaponPreviewViewModel.Milliseconds(t.SettleMs ?? 0)})",
            WeaponPreviewViewModel.Degrees(t.Climb) == "2.0°" && WeaponPreviewViewModel.DriftOf(t.Drift) == "0.3° left"
            && WeaponPreviewViewModel.DriftOf(-0.84f) == "0.8° right" && WeaponPreviewViewModel.DriftOf(0.04f) == "0.0°"
            && WeaponPreviewViewModel.Milliseconds(t.SettleMs ?? 0) == "50 ms" && WeaponPreviewViewModel.Degrees(-1f) == "0.0°"
            && ReferenceEquals(WeaponPreviewViewModel.Degrees(3.24f), WeaponPreviewViewModel.Degrees(3.2f)));

        // Settled, the next round is a new burst: the last one is cleared.
        t.Add(dt, 1, View(-0.8f, 0f));
        Check($"recoil overlay: a round after the view settled starts a new burst ({t.DotCount} dots, {t.Shots} shots, settle {t.SettleMs?.ToString() ?? "pending"})",
            t.DotCount == 1 && t.Shots == 1 && t.SettleMs is null && Near(t.Climb, 0.8f) && t.TrailCount == 1);

        // Held: a dot per round, each the peak of its own window; the motion comes to rest away from rest.
        t = new SprayTrace();
        float[] pitches = [-1f, -1.5f, -1.2f, -2f, -2.6f, -2.4f, -3f, -3.5f, -3f];
        for (var i = 0; i < pitches.Length; i++)
            t.Add(dt, i % 3 == 0 ? 1u : 0u, View(pitches[i], 0f));
        Check($"recoil overlay: held, each round's dot is the peak before the next round ({string.Join(", ", Enumerable.Range(0, t.DotCount).Select(i => t.Dot(i).X))})",
            t.DotCount == 3 && t.Dot(0).X == -1.5f && t.Dot(1).X == -2.6f && t.Dot(2).X == -3.5f && t.Shots == 3 && Near(t.Climb, 3.5f));
        t.Rest();
        Check("recoil overlay: motion that comes to rest away from rest doesn't return", t.DoesNotReturn && t.SettleMs is null);
        t.Add(dt, 1, View(-0.2f, 0f));
        Check("recoil overlay: after it came to rest the next round starts a new burst", t.DotCount == 1 && t.Shots == 1 && !t.DoesNotReturn);

        // Rounds that never move the view (no spring for the mode): it comes to rest at rest before the hold ends.
        t = new SprayTrace();
        t.Add(dt, 1, View(0f, 0f));
        t.Add(dt, 1, View(0f, 0f));
        t.Add(dt, 0, View(0f, 0f));
        t.Rest();
        Check($"recoil overlay: a burst that never left rest has settled, not 'doesn't return' ({t.SettleMs?.ToString() ?? "null"} ms, {t.DoesNotReturn})",
            !t.DoesNotReturn && t.SettleMs is { } still && Near(still, 10f, 0.01f) && t.Climb == 0);

        // Taps before it settles are one burst; two rounds in one step share their peak.
        t = new SprayTrace();
        t.Add(dt, 1, View(-1f, 0f));
        t.Add(dt, 0, View(-0.5f, 0f));
        t.Add(dt, 2, View(-1.2f, -0.4f));
        Check($"recoil overlay: a round before the view settles joins the burst, and rounds fired together share a dot's place ({t.Shots} shots, {t.DotCount} dots)",
            t.Shots == 3 && t.DotCount == 3 && t.Dot(1) == t.Dot(2) && t.Dot(1) == new Vector2(-1.2f, -0.4f) && Near(t.Drift, -0.4f));

        // No view kick: nothing measured or drawn, rounds still counted.
        t = new SprayTrace();
        t.Add(dt, 1, View(-3f, 1f, SimulatorParts.GunAngles));
        t.Add(dt, 0, View(-3f, 1f, SimulatorParts.GunAngles));
        t.Rest();
        Check("recoil overlay: a module without view angles has nothing measured or drawn, and the rounds are counted",
            !t.ViewSupported && t.DotCount == 0 && t.TrailCount == 0 && t.Shots == 1 && t.Climb == 0 && !t.DoesNotReturn);
        var failed = t.Version;
        t.Add(dt, 1, SimulatorFrame.Failed("no"));
        Check("recoil overlay: a failed step adds nothing", t.Version == failed && t.Shots == 1);
        t.Clear();
        Check("recoil overlay: Clear is nothing measured", t is { ViewSupported: true, DotCount: 0, TrailCount: 0, Shots: 0, HasBurst: false, SettleMs: null });

        // A step allocates nothing (it runs every display frame of a burst), past both buffers' ends.
        // Up to three times, idle between: until the step's code reaches its optimized tier the runtime can allocate on
        // this thread (2,400 bytes once, in a shard sharing the machine with four others, where tiering up lagged). A
        // step that allocated would do so on every pass: 20,000 of them are megabytes, never 0.
        SprayTrace trace;
        long allocated;
        var passes = new List<long>();
        double micros;
        do
        {
            if (passes.Count > 0)
                Pump(200);
            trace = new SprayTrace();
            trace.Add(dt, 1, View(-1f, 0f));
            var clock = new Stopwatch();
            var before = GC.GetAllocatedBytesForCurrentThread();
            clock.Start();
            for (var i = 0; i < 20_000; i++)
                trace.Add(dt, i % 7 == 0 ? 1u : 0u, View(-1f - i * 0.001f, MathF.Sin(i * 0.01f)));
            micros = clock.Elapsed.TotalMicroseconds / 20_000;
            allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            passes.Add(allocated);
        } while (allocated != 0 && passes.Count < 3);
        Console.WriteLine($"info  recoil overlay: a trace step takes {micros:0.000} µs (bytes allocated per pass: {string.Join(", ", passes)})");
        Check($"recoil overlay: 20,000 steps allocate nothing and keep the newest {SprayTrace.DotCapacity} dots and {SprayTrace.TrailCapacity} path points ({allocated} bytes)",
            allocated == 0 && trace.DotCount == SprayTrace.DotCapacity && trace.TrailCount == SprayTrace.TrailCapacity
            && Near(trace.Dot(trace.DotCount - 1).X, trace.TrailPoint(trace.TrailCount - 1).X, 0.01f));
    }

    /// <summary>The driver feeding the trace, as the preview does: a tap, a held trigger, hip and ADS.</summary>
    private static void RecoilTraceDriverChecks()
    {
        const float frame = 1 / 64f;
        (RecoilDriver, SprayTrace) Rig(ScriptedSimulation sim)
        {
            var d = new RecoilDriver { Simulation = sim, FireTime = 0.125f, AdsInTime = 0.25f };
            var t = new SprayTrace();
            d.Stepped += (dt, _, shots, f) => t.Add(dt, shots, f);
            return (d, t);
        }
        void Run(RecoilDriver d, SprayTrace t)
        {
            for (var i = 0; i < 64 * 20 && d.NeedsFrames; i++)
                if (!d.Step(frame))
                    t.Rest();
        }

        var (d, t) = Rig(new ScriptedSimulation());
        d.PressTrigger();
        d.ReleaseTrigger();
        Run(d, t);
        Check($"recoil overlay: through the driver, a single tap is one dot and one shot, and the spring settles ({t.DotCount}, {t.Shots}, {t.SettleMs:0} ms)",
            t.DotCount == 1 && t.Shots == 1 && t.SettleMs is > 80 and < 160 && Math.Abs(t.Dot(0).X + 1f) < 0.5f);

        var sim = new ScriptedSimulation();
        var v = 0f;
        sim.Output = (dt, ads, shots) =>
        {
            v = (v + shots) * MathF.Exp(-dt * 25f);
            return View(-v, v * ads * 0.5f);
        };
        (d, t) = Rig(sim);
        d.AimDownSights = true;
        Run(d, t);
        d.PressTrigger();
        for (var i = 0; i < 16; i++)
            d.Step(frame);
        d.ReleaseTrigger();
        Run(d, t);
        Check($"recoil overlay: in ADS the readout measures what the module gives for ADS (here a drift left, {t.Drift:0.000}°), hip the same way",
            d.Ads == 1f && t.Shots == 2 && t.DotCount == 2 && t.Drift > 0.3f && t.SettleMs is not null);
    }

    // ═══ Drawing ══════════════════════════════════════════════════════════

    private static (Window Window, RecoilOverlay Overlay) OverlayWindow(SprayTrace trace, int size = 200)
    {
        var overlay = new RecoilOverlay
        {
            Trace = trace,
            MarkBrush = Brushes.White,
            LatestBrush = Brushes.DeepSkyBlue,
            TraceBrush = new SolidColorBrush(Color.FromArgb(0x73, 0xF2, 0xF2, 0xF2)),
            OutlineBrush = Brushes.Black,
        };
        var window = new Window { Width = size, Height = size, Content = new Panel { Background = Brushes.Black, Children = { overlay } } };
        window.Show();
        Pump();
        return (window, overlay);
    }

    private static Color PixelAt(WriteableBitmap bitmap, int x, int y)
    {
        using var fb = bitmap.Lock();
        var p = fb.Address + y * fb.RowBytes + x * 4;
        var (b0, b1, b2, a) = (Marshal.ReadByte(p), Marshal.ReadByte(p, 1), Marshal.ReadByte(p, 2), Marshal.ReadByte(p, 3));
        return fb.Format == Avalonia.Platform.PixelFormat.Rgba8888 ? Color.FromArgb(a, b0, b1, b2) : Color.FromArgb(a, b2, b1, b0);
    }

    private static void RecoilOverlayDrawChecks()
    {
        var trace = new SprayTrace();
        var (window, overlay) = OverlayWindow(trace);
        try
        {
            var focal = RecoilOverlay.FocalLength(200, 65);
            Check($"recoil overlay: angles land where the first-person camera puts them (cg_fov 65 at 200 px tall: {focal:0.0} px per unit of tan)",
                Math.Abs(focal - 100 / (0.75 * Math.Tan(32.5 * Math.PI / 180))) < 1e-9
                && RecoilOverlay.Offset(-5, 0, focal).Y < 0 && RecoilOverlay.Offset(0, 5, focal).X < 0);
            trace.Add(0.01f, 1, View(-5f, 0f));
            trace.Add(0.01f, 0, View(-4f, 0f));
            Pump();
            var shot = window.CaptureRenderedFrame()!;
            var dotY = (int)Math.Round(100 + RecoilOverlay.Offset(-5, 0, focal).Y);
            var dot = PixelAt(shot, 100, dotY);
            var empty = PixelAt(shot, 160, 160);
            var arm = PixelAt(shot, 100 - 8, 100);
            Check($"recoil overlay: the dot is drawn 5° above the centre ({dotY} px: {dot}), the reticle at the centre ({arm}), nothing elsewhere ({empty})",
                dot.B > 200 && dot.G > 150 && arm.R > 200 && empty.R < 10 && empty.G < 10 && empty.B < 10);

            // At rest nothing draws; a step draws once.
            Pump(50);
            var renders = overlay.Renders;
            for (var i = 0; i < 5; i++)
            {
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Pump(16);
            }
            var atRest = overlay.Renders - renders;
            trace.Add(0.01f, 0, View(-3f, 0f));
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Pump(16);
            Check($"recoil overlay: at rest the overlay draws nothing ({atRest} draws in 5 frames); a step draws it again ({overlay.Renders - renders - atRest})",
                atRest == 0 && overlay.Renders > renders);

            // What a frame costs, drawn into a bitmap the size of the docked pane's viewport: a two-second burst at 600 rounds
            // a minute (what the preview draws while firing), and the most the overlay holds.
            window.Width = 360;
            window.Height = 760;
            Pump();
            using var target = new RenderTargetBitmap(new PixelSize(360, 760));
            double Cost(SprayTrace drawn, string what)
            {
                overlay.Trace = drawn;
                Pump();
                var times = new List<double>();
                var bytes = 0L;
                for (var i = 0; i < 60; i++)
                {
                    using var context = target.CreateDrawingContext();
                    var before = GC.GetAllocatedBytesForCurrentThread();
                    var clock = Stopwatch.StartNew();
                    overlay.Render(context);
                    times.Add(clock.Elapsed.TotalMilliseconds);
                    bytes = GC.GetAllocatedBytesForCurrentThread() - before;
                }
                times.Sort();
                Console.WriteLine($"info  recoil overlay: {what} ({drawn.TrailCount} path points, {drawn.DotCount} dots) draws in {times[30]:0.000} ms median, "
                    + $"{times[57]:0.000} ms p95, allocating {bytes} bytes");
                return times[30];
            }
            var burst = new SprayTrace();
            for (var i = 0; i < 120; i++)
                burst.Add(1 / 60f, i % 6 == 0 ? 1u : 0u, View(-0.04f * i, 0.3f * MathF.Sin(i * 0.2f)));
            var full = new SprayTrace();
            for (var i = 0; i < 4000; i++)
                full.Add(1 / 60f, i % 8 == 0 ? 1u : 0u, View(-1f - i * 0.002f, MathF.Sin(i * 0.05f)));
            var typical = Cost(burst, "a 2 s burst");
            Cost(full, "the fullest overlay");
            Gate($"recoil overlay: a burst's overlay costs a small part of a frame ({typical:0.000} ms median)", typical < 2);
        }
        finally
        {
            window.Close();
        }
    }

    // ═══ The module question, in place ════════════════════════════════════

    private static void RecoilQuestionChecks()
    {
        var request = new SimulatorConsentRequest("sim‮-fixture", "1.0\u0007", @"C:\x\sim-fixture\bin\sim‮.dll", new string('a', 64), Changed: false, 5000,
            new DateTime(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc));
        var q = new ModuleQuestion(request);
        Check($"recoil question: leads with the plain question, names the extension and the file's name, all made plain ('{q.Title}', '{q.ExtensionLine}', '{q.FileName}')",
            q.Title == "Load sim-fixture's preview module?" && q.ExtensionLine == "sim-fixture 1.0" && q.FileName == "sim.dll" && q.Change is null
            && q.Details == "C:\\x\\sim-fixture\\bin\\sim.dll\nSHA-256 " + new string('a', 64) && !q.ShowDetails);

        // The broker: in place when a preview watches for it, the dialog otherwise.
        var fallback = new ScriptedConsent(false);
        var questions = new ModuleQuestions(fallback);
        var plain = request with { ExtensionId = "x" };
        var unwatched = questions.AskAsync(plain, CancellationToken.None);
        Check("recoil question: with no preview to show it, the question goes to the dialog", fallback.Asked.Count == 1 && unwatched.IsCompleted && questions.For("x") is null);

        var watch = questions.Watch("x");
        var changes = 0;
        questions.Changed += () => changes++;
        var ask = questions.AskAsync(plain, CancellationToken.None);
        var open = questions.For("X");
        open?.LoadCommand.Execute(null);
        Check($"recoil question: watched, it is asked in place; Load answers it, and it is gone before the answer runs ({changes} changes)",
            open is not null && fallback.Asked.Count == 1 && ask.IsCompletedSuccessfully && ask.Result == true && questions.For("x") is null && changes == 2);
        open!.DontLoadCommand.Execute(null);
        Check("recoil question: an answered question can't be answered again", ask.Result == true);

        using var withdraw = new CancellationTokenSource();
        var later = questions.AskAsync(plain, withdraw.Token);
        var pending = questions.For("x");
        withdraw.Cancel();
        pending?.LoadCommand.Execute(null);
        Check("recoil question: withdrawn (every preview asking closed), it closes with no answer, and Load afterwards does nothing",
            pending is { IsOpen: false } && later.IsCompletedSuccessfully && later.Result is null && questions.For("x") is null);

        using var gone = new CancellationTokenSource();
        gone.Cancel();
        var stillborn = questions.AskAsync(plain, gone.Token);
        Check("recoil question: a question whose askers are already gone is never shown", stillborn.IsCompleted && stillborn.Result is null && questions.For("x") is null);

        watch.Dispose();
        watch.Dispose();
        questions.AskAsync(plain, CancellationToken.None);
        Check("recoil question: with its preview gone, a later question goes to the dialog again", fallback.Asked.Count == 2 && questions.For("x") is null);

        // A changed module says what changed; an answer kept by 0.2.0 has no size or time to compare.
        var previous = new SimulatorModuleBuild(new string('b', 64), 4000, new DateTime(2026, 10, 1, 9, 15, 0, DateTimeKind.Utc), new DateTime(2026, 10, 1, 10, 0, 0, DateTimeKind.Utc));
        string Local(DateTime utc) => utc.ToLocalTime().ToString("d MMM yyyy HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        var changed = new ModuleQuestion(request with { Changed = true, Previous = previous });
        var old = ModuleQuestion.ChangeOf(request with { Changed = true, Previous = previous with { Size = null, ModifiedUtc = null } });
        var during = ModuleQuestion.ChangeOf(request with { Changed = true });
        Check($"recoil question: a changed module says how: hash prefixes, size and its change, and times ('{changed.Change}')",
            changed.Title == "Load sim-fixture's changed preview module?"
            && changed.Change == $"Changed since you answered on {previous.AnsweredUtc.ToLocalTime().ToString("d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture)}: "
                + "SHA-256 aaaaaaaa (was bbbbbbbb), 4.9 KB (+1000 bytes), "
                + $"modified {Local(request.ModifiedUtc!.Value)} (was {Local(previous.ModifiedUtc!.Value)}).");
        Check($"recoil question: with nothing kept to compare, it says what it can ('{old}'; '{during}')",
            old.Contains("SHA-256 aaaaaaaa (was bbbbbbbb), 4.9 KB, modified") && !old.Contains("(was " + Local(previous.ModifiedUtc!.Value))
            && during == $"The file changed while Apex was loading it: SHA-256 aaaaaaaa, 4.9 KB, modified {Local(request.ModifiedUtc!.Value)}.");
    }

    // ═══ In the app (called from RecoilAppChecks, on its temp install) ════════

    /// <summary>Answers the question in <paramref name="recoil"/>'s preview with real keys: into the preview, then Esc (Don't load) or Tab to Load and Enter.</summary>
    private static void AnswerQuestion(Window window, WeaponPreviewViewModel recoil, bool load)
    {
        WaitUntil(() => recoil.Question is not null, 3000);
        RecoilRoot(window, recoil).Focus();
        Pump();
        if (!load)
        {
            Key(window, K.Escape);
            return;
        }
        for (var i = 0; i < 12 && (window.FocusManager?.GetFocusedElement() as Control)?.Name != "LoadButton"; i++)
            Key(window, K.Tab);
        Key(window, K.Enter);
    }

    private static bool InspectorShown(Window window) =>
        window.GetVisualDescendants().OfType<Border>().First(b => b.Name == "InspectorLayer").IsVisible;

    /// <summary>The recoil preview takes the right column's full height; the viewport keeps its minimum; the overlay fills its fill (inset a pixel inside the frame's edge).</summary>
    private static void RecoilPaneSizeCheck(MainWindow window, WeaponPreviewViewModel recoil)
    {
        window.UpdateLayout();
        var preview = window.GetVisualDescendants().OfType<Border>().First(b => b.Name == "PreviewLayer");
        var stack = window.GetVisualDescendants().OfType<Grid>().First(g => g.Name == "RightStack");
        var overlay = Named<RecoilOverlay>(window, "RecoilOverlay");
        var viewport = overlay.GetVisualAncestors().OfType<Border>().First(b => b.Classes.Contains("viewport"));
        Check($"recoil pane: docked, the recoil preview takes the right column's full height and the Inspector steps aside (preview {preview.Bounds.Height:0} of {stack.Bounds.Height:0} px; viewport {viewport.Bounds.Width:0}×{viewport.Bounds.Height:0})",
            !InspectorShown(window) && Math.Abs(preview.Bounds.Height - stack.Bounds.Height) < 1 && viewport.Bounds.Height >= 160
            && Math.Abs(overlay.Bounds.Height - viewport.Bounds.Height) < 1);
    }

    /// <summary>A weapon with no gun to draw: Fire works, the overlay and readout carry the preview, the pane says why.</summary>
    private static void RecoilNoGunCheck(MainViewModel vm, MainWindow window, string outDir)
    {
        vm.OpenByName("apex_recoil_noidle");
        Pump(300);
        var noGun = (WeaponPreviewViewModel)vm.ActiveTab!.PreviewPane!.Content!;
        WaitUntil(() => noGun.CanFire, 3000);
        noGun.FireOnce();
        Frames(6);
        Check($"recoil no gun: Fire works without a viewmodel: one tap, one dot and one shot, and the pane says why there's no gun ('{noGun.Error}', {noGun.Trace.DotCount} dots, '{noGun.ShotsText}')",
            noGun is { CanFire: true, HasPlan: false, ShowError: true, ShotsText: "1", Question: null } && noGun.Trace.DotCount == 1 && noGun.ClimbText != WeaponPreviewViewModel.NoValue
            && Named<Border>(window, "RecoilError").IsEffectivelyVisible && Named<RecoilOverlay>(window, "RecoilOverlay").IsEffectivelyVisible);
        noGun.PressTrigger();
        Frames(12, 25);
        noGun.ReleaseTrigger();
        Frames(2);
        Capture(window, Path.Combine(outDir, "96-recoil-no-gun.png"));
        Avalonia.Application.Current!.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
        Pump(100);
        Capture(window, Path.Combine(outDir, "97-recoil-no-gun-light.png"));
        Avalonia.Application.Current.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
        Pump();
        vm.OpenByName("apex_recoil_base");
        Pump(300);
    }

    /// <summary>ADS: the module is given the ADS fraction and the readout measures what comes back (the fixture's yaw is the fraction).</summary>
    private static void RecoilAdsReadoutCheck(MainWindow window, WeaponPreviewViewModel recoil, string outDir)
    {
        RecoilRoot(window, recoil).Focus();
        Pump();
        Key(window, K.A);
        Frames(30);
        Key(window, K.R);
        Key(window, K.Space);
        Frames(4);
        Check($"recoil ADS: in ADS a tap is measured too: the fixture turns the view left by the ADS fraction ('{recoil.DriftText}', '{recoil.ShotsText}')",
            recoil.IsAds && recoil.Driver.Ads == 1f && recoil.DriftText == "1.0° left" && recoil.ShotsText == "1" && recoil.Trace.DotCount == 1);
        window.KeyPress(K.Space, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.Space, " ");
        Frames(10, 25);
        window.KeyRelease(K.Space, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.Space, " ");
        Frames(2);
        Capture(window, Path.Combine(outDir, "98-recoil-ads.png"));
        Avalonia.Application.Current!.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
        Pump(100);
        Capture(window, Path.Combine(outDir, "99-recoil-ads-light.png"));
        Avalonia.Application.Current.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
        Pump();
        Key(window, K.A);
        Frames(4);
        Key(window, K.R);
    }

    /// <summary>A module that doesn't compute view angles: the readout says so instead of showing blanks.</summary>
    private static void RecoilNoViewKickCheck(MainWindow window, WeaponPreviewViewModel recoil, string outDir)
    {
        var real = recoil.Driver.Simulation;
        recoil.Driver.Simulation = new ScriptedSimulation { Output = (_, _, _) => new SimulatorFrame(true, SimulatorParts.GunAngles, default, default, new Vector3(-2, 0, 0), default, "") };
        recoil.Trace.Clear();
        recoil.FireOnce();
        Frames(4);
        Check($"recoil readout: with no view kick from the module it says there's nothing to measure, not blanks ('{recoil.MeasureNote}', shots '{recoil.ShotsText}')",
            recoil.MeasureNote == WeaponPreviewViewModel.NoViewKick && recoil.ShotsText == "1" && recoil.Trace.DotCount == 0
            && Named<TextBlock>(window, "MeasureNote").Text == WeaponPreviewViewModel.NoViewKick
            && !window.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Name == "ClimbValue" && t.IsEffectivelyVisible));
        Capture(window, Path.Combine(outDir, "100-recoil-no-view-kick.png"));
        recoil.Driver.Simulation = real;
        recoil.ResetCommand.Execute(null);
        Frames(2);
    }

    /// <summary>A rebuilt module (new hash) is asked about again, in place, saying what changed.</summary>
    private static void RecoilChangedModuleCheck((string Install, string Ext, string Settings, string Dll) setup, string outDir)
    {
        var saved = (Environment.GetEnvironmentVariable("APEX_FORCE_MOCK"), Environment.GetEnvironmentVariable("APEX_BO3_ROOT"),
            Environment.GetEnvironmentVariable("APEX_NO_PERSIST"));
        MainViewModel? vm = null;
        MainWindow? window = null;
        try
        {
            File.AppendAllText(setup.Dll, "rebuilt");
            (vm, window) = StartRecoilApp(setup)!.Value;
            vm.OpenByName("apex_recoil_noidle");
            Pump(300);
            var recoil = (WeaponPreviewViewModel)vm.ActiveTab!.PreviewPane!.Content!;
            WaitUntil(() => recoil.Question is not null, 3000);
            Pump(50);
            var q = recoil.Question;
            Check($"recoil changed module: a rebuilt module is asked about again in place, saying what changed ('{q?.Change}')",
                q is { Title: "Load sim-fixture's changed preview module?" } && q.Change!.StartsWith("Changed since you answered on ")
                && q.Change.Contains("(+7 bytes)") && q.Change.Contains($"SHA-256 {Sha(setup.Dll)[..8]} (was ")
                && Named<TextBlock>(window, "ModuleQuestionChange").IsEffectivelyVisible && !vm.IsConfirmOpen);
            Click(window, Named<ToggleButton>(window, "ModuleQuestionDetails"), Avalonia.Input.MouseButton.Left);
            Check("recoil changed module: Details shows the full path and hash", q!.ShowDetails && q.Details.Contains(setup.Dll) && q.Details.Contains(Sha(setup.Dll)));
            Capture(window, Path.Combine(outDir, "101-recoil-changed-module.png"));
            Avalonia.Application.Current!.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
            Pump(100);
            Capture(window, Path.Combine(outDir, "102-recoil-changed-module-light.png"));
            Avalonia.Application.Current.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
            Pump();
            AnswerQuestion(window, recoil, load: false);
            Pump(100);
            Check("recoil changed module: Esc in the preview is Don't load", recoil.Question is null && recoil.CanLoadModule && !IsLoaded(setup.Dll));
        }
        catch (Exception ex)
        {
            Check($"recoil changed module: {ex}", false);
        }
        finally
        {
            StopRecoilApp(vm, window, saved);
        }
    }

    // ═══ Notes in the form's words ════════════════════════════════════════

    /// <summary>A manifest with weapon-tech's shape (keys, labels, tables), so its module's notes can be said in its labels.</summary>
    private const string NoteManifest = """
        { "apexSchema": 1, "id": "wt-notes", "version": "1", "targets": ["weapon"], "enabledBy": "wtEnabled",
          "sections": [
            { "title": "Weapon tech", "fields": [ { "key": "wtEnabled", "kind": "toggle", "label": "Enabled" }, { "key": "wtRecoil", "kind": "toggle", "label": "Kick and offsets" } ] },
            { "title": "Recoil & Kick", "fields": [
                { "key": "wtFireTimeMs", "kind": "number", "label": "Fire time (ms)" },
                { "key": "wtKickPct", "kind": "text", "label": "Kick percent" },
                { "key": "wtSpringViewHip", "kind": "text", "label": "Spring: view, hip" }, { "key": "wtSpringViewAds", "kind": "text", "label": "Spring: view, ADS" },
                { "key": "wtSpringGunHip", "kind": "text", "label": "Spring: gun, hip" }, { "key": "wtSpringGunAds", "kind": "text", "label": "Spring: gun, ADS" },
                { "key": "wtKickReturn", "kind": "text", "label": "Kick return" } ],
              "records": [ { "key": "wtKick#", "label": "Kick sets", "columns": [ { "name": "a", "kind": "number" } ] } ] },
            { "title": "Offset patterns", "fields": [ { "key": "wtWopCurveKick", "kind": "text", "label": "Curve 2: kick" }, { "key": "wtWopCurveAds", "kind": "text", "label": "Curve 4: ADS" } ],
              "records": [ { "key": "wtWop#", "label": "Offset patterns", "columns": [ { "name": "a", "kind": "number" } ] } ] },
            { "title": "Sway", "fields": [ { "key": "wtSwayIdle1", "kind": "text", "label": "Idle 1" } ] } ] }
        """;

    private static void RecoilNoteLabelChecks()
    {
        var dir = NewScratch("recoil-note-labels");
        File.WriteAllText(Path.Combine(dir, ExtensionLoader.FileName), NoteManifest);
        var m = ExtensionLoader.Read(Path.Combine(dir, ExtensionLoader.FileName), new List<ExtensionDiagnostic>())!;
        var cases = new (string Note, string Said)[]
        {
            ("no wtSpringGun*: BO3's own gun kick isn't simulated", "no “Spring: gun”: BO3's own gun kick isn't simulated"),
            ("no wtSpring* keys: BO3's own view and gun kick aren't simulated", "no “Spring” keys: BO3's own view and gun kick aren't simulated"),
            ("this weapon has no kick or offset-pattern keys (wtKick*, wtWop*) to preview", "this weapon has no kick or offset-pattern keys (“Kick sets”, “Offset patterns”) to preview"),
            ("wtRecoil is off, so this weapon has no weapon-tech kick to preview", "“Kick and offsets” is off, so this weapon has no weapon-tech kick to preview"),
            ("wtKick3 can't be read, skipped as in game", "“Kick sets row 3” can't be read, skipped as in game"),
            ("wtKickReturn/Maintain can't be read", "“Kick return”/Maintain can't be read"),
            ("wtWopCurve* look odd; wtSway* too", "“Curve” look odd; “Idle 1” too"),
            ("noise patterns use a fixed seed (in game they vary per burst)", "noise patterns use a fixed seed (in game they vary per burst)"),
            ("wtNoSuchKey and wtZz* stay as written", "wtNoSuchKey and wtZz* stay as written"),
        };
        var wrong = cases.Select(c => (c.Said, Got: ExtensionKeyText.ToLabels(c.Note, m))).Where(c => c.Got != c.Said).ToList();
        Check($"recoil notes: a module's keys are said as the form labels them, longest token first, stems by the table or field that owns them ({(wrong.Count == 0 ? "all" : string.Join(" | ", wrong.Select(w => $"got '{w.Got}'")))})",
            wrong.Count == 0);
        var unchanged = "no keys here";
        Check("recoil notes: a note with no keys is the same string", ReferenceEquals(ExtensionKeyText.ToLabels(unchanged, m), unchanged));

        // The real extension's notes as its manifest labels them today (for weapon-tech's own rewrite; nothing asserted).
        const string real = @"J:\Github\weapon-tech-sim\kit\apex\extension.json";
        if (File.Exists(real) && ExtensionLoader.Read(real, new List<ExtensionDiagnostic>()) is { } wt)
            foreach (var note in new[]
                     {
                         "wtRecoil is off, so this weapon has no weapon-tech kick to preview",
                         "this weapon has no kick or offset-pattern keys (wtKick*, wtWop*) to preview",
                         "this weapon has kick sets but no wtSpring* or wtWop* keys: its kick is BO3's own, which isn't simulated",
                         "no wtSpring* keys: BO3's own view and gun kick aren't simulated",
                         "no wtSpringView*: BO3's own view kick isn't simulated",
                         "no wtSpringGun*: BO3's own gun kick isn't simulated",
                         "wtKickReturn/Maintain can't be read, skipped as in game",
                     })
                Console.WriteLine($"info  recoil notes (weapon-tech {wt.Version}): '{note}' → '{ExtensionKeyText.ToLabels(note, wt)}'");
    }
}
