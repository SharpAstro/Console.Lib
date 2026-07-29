namespace Console.Lib;

public interface ITerminalViewport
{
    (int Column, int Row) Offset { get; }
    (int Width, int Height) Size { get; }
    void SetCursorPosition(int left, int top);
    void Write(string text);
    void WriteLine(string? text = null);
    TermCell CellSize { get; }

    /// <summary>
    /// Prepares for RAW bytes about to be written to <see cref="OutputStream"/> — a Sixel blit — by
    /// flushing any buffered cell output and positioning the terminal's real cursor.
    /// <para>
    /// Needed because raw bytes bypass <see cref="Write"/> entirely, so on a buffered terminal
    /// <see cref="SetCursorPosition"/> moves only the BUFFER's cursor and the blit would land wherever the
    /// real cursor was left. The default is today's behaviour, so an unbuffered terminal is unaffected.
    /// </para>
    /// </summary>
    void BeginRawOutput(int column, int row) => SetCursorPosition(column, row);

    /// <summary>
    /// Declares that raw output owns this cell region, so a diffing terminal never paints a glyph over it —
    /// which would punch a hole in the picture. No-op unless the terminal is buffered.
    /// </summary>
    void MarkRawRegion(int column, int row, int width, int height) { }

    /// <summary>Viewport size in pixels (columns * cellWidth, rows * cellHeight).</summary>
    (uint Width, uint Height) PixelSize
    {
        get
        {
            var (cols, rows) = Size;
            var cell = CellSize;
            return ((uint)cols * cell.Width, (uint)rows * cell.Height);
        }
    }
    void Flush();
    Stream OutputStream { get; }

    /// <summary>Color mode supported by this terminal. Defaults to SGR-16.</summary>
    ColorMode ColorMode => ColorMode.Sgr16;
}

public static class TerminalViewportExtensions
{
    /// <summary>
    /// Overwrites the current line with <paramref name="text"/> using carriage return,
    /// padding with spaces to erase any previous content. Does not advance to the next line.
    /// </summary>
    public static void WriteInPlace(this ITerminalViewport terminal, string text)
    {
        var padding = Math.Max(0, terminal.Size.Width - text.Length);
        terminal.Write($"\r{text}{new string(' ', padding)}\r{text}");
    }
}
