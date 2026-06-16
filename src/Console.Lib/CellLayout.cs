using System;
using System.Collections.Immutable;
using DIR.Lib;

namespace Console.Lib;

/// <summary>
/// The terminal-surface <see cref="IMeasureContext{T}"/>: text width is the character count (one row tall),
/// and design-unit scalars are authored in cells for the TUI so they round to whole cells. (Wide-char /
/// East-Asian-width measurement is a documented follow-up; v1 is char-count, matching the rest of Console.Lib.)
/// </summary>
public sealed class CellMeasureContext : IMeasureContext<int>
{
    public Size<int> MeasureText(ReadOnlySpan<char> text, float fontSize) => new(text.Length, 1);

    public int ToSurface(float designUnits) => (int)MathF.Round(designUnits);
}

/// <summary>
/// Cell painter: walks the SAME arranged <see cref="LayoutNode"/> tree the pixel painter uses, but writes
/// character cells to an <see cref="ITerminalViewport"/>. <see cref="LayoutNode.Background"/> + filled
/// <see cref="LayoutContent.Box"/> become runs of spaces with a background SGR (parent-before-children =
/// correct paint order); <see cref="LayoutContent.Text"/> writes glyphs foreground-only so the painted
/// background shows through; <see cref="LayoutContent.Fill"/> defers to an app callback. <see cref="HitTest"/>
/// maps a (column,row) back to a leaf's <see cref="LayoutContent.Hit"/>, so the arranged rect IS the hit region
/// -- the same auto-binding guarantee the pixel painter gives.
/// </summary>
public static class CellLayout
{
    /// <summary>Paints the arranged tree to <paramref name="viewport"/> in cell coordinates (0-based within the viewport).</summary>
    public static void Paint(ITerminalViewport viewport, ImmutableArray<ArrangedNode<int>> arranged,
        Action<LayoutContent.Fill, Rect<int>>? drawFill = null)
    {
        var mode = viewport.ColorMode;
        foreach (var (node, rect) in arranged)
        {
            if (node.Background is { } bg)
            {
                FillCells(viewport, rect, bg, mode);
            }

            if (node is not LayoutNode.Leaf leaf)
            {
                continue;
            }

            switch (leaf.Content)
            {
                case LayoutContent.Text text:
                    DrawText(viewport, rect, text, mode);
                    break;
                case LayoutContent.Box box when box.Color.Alpha > 0:
                    FillCells(viewport, rect, box.Color, mode);
                    break;
                case LayoutContent.Fill fill:
                    drawFill?.Invoke(fill, rect);
                    break;
            }
        }
    }

    /// <summary>
    /// Reverse-order (top-most wins) hit test in cell coordinates: invokes the matched leaf's
    /// <see cref="LayoutContent.OnClick"/> and returns its <see cref="LayoutContent.Hit"/>, or null.
    /// </summary>
    public static HitResult? HitTest(ImmutableArray<ArrangedNode<int>> arranged, int column, int row,
        InputModifier modifiers = InputModifier.None)
    {
        for (var i = arranged.Length - 1; i >= 0; i--)
        {
            var (node, rect) = arranged[i];
            if (node is LayoutNode.Leaf { Content: { Hit: { } hit } content } && rect.Contains(column, row))
            {
                content.OnClick?.Invoke(modifiers);
                return hit;
            }
        }

        return null;
    }

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

    private static void DrawText(ITerminalViewport viewport, Rect<int> rect, LayoutContent.Text text, ColorMode mode)
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
