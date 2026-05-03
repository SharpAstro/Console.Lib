using Console.Lib;
using DIR.Lib;
using Shouldly;
using Xunit;

namespace Console.Lib.Tests;

/// <summary>
/// Cursor / selection coverage for <see cref="ScrollableList{TItem}"/>.
/// The mouse-drag and scrollbar paths are covered by the wider integration
/// tests; these focus on the cursor model added alongside the original
/// scroll-only API.
/// </summary>
public sealed class ScrollableListTests
{
    private readonly struct Row(int index) : IRowFormatter
    {
        public int Index { get; } = index;
        public string FormatRow(int width, ColorMode mode) => Index.ToString().PadRight(width);
    }

    private static ScrollableList<Row> NewList(int itemCount, int width = 20, int height = 8)
    {
        var terminal = new FakeTerminal(new Queue<ConsoleInputEvent>(), width, height);
        var viewport = new TerminalViewport(terminal, 0, 0, width, height);
        var list = new ScrollableList<Row>(viewport).Header(" idx");
        var items = Enumerable.Range(0, itemCount).Select(i => new Row(i)).ToList();
        list.Items(items);
        return list;
    }

    [Fact]
    public void EmptyList_HasCursorIndexMinusOne()
    {
        var list = NewList(0);
        list.CursorIndex.ShouldBe(-1);
        list.Selected.ShouldBe(default);
    }

    [Fact]
    public void NewList_StartsCursorAtZero()
    {
        var list = NewList(5);
        list.CursorIndex.ShouldBe(0);
        list.Selected.Index.ShouldBe(0);
    }

    [Fact]
    public void MoveCursor_AdvancesAndClamps()
    {
        var list = NewList(3);

        list.MoveCursor(+1).ShouldBeTrue();
        list.CursorIndex.ShouldBe(1);

        list.MoveCursor(+10).ShouldBeTrue();
        list.CursorIndex.ShouldBe(2);            // clamped to last

        list.MoveCursor(+1).ShouldBeFalse();     // already at end
        list.CursorIndex.ShouldBe(2);

        list.MoveCursor(-100).ShouldBeTrue();
        list.CursorIndex.ShouldBe(0);
    }

    [Fact]
    public void MoveTo_AcceptsIntMaxValueAsLast()
    {
        var list = NewList(7);
        list.MoveTo(int.MaxValue).ShouldBeTrue();
        list.CursorIndex.ShouldBe(6);
    }

    [Fact]
    public void MoveCursor_ScrollsViewportToFollowCursor()
    {
        // Header + 7 visible data rows. Item 30 is way below the initial view.
        var list = NewList(50);
        list.MoveTo(30).ShouldBeTrue();

        // Cursor must be in view: 0 ≤ cursor - scrollOffset < VisibleRows.
        var inView = list.CursorIndex - list.ScrollOffset;
        inView.ShouldBeInRange(0, list.VisibleRows - 1);
    }

    [Fact]
    public void HandleKey_MapsArrowAndPagingKeys()
    {
        var list = NewList(50);

        list.HandleKey(ConsoleKey.DownArrow).ShouldBeTrue();
        list.CursorIndex.ShouldBe(1);

        list.HandleKey(ConsoleKey.PageDown).ShouldBeTrue();
        list.CursorIndex.ShouldBeGreaterThan(1);

        list.HandleKey(ConsoleKey.End).ShouldBeTrue();
        list.CursorIndex.ShouldBe(49);

        list.HandleKey(ConsoleKey.Home).ShouldBeTrue();
        list.CursorIndex.ShouldBe(0);

        list.HandleKey(ConsoleKey.Spacebar).ShouldBeFalse();
    }

    [Fact]
    public void Items_ClampsCursorToNewBounds()
    {
        var list = NewList(20);
        list.MoveTo(15);
        list.Items(Enumerable.Range(0, 5).Select(i => new Row(i)).ToList());
        list.CursorIndex.ShouldBe(4);
    }
}
