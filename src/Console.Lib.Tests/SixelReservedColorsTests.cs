using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using DIR.Lib;
using Shouldly;
using Xunit;

namespace Console.Lib.Tests;

/// <summary>
/// Reserved palette entries: colours that must survive quantisation on merit rather than on area.
///
/// <para>The encoder picks its 255 slots by pixel FREQUENCY, which is the right default for large flat
/// regions and the wrong one for anything that carries meaning in a few pixels. Measured on a real chess
/// board render: ~1966 distinct colours, three of them (background and the two square fills) covering
/// ~95% of the surface, and the 255th-ranked colour occupying TWELVE pixels — the rest of the budget
/// being consumed by glyph antialiasing. A selection tint or a 3px last-move border competes with that
/// tail on area alone, and losing means <c>FindNearest</c> snaps it to the closest survivor, which is
/// almost always a near-duplicate of the background or the board. So the failure is not a slight shift
/// in hue: it is the accent disappearing.</para>
///
/// <para>Colours travel the wire as 0..100 PERCENTAGES, so assertions here compare in percent space —
/// 255 levels per channel collapse to 101 and the encoder is not lossless by design.</para>
/// </summary>
public sealed class SixelReservedColorsTests
{
    private static byte[] Encode(byte[] pixels, int width, int height, params RGBAColor32[] reserved)
    {
        using var ms = new MemoryStream();
        SixelEncoder.Encode(pixels, width, height, 4, ms, reserved);
        return ms.ToArray();
    }

    private static byte[] Make(int width, int height, Func<int, int, (byte R, byte G, byte B, byte A)> shade)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var (r, g, b, a) = shade(x, y);
                var i = (y * width + x) * 4;
                pixels[i] = r; pixels[i + 1] = g; pixels[i + 2] = b; pixels[i + 3] = a;
            }
        }
        return pixels;
    }

    /// <summary>The palette declarations, in index order: <c>#n;2;R%;G%;B%</c>.</summary>
    private static List<(int Index, int R, int G, int B)> Palette(byte[] encoded)
    {
        var text = Encoding.ASCII.GetString(encoded);
        return Regex.Matches(text, @"#(\d+);2;(\d+);(\d+);(\d+)")
            .Select(m => (
                int.Parse(m.Groups[1].Value),
                int.Parse(m.Groups[2].Value),
                int.Parse(m.Groups[3].Value),
                int.Parse(m.Groups[4].Value)))
            .ToList();
    }

    private static (int R, int G, int B) Pct(RGBAColor32 c)
        => (c.Red * 100 / 255, c.Green * 100 / 255, c.Blue * 100 / 255);

    /// <summary>
    /// A board-like surface: two flat fills over most of the area plus a wide spread of near-identical
    /// shades standing in for antialiasing, so the palette is oversubscribed exactly as a real render's
    /// is. <paramref name="accentPixels"/> pixels get the accent — few enough to lose the frequency cut.
    /// </summary>
    private static byte[] BoardLike(int width, int height, RGBAColor32 accent, int accentPixels)
    {
        // Rows of dense near-identical shades standing in for glyph antialiasing. Repeat is the load-
        // bearing number: each shade covers that many pixels, which is what puts the frequency CUTLINE
        // above a few-pixel accent. Shades one pixel apiece would leave the cutline at 1 and any accent
        // would survive on its own merits, proving nothing.
        const int TailRows = 24;
        const int Repeat = 8;

        var pixels = Make(width, height, (x, y) =>
        {
            if (y < TailRows)
            {
                var n = (y * width + x) / Repeat;
                // Blue is pinned well away from the accent's, so no shade can collide with it in the
                // coarse 0..100 percent space the wire format actually stores.
                return ((byte)(n * 7 % 256), (byte)(n * 13 % 256), (byte)0x20, (byte)0xFF);
            }
            return ((x / 8 + y / 8) % 2 == 0)
                ? ((byte)0xFF, (byte)0xCE, (byte)0x9E, (byte)0xFF)
                : ((byte)0xD1, (byte)0x8B, (byte)0x47, (byte)0xFF);
        });

        for (var i = 0; i < accentPixels; i++)
        {
            var o = i * 4;
            pixels[o] = accent.Red; pixels[o + 1] = accent.Green; pixels[o + 2] = accent.Blue; pixels[o + 3] = 0xFF;
        }
        return pixels;
    }

    /// <summary>
    /// The default must be the historical behaviour byte for byte — the pinned hashes in
    /// <see cref="SixelEncoderTests"/> are the other half of this guarantee.
    /// </summary>
    [Fact]
    public void NoReservations_EncodeIsUnchanged()
    {
        var pixels = BoardLike(64, 64, new RGBAColor32(0x8A, 0x4F, 0xD0, 0xFF), accentPixels: 4);

        using var withoutParam = new MemoryStream();
        SixelEncoder.Encode(pixels, 64, 64, 4, withoutParam);

        Encode(pixels, 64, 64).ShouldBe(withoutParam.ToArray());
    }

    /// <summary>
    /// The whole point: an accent with fewer pixels than the frequency cutline is still exact. Asserted
    /// against the un-reserved encode of the SAME image, which must NOT contain it — otherwise the
    /// fixture is not actually oversubscribed and the test proves nothing.
    /// </summary>
    [Fact]
    public void AReservedAccent_SurvivesAnOversubscribedPalette()
    {
        var accent = new RGBAColor32(0x8A, 0x4F, 0xD0, 0xFF);
        var pixels = BoardLike(96, 96, accent, accentPixels: 3);

        Palette(Encode(pixels, 96, 96)).Select(p => (p.R, p.G, p.B))
            .ShouldNotContain(Pct(accent), "fixture must be oversubscribed, or this proves nothing");

        Palette(Encode(pixels, 96, 96, accent)).Select(p => (p.R, p.G, p.B))
            .ShouldContain(Pct(accent));
    }

    /// <summary>Reserved colours hold their slots even at zero occurrences — that is what makes the
    /// index stable rather than merely present.</summary>
    [Fact]
    public void AReservedColour_GetsASlotEvenWhenAbsentFromTheImage()
    {
        var absent = new RGBAColor32(0x8A, 0x4F, 0xD0, 0xFF);
        var flat = Make(16, 16, (_, _) => (0x10, 0x20, 0x30, 0xFF));

        var palette = Palette(Encode(flat, 16, 16, absent));

        palette[0].ShouldBe((0, Pct(absent).R, Pct(absent).G, Pct(absent).B));
    }

    /// <summary>
    /// Index stability across encodes, which is what a partial strip and an animation frame both need:
    /// the same reservation list must yield the same index whatever the image contains.
    /// </summary>
    [Fact]
    public void ReservedIndices_AreStableAcrossDifferentImages()
    {
        RGBAColor32[] reserved =
        [
            new(0xFF, 0xCE, 0x9E, 0xFF),
            new(0xD1, 0x8B, 0x47, 0xFF),
            new(0x8A, 0x4F, 0xD0, 0xFF),
        ];

        var a = Palette(Encode(BoardLike(64, 64, reserved[2], 2), 64, 64, reserved));
        var b = Palette(Encode(Make(64, 64, (x, y) => ((byte)x, (byte)y, (byte)(x ^ y), 0xFF)), 64, 64, reserved));

        for (var i = 0; i < reserved.Length; i++)
        {
            a[i].ShouldBe((i, Pct(reserved[i]).R, Pct(reserved[i]).G, Pct(reserved[i]).B));
            b[i].ShouldBe(a[i], $"reservation {i} must land on the same index in both images");
        }
    }

    [Fact]
    public void DuplicateReservations_DoNotWasteASlot()
    {
        var c = new RGBAColor32(0x8A, 0x4F, 0xD0, 0xFF);
        var other = new RGBAColor32(0x11, 0x22, 0x33, 0xFF);
        var flat = Make(8, 8, (_, _) => (0x10, 0x20, 0x30, 0xFF));

        var palette = Palette(Encode(flat, 8, 8, c, c, other));

        palette[0].ShouldBe((0, Pct(c).R, Pct(c).G, Pct(c).B));
        palette[1].ShouldBe((1, Pct(other).R, Pct(other).G, Pct(other).B), "the duplicate must not occupy index 1");
    }

    /// <summary>
    /// The limit case, and the reason animation needs no separate mode: reserve every slot and the
    /// frequency pass has nothing left to allocate, so the palette IS the list given and every colour in
    /// the image snaps into it.
    /// </summary>
    [Fact]
    public void AFullReservationList_IsAFixedPalette()
    {
        // 255 distinct greys — the whole budget.
        var fixedPalette = Enumerable.Range(0, 255)
            .Select(i => new RGBAColor32((byte)i, (byte)i, (byte)i, 0xFF))
            .ToArray();

        // An image of saturated colour, none of which is grey.
        var colourful = Make(64, 64, (x, y) => ((byte)(x * 4 % 256), (byte)(255 - y * 4 % 256), (byte)0xC0, 0xFF));

        var palette = Palette(Encode(colourful, 64, 64, fixedPalette));

        palette.Count.ShouldBe(255);
        palette.ShouldAllBe(p => p.R == p.G && p.G == p.B, "every entry is one of the greys handed in");
    }
}
