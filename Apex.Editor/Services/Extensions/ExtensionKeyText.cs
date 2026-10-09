using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Apex.Editor.Services.Extensions;

/// <summary>
/// A module's or extension's sentence in the words of the form: each key token in it (<c>wtFireTimeMs</c>, a list key
/// <c>wtKick#</c> or <c>wtKick3</c>, a stem <c>wtKick*</c>) is replaced by the label the form shows for the field or table
/// that owns it, from the manifest, in quotes; anything else is left as written. Generic: Apex knows no extension's keys.
/// <list type="bullet">
/// <item>a field's key: that field's label;</item>
/// <item>a list's key, pattern or numbered key: the table's label ("Kick sets", "Kick sets row 3");</item>
/// <item>a stem <c>X*</c>: the table whose stem is X, else the field whose key is X, else, when every key starting with X
/// is in one section, the words their labels share ("Spring: gun, hip" and "Spring: gun, ADS": "Spring: gun"), else that
/// section's title. A stem matching keys in several sections, or none, is left as written.</item>
/// </list>
/// </summary>
public static partial class ExtensionKeyText
{
    [GeneratedRegex(@"[A-Za-z_][A-Za-z0-9_]*[#*]?")]
    private static partial Regex Token();

    /// <summary><paramref name="text"/> with the manifest's key tokens replaced by labels (the same text when there were none).</summary>
    public static string ToLabels(string text, ExtensionManifest manifest)
    {
        if (string.IsNullOrEmpty(text))
            return text;
        StringBuilder? sb = null;
        var at = 0;
        foreach (Match m in Token().Matches(text))
        {
            if (LabelOf(m.Value, manifest) is not { } label)
                continue;
            sb ??= new StringBuilder(text.Length);
            // Quoted, as the app names a label elsewhere (“Used by”): a label's own colons and commas then read as its own.
            sb.Append(text, at, m.Index - at).Append('“').Append(label).Append('”');
            at = m.Index + m.Length;
        }
        return sb is null ? text : sb.Append(text, at, text.Length - at).ToString();
    }

    /// <summary>The label for one token, or null when it names nothing in the manifest.</summary>
    public static string? LabelOf(string token, ExtensionManifest manifest)
    {
        var fields = manifest.Sections.SelectMany(s => s.Fields.Select(f => (Section: s, f.Def.Key, f.Def.Label))).ToList();
        var lists = manifest.Sections.SelectMany(s => s.Records.Select(r => (Section: s, r.Base, r.Def.Key, r.Def.Label, List: r))).ToList();
        if (token.EndsWith('*'))
        {
            var stem = token[..^1];
            if (stem.Length == 0)
                return null;
            foreach (var l in lists)
                if (l.Base.Equals(stem, StringComparison.OrdinalIgnoreCase))
                    return l.Label;
            foreach (var f in fields)
                if (f.Key.Equals(stem, StringComparison.OrdinalIgnoreCase))
                    return f.Label;
            var owners = fields.Where(f => f.Key.StartsWith(stem, StringComparison.OrdinalIgnoreCase)).Select(f => (f.Section, f.Label))
                .Concat(lists.Where(l => l.Base.StartsWith(stem, StringComparison.OrdinalIgnoreCase)).Select(l => (l.Section, l.Label))).ToList();
            if (owners.Count == 0 || owners.Any(o => !ReferenceEquals(o.Section, owners[0].Section)))
                return null;
            if (owners.Count == 1)
                return owners[0].Label;
            return SharedWords(owners.Select(o => o.Label).ToList()) ?? owners[0].Section.Title;
        }
        foreach (var f in fields)
            if (f.Key.Equals(token, StringComparison.OrdinalIgnoreCase))
                return f.Label;
        foreach (var l in lists)
        {
            if (l.Key.Equals(token, StringComparison.OrdinalIgnoreCase))
                return l.Label;
            if (l.List.NumberOf(token) is { } n)
                return $"{l.Label} row {n}";
        }
        return null;
    }

    /// <summary>The whole words every label starts with, without trailing punctuation; null when they share none.</summary>
    private static string? SharedWords(IReadOnlyList<string> labels)
    {
        var first = labels[0];
        var length = first.Length;
        foreach (var l in labels.Skip(1))
        {
            var n = 0;
            while (n < length && n < l.Length && char.ToLowerInvariant(l[n]) == char.ToLowerInvariant(first[n]))
                n++;
            length = n;
        }
        // Back to a word's end: "Curve 0: hold" and "Curve 1: kick" share "Curve ", not "Curve 0".
        if (labels.Any(l => l.Length > length && char.IsLetterOrDigit(l[length])) && length > 0 && char.IsLetterOrDigit(first[length - 1]))
            while (length > 0 && char.IsLetterOrDigit(first[length - 1]))
                length--;
        var shared = first[..length].TrimEnd(' ', ',', ':', ';', '-', '(', '/');
        return shared.Length == 0 ? null : shared;
    }
}
