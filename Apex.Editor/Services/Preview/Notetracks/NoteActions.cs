using System;
using System.Collections.Generic;
using System.Linq;

namespace Apex.Editor.Services.Preview.Notetracks;

/// <summary>
/// A note's actions sorted into groups for the action picker, read from the action names xanim.awi gives its combo
/// ("Play Fx", "Clip Drop", "Call Client Script Function"…), so an action a newer deffile adds lands in the group its
/// name says (or Other) without a list to keep up to date.
/// </summary>
public static class NoteActions
{
    /// <summary>The groups in the order the picker lists them.</summary>
    public static readonly IReadOnlyList<string> GroupOrder = new[]
    {
        "Sound", "FX", "Exploder", "Weapon", "Clip", "Camera", "Model", "Notify", "Script", "Graphic content", "Other",
    };

    /// <summary>The group <paramref name="action"/>'s name puts it in; null for None (the picker lists it on its own).</summary>
    public static string? GroupOf(string action)
    {
        if (action.Length == 0 || action.Equals("None", StringComparison.OrdinalIgnoreCase))
            return null;
        bool Has(string word) => action.Contains(word, StringComparison.OrdinalIgnoreCase);
        if (Has("Graphic Content"))
            return "Graphic content";
        if (Has("Script"))
            return "Script";
        if (Has("Sound") || action.Equals("Vox", StringComparison.OrdinalIgnoreCase))
            return "Sound";
        if (Has("Exploder"))
            return "Exploder";
        if (Has("Fx") || Has("Light"))
            return "FX";
        if (Has("Clip"))
            return "Clip";
        if (Has("Weapon") || Has("Offhand") || Has("Firing") || Has("Rumble"))
            return "Weapon";
        if (Has("Timescale") || Has("FOV") || Has("Focal") || Has("Blur") || Has("Blackscreen"))
            return "Camera";
        if (Has("Bone") || Has("Model") || Has("Shader"))
            return "Model";
        if (Has("Notify"))
            return "Notify";
        return "Other";
    }

    /// <summary>
    /// <paramref name="actions"/> as the picker lists them: None first (on its own), then each group in
    /// <see cref="GroupOrder"/>, each keeping the deffile's order. Only actions whose name contains every word of
    /// <paramref name="filter"/> (or whose group name does) are kept; groups left empty are dropped.
    /// </summary>
    public static IReadOnlyList<(string? Group, IReadOnlyList<string> Actions)> Grouped(IEnumerable<string> actions, string? filter = null)
    {
        var words = (filter ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        bool Keeps(string action, string? group) => words.All(w =>
            action.Contains(w, StringComparison.OrdinalIgnoreCase) || (group?.Contains(w, StringComparison.OrdinalIgnoreCase) ?? false));

        var byGroup = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var none = new List<string>();
        foreach (var action in actions.Distinct(StringComparer.Ordinal))
        {
            var group = GroupOf(action);
            if (!Keeps(action, group))
                continue;
            if (group is null)
                none.Add(action);
            else
            {
                if (!byGroup.TryGetValue(group, out var list))
                    byGroup[group] = list = new List<string>();
                list.Add(action);
            }
        }
        var result = new List<(string?, IReadOnlyList<string>)>();
        if (none.Count > 0)
            result.Add((null, none));
        foreach (var group in GroupOrder)
            if (byGroup.TryGetValue(group, out var list))
                result.Add((group, list));
        return result;
    }
}
