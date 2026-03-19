using Markdig;
using Markdig.Helpers;
using Markdig.Parsers;
using Markdig.Parsers.Inlines;
using Markdig.Syntax.Inlines;

namespace Console.Lib;

/// <summary>
/// Markdig inline parser for <c>[text]{color}</c> syntax.
/// Triggers on <c>[</c> and looks ahead for the full <c>[text]{color}</c> pattern.
/// Registered with higher priority than the link parser so it gets first crack.
/// <para>
/// If the pattern doesn't match (e.g. missing <c>{color}</c>, or the color is invalid),
/// returns false so Markdig falls through to the link parser or treats <c>[</c> as literal.
/// </para>
/// </summary>
public class ColorInlineParser : InlineParser
{
    public ColorInlineParser()
    {
        OpeningCharacters = ['['];
    }

    public override bool Match(InlineProcessor processor, ref StringSlice slice)
    {
        var text = slice.Text;
        if (text is null) return false;

        var start = slice.Start;

        // Find the closing ']'
        var closeBracket = FindClosingBracket(text, start + 1);
        if (closeBracket < 0 || closeBracket + 1 >= text.Length)
            return false;

        // Must be immediately followed by '{'
        if (text[closeBracket + 1] != '{')
            return false;

        // Find the closing '}'
        var closeBrace = text.IndexOf('}', closeBracket + 2);
        if (closeBrace < 0)
            return false;

        // Extract and validate color
        var colorName = text.Substring(closeBracket + 2, closeBrace - closeBracket - 2).Trim();
        if (colorName.Length == 0 || !MarkdownTheme.TryParseColor(colorName, out var color))
            return false;

        // Extract inner text
        var innerText = text.Substring(start + 1, closeBracket - start - 1);

        var colorInline = new ColorInline { Color = color };
        colorInline.AppendChild(new LiteralInline(innerText));

        processor.Inline = colorInline;
        slice.Start = closeBrace + 1;
        return true;
    }

    private static int FindClosingBracket(string text, int from)
    {
        var depth = 0;
        for (var i = from; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '[': depth++; break;
                case ']' when depth == 0: return i;
                case ']': depth--; break;
            }
        }
        return -1;
    }
}

public static class ColorInlineExtensions
{
    /// <summary>
    /// Adds <c>[text]{color}</c> support to the Markdig pipeline.
    /// </summary>
    public static MarkdownPipelineBuilder UseColorInlines(this MarkdownPipelineBuilder pipeline)
    {
        var parser = new ColorInlineParser();
        pipeline.InlineParsers.InsertBefore<LinkInlineParser>(parser);
        return pipeline;
    }
}
