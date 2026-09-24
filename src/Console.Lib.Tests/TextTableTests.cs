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

    // ── A width to fit ────────────────────────────────────────────────

    private static List<string> RenderAt(int maxWidth, IReadOnlyList<string> headers,
        IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var output = new List<string>();
        TextTable.Render(headers, rows, [], output, maxWidth);
        return output;
    }

    [Fact]
    public void ATableThatFitsIsUnchangedByTheWidth()
    {
        // 15 is this table's natural width exactly, so nothing may move.
        var bounded = new List<string>();
        TextTable.Render(["Name", "Age"], [["Alice", "30"], ["Bob", "5"]], [], bounded, maxWidth: 15);

        bounded.ShouldBe(Render());
    }

    [Fact]
    public void AnOverWideTableShrinksItsWidestColumnAndWrapsIt()
    {
        var lines = RenderAt(24, ["Key", "Value"], [["a", "one two three four five"], ["b", "six"]]);

        lines.ShouldBe([
            "┌─────┬────────────────┐",
            "│ Key │ Value          │",
            "├─────┼────────────────┤",
            "│ a   │ one two three  │",
            "│     │ four five      │",
            "├─────┼────────────────┤",
            "│ b   │ six            │",
            "└─────┴────────────────┘",
        ]);
    }

    [Fact]
    public void AWordTooLongForItsColumnBreaksAfterAHyphenFirst()
    {
        // An 11-cell column. Cut at the character it would read "printer-dri" / "ver-cups-pd" / "f".
        var lines = RenderAt(15, ["Package"], [["printer-driver-cups-pdf"]]);

        lines.ShouldAllBe(l => l.Length == 15);
        lines[3].ShouldBe("│ printer-    │");
        lines[4].ShouldBe("│ driver-     │");
        lines[5].ShouldBe("│ cups-pdf    │");
    }

    [Fact]
    public void ASegmentTooLongForItsColumnBreaksInsideIt()
    {
        var lines = RenderAt(9, ["H"], [["abcdefghij"]]);

        lines.ShouldAllBe(l => l.Length == 9);
        lines[3].ShouldBe("│ abcde │");
        lines[4].ShouldBe("│ fghij │");
    }

    [Fact]
    public void AWrappedCellClosesItsStyleAtTheBreakAndReopensIt()
    {
        // Left open, the bold would run on through the padding and into the next cell's border.
        const string bold = "\e[1m";
        const string reset = "\e[0m";

        var lines = RenderAt(13, ["H"], [[$"{bold}alpha beta{reset}"]]);

        lines[3].ShouldBe($"│ {bold}alpha{reset}     │");
        lines[4].ShouldBe($"│ {bold}beta{reset}      │");
    }

    [Fact]
    public void AWrappedLinkIsClosedAtTheBreakAndReopened()
    {
        // The padding and border after a fragment must not be clickable.
        const string open = "\e]8;;https://example.com/more\a";
        const string close = "\e]8;;\a";

        var lines = RenderAt(13, ["H"], [[$"{open}alpha beta{close}"]]);

        lines[3].ShouldBe($"│ {open}alpha{close}     │");
        lines[4].ShouldBe($"│ {open}beta{close}      │");
    }

    [Fact]
    public void BordersWiderThanTheWidthDrawOneCellPerColumn()
    {
        // Three columns need ten cells of border alone. Nothing fits, so draw the narrowest table there
        // is rather than throw.
        var lines = RenderAt(8, ["A", "B", "C"], [["xy", "z", ""]]);

        lines[0].ShouldBe("┌───┬───┬───┐");
        lines[3].ShouldBe("│ x │ z │   │");
        lines[4].ShouldBe("│ y │   │   │");
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
