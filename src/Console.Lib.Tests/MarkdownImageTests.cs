using System;
using System.Collections.Generic;
using System.Linq;
using Console.Lib;
using SharpAstro.Png;
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
        var bmp = MakeSolidPng(4, 4, 10, 20, 30);
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
        var bmp = MakeSolidPng(4, 4, 0x80, 0x40, 0x20);
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
        var bmp = MakeSolidPng(100, 4, 200, 100, 50);
        var opts = new MarkdownImageOptions(_ => bmp, BoxRenderMode.HalfBlock, CellPixelWidth: 10, CellPixelHeight: 20);
        var lines = MarkdownRenderer.RenderLines("![big](big.bmp)", width: 10, ColorMode.TrueColor, images: opts);

        lines.Count.ShouldBeGreaterThan(0);
        // Half-block uses 1 px per column, so the rendered width must fit in 10 cells.
        lines.ShouldAllBe(l => MarkdownRenderer.VisibleLength(l) <= 10);
    }

    // ── Fixture: a solid-colour RGBA PNG (decoded via the SharpAstro.Codecs facade) ──

    private static byte[] MakeSolidPng(int w, int h, byte r, byte g, byte b)
    {
        var rgba = new byte[w * h * 4];
        for (var i = 0; i < w * h; i++)
        {
            rgba[i * 4] = r;
            rgba[i * 4 + 1] = g;
            rgba[i * 4 + 2] = b;
            rgba[i * 4 + 3] = 255;
        }
        return PngWriter.Encode(rgba, w, h);
    }
}
