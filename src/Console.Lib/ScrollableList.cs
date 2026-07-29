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
public class ScrollableList<TItem>(ITerminalViewport viewport) : Widget(viewport) where TItem : IRowLayout
{
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
    private int _scrollOffset;
    private int _cursor;                // -1 when the list is empty; index into _items otherwise.
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
    public int ScrollOffset => _scrollOffset;

    /// <summary>Total item count (read-only snapshot).</summary>
    public int ItemCount => _items.Count;

    /// <summary>
    /// Index of the cursor row, or <c>-1</c> when the list is empty. The cursor
    /// is always within <c>[0, ItemCount)</c> when there is at least one item;
    /// changing <see cref="Items"/> clamps it. Mirrors <see cref="TreeView{T}.CursorIndex"/>.
    /// </summary>
    public int CursorIndex => _items.Count == 0 ? -1 : _cursor;

    /// <summary>
    /// Currently-selected item, or <c>default</c> when the list is empty.
    /// </summary>
    public TItem? Selected => _items.Count > 0 && _cursor >= 0 && _cursor < _items.Count
        ? _items[_cursor]
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
        if (_cursor >= _items.Count) _cursor = Math.Max(0, _items.Count - 1);
        if (_cursor < 0) _cursor = 0;
        ClampOffset();
        return this;
    }

    public ScrollableList<TItem> ScrollTo(int offset)
    {
        _scrollOffset = offset;
        ClampOffset();
        return this;
    }

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
        var nextRow = Math.Clamp(_cursor + rowDelta, 0, _items.Count - 1);
        var nextCol = Math.Clamp(_columnIndex + colDelta, 0, _columns - 1);
        if (nextRow == _cursor && nextCol == _columnIndex) return false;
        _cursor = nextRow;
        _columnIndex = nextCol;
        EnsureCursorVisible();
        return true;
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
        if (idx == _cursor) return false;
        _cursor = idx;
        EnsureCursorVisible();
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

    private void EnsureCursorVisible()
    {
        if (_cursor < 0 || VisibleRows <= 0) return;
        if (_cursor < _scrollOffset) _scrollOffset = _cursor;
        else if (_cursor >= _scrollOffset + VisibleRows) _scrollOffset = _cursor - VisibleRows + 1;
        ClampOffset();
    }

    /// <summary>
    /// Nudges the scroll offset just enough so that <paramref name="itemIndex"/>
    /// is visible. No-op when the item is already in view — this lets mouse-driven
    /// scroll coexist with keyboard selection without snapping back to a computed
    /// "center".
    /// </summary>
    public ScrollableList<TItem> EnsureVisible(int itemIndex)
    {
        if (itemIndex < 0 || itemIndex >= _items.Count || VisibleRows <= 0) return this;
        if (itemIndex < _scrollOffset) _scrollOffset = itemIndex;
        else if (itemIndex >= _scrollOffset + VisibleRows) _scrollOffset = itemIndex - VisibleRows + 1;
        ClampOffset();
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
        var before = _scrollOffset;
        _scrollOffset -= delta;
        ClampOffset();
        return _scrollOffset != before;
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
            _scrollOffset = (int)Math.Round((double)newThumbTop * maxOffset / trackUsable);
            ClampOffset();
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
            var clickedIdx = _scrollOffset + (row - HeaderRows);
            if (clickedIdx < 0 || clickedIdx >= _items.Count) return false;
            var contentWidth = HasScrollBar ? Viewport.Size.Width - 1 : Viewport.Size.Width;
            var newCol = _columns > 1 && contentWidth > 0
                ? Math.Clamp(col * _columns / contentWidth, 0, _columns - 1)
                : 0;
            if (clickedIdx == _cursor && newCol == _columnIndex) return false;
            _cursor = clickedIdx;
            _columnIndex = newCol;
            EnsureCursorVisible();
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
            _dragStartOffset = _scrollOffset;
        }
        else if (clickDataRow < thumbTop)
        {
            // Track above the thumb → page up.
            _scrollOffset -= VisibleRows;
            ClampOffset();
        }
        else
        {
            // Track below the thumb → page down.
            _scrollOffset += VisibleRows;
            ClampOffset();
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
            var itemIdx = _scrollOffset + dataRow;
            if (itemIdx >= 0 && itemIdx < _items.Count)
            {
                // Arranged at the row it is painted on, at the width it is painted at, and then KEPT --
                // so the region a click resolves against is the very rect that was drawn. CellLayout
                // positions its own writes, hence no TrySetCursorPosition on this branch.
                var sel = itemIdx == _cursor;
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

        var itemIndex = _scrollOffset + (row - HeaderRows);
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
        var thumbTop = maxOffset > 0 ? trackUsable * _scrollOffset / maxOffset : 0;
        return (thumbTop, thumbH);
    }

    private void ClampOffset()
    {
        var max = Math.Max(0, _items.Count - VisibleRows);
        if (_scrollOffset < 0) _scrollOffset = 0;
        else if (_scrollOffset > max) _scrollOffset = max;
    }
}
