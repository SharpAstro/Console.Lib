using System.Globalization;
using DIR.Lib;

namespace Console.Lib;

/// <summary>
/// Defines the color palette used by <see cref="MarkdownRenderer"/> for structural elements.
/// All colors are resolved at render time via <see cref="Resolve"/> to respect the active <see cref="ColorMode"/>.
/// </summary>
public record MarkdownTheme
{
    public RGBAColor32 Heading1 { get; init; } = SgrColor.Blue.ToRgba();
    public RGBAColor32 Heading2 { get; init; } = SgrColor.Cyan.ToRgba();
    public RGBAColor32 Heading3 { get; init; } = SgrColor.BrightWhite.ToRgba();
    public RGBAColor32 Link { get; init; } = SgrColor.Cyan.ToRgba();
    public RGBAColor32 Bullet { get; init; } = SgrColor.Cyan.ToRgba();
    public RGBAColor32 Dim { get; init; } = SgrColor.BrightBlack.ToRgba();
    /// <summary>Color used for inline code spans (<c>`x`</c>) and the body of fenced code blocks.</summary>
    public RGBAColor32 Code { get; init; } = SgrColor.BrightYellow.ToRgba();
    /// <summary>Color used for math content (inline <c>$x$</c> and display <c>$$x$$</c>) when rendered via the LaTeX visitor.</summary>
    public RGBAColor32 Math { get; init; } = SgrColor.BrightMagenta.ToRgba();

    public static MarkdownTheme Default { get; } = new();

    /// <summary>
    /// A "GitHub Dark"-inspired 24-bit palette for terminals that support
    /// truecolor. These exact hex tones look right only with a full RGB
    /// channel — on a 16-colour (<see cref="ColorMode.Sgr16"/>) terminal they
    /// snap to imprecise approximations, so callers should fall back to
    /// <see cref="Default"/> (which is built from exact <see cref="SgrColor"/>
    /// values) there. Tuned for dark backgrounds.
    /// </summary>
    public static MarkdownTheme Modern { get; } = new()
    {
        Heading1 = new RGBAColor32(0x58, 0xa6, 0xff, 0xff), // #58a6ff
        Heading2 = new RGBAColor32(0x79, 0xc0, 0xff, 0xff), // #79c0ff
        Heading3 = new RGBAColor32(0xa5, 0xd6, 0xff, 0xff), // #a5d6ff
        Link     = new RGBAColor32(0x58, 0xa6, 0xff, 0xff), // #58a6ff
        Bullet   = new RGBAColor32(0x58, 0xa6, 0xff, 0xff), // #58a6ff
        Dim      = new RGBAColor32(0x8b, 0x94, 0x9e, 0xff), // #8b949e
        Code     = new RGBAColor32(0xd2, 0xa8, 0xff, 0xff), // #d2a8ff
        Math     = new RGBAColor32(0x56, 0xd4, 0xdd, 0xff), // #56d4dd
    };

    /// <summary>
    /// Emits the foreground VT escape for <paramref name="color"/> in the given <paramref name="mode"/>,
    /// or an empty string when <paramref name="mode"/> is <see cref="ColorMode.None"/>.
    /// </summary>
    public static string Resolve(RGBAColor32 color, ColorMode mode) =>
        new VtStyle(color, default).ApplyFg(mode);

    /// <summary>
    /// Parses a color string that is either a named <see cref="SgrColor"/> (case-insensitive)
    /// or a hex literal (<c>#RRGGBB</c>).
    /// </summary>
    public static RGBAColor32 ParseColor(string value)
    {
        if (value.StartsWith('#') && value.Length == 7
            && byte.TryParse(value.AsSpan(1, 2), NumberStyles.HexNumber, null, out var r)
            && byte.TryParse(value.AsSpan(3, 2), NumberStyles.HexNumber, null, out var g)
            && byte.TryParse(value.AsSpan(5, 2), NumberStyles.HexNumber, null, out var b))
        {
            return new RGBAColor32(r, g, b, 0xff);
        }

        if (Enum.TryParse<SgrColor>(value, ignoreCase: true, out var sgr))
            return sgr.ToRgba();

        throw new ArgumentException($"Unknown color: '{value}'. Use a SgrColor name or #RRGGBB hex.", nameof(value));
    }

    /// <summary>
    /// Tries to parse a color string. Returns false if the format is unrecognized.
    /// </summary>
    public static bool TryParseColor(string value, out RGBAColor32 color)
    {
        try
        {
            color = ParseColor(value);
            return true;
        }
        catch (ArgumentException)
        {
            color = default;
            return false;
        }
    }
}
