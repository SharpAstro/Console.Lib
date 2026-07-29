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
    private readonly struct Row(int index) : IRowLayout
    {
        public int Index { get; } = index;
        public Layout.Node BuildRow(in RowContext context) => Layout.Builder.Text(Index.ToString(), 1f);
    }

    /// <summary>Class-typed row that records the context of every BuildRow call.</summary>
    private sealed class RecordingRow(int index) : IRowLayout
    {
        public int Index { get; } = index;
        public List<(bool IsSelected, int SelectedColumn, int ColumnCount)> Calls { get; } = new();
        public Layout.Node BuildRow(in RowContext context)
        {
            Calls.Add((context.Selected, context.SelectedColumn, context.ColumnCount));
            return Layout.Builder.Text(Index.ToString(), 1f);
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
        // Overflowing list (>VisibleRows = 7) → scrollbar present. CellSize is (10, 20)
        // per FakeTerminal; with a scrollbar, content width = viewport width - 1 = 19.
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
    public void MouseClick_OnContentRow_RegistersWithoutScrollbar()
    {
        // Short list (≤VisibleRows = 7) → no scrollbar; clicks must still register.
        // Without scrollbar, content width = full viewport width (20). Columns(2) →
        // cells [0..9] are column 0, cells [10..19] are column 1.
        var list = NewList(itemCount: 3, width: 20, height: 8).Columns(2);

        // Click on the second content row, right half (cell col 15 → pixel x 150).
        list.HandleMouse(new MouseEvent(Button: 0, X: 150, Y: 40, IsRelease: false))
            .ShouldBeTrue();
        list.CursorIndex.ShouldBe(1);
        list.ColumnIndex.ShouldBe(1);

        // Click on the last column of the row (cell col 19 → pixel x 190) still
        // routes to the content branch when there is no scrollbar.
        list.HandleMouse(new MouseEvent(Button: 0, X: 190, Y: 20, IsRelease: false))
            .ShouldBeTrue();
        list.CursorIndex.ShouldBe(0);
        list.ColumnIndex.ShouldBe(1);

        // Click on the left half (cell col 5 → pixel x 50) of row 2.
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

    [Fact]
    public void Wheel_NotRouted_WhenAutoHandleWheelDisabled()
    {
        // 20 items / 7 visible rows → scrollbar present. With AutoHandleWheel
        // off (default), wheel events must NOT change the scroll offset; they
        // fall through so the caller can attach its own semantics.
        var list = NewList(itemCount: 20).ScrollTo(5);
        list.AutoHandleWheel.ShouldBeFalse();

        list.HandleMouse(new MouseEvent(Button: 64, X: 0, Y: 0, IsRelease: false))
            .ShouldBeFalse();
        list.ScrollOffset.ShouldBe(5);

        list.HandleMouse(new MouseEvent(Button: 65, X: 0, Y: 0, IsRelease: false))
            .ShouldBeFalse();
        list.ScrollOffset.ShouldBe(5);
    }

    [Fact]
    public void Wheel_AutoRoutes_WhenEnabled_ScrollsByWheelStep()
    {
        var list = NewList(itemCount: 20).ScrollTo(5);
        list.AutoHandleWheel = true;

        // Button 64 = wheel up → scroll toward list start → offset decreases by WheelStep (3).
        list.HandleMouse(new MouseEvent(Button: 64, X: 0, Y: 0, IsRelease: false))
            .ShouldBeTrue();
        list.ScrollOffset.ShouldBe(2);

        // Button 65 = wheel down → offset increases by WheelStep (3).
        list.HandleMouse(new MouseEvent(Button: 65, X: 0, Y: 0, IsRelease: false))
            .ShouldBeTrue();
        list.ScrollOffset.ShouldBe(5);
    }

    [Fact]
    public void Wheel_AutoRoutes_HonoursCustomWheelStep()
    {
        var list = NewList(itemCount: 20).ScrollTo(0);
        list.AutoHandleWheel = true;
        list.WheelStep = 5;

        list.HandleMouse(new MouseEvent(Button: 65, X: 0, Y: 0, IsRelease: false))
            .ShouldBeTrue();
        list.ScrollOffset.ShouldBe(5);
    }

    [Fact]
    public void Wheel_AutoRoutes_ReturnsFalseAtBoundary()
    {
        // Already at offset 0; wheeling up further shouldn't change state.
        var list = NewList(itemCount: 20).ScrollTo(0);
        list.AutoHandleWheel = true;

        list.HandleMouse(new MouseEvent(Button: 64, X: 0, Y: 0, IsRelease: false))
            .ShouldBeFalse();
        list.ScrollOffset.ShouldBe(0);
    }

    [Fact]
    public void Wheel_AutoRoutes_NoOpWhenListFitsInViewport()
    {
        // 3 items / 7 visible rows → no scrollbar → HandleWheel returns false
        // even with AutoHandleWheel enabled (nothing to scroll).
        var list = NewList(itemCount: 3);
        list.AutoHandleWheel = true;

        list.HandleMouse(new MouseEvent(Button: 65, X: 0, Y: 0, IsRelease: false))
            .ShouldBeFalse();
        list.ScrollOffset.ShouldBe(0);
    }

    // ---- DispatchRowHit ----

    /// <summary>Builds a list at a known viewport offset so the pixel origin is not trivially (0,0).</summary>
    private static ScrollableList<Row> NewOffsetList(int itemCount, int col, int row, int width = 20, int height = 8)
    {
        var terminal = new FakeTerminal(new Queue<ConsoleInputEvent>(), col + width, row + height);
        var viewport = new TerminalViewport(terminal, col, row, width, height);
        var list = new ScrollableList<Row>(viewport).Header(" idx");
        list.Items(Enumerable.Range(0, itemCount).Select(i => new Row(i)).ToList());
        return list;
    }

    /// <summary>
    /// A row with an inline button pinned to its RIGHT edge -- a Star label followed by a fixed-width
    /// button. This shape is the reason the row contract is a tree: expressed as columns in a formatted
    /// string, the button's position depends on the row's usable width, which shrinks by one the moment a
    /// scrollbar appears, so any hand-derived hit region drifted exactly when the list overflowed.
    /// </summary>
    private sealed class ButtonRow(int index, List<int> clicks) : IRowLayout
    {
        public Layout.Node BuildRow(in RowContext context) => Layout.Builder.HStack(
            Layout.Builder.Text(index.ToString(), 1f).WStar().HStar(),
            Layout.Builder.Text("[X]", 1f).WFixed(3).HStar()
                .Clickable(new HitResult.ButtonHit($"del{index}"), _ => clicks.Add(index)));
    }

    private static (ScrollableList<ButtonRow> List, List<int> Clicks) NewButtonList(
        int itemCount, int col = 0, int row = 0, int width = 20, int height = 8, int scrollTo = 0)
    {
        var clicks = new List<int>();
        var terminal = new FakeTerminal(new Queue<ConsoleInputEvent>(), col + width, row + height);
        var viewport = new TerminalViewport(terminal, col, row, width, height);
        var list = new ScrollableList<ButtonRow>(viewport).Header(" idx");
        list.Items(Enumerable.Range(0, itemCount).Select(i => new ButtonRow(i, clicks)).ToList());
        list.ScrollTo(scrollTo);
        list.Render();   // DispatchRowHit resolves against the ARRANGED trees, so a paint must have happened.
        return (list, clicks);
    }

    /// <summary>
    /// A right-anchored button resolves at the row's real right edge, and that edge accounts for the
    /// scrollbar without anyone computing it: the same tree lands one column further left once the list
    /// overflows, because it was arranged into the content width.
    /// </summary>
    [Theory]
    [InlineData(3, 20)]    // fits: content is all 20 columns, so the button occupies 17..19
    [InlineData(100, 19)]  // overflows: the track takes column 19, so the button occupies 16..18
    public void DispatchRowHit_ResolvesARightAnchoredButtonAgainstTheContentWidth(int itemCount, int contentColumns)
    {
        var (list, clicks) = NewButtonList(itemCount);
        var cell = list.Viewport.CellSize;
        var buttonX = (int)((contentColumns - 1) * cell.Width);   // last content column
        var firstRowY = (int)cell.Height + 1;                     // header occupies row 0

        list.DispatchRowHit(buttonX, firstRowY).ShouldBe(new HitResult.ButtonHit($"del{list.ScrollOffset}"));
        clicks.ShouldBe([list.ScrollOffset]);
    }

    /// <summary>The header is not a row, so nothing on it dispatches.</summary>
    [Fact]
    public void DispatchRowHit_OnTheHeader_DispatchesNothing()
    {
        var (list, clicks) = NewButtonList(itemCount: 3);
        var cell = list.Viewport.CellSize;

        list.DispatchRowHit((int)(17 * cell.Width), 1).ShouldBeNull();
        clicks.ShouldBeEmpty();
    }

    /// <summary>
    /// The scroll trap: once scrolled, the top visible row is item ScrollOffset. A hit must dispatch THAT
    /// item's button, not the first item's -- which is what a host re-deriving the index from a
    /// visible-row number used to get wrong.
    /// </summary>
    [Fact]
    public void DispatchRowHit_DispatchesTheScrolledItemsButton()
    {
        var (list, clicks) = NewButtonList(itemCount: 50, scrollTo: 10);
        var cell = list.Viewport.CellSize;

        list.DispatchRowHit((int)(16 * cell.Width), (int)cell.Height + 1)
            .ShouldBe(new HitResult.ButtonHit("del10"), "the top visible row is item 10");
        clicks.ShouldBe([10]);
    }

    /// <summary>
    /// The scrollbar trap, from the other side: a click on the track must not reach a row's button, or the
    /// thumb could never be grabbed.
    /// </summary>
    [Fact]
    public void DispatchRowHit_OnTheScrollbarColumn_DispatchesNothing()
    {
        var (list, clicks) = NewButtonList(itemCount: 100);
        var cell = list.Viewport.CellSize;

        list.DispatchRowHit((int)(19 * cell.Width) + 1, (int)cell.Height + 1).ShouldBeNull();
        clicks.ShouldBeEmpty();
    }

    /// <summary>The origin is the viewport OFFSET times cell size, not the viewport rect.</summary>
    [Fact]
    public void DispatchRowHit_OriginFollowsTheViewportOffset()
    {
        var (list, clicks) = NewButtonList(itemCount: 3, col: 4, row: 2);
        var cell = list.Viewport.CellSize;

        // Item 0 sits at the offset row plus the header row; its button is in the last content column.
        list.DispatchRowHit((int)((4 + 19) * cell.Width), (int)((2 + 1) * cell.Height) + 1)
            .ShouldBe(new HitResult.ButtonHit("del0"));
        clicks.ShouldBe([0]);

        list.DispatchRowHit(1, (int)((2 + 1) * cell.Height) + 1).ShouldBeNull("left of the viewport");
    }

    /// <summary>A point on the row but not on any clickable leaf dispatches nothing.</summary>
    [Fact]
    public void DispatchRowHit_OffTheButton_DispatchesNothing()
    {
        var (list, clicks) = NewButtonList(itemCount: 3);
        var cell = list.Viewport.CellSize;

        list.DispatchRowHit((int)(2 * cell.Width), (int)cell.Height + 1).ShouldBeNull();
        clicks.ShouldBeEmpty();
    }

    [Fact]
    public void DispatchRowHit_BeforeAnyRender_DispatchesNothing()
    {
        var list = NewOffsetList(itemCount: 3, col: 0, row: 0);
        var cell = list.Viewport.CellSize;

        list.DispatchRowHit(1, (int)cell.Height + 1).ShouldBeNull("nothing has been arranged yet");
    }

    [Fact]
    public void DispatchRowHit_EmptyList_DispatchesNothing()
    {
        var (list, clicks) = NewButtonList(itemCount: 0);

        list.DispatchRowHit(1, 20).ShouldBeNull();
        clicks.ShouldBeEmpty();
    }

    /// <summary>
    /// Only the visible rows are arranged, so a 10k-item list costs viewport-height trees per frame
    /// rather than one per item.
    /// </summary>
    [Fact]
    public void ArrangedRows_CoverOnlyTheVisibleRows()
    {
        var (list, _) = NewButtonList(itemCount: 10_000);

        list.ArrangedRows.ShouldNotBeEmpty();
        // Each row contributes its HStack plus two leaves; the bound is what matters, not the exact count.
        list.ArrangedRows.Length.ShouldBeLessThanOrEqualTo(list.VisibleRows * 8);
    }

    // ---- HitTestRow ----

    [Fact]
    public void HitTestRow_ResolvesTheItemUnderThePoint_BelowTheHeader()
    {
        var list = NewOffsetList(itemCount: 3, col: 0, row: 0);
        var cell = list.Viewport.CellSize;

        // Row 0 is the header, so the first item sits one cell down.
        list.HitTestRow(1, (int)cell.Height + 1).ShouldNotBeNull().ItemIndex.ShouldBe(0);
        list.HitTestRow(1, (int)(cell.Height * 2) + 1).ShouldNotBeNull().ItemIndex.ShouldBe(1);
    }

    [Fact]
    public void HitTestRow_OnTheHeader_IsNotARow()
    {
        var list = NewOffsetList(itemCount: 3, col: 0, row: 0);

        list.HitTestRow(1, 1).ShouldBeNull();
    }

    /// <summary>Once scrolled, the top visible row is item ScrollOffset — the trap a host re-deriving
    /// this from its own scroll state falls into.</summary>
    [Fact]
    public void HitTestRow_MapsToScrolledItemIndices()
    {
        var list = NewOffsetList(itemCount: 50, col: 0, row: 0);
        list.ScrollTo(10);
        var cell = list.Viewport.CellSize;

        list.HitTestRow(1, (int)cell.Height + 1).ShouldNotBeNull().ItemIndex.ShouldBe(10);
    }

    /// <summary>
    /// The defect this API removes: <see cref="Widget.HitTest"/> reports the scrollbar's column like any
    /// other, so a host that splits a row by the viewport width treats a click on the track as content.
    /// </summary>
    [Fact]
    public void HitTestRow_OnTheScrollbarColumn_IsNotARow()
    {
        var scrolling = NewOffsetList(itemCount: 100, col: 0, row: 0, width: 20, height: 8);
        var fitting = NewOffsetList(itemCount: 2, col: 0, row: 0, width: 20, height: 8);
        var cell = scrolling.Viewport.CellSize;
        var y = (int)cell.Height + 1;
        var lastColumnX = (int)(19 * cell.Width) + 1;

        scrolling.HitTestRow(lastColumnX, y).ShouldBeNull("the scrollbar owns the last column");
        fitting.HitTestRow(lastColumnX, y).ShouldNotBeNull().Column.ShouldBe(19, "a list that fits has no scrollbar, so every column is content");
    }

    /// <summary>
    /// Content width comes back with the column so a caller can divide the row into fields without
    /// knowing whether a scrollbar is showing — which depends on the item count, so it moves under them.
    /// </summary>
    [Fact]
    public void HitTestRow_ReportsContentWidthExcludingTheScrollbar()
    {
        var cell = NewOffsetList(1, 0, 0).Viewport.CellSize;
        var y = (int)cell.Height + 1;

        NewOffsetList(itemCount: 100, col: 0, row: 0, width: 20, height: 8)
            .HitTestRow(1, y).ShouldNotBeNull().Columns.ShouldBe(19);
        NewOffsetList(itemCount: 2, col: 0, row: 0, width: 20, height: 8)
            .HitTestRow(1, y).ShouldNotBeNull().Columns.ShouldBe(20);
    }

    [Fact]
    public void HitTestRow_PastTheLastItem_IsNotARow()
    {
        var list = NewOffsetList(itemCount: 2, col: 0, row: 0, width: 20, height: 8);
        var cell = list.Viewport.CellSize;

        // Visible rows 1 and 2 hold the two items; row 3 is empty space below them.
        list.HitTestRow(1, (int)(cell.Height * 3) + 1).ShouldBeNull();
    }

    /// <summary>The origin is the viewport OFFSET times cell size, not the viewport rect.</summary>
    [Fact]
    public void HitTestRow_OriginFollowsTheViewportOffset()
    {
        var list = NewOffsetList(itemCount: 3, col: 4, row: 2);
        var cell = list.Viewport.CellSize;

        // Offset row plus the header row is where item 0 lives; anything above it is not a row.
        list.HitTestRow((int)(4 * cell.Width) + 1, (int)(3 * cell.Height) + 1).ShouldNotBeNull().ItemIndex.ShouldBe(0);
        list.HitTestRow(1, (int)(3 * cell.Height) + 1).ShouldBeNull("left of the viewport");
    }

}
