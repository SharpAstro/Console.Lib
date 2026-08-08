namespace Console.Lib;

/// <summary>
/// Single-line widget with left-aligned text and optional right-aligned text. A caller editing text
/// inside that left string can park the terminal's real cursor in it with <see cref="Caret"/>.
/// </summary>
public class TextBar(ITerminalViewport viewport) : Widget(viewport)
{
    private string _text = "";
    private string _rightText = "";
    private VtStyle _style = new(SgrColor.BrightWhite, SgrColor.BrightBlack);
    private int? _caretColumn;
    private CaretStyle _caretStyle = CaretStyle.BlinkingBar;
    private bool _ownsCaret;

    public TextBar Text(string text) { _text = text; return this; }
    public TextBar RightText(string text) { _rightText = text; return this; }
    public TextBar Style(VtStyle style) { _style = style; return this; }

    /// <summary>
    /// Parks the terminal's REAL cursor at <paramref name="column"/> of the left text (see
    /// <see cref="ITerminalViewport.SetCaret"/>) — the caret for a bar whose text the caller composes and
    /// edits itself, which is why the column is passed in rather than derived from a state object as
    /// <see cref="TextInputBar"/> does. A null column withdraws it.
    /// <para>
    /// The bar owns the CLIPPING decision, because it owns the truncation: a column the ellipsis ate, or
    /// one past the room the right text left, withdraws the caret rather than parking it on a cell showing
    /// something else. Once called, this bar takes responsibility for the caret and reasserts or withdraws
    /// it on every <see cref="Render"/>; a bar that never calls it never touches the caret, so it cannot
    /// stomp one another widget owns.
    /// </para>
    /// </summary>
    public TextBar Caret(int? column, CaretStyle style)
    {
        _caretColumn = column;
        _caretStyle = style;
        _ownsCaret = true;
        return this;
    }

    public override void Render()
    {
        var width = Viewport.Size.Width;
        if (width <= 0)
        {
            WithdrawCaret();
            return;
        }

        if (!TrySetCursorPosition(Viewport, 0, 0))
        {
            WithdrawCaret();
            return;
        }

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

        if (!_ownsCaret) return;

        // Cells of the row a caret may legitimately sit on. When the left text fits, that is the whole
        // left region including its padding — an insertion point one past the last character is a real
        // position. When it did not fit, the last cell is the ellipsis, which stands for text the user
        // cannot see, so the caret must not sit on it (and padWidth <= 1 leaves no cell at all).
        var caretCells = _text.Length <= padWidth ? padWidth : Math.Max(0, padWidth - 1);

        if (_caretColumn is { } column && column >= 0 && column < caretCells)
        {
            Viewport.SetCaret(column, 0, _caretStyle);
        }
        else
        {
            Viewport.HideCaret();
        }
    }

    /// <summary>Withdraws the caret on a render that paints nothing — a bar with no room still owes the
    /// caret an answer, or last frame's would stay parked on a row this one never drew.</summary>
    private void WithdrawCaret()
    {
        if (_ownsCaret) Viewport.HideCaret();
    }
}
