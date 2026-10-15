using System.Security.Cryptography;
using System.Text;
using Shouldly;
using Xunit;

namespace Console.Lib.Tests;

/// <summary>
/// Pins display math's raster output byte for byte: <see cref="MarkdownRenderer.RenderLines(string, int, ColorMode, MarkdownTheme?, BoxRenderMode?, string?, MarkdownImageOptions?, Func{string, string}?)"/>
/// over a fixed corpus, in every raster encoding, at a wide, a middling and a narrow width. The golden
/// was captured from main before the width-fit loop and the Sixel encoder were reworked for speed, so
/// a rework that changes a single byte of what reaches the terminal fails here.
/// <para>
/// The golden holds a SHA-256 of each case's output rather than the output itself. The corpus renders
/// to over a megabyte of escape sequences, which nobody could review as a diff, and a hash of the
/// exact bytes is as strict a comparison as the bytes. When the corpus and the golden disagree,
/// <see cref="Golden_IsTheCorpusRenderedToday"/> writes what it rendered next to the test assembly, so
/// the two files can be compared line by line.
/// </para>
/// </summary>
public sealed class MathRenderParityTests
{
    private static readonly string MathFont = Path.Combine(AppContext.BaseDirectory, "Fonts", "STIX2Math.otf");
    private static readonly string GoldenPath = Path.Combine(AppContext.BaseDirectory, "Golden", "MathRenderParity.golden.txt");

    /// <summary>
    /// Display formulas, each rendered as a fenced block (delimiters on their own lines): a one-line
    /// <c>$$x$$</c> parses as inline math inside a paragraph, which never rasters.
    /// <para>
    /// The wide ones are there for the width-fit loop. At width 30 <c>quadratic</c>,
    /// <c>definite-integral</c> and <c>fine-structure</c> shrink to fit as Sixel; at 60 the half-block
    /// rasters of <c>gauss-sum</c>, <c>gaussian-integral</c>, <c>limit</c> and <c>vectors</c> take three
    /// passes, because the first shrink leaves them still too wide; <c>fourteen-terms</c> shrinks as Sixel at 60 and as
    /// sextant at 120, and is too wide for any raster at 30. <c>hypotenuse</c> as half-block at 30 lands
    /// exactly on the two-thirds floor. The last formula cannot be lexed by the math grammar at all, so
    /// it pins the Unicode fallback.
    /// </para>
    /// </summary>
    internal static readonly (string Name, string Source)[] Corpus =
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

    internal static readonly BoxRenderMode[] Modes = [BoxRenderMode.Sixel, BoxRenderMode.Sextant, BoxRenderMode.HalfBlock];
    internal static readonly int[] Widths = [120, 60, 30];

    public static TheoryData<string, BoxRenderMode, int> Cases()
    {
        var data = new TheoryData<string, BoxRenderMode, int>();
        foreach (var (name, _) in Corpus)
        foreach (var mode in Modes)
        foreach (var width in Widths)
            data.Add(name, mode, width);
        return data;
    }

    internal static string Markdown(string source) => $"$$\n{source}\n$$";

    internal static List<string> Render(string source, BoxRenderMode mode, int width)
        => MarkdownRenderer.RenderLines(Markdown(source), width, ColorMode.TrueColor,
            mathMode: mode, mathFontPath: MathFont);

    [Theory]
    [MemberData(nameof(Cases))]
    public void RenderLines_IsByteIdenticalToTheGolden(string name, BoxRenderMode mode, int width)
    {
        File.Exists(MathFont).ShouldBeTrue("the test project copies mdcat's STIX Two Math next to the tests");
        var source = Corpus.Single(c => c.Name == name).Source;

        var golden = ReadGolden();
        golden.TryGetValue(Key(name, mode, width), out var expected)
            .ShouldBeTrue($"the golden has no entry for {Key(name, mode, width)}");

        Entry(name, mode, width, Render(source, mode, width)).ShouldBe(expected);
    }

    [Fact]
    public void Golden_IsTheCorpusRenderedToday()
    {
        var actual = new StringBuilder();
        actual.Append("# name mode width lines chars sha256\n");
        foreach (var (name, source) in Corpus)
        foreach (var mode in Modes)
        foreach (var width in Widths)
            actual.Append(Entry(name, mode, width, Render(source, mode, width))).Append('\n');

        var expected = string.Join('\n', File.ReadAllLines(GoldenPath).Where(l => !l.StartsWith("# ", StringComparison.Ordinal)));
        var rendered = string.Join('\n', actual.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(l => !l.StartsWith("# ", StringComparison.Ordinal)));
        if (rendered != expected)
        {
            var actualPath = Path.Combine(AppContext.BaseDirectory, "Golden", "MathRenderParity.actual.txt");
            File.WriteAllText(actualPath, actual.ToString());
            rendered.ShouldBe(expected, $"the corpus no longer renders as the golden says; today's rendering is in {actualPath}");
        }
    }

    private static string Key(string name, BoxRenderMode mode, int width) => $"{name} {mode} {width}";

    /// <summary>One golden line: the case, then the output's line count, its length in chars and the SHA-256 of its UTF-8 bytes.</summary>
    private static string Entry(string name, BoxRenderMode mode, int width, List<string> lines)
    {
        var text = string.Join('\n', lines);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        return $"{Key(name, mode, width)} {lines.Count} {text.Length} {hash}";
    }

    private static Dictionary<string, string> ReadGolden()
    {
        var golden = new Dictionary<string, string>();
        foreach (var line in File.ReadAllLines(GoldenPath))
        {
            if (line.Length == 0 || line.StartsWith("# ", StringComparison.Ordinal)) continue;
            var parts = line.Split(' ');
            golden[$"{parts[0]} {parts[1]} {parts[2]}"] = line;
        }
        return golden;
    }
}
