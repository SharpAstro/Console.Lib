using Console.Lib;
using Shouldly;
using Xunit;

namespace Console.Lib.Tests;

public sealed class ConsoleSizeTests
{
    // -----------------------------------------------------------------------
    // TryComputeSize — the srWindow arithmetic behind the CONOUT$ size probe.
    //
    // Two ways to get this wrong, both of which produce a plausible number that
    // is silently off: taking the raw difference (srWindow's Right/Bottom name
    // the last INCLUDED cell, so an 80-column window reads Left=0, Right=79),
    // and reaching for CONSOLE_SCREEN_BUFFER_INFO.dwSize instead, whose height
    // is the scrollback buffer — commonly 9001 rows against a 25-row window.
    // These cases pin the +1 and the units, so a rewrite cannot quietly swap in
    // either mistake.
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(0, 0, 79, 24, 80, 25)]   // the classic default console window
    [InlineData(0, 0, 0, 0, 1, 1)]       // one cell is a size, not an empty rect
    [InlineData(5, 3, 84, 32, 80, 30)]   // scrolled buffer: the window is not at the origin
    public void TryComputeSize_MeasuresInclusiveWindowRect(
        int left, int top, int right, int bottom, int expectedWidth, int expectedHeight)
    {
        ConsoleSize.TryComputeSize(left, top, right, bottom, out var width, out var height)
            .ShouldBeTrue();
        width.ShouldBe(expectedWidth);
        height.ShouldBe(expectedHeight);
    }

    /// <summary>
    /// A console that reports an inverted rect has no usable size; returning
    /// false hands the caller back to its own fallback rather than letting a
    /// zero or negative width reach a layout routine that will divide by it.
    /// </summary>
    [Theory]
    [InlineData(10, 10, 9, 20)]   // Right < Left
    [InlineData(10, 10, 20, 9)]   // Bottom < Top
    public void TryComputeSize_RejectsDegenerateRect(int left, int top, int right, int bottom)
    {
        ConsoleSize.TryComputeSize(left, top, right, bottom, out _, out _)
            .ShouldBeFalse();
    }

    /// <summary>
    /// The public entry point has to be safe to call anywhere — including the
    /// Linux CI runner, where stdout is redirected and there is no CONOUT$ to
    /// fall back to. It must answer rather than throw, and when it does claim a
    /// size that size must be usable.
    /// </summary>
    [Fact]
    public void TryGetWindowSize_WithoutAConsole_AnswersWithoutThrowing()
    {
        if (ConsoleSize.TryGetWindowSize(out var width, out var height))
        {
            width.ShouldBeGreaterThan(0);
            height.ShouldBeGreaterThan(0);
        }

        ConsoleSize.GetWidth(fallback: 100).ShouldBeGreaterThan(0);
    }
}
