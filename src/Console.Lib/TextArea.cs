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
/// <para>
/// <b>Three coordinate systems live here</b>, and click-mapping has to bridge
/// them:
/// <list type="bullet">
/// <item><term>bytes</term> — what <see cref="TextAreaState"/> stores. UTF-8.
///   <c>"café"</c> is 5 bytes. The cursor position
///   (<see cref="TextAreaState.CursorPos"/>) and column
///   (<see cref="TextAreaState.CursorLineColumn"/>) are byte offsets.</item>
/// <item><term>UTF-16 chars</term> — what <see cref="string"/> exposes after
///   <see cref="TextAreaState.GetLine"/> decodes a line. <c>"café"</c> is 4
///   chars; <c>"🙂"</c> is 2 (a surrogate pair). Used internally by the
///   render-side cursor split.</item>
/// <item><term>cells</term> — what the terminal actually draws. One cell per
///   ASCII char, one cell per BMP codepoint, <em>one</em> cell per non-BMP
///   surrogate pair (so <c>"🙂"</c> renders as a single cell here even though
///   it's 2 chars and 4 bytes), <see cref="TabWidth"/> cells per tab.</item>
/// </list>
/// </para>
/// <para>
/// <b>Known limitation: East Asian Width.</b> True wide-glyph codepoints
/// (CJK Han, fullwidth Latin, most emoji-presentation scripts) render as
/// <em>two</em> cells in xterm-family terminals but this widget counts them
/// as one. That means a click on the second cell of 中 lands one byte past
/// where the user pointed, and the on-screen cursor block is half-width over
/// such glyphs. Fixing this needs an East-Asian-Width / emoji-width
/// classification table; tracked but not yet implemented. ASCII-only and
/// Latin-Extended content (the realistic input for the lalr-tui consumer)
/// is unaffected.
/// </para>
/// <para>
/// <b>Tab handling.</b> <see cref="HandleKey"/> on
/// <see cref="ConsoleKey.Tab"/> inserts four spaces (a soft tab) — content
/// authored inside the widget never contains <c>\t</c>. Loaded files that
/// <em>do</em> contain real tab bytes are mapped at <see cref="TabWidth"/>
/// (4) cells in click-positioning, which matches the soft-tab convention
/// but <em>not</em> xterm's default 8-column hard tab stop. So clicking past
/// a hard tab in a loaded file may land a few bytes off in the unrelated
/// case where the source uses 8-column tabs. The realistic content of the
/// lalr-tui consumer (YAML grammars, source input) doesn't hit this; the
/// approximation is documented here so a future content-loader knows to
/// normalise tabs on load if exact alignment matters.
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
    /// <para>
    /// This is the byte→char half of the click-mapping pair (see the class doc
    /// for the full bytes/chars/cells story); the cell→byte direction lives in
    /// <see cref="CellOffsetToByteOffset"/>. Used by the cursor-row render to
    /// figure out where to slice the decoded line for the reverse-video cursor
    /// cell. Surrogate pairs encode a single non-BMP codepoint as 4 UTF-8
    /// bytes and 2 UTF-16 chars; BMP chars map 1:1 from char to (1, 2, 3) UTF-8
    /// bytes by codepoint range. Wide-glyph (East-Asian-Width) is not handled
    /// — see the class doc.
    /// </para>
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
            ConsoleKey.LeftArrow  => ctrl ? State.MoveWordLeft()  : State.MoveLeft(),
            ConsoleKey.RightArrow => ctrl ? State.MoveWordRight() : State.MoveRight(),
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
    /// Routes a mouse event into the editor. A primary-button press inside
    /// the body (i.e. past the gutter) moves the cursor to the clicked cell.
    /// Mouse releases / drags / wheel events are ignored — selection support
    /// is a follow-up. Returns <c>true</c> when the cursor moved.
    /// <para>
    /// Heavily defensive: every coordinate is clamped, every conversion is
    /// bounded, and the whole body is wrapped in a try/catch so a malformed
    /// state (e.g. a stale TextAreaState, a zero-size viewport, an
    /// out-of-range scroll offset after a resize) yields a no-op rather than
    /// propagating an exception out of the input loop.
    /// </para>
    /// </summary>
    public bool HandleMouse(MouseEvent m)
    {
        if (State is null) return false;
        // Press only — drag selection, double-click word-select, etc. are
        // future work; for now click-to-position is the whole contract.
        if (m.IsRelease || m.IsMotion) return false;
        if (m.Button != 0) return false;
        if (HitTest(m.X, m.Y) is not (var col, var row)) return false;

        try
        {
            var lineCount = Math.Max(1, State.LineCount);   // EnsureIndex guarantees >=1, but defend anyway
            // _scrollLine could in principle be stale after a resize that
            // shrinks the viewport; clamp to a valid line index up front so
            // we never feed a negative or out-of-range value to MoveTo.
            var line = Math.Clamp(_scrollLine + Math.Max(0, row), 0, lineCount - 1);

            // Same gutter-width formula as Render() — keep these two in sync.
            // Click-in-gutter lands at byte 0 of the line (so users can jump
            // to a line's start by clicking the line-number marker).
            var gutterWidth = _showGutter ? Math.Max(4, lineCount.ToString().Length + 1) : 0;
            var contentCol = Math.Max(0, col - gutterWidth);

            // Cell column → byte column. Render() lays out one cell per
            // UTF-16 char (with tab counted as TabWidth cells to match the
            // soft-tab insert convention) so we mirror that accounting here.
            // Multi-byte UTF-8 codepoints (é, 中, …) consume 2-3 bytes each
            // but render in 1 cell, so we walk the decoded line.
            var lineText = State.GetLine(line) ?? "";
            var byteCol = CellOffsetToByteOffset(lineText, contentCol);
            return State.MoveTo(line, byteCol);
        }
        catch (Exception)
        {
            // Defense in depth — any unexpected state mismatch (state mutated
            // mid-render, viewport size lying about its width, etc.) becomes
            // a no-op click instead of bubbling up. The cursor stays put.
            return false;
        }
    }

    /// <summary>
    /// Visual width assumed for a tab character. Matches the 4-space soft-tab
    /// inserted by <see cref="HandleKey"/> so click-positioning over a line
    /// that was edited inside the widget always lines up. Loaded files that
    /// contain real tab bytes get the same 4-cell approximation — close enough
    /// for editor click-mapping and consistent with what most code editors
    /// default to before the user changes a setting.
    /// </summary>
    private const int TabWidth = 4;

    /// <summary>
    /// Cell-column → UTF-8 byte offset. Used by <see cref="HandleMouse"/> to
    /// turn a click at terminal-cell <c>(col)</c> into the byte position
    /// <see cref="TextAreaState.MoveTo"/> consumes. Per-codepoint accounting:
    /// <list type="bullet">
    /// <item>tab — <see cref="TabWidth"/> cells, 1 byte (4-cell soft-tab
    ///   approximation; see class doc on tab handling)</item>
    /// <item>ASCII (&lt;0x80) — 1 cell, 1 byte</item>
    /// <item>BMP non-ASCII (&lt;0x800) — 1 cell, 2 bytes (e.g. é, Cyrillic)</item>
    /// <item>BMP non-ASCII (≥0x800) — 1 cell, 3 bytes (e.g. 中, 日, most CJK
    ///   — but see <b>known limitation</b> below: real-world CJK glyphs
    ///   actually render as 2 cells, which we don't yet account for)</item>
    /// <item>surrogate pair (non-BMP, e.g. 🙂) — 1 cell, 4 bytes, advances
    ///   the source-string index by 2</item>
    /// </list>
    /// When the click lands past end-of-line the function returns the line's
    /// full byte length and <see cref="TextAreaState.MoveTo"/> clamps to the
    /// line end — so a click on a tilde row (past EOF) lands at end-of-buffer
    /// rather than throwing.
    /// <para>
    /// <b>Known limitation</b>: East Asian Width / emoji-presentation
    /// codepoints render as two terminal cells but are counted as one here;
    /// see the class doc for the impact and the fix path.
    /// </para>
    /// </summary>
    private static int CellOffsetToByteOffset(string lineText, int cellOffset)
    {
        if (cellOffset <= 0 || lineText.Length == 0) return 0;
        var bytes = 0;
        var cells = 0;
        for (var i = 0; i < lineText.Length; i++)
        {
            if (cells >= cellOffset) return bytes;
            var c = lineText[i];
            int byteAdv;
            int cellAdv;
            if (c == '\t')
            {
                byteAdv = 1;
                cellAdv = TabWidth;
            }
            else if (char.IsHighSurrogate(c) && i + 1 < lineText.Length && char.IsLowSurrogate(lineText[i + 1]))
            {
                byteAdv = 4;
                cellAdv = 1;
                i++;
            }
            else if (c < 0x80) { byteAdv = 1; cellAdv = 1; }
            else if (c < 0x800) { byteAdv = 2; cellAdv = 1; }
            else { byteAdv = 3; cellAdv = 1; }
            bytes += byteAdv;
            cells += cellAdv;
        }
        return bytes;
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
