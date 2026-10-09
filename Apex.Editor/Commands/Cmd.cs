using System;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Markup.Xaml;
using Apex.Editor.ViewModels;

namespace Apex.Editor.Commands;

/// <summary>
/// Puts a catalog command on a menu item or button: <c>c:Cmd.Id="asset.rename"</c>. A menu item takes the command's
/// name, key and check state; a button takes "Name (keys)" as its tooltip and the name as its accessible name. Either
/// runs the command unless the element binds its own Command (a tab's context menu acting on that tab).
/// </summary>
public sealed class Cmd : AvaloniaObject
{
    public static readonly AttachedProperty<string?> IdProperty =
        AvaloniaProperty.RegisterAttached<Cmd, Control, string?>("Id");

    /// <summary>Set when the command, not the element's own binding, supplies Command.</summary>
    private static readonly AttachedProperty<bool> OwnsCommandProperty =
        AvaloniaProperty.RegisterAttached<Cmd, Control, bool>("OwnsCommand");

    static Cmd()
    {
        IdProperty.Changed.AddClassHandler<Control>((control, _) => Apply(control));
    }

    public static string? GetId(Control c) => c.GetValue(IdProperty);
    public static void SetId(Control c, string? value) => c.SetValue(IdProperty, value);

    private static void Apply(Control control)
    {
        if (GetId(control) is not { } id)
            return;
        var info = CommandCatalog.Get(id);
        switch (control)
        {
            case MenuItem item:
                item.Header = info.Name;
                // Only a window-wide key works wherever the menu is: Delete in the editor's ⋯ menu would promise a key
                // that only the Explorer hears.
                item.InputGesture = info.Scope == CommandScope.Window ? info.Gesture : null;
                if (info.IsToggle && item.ToggleType == MenuItemToggleType.None)
                    item.ToggleType = MenuItemToggleType.CheckBox;
                break;
            default:
                ToolTip.SetTip(control, info.Tip);
                if (!control.IsSet(AutomationProperties.NameProperty))
                    AutomationProperties.SetName(control, info.Name);
                break;
        }
        control.DataContextChanged -= Control_DataContextChanged;
        control.DataContextChanged += Control_DataContextChanged;
        Resolve(control);
    }

    private static void Control_DataContextChanged(object? sender, EventArgs e)
    {
        if (sender is Control control)
            Resolve(control);
    }

    /// <summary>The window's registry, from a MainViewModel or an editor tab's owner.</summary>
    private static CommandRegistry? RegistryOf(object? dataContext) => dataContext switch
    {
        MainViewModel vm => vm.Registry,
        AssetEditorViewModel { Owner: { } owner } => owner.Registry,
        _ => null,
    };

    private static void Resolve(Control control)
    {
        if (GetId(control) is not { } id || RegistryOf(control.DataContext) is not { } registry || !registry.Contains(id))
            return;
        var command = registry[id];
        if (!control.GetValue(OwnsCommandProperty))
        {
            var ownBinding = control switch
            {
                MenuItem m => m.IsSet(MenuItem.CommandProperty),
                Button b => b.IsSet(Button.CommandProperty),
                _ => true,
            };
            if (ownBinding)
                return;
            control.SetValue(OwnsCommandProperty, true);
        }
        switch (control)
        {
            case MenuItem item:
                item.Command = command;
                if (command.Info.IsToggle && item.ToggleType != MenuItemToggleType.None)
                    item.Bind(MenuItem.IsCheckedProperty, new Binding(nameof(AppCommand.IsChecked)) { Source = command, Mode = BindingMode.OneWay });
                break;
            case Button button:
                button.Command = command;
                if (command.Info.OnName is not null)
                    button.Bind(ToolTip.TipProperty, new Binding(nameof(AppCommand.Tip)) { Source = command, Mode = BindingMode.OneWay });
                break;
        }
    }
}

/// <summary>
/// Writes a menu item's key the way the rest of Apex does ("Alt+1", "⇧⌘P"), not as the Key enum names ("Alt+D1") the
/// theme's own converter prints. Registered over the theme's KeyGestureConverter resource.
/// </summary>
public sealed class GestureTextConverter : Avalonia.Data.Converters.IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
        value is Avalonia.Input.KeyGesture g ? KeyText.Format(g) : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary><c>{c:Tip go.asset}</c> → "Go to asset… (Ctrl+P)"; with Detail, a second line of explanation.</summary>
public sealed class TipExtension : MarkupExtension
{
    public TipExtension() { }
    public TipExtension(string id) => Id = id;

    public string Id { get; set; } = "";
    public string? Detail { get; set; }

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var tip = CommandCatalog.Get(Id).Tip;
        return Detail is { Length: > 0 } d ? $"{tip}\n{d}" : tip;
    }
}

/// <summary><c>{c:Gesture go.asset}</c> → "Ctrl+P" ("⌘P" on macOS).</summary>
public sealed class GestureExtension : MarkupExtension
{
    public GestureExtension() { }
    public GestureExtension(string id) => Id = id;

    public string Id { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) => CommandCatalog.Get(Id).GestureText;
}

/// <summary><c>{c:Name asset.compare}</c> → "Compare".</summary>
public sealed class NameExtension : MarkupExtension
{
    public NameExtension() { }
    public NameExtension(string id) => Id = id;

    public string Id { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) => CommandCatalog.Get(Id).Name;
}

/// <summary><c>{c:Hints 'go.asset go.commands'}</c> → "Ctrl+P Go to asset  ·  Ctrl+Shift+P Show all commands".</summary>
public sealed class HintsExtension : MarkupExtension
{
    public HintsExtension() { }
    public HintsExtension(string ids) => Ids = ids;

    public string Ids { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        CommandCatalog.Hints(Ids.Split(' ', StringSplitOptions.RemoveEmptyEntries));
}
