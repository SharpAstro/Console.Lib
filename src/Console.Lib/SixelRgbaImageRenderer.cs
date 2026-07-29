using DIR.Lib;

namespace Console.Lib;

/// <summary>
/// Extends <see cref="RgbaImageRenderer"/> (from DIR.Lib) with Sixel encoding.
/// Drop-in replacement for the former <c>RgbaImageRenderer</c> that lived in Console.Lib.
/// </summary>
public sealed class SixelRgbaImageRenderer(uint width, uint height)
    : RgbaImageRenderer(width, height), ISixelEncoder
{
    /// <summary>
    /// Colours that must survive quantisation, whatever their pixel count — an app's SEMANTIC palette
    /// (square fills, selection, check, last-move accents) as opposed to the antialiasing shades that
    /// otherwise win the frequency contest by sheer area. See <see cref="SixelEncoder.Encode"/> for what
    /// goes wrong without it and why the index also needs to be stable.
    /// <para>
    /// Set once by the host and honoured by BOTH encode paths, which is the point: a partial strip
    /// otherwise ranks colours against that strip's histogram alone and can represent the same accent
    /// differently from the full frame before it.
    /// </para>
    /// </summary>
    public RGBAColor32[] ReservedColors { get; set; } = [];

    public void EncodeSixel(Stream output)
        => SixelEncoder.Encode(Surface.Pixels, Surface.Width, Surface.Height, 4, output, ReservedColors);

    public void EncodeSixel(int startY, uint height1, Stream output)
    {
        var w = Surface.Width;
        var h = (int)Math.Min(height1, Surface.Height - startY);
        if (h <= 0) return;

        // Extract the sub-region
        var regionSize = w * h * 4;
        var region = new byte[regionSize];
        Buffer.BlockCopy(Surface.Pixels, startY * w * 4, region, 0, regionSize);
        SixelEncoder.Encode(region, w, h, 4, output, ReservedColors);
    }
}
