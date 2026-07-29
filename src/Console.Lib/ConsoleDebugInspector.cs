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
/// <c>click</c>, <c>size</c>, <c>inputLog</c> — the last N events with the state they changed.</para>
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

    /// <summary>A terminal. See <see cref="IDebugInspectorHost.SurfaceKind"/> for why this matters — a
    /// sidecar filters on it, and the default of "unknown" makes an instance invisible.</summary>
    public string SurfaceKind => "console";

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
        return $"{{\"column\":{column},\"row\":{row}," +
               $"\"glyph\":{DebugInspectorCore.Quote(cell.Glyph == '\0' ? " " : cell.Glyph.ToString())}," +
               $"\"kind\":\"{cell.Kind}\",\"reverse\":{cell.Reverse.ToString().ToLowerInvariant()}," +
               $"\"fg\":\"{Hex(cell.Style.Foreground)}\",\"bg\":\"{Hex(cell.Style.Background)}\"}}";
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
    /// e2e4 is <c>e,2,e,4</c>.
    /// </summary>
    private string Key(JsonElement p)
    {
        var key = Str(p, "key");
        if (key.Length == 0) return "{\"ok\":false,\"reason\":\"no key\"}";

        if (!TryMapKey(key, out var consoleKey))
        {
            return $"{{\"ok\":false,\"reason\":\"unknown key '{key}'\"}}";
        }

        _terminal.Inject(new ConsoleInputEvent(null, consoleKey, 0));
        return $"{{\"ok\":true,\"key\":\"{consoleKey}\"}}";
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
        var cell = _terminal.CellSize;

        // Centre of the cell, so a rounding difference cannot land on a neighbour.
        var x = column * cell.Width + cell.Width / 2;
        var y = row * cell.Height + cell.Height / 2;

        _terminal.Inject(new ConsoleInputEvent(new MouseEvent(0, x, y, IsRelease: false), ConsoleKey.None, 0));
        _terminal.Inject(new ConsoleInputEvent(new MouseEvent(0, x, y, IsRelease: true), ConsoleKey.None, 0));

        return $"{{\"ok\":true,\"x\":{x},\"y\":{y}}}";
    }

    public void Dispose() => _core?.Dispose();
}
#endif
