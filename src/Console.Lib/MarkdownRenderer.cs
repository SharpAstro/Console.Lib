using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using DIR.Lib;
using DIR.Lib.MathLayout;
using LALR.CC.LexicalGrammar;
using Markdig;
using Markdig.Extensions.Mathematics;
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
    /// Markdig pipeline with pipe-table, color-inline, and math (dollar-sign
    /// delimited) support enabled. Inline <c>$x$</c> and display <c>$$x$$</c>
    /// produce <see cref="MathInline"/> / <see cref="MathBlock"/> AST nodes;
    /// LaTeX-style <c>\(...\)</c> and <c>\[...\]</c> wrappers are converted
    /// to dollar-sign form in <see cref="PreProcessLatexWrappers"/> before
    /// parsing so Markdig classifies them too.
    /// </summary>
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseColorInlines()
        .UseMathematics()
        .Build();

    /// <summary>
    /// Cached math parser + lexer table. The LALR.CC source generator
    /// pre-bakes the parse table and lexer transitions at build time, so
    /// constructing these is just struct/array initialization — but we
    /// still hold the result so each math node doesn't pay the cost.
    /// </summary>
    private static readonly LatexUnicodeVisitor MathVisitor = new();
    private static readonly LALR.CC.Parser MathParser = Latex.BuildParser(MathVisitor);
    private static readonly System.Collections.Generic.Dictionary<string, LexRule[]> MathLexerTable = Latex.BuildLexer();

    /// <summary>
    /// Lazily resolved math-rendering font. <see cref="ResolveMathFont"/>
    /// picks the first existing candidate from a STIX-Math-preferred list;
    /// null means no usable font is installed (in which case pixel-rendered
    /// math falls back to the Unicode path).
    /// </summary>
    private static string? s_mathFontPath;
    private static bool s_mathFontResolved;

    /// <summary>
    /// Renders Markdown to the given <see cref="TextWriter"/>.
    /// </summary>
    /// <param name="mathMode">When non-null, display math (<c>$$...$$</c> /
    /// <c>\[...\]</c>) is pixel-rendered as sixel / sextant / half-block.
    /// Default null keeps display math on the single-row Unicode path —
    /// callers should set this only after confirming the terminal supports
    /// the chosen mode (e.g. via <see cref="VirtualTerminal.HasSixelSupport"/>).</param>
    public static void Render(string markdown, TextWriter output, int width,
        ColorMode colorMode = ColorMode.TrueColor, MarkdownTheme? theme = null,
        BoxRenderMode? mathMode = null)
    {
        foreach (var line in RenderLines(markdown, width, colorMode, theme, mathMode))
            output.WriteLine(line);
    }

    /// <summary>
    /// Renders Markdown to a list of pre-formatted VT lines suitable for widget rendering.
    /// </summary>
    /// <param name="mathMode">See <see cref="Render"/> for the math-mode semantics.</param>
    public static List<string> RenderLines(string markdown, int width,
        ColorMode colorMode = ColorMode.TrueColor, MarkdownTheme? theme = null,
        BoxRenderMode? mathMode = null)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return [];

        theme ??= MarkdownTheme.Default;
        var preprocessed = PreProcessLatexWrappers(markdown);
        var doc = Markdown.Parse(preprocessed, Pipeline);
        var result = new List<string>();
        var first = true;

        foreach (var block in doc)
        {
            if (!first) result.Add("");
            RenderBlock(block, width, colorMode, theme, result, nestLevel: 0, mathMode);
            first = false;
        }

        return result;
    }

    /// <summary>
    /// Rewrites LaTeX-style math wrappers (<c>\(...\)</c> and <c>\[...\]</c>)
    /// into Markdig's dollar-sign form (<c>$...$</c> and <c>$$...$$</c>) so
    /// the UseMathematics() extension picks them up as MathInline/MathBlock.
    /// Anything that fails to match is left untouched, which means a stray
    /// unmatched <c>\(</c> just renders as literal text — same as Markdig's
    /// usual behavior for malformed inlines.
    /// </summary>
    private static string PreProcessLatexWrappers(string markdown)
    {
        // Display first (longer delimiter) so it wins over inline on `\[...\]`.
        markdown = Regex.Replace(markdown, @"\\\[([\s\S]*?)\\\]", "$$$$$1$$$$");
        markdown = Regex.Replace(markdown, @"\\\(([\s\S]*?)\\\)", "$$$1$$");
        return markdown;
    }

    /// <summary>
    /// Parse + visit a LaTeX math source string through <see cref="LatexUnicodeVisitor"/>.
    /// Returns the rendered Unicode string, or the literal input wrapped in
    /// fallback markers on parse error — so a single mangled formula doesn't
    /// take down the surrounding markdown render.
    /// </summary>
    private static string RenderMathUnicode(string source)
    {
        if (string.IsNullOrWhiteSpace(source)) return string.Empty;
        try
        {
            using var lexer = BytesLexer.FromString(source, MathLexerTable);
            using var tokens = new SyncLATokenIterator(lexer);
            var item = MathParser.ParseInput(tokens, debugger: null);
            if (item.IsError) return source;
            return item.Content is string s ? s : source;
        }
        catch
        {
            return source;
        }
    }

    // ── Block rendering ───────────────────────────────────────────────

    private static void RenderBlock(Block block, int width, ColorMode colorMode,
        MarkdownTheme theme, List<string> result, int nestLevel,
        BoxRenderMode? mathMode = null)
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

            // MathBlock first — it extends FencedCodeBlock in Markdig, so the
            // more specific arm has to be listed before the general one or
            // C# pattern matching flags the second as unreachable.
            case MathBlock mathBlock:
                RenderMathBlock(mathBlock, width, colorMode, theme, result, mathMode);
                break;

            case FencedCodeBlock fenced:
                RenderFencedCodeBlock(fenced, width, colorMode, theme, result);
                break;
        }
    }

    private static void RenderFencedCodeBlock(FencedCodeBlock fenced, int width,
        ColorMode colorMode, MarkdownTheme theme, List<string> result)
    {
        var codeColor = Resolve(theme.Code, colorMode);
        var dimColor = Resolve(theme.Dim, colorMode);
        var rst = Rst(colorMode);

        // Top rule with optional language tag on the right edge.
        var lang = fenced.Info ?? string.Empty;
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

        // Body, code-colored, two-space indent so leading whitespace inside the
        // block is preserved without colliding with the bottom rule.
        // Iterate by Count — Markdig's StringLineGroup has a backing array
        // that can be larger than the live line count.
        for (var i = 0; i < fenced.Lines.Count; i++)
        {
            var s = fenced.Lines.Lines[i].ToString() ?? string.Empty;
            result.Add($"  {codeColor}{s}{rst}");
        }

        result.Add($"{dimColor}{new string('─', width)}{rst}");
    }

    private static void RenderMathBlock(MathBlock mathBlock, int width,
        ColorMode colorMode, MarkdownTheme theme, List<string> result,
        BoxRenderMode? mathMode)
    {
        // Concatenate the block's lines (Markdig's MathBlock can span multiple
        // lines between $$ fences) into a single source string. The grammar
        // can't parse multi-line constructs (\begin{align} etc.) — multi-line
        // display math falls back to literal text via the parse-error path.
        var sb = new StringBuilder();
        for (var i = 0; i < mathBlock.Lines.Count; i++)
        {
            if (sb.Length > 0) sb.Append(' ');
            var slice = mathBlock.Lines.Lines[i].ToString();
            if (slice is not null) sb.Append(slice);
        }
        var source = sb.ToString().Trim();

        // Try pixel rendering first when the caller asked for it. Falls back
        // to Unicode on any failure (font missing, parse error, layout error)
        // so a broken math block can't take down the rest of the document.
        if (mathMode is { } mode && TryRenderMathBox(source, mode, result))
            return;

        var mathColor = Resolve(theme.Math, colorMode);
        var rst = Rst(colorMode);
        var rendered = RenderMathUnicode(source);
        result.AddRange(WordWrap($"  {mathColor}{rendered}{rst}", width, "  "));
    }

    /// <summary>
    /// Parse + lay out + raster-render a math expression as pixels via
    /// <see cref="BoxBuildingVisitor"/> + <see cref="BoxRenderer"/>. Returns
    /// false (without writing to <paramref name="result"/>) if any step
    /// fails — font resolution, parse, layout, or render — so the caller
    /// can fall through to the Unicode path. The mode-specific font size
    /// matches what the LatexConsole example uses (sixel renders larger
    /// because its sub-pixels are smaller).
    /// </summary>
    private static bool TryRenderMathBox(string source, BoxRenderMode mode, List<string> result)
    {
        if (string.IsNullOrWhiteSpace(source)) return false;

        var fontPath = ResolveMathFont();
        if (string.IsNullOrEmpty(fontPath)) return false;

        try
        {
            var fontSize = mode switch
            {
                BoxRenderMode.Sixel     => 32f,
                BoxRenderMode.Sextant   => 12f,
                BoxRenderMode.HalfBlock => 10f,
                _                       => 12f,
            };
            var style = new BoxStyle(fontPath, fontSize);
            var visitor = new BoxBuildingVisitor(style);

            // Math expressions use a separate parser instance because each
            // BuildParser call binds a specific visitor; we can't share the
            // Unicode parser with a Box-typed visitor.
            var boxParser = Latex.BuildParser(visitor);
            using var lexer = BytesLexer.FromString(source, MathLexerTable);
            using var tokens = new SyncLATokenIterator(lexer);
            var item = boxParser.ParseInput(tokens, debugger: null);
            if (item.IsError || item.Content is not Box box) return false;

            using var sw = new StringWriter();
            BoxRenderer.Render(box, style, mode, sw);

            // Split into lines so the caller's surrounding layout (transcript
            // widget, scrollback, etc.) sees one entry per cell row. Sixel
            // collapses to a single entry because its DCS sequence doesn't
            // contain real newlines.
            foreach (var line in sw.ToString().Split('\n'))
            {
                if (line.Length > 0) result.Add(line);
            }
            return result.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Pick a font with good math/Greek coverage. STIX Two Math is the
    /// gold-standard for math typography (full math glyphs + Greek,
    /// OpenType MATH tables); Cambria, Consolas, DejaVu Sans Mono, and the
    /// platform's resolved system monospace are fallbacks when nothing
    /// better is installed. Returns null if even the platform fallback
    /// fails — pixel math then falls through to the Unicode renderer.
    /// Result is cached per process; the lookup runs once on first math
    /// block.
    /// </summary>
    private static string? ResolveMathFont()
    {
        if (s_mathFontResolved) return s_mathFontPath;

        string[] candidates;
        if (OperatingSystem.IsWindows())
            candidates =
            [
                @"C:\Windows\Fonts\STIXTwoMath-Regular.otf",
                @"C:\Windows\Fonts\cambria.ttc",
                @"C:\Windows\Fonts\consola.ttf",
                @"C:\Windows\Fonts\cour.ttf",
            ];
        else if (OperatingSystem.IsMacOS())
            candidates =
            [
                "/Library/Fonts/STIXTwoMath-Regular.otf",
                "/System/Library/Fonts/Menlo.ttc",
                "/System/Library/Fonts/Monaco.dfont",
            ];
        else
            candidates =
            [
                "/usr/share/fonts/opentype/stix/STIXTwoMath-Regular.otf",
                "/usr/share/fonts/truetype/dejavu/DejaVuSansMono.ttf",
                "/usr/share/fonts/TTF/DejaVuSansMono.ttf",
            ];

        foreach (var p in candidates)
        {
            if (File.Exists(p)) { s_mathFontPath = p; s_mathFontResolved = true; return p; }
        }

        try { s_mathFontPath = FontResolver.ResolveSystemFont(); }
        catch { s_mathFontPath = null; }
        s_mathFontResolved = true;
        return s_mathFontPath;
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

                case CodeInline code:
                {
                    var codeColor = Resolve(theme.Code, colorMode);
                    sb.Append(rst);
                    sb.Append(codeColor);
                    sb.Append(code.Content);
                    sb.Append(rst);
                    if (bold) sb.Append(boldAttr);
                    if (italic) sb.Append(italicAttr);
                    break;
                }

                case MathInline math:
                {
                    var mathColor = Resolve(theme.Math, colorMode);
                    var rendered = RenderMathUnicode(math.Content.ToString());
                    sb.Append(rst);
                    sb.Append(mathColor);
                    sb.Append(rendered);
                    sb.Append(rst);
                    if (bold) sb.Append(boldAttr);
                    if (italic) sb.Append(italicAttr);
                    break;
                }
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
    public static int VisibleLength(string text)
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
