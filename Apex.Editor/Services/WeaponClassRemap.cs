using System;
using System.Collections.Generic;
using System.Linq;
using Apex.Editor.Models;

namespace Apex.Editor.Services;

/// <summary>A choice value the new class doesn't offer, and how many of the family's assets hold it.</summary>
public sealed record RemapChoice(PropertyDef Def, string OldValue, int Assets)
{
    /// <summary>What it becomes unless the user picks another: the new class's default.</summary>
    public string Suggested => Def.Default.Length > 0 || Def.Choices.Length == 0 ? Def.Default : Def.Choices[0];
}

/// <summary>
/// A key the new class's deffile has no field for, with the value it holds (the first asset's that set it) and how many
/// hold it. <see cref="AtDefault"/>: every one holds the old class's default (APE writes every key, so most are).
/// </summary>
public sealed record RemapOrphan(string Key, string Value, int Assets, bool AtDefault);

/// <summary>
/// What changing a weapon family's class means for its values: which carry over as they are, which hold a choice the new
/// class doesn't offer, which keys the new class has no field for, and how many of its fields are new.
/// </summary>
public sealed class RemapPlan
{
    public required string From { get; init; }
    public required string To { get; init; }
    public required IReadOnlyList<AssetRecord> Family { get; init; }
    public required int Carried { get; init; }
    public required IReadOnlyList<RemapChoice> Choices { get; init; }
    public required IReadOnlyList<RemapOrphan> Orphans { get; init; }
    public required int NewFields { get; init; }
}

/// <summary>
/// Change weapon class (bulletweapon → projectileweapon…): a weapon's class is the deffile its GDT header names, held by
/// the root of its derivation chain; derived assets take it from there. The two deffiles share most keys, so most values
/// carry over as they are. A choice the new deffile doesn't offer takes a value the user picks; a key it has no field
/// for is dropped, or kept when asked (APE and the linker ignore it). Pure: the app applies the result as one step.
/// </summary>
public static class WeaponClassRemap
{
    /// <summary>Every weapon type with a schema, other than <paramref name="from"/>, in name order.</summary>
    public static IReadOnlyList<string> Targets(string from) =>
        SchemaRegistry.TypeNames
            .Where(t => Extensions.ExtensionManifest.IsWeapon(t) && !t.Equals(from, StringComparison.OrdinalIgnoreCase))
            .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>The plan for <paramref name="family"/> (its root first) moving from its class to <paramref name="to"/>.</summary>
    public static RemapPlan Plan(IReadOnlyList<AssetRecord> family, AssetSchema from, AssetSchema to)
    {
        var carried = 0;
        var choices = new Dictionary<(string Key, string Value), (PropertyDef Def, string Value, int Assets)>(KeyValueComparer.Instance);
        var orphans = new Dictionary<string, (string Key, string Value, int Assets, bool AtDefault)>(StringComparer.OrdinalIgnoreCase);
        foreach (var rec in family)
            foreach (var (key, value) in rec.Properties)
            {
                switch (Fate(to.Find(key), value))
                {
                    case Outcome.Orphan:
                        var atDefault = AssetQuery.ValuesEqual(value, from.Find(key)?.Default ?? "");
                        orphans[key] = orphans.TryGetValue(key, out var o)
                            ? o with { Assets = o.Assets + 1, Value = o.AtDefault && !atDefault ? value : o.Value, AtDefault = o.AtDefault && atDefault }
                            : (key, value, 1, atDefault);
                        break;
                    case Outcome.Choice:
                        var def = to.Find(key)!;
                        choices[(key, value)] = choices.TryGetValue((key, value), out var c) ? c with { Assets = c.Assets + 1 } : (def, value, 1);
                        break;
                    default:
                        carried++;
                        break;
                }
            }
        return new RemapPlan
        {
            From = from.TypeName,
            To = to.TypeName,
            Family = family,
            Carried = carried,
            Choices = choices.Values.Select(c => new RemapChoice(c.Def, c.Value, c.Assets))
                .OrderBy(c => c.Def.Label, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.OldValue, StringComparer.OrdinalIgnoreCase).ToList(),
            Orphans = orphans.Values.Select(o => new RemapOrphan(o.Key, o.Value, o.Assets, o.AtDefault))
                .OrderBy(o => o.AtDefault).ThenBy(o => o.Key, StringComparer.OrdinalIgnoreCase).ToList(),
            NewFields = to.Properties.Count(p => p.Extension.Length == 0 && from.Find(p.Key) is null),
        };
    }

    /// <summary>
    /// Each family member's values in the new class: carried as they are, a choice the new class lacks replaced by
    /// <paramref name="picks"/> (its suggested value when none was picked), a key it has no field for dropped unless
    /// <paramref name="keepOrphans"/>. Only members whose values change are in the result.
    /// </summary>
    public static Dictionary<AssetRecord, Dictionary<string, string>> Apply(RemapPlan plan, AssetSchema to,
        IReadOnlyDictionary<RemapChoice, string>? picks, bool keepOrphans)
    {
        var result = new Dictionary<AssetRecord, Dictionary<string, string>>();
        var choices = plan.Choices.ToDictionary(c => (c.Def.Key, c.OldValue), c => c, KeyValueComparer.Instance);
        foreach (var rec in plan.Family)
        {
            Dictionary<string, string>? values = null;
            foreach (var (key, value) in rec.Properties)
            {
                var def = to.Find(key);
                switch (Fate(def, value))
                {
                    case Outcome.Orphan when !keepOrphans:
                        (values ??= Copy(rec)).Remove(key);
                        break;
                    case Outcome.Choice:
                        // By key and old value, not by instance: a redo replans, and its choices are new objects.
                        var choice = choices[(key, value)];
                        var pick = picks?.FirstOrDefault(p => p.Key.Def.Key.Equals(key, StringComparison.OrdinalIgnoreCase)
                                                             && p.Key.OldValue == value).Value ?? choice.Suggested;
                        (values ??= Copy(rec))[key] = pick;
                        break;
                }
            }
            if (values is not null)
                result[rec] = values;
        }
        return result;
    }

    private enum Outcome { Carried, Choice, Orphan }

    private static Outcome Fate(PropertyDef? def, string value)
    {
        if (def is null || def.Extension.Length > 0)
            return Outcome.Orphan;
        if (def.Kind == PropertyKind.Choice && def.Choices.Length > 0 && value.Length > 0
            && !def.Choices.Any(c => c.Equals(value, StringComparison.OrdinalIgnoreCase)))
            return Outcome.Choice;
        return Outcome.Carried;
    }

    private static Dictionary<string, string> Copy(AssetRecord rec) => new(rec.Properties, StringComparer.OrdinalIgnoreCase);

    private sealed class KeyValueComparer : IEqualityComparer<(string Key, string Value)>
    {
        public static readonly KeyValueComparer Instance = new();

        public bool Equals((string Key, string Value) x, (string Key, string Value) y) =>
            x.Key.Equals(y.Key, StringComparison.OrdinalIgnoreCase) && x.Value.Equals(y.Value, StringComparison.Ordinal);

        public int GetHashCode((string Key, string Value) k) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(k.Key), StringComparer.Ordinal.GetHashCode(k.Value));
    }
}
