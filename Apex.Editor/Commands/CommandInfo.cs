using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Input;

namespace Apex.Editor.Commands;

/// <summary>
/// Where a command's key gesture is heard. <see cref="Window"/> gestures work from anywhere in the main window (and
/// the popped-out preview); the others only while keyboard focus is inside that part of the UI, which then applies
/// the command to its own subject (the Explorer's selected row, the focused anim preview).
/// </summary>
public enum CommandScope
{
    Window,
    Explorer,
    AnimPreview,
    Viewport,
    RecoilPreview,
}

/// <summary>
/// A command's identity: its one name, its keys and where it belongs. Defined once in <see cref="CommandCatalog"/>;
/// menus, the palette, tooltips, hint text and key handling all read it from there, so none of them can drift.
/// What the command does is bound per window by <see cref="CommandRegistry"/>.
/// </summary>
public sealed record CommandInfo
{
    public CommandInfo(string id, string name, string category, string glyph, CommandScope scope, params string[] gestures)
    {
        Id = id;
        Name = name;
        Category = category;
        Glyph = glyph;
        Scope = scope;
        Gestures = gestures.Select(KeyText.Parse).ToArray();
    }

    public string Id { get; }

    /// <summary>Sentence case, APE/GDT vocabulary. An ellipsis only when the command asks for more input.</summary>
    public string Name { get; }

    /// <summary>The palette groups and prefixes by this ("Explorer: Group by type").</summary>
    public string Category { get; }

    public string Glyph { get; }

    public CommandScope Scope { get; }

    /// <summary>The first is the one shown in menus and the palette; the rest are alternates listed in tooltips.</summary>
    public IReadOnlyList<KeyGesture> Gestures { get; }

    public KeyGesture? Gesture => Gestures.Count > 0 ? Gestures[0] : null;

    /// <summary>For a toggle: what its icon button says while it is on ("Restore preview" for "Maximize preview").</summary>
    public string? OnName { get; init; }

    /// <summary>Other words the palette matches ("hide", "toggle"), never shown.</summary>
    public string Keywords { get; init; } = "";

    /// <summary>True for a command that is a switch: menus draw it with a check, the palette says on or off.</summary>
    public bool IsToggle { get; init; }

    /// <summary>"Show Explorer (Ctrl+B)": the name, then every gesture.</summary>
    public string Tip => TipFor(Name);

    public string TipFor(string label) =>
        Gestures.Count == 0 ? label : $"{label} ({string.Join(" or ", Gestures.Select(KeyText.Format))})";

    /// <summary>The primary gesture as the platform writes it ("Ctrl+Shift+P", "⇧⌘P"), or "".</summary>
    public string GestureText => Gesture is { } g ? KeyText.Format(g) : "";

    public bool Matches(KeyEventArgs e) => Gestures.Any(g => g.Matches(e));

    public override string ToString() => Id;
}

/// <summary>Parses and prints gestures once, so every hint in the app writes a key the same way.</summary>
public static class KeyText
{
    /// <summary>
    /// Parses "Ctrl+Shift+P". "Ctrl" is the platform's command key: Control on Windows and Linux, ⌘ on macOS.
    /// </summary>
    public static KeyGesture Parse(string text)
    {
        var g = KeyGesture.Parse(text);
        // ⌘Tab belongs to macOS (the app switcher); tab cycling stays on Control there, as in every Mac editor.
        if (OperatingSystem.IsMacOS() && g.KeyModifiers.HasFlag(KeyModifiers.Control) && g.Key != Key.Tab)
            g = new KeyGesture(g.Key, (g.KeyModifiers & ~KeyModifiers.Control) | KeyModifiers.Meta);
        return g;
    }

    public static string Format(KeyGesture g)
    {
        var key = KeyName(g.Key);
        var m = g.KeyModifiers;
        if (OperatingSystem.IsMacOS())
        {
            var mac = "";
            if (m.HasFlag(KeyModifiers.Control)) mac += "⌃";
            if (m.HasFlag(KeyModifiers.Alt)) mac += "⌥";
            if (m.HasFlag(KeyModifiers.Shift)) mac += "⇧";
            if (m.HasFlag(KeyModifiers.Meta)) mac += "⌘";
            return mac + key;
        }
        var parts = new List<string>(4);
        if (m.HasFlag(KeyModifiers.Control)) parts.Add("Ctrl");
        if (m.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
        if (m.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
        if (m.HasFlag(KeyModifiers.Meta)) parts.Add("Win");
        parts.Add(key);
        return string.Join('+', parts);
    }

    private static string KeyName(Key key) => key switch
    {
        >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
        Key.Left => "←",
        Key.Right => "→",
        Key.Up => "↑",
        Key.Down => "↓",
        Key.OemComma => ",",
        Key.OemPeriod => ".",
        Key.Delete => "Delete",
        Key.Escape => "Esc",
        Key.Return => "Enter",
        _ => key.ToString(),
    };

    /// <summary>
    /// A key that types or edits text when a text box has focus: no Ctrl, Alt or ⌘, and not a function key or Esc.
    /// Such gestures never fire while the user is typing in a field.
    /// </summary>
    public static bool IsTypingKey(KeyGesture g) =>
        (g.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta)) == 0
        && g.Key is not (>= Key.F1 and <= Key.F24) and not Key.Escape;
}
