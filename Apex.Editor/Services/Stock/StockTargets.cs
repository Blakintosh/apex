using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Apex.Editor.Services.Stock;

/// <summary>A map or mod a stock asset can be copied into.</summary>
/// <param name="Kind">"usermap" or "mod".</param>
/// <param name="Name">The folder's name.</param>
/// <param name="Argument">What the helper's <c>map</c> parameter takes: the usermap's name, or <c>mods/&lt;mod&gt;</c>.</param>
public sealed record CloneTarget(string Kind, string Name, string Argument)
{
    public string Label => $"{Name}  ·  {(Kind == "mod" ? "mod" : "map")}";

    /// <summary>The key kept in the settings to offer the same target first next time.</summary>
    public string Key => Kind + ":" + Name;
}

/// <summary>Finds the maps and mods under an install: a usermap or a mod with a zone file to link against.</summary>
public static class StockTargets
{
    public static IReadOnlyList<CloneTarget> Enumerate(string? bo3Root, string? preferredKey = null)
    {
        var list = new List<CloneTarget>();
        if (bo3Root is null)
            return list;
        foreach (var dir in Folders(Path.Combine(bo3Root, "usermaps")))
        {
            var name = Path.GetFileName(dir);
            if (File.Exists(Path.Combine(dir, "zone_source", name + ".zone")))
                list.Add(new CloneTarget("usermap", name, name));
        }
        foreach (var dir in Folders(Path.Combine(bo3Root, "mods")))
        {
            var name = Path.GetFileName(dir);
            var zones = Path.Combine(dir, "zone_source");
            if (Directory.Exists(zones) && Directory.EnumerateFiles(zones, "*.zone", SearchOption.TopDirectoryOnly).Any())
                list.Add(new CloneTarget("mod", name, "mods/" + name));
        }
        return list
            .OrderBy(t => t.Key.Equals(preferredKey, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(t => t.Kind == "usermap" ? 0 : 1)
            .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IEnumerable<string> Folders(string dir)
    {
        try
        {
            return Directory.Exists(dir) ? Directory.GetDirectories(dir) : Array.Empty<string>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }
}
