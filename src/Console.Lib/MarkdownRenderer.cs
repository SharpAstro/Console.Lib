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
public static partial class MarkdownRenderer
{
    // ── VT attribute constants (mode-independent) ─────────────────────

    private const string Bold = "\e[1m";
    private const string ItalicCode = "\e[3m";
    private const string Underline = "\e[4m";
    private const string Reset = "\e[0m";

    /// <summary>
    /// Markdig pipeline with pipe-table, color-inline, and math (dollar-sign
    /// delimited) support enabled. Inline <c>$x$</c> and display <c>$$x$$</c>
    /// produce <see cref="MathInline"/> / <see cref="MathBlock"/> AST nodes
    /// via Markdig's builtin <c>UseMathematics()</c>.
    /// <see cref="LatexBackslashInlineExtension"/> handles <c>\(...\)</c> and
    /// <c>\boxed{...}</c> as inline math during parse (no preprocessing
    /// required). The block-level <c>\[...\]</c> form is still rewritten to
    /// <c>$$...$$</c> in <see cref="PreProcessLatexWrappers"/> because the
    /// inline parser can't promote block math; a dedicated block parser
    /// would replace that last regex.
    /// </summary>
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseColorInlines()
        .UseMathematics()
        .UseLatexBackslashInline()
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
            RenderBlock(block, width, colorMode, theme, result, nestLevel: 0, mathMode, mathFontPath);
            first = false;
        }

        return result;
    }

    /// <summary>
    /// Block-level <c>\[X\]</c> still needs a textual rewrite to <c>$$X$$</c>
    /// because Markdig's <c>MathBlockParser</c> opens on a single character
    /// and the inline-level <see cref="LatexBackslashInlineParser"/> can't
    /// promote a paragraph to a block. Inline <c>\(...\)</c> and the bare
    /// <c>\boxed{...}</c> form are handled by the inline parser during
    /// parse, so they no longer need preprocessing.
    ///
    /// Then substitutes a curated set of common LaTeX math commands
    /// (<c>\div</c>, <c>\times</c>, <c>\approx</c>, …) with their Unicode
    /// equivalents — but only in prose regions, NOT inside <c>$…$</c> or
    /// <c>$$…$$</c> math spans. The math renderer needs the original
    /// <c>\name</c> tokens to identify big operators (<c>\sum</c> scripts as
    /// limits-above-below) and other context-sensitive renders. Prose
    /// substitution lets us handle the case where the model emits LaTeX
    /// commands without wrapping them in math delimiters (common for short
    /// inline ops like <c>131\div2</c>).
    /// </summary>
    private static string PreProcessLatexWrappers(string markdown)
    {
        markdown = Regex.Replace(markdown, @"\\\[([\s\S]*?)\\\]", "$$$$$1$$$$");
        return SubstituteLooseLatexOutsideMath(markdown);
    }


    /// <summary>
    /// Walks <paramref name="markdown"/> alternating between prose and math
    /// spans, applying <see cref="SubstituteLooseLatex"/> only to the prose
    /// halves. Math spans are passed through untouched so the math renderer's
    /// command lookup still sees the original <c>\name</c> tokens.
    ///
    /// <para>Four math-span shapes are recognised: <c>$…$</c>, <c>$$…$$</c>,
    /// <c>\(…\)</c>, and <c>\[…\]</c>. The LaTeX backslash forms were added
    /// when <see cref="LatexBackslashInlineParser"/> took over their parse
    /// — previously they were rewritten to <c>$…$</c> upfront so the
    /// <c>$</c>-only scan caught them by accident; now they reach Markdig
    /// intact, so this scan has to know them explicitly. Without that, a
    /// loose <c>\ll</c> inside <c>\( v \ll c \)</c> was substituted to <c>≪</c>
    /// in the prose pass, then the math grammar saw the Unicode glyph as
    /// an opaque atom, dropped surrounding spaces via juxtaposition, and
    /// rendered <c>v≪c</c> instead of <c>v ≪ c</c>.</para>
    /// </summary>
    private static string SubstituteLooseLatexOutsideMath(string markdown)
    {
        var sb = new StringBuilder(markdown.Length);
        int i = 0;
        while (i < markdown.Length)
        {
            // Find the next math-span opener of any flavour. The opener is
            // either `$`, `\(`, or `\[`.
            int next = -1;
            string? closer = null;
            int openerLen = 0;
            for (int j = i; j < markdown.Length; j++)
            {
                char c = markdown[j];
                if (c == '$')
                {
                    bool isDouble = j + 1 < markdown.Length && markdown[j + 1] == '$';
                    closer = isDouble ? "$$" : "$";
                    openerLen = closer.Length;
                    next = j;
                    break;
                }
                if (c == '\\' && j + 1 < markdown.Length)
                {
                    char n = markdown[j + 1];
                    if (n == '(') { closer = "\\)"; openerLen = 2; next = j; break; }
                    if (n == '[') { closer = "\\]"; openerLen = 2; next = j; break; }
                }
            }

            if (next < 0)
            {
                sb.Append(SubstituteLooseLatex(markdown.AsSpan(i)));
                break;
            }
            if (next > i)
                sb.Append(SubstituteLooseLatex(markdown.AsSpan(i, next - i)));

            int endSearch = next + openerLen;
            int close = markdown.IndexOf(closer!, endSearch, StringComparison.Ordinal);
            if (close < 0)
            {
                // Unterminated math — pass the rest through unchanged, matches
                // Markdig's behaviour for malformed inlines.
                sb.Append(markdown, next, markdown.Length - next);
                break;
            }
            sb.Append(markdown, next, close + closer!.Length - next);
            i = close + closer.Length;
        }
        return sb.ToString();
    }

    /// <summary>
    /// Substitutes the curated LaTeX → Unicode list into a slice of prose.
    /// Letter-named commands (<c>\div</c>, <c>\times</c>, …) use a
    /// <c>(?![a-zA-Z])</c> tail so <c>\div</c> doesn't gobble <c>\divide</c>.
    /// The punctuation-named macros (<c>\,</c> <c>\;</c> <c>\:</c> <c>\!</c>)
    /// are simple literal replacements — no boundary needed since they end
    /// with a non-letter anyway.
    /// </summary>
    private static string SubstituteLooseLatex(ReadOnlySpan<char> slice)
    {
        var text = slice.ToString();
        foreach (var (cmd, repl) in LooseLatexCommands)
            text = Regex.Replace(text, Regex.Escape(cmd) + @"(?![a-zA-Z])", repl);
        text = text
            .Replace(@"\,", " ")
            .Replace(@"\;", " ")
            .Replace(@"\:", " ")
            .Replace(@"\!", string.Empty);
        return text;
    }

    /// <summary>
    /// Curated whitelist of LaTeX math commands that are safe to substitute
    /// inside prose (the model emits them without <c>$…$</c> wrappers).
    /// Limited to commands whose Unicode equivalent is unambiguous and very
    /// unlikely to be the literal intent in markdown prose.
    /// </summary>
    private static readonly (string Command, string Replacement)[] LooseLatexCommands =
    [
        (@"\div",        "÷"),
        (@"\times",      "×"),
        (@"\cdot",       "·"),
        (@"\pm",         "±"),
        (@"\mp",         "∓"),
        (@"\leq",        "≤"),
        (@"\geq",        "≥"),
        (@"\neq",        "≠"),
        (@"\approx",     "≈"),
        (@"\equiv",      "≡"),
        (@"\to",         "→"),
        (@"\rightarrow", "→"),
        (@"\leftarrow",  "←"),
        (@"\infty",      "∞"),
        (@"\partial",    "∂"),
        (@"\nabla",      "∇"),
        (@"\alpha",      "α"),
        (@"\beta",       "β"),
        (@"\gamma",      "γ"),
        (@"\delta",      "δ"),
        (@"\epsilon",    "ε"),
        (@"\theta",      "θ"),
        (@"\lambda",     "λ"),
        (@"\mu",         "μ"),
        (@"\pi",         "π"),
        (@"\sigma",      "σ"),
        (@"\phi",        "φ"),
        (@"\omega",      "ω"),
        (@"\Gamma",      "Γ"),
        (@"\Delta",      "Δ"),
        (@"\Sigma",      "Σ"),
        (@"\Omega",      "Ω"),
        (@"\quad",       "  "),
        (@"\qquad",      "    "),
        // Ellipsis macros — \dots is the catch-all, the others pick a
        // specific orientation. Falling through to literal "\dots" was the
        // single most-common surface bug after the big rendering pass: any
        // power-series or "and so on" line in the model output kept the
        // raw command visible.
        (@"\dots",       "…"),
        (@"\ldots",      "…"),
        (@"\cdots",      "⋯"),
        (@"\vdots",      "⋮"),
        (@"\ddots",      "⋱"),
        // Much-less-than / much-greater-than. Used by the model in
        // "small-velocity expansion" contexts ("for v \ll c") — without
        // these the cmd-rule + juxt-rule collapses to "v\llc" once the
        // grammar's whitespace-discard kicks in.
        (@"\ll",         "≪"),
        (@"\gg",         "≫"),
    ];

    /// <summary>
    /// Parse + visit a LaTeX math source string through <see cref="LatexUnicodeVisitor"/>.
    /// Returns the rendered Unicode string, or the literal input wrapped in
    /// fallback markers on parse error — so a single mangled formula doesn't
    /// take down the surrounding markdown render.
    ///
    /// Before parsing, the source is run through <see cref="ExpandLatexMacros"/>
    /// which extracts <c>\text{...}</c> and <c>\boxed{...}</c> bodies. The math
    /// grammar treats them as opaque <c>\name</c> commands which would otherwise
    /// surface as literal "\text" / "\boxed" in the output and lose any internal
    /// whitespace (since the grammar tokenises letters as math-italic variables
    /// and discards whitespace).
    /// </summary>
    private static string RenderMathUnicode(string source)
    {
        if (string.IsNullOrWhiteSpace(source)) return string.Empty;

        var (expanded, replacements) = ExpandLatexMacros(source);

        string rendered;
        try
        {
            using var lexer = BytesLexer.FromString(expanded, MathLexerTable);
            using var tokens = new SyncLATokenIterator(lexer);
            var item = MathParser.ParseInput(tokens, debugger: null);
            rendered = item.IsError
                ? expanded
                : (item.Content as string ?? expanded);
        }
        catch
        {
            rendered = expanded;
        }

        // Substitute placeholders back. Multiple passes because a replacement
        // for an outer macro can itself contain a placeholder for an inner one
        // (e.g. \boxed{\text{X}} — the boxed-replacement is the rendered X
        // which still references the text placeholder).
        var prev = string.Empty;
        while (prev != rendered && replacements.Count > 0)
        {
            prev = rendered;
            foreach (var kv in replacements)
                rendered = rendered.Replace(kv.Key, kv.Value);
        }
        return rendered;
    }

    /// <summary>
    /// Expands the LaTeX macros that the math-only grammar can't represent.
    /// Each match is replaced with a synthetic <c>\PHxxx</c> command (which the
    /// lexer accepts as a generic <c>cmd</c> atom and the Unicode visitor emits
    /// verbatim, so we can splice the expansion back in after rendering).
    ///
    /// Macros handled:
    /// <list type="bullet">
    ///   <item><c>\text{X}</c> — X is captured verbatim, preserving whitespace.</item>
    ///   <item><c>\boxed{X}</c> — X is recursively math-rendered, then wrapped in
    ///         square brackets as a Unicode-friendly stand-in for the LaTeX
    ///         <c>\boxed</c> frame.</item>
    /// </list>
    /// </summary>
    private static (string expanded, List<KeyValuePair<string, string>> replacements) ExpandLatexMacros(string source)
    {
        var replacements = new List<KeyValuePair<string, string>>();
        var counter = 0;

        string NewPlaceholder()
        {
            // Lexable as `\\[a-zA-Z]+` so it tokenises as a single `cmd` atom.
            // Base-26 letters give us plenty of room without colliding with any
            // real LaTeX command name.
            var n = counter++;
            var sb = new StringBuilder("\\PH");
            do
            {
                sb.Append((char)('a' + (n % 26)));
                n /= 26;
            } while (n > 0);
            return sb.ToString();
        }

        // Common LaTeX aliases the grammar doesn't recognise but reduce to a
        // known form. Done up-front so the substituted text flows through the
        // rest of the expansion as if the model had written it canonically.
        //   \dfrac / \tfrac   — display-style / text-style fractions, alias
        //                       for \frac (the grammar's cmdfrac rule).
        //   \left[ … \right]  — auto-sizing delimiters; in plain text they're
        //                       indistinguishable from the bare delimiter.
        //   \bigl / \bigr …   — sizing hints. Same treatment — strip the size
        //                       prefix and keep the delimiter as a plain char.
        source = NormalizeLatexAliases(source);

        // \begin{NAME}[args]...\end{NAME} environments (array, matrix, align,
        // pmatrix, tabular, …) are out-of-scope for the math grammar — feeding
        // the body to the parser would discard the row/column whitespace and
        // collapse it onto one line. Instead, replace the whole environment
        // with a single placeholder whose replacement is a plain-text render:
        //   \\           → newline (row break)
        //   &            → "  "  (column separator, two-space gutter)
        //   \text{X}     → X with backslash-escapes resolved
        //   other macros → ResolveBackslashEscapes
        // The body never reaches the lexer, so spaces and newlines survive.
        source = ExpandBalancedEnvironment(source, body =>
        {
            var key = NewPlaceholder();
            replacements.Add(new KeyValuePair<string, string>(key, RenderEnvironmentBody(body)));
            return key;
        });

        // \text{X} → placeholder; replacement is X with backslash-non-letter
        // macros already resolved (so e.g. "Yes,\ 131" renders as "Yes, 131"
        // rather than carrying the LaTeX explicit-space "\ " through to the
        // final output). The math grammar's whitespace-skip can't touch the
        // captured-text region because it never sees those bytes — it only
        // sees the opaque placeholder.
        source = ExpandBalancedMacro(source, "text", inner =>
        {
            var key = NewPlaceholder();
            replacements.Add(new KeyValuePair<string, string>(key, ResolveBackslashEscapes(inner)));
            return key;
        });

        // \boxed{X} → placeholder; replacement is "[X-rendered]" where X is
        // recursively run through RenderMathUnicode (so a nested \frac, \text,
        // etc. inside the box body still renders correctly).
        source = ExpandBalancedMacro(source, "boxed", inner =>
        {
            var key = NewPlaceholder();
            replacements.Add(new KeyValuePair<string, string>(key, "[" + RenderMathUnicode(inner) + "]"));
            return key;
        });

        // Outer-source pass over LaTeX backslash-non-letter macros (\, \; \: \!
        // \\ \{ \} \_ \$ \% \# \&). Each becomes a placeholder mapping to its
        // rendered equivalent so the lexer doesn't choke on the orphan `\` +
        // non-letter pair (the cmd rule is `\\[a-zA-Z]+`).
        var spaceSb = new StringBuilder(source.Length);
        int p = 0;
        while (p < source.Length)
        {
            if (source[p] == '\\' && p + 1 < source.Length && !IsAsciiLetter(source[p + 1]))
            {
                var key = NewPlaceholder();
                replacements.Add(new KeyValuePair<string, string>(key, RenderBackslashEscape(source[p + 1])));
                spaceSb.Append(key);
                p += 2;
            }
            else
            {
                spaceSb.Append(source[p]);
                p++;
            }
        }
        source = spaceSb.ToString();

        // Any char the latex.lalr.yaml lexer has no rule for (Unicode operators
        // like ÷ ≈ ≤ ≥ × −, punctuation like , ; !, …) — wrap each in a
        // placeholder so the lexer keeps going. Without this, a single stray
        // U+2248 in "\sqrt{131} ≈ 11.45" kills the lex pass and \sqrt never
        // gets to render. The placeholder lexes as a cmd atom; the visitor
        // emits it verbatim; we splice the original char back in afterwards.
        var sb = new StringBuilder(source.Length);
        foreach (var ch in source)
        {
            if (IsLexerSafe(ch))
            {
                sb.Append(ch);
            }
            else
            {
                var key = NewPlaceholder();
                replacements.Add(new KeyValuePair<string, string>(key, ch.ToString()));
                sb.Append(key);
            }
        }
        source = sb.ToString();

        return (source, replacements);
    }

    /// <summary>
    /// True iff the latex.lalr.yaml lexer has a tokenisation rule that matches
    /// a single occurrence of <paramref name="c"/>. ASCII letters, digits, dot
    /// (number fraction), whitespace, the seven operator chars + - * / = ^ _,
    /// brackets ( ) { }, and backslash (command lead-in) — anything else
    /// aborts the lex pass and so needs a placeholder substitution.
    /// </summary>
    private static bool IsLexerSafe(char c)
    {
        if (c == ' ' || c == '\t' || c == '\r' || c == '\n') return true;
        if (c >= '0' && c <= '9') return true;
        if (c == '.') return true;
        if (IsAsciiLetter(c)) return true;
        return c is '+' or '-' or '*' or '/' or '=' or '^' or '_'
                or '(' or ')' or '{' or '}' or '\\';
    }

    /// <summary>
    /// Walks <paramref name="source"/> looking for <c>\<paramref name="commandName"/>{…}</c>
    /// with balanced braces, replacing each occurrence with the result of
    /// <paramref name="onMatch"/> applied to the inner body. Skips matches
    /// where the command name has trailing letters (so <c>\textit</c> doesn't
    /// match a <c>\text</c> rule) or where the trailing brace can't be located.
    /// </summary>
    private static string ExpandBalancedMacro(string source, string commandName, Func<string, string> onMatch)
    {
        var sb = new StringBuilder(source.Length);
        var marker = "\\" + commandName;
        int i = 0;
        while (i < source.Length)
        {
            if (i + marker.Length <= source.Length
                && string.CompareOrdinal(source, i, marker, 0, marker.Length) == 0
                && (i + marker.Length == source.Length
                    || !IsAsciiLetter(source[i + marker.Length])))
            {
                int afterCmd = i + marker.Length;
                int j = afterCmd;
                while (j < source.Length && (source[j] == ' ' || source[j] == '\t' || source[j] == '\r' || source[j] == '\n'))
                    j++;
                if (j < source.Length && source[j] == '{')
                {
                    int braceEnd = FindMatchingBrace(source, j);
                    if (braceEnd > j)
                    {
                        var inner = source.Substring(j + 1, braceEnd - j - 1);
                        sb.Append(onMatch(inner));
                        i = braceEnd + 1;
                        continue;
                    }
                }
            }
            sb.Append(source[i]);
            i++;
        }
        return sb.ToString();
    }

    private static bool IsAsciiLetter(char c) =>
        (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');

    /// <summary>
    /// Pre-substitutes LaTeX aliases / size hints that the math grammar doesn't
    /// natively understand, into forms it does. Conservative whitelist — only
    /// substitutions where the rendered text is identical to the canonical form.
    /// </summary>
    private static string NormalizeLatexAliases(string source)
    {
        // Order matters: longer prefixes first so e.g. \Biggl isn't eaten by
        // an earlier \bigl pass (they don't share a 5-char prefix today but
        // keeping the ordering explicit for future additions).
        foreach (var (from, to) in LatexAliases)
            source = source.Replace(from, to);
        return source;
    }

    private static readonly (string From, string To)[] LatexAliases =
    [
        // Fraction-style aliases.
        (@"\dfrac",   @"\frac"),
        (@"\tfrac",   @"\frac"),
        // Auto-sizing delimiter pairs collapse to the bare delimiter.
        (@"\left[",   "["),
        (@"\right]",  "]"),
        (@"\left(",   "("),
        (@"\right)",  ")"),
        (@"\left\{",  "{"),
        (@"\right\}", "}"),
        (@"\left|",   "|"),
        (@"\right|",  "|"),
        (@"\left.",   ""),   // null delimiter
        (@"\right.",  ""),
        // Manual size prefixes — strip, keep the delimiter that follows.
        (@"\biggl",   ""),
        (@"\biggr",   ""),
        (@"\Biggl",   ""),
        (@"\Biggr",   ""),
        (@"\bigl",    ""),
        (@"\bigr",    ""),
        (@"\Bigl",    ""),
        (@"\Bigr",    ""),
    ];

    /// <summary>
    /// Renders a single LaTeX backslash-non-letter macro to its plain-Unicode
    /// equivalent. <c>\,</c> <c>\;</c> <c>\:</c> and <c>\ </c> (explicit space)
    /// render as a regular space; <c>\!</c> (negative thin space) renders as
    /// empty; <c>\\</c> is a line break in math mode (rendered as a space here
    /// for inline contexts); the typesetter-escapes <c>\&amp;</c> <c>\$</c>
    /// <c>\#</c> <c>\%</c> <c>\_</c> <c>\{</c> <c>\}</c> render as the bare
    /// character. Unknown <c>\?</c> falls through as the literal two-char
    /// sequence so it's visible in output for debugging.
    /// </summary>
    private static string RenderBackslashEscape(char next) => next switch
    {
        ',' or ';' or ':' or ' ' => " ",
        '!'                       => string.Empty,
        '\\'                      => " ",
        '&' or '$' or '#' or '%' or '_' or '{' or '}' => next.ToString(),
        _                         => "\\" + next,
    };

    /// <summary>
    /// Scans a captured <c>\text{...}</c> body and resolves any
    /// backslash-non-letter macros to their Unicode equivalent in-place.
    /// Differs from the outer-source pass in <see cref="ExpandLatexMacros"/>:
    /// no placeholder substitution is needed because the body never reaches
    /// the lexer — it's stashed in the replacement map and spliced back in
    /// after the rest of the formula has rendered.
    /// </summary>
    private static string ResolveBackslashEscapes(string body)
    {
        if (body.IndexOf('\\') < 0) return body;
        var sb = new StringBuilder(body.Length);
        int i = 0;
        while (i < body.Length)
        {
            if (body[i] == '\\' && i + 1 < body.Length && !IsAsciiLetter(body[i + 1]))
            {
                sb.Append(RenderBackslashEscape(body[i + 1]));
                i += 2;
            }
            else
            {
                sb.Append(body[i]);
                i++;
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Walks <paramref name="source"/> looking for <c>\begin{NAME}[args]...\end{NAME}</c>
    /// environments (matched names, balanced braces ignored — non-greedy body),
    /// replacing each with <paramref name="onMatch"/> applied to the body. The
    /// outer regex eats any number of positional argument groups (the <c>{ll}</c>
    /// column spec in <c>\begin{array}{ll}</c>, the optional <c>[t]</c>, etc.).
    /// </summary>
    private static string ExpandBalancedEnvironment(string source, Func<string, string> onMatch)
    {
        if (source.IndexOf(@"\begin{", StringComparison.Ordinal) < 0) return source;
        return EnvironmentRegex.Replace(source, m => onMatch(m.Groups[2].Value));
    }

    private static readonly Regex EnvironmentRegex = new(
        @"\\begin\{([a-zA-Z*]+)\}(?:\s*\{[^{}]*\}|\s*\[[^\]]*\])*([\s\S]*?)\\end\{\1\}",
        RegexOptions.Compiled);

    /// <summary>
    /// Plain-text rendering of an environment body: row break (<c>\\</c>) to
    /// newline, column separator (<c>&amp;</c>) to a two-space gutter, any
    /// <c>\text{X}</c> bodies to their resolved-escape X, then a final pass
    /// over remaining backslash-non-letter macros. Each row is then trimmed
    /// of trailing whitespace and the whole block trimmed at the edges so the
    /// surrounding <c>\boxed{}</c> sees clean content.
    /// </summary>
    private static string RenderEnvironmentBody(string body)
    {
        body = body.Replace(@"\\", "\n");
        body = ExpandBalancedMacro(body, "text", inner => ResolveBackslashEscapes(inner));
        body = body.Replace("&", "  ");
        body = ResolveBackslashEscapes(body);
        var rows = body.Split('\n');
        for (int r = 0; r < rows.Length; r++) rows[r] = rows[r].TrimEnd();
        return string.Join("\n", rows).Trim('\n', ' ', '\t');
    }

    /// <summary>
    /// Returns true if <paramref name="source"/> contains a <c>\<paramref name="commandName"/></c>
    /// token, i.e. the literal name preceded by a backslash and not followed by
    /// another ASCII letter (so <c>\text</c> matches but <c>\textbf</c> doesn't).
    /// </summary>
    private static bool ContainsMacro(string source, string commandName)
    {
        var marker = "\\" + commandName;
        int idx = 0;
        while ((idx = source.IndexOf(marker, idx, StringComparison.Ordinal)) >= 0)
        {
            int after = idx + marker.Length;
            if (after == source.Length || !IsAsciiLetter(source[after]))
                return true;
            idx = after;
        }
        return false;
    }

    private static int FindMatchingBrace(string s, int openPos)
    {
        int depth = 1;
        for (int i = openPos + 1; i < s.Length; i++)
        {
            if (s[i] == '{') depth++;
            else if (s[i] == '}')
            {
                depth--;
                if (depth == 0) return i;
            }
        }
        return -1;
    }

    // ── Block rendering ───────────────────────────────────────────────

    private static void RenderBlock(Block block, int width, ColorMode colorMode,
        MarkdownTheme theme, List<string> result, int nestLevel,
        BoxRenderMode? mathMode = null, string? mathFontPath = null)
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
                RenderMathBlock(mathBlock, width, colorMode, theme, result, mathMode, mathFontPath);
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
        BoxRenderMode? mathMode, string? mathFontPath)
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
        if (mathMode is { } mode && TryRenderMathBox(source, mode, mathFontPath, result))
            return;

        var mathColor = Resolve(theme.Math, colorMode);
        var rst = Rst(colorMode);
        var rendered = RenderMathUnicode(source);
        // The rendered output may contain embedded newlines from an environment
        // expansion (e.g. \begin{array}…\end{array} rendered as a multi-row
        // text block). Emit each row as its own wrapped line so the layout
        // survives — WordWrap on its own treats \n as in-word whitespace and
        // would pass it through to the terminal as a stray control char.
        foreach (var ln in rendered.Split('\n'))
            result.AddRange(WordWrap($"  {mathColor}{ln}{rst}", width, "  "));
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
    private static bool TryRenderMathBox(string source, BoxRenderMode mode,
        string? callerFontPath, List<string> result)
    {
        if (string.IsNullOrWhiteSpace(source)) return false;

        // Pre-process the source so the math grammar can swallow it. Things
        // we can fix up source-side (rendered the same as Unicode path):
        //   - LaTeX aliases: \dfrac/\tfrac → \frac, \left[ → [, …
        //   - \boxed{X}     → X  (strip the wrapper; v1 has no boxed frame)
        //   - \, \; \! \\   → literal whitespace (lexer ignores it)
        //
        // Things we still can't do in box mode (visitor-side, would need new
        // Box types):
        //   - \text{X}      → no upright-text run box yet → fall back to Unicode
        //   - \begin{}/end{} → no multi-line table layout → fall back to Unicode
        if (ContainsMacro(source, "text") || source.Contains(@"\begin", StringComparison.Ordinal))
            return false;

        source = NormalizeLatexAliases(source);
        source = ExpandBalancedMacro(source, "boxed", inner => inner);
        // Recheck after \boxed strip — its body could have introduced \text.
        if (ContainsMacro(source, "text") || source.Contains(@"\begin", StringComparison.Ordinal))
            return false;
        source = ResolveBackslashEscapes(source);

        // Font resolution. If the caller passed a path (apps typically pick
        // something co-located with their executable so the library doesn't
        // have to know about AppContext or assembly-location quirks), trust
        // it. Otherwise fall back to a small built-in system-font search.
        string? fontPath = !string.IsNullOrEmpty(callerFontPath) && File.Exists(callerFontPath)
            ? callerFontPath
            : ResolveMathFont();
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
