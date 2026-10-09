using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Apex.Editor.Controls;
using Apex.Editor.Models;
using Apex.Editor.ViewModels;

namespace Apex.Editor.Views;

/// <summary>
/// A reference field: the text box of a <see cref="RefPropertyViewModel"/> that knows what it references. Typing lists
/// the assets of the referenced type whose names match, ranked like Quick Open; its ▾ (the chevron every dropdown in Apex
/// draws), Alt+↓ or F4 lists all of them, as APE's combo does, with the current value highlighted. ↓/↑ move the highlight,
/// Enter or Tab accepts it, a click accepts a line, Esc closes the list and a second Esc reverts the typing. Tab takes
/// only a highlighted line or the name typed exactly: what was typed is never swapped for a fuzzy match. Accepting
/// commits through the row's value exactly as typing the name and pressing Enter would, and only names that are in the
/// index right now can be accepted. The keyboard never leaves the field. A reference that resolves opens with F12, a
/// Ctrl+click on the name, or the open arrow beside the ▾.
///
/// Committing typed text (Enter, focus loss) and reverting it (Esc) stay with <see cref="PropertyEditorView"/>; this
/// only adds the list. While the list is open its keys are taken at the window, ahead of the editor's own ↑↓ row walk.
/// </summary>
public sealed class RefField : TextBox
{
    private Popup? _popup;
    private RefSuggestPopup? _list;
    private TopLevel? _keyHost;
    private MainViewModel? _waitingOn;
    private DispatcherTimer? _loading;
    // The list was opened with Alt+↓ (everything, the value highlighted) rather than by typing.
    private bool _browse;
    // The text is changing because of a key the user pressed (not a binding, an undo or a revert).
    private bool _typing;
    // A paste is under way: the clipboard is read asynchronously, so its text lands after the key that started it.
    private bool _pasting;
    // Where the field sat in its window when the list opened: the list closes if the field moves (the form scrolled).
    private Point? _openedAt;

    protected override Type StyleKeyOverride => typeof(TextBox);

    private RefPropertyViewModel? Row => DataContext as RefPropertyViewModel;

    // The window's: a field in a flyout (the preview's hands and gun) sits in a popup whose own top level carries the
    // preview's data, so the window is found up the logical tree.
    private MainViewModel? Shell => TopLevel.GetTopLevel(this)?.DataContext as MainViewModel
        ?? Avalonia.LogicalTree.LogicalExtensions.GetLogicalAncestors(this).OfType<TopLevel>().Select(t => t.DataContext).OfType<MainViewModel>().FirstOrDefault();

    /// <summary>The suggestion list is open.</summary>
    public bool IsSuggesting => _popup?.IsOpen == true;

    /// <summary>The list's content (null until the field first opens it).</summary>
    public RefSuggestPopup? Suggestions => _list;

    // ── Typing opens the list ────────────────────────────────────────────────

    private readonly Button _drop;
    private readonly Button _goTo;
    private RefPropertyViewModel? _watched;

    /// <summary>Below this width (a narrow table cell) the buttons go: the name needs the room, and Alt+↓ / F12 still work.</summary>
    internal const double MinWidthForButtons = 120;

    public RefField()
    {
        PastingFromClipboard += (_, _) => _pasting = true;
        // Neither button takes the keyboard: it stays in the field, and a focusable button would cancel its own click.
        _goTo = new Button { Focusable = false, Content = FieldGlyphs.GoTo(), Width = 20 };
        _goTo.Classes.Add("rowaction");
        _goTo.Bind(TemplatedControl.ForegroundProperty, _goTo.GetResourceObservable("TextBrush"));
        _goTo.Click += (_, _) =>
        {
            if (Row is { CanGoTo: true } row)
                row.GoToCommand.Execute(null);
        };
        Avalonia.Automation.AutomationProperties.SetName(_goTo, "Go to reference");
        _drop = new Button { Focusable = false, Content = FieldGlyphs.DropDown(), Width = 20 };
        _drop.Classes.Add("rowaction");
        _drop.Bind(TemplatedControl.ForegroundProperty, _drop.GetResourceObservable("TextBrush"));
        _drop.Click += (_, _) => ToggleAll();
        Avalonia.Automation.AutomationProperties.SetName(_drop, "Show all");
        InnerRightContent = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Children = { _goTo, _drop } };
        UpdateButtons();
    }

    /// <summary>The ▾ that lists every asset of the type (for Apex.Shots).</summary>
    public Button DropButton => _drop;

    /// <summary>The arrow that opens the referenced asset (for Apex.Shots).</summary>
    public Button GoToButton => _goTo;

    private void UpdateButtons()
    {
        var wide = Bounds.Width <= 0 || Bounds.Width >= MinWidthForButtons;
        _drop.IsVisible = wide;
        _goTo.IsVisible = wide && Row is { CanGoTo: true };
        if (Row is { } row)
        {
            ToolTip.SetTip(_drop, $"All {row.RefType} assets (Alt+↓)");
            ToolTip.SetTip(_goTo, $"Open {row.Value} (F12, or Ctrl+click the name)");
        }
    }

    private void Row_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RefPropertyViewModel.CanGoTo) or nameof(RefPropertyViewModel.Value))
            UpdateButtons();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        UpdateButtons();
    }

    /// <summary>▾: lists every asset of the type, or closes the list it opened.</summary>
    private void ToggleAll()
    {
        if (IsSuggesting && _browse)
        {
            Close();
            return;
        }
        if (!IsKeyboardFocusWithin)
            Focus(NavigationMethod.Pointer);
        OpenAll();
    }

    /// <summary>Ctrl+click on the name follows the reference, as in a code editor.</summary>
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
            && Row is { CanGoTo: true } row && !IsOnButton(e.Source))
        {
            e.Handled = true;
            row.GoToCommand.Execute(null);
            return;
        }
        base.OnPointerPressed(e);
    }

    private bool IsOnButton(object? source) =>
        source is Visual v && (v == _drop || v == _goTo || _drop.IsVisualAncestorOf(v) || _goTo.IsVisualAncestorOf(v));

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        // Build the catalog now (off the UI thread on the real install) so the first keystroke finds it ready.
        Shell?.WarmRefCatalog();
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        var before = Text;
        _typing = true;
        try { base.OnTextInput(e); }
        finally { _typing = false; }
        if (Text != before)
            Typed();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (!IsSuggesting && (e.Key == Key.Down && e.KeyModifiers == KeyModifiers.Alt || e.Key == Key.F4 && e.KeyModifiers == KeyModifiers.None))
        {
            OpenAll();
            e.Handled = true;
            return;
        }
        // Backspace, Delete and cut edit the text from here; a paste's text arrives later (see OnPropertyChanged).
        var before = Text;
        _typing = true;
        try { base.OnKeyDown(e); }
        finally { _typing = false; }
        if (Text != before)
            Typed();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != TextProperty || _typing)
            return;
        if (_pasting)
        {
            _pasting = false;
            Typed();
        }
        // The text was put back from outside (Ctrl+Z discarding the typing, an undo, the row's value changing): the
        // list of matches for what had been typed no longer applies.
        else if (!_browse && IsSuggesting)
            Close();
    }

    /// <summary>The user changed the text: list what matches it, or close the list when it's empty.</summary>
    private void Typed()
    {
        // A paste whose text already landed (a synchronous clipboard) is done; only one still in flight waits.
        _pasting = false;
        _browse = false;
        if (string.IsNullOrEmpty(Text))
            Close();
        else
            Suggest();
    }

    /// <summary>Alt+↓ / F4: every asset of the type, by name, the field's value highlighted (or the nearest name in view).</summary>
    private void OpenAll()
    {
        _browse = true;
        Suggest();
    }

    private void Suggest()
    {
        if (Row is not { } row || Shell is not { } shell)
            return;
        var text = Text ?? "";
        var items = shell.MatchRefs(row.RefType, _browse ? "" : text);
        if (items is null)
        {
            // The catalog is building (real install, first use or just after the index changed): ask again when it's
            // ready. A list already open keeps showing what it has; a new one says so only if the wait passes 150 ms.
            WaitForCatalog(shell);
            return;
        }
        StopWaiting();
        // A type with no assets in the index (sound aliases live outside the GDTs) has nothing to suggest while typing.
        if (items.Count == 0 && !_browse && shell.MatchRefs(row.RefType, "") is { Count: 0 })
        {
            Close();
            return;
        }
        var list = EnsurePopup();
        if (items.Count == 0)
            list.ShowMessage(_browse ? $"No {row.RefType} in the loaded GDTs" : $"No {row.RefType} named ‘{text}’");
        else if (_browse)
        {
            var at = BinarySearchName(items, text);
            list.ShowResults(items, "", at >= 0 ? at : -1, anchor: at >= 0 ? at : ~at);
        }
        else
        {
            // Only an exact name is highlighted up front: Enter must never swap what was typed for something else.
            var exact = items[0].Name.Equals(text, StringComparison.OrdinalIgnoreCase);
            list.ShowResults(items, text, exact ? 0 : -1);
        }
        Open();
    }

    private static int BinarySearchName(IReadOnlyList<AssetRecord> items, string name)
    {
        int lo = 0, hi = items.Count - 1;
        while (lo <= hi)
        {
            var mid = lo + (hi - lo) / 2;
            var c = string.Compare(items[mid].Name, name, StringComparison.OrdinalIgnoreCase);
            if (c == 0)
                return mid;
            if (c < 0)
                lo = mid + 1;
            else
                hi = mid - 1;
        }
        return ~lo;
    }

    private void WaitForCatalog(MainViewModel shell)
    {
        if (_waitingOn != shell)
        {
            StopWaiting();
            _waitingOn = shell;
            shell.RefCatalogReady += Catalog_Ready;
        }
        if (IsSuggesting)
            return;
        _loading ??= new DispatcherTimer(TimeSpan.FromMilliseconds(150), DispatcherPriority.Normal, (_, _) =>
        {
            _loading?.Stop();
            if (_waitingOn is not null && IsKeyboardFocusWithin)
            {
                EnsurePopup().ShowMessage("Loading…");
                Open();
            }
        });
        _loading.Stop();
        _loading.Start();
    }

    private void StopWaiting()
    {
        _loading?.Stop();
        if (_waitingOn is { } shell)
            shell.RefCatalogReady -= Catalog_Ready;
        _waitingOn = null;
    }

    private void Catalog_Ready()
    {
        StopWaiting();
        if (IsKeyboardFocusWithin && Row is not null)
            Suggest();
    }

    // ── The list's keys, taken at the window while it is open ────────────────

    private void Host_KeyDown(object? sender, KeyEventArgs e)
    {
        if (!IsSuggesting || !IsKeyboardFocusWithin || _list is not { } list)
            return;
        var mods = e.KeyModifiers;
        switch (e.Key)
        {
            case Key.Down when mods == KeyModifiers.None:
                list.Move(1);
                break;
            case Key.Up when mods == KeyModifiers.None:
                list.Move(-1);
                break;
            case Key.PageDown when mods == KeyModifiers.None:
                list.Move(list.SelectedIndex < 0 ? RefSuggestPopup.VisibleLines : RefSuggestPopup.VisibleLines - 1);
                break;
            case Key.PageUp when mods == KeyModifiers.None:
                list.Move(-(RefSuggestPopup.VisibleLines - 1));
                break;
            case Key.Enter when mods == KeyModifiers.None:
                if (list.Selected is not { } picked)
                {
                    // Nothing highlighted: Enter commits what was typed (PropertyEditorView), as it always has.
                    Close();
                    return;
                }
                Accept(picked);
                break;
            case Key.Tab when mods == KeyModifiers.None:
                // Only a highlighted line, or the name exactly as typed: Tab never trades what was typed for the top fuzzy
                // match. Otherwise the list closes and Tab moves on, committing the typing as leaving the field does.
                var exact = list.Items.Count > 0 && list.Items[0].Name.Equals(Text ?? "", StringComparison.OrdinalIgnoreCase)
                    ? list.Items[0] : null;
                if ((list.Selected ?? exact) is not { } completed)
                {
                    Close();
                    return;
                }
                Accept(completed);
                break;
            case Key.Escape when mods == KeyModifiers.None:
            case Key.Up when mods == KeyModifiers.Alt:
            case Key.Down when mods == KeyModifiers.Alt:
            case Key.F4 when mods == KeyModifiers.None:
                Close();
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    /// <summary>Commits <paramref name="record"/>'s name through the row, as typing it and pressing Enter would.</summary>
    private void Accept(AssetRecord record)
    {
        if (Row is not { } row || Shell is not { } shell)
            return;
        // The index may have changed since the list was filled (a GDT reloaded from disk): never write a name that no
        // longer resolves. The list refreshes instead.
        if (!shell.RefExists(row.RefType, record.Name))
        {
            Suggest();
            return;
        }
        Close();
        if (row.RawValue != record.Name)
            row.RawValue = record.Name;
        if (Text != row.RawValue)
            SetCurrentValue(TextProperty, row.RawValue);
        SelectAll();
    }

    private void List_Picked(AssetRecord record)
    {
        if (IsKeyboardFocusWithin)
            Accept(record);
    }

    // ── Opening and closing ──────────────────────────────────────────────────

    private RefSuggestPopup EnsurePopup()
    {
        if (_list is not null)
            return _list;
        _list = new RefSuggestPopup();
        _list.Picked += List_Picked;
        _popup = new Popup
        {
            Child = _list,
            PlacementTarget = this,
            Placement = PlacementMode.BottomEdgeAlignedLeft,
            VerticalOffset = 2,
            // Dismissed by hand (see Host_PointerPressed) rather than by the popup's light dismiss, whose overlay would
            // also swallow the wheel meant for the form underneath.
            IsLightDismissEnabled = false,
        };
        _popup.Closed += (_, _) => Detach();
        // In the field's logical tree, so the list takes the app's styles and theme.
        LogicalChildren.Add(_popup);
        return _list;
    }

    private void Open()
    {
        if (_popup is null || _list is null)
            return;
        // As wide as the field, and never so narrow that names and GDTs can't both show (table cells are narrow).
        _list.Width = Math.Clamp(Bounds.Width, 360, 560);
        if (!_popup.IsOpen)
            _popup.IsOpen = true;
        if (_keyHost is null && TopLevel.GetTopLevel(this) is { } top)
        {
            _keyHost = top;
            top.AddHandler(KeyDownEvent, Host_KeyDown, RoutingStrategies.Tunnel);
            top.AddHandler(PointerPressedEvent, Host_PointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
            if (top is WindowBase window)
                window.Deactivated += Host_Deactivated;
            _openedAt = this.TranslatePoint(default, top);
            LayoutUpdated += Field_Moved;
        }
    }

    /// <summary>A press anywhere outside the list closes it, and goes on to whatever it landed on.</summary>
    private void Host_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // The ▾ toggles the list itself: a press on it isn't a press outside.
        if (_list is not null && e.Source is Visual v && (_list == v || _list.IsVisualAncestorOf(v)) || IsOnButton(e.Source))
            return;
        Close();
    }

    private void Host_Deactivated(object? sender, EventArgs e) => Close();

    /// <summary>The form scrolled (or the layout moved) under the open list: it would hang detached, so it closes.</summary>
    private void Field_Moved(object? sender, EventArgs e)
    {
        if (_keyHost is { } top && _openedAt is { } at && this.TranslatePoint(default, top) is { } now
            && (Math.Abs(now.X - at.X) > 0.5 || Math.Abs(now.Y - at.Y) > 0.5))
            Close();
    }

    /// <summary>Closes the list (the typed text stays as it is).</summary>
    public void Close()
    {
        _browse = false;
        StopWaiting();
        if (_popup is { IsOpen: true })
            _popup.IsOpen = false;
        Detach();
    }

    private void Detach()
    {
        if (_keyHost is null)
            return;
        _keyHost.RemoveHandler(KeyDownEvent, Host_KeyDown);
        _keyHost.RemoveHandler(PointerPressedEvent, Host_PointerPressed);
        if (_keyHost is WindowBase window)
            window.Deactivated -= Host_Deactivated;
        LayoutUpdated -= Field_Moved;
        _keyHost = null;
        _openedAt = null;
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        _pasting = false;
        Close();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        // A recycled row now edits another property: its list is not this one's.
        Close();
        Watch(this.IsAttachedToVisualTree() ? Row : null);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Watch(Row);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Close();
        // A row kept by its view model outlives this control: the control mustn't stay subscribed to it.
        Watch(null);
    }

    private void Watch(RefPropertyViewModel? row)
    {
        if (_watched != row)
        {
            if (_watched is not null)
                _watched.PropertyChanged -= Row_PropertyChanged;
            _watched = row;
            if (row is not null)
                row.PropertyChanged += Row_PropertyChanged;
        }
        UpdateButtons();
    }
}
