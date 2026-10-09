using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using K = Avalonia.Input.Key;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Apex.Editor.Commands;
using Apex.Editor.Controls;
using Apex.Editor.ViewModels;
using Apex.Editor.Views;

namespace Apex.Shots;

/// <summary>
/// The command catalog: one definition per command, APE's shortcuts kept, every command reachable from the palette, and
/// menus, tooltips and keys all reading the same names and gestures — checked with real key presses and clicks.
/// </summary>
public partial class Program
{
    private static void RunCommandChecks(string outDir)
    {
        CatalogChecks();

        var vm = new MainViewModel();
        var window = new MainWindow { DataContext = vm };
        window.Show();
        window.Activate();
        window.UpdateLayout();
        Pump();

        // ── Start page: the hint line is built from the catalog ──
        var hints = window.FindControl<TextBlock>("StartShortcuts")!.Text ?? "";
        Check($"start page: shortcut hints come from the catalog ('{hints}')",
            hints == CommandCatalog.Hints(CommandCatalog.QuickOpen, CommandCatalog.ShowCommands, CommandCatalog.OpenRecent, CommandCatalog.NewAsset, CommandCatalog.SaveAll)
            && hints.Contains("Ctrl+S Save all"));

        // ── Every command is bound, and every one that applies is in the palette with its key ──
        Check($"registry: every catalog command is bound ({string.Join(", ", vm.Registry.Unbound())})", !vm.Registry.Unbound().Any());
        vm.OpenByName("wpn_ar_havoc_zm_upgraded");
        vm.OpenByName("wpn_smg_riot");
        Pump();
        vm.OpenCommandPaletteCommand.Execute(null);
        var listed = vm.PaletteResults.Select(r => r.CommandId).ToHashSet();
        var missing = vm.Registry.All.Where(c => c.CanRun && !listed.Contains(c.Id)).Select(c => c.Id).ToList();
        Check($"palette: lists every command that applies ({vm.PaletteResults.Count}; missing: {string.Join(", ", missing)})", missing.Count == 0);
        // A key shows only where it works from: a window key always, a scoped one (Delete, Space) never in the palette.
        var wrongKeys = vm.PaletteResults.Where(r => r.Shortcut != (CommandCatalog.Get(r.CommandId!) is { Scope: CommandScope.Window } info ? info.GestureText : ""))
            .Select(r => r.Name).ToList();
        Check($"palette: every row shows its command's key, and only window keys ({string.Join(", ", wrongKeys)})",
            wrongKeys.Count == 0 && vm.PaletteResults.Any(r => r.CommandId == CommandCatalog.Delete && r.Shortcut == ""));
        var hiddenWhenNoTab = new MainViewModel();
        hiddenWhenNoTab.OpenCommandPaletteCommand.Execute(null);
        Check("palette: commands that need an open asset are left out while none is open",
            hiddenWhenNoTab.PaletteResults.All(r => r.CommandId != CommandCatalog.Compare && r.CommandId != CommandCatalog.Rename));
        vm.ClosePaletteCommand.Execute(null);
        Pump();

        // ── Palette keystrokes: a longer query searches the last one's matches; the list keeps its lines ──
        // Hidden palette lines (no Item) and compare's rows torn down on close must bind quietly: a binding warning per
        // line per keystroke is noise that buries real ones.
        using var bindingWarnings = BindingWarnings.Capture();
        var paletteList = window.FindControl<ListBox>("PaletteList")!;
        vm.OpenPaletteCommand.Execute(null);
        Pump(); // the palette box takes focus, as it does before anyone can type
        vm.PaletteText = "wpn_ar";
        window.UpdateLayout();
        var wide = vm.PaletteResults.Select(r => r.Name).ToList();
        var firstLine = paletteList.ContainerFromIndex(0);
        vm.PaletteText = "wpn_ar_havoc";
        window.UpdateLayout();
        Check($"palette narrowing: typing on narrows ({string.Join(", ", vm.PaletteResults.Select(r => r.Name))})",
            vm.PaletteResults.Count > 0 && vm.PaletteResults.All(r => r.Name.Contains("wpn_ar_havoc")));
        var firstText = firstLine?.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList() ?? new();
        Check($"palette lines: a keystroke changes what a line shows, not the line ({string.Join(" | ", firstText.Where(t => t?.Length > 0))})",
            firstLine is not null && ReferenceEquals(paletteList.ContainerFromIndex(0), firstLine) && firstText.Contains(vm.PaletteResults[0].Name));
        vm.PaletteText = "wpn_ar";
        Check("palette narrowing: backspacing widens again to the full result",
            vm.PaletteResults.Select(r => r.Name).SequenceEqual(wide));
        Key(window, K.Down);
        var down = (paletteList.SelectedIndex, vm.PaletteSelection?.Name);
        Key(window, K.Up);
        Key(window, K.Up); // already on the first line: stays there
        Check($"palette keys: Down and Up move the highlight ({down}, then {paletteList.SelectedIndex})",
            down == (1, wide[1]) && paletteList.SelectedIndex == 0 && vm.PaletteSelection?.Name == wide[0]);
        vm.PaletteText = "wpn_ar_havoc_zm_upgraded";
        Key(window, K.Enter);
        Check($"palette keys: Enter opens the selected asset ({vm.ActiveTab?.Name})",
            !vm.IsPaletteOpen && vm.ActiveTab?.Name == "wpn_ar_havoc_zm_upgraded");

        // ── Palette with real keys: Ctrl+Shift+P, type, the row shows name and key, Enter runs it ──
        Key(window, K.P, RawInputModifiers.Control | RawInputModifiers.Shift);
        window.KeyTextInput("compare");
        Pump();
        var row = paletteList.ContainerFromIndex(0);
        var rowTexts = row?.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).Where(t => !string.IsNullOrEmpty(t)).ToList() ?? new();
        Check($"palette keys: '>compare' lists Compare with its key ({string.Join(" | ", rowTexts)})",
            rowTexts.Contains("Asset: Compare") && rowTexts.Contains(CommandCatalog.Get(CommandCatalog.Compare).GestureText));
        Capture(window, Path.Combine(outDir, "40-palette-command-keys.png"));
        Key(window, K.Enter);
        Check("palette keys: Enter runs it (compare opens)", vm.IsCompareOpen && !vm.IsPaletteOpen);
        Key(window, K.Escape);
        Check("palette keys: Esc closes compare", !vm.IsCompareOpen);
        Pump();
        var noisy = bindingWarnings.Stop().Where(w => w.Contains("ColumnWidth") || w.Contains("PaletteRow")).ToList();
        Check($"bindings: palette lines and compare's close log no binding warnings ({noisy.Count}{string.Concat(noisy.Distinct().Take(2).Select(w => "; " + w))})",
            noisy.Count == 0);

        // ── Shortcuts fire their commands (real key presses on the window) ──
        var explorer = vm.ShowExplorer;
        Key(window, K.B, RawInputModifiers.Control);
        Check("keys: Ctrl+B hides the Explorer", vm.ShowExplorer != explorer);
        var strip = window.FindControl<Button>("ExplorerStrip")!;
        Check($"tooltip: the collapsed Explorer strip says what it does ('{ToolTip.GetTip(strip)}')",
            ToolTip.GetTip(strip) as string == "Show Explorer (Ctrl+B)");
        Key(window, K.B, RawInputModifiers.Control);
        Check("keys: Ctrl+B brings it back", vm.ShowExplorer == explorer);

        Key(window, K.D, RawInputModifiers.Control);
        Check("keys: Ctrl+D opens compare", vm.IsCompareOpen);
        Key(window, K.Escape);

        // APE's New GDT and New Tab.
        Key(window, K.N, RawInputModifiers.Control | RawInputModifiers.Shift);
        Check("keys (APE): Ctrl+Shift+N asks for a new GDT", vm.IsNewOpen && vm.NewIsGdt);
        Key(window, K.Escape);
        Check("keys: Esc closes it", !vm.IsNewOpen);

        var tabsBefore = vm.OpenTabs.Count;
        Key(window, K.T, RawInputModifiers.Control);
        Check($"keys (APE): Ctrl+T opens Quick Open for a new tab ('{vm.PaletteHint}')", vm.IsPaletteOpen && vm.PaletteHint.StartsWith("New tab"));
        window.KeyTextInput("wpn_snp_locus");
        Pump();
        Key(window, K.Enter);
        Check($"keys (APE): the pick opens in its own kept tab ({vm.ActiveTab?.Name}, preview: {vm.ActiveTab?.IsPreview})",
            vm.ActiveTab is { Name: "wpn_snp_locus", IsPreview: false } && vm.OpenTabs.Count == tabsBefore + 1);

        var closing = vm.ActiveTab;
        Key(window, K.W, RawInputModifiers.Control);
        Check("keys (APE): Ctrl+W closes the tab", !vm.OpenTabs.Contains(closing!));

        // ── Focus rule: a key a field owns stays the field's ──
        vm.OpenByName("wpn_ar_havoc_zm_upgraded");
        Pump(100);
        var editor = window.GetVisualDescendants().OfType<AssetEditorView>().First();
        var number = editor.GetVisualDescendants().OfType<ScrubNumberBox>().First(n => n.IsEffectivelyVisible);
        number.Focus();
        Pump();
        Key(window, K.F2);
        var focused = window.FocusManager?.GetFocusedElement() as Control;
        Check($"focus: F2 in a number field edits the number, not the asset name ({focused?.GetType().Name})",
            !vm.IsRenameOpen && focused is TextBox);
        Key(window, K.Escape);
        Pump();

        // Typing in a text field never triggers a bare-key command: Delete and Space type, they don't delete or play.
        var search = window.GetVisualDescendants().OfType<AssetBrowserView>().First().FindControl<TextBox>("SearchBox")!;
        search.Focus();
        window.KeyTextInput("havoc");
        Key(window, K.Space);
        window.KeyTextInput(" ");
        Key(window, K.Delete);
        Pump();
        Check($"focus: Space and Delete in the Explorer search stay text ('{search.Text}', confirm open: {vm.IsConfirmOpen})",
            !vm.IsConfirmOpen && !vm.IsRenameOpen && search.Text?.StartsWith("havoc") == true);
        var typedSpace = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = K.Space, Source = search };
        Check("focus: the anim preview's Space is typing when it comes from a text field",
            CommandCatalog.IsTyping(CommandCatalog.Get(CommandCatalog.PlayPause), typedSpace));
        var chord = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = K.B, KeyModifiers = KeyModifiers.Control, Source = search };
        Check("focus: a Ctrl chord still runs from a text field",
            !CommandCatalog.IsTyping(CommandCatalog.Get(CommandCatalog.ToggleExplorer), chord));
        Key(window, K.B, RawInputModifiers.Control);
        Check("focus: Ctrl+B from the search box hides the Explorer", !vm.ShowExplorer);
        Key(window, K.B, RawInputModifiers.Control);
        search.Text = "";
        vm.ApplyFilterNow();
        Pump();

        // ── Menus and tooltips read the catalog ──
        var undo = window.FindControl<Button>("UndoButton")!;
        var redo = window.FindControl<Button>("RedoButton")!;
        Check($"tooltips: undo and redo ('{ToolTip.GetTip(undo)}', '{ToolTip.GetTip(redo)}')",
            ToolTip.GetTip(undo) as string == "Undo (Ctrl+Z)" && ToolTip.GetTip(redo) as string == "Redo (Ctrl+Y or Ctrl+Shift+Z)");
        var compare = editor.GetVisualDescendants().OfType<Button>().First(b => b.Content as string == "Compare");
        Check($"tooltips: Compare ('{ToolTip.GetTip(compare)}')", ToolTip.GetTip(compare) as string == "Compare (Ctrl+D)");

        var more = editor.GetVisualDescendants().OfType<Button>().First(b => Avalonia.Automation.AutomationProperties.GetName(b) == "More actions");
        Click(window, more, MouseButton.Left);
        var flyout = (MenuFlyout)more.Flyout!;
        var items = flyout.Items.OfType<MenuItem>().ToList();
        var rename = items.First(i => i.Header as string == "Rename…");
        var named = items.Select(i => i.Header as string).ToHashSet();
        var apeActions = new[] { CommandCatalog.Derive, CommandCatalog.Underive, CommandCatalog.DuplicateTo, CommandCatalog.MoveTo,
            CommandCatalog.GoToParent, CommandCatalog.RestorePrevious, CommandCatalog.UndoAllChanges }.Select(id => CommandCatalog.Get(id).Name).ToList();
        Check($"menu: the ⋯ menu names commands and shows their keys ({string.Join(" · ", items.Select(i => $"{i.Header}{(i.InputGesture is { } g ? " " + KeyText.Format(g) : "")}"))})",
            rename.InputGesture?.Key == K.F2 && items.All(i => i.Command is not null)
            // APE's asset actions are all here (Undo all changes left the Inspector for here and the palette).
            && apeActions.All(named.Contains)
            // Delete is here, without the Delete key: that key is the Explorer's, and does nothing in the editor.
            && items.First(i => i.Header as string == "Delete").InputGesture is null);
        Capture(window, Path.Combine(outDir, "41-editor-more-menu.png"));
        if (TopLevel.GetTopLevel(rename) is { } popup)
        {
            var at = rename.TranslatePoint(new Point(rename.Bounds.Width / 2, rename.Bounds.Height / 2), popup)!.Value;
            popup.MouseDown(at, MouseButton.Left);
            popup.MouseUp(at, MouseButton.Left);
            Pump();
        }
        Check("menu: clicking Rename… opens the rename dialog", vm.IsRenameOpen);
        Key(window, K.Escape);
        Pump();

        // The Layout menu: checkable toggles with their keys.
        var layout = window.GetVisualDescendants().OfType<Button>().First(b => ToolTip.GetTip(b) as string == "Panes and the preview layout");
        Click(window, layout, MouseButton.Left);
        var layoutItems = ((MenuFlyout)layout.Flyout!).Items.OfType<MenuItem>().ToList();
        var explorerItem = layoutItems.First(i => i.Header as string == "Show Explorer");
        Check($"menu: Layout lists Show Explorer checked with Ctrl+B ({explorerItem.IsChecked}, {explorerItem.InputGesture})",
            explorerItem.IsChecked && explorerItem.InputGesture?.Key == K.B && explorerItem.ToggleType == MenuItemToggleType.CheckBox);
        var layoutIds = new[]
        {
            CommandCatalog.MaximizePreview, CommandCatalog.ToggleExplorer, CommandCatalog.ToggleInspector, CommandCatalog.PreviewWindow,
        };
        Check($"menu: Layout holds only the panes and the preview layout, no theme or Updates ({string.Join(", ", layoutItems.Select(i => i.Header))})",
            layoutItems.Select(i => i.Header as string).SequenceEqual(layoutIds.Select(id => CommandCatalog.Get(id).Name)));
        Capture(window, Path.Combine(outDir, "42-layout-menu.png"));
        ((MenuFlyout)layout.Flyout!).Hide();
        Pump();

        // Menus print keys in the catalog's format too ("Alt+1", not the enum's "Alt+D1").
        var group = window.GetVisualDescendants().OfType<Button>().First(b => ToolTip.GetTip(b) as string == "How the Explorer groups assets");
        Click(window, group, MouseButton.Left);
        var byGdt = ((MenuFlyout)group.Flyout!).Items.OfType<MenuItem>().First();
        var shown = byGdt.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).FirstOrDefault(t => t?.StartsWith("Alt") == true);
        Check($"menu: keys print as the catalog writes them ('{byGdt.Header}' shows '{shown}')",
            byGdt.Header as string == "Group by GDT file" && shown == "Alt+1");
        Capture(window, Path.Combine(outDir, "43-group-menu.png"));
        ((MenuFlyout)group.Flyout!).Hide();
        Pump();

        window.Close();
        // The palette and search menu timed on the real install follow in the full run: the live-timing group.
    }

    /// <summary>The catalog on its own: unique keys, APE's shortcuts kept, no AltGr chords, names in sentence case.</summary>
    private static void CatalogChecks()
    {
        var all = CommandCatalog.All;
        var ids = all.GroupBy(c => c.Id).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Check($"catalog: ids are unique ({string.Join(", ", ids)})", ids.Count == 0);

        // No key means two things where it can be heard: not within a scope, and a window key is never reused by a
        // scoped command (the scope would shadow it). Home is the one deliberate nesting: the viewport frames on Home
        // except inside an anim preview, where the transport's First frame owns it (the viewports' HomeFrames).
        static string G(KeyGesture g) => KeyText.Format(g);
        var pairs = all.SelectMany(c => c.Gestures.Select(g => (Command: c, Gesture: g))).ToList();
        var inScope = pairs.GroupBy(p => (p.Command.Scope, G(p.Gesture))).Where(g => g.Count() > 1)
            .Select(g => $"{g.Key.Item2} in {g.Key.Scope}: {string.Join("/", g.Select(p => p.Command.Id))}").ToList();
        Check($"catalog: every gesture is unique within its scope ({string.Join("; ", inScope)})", inScope.Count == 0);
        var windowKeys = pairs.Where(p => p.Command.Scope == CommandScope.Window).Select(p => G(p.Gesture)).ToHashSet();
        var shadowed = pairs.Where(p => p.Command.Scope != CommandScope.Window && windowKeys.Contains(G(p.Gesture)))
            .Select(p => $"{G(p.Gesture)} ({p.Command.Id})").ToList();
        Check($"catalog: no scoped key shadows a window key ({string.Join(", ", shadowed)})", shadowed.Count == 0);
        var crossScope = pairs.Where(p => p.Command.Scope != CommandScope.Window)
            .GroupBy(p => G(p.Gesture)).Where(g => g.Select(p => p.Command.Id).Distinct().Count() > 1)
            .Select(g => g.Key).ToList();
        // Space is the other: Play/pause in an anim preview, Fire in a recoil preview, which are never one view.
        Check($"catalog: the only keys shared across scopes are Home and Space ({string.Join(", ", crossScope)})",
            crossScope.Order().SequenceEqual(new[] { "Home", "Space" }));

        foreach (var (gesture, id, ape) in CommandCatalog.ApeShortcuts)
        {
            var info = CommandCatalog.Get(id);
            var parsed = KeyText.Parse(gesture);
            Check($"APE parity: {gesture} ({ape}) is {info.Name}",
                info.Scope == CommandScope.Window && info.Gestures.Any(g => g.Key == parsed.Key && g.KeyModifiers == parsed.KeyModifiers));
        }

        var badNames = all.Where(c => c.Name.Length == 0 || char.IsLower(c.Name[0]) || c.Name.EndsWith('.') || c.Name.Contains('!')
                                      || c.Name.Contains("...")).Select(c => c.Name).ToList();
        Check($"catalog: names are sentence case with no trailing punctuation ({string.Join(", ", badNames)})", badNames.Count == 0);
    }
}

/// <summary>Collects Avalonia's binding warnings between <see cref="Capture"/> and <see cref="Stop"/>, as "Control (dc Type): values".</summary>
internal sealed class BindingWarnings : Avalonia.Logging.ILogSink, IDisposable
{
    private readonly Avalonia.Logging.ILogSink? _previous;
    private readonly System.Collections.Generic.List<string> _seen = new();

    private BindingWarnings(Avalonia.Logging.ILogSink? previous) => _previous = previous;

    public static BindingWarnings Capture()
    {
        var sink = new BindingWarnings(Avalonia.Logging.Logger.Sink);
        Avalonia.Logging.Logger.Sink = sink;
        return sink;
    }

    /// <summary>Puts the previous sink back (once) and returns what was seen.</summary>
    public System.Collections.Generic.List<string> Stop()
    {
        if (Avalonia.Logging.Logger.Sink == this)
            Avalonia.Logging.Logger.Sink = _previous;
        return _seen;
    }

    /// <summary>A check that throws still leaves the app's own sink in place.</summary>
    public void Dispose() => Stop();

    public bool IsEnabled(Avalonia.Logging.LogEventLevel level, string area) =>
        level >= Avalonia.Logging.LogEventLevel.Warning && area == Avalonia.Logging.LogArea.Binding
        || _previous?.IsEnabled(level, area) == true;

    public void Log(Avalonia.Logging.LogEventLevel level, string area, object? source, string messageTemplate) =>
        Log(level, area, source, messageTemplate, Array.Empty<object?>());

    public void Log(Avalonia.Logging.LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues)
    {
        if (level >= Avalonia.Logging.LogEventLevel.Warning && area == Avalonia.Logging.LogArea.Binding)
            _seen.Add($"{source?.GetType().Name} (dc {(source as StyledElement)?.DataContext?.GetType().Name}): {string.Join(" | ", propertyValues)}");
        if (_previous?.IsEnabled(level, area) == true)
            _previous.Log(level, area, source, messageTemplate, propertyValues);
    }
}