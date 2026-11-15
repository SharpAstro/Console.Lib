using System.Collections.Immutable;
using Console.Lib;
using DIR.Lib;
using Shouldly;
using Xunit;

namespace Console.Lib.Tests;

/// <summary>
/// <see cref="CellMeasureContext.PixelAuthored"/> keeps one row for one line of content whatever pixel height
/// or padding the tree was authored with (Console.Lib #60), through DIR.Lib's extent hook. CellAuthored,
/// where a design unit is a cell, keeps its plain rounding.
/// </summary>
public class CellMeasureContextTests
{
    private static readonly RGBAColor32 Fill = new(0x20, 0x40, 0x60, 0xff);

    private static Layout.Node Text12() => new Layout.Node.Leaf(new Layout.Content.Text("Hello", 12f));

    private static ImmutableArray<Layout.ArrangedNode<int>> Arrange(Layout.Node root, Layout.IMeasureContext<int> ctx, int w = 40, int h = 20)
        => Layout.Engine.Arrange(root, new Rect<int>(0, 0, w, h), ctx);

    private static ImmutableArray<Layout.Node> Kids(params Layout.Node[] nodes) => [.. nodes];

    [Fact]
    public void PixelAuthored_FixedRowOverOneLineOfText_IsOneRow()
    {
        var row = Text12() with { Height = Layout.Sizing.Fixed(28), Width = Layout.Sizing.Star() };
        var arranged = Arrange(new Layout.Node.Stack(Kids(row)), CellMeasureContext.PixelAuthored);

        arranged.Single(a => ReferenceEquals(a.Node, row)).Bounds.Height.ShouldBe(1);
    }

    [Fact]
    public void PixelAuthored_PaddedButton_IsOneRowWithOneCellEachSide()
    {
        var label = Text12() with { Width = Layout.Sizing.Star(), Height = Layout.Sizing.Star() };
        var button = new Layout.Node.Stack(Kids(label))
        {
            Height = Layout.Sizing.Fixed(28),
            Width = Layout.Sizing.Fixed(80),
            Padding = 6,
        };
        var arranged = Arrange(new Layout.Node.Stack(Kids(button)), CellMeasureContext.PixelAuthored);

        var outer = arranged.Single(a => ReferenceEquals(a.Node, button)).Bounds;
        var inner = arranged.Single(a => ReferenceEquals(a.Node, label)).Bounds;
        outer.Height.ShouldBe(1);
        outer.Width.ShouldBe(10);
        inner.X.ShouldBe(outer.X + 1);
        inner.Width.ShouldBe(outer.Width - 2);
        inner.Height.ShouldBe(1);
    }

    [Fact]
    public void PixelAuthored_EmptySpacerUnderACell_AddsNoRow()
    {
        var spacer = new Layout.Node.Stack([]) { Height = Layout.Sizing.Fixed(6), Width = Layout.Sizing.Star() };
        var a = Text12() with { Height = Layout.Sizing.Fixed(16), Width = Layout.Sizing.Star() };
        var b = Text12() with { Height = Layout.Sizing.Fixed(16), Width = Layout.Sizing.Star() };
        var arranged = Arrange(new Layout.Node.Stack(Kids(a, spacer, b)), CellMeasureContext.PixelAuthored);

        arranged.Single(x => ReferenceEquals(x.Node, spacer)).Bounds.Height.ShouldBe(0);
        arranged.Single(x => ReferenceEquals(x.Node, b)).Bounds.Y.ShouldBe(1);
    }

    [Fact]
    public void PixelAuthored_FilledNodeThinnerThanACell_KeepsOneRow()
    {
        var bar = new Layout.Node.Stack([]) { Background = Fill, Width = Layout.Sizing.Star(), Height = Layout.Sizing.Star() };
        var row = new Layout.Node.Stack(Kids(bar)) { Height = Layout.Sizing.Fixed(8), Width = Layout.Sizing.Star() };
        var arranged = Arrange(new Layout.Node.Stack(Kids(row)), CellMeasureContext.PixelAuthored);

        arranged.Single(a => ReferenceEquals(a.Node, row)).Bounds.Height.ShouldBe(1);
    }

    [Fact]
    public void CellAuthored_KeepsPlainRounding()
    {
        var row = Text12() with { Height = Layout.Sizing.Fixed(3), Width = Layout.Sizing.Star() };
        var spacer = new Layout.Node.Stack([]) { Height = Layout.Sizing.Fixed(2), Width = Layout.Sizing.Star() };
        var arranged = Arrange(new Layout.Node.Stack(Kids(row, spacer)), CellMeasureContext.CellAuthored);

        arranged.Single(a => ReferenceEquals(a.Node, row)).Bounds.Height.ShouldBe(3);
        arranged.Single(a => ReferenceEquals(a.Node, spacer)).Bounds.Height.ShouldBe(2);
    }
}
