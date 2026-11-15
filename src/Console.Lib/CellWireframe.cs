using System.Collections.Immutable;
using DIR.Lib;

namespace Console.Lib;

/// <summary>
/// Wireframe checks for a cell frame: <see cref="Record"/> paints an arranged tree and records what the
/// painter put where, in cells, and <see cref="Check"/> judges the recording with DIR.Lib's own
/// <see cref="Wireframe.Check"/>, so text over text, text half over a control and a cut label with no
/// ellipsis fail on a cell surface exactly as on a pixel one. No rule is copied here.
/// </summary>
/// <remarks>
/// Recording happens at the paint call and not from the viewport's escape stream, because the stream only
/// holds the text AFTER it was trimmed: the untrimmed extent is what makes a cut visible. A text run is
/// recorded with <c>Text</c> as painted (so an ellipsis is seen), <c>Ink</c> as the full untrimmed run and
/// <c>Visible</c> as the part the painter actually wrote. One row is one unit tall (<c>FontSize</c> 1).
/// </remarks>
public static class CellWireframe
{
    /// <summary>A fill less opaque than this is a wash over what is under it, not a cover (as in DIR.Lib's renderer).</summary>
    private const byte OpaqueFrom = 0xC0;

    /// <summary>Paints <paramref name="arranged"/> to <paramref name="viewport"/> as <see cref="CellLayout.Paint(ITerminalViewport, ImmutableArray{Layout.ArrangedNode{int}}, Action{Layout.Content.Fill, Rect{int}})"/> does, and returns what it recorded.</summary>
    public static WireframeFrame Record(ITerminalViewport viewport, ImmutableArray<Layout.ArrangedNode<int>> arranged,
        Action<Layout.Content.Fill, Rect<int>>? drawFill = null, bool reverse = false)
    {
        var recorder = new Recorder(viewport.Size);
        CellLayout.Paint(viewport, arranged, drawFill, reverse, null, recorder);
        return recorder.Frame;
    }

    /// <summary>
    /// <see cref="Wireframe.Check"/> over a cell frame. The tolerance defaults to 0 cells (a cell is the
    /// smallest thing there is, so sharing one is sharing everything); <c>IgnoreText</c> and
    /// <c>IgnoreRegion</c> pass through unchanged.
    /// </summary>
    public static ImmutableArray<WireframeFinding> Check(WireframeFrame frame, WireframeOptions? options = null)
        => Wireframe.Check(frame, options ?? new WireframeOptions { Tolerance = 0f });

    /// <summary>Like <see cref="Record"/>, but keeps the cell facts the cell rules need.</summary>
    public static CellWireframeFrame RecordCells(ITerminalViewport viewport, ImmutableArray<Layout.ArrangedNode<int>> arranged,
        Action<Layout.Content.Fill, Rect<int>>? drawFill = null, bool reverse = false)
    {
        var recorder = new Recorder(viewport.Size);
        CellLayout.Paint(viewport, arranged, drawFill, reverse, null, recorder);
        return new CellWireframeFrame(recorder.Frame, recorder.Facts);
    }

    /// <summary>DIR.Lib's rules plus the cell rules, reported side by side.</summary>
    public static CellWireframeResult CheckCells(CellWireframeFrame frame, CellWireframeOptions? options = null)
        => new(Check(frame.Frame, options?.Pixel), CellRules.Judge(frame, options?.Rules ?? CellWireframeRules.All));

    /// <summary>One message for both kinds of finding.</summary>
    public static string Describe(CellWireframeResult result)
        => Wireframe.Describe(result.Findings) + string.Concat(result.CellFindings.Select(f => $"{System.Environment.NewLine}{f.Rule} at {f.Where}: {f.Message}"));

    internal sealed class Recorder((int Width, int Height) size)
    {
        private readonly List<WireframeText> _texts = [];
        private readonly List<WireframeFill> _fills = [];
        private readonly List<WireframeRegion> _regions = [];
        private readonly List<CellFact> _facts = [];
        private int _order;

        public ImmutableArray<CellFact> Facts => [.. _facts];

        public void Node(int depth, Rect<int> bounds, Rect<int> clipped, bool isText, bool ownFill, bool fillPainted)
            => _facts.Add(new CellFact(depth, bounds, clipped, isText, ownFill, fillPainted, -1, 0, 0));

        /// <summary>A Box leaf's own fill, on the fact just recorded for it.</summary>
        public void BoxFill(bool painted)
        {
            var last = _facts[^1];
            _facts[^1] = last with { OwnFill = true, FillPainted = last.FillPainted || painted };
        }

        /// <summary>Where the text just recorded went, on the fact just recorded for its leaf.</summary>
        public void TextFact(int row, int textLength, int paintedLength)
            => _facts[^1] = _facts[^1] with { Row = row, TextLength = textLength, PaintedLength = paintedLength };

        public WireframeFrame Frame => new(
            new RectF32(0f, 0f, size.Width, size.Height), [.. _texts], [.. _fills], [.. _regions]);

        public void Fill(Rect<int> rect, RGBAColor32 color)
        {
            if (color.Alpha < OpaqueFrom)
            {
                return;
            }

            var cut = CutToViewport(rect);
            if (cut.Width > 0f && cut.Height > 0f)
            {
                _fills.Add(new WireframeFill(_order++, cut, color));
            }
        }

        public void Region(Rect<int> bounds, Rect<int> clipped, HitResult hit)
        {
            _regions.Add(new WireframeRegion(_order++, ToRect(bounds), CutToViewport(clipped), hit));
        }

        /// <param name="box">The leaf rect (already cut to any scroll clip).</param>
        /// <param name="painted">The string as written, after any trim.</param>
        /// <param name="untrimmedLength">The length of the whole run before the trim.</param>
        /// <param name="inkColumn">Where the whole untrimmed run would start, aligned in the box.</param>
        /// <param name="row">The row it is on.</param>
        /// <param name="paintedStart">The column <paramref name="painted"/> started at.</param>
        public void Text(Rect<int> box, string painted, int untrimmedLength, int inkColumn, int row, int paintedStart)
        {
            var ink = new RectF32(inkColumn, row, untrimmedLength, 1f);
            var written = new RectF32(paintedStart, row, painted.Length, 1f);
            _texts.Add(new WireframeText(_order++, painted, ToRect(box), ink, CutToViewport(written), 1f));
        }

        private RectF32 CutToViewport(Rect<int> r)
        {
            var x = Math.Max(0, r.X);
            var y = Math.Max(0, r.Y);
            var right = Math.Min(size.Width, r.Right);
            var bottom = Math.Min(size.Height, r.Bottom);
            return right > x && bottom > y ? new RectF32(x, y, right - x, bottom - y) : new RectF32(x, y, 0f, 0f);
        }

        private RectF32 CutToViewport(RectF32 r)
        {
            var x = MathF.Max(0f, r.X);
            var y = MathF.Max(0f, r.Y);
            var right = MathF.Min(size.Width, r.Right);
            var bottom = MathF.Min(size.Height, r.Bottom);
            return right > x && bottom > y ? new RectF32(x, y, right - x, bottom - y) : new RectF32(x, y, 0f, 0f);
        }

        private static RectF32 ToRect(Rect<int> r) => new(r.X, r.Y, r.Width, r.Height);
    }
}
