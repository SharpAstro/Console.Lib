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

    // -----------------------------------------------------------------------
    // ByteToConsoleKey — input byte → (ConsoleKey, ConsoleModifiers)
    //
    // Round-trips through ConsoleInputMapping + InputKeyCharMapping to assert
    // that what comes out of the terminal byte stream is what the consumer sees.
    // -----------------------------------------------------------------------

    [Theory]
    // Lowercase letters: no modifier
    [InlineData((int)'a', ConsoleKey.A, (ConsoleModifiers)0)]
    [InlineData((int)'z', ConsoleKey.Z, (ConsoleModifiers)0)]
    // Uppercase letters: Shift
    [InlineData((int)'A', ConsoleKey.A, ConsoleModifiers.Shift)]
    [InlineData((int)'Z', ConsoleKey.Z, ConsoleModifiers.Shift)]
    // Digits: no modifier
    [InlineData((int)'0', ConsoleKey.D0, (ConsoleModifiers)0)]
    [InlineData((int)'9', ConsoleKey.D9, (ConsoleModifiers)0)]
    // Shifted digit row
    [InlineData((int)'!', ConsoleKey.D1, ConsoleModifiers.Shift)]
    [InlineData((int)'@', ConsoleKey.D2, ConsoleModifiers.Shift)]
    [InlineData((int)'#', ConsoleKey.D3, ConsoleModifiers.Shift)]
    [InlineData((int)'$', ConsoleKey.D4, ConsoleModifiers.Shift)]
    [InlineData((int)'%', ConsoleKey.D5, ConsoleModifiers.Shift)]
    [InlineData((int)'^', ConsoleKey.D6, ConsoleModifiers.Shift)]
    [InlineData((int)'&', ConsoleKey.D7, ConsoleModifiers.Shift)]
    [InlineData((int)'*', ConsoleKey.D8, ConsoleModifiers.Shift)]
    [InlineData((int)'(', ConsoleKey.D9, ConsoleModifiers.Shift)]
    [InlineData((int)')', ConsoleKey.D0, ConsoleModifiers.Shift)]
    // OEM symbols: unshifted vs shifted
    [InlineData((int)'-', ConsoleKey.OemMinus, (ConsoleModifiers)0)]
    [InlineData((int)'_', ConsoleKey.OemMinus, ConsoleModifiers.Shift)]
    [InlineData((int)'=', ConsoleKey.OemPlus, (ConsoleModifiers)0)]
    [InlineData((int)'+', ConsoleKey.OemPlus, ConsoleModifiers.Shift)]
    [InlineData((int)'.', ConsoleKey.OemPeriod, (ConsoleModifiers)0)]
    [InlineData((int)'>', ConsoleKey.OemPeriod, ConsoleModifiers.Shift)]
    [InlineData((int)',', ConsoleKey.OemComma, (ConsoleModifiers)0)]
    [InlineData((int)'<', ConsoleKey.OemComma, ConsoleModifiers.Shift)]
    [InlineData((int)'/', ConsoleKey.Oem2, (ConsoleModifiers)0)]
    [InlineData((int)'?', ConsoleKey.Oem2, ConsoleModifiers.Shift)]
    [InlineData((int)'\\', ConsoleKey.Oem5, (ConsoleModifiers)0)]
    [InlineData((int)'|', ConsoleKey.Oem5, ConsoleModifiers.Shift)]
    [InlineData((int)';', ConsoleKey.Oem1, (ConsoleModifiers)0)]
    [InlineData((int)':', ConsoleKey.Oem1, ConsoleModifiers.Shift)]
    [InlineData((int)'\'', ConsoleKey.Oem7, (ConsoleModifiers)0)]
    [InlineData((int)'"', ConsoleKey.Oem7, ConsoleModifiers.Shift)]
    [InlineData((int)'[', ConsoleKey.Oem4, (ConsoleModifiers)0)]
    [InlineData((int)'{', ConsoleKey.Oem4, ConsoleModifiers.Shift)]
    [InlineData((int)']', ConsoleKey.Oem6, (ConsoleModifiers)0)]
    [InlineData((int)'}', ConsoleKey.Oem6, ConsoleModifiers.Shift)]
    [InlineData((int)'`', ConsoleKey.Oem3, (ConsoleModifiers)0)]
    [InlineData((int)'~', ConsoleKey.Oem3, ConsoleModifiers.Shift)]
    // Whitespace + control
    [InlineData((int)' ', ConsoleKey.Spacebar, (ConsoleModifiers)0)]
    [InlineData((int)'\t', ConsoleKey.Tab, (ConsoleModifiers)0)]
    [InlineData((int)'\r', ConsoleKey.Enter, (ConsoleModifiers)0)]
    [InlineData((int)'\n', ConsoleKey.Enter, (ConsoleModifiers)0)]
    [InlineData((int)'\b', ConsoleKey.Backspace, (ConsoleModifiers)0)]
    [InlineData(0x7F, ConsoleKey.Backspace, (ConsoleModifiers)0)]
    // Ctrl+letter range (0x01..0x1A → A..Z + Ctrl)
    [InlineData(0x01, ConsoleKey.A, ConsoleModifiers.Control)]
    [InlineData(0x1A, ConsoleKey.Z, ConsoleModifiers.Control)]
    public void ByteToConsoleKey_MapsBytesToKeyAndModifiers(int b, ConsoleKey expectedKey, ConsoleModifiers expectedMods)
    {
        var (key, mods) = VirtualTerminal.ByteToConsoleKey(b);
        key.ShouldBe(expectedKey);
        mods.ShouldBe(expectedMods);
    }

    [Theory]
    // The full point of the byte-map fix: every printable ASCII byte that a
    // terminal might send should round-trip through ByteToConsoleKey →
    // ConsoleInputMapping → InputKeyCharMapping back to the original char.
    [InlineData((int)'a')] [InlineData((int)'z')]
    [InlineData((int)'A')] [InlineData((int)'Z')]
    [InlineData((int)'0')] [InlineData((int)'9')]
    [InlineData((int)'!')] [InlineData((int)'@')] [InlineData((int)'#')]
    [InlineData((int)'$')] [InlineData((int)'%')] [InlineData((int)'^')]
    [InlineData((int)'&')] [InlineData((int)'*')] [InlineData((int)'(')]
    [InlineData((int)')')]
    [InlineData((int)'-')] [InlineData((int)'_')]
    [InlineData((int)'=')] [InlineData((int)'+')]
    [InlineData((int)'.')] [InlineData((int)'>')]
    [InlineData((int)',')] [InlineData((int)'<')]
    [InlineData((int)'/')] [InlineData((int)'?')]
    [InlineData((int)'\\')] [InlineData((int)'|')]
    [InlineData((int)';')] [InlineData((int)':')]
    [InlineData((int)'\'')] [InlineData((int)'"')]
    [InlineData((int)'[')] [InlineData((int)'{')]
    [InlineData((int)']')] [InlineData((int)'}')]
    [InlineData((int)'`')] [InlineData((int)'~')]
    [InlineData((int)' ')]
    public void ByteToConsoleKey_RoundTripsToPrintableChar(int b)
    {
        var (key, mods) = VirtualTerminal.ByteToConsoleKey(b);
        var inputKey = key.ToInputKey;
        var inputMod = mods.ToInputModifier;
        var ch = inputKey.ToChar(inputMod);
        ch.ShouldBe((char)b);
    }
}
