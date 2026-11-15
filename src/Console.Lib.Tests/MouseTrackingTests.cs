using System.Text;
using DIR.Lib;
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

    private static string Motion(int x, int y) => $"\e[<35;{x};{y}M";

    [Fact]
    public async Task AnyMotionWritesTrackingModesOnEnterAndTheirOffSwitchesOnDispose()
    {
        var (terminal, output) = Make("");
        terminal.EnterAlternateScreen();
        var entered = output.ToString();
        entered.ShouldContain("\e[?1000h");
        entered.ShouldContain("\e[?1002h");
        entered.ShouldContain("\e[?1003h");
        entered.ShouldContain("\e[?1006h");
        var before = entered.Length;

        await terminal.DisposeAsync();
        var disposed = output.ToString()[before..];
        disposed.ShouldContain("\e[?1003l");
        disposed.ShouldContain("\e[?1002l");
        disposed.ShouldContain("\e[?1000l");
        disposed.ShouldContain("\e[?1006l");
    }

    [Fact]
    public async Task TheOptOutStillWritesButtonTrackingAndItsOffSwitches()
    {
        var (terminal, output) = Make("", MouseTracking.ButtonMotion);
        terminal.EnterAlternateScreen();
        var entered = output.ToString();
        entered.ShouldContain("\e[?1000h");
        entered.ShouldContain("\e[?1002h");
        entered.ShouldContain("\e[?1006h");
        var before = entered.Length;

        await terminal.DisposeAsync();
        var disposed = output.ToString()[before..];
        disposed.ShouldContain("\e[?1002l");
        disposed.ShouldContain("\e[?1000l");
        disposed.ShouldNotContain("\e[?1003l");
    }

    [Fact]
    public void FiftyMotionReportsInOneBatchFoldIntoOneMouseMoveAtTheLastCell()
    {
        var input = string.Concat(Enumerable.Range(1, 50).Select(i => Motion(i, i + 1)));
        var (terminal, _) = Make(input);
        terminal.EnterAlternateScreen();

        var evt = terminal.TryReadInput();
        var move = evt.ToInputEvent.ShouldBeOfType<InputEvent.MouseMove>();
        move.X.ShouldBe(49f);
        move.Y.ShouldBe(50f);
        move.Button.ShouldBe(MouseButton.None);

        terminal.HasInput().ShouldBeFalse();
    }

    [Fact]
    public void APressInTheMiddleOfAMotionRunGivesMotionPressMotion()
    {
        var (terminal, _) = Make(Motion(1, 1) + Motion(2, 2) + "\e[<0;3;3M" + Motion(4, 4) + Motion(5, 5));
        terminal.EnterAlternateScreen();

        var first = terminal.TryReadInput().Mouse.ShouldNotBeNull();
        first.IsMotion.ShouldBeTrue();
        first.X.ShouldBe(1);

        var press = terminal.TryReadInput().Mouse.ShouldNotBeNull();
        press.IsMotion.ShouldBeFalse();
        press.IsRelease.ShouldBeFalse();
        press.Button.ShouldBe(0);
        press.X.ShouldBe(2);

        var last = terminal.TryReadInput().Mouse.ShouldNotBeNull();
        last.IsMotion.ShouldBeTrue();
        last.X.ShouldBe(4);
        last.Y.ShouldBe(4);
        terminal.HasInput().ShouldBeFalse();
    }

    [Fact]
    public void ARunIsNotMergedAcrossAReleaseAWheelStepOrAKey()
    {
        var (terminal, _) = Make(
            Motion(1, 1) + "\e[<0;2;2m" + Motion(3, 3)
            + "\e[<64;4;4M" + Motion(5, 5)
            + "\e[<65;6;6M" + Motion(7, 7)
            + "a" + Motion(8, 8));
        terminal.EnterAlternateScreen();

        var m1 = terminal.TryReadInput().Mouse.ShouldNotBeNull();
        (m1.IsMotion, m1.X).ShouldBe((true, 0));

        var release = terminal.TryReadInput().Mouse.ShouldNotBeNull();
        (release.IsRelease, release.Button, release.X).ShouldBe((true, 0, 1));

        var m2 = terminal.TryReadInput().Mouse.ShouldNotBeNull();
        (m2.IsMotion, m2.X).ShouldBe((true, 2));

        var up = terminal.TryReadInput().Mouse.ShouldNotBeNull();
        (up.Button, up.X).ShouldBe((64, 3));

        var m3 = terminal.TryReadInput().Mouse.ShouldNotBeNull();
        (m3.IsMotion, m3.X).ShouldBe((true, 4));

        var down = terminal.TryReadInput().Mouse.ShouldNotBeNull();
        (down.Button, down.X).ShouldBe((65, 5));

        var m4 = terminal.TryReadInput().Mouse.ShouldNotBeNull();
        (m4.IsMotion, m4.X).ShouldBe((true, 6));

        var key = terminal.TryReadInput();
        key.Mouse.ShouldBeNull();
        key.Key.ShouldBe(ConsoleKey.A);

        var m5 = terminal.TryReadInput().Mouse.ShouldNotBeNull();
        (m5.IsMotion, m5.X).ShouldBe((true, 7));
        terminal.HasInput().ShouldBeFalse();
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
