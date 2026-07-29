using System;
using System.Text;
using DIR.Lib;

namespace Console.Lib;

/// <summary>What a cell is, which decides whether the diff may touch it.</summary>
public enum CellKind : byte
{
    /// <summary>Ordinary text, fully modelled: diffable, and only re-emitted when it changes.</summary>
    Text,

    /// <summary>
    /// Written while the pen could not be modelled — an escape sequence this buffer does not understand
    /// (an OSC hyperlink, a cursor move, an erase, an SGR attribute outside <see cref="VtStyle"/>'s
    /// vocabulary). Always re-emitted, never diffed, so anything we cannot reason about degrades to the
    /// immediate-mode behaviour it had before rather than being modelled wrongly.
    /// </summary>
    Opaque,

    /// <summary>
    /// Covered by an image the buffer does not own the pixels of (a Sixel blit). The diff must never write
    /// a glyph here, because doing so punches a hole in the picture. Writing text to the cell reclaims it
    /// as <see cref="Text"/>, which is what lets a shrinking image give its cells back.
    /// </summary>
    Image,
}

/// <summary>One character cell: a glyph, the pen it was written in, and what kind of cell it is.</summary>
/// <param name="Glyph">The character. Space for an unwritten cell.</param>
/// <param name="Style">Foreground/background pair as parsed back out of the SGR stream.</param>
/// <param name="Reverse">Reverse video (<c>\e[7m</c>), which is a pen attribute rather than a colour.</param>
/// <param name="Kind">See <see cref="CellKind"/>.</param>
public readonly record struct Cell(char Glyph, VtStyle Style, bool Reverse, CellKind Kind)
{
    public static readonly Cell Blank = new(' ', default, false, CellKind.Text);
}

/// <summary>
/// Where a <see cref="CellBuffer"/> flush emits to. Deliberately not the terminal: the diff is pure
/// arithmetic over two grids, so it is worth being able to assert on the emitted calls directly.
/// </summary>
public interface ICellSink
{
    /// <summary>Place the cursor at an absolute cell position.</summary>
    void MoveTo(int column, int row);

    /// <summary>Select the pen for subsequent <see cref="Write"/> calls.</summary>
    void SetPen(VtStyle style, bool reverse);

    /// <summary>Emit a run of glyphs at the current position in the current pen.</summary>
    void Write(ReadOnlySpan<char> run);
}

/// <summary>
/// A front/back character-cell buffer with a diffing flush: writes land in the back buffer, and
/// <see cref="Flush"/> emits only the cells that actually changed.
///
/// <para><b>Why.</b> Console.Lib was immediate-mode — every widget wrote its whole region straight to the
/// terminal, as one string of SGR plus padded text. That is invisible for a redraw the user asked for and
/// very visible for one on a clock: a ticking row repainted every cell every second, including the padding
/// spaces, which reads as a flash. With a diff, a clock tick emits the two digits that changed.</para>
///
/// <para><b>How writes get in.</b> <see cref="Write"/> parses the SGR that Console.Lib itself generates
/// (<see cref="VtStyle.Apply"/> / <see cref="VtStyle.ApplyFg"/> / <see cref="VtStyle.Reset"/> /
/// <see cref="VtStyle.ReverseOn"/> — a closed vocabulary) back into a pen, and printable runes become cells.
/// This is NOT a terminal emulator and does not try to be: anything outside that vocabulary makes the pen
/// unmodellable, and cells written under it are <see cref="CellKind.Opaque"/> — always re-emitted, never
/// diffed. Being wrong about a pen would show up as a missing repaint, so the buffer declines to guess.</para>
///
/// <para><b>Images.</b> A Sixel blit writes pixels over a cell region through a channel this buffer never
/// sees (<c>ITerminalViewport.OutputStream</c>). <see cref="MarkImage"/> is how the owner declares that
/// region, and those cells are then excluded from the diff entirely — see <see cref="CellKind.Image"/>.
/// The blit must happen AFTER the flush for that frame, so the cell diff can never paint over the picture.
/// </para>
/// </summary>
public sealed class CellBuffer
{
    private Cell[] _front = [];
    private Cell[] _back = [];
    private int _width;
    private int _height;

    private int _column;
    private int _row;

    private VtStyle _pen;
    private bool _reverse;

    /// <summary>True once an escape we cannot model has been seen: cells written now are Opaque.</summary>
    private bool _penUnmodellable;

    /// <summary>The colour mode the pen parser should read SGR-16 codes against.</summary>
    public ColorMode ColorMode { get; set; } = ColorMode.Sgr16;

    public int Width => _width;

    public int Height => _height;

    /// <summary>
    /// Resizes the grid, discarding both buffers' contents. The front buffer is filled with a sentinel that
    /// cannot equal any real cell, so the first flush after a resize repaints everything — a resized
    /// terminal has no relationship to what was on screen before it.
    /// </summary>
    public void Resize(int width, int height)
    {
        _width = Math.Max(0, width);
        _height = Math.Max(0, height);
        var count = _width * _height;

        _back = new Cell[count];
        _front = new Cell[count];
        Array.Fill(_back, Cell.Blank);
        // '\0' never appears in a written cell, so every cell reads as changed on the next flush.
        Array.Fill(_front, new Cell('\0', default, false, CellKind.Text));

        _column = 0;
        _row = 0;
        _pen = default;
        _reverse = false;
        _penUnmodellable = false;
    }

    /// <summary>Moves the write cursor. Out-of-range positions are clamped, matching the terminal.</summary>
    public void MoveTo(int column, int row)
    {
        _column = Math.Clamp(column, 0, Math.Max(0, _width - 1));
        _row = Math.Clamp(row, 0, Math.Max(0, _height - 1));
    }

    /// <summary>The back buffer's current cell, for tests and for the inspector's cell plane.</summary>
    public Cell BackAt(int column, int row) => InBounds(column, row) ? _back[row * _width + column] : Cell.Blank;

    /// <summary>
    /// The FRONT buffer's cell — what was last actually emitted, i.e. what is on screen. This is what the
    /// debug inspector reports: it is not a parallel model that could drift from the terminal, it is the
    /// record of what was sent.
    /// </summary>
    public Cell FrontAt(int column, int row) => InBounds(column, row) ? _front[row * _width + column] : Cell.Blank;

    /// <summary>Reads a row of the front buffer as text — the plain-text view of the screen.</summary>
    public string FrontRowText(int row)
    {
        if (row < 0 || row >= _height) return "";
        var sb = new StringBuilder(_width);
        for (var c = 0; c < _width; c++)
        {
            var cell = _front[row * _width + c];
            sb.Append(cell.Glyph == '\0' ? ' ' : cell.Glyph);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Declares that an image owns this cell region, so the diff will not write glyphs into it. Idempotent,
    /// and re-declaring after a re-blit is harmless. Cells outside a later, smaller region return to text
    /// automatically the moment anything writes to them.
    /// </summary>
    public void MarkImage(int column, int row, int width, int height)
    {
        for (var r = row; r < row + height; r++)
        {
            for (var c = column; c < column + width; c++)
            {
                if (!InBounds(c, r)) continue;
                var i = r * _width + c;
                _back[i] = _back[i] with { Kind = CellKind.Image };
            }
        }
    }

    /// <summary>
    /// Writes <paramref name="text"/> at the cursor, interpreting the SGR sequences Console.Lib emits and
    /// treating everything else as unmodellable (see the class remarks). Advances the cursor; wraps to the
    /// next row at the right edge and stops at the bottom, as a terminal would.
    /// </summary>
    public void Write(ReadOnlySpan<char> text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];

            if (ch == '\e')
            {
                i += ConsumeEscape(text[i..]) - 1;
                continue;
            }

            if (ch == '\n') { _row++; _column = 0; continue; }
            if (ch == '\r') { _column = 0; continue; }
            if (char.IsControl(ch)) continue;

            if (_row >= _height) return;
            if (_column >= _width) { _column = 0; _row++; if (_row >= _height) return; }

            _back[_row * _width + _column] = new Cell(
                ch, _pen, _reverse, _penUnmodellable ? CellKind.Opaque : CellKind.Text);
            _column++;
        }
    }

    /// <summary>
    /// Emits the difference between the back and front buffers and swaps them.
    ///
    /// <para>Runs are coalesced per row: a maximal span of changed cells sharing one pen becomes a single
    /// <see cref="ICellSink.MoveTo"/> + <see cref="ICellSink.SetPen"/> + <see cref="ICellSink.Write"/>.
    /// <see cref="CellKind.Image"/> cells break a run and are never written. <see cref="CellKind.Opaque"/>
    /// cells always count as changed.</para>
    /// </summary>
    /// <returns>How many cells were emitted — zero meaning the screen was already correct, which is the
    /// whole point and what a test asserts on.</returns>
    public int Flush(ICellSink sink)
    {
        var emitted = 0;
        var run = new StringBuilder();

        for (var r = 0; r < _height; r++)
        {
            var c = 0;
            while (c < _width)
            {
                var i = r * _width + c;
                var back = _back[i];

                // An image owns its pixels; writing a glyph here would punch a hole in the picture. Still
                // reconcile the front buffer, or the cell would read as dirty forever.
                if (back.Kind == CellKind.Image)
                {
                    _front[i] = back;
                    c++;
                    continue;
                }

                if (!IsDirty(back, _front[i]))
                {
                    c++;
                    continue;
                }

                // Start a run: same pen, contiguous, all dirty, no image in the way.
                var pen = back.Style;
                var reverse = back.Reverse;
                var start = c;
                run.Clear();

                while (c < _width)
                {
                    var j = r * _width + c;
                    var cell = _back[j];
                    if (cell.Kind == CellKind.Image) break;
                    if (!IsDirty(cell, _front[j])) break;
                    if (cell.Style != pen || cell.Reverse != reverse) break;

                    run.Append(cell.Glyph == '\0' ? ' ' : cell.Glyph);
                    _front[j] = cell;
                    c++;
                }

                sink.MoveTo(start, r);
                sink.SetPen(pen, reverse);
                foreach (var chunk in run.GetChunks())
                {
                    sink.Write(chunk.Span);
                }
                emitted += run.Length;
            }
        }

        return emitted;
    }

    /// <summary>An Opaque cell is always dirty — that is what "we could not model this" buys.</summary>
    private static bool IsDirty(in Cell back, in Cell front)
        => back.Kind == CellKind.Opaque || back != front;

    private bool InBounds(int column, int row)
        => column >= 0 && row >= 0 && column < _width && row < _height;

    /// <summary>
    /// Interprets one escape sequence starting at <paramref name="text"/>[0] == ESC and returns how many
    /// chars it consumed. Recognised: SGR (CSI … 'm') carrying only parameters <see cref="VtStyle"/> emits.
    /// Anything else — a different CSI final byte, an OSC, a bare escape — leaves the pen unmodellable, so
    /// subsequent cells are <see cref="CellKind.Opaque"/> and always re-emitted.
    /// </summary>
    private int ConsumeEscape(ReadOnlySpan<char> text)
    {
        // CSI: ESC '[' params final
        if (text.Length >= 2 && text[1] == '[')
        {
            var end = 2;
            while (end < text.Length && !char.IsBetween(text[end], '@', '~')) end++;
            if (end >= text.Length)
            {
                _penUnmodellable = true;
                return text.Length;
            }

            var final = text[end];
            var pars = text[2..end];
            var consumed = end + 1;

            if (final == 'm' && TryApplySgr(pars))
            {
                return consumed;
            }

            // A CSI we do not model — cursor addressing, erase, scroll region. The buffer's idea of where
            // the cursor is and what is on screen is now untrustworthy for these cells.
            _penUnmodellable = true;
            return consumed;
        }

        // OSC: ESC ']' … BEL or ST. Hyperlinks and titles land here (mdcat emits them).
        if (text.Length >= 2 && text[1] == ']')
        {
            _penUnmodellable = true;
            for (var i = 2; i < text.Length; i++)
            {
                if (text[i] == '\a') return i + 1;
                if (text[i] == '\e' && i + 1 < text.Length && text[i + 1] == '\\') return i + 2;
            }
            return text.Length;
        }

        _penUnmodellable = true;
        return Math.Min(2, text.Length);
    }

    /// <summary>
    /// Applies an SGR parameter list to the pen. False when ANY parameter is outside the vocabulary
    /// <see cref="VtStyle"/> emits — the caller then marks the pen unmodellable rather than silently
    /// carrying a pen that is only mostly right, because a wrong pen shows up as a MISSING repaint.
    /// </summary>
    private bool TryApplySgr(ReadOnlySpan<char> pars)
    {
        // A bare "\e[m" means reset, as does "\e[0m".
        if (pars.IsEmpty)
        {
            ResetPen();
            return true;
        }

        Span<int> codes = stackalloc int[16];
        var n = 0;
        var value = 0;
        var any = false;

        foreach (var ch in pars)
        {
            if (char.IsAsciiDigit(ch)) { value = value * 10 + (ch - '0'); any = true; continue; }
            if (ch != ';') return false;
            if (n >= codes.Length) return false;
            codes[n++] = any ? value : 0;
            value = 0;
            any = false;
        }
        if (n >= codes.Length) return false;
        codes[n++] = any ? value : 0;

        for (var i = 0; i < n; i++)
        {
            switch (codes[i])
            {
                case 0: ResetPen(); break;
                case 7: _reverse = true; break;
                case 27: _reverse = false; break;

                // Truecolor: 38;2;r;g;b and 48;2;r;g;b — the only extended forms VtStyle produces.
                case 38 or 48 when i + 4 < n && codes[i + 1] == 2:
                    {
                        var colour = new RGBAColor32((byte)codes[i + 2], (byte)codes[i + 3], (byte)codes[i + 4], 0xff);
                        _pen = codes[i] == 38 ? _pen with { Foreground = colour } : _pen with { Background = colour };
                        i += 4;
                        break;
                    }

                case >= 30 and <= 37: _pen = _pen with { Foreground = ((SgrColor)(codes[i] - 30)).ToRgba() }; break;
                case >= 90 and <= 97: _pen = _pen with { Foreground = ((SgrColor)(codes[i] - 82)).ToRgba() }; break;
                case >= 40 and <= 47: _pen = _pen with { Background = ((SgrColor)(codes[i] - 40)).ToRgba() }; break;
                case >= 100 and <= 107: _pen = _pen with { Background = ((SgrColor)(codes[i] - 92)).ToRgba() }; break;

                default: return false;
            }
        }

        return true;
    }

    private void ResetPen()
    {
        _pen = default;
        _reverse = false;
        // A reset is something we DO understand, so it also clears the unmodellable state: whatever we
        // could not parse, the pen is now known again.
        _penUnmodellable = false;
    }
}
