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

    /// <summary>
    /// Translates to the parent's cells and forwards. It must do NOTHING else — in particular it must not
    /// flush. It used to call <c>parent.Flush()</c> per move, which on a buffered terminal means "emit the
    /// pending diff NOW", i.e. mid-paint: a painter that fills its background and then draws its text (every
    /// <see cref="CellLayout"/> frame) had the half-painted state shipped at each cursor move — blanks
    /// emitted over the old text, then the labels flushed back one by one. On screen that is erase-then-
    /// redraw at exactly the repaint cadence: the once-per-second top-bar flicker that survived every fix
    /// aimed at the emissions themselves, because the emissions were correct and their TIMING was not.
    /// A caller that genuinely needs bytes out before raw output has <see cref="BeginRawOutput"/>, whose
    /// contract says so explicitly.
    /// </summary>
    public void SetCursorPosition(int left, int top)
        => parent.SetCursorPosition(
            _columnOffset + Math.Clamp(left, 0, _width - 1),
            _rowOffset + Math.Clamp(top, 0, _height - 1));

    public void BeginRawOutput(int column, int row)
        => parent.BeginRawOutput(
            _columnOffset + Math.Clamp(column, 0, Math.Max(0, _width - 1)),
            _rowOffset + Math.Clamp(row, 0, Math.Max(0, _height - 1)));

    public void MarkRawRegion(int column, int row, int width, int height)
        => parent.MarkRawRegion(_columnOffset + column, _rowOffset + row,
            Math.Min(width, _width), Math.Min(height, _height));

    public void SetCaret(int column, int row, CaretStyle style)
        => parent.SetCaret(
            _columnOffset + Math.Clamp(column, 0, Math.Max(0, _width - 1)),
            _rowOffset + Math.Clamp(row, 0, Math.Max(0, _height - 1)), style);

    public void HideCaret() => parent.HideCaret();

    public void Write(string text) => parent.Write(text);

    public void WriteLine(string? text = null) => parent.WriteLine(text);

    public TermCell CellSize => parent.CellSize;

    public void Flush() => parent.Flush();

    public Stream OutputStream => parent.OutputStream;

    public ColorMode ColorMode => parent.ColorMode;
}
