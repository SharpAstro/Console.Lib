using System.IO;
using Console.Lib;
using DIR.Lib;
using Shouldly;
using Xunit;

namespace Console.Lib.Tests;

/// <summary>
/// The caret is the terminal's REAL cursor, parked at the insertion point and drawn (and blinked) by the
/// terminal itself — the only way a cell surface gets a thinner-than-a-cell editor bar. Three layers are
/// under test: the DECSCUSR/DECTCEM protocol (<see cref="VirtualTerminal.CaretTransition"/>), the viewport
/// coordinate translation, and the two widgets' placement arithmetic (which must land on the exact cell
/// their painted reverse-video block used to occupy).
/// </summary>
public sealed class CaretTests
{
    // -----------------------------------------------------------------------
    // CaretEscape — DECSCUSR. The space before the q is an intermediate byte,
    // part of the sequence; a terminal receiving "\e[5q" instead would ignore
    // it (or worse), so the byte string is pinned here verbatim.
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(CaretStyle.BlinkingBlock, "\e[1 q")]
    [InlineData(CaretStyle.SteadyBlock, "\e[2 q")]
    [InlineData(CaretStyle.BlinkingUnderline, "\e[3 q")]
    [InlineData(CaretStyle.SteadyUnderline, "\e[4 q")]
    [InlineData(CaretStyle.BlinkingBar, "\e[5 q")]
    [InlineData(CaretStyle.SteadyBar, "\e[6 q")]
    public void CaretEscape_IsDecscusrWithIntermediateSpace(CaretStyle style, string expected)
        => VirtualTerminal.CaretEscape(style).ShouldBe(expected);

    // -----------------------------------------------------------------------
    // CaretTransition — shape and visibility emit only on a CHANGE (the pen's
    // "emit less" rule); position emits every time, because the paint that
    // just ran moved the real cursor as a side effect of writing.
    // -----------------------------------------------------------------------

    [Fact]
    public void CaretTransition_NoCaretNeverShown_EmitsNothing()
    {
        var (escape, shown, shape) = VirtualTerminal.CaretTransition(null, shown: false, shape: null);

        escape.ShouldBe("");
        shown.ShouldBeFalse();
        shape.ShouldBeNull();
    }

    [Fact]
    public void CaretTransition_CaretWithdrawn_HidesButKeepsShape()
    {
        // The terminal keeps its DECSCUSR shape while hidden, so remembering it is what lets the
        // re-show skip the DECSCUSR re-emit.
        var (escape, shown, shape) = VirtualTerminal.CaretTransition(
            null, shown: true, shape: CaretStyle.BlinkingBar);

        escape.ShouldBe(VirtualTerminal.HideCursorEscape);
        shown.ShouldBeFalse();
        shape.ShouldBe(CaretStyle.BlinkingBar);
    }

    [Fact]
    public void CaretTransition_FirstShow_EmitsShapeThenMoveThenShow()
    {
        // Shape before show: the cursor must not become visible in its previous shape for a frame.
        var (escape, shown, shape) = VirtualTerminal.CaretTransition(
            (5, 2, CaretStyle.BlinkingBar), shown: false, shape: null);

        escape.ShouldBe($"{VirtualTerminal.CaretEscape(CaretStyle.BlinkingBar)}\e[3;6H{VirtualTerminal.ShowCursorEscape}");
        shown.ShouldBeTrue();
        shape.ShouldBe(CaretStyle.BlinkingBar);
    }

    [Fact]
    public void CaretTransition_ShownSameShape_EmitsMoveOnly()
    {
        var (escape, shown, shape) = VirtualTerminal.CaretTransition(
            (5, 2, CaretStyle.BlinkingBar), shown: true, shape: CaretStyle.BlinkingBar);

        escape.ShouldBe("\e[3;6H");
        shown.ShouldBeTrue();
        shape.ShouldBe(CaretStyle.BlinkingBar);
    }

    [Fact]
    public void CaretTransition_ShownDifferentShape_ReEmitsShape()
    {
        var (escape, shown, shape) = VirtualTerminal.CaretTransition(
            (5, 2, CaretStyle.SteadyBar), shown: true, shape: CaretStyle.BlinkingBar);

        escape.ShouldBe($"{VirtualTerminal.CaretEscape(CaretStyle.SteadyBar)}\e[3;6H");
        shown.ShouldBeTrue();
        shape.ShouldBe(CaretStyle.SteadyBar);
    }

    [Fact]
    public void CaretTransition_ReShowAfterHideSameShape_SkipsShapeEmit()
    {
        var (escape, shown, shape) = VirtualTerminal.CaretTransition(
            (0, 0, CaretStyle.BlinkingBar), shown: false, shape: CaretStyle.BlinkingBar);

        escape.ShouldBe($"\e[1;1H{VirtualTerminal.ShowCursorEscape}");
        shown.ShouldBeTrue();
        shape.ShouldBe(CaretStyle.BlinkingBar);
    }

    // -----------------------------------------------------------------------
    // TerminalViewport — the caret translates like every other cell write.
    // -----------------------------------------------------------------------

    [Fact]
    public void ViewportSetCaret_AddsOffsetToCoordinates()
    {
        var terminal = new FakeTerminal(new Queue<ConsoleInputEvent>(), 80, 24);
        var viewport = new TerminalViewport(terminal, 10, 5, 30, 15);

        viewport.SetCaret(3, 7, CaretStyle.BlinkingBar);

        terminal.Caret.ShouldBe((13, 12, CaretStyle.BlinkingBar));
    }

    [Fact]
    public void ViewportSetCaret_ClampsToViewportBounds()
    {
        var terminal = new FakeTerminal(new Queue<ConsoleInputEvent>(), 80, 24);
        var viewport = new TerminalViewport(terminal, 10, 5, 30, 15);

        viewport.SetCaret(50, 20, CaretStyle.SteadyBar);

        terminal.Caret.ShouldBe((10 + 29, 5 + 14, CaretStyle.SteadyBar));
    }

    [Fact]
    public void ViewportHideCaret_Forwards()
    {
        var terminal = new FakeTerminal(new Queue<ConsoleInputEvent>(), 80, 24);
        var viewport = new TerminalViewport(terminal, 10, 5, 30, 15);

        viewport.SetCaret(0, 0, CaretStyle.BlinkingBar);
        viewport.HideCaret();

        terminal.Caret.ShouldBeNull();
    }

    // -----------------------------------------------------------------------
    // TextInputBar — caret mode parks the real cursor on the cell the painted
    // reverse-video block used to occupy, and stops painting the block.
    // -----------------------------------------------------------------------

    [Fact]
    public void TextInputBar_Default_PaintsReverseVideoAndParksNoCaret()
    {
        var viewport = new RecordingViewport(40, 1);
        var bar = new TextInputBar(viewport) { State = TypedState("adsadf") };

        bar.Render();

        string.Concat(viewport.Writes).ShouldContain(VtStyle.ReverseOn);
        viewport.Caret.ShouldBeNull();
    }

    [Fact]
    public void TextInputBar_CaretMode_ParksAtCursorCellWithoutReverseVideo()
    {
        var viewport = new RecordingViewport(40, 1);
        var bar = new TextInputBar(viewport) { State = TypedState("adsadf") }
            .Caret(CaretStyle.BlinkingBar);

        bar.Render();

        string.Concat(viewport.Writes).ShouldNotContain(VtStyle.ReverseOn);
        viewport.Caret.ShouldBe((6, 0, CaretStyle.BlinkingBar));
    }

    [Fact]
    public void TextInputBar_CaretMode_AccountsForLabelAndItsSeparator()
    {
        var viewport = new RecordingViewport(40, 1);
        var bar = new TextInputBar(viewport) { State = TypedState("hi") }
            .Label("Input:")
            .Caret(CaretStyle.BlinkingBar);

        bar.Render();

        // "Input:" is 6 cells plus the separating space the label render appends.
        viewport.Caret.ShouldBe((6 + 1 + 2, 0, CaretStyle.BlinkingBar));
    }

    [Fact]
    public void TextInputBar_CaretMode_CountsSurrogatePairAsOneCell()
    {
        // "🙂" is two UTF-16 chars but renders as one cell, so a caret after "🙂a" sits at cell 2 —
        // the same cell the painted block would have occupied, not char offset 3.
        var viewport = new RecordingViewport(40, 1);
        var bar = new TextInputBar(viewport) { State = TypedState("🙂a") }
            .Caret(CaretStyle.BlinkingBar);

        bar.Render();

        viewport.Caret.ShouldBe((2, 0, CaretStyle.BlinkingBar));
    }

    // -----------------------------------------------------------------------
    // TextArea — gutter offset, scroll-relative row, click-mapping cell
    // accounting (tab = TabWidth, surrogate pair = 1), and clip-to-hide.
    // -----------------------------------------------------------------------

    [Fact]
    public void TextArea_Default_PaintsReverseVideoAndParksNoCaret()
    {
        var viewport = new RecordingViewport(20, 3);
        var area = new TextArea(viewport) { State = new TextAreaState("hello\nworld") };

        area.Render();

        string.Concat(viewport.Writes).ShouldContain(VtStyle.ReverseOn);
        viewport.Caret.ShouldBeNull();
    }

    [Fact]
    public void TextArea_CaretMode_ParksPastGutterWithoutReverseVideo()
    {
        var viewport = new RecordingViewport(20, 3);
        var state = new TextAreaState("hello\nworld");
        state.MoveTo(0, 2);
        var area = new TextArea(viewport) { State = state }.Caret(CaretStyle.BlinkingBar);

        area.Render();

        // Gutter is max(4, digits+1) = 4 cells for a 2-line buffer.
        string.Concat(viewport.Writes).ShouldNotContain(VtStyle.ReverseOn);
        viewport.Caret.ShouldBe((4 + 2, 0, CaretStyle.BlinkingBar));
    }

    [Fact]
    public void TextArea_CaretMode_RowIsScrollRelative()
    {
        var viewport = new RecordingViewport(20, 3);
        var state = new TextAreaState("l0\nl1\nl2\nl3\nl4\nl5");
        state.MoveTo(5, 1);
        var area = new TextArea(viewport) { State = state }.Caret(CaretStyle.BlinkingBar);

        area.Render();

        // Follow-the-cursor scroll puts line 5 on the bottom row of a 3-row viewport.
        viewport.Caret.ShouldBe((4 + 1, 2, CaretStyle.BlinkingBar));
    }

    [Fact]
    public void TextArea_CaretMode_TabCountsTabWidthCells()
    {
        var viewport = new RecordingViewport(20, 3);
        var state = new TextAreaState("\thi");
        state.MoveTo(0, 1);
        var area = new TextArea(viewport) { State = state }.Caret(CaretStyle.BlinkingBar);

        area.Render();

        // Same accounting as click mapping (CellOffsetToByteOffset): the tab byte spans 4 cells, so a
        // click there and the caret it places round-trip to the same cell.
        viewport.Caret.ShouldBe((4 + 4, 0, CaretStyle.BlinkingBar));
    }

    [Fact]
    public void TextArea_CaretMode_ClippedCursorHidesTheCaret()
    {
        // contentWidth is 6 - 4 (gutter) = 2 cells; a cursor at cell 4 is off the paintable area, and a
        // stale caret from the previous frame must be withdrawn, not left standing on the wrong cell.
        var viewport = new RecordingViewport(6, 1);
        var state = new TextAreaState("hello");
        var area = new TextArea(viewport) { State = state }.Caret(CaretStyle.BlinkingBar);

        area.Render();
        viewport.Caret.ShouldBe((4, 0, CaretStyle.BlinkingBar));

        state.MoveTo(0, 4);
        area.Render();

        viewport.Caret.ShouldBeNull();
        viewport.CaretHidden.ShouldBeTrue();
    }

    // -----------------------------------------------------------------------
    // TextBar — the caller composes the text and passes the column, but the BAR
    // owns the clipping decision, because the bar owns the truncation.
    // -----------------------------------------------------------------------

    [Fact]
    public void TextBar_WithoutCaretCall_NeverTouchesTheCaret()
    {
        // Most bars are labels. If rendering one withdrew the caret, any status bar painted after the
        // focused editor would erase that editor's caret every frame.
        var viewport = new RecordingViewport(20, 1);
        var bar = new TextBar(viewport).Text("Site: 33S 151E");

        bar.Render();

        viewport.Caret.ShouldBeNull();
        viewport.CaretHidden.ShouldBeFalse();
    }

    [Fact]
    public void TextBar_Caret_ParksAtColumnOfLeftText()
    {
        var viewport = new RecordingViewport(20, 1);
        var bar = new TextBar(viewport).Text(" Lat: [33.8]").Caret(8, CaretStyle.BlinkingBar);

        bar.Render();

        viewport.Caret.ShouldBe((8, 0, CaretStyle.BlinkingBar));
    }

    [Fact]
    public void TextBar_Caret_ColumnJustPastTheTextIsAnInsertionPoint()
    {
        // A caret one past the last character is where typing appends; the cell is blank padding, not
        // truncated content, so it is a legitimate park.
        var viewport = new RecordingViewport(20, 1);
        var bar = new TextBar(viewport).Text("abc").Caret(3, CaretStyle.BlinkingBar);

        bar.Render();

        viewport.Caret.ShouldBe((3, 0, CaretStyle.BlinkingBar));
    }

    [Fact]
    public void TextBar_Caret_NullWithdraws()
    {
        var viewport = new RecordingViewport(20, 1);
        var bar = new TextBar(viewport).Text("abc").Caret(1, CaretStyle.BlinkingBar);

        bar.Render();
        viewport.Caret.ShouldNotBeNull();

        bar.Caret(null, CaretStyle.BlinkingBar).Render();

        viewport.Caret.ShouldBeNull();
        viewport.CaretHidden.ShouldBeTrue();
    }

    [Fact]
    public void TextBar_Caret_ColumnEatenByTheEllipsisWithdraws()
    {
        // padWidth 19, text 25 chars -> left renders as text[..18] + ellipsis. Column 18 IS the ellipsis:
        // it stands for text the user cannot see, so a caret there points at nothing.
        var viewport = new RecordingViewport(20, 1);
        var bar = new TextBar(viewport).Text(new string('x', 25)).RightText("R");

        bar.Caret(17, CaretStyle.BlinkingBar).Render();
        viewport.Caret.ShouldBe((17, 0, CaretStyle.BlinkingBar));

        bar.Caret(18, CaretStyle.BlinkingBar).Render();

        viewport.Caret.ShouldBeNull();
        viewport.CaretHidden.ShouldBeTrue();
    }

    [Fact]
    public void TextBar_Caret_ColumnInsideTheRightTextRegionWithdraws()
    {
        // The right text wins the row's tail; a caret parked under it would sit on the hint, not the
        // field being edited.
        var viewport = new RecordingViewport(20, 1);
        var bar = new TextBar(viewport).Text("short").RightText(new string('R', 10));

        bar.Caret(9, CaretStyle.BlinkingBar).Render();
        viewport.Caret.ShouldBe((9, 0, CaretStyle.BlinkingBar));

        bar.Caret(10, CaretStyle.BlinkingBar).Render();

        viewport.Caret.ShouldBeNull();
        viewport.CaretHidden.ShouldBeTrue();
    }

    [Fact]
    public void TextBar_Caret_ZeroWidthRowWithdraws()
    {
        // A bar squeezed to nothing paints nothing, and a caret left parked from the last frame would
        // stand on a row this render never drew.
        var viewport = new RecordingViewport(0, 1);
        var bar = new TextBar(viewport).Text("abc").Caret(1, CaretStyle.BlinkingBar);

        bar.Render();

        viewport.Caret.ShouldBeNull();
        viewport.CaretHidden.ShouldBeTrue();
    }

    private static TextInputState TypedState(string text)
    {
        var state = new TextInputState();
        state.InsertText(text);
        return state;
    }

    /// <summary>Viewport that records writes and caret calls — enough surface for the widget renders.</summary>
    private sealed class RecordingViewport(int width, int height) : ITerminalViewport
    {
        public List<string> Writes { get; } = [];
        public (int Column, int Row, CaretStyle Style)? Caret { get; private set; }
        public bool CaretHidden { get; private set; }

        public (int Column, int Row) Offset => (0, 0);
        public (int Width, int Height) Size => (width, height);
        public void SetCursorPosition(int left, int top) { }
        public void Write(string text) => Writes.Add(text);
        public void WriteLine(string? text = null) { }
        public TermCell CellSize => new(10, 20);
        public void Flush() { }
        public Stream OutputStream => Stream.Null;
        public void SetCaret(int column, int row, CaretStyle style)
        {
            Caret = (column, row, style);
            CaretHidden = false;
        }
        public void HideCaret()
        {
            Caret = null;
            CaretHidden = true;
        }
    }
}
