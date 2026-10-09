using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Apex.Editor.ViewModels;
using Apex.Editor.Views;

namespace Apex.Shots;

/// <summary>
/// The palette on the real install (122k assets): Ctrl+P / Ctrl+Shift+P until the list is laid out, and each typed
/// letter until the list is updated and laid out. UI-thread time only: keys are raised on the focused element as the
/// keyboard device does, without the headless helpers' forced software render of the whole window (in the app that
/// happens on the render thread). Skipped without a BO3 install; the settings file is never written (the mock flag is
/// back on before anything saves).
/// </summary>
public partial class Program
{
    private static void LivePaletteTiming()
    {
        // Loading the install replaces the process-wide schemas; later checks build mock data from them.
        try { TimeLivePalette(); }
        finally { Apex.Editor.Models.SchemaRegistry.ResetToMock(); }
    }

    private static void TimeLivePalette()
    {
        var saved = Environment.GetEnvironmentVariable("APEX_FORCE_MOCK");
        Environment.SetEnvironmentVariable("APEX_FORCE_MOCK", null);
        MainViewModel vm;
        // A session of its own in this run's temp folder, never the user's journal: restoring that put its edits in the timed
        // corpus and its 'Restored…' in place of 'Loaded…' (so the wait below ran its full 3 minutes), and other runs, or
        // Apex itself, may hold it at the same time.
        try { vm = new MainViewModel(NewScratch("live-session")); }
        finally { Environment.SetEnvironmentVariable("APEX_FORCE_MOCK", saved); }
        if (vm.IsMockData)
        {
            Console.WriteLine("palette timing: BO3 not found, skipped");
            return;
        }
        var loadUntil = DateTime.UtcNow.AddMinutes(3);
        while (!vm.Status.StartsWith("Loaded", StringComparison.Ordinal) && DateTime.UtcNow < loadUntil)
            Pump(100);
        var window = new MainWindow { DataContext = vm };
        window.Show();
        window.Activate();
        window.UpdateLayout();
        Pump();

        Interactive Focused() => window.FocusManager?.GetFocusedElement() as Interactive ?? window;
        // Not in Avalonia's reference assembly.
        var uiThreadRender = (DispatcherPriority)typeof(DispatcherPriority).GetField("UiThreadRender")!.GetValue(null)!;
        // The headless platform renders the window in software on the UI thread (UiThreadRender); the app renders on its
        // own thread. That frame is left out: everything else the input queued, and layout, is counted.
        // The UI thread's own time, as the perf gates count it: a moment the OS gave the core to another process isn't the
        // palette's.
        double Time(Action input)
        {
            var total = UiThreadMs(() =>
            {
                input();
                Dispatcher.UIThread.RunJobs(DispatcherPriority.Render);
            });
            Dispatcher.UIThread.RunJobs(uiThreadRender);
            return total + UiThreadMs(() =>
            {
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
            });
        }
        void Press(Avalonia.Input.Key key, KeyModifiers mods)
        {
            var target = Focused();
            target.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = mods, Source = target });
            target.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = key, KeyModifiers = mods, Source = target });
        }
        void Type(char c)
        {
            var target = Focused();
            target.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = c.ToString(), Source = target });
        }
        var opened = true;
        (double Open, List<double> Keys, int Rows) Run(KeyModifiers mods, string typed)
        {
            var open = Time(() => Press(Avalonia.Input.Key.P, mods));
            opened &= vm.IsPaletteOpen;
            var keys = typed.Select(c => Time(() => Type(c))).ToList();
            var rows = vm.PaletteResults.Count;
            Press(Avalonia.Input.Key.Escape, KeyModifiers.None);
            Pump();
            return (open, keys, rows);
        }

        const string assetQuery = "ar_standard";
        const string commandQuery = "explorer";
        const int runs = 7;
        // Warm-up: JIT, first templates. One pass leaves the palette's code at its first, unoptimized tier when nothing
        // before it in the process ran the palette (a run of just this group, a shard): the first letter measured 23 ms,
        // against 14 with tiered compilation off or after the command checks. Passes with idle time between let the
        // runtime finish tiering up (it works through a fresh process's whole backlog first), wherever the group runs.
        for (var i = 0; i < 10; i++)
        {
            Run(KeyModifiers.Control, assetQuery);
            Run(KeyModifiers.Control | KeyModifiers.Shift, commandQuery);
            Pump(250);
        }
        var assets = Enumerable.Range(0, runs).Select(_ => Run(KeyModifiers.Control, assetQuery)).ToList();
        var commands = Enumerable.Range(0, runs).Select(_ => Run(KeyModifiers.Control | KeyModifiers.Shift, commandQuery)).ToList();

        static double Median(IEnumerable<double> xs)
        {
            var s = xs.OrderBy(x => x).ToList();
            return s[s.Count / 2];
        }
        static string Letters(IEnumerable<List<double>> runs) =>
            string.Join(" ", Enumerable.Range(0, runs.First().Count).Select(i => Median(runs.Select(r => r[i])).ToString("0.0")));
        var assetKeys = assets.SelectMany(r => r.Keys).ToList();
        var commandKeys = commands.SelectMany(r => r.Keys).ToList();
        Console.WriteLine($"palette timing ({vm.TotalCount:N0} assets, median of {runs}): Ctrl+P open {Median(assets.Select(r => r.Open)):0.0} ms; " +
                          $"typing '{assetQuery}' per letter {Letters(assets.Select(r => r.Keys))} ms (worst single {assetKeys.Max():0.0}, {assets[0].Rows} rows)");
        Console.WriteLine($"palette timing: Ctrl+Shift+P open {Median(commands.Select(r => r.Open)):0.0} ms; " +
                          $"typing '{commandQuery}' per letter {Letters(commands.Select(r => r.Keys))} ms (worst single {commandKeys.Max():0.0}, {commands[0].Rows} rows)");
        var worstMedian = new[] { Median(assets.Select(r => r.Open)), Median(commands.Select(r => r.Open)) }
            .Concat(Enumerable.Range(0, assetQuery.Length).Select(i => Median(assets.Select(r => r.Keys[i]))))
            .Concat(Enumerable.Range(0, commandQuery.Length).Select(i => Median(commands.Select(r => r.Keys[i])))).Max();
        Gate($"palette timing: Ctrl+P, Ctrl+Shift+P and every letter typed are within a frame (slowest median {worstMedian:0.0} ms of 16)",
            opened, worstMedian <= 16);
        window.Close();
        vm.Dispose();
    }
}
