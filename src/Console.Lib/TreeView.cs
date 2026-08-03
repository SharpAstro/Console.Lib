using System.Collections.Immutable;
using DIR.Lib;

namespace Console.Lib;

/// <summary>
/// Tree widget with a single header row, vertical scroll + scrollbar, a movable
/// cursor (selected row), and expand/collapse per node. The widget walks the
/// tree on demand to materialise the visible rows; nodes only need to expose
/// their immediate children via <see cref="ITreeNode{TSelf}"/>. Lazy population
/// is supported via <see cref="ITreeNode{TSelf}.EnsureChildrenLoaded"/>.
///
/// Coordinate model:
///   row 0 (when header set): column header
///   row 1..end-1            : data rows = visible[scrollOffset + (row-1)]
///   col 0..width-2          : indent (depth*2) + twirl(2) + content
///   col width-1             : scrollbar (only when overflow)
///
/// Scrollbar drag/page semantics mirror <see cref="ScrollableList{T}"/>; if you
/// change the model there, mirror it here.
/// </summary>
public sealed class TreeView<TItem> : Widget where TItem : class, ITreeNode<TItem>
{
    private TItem? _root;
    private readonly HashSet<TItem> _expanded = new(ReferenceEqualityComparer.Instance);
    private readonly List<(TItem Item, int Depth)> _visible = new();
    private bool _visibleStale = true;

    private int _scrollOffset;
    private int _cursor;                    // index into _visible

    private string _header = "";
    private VtStyle _headerStyle    = new(SgrColor.BrightWhite, SgrColor.BrightBlack);
    private VtStyle _emptyStyle     = new(SgrColor.White,       SgrColor.Black);
    private VtStyle _scrollBarStyle = new(SgrColor.BrightBlack, SgrColor.Black);
    private VtStyle _thumbStyle     = new(SgrColor.BrightWhite, SgrColor.Black);
    private VtStyle _twirlStyle     = new(SgrColor.BrightBlack, SgrColor.Black);
    private VtStyle _twirlSelStyle  = new(SgrColor.BrightYellow, SgrColor.Black);

    // Scrollbar drag — same model as ScrollableList<T>.
    private bool _isDragging;
    private int _dragStartRow;
    private int _dragStartOffset;

    public TreeView(ITerminalViewport vp) : base(vp) { }

    /// <summary>Number of data rows visible (excluding header).</summary>
    public int VisibleRows => Math.Max(0, Viewport.Size.Height - HeaderRows);

    /// <summary>Current scroll offset (index of the first visible item).</summary>
    public int ScrollOffset => _scrollOffset;

    /// <summary>Current cursor index (into the flattened visible-list).</summary>
    public int CursorIndex => _cursor;

    /// <summary>Total visible-after-expansion item count.</summary>
    public int ItemCount { get { EnsureVisible(); return _visible.Count; } }

    /// <summary>Currently-selected node, or <c>null</c> when the tree is empty.</summary>
    public TItem? Selected
    {
        get
        {
            EnsureVisible();
            return _cursor >= 0 && _cursor < _visible.Count ? _visible[_cursor].Item : null;
        }
    }

    /// <summary>Depth (0 = root) of the currently-selected node, or -1 when empty.</summary>
    public int SelectedDepth
    {
        get
        {
            EnsureVisible();
            return _cursor >= 0 && _cursor < _visible.Count ? _visible[_cursor].Depth : -1;
        }
    }

    private int HeaderRows => _header.Length > 0 ? 1 : 0;

    // ---- configuration -----------------------------------------------------

    public TreeView<TItem> Root(TItem root, bool expandRoot = true)
    {
        _root = root;
        _expanded.Clear();
        if (expandRoot) _expanded.Add(root);
        _cursor = 0;
        _scrollOffset = 0;
        _visibleStale = true;
        return this;
    }

    public TreeView<TItem> Header(string text)             { _header = text;       return this; }
    public TreeView<TItem> HeaderStyle(VtStyle s)          { _headerStyle = s;     return this; }
    public TreeView<TItem> EmptyStyle(VtStyle s)           { _emptyStyle = s;      return this; }
    public TreeView<TItem> ScrollBarStyle(VtStyle s)       { _scrollBarStyle = s;  return this; }
    public TreeView<TItem> ThumbStyle(VtStyle s)           { _thumbStyle = s;      return this; }
    public TreeView<TItem> TwirlStyle(VtStyle s)           { _twirlStyle = s;      return this; }
    public TreeView<TItem> TwirlSelectedStyle(VtStyle s)   { _twirlSelStyle = s;   return this; }

    // ---- expansion control -------------------------------------------------

    public bool ToggleExpand(TItem? item = null)
    {
        item ??= Selected;
        if (item is null || !item.HasChildren) return false;
        if (!_expanded.Add(item)) _expanded.Remove(item);
        _visibleStale = true;
        return true;
    }

    public bool Expand(TItem? item = null)
    {
        item ??= Selected;
        if (item is null || !item.HasChildren) return false;
        if (_expanded.Add(item)) { _visibleStale = true; return true; }
        return false;
    }

    public bool Collapse(TItem? item = null)
    {
        item ??= Selected;
        if (item is null) return false;
        if (_expanded.Remove(item)) { _visibleStale = true; return true; }
        return false;
    }

    public bool IsExpanded(TItem item) => _expanded.Contains(item);

    /// <summary>
    /// Marks the cached visible-row list stale so the next <see cref="Render"/>
    /// re-flattens the tree. Call this after structurally mutating any node's
    /// <see cref="ITreeNode{TSelf}.Children"/> outside the widget (e.g. a deep
    /// rescan that splices in a new subtree). Cheap — just flips a flag.
    /// </summary>
    public void Invalidate() => _visibleStale = true;

    // ---- cursor movement ---------------------------------------------------

    public bool MoveCursor(int delta)
    {
        EnsureVisible();
        if (_visible.Count == 0) return false;
        int next = Math.Clamp(_cursor + delta, 0, _visible.Count - 1);
        if (next == _cursor) return false;
        _cursor = next;
        EnsureCursorVisible();
        return true;
    }

    public bool MoveTo(int idx)
    {
        EnsureVisible();
        if (_visible.Count == 0) return false;
        idx = Math.Clamp(idx, 0, _visible.Count - 1);
        if (idx == _cursor) return false;
        _cursor = idx;
        EnsureCursorVisible();
        return true;
    }

    /// <summary>
    /// Jumps the cursor to the parent of the currently-selected node — the
    /// nearest preceding row with a smaller depth. Returns <c>false</c> when
    /// already at the root.
    /// </summary>
    public bool JumpToParent()
    {
        EnsureVisible();
        if (_cursor <= 0) return false;
        var depth = _visible[_cursor].Depth;
        if (depth == 0) return false;
        for (int i = _cursor - 1; i >= 0; i--)
        {
            if (_visible[i].Depth < depth)
            {
                _cursor = i;
                EnsureCursorVisible();
                return true;
            }
        }
        return false;
    }

    // ---- input -------------------------------------------------------------

    /// <summary>
    /// Handles a key for this tree. Returns <c>true</c> when the event changed
    /// state (cursor / expansion / scroll) and a re-render is needed.
    /// </summary>
    public bool HandleKey(ConsoleKey key, ConsoleModifiers _ = 0)
    {
        EnsureVisible();
        switch (key)
        {
            case ConsoleKey.UpArrow:    return MoveCursor(-1);
            case ConsoleKey.DownArrow:  return MoveCursor(+1);
            case ConsoleKey.PageUp:     return MoveCursor(-Math.Max(1, VisibleRows - 1));
            case ConsoleKey.PageDown:   return MoveCursor(+Math.Max(1, VisibleRows - 1));
            case ConsoleKey.Home:       return MoveTo(0);
            case ConsoleKey.End:        return MoveTo(int.MaxValue);

            case ConsoleKey.LeftArrow:
                // Expanded → collapse. Already collapsed (or leaf) → jump to parent.
                if (Selected is { } lsel && _expanded.Contains(lsel)) return Collapse(lsel);
                return JumpToParent();

            case ConsoleKey.RightArrow:
                // Collapsible & collapsed → expand. Already expanded → descend to first child.
                if (Selected is { } rsel)
                {
                    if (rsel.HasChildren && !_expanded.Contains(rsel)) return Expand(rsel);
                    if (_expanded.Contains(rsel) && rsel.Children.Count > 0) return MoveCursor(+1);
                }
                return false;

            case ConsoleKey.Enter:
            case ConsoleKey.Spacebar:
                return ToggleExpand();
        }
        return false;
    }

    /// <summary>
    /// Returns the node visible at the given mouse-event position WITHOUT
    /// mutating cursor or scroll. Used by callers that want to act on a
    /// clicked row (modifier-click semantics) without selecting it.
    /// </summary>
    public TItem? HitTestNode(MouseEvent m)
    {
        EnsureVisible();
        if (HitTest(m.X, m.Y) is not (var col, var row)) return null;
        if (row < HeaderRows) return null;
        var idx = _scrollOffset + (row - HeaderRows);
        if (idx < 0 || idx >= _visible.Count) return null;
        // Don't return a node when the click is on the scrollbar column.
        var (width, _) = Viewport.Size;
        if (HasScrollBar && col == width - 1) return null;
        return _visible[idx].Item;
    }

    /// <summary>
    /// When <c>true</c>, <see cref="HandleMouse"/> auto-routes wheel events
    /// (button 64 = up, 65 = down) into <see cref="HandleWheel"/> at the
    /// <see cref="WheelStep"/> rate. Default <c>false</c> for backward
    /// compatibility — hosts that want different semantics keep doing their
    /// own dispatch.
    /// </summary>
    public bool AutoHandleWheel { get; set; }

    /// <summary>
    /// Rows scrolled per wheel notch when <see cref="AutoHandleWheel"/> is
    /// <c>true</c>. Default <c>3</c>.
    /// </summary>
    public int WheelStep { get; set; } = 3;

    public bool HandleWheel(int delta)
    {
        EnsureVisible();
        if (!HasScrollBar) return false;
        var before = _scrollOffset;
        _scrollOffset -= delta;
        ClampOffset();
        return _scrollOffset != before;
    }

    /// <summary>
    /// Routes a mouse event. Click on the twirl cell toggles expansion; click
    /// elsewhere on a row moves the cursor (re-clicking the already-selected
    /// row toggles, which gives one-handed mouse drill-down). The scrollbar
    /// column drives drag/page exactly like <see cref="ScrollableList{T}"/>.
    /// Wheel events (button 64/65) are routed to <see cref="HandleWheel"/>
    /// when <see cref="AutoHandleWheel"/> is set; otherwise they fall through.
    /// </summary>
    public bool HandleMouse(MouseEvent m)
    {
        // Wheel auto-routing (opt-in). Peeled off first because wheel events
        // never carry IsRelease/IsMotion and shouldn't trip the drag path.
        if (AutoHandleWheel && m.Button is 64 or 65)
            return HandleWheel(m.Button == 64 ? WheelStep : -WheelStep);

        if (m.IsRelease)
        {
            var was = _isDragging;
            _isDragging = false;
            return was;
        }

        EnsureVisible();
        if (m.Button != 0) return false;
        var (width, _) = Viewport.Size;

        // Drag continues even when the cursor leaves the widget — desktop convention.
        if (m.IsMotion && _isDragging && HasScrollBar)
        {
            var cellH = Viewport.CellSize.Height;
            if (cellH <= 0) return true;
            var rawRow = m.Y / cellH - Viewport.Offset.Row;
            var dataRow = rawRow - HeaderRows;
            var (_, thumbH) = ComputeThumb();
            var trackUsable = Math.Max(1, VisibleRows - thumbH);
            var maxOffset = Math.Max(1, _visible.Count - VisibleRows);
            var thumbTopAtStart = (int)((long)_dragStartOffset * trackUsable / maxOffset);
            var grip = _dragStartRow - thumbTopAtStart;
            var newThumbTop = Math.Clamp(dataRow - grip, 0, trackUsable);
            _scrollOffset = (int)Math.Round((double)newThumbTop * maxOffset / trackUsable);
            ClampOffset();
            return true;
        }

        if (HitTest(m.X, m.Y) is not (var col, var row)) return false;
        if (m.IsMotion) return false;     // motion w/o drag is ignored

        // Scrollbar column.
        var lastCol = width - 1;
        if (HasScrollBar && col == lastCol)
        {
            var clickRow = row - HeaderRows;
            if (clickRow < 0) return false;
            var (thumbTop, thumbH) = ComputeThumb();
            if (clickRow >= thumbTop && clickRow < thumbTop + thumbH)
            {
                _isDragging = true;
                _dragStartRow = clickRow;
                _dragStartOffset = _scrollOffset;
            }
            else if (clickRow < thumbTop) { _scrollOffset -= VisibleRows; ClampOffset(); }
            else                          { _scrollOffset += VisibleRows; ClampOffset(); }
            return true;
        }

        // Header row → consume but ignore (no sort/header click yet).
        if (row < HeaderRows) return false;

        var dataRow2 = row - HeaderRows;
        var idx = _scrollOffset + dataRow2;
        if (idx < 0 || idx >= _visible.Count) return false;

        var (item, depth) = _visible[idx];
        var twirlCol = depth * 2;

        // Click on the twirl glyph toggles, regardless of current selection.
        if (item.HasChildren && col >= twirlCol && col < twirlCol + 1)
        {
            _cursor = idx;
            ToggleExpand(item);
            EnsureCursorVisible();
            return true;
        }

        // Click on a different row selects it.
        if (idx != _cursor)
        {
            _cursor = idx;
            EnsureCursorVisible();
            return true;
        }

        // Click on the already-selected row toggles expansion (drill-down ergonomics).
        if (item.HasChildren) { ToggleExpand(item); return true; }
        return false;
    }

    // ---- rendering ---------------------------------------------------------

    public override void Render()
    {
        EnsureVisible();
        var (width, height) = Viewport.Size;
        if (width <= 0 || height <= 0) return;

        var colorMode = Viewport.ColorMode;
        var contentWidth = HasScrollBar ? width - 1 : width;
        var (thumbTop, thumbHeight) = HasScrollBar ? ComputeThumb() : (0, 0);

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
            if (!TrySetCursorPosition(Viewport, 0, row)) return;
            var dataRow = row - HeaderRows;
            var idx = _scrollOffset + dataRow;
            if (idx >= 0 && idx < _visible.Count)
            {
                PaintRow(_visible[idx].Item, _visible[idx].Depth, idx == _cursor, contentWidth, row,
                    colorMode, rowTrees);
            }
            else
            {
                Viewport.Write($"{_emptyStyle.Apply(colorMode)}{new string(' ', contentWidth)}{VtStyle.Reset}");
            }

            if (HasScrollBar)
            {
                // Position explicitly rather than trusting where the row left the cursor.
                //
                // Until 4.10 a row was one string of exactly contentWidth cells, written in sequence, so
                // the cursor arrived here on its own. A row is now a layout tree, and CellLayout positions
                // per text run and leaves the cursor after the LAST GLYPH it drew -- so the bar landed
                // immediately right of each row's rightmost text, at a different column on every row. That
                // reads as a stray block beside every label rather than as a scrollbar, and it shows up
                // exactly when the tree overflows, which is when the bar is supposed to start helping.
                //
                // ScrollableList already carries this line; TreeView was missed when rows moved to trees.
                if (!TrySetCursorPosition(Viewport, contentWidth, row)) return;

                var onThumb = dataRow >= thumbTop && dataRow < thumbTop + thumbHeight;
                var style = onThumb ? _thumbStyle : _scrollBarStyle;
                var glyph = onThumb ? '\u2588' : '\u2502'; // █ on thumb, │ on track
                Viewport.Write($"{style.Apply(colorMode)}{glyph}{VtStyle.Reset}");
            }
        }

        _arrangedRows = rowTrees.ToImmutable();
    }

    // Paint exactly contentWidth visible cells: indent + twirl + content.
    //
    // Indent and twirl are the WIDGET's chrome and stay direct writes. The content slice is the node's
    // own layout tree, arranged into the rect that is left over and painted through CellLayout -- so a
    // node states structure and colour, never padding or escapes, and the rect it was arranged into is
    // also its hit region (see DispatchRowHit).
    //
    // This deliberately reverses the old "compose one string, emit a single Write" rule, which existed
    // because multiple Writes per row visibly slowed Windows console hosts. The diffing cell buffer
    // removed that cost: Writes land in the back grid and one flush emits the diff, so the number of
    // Write calls no longer maps to terminal traffic at all.
    private void PaintRow(TItem item, int depth, bool isSelected, int contentWidth, int viewportRow,
        ColorMode mode, ImmutableArray<Layout.ArrangedNode<int>>.Builder rowTrees)
    {
        if (contentWidth <= 0) return;

        // 2 cells per indent level. Cap so very deep trees don't push the
        // content off-screen — leave at least 8 cells for content + twirl.
        int maxIndent = Math.Max(0, contentWidth - 8);
        int indent = Math.Min(depth * 2, maxIndent);
        int remaining = contentWidth - indent;

        var sb = new System.Text.StringBuilder(indent + 32);
        if (indent > 0) sb.Append(' ', indent);
        if (remaining <= 0) { Viewport.Write(sb.ToString()); return; }

        if (remaining < 2)
        {
            sb.Append(' ', remaining);
            Viewport.Write(sb.ToString());
            return;
        }

        // Twirl glyph: ▶ collapsed, ▼ expanded, · leaf. Always followed by a space.
        var twirl = !item.HasChildren ? '·' : (_expanded.Contains(item) ? '▼' /*▼*/ : '▶' /*▶*/);
        var twirlStyle = isSelected ? _twirlSelStyle : _twirlStyle;
        sb.Append(twirlStyle.Apply(mode)).Append(twirl).Append(' ').Append(VtStyle.Reset);
        Viewport.Write(sb.ToString());

        remaining -= 2;
        if (remaining <= 0) return;

        var arranged = Layout.Engine.Arrange(
            item.BuildNodeContent(RowContext.Single(isSelected)),
            new Rect<int>(indent + 2, viewportRow, remaining, 1),
            MeasureContext);

        CellLayout.Paint(Viewport, arranged);
        rowTrees.AddRange(arranged);
    }

    /// <summary>
    /// Resolves a MOUSE-PIXEL point against the arranged node-content trees, invoking the matched leaf's
    /// <c>OnClick</c> and returning its hit. The twirl and indent columns are the widget's own chrome and
    /// carry no node hits, so a click on the twirl falls through to the widget's expand/collapse handling.
    /// Mirrors <see cref="ScrollableList{TItem}.DispatchRowHit"/>.
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
    /// The visible node-content slices as last arranged, concatenated in paint order, in
    /// viewport-relative cell coordinates. For tests that assert drawn geometry.
    /// </summary>
    public ImmutableArray<Layout.ArrangedNode<int>> ArrangedRows => _arrangedRows;

    /// <summary>
    /// Sets the unit convention node-content trees are authored in. Cells by default
    /// (<c>RowH(1)</c> = one row); pass <see cref="CellMeasureContext.PixelAuthored"/> for a tree shared
    /// with a GPU surface. A fluent setter rather than a virtual because this type is sealed.
    /// </summary>
    public TreeView<TItem> Measure(CellMeasureContext context)
    {
        _measureContext = context;
        return this;
    }

    private CellMeasureContext MeasureContext => _measureContext;
    private CellMeasureContext _measureContext = CellMeasureContext.CellAuthored;

    private ImmutableArray<Layout.ArrangedNode<int>> _arrangedRows = [];

    // ---- internals ---------------------------------------------------------

    private bool HasScrollBar
    {
        get { EnsureVisible(); return _visible.Count > VisibleRows && VisibleRows > 0; }
    }

    private void EnsureVisible()
    {
        if (!_visibleStale) return;
        _visible.Clear();
        if (_root != null) AddVisible(_root, 0);
        _visibleStale = false;
        if (_cursor >= _visible.Count) _cursor = Math.Max(0, _visible.Count - 1);
        ClampOffset();
    }

    private void AddVisible(TItem item, int depth)
    {
        _visible.Add((item, depth));
        if (_expanded.Contains(item))
        {
            // Trigger lazy population *just* before we walk Children. The hook is
            // idempotent, so this is safe to call on every flatten — typical
            // implementations gate on a "loaded" flag and return immediately.
            item.EnsureChildrenLoaded();
            foreach (var c in item.Children) AddVisible(c, depth + 1);
        }
    }

    private void EnsureCursorVisible()
    {
        if (_cursor < 0 || VisibleRows <= 0) return;
        if (_cursor < _scrollOffset) _scrollOffset = _cursor;
        else if (_cursor >= _scrollOffset + VisibleRows) _scrollOffset = _cursor - VisibleRows + 1;
        ClampOffset();
    }

    private void ClampOffset()
    {
        var max = Math.Max(0, _visible.Count - VisibleRows);
        if (_scrollOffset < 0) _scrollOffset = 0;
        else if (_scrollOffset > max) _scrollOffset = max;
    }

    private (int ThumbTop, int ThumbH) ComputeThumb()
    {
        var total = _visible.Count;
        if (total <= VisibleRows || VisibleRows <= 0) return (0, 0);
        var thumbH = Math.Max(1, VisibleRows * VisibleRows / total);
        var maxOffset = total - VisibleRows;
        var trackUsable = VisibleRows - thumbH;
        var thumbTop = maxOffset > 0 ? trackUsable * _scrollOffset / maxOffset : 0;
        return (thumbTop, thumbH);
    }
}
