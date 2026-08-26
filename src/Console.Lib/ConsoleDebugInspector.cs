#if DEBUG
using System;
using System.Text;
using System.Text.Json;
using DIR.Lib;
using DIR.Lib.Diagnostics;

namespace Console.Lib;

/// <summary>
/// The slice of a terminal the inspector needs: how big it is, what it last emitted, and where to put
/// injected input. <see cref="VirtualTerminal"/> satisfies it already; naming it separately is what lets the
/// inspector be driven over a real socket in a test, against a fake screen, with no console attached.
/// </summary>
public interface IInspectableTerminal
{
    (int Width, int Height) Size { get; }

    TermCell CellSize { get; }

    /// <summary>Null when the terminal is running immediate-mode, in which case there is no screen to report.</summary>
    CellBuffer? CellBuffer { get; }

    /// <summary>Queues a synthetic event as though it had been typed.</summary>
    void Inject(ConsoleInputEvent evt);
}

/// <summary>
/// The terminal backend for <see cref="DebugInspectorCore"/>: lets a driver read the screen as TEXT, read
/// the app's own state, and inject keys and clicks — over a loopback socket, against the running app.
///
/// <para><b>The cell plane is what a terminal has and a GPU surface does not.</b> A pixel inspector can
/// only offer a screenshot to eyeball or hash; a terminal can be asserted in words — "the status bar reads
/// <c>White to move.</c>", "history row 3 is <c>4. Nc5xb7 Bc8xb7</c>". It reads
/// <see cref="CellBuffer.FrontAt"/>, i.e. the record of what was actually EMITTED, not a parallel model
/// that could drift from the terminal. Requires <see cref="VirtualTerminal.EnableCellBuffer"/>; without it
/// there is nothing to report and the cell verbs say so rather than inventing a blank screen.</para>
///
/// <para><b>Methods.</b> <c>ping</c> (core), <c>screen</c> — every row as text, <c>row</c> — one row,
/// <c>cell</c> — glyph plus pen at a position, <c>appState</c> — the host's own snapshot, <c>key</c>,
/// <c>click</c>, <c>drag</c>, <c>size</c>, <c>inputLog</c> — the last N events with the state they changed.</para>
///
/// <para>DEBUG only, loopback only.</para>
/// </summary>
public sealed class ConsoleDebugInspector : IDebugInspectorHost, IDisposable
{
    private readonly IInspectableTerminal _terminal;
    private readonly DebugInspectorCore? _core;
    private readonly Func<string>? _appState;

    /// <summary>The input trace, so a driver can see what the app actually received and what it changed.
    /// This is the diagnostic that found the mouse-motion-as-click bug, promoted from a stderr print.</summary>
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _inputLog = new();
    private const int InputLogLimit = 64;

    public string AppName { get; }

    /// <summary>A character-cell terminal. See <see cref="IDebugInspectorHost.SurfaceKind"/> for the
    /// vocabulary and for why this matters — a sidecar filters on it, and the default of "unknown" makes an
    /// instance invisible.</summary>
    public string SurfaceKind => "tui";

    /// <summary>The command server's port; -1 when <see cref="Detached"/>.</summary>
    public int Port => _core?.Port ?? -1;

    /// <param name="appName">Names the app in the banner.</param>
    /// <param name="terminal">The live terminal — injected input goes into its queue, and its
    /// <see cref="VirtualTerminal.CellBuffer"/> is the screen this reports.</param>
    /// <param name="appState">Optional: a JSON object describing whatever the app considers its state. This
    /// is the highest-value verb by a distance — a snapshot naming the selected square, the side to move
    /// and the mode turns "the piece selected itself" from a manual hunt into one request.</param>
    private ConsoleDebugInspector(
        string appName, IInspectableTerminal terminal, Func<string>? appState, bool withTransport)
    {
        AppName = appName;
        _terminal = terminal;
        _appState = appState;
        _core = withTransport ? DebugInspectorCore.Start(this) : null;
    }

    /// <summary>Starts the inspector and its command server. The caller must call <see cref="Pump"/> from
    /// its loop, or no command will ever run.</summary>
    public static ConsoleDebugInspector Attach(string appName, IInspectableTerminal terminal, Func<string>? appState = null)
        => new(appName, terminal, appState, withTransport: true);

    /// <summary>
    /// The same verbs with NO transport: no TCP listener, no multicast bind. <see cref="Invoke"/> is called
    /// directly.
    /// <para>
    /// This is what tests should use. The method table is ordinary logic — what a row of cells reads as, how
    /// a key name maps, what a click's pixel centre is — and asserting it through a socket makes those tests
    /// slow, order-dependent on port availability, and reliant on joining a multicast group, none of which
    /// has anything to do with what is being checked. Wire behaviour deserves its own small functional test
    /// rather than a tax on every other one.
    /// </para>
    /// </summary>
    public static ConsoleDebugInspector Detached(string appName, IInspectableTerminal terminal, Func<string>? appState = null)
        => new(appName, terminal, appState, withTransport: false);

    /// <summary>Runs queued commands on the calling thread. Call once per loop iteration. No-op when
    /// <see cref="Detached"/>, since nothing can enqueue.</summary>
    public void Pump() => _core?.Pump();

    /// <summary>A pull loop already ticks on its own, so there is nothing to wake.</summary>
    public void Poke() { }

    /// <summary>
    /// Records one input event and what it did. Called by the app's input dispatch, which is the only place
    /// that sees the before and after.
    /// </summary>
    public void LogInput(string description)
    {
        _inputLog.Enqueue(description);
        while (_inputLog.Count > InputLogLimit && _inputLog.TryDequeue(out _)) { }
    }

    public string? Invoke(string method, JsonElement p) => method switch
    {
        "size" => Size(),
        "screen" => Screen(),
        "row" => Row(Int(p, "row", 0)),
        "cell" => CellAt(Int(p, "column", 0), Int(p, "row", 0)),
        "appState" => _appState is null ? "null" : _appState(),
        "inputLog" => InputLog(),
        "key" => Key(p),
        "click" => Click(p),
        "drag" => Drag(p),
        "press" => Press(p),
        "move" => Move(p),
        "release" => Release(p),
        _ => null,
    };

    private static int Int(JsonElement p, string name, int fallback)
        => p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var v) && v.TryGetInt32(out var i)
            ? i : fallback;

    private static string Str(JsonElement p, string name)
        => p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var v) ? v.GetString() ?? "" : "";

    private string Size()
    {
        var (w, h) = _terminal.Size;
        var cell = _terminal.CellSize;
        return $"{{\"columns\":{w},\"rows\":{h},\"cellWidth\":{cell.Width},\"cellHeight\":{cell.Height}," +
               $"\"buffered\":{(_terminal.CellBuffer is not null).ToString().ToLowerInvariant()}}}";
    }

    /// <summary>Every row of the front buffer as a JSON string array — the screen, in words.</summary>
    private string Screen()
    {
        if (_terminal.CellBuffer is not { } buffer)
        {
            return Unbuffered();
        }

        var sb = new StringBuilder("{\"rows\":[");
        for (var r = 0; r < buffer.Height; r++)
        {
            if (r > 0) sb.Append(',');
            sb.Append(DebugInspectorCore.Quote(buffer.FrontRowText(r)));
        }
        return sb.Append("]}").ToString();
    }

    private string Row(int row)
        => _terminal.CellBuffer is { } buffer
            ? $"{{\"row\":{row},\"text\":{DebugInspectorCore.Quote(buffer.FrontRowText(row))}}}"
            : Unbuffered();

    private string CellAt(int column, int row)
    {
        if (_terminal.CellBuffer is not { } buffer) return Unbuffered();

        var cell = buffer.FrontAt(column, row);

        // "link" is reported only when there is one, so an ordinary cell's shape is unchanged and an agent
        // asking "is this path clickable, and where does it point" gets an answer rather than having to
        // infer it from escapes it cannot see.
        var link = cell.Link is null ? "" : $",\"link\":{DebugInspectorCore.Quote(cell.Link)}";
        return $"{{\"column\":{column},\"row\":{row}," +
               $"\"glyph\":{DebugInspectorCore.Quote(cell.Glyph == '\0' ? " " : cell.Glyph.ToString())}," +
               $"\"kind\":\"{cell.Kind}\",\"reverse\":{cell.Reverse.ToString().ToLowerInvariant()}," +
               $"\"fg\":\"{Hex(cell.Style.Foreground)}\",\"bg\":\"{Hex(cell.Style.Background)}\"{link}}}";
    }

    private static string Hex(RGBAColor32 c) => $"#{c.Red:X2}{c.Green:X2}{c.Blue:X2}";

    /// <summary>Says why there is nothing to report, rather than returning a blank screen that looks real.</summary>
    private static string Unbuffered()
        => "{\"error\":\"this terminal is not running buffered — call VirtualTerminal.EnableCellBuffer()\"}";

    private string InputLog()
    {
        var sb = new StringBuilder("{\"events\":[");
        var first = true;
        foreach (var entry in _inputLog)
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append(DebugInspectorCore.Quote(entry));
        }
        return sb.Append("]}").ToString();
    }

    /// <summary>
    /// Injects a keystroke. Accepts a <see cref="ConsoleKey"/> name ("Escape", "F1", "A", "D4") or a single
    /// character, which is what a board move needs — GameUI reads files as letters and ranks as digits, so
    /// e2e4 is <c>e,2,e,4</c>. An optional <c>mods</c> string carries the chord.
    /// </summary>
    /// <remarks>
    /// A chord injected here is byte-for-byte what the real parser produces: a terminal sends Ctrl+letter as
    /// a single control byte, which <see cref="VirtualTerminal"/> decodes back to
    /// <c>(ConsoleKey.A + n, ConsoleModifiers.Control)</c> — the same pair built here. So driving Ctrl+F
    /// exercises the app's real binding rather than an inspector-only path.
    /// </remarks>
    private string Key(JsonElement p)
    {
        var key = Str(p, "key");
        if (key.Length == 0) return "{\"ok\":false,\"reason\":\"no key\"}";

        if (!TryMapKey(key, out var consoleKey))
        {
            return $"{{\"ok\":false,\"reason\":\"unknown key '{key}'\"}}";
        }

        var raw = Str(p, "mods");
        if (!TryMapModifiers(raw, out var modifiers))
        {
            return $"{{\"ok\":false,\"reason\":\"unknown modifiers '{raw}'\"}}";
        }

        _terminal.Inject(new ConsoleInputEvent(null, consoleKey, modifiers));
        // The modifiers are echoed because an unheeded chord is otherwise INVISIBLE: a dropped Ctrl turns
        // Ctrl+F into bare `f`, which in chess is not a no-op but a different valid action (the file-f
        // selector). A driver can assert on this instead of inferring from the app's state.
        return $"{{\"ok\":true,\"key\":\"{consoleKey}\",\"mods\":\"{modifiers}\"}}";
    }

    /// <summary>
    /// Maps a driver's modifier string to <see cref="ConsoleModifiers"/>. Substring-matched and
    /// case-insensitive, so "Ctrl", "ctrl+shift", "CtrlShift" and "control-alt" all work — the same spelling
    /// the SDL inspector accepts, so one convention covers both.
    /// </summary>
    /// <remarks>
    /// <b>Unrecognised text is an ERROR, not <see cref="ConsoleModifiers"/> zero.</b> Silently dropping it
    /// would deliver a BARE key, and a bare key is frequently a different valid binding rather than a
    /// no-op — so the failure would look like the app ignoring a correct chord. This is stricter than the
    /// SDL inspector's equivalent, which resolves the unknown to None.
    /// </remarks>
    private static bool TryMapModifiers(string? raw, out ConsoleModifiers modifiers)
    {
        modifiers = 0;
        if (string.IsNullOrWhiteSpace(raw)) return true;

        var s = raw.ToLowerInvariant();
        if (s is "none" or "0") return true;

        if (s.Contains("ctrl") || s.Contains("control")) modifiers |= ConsoleModifiers.Control;
        if (s.Contains("shift")) modifiers |= ConsoleModifiers.Shift;
        if (s.Contains("alt") || s.Contains("option")) modifiers |= ConsoleModifiers.Alt;

        return modifiers != 0;
    }

    /// <summary>
    /// Maps a driver's key name to a <see cref="ConsoleKey"/>.
    ///
    /// <para><b>Single characters are resolved FIRST, deliberately.</b> <c>Enum.TryParse&lt;ConsoleKey&gt;</c>
    /// accepts a NUMERIC string as a raw underlying value, so <c>"4"</c> parses to <c>(ConsoleKey)4</c> —
    /// which is not <see cref="ConsoleKey.D4"/> and is not any named key at all. Delegating to TryParse first
    /// therefore silently mis-delivers every digit, and digits are half of a typed board move.</para>
    /// </summary>
    private static bool TryMapKey(string key, out ConsoleKey consoleKey)
    {
        if (key.Length == 1)
        {
            var ch = char.ToUpperInvariant(key[0]);
            consoleKey = ch switch
            {
                >= 'A' and <= 'Z' => ConsoleKey.A + (ch - 'A'),
                >= '0' and <= '9' => ConsoleKey.D0 + (ch - '0'),
                ' ' => ConsoleKey.Spacebar,
                _ => ConsoleKey.None,
            };
            return consoleKey != ConsoleKey.None;
        }

        // Names a driver would reasonably use that ConsoleKey spells differently.
        consoleKey = key.ToLowerInvariant() switch
        {
            "esc" => ConsoleKey.Escape,
            "up" or "arrowup" => ConsoleKey.UpArrow,
            "down" or "arrowdown" => ConsoleKey.DownArrow,
            "left" or "arrowleft" => ConsoleKey.LeftArrow,
            "right" or "arrowright" => ConsoleKey.RightArrow,
            "return" or "cr" => ConsoleKey.Enter,
            "space" or "spc" => ConsoleKey.Spacebar,
            "pgup" => ConsoleKey.PageUp,
            "pgdn" or "pgdown" => ConsoleKey.PageDown,
            "del" => ConsoleKey.Delete,
            "bksp" or "bs" => ConsoleKey.Backspace,
            _ => ConsoleKey.None,
        };
        if (consoleKey != ConsoleKey.None) return true;

        // Only now the enum's own names — and never a bare number, for the reason in the remarks.
        return !ulong.TryParse(key, out _)
            && Enum.TryParse(key, ignoreCase: true, out consoleKey)
            && consoleKey != ConsoleKey.None;
    }

    /// <summary>
    /// Injects a click at a CELL position, converted to the pixel coordinates the app's hit-testing wants.
    /// Cells rather than pixels because that is what a driver can compute from <c>screen</c>: the thing it
    /// just read the text of is at a column and a row.
    /// </summary>
    private string Click(JsonElement p)
    {
        var column = Int(p, "column", 0);
        var row = Int(p, "row", 0);
        var (x, y) = CellCentre(column, row);

        _terminal.Inject(new ConsoleInputEvent(new MouseEvent(0, x, y, IsRelease: false), ConsoleKey.None, 0));
        _terminal.Inject(new ConsoleInputEvent(new MouseEvent(0, x, y, IsRelease: true), ConsoleKey.None, 0));

        return $"{{\"ok\":true,\"x\":{x},\"y\":{y}}}";
    }

    /// <summary>
    /// Injects a DRAG: a press at the first cell, motion while the button is held, then a release at the
    /// second. Addressed in cells for the same reason <see cref="Click"/> is.
    /// </summary>
    /// <remarks>
    /// <para>Motion is emitted once per CELL CROSSED rather than once per interpolation step, because that
    /// is what a terminal does: mode 1002 reports a position when it CHANGES, and its resolution is a cell.
    /// So asking for more steps than the path has cells yields the cells, not repeats -- a duplicate report
    /// is an event no terminal emits, and a consumer that coalesces motion would swallow the difference
    /// until something downstream counted them.</para>
    /// <para>There is deliberately NO bare <c>move</c> verb here, though the SDL/Vulkan inspector has one.
    /// Mode 1002 is BUTTON-motion tracking: a terminal reports movement only while a button is held, so
    /// hover is not something a terminal app can receive. A synthetic hover would let behaviour pass a test
    /// on input that cannot reach it in production.</para>
    /// </remarks>
    private string Drag(JsonElement p)
    {
        var column1 = Int(p, "column1", 0);
        var row1 = Int(p, "row1", 0);
        var column2 = Int(p, "column2", 0);
        var row2 = Int(p, "row2", 0);

        InjectPress(column1, row1);
        var motion = InjectMotionTo(column2, row2, Steps(p, column1, row1, column2, row2));
        var (endX, endY) = InjectRelease(column2, row2);

        return $"{{\"ok\":true,\"motion\":{motion},\"x\":{endX},\"y\":{endY}}}";
    }

    /// <summary>
    /// The halves of a drag as SEPARATE calls, so a driver can look at the app BETWEEN them.
    /// </summary>
    /// <remarks>
    /// <para><see cref="Drag"/> cannot answer "does the dragged thing follow the pointer", and that is not
    /// a gap in this inspector -- it is the app being right. A consumer that coalesces motion (drop the
    /// render when another event is already queued, carry its damage forward) sees a whole injected drag
    /// sitting in the queue at once and correctly renders NONE of the intermediate positions. The gesture
    /// completes, every report arrives, and nothing mid-drag is ever painted. Only one event in flight at a
    /// time reproduces a human drag closely enough to observe it.</para>
    /// </remarks>
    private string Press(JsonElement p)
    {
        var (x, y) = InjectPress(Int(p, "column", 0), Int(p, "row", 0));
        return $"{{\"ok\":true,\"x\":{x},\"y\":{y}}}";
    }

    /// <summary>Motion to a cell, reported once per cell crossed from wherever the button went down.</summary>
    /// <remarks>
    /// Refused when no button is held, rather than sent anyway. Mode 1002 is BUTTON-motion tracking: a
    /// terminal reports movement only during a drag, so a hover report is an event no terminal emits.
    /// Injecting one would let hover-driven behaviour pass a test through a door that is nailed shut in
    /// production -- the failure this refusal prevents is a GREEN test, not a red one.
    /// </remarks>
    private string Move(JsonElement p)
    {
        if (_held is not { } held)
        {
            throw new InvalidOperationException(
                "no button is held, and a terminal reports motion only while one is (mode 1002 is " +
                "button-motion tracking, not any-event tracking). Send 'press' first.");
        }

        var column = Int(p, "column", held.Column);
        var row = Int(p, "row", held.Row);
        var motion = InjectMotionTo(column, row, Steps(p, held.Column, held.Row, column, row));
        var (x, y) = CellCentre(column, row);
        return $"{{\"ok\":true,\"motion\":{motion},\"x\":{x},\"y\":{y}}}";
    }

    /// <summary>Releases the held button, at the cell it is on unless another is named.</summary>
    private string Release(JsonElement p)
    {
        if (_held is not { } held)
        {
            throw new InvalidOperationException("no button is held; send 'press' first.");
        }

        var (x, y) = InjectRelease(Int(p, "column", held.Column), Int(p, "row", held.Row));
        return $"{{\"ok\":true,\"x\":{x},\"y\":{y}}}";
    }

    /// <summary>
    /// One step per cell of the longer axis already visits every cell on the path; a caller can ask for more
    /// to walk a diagonal more finely, but never for fewer events than the path crosses cells.
    /// </summary>
    private static int Steps(JsonElement p, int column1, int row1, int column2, int row2)
    {
        var span = Math.Max(Math.Abs(column2 - column1), Math.Abs(row2 - row1));
        return Math.Clamp(Int(p, "steps", Math.Max(span, 1)), 1, 64);
    }

    private (int X, int Y) InjectPress(int column, int row)
    {
        var (x, y) = CellCentre(column, row);
        _terminal.Inject(new ConsoleInputEvent(
            new MouseEvent(0, x, y, IsRelease: false), ConsoleKey.None, 0));
        _held = (column, row);
        return (x, y);
    }

    /// <summary>
    /// Walks to the target cell, emitting one report per cell ENTERED, and returns how many there were.
    /// </summary>
    private int InjectMotionTo(int column, int row, int steps)
    {
        var (fromColumn, fromRow) = _held ?? (column, row);
        var (lastColumn, lastRow) = (fromColumn, fromRow);
        var motion = 0;

        for (var i = 1; i <= steps; i++)
        {
            var t = (double)i / steps;
            var stepColumn = (int)Math.Round(fromColumn + (column - fromColumn) * t);
            var stepRow = (int)Math.Round(fromRow + (row - fromRow) * t);
            if (stepColumn == lastColumn && stepRow == lastRow) continue;

            var (x, y) = CellCentre(stepColumn, stepRow);
            _terminal.Inject(new ConsoleInputEvent(
                new MouseEvent(0, x, y, IsRelease: false) { IsMotion = true }, ConsoleKey.None, 0));
            (lastColumn, lastRow) = (stepColumn, stepRow);
            motion++;
        }

        if (_held is not null) _held = (lastColumn, lastRow);
        return motion;
    }

    private (int X, int Y) InjectRelease(int column, int row)
    {
        var (x, y) = CellCentre(column, row);
        _terminal.Inject(new ConsoleInputEvent(
            new MouseEvent(0, x, y, IsRelease: true), ConsoleKey.None, 0));
        _held = null;
        return (x, y);
    }

    /// <summary>Centre of a cell, so a rounding difference cannot land the pointer on a neighbour.</summary>
    private (int X, int Y) CellCentre(int column, int row)
    {
        var cell = _terminal.CellSize;
        return (column * cell.Width + cell.Width / 2, row * cell.Height + cell.Height / 2);
    }

    /// <summary>
    /// The cell the held button is on, or null when nothing is pressed. This is a POINTER cursor, not a
    /// gesture state machine: it exists so <c>move</c> knows where it is moving FROM and can refuse to
    /// report motion that no terminal would send.
    /// </summary>
    private (int Column, int Row)? _held;

    public void Dispose() => _core?.Dispose();
}
#endif
