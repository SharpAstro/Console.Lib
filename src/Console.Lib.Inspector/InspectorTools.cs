using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;

namespace Console.Lib.Inspector;

/// <summary>
/// MCP tools bridging an agent to a running Debug-build Console.Lib terminal app.
///
/// <para>The distinctive one is <see cref="screen"/>: a terminal can be read as TEXT, so an agent asserts in
/// words ("the status bar reads <c>White to move.</c>") instead of squinting at a screenshot. It reports the
/// FRONT cell buffer — what was actually emitted — so it cannot drift from what is on screen.</para>
/// </summary>
[McpServerToolType]
public sealed class InspectorTools
{
    [McpServerTool, Description("Discover running Debug-build Console.Lib terminal apps. Returns one line per instance: pid, app, address:port. Call this first; every other tool takes an optional instance pid.")]
    public static async Task<string> list_instances(InspectorDiscoveryClient discovery, CancellationToken ct = default)
    {
        var all = await discovery.DiscoverAsync(ct);
        if (all.Count == 0)
        {
            return "No debuggable terminal apps found. The app must be a DEBUG build with the inspector attached "
                 + "(for chess: CHESS_INSPECTOR=1 and `dotnet run -c Debug`). The inspector is compiled out of Release entirely.";
        }

        var sb = new StringBuilder();
        foreach (var i in all)
        {
            sb.AppendLine($"pid={i.Pid}  app={i.App}  {i.Address}:{i.TcpPort}  proto={i.Proto}");
        }
        return sb.ToString();
    }

    [McpServerTool, Description("Confirm the inspector is alive and report the protocol version.")]
    public static async Task<string> ping(InspectorDiscoveryClient d, InspectorSocketClient s,
        [Description("Target instance pid (0 = the only running instance).")] int instance = 0,
        CancellationToken ct = default)
        => (await s.SendAsync(await Resolve(d, instance, ct), "ping", null, ct)).GetRawText();

    [McpServerTool, Description("The terminal's grid size, cell size in pixels, and whether it is running buffered (buffered is required for screen/row/cell).")]
    public static async Task<string> size(InspectorDiscoveryClient d, InspectorSocketClient s,
        [Description("Target instance pid (0 = the only running instance).")] int instance = 0,
        CancellationToken ct = default)
        => (await s.SendAsync(await Resolve(d, instance, ct), "size", null, ct)).GetRawText();

    [McpServerTool, Description(
        "The whole screen as text, one string per row - what the terminal actually emitted. Use this to assert "
        + "on visible text. NOTE: only CELLS appear here. A Sixel image (e.g. a chess board) occupies cells that "
        + "read as blank, and `cell` reports kind=Image for them; that is correct, not a fault.")]
    public static async Task<string> screen(InspectorDiscoveryClient d, InspectorSocketClient s,
        [Description("Target instance pid (0 = the only running instance).")] int instance = 0,
        [Description("Prefix each row with its index, which makes rows easy to refer to in follow-up calls.")] bool numbered = true,
        CancellationToken ct = default)
    {
        var result = await s.SendAsync(await Resolve(d, instance, ct), "screen", null, ct);
        if (result.TryGetProperty("error", out var err)) return err.GetString() ?? "error";
        if (!numbered) return result.GetRawText();

        var sb = new StringBuilder();
        var i = 0;
        foreach (var row in result.GetProperty("rows").EnumerateArray())
        {
            sb.AppendLine($"{i++,3}|{row.GetString()}|");
        }
        return sb.ToString();
    }

    [McpServerTool, Description("One row of the screen as text.")]
    public static async Task<string> row(InspectorDiscoveryClient d, InspectorSocketClient s,
        [Description("Row index, 0-based from the top.")] int row,
        [Description("Target instance pid (0 = the only running instance).")] int instance = 0,
        CancellationToken ct = default)
        => (await s.SendAsync(await Resolve(d, instance, ct), "row", $"{{\"row\":{row}}}", ct)).GetRawText();

    [McpServerTool, Description("One cell: its glyph, its foreground/background colours, and its kind (Text, Opaque, or Image - Image meaning a Sixel picture owns those pixels).")]
    public static async Task<string> cell(InspectorDiscoveryClient d, InspectorSocketClient s,
        [Description("Column, 0-based.")] int column,
        [Description("Row, 0-based.")] int row,
        [Description("Target instance pid (0 = the only running instance).")] int instance = 0,
        CancellationToken ct = default)
        => (await s.SendAsync(await Resolve(d, instance, ct), "cell",
            $"{{\"column\":{column},\"row\":{row}}}", ct)).GetRawText();

    [McpServerTool, Description(
        "The app's own state snapshot. Usually the fastest way to understand what the app thinks is happening - "
        + "for chess: the selected square, whose move it is, the ply count and the UI mode.")]
    public static async Task<string> app_state(InspectorDiscoveryClient d, InspectorSocketClient s,
        [Description("Target instance pid (0 = the only running instance).")] int instance = 0,
        CancellationToken ct = default)
        => (await s.SendAsync(await Resolve(d, instance, ct), "appState", null, ct)).GetRawText();

    [McpServerTool, Description(
        "The last input events the app received, each with the state it changed. This is the fastest route to any "
        + "input bug: it shows what actually arrived, not what you meant to send.")]
    public static async Task<string> input_log(InspectorDiscoveryClient d, InspectorSocketClient s,
        [Description("Target instance pid (0 = the only running instance).")] int instance = 0,
        CancellationToken ct = default)
    {
        var result = await s.SendAsync(await Resolve(d, instance, ct), "inputLog", null, ct);
        var sb = new StringBuilder();
        foreach (var e in result.GetProperty("events").EnumerateArray())
        {
            sb.AppendLine(e.GetString());
        }
        return sb.Length == 0 ? "(no input recorded yet)" : sb.ToString();
    }

    [McpServerTool, Description(
        "Inject a keystroke. Accepts a key name (Escape, Enter, F1, Up, PageDown, Tab) or a single character, "
        + "plus an optional chord via `mods`. For chess a board move is FOUR keys - file letter then rank "
        + "digit, twice: e2e4 is e, 2, e, 4. NOTE some bindings are chords whose bare key does something "
        + "ELSE: chess flips the board on Ctrl+F, while bare `f` selects file f.")]
    public static async Task<string> key(InspectorDiscoveryClient d, InspectorSocketClient s,
        [Description("Key name or single character.")] string key,
        [Description("Modifiers, e.g. \"Ctrl\", \"Ctrl+Shift\", \"Alt\". Omit for a bare key. Unrecognised text is refused rather than silently sending the bare key.")] string? mods = null,
        [Description("Target instance pid (0 = the only running instance).")] int instance = 0,
        CancellationToken ct = default)
        => (await s.SendAsync(await Resolve(d, instance, ct), "key",
            Json.Obj(("key", key), ("mods", mods)), ct)).GetRawText();

    [McpServerTool, Description("Inject a sequence of keystrokes in order - the convenient form of `key` for typing a move or walking a menu.")]
    public static async Task<string> keys(InspectorDiscoveryClient d, InspectorSocketClient s,
        [Description("Keys in order, e.g. [\"e\",\"2\",\"e\",\"4\"].")] string[] keys,
        [Description("Target instance pid (0 = the only running instance).")] int instance = 0,
        CancellationToken ct = default)
    {
        var target = await Resolve(d, instance, ct);
        var sb = new StringBuilder();
        foreach (var k in keys)
        {
            var r = await s.SendAsync(target, "key", $"{{\"key\":{Json.Quote(k)}}}", ct);
            sb.AppendLine($"{k}: {r.GetRawText()}");
        }
        return sb.ToString();
    }

    [McpServerTool, Description("Click at a CELL position (a press and a release at the cell's centre). Addressed in cells because that is what `screen` gives you coordinates in.")]
    public static async Task<string> click(InspectorDiscoveryClient d, InspectorSocketClient s,
        [Description("Column, 0-based.")] int column,
        [Description("Row, 0-based.")] int row,
        [Description("Target instance pid (0 = the only running instance).")] int instance = 0,
        CancellationToken ct = default)
        => (await s.SendAsync(await Resolve(d, instance, ct), "click",
            $"{{\"column\":{column},\"row\":{row}}}", ct)).GetRawText();

    /// <summary>
    /// Picks the target instance. Zero means "the only one", which is the normal case; it fails loudly when
    /// several are running rather than guessing, because driving the wrong app looks like the right app
    /// misbehaving.
    /// </summary>
    private static async Task<InspectorInstance> Resolve(
        InspectorDiscoveryClient discovery, int pid, CancellationToken ct)
    {
        var all = await discovery.DiscoverAsync(ct);
        if (all.Count == 0)
        {
            throw new InvalidOperationException(
                "No debuggable terminal app found. It must be a DEBUG build with the inspector attached.");
        }

        if (pid != 0)
        {
            return all.FirstOrDefault(i => i.Pid == pid)
                ?? throw new InvalidOperationException(
                    $"No instance with pid {pid}. Running: {string.Join(", ", all.Select(i => i.Pid))}.");
        }

        return all.Count == 1
            ? all[0]
            : throw new InvalidOperationException(
                $"{all.Count} instances are running; pass one of these pids: {string.Join(", ", all.Select(i => i.Pid))}.");
    }
}
