using System;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Apex.Editor.Controls;

/// <summary>A text block that reads <c>**bold**</c>, <c>`code`</c> and <c>[text](url)</c> (as its text) out of release notes.</summary>
public sealed partial class MarkdownText : TextBlock
{
    public static readonly StyledProperty<string?> MarkdownProperty = AvaloniaProperty.Register<MarkdownText, string?>(nameof(Markdown));

    protected override Type StyleKeyOverride => typeof(TextBlock);

    public string? Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == MarkdownProperty)
            Rebuild(Markdown ?? "");
    }

    [GeneratedRegex(@"\*\*(.+?)\*\*|`(.+?)`|\[(.+?)\]\(.+?\)")]
    private static partial Regex Inline();

    private void Rebuild(string text)
    {
        Inlines ??= new InlineCollection();
        Inlines.Clear();
        var at = 0;
        foreach (Match m in Inline().Matches(text))
        {
            if (m.Index > at)
                Inlines.Add(new Run(text[at..m.Index]));
            if (m.Groups[1].Success)
                Inlines.Add(new Run(m.Groups[1].Value) { FontWeight = FontWeight.SemiBold });
            else if (m.Groups[2].Success)
                Inlines.Add(new Run(m.Groups[2].Value)
                {
                    FontFamily = this.TryFindResource("MonoFont", ActualThemeVariant, out var mono) && mono is FontFamily family ? family : FontFamily.Default,
                });
            else
                Inlines.Add(new Run(m.Groups[3].Value));
            at = m.Index + m.Length;
        }
        if (at < text.Length)
            Inlines.Add(new Run(text[at..]));
    }
}
