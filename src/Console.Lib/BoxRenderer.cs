using System.Text;
using DIR.Lib.MathLayout;

namespace Console.Lib;

/// <summary>
/// Encoding to use when shipping a rasterized <see cref="Box"/> to the
/// terminal. Sixel gives true raster fidelity but needs terminal support;
/// Sextant uses Unicode 13 sub-pixel block characters (~3× denser than
/// half-block); HalfBlock uses U+2580 (universal but coarse).
/// </summary>
public enum BoxRenderMode
{
    Sixel,
    Sextant,
    HalfBlock,
}

/// <summary>
/// Terminal-output adapter for the upstream <see cref="DIR.Lib.MathLayout"/>
/// box engine. Rasterises a <see cref="Box"/> via
/// <see cref="BoxRasterizer.RenderToRgba"/> and emits the result in one of
/// three encodings:
/// <list type="bullet">
///   <item><b>Sixel</b> — true raster, 6 sub-pixels per cell, 24-bit colour.
///         Best fidelity. Requires a sixel-capable terminal.</item>
///   <item><b>Sextant</b> — Unicode 13 block characters (U+1FB00–U+1FB3F)
///         packing a 2×3 sub-pixel grid into each cell. ~3× denser than
///         half-block both vertically and per-formula. Renders on modern
///         Windows Terminal, iTerm2, mintty, kitty etc.</item>
///   <item><b>HalfBlock</b> — Unicode upper-half (U+2580) with 2 sub-pixels
///         per cell. Universal but coarse and tall on screen.</item>
/// </list>
///
/// All three encodings are transparency-aware: pixels with zero alpha emit
/// no background-colour SGR (and contribute no sixel bit), so the box floats
/// over the terminal's natural background instead of sitting on a hard
/// black square.
/// </summary>
public static class BoxRenderer
{
    /// <summary>
    /// Render <paramref name="box"/> at <paramref name="style"/> into a
    /// transparent RGBA buffer and ship the encoded result to
    /// <paramref name="output"/>. For <see cref="BoxRenderMode.Sixel"/> the
    /// raw escape sequence is written to stdout (the sixel encoder bypasses
    /// the TextWriter so the binary payload doesn't get re-encoded by
    /// Console.OutputEncoding); for the text encodings the result is written
    /// directly to <paramref name="output"/>.
    /// </summary>
    public static void Render(Box box, BoxStyle style, BoxRenderMode mode, TextWriter output)
    {
        var image = BoxRasterizer.RenderToRgba(box, style);
        if (image.Width <= 0 || image.Height <= 0) return;

        var rgba = image.Pixels;
        var totalW = image.Width;
        var totalH = image.Height;

        switch (mode)
        {
            case BoxRenderMode.Sixel:
                output.Flush();
                using (var stdout = System.Console.OpenStandardOutput())
                {
                    SixelEncoder.Encode(rgba, totalW, totalH, channels: 4, stdout);
                    stdout.Flush();
                }
                output.WriteLine();
                break;

            case BoxRenderMode.Sextant:
                EncodeSextant(rgba, totalW, totalH, output);
                break;

            case BoxRenderMode.HalfBlock:
                EncodeHalfBlock(rgba, totalW, totalH, output);
                break;
        }
    }

    /// <summary>
    /// Transparency-aware half-block (▀ U+2580) encoder. Each terminal cell
    /// represents 2 source pixels stacked vertically. Foreground colour is
    /// drawn from the upper sub-pixel; background from the lower. When a
    /// sub-pixel's alpha is 0, that channel is omitted from the SGR payload —
    /// so a fully-transparent cell becomes a plain space and the terminal's
    /// natural background shows through.
    /// </summary>
    public static void EncodeHalfBlock(byte[] pixels, int width, int height, TextWriter output)
    {
        var sb = new StringBuilder(width * 8);
        for (var y = 0; y < height; y += 2)
        {
            sb.Clear();
            for (var x = 0; x < width; x++)
            {
                var (tr, tg, tb, ta) = ReadPixel(pixels, width, height, x, y);
                var (br, bg, bb, ba) = ReadPixel(pixels, width, height, x, y + 1);

                if (ta == 0 && ba == 0)
                {
                    sb.Append(' ');
                }
                else if (ta != 0 && ba == 0)
                {
                    sb.Append($"\x1b[38;2;{tr};{tg};{tb}m▀\x1b[0m");
                }
                else if (ta == 0 && ba != 0)
                {
                    sb.Append($"\x1b[38;2;{br};{bg};{bb}m▄\x1b[0m");
                }
                else
                {
                    sb.Append($"\x1b[38;2;{tr};{tg};{tb};48;2;{br};{bg};{bb}m▀\x1b[0m");
                }
            }
            output.WriteLine(sb.ToString());
        }
    }

    /// <summary>
    /// Sextant-block encoder: each cell carries a 2×3 sub-pixel grid, mapped
    /// to a Unicode 13.0 sextant character. Roughly 3× denser than half-block
    /// vertically and 1.5× horizontally — formulas come out about a third the
    /// height of the half-block path on the same source buffer. The colour
    /// for a cell is the average of its filled sub-pixels (so we still emit
    /// only one foreground SGR per cell).
    ///
    /// Sextants are encoded with a 6-bit pattern (TL, TR, ML, MR, BL, BR);
    /// the empty pattern (0) and the all-set pattern (63) are special-cased
    /// to space and U+2588 (full block) respectively, because sextants
    /// 0/63 don't have dedicated codepoints.
    /// </summary>
    public static void EncodeSextant(byte[] pixels, int width, int height, TextWriter output)
    {
        var sb = new StringBuilder(width * 4);
        for (var y = 0; y < height; y += 3)
        {
            sb.Clear();
            for (var x = 0; x < width; x += 2)
            {
                int rSum = 0, gSum = 0, bSum = 0, count = 0, mask = 0;
                for (var dy = 0; dy < 3; dy++)
                {
                    for (var dx = 0; dx < 2; dx++)
                    {
                        var (r, g, b, a) = ReadPixel(pixels, width, height, x + dx, y + dy);
                        if (a == 0) continue;
                        // Bit ordering: TL=0, TR=1, ML=2, MR=3, BL=4, BR=5.
                        mask |= 1 << (dy * 2 + dx);
                        rSum += r; gSum += g; bSum += b; count++;
                    }
                }
                if (count == 0) { sb.Append(' '); continue; }
                int rA = rSum / count, gA = gSum / count, bA = bSum / count;
                sb.Append($"\x1b[38;2;{rA};{gA};{bA}m{SextantChar(mask)}\x1b[0m");
            }
            output.WriteLine(sb.ToString());
        }
    }

    /// <summary>
    /// 2×3 sextant bitmask → Unicode codepoint. The mask bit layout follows
    /// the spec: bit 0 = TL, 1 = TR, 2 = ML, 3 = MR, 4 = BL, 5 = BR.
    /// Patterns 0 and 63 (all empty / all filled) map to space and full
    /// block respectively — they don't have unique codepoints in the
    /// sextant block. Patterns 21 (= LeftHalf) and 42 (= RightHalf) map to
    /// the half-block characters U+258C / U+2590 by spec, not to a sextant.
    /// </summary>
    private static string SextantChar(int mask) => mask switch
    {
        0  => " ",
        63 => "█", // FULL BLOCK
        21 => "▌", // LEFT HALF BLOCK
        42 => "▐", // RIGHT HALF BLOCK
        _  => char.ConvertFromUtf32(0x1FB00 + SextantIndex(mask)),
    };

    /// <summary>
    /// Convert the 6-bit sextant mask into the 0..59 codepoint offset within
    /// the U+1FB00..U+1FB3B range. The Unicode block omits the four masks
    /// that have dedicated block-character codepoints elsewhere (0=space,
    /// 21=left-half, 42=right-half, 63=full-block); for those, callers must
    /// not call this helper.
    /// </summary>
    private static int SextantIndex(int mask)
    {
        // Masks below 21 just map directly. After 21 (LEFT HALF), the
        // sequence shifts down by 1. After 42 (RIGHT HALF), shift down by
        // another 1.
        var idx = mask - 1;        // skip 0 (space)
        if (mask > 21) idx--;      // skip 21 (left half)
        if (mask > 42) idx--;      // skip 42 (right half)
        return idx;
    }

    private static (byte r, byte g, byte b, byte a) ReadPixel(byte[] pixels, int width, int height, int x, int y)
    {
        if (x < 0 || y < 0 || x >= width || y >= height) return (0, 0, 0, 0);
        var i = (y * width + x) * 4;
        return (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]);
    }
}
