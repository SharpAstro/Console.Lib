using System.Security.Cryptography;
using Shouldly;
using Xunit;

namespace Console.Lib.Tests;

/// <summary>
/// Output-stability tests for <see cref="SixelEncoder"/>.
///
/// <para>The encoder emits a byte-exact wire format that terminals parse directly, so any change to
/// the stream is a behaviour change even when the decoded picture would look identical. These hashes
/// were captured from the encoder as it stood before the per-colour column-extent optimization, and
/// are pinned so that a later tightening of the RLE loop cannot silently alter the output.</para>
///
/// <para>They are also what guards the extent arithmetic specifically: that optimization stops
/// scanning a colour's empty leading/trailing margins and re-emits them as computed runs instead. Get
/// a count wrong by one and the picture shifts horizontally — which changes the stream, and so fails
/// here.</para>
///
/// <para>The cases span the axes that steer the encoder: palette size (a flat two-colour board vs. a
/// gradient pushing toward the 255-entry ceiling), the transparency sentinel, a height that is not a
/// multiple of the 6-pixel band, single-row and single-column degenerate bands, maximum per-band
/// palette churn, and content whose colours are confined to narrow column ranges — the last being the
/// shape the extent skip exists for.</para>
/// </summary>
public sealed class SixelEncoderTests
{
    private static byte[] Encode(byte[] pixels, int width, int height)
    {
        using var ms = new MemoryStream();
        SixelEncoder.Encode(pixels, width, height, 4, ms);
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
                pixels[i] = r;
                pixels[i + 1] = g;
                pixels[i + 2] = b;
                pixels[i + 3] = a;
            }
        }
        return pixels;
    }

    private static (byte[] Pixels, int Width, int Height) Case(string name) => name switch
    {
        "checker" => (Make(64, 64, (x, y) => ((x / 8 + y / 8) % 2 == 0)
            ? ((byte)0xEE, (byte)0xEE, (byte)0xD2, (byte)0xFF)
            : ((byte)0x76, (byte)0x96, (byte)0x56, (byte)0xFF)), 64, 64),

        "gradient" => (Make(120, 40, (x, y) =>
            ((byte)(x * 2 % 256), (byte)(y * 6 % 256), (byte)((x + y) % 256), (byte)0xFF)), 120, 40),

        "alpha" => (Make(48, 20, (x, y) => (x + y) % 3 == 0
            ? ((byte)0, (byte)0, (byte)0, (byte)0)
            : ((byte)(x * 5 % 256), (byte)0x40, (byte)0x80, (byte)0xFF)), 48, 20),

        "ragged" => (Make(32, 17, (x, y) =>
            ((byte)(x % 7 * 36), (byte)(y % 5 * 51), (byte)0x20, (byte)0xFF)), 32, 17),

        "thin-row" => (Make(40, 1, (x, y) =>
            ((byte)(x * 6 % 256), (byte)0x10, (byte)0x90, (byte)0xFF)), 40, 1),

        "thin-col" => (Make(1, 40, (x, y) =>
            ((byte)0x10, (byte)(y * 6 % 256), (byte)0x90, (byte)0xFF)), 1, 40),

        "churn" => (Make(24, 24, (x, y) =>
            ((byte)(x * 11 % 256), (byte)(y * 13 % 256), (byte)((x * y) % 256), (byte)0xFF)), 24, 24),

        // Each colour confined to its own vertical stripe: every colour's set columns are a small
        // sub-range of the row, with empty margins either side. This is the case the extent skip
        // exploits, so it is also the one most sensitive to getting the margin runs wrong.
        "striped" => (Make(96, 24, (x, y) =>
        {
            var c = (byte)((x * 8 / 96) * 32);
            return (c, (byte)(255 - c), (byte)(c / 2), (byte)0xFF);
        }), 96, 24),

        // 216 colours in noise with a transparent speckle: the output runs to several times the
        // encoder's 64 KiB write buffer, so the stream is written in more than one flush.
        "large" => (Make(320, 240, (x, y) =>
        {
            var n = Noise(x, y);
            return n % 11 == 0
                ? ((byte)0, (byte)0, (byte)0, (byte)0)
                : ((byte)(n % 6 * 51), (byte)(n / 6 % 6 * 51), (byte)(n / 36 % 6 * 51), (byte)0xFF);
        }), 320, 240),

        // Thousands of colours, so the palette is oversubscribed and most snap to a nearest survivor,
        // at a size that also spans several flushes.
        "large-oversubscribed" => (Make(300, 200, (x, y) =>
            ((byte)(x * 7 % 256), (byte)(y * 5 % 256), (byte)(Noise(x, y) % 8 * 32), (byte)0xFF)), 300, 200),

        // Runs far longer than a row usually holds (four-digit '!' counts), transparent gaps between
        // them, and a height that ends mid-band.
        "long-runs" => (Make(2000, 13, (x, y) => x switch
        {
            < 1200 => ((byte)0xFF, (byte)0xFF, (byte)0xFF, (byte)0xFF),
            < 1500 => ((byte)0, (byte)0, (byte)0, (byte)0),
            < 1800 => y % 2 == 0 ? ((byte)0x20, (byte)0x40, (byte)0x60, (byte)0xFF) : ((byte)0, (byte)0, (byte)0, (byte)0),
            _ => ((byte)0x80, (byte)0x10, (byte)0x10, (byte)0xFF),
        }), 2000, 13),

        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "unknown case"),
    };

    /// <summary>A deterministic 0..255 hash of a pixel position, standing in for picture content.</summary>
    private static int Noise(int x, int y) => (int)((uint)(x * 73856093 ^ y * 19349663) * 2654435761u >> 24);

    public static TheoryData<string> DataSource() =>
        ["checker", "gradient", "alpha", "ragged", "thin-row", "thin-col", "churn", "striped",
         "large", "large-oversubscribed", "long-runs"];

    /// <summary>
    /// Stream length and hash captured from the pre-optimization encoder. The last three cases, and the
    /// two tests below, came later: captured from the encoder at cac44a8, before the RLE and write-path
    /// rework, to pin what the first eight do not reach (more than one flush of the write buffer,
    /// four-digit runs, three-channel input and reservations).
    /// </summary>
    private static (int Length, string Hash) Golden(string name) => name switch
    {
        "checker" => (637, "33DE75D4D682A696"),
        "gradient" => (14853, "E75FB3169F01DE6E"),
        "alpha" => (3001, "2EBE41F6C0DBE823"),
        "ragged" => (2930, "1CDDD61BA6FB740C"),
        "thin-row" => (999, "3D2DD24589762187"),
        "thin-col" => (711, "19BCAE57AA904CDE"),
        "churn" => (7645, "4C136B6679539ED3"),
        "striped" => (567, "567BC60B35AC4E34"),
        "large" => (383143, "E7A1ABAECEAB08FD"),
        "large-oversubscribed" => (147572, "003D9A5AB9A76D15"),
        "long-runs" => (200, "A4A395DB8ED38487"),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "unknown case"),
    };

    [Theory]
    [MemberData(nameof(DataSource))]
    public void Encode_ProducesStableBytes(string name)
    {
        var (length, hash) = Golden(name);
        var (pixels, width, height) = Case(name);

        var encoded = Encode(pixels, width, height);

        encoded.Length.ShouldBe(length);
        Convert.ToHexString(SHA256.HashData(encoded))[..16].ShouldBe(hash);
    }

    [Fact]
    public void Encode_IsDeterministic()
    {
        var (pixels, width, height) = Case("striped");

        var first = Encode(pixels, width, height);
        var second = Encode(pixels, width, height);

        // Working buffers are rented from ArrayPool and reused across calls, so a missed reset
        // between encodes would surface as the second stream differing from the first.
        second.ShouldBe(first);
    }

    [Fact]
    public void Encode_ThreeChannels_ProducesStableBytes()
    {
        // The alpha case with its alpha dropped: without a fourth channel nothing is transparent, so
        // the pixels the RGBA encode skips become black ones here.
        var (rgba, width, height) = Case("alpha");
        var rgb = new byte[width * height * 3];
        for (var i = 0; i < width * height; i++)
        {
            rgb[i * 3] = rgba[i * 4];
            rgb[i * 3 + 1] = rgba[i * 4 + 1];
            rgb[i * 3 + 2] = rgba[i * 4 + 2];
        }

        using var ms = new MemoryStream();
        SixelEncoder.Encode(rgb, width, height, 3, ms);
        var encoded = ms.ToArray();

        encoded.Length.ShouldBe(3220);
        Convert.ToHexString(SHA256.HashData(encoded))[..16].ShouldBe("EEA89AC1AF44DECA");
    }

    [Fact]
    public void Encode_WithReservations_ProducesStableBytes()
    {
        // Oversubscribed, so the reservations displace frequency-ranked colours; red appears twice to
        // pin that a duplicate takes no slot.
        var (pixels, width, height) = Case("large-oversubscribed");

        using var ms = new MemoryStream();
        SixelEncoder.Encode(pixels, width, height, 4, ms,
            [new DIR.Lib.RGBAColor32(0xFF, 0, 0, 0xFF), new DIR.Lib.RGBAColor32(0, 0x80, 0xFF, 0xFF), new DIR.Lib.RGBAColor32(0xFF, 0, 0, 0xFF)]);
        var encoded = ms.ToArray();

        encoded.Length.ShouldBe(147291);
        Convert.ToHexString(SHA256.HashData(encoded))[..16].ShouldBe("AB755609A61C9E19");
    }
}
