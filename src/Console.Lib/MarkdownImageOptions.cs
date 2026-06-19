using System;

namespace Console.Lib;

/// <summary>
/// How <see cref="MarkdownRenderer"/> should render Markdown images
/// (<c>![alt](url)</c>). Supplying this enables raster rendering for an image
/// that sits alone on its own line (mirroring how display math rasters while
/// inline math stays text); leave it <c>null</c> to render every image as its
/// alt text only. An image that appears mid-paragraph always renders as alt
/// text, since a multi-row raster can't be spliced into the middle of a line.
/// </summary>
/// <param name="Resolver">Maps an image source — the <c>url</c> from
/// <c>![alt](url)</c> — to its encoded bytes (PNG / baseline JPEG / BMP / GIF /
/// …), or <c>null</c> to skip it (rendered as alt text). The renderer never
/// fetches anything itself: the host decides what a source resolves to (e.g. a
/// local file relative to the document), and can deny remote URLs by returning
/// <c>null</c>.</param>
/// <param name="Mode">Raster encoding for the terminal. Typically derived from
/// <see cref="IVirtualTerminal.ImageDisplayCapability"/> (Sixel → <see
/// cref="BoxRenderMode.Sixel"/>; AsciiBlock → <see cref="BoxRenderMode.Sextant"/>
/// or <see cref="BoxRenderMode.HalfBlock"/>; NoColor → don't supply options).</param>
/// <param name="CellPixelWidth">Pixel width of one terminal cell, used to size
/// Sixel output to the render width. From <see cref="ITerminalViewport.CellSize"/>.</param>
/// <param name="CellPixelHeight">Pixel height of one terminal cell.</param>
/// <param name="MaxRows">Upper bound on the rendered image's height in terminal
/// rows; taller images scale down with aspect ratio preserved.</param>
public sealed record MarkdownImageOptions(
    Func<string, byte[]?> Resolver,
    BoxRenderMode Mode,
    int CellPixelWidth = 10,
    int CellPixelHeight = 20,
    int MaxRows = 20);
