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
    public static void Render(string markdown, TextWriter output, int width,
        ColorMode colorMode = ColorMode.TrueColor, MarkdownTheme? theme = null,
        BoxRenderMode? mathMode = null, string? mathFontPath = null)
    {
        foreach (var line in RenderLines(markdown, width, colorMode, theme, mathMode, mathFontPath))
            output.WriteLine(line);
    }

    /// <summary>
    /// Renders Markdown to a list of pre-formatted VT lines suitable for widget rendering.
    /// </summary>
    /// <param name="mathMode">See <see cref="Render"/> for the math-mode semantics.</param>
    /// <param name="mathFontPath">See <see cref="Render"/> for the math-font semantics.</param>
    public static List<string> RenderLines(string markdown, int width,
        ColorMode colorMode = ColorMode.TrueColor, MarkdownTheme? theme = null,
        BoxRenderMode? mathMode = null, string? mathFontPath = null)
        => RenderLinesLalr(markdown, width, colorMode, theme, mathMode, mathFontPath);

    private static bool TryRenderMathBox(string source, BoxRenderMode mode,
        string? callerFontPath, List<string> result)
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
            using var lexer = BytesLexer.FromString(source, MarkdownMacros.MathLexerTable);
            using var tokens = new SyncLATokenIterator(lexer);
            var item = boxParser.ParseInput(tokens, debugger: null);
            if (item.IsError || item.Content is not Func<BoxStyle, Box> builder) return false;
            var box = builder(style);

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
    /// Format is <c>\e]8;;URL\aTEXT\e]8;;\a</c> — BEL-terminated OSC
    /// (the ST-terminated form <c>\e\\</c> is equivalent but BEL has
    /// wider terminal support, including Windows Terminal &lt; 1.18).
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
        return ($"\e]8;;{url}\a", "\e]8;;\a");
    }

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
