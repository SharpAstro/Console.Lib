using System;
using System.IO;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace Console.Lib.Tests;

/// <summary>mdcat's way of writing Markdown to standard output, as a library any CLI can take.</summary>
public sealed class MarkdownOutputTests
{
    [Theory]
    [InlineData("auto", OutputFormat.Auto)]
    [InlineData("Rich", OutputFormat.Rich)]
    [InlineData("markdown", OutputFormat.Markdown)]
    [InlineData("md", OutputFormat.Markdown)]
    [InlineData("PLAIN", OutputFormat.Plain)]
    [InlineData("text", OutputFormat.Plain)]
    public void AFormatParsesInAnyCase(string value, OutputFormat expected)
    {
        MarkdownOutput.TryParseFormat(value, out var format).ShouldBeTrue();
        format.ShouldBe(expected);
    }

    [Theory]
    [InlineData("html")]
    [InlineData("")]
    [InlineData(null)]
    public void AnUnknownFormatIsRefused(string? value) => MarkdownOutput.TryParseFormat(value, out _).ShouldBeFalse();

    [Theory]
    [InlineData(OutputFormat.Rich)]
    [InlineData(OutputFormat.Markdown)]
    [InlineData(OutputFormat.Plain)]
    public async Task AFormatAskedForIsTakenWithoutAProbe(OutputFormat asked)
    {
        var (format, probe) = await MarkdownOutput.ResolveFormatAsync(asked);

        format.ShouldBe(asked);
        probe.ShouldBeNull();
    }

    /// <summary>The test host's standard output is redirected, as a pipe's is: auto is plain, and nobody probes.</summary>
    [Fact]
    public async Task AutoIsPlainWhenTheOutputIsRedirected()
    {
        Assert.SkipUnless(System.Console.IsOutputRedirected, "standard output is a terminal here");

        var (format, probe) = await MarkdownOutput.ResolveFormatAsync(OutputFormat.Auto);

        format.ShouldBe(OutputFormat.Plain);
        probe.ShouldBeNull();
    }

    [Fact]
    public async Task WithoutColourItRendersTextAndNoEscapes()
    {
        var output = new StringWriter();

        var rendered = await MarkdownOutput.RenderAsync("# Flights\n\n- **TIA1818** flying", output,
            new MarkdownOutputOptions(Width: 60, Color: ColorMode.None));

        rendered.ShouldBeTrue();
        output.ToString().ShouldContain("Flights");
        output.ToString().ShouldContain("TIA1818");
        output.ToString().ShouldNotContain("\e[");
    }

    [Fact]
    public void ALocalLinkBecomesAFileUriAndAnAbsoluteOneIsLeftAlone()
    {
        var baseDir = Path.GetTempPath();

        MarkdownOutput.ResolveLocalLink("docs/a.md", baseDir).ShouldBe(new Uri(Path.Combine(baseDir, "docs", "a.md")).AbsoluteUri);
        MarkdownOutput.ResolveLocalLink("https://github.com/SharpAstro/pilot/pull/226", baseDir).ShouldBe("https://github.com/SharpAstro/pilot/pull/226");
        MarkdownOutput.ResolveLocalLink("#top", baseDir).ShouldBe("#top");
    }

    [Fact]
    public void ARemoteImageIsNeverFetched()
    {
        MarkdownOutput.LoadLocalImage("https://example.com/a.png", Path.GetTempPath()).ShouldBeNull();
        MarkdownOutput.LoadLocalImage("data:image/png;base64,AAAA", Path.GetTempPath()).ShouldBeNull();
        MarkdownOutput.LoadLocalImage("no-such-file.png", Path.GetTempPath()).ShouldBeNull();
    }

    [Fact]
    public void NoImagesWithoutAProbeOrWithoutColour()
    {
        MarkdownOutput.BuildImageOptions(default, null, ".").ShouldBeNull();
        MarkdownOutput.BuildImageOptions(new TerminalProbe(true, false, ImageDisplayCapability.NoColor, 10, 20), null, ".").ShouldBeNull();
        MarkdownOutput.BuildImageOptions(new TerminalProbe(true, true, ImageDisplayCapability.Sixel, 0, 0), null, ".")
            .ShouldNotBeNull().Mode.ShouldBe(BoxRenderMode.Sixel);
    }
}
