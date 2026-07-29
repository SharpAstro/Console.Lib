#if DEBUG
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using DIR.Lib;
using Shouldly;
using Xunit;

namespace Console.Lib.Tests;

/// <summary>
/// The console inspector's verbs, called DIRECTLY — no TCP listener, no multicast bind.
///
/// <para>These assertions are about ordinary logic: what a row of cells reads back as, how a key name maps to
/// a <see cref="ConsoleKey"/>, where a click's pixel centre lands. Driving that through a socket would make
/// every one of them depend on port availability and on joining a multicast group, which has nothing to do
/// with what is being checked. The transport gets ONE functional test, at the bottom, marked as such.</para>
/// </summary>
public sealed class ConsoleDebugInspectorTests
{
    /// <summary>A screen and an input queue, with no console behind them.</summary>
    private sealed class FakeScreen : IInspectableTerminal
    {
        public CellBuffer? CellBuffer { get; }
        public (int Width, int Height) Size { get; }
        public TermCell CellSize { get; } = new(10, 20);
        public readonly ConcurrentQueue<ConsoleInputEvent> Injected = new();

        public FakeScreen(int columns, int rows, bool buffered = true)
        {
            Size = (columns, rows);
            if (!buffered) return;
            CellBuffer = new CellBuffer { ColorMode = ColorMode.TrueColor };
            CellBuffer.Resize(columns, rows);
        }

        public void Inject(ConsoleInputEvent evt) => Injected.Enqueue(evt);
    }

    private sealed class NullSink : ICellSink
    {
        public void MoveTo(int column, int row) { }
        public void SetPen(VtStyle style, bool reverse) { }
        public void Write(ReadOnlySpan<char> run) { }
    }

    private static readonly JsonElement NoParams = JsonDocument.Parse("{}").RootElement.Clone();

    private static JsonElement Params(string json) => JsonDocument.Parse(json).RootElement.Clone();

    /// <summary>Calls a verb and parses its result, exactly as the transport would.</summary>
    private static JsonElement Call(ConsoleDebugInspector inspector, string method, string? paramsJson = null)
    {
        var raw = inspector.Invoke(method, paramsJson is null ? NoParams : Params(paramsJson));
        raw.ShouldNotBeNull($"'{method}' should be a known verb");
        return JsonDocument.Parse(raw).RootElement.Clone();
    }

    private static (ConsoleDebugInspector Inspector, FakeScreen Screen) Detached(
        FakeScreen? screen = null, Func<string>? appState = null)
    {
        screen ??= new FakeScreen(20, 4);
        return (ConsoleDebugInspector.Detached("Test", screen, appState), screen);
    }

    private static void Paint(FakeScreen screen, int column, int row, string text, VtStyle? style = null)
    {
        var pen = style ?? new VtStyle(new RGBAColor32(0xFF, 0xCE, 0x9E, 0xff), new RGBAColor32(0, 0, 0, 0xff));
        screen.CellBuffer!.MoveTo(column, row);
        screen.CellBuffer.Write($"{pen.Apply(ColorMode.TrueColor)}{text}");
        screen.CellBuffer.Flush(new NullSink());
    }

    [Fact]
    public void AnUnknownVerb_IsReportedAsUnknown()
    {
        var (inspector, _) = Detached();

        inspector.Invoke("noSuchThing", NoParams).ShouldBeNull(
            "null is what the core turns into an error reply");
    }

    /// <summary>
    /// The cell plane: the screen readable as WORDS. This is the thing a pixel inspector cannot do, and the
    /// reason the console inspector is worth having rather than merely having parity.
    /// </summary>
    [Fact]
    public void Screen_ReadsTheTerminalBackAsText()
    {
        var (inspector, screen) = Detached(new FakeScreen(20, 3));
        Paint(screen, 0, 1, "White to move.".PadRight(20));

        var rows = Call(inspector, "screen").GetProperty("rows");

        rows.GetArrayLength().ShouldBe(3);
        rows[1].GetString().ShouldBe("White to move.      ");
        Call(inspector, "row", "{\"row\":1}").GetProperty("text").GetString().ShouldBe("White to move.      ");
    }

    [Fact]
    public void Cell_ReportsTheGlyphAndThePen()
    {
        var (inspector, screen) = Detached(new FakeScreen(8, 2));
        Paint(screen, 2, 0, "Q",
            new VtStyle(new RGBAColor32(0x8A, 0x4F, 0xD0, 0xff), new RGBAColor32(0x20, 0x20, 0x34, 0xff)));

        var cell = Call(inspector, "cell", "{\"column\":2,\"row\":0}");

        cell.GetProperty("glyph").GetString().ShouldBe("Q");
        cell.GetProperty("fg").GetString().ShouldBe("#8A4FD0");
        cell.GetProperty("bg").GetString().ShouldBe("#202034");
        cell.GetProperty("kind").GetString().ShouldBe("Text");
    }

    /// <summary>An unbuffered terminal has no screen to report, and must say so rather than answering with a
    /// blank one a driver would happily assert against.</summary>
    [Fact]
    public void AnUnbufferedTerminal_SaysWhyItHasNoScreen()
    {
        var (inspector, _) = Detached(new FakeScreen(10, 2, buffered: false));

        Call(inspector, "screen").GetProperty("error").GetString().ShouldContain("EnableCellBuffer");
    }

    [Fact]
    public void Size_ReportsTheGridAndWhetherItIsBuffered()
    {
        var (inspector, _) = Detached(new FakeScreen(108, 30));

        var size = Call(inspector, "size");

        size.GetProperty("columns").GetInt32().ShouldBe(108);
        size.GetProperty("rows").GetInt32().ShouldBe(30);
        size.GetProperty("cellHeight").GetInt32().ShouldBe(20);
        size.GetProperty("buffered").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public void Key_MapsNamesAndAliases()
    {
        var (inspector, screen) = Detached();

        foreach (var name in new[] { "Escape", "esc", "F1", "up", "pgdn", "Enter", "return" })
        {
            Call(inspector, "key", $"{{\"key\":\"{name}\"}}").GetProperty("ok").GetBoolean()
                .ShouldBeTrue($"'{name}' should map");
        }

        Drain(screen).ShouldBe([
            ConsoleKey.Escape, ConsoleKey.Escape, ConsoleKey.F1,
            ConsoleKey.UpArrow, ConsoleKey.PageDown, ConsoleKey.Enter, ConsoleKey.Enter]);
    }

    /// <summary>
    /// Digits are the half of this that broke. <c>Enum.TryParse&lt;ConsoleKey&gt;</c> accepts a NUMERIC string
    /// as a raw underlying value, so <c>"4"</c> parsed to <c>(ConsoleKey)4</c> — not <c>D4</c>, not any named
    /// key. A board move is a file letter then a rank digit, twice, so that mis-delivered every other
    /// keystroke of every move.
    /// </summary>
    [Fact]
    public void Key_AcceptsBareLettersAndDigits_WhichIsHowAMoveIsTyped()
    {
        var (inspector, screen) = Detached();

        foreach (var k in new[] { "e", "2", "e", "4" })
        {
            Call(inspector, "key", $"{{\"key\":\"{k}\"}}").GetProperty("ok").GetBoolean().ShouldBeTrue();
        }

        Drain(screen).ShouldBe([ConsoleKey.E, ConsoleKey.D2, ConsoleKey.E, ConsoleKey.D4]);
    }

    [Fact]
    public void Key_RefusesARawEnumNumber()
    {
        var (inspector, screen) = Detached();

        Call(inspector, "key", "{\"key\":\"27\"}").GetProperty("ok").GetBoolean().ShouldBeFalse(
            "a driver sends names and characters, never underlying values");
        screen.Injected.ShouldBeEmpty();
    }

    /// <summary>
    /// A click is addressed in CELLS and converted to the pixel coordinates hit-testing wants, because cells
    /// are what a driver can compute — it just read the text at a column and a row. It lands on the cell
    /// CENTRE so a rounding difference cannot pick a neighbour.
    /// </summary>
    [Fact]
    public void Click_InjectsAPressAndReleaseAtTheCellCentre()
    {
        var (inspector, screen) = Detached();

        Call(inspector, "click", "{\"column\":3,\"row\":2}");

        var events = new List<ConsoleInputEvent>();
        while (screen.Injected.TryDequeue(out var evt)) events.Add(evt);

        events.Count.ShouldBe(2, "a click is a press AND a release");
        events[0].Mouse!.Value.X.ShouldBe(3 * 10 + 5);
        events[0].Mouse!.Value.Y.ShouldBe(2 * 20 + 10);
        events[0].Mouse!.Value.IsRelease.ShouldBeFalse();
        events[1].Mouse!.Value.IsRelease.ShouldBeTrue();
        events.ShouldAllBe(e => e.Mouse!.Value.IsMotion == false,
            "an injected click must never look like a drag report");
    }

    [Fact]
    public void AppState_ReturnsWhateverTheHostSnapshots()
    {
        var (inspector, _) = Detached(appState: () => "{\"selected\":\"a3\",\"sideToMove\":\"White\",\"plies\":2}");

        var state = Call(inspector, "appState");

        state.GetProperty("selected").GetString().ShouldBe("a3");
        state.GetProperty("plies").GetInt32().ShouldBe(2);
    }

    /// <summary>
    /// The input log: the deleted env-var stderr trace, promoted to a wire verb. This exact shape is what
    /// identified the mouse-motion-as-click bug — the raw event, and the state it changed.
    /// </summary>
    [Fact]
    public void InputLog_ReportsWhatTheAppReceived()
    {
        var (inspector, _) = Detached();

        inspector.LogInput("MouseDown(530,820) selected b1=>- side=Black");
        inspector.LogInput("MouseMove(530,820) -> None selected -=>-");

        var events = Call(inspector, "inputLog").GetProperty("events");

        events.GetArrayLength().ShouldBe(2);
        events[1].GetString().ShouldContain("MouseMove");
    }

    private static List<ConsoleKey> Drain(FakeScreen screen)
    {
        var keys = new List<ConsoleKey>();
        while (screen.Injected.TryDequeue(out var evt)) keys.Add(evt.Key);
        return keys;
    }

    // ------------------------------------------------------------------------------------------------
    // Transport. ONE test, because the wire contract does deserve covering — a driver has to be able to
    // connect, get its id echoed, and see an error rather than a silent empty result. Everything above is
    // socket-free precisely so this is the only test that can be flaky about ports.
    // ------------------------------------------------------------------------------------------------

    [Fact]
    [Trait("Category", "Functional")]
    public void OverTheWire_ARequestIsAnsweredAndAnUnknownVerbIsAnError()
    {
        var screen = new FakeScreen(12, 2);
        using var inspector = ConsoleDebugInspector.Attach("WireTest", screen);

        using var stop = new CancellationTokenSource();
        var pump = new Thread(() =>
        {
            while (!stop.IsCancellationRequested) { inspector.Pump(); Thread.Sleep(2); }
        }) { IsBackground = true };
        pump.Start();

        try
        {
            using var tcp = new TcpClient("127.0.0.1", inspector.Port);
            using var stream = tcp.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };

            writer.WriteLine("{\"id\":7,\"method\":\"ping\",\"params\":{}}");
            var pong = JsonDocument.Parse(reader.ReadLine()!).RootElement;
            pong.GetProperty("id").GetInt32().ShouldBe(7, "the id must be echoed so replies can be matched");
            pong.GetProperty("result").GetProperty("app").GetString().ShouldBe("WireTest");

            writer.WriteLine("{\"id\":8,\"method\":\"nope\",\"params\":{}}");
            var err = JsonDocument.Parse(reader.ReadLine()!).RootElement;
            err.GetProperty("id").GetInt32().ShouldBe(8);
            err.GetProperty("error").GetString().ShouldContain("nope");
        }
        finally
        {
            stop.Cancel();
        }
    }
}
#endif
