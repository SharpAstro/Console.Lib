using System.Collections.Immutable;
using System.Linq;
using Console.Lib;
using DIR.Lib;
using Shouldly;
using Xunit;

namespace Console.Lib.Tests;

/// <summary>
/// Tests the terminal cell painter + the <see cref="TerminalLayout"/> dock unification onto
/// <see cref="DockLayout{T}"/>. Layout/arrange itself is covered in DIR.Lib; here we check the
/// cell-specific bits: dock geometry stays correct, hit-test maps cells back to leaf hits, and
/// Paint writes the expected backgrounds + text.
/// </summary>
public class CellLayoutTests
{
    /// <summary>Captures the (column, row, text) of each write so painter output can be asserted.</summary>
    private sealed class RecordingViewport(int width, int height) : ITerminalViewport
    {
        public List<(int Col, int Row, string Text)> Writes { get; } = [];
        private int _col, _row;

        public (int Column, int Row) Offset => (0, 0);
        public (int Width, int Height) Size => (width, height);
        public TermCell CellSize => new(10, 20);
        public ColorMode ColorMode => ColorMode.None; // no escapes => Writes capture raw text (+ Reset suffix)

        public void SetCursorPosition(int left, int top)
        {
            _col = left;
            _row = top;
        }

        public void Write(string text) => Writes.Add((_col, _row, text));
        public void WriteLine(string? text = null) { }
        public void Flush() { }
        public Stream OutputStream => Stream.Null;
    }

    private static string StripReset(string text) => text.Replace(VtStyle.Reset, "");

    private static Layout.Node.Leaf HitRow(string action) =>
        new(new Layout.Content.Box(0, 0))
        {
            Hit = new HitResult.ButtonHit(action),
            Height = Layout.Sizing.Fixed(1),
            Width = Layout.Sizing.Star(),
        };

    // --- dock unification ---

    [Fact]
    public void TerminalLayout_DocksEdges_GeometryUnchangedOnDockLayoutInt()
    {
        var term = new FakeTerminal(new Queue<ConsoleInputEvent>(), width: 80, height: 24);
        var layout = new TerminalLayout(term);

        var top = layout.Dock(DockStyle.Top, 3);
        var bottom = layout.Dock(DockStyle.Bottom, 2);
        var left = layout.Dock(DockStyle.Left, 10);
        var fill = layout.Dock(DockStyle.Fill);

        top.Offset.ShouldBe((0, 0));
        top.Size.ShouldBe((80, 3));
        bottom.Offset.ShouldBe((0, 22));   // 24 - 2
        bottom.Size.ShouldBe((80, 2));
        left.Offset.ShouldBe((0, 3));      // below the top strip
        left.Size.ShouldBe((10, 19));      // rows 3..21 inclusive => 19 tall
        fill.Offset.ShouldBe((10, 3));
        fill.Size.ShouldBe((70, 19));
    }

    [Fact]
    public void TerminalLayout_OversizedStrip_ClampsToRemaining()
    {
        var term = new FakeTerminal(new Queue<ConsoleInputEvent>(), width: 40, height: 10);
        var layout = new TerminalLayout(term);

        var top = layout.Dock(DockStyle.Top, 100); // larger than the terminal
        var fill = layout.Dock(DockStyle.Fill);

        top.Size.ShouldBe((40, 10));  // clamped to all 10 rows
        fill.Size.ShouldBe((40, 0));  // nothing left
    }

    // --- hit testing (the arranged rect IS the hit region) ---

    [Fact]
    public void CellLayout_HitTest_MapsCellToLeafHit()
    {
        var a = HitRow("A");
        var b = HitRow("B");
        var arranged = Layout.Engine.Arrange(new Layout.Node.Stack([a, b]), new Rect<int>(0, 0, 20, 4), new CellMeasureContext());

        CellLayout.HitTest(arranged, 5, 0).ShouldBeOfType<HitResult.ButtonHit>().Action.ShouldBe("A");
        CellLayout.HitTest(arranged, 5, 1).ShouldBeOfType<HitResult.ButtonHit>().Action.ShouldBe("B");
        CellLayout.HitTest(arranged, 5, 3).ShouldBeNull(); // below both 1-row leaves
    }

    [Fact]
    public void CellLayout_HitTest_InvokesOnClickInsideRect()
    {
        var clicks = 0;
        var leaf = new Layout.Node.Leaf(new Layout.Content.Box(0, 0))
        {
            Hit = new HitResult.ButtonHit("X"),
            OnClick = _ => clicks++,
            Height = Layout.Sizing.Fixed(2),
            Width = Layout.Sizing.Star(),
        };
        var arranged = Layout.Engine.Arrange(new Layout.Node.Stack([leaf]), new Rect<int>(0, 0, 20, 4), new CellMeasureContext());

        CellLayout.HitTest(arranged, 3, 1).ShouldNotBeNull();
        clicks.ShouldBe(1);

        CellLayout.HitTest(arranged, 3, 3); // outside the 0..1 rows
        clicks.ShouldBe(1);
    }

    // --- paint output ---

    [Fact]
    public void CellLayout_Paint_FillsBackgroundThenDrawsText()
    {
        var vp = new RecordingViewport(20, 3);
        var label = new Layout.Node.Leaf(new Layout.Content.Text("Hi") { HAlign = TextAlign.Near })
        {
            Height = Layout.Sizing.Fixed(1),
            Width = Layout.Sizing.Star(),
        };
        var panel = new Layout.Node.Stack([label]) { Background = new RGBAColor32(0x10, 0x10, 0x18, 0xff) };
        var arranged = Layout.Engine.Arrange(panel, new Rect<int>(0, 0, 20, 3), new CellMeasureContext());

        CellLayout.Paint(vp, arranged);

        // The panel background fills all 3 rows with 20 spaces.
        var fills = vp.Writes.Where(w => StripReset(w.Text).Length == 20 && StripReset(w.Text).Trim().Length == 0).ToList();
        fills.Count.ShouldBe(3);
        fills.Select(w => w.Row).OrderBy(r => r).ShouldBe([0, 1, 2]);

        // The text "Hi" is drawn near-aligned at the top-left of the row.
        var textWrite = vp.Writes.First(w => StripReset(w.Text).Contains("Hi"));
        textWrite.Col.ShouldBe(0);
        textWrite.Row.ShouldBe(0);
    }

    [Fact]
    public void CellLayout_Paint_CenterAlignsText()
    {
        var vp = new RecordingViewport(20, 1);
        var label = new Layout.Node.Leaf(new Layout.Content.Text("Hi") { HAlign = TextAlign.Center })
        {
            Height = Layout.Sizing.Fixed(1),
            Width = Layout.Sizing.Star(),
        };
        var arranged = Layout.Engine.Arrange(new Layout.Node.Stack([label]), new Rect<int>(0, 0, 20, 1), new CellMeasureContext());

        CellLayout.Paint(vp, arranged);

        var textWrite = vp.Writes.First(w => StripReset(w.Text).Contains("Hi"));
        textWrite.Col.ShouldBe(9); // (20 - 2) / 2
    }

    // --- Describe (cell-side counterpart to the pixel inspector's describe_layout) ---

    [Fact]
    public void CellLayout_Describe_IndentsChildrenByDepthAndNamesContent()
    {
        var a = new Layout.Node.Leaf(new Layout.Content.Text("A")) { Height = Layout.Sizing.Fixed(1), Width = Layout.Sizing.Star() };
        var b = new Layout.Node.Leaf(new Layout.Content.Text("B")) { Height = Layout.Sizing.Fixed(1), Width = Layout.Sizing.Star() };
        var arranged = Layout.Engine.Arrange(new Layout.Node.Stack([a, b]), new Rect<int>(0, 0, 20, 4), new CellMeasureContext());

        var lines = CellLayout.Describe(arranged).Split('\n', StringSplitOptions.RemoveEmptyEntries);

        lines[0].ShouldBe("Stack[V] (0,0 20x4)");        // root, depth 0
        lines[1].ShouldBe("  Leaf Text \"A\" (0,0 20x1)"); // depth 1 => 2-space indent
        lines[2].ShouldBe("  Leaf Text \"B\" (0,1 20x1)");
    }

    [Fact]
    public void CellLayout_Describe_MarksBackgroundAndHit()
    {
        var hitLeaf = new Layout.Node.Leaf(new Layout.Content.Box(0, 0))
        {
            Hit = new HitResult.ButtonHit("go"),
            Height = Layout.Sizing.Fixed(1),
            Width = Layout.Sizing.Star(),
        };
        var panel = new Layout.Node.Stack([hitLeaf]) { Background = new RGBAColor32(0x10, 0x10, 0x18, 0xff) };
        var arranged = Layout.Engine.Arrange(panel, new Rect<int>(0, 0, 10, 2), new CellMeasureContext());

        var dump = CellLayout.Describe(arranged);

        dump.ShouldContain("Stack[V] (0,0 10x2) +bg");        // container paints a background
        dump.ShouldContain("Leaf Box(spacer) (0,0 10x1) +hit"); // transparent Box + click binding
    }

    [Fact]
    public void CellLayout_Describe_DistinguishesFilledBoxAndKeyedFill()
    {
        var swatch = new Layout.Node.Leaf(new Layout.Content.Box(0, 0) { Color = new RGBAColor32(0xff, 0x00, 0x00, 0xff) })
            { Height = Layout.Sizing.Fixed(1), Width = Layout.Sizing.Star() };
        var canvas = new Layout.Node.Leaf(new Layout.Content.Fill(Key: "chart"))
            { Height = Layout.Sizing.Star(), Width = Layout.Sizing.Star() };
        var arranged = Layout.Engine.Arrange(new Layout.Node.Stack([swatch, canvas]), new Rect<int>(0, 0, 8, 4), new CellMeasureContext());

        var dump = CellLayout.Describe(arranged);

        dump.ShouldContain("Leaf Box(filled)");
        dump.ShouldContain("Leaf Fill(\"chart\")");
    }

    // ---- Node.Radius on a cell surface ----

    private static ImmutableArray<Layout.ArrangedNode<int>> ArrangePanel(float radius, int w, int h) =>
        Layout.Engine.Arrange(
            new Layout.Node.Leaf(new Layout.Content.Box(0, 0))
            {
                Background = new RGBAColor32(0x20, 0x30, 0x40, 0xff),
                CornerRadius = radius,
                Width = Layout.Sizing.Star(),
                Height = Layout.Sizing.Star(),
            },
            new Rect<int>(0, 0, w, h), new CellMeasureContext());

    /// <summary>
    /// A grid cannot round by fractions of a cell, so the approximation clips a QUARTER off each corner
    /// cell with a three-quadrant block. This asserts the four glyphs land on the four corner cells and
    /// nowhere else, and that each one omits the quadrant pointing away from the interior.
    /// <para>
    /// Not the arc glyphs (U+256D..U+2570): those are a thin stroke, so on a solid fill the corner cell
    /// comes out ~90% parent colour and reads as a bite punched out of the card rather than a softened
    /// corner. They remain the right choice for an unfilled box drawn with border characters, which the
    /// layout DSL cannot currently express.
    /// </para>
    /// </summary>
    [Fact]
    public void Radius_ClipsAQuarterOffEachCornerCellOfAFill()
    {
        var vp = new RecordingViewport(10, 4);

        CellLayout.Paint(vp, ArrangePanel(radius: 2f, 10, 4));

        var glyphs = vp.Writes
            .Select(w => (w.Col, w.Row, Text: StripReset(w.Text)))
            .Where(w => w.Text is "▙" or "▛" or "▜" or "▟")
            .ToList();

        glyphs.Count.ShouldBe(4);
        glyphs.ShouldContain((0, 0, "▟"), "top-left omits the upper-left quadrant");
        glyphs.ShouldContain((9, 0, "▙"), "top-right omits the upper-right quadrant");
        glyphs.ShouldContain((0, 3, "▜"), "bottom-left omits the lower-left quadrant");
        glyphs.ShouldContain((9, 3, "▛"), "bottom-right omits the lower-right quadrant");
    }

    /// <summary>
    /// The arc glyphs must not appear on a fill at all -- that was the original rendering, and it is the
    /// one that looked broken.
    /// </summary>
    [Fact]
    public void Radius_DoesNotUseArcGlyphsOnAFill()
    {
        var vp = new RecordingViewport(10, 4);

        CellLayout.Paint(vp, ArrangePanel(radius: 2f, 10, 4));

        // Plain comparisons, not an `is` pattern: Shouldly's predicate overload builds an expression tree.
        vp.Writes.Select(w => StripReset(w.Text))
            .ShouldNotContain(t => t == "╭" || t == "╮" || t == "╰" || t == "╯");
    }

    /// <summary>Zero radius must leave the fill exactly as it was before the feature existed.</summary>
    [Fact]
    public void Radius_Zero_PaintsNoCornerGlyphs()
    {
        var rounded = new RecordingViewport(10, 4);
        var square = new RecordingViewport(10, 4);

        CellLayout.Paint(rounded, ArrangePanel(radius: 0f, 10, 4));
        CellLayout.Paint(square, Layout.Engine.Arrange(
            new Layout.Node.Leaf(new Layout.Content.Box(0, 0))
            {
                Background = new RGBAColor32(0x20, 0x30, 0x40, 0xff),
                Width = Layout.Sizing.Star(),
                Height = Layout.Sizing.Star(),
            },
            new Rect<int>(0, 0, 10, 4), new CellMeasureContext()));

        rounded.Writes.ShouldBe(square.Writes);
    }

    /// <summary>
    /// Below 3x3 the corners ARE the shape, so rounding would erase most of the fill rather than
    /// soften it. The fill is left square instead.
    /// </summary>
    [Theory]
    [InlineData(2, 4)]
    [InlineData(10, 2)]
    [InlineData(1, 1)]
    public void Radius_IsSkippedWhenTheRectIsTooSmallToHaveCorners(int w, int h)
    {
        var vp = new RecordingViewport(w, h);

        CellLayout.Paint(vp, ArrangePanel(radius: 2f, w, h));

        var corners = vp.Writes.Select(x => StripReset(x.Text))
            .Where(t => t == "▙" || t == "▛" || t == "▜" || t == "▟")
            .ToList();
        corners.ShouldBeEmpty();
    }
}
