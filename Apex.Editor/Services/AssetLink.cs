using System;

namespace Apex.Editor.Services;

/// <summary>
/// An <c>asset://&lt;name&gt;.&lt;type&gt;</c> link, as Radiant opens one (e.g. <c>asset://ar_standard.bulletweapon</c>): the
/// asset's name, and its type after the last <c>.</c>. Apex is started with the link as its argument.
/// </summary>
public readonly record struct AssetLink(string Name, string Type)
{
    public const string Scheme = "asset://";

    /// <summary>Whether <paramref name="arg"/> is meant as a link (starts with <c>asset://</c>), well-formed or not.</summary>
    public static bool IsLink(string? arg) => arg is not null && arg.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The link in <paramref name="text"/>: percent-escapes decoded, a trailing <c>/</c> (some launchers add one) ignored.
    /// Null when it is not a link or names no asset. Without a <c>.</c> the type is empty (any type).
    /// </summary>
    public static AssetLink? Parse(string? text)
    {
        if (!IsLink(text) || text!.Length > 1024)
            return null;
        string body;
        try
        {
            body = Uri.UnescapeDataString(text[Scheme.Length..]).Trim().TrimEnd('/');
        }
        catch (UriFormatException)
        {
            return null;
        }
        var dot = body.LastIndexOf('.');
        var name = (dot < 0 ? body : body[..dot]).Trim();
        var type = dot < 0 ? "" : body[(dot + 1)..].Trim();
        if (name.Length == 0 || name.IndexOfAny(['/', '\\', '"', '\r', '\n']) >= 0 || type.IndexOfAny([' ', '/', '\\', '"']) >= 0)
            return null;
        return new AssetLink(name, type);
    }

    public override string ToString() => Type.Length == 0 ? Scheme + Name : $"{Scheme}{Name}.{Type}";
}
