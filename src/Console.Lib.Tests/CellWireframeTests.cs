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

    private static CellWireframeResult CheckSized(int width, int height, params Layout.ArrangedNode<int>[] arranged)
    {
        var buffer = new CellBuffer { ColorMode = ColorMode.TrueColor };
        buffer.Resize(width, height);
        var viewport = new CellBufferViewport(buffer, width, height);
        return CellWireframe.CheckCells(CellWireframe.RecordCells(viewport, [.. arranged]));
    }

    [Fact]
    public void FilledBarArrangedHalfTheScreenHoldingOneRowOfLabelsIsEmptyRows()
    {
        var bar = new Layout.Node.Stack([]) { Background = Paint };
        var result = CheckSized(80, 24,
            At(bar, new Rect<int>(0, 0, 80, 12), 0),
            At(Label("Home"), new Rect<int>(0, 0, 4, 1), 1),
            At(Label("Sessions"), new Rect<int>(5, 0, 8, 1), 1),
            At(new Layout.Node.Stack([]), new Rect<int>(0, 12, 80, 12), 0));

        CellRules(result).ShouldContain(CellWireframeRules.EmptyRows, CellWireframe.Describe(result));
    }

    [Fact]
    public void OneRowFilledBarIsNotEmptyRows()
    {
        var bar = new Layout.Node.Stack([]) { Background = Paint };
        var result = CheckSized(80, 24,
            At(bar, new Rect<int>(0, 0, 80, 1), 0),
            At(Label("Home"), new Rect<int>(0, 0, 4, 1), 1));

        CellRules(result).ShouldNotContain(CellWireframeRules.EmptyRows, CellWireframe.Describe(result));
    }

    [Fact]
    public void SparseVerticalPageWithLabelsOnTwoRowsIsNotEmptyRows()
    {
        var page = new Layout.Node.Stack([]) { Background = Paint };
        var result = CheckSized(80, 24,
            At(page, new Rect<int>(0, 0, 80, 12), 0),
            At(Label("first"), new Rect<int>(0, 0, 5, 1), 1),
            At(Label("second"), new Rect<int>(0, 1, 6, 1), 1));

        CellRules(result).ShouldNotContain(CellWireframeRules.EmptyRows, CellWireframe.Describe(result));
    }

    [Fact]
    public void BarWhoseExtraRowsAreItsOwnVerticalPaddingIsNotEmptyRows()
    {
        var bar = new Layout.Node.Stack([]) { Background = Paint, PaddingY = 1f };
        var result = CheckSized(80, 24,
            At(bar, new Rect<int>(0, 0, 80, 3), 0),
            At(Label("Home"), new Rect<int>(0, 1, 4, 1), 1));

        CellRules(result).ShouldNotContain(CellWireframeRules.EmptyRows, CellWireframe.Describe(result));
    }

    [Fact]
    public void FilledContainerDeliberatelyFixedTallerThanItsOneRowIsEmptyRows()
    {
        var bar = new Layout.Node.Stack([]) { Background = Paint, Height = Layout.Sizing.Fixed(4) };
        var result = CheckSized(80, 24,
            At(bar, new Rect<int>(0, 0, 80, 4), 0),
            At(Label("Home"), new Rect<int>(0, 0, 4, 1), 1));

        CellRules(result).ShouldContain(CellWireframeRules.EmptyRows, CellWireframe.Describe(result));
        result.CellFindings.First(f => f.Rule == CellWireframeRules.EmptyRows).Where.Height.ShouldBe(4f);
    }

    [Fact]
    public void UnpaintedTextLeafIsNotContentOnRowMinusOne()
    {
        // A text leaf arranged wholly outside the viewport never paints, so it has no row; it must not
        // make the container's content look like it spans rows -1 and 0.
        var bar = new Layout.Node.Stack([]) { Background = Paint };
        var result = CheckSized(80, 24,
            At(bar, new Rect<int>(0, 0, 80, 12), 0),
            At(Label("Home"), new Rect<int>(0, 0, 4, 1), 1),
            At(Label("offscreen"), new Rect<int>(0, 30, 9, 1), 1));

        CellRules(result).ShouldContain(CellWireframeRules.EmptyRows, CellWireframe.Describe(result));
    }

    // Console.Lib #68: a bare null third argument compiles, and means "no drawFill, no buffer" as it did before the
    // buffer overloads were added. Without the overload priority on the older overloads this is CS0121.
    [Fact]
    public void ABareNullThirdArgumentStillBindsToTheOverloadWithoutABuffer()
    {
        var label = new Layout.Node.Leaf(new Layout.Content.Text("Home", 12f)) { Height = Layout.Sizing.Fixed(16), Width = Layout.Sizing.Star() };
        var arranged = Layout.Engine.Arrange(new Layout.Node.Stack([label]), new Rect<int>(0, 0, Width, Height), CellMeasureContext.PixelAuthored);
        var buffer = new CellBuffer { ColorMode = ColorMode.TrueColor };
        buffer.Resize(Width, Height);
        var viewport = new CellBufferViewport(buffer, Width, Height, ColorMode.TrueColor);

        CellWireframe.Record(viewport, arranged, null).ShouldNotBeNull();
        CellWireframe.RecordCells(viewport, arranged, null).ShouldNotBeNull();
    }

    // Console.Lib #72: the test below cannot tell a floored padding from a rounded one (16 units is one row either
    // way). Here it can: 24 units of padding are 1 row a side floored and 2 rounded, so a 4 row bar round one row of
    // text has one row its padding does not explain, and a rounding branch would hide it.
    [Fact]
    public void PixelAuthoredPaddingIsFlooredToWholeRows_SoARowItDoesNotCoverIsStillAnEmptyRow()
    {
        var label = new Layout.Node.Leaf(new Layout.Content.Text("Home", 12f)) { Height = Layout.Sizing.Fixed(16), Width = Layout.Sizing.Star() };
        var bar = new Layout.Node.Stack([label]) { Background = Paint, PaddingY = 24f, Height = Layout.Sizing.Fixed(64), Width = Layout.Sizing.Star() };
        var arranged = Layout.Engine.Arrange(new Layout.Node.Stack([bar]), new Rect<int>(0, 0, Width, Height), CellMeasureContext.PixelAuthored);
        var result = CellWireframe.CheckCells(RecordCells(arranged));

        CellRules(result).ShouldContain(CellWireframeRules.EmptyRows, CellWireframe.Describe(result));
    }

    [Fact]
    public void PixelAuthoredBarWhoseExtraRowsAreItsPaddingIsNotEmptyRows()
    {
        var label = new Layout.Node.Leaf(new Layout.Content.Text("Home", 12f)) { Height = Layout.Sizing.Fixed(28), Width = Layout.Sizing.Star() };
        var bar = new Layout.Node.Stack([label]) { Background = Paint, PaddingY = 16f, Height = Layout.Sizing.Fixed(60), Width = Layout.Sizing.Star() };
        var arranged = Layout.Engine.Arrange(new Layout.Node.Stack([bar]), new Rect<int>(0, 0, Width, Height), CellMeasureContext.PixelAuthored);
        var result = CellWireframe.CheckCells(RecordCells(arranged));

        CellRules(result).ShouldNotContain(CellWireframeRules.EmptyRows, CellWireframe.Describe(result));
    }

    [Fact]
    public void DoubledRowLeafIsNotAlsoEmptyRows()
    {
        var label = Label("name") with { Background = Paint };
        var result = CheckSized(80, 24, At(label, new Rect<int>(0, 0, 10, 2), 0));

        CellRules(result).ShouldContain(CellWireframeRules.DoubledRow);
        CellRules(result).ShouldNotContain(CellWireframeRules.EmptyRows, CellWireframe.Describe(result));
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
    public void PixelAuthored8UnitBarPaintsAndIsOnlyASolidStrip()
    {
        var bar = new Layout.Node.Stack([]) { Background = Paint, Width = Layout.Sizing.Star(), Height = Layout.Sizing.Star() };
        var row = new Layout.Node.Stack([bar]) { Height = Layout.Sizing.Fixed(8), Width = Layout.Sizing.Star() };
        var arranged = Layout.Engine.Arrange(new Layout.Node.Stack([row]), new Rect<int>(0, 0, Width, Height), CellMeasureContext.PixelAuthored);
        var result = CellWireframe.CheckCells(RecordCells(arranged));

        // An empty full-width bar painted with whole cells is exactly the defect SolidStrip names (Console.Lib #70):
        // it paints (no VanishedFill) and nothing else is wrong, but a rule should be a thin line.
        result.Findings.IsEmpty.ShouldBeTrue(CellWireframe.Describe(result));
        CellRules(result).ShouldBe([CellWireframeRules.SolidStrip], CellWireframe.Describe(result));
    }

    private static Layout.Node RuleStrip(float rowHeight, bool star = true) => new Layout.Node.Stack([])
    {
        Background = Paint, Height = Layout.Sizing.Fixed(rowHeight), Width = star ? Layout.Sizing.Star() : Layout.Sizing.Fixed(160),
    };

    private static CellWireframeResult CheckRuleTree(Layout.Node strip, Layout.Node? middle = null)
    {
        middle ??= new Layout.Node.Stack([new Layout.Node.Leaf(new Layout.Content.Text("> prompt", 12f)) { Height = Layout.Sizing.Fixed(16), Width = Layout.Sizing.Star() }])
        { Height = Layout.Sizing.Fixed(16), Width = Layout.Sizing.Star() };
        var tree = new Layout.Node.Stack([strip, middle, strip]);
        var arranged = Layout.Engine.Arrange(tree, new Rect<int>(0, 0, Width, Height), CellMeasureContext.PixelAuthored);
        return CellWireframe.CheckCells(RecordCells(arranged));
    }

    [Fact]
    public void PixelAuthoredGreyRuleBarsAroundAPromptAreSolidStrips()
    {
        var result = CheckRuleTree(RuleStrip(16f));
        var strips = result.CellFindings.Where(f => f.Rule == CellWireframeRules.SolidStrip).ToArray();

        strips.Length.ShouldBe(2, CellWireframe.Describe(result));
        strips.All(f => f.Where.Width == Width && f.Where.Height == 1f).ShouldBeTrue(CellWireframe.Describe(result));
        strips[0].Where.Y.ShouldBe(0f);
        strips[1].Where.Y.ShouldBe(2f);
    }

    [Fact]
    public void PixelAuthoredThinRuleLinesAreNotSolidStrips()
    {
        var result = CheckRuleTree(RuleStrip(4f));

        CellRules(result).ShouldNotContain(CellWireframeRules.SolidStrip, CellWireframe.Describe(result));
    }

    [Fact]
    public void OneRowStripHoldingALabelIsNotASolidStrip()
    {
        var label = new Layout.Node.Leaf(new Layout.Content.Text("Home", 12f)) { Height = Layout.Sizing.Fixed(16), Width = Layout.Sizing.Star() };
        var strip = new Layout.Node.Stack([label]) { Background = Paint, Height = Layout.Sizing.Fixed(16), Width = Layout.Sizing.Star() };
        var result = CheckRuleTree(strip);

        CellRules(result).ShouldNotContain(CellWireframeRules.SolidStrip, CellWireframe.Describe(result));
    }

    [Fact]
    public void OneRowStripNarrowerThanItsContainerIsNotASolidStrip()
    {
        var result = CheckRuleTree(RuleStrip(16f, star: false));

        CellRules(result).ShouldNotContain(CellWireframeRules.SolidStrip, CellWireframe.Describe(result));
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
            drawFill: (_, rect) =>
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
        // A host that blanks its slot with the default pen: cells are written, but there is no text and no opaque fill to judge.
        var fill = Layout.Builder.Fill(1f, 1f);
        var frame = CellWireframe.RecordCells(viewport, Arrange((fill, new Rect<int>(0, 0, 10, 1))),
            drawFill: (_, rect) =>
            {
                viewport.SetCursorPosition(rect.X, rect.Y);
                viewport.Write(new string(' ', rect.Width));
            }, buffer: buffer);

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

    [Fact]
    public void BorderedNodeHoldingTextChecksClean()
    {
        var node = new Layout.Node.Stack([Label("Hi")]).Border(new RGBAColor32(0xff, 0, 0, 0xff));
        var buffer = new CellBuffer { ColorMode = ColorMode.TrueColor };
        buffer.Resize(Width, Height);
        var viewport = new CellBufferViewport(buffer, Width, Height);
        var arranged = Layout.Engine.Arrange(node, new Rect<int>(0, 0, 10, 4), new CellMeasureContext());

        var frame = CellWireframe.RecordCells(viewport, arranged, buffer: buffer);
        var result = CellWireframe.CheckCells(frame);

        result.IsEmpty.ShouldBeTrue(CellWireframe.Describe(result));
    }

    private static CellWireframeResult CheckBordered(int arrangedHeight, int viewportHeight, Layout.Sizing? fixedHeight = null)
    {
        var stack = new Layout.Node.Stack([Label("Hi")]) { Background = Paint };
        if (fixedHeight is { } h)
        {
            stack = stack with { Height = h };
        }

        Layout.Node node = stack.Border(new RGBAColor32(0xff, 0, 0, 0xff));
        if (viewportHeight < arrangedHeight)
        {
            // A scroll viewport viewportHeight rows tall cuts the node to its first rows.
            node = new Layout.Node.Stack([node]) { Scroll = new ListScrollController(), Height = Layout.Sizing.Fixed(viewportHeight) };
        }

        var buffer = new CellBuffer { ColorMode = ColorMode.TrueColor };
        buffer.Resize(Width, Height);
        var viewport = new CellBufferViewport(buffer, Width, Height);
        var arranged = Layout.Engine.Arrange(node, new Rect<int>(0, 0, 10, viewportHeight < arrangedHeight ? viewportHeight : arrangedHeight), new CellMeasureContext());
        var frame = CellWireframe.RecordCells(viewport, arranged, buffer: buffer);
        return CellWireframe.CheckCells(frame);
    }

    [Fact]
    public void BorderedFilledContainerCutToTopBorderRowAndTextIsNotEmptyRows()
    {
        var result = CheckBordered(4, 2, Layout.Sizing.Fixed(4));

        CellRules(result).ShouldNotContain(CellWireframeRules.EmptyRows, CellWireframe.Describe(result));
    }

    [Fact]
    public void BorderedFilledContainerShownWholeAroundOneRowIsNotEmptyRows()
    {
        var result = CheckBordered(3, 3);

        CellRules(result).ShouldNotContain(CellWireframeRules.EmptyRows, CellWireframe.Describe(result));
    }

    [Fact]
    public void BorderedFilledContainerFixedTallerThanBorderAndContentIsEmptyRows()
    {
        var result = CheckBordered(6, 6, Layout.Sizing.Fixed(6));

        CellRules(result).ShouldContain(CellWireframeRules.EmptyRows, CellWireframe.Describe(result));
    }
}
