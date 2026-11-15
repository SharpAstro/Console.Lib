using System;
using System.Collections.Immutable;
using System.Text;
using DIR.Lib;

namespace Console.Lib;

/// <summary>
/// The terminal-surface <see cref="Layout.IMeasureContext{T}"/>: text width is the character count (one row
/// tall), and design-unit scalars round to whole cells. (Wide-char / East-Asian-width measurement is a
/// documented follow-up; v1 is char-count, matching the rest of Console.Lib.)
/// <para>
/// <b>The unit convention belongs to the TREE, so it is a parameter here.</b> A tree written for the TUI
/// counts in cells (<c>RowH(1)</c> means one row), while a tree written for a pixel surface counts in
/// pixel-ish units (<c>RowH(16)</c> means one line of text). The same numbers cannot mean both, so the
/// context has to be told which convention the tree it is arranging was authored in -- see
/// <see cref="CellAuthored"/> and <see cref="PixelAuthored"/>. The opposite crossing lives in DIR.Lib
/// (7.4): <c>PixelMeasureContext&lt;TSurface&gt;.CellAuthored</c> carries a cell-authored tree onto a pixel
/// surface with the same nominal 8x16 cell, so together the pair lets a tree authored in either convention
/// arrange on either surface.
/// </para>
/// </summary>
/// <param name="designUnitsPerColumn">Design units spanned by one character cell horizontally.</param>
/// <param name="designUnitsPerRow">Design units spanned by one character cell vertically.</param>
public sealed class CellMeasureContext(float designUnitsPerColumn = 1f, float designUnitsPerRow = 1f)
    : Layout.IMeasureContext<int>
{
    /// <summary>
    /// One design unit is one cell -- the convention every hand-written TUI tree already uses, and the
    /// default, so existing callers are unchanged.
    /// </summary>
    public static CellMeasureContext CellAuthored { get; } = new CellMeasureContext();

    /// <summary>
    /// For a tree authored in pixel-ish design units (a shared tree that also renders on a GPU surface),
    /// mapped onto a nominal 8x16 cell. This is the case that needs the axes to differ: the same 250-unit
    /// card is 31 columns across but only 8 rows down.
    /// </summary>
    public static CellMeasureContext PixelAuthored { get; } = new CellMeasureContext(8f, 16f) { KeepsRowPerLine = true };

    /// <summary>
    /// Set for a pixel-authored mapping: <see cref="MapExtent"/> then keeps one row for one line of content
    /// (a Fixed height takes the content's rows, vertical padding rounds down, an empty spacer under a cell
    /// adds nothing, a filled node thinner than a cell keeps one row) and gives horizontal padding at most
    /// one cell a side. Off, as in <see cref="CellAuthored"/>, an extent is today's plain rounding.
    /// </summary>
    private bool KeepsRowPerLine { get; init; }

    /// <inheritdoc />
    public int MapExtent(in Layout.ExtentRequest request)
    {
        if (!KeepsRowPerLine)
        {
            return request.Axis == Layout.Axis.Horizontal ? ToSurfaceX(request.Value) : ToSurfaceY(request.Value);
        }

        if (request.Axis == Layout.Axis.Horizontal)
        {
            var columns = ToSurfaceX(request.Value);
            return request.Kind == Layout.ExtentKind.Padding ? Math.Min(1, columns) : columns;
        }

        var rows = (int)MathF.Floor(request.Value / designUnitsPerRow);
        if (request.Kind == Layout.ExtentKind.Padding)
        {
            return rows;
        }

        if (request.HasContent)
        {
            return Math.Max(request.ContentLines, rows);
        }

        return request.HasFill && request.Value > 0f ? Math.Max(1, rows) : rows;
    }

    public Layout.Size<int> MeasureText(ReadOnlySpan<char> text, float fontSize) => new(text.Length, 1);

    /// <summary>
    /// The axis-free mapping, used for genuinely axis-free scalars such as a corner radius. Resolved against
    /// the COLUMN size, since a terminal's horizontal resolution is the finer of the two.
    /// </summary>
    public int ToSurface(float designUnits) => (int)MathF.Round(designUnits / designUnitsPerColumn);

    public int ToSurfaceX(float designUnits) => (int)MathF.Round(designUnits / designUnitsPerColumn);

    public int ToSurfaceY(float designUnits) => (int)MathF.Round(designUnits / designUnitsPerRow);
}

/// <summary>
/// Cell painter: walks the SAME arranged <see cref="Layout.Node"/> tree the pixel painter uses, but writes
/// character cells to an <see cref="ITerminalViewport"/>. <see cref="Layout.Node.Background"/> + filled
/// <see cref="Layout.Content.Box"/> become runs of spaces with a background SGR (parent-before-children =
/// correct paint order); <see cref="Layout.Content.Text"/> writes glyphs foreground-only so the painted
/// background shows through; <see cref="Layout.Content.Fill"/> defers to an app callback. <see cref="HitTest"/>
/// maps a (column,row) back to a leaf's <see cref="Layout.Content.Hit"/>, so the arranged rect IS the hit region
/// -- the same auto-binding guarantee the pixel painter gives.
/// </summary>
public static class CellLayout
{
    /// <summary>Paints the arranged tree to <paramref name="viewport"/> in cell coordinates (0-based within the viewport).</summary>
    public static void Paint(ITerminalViewport viewport, ImmutableArray<Layout.ArrangedNode<int>> arranged,
        Action<Layout.Content.Fill, Rect<int>>? drawFill = null)
        => Paint(viewport, arranged, drawFill, reverse: false);

    // No scrollbar is drawn for a Scroll node. The engine reserves ScrollBarWidthPx (6 design units, which is 6
    // columns at CellAuthored) beside the rows of an overflowing list unless its controller's Mode is None, and
    // a six-column bar is not a terminal scrollbar; a cell host sets Mode = None and scrolls by wheel and
    // list cursor. A one-column bar needs its own thumb geometry and a reserve the engine does not offer.

    /// <summary>
    /// Paints as the other overloads do, and declares the tree to <paramref name="surface"/> (clearing what
    /// the last frame declared), so an <see cref="InputRouter"/> over the surface routes it.
    /// </summary>
    public static void Paint(ITerminalViewport viewport, ImmutableArray<Layout.ArrangedNode<int>> arranged,
        CellInputSurface surface, Action<Layout.Content.Fill, Rect<int>>? drawFill = null, bool reverse = false)
    {
        Paint(viewport, arranged, drawFill, reverse, surface.ListCursor, null, surface.Pointer);
        surface.Declare(arranged);
    }

    /// <summary>
    /// Paints the arranged tree, in reverse video when <paramref name="reverse"/> is set: every text run and
    /// every fill carries the attribute after its pen, since each run ends in a reset that would clear an
    /// attribute stated once for the tree.
    /// </summary>
    /// <remarks>
    /// Reverse video is the terminal's own emphasis and needs no colour, which is what makes it the selection
    /// under <see cref="ColorMode.None"/>: a list's cursor row states itself through its pens, and with the pens
    /// suppressed a selected row is indistinguishable from the rest (the TianWen planner, 2026-09-19). The
    /// attribute survives <see cref="ColorMode.None"/> because it is not a colour: the cell buffer models it
    /// apart from the pen and the flush states it in every mode. A separate overload, not an optional
    /// parameter, so a caller compiled against the three-parameter form still binds.
    /// </remarks>
    public static void Paint(ITerminalViewport viewport, ImmutableArray<Layout.ArrangedNode<int>> arranged,
        Action<Layout.Content.Fill, Rect<int>>? drawFill, bool reverse)
        => Paint(viewport, arranged, drawFill, reverse, null, null);

    // A node's FocusBackground resolves against the NEAREST enclosing declared list's cursor (Node.ListOf),
    // so the row a router steps is the row lit; a row under no declaration falls back to the surface's own
    // cursor when one is given.
    // A non-null recorder (CellWireframe.Record) is told each fill, region and text run in paint order;
    // null is the normal path and allocates nothing for it.
    internal static void Paint(ITerminalViewport viewport, ImmutableArray<Layout.ArrangedNode<int>> arranged,
        Action<Layout.Content.Fill, Rect<int>>? drawFill, bool reverse, ListCursor? surfaceCursor,
        CellWireframe.Recorder? recorder, (float X, float Y)? pointer = null)
    {
        var declared = new Stack<(int Depth, ListCursor Cursor)>();
        var mode = viewport.ColorMode;

        // The background each node is painted OVER, resolved from the tree. Text is drawn foreground-only so
        // the fill underneath shows through, which on a live terminal just works: the fill's own SGR is still
        // in effect, because "still in effect" is a property of the real terminal. A CellBuffer has to name a
        // colour for every cell it stores, so it can only record a background it was actually told about --
        // and a foreground-only write tells it nothing, leaving the cell to carry whatever the previous write
        // happened to leave behind. That made a cell's colour depend on which SIBLING was painted before it:
        // rows following a row that ended with a Reset lost their fill and were drawn on black.
        //
        // A stack keyed by depth is the whole mechanism. Entering a node pops every entry at or below its own
        // depth (those belong to a sibling subtree, not to an ancestor), so the top is always the nearest
        // enclosing background -- exactly what the pixel painter composites against.
        var backgrounds = new Stack<(int Depth, RGBAColor32 Color)>();

        // The enclosing hyperlink, tracked exactly like the background above and for the same reason: a link
        // is stated once on the node that OWNS the link and has to reach the text leaves underneath it.
        //
        // A node states one by carrying a HitResult.LinkHit — the hit it already had to carry for the click
        // to work. That is the whole design: the OSC 8 region and the clickable region are the same arranged
        // rect by construction, so a link cannot be drawn somewhere it cannot be clicked or vice versa. It
        // also means no new property on Layout.Node, and nothing to keep in step with the one that exists.
        var links = new Stack<(int Depth, string Url)>();

        // The viewports of the Scroll nodes the walk is inside, innermost on top. The engine has already slid
        // the children by the controller's offset, so a child outside the viewport sits beyond its edge: it
        // is not painted, and one straddling the edge is painted only where it shows. No Scroll node, no
        // entries, and the walk is what it always was.
        var scrolls = new Stack<(int Depth, Rect<int> Clip)>();

        var pixel = IsPixelAuthored(arranged);
        var borderCells = new BorderCells();

        for (var index = 0; index < arranged.Length; index++)
        {
            var arrangedNode = arranged[index];
            var node = arrangedNode.Node;
            var rect = arrangedNode.Bounds;

            while (scrolls.Count > 0 && scrolls.Peek().Depth >= arrangedNode.Depth)
            {
                scrolls.Pop();
            }

            if (scrolls.Count > 0)
            {
                if (!Overlaps(scrolls.Peek().Clip, rect))
                {
                    continue;
                }

                rect = Intersect(scrolls.Peek().Clip, rect);
            }

            while (backgrounds.Count > 0 && backgrounds.Peek().Depth >= arrangedNode.Depth)
            {
                backgrounds.Pop();
            }

            while (links.Count > 0 && links.Peek().Depth >= arrangedNode.Depth)
            {
                links.Pop();
            }

            while (declared.Count > 0 && declared.Peek().Depth >= arrangedNode.Depth)
            {
                declared.Pop();
            }

            if (node.DeclaredList is { } list)
            {
                declared.Push((arrangedNode.Depth, list.Cursor));
            }

            if (node.Hit is HitResult.LinkHit linkHit)
            {
                links.Push((arrangedNode.Depth, linkHit.Url));
            }

            // What this node is painted over, before it contributes a background of its own.
            var under = backgrounds.Count > 0 ? backgrounds.Peek().Color : default;

            // A grid cannot round by fractions of a cell, so any non-zero radius means the same thing here:
            // clip a quarter off each corner cell. See ClipFilledCorners, which also explains why a FILLED
            // rect wants quadrant blocks where a bordered one would want the arc glyphs.
            var rounded = node.CornerRadius > 0f;

            var nodeFilled = false;
            var nodePainted = false;
            var focusCursor = declared.Count > 0 ? declared.Peek().Cursor : surfaceCursor;
            Func<Layout.Node, RGBAColor32?> fillOf = n => ResolveFill(n, focusCursor);
            var nodeFill = node.FocusBackground is { } focus && focusCursor is not null && focusCursor.IsOn(node.Hit)
                ? focus
                : node.HoverBackground is { } hover && pointer is { } at && at.X >= rect.X && at.X < rect.X + rect.Width && at.Y >= rect.Y && at.Y < rect.Y + rect.Height
                    ? hover
                    : node.Background;
            if (nodeFill is { } bg)
            {
                recorder?.Fill(rect, bg);
                nodePainted = PaintFill(viewport, arranged, index, pixel, rect, bg, mode, under, rounded, reverse, fillOf, scrolls.Count > 0 ? scrolls.Peek().Clip : null);
                nodeFilled = true;
                backgrounds.Push((arrangedNode.Depth, bg));
                under = bg;
            }

            recorder?.Node(arrangedNode.Depth, arrangedNode.Bounds, rect, node is Layout.Node.Leaf { Content: Layout.Content.Text },
                nodeFilled, nodePainted, node is Layout.Node.Leaf { Content: Layout.Content.Icon or Layout.Content.TextInput or Layout.Content.Fill },
                // Top plus bottom padding in cells, as the arrangement rounded it (a pixel-authored tree floors to whole rows).
                2 * (pixel ? (int)MathF.Floor(node.PadDown / 16f) : (int)MathF.Round(node.PadDown)),
                thinFill: nodeFilled && IsThinFill(arranged, index, pixel, rect));

            if (node.BorderStyle is { Sides: not Layout.BorderSides.None, Width: > 0f, Color.Alpha: > 0 } border)
            {
                PaintBorder(borderCells, viewport, arrangedNode.Bounds, scrolls.Count > 0 ? scrolls.Peek().Clip : null, border, mode, under, reverse);
                recorder?.Border();
            }

            if (recorder is not null && HitOf(node) is { } regionHit)
            {
                recorder.Region(arrangedNode.Bounds, rect, regionHit);
            }

            if (node.Scroll is { } scrolled)
            {
                // The arranged rect IS the viewport, in cells: one cell is one unit of the controller.
                scrolled.BindViewport(new RectF32(arrangedNode.Bounds.X, arrangedNode.Bounds.Y,
                    arrangedNode.Bounds.Width, arrangedNode.Bounds.Height));
                scrolls.Push((arrangedNode.Depth, rect));
            }

            if (node is not Layout.Node.Leaf leaf)
            {
                continue;
            }

            switch (leaf.Content)
            {
                case Layout.Content.Text text:
                    DrawText(viewport, rect, text, mode, under, links.Count > 0 ? links.Peek().Url : null, reverse, recorder);
                    break;
                case Layout.Content.Box box when box.Color.Alpha > 0:
                    recorder?.Fill(rect, box.Color);
                    recorder?.BoxFill(PaintFill(viewport, arranged, index, pixel, rect, box.Color, mode, under, rounded, reverse, fillOf, scrolls.Count > 0 ? scrolls.Peek().Clip : null),
                        IsThinFill(arranged, index, pixel, rect));
                    break;
                case Layout.Content.Icon icon:
                    DrawText(viewport, rect, IconGlyph(icon), mode, under, null, reverse, recorder);
                    break;
                case Layout.Content.TextInput field:
                    DrawTextInput(viewport, rect, field, mode, under, rounded);
                    break;
                case Layout.Content.Fill fill:
                    drawFill?.Invoke(fill, rect);
                    break;
            }
        }

        FlushBorders(borderCells, viewport);
    }

    /// <summary>The directions a border cell draws toward, as flags, so a junction is the union of its arms.</summary>
    [Flags]
    private enum Arms { None = 0, Up = 1, Down = 2, Left = 4, Right = 8 }

    /// <summary>The border cells of one <see cref="Paint(ITerminalViewport, ImmutableArray{Layout.ArrangedNode{int}}, Action{Layout.Content.Fill, Rect{int}})"/> pass.</summary>
    private sealed class BorderCells
    {
        public readonly Dictionary<(int X, int Y), (Arms Arms, string Pen, bool Rounded)> Cells = [];
    }

    /// <summary>
    /// Collects a declared border (<see cref="Layout.Node.BorderStyle"/>) on the outer ring of <paramref name="bounds"/>,
    /// the cells the engine reserved for it, as arms per cell: a side draws its run (up and down on a vertical
    /// side, left and right on a horizontal one), and a corner where both of its sides are present draws only the
    /// two arms between them, else the run of the side present continues to the edge. Nothing is written here;
    /// <see cref="FlushBorders"/> resolves glyphs once the whole pass has collected, so bordered siblings that meet
    /// can join. Foreground is the border colour, background <paramref name="under"/> (the node's own fill when it
    /// has one). Clipped to the scroll viewport and the terminal.
    /// <para>
    /// A cell painted by two nodes takes the union of their arms and the pen (colour) of the node painted LAST,
    /// so the later sibling's border colour wins on a shared separator.
    /// </para>
    /// </summary>
    private static void PaintBorder(BorderCells cells, ITerminalViewport viewport, Rect<int> bounds, Rect<int>? clip,
        Layout.BorderStyle border, ColorMode mode, RGBAColor32 under, bool reverse)
    {
        var (vw, vh) = viewport.Size;
        int x0 = Math.Max(Math.Max(0, bounds.X), clip?.X ?? int.MinValue), x1 = Math.Min(Math.Min(vw, bounds.Right), clip?.Right ?? int.MaxValue);
        int y0 = Math.Max(Math.Max(0, bounds.Y), clip?.Y ?? int.MinValue), y1 = Math.Min(Math.Min(vh, bounds.Bottom), clip?.Bottom ?? int.MaxValue);
        var pen = new VtStyle(border.Color, under).Apply(mode) + (reverse ? VtStyle.ReverseOn : "");
        var rounded = border.Radius > 0f;
        var sides = border.Sides;
        for (var y = y0; y < y1; y++)
        {
            for (var x = x0; x < x1; x++)
            {
                var t = y == bounds.Y && sides.HasFlag(Layout.BorderSides.Top);
                var b = y == bounds.Bottom - 1 && sides.HasFlag(Layout.BorderSides.Bottom);
                var l = x == bounds.X && sides.HasFlag(Layout.BorderSides.Left);
                var r = x == bounds.Right - 1 && sides.HasFlag(Layout.BorderSides.Right);
                Arms arms;
                if (t && l) arms = Arms.Right | Arms.Down;
                else if (t && r) arms = Arms.Left | Arms.Down;
                else if (b && l) arms = Arms.Right | Arms.Up;
                else if (b && r) arms = Arms.Left | Arms.Up;
                else if (t || b) arms = Arms.Left | Arms.Right;
                else if (l || r) arms = Arms.Up | Arms.Down;
                else continue;
                cells.Cells[(x, y)] = cells.Cells.TryGetValue((x, y), out var prev)
                    ? (prev.Arms | arms, pen, rounded)
                    : (arms, pen, rounded);
            }
        }
    }

    /// <summary>
    /// Resolves and writes the collected border cells. A cell gains the arm toward a neighbour that draws the
    /// opposite arm back at it (a vertical beside the bottom edge of the item above continues it), computed from
    /// the arms as painted so the result does not depend on iteration order; the glyph for the final arm set comes
    /// from <see cref="BorderChars"/> (corner, run, tee or cross). A rounded cell keeps its arc only while it is
    /// still a plain corner; a junction is the light tee or cross.
    /// </summary>
    private static void FlushBorders(BorderCells cells, ITerminalViewport viewport)
    {
        if (cells.Cells.Count == 0)
        {
            return;
        }

        var light = BorderChars.For(Console.Lib.BorderStyle.Light);
        var arc = BorderChars.For(Console.Lib.BorderStyle.Rounded);
        foreach (var ((x, y), cell) in cells.Cells.OrderBy(c => c.Key.Y).ThenBy(c => c.Key.X).ToArray())
        {
            var arms = cell.Arms;
            if (cells.Cells.TryGetValue((x, y - 1), out var up) && up.Arms.HasFlag(Arms.Down)) arms |= Arms.Up;
            if (cells.Cells.TryGetValue((x, y + 1), out var down) && down.Arms.HasFlag(Arms.Up)) arms |= Arms.Down;
            if (cells.Cells.TryGetValue((x - 1, y), out var left) && left.Arms.HasFlag(Arms.Right)) arms |= Arms.Left;
            if (cells.Cells.TryGetValue((x + 1, y), out var right) && right.Arms.HasFlag(Arms.Left)) arms |= Arms.Right;
            var glyphs = cell.Rounded ? arc : light;
            var glyph = arms switch
            {
                Arms.Right | Arms.Down => glyphs.TopLeft,
                Arms.Left | Arms.Down => glyphs.TopRight,
                Arms.Right | Arms.Up => glyphs.BottomLeft,
                Arms.Left | Arms.Up => glyphs.BottomRight,
                Arms.Left | Arms.Right | Arms.Down => light.TeeDown,
                Arms.Left | Arms.Right | Arms.Up => light.TeeUp,
                Arms.Up | Arms.Down | Arms.Right => light.TeeRight,
                Arms.Up | Arms.Down | Arms.Left => light.TeeLeft,
                Arms.Up | Arms.Down | Arms.Left | Arms.Right => light.Cross,
                _ when arms.HasFlag(Arms.Up) || arms.HasFlag(Arms.Down) => light.Vertical,
                _ => light.Horizontal,
            };
            WriteGlyph(viewport, x, y, glyph, cell.Pen);
        }
    }

    /// <summary>Whether two cell rects share at least one cell.</summary>
    internal static bool Overlaps(Rect<int> clip, Rect<int> r)
        => r.X < clip.Right && r.Right > clip.X && r.Y < clip.Bottom && r.Bottom > clip.Y;

    /// <summary>The cells two rects share (empty when they share none).</summary>
    internal static Rect<int> Intersect(Rect<int> clip, Rect<int> r)
    {
        var x0 = Math.Max(clip.X, r.X);
        var y0 = Math.Max(clip.Y, r.Y);
        var x1 = Math.Min(clip.Right, r.Right);
        var y1 = Math.Min(clip.Bottom, r.Bottom);
        return new Rect<int>(x0, y0, Math.Max(0, x1 - x0), Math.Max(0, y1 - y0));
    }

    /// <summary>
    /// The glyph a named icon becomes on a character grid, as a synthesized centred text run -- a cell
    /// surface cannot draw the pixel painter's rectangles, so this is the other half of naming icons by
    /// meaning rather than by drawing.
    /// <para>
    /// Every glyph is chosen from the ranges a terminal font is relied on to carry: plain ASCII where the
    /// mark has an honest one, then U+2580..259F block elements, U+2200..22FF mathematical operators and
    /// U+25A0..25FF geometric shapes, which is the same well every border, scrollbar and tree marker in this
    /// library already draws from. Written as escape sequences rather than literals so a search-and-replace
    /// tool can match them, matching how consumers spell their tab icons.
    /// </para>
    /// <para>
    /// The three theme marks deliberately do NOT reach for the sun and moon of U+2600..263F, which is where
    /// the pixel painter's drawings would send you. Those live in Miscellaneous Symbols, a pictographic
    /// block a monospace face is under no obligation to cover, and the whole reason an icon is named rather
    /// than spelled is that the cell surface should not gamble on coverage. The circles say the same thing
    /// with the same three-way distinction: empty, half, full.
    /// </para>
    /// </summary>
    private static Layout.Content.Text IconGlyph(Layout.Content.Icon icon) =>
        new(icon.Kind switch
        {
            // Quadrant upper-right + lower-left: a 2x2 of filled squares at one cell.
            Layout.IconKind.Grid => "\u259E",
            // "Identical to": three stacked bars, which is the list icon exactly.
            Layout.IconKind.List => "\u2261",
            // Filled triangles, matching the pixel drawings -- which are filled rather than chevrons for a
            // reason that applies here too: a terminal has one cell, and a two-stroke chevron in it is a
            // hairline with a hole that the font's hinting closes up at small point sizes.
            Layout.IconKind.CaretUp => "\u25B2",
            Layout.IconKind.CaretDown => "\u25BC",
            // The horizontal pair, from the same filled-triangle block so all four share a weight. New in
            // DIR.Lib 9.2, and the reason a kind added there is a two-repo change: the pixel side draws it
            // and the cell side spells it, and a kind with only one of those falls back to the placeholder.
            // `EveryIconKindHasAGlyph_SoNoneFallsBackToThePlaceholder` is what says so, and it is exactly
            // the test that caught these two.
            Layout.IconKind.CaretLeft => "\u25C0",
            Layout.IconKind.CaretRight => "\u25B6",
            // ASCII plus, and the MATHEMATICAL minus rather than the ASCII hyphen. The pair exists so the
            // two marks line up in a stepper, and hyphen-minus is the one character that reliably breaks
            // that: most faces draw it shorter and higher than the plus's crossbar, because it is a
            // hyphen first and an operator second. U+2212 is defined to sit on the same axis, and lives in
            // the Mathematical Operators block this file already draws the list icon from.
            Layout.IconKind.Plus => "+",
            Layout.IconKind.Minus => "\u2212",
            // Just the letter: one cell has no room for the brackets, and ASCII 'A' is the safest
            // glyph there is. The meaning is what has to survive the crossing, not the frame.
            Layout.IconKind.Auto => "A",
            // The theme trio as unfilled / half / filled circle, which carries the light-to-dark ordering
            // the three states actually form -- a terminal cannot show a sun's rays or a crescent's horns
            // at one cell anyway, so the ordering is the part worth preserving.
            Layout.IconKind.ThemeLight => "\u25CB",
            Layout.IconKind.ThemeSystem => "\u25D0",
            Layout.IconKind.ThemeDark => "\u25CF",
            // The three tool-palette marks (DIR.Lib 8.11 and 8.12). Each follows this file's rule of
            // preferring a block a monospace face actually covers over the pictograph that looks most
            // like the pixel drawing.
            //
            // Search is a lens with a handle, and DIR.Lib's own note names U+1F50D with U+2315 "for a
            // narrow cell" -- a terminal cell IS the narrow case, permanently, so the alternative is
            // the only candidate here. U+1F50D is in Miscellaneous Symbols and Pictographs, the same
            // emoji territory the theme marks above refuse for exactly this reason.
            Layout.IconKind.Search => "⌕",
            // Pan is four barbed arrows from a centre, stroked rather than filled so it reads by its
            // arms reaching out. The HEAVY open-centre cross rather than the light one, on the same
            // reasoning the carets are filled triangles: at one cell a hairline stroke is what the
            // font's hinting closes up. The open centre is also what keeps it from reading as Plus.
            Layout.IconKind.Pan => "✜",
            // IBeam is a stem with a serif at each end, and APL's I-beam is precisely that shape and
            // is named it. Like Minus it cannot ink its full square -- it is tall and narrow by
            // definition -- and the serifs are the load-bearing part: a bare stem at cell size is a
            // box-drawing separator, which is the one thing it must not be mistaken for.
            Layout.IconKind.IBeam => "⌶",
            // Check (DIR.Lib 11.1) is the plain CHECK MARK, U+2713, not the heavy U+2714 and never the
            // emoji U+2705: a terminal draws an emoji in its own colour, and the tick has to take the
            // cell's pen like every other mark here. Dingbats is covered by the monospace faces a
            // terminal ships with, which is this file's test for a glyph.
            Layout.IconKind.Check => "✓",
            // A kind with no glyph yet shows as a visible placeholder rather than an empty cell, the cell
            // counterpart of the pixel painter drawing nothing for an unhandled kind.
            _ => "?",
        }, icon.Size)
        {
            Color = icon.Color,
            HAlign = TextAlign.Center,
            VAlign = TextAlign.Center,
        };

    /// <summary>
    /// A text field on a character grid. Three things it does differently from the pixel renderer, each
    /// because the surface can genuinely only say one of them:
    /// <list type="bullet">
    /// <item><b>The fill IS the field.</b> A terminal cannot spare a row and a column for the 1px border the
    /// pixel renderer draws, and a one-row field bordered in box-drawing characters would be three rows tall.
    /// So focus is carried by the background alone, which the palette already distinguishes
    /// (<see cref="TextInputColors.BackgroundActive"/>).</item>
    /// <item><b>The caret is the terminal's REAL one</b> (<see cref="ITerminalViewport.SetCaret"/>), not a
    /// painted cell: it can be thinner than a cell and it blinks with no repaint traffic, neither of which a
    /// drawn block manages. This is the same choice the hand-rolled TUI site row already made, and the
    /// reason it could be retired in favour of this leaf rather than reimplemented.</item>
    /// <item><b>An over-long value scrolls; it does not ellipsize.</b> See <see cref="VisibleOffset"/>.</item>
    /// </list>
    /// </summary>
    private static void DrawTextInput(ITerminalViewport viewport, Rect<int> rect,
        Layout.Content.TextInput field, ColorMode mode, RGBAColor32 under, bool rounded)
    {
        var (vw, vh) = viewport.Size;
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        var startCol = Math.Max(0, rect.X);
        var maxW = Math.Min(rect.Right, vw) - startCol;
        if (maxW <= 0)
        {
            return;
        }

        var state = field.State;
        var colors = field.Colors ?? TextInputRenderer.Colors;
        var bg = state.IsActive ? colors.BackgroundActive : colors.Background;

        FillCells(viewport, rect, bg, mode, under, rounded);

        var row = rect.Y + (rect.Height - 1) / 2;
        if (row < 0 || row >= vh)
        {
            return;
        }

        // Placeholder only while empty AND unfocused, matching the pixel renderer: under a live caret a
        // placeholder reads as text the user can edit, and the first keystroke appearing to REPLACE it is
        // then indistinguishable from an insert that went wrong.
        var showPlaceholder = state.Text.Length == 0 && !state.IsActive;
        var value = showPlaceholder ? state.Placeholder : state.Text;
        var fg = showPlaceholder ? colors.Placeholder : colors.Text;

        var cursor = Math.Clamp(state.CursorPos, 0, state.Text.Length);
        var offset = showPlaceholder ? 0 : VisibleOffset(cursor, maxW);
        var visibleLength = Math.Clamp(value.Length - offset, 0, maxW);

        if (visibleLength > 0)
        {
            viewport.SetCursorPosition(startCol, row);
            viewport.Write($"{new VtStyle(fg, bg).Apply(mode)}{value.Substring(offset, visibleLength)}{VtStyle.Reset}");
        }

        // Selection restates the colours of the cells that are selected, over a run already drawn, rather
        // than splitting the first pass into segments -- one clipped range instead of three.
        if (state.IsActive && state.HasSelection)
        {
            var selStart = Math.Max(state.SelectionStart, offset);
            var selEnd = Math.Min(state.SelectionEnd, offset + visibleLength);
            if (selEnd > selStart)
            {
                viewport.SetCursorPosition(startCol + (selStart - offset), row);
                viewport.Write($"{new VtStyle(fg, colors.Selection).Apply(mode)}{value[selStart..selEnd]}{VtStyle.Reset}");
            }
        }

        if (state.IsActive)
        {
            viewport.SetCaret(startCol + (cursor - offset), row, CaretStyle.BlinkingBar);
        }
    }

    /// <summary>
    /// The first character index visible in a field <paramref name="maxChars"/> wide: the least scroll that
    /// keeps the caret inside the field.
    /// <para>
    /// A field cannot ellipsize the way a label does. The <c>…</c> would sit exactly where the text being
    /// edited belongs, and the caret would have no real cell to land on -- so an over-long value scrolls, and
    /// the caret is what it follows.
    /// </para>
    /// <para>
    /// The <c>+ 1</c> is the whole subtlety: a caret sits BETWEEN characters, so a caret at the end of the
    /// value needs a cell of its own past the last glyph. Without it the window is the last
    /// <paramref name="maxChars"/> characters and the caret lands one cell outside the field, on whatever a
    /// neighbour painted there.
    /// </para>
    /// <para>
    /// Deliberately char-based, and so cell-only: a proportional surface solves the same problem in pixels,
    /// which is different arithmetic rather than this scaled. Today it does not solve it at all --
    /// <see cref="TextInputRenderer"/> draws from the left edge and lets the rect clip -- which is
    /// pre-existing and left alone rather than half-fixed on one surface.
    /// </para>
    /// </summary>
    internal static int VisibleOffset(int cursor, int maxChars) => Math.Max(0, cursor - maxChars + 1);

    /// <summary>
    /// Reverse-order (top-most wins) hit test in cell coordinates: invokes the matched leaf's
    /// <see cref="Layout.Content.OnClick"/> and returns its <see cref="Layout.Content.Hit"/>, or null.
    /// </summary>
    public static HitResult? HitTest(ImmutableArray<Layout.ArrangedNode<int>> arranged, int column, int row,
        InputModifier modifiers = InputModifier.None)
    {
        for (var i = arranged.Length - 1; i >= 0; i--)
        {
            var (node, rect) = arranged[i];
            if (HitOf(node) is { } hit && rect.Contains(column, row))
            {
                node.OnClick?.Invoke(modifiers);
                return hit;
            }
        }

        return null;
    }

    /// <summary>
    /// What clicking this node means: its own <see cref="Layout.Node.Hit"/>, or -- for a
    /// <see cref="Layout.Content.TextInput"/> leaf -- the focus hit that comes with BEING a field.
    /// <para>
    /// Derived from the CONTENT rather than required on the node, which is the same rule the pixel painter
    /// follows when it registers the region while drawing the field. Both surfaces therefore answer from the
    /// leaf itself, so a field is clickable because it is a field: a consumer cannot declare one that is
    /// unfocusable by forgetting a hit, and cannot un-declare it by putting a <c>.Clickable()</c> of its own
    /// on the same node.
    /// </para>
    /// <para>
    /// The field wins over a node hit on the same node for the same reason it wins on pixels, where its
    /// registration lands last: a wrapper's handler must not swallow the click that focuses the field.
    /// </para>
    /// </summary>
    private static HitResult? HitOf(Layout.Node node) =>
        node is Layout.Node.Leaf { Content: Layout.Content.TextInput field }
            // No painted geometry, and that is a statement rather than an omission: the geometry exists so
            // a press can be resolved to a CHARACTER, and this surface does not do that -- a terminal
            // reports a cell, and the caret is moved with the arrows. DIR.Lib 10.0 made the parameter
            // required for exactly this reason; a default read as "geometry optional" while silently
            // putting every caret at the start of the field. Wire CaretIndexAt here before filling it in.
            ? new HitResult.TextInputHit(field.State, default)
            : node.Hit;

    /// <summary>
    /// Every text field in the arranged tree, in paint order -- the cell-surface answer to
    /// <c>IPixelWidget.GetRegisteredTextInputs</c>, and what a terminal host feeds
    /// <c>TextInputInteraction.KeyContext.TabFields</c> so Tab cycles fields in the order they appear.
    /// <para>
    /// Read off the tree rather than accumulated during <see cref="Paint(ITerminalViewport, ImmutableArray{Layout.ArrangedNode{int}}, Action{Layout.Content.Fill, Rect{int}})"/>, so it is answerable without
    /// having painted and cannot go stale against a tree that was rebuilt since. Order is the flat pre-order
    /// the engine produced, which IS the visual order, so tab order needs no declaring and no maintaining.
    /// </para>
    /// </summary>
    public static List<TextInputState> TextInputs(ImmutableArray<Layout.ArrangedNode<int>> arranged)
    {
        var result = new List<TextInputState>();
        foreach (var an in arranged)
        {
            if (an.Node is Layout.Node.Leaf { Content: Layout.Content.TextInput field }
                && !result.Contains(field.State))
            {
                result.Add(field.State);
            }
        }

        return result;
    }

    /// <summary>
    /// Serialises the arranged tree to an indented, one-line-per-node text dump — the cell-surface
    /// counterpart to the pixel inspector's <c>describe_layout</c>. Each line is indented by the node's
    /// <see cref="Layout.ArrangedNode{T}.Depth"/> (the flat pre-order list is nested back into a tree),
    /// then names the node kind, its leaf content, its arranged rect <c>(x,y wxh)</c>, and <c>+bg</c> /
    /// <c>+hit</c> markers when the node paints a background or binds a click. Purely diagnostic: it does
    /// not touch the viewport and allocates a fresh string, so keep it out of the per-frame paint path.
    /// </summary>
    public static string Describe(ImmutableArray<Layout.ArrangedNode<int>> arranged)
    {
        var sb = new StringBuilder();
        foreach (var an in arranged)
        {
            var node = an.Node;
            var rect = an.Bounds;
            sb.Append(' ', an.Depth * 2);
            sb.Append(DescribeNode(node));
            sb.Append(" (").Append(rect.X).Append(',').Append(rect.Y)
              .Append(' ').Append(rect.Width).Append('x').Append(rect.Height).Append(')');
            if (node.Background is not null)
            {
                sb.Append(" +bg");
            }
            // A LinkHit is called out separately because it is the one hit that also changes what is PAINTED
            // (an OSC 8 wrap), so "is this text a hyperlink" is answerable from the dump.
            if (node.Hit is HitResult.LinkHit link)
            {
                sb.Append(" +link(").Append(link.Url).Append(')');
            }
            else if (node.Hit is not null)
            {
                sb.Append(" +hit");
            }
            sb.Append('\n');
        }

        return sb.ToString();
    }

    private static string DescribeNode(Layout.Node node) => node switch
    {
        Layout.Node.Stack s => $"Stack[{(s.Axis == Layout.Axis.Horizontal ? "H" : "V")}]",
        Layout.Node.Dock => "Dock",
        Layout.Node.Grid g => $"Grid[{g.Columns}col]",
        Layout.Node.Overlay => "Overlay",
        Layout.Node.Split sp => $"Split[{(sp.Axis == Layout.Axis.Horizontal ? "H" : "V")}]",
        Layout.Node.Leaf leaf => $"Leaf {DescribeContent(leaf.Content)}",
        _ => "Node?",
    };

    private static string DescribeContent(Layout.Content content) => content switch
    {
        Layout.Content.Text t => $"Text \"{t.Value}\"",
        Layout.Content.Box b => b.Color.Alpha > 0 ? "Box(filled)" : "Box(spacer)",
        Layout.Content.Icon i => $"Icon({i.Kind})",
        // The focus marker is the point of dumping a field at all: "which box has the keyboard" is the one
        // question a text-input bug starts from, and it is otherwise invisible in a text dump.
        Layout.Content.TextInput t => $"TextInput(\"{t.State.Text}\"{(t.State.IsActive ? ", active" : "")})",
        Layout.Content.Fill f => f.Key is { } key ? $"Fill(\"{key}\")" : "Fill",
        _ => "Content?",
    };

    /// <summary>
    /// Whether the arrangement was made at a pixel-authored scale: some Fixed extent holds more design units
    /// than the cells it arranged to. At a cell-authored scale a design unit IS a cell, so that never holds
    /// and none of the sub-cell fill rules below may fire.
    /// </summary>
    private static bool IsPixelAuthored(ImmutableArray<Layout.ArrangedNode<int>> arranged)
    {
        foreach (var a in arranged)
        {
            var n = a.Node;
            if ((n.Height.Kind == Layout.SizeKind.Fixed && n.Height.Value >= 2f && a.Bounds.Height < (int)n.Height.Value)
                || (n.Width.Kind == Layout.SizeKind.Fixed && n.Width.Value >= 2f && a.Bounds.Width < (int)n.Width.Value))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The design height that governs a fill: its own Fixed height, else the nearest Fixed ancestor's.</summary>
    private static float? DesignHeight(ImmutableArray<Layout.ArrangedNode<int>> arranged, int index)
    {
        if (arranged[index].Node.Height.Kind == Layout.SizeKind.Fixed)
        {
            return arranged[index].Node.Height.Value;
        }

        var depth = arranged[index].Depth;
        for (var j = index - 1; j >= 0 && depth > 0; j--)
        {
            if (arranged[j].Depth < depth)
            {
                depth = arranged[j].Depth;
                if (arranged[j].Node.Height.Kind == Layout.SizeKind.Fixed)
                {
                    return arranged[j].Node.Height.Value;
                }
            }
        }

        return null;
    }

    private readonly record struct StarSpan(double Start, double End, RGBAColor32? NextFill, bool PrevFilled);

    private static RGBAColor32? OwnFill(Layout.Node n) => n.Background is { } bg ? bg
        : n is Layout.Node.Leaf { Content: Layout.Content.Box { Color.Alpha: > 0 } box } ? box.Color : null;

    // What Paint itself would fill the node with: its FocusBackground when the cursor is on it, else its own fill.
    private static RGBAColor32? ResolveFill(Layout.Node n, ListCursor? cursor)
        => n.FocusBackground is { } focus && cursor is not null && cursor.IsOn(n.Hit) ? focus : OwnFill(n);

    private static bool IsPlainStar(Layout.Node n)
    {
        var w = n.Width;
        return w.Kind == Layout.SizeKind.Star && !w.UpToContent && !(w.Min > 0f) && !(w.Max > 0f) && w.Value > 0f;
    }

    /// <summary>
    /// The exact span in cells of a plain Star child of a horizontal stack: its start is the parent's start plus
    /// the exact widths of the siblings before it (so adjacent filled siblings tile with no gap), its width its
    /// share of what the fixed and auto siblings leave. Null when the node is not one or it is not derivable.
    /// </summary>
    private static StarSpan? ExactStarSpan(ImmutableArray<Layout.ArrangedNode<int>> arranged, int index, Func<Layout.Node, RGBAColor32?> fillOf)
    {
        var self = arranged[index];
        if (!IsPlainStar(self.Node))
        {
            return null;
        }

        var p = index - 1;
        while (p >= 0 && arranged[p].Depth >= self.Depth)
        {
            p--;
        }

        if (p < 0 || arranged[p].Node is not Layout.Node.Stack { Axis: Layout.Axis.Horizontal })
        {
            return null;
        }

        var kids = new List<Layout.ArrangedNode<int>>();
        var selfK = -1;
        for (var j = p + 1; j < arranged.Length && arranged[j].Depth > arranged[p].Depth; j++)
        {
            if (arranged[j].Depth == self.Depth)
            {
                if (j == index)
                {
                    selfK = kids.Count;
                }

                kids.Add(arranged[j]);
            }
        }

        double weights = 0, nonStar = 0, gaps = 0;
        for (var k = 0; k < kids.Count; k++)
        {
            var kw = kids[k].Node.Width;
            if (kw.Kind == Layout.SizeKind.Star)
            {
                if (!IsPlainStar(kids[k].Node))
                {
                    return null;
                }

                weights += kw.Value;
            }
            else
            {
                nonStar += kids[k].Bounds.Width;
            }

            if (k > 0)
            {
                gaps += kids[k].Bounds.X - kids[k - 1].Bounds.Right;
            }
        }

        if (selfK < 0 || weights <= 0)
        {
            return null;
        }

        var starSpace = kids[^1].Bounds.Right - kids[0].Bounds.X - nonStar - gaps;
        double x = kids[0].Bounds.X, start = x, end = x;
        for (var k = 0; k < kids.Count; k++)
        {
            if (k > 0)
            {
                x += kids[k].Bounds.X - kids[k - 1].Bounds.Right;
            }

            var width = kids[k].Node.Width.Kind == Layout.SizeKind.Star
                ? starSpace * kids[k].Node.Width.Value / weights
                : kids[k].Bounds.Width;
            if (k == selfK)
            {
                start = x;
                end = x + width;
            }

            x += width;
        }

        // A neighbour only shares the boundary cell when it touches: a Stack gap between them is cells of what is
        // under, so the gap cell must not take the neighbour's colour.
        RGBAColor32? next = selfK + 1 < kids.Count && IsPlainStar(kids[selfK + 1].Node)
            && kids[selfK + 1].Bounds.X <= kids[selfK].Bounds.Right ? fillOf(kids[selfK + 1].Node) : null;
        var prevFilled = selfK > 0 && IsPlainStar(kids[selfK - 1].Node)
            && kids[selfK].Bounds.X <= kids[selfK - 1].Bounds.Right && fillOf(kids[selfK - 1].Node) is not null;
        return new StarSpan(start, end, next, prevFilled);
    }

    /// <summary>True when a fill is painted as a row of upper half blocks (a thin line): pixel-authored, one row, under 8 design units tall.</summary>
    private static bool IsThinFill(ImmutableArray<Layout.ArrangedNode<int>> arranged, int index, bool pixel, Rect<int> rect)
        => pixel && rect.Height == 1 && DesignHeight(arranged, index) is { } h && h < 8f;

    /// <summary>
    /// Paints a fill. At a pixel-authored scale a fill under half a row (by design height) is a row of upper
    /// half blocks, and a Star child of a horizontal stack covers its exact span to 1/8 cell: whole cells, plus an
    /// eighth-block glyph (U+258F..U+2589) at each fractional edge. Between two filled Star siblings the boundary
    /// cell is one glyph (fg the left colour, bg the right); next to anything else the other side is what is
    /// under. Anything else is FillCells. (The sub-cell path does not clip rounded corners.)
    /// </summary>
    private static bool PaintFill(ITerminalViewport viewport, ImmutableArray<Layout.ArrangedNode<int>> arranged, int index,
        bool pixel, Rect<int> rect, RGBAColor32 color, ColorMode mode, RGBAColor32 under, bool rounded, bool reverse,
        Func<Layout.Node, RGBAColor32?> fillOf, Rect<int>? clip = null)
    {
        var half = IsThinFill(arranged, index, pixel, rect);
        StarSpan? exact = null;
        if (pixel)
        {
            exact = ExactStarSpan(arranged, index, fillOf);
        }

        if (!half && exact is null)
        {
            return FillCells(viewport, rect, color, mode, under, rounded, reverse);
        }

        var (vw, vh) = viewport.Size;
        // The exact span can reach a cell the engine's rounded rect leaves out (its 5.55 end is cell 5 of a
        // 5-wide rect), so glyphs and runs clamp to the real clip (a Scroll viewport, else the viewport),
        // not to the arranged rect.
        var lo = Math.Max(0, clip?.X ?? 0);
        var hi = Math.Min(clip?.Right ?? vw, vw);
        int from = Math.Max(0, rect.X), to = Math.Min(rect.Right, vw);
        int leadEighths = 0, leadCell = -1, trailEighths = 0, trailCell = -1;
        RGBAColor32 trailBg = under;
        if (exact is { } span)
        {
            var sE = (long)Math.Round(span.Start * 8);
            var eE = (long)Math.Round(span.End * 8);
            if (half)
            {
                from = (int)Math.Max(lo, (sE + 4) / 8);
                to = (int)Math.Min(hi, (eE + 4) / 8);
            }
            else
            {
                from = (int)Math.Max(lo, (sE + 7) / 8);
                to = (int)Math.Min(hi, eE / 8);
                // sE > 0: a start in (-1, 0) cells rounds toward zero to cell 0 with a negative remainder.
                if (sE > 0 && sE % 8 != 0 && !span.PrevFilled)
                {
                    leadCell = (int)(sE / 8);
                    leadEighths = (int)(sE % 8);
                }

                if (eE % 8 != 0)
                {
                    trailCell = (int)(eE / 8);
                    trailEighths = (int)(eE % 8);
                    trailBg = span.NextFill ?? under;
                }

                if (leadCell == trailCell)
                {
                    // The fill sits inside one cell and no eighth-block glyph shows both edges: keep the edge
                    // nearer its cell side, so the offset survives to the nearer eighth rather than vanishing.
                    if (leadEighths <= 8 - trailEighths)
                    {
                        leadCell = -1;
                    }
                    else
                    {
                        trailCell = -1;
                    }
                }
            }
        }

        var wrote = false;
        var rev = reverse ? VtStyle.ReverseOn : "";
        var pen = new VtStyle(color, under).Apply(mode) + rev;
        var solid = new VtStyle(color, color).Apply(mode) + rev;
        var leadPen = new VtStyle(under, color).Apply(mode) + rev;
        var trailPen = new VtStyle(color, trailBg).Apply(mode) + rev;
        for (var row = Math.Max(0, rect.Y); row < Math.Min(rect.Bottom, vh); row++)
        {
            if (to > from)
            {
                wrote = true;
                viewport.SetCursorPosition(from, row);
                viewport.Write(half
                    ? $"{VtStyle.Reset}{pen}{new string('▀', to - from)}{VtStyle.Reset}"
                    : $"{solid}{new string(' ', to - from)}{VtStyle.Reset}");
            }

            if (leadCell >= lo && leadCell < hi)
            {
                wrote = true;
                viewport.SetCursorPosition(leadCell, row);
                viewport.Write($"{VtStyle.Reset}{leadPen}{(char)(0x2590 - leadEighths)}{VtStyle.Reset}");
            }

            if (trailCell >= lo && trailCell < hi)
            {
                wrote = true;
                viewport.SetCursorPosition(trailCell, row);
                viewport.Write($"{VtStyle.Reset}{trailPen}{(char)(0x2590 - trailEighths)}{VtStyle.Reset}");
            }
        }

        return wrote;
    }

    /// <param name="under">The background this fill sits on, needed only by the rounded-corner clip: the
    /// quadrant it omits has to show the enclosing colour, and a terminal cell cannot composite, so that
    /// colour must be stated rather than left to whatever the terminal currently has.</param>
    private static bool FillCells(ITerminalViewport viewport, Rect<int> rect, RGBAColor32 color, ColorMode mode,
        RGBAColor32 under, bool rounded = false, bool reverse = false)
    {
        var (vw, vh) = viewport.Size;
        var x = Math.Max(0, rect.X);
        var width = Math.Min(rect.Right, vw) - x;
        if (width <= 0)
        {
            return false;
        }

        var esc = new VtStyle(color, color).Apply(mode) + (reverse ? VtStyle.ReverseOn : "");
        var spaces = new string(' ', width);
        var yEnd = Math.Min(rect.Bottom, vh);
        for (var row = Math.Max(0, rect.Y); row < yEnd; row++)
        {
            viewport.SetCursorPosition(x, row);
            viewport.Write($"{esc}{spaces}{VtStyle.Reset}");
        }

        if (rounded)
        {
            ClipFilledCorners(viewport, rect, color, mode, under, x, width, yEnd);
        }

        return yEnd > Math.Max(0, rect.Y);
    }

    /// <summary>
    /// Softens the four corners of an already-FILLED rect by redrawing each corner CELL as a
    /// three-quadrant block (U+2599, U+259B, U+259C, U+259F) in the fill colour, foreground-only -- so the
    /// missing quadrant shows whatever the parent painted underneath and the corner reads as clipped.
    /// <para>
    /// <b>A filled rect and a bordered one want different glyphs, and this is the filled one.</b> The arc
    /// glyphs (U+256D..U+2570) are what this drew first, and they are the right answer for an UNFILLED box
    /// whose outline is drawn in box-drawing characters -- that is precisely what they were designed to
    /// join. They are the wrong answer for a solid fill: an arc is a thin stroke, so a corner cell drawn
    /// that way is ~90% parent colour, and on a high-contrast card that reads as a bite punched out of the
    /// shape rather than a softened corner. (On the TianWen home board -- a blue card on a near-black page
    /// -- it read as damage.) A three-quadrant block covers three quarters of the cell, so the corner loses
    /// a QUARTER cell instead of a whole one, which is the smallest bite a character grid can express.
    /// </para>
    /// <para>
    /// There is deliberately no arc branch here: both <see cref="FillCells"/> call sites are gated on a
    /// fill (<see cref="Layout.Node.Background"/>, or a <see cref="Layout.Content.Box"/> with alpha), and
    /// the layout DSL has no border/stroke chrome at all -- so an unfilled rounded box is currently
    /// unexpressible. If a border property is added to <c>Layout.Node</c>, the arc glyphs are what should
    /// render its corners, and the branch belongs at that call site rather than in here.
    /// </para>
    /// <para>
    /// The <i>magnitude</i> of <see cref="Layout.Node.CornerRadius"/> is ignored either way: a grid cannot
    /// round by fractions of a cell, and there is no wider rounded form to scale up to. Any non-zero radius
    /// means the same quarter-cell clip.
    /// </para>
    /// Skipped entirely for a rect too small to have distinct corners, where clipping all four would shape
    /// the fill rather than soften it.
    /// </summary>
    private static void ClipFilledCorners(ITerminalViewport viewport, Rect<int> rect, RGBAColor32 color, ColorMode mode,
        RGBAColor32 under, int x, int width, int yEnd)
    {
        var top = Math.Max(0, rect.Y);
        var bottom = yEnd - 1;
        var right = x + width - 1;

        // Below 3x3 the corners are the whole shape; rounding would eat it.
        if (width < 3 || bottom - top < 2)
        {
            return;
        }

        // Each glyph omits exactly the quadrant pointing away from the rect's interior. The fill colour is
        // the GLYPH and the enclosing colour is its background, which is what makes the omitted quadrant read
        // as clipped -- so both are stated, or the quadrant shows black instead of the page behind the card.
        var pen = new VtStyle(color, under).Apply(mode);
        WriteGlyph(viewport, x, top, '▟', pen);        // top-left: upper-left quadrant omitted
        WriteGlyph(viewport, right, top, '▙', pen);    // top-right: upper-right omitted
        WriteGlyph(viewport, x, bottom, '▜', pen);     // bottom-left: lower-left omitted
        WriteGlyph(viewport, right, bottom, '▛', pen); // bottom-right: lower-right omitted
    }

    /// <summary>
    /// Shortens <paramref name="s"/> to exactly <paramref name="maxW"/> cells, sacrificing the end
    /// <paramref name="trim"/> names and spending one cell on the ellipsis.
    /// <para>
    /// A cell surface has to cut somewhere — it measures in whole characters — and WHICH end it cuts is the
    /// run's business, not the painter's. Before <see cref="Layout.Content.Text.Trim"/> existed this was
    /// unconditionally end-trimmed, so a caller with a path had to pre-truncate against a width it derived
    /// itself; that width is the one thing a row no longer knows, so in practice the path column just lost
    /// its filename.
    /// </para>
    /// <para>
    /// At <paramref name="maxW"/> of 1 there is no room for both a glyph and an ellipsis, so the single cell
    /// goes to the character from the surviving end rather than to a lone "…" that says nothing.
    /// </para>
    /// <para>
    /// <see cref="TextTrim.Middle"/> needs no degradation: it is a character-count cut like Start and End,
    /// so a cell surface honours it exactly as a pixel one does.
    /// </para>
    /// <para>
    /// Two of the policies a PIXEL surface can honour, this one cannot, so each degrades to the nearest cell
    /// behaviour. <see cref="TextTrim.Shrink"/> asks for a smaller face and a character grid has exactly one
    /// size, so it end-trims — a shorter whole run being unavailable, the head is the next best thing.
    /// <see cref="TextTrim.None"/> asks to overflow, and here overflowing would overwrite a neighbouring
    /// cell, so it hard-clips: the same cut with no ellipsis, because nothing should claim a removal the
    /// author asked not to make.
    /// </para>
    /// </summary>
    private static string Ellipsize(string s, int maxW, TextTrim trim)
    {
        if (trim == TextTrim.None)
        {
            return s[..maxW];
        }

        if (maxW <= 1)
        {
            return trim == TextTrim.Start ? s[^maxW..] : s[..maxW];
        }

        if (trim == TextTrim.Middle)
        {
            // Equal count from each end, the ellipsis taking the odd cell. At maxW 2 that leaves one
            // cell to split between two ends, so the head keeps it and the tail is dropped -- the
            // same "the surviving end is the head" tie-break the maxW <= 1 case above makes.
            var keep = (maxW - 1) / 2;
            return keep == 0
                ? s[..(maxW - 1)] + '…'
                : s[..keep] + '…' + s[^(maxW - 1 - keep)..];
        }

        return trim == TextTrim.Start
            ? '…' + s[(s.Length - maxW + 1)..]
            : s[..(maxW - 1)] + '…';
    }

    private static void WriteGlyph(ITerminalViewport viewport, int column, int row, char glyph, string pen)
    {
        viewport.SetCursorPosition(column, row);
        viewport.Write($"{VtStyle.Reset}{pen}{glyph}{VtStyle.Reset}");
    }

    /// <param name="under">The background this text is painted over, from the nearest enclosing node that
    /// painted one (see the stack in <see cref="Paint(ITerminalViewport, ImmutableArray{Layout.ArrangedNode{int}}, Action{Layout.Content.Fill, Rect{int}})"/>). Stated explicitly rather than inherited, so the
    /// cell carries it; an unset value is emitted as the terminal's default rather than as black.</param>
    /// <param name="link">
    /// The nearest enclosing <see cref="HitResult.LinkHit"/>'s target, or null. Only TEXT is wrapped: the
    /// padding and fills around it are cells the row happens to occupy, and a terminal that underlines them
    /// as part of the link draws a hyperlink stretching across gaps the reader cannot see any text in.
    /// </param>
    private static void DrawText(ITerminalViewport viewport, Rect<int> rect, Layout.Content.Text text, ColorMode mode,
        RGBAColor32 under, string? link = null, bool reverse = false, CellWireframe.Recorder? recorder = null)
    {
        var (vw, vh) = viewport.Size;
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        var maxW = Math.Min(rect.Right, vw) - Math.Max(0, rect.X);
        if (maxW <= 0)
        {
            return;
        }

        var s = text.Value;
        if (s.Length > maxW)
        {
            s = Ellipsize(s, maxW, text.Trim);
        }

        var len = s.Length;
        var startCol = text.HAlign switch
        {
            TextAlign.Center => rect.X + (maxW - len) / 2,
            TextAlign.Far => rect.X + (maxW - len),
            _ => rect.X,
        };
        var row = text.VAlign switch
        {
            TextAlign.Center => rect.Y + (rect.Height - 1) / 2,
            TextAlign.Far => rect.Y + rect.Height - 1,
            _ => rect.Y,
        };
        if (row < 0 || row >= vh)
        {
            return;
        }

        // States the background as well as the foreground, so the cells keep the fill painted underneath
        // WITHOUT depending on it still being the terminal's current SGR state. Foreground-only was correct
        // for a live terminal and silently wrong for a cell buffer -- see the stack in Paint.
        //
        // The pen is stated INSIDE the link, and the trailing SGR reset does not close it: SGR and OSC 8 are
        // independent state in a terminal, and CellBuffer models them the same way. ColorMode.None writes no
        // escapes at all, so it gets no link either -- a plain-text dump stays plain text.
        var open = link is not null && mode != ColorMode.None ? Osc8.Open(link) : "";
        var close = open.Length > 0 ? Osc8.Close : "";

        if (recorder is not null)
        {
            var inkColumn = text.HAlign switch
            {
                TextAlign.Center => rect.X + (maxW - text.Value.Length) / 2,
                TextAlign.Far => rect.X + (maxW - text.Value.Length),
                _ => rect.X,
            };
            // An ellipsized run was shortened ON PURPOSE: its ink is what was written (the ellipsis stands for the
            // rest), whichever end or middle the ellipsis sits at. Only a hard clip (TextTrim.None) keeps the
            // untrimmed ink, which is the cut DIR.Lib's rule reports.
            var ellipsized = text.Trim != TextTrim.None && s.Length < text.Value.Length;
            recorder.Text(rect, s, ellipsized ? s.Length : text.Value.Length, ellipsized ? startCol : inkColumn, row, startCol);
            recorder.TextFact(row, text.Value.Length, s.Length);
        }

        viewport.SetCursorPosition(startCol, row);
        var attribute = reverse ? VtStyle.ReverseOn : "";
        viewport.Write($"{open}{new VtStyle(text.Color, under).Apply(mode)}{attribute}{s}{VtStyle.Reset}{close}");
    }
}
