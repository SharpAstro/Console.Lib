using DIR.Lib;
using Shouldly;
using Xunit;

namespace Console.Lib.Tests;

/// <summary>
/// Which end an overlong run loses. A cell surface measures in whole characters, so it always had to cut
/// somewhere — what it lacked was any way for the run to say WHERE.
///
/// <para>
/// It was unconditionally end-trimmed, which is right for a label and actively useless for a path: every
/// path on a machine shares its head, so <c>C:\Users\seb\repos\so…</c> identifies nothing, while
/// <c>…\ftw\Program.cs</c> is the part being read. The workaround was to pre-truncate against the column
/// width — and a row's own width is precisely the thing the layout engine took over, so after rows became
/// layout trees (4.10) the workaround stopped being available and the path column simply lost its filename.
/// </para>
///
/// <para>
/// Asserted through <see cref="CellBuffer.FrontRowText"/>: the plain-text view of what actually reached the
/// screen, so these read as the row a user would see rather than as an internal call.
/// </para>
/// </summary>
public class CellLayoutTrimTests
{
    private const string Path = @"C:\Users\seb\repos\ftw\Program.cs";

    private sealed class BufferedViewport(CellBuffer buffer, int width, int height) : ITerminalViewport
    {
        public (int Column, int Row) Offset => (0, 0);
        public (int Width, int Height) Size => (width, height);
        public TermCell CellSize => new(10, 20);
        public ColorMode ColorMode => Console.Lib.ColorMode.Sgr16;

        public void SetCursorPosition(int left, int top) => buffer.MoveTo(left, top);
        public void Write(string text) => buffer.Write(text);
        public void WriteLine(string? text = null) { }
        public void Flush() { }
        public Stream OutputStream => Stream.Null;
    }

    private sealed class NullSink : ICellSink
    {
        public void MoveTo(int column, int row) { }
        public void SetPen(VtStyle style, bool reverse) { }
        public void SetLink(string? url) { }
        public void Write(ReadOnlySpan<char> run) { }
    }

    /// <summary>Paints one run into a width-wide row and returns the row as text.</summary>
    private static string Painted(string text, int width, TextTrim trim)
    {
        var buffer = new CellBuffer { ColorMode = ColorMode.Sgr16 };
        buffer.Resize(width, 1);

        var tree = Layout.Builder.Text(text, 1f, trim: trim).WStar().HStar();
        var arranged = Layout.Engine.Arrange(tree, new Rect<int>(0, 0, width, 1), new CellMeasureContext());
        CellLayout.Paint(new BufferedViewport(buffer, width, 1), arranged);
        buffer.Flush(new NullSink());

        return buffer.FrontRowText(0);
    }

    /// <summary>The default, and what every run got before Trim existed.</summary>
    [Fact]
    public void EndTrim_KeepsTheHead()
        => Painted(Path, 12, TextTrim.End).ShouldBe(@"C:\Users\se…");

    /// <summary>The case that motivated the whole thing.</summary>
    [Fact]
    public void StartTrim_KeepsTheTail()
        => Painted(Path, 12, TextTrim.Start).ShouldBe(@"…\Program.cs");

    [Theory]
    [InlineData(TextTrim.End)]
    [InlineData(TextTrim.Start)]
    public void ARunThatFits_IsUntouched(TextTrim trim)
        => Painted("short", 10, trim).ShouldBe("short     ");

    /// <summary>
    /// One cell has no room for a glyph AND an ellipsis, so it goes to the surviving end's character: a lone
    /// "…" says strictly less than one real character does.
    /// </summary>
    [Theory]
    [InlineData(TextTrim.End, "a")]
    [InlineData(TextTrim.Start, "e")]
    public void AtOneCell_TheGlyphWinsOverTheEllipsis(TextTrim trim, string expected)
        => Painted("abcde", 1, trim).ShouldBe(expected);

    /// <summary>Two cells is the narrowest width that can carry an ellipsis at all.</summary>
    [Theory]
    [InlineData(TextTrim.End, "a…")]
    [InlineData(TextTrim.Start, "…e")]
    public void AtTwoCells_TheEllipsisAppears(TextTrim trim, string expected)
        => Painted("abcde", 2, trim).ShouldBe(expected);

    /// <summary>Whichever end is trimmed, the run still occupies exactly the cells it was given.</summary>
    [Theory]
    [InlineData(TextTrim.End)]
    [InlineData(TextTrim.Start)]
    public void ATrimmedRun_FillsItsRectExactly(TextTrim trim)
    {
        for (var width = 1; width <= 20; width++)
        {
            Painted(Path, width, trim).Length.ShouldBe(width, $"width {width}");
        }
    }
}
