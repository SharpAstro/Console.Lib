using Console.Lib;
using Shouldly;
using Xunit;

namespace Console.Lib.Tests;

public sealed class VirtualTerminalTests
{
    // -----------------------------------------------------------------------
    // ResolveColorMode
    // -----------------------------------------------------------------------

    [Fact]
    public void ResolveColorMode_OutputRedirected_ReturnsNone()
    {
        VirtualTerminal.ResolveColorMode(isOutputRedirected: true, noColor: false, hasColorCapability: true)
            .ShouldBe(ColorMode.None);
    }

    [Fact]
    public void ResolveColorMode_NoColor_ReturnsNone()
    {
        VirtualTerminal.ResolveColorMode(isOutputRedirected: false, noColor: true, hasColorCapability: true)
            .ShouldBe(ColorMode.None);
    }

    [Fact]
    public void ResolveColorMode_BothRedirectedAndNoColor_ReturnsNone()
    {
        VirtualTerminal.ResolveColorMode(isOutputRedirected: true, noColor: true, hasColorCapability: true)
            .ShouldBe(ColorMode.None);
    }

    [Fact]
    public void ResolveColorMode_WithColorCapability_ReturnsTrueColor()
    {
        VirtualTerminal.ResolveColorMode(isOutputRedirected: false, noColor: false, hasColorCapability: true)
            .ShouldBe(ColorMode.TrueColor);
    }

    [Fact]
    public void ResolveColorMode_WithoutColorCapability_ReturnsSgr16()
    {
        VirtualTerminal.ResolveColorMode(isOutputRedirected: false, noColor: false, hasColorCapability: false)
            .ShouldBe(ColorMode.Sgr16);
    }

    // -----------------------------------------------------------------------
    // ResolveImageDisplayCapability
    // -----------------------------------------------------------------------

    [Fact]
    public void ResolveImageDisplayCapability_NoColor_ReturnsNoColor()
    {
        VirtualTerminal.ResolveImageDisplayCapability(noColor: true, hasColorCapability: true, hasSixelCapability: true)
            .ShouldBe(ImageDisplayCapability.NoColor);
    }

    [Fact]
    public void ResolveImageDisplayCapability_NoColorCapability_ReturnsNoColor()
    {
        VirtualTerminal.ResolveImageDisplayCapability(noColor: false, hasColorCapability: false, hasSixelCapability: true)
            .ShouldBe(ImageDisplayCapability.NoColor);
    }

    [Fact]
    public void ResolveImageDisplayCapability_ColorButNoSixel_ReturnsAsciiBlock()
    {
        VirtualTerminal.ResolveImageDisplayCapability(noColor: false, hasColorCapability: true, hasSixelCapability: false)
            .ShouldBe(ImageDisplayCapability.AsciiBlock);
    }

    [Fact]
    public void ResolveImageDisplayCapability_WithSixel_ReturnsSixel()
    {
        VirtualTerminal.ResolveImageDisplayCapability(noColor: false, hasColorCapability: true, hasSixelCapability: true)
            .ShouldBe(ImageDisplayCapability.Sixel);
    }
}
