using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Apex.Render.Data.Techsets;

namespace Apex.Editor.Services.Gdf;

/// <summary>
/// The install's ToolsGfx techsetdefs as material.awi's host calls see them (material categories and types, and the
/// tweaks that decide which material fields show). Configured with the deffiles; null without an install, when the
/// host calls answer as if there were no techsetdefs.
/// </summary>
public static class Techsetdefs
{
    private static volatile TechsetdefLibrary? _library;
    private static readonly ConditionalWeakTable<TechsetdefLibrary, IReadOnlyList<KeyValuePair<string, TechsetdefTweak>>> Unions = new();

    public static TechsetdefLibrary? Library => _library;

    /// <summary>
    /// Points the host calls at the techsetdefs beside <paramref name="deffilesDir"/>
    /// (<c>share\raw\techsetdefs_stable_toolsgfx</c>), freshly indexed so a reload sees edited techsetdefs, and starts
    /// reading them off the calling thread. No techsetdefs (or no deffiles): the host calls answer as if there were none.
    /// </summary>
    /// <summary>
    /// The type a new material starts as: <c>lit</c>, the everyday opaque surface, when the install's Geometry
    /// category has it; otherwise Geometry's first type. Material Type has no deffile default, and a material saved
    /// without one is invalid, so a new material gets a real, stored type the combo shows.
    /// </summary>
    public static string? NewMaterialType()
    {
        if (Library is not { } lib)
            return null;
        try
        {
            var types = lib.MaterialTypesIn("Geometry");
            return types.FirstOrDefault(t => t.Equals("lit", StringComparison.OrdinalIgnoreCase)) ?? types.FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>A new material gets <see cref="NewMaterialType"/> unless it already has a type.</summary>
    public static void SeedNewAsset(Apex.Editor.Models.AssetRecord rec)
    {
        if (rec.Type.Equals("material", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrEmpty(rec.Properties.GetValueOrDefault("materialType")) && NewMaterialType() is { } type)
            rec.Properties["materialType"] = type;
    }

    public static void Configure(string deffilesDir)
    {
        var root = Path.GetFullPath(Path.Combine(deffilesDir, "..", "share", "raw", "techsetdefs_stable_toolsgfx"));
        if (!Directory.Exists(deffilesDir) || !Directory.Exists(root))
        {
            _library = null;
            return;
        }
        var library = new TechsetdefLibrary(root);
        _library = library;
        // The type lists (the first material opened needs them). The every-type layout (~50 ms) is built by material's
        // schema run, in parallel with the other deffiles; warming it too only competes with startup for the cores.
        Task.Run(() =>
        {
            try
            {
                _ = library.MaterialCategories;
            }
            catch (Exception) { /* a failed read is dropped; whoever asks next reads again and sees it */ }
        });
    }

    /// <summary>Forgets the techsetdefs (mock mode / tests).</summary>
    public static void Reset() => _library = null;

    /// <summary>
    /// Every field any techsetdef tweaks with the tweak <see cref="TechsetdefLibrary.TweakUnion"/> gives it, in that
    /// field's sort order: the layout of a material before its type is known. Each tweak keeps its element's fields,
    /// so a vector entry keeps its title.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, TechsetdefTweak>> UnionLayout(TechsetdefLibrary library) =>
        Unions.GetValue(library, static lib => lib.TweakUnion
            .OrderBy(kv => kv.Value.SortIndex)
            .ThenBy(kv => kv.Value.Element, StringComparer.Ordinal)
            .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .ToList());
}
