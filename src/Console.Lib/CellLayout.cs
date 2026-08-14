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
    public static CellMeasureContext PixelAuthored { get; } = new CellMeasureContext(8f, 16f);

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
    {
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

        foreach (var arrangedNode in arranged)
        {
            var node = arrangedNode.Node;
            var rect = arrangedNode.Bounds;

            while (backgrounds.Count > 0 && backgrounds.Peek().Depth >= arrangedNode.Depth)
            {
                backgrounds.Pop();
            }

            while (links.Count > 0 && links.Peek().Depth >= arrangedNode.Depth)
            {
                links.Pop();
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

            if (node.Background is { } bg)
            {
                FillCells(viewport, rect, bg, mode, under, rounded);
                backgrounds.Push((arrangedNode.Depth, bg));
                under = bg;
            }

            if (node is not Layout.Node.Leaf leaf)
            {
                continue;
            }

            switch (leaf.Content)
            {
                case Layout.Content.Text text:
                    DrawText(viewport, rect, text, mode, under, links.Count > 0 ? links.Peek().Url : null);
                    break;
                case Layout.Content.Box box when box.Color.Alpha > 0:
                    FillCells(viewport, rect, box.Color, mode, under, rounded);
                    break;
                case Layout.Content.Icon icon:
                    DrawText(viewport, rect, IconGlyph(icon), mode, under, null);
                    break;
                case Layout.Content.TextInput field:
                    DrawTextInput(viewport, rect, field, mode, under, rounded);
                    break;
                case Layout.Content.Fill fill:
                    drawFill?.Invoke(fill, rect);
                    break;
            }
        }
    }

    /// <summary>
    /// The glyph a named icon becomes on a character grid, as a synthesized centred text run -- a cell
    /// surface cannot draw the pixel painter's rectangles, so this is the other half of naming icons by
    /// meaning rather than by drawing.
    /// <para>
    /// Every glyph is chosen from the ranges a terminal font is relied on to carry: U+2580..259F block
    /// elements, U+2200..22FF mathematical operators and U+25A0..25FF geometric shapes, which is the same
    /// well every border, scrollbar and tree marker in this library already draws from. Written as escape
    /// sequences rather than literals so a search-and-replace tool can match them, matching how consumers
    /// spell their tab icons.
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
            // Just the letter: one cell has no room for the brackets, and ASCII 'A' is the safest
            // glyph there is. The meaning is what has to survive the crossing, not the frame.
            Layout.IconKind.Auto => "A",
            // The theme trio as unfilled / half / filled circle, which carries the light-to-dark ordering
            // the three states actually form -- a terminal cannot show a sun's rays or a crescent's horns
            // at one cell anyway, so the ordering is the part worth preserving.
            Layout.IconKind.ThemeLight => "\u25CB",
            Layout.IconKind.ThemeSystem => "\u25D0",
            Layout.IconKind.ThemeDark => "\u25CF",
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
    private static int VisibleOffset(int cursor, int maxChars) => Math.Max(0, cursor - maxChars + 1);

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
            ? new HitResult.TextInputHit(field.State)
            : node.Hit;

    /// <summary>
    /// Every text field in the arranged tree, in paint order -- the cell-surface answer to
    /// <c>IPixelWidget.GetRegisteredTextInputs</c>, and what a terminal host feeds
    /// <c>TextInputInteraction.KeyContext.TabFields</c> so Tab cycles fields in the order they appear.
    /// <para>
    /// Read off the tree rather than accumulated during <see cref="Paint"/>, so it is answerable without
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

    /// <param name="under">The background this fill sits on, needed only by the rounded-corner clip: the
    /// quadrant it omits has to show the enclosing colour, and a terminal cell cannot composite, so that
    /// colour must be stated rather than left to whatever the terminal currently has.</param>
    private static void FillCells(ITerminalViewport viewport, Rect<int> rect, RGBAColor32 color, ColorMode mode,
        RGBAColor32 under, bool rounded = false)
    {
        var (vw, vh) = viewport.Size;
        var x = Math.Max(0, rect.X);
        var width = Math.Min(rect.Right, vw) - x;
        if (width <= 0)
        {
            return;
        }

        var esc = new VtStyle(color, color).Apply(mode);
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
    /// painted one (see the stack in <see cref="Paint"/>). Stated explicitly rather than inherited, so the
    /// cell carries it; an unset value is emitted as the terminal's default rather than as black.</param>
    /// <param name="link">
    /// The nearest enclosing <see cref="HitResult.LinkHit"/>'s target, or null. Only TEXT is wrapped: the
    /// padding and fills around it are cells the row happens to occupy, and a terminal that underlines them
    /// as part of the link draws a hyperlink stretching across gaps the reader cannot see any text in.
    /// </param>
    private static void DrawText(ITerminalViewport viewport, Rect<int> rect, Layout.Content.Text text, ColorMode mode,
        RGBAColor32 under, string? link = null)
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

        viewport.SetCursorPosition(startCol, row);
        viewport.Write($"{open}{new VtStyle(text.Color, under).Apply(mode)}{s}{VtStyle.Reset}{close}");
    }
}
