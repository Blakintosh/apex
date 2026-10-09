using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Apex.Editor.Models;

namespace Apex.Editor.ViewModels;

/// <summary>
/// A deffile vector (AddEntry_Vector2/3/4: an offset's X, Y and Z) as the form shows it: one row, its title once, and a
/// box per component side by side. Each component stays a property of its own (its key, value, undo, problem and
/// Inspector details are the component's), so this is only how the form lays them out: three rows repeating one long
/// label read as three settings.
/// </summary>
public sealed partial class VectorRowViewModel : ObservableObject, Controls.IRowStandIn
{
    public VectorRowViewModel(IReadOnlyList<PropertyItemViewModel> parts)
    {
        Parts = parts;
        foreach (var part in parts)
            part.PropertyChanged += Part_PropertyChanged;
    }

    object? Controls.IRowStandIn.CreateStandIn() =>
        new VectorRowViewModel(Parts.Select(p => new NumberPropertyViewModel(p.Def, p.Def.Default)).ToArray());

    /// <summary>The components, in the deffile's order.</summary>
    public IReadOnlyList<PropertyItemViewModel> Parts { get; }

    // The form's row has a slot per component a vector can have (2 to 4), bound to these, so a pooled row moving to the
    // next vector rebinds its boxes instead of building new ones.
    public PropertyItemViewModel? Part0 => Parts.Count > 0 ? Parts[0] : null;
    public PropertyItemViewModel? Part1 => Parts.Count > 1 ? Parts[1] : null;
    public PropertyItemViewModel? Part2 => Parts.Count > 2 ? Parts[2] : null;
    public PropertyItemViewModel? Part3 => Parts.Count > 3 ? Parts[3] : null;

    private PropertyDef First => Parts[0].Def;

    /// <summary>The vector's title, once.</summary>
    public string Label => First.VectorTitle ?? First.Label;

    /// <summary>The components' keys, as the hovered row shows a key.</summary>
    public string Key => string.Join("  ", Parts.Select(p => p.Key));

    public string Tooltip =>
        $"{string.Join(", ", Parts.Select(p => p.Key))}\n\n{First.Description}".TrimEnd()
        + $"\n\nDefault: {string.Join(" ", Parts.Select(p => p.Def.Default.Length == 0 ? "(empty)" : p.Def.Default))}";

    /// <summary>A component edited this session: the row's bar.</summary>
    public bool IsChanged => Parts.Any(p => p.IsChanged);

    /// <summary>The keyboard is in one of its boxes.</summary>
    public bool IsFocused => Parts.Any(p => p.IsFocused);

    /// <summary>Every component just follows the parent: the row reads quiet.</summary>
    public bool IsInherited => Parts.All(p => p.IsInherited);

    /// <summary>The deffile disables the entry (its components share the rule).</summary>
    public bool IsRuleDisabled => Parts[0].IsRuleDisabled;

    public string? DisabledTip => Parts[0].DisabledTip;

    /// <summary>The components' problems, one per line ("X: 40 is outside 0–10").</summary>
    public string? Problem
    {
        get
        {
            var problems = Parts.Where(p => p.HasProblem).Select(p => $"{p.Def.VectorPart ?? p.Label}: {p.Problem}").ToList();
            return problems.Count == 0 ? null : string.Join("\n", problems);
        }
    }

    public bool HasProblem => Parts.Any(p => p.HasProblem);

    public bool ShowDisabledMark => IsRuleDisabled && !HasProblem;

    /// <summary>↶ on the row: every component this session changed goes back.</summary>
    public bool ShowRevert => IsChanged;

    /// <summary>↑ on the row: a component overrides the parent and none was changed this session.</summary>
    public bool ShowRevertParent => !IsChanged && Parts.Any(p => p.ShowRevertParent);

    public string RevertTip => "Undo this session's changes to " + Label;

    public string ProvenanceTip => "Use the parent's values for " + Label;

    [RelayCommand]
    private void RevertChange()
    {
        foreach (var part in Parts)
            if (part.IsChanged)
                part.RawValue = part.BaselineValue;
    }

    [RelayCommand]
    private void RevertToParent()
    {
        foreach (var part in Parts)
            if (part.ShowRevertParent && part.ParentValue is { } parent)
                part.RawValue = parent;
    }

    private void Part_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PropertyItemViewModel.IsChanged):
                OnPropertyChanged(nameof(IsChanged));
                OnPropertyChanged(nameof(ShowRevert));
                OnPropertyChanged(nameof(ShowRevertParent));
                break;
            case nameof(PropertyItemViewModel.IsFocused):
                OnPropertyChanged(nameof(IsFocused));
                break;
            case nameof(PropertyItemViewModel.IsOverride):
                OnPropertyChanged(nameof(IsInherited));
                OnPropertyChanged(nameof(ShowRevertParent));
                break;
            case nameof(PropertyItemViewModel.IsRuleDisabled) or nameof(PropertyItemViewModel.DisabledTip):
                OnPropertyChanged(nameof(IsRuleDisabled));
                OnPropertyChanged(nameof(DisabledTip));
                OnPropertyChanged(nameof(ShowDisabledMark));
                break;
            case nameof(PropertyItemViewModel.Problem):
                OnPropertyChanged(nameof(Problem));
                OnPropertyChanged(nameof(HasProblem));
                OnPropertyChanged(nameof(ShowDisabledMark));
                break;
        }
    }
}
