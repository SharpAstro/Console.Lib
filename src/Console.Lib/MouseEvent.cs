namespace Console.Lib;

/// <summary>
/// Represents a mouse button event with pixel position and press/release state.
/// <see cref="IsMotion"/> is set when the terminal emits a drag report
/// (xterm mode 1002: button-held-while-moving), in which case <see cref="Button"/>
/// is the button held during the motion and <see cref="IsRelease"/> is false.
/// </summary>
public readonly record struct MouseEvent(int Button, int X, int Y, bool IsRelease)
{
    /// <summary>
    /// True when this event reports motion with a button held (drag), false for
    /// a plain click / release / wheel event. Mapped from xterm CSI bit 5 (0x20).
    /// </summary>
    public bool IsMotion { get; init; }
}
