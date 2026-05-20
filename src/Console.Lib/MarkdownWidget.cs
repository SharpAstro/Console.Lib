namespace Console.Lib;

/// <summary>
/// Widget that renders Markdown content to a terminal viewport with VT styling.
/// Uses <see cref="MarkdownRenderer"/> (and, transitively, the LALR.CC inline +
/// block grammars in <c>DIR.Lib.Markdown</c>) for the actual Markdown-to-VT
/// conversion.
/// <para>
/// The fully wrapped VT output lines are cached and reused across renders. They
/// are re-computed automatically when the viewport width changes (e.g. on
/// terminal resize), so word wrapping and table layout adapt to the new size.
/// </para>
/// </summary>
public class MarkdownWidget(ITerminalViewport viewport) : Widget(viewport)
{
    private string _markdown = "";
    private List<string>? _renderedLines;
    private int _renderedWidth;
    private int _scrollOffset;
    private BoxRenderMode? _mathMode;
    private string? _mathFontPath;

    /// <summary>
    /// The color theme used for rendering. Defaults to <see cref="MarkdownTheme.Default"/>.
    /// </summary>
    public MarkdownTheme Theme { get; set; } = MarkdownTheme.Default;

    /// <summary>
    /// Pixel-render mode for display-math blocks (<c>$$…$$</c> / <c>\[…\]</c>).
    /// <c>null</c> (default) keeps display math on the single-row Unicode path,
    /// matching the pre-existing behaviour. Sextant and HalfBlock produce one
    /// text row per encoded row and compose cleanly with the row-by-row writer
    /// below; Sixel emits a DCS payload that extends downward across multiple
    /// cell rows, so callers using Sixel should size the widget tall enough
    /// that any lines following the math block sit below the image (the writer
    /// will otherwise overwrite the image's tail rows when it positions back
    /// to row+1). Setting this invalidates the line cache so the next render
    /// re-walks the renderer with the new mode.
    /// </summary>
    public BoxRenderMode? MathMode
    {
        get => _mathMode;
        set { if (_mathMode != value) { _mathMode = value; _renderedLines = null; } }
    }

    /// <summary>
    /// Optional path to an OpenType math font used by the pixel-render path.
    /// When <c>null</c> the renderer falls back to its built-in system-font
    /// search. Has no effect when <see cref="MathMode"/> is <c>null</c>.
    /// </summary>
    public string? MathFontPath
    {
        get => _mathFontPath;
        set { if (_mathFontPath != value) { _mathFontPath = value; _renderedLines = null; } }
    }

    /// <summary>
    /// Sets the Markdown content to render.
    /// VT output is deferred until <see cref="Render"/>.
    /// </summary>
    public MarkdownWidget Markdown(string markdown)
    {
        _markdown = markdown;
        _renderedLines = null;
        return this;
    }

    /// <summary>Scrolls to the given line offset (clamped to zero).</summary>
    public MarkdownWidget ScrollTo(int offset)
    {
        _scrollOffset = Math.Max(0, offset);
        return this;
    }

    /// <summary>Total number of rendered output lines at the current viewport width.</summary>
    public int TotalLines => EnsureRendered().Count;

    /// <summary>Number of lines visible in the viewport.</summary>
    public int VisibleRows => Viewport.Size.Height;

    /// <summary>Current scroll offset.</summary>
    public int ScrollOffset => _scrollOffset;

    /// <inheritdoc/>
    public override void Render()
    {
        var lines = EnsureRendered();
        var (width, height) = Viewport.Size;
        if (width <= 0 || height <= 0) return;

        for (var row = 0; row < height; row++)
        {
            if (!TrySetCursorPosition(Viewport, 0, row))
            {
                return;
            }

            var lineIdx = _scrollOffset + row;
            if (lineIdx >= 0 && lineIdx < lines.Count)
            {
                var line = lines[lineIdx];
                var visLen = MarkdownRenderer.VisibleLength(line);
                Viewport.Write(visLen >= width ? line : $"{line}{new string(' ', width - visLen)}");
            }
            else
            {
                Viewport.Write(new string(' ', width));
            }
        }
    }

    /// <summary>
    /// Returns the cached VT output lines, re-rendering from the AST if the
    /// viewport width has changed since the last render.
    /// </summary>
    private List<string> EnsureRendered()
    {
        var currentWidth = Viewport.Size.Width;
        if (_renderedLines is not null && _renderedWidth == currentWidth)
            return _renderedLines;

        _renderedLines = MarkdownRenderer.RenderLines(
            _markdown, currentWidth, Viewport.ColorMode, Theme,
            mathMode: _mathMode, mathFontPath: _mathFontPath);
        _renderedWidth = currentWidth;
        return _renderedLines;
    }
}
