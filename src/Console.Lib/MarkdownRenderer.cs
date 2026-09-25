using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using DIR.Lib;
using DIR.Lib.MathLayout;
using DIR.Lib.Markdown;
using LALR.CC.LexicalGrammar;

namespace Console.Lib;

/// <summary>
/// Renders Markdown text to VT-styled terminal output via an LALR.CC
/// inline + block grammar (see <c>markdown-inline.lalr.yaml</c> and
/// <c>markdown-block.lalr.yaml</c>) with the LaTeX math grammar
/// (<c>latex.lalr.yaml</c>) invoked as a sub-parser on math bodies.
/// Supports headers, bold, italic, links (with OSC 8 hyperlinks),
/// tables, lists, horizontal rules, fenced code, inline + display
/// math (Unicode / sixel / sextant / half-block), and colored text
/// via the <c>[text]{color}</c> syntax.
/// <para>
/// All colors are resolved at render time through <see cref="MarkdownTheme"/> to respect
/// the active <see cref="ColorMode"/>. Use <see cref="ColorMode.None"/> to suppress all escapes.
/// </para>
/// </summary>
public static partial class MarkdownRenderer
{
    // ── VT attribute constants (mode-independent) ─────────────────────

    private const string Bold = "\e[1m";
    private const string ItalicCode = "\e[3m";
    private const string Underline = "\e[4m";
    private const string Reset = "\e[0m";


    /// <summary>
    /// Renders Markdown to the given <see cref="TextWriter"/>.
    /// </summary>
    /// <param name="mathMode">When non-null, display math (<c>$$...$$</c> /
    /// <c>\[...\]</c>) is pixel-rendered as sixel / sextant / half-block.
    /// Default null keeps display math on the single-row Unicode path —
    /// callers should set this only after confirming the terminal supports
    /// the chosen mode (e.g. via <see cref="VirtualTerminal.HasSixelSupport"/>).</param>
    /// <param name="mathFontPath">Path to an OpenType math font (e.g. STIX Two
    /// Math) for the pixel-render path. The caller decides discovery — apps
    /// typically pass a path co-located with their executable. When null or
    /// not-found, the renderer falls back to a small system-font search and
    /// then Unicode rendering.</param>
    /// <param name="images">When non-null, an image alone on its own line is
    /// rasterized (Sixel / sextant / half-block per <see cref="MarkdownImageOptions.Mode"/>)
    /// using the supplied resolver. Default null renders every image as alt text.
    /// Images that appear mid-paragraph always render as alt text.</param>
    /// <param name="linkResolver">Maps a Markdown link's raw <c>url</c> (from
    /// <c>[text](url)</c>) to the URI actually placed in the OSC 8 hyperlink target.
    /// Default null emits the raw url unchanged. A bare relative href (e.g.
    /// <c>docs/foo.md</c>) is not a valid absolute URI, so terminals reject it as a
    /// clickable target ("invalid link" in Windows Terminal) — a host that knows the
    /// document's own path can supply a resolver that turns such hrefs into
    /// <c>file://</c> URIs. The renderer never resolves paths itself, mirroring
    /// <paramref name="images"/>'s <see cref="MarkdownImageOptions.Resolver"/>. The
    /// visible <c>(url)</c> text after the label always shows the original,
    /// unresolved href.</param>
    public static void Render(string markdown, TextWriter output, int width,
        ColorMode colorMode = ColorMode.TrueColor, MarkdownTheme? theme = null,
        BoxRenderMode? mathMode = null, string? mathFontPath = null,
        MarkdownImageOptions? images = null, Func<string, string>? linkResolver = null)
    {
        foreach (var line in RenderLines(markdown, width, colorMode, theme, mathMode, mathFontPath, images, linkResolver))
            output.WriteLine(line);
    }

    /// <summary>
    /// Renders Markdown to a list of pre-formatted VT lines suitable for widget rendering.
    /// </summary>
    /// <param name="mathMode">See <see cref="Render"/> for the math-mode semantics.</param>
    /// <param name="mathFontPath">See <see cref="Render"/> for the math-font semantics.</param>
    /// <param name="images">See <see cref="Render"/> for the image semantics.</param>
    /// <param name="linkResolver">See <see cref="Render"/> for the link-resolution semantics.</param>
    public static List<string> RenderLines(string markdown, int width,
        ColorMode colorMode = ColorMode.TrueColor, MarkdownTheme? theme = null,
        BoxRenderMode? mathMode = null, string? mathFontPath = null,
        MarkdownImageOptions? images = null, Func<string, string>? linkResolver = null)
        => RenderLinesCore(markdown, width, breakLongWords: false, colorMode, theme, mathMode, mathFontPath, images, linkResolver);

    /// <summary>
    /// Renders Markdown to VT lines, choosing what happens to a word too long for a line of its own.
    /// <para>
    /// Left whole (<c>false</c>, what the other overload does) it overflows the width, which suits output
    /// headed for a terminal's scrollback: the terminal soft-wraps the line and rejoins it on copy, so a
    /// long URL stays copyable. Broken (<c>true</c>) it stays inside the width, split after a hyphen or
    /// slash where one falls and mid-character otherwise, which suits a fixed-width surface that clips what
    /// overflows. <see cref="MarkdownWidget"/> breaks. Tables always fit the width either way; a table
    /// cannot overflow without the terminal wrapping its borders too.
    /// </para>
    /// </summary>
    /// <param name="breakLongWords">Break a word too long for a line instead of letting it overflow.</param>
    /// <inheritdoc cref="RenderLines(string, int, ColorMode, MarkdownTheme?, BoxRenderMode?, string?, MarkdownImageOptions?, Func{string, string}?)"/>
    public static List<string> RenderLines(string markdown, int width, bool breakLongWords,
        ColorMode colorMode = ColorMode.TrueColor, MarkdownTheme? theme = null,
        BoxRenderMode? mathMode = null, string? mathFontPath = null,
        MarkdownImageOptions? images = null, Func<string, string>? linkResolver = null)
        => RenderLinesCore(markdown, width, breakLongWords, colorMode, theme, mathMode, mathFontPath, images, linkResolver);

    /// <summary>
    /// Rasters display math into <paramref name="result"/>, no wider than <paramref name="width"/> cells.
    /// False, having added nothing, when it cannot, and the caller then takes the Unicode path.
    /// <para>
    /// A formula wider than the width is re-laid at a smaller font size until it fits, down to two thirds
    /// of the encoding's default. Below that a raster stops being legible, while the Unicode path wraps,
    /// so a formula that still does not fit is left to that path. The raster used to be emitted at its
    /// natural width whatever the width was: 135 cells for a long sum at a width of 30.
    /// </para>
    /// </summary>
    /// <param name="cellPixelWidth">
    /// Pixels per cell, which is what turns a Sixel raster's width into cells. Sextant and half-block
    /// have a fixed number of pixels per cell and ignore it.
    /// </param>
    private static bool TryRenderMathBox(string source, BoxRenderMode mode,
        string? callerFontPath, int width, int cellPixelWidth, List<string> result)
    {
        if (string.IsNullOrWhiteSpace(source)) return false;

        // Pre-process the source so the math grammar can swallow it. Things
        // we can fix up source-side (rendered the same as Unicode path):
        //   - LaTeX aliases: \dfrac/\tfrac → \frac, \left[ → [, …
        //   - \boxed{X}     → X  (strip the wrapper; v1 has no boxed frame)
        //   - \ce{X}        → Mhchem.ToLatex(X)  (chem → LaTeX math source,
        //                     so chem picks up the same box layout as math —
        //                     Phase-2 mhchem)
        //   - \, \; \! \\   → literal whitespace (lexer ignores it)
        //
        // Things we still can't do in box mode (visitor-side, would need new
        // Box types):
        //   - \text{X}      → no upright-text run box yet → fall back to Unicode
        //   - \begin{}/end{} → no multi-line table layout → fall back to Unicode
        if (MarkdownMacros.ContainsMacro(source, "text") || source.Contains(@"\begin", StringComparison.Ordinal))
            return false;

        source = MarkdownMacros.NormalizeLatexAliases(source);
        source = MarkdownMacros.ExpandBalancedMacro(source, "boxed", inner => inner);
        source = MarkdownMacros.ExpandBalancedMacro(source, "ce", inner => Mhchem.ToLatex(inner));
        // Recheck after \boxed / \ce expansion — either body could have
        // introduced \text (chem doesn't today, but the door's open in
        // case future Mhchem.ToLatex emits \text{l} / \text{aq} for
        // state markers once \mathrm/\text gain box-visitor support).
        if (MarkdownMacros.ContainsMacro(source, "text") || source.Contains(@"\begin", StringComparison.Ordinal))
            return false;
        source = MarkdownMacros.ResolveBackslashEscapes(source);

        // Font resolution. If the caller passed a path (apps typically pick
        // something co-located with their executable so the library doesn't
        // have to know about AppContext or assembly-location quirks), trust
        // it. Otherwise fall back to a small built-in system-font search.
        string? fontPath = !string.IsNullOrEmpty(callerFontPath) && File.Exists(callerFontPath)
            ? callerFontPath
            : MarkdownMacros.ResolveMathFont();
        if (string.IsNullOrEmpty(fontPath)) return false;

        try
        {
            var defaultSize = mode switch
            {
                BoxRenderMode.Sixel     => 32f,
                BoxRenderMode.Sextant   => 12f,
                BoxRenderMode.HalfBlock => 10f,
                _                       => 12f,
            };
            var minimumSize = defaultSize * 2 / 3;
            var fontSize = defaultSize;

            while (true)
            {
                var style = new BoxStyle(fontPath, fontSize);
                if (Build(style) is not { } box) return false;
                var image = BoxRasterizer.RenderToRgba(box, style);
                if (image.Width <= 0 || image.Height <= 0) return false;

                var cells = mode switch
                {
                    BoxRenderMode.Sixel   => (image.Width + cellPixelWidth - 1) / Math.Max(1, cellPixelWidth),
                    BoxRenderMode.Sextant => (image.Width + 1) / 2,
                    _                     => image.Width,
                };
                if (cells > width && width > 0)
                {
                    // Shrink in proportion, and by at least half a point so the loop always moves. The
                    // layout is not exactly linear in the size (rules and spacing have minimums), so the
                    // next pass measures again rather than trusting the ratio.
                    var next = Math.Min(fontSize * width / cells, fontSize - 0.5f);
                    if (next < minimumSize) return false;
                    fontSize = next;
                    continue;
                }

                using var sw = new StringWriter();
                BoxRenderer.EncodeImage(image.Pixels, image.Width, image.Height, mode, sw);

                // Split into lines so the caller's surrounding layout (transcript
                // widget, scrollback, etc.) sees one entry per cell row. Sixel
                // collapses to a single entry because its DCS sequence doesn't
                // contain real newlines. StringWriter.WriteLine emits the platform
                // newline, so on Windows each line would keep a '\r' that counts
                // as visible width; trim it as the image path does.
                var any = false;
                foreach (var raw in sw.ToString().Split('\n'))
                {
                    var line = raw.TrimEnd('\r');
                    if (line.Length > 0) { result.Add(line); any = true; }
                }
                return any;
            }
        }
        catch
        {
            return false;
        }

        Box? Build(BoxStyle style)
        {
            // Math expressions use a separate parser instance because each
            // BuildParser call binds a specific visitor; we can't share the
            // Unicode parser with a Box-typed visitor. The visitor is bound to
            // a style too, so a pass at another size parses afresh.
            var visitor = new BoxBuildingVisitor(style);
            var boxParser = Latex.BuildParser(visitor);
            using var lexer = BytesLexer.FromString(source, MarkdownMacros.MathLexerTable);
            using var tokens = new SyncLATokenIterator(lexer);
            var item = boxParser.ParseInput(tokens, debugger: null);
            return item.IsError || item.Content is not Func<BoxStyle, Box> builder ? null : builder(style);
        }
    }

    internal static string FormatInline(string text, ColorMode colorMode, MarkdownTheme? theme = null)
    {
        theme ??= MarkdownTheme.Default;
        var inlines = s_formatInlineVisitor.Parse(text);
        if (inlines.Count == 0) return text;
        var sb = new StringBuilder();
        RenderMdInlines(inlines, sb, bold: false, italic: false, colorMode, theme);
        return sb.ToString();
    }

    private static readonly MarkdownInlineVisitor s_formatInlineVisitor = new();


    // ── Mode-aware attribute helpers ──────────────────────────────────

    private static string Resolve(DIR.Lib.RGBAColor32 color, ColorMode mode) =>
        MarkdownTheme.Resolve(color, mode);

    private static string Rst(ColorMode mode) => mode == ColorMode.None ? "" : Reset;
    private static string BoldAttr(ColorMode mode) => mode == ColorMode.None ? "" : Bold;
    private static string ItalicAttr(ColorMode mode) => mode == ColorMode.None ? "" : ItalicCode;
    private static string UnderlineAttr(ColorMode mode) => mode == ColorMode.None ? "" : Underline;

    // Selective SGR unset codes — clear one attribute without touching
    // the others. Important inside nested spans like `[**bold**](url)`
    // where the inner emphasis must drop its bold without killing the
    // outer link's underline + colour. `\e[0m` (full reset) would
    // collapse all the parent state.
    private const string NoBold = "\e[22m";
    private const string NoItalic = "\e[23m";
    private const string NoUnderline = "\e[24m";
    internal static string NoBoldAttr(ColorMode mode) => mode == ColorMode.None ? "" : NoBold;
    internal static string NoItalicAttr(ColorMode mode) => mode == ColorMode.None ? "" : NoItalic;
    internal static string NoUnderlineAttr(ColorMode mode) => mode == ColorMode.None ? "" : NoUnderline;

    /// <summary>
    /// OSC 8 hyperlink sequences. Wrap a piece of rendered text so
    /// supporting terminals (Windows Terminal, iTerm2, WezTerm, kitty,
    /// mintty, GNOME Terminal, VS Code's integrated terminal, etc.)
    /// turn it into a clickable hyperlink targeting <paramref name="url"/>.
    /// The sequences themselves live in <see cref="Osc8"/>, which is also
    /// what <see cref="CellBuffer"/> parses back.
    /// </summary>
    /// <returns>(opener, closer) pair. Empty strings if
    /// <paramref name="mode"/> is <see cref="ColorMode.None"/> or
    /// <paramref name="url"/> is empty — non-supporting terminals
    /// typically swallow unknown OSC sequences silently but skipping
    /// them outright in plain-text mode keeps the output free of
    /// any control bytes.</returns>
    internal static (string Open, string Close) Hyperlink(string? url, ColorMode mode)
    {
        if (mode == ColorMode.None || string.IsNullOrEmpty(url)) return ("", "");
        return (Osc8.Open(url), Osc8.Close);
    }

    // ── Word wrapping (ANSI-aware) ────────────────────────────────────

    /// <summary>
    /// Wraps text containing VT escape sequences at word boundaries.
    /// <para>
    /// The SGR run in force at a break is re-stated at the start of the next line. An OSC 8 hyperlink
    /// open at a break is closed at the end of the line and re-opened on the next, so the line break and
    /// the continuation indent are not part of the link.
    /// </para>
    /// <para>
    /// A word too long for a line of its own is left whole by default, and overflows. Printed to a
    /// terminal's scrollback that is the right answer: the terminal soft-wraps the line and rejoins it on
    /// copy, which is what keeps a long URL copyable. With <paramref name="breakLongWords"/> the word is
    /// broken instead — after a hyphen or slash where one falls, mid-character only where a piece is
    /// itself too long — which is right for a fixed-width surface such as <see cref="MarkdownWidget"/>
    /// that clips whatever overflows.
    /// </para>
    /// <para>
    /// Leading spaces start the first line, as they would have without the wrap: a list item arrives as
    /// <c>"  • item"</c>, and a wrapped item must keep that indent as an unwrapped one does. Every later line
    /// starts with <paramref name="continuationIndent"/> instead.
    /// </para>
    /// </summary>
    internal static List<string> WordWrap(string text, int maxWidth, string continuationIndent = "",
        bool breakLongWords = false)
    {
        if (maxWidth <= 0 || VisibleLength(text) <= maxWidth)
            return [text];

        var words = VtWords.Split(text);
        if (words.Count == 0) return [""];

        var result = new List<string>();
        var line = new StringBuilder();
        var lineVisWidth = 0;
        var indentWidth = VisibleLength(continuationIndent);
        var styles = new StringBuilder();
        string? link = null;
        var needSpace = false;
        // Whether the current line holds a visible word yet. Only then may it break: a line holding
        // nothing but its indent would be emitted blank.
        var hasContent = false;

        // The splitter drops leading spaces along with every other run of them, so restore them here.
        var lead = 0;
        while (lead < text.Length && text[lead] == ' ') lead++;
        line.Append(' ', lead);
        lineVisWidth = lead;

        foreach (var word in words)
        {
            var wordVisWidth = VisibleLength(word);
            var spaceNeeded = needSpace ? 1 : 0;

            if (lineVisWidth + spaceNeeded + wordVisWidth > maxWidth && hasContent)
            {
                NewLine();
            }

            if (needSpace)
            {
                line.Append(' ');
                lineVisWidth++;
            }

            if (breakLongWords && lineVisWidth + wordVisWidth > maxWidth)
            {
                // Only reached at the start of a line: a word that would not fit after others already
                // moved to a fresh one above. A segment that fits a fresh line goes whole; one that
                // does not fills the line it is on, a character at a time.
                foreach (var segment in VtWords.Segments(word))
                {
                    var segmentWidth = VisibleLength(segment);
                    if (hasContent && lineVisWidth + segmentWidth > maxWidth && indentWidth + segmentWidth <= maxWidth)
                    {
                        NewLine();
                    }

                    if (lineVisWidth + segmentWidth <= maxWidth)
                    {
                        Place(segment, segmentWidth);
                        continue;
                    }

                    foreach (var glyph in VtWords.Glyphs(segment))
                    {
                        var glyphWidth = VisibleLength(glyph);
                        if (hasContent && lineVisWidth + glyphWidth > maxWidth)
                        {
                            NewLine();
                        }
                        Place(glyph, glyphWidth);
                    }
                }
            }
            else
            {
                Place(word, wordVisWidth);
            }
            needSpace = true;
        }

        if (line.Length > 0)
            result.Add(line.ToString());

        return result;

        void NewLine()
        {
            if (link is not null) line.Append(Osc8.Close);
            result.Add(line.ToString());
            line.Clear();
            line.Append(continuationIndent);
            if (link is not null) line.Append(link);
            line.Append(styles);
            lineVisWidth = indentWidth;
            needSpace = false;
            hasContent = false;
        }

        void Place(string piece, int pieceWidth)
        {
            line.Append(piece);
            lineVisWidth += pieceWidth;
            hasContent |= pieceWidth > 0;
            UpdateStyles(piece, styles, ref link);
        }
    }

    /// <summary>
    /// Follows the SGR run and the OSC 8 link through <paramref name="text"/>, so a break can re-state
    /// them. Only SGR goes into <paramref name="styles"/>: an OSC 8 open is not a style that a reset
    /// clears, and replaying a close would be a no-op at best.
    /// </summary>
    private static void UpdateStyles(string text, StringBuilder styles, ref string? link)
    {
        var i = 0;
        while (i < text.Length)
        {
            if (text[i] == '\e')
            {
                var length = VtEscape.Length(text, i);
                var seq = text.Substring(i, length);
                if (Osc8.IsHyperlink(seq, out var opens))
                {
                    link = opens ? seq : null;
                }
                else if (seq.StartsWith("\e[", StringComparison.Ordinal) && seq.EndsWith('m'))
                {
                    if (seq == Reset)
                        styles.Clear();
                    else
                        styles.Append(seq);
                }
                i += length;
                continue;
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
        var i = 0;
        while (i < text.Length)
        {
            if (text[i] == '\e') { i += VtEscape.Length(text, i); continue; }
            len++;
            i++;
        }
        return len;
    }
}
