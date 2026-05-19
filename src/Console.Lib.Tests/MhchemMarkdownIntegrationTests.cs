using Console.Lib;
using Shouldly;
using Xunit;

namespace Console.Lib.Tests;

/// <summary>
/// End-to-end round-trip from a markdown source containing <c>\ce{...}</c>
/// inside a math span, through <see cref="MarkdownRenderer.RenderLines"/>,
/// to the final VT output. Confirms the macro is wired into
/// <c>ExpandLatexMacros</c> (in <c>DIR.Lib.Markdown.MarkdownMacros</c>)
/// for both the inline (<c>\(...\)</c>) and block (<c>$$...$$</c>) math
/// forms. Pure <see cref="DIR.Lib.Markdown.Mhchem"/> state-machine tests
/// live in <c>DIR.Lib.Tests.MhchemTests</c> — those don't reach into
/// Console.Lib at all and DIR.Lib.Tests deliberately doesn't reference
/// Console.Lib.
/// </summary>
public sealed class MhchemMarkdownIntegrationTests
{
    [Fact]
    public void MarkdownRender_CeInsideInlineMath_EmitsUnicode()
    {
        var src = @"The reaction is \(\ce{2H2 + O2 -> 2H2O}\).";
        var lines = MarkdownRenderer.RenderLines(src, width: 200, ColorMode.None);
        var joined = string.Join("\n", lines);
        joined.ShouldContain("2H₂ + O₂ → 2H₂O");
    }

    [Fact]
    public void MarkdownRender_CeWithIsotope_StripsLeadingCaret()
    {
        // The whole point of \ce — leading ^{N} works without an explicit base.
        var src = @"\(\ce{^{238}U -> ^{234}Th}\)";
        var lines = MarkdownRenderer.RenderLines(src, width: 200, ColorMode.None);
        var joined = string.Join("\n", lines);
        joined.ShouldContain("²³⁸U → ²³⁴Th");
        joined.ShouldNotContain("^{");
    }

    [Fact]
    public void MarkdownRender_CeInDisplayMath_EmitsUnicode()
    {
        var src = "$$\\ce{Fe^3+ + 3OH^- -> Fe(OH)3}$$";
        var lines = MarkdownRenderer.RenderLines(src, width: 200, ColorMode.None);
        var joined = string.Join("\n", lines);
        joined.ShouldContain("Fe³⁺ + 3OH⁻ → Fe(OH)₃");
    }
}
