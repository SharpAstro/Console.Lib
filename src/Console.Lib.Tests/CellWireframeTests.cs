using System.Collections.Immutable;
using DIR.Lib;
using Shouldly;
using Xunit;

namespace Console.Lib.Tests;

/// <summary>
/// A cell frame judged by DIR.Lib's own <see cref="Wireframe.Check"/>, in cells, at a default tolerance of 0.
/// Trees are arranged by hand so the overlaps a real layout would never produce can be stated exactly.
/// </summary>
public class CellWireframeTests
{
    private const int Width = 30;
    private const int Height = 5;

    private static ImmutableArray<Layout.ArrangedNode<int>> Arrange(params (Layout.Node Node, Rect<int> Bounds)[] nodes)
        => [.. nodes.Select(n => new Layout.ArrangedNode<int>(n.Node, n.Bounds))];

    private static WireframeFrame Record(ImmutableArray<Layout.ArrangedNode<int>> arranged)
    {
        var buffer = new CellBuffer { ColorMode = ColorMode.TrueColor };
        buffer.Resize(Width, Height);
        var viewport = new CellBufferViewport(buffer, Width, Height);
        return CellWireframe.Record(viewport, arranged);
    }

    private static Layout.Node Label(string text, TextTrim trim = TextTrim.End)
        => Layout.Builder.Text(text, 1f, trim: trim);

    [Fact]
    public void TwoTextRunsInOneCellAreTextOverText()
    {
        var frame = Record(Arrange(
            (Label("hello world"), new Rect<int>(0, 0, 11, 1)),
            (Label("overlap"), new Rect<int>(6, 0, 7, 1))));

        var findings = CellWireframe.Check(frame);

        findings.Select(f => f.Rule).ShouldContain(WireframeRules.TextOverText, Wireframe.Describe(findings));
    }

    [Fact]
    public void LabelHalfOverAButtonIsTextOverRegion()
    {
        var button = Layout.Builder.Text("OK", 1f).Clickable(new HitResult.ButtonHit("ok"), _ => { });
        var frame = Record(Arrange(
            (button, new Rect<int>(0, 0, 6, 1)),
            (Label("label"), new Rect<int>(3, 0, 8, 1))));

        var findings = CellWireframe.Check(frame);

        findings.Select(f => f.Rule).ShouldContain(WireframeRules.TextOverRegion, Wireframe.Describe(findings));
    }

    [Fact]
    public void LabelCutWithNoEllipsisIsCutText()
    {
        // TextTrim.None is the painter's own hard clip: the cut with no ellipsis.
        var frame = Record(Arrange((Label("a long label", TextTrim.None), new Rect<int>(0, 0, 6, 1))));

        var findings = CellWireframe.Check(frame);

        findings.Select(f => f.Rule).ShouldContain(WireframeRules.CutText, Wireframe.Describe(findings));
    }

    [Fact]
    public void LabelTrimmedWithAnEllipsisPasses()
    {
        var frame = Record(Arrange((Label("a long label"), new Rect<int>(0, 0, 6, 1))));

        var findings = CellWireframe.Check(frame);

        findings.ShouldBeEmpty(Wireframe.Describe(findings));
    }

    [Fact]
    public void CleanTwoLabelRowYieldsNoFinding()
    {
        var button = Layout.Builder.Text("OK", 1f).Clickable(new HitResult.ButtonHit("ok"), _ => { });
        var frame = Record(Arrange(
            (Label("name"), new Rect<int>(0, 0, 8, 1)),
            (button, new Rect<int>(8, 0, 6, 1))));

        var findings = CellWireframe.Check(frame);

        findings.ShouldBeEmpty(Wireframe.Describe(findings));
        frame.Texts.Length.ShouldBe(2);
        frame.Regions.Length.ShouldBe(1);
    }

    // ---- cell-only rules ----

    private static readonly RGBAColor32 Paint = new(0x20, 0x40, 0x60, 0xff);

    private static CellWireframeFrame RecordCells(ImmutableArray<Layout.ArrangedNode<int>> arranged)
    {
        var buffer = new CellBuffer { ColorMode = ColorMode.TrueColor };
        buffer.Resize(Width, Height);
        var viewport = new CellBufferViewport(buffer, Width, Height);
        return CellWireframe.RecordCells(viewport, arranged);
    }

    private static Layout.ArrangedNode<int> At(Layout.Node node, Rect<int> bounds, int depth)
        => new(node, bounds) { Depth = depth };

    private static IEnumerable<CellWireframeRules> CellRules(CellWireframeResult r) => r.CellFindings.Select(f => f.Rule);

    [Fact]
    public void OneLineLeafArrangedTwoRowsTallInsideItsFillIsADoubledRow()
    {
        var label = Label("name") with { Background = Paint };
        var result = CellWireframe.CheckCells(RecordCells([At(label, new Rect<int>(0, 0, 10, 2), 0)]));

        CellRules(result).ShouldContain(CellWireframeRules.DoubledRow, CellWireframe.Describe(result));
    }

    [Fact]
    public void PixelAuthored28UnitOneLineRowIsOneRowAndPasses()
    {
        var row = new Layout.Node.Leaf(new Layout.Content.Text("Hello", 12f))
        {
            Height = Layout.Sizing.Fixed(28), Width = Layout.Sizing.Star(), Background = Paint,
        };
        var arranged = Layout.Engine.Arrange(new Layout.Node.Stack([row]), new Rect<int>(0, 0, Width, Height), CellMeasureContext.PixelAuthored);
        var result = CellWireframe.CheckCells(RecordCells(arranged));

        result.IsEmpty.ShouldBeTrue(CellWireframe.Describe(result));
    }

    [Fact]
    public void ArrangedFilledNodeThatPaintsNoCellIsAVanishedFill()
    {
        var bar = new Layout.Node.Stack([]) { Background = Paint };
        var result = CellWireframe.CheckCells(RecordCells([At(bar, new Rect<int>(2, 1, 5, 0), 0)]));

        CellRules(result).ShouldContain(CellWireframeRules.VanishedFill, CellWireframe.Describe(result));
    }

    [Fact]
    public void PixelAuthored8UnitBarPaintsAndPasses()
    {
        var bar = new Layout.Node.Stack([]) { Background = Paint, Width = Layout.Sizing.Star(), Height = Layout.Sizing.Star() };
        var row = new Layout.Node.Stack([bar]) { Height = Layout.Sizing.Fixed(8), Width = Layout.Sizing.Star() };
        var arranged = Layout.Engine.Arrange(new Layout.Node.Stack([row]), new Rect<int>(0, 0, Width, Height), CellMeasureContext.PixelAuthored);
        var result = CellWireframe.CheckCells(RecordCells(arranged));

        result.IsEmpty.ShouldBeTrue(CellWireframe.Describe(result));
    }

    [Fact]
    public void LabelCutWithFreeCellsToItsRightInItsContainerIsCutWithRoom()
    {
        var container = new Layout.Node.Stack([]);
        var result = CellWireframe.CheckCells(RecordCells([
            At(container, new Rect<int>(0, 0, 20, 1), 0),
            At(Label("a long label"), new Rect<int>(0, 0, 6, 1), 1)]));

        CellRules(result).ShouldContain(CellWireframeRules.CutWithRoom, CellWireframe.Describe(result));
    }

    [Fact]
    public void LabelCutFlushAgainstItsContainersEdgeIsNotCutWithRoom()
    {
        var container = new Layout.Node.Stack([]);
        var result = CellWireframe.CheckCells(RecordCells([
            At(container, new Rect<int>(0, 0, 6, 1), 0),
            At(Label("a long label"), new Rect<int>(0, 0, 6, 1), 1)]));

        CellRules(result).ShouldNotContain(CellWireframeRules.CutWithRoom, CellWireframe.Describe(result));
    }

    [Fact]
    public void RulesAreSelectableThroughOptions()
    {
        var label = Label("name") with { Background = Paint };
        var frame = RecordCells([At(label, new Rect<int>(0, 0, 10, 2), 0)]);

        CellWireframe.CheckCells(frame, new CellWireframeOptions { Rules = CellWireframeRules.VanishedFill })
            .CellFindings.ShouldBeEmpty();
    }
}
