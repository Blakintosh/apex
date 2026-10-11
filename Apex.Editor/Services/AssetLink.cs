using System;

namespace Apex.Editor.Services;

/// <summary>
/// An <c>asset://&lt;name&gt;.&lt;type&gt;</c> link, as Radiant opens one (e.g. <c>asset://ar_standard.bulletweapon</c>): the
/// asset's name, and its type after the last <c>.</c>. Apex is started with the link as its argument. A link may carry a
/// query: <c>?stock=clone&amp;map=&lt;map&gt;</c> opens the <em>stock</em> asset's Create local copy dialog with that map as
/// its target.
/// </summary>
public readonly record struct AssetLink(string Name, string Type)
{
    public const string Scheme = "asset://";

    /// <summary><c>clone</c> when the link asks for the Create local copy dialog on the stock asset; empty otherwise.</summary>
    public string Stock { get; init; } = "";

    /// <summary>The map or mod the copy goes into (<c>?map=</c>); empty when the link names none.</summary>
    public string Map { get; init; } = "";

    public bool IsStockClone => Stock == "clone";

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
        string body, stock = "", map = "";
        try
        {
            var raw = text[Scheme.Length..];
            if (raw.IndexOf('?') is >= 0 and var q)
            {
                foreach (var pair in raw[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
                {
                    var eq = pair.IndexOf('=');
                    var key = Uri.UnescapeDataString((eq < 0 ? pair : pair[..eq]).Replace('+', ' ')).Trim();
                    var value = eq < 0 ? "" : Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' ')).Trim();
                    if (key.Equals("stock", StringComparison.OrdinalIgnoreCase) && value.Equals("clone", StringComparison.OrdinalIgnoreCase))
                        stock = "clone";
                    else if (key.Equals("map", StringComparison.OrdinalIgnoreCase) && value.Length <= 260 && value.IndexOfAny(['"', '\r', '\n', '\0']) < 0)
                        map = value;
                }
                raw = raw[..q];
            }
            body = Uri.UnescapeDataString(raw).Trim().TrimEnd('/');
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
        return new AssetLink(name, type) { Stock = stock, Map = map };
    }

    public override string ToString()
    {
        var link = Type.Length == 0 ? Scheme + Name : $"{Scheme}{Name}.{Type}";
        if (Stock.Length == 0)
            return link;
        return link + "?stock=" + Stock + (Map.Length > 0 ? "&map=" + Uri.EscapeDataString(Map) : "");
    }
}
