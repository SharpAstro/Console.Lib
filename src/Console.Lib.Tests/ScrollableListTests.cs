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

    // ---- RegisterRowHits ----

    /// <summary>Builds a list at a known viewport offset so the pixel origin is not trivially (0,0).</summary>
    private static ScrollableList<Row> NewOffsetList(int itemCount, int col, int row, int width = 20, int height = 8)
    {
        var terminal = new FakeTerminal(new Queue<ConsoleInputEvent>(), col + width, row + height);
        var viewport = new TerminalViewport(terminal, col, row, width, height);
        var list = new ScrollableList<Row>(viewport).Header(" idx");
        list.Items(Enumerable.Range(0, itemCount).Select(i => new Row(i)).ToList());
        return list;
    }

    [Fact]
    public void RegisterRowHits_BindsOneRegionPerVisibleRow_BelowTheHeader()
    {
        var list = NewOffsetList(itemCount: 3, col: 0, row: 0);
        var tracker = new ClickableRegionTracker();
        tracker.BeginFrame();

        list.RegisterRowHits(tracker, (i, _) => new HitResult.ListItemHit("row", i));

        var regions = tracker.GetRegisteredRegions();
        regions.Length.ShouldBe(3);
        var cell = list.Viewport.CellSize;
        // Header occupies row 0, so the first item starts one cell down.
        regions[0].Y.ShouldBe(cell.Height);
        regions[1].Y.ShouldBe(cell.Height * 2);
    }

    /// <summary>
    /// The trap this API exists to remove: once scrolled, visible row 0 is item ScrollOffset, not item 0.
    /// A host doing its own arithmetic against the item list selects the wrong item after any scroll.
    /// </summary>
    [Fact]
    public void RegisterRowHits_MapsRowsToScrolledItemIndices()
    {
        var list = NewOffsetList(itemCount: 50, col: 0, row: 0);
        list.ScrollTo(10);
        var tracker = new ClickableRegionTracker();
        tracker.BeginFrame();

        var clicked = -1;
        list.RegisterRowHits(tracker, (i, _) => new HitResult.ListItemHit("row", i), (i, _) => clicked = i);

        // Click the top visible row; it must resolve to item 10, not item 0.
        var cell = list.Viewport.CellSize;
        tracker.HitTestAndDispatch(1f, cell.Height + 1f).ShouldBe(new HitResult.ListItemHit("row", 10));
        clicked.ShouldBe(10);
    }

    /// <summary>
    /// The other trap: a row region drawn across the scrollbar column wins the hit test, so the thumb
    /// can never be grabbed. The regions must stop one column short whenever a scrollbar is showing.
    /// </summary>
    [Fact]
    public void RegisterRowHits_LeavesTheScrollbarColumnAlone()
    {
        var scrolling = NewOffsetList(itemCount: 100, col: 0, row: 0, width: 20, height: 8);
        var fitting = NewOffsetList(itemCount: 2, col: 0, row: 0, width: 20, height: 8);

        var a = new ClickableRegionTracker();
        a.BeginFrame();
        scrolling.RegisterRowHits(a, (i, _) => new HitResult.ListItemHit("row", i));

        var b = new ClickableRegionTracker();
        b.BeginFrame();
        fitting.RegisterRowHits(b, (i, _) => new HitResult.ListItemHit("row", i));

        var cell = scrolling.Viewport.CellSize;
        a.GetRegisteredRegions()[0].Width.ShouldBe(19 * cell.Width, "a scrolling list yields the last column to the scrollbar");
        b.GetRegisteredRegions()[0].Width.ShouldBe(20 * cell.Width, "a list that fits has no scrollbar, so rows span the full width");
    }

    /// <summary>A null hit leaves that row unclickable -- group headers and separators.</summary>
    [Fact]
    public void RegisterRowHits_SkipsRowsWithNoHit()
    {
        var list = NewOffsetList(itemCount: 6, col: 0, row: 0);
        var tracker = new ClickableRegionTracker();
        tracker.BeginFrame();

        list.RegisterRowHits(tracker, (i, _) => i % 2 == 0 ? new HitResult.ListItemHit("row", i) : null);

        tracker.GetRegisteredRegions().Length.ShouldBe(3);
    }

    /// <summary>The origin is the viewport OFFSET times cell size, not the viewport rect.</summary>
    [Fact]
    public void RegisterRowHits_OriginFollowsTheViewportOffset()
    {
        var list = NewOffsetList(itemCount: 3, col: 4, row: 2);
        var tracker = new ClickableRegionTracker();
        tracker.BeginFrame();

        list.RegisterRowHits(tracker, (i, _) => new HitResult.ListItemHit("row", i));

        var cell = list.Viewport.CellSize;
        var first = tracker.GetRegisteredRegions()[0];
        first.X.ShouldBe(4 * cell.Width);
        first.Y.ShouldBe((2 + 1) * cell.Height, "offset row plus the header row");
    }

    [Fact]
    public void RegisterRowHits_EmptyList_RegistersNothing()
    {
        var terminal = new FakeTerminal(new Queue<ConsoleInputEvent>(), 20, 8);
        var list = new ScrollableList<Row>(new TerminalViewport(terminal, 0, 0, 20, 8));
        var tracker = new ClickableRegionTracker();
        tracker.BeginFrame();

        list.RegisterRowHits(tracker, (i, _) => new HitResult.ListItemHit("row", i));

        tracker.GetRegisteredRegions().ShouldBeEmpty();
    }

    // ---- RegisterRowSpanHits ----

    [Fact]
    public void RegisterRowSpanHits_PlacesSpansAtTheirColumnOffsets()
    {
        var list = NewOffsetList(itemCount: 3, col: 0, row: 0);
        var tracker = new ClickableRegionTracker();
        tracker.BeginFrame();

        list.RegisterRowSpanHits(tracker, (i, _) => i == 0
            ? [new RowSpan(2, 6, new HitResult.ButtonHit("dec")), new RowSpan(8, 12, new HitResult.ButtonHit("inc"))]
            : []);

        var cell = list.Viewport.CellSize;
        var regions = tracker.GetRegisteredRegions();
        regions.Length.ShouldBe(2);
        regions[0].X.ShouldBe(2 * cell.Width);
        regions[0].Width.ShouldBe(4 * cell.Width);
        regions[1].X.ShouldBe(8 * cell.Width);
        // Header row displaces the first data row, exactly as for whole-row hits.
        regions[0].Y.ShouldBe(cell.Height);
    }

    /// <summary>
    /// A span running past the row is trimmed to the content width rather than overlapping the
    /// scrollbar -- so int.MaxValue is a usable "to the end of the row".
    /// </summary>
    [Fact]
    public void RegisterRowSpanHits_ClampsASpanToTheContentWidth()
    {
        var list = NewOffsetList(itemCount: 100, col: 0, row: 0, width: 20, height: 8);
        var tracker = new ClickableRegionTracker();
        tracker.BeginFrame();

        list.RegisterRowSpanHits(tracker, (i, _) => i == list.ScrollOffset
            ? [new RowSpan(0, int.MaxValue, new HitResult.ButtonHit("all"))]
            : []);

        var cell = list.Viewport.CellSize;
        // 20 columns minus the scrollbar column.
        tracker.GetRegisteredRegions()[0].Width.ShouldBe(19 * cell.Width);
    }

    /// <summary>
    /// Spans follow the scroll like whole rows do. The hand-rolled version this replaced hardcoded a
    /// zero scroll offset, which was correct only for as long as the list never scrolled.
    /// </summary>
    [Fact]
    public void RegisterRowSpanHits_FollowsTheScrollOffset()
    {
        var list = NewOffsetList(itemCount: 50, col: 0, row: 0);
        list.ScrollTo(12);
        var tracker = new ClickableRegionTracker();
        tracker.BeginFrame();

        var seen = new List<int>();
        list.RegisterRowSpanHits(tracker, (i, _) =>
        {
            seen.Add(i);
            return [new RowSpan(0, 4, new HitResult.ListItemHit("row", i))];
        });

        seen[0].ShouldBe(12, "the top visible row is the scrolled item, not item 0");
    }

    [Fact]
    public void RegisterRowSpanHits_SkipsDegenerateSpans()
    {
        var list = NewOffsetList(itemCount: 2, col: 0, row: 0);
        var tracker = new ClickableRegionTracker();
        tracker.BeginFrame();

        list.RegisterRowSpanHits(tracker, (_, _) =>
            [new RowSpan(5, 5, new HitResult.ButtonHit("empty")), new RowSpan(9, 3, new HitResult.ButtonHit("inverted"))]);

        tracker.GetRegisteredRegions().ShouldBeEmpty();
    }
}
