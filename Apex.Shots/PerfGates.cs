using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using K = Avalonia.Input.Key;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Apex.Editor.Controls;
using Apex.Editor.Models;
using Apex.Editor.ViewModels;
using Apex.Editor.Views;

namespace Apex.Shots;

/// <summary>
/// Perf gates: the interaction budgets checked on every run instead of whenever someone remembers. Each budgeted
/// interaction is driven with real input on the real controls of a fresh window and timed on the UI thread, from the
/// input to the moment the thread is idle again with layout done (queued jobs, bindings, layout — everything that stands
/// between the input and the next frame; rasterizing that frame is the compositor's, not the UI thread's). The gated
/// number is the UI thread's own CPU time (see <see cref="UiThreadClock"/>); wall clock is shown beside it. Samples
/// after a warmup give a median and a p95 per interaction; <see cref="PerfBudgets"/> decides pass or fail.
///
/// Mock data runs with every harness run (and alone with <c>--perf</c>). The real install runs with <c>--perf-live</c>
/// (or APEX_PERF_LIVE=1): startup timed over several launches of <c>--perf-startup</c>, each a process of its own, then
/// every interaction on the full corpus. APEX_PERF_ONLY=&lt;part of a gate's name&gt; narrows a run for profiling.
/// </summary>
public partial class Program
{
    /// <summary>
    /// One gate's numbers. <see cref="Median"/> and <see cref="P95"/> are what's gated: UI-thread CPU time where the gate
    /// measures a UI-thread interaction, wall clock where the work spans threads (then <see cref="WallMedian"/> is NaN).
    /// </summary>
    private sealed record PerfResult(string Name, double Budget, double Median, double P95, int Runs, string Note = "",
        double WallMedian = double.NaN, double WallP95 = double.NaN)
    {
        public bool Pass => Median <= Budget && P95 <= Budget * PerfBudgets.P95Slack;
    }

    /// <summary>
    /// CPU time the UI thread itself spends, from its cycle counter. Wall clock on a shared machine also counts every
    /// moment the OS gave the core to another process (a build, another program, a browser), which moves medians by
    /// tens of percent run to run and says nothing about Apex. The UI thread's own cycles are what a budget is about:
    /// on an idle machine the two agree, and the table shows both.
    /// </summary>
    private static class UiThreadClock
    {
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern bool QueryThreadCycleTime(IntPtr thread, out ulong cycles);

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentThread();

        public static bool Available { get; } = OperatingSystem.IsWindows();

        public static ulong Cycles() => Available && QueryThreadCycleTime(GetCurrentThread(), out var c) ? c : 0;

        /// <summary>
        /// Cycles per millisecond, from spinning on this thread against the wall clock. The fastest ratio wins: a spin
        /// the OS interrupted counts fewer cycles per wall millisecond, never more.
        /// </summary>
        public static readonly double CyclesPerMs = Calibrate();

        private static double Calibrate()
        {
            if (!Available)
                return double.NaN;
            var best = 0.0;
            for (var i = 0; i < 7; i++)
            {
                var c0 = Cycles();
                var t0 = Stopwatch.GetTimestamp();
                while (Stopwatch.GetElapsedTime(t0).TotalMilliseconds < 15)
                {
                }
                var ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                best = Math.Max(best, (Cycles() - c0) / ms);
            }
            return best;
        }

        public static double ToMs(ulong cycles) => cycles / CyclesPerMs;
    }

    /// <summary>
    /// The UI thread's own milliseconds for <paramref name="act"/>, counted as <see cref="Time"/> counts them (wall time
    /// where the cycle counter isn't available): a moment the OS gave the core to another process isn't the app's.
    /// </summary>
    private static double UiThreadMs(Action act)
    {
        var start = Stopwatch.GetTimestamp();
        var cycles = UiThreadClock.Cycles();
        act();
        return UiThreadClock.Available ? UiThreadClock.ToMs(UiThreadClock.Cycles() - cycles) : Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }

    /// <summary>Timings from a Debug build (JIT optimizer off) say nothing about the budgets, which are Release numbers.</summary>
    private static bool IsOptimizedBuild =>
        typeof(MainViewModel).Assembly.GetCustomAttribute<DebuggableAttribute>()?.IsJITOptimizerDisabled != true;

    /// <summary>Everything the UI thread does before it can take the next input: queued jobs, then layout, then whatever layout queued.</summary>
    private static void Settle(TopLevel top)
    {
        Dispatcher.UIThread.RunJobs();
        top.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>
    /// Times <paramref name="act"/> (input plus everything it sets off, to idle with layout done) <paramref name="runs"/>
    /// times after <paramref name="warmup"/> untimed ones. <paramref name="before"/> and <paramref name="after"/> set up
    /// and tidy each run outside the clock.
    /// </summary>
    private static PerfResult Time(TopLevel top, string name, double budget, int warmup, int runs, Action<int> act,
        Action<int>? before = null, Action<int>? after = null, string note = "", bool firstFrame = false)
    {
        // APEX_PERF_ONLY=<part of a name> runs the other gates once, and the chosen one's timed runs under a frame of
        // their own (ProfiledRun), so a profile (dotnet-trace) can be cut to exactly that gate.
        var profiled = false;
        if (Environment.GetEnvironmentVariable("APEX_PERF_ONLY") is { Length: > 0 } only)
        {
            if (name.Contains(only, StringComparison.OrdinalIgnoreCase))
                profiled = true;
            else
                (warmup, runs) = (0, 1);
        }
        var cpu = new List<double>(runs);
        var wall = new List<double>(runs);
        for (var i = 0; i < warmup + runs; i++)
        {
            before?.Invoke(i);
            Settle(top);
            // Each run starts from a drawn frame, as the next input would in the app.
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Settle(top);
            var start = Stopwatch.GetTimestamp();
            var cycles = UiThreadClock.Cycles();
            if (profiled && i >= warmup)
                ProfiledRun(top, act, i, firstFrame);
            else
                TimedRun(top, act, i, firstFrame);
            var used = UiThreadClock.Cycles() - cycles;
            var ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            if (i >= warmup)
            {
                wall.Add(ms);
                cpu.Add(UiThreadClock.Available ? UiThreadClock.ToMs(used) : ms);
            }
            after?.Invoke(i);
        }
        var (wallMedian, wallP95) = MedianP95(wall);
        return Summarize(name, budget, cpu, note) with { WallMedian = wallMedian, WallP95 = wallP95 };
    }

    private static (double Median, double P95) MedianP95(List<double> samples)
    {
        samples.Sort();
        var median = samples.Count % 2 == 1
            ? samples[samples.Count / 2]
            : (samples[samples.Count / 2 - 1] + samples[samples.Count / 2]) / 2;
        // Nearest-rank p95.
        return (median, samples[Math.Clamp((int)Math.Ceiling(samples.Count * 0.95) - 1, 0, samples.Count - 1)]);
    }

    /// <summary>The clocked part of a run, kept a frame of its own so a profile (dotnet-trace) can isolate it.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void TimedRun(TopLevel top, Action<int> act, int i, bool firstFrame)
    {
        act(i);
        if (!firstFrame)
        {
            Settle(top);
            return;
        }
        // To the first frame only: work the UI left for later frames, at background priority, stays queued (the run's
        // after-step settles it off the clock).
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Input);
        top.UpdateLayout();
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Input);
    }

    /// <summary>The timed runs of the gate APEX_PERF_ONLY picked, after its warmup.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void ProfiledRun(TopLevel top, Action<int> act, int i, bool firstFrame) => TimedRun(top, act, i, firstFrame);

    private static PerfResult Summarize(string name, double budget, List<double> samples, string note = "")
    {
        var (median, p95) = MedianP95(samples);
        return new PerfResult(name, budget, median, p95, samples.Count, note);
    }

    /// <summary>
    /// Input straight into the headless window. The public helpers (<c>window.KeyPress</c> and friends) rasterize a
    /// whole frame with Skia before and after every event, on the calling thread; in the app that rasterizing happens on
    /// the compositor's render thread, so timing through them would charge a desktop-sized software render to every
    /// keystroke. The window's own input methods are the same events without the forced frames.
    /// </summary>
    private static class RawInput
    {
        private static readonly Type Window = typeof(AvaloniaHeadlessPlatform).Assembly.GetType("Avalonia.Headless.IHeadlessWindow")
            ?? throw new InvalidOperationException("Avalonia.Headless no longer has IHeadlessWindow; update the perf gates' input");

        private static readonly Dictionary<string, MethodInfo> Methods = Window.GetMethods().GroupBy(m => m.Name).ToDictionary(g => g.Key, g => g.First());

        public static void Send(TopLevel top, string method, params object?[] args) => Methods[method].Invoke(top.PlatformImpl, args);
    }

    /// <summary>
    /// The headless platform drives its render loop from a DispatcherTimer on the UI thread, so any timed run longer
    /// than a frame would also pay for a full software render of the window (the app renders on its compositor thread).
    /// Push that timer out of reach; <see cref="Time"/> draws a frame between runs itself, off the clock.
    /// </summary>
    private static void SilenceHeadlessRenderTimer()
    {
        var timer = typeof(AvaloniaHeadlessPlatform).GetField("s_renderTimer", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null)
            ?? throw new InvalidOperationException("Avalonia.Headless no longer keeps s_renderTimer; update the perf gates");
        var found = 0;
        foreach (var dt in Reachable(timer, depth: 3).OfType<DispatcherTimer>())
        {
            dt.Interval = TimeSpan.FromDays(1);
            found++;
        }
        if (found == 0)
            throw new InvalidOperationException("the headless render timer no longer runs on a DispatcherTimer; update the perf gates");

        static IEnumerable<object> Reachable(object root, int depth)
        {
            yield return root;
            if (depth == 0 || root is DispatcherTimer)
                yield break;
            for (var type = root.GetType(); type is not null && type != typeof(object); type = type.BaseType)
                foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly))
                {
                    var value = field.GetValue(root);
                    var next = value is Delegate d ? d.Target : value;
                    if (next is null || next is string || next.GetType().IsPrimitive)
                        continue;
                    foreach (var o in Reachable(next, depth - 1))
                        yield return o;
                }
        }
    }

    private static void KeyStroke(Window window, K key, RawInputModifiers mods = RawInputModifiers.None)
    {
        RawInput.Send(window, "KeyPress", key, mods, PhysicalKey.None, null);
        RawInput.Send(window, "KeyRelease", key, mods, PhysicalKey.None, null);
    }

    /// <summary>A typed character as the platform delivers it: key down, text input, key up.</summary>
    private static void TypeChar(Window window, char c)
    {
        var key = c switch
        {
            >= 'a' and <= 'z' => K.A + (c - 'a'),
            >= '0' and <= '9' => K.D0 + (c - '0'),
            '_' => K.OemMinus,
            _ => K.None,
        };
        RawInput.Send(window, "KeyPress", key, RawInputModifiers.None, PhysicalKey.None, c.ToString());
        RawInput.Send(window, "TextInput", c.ToString());
        RawInput.Send(window, "KeyRelease", key, RawInputModifiers.None, PhysicalKey.None, c.ToString());
    }

    private static Point CentreOf(Window window, Control control, double nudge = 0) =>
        control.TranslatePoint(new Point(control.Bounds.Width / 2 + nudge, control.Bounds.Height / 2), window)
        ?? throw new InvalidOperationException($"{control} is not in the window");

    private static void ClickAt(Window window, Point at)
    {
        RawInput.Send(window, "MouseMove", at, RawInputModifiers.None);
        RawInput.Send(window, "MouseDown", at, MouseButton.Left, RawInputModifiers.None);
        RawInput.Send(window, "MouseUp", at, MouseButton.Left, RawInputModifiers.None);
    }

    /// <summary>
    /// Selects an Explorer row and puts the keyboard on it, as arrowing onto it would, without letting the Explorer's
    /// settle-then-open timer fire: RunJobs fires due timers, so a slow setup would otherwise open the asset before the
    /// clock starts and the timed Enter would only pin it.
    /// </summary>
    private static void SelectRow(Window window, ListBox tree, BrowserNode row)
    {
        var openDelay = (DispatcherTimer)typeof(AssetBrowserView).GetField("_openDelay", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(tree.FindAncestorOfType<AssetBrowserView>()!)!;
        tree.SelectedItems?.Clear();
        tree.SelectedItem = row;
        openDelay.Stop();
        tree.ScrollIntoView(row);
        Settle(window);
        tree.ContainerFromItem(row)?.Focus(NavigationMethod.Directional);
        openDelay.Stop();
        Settle(window);
    }

    /// <summary>
    /// The editor rows on screen whose label or value isn't their own property's: what a row recycled from another
    /// property or asset would show if it missed a rebind. Empty when every row is right.
    /// </summary>
    /// <summary>The Explorer rows on screen that don't show their own node's name (a recycled row that missed a rebind).</summary>
    private static List<string> TreeRowsShowingOthers(ListBox tree)
    {
        var wrong = new List<string>();
        foreach (var row in tree.GetRealizedContainers().Where(c => c.IsEffectivelyVisible))
        {
            if (row.DataContext is not BrowserNode { IsHeader: false } node)
                continue;
            _rowsChecked++;
            var name = row.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Classes.Contains("rowname"));
            var runs = name?.Inlines is { } inlines ? string.Concat(inlines.OfType<Avalonia.Controls.Documents.Run>().Select(r => r.Text)) : name?.Text;
            var drawn = name?.TextLayout.TextLines.Sum(l => l.Length) ?? 0;
            if (runs != node.DisplayName || drawn < node.DisplayName.Length)
                wrong.Add($"'{node.DisplayName}' shows '{runs}' ({drawn} characters laid out)");
            wrong.AddRange(StaleTexts(row, except: name).Select(s => $"'{node.Title}': {s}"));
        }
        return wrong;
    }

    /// <summary>
    /// Plain texts in a recycled row laid out for something other than the text they hold now (a stale layout keeps the
    /// width of the row's previous item: a suffix sits in the wrong place, a label is cut short).
    /// </summary>
    private static IEnumerable<string> StaleTexts(Control row, TextBlock? except = null)
    {
        foreach (var t in row.GetVisualDescendants().OfType<TextBlock>()
                     .Where(t => t != except && t.IsEffectivelyVisible && t.Text is { Length: > 0 } && double.IsNaN(t.Width)))
        {
            var fresh = new Avalonia.Media.FormattedText(t.Text!, System.Globalization.CultureInfo.CurrentCulture, Avalonia.Media.FlowDirection.LeftToRight,
                new Avalonia.Media.Typeface(t.FontFamily, t.FontStyle, t.FontWeight), t.FontSize, null).WidthIncludingTrailingWhitespace;
            var needs = Math.Min(fresh, t.MaxWidth);
            var laidOut = t.DesiredSize.Width - t.Margin.Left - t.Margin.Right - t.Padding.Left - t.Padding.Right;
            // Only a text laid out narrower than it fits, or wider than it needs: a trimmed text in a tight column is fine.
            if (laidOut > needs + 1.5 || (laidOut < needs - 1.5 && t.Bounds.Width >= needs + t.Margin.Left + t.Margin.Right + 1.5))
                yield return $"'{t.Text}' laid out {laidOut:0.#} wide, needs {needs:0.#}";
        }
    }

    private static int _rowsChecked;

    private static List<string> RowsShowingOthers(Window window)
    {
        var wrong = new List<string>();
        var form = window.GetVisualDescendants().OfType<AssetEditorView>().First().FindControl<ItemsControl>("Form")!;
        foreach (var row in form.GetRealizedContainers().Where(c => c.IsEffectivelyVisible))
        {
            if (row.DataContext is not PropertyItemViewModel p)
                continue;
            _rowsChecked++;
            var parts = row.GetVisualDescendants().ToList();
            var label = parts.OfType<TextBlock>().FirstOrDefault(t => t.Name == "PropLabel")?.Text;
            string? shown = p switch
            {
                NumberPropertyViewModel n => parts.OfType<ScrubNumberBox>().FirstOrDefault()?.GetVisualDescendants().OfType<TextBlock>()
                    .Any(t => t.IsEffectivelyVisible && t.Text == n.DisplayText) == true ? n.DisplayText : "(not shown)",
                TogglePropertyViewModel t => parts.OfType<ToggleButton>().FirstOrDefault()?.IsChecked == t.IsOn ? p.RawValue : "(switch disagrees)",
                ChoicePropertyViewModel c => !c.Choices.Contains(c.Value)
                                             || Equals(parts.OfType<ComboBox>().FirstOrDefault()?.SelectedItem is ChoiceItem item ? item.Value : parts.OfType<ComboBox>().FirstOrDefault()?.SelectedItem, c.Value)
                    ? p.RawValue : $"{parts.OfType<ComboBox>().FirstOrDefault()?.SelectedItem}",
                PartsPropertyViewModel ps => parts.OfType<ItemsControl>().FirstOrDefault(i => i.Classes.Contains("partlist"))?.ItemsSource == ps.Parts
                    ? p.RawValue : "(parts disagree)",
                LinesPropertyViewModel l => LinesShown(parts, l) ? p.RawValue : "(items disagree)",
                RecordsPropertyViewModel r => RecordsShown(parts, r) ? p.RawValue : "(rows disagree)",
                _ => parts.OfType<TextBox>().FirstOrDefault(b => b.Classes.Contains("pfield")) is { } box && !box.IsKeyboardFocusWithin
                    ? box.Text : p.RawValue,
            };
            if (label != p.Label || shown != p.RawValue && !(p is NumberPropertyViewModel && shown == ((NumberPropertyViewModel)p).DisplayText))
                wrong.Add($"{p.Key}: '{label}' = '{shown}' (is '{p.Label}' = '{p.RawValue}')");
            wrong.AddRange(StaleTexts(row).Select(s => $"{p.Key}: {s}"));
        }
        return wrong;
    }

    /// <summary>A record table's rows are its rows, in order, and each cell holds its own row's editor.</summary>
    private static bool RecordsShown(List<Visual> parts, RecordsPropertyViewModel table)
    {
        var rows = parts.OfType<Grid>().Where(g => g.Classes.Contains("rrow") && g.IsEffectivelyVisible).ToList();
        if (rows.Count != table.Rows.Count)
            return false;
        for (var i = 0; i < rows.Count; i++)
        {
            if (rows[i].DataContext != table.Rows[i])
                return false;
            var cells = rows[i].GetVisualDescendants().OfType<ContentControl>().Where(c => c.Classes.Contains("rcell")).Select(c => c.Content).ToList();
            if (!cells.SequenceEqual(table.Rows[i].Cells))
                return false;
        }
        return true;
    }

    /// <summary>A line list's item rows each show their own item (a focused field may hold typing).</summary>
    private static bool LinesShown(List<Visual> parts, LinesPropertyViewModel list)
    {
        var rows = parts.OfType<Grid>().Where(g => g.Classes.Contains("litem") && g.DataContext is LineItemViewModel { IsAdd: false }).ToList();
        // A table cell shows the count, not the items.
        if (rows.Count == 0 && list.Items.Count > 0)
            return parts.OfType<TextBlock>().Any(t => t.Text == list.Summary);
        if (rows.Count != list.Items.Count)
            return false;
        for (var i = 0; i < rows.Count; i++)
        {
            var item = list.Items[i];
            if (rows[i].DataContext != item)
                return false;
            foreach (var box in rows[i].GetVisualDescendants().OfType<TextBox>().Where(b => b.IsEffectivelyVisible && !b.IsKeyboardFocusWithin))
            {
                var shows = box.DataContext as PropertyItemViewModel;
                if (shows != item.Main && shows != item.Replacement || box.Text != shows?.RawValue)
                    return false;
            }
        }
        return true;
    }

    private static AssetDatabase DatabaseOf(MainViewModel vm) =>
        (AssetDatabase)typeof(MainViewModel).GetField("_db", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(vm)!;

    /// <summary>The Explorer and editor interactions with a budget, on <paramref name="vm"/>'s corpus.</summary>
    private static List<PerfResult> MeasureInteractions(MainWindow window, MainViewModel vm, bool live)
    {
        const int warm = 5;
        const int runs = 30;
        var results = new List<PerfResult>();
        var browser = window.GetVisualDescendants().OfType<AssetBrowserView>().First();
        var tree = browser.FindControl<ListBox>("Tree")!;
        var search = browser.FindControl<TextBox>("SearchBox")!;
        var db = DatabaseOf(vm);

        // ── Explorer: arrow through the collapsed GDT list ──
        vm.FilterText = "";
        vm.ApplyFilterNow();
        browser.FocusTree();
        Settle(window);
        var down = true;
        results.Add(Time(window, "Explorer: arrow one row", PerfBudgets.ArrowStep, warm, runs,
            _ => KeyStroke(window, down ? K.Down : K.Up),
            before: _ =>
            {
                if (tree.SelectedIndex >= tree.ItemCount - 1)
                    down = false;
                else if (tree.SelectedIndex <= 0)
                    down = true;
            }));

        // ── Explorer: expand a large GDT group (→ on the row; ← collapses it again off the clock) ──
        var largest = vm.FlatRows.Where(n => n.Gdt is not null).OrderByDescending(n => n.Gdt!.Assets.Count).Take(warm + runs).ToList();
        var groupSizes = largest.Skip(warm).Select(n => n.Gdt!.Assets.Count).DefaultIfEmpty(0).ToList();
        var treeForeign = new List<string>();
        results.Add(Time(window, "Explorer: expand a large GDT", PerfBudgets.Frame, warm, runs,
            _ => KeyStroke(window, K.Right),
            before: i => SelectRow(window, tree, largest[i % largest.Count]),
            after: _ =>
            {
                treeForeign.AddRange(TreeRowsShowingOthers(tree));
                KeyStroke(window, K.Left);
            },
            note: $"{groupSizes.Min():N0}–{groupSizes.Max():N0} assets, first expand of each"));
        Check($"perf: every Explorer row on screen after each expand showed its own name ({treeForeign.Count} didn't{string.Concat(treeForeign.Take(3).Select(f => "; " + f))})",
            treeForeign.Count == 0);

        // ── Explorer filter: a keystroke (the rebuild waits out the debounce) ──
        const string typed = "wpn_ar_havoc";
        results.Add(Time(window, "Explorer filter: keystroke", PerfBudgets.Frame, warm, runs,
            i => TypeChar(window, typed[i % typed.Length]),
            before: i =>
            {
                if (i % typed.Length != 0)
                    return;
                vm.FilterText = "";
                vm.ApplyFilterNow();
                search.Focus();
            }));

        // ── Explorer filter: the rebuild once typing settles (what the debounce runs), then Esc clearing it ──
        string[] queries = { "wpn_ar", "havoc", "wpn_", "mtl", "zm_" };
        results.Add(Time(window, "Explorer filter: settled rebuild", PerfBudgets.UiChunk, warm, runs,
            _ => vm.ApplyFilterNow(),
            before: i => vm.FilterText = queries[i % queries.Length],
            after: _ =>
            {
                treeForeign.AddRange(TreeRowsShowingOthers(tree));
                vm.FilterText = "";
                vm.ApplyFilterNow();
            }));
        Check($"perf: every Explorer row on screen after each filter showed its own name ({treeForeign.Count} didn't{string.Concat(treeForeign.Take(3).Select(f => "; " + f))})",
            treeForeign.Count == 0);

        // ── Explorer filter: a one-letter search, which matches most of the corpus but shows the first 5,000 ──
        string[] letters = { "a", "_", "e" };
        results.Add(Time(window, "Explorer filter: one-letter search", PerfBudgets.UiChunk, warm, runs,
            _ => vm.ApplyFilterNow(),
            before: i => vm.FilterText = letters[i % letters.Length],
            after: _ =>
            {
                treeForeign.AddRange(TreeRowsShowingOthers(tree));
                vm.FilterText = "";
                vm.ApplyFilterNow();
            },
            note: $"{db.Assets.Count:N0} assets"));
        Check($"perf: every Explorer row on screen after each one-letter search showed its own name ({treeForeign.Count} didn't{string.Concat(treeForeign.Take(3).Select(f => "; " + f))})",
            treeForeign.Count == 0);
        // The rows a broad search shows are the top of the full ranking: prefix matches, then shorter, then by name.
        foreach (var letter in letters)
        {
            vm.FilterText = letter;
            vm.ApplyFilterNow();
            var ranked = db.Gdts.SelectMany(g => g.Assets).Where(a => a.Name.Contains(letter, StringComparison.OrdinalIgnoreCase))
                .OrderBy(a => a.Name.StartsWith(letter, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(a => a.Name.Length)
                .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                .Take(5000).ToList();
            var listed = vm.FlatRows.Where(n => n.Asset is not null).Select(n => n.Asset!).ToList();
            var firstDiff = Enumerable.Range(0, Math.Min(listed.Count, ranked.Count)).FirstOrDefault(i => listed[i] != ranked[i], -1);
            Check($"perf: search '{letter}' shows the first {ranked.Count:N0} of {vm.MatchCount:N0} matches in rank order ({listed.Count:N0} rows{(firstDiff >= 0 ? $", first differs at {firstDiff}: {listed[firstDiff].Name} vs {ranked[firstDiff].Name}" : "")})",
                listed.Count == ranked.Count && firstDiff < 0);
        }
        vm.FilterText = "";
        vm.ApplyFilterNow();
        results.Add(Time(window, "Explorer filter: Esc clears", PerfBudgets.UiChunk, warm, runs,
            _ => KeyStroke(window, K.Escape),
            before: i =>
            {
                vm.FilterText = queries[i % queries.Length];
                vm.ApplyFilterNow();
                search.Focus();
            }));

        // ── Open an asset: Enter on its Explorer row, with another asset already open ──
        // Each run opens a different asset of the type, so every open is cold (its properties read from the GDT).
        // With fromTypes, each run first opens an asset of another type (cycling through them), as moving between an
        // anim and the weapon that uses it does: the editor's rows then come from the other type's list, not the last open's.
        List<AssetRecord> MeasureOpen(string type, string[]? fromTypes = null)
        {
            var pool = db.Assets.Where(a => a.Type.Equals(type, StringComparison.OrdinalIgnoreCase)).ToList();
            var froms = (fromTypes ?? Array.Empty<string>())
                .Select(t => db.Assets.FirstOrDefault(a => a.Type.Equals(t, StringComparison.OrdinalIgnoreCase)))
                .OfType<AssetRecord>().ToList();
            var name = froms.Count > 0 ? $"Open asset ({type}, after another type)" : $"Open asset ({type})";
            // Spread over the corpus; on a small one, cycle so no two consecutive opens are the same asset.
            var picks = Enumerable.Range(0, warm + runs)
                .Select(i => pool.Count >= warm + runs ? pool[(int)((long)i * pool.Count / (warm + runs))] : pool[i % pool.Count]).ToList();
            var alreadyOpen = 0;
            var rowCounts = new List<int>();
            var problemCounts = new List<int>();
            var railCounts = new List<int>();
            var realized = true;
            var foreign = new List<string>();
            var open = Time(window, name, PerfBudgets.OpenAsset, warm, runs,
                _ => KeyStroke(window, K.Enter),
                before: i =>
                {
                    if (froms.Count > 0)
                    {
                        vm.OpenAsset(froms[i % froms.Count]);
                        Settle(window);
                    }
                    vm.FilterText = picks[i].Name;
                    vm.ApplyFilterNow();
                    Settle(window);
                    SelectRow(window, tree, vm.FlatRows.First(n => n.Asset == picks[i]));
                    if (vm.OpenTabs.Any(t => t.Record == picks[i]))
                        alreadyOpen++;
                },
                after: i =>
                {
                    var editor = window.GetVisualDescendants().OfType<AssetEditorView>().FirstOrDefault();
                    var form = editor?.FindControl<ItemsControl>("Form");
                    // An xanim's rows are its properties panel's (its notetracks are in the dock under the preview).
                    var panelRows = vm.ActiveTab is { IsAnim: true } && window.FindControl<AnimPropertiesView>("AnimProperties") is { } panel
                        && panel.GetVisualDescendants().OfType<Border>().Any(b => b.Classes.Contains("cardrow") && b.IsEffectivelyVisible
                            && b.DataContext is PropertyItemViewModel);
                    realized &= vm.ActiveTab?.Record == picks[i] && (form?.ContainerFromIndex(1) is not null || panelRows);
                    if (i >= warm)
                        foreign.AddRange(RowsShowingOthers(window).Select(w => $"{picks[i].Name} {w}"));
                    rowCounts.Add(vm.ActiveTab?.PropertyCount ?? 0);
                    problemCounts.Add(vm.ActiveTab?.ProblemCount ?? 0);
                    railCounts.Add(vm.ActiveTab?.RailItems.Count ?? 0);
                    foreach (var other in vm.OpenTabs.Where(t => t != vm.ActiveTab).ToList())
                        other.CloseCommand.Execute(null);
                    Settle(window);
                });
            results.Add(open with { Note = $"target {PerfBudgets.OpenAssetTarget:0} ms; {rowCounts.DefaultIfEmpty(0).Min():N0}–{rowCounts.DefaultIfEmpty(0).Max():N0} properties, {problemCounts.DefaultIfEmpty(0).Min():N0}–{problemCounts.DefaultIfEmpty(0).Max():N0} problems, {railCounts.DefaultIfEmpty(0).Max():N0} sections" });
            Check($"perf: every timed {type} open showed the asset's editor rows", realized);
            Check($"perf: every row on screen after each {type} open showed its own property's label and value ({_rowsChecked:N0} rows so far, {foreign.Count} didn't{string.Concat(foreign.Take(3).Select(f => "; " + f))})",
                foreign.Count == 0);
            Check($"perf: every timed {type} open started with the asset closed ({alreadyOpen} weren't)", alreadyOpen == 0);
            // Rows are recycled from one asset to the next: a recycled editor must never write into the asset it now shows.
            var edited = picks.Concat(froms).Distinct().Where(p => p.History.CanUndo || p.HasSessionEdits).ToList();
            Check($"perf: opening {picks.Distinct().Count()} {type} assets{(froms.Count > 0 ? " (and the assets opened before them)" : "")} edited none of them ({string.Join(", ", edited.Take(3).Select(p => $"{p.Name}: {string.Join(" ", p.History.NextUndo?.Changes.Select(c => $"{c.Key} '{c.Before}'→'{c.After}'") ?? Array.Empty<string>())}"))})",
                edited.Count == 0);
            vm.FilterText = "";
            vm.ApplyFilterNow();
            return picks;
        }

        // The weapon type modders open most, and the heaviest type in the corpus (most schema properties).
        var types = db.Assets.Select(a => a.Type).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var heavyType = types.OrderByDescending(t => SchemaRegistry.Get(t)?.Properties.Count ?? 0).First();
        if (types.FirstOrDefault(t => t.Equals("bulletweapon", StringComparison.OrdinalIgnoreCase)) is { } weaponType && weaponType != heavyType)
            MeasureOpen(weaponType);
        var picks = MeasureOpen(heavyType);
        // An xanim opened from a weapon, a model or a rumble: each of those lists lacks most of an xanim's row kinds.
        if (live && types.FirstOrDefault(t => t.Equals("xanim", StringComparison.OrdinalIgnoreCase)) is { } animType)
            MeasureOpen(animType, new[] { "bulletweapon", "xmodel", "rumble" });
        // A material: material.awi asks the techsetdefs which fields its type shows (a cold type parses its techsetdef).
        if (live && types.FirstOrDefault(t => t.Equals("material", StringComparison.OrdinalIgnoreCase)) is { } materialType)
            MeasureOpen(materialType);

        // ── Switch tabs: a click on the other open tab ──
        vm.OpenAsset(picks[1]);
        vm.OpenAsset(picks[2]);
        Settle(window);
        var tabList = window.FindControl<ListBox>("TabList")!;
        var tabPoint = default(Point);
        var switchedForeign = new List<string>();
        results.Add(Time(window, "Switch tabs (click)", PerfBudgets.Reopen, warm, runs,
            _ => ClickAt(window, tabPoint),
            before: i =>
            {
                var other = vm.OpenTabs.First(t => t != vm.ActiveTab);
                tabPoint = CentreOf(window, tabList.ContainerFromItem(other)!, (i % 5) - 2);
            },
            after: _ => switchedForeign.AddRange(RowsShowingOthers(window))));
        Check($"perf: every row on screen after each tab switch showed its own property's label and value ({_rowsChecked:N0} rows so far, {switchedForeign.Count} didn't{string.Concat(switchedForeign.Take(3).Select(f => "; " + f))})",
            switchedForeign.Count == 0);

        // ── Replacing the preview tab and closing the active tab: straight from the old tab to the next, so the editor
        // view is reused as on any other open (a null in between tore it down and built it again) ──
        AssetEditorView? View() => window.GetVisualDescendants().OfType<AssetEditorView>().FirstOrDefault();
        var wentNull = 0;
        var rebuilt = 0;
        void OnVm(object? s, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MainViewModel.ActiveTab) && vm.ActiveTab is null)
                wentNull++;
        }
        vm.PropertyChanged += OnVm;
        var clickType = types.FirstOrDefault(t => t.Equals("bulletweapon", StringComparison.OrdinalIgnoreCase)) ?? heavyType;
        var clickPool = db.Assets.Where(a => a.Type.Equals(clickType, StringComparison.OrdinalIgnoreCase) && !a.IsMaterialized).ToList();
        if (clickPool.Count < warm + runs)
            clickPool = db.Assets.Where(a => a.Type.Equals(clickType, StringComparison.OrdinalIgnoreCase)).ToList();
        // Spread over the type; on a small corpus, cycle so no two consecutive picks are the same asset.
        var clickPicks = Enumerable.Range(0, warm + runs)
            .Select(i => clickPool.Count >= warm + runs ? clickPool[(int)((long)i * clickPool.Count / (warm + runs))] : clickPool[i % clickPool.Count]).ToList();
        // From one preview tab and nothing else, so no click lands on an asset already open in a kept tab.
        foreach (var t in vm.OpenTabs.ToList())
            t.CloseCommand.Execute(null);
        vm.OpenAsset(clickPicks[^1], preview: true);
        Settle(window);
        var rowPoint = default(Point);
        AssetEditorView? viewBefore = null;
        var clickLanded = true;
        string? clickMiss = null;
        results.Add(Time(window, "Explorer: click opens into the preview tab", PerfBudgets.OpenAsset, warm, runs,
            _ => ClickAt(window, rowPoint),
            before: i =>
            {
                vm.FilterText = clickPicks[i].Name;
                vm.ApplyFilterNow();
                tree.SelectedItems?.Clear();
                Settle(window);
                var row = vm.FlatRows.First(n => n.Asset == clickPicks[i]);
                tree.ScrollIntoView(row);
                Settle(window);
                rowPoint = CentreOf(window, tree.ContainerFromItem(row)!);
                // Hit-testing reads the drawn scene, a frame behind a list that just changed: draw one here, and the
                // run's own frame brings it up to date.
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                viewBefore = View();
                wentNull = 0;
            },
            after: i =>
            {
                var landed = vm.ActiveTab is { IsPreview: true } t && t.Record == clickPicks[i] && vm.OpenTabs.Count(o => o.IsPreview) == 1;
                if (!landed && clickMiss is null)
                    clickMiss = $"run {i}: {clickPicks[i].Name} → active {vm.ActiveTab?.Name} (preview {vm.ActiveTab?.IsPreview}), {vm.OpenTabs.Count(o => o.IsPreview)} preview tabs";
                clickLanded &= landed;
                if (wentNull > 0 || View() != viewBefore)
                    rebuilt++;
            },
            note: $"{clickType}, replacing the preview tab"));
        Check($"perf: each timed Explorer click opened its asset into the one preview tab{(clickMiss is null ? "" : $" (first miss: {clickMiss})")}", clickLanded);
        Check($"perf: replacing the preview tab reused the editor view ({rebuilt} of {warm + runs} clicks went through no tab or a new view)", rebuilt == 0);
        vm.FilterText = "";
        vm.ApplyFilterNow();

        // Close: two pinned tabs, the active one closed by its ✕; its neighbour takes over.
        rebuilt = 0;
        var closeLanded = true;
        var closePoint = default(Point);
        AssetEditorViewModel? expected = null;
        results.Add(Time(window, "Close the active tab (✕)", PerfBudgets.Reopen, warm, runs,
            _ => ClickAt(window, closePoint),
            before: i =>
            {
                foreach (var t in vm.OpenTabs.ToList())
                    if (t.Record != clickPicks[i] && t.Record != clickPicks[(i + 1) % clickPicks.Count])
                        t.CloseCommand.Execute(null);
                vm.OpenAsset(clickPicks[(i + 1) % clickPicks.Count]);
                vm.OpenAsset(clickPicks[i]);
                Settle(window);
                expected = vm.OpenTabs.First(t => t.Record == clickPicks[(i + 1) % clickPicks.Count]);
                var tabRow = tabList.ContainerFromItem(vm.ActiveTab!)!;
                // The ✕ shows with the pointer on its tab.
                window.MouseMove(CentreOf(window, tabRow));
                Settle(window);
                closePoint = CentreOf(window, tabRow.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("tabclose")));
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                viewBefore = View();
                wentNull = 0;
            },
            after: i =>
            {
                closeLanded &= vm.ActiveTab == expected && vm.OpenTabs.All(t => t.Record != clickPicks[i]);
                if (wentNull > 0 || View() != viewBefore)
                    rebuilt++;
            },
            note: "to its neighbour, already open"));
        Check("perf: each timed ✕ closed the active tab and activated its neighbour", closeLanded);
        Check($"perf: closing the active tab reused the editor view ({rebuilt} of {warm + runs} closes went through no tab or a new view)", rebuilt == 0);
        vm.PropertyChanged -= OnVm;

        // ── Property rows: type, commit, step, toggle (on a weapon when the corpus has one: it has every kind of row) ──
        if (types.Any(t => t.Equals("bulletweapon", StringComparison.OrdinalIgnoreCase)))
        {
            vm.OpenAsset(db.Assets.First(a => a.Type.Equals("bulletweapon", StringComparison.OrdinalIgnoreCase) && a.Parent is null));
            Settle(window);
        }
        var tab = vm.ActiveTab!;
        bool Usable(PropertyItemViewModel p) => !p.IsRuleHidden && !p.IsRuleDisabled;
        var text = tab.AllSentinel.All.FirstOrDefault(p => p is TextPropertyViewModel && Usable(p));
        // damage where the type has it: the field modders step most, and one the deffile's display rules read.
        var number = tab.AllSentinel.All.FirstOrDefault(p => p is NumberPropertyViewModel && Usable(p) && p.Key == "damage")
                     ?? tab.AllSentinel.All.FirstOrDefault(p => p is NumberPropertyViewModel && Usable(p));
        var toggle = tab.AllSentinel.All.FirstOrDefault(p => p is TogglePropertyViewModel && Usable(p));
        IInputElement? Focused() => window.FocusManager?.GetFocusedElement();
        void Reveal(PropertyItemViewModel p)
        {
            tab.RevealProperty(p.Key);
            Settle(window);
            Settle(window);
        }

        if (text is not null)
        {
            Reveal(text);
            var box = Focused() as TextBox;
            Check($"perf: the text field '{text.Key}' has the keyboard ({Focused()?.GetType().Name})", box is not null);

            // Recycled rows sit in the panel in whatever order they came back; Tab must still walk the form top to bottom.
            int FocusedRow() => (Focused() as Visual)?.GetSelfAndVisualAncestors().Select(v => (v as StyledElement)?.DataContext)
                .OfType<PropertyItemViewModel>().FirstOrDefault() is { } r ? tab.FlatRows.IndexOf(r) : -1;
            var from = tab.FlatRows.IndexOf(text);
            var next = Enumerable.Range(from + 1, tab.FlatRows.Count - from - 1)
                .FirstOrDefault(j => tab.FlatRows[j] is PropertyItemViewModel { IsRuleDisabled: false }, -1);
            KeyStroke(window, K.Tab);
            Settle(window);
            var tabbed = FocusedRow();
            KeyStroke(window, K.Tab, RawInputModifiers.Shift);
            Settle(window);
            var back = FocusedRow();
            Check($"keyboard: Tab from '{text.Key}' goes to the next row and Shift+Tab back (rows {from} → {tabbed} → {back}, next is {next})",
                tabbed == next && back == from && Focused() == box);
            results.Add(Time(window, "Property field: keystroke", PerfBudgets.Frame, warm, runs,
                i => TypeChar(window, (char)('a' + i % 26)),
                before: i =>
                {
                    if (i % 10 == 0 && box is not null)
                        box.Text = text.RawValue;
                }));
            results.Add(Time(window, "Property field: Enter commits", PerfBudgets.Frame, warm, runs,
                _ => KeyStroke(window, K.Enter),
                before: i =>
                {
                    box?.SelectAll();
                    TypeChar(window, (char)('a' + i % 26));
                }));
        }

        if (number is not null)
        {
            Reveal(number);
            Check($"perf: the number field '{number.Key}' has the keyboard ({Focused()?.GetType().Name})", Focused() is Apex.Editor.Controls.ScrubNumberBox);
            var up = true;
            results.Add(Time(window, "Number field: Ctrl+arrow step", PerfBudgets.Frame, warm, runs,
                _ => KeyStroke(window, up ? K.Up : K.Down, RawInputModifiers.Control),
                after: i => up = i % 2 == 1,
                note: $"{number.Key} on {tab.TypeName}"));
            // The same keystroke through Avalonia.Headless's public input helpers, which draw a full software frame
            // before and after every event on this thread: how much of a headless measurement is the renderer.
            var viaHelpers = Time(window, "Number field: via headless helpers", PerfBudgets.Frame, warm, runs,
                _ =>
                {
                    window.KeyPress(K.Up, RawInputModifiers.Control, PhysicalKey.None, null);
                    window.KeyRelease(K.Up, RawInputModifiers.Control, PhysicalKey.None, null);
                },
                after: _ =>
                {
                    window.KeyPress(K.Down, RawInputModifiers.Control, PhysicalKey.None, null);
                    window.KeyRelease(K.Down, RawInputModifiers.Control, PhysicalKey.None, null);
                });
            Console.WriteLine($"info  number step through the headless helpers: median {viaHelpers.Median:0.0} ms, p95 {viaHelpers.P95:0.0} ms " +
                "(each event also rasterizes the window in software on the UI thread; not gated)");
        }

        if (toggle is not null)
        {
            Reveal(toggle);
            var button = Focused() as ToggleButton;
            Check($"perf: the toggle '{toggle.Key}' is on screen ({Focused()?.GetType().Name})", button is not null);
            if (button is not null)
            {
                var before = toggle.RawValue;
                var clickPoint = default(Point);
                results.Add(Time(window, "Toggle (click)", PerfBudgets.Frame, warm, runs,
                    _ => ClickAt(window, clickPoint),
                    // Alternate ends of the switch, far enough apart that two quick clicks never pair into a double-click.
                    before: i => clickPoint = CentreOf(window, button, (i % 2 == 0 ? -1 : 1) * Math.Min(10, button.Bounds.Width / 3))));
                Check($"perf: the toggle clicks flipped the value ({before} → {toggle.RawValue} after {warm + runs} clicks)",
                    (warm + runs) % 2 == 0 ? toggle.RawValue == before : toggle.RawValue != before);
            }
        }

        // ── Dropdowns: rows recycle them from asset to asset, so each shows its own row's value, and a pick writes ──
        var shown = window.GetVisualDescendants().OfType<AssetEditorView>().First().GetVisualDescendants()
            .OfType<ChoiceBox>().Where(b => b.IsEffectivelyVisible && b.DataContext is ChoicePropertyViewModel).ToList();
        var wrong = shown.Where(b => b.DataContext is ChoicePropertyViewModel c && c.Choices.Contains(c.Value) && !Equals(b.SelectedItem, c.Value)).ToList();
        Check($"perf: every dropdown on screen shows its own row's value ({shown.Count} shown{string.Join("", wrong.Take(3).Select(b => $"; {((ChoicePropertyViewModel)b.DataContext!).Key} shows '{b.SelectedItem}'"))})",
            shown.Count > 0 && wrong.Count == 0);
        // The wheel over a dropdown (focused, closed) scrolls the form and never changes the value: down and back up, so
        // the form ends where it started.
        if (tab.AllSentinel.All.OfType<ChoicePropertyViewModel>()
                .FirstOrDefault(p => Usable(p) && Array.IndexOf(p.Choices, p.Value) >= 0 && p.Choices.Length > 1) is { } choice)
        {
            Reveal(choice);
            if (Focused() is ChoiceBox dropdown)
            {
                var original = choice.Value;
                var wheelAt = CentreOf(window, dropdown);
                var seen = new List<string?>();
                results.Add(Time(window, "Dropdown: the wheel scrolls the form", PerfBudgets.Frame, warm, runs,
                    i => RawInput.Send(window, "MouseWheel", wheelAt, new Vector(0, i % 2 == 0 ? -1 : 1), RawInputModifiers.None),
                    after: _ => seen.Add(choice.Value),
                    note: $"{choice.Key} on {tab.TypeName}"));
                Check($"perf: the wheel over the dropdown '{choice.Key}' left its value alone ('{original}' throughout: {seen.All(v => v == original)}), and it shows it",
                    seen.Count >= 2 && seen.All(v => v == original) && Equals(dropdown.SelectedItem, choice.Value));
            }
            else
                Check($"perf: the dropdown '{choice.Key}' has the keyboard ({Focused()?.GetType().Name})", false);
        }
        else
            Check("perf: a dropdown to pick from: " + string.Join("; ", tab.AllSentinel.All.OfType<ChoicePropertyViewModel>().Take(6)
                .Select(p => $"{p.Key}='{p.Value}' usable={Usable(p)} [{string.Join(",", p.Choices.Take(5))}]")), false);

        MeasureRefField(window, vm, db, warm, runs, results);

        // ── Quick Open: a keystroke in the palette ──
        KeyStroke(window, K.P, RawInputModifiers.Control);
        Settle(window);
        Check("perf: Ctrl+P opened Quick Open", vm.IsPaletteOpen);
        results.Add(Time(window, "Quick Open: keystroke", PerfBudgets.Frame, warm, runs,
            i => TypeChar(window, typed[i % typed.Length]),
            before: i =>
            {
                if (i % typed.Length == 0)
                    vm.PaletteText = "";
            }));
        KeyStroke(window, K.Escape);
        Settle(window);

        if (live)
            results.Add(MeasureModelReopen(window, vm, db));
        return results;
    }

    /// <summary>
    /// Re-opening a recently previewed model: Enter on its Explorer row until its prepared model is ready to draw (the
    /// frame itself is the GPU's). Wall clock, not UI-thread time: the prepare runs on a worker, and a cache hit is
    /// what this measures.
    /// </summary>
    private static PerfResult MeasureModelReopen(MainWindow window, MainViewModel vm, AssetDatabase db)
    {
        var browser = window.GetVisualDescendants().OfType<AssetBrowserView>().First();
        var tree = browser.FindControl<ListBox>("Tree")!;
        var samples = new List<double>();
        var models = db.Assets.Where(a => a.Type.Equals("xmodel", StringComparison.OrdinalIgnoreCase)).Take(4000).ToList();

        // Headless has no GPU, so the first ToolsGfx viewport records that D3D interop is unavailable and later loads
        // fall back to OpenGL before preparing anything. Clearing that note before each open lets the load run its real
        // path; the time is taken when the prepared model lands, which is when the viewport would draw it.
        double OpenAndWait(AssetRecord asset, int timeoutMs, out bool prepared)
        {
            vm.FilterText = asset.Name;
            vm.ApplyFilterNow();
            Settle(window);
            SelectRow(window, tree, vm.FlatRows.First(n => n.Asset == asset));
            Apex.Editor.Services.Preview.ToolsGfx.ToolsGfxPreviewService.InteropUnavailableReason = null;
            double? landed = null;
            var start = Stopwatch.GetTimestamp();
            KeyStroke(window, K.Enter);
            var preview = vm.ActiveTab?.PreviewPane?.Content as ModelPreviewViewModel;
            void Landed(object? s, System.ComponentModel.PropertyChangedEventArgs e)
            {
                if (e.PropertyName == nameof(ToolsGfxPreviewState.Model) && preview!.Gfx.Model is not null)
                    landed ??= Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            }
            if (preview is not null)
                preview.Gfx.PropertyChanged += Landed;
            Settle(window);
            while (landed is null && preview is not null && Stopwatch.GetElapsedTime(start).TotalMilliseconds < timeoutMs
                   && (preview.Gfx.FallbackStatus is null || preview.IsLoading))
            {
                System.Threading.Thread.Sleep(1);
                Settle(window);
            }
            if (preview is not null)
                preview.Gfx.PropertyChanged -= Landed;
            prepared = landed is not null;
            return landed ?? Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }

        // Two models that draw with APE's renderer, found by opening candidates cold.
        var drawable = new List<AssetRecord>();
        string? why = null;
        foreach (var m in models.Where((_, i) => i % 97 == 0).Take(40))
        {
            OpenAndWait(m, 20000, out var ok);
            if (ok)
                drawable.Add(m);
            else
                why ??= (vm.ActiveTab?.PreviewPane?.Content as ModelPreviewViewModel)?.Gfx.FallbackDetail;
            if (drawable.Count == 2)
                break;
        }
        if (drawable.Count < 2)
            return new PerfResult("Re-open a previewed model", PerfBudgets.Reopen, double.NaN, double.NaN, 0,
                $"skipped: {why ?? "no drawable model found"}");

        for (var i = 0; i < 25; i++)
        {
            var ms = OpenAndWait(drawable[i % 2], 5000, out var ok);
            if (i >= 5 && ok)
                samples.Add(ms);
            foreach (var other in vm.OpenTabs.Where(t => t != vm.ActiveTab).ToList())
                other.CloseCommand.Execute(null);
        }
        vm.FilterText = "";
        vm.ApplyFilterNow();
        if (samples.Count == 0)
            return new PerfResult("Re-open a previewed model", PerfBudgets.Reopen, double.NaN, double.NaN, 0, "skipped: re-opens never landed a prepared model");
        return Summarize("Re-open a previewed model", PerfBudgets.Reopen, samples, "to its prepared model (wall clock)");
    }

    private static void PrintPerfTable(string corpus, IReadOnlyList<PerfResult> results)
    {
        Console.WriteLine();
        Console.WriteLine($"PERF  {corpus}{(IsOptimizedBuild ? "" : "  (DEBUG BUILD: numbers not comparable, gates not enforced)")}");
        Console.WriteLine($"      {"interaction",-34} {"median",8} {"p95",8} {"budget",8}  {"result",-6}  {"wall median / p95",-18}");
        foreach (var r in results)
        {
            var result = double.IsNaN(r.Median) ? "skip" : r.Pass ? "pass" : "FAIL";
            var wall = double.IsNaN(r.WallMedian) ? "" : $"{Ms(r.WallMedian)} / {Ms(r.WallP95)}";
            Console.WriteLine($"      {r.Name,-34} {Ms(r.Median),8} {Ms(r.P95),8} {r.Budget,5:0} ms  {result,-6}  {wall,-18}{(r.Note.Length > 0 ? "  " + r.Note : "")}");
        }
        Console.WriteLine($"      ms; pass = median ≤ budget and p95 ≤ {PerfBudgets.P95Slack:0.#}× budget, n = {results.FirstOrDefault()?.Runs} after warmup.");
        Console.WriteLine(UiThreadClock.Available
            ? $"      median/p95: UI-thread CPU time, input to idle with layout done ({UiThreadClock.CyclesPerMs / 1000:0} cycles/µs); wall: the same runs by the clock."
            : "      median/p95: wall clock, input to idle with layout done (no thread cycle counter on this OS).");
        Console.WriteLine();

        static string Ms(double v) => double.IsNaN(v) ? "—" : $"{v:0.0}";
    }

    private static void GateResults(string corpus, IReadOnlyList<PerfResult> results)
    {
        PrintPerfTable(corpus, results);
        foreach (var r in results.Where(r => !double.IsNaN(r.Median)))
        {
            var label = $"perf ({corpus}): {r.Name} median {r.Median:0.0} ms, p95 {r.P95:0.0} ms (budget {r.Budget:0} ms)";
            if (!IsOptimizedBuild)
                Console.WriteLine($"info  {label} — not gated in a Debug build");
            else if (Environment.GetEnvironmentVariable("APEX_PERF_ONLY") is { Length: > 0 })
                Console.WriteLine($"info  {label} — not gated while profiling one gate (APEX_PERF_ONLY)");
            else
                Gate(label, r.Pass);
        }
    }

    /// <summary>The mock-corpus gates: a fresh window, every budgeted interaction, part of every harness run.</summary>
    private static void RunMockPerfGates()
    {
        var vm = new MainViewModel();
        var window = new MainWindow { DataContext = vm };
        window.Show();
        window.Activate();
        SilenceHeadlessRenderTimer();
        Settle(window);
        var results = MeasureInteractions(window, vm, live: false);
        MeasureTable(window, vm, DatabaseOf(vm), warm: 5, runs: 30, results);
        GateResults("mock", results);
        window.Close();
    }

    /// <summary>
    /// The real-install gates (<c>--perf-live</c>): a cold launch timed to the first GDT groups on screen and to the first
    /// asset that opens with its real schema, then every budgeted interaction on the full corpus. Nothing is written:
    /// no save runs (APEX_WRITE_ROOT would refuse one outside the temp folder anyway) and APEX_NO_PERSIST keeps the
    /// user's workspace settings untouched.
    /// </summary>
    private static void RunLivePerfGates()
    {
        if (!new Apex.Editor.Services.Gdt.GameEnvironment().IsAvailable)
        {
            Console.WriteLine("perf (live): BO3 install not found — skipped. Set APEX_BO3_ROOT or TA_TOOLS_PATH.");
            return;
        }

        // Startup is wall clock across threads and processes, so one launch says little: time several launches, each a
        // process of its own, and gate the median.
        // Skipped while profiling one gate (APEX_PERF_ONLY): a profiler's suspended-start port would hang the launches.
        var launches = new List<StartupTimes>();
        var launchCount = Environment.GetEnvironmentVariable("APEX_PERF_ONLY") is { Length: > 0 } ? 0 : StartupLaunches;
        for (var i = 0; i < launchCount; i++)
            if (LaunchForStartup() is { } t)
                launches.Add(t);
        if (launchCount > 0)
            GateStartup(launches);

        var (window, vm) = StartLive(out _);
        if (window is null)
            return;
        var results = MeasureInteractions(window, vm, live: true);
        var materialized = DatabaseOf(vm).Assets.Count(a => a.IsMaterialized);
        // After the laziness count: a table materializes its rows.
        MeasureTableLive(window, vm, DatabaseOf(vm), results);
        GateResults("real install", results);
        // Ctrl+S's UI-thread share on the full corpus: working out what to write (the I/O runs off the UI thread; nothing is saved here).
        var plans = Enumerable.Range(0, 7).Select(_ => vm.TimeSavePlan(out _)).OrderBy(x => x).ToList();
        Gate($"perf (live): planning a save over {vm.TotalCount:N0} assets takes {plans[3]:0.0} ms median on the UI thread (budget 16 ms)", plans[3] <= 16);
        Check($"perf (live): laziness holds — {materialized:N0} of {vm.TotalCount:N0} records materialized after the run", materialized < 1000);
        window.Close();
    }

    private static void GateStartup(List<StartupTimes> launches)
    {
        Check($"perf (live): {launches.Count} of {StartupLaunches} startup launches reported their times", launches.Count == StartupLaunches);
        if (launches.Count == 0)
            return;
        var startup = new List<PerfResult>
        {
            StartupResult("Startup: window shown", double.NaN, launches.Select(l => l.Shown), "from process start"),
            StartupResult("Startup: first GDT groups visible", PerfBudgets.StartupFirstGroups, launches.Select(l => l.FirstGroups), "from process start"),
            StartupResult("Startup: first asset openable", PerfBudgets.StartupFirstOpenable, launches.Select(l => l.FirstOpenable), "deffiles parsed, GDTs indexing"),
            StartupResult("Startup: whole corpus indexed", double.NaN, launches.Select(l => l.Indexed), "reported, not gated"),
        };
        Console.WriteLine();
        Console.WriteLine($"PERF  startup (real install), {launches.Count} launches, wall clock from process start");
        Console.WriteLine($"      {"",-34} {"median",8} {"max",8} {"budget",8}  result");
        foreach (var s in startup)
            Console.WriteLine($"      {s.Name,-34} {s.Median,8:0} {s.P95,8:0} {(double.IsNaN(s.Budget) ? "" : $"{s.Budget,6:0} ms  {(s.Median <= s.Budget ? "pass" : "FAIL")}"),-14}  {s.Note}");
        foreach (var s in startup.Where(s => !double.IsNaN(s.Budget)))
            if (IsOptimizedBuild)
                Gate($"perf (live): {s.Name} median {s.Median:0} ms over {launches.Count} launches (budget {s.Budget:0} ms)", s.Median <= s.Budget);
    }

    private const int StartupLaunches = 5;

    private sealed record StartupTimes(double Shown, double FirstGroups, double FirstOpenable, double Indexed);

    private static PerfResult StartupResult(string name, double budget, IEnumerable<double> samples, string note)
    {
        var sorted = samples.OrderBy(s => s).ToList();
        var median = sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;
        // The P95 column holds the slowest launch: with five samples a percentile would only be the maximum anyway.
        return new PerfResult(name, budget, median, sorted[^1], sorted.Count, note);
    }

    /// <summary>One launch of this program with <c>--perf-startup</c>; its times, or null if it failed.</summary>
    private static StartupTimes? LaunchForStartup()
    {
        var exe = Environment.ProcessPath!;
        var info = new ProcessStartInfo(exe) { RedirectStandardOutput = true, UseShellExecute = false };
        // A profiler attached to this process must not hold the launch at a suspended start.
        info.Environment.Remove("DOTNET_DiagnosticPorts");
        if (System.IO.Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            info.ArgumentList.Add(typeof(Program).Assembly.Location);
        info.ArgumentList.Add("--perf-startup");
        using var child = Process.Start(info)!;
        var output = child.StandardOutput.ReadToEnd();
        child.WaitForExit();
        var line = output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith("STARTUP ", StringComparison.Ordinal));
        if (line is null)
        {
            Console.WriteLine($"info  a startup launch printed no times (exit {child.ExitCode}): {output.Trim().Split('\n').LastOrDefault()}");
            return null;
        }
        var v = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1)
            .Select(s => double.Parse(s, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        return new StartupTimes(v[0], v[1], v[2], v[3]);
    }

    /// <summary><c>--perf-startup</c>: launch on the real install, print the startup times on one line, exit.</summary>
    private static void RunStartupOnce()
    {
        var (window, _) = StartLive(out var times);
        if (window is null || times is null)
            return;
        Console.WriteLine(FormattableString.Invariant($"STARTUP {times.Shown:0.0} {times.FirstGroups:0.0} {times.FirstOpenable:0.0} {times.Indexed:0.0}"));
        window.Close();
    }

    /// <summary>
    /// Opens the main window on the real install and waits for the whole corpus to be indexed, timing from process start
    /// to the window, to the first GDT groups on screen, to the first asset that can open with its real schema, and to
    /// the end of indexing.
    /// </summary>
    private static (MainWindow? Window, MainViewModel Vm) StartLive(out StartupTimes? times)
    {
        times = null;
        var launched = Process.GetCurrentProcess().StartTime;
        double SinceLaunch() => (DateTime.Now - launched).TotalMilliseconds;

        var vm = new MainViewModel();
        if (vm.IsMockData)
            return (null, vm);
        var loaded = false;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.Status) && vm.Status.StartsWith("Loaded ", StringComparison.Ordinal))
                loaded = true;
        };
        var window = new MainWindow { DataContext = vm };
        window.Show();
        window.Activate();
        SilenceHeadlessRenderTimer();
        var shown = SinceLaunch();
        var tree = window.GetVisualDescendants().OfType<AssetBrowserView>().First().FindControl<ListBox>("Tree")!;

        double firstGroups = double.NaN, firstOpenable = double.NaN;
        var clock = Stopwatch.StartNew();
        while (!loaded && clock.Elapsed < TimeSpan.FromSeconds(60))
        {
            Settle(window);
            if (double.IsNaN(firstGroups) && tree.ContainerFromIndex(0)?.DataContext is BrowserNode { Gdt: not null })
                firstGroups = SinceLaunch();
            if (double.IsNaN(firstOpenable) && SchemaRegistry.Get("bulletweapon") is not null && vm.TotalCount > 0)
                firstOpenable = SinceLaunch();
            System.Threading.Thread.Sleep(1);
        }
        Settle(window);
        var fullyLoaded = SinceLaunch();
        Check($"perf (live): the corpus loaded ({vm.TotalCount:N0} assets in {vm.GdtCount:N0} GDTs)", loaded);
        if (!loaded)
        {
            window.Close();
            return (null, vm);
        }
        times = new StartupTimes(shown, firstGroups, firstOpenable, fullyLoaded);
        return (window, vm);
    }
}
