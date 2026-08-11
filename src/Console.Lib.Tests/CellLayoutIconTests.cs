using DIR.Lib;
using Shouldly;
using Xunit;

namespace Console.Lib.Tests;

/// <summary>
/// The cell half of <see cref="Layout.Content.Icon"/>. DIR.Lib pins the pixel half by asserting the
/// rectangles it constructs; a character grid cannot draw those, so the same node becomes a glyph here.
/// Together these two files are the whole reason an icon names its MEANING rather than its drawing.
/// </summary>
public class CellLayoutIconTests
{
    /// <summary>
    /// The glyphs are deliberately drawn from the ranges a terminal font is relied on to carry (block
    /// elements, mathematical operators) -- the same well every border and tree marker here draws from. Spelt
    /// as escapes on both sides so this pins the codepoint, not whatever a copy-paste preserved.
    /// </summary>
    [Theory]
    [InlineData(Layout.IconKind.Grid, '\u259E')]
    [InlineData(Layout.IconKind.List, '\u2261')]
    [InlineData(Layout.IconKind.Auto, 'A')]
    public void AnIconBecomesItsGlyph_CentredInTheArrangedRect(Layout.IconKind kind, char expected)
    {
        var buffer = new CellBuffer { ColorMode = ColorMode.Sgr16 };
        buffer.Resize(5, 1);
        var viewport = new CellBufferViewport(buffer, 5, 1);

        // One cell wide is the real case (a toolbar button), but arrange it across five so the centring is
        // observable: the glyph lands in the middle column, not at the near edge like a text run would.
        var tree = new Layout.Node.Stack([Layout.Builder.Icon(kind, 1f).WStar().HStar().RowH(1)]);
        CellLayout.Paint(viewport, Layout.Engine.Arrange(
            tree, new Rect<int>(0, 0, 5, 1), CellMeasureContext.CellAuthored));

        buffer.BackAt(2, 0).Glyph.ShouldBe(expected);
        buffer.BackAt(2, 0).Kind.ShouldBe(CellKind.Text);
    }

    /// <summary>
    /// An icon states its ink like any other run, so it cannot inherit whatever SGR the previous write left
    /// behind -- the failure mode a cell buffer exists to make observable, since a glyph drawn in its own
    /// background is present in the dump and invisible on screen.
    /// </summary>
    [Fact]
    public void AnIconStatesItsOwnInk()
    {
        var buffer = new CellBuffer { ColorMode = ColorMode.Sgr16 };
        buffer.Resize(3, 1);
        var viewport = new CellBufferViewport(buffer, 3, 1);

        var ink = new RGBAColor32(0xff, 0x40, 0x40, 0xff);
        var tree = new Layout.Node.Stack(
            [Layout.Builder.Icon(Layout.IconKind.Grid, 1f, ink).WStar().HStar().RowH(1)]);
        CellLayout.Paint(viewport, Layout.Engine.Arrange(
            tree, new Rect<int>(0, 0, 3, 1), CellMeasureContext.CellAuthored));

        var cell = buffer.BackAt(1, 0);
        cell.Glyph.ShouldBe('\u259E');
        cell.Style.Foreground.Alpha.ShouldBe((byte)0xff, "the ink was asked for, so it is stated");
        cell.Style.Foreground.ShouldNotBe(cell.Style.Background);
    }
}
