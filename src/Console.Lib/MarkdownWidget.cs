using Markdig;
using Markdig.Syntax;

namespace Console.Lib;

/// <summary>
/// Widget that renders Markdown content to a terminal viewport with VT styling.
/// Uses <see cref="MarkdownRenderer"/> for the actual Markdown-to-VT conversion.
/// <para>
/// The parsed Markdig AST is cached and reused across renders. The VT output lines
/// are automatically re-rendered when the viewport width changes (e.g. on terminal resize),
/// so word wrapping and table layout adapt to the new size.
/// </para>
/// </summary>
public class MarkdownWidget(ITerminalViewport viewport) : Widget(viewport)
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .Build();

    private string _markdown = "";
    private MarkdownDocument? _document;
    private List<string>? _renderedLines;
    private int _renderedWidth;
    private int _scrollOffset;

    /// <summary>
    /// Sets the Markdown content to render.
    /// Parses the AST immediately; VT output is deferred until <see cref="Render"/>.
    /// </summary>
    public MarkdownWidget Markdown(string markdown)
    {
        _markdown = markdown;
        _document = Markdig.Markdown.Parse(markdown, Pipeline);
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
            if (!TrySetCursorPosition(Viewport, 0, row)) return;

            var lineIdx = _scrollOffset + row;
            if (lineIdx >= 0 && lineIdx < lines.Count)
                Viewport.Write(lines[lineIdx]);
            else
                Viewport.Write(new string(' ', width));
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

        _renderedLines = MarkdownRenderer.RenderLines(_markdown, currentWidth, Viewport.ColorMode);
        _renderedWidth = currentWidth;
        return _renderedLines;
    }
}
