using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace Apex.Editor.Commands;

/// <summary>
/// Every command in Apex, defined once: id, name, category, keys and scope. APE's own shortcuts (Ctrl+N, Ctrl+Shift+N,
/// Ctrl+S, Ctrl+Z, Ctrl+Y, Ctrl+T, Ctrl+W, read from asseteditor_modtools.exe's action table) are listed in
/// <see cref="ApeShortcuts"/> and must stay bound to the same actions here.
/// </summary>
public static class CommandCatalog
{
    // ── Asset (APE's File menu, plus what Apex adds per asset) ──
    public const string NewAsset = "asset.new";
    public const string NewGdt = "gdt.new";
    public const string SaveAll = "file.saveAll";
    public const string RestorePrevious = "gdt.restorePrevious";
    public const string Rename = "asset.rename";
    public const string Duplicate = "asset.duplicate";
    public const string DuplicateTo = "asset.duplicateTo";
    public const string MoveTo = "asset.moveTo";
    public const string CopyAssets = "asset.copy";
    public const string CutAssets = "asset.cut";
    public const string PasteAssets = "asset.paste";
    public const string Derive = "asset.derive";
    public const string Underive = "asset.underive";
    public const string Delete = "asset.delete";
    public const string Compare = "asset.compare";
    public const string Pin = "asset.pin";
    public const string Reveal = "asset.reveal";
    public const string CopyName = "asset.copyName";
    public const string UndoAllChanges = "asset.undoAll";
    public const string GoToParent = "asset.goToParent";
    public const string GoToReference = "asset.goToReference";
    public const string DiscardAll = "session.discardAll";

    // ── Edit ──
    public const string Undo = "edit.undo";
    public const string Redo = "edit.redo";
    public const string FilterProperties = "edit.filterProperties";

    // ── Editor views (APE's View menu: Normal / Specified / Non-Default) ──
    public const string ShowAllProperties = "editor.viewAll";
    public const string ShowChangedProperties = "editor.viewChanged";
    public const string ShowProblemProperties = "editor.viewProblems";
    public const string ShowOverrides = "editor.viewOverrides";
    public const string ShowSetProperties = "editor.viewSet";
    public const string AddProperty = "editor.addProperty";

    // ── Go ──
    public const string QuickOpen = "go.asset";
    public const string ShowCommands = "go.commands";
    public const string OpenRecent = "go.recent";
    public const string GoToProperty = "go.property";
    public const string Back = "go.back";
    public const string Forward = "go.forward";

    // ── Tabs ──
    public const string NewTab = "tab.new";
    public const string CloseTab = "tab.close";
    public const string CloseOtherTabs = "tab.closeOthers";
    public const string NextTab = "tab.next";
    public const string PreviousTab = "tab.previous";
    public const string ShowAllTabs = "tab.all";

    // ── View ──
    public const string ToggleExplorer = "view.explorer";
    public const string ToggleInspector = "view.inspector";
    public const string MaximizePreview = "view.maximizePreview";
    public const string PreviewWindow = "view.previewWindow";
    public const string NextPane = "view.nextPane";
    public const string PreviousPane = "view.previousPane";
    public const string ThemeSystem = "view.themeSystem";
    public const string ThemeGraphite = "view.themeGraphite";
    public const string ThemeSlate = "view.themeSlate";
    public const string ThemeLight = "view.themeLight";
    public const string CheckForUpdates = "app.checkUpdates";
    public const string About = "app.about";

    // ── Explorer ──
    public const string SearchAssets = "explorer.search";
    public const string GroupByGdt = "explorer.groupByGdt";
    public const string GroupByType = "explorer.groupByType";
    public const string ShowChangedAssets = "explorer.showChanged";
    public const string ShowProblemAssets = "explorer.showProblems";
    public const string ClearFilters = "explorer.clearFilters";
    public const string OpenAsTable = "explorer.openTable";
    public const string CollapseAll = "explorer.collapseAll";

    // ── Preview ──
    public const string PlayPause = "preview.playPause";
    public const string PreviousFrame = "preview.previousFrame";
    public const string NextFrame = "preview.nextFrame";
    public const string FirstFrame = "preview.firstFrame";
    public const string LastFrame = "preview.lastFrame";
    public const string FramePreview = "preview.frame";
    public const string Fire = "preview.fire";
    public const string AimDownSights = "preview.aimDownSights";
    public const string ResetRecoil = "preview.resetRecoil";

    private const string CatAsset = "Asset";
    private const string CatEdit = "Edit";
    private const string CatEditor = "Editor";
    private const string CatGo = "Go";
    private const string CatTab = "Tab";
    private const string CatView = "View";
    private const string CatExplorer = "Explorer";
    private const string CatPreview = "Preview";

    private static CommandInfo C(string id, string name, string category, string glyph, params string[] gestures) =>
        new(id, name, category, glyph, CommandScope.Window, gestures);

    private static CommandInfo S(CommandScope scope, string id, string name, string glyph, params string[] gestures) =>
        new(id, name, CatPreview, glyph, scope, gestures);

    /// <summary>In palette order: the commands used most sit first when nothing is typed.</summary>
    public static IReadOnlyList<CommandInfo> All { get; } = new[]
    {
        C(QuickOpen, "Go to asset…", CatGo, "⌕", "Ctrl+P") with { Keywords = "quick open search find" },
        C(ShowCommands, "Show all commands", CatGo, "›", "Ctrl+Shift+P") with { Keywords = "palette" },
        C(OpenRecent, "Open recent…", CatGo, "◷", "Ctrl+E"),
        C(GoToProperty, "Go to property…", CatGo, "≡") with { Keywords = "field key" },
        C(Back, "Back", CatGo, "‹", "Alt+Left") with { Keywords = "history previous" },
        C(Forward, "Forward", CatGo, "›", "Alt+Right") with { Keywords = "history next" },

        C(NewAsset, "New asset…", CatAsset, "+", "Ctrl+N") with { Keywords = "create" },
        C(NewGdt, "New GDT…", CatAsset, "+", "Ctrl+Shift+N") with { Keywords = "create file" },
        C(SaveAll, "Save all", CatAsset, "↓", "Ctrl+S") with { Keywords = "write gdt files" },
        C(RestorePrevious, "Restore previous version of this GDT", CatAsset, "↶") with { Keywords = "backup revert undo save file" },
        C(Compare, "Compare", CatAsset, "⇄", "Ctrl+D") with { Keywords = "diff" },
        C(Rename, "Rename…", CatAsset, "✎", "F2"),
        C(Duplicate, "Duplicate", CatAsset, "⎘") with { Keywords = "copy clone" },
        C(DuplicateTo, "Duplicate to…", CatAsset, "⎘") with { Keywords = "copy clone another gdt" },
        C(MoveTo, "Move to…", CatAsset, "→") with { Keywords = "another gdt file relocate" },
        C(Derive, "Derive", CatAsset, "↳") with { Keywords = "inherit child parent template based on" },
        C(Underive, "Underive", CatAsset, "⇤") with { Keywords = "flatten detach parent inherited values root" },
        new CommandInfo(CopyAssets, "Copy", CatAsset, "⧉", CommandScope.Explorer, "Ctrl+C") with { Keywords = "clipboard assets paste" },
        new CommandInfo(CutAssets, "Cut", CatAsset, "✂", CommandScope.Explorer, "Ctrl+X") with { Keywords = "clipboard move assets paste" },
        new CommandInfo(PasteAssets, "Paste", CatAsset, "⎗", CommandScope.Explorer, "Ctrl+V") with { Keywords = "clipboard assets gdt" },
        new CommandInfo(Delete, "Delete", CatAsset, "✕", CommandScope.Explorer, "Delete") with { Keywords = "remove" },
        C(Pin, "Pin to Explorer", CatAsset, "⇱") with { IsToggle = true, Keywords = "unpin favorite" },
        C(Reveal, "Reveal in Explorer", CatAsset, "◎", "Alt+F1") with { Keywords = "locate show tree" },
        C(CopyName, "Copy name", CatAsset, "⧉") with { Keywords = "clipboard" },
        C(GoToParent, "Open parent", CatAsset, "↑") with { Keywords = "template derived based on" },
        C(GoToReference, "Go to reference", CatAsset, "→", "F12") with { Keywords = "follow open referenced" },
        C(UndoAllChanges, "Undo all changes", CatAsset, "↶") with { Keywords = "revert discard" },
        C(DiscardAll, "Discard all changes", CatAsset, "✕") with { Keywords = "session revert every change reset undo" },

        C(Undo, "Undo", CatEdit, "↶", "Ctrl+Z"),
        C(Redo, "Redo", CatEdit, "↷", "Ctrl+Y", "Ctrl+Shift+Z"),
        C(FilterProperties, "Filter properties", CatEdit, "⌕", "Ctrl+F") with { Keywords = "find search" },

        C(ShowAllProperties, "Show all properties", CatEditor, "≡") with { Keywords = "normal view" },
        C(ShowSetProperties, "Show set properties", CatEditor, "◉") with { Keywords = "view specified values held" },
        C(AddProperty, "Add property", CatEditor, "+") with { Keywords = "set key unset new show" },
        C(ShowChangedProperties, "Show changed properties", CatEditor, "●") with { Keywords = "view session edits" },
        C(ShowProblemProperties, "Show properties with problems", CatEditor, "⚠") with { Keywords = "view validation errors" },
        C(ShowOverrides, "Show overrides", CatEditor, "◐") with { Keywords = "view specified parent differs" },

        C(NewTab, "New tab…", CatTab, "+", "Ctrl+T") with { Keywords = "open" },
        C(CloseTab, "Close tab", CatTab, "✕", "Ctrl+W"),
        C(CloseOtherTabs, "Close other tabs", CatTab, "✕"),
        C(NextTab, "Next tab", CatTab, "→", "Ctrl+Tab") with { Keywords = "switch cycle recent" },
        C(PreviousTab, "Previous tab", CatTab, "←", "Ctrl+Shift+Tab") with { Keywords = "switch cycle recent" },
        C(ShowAllTabs, "Show all tabs", CatTab, "▾", "Ctrl+Shift+A") with { Keywords = "list overflow" },

        C(ToggleExplorer, "Show Explorer", CatView, "◧", "Ctrl+B") with { IsToggle = true, Keywords = "hide toggle sidebar" },
        C(ToggleInspector, "Show Inspector", CatView, "◨", "Ctrl+Shift+B") with { IsToggle = true, Keywords = "hide toggle" },
        C(MaximizePreview, "Maximize preview", CatView, "⤢", "Ctrl+Shift+M")
            with { IsToggle = true, OnName = "Restore preview", Keywords = "layout restore editing" },
        C(PreviewWindow, "Preview in its own window", CatView, "⧉", "Ctrl+Shift+O")
            with { IsToggle = true, OnName = "Dock preview", Keywords = "float pop out dock" },
        C(NextPane, "Focus next pane", CatView, "→", "F6"),
        C(PreviousPane, "Focus previous pane", CatView, "←", "Shift+F6"),
        C(ThemeSystem, "Match Windows theme", CatView, "◑") with { IsToggle = true, Keywords = "appearance dark light mode system colour color" },
        C(ThemeGraphite, "Graphite theme", CatView, "●") with { IsToggle = true, Keywords = "appearance dark mode grey gray colour color" },
        C(ThemeSlate, "Slate theme", CatView, "●") with { IsToggle = true, Keywords = "appearance dark mode blue colour color" },
        C(ThemeLight, "Light theme", CatView, "○") with { IsToggle = true, Keywords = "appearance light mode white day colour color" },
        C(CheckForUpdates, "Check for updates", CatView, "↓") with { Keywords = "update updates new version release latest github download install" },
        C(About, "About Apex", CatView, "ⓘ") with { Keywords = "version info information release" },

        C(SearchAssets, "Search assets", CatExplorer, "⌕", "Ctrl+Shift+F") with { Keywords = "filter find" },
        C(GroupByGdt, "Group by GDT file", CatExplorer, "▣", "Alt+D1") with { IsToggle = true },
        C(GroupByType, "Group by type", CatExplorer, "▣", "Alt+D2") with { IsToggle = true },
        C(ShowChangedAssets, "Show changed assets", CatExplorer, "●") with { Keywords = "review session edits" },
        C(ShowProblemAssets, "Show assets with problems", CatExplorer, "⚠") with { IsToggle = true, Keywords = "validation errors" },
        C(ClearFilters, "Clear all filters", CatExplorer, "✕") with { Keywords = "reset search" },
        new CommandInfo(CollapseAll, "Collapse all", CatExplorer, "⊟", CommandScope.Explorer, "Ctrl+Left") with { Keywords = "fold close groups tree" },
        C(OpenAsTable, "Open results as table", CatExplorer, "⊞") with { Keywords = "spreadsheet bulk edit" },

        S(CommandScope.AnimPreview, PlayPause, "Play/pause", "▶", "Space"),
        S(CommandScope.AnimPreview, PreviousFrame, "Previous frame", "◀", "OemComma", "Left"),
        S(CommandScope.AnimPreview, NextFrame, "Next frame", "▶", "OemPeriod", "Right"),
        S(CommandScope.AnimPreview, FirstFrame, "First frame", "⏮", "Home"),
        S(CommandScope.AnimPreview, LastFrame, "Last frame", "⏭", "End"),
        S(CommandScope.Viewport, FramePreview, "Frame", "⛶", "F", "Home") with { Keywords = "fit zoom reset camera" },
        S(CommandScope.RecoilPreview, Fire, "Fire", "•", "Space") with { Keywords = "shoot recoil kick trigger" },
        S(CommandScope.RecoilPreview, AimDownSights, "Aim down sights", "◎", "A") with { IsToggle = true, Keywords = "ads hip recoil" },
        S(CommandScope.RecoilPreview, ResetRecoil, "Reset recoil", "↺", "R") with { Keywords = "kick burst rest" },
    };

    /// <summary>
    /// What Quick Open does, in the one sentence every place that describes it uses (the title bar's box, the palette's
    /// own placeholder), so the palette is described the same way wherever it is met.
    /// </summary>
    public const string PaletteSentence = "Go to asset, or type > for commands, @ for a property";

    /// <summary>The title bar's box is too narrow for <see cref="PaletteSentence"/>, so it says the short form; the sentence is its tooltip.</summary>
    public const string PaletteBoxText = "Go to asset or command";

    private static readonly Dictionary<string, CommandInfo> ById = All.ToDictionary(c => c.Id);

    public static CommandInfo Get(string id) =>
        ById.TryGetValue(id, out var info) ? info : throw new ArgumentException($"No command '{id}'", nameof(id));

    /// <summary>The command a key press means in <paramref name="scope"/>, or null.</summary>
    public static CommandInfo? Match(CommandScope scope, KeyEventArgs e)
    {
        foreach (var c in All)
            if (c.Scope == scope && c.Matches(e))
                return c;
        return null;
    }

    /// <summary>
    /// The focus rule: a bare key (Space, F, Delete, a comma) pressed while a text field has focus is typing, so the
    /// command it would trigger stays quiet. Chords and function keys still run.
    /// </summary>
    public static bool IsTyping(CommandInfo command, KeyEventArgs e)
    {
        if (command.Gestures.FirstOrDefault(g => g.Matches(e)) is not { } gesture || !KeyText.IsTypingKey(gesture))
            return false;
        for (var v = e.Source as Visual; v is not null; v = v.GetVisualParent())
            if (v is TextBox { IsReadOnly: false })
                return true;
        return false;
    }

    /// <summary>True when <paramref name="e"/> is <paramref name="id"/>'s key (any scope).</summary>
    public static bool Is(string id, KeyEventArgs e) => Get(id).Matches(e);

    /// <summary>
    /// APE's keyboard shortcuts, confirmed from asseteditor_modtools.exe (its retranslateUi assigns exactly these seven
    /// to QActions: New Asset, New GDT, Save All, Undo, Redo, New Tab, Close Tab). Apex keeps each one on the same action.
    /// </summary>
    public static IReadOnlyList<(string Gesture, string CommandId, string ApeAction)> ApeShortcuts { get; } = new[]
    {
        ("Ctrl+N", NewAsset, "New Asset..."),
        ("Ctrl+Shift+N", NewGdt, "New GDT..."),
        ("Ctrl+S", SaveAll, "Save All"),
        ("Ctrl+Z", Undo, "Undo"),
        ("Ctrl+Y", Redo, "Redo"),
        ("Ctrl+T", NewTab, "New Tab"),
        ("Ctrl+W", CloseTab, "Close Tab"),
    };

    /// <summary>"Ctrl+P Go to asset · Ctrl+Shift+P Show all commands …" for hint lines such as the start page's.</summary>
    public static string Hints(params string[] ids) =>
        string.Join("  ·  ", ids.Select(Get).Select(c => $"{c.GestureText} {c.Name.TrimEnd('…')}"));
}
