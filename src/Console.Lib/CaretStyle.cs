namespace Console.Lib;

/// <summary>
/// Shape of the terminal's REAL cursor when a widget parks it at the insertion point
/// (<see cref="ITerminalViewport.SetCaret"/>) instead of painting a reverse-video cell. The values are
/// DECSCUSR parameters (<c>ESC [ Ps SP q</c>), emitted verbatim — which is the point of the feature: the
/// terminal draws and blinks the caret itself, so a bar can be thinner than a cell and blinking costs no
/// repaint traffic, neither of which any cell-grid paint can imitate. A terminal without DECSCUSR ignores
/// the shape and shows its default cursor at the parked cell — degraded to a block, never wrong.
/// </summary>
public enum CaretStyle
{
    /// <summary>DECSCUSR 1 — blinking block.</summary>
    BlinkingBlock = 1,
    /// <summary>DECSCUSR 2 — steady block.</summary>
    SteadyBlock = 2,
    /// <summary>DECSCUSR 3 — blinking underline.</summary>
    BlinkingUnderline = 3,
    /// <summary>DECSCUSR 4 — steady underline.</summary>
    SteadyUnderline = 4,
    /// <summary>DECSCUSR 5 — blinking bar: the thin editor caret.</summary>
    BlinkingBar = 5,
    /// <summary>DECSCUSR 6 — steady bar.</summary>
    SteadyBar = 6,
}
