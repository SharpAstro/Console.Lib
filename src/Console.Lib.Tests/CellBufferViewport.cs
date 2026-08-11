using System.IO;
using DIR.Lib;

namespace Console.Lib.Tests;

/// <summary>
/// An <see cref="ITerminalViewport"/> that writes into a <see cref="CellBuffer"/>, which is how a paint test
/// observes what a cell ENDED UP as (glyph plus resolved pen) instead of scraping escape bytes.
/// <para>
/// Shared because four test files each carry a private copy of exactly this
/// (<c>CellLayoutPenTests</c>, <c>CellLayoutLinkTests</c>, <c>CellLayoutTrimTests</c>,
/// <c>TreeViewScrollBarTests</c>) and a fifth would have been the wrong answer. Those four predate this and
/// should migrate onto it; the only variation between them is the colour mode, which is a parameter here.
/// </para>
/// </summary>
internal sealed class CellBufferViewport(
    CellBuffer buffer, int width, int height, ColorMode mode = ColorMode.Sgr16) : ITerminalViewport
{
    public (int Column, int Row) Offset => (0, 0);

    public (int Width, int Height) Size => (width, height);

    public TermCell CellSize => new(10, 20);

    public ColorMode ColorMode => mode;

    public void SetCursorPosition(int left, int top) => buffer.MoveTo(left, top);

    public void Write(string text) => buffer.Write(text);

    public void WriteLine(string? text = null)
    {
    }

    public void Flush()
    {
    }

    public Stream OutputStream => Stream.Null;
}
