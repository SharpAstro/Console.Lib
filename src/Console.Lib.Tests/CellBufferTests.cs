using System;
using System.Collections.Generic;
using System.Text;
using DIR.Lib;
using Shouldly;
using Xunit;

namespace Console.Lib.Tests;

/// <summary>
/// The front/back cell buffer and its diffing flush.
///
/// <para>The case that motivated it is <see cref="ATickingClock_EmitsOnlyTheDigitsThatChanged"/>. Console.Lib
/// was immediate-mode: a widget wrote its whole region as one string of SGR plus padded text, which is
/// invisible for a redraw the user asked for and very visible once per second on a clock — every cell in the
/// row repainted, padding spaces included, which reads as a flash.</para>
/// </summary>
public class CellBufferTests
{
    /// <summary>Records what a flush emitted, so the diff can be asserted on directly.</summary>
    private sealed class RecordingSink : ICellSink
    {
        public readonly List<(int Column, int Row)> Moves = [];
        public readonly List<(VtStyle Style, bool Reverse)> Pens = [];
        public readonly StringBuilder Text = new();

        /// <summary>Each emitted run paired with the link in force for it — what a hyperlink test asserts on,
        /// because the interesting property is which GLYPHS ended up inside which link, not the call order.</summary>
        public readonly List<(string? Link, string Run)> LinkedRuns = [];

        private string? _link;

        public void MoveTo(int column, int row) => Moves.Add((column, row));
        public void SetPen(VtStyle style, bool reverse) => Pens.Add((style, reverse));
        public void SetLink(string? url) => _link = url;

        public void Write(ReadOnlySpan<char> run)
        {
            Text.Append(run);
            LinkedRuns.Add((_link, run.ToString()));
        }
    }

    private static CellBuffer Sized(int w, int h, ColorMode mode = ColorMode.TrueColor)
    {
        var buf = new CellBuffer { ColorMode = mode };
        buf.Resize(w, h);
        return buf;
    }

    private static readonly VtStyle Style = new(
        new RGBAColor32(0xFF, 0xCE, 0x9E, 0xff), new RGBAColor32(0x20, 0x20, 0x34, 0xff));

    /// <summary>How a widget really writes a row today — see TextBar.Render.</summary>
    private static string Row(string text, int width)
        => $"{Style.Apply(ColorMode.TrueColor)}{text.PadRight(width)}{VtStyle.Reset}";

    /// <summary>
    /// The point of the whole exercise. A clock row is written in full every tick; only the seconds change,
    /// so only the seconds may reach the terminal.
    /// </summary>
    [Fact]
    public void ATickingClock_EmitsOnlyTheDigitsThatChanged()
    {
        var buf = Sized(40, 3);

        buf.MoveTo(0, 1);
        buf.Write(Row("Elapsed 00:01:58", 40));
        buf.Flush(new RecordingSink()).ShouldBe(40 * 3,
            "the first flush paints the whole grid — what the terminal already held is unknown");

        // One second later: the widget re-renders the identical row with one character different.
        var sink = new RecordingSink();
        buf.MoveTo(0, 1);
        buf.Write(Row("Elapsed 00:01:59", 40));
        var emitted = buf.Flush(sink);

        emitted.ShouldBe(1, "40 cells were rewritten; exactly one of them changed");
        sink.Text.ToString().ShouldBe("9");
        sink.Moves.ShouldHaveSingleItem().ShouldBe((15, 1), "one cursor move, straight to the digit");
    }

    [Fact]
    public void ReflushingUnchangedContent_EmitsNothing()
    {
        var buf = Sized(20, 2);
        buf.MoveTo(0, 0);
        buf.Write(Row("hello", 20));
        buf.Flush(new RecordingSink());

        var sink = new RecordingSink();
        buf.MoveTo(0, 0);
        buf.Write(Row("hello", 20));

        buf.Flush(sink).ShouldBe(0);
        sink.Moves.ShouldBeEmpty();
    }

    /// <summary>
    /// A resized terminal has no relationship to what was on screen, so the first flush after one must
    /// repaint everything rather than trusting a stale front buffer.
    /// </summary>
    [Fact]
    public void TheFirstFlushAfterAResize_RepaintsEverything()
    {
        var buf = Sized(10, 2);
        buf.MoveTo(0, 0);
        buf.Write(Row("x", 10));
        buf.Flush(new RecordingSink());

        buf.Resize(10, 2);

        buf.Flush(new RecordingSink()).ShouldBe(20, "every cell of the new grid is unknown");
    }

    /// <summary>A recolour with identical text still has to reach the terminal.</summary>
    [Fact]
    public void AStyleOnlyChange_CountsAsDirty()
    {
        var buf = Sized(8, 1);
        buf.MoveTo(0, 0);
        buf.Write($"{Style.Apply(ColorMode.TrueColor)}abc");
        buf.Flush(new RecordingSink());

        var other = new VtStyle(new RGBAColor32(0xFF, 0x00, 0x00, 0xff), Style.Background);
        var sink = new RecordingSink();
        buf.MoveTo(0, 0);
        buf.Write($"{other.Apply(ColorMode.TrueColor)}abc");

        buf.Flush(sink).ShouldBe(3);
        sink.Text.ToString().ShouldBe("abc");
        sink.Pens.ShouldHaveSingleItem().Style.Foreground.ShouldBe(other.Foreground);
    }

    [Fact]
    public void ContiguousChangesInOnePen_CoalesceIntoASingleRun()
    {
        var buf = Sized(20, 1);
        buf.MoveTo(0, 0);
        buf.Write(Row("aaaaaaaaaa", 20));
        buf.Flush(new RecordingSink());

        var sink = new RecordingSink();
        buf.MoveTo(2, 0);
        buf.Write($"{Style.Apply(ColorMode.TrueColor)}bbbb");

        buf.Flush(sink).ShouldBe(4);
        sink.Moves.ShouldHaveSingleItem().ShouldBe((2, 0));
        sink.Pens.Count.ShouldBe(1, "one pen selection for the whole run");
        sink.Text.ToString().ShouldBe("bbbb");
    }

    [Theory]
    [InlineData(ColorMode.TrueColor)]
    [InlineData(ColorMode.Sgr16)]
    public void ThePenParsesBackOutOfWhatVtStyleEmits(ColorMode mode)
    {
        var buf = Sized(4, 1, mode);
        buf.MoveTo(0, 0);
        buf.Write($"{Style.Apply(mode)}q");

        var cell = buf.BackAt(0, 0);
        cell.Glyph.ShouldBe('q');
        cell.Kind.ShouldBe(CellKind.Text, "our own SGR must always be modellable");
        if (mode == ColorMode.TrueColor)
        {
            cell.Style.ShouldBe(Style, "truecolor round-trips exactly");
        }
    }

    [Fact]
    public void ReverseVideo_IsPartOfThePen()
    {
        var buf = Sized(4, 1);
        buf.MoveTo(0, 0);
        buf.Write($"{VtStyle.ReverseOn}a{VtStyle.ReverseOff}b");

        buf.BackAt(0, 0).Reverse.ShouldBeTrue();
        buf.BackAt(1, 0).Reverse.ShouldBeFalse();
    }

    /// <summary>
    /// The escape hatch that keeps this from being a terminal emulator. An OSC the buffer does not model —
    /// here OSC 0, a window title — leaves the cells after it always re-emitted, degrading to the
    /// immediate-mode behaviour they had before rather than being modelled wrongly and skipped.
    /// <para>
    /// This used to be written with an OSC 8 hyperlink, which is no longer an example of the rule: a link is
    /// modelled per cell now (see <see cref="AHyperlinkedRun_IsModelledPerCellAndStillDiffs"/>). The rule
    /// itself is unchanged, so the test keeps its assertion and changes its example.
    /// </para>
    /// </summary>
    [Fact]
    public void CellsWrittenUnderAnUnknownEscape_AreAlwaysReEmitted()
    {
        var buf = Sized(10, 1);

        buf.MoveTo(0, 0);
        buf.Write("\e]0;a window title\e\\link");
        buf.BackAt(0, 0).Kind.ShouldBe(CellKind.Opaque);
        buf.Flush(new RecordingSink());

        // Byte-identical content a second time: a Text cell would be skipped, an Opaque one must not be.
        var sink = new RecordingSink();
        buf.MoveTo(0, 0);
        buf.Write("\e]0;a window title\e\\link");

        buf.Flush(sink).ShouldBe(4, "opaque cells are never diffed away");
        sink.Text.ToString().ShouldBe("link");
    }

    [Fact]
    public void AnSgrAttributeOutsideTheVocabulary_AlsoGoesOpaque()
    {
        var buf = Sized(6, 1);
        buf.MoveTo(0, 0);
        buf.Write("\e[1mbold");   // bold is not something VtStyle emits

        buf.BackAt(0, 0).Kind.ShouldBe(CellKind.Opaque,
            "a pen we only mostly understand would show up as a MISSING repaint");
    }

    [Fact]
    public void AReset_MakesThePenKnownAgain()
    {
        var buf = Sized(8, 1);
        buf.MoveTo(0, 0);
        buf.Write($"\e[1mx{VtStyle.Reset}y");

        buf.BackAt(0, 0).Kind.ShouldBe(CellKind.Opaque);
        buf.BackAt(1, 0).Kind.ShouldBe(CellKind.Text, "after a reset the pen is known");
    }

    // ── OSC 8 hyperlinks ──────────────────────────────────────────────────────────────────────────────

    private const string Target = "file:///c/report.txt";

    /// <summary>
    /// The whole reason a link is cell state. Wrapping a write in OSC 8 used to make the pen unmodellable,
    /// so every linked cell was Opaque and went out again on every frame — invisible on one link, and on a
    /// file list where every row carries one it silently bypasses the diff for the entire column while the
    /// emitted-cell count still looks small.
    /// </summary>
    [Fact]
    public void AHyperlinkedRun_IsModelledPerCellAndStillDiffs()
    {
        var buf = Sized(10, 1);
        var write = $"\e]8;;{Target}\areport.txt\e]8;;\a";

        buf.MoveTo(0, 0);
        buf.Write(write);

        buf.BackAt(0, 0).Kind.ShouldBe(CellKind.Text, "a link is modelled, so its cells are not opaque");
        buf.BackAt(0, 0).Link.ShouldBe(Target);
        buf.BackAt(9, 0).Link.ShouldBe(Target, "the link covers every glyph inside the pair");

        var first = new RecordingSink();
        buf.Flush(first);
        first.LinkedRuns.ShouldContain(r => r.Link == Target && r.Run == "report.txt");

        // The same frame again: a modelled link diffs away to nothing, an opaque one would not.
        buf.MoveTo(0, 0);
        buf.Write(write);

        buf.Flush(new RecordingSink()).ShouldBe(0, "an unchanged linked row must emit nothing");
        buf.LastFlushOpaqueCells.ShouldBe(0);
    }

    /// <summary>The sink states one target per run, so the run has to end where the target changes.</summary>
    [Fact]
    public void ALinkBoundary_BreaksTheRun()
    {
        var buf = Sized(4, 1);
        buf.MoveTo(0, 0);
        buf.Write("\e]8;;https://a\aab\e]8;;\acd");

        var sink = new RecordingSink();
        buf.Flush(sink);

        sink.LinkedRuns.ShouldBe([("https://a", "ab"), (null, "cd")]);
    }

    /// <summary>
    /// SGR and OSC 8 are independent terminal state: <c>\e[0m</c> resets colour and leaves an open link
    /// open. It matters because it is exactly how a linked row gets written — the pen is stated INSIDE the
    /// link (see CellLayout.DrawText), so a reset that closed the link would drop it from every cell after
    /// the first styled span.
    /// </summary>
    [Fact]
    public void AnSgrReset_DoesNotCloseAnOpenHyperlink()
    {
        var buf = Sized(4, 1);
        buf.MoveTo(0, 0);
        buf.Write($"\e]8;;https://a\a{Style.Apply(ColorMode.TrueColor)}ab{VtStyle.Reset}cd\e]8;;\a");

        buf.BackAt(3, 0).Link.ShouldBe("https://a", "a colour reset is not a link close");
    }

    /// <summary>Same glyphs, different target — a change the diff has to see, or the row keeps the old link.</summary>
    [Fact]
    public void ChangingOnlyTheTarget_RepaintsTheCells()
    {
        var buf = Sized(2, 1);
        buf.MoveTo(0, 0);
        buf.Write("\e]8;;https://a\aab\e]8;;\a");
        buf.Flush(new RecordingSink());

        buf.MoveTo(0, 0);
        buf.Write("\e]8;;https://b\aab\e]8;;\a");

        var sink = new RecordingSink();
        buf.Flush(sink).ShouldBe(2, "the glyphs are identical but the link is not");
        sink.LinkedRuns.ShouldBe([("https://b", "ab")]);
    }

    /// <summary>
    /// Both OSC terminators. Console.Lib emits BEL (wider terminal support), but an app writing its own
    /// links is free to use ST, and a parser that only knew one would swallow the rest of the frame.
    /// </summary>
    [Theory]
    [InlineData("\a")]
    [InlineData("\e\\")]
    public void EitherOscTerminator_Parses(string terminator)
    {
        var buf = Sized(2, 1);
        buf.MoveTo(0, 0);
        buf.Write($"\e]8;;https://a{terminator}ab");

        buf.BackAt(0, 0).Link.ShouldBe("https://a");
        buf.BackAt(0, 0).Kind.ShouldBe(CellKind.Text);
    }

    /// <summary>
    /// OSC 8 with no URI field is malformed — the params are not a target. Falling back to unmodellable is
    /// the conservative answer; reading the params as a URI would bind every following cell to nonsense.
    /// </summary>
    [Fact]
    public void AMalformedHyperlink_StaysUnmodellable()
    {
        var buf = Sized(2, 1);
        buf.MoveTo(0, 0);
        buf.Write("\e]8;no-uri-field\ax");

        buf.BackAt(0, 0).Link.ShouldBeNull();
        buf.BackAt(0, 0).Kind.ShouldBe(CellKind.Opaque);
    }

    /// <summary>
    /// The <c>id=</c> field is skipped, not mistaken for the target. A terminal uses it to group runs; here
    /// equal URLs already do that, so it carries no information the cell needs.
    /// </summary>
    [Fact]
    public void TheIdParameter_IsSkippedRatherThanReadAsTheTarget()
    {
        var buf = Sized(2, 1);
        buf.MoveTo(0, 0);
        buf.Write($"\e]8;id=deadbeef;{Target}\aab");

        buf.BackAt(0, 0).Link.ShouldBe(Target);
    }

    /// <summary>A deterministic id, so the emitted bytes are assertable — string.GetHashCode is not.</summary>
    [Fact]
    public void TheEmittedIdIsStablePerTarget()
    {
        Osc8.IdFor(Target).ShouldBe(Osc8.IdFor(Target));
        Osc8.IdFor(Target).ShouldNotBe(Osc8.IdFor("file:///c/other.txt"));
        Osc8.Open(Target, Osc8.IdFor(Target)).ShouldBe($"\e]8;id={Osc8.IdFor(Target)};{Target}\a");
    }

    /// <summary>
    /// A Sixel blit owns pixels the buffer never sees, so the diff must not write glyphs into that region —
    /// doing so punches a hole in the picture. This is the interaction most likely to corrupt real output.
    /// </summary>
    [Fact]
    public void ImageCells_AreNeverWrittenByTheDiff()
    {
        var buf = Sized(10, 2);
        buf.MoveTo(0, 0);
        buf.Write(Row("aaaaaaaaaa", 10));
        buf.Flush(new RecordingSink());

        buf.MoveTo(0, 0);
        buf.Write(Row("bbbbbbbbbb", 10));
        buf.MarkImage(2, 0, 4, 1);

        var sink = new RecordingSink();
        buf.Flush(sink);

        sink.Text.ToString().ShouldBe("bbbbbb", "the four image cells are skipped, the six others emitted");
        sink.Moves.Count.ShouldBe(2, "the image region breaks the row into two runs");
    }

    /// <summary>Cells an image no longer covers must come back, which is what a shrinking board needs.</summary>
    [Fact]
    public void WritingText_ReclaimsAnImageCell()
    {
        var buf = Sized(6, 1);
        buf.MarkImage(0, 0, 6, 1);
        buf.Flush(new RecordingSink()).ShouldBe(0, "nothing to paint under an image");

        buf.MoveTo(0, 0);
        buf.Write(Row("hi", 6));

        buf.BackAt(0, 0).Kind.ShouldBe(CellKind.Text);
        buf.Flush(new RecordingSink()).ShouldBe(6, "the reclaimed cells paint");
    }

    /// <summary>
    /// The front buffer is the record of what was actually emitted, which is what makes it usable as the
    /// debug inspector's cell plane: not a parallel model that could drift, but the sent bytes themselves.
    /// </summary>
    [Fact]
    public void TheFrontBuffer_ReadsBackAsScreenText()
    {
        var buf = Sized(16, 2);
        buf.MoveTo(0, 0);
        buf.Write(Row(" Move History", 16));
        buf.MoveTo(0, 1);
        buf.Write(Row("White to move", 16));

        buf.FrontRowText(0).ShouldBe(new string(' ', 16), "nothing is on screen until the flush");

        buf.Flush(new RecordingSink());

        buf.FrontRowText(0).ShouldBe(" Move History   ");
        buf.FrontRowText(1).ShouldBe("White to move   ");
    }
}
