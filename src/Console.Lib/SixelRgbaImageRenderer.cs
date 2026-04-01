using DIR.Lib;

namespace Console.Lib;

/// <summary>
/// Extends <see cref="RgbaImageRenderer"/> (from DIR.Lib) with Sixel encoding.
/// Drop-in replacement for the former <c>RgbaImageRenderer</c> that lived in Console.Lib.
/// </summary>
public sealed class SixelRgbaImageRenderer(uint width, uint height)
    : RgbaImageRenderer(width, height), ISixelEncoder
{
    public void EncodeSixel(Stream output)
        => SixelEncoder.Encode(Surface.Pixels, Surface.Width, Surface.Height, 4, output);

    public void EncodeSixel(int startY, uint height1, Stream output)
    {
        var w = Surface.Width;
        var h = (int)Math.Min(height1, Surface.Height - startY);
        if (h <= 0) return;

        // Extract the sub-region
        var regionSize = w * h * 4;
        var region = new byte[regionSize];
        Buffer.BlockCopy(Surface.Pixels, startY * w * 4, region, 0, regionSize);
        SixelEncoder.Encode(region, w, h, 4, output);
    }
}
