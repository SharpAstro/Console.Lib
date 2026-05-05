using System.IO;
using System.IO.Compression;

namespace Console.Lib.MathLayout;

/// <summary>
/// Minimal PNG encoder for 8-bit RGBA images. Emits an uncompressed-but-
/// zlib-wrapped IDAT (deflate filter type 0 = None per scanline). No
/// interlacing, no palette, no ancillary chunks. Adequate for golden-image
/// test baselines and simple "save my Box render to a file" use cases.
///
/// PNG layout: 8-byte signature, IHDR chunk, IDAT chunk, IEND chunk.
/// Each chunk is length(BE u32) + type(4 bytes) + data + CRC32(BE u32).
/// </summary>
public static class PngEncoder
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>
    /// Encode an 8-bit RGBA pixel buffer (row-major, no padding) as a PNG.
    /// </summary>
    public static byte[] Encode(ReadOnlySpan<byte> rgba, int width, int height)
    {
        if (width <= 0 || height <= 0) throw new ArgumentException("width and height must be positive");
        if (rgba.Length != width * height * 4) throw new ArgumentException("rgba length must equal width*height*4");

        using var ms = new MemoryStream();
        ms.Write(Signature);

        // IHDR: width, height, bit depth (8), color type (6 = RGBA),
        // compression (0 = deflate), filter (0 = adaptive), interlace (0).
        Span<byte> ihdr = stackalloc byte[13];
        WriteBE(ihdr.Slice(0, 4), (uint)width);
        WriteBE(ihdr.Slice(4, 4), (uint)height);
        ihdr[8] = 8; ihdr[9] = 6; ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;
        WriteChunk(ms, "IHDR"u8, ihdr);

        // IDAT: each scanline prefixed with 1 filter byte (0 = None), then
        // zlib-compressed.
        var raw = new byte[height * (1 + width * 4)];
        for (int y = 0; y < height; y++)
        {
            int srcRow = y * width * 4;
            int dstRow = y * (1 + width * 4);
            raw[dstRow] = 0; // filter: None
            rgba.Slice(srcRow, width * 4).CopyTo(raw.AsSpan(dstRow + 1));
        }
        var compressed = DeflateZlib(raw);
        WriteChunk(ms, "IDAT"u8, compressed);

        // IEND: empty data.
        WriteChunk(ms, "IEND"u8, ReadOnlySpan<byte>.Empty);
        return ms.ToArray();
    }

    private static void WriteChunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> lenBuf = stackalloc byte[4];
        WriteBE(lenBuf, (uint)data.Length);
        output.Write(lenBuf);
        output.Write(type);
        output.Write(data);

        // CRC32 over type + data, big-endian.
        var crc = Crc32(type, data);
        Span<byte> crcBuf = stackalloc byte[4];
        WriteBE(crcBuf, crc);
        output.Write(crcBuf);
    }

    private static void WriteBE(Span<byte> dst, uint value)
    {
        dst[0] = (byte)(value >> 24);
        dst[1] = (byte)(value >> 16);
        dst[2] = (byte)(value >> 8);
        dst[3] = (byte)value;
    }

    private static byte[] DeflateZlib(ReadOnlySpan<byte> raw)
    {
        // ZLibStream wraps deflate with a 2-byte header + 4-byte Adler32
        // trailer, which is exactly what the PNG IDAT spec asks for.
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        {
            z.Write(raw);
        }
        return ms.ToArray();
    }

    /// <summary>
    /// Standard PNG CRC32 (polynomial 0xEDB88320, IEEE 802.3). Computed on
    /// the concatenation of <paramref name="a"/> and <paramref name="b"/>
    /// without materializing either span.
    /// </summary>
    private static uint Crc32(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        uint c = 0xFFFFFFFFu;
        foreach (var x in a) c = CrcTable[(c ^ x) & 0xFF] ^ (c >> 8);
        foreach (var x in b) c = CrcTable[(c ^ x) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (int k = 0; k < 8; k++)
                c = ((c & 1) != 0) ? 0xEDB88320u ^ (c >> 1) : (c >> 1);
            t[n] = c;
        }
        return t;
    }
}
