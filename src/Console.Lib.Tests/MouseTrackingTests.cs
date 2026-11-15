using System.Text;
using Shouldly;
using Xunit;

namespace Console.Lib.Tests;

/// <summary>Escape-stream tests for mouse tracking and focus reporting: bytes in through an in-memory stream,
/// mode escapes out through a StringWriter. No console.</summary>
public class MouseTrackingTests
{
    private static (VirtualTerminal Terminal, StringWriter Out) Make(string input, MouseTracking tracking = MouseTracking.AnyMotion)
    {
        var output = new StringWriter();
        var terminal = new VirtualTerminal(output, 80, 24, ColorMode.TrueColor, new MemoryStream(Encoding.UTF8.GetBytes(input)))
        {
            MouseTracking = tracking,
        };
        return (terminal, output);
    }

    [Fact]
    public async Task FocusReportingIsEnabledAfterAnyMotionAndDisabledOnDispose()
    {
        var (terminal, output) = Make("");
        terminal.EnterAlternateScreen();
        var entered = output.ToString();
        entered.ShouldContain("\e[?1003h");
        entered.ShouldContain("\e[?1004h");
        entered.IndexOf("\e[?1004h", StringComparison.Ordinal).ShouldBeGreaterThan(entered.IndexOf("\e[?1003h", StringComparison.Ordinal));

        await terminal.DisposeAsync();
        output.ToString().ShouldContain("\e[?1004l");
    }

    [Fact]
    public async Task TheOptOutWritesNeitherAnyMotionNorFocusReporting()
    {
        var (terminal, output) = Make("", MouseTracking.ButtonMotion);
        terminal.EnterAlternateScreen();
        await terminal.DisposeAsync();

        output.ToString().ShouldNotContain("?1003");
        output.ToString().ShouldNotContain("?1004");
    }

    [Fact]
    public void AFocusOutReportIsPointerLeftAndFocusInIsSwallowed()
    {
        var (terminal, _) = Make("\e[O\e[I");
        terminal.EnterAlternateScreen();

        var left = terminal.TryReadInput();
        left.PointerLeft.ShouldBeTrue();
        left.Mouse.ShouldBeNull();
        left.Key.ShouldBe(ConsoleKey.None);

        var focusIn = terminal.TryReadInput();
        focusIn.PointerLeft.ShouldBeFalse();
        focusIn.Mouse.ShouldBeNull();
        focusIn.Key.ShouldBe(ConsoleKey.None);
    }

    [Fact]
    public void FocusOutEndsAMotionRunAndIsNotFoldedIntoIt()
    {
        var (terminal, _) = Make("\e[<35;5;5M\e[<35;6;5M\e[O");
        terminal.EnterAlternateScreen();

        var motion = terminal.TryReadInput();
        motion.Mouse.ShouldNotBeNull().X.ShouldBe(5);
        terminal.TryReadInput().PointerLeft.ShouldBeTrue();
    }
}
