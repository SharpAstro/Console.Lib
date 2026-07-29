# Migration notes

## 4.10 — list and tree rows are layout trees

`IRowFormatter` is gone. `ITreeNode<TSelf>.FormatNodeContent` is gone. Both are replaced by a method
returning a `DIR.Lib.Layout.Node` tree, and both take the same new `RowContext`.

This is a hard break with no compatibility shim, deliberately: a default-implementation bridge would
let a codebase sit half-ported, and a row that quietly kept the old shape would keep the exact defects
below while appearing to work.

### Why

The old contract was `string FormatRow(int width, ColorMode colorMode, ...)`, documented as "must
include VT escape codes and pad to the full width". Every row therefore hand-rolled its own layout,
padding, truncation and escape sequences. Three consequences:

1. **An inline button on a row had no hit region.** There was no arranged rect to bind to, so a caller
   re-derived the button's columns next to the code that drew them and kept the two in step by hand.
   Worse, the row's usable width is *not* the viewport width — the scrollbar takes a column once the
   list overflows — so a right-aligned button drifted by one column exactly when the list scrolled.
2. **A row could not state a colour it did not own.** Foreground-only writes relied on whatever SGR
   state a previous write left in effect. A real terminal forgives that; the diffing cell buffer added
   in 4.8 cannot, because it must name a colour for every cell it stores.
3. **The same row is often also a GPU row.** A `Layout.Node` tree renders on a pixel surface too, so a
   row authored once serves both instead of being written twice and drifting apart.

`width` and `ColorMode` are no longer parameters: the widget owns the rect (it already computed it) and
`CellLayout` owns the pen. The three-overload cascade (`(width, mode)` → `(.., isSelected)` →
`(.., selectedColumn, columnCount)`) collapses into fields on `RowContext`, so the next capability adds
a field rather than a fourth rung an implementation can silently ignore.

### Porting a row

```csharp
// before
internal sealed class FileRow(string name) : IRowFormatter
{
    public string FormatRow(int width, ColorMode mode) =>
        $"{new VtStyle(SgrColor.White, SgrColor.Black).Apply(mode)}{name.PadRight(width)}{VtStyle.Reset}";
}

// after
internal sealed class FileRow(string name) : IRowLayout
{
    public Layout.Node BuildRow(in RowContext context) =>
        Layout.Builder.Text(name, 1f, Palette.Text).WStar().HStar();
}
```

Notes:

- **Font size is `1f`** for a cell-authored tree: one design unit is one cell
  (`CellMeasureContext.CellAuthored`, the default). Use `CellMeasureContext.PixelAuthored` — via
  `ScrollableList`'s overridable `MeasureContext`, or `TreeView.Measure(...)` — only for a tree shared
  with a GPU surface, where `RowH(16)` means one line of text.
- **Do not pad or truncate.** The row is arranged into its rect; overflow is clipped by the engine.
- **State the background you want** with `.Bg(colour)` rather than relying on an enclosing style.
  An unstated colour resolves to the terminal default (SGR 39/49), never black.
- **Selection styling** comes from `context.Selected`; read `context.Columns` (not `ColumnCount`) for the
  column count, because `default(RowContext)` cannot carry the primary-constructor default.

### Porting an inline button — the case that motivated this

```csharp
public Layout.Node BuildRow(in RowContext context) => Layout.Builder.HStack(
    Layout.Builder.Text(name, 1f).WStar().HStar(),                       // takes the slack
    Layout.Builder.Text("[X]", 1f).WFixed(3).HStar()                     // pinned to the right edge
        .Clickable(new HitResult.ButtonHit($"del{index}"), _ => Delete(index)));
```

Right-anchoring now just works: the row was arranged into the *content* width, so the button lands at
the real right edge with or without a scrollbar, and nothing computes a column.

### Porting the host

`RegisterRowHits`, `RegisterRowSpanHits` and `RowSpan` are **deleted**. Hits ride on the tree.

```csharp
// before — register regions up front, dispatch through a tracker
list.RegisterRowHits(Tracker,
    hitFor: (i, _) => new HitResult.ListItemHit("Profile", i),
    onClick: (i, _) => { list.MoveTo(i); SwitchToSelected(); });

// after — HandleMouse already moves the cursor, so act on it; row buttons dispatch themselves
if (list.HandleMouse(mouse)) { SwitchToSelected(); return true; }
if (list.DispatchRowHit(mouseX, mouseY) is not null) { NeedsRedraw = true; return true; }
```

`DispatchRowHit` resolves against the trees as last *arranged*, so it requires a `Render()` to have
happened — and that is what makes it correct: the four ways the old helpers could silently disagree with
the paint (pixel origin from the viewport offset, the header row, the *scrolled* item index, the
scrollbar column) are no longer expressible, because each row was arranged at the row it was painted on
and at the width it was painted at.

`HitTestRow` is unchanged and still returns the item behind a point — for context menus, drag sources and
hover, not for reaching a row's buttons.

### Kept on purpose

`ITreeNode`'s `Children`, `HasChildren` and `EnsureChildrenLoaded` keep their default implementations.
Those describe genuinely optional *behaviour* (a leaf has no children; lazy loading is opt-in), not a
second way to satisfy one obligation. `BuildNodeContent` has no default: a node that does not describe
itself has nothing to draw.
