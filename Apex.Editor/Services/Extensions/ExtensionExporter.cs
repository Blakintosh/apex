using System;
using System.Collections.Generic;
using System.Text;

namespace Apex.Editor.Services.Extensions;

/// <summary>
/// An asset's extension values as its manifest's <c>export</c> writes them, for the clipboard. <c>ini-section</c>: the
/// header (<c>{asset}</c> is the asset's name), then <c>key = value</c> per key that has a value, the asset's own or
/// inherited (what the asset's block would read in a tool that follows parents, so a hand copy matches it), in the
/// form's order (<see cref="ExtensionSection.Items"/>), a record list as its numbered keys 1..N in row order. Keys with
/// only a default are left out, and so is the extension's switch: the section itself says the asset has it on. Values
/// are raw, as the <c>.gdtx</c> holds them.
/// </summary>
public static class ExtensionExporter
{
    /// <param name="valueOf">A flat key's value, own else inherited; null when only its default applies.</param>
    /// <param name="rowsOf">A list's rows joined (<see cref="RecordCodec.Join"/>), own else inherited; null for none.</param>
    public static string Write(ExtensionExport export, string asset, ExtensionSchema schema, string? enabledBy,
        Func<string, string?> valueOf, Func<ExtensionRecordList, string?> rowsOf)
    {
        var lines = new List<string> { export.Header.Replace("{asset}", asset, StringComparison.Ordinal) };
        foreach (var section in schema.Sections)
            foreach (var (field, list) in section.Items())
            {
                if (field is not null)
                {
                    if (!field.Def.Key.Equals(enabledBy, StringComparison.OrdinalIgnoreCase) && valueOf(field.Def.Key) is { Length: > 0 } value)
                        lines.Add($"{field.Def.Key} = {value}");
                }
                else if (rowsOf(list!) is { } rows)
                {
                    var records = RecordCodec.Split(rows);
                    for (var i = 0; i < records.Count; i++)
                        lines.Add($"{list!.KeyOf(i + 1)} = {records[i]}");
                }
            }
        var sb = new StringBuilder();
        foreach (var line in lines)
            sb.Append(line).Append("\r\n");
        return sb.ToString();
    }
}
