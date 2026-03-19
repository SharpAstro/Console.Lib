using System.Text;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Console.Lib;

/// <summary>
/// Renders Markdown text to VT-styled terminal output using Markdig for parsing.
/// Supports headers, bold, italic, links, tables, lists, and horizontal rules.
/// </summary>
public static class MarkdownRenderer
{
    // ── VT escape constants ───────────────────────────────────────────

    private const string Bold = "\e[1m";
    private const string Dim = "\e[2m";
    private const string ItalicCode = "\e[3m";
    private const string Underline = "\e[4m";
    private const string Reset = "\e[0m";
    private const string BoldBlue = "\e[1;34m";
    private const string BoldCyan = "\e[1;36m";
    private const string BoldWhite = "\e[1;97m";
    private const string CyanFg = "\e[36m";

    /// <summary>
    /// Markdig pipeline with pipe-table support enabled.
    /// </summary>
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .Build();

    /// <summary>
    /// Renders Markdown to the given <see cref="TextWriter"/>.
    /// </summary>
    /// <param name="markdown">The Markdown source text.</param>
    /// <param name="output">Target writer for the styled output.</param>
    /// <param name="width">Available terminal width in columns.</param>
    /// <param name="colorMode">Terminal color mode (SGR-16 or TrueColor).</param>
    public static void Render(string markdown, TextWriter output, int width, ColorMode colorMode = ColorMode.TrueColor)
    {
        foreach (var line in RenderLines(markdown, width, colorMode))
            output.WriteLine(line);
    }

    /// <summary>
    /// Renders Markdown to a list of pre-formatted VT lines suitable for widget rendering.
    /// Each string may contain VT escape sequences for styling.
    /// </summary>
    /// <param name="markdown">The Markdown source text.</param>
    /// <param name="width">Available terminal width in columns.</param>
    /// <param name="colorMode">Terminal color mode (SGR-16 or TrueColor).</param>
    /// <returns>A list of styled output lines ready for terminal display.</returns>
    public static List<string> RenderLines(string markdown, int width, ColorMode colorMode = ColorMode.TrueColor)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return [];

        var doc = Markdown.Parse(markdown, Pipeline);
        var result = new List<string>();
        var first = true;

        foreach (var block in doc)
        {
            if (!first) result.Add("");
            RenderBlock(block, width, colorMode, result, nestLevel: 0);
            first = false;
        }

        return result;
    }

    // ── Block rendering ───────────────────────────────────────────────

    private static void RenderBlock(Block block, int width, ColorMode colorMode, List<string> result, int nestLevel)
    {
        switch (block)
        {
            case HeadingBlock heading:
                RenderHeading(heading, width, colorMode, result);
                break;

            case ThematicBreakBlock:
                result.Add($"{Dim}{new string('─', width)}{Reset}");
                break;

            case ListBlock list:
                RenderList(list, width, colorMode, result, nestLevel);
                break;

            case Table table:
                RenderTable(table, width, colorMode, result);
                break;

            case ParagraphBlock paragraph when paragraph.Inline is not null:
                var text = FormatInlinesFromAst(paragraph.Inline, bold: false, italic: false);
                result.AddRange(WordWrap(text, width));
                break;
        }
    }

    private static void RenderHeading(HeadingBlock heading, int width, ColorMode colorMode, List<string> result)
    {
        if (heading.Inline is null) return;

        var style = heading.Level switch
        {
            1 => BoldBlue,
            2 => BoldCyan,
            _ => BoldWhite,
        };

        var text = FormatInlinesFromAst(heading.Inline, bold: false, italic: false);
        result.AddRange(WordWrap($"{style}{text}{Reset}", width));
    }

    private static void RenderList(ListBlock list, int width, ColorMode colorMode, List<string> result, int nestLevel)
    {
        var orderedNumber = list.IsOrdered
            ? (int.TryParse(list.OrderedStart, out var start) ? start : 1)
            : 0;

        foreach (var item in list)
        {
            if (item is not ListItemBlock listItem) continue;

            var isFirstChild = true;
            foreach (var child in listItem)
            {
                if (isFirstChild && child is ParagraphBlock para && para.Inline is not null)
                {
                    var text = FormatInlinesFromAst(para.Inline, bold: false, italic: false);

                    if (list.IsOrdered)
                    {
                        var prefix = $"  {Dim}{orderedNumber}.{Reset} ";
                        result.AddRange(WordWrap($"{prefix}{text}", width, "     "));
                        orderedNumber++;
                    }
                    else
                    {
                        var bulletChar = nestLevel switch { 0 => "•", 1 => "◦", _ => "▪" };
                        var pad = new string(' ', 2 + nestLevel * 2);
                        var bullet = $"{pad}{CyanFg}{bulletChar}{Reset} ";
                        var wrapIndent = new string(' ', pad.Length + 2);
                        result.AddRange(WordWrap($"{bullet}{text}", width, wrapIndent));
                    }

                    isFirstChild = false;
                }
                else if (child is ListBlock nestedList)
                {
                    RenderList(nestedList, width, colorMode, result, nestLevel + 1);
                }
                else
                {
                    RenderBlock(child, width, colorMode, result, nestLevel);
                    isFirstChild = false;
                }
            }
        }
    }

    // ── Table rendering ───────────────────────────────────────────────

    private static void RenderTable(Table table, int width, ColorMode colorMode, List<string> result)
    {
        // Collect rows and cell text
        var headerCells = new List<string>();
        var dataRows = new List<List<string>>();

        foreach (var rowBlock in table)
        {
            if (rowBlock is not TableRow row) continue;

            var cells = new List<string>();
            foreach (var cellBlock in row)
            {
                if (cellBlock is not TableCell cell) continue;
                cells.Add(GetCellText(cell));
            }

            if (row.IsHeader)
                headerCells = cells;
            else
                dataRows.Add(cells);
        }

        if (headerCells.Count == 0 && dataRows.Count == 0) return;

        // Column count and widths
        var colCount = headerCells.Count;
        foreach (var row in dataRows)
            colCount = Math.Max(colCount, row.Count);

        var colWidths = new int[colCount];
        for (var col = 0; col < colCount; col++)
        {
            if (col < headerCells.Count)
                colWidths[col] = headerCells[col].Length;
            foreach (var row in dataRows)
                if (col < row.Count)
                    colWidths[col] = Math.Max(colWidths[col], row[col].Length);
            colWidths[col] = Math.Max(1, colWidths[col]);
        }

        // Column alignments from Markdig
        var alignments = new Alignment[colCount];
        for (var col = 0; col < colCount; col++)
        {
            if (col < table.ColumnDefinitions.Count)
            {
                alignments[col] = table.ColumnDefinitions[col].Alignment switch
                {
                    TableColumnAlign.Center => Alignment.Center,
                    TableColumnAlign.Right => Alignment.Right,
                    _ => Alignment.Left,
                };
            }
        }

        // Top border
        result.Add($"{Dim}{TableBorder('┌', '┬', '┐', colWidths)}{Reset}");

        // Header
        if (headerCells.Count > 0)
        {
            result.Add(FormatTableRow(headerCells, colWidths, alignments, colorMode, isHeader: true));
            result.Add($"{Dim}{TableBorder('├', '┼', '┤', colWidths)}{Reset}");
        }

        // Data rows
        foreach (var row in dataRows)
            result.Add(FormatTableRow(row, colWidths, alignments, colorMode, isHeader: false));

        // Bottom border
        result.Add($"{Dim}{TableBorder('└', '┴', '┘', colWidths)}{Reset}");
    }

    private static string GetCellText(TableCell cell)
    {
        if (cell.FirstOrDefault() is ParagraphBlock para && para.Inline is not null)
        {
            var sb = new StringBuilder();
            foreach (var inline in para.Inline)
                AppendRawText(inline, sb);
            return sb.ToString();
        }
        return "";
    }

    private static void AppendRawText(Inline inline, StringBuilder sb)
    {
        switch (inline)
        {
            case LiteralInline literal:
                sb.Append(literal.Content);
                break;
            case ContainerInline container:
                foreach (var child in container)
                    AppendRawText(child, sb);
                break;
        }
    }

    private static string FormatTableRow(List<string> cells, int[] colWidths, Alignment[] alignments, ColorMode colorMode, bool isHeader)
    {
        var sb = new StringBuilder();
        sb.Append($"{Dim}│{Reset}");
        for (var col = 0; col < colWidths.Length; col++)
        {
            var rawText = col < cells.Count ? cells[col] : "";
            var formatted = FormatInline(rawText, colorMode);
            if (isHeader) formatted = $"{Bold}{formatted}{Reset}";

            var aligned = AlignCell(formatted, rawText.Length, colWidths[col],
                col < alignments.Length ? alignments[col] : Alignment.Left);
            sb.Append($" {aligned} {Dim}│{Reset}");
        }
        return sb.ToString();
    }

    private static string TableBorder(char left, char cross, char right, int[] colWidths)
    {
        var sb = new StringBuilder();
        sb.Append(left);
        for (var col = 0; col < colWidths.Length; col++)
        {
            if (col > 0) sb.Append(cross);
            sb.Append(new string('─', colWidths[col] + 2));
        }
        sb.Append(right);
        return sb.ToString();
    }

    private static string AlignCell(string formatted, int visibleLen, int colWidth, Alignment alignment)
    {
        var padding = colWidth - visibleLen;
        if (padding <= 0) return formatted;
        return alignment switch
        {
            Alignment.Right => new string(' ', padding) + formatted,
            Alignment.Center => new string(' ', padding / 2) + formatted + new string(' ', padding - padding / 2),
            _ => formatted + new string(' ', padding),
        };
    }

    private enum Alignment { Left, Center, Right }

    // ── Inline rendering ──────────────────────────────────────────────

    /// <summary>
    /// Formats a string containing inline Markdown (bold, italic, links) into VT-styled text.
    /// Uses Markdig to parse the inline elements.
    /// </summary>
    /// <param name="text">Raw Markdown text with inline formatting.</param>
    /// <param name="colorMode">Terminal color mode.</param>
    /// <returns>A string with VT escape sequences for styled terminal output.</returns>
    internal static string FormatInline(string text, ColorMode colorMode)
    {
        var doc = Markdown.Parse(text, Pipeline);
        if (doc.FirstOrDefault() is ParagraphBlock para && para.Inline is not null)
            return FormatInlinesFromAst(para.Inline, bold: false, italic: false);
        return text;
    }

    /// <summary>
    /// Walks a Markdig inline AST and emits VT-styled text, correctly handling
    /// nested emphasis by tracking the bold/italic state through the recursion.
    /// </summary>
    private static string FormatInlinesFromAst(ContainerInline container, bool bold, bool italic)
    {
        var sb = new StringBuilder();
        RenderInlines(container, sb, bold, italic);
        return sb.ToString();
    }

    private static void RenderInlines(ContainerInline container, StringBuilder sb, bool bold, bool italic)
    {
        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    sb.Append(literal.Content);
                    break;

                case EmphasisInline emphasis:
                {
                    var newBold = bold || emphasis.DelimiterCount >= 2;
                    var newItalic = italic || emphasis.DelimiterCount == 1 || emphasis.DelimiterCount >= 3;

                    // Apply combined style
                    sb.Append(Reset);
                    if (newBold) sb.Append(Bold);
                    if (newItalic) sb.Append(ItalicCode);

                    RenderInlines(emphasis, sb, newBold, newItalic);

                    // Restore parent style
                    sb.Append(Reset);
                    if (bold) sb.Append(Bold);
                    if (italic) sb.Append(ItalicCode);
                    break;
                }

                case LinkInline link:
                    sb.Append($"{Underline}{CyanFg}");
                    RenderInlines(link, sb, false, false);
                    sb.Append(Reset);
                    if (!string.IsNullOrEmpty(link.Url))
                        sb.Append($"{Dim} ({link.Url}){Reset}");
                    // Restore parent style
                    if (bold) sb.Append(Bold);
                    if (italic) sb.Append(ItalicCode);
                    break;

                case LineBreakInline:
                    sb.Append(' ');
                    break;
            }
        }
    }

    // ── Word wrapping (ANSI-aware) ────────────────────────────────────

    /// <summary>
    /// Wraps text containing VT escape sequences at word boundaries.
    /// Continuation lines are prefixed with <paramref name="continuationIndent"/>
    /// and any active ANSI styles are carried over.
    /// </summary>
    internal static List<string> WordWrap(string text, int maxWidth, string continuationIndent = "")
    {
        if (maxWidth <= 0 || VisibleLength(text) <= maxWidth)
            return [text];

        var words = SplitWords(text);
        if (words.Count == 0) return [""];

        var result = new List<string>();
        var line = new StringBuilder();
        var lineVisWidth = 0;
        var styles = new StringBuilder();
        var needSpace = false;

        foreach (var word in words)
        {
            var wordVisWidth = VisibleLength(word);
            var spaceNeeded = needSpace ? 1 : 0;

            if (lineVisWidth + spaceNeeded + wordVisWidth > maxWidth && lineVisWidth > 0)
            {
                result.Add(line.ToString());
                line.Clear();
                line.Append(continuationIndent);
                line.Append(styles);
                lineVisWidth = VisibleLength(continuationIndent);
                needSpace = false;
                spaceNeeded = 0;
            }

            if (needSpace)
            {
                line.Append(' ');
                lineVisWidth++;
            }

            line.Append(word);
            lineVisWidth += wordVisWidth;
            needSpace = true;

            UpdateStyles(word, styles);
        }

        if (line.Length > 0)
            result.Add(line.ToString());

        return result;
    }

    /// <summary>
    /// Splits a VT-styled string into words, keeping ANSI escape sequences
    /// attached to the adjacent word rather than treating them as separate tokens.
    /// </summary>
    private static List<string> SplitWords(string text)
    {
        var words = new List<string>();
        var current = new StringBuilder();
        var i = 0;

        while (i < text.Length)
        {
            if (text[i] == '\e')
            {
                var end = text.IndexOf('m', i);
                if (end >= 0)
                {
                    current.Append(text, i, end - i + 1);
                    i = end + 1;
                    continue;
                }
            }

            if (text[i] == ' ')
            {
                if (current.Length > 0)
                {
                    words.Add(current.ToString());
                    current.Clear();
                }
                i++;
                continue;
            }

            current.Append(text[i]);
            i++;
        }

        if (current.Length > 0)
            words.Add(current.ToString());

        return words;
    }

    /// <summary>
    /// Tracks active ANSI style codes within a text fragment.
    /// <see cref="Reset"/> (<c>\e[0m</c>) clears all tracked styles;
    /// any other ANSI sequence is appended.
    /// </summary>
    private static void UpdateStyles(string text, StringBuilder styles)
    {
        var i = 0;
        while (i < text.Length)
        {
            if (text[i] == '\e')
            {
                var end = text.IndexOf('m', i);
                if (end >= 0)
                {
                    var seq = text.Substring(i, end - i + 1);
                    if (seq == Reset)
                        styles.Clear();
                    else
                        styles.Append(seq);
                    i = end + 1;
                    continue;
                }
            }
            i++;
        }
    }

    // ── Utility ───────────────────────────────────────────────────────

    /// <summary>
    /// Returns the visible character count of a string, ignoring any embedded ANSI escape sequences.
    /// </summary>
    internal static int VisibleLength(string text)
    {
        var len = 0;
        var inEscape = false;
        foreach (var c in text)
        {
            if (c == '\e') { inEscape = true; continue; }
            if (inEscape) { if (c == 'm') inEscape = false; continue; }
            len++;
        }
        return len;
    }
}
