using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Apex.Editor.Models;

namespace Apex.Editor.Services.Gdf;

/// <summary>Maps the entries recorded by a <see cref="RecordingAsset"/> into an <see cref="AssetSchema"/>.</summary>
internal static class SchemaMapper
{
    private static readonly string[] VectorSuffix = { "X", "Y", "Z", "W" };

    public static AssetSchema Build(string typeName, RecordingAsset host)
    {
        var props = new List<PropertyDef>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Form order: sections as the script first used them, entries within one as registered (or by techset tweak).
        foreach (var e in host.Entries.OrderBy(host.SortKey))
        {
            if (e.Dead) continue;
            if (e.Kind is EntryKind.Label or EntryKind.ButtonGroup) continue;
            if (!e.Save) continue;

            if (e.Kind is EntryKind.Vector2 or EntryKind.Vector3 or EntryKind.Vector4)
            {
                for (int i = 0; i < e.Names.Count; i++)
                {
                    string key = e.Names[i];
                    if (string.IsNullOrEmpty(key) || !seen.Add(key)) continue;
                    string? title = GdfRuntime.CleanTitle(e.Title);
                    string suffix = ComponentLabel(e, i) ?? (i < VectorSuffix.Length ? VectorSuffix[i] : (i + 1).ToString());
                    props.Add(new PropertyDef(key, $"{title ?? Humanize(key)} {suffix}", e.Category, PropertyKind.Number, e.ToolTip ?? "")
                    {
                        VectorKey = e.PrimaryName,
                        VectorTitle = title ?? Humanize(e.PrimaryName),
                        VectorPart = suffix,
                        Default = GdfValue.AsString(i < e.Defaults.Count ? e.Defaults[i] : 0L),
                        Min = e.Min,
                        Max = e.Max,
                        Step = e.StepSet ? e.Step : FloatStep(e),
                        DefaultVisible = e.Visible,
                        Subgroup = e.Subgroup,
                    });
                }
                continue;
            }

            string name = e.PrimaryName;
            if (string.IsNullOrEmpty(name) || !seen.Add(name)) continue;

            var kind = MapKind(e.Kind);
            var def = new PropertyDef(
                name,
                GdfRuntime.CleanTitle(e.Title) ?? Humanize(name),
                e.Category,
                kind,
                e.ToolTip ?? "")
            {
                Default = DefaultString(e),
                DefaultVisible = e.Visible,
                Subgroup = e.Subgroup,
            };

            if (kind == PropertyKind.Number)
            {
                def = def with
                {
                    Min = e.Min,
                    Max = e.Max,
                    Step = e.StepSet ? e.Step : (e.Kind == EntryKind.Int ? 1 : FloatStep(e)),
                    IsInteger = e.Kind == EntryKind.Int,
                };
            }
            else if (kind == PropertyKind.Choice)
            {
                var options = e.OptionsPerAsset ? NoOptions : ParseOptions(e.ComboOptions);
                def = def with { Choices = options.Values, ChoiceLabels = options.Labels };
                // Surface Type starts with "<error>", the deffile's "never chosen": the build rejects it, so it isn't offered.
                // (Other combos use the same word for something real, e.g. a note's type.) An asset that has it still shows
                // it, and the validator says to pick another.
                if (def.Key == "surfaceType" && Array.IndexOf(def.Choices, "<error>") is var bad and >= 0)
                    def = def with
                    {
                        Choices = def.Choices.Where((_, i) => i != bad).ToArray(),
                        ChoiceLabels = def.ChoiceLabels?.Where((_, i) => i != bad).ToArray(),
                    };
            }
            else if (kind == PropertyKind.AssetRef)
            {
                def = def with { RefType = e.AssetType };
            }
            else if (kind == PropertyKind.Text)
            {
                var (fileKind, root) = MapFile(e);
                if (fileKind != PropertyFileKind.None)
                    def = def with { FileKind = fileKind, RelativeRoot = root, FileFilter = e.FileFilter };
                def = MapTextEditor(def, e);
            }

            props.Add(def);
        }

        return new AssetSchema
        {
            TypeName = typeName,
            GdfName = typeName + ".gdf",
            Properties = props,
        };
    }

    /// <summary>
    /// Step for a float entry whose deffile sets none (about 40% of them). A whole unit would make
    /// Ctrl+↑ jump a 0–1 roughness from 0.1 to 1.1, so the step follows the range instead: a
    /// hundredth of its order of magnitude (0–1 → 0.01, 0–100 → 1), never coarser than 1; 0.1
    /// without a range.
    /// </summary>
    private static double FloatStep(RecordedEntry e)
    {
        if (!e.HasRange)
            return 0.1;
        var magnitude = Math.Floor(Math.Log10(e.Max - e.Min));
        return Math.Clamp(Math.Pow(10, magnitude - 2), 0.001, 1);
    }

    private static PropertyKind MapKind(EntryKind kind) => kind switch
    {
        EntryKind.Float or EntryKind.Int => PropertyKind.Number,
        EntryKind.CheckBox => PropertyKind.Toggle,
        EntryKind.Combo => PropertyKind.Choice,
        EntryKind.AssetCombo => PropertyKind.AssetRef,
        _ => PropertyKind.Text,
    };

    /// <summary>
    /// Classifies a file-backed Text entry and resolves its root-relative directory.
    /// Texture/XModel/Path map straight from the deffile factory; Path splits into Anim vs generic
    /// Path by the recorded relative dir ("xanim_export/") or an extension filter mentioning XANIM.
    /// </summary>
    private static (PropertyFileKind, string) MapFile(RecordedEntry e)
    {
        string root = e.RelativePath;
        switch (e.Kind)
        {
            case EntryKind.Texture:
                return (PropertyFileKind.Texture, root);
            case EntryKind.XModel:
                return (PropertyFileKind.Model, root.Length == 0 ? "model_export/" : root);
            case EntryKind.Path:
                bool isAnim = NormalizeDir(root) == "xanim_export"
                    || e.FileFilter.IndexOf("XANIM", StringComparison.OrdinalIgnoreCase) >= 0;
                return isAnim ? (PropertyFileKind.Anim, root) : (PropertyFileKind.Path, root);
            default:
                return (PropertyFileKind.None, "");
        }
    }

    private static string NormalizeDir(string dir) => dir.Trim().TrimEnd('/', '\\');

    /// <summary>
    /// A vector component's label from the deffile's .SetLabels ("Forward", "Right", "Up"), in place of X/Y/Z; null
    /// when it gives none for this component. APE pads labels with spaces for its layout, so they are trimmed.
    /// </summary>
    private static string? ComponentLabel(RecordedEntry e, int index)
    {
        if (e.Labels is not { } labels || index >= labels.Count)
            return null;
        return GdfRuntime.CleanTitle(labels[index]);
    }

    /// <summary>The tailored editor for a Color, FileCombo or BoneCombo entry (a plain text field otherwise).</summary>
    private static PropertyDef MapTextEditor(PropertyDef def, RecordedEntry e) => e.Kind switch
    {
        EntryKind.Color => def with { TextEditor = PropertyTextEditor.Color, ShowAlpha = e.ShowAlpha },
        EntryKind.FileCombo => def with { TextEditor = PropertyTextEditor.FileList, RelativeRoot = e.RelativePath, FileFilter = e.FileFilter },
        EntryKind.BoneCombo => def with { TextEditor = PropertyTextEditor.Bone, ModelKeys = SplitKeys(e.ModelKeys) },
        // Every deffile Text entry is "one per line" except comments, which are prose: those stay one text field.
        EntryKind.Text when !def.Key.Equals("comments", StringComparison.OrdinalIgnoreCase) => def with
        {
            TextEditor = PropertyTextEditor.Lines,
            ModelKeys = def.Key.Equals("hideTags", StringComparison.OrdinalIgnoreCase) ? HideTagModelKeys : def.ModelKeys,
        },
        _ => def,
    };

    /// <summary>
    /// The models whose tags hideTags hides ("name of tags to hide on this model"): a weapon's gun model (its world
    /// model without one), an attachment's first view or world model. The first that holds a value offers its bones.
    /// </summary>
    private static readonly string[] HideTagModelKeys = { "gunModel", "worldModel", "attachViewModel1", "attachWorldModel1" };

    private static string[] SplitKeys(string keys) =>
        keys.Split(new[] { ',', ';', ' ', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string DefaultString(RecordedEntry e)
    {
        // Prefer e.Value — the value established while executing GenerateUI. The factory seeds both
        // e.Value and e.Defaults[0] with the factory default, but chained modifiers
        // (.SetDefaultValue/.SetValue/.SetInt/.SetBool/.SetFloat/.UpdateSavedValue) write ONLY to
        // e.Value. Combo/AssetCombo factories hardcode e.Defaults[0]="" and supply the real default
        // later via .SetDefaultValue, so reading e.Defaults[0] would drop it (exporting "").
        object? def = e.Value ?? (e.Defaults.Count > 0 ? e.Defaults[0] : null);
        if (e.Kind == EntryKind.CheckBox)
            return GdfValue.AsBool(def) ? "1" : "0";
        return GdfValue.AsString(def);
    }

    /// <summary>A combo's stored values and, when the deffile names them ("Best color compression{compressed high color}"),
    /// what APE shows for each; Labels is null when every option is shown as it is stored.</summary>
    internal sealed record Options(string[] Values, string[]? Labels);

    private static readonly Options NoOptions = new(Array.Empty<string>(), null);

    // Combo option strings repeat across every evaluation of a type (GenerateUI re-runs per asset), so
    // each distinct string is split once. The arrays are shared and never mutated.
    private static readonly ConcurrentDictionary<string, Options> Choices = new(StringComparer.Ordinal);

    internal static string[] ParseChoices(string options) => ParseOptions(options).Values;

    internal static Options ParseOptions(string options) =>
        string.IsNullOrWhiteSpace(options) ? NoOptions : Choices.GetOrAdd(options, SplitChoices);

    private static Options SplitChoices(string options)
    {
        var parts = options.Split('|');
        var values = new List<string>(parts.Length);
        var labels = new List<string>(parts.Length);
        var labelled = false;
        foreach (var raw in parts)
        {
            string token = raw.Trim();
            if (token.Length == 0) continue;
            // "Display{value}" — the stored value is the braced text; APE lists the display text.
            int open = token.IndexOf('{');
            if (open >= 0 && token.EndsWith('}'))
            {
                string value = token.Substring(open + 1, token.Length - open - 2).Trim();
                string label = GdfRuntime.CleanTitle(token[..open]) ?? value;
                values.Add(value);
                labels.Add(label);
                labelled |= label != value;
            }
            else
            {
                values.Add(token);
                labels.Add(token);
            }
        }
        return new Options(values.ToArray(), labelled ? labels.ToArray() : null);
    }

    private static string Humanize(string key)
    {
        if (string.IsNullOrEmpty(key)) return key;
        var sb = new StringBuilder(key.Length + 8);
        for (int i = 0; i < key.Length; i++)
        {
            char c = key[i];
            if (i == 0)
            {
                sb.Append(char.ToUpperInvariant(c));
                continue;
            }
            if ((char.IsUpper(c) || char.IsDigit(c)) && !char.IsWhiteSpace(key[i - 1]) &&
                (!char.IsUpper(key[i - 1]) || (char.IsDigit(c) && !char.IsDigit(key[i - 1]))))
                sb.Append(' ');
            else if (c == '_')
            {
                sb.Append(' ');
                continue;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }
}
