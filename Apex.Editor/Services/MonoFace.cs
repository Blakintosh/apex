using System;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Avalonia.Media;
using Avalonia.Media.Fonts;

namespace Apex.Editor.Services;

/// <summary>
/// The mono face (MonoFont's first family, <c>fonts:apex-mono#Cascadia Mono</c>): the installed Cascadia Mono, with its
/// regular drawn at weight 350 and every other weight as asked.
/// <para>
/// Why 350: Cascadia Mono's 400 is a heavier design than Segoe UI's 400. At 12 px with greyscale antialiasing (the
/// window is translucent, so there is no subpixel text) a value drew about 10% more ink than the 13 px label beside it
/// and out-shouted it. At 350, a variable-font instance the face ships as SemiLight, the two carry the same ink (1.7%
/// apart in both themes), and SemiBold titles and search hits stay as heavy as Segoe's SemiBold.
/// </para>
/// <para>
/// Why a collection of its own: Cascadia Mono is a variable font, and every instance Skia makes of it reports the
/// file's weight (400). Avalonia's system font cache files an instance under the weight it reports as well as the one
/// asked for, so whichever weight was asked for first took the regular slot: after a SemiBold asset title, every value
/// drew semibold. Here each requested weight maps to its own instance, and only this collection asks the system for
/// Cascadia Mono. Without Cascadia Mono installed it finds nothing, and MonoFont falls through to Consolas.
/// </para>
/// </summary>
public sealed class MonoFace : FontCollectionBase
{
    public const string Family = "Cascadia Mono";

    public static readonly Uri CollectionKey = new("fonts:apex-mono", UriKind.Absolute);

    /// <summary>The weight the regular draws at.</summary>
    public const FontWeight Regular = (FontWeight)350;

    private readonly ConcurrentDictionary<(FontStyle, FontWeight, FontStretch), GlyphTypeface?> _faces = new();

    public override Uri Key => CollectionKey;

    public static void Register() => FontManager.Current.AddFontCollection(new MonoFace());

    public override bool TryGetGlyphTypeface(string familyName, FontStyle style, FontWeight weight, FontStretch stretch,
        [NotNullWhen(true)] out GlyphTypeface? glyphTypeface)
    {
        glyphTypeface = null;
        if (!string.Equals(familyName, Family, StringComparison.OrdinalIgnoreCase))
            return false;
        var drawn = weight == FontWeight.Normal ? Regular : weight;
        glyphTypeface = _faces.GetOrAdd((style, drawn, stretch), key =>
            FontManager.Current.SystemFonts.TryGetGlyphTypeface(Family, key.Item1, key.Item2, key.Item3, out var face)
            && face.FamilyName.Contains(Family, StringComparison.OrdinalIgnoreCase)
                ? face
                : null);
        return glyphTypeface is not null;
    }
}
