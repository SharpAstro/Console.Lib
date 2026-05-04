using System.Text;

namespace Console.Lib;

/// <summary>
/// Editable multi-line text state on top of a UTF-8 <see cref="GapBuffer"/>.
/// Cursor and column positions are <em>byte</em> offsets into the buffer;
/// cursor moves in <see cref="MoveLeft"/> / <see cref="MoveRight"/> step over
/// a whole UTF-8 codepoint, never landing mid-sequence.
/// <para>
/// All mutating methods return <c>true</c> when something actually changed —
/// callers (typically a render loop) use that signal to decide whether to
/// repaint and propagate the edit downstream (re-tokenize, re-validate, etc.).
/// </para>
/// <para>
/// <b>Why bytes everywhere</b>: the buffer is UTF-8 by design (lingua franca
/// for terminal I/O and source code), and exposing byte-based columns means
/// pipe consumers — lexers, validators — can walk the gap buffer directly
/// without re-encoding. The trade-off is that one byte of column ≠ one
/// codepoint ≠ one terminal cell:
/// <list type="bullet">
/// <item>ASCII (&lt;0x80): 1 byte, 1 codepoint, 1 cell — the trivial case</item>
/// <item>2-byte UTF-8 (e.g. é, ñ, Cyrillic): 2 bytes, 1 codepoint, 1 cell</item>
/// <item>3-byte UTF-8 (e.g. 中, 日 — CJK Han): 3 bytes, 1 codepoint, but
///   <em>2</em> cells in xterm — the renderer in <see cref="TextArea"/>
///   does not yet account for that, see its class doc</item>
/// <item>4-byte UTF-8 (non-BMP, e.g. 🙂): 4 bytes, 1 codepoint, 1 cell in
///   most terminals (some emoji presentations render 2; treated as 1 here)</item>
/// </list>
/// All <see cref="MoveLeft"/>/<see cref="MoveRight"/>/<see cref="MoveWordLeft"/>/
/// <see cref="MoveWordRight"/> step in codepoints (snap to UTF-8 boundary);
/// <see cref="MoveTo"/> takes a byte column from the click-mapping layer and
/// snaps backward to the nearest codepoint boundary so the cursor never lands
/// mid-sequence.
/// </para>
/// </summary>
public sealed class TextAreaState
{
    private readonly GapBuffer _buf;
    private int[] _lineStarts;      // byte offsets where each line begins; lineStarts[0] == 0 always
    private int _lineCount;
    private bool _indexValid;
    private int _cursorPos;          // byte offset into the buffer
    private int _desiredColumn;      // sticky column (bytes) for vertical motion; -1 means "use current column"

    /// <summary>Creates an empty state, optionally pre-populated with the supplied initial text.</summary>
    public TextAreaState(string initial = "")
    {
        _buf = new GapBuffer(initial);
        _lineStarts = new int[16];
        _indexValid = false;
        _cursorPos = 0;
        _desiredColumn = -1;
    }

    /// <summary>Current cursor position, expressed as a byte offset into the buffer.</summary>
    public int CursorPos => _cursorPos;

    /// <summary>Total length of the buffer in bytes.</summary>
    public int Length => _buf.Length;

    /// <summary>Number of lines in the buffer (always &gt;= 1; an empty buffer counts as one empty line).</summary>
    public int LineCount { get { EnsureIndex(); return _lineCount; } }

    /// <summary>(Line, byte-column) for the current cursor.</summary>
    public (int Line, int Column) CursorLineColumn
    {
        get
        {
            EnsureIndex();
            var line = LineForPos(_cursorPos);
            return (line, _cursorPos - _lineStarts[line]);
        }
    }

    /// <summary>Materialises the entire buffer to a UTF-8 decoded string.</summary>
    public string GetText() => _buf.GetText();

    /// <summary>Returns line <paramref name="line"/> as a string (decoded UTF-8, no trailing newline).</summary>
    public string GetLine(int line)
    {
        EnsureIndex();
        if ((uint)line >= (uint)_lineCount) return "";
        var start = _lineStarts[line];
        var end = line + 1 < _lineCount ? _lineStarts[line + 1] - 1 /* drop trailing \n */ : _buf.Length;
        if (end < start) end = start;
        var len = end - start;
        if (len == 0) return "";
        Span<byte> dest = len <= 1024 ? stackalloc byte[len] : new byte[len];
        _buf.CopyTo(start, dest, len);
        return Encoding.UTF8.GetString(dest);
    }

    /// <summary>Byte length of line <paramref name="line"/> (excluding the trailing newline).</summary>
    public int GetLineLength(int line)
    {
        EnsureIndex();
        if ((uint)line >= (uint)_lineCount) return 0;
        var start = _lineStarts[line];
        var end = line + 1 < _lineCount ? _lineStarts[line + 1] - 1 : _buf.Length;
        return Math.Max(0, end - start);
    }

    /// <summary>Inserts a single BMP character at the cursor and advances the cursor past it.</summary>
    public bool InsertChar(char c)
    {
        // BMP fast path; non-BMP requires surrogate pairs which most key mappers never emit.
        Span<byte> tmp = stackalloc byte[4];
        if (!System.Text.Rune.TryCreate(c, out var rune)) return false;
        var n = rune.EncodeToUtf8(tmp);
        _buf.Insert(_cursorPos, tmp[..n]);
        _cursorPos += n;
        _indexValid = false;
        _desiredColumn = -1;
        return true;
    }

    /// <summary>Inserts a single Unicode codepoint at the cursor and advances the cursor past it.
    /// Handles non-BMP codepoints (4-byte UTF-8 sequences) that <see cref="InsertChar"/> cannot.</summary>
    public bool InsertRune(System.Text.Rune rune)
    {
        Span<byte> tmp = stackalloc byte[4];
        var n = rune.EncodeToUtf8(tmp);
        _buf.Insert(_cursorPos, tmp[..n]);
        _cursorPos += n;
        _indexValid = false;
        _desiredColumn = -1;
        return true;
    }

    /// <summary>Inserts a span of UTF-16 code units (surrogates handled by the underlying encoder).</summary>
    public bool InsertText(ReadOnlySpan<char> s)
    {
        if (s.IsEmpty) return false;
        var byteCount = Encoding.UTF8.GetByteCount(s);
        Span<byte> dest = byteCount <= 256 ? stackalloc byte[byteCount] : new byte[byteCount];
        Encoding.UTF8.GetBytes(s, dest);
        _buf.Insert(_cursorPos, dest);
        _cursorPos += byteCount;
        _indexValid = false;
        _desiredColumn = -1;
        return true;
    }

    /// <summary>Deletes the codepoint immediately before the cursor.</summary>
    public bool Backspace()
    {
        if (_cursorPos == 0) return false;
        var newPos = PrevCodepointStart(_cursorPos);
        _buf.DeleteRange(newPos, _cursorPos - newPos);
        _cursorPos = newPos;
        _indexValid = false;
        _desiredColumn = -1;
        return true;
    }

    /// <summary>Deletes the codepoint at the cursor.</summary>
    public bool DeleteForward()
    {
        if (_cursorPos >= _buf.Length) return false;
        var next = NextCodepointStart(_cursorPos);
        _buf.DeleteRange(_cursorPos, next - _cursorPos);
        _indexValid = false;
        _desiredColumn = -1;
        return true;
    }

    /// <summary>Moves the cursor one codepoint to the left.</summary>
    public bool MoveLeft()
    {
        if (_cursorPos == 0) return false;
        _cursorPos = PrevCodepointStart(_cursorPos);
        _desiredColumn = -1;
        return true;
    }

    /// <summary>Moves the cursor one codepoint to the right.</summary>
    public bool MoveRight()
    {
        if (_cursorPos >= _buf.Length) return false;
        _cursorPos = NextCodepointStart(_cursorPos);
        _desiredColumn = -1;
        return true;
    }

    /// <summary>Moves the cursor up one line, preserving the sticky column.</summary>
    public bool MoveUp() => MoveByLines(-1);

    /// <summary>Moves the cursor down one line, preserving the sticky column.</summary>
    public bool MoveDown() => MoveByLines(+1);

    /// <summary>Moves the cursor by <paramref name="delta"/> lines, preserving the sticky column.</summary>
    public bool MoveByLines(int delta)
    {
        if (delta == 0) return false;
        EnsureIndex();
        var line = LineForPos(_cursorPos);
        var target = Math.Clamp(line + delta, 0, _lineCount - 1);
        if (target == line) return false;
        // Sticky column: snapshot the column the first time we move vertically,
        // then preserve it across multiple ups/downs even if some intermediate
        // lines are shorter than the desired column.
        var col = _desiredColumn >= 0 ? _desiredColumn : _cursorPos - _lineStarts[line];
        _desiredColumn = col;
        var lineStart = _lineStarts[target];
        var lineEnd = target + 1 < _lineCount ? _lineStarts[target + 1] - 1 : _buf.Length;
        // Land at min(lineStart+col, lineEnd) but snap onto a codepoint boundary
        // — sticky column counts bytes, so a partial-codepoint landing is possible
        // when crossing into a multibyte line.
        var raw = Math.Min(lineStart + col, lineEnd);
        _cursorPos = SnapToCodepointBoundary(raw, lineStart);
        return true;
    }

    /// <summary>Moves the cursor to the start of the current line.</summary>
    public bool MoveLineStart()
    {
        EnsureIndex();
        var line = LineForPos(_cursorPos);
        if (_cursorPos == _lineStarts[line]) return false;
        _cursorPos = _lineStarts[line];
        _desiredColumn = -1;
        return true;
    }

    /// <summary>Moves the cursor to the end of the current line (before any trailing newline).</summary>
    public bool MoveLineEnd()
    {
        EnsureIndex();
        var line = LineForPos(_cursorPos);
        var end = line + 1 < _lineCount ? _lineStarts[line + 1] - 1 : _buf.Length;
        if (_cursorPos == end) return false;
        _cursorPos = end;
        _desiredColumn = -1;
        return true;
    }

    /// <summary>Moves the cursor to the very start of the buffer.</summary>
    public bool MoveDocumentStart()
    {
        if (_cursorPos == 0) return false;
        _cursorPos = 0;
        _desiredColumn = -1;
        return true;
    }

    /// <summary>Moves the cursor to the very end of the buffer.</summary>
    public bool MoveDocumentEnd()
    {
        if (_cursorPos == _buf.Length) return false;
        _cursorPos = _buf.Length;
        _desiredColumn = -1;
        return true;
    }

    /// <summary>
    /// Moves the cursor to the start of the previous word, where a word is a
    /// maximal run of letters / digits / underscores (Emacs <c>M-b</c>, also
    /// VS Code's <c>Ctrl+Left</c>). Two passes: first skip any non-word bytes
    /// behind the cursor, then skip the run of word bytes — so a click after
    /// trailing whitespace lands at the start of the preceding word, not in
    /// the gap between words.
    /// </summary>
    public bool MoveWordLeft()
    {
        if (_cursorPos == 0) return false;
        var p = _cursorPos;
        while (p > 0 && !IsWordByte(_buf[p - 1])) p--;
        while (p > 0 && IsWordByte(_buf[p - 1])) p--;
        if (p == _cursorPos) return false;
        _cursorPos = p;
        _desiredColumn = -1;
        return true;
    }

    /// <summary>
    /// Moves the cursor past the next word boundary (Emacs <c>M-f</c> / VS
    /// Code <c>Ctrl+Right</c>). Mirror of <see cref="MoveWordLeft"/>: skip
    /// non-word bytes, then skip the following word run.
    /// </summary>
    public bool MoveWordRight()
    {
        var len = _buf.Length;
        if (_cursorPos >= len) return false;
        var p = _cursorPos;
        while (p < len && !IsWordByte(_buf[p])) p++;
        while (p < len && IsWordByte(_buf[p])) p++;
        if (p == _cursorPos) return false;
        _cursorPos = p;
        _desiredColumn = -1;
        return true;
    }

    /// <summary>
    /// Sets the cursor to (<paramref name="line"/>, <paramref name="byteCol"/>),
    /// clamped to a valid line index and to the line's byte length, then
    /// snapped backward to a UTF-8 codepoint boundary so the cursor never
    /// lands mid-sequence. Used by <see cref="TextArea"/>'s mouse handler to
    /// turn a click into a cursor position.
    /// </summary>
    public bool MoveTo(int line, int byteCol)
    {
        EnsureIndex();
        // _lineCount is always >= 1 after EnsureIndex (an empty buffer still
        // counts as one empty line), but defend against a zero anyway —
        // Math.Clamp throws ArgumentException when min > max, and we'd rather
        // a click on a freshly-cleared buffer be a no-op than crash the input
        // loop. Same defensiveness applies to a negative byteCol.
        if (_lineCount <= 0) return false;
        line = Math.Clamp(line, 0, _lineCount - 1);
        var lineStart = _lineStarts[line];
        var lineEnd = line + 1 < _lineCount ? _lineStarts[line + 1] - 1 : _buf.Length;
        if (lineEnd < lineStart) lineEnd = lineStart;        // malformed index defence
        var raw = lineStart + Math.Clamp(byteCol, 0, lineEnd - lineStart);
        var newPos = SnapToCodepointBoundary(raw, lineStart);
        if (newPos == _cursorPos) return false;
        _cursorPos = newPos;
        _desiredColumn = -1;
        return true;
    }

    /// <summary>
    /// Word-character classifier for <see cref="MoveWordLeft"/> / <see cref="MoveWordRight"/>.
    /// ASCII bytes use the standard letter/digit/underscore rule; any non-ASCII
    /// byte (lead or continuation of a multi-byte UTF-8 codepoint) is treated
    /// as a word byte. That keeps non-Latin scripts (Cyrillic, Greek, CJK,
    /// emoji, …) inside word boundaries instead of fragmenting on every byte.
    /// </summary>
    private static bool IsWordByte(byte b)
    {
        if (b >= 0x80) return true;
        return b == (byte)'_'
            || (b >= (byte)'0' && b <= (byte)'9')
            || (b >= (byte)'A' && b <= (byte)'Z')
            || (b >= (byte)'a' && b <= (byte)'z');
    }

    /// <summary>Pre-gap byte segment (span — for sync consumers).</summary>
    public ReadOnlySpan<byte> SpanBeforeGap => _buf.SpanBeforeGap;

    /// <summary>Post-gap byte segment (span).</summary>
    public ReadOnlySpan<byte> SpanAfterGap => _buf.SpanAfterGap;

    /// <summary>Pre-gap byte segment as <see cref="ReadOnlyMemory{T}"/> (for async / Pipe consumers).</summary>
    public ReadOnlyMemory<byte> MemoryBeforeGap => _buf.MemoryBeforeGap;

    /// <summary>Post-gap byte segment as <see cref="ReadOnlyMemory{T}"/>.</summary>
    public ReadOnlyMemory<byte> MemoryAfterGap => _buf.MemoryAfterGap;

    // ---- internal helpers ----

    private int LineForPos(int pos)
    {
        EnsureIndex();
        // Binary search for the largest line start <= pos.
        int lo = 0, hi = _lineCount - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) >> 1;
            if (_lineStarts[mid] <= pos) lo = mid;
            else hi = mid - 1;
        }
        return lo;
    }

    private void EnsureIndex()
    {
        if (_indexValid) return;
        _lineCount = 0;
        EnsureCapacity(ref _lineStarts, 1);
        _lineStarts[_lineCount++] = 0;
        var len = _buf.Length;
        for (int i = 0; i < len; i++)
        {
            if (_buf[i] == (byte)'\n')
            {
                EnsureCapacity(ref _lineStarts, _lineCount + 1);
                _lineStarts[_lineCount++] = i + 1;
            }
        }
        _indexValid = true;
    }

    private static void EnsureCapacity(ref int[] array, int min)
    {
        if (array.Length >= min) return;
        Array.Resize(ref array, Math.Max(array.Length * 2, min));
    }

    /// <summary>Step forward over one full UTF-8 codepoint starting at <paramref name="pos"/>.</summary>
    private int NextCodepointStart(int pos)
    {
        // Lead byte tells us how many bytes the codepoint occupies; scan past
        // any continuation bytes defensively in case the buffer holds partial
        // sequences (it shouldn't, but cheaper to scan than to validate).
        var len = _buf.Length;
        if (pos >= len) return pos;
        var lead = _buf[pos];
        var step = lead < 0x80 ? 1 : lead < 0xC0 ? 1 : lead < 0xE0 ? 2 : lead < 0xF0 ? 3 : 4;
        var next = pos + step;
        if (next > len) next = len;
        // Snap forward past continuation bytes if the lead was malformed.
        while (next < len && IsContinuation(_buf[next])) next++;
        return next;
    }

    /// <summary>Step backward to the start of the previous UTF-8 codepoint.</summary>
    private int PrevCodepointStart(int pos)
    {
        if (pos <= 0) return 0;
        var p = pos - 1;
        // Continuation bytes have the top two bits set to 10. Walk backward until
        // we hit a non-continuation (lead) byte, capped at 4 steps for malformed input.
        for (var guard = 0; guard < 4 && p > 0 && IsContinuation(_buf[p]); guard++) p--;
        return p;
    }

    /// <summary>Snap <paramref name="pos"/> backward to the nearest codepoint boundary, not below <paramref name="floor"/>.</summary>
    private int SnapToCodepointBoundary(int pos, int floor)
    {
        if (pos >= _buf.Length) return pos;
        while (pos > floor && IsContinuation(_buf[pos])) pos--;
        return pos;
    }

    private static bool IsContinuation(byte b) => (b & 0xC0) == 0x80;
}
