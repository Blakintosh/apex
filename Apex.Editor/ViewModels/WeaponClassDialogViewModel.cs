using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Apex.Editor.Models;
using Apex.Editor.Services;

namespace Apex.Editor.ViewModels;

/// <summary>A choice the new class doesn't offer: the old value struck through and a pick from the new class's list.</summary>
public sealed partial class RemapChoiceRow : ObservableObject
{
    public RemapChoiceRow(RemapChoice choice, int familySize)
    {
        Choice = choice;
        Selected = choice.Suggested;
        HoldersText = familySize > 1 ? (choice.Assets == 1 ? "in 1 asset" : $"in {choice.Assets} assets") : "";
    }

    public RemapChoice Choice { get; }
    public string Label => Choice.Def.Label;
    public string Key => Choice.Def.Key;
    public string OldValue => Choice.OldValue;
    public IReadOnlyList<string> Options => Choice.Def.Choices;
    public string HoldersText { get; }
    public bool HasHolders => HoldersText.Length > 0;
    public string AccessibleName => $"New value for {Label} (was {OldValue})";

    [ObservableProperty]
    private string _selected;
}

/// <summary>A key the new class has no field for.</summary>
public sealed record RemapOrphanRow(string Key, string Value, string HoldersText);

/// <summary>
/// Change weapon class: which class, and what becomes of the values the new one can't hold as they are. The plan is
/// recomputed when the class changes; nothing touches the session until <see cref="Accept"/>.
/// </summary>
public sealed partial class WeaponClassDialogViewModel : ObservableObject
{
    private readonly Action<WeaponClassDialogViewModel> _accept;
    private readonly Action _cancel;

    public WeaponClassDialogViewModel(AssetRecord opened, AssetRecord root, IReadOnlyList<AssetRecord> family,
        IReadOnlyList<string> targets, Action<WeaponClassDialogViewModel> accept, Action cancel)
    {
        Opened = opened;
        Root = root;
        Family = family;
        Targets = targets;
        From = root.Type;
        _accept = accept;
        _cancel = cancel;
        _target = DefaultTarget(root.Type, targets);
        Replan();
    }

    public AssetRecord Opened { get; }
    public AssetRecord Root { get; }
    public IReadOnlyList<AssetRecord> Family { get; }
    public IReadOnlyList<string> Targets { get; }
    public string From { get; }

    /// <summary>Who changes: the asset, and its derived assets with it; or, opened on a derived asset, the root it takes its class from.</summary>
    public string Intro
    {
        get
        {
            var kids = Family.Count - 1;
            var derived = kids switch
            {
                0 => "",
                1 => $" Its derived asset, {Family[1].Name}, takes the new class with it.",
                <= 3 => $" Its {kids} derived assets take the new class with it: {string.Join(", ", Family.Skip(1).Select(f => f.Name))}.",
                _ => $" Its {kids} derived assets take the new class with it.",
            };
            if (Opened != Root)
                return $"{Opened.Name} takes its class from {Root.Name}, the asset it derives from, so the class changes there."
                       + (kids == 1 ? " The two change together." : $" {Root.Name} and its {kids} derived assets change together.");
            return $"{Root.Name} becomes a different kind of weapon.{derived}";
        }
    }

    [ObservableProperty]
    private string _target;

    partial void OnTargetChanged(string value) => Replan();

    public RemapPlan? Plan { get; private set; }

    public IReadOnlyList<RemapChoiceRow> Choices { get; private set; } = Array.Empty<RemapChoiceRow>();
    public IReadOnlyList<RemapOrphanRow> Orphans { get; private set; } = Array.Empty<RemapOrphanRow>();

    /// <summary>Keys the new class has no field for that hold the old class's default: counted, not listed.</summary>
    public int DefaultOrphans { get; private set; }

    [ObservableProperty]
    private bool _keepOrphans;

    public bool HasChoices => Choices.Count > 0;
    public bool HasOrphans => Plan is { Orphans.Count: > 0 };
    public bool HasSetOrphans => Orphans.Count > 0;
    public bool HasDefaultOrphans => DefaultOrphans > 0;
    public string DefaultOrphansText => HasSetOrphans
        ? $"and {DefaultOrphans:N0} more at {From}'s defaults"
        : $"All {DefaultOrphans:N0} are at {From}'s defaults.";
    public bool NothingToSettle => Plan is not null && !HasChoices && !HasOrphans;

    public string CarriedText => Plan is null ? "" : Count(Plan.Carried, "value carries over", "values carry over");
    public string ChoicesText => Plan is null ? "" : Count(Plan.Choices.Count, "needs a new value", "need a new value");
    public string OrphansText => Plan is null ? "" : $"{Plan.Orphans.Count:N0} {(Plan.Orphans.Count == 1 ? "isn't" : "aren't")} in {Target}";
    public string NewFieldsText => Plan is null ? "" : $"{Plan.NewFields:N0} new, at {(Plan.NewFields == 1 ? "its default" : "their defaults")}";
    public string ChoicesNote => $"{Target} doesn't offer these. Each starts at {Target}'s default.";
    public string OrphansHeading => $"Not in {Target}";
    public string OrphansNote => Plan is not { Orphans.Count: var n } ? "" : n == 1 ? "1 value its deffile has no field for" : $"{n:N0} values its deffile has no field for";
    public string ActionLabel => Family.Count == 1 ? "Change class" : $"Change {Family.Count} assets";
    public string Unavailable => Plan is null ? $"Apex has no deffile for {Target}." : "";

    /// <summary>The picks for <see cref="WeaponClassRemap.Apply"/>.</summary>
    public Dictionary<RemapChoice, string> Picks() => Choices.ToDictionary(c => c.Choice, c => c.Selected);

    [RelayCommand]
    public void Accept()
    {
        if (Plan is not null)
            _accept(this);
    }

    [RelayCommand]
    public void Cancel() => _cancel();

    private void Replan()
    {
        Plan = SchemaRegistry.Get(From) is { } from && SchemaRegistry.Get(Target) is { } to
            ? WeaponClassRemap.Plan(Family, from, to)
            : null;
        Choices = Plan?.Choices.Select(c => new RemapChoiceRow(c, Family.Count)).ToList() ?? new List<RemapChoiceRow>();
        // The values someone set, one row each; the rest (APE writes every key, mostly at its default) as one line.
        Orphans = Plan?.Orphans.Where(o => !o.AtDefault).Select(o => new RemapOrphanRow(o.Key, o.Value,
            Family.Count > 1 ? (o.Assets == 1 ? "1 asset" : $"{o.Assets} assets") : "")).ToList() ?? new List<RemapOrphanRow>();
        DefaultOrphans = Plan?.Orphans.Count(o => o.AtDefault) ?? 0;
        OnPropertyChanged(string.Empty);
    }

    /// <summary>The class a weapon of <paramref name="from"/> most often goes to (a bullet weapon to a projectile one), else the first.</summary>
    private static string DefaultTarget(string from, IReadOnlyList<string> targets)
    {
        string? Want(string t) => targets.FirstOrDefault(x => x.Equals(t, StringComparison.OrdinalIgnoreCase));
        var preferred = from.ToLowerInvariant() switch
        {
            "bulletweapon" => Want("projectileweapon"),
            "projectileweapon" => Want("bulletweapon"),
            "dualwieldweapon" => Want("bulletweapon"),
            "dualwieldprojectileweapon" => Want("projectileweapon"),
            _ => null,
        };
        return preferred ?? targets[0];
    }

    private static string Count(int n, string one, string many) => $"{n:N0} {(n == 1 ? one : many)}";
}
