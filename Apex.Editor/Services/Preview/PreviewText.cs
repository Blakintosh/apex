using System;

namespace Apex.Editor.Services.Preview;

/// <summary>Text helpers for the lines drawn on a preview.</summary>
public static class PreviewText
{
    /// <summary>
    /// A file or asset name shortened in the middle ("vm_ar_an94_r…oad_empty.xanim_bin"), as tab titles are: names in one
    /// family share their start and differ at the end, so cutting the tail would hide the part that tells them apart.
    /// </summary>
    /// <summary>A stored path as a message shows it: a GDT doubles every backslash, which says nothing to the reader.</summary>
    public static string Path(string stored) => stored.Replace("\\\\", "\\");

    public static string MiddleTrim(string name, int maxChars)
    {
        if (name.Length <= maxChars || maxChars < 3)
            return name;
        var tail = (maxChars - 1) / 2;
        var head = maxChars - 1 - tail;
        return string.Concat(name.AsSpan(0, head), "…", name.AsSpan(name.Length - tail));
    }
}
