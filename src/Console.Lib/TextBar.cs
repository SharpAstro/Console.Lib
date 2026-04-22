namespace Console.Lib;

/// <summary>
/// Single-line widget with left-aligned text and optional right-aligned text.
/// </summary>
public class TextBar(ITerminalViewport viewport) : Widget(viewport)
{
    private string _text = "";
    private string _rightText = "";
    private VtStyle _style = new(SgrColor.BrightWhite, SgrColor.BrightBlack);

    public TextBar Text(string text) { _text = text; return this; }
    public TextBar RightText(string text) { _rightText = text; return this; }
    public TextBar Style(VtStyle style) { _style = style; return this; }

    public override void Render()
    {
        var width = Viewport.Size.Width;
        if (width <= 0) return;

        if (!TrySetCursorPosition(Viewport, 0, 0)) return;

        // Right text wins priority; ellipsize it if it alone exceeds the row.
        var right = _rightText.Length <= width
            ? _rightText
            : width > 1 ? _rightText[..(width - 1)] + '\u2026' : _rightText[..width];

        var padWidth = Math.Max(0, width - right.Length);

        // Truncate left with ellipsis when it would collide with right.
        var left = _text.Length <= padWidth
            ? _text
            : padWidth > 1 ? _text[..(padWidth - 1)] + '\u2026' : "";

        Viewport.Write($"{_style.Apply(Viewport.ColorMode)}{left.PadRight(padWidth)}{right}{VtStyle.Reset}");
    }
}
