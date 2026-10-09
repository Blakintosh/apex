using Avalonia.Controls;
using Avalonia.Interactivity;
using Apex.Editor.ViewModels;

namespace Apex.Editor.Views;

public partial class PreviewDockView : UserControl
{
    private MenuFlyout? _subjectMenu;

    public PreviewDockView() => InitializeComponent();

    /// <summary>
    /// The subject picker as a menu: arrow keys move through it, Enter picks, Esc closes, and the subject on show is
    /// checked (the tab strip's all-tabs menu works the same way).
    /// </summary>
    private void SubjectButton_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
            return;
        var menu = _subjectMenu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
        foreach (var subject in vm.PreviewSubjects)
        {
            var header = new DockPanel { MinWidth = 240 };
            var detail = new TextBlock
            {
                Text = subject.Detail,
                Classes = { "faint", "mono" },
                FontSize = (double)this.FindResource("FontSizeXs")!,
                Margin = new Avalonia.Thickness(16, 0, 0, 0),
                MaxWidth = 180,
                TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            };
            DockPanel.SetDock(detail, Dock.Right);
            header.Children.Add(detail);
            header.Children.Add(new TextBlock { Text = subject.Label, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
            menu.Items.Add(new MenuItem
            {
                Header = header,
                Command = vm.ChoosePreviewSubjectCommand,
                CommandParameter = subject,
                ToggleType = MenuItemToggleType.Radio,
                IsChecked = subject == vm.PreviewSubject,
            });
        }
        menu.ShowAt(SubjectButton);
    }
}
