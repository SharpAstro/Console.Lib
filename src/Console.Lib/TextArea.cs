using System.Text;
using DIR.Lib;

namespace Console.Lib;

/// <summary>
/// Multi-line text editor widget. Renders a <see cref="TextAreaState"/> into a
/// terminal viewport with a reverse-video block cursor and follow-the-cursor
/// scrolling. Optional left-side line-number gutter (vim-style <c>~</c> markers
/// past the end of buffer).
/// <para>
/// Keystrokes split between two methods so callers can control which kinds of
/// keys reach the editor: <see cref="HandleKey"/> handles navigation/edit keys
/// (arrows, Home/End, Backspace/Delete, Enter, Tab); <see cref="HandleChar"/>
/// inserts the codepoint carried by <see cref="ConsoleInputEvent.KeyChar"/>
/// (set by <see cref="VirtualTerminal"/> from the UTF-8 input byte stream),
/// preserving non-ASCII input (é, 中, 🙂, …) that the
/// <see cref="ConsoleKey"/> + <see cref="ConsoleModifiers"/> pair cannot
/// round-trip through a US layout.
/// </para>
/// </summary>
public sealed class TextArea(ITerminalViewport viewport) : Widget(viewport)
{
    private VtStyle _style = new(SgrColor.White, SgrColor.Black);
    private VtStyle _gutterStyle = new(SgrColor.BrightBlack, SgrColor.Black);
    private bool _showGutter = true;
    private int _scrollLine;

    /// <summary>The text-area state (cursor + buffer) this widget renders. Null until the caller assigns one.</summary>
    public TextAreaState? State { get; set; }

    /// <summary>Sets the body style (text fg/bg).</summary>
    public TextArea Style(VtStyle style) { _style = style; return this; }

    /// <summary>Sets the gutter style (line numbers + tilde markers).</summary>
    public TextArea GutterStyle(VtStyle style) { _gutterStyle = style; return this; }

    /// <summary>Show or hide the line-number gutter. Default: visible.</summary>
    public TextArea ShowGutter(bool show) { _showGutter = show; return this; }

    /// <summary>Number of rows currently visible in the viewport.</summary>
    public int VisibleRows => Viewport.Size.Height;

    /// <summary>Topmost visible line index.</summary>
    public int ScrollLine => _scrollLine;

    /// <inheritdoc/>
    public override void Render()
    {
        if (State is null) return;
        var (width, height) = Viewport.Size;
        if (width <= 0 || height <= 0) return;

        var lineCount = State.LineCount;
        var (cline, ccol) = State.CursorLineColumn;

        // Follow-the-cursor scroll.
        if (cline < _scrollLine) _scrollLine = cline;
        else if (cline >= _scrollLine + height) _scrollLine = cline - height + 1;
        if (_scrollLine > Math.Max(0, lineCount - height)) _scrollLine = Math.Max(0, lineCount - height);
        if (_scrollLine < 0) _scrollLine = 0;

        var colorMode = Viewport.ColorMode;
        var fillStyle = _style.Apply(colorMode);
        var gutterStyle = _gutterStyle.Apply(colorMode);

        // Gutter width: log10(lineCount) digits + a trailing space. 4 chars min for
        // readability ("  1 ", "  2 ", … "999 "). Fits up to 9999 lines comfortably.
        var gutterWidth = _showGutter ? Math.Max(4, lineCount.ToString().Length + 1) : 0;
        var contentWidth = Math.Max(0, width - gutterWidth);

        for (var row = 0; row < height; row++)
        {
            if (!TrySetCursorPosition(Viewport, 0, row)) return;

            var lineIdx = _scrollLine + row;
            if (lineIdx < lineCount)
            {
                var sb = new StringBuilder(width + 32);
                if (_showGutter)
                {
                    sb.Append(gutterStyle);
                    var s = (lineIdx + 1).ToString();
                    sb.Append(' ', gutterWidth - 1 - s.Length);
                    sb.Append(s);
                    sb.Append(' ');
                    sb.Append(VtStyle.Reset);
                }
                sb.Append(fillStyle);
                AppendLine(sb, lineIdx, cline, ccol, contentWidth, colorMode);
                sb.Append(VtStyle.Reset);
                Viewport.Write(sb.ToString());
            }
            else
            {
                // Past end-of-buffer: draw a tilde + blanks (vim-style empty marker).
                var sb = new StringBuilder(width + 16);
                if (_showGutter)
                {
                    sb.Append(gutterStyle).Append('~').Append(' ', gutterWidth - 1).Append(VtStyle.Reset);
                }
                sb.Append(fillStyle).Append(' ', contentWidth).Append(VtStyle.Reset);
                Viewport.Write(sb.ToString());
            }
        }
    }

    private void AppendLine(StringBuilder sb, int line, int cursorLine, int cursorCol, int contentWidth, ColorMode colorMode)
    {
        if (contentWidth <= 0) return;
        var lineText = State!.GetLine(line);

        // Cursor in *bytes* — convert to char offset for slicing the decoded
        // string. ASCII (the common case) makes this a no-op.
        var cursorCharOffset = -1;
        if (line == cursorLine)
        {
            cursorCharOffset = ByteOffsetToCharOffset(lineText, cursorCol);
        }

        // Emit the part before the cursor, then the cursor cell (reverse-video
        // single char), then the part after — all clipped to contentWidth.
        // For lines without the cursor, emit everything as plain text. Padding
        // fills the rest of the row with the line's background style so the
        // cursor row visually matches the rest.
        var written = 0;
        if (cursorCharOffset >= 0 && cursorCharOffset <= lineText.Length)
        {
            written += AppendClipped(sb, lineText.AsSpan(0, cursorCharOffset), contentWidth - written);
            if (written < contentWidth)
            {
                var cursorChar = cursorCharOffset < lineText.Length ? lineText[cursorCharOffset].ToString() : " ";
                sb.Append(VtStyle.ReverseOn).Append(cursorChar).Append(VtStyle.ReverseOff);
                written++;
                if (cursorCharOffset < lineText.Length)
                {
                    written += AppendClipped(sb, lineText.AsSpan(cursorCharOffset + 1), contentWidth - written);
                }
            }
        }
        else
        {
            written += AppendClipped(sb, lineText.AsSpan(), contentWidth);
        }
        if (written < contentWidth) sb.Append(' ', contentWidth - written);
        // colorMode currently unused — reserved for future inline-style emission.
        _ = colorMode;
    }

    private static int AppendClipped(StringBuilder sb, ReadOnlySpan<char> s, int budget)
    {
        if (budget <= 0) return 0;
        if (s.Length <= budget)
        {
            sb.Append(s);
            return s.Length;
        }
        sb.Append(s[..budget]);
        return budget;
    }

    /// <summary>
    /// Map a byte offset within a line to its corresponding char offset in the
    /// decoded UTF-16 string. Lines are short, so a linear scan is fine.
    /// </summary>
    private static int ByteOffsetToCharOffset(string lineText, int byteOffset)
    {
        if (byteOffset <= 0) return 0;
        var bytes = 0;
        for (var i = 0; i < lineText.Length; i++)
        {
            if (bytes >= byteOffset) return i;
            var c = lineText[i];
            // Surrogate pairs encode a non-BMP codepoint; UTF-8 length is 4.
            if (char.IsHighSurrogate(c) && i + 1 < lineText.Length && char.IsLowSurrogate(lineText[i + 1]))
            {
                bytes += 4;
                i++;
            }
            else if (c < 0x80) bytes += 1;
            else if (c < 0x800) bytes += 2;
            else bytes += 3;
        }
        return lineText.Length;
    }

    /// <summary>
    /// Routes a navigation or edit key to the underlying state. Returns <c>true</c>
    /// when the state changed.
    /// </summary>
    public bool HandleKey(ConsoleKey key, ConsoleModifiers mods)
    {
        if (State is null) return false;
        var ctrl = (mods & ConsoleModifiers.Control) != 0;
        var pageRows = Math.Max(1, VisibleRows - 1);
        return key switch
        {
            ConsoleKey.LeftArrow  => State.MoveLeft(),
            ConsoleKey.RightArrow => State.MoveRight(),
            ConsoleKey.UpArrow    => State.MoveUp(),
            ConsoleKey.DownArrow  => State.MoveDown(),
            ConsoleKey.Home       => ctrl ? State.MoveDocumentStart() : State.MoveLineStart(),
            ConsoleKey.End        => ctrl ? State.MoveDocumentEnd()   : State.MoveLineEnd(),
            ConsoleKey.PageUp     => State.MoveByLines(-pageRows),
            ConsoleKey.PageDown   => State.MoveByLines( pageRows),
            ConsoleKey.Backspace  => State.Backspace(),
            ConsoleKey.Delete     => State.DeleteForward(),
            ConsoleKey.Enter      => State.InsertChar('\n'),
            ConsoleKey.Tab        => State.InsertText("    "),  // soft-tab; smart-tab is a later concern
            _ => false,
        };
    }

    /// <summary>
    /// Inserts the printable codepoint carried by
    /// <see cref="ConsoleInputEvent.KeyChar"/> at the cursor. Returns <c>true</c>
    /// if a character was inserted. <see cref="VirtualTerminal"/> populates
    /// <c>KeyChar</c> from the UTF-8 byte stream, so non-ASCII input (é, 中,
    /// emoji) round-trips correctly without depending on the US-layout
    /// <see cref="InputKeyCharMapping"/> path.
    /// </summary>
    public bool HandleChar(ConsoleInputEvent ev)
    {
        if (State is null) return false;
        // Ctrl/Alt held → it's a hotkey, not text.
        if ((ev.Modifiers & (ConsoleModifiers.Control | ConsoleModifiers.Alt)) != 0) return false;
        return ev.KeyChar is { } rune && State.InsertRune(rune);
    }
}
