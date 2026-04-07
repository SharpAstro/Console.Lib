namespace Console.Lib;

/// <summary>
/// Describes how the terminal can display images, independent of redirection.
/// Sixel data can in principle be written even when output is redirected,
/// but layout helpers like <c>Console.Width</c> are unavailable.
/// </summary>
public enum ImageDisplayCapability : byte
{
    /// <summary>No color — <c>NO_COLOR</c> is set or color capability is absent.</summary>
    NoColor,

    /// <summary>No Sixel, but color is available — use ASCII block characters.</summary>
    AsciiBlock,

    /// <summary>Terminal reports Sixel graphics support.</summary>
    Sixel
}
