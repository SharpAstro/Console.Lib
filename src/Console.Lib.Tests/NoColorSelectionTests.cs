using Console.Lib;
using DIR.Lib;
using Shouldly;
using Xunit;

namespace Console.Lib.Tests;

/// <summary>
/// Without colour the selection is reverse video. A row states its own selection through its pens, and
/// <see cref="ColorMode.None"/> (NO_COLOR, or output that is not a terminal) suppresses every pen, so a selected
/// row was indistinguishable from the rest: the TianWen planner list, 2026-09-19, where a click looked as if it
/// selected nothing. Reverse video is an attribute, not a colour, so NO_COLOR leaves it alone. With colour the
/// rows paint exactly as before, which the colour-mode cases pin.
/// </summary>
public sealed class NoColorSelectionTests
{
    private const int Width = 20;
    private const int Height = 6;

    private readonly struct Row(int index) : IRowLayout
    {
        public Layout.Node BuildRow(in RowContext context) => Layout.Builder.Text($"row {index}", 1f).WStar();
    }

    private sealed class Node(string label) : ITreeNode<Node>
    {
        public List<Node> Kids { get; } = [];
        public IReadOnlyList<Node> Children => Kids;

        public Layout.Node BuildNodeContent(in RowContext context) => Layout.Builder.Text(label, 1f).WStar().HStar();
    }

    private static CellBuffer ListWithCursorOn(int cursor, ColorMode mode)
    {
        var buffer = new CellBuffer { ColorMode = mode };
        buffer.Resize(Width, Height);
        var list = new ScrollableList<Row>(new CellBufferViewport(buffer, Width, Height, mode));
        list.Items(Enumerable.Range(0, 4).Select(i => new Row(i)).ToList());
        list.MoveTo(cursor);
        list.Render();
        return buffer;
    }

    private static bool RowIsReversed(CellBuffer buffer, int row, int columns = 5)
        => Enumerable.Range(0, columns).All(column => buffer.BackAt(column, row).Reverse);

    private static bool AnyCellReversed(CellBuffer buffer)
        => Enumerable.Range(0, Height).Any(row => Enumerable.Range(0, Width).Any(column => buffer.BackAt(column, row).Reverse));

    [Fact]
    public void WithoutColourTheCursorRowIsReverseVideoAndNoOtherIs()
    {
        var buffer = ListWithCursorOn(2, ColorMode.None);

        buffer.FrontRowText(0).ShouldNotBeNull();
        RowIsReversed(buffer, 2).ShouldBeTrue("the cursor row, all of its text");
        RowIsReversed(buffer, 0).ShouldBeFalse();
        RowIsReversed(buffer, 1).ShouldBeFalse();
        RowIsReversed(buffer, 3).ShouldBeFalse();
    }

    [Fact]
    public void TheReversedRowFollowsTheCursor()
    {
        RowIsReversed(ListWithCursorOn(0, ColorMode.None), 0).ShouldBeTrue();
        RowIsReversed(ListWithCursorOn(3, ColorMode.None), 3).ShouldBeTrue();
        RowIsReversed(ListWithCursorOn(3, ColorMode.None), 0).ShouldBeFalse();
    }

    [Theory]
    [InlineData(ColorMode.Sgr16)]
    [InlineData(ColorMode.TrueColor)]
    public void WithColourNothingIsReversed(ColorMode mode)
        => AnyCellReversed(ListWithCursorOn(2, mode)).ShouldBeFalse("the row states its own selection in colour");

    [Fact]
    public void WithoutColourTheTreesSelectedRowIsReverseVideoFromTheTwirlOn()
    {
        var root = new Node("root");
        root.Kids.Add(new Node("child one"));
        root.Kids.Add(new Node("child two"));
        var buffer = new CellBuffer { ColorMode = ColorMode.None };
        buffer.Resize(Width, Height);

        new TreeView<Node>(new CellBufferViewport(buffer, Width, Height, ColorMode.None))
            .Root(root, expandRoot: true)
            .Render();

        // The cursor starts on the root, at depth zero: the twirl, its space and the label are all reversed.
        RowIsReversed(buffer, 0, columns: 6).ShouldBeTrue("the selected row, twirl and label");
        RowIsReversed(buffer, 1, columns: 6).ShouldBeFalse();
    }
}
