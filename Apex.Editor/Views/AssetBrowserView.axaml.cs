using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Apex.Editor.Commands;
using Apex.Editor.Models;
using Apex.Editor.ViewModels;

namespace Apex.Editor.Views;

public partial class AssetBrowserView : UserControl
{
    /// <summary>
    /// Keyboard selection opens the row only once it settles: opening builds a full editor (a weapon
    /// is ~1,300 rows plus its preview), so arrow-keying down a GDT must not open every row it passes.
    /// Longer than key repeat (~33 ms), short enough that stopping on a row still feels immediate.
    /// </summary>
    private static readonly TimeSpan KeyboardOpenDelay = TimeSpan.FromMilliseconds(130);

    private MainViewModel? _hooked;
    private bool _revealing;
    // A GDT row the user picked (to paste into, or to make an asset in): a rebuild of the rows keeps it picked rather
    // than snapping back to the active asset's row. Opening or switching tabs clears it.
    private string? _pickedGdt;
    private readonly DispatcherTimer _openDelay = new() { Interval = KeyboardOpenDelay };
    private AssetRecord? _pendingOpen;
    // The button of a press still in progress (None otherwise): a left click opens at once, a right
    // click only selects the row for its context menu, and no button means the keyboard moved.
    private MouseButton _pointerButton = MouseButton.None;
    private int _pressClicks;

    public AssetBrowserView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            CancelPendingOpen();
            if (_hooked is not null)
                _hooked.RevealRequested -= Reveal;
            if (_hooked is not null)
            {
                _hooked.PropertyChanged -= Vm_PropertyChanged;
                _hooked.SearchFacets.CollectionChanged -= SearchFacets_CollectionChanged;
            }
            _hooked = Vm;
            SyncFacets();
            if (_hooked is not null)
            {
                _hooked.RevealRequested += Reveal;
                _hooked.PropertyChanged += Vm_PropertyChanged;
                _hooked.SearchFacets.CollectionChanged += SearchFacets_CollectionChanged;
                // Read when a command asks (the palette's bulk commands), never pushed on each selection change.
                _hooked.ExplorerSelection = SelectedAssets;
            }
        };
        _openDelay.Tick += (_, _) =>
        {
            var asset = _pendingOpen;
            CancelPendingOpen();
            // The tree also re-raises selection on its own when its rows are replaced; by the time
            // the delay ends that row may no longer be what the user has selected.
            if (asset is not null && SelectedAssets() is [var selected] && selected == asset)
                Vm?.OpenAsset(asset, preview: true);
        };
        Tree.AddHandler(PointerPressedEvent, Tree_PointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        Tree.AddHandler(PointerReleasedEvent, Tree_PointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        Tree.AddHandler(PointerCaptureLostEvent, (_, _) => _pointerButton = MouseButton.None, RoutingStrategies.Bubble, handledEventsToo: true);
        // Tunnel: the ListBox (and its rows) consume ←, → and Enter for their own navigation first.
        Tree.AddHandler(KeyDownEvent, Tree_KeyDown, RoutingStrategies.Tunnel);
        // Typing on a row searches, as in Windows Explorer: the letters go to the search box.
        Tree.AddHandler(TextInputEvent, Tree_TextInput, RoutingStrategies.Tunnel);
        Facets.HiddenChanged += _ => LabelFacetMore();
        DetachedFromVisualTree += (_, _) =>
        {
            CancelPendingOpen();
            _window?.RemoveHandler(PointerPressedEvent, Window_PointerPressed);
            _window = null;
        };
        AttachedToVisualTree += (_, _) =>
        {
            _window = TopLevel.GetTopLevel(this);
            _window?.AddHandler(PointerPressedEvent, Window_PointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
            PrepareSearchMenu();
        };
        SearchField.AddHandler(PointerPressedEvent, SearchField_PointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        // Tunnel: the text box claims Backspace (and the caret keys) for itself before a bubbling handler sees them.
        SearchBox.AddHandler(KeyDownEvent, SearchBox_KeyDown, RoutingStrategies.Tunnel);
        SearchMenuCard.AddHandler(KeyDownEvent, SearchMenuCard_KeyDown, RoutingStrategies.Tunnel);
        MenuResults.AddHandler(PointerReleasedEvent, MenuResults_PointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        // The row menu acts on the selected rows, so it runs its own handlers, but it names each command and shows
        // its key exactly as everywhere else.
        Label(MenuPin, CommandCatalog.Pin);
        Label(MenuCopy, CommandCatalog.CopyName);
        Label(MenuRename, CommandCatalog.Rename);
        Label(MenuDuplicate, CommandCatalog.Duplicate);
        Label(MenuDuplicateTo, CommandCatalog.DuplicateTo);
        Label(MenuMoveTo, CommandCatalog.MoveTo);
        Label(MenuDerive, CommandCatalog.Derive);
        Label(MenuUnderive, CommandCatalog.Underive);
        Label(MenuCutAssets, CommandCatalog.CutAssets);
        Label(MenuCopyAssets, CommandCatalog.CopyAssets);
        Label(MenuPasteAssets, CommandCatalog.PasteAssets);
        Label(MenuDelete, CommandCatalog.Delete);
        Label(MenuCollapseAll, CommandCatalog.CollapseAll);
        MenuPin.ToggleType = MenuItemToggleType.CheckBox;
    }

    private static void Label(MenuItem item, string id)
    {
        var info = CommandCatalog.Get(id);
        item.Header = info.Name;
        item.InputGesture = info.Gesture;
    }

    private void CancelPendingOpen()
    {
        _openDelay.Stop();
        _pendingOpen = null;
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    private List<AssetRecord> SelectedAssets() =>
        Tree.SelectedItems?.OfType<BrowserNode>().Where(n => n.Asset is not null).Select(n => n.Asset!).Distinct().ToList()
        ?? new List<AssetRecord>();

    private void Tree_ContainerPrepared(object? sender, ContainerPreparedEventArgs e)
    {
        // Section headings sit in the list but are not rows: no hover, no selection, no focus.
        if (e.Container is ListBoxItem item)
            item.Classes.Set("heading", item.DataContext is BrowserNode { IsHeader: true });
    }

    private void Tree_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var props = e.GetCurrentPoint(Tree).Properties;
        _pointerButton = props.IsLeftButtonPressed ? MouseButton.Left
            : props.IsRightButtonPressed ? MouseButton.Right
            : MouseButton.Middle;
        _pressClicks = e.ClickCount;
    }

    /// <summary>A left click on a GDT or type row opens or closes it (the second click of a double-click doesn't undo the first).</summary>
    private void Tree_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        var button = _pointerButton;
        Dispatcher.UIThread.Post(() => _pointerButton = MouseButton.None);
        if (button != MouseButton.Left || e.InitialPressMouseButton != MouseButton.Left || _pressClicks > 1 || Vm is null)
            return;
        // The chevron is a button with its own toggle.
        if (e.Source is not Visual source || source.FindAncestorOfType<Button>(includeSelf: true) is not null)
            return;
        switch (source.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext)
        {
            case BrowserNode { IsGroup: true } group:
                Vm.ToggleNode(group);
                break;
            // A left click on a row that was already selected (by a right click, or a preview
            // replaced since) changes no selection, so open it here.
            case BrowserNode { Asset: { } asset } when SelectedAssets() is [var only] && only == asset
                                                    && Vm.ActiveTab?.Record != asset:
                CancelPendingOpen();
                Vm.OpenAsset(asset, preview: true);
                break;
        }
    }

    private void Tree_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (Vm is null)
            return;
        if (_revealing)
        {
            CancelPendingOpen();
            return;
        }

        // Headings aren't rows. Group rows are selectable like any tree row, so arrowing down a
        // collapsed list just moves; only a click, Enter or ←/→ opens or closes a group.
        foreach (var added in e.AddedItems.OfType<BrowserNode>().ToList())
            if (added.IsHeader)
                Tree.SelectedItems?.Remove(added);
        if (Tree.SelectedItems?.Count == 1 && Tree.SelectedItem is BrowserNode { Gdt: { } gdt })
        {
            Vm.ExplorerGdt = gdt;
            _pickedGdt = gdt.Name;
        }
        else if (e.AddedItems.Count > 0)
            _pickedGdt = null;

        // One asset selected = open it in the preview tab (italic, replaceable); a multi-selection
        // is for the context menu's Compare / Open as table. A click opens at once; keyboard
        // selection waits for the row the user stops on; a right click only picks the row its
        // menu acts on.
        var selected = SelectedAssets();
        if (selected.Count == 1 && e.AddedItems.OfType<BrowserNode>().Any(n => n.Asset is not null))
        {
            if (_pointerButton == MouseButton.Left)
            {
                CancelPendingOpen();
                Vm.OpenAsset(selected[0], preview: true);
            }
            else if (_pointerButton != MouseButton.None)
                CancelPendingOpen();
            else
            {
                _pendingOpen = selected[0];
                _openDelay.Stop();
                _openDelay.Start();
            }
        }
        else
            CancelPendingOpen();
    }

    /// <summary>
    /// Down from the search box drops into the results without touching the mouse; Enter opens the
    /// top result as a kept tab; Esc clears.
    /// </summary>
    private void SearchBox_KeyDown(object? sender, KeyEventArgs e)
    {
        // Backspace with nothing before the caret takes the last chip, as in any chip box.
        if (e.Key == Key.Back && e.KeyModifiers == KeyModifiers.None && AtStart(SearchBox) && Vm is { } chipsVm
            && chipsVm.ExplorerQuery.RemoveLast())
        {
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape && Vm is { HasFilter: true } vm)
        {
            vm.ClearFilterCommand.Execute(null);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None && Vm is { } v)
        {
            CancelPendingOpen();
            v.OpenTopResult();
            e.Handled = true;
            return;
        }
        if (e.Key != Key.Down || Tree.ItemCount == 0)
            return;
        FocusTree();
        e.Handled = true;
    }

    /// <summary>
    /// Enter keeps the selected asset open (selection alone only opens a replaceable preview tab)
    /// and opens or closes a group. As in any tree, → expands then steps into the children, ←
    /// collapses then steps out to the parent row. F2 renames, Delete deletes.
    /// </summary>
    private void Tree_KeyDown(object? sender, KeyEventArgs e)
    {
        if (Tree.SelectedItem is not BrowserNode node || Vm is null)
            return;
        // Cut, copy and paste assets: the selected rows, pasted onto the GDT of the row under the cursor.
        if (CommandCatalog.Is(CommandCatalog.CutAssets, e) || CommandCatalog.Is(CommandCatalog.CopyAssets, e))
        {
            var assets = SelectedAssets();
            if (assets.Count > 0)
            {
                if (CommandCatalog.Is(CommandCatalog.CutAssets, e))
                    Vm.CutAssets(assets);
                else
                    Vm.CopyAssets(assets);
                e.Handled = true;
            }
            return;
        }
        if (CommandCatalog.Is(CommandCatalog.PasteAssets, e))
        {
            // Nothing to paste: the key goes on (a row has no paste of its own, but nothing here claims it either).
            if (PasteTarget() is { } target && Vm.CanPasteAssets)
            {
                CancelPendingOpen();
                _ = Vm.PasteAssetsAsync(target);
                e.Handled = true;
            }
            return;
        }
        // Rename, Delete and Reveal in Explorer: the catalog's keys, applied to the selected row rather than the open tab.
        if (node.Asset is { } rowAsset)
        {
            var handled = true;
            if (CommandCatalog.Is(CommandCatalog.Rename, e))
                Vm.RenameAsset(rowAsset);
            else if (CommandCatalog.Is(CommandCatalog.Delete, e))
                Vm.DeleteAsset(rowAsset);
            else if (CommandCatalog.Is(CommandCatalog.Reveal, e))
                Vm.RevealInTree(rowAsset);
            else
                handled = false;
            if (handled)
            {
                CancelPendingOpen();
                e.Handled = true;
                return;
            }
        }
        if (CommandCatalog.Is(CommandCatalog.CollapseAll, e))
        {
            CancelPendingOpen();
            if (Vm.CollapseAllGroups(node) is { } owner)
                MoveSelection(Vm.FlatRows.IndexOf(owner));
            e.Handled = true;
            return;
        }
        var plain = e.KeyModifiers == KeyModifiers.None;
        switch (e.Key)
        {
            // Up from the first row, and Esc, go back to the search box (a second Esc there clears it).
            case Key.Up when plain && IsFirstRow(node):
            case Key.Escape when plain:
                CancelPendingOpen();
                FocusSearchBox();
                break;
            case Key.Enter when plain && node.Asset is { } asset:
                CancelPendingOpen();
                Vm.OpenAsset(asset);
                break;
            case Key.Enter when plain && node.IsGroup:
                Vm.ToggleNode(node);
                break;
            case Key.Right when plain && node.ShowChevron && !node.IsExpanded:
            case Key.Left when plain && node.ShowChevron && node.IsExpanded:
                Vm.ToggleNode(node);
                break;
            case Key.Right when plain && node.ShowChevron:
                MoveSelection(Vm.FlatRows.IndexOf(node) + 1);
                break;
            case Key.Left when plain && ParentRow(node) is { } parent:
                MoveSelection(Vm.FlatRows.IndexOf(parent));
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private bool IsFirstRow(BrowserNode node) => Vm!.FlatRows.FirstOrDefault(r => !r.IsHeader) == node;

    /// <summary>The keyboard into the search box, the caret after what's there.</summary>
    private void FocusSearchBox()
    {
        SearchBox.Focus(NavigationMethod.Directional);
        SearchBox.CaretIndex = SearchBox.Text?.Length ?? 0;
        SearchBox.SelectionStart = SearchBox.SelectionEnd = SearchBox.CaretIndex;
    }

    /// <summary>A letter typed on a row starts (or carries on) the search with it.</summary>
    private void Tree_TextInput(object? sender, TextInputEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.Text) || e.Text.Any(char.IsControl) || Vm is null)
            return;
        CancelPendingOpen();
        FocusSearchBox();
        var text = SearchBox.Text ?? "";
        SearchBox.Text = text + e.Text;
        SearchBox.CaretIndex = SearchBox.Text.Length;
        e.Handled = true;
    }

    /// <summary>The GDT a paste lands in: the selected GDT row's, or the GDT of the selected asset or type row.</summary>
    private GdtFile? PasteTarget()
    {
        if (Tree.SelectedItems is not { Count: 1 } || Tree.SelectedItem is not BrowserNode node || Vm is null)
            return null;
        for (BrowserNode? row = node; row is not null; row = ParentRow(row))
        {
            if (row.Gdt is { } gdt)
                return gdt;
            if (row.Asset is { } asset && !row.IsPinnedEntry)
                return Vm.GdtOf(asset);
        }
        return null;
    }

    /// <summary>The row a row sits under: the nearest row above it at a shallower level.</summary>
    private BrowserNode? ParentRow(BrowserNode node)
    {
        var rows = Vm!.FlatRows;
        for (var i = rows.IndexOf(node) - 1; i >= 0; i--)
        {
            if (rows[i].IsHeader)
                return null;
            if (rows[i].Level < node.Level)
                return rows[i];
        }
        return null;
    }

    /// <summary>Selects a row by index and moves keyboard focus with it, as the arrow keys do.</summary>
    private void MoveSelection(int index)
    {
        if (Vm is null || index < 0 || index >= Vm.FlatRows.Count || Vm.FlatRows[index].IsHeader)
            return;
        var row = Vm.FlatRows[index];
        Tree.SelectedItems?.Clear();
        Tree.SelectedItem = row;
        Tree.ScrollIntoView(row);
        Tree.ContainerFromItem(row)?.Focus(NavigationMethod.Directional);
    }

    private void Tree_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (Tree.SelectedItem is BrowserNode { Asset: { } asset } && Vm is not null)
        {
            CancelPendingOpen();
            Vm.OpenAsset(asset);
        }
    }

    public void FocusSearch()
    {
        // Ctrl+Shift+F opened the menu over the box: the keyboard belongs in the menu's box.
        if (Vm is { IsSearchMenuOpen: true })
        {
            ShowSearchMenu();
            return;
        }
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    // ── Search menu ──────────────────────────────────────────────────────────

    private static bool AtStart(TextBox box) => box.CaretIndex == 0 && box.SelectionStart == box.SelectionEnd;

    private static bool Within(object? source, Control control) =>
        source is Visual v && (ReferenceEquals(v, control) || v.GetVisualAncestors().Contains(control));

    /// <summary>
    /// A click on the box's glyph, its chips' gaps or its padding puts the caret in the text, as a click on any field
    /// does. The text box places its own caret; the buttons in the box (a chip's ✕, clear, the menu) keep their clicks.
    /// </summary>
    private void SearchField_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(SearchField).Properties.IsLeftButtonPressed || e.Source is not Visual source)
            return;
        if (source.FindAncestorOfType<Button>(includeSelf: true) is not null || Within(source, SearchBox))
            return;
        FocusSearchBox();
        e.Handled = true;
    }

    /// <summary>The button at the box's end opens the search menu over it.</summary>
    private void SearchMenuButton_Click(object? sender, RoutedEventArgs e) => Vm?.OpenSearchMenu();

    /// <summary>
    /// Shows the menu over the box once layout has placed it, sized to the window, with the keyboard in it. The popup
    /// stays open with the card hidden between uses: opening a popup builds its host and lays the whole card out again
    /// (40–120 ms on the install), where showing the hidden card only measures it.
    /// </summary>
    private void ShowSearchMenu()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (Vm is not { IsSearchMenuOpen: true })
                return;
            if (TopLevel.GetTopLevel(this) is { } top && SearchField.TranslatePoint(default, top) is { } at)
                SearchMenuCard.Width = Math.Clamp(top.Bounds.Width - at.X - 10, 480, 880);
            ShowCard(true);
            if (!SearchMenu.IsOpen)
                SearchMenu.IsOpen = true;
            else
            {
                // Place it over the box as it is now (the Explorer may have moved or resized since it was last shown).
                SearchMenu.HorizontalOffset = MenuOffsetX + 0.01;
                SearchMenu.HorizontalOffset = MenuOffsetX;
            }
            MenuBox.Focus(NavigationMethod.Pointer);
            MenuBox.CaretIndex = MenuBox.Text?.Length ?? 0;
        }, DispatcherPriority.Loaded);
    }

    private const double MenuOffsetX = -6;

    /// <summary>The menu shown, as the view model says.</summary>
    public bool IsSearchMenuShown => SearchMenu.IsOpen && _cardShown;

    private bool _cardShown;

    /// <summary>
    /// Hidden, the card stays laid out but can't be seen, clicked or tabbed into: showing it again is a repaint, not the
    /// 10-20 ms re-measure of every pill, field and row that toggling IsVisible costs.
    /// </summary>
    private void ShowCard(bool shown)
    {
        _cardShown = shown;
        SearchMenuCard.Opacity = shown ? 1 : 0;
        SearchMenuCard.IsHitTestVisible = shown;
        KeyboardNavigation.SetTabNavigation(SearchMenuCard, shown ? KeyboardNavigationMode.Cycle : KeyboardNavigationMode.None);
    }

    /// <summary>Opens the popup with the card hidden once the window is up, so the first Ctrl+Shift+F is as quick as the rest.</summary>
    private void PrepareSearchMenu()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!SearchMenu.IsOpen && this.IsAttachedToVisualTree())
                SearchMenu.IsOpen = true;
        }, DispatcherPriority.Background);
    }

    private void SyncSearchMenu()
    {
        if (Vm is not { } vm)
            return;
        if (vm.IsSearchMenuOpen)
        {
            ShowSearchMenu();
            return;
        }
        // Closed with the keyboard in it (Esc, Enter, Ctrl+Enter, a result clicked): the keyboard goes back to the box.
        var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
        var hadFocus = Within(focused, SearchMenuCard);
        ShowCard(false);
        if (hadFocus)
        {
            SearchBox.Focus();
            SearchBox.CaretIndex = SearchBox.Text?.Length ?? 0;
        }
    }

    private void SearchMenu_Closed(object? sender, EventArgs e) => Vm?.SetSearchMenuAside();

    private TopLevel? _window;

    /// <summary>
    /// A press anywhere outside the menu closes it and still does what it was aimed at (one click, not two): the row or
    /// field under the pointer gets the press, and the draft waits for the menu to open again.
    /// </summary>
    private void Window_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Vm is { IsSearchMenuOpen: true } vm && !Within(e.Source, SearchMenuCard))
            vm.SetSearchMenuAside();
    }

    /// <summary>
    /// The menu's keys. In its box: ↑↓ choose, Enter opens, Backspace takes the last chip. In the GDT field Space and
    /// Enter add the GDT; in the Property fields Enter adds the property. Anywhere: Ctrl+Enter filters the Explorer, Esc closes.
    /// </summary>
    private void SearchMenuCard_KeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm is not { } vm)
            return;
        var plain = e.KeyModifiers == KeyModifiers.None;
        var inBox = Within(e.Source, MenuBox);
        var inGdt = Within(e.Source, MenuGdtBox);
        var inProp = Within(e.Source, MenuPropKeyBox) || Within(e.Source, MenuPropValueBox);
        switch (e.Key)
        {
            case Key.Escape:
                vm.CloseSearchMenu();
                break;
            case Key.Enter when e.KeyModifiers == KeyModifiers.Control:
                vm.ApplySearchMenu();
                break;
            case Key.Enter when plain && inGdt:
                vm.CommitMenuGdt();
                break;
            case Key.Enter when plain && inProp:
                vm.CommitMenuProp();
                break;
            case Key.Enter when plain && inBox:
                vm.OpenSearchMenuSelection();
                break;
            case Key.Space when plain && inGdt && vm.CommitMenuGdt():
                break;
            case Key.Down or Key.Up when plain && inBox:
                vm.MoveSearchMenuSelection(e.Key == Key.Down ? 1 : -1);
                if (vm.SearchMenuSelectedIndex >= 0)
                    MenuResults.ScrollIntoView(vm.SearchMenuSelectedIndex);
                break;
            case Key.Back when plain && inBox && AtStart(MenuBox) && vm.MenuQuery.RemoveLast():
                break;
            case Key.Back when plain && inGdt && AtStart(MenuGdtBox) && vm.MenuQuery.RemoveLast(Services.TokenKind.Gdt):
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    /// <summary>One click on a result opens it.</summary>
    private void MenuResults_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left || Vm is not { } vm)
            return;
        if ((e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext is SearchMenuRow { Item: { } result })
            vm.OpenSearchMenuResult(result);
    }

    // ── Type facets ──────────────────────────────────────────────────────────

    private void SearchFacets_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => SyncFacets();

    /// <summary>Most facets given a button of their own; the rest only ever sit behind "+N" (an install has ~65 types).</summary>
    private const int FacetButtons = 10;

    /// <summary>Facets with no button this search, in the results' order.</summary>
    private List<SearchFacet> _facetsWithoutButton = new();

    /// <summary>
    /// One button per facet (the first ten, and the picked one wherever it ranks), kept between searches and
    /// re-labelled: a keystroke has a frame and building buttons costs most of it. The row shows the buttons that fit
    /// and "+N" for every facet it can't show.
    /// </summary>
    private void SyncFacets()
    {
        var facets = Vm?.SearchFacets ?? (IReadOnlyList<SearchFacet>)Array.Empty<SearchFacet>();
        var withButton = facets.Take(FacetButtons).ToList();
        if (facets.Skip(FacetButtons).FirstOrDefault(f => f.IsActive) is { } picked)
            withButton.Add(picked);
        _facetsWithoutButton = facets.Where(f => !withButton.Contains(f)).ToList();
        var row = Facets.Children;
        while (row.Count - 1 < withButton.Count)
        {
            var button = new Button();
            button.Classes.Add("facet");
            button.Click += (s, _) => ((s as Button)?.DataContext as SearchFacet)?.SelectCommand.Execute(null);
            row.Insert(row.Count - 1, button);
        }
        for (var i = 0; i < row.Count - 1; i++)
        {
            var button = (Button)row[i];
            var facet = i < withButton.Count ? withButton[i] : null;
            button.IsVisible = facet is not null;
            if (facet is null)
                continue;
            button.DataContext = facet;
            button.Content = facet.Label;
            button.Classes.Set("active", facet.IsActive);
            AutomationProperties.SetName(button, facet.Type is null ? facet.Label : $"{facet.Label}, show only {facet.Type}");
        }
        Facets.MoreElsewhere = _facetsWithoutButton.Count;
        LabelFacetMore();
    }

    /// <summary>The facets "+N" offers: those the row had no room for and those without a button, in the results' order.</summary>
    private List<SearchFacet> FacetsBehindMore()
    {
        var behind = Facets.Hidden.Select(h => h.DataContext).OfType<SearchFacet>().Concat(_facetsWithoutButton).ToHashSet();
        return (Vm?.SearchFacets ?? Enumerable.Empty<SearchFacet>()).Where(behind.Contains).ToList();
    }

    /// <summary>"+N" says how many wait behind it; the facets out of sight leave the Tab order.</summary>
    private void LabelFacetMore()
    {
        var more = Facets.Hidden.Count + _facetsWithoutButton.Count;
        FacetMore.Content = $"+{more}";
        foreach (var child in Facets.Children)
            KeyboardNavigation.SetIsTabStop(child, child == FacetMore ? more > 0 : !Facets.Hidden.Contains(child));
    }

    /// <summary>"+N": the types that didn't fit the row, as a menu.</summary>
    private void FacetMore_Click(object? sender, RoutedEventArgs e)
    {
        var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
        foreach (var facet in FacetsBehindMore())
            menu.Items.Add(new MenuItem { Header = facet.Label, Command = facet.SelectCommand });
        FlyoutBase.SetAttachedFlyout(FacetMore, menu);
        menu.ShowAt(FacetMore);
    }

    /// <summary>The footer's query, with each filter's prefix in the accent so the syntax reads at a glance.</summary>
    private void RenderMenuQuery(string query)
    {
        var inlines = new InlineCollection();
        foreach (var raw in Services.AssetQuery.Tokenize(query))
        {
            if (inlines.Count > 0)
                inlines.Add(new Run(" "));
            var colon = raw.IndexOf(':');
            if (colon > 0 && Services.AssetQuery.Parse(raw) is [{ Kind: not Services.TokenKind.Name }])
            {
                var prefix = new Run(raw[..(colon + 1)]);
                prefix.Bind(TextElement.ForegroundProperty, this.GetResourceObservable("AccentTextBrush"));
                inlines.Add(prefix);
                inlines.Add(new Run(raw[(colon + 1)..]));
            }
            else
                inlines.Add(new Run(raw));
        }
        MenuQueryText.Inlines = inlines;
    }

    /// <summary>
    /// Puts the keyboard on the selected row (the first row if none). The ListBox itself doesn't
    /// take focus — its rows do — so focusing the list alone left the arrow keys going nowhere.
    /// </summary>
    public void FocusTree()
    {
        var index = Tree.SelectedIndex;
        if (index < 0)
        {
            index = Tree.Items.OfType<BrowserNode>().ToList().FindIndex(n => !n.IsHeader);
            if (index < 0)
                return;
            Tree.SelectedIndex = index;
        }
        Tree.ScrollIntoView(index);
        Tree.UpdateLayout();
        Tree.ContainerFromIndex(index)?.Focus(NavigationMethod.Directional);
    }

    private void Vm_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.ActiveTab))
        {
            _pickedGdt = null;
            SyncSelectionToActive();
        }
        else if (e.PropertyName == nameof(MainViewModel.FlatRows))
        {
            if (!KeepPickedGdt())
                SyncSelectionToActive();
        }
        else if (e.PropertyName == nameof(MainViewModel.IsSearchMenuOpen))
            SyncSearchMenu();
        // The menu belongs to the Explorer's box: hiding the Explorer (Ctrl+B, the preview layout) closes it.
        else if (e.PropertyName == nameof(MainViewModel.ShowExplorer) && Vm is { ShowExplorer: false } hidden)
            hidden.CloseSearchMenu();
        else if (e.PropertyName == nameof(MainViewModel.SearchMenuQuery) && Vm is { } vm)
            RenderMenuQuery(vm.SearchMenuQuery);
    }

    /// <summary>After the rows were rebuilt, re-selects the GDT row the user had picked, if it is still shown.</summary>
    private bool KeepPickedGdt()
    {
        if (_pickedGdt is not { } name || Vm?.FlatRows.FirstOrDefault(n => n.Gdt?.Name == name) is not { } row)
            return false;
        _revealing = true;
        try
        {
            Tree.SelectedItems?.Clear();
            Tree.SelectedItem = row;
        }
        finally
        {
            _revealing = false;
        }
        return true;
    }

    /// <summary>Selects the active asset's row if it is visible; never expands groups or re-opens it.</summary>
    private void SyncSelectionToActive()
    {
        if (Vm?.ActiveTab?.Record is not { } active)
            return;
        if (Tree.SelectedItems is { Count: > 1 })
            return;
        if (Tree.SelectedItem is BrowserNode { Asset: { } current } && current == active)
            return;
        var row = Vm.FlatRows.FirstOrDefault(n => n.Asset == active && !n.IsPinnedEntry);
        _revealing = true;
        try
        {
            Tree.SelectedItems?.Clear();
            if (row is not null)
                Tree.SelectedItem = row;
        }
        finally
        {
            _revealing = false;
        }
    }

    private void Reveal(BrowserNode node)
    {
        _revealing = true;
        try
        {
            Tree.SelectedItems?.Clear();
            Tree.SelectedItem = node;
            Tree.ScrollIntoView(node);
        }
        finally
        {
            _revealing = false;
        }
    }

    // ── Context menu ─────────────────────────────────────────────────────────

    /// <summary>The one group row selected (a right-click on a GDT or type row), or null.</summary>
    private BrowserNode? SelectedGroup() =>
        Tree.SelectedItems is { Count: 1 } && Tree.SelectedItem is BrowserNode { IsGroup: true } group ? group : null;

    private void TreeMenu_Opening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        var selected = SelectedAssets();
        var group = SelectedGroup();
        if ((selected.Count == 0 && group is null) || Vm is null)
        {
            e.Cancel = true;
            return;
        }
        var one = selected.Count == 1;
        var assets = selected.Count > 0;
        MenuOpen.IsVisible = one;
        MenuToggle.IsVisible = group is not null;
        MenuToggle.Header = group is { IsExpanded: true } ? "Collapse" : "Expand";
        MenuNewIn.IsVisible = group?.Gdt is not null;
        MenuCollapseAll.IsVisible = group is not null && Vm.CanCollapseAll;
        MenuPin.IsVisible = one;
        MenuPin.IsChecked = one && Vm.IsPinned(selected[0]);
        MenuCopy.Header = group is not null || one ? CommandCatalog.Get(CommandCatalog.CopyName).Name : $"Copy {selected.Count} names";
        var pasteTarget = Vm.CanPasteAssets ? PasteTarget() : null;
        MenuClipSeparator.IsVisible = assets || pasteTarget is not null;
        MenuCutAssets.IsVisible = assets;
        MenuCopyAssets.IsVisible = assets;
        MenuPasteAssets.IsVisible = pasteTarget is not null;
        MenuEditSeparator.IsVisible = assets;
        MenuRename.IsVisible = one;
        MenuDuplicate.IsVisible = one;
        MenuDuplicateTo.IsVisible = assets;
        MenuMoveTo.IsVisible = assets;
        MenuDerive.IsVisible = one && !selected[0].Type.Equals(Services.Gdt.GdtLoader.UnknownType, StringComparison.OrdinalIgnoreCase);
        MenuUnderive.IsVisible = one && selected[0].Parent is not null;
        MenuDelete.IsVisible = one;
        var sameType = selected.Select(a => a.Type).Distinct().Count() == 1;
        MenuMultiSeparator.IsVisible = selected.Count >= 2;
        MenuCompare.IsVisible = selected.Count >= 2 && sameType;
        MenuCompare.Header = $"Compare {selected.Count} assets";
        MenuTable.IsVisible = selected.Count >= 2 && assets;
        MenuTable.Header = $"Open {selected.Count} as table";
    }

    private void MenuOpen_Click(object? sender, RoutedEventArgs e)
    {
        if (SelectedAssets() is [var asset] && Vm is { } vm)
            vm.OpenAsset(asset);
    }

    private void MenuToggle_Click(object? sender, RoutedEventArgs e)
    {
        if (SelectedGroup() is { } group)
            Vm?.ToggleNode(group);
    }

    private void MenuCollapseAll_Click(object? sender, RoutedEventArgs e)
    {
        if (Vm?.CollapseAllGroups(SelectedGroup()) is { } owner)
            MoveSelection(Vm.FlatRows.IndexOf(owner));
    }

    private void MenuNewIn_Click(object? sender, RoutedEventArgs e)
    {
        if (SelectedGroup() is { Gdt: { } gdt })
            Vm?.NewAssetIn(gdt);
    }

    private void MenuPin_Click(object? sender, RoutedEventArgs e)
    {
        if (SelectedAssets() is [var asset] && Vm is { } vm)
            vm.TogglePinned(asset);
    }

    private void MenuRename_Click(object? sender, RoutedEventArgs e)
    {
        if (SelectedAssets() is [var asset])
            Vm?.RenameAsset(asset);
    }

    private void MenuDuplicate_Click(object? sender, RoutedEventArgs e)
    {
        if (SelectedAssets() is [var asset])
            Vm?.DuplicateAsset(asset);
    }

    private void MenuCutAssets_Click(object? sender, RoutedEventArgs e) => Vm?.CutAssets(SelectedAssets());

    private void MenuCopyAssets_Click(object? sender, RoutedEventArgs e) => Vm?.CopyAssets(SelectedAssets());

    private void MenuPasteAssets_Click(object? sender, RoutedEventArgs e)
    {
        if (PasteTarget() is { } target)
            _ = Vm?.PasteAssetsAsync(target);
    }

    private void MenuDuplicateTo_Click(object? sender, RoutedEventArgs e) => Vm?.DuplicateTo(SelectedAssets());

    private void MenuMoveTo_Click(object? sender, RoutedEventArgs e) => Vm?.MoveTo(SelectedAssets());

    private void MenuDerive_Click(object? sender, RoutedEventArgs e)
    {
        if (SelectedAssets() is [var asset])
            Vm?.DeriveAsset(asset);
    }

    private void MenuUnderive_Click(object? sender, RoutedEventArgs e)
    {
        if (SelectedAssets() is [var asset])
            Vm?.UnderiveAsset(asset);
    }

    private void MenuDelete_Click(object? sender, RoutedEventArgs e)
    {
        if (SelectedAssets() is [var asset])
            Vm?.DeleteAsset(asset);
    }

    private async void MenuCopy_Click(object? sender, RoutedEventArgs e)
    {
        var names = SelectedGroup() is { } group
            ? group.Title
            : string.Join('\n', SelectedAssets().Select(a => a.Name));
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            await clipboard.SetTextAsync(names);
    }

    private void MenuCompare_Click(object? sender, RoutedEventArgs e) => Vm?.CompareAssets(SelectedAssets());

    private void MenuTable_Click(object? sender, RoutedEventArgs e) => Vm?.OpenTableFor(SelectedAssets());
}
