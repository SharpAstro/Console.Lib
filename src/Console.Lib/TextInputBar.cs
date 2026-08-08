using DIR.Lib;

namespace Console.Lib;

/// <summary>
/// Single-line text input widget that renders a <see cref="TextInputState"/> with a visible
/// reverse-video cursor — or, opted in via <see cref="Caret"/>, with the terminal's REAL cursor
/// parked at the insertion point. Handles keyboard input routing: navigation keys go to
/// <see cref="TextInputState.HandleKey"/>, printable characters go to
/// <see cref="TextInputState.InsertText"/>.
/// </summary>
public class TextInputBar(ITerminalViewport viewport) : Widget(viewport)
{
    private string _label = "";
    private VtStyle _style = new(SgrColor.BrightWhite, SgrColor.BrightBlack);
    private VtStyle _labelStyle = new(SgrColor.BrightCyan, SgrColor.BrightBlack);
    private CaretStyle? _caret;

    /// <summary>The text input state to render and edit.</summary>
    public TextInputState? State { get; set; }

    /// <summary>Sets the label shown before the input field.</summary>
    public TextInputBar Label(string label) { _label = label; return this; }

    /// <summary>Sets the style for the input field text.</summary>
    public TextInputBar Style(VtStyle style) { _style = style; return this; }

    /// <summary>Sets the style for the label.</summary>
    public TextInputBar LabelStyle(VtStyle style) { _labelStyle = style; return this; }

    /// <summary>
    /// Renders the cursor as the terminal's REAL caret in this shape (parked via
    /// <see cref="ITerminalViewport.SetCaret"/>) instead of painting the reverse-video cell —
    /// <see cref="CaretStyle.BlinkingBar"/> is the thin blinking prompt of a modern editor. The caret is
    /// sticky terminal state: a host that moves focus off this widget calls
    /// <see cref="ITerminalViewport.HideCaret"/>. Default (unset) keeps the painted block.
    /// </summary>
    public TextInputBar Caret(CaretStyle style) { _caret = style; return this; }

    /// <summary>
    /// Renders the label and input field with a reverse-video cursor.
    /// If <see cref="State"/> is null, renders an empty field.
    /// </summary>
    public override void Render()
    {
        var width = Viewport.Size.Width;
        if (width <= 0) return;

        if (!TrySetCursorPosition(Viewport, 0, 0)) return;

        var colorMode = Viewport.ColorMode;
        var text = State?.Text ?? "";
        var cursorPos = State is not null ? Math.Clamp(State.CursorPos, 0, text.Length) : 0;

        var before = text[..cursorPos];
        var cursorChar = cursorPos < text.Length ? text[cursorPos].ToString() : " ";
        var after = cursorPos < text.Length ? text[(cursorPos + 1)..] : "";

        var labelPart = _label.Length > 0 ? $"{_labelStyle.Apply(colorMode)}{_label} " : "";
        var fieldPart = State is not null && _caret is null
            ? $"{_style.Apply(colorMode)}{before}{VtStyle.ReverseOn}{cursorChar}{VtStyle.ReverseOff}{after}"
            : $"{_style.Apply(colorMode)}{text}";

        var content = $"{labelPart}{fieldPart}";
        // Pad to full width to clear previous content
        var visibleLen = _label.Length + 1 + text.Length + 1; // approximate visible chars
        var padding = Math.Max(0, width - visibleLen);

        Viewport.Write($"{content}{new string(' ', padding)}{VtStyle.Reset}");

        if (State is not null && _caret is { } caretStyle)
        {
            // The cell the reverse-video block would have occupied: label + its separating space, then one
            // cell per UTF-16 char before the cursor — except a surrogate PAIR, which the terminal renders
            // as one cell. (East-Asian double-width is the same known limitation as everywhere else here.)
            var labelCells = _label.Length > 0 ? _label.Length + 1 : 0;
            Viewport.SetCaret(labelCells + CellCount(text.AsSpan(0, cursorPos)), 0, caretStyle);
        }
    }

    /// <summary>Cells a char span occupies on screen: surrogate pairs are one cell, everything else 1:1.</summary>
    private static int CellCount(ReadOnlySpan<char> s)
    {
        var cells = 0;
        for (var i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) i++;
            cells++;
        }
        return cells;
    }

    /// <summary>
    /// Routes a <see cref="ConsoleInputEvent"/> to the active <see cref="TextInputState"/>.
    /// Returns <c>true</c> if the event was consumed.
    /// Navigation/editing keys go to <see cref="TextInputState.HandleKey"/>;
    /// printable input (including non-ASCII codepoints carried by
    /// <see cref="ConsoleInputEvent.KeyChar"/>) goes to <see cref="TextInputState.InsertText"/>.
    /// </summary>
    /// <remarks>
    /// After this method returns, check <see cref="TextInputState.IsCommitted"/>
    /// and <see cref="TextInputState.IsCancelled"/> to handle Enter/Escape.
    /// </remarks>
    public bool HandleInput(ConsoleInputEvent ev)
    {
        if (State is not { } state)
        {
            return false;
        }

        var (inputKey, inputMod) = (ev.Key.ToInputKey, ev.Modifiers.ToInputModifier);

        // Navigation and editing keys (backspace, delete, arrows, home, end, enter, escape)
        if (inputKey.ToTextInputKey(inputMod) is { } textKey)
        {
            state.HandleKey(textKey);
            return true;
        }

        // Ctrl/Alt held → not text input.
        if ((ev.Modifiers & (ConsoleModifiers.Control | ConsoleModifiers.Alt)) != 0)
        {
            return false;
        }

        // Prefer the decoded UTF-8 codepoint when the terminal supplied one
        // (this is what makes non-US-layout characters work).
        if (ev.KeyChar is { } rune)
        {
            state.InsertText(rune.ToString());
            return true;
        }

        // Fallback for paths that don't populate KeyChar (legacy callers,
        // synthetic events): resolve the (key, mods) pair via the US-layout map.
        if (inputKey.ToChar(inputMod) is { } ch)
        {
            state.InsertText(ch.ToString());
            return true;
        }

        return false;
    }
}
