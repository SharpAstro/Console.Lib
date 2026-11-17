using DIR.Lib;

namespace Console.Lib;

/// <summary>The header actions a menu step offers besides its rows.</summary>
[Flags]
public enum MenuActions
{
    /// <summary>Rows only.</summary>
    None = 0,
    /// <summary>The step can go back.</summary>
    Back = 1,
    /// <summary>The step can be closed.</summary>
    Close = 2,
}

/// <summary>What a menu step returned: a row, or one of the header actions.</summary>
/// <param name="Index">The chosen row, or -1 for Back and Close.</param>
/// <param name="Action"><see cref="MenuActions.Back"/> or <see cref="MenuActions.Close"/> when a header action was
/// taken (a press on its control, or Esc), else <see cref="MenuActions.None"/>.</param>
public readonly record struct MenuChoice(int Index, MenuActions Action)
{
    public bool IsBack => Action == MenuActions.Back;
    public bool IsClose => Action == MenuActions.Close;
    public bool IsRow => Action == MenuActions.None;
    public static MenuChoice Row(int index) => new(index, MenuActions.None);
    public static MenuChoice OfBack => new(-1, MenuActions.Back);
    public static MenuChoice OfClose => new(-1, MenuActions.Close);
}

/// <summary>
/// Abstract base class for terminal menus with arrow-key navigation,
/// digit shortcuts, and mouse support in alternate screen mode.
/// </summary>
public abstract class MenuBase<T>(IVirtualTerminal terminal, TimeProvider timeProvider)
{
    private static readonly VtStyle SelectedStyle = new(SgrColor.BrightYellow, SgrColor.Blue);

    private int? _lastWindowWidth;
    private int? _lastWindowHeight;
    private readonly byte _cellHeight = terminal.CellSize.Height;

    protected IVirtualTerminal Terminal => terminal;

    /// <summary>
    /// Opt-in. When true, the alternate-screen menu is <see cref="MenuWidget.RenderPanel"/>: a card over whatever
    /// the host drew first, with no <c>Clear()</c>, keys and mouse routed through <see cref="MenuWidget"/> (a press
    /// outside the card is consumed, a press on a row returns that row). Normal mode is unchanged.
    /// Default false: the full-screen rows path.
    /// </summary>
    protected virtual bool UsePanel => false;

    /// <summary>
    /// Called on the menu's thread on every pass of the wait loop with no input waiting, in every mode (normal,
    /// alternate rows and panel), just before the loop waits. A host can pump its debug inspector here so it
    /// stays responsive while a menu is up. Keep it short: it delays the next input check. Empty by default.
    /// </summary>
    protected virtual void OnIdle() { }

    public async Task<T> ShowAsync(CancellationToken cancellationToken)
    {
        return await ShowAsyncCore(cancellationToken);
    }

    protected abstract Task<T> ShowAsyncCore(CancellationToken cancellationToken);

    protected async Task<int> ShowMenuAsync(
        string title, string prompt, string[] items, CancellationToken cancellationToken)
        => (await ShowMenuAsync(title, prompt, items, MenuActions.None, cancellationToken)).Index;

    /// <summary>
    /// Shows a step that may also have Back and/or close, and says which was taken. In panel mode
    /// (<see cref="UsePanel"/>) the header shows the controls for <paramref name="actions"/>; a press on one returns it.
    /// In every mode Esc returns Back when the step has it, else Close when it has it, else is ignored.
    /// Backspace returns Back when the step has it, else is ignored; it never closes.
    /// With <see cref="MenuActions.None"/> this is the row-only menu.
    /// </summary>
    protected async Task<MenuChoice> ShowMenuAsync(
        string title, string prompt, string[] items, MenuActions actions, CancellationToken cancellationToken)
    {
        if (terminal.IsAlternateScreen && UsePanel)
        {
            return await ShowMenuPanelAsync(title, prompt, items, actions, cancellationToken);
        }

        if (terminal.IsAlternateScreen)
        {
            return await ShowMenuAlternateAsync(title, prompt, items, actions, cancellationToken);
        }

        terminal.WriteLine();
        terminal.WriteLine(prompt);
        for (var i = 0; i < items.Length; i++)
        {
            terminal.WriteLine($"  {i + 1}) {items[i]}");
        }

        terminal.Write("> ");

        while (!cancellationToken.IsCancellationRequested)
        {
            if (!terminal.HasInput())
            {
                OnIdle();
                await Task.Delay(TimeSpan.FromMilliseconds(25), timeProvider, cancellationToken);
                continue;
            }

            var input = terminal.TryReadInput();

            if (input.Key == ConsoleKey.Backspace && BackspaceChoice(actions) is { } bs)
            {
                terminal.WriteLine();
                return bs;
            }

            if (input.Key == ConsoleKey.Escape && EscChoice(actions) is { } esc)
            {
                terminal.WriteLine();
                return esc;
            }

            var digit = input.Key - ConsoleKey.D1;
            if (digit >= 0 && digit < items.Length)
            {
                terminal.WriteLine($"{digit + 1}) {items[digit]}");
                return MenuChoice.Row(digit);
            }
        }

        return MenuChoice.Row(0);
    }

    /// <summary>Esc: Back if the step has it, else Close if it has it, else nothing.</summary>
    private static MenuChoice? EscChoice(MenuActions actions) =>
        actions.HasFlag(MenuActions.Back) ? MenuChoice.OfBack
        : actions.HasFlag(MenuActions.Close) ? MenuChoice.OfClose
        : null;

    /// <summary>Backspace: Back if the step has it, else nothing. It never closes.</summary>
    private static MenuChoice? BackspaceChoice(MenuActions actions) =>
        actions.HasFlag(MenuActions.Back) ? MenuChoice.OfBack : null;

    private async Task<MenuChoice> ShowMenuPanelAsync(
        string title, string prompt, string[] items, MenuActions actions, CancellationToken cancellationToken)
    {
        var widget = new MenuWidget(terminal);
        MenuChoice? header = null;
        if (actions.HasFlag(MenuActions.Back)) widget.Back = () => header = MenuChoice.OfBack;
        if (actions.HasFlag(MenuActions.Close)) widget.Close = () => header = MenuChoice.OfClose;
        widget.Reset(title, prompt, [.. items]);

        void Draw()
        {
            widget.RenderPanel();
            terminal.Flush();
        }

        Draw();
        var (lastW, lastH) = terminal.Size;

        while (!cancellationToken.IsCancellationRequested)
        {
            if (!terminal.HasInput())
            {
                var (w, h) = terminal.Size;
                if (w != lastW || h != lastH)
                {
                    (lastW, lastH) = (w, h);
                    Draw();
                }

                OnIdle();
                await Task.Delay(TimeSpan.FromMilliseconds(25), timeProvider, cancellationToken);
                continue;
            }

            var input = terminal.TryReadInput();

            if (input.Mouse is { } mouse)
            {
                // Acts on the press, as MenuWidget does; a release is ignored.
                if (widget.HandleMouse(mouse))
                {
                    if (header is { } h) return h;
                    if (widget.IsConfirmed) return MenuChoice.Row(widget.SelectedIndex);
                }
                continue;
            }

            if (input.Key == ConsoleKey.Backspace)
            {
                if (BackspaceChoice(actions) is { } bs) return bs;
                continue;
            }

            if (input.Key == ConsoleKey.Escape)
            {
                if (EscChoice(actions) is { } esc) return esc;
                continue;
            }

            if (widget.HandleKey(input.Key.ToInputKey))
            {
                if (widget.IsConfirmed)
                {
                    return MenuChoice.Row(widget.SelectedIndex);
                }
                Draw();
            }
        }

        return MenuChoice.Row(0);
    }

    private async Task<MenuChoice> ShowMenuAlternateAsync(
        string title, string prompt, string[] items, MenuActions actions, CancellationToken cancellationToken)
    {
        // Force a full redraw when entering a new menu (clears stale lines from previous menu)
        _lastWindowWidth = null;
        _lastWindowHeight = null;

        var selected = 0;
        var menuStartRow = DrawMenuAlternateScreen(title, prompt, items, selected);

        while (!cancellationToken.IsCancellationRequested)
        {
            if (!terminal.HasInput())
            {
                // Check for resize while idle
                var (w, h) = terminal.Size;
                if (w != _lastWindowWidth || h != _lastWindowHeight)
                {
                    menuStartRow = DrawMenuAlternateScreen(title, prompt, items, selected);
                }

                OnIdle();
                await Task.Delay(TimeSpan.FromMilliseconds(25), timeProvider, cancellationToken);
                continue;
            }

            var input = terminal.TryReadInput();

            if (input.Mouse is { IsRelease: true } mouse)
            {
                var row = _cellHeight is > 0 ? mouse.Y / _cellHeight : mouse.Y;
                var clickedItem = row - menuStartRow;
                if (clickedItem >= 0 && clickedItem < items.Length)
                {
                    return MenuChoice.Row(clickedItem);
                }
                continue;
            }

            switch (input.Key)
            {
                case ConsoleKey.UpArrow:
                    selected = (selected - 1 + items.Length) % items.Length;
                    menuStartRow = DrawMenuAlternateScreen(title, prompt, items, selected);
                    break;
                case ConsoleKey.DownArrow:
                    selected = (selected + 1) % items.Length;
                    menuStartRow = DrawMenuAlternateScreen(title, prompt, items, selected);
                    break;
                case ConsoleKey.Enter:
                    return MenuChoice.Row(selected);
                case ConsoleKey.Escape:
                    if (EscChoice(actions) is { } esc) return esc;
                    break;
                case ConsoleKey.Backspace:
                    if (BackspaceChoice(actions) is { } bs) return bs;
                    break;
                default:
                    var digit = input.Key - ConsoleKey.D1;
                    if (digit >= 0 && digit < items.Length)
                    {
                        return MenuChoice.Row(digit);
                    }
                    break;
            }
        }

        return MenuChoice.Row(0);
    }

    /// <returns>The row of the first menu item (for mouse hit testing).</returns>
    private int DrawMenuAlternateScreen(string title, string prompt, string[] items, int selected)
    {
        var (windowWidth, windowHeight) = terminal.Size;

        // Total lines: title + blank + prompt + blank + items
        var totalLines = 4 + items.Length;
        var startRow = Math.Max(0, (windowHeight - totalLines) / 2);
        var menuStartRow = startRow + 4;

        // full redraw
        if (windowWidth != _lastWindowWidth || windowHeight != _lastWindowHeight)
        {
            terminal.Clear();
        }

        WriteCenterPadded(startRow, title, windowWidth);
        WriteCenterPadded(startRow + 2, prompt, windowWidth);

        for (var i = 0; i < items.Length; i++)
        {
            var indicator = i == selected ? " \u25B6 " : "   ";
            var label = $"{indicator}{items[i]}";
            var row = menuStartRow + i;

            WriteCenterPadded(row, label, windowWidth, i == selected ? SelectedStyle : null);
        }

        _lastWindowWidth = windowWidth;
        _lastWindowHeight = windowHeight;

        return menuStartRow;
    }

    /// <summary>
    /// Writes centered text padded to the full window width, erasing any stale content without a full clear.
    /// </summary>
    private void WriteCenterPadded(int row, string text, int windowWidth, VtStyle? style = null)
    {
        var col = Math.Max(0, (windowWidth - text.Length) / 2);
        terminal.SetCursorPosition(0, row);

        terminal.Write(new string(' ', col));

        if (style is { } s)
        {
            terminal.Write($"{s.Apply(terminal.ColorMode)}{text}{VtStyle.Reset}");
        }
        else
        {
            terminal.Write(text);
        }

        terminal.Write(new string(' ', Math.Max(0, windowWidth - col - text.Length)));
    }
}
