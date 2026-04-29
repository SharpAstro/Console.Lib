using System.Text;

namespace Console.Lib;

/// <summary>
/// Terminal-side clipboard interaction via OSC 52 (the "set selection"
/// escape). The terminal itself talks to the host clipboard, so this works
/// without any platform-specific clipboard library and is AOT-friendly.
///
/// Supported by Windows Terminal, iTerm2, kitty, foot, alacritty, wezterm,
/// xterm, gnome-terminal, etc. Some terminals require an opt-in setting; if
/// nothing lands on the clipboard, that's a terminal config issue, not an
/// emission issue.
///
/// Useful in TUIs where part of the screen isn't selectable via the
/// terminal's own drag-select — most commonly Sixel-rendered content, but
/// also any region behind alternate-screen mouse-capture mode.
/// </summary>
public static class Clipboard
{
    /// <summary>
    /// Writes <paramref name="text"/> to the system clipboard via OSC 52.
    /// The viewport is flushed after the escape so the terminal sees it
    /// immediately rather than buffering it behind the next frame.
    /// </summary>
    public static void SetText(ITerminalViewport viewport, string text)
    {
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
        viewport.Write($"\u001b]52;c;{b64}\u0007");
        viewport.Flush();
    }
}
