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

    [Theory]
    [InlineData(TextTrim.Start)]
    [InlineData(TextTrim.Middle)]
    public void LabelEllipsizedAtStartOrMiddlePassesTheCutCheck(TextTrim trim)
    {
        var frame = Record(Arrange((Label("a long label", trim), new Rect<int>(0, 0, 6, 1))));

        var findings = CellWireframe.Check(frame);

        findings.ShouldBeEmpty(Wireframe.Describe(findings));
    }

    [Fact]
    public void OptionsThatOnlySetIgnoreTextKeepToleranceZero()
    {
        var frame = Record(Arrange(
            (Label("hello"), new Rect<int>(0, 0, 5, 1)),
            (Label("world"), new Rect<int>(4, 0, 5, 1))));

        var findings = CellWireframe.Check(frame, new WireframeOptions { IgnoreText = t => t.Text == "nothing" });

        findings.Select(f => f.Rule).ShouldContain(WireframeRules.TextOverText, Wireframe.Describe(findings));
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
    public void CutLabelWithAPaintedBoxSiblingToItsRightIsNotCutWithRoom()
    {
        var container = new Layout.Node.Stack([]);
        var swatch = Layout.Builder.Box(6f, 1f, new RGBAColor32(0xFF, 0, 0, 0xFF));
        var result = CellWireframe.CheckCells(RecordCells([
            At(container, new Rect<int>(0, 0, 12, 1), 0),
            At(Label("a long label"), new Rect<int>(0, 0, 6, 1), 1),
            At(swatch, new Rect<int>(6, 0, 6, 1), 1)]));

        CellRules(result).ShouldNotContain(CellWireframeRules.CutWithRoom, CellWireframe.Describe(result));
    }

    [Theory]
    [InlineData(TextTrim.Start)]
    [InlineData(TextTrim.End)]
    [InlineData(TextTrim.Middle)]
    public void LabelTrimmedToOneCellIsNotReportedAsCutWithNoEllipsis(TextTrim trim)
    {
        var frame = Record(Arrange((Label("a long label", trim), new Rect<int>(0, 0, 1, 1))));

        var findings = CellWireframe.Check(frame);

        findings.ShouldBeEmpty(Wireframe.Describe(findings));
    }

    [Fact]
    public void CutLabelWithAHostPaintedFillLeafToItsRightIsNotCutWithRoom()
    {
        var container = new Layout.Node.Stack([]);
        var hostPainted = Layout.Builder.Fill(6f, 1f);
        var result = CellWireframe.CheckCells(RecordCells([
            At(container, new Rect<int>(0, 0, 12, 1), 0),
            At(Label("a long label"), new Rect<int>(0, 0, 6, 1), 1),
            At(hostPainted, new Rect<int>(6, 0, 6, 1), 1)]));

        CellRules(result).ShouldNotContain(CellWireframeRules.CutWithRoom, CellWireframe.Describe(result));
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

    [Fact]
    public void HostedWidgetOverlappedByALabelIsTextOverText()
    {
        var buffer = new CellBuffer { ColorMode = ColorMode.TrueColor };
        buffer.Resize(Width, Height);
        var viewport = new CellBufferViewport(buffer, Width, Height);
        var hosted = new TerminalViewport(viewport, 0, 0, 1, 1);
        var bar = new TextBar(hosted).Text("hosted bar text");
        var fill = Layout.Builder.Fill(1f, 1f);
        var frame = CellWireframe.RecordCells(viewport,
            Arrange((fill, new Rect<int>(0, 0, 20, 1)), (Label("label"), new Rect<int>(4, 0, 8, 1))),
            (_, rect) =>
            {
                hosted.UpdateGeometry(rect.X, rect.Y, rect.Width, rect.Height);
                bar.Render();
            }, buffer: buffer);

        var result = CellWireframe.CheckCells(frame);

        result.Findings.Select(f => f.Rule).ShouldContain(WireframeRules.TextOverText, CellWireframe.Describe(result));
    }

    [Fact]
    public void FrameThatWritesCellsButRecordsNothingFailsAsNothingRecorded()
    {
        var buffer = new CellBuffer { ColorMode = ColorMode.TrueColor };
        buffer.Resize(Width, Height);
        var viewport = new CellBufferViewport(buffer, Width, Height);
        var input = Layout.Builder.TextInput(new TextInputState { Text = "typed" }, 1f);
        var frame = CellWireframe.RecordCells(viewport, Arrange((input, new Rect<int>(0, 0, 10, 1))), buffer: buffer);

        var result = CellWireframe.CheckCells(frame);

        result.CellFindings.Select(f => f.Rule).ShouldContain(CellWireframeRules.NothingRecorded, CellWireframe.Describe(result));
    }

    [Fact]
    public void EmptyFrameThatWroteNothingPasses()
    {
        var buffer = new CellBuffer { ColorMode = ColorMode.TrueColor };
        buffer.Resize(Width, Height);
        var viewport = new CellBufferViewport(buffer, Width, Height);
        var frame = CellWireframe.RecordCells(viewport, Arrange(), buffer: buffer);

        CellWireframe.CheckCells(frame).IsEmpty.ShouldBeTrue();
    }
}
