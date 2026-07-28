using System;
using Console.Lib;
using DIR.Lib;
using Shouldly;
using Xunit;

namespace Console.Lib.Tests;

/// <summary>
/// Terminal input to <see cref="InputEvent"/>. Mostly about the mouse: a terminal reports presses,
/// releases and motion down the same channel, distinguished only by bits, and a consumer that acts on
/// <see cref="InputEvent.MouseDown"/> is entitled to assume it means "a button went down just now".
/// </summary>
public class ConsoleInputMappingTests
{
    private static InputEvent? Map(MouseEvent mouse) =>
        new ConsoleInputEvent(mouse, ConsoleKey.None, 0).ToInputEvent;

    [Fact]
    public void APress_IsAMouseDown()
        => Map(new MouseEvent(0, 40, 80, IsRelease: false))
            .ShouldBeOfType<InputEvent.MouseDown>()
            .ShouldSatisfyAllConditions(
                e => e.X.ShouldBe(40),
                e => e.Y.ShouldBe(80),
                e => e.Button.ShouldBe(MouseButton.Left));

    [Fact]
    public void ARelease_IsAMouseUp()
        => Map(new MouseEvent(0, 40, 80, IsRelease: true))
            .ShouldBeOfType<InputEvent.MouseUp>();

    /// <summary>
    /// The defect this file exists for. xterm mode 1002 reports pointer movement with the HELD BUTTON
    /// still in the button field and <c>IsRelease</c> false, so a mapping that asks only "is this a
    /// release?" classifies motion as a press. Two consequences, both observed in Chess.Console:
    /// a click whose pointer drifts a single pixel is delivered TWICE (the second one landing after the
    /// opponent has already replied, which silently selected the piece the user had just moved), and
    /// dragging with the button down is delivered as a click on every square crossed — i.e. it plays a
    /// move at whatever the pointer passes over.
    /// </summary>
    [Fact]
    public void Motion_IsAMouseMove_NotAPress()
    {
        var evt = Map(new MouseEvent(0, 40, 80, IsRelease: false) { IsMotion = true });

        evt.ShouldBeOfType<InputEvent.MouseMove>();
        evt.ShouldNotBeOfType<InputEvent.MouseDown>();
    }

    [Theory]
    [InlineData(0)]   // left held
    [InlineData(1)]   // middle held
    [InlineData(2)]   // right held
    [InlineData(3)]   // no button held (mode 1003 any-motion tracking)
    public void Motion_IsAMouseMove_WhicheverButtonIsHeld(int button)
        => Map(new MouseEvent(button, 40, 80, IsRelease: false) { IsMotion = true })
            .ShouldBeOfType<InputEvent.MouseMove>();

    [Theory]
    [InlineData(64, 1f)]
    [InlineData(65, -1f)]
    public void TheWheel_IsAScroll(int button, float delta)
        => Map(new MouseEvent(button, 40, 80, IsRelease: false))
            .ShouldBeOfType<InputEvent.Scroll>()
            .Delta.ShouldBe(delta);

    /// <summary>A wheel report carrying the motion bit is still a wheel — the scroll check comes first,
    /// and a scroll has a position of its own, so it must not be demoted to a bare move.</summary>
    [Fact]
    public void TheWheel_IsAScroll_EvenWithTheMotionBitSet()
        => Map(new MouseEvent(64, 40, 80, IsRelease: false) { IsMotion = true })
            .ShouldBeOfType<InputEvent.Scroll>();

    [Fact]
    public void AKeyPress_WithNoMouseData_IsAKeyDown()
        => new ConsoleInputEvent(null, ConsoleKey.A, 0).ToInputEvent
            .ShouldBeOfType<InputEvent.KeyDown>()
            .Key.ShouldBe(InputKey.A);
}
