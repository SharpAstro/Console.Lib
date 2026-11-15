using System.Text;

namespace Console.Lib;

/// <summary>
/// Represents a console input event: either a mouse event, a key press, or both with modifier state.
/// <para>
/// <see cref="KeyChar"/> carries the decoded printable character (UTF-8 codepoint) when the
/// underlying byte stream produced one — this is the right field for text-input widgets
/// because it preserves non-ASCII codepoints (e.g. <c>é</c>, <c>中</c>, emoji) that the
/// <see cref="Key"/> + <see cref="Modifiers"/> pair cannot round-trip through a US layout.
/// Null for navigation keys, control bytes (Tab, Enter, Backspace, …), and mouse events.
/// </para>
/// </summary>
public readonly record struct ConsoleInputEvent(
    MouseEvent? Mouse,
    ConsoleKey Key,
    ConsoleModifiers Modifiers,
    Rune? KeyChar = null)
{
    /// <summary>
    /// The terminal reported focus-out (<c>ESC[O</c>, mode 1004), which is the only leave signal an SGR-tracking
    /// terminal gives. Maps to <c>InputEvent.PointerLeft</c>. An init property, not a positional parameter, so the
    /// constructor and <c>Deconstruct</c> consumers compiled against stay as they were.
    /// </summary>
    public bool PointerLeft { get; init; }
}
