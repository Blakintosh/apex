using System;
using System.Linq;

namespace Apex.Editor.Services.Stock;

/// <summary>The words above a stock asset's fields: where it ships, how widely, and what its dependencies come to.</summary>
public sealed class StockHeader
{
    /// <summary>The zone the breadcrumb names ("zm_levelcommon"); the zone file is <c>Zone + ".ff"</c>.</summary>
    public string Zone { get; init; } = "";

    public string ZoneFile => Zone.Length > 0 ? Zone + ".ff" : "";

    /// <summary>The type in the breadcrumb ("weapon").</summary>
    public string Kind { get; init; } = "";

    /// <summary>"Shipped in 15 zones · always loaded in zombies maps (zm_levelcommon) · 161 dependencies".</summary>
    public string Info { get; init; } = "";

    /// <summary>The line in the banner under the title.</summary>
    public string Banner { get; init; } = "";

    /// <summary>A map loads a zone that defines it: the asset can be used by name without copying it.</summary>
    public bool AlwaysLoaded { get; init; }

    /// <summary>What the asset is called in a sentence ("weapon", "model", "animation").</summary>
    public static string Noun(string kind) => kind switch
    {
        "xmodel" => "model",
        "xanim" => "animation",
        "fx" => "effect",
        "attachmentunique" => "attachment",
        _ => kind,
    };

    /// <param name="useDirectly">Dragon carries stock assets for this install (the helper's <c>use_directly</c> verdict): a map can name any of them.</param>
    public static StockHeader Build(StockOrigin origin, StockDb? db, bool useDirectly = false)
    {
        var zones = origin.Zones.Where(z => !z.StartsWith("en_", StringComparison.OrdinalIgnoreCase)).ToList();
        if (zones.Count == 0)
            zones = origin.Zones.ToList();
        var always = db is null ? new System.Collections.Generic.List<string>() : zones.Where(z => db.AlwaysLoadedZones().Contains(z)).ToList();
        var noun = Noun(origin.Kind);
        var parts = new System.Collections.Generic.List<string>
        {
            zones.Count == 1 ? $"Only shipped in {zones[0]}.ff" : $"Shipped in {zones.Count:N0} zones",
        };
        if (always.Count > 0)
        {
            var ordered = always.OrderBy(z => z.StartsWith("core_", StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenBy(z => z, StringComparer.OrdinalIgnoreCase).ToList();
            var modes = ordered.Select(ModeWords).Distinct().ToList();
            // "every map" already says all of them: no mode to name beside it.
            var where = modes.Contains("every") ? "every map" : string.Join(" and ", modes) + " maps";
            var named = string.Join(", ", ordered.Take(2)) + (ordered.Count > 2 ? $" +{ordered.Count - 2}" : "");
            parts.Add($"always loaded in {where} ({named})");
        }
        if (origin.Deps is { } deps)
        {
            var count = deps.Nodes.Count(n => n.Id != deps.Root && n.Class != StockClass.Embedded);
            parts.Add(count == 1 ? "1 dependency" : $"{count:N0} dependencies");
        }
        var banner = always.Count > 0
            ? $"You can use this {noun} in your map without copying it. To change it, create a local copy. Your copy goes in one of your GDTs and takes precedence over the stock one."
            : useDirectly
            ? $"Use this {noun} in your map by name: Dragon takes it from the game's files when you build, with everything it needs. To change it, create a local copy. Your copy goes in one of your GDTs and takes precedence over the stock one."
            : $"This {noun} ships only in {(zones.Count > 0 ? zones[0] + ".ff" : "a zone")}, which maps don't load on their own, so a map that uses it needs its own copy. Create a local copy to put it in one of your GDTs.";
        return new StockHeader
        {
            Zone = zones.Count > 0 ? zones[0] : "",
            Kind = origin.Kind,
            Info = string.Join(" · ", parts),
            Banner = banner,
            AlwaysLoaded = always.Count > 0 || useDirectly,
        };
    }

    private static string ModeWords(string zone) =>
        zone.StartsWith("zm_", StringComparison.OrdinalIgnoreCase) ? "zombies"
        : zone.StartsWith("mp_", StringComparison.OrdinalIgnoreCase) ? "multiplayer"
        : zone.StartsWith("cp_", StringComparison.OrdinalIgnoreCase) ? "campaign"
        : "every";
}
