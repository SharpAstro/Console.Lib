using System;
using System.Collections.Immutable;
using System.Text;
using DIR.Lib;

namespace Console.Lib;

/// <summary>
/// The terminal-surface <see cref="Layout.IMeasureContext{T}"/>: text width is the character count (one row tall),
/// and design-unit scalars are authored in cells for the TUI so they round to whole cells. (Wide-char /
/// East-Asian-width measurement is a documented follow-up; v1 is char-count, matching the rest of Console.Lib.)
/// </summary>
public sealed class CellMeasureContext : Layout.IMeasureContext<int>
{
    public Layout.Size<int> MeasureText(ReadOnlySpan<char> text, float fontSize) => new(text.Length, 1);

    public int ToSurface(float designUnits) => (int)MathF.Round(designUnits);
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
            if (node.Background is { } bg)
            {
                FillCells(viewport, rect, bg, mode);
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
                    FillCells(viewport, rect, box.Color, mode);
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

    private static void FillCells(ITerminalViewport viewport, Rect<int> rect, RGBAColor32 color, ColorMode mode)
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
