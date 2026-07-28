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
/// <see cref="CellAuthored"/> and <see cref="PixelAuthored"/>.
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
        foreach (var (node, rect) in arranged)
        {
            // A grid cannot round by fractions of a cell, so any non-zero radius means the same thing
            // here: knock one cell off each corner and draw an arc glyph. See RoundCorners.
            var rounded = node.CornerRadius > 0f;

            if (node.Background is { } bg)
            {
                FillCells(viewport, rect, bg, mode, rounded);
            }

            if (node is not Layout.Node.Leaf leaf)
            {
                continue;
            }

            switch (leaf.Content)
            {
                case Layout.Content.Text text:
                    DrawText(viewport, rect, text, mode);
                    break;
                case Layout.Content.Box box when box.Color.Alpha > 0:
                    FillCells(viewport, rect, box.Color, mode, rounded);
                    break;
                case Layout.Content.Fill fill:
                    drawFill?.Invoke(fill, rect);
                    break;
            }
        }
    }

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
            if (node.Hit is { } hit && rect.Contains(column, row))
            {
                node.OnClick?.Invoke(modifiers);
                return hit;
            }
        }

        return null;
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
            if (node.Hit is not null)
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
        Layout.Content.Fill f => f.Key is { } key ? $"Fill(\"{key}\")" : "Fill",
        _ => "Content?",
    };

    private static void FillCells(ITerminalViewport viewport, Rect<int> rect, RGBAColor32 color, ColorMode mode,
        bool rounded = false)
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
            RoundCorners(viewport, rect, color, mode, x, width, yEnd);
        }
    }

    /// <summary>
    /// Approximates a rounded corner on a character grid by replacing the four corner CELLS of an
    /// already-filled rect with arc glyphs (U+256D..U+2570) drawn foreground-only, so the curve reads in
    /// the fill colour against whatever the parent painted underneath.
    /// <para>
    /// A grid cannot round by fractions of a cell, so the <i>magnitude</i> of
    /// <see cref="Layout.Node.CornerRadius"/> is deliberately ignored here -- any non-zero radius knocks
    /// exactly one cell off each corner. Scaling the bite with the radius would need multi-cell arcs, and
    /// Unicode has arc forms for corners ONLY (there is no rounded tee or cross), so a larger arc cannot
    /// be drawn without inventing it out of quadrant blocks. One cell is the honest approximation.
    /// </para>
    /// Skipped entirely for a rect too small to have distinct corners, where knocking out cells would
    /// erase most of the fill rather than soften it.
    /// </summary>
    private static void RoundCorners(ITerminalViewport viewport, Rect<int> rect, RGBAColor32 color, ColorMode mode,
        int x, int width, int yEnd)
    {
        var top = Math.Max(0, rect.Y);
        var bottom = yEnd - 1;
        var right = x + width - 1;

        // Below 3x3 the corners are the whole shape; rounding would eat it.
        if (width < 3 || bottom - top < 2)
        {
            return;
        }

        var fg = new VtStyle(color, color).ApplyFg(mode);
        WriteGlyph(viewport, x, top, '╭', fg);      // top-left
        WriteGlyph(viewport, right, top, '╮', fg);  // top-right
        WriteGlyph(viewport, x, bottom, '╰', fg);   // bottom-left
        WriteGlyph(viewport, right, bottom, '╯', fg); // bottom-right
    }

    private static void WriteGlyph(ITerminalViewport viewport, int column, int row, char glyph, string fg)
    {
        viewport.SetCursorPosition(column, row);
        viewport.Write($"{VtStyle.Reset}{fg}{glyph}{VtStyle.Reset}");
    }

    private static void DrawText(ITerminalViewport viewport, Rect<int> rect, Layout.Content.Text text, ColorMode mode)
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
            s = maxW > 1 ? s[..(maxW - 1)] + '…' : s[..maxW];
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

        // Foreground-only so the cells keep whatever Background was painted underneath.
        viewport.SetCursorPosition(startCol, row);
        viewport.Write($"{new VtStyle(text.Color, text.Color).ApplyFg(mode)}{s}{VtStyle.Reset}");
    }
}
