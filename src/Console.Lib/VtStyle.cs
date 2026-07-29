using DIR.Lib;

namespace Console.Lib;

/// <summary>
/// Standard SGR (Select Graphic Rendition) colors for terminal text.
/// </summary>
public enum SgrColor : byte
{
    Black, Red, Green, Yellow, Blue, Magenta, Cyan, White,
    BrightBlack, BrightRed, BrightGreen, BrightYellow,
    BrightBlue, BrightMagenta, BrightCyan, BrightWhite,
}

public static class SgrColorExtensions
{
    private static readonly RGBAColor32[] SgrToRgba =
    [
        new(0x00, 0x00, 0x00, 0xff), // Black
        new(0xaa, 0x00, 0x00, 0xff), // Red
        new(0x00, 0xaa, 0x00, 0xff), // Green
        new(0xaa, 0x55, 0x00, 0xff), // Yellow (dark)
        new(0x00, 0x00, 0xaa, 0xff), // Blue
        new(0xaa, 0x00, 0xaa, 0xff), // Magenta
        new(0x00, 0xaa, 0xaa, 0xff), // Cyan
        new(0xaa, 0xaa, 0xaa, 0xff), // White
        new(0x55, 0x55, 0x55, 0xff), // BrightBlack
        new(0xff, 0x55, 0x55, 0xff), // BrightRed
        new(0x55, 0xff, 0x55, 0xff), // BrightGreen
        new(0xff, 0xff, 0x55, 0xff), // BrightYellow
        new(0x55, 0x55, 0xff, 0xff), // BrightBlue
        new(0xff, 0x55, 0xff, 0xff), // BrightMagenta
        new(0x55, 0xff, 0xff, 0xff), // BrightCyan
        new(0xff, 0xff, 0xff, 0xff), // BrightWhite
    ];

    public static RGBAColor32 ToRgba(this SgrColor color) => SgrToRgba[(int)color];

    /// <summary>
    /// Finds the nearest SGR color for the given RGBA color using Euclidean distance in RGB space.
    /// </summary>
    public static SgrColor NearestSgrColor(RGBAColor32 color)
    {
        var bestIdx = 0;
        var bestDist = int.MaxValue;
        for (var i = 0; i < SgrToRgba.Length; i++)
        {
            var c = SgrToRgba[i];
            var dr = color.Red - c.Red;
            var dg = color.Green - c.Green;
            var db = color.Blue - c.Blue;
            var dist = dr * dr + dg * dg + db * db;
            if (dist < bestDist)
            {
                bestDist = dist;
                bestIdx = i;
            }
        }
        return (SgrColor)bestIdx;
    }
}

/// <summary>
/// Controls how <see cref="VtStyle"/> emits color escape sequences.
/// </summary>
public enum ColorMode : byte
{
    /// <summary>No color escapes emitted.</summary>
    None,
    /// <summary>16-color SGR codes (works everywhere).</summary>
    Sgr16,
    /// <summary>24-bit truecolor via <c>\e[38;2;R;G;Bm</c> / <c>\e[48;2;R;G;Bm</c>.</summary>
    TrueColor,
}

/// <summary>
/// A terminal text style represented as a foreground/background color pair.
/// Use <see cref="Apply"/> to produce the appropriate escape sequence for the terminal's color mode.
/// </summary>
public readonly record struct VtStyle(RGBAColor32 Foreground, RGBAColor32 Background)
{
    public const string Reset = "\e[0m";
    public const string ReverseOn = "\e[7m";
    public const string ReverseOff = "\e[27m";

    public VtStyle(SgrColor foreground, SgrColor background)
        : this(foreground.ToRgba(), background.ToRgba()) { }

    private static int FgCode(SgrColor c) => (int)c < 8 ? 30 + (int)c : 82 + (int)c;
    private static int BgCode(SgrColor c) => (int)c < 8 ? 40 + (int)c : 92 + (int)c;

    /// <summary>
    /// Whether a component names a colour at all.
    /// <para>
    /// <b>Alpha zero is not transparent black — a terminal cell does not composite, so there is nothing to
    /// be transparent against.</b> It means the component was never stated, and what should show is the
    /// terminal's own default. Emitting it as a colour paints opaque BLACK instead, which is how text drawn
    /// over a painted background destroyed it: the glyph carried an unstated background, the pen stated it
    /// as black, and the fill underneath was gone.
    /// </para>
    /// </summary>
    private static bool IsUnstated(RGBAColor32 color) => color.Alpha == 0;

    /// <summary>SGR "default foreground" — the counterpart to 30-37 for a component with no colour.</summary>
    private const string DefaultFgParam = "39";

    /// <summary>SGR "default background" — the counterpart to 40-47.</summary>
    private const string DefaultBgParam = "49";

    private string FgParams(ColorMode colorMode) => IsUnstated(Foreground)
        ? DefaultFgParam
        : colorMode == ColorMode.TrueColor
            ? $"38;2;{Foreground.Red};{Foreground.Green};{Foreground.Blue}"
            : $"{FgCode(SgrColorExtensions.NearestSgrColor(Foreground))}";

    private string BgParams(ColorMode colorMode) => IsUnstated(Background)
        ? DefaultBgParam
        : colorMode == ColorMode.TrueColor
            ? $"48;2;{Background.Red};{Background.Green};{Background.Blue}"
            : $"{BgCode(SgrColorExtensions.NearestSgrColor(Background))}";

    /// <summary>
    /// Returns the VT escape sequence for this style in the given <paramref name="colorMode"/>. States BOTH
    /// components, so nothing is left to inherit from whatever was written before — see
    /// <see cref="IsUnstated"/> for why a component with no colour becomes 39/49 rather than black.
    /// </summary>
    public string Apply(ColorMode colorMode) => colorMode switch
    {
        ColorMode.None => "",
        _ => $"\e[{FgParams(colorMode)};{BgParams(colorMode)}m",
    };

    /// <summary>
    /// Returns the VT escape sequence for the foreground color only, leaving the background as whatever the
    /// terminal currently has.
    /// <para>
    /// <b>Prefer <see cref="Apply"/> for anything a <see cref="CellBuffer"/> will record.</b> Inheriting the
    /// background works on a live terminal, where the leftover state is genuinely what is on screen, but a
    /// cell buffer has to name a colour per cell and can only record the pen it was told about.
    /// </para>
    /// </summary>
    public string ApplyFg(ColorMode colorMode) => colorMode switch
    {
        ColorMode.None => "",
        _ => $"\e[{FgParams(colorMode)}m",
    };

    /// <summary>
    /// Default <see cref="ToString"/> uses SGR-16 for maximum compatibility.
    /// Prefer <see cref="Apply"/> when the terminal's <see cref="ColorMode"/> is known.
    /// </summary>
    public override string ToString() => Apply(ColorMode.Sgr16);
}
