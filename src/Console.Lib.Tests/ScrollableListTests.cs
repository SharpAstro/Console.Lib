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

    /// <summary>Class-typed row that records every column-aware FormatRow call.</summary>
    private sealed class RecordingRow(int index) : IRowFormatter
    {
        public int Index { get; } = index;
        public List<(bool IsSelected, int SelectedColumn, int ColumnCount)> Calls { get; } = new();
        public string FormatRow(int width, ColorMode mode) => Index.ToString().PadRight(width);
        public string FormatRow(int width, ColorMode mode, bool isSelected, int selectedColumn, int columnCount)
        {
            Calls.Add((isSelected, selectedColumn, columnCount));
            return FormatRow(width, mode);
        }
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

    // ── two-column mode ───────────────────────────────────────────────

    [Fact]
    public void Columns_DefaultsToOne()
    {
        var list = NewList(5);
        list.ColumnCount.ShouldBe(1);
        list.ColumnIndex.ShouldBe(0);
    }

    [Fact]
    public void Columns_RejectsLessThanOne()
    {
        var list = NewList(5);
        Should.Throw<ArgumentOutOfRangeException>(() => list.Columns(0));
        Should.Throw<ArgumentOutOfRangeException>(() => list.Columns(-1));
    }

    [Fact]
    public void Columns_ClampsCurrentColumnToNewBounds()
    {
        var list = NewList(5).Columns(3);
        list.MoveColumn(+2).ShouldBeTrue();
        list.ColumnIndex.ShouldBe(2);

        list.Columns(2);
        list.ColumnIndex.ShouldBe(1);   // clamped from 2 → 1

        list.Columns(1);
        list.ColumnIndex.ShouldBe(0);   // clamped to single-column
    }

    [Fact]
    public void MoveColumn_AdvancesAndClamps()
    {
        var list = NewList(5).Columns(3);

        list.MoveColumn(+1).ShouldBeTrue();
        list.ColumnIndex.ShouldBe(1);

        list.MoveColumn(+10).ShouldBeTrue();
        list.ColumnIndex.ShouldBe(2);          // clamped to last column

        list.MoveColumn(+1).ShouldBeFalse();   // already at last column
        list.ColumnIndex.ShouldBe(2);

        list.MoveColumn(-100).ShouldBeTrue();
        list.ColumnIndex.ShouldBe(0);

        // Single-column lists ignore MoveColumn.
        var single = NewList(5);
        single.MoveColumn(+1).ShouldBeFalse();
        single.ColumnIndex.ShouldBe(0);
    }

    [Fact]
    public void HandleKey_LeftRight_MoveColumnWhenMultiColumn()
    {
        var list = NewList(5).Columns(2);

        list.HandleKey(ConsoleKey.RightArrow).ShouldBeTrue();
        list.ColumnIndex.ShouldBe(1);

        list.HandleKey(ConsoleKey.RightArrow).ShouldBeFalse(); // clamped at last
        list.ColumnIndex.ShouldBe(1);

        list.HandleKey(ConsoleKey.LeftArrow).ShouldBeTrue();
        list.ColumnIndex.ShouldBe(0);

        list.HandleKey(ConsoleKey.LeftArrow).ShouldBeFalse();  // clamped at first

        // Single-column lists fall through on Left/Right.
        var single = NewList(5);
        single.HandleKey(ConsoleKey.LeftArrow).ShouldBeFalse();
        single.HandleKey(ConsoleKey.RightArrow).ShouldBeFalse();
    }

    [Fact]
    public void MouseClick_OnContentRow_SetsRowAndColumn()
    {
        // Need enough items to trigger HasScrollBar (>VisibleRows = 7) so the
        // mouse handler engages. CellSize is (10, 20) per FakeTerminal.
        var list = NewList(itemCount: 10, width: 20, height: 8).Columns(2);

        // Click on the first content row, right half (cell col 15 → pixel x 150).
        // Cell row 1 (after the header at row 0) → pixel y 20.
        list.HandleMouse(new MouseEvent(Button: 0, X: 150, Y: 20, IsRelease: false))
            .ShouldBeTrue();
        list.CursorIndex.ShouldBe(0);
        list.ColumnIndex.ShouldBe(1);

        // Click on the second content row, left half (cell col 5 → pixel x 50).
        list.HandleMouse(new MouseEvent(Button: 0, X: 50, Y: 40, IsRelease: false))
            .ShouldBeTrue();
        list.CursorIndex.ShouldBe(1);
        list.ColumnIndex.ShouldBe(0);
    }

    [Fact]
    public void Render_PassesSelectedColumnToFormatter()
    {
        var terminal = new FakeTerminal(new Queue<ConsoleInputEvent>(), 20, 8);
        var viewport = new TerminalViewport(terminal, 0, 0, 20, 8);
        var rows = Enumerable.Range(0, 4).Select(i => new RecordingRow(i)).ToList();
        var list = new ScrollableList<RecordingRow>(viewport)
            .Header(" idx")
            .Columns(2)
            .Items(rows);

        list.MoveCursor(+2, +1);   // cursor row 2, column 1

        list.Render();

        // Non-cursor rows: isSelected=false, selectedColumn=-1, columnCount=2.
        rows[0].Calls.ShouldHaveSingleItem().ShouldBe((false, -1, 2));
        rows[1].Calls.ShouldHaveSingleItem().ShouldBe((false, -1, 2));
        rows[3].Calls.ShouldHaveSingleItem().ShouldBe((false, -1, 2));
        // Cursor row: isSelected=true, selectedColumn=1, columnCount=2.
        rows[2].Calls.ShouldHaveSingleItem().ShouldBe((true, 1, 2));
    }
}
