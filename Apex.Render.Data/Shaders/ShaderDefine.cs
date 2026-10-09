namespace Apex.Render.Data.Shaders;

/// <summary>One preprocessor define of a shader variant. Order matters: it is part of the cache key.</summary>
public readonly record struct ShaderDefine(string Name, string Value)
{
    /// <summary>
    /// Techsetdef/APE define strings are <c>"NAME"</c> (value <c>"1"</c>) or <c>"NAME VALUE"</c>, split at the
    /// first space.
    /// </summary>
    public static ShaderDefine Parse(string text)
    {
        var s = text.Trim();
        int sp = s.IndexOfAny([' ', '\t']);
        return sp < 0 ? new ShaderDefine(s, "1") : new ShaderDefine(s[..sp], s[(sp + 1)..].Trim());
    }

    /// <summary>Parses a list of define strings, skipping blanks.</summary>
    public static IReadOnlyList<ShaderDefine> ParseList(IEnumerable<string> texts) =>
        texts.Where(t => !string.IsNullOrWhiteSpace(t)).Select(Parse).ToArray();

    public override string ToString() => $"{Name}={Value}";
}
