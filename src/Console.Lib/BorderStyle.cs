namespace Console.Lib;

/// <summary>
/// Box-drawing character families for terminal borders, rules and tables.
/// </summary>
public enum BorderStyle
{
    /// <summary>Single thin lines (U+250x). The default, and what Markdown tables have always used.</summary>
    Light,

    /// <summary>Thick lines (U+250F..U+254B). Reads as emphasis next to <see cref="Light"/>.</summary>
    Heavy,

    /// <summary>Double lines (U+2550..U+256C).</summary>
    Double,

    /// <summary>
    /// Thin lines with arc corners (U+256D..U+2570) -- the terminal's answer to a rounded rectangle.
    /// </summary>
    Rounded,

    /// <summary>
    /// <c>+</c>, <c>-</c> and <c>|</c>. For a terminal (or a pipe destination) that cannot be trusted
    /// with box-drawing characters at all.
    /// </summary>
    Ascii,
}

/// <summary>
/// The eleven characters a bordered box or table needs, resolved for one <see cref="BorderStyle"/>.
/// </summary>
/// <param name="TopLeft">Top-left corner.</param>
/// <param name="TopRight">Top-right corner.</param>
/// <param name="BottomLeft">Bottom-left corner.</param>
/// <param name="BottomRight">Bottom-right corner.</param>
/// <param name="Horizontal">Horizontal run (top and bottom edges, rules, separators).</param>
/// <param name="Vertical">Vertical run (side edges, column dividers).</param>
/// <param name="TeeDown">Top edge meeting a column divider.</param>
/// <param name="TeeUp">Bottom edge meeting a column divider.</param>
/// <param name="TeeRight">Left edge meeting a row separator.</param>
/// <param name="TeeLeft">Right edge meeting a row separator.</param>
/// <param name="Cross">A row separator meeting a column divider.</param>
public readonly record struct BorderChars(
    char TopLeft,
    char TopRight,
    char BottomLeft,
    char BottomRight,
    char Horizontal,
    char Vertical,
    char TeeDown,
    char TeeUp,
    char TeeRight,
    char TeeLeft,
    char Cross)
{
    /// <summary>
    /// The character set for <paramref name="style"/>.
    /// <para>
    /// <see cref="BorderStyle.Rounded"/> deliberately shares <see cref="BorderStyle.Light"/>'s tees and cross:
    /// Unicode has arc forms for the four <i>corners</i> only (U+256D..U+2570), and no rounded junction
    /// exists to pair with them. A rounded box is therefore light lines with arc corners, which is exactly
    /// how every terminal UI that offers the style draws it.
    /// </para>
    /// </summary>
    public static BorderChars For(BorderStyle style) => style switch
    {
        BorderStyle.Heavy => new BorderChars('┏', '┓', '┗', '┛', '━', '┃', '┳', '┻', '┣', '┫', '╋'),
        BorderStyle.Double => new BorderChars('╔', '╗', '╚', '╝', '═', '║', '╦', '╩', '╠', '╣', '╬'),
        BorderStyle.Rounded => new BorderChars('╭', '╮', '╰', '╯', '─', '│', '┬', '┴', '├', '┤', '┼'),
        BorderStyle.Ascii => new BorderChars('+', '+', '+', '+', '-', '|', '+', '+', '+', '+', '+'),
        _ => new BorderChars('┌', '┐', '└', '┘', '─', '│', '┬', '┴', '├', '┤', '┼'),
    };
}
