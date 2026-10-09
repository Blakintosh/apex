using System.IO;
using Apex.Editor.Models;

namespace Apex.Editor.Services;

/// <summary>Renders an asset back to GDT source text (Radiant/APE-compatible shape).</summary>
public static class GdtWriter
{
    public static void Write(AssetRecord asset, TextWriter writer)
    {
        writer.Write("{\n\t");
        WriteQuoted(writer, asset.Name);
        if (asset.Parent is null)
        {
            writer.Write(" ( ");
            WriteQuoted(writer, SchemaRegistry.GdfName(asset.Type));
            writer.Write(" )\n");
        }
        else
        {
            writer.Write(" [ ");
            WriteQuoted(writer, asset.Parent);
            writer.Write(" ]\n");
        }
        writer.Write("\t{\n");

        var schema = SchemaRegistry.Get(asset.Type);
        if (schema is not null)
        {
            // Schema order keeps diffs stable and matches the editor layout.
            foreach (var def in schema.Properties)
                if (asset.Properties.TryGetValue(def.Key, out var value))
                    WriteProperty(writer, def.Key, value);
            foreach (var (key, value) in asset.Properties)
                if (schema.Find(key) is null)
                    WriteProperty(writer, key, value);
        }
        else
        {
            foreach (var (key, value) in asset.Properties)
                WriteProperty(writer, key, value);
        }

        writer.Write("\t}\n}\n");
    }

    private static void WriteProperty(TextWriter writer, string key, string value)
    {
        writer.Write("\t\t");
        WriteQuoted(writer, key);
        writer.Write(' ');
        WriteQuoted(writer, value);
        writer.Write('\n');
    }

    /// <summary>
    /// Writes <paramref name="s"/> quoted and raw, the inverse of <c>GdtParser</c> (values have no escapes). A GDT
    /// value cannot hold a quote or a line break: line breaks become the literal <c>\r\n</c> APE writes between
    /// skinOverride lines, and quotes become apostrophes.
    /// </summary>
    private static void WriteQuoted(TextWriter writer, string s)
    {
        writer.Write('"');
        for (int i = 0; i < s.Length; i++)
        {
            switch (s[i])
            {
                case '\r' when i + 1 < s.Length && s[i + 1] == '\n':
                    break;
                case '\r' or '\n': writer.Write(@"\r\n"); break;
                case '"': writer.Write('\''); break;
                default: writer.Write(s[i]); break;
            }
        }
        writer.Write('"');
    }
}
