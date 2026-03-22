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
    // ResolveSixelSupport
    // -----------------------------------------------------------------------

    [Fact]
    public void ResolveSixelSupport_NoColor_ReturnsFalse()
    {
        VirtualTerminal.ResolveSixelSupport(noColor: true, hasSixelCapability: true)
            .ShouldBeFalse();
    }

    [Fact]
    public void ResolveSixelSupport_WithCapability_ReturnsTrue()
    {
        VirtualTerminal.ResolveSixelSupport(noColor: false, hasSixelCapability: true)
            .ShouldBeTrue();
    }

    [Fact]
    public void ResolveSixelSupport_WithoutCapability_ReturnsFalse()
    {
        VirtualTerminal.ResolveSixelSupport(noColor: false, hasSixelCapability: false)
            .ShouldBeFalse();
    }
}
