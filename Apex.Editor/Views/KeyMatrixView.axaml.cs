using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Apex.Editor.ViewModels;

namespace Apex.Editor.Views;

/// <summary>A section laid out as a grid of its keys (see the XAML).</summary>
public partial class KeyMatrixView : UserControl
{
    public KeyMatrixView()
    {
        InitializeComponent();
    }

    /// <summary>The grid moves its own selection with the arrows (the form's row-to-row walk leaves them alone).</summary>
    public static bool OwnsArrows(object? source) => source is KeyMatrixView;

    private MatrixRowViewModel? Vm => DataContext as MatrixRowViewModel;

    /// <summary>A click on a square selects it and brings the keyboard to the grid.</summary>
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || Vm is not { } vm)
            return;
        if ((e.Source as Visual)?.FindAncestorOfType<Border>(includeSelf: true) is { DataContext: MatrixCell { IsHeader: false } cell } && IsInGrid(e.Source))
        {
            vm.Select(cell.Row, cell.Column);
            // Not when the press landed in the editor below, which is its own field.
            Focus(NavigationMethod.Pointer);
            e.Handled = true;
        }
    }

    private bool IsInGrid(object? source) => source is Visual v && Squares.IsVisualAncestorOf(v);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || Vm is not { } vm || e.Source != this)
            return;
        switch (e.Key)
        {
            case Key.Left: vm.Move(0, -1); break;
            case Key.Right: vm.Move(0, 1); break;
            case Key.Up: vm.Move(-1, 0); break;
            case Key.Down: vm.Move(1, 0); break;
            case Key.Home: vm.Move(0, -vm.Grid.Columns.Count); break;
            case Key.End: vm.Move(0, vm.Grid.Columns.Count); break;
            case Key.Delete:
            case Key.Back:
                vm.ClearSelected();
                break;
            case Key.Enter:
            case Key.F2:
                FocusEditor();
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    /// <summary>Enter or F2: the keyboard goes to the selected property's editor, to type its value.</summary>
    private void FocusEditor()
    {
        if (Vm?.SelectedItem is null)
            return;
        var editor = this.GetVisualDescendants().OfType<PropertyEditorView>().FirstOrDefault();
        var field = editor?.GetVisualDescendants().OfType<InputElement>().FirstOrDefault(i => i.Focusable && i.IsEffectivelyVisible && i.IsEffectivelyEnabled);
        field?.Focus(NavigationMethod.Directional);
    }

    /// <summary>Esc in the editor: back to the grid, where the arrows work.</summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        AddHandler(KeyDownEvent, EditorKeyDown, RoutingStrategies.Bubble, handledEventsToo: false);
    }

    private void EditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && e.Source != this && e.Source is Visual v && !Squares.IsVisualAncestorOf(v))
        {
            Dispatcher.UIThread.Post(() => Focus(NavigationMethod.Directional), DispatcherPriority.Input);
            e.Handled = true;
        }
    }
}
