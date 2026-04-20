namespace Console.Lib;

/// <summary>
/// Multi-row scrollable list with a header row.
/// Each item implements <see cref="IRowFormatter"/> for its own row styling.
///
/// When the list overflows the viewport, the rightmost column becomes a
/// scrollbar — box-drawing vertical bar (track) with a solid-block thumb.
/// The formatter is passed <c>width - 1</c> so content never writes under the
/// track. <see cref="HandleMouse"/> dispatches click + drag against the
/// track/thumb; callers route <see cref="MouseEvent"/>s through it before
/// falling back to their own hit-testing.
/// </summary>
public class ScrollableList<TItem>(ITerminalViewport viewport) : Widget(viewport) where TItem : IRowFormatter
{
    private IReadOnlyList<TItem> _items = [];
    private int _scrollOffset;
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

    private int HeaderRows => _header.Length > 0 ? 1 : 0;

    public ScrollableList<TItem> Items(IReadOnlyList<TItem> items)
    {
        _items = items;
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
    /// Dispatches a mouse event to the scrollbar. Returns <c>true</c> when the event
    /// was consumed (click or drag over the scrollbar column), otherwise <c>false</c>
    /// so the caller can continue its own hit-testing.
    ///
    /// Left-button press in the track above the thumb pages up; below pages down;
    /// on the thumb starts a drag. Drag motion updates the offset proportionally;
    /// release ends the drag. Wheel (button 64/65) is intentionally not handled
    /// here — callers already have wheel hooks tied to their own semantics.
    /// </summary>
    public bool HandleMouse(MouseEvent mouse)
    {
        // End-of-drag on any release, even outside the track, so a fast flick
        // doesn't leave us stuck in drag state.
        if (mouse.IsRelease)
        {
            var wasDragging = _isDragging;
            _isDragging = false;
            return wasDragging;
        }

        if (!HasScrollBar) return false;

        // Motion without a held button is ignored in mode 1002, but guard anyway.
        var isLeftButton = mouse.Button == 0;
        if (!isLeftButton) return false;

        // Drag: keep consuming motion regardless of whether the cursor is still over
        // the widget. Desktop scrollbar convention — once you grab the thumb, the drag
        // continues until release. Use raw pixel Y rather than Widget.HitTest so we
        // also respond when the user drifts off the list.
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
        if (col != lastCol) return false; // click outside the track column

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
        if (width <= 0 || height <= 0) return;

        var colorMode = Viewport.ColorMode;
        var contentWidth = HasScrollBar ? width - 1 : width;
        var (thumbTop, thumbHeight) = HasScrollBar ? ComputeThumb() : (0, 0);

        var row = 0;
        if (HeaderRows > 0)
        {
            if (!TrySetCursorPosition(Viewport, 0, row)) return;
            Viewport.Write($"{_headerStyle.Apply(colorMode)}{_header.PadRight(width)}{VtStyle.Reset}");
            row++;
        }

        for (; row < height; row++)
        {
            if (!TrySetCursorPosition(Viewport, 0, row)) return;

            var dataRow = row - HeaderRows;
            var itemIdx = _scrollOffset + dataRow;
            if (itemIdx >= 0 && itemIdx < _items.Count)
            {
                Viewport.Write(_items[itemIdx].FormatRow(contentWidth, colorMode));
            }
            else
            {
                Viewport.Write($"{_emptyStyle.Apply(colorMode)}{new string(' ', contentWidth)}{VtStyle.Reset}");
            }

            if (HasScrollBar)
            {
                var onThumb = dataRow >= thumbTop && dataRow < thumbTop + thumbHeight;
                var style = onThumb ? _thumbStyle : _scrollBarStyle;
                var glyph = onThumb ? '\u2588' : '\u2502'; // █ on thumb, │ on track
                Viewport.Write($"{style.Apply(colorMode)}{glyph}{VtStyle.Reset}");
            }
        }
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
