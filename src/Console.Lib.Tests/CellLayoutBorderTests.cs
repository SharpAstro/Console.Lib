using System.Text;
using Console.Lib;
using DIR.Lib;
using Shouldly;
using Xunit;

namespace Console.Lib.Tests;

/// <summary>
/// CellLayout paints a node's declared DIR.Lib border (<c>Node.BorderStyle</c>) on the cells the engine reserved:
/// light box glyphs, arc corners for a radius, a single side as a rule, in the border colour.
/// </summary>
public class CellLayoutBorderTests
{
    private static readonly RGBAColor32 Red = new(0xff, 0x00, 0x00, 0xff);
    private static readonly RGBAColor32 Navy = new(0x10, 0x10, 0x40, 0xff);
    private static readonly RGBAColor32 Page = new(0x20, 0x20, 0x20, 0xff);

    private sealed class CaptureViewport(int width, int height, ColorMode mode) : ITerminalViewport
    {
        public List<(int Col, int Row, string Text)> Writes { get; } = [];
        private int _col, _row;

        public (int Column, int Row) Offset => (0, 0);
        public (int Width, int Height) Size => (width, height);
        public TermCell CellSize => new(10, 20);
        public ColorMode ColorMode => mode;
        public void SetCursorPosition(int left, int top) { _col = left; _row = top; }
        public void Write(string text) => Writes.Add((_col, _row, text));
        public void WriteLine(string? text = null) { }
        public void Flush() { }
        public Stream OutputStream => Stream.Null;
    }

    /// <summary>The visible characters, one string per row, later writes over earlier ones.</summary>
    private static string[] Grid(CaptureViewport vp)
    {
        var rows = Enumerable.Range(0, vp.Size.Height).Select(_ => new StringBuilder(new string(' ', vp.Size.Width))).ToArray();
        foreach (var (col, row, text) in vp.Writes)
        {
            var plain = text.Replace(VtStyle.Reset, "");
            for (var i = 0; i < plain.Length && col + i < vp.Size.Width; i++)
            {
                rows[row][col + i] = plain[i];
            }
        }

        return [.. rows.Select(r => r.ToString())];
    }

    private static Layout.Node Label(string text) => new Layout.Node.Leaf(new Layout.Content.Text(text) { HAlign = TextAlign.Near })
    {
        Height = Layout.Sizing.Fixed(1),
        Width = Layout.Sizing.Star(),
    };

    private static CaptureViewport Paint(Layout.Node root, ColorMode mode = ColorMode.None, int width = 10, int height = 4)
    {
        var vp = new CaptureViewport(width, height, mode);
        var arranged = Layout.Engine.Arrange(root, new Rect<int>(0, 0, width, height), new CellMeasureContext());
        CellLayout.Paint(vp, arranged);
        return vp;
    }

    [Fact]
    public void BorderedNode_PaintsFrameOnOuterRing_AndContentInside()
    {
        var node = new Layout.Node.Stack([Label("Hi")]).Border(Red);

        var grid = Grid(Paint(node));

        grid[0].ShouldBe("┌────────┐");
        grid[1].ShouldBe("│Hi      │");
        grid[3].ShouldBe("└────────┘");
        grid[2][0].ShouldBe('│');
        grid[2][9].ShouldBe('│');
    }

    [Fact]
    public void BorderWithRadius_PaintsArcCorners()
    {
        var node = new Layout.Node.Stack([Label("Hi")]).Border(Red, 1f, 4f);

        var grid = Grid(Paint(node));

        grid[0].ShouldBe("╭────────╮");
        grid[3].ShouldBe("╰────────╯");
    }

    [Fact]
    public void RoundedBorderedFill_KeepsTheArcAndDoesNotClipItToQuadrants()
    {
        var node = new Layout.Node.Stack([Label("Hi")]) { Background = Navy, CornerRadius = 4f }.Border(Red, 1f, 4f);

        var grid = Grid(Paint(node));

        grid[0][0].ShouldBe('╭');
        grid[3][9].ShouldBe('╯');
    }

    [Fact]
    public void BorderTopOnly_PaintsAFullRowOfRules_AndNoCorners()
    {
        var node = new Layout.Node.Stack([Label("Hi")]).BorderTop(Red);

        var grid = Grid(Paint(node));

        grid[0].ShouldBe("──────────");
        grid[1].ShouldStartWith("Hi");
        grid[1].ShouldNotContain("│");
        grid[3].Trim().ShouldBeEmpty();
    }

    [Fact]
    public void BorderLeftOnly_PaintsAColumnOfVerticalRules()
    {
        var node = new Layout.Node.Stack([Label("Hi")]).BorderLeft(Red);

        var grid = Grid(Paint(node));

        grid.Select(r => r[0]).ShouldAllBe(c => c == '│');
        grid[0].ShouldNotContain("┌");
        grid[0].ShouldNotContain("─");
        grid[0][1].ShouldBe('H');
    }

    [Fact]
    public void BorderGlyphs_CarryBorderColourAsForeground_AndEnclosingFillAsBackground()
    {
        var node = new Layout.Node.Stack([Label("Hi")]).Border(Red);
        var root = new Layout.Node.Stack([node]) { Background = Page };

        var vp = Paint(root, ColorMode.TrueColor);

        var corner = vp.Writes.Last(w => w.Text.Contains('┌'));
        corner.Text.ShouldContain("38;2;255;0;0");
        corner.Text.ShouldContain("48;2;32;32;32");
    }

    [Fact]
    public void BorderGlyphs_OnANodeWithItsOwnFill_UseThatFillAsBackground()
    {
        var node = new Layout.Node.Stack([Label("Hi")]) { Background = Navy }.Border(Red);
        var root = new Layout.Node.Stack([node]) { Background = Page };

        var vp = Paint(root, ColorMode.TrueColor);

        var corner = vp.Writes.Last(w => w.Text.Contains('┌'));
        corner.Text.ShouldContain("38;2;255;0;0");
        corner.Text.ShouldContain("48;2;16;16;64");
    }

    [Fact]
    public void TransparentBorderColour_PaintsNothing()
    {
        var node = new Layout.Node.Stack([Label("Hi")]).Border(new RGBAColor32(0xff, 0, 0, 0));

        var grid = Grid(Paint(node));

        string.Concat(grid).Any(c => c is >= '─' and <= '╿').ShouldBeFalse();
        grid[1].ShouldBe(" Hi       ");
    }

    private static Layout.Node Box(Layout.Node n, float? w = null, float? h = null)
        => n with { Width = w is { } ww ? Layout.Sizing.Fixed(ww) : Layout.Sizing.Star(), Height = h is { } hh ? Layout.Sizing.Fixed(hh) : n.Height };

    [Fact]
    public void StackedItems_JoinIntoOneFrame_WithTeeSeparatorRows()
    {
        var first = Box(new Layout.Node.Stack([Label("a")]).Border(Red), h: 3);
        var second = Box(new Layout.Node.Stack([Label("b")]).BorderLeft(Red).BorderRight(Red).BorderBottom(Red), h: 2);
        var third = Box(new Layout.Node.Stack([Label("c")]).BorderLeft(Red).BorderRight(Red).BorderBottom(Red), h: 2);

        var grid = Grid(Paint(new Layout.Node.Stack([first, second, third]), height: 7));

        grid[0].ShouldBe("┌────────┐");
        grid[2].ShouldBe("├────────┤");
        grid[4].ShouldBe("├────────┤");
        grid[6].ShouldBe("└────────┘");
        grid[3][0].ShouldBe('│');
        string.Concat(grid[1..6]).ShouldNotContain("┌");
        string.Concat(grid[1..6]).ShouldNotContain("┘");
    }

    [Fact]
    public void SideBySide_JoinWithTeeDownAndTeeUp_WhereTheyMeet()
    {
        var left = Box(new Layout.Node.Stack([Label("a")]).Border(Red), w: 5);
        var right = Box(new Layout.Node.Stack([Label("b")]).BorderTop(Red).BorderRight(Red).BorderBottom(Red));

        var grid = Grid(Paint(Layout.Builder.HStack(left, right), height: 3));

        grid[0].ShouldBe("┌───┬────┐");
        grid[2].ShouldBe("└───┴────┘");
        grid[1][4].ShouldBe('│');
    }

    [Fact]
    public void WhereFourArmsMeet_TheCellIsACross()
    {
        // a: Right|Bottom, b: Bottom, c: Right -> the separator row meets the column divider at (4,1).
        var a = Box(new Layout.Node.Stack([Label("a")]).BorderRight(Red).BorderBottom(Red), w: 5, h: 2);
        var b = Box(new Layout.Node.Stack([Label("b")]).BorderBottom(Red), h: 2);
        var c = Box(new Layout.Node.Stack([Label("c")]).BorderRight(Red), w: 5, h: 2);
        var d = Box(new Layout.Node.Stack([Label("d")]), h: 2);

        var grid = Grid(Paint(Layout.Builder.VStack(Layout.Builder.HStack(a, b).WStar(), Layout.Builder.HStack(c, d).WStar()).WStar(), height: 4));

        grid[1].ShouldBe("────┼─────");
        grid[0][4].ShouldBe('│');
        grid[2][4].ShouldBe('│');
    }

    [Fact]
    public void RoundedCorner_StaysAnArc_UntilItBecomesAJunction()
    {
        var first = Box(new Layout.Node.Stack([Label("a")]).Border(Red, 1f, 4f), h: 3);
        var second = Box(new Layout.Node.Stack([Label("b")]).BorderLeft(Red).BorderRight(Red).BorderBottom(Red), h: 2);

        var grid = Grid(Paint(new Layout.Node.Stack([first, second]), height: 5));

        grid[0][0].ShouldBe('╭');
        grid[2][0].ShouldBe('├');
        grid[2][9].ShouldBe('┤');
    }
}
