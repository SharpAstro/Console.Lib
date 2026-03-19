using Console.Lib;
using Shouldly;
using Xunit;

namespace Console.Lib.Tests;

public sealed class MarkdownRendererTests
{
    private const string Reset = "\e[0m";
    private const string Bold = "\e[1m";
    private const string Dim = "\e[2m";
    private const string Italic = "\e[3m";
    private const string Underline = "\e[4m";
    private const string Cyan = "\e[36m";
    private const string BoldBlue = "\e[1;34m";
    private const string BoldCyan = "\e[1;36m";
    private const string BoldWhite = "\e[1;97m";

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

    [Fact]
    public void FormatInline_Bold()
    {
        var result = MarkdownRenderer.FormatInline("**hello**", ColorMode.TrueColor);
        result.ShouldBe($"{Reset}{Bold}hello{Reset}");
    }

    [Fact]
    public void FormatInline_Italic()
    {
        var result = MarkdownRenderer.FormatInline("*hello*", ColorMode.TrueColor);
        result.ShouldBe($"{Reset}{Italic}hello{Reset}");
    }

    [Fact]
    public void FormatInline_BoldItalic()
    {
        var result = MarkdownRenderer.FormatInline("***hello***", ColorMode.TrueColor);
        // Markdig nests emphasis: <em><strong>hello</strong></em>
        // Visually identical — bold+italic "hello" then reset
        result.ShouldContain(Bold);
        result.ShouldContain(Italic);
        result.ShouldContain("hello");
        result.ShouldEndWith(Reset);
        MarkdownRenderer.VisibleLength(result).ShouldBe(5);
    }

    [Fact]
    public void FormatInline_Link()
    {
        var result = MarkdownRenderer.FormatInline("[click](http://example.com)", ColorMode.TrueColor);
        result.ShouldBe($"{Underline}{Cyan}click{Reset}{Dim} (http://example.com){Reset}");
    }

    [Fact]
    public void FormatInline_BackslashEscape()
    {
        var result = MarkdownRenderer.FormatInline("\\*not italic\\*", ColorMode.TrueColor);
        result.ShouldBe("*not italic*");
    }

    [Fact]
    public void FormatInline_PlainText_Unchanged()
    {
        var result = MarkdownRenderer.FormatInline("no formatting here", ColorMode.TrueColor);
        result.ShouldBe("no formatting here");
    }

    [Fact]
    public void FormatInline_NestedBoldInItalic()
    {
        // *italic **bold** italic*
        var result = MarkdownRenderer.FormatInline("*italic **bold** italic*", ColorMode.TrueColor);
        // After first *: italic=true → Reset+Italic
        // "italic " literal
        // After **: bold=true → Reset+Bold+Italic
        // "bold" literal
        // After **: bold=false → Reset+Italic
        // " italic" literal
        // After *: italic=false → Reset
        result.ShouldBe($"{Reset}{Italic}italic {Reset}{Bold}{Italic}bold{Reset}{Italic} italic{Reset}");
    }

    // ── Headers ───────────────────────────────────────────────────────

    [Fact]
    public void RenderLines_H1_BoldBlue()
    {
        var lines = MarkdownRenderer.RenderLines("# Title", 80, ColorMode.TrueColor);
        lines.Count.ShouldBe(1);
        lines[0].ShouldBe($"{BoldBlue}Title{Reset}");
    }

    [Fact]
    public void RenderLines_H2_BoldCyan()
    {
        var lines = MarkdownRenderer.RenderLines("## Subtitle", 80, ColorMode.TrueColor);
        lines.Count.ShouldBe(1);
        lines[0].ShouldBe($"{BoldCyan}Subtitle{Reset}");
    }

    [Fact]
    public void RenderLines_H3_BoldWhite()
    {
        var lines = MarkdownRenderer.RenderLines("### Section", 80, ColorMode.TrueColor);
        lines.Count.ShouldBe(1);
        lines[0].ShouldBe($"{BoldWhite}Section{Reset}");
    }

    [Fact]
    public void RenderLines_HeaderWithTrailingHashes()
    {
        var lines = MarkdownRenderer.RenderLines("## Title ##", 80, ColorMode.TrueColor);
        lines[0].ShouldBe($"{BoldCyan}Title{Reset}");
    }

    // ── Horizontal rules ──────────────────────────────────────────────

    [Fact]
    public void RenderLines_HorizontalRule_Dashes()
    {
        var lines = MarkdownRenderer.RenderLines("---", 40, ColorMode.TrueColor);
        lines.Count.ShouldBe(1);
        lines[0].ShouldBe($"{Dim}{new string('─', 40)}{Reset}");
    }

    [Fact]
    public void RenderLines_HorizontalRule_Asterisks()
    {
        var lines = MarkdownRenderer.RenderLines("***", 20, ColorMode.TrueColor);
        lines[0].ShouldBe($"{Dim}{new string('─', 20)}{Reset}");
    }

    // ── Unordered lists ───────────────────────────────────────────────

    [Fact]
    public void RenderLines_UnorderedList()
    {
        var md = "- First\n- Second\n- Third";
        var lines = MarkdownRenderer.RenderLines(md, 80, ColorMode.TrueColor);
        lines.Count.ShouldBe(3);
        lines[0].ShouldContain("•");
        lines[0].ShouldContain("First");
        lines[1].ShouldContain("Second");
    }

    [Fact]
    public void RenderLines_NestedUnorderedList()
    {
        var md = "- Outer\n  - Inner";
        var lines = MarkdownRenderer.RenderLines(md, 80, ColorMode.TrueColor);
        lines.Count.ShouldBe(2);
        lines[0].ShouldContain("•");
        lines[1].ShouldContain("◦");
    }

    // ── Ordered lists ─────────────────────────────────────────────────

    [Fact]
    public void RenderLines_OrderedList()
    {
        var md = "1. First\n2. Second";
        var lines = MarkdownRenderer.RenderLines(md, 80, ColorMode.TrueColor);
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
        var lines = MarkdownRenderer.RenderLines(md, 80, ColorMode.TrueColor);

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
        var lines = MarkdownRenderer.RenderLines(md, 80, ColorMode.TrueColor);
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
        var lines = MarkdownRenderer.RenderLines(md, 80, ColorMode.TrueColor);
        lines.Count.ShouldBeGreaterThan(5);
    }

    [Fact]
    public void RenderLines_EmptyInput()
    {
        var lines = MarkdownRenderer.RenderLines("", 80, ColorMode.TrueColor);
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
}
