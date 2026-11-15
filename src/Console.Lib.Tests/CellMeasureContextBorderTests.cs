using System.Collections.Immutable;
using Console.Lib;
using DIR.Lib;
using Shouldly;
using Xunit;

namespace Console.Lib.Tests;

/// <summary>
/// A border is one cell a side whatever the node holds (Console.Lib #82): the extent hook used to read a
/// border's design units as a content extent and inset the content by its line count.
/// </summary>
public class CellMeasureContextBorderTests
{
    private static readonly RGBAColor32 Fill = new(0x20, 0x40, 0x60, 0xff);
    private static readonly RGBAColor32 Rule = new(0xff, 0x00, 0x00, 0xff);

    private static (ImmutableArray<Layout.ArrangedNode<int>> Arranged, Layout.Node Box, Layout.Node[] Lines) ArrangeBordered(
        int lineCount, Func<Layout.Node, Layout.Node> border)
    {
        var lines = Enumerable.Range(0, lineCount).Select(i => Layout.Builder.Text($"line {i}", 1f)).ToArray();
        var box = border(Layout.Builder.VStack(lines).Bg(Fill));
        var root = new Layout.Node.Stack([box]);
        var arranged = Layout.Engine.Arrange(root, new Rect<int>(0, 0, 40, 30), CellMeasureContext.PixelAuthored);
        return (arranged, box, lines);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(8)]
    public void PixelAuthored_BorderedBlock_IsLinesPlusTwoRowsAndWidthPlusTwoColumns(int lineCount)
    {
        var (arranged, box, lines) = ArrangeBordered(lineCount, n => n.Border(Rule, 1f, 6f));

        var outer = arranged.Single(a => ReferenceEquals(a.Node, box)).Bounds;
        var first = arranged.Single(a => ReferenceEquals(a.Node, lines[0])).Bounds;
        outer.Height.ShouldBe(lineCount + 2);
        first.Y.ShouldBe(outer.Y + 1);
        first.X.ShouldBe(outer.X + 1);
        outer.Width.ShouldBe(first.Width + 2);
    }

    [Fact]
    public void PixelAuthored_BorderBottomOnFourLines_PutsTheRuleOnTheRowRightAfterTheText()
    {
        var (arranged, box, lines) = ArrangeBordered(4, n => n.BorderBottom(Rule, 1f));

        var outer = arranged.Single(a => ReferenceEquals(a.Node, box)).Bounds;
        var last = arranged.Single(a => ReferenceEquals(a.Node, lines[3])).Bounds;
        outer.Height.ShouldBe(5);
        last.Y.ShouldBe(outer.Y + 3);
        (outer.Y + outer.Height - 1).ShouldBe(last.Y + 1, "the rule is the row after the last text row");
    }
}
