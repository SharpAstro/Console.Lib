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
    [InlineData(Layout.IconKind.CaretUp, '\u25B2')]
    [InlineData(Layout.IconKind.CaretDown, '\u25BC')]
    [InlineData(Layout.IconKind.Plus, '+')]
    [InlineData(Layout.IconKind.Minus, '\u2212')]
    [InlineData(Layout.IconKind.ThemeLight, '\u25CB')]
    [InlineData(Layout.IconKind.ThemeSystem, '\u25D0')]
    [InlineData(Layout.IconKind.ThemeDark, '\u25CF')]
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
    /// EVERY kind has a glyph -- the assertion the theory above cannot make, because a theory only covers the
    /// rows someone remembered to write.
    /// <para>
    /// This is not hypothetical. <see cref="Layout.IconKind.CaretUp"/> and <see cref="Layout.IconKind.CaretDown"/>
    /// were added upstream and went unmapped here for four minor versions, rendering as the <c>?</c>
    /// placeholder on every terminal. Nothing failed: the fallback exists precisely so a forgotten kind
    /// degrades instead of throwing, which also makes it silent. Enumerating the enum is what turns "the
    /// next person remembers" into something the build says out loud, and the cost of a new kind -- a
    /// drawing there, a glyph here -- is the deal the icon vocabulary is documented as making.
    /// </para>
    /// </summary>
    [Fact]
    public void EveryIconKindHasAGlyph_SoNoneFallsBackToThePlaceholder()
    {
        var unmapped = Enum.GetValues<Layout.IconKind>()
            .Where(kind => GlyphOf(kind) == '?')
            .ToArray();

        unmapped.ShouldBeEmpty($"these kinds have no cell glyph: {string.Join(", ", unmapped)}");
    }

    /// <summary>Paints one icon into a 3x1 grid and reads back the glyph it became.</summary>
    private static char GlyphOf(Layout.IconKind kind)
    {
        var buffer = new CellBuffer { ColorMode = ColorMode.Sgr16 };
        buffer.Resize(3, 1);
        var viewport = new CellBufferViewport(buffer, 3, 1);

        var tree = new Layout.Node.Stack([Layout.Builder.Icon(kind, 1f).WStar().HStar().RowH(1)]);
        CellLayout.Paint(viewport, Layout.Engine.Arrange(
            tree, new Rect<int>(0, 0, 3, 1), CellMeasureContext.CellAuthored));

        return buffer.BackAt(1, 0).Glyph;
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
