using System;
using System.Collections.Generic;

namespace Apex.Editor.Services;

public enum MdKind { Heading, Paragraph, Bullet }

/// <summary>One block of release notes: a heading, a paragraph, or one bullet (its wrapped lines joined).</summary>
public sealed record MdBlock(MdKind Kind, string Text)
{
    public bool IsHeading => Kind == MdKind.Heading;
    public bool IsParagraph => Kind == MdKind.Paragraph;
    public bool IsBullet => Kind == MdKind.Bullet;
}

/// <summary>
/// The little of Markdown release notes use: <c>#</c> headings, paragraphs, and <c>-</c> bullets whose wrapped lines are
/// indented. Inline marks (bold, code, links) are left in the text for <see cref="Controls.MarkdownText"/>.
/// </summary>
public static class MarkdownBlocks
{
    /// <summary>The blocks of <paramref name="markdown"/>; a first heading that is just <paramref name="skipTitle"/> is left out.</summary>
    public static List<MdBlock> Parse(string markdown, string? skipTitle = null)
    {
        var blocks = new List<MdBlock>();
        var open = new List<string>();
        var kind = MdKind.Paragraph;

        void Flush()
        {
            if (open.Count > 0)
                blocks.Add(new MdBlock(kind, string.Join(' ', open)));
            open.Clear();
        }

        foreach (var raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                Flush();
                continue;
            }
            if (line.StartsWith('#'))
            {
                Flush();
                var text = line.TrimStart('#').Trim();
                if (blocks.Count > 0 || skipTitle is null || !text.Equals(skipTitle, StringComparison.OrdinalIgnoreCase))
                    blocks.Add(new MdBlock(MdKind.Heading, text));
                continue;
            }
            if (line.Length > 1 && line[0] is '-' or '*' or '+' && line[1] == ' ')
            {
                Flush();
                kind = MdKind.Bullet;
                open.Add(line[2..].Trim());
                continue;
            }
            if (open.Count == 0)
                kind = MdKind.Paragraph;
            open.Add(line);
        }
        Flush();
        return blocks;
    }
}
