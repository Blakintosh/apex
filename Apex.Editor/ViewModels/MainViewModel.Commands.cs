using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Apex.Editor.Commands;
using Apex.Editor.Services;
using CommunityToolkit.Mvvm.Input;
using K = Apex.Editor.Commands.CommandCatalog;

namespace Apex.Editor.ViewModels;

/// <summary>
/// What each catalog command does in this window, and when it applies. Menus, buttons, the palette and the keyboard
/// all run these; nothing else in the shell names a command or its keys.
/// </summary>
public sealed partial class MainViewModel
{
    private CommandRegistry? _registry;
    private AssetEditorViewModel? _registryTab;

    /// <summary>Every command, bound to this window.</summary>
    public CommandRegistry Registry => _registry ??= BuildRegistry();

    /// <summary>The window, for commands that move focus, open a menu at a control or use the clipboard.</summary>
    public IShellView? Shell { get; set; }

    private CommandRegistry BuildRegistry()
    {
        // Until an install is found only Locate… applies: every command is off, keys and palette included.
        var r = new CommandRegistry { Suspended = () => IsInstallMissing };
        bool Tab() => ActiveTab is not null;
        // An xanim's properties are a panel of its own with no Set tab: the view isn't offered there.
        bool SetView() => ActiveTab is { IsAnim: false };

        // Go
        r.Bind(K.QuickOpen, OpenPalette);
        r.Bind(K.ShowCommands, OpenCommandPalette);
        r.Bind(K.OpenRecent, OpenRecentPalette);
        r.Bind(K.GoToProperty, OpenPropertyPalette, Tab);
        r.Bind(K.Back, GoBack, () => CanGoBack);
        r.Bind(K.Forward, GoForward, () => CanGoForward);

        // Asset
        r.Bind(K.NewAsset, NewAsset);
        r.Bind(K.NewGdt, NewGdt);
        r.Bind(K.SaveAll, SaveActive);
        r.Bind(K.RestorePrevious, RestorePrevious, CanRestorePrevious);
        r.Bind(K.Compare, OpenCompare, Tab);
        r.Bind(K.Rename, OpenRename, Tab);
        r.Bind(K.Duplicate, DuplicateActive, Tab);
        r.Bind(K.DuplicateTo, () => DuplicateTo(new[] { ActiveTab!.Record }), Tab);
        r.Bind(K.MoveTo, () => MoveTo(new[] { ActiveTab!.Record }), Tab);
        r.Bind(K.Derive, () => DeriveAsset(ActiveTab!.Record), Tab);
        r.Bind(K.Underive, () => UnderiveAsset(ActiveTab!.Record), () => ActiveTab is { HasParent: true, IsParentMissing: false });
        r.Bind(K.CopyAssets, () => CopyAssets(new[] { ActiveTab!.Record }), Tab);
        r.Bind(K.CutAssets, () => CutAssets(new[] { ActiveTab!.Record }), Tab);
        r.Bind(K.PasteAssets, PasteFromPalette, () => CanPasteAssets);
        r.Bind(K.Delete, DeleteActive, Tab);
        r.Bind(K.Pin, TogglePinActive, Tab, () => IsActivePinned);
        r.Bind(K.Reveal, RevealActiveInTree, Tab);
        r.Bind(K.CopyName, () => Shell?.CopyText(ActiveTab!.Name), Tab);
        r.Bind(K.GoToParent, GoToParent, () => ActiveTab is { HasParent: true, IsParentMissing: false });
        r.Bind(K.GoToReference, () => FocusedReference()?.GoToCommand.Execute(null), () => FocusedReference() is { CanGoTo: true });
        r.Bind(K.UndoAllChanges, UndoAllChanges, () => ActiveTab?.Changes.Count > 0);
        r.Bind(K.DiscardAll, DiscardSession, () => HasSessionChanges || _unapplied.Count > 0);

        // Edit. Undo and redo always run: they also take back uncommitted typing, and table and compare keep their
        // own history (StepHistory decides).
        r.Bind(K.Undo, UndoActive);
        r.Bind(K.Redo, RedoActive);
        r.Bind(K.FilterProperties, () => Shell?.FocusPropertyFilter(), Tab);

        // Editor views
        r.Bind(K.ShowAllProperties, () => ActiveTab!.View = EditorView.All, Tab, () => ActiveTab?.View == EditorView.All);
        r.Bind(K.ShowSetProperties, () => ActiveTab!.View = EditorView.Set, SetView, () => ActiveTab?.View == EditorView.Set);
        r.Bind(K.AddProperty, () => { ActiveTab!.View = EditorView.Set; Shell?.OpenAddProperty(); }, SetView);
        r.Bind(K.ShowChangedProperties, () => ActiveTab!.View = EditorView.Changed, Tab, () => ActiveTab?.View == EditorView.Changed);
        r.Bind(K.ShowProblemProperties, () => ActiveTab!.View = EditorView.Problems, Tab, () => ActiveTab?.View == EditorView.Problems);
        r.Bind(K.ShowOverrides, () => ActiveTab!.View = EditorView.Overrides, () => ActiveTab?.HasParent == true,
            () => ActiveTab?.View == EditorView.Overrides);

        // Tabs
        r.Bind(K.NewTab, OpenNewTabPalette);
        r.Bind(K.CloseTab, CloseActiveTab, Tab);
        r.Bind(K.CloseOtherTabs, () => CloseOtherTabs(ActiveTab!), () => ActiveTab is not null && OpenTabs.Count > 1);
        r.Bind(K.NextTab, () => CycleTab(1), () => OpenTabs.Count > 1);
        r.Bind(K.PreviousTab, () => CycleTab(-1), () => OpenTabs.Count > 1);
        r.Bind(K.ShowAllTabs, () => Shell?.ShowAllTabs(), () => OpenTabs.Count > 0);

        // View
        r.Bind(K.ToggleExplorer, ToggleExplorer, isChecked: () => ShowExplorer);
        r.Bind(K.ToggleInspector, () =>
        {
            // In the preview layout the Inspector is tucked away regardless: bring it back with the editing layout.
            if (IsPreviewLayout)
            {
                Layout = WorkspaceLayout.Edit;
                IsInspectorVisible = true;
            }
            else
                ToggleInspector();
        }, isChecked: () => ShowInspector);
        r.Bind(K.MaximizePreview, TogglePreviewMaximized, isChecked: () => IsPreviewLayout);
        r.Bind(K.PreviewWindow, TogglePreviewFloating, isChecked: () => IsPreviewFloating);
        r.Bind(K.NextPane, () => Shell?.CyclePaneFocus(1));
        r.Bind(K.ThemeSystem, () => Theme = ThemeChoice.System, isChecked: () => Theme == ThemeChoice.System);
        r.Bind(K.ThemeGraphite, () => Theme = ThemeChoice.Graphite, isChecked: () => Theme == ThemeChoice.Graphite);
        r.Bind(K.ThemeSlate, () => Theme = ThemeChoice.Slate, isChecked: () => Theme == ThemeChoice.Slate);
        r.Bind(K.ThemeLight, () => Theme = ThemeChoice.Light, isChecked: () => Theme == ThemeChoice.Light);
        r.Bind(K.PreviousPane, () => Shell?.CyclePaneFocus(-1));
        r.Bind(K.CheckForUpdates, CheckForUpdates);
        r.Bind(K.About, () => Shell?.ShowAbout());

        // Explorer
        r.Bind(K.SearchAssets, () =>
        {
            // The search menu opens over the Explorer's box; the keyboard goes to it.
            ShowExplorerPane();
            OpenSearchMenu();
            Shell?.FocusAssetSearch();
        });
        r.Bind(K.GroupByGdt, () => Grouping = ExplorerGrouping.Gdt, isChecked: () => IsGroupedByGdt);
        r.Bind(K.GroupByType, () => Grouping = ExplorerGrouping.Type, isChecked: () => IsGroupedByType);
        r.Bind(K.ShowChangedAssets, ReviewChanges);
        r.Bind(K.ShowProblemAssets, ToggleProblemsFilter, isChecked: () => ProblemsFilter && ShowExplorer);
        r.Bind(K.ClearFilters, ClearFilter, () => HasFilter);
        r.Bind(K.CollapseAll, CollapseAll, () => CanCollapseAll);
        r.Bind(K.OpenAsTable, OpenTable);

        // Preview: the keys act on the anim preview that has focus; the palette acts on the one shown.
        foreach (var id in new[] { K.PlayPause, K.PreviousFrame, K.NextFrame, K.FirstFrame, K.LastFrame })
        {
            var captured = id;
            r.Bind(captured, () => RunAnimCommand(captured, (AnimPreviewViewModel)CurrentPreview!.Content),
                () => CurrentPreview?.Content is AnimPreviewViewModel);
        }
        r.Bind(K.FramePreview, () => Shell?.FramePreview(), () => HasPreview);
        foreach (var id in new[] { K.Fire, K.AimDownSights, K.ResetRecoil })
        {
            var captured = id;
            // Fire and Reset need a simulation to act on; ADS moves the pose without one.
            r.Bind(captured, () => RunRecoilCommand(captured, (WeaponPreviewViewModel)CurrentPreview!.Content),
                captured == K.AimDownSights
                    ? () => CurrentPreview?.Content is WeaponPreviewViewModel
                    : () => CurrentPreview?.Content is WeaponPreviewViewModel { CanFire: true },
                captured == K.AimDownSights ? () => CurrentPreview?.Content is WeaponPreviewViewModel { IsAds: true } : null);
        }

        PropertyChanged += Registry_OwnerChanged;
        return r;
    }

    /// <summary>A tab's context menu: reveal that tab's asset, which need not be the active one.</summary>
    [RelayCommand]
    private void RevealTabInTree(AssetEditorViewModel tab) => RevealInTree(tab.Record);

    private RefPropertyViewModel? FocusedReference() =>
        ActiveTab?.FocusedProperty is RefPropertyViewModel { CanGoTo: true, Value.Length: > 0 } reference ? reference : null;

    private void Registry_OwnerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ActiveTab) && !ReferenceEquals(_registryTab, ActiveTab))
        {
            // The active tab's own state (its view, its changes) feeds availability and checks too.
            if (_registryTab is not null)
                _registryTab.PropertyChanged -= RegistryTab_PropertyChanged;
            _registryTab = ActiveTab;
            if (_registryTab is not null)
                _registryTab.PropertyChanged += RegistryTab_PropertyChanged;
        }
        _registry?.RequestRefresh();
    }

    private void RegistryTab_PropertyChanged(object? sender, PropertyChangedEventArgs e) => _registry?.RequestRefresh();

    /// <summary>The anim transport: one mapping for the keys (on the focused preview) and the palette (on the shown one).</summary>
    public static void RunAnimCommand(string id, AnimPreviewViewModel anim)
    {
        switch (id)
        {
            case K.PlayPause: anim.TogglePlay(); break;
            case K.PreviousFrame: anim.StepFrame(-1); break;
            case K.NextFrame: anim.StepFrame(1); break;
            case K.FirstFrame: anim.GoToFrame(0); break;
            case K.LastFrame: anim.GoToFrame(anim.LastFrame); break;
        }
    }

    /// <summary>A recoil preview command run once (the palette, or its key in the preview: Space's hold is the view's).</summary>
    public static void RunRecoilCommand(string id, WeaponPreviewViewModel recoil)
    {
        switch (id)
        {
            case K.Fire: recoil.FireOnce(); break;
            case K.AimDownSights: recoil.ToggleAdsCommand.Execute(null); break;
            case K.ResetRecoil: recoil.ResetCommand.Execute(null); break;
        }
    }

    /// <summary>Ctrl+T, APE's New Tab: Quick Open, and the pick opens as a kept tab rather than the preview tab.</summary>
    private void OpenNewTabPalette()
    {
        OpenPalette();
        _paletteKeepsTab = true;
        PaletteHint = "New tab · type to search · ⏎ opens it in its own tab";
    }

    /// <summary>Set by <see cref="OpenNewTabPalette"/>: the asset picked opens as a kept tab.</summary>
    private bool _paletteKeepsTab;

    // ══ Palette ">" mode ═════════════════════════════════════════════════════

    /// <summary>"View: Show Explorer"; the category is left off when the name already says it ("Close tab").</summary>
    public static string PaletteTitle(CommandInfo info) =>
        info.Name.Contains(info.Category, StringComparison.OrdinalIgnoreCase) ? info.Name : $"{info.Category}: {info.Name}";

    /// <summary>
    /// Every command that applies right now, ranked against what was typed after ">". Commands that don't apply (Compare
    /// with nothing open) are left out rather than listed greyed: the palette only offers what will run.
    /// </summary>
    private List<PaletteItemViewModel> PaletteCommands(string query)
    {
        var ranked = new List<(int Score, int Order, AppCommand Command)>();
        var order = 0;
        foreach (var command in Registry.All)
        {
            order++;
            if (!command.CanRun)
                continue;
            var info = command.Info;
            var score = TextScore(PaletteTitle(info), query);
            if (score is null && info.Keywords.Length > 0 && TextScore(info.Keywords, query) is { } k)
                score = 10 + k;
            if (score is { } s)
                ranked.Add((s, order, command));
        }
        var results = ranked
            .OrderBy(x => x.Score).ThenBy(x => x.Order)
            .Take(60)
            .Select(x => PaletteItemFor(x.Command))
            .ToList();
        // The open asset's deffile buttons ("Medal 4: Add Item"), after the app's own commands.
        if (ActiveTab is { } tab)
            results.AddRange(tab.VisibleButtons
                .Select(b => (Score: TextScore(b.Title, query), Button: b))
                .Where(x => x.Score is not null)
                .OrderBy(x => x.Score)
                .Take(30)
                .Select(x => new PaletteItemViewModel(x.Button.Title, "", "▸", () => x.Button.Command.Execute(null))
                {
                    Detail = $"deffile button on {tab.Name}",
                }));
        results.AddRange(ExtensionPaletteItems(query));
        return results;
    }

    private PaletteItemViewModel PaletteItemFor(AppCommand command)
    {
        var info = command.Info;
        // A key shows only when it works from here: a scoped one (Delete, Space) is heard only inside its part of the window.
        return new PaletteItemViewModel(PaletteTitle(info), info.Scope == CommandScope.Window ? info.GestureText : "", info.Glyph, () =>
        {
            command.Execute();
            // Run from the palette, Next tab is one step: there is no held Ctrl to let go of.
            EndTabCycle();
        })
        {
            CommandId = info.Id,
            Detail = info.IsToggle ? (command.CurrentlyChecked ? "On" : "Off")
                : info.Scope switch
                {
                    CommandScope.Explorer => "in the Explorer",
                    CommandScope.AnimPreview => "in the anim preview",
                    CommandScope.Viewport => "in the preview",
                    CommandScope.RecoilPreview => "in the recoil preview",
                    _ => "",
                },
        };
    }

    /// <summary>
    /// Shows <paramref name="results"/> and selects the first. The list keeps every line it has made (hiding the ones
    /// not needed), so typing, and opening the palette again, change what lines show instead of building new ones.
    /// </summary>
    private void ShowPaletteRows(IReadOnlyList<PaletteItemViewModel> results)
    {
        _paletteResults.Clear();
        _paletteResults.AddRange(results);
        for (var i = 0; i < PaletteRows.Count; i++)
            PaletteRows[i].Item = i < results.Count ? results[i] : null;
        for (var i = PaletteRows.Count; i < results.Count; i++)
            PaletteRows.Add(new PaletteRow { Item = results[i] });
        PaletteSelectedIndex = results.Count > 0 ? 0 : -1;
        OnPropertyChanged(nameof(PaletteSelection));
    }

    // Matches kept for narrowing are only trusted while the palette stays up.
    partial void OnIsPaletteOpenChanged(bool value)
    {
        _narrowMatches = null;
        if (!value)
            _gdtPick = null;
    }
}
