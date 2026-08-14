using System.Collections.Immutable;
using DIR.Lib;
using Shouldly;
using Xunit;

namespace Console.Lib.Tests;

/// <summary>
/// <see cref="Layout.Content.TextInput"/> painted onto a character grid.
/// <para>
/// The leaf is shared with the pixel painter, which is the whole point: a field becomes ONE declaration that
/// both surfaces can draw, the way <c>HomeBoardLayout</c> already made a rig card one tree. What this pins is
/// the part that cannot be shared -- the three places a terminal has to answer differently from a GPU, each
/// of which is a decision rather than a limitation:
/// </para>
/// <list type="number">
/// <item>the fill IS the field, because a one-row box cannot also carry a border;</item>
/// <item>the caret is the terminal's REAL one, because a painted block can be neither thin nor blinking;</item>
/// <item>an over-long value SCROLLS, because an ellipsis in an editable field hides the text being edited.</item>
/// </list>
/// <para>
/// Together they are what let the TUI's hand-rolled caret arithmetic be retired rather than reimplemented:
/// the old site row computed a caret column by hand out of label lengths and separators, which is exactly the
/// arithmetic the arranged rect already knows.
/// </para>
/// </summary>
public class CellLayoutTextInputTests
{
    /// <summary>
    /// Paints into a real <see cref="CellBuffer"/> (so per-cell pens are assertable) AND records the caret
    /// requests, which are the field's other output and go nowhere near the cell grid.
    /// </summary>
    private sealed class CaretRecordingViewport(CellBuffer buffer, int width, int height) : ITerminalViewport
    {
        public (int Column, int Row) Offset => (0, 0);
        public (int Width, int Height) Size => (width, height);
        public TermCell CellSize => new(10, 20);
        public ColorMode ColorMode => Console.Lib.ColorMode.TrueColor;

        public (int Column, int Row, CaretStyle Style)? Caret { get; private set; }
        public bool CaretHidden { get; private set; }

        public void SetCursorPosition(int left, int top) => buffer.MoveTo(left, top);
        public void Write(string text) => buffer.Write(text);
        public void WriteLine(string? text = null) { }
        public void Flush() { }
        public Stream OutputStream => Stream.Null;
        public void SetCaret(int column, int row, CaretStyle style) => Caret = (column, row, style);
        public void HideCaret() => CaretHidden = true;
    }

    private static readonly TextInputColors Palette = new();

    private static (CellBuffer Buffer, CaretRecordingViewport Viewport) Paint(
        TextInputState state, int width, int height = 1)
    {
        var buffer = new CellBuffer { ColorMode = ColorMode.TrueColor };
        buffer.Resize(width, height);
        var viewport = new CaretRecordingViewport(buffer, width, height);

        var tree = Layout.Builder.VStack(Layout.Builder.TextInput(state, 1f).RowH(height).WStar());
        var arranged = Layout.Engine.Arrange(tree, new Rect<int>(0, 0, width, height), CellMeasureContext.CellAuthored);
        CellLayout.Paint(viewport, arranged);

        return (buffer, viewport);
    }

    private static string RowText(CellBuffer buffer, int width, int row = 0)
    {
        var chars = new char[width];
        for (var i = 0; i < width; i++)
        {
            chars[i] = buffer.BackAt(i, row).Glyph;
        }

        return new string(chars);
    }

    // ---- What is shown ----

    [Fact]
    public void AnEmptyUnfocusedField_ShowsItsPlaceholder()
    {
        var (buffer, _) = Paint(new TextInputState { Placeholder = "Lat" }, width: 8);

        RowText(buffer, 8).ShouldBe("Lat     ");
    }

    /// <summary>
    /// A focused empty field shows a bare caret, not the placeholder. A placeholder sitting under a live
    /// caret reads as text the user can edit, which makes the first keystroke look like a replace that went
    /// wrong. The pixel renderer already made this call; the cell painter has to make the same one, or the
    /// same field says different things on the two surfaces.
    /// </summary>
    [Fact]
    public void AFocusedEmptyField_ShowsNoPlaceholder()
    {
        var state = new TextInputState { Placeholder = "Lat" };
        state.Activate();

        var (buffer, viewport) = Paint(state, width: 8);

        RowText(buffer, 8).ShouldBe("        ");
        viewport.Caret!.Value.Column.ShouldBe(0);
    }

    [Fact]
    public void AFieldWithText_ShowsTheTextRatherThanThePlaceholder()
    {
        var state = new TextInputState { Placeholder = "Lat", Text = "-33.8" };

        var (buffer, _) = Paint(state, width: 8);

        RowText(buffer, 8).ShouldBe("-33.8   ");
    }

    // ---- The caret ----

    [Fact]
    public void AFocusedField_PutsTheTerminalCaretAtTheCursor()
    {
        var state = new TextInputState { Text = "abcdef" };
        state.Activate();
        state.CursorPos = 2;

        var (_, viewport) = Paint(state, width: 12);

        viewport.Caret.ShouldBe((2, 0, CaretStyle.BlinkingBar));
    }

    [Fact]
    public void AnUnfocusedField_AsksForNoCaret()
    {
        var (_, viewport) = Paint(new TextInputState { Text = "abc" }, width: 12);

        viewport.Caret.ShouldBeNull();
    }

    /// <summary>
    /// A caret sits BETWEEN characters, so a caret at the end of a value exactly filling the field needs a
    /// cell of its own past the last glyph. Without that the window is simply the last N characters and the
    /// caret lands one cell outside the field, on whatever the neighbour painted there.
    /// </summary>
    [Fact]
    public void ACaretAtTheEndOfAFullField_GetsItsOwnCell()
    {
        var state = new TextInputState { Text = "0123456789" };   // exactly the field width
        state.Activate();
        state.CursorPos = state.Text.Length;

        var (buffer, viewport) = Paint(state, width: 10);

        viewport.Caret!.Value.Column.ShouldBe(9, "the caret must stay inside the field");
        RowText(buffer, 10).ShouldBe("123456789 ", "so the value scrolls by one to make room for it");
    }

    // ---- Scrolling, not ellipsizing ----

    /// <summary>
    /// The behaviour that separates a field from a label. A label ellipsizes because the middle of a name is
    /// the part it can afford to lose; a field cannot, because the "…" would sit exactly where the text being
    /// edited belongs and the caret would have no real cell to land on.
    /// </summary>
    [Fact]
    public void AnOverlongValue_ScrollsToTheCaretInsteadOfEllipsizing()
    {
        var state = new TextInputState { Text = "0123456789ABCDEF" };
        state.Activate();
        state.CursorPos = state.Text.Length;

        var (buffer, viewport) = Paint(state, width: 10);

        var row = RowText(buffer, 10);
        row.ShouldNotContain("…", Case.Sensitive, "an ellipsis would hide the text being edited");
        row.ShouldBe("789ABCDEF ");
        viewport.Caret!.Value.Column.ShouldBe(9);
    }

    /// <summary>
    /// Scrolling follows the caret in both directions: moving back to the start must bring the head of the
    /// value back into view, not leave the window parked at the tail.
    /// </summary>
    [Fact]
    public void MovingTheCaretBack_ScrollsTheWindowBackToTheHead()
    {
        var state = new TextInputState { Text = "0123456789ABCDEF" };
        state.Activate();
        state.CursorPos = 0;

        var (buffer, viewport) = Paint(state, width: 10);

        RowText(buffer, 10).ShouldBe("0123456789");
        viewport.Caret!.Value.Column.ShouldBe(0);
    }

    // ---- Focus and selection, carried by colour alone ----

    /// <summary>
    /// A terminal cannot spare a row and a column for the 1px border the pixel renderer draws, so focus is
    /// carried by the background. If the two states resolved to the same colour a TUI user would have no way
    /// at all to tell which field has the keyboard.
    /// </summary>
    [Fact]
    public void FocusIsVisibleWithoutABorder_ThroughTheBackgroundAlone()
    {
        var focused = new TextInputState { Text = "abc" };
        focused.Activate();

        var (idleBuffer, _) = Paint(new TextInputState { Text = "abc" }, width: 8);
        var (focusedBuffer, _) = Paint(focused, width: 8);

        idleBuffer.BackAt(0, 0).Style.Background.ShouldBe(Palette.Background);
        focusedBuffer.BackAt(0, 0).Style.Background.ShouldBe(Palette.BackgroundActive);
        Palette.BackgroundActive.ShouldNotBe(Palette.Background,
            "if these were equal the assertions above would pass while saying nothing");
    }

    /// <summary>
    /// Compares the colour a cell RECORDS, which is necessarily opaque: a terminal cell does not composite,
    /// so the default translucent selection (alpha 180, meant to blend over the field behind it) lands as its
    /// plain RGB. Asserting the whole struct would be asserting that a terminal can do alpha.
    /// </summary>
    private static void ShouldBeSameHue(RGBAColor32 actual, RGBAColor32 expected, string because)
    {
        (actual.Red, actual.Green, actual.Blue)
            .ShouldBe((expected.Red, expected.Green, expected.Blue), because);
    }

    [Fact]
    public void ASelection_RestatesTheBackgroundOfExactlyTheSelectedCells()
    {
        var state = new TextInputState { Text = "abcdef" };
        state.Activate();
        state.SelectionAnchor = 1;
        state.CursorPos = 4;

        var (buffer, _) = Paint(state, width: 8);

        ShouldBeSameHue(buffer.BackAt(0, 0).Style.Background, Palette.BackgroundActive, "'a' is outside the selection");
        ShouldBeSameHue(buffer.BackAt(1, 0).Style.Background, Palette.Selection, "'b' opens the selection");
        ShouldBeSameHue(buffer.BackAt(3, 0).Style.Background, Palette.Selection, "'d' closes it");
        ShouldBeSameHue(buffer.BackAt(4, 0).Style.Background, Palette.BackgroundActive, "the range is half-open");
        RowText(buffer, 8).ShouldBe("abcdef  ", "highlighting must not disturb the glyphs");
    }

    /// <summary>
    /// A per-field palette overrides the shared static, the same escape hatch the pixel renderer takes for a
    /// field inlaid somewhere too small for the default scheme.
    /// </summary>
    [Fact]
    public void APerFieldPalette_OverridesTheSharedOne()
    {
        var mine = new TextInputColors { Background = new RGBAColor32(0x10, 0x20, 0x30, 0xff) };
        var buffer = new CellBuffer { ColorMode = ColorMode.TrueColor };
        buffer.Resize(8, 1);
        var viewport = new CaretRecordingViewport(buffer, 8, 1);

        var tree = Layout.Builder.VStack(
            Layout.Builder.TextInput(new TextInputState { Text = "x" }, 1f, colors: mine).RowH(1).WStar());
        CellLayout.Paint(viewport,
            Layout.Engine.Arrange(tree, new Rect<int>(0, 0, 8, 1), CellMeasureContext.CellAuthored));

        buffer.BackAt(0, 0).Style.Background.ShouldBe(mine.Background);
    }

    // ---- Hit testing ----

    /// <summary>
    /// The arranged rect IS the hit region on this surface too, so a click focuses the field with no
    /// per-field wiring -- the cell counterpart of the pixel painter's auto-registration.
    /// </summary>
    [Fact]
    public void ClickingAField_HitsIt()
    {
        var state = new TextInputState { Text = "abc" };
        var tree = Layout.Builder.VStack(Layout.Builder.TextInput(state, 1f).RowH(1).WStar());
        var arranged = Layout.Engine.Arrange(tree, new Rect<int>(0, 0, 8, 1), CellMeasureContext.CellAuthored);

        CellLayout.HitTest(arranged, 3, 0).ShouldBeOfType<HitResult.TextInputHit>().Input.ShouldBeSameAs(state);
    }

    /// <summary>
    /// The dump names the focused field, because "which box has the keyboard" is the question a text-input
    /// bug starts from and it is otherwise invisible in a text dump.
    /// </summary>
    [Fact]
    public void TheLayoutDump_NamesTheFocusedField()
    {
        var idle = new TextInputState { Text = "idle" };
        var busy = new TextInputState { Text = "busy" };
        busy.Activate();

        var tree = Layout.Builder.VStack(
            Layout.Builder.TextInput(idle, 1f).RowH(1).WStar(),
            Layout.Builder.TextInput(busy, 1f).RowH(1).WStar());

        var dump = CellLayout.Describe(
            Layout.Engine.Arrange(tree, new Rect<int>(0, 0, 12, 2), CellMeasureContext.CellAuthored));

        dump.ShouldContain("TextInput(\"idle\")");
        dump.ShouldContain("TextInput(\"busy\", active)");
    }
}
