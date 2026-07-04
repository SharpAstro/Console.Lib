using System;
using SharpAstro.Jpeg;
using SharpAstro.Png;

namespace Console.Lib;

/// <summary>
/// Minimal image sniffer + decoder for inline markdown images. Detects the format
/// from its leading magic-byte signature (a <see cref="ReadOnlySpan{T}.SequenceEqual(ReadOnlySpan{T})"/>
/// on the first few bytes) and dispatches to the matching focused SharpAstro codec,
/// returning tightly-packed 8-bit RGBA (row-major, 4 bytes/pixel).
///
/// <para>Supports <b>PNG</b> and <b>JPEG</b> — the formats realistically embedded in
/// terminal markdown. Anything else (BMP / GIF / TGA / …) or malformed input yields
/// <c>false</c>, and the caller falls back to alt-text. This replaces a dependency on
/// the general-purpose stb_image port: PNG/JPEG are covered by SharpAstro.Png /
/// SharpAstro.Jpeg, which the rest of the SharpAstro line already uses.</para>
/// </summary>
internal static class ImageDecoder
{
    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static ReadOnlySpan<byte> JpegSignature => [0xFF, 0xD8, 0xFF];

    /// <summary>
    /// Decode PNG or JPEG bytes to 8-bit RGBA. Returns false for unsupported formats
    /// or undecodable input (the caller treats that as "no raster available").
    /// </summary>
    public static bool TryDecodeRgba(ReadOnlySpan<byte> bytes, out byte[] rgba, out int width, out int height)
    {
        rgba = Array.Empty<byte>();
        width = 0;
        height = 0;
        try
        {
            if (bytes.Length >= PngSignature.Length && bytes[..PngSignature.Length].SequenceEqual(PngSignature))
            {
                var img = PngReader.Decode(bytes);
                if (img.Width <= 0 || img.Height <= 0) return false;
                var px = ToRgba8(img);
                if (px is null) return false;
                rgba = px;
                width = img.Width;
                height = img.Height;
                return true;
            }
            if (bytes.Length >= JpegSignature.Length && bytes[..JpegSignature.Length].SequenceEqual(JpegSignature))
            {
                var img = JpegDecoder.Decode(bytes);
                if (img.Width <= 0 || img.Height <= 0) return false;
                rgba = img.Pixels; // already tightly-packed 8-bit RGBA
                width = img.Width;
                height = img.Height;
                return true;
            }
        }
        catch
        {
            // Malformed / unsupported payload — fall through to the alt-text path.
        }
        return false;
    }

    /// <summary>
    /// Expand a decoded <see cref="PngImage"/> into tightly-packed 8-bit RGBA. Handles
    /// greyscale / RGB / greyscale+alpha / RGBA / indexed (PLTE + tRNS); 16-bit samples
    /// truncate to their high byte (PNG stores 16-bit big-endian). Returns null for a
    /// colour type this can't expand.
    /// </summary>
    private static byte[]? ToRgba8(PngImage img)
    {
        var w = img.Width;
        var h = img.Height;
        var src = img.Pixels;
        var spp = img.SamplesPerPixel;
        var step = img.BitDepth == 16 ? 2 : 1; // bytes per sample (high byte first for 16-bit)
        var rowBytes = w * spp * step;
        var dst = new byte[w * h * 4];

        for (var y = 0; y < h; y++)
        {
            var srcRow = y * rowBytes;
            var dstRow = y * w * 4;
            for (var x = 0; x < w; x++)
            {
                var s = srcRow + x * spp * step;
                byte r, g, b, a;
                switch (img.ColorType)
                {
                    case 0: // greyscale
                        r = g = b = src[s];
                        a = 255;
                        break;
                    case 2: // RGB
                        r = src[s];
                        g = src[s + step];
                        b = src[s + 2 * step];
                        a = 255;
                        break;
                    case 3: // indexed (palette): src[s] is an index into Palette (+ optional PaletteAlpha)
                        var pal = img.Palette;
                        if (pal is null) return null;
                        var idx = src[s];
                        var pi = idx * 3;
                        if (pi + 2 >= pal.Length) return null;
                        r = pal[pi];
                        g = pal[pi + 1];
                        b = pal[pi + 2];
                        a = img.PaletteAlpha is { } pa && idx < pa.Length ? pa[idx] : (byte)255;
                        break;
                    case 4: // greyscale + alpha
                        r = g = b = src[s];
                        a = src[s + step];
                        break;
                    case 6: // RGBA
                        r = src[s];
                        g = src[s + step];
                        b = src[s + 2 * step];
                        a = src[s + 3 * step];
                        break;
                    default:
                        return null;
                }
                var d = dstRow + x * 4;
                dst[d] = r;
                dst[d + 1] = g;
                dst[d + 2] = b;
                dst[d + 3] = a;
            }
        }
        return dst;
    }
}
