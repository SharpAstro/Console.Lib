using DIR.Lib;
using Shouldly;
using Xunit;

namespace Console.Lib.Tests;

/// <summary>
/// Hyperlinks on the cell path: a node states one by carrying a <see cref="HitResult.LinkHit"/>, and
/// <see cref="CellLayout"/> paints its text inside an OSC 8 pair.
///
/// <para>
/// <b>Why the hit and not a new property.</b> A link is a region that points somewhere, which is what a
/// LinkHit already says — and reusing it makes the drawn region and the clickable region the same arranged
/// rect by construction. A separate <c>Layout.Node.Link</c> would be a second way to say the same thing,
/// with the standing possibility of a row that underlines text it cannot click or clicks text it does not
/// underline.
/// </para>
///
/// <para>
/// Asserted through a real <see cref="CellBuffer"/> rather than a recording viewport, for the reason
/// <see cref="CellLayoutPenTests"/> gives about colours: the escape STRING is not the thing that matters,
/// the cell is. A test that pinned the string would pass on a frame whose links never survive the diff.
/// </para>
/// </summary>
public class CellLayoutLinkTests
{
    private const string Target = "file:///c/repos/report.txt";

    private sealed class BufferedViewport(CellBuffer buffer, int width, int height, ColorMode mode)
        : ITerminalViewport
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

    /// <summary>Paints <paramref name="tree"/> into a buffer of the given size and hands the buffer back.</summary>
    private static CellBuffer Paint(Layout.Node tree, int width, int height,
        ColorMode mode = ColorMode.Sgr16)
    {
        var buffer = new CellBuffer { ColorMode = mode };
        buffer.Resize(width, height);

        var arranged = Layout.Engine.Arrange(tree, new Rect<int>(0, 0, width, height), new CellMeasureContext());
        CellLayout.Paint(new BufferedViewport(buffer, width, height, mode), arranged);
        return buffer;
    }

    private static Layout.Node Text(string value) => Layout.Builder.Text(value, 1f).WStar().HStar();

    [Fact]
    public void ALinkedTextLeaf_PaintsItsGlyphsInsideTheLink()
    {
        var buffer = Paint(Text("report.txt").Clickable(new HitResult.LinkHit(Target)), 10, 1);

        buffer.BackAt(0, 0).Link.ShouldBe(Target);
        buffer.BackAt(9, 0).Link.ShouldBe(Target);
    }

    /// <summary>
    /// The shape a row actually has: the link sits on a wrapper (the cell the row treats as "the path"), and
    /// the text is a leaf underneath it. Resolved through the same nearest-enclosing walk the background
    /// uses, so stating a link one level up is not a silent no-op.
    /// </summary>
    [Fact]
    public void ALinkOnAnAncestor_ReachesTheTextUnderneath()
    {
        var tree = Layout.Builder.HStack(Text("report.txt"))
            .RowH(1)
            .Clickable(new HitResult.LinkHit(Target));

        Paint(tree, 10, 1).BackAt(0, 0).Link.ShouldBe(Target);
    }

    /// <summary>A sibling outside the linked subtree must not inherit it — the stack has to pop.</summary>
    [Fact]
    public void ASiblingOutsideTheLinkedSubtree_IsNotLinked()
    {
        var tree = Layout.Builder.HStack(
            Layout.Builder.HStack(Text("ab")).WFixed(2).HStar().Clickable(new HitResult.LinkHit(Target)),
            Text("cd").WFixed(2));

        var buffer = Paint(tree, 4, 1);

        buffer.BackAt(0, 0).Link.ShouldBe(Target);
        buffer.BackAt(2, 0).Link.ShouldBeNull("the link ended with its subtree");
    }

    /// <summary>An inner link wins over an enclosing one, as the nearest enclosing background does.</summary>
    [Fact]
    public void ANestedLink_OverridesTheOneAroundIt()
    {
        var tree = Layout.Builder.HStack(Text("ab").Clickable(new HitResult.LinkHit("https://inner")))
            .RowH(1)
            .Clickable(new HitResult.LinkHit(Target));

        Paint(tree, 2, 1).BackAt(0, 0).Link.ShouldBe("https://inner");
    }

    /// <summary>Only a LinkHit is a link. A button is clickable and is not a place the terminal can navigate to.</summary>
    [Fact]
    public void AnOrdinaryHit_IsNotAHyperlink()
    {
        var buffer = Paint(Text("ab").Clickable(new HitResult.ButtonHit("delete")), 2, 1);

        buffer.BackAt(0, 0).Link.ShouldBeNull();
    }

    /// <summary>
    /// ColorMode.None means no escapes at all — the mode a plain-text dump uses. A hyperlink is an escape
    /// like any other, so it is omitted with the rest rather than being the one control sequence that
    /// survives into text output.
    /// </summary>
    [Fact]
    public void InPlainTextMode_NoLinkIsEmitted()
    {
        var buffer = Paint(Text("ab").Clickable(new HitResult.LinkHit(Target)), 2, 1, ColorMode.None);

        buffer.BackAt(0, 0).Link.ShouldBeNull();
        buffer.BackAt(0, 0).Glyph.ShouldBe('a');
    }

    /// <summary>
    /// The painted link region and the clickable region are the same rect. This is the property that reusing
    /// the hit buys, so it is pinned directly: the cell the terminal would navigate from is the cell
    /// <see cref="CellLayout.HitTest"/> answers for.
    /// </summary>
    [Fact]
    public void TheLinkedCellsAreExactlyTheHitRegion()
    {
        const int width = 4;
        var tree = Layout.Builder.HStack(
            Text("ab").WFixed(2).Clickable(new HitResult.LinkHit(Target)),
            Text("cd").WFixed(2));

        var buffer = new CellBuffer { ColorMode = ColorMode.Sgr16 };
        buffer.Resize(width, 1);
        var arranged = Layout.Engine.Arrange(tree, new Rect<int>(0, 0, width, 1), new CellMeasureContext());
        CellLayout.Paint(new BufferedViewport(buffer, width, 1, ColorMode.Sgr16), arranged);

        for (var column = 0; column < width; column++)
        {
            var linked = buffer.BackAt(column, 0).Link is not null;
            var hit = CellLayout.HitTest(arranged, column, 0) is HitResult.LinkHit;

            linked.ShouldBe(hit, $"column {column}: a link must be drawn exactly where it can be clicked");
        }
    }

    /// <summary>
    /// A linked row that has not changed emits nothing — the end-to-end version of the property
    /// <see cref="CellBufferTests.AHyperlinkedRun_IsModelledPerCellAndStillDiffs"/> pins at the buffer, via
    /// the painter a consumer actually calls.
    /// </summary>
    [Fact]
    public void ARepaintedLinkedRow_EmitsNothing()
    {
        var tree = Layout.Builder.HStack(Text("report.txt").Clickable(new HitResult.LinkHit(Target)))
            .RowH(1);

        var buffer = new CellBuffer { ColorMode = ColorMode.Sgr16 };
        buffer.Resize(10, 1);
        var viewport = new BufferedViewport(buffer, 10, 1, ColorMode.Sgr16);
        var arranged = Layout.Engine.Arrange(tree, new Rect<int>(0, 0, 10, 1), new CellMeasureContext());

        CellLayout.Paint(viewport, arranged);
        buffer.Flush(new NullSink());

        CellLayout.Paint(viewport, arranged);
        buffer.Flush(new NullSink()).ShouldBe(0, "a linked row diffs like any other");
        buffer.LastFlushOpaqueCells.ShouldBe(0, "…and does not reach the terminal by bypassing the diff");
    }

    private sealed class NullSink : ICellSink
    {
        public void MoveTo(int column, int row) { }
        public void SetPen(VtStyle style, bool reverse) { }
        public void SetLink(string? url) { }
        public void Write(ReadOnlySpan<char> run) { }
    }
}
