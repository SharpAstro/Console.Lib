namespace Console.Lib;

/// <summary>
/// Interface for renderers that can encode their surface as Sixel graphics.
/// Replaces the former <c>SixelRenderer&lt;TSurface&gt;</c> abstract class,
/// allowing any <see cref="DIR.Lib.Renderer{TSurface}"/> to add Sixel output via composition.
/// </summary>
public interface ISixelEncoder
{
    /// <summary>Renderer height in pixels (needed for partial-region clamping).</summary>
    uint Height { get; }

    /// <summary>Encodes the full surface as Sixel data.</summary>
    void EncodeSixel(Stream output);

    /// <summary>Encodes a horizontal strip starting at <paramref name="startY"/>.</summary>
    void EncodeSixel(int startY, uint height, Stream output);
}
