using System.Collections.Immutable;
using Console.Lib;
using DIR.Lib;
using Shouldly;
using Xunit;

namespace Console.Lib.Tests;

/// <summary>
/// Tests for <see cref="MenuWidget"/>: BuildTree produces the expected arranged item count,
/// HitTest maps cells back to item clicks, and HandleKey navigates the model.
/// </summary>
public class MenuWidgetTests
{
    /// <summary>Minimal recording viewport for cell-layout tests (no color escapes).</summary>
    private sealed class SimpleViewport(int width, int height) : ITerminalViewport
    {
        public (int Column, int Row) Offset => (0, 0);
        public (int Width, int Height) Size => (width, height);
        public TermCell CellSize => new(10, 20);
        public ColorMode ColorMode => ColorMode.None;

        public void SetCursorPosition(int left, int top) { }
        public void Write(string text) { }
        public void WriteLine(string? text = null) { }
        public void Flush() { }
        public Stream OutputStream => Stream.Null;
    }

    private static readonly ImmutableArray<string> ThreeItems = ["Alpha", "Beta", "Gamma"];

    [Fact]
    public void BuildTree_ThreeItems_ProducesExpectedArrangedNodeCount()
    {
        // MenuLayout.BuildTree with 3 items = outer Stack containing:
        //   top spacer + title + prompt + gap box + 3 item leaves + bottom spacer = 8 children.
        // Layout.Engine.Arrange flattens the tree into one Layout.ArrangedNode per Layout.Node visited.
        var tree = MenuLayout.BuildTree(
            BuildModel("Title", "Pick:", ThreeItems),
            new MenuColors(),
            fontSize: 1f);

        var arranged = Layout.Engine.Arrange(tree, new Rect<int>(0, 0, 40, 20), new CellMeasureContext());

        // The outer Stack node + 8 children = 9 arranged nodes at minimum.
        arranged.Length.ShouldBeGreaterThanOrEqualTo(9);
    }

    [Fact]
    public void HitTest_ClickOnFirstItemRow_ConfirmsAtIndexZero()
    {
        // fontSize=1f, so each item row is Fixed(itemLineH = 2 cells tall).
        // Layout: top Star spacer | title(2) | prompt(2) | gap(0) | item0(2) | item1(2) | item2(2) | bottom Star.
        // With a 20-row viewport the Star spacers split the slack. Total content = 2+2+0+6 = 10 rows.
        // Slack = 20 - 10 = 10, each Star spacer gets 5 rows. So item0 starts at row 5+2+2+0 = 9.
        var vp = new SimpleViewport(40, 20);
        var widget = new MenuWidget(vp);
        widget.Reset("Title", "Pick:", ThreeItems);

        // Render to populate _arranged.
        widget.Render();

        // Click on column 5, row 9 should land on item 0.
        // Use pixel coords: viewport offset=(0,0), cellSize=(10,20), so pixel (50, 180) -> (col=5, row=9).
        var clicked = widget.HandleMouse(new MouseEvent(0, 50, 180, IsRelease: false));

        clicked.ShouldBeTrue();
        widget.IsConfirmed.ShouldBeTrue();
        widget.SelectedIndex.ShouldBe(0);
    }

    [Fact]
    public void HandleKey_DownThenEnter_ConfirmsAtIndexOne()
    {
        var vp = new SimpleViewport(40, 20);
        var widget = new MenuWidget(vp);
        widget.Reset("Title", "Pick:", ThreeItems);

        widget.HandleKey(InputKey.Down).ShouldBeTrue();
        widget.SelectedIndex.ShouldBe(1);
        widget.IsConfirmed.ShouldBeFalse();

        widget.HandleKey(InputKey.Enter).ShouldBeTrue();
        widget.IsConfirmed.ShouldBeTrue();
        widget.SelectedIndex.ShouldBe(1);
    }

    [Fact]
    public void HandleKey_UpWraps_FromFirstToLast()
    {
        var vp = new SimpleViewport(40, 20);
        var widget = new MenuWidget(vp);
        widget.Reset("Title", "Pick:", ThreeItems);

        // SelectedIndex starts at 0; Up should wrap to last item (index 2).
        widget.HandleKey(InputKey.Up).ShouldBeTrue();
        widget.SelectedIndex.ShouldBe(2);
    }

    [Fact]
    public void HandleKey_D2_ConfirmsAtIndexOne()
    {
        var vp = new SimpleViewport(40, 20);
        var widget = new MenuWidget(vp);
        widget.Reset("Title", "Pick:", ThreeItems);

        widget.HandleKey(InputKey.D2).ShouldBeTrue();
        widget.IsConfirmed.ShouldBeTrue();
        widget.SelectedIndex.ShouldBe(1);
    }

    [Fact]
    public void Reset_ClearsConfirmedState()
    {
        var vp = new SimpleViewport(40, 20);
        var widget = new MenuWidget(vp);
        widget.Reset("Title", "Pick:", ThreeItems);
        widget.HandleKey(InputKey.Enter);
        widget.IsConfirmed.ShouldBeTrue();

        // Reset clears IsConfirmed.
        widget.Reset("New Title", "Pick again:", ThreeItems);
        widget.IsConfirmed.ShouldBeFalse();
        widget.SelectedIndex.ShouldBe(0);
    }

    private static MenuModel BuildModel(string title, string prompt, ImmutableArray<string> items)
    {
        var m = new MenuModel();
        m.Reset(title, prompt, items);
        return m;
    }
}
