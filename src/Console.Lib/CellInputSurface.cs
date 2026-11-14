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
/// The list-cursor half (<see cref="MoveListCursor"/>, <see cref="ActivateListCursor"/>,
/// <see cref="HandleListKey"/>) mirrors the logic of DIR.Lib's <c>PixelWidgetBase</c>, whose members are
/// instance members of an abstract renderer-bound class and so cannot be reused by a cell host.
/// Keep the two in step.
/// </para>
/// </summary>
public sealed class CellInputSurface(WindowUiSettings ui) : IPixelWidget
{
    private readonly ClickableRegionTracker _tracker = new();
    private readonly List<Layout.ArrangedNode<float>> _painted = [];
    private readonly List<TextInputState> _fields = [];
    private readonly Dictionary<TextInputState, (int X, int Width)> _fieldRects = [];
    private readonly List<ListCursor.PaintedRow> _rows = [];

    public WindowUiSettings Ui { get; } = ui;

    /// <summary>The keyboard's position in a declared list; open it with <see cref="ListCursor.Open(string, int)"/>.</summary>
    public ListCursor ListCursor { get; } = new();

    public (float X, float Y)? Pointer { get; set; }

    private bool Current => _tracker.Frame == Ui.FrameId;

    /// <summary>Clears last frame's declarations and registers this arranged tree's, in paint order.</summary>
    internal void Declare(ImmutableArray<Layout.ArrangedNode<int>> arranged)
    {
        _tracker.BeginFrame(Ui.FrameId);
        _painted.Clear();
        _fields.Clear();
        _fieldRects.Clear();

        var disabledDepth = -1;
        var closedPopoverDepth = -1;

        foreach (var an in arranged)
        {
            var node = an.Node;
            var depth = an.Depth;
            var r = an.Bounds;

            if (disabledDepth >= 0 && depth <= disabledDepth)
            {
                disabledDepth = -1;
            }

            if (closedPopoverDepth >= 0 && depth <= closedPopoverDepth)
            {
                closedPopoverDepth = -1;
            }

            if (node.Popover is { IsOpen: false } && closedPopoverDepth < 0)
            {
                closedPopoverDepth = depth;
            }

            if (closedPopoverDepth >= 0)
            {
                continue;
            }

            _painted.Add(new Layout.ArrangedNode<float>(node, new Rect<float>(r.X, r.Y, r.Width, r.Height))
            {
                Depth = depth,
            });

            if (node.IsDisabled && disabledDepth < 0)
            {
                disabledDepth = depth;
            }

            var disabled = disabledDepth >= 0;
            var cursor = node.IsDisabled ? CursorKind.NotAllowed : disabled ? null : node.Cursor;
            var tooltip = node.Tooltip ?? node.DisabledReason;

            if (node.Hit is { } hit)
            {
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
        if (!_fieldRects.TryGetValue(state, out var rect))
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

    public ListScrollController? ScrollTargetAt(float x, float y) => null;

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

        return ListCursor.Step(delta, _rows);
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
/// read event, map to <see cref="InputEvent"/> in cells (<c>ConsoleInputEvent.ToInputEvent</c>), <c>router.Handle</c>.
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
