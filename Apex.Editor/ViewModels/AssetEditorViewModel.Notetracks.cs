namespace Apex.Editor.ViewModels;

public sealed partial class AssetEditorViewModel
{
    /// <summary>The type's deffile ran for this asset: rows hidden by its rules are hidden for a reason.</summary>
    public bool HasDisplayRules => _overlay is not null;

    /// <summary>
    /// The title this asset's deffile run gave <paramref name="key"/> for its current values (an xanim note's first
    /// parameter is "Sound Alias" under Sound, "Rumble" under Rumble), cleaned of the deffile's colons and padding; null
    /// when the run gave it none or the type has no deffile program.
    /// </summary>
    public string? EntryTitle(string key) =>
        _overlay?.Layout is { } layout && layout.TryGetValue(key, out var place) && place.Title is { } title
        && title.Trim().TrimEnd(':').Trim() is { Length: > 0 } clean
            ? clean
            : null;
}
