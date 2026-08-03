using DIR.Lib;
using Shouldly;
using Xunit;

namespace Console.Lib.Tests;

/// <summary>
/// Where the tree's scroll bar is painted.
///
/// <para>
/// It used to be written with no cursor positioning at all, because until 4.10 a row was one string of
/// exactly <c>contentWidth</c> cells emitted in sequence — so the cursor arrived at the bar's column on its
/// own. Once rows became layout trees, <see cref="CellLayout"/> began positioning per text run and leaving
/// the cursor after the last glyph it drew, and the bar started landing immediately right of each row's
/// rightmost text: a different column on every row, reading as a stray block beside every label.
/// </para>
///
/// <para>
/// It only appears once the tree OVERFLOWS, so a small fixture never sees it, and on screen it is a single
/// cell that looks exactly like a font that cannot draw a glyph — which is what made it survive being
/// looked at directly several times. Asserting the column is the only way to hold it.
/// </para>
/// </summary>
public class TreeViewScrollBarTests
{
    private const char Thumb = '█';
    private const char Track = '│';

    private sealed class Node(string label) : ITreeNode<Node>
    {
        public List<Node> Kids { get; } = [];
        public IReadOnlyList<Node> Children => Kids;

        public Layout.Node BuildNodeContent(in RowContext context)
            => Layout.Builder.Text(label, 1f).WStar().HStar();
    }

    private sealed class BufferedViewport(CellBuffer buffer, int width, int height) : ITerminalViewport
    {
        public (int Column, int Row) Offset => (0, 0);
        public (int Width, int Height) Size => (width, height);
        public TermCell CellSize => new(10, 20);
        public ColorMode ColorMode => Console.Lib.ColorMode.Sgr16;

        public void SetCursorPosition(int left, int top) => buffer.MoveTo(left, top);
        public void Write(string text) => buffer.Write(text);
        public void WriteLine(string? text = null) { }
        public void Flush() { }
        public Stream OutputStream => Stream.Null;
    }

    /// <summary>Renders a root with <paramref name="childCount"/> children into a width x height grid.</summary>
    private static CellBuffer Render(int childCount, int width, int height)
    {
        var root = new Node("root");
        for (var i = 0; i < childCount; i++)
        {
            // Deliberately RAGGED label lengths: a uniform fixture would put the misplaced bar in the same
            // column on every row, which is indistinguishable from a correct one.
            root.Kids.Add(new Node(new string('x', 1 + i % 7)));
        }

        var buffer = new CellBuffer { ColorMode = ColorMode.Sgr16 };
        buffer.Resize(width, height);

        new TreeView<Node>(new BufferedViewport(buffer, width, height))
            .Root(root, expandRoot: true)
            .Render();

        return buffer;
    }

    private static bool IsBarGlyph(char c) => c is Thumb or Track;

    [Fact]
    public void WhenTheTreeOverflows_TheBarIsInTheLastColumnOfEveryRow()
    {
        const int width = 24, height = 8;
        var buffer = Render(childCount: 40, width, height);

        for (var row = 0; row < height; row++)
        {
            IsBarGlyph(buffer.BackAt(width - 1, row).Glyph)
                .ShouldBeTrue($"row {row} should carry the bar in its last column");
        }
    }

    /// <summary>
    /// The actual defect: the bar drawn at the row's text width instead of the viewport's. Asserting only
    /// that the last column HAS a bar would pass with a second bar sprayed across the middle of the tree.
    /// </summary>
    [Fact]
    public void TheBarAppearsNowhereButTheLastColumn()
    {
        const int width = 24, height = 8;
        var buffer = Render(childCount: 40, width, height);

        for (var row = 0; row < height; row++)
        {
            for (var col = 0; col < width - 1; col++)
            {
                IsBarGlyph(buffer.BackAt(col, row).Glyph)
                    .ShouldBeFalse($"a bar glyph at ({col},{row}) is the bar following the row's text");
            }
        }
    }

    /// <summary>A tree that fits has no bar at all, so no column is stolen from the content.</summary>
    [Fact]
    public void WhenTheTreeFits_ThereIsNoBar()
    {
        const int width = 24, height = 12;
        var buffer = Render(childCount: 3, width, height);

        for (var row = 0; row < height; row++)
        {
            for (var col = 0; col < width; col++)
            {
                IsBarGlyph(buffer.BackAt(col, row).Glyph).ShouldBeFalse($"({col},{row})");
            }
        }
    }

    /// <summary>
    /// The bar is contiguous: a run of thumb cells, then track. A bar that follows the text produces
    /// thumb/track glyphs in an order that has nothing to do with the scroll position.
    /// </summary>
    [Fact]
    public void TheThumbIsOneContiguousRun()
    {
        const int width = 24, height = 8;
        var buffer = Render(childCount: 40, width, height);

        var column = new string(Enumerable.Range(0, height)
            .Select(r => buffer.BackAt(width - 1, r).Glyph)
            .ToArray());

        column.ShouldNotContain($"{Track}{Thumb}", Case.Sensitive,
            "the thumb must be one run, not interleaved with track");
    }
}
