using DIR.Lib;

namespace Console.Lib;

/// <summary>
/// A float measure context for planners that want fractional design units (DIR.Lib's
/// <c>MenuLayout.PlanPanel</c> / <c>BuildPanel</c> take <c>IMeasureContext&lt;float&gt;</c>). One design unit is
/// one cell, and a text run is its character count times the font size, so the planner's arithmetic (width
/// linear in size) holds exactly. The resulting tree is arranged with <see cref="CellMeasureContext.CellAuthored"/>.
/// </summary>
public sealed class CellFloatMeasureContext : Layout.IMeasureContext<float>
{
    /// <summary>The shared instance.</summary>
    public static CellFloatMeasureContext Instance { get; } = new();

    /// <inheritdoc />
    public Layout.Size<float> MeasureText(ReadOnlySpan<char> text, float fontSize) => new(text.Length * fontSize, fontSize);

    /// <inheritdoc />
    public float ToSurface(float designUnits) => designUnits;
}
