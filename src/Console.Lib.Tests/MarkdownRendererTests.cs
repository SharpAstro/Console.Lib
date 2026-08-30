using Console.Lib;
using DIR.Lib;
using Shouldly;
using Xunit;

namespace Console.Lib.Tests;

public sealed class MarkdownRendererTests
{
    private const string Reset = "\e[0m";
    private const string Bold = "\e[1m";
    private const string Italic = "\e[3m";
    private const string Underline = "\e[4m";

    // Resolved color escapes (SGR-16 mode via VtStyle.ApplyFg)
    private static readonly string Cyan = new VtStyle(SgrColor.Cyan, default).ApplyFg(ColorMode.Sgr16);
    private static readonly string Dim = new VtStyle(SgrColor.BrightBlack, default).ApplyFg(ColorMode.Sgr16);
    private static readonly string BoldBlue = Bold + new VtStyle(SgrColor.Blue, default).ApplyFg(ColorMode.Sgr16);
    private static readonly string BoldCyan = Bold + new VtStyle(SgrColor.Cyan, default).ApplyFg(ColorMode.Sgr16);
    private static readonly string BoldWhite = Bold + new VtStyle(SgrColor.BrightWhite, default).ApplyFg(ColorMode.Sgr16);

    // ── VisibleLength ─────────────────────────────────────────────────

    [Fact]
    public void VisibleLength_PlainText_ReturnsLength()
    {
        MarkdownRenderer.VisibleLength("hello").ShouldBe(5);
    }

    [Fact]
    public void VisibleLength_WithAnsi_IgnoresEscapes()
    {
        MarkdownRenderer.VisibleLength($"{Bold}hello{Reset}").ShouldBe(5);
        MarkdownRenderer.VisibleLength($"{Cyan}a{Reset}{Dim}b{Reset}").ShouldBe(2);
    }

    [Fact]
    public void VisibleLength_Empty_ReturnsZero()
    {
        MarkdownRenderer.VisibleLength("").ShouldBe(0);
    }

    // ── Inline formatting ─────────────────────────────────────────────

    // Note: Phase F renderer uses selective SGR unset codes (22 / 23
    // for no-bold / no-italic) instead of a full reset on emphasis
    // close — preserves outer underline / colour when emphasis is
    // nested inside a link or color inline. These tests assert on the
    // emitted SGR enable/disable codes accordingly.

    private const string NoBold = "\e[22m";
    private const string NoItalic = "\e[23m";

    [Fact]
    public void FormatInline_Bold()
    {
        var result = MarkdownRenderer.FormatInline("**hello**", ColorMode.Sgr16);
        result.ShouldBe($"{Bold}hello{NoBold}");
    }

    [Fact]
    public void FormatInline_Italic()
    {
        var result = MarkdownRenderer.FormatInline("*hello*", ColorMode.Sgr16);
        result.ShouldBe($"{Italic}hello{NoItalic}");
    }

    [Fact]
    public void FormatInline_BoldItalic()
    {
        var result = MarkdownRenderer.FormatInline("***hello***", ColorMode.Sgr16);
        // Bold + italic on, content, italic + bold off — both attributes
        // selectively cleared so the result has visible length 5.
        result.ShouldContain(Bold);
        result.ShouldContain(Italic);
        result.ShouldContain("hello");
        result.ShouldContain(NoBold);
        result.ShouldContain(NoItalic);
        MarkdownRenderer.VisibleLength(result).ShouldBe(5);
    }

    [Fact]
    public void FormatInline_Link()
    {
        var result = MarkdownRenderer.FormatInline("[click](http://example.com)", ColorMode.Sgr16);
        // OSC 8 hyperlink wrap (`\e]8;;URL\a`) makes the label clickable
        // on supporting terminals; the underline + colour give visual
        // affordance for unsupporting terminals; the dim `(url)` after
        // keeps the URL visible + copy-pasteable for plain-text dumps.
        var oscOpen = "\e]8;;http://example.com\a";
        var oscClose = "\e]8;;\a";
        result.ShouldBe($"{oscOpen}{Underline}{Cyan}click{Reset}{oscClose}{Dim} (http://example.com){Reset}");
    }

    // ── Link resolution ───────────────────────────────────────────────
    // A bare relative href (`docs/foo.md`) isn't a valid absolute URI, so
    // terminals reject it as an OSC 8 target ("invalid link" in Windows
    // Terminal). `linkResolver` lets a host (mdcat) rewrite the OSC 8 target
    // — e.g. into a `file://` URI — while the visible `(url)` text keeps
    // showing the original href.

    [Fact]
    public void RenderLines_LinkResolver_RewritesOsc8TargetOnly()
    {
        var lines = MarkdownRenderer.RenderLines(
            "[docs](docs/foo.md)", 80, ColorMode.Sgr16,
            linkResolver: url => $"file:///base/{url}");

        var line = lines[0];
        line.ShouldContain("\e]8;;file:///base/docs/foo.md\a");
        line.ShouldContain("(docs/foo.md)"); // visible text stays the original href
        line.ShouldNotContain("\e]8;;docs/foo.md\a");
    }

    [Fact]
    public void RenderLines_NoLinkResolver_EmitsRawUrlUnchanged()
    {
        var lines = MarkdownRenderer.RenderLines("[docs](docs/foo.md)", 80, ColorMode.Sgr16);
        lines[0].ShouldContain("\e]8;;docs/foo.md\a");
    }

    [Fact]
    public void RenderLines_LinkResolver_AppliesInsideHeadingsListsAndTables()
    {
        var md = "# [H](h.md)\n\n- [L](l.md)\n\n| Col |\n| --- |\n| [T](t.md) |";
        var lines = MarkdownRenderer.RenderLines(
            md, 80, ColorMode.Sgr16, linkResolver: url => $"resolved/{url}");

        var joined = string.Join("\n", lines);
        joined.ShouldContain("\e]8;;resolved/h.md\a");
        joined.ShouldContain("\e]8;;resolved/l.md\a");
        joined.ShouldContain("\e]8;;resolved/t.md\a");
    }

    [Fact]
    public void FormatInline_BackslashEscape()
    {
        var result = MarkdownRenderer.FormatInline("\\*not italic\\*", ColorMode.Sgr16);
        result.ShouldBe("*not italic*");
    }

    [Fact]
    public void FormatInline_PlainText_Unchanged()
    {
        var result = MarkdownRenderer.FormatInline("no formatting here", ColorMode.Sgr16);
        result.ShouldBe("no formatting here");
    }

    [Fact]
    public void FormatInline_NestedBoldInItalic()
    {
        // *italic **bold** italic*
        // With selective SGR unsets, the outer italic stays on across
        // the inner bold span — emit italic-on once, italic-off once.
        // The inner bold emits bold-on then bold-off; italic survives.
        var result = MarkdownRenderer.FormatInline("*italic **bold** italic*", ColorMode.Sgr16);
        result.ShouldBe($"{Italic}italic {Bold}bold{NoBold} italic{NoItalic}");
    }

    // ── Headers ───────────────────────────────────────────────────────

    [Fact]
    public void RenderLines_H1_BoldBlue()
    {
        var lines = MarkdownRenderer.RenderLines("# Title", 80, ColorMode.Sgr16);
        lines.Count.ShouldBe(1);
        lines[0].ShouldBe($"{BoldBlue}Title{Reset}");
    }

    [Fact]
    public void RenderLines_H2_BoldCyan()
    {
        var lines = MarkdownRenderer.RenderLines("## Subtitle", 80, ColorMode.Sgr16);
        lines.Count.ShouldBe(1);
        lines[0].ShouldBe($"{BoldCyan}Subtitle{Reset}");
    }

    [Fact]
    public void RenderLines_H3_BoldWhite()
    {
        var lines = MarkdownRenderer.RenderLines("### Section", 80, ColorMode.Sgr16);
        lines.Count.ShouldBe(1);
        lines[0].ShouldBe($"{BoldWhite}Section{Reset}");
    }

    [Fact]
    public void RenderLines_HeaderWithTrailingHashes()
    {
        var lines = MarkdownRenderer.RenderLines("## Title ##", 80, ColorMode.Sgr16);
        lines[0].ShouldBe($"{BoldCyan}Title{Reset}");
    }

    // ── Horizontal rules ──────────────────────────────────────────────

    [Fact]
    public void RenderLines_HorizontalRule_Dashes()
    {
        var lines = MarkdownRenderer.RenderLines("---", 40, ColorMode.Sgr16);
        lines.Count.ShouldBe(1);
        lines[0].ShouldBe($"{Dim}{new string('─', 40)}{Reset}");
    }

    [Fact]
    public void RenderLines_HorizontalRule_Asterisks()
    {
        var lines = MarkdownRenderer.RenderLines("***", 20, ColorMode.Sgr16);
        lines[0].ShouldBe($"{Dim}{new string('─', 20)}{Reset}");
    }

    // ── Unordered lists ───────────────────────────────────────────────

    [Fact]
    public void RenderLines_UnorderedList()
    {
        var md = "- First\n- Second\n- Third";
        var lines = MarkdownRenderer.RenderLines(md, 80, ColorMode.Sgr16);
        lines.Count.ShouldBe(3);
        lines[0].ShouldContain("•");
        lines[0].ShouldContain("First");
        lines[1].ShouldContain("Second");
    }

    [Fact]
    public void RenderLines_NestedUnorderedList()
    {
        var md = "- Outer\n  - Inner";
        var lines = MarkdownRenderer.RenderLines(md, 80, ColorMode.Sgr16);
        lines.Count.ShouldBe(2);
        lines[0].ShouldContain("•");
        lines[1].ShouldContain("◦");
    }

    // ── Ordered lists ─────────────────────────────────────────────────

    [Fact]
    public void RenderLines_OrderedList()
    {
        var md = "1. First\n2. Second";
        var lines = MarkdownRenderer.RenderLines(md, 80, ColorMode.Sgr16);
        lines.Count.ShouldBe(2);
        lines[0].ShouldContain("1.");
        lines[0].ShouldContain("First");
        lines[1].ShouldContain("2.");
    }

    // ── Tables ────────────────────────────────────────────────────────

    [Fact]
    public void RenderLines_SimpleTable()
    {
        var md = "| Name | Age |\n| --- | --- |\n| Alice | 30 |\n| Bob | 25 |";
        var lines = MarkdownRenderer.RenderLines(md, 80, ColorMode.Sgr16);

        // Top border, header, separator, 2 data rows, bottom border = 6 lines
        lines.Count.ShouldBe(6);

        // Borders use box-drawing characters
        lines[0].ShouldContain("┌");
        lines[0].ShouldContain("┬");
        lines[0].ShouldContain("┐");
        lines[2].ShouldContain("├");
        lines[2].ShouldContain("┼");
        lines[2].ShouldContain("┤");
        lines[5].ShouldContain("└");
        lines[5].ShouldContain("┴");
        lines[5].ShouldContain("┘");

        // Header row contains bold names
        lines[1].ShouldContain("Name");
        lines[1].ShouldContain("Age");

        // Data rows contain values
        lines[3].ShouldContain("Alice");
        lines[4].ShouldContain("Bob");
    }

    [Fact]
    public void RenderLines_TableWithAlignment()
    {
        var md = "| Left | Center | Right |\n| :--- | :---: | ---: |\n| a | b | c |";
        var lines = MarkdownRenderer.RenderLines(md, 80, ColorMode.Sgr16);
        lines.Count.ShouldBe(5); // top + header + sep + 1 row + bottom
    }

    // ── Word wrapping ─────────────────────────────────────────────────

    [Fact]
    public void WordWrap_FitsInWidth_SingleLine()
    {
        var result = MarkdownRenderer.WordWrap("short text", 80);
        result.Count.ShouldBe(1);
        result[0].ShouldBe("short text");
    }

    [Fact]
    public void WordWrap_ExceedsWidth_Wraps()
    {
        var result = MarkdownRenderer.WordWrap("hello world foo bar", 11);
        result.Count.ShouldBe(2);
        result[0].ShouldBe("hello world");
        result[1].ShouldBe("foo bar");
    }

    [Fact]
    public void WordWrap_WithContinuationIndent()
    {
        var result = MarkdownRenderer.WordWrap("hello world foo bar", 11, "  ");
        result.Count.ShouldBe(2);
        result[0].ShouldBe("hello world");
        result[1].ShouldBe("  foo bar");
    }

    [Fact]
    public void WordWrap_PreservesAnsiCodes()
    {
        var text = $"{Bold}hello world{Reset}";
        var result = MarkdownRenderer.WordWrap(text, 7);
        result.Count.ShouldBe(2);
        // First line has "hello" with bold
        MarkdownRenderer.VisibleLength(result[0]).ShouldBe(5);
        // Second line has "world" with bold carried over
        result[1].ShouldContain("world");
    }

    // ── Mixed content ─────────────────────────────────────────────────

    [Fact]
    public void RenderLines_MixedContent()
    {
        var md = "# Hello\n\nSome **bold** text.\n\n---\n\n- Item 1\n- Item 2";
        var lines = MarkdownRenderer.RenderLines(md, 80, ColorMode.Sgr16);
        lines.Count.ShouldBeGreaterThan(5);
    }

    [Fact]
    public void RenderLines_EmptyInput()
    {
        var lines = MarkdownRenderer.RenderLines("", 80, ColorMode.Sgr16);
        lines.Count.ShouldBe(0);
    }

    // ── Widget ────────────────────────────────────────────────────────

    [Fact]
    public void MarkdownWidget_RendersToViewport()
    {
        var terminal = new FakeTerminal(new Queue<ConsoleInputEvent>(), 40, 10);
        var widget = new MarkdownWidget(terminal);
        widget.Markdown("# Hello\n\nWorld");
        widget.Render();

        widget.TotalLines.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void MarkdownWidget_ScrollTo_ClampsNegative()
    {
        var terminal = new FakeTerminal(new Queue<ConsoleInputEvent>(), 40, 10);
        var widget = new MarkdownWidget(terminal);
        widget.Markdown("# Test").ScrollTo(-5);
        widget.ScrollOffset.ShouldBe(0);
    }

    // ── Color inlines ─────────────────────────────────────────────────

    [Fact]
    public void FormatInline_ColorByName()
    {
        var result = MarkdownRenderer.FormatInline("[warning]{red}", ColorMode.Sgr16);
        var red = new VtStyle(SgrColor.Red, default).ApplyFg(ColorMode.Sgr16);
        result.ShouldBe($"{Reset}{red}warning{Reset}");
    }

    [Fact]
    public void FormatInline_ColorByHex()
    {
        var result = MarkdownRenderer.FormatInline("[custom]{#FF8800}", ColorMode.Sgr16);
        var color = MarkdownTheme.ParseColor("#FF8800");
        var fg = new VtStyle(color, default).ApplyFg(ColorMode.Sgr16);
        result.ShouldBe($"{Reset}{fg}custom{Reset}");
    }

    [Fact]
    public void FormatInline_ColorByHex_TrueColor()
    {
        var result = MarkdownRenderer.FormatInline("[custom]{#FF8800}", ColorMode.TrueColor);
        result.ShouldContain("\e[38;2;255;136;0m");
        result.ShouldContain("custom");
    }

    [Fact]
    public void FormatInline_InvalidColor_NotParsed()
    {
        // Invalid color name should fall through — treated as literal text
        var result = MarkdownRenderer.FormatInline("[text]{notacolor}", ColorMode.Sgr16);
        result.ShouldContain("[text]");
        result.ShouldContain("{notacolor}");
    }

    [Fact]
    public void RenderLines_ColorInline_InParagraph()
    {
        var lines = MarkdownRenderer.RenderLines("This has a [warning]{red} word.", 80, ColorMode.Sgr16);
        lines.Count.ShouldBe(1);
        lines[0].ShouldContain("warning");
        var red = new VtStyle(SgrColor.Red, default).ApplyFg(ColorMode.Sgr16);
        lines[0].ShouldContain(red);
    }

    // ── No-color mode ─────────────────────────────────────────────────

    [Fact]
    public void RenderLines_NoColor_NoEscapes()
    {
        var lines = MarkdownRenderer.RenderLines("# Hello\n\n**bold** and [link](http://x.com)", 80, ColorMode.None);
        foreach (var line in lines)
            line.ShouldNotContain("\e[");
    }

    [Fact]
    public void FormatInline_NoColor_PlainText()
    {
        var result = MarkdownRenderer.FormatInline("[colored]{red}", ColorMode.None);
        result.ShouldNotContain("\e[");
        result.ShouldContain("colored");
    }

    // ── Theme customization ───────────────────────────────────────────

    [Fact]
    public void RenderLines_CustomTheme_UsesCustomColors()
    {
        var theme = MarkdownTheme.Default with { Heading1 = SgrColor.Green.ToRgba() };
        var lines = MarkdownRenderer.RenderLines("# Title", 80, ColorMode.Sgr16, theme);
        var green = new VtStyle(SgrColor.Green, default).ApplyFg(ColorMode.Sgr16);
        lines[0].ShouldContain(green);
        lines[0].ShouldContain("Title");
    }

    // ── ParseColor ────────────────────────────────────────────────────

    [Fact]
    public void ParseColor_NamedColor()
    {
        MarkdownTheme.ParseColor("red").ShouldBe(SgrColor.Red.ToRgba());
        MarkdownTheme.ParseColor("BrightCyan").ShouldBe(SgrColor.BrightCyan.ToRgba());
    }

    [Fact]
    public void ParseColor_HexColor()
    {
        MarkdownTheme.ParseColor("#FF0000").ShouldBe(new RGBAColor32(0xFF, 0x00, 0x00, 0xFF));
        MarkdownTheme.ParseColor("#1A2B3C").ShouldBe(new RGBAColor32(0x1A, 0x2B, 0x3C, 0xFF));
    }

    [Fact]
    public void ParseColor_Invalid_Throws()
    {
        Should.Throw<ArgumentException>(() => MarkdownTheme.ParseColor("notacolor"));
    }

    [Fact]
    public void TryParseColor_Invalid_ReturnsFalse()
    {
        MarkdownTheme.TryParseColor("nope", out _).ShouldBeFalse();
    }

    // ── Fenced code blocks + inline code ─────────────────────────────────

    [Fact]
    public void FencedCodeBlock_RendersWithRules()
    {
        var md = "```\nprint(\"hi\")\n```";
        var lines = MarkdownRenderer.RenderLines(md, 40, ColorMode.None);
        // Three lines: top rule, body, bottom rule.
        lines.Count.ShouldBe(3);
        lines[0].ShouldContain("─");
        lines[1].Trim().ShouldBe("print(\"hi\")");
        lines[2].ShouldContain("─");
    }

    [Fact]
    public void FencedCodeBlock_WithLanguageTag_IncludesLabel()
    {
        var md = "```python\nx = 1\n```";
        var lines = MarkdownRenderer.RenderLines(md, 40, ColorMode.None);
        lines[0].ShouldContain("python");
    }

    [Fact]
    public void CodeInline_RendersWithCodeColor()
    {
        var md = "use `foo` here";
        var lines = MarkdownRenderer.RenderLines(md, 80, ColorMode.None);
        // ColorMode.None suppresses escapes, so we just check the content survives.
        string.Join("", lines).ShouldContain("foo");
    }

    // ── Math: inline ($x$) and display ($$x$$) ──────────────────────────

    [Fact]
    public void MathInline_RendersSuperscriptUnicode()
    {
        var md = "Einstein: $E = mc^2$.";
        var lines = MarkdownRenderer.RenderLines(md, 80, ColorMode.None);
        var joined = string.Join("", lines);
        // c^2 → c² via the Unicode superscript table.
        joined.ShouldContain("c²");
        // Surrounding prose survives.
        joined.ShouldContain("Einstein");
    }

    [Fact]
    public void MathInline_FracProducesFractionSlash()
    {
        var md = "half is $\\frac{1}{2}$.";
        var lines = MarkdownRenderer.RenderLines(md, 80, ColorMode.None);
        // \frac renders with U+2044 (fraction slash), not plain '/'.
        string.Join("", lines).ShouldContain("⁄");
    }

    [Fact]
    public void MathInline_GreekLetterCommand()
    {
        var md = "angle: $\\alpha + \\beta$.";
        var lines = MarkdownRenderer.RenderLines(md, 80, ColorMode.None);
        var joined = string.Join("", lines);
        joined.ShouldContain("α");
        joined.ShouldContain("β");
    }

    [Fact]
    public void MathBlock_RendersAsBlock()
    {
        var md = "Before.\n\n$$E = mc^2$$\n\nAfter.";
        var lines = MarkdownRenderer.RenderLines(md, 80, ColorMode.None);
        var joined = string.Join("\n", lines);
        joined.ShouldContain("Before");
        joined.ShouldContain("c²");
        joined.ShouldContain("After");
    }

    // ── LaTeX wrapper preprocessing ──────────────────────────────────────

    [Fact]
    public void LatexInlineWrapper_RendersLikeDollarMath()
    {
        // \(x^2\) should pre-process into $x^2$ and render the same way.
        var lines = MarkdownRenderer.RenderLines("here: \\(x^2\\).", 80, ColorMode.None);
        string.Join("", lines).ShouldContain("x²");
    }

    [Fact]
    public void LatexDisplayWrapper_RendersLikeDoubleDollarMath()
    {
        var lines = MarkdownRenderer.RenderLines("\\[E = mc^2\\]", 80, ColorMode.None);
        string.Join("", lines).ShouldContain("c²");
    }

    // ── Parse-failure fallback ───────────────────────────────────────────

    [Fact]
    public void MathInline_ParseError_FallsBackToLiteral()
    {
        // A trailing unmatched paren has no valid parse — the renderer should
        // emit the source literally rather than throwing.
        var lines = MarkdownRenderer.RenderLines("oops: $\\frac{1}{$.", 80, ColorMode.None);
        // The render should at least include "oops:" and not crash.
        string.Join("", lines).ShouldContain("oops");
    }

    // ── Pixel-rendered display math ──────────────────────────────────────

    [Fact]
    public void MathBlock_HalfBlockMode_EmitsPixelOutputOrFallsBackCleanly()
    {
        // Asking for half-block (most universal pixel mode) — if a usable
        // math font is installed the renderer emits multi-row pixel output;
        // if not, it falls back to the single-line Unicode path. Either way
        // the call must not throw, and "Before"/"After" prose must survive.
        var md = "Before.\n\n$$E = mc^2$$\n\nAfter.";
        var lines = MarkdownRenderer.RenderLines(md, 80, ColorMode.None, theme: null, mathMode: BoxRenderMode.HalfBlock);
        var joined = string.Join("\n", lines);
        joined.ShouldContain("Before");
        joined.ShouldContain("After");
        lines.Count.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void MathBlock_NoMathModeArg_UsesUnicodePath()
    {
        // Default mathMode is null — pixel rendering is opt-in, so existing
        // callers keep the single-row Unicode rendering they had before.
        var lines = MarkdownRenderer.RenderLines("$$x^2$$", 80, ColorMode.None);
        string.Join("", lines).ShouldContain("x²");
    }

    // ── Loose-Unicode boundary scan respects LaTeX backslash math ────────

    [Fact]
    public void LooseLatex_LlInsideLatexParen_RendersWithSpaces()
    {
        // Regression: when \(..\) was rewritten to $..$ in the preprocessing
        // pass, SubstituteLooseLatexOutsideMath caught the resulting $..$
        // boundary and left \ll alone, letting the math grammar tokenise it
        // as `rel` and emit `v ≪ c` with proper spaces. After Path 1 moved
        // \(..\) handling into LatexBackslashInlineParser, the bare \(..\)
        // form was no longer being detected as math by the prose-substitution
        // scan, so `\ll` got substituted to `≪` upstream and the math grammar
        // saw the bare Unicode glyph, dropped surrounding spaces via the
        // juxtaposition rule, and rendered `v≪c`.
        var lines = MarkdownRenderer.RenderLines("Test: \\( v \\ll c \\) end.", 80, ColorMode.None);
        string.Join("", lines).ShouldContain(" ≪ ");
    }

    [Fact]
    public void LooseLatex_DivInsideLatexBracket_NotSubstitutedInProse()
    {
        // The \[..\] form should NOT have its body substituted by the
        // prose-Unicode pass — the math grammar owns it. We only verify the
        // body isn't being double-handled, not the final spacing (that's a
        // separate concern: the latex grammar needs `\div` added to its
        // `rel` rules to get the same `Visit(Rel)`-style spaced rendering
        // that `\ll` and `\approx` get; currently it falls through `cmd`
        // and juxtaposes without spaces).
        var lines = MarkdownRenderer.RenderLines("\\[ a \\div b \\]", 80, ColorMode.None);
        string.Join("", lines).ShouldContain("÷");
        // Either spaced (after the future latex-grammar fix) or unspaced
        // (current behaviour) — what we explicitly DON'T want is the raw
        // `\div` to survive into the output.
        string.Join("", lines).ShouldNotContain("\\div");
    }

    // ── Bare \boxed{} (math-benchmark final-answer convention) ───────────

    [Fact]
    public void BareBoxed_OutsideMathDelimiters_RendersAsMath()
    {
        // Qwen-Math / DeepSeek-R1-style "final answer" emission: \boxed{...}
        // sits in prose with no $$/\[/$/\( wrapper. Renderer must still treat
        // it as math, otherwise the literal "\boxed{...}" leaks into the page.
        var md = "Energy: \\boxed{E = mc^2 + \\frac{1}{2}mv^2}";
        var lines = MarkdownRenderer.RenderLines(md, 80, ColorMode.None);
        var joined = string.Join("", lines);
        joined.ShouldNotContain("\\boxed");
        joined.ShouldContain("Energy:");
        joined.ShouldContain("mc²");
    }

    [Fact]
    public void BareBoxed_NestedBraces_BalancesCorrectly()
    {
        // The body has a nested \frac{...}{...} — a non-greedy regex would
        // stop at the first '}' and leave "}mv^2}" stranded as prose. The
        // balanced-brace scanner has to walk the whole body.
        var md = "Result: \\boxed{\\frac{1}{2}mv^2}";
        var lines = MarkdownRenderer.RenderLines(md, 80, ColorMode.None);
        var joined = string.Join("", lines);
        joined.ShouldNotContain("\\boxed");
        joined.ShouldNotContain("\\frac");
        joined.ShouldNotContain("}mv");
    }

    [Fact]
    public void BareBoxed_InsideExistingMathSpan_NotDoubleWrapped()
    {
        // A \boxed{} already inside $$...$$ must be left alone — the math
        // pipeline's own handler renders the body. Double-wrapping with $
        // would break the dollar-balanced parse.
        var md = "$$\\boxed{x^2}$$";
        var lines = MarkdownRenderer.RenderLines(md, 80, ColorMode.None);
        string.Join("", lines).ShouldNotContain("\\boxed");
    }

    [Fact]
    public void BareBoxed_PreservesProseAroundIt()
    {
        // The surrounding sentence text must survive the wrap. Pre-wrap
        // prose, the boxed math, post-wrap prose — all three regions
        // intact.
        var md = "Before \\boxed{x} after.";
        var lines = MarkdownRenderer.RenderLines(md, 80, ColorMode.None);
        var joined = string.Join("", lines);
        joined.ShouldContain("Before");
        joined.ShouldContain("after.");
    }

    // ── Real-world model output patterns ─────────────────────────────────

    [Fact]
    public void DisplayMath_InsideListItem_NoLiteralBracketsLeak()
    {
        // Mirrors what DeepSeek-R1 emits for "is 131 prime": a numbered list item
        // whose body contains a display-math block. The \[ and \] markers must
        // not survive into the rendered output.
        var md = "- **7:**\n   \\[\n   131 ÷ 7 ≈ 18.714...\n   \\]\n   Not an integer.";
        var lines = MarkdownRenderer.RenderLines(md, 80, ColorMode.None);
        var joined = string.Join("\n", lines);
        joined.ShouldNotContain("\\[");
        joined.ShouldNotContain("\\]");
        joined.ShouldContain("131");
    }

    [Fact]
    public void DisplayMath_InsideNestedListItem_NoLiteralBracketsLeak()
    {
        // The actual DeepSeek-R1 "is 131 prime" pattern: ordered top-level list
        // containing nested unordered items whose body has \[ ... \] math.
        // Indentation: ordered marker at col 0, continuation at col 3; nested
        // unordered marker at col 3, continuation at col 5.
        var md =
            "1. **List Prime Numbers Up to the Square Root of 131:**\n" +
            "\n" +
            "   - **7:**\n" +
            "     \\[\n" +
            "     131 ÷ 7 ≈ 18.714...\n" +
            "     \\]\n" +
            "     Not an integer.\n";
        var lines = MarkdownRenderer.RenderLines(md, 80, ColorMode.None);
        var joined = string.Join("\n", lines);
        joined.ShouldNotContain("\\[");
        joined.ShouldNotContain("\\]");
        joined.ShouldContain("131");
    }

    [Fact]
    public void BoxedMacro_StrippedFromRender()
    {
        // \boxed{X} is a LaTeX macro for drawing a border around X. The math
        // grammar doesn't know it — must be pre-expanded before parsing or it
        // surfaces as literal "\boxed" in the output.
        var lines = MarkdownRenderer.RenderLines("$$\\boxed{x = 7}$$", 80, ColorMode.None);
        var joined = string.Join("", lines);
        joined.ShouldNotContain("\\boxed");
        joined.ShouldContain("7");
    }

    [Fact]
    public void TextMacro_PreservesSpacesInContent()
    {
        // \text{X} switches to text mode inside math. The grammar would otherwise
        // tokenise each letter as a math-italic variable and lose the spaces.
        var lines = MarkdownRenderer.RenderLines("$$\\text{is a prime number}$$", 80, ColorMode.None);
        var joined = string.Join("", lines);
        joined.ShouldNotContain("\\text");
        joined.ShouldContain("is a prime number");
    }

    [Fact]
    public void BoxedTextCombination_RendersCleanly()
    {
        // The end-of-DeepSeek-thinking-block answer pattern.
        var lines = MarkdownRenderer.RenderLines("\\[\\boxed{131\\text{ is a prime number}}\\]", 80, ColorMode.None);
        var joined = string.Join("", lines);
        joined.ShouldNotContain("\\boxed");
        joined.ShouldNotContain("\\text");
        joined.ShouldContain("131");
        joined.ShouldContain("is a prime number");
    }

    [Fact]
    public void Sqrt_WithUnicodeOperators_StillRendersSqrtGlyph()
    {
        // Real-world output from a chain-of-thought model: "\sqrt{131} ≈ 11.45".
        // The math grammar's lexer has no rule for U+2248 (≈), so a naive parse
        // bails on it and \sqrt never gets to render. Pre-processing must
        // substitute the unknown char with a placeholder so the rest of the
        // expression (including \sqrt) renders normally.
        var lines = MarkdownRenderer.RenderLines("$\\sqrt{131} ≈ 11.45$", 80, ColorMode.None);
        var joined = string.Join("", lines);
        joined.ShouldNotContain("\\sqrt");
        joined.ShouldContain("√"); // √
        joined.ShouldContain("131");
        joined.ShouldContain("11.45");
    }

    [Fact]
    public void Sqrt_StandaloneStillRenders()
    {
        var lines = MarkdownRenderer.RenderLines("$\\sqrt{131}$", 80, ColorMode.None);
        var joined = string.Join("", lines);
        joined.ShouldContain("√"); // √
        joined.ShouldContain("131");
    }

    [Fact]
    public void UnicodeOperators_PreservedInOutput()
    {
        // ÷ ≈ ≤ should survive an arithmetic expression and not abort the parse.
        var lines = MarkdownRenderer.RenderLines("$131 ÷ 7 ≈ 18.71$", 80, ColorMode.None);
        var joined = string.Join("", lines);
        joined.ShouldContain("÷"); // ÷
        joined.ShouldContain("≈"); // ≈
        joined.ShouldContain("131");
        joined.ShouldContain("18.71");
    }

    [Fact]
    public void LooseLatexInProse_DivConvertedToUnicode()
    {
        // Model emits LaTeX commands without $…$ wrappers. The user shouldn't
        // see "\div" — convert common math commands to their Unicode glyph
        // in prose regions.
        var lines = MarkdownRenderer.RenderLines("131\\div2 = 65.5", 80, ColorMode.None);
        var joined = string.Join("", lines);
        joined.ShouldNotContain("\\div");
        joined.ShouldContain("÷");
    }

    [Fact]
    public void LooseLatexInProse_QuadConvertedToSpace()
    {
        var lines = MarkdownRenderer.RenderLines("131/2 = 65.5\\quad(Not an integer)", 80, ColorMode.None);
        var joined = string.Join("", lines);
        joined.ShouldNotContain("\\quad");
        joined.ShouldContain("(Not an integer)");
    }

    [Fact]
    public void BackslashThinSpace_StrippedInMath()
    {
        // \boxed{131\,, prime} contains a LaTeX thin-space \, that the cmd
        // rule can't tokenise. Without the \<non-letter> pre-pass, the whole
        // boxed body falls back to literal.
        var lines = MarkdownRenderer.RenderLines("\\[\\boxed{131\\,, prime}\\]", 80, ColorMode.None);
        var joined = string.Join("", lines);
        joined.ShouldNotContain("\\,");
        joined.ShouldNotContain("\\boxed");
        joined.ShouldContain("131");
        joined.ShouldContain("prime");
    }

    [Fact]
    public void Dots_RenderAsEllipsis_BothProseAndMath()
    {
        // Power-series and "and so on" lines in model output use \dots
        // (and friends). Without explicit handling they fell through to
        // literal "\dots" in the rendered output.
        var prose = MarkdownRenderer.RenderLines("a + b + \\dots + z", 80, ColorMode.None);
        string.Join("", prose).ShouldContain("…");
        string.Join("", prose).ShouldNotContain("\\dots");

        var math = MarkdownRenderer.RenderLines("$1 + 2 + \\dots + n$", 80, ColorMode.None);
        string.Join("", math).ShouldContain("…");
        string.Join("", math).ShouldNotContain("\\dots");
    }

    [Fact]
    public void Ll_RendersAsUnicodeRelation_NotJuxtaposedCmd()
    {
        // The "for v \ll c" small-velocity expansion pattern. Without
        // explicit handling the grammar's whitespace-discard collapses
        // "v \ll c" → "v\llc" via juxtaposition of the cmd("\ll") atom.
        var lines = MarkdownRenderer.RenderLines("for $v \\ll c$ the expansion is", 80, ColorMode.None);
        var joined = string.Join("", lines);
        joined.ShouldNotContain("\\ll");
        joined.ShouldContain("≪"); // ≪
    }

    [Fact]
    public void BinaryRelations_HaveSurroundingSpaces()
    {
        // LALR.CC 3.1's `rel` token + E → E rel T production means \approx /
        // \leq / \neq / \ll / \in / etc. now flow through Visit(Rel) instead
        // of being juxtaposed cmd atoms. Output should contain "X ≈ Y" with
        // single spaces around the glyph — no more "X≈Y" collapse.
        foreach (var (src, sym) in new[] {
            ("$\\gamma \\approx 1$", "≈"),
            ("$a \\leq b$",          "≤"),
            ("$a \\geq b$",          "≥"),
            ("$a \\neq b$",          "≠"),
            ("$v \\ll c$",           "≪"),
            ("$a \\gg b$",           "≫"),
            ("$x \\in S$",           "∈"),
            ("$a \\equiv b$",        "≡"),
            ("$A \\to B$",           "→"),
            ("$a \\pm b$",           "±"),
        })
        {
            var joined = string.Join("", MarkdownRenderer.RenderLines(src, 80, ColorMode.None));
            joined.ShouldContain($" {sym} ", customMessage: $"expected ' {sym} ' with surrounding spaces in: {joined}");
        }
    }

    [Fact]
    public void MathRegion_LeavesCommandsAlone()
    {
        // Inside $…$ we must NOT prose-substitute \sum — the math renderer
        // needs the original token to recognise it as a big operator and
        // fold scripts as limits above/below.
        var lines = MarkdownRenderer.RenderLines("$\\sum_{i=1}^{n} i$", 80, ColorMode.None);
        var joined = string.Join("", lines);
        joined.ShouldContain("∑"); // sum glyph
    }

    [Fact]
    public void TextBody_ResolvesExplicitSpaceMacro()
    {
        // The DeepSeek "is 131 prime" final-answer pattern:
        //   \[\boxed{\text{Yes,\ 131\ is\ a\ prime\ number}}\]
        // `\ ` is LaTeX's explicit control-space macro — must render as a
        // regular space inside the \text{} body, not survive as literal "\ ".
        var lines = MarkdownRenderer.RenderLines(
            "\\[\\boxed{\\text{Yes,\\ 131\\ is\\ a\\ prime\\ number}}\\]",
            80, ColorMode.None);
        var joined = string.Join("", lines);
        joined.ShouldNotContain("\\ ");
        joined.ShouldNotContain("\\,");
        joined.ShouldContain("Yes, 131 is a prime number");
    }

    [Fact]
    public void BeginArray_RendersAsMultilineTextBlock()
    {
        // Real DeepSeek output captured via /raw: list of continents inside
        // a 2-column array, the whole thing wrapped in \boxed{}. The math
        // grammar can't parse \begin{}/\end{} — the env must be expanded as
        // a multi-line text block (rows preserved, \text{} bodies resolved).
        // \[ and \] on their own lines so Markdig sees $$ as block math.
        var md =
            "\\[\n" +
            "\\boxed{\n" +
            "\\begin{array}{ll}\n" +
            "1. & \\text{Asia} \\\\\n" +
            "2. & \\text{Africa} \\\\\n" +
            "3. & \\text{Europe} \\\\\n" +
            "\\end{array}\n" +
            "}\n" +
            "\\]\n";
        var lines = MarkdownRenderer.RenderLines(md, 80, ColorMode.None);
        var joined = string.Join("\n", lines);
        joined.ShouldNotContain("\\begin");
        joined.ShouldNotContain("\\end");
        joined.ShouldNotContain("\\text");
        joined.ShouldNotContain("&");
        joined.ShouldContain("Asia");
        joined.ShouldContain("Africa");
        joined.ShouldContain("Europe");
        // Each numbered row should land on its own output line, not be glued
        // together by the math renderer's whitespace-skip.
        lines.Count(l => l.Contains("Asia") || l.Contains("Africa") || l.Contains("Europe"))
            .ShouldBe(3);
    }
}
