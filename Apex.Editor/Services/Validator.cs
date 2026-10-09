using System;
using System.Collections.Generic;
using System.Globalization;
using Apex.Editor.Models;

namespace Apex.Editor.Services;

/// <summary>
/// Schema-driven value validation — the "shift-left" answer to finding out from the linker.
/// Returns null when the value is fine, otherwise a short human-readable problem.
/// </summary>
public static class Validator
{
    /// <param name="files">Also flag a file field whose file is missing (from the background-checked cache, so this
    /// never touches the disk; see <see cref="FieldFiles.MissingProblem"/>).</param>
    /// <param name="options">A choice's options for this asset (the deffile can build them per asset, as image's
    /// compressionMethod does for a diffuse map); the schema's own list when null.</param>
    public static string? Check(PropertyDef def, string value, Func<string, string, bool> assetExists, bool files = true,
        string[]? options = null)
    {
        switch (def.Kind)
        {
            case PropertyKind.Number:
                if (!double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var n))
                    return value.Length == 0 ? null : $"‘{value}’ is not a number";
                if (def.HasRange && (n < def.Min || n > def.Max))
                    return $"{value} is outside {def.Min.ToString("0.###", CultureInfo.InvariantCulture)}–{def.Max.ToString("0.###", CultureInfo.InvariantCulture)}";
                return null;

            case PropertyKind.Toggle:
                return value is "0" or "1" or "" ? null : $"‘{value}’ should be 0 or 1";

            case PropertyKind.Choice:
                options ??= def.Choices;
                if (value.Length == 0 || options.Length == 0)
                    return null;
                if (def.Key == "surfaceType" && value == "<error>")
                    return "Pick another option: <error> means nothing was chosen, and the build rejects it";
                foreach (var c in options)
                    if (string.Equals(c, value, StringComparison.OrdinalIgnoreCase))
                        return null;
                return $"‘{value}’ isn't one of this property's options";

            case PropertyKind.AssetRef:
                if (value.Length == 0)
                    return null;
                return assetExists(def.RefType, value) || ShippedAssets.Contains(def.RefType, value)
                    ? null
                    : $"No {def.RefType} named ‘{value}’";

            case PropertyKind.Text when def.TextEditor == PropertyTextEditor.Lines:
                return LineList.Problem(def, value, assetExists);

            default:
                return files ? FieldFiles.MissingProblem(def, value) : null;
        }
    }

    /// <summary>
    /// How many problems an asset has, by the one rule every count in Apex follows (the editor's Problems view and tab,
    /// the rail, the Explorer's ⚠, is:problems and the status bar): a property the deffile shows for this asset whose
    /// value, its own or else the one it inherits or defaults to, fails <see cref="Check"/> against the options the
    /// deffile offers this asset. A property the deffile hides for the asset's current values is no problem: nobody can
    /// see or fix it there, and APE doesn't save it.
    /// </summary>
    public static int CountProblems(AssetRecord record, Func<string, string, AssetRecord?> resolve, Func<string, string, bool> assetExists)
    {
        var schema = SchemaRegistry.Get(record.Type);
        if (schema is null)
            return 0;
        var parents = ParentValues(record, resolve);
        IReadOnlyDictionary<string, string> effective = record.Properties;
        if (parents is not null)
        {
            var merged = new Dictionary<string, string>(parents, StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in record.Properties)
                merged[key] = value;
            effective = merged;
        }
        var overlay = Gdf.GdfRuntime.EvaluateOverlay(record.Type, record.Name, effective);
        var count = 0;
        foreach (var def in schema.Properties)
        {
            var rule = overlay?.Rules.GetValueOrDefault(def.Key);
            if (rule is not null ? !rule.Visible : !def.DefaultVisible)
                continue;
            var value = effective.TryGetValue(def.Key, out var v) ? v : def.Default;
            if (Check(def, value, assetExists, options: def.Kind == PropertyKind.Choice ? rule?.Choices : null) is not null)
                count++;
        }
        return count;
    }

    /// <summary>What the asset inherits: each key from the nearest ancestor holding it; null without a parent.</summary>
    public static Dictionary<string, string>? ParentValues(AssetRecord record, Func<string, string, AssetRecord?> resolve)
    {
        if (record.Parent is null)
            return null;
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { record.Name };
        for (var cur = record; cur.Parent is { } parentName && seen.Add(parentName);)
        {
            if (resolve(record.Type, parentName) is not { } parent)
                break;
            foreach (var (key, value) in parent.Properties)
                values.TryAdd(key, value);
            cur = parent;
        }
        return values;
    }
}
