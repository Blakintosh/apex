using System;
using System.Collections.Generic;
using System.Linq;
using Apex.Editor.Models;

namespace Apex.Editor.Services;

/// <summary>
/// A section whose keys are one name for the row and one for the column ("Zombie" × "Head fatal"): what the Matrix
/// view lays out. <see cref="Keys"/> is [row, column]; a cell no key fills is null.
/// </summary>
public sealed record KeyGrid(string Section, IReadOnlyList<string> Rows, IReadOnlyList<string> Columns, string?[,] Keys)
{
    public int Cells => Rows.Count * Columns.Count;

    public int Filled => Keys.Cast<string?>().Count(k => k is not null);
}

/// <summary>
/// Finds the grid a section's labels make, with no hint from the deffile: every label ends in one of a few shared
/// column names (the longest ending shared by at least three labels), and what is left in front of it is the row's.
/// A section is a grid only when nearly all its labels fit one, its cells are mostly there, and the cells are one
/// kind of value; anything else stays a list.
/// </summary>
public static class KeyGridDetector
{
    private const int MinRows = 3, MinColumns = 2, MaxColumns = 16, MinShare = 3, MinCells = 16;
    private const double MinCoverage = 0.9, MinFill = 0.75;

    public static KeyGrid? Detect(string section, IReadOnlyList<PropertyDef> props)
    {
        // A vector's components are already one row; a "Piece 3" / "Piece 4" run is a numbered list, not a grid.
        props = props.Where(p => p.VectorKey is null).ToList();
        if (props.Count < MinCells)
            return null;
        if (props.Select(p => p.Kind).Distinct().Count() != 1)
            return null;
        var words = props.Select(p => p.Label.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToArray();

        KeyGrid? best = null;
        var bestScore = 0.0;
        for (var longest = 1; longest <= 3; longest++)
        {
            var grid = TryDetect(section, props, words, longest, out var score);
            if (grid is not null && score > bestScore + 1e-9)
            {
                best = grid;
                bestScore = score;
            }
        }
        return best;
    }

    private static KeyGrid? TryDetect(string section, IReadOnlyList<PropertyDef> props, string[][] words, int longest, out double score)
    {
        score = 0;
        // How many labels each ending belongs to (once per label), then each label's longest ending shared by enough.
        var share = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var w in words)
            for (var len = 1; len <= Math.Min(longest, w.Length - 1); len++)
                share[string.Join(' ', w[^len..])] = share.GetValueOrDefault(string.Join(' ', w[^len..])) + 1;

        var rowNames = new List<string>();
        var columnNames = new List<string>();
        var cells = new List<(int Row, int Column, string Key)>();
        var seen = new HashSet<(int, int)>();
        for (var i = 0; i < props.Count; i++)
        {
            var w = words[i];
            string? column = null;
            for (var len = Math.Min(longest, w.Length - 1); len >= 1 && column is null; len--)
            {
                var ending = string.Join(' ', w[^len..]);
                if (share[ending] >= MinShare)
                    column = ending;
            }
            if (column is null)
                continue;
            var row = string.Join(' ', w[..^column.Split(' ').Length]);
            var r = IndexOf(rowNames, row);
            var c = IndexOf(columnNames, column);
            if (!seen.Add((r, c)))
                return null;
            cells.Add((r, c, props[i].Key));
        }
        if (rowNames.Count < MinRows || columnNames.Count < MinColumns || columnNames.Count > MaxColumns
            || rowNames.Count * columnNames.Count < MinCells || columnNames.Any(c => c.All(ch => char.IsDigit(ch) || ch == ' ')))
            return null;
        var coverage = (double)cells.Count / props.Count;
        var fill = (double)cells.Count / (rowNames.Count * columnNames.Count);
        if (coverage < MinCoverage || fill < MinFill)
            return null;

        var keys = new string?[rowNames.Count, columnNames.Count];
        foreach (var (r, c, key) in cells)
            keys[r, c] = key;
        score = coverage * fill;
        return new KeyGrid(section, rowNames, columnNames, keys);
    }

    private static int IndexOf(List<string> names, string name)
    {
        var at = names.FindIndex(n => n.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (at >= 0)
            return at;
        names.Add(name);
        return names.Count - 1;
    }
}
