using System.Text;

namespace Console.Lib;

/// <summary>
/// Renders a <see cref="SoftText"/> block into a viewport at a given
/// <c>(col, row)</c> origin. Every cell in the W×H rectangle is emitted, so
/// callers can paint on top of arbitrary backgrounds without leaving gaps.
/// One <see cref="ITerminalViewport.Write"/> call per row keeps the write
/// count low — the consumer's loop is bound by the block's Height, not by
/// the number of styled spans.
/// </summary>
public static class SoftRenderer
{
    /// <param name="viewport">Target viewport. Cursor positions are local to it.</param>
    /// <param name="col">Left column of the block within the viewport.</param>
    /// <param name="row">Top row of the block within the viewport.</param>
    /// <param name="text">The W×H block to render.</param>
    /// <param name="mode">Active color mode (read from <see cref="ITerminalViewport.ColorMode"/>).</param>
    /// <param name="background">
    /// If non-null, applied at the start of every row and re-applied after each
    /// styled span so padding cells inherit it. Pass null for transparent
    /// padding (default terminal background).
    /// </param>
    public static void Render(
        ITerminalViewport viewport,
        int col,
        int row,
        SoftText text,
        ColorMode mode,
        VtStyle? background = null)
    {
        var sb = new StringBuilder(capacity: text.Width * 4);
        string bg = background.HasValue ? background.Value.Apply(mode) : "";

        for (int i = 0; i < text.Height; i++)
        {
            if (!TrySetCursor(viewport, col, row + i)) continue;

            sb.Clear();
            sb.Append(bg);

            var line = i < text.Lines.Count ? text.Lines[i] : null;
            int visibleLen = Math.Min(line?.VisibleLength ?? 0, text.Width);
            int extra = text.Width - visibleLen;
            int leftPad, rightPad;
            switch (line?.Align ?? HAlign.Left)
            {
                case HAlign.Center: leftPad = extra / 2; rightPad = extra - leftPad; break;
                case HAlign.Right:  leftPad = extra; rightPad = 0; break;
                default:            leftPad = 0; rightPad = extra; break;
            }

            if (leftPad > 0) sb.Append(' ', leftPad);

            if (line is not null)
            {
                int remaining = text.Width;
                foreach (var span in line.Spans)
                {
                    if (remaining <= 0) break;
                    int take = Math.Min(span.Text.Length, remaining);
                    if (span.Style is { } st)
                    {
                        sb.Append(st.Apply(mode));
                        if (take == span.Text.Length) sb.Append(span.Text);
                        else sb.Append(span.Text, 0, take);
                        // Restore background so any following padding/span on
                        // this row keeps the cell background colour.
                        if (background.HasValue) sb.Append(bg);
                        else sb.Append(VtStyle.Reset);
                    }
                    else
                    {
                        if (take == span.Text.Length) sb.Append(span.Text);
                        else sb.Append(span.Text, 0, take);
                    }
                    remaining -= take;
                }
            }

            if (rightPad > 0) sb.Append(' ', rightPad);
            sb.Append(VtStyle.Reset);
            viewport.Write(sb.ToString());
        }
    }

    private static bool TrySetCursor(ITerminalViewport vp, int col, int row)
    {
        if (col < 0 || row < 0) return false;
        if (col >= vp.Size.Width || row >= vp.Size.Height) return false;
        vp.SetCursorPosition(col, row);
        return true;
    }
}
