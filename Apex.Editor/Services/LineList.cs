using System;
using System.Collections.Generic;
using System.Text;
using Apex.Editor.Models;

namespace Apex.Editor.Services;

/// <summary>What one line of a line-list field holds.</summary>
public enum LineShape
{
    /// <summary>Free text (comments, mods, bone controllers).</summary>
    Plain,

    /// <summary>A material name (xmodel <c>materials</c>).</summary>
    Material,

    /// <summary>"surfaceMaterial replacement" (xmodel <c>skinOverride</c>); the replacement may be <c>nodraw</c>.</summary>
    SkinOverride,

    /// <summary>A bone (tag) of the asset's model (weapon <c>hideTags</c>).</summary>
    Bone,
}

/// <summary>
/// The deffile's Text entries ("one per line"): lines separated by the literal text <c>\r\n</c>, as APE's multi-line box
/// writes them into the GDT (values are raw; the backslashes are characters, not a newline).
///
/// Writing back keeps the value's own shape. Blank lines aren't items, so an edit drops blank lines between items, but
/// the run of separators after the last item is kept exactly: 96% of skinOverride values and every materials value
/// end in one separator, and 82 hideTags values carry 32 of them — padding left over from the fixed 32-line tag list
/// older tools wrote (55 values are that padding alone, and 29 more are tags followed by it, so it is how those
/// weapons were saved, not something the game reads: blank names hide nothing). Keeping the run means an edited value
/// differs from the original only in the items the user touched. A value that starts empty takes the style most of the
/// install uses for that field: a closing separator for skinOverride and materials, none for the rest.
/// </summary>
public static class LineList
{
    public const string Separator = @"\r\n";

    public static LineShape ShapeOf(PropertyDef def) => def.Key switch
    {
        _ when def.Key.Equals("materials", StringComparison.OrdinalIgnoreCase) => LineShape.Material,
        _ when def.Key.Equals("skinOverride", StringComparison.OrdinalIgnoreCase) => LineShape.SkinOverride,
        _ when def.ModelKeys.Length > 0 => LineShape.Bone,
        _ => LineShape.Plain,
    };

    /// <summary>The value's items: its non-blank lines, each exactly as written.</summary>
    public static List<string> Items(string value)
    {
        var items = new List<string>();
        foreach (var line in value.Split(Separator))
            if (line.Trim().Length > 0)
                items.Add(line);
        return items;
    }

    /// <summary>How many separators close the value (all of them, for a value that is only blank lines).</summary>
    public static int TrailingSeparators(string value)
    {
        var count = 0;
        var end = value.Length;
        while (end >= Separator.Length && string.CompareOrdinal(value, end - Separator.Length, Separator, 0, Separator.Length) == 0)
        {
            count++;
            end -= Separator.Length;
        }
        return count;
    }

    /// <summary>
    /// <paramref name="items"/> written in the shape of <paramref name="previous"/> (the value they replace): joined by
    /// the separator and closed by the same run of separators. With no items left, only a padded value (two or more
    /// separators) keeps its padding; anything else becomes empty.
    /// </summary>
    public static string Compose(IReadOnlyList<string> items, string previous, LineShape shape)
    {
        var trailing = previous.Length == 0
            ? (shape is LineShape.Material or LineShape.SkinOverride ? 1 : 0)
            : TrailingSeparators(previous);
        if (items.Count == 0)
            return trailing >= 2 ? Repeat(trailing) : "";
        var sb = new StringBuilder();
        for (var i = 0; i < items.Count; i++)
        {
            if (i > 0)
                sb.Append(Separator);
            sb.Append(items[i]);
        }
        sb.Append(Repeat(trailing));
        return sb.ToString();
    }

    private static string Repeat(int count)
    {
        var sb = new StringBuilder(count * Separator.Length);
        for (var i = 0; i < count; i++)
            sb.Append(Separator);
        return sb.ToString();
    }

    /// <summary>A skinOverride line split into its surface material and its replacement (either may be empty).</summary>
    public static (string Surface, string Replacement) SplitPair(string line)
    {
        var trimmed = line.Trim();
        var at = trimmed.IndexOfAny(new[] { ' ', '\t' });
        return at < 0 ? (trimmed, "") : (trimmed[..at], trimmed[(at + 1)..].Trim());
    }

    public static string JoinPair(string surface, string replacement) =>
        replacement.Length == 0 ? surface.Trim() : $"{surface.Trim()} {replacement.Trim()}";

    /// <summary>The replacement that hides a surface rather than naming a material.</summary>
    public const string NoDraw = "nodraw";

    /// <summary>The material names a line references (for the ⚠): the line itself, or a skinOverride's replacement.</summary>
    public static string? MaterialOf(string line, LineShape shape) => shape switch
    {
        LineShape.Material => line.Trim(),
        LineShape.SkinOverride when SplitPair(line).Replacement is { Length: > 0 } r
            && !r.Equals(NoDraw, StringComparison.OrdinalIgnoreCase) => r,
        _ => null,
    };

    /// <summary>The field's problem: the materials its lines name that aren't in the index and don't ship with the game.</summary>
    public static string? Problem(PropertyDef def, string value, Func<string, string, bool> assetExists)
    {
        var shape = ShapeOf(def);
        if (shape is not (LineShape.Material or LineShape.SkinOverride) || value.Length == 0)
            return null;
        List<string>? missing = null;
        foreach (var line in Items(value))
            if (MaterialOf(line, shape) is { Length: > 0 } name && !assetExists("material", name) && !ShippedAssets.Contains("material", name))
                (missing ??= new List<string>()).Add(name);
        return missing switch
        {
            null => null,
            [var one] => $"No material named ‘{one}’",
            _ => $"{missing.Count} materials not found: {string.Join(", ", missing)}",
        };
    }

    /// <summary>"3 tags": the one-line summary a table cell shows.</summary>
    public static string Summary(LineShape shape, int count)
    {
        var (one, many) = shape switch
        {
            LineShape.Material => ("material", "materials"),
            LineShape.SkinOverride => ("override", "overrides"),
            LineShape.Bone => ("tag", "tags"),
            _ => ("line", "lines"),
        };
        return count == 0 ? $"No {many}" : $"{count} {(count == 1 ? one : many)}";
    }
}
