using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Apex.Editor.Controls;
using Apex.Editor.Services.Preview.Notetracks;
using Apex.Editor.ViewModels;

namespace Apex.Editor.Views;

public partial class AnimPropertiesView : UserControl
{
    /// <summary>An open card's header has a rule under it; a closed card is its header alone.</summary>
    public static readonly IValueConverter HeaderRule =
        new FuncValueConverter<bool, Thickness>(open => open ? new Thickness(0, 0, 0, 1) : default);

    private AssetEditorViewModel? _vm;
    private bool _filterOpening;

    public AnimPropertiesView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as AssetEditorViewModel);
        PanelFilter.KeyDown += PanelFilter_KeyDown;
        PanelFilter.GotFocus += (_, _) => ShowFilterState();
        PanelFilter.LostFocus += (_, _) => ShowFilterState();
        PanelFilter.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty)
                ShowFilterState();
        };
        // Focus or a press in a card's row makes it the Inspector's subject, as in the full form.
        Cards.AddHandler(GotFocusEvent, Cards_GotFocus, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private void Attach(AssetEditorViewModel? vm)
    {
        if (_vm is not null)
            _vm.ScrollToRequested -= Reveal;
        _vm = vm;
        if (vm is not null)
            vm.ScrollToRequested += Reveal;
        ShowFilterState();
    }

    /// <summary>Open while it has focus or text; otherwise ⌕ alone, so the cards keep the room.</summary>
    private void ShowFilterState() =>
        PanelFilter.IsVisible = _filterOpening || PanelFilter.IsKeyboardFocusWithin || !string.IsNullOrEmpty(PanelFilter.Text);

    private void FilterButton_Click(object? sender, RoutedEventArgs e) => FocusFilter();

    /// <summary>Filter properties (Ctrl+F) in the xanim editor: the panel's box, its text selected.</summary>
    public void FocusFilter()
    {
        _filterOpening = true;
        ShowFilterState();
        PanelFilter.Focus();
        _filterOpening = false;
        PanelFilter.SelectAll();
    }

    private void PanelFilter_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;
        e.Handled = true;
        if (_vm is { SearchText.Length: > 0 } vm)
        {
            vm.SearchText = "";
            return;
        }
        // Empty: Esc closes it, the keyboard back on ⌕.
        PanelFilterButton.Focus(NavigationMethod.Directional);
        ShowFilterState();
    }

    private void Cards_GotFocus(object? sender, FocusChangedEventArgs e)
    {
        if (_vm is not null && (e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<StyledElement>()
                .Select(s => s.DataContext).OfType<PropertyItemViewModel>().FirstOrDefault() is { } row)
            _vm.FocusedProperty = row;
    }

    /// <summary>A row the editor was asked to show (a palette jump, an undo): its card opens and the row comes into view.</summary>
    private void Reveal(object target)
    {
        if (_vm is not { IsAnim: true } vm || target is not PropertyItemViewModel row || GdtNotetracks.IsNotetrackKey(row.Key))
            return;
        if (vm.CategoryOf(row) is { IsExpanded: false } card)
            card.IsExpanded = true;
        Dispatcher.UIThread.Post(() =>
        {
            var editor = this.GetVisualDescendants().OfType<Control>()
                .FirstOrDefault(c => c.Classes.Contains("cardrow") && ReferenceEquals(c.DataContext, row));
            editor?.BringIntoView();
            editor?.GetVisualDescendants().OfType<InputElement>()
                .FirstOrDefault(c => c is ScrubNumberBox or TextBox or ComboBox or Avalonia.Controls.Primitives.ToggleButton && c.Focusable && c.IsEffectivelyEnabled)
                ?.Focus(NavigationMethod.Directional);
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
