using System.Security.Cryptography;
using System.Text;
using DIR.Lib.Markdown;
using DIR.Lib.MathLayout;
using LALR.CC.LexicalGrammar;
using Shouldly;
using Xunit;

namespace Console.Lib.Tests;

/// <summary>
/// Pins display math's raster output byte for byte: <see cref="MarkdownRenderer.RenderLines(string, int, ColorMode, MarkdownTheme?, BoxRenderMode?, string?, MarkdownImageOptions?, Func{string, string}?)"/>
/// over <see cref="MathCorpus"/>, in every raster encoding, at a wide, a middling and a narrow width. The golden
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

    public static TheoryData<string, BoxRenderMode, int> Cases()
    {
        var data = new TheoryData<string, BoxRenderMode, int>();
        foreach (var (name, _) in MathCorpus.Formulas)
        foreach (var mode in MathCorpus.Modes)
        foreach (var width in MathCorpus.Widths)
            data.Add(name, mode, width);
        return data;
    }

    internal static List<string> Render(string source, BoxRenderMode mode, int width)
        => MarkdownRenderer.RenderLines(MathCorpus.Markdown(source), width, ColorMode.TrueColor,
            mathMode: mode, mathFontPath: MathFont);

    [Theory]
    [MemberData(nameof(Cases))]
    public void RenderLines_IsByteIdenticalToTheGolden(string name, BoxRenderMode mode, int width)
    {
        File.Exists(MathFont).ShouldBeTrue("the test project copies mdcat's STIX Two Math next to the tests");
        var source = MathCorpus.Formulas.Single(c => c.Name == name).Source;

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
        foreach (var (name, source) in MathCorpus.Formulas)
        foreach (var mode in MathCorpus.Modes)
        foreach (var width in MathCorpus.Widths)
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

    public static TheoryData<string, BoxRenderMode, int, string> FitTargetCases()
    {
        var data = new TheoryData<string, BoxRenderMode, int, string>();
        foreach (var (name, mode, width, target) in MathCorpus.FitTargets)
            data.Add(name, mode, width, target.ToString());
        return data;
    }

    /// <summary>
    /// The corpus still sends the width-fit loop where it is meant to: three passes, rastering just above
    /// the two-thirds floor, and falling back just past it (<see cref="MathCorpus.FitTargets"/>). A case a
    /// DIR.Lib release moves off its target fails here by name, rather than the corpus quietly no longer
    /// covering the loop's edges.
    /// </summary>
    [Theory]
    [MemberData(nameof(FitTargetCases))]
    public void Corpus_KeepsCoveringItsWidthFitTarget(string name, BoxRenderMode mode, int width, string target)
    {
        var source = MathCorpus.Formulas.Single(c => c.Name == name).Source;

        MarkdownRenderer.t_lastMathFit = default;
        Render(source, mode, width);
        var fit = MarkdownRenderer.t_lastMathFit;

        var covered = Enum.Parse<MathCorpus.FitTarget>(target) switch
        {
            MathCorpus.FitTarget.ThreePasses => fit.Rastered && fit.Passes >= 3,
            MathCorpus.FitTarget.OnTheFloor => fit.Rastered && fit.Size - fit.Floor < 0.5f,
            MathCorpus.FitTarget.PastTheFloor => !fit.Rastered && fit.Passes >= 2,
            _ => false,
        };
        covered.ShouldBeTrue($"{name} as {mode} at width {width} no longer covers {target}: {fit}");
    }

    /// <summary>
    /// The width-fit loop sizes each candidate from the box and rasterizes only the size it settles on:
    /// once when the formula rasters, at whatever size, and not at all when even the two-thirds floor is
    /// too wide or the grammar rejects it. It used to rasterize every size it tried.
    /// </summary>
    [Theory]
    [MemberData(nameof(Cases))]
    public void WidthFit_RasterizesOnlyTheSizeItSettlesOn(string name, BoxRenderMode mode, int width)
    {
        var source = MathCorpus.Formulas.Single(c => c.Name == name).Source;

        var before = MarkdownRenderer.t_mathRasterizations;
        var lines = Render(source, mode, width);
        var rasterizations = MarkdownRenderer.t_mathRasterizations - before;

        rasterizations.ShouldBe(IsRaster(lines) ? 1 : 0);
    }

    /// <summary>
    /// <see cref="MarkdownRenderer.MathRasterSize"/> restates how DIR.Lib sizes a raster, and the width-fit
    /// loop trusts it instead of rasterizing to find out. This holds it to the size
    /// <see cref="BoxRasterizer.RenderToRgba"/> really gives, for every formula the grammar takes, at
    /// thirteen sizes from the encoding's default down to its two-thirds floor, so a DIR.Lib that sizes
    /// rasters differently fails here instead of quietly mis-sizing fits.
    /// </summary>
    [Theory]
    [InlineData(32f)]
    [InlineData(12f)]
    [InlineData(10f)]
    public void MathRasterSize_IsTheSizeRenderToRgbaGives(float defaultSize)
    {
        var formulas = 0;
        foreach (var (name, source) in MathCorpus.Formulas)
        {
            Func<BoxStyle, Box>? builder;
            try
            {
                // TryRenderMathBox's preprocessing, as far as this corpus needs it (no \boxed, no \ce).
                var preprocessed = MarkdownMacros.ResolveBackslashEscapes(MarkdownMacros.NormalizeLatexAliases(source));
                builder = MarkdownRenderer.ParseMathBox(preprocessed, new BoxStyle(MathFont, defaultSize));
            }
            catch (LexerException)
            {
                continue;
            }
            if (builder is null) continue;
            formulas++;

            for (var step = 0; step <= 12; step++)
            {
                var size = defaultSize - defaultSize / 3 * step / 12;
                var style = new BoxStyle(MathFont, size);
                var box = builder(style);
                var image = BoxRasterizer.RenderToRgba(box, style);
                MarkdownRenderer.MathRasterSize(box, style).ShouldBe((image.Width, image.Height), $"{name} at {size} px");
            }
        }

        formulas.ShouldBe(MathCorpus.Formulas.Length - 1, "every formula but factorial-unlexable parses");
    }

    /// <summary>
    /// Whether display math came out as a raster rather than as Unicode: a Sixel DCS sequence, or the
    /// block and sextant characters the cell encoders draw with, none of which the Unicode path emits.
    /// </summary>
    private static bool IsRaster(List<string> lines) => lines.Any(line =>
        line.Contains("\eP", StringComparison.Ordinal)
        || line.EnumerateRunes().Any(r => r.Value is 0x2580 or 0x2584 or 0x2588 or 0x258C or 0x2590 or (>= 0x1FB00 and <= 0x1FB3B)));

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
