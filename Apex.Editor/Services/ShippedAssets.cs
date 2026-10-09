using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace Apex.Editor.Services;

/// <summary>
/// The assets the game already ships (<c>zone_source/all/assetlist/*.csv</c>: "type,name" per line). A reference to one
/// links without any GDT defining it (global_invisible, the $white and $black images), so it is no missing asset.
/// Shipped materials are listed under their techset folder ("mc/global_invisible"); a GDT names them without it.
/// Read once per install on a worker thread; <see cref="Contains"/> waits for that read the first time it is asked.
/// </summary>
public static class ShippedAssets
{
    private static Task<HashSet<string>>? _load;
    private static string? _root;

    /// <summary>Starts reading the lists of <paramref name="bo3Root"/> (null: no install, nothing ships).</summary>
    public static void Use(string? bo3Root)
    {
        if (string.Equals(_root, bo3Root, StringComparison.OrdinalIgnoreCase) && _load is not null)
            return;
        _root = bo3Root;
        _load = bo3Root is null ? null : Task.Run(() => Read(bo3Root));
    }

    /// <summary>True when the game ships a <paramref name="type"/> named <paramref name="name"/>.</summary>
    public static bool Contains(string type, string name)
    {
        if (_load is not { } load || name.Length == 0)
            return false;
        return load.Result.Contains(Key(type, name));
    }

    private static string Key(string type, string name) => $"{type}\0{name}";

    private static HashSet<string> Read(string root)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dir = Path.Combine(root, "zone_source", "all", "assetlist");
        try
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*.csv"))
            {
                foreach (var line in File.ReadLines(file))
                {
                    var comma = line.IndexOf(',');
                    if (comma <= 0)
                        continue;
                    var type = line[..comma].Trim();
                    var name = line[(comma + 1)..].Trim();
                    if (type.Equals("material", StringComparison.OrdinalIgnoreCase) && name.IndexOf('/') is var slash and > 0)
                        name = name[(slash + 1)..];
                    if (name.Length > 0)
                        names.Add(Key(type, name));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // No lists (an install without them, or unreadable): only the index decides what exists.
        }
        return names;
    }
}
