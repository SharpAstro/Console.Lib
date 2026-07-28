namespace Console.Lib;

public sealed class TerminalViewport(ITerminalViewport parent, int columnOffset, int rowOffset, int width, int height) : ITerminalViewport
{
    private int _columnOffset = columnOffset, _rowOffset = rowOffset, _width = width, _height = height;

    public (int Column, int Row) Offset => (_columnOffset, _rowOffset);
    public (int Width, int Height) Size => (_width, _height);

    /// <summary>
    /// Re-points this viewport at a different cell rectangle.
    /// <para>
    /// Public because it is how a behaviour widget gets hosted inside a layout tree: a
    /// <see cref="DIR.Lib.Layout.Content.Fill"/> leaf arrives at the <c>drawFill</c> callback with its
    /// arranged rect, and the host re-points the widget's viewport at that rect before rendering it. So
    /// a <see cref="ScrollableList{T}"/>, <see cref="Canvas"/> or <see cref="MarkdownWidget"/> keeps the
    /// behaviour a layout node cannot model (scroll state, a sixel dirty region, its own wrapping) while
    /// its <i>placement</i> comes from the same arranged tree as everything around it.
    /// </para>
    /// <para>
    /// Re-pointing rather than reallocating is deliberate: the tree is rebuilt every frame, so allocating
    /// a viewport per Fill leaf per frame would churn, and the widget holds a reference to this instance.
    /// </para>
    /// </summary>
    public void UpdateGeometry(int columnOffset, int rowOffset, int width, int height)
    {
        _columnOffset = columnOffset;
        _rowOffset = rowOffset;
        _width = width;
        _height = height;
    }

    public void SetCursorPosition(int left, int top)
    {
        parent.SetCursorPosition(
            _columnOffset + Math.Clamp(left, 0, _width - 1),
            _rowOffset + Math.Clamp(top, 0, _height - 1));
        parent.Flush();
    }

    public void Write(string text) => parent.Write(text);

    public void WriteLine(string? text = null) => parent.WriteLine(text);

    public TermCell CellSize => parent.CellSize;

    public void Flush() => parent.Flush();

    public Stream OutputStream => parent.OutputStream;

    public ColorMode ColorMode => parent.ColorMode;
}
