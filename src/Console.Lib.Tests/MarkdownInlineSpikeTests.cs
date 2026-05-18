using System.Linq;
using Shouldly;
using Xunit;

namespace Console.Lib.Tests;

/// <summary>
/// Phase B: validate the LALR.CC inline grammar across all five math
/// forms. Each form pushes a dedicated lexer state and invokes the
/// LaTeX sub-parser via a rewriter; the assertions check both
/// structural shape (correct number of spans, correct body capture)
/// and rendered semantics (sub-parser actually produced the expected
/// Unicode glyphs).
/// </summary>
public sealed class MarkdownInlineSpikeTests
{
    private readonly MarkdownInlineVisitor _visitor = new();

    // ── Plain text + dollar math (Phase A coverage) ───────────────────

    [Fact]
    public void PlainText_ProducesSingleLiteral()
    {
        var spans = _visitor.Parse("hello world");
        spans.Count.ShouldBe(1);
        spans[0].ShouldBeOfType<MdLiteral>().Text.ShouldBe("hello world");
    }

    [Fact]
    public void DollarMath_ProducesMathInline()
    {
        var spans = _visitor.Parse("$x^2$");
        spans.Count.ShouldBe(1);
        var math = spans[0].ShouldBeOfType<MdMathInline>();
        math.Source.ShouldBe("x^2");
        math.Unicode.ShouldContain("x");
        math.Unicode.ShouldContain("²");
    }

    [Fact]
    public void TextThenMathThenText_ProducesThreeSpans()
    {
        var spans = _visitor.Parse("before $x^2$ after");
        spans.Count.ShouldBe(3);
        spans[0].ShouldBeOfType<MdLiteral>().Text.ShouldBe("before ");
        spans[1].ShouldBeOfType<MdMathInline>().Source.ShouldBe("x^2");
        spans[2].ShouldBeOfType<MdLiteral>().Text.ShouldBe(" after");
    }

    [Fact]
    public void MultipleMathSpans_EachInvokeSubParser()
    {
        var spans = _visitor.Parse("$a$ and $b$");
        spans.OfType<MdMathInline>().Select(m => m.Source).ShouldBe(new[] { "a", "b" });
    }

    // ── $$ display math ──────────────────────────────────────────────

    [Fact]
    public void DoubleDollarMath_ProducesMathInline()
    {
        // The lexer's longest-match resolves `$$` before bare `$`, so the
        // dollar2 rule wins and the math_dollar2 state captures the body
        // as a whole even though `$` alone has its own opener rule.
        var spans = _visitor.Parse("text $$x^2$$ more");
        spans.OfType<MdMathInline>().Single().Source.ShouldBe("x^2");
    }

    // ── \(..\) inline math ───────────────────────────────────────────

    [Fact]
    public void LatexParenMath_ProducesMathInline()
    {
        // \( opens the math_paren state; the body is a list of latex_frag
        // tokens (so backslash-prefixed LaTeX commands like \pi survive
        // the lex pass), concatenated by the visitor.
        var spans = _visitor.Parse(@"see \( e^{i\pi} + 1 = 0 \) end");
        var math = spans.OfType<MdMathInline>().Single();
        math.Source.ShouldBe(" e^{i\\pi} + 1 = 0 ");
    }

    [Fact]
    public void LatexParenMath_PreservesCommandsInBody()
    {
        // The body's `\ll` must survive intact so the LaTeX sub-parser
        // can tokenise it as a `rel`. Direct-grammar-driven path —
        // unlike the prose-substitution fallback, no upstream regex
        // touches the body.
        var spans = _visitor.Parse(@"\( v \ll c \)");
        spans.OfType<MdMathInline>().Single().Source.ShouldBe(" v \\ll c ");
    }

    // ── \[..\] inline (block-level handling lands in Phase C) ────────

    [Fact]
    public void LatexBracketMath_ProducesMathInline()
    {
        var spans = _visitor.Parse(@"intro \[ E = mc^2 \] outro");
        spans.OfType<MdMathInline>().Single().Source.ShouldBe(" E = mc^2 ");
    }

    // ── \boxed{..} with balanced braces ──────────────────────────────

    [Fact]
    public void BoxedMath_CapturesBody()
    {
        var spans = _visitor.Parse(@"answer: \boxed{x}");
        spans.OfType<MdMathInline>().Single().Source.ShouldBe("x");
    }

    [Fact]
    public void BoxedMath_BalancesNestedBraces()
    {
        // \boxed{\frac{1}{2}} has nested {} that must NOT terminate the
        // outer box. The BoxedBody recursion handles this naturally —
        // the LR grammar makes balanced braces a one-line production
        // (no hand-rolled scanner needed).
        var spans = _visitor.Parse(@"\boxed{\frac{1}{2}mv^2}");
        spans.OfType<MdMathInline>().Single().Source.ShouldBe(@"\frac{1}{2}mv^2");
    }

    [Fact]
    public void BoxedMath_RoundTripsThroughSubParser()
    {
        // Visit(MathBoxedSpan) wraps the body back in \boxed{...} so the
        // existing LaTeX sub-parser's \boxed handler (which frames the
        // body in [..] for the Unicode renderer) takes over.
        var spans = _visitor.Parse(@"\boxed{E = mc^2}");
        var math = spans.OfType<MdMathInline>().Single();
        math.Unicode.ShouldContain("E");
        math.Unicode.ShouldContain("mc²");
    }

    // ── Inline code ──────────────────────────────────────────────────

    [Fact]
    public void Backticks_ProduceCodeInline()
    {
        var spans = _visitor.Parse("use the `git status` command");
        spans.Count.ShouldBe(3);
        spans[0].ShouldBeOfType<MdLiteral>().Text.ShouldBe("use the ");
        spans[1].ShouldBeOfType<MdCodeInline>().Content.ShouldBe("git status");
        spans[2].ShouldBeOfType<MdLiteral>().Text.ShouldBe(" command");
    }

    [Fact]
    public void Backticks_BodyIsOpaque_NoMathSubstitution()
    {
        // Math markers inside a code span must NOT be parsed as math —
        // the lexer state isolates the body and the visitor emits it
        // verbatim. This is one of the regressions the LALR.CC path
        // fixes vs the current regex preprocessing.
        var spans = _visitor.Parse("`\\boxed{x}` is the syntax");
        spans[0].ShouldBeOfType<MdCodeInline>().Content.ShouldBe("\\boxed{x}");
        // No MdMathInline produced for the code body.
        spans.OfType<MdMathInline>().ShouldBeEmpty();
    }

    [Fact]
    public void Backticks_EmptyBody_ParseFailsCleanly()
    {
        // `` (two adjacent backticks) is degenerate — the grammar requires
        // a non-empty codebody. Fail-closed matches the rest of the
        // error-recovery posture; CommonMark's escape-for-empty-code
        // (`<code></code>`) isn't supported here.
        _visitor.Parse("`` foo").ShouldBeEmpty();
    }

    // ── Failure modes ────────────────────────────────────────────────

    [Fact]
    public void EmptyInput_ProducesEmptyList()
    {
        _visitor.Parse(string.Empty).ShouldBeEmpty();
    }

    [Fact]
    public void UnterminatedMath_ParseFailsCleanly()
    {
        // Spike scope: malformed input returns empty rather than throwing.
        // Phase B-extension may tighten error recovery (preserve the
        // literal up to the unmatched delimiter); for now "fail closed"
        // is enough.
        _visitor.Parse("text $unterminated").ShouldBeEmpty();
    }
}
