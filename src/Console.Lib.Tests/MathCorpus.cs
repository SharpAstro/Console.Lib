namespace Console.Lib.Tests;

/// <summary>
/// The display-math corpus <see cref="MathRenderParityTests"/> pins and <c>tools/MathPerf</c> times, kept
/// in one file the harness links so the two cannot drift apart. Nothing here depends on xunit.
/// </summary>
internal static class MathCorpus
{
    /// <summary>
    /// Display formulas, each rendered as a fenced block (delimiters on their own lines): a one-line
    /// <c>$$x$$</c> parses as inline math inside a paragraph, which never rasters.
    /// <para>
    /// The wide ones are there for the width-fit loop. At width 30 <c>quadratic</c>,
    /// <c>definite-integral</c> and <c>fine-structure</c> shrink to fit as Sixel; at 60 the half-block
    /// rasters of <c>gauss-sum</c>, <c>gaussian-integral</c>, <c>limit</c> and <c>vectors</c> take three
    /// passes, because the first shrink leaves them still too wide; <c>fourteen-terms</c> shrinks as Sixel
    /// at 60 and as sextant at 120, and is too wide for any raster at 30. <c>hypotenuse</c> as half-block
    /// at 30 lands exactly on the two-thirds floor. The last formula cannot be lexed by the math grammar
    /// at all, so it pins the Unicode fallback.
    /// </para>
    /// </summary>
    internal static readonly (string Name, string Source)[] Formulas =
    [
        ("quadratic", @"x = \frac{-b \pm \sqrt{b^2 - 4ac}}{2a}"),
        ("hypotenuse", @"\sqrt{x^2 + y^2}"),
        ("fraction-sum", @"\frac{a}{b} + \frac{c}{d}"),
        ("gauss-sum", @"\sum_{i=1}^{n} i = \frac{n(n+1)}{2}"),
        ("gaussian-integral", @"\int_0^\infty e^{-x^2} dx = \frac{\sqrt{\pi}}{2}"),
        ("left-right", @"\left( \frac{a}{b} \right)^2"),
        ("euler", @"e^{i\pi} + 1 = 0"),
        ("mass-energy", @"E = mc^2"),
        ("pythagoras", @"a^2 + b^2 = c^2"),
        ("continued-fraction", @"\frac{1}{1 + \frac{1}{1 + \frac{1}{x}}}"),
        ("definite-integral", @"\int_a^b f(x) dx = F(b) - F(a)"),
        ("fine-structure", @"\alpha = \frac{e^2}{4\pi\varepsilon_0 \hbar c}"),
        ("fourteen-terms", @"a + b + c + d + e + f + g + h + i + j + k + l + m + n"),
        ("limit", @"\lim_{x \to 0} \frac{\sin x}{x} = 1"),
        ("binomial", @"\binom{n}{k}"),
        ("vectors", @"\vec{F} = m \vec{a}"),
        ("factorial-unlexable", @"\prod_{k=1}^{n} k = n!"),
    ];

    /// <summary>The formulas wide enough that some width in <see cref="Widths"/> sends them round the width-fit loop.</summary>
    internal static readonly string[] Wide = ["quadratic", "definite-integral", "fine-structure", "fourteen-terms"];

    internal static readonly BoxRenderMode[] Modes = [BoxRenderMode.Sixel, BoxRenderMode.Sextant, BoxRenderMode.HalfBlock];

    internal static readonly int[] Widths = [120, 60, 30];

    /// <summary>The formula as a fenced display block.</summary>
    internal static string Markdown(string source) => $"$$\n{source}\n$$";
}
