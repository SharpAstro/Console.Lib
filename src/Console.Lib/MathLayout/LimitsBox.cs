using DIR.Lib;

namespace Console.Lib.MathLayout;

/// <summary>
/// Display-style limits attachment: <paramref name="lower"/> stacks
/// directly below the base, <paramref name="upper"/> directly above —
/// both centred on the base's horizontal axis. Used for big-operator
/// limits in math display mode (<c>\int_0^\infty</c>, <c>\sum_{i=0}^n</c>,
/// <c>\lim_{x \to 0}</c>) where TeX puts the limits above and below
/// instead of as scripts to the right.
///
/// Contrast with <see cref="SupSubBox"/>, which places sup/sub to the
/// right of the base (text/script style). Callers shrink the limit
/// boxes themselves via <see cref="BoxStyle.Smaller"/> before passing
/// them in, exactly like SupSubBox; the layout doesn't know about the
/// font scale.
/// </summary>
public sealed class LimitsBox : Box
{
    private readonly Box _base;
    private readonly Box? _lower;
    private readonly Box? _upper;
    private readonly float _gap;
    private readonly float _width;
    private readonly float _height;
    private readonly float _depth;

    public LimitsBox(Box @base, Box? lower, Box? upper, BoxStyle style)
    {
        _base = @base;
        _lower = lower;
        _upper = upper;
        // Small visible separation between base and each limit. ~0.1·em
        // matches what TeX uses for \displaystyle limits.
        _gap = style.FontSize * 0.1f;

        _width = MathF.Max(_base.Width, MathF.Max(lower?.Width ?? 0, upper?.Width ?? 0));
        _height = _base.Height + (upper is not null ? _gap + upper.TotalHeight : 0);
        _depth  = _base.Depth  + (lower is not null ? _gap + lower.TotalHeight  : 0);
    }

    public override float Width => _width;
    public override float Height => _height;
    public override float Depth => _depth;

    public override void Draw(RgbaImageRenderer renderer, float penX, float baselineY, BoxStyle style)
    {
        // Centre everything on the same vertical axis as the base. The
        // base's own baseline stays at the caller's baselineY, so a
        // LimitsBox embedded in an HBox aligns its base's baseline with
        // siblings — matching TeX's behaviour for inline-display sums.
        float centerX = penX + _width / 2f;

        float baseX = centerX - _base.Width / 2f;
        _base.Draw(renderer, baseX, baselineY, style);

        if (_upper is not null)
        {
            // Upper sits above the base: its bottom edge = top of base - gap.
            // Top of base = baselineY - _base.Height. Upper's baseline is
            // upper.Depth above its bottom edge.
            float upperX = centerX - _upper.Width / 2f;
            float upperBaseline = baselineY - _base.Height - _gap - _upper.Depth;
            _upper.Draw(renderer, upperX, upperBaseline, style);
        }

        if (_lower is not null)
        {
            // Lower sits below the base: its top edge = bottom of base + gap.
            // Bottom of base = baselineY + _base.Depth. Lower's baseline is
            // lower.Height below its top edge.
            float lowerX = centerX - _lower.Width / 2f;
            float lowerBaseline = baselineY + _base.Depth + _gap + _lower.Height;
            _lower.Draw(renderer, lowerX, lowerBaseline, style);
        }
    }
}
