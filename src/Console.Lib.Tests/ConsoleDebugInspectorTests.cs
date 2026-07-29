#if DEBUG
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using DIR.Lib;
using Shouldly;
using Xunit;

namespace Console.Lib.Tests;

/// <summary>
/// The console debug inspector, driven the way a real driver drives it: over a loopback socket, one JSON
/// object per line, against a running app.
///
/// <para>These are the tests that make the inspector worth building. The bug that prompted it — a piece
/// selecting itself after a move — took a manual session, a screenshot, an env-var trace and several wrong
/// hypotheses from me. With <c>appState</c> and <c>inputLog</c> it is one request, and the whole exchange is
/// scriptable, which means it can live in CI instead of in a person's afternoon.</para>
///
/// <para>The fake terminal is a real <see cref="CellBuffer"/> with no console attached, so the socket, the
/// framing, the command queue and the cell plane are all genuinely exercised — only the OS terminal is
/// absent.</para>
/// </summary>
public sealed class ConsoleDebugInspectorTests : IDisposable
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

    /// <summary>Newline-delimited JSON client — the same protocol a Python driver would speak.</summary>
    private sealed class Client : IDisposable
    {
        private readonly TcpClient _tcp;
        private readonly StreamReader _reader;
        private readonly StreamWriter _writer;
        private int _id;

        public Client(int port)
        {
            _tcp = new TcpClient("127.0.0.1", port);
            var stream = _tcp.GetStream();
            _reader = new StreamReader(stream, Encoding.UTF8);
            _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
        }

        public JsonElement Call(string method, string paramsJson = "{}")
        {
            var id = ++_id;
            _writer.WriteLine($"{{\"id\":{id},\"method\":\"{method}\",\"params\":{paramsJson}}}");
            var line = _reader.ReadLine() ?? throw new IOException("inspector closed the connection");
            var doc = JsonDocument.Parse(line);
            doc.RootElement.GetProperty("id").GetInt32().ShouldBe(id);
            return doc.RootElement.Clone();
        }

        public JsonElement Result(string method, string paramsJson = "{}")
        {
            var reply = Call(method, paramsJson);
            reply.TryGetProperty("error", out var err).ShouldBeFalse(
                err.ValueKind == JsonValueKind.String ? err.GetString() : "unexpected error");
            return reply.GetProperty("result");
        }

        public void Dispose() { _reader.Dispose(); _writer.Dispose(); _tcp.Dispose(); }
    }

    private readonly List<IDisposable> _owned = [];

    /// <summary>
    /// Starts an inspector plus a pump thread — the equivalent of the app's own loop calling Pump each
    /// iteration, which is what makes commands run at all.
    /// </summary>
    private (ConsoleDebugInspector Inspector, FakeScreen Screen, Client Client) Start(
        FakeScreen? screen = null, Func<string>? appState = null)
    {
        screen ??= new FakeScreen(20, 4);
        var inspector = ConsoleDebugInspector.Attach("Test", screen, appState);
        var stop = new CancellationTokenSource();
        var pump = new Thread(() =>
        {
            while (!stop.IsCancellationRequested) { inspector.Pump(); Thread.Sleep(2); }
        }) { IsBackground = true };
        pump.Start();

        var client = new Client(inspector.Port);
        _owned.Add(client);
        _owned.Add(inspector);
        _owned.Add(new Disposer(() => stop.Cancel()));
        return (inspector, screen, client);
    }

    private sealed class Disposer(System.Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }

    public void Dispose()
    {
        for (var i = _owned.Count - 1; i >= 0; i--) _owned[i].Dispose();
    }

    [Fact]
    public void Ping_AnswersOverTheSocket()
    {
        var (_, _, client) = Start();

        var result = client.Result("ping");

        result.GetProperty("ok").GetBoolean().ShouldBeTrue();
        result.GetProperty("app").GetString().ShouldBe("Test");
        result.GetProperty("protocol").GetInt32().ShouldBe(1);
    }

    [Fact]
    public void AnUnknownMethod_IsAnError_NotASilentEmptyResult()
    {
        var (_, _, client) = Start();

        var reply = client.Call("noSuchThing");

        reply.TryGetProperty("error", out var err).ShouldBeTrue();
        err.GetString().ShouldContain("noSuchThing");
    }

    /// <summary>
    /// The cell plane: the screen readable as WORDS. This is the thing a pixel inspector cannot do, and it
    /// is why the console inspector is worth having rather than merely having parity.
    /// </summary>
    [Fact]
    public void Screen_ReadsTheTerminalBackAsText()
    {
        var screen = new FakeScreen(20, 3);
        var (_, _, client) = Start(screen);

        var style = new VtStyle(new RGBAColor32(0xFF, 0xCE, 0x9E, 0xff), new RGBAColor32(0, 0, 0, 0xff));
        screen.CellBuffer!.MoveTo(0, 1);
        screen.CellBuffer.Write($"{style.Apply(ColorMode.TrueColor)}{"White to move.".PadRight(20)}");
        screen.CellBuffer.Flush(new NullSink());

        var rows = client.Result("screen").GetProperty("rows");

        rows.GetArrayLength().ShouldBe(3);
        rows[1].GetString().ShouldBe("White to move.      ");
        client.Result("row", "{\"row\":1}").GetProperty("text").GetString().ShouldBe("White to move.      ");
    }

    [Fact]
    public void Cell_ReportsTheGlyphAndThePen()
    {
        var screen = new FakeScreen(8, 2);
        var (_, _, client) = Start(screen);

        var style = new VtStyle(new RGBAColor32(0x8A, 0x4F, 0xD0, 0xff), new RGBAColor32(0x20, 0x20, 0x34, 0xff));
        screen.CellBuffer!.MoveTo(2, 0);
        screen.CellBuffer.Write($"{style.Apply(ColorMode.TrueColor)}Q");
        screen.CellBuffer.Flush(new NullSink());

        var cell = client.Result("cell", "{\"column\":2,\"row\":0}");

        cell.GetProperty("glyph").GetString().ShouldBe("Q");
        cell.GetProperty("fg").GetString().ShouldBe("#8A4FD0");
        cell.GetProperty("bg").GetString().ShouldBe("#202034");
        cell.GetProperty("kind").GetString().ShouldBe("Text");
    }

    /// <summary>An unbuffered terminal has no screen to report, and must say so rather than answering with
    /// a blank one that a driver would happily assert against.</summary>
    [Fact]
    public void AnUnbufferedTerminal_SaysWhyItHasNoScreen()
    {
        var (_, _, client) = Start(new FakeScreen(10, 2, buffered: false));

        client.Result("screen").GetProperty("error").GetString().ShouldContain("EnableCellBuffer");
    }

    [Fact]
    public void Key_InjectsAKeystroke()
    {
        var (_, screen, client) = Start();

        client.Result("key", "{\"key\":\"Escape\"}").GetProperty("ok").GetBoolean().ShouldBeTrue();
        client.Result("key", "{\"key\":\"e\"}");
        client.Result("key", "{\"key\":\"4\"}");

        var keys = new List<ConsoleKey>();
        while (screen.Injected.TryDequeue(out var evt)) keys.Add(evt.Key);

        keys.ShouldBe([ConsoleKey.Escape, ConsoleKey.E, ConsoleKey.D4]);
    }

    /// <summary>
    /// A single letter and a single digit have to work, because that is how a board move is made: GameUI
    /// reads a file as a letter and a rank as a digit, so e2e4 is four keystrokes.
    /// </summary>
    [Fact]
    public void Key_AcceptsBareLettersAndDigits_WhichIsHowAMoveIsTyped()
    {
        var (_, screen, client) = Start();

        foreach (var k in new[] { "e", "2", "e", "4" })
        {
            client.Result("key", $"{{\"key\":\"{k}\"}}").GetProperty("ok").GetBoolean().ShouldBeTrue();
        }

        var keys = new List<ConsoleKey>();
        while (screen.Injected.TryDequeue(out var evt)) keys.Add(evt.Key);

        keys.ShouldBe([ConsoleKey.E, ConsoleKey.D2, ConsoleKey.E, ConsoleKey.D4]);
    }

    /// <summary>
    /// A click is addressed in CELLS and converted to the pixel coordinates hit-testing wants, because cells
    /// are what a driver can compute — it just read the text at a column and a row. It lands on the cell
    /// CENTRE so a rounding difference cannot pick a neighbour.
    /// </summary>
    [Fact]
    public void Click_InjectsAPressAndReleaseAtTheCellCentre()
    {
        var (_, screen, client) = Start();

        client.Result("click", "{\"column\":3,\"row\":2}");

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
        var (_, _, client) = Start(appState: () => "{\"selected\":\"a3\",\"sideToMove\":\"White\",\"plies\":2}");

        var state = client.Result("appState");

        state.GetProperty("selected").GetString().ShouldBe("a3");
        state.GetProperty("plies").GetInt32().ShouldBe(2);
    }

    /// <summary>
    /// The input log, which is the deleted env-var trace promoted to a wire verb. This exact shape is what
    /// identified the motion-as-click bug: the raw event, and the state it changed.
    /// </summary>
    [Fact]
    public void InputLog_ReportsWhatTheAppReceived()
    {
        var (inspector, _, client) = Start();

        inspector.LogInput("MouseDown(530,820) selected b1=>- side=Black");
        inspector.LogInput("MouseMove(530,820) -> None selected -=>-");

        var events = client.Result("inputLog").GetProperty("events");

        events.GetArrayLength().ShouldBe(2);
        events[1].GetString().ShouldContain("MouseMove");
    }

    [Fact]
    public void Size_ReportsTheGridAndWhetherItIsBuffered()
    {
        var (_, _, client) = Start(new FakeScreen(108, 30));

        var size = client.Result("size");

        size.GetProperty("columns").GetInt32().ShouldBe(108);
        size.GetProperty("rows").GetInt32().ShouldBe(30);
        size.GetProperty("cellHeight").GetInt32().ShouldBe(20);
        size.GetProperty("buffered").GetBoolean().ShouldBeTrue();
    }

    private sealed class NullSink : ICellSink
    {
        public void MoveTo(int column, int row) { }
        public void SetPen(VtStyle style, bool reverse) { }
        public void Write(ReadOnlySpan<char> run) { }
    }
}
#endif
