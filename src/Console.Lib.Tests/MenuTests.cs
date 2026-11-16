using Console.Lib;
using Shouldly;
using Xunit;

namespace Console.Lib.Tests;

public class MenuTests
{
    private sealed class TestMenu(
        IVirtualTerminal terminal, TimeProvider timeProvider,
        string title, string prompt, string[] items)
        : MenuBase<int>(terminal, timeProvider)
    {
        protected override Task<int> ShowAsyncCore(CancellationToken cancellationToken)
            => ShowMenuAsync(title, prompt, items, cancellationToken);
    }

    private sealed class PanelMenu(IVirtualTerminal terminal, string[] items)
        : MenuBase<int>(terminal, new FakeClock())
    {
        protected override bool UsePanel => true;
        protected override Task<int> ShowAsyncCore(CancellationToken cancellationToken)
            => ShowMenuAsync("Title", "Pick one:", items, cancellationToken);
    }

    /// <summary>A panel test must fail with a message, not wait for ever on the clock below: cancel after a few seconds.</summary>
    private static CancellationToken Deadline() => new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token;

    /// <summary>Time that never advances by itself: a panel test with queued input must never reach a delay.</summary>
    private sealed class FakeClock : TimeProvider;

    /// <summary>A terminal whose cells land in a <see cref="CellBuffer"/>, so a test can read the screen back.</summary>
    private sealed class BufferTerminal : IVirtualTerminal
    {
        private readonly Queue<ConsoleInputEvent> _inputs;
        private readonly CellBufferViewport _vp;
        private readonly int _width, _height;

        public BufferTerminal(Queue<ConsoleInputEvent> inputs, int width, int height)
        {
            _inputs = inputs;
            _width = width;
            _height = height;
            Buffer = new CellBuffer { ColorMode = ColorMode.Sgr16 };
            Buffer.Resize(width, height);
            _vp = new CellBufferViewport(Buffer, width, height);
        }

        public CellBuffer Buffer { get; }
        public int ClearCount { get; private set; }
        public (int Column, int Row) Offset => (0, 0);
        public (int Width, int Height) Size => (_width, _height);
        public void SetCursorPosition(int left, int top) => _vp.SetCursorPosition(left, top);
        public void Write(string text) => _vp.Write(text);
        public void WriteLine(string? text = null) => _vp.WriteLine(text);
        public void Flush() { }
        public Stream OutputStream => Stream.Null;
        public TermCell CellSize => new(10, 20);
        public ColorMode ColorMode => ColorMode.Sgr16;
        public Task InitAsync() => Task.CompletedTask;
        public ImageDisplayCapability ImageDisplayCapability => ImageDisplayCapability.NoColor;
        public bool HasSixelSupport => false;
        public bool HasColorSupport => false;
        public bool IsInputRedirected => false;
        public bool IsOutputRedirected => false;
        public void EnterAlternateScreen() => IsAlternateScreen = true;
        public bool IsAlternateScreen { get; private set; }
        public void Clear() => ClearCount++;
        public bool HasInput() => _inputs.Count > 0 ? true : throw new InvalidOperationException("input exhausted before the menu returned");
        public ConsoleInputEvent TryReadInput() => _inputs.Dequeue();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static string RowText(CellBuffer b, int row)
        => string.Concat(Enumerable.Range(0, b.Width).Select(x => b.BackAt(x, row).Glyph));

    private static BufferTerminal PanelTerminal(params ConsoleInputEvent[] inputs)
    {
        var t = new BufferTerminal(new Queue<ConsoleInputEvent>(inputs), 60, 24);
        t.EnterAlternateScreen();
        for (var y = 0; y < 24; y++)
        {
            t.SetCursorPosition(0, y);
            t.Write(new string('.', 60));
        }
        return t;
    }

    private static ConsoleInputEvent Press(int col, int row) =>
        new(new MouseEvent(0, col * 10 + 5, row * 20 + 5, IsRelease: false), ConsoleKey.None, 0);

    [Fact]
    public async Task Panel_DownEnter_ReturnsIndex_BackdropSurvives_NoClear()
    {
        var t = PanelTerminal(Key(ConsoleKey.DownArrow), Key(ConsoleKey.Enter));

        (await new PanelMenu(t, TestItems).ShowAsync(Deadline())).ShouldBe(1);

        t.ClearCount.ShouldBe(0);
        RowText(t.Buffer, 0).ShouldBe(new string('.', 60));
        RowText(t.Buffer, 23).ShouldBe(new string('.', 60));
        Enumerable.Range(0, 24).Any(r => RowText(t.Buffer, r).Contains("Beta")).ShouldBeTrue();
    }

    [Fact]
    public async Task Panel_Digit_ReturnsIndex()
    {
        var t = PanelTerminal(Key(ConsoleKey.D3));

        (await new PanelMenu(t, TestItems).ShowAsync(Deadline())).ShouldBe(2);
    }

    [Fact]
    public async Task Panel_PressOutsideCardIsConsumed_ThenRowPressReturnsIt()
    {
        // The first frame is drawn before input is read, so find the Gamma row from a scratch render.
        var probe = PanelTerminal();
        var widget = new MenuWidget(probe);
        widget.Reset("Title", "Pick one:", [.. TestItems]);
        widget.RenderPanel();
        var gamma = Enumerable.Range(0, 24).First(r => RowText(probe.Buffer, r).Contains("Gamma"));

        var t = PanelTerminal(Press(2, 2), Press(23, gamma));

        (await new PanelMenu(t, TestItems).ShowAsync(Deadline())).ShouldBe(2);
    }

    private static readonly string[] TestItems = ["Alpha", "Beta", "Gamma"];

    // --- Non-alternate screen tests (digit shortcuts only) ---

    [Theory]
    [MemberData(nameof(DigitDataSource))]
    public async Task ShowAsync_NonAlternate_DigitSelectsItem(
        Queue<ConsoleInputEvent> inputs,
        int expectedIndex)
    {
        var terminal = new FakeTerminal(inputs);
        var menu = new TestMenu(terminal, TimeProvider.System, "Title", "Pick one:", TestItems);

        var result = await menu.ShowAsync(CancellationToken.None);

        terminal.IsAlternateScreen.ShouldBeFalse();
        result.ShouldBe(expectedIndex);
    }

    public static IEnumerable<object[]> DigitDataSource() =>
    [
        [new Queue<ConsoleInputEvent>([Key(ConsoleKey.D1)]), 0],
        [new Queue<ConsoleInputEvent>([Key(ConsoleKey.D2)]), 1],
        [new Queue<ConsoleInputEvent>([Key(ConsoleKey.D3)]), 2],
    ];

    // --- Alternate screen tests (arrow keys, Enter, mouse) ---

    [Theory]
    [MemberData(nameof(AlternateDataSource))]
    public async Task ShowAsync_Alternate_ReturnsExpectedIndex(
        Queue<ConsoleInputEvent> inputs,
        int expectedIndex)
    {
        var terminal = new FakeTerminal(inputs);
        terminal.EnterAlternateScreen();
        var menu = new TestMenu(terminal, TimeProvider.System, "Title", "Pick one:", TestItems);

        var result = await menu.ShowAsync(CancellationToken.None);

        terminal.IsAlternateScreen.ShouldBeTrue();
        result.ShouldBe(expectedIndex);
    }

    public static IEnumerable<object[]> AlternateDataSource() =>
    [
        ArrowDownThenEnter(),
        ArrowUpWrapsThenEnter(),
        DigitShortcutInAlternate(),
        MouseClickOnItem(),
    ];

    private static ConsoleInputEvent Key(ConsoleKey key) => new(null, key, 0);

    private static ConsoleInputEvent Mouse(int y) =>
        new(new MouseEvent(0, 0, y, IsRelease: true), ConsoleKey.None, 0);

    /// <summary>
    /// Computes the row of the first menu item, matching DrawMenuAlternateScreen logic.
    /// Uses the same default height (24) as <see cref="FakeTerminal"/>.
    /// </summary>
    private static int ComputeMenuStartRow(int itemCount, int windowHeight = 24)
    {
        var totalLines = 4 + itemCount;
        var startRow = Math.Max(0, (windowHeight - totalLines) / 2);
        return startRow + 4;
    }

    // DownArrow moves to index 1, Enter confirms
    private static object[] ArrowDownThenEnter() =>
    [
        new Queue<ConsoleInputEvent>([Key(ConsoleKey.DownArrow), Key(ConsoleKey.Enter)]),
        1,
    ];

    // UpArrow from index 0 wraps to last item (index 2), Enter confirms
    private static object[] ArrowUpWrapsThenEnter() =>
    [
        new Queue<ConsoleInputEvent>([Key(ConsoleKey.UpArrow), Key(ConsoleKey.Enter)]),
        2,
    ];

    // D1 selects index 0 in alternate screen too
    private static object[] DigitShortcutInAlternate() =>
    [
        new Queue<ConsoleInputEvent>([Key(ConsoleKey.D1)]),
        0,
    ];

    // Mouse click on first menu item row selects index 0
    // FakeTerminal.CellSize is (10, 20), so mouse events use pixel coordinates
    private static object[] MouseClickOnItem()
    {
        const int cellHeight = 20;
        var menuStartRow = ComputeMenuStartRow(3);
        return
        [
            new Queue<ConsoleInputEvent>([Mouse(menuStartRow * cellHeight)]),
            0,
        ];
    }
}
