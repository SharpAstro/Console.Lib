using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using DIR.Lib;
using DIR.Lib.MathLayout;

namespace Console.Lib;

/// <summary>
/// Phase D: LALR.CC-driven rendering path. Parallels the existing
/// Markdig-based <see cref="MarkdownRenderer.RenderLines"/> but uses
/// <see cref="MarkdownBlockVisitor"/> for block parsing and walks the
/// resulting <see cref="MdBlock"/> / <see cref="MdInline"/> tree
/// directly. Reuses the existing theme / VT / word-wrap helpers from
/// the Markdig path (kept in the partial class) so the output should
/// be byte-identical for the common-case inputs.
///
/// <para>Switch via <see cref="RenderLinesLalr"/> for now; Phase E
/// runs the existing 327-test suite against this entry point to
/// prove parity, then Phase F flips <see cref="MarkdownRenderer.RenderLines"/>
/// to call this path by default and deletes the Markdig dependency.</para>
/// </summary>
public static partial class MarkdownRenderer
{
    /// <summary>Renders Markdown via the LALR.CC inline + block
    /// grammars. Same signature as <see cref="RenderLines"/>; output
    /// should match byte-for-byte for the cases both paths cover.</summary>
    public static List<string> RenderLinesLalr(string markdown, int width,
        ColorMode colorMode = ColorMode.TrueColor, MarkdownTheme? theme = null,
        BoxRenderMode? mathMode = null, string? mathFontPath = null)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return new List<string>();

        theme ??= MarkdownTheme.Default;
        var blocks = s_blockVisitor.Parse(markdown);
        var result = new List<string>();
        var first = true;

        foreach (var block in blocks)
        {
            if (!first) result.Add(string.Empty);
            RenderMdBlock(block, width, colorMode, theme, result, mathMode, mathFontPath);
            first = false;
        }

        return result;
    }

    private static readonly MarkdownBlockVisitor s_blockVisitor = new();

    // ── Block dispatch ────────────────────────────────────────────────

    private static void RenderMdBlock(MdBlock block, int width, ColorMode colorMode,
        MarkdownTheme theme, List<string> result,
        BoxRenderMode? mathMode, string? mathFontPath)
    {
        switch (block)
        {
            case MdHeading h:
                RenderMdHeading(h, width, colorMode, theme, result);
                break;
            case MdThematicBreak:
                result.Add($"{Resolve(theme.Dim, colorMode)}{new string('─', width)}{Rst(colorMode)}");
                break;
            case MdParagraph p:
                {
                    var sb = new StringBuilder();
                    RenderMdInlines(p.Content, sb, bold: false, italic: false, colorMode, theme);
                    result.AddRange(WordWrap(sb.ToString(), width));
                    break;
                }
            case MdCodeFence f:
                RenderMdCodeFence(f, width, colorMode, theme, result);
                break;
            case MdMathBlock m:
                RenderMdMathBlock(m, width, colorMode, theme, result, mathMode, mathFontPath);
                break;
            case MdList l:
                RenderMdList(l, width, colorMode, theme, result, mathMode, mathFontPath, nestLevel: 0);
                break;
            case MdTable t:
                RenderMdTable(t, colorMode, theme, result);
                break;
        }
    }

    private static void RenderMdHeading(MdHeading h, int width, ColorMode colorMode,
        MarkdownTheme theme, List<string> result)
    {
        var color = h.Level switch
        {
            1 => Resolve(theme.Heading1, colorMode),
            2 => Resolve(theme.Heading2, colorMode),
            _ => Resolve(theme.Heading3, colorMode),
        };
        var sb = new StringBuilder();
        RenderMdInlines(h.Content, sb, bold: false, italic: false, colorMode, theme);
        var text = $"{BoldAttr(colorMode)}{color}{sb}{Rst(colorMode)}";
        result.AddRange(WordWrap(text, width));
    }

    private static void RenderMdCodeFence(MdCodeFence f, int width, ColorMode colorMode,
        MarkdownTheme theme, List<string> result)
    {
        var codeColor = Resolve(theme.Code, colorMode);
        var dimColor = Resolve(theme.Dim, colorMode);
        var rst = Rst(colorMode);
        var lang = f.Lang ?? string.Empty;
        if (!string.IsNullOrEmpty(lang))
        {
            var prefix = "── ";
            var tag = $" {lang} ";
            var fillLen = System.Math.Max(0, width - prefix.Length - tag.Length);
            result.Add($"{dimColor}{prefix}{tag}{new string('─', fillLen)}{rst}");
        }
        else
        {
            result.Add($"{dimColor}{new string('─', width)}{rst}");
        }
        foreach (var line in f.Lines)
            result.Add($"  {codeColor}{line}{rst}");
        result.Add($"{dimColor}{new string('─', width)}{rst}");
    }

    private static void RenderMdMathBlock(MdMathBlock m, int width, ColorMode colorMode,
        MarkdownTheme theme, List<string> result,
        BoxRenderMode? mathMode, string? mathFontPath)
    {
        // Pixel-mode rendering tries the BoxRenderer path; if it can't
        // build (no math font, no LaTeX parser support, etc.) we fall
        // through to the Unicode rendering already on m.Unicode.
        if (mathMode is { } mode && TryRenderMathBox(m.Source, mode, mathFontPath, result))
            return;

        var mathColor = Resolve(theme.Math, colorMode);
        var rst = Rst(colorMode);
        foreach (var line in (m.Unicode ?? string.Empty).Split('\n'))
            result.AddRange(WordWrap($"  {mathColor}{line}{rst}", width, "  "));
    }

    private static void RenderMdList(MdList list, int width, ColorMode colorMode,
        MarkdownTheme theme, List<string> result,
        BoxRenderMode? mathMode, string? mathFontPath, int nestLevel)
    {
        var bulletColor = Resolve(theme.Bullet, colorMode);
        var dimColor = Resolve(theme.Dim, colorMode);
        var rst = Rst(colorMode);
        var indent = new string(' ', 2 + nestLevel * 2);
        var contIndent = new string(' ', indent.Length + 3);
        var itemNum = list.OrderedStart;

        foreach (var item in list.Items)
        {
            var marker = list.Ordered
                ? $"{dimColor}{itemNum}.{rst}"
                : $"{bulletColor}•{rst}";
            itemNum++;

            // First block of the list item — usually a paragraph.
            // Subsequent blocks (nested lists, etc.) get indented under
            // the same item.
            bool firstBlock = true;
            foreach (var body in item.Body)
            {
                if (body is MdParagraph para)
                {
                    var sb = new StringBuilder();
                    RenderMdInlines(para.Content, sb, bold: false, italic: false, colorMode, theme);
                    var prefix = firstBlock ? $"{indent}{marker} " : contIndent;
                    var wrapped = WordWrap($"{prefix}{sb}", width, contIndent);
                    result.AddRange(wrapped);
                }
                else if (body is MdList nestedList)
                {
                    RenderMdList(nestedList, width, colorMode, theme, result, mathMode, mathFontPath, nestLevel + 1);
                }
                firstBlock = false;
            }
        }
    }

    private static void RenderMdTable(MdTable t, ColorMode colorMode,
        MarkdownTheme theme, List<string> result)
    {
        var dimColor = Resolve(theme.Dim, colorMode);
        var rst = Rst(colorMode);
        var bold = BoldAttr(colorMode);

        // Column widths derived from header + body cell visible widths.
        int columns = t.Headers.Count;
        int[] widths = new int[columns];
        for (int c = 0; c < columns; c++)
            widths[c] = VisibleLength(FormatInlinesToString(t.Headers[c], colorMode, theme));
        foreach (var row in t.Rows)
        {
            for (int c = 0; c < columns && c < row.Count; c++)
            {
                var w = VisibleLength(FormatInlinesToString(row[c], colorMode, theme));
                if (w > widths[c]) widths[c] = w;
            }
        }

        result.Add(BuildTableBorder(widths, dimColor, rst, top: true));
        result.Add(BuildTableRow(t.Headers, widths, t.Alignments, dimColor, rst, bold, isHeader: true, colorMode, theme));
        result.Add(BuildTableSeparator(widths, t.Alignments, dimColor, rst));
        foreach (var row in t.Rows)
            result.Add(BuildTableRow(row, widths, t.Alignments, dimColor, rst, bold: string.Empty, isHeader: false, colorMode, theme));
        result.Add(BuildTableBorder(widths, dimColor, rst, top: false));
    }

    private static string BuildTableBorder(int[] widths, string dimColor, string rst, bool top)
    {
        var sb = new StringBuilder();
        sb.Append(dimColor).Append(top ? '┌' : '└');
        for (int i = 0; i < widths.Length; i++)
        {
            sb.Append(new string('─', widths[i] + 2));
            sb.Append(i < widths.Length - 1 ? (top ? '┬' : '┴') : (top ? '┐' : '┘'));
        }
        sb.Append(rst);
        return sb.ToString();
    }

    private static string BuildTableSeparator(int[] widths, IReadOnlyList<MdTableAlignment> alignments, string dimColor, string rst)
    {
        var sb = new StringBuilder();
        sb.Append(dimColor).Append('├');
        for (int i = 0; i < widths.Length; i++)
        {
            sb.Append(new string('─', widths[i] + 2));
            sb.Append(i < widths.Length - 1 ? '┼' : '┤');
        }
        sb.Append(rst);
        return sb.ToString();
    }

    private static string BuildTableRow(IReadOnlyList<IReadOnlyList<MdInline>> cells, int[] widths,
        IReadOnlyList<MdTableAlignment> alignments, string dimColor, string rst, string bold,
        bool isHeader, ColorMode colorMode, MarkdownTheme theme)
    {
        var sb = new StringBuilder();
        sb.Append(dimColor).Append('│').Append(rst);
        for (int i = 0; i < widths.Length; i++)
        {
            var raw = i < cells.Count ? FormatInlinesToString(cells[i], colorMode, theme) : string.Empty;
            var formatted = isHeader ? $"{bold}{raw}{rst}" : raw;
            var aligned = AlignTableCell(formatted, VisibleLength(raw), widths[i], i < alignments.Count ? alignments[i] : MdTableAlignment.Left);
            sb.Append(' ').Append(aligned).Append(' ').Append(dimColor).Append('│').Append(rst);
        }
        return sb.ToString();
    }

    private static string AlignTableCell(string content, int visibleLength, int columnWidth, MdTableAlignment alignment)
    {
        var pad = System.Math.Max(0, columnWidth - visibleLength);
        return alignment switch
        {
            MdTableAlignment.Right => new string(' ', pad) + content,
            MdTableAlignment.Center => new string(' ', pad / 2) + content + new string(' ', pad - pad / 2),
            _ => content + new string(' ', pad),
        };
    }

    private static string FormatInlinesToString(IReadOnlyList<MdInline> inlines, ColorMode colorMode, MarkdownTheme theme)
    {
        var sb = new StringBuilder();
        RenderMdInlines(inlines, sb, bold: false, italic: false, colorMode, theme);
        return sb.ToString();
    }

    // ── Inline dispatch ───────────────────────────────────────────────

    private static void RenderMdInlines(IReadOnlyList<MdInline> inlines, StringBuilder sb,
        bool bold, bool italic, ColorMode colorMode, MarkdownTheme theme)
    {
        var rst = Rst(colorMode);
        var boldAttr = BoldAttr(colorMode);
        var italicAttr = ItalicAttr(colorMode);

        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case MdLiteral lit:
                    sb.Append(lit.Text);
                    break;

                case MdEmphasis em:
                    {
                        var newBold = bold || em.Level >= 2;
                        var newItalic = italic || em.Level == 1 || em.Level >= 3;
                        sb.Append(rst);
                        if (newBold) sb.Append(boldAttr);
                        if (newItalic) sb.Append(italicAttr);
                        RenderMdInlines(em.Content, sb, newBold, newItalic, colorMode, theme);
                        sb.Append(rst);
                        if (bold) sb.Append(boldAttr);
                        if (italic) sb.Append(italicAttr);
                        break;
                    }

                case MdLink link:
                    {
                        var linkColor = Resolve(theme.Link, colorMode);
                        var dimColor = Resolve(theme.Dim, colorMode);
                        sb.Append($"{UnderlineAttr(colorMode)}{linkColor}");
                        RenderMdInlines(link.Text, sb, bold: false, italic: false, colorMode, theme);
                        sb.Append(rst);
                        if (!string.IsNullOrEmpty(link.Url))
                            sb.Append($"{dimColor} ({link.Url}){rst}");
                        if (bold) sb.Append(boldAttr);
                        if (italic) sb.Append(italicAttr);
                        break;
                    }

                case MdColor color:
                    {
                        if (MarkdownTheme.TryParseColor(color.Color, out var rgba))
                        {
                            var fg = Resolve(rgba, colorMode);
                            sb.Append(rst).Append(fg);
                            RenderMdInlines(color.Text, sb, bold: false, italic: false, colorMode, theme);
                            sb.Append(rst);
                            if (bold) sb.Append(boldAttr);
                            if (italic) sb.Append(italicAttr);
                        }
                        else
                        {
                            // Unknown colour name — fall through to literal `[text]{name}`.
                            sb.Append('[');
                            RenderMdInlines(color.Text, sb, bold, italic, colorMode, theme);
                            sb.Append(']').Append('{').Append(color.Color).Append('}');
                        }
                        break;
                    }

                case MdLineBreak br:
                    sb.Append(br.Hard ? '\n' : ' ');
                    break;

                case MdCodeInline code:
                    {
                        var codeColor = Resolve(theme.Code, colorMode);
                        sb.Append(rst).Append(codeColor).Append(code.Content).Append(rst);
                        if (bold) sb.Append(boldAttr);
                        if (italic) sb.Append(italicAttr);
                        break;
                    }

                case MdMathInline math:
                    {
                        var mathColor = Resolve(theme.Math, colorMode);
                        sb.Append(rst).Append(mathColor).Append(math.Unicode).Append(rst);
                        if (bold) sb.Append(boldAttr);
                        if (italic) sb.Append(italicAttr);
                        break;
                    }
            }
        }
    }
}
