using System.Collections.Generic;
using System.Collections.Immutable;
using DIR.Lib;

namespace Console.Lib;

/// <summary>
/// Multi-row scrollable list with a header row.
/// Each item implements <see cref="IRowLayout"/> to build its row as a layout tree.
///
/// When the list overflows the viewport, the rightmost column becomes a
/// scrollbar — box-drawing vertical bar (track) with a solid-block thumb.
/// Rows are arranged into the content width (which excludes the track), so content
/// can never write under it. <see cref="HandleMouse"/> dispatches click + drag against the
/// track/thumb and moves the cursor; <see cref="DispatchRowHit"/> resolves a click against the
/// arranged row trees for inline buttons.
/// </summary>
public class ScrollableList<TItem> : Widget where TItem : IRowLayout
{
    /// <summary>
    /// The list the cursor is opened in. One id per instance rather than per consumer: a
    /// <see cref="ListCursor"/> is in exactly one list, this widget IS that list, and nothing outside
    /// resolves a hit against the name.
    /// </summary>
    public const string CursorListId = "ScrollableList";

    public ScrollableList(ITerminalViewport viewport) : base(viewport)
    {
        // Stated once instead of at every mover. A step that lands on a row the window does not show has
        // to bring it into view, and the cursor is the only thing that knows the step happened -- which
        // is what ListCursor.Moved exists for. Four call sites used to remember to call EnsureVisible
        // afterwards, and the one that forgot would have parked the highlight off screen.
        _cursor.Moved += index => _scroll.EnsureVisible(index);
    }

    // Every visible row's arranged tree, concatenated, in VIEWPORT-RELATIVE cell coordinates -- which is
    // what makes DispatchRowHit correct by construction rather than by parallel arithmetic. Each row was
    // arranged at the row it was actually painted on, so the header offset and the scroll offset are
    // already folded in, and the content width already excludes the scrollbar column. The four ways the
    // old RegisterRowHits/RegisterRowSpanHits helpers could silently disagree with the paint (offset
    // origin, header row, scrolled index, scrollbar column) are therefore not expressible here.
    private ImmutableArray<Layout.ArrangedNode<int>> _arrangedRows = [];

    /// <summary>The unit convention row trees are authored in. Cells by default (<c>RowH(1)</c> = one row);
    /// override with <see cref="CellMeasureContext.PixelAuthored"/> for a tree shared with a GPU surface.</summary>
    protected virtual CellMeasureContext MeasureContext => CellMeasureContext.CellAuthored;

    private IReadOnlyList<TItem> _items = [];

    /// <summary>
    /// Where the keyboard is, and the arrow walk that moves it -- DIR.Lib's, so a list steps the same way
    /// here as on a GPU surface. This class used to carry its own index and its own clamping walk beside
    /// the engine's, which is two answers to "where does Down go" with nothing comparing them.
    /// </summary>
    private readonly ListCursor _cursor = new();

    /// <summary>
    /// The scroll position, also DIR.Lib's. A cell is the atom, so <c>atomExtentPx</c> is 1 and
    /// <see cref="ListScrollController.AtomOffset"/> IS the index of the first visible item -- the same
    /// number the hand-written offset held, with the clamp and the minimal ensure-visible coming from the
    /// engine rather than from a second copy of each.
    /// </summary>
    /// <remarks>
    /// <see cref="ScrollBarMode.None"/> because the bar is drawn HERE, in box-drawing characters against
    /// a cell grid, which the controller's pixel-rect painter cannot express. What is shared is the model
    /// the bar reports, not the drawing.
    /// </remarks>
    private readonly ListScrollController _scroll = new()
    {
        SnapToAtom = true,
        Mode = ScrollBarMode.None,
    };

    // Scratch for the cursor walk: the window of rows this list shows, rebuilt per move. Held by Step for
    // the duration of the walk, so nothing reached from ListCursor.Moved may rebuild it -- today that is
    // EnsureVisible alone, which moves the offset and touches no rows.
    private readonly List<ListCursor.PaintedRow> _paintedRows = [];

    private int _columns = 1;           // Sub-cells per row. 1 = legacy single-column behavior.
    private int _columnIndex;           // Cursor column in [0, _columns).
    private string _header = "";
    private VtStyle _headerStyle = new(SgrColor.BrightWhite, SgrColor.BrightBlack);
    private VtStyle _emptyStyle = new(SgrColor.White, SgrColor.Black);
    private VtStyle _scrollBarStyle = new(SgrColor.BrightBlack, SgrColor.Black);
    private VtStyle _thumbStyle = new(SgrColor.BrightWhite, SgrColor.Black);

    // Drag state — set on left-button press inside the thumb, cleared on release.
    private bool _isDragging;
    private int _dragStartRow;          // data-row (0-based, relative to data area) where press landed
    private int _dragStartOffset;       // scroll offset at press time

    /// <summary>Number of data rows visible (excluding header).</summary>
    public int VisibleRows => Math.Max(0, Viewport.Size.Height - HeaderRows);

    /// <summary>Current scroll offset (index of the first visible item).</summary>
    public int ScrollOffset => _scroll.AtomOffset;

    /// <summary>Total item count (read-only snapshot).</summary>
    public int ItemCount => _items.Count;

    /// <summary>
    /// Index of the cursor row, or <c>-1</c> when the list is empty. The cursor
    /// is always within <c>[0, ItemCount)</c> when there is at least one item;
    /// changing <see cref="Items"/> clamps it. Mirrors <see cref="TreeView{T}.CursorIndex"/>.
    /// </summary>
    public int CursorIndex => _items.Count == 0 ? -1 : _cursor.Index;

    /// <summary>
    /// Currently-selected item, or <c>default</c> when the list is empty.
    /// </summary>
    public TItem? Selected => _items.Count > 0 && _cursor.Index >= 0 && _cursor.Index < _items.Count
        ? _items[_cursor.Index]
        : default;

    /// <summary>
    /// Number of selectable sub-cells per row. Default <c>1</c> (legacy single-cell rows).
    /// Set via <see cref="Columns(int)"/>; consumed by <see cref="HandleKey"/> (Left/Right
    /// arms), the mouse click handler, and the <see cref="RowContext"/> handed to each row.
    /// </summary>
    public int ColumnCount => _columns;

    /// <summary>
    /// Cursor column in <c>[0, ColumnCount)</c>, or <c>-1</c> when the list is empty.
    /// Always <c>0</c> in the default single-column mode.
    /// </summary>
    public int ColumnIndex => _items.Count == 0 ? -1 : _columnIndex;

    private int HeaderRows => _header.Length > 0 ? 1 : 0;

    public ScrollableList<TItem> Items(IReadOnlyList<TItem> items)
    {
        _items = items;
        // Re-opened rather than nudged: Open states the row COUNT with the index, and the count is what
        // lets the walk step past the window this list shows -- a cursor that cannot reach row 20 of a
        // list showing 5 stops dead at the bottom and the list is mouse-only from there.
        _cursor.Open(CursorListId, ClampCursor(_cursor.Index, items.Count), items.Count);
        SyncExtent();
        return this;
    }

    public ScrollableList<TItem> ScrollTo(int offset)
    {
        SyncExtent();               // AtomOffset clamps against the CURRENT geometry, so state it first
        _scroll.AtomOffset = offset;
        return this;
    }

    /// <summary>
    /// Where the cursor sits in a list of <paramref name="count"/>: -1 for an empty one, and otherwise
    /// inside its bounds, with a cursor that was nowhere landing on the first row.
    /// </summary>
    private static int ClampCursor(int index, int count)
        => count == 0 ? -1 : Math.Clamp(index < 0 ? 0 : index, 0, count - 1);

    /// <summary>
    /// Set the number of selectable sub-cells per row. Default is <c>1</c>.
    /// Values greater than <c>1</c> opt the list into "multi-column" mode:
    /// <see cref="HandleKey"/> grows Left/Right arms, mouse clicks resolve to a
    /// (row, column) pair via even-split of the row width, and each row's
    /// <see cref="RowContext"/> carries the cursor column. Throws when <paramref name="n"/> is less than 1;
    /// clamps the current column index to <c>[0, n)</c>.
    /// </summary>
    public ScrollableList<TItem> Columns(int n)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(n, 1);
        _columns = n;
        if (_columnIndex >= n) _columnIndex = n - 1;
        if (_columnIndex < 0) _columnIndex = 0;
        return this;
    }

    /// <summary>
    /// Move the cursor by <paramref name="delta"/> rows, clamping at the list
    /// boundaries. The scroll offset follows so the cursor row stays visible.
    /// Returns <c>true</c> when the cursor actually moved.
    /// </summary>
    public bool MoveCursor(int delta) => MoveCursor(delta, 0);

    /// <summary>
    /// Move the cursor by <paramref name="rowDelta"/> rows and
    /// <paramref name="colDelta"/> sub-columns, clamping each axis at its
    /// boundaries. The scroll offset follows so the cursor row stays visible.
    /// Returns <c>true</c> when the cursor actually moved on either axis.
    /// </summary>
    public bool MoveCursor(int rowDelta, int colDelta)
    {
        if (_items.Count == 0) return false;

        // The row axis is the engine's walk; the column is this widget's, there being no column in the
        // shared model. Both are attempted, and either moving counts -- a diagonal step that can only go
        // one way should still go that way.
        var movedRow = _cursor.Step(rowDelta, PaintedRows());

        var nextCol = Math.Clamp(_columnIndex + colDelta, 0, _columns - 1);
        var movedCol = nextCol != _columnIndex;
        _columnIndex = nextCol;

        return movedRow || movedCol;
    }

    /// <summary>
    /// Move the cursor column by <paramref name="delta"/>, clamping at
    /// <c>[0, ColumnCount)</c>. No-op in single-column mode.
    /// Returns <c>true</c> when the column actually moved.
    /// </summary>
    public bool MoveColumn(int delta)
    {
        if (_items.Count == 0 || _columns <= 1) return false;
        var next = Math.Clamp(_columnIndex + delta, 0, _columns - 1);
        if (next == _columnIndex) return false;
        _columnIndex = next;
        return true;
    }

    /// <summary>
    /// Move the cursor to <paramref name="idx"/> (clamped to the list bounds).
    /// Pass <c>int.MaxValue</c> to jump to the last item. Returns <c>true</c>
    /// when the cursor actually moved.
    /// </summary>
    public bool MoveTo(int idx)
    {
        if (_items.Count == 0) return false;
        idx = Math.Clamp(idx, 0, _items.Count - 1);
        if (idx == _cursor.Index) return false;
        _cursor.MoveTo(idx);        // raises Moved, which is what brings the row into view
        return true;
    }

    /// <summary>
    /// Handles a key for this list. Returns <c>true</c> when the event changed
    /// state (cursor / scroll) and a re-render is needed. Mirrors the key map
    /// used by <see cref="TreeView{T}.HandleKey"/>: ↑/↓, PageUp/PageDown, Home,
    /// End. When <see cref="ColumnCount"/> is greater than one, Left/Right move
    /// the cursor column. Unknown keys return <c>false</c> so the caller can
    /// fall through.
    /// </summary>
    public bool HandleKey(ConsoleKey key, ConsoleModifiers _ = 0) => key switch
    {
        ConsoleKey.UpArrow    => MoveCursor(-1),
        ConsoleKey.DownArrow  => MoveCursor(+1),
        ConsoleKey.PageUp     => MoveCursor(-Math.Max(1, VisibleRows - 1)),
        ConsoleKey.PageDown   => MoveCursor(+Math.Max(1, VisibleRows - 1)),
        ConsoleKey.Home       => MoveTo(0),
        ConsoleKey.End        => MoveTo(int.MaxValue),
        ConsoleKey.LeftArrow  => MoveColumn(-1),
        ConsoleKey.RightArrow => MoveColumn(+1),
        _                     => false,
    };

    /// <summary>
    /// The window of rows this list shows, which is the evidence the cursor walk resolves against -- the
    /// cell-surface answer to what a pixel widget reads off its registered regions.
    /// </summary>
    /// <remarks>
    /// Derived from the CURRENT offset rather than from what the last <see cref="Render"/> drew, so a list
    /// that has never been painted is still navigable: a consumer wiring up its keys before its first
    /// frame would otherwise find the arrows dead, which is not a rule anybody asked for. At least one row
    /// of evidence even in a viewport with no room, for the same reason -- a list with nowhere to draw
    /// still has a selection, and the caller sees it through <see cref="CursorIndex"/>.
    /// </remarks>
    private IReadOnlyList<ListCursor.PaintedRow> PaintedRows()
    {
        _paintedRows.Clear();
        var first = _scroll.AtomOffset;
        var last = Math.Min(_items.Count, first + Math.Max(1, VisibleRows));
        for (var i = first; i < last; i++)
        {
            _paintedRows.Add(new ListCursor.PaintedRow(i));
        }

        return _paintedRows;
    }

    /// <summary>
    /// Nudges the scroll offset just enough so that <paramref name="itemIndex"/>
    /// is visible. No-op when the item is already in view — this lets mouse-driven
    /// scroll coexist with keyboard selection without snapping back to a computed
    /// "center".
    /// </summary>
    public ScrollableList<TItem> EnsureVisible(int itemIndex)
    {
        if (VisibleRows <= 0) return this;
        SyncExtent();
        _scroll.EnsureVisible(itemIndex);
        return this;
    }

    public ScrollableList<TItem> Header(string text) { _header = text; return this; }
    public ScrollableList<TItem> HeaderStyle(VtStyle style) { _headerStyle = style; return this; }
    public ScrollableList<TItem> EmptyStyle(VtStyle style) { _emptyStyle = style; return this; }
    public ScrollableList<TItem> ScrollBarStyle(VtStyle style) { _scrollBarStyle = style; return this; }
    public ScrollableList<TItem> ThumbStyle(VtStyle style) { _thumbStyle = style; return this; }

    /// <summary>
    /// When <c>true</c>, <see cref="HandleMouse"/> auto-routes wheel events
    /// (button 64 = up, 65 = down) into <see cref="HandleWheel"/> at the
    /// <see cref="WheelStep"/> rate. Default <c>false</c> for backward
    /// compatibility — hosts that want different semantics (e.g. wheel = zoom)
    /// keep doing their own dispatch. Opt-in lets the common case stop
    /// boilerplating a button-64/65 branch in every consumer.
    /// </summary>
    public bool AutoHandleWheel { get; set; }

    /// <summary>
    /// Rows scrolled per wheel notch when <see cref="AutoHandleWheel"/> is
    /// <c>true</c>. Default <c>3</c>, matching typical desktop conventions.
    /// </summary>
    public int WheelStep { get; set; } = 3;

    /// <summary>
    /// Scrolls the list by <paramref name="delta"/> rows. Positive <paramref name="delta"/>
    /// scrolls up (toward the list start). Returns <c>true</c> when the offset actually
    /// changed, <c>false</c> at either end or when the list fits in the viewport.
    /// </summary>
    public bool HandleWheel(int delta)
    {
        if (!HasScrollBar) return false;
        SyncExtent();
        var before = _scroll.AtomOffset;
        _scroll.AtomOffset = before - delta;
        return _scroll.AtomOffset != before;
    }

    /// <summary>
    /// Dispatches a mouse event to the scrollbar. Returns <c>true</c> when the event
    /// was consumed (click or drag over the scrollbar column), otherwise <c>false</c>
    /// so the caller can continue its own hit-testing.
    ///
    /// Left-button press in the track above the thumb pages up; below pages down;
    /// on the thumb starts a drag. Drag motion updates the offset proportionally;
    /// release ends the drag. Wheel (button 64/65) is routed to
    /// <see cref="HandleWheel"/> when <see cref="AutoHandleWheel"/> is set;
    /// otherwise wheel events fall through so the caller can attach its own
    /// semantics.
    /// </summary>
    public bool HandleMouse(MouseEvent mouse)
    {
        // Wheel auto-routing (opt-in). Button 64 = up, 65 = down. Wheel events
        // never carry IsRelease/IsMotion so they're safe to peel off first.
        if (AutoHandleWheel && mouse.Button is 64 or 65)
            return HandleWheel(mouse.Button == 64 ? WheelStep : -WheelStep);

        // End-of-drag on any release, even outside the track, so a fast flick
        // doesn't leave us stuck in drag state.
        if (mouse.IsRelease)
        {
            var wasDragging = _isDragging;
            _isDragging = false;
            return wasDragging;
        }

        // Motion without a held button is ignored in mode 1002, but guard anyway.
        var isLeftButton = mouse.Button == 0;
        if (!isLeftButton) return false;

        // Drag: keep consuming motion regardless of whether the cursor is still over
        // the widget. Desktop scrollbar convention — once you grab the thumb, the drag
        // continues until release. _isDragging is only set when HasScrollBar was true
        // at press time, so this branch implicitly requires a scrollbar.
        if (mouse.IsMotion && _isDragging)
        {
            var cellH = Viewport.CellSize.Height;
            if (cellH <= 0) return true;
            var rawRow = mouse.Y / cellH - Viewport.Offset.Row;
            var dataRow = rawRow - HeaderRows;
            var (_, thumbHeight) = ComputeThumb();
            var trackUsable = Math.Max(1, VisibleRows - thumbHeight);
            var maxOffset = Math.Max(1, _items.Count - VisibleRows);

            // Absolute positioning: compute where the thumb top should be so the user's
            // grip point within the thumb tracks the cursor. Using absolute math (rather
            // than accumulated deltas) ensures the endpoints 0 and maxOffset are reachable
            // regardless of integer-division truncation along the way.
            var thumbTopAtStart = (int)((long)_dragStartOffset * trackUsable / maxOffset);
            var grip = _dragStartRow - thumbTopAtStart;
            var newThumbTop = Math.Clamp(dataRow - grip, 0, trackUsable);
            SyncExtent();
            _scroll.AtomOffset = (int)Math.Round((double)newThumbTop * maxOffset / trackUsable);
            return true;
        }

        if (HitTest(mouse.X, mouse.Y) is not (var col, var row)) return false;

        var lastCol = Viewport.Size.Width - 1;
        // Without a scrollbar the entire viewport is content, so every column
        // routes to the content-click branch. With a scrollbar, only the
        // last column is the track.
        if (!HasScrollBar || col != lastCol)
        {
            // Click on a content row → move the cursor there. Header row click
            // is consumed but ignored (no sort behavior yet). Motion without a
            // drag is dropped. In multi-column mode the click also picks a
            // sub-column via even-split of the content area.
            if (mouse.IsMotion) return false;
            if (row < HeaderRows) return false;
            var clickedIdx = _scroll.AtomOffset + (row - HeaderRows);
            if (clickedIdx < 0 || clickedIdx >= _items.Count) return false;
            var contentWidth = HasScrollBar ? Viewport.Size.Width - 1 : Viewport.Size.Width;
            var newCol = _columns > 1 && contentWidth > 0
                ? Math.Clamp(col * _columns / contentWidth, 0, _columns - 1)
                : 0;
            if (clickedIdx == _cursor.Index && newCol == _columnIndex) return false;
            _cursor.MoveTo(clickedIdx);
            _columnIndex = newCol;
            return true;
        }

        var clickDataRow = row - HeaderRows;
        if (clickDataRow < 0) return false;

        var (thumbTop, thumbH) = ComputeThumb();

        if (mouse.IsMotion) return false; // motion without drag — ignore

        // Fresh press in the scrollbar column.
        if (clickDataRow >= thumbTop && clickDataRow < thumbTop + thumbH)
        {
            // On the thumb → start drag.
            _isDragging = true;
            _dragStartRow = clickDataRow;
            _dragStartOffset = _scroll.AtomOffset;
        }
        else if (clickDataRow < thumbTop)
        {
            // Track above the thumb → page up.
            SyncExtent();
            _scroll.AtomOffset -= VisibleRows;
        }
        else
        {
            // Track below the thumb → page down.
            SyncExtent();
            _scroll.AtomOffset += VisibleRows;
        }
        return true;
    }

    public override void Render()
    {
        var (width, height) = Viewport.Size;
        if (width <= 0 || height <= 0)
        {
            _arrangedRows = [];
            return;
        }

        SyncExtent();
        var colorMode = Viewport.ColorMode;
        var contentWidth = HasScrollBar ? width - 1 : width;
        var (thumbTop, thumbHeight) = HasScrollBar ? ComputeThumb() : (0, 0);
        var measureCtx = MeasureContext;
        var rowTrees = ImmutableArray.CreateBuilder<Layout.ArrangedNode<int>>();

        var row = 0;
        if (HeaderRows > 0)
        {
            if (!TrySetCursorPosition(Viewport, 0, row)) return;
            Viewport.Write($"{_headerStyle.Apply(colorMode)}{_header.PadRight(width)}{VtStyle.Reset}");
            row++;
        }

        for (; row < height; row++)
        {
            var dataRow = row - HeaderRows;
            var itemIdx = _scroll.AtomOffset + dataRow;
            if (itemIdx >= 0 && itemIdx < _items.Count)
            {
                // Arranged at the row it is painted on, at the width it is painted at, and then KEPT --
                // so the region a click resolves against is the very rect that was drawn. CellLayout
                // positions its own writes, hence no TrySetCursorPosition on this branch.
                var sel = itemIdx == _cursor.Index;
                var context = new RowContext(sel, sel ? _columnIndex : -1, _columns);
                var arranged = Layout.Engine.Arrange(
                    _items[itemIdx].BuildRow(context),
                    new Rect<int>(0, row, contentWidth, 1),
                    measureCtx);

                CellLayout.Paint(Viewport, arranged);
                rowTrees.AddRange(arranged);
            }
            else
            {
                if (!TrySetCursorPosition(Viewport, 0, row)) return;
                Viewport.Write($"{_emptyStyle.Apply(colorMode)}{new string(' ', contentWidth)}{VtStyle.Reset}");
            }

            if (HasScrollBar)
            {
                if (!TrySetCursorPosition(Viewport, contentWidth, row)) return;
                var onThumb = dataRow >= thumbTop && dataRow < thumbTop + thumbHeight;
                var style = onThumb ? _thumbStyle : _scrollBarStyle;
                var glyph = onThumb ? '█' : '│'; // block on thumb, light vertical on track
                Viewport.Write($"{style.Apply(colorMode)}{glyph}{VtStyle.Reset}");
            }
        }

        _arrangedRows = rowTrees.ToImmutable();
    }

    /// <summary>
    /// Resolves a MOUSE-PIXEL point against the arranged row trees, invoking the matched leaf's
    /// <c>OnClick</c> and returning its hit -- for inline buttons ON a row (a delete affordance, a
    /// toggle). Null when nothing clickable sits under the point.
    /// <para>
    /// Cursor movement is NOT this method's job: <see cref="HandleMouse"/> already moves the cursor on a
    /// press, so a host wanting "clicking a row selects it, and selecting has a side effect" acts on
    /// <see cref="HandleMouse"/> returning true. This resolves only leaves that claimed a hit.
    /// </para>
    /// <para>
    /// The scrollbar column is excluded, so a click on the track can never dispatch a row's button; and
    /// because each row was arranged where it was painted, a SCROLLED list resolves to the item actually
    /// under the cursor rather than to the visible-row index.
    /// </para>
    /// </summary>
    public HitResult? DispatchRowHit(int pixelX, int pixelY, InputModifier modifiers = InputModifier.None)
    {
        if (_arrangedRows.IsDefaultOrEmpty || HitTest(pixelX, pixelY) is not (var column, var row))
        {
            return null;
        }

        var contentColumns = HasScrollBar ? Viewport.Size.Width - 1 : Viewport.Size.Width;
        return column >= contentColumns ? null : CellLayout.HitTest(_arrangedRows, column, row, modifiers);
    }

    /// <summary>
    /// The visible rows as last arranged, concatenated in paint order, in viewport-relative cell
    /// coordinates. For tests that assert drawn geometry, and for a host that owns its own dispatch.
    /// </summary>
    public ImmutableArray<Layout.ArrangedNode<int>> ArrangedRows => _arrangedRows;

    /// <summary>
    /// Resolves a pixel point to the row under it: the ITEM index (not the visible row), the item, and the
    /// column <b>within the content area</b> together with how many content columns there are.
    ///
    /// <para>For a host that needs the ITEM behind a point -- a context menu, a drag source, a hover
    /// tooltip. It is not the way to reach a row's inline buttons: those live on the row's own tree, so
    /// they resolve through <see cref="DispatchRowHit"/>, and nothing here has to know their columns.</para>
    ///
    /// <para>Null when the point falls outside the viewport, on the header, on the scrollbar column, or
    /// past the last item. That third one is the reason this exists rather than being left to callers:
    /// <see cref="Widget.HitTest"/> reports every column including the scrollbar's, so a host splitting a
    /// row into fields by <c>Viewport.Size.Width</c> silently treats clicks on the track as content.</para>
    ///
    /// <para>Reporting the content width alongside the column keeps the caller out of that trap without
    /// having to know whether a scrollbar is showing — which depends on the item count, so it changes
    /// under the caller's feet.</para>
    /// </summary>
    public (int ItemIndex, TItem Item, int Column, int Columns)? HitTestRow(int pixelX, int pixelY)
    {
        if (HitTest(pixelX, pixelY) is not (var column, var row))
        {
            return null;
        }

        var contentColumns = HasScrollBar ? Viewport.Size.Width - 1 : Viewport.Size.Width;
        if (column >= contentColumns || row < HeaderRows)
        {
            return null;
        }

        var itemIndex = _scroll.AtomOffset + (row - HeaderRows);
        return itemIndex >= 0 && itemIndex < _items.Count
            ? (itemIndex, _items[itemIndex], column, contentColumns)
            : null;
    }

    private bool HasScrollBar => _items.Count > VisibleRows && VisibleRows > 0;

    private (int ThumbTop, int ThumbHeight) ComputeThumb()
    {
        var total = _items.Count;
        if (total <= VisibleRows || VisibleRows <= 0) return (0, 0);

        var thumbH = Math.Max(1, VisibleRows * VisibleRows / total);
        var maxOffset = total - VisibleRows;
        var trackUsable = VisibleRows - thumbH;
        var thumbTop = maxOffset > 0 ? trackUsable * _scroll.AtomOffset / maxOffset : 0;
        return (thumbTop, thumbH);
    }

    /// <summary>
    /// States this frame's geometry on the scroll model: a cell is the atom, so the atom extent is 1 and
    /// the viewport is the data area in cells. Re-clamps the offset and raises nothing, which is what
    /// lets it be called from every mutator rather than only from the paint.
    /// </summary>
    /// <remarks>
    /// This is what the hand-written <c>ClampOffset</c> was, with the difference that the geometry is now
    /// STATED rather than recomputed at each reader -- <see cref="ListScrollController.MaxOffset"/>,
    /// <see cref="ListScrollController.EnsureVisible"/> and the clamp all read the one extent, so they
    /// cannot disagree about how many rows fit.
    /// </remarks>
    private void SyncExtent()
    {
        var rows = VisibleRows;
        var width = Math.Max(1, HasScrollBar ? Viewport.Size.Width - 1 : Viewport.Size.Width);
        _scroll.SetExtent(new RectF32(0f, HeaderRows, width, Math.Max(1, rows)), 1f, _items.Count,
            DesignScale.One);
    }
}
