using System.IO;
using Console.Lib;
using Shouldly;
using Xunit;

namespace Console.Lib.Tests;

/// <summary>
/// The bytes <see cref="VirtualTerminal.Flush"/> writes around a parked caret (#49): a quiet Flush writes
/// nothing, a painting Flush is bracketed by synchronized output where supported. Everything runs in memory
/// over a <see cref="StringWriter"/>; no console is touched.
/// </summary>
public sealed class FlushCaretTests
{
    private const string Hide = "\e[?25l";
    private const string Show = "\e[?25h";
    private const string SyncOn = "\e[?2026h";
    private const string SyncOff = "\e[?2026l";

    private static (VirtualTerminal Terminal, StringWriter Output) Create(bool sync = false,
        ColorMode mode = ColorMode.TrueColor)
    {
        var output = new StringWriter();
        var terminal = new VirtualTerminal(output, 40, 10, mode, sync);
        terminal.EnableCellBuffer();
        return (terminal, output);
    }

    private static string Take(StringWriter output)
    {
        var text = output.ToString();
        output.GetStringBuilder().Clear();
        return text;
    }

    private static (VirtualTerminal Terminal, StringWriter Output) Parked(bool sync)
    {
        var (terminal, output) = Create(sync);
        terminal.SetCaret(5, 3, CaretStyle.BlinkingBar);
        terminal.Flush();
        Take(output);
        return (terminal, output);
    }

    [Fact]
    public void EmptyFlushWithSameCaretWritesNothing()
    {
        var (terminal, output) = Parked(sync: false);

        terminal.Flush();

        Take(output).ShouldBeEmpty();
    }

    [Fact]
    public void EmptyFlushWithSameCaretWritesNothingUnderSync()
    {
        var (terminal, output) = Parked(sync: true);

        terminal.Flush();

        Take(output).ShouldBeEmpty();
    }

    [Fact]
    public void ChangedCellsWithSyncAreBracketedAndNeverToggleVisibility()
    {
        var (terminal, output) = Parked(sync: true);

        terminal.Write("X");
        terminal.Flush();
        var bytes = Take(output);

        var cup = VirtualTerminal.MoveEscape(5, 3);
        bytes.ShouldNotContain(Hide);
        bytes.ShouldNotContain(Show);
        bytes.ShouldStartWith(SyncOn);
        bytes.ShouldEndWith(cup + SyncOff);
        bytes.ShouldContain("X");
    }

    [Fact]
    public void MovedCaretWithNoChangedCellsWritesExactlyOneCup()
    {
        var (terminal, output) = Parked(sync: true);

        terminal.SetCaret(7, 2, CaretStyle.BlinkingBar);
        terminal.Flush();

        Take(output).ShouldBe(VirtualTerminal.MoveEscape(7, 2));
    }

    [Fact]
    public void ChangedCellsWithoutSyncStillHideAndReshow()
    {
        var (terminal, output) = Parked(sync: false);

        terminal.Write("X");
        terminal.Flush();
        var bytes = Take(output);

        var cup = VirtualTerminal.MoveEscape(5, 3);
        bytes.ShouldNotContain(SyncOn);
        bytes.ShouldStartWith(Hide);
        bytes.ShouldEndWith(cup + Show);
        bytes.IndexOf("X", StringComparison.Ordinal).ShouldBeLessThan(bytes.LastIndexOf(cup, StringComparison.Ordinal));
    }

    [Fact]
    public void ChangedShapeReemitsOnlyTheShape()
    {
        var (terminal, output) = Parked(sync: true);

        terminal.SetCaret(5, 3, CaretStyle.SteadyBlock);
        terminal.Flush();

        Take(output).ShouldBe(VirtualTerminal.CaretEscape(CaretStyle.SteadyBlock));
    }

    [Fact]
    public void ImmediateWriteForgetsTheCursorSoTheCaretIsReplaced()
    {
        var (terminal, output) = Parked(sync: true);
        terminal.WriteLine("hi");
        Take(output);

        terminal.Flush();

        Take(output).ShouldContain(VirtualTerminal.MoveEscape(5, 3));
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

    [Fact]
    public void ColorModeNoneWritesNoSyncEscapes()
    {
        var (terminal, output) = Create(sync: true, ColorMode.None);
        terminal.Write("X");
        terminal.SetCaret(1, 1, CaretStyle.BlinkingBar);

        terminal.Flush();

        var bytes = Take(output);
        bytes.ShouldNotContain("\e[?2026");
        bytes.ShouldNotContain("\e[?25");
        bytes.ShouldNotContain(" q");
    }

    [Theory]
    [InlineData("\e[?2026;1$y\e[?62;4c", true, "\e[?62;4c")]
    [InlineData("\e[?2026;2$y\e[?62;4c", true, "\e[?62;4c")]
    [InlineData("\e[?2026;0$y\e[?62;4c", false, "\e[?62;4c")]
    [InlineData("\e[?2026;3$y\e[?62;4c", true, "\e[?62;4c")]
    [InlineData("\e[?2026;4$y\e[?62;4c", false, "\e[?62;4c")]
    [InlineData("\e[?62;4c", false, "\e[?62;4c")]
    [InlineData("", false, "")]
    public void ParsesTheDecrqmReply(string reply, bool supported, string remainder)
    {
        var (ok, rest) = VirtualTerminal.ParseSynchronizedOutputReply(reply);

        ok.ShouldBe(supported);
        rest.ShouldBe(remainder);
    }

    [Fact]
    public void ThrowingSinkStillClosesTheSyncBracket()
    {
        var output = new ThrowOnceWriter("X");
        var terminal = new VirtualTerminal(output, 40, 10, ColorMode.TrueColor, true);
        terminal.EnableCellBuffer();
        terminal.Write("X");

        Should.Throw<IOException>(() => terminal.Flush());

        var bytes = output.ToString();
        bytes.ShouldStartWith(SyncOn);
        bytes.ShouldEndWith(SyncOff);
    }

    /// <summary>Records everything and throws once, on the first write containing a trigger (a closed pipe).</summary>
    private sealed class ThrowOnceWriter(string trigger) : StringWriter
    {
        private bool _thrown;

        private void Check(string text)
        {
            if (_thrown || !text.Contains(trigger, StringComparison.Ordinal)) return;
            _thrown = true;
            throw new IOException("closed pipe");
        }

        public override void Write(char value) { Check(value.ToString()); base.Write(value); }
        public override void Write(string? value) { Check(value ?? ""); base.Write(value); }
        public override void Write(char[] buffer, int index, int count) { Check(new string(buffer, index, count)); base.Write(buffer, index, count); }
        public override void Write(ReadOnlySpan<char> buffer) { Check(new string(buffer)); base.Write(buffer); }
    }

    [Fact]
    public async Task ReadResponseWaitsForASecondReplyArrivingAfterAnEmptyPoll()
    {
        // DECRQM reply is there at once; the DA1 reply lands only after an empty poll.
        var input = new Queue<char>("\e[?2026;2$y");
        var delays = 0;
        var result = await VirtualTerminal.ReadResponseAsync(
            'c',
            () => input.Count > 0,
            () => input.Dequeue(),
            _ =>
            {
                if (++delays == 1) foreach (var ch in "\e[?62;4c") input.Enqueue(ch);
                return Task.CompletedTask;
            });

        result.ShouldBe("\e[?2026;2$y\e[?62;4c");
        delays.ShouldBe(1);
    }

    [Fact]
    public async Task ReadResponseGivesUpAfterBoundedIdlePolls()
    {
        var delays = 0;
        var result = await VirtualTerminal.ReadResponseAsync(
            'c', () => false, () => throw new InvalidOperationException(), _ => { delays++; return Task.CompletedTask; }, maxIdlePolls: 3);

        result.ShouldBe("");
        delays.ShouldBe(3);
    }
}
