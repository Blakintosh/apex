using System;
using System.Collections.Generic;
using System.Linq;

namespace Apex.Editor.Models;

public enum PropertyKind
{
    Text,
    Number,
    Toggle,
    Choice,
    AssetRef,
}

/// <summary>Classifies a Text property that actually references a file on disk (preview pane).</summary>
public enum PropertyFileKind
{
    None,
    Texture,
    Model,
    Anim,
    Path,
}

/// <summary>Definition of a single editable property on an asset type.</summary>
public sealed record PropertyDef(string Key, string Label, string Category, PropertyKind Kind, string Description)
{
    public string Default { get; init; } = "";

    /// <summary>The deffile's sub-section under <see cref="Category"/> ("LOD 1" for BeginCategory("LODs.LOD 1")); empty when it names none.</summary>
    public string Subgroup { get; init; } = "";
    public double Min { get; init; }
    public double Max { get; init; }
    public double Step { get; init; } = 1;

    /// <summary>A whole-number field (the deffile's int entries): stepping never leaves a fraction.</summary>
    public bool IsInteger { get; init; }
    public string[] Choices { get; init; } = Array.Empty<string>();

    /// <summary>What each of <see cref="Choices"/> is shown as (an extension's <c>{ value, label }</c> choices); null when the values are shown as they are.</summary>
    public string[]? ChoiceLabels { get; init; }

    /// <summary>A stored choice value as the dropdown shows it: its label, or "custom: x" for a value a labelled list lacks.</summary>
    public string ChoiceLabel(string value)
    {
        if (ChoiceLabels is null)
            return value;
        for (var i = 0; i < Choices.Length; i++)
            if (Choices[i].Equals(value, StringComparison.OrdinalIgnoreCase))
                return ChoiceLabels[i];
        return value.Length == 0 ? "" : "custom: " + value;
    }

    public string RefType { get; init; } = "";

    /// <summary>
    /// The deffile vector entry (AddEntry_Vector2/3/4) this number is a component of, by its first component's key; null
    /// for a property of its own. The editor shows a vector's components on one row (<see cref="VectorTitle"/>), each
    /// labelled by <see cref="VectorPart"/>.
    /// </summary>
    public string? VectorKey { get; init; }

    /// <summary>The vector's own title ("Position Offset (x,y)").</summary>
    public string? VectorTitle { get; init; }

    /// <summary>This component's name within its vector ("X", or the deffile's .SetLabels: "Forward").</summary>
    public string? VectorPart { get; init; }

    /// <summary>
    /// When this Text property references a file (texture/model/anim/generic path), which kind — else None.
    /// Drives the preview pane; the editor UI still treats these as PropertyKind.Text.
    /// </summary>
    public PropertyFileKind FileKind { get; init; } = PropertyFileKind.None;

    /// <summary>Root-relative directory the file resolves under (e.g. "model_export/"); empty if unspecified.</summary>
    public string RelativeRoot { get; init; } = "";

    /// <summary>
    /// Whether the deffile shows this entry for a default-state asset (.Show(false) entries are
    /// internal/plumbing fields APE hides). Per-asset evaluation (SchemaOverlay) refines this.
    /// </summary>
    public bool DefaultVisible { get; init; } = true;

    public bool HasRange => Kind == PropertyKind.Number && Max > Min;

    // ── Tailored text editors (the deffile's Color, FileCombo and BoneCombo entries) ──

    /// <summary>Which tailored editor a Text property gets; Plain for an ordinary text field.</summary>
    public PropertyTextEditor TextEditor { get; init; } = PropertyTextEditor.Plain;

    /// <summary>The deffile's file-type filter (".SetFileFilter", a FileCombo's file types), e.g. "Collision Map Files (*.map)".</summary>
    public string FileFilter { get; init; } = "";

    /// <summary>A Color entry offers alpha (".SetShowAlpha"; on unless the deffile turns it off).</summary>
    public bool ShowAlpha { get; init; } = true;

    /// <summary>A BoneCombo's model keys: the properties naming the xmodel whose bones it offers.</summary>
    public string[] ModelKeys { get; init; } = Array.Empty<string>();

    /// <summary>
    /// The extension whose field this is (its value lives in the GDT's <c>.gdtx</c>, not the GDT); empty for a deffile
    /// property.
    /// </summary>
    public string Extension { get; init; } = "";
}

/// <summary>The tailored editor a Text property gets (files are told apart by <see cref="PropertyDef.FileKind"/>).</summary>
public enum PropertyTextEditor
{
    Plain,

    /// <summary>"r g b a" floats with a swatch and a picker.</summary>
    Color,

    /// <summary>A file name picked from the files in <see cref="PropertyDef.RelativeRoot"/>.</summary>
    FileList,

    /// <summary>A bone (tag) name of the model named by <see cref="PropertyDef.ModelKeys"/>.</summary>
    Bone,

    /// <summary>One item per line (the deffile's Text entries), edited as a list; see <c>Services/LineList</c>.</summary>
    Lines,
}

/// <summary>Editing schema for an asset type — drives the property editor UI.</summary>
public sealed class AssetSchema
{
    public required string TypeName { get; init; }
    public required string GdfName { get; init; }
    public required IReadOnlyList<PropertyDef> Properties { get; init; }

    private Dictionary<string, PropertyDef>? _byKey;

    public PropertyDef? Find(string key)
    {
        _byKey ??= Properties.ToDictionary(p => p.Key, StringComparer.OrdinalIgnoreCase);
        return _byKey.GetValueOrDefault(key);
    }
}
