using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Apex.Editor.Services.Extensions;

/// <summary>
/// One record of a record list, split into its columns. Fields are separated by commas with nothing escaped, as
/// weapon-tech splits them; values are raw (a backslash is a backslash). A record that is never edited is written
/// back exactly as read (<see cref="RecordCodec.Format"/> is only used for an edited one).
/// </summary>
public sealed class RecordFields
{
    public RecordFields(string[] cells, List<string> extras, bool[] missingPrefix)
    {
        Cells = cells;
        Extras = extras;
        MissingPrefix = missingPrefix;
    }

    /// <summary>Each column's value, without its prefix; empty when the record leaves it out.</summary>
    public string[] Cells { get; }

    /// <summary>Fields past the last column, kept as written.</summary>
    public List<string> Extras { get; }

    /// <summary>A prefixed column whose field didn't start with its prefix (the cell holds the whole field).</summary>
    public bool[] MissingPrefix { get; }
}

/// <summary>
/// Reads and writes record lists: the numbered keys of a block in row order, and each record's columns. Pure: the
/// editor, the table and the tests use it alike.
/// </summary>
public static partial class RecordCodec
{
    /// <summary>
    /// The list's rows in a block's values: (number, key, record), by number, and one number spelled twice by fewer
    /// digits first (wtKick1, wtKick01, wtKick001), as <see cref="Save.ExtensionKeyComparer"/> writes them. Never by
    /// position: a block's dictionary order isn't the file's once an undo has put a key back, so file order can't be a rule.
    /// </summary>
    public static List<(int Number, string Key, string Value)> Rows(IReadOnlyDictionary<string, string> values, ExtensionRecordList list)
    {
        var rows = new List<(int, string, string)>();
        foreach (var (key, value) in values)
            if (list.NumberOf(key) is { } n)
                rows.Add((n, key, value));
        // Keys of one list share the stem, so the longer key has more digits; two keys of one length and number are one key.
        rows.Sort((a, b) => a.Item1 != b.Item1 ? a.Item1.CompareTo(b.Item1) : a.Item2.Length.CompareTo(b.Item2.Length));
        return rows;
    }

    /// <summary>
    /// The rows as one string, each ended by a line break (no rows: empty). A GDT value can't hold a line break, so
    /// this is unambiguous, and an empty record still counts as a row. It is what the editor compares, undoes and
    /// journals the table by.
    /// </summary>
    public static string Join(IEnumerable<string> rows)
    {
        var sb = new StringBuilder();
        foreach (var r in rows)
            sb.Append(r).Append('\n');
        return sb.ToString();
    }

    public static List<string> Split(string joined)
    {
        var rows = new List<string>();
        var start = 0;
        for (var i = 0; i < joined.Length; i++)
            if (joined[i] == '\n')
            {
                rows.Add(joined[start..i]);
                start = i + 1;
            }
        // A string not ended by a break (typed into a filter, or an old value) still keeps its last row.
        if (start < joined.Length)
            rows.Add(joined[start..]);
        return rows;
    }

    /// <summary>The list's keys and values for <paramref name="rows"/>, numbered 1..N in row order.</summary>
    public static Dictionary<string, string> Keys(ExtensionRecordList list, IReadOnlyList<string> rows)
    {
        var keys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < rows.Count; i++)
            keys[list.KeyOf(i + 1)] = rows[i];
        return keys;
    }

    /// <summary>
    /// Makes <paramref name="asset"/>'s block in <paramref name="gdt"/>'s sidecar hold exactly <paramref name="target"/>
    /// of the list's keys (others of the list go), new keys in row order. Returns what changed, for one undo step.
    /// </summary>
    public static List<Models.PropertyChange> Write(Models.GdtFile gdt, string asset, Models.ExtensionTarget x, ExtensionRecordList list,
        IReadOnlyDictionary<string, string> target)
    {
        var sidecar = Models.ExtensionSidecar.Of(gdt);
        var id = x.Id;
        var current = Rows(sidecar.Values(asset, id), list).ToDictionary(r => r.Key, r => r.Value, StringComparer.OrdinalIgnoreCase);
        var changes = new List<Models.PropertyChange>();
        foreach (var (key, value) in target)
        {
            var before = current.GetValueOrDefault(key);
            if (before == value)
                continue;
            sidecar.Set(asset, id, key, value);
            changes.Add(new Models.PropertyChange(key, before, value) { Extension = x });
        }
        foreach (var (key, value) in current)
            if (!target.ContainsKey(key))
            {
                sidecar.Set(asset, id, key, null);
                changes.Add(new Models.PropertyChange(key, value, null) { Extension = x });
            }
        return changes;
    }

    public static RecordFields Parse(ExtensionRecordList list, string record)
    {
        var columns = list.Columns;
        var cells = new string[columns.Count];
        Array.Fill(cells, "");
        var missing = new bool[columns.Count];
        var fields = record.Length == 0 ? new List<string>() : record.Split(',').ToList();

        // Named fields (side:left) can sit anywhere after the fields every record has.
        var required = columns.TakeWhile(c => !c.Named && !c.Optional).Count();
        for (var i = 0; i < columns.Count; i++)
        {
            var column = columns[i];
            if (!column.Named)
                continue;
            for (var f = required; f < fields.Count; f++)
                if (fields[f].StartsWith(column.Prefix, StringComparison.OrdinalIgnoreCase))
                {
                    cells[i] = fields[f][column.Prefix.Length..];
                    fields.RemoveAt(f);
                    break;
                }
        }

        var next = 0;
        for (var i = 0; i < columns.Count && next < fields.Count; i++)
        {
            var column = columns[i];
            if (column.Named)
                continue;
            var field = fields[next++];
            if (column.Prefix.Length > 0 && field.StartsWith(column.Prefix, StringComparison.OrdinalIgnoreCase))
                cells[i] = field[column.Prefix.Length..];
            else
            {
                cells[i] = field;
                missing[i] = column.Prefix.Length > 0 && field.Length > 0;
            }
        }
        return new RecordFields(cells, fields.GetRange(next, fields.Count - next), missing);
    }

    /// <summary>
    /// The record for <paramref name="cells"/>: positional fields in column order (a trailing run of empty optional
    /// ones left out), then the extra fields as they were, then each named field that has a value.
    /// </summary>
    public static string Format(ExtensionRecordList list, IReadOnlyList<string> cells, IReadOnlyList<string>? extras = null)
    {
        var columns = list.Columns;
        var positional = new List<string>();
        var last = -1;
        for (var i = 0; i < columns.Count; i++)
        {
            if (columns[i].Named)
                continue;
            positional.Add(cells[i].Length == 0 && columns[i].Optional ? "" : columns[i].Prefix + cells[i]);
            if (cells[i].Length > 0 || !columns[i].Optional)
                last = positional.Count - 1;
        }
        var parts = positional.GetRange(0, last + 1);
        if (extras is { Count: > 0 })
        {
            // Extras follow the last column, so every column before them is written, empty or not.
            parts = positional;
            parts.AddRange(extras);
        }
        for (var i = 0; i < columns.Count; i++)
            if (columns[i].Named && cells[i].Length > 0)
                parts.Add(columns[i].Prefix + cells[i]);
        return string.Join(",", parts);
    }

    /// <summary>
    /// <paramref name="record"/> with hidden columns filled with their defaults: all of them for a new row
    /// (<paramref name="newRow"/>), else only a required one that is empty (a field the format needs that nobody can see
    /// to fill). A stored or pasted hidden value is kept as written, and a record with nothing to fill is returned as is.
    /// </summary>
    public static string FillHidden(ExtensionRecordList list, string record, bool newRow)
    {
        if (!list.Columns.Any(c => c.Hidden))
            return record;
        var fields = Parse(list, record);
        var cells = fields.Cells.ToArray();
        var changed = false;
        for (var i = 0; i < cells.Length; i++)
        {
            var c = list.Columns[i];
            if (c.Hidden && (newRow ? cells[i] != c.Def.Default : cells[i].Length == 0 && !c.Optional))
            {
                cells[i] = c.Def.Default;
                changed = true;
            }
        }
        return changed ? Format(list, cells, fields.Extras) : record;
    }

    /// <summary>
    /// What is wrong with one record, in column order (empty: nothing). <paramref name="exists"/> checks an anim
    /// column's xanim.
    /// </summary>
    public static List<string> Problems(ExtensionRecordList list, RecordFields record, Func<string, string, bool> exists)
    {
        var problems = new List<string>();
        var columns = list.Columns;
        var cells = record.Cells;
        for (var i = 0; i < columns.Count; i++)
        {
            var c = columns[i];
            var value = cells[i];
            if (value.Trim().Length == 0)
            {
                if (!c.Optional)
                    problems.Add(c.Hidden
                        ? $"{c.Def.Label} is empty. The table doesn't show it; changing any cell of the row writes {c.Def.Default}."
                        : $"{c.Def.Label} is empty.");
                continue;
            }
            if (record.MissingPrefix[i])
                problems.Add($"{c.Def.Label} should start with {c.Prefix}");
            if (c.Def.Kind == Models.PropertyKind.Number && c.Def.IsInteger
                && double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var n) && n != Math.Floor(n))
                problems.Add($"{c.Def.Label}: {value} isn't a whole number.");
            else if (c.Def is { Kind: Models.PropertyKind.Choice, Choices.Length: > 0 }
                     && !c.Def.Choices.Any(x => x.Equals(value.Trim(), StringComparison.OrdinalIgnoreCase)))
                problems.Add($"{c.Def.Label}: ‘{value}’ isn't one of {string.Join(", ", c.Def.Choices)}.");
            else if (Validator.Check(c.Def, value.Trim(), exists, files: false) is { } problem)
                problems.Add($"{c.Def.Label}: {problem}.");
        }

        // weapon-tech reads the trailing numbers in order, skipping empty ones: an empty optional field before a set
        // one shifts that value into the empty one's place.
        var gap = -1;
        for (var i = 0; i < columns.Count; i++)
        {
            if (columns[i].Named || !columns[i].Optional)
                continue;
            if (cells[i].Length == 0)
                gap = gap < 0 ? i : gap;
            else if (gap >= 0)
            {
                problems.Add($"{columns[gap].Def.Label} is empty but {columns[i].Def.Label} is set: fill it in, or clear {columns[i].Def.Label} too.");
                break;
            }
        }

        var extra = record.Extras.Count(e => e.Trim().Length > 0);
        if (extra > 0)
            problems.Add(extra == 1
                ? $"There is a field after {columns.Last(c => !c.Named).Def.Label} that isn't one of the columns; it is kept as written."
                : $"There are {extra} fields after {columns.Last(c => !c.Named).Def.Label} that aren't columns; they are kept as written.");

        foreach (var check in list.Checks)
            if (check.Rule.Evaluate(name => ValueOf(list, record, name)))
                problems.Add(check.Message);
        return problems;
    }

    /// <summary>A column's value as rules read it: what the record says, else what an empty field means.</summary>
    public static string? ValueOf(ExtensionRecordList list, RecordFields record, string name)
    {
        for (var i = 0; i < list.Columns.Count; i++)
            if (list.Columns[i].Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                return record.Cells[i].Trim().Length > 0 ? record.Cells[i].Trim() : list.Columns[i].Def.Default;
        return null;
    }

    /// <summary>For each row, the earlier row it repeats on the list's unique columns, or -1.</summary>
    public static int[] Repeats(ExtensionRecordList list, IReadOnlyList<RecordFields> rows)
    {
        var repeats = new int[rows.Count];
        Array.Fill(repeats, -1);
        if (list.Unique.Count == 0)
            return repeats;
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var r = 0; r < rows.Count; r++)
        {
            var key = string.Join("\0", list.Unique.Select(c => ValueOf(list, rows[r], c.Name) ?? ""));
            if (!seen.TryAdd(key, r))
                repeats[r] = seen[key];
        }
        return repeats;
    }

    /// <summary>
    /// A table cell's value: a stored column's field, or for a combined group the choice its columns match (each empty one
    /// read as its default, numbers by value), else its columns' values as written, which the dropdown shows as custom.
    /// </summary>
    public static string DisplayCell(ExtensionRecordList list, RecordFields record, RecordDisplayColumn display)
    {
        if (display.Combine is not { } combine)
            return record.Cells[display.Column];
        var cells = combine.Columns.Select(i => record.Cells[i].Trim()).ToArray();
        if (cells.All(c => c.Length == 0))
            return "";
        var read = combine.Columns.Select((column, k) => cells[k].Length > 0 ? cells[k] : list.Columns[column].Def.Default).ToArray();
        foreach (var choice in combine.Def.Choices)
            if (choice.Split(',').Zip(read).All(p => Same(p.First, p.Second)))
                return choice;
        return string.Join(",", cells);

        static bool Same(string a, string b) =>
            a.Equals(b, StringComparison.OrdinalIgnoreCase)
            || double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
               && double.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out var y) && x == y;
    }

    /// <summary>
    /// Records from pasted text, one per line (blank lines skipped). A line is a record as stored (<c>1,0,3,93</c>), cells
    /// separated by tabs (a spreadsheet's copy), or a key and its record as a <c>.gdtx</c> or a cfg writes it
    /// (<c>"wtKick1" "1,0,3"</c>, <c>wtKick1 = 1,0,3</c>) when the key is one of the list's.
    /// </summary>
    public static List<string> ParseRows(ExtensionRecordList list, string text)
    {
        var records = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            if (line.Length == 0)
                continue;
            if (KeyedLine().Match(line) is { Success: true } m && list.NumberOf(m.Groups[1].Value) is not null)
                line = m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value.Trim();
            if (line.Contains('\t'))
                line = string.Join(",", line.Split('\t').Select(c => c.Trim()));
            records.Add(line);
        }
        return records;
    }

    // "wtKick1" "1,0,3" (a .gdtx line) or wtKick1 = 1,0,3 (a cfg line).
    [System.Text.RegularExpressions.GeneratedRegex("""^"?([A-Za-z_][A-Za-z0-9_]*)"?\s*(?:"([^"]*)"$|=\s*(.*)$)""")]
    private static partial System.Text.RegularExpressions.Regex KeyedLine();

    /// <summary>"purpose and side": the unique columns' labels, for the repeat message.</summary>
    public static string UniqueLabels(ExtensionRecordList list)
    {
        var labels = list.Unique.Select(c => c.Def.Label.ToLowerInvariant()).ToList();
        return labels.Count <= 1 ? string.Concat(labels) : string.Join(", ", labels.Take(labels.Count - 1)) + " and " + labels[^1];
    }
}
