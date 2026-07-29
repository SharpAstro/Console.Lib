using System;
using System.Collections.Generic;
using System.IO;
using DIR.Lib;
using Shouldly;
using Xunit;

namespace Console.Lib.Tests;

/// <summary>
/// Raw output (a Sixel blit) against a buffered terminal — the interaction that broke the chess board the
/// first time the cell buffer was switched on, and the one most likely to corrupt real output.
///
/// <para>Two separate failures were live at once, and each is asserted here:</para>
/// <list type="number">
/// <item>Sixel bytes go to <see cref="ITerminalViewport.OutputStream"/>, bypassing the buffer — but
/// <c>SetCursorPosition</c> did NOT bypass it, so it moved only the buffer's cursor and the blit landed
/// wherever the real cursor was left. The board rendered nowhere.</item>
/// <item>The covered cells were never declared, so the diff happily painted blanks over the picture.</item>
/// </list>
///
/// <para>Hence <see cref="ITerminalViewport.BeginRawOutput"/> and
/// <see cref="ITerminalViewport.MarkRawRegion"/>: raw output is a declared operation, not something that
/// happens to work because writes were immediate.</para>
/// </summary>
public class RawOutputRegionTests
{
    /// <summary>Records the raw-output calls a widget makes, and offsets like a real nested viewport.</summary>
    private sealed class RecordingViewport(int columns, int rows, int columnOffset = 0, int rowOffset = 0)
        : ITerminalViewport
    {
        public readonly List<(int Column, int Row)> RawStarts = [];
        public readonly List<(int Column, int Row, int Width, int Height)> RawRegions = [];
        public readonly List<(int Column, int Row)> CursorMoves = [];

        public (int Width, int Height) Size => (columns, rows);
        public (int Column, int Row) Offset => (columnOffset, rowOffset);
        public TermCell CellSize => new(10, 20);
        public ColorMode ColorMode => ColorMode.TrueColor;
        public Stream OutputStream { get; } = new MemoryStream();

        public void SetCursorPosition(int left, int top) => CursorMoves.Add((columnOffset + left, rowOffset + top));
        public void Write(string text) { }
        public void WriteLine(string? text = null) { }
        public void Flush() { }

        public void BeginRawOutput(int column, int row) => RawStarts.Add((columnOffset + column, rowOffset + row));
        public void MarkRawRegion(int column, int row, int width, int height)
            => RawRegions.Add((columnOffset + column, rowOffset + row, width, height));
    }

    private sealed class StubEncoder(uint height) : ISixelEncoder
    {
        public uint Height => height;
        public void EncodeSixel(Stream output) => output.WriteByte((byte)'x');
        public void EncodeSixel(int startY, uint height1, Stream output) => output.WriteByte((byte)'y');
    }

    [Fact]
    public void AFullBlit_DeclaresItsStartAndItsWholeRegion()
    {
        var vp = new RecordingViewport(20, 6);
        var canvas = new Canvas(vp, new StubEncoder(120));

        canvas.Render();

        vp.RawStarts.ShouldHaveSingleItem().ShouldBe((0, 0),
            "the blit must position the REAL cursor, which SetCursorPosition no longer does when buffered");
        vp.RawRegions.ShouldHaveSingleItem().ShouldBe((0, 0, 20, 6));
        vp.CursorMoves.ShouldBeEmpty("a raw blit does not go through the buffered cursor at all");
    }

    [Fact]
    public void APartialBlit_DeclaresOnlyTheRowsItPainted()
    {
        var vp = new RecordingViewport(20, 6);
        var canvas = new Canvas(vp, new StubEncoder(120));

        // Pixel rows 40..79 at a 20px cell = character rows 2..3.
        canvas.Render(new RectInt(new PointInt(200, 79), new PointInt(0, 40)));

        vp.RawStarts.ShouldHaveSingleItem().ShouldBe((0, 2));
        vp.RawRegions.ShouldHaveSingleItem().ShouldBe((0, 2, 20, 2));
    }

    /// <summary>
    /// A hosted viewport sits at an offset inside the terminal, and both raw calls have to be translated —
    /// an untranslated region would declare the wrong cells and leave the real ones open to the diff.
    /// </summary>
    [Fact]
    public void ANestedViewport_TranslatesBothRawCallsToAbsoluteCells()
    {
        var vp = new RecordingViewport(10, 4, columnOffset: 24, rowOffset: 1);
        var canvas = new Canvas(vp, new StubEncoder(80));

        canvas.Render();

        vp.RawStarts.ShouldHaveSingleItem().ShouldBe((24, 1));
        vp.RawRegions.ShouldHaveSingleItem().ShouldBe((24, 1, 10, 4));
    }

    /// <summary>
    /// The default implementations keep an immediate-mode terminal behaving exactly as before: BeginRawOutput
    /// falls back to moving the cursor, and MarkRawRegion does nothing, because there is no diff to protect
    /// the region from.
    /// </summary>
    [Fact]
    public void AnUnbufferedViewport_FallsBackToPlainCursorMovement()
    {
        var vp = new LegacyViewport();
        var canvas = new Canvas(vp, new StubEncoder(80));

        canvas.Render();

        vp.CursorMoves.ShouldHaveSingleItem().ShouldBe((0, 0),
            "the interface default routes BeginRawOutput to SetCursorPosition");
    }

    /// <summary>A viewport written before the raw-output members existed — i.e. it overrides neither.</summary>
    private sealed class LegacyViewport : ITerminalViewport
    {
        public readonly List<(int Column, int Row)> CursorMoves = [];
        public (int Width, int Height) Size => (8, 4);
        public (int Column, int Row) Offset => (0, 0);
        public TermCell CellSize => new(10, 20);
        public ColorMode ColorMode => ColorMode.None;
        public Stream OutputStream { get; } = new MemoryStream();
        public void SetCursorPosition(int left, int top) => CursorMoves.Add((left, top));
        public void Write(string text) { }
        public void WriteLine(string? text = null) { }
        public void Flush() { }
    }

    /// <summary>
    /// End to end through the buffer: a declared image region survives a diff that would otherwise blank it,
    /// which is the guarantee the board depends on.
    /// </summary>
    [Fact]
    public void ADeclaredRegion_SurvivesADiffThatWouldOtherwiseBlankIt()
    {
        var buffer = new CellBuffer { ColorMode = ColorMode.TrueColor };
        buffer.Resize(10, 3);

        // Frame 1: chrome everywhere, then the picture claims the middle row.
        buffer.MoveTo(0, 0);
        buffer.Write(new string('a', 10));
        buffer.MarkImage(0, 1, 10, 1);
        buffer.Flush(new NullSink());

        // Frame 2: a widget writes across the whole grid, image row included.
        buffer.MoveTo(0, 0);
        buffer.Write(new string('b', 30));

        // Re-declared after the blit, exactly as Canvas does it.
        buffer.MarkImage(0, 1, 10, 1);

        var sink = new CountingSink();
        buffer.Flush(sink);

        sink.Written.ShouldBe(20, "rows 0 and 2 repaint; the image row is left alone");
        sink.Text.Length.ShouldBe(20, "no run reached across the image row");
        sink.Moves.Count.ShouldBe(2, "one run per surviving row, the image row breaking them apart");
        buffer.FrontAt(3, 1).Kind.ShouldBe(CellKind.Image);
    }

    private sealed class NullSink : ICellSink
    {
        public void MoveTo(int column, int row) { }
        public void SetPen(VtStyle style, bool reverse) { }
        public void Write(ReadOnlySpan<char> run) { }
    }

    private sealed class CountingSink : ICellSink
    {
        public int Written;
        public string Text = "";
        public readonly List<(int Column, int Row)> Moves = [];
        public void MoveTo(int column, int row) => Moves.Add((column, row));
        public void SetPen(VtStyle style, bool reverse) { }
        public void Write(ReadOnlySpan<char> run) { Written += run.Length; Text += run.ToString(); }
    }
}
