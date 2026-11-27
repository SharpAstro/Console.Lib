using DIR.Lib;
using Shouldly;
using Xunit;

namespace Console.Lib.Tests;

/// <summary>
/// DIR.Lib's <c>Content.Text.Weight</c> on the cell surface: Bold is drawn in SGR bold and modelled by
/// <see cref="CellBuffer"/> as a pen attribute; <c>Face</c> is ignored (a terminal has one face). In memory only.
/// </summary>
public class CellLayoutWeightTests
{
    private sealed class BufferedViewport(CellBuffer buffer, int width, int height, ColorMode mode) : ITerminalViewport
    {
        public (int Column, int Row) Offset => (0, 0);
        public (int Width, int Height) Size => (width, height);
        public TermCell CellSize => new(10, 20);
        public ColorMode ColorMode => mode;

        public void SetCursorPosition(int left, int top) => buffer.MoveTo(left, top);
        public void Write(string text) => buffer.Write(text);
        public void WriteLine(string? text = null) { }
        public void Flush() { }
        public Stream OutputStream => Stream.Null;
    }

    private static readonly RGBAColor32 Ink = new(0xdd, 0xdd, 0xdd, 0xff);

    private static CellBuffer Paint(Layout.Node[] leaves, ColorMode mode = ColorMode.Sgr16, int width = 20)
    {
        var buffer = new CellBuffer { ColorMode = mode };
        buffer.Resize(width, 1);
        var tree = new Layout.Node.Stack([Layout.Builder.HStack(leaves).RowH(1)]);
        var arranged = Layout.Engine.Arrange(tree, new Rect<int>(0, 0, width, 1), new CellMeasureContext());
        CellLayout.Paint(new BufferedViewport(buffer, width, 1, mode), arranged);
        return buffer;
    }

    private static Layout.Node Leaf(string s) => Layout.Builder.Text(s, 1f, Ink).WStar().HStar();

    [Fact]
    public void ABoldLeaf_CarriesBold_AndARegularSiblingDoesNot()
    {
        var buffer = Paint([Leaf("Head").Bold(), Leaf("Body")], width: 8);

        for (var c = 0; c < 4; c++) buffer.BackAt(c, 0).Bold.ShouldBeTrue($"column {c} is in the bold leaf");
        for (var c = 4; c < 8; c++) buffer.BackAt(c, 0).Bold.ShouldBeFalse($"column {c} is in the regular leaf");
        buffer.BackAt(0, 0).Kind.ShouldBe(CellKind.Text, "bold is modelled, not an unparseable pen");
    }

    [Fact]
    public void ABoldLeafFollowedByARegularOne_DoesNotLeakBold()
    {
        var buffer = Paint([Leaf("AB").Bold(), Leaf("CD"), Leaf("EF").Bold()], width: 6);

        buffer.BackAt(1, 0).Bold.ShouldBeTrue();
        buffer.BackAt(2, 0).Bold.ShouldBeFalse();
        buffer.BackAt(3, 0).Bold.ShouldBeFalse();
        buffer.BackAt(4, 0).Bold.ShouldBeTrue();
    }

    [Fact]
    public void MonoPaintsTheSameCellsAsUi()
    {
        var ui = Paint([Leaf("Same"), Leaf("Bold").Bold()], width: 8);
        var mono = Paint([Leaf("Same").Mono(), Leaf("Bold").Bold().Mono()], width: 8);

        for (var c = 0; c < 8; c++) mono.BackAt(c, 0).ShouldBe(ui.BackAt(c, 0), $"column {c}");
    }

    [Fact]
    public void ColorModeNone_WritesNoBoldEscape()
    {
        var buffer = Paint([Leaf("Head").Bold()], ColorMode.None, width: 4);

        for (var c = 0; c < 4; c++) buffer.BackAt(c, 0).Bold.ShouldBeFalse();
        buffer.BackAt(0, 0).Glyph.ShouldBe('H');
    }
}
