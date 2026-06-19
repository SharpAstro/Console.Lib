using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using DIR.Lib;
using DIR.Lib.MathLayout;
using DIR.Lib.Markdown;

namespace Console.Lib;

/// <summary>
/// LALR.CC-driven rendering path. <see cref="MarkdownRenderer.RenderLines"/>
/// dispatches here unconditionally: the method parses the source via
/// <see cref="MarkdownBlockVisitor"/> (from <c>DIR.Lib.Markdown</c>) and walks
/// the resulting <see cref="MdBlock"/> / <see cref="MdInline"/> tree, emitting
/// VT-styled lines via the helpers in the partial class. The legacy Markdig
/// path was retired (Phase F cleanup) and the parser layer relocated to
/// <c>DIR.Lib.Markdown</c> in v2.14.0 — this file is now the renderer
/// implementation, not a parallel experiment.
/// </summary>
public static partial class MarkdownRenderer
{
    /// <summary>Renders Markdown via the LALR.CC inline + block
    /// grammars. Same signature as <see cref="RenderLines"/>; output
    /// should match byte-for-byte for the cases both paths cover.</summary>
    public static List<string> RenderLinesLalr(string markdown, int width,
        ColorMode colorMode = ColorMode.TrueColor, MarkdownTheme? theme = null,
        BoxRenderMode? mathMode = null, string? mathFontPath = null,
        MarkdownImageOptions? images = null)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return new List<string>();

        theme ??= MarkdownTheme.Default;
        var blocks = s_blockVisitor.Parse(markdown);
        var result = new List<string>();
        var first = true;

        foreach (var block in blocks)
        {
            if (!first) result.Add(string.Empty);
            RenderMdBlock(block, width, colorMode, theme, result, mathMode, mathFontPath, images);
            first = false;
        }

        return result;
    }

    private static readonly MarkdownBlockVisitor s_blockVisitor = new();

    // ── Block dispatch ────────────────────────────────────────────────

    private static void RenderMdBlock(MdBlock block, int width, ColorMode colorMode,
        MarkdownTheme theme, List<string> result,
        BoxRenderMode? mathMode, string? mathFontPath, MarkdownImageOptions? images)
    {
        switch (block)
        {
            case MdHeading h:
                RenderMdHeading(h, width, colorMode, theme, result);
                break;
            case MdThematicBreak:
                result.Add($"{Resolve(theme.Dim, colorMode)}{new string('─', width)}{Rst(colorMode)}");
                break;
            case MdParagraph p:
                {
                    // A paragraph that is just one image (its own line) rasters
                    // as a block — mirroring display math. If raster is off or
                    // fails, we fall through to the inline walker, which emits
                    // the image's alt text via its MdImage case.
                    if (images is not null && TrySingleImage(p.Content) is { } soleImage
                        && TryRenderImage(soleImage, width, images, result))
                    {
                        break;
                    }
                    var sb = new StringBuilder();
                    RenderMdInlines(p.Content, sb, bold: false, italic: false, colorMode, theme);
                    result.AddRange(WordWrap(sb.ToString(), width));
                    break;
                }
            case MdCodeFence f:
                RenderMdCodeFence(f, width, colorMode, theme, result);
                break;
            case MdMathBlock m:
                RenderMdMathBlock(m, width, colorMode, theme, result, mathMode, mathFontPath);
                break;
            case MdList l:
                RenderMdList(l, width, colorMode, theme, result, mathMode, mathFontPath, images, nestLevel: 0);
                break;
            case MdTable t:
                RenderMdTable(t, colorMode, theme, result);
                break;
        }
    }

    private static void RenderMdHeading(MdHeading h, int width, ColorMode colorMode,
        MarkdownTheme theme, List<string> result)
    {
        var color = h.Level switch
        {
            1 => Resolve(theme.Heading1, colorMode),
            2 => Resolve(theme.Heading2, colorMode),
            _ => Resolve(theme.Heading3, colorMode),
        };
        var sb = new StringBuilder();
        RenderMdInlines(h.Content, sb, bold: false, italic: false, colorMode, theme);
        var text = $"{BoldAttr(colorMode)}{color}{sb}{Rst(colorMode)}";
        result.AddRange(WordWrap(text, width));
    }

    private static void RenderMdCodeFence(MdCodeFence f, int width, ColorMode colorMode,
        MarkdownTheme theme, List<string> result)
    {
        var codeColor = Resolve(theme.Code, colorMode);
        var dimColor = Resolve(theme.Dim, colorMode);
        var rst = Rst(colorMode);
        var lang = f.Lang ?? string.Empty;
        if (!string.IsNullOrEmpty(lang))
        {
            var prefix = "── ";
            var tag = $" {lang} ";
            var fillLen = System.Math.Max(0, width - prefix.Length - tag.Length);
            result.Add($"{dimColor}{prefix}{tag}{new string('─', fillLen)}{rst}");
        }
        else
        {
            result.Add($"{dimColor}{new string('─', width)}{rst}");
        }
        foreach (var line in f.Lines)
            result.Add($"  {codeColor}{line}{rst}");
        result.Add($"{dimColor}{new string('─', width)}{rst}");
    }

    private static void RenderMdMathBlock(MdMathBlock m, int width, ColorMode colorMode,
        MarkdownTheme theme, List<string> result,
        BoxRenderMode? mathMode, string? mathFontPath)
    {
        // Pixel-mode rendering tries the BoxRenderer path; if it can't
        // build (no math font, no LaTeX parser support, etc.) we fall
        // through to the Unicode rendering already on m.Unicode.
        if (mathMode is { } mode && TryRenderMathBox(m.Source, mode, mathFontPath, result))
            return;

        var mathColor = Resolve(theme.Math, colorMode);
        var rst = Rst(colorMode);
        foreach (var line in (m.Unicode ?? string.Empty).Split('\n'))
            result.AddRange(WordWrap($"  {mathColor}{line}{rst}", width, "  "));
    }

    private static void RenderMdList(MdList list, int width, ColorMode colorMode,
        MarkdownTheme theme, List<string> result,
        BoxRenderMode? mathMode, string? mathFontPath, MarkdownImageOptions? images, int nestLevel)
    {
        var bulletColor = Resolve(theme.Bullet, colorMode);
        var dimColor = Resolve(theme.Dim, colorMode);
        var rst = Rst(colorMode);
        var indent = new string(' ', 2 + nestLevel * 2);
        var contIndent = new string(' ', indent.Length + 3);
        var itemNum = list.OrderedStart;

        foreach (var item in list.Items)
        {
            var bulletChar = nestLevel switch { 0 => "•", 1 => "◦", _ => "▪" };
            var marker = list.Ordered
                ? $"{dimColor}{itemNum}.{rst}"
                : $"{bulletColor}{bulletChar}{rst}";
            itemNum++;

            // First block of the list item — usually a paragraph.
            // Subsequent blocks (nested lists, math blocks, fences,
            // additional paragraphs) get indented under the same item.
            bool firstBlock = true;
            foreach (var body in item.Body)
            {
                if (body is MdParagraph para)
                {
                    var sb = new StringBuilder();
                    RenderMdInlines(para.Content, sb, bold: false, italic: false, colorMode, theme);
                    var prefix = firstBlock ? $"{indent}{marker} " : contIndent;
                    var wrapped = WordWrap($"{prefix}{sb}", width, contIndent);
                    result.AddRange(wrapped);
                }
                else if (body is MdList nestedList)
                {
                    RenderMdList(nestedList, width, colorMode, theme, result, mathMode, mathFontPath, images, nestLevel + 1);
                }
                else
                {
                    // Math block, code fence, etc. — render via the main
                    // block dispatcher; the indentation isn't preserved
                    // perfectly here (the existing Markdig path has the
                    // same approximation) but the content surfaces.
                    RenderMdBlock(body, width, colorMode, theme, result, mathMode, mathFontPath, images);
                }
                firstBlock = false;
            }
        }
    }

    private static void RenderMdTable(MdTable t, ColorMode colorMode,
        MarkdownTheme theme, List<string> result)
    {
        var dimColor = Resolve(theme.Dim, colorMode);
        var rst = Rst(colorMode);
        var bold = BoldAttr(colorMode);

        // Column widths derived from header + body cell visible widths.
        int columns = t.Headers.Count;
        int[] widths = new int[columns];
        for (int c = 0; c < columns; c++)
            widths[c] = VisibleLength(FormatInlinesToString(t.Headers[c], colorMode, theme));
        foreach (var row in t.Rows)
        {
            for (int c = 0; c < columns && c < row.Count; c++)
            {
                var w = VisibleLength(FormatInlinesToString(row[c], colorMode, theme));
                if (w > widths[c]) widths[c] = w;
            }
        }

        result.Add(BuildTableBorder(widths, dimColor, rst, top: true));
        result.Add(BuildTableRow(t.Headers, widths, t.Alignments, dimColor, rst, bold, isHeader: true, colorMode, theme));
        result.Add(BuildTableSeparator(widths, t.Alignments, dimColor, rst));
        foreach (var row in t.Rows)
            result.Add(BuildTableRow(row, widths, t.Alignments, dimColor, rst, bold: string.Empty, isHeader: false, colorMode, theme));
        result.Add(BuildTableBorder(widths, dimColor, rst, top: false));
    }

    private static string BuildTableBorder(int[] widths, string dimColor, string rst, bool top)
    {
        var sb = new StringBuilder();
        sb.Append(dimColor).Append(top ? '┌' : '└');
        for (int i = 0; i < widths.Length; i++)
        {
            sb.Append(new string('─', widths[i] + 2));
            sb.Append(i < widths.Length - 1 ? (top ? '┬' : '┴') : (top ? '┐' : '┘'));
        }
        sb.Append(rst);
        return sb.ToString();
    }

    private static string BuildTableSeparator(int[] widths, IReadOnlyList<MdTableAlignment> alignments, string dimColor, string rst)
    {
        var sb = new StringBuilder();
        sb.Append(dimColor).Append('├');
        for (int i = 0; i < widths.Length; i++)
        {
            sb.Append(new string('─', widths[i] + 2));
            sb.Append(i < widths.Length - 1 ? '┼' : '┤');
        }
        sb.Append(rst);
        return sb.ToString();
    }

    private static string BuildTableRow(IReadOnlyList<IReadOnlyList<MdInline>> cells, int[] widths,
        IReadOnlyList<MdTableAlignment> alignments, string dimColor, string rst, string bold,
        bool isHeader, ColorMode colorMode, MarkdownTheme theme)
    {
        var sb = new StringBuilder();
        sb.Append(dimColor).Append('│').Append(rst);
        for (int i = 0; i < widths.Length; i++)
        {
            var raw = i < cells.Count ? FormatInlinesToString(cells[i], colorMode, theme) : string.Empty;
            var formatted = isHeader ? $"{bold}{raw}{rst}" : raw;
            var aligned = AlignTableCell(formatted, VisibleLength(raw), widths[i], i < alignments.Count ? alignments[i] : MdTableAlignment.Left);
            sb.Append(' ').Append(aligned).Append(' ').Append(dimColor).Append('│').Append(rst);
        }
        return sb.ToString();
    }

    private static string AlignTableCell(string content, int visibleLength, int columnWidth, MdTableAlignment alignment)
    {
        var pad = System.Math.Max(0, columnWidth - visibleLength);
        return alignment switch
        {
            MdTableAlignment.Right => new string(' ', pad) + content,
            MdTableAlignment.Center => new string(' ', pad / 2) + content + new string(' ', pad - pad / 2),
            _ => content + new string(' ', pad),
        };
    }

    private static string FormatInlinesToString(IReadOnlyList<MdInline> inlines, ColorMode colorMode, MarkdownTheme theme)
    {
        var sb = new StringBuilder();
        RenderMdInlines(inlines, sb, bold: false, italic: false, colorMode, theme);
        return sb.ToString();
    }

    // ── Inline dispatch ───────────────────────────────────────────────

    private static void RenderMdInlines(IReadOnlyList<MdInline> inlines, StringBuilder sb,
        bool bold, bool italic, ColorMode colorMode, MarkdownTheme theme)
    {
        var rst = Rst(colorMode);
        var boldAttr = BoldAttr(colorMode);
        var italicAttr = ItalicAttr(colorMode);

        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case MdLiteral lit:
                    sb.Append(lit.Text);
                    break;

                case MdEmphasis em:
                    {
                        // Use selective SGR unset codes (22 = no-bold,
                        // 23 = no-italic) instead of a full reset.
                        // A reset (\e[0m) would also clear underline /
                        // foreground colour set by an outer span (most
                        // commonly an MdLink wrapping `**bold link**` —
                        // the link's underline gets killed otherwise).
                        var newBold = bold || em.Level >= 2;
                        var newItalic = italic || em.Level == 1 || em.Level >= 3;
                        if (newBold && !bold) sb.Append(boldAttr);
                        if (newItalic && !italic) sb.Append(italicAttr);
                        RenderMdInlines(em.Content, sb, newBold, newItalic, colorMode, theme);
                        if (newBold && !bold) sb.Append(NoBoldAttr(colorMode));
                        if (newItalic && !italic) sb.Append(NoItalicAttr(colorMode));
                        break;
                    }

                case MdLink link:
                    {
                        var linkColor = Resolve(theme.Link, colorMode);
                        var dimColor = Resolve(theme.Dim, colorMode);
                        // OSC 8 hyperlink wrap — turns the label into a
                        // clickable target on supporting terminals
                        // (Windows Terminal, iTerm2, WezTerm, kitty,
                        // mintty, VS Code, GNOME). Non-supporting
                        // terminals discard the OSC silently. The
                        // `(url)` text after the label stays so the URL
                        // is visible to readers + copy-pasteable from
                        // terminals that don't render the hyperlink.
                        var (hOpen, hClose) = Hyperlink(link.Url, colorMode);
                        sb.Append(hOpen);
                        sb.Append($"{UnderlineAttr(colorMode)}{linkColor}");
                        RenderMdInlines(link.Text, sb, bold: false, italic: false, colorMode, theme);
                        sb.Append(rst);
                        sb.Append(hClose);
                        if (!string.IsNullOrEmpty(link.Url))
                            sb.Append($"{dimColor} ({link.Url}){rst}");
                        if (bold) sb.Append(boldAttr);
                        if (italic) sb.Append(italicAttr);
                        break;
                    }

                case MdImage image:
                    {
                        // Inline (mid-text) images and the no-raster fallback
                        // render the alt text. An empty alt shows the source
                        // filename, dimmed, so the image isn't silently blank.
                        var altSb = new StringBuilder();
                        RenderMdInlines(image.Alt, altSb, bold, italic, colorMode, theme);
                        if (VisibleLength(altSb.ToString()) == 0)
                        {
                            var dimColor = Resolve(theme.Dim, colorMode);
                            sb.Append(rst).Append(dimColor).Append(ImageName(image.Url)).Append(rst);
                            if (bold) sb.Append(boldAttr);
                            if (italic) sb.Append(italicAttr);
                        }
                        else
                        {
                            sb.Append(altSb);
                        }
                        break;
                    }

                case MdColor color:
                    {
                        if (MarkdownTheme.TryParseColor(color.Color, out var rgba))
                        {
                            var fg = Resolve(rgba, colorMode);
                            sb.Append(rst).Append(fg);
                            RenderMdInlines(color.Text, sb, bold: false, italic: false, colorMode, theme);
                            sb.Append(rst);
                            if (bold) sb.Append(boldAttr);
                            if (italic) sb.Append(italicAttr);
                        }
                        else
                        {
                            // Unknown colour name — fall through to literal `[text]{name}`.
                            sb.Append('[');
                            RenderMdInlines(color.Text, sb, bold, italic, colorMode, theme);
                            sb.Append(']').Append('{').Append(color.Color).Append('}');
                        }
                        break;
                    }

                case MdLineBreak br:
                    sb.Append(br.Hard ? '\n' : ' ');
                    break;

                case MdCodeInline code:
                    {
                        var codeColor = Resolve(theme.Code, colorMode);
                        sb.Append(rst).Append(codeColor).Append(code.Content).Append(rst);
                        if (bold) sb.Append(boldAttr);
                        if (italic) sb.Append(italicAttr);
                        break;
                    }

                case MdMathInline math:
                    {
                        var mathColor = Resolve(theme.Math, colorMode);
                        sb.Append(rst).Append(mathColor).Append(math.Unicode).Append(rst);
                        if (bold) sb.Append(boldAttr);
                        if (italic) sb.Append(italicAttr);
                        break;
                    }
            }
        }
    }

    // ── Image rasterization ───────────────────────────────────────────

    /// <summary>
    /// Returns the single image in <paramref name="content"/> when it holds
    /// exactly one (ignoring whitespace-only literals and line breaks), else
    /// null. This is the "image alone on its own line" test that promotes an
    /// image to a rasterized block.
    /// </summary>
    private static MdImage? TrySingleImage(IReadOnlyList<MdInline> content)
    {
        MdImage? found = null;
        foreach (var inline in content)
        {
            if (inline is MdImage img)
            {
                if (found is not null) return null; // more than one image
                found = img;
            }
            else if (inline is MdLineBreak)
            {
                // ignore breaks around the image
            }
            else if (inline is MdLiteral lit && string.IsNullOrWhiteSpace(lit.Text))
            {
                // ignore surrounding whitespace
            }
            else
            {
                return null; // any other content → not image-only
            }
        }
        return found;
    }

    /// <summary>
    /// Resolves, decodes, scales and rasterizes <paramref name="img"/>, appending
    /// the encoded rows to <paramref name="result"/>. Returns false (leaving
    /// <paramref name="result"/> untouched) when the source can't be resolved or
    /// decoded, so the caller can fall back to alt text. Never throws.
    /// </summary>
    private static bool TryRenderImage(MdImage img, int width, MarkdownImageOptions images, List<string> result)
    {
        byte[]? bytes;
        try { bytes = images.Resolver(img.Url); }
        catch { bytes = null; }
        if (bytes is null || bytes.Length == 0) return false;

        try
        {
            var decoded = StbImageSharp.ImageResult.FromMemory(bytes, StbImageSharp.ColorComponents.RedGreenBlueAlpha);
            if (decoded is null || decoded.Width <= 0 || decoded.Height <= 0 || decoded.Data is null)
                return false;

            var rgba = decoded.Data;
            int srcW = decoded.Width, srcH = decoded.Height;

            // Bound the image to `width` columns and `MaxRows` rows, converting
            // each to a pixel budget for the chosen encoding (HalfBlock packs
            // 1×2 px/cell, Sextant 2×3, Sixel a full cell). Aspect preserved;
            // never upscale.
            var (maxPxW, maxPxH) = images.Mode switch
            {
                BoxRenderMode.Sixel     => (width * images.CellPixelWidth, images.MaxRows * images.CellPixelHeight),
                BoxRenderMode.Sextant   => (width * 2, images.MaxRows * 3),
                BoxRenderMode.HalfBlock => (width, images.MaxRows * 2),
                _                       => (width, images.MaxRows * 2),
            };
            var (dstW, dstH) = FitWithin(srcW, srcH, Math.Max(1, maxPxW), Math.Max(1, maxPxH));
            if (dstW != srcW || dstH != srcH)
                rgba = Downscale(rgba, srcW, srcH, dstW, dstH);

            using var sw = new StringWriter();
            BoxRenderer.EncodeImage(rgba, dstW, dstH, images.Mode, sw);

            // StringWriter.WriteLine emits the platform newline (\r\n on
            // Windows), so trim the stray \r each split line would otherwise
            // keep — it inflates visible width and, for Sixel, would surface
            // the leading blank line as a lone-\r entry.
            var any = false;
            foreach (var raw in sw.ToString().Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.Length > 0) { result.Add(line); any = true; }
            }
            return any;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Largest (w,h) ≤ (maxW,maxH) keeping the source aspect ratio; only shrinks.</summary>
    private static (int w, int h) FitWithin(int srcW, int srcH, int maxW, int maxH)
    {
        if (srcW <= maxW && srcH <= maxH) return (srcW, srcH);
        var scale = Math.Min((double)maxW / srcW, (double)maxH / srcH);
        return (Math.Max(1, (int)(srcW * scale)), Math.Max(1, (int)(srcH * scale)));
    }

    /// <summary>
    /// Area-average box downscale of an RGBA buffer. Each destination pixel is
    /// the mean of the source pixels it covers, so every source pixel is read
    /// exactly once (O(srcW·srcH)). Downscale only — callers never enlarge.
    /// </summary>
    private static byte[] Downscale(byte[] src, int srcW, int srcH, int dstW, int dstH)
    {
        var dst = new byte[dstW * dstH * 4];
        for (var dy = 0; dy < dstH; dy++)
        {
            var sy0 = dy * srcH / dstH;
            var sy1 = Math.Max(sy0 + 1, (dy + 1) * srcH / dstH);
            for (var dx = 0; dx < dstW; dx++)
            {
                var sx0 = dx * srcW / dstW;
                var sx1 = Math.Max(sx0 + 1, (dx + 1) * srcW / dstW);
                long r = 0, g = 0, b = 0, a = 0, n = 0;
                for (var sy = sy0; sy < sy1; sy++)
                {
                    var rowBase = sy * srcW;
                    for (var sx = sx0; sx < sx1; sx++)
                    {
                        var si = (rowBase + sx) * 4;
                        r += src[si]; g += src[si + 1]; b += src[si + 2]; a += src[si + 3];
                        n++;
                    }
                }
                var di = (dy * dstW + dx) * 4;
                dst[di]     = (byte)(r / n);
                dst[di + 1] = (byte)(g / n);
                dst[di + 2] = (byte)(b / n);
                dst[di + 3] = (byte)(a / n);
            }
        }
        return dst;
    }

    /// <summary>Last path segment of an image source, for the empty-alt fallback.</summary>
    private static string ImageName(string url)
    {
        if (string.IsNullOrEmpty(url)) return "image";
        var slash = url.LastIndexOfAny(['/', '\\']);
        var name = slash >= 0 ? url[(slash + 1)..] : url;
        return string.IsNullOrEmpty(name) ? url : name;
    }
}
