using System.Text;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Console.Lib;

/// <summary>
/// Renders Markdown text to VT-styled terminal output using Markdig for parsing.
/// Supports headers, bold, italic, links, tables, lists, horizontal rules,
/// and colored text via <c>[text]{color}</c> syntax.
/// <para>
/// All colors are resolved at render time through <see cref="MarkdownTheme"/> to respect
/// the active <see cref="ColorMode"/>. Use <see cref="ColorMode.None"/> to suppress all escapes.
/// </para>
/// </summary>
public static class MarkdownRenderer
{
    // ── VT attribute constants (mode-independent) ─────────────────────

    private const string Bold = "\e[1m";
    private const string ItalicCode = "\e[3m";
    private const string Underline = "\e[4m";
    private const string Reset = "\e[0m";

    /// <summary>
    /// Markdig pipeline with pipe-table and color-inline support enabled.
    /// </summary>
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseColorInlines()
        .Build();

    /// <summary>
    /// Renders Markdown to the given <see cref="TextWriter"/>.
    /// </summary>
    public static void Render(string markdown, TextWriter output, int width,
        ColorMode colorMode = ColorMode.TrueColor, MarkdownTheme? theme = null)
    {
        foreach (var line in RenderLines(markdown, width, colorMode, theme))
            output.WriteLine(line);
    }

    /// <summary>
    /// Renders Markdown to a list of pre-formatted VT lines suitable for widget rendering.
    /// </summary>
    public static List<string> RenderLines(string markdown, int width,
        ColorMode colorMode = ColorMode.TrueColor, MarkdownTheme? theme = null)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return [];

        theme ??= MarkdownTheme.Default;
        var doc = Markdown.Parse(markdown, Pipeline);
        var result = new List<string>();
        var first = true;

        foreach (var block in doc)
        {
            if (!first) result.Add("");
            RenderBlock(block, width, colorMode, theme, result, nestLevel: 0);
            first = false;
        }

        return result;
    }

    // ── Block rendering ───────────────────────────────────────────────

    private static void RenderBlock(Block block, int width, ColorMode colorMode,
        MarkdownTheme theme, List<string> result, int nestLevel)
    {
        switch (block)
        {
            case HeadingBlock heading:
                RenderHeading(heading, width, colorMode, theme, result);
                break;

            case ThematicBreakBlock:
                var dimColor = Resolve(theme.Dim, colorMode);
                result.Add($"{dimColor}{new string('─', width)}{Rst(colorMode)}");
                break;

            case ListBlock list:
                RenderList(list, width, colorMode, theme, result, nestLevel);
                break;

            case Table table:
                RenderTable(table, width, colorMode, theme, result);
                break;

            case ParagraphBlock paragraph when paragraph.Inline is not null:
                var text = FormatInlinesFromAst(paragraph.Inline, bold: false, italic: false, colorMode, theme);
                result.AddRange(WordWrap(text, width));
                break;
        }
    }

    private static void RenderHeading(HeadingBlock heading, int width, ColorMode colorMode,
        MarkdownTheme theme, List<string> result)
    {
        if (heading.Inline is null) return;

        var headingColor = heading.Level switch
        {
            1 => theme.Heading1,
            2 => theme.Heading2,
            _ => theme.Heading3,
        };

        var style = BoldAttr(colorMode) + Resolve(headingColor, colorMode);
        var text = FormatInlinesFromAst(heading.Inline, bold: false, italic: false, colorMode, theme);
        result.AddRange(WordWrap($"{style}{text}{Rst(colorMode)}", width));
    }

    private static void RenderList(ListBlock list, int width, ColorMode colorMode,
        MarkdownTheme theme, List<string> result, int nestLevel)
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
                    var text = FormatInlinesFromAst(para.Inline, bold: false, italic: false, colorMode, theme);
                    var dimColor = Resolve(theme.Dim, colorMode);
                    var bulletColor = Resolve(theme.Bullet, colorMode);
                    var rst = Rst(colorMode);

                    if (list.IsOrdered)
                    {
                        var prefix = $"  {dimColor}{orderedNumber}.{rst} ";
                        result.AddRange(WordWrap($"{prefix}{text}", width, "     "));
                        orderedNumber++;
                    }
                    else
                    {
                        var bulletChar = nestLevel switch { 0 => "•", 1 => "◦", _ => "▪" };
                        var pad = new string(' ', 2 + nestLevel * 2);
                        var bullet = $"{pad}{bulletColor}{bulletChar}{rst} ";
                        var wrapIndent = new string(' ', pad.Length + 2);
                        result.AddRange(WordWrap($"{bullet}{text}", width, wrapIndent));
                    }

                    isFirstChild = false;
                }
                else if (child is ListBlock nestedList)
                {
                    RenderList(nestedList, width, colorMode, theme, result, nestLevel + 1);
                }
                else
                {
                    RenderBlock(child, width, colorMode, theme, result, nestLevel);
                    isFirstChild = false;
                }
            }
        }
    }

    // ── Table rendering ───────────────────────────────────────────────

    private static void RenderTable(Table table, int width, ColorMode colorMode,
        MarkdownTheme theme, List<string> result)
    {
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

        var dimColor = Resolve(theme.Dim, colorMode);
        var rst = Rst(colorMode);

        result.Add($"{dimColor}{TableBorder('┌', '┬', '┐', colWidths)}{rst}");

        if (headerCells.Count > 0)
        {
            result.Add(FormatTableRow(headerCells, colWidths, alignments, colorMode, theme, isHeader: true));
            result.Add($"{dimColor}{TableBorder('├', '┼', '┤', colWidths)}{rst}");
        }

        foreach (var row in dataRows)
            result.Add(FormatTableRow(row, colWidths, alignments, colorMode, theme, isHeader: false));

        result.Add($"{dimColor}{TableBorder('└', '┴', '┘', colWidths)}{rst}");
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

    private static string FormatTableRow(List<string> cells, int[] colWidths,
        Alignment[] alignments, ColorMode colorMode, MarkdownTheme theme, bool isHeader)
    {
        var sb = new StringBuilder();
        var dimColor = Resolve(theme.Dim, colorMode);
        var rst = Rst(colorMode);
        var boldAttr = BoldAttr(colorMode);

        sb.Append($"{dimColor}│{rst}");
        for (var col = 0; col < colWidths.Length; col++)
        {
            var rawText = col < cells.Count ? cells[col] : "";
            var formatted = FormatInline(rawText, colorMode, theme);
            if (isHeader) formatted = $"{boldAttr}{formatted}{rst}";

            var aligned = AlignCell(formatted, rawText.Length, colWidths[col],
                col < alignments.Length ? alignments[col] : Alignment.Left);
            sb.Append($" {aligned} {dimColor}│{rst}");
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
    /// </summary>
    internal static string FormatInline(string text, ColorMode colorMode, MarkdownTheme? theme = null)
    {
        theme ??= MarkdownTheme.Default;
        var doc = Markdown.Parse(text, Pipeline);
        if (doc.FirstOrDefault() is ParagraphBlock para && para.Inline is not null)
            return FormatInlinesFromAst(para.Inline, bold: false, italic: false, colorMode, theme);
        return text;
    }

    private static string FormatInlinesFromAst(ContainerInline container, bool bold, bool italic,
        ColorMode colorMode, MarkdownTheme theme)
    {
        var sb = new StringBuilder();
        RenderInlines(container, sb, bold, italic, colorMode, theme);
        return sb.ToString();
    }

    private static void RenderInlines(ContainerInline container, StringBuilder sb,
        bool bold, bool italic, ColorMode colorMode, MarkdownTheme theme)
    {
        var rst = Rst(colorMode);
        var boldAttr = BoldAttr(colorMode);
        var italicAttr = ItalicAttr(colorMode);

        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    sb.Append(literal.Content);
                    break;

                case ColorInline colorInline:
                {
                    var fg = Resolve(colorInline.Color, colorMode);
                    sb.Append(rst);
                    sb.Append(fg);
                    RenderInlines(colorInline, sb, false, false, colorMode, theme);
                    sb.Append(rst);
                    // Restore parent style
                    if (bold) sb.Append(boldAttr);
                    if (italic) sb.Append(italicAttr);
                    break;
                }

                case EmphasisInline emphasis:
                {
                    var newBold = bold || emphasis.DelimiterCount >= 2;
                    var newItalic = italic || emphasis.DelimiterCount == 1 || emphasis.DelimiterCount >= 3;

                    sb.Append(rst);
                    if (newBold) sb.Append(boldAttr);
                    if (newItalic) sb.Append(italicAttr);

                    RenderInlines(emphasis, sb, newBold, newItalic, colorMode, theme);

                    sb.Append(rst);
                    if (bold) sb.Append(boldAttr);
                    if (italic) sb.Append(italicAttr);
                    break;
                }

                case LinkInline link:
                    var linkColor = Resolve(theme.Link, colorMode);
                    var dimColor = Resolve(theme.Dim, colorMode);
                    sb.Append($"{UnderlineAttr(colorMode)}{linkColor}");
                    RenderInlines(link, sb, false, false, colorMode, theme);
                    sb.Append(rst);
                    if (!string.IsNullOrEmpty(link.Url))
                        sb.Append($"{dimColor} ({link.Url}){rst}");
                    if (bold) sb.Append(boldAttr);
                    if (italic) sb.Append(italicAttr);
                    break;

                case LineBreakInline:
                    sb.Append(' ');
                    break;
            }
        }
    }

    // ── Mode-aware attribute helpers ──────────────────────────────────

    private static string Resolve(DIR.Lib.RGBAColor32 color, ColorMode mode) =>
        MarkdownTheme.Resolve(color, mode);

    private static string Rst(ColorMode mode) => mode == ColorMode.None ? "" : Reset;
    private static string BoldAttr(ColorMode mode) => mode == ColorMode.None ? "" : Bold;
    private static string ItalicAttr(ColorMode mode) => mode == ColorMode.None ? "" : ItalicCode;
    private static string UnderlineAttr(ColorMode mode) => mode == ColorMode.None ? "" : Underline;

    // ── Word wrapping (ANSI-aware) ────────────────────────────────────

    /// <summary>
    /// Wraps text containing VT escape sequences at word boundaries.
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
