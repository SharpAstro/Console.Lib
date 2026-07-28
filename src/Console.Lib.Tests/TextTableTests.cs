using Console.Lib;
using Shouldly;
using Xunit;

namespace Console.Lib.Tests;

/// <summary>
/// Pins <see cref="TextTable"/>, the table renderer lifted out of the Markdown renderer so that
/// anything drawing a terminal table shares one implementation of the border junctions.
/// </summary>
public class TextTableTests
{
    private static List<string> Render(
        BorderStyle style = BorderStyle.Light,
        IReadOnlyList<CellAlignment>? alignments = null)
    {
        var output = new List<string>();
        TextTable.Render(
            ["Name", "Age"],
            [["Alice", "30"], ["Bob", "5"]],
            alignments ?? [],
            output,
            style);
        return output;
    }

    [Fact]
    public void TheShapeIsBorderHeaderSeparatorRowsBorder()
    {
        var lines = Render();

        lines.Count.ShouldBe(6);
        lines[0].ShouldBe("┌───────┬─────┐");
        lines[1].ShouldBe("│ Name  │ Age │");
        lines[2].ShouldBe("├───────┼─────┤");
        lines[3].ShouldBe("│ Alice │ 30  │");
        lines[4].ShouldBe("│ Bob   │ 5   │");
        lines[5].ShouldBe("└───────┴─────┘");
    }

    [Fact]
    public void EachEdgeGetsItsOwnJunction()
    {
        // The bug this guards: reusing one tee for all three edges. It looks fine on the top border
        // and wrong on the other two, so an eyeball check of a single table can miss it.
        var lines = Render();

        lines[0].ShouldContain("┬");
        lines[2].ShouldContain("┼");
        lines[5].ShouldContain("┴");
    }

    [Theory]
    [InlineData(BorderStyle.Light, '┌', '┬', '│')]
    [InlineData(BorderStyle.Heavy, '┏', '┳', '┃')]
    [InlineData(BorderStyle.Double, '╔', '╦', '║')]
    [InlineData(BorderStyle.Rounded, '╭', '┬', '│')]
    [InlineData(BorderStyle.Ascii, '+', '+', '|')]
    public void EveryStyleDrawsItsOwnFamily(BorderStyle style, char topLeft, char teeDown, char vertical)
    {
        var lines = Render(style);

        lines[0][0].ShouldBe(topLeft);
        lines[0].ShouldContain(teeDown);
        lines[1][0].ShouldBe(vertical);
    }

    [Fact]
    public void RoundedIsLightWithArcCorners()
    {
        // Unicode has arc forms for the corners only, so the tees and cross stay Light. Pinned because
        // the obvious "rounded" expectation is a full arc family that does not exist.
        var lines = Render(BorderStyle.Rounded);

        lines[0][0].ShouldBe('╭');
        lines[0][^1].ShouldBe('╮');
        lines[5][0].ShouldBe('╰');
        lines[5][^1].ShouldBe('╯');
        lines[2].ShouldContain("┼", Case.Sensitive, "no rounded cross exists in Unicode");
    }

    [Fact]
    public void AlignmentPositionsTheCellWithinItsColumn()
    {
        var output = new List<string>();
        TextTable.Render(
            ["Left", "Middle", "Right"],
            [["a", "b", "c"]],
            [CellAlignment.Left, CellAlignment.Center, CellAlignment.Right],
            output);

        output[3].ShouldBe("│ a    │   b    │     c │");
    }

    [Fact]
    public void ColumnWidthIgnoresEscapeSequences()
    {
        // The reason cells are measured rather than counted: a styled cell must not blow out its column.
        var styled = "\e[1mAlice\e[0m";
        var plain = new List<string>();
        var withSgr = new List<string>();

        TextTable.Render(["Name"], [["Alice"]], [], plain);
        TextTable.Render(["Name"], [[styled]], [], withSgr);

        withSgr[0].ShouldBe(plain[0], "the border width must not change when a cell is styled");
        withSgr[3].Replace("\e[1m", "").Replace("\e[0m", "").ShouldBe(plain[3]);
    }

    [Fact]
    public void ARowShorterThanTheHeaderIsPadded()
    {
        // Malformed Markdown reaches this path, so a short row must not throw or misalign the border.
        var output = new List<string>();

        TextTable.Render(["A", "B", "C"], [["only"]], [], output);

        output.Count.ShouldBe(5);
        output[3].ShouldBe("│ only │   │   │");
    }

    [Fact]
    public void NoColumnsRendersNothing()
    {
        var output = new List<string>();

        TextTable.Render([], [], [], output);

        output.ShouldBeEmpty();
    }

    [Fact]
    public void TheBorderColourWrapsOnlyTheBorder()
    {
        var output = new List<string>();

        TextTable.Render(["A"], [["b"]], [], output, BorderStyle.Light, "\e[2m", "\e[0m");

        output[0].ShouldBe("\e[2m┌───┐\e[0m");
        // The cell content sits outside the dim run, so a row is dim-pipe, content, dim-pipe.
        output[3].ShouldBe("\e[2m│\e[0m b \e[2m│\e[0m");
    }
}
