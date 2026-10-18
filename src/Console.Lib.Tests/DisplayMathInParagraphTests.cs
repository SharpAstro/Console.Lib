using System.Text.RegularExpressions;
using DIR.Lib;
using Shouldly;
using Xunit;

namespace Console.Lib.Tests;

/// <summary>
/// Display math written inside a paragraph (<c>$$..$$</c>, <c>\[..\]</c>) rasters as a block: the paragraph
/// splits into text, raster, text at each such span, wherever it sits, as Pandoc's <c>tex_math_dollars</c>
/// does. Headings and table cells stay inline, because a raster cannot sit in one row, and so does
/// everything under <see cref="ColorMode.None"/> or without a raster mode.
/// </summary>
public sealed class DisplayMathInParagraphTests
{
    private static readonly string MathFont = Path.Combine(AppContext.BaseDirectory, "Fonts", "STIX2Math.otf");

    public static TheoryData<BoxRenderMode> Modes() => [BoxRenderMode.Sixel, BoxRenderMode.Sextant, BoxRenderMode.HalfBlock];

    private static List<string> Raster(string markdown, BoxRenderMode mode, int width = 80)
        => MarkdownRenderer.RenderLines(markdown, width, ColorMode.TrueColor, mathMode: mode, mathFontPath: MathFont);

    private static List<string> NoRaster(string markdown, int width = 80)
        => MarkdownRenderer.RenderLines(markdown, width, ColorMode.TrueColor);

    /// <summary>The fenced display block of <paramref name="body"/>, which has always rastered.</summary>
    private static List<string> Fenced(string body, BoxRenderMode mode)
    {
        var lines = Raster($"$$\n{body}\n$$", mode);
        lines.ShouldNotBe(NoRaster($"$$\n{body}\n$$"), $"the fenced {body} has to raster for the comparison to mean anything");
        return lines;
    }

    private static string StripEscapes(string line) => Regex.Replace(line, "\e\\[[0-9;]*[@-~]|\e\\][^\a]*\a", "");

    [Theory]
    [MemberData(nameof(Modes))]
    public void AloneInAParagraph_RastersExactlyAsTheFencedForm(BoxRenderMode mode)
    {
        File.Exists(MathFont).ShouldBeTrue("the test project copies mdcat's STIX Two Math next to the tests");

        Raster("$$x$$", mode).ShouldBe(Fenced("x", mode));
        Raster(@"\[x\]", mode).ShouldBe(Fenced("x", mode));
        Raster(@"$$\frac{a}{b}$$", mode).ShouldBe(Fenced(@"\frac{a}{b}", mode));
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void InsideASentence_SplitsIntoTextRasterText(BoxRenderMode mode)
    {
        Raster("a $$x$$ b", mode).ShouldBe(["a", .. Fenced("x", mode), "b"]);
        Raster(@"a \[x\] b", mode).ShouldBe(["a", .. Fenced("x", mode), "b"]);
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void AcrossASoftLineBreak_SplitsWithNoEmptySegment(BoxRenderMode mode)
    {
        // The span on a line of its own between two lines of text: the soft breaks only separated it.
        Raster("a\n$$x$$\nb", mode).ShouldBe(["a", .. Fenced("x", mode), "b"]);
        // The span itself running across the break.
        Raster("a $$x +\ny$$ b", mode).ShouldBe(["a", .. Fenced("x +\ny", mode), "b"]);
        // At either end of the paragraph: no empty text segment before or after the raster.
        Raster("$$x$$\nb", mode).ShouldBe([.. Fenced("x", mode), "b"]);
        Raster("a\n$$x$$", mode).ShouldBe(["a", .. Fenced("x", mode)]);
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void TwoSpans_EachSplitsTheParagraph(BoxRenderMode mode)
        => Raster("a $$x$$ b $$y$$ c", mode).ShouldBe(["a", .. Fenced("x", mode), "b", .. Fenced("y", mode), "c"]);

    [Theory]
    [MemberData(nameof(Modes))]
    public void InlineDelimiters_StayInline(BoxRenderMode mode)
    {
        Raster("a $x$ b", mode).ShouldBe(NoRaster("a $x$ b"));
        Raster(@"a \(x\) b", mode).ShouldBe(NoRaster(@"a \(x\) b"));
        Raster("a $x$ b", mode).Count.ShouldBe(1);
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void AHeading_StaysInline(BoxRenderMode mode)
    {
        var lines = Raster("# a $$x$$ b", mode);

        lines.ShouldBe(NoRaster("# a $$x$$ b"));
        lines.Count.ShouldBe(1);
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void ATableCell_StaysInline(BoxRenderMode mode)
    {
        const string md = "| f | g |\n|---|---|\n| a $$x$$ b | c |";

        Raster(md, mode).ShouldBe(NoRaster(md));
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void InsideEmphasis_StaysInline(BoxRenderMode mode)
        => Raster("a **$$x$$** b", mode).ShouldBe(NoRaster("a **$$x$$** b"));

    [Theory]
    [MemberData(nameof(Modes))]
    public void UnderColorModeNone_NothingSplitsAndNoEscapeIsWritten(BoxRenderMode mode)
    {
        const string md = "a $$x$$ b\n\n$$y$$";
        var lines = MarkdownRenderer.RenderLines(md, 80, ColorMode.None, mathMode: mode, mathFontPath: MathFont);

        lines.ShouldBe(MarkdownRenderer.RenderLines(md, 80, ColorMode.None));
        lines.ShouldAllBe(l => !l.Contains('\e'));
    }

    [Fact]
    public void WithoutARasterMode_NothingSplits()
    {
        var lines = NoRaster("a $$x$$ b");

        lines.Count.ShouldBe(1);
        StripEscapes(lines[0]).ShouldBe("a x b");
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void ADollarAmount_StaysOneParagraph(BoxRenderMode mode)
    {
        var lines = Raster("It costs $5 today.", mode);

        lines.ShouldBe(["It costs $5 today."]);
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void ASpanTheBoxPathTurnsAway_StaysInItsTextSegment(BoxRenderMode mode)
    {
        // An environment has no box: that span keeps its Unicode rendering inside the text, and only the
        // span that rasters splits the paragraph. (\text was the example until it got a box.)
        var lines = Raster(@"a $$x$$ b $$\begin{matrix} hi \end{matrix}$$ c", mode);

        lines[..^1].ShouldBe(["a", .. Fenced("x", mode)]);
        StripEscapes(lines[^1]).ShouldBe("b hi c");
    }

    [Theory]
    [InlineData(@"\{1, 2, 3\}")]
    [InlineData(@"\{a\}")]
    [InlineData(@"\left\{ 1, 2 \right\}")]
    [InlineData(@"\lbrace x \rbrace")]
    public void EscapedBraces_Raster(string body)
    {
        // DIR.Lib's preparation helper keeps a set's braces for the grammar, which draws them as a curly
        // bracket box (its BraceSetTests read the shapes). The box path used to have no brace, and these
        // took the Unicode path, inline. This asserts the route.
        foreach (var mode in new[] { BoxRenderMode.Sixel, BoxRenderMode.Sextant, BoxRenderMode.HalfBlock })
        {
            Raster($"$${body}$$", mode).ShouldBe(Fenced(body, mode));
            Raster($"a $${body}$$ b", mode).ShouldBe(["a", .. Fenced(body, mode), "b"]);
        }
    }

    [Theory]
    [InlineData(@"\{1, 2, 3\}", "{1,2,3}")]
    [InlineData(@"\{a\}", "{a}")]
    public void EscapedBraces_KeepTheirBraces_OnTheUnicodePath(string body, string expected)
    {
        // Spaces aside, which are DIR.Lib's to set.
        var lines = NoRaster($"$${body}$$");

        StripEscapes(string.Join("\n", lines)).Replace(" ", "").ShouldContain(expected);
    }

    [Theory]
    [InlineData(@"\sqrt[n]{x}")]
    [InlineData("[a, b]")]
    public void SquareBrackets_StillRaster(string body)
    {
        foreach (var mode in new[] { BoxRenderMode.Sixel, BoxRenderMode.Sextant, BoxRenderMode.HalfBlock })
        {
            Raster($"$${body}$$", mode).ShouldBe(Fenced(body, mode));
            Raster($"a $${body}$$ b", mode).ShouldBe(["a", .. Fenced(body, mode), "b"]);
        }
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void InAListItem_TheMarkerLeadsAndTheTextAfterIsIndented(BoxRenderMode mode)
    {
        var lines = Raster("- a $$x$$ b", mode);

        StripEscapes(lines[0]).ShouldBe("  • a");
        lines[1..^1].ShouldBe(CentredInTheItem("x", mode));
        lines[^1].ShouldBe("     b");
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void AListItemThatOpensWithDisplayMath_KeepsItsMarker(BoxRenderMode mode)
    {
        var lines = Raster("- $$x$$", mode);

        StripEscapes(lines[0]).ShouldBe("  •");
        lines[1..].ShouldBe(CentredInTheItem("x", mode));
    }

    /// <summary>
    /// The fenced block of <paramref name="body"/> centred in an item's text column, which in an 80-column
    /// document is the 75 columns after its five of indent: the block as a 75-column document would centre
    /// it, five columns further right. It used to sit at column 0, left of the item's own text.
    /// </summary>
    private static List<string> CentredInTheItem(string body, BoxRenderMode mode)
        => Raster($"$$\n{body}\n$$", mode, width: 75).Select(l => "     " + l).ToList();
}
