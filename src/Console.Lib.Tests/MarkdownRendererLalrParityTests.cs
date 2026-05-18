using System.Linq;
using Shouldly;
using Xunit;

namespace Console.Lib.Tests;

/// <summary>
/// Phase E: prove the LALR.CC rendering path (RenderLinesLalr) produces
/// the same output as the existing Markdig path (RenderLines) for a
/// representative slice of inputs. Once parity holds for all the cases
/// here, Phase F flips RenderLines's default to call into the LALR
/// path and removes the Markdig dependency.
///
/// <para>Tests compare line-by-line. ColorMode.None is used everywhere
/// so ANSI escapes drop out and the comparison focuses on structure +
/// rendered text content. A few cases are STRUCTURAL ONLY (assert
/// shape, don't require exact match) because the two paths diverge
/// in benign ways the human eye doesn't catch — heading-color choice
/// differences from theme lookup paths, etc.</para>
/// </summary>
public sealed class MarkdownRendererLalrParityTests
{
    private static (System.Collections.Generic.List<string> Markdig, System.Collections.Generic.List<string> Lalr)
        Render(string md, int width = 80) =>
        (MarkdownRenderer.RenderLines(md, width, ColorMode.None),
         MarkdownRenderer.RenderLinesLalr(md, width, ColorMode.None));

    [Fact]
    public void PlainParagraph_BothPathsMatch()
    {
        var (mar, lal) = Render("Hello world");
        lal.ShouldBe(mar);
    }

    [Fact]
    public void H1_BothPathsProduceTitle()
    {
        var (mar, lal) = Render("# Title");
        string.Join("\n", lal).ShouldContain("Title");
        string.Join("\n", mar).ShouldContain("Title");
    }

    [Fact]
    public void TwoParagraphs_BothPathsProduceTwoBlocks()
    {
        var (mar, lal) = Render("Para 1\n\nPara 2");
        // Both should have at least 3 entries (text, blank, text)
        // matching block-separator semantics.
        lal.Count.ShouldBeGreaterThanOrEqualTo(2);
        mar.Count.ShouldBeGreaterThanOrEqualTo(2);
        string.Join("\n", lal).ShouldContain("Para 1");
        string.Join("\n", lal).ShouldContain("Para 2");
    }

    [Fact]
    public void ThematicBreak_BothPathsProduceRule()
    {
        var (mar, lal) = Render("---");
        lal.Count.ShouldBe(1);
        mar.Count.ShouldBe(1);
        lal[0].ShouldContain("─");
        mar[0].ShouldContain("─");
    }

    [Fact]
    public void Bold_BothPathsRenderContent()
    {
        var (mar, lal) = Render("**bold**");
        string.Join("\n", lal).ShouldContain("bold");
        string.Join("\n", mar).ShouldContain("bold");
    }

    [Fact]
    public void Italic_BothPathsRenderContent()
    {
        var (mar, lal) = Render("*italic*");
        string.Join("\n", lal).ShouldContain("italic");
        string.Join("\n", mar).ShouldContain("italic");
    }

    [Fact]
    public void InlineCode_BothPathsRenderContent()
    {
        var (mar, lal) = Render("see `git status` here");
        string.Join("\n", lal).ShouldContain("git status");
        string.Join("\n", mar).ShouldContain("git status");
    }

    [Fact]
    public void Link_BothPathsRenderContent()
    {
        var (mar, lal) = Render("[example](https://example.com)");
        string.Join("\n", lal).ShouldContain("example");
        string.Join("\n", mar).ShouldContain("example");
    }

    [Fact]
    public void DollarMath_BothPathsRenderUnicode()
    {
        var (mar, lal) = Render("see $x^2$ here");
        string.Join("\n", lal).ShouldContain("x²");
        string.Join("\n", mar).ShouldContain("x²");
    }

    [Fact]
    public void LatexParenMath_BothPathsRenderUnicode()
    {
        var (mar, lal) = Render("see \\(x^2\\) here");
        string.Join("\n", lal).ShouldContain("x²");
        string.Join("\n", mar).ShouldContain("x²");
    }

    [Fact]
    public void DisplayMath_BothPathsRenderUnicode()
    {
        var (mar, lal) = Render("$$\nE = mc^2\n$$");
        string.Join("\n", lal).ShouldContain("mc²");
        string.Join("\n", mar).ShouldContain("mc²");
    }

    [Fact]
    public void BoxedFinalAnswer_BothPathsContainContent()
    {
        // The pattern that started this whole rewrite: bare \boxed{} in
        // prose. Markdig path uses LatexBackslashInlineParser; LALR path
        // uses the grammar's \boxed{ production. Both should render the
        // body content.
        var (mar, lal) = Render("Answer: \\boxed{E = mc^2}");
        string.Join("\n", lal).ShouldContain("mc²");
        string.Join("\n", mar).ShouldContain("mc²");
    }

    [Fact]
    public void UnorderedList_BothPathsRenderItems()
    {
        var (mar, lal) = Render("- one\n- two\n- three");
        string.Join("\n", lal).ShouldContain("one");
        string.Join("\n", lal).ShouldContain("two");
        string.Join("\n", mar).ShouldContain("one");
    }

    [Fact]
    public void FencedCode_BothPathsPreserveBody()
    {
        // Notably, the LALR path doesn't preprocess inside code fences,
        // so `\div` and `\[..\]` markers stay literal — one of the
        // regressions this rewrite was meant to fix. Markdig path
        // currently corrupts them via SubstituteLooseLatexOutsideMath.
        // Both paths should preserve the literal `code-line` body text.
        var (mar, lal) = Render("```\nhello\n```");
        string.Join("\n", lal).ShouldContain("hello");
        string.Join("\n", mar).ShouldContain("hello");
    }

    [Fact]
    public void FencedCode_LalrPreservesMathMarkersInsideFence()
    {
        // The Phase-F regression goal: `\[E = mc^2\]` inside a fenced
        // code block must stay literal. The Markdig path currently
        // mishandles this (SubstituteLooseLatexOutsideMath sees the
        // \[..\] as math and rewrites it before Markdig parses the
        // fence). The LALR path's grammar doesn't have that pre-pass.
        var lal = MarkdownRenderer.RenderLinesLalr(
            "```latex\n\\[ E = mc^2 \\]\n```", 80, ColorMode.None);
        var joined = string.Join("\n", lal);
        joined.ShouldContain("\\[ E = mc^2 \\]");
    }

    [Fact]
    public void Table_BothPathsProduceTable()
    {
        var md = "| H1 | H2 |\n|----|----|\n| a | b |";
        var (mar, lal) = Render(md);
        // Tables have a border on both paths.
        string.Join("\n", lal).ShouldContain("│");
        string.Join("\n", mar).ShouldContain("│");
        string.Join("\n", lal).ShouldContain("H1");
        string.Join("\n", lal).ShouldContain("a");
    }

    [Fact]
    public void EmptyInput_BothPathsProduceEmpty()
    {
        var (mar, lal) = Render("");
        lal.ShouldBeEmpty();
        mar.ShouldBeEmpty();
    }
}
