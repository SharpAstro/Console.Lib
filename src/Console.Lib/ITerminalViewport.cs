namespace Console.Lib;

public interface ITerminalViewport
{
    (int Column, int Row) Offset { get; }
    (int Width, int Height) Size { get; }
    void SetCursorPosition(int left, int top);
    void Write(string text);
    void WriteLine(string? text = null);
    TermCell CellSize { get; }

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
