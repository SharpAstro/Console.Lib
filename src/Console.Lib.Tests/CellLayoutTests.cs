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

    private static LayoutNode.Leaf HitRow(string action) =>
        new(new LayoutContent.Box(0, 0) { Hit = new HitResult.ButtonHit(action) })
        {
            Height = Sizing.Fixed(1),
            Width = Sizing.Star(),
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
        var arranged = LayoutEngine.Arrange(new LayoutNode.Stack([a, b]), new Rect<int>(0, 0, 20, 4), new CellMeasureContext());

        CellLayout.HitTest(arranged, 5, 0).ShouldBeOfType<HitResult.ButtonHit>().Action.ShouldBe("A");
        CellLayout.HitTest(arranged, 5, 1).ShouldBeOfType<HitResult.ButtonHit>().Action.ShouldBe("B");
        CellLayout.HitTest(arranged, 5, 3).ShouldBeNull(); // below both 1-row leaves
    }

    [Fact]
    public void CellLayout_HitTest_InvokesOnClickInsideRect()
    {
        var clicks = 0;
        var leaf = new LayoutNode.Leaf(new LayoutContent.Box(0, 0) { Hit = new HitResult.ButtonHit("X"), OnClick = _ => clicks++ })
        {
            Height = Sizing.Fixed(2),
            Width = Sizing.Star(),
        };
        var arranged = LayoutEngine.Arrange(new LayoutNode.Stack([leaf]), new Rect<int>(0, 0, 20, 4), new CellMeasureContext());

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
        var label = new LayoutNode.Leaf(new LayoutContent.Text("Hi") { HAlign = TextAlign.Near })
        {
            Height = Sizing.Fixed(1),
            Width = Sizing.Star(),
        };
        var panel = new LayoutNode.Stack([label]) { Background = new RGBAColor32(0x10, 0x10, 0x18, 0xff) };
        var arranged = LayoutEngine.Arrange(panel, new Rect<int>(0, 0, 20, 3), new CellMeasureContext());

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
        var label = new LayoutNode.Leaf(new LayoutContent.Text("Hi") { HAlign = TextAlign.Center })
        {
            Height = Sizing.Fixed(1),
            Width = Sizing.Star(),
        };
        var arranged = LayoutEngine.Arrange(new LayoutNode.Stack([label]), new Rect<int>(0, 0, 20, 1), new CellMeasureContext());

        CellLayout.Paint(vp, arranged);

        var textWrite = vp.Writes.First(w => StripReset(w.Text).Contains("Hi"));
        textWrite.Col.ShouldBe(9); // (20 - 2) / 2
    }
}
