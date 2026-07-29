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

        public void MoveTo(int column, int row) => Moves.Add((column, row));
        public void SetPen(VtStyle style, bool reverse) => Pens.Add((style, reverse));
        public void Write(ReadOnlySpan<char> run) => Text.Append(run);
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
    /// The escape hatch that keeps this from being a terminal emulator. An OSC hyperlink (mdcat emits them)
    /// is not something the buffer models, so the cells it covers are always re-emitted — degrading to the
    /// immediate-mode behaviour they had before, rather than being modelled wrongly and skipped.
    /// </summary>
    [Fact]
    public void CellsWrittenUnderAnUnknownEscape_AreAlwaysReEmitted()
    {
        var buf = Sized(10, 1);

        buf.MoveTo(0, 0);
        buf.Write("\e]8;;https://example.com\e\\link");
        buf.BackAt(0, 0).Kind.ShouldBe(CellKind.Opaque);
        buf.Flush(new RecordingSink());

        // Byte-identical content a second time: a Text cell would be skipped, an Opaque one must not be.
        var sink = new RecordingSink();
        buf.MoveTo(0, 0);
        buf.Write("\e]8;;https://example.com\e\\link");

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
