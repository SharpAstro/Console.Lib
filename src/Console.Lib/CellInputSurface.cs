using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using DIR.Lib;

namespace Console.Lib;

/// <summary>
/// The cell surface's <see cref="IPixelWidget"/>: what a cell-painted <see cref="Layout.Node"/> tree declares
/// (click regions, fields, shortcuts, list rows) registered in CELL coordinates held as floats, so DIR.Lib's
/// <see cref="InputRouter"/> routes keys, shortcuts, focus, Tab and clicks for a terminal host with no
/// host-side routing. Filled per frame by <c>CellLayout.Paint(viewport, arranged, surface, ...)</c>.
/// <para>
/// A list that declares its own cursor (<c>Node.ListOf(cursor, listId)</c>) needs nothing here: DIR.Lib's
/// <see cref="InputRouter"/> reads the declaration from the painted nodes, applies its one active-list rule
/// (<see cref="DeclaredList.Active"/>) and steps that cursor, and this surface only answers what the router asks
/// of a widget (the painted regions, <see cref="CollectClippedListRows"/>, <see cref="RevealListRow"/>). The
/// cell paint lights the row from the same cursor. The surface's own <see cref="ListCursor"/>
/// (<see cref="MoveListCursor"/>, <see cref="ActivateListCursor"/>, <see cref="HandleListKey"/>) is the
/// fallback for a tree that declares no list, and behaves as it did before declarations existed.
/// </para>
/// </summary>
public sealed class CellInputSurface(WindowUiSettings ui) : IPixelWidget
{
    private readonly ClickableRegionTracker _tracker = new();
    private readonly List<Layout.ArrangedNode<float>> _painted = [];
    private readonly List<TextInputState> _fields = [];
    private readonly Dictionary<TextInputState, (int X, int Width)> _fieldRects = [];
    private readonly List<ListCursor.PaintedRow> _rows = [];
    private readonly List<ScrolledRow> _scrolledRows = [];

    public WindowUiSettings Ui { get; } = ui;

    /// <summary>The keyboard's position in a declared list; open it with <see cref="ListCursor.Open(string, int)"/>.</summary>
    public ListCursor ListCursor { get; } = new();

    public (float X, float Y)? Pointer { get; set; }

    // A claim's handler: present so the router does not read the claim as inert chrome and look through it.
    private static void NoOp(InputModifier _) { }

    private bool Current => _tracker.Frame == Ui.FrameId;

    /// <summary>Clears last frame's declarations and registers this arranged tree's, in paint order.</summary>
    internal void Declare(ImmutableArray<Layout.ArrangedNode<int>> arranged)
    {
        _tracker.BeginFrame(Ui.FrameId);
        _painted.Clear();
        _fields.Clear();
        _fieldRects.Clear();
        Ui.NoteFrameBegin(this);

        _scrolledRows.Clear();
        var scrolls = new Stack<(int Depth, ListScrollController Scroll, Rect<int> Clip)>();
        var disabledDepth = -1;
        var closedPopoverDepth = -1;

        // An open popover claims the pointer for its CONTENT rect (the first child of its Anchored node, one
        // level below it), as DIR.Lib's PixelWidgetBase does: -1 is "not inside an open popover".
        var openPopoverDepth = -1;
        var pendingContentDepth = -1;
        var claimed = false;
        PopoverState? openPopover = null;

        var modalDepth = -1;
        Layout.Node.Overlay? modalOverlay = null;
        ModalClaim? modalClaim = null;
        var modalCardDepth = -1;

        foreach (var an in arranged)
        {
            var node = an.Node;
            var depth = an.Depth;
            var full = an.Bounds;
            var r = full;

            if (disabledDepth >= 0 && depth <= disabledDepth)
            {
                disabledDepth = -1;
            }

            if (closedPopoverDepth >= 0 && depth <= closedPopoverDepth)
            {
                closedPopoverDepth = -1;
            }

            if (openPopoverDepth >= 0 && depth <= openPopoverDepth)
            {
                openPopoverDepth = -1;
                pendingContentDepth = -1;
            }

            if (node.Popover is { IsOpen: false } && closedPopoverDepth < 0)
            {
                closedPopoverDepth = depth;
            }
            else if (node.Popover is { IsOpen: true } && closedPopoverDepth < 0)
            {
                openPopoverDepth = depth;
                openPopover = node.Popover;
                pendingContentDepth = -1;
                claimed = false;
                // An open popover of this frame, whether or not a Scroll leaves any of it on screen: the router
                // walks the painted popovers for Escape, as DIR.Lib's pixel surface notes every open one it meets.
                // The claim below notes it again when the content is on screen, which only moves it to the top.
                Ui.NotePopoverPainted(node.Popover);
            }

            if (closedPopoverDepth >= 0)
            {
                continue;
            }

            while (scrolls.Count > 0 && scrolls.Peek().Depth >= depth)
            {
                scrolls.Pop();
            }

            if (scrolls.Count > 0)
            {
                var top = scrolls.Peek();
                if (!CellLayout.Overlaps(top.Clip, r))
                {
                    if (pendingContentDepth >= 0 && depth <= pendingContentDepth)
                    {
                        // The popover's content was clipped away: it has none to claim, and a later sibling at
                        // this depth is not it.
                        pendingContentDepth = -1;
                        claimed = true;
                    }

                    // A disabled node disables what it holds whether or not it is on screen. The same note is made
                    // further down for a node that is, which this branch never reaches: without it a row under a
                    // disabled group scrolled out of view was noted enabled, and Enter activated it.
                    if (node.IsDisabled && disabledDepth < 0)
                    {
                        disabledDepth = depth;
                    }

                    // Off screen: nothing registered, so it cannot answer a press, but a list row is still a
                    // row of its list and is noted for the keyboard.
                    if (node.Hit is HitResult.ListItemHit clipped)
                    {
                        NoteScrolledRow(clipped, node, node.IsDisabled || disabledDepth >= 0, true, top.Scroll, full);
                    }

                    continue;
                }

                r = CellLayout.Intersect(top.Clip, r);
            }

            // A declared modal is recorded as DIR.Lib's PixelWidgetBase records it: when Top begins, before it
            // registers anything, so the counts are what lay beneath; an Anchored Top's card is its next-depth child.
            if (modalDepth >= 0 && depth <= modalDepth)
            {
                modalDepth = -1;
                modalOverlay = null;
                modalClaim = null;
                modalCardDepth = -1;
            }

            if (modalClaim is { } pendingCard && modalCardDepth >= 0 && depth == modalCardDepth)
            {
                pendingCard.Card = new RectF32(r.X, r.Y, r.Width, r.Height);
                modalCardDepth = -1;
            }

            if (node is Layout.Node.Overlay { Modal: true } declaredModal)
            {
                modalDepth = depth;
                modalOverlay = declaredModal;
            }
            else if (modalOverlay is not null && modalClaim is null && ReferenceEquals(node, modalOverlay.Top))
            {
                modalClaim = Ui.NoteModalPainted(this, _tracker.Regions.Length, _painted.Count,
                    new RectF32(r.X, r.Y, r.Width, r.Height), modalOverlay.OnDismiss);
                if (node is Layout.Node.Anchored)
                {
                    modalCardDepth = depth + 1;
                }
            }

            _painted.Add(new Layout.ArrangedNode<float>(node, new Rect<float>(r.X, r.Y, r.Width, r.Height))
            {
                Depth = depth,
            });

            // The popover's content takes every press inside it, so the scrim under it (which dismisses) and the
            // list rows under that never see it, on the content's empty padding as much as on a child. Registered
            // before the content's own hit and its children, which therefore still win where they are. This is
            // the cell surface's own claim. It is also made through NoteFrameBegin + ClaimPointer (public in
            // DIR.Lib 11.61), which sets PointerOwner so a press on the padding neither chooses nor clears the
            // declared list that owns the arrows, and lets the claim settle so AfterPaint requests no extra frame.
            if (pendingContentDepth >= 0 && depth == pendingContentDepth && !claimed)
            {
                claimed = true;
                pendingContentDepth = -1;
                if (openPopover is not null)
                {
                    Ui.NotePopoverPainted(openPopover);
                    Ui.ClaimPointer(new RectF32(r.X, r.Y, r.Width, r.Height), openPopover);
                }

                var claim = new ClickableRegion(r.X, r.Y, r.Width, r.Height, new HitResult.ChromeHit(), NoOp, CursorKind.Default);
                _tracker.Register(ref claim);
            }
            else if (openPopoverDepth >= 0 && !claimed && node is Layout.Node.Anchored)
            {
                pendingContentDepth = depth + 1;
            }

            if (node.IsDisabled && disabledDepth < 0)
            {
                disabledDepth = depth;
            }

            var disabled = disabledDepth >= 0;
            var cursor = node.IsDisabled ? CursorKind.NotAllowed : disabled ? null : node.Cursor;
            var tooltip = node.Tooltip ?? node.DisabledReason;

            if (node.Hit is { } hit)
            {
                if (hit is HitResult.ListItemHit listRow && scrolls.Count > 0)
                {
                    NoteScrolledRow(listRow, node, disabled, false, scrolls.Peek().Scroll, full);
                }

                var region = new ClickableRegion(r.X, r.Y, r.Width, r.Height, hit, disabled ? null : node.OnClick, cursor)
                {
                    OnPress = disabled ? null : node.OnPress,
                    OnActivate = disabled ? null : node.OnActivate,
                    OnDoubleClick = disabled ? null : node.OnDoubleClick,
                    OnContextMenu = disabled ? null : node.OnContextMenu,
                    Tooltip = tooltip,
                    TooltipSide = node.TooltipSide,
                    IsDisabled = disabled,
                    Scroll = node.Scroll,
                    Opens = disabled ? null : node.OpensPopover,
                    Dismisses = node.DismissesPopover,
                };
                _tracker.Register(ref region);
            }
            else if (cursor is not null || tooltip is not null || node.Scroll is not null)
            {
                var region = new ClickableRegion(r.X, r.Y, r.Width, r.Height, new HitResult.ChromeHit(), null, cursor)
                {
                    Tooltip = tooltip,
                    TooltipSide = node.TooltipSide,
                    IsDisabled = disabled,
                    Scroll = node.Scroll,
                };
                _tracker.Register(ref region);
            }

            // The node's children are under its viewport from here on; its own region is never clipped by it.
            if (node.Scroll is { } scrolled)
            {
                scrolls.Push((depth, scrolled, r));
            }

            // Registered after the node's own hit so the field wins the click, as on the pixel surface.
            if (node is Layout.Node.Leaf { Content: Layout.Content.TextInput field })
            {
                var region = new ClickableRegion(r.X, r.Y, r.Width, r.Height,
                    new HitResult.TextInputHit(field.State, new TextInputGeometry(r.X, "", 1f, 0f)),
                    null, CursorKind.Text)
                {
                    FocusOnOpen = field.FocusOnOpen,
                };
                _tracker.Register(ref region);
                if (!_fields.Contains(field.State))
                {
                    _fields.Add(field.State);
                }

                _fieldRects[field.State] = (r.X, r.Width);
            }
        }
    }

    public bool HandleInput(InputEvent evt) => false;

    public HitResult? HitTest(float x, float y) => Current ? _tracker.HitTest(x, y) : null;

    public List<TextInputState> GetRegisteredTextInputs() => Current ? [.. _fields] : [];

    /// <summary>A column to a character index: cells are one character wide, less the field's scroll.</summary>
    public int CaretIndexAt(HitResult.TextInputHit hit, float pointerX)
    {
        var state = hit.Input;
        // Like HitTest and GetRegisteredTextInputs, this answers only for the frame it was declared in: a field's
        // rect from an earlier frame says where the field was, not where it is.
        if (!Current || !_fieldRects.TryGetValue(state, out var rect))
        {
            return state.Text.Length;
        }

        var cursor = Math.Clamp(state.CursorPos, 0, state.Text.Length);
        var offset = CellLayout.VisibleOffset(cursor, Math.Max(1, rect.Width));
        var index = (int)MathF.Floor(pointerX) - rect.X + offset;
        return Math.Clamp(index, 0, state.Text.Length);
    }

    public void CollectPaintedRegions(List<ClickableRegion> into)
    {
        ArgumentNullException.ThrowIfNull(into);
        if (Current)
        {
            into.AddRange(_tracker.GetRegisteredRegions());
        }
    }

    public void CollectPaintedNodes(List<Layout.ArrangedNode<float>> into)
    {
        ArgumentNullException.ThrowIfNull(into);
        if (Current)
        {
            into.AddRange(_painted);
        }
    }

    /// <summary>The innermost scrolled node under the point (the last registered, as parents register first).</summary>
    public ListScrollController? ScrollTargetAt(float x, float y)
    {
        if (!Current)
        {
            return null;
        }

        var regions = _tracker.Regions;
        for (var i = regions.Length - 1; i >= 0; i--)
        {
            var r = regions[i];
            if (r.Scroll is { } scroll && x >= r.X && x < r.X + r.Width && y >= r.Y && y < r.Y + r.Height)
            {
                return scroll;
            }
        }

        return null;
    }

    /// <summary>
    /// A list row met inside a scrolled node: where it sits in what that node scrolls over (its WHOLE extent, since
    /// bringing it into view means all of it), and its handler, so Enter can act on a row that was revealed but
    /// has not been repainted (a clipped row registered no region).
    /// </summary>
    private readonly record struct ScrolledRow(HitResult.ListItemHit Hit, bool IsDisabled, bool IsClipped,
        ListScrollController Scroll, float Start, float End, Action<InputModifier>? Act);

    private void NoteScrolledRow(HitResult.ListItemHit hit, Layout.Node node, bool isDisabled, bool isClipped,
        ListScrollController scroll, Rect<int> bounds)
    {
        var (start, end) = scroll.ContentExtentOf(new RectF32(bounds.X, bounds.Y, bounds.Width, bounds.Height));
        _scrolledRows.Add(new ScrolledRow(hit, isDisabled, isClipped, scroll, start, end,
            isDisabled ? null : node.OnActivate ?? node.OnClick));
    }

    /// <summary>
    /// The rows of list <paramref name="listId"/> a scrolled viewport arranged but clipped out, so a router stepping
    /// a DECLARED list (<c>Node.ListOf</c>) can step through rows that are reachable but not on screen.
    /// </summary>
    public void CollectClippedListRows(string listId, List<ListCursor.PaintedRow> into)
    {
        ArgumentNullException.ThrowIfNull(into);
        if (!Current)
        {
            return;
        }

        foreach (var row in _scrolledRows)
        {
            if (row.IsClipped && string.Equals(row.Hit.ListId, listId, StringComparison.Ordinal))
            {
                into.Add(new ListCursor.PaintedRow(row.Hit.Index, row.IsDisabled));
            }
        }
    }

    /// <summary>Scrolls the viewport holding <paramref name="cursor"/>'s row to show it, after a router stepped a declared list.</summary>
    public void RevealListRow(ListCursor cursor)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        RevealListCursor(cursor);
    }

    private void RevealListCursor() => RevealListCursor(ListCursor);

    private void RevealListCursor(ListCursor cursor)
    {
        foreach (var row in _scrolledRows)
        {
            if (cursor.IsOn(row.Hit))
            {
                row.Scroll.RevealExtent(row.Start, row.End);
                return;
            }
        }
    }

    public CursorKind? HitTestCursor(float x, float y) => Current ? _tracker.HitTestCursor(x, y) : null;

    /// <summary>Moves the list cursor by <paramref name="delta"/> over the rows the last paint declared.</summary>
    public bool MoveListCursor(int delta)
    {
        if (delta == 0 || !ListCursor.IsOpen || !Current)
        {
            return false;
        }

        _rows.Clear();
        foreach (var region in _tracker.Regions)
        {
            if (region.Result is HitResult.ListItemHit item && ListCursor.Owns(item))
            {
                _rows.Add(new ListCursor.PaintedRow(item.Index, region.IsDisabled));
            }
        }

        // And the rows a scrolled list arranged but its viewport clipped out: rows of the list all the same.
        foreach (var row in _scrolledRows)
        {
            if (row.IsClipped && ListCursor.Owns(row.Hit))
            {
                _rows.Add(new ListCursor.PaintedRow(row.Hit.Index, row.IsDisabled));
            }
        }

        if (!ListCursor.Step(delta, _rows))
        {
            return false;
        }

        RevealListCursor();
        return true;
    }

    /// <summary>Acts on the cursor's row as a click would (Enter's own handler first); false if nothing was acted on.</summary>
    public bool ActivateListCursor(InputModifier modifier = InputModifier.None)
    {
        if (!ListCursor.IsOpen || !Current)
        {
            return false;
        }

        if (ListCursor.Index < 0 && !MoveListCursor(1))
        {
            return false;
        }

        foreach (var region in _tracker.Regions)
        {
            if (!ListCursor.IsOn(region.Result))
            {
                continue;
            }

            if (region.IsDisabled || (region.OnActivate ?? region.OnClick) is not { } act)
            {
                return false;
            }

            act(modifier);
            return true;
        }

        // Revealed by the keyboard but not repainted yet: no region, so the noted row's own handler acts.
        foreach (var row in _scrolledRows)
        {
            if (row.IsClipped && ListCursor.IsOn(row.Hit))
            {
                if (row.Act is not { } act)
                {
                    return false;
                }

                act(modifier);
                return true;
            }
        }

        return false;
    }

    /// <summary>Up and Down move, Enter acts; false for anything else so the key stays the host's.</summary>
    public bool HandleListKey(InputKey key, InputModifier modifier = InputModifier.None) => key switch
    {
        InputKey.Up => MoveListCursor(-1),
        InputKey.Down => MoveListCursor(1),
        InputKey.Enter => ActivateListCursor(modifier),
        _ => false,
    };
}

/// <summary>
/// The one entry point for a cell host: an <see cref="InputRouter"/> whose widgets are the surface and whose
/// unhandled events go to the surface's list keys, then to the host's fallback. A consumer's loop is then
/// read event, map to <see cref="InputEvent"/> in cells (<c>ConsoleInputEvent.ToCellInputEvent(terminal.CellSize)</c>, which divides
/// the pixel mouse coordinates of a probed terminal), <c>router.Handle</c>.
/// </summary>
public static class CellInputRouter
{
    public static InputRouter Create(CellInputSurface surface, BackgroundTaskTracker tracker, Action requestRedraw,
        Func<InputEvent, bool>? fallback = null)
        => new(surface.Ui, tracker, requestRedraw)
        {
            Widgets = () => [surface],
            Unhandled = evt =>
                (evt is InputEvent.KeyDown key && surface.HandleListKey(key.Key, key.Modifiers))
                || (fallback?.Invoke(evt) ?? false),
        };
}
