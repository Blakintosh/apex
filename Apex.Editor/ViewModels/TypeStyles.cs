using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;

namespace Apex.Editor.ViewModels;

/// <summary>
/// Per-asset-type glyph and family colour used across the browser, tabs and inspector. The glyph's shape names the
/// type; the colour only names its family (weapons, models, surfaces, motion), from the Type* tokens in
/// Resources/Tokens.axaml, which keep clear of the amber, rose and sky that mean change, problem and accent. Each
/// family has a dark-surface hue (TypeX) and a light-surface ink (TypeXInk), because the hues tuned for Graphite drop
/// to ~2:1 on a white layer. The brushes handed out are shared and re-tinted in place on a theme switch, so every
/// existing binding follows the variant without re-evaluating.
/// </summary>
public static class TypeStyles
{
    private sealed record Style(string Glyph, string Token);

    private const string Weapons = "TypeWeapons";
    private const string Models = "TypeModels";
    private const string Surfaces = "TypeSurfaces";
    private const string Motion = "TypeMotion";

    private static readonly Style Weapon = new("⌖", Weapons);
    private static readonly Style Attachment = new("⊕", Weapons);
    private static readonly Style XModel = new("◇", Models);
    private static readonly Style Fallback = new("◈", "TypeOther");

    private static readonly Dictionary<string, Style> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["weapon"] = Weapon,
        ["attachment"] = Attachment,
        ["xmodel"] = XModel,
        ["material"] = new("▨", Surfaces),
        ["image"] = new("▦", Surfaces),
        ["xanim"] = new("⌁", Motion),
        ["fx"] = new("✺", Motion),
        ["sound"] = new("◉", Motion),
        ["character"] = new("♟", Models),
        ["vehicle"] = new("⬢", Models),
        ["zbarrier"] = new("▤", Models),

        // ── Weapon-family types (BO3 deffiles) all read as "a weapon" ──
        ["bulletweapon"] = Weapon,
        ["projectileweapon"] = Weapon,
        ["grenadeweapon"] = Weapon,
        ["gasweapon"] = Weapon,
        ["riotshieldweapon"] = Weapon,
        ["meleeweapon"] = Weapon,
        ["attachmentunique"] = Attachment,
        ["attachment_unique"] = Attachment,
        ["weaponcamo"] = new("◒", Weapons),
        ["xmodelalias"] = XModel,
    };

    private static readonly Dictionary<string, SolidColorBrush> BrushCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, SolidColorBrush> DimCache = new(StringComparer.OrdinalIgnoreCase);
    private static bool _light;

    public static string Glyph(string type) => Map.TryGetValue(type, out var s) ? s.Glyph : Fallback.Glyph;

    /// <summary>True for types the browser treats as weapons (bulletweapon, meleeweapon…).</summary>
    public static bool IsWeaponType(string type) => Map.TryGetValue(type, out var s) && ReferenceEquals(s, Weapon);

    private static Color ColorOf(string type)
    {
        var s = Map.TryGetValue(type, out var found) ? found : Fallback;
        var key = _light ? s.Token + "Ink" : s.Token;
        // Outside a running app (unit tests without App resources) fall back to a neutral grey.
        return Application.Current?.TryGetResource(key, null, out var value) == true && value is Color c
            ? c
            : Colors.Gray;
    }

    private static Color DimOf(string type)
    {
        var c = ColorOf(type);
        return new Color((byte)(_light ? 40 : 36), c.R, c.G, c.B);
    }

    public static IBrush Brush(string type)
    {
        if (!BrushCache.TryGetValue(type, out var b))
            BrushCache[type] = b = new SolidColorBrush(ColorOf(type));
        return b;
    }

    /// <summary>The faint, theme-following glyph colour for things that are not assets (commands).</summary>
    public static IBrush NeutralBrush => Brush("");

    public static IBrush DimBrush(string type)
    {
        if (!DimCache.TryGetValue(type, out var b))
            DimCache[type] = b = new SolidColorBrush(DimOf(type));
        return b;
    }

    /// <summary>
    /// Re-tints every handed-out brush for the given theme variant. Called once at startup (the app
    /// may start light) and again whenever the Windows light/dark setting changes.
    /// </summary>
    public static void ApplyVariant(bool light)
    {
        if (_light == light)
            return;
        _light = light;
        foreach (var (type, brush) in BrushCache)
            brush.Color = ColorOf(type);
        foreach (var (type, brush) in DimCache)
            brush.Color = DimOf(type);
    }
}
