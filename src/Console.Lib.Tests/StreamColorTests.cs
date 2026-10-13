using Console.Lib;
using Shouldly;
using Xunit;

namespace Console.Lib.Tests;

public sealed class StreamColorTests
{
    [Fact]
    public void Paint_Sgr16_WrapsTheTextInTheColourAndAReset()
    {
        StreamColor.Paint("boom", SgrColor.Red, ColorMode.Sgr16).ShouldBe("\e[31mboom\e[0m");
        StreamColor.Paint("boom", SgrColor.BrightRed, ColorMode.Sgr16).ShouldBe("\e[91mboom\e[0m");
    }

    [Fact]
    public void Paint_None_IsTheTextAlone()
    {
        StreamColor.Paint("boom", SgrColor.Red, ColorMode.None).ShouldBe("boom");
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("1", true)]
    [InlineData("0", true)]
    [InlineData("false", true)]
    public void NoColor_IsAnyValueButTheEmptyString(string? value, bool expected)
    {
        // https://no-color.org: present and not empty, whatever the value, so even "0" asks for no colour.
        StreamColor.IsNoColor(value).ShouldBe(expected);
    }

    [Fact]
    public void Resolve_ATerminalThatTakesEscapes_IsSgr16()
    {
        StreamColor.Resolve(isRedirected: false, noColor: false, takesEscapes: () => true).ShouldBe(ColorMode.Sgr16);
    }

    [Fact]
    public void Resolve_AConsoleThatRefusesEscapes_IsNone()
    {
        StreamColor.Resolve(isRedirected: false, noColor: false, takesEscapes: () => false).ShouldBe(ColorMode.None);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Resolve_ARedirectedStreamOrNoColor_IsNone_AndNeverTouchesTheConsole(bool isRedirected, bool noColor)
    {
        var asked = false;

        StreamColor.Resolve(isRedirected, noColor, () => asked = true).ShouldBe(ColorMode.None);

        asked.ShouldBeFalse("the console's mode is changed only for a stream that will take colour");
    }
}
