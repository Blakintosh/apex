using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Apex.Editor.Services.Gdf;

namespace Apex.Editor.ViewModels;

/// <summary>One button of a deffile button group, as the form shows it.</summary>
public sealed class DeffileButtonViewModel
{
    public DeffileButtonViewModel(ButtonGroupRowViewModel group, DeffileButton button, Action<ButtonGroupRowViewModel, DeffileButton>? run)
    {
        Group = group;
        Button = button;
        Command = new RelayCommand(() => run?.Invoke(group, button));
    }

    public ButtonGroupRowViewModel Group { get; }
    public DeffileButton Button { get; }
    public string Label => Button.Label;
    public IRelayCommand Command { get; }

    /// <summary>
    /// "Medal 4 → Add Item", "Number of LODs → 3": the group, then the button. The palette, screen readers and the status
    /// line after a click all use it; an arrow rather than a colon, so "Number of LODs → 3: 3 values changed" reads once.
    /// </summary>
    public string Title =>
        // A title the deffile builds from an empty name ("Object 2 - " before the object is named) loses its dangling dash.
        $"{(Group.Title is "" or "Edit" or "Copy" or "Validation" ? Group.Category : Group.Title).TrimEnd(' ', '-', ':')} → {Button.Label}";
}

/// <summary>
/// A deffile ButtonGroup row (APE's AddEntry_ButtonGroup): its title in the label column and its buttons in the value
/// column, in deffile order. A click runs the button's callback; what it writes lands as one undo step.
/// </summary>
public sealed partial class ButtonGroupRowViewModel : ObservableObject, Controls.IRowStandIn
{
    private readonly Action<ButtonGroupRowViewModel, DeffileButton>? _run;

    public ButtonGroupRowViewModel(string name, string category, Action<ButtonGroupRowViewModel, DeffileButton>? run)
    {
        Name = name;
        Category = category;
        _run = run;
    }

    object? Controls.IRowStandIn.CreateStandIn() => new ButtonGroupRowViewModel(Name, Category, null);

    public string Name { get; }
    public string Category { get; }

    [ObservableProperty]
    private string _title = "";

    [ObservableProperty]
    private string? _toolTip;

    /// <summary>The deffile's .Enable(...) for the group.</summary>
    [ObservableProperty]
    private bool _isEnabled = true;

    /// <summary>The deffile shows the group for the asset's current values (.Show, or it wasn't added at all).</summary>
    public bool IsShown { get; set; }

    /// <summary>The key of the property row it sits above; null at the end of its section.</summary>
    public string? AnchorKey { get; set; }

    [ObservableProperty]
    private IReadOnlyList<DeffileButtonViewModel> _buttons = [];

    /// <summary>Brings the row to <paramref name="group"/>; the buttons are rebuilt only when they differ.</summary>
    public void Update(DeffileButtonGroup group)
    {
        Title = group.Title;
        ToolTip = group.ToolTip;
        IsEnabled = group.Enabled;
        IsShown = group.Visible;
        AnchorKey = group.AnchorKey;
        if (!Buttons.Select(b => b.Button).SequenceEqual(group.Buttons))
            Buttons = group.Buttons.Select(b => new DeffileButtonViewModel(this, b, _run)).ToArray();
    }
}
