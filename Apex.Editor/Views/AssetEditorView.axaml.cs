using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Apex.Editor.Controls;
using Apex.Editor.ViewModels;

namespace Apex.Editor.Views;

public partial class AssetEditorView : UserControl
{
    private AssetEditorViewModel? _vm;
    private ScrollViewer? _scroll;

    static AssetEditorView()
    {
        // A disabled row's tip says what enables it: the search for that runs when someone opens the tip, not before.
        ToolTip.ToolTipOpeningEvent.AddClassHandler<Control>((control, _) =>
        {
            var row = control.DataContext switch
            {
                PropertyItemViewModel item => item,
                VectorRowViewModel vector => vector.Parts[0],
                _ => null,
            };
            if (row is { IsRuleDisabled: true, EnabledBy: null } && control.FindAncestorOfType<AssetEditorView>()?.DataContext is AssetEditorViewModel tab)
                tab.FindEnablers(row);
        });
    }

    public AssetEditorView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as AssetEditorViewModel);
        Form.TemplateApplied += (_, e) =>
        {
            _scroll = e.NameScope.Find<ScrollViewer>("FormScroll");
            if (_scroll is not null)
                _scroll.ScrollChanged += (_, _) => TrackCurrentSection();
        };
        Form.SizeChanged += (_, e) =>
        {
            if (e.WidthChanged)
                ApplyLabelWidth();
        };
        // Focus anywhere inside a row makes it the Inspector's subject.
        Form.AddHandler(GotFocusEvent, Form_GotFocus, RoutingStrategies.Bubble, handledEventsToo: true);
        Form.AddHandler(PointerPressedEvent, Form_PointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        Form.AddHandler(KeyDownEvent, Form_KeyDown, RoutingStrategies.Tunnel);
        PropertyFilter.KeyDown += PropertyFilter_KeyDown;

        // The xanim's notetracks dock: its splitter saves the height for the type; a double-click folds it.
        DockSplitter.DragCompleted += (_, _) => SaveDockHeight();
        DockSplitter.DoubleTapped += (_, e) =>
        {
            ToggleDockCollapsed();
            e.Handled = true;
        };
        AnimDock.CollapseToggled += ToggleDockCollapsed;
        AnimArea.SizeChanged += (_, e) =>
        {
            if (e.HeightChanged)
                ApplyDockHeight();
        };

        // The filter box fits what the tabs leave (see FitFilter): refit when the row or any tab changes width.
        ViewTabRow.SizeChanged += (_, e) =>
        {
            if (e.WidthChanged)
                FitFilter();
        };
        foreach (var tab in ViewTabs.Children)
        {
            tab.SizeChanged += (_, e) =>
            {
                if (e.WidthChanged)
                    FitFilter();
            };
            tab.PropertyChanged += (_, e) =>
            {
                if (e.Property == IsVisibleProperty)
                    FitFilter();
            };
        }
        PropertyFilter.GotFocus += (_, _) => ShowFilterState();
        PropertyFilter.LostFocus += (_, _) => ShowFilterState();
        PropertyFilter.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty)
                ShowFilterTip();
        };
        ShowFilterTip();
    }

    // ── Add property (Set view) ───────────────────────────────────────────

    private Flyout? _addFlyout;

    /// <summary>The open (or last opened) Add property list.</summary>
    public AddPropertyPicker? Picker { get; private set; }

    private void AddProperty_Click(object? sender, RoutedEventArgs e) => OpenAddProperty();

    /// <summary>Opens the Add property list under its button (the Set view's, switched to if another is showing).</summary>
    public void OpenAddProperty()
    {
        if (_vm is null)
            return;
        if (!_vm.IsSetView)
            _vm.View = EditorView.Set;
        // The button shows once the view has switched; the list opens under it after that layout.
        Dispatcher.UIThread.Post(() =>
        {
            if (Picker is null)
            {
                Picker = new AddPropertyPicker();
                _addFlyout = new Flyout { Content = Picker, Placement = PlacementMode.BottomEdgeAlignedLeft };
                Picker.Picked += () => _addFlyout.Hide();
            }
            Picker.DataContext = _vm;
            Picker.Reset();
            _addFlyout!.ShowAt(AddPropertyButton);
        }, DispatcherPriority.Loaded);
    }

    // ── View tabs · filter: the box shrinks, then folds to ⌕ ──────────────

    private const double FilterMax = 220, FilterMin = 140, FilterGap = 12;

    /// <summary>True when the row has no room for even the narrowest box beside the tabs: the filter is the ⌕ button.</summary>
    public bool IsFilterFolded { get; private set; }

    private bool _filterOpening;

    /// <summary>The tabs' own width (what they take unclipped), whether or not the open filter is covering them now.</summary>
    private double TabsWidth()
    {
        double width = 0;
        var shown = 0;
        foreach (var tab in ViewTabs.Children)
        {
            if (!tab.IsVisible)
                continue;
            // Under the open filter the row isn't laid out, so a count that changed meanwhile isn't in DesiredSize yet.
            if (!ViewTabs.IsVisible)
                tab.Measure(Size.Infinity);
            width += tab.DesiredSize.Width;
            shown++;
        }
        return width + Math.Max(0, shown - 1) * ViewTabs.Spacing;
    }

    private void FitFilter()
    {
        var row = ViewTabRow.Bounds.Width;
        if (row <= 0)
            return;
        var room = row - TabsWidth() - FilterGap;
        IsFilterFolded = room < FilterMin;
        if (!IsFilterFolded)
            PropertyFilter.Width = Math.Min(FilterMax, Math.Floor(room));
        ShowFilterState();
    }

    /// <summary>
    /// Folded, the box opens across the row (over the tabs) only while it has focus, and is ⌕ otherwise, so the tabs
    /// stay usable while a filter applies; ⌕ then carries a dot and its tooltip says what it filters by.
    /// </summary>
    private void ShowFilterState()
    {
        var open = IsFilterFolded && (_filterOpening || PropertyFilter.IsKeyboardFocusWithin);
        // The window widened and the box came back beside the tabs: the keyboard on ⌕ goes into it.
        if (!IsFilterFolded && FilterButton.IsKeyboardFocusWithin)
            Dispatcher.UIThread.Post(() => PropertyFilter.Focus(NavigationMethod.Directional), DispatcherPriority.Loaded);
        FilterButton.IsVisible = IsFilterFolded && !open;
        PropertyFilter.IsVisible = !IsFilterFolded || open;
        ViewTabs.IsVisible = !open;
        Grid.SetColumn(PropertyFilter, open ? 0 : 1);
        Grid.SetColumnSpan(PropertyFilter, open ? 2 : 1);
        PropertyFilter.Margin = open ? default : new Thickness(FilterGap, 0, 0, 0);
        if (open)
            PropertyFilter.Width = double.NaN;
        else if (IsFilterFolded is false && double.IsNaN(PropertyFilter.Width))
            PropertyFilter.Width = FilterMax; // FitFilter narrows it on the next pass
    }

    private void ShowFilterTip()
    {
        var text = PropertyFilter.Text;
        var gesture = Apex.Editor.Commands.CommandCatalog.Get("edit.filterProperties").GestureText;
        ToolTip.SetTip(FilterButton, string.IsNullOrEmpty(text)
            ? $"Filter properties ({gesture})"
            : $"Filtering by “{text}” ({gesture} to change it)");
        Avalonia.Automation.AutomationProperties.SetName(FilterButton,
            string.IsNullOrEmpty(text) ? "Filter properties" : $"Filter properties, filtering by {text}");
    }

    private void FilterButton_Click(object? sender, RoutedEventArgs e) => FocusFilter();

    private void Attach(AssetEditorViewModel? vm)
    {
        if (_vm is not null)
        {
            _vm.ScrollToRequested -= ScrollTo;
            _vm.PropertyChanged -= Vm_PropertyChanged;
        }
        _vm = vm;
        if (_vm is not null)
        {
            _vm.ScrollToRequested += ScrollTo;
            _vm.PropertyChanged += Vm_PropertyChanged;
        }
        ApplyDockHeight();
        FitLabels();
        Dispatcher.UIThread.Post(TrackCurrentSection, DispatcherPriority.Background);
    }

    private void Vm_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AssetEditorViewModel.CentrePreview))
            ApplyDockHeight();
        // The rules showed or hid rows: the label column fits the labels shown now.
        else if (e.PropertyName == nameof(AssetEditorViewModel.ShownPropertyCount) && _vm is { } vm && vm.ShownPropertyCount != _fittedFor)
            FitLabels();
    }

    // ── The label column: as wide as the asset's widest shown label, clamped ──

    /// <summary>The label column never gets narrower than this (short labels keep a calm left edge for the values)…</summary>
    public const double LabelMin = 140;

    /// <summary>…nor wider: a longer label trims, whole in its tooltip, so one long label can't push every value right.</summary>
    public const double LabelMax = 300;

    /// <summary>In a narrow pane the labels give way first, down to this share of the row, so the values keep room.</summary>
    private const double LabelShare = 0.42;

    // The widest shown label's fit (before the narrow pane's cap), and how many rows showed when it was fitted.
    private double _fittedLabels = LabelMin;
    private int _fittedFor = -1;

    // Labels repeat across every asset of a type: each is measured once.
    private static readonly Dictionary<string, double> LabelWidths = new(StringComparer.Ordinal);
    private static Avalonia.Media.Typeface? _labelFace;
    private static double _labelSize;

    /// <summary>The label column's width now (what <c>FormLabelWidth</c> holds).</summary>
    public double LabelWidth => Resources.TryGetResource("FormLabelWidth", null, out var w) && w is double d ? d : LabelMin;

    /// <summary>Fits the label column to the widest label the form shows for this asset, between the clamps.</summary>
    private void FitLabels()
    {
        if (_vm is null || _vm.IsAnim)
            return;
        _fittedFor = _vm.ShownPropertyCount;
        if (_labelFace is null)
        {
            var family = this.TryFindResource("UiFont", out var f) && f is Avalonia.Media.FontFamily ff ? ff : Avalonia.Media.FontFamily.Default;
            _labelFace = new Avalonia.Media.Typeface(family);
            _labelSize = this.TryFindResource("FontSizeBase", out var sz) && sz is double size ? size : 13;
        }
        double widest = 0;
        foreach (var label in _vm.FormLabels)
        {
            if (!LabelWidths.TryGetValue(label, out var width))
            {
                width = new Avalonia.Media.FormattedText(label, System.Globalization.CultureInfo.CurrentUICulture,
                    Avalonia.Media.FlowDirection.LeftToRight, _labelFace.Value, _labelSize, null).WidthIncludingTrailingWhitespace;
                LabelWidths[label] = width;
            }
            widest = Math.Max(widest, width);
        }
        // Room for the label's own breathing space before the value.
        _fittedLabels = Math.Clamp(Math.Ceiling(widest) + 12, LabelMin, LabelMax);
        ApplyLabelWidth();
    }

    /// <summary>The fitted width, or less in a pane too narrow to give the labels that and the values their room.</summary>
    private void ApplyLabelWidth()
    {
        // The row's width: the form less its scroll padding, the bar and the gutters.
        var row = Form.Bounds.Width - 22 - 26 - 4;
        var width = row > 0 ? Math.Min(_fittedLabels, Math.Max(96, Math.Floor(row * LabelShare))) : _fittedLabels;
        if (LabelWidth != width)
            Resources["FormLabelWidth"] = width;
    }

    // ── The xanim's notetracks dock: height per asset type, folded to its transport on a double-click ──

    private const double DockDefault = 340, DockMin = 160, PreviewMin = 140, DockGap = 10;

    private string DockKey => _vm?.TypeName.ToLowerInvariant() ?? "";

    private bool DockCollapsed => _vm?.Owner?.Settings.DocksCollapsed.Contains(DockKey) == true;

    /// <summary>
    /// Sizes the dock row: the saved height for this type (fitted so the preview keeps its minimum), the transport row
    /// alone when folded, or the whole area when the preview is in its own window.
    /// </summary>
    private void ApplyDockHeight()
    {
        if (_vm is not { IsAnim: true } vm)
            return;
        var rows = AnimArea.RowDefinitions;
        var collapsed = DockCollapsed;
        AnimDock.IsCollapsed = collapsed;
        if (!vm.HasCentrePreview)
        {
            // The preview is popped out: the dock has the editor to itself.
            rows[0].Height = new GridLength(0);
            rows[1].Height = new GridLength(0);
            rows[2].Height = collapsed ? new GridLength(NotetrackDockView.CollapsedHeight) : new GridLength(1, GridUnitType.Star);
            DockSplitter.IsVisible = false;
            return;
        }
        DockSplitter.IsVisible = true;
        rows[0].Height = new GridLength(1, GridUnitType.Star);
        rows[1].Height = new GridLength(DockGap);
        if (collapsed)
        {
            rows[2].Height = new GridLength(NotetrackDockView.CollapsedHeight);
            return;
        }
        var saved = vm.Owner?.Settings.DockHeights.GetValueOrDefault(DockKey, DockDefault) ?? DockDefault;
        var room = AnimArea.Bounds.Height;
        var height = room > 0 ? Math.Min(saved, Math.Max(DockMin, room - PreviewMin - DockGap)) : saved;
        rows[2].Height = new GridLength(Math.Max(DockMin, height));
    }

    private void SaveDockHeight()
    {
        if (_vm?.Owner is not { } owner || !AnimArea.RowDefinitions[2].Height.IsAbsolute)
            return;
        var height = AnimArea.RowDefinitions[2].ActualHeight;
        if (height < DockMin)
        {
            // Dragged down past its minimum: folded, as a double-click would.
            if (!owner.Settings.DocksCollapsed.Contains(DockKey))
                owner.Settings.DocksCollapsed.Add(DockKey);
        }
        else
        {
            owner.Settings.DocksCollapsed.Remove(DockKey);
            owner.Settings.DockHeights[DockKey] = Math.Round(height);
        }
        owner.SaveSettings();
        ApplyDockHeight();
    }

    private void ToggleDockCollapsed()
    {
        if (_vm?.Owner is not { } owner)
            return;
        if (!owner.Settings.DocksCollapsed.Remove(DockKey))
            owner.Settings.DocksCollapsed.Add(DockKey);
        owner.SaveSettings();
        ApplyDockHeight();
    }

    /// <summary>Filter properties (Ctrl+F): the keyboard goes to the filter box, its text selected.</summary>
    public void FocusFilter()
    {
        // Folded: open the box first, since a hidden box can't take focus.
        _filterOpening = true;
        ShowFilterState();
        PropertyFilter.Focus();
        _filterOpening = false;
        PropertyFilter.SelectAll();
    }

    private void PropertyFilter_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _vm is { SearchText.Length: > 0 } vm)
        {
            vm.SearchText = "";
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && IsFilterFolded)
        {
            // An empty open box: Esc folds it back, and the keyboard lands on ⌕ (where Enter opens it again) once ⌕ is
            // laid out.
            FilterButton.IsVisible = true;
            Dispatcher.UIThread.Post(() => FilterButton.Focus(NavigationMethod.Directional), DispatcherPriority.Loaded);
            e.Handled = true;
        }
        else if (e.Key == Key.Down && _vm is { } v && v.MoveFocus(1) is { } first)
        {
            FocusRow(first);
            e.Handled = true;
        }
    }

    // ── Rail: jump to a section; the section in view lights up as you scroll ──

    private void Rail_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm is null || (sender as Control)?.DataContext is not CategoryViewModel section)
            return;
        // Re-selecting the same section must still scroll back to it.
        _vm.SelectedRail = null;
        _vm.SelectedRail = section;
    }

    private const double RowHeight = 30;

    private static double HeaderHeight(CategoryViewModel c) => c.IsFirst ? 34 : 42;

    /// <summary>A form row's height, which each kind fixes (see the row styles), so offsets are sums.</summary>
    private static double HeightOf(object item) => item switch
    {
        CategoryViewModel c => HeaderHeight(c),
        MatrixRowViewModel matrix => matrix.Height,
        RecordsPropertyViewModel table => RecordTableEditor.HeightOf(table),
        PartsPropertyViewModel parts => RowHeight * (1 + parts.Parts.Count),
        ExtensionOffRowViewModel => 40,
        ExtensionNoticeRowViewModel => 26,
        SubsectionRowViewModel => 34,
        _ => RowHeight,
    };

    /// <summary>Rows have fixed heights, so a row's offset is a sum — no realization needed.</summary>
    private double OffsetOf(object target)
    {
        // A vector's component is in its vector's row.
        if (target is PropertyItemViewModel property)
            target = _vm!.RowFor(property);
        var y = 0.0;
        foreach (var item in _vm!.FlatRows)
        {
            if (ReferenceEquals(item, target))
                return y;
            y += HeightOf(item);
        }
        return y;
    }

    private void ScrollTo(object target)
    {
        // An xanim has no form here: a notetrack key selects its note in the dock (the panel shows the rest).
        if (_vm is { IsAnim: true })
        {
            if (target is PropertyItemViewModel note && Services.Preview.Notetracks.GdtNotetracks.IsNotetrackKey(note.Key))
                AnimDock.Reveal(note.Key);
            return;
        }
        if (_scroll is null || _vm is null)
            return;
        var y = OffsetOf(target);
        if (target is PropertyItemViewModel)
        {
            // Rows scroll just enough to be seen; sections go to the top.
            var top = _scroll.Offset.Y;
            var bottom = top + _scroll.Viewport.Height - RowHeight - 16;
            if (y >= top && y <= bottom)
            {
                Dispatcher.UIThread.Post(() => FocusRow((PropertyItemViewModel)target), DispatcherPriority.Background);
                return;
            }
            y = Math.Max(0, y - _scroll.Viewport.Height / 3);
        }
        _scroll.Offset = new Vector(0, y);
        if (target is PropertyItemViewModel p)
            Dispatcher.UIThread.Post(() => FocusRow(p), DispatcherPriority.Background);
    }

    /// <summary>The section of the first row at the top of the viewport is the one the rail lights.</summary>
    private void TrackCurrentSection()
    {
        if (_vm is null)
            return;
        var top = (_scroll?.Offset.Y ?? 0) + 1;
        var y = 0.0;
        CategoryViewModel? current = null;
        foreach (var item in _vm.FlatRows)
        {
            if (item is CategoryViewModel c)
                current = c;
            var h = HeightOf(item);
            if (y + h > top)
                break;
            y += h;
        }
        _vm.SetCurrentSection(current);
    }

    // ── Row focus → Inspector; ↑↓ walk the form ─────────────────────────────

    // A colour picker's fields are bound to the colour's components, and a line list's to its items: the row they
    // belong to is the picker's, or the list's.
    private static PropertyItemViewModel? RowOf(object? source) =>
        ((source as Visual)?.FindAncestorOfType<ColorPanel>(includeSelf: true)
            ?? (source as Visual)?.FindAncestorOfType<LineListEditor>()
            ?? (source as Visual)?.FindAncestorOfType<RecordTableEditor>()
            ?? PartListOf(source)
            ?? source as Visual)?.GetSelfAndVisualAncestors()
            .OfType<StyledElement>()
            // A vector's row (its label, its gaps) stands for its first component.
            .Select(e => e.DataContext is VectorRowViewModel vector ? vector.Parts[0] : e.DataContext)
            .OfType<PropertyItemViewModel>()
            .FirstOrDefault();

    // A part's editor belongs to the value in parts around it.
    private static ItemsControl? PartListOf(object? source) =>
        (source as Visual)?.GetSelfAndVisualAncestors().OfType<ItemsControl>().FirstOrDefault(i => i.Classes.Contains("partlist"));

    /// <summary>↑↓ in a value in parts walk its parts, then fall through to the form's rows at either end.</summary>
    private static bool StepPart(object? source, Key key)
    {
        if (PartListOf(source) is not { } list || (source as Visual)?.GetSelfAndVisualAncestors()
                .OfType<Control>().FirstOrDefault(c => c.GetVisualParent() == list.ItemsPanelRoot) is not { } part)
            return false;
        var index = list.IndexFromContainer(part);
        var to = index + (key == Key.Down ? 1 : -1);
        if (index < 0 || to < 0 || to >= list.ItemCount || list.ContainerFromIndex(to) is not { } next || EditorIn(next) is not { } editor)
            return false;
        editor.Focus(NavigationMethod.Directional);
        return true;
    }

    private void Form_GotFocus(object? sender, FocusChangedEventArgs e)
    {
        if (_vm is not null && RowOf(e.Source) is { } row)
            _vm.FocusedProperty = row;
    }

    private void Form_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_vm is null || RowOf(e.Source) is not { } row)
            return;
        _vm.FocusedProperty = row;
        // A click on the label (not focusable) puts the keyboard in the row's editor, so typing or
        // Ctrl+↑↓ acts on the row just picked. A press inside any focusable control is that
        // control's own: moving focus mid-press would cancel its click.
        var hit = (e.Source as Visual)?.GetSelfAndVisualAncestors().TakeWhile(v => v != Form).ToList();
        if (hit is null || hit.OfType<InputElement>().Any(i => i.Focusable))
            return;
        if (hit.OfType<Border>().FirstOrDefault(b => b.Classes.Contains("prow")) is { } rowBorder
            && EditorIn(rowBorder) is { } editor)
            Dispatcher.UIThread.Post(() => editor.Focus(NavigationMethod.Pointer), DispatcherPriority.Input);
    }

    /// <param name="row">In a vector's row, the component whose box to pick (its first otherwise).</param>
    private static InputElement? EditorIn(Visual container, PropertyItemViewModel? row = null) =>
        container.GetVisualDescendants()
            .OfType<InputElement>()
            .Where(c => c is ScrubNumberBox or TextBox or ComboBox or ToggleButton && c.Focusable && c.IsEffectivelyEnabled)
            .OrderBy(c => row is not null && c.DataContext == row ? 0 : 1)
            .FirstOrDefault();

    private void Form_KeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm is null || e.KeyModifiers != KeyModifiers.None)
            return;
        // An open dropdown owns its arrows, and so does a number being typed (↑↓ step it).
        // (An open list's keys come from its items, which sit in a popup under the ComboBox.)
        if ((e.Source as Avalonia.LogicalTree.ILogical)?.FindLogicalAncestorOfType<ComboBox>(includeSelf: true) is { IsDropDownOpen: true }
            || (e.Source is TextBox t && t.Classes.Contains("scrubedit"))
            || SuggestBox.OwnsArrows(e.Source)
            || LineListEditor.OwnsArrows(e.Source, e.Key)
            || RecordTableEditor.OwnsArrows(e.Source, e.Key)
            || KeyMatrixView.OwnsArrows(e.Source))
            return;
        switch (e.Key)
        {
            case Key.Up or Key.Down when StepPart(e.Source, e.Key):
                e.Handled = true;
                break;
            case Key.Up or Key.Down:
                if (_vm.MoveFocus(e.Key == Key.Down ? 1 : -1) is { } next)
                {
                    ScrollTo(next);
                    e.Handled = true;
                }
                break;
        }
    }

    /// <summary>Moves keyboard focus into a row's editor (realizing it first if it was off-screen).</summary>
    private void FocusRow(PropertyItemViewModel row)
    {
        if (_vm is null)
            return;
        var index = _vm.FlatRows.IndexOf(_vm.RowFor(row));
        if (index < 0)
            return;
        Form.ScrollIntoView(index);
        Dispatcher.UIThread.Post(() =>
        {
            if (Form.ContainerFromIndex(index) is not { } container)
                return;
            // The keyboard is already in the row (a cell of a table, the item of a list): it stays where it is.
            if (container.IsKeyboardFocusWithin && _vm.RowFor(row) == row)
                _vm.FocusedProperty = row;
            else if (EditorIn(container, row) is { } editor)
                editor.Focus(NavigationMethod.Directional);
            else
                _vm.FocusedProperty = row;
        }, DispatcherPriority.Loaded);
    }

    private void ShowAll_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm is null)
            return;
        _vm.SearchText = "";
        _vm.View = EditorView.All;
    }
}
