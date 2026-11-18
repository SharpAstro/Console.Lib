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

    private sealed class ActionMenu(IVirtualTerminal terminal, MenuActions actions, bool panel)
        : MenuBase<MenuChoice>(terminal, new FakeClock())
    {
        protected override bool UsePanel => panel;
        protected override Task<MenuChoice> ShowAsyncCore(CancellationToken cancellationToken)
            => ShowMenuAsync("Title", "Pick one:", ["Alpha", "Beta", "Gamma"], actions, cancellationToken);
    }

    /// <summary>A panel test must fail with a message, not wait for ever on the clock below: cancel after a few seconds.</summary>
    private static CancellationToken Deadline() => new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token;

    /// <summary>Time that never advances by itself: a panel test with queued input must never reach a delay.</summary>
    private sealed class FakeClock : TimeProvider;

    /// <summary>A clock whose timers fire at once (off the calling thread), so a menu's idle delay costs no wall time.</summary>
    private sealed class InstantClock : TimeProvider
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            ThreadPool.QueueUserWorkItem(_ => callback(state));
            return new NullTimer();
        }

        private sealed class NullTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    /// <summary>Counts <see cref="MenuBase{T}.OnIdle"/> and, on the Nth call, queues the key that chooses row 2.</summary>
    private sealed class IdleMenu(BufferTerminal terminal, bool panel, int chooseOnCall) : MenuBase<int>(terminal, new InstantClock())
    {
        public int IdleCalls { get; private set; }
        protected override bool UsePanel => panel;
        protected override void OnIdle()
        {
            IdleCalls++;
            if (IdleCalls == chooseOnCall) terminal.Enqueue(Key(ConsoleKey.D2));
        }
        protected override Task<int> ShowAsyncCore(CancellationToken cancellationToken)
            => ShowMenuAsync("Title", "Pick one:", ["Alpha", "Beta", "Gamma"], cancellationToken);
    }

    private static async Task AssertIdleHook(bool alternate, bool panel)
    {
        var t = new BufferTerminal(new Queue<ConsoleInputEvent>(), 60, 24) { IdleWhenEmpty = true };
        if (alternate) t.EnterAlternateScreen();
        var menu = new IdleMenu(t, panel, chooseOnCall: 3);

        (await menu.ShowAsync(Deadline())).ShouldBe(1);

        menu.IdleCalls.ShouldBe(3);
    }

    [Fact]
    public Task OnIdle_NormalMode_RunsWhileNoInputWaiting() => AssertIdleHook(alternate: false, panel: false);

    [Fact]
    public Task OnIdle_AlternateRows_RunsWhileNoInputWaiting() => AssertIdleHook(alternate: true, panel: false);

    [Fact]
    public Task OnIdle_Panel_RunsWhileNoInputWaiting() => AssertIdleHook(alternate: true, panel: true);

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
        /// <summary>When true, an empty queue means "no input waiting" instead of a failure (idle-hook tests).</summary>
        public bool IdleWhenEmpty { get; init; }
        public void Enqueue(ConsoleInputEvent e) => _inputs.Enqueue(e);
        public bool HasInput() => _inputs.Count > 0 ? true : IdleWhenEmpty ? false : throw new InvalidOperationException("input exhausted before the menu returned");
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

    /// <summary>Finds the cell of a header control by rendering the same panel on a scratch terminal.</summary>
    private static (int Col, int Row) FindInPanel(string text)
    {
        var probe = PanelTerminal();
        var widget = new MenuWidget(probe) { Back = () => { }, Close = () => { } };
        widget.Reset("Title", "Pick one:", [.. TestItems]);
        widget.RenderPanel();
        for (var r = 0; r < 24; r++)
        {
            var col = RowText(probe.Buffer, r).IndexOf(text, StringComparison.Ordinal);
            if (col >= 0) return (col, r);
        }
        throw new InvalidOperationException($"'{text}' not drawn");
    }

    private const MenuActions Both = MenuActions.Back | MenuActions.Close;

    [Fact]
    public async Task Panel_PressBack_ReturnsBack()
    {
        var (c, r) = FindInPanel("Back");
        var t = PanelTerminal(Press(c, r));
        (await new ActionMenu(t, Both, true).ShowAsync(Deadline())).IsBack.ShouldBeTrue();
    }

    [Fact]
    public async Task Panel_PressClose_ReturnsClose()
    {
        var (c, r) = FindInPanel("×");
        var t = PanelTerminal(Press(c, r));
        (await new ActionMenu(t, Both, true).ShowAsync(Deadline())).IsClose.ShouldBeTrue();
    }

    [Fact]
    public async Task Panel_EscWithBackAndClose_ReturnsBack()
    {
        var t = PanelTerminal(Key(ConsoleKey.Escape));
        (await new ActionMenu(t, Both, true).ShowAsync(Deadline())).IsBack.ShouldBeTrue();
    }

    [Fact]
    public async Task Panel_EscWithCloseOnly_ReturnsClose()
    {
        var t = PanelTerminal(Key(ConsoleKey.Escape));
        (await new ActionMenu(t, MenuActions.Close, true).ShowAsync(Deadline())).IsClose.ShouldBeTrue();
    }

    [Fact]
    public async Task Panel_EscWithNeither_IsIgnored_ThenDigitReturnsRow()
    {
        var t = PanelTerminal(Key(ConsoleKey.Escape), Key(ConsoleKey.D2));
        (await new ActionMenu(t, MenuActions.None, true).ShowAsync(Deadline())).ShouldBe(MenuChoice.Row(1));
    }

    [Fact]
    public async Task Alternate_Esc_MapsBackThenCloseThenIgnored()
    {
        var a = new FakeTerminal(new Queue<ConsoleInputEvent>([Key(ConsoleKey.Escape)]));
        a.EnterAlternateScreen();
        (await new ActionMenu(a, Both, false).ShowAsync(Deadline())).IsBack.ShouldBeTrue();

        var b = new FakeTerminal(new Queue<ConsoleInputEvent>([Key(ConsoleKey.Escape)]));
        b.EnterAlternateScreen();
        (await new ActionMenu(b, MenuActions.Close, false).ShowAsync(Deadline())).IsClose.ShouldBeTrue();

        var c = new FakeTerminal(new Queue<ConsoleInputEvent>([Key(ConsoleKey.Escape), Key(ConsoleKey.D3)]));
        c.EnterAlternateScreen();
        (await new ActionMenu(c, MenuActions.None, false).ShowAsync(Deadline())).ShouldBe(MenuChoice.Row(2));
    }

    [Fact]
    public async Task Normal_Esc_MapsBackThenCloseThenIgnored()
    {
        var a = new FakeTerminal(new Queue<ConsoleInputEvent>([Key(ConsoleKey.Escape)]));
        (await new ActionMenu(a, Both, false).ShowAsync(Deadline())).IsBack.ShouldBeTrue();

        var b = new FakeTerminal(new Queue<ConsoleInputEvent>([Key(ConsoleKey.Escape)]));
        (await new ActionMenu(b, MenuActions.Close, false).ShowAsync(Deadline())).IsClose.ShouldBeTrue();

        var c = new FakeTerminal(new Queue<ConsoleInputEvent>([Key(ConsoleKey.Escape), Key(ConsoleKey.D1)]));
        (await new ActionMenu(c, MenuActions.None, false).ShowAsync(Deadline())).ShouldBe(MenuChoice.Row(0));
    }

    [Fact]
    public async Task Panel_BackspaceWithBack_ReturnsBack()
    {
        var t = PanelTerminal(Key(ConsoleKey.Backspace));
        (await new ActionMenu(t, MenuActions.Back, true).ShowAsync(Deadline())).IsBack.ShouldBeTrue();

        var u = PanelTerminal(Key(ConsoleKey.Backspace));
        (await new ActionMenu(u, Both, true).ShowAsync(Deadline())).IsBack.ShouldBeTrue();
    }

    [Fact]
    public async Task Panel_BackspaceWithCloseOnlyOrNone_IsIgnored_ThenDigitReturnsRow()
    {
        var a = PanelTerminal(Key(ConsoleKey.Backspace), Key(ConsoleKey.D2));
        (await new ActionMenu(a, MenuActions.Close, true).ShowAsync(Deadline())).ShouldBe(MenuChoice.Row(1));

        var b = PanelTerminal(Key(ConsoleKey.Backspace), Key(ConsoleKey.D3));
        (await new ActionMenu(b, MenuActions.None, true).ShowAsync(Deadline())).ShouldBe(MenuChoice.Row(2));
    }

    // Tab and Shift+Tab reach the header controls (DIR.Lib MenuModel.Focus); arrows stay on the rows.

    private static ConsoleInputEvent ShiftTab => new(null, ConsoleKey.Tab, ConsoleModifiers.Shift);

    [Fact]
    public async Task Panel_TabThenEnter_ReturnsBack()
    {
        var t = PanelTerminal(Key(ConsoleKey.Tab), Key(ConsoleKey.Enter));
        (await new ActionMenu(t, Both, true).ShowAsync(Deadline())).IsBack.ShouldBeTrue();
    }

    [Fact]
    public async Task Panel_ShiftTabThenEnter_ReturnsClose()
    {
        var t = PanelTerminal(ShiftTab, Key(ConsoleKey.Enter));
        (await new ActionMenu(t, Both, true).ShowAsync(Deadline())).IsClose.ShouldBeTrue();
    }

    [Fact]
    public async Task Panel_TabThenArrowReturnsToTheRows()
    {
        // Down from a focused control goes back to the rows and moves there, so Enter confirms row 1, not Back.
        var t = PanelTerminal(Key(ConsoleKey.Tab), Key(ConsoleKey.DownArrow), Key(ConsoleKey.Enter));
        (await new ActionMenu(t, Both, true).ShowAsync(Deadline())).ShouldBe(MenuChoice.Row(1));
    }

    [Fact]
    public async Task Panel_TabWithNoHeaderControl_IsIgnored_ThenEnterReturnsRow()
    {
        var t = PanelTerminal(Key(ConsoleKey.Tab), Key(ConsoleKey.Enter));
        (await new ActionMenu(t, MenuActions.None, true).ShowAsync(Deadline())).ShouldBe(MenuChoice.Row(0));
    }

    [Fact]
    public async Task Alternate_Backspace_MapsBackElseIgnored()
    {
        var a = new FakeTerminal(new Queue<ConsoleInputEvent>([Key(ConsoleKey.Backspace)]));
        a.EnterAlternateScreen();
        (await new ActionMenu(a, MenuActions.Back, false).ShowAsync(Deadline())).IsBack.ShouldBeTrue();

        var b = new FakeTerminal(new Queue<ConsoleInputEvent>([Key(ConsoleKey.Backspace)]));
        b.EnterAlternateScreen();
        (await new ActionMenu(b, Both, false).ShowAsync(Deadline())).IsBack.ShouldBeTrue();

        var c = new FakeTerminal(new Queue<ConsoleInputEvent>([Key(ConsoleKey.Backspace), Key(ConsoleKey.D3)]));
        c.EnterAlternateScreen();
        (await new ActionMenu(c, MenuActions.Close, false).ShowAsync(Deadline())).ShouldBe(MenuChoice.Row(2));

        var d = new FakeTerminal(new Queue<ConsoleInputEvent>([Key(ConsoleKey.Backspace), Key(ConsoleKey.D1)]));
        d.EnterAlternateScreen();
        (await new ActionMenu(d, MenuActions.None, false).ShowAsync(Deadline())).ShouldBe(MenuChoice.Row(0));
    }

    [Fact]
    public async Task Normal_Backspace_MapsBackElseIgnored()
    {
        var a = new FakeTerminal(new Queue<ConsoleInputEvent>([Key(ConsoleKey.Backspace)]));
        (await new ActionMenu(a, MenuActions.Back, false).ShowAsync(Deadline())).IsBack.ShouldBeTrue();

        var b = new FakeTerminal(new Queue<ConsoleInputEvent>([Key(ConsoleKey.Backspace)]));
        (await new ActionMenu(b, Both, false).ShowAsync(Deadline())).IsBack.ShouldBeTrue();

        var c = new FakeTerminal(new Queue<ConsoleInputEvent>([Key(ConsoleKey.Backspace), Key(ConsoleKey.D2)]));
        (await new ActionMenu(c, MenuActions.Close, false).ShowAsync(Deadline())).ShouldBe(MenuChoice.Row(1));

        var d = new FakeTerminal(new Queue<ConsoleInputEvent>([Key(ConsoleKey.Backspace), Key(ConsoleKey.D1)]));
        (await new ActionMenu(d, MenuActions.None, false).ShowAsync(Deadline())).ShouldBe(MenuChoice.Row(0));
    }

    [Fact]
    public async Task IntOverload_StillReturnsRowIndex_AndIgnoresEsc()
    {
        var t = PanelTerminal(Key(ConsoleKey.Escape), Key(ConsoleKey.D2));
        (await new PanelMenu(t, TestItems).ShowAsync(Deadline())).ShouldBe(1);
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

    // Records the screen each time the card is about to change size (the key list opening or closing), which is the
    // moment the host must put the board back: the screen still shows the card as it was.
    private sealed class HelpMenu(BufferTerminal terminal, MenuActions actions) : MenuBase<MenuChoice>(terminal, new FakeClock())
    {
        public List<string[]> Screens { get; } = [];
        protected override bool UsePanel => true;
        protected override void RedrawBackdrop()
            => Screens.Add([.. Enumerable.Range(0, terminal.Buffer.Height).Select(r => RowText(terminal.Buffer, r))]);
        protected override Task<MenuChoice> ShowAsyncCore(CancellationToken cancellationToken)
            => ShowMenuAsync("Title", "Pick one:", ["Alpha", "Beta", "Gamma"], actions, cancellationToken);
    }

    [Fact]
    public async Task Panel_F1ShowsTheKeyList_AndEscClosesItWithoutGoingBack()
    {
        // F1 opens the list; Down is swallowed by it; Esc closes the list (it does NOT take Back); 2 then picks Beta.
        var t = PanelTerminal(Key(ConsoleKey.F1), Key(ConsoleKey.DownArrow), Key(ConsoleKey.Escape), Key(ConsoleKey.D2));
        var menu = new HelpMenu(t, MenuActions.Back);

        var choice = await menu.ShowAsync(Deadline());

        choice.IsBack.ShouldBeFalse();
        choice.Index.ShouldBe(1);
        menu.Screens.Count.ShouldBe(2, "the board goes back once as the list opens and once as it closes");

        var list = menu.Screens[1];
        list.ShouldContain(r => r.Contains("Keys"));
        list.ShouldContain(r => r.Contains("F1 or Esc to return"));
        list.ShouldContain(r => r.Contains("Esc, Backspace") && r.Contains("Go back a step"));
        list.ShouldContain(r => r.Contains("Tab, Shift+Tab") && r.Contains("Reach Back"));
        list.ShouldNotContain(r => r.Contains("Alpha"), "the rows are not shown under the list");

        // The descriptions line up in one column.
        var starts = new[] { "Move the selection", "Go back a step", "Reach Back" }
            .Select(d => list.Single(r => r.Contains(d)).IndexOf(d)).Distinct().ToArray();
        starts.Length.ShouldBe(1);
    }

    [Fact]
    public async Task Panel_F1Twice_ReturnsToTheRows()
    {
        var t = PanelTerminal(Key(ConsoleKey.F1), Key(ConsoleKey.F1), Key(ConsoleKey.DownArrow), Key(ConsoleKey.Enter));
        (await new HelpMenu(t, MenuActions.None).ShowAsync(Deadline())).Index.ShouldBe(1);
    }

    [Fact]
    public async Task Panel_KeyList_NamesEscAsCloseWhenTheStepCanOnlyClose()
    {
        var t = PanelTerminal(Key(ConsoleKey.F1), Key(ConsoleKey.F1), Key(ConsoleKey.D1));
        var menu = new HelpMenu(t, MenuActions.Close);
        (await menu.ShowAsync(Deadline())).Index.ShouldBe(0);
        menu.Screens[1].ShouldContain(r => r.Contains("Esc") && r.Contains("Close") && !r.Contains("Backspace"));
    }

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
