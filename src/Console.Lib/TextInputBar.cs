using DIR.Lib;

namespace Console.Lib;

/// <summary>
/// Single-line text input widget that renders a <see cref="TextInputState"/> with a visible
/// reverse-video cursor. Handles keyboard input routing: navigation keys go to
/// <see cref="TextInputState.HandleKey"/>, printable characters go to
/// <see cref="TextInputState.InsertText"/>.
/// </summary>
public class TextInputBar(ITerminalViewport viewport) : Widget(viewport)
{
    private string _label = "";
    private VtStyle _style = new(SgrColor.BrightWhite, SgrColor.BrightBlack);
    private VtStyle _labelStyle = new(SgrColor.BrightCyan, SgrColor.BrightBlack);

    /// <summary>The text input state to render and edit.</summary>
    public TextInputState? State { get; set; }

    /// <summary>Sets the label shown before the input field.</summary>
    public TextInputBar Label(string label) { _label = label; return this; }

    /// <summary>Sets the style for the input field text.</summary>
    public TextInputBar Style(VtStyle style) { _style = style; return this; }

    /// <summary>Sets the style for the label.</summary>
    public TextInputBar LabelStyle(VtStyle style) { _labelStyle = style; return this; }

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
        var fieldPart = State is not null
            ? $"{_style.Apply(colorMode)}{before}{VtStyle.ReverseOn}{cursorChar}{VtStyle.ReverseOff}{after}"
            : $"{_style.Apply(colorMode)}{text}";

        var content = $"{labelPart}{fieldPart}";
        // Pad to full width to clear previous content
        var visibleLen = _label.Length + 1 + text.Length + 1; // approximate visible chars
        var padding = Math.Max(0, width - visibleLen);

        Viewport.Write($"{content}{new string(' ', padding)}{VtStyle.Reset}");
    }

    /// <summary>
    /// Routes an <see cref="InputKey"/> to the active <see cref="TextInputState"/>.
    /// Returns <c>true</c> if the key was consumed.
    /// Navigation/editing keys go to <see cref="TextInputState.HandleKey"/>,
    /// printable characters go to <see cref="TextInputState.InsertText"/>.
    /// </summary>
    /// <remarks>
    /// After this method returns, check <see cref="TextInputState.IsCommitted"/>
    /// and <see cref="TextInputState.IsCancelled"/> to handle Enter/Escape.
    /// </remarks>
    public bool HandleInput(InputKey key, InputModifier modifiers)
    {
        if (State is not { } state)
        {
            return false;
        }

        // Navigation and editing keys (backspace, delete, arrows, home, end, enter, escape)
        if (key.ToTextInputKey(modifiers) is { } textKey)
        {
            state.HandleKey(textKey);
            return true;
        }

        // Printable character input
        if (key.ToChar(modifiers) is { } ch)
        {
            state.InsertText(ch.ToString());
            return true;
        }

        return false;
    }
}
