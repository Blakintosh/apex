using System;
using Avalonia;
using Avalonia.Styling;

namespace Apex.Editor.Services;

/// <summary>The theme the user picked. <see cref="System"/> follows the Windows light/dark setting, live.</summary>
public enum ThemeChoice
{
    System,
    Graphite,
    Slate,
    Light,
}

/// <summary>
/// The three themes in Resources/Tokens.axaml and how a <see cref="ThemeChoice"/> maps onto them. Graphite is the
/// Dark variant and Light the Light one, so following Windows needs nothing here; Slate is its own variant that
/// inherits Dark, so stock Fluent resources and every dark-surface check (TypeStyles, the viewport) treat it as dark.
/// </summary>
public static class AppTheme
{
    public static readonly ThemeVariant Slate = new("Slate", ThemeVariant.Dark);

    public static ThemeVariant VariantOf(ThemeChoice choice) => choice switch
    {
        ThemeChoice.Graphite => ThemeVariant.Dark,
        ThemeChoice.Slate => Slate,
        ThemeChoice.Light => ThemeVariant.Light,
        _ => ThemeVariant.Default,
    };

    public static ThemeChoice Parse(string? value) =>
        Enum.TryParse<ThemeChoice>(value, true, out var choice) ? choice : ThemeChoice.System;

    /// <summary>True when the variant (or the one it inherits) is Light: surfaces are light and inks replace hues.</summary>
    public static bool IsLight(ThemeVariant? variant)
    {
        for (var v = variant; v is not null; v = v.InheritVariant)
            if (v == ThemeVariant.Light)
                return true;
        return false;
    }

    public static void Apply(ThemeChoice choice)
    {
        if (Application.Current is { } app)
            app.RequestedThemeVariant = VariantOf(choice);
    }
}
