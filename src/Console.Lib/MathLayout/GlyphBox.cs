using System.Text;
using DIR.Lib;

namespace Console.Lib.MathLayout;

/// <summary>
/// A leaf box wrapping a string of text rasterized at a fixed font size. The
/// box's <see cref="Box.Width"/> is the advance width of the text;
/// <see cref="Box.Height"/> is the ascent; <see cref="Box.Depth"/> is the
/// descent. Sizing uses <see cref="RgbaImageRenderer.MeasureText"/> against
/// the same renderer that will eventually paint, so cache hits are reused.
/// </summary>
public sealed class GlyphBox : Box
{
    private readonly string _text;
    private readonly float _fontSize;
    private readonly float _width;
    private readonly float _height;
    private readonly float _depth;

    public GlyphBox(string text, BoxStyle style)
        : this(text, style, style.FontSize)
    { }

    public GlyphBox(string text, BoxStyle style, float fontSize)
    {
        _text = text;
        _fontSize = fontSize;

        // We need a temporary renderer to measure — MeasureText is an
        // instance method on RgbaImageRenderer, but it doesn't depend on the
        // surface dimensions, only on the cached glyph metrics. Construct a
        // 1×1 throwaway just to get the rasterizer; the cache is per-instance
        // so this allocates a tiny new font cache. For the demo's small
        // formula corpus that's fine; if it ever matters, we'd thread a
        // shared rasterizer through BoxStyle instead.
        using var measurer = new RgbaImageRenderer(1, 1);
        var (w, _) = measurer.MeasureText(text, style.FontPath, fontSize);
        _width = w;

        // The Height/Depth split has to match what RgbaImageRenderer.DrawText
        // *actually paints* — not just the glyph's intrinsic ascent/descent.
        // DrawText uses lineHeight = fontSize * 1.3 and positions the
        // baseline at  rectTop + (lineHeight + ascent - descent) / 2.
        // For a Near-vertical-aligned single-line render with the rect
        // we pass in Draw() below (top = baselineY - _height,
        // height = _height + _depth), the actual baseline lands exactly
        // at our intended baselineY iff _height = (lineHeight + ascent
        // - descent) / 2 AND _height + _depth >= lineHeight (so the
        // glyph row's full padding fits inside the rect).
        //
        // We don't have direct access to per-glyph (ascent, descent) here
        // — MeasureText only returns combined visual height. Use the
        // 0.8/0.2 split heuristic for typical Latin/Greek glyphs and pad
        // total to lineHeight so DrawText's line-height padding fits
        // without bottom-clipping descenders or fraction denominators.
        const float LineHeightFactor = 1.3f;
        var lineHeight = fontSize * LineHeightFactor;
        float maxAscent = 0, maxDescent = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune)) continue;
            var (_, h) = measurer.MeasureText(rune.ToString(), style.FontPath, fontSize);
            // Split the visual height into ascent/descent. 0.8/0.2 is
            // approximately right for Cambria/Consolas/STIX at typical
            // sizes — letters with true descenders (g, y, p) get the
            // ~20% descent they need.
            float ascent = h * 0.8f;
            float descent = h * 0.2f;
            if (ascent > maxAscent) maxAscent = ascent;
            if (descent > maxDescent) maxDescent = descent;
        }
        // Match DrawText's interpretation of the rect bounds. _height is
        // distance from rect-top down to baseline; _depth is distance from
        // baseline down to rect-bottom. Sum equals lineHeight exactly.
        _height = (lineHeight + maxAscent - maxDescent) / 2f;
        _depth  = lineHeight - _height;
    }

    public override float Width => _width;
    public override float Height => _height;
    public override float Depth => _depth;

    public override void Draw(RgbaImageRenderer renderer, float penX, float baselineY, BoxStyle style)
    {
        // RgbaImageRenderer.DrawText with TextAlign.Near vertically centres
        // text within the layout rect; we want baseline alignment instead.
        // Trick: pass a layout rect whose top sits at (baselineY - ascent)
        // and whose height is exactly the visual height — then Near vertical
        // alignment puts the first line at the top, baseline at +ascent,
        // which is exactly what we want.
        var rect = new RectInt(
            new PointInt((int)MathF.Ceiling(penX + _width), (int)MathF.Ceiling(baselineY + _depth)),
            new PointInt((int)MathF.Floor(penX), (int)MathF.Floor(baselineY - _height)));
        renderer.DrawText(_text, style.FontPath, _fontSize, style.Foreground, rect,
            horizAlignment: TextAlign.Near, vertAlignment: TextAlign.Near);
    }
}
