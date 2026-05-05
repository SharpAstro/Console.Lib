using System.IO;
using System.Text;

namespace Console.Lib.MathLayout;

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
/// Allocates an RGBA buffer just large enough for a Box, draws the box, and
/// emits the result to the terminal in one of three encodings:
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
/// The half-block and sextant encoders are transparency-aware: pixels with
/// zero alpha emit no background-colour SGR, so the box floats over the
/// terminal's natural background instead of sitting on a hard black square.
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
        var (renderer, totalW, totalH) = Rasterize(box, style);
        if (renderer is null) return;
        using (renderer)
        {
            switch (mode)
            {
                case BoxRenderMode.Sixel:
                    output.Flush();
                    using (var stdout = System.Console.OpenStandardOutput())
                    {
                        renderer.EncodeSixel(stdout);
                        stdout.Flush();
                    }
                    output.WriteLine();
                    break;

                case BoxRenderMode.Sextant:
                    EncodeSextant(renderer.Surface.Pixels, totalW, totalH, output);
                    break;

                case BoxRenderMode.HalfBlock:
                    EncodeHalfBlock(renderer.Surface.Pixels, totalW, totalH, output);
                    break;
            }
        }
    }

    /// <summary>
    /// Rasterize <paramref name="box"/> at <paramref name="style"/> into a
    /// transparent 8-bit RGBA buffer (row-major, no padding) and return it
    /// along with its dimensions. Useful for unit/golden-image testing or
    /// for callers that want to post-process the bitmap themselves before
    /// shipping it to the terminal.
    /// </summary>
    public static (byte[] Rgba, int Width, int Height) RenderToRgba(Box box, BoxStyle style)
    {
        var (renderer, totalW, totalH) = Rasterize(box, style);
        if (renderer is null) return ([], 0, 0);
        using (renderer)
        {
            // Surface.Pixels is owned by the renderer and freed on dispose;
            // copy into a stable array before returning to the caller.
            var pixels = renderer.Surface.Pixels;
            var copy = new byte[pixels.Length];
            Buffer.BlockCopy(pixels, 0, copy, 0, pixels.Length);
            return (copy, totalW, totalH);
        }
    }

    /// <summary>
    /// Rasterize <paramref name="box"/> and encode the result as a PNG,
    /// returning the file bytes. Returns an empty array if the box has zero
    /// area.
    /// </summary>
    public static byte[] RenderToPng(Box box, BoxStyle style)
    {
        var (rgba, w, h) = RenderToRgba(box, style);
        if (w == 0 || h == 0) return [];
        return DIR.Lib.PngWriter.Encode(rgba, w, h);
    }

    /// <summary>
    /// Common box → bitmap step shared by all the public Render* entry
    /// points. Returns <c>null</c> when the box would rasterize to zero
    /// area, otherwise a fully-drawn renderer the caller is responsible for
    /// disposing.
    /// </summary>
    private static (SixelRgbaImageRenderer? Renderer, int Width, int Height) Rasterize(Box box, BoxStyle style)
    {
        int margin = (int)MathF.Ceiling(style.FontSize * 0.15f);
        int totalW = (int)MathF.Ceiling(box.Width) + margin * 2;
        int totalH = (int)MathF.Ceiling(box.TotalHeight) + margin * 2;
        if (totalW <= 0 || totalH <= 0) return (null, 0, 0);

        // Buffer starts transparent (RGBA 0,0,0,0). Sixel still uses the
        // alpha channel correctly; the text encoders below check alpha to
        // decide whether to draw a sub-pixel.
        var renderer = new SixelRgbaImageRenderer((uint)totalW, (uint)totalH);
        float baselineY = margin + box.Height;
        box.Draw(renderer, margin, baselineY, style);
        return (renderer, totalW, totalH);
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
