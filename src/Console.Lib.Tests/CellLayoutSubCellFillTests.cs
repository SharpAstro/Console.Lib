using DIR.Lib;
using Shouldly;
using Xunit;

namespace Console.Lib.Tests;

/// <summary>
/// A fill thinner or narrower than a cell is painted with block glyphs rather than dropped or snapped to a
/// whole cell. Only a pixel-authored arrangement has such fills (a cell-authored design unit IS a cell).
/// </summary>
public class CellLayoutSubCellFillTests
{
    private static readonly RGBAColor32 Track = new(0x20, 0x20, 0x30, 0xff);
    private static readonly RGBAColor32 FillColor = new(0x30, 0xa0, 0x50, 0xff);

    private static CellBuffer Paint(Layout.Node tree, int width, int height)
    {
        var buffer = new CellBuffer { ColorMode = ColorMode.TrueColor };
        buffer.Resize(width, height);
        var viewport = new CellBufferViewport(buffer, width, height, ColorMode.TrueColor);
        var arranged = Layout.Engine.Arrange(tree, new Rect<int>(0, 0, width, height), CellMeasureContext.PixelAuthored);
        CellLayout.Paint(viewport, arranged);
        return buffer;
    }

    [Fact]
    public void AMeterFill_ShowsItsFractionToAnEighthOfACell()
    {
        // 15 columns at 8 design units a column; 0.37 of it is 5.55 cells: 5 whole, then 4/8.
        var meter = Layout.Builder.HStack(
                Layout.Builder.Spacer().WStar(0.37f).HStar().Bg(FillColor),
                Layout.Builder.Spacer().WStar(0.63f).HStar())
            .RowH(8)
            .WFixed(120)
            .Bg(Track);

        var buffer = Paint(new Layout.Node.Stack([meter]), 15, 1);

        for (var x = 0; x < 5; x++)
        {
            buffer.BackAt(x, 0).Style.Background.ShouldBe(FillColor, $"column {x} is inside the fill");
        }

        var edge = buffer.BackAt(5, 0);
        edge.Glyph.ShouldBe('▌');
        edge.Style.Foreground.ShouldBe(FillColor);
        edge.Style.Background.ShouldBe(Track);

        for (var x = 6; x < 15; x++)
        {
            buffer.BackAt(x, 0).Style.Background.ShouldBe(Track, $"column {x} is track");
        }
    }

    [Fact]
    public void ThreeEqualFilledStarColumns_TileTheSpanWithNoSliverAtTheRightEdge()
    {
        var a = new RGBAColor32(0xc0, 0x30, 0x30, 0xff);
        var b = new RGBAColor32(0x30, 0xc0, 0x30, 0xff);
        var c = new RGBAColor32(0x30, 0x30, 0xc0, 0xff);
        var page = new RGBAColor32(0x05, 0x05, 0x05, 0xff);
        var row = Layout.Builder.HStack(
                Layout.Builder.Spacer().WStar().HStar().Bg(a),
                Layout.Builder.Spacer().WStar().HStar().Bg(b),
                Layout.Builder.Spacer().WStar().HStar().Bg(c))
            .RowH(32)
            .Bg(page);

        var buffer = Paint(new Layout.Node.Stack([row]), 40, 1);

        for (var x = 0; x < 13; x++)
        {
            buffer.BackAt(x, 0).Style.Background.ShouldBe(a, $"column {x}");
        }

        var ab = buffer.BackAt(13, 0);
        ab.Style.Foreground.ShouldBe(a);
        ab.Style.Background.ShouldBe(b);
        for (var x = 14; x < 26; x++)
        {
            buffer.BackAt(x, 0).Style.Background.ShouldBe(b, $"column {x}");
        }

        var bc = buffer.BackAt(26, 0);
        bc.Style.Foreground.ShouldBe(b);
        bc.Style.Background.ShouldBe(c);
        for (var x = 27; x < 40; x++)
        {
            var cell = buffer.BackAt(x, 0);
            cell.Glyph.ShouldBe(' ', $"column {x} is a solid cell, not a glyph");
            cell.Style.Background.ShouldBe(c, $"column {x}");
        }
    }

    [Fact]
    public void AMeterWithAFilledTrack_ShowsTheBoundaryAsOneGlyphAndNoTrailingGlyph()
    {
        var page = new RGBAColor32(0x05, 0x05, 0x05, 0xff);
        var meter = Layout.Builder.HStack(
                Layout.Builder.Spacer().WStar(0.37f).HStar().Bg(FillColor),
                Layout.Builder.Spacer().WStar(0.63f).HStar().Bg(Track))
            .RowH(8)
            .WFixed(120)
            .Bg(page);

        var buffer = Paint(new Layout.Node.Stack([meter]), 20, 1);

        for (var x = 0; x < 5; x++)
        {
            buffer.BackAt(x, 0).Style.Background.ShouldBe(FillColor, $"column {x}");
        }

        var edge = buffer.BackAt(5, 0);
        edge.Glyph.ShouldBe('▌');
        edge.Style.Foreground.ShouldBe(FillColor);
        edge.Style.Background.ShouldBe(Track);

        for (var x = 6; x < 15; x++)
        {
            var cell = buffer.BackAt(x, 0);
            cell.Glyph.ShouldBe(' ', $"column {x} is a solid track cell");
            cell.Style.Background.ShouldBe(Track, $"column {x}");
        }

        buffer.BackAt(15, 0).Style.Background.ShouldNotBe(Track);
    }

    [Fact]
    public void AFillUnderHalfARow_PaintsHalfBlocks()
    {
        var bar = Layout.Builder.Spacer().WStar().HStar().Bg(FillColor);
        var row = new Layout.Node.Stack([bar]) { Width = Layout.Sizing.Star(), Height = Layout.Sizing.Fixed(4), Background = Track };

        var buffer = Paint(new Layout.Node.Stack([row]), 6, 1);

        for (var x = 0; x < 6; x++)
        {
            var cell = buffer.BackAt(x, 0);
            cell.Glyph.ShouldBe('▀');
            cell.Style.Foreground.ShouldBe(FillColor);
            cell.Style.Background.ShouldBe(Track);
        }
    }
}
