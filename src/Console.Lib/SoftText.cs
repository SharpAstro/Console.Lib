namespace Console.Lib;

public enum HAlign { Left, Center, Right }

/// <summary>One styled run inside a <see cref="SoftLine"/>.</summary>
public readonly record struct SoftSpan(string Text, VtStyle? Style = null)
{
    /// <summary>Visible cell width contributed by this span. Equals <c>Text.Length</c>
    /// today; reserved for future wide-char accounting.</summary>
    public int VisibleLength => Text.Length;
}

/// <summary>
/// One row inside a <see cref="SoftText"/>. The line is composed of styled
/// <see cref="SoftSpan"/>s and is horizontally aligned within the parent's
/// <see cref="SoftText.Width"/>.
/// </summary>
public sealed record SoftLine(IReadOnlyList<SoftSpan> Spans, HAlign Align = HAlign.Center)
{
    /// <summary>Convenience: build a single-span line.</summary>
    public static SoftLine Of(string text, HAlign align = HAlign.Center, VtStyle? style = null)
        => new([new SoftSpan(text, style)], align);

    public int VisibleLength
    {
        get
        {
            int n = 0;
            foreach (var s in Spans) n += s.VisibleLength;
            return n;
        }
    }
}

/// <summary>
/// A logical block of text occupying <see cref="Width"/>×<see cref="Height"/>
/// terminal cells. Each <see cref="SoftLine"/> is independently aligned and
/// styled; lines shorter than <see cref="Width"/> are padded, longer lines
/// are truncated at the visible boundary (VT escape sequences are not counted
/// toward the visible width).
///
/// Useful when a widget needs to lay out a small rectangle of text with
/// internal alignment — e.g. a table cell with header/value/footer rows, or
/// a labelled status tile. Render via <see cref="SoftRenderer.Render"/>.
/// </summary>
public sealed class SoftText
{
    public int Width { get; }
    public int Height { get; }
    public IReadOnlyList<SoftLine> Lines { get; }

    public SoftText(int width, int height, IReadOnlyList<SoftLine> lines)
    {
        if (width < 1) throw new ArgumentOutOfRangeException(nameof(width));
        if (height < 1) throw new ArgumentOutOfRangeException(nameof(height));
        Width = width;
        Height = height;
        Lines = lines;
    }
}
