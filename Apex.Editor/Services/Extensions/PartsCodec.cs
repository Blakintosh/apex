using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Apex.Editor.Models;

namespace Apex.Editor.Services.Extensions;

/// <summary>
/// A value made of named parts joined by commas (a field's <c>parts</c>): one stored value, edited a part at a time.
/// Only the part edited is written again; every other field keeps its text byte for byte (spaces, an empty field, a
/// trailing comma, values past the last part). Pure: the editor, the Explorer's problem count and the tests use it alike.
/// </summary>
public static class PartsCodec
{
    /// <summary>The value's fields as written (no value: none).</summary>
    public static string[] Split(string value) => value.Length == 0 ? Array.Empty<string>() : value.Split(',');

    /// <summary>Part <paramref name="index"/> as its editor shows it: trimmed (a switch reads " 1" as on), empty when the value stops before it.</summary>
    public static string Cell(string[] fields, int index) => index < fields.Length ? fields[index].Trim() : "";

    /// <summary>
    /// <paramref name="value"/> with part <paramref name="index"/> set to <paramref name="part"/>. Parts the value stopped
    /// short of are written as their defaults (else empty); a part emptied with nothing set after it ends the value there.
    /// </summary>
    public static string Set(string value, int index, string part, IReadOnlyList<PropertyDef> parts)
    {
        var fields = Split(value).ToList();
        if (index >= fields.Count && part.Length == 0)
            return value;
        while (fields.Count <= index)
            fields.Add(fields.Count < parts.Count ? parts[fields.Count].Default : "");
        // Unchanged but for spaces: the field keeps its spelling.
        if (fields[index].Trim() == part)
            return value;
        fields[index] = part;
        if (part.Length == 0 && fields.Skip(index + 1).All(f => f.Trim().Length == 0))
            fields.RemoveRange(index, fields.Count - index);
        return string.Join(",", fields);
    }

    /// <summary>
    /// What is wrong with a value, each with the part it is about (-1: the value as a whole). An empty value is the field
    /// left unset and has nothing wrong; otherwise a part with no default may not be missing or empty.
    /// </summary>
    public static List<(int Part, string Message)> Problems(IReadOnlyList<PropertyDef> parts, string value, Func<string, string, bool> exists)
    {
        var problems = new List<(int, string)>();
        if (value.Length == 0)
            return problems;
        var fields = Split(value);
        for (var i = 0; i < parts.Count; i++)
        {
            var def = parts[i];
            var cell = Cell(fields, i);
            if (cell.Length == 0)
            {
                if (def.Default.Length == 0)
                    problems.Add((i, $"{def.Label} is empty."));
                continue;
            }
            if (def.Kind == PropertyKind.Number && def.IsInteger
                && double.TryParse(cell, NumberStyles.Any, CultureInfo.InvariantCulture, out var n) && n != Math.Floor(n))
                problems.Add((i, $"{def.Label}: {cell} isn't a whole number."));
            else if (Validator.Check(def, cell, exists, files: false) is { } problem)
                problems.Add((i, $"{def.Label}: {problem}."));
        }
        var extra = fields.Skip(parts.Count).Count(f => f.Trim().Length > 0);
        if (extra > 0)
            problems.Add((-1, extra == 1
                ? $"There is a value after {parts[^1].Label} that isn't one of its parts; it is kept as written."
                : $"There are {extra} values after {parts[^1].Label} that aren't its parts; they are kept as written."));
        return problems;
    }

    /// <summary>The parts that differ between two values, by label ("Stiffness", "Stiffness and Max climb", "3 parts").</summary>
    public static string? Changed(IReadOnlyList<PropertyDef> parts, string before, string after)
    {
        var a = Split(before);
        var b = Split(after);
        var changed = new List<string>();
        for (var i = 0; i < Math.Max(parts.Count, Math.Max(a.Length, b.Length)); i++)
            if (Cell(a, i) != Cell(b, i))
                changed.Add(i < parts.Count ? parts[i].Label : $"value {i + 1}");
        return changed.Count switch
        {
            0 => null,
            1 => changed[0],
            2 => $"{changed[0]} and {changed[1]}",
            _ => $"{changed.Count} parts",
        };
    }
}
