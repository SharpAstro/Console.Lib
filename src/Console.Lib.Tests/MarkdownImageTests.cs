using System;
using System.Collections.Generic;
using System.Linq;
using Console.Lib;
using Shouldly;
using Xunit;

namespace Console.Lib.Tests;

public sealed class MarkdownImageTests
{
    // ── Alt-text fallbacks (no raster) ────────────────────────────────

    [Fact]
    public void Image_NoOptions_RendersAltText()
    {
        // Without MarkdownImageOptions, every image is alt text only.
        var lines = MarkdownRenderer.RenderLines("![a cat](cat.png)", width: 40, ColorMode.None);
        string.Join("\n", lines).ShouldContain("a cat");
    }

    [Fact]
    public void Image_ResolverReturnsNull_FallsBackToAlt()
    {
        var opts = new MarkdownImageOptions(_ => null, BoxRenderMode.HalfBlock);
        var lines = MarkdownRenderer.RenderLines("![a cat](cat.png)", 40, ColorMode.None, images: opts);
        string.Join("\n", lines).ShouldContain("a cat");
    }

    [Fact]
    public void Image_UndecodableBytes_FallsBackToAlt()
    {
        var opts = new MarkdownImageOptions(_ => new byte[] { 1, 2, 3, 4, 5 }, BoxRenderMode.HalfBlock);
        var lines = MarkdownRenderer.RenderLines("![a cat](cat.png)", 40, ColorMode.None, images: opts);
        string.Join("\n", lines).ShouldContain("a cat");
    }

    [Fact]
    public void Image_EmptyAlt_ShowsFilename()
    {
        var lines = MarkdownRenderer.RenderLines("![](pics/logo.png)", 40, ColorMode.None);
        string.Join("\n", lines).ShouldContain("logo.png");
    }

    [Fact]
    public void Image_MidText_RendersAltInline_NotRastered()
    {
        // An image inside a line of text is never promoted to a raster block;
        // it renders its alt text inline even when raster options are present.
        var bmp = MakeSolidBmp(4, 4, 10, 20, 30);
        var opts = new MarkdownImageOptions(_ => bmp, BoxRenderMode.HalfBlock);
        var lines = MarkdownRenderer.RenderLines("see ![x](a.bmp) now", 40, ColorMode.None, images: opts);
        var text = string.Join("\n", lines);
        text.ShouldContain("see x now");
        text.ShouldNotContain("▀");
    }

    // ── Raster rendering ──────────────────────────────────────────────

    [Fact]
    public void Image_StandaloneLine_RastersWhenResolved()
    {
        var bmp = MakeSolidBmp(4, 4, 0x80, 0x40, 0x20);
        var opts = new MarkdownImageOptions(_ => bmp, BoxRenderMode.HalfBlock, CellPixelWidth: 10, CellPixelHeight: 20);
        var lines = MarkdownRenderer.RenderLines("![cat](cat.bmp)", width: 40, ColorMode.TrueColor, images: opts);

        // 4px tall in half-block (2 px/row) → 2 rows; solid opaque → ▀ cells.
        lines.Count.ShouldBe(2);
        lines.ShouldAllBe(l => l.Contains('▀') || l.Contains('▄') || l.Contains('█'));
        // The alt text must not leak into a successful raster.
        string.Join("\n", lines).ShouldNotContain("cat");
    }

    [Fact]
    public void Image_WiderThanWidth_ScalesDownToFit()
    {
        var bmp = MakeSolidBmp(100, 4, 200, 100, 50);
        var opts = new MarkdownImageOptions(_ => bmp, BoxRenderMode.HalfBlock, CellPixelWidth: 10, CellPixelHeight: 20);
        var lines = MarkdownRenderer.RenderLines("![big](big.bmp)", width: 10, ColorMode.TrueColor, images: opts);

        lines.Count.ShouldBeGreaterThan(0);
        // Half-block uses 1 px per column, so the rendered width must fit in 10 cells.
        lines.ShouldAllBe(l => MarkdownRenderer.VisibleLength(l) <= 10);
    }

    // ── Fixture: a minimal 24-bit uncompressed BMP (decoded by StbImageSharp) ──

    private static byte[] MakeSolidBmp(int w, int h, byte r, byte g, byte b)
    {
        var rowStride = ((w * 3 + 3) / 4) * 4;
        var imageSize = rowStride * h;
        var fileSize = 54 + imageSize;
        var bytes = new byte[fileSize];

        // BITMAPFILEHEADER
        bytes[0] = (byte)'B';
        bytes[1] = (byte)'M';
        WriteI32(bytes, 2, fileSize);
        WriteI32(bytes, 10, 54); // pixel-data offset

        // BITMAPINFOHEADER
        WriteI32(bytes, 14, 40); // header size
        WriteI32(bytes, 18, w);
        WriteI32(bytes, 22, h);
        bytes[26] = 1;  // planes
        bytes[28] = 24; // bits per pixel
        WriteI32(bytes, 34, imageSize);

        // Pixel data — bottom-up rows, BGR, padded to 4 bytes.
        const int off = 54;
        for (var y = 0; y < h; y++)
        {
            var rowStart = off + y * rowStride;
            for (var x = 0; x < w; x++)
            {
                var p = rowStart + x * 3;
                bytes[p] = b;
                bytes[p + 1] = g;
                bytes[p + 2] = r;
            }
        }
        return bytes;
    }

    private static void WriteI32(byte[] b, int o, int v)
    {
        b[o] = (byte)(v & 0xFF);
        b[o + 1] = (byte)((v >> 8) & 0xFF);
        b[o + 2] = (byte)((v >> 16) & 0xFF);
        b[o + 3] = (byte)((v >> 24) & 0xFF);
    }
}
