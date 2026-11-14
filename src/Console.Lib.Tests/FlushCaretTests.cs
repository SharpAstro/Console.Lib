using System.IO;
using Console.Lib;
using Shouldly;
using Xunit;

namespace Console.Lib.Tests;

/// <summary>
/// Characterization of the bytes <see cref="VirtualTerminal.Flush"/> writes around a parked caret (#49).
/// Everything runs in memory over a <see cref="StringWriter"/>; no console is touched.
/// </summary>
public sealed class FlushCaretTests
{
    private const string Hide = "\e[?25l";
    private const string Show = "\e[?25h";

    private static (VirtualTerminal Terminal, StringWriter Output) Create()
    {
        var output = new StringWriter();
        var terminal = new VirtualTerminal(output, 40, 10, ColorMode.TrueColor);
        terminal.EnableCellBuffer();
        return (terminal, output);
    }

    private static string Take(StringWriter output)
    {
        var text = output.ToString();
        output.GetStringBuilder().Clear();
        return text;
    }

    [Fact]
    public void EmptyFlushWithShownCaretHidesAndReshowsIt()
    {
        var (terminal, output) = Create();
        terminal.SetCaret(5, 3, CaretStyle.BlinkingBar);
        terminal.Flush();
        Take(output);

        // CURRENT behaviour (the bug in #49): nothing painted, yet the caret is retracted and re-shown,
        // which restarts the terminal's blink.
        terminal.Flush();
        var bytes = Take(output);

        bytes.ShouldContain(Hide);
        bytes.ShouldContain(Show);
        bytes.IndexOf(Hide, StringComparison.Ordinal).ShouldBeLessThan(bytes.IndexOf(Show, StringComparison.Ordinal));
    }

    [Fact]
    public void ChangedCellFlushEmitsRunThenParksCaret()
    {
        var (terminal, output) = Create();
        terminal.SetCaret(5, 3, CaretStyle.BlinkingBar);
        terminal.Flush();
        Take(output);

        terminal.Write("X");
        terminal.Flush();
        var bytes = Take(output);

        var cup = VirtualTerminal.MoveEscape(5, 3);
        bytes.ShouldContain("X");
        bytes.ShouldContain(cup);
        bytes.IndexOf("X", StringComparison.Ordinal)
            .ShouldBeLessThan(bytes.LastIndexOf(cup, StringComparison.Ordinal));
        bytes.ShouldContain(Hide);
        bytes.ShouldEndWith(cup + Show);
    }

    [Fact]
    public void NoCaretAndEmptyDiffWritesNothing()
    {
        var (terminal, output) = Create();
        terminal.Flush();
        Take(output);

        terminal.Flush();

        Take(output).ShouldBeEmpty();
    }
}
