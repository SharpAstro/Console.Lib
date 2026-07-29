using System.IO;
using System.Text;
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
    // DEL is the Backspace KEY. 0x08 is not: it is Ctrl+H, asserted in the Ctrl+letter range below.
    [InlineData(0x7F, ConsoleKey.Backspace, (ConsoleModifiers)0)]
    // Ctrl+letter range (0x01..0x1A → A..Z + Ctrl)
    [InlineData(0x01, ConsoleKey.A, ConsoleModifiers.Control)]
    [InlineData(0x1A, ConsoleKey.Z, ConsoleModifiers.Control)]
    // 0x08 must not be special-cased out of that range: it was, as Backspace, which made Ctrl+H the only
    // letter an app could not bind. A Ctrl+H tab shortcut silently did nothing because of this one line.
    [InlineData((int)'\b', ConsoleKey.H, ConsoleModifiers.Control)]
    public void ByteToConsoleKey_MapsBytesToKeyAndModifiers(int b, ConsoleKey expectedKey, ConsoleModifiers expectedMods)
    {
        var (key, mods) = VirtualTerminal.ByteToConsoleKey(b);
        key.ShouldBe(expectedKey);
        mods.ShouldBe(expectedMods);
    }

    /// <summary>VT cell coordinates are 1-based; the buffer's are 0-based, and the top-left is the case that
    /// silently proves it (a 0 row or column is invalid VT and terminals disagree on what to do with it).</summary>
    [Theory]
    [InlineData(0, 0, "\e[1;1H")]
    [InlineData(5, 2, "\e[3;6H")]
    [InlineData(201, 62, "\e[63;202H")]
    public void MoveEscape_IsOneBased(int column, int row, string expected)
        => VirtualTerminal.MoveEscape(column, row).ShouldBe(expected);

    /// <summary>
    /// Reverse video has to be turned OFF as explicitly as it is turned on. Apply emits colours and no
    /// attribute reset, so a sink that only ever emitted ReverseOn left the terminal inverted for every run
    /// after it — one reversed cell (a text cursor) turned every following header into a solid bar and the
    /// selection into dark text on white.
    /// </summary>
    [Theory]
    [InlineData(true, true, VtStyle.ReverseOn)]
    [InlineData(false, true, VtStyle.ReverseOff)]
    [InlineData(true, false, "")]
    [InlineData(false, false, "")]
    public void PenEscape_StatesReverseInBothDirections(bool reverse, bool mustState, string expectedSuffix)
    {
        var style = new VtStyle(SgrColor.White, SgrColor.Black);
        var escape = VirtualTerminal.PenEscape(style, ColorMode.Sgr16, reverse, mustState);

        escape.ShouldStartWith(style.Apply(ColorMode.Sgr16));
        escape[style.Apply(ColorMode.Sgr16).Length..].ShouldBe(expectedSuffix);
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

    // -----------------------------------------------------------------------
    // TryReadRuneFrom — UTF-8 byte stream → Rune
    //
    // The byte-level input path (PipeBytesLexer-style: ParseSgrInput's non-ESC
    // branch) needs to buffer continuation bytes for non-ASCII codepoints so
    // 'é' (0xC3 0xA9) and friends round-trip into ConsoleInputEvent.KeyChar.
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData("a")]      // 1-byte ASCII
    [InlineData("z")]
    [InlineData("0")]
    [InlineData("é")]      // 2-byte UTF-8 (U+00E9)
    [InlineData("ñ")]
    [InlineData("中")]      // 3-byte UTF-8 (U+4E2D)
    [InlineData("漢")]
    [InlineData("🙂")]      // 4-byte UTF-8 (U+1F642, surrogate pair in UTF-16)
    [InlineData("🚀")]
    public void TryReadRuneFrom_DecodesUtf8Codepoint(string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        using var ms = new MemoryStream(bytes);
        var first = ms.ReadByte();
        var rune = VirtualTerminal.TryReadRuneFrom(first, ms);
        rune.ShouldNotBeNull();
        rune!.Value.ToString().ShouldBe(s);
        ms.Position.ShouldBe(ms.Length); // all continuation bytes consumed
    }

    [Theory]
    [InlineData(0x00)] // NUL
    [InlineData(0x09)] // TAB — control byte, KeyChar should not carry it
    [InlineData(0x0D)] // CR
    [InlineData(0x1B)] // ESC
    [InlineData(0x7F)] // DEL
    public void TryReadRuneFrom_RejectsControlBytes(int b)
    {
        using var ms = new MemoryStream([(byte)b]);
        var first = ms.ReadByte();
        VirtualTerminal.TryReadRuneFrom(first, ms).ShouldBeNull();
    }

    [Fact]
    public void TryReadRuneFrom_RejectsBareContinuationByte()
    {
        using var ms = new MemoryStream([0xA9]); // continuation byte without lead
        var first = ms.ReadByte();
        VirtualTerminal.TryReadRuneFrom(first, ms).ShouldBeNull();
    }

    [Fact]
    public void TryReadRuneFrom_RejectsTruncatedSequence()
    {
        // 0xC3 expects 1 continuation byte but stream is empty after the lead
        using var ms = new MemoryStream([0xC3]);
        var first = ms.ReadByte();
        VirtualTerminal.TryReadRuneFrom(first, ms).ShouldBeNull();
    }

    [Fact]
    public void TryReadRuneFrom_RejectsInvalidContinuation()
    {
        // 0xC3 expects continuation, but 0x40 is not a continuation byte
        using var ms = new MemoryStream([0xC3, 0x40]);
        var first = ms.ReadByte();
        VirtualTerminal.TryReadRuneFrom(first, ms).ShouldBeNull();
    }

    [Fact]
    public void TryReadRuneFrom_LeavesUnreadBytesAlone_OnAscii()
    {
        // ASCII codepoints never consume more than the lead byte
        using var ms = new MemoryStream([0x42, 0x99, 0xFF]);
        var first = ms.ReadByte();
        var rune = VirtualTerminal.TryReadRuneFrom(first, ms);
        rune.ShouldNotBeNull();
        rune!.Value.Value.ShouldBe(0x42);
        ms.Position.ShouldBe(1);
    }
}
