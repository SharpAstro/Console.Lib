using System.Text;

namespace Console.Lib;

/// <summary>
/// Manages terminal lifecycle (alternate buffer, cursor, mouse tracking)
/// and provides platform-aware mouse input reading.
/// </summary>
public sealed class VirtualTerminal : IVirtualTerminal
#if DEBUG
    // Satisfied by members that already exist; declaring it is what lets the debug inspector be driven
    // against a fake screen in a test instead of only against a real console.
    , IInspectableTerminal
#endif
{
    private bool _initialized;
    private bool _noConsole;
    private HashSet<TerminalCapability> _deviceCapabilities = [];
    private TermCell? _cellSize;
    private bool _alternateScreen;
    private Stream? _stdIn;
    private static readonly bool s_isInputRedirected = System.Console.IsInputRedirected;
    private static readonly bool s_isOutputRedirected = System.Console.IsOutputRedirected;
    private static readonly bool s_noColor = Environment.GetEnvironmentVariable("NO_COLOR") is not null;

    public bool IsInputRedirected => s_isInputRedirected || _noConsole;
    public bool IsOutputRedirected => s_isOutputRedirected || _noConsole;

    public async Task InitAsync()
    {
        if (_initialized) return;

        System.Console.InputEncoding = Encoding.UTF8;
        System.Console.OutputEncoding = Encoding.UTF8;

        // Seed terminal size before alternate screen
        try
        {
            _lastSize = (System.Console.WindowWidth, System.Console.WindowHeight);
        }
        catch (System.IO.IOException)
        {
            // Console handle is invalid (e.g. no TTY attached) — degrade gracefully
            _noConsole = true;
            _lastSize = (80, 24);
        }

        _cellSize = new TermCell(10, 20);

        if (_noConsole)
        {
            _initialized = true;
            return;
        }

        var daResponse = await GetControlSequenceResponseAsync("\e[0c", 'c');
        _deviceCapabilities = [.. daResponse
                .TrimStart('\e', '[', '?')
                .TrimEnd('c')
                .Split(';')
                .Select(s => Enum.TryParse<TerminalCapability>(s, out var cap) ? cap : (TerminalCapability?)null)
                .Where(cap => cap.HasValue)
                .Select(cap => cap!.Value)
        ];

        var csResponse = await GetControlSequenceResponseAsync("\e[16t", 't');
        var tIndex = csResponse.IndexOf('t');
        if (tIndex >= 0)
        {
            var parts = csResponse[..tIndex].TrimStart('\e', '[').Split(';');
            if (parts.Length == 3 &&
                parts[0] == "6" &&
                uint.TryParse(parts[1], out var height) &&
                uint.TryParse(parts[2], out var width))
            {
                _cellSize = new TermCell((byte)width, (byte)height);
            }
        }

        _initialized = true;
    }

    public ImageDisplayCapability ImageDisplayCapability
    {
        get
        {
            if (!_initialized) throw new InvalidOperationException("Call InitAsync() first.");
            return ResolveImageDisplayCapability(s_noColor,
                _deviceCapabilities.Contains(TerminalCapability.Color),
                _deviceCapabilities.Contains(TerminalCapability.Sixel));
        }
    }

    public bool HasSixelSupport
    {
        get
        {
            if (!_initialized) throw new InvalidOperationException("Call InitAsync() first.");
            return !IsOutputRedirected && ImageDisplayCapability == ImageDisplayCapability.Sixel;
        }
    }

    public bool HasColorSupport
    {
        get
        {
            if (!_initialized) throw new InvalidOperationException("Call InitAsync() first.");
            return _deviceCapabilities.Contains(TerminalCapability.Color);
        }
    }

    public ColorMode ColorMode => ResolveColorMode(s_isOutputRedirected || _noConsole, s_noColor,
        _initialized && _deviceCapabilities.Contains(TerminalCapability.Color));

    internal static ColorMode ResolveColorMode(bool isOutputRedirected, bool noColor, bool hasColorCapability)
        => isOutputRedirected || noColor ? ColorMode.None
           : hasColorCapability ? ColorMode.TrueColor : ColorMode.Sgr16;

    internal static ImageDisplayCapability ResolveImageDisplayCapability(
        bool noColor, bool hasColorCapability, bool hasSixelCapability)
        => noColor || !hasColorCapability ? ImageDisplayCapability.NoColor
           : hasSixelCapability ? ImageDisplayCapability.Sixel
           : ImageDisplayCapability.AsciiBlock;

    public TermCell CellSize =>
        _cellSize ?? throw new InvalidOperationException("Call InitAsync() first.");

    /// <summary>
    /// Enters the alternate screen buffer, hides the cursor, and enables mouse tracking.
    /// </summary>
    public void EnterAlternateScreen()
    {
        if (s_isInputRedirected) return; // No alternate screen when stdin is redirected

        if (OperatingSystem.IsWindows())
        {
            WindowsConsoleInput.EnableVirtualTerminalIO();
        }

        System.Console.Write("\e[?1049h"); // Enter alternate buffer
        System.Console.Write("\e[?25l");   // Hide cursor
        System.Console.Write("\e[?1000h"); // VT200 mouse tracking (basic button press/release and wheel)
        System.Console.Write("\e[?1002h"); // Button-motion tracking (drag reports while a button is held)
        System.Console.Write("\e[?1006h"); // SGR extended tracking
        Flush();

        _stdIn = System.Console.OpenStandardInput();
        _alternateScreen = true;
    }

    public bool IsAlternateScreen => _alternateScreen;

    public (int Column, int Row) Offset => (0, 0);

    private (int Width, int Height) _lastSize;

    public (int Width, int Height) Size
    {
        get
        {
            try
            {
                _lastSize = (System.Console.WindowWidth, System.Console.WindowHeight);
            }
            catch (System.IO.IOException)
            {
                // Console handle can become temporarily invalid — return last known good size
            }
            return _lastSize;
        }
    }

    /// <summary>
    /// The front/back cell buffer, when this terminal is running buffered — see
    /// <see cref="EnableCellBuffer"/>. Null means immediate mode.
    /// </summary>
    public CellBuffer? CellBuffer { get; private set; }

    /// <summary>
    /// Running total of cells every <see cref="Flush"/> has sent to the terminal, and how many of those were
    /// <see cref="CellKind.Opaque"/> (re-sent because their pen could not be modelled, not because they
    /// changed). A host samples the totals and diffs across its own interval.
    /// <para>
    /// Exposed because "is it repainting too much?" is otherwise unanswerable from outside: a caller can see
    /// a flickering screen and can see its own paint calls, but not how much of the paint survived the diff.
    /// TOTALS, deliberately, not last-flush values: a frame can flush more than once, and a
    /// last-flush property reports only the final one — which is precisely how a mid-paint flush bug hid
    /// from the first version of this diagnostic while being the whole problem.
    /// </para>
    /// </summary>
    public long FlushedCellsTotal { get; private set; }

    /// <inheritdoc cref="FlushedCellsTotal"/>
    public long FlushedOpaqueCellsTotal { get; private set; }

    /// <summary>
    /// Switches this terminal to a buffered, DIFFING write path: writes accumulate in
    /// <see cref="CellBuffer"/> and <see cref="Flush"/> emits only the cells that changed.
    ///
    /// <para>Opt-in, and off by default, deliberately. Every widget and every consumer currently relies on
    /// writes reaching the terminal immediately, so flipping the default would change the behaviour of
    /// mdcat and of every hosted app at once. A caller that wants the diff — anything with a clock, or
    /// anything that wants the debug inspector's cell plane — asks for it.</para>
    ///
    /// <para>Sixel is the interaction to be careful with: a blit writes pixels over cells through
    /// <see cref="OutputStream"/>, which the buffer never sees, so its owner must declare the region with
    /// <see cref="Console.Lib.CellBuffer.MarkImage"/> and blit only AFTER a flush.</para>
    /// </summary>
    public void EnableCellBuffer()
    {
        var (width, height) = Size;
        CellBuffer = new CellBuffer { ColorMode = ColorMode };
        CellBuffer.Resize(width, height);
        _sink = new ConsoleCellSink();

        // Seed the size the resize-detection in Flush compares against. Without this it stays (0,0), so the
        // FIRST Flush always reads as a resize and calls Resize again -- which reallocates the back buffer
        // and fills it with blanks, discarding everything the frame had already painted. The screen then
        // shows only what happened to be written AFTER that flush.
        //
        // An app that repaints unconditionally per frame heals on frame two and never sees it. One that
        // paints on demand and leaves cells standing (a periodic table redrawn only on selection change)
        // loses that content permanently -- which is how this was found.
        _bufferedSize = (width, height);
    }

    private ConsoleCellSink? _sink;
    private (int Width, int Height) _bufferedSize;

    /// <summary>Writes a diffed run straight to the console — <see cref="CellBuffer"/>'s emit target.</summary>
    private sealed class ConsoleCellSink : ICellSink
    {
        private VtStyle? _pen;
        private bool _reverse;
        private ColorMode _mode = ColorMode.Sgr16;
        private string? _link;
        private bool _linkKnown;

        public ColorMode Mode { set => _mode = value; }

        /// <summary>Forgets the pen, so the next run re-states it. Needed after anything that could have
        /// changed the terminal's state behind our back (a clear, a Sixel blit).</summary>
        public void Invalidate()
        {
            _pen = null;

            // An open hyperlink is terminal state as much as the pen is, and a clear does not close one.
            // Forgetting it -- rather than assuming it closed -- makes the next SetLink re-state whatever it
            // should be, including emitting the close for a link the terminal may still have open.
            _linkKnown = false;
        }

        /// <summary>
        /// Moves the cursor with a VT sequence in the SAME stream as the pen and the glyphs, rather than
        /// through <see cref="System.Console.SetCursorPosition"/>.
        /// <para>
        /// <b>A diff is one ordered sequence and has to be delivered as one.</b> SetCursorPosition is a Win32
        /// call on the screen buffer; the runs around it are VT bytes on stdout. Splitting the sequence across
        /// two delivery mechanisms makes the console host synchronise the buffer on every move, and a frame
        /// that emits a dozen small runs therefore costs a dozen of those -- visible as a flicker at exactly
        /// the rate the screen updates, which is how it was found (a clock ticking once a second, with the
        /// paint accounting showing only 15 cells emitted for it). As bytes it is also portable and correctly
        /// ordered by construction.
        /// </para>
        /// </summary>
        public void MoveTo(int column, int row) => System.Console.Write(MoveEscape(column, row));

        public void SetPen(VtStyle style, bool reverse)
        {
            // Only re-state the pen when it actually differs: the whole point is to emit less.
            if (_pen == style && _reverse == reverse) return;

            // Reverse has to be stated in BOTH directions. Turning it on without ever turning it off leaves
            // the terminal inverted for every run after it, because Apply emits colours and no attribute
            // reset -- one reversed cell (a text cursor is the usual source) inverted the whole screen from
            // that point on: headers came out as a solid bar of their foreground, the selection as dark text
            // on white. Emitted only on a CHANGE, so a screen with no reverse cells is byte-identical, and
            // also after Invalidate, when the terminal's attribute state is unknown rather than merely stale.
            var mustStateReverse = _pen is null || _reverse != reverse;
            _pen = style;
            _reverse = reverse;

            System.Console.Write(PenEscape(style, _mode, reverse, mustStateReverse));
        }

        /// <summary>
        /// States the hyperlink for the run about to be written, emitting only on a change — the same
        /// "emit less" rule <see cref="SetPen"/> follows, and it matters more here: a list where every row
        /// is a link would otherwise pay an open and a close per row per frame.
        /// <para>
        /// The id is what makes a DIFFED link hold together; see <see cref="Osc8.Open(string, string)"/>.
        /// </para>
        /// </summary>
        public void SetLink(string? url)
        {
            if (_linkKnown && _link == url) return;

            _link = url;
            _linkKnown = true;

            // ColorMode.None means "no escapes at all" for the pen, and a hyperlink is no different.
            if (_mode == ColorMode.None) return;

            System.Console.Write(url is null ? Osc8.Close : Osc8.Open(url, Osc8.IdFor(url)));
        }

        public void Write(ReadOnlySpan<char> run) => System.Console.Out.Write(run);
    }

    /// <summary>
    /// CUP: move the cursor to a 0-based cell. VT rows and columns are 1-based, which is the whole reason this
    /// is a named function rather than an inline interpolation.
    /// </summary>
    internal static string MoveEscape(int column, int row) => $"\e[{row + 1};{column + 1}H";

    /// <summary>
    /// The escape sequence that puts the terminal into <paramref name="style"/>, plus the reverse-video
    /// attribute when <paramref name="mustStateReverse"/> says the terminal's current attribute cannot be
    /// relied on. Pulled out of the sink so the both-directions rule is assertable without a console.
    /// </summary>
    internal static string PenEscape(VtStyle style, ColorMode mode, bool reverse, bool mustStateReverse)
    {
        var attribute = mustStateReverse
            ? reverse ? VtStyle.ReverseOn : VtStyle.ReverseOff
            : "";
        return $"{style.Apply(mode)}{attribute}";
    }

    public void Clear()
    {
        System.Console.Clear();
        if (CellBuffer is { } buffer)
        {
            // A clear leaves the terminal blank, which the front buffer knows nothing about: re-arm it so
            // the next flush repaints in full rather than trusting a front buffer that now describes
            // content the terminal has thrown away.
            var (width, height) = Size;
            buffer.Resize(width, height);
            _sink?.Invalidate();
        }
    }

    public void SetCursorPosition(int left, int top)
    {
        var (width, height) = Size;
        var col = Math.Clamp(left, 0, width - 1);
        var row = Math.Clamp(top, 0, height - 1);

        if (CellBuffer is { } buffer)
        {
            buffer.MoveTo(col, row);
            return;
        }

        System.Console.SetCursorPosition(col, row);
    }

    public void Write(string text)
    {
        if (CellBuffer is { } buffer)
        {
            buffer.Write(text);
            return;
        }

        System.Console.Write(text);
    }

    public void WriteLine(string? text = null) => System.Console.WriteLine(text);

    public void Flush()
    {
        if (CellBuffer is { } buffer && _sink is { } sink)
        {
            // A resize invalidates the whole grid; the buffer re-arms itself so the next diff is a full
            // repaint (see CellBuffer.Resize).
            var size = Size;
            if (size != _bufferedSize)
            {
                _bufferedSize = size;
                buffer.Resize(size.Width, size.Height);
                sink.Invalidate();
            }

            sink.Mode = ColorMode;
            FlushedCellsTotal += buffer.Flush(sink);
            FlushedOpaqueCellsTotal += buffer.LastFlushOpaqueCells;
        }

        System.Console.Out.Flush();
    }

    /// <summary>
    /// Hands the terminal over to raw bytes: emits any pending cell diff FIRST, so buffered content cannot
    /// land on top of the picture afterwards, then moves the REAL cursor (not the buffer's) to where the
    /// blit must start.
    /// <para>
    /// The pen is forgotten as well. Raw output can leave the terminal in any SGR state it likes, so a
    /// remembered pen would be a lie and the next run would skip re-stating it.
    /// </para>
    /// </summary>
    public void BeginRawOutput(int column, int row)
    {
        var (width, height) = Size;
        var col = Math.Clamp(column, 0, Math.Max(0, width - 1));
        var r = Math.Clamp(row, 0, Math.Max(0, height - 1));

        if (CellBuffer is null)
        {
            System.Console.SetCursorPosition(col, r);
            return;
        }

        Flush();
        System.Console.SetCursorPosition(col, r);
        System.Console.Out.Flush();
        _sink?.Invalidate();
    }

    /// <inheritdoc />
    public void MarkRawRegion(int column, int row, int width, int height)
        => CellBuffer?.MarkImage(column, row, width, height);

    public Stream OutputStream { get; } = System.Console.OpenStandardOutput();

#if DEBUG
    /// <summary>
    /// Input injected by a driver rather than typed — the debug inspector's `key` and `click`. Kept
    /// separate from the real stream and drained FIRST, so a synthetic event cannot be interleaved into
    /// the middle of an escape sequence the parser is midway through reading.
    /// <para>
    /// DEBUG-only, like the inspector that feeds it: a release build carries neither the queue nor the
    /// check for it, so the input path costs exactly what it did before.
    /// </para>
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentQueue<ConsoleInputEvent> _injected = new();

    /// <summary>
    /// Queues a synthetic input event, to be returned by the next <see cref="TryReadInput"/> as though it
    /// had been typed. Thread-safe: the inspector's socket thread enqueues, the app's loop thread drains.
    /// </summary>
    public void Inject(ConsoleInputEvent evt) => _injected.Enqueue(evt);
#endif

    public bool HasInput() =>
#if DEBUG
        !_injected.IsEmpty ||
#endif
        (s_isInputRedirected ? System.Console.In.Peek() != -1 : System.Console.KeyAvailable);

    /// <summary>
    /// Attempts to read input from the terminal.
    /// Returns a mouse event if mouse input was received, or a raw key character if keyboard input was received.
    /// Mouse input takes precedence; both may be null if the consumed input was neither.
    /// </summary>
    public ConsoleInputEvent TryReadInput()
    {
#if DEBUG
        // Injected events come first and never mix with the real stream — see Inject.
        if (_injected.TryDequeue(out var synthetic))
        {
            return synthetic;
        }
#endif

        // only in alternate screen we enabled SGR mouse tracking, so we only attempt to parse it there
        if (_alternateScreen)
        {
            var result = ParseSgrInput();

            if (result.Mouse is not { } r || _cellSize is not { Width: var cw, Height: var ch })
            {
                return result;
            }

            // Normalize cell coordinates to pixels (preserve IsMotion — parsed from the xterm drag bit)
            return new(new MouseEvent(r.Button, r.X * (int)cw, r.Y * (int)ch, r.IsRelease) { IsMotion = r.IsMotion }, ConsoleKey.None, result.Modifiers);
        }
        else if (s_isInputRedirected)
        {
            var b = System.Console.In.Read();
            if (b == -1) return default;

            var (key, modifiers) = ByteToConsoleKey(b);
            // System.Console.In is text-decoded (UTF-8 once we set InputEncoding in InitAsync),
            // so a single Read() already yields a UTF-16 code unit. Promote BMP chars to a Rune;
            // surrogate halves are skipped here because pairing them across reads would require
            // a buffer the redirected path doesn't keep.
            var keyChar = TryPromoteCharToRune((char)b);
            return new(null, key, modifiers, keyChar);
        }
        else
        {
            var first = System.Console.ReadKey(intercept: true);

            if (first.Key == ConsoleKey.F1)
            {
                return new(null, ConsoleKey.None, first.Modifiers);
            }
            else if (first.Key != ConsoleKey.Escape)
            {
                return new(null, first.Key, first.Modifiers, TryPromoteCharToRune(first.KeyChar));
            }
            else
            {
                return new(null, ConsoleKey.None, first.Modifiers);
            }
        }
    }

    // Promotes a UTF-16 code unit to a Rune iff it represents a printable, non-surrogate
    // codepoint. Used by the non-byte input paths (System.Console.ReadKey / In.Read).
    private static Rune? TryPromoteCharToRune(char c)
    {
        if (c < 0x20 || c == 0x7F) return null; // C0 controls + DEL
        if (char.IsSurrogate(c)) return null;
        return new Rune(c);
    }

    public ValueTask DisposeAsync()
    {
        if (_alternateScreen)
        {
            System.Console.Write("\e[?1000l"); // Disable VT200 mouse tracking
            System.Console.Write("\e[?1002l"); // Disable button-motion tracking
            System.Console.Write("\e[?1006l"); // Disable SGR extended tracking

            System.Console.Write("\e[?25h");   // Show cursor
            System.Console.Write("\e[?1049l"); // Leave alternate buffer
        }

        if (OperatingSystem.IsWindows())
        {
            WindowsConsoleInput.RestoreConsoleMode();
        }

        OutputStream.Dispose();

        if (_stdIn is { } stdIn)
        {
            return stdIn.DisposeAsync();
        }
        return ValueTask.CompletedTask;
    }

    private static async Task<string> GetControlSequenceResponseAsync(string sequence, char terminator)
    {
        if (s_isInputRedirected) return string.Empty;

        const int maxTries = 10;

        System.Console.Write(sequence);
        System.Console.Out.Flush();

        var response = new StringBuilder();

        try
        {
            var tries = 0;
            while (!System.Console.KeyAvailable && tries++ < maxTries)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10));
            }

            while (System.Console.KeyAvailable)
            {
                var key = System.Console.ReadKey(true);
                response.Append(key.KeyChar);

                if (key.KeyChar == terminator)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Terminal did not respond in time
        }

        return response.ToString();
    }

    /// <summary>
    /// Parses an SGR mouse event (CSI &lt; Pb;Px;Py M/m), or returns the raw key character
    /// if the input was not an escape sequence.
    /// </summary>
    private ConsoleInputEvent ParseSgrInput()
    {
        if (_stdIn is not { })
        {
            throw new InvalidOperationException("Standard input stream is not available.");
        }

        var sb = new StringBuilder();

        var @byte = _stdIn.ReadByte(); // Consume the initial ESC

        if (@byte == -1)
        {
            return default;
        }
        else if (@byte != '\e')
        {
            var (key, modifiers) = ByteToConsoleKey(@byte);
            // Buffer UTF-8 continuation bytes for non-ASCII codepoints so non-US-layout
            // input (é, ñ, 中, 🙂, …) survives the byte stream. ASCII bytes return a
            // single-byte Rune; control bytes (< 0x20 or 0x7F) skip KeyChar entirely.
            var keyChar = TryReadRuneFrom(@byte, _stdIn!);
            return new(null, key, modifiers, keyChar);
        }

        while (s_isInputRedirected ? System.Console.In.Peek() != -1 : System.Console.KeyAvailable)
        {
            var ch = _stdIn.ReadByte();
            if (ch == -1)
            {
                return default;
            }

            // SGR mouse terminator: M (press) or m (release) — don't append, params are already in sb
            if (ch is 'M' or 'm')
            {
                var isRelease = ch == 'm';
                var parts = sb.ToString().TrimStart('[', '<').Split(';');
                if (parts.Length == 3 &&
                    int.TryParse(parts[0], out var pb) &&
                    int.TryParse(parts[1], out var x) &&
                    int.TryParse(parts[2], out var y))
                {
                    // Pb encodes button in bits 0-1, modifiers in bits 2-4, bit 5 = motion (drag), bit 6 = scroll wheel
                    var button = pb & 0x43;
                    var isMotion = (pb & 0x20) != 0;
                    var modifiers = (ConsoleModifiers)0;
                    if ((pb & 0x04) != 0) modifiers |= ConsoleModifiers.Shift;
                    if ((pb & 0x08) != 0) modifiers |= ConsoleModifiers.Alt;
                    if ((pb & 0x10) != 0) modifiers |= ConsoleModifiers.Control;
                    // SGR coordinates are 1-based
                    return new(new MouseEvent(button, x - 1, y - 1, isRelease) { IsMotion = isMotion }, ConsoleKey.None, modifiers);
                }
                return default;
            }

            sb.Append((char)ch);

            // CSI sequences: \e[ ...
            if (sb[0] == '[' && TryParseCsiKey(sb, out var csiKey, out var csiMods))
            {
                return new(null, csiKey, csiMods);
            }

            // SS3 sequences: \eO{P|Q|R|S} → F1-F4
            if (sb[0] == 'O' && sb.Length == 2)
            {
                var ss3Key = sb[1] switch
                {
                    'P' => ConsoleKey.F1,
                    'Q' => ConsoleKey.F2,
                    'R' => ConsoleKey.F3,
                    'S' => ConsoleKey.F4,
                    _ => ConsoleKey.None,
                };
                if (ss3Key != ConsoleKey.None)
                    return new(null, ss3Key, ConsoleModifiers.None);
            }
        }

        // No bytes followed ESC → bare Escape key
        if (sb.Length == 0)
        {
            return new(null, ConsoleKey.Escape, ConsoleModifiers.None);
        }

        return default;
    }

    /// <summary>
    /// Converts a raw stdin byte to a <see cref="ConsoleKey"/> with <see cref="ConsoleModifiers"/>.
    /// Uppercase letters produce Shift, Ctrl+letter (0x01-0x1A) produces Control.
    /// </summary>
    /// <summary>
    /// Tries to parse a CSI sequence from the buffer (including final byte as last char).
    /// Buffer format: [ params final — e.g. "[A", "[1;5A", "[3~", "[3;5~".
    /// Modifier parameter: 2=Shift, 3=Alt, 4=Shift+Alt, 5=Ctrl, 6=Ctrl+Shift, 7=Ctrl+Alt, 8=Ctrl+Shift+Alt.
    /// </summary>
    private static bool TryParseCsiKey(StringBuilder sb, out ConsoleKey key, out ConsoleModifiers modifiers)
    {
        key = ConsoleKey.None;
        modifiers = ConsoleModifiers.None;

        if (sb.Length < 2)
            return false;

        var final = sb[^1];
        var param = sb.ToString().AsSpan(1, sb.Length - 2); // between '[' and final byte

        // Extract optional modifier after ';': e.g. "1;5" → n=1, mod=5
        var semiPos = param.IndexOf(';');
        if (semiPos >= 0 && int.TryParse(param[(semiPos + 1)..], out var mod))
        {
            if ((mod - 1 & 1) != 0) modifiers |= ConsoleModifiers.Shift;
            if ((mod - 1 & 2) != 0) modifiers |= ConsoleModifiers.Alt;
            if ((mod - 1 & 4) != 0) modifiers |= ConsoleModifiers.Control;
            param = param[..semiPos];
        }

        // Letter final byte: arrow keys, Home, End
        if (final is >= 'A' and <= 'D' or 'H' or 'F')
        {
            key = final switch
            {
                'A' => ConsoleKey.UpArrow,
                'B' => ConsoleKey.DownArrow,
                'C' => ConsoleKey.RightArrow,
                'D' => ConsoleKey.LeftArrow,
                'H' => ConsoleKey.Home,
                _ => ConsoleKey.End,
            };
            return true;
        }

        // CSI Z final byte: back-tab (Shift+Tab). Standard: \e[Z.
        // Some terminals also emit \e[1;2Z explicitly with the Shift modifier param.
        if (final == 'Z')
        {
            key = ConsoleKey.Tab;
            modifiers |= ConsoleModifiers.Shift;
            return true;
        }

        // Tilde final byte: ESC [ n ~ or ESC [ n;mod ~
        if (final == '~' && int.TryParse(param, out var n))
        {
            key = n switch
            {
                1 => ConsoleKey.Home,
                2 => ConsoleKey.Insert,
                3 => ConsoleKey.Delete,
                4 => ConsoleKey.End,
                5 => ConsoleKey.PageUp,
                6 => ConsoleKey.PageDown,
                15 => ConsoleKey.F5,
                17 => ConsoleKey.F6,
                18 => ConsoleKey.F7,
                19 => ConsoleKey.F8,
                20 => ConsoleKey.F9,
                21 => ConsoleKey.F10,
                23 => ConsoleKey.F11,
                24 => ConsoleKey.F12,
                _ => ConsoleKey.None,
            };
            return key != ConsoleKey.None;
        }

        return false;
    }

    // Given a leading input byte, decodes the rest of the UTF-8 codepoint (if any) by
    // reading continuation bytes from the supplied stream, and returns the resulting Rune
    // iff it's a printable codepoint. ASCII control bytes (< 0x20 or 0x7F) and decode
    // failures return null so the caller falls back to the (ConsoleKey, ConsoleModifiers)
    // path. Internal-and-static so tests can drive it with a MemoryStream.
    internal static Rune? TryReadRuneFrom(int firstByte, Stream stdIn)
    {
        if (firstByte < 0)
        {
            return null;
        }
        if (firstByte < 0x80)
        {
            return (firstByte < 0x20 || firstByte == 0x7F) ? null : new Rune((char)firstByte);
        }

        // UTF-8 lead-byte shape: 110xxxxx → 1 cont, 1110xxxx → 2 cont, 11110xxx → 3 cont.
        int contBytes;
        if ((firstByte & 0xE0) == 0xC0) contBytes = 1;
        else if ((firstByte & 0xF0) == 0xE0) contBytes = 2;
        else if ((firstByte & 0xF8) == 0xF0) contBytes = 3;
        else return null; // bare continuation byte or 5/6-byte sequence — not valid UTF-8

        Span<byte> buf = stackalloc byte[4];
        buf[0] = (byte)firstByte;
        for (var i = 1; i <= contBytes; i++)
        {
            var b = stdIn.ReadByte();
            if (b < 0 || (b & 0xC0) != 0x80) return null; // truncated or not a continuation byte
            buf[i] = (byte)b;
        }

        return Rune.DecodeFromUtf8(buf[..(contBytes + 1)], out var rune, out _) == System.Buffers.OperationStatus.Done
            ? rune
            : null;
    }

    // Maps a single ASCII input byte to the (ConsoleKey, ConsoleModifiers) pair the
    // rest of the input pipeline expects. Shifted-symbol bytes (e.g. '!', '@', '_', '<')
    // resolve to the unshifted ConsoleKey + ConsoleModifiers.Shift so that
    // InputKeyCharMapping.ToChar can recover the original character on a US layout.
    internal static (ConsoleKey Key, ConsoleModifiers Modifiers) ByteToConsoleKey(int b) => b switch
    {
        >= 'a' and <= 'z' => ((ConsoleKey)(b - 'a' + 'A'), 0),
        >= 'A' and <= 'Z' => ((ConsoleKey)b, ConsoleModifiers.Shift),
        >= '0' and <= '9' => ((ConsoleKey)b, 0),
        // NO '\b' case: 0x08 falls through to the Ctrl+letter range below, where it is Ctrl+H. A special case
        // here used to claim it as Backspace, which made Ctrl+H the ONE letter in 0x01..0x1A that an app could
        // not bind -- the binding looked broken with nothing wrong at the call site. Nothing is lost: a
        // terminal sends DEL (0x7F) for the Backspace KEY, which the line below has always handled, and that
        // is the byte every Backspace handler in this library actually receives.
        '\t' => (ConsoleKey.Tab, 0),
        '\r' or '\n' => (ConsoleKey.Enter, 0),
        ' ' => (ConsoleKey.Spacebar, 0),
        0x7F => (ConsoleKey.Backspace, 0), // terminals send DEL (0x7F) for Backspace key
        // Shifted digit row (US layout): the byte already carries the shifted character,
        // so we report the underlying digit key plus the Shift modifier.
        '!' => (ConsoleKey.D1, ConsoleModifiers.Shift),
        '@' => (ConsoleKey.D2, ConsoleModifiers.Shift),
        '#' => (ConsoleKey.D3, ConsoleModifiers.Shift),
        '$' => (ConsoleKey.D4, ConsoleModifiers.Shift),
        '%' => (ConsoleKey.D5, ConsoleModifiers.Shift),
        '^' => (ConsoleKey.D6, ConsoleModifiers.Shift),
        '&' => (ConsoleKey.D7, ConsoleModifiers.Shift),
        '*' => (ConsoleKey.D8, ConsoleModifiers.Shift),
        '(' => (ConsoleKey.D9, ConsoleModifiers.Shift),
        ')' => (ConsoleKey.D0, ConsoleModifiers.Shift),
        '-' or '_' => (ConsoleKey.OemMinus, b == '_' ? ConsoleModifiers.Shift : 0),
        '+' or '=' => (ConsoleKey.OemPlus, b == '+' ? ConsoleModifiers.Shift : 0),
        '.' or '>' => (ConsoleKey.OemPeriod, b == '>' ? ConsoleModifiers.Shift : 0),
        ',' or '<' => (ConsoleKey.OemComma, b == '<' ? ConsoleModifiers.Shift : 0),
        '/' or '?' => (ConsoleKey.Oem2, b == '?' ? ConsoleModifiers.Shift : 0),
        '\\' or '|' => (ConsoleKey.Oem5, b == '|' ? ConsoleModifiers.Shift : 0),
        ';' or ':' => (ConsoleKey.Oem1, b == ':' ? ConsoleModifiers.Shift : 0),
        '\'' or '"' => (ConsoleKey.Oem7, b == '"' ? ConsoleModifiers.Shift : 0),
        '[' or '{' => (ConsoleKey.Oem4, b == '{' ? ConsoleModifiers.Shift : 0),
        ']' or '}' => (ConsoleKey.Oem6, b == '}' ? ConsoleModifiers.Shift : 0),
        '`' or '~' => (ConsoleKey.Oem3, b == '~' ? ConsoleModifiers.Shift : 0),
        >= 0x01 and <= 0x1A => ((ConsoleKey)(b - 0x01 + 'A'), ConsoleModifiers.Control),
        _ => (ConsoleKey.None, 0),
    };
}
