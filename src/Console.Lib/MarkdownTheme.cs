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

    public static MarkdownTheme Default { get; } = new();

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
