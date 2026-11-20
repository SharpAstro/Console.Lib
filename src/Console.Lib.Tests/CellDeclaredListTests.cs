using DIR.Lib;
using Shouldly;
using Xunit;

namespace Console.Lib.Tests;

/// <summary>
/// Lists that declare their own cursor (<c>Node.ListOf</c>) work from the keyboard on the cell surface with no
/// host code beyond handing events to the router: two widgets each own a <see cref="ListCursor"/>, each declares
/// its list into ONE tree, and the router steps the active one, the cell paint lights its row, and Enter fires
/// its handler. Fakes only: no console, no clock.
/// </summary>
public class CellDeclaredListTests
{
    private static readonly RGBAColor32 Focus = new(200, 40, 40, 255);

    private sealed class Widget(string id, int rows, int viewportRows = 0, int disabledFrom = -1, int disabledTo = -1)
    {
        public string Id { get; } = id;

        public ListCursor Cursor { get; } = new();

        public ListScrollController Scroll { get; } = new() { Mode = ScrollBarMode.None };

        public List<int> Activated { get; } = [];

        public Layout.Node Build()
        {
            var nodes = new Layout.Node[rows];
            for (var i = 0; i < nodes.Length; i++)
            {
                var index = i;
                nodes[i] = Layout.Builder.Text($"{Id}{i}", 1f).RowH(1).WStar()
                    .Clickable(new HitResult.ListItemHit(Id, index))
                    .Activatable(_ => Activated.Add(index))
                    .BgFocus(Focus);
                if (i >= disabledFrom && i <= disabledTo)
                {
                    nodes[i] = nodes[i].Disabled("not now");
                }
            }

            var list = Layout.Builder.VStack(nodes);
            if (viewportRows > 0)
            {
                list = list.WithScroll(Scroll).H(Layout.Sizing.Fixed(viewportRows)).WStar();
            }

            return Layout.Builder.VStack(list).ListOf(Cursor, Id);
        }
    }

    private sealed class Host
    {
        private const int Width = 40;
        private const int Height = 10;
        private readonly CellBuffer _buffer = new() { ColorMode = ColorMode.TrueColor };
        private readonly CellBufferViewport _viewport;

        public Host()
        {
            _buffer.Resize(Width, Height);
            _viewport = new CellBufferViewport(_buffer, Width, Height);
            Surface = new CellInputSurface(new WindowUiSettings());
            Router = CellInputRouter.Create(Surface, new BackgroundTaskTracker(), () => { });
        }

        public CellInputSurface Surface { get; }

        public InputRouter Router { get; }

        public void Frame(params Widget[] widgets)
        {
            var tree = Layout.Builder.HStack([.. widgets.Select(w => w.Build().WStar())]);
            var arranged = Layout.Engine.Arrange(tree, new Rect<int>(0, 0, Width, Height), CellMeasureContext.CellAuthored);
            CellLayout.Paint(_viewport, arranged, Surface);
        }

        public bool Key(InputKey key) => Router.Handle(new InputEvent.KeyDown(key, InputModifier.None));

        public bool Press(int column, int row)
            => Router.Handle(new InputEvent.MouseDown(column, row, MouseButton.Left, InputModifier.None, 1));

        public RGBAColor32 BackgroundAt(int column, int row) => _buffer.BackAt(column, row).Style.Background;
    }

    /// <summary>What the buffer stores for a row authored with <paramref name="colour"/> as its plain background.</summary>
    private static RGBAColor32 PaintedAuthoredFill(RGBAColor32 colour)
    {
        var buffer = new CellBuffer { ColorMode = ColorMode.TrueColor };
        buffer.Resize(10, 1);
        var viewport = new CellBufferViewport(buffer, 10, 1);
        var tree = Layout.Builder.Text("x", 1f).RowH(1).WStar().Bg(colour);
        var arranged = Layout.Engine.Arrange(tree, new Rect<int>(0, 0, 10, 1), CellMeasureContext.CellAuthored);
        CellLayout.Paint(viewport, arranged, new CellInputSurface(new WindowUiSettings()));
        return buffer.BackAt(5, 0).Style.Background;
    }

    [Fact]
    public void WithOneListDeclared_DownDownMovesItsCursorToIndexTwo()
    {
        var host = new Host();
        var list = new Widget("a", 5);
        list.Cursor.Open("a", 0);
        host.Frame(list);

        host.Key(InputKey.Down).ShouldBeTrue();
        host.Key(InputKey.Down).ShouldBeTrue();

        list.Cursor.Index.ShouldBe(2);
        host.Surface.ListCursor.IsOpen.ShouldBeFalse();
    }

    [Fact]
    public void WithTwoListsDeclared_TheOneLastPressedStepsAndTheOtherIsUnchanged()
    {
        var host = new Host();
        var a = new Widget("a", 5);
        var b = new Widget("b", 5);
        host.Frame(a, b);

        host.Press(20, 0); // row 0 of the right-hand list (rows are as wide as their text)
        b.Cursor.Index.ShouldBe(0, "the press opens the pressed list's cursor at the pressed row");
        host.Key(InputKey.Down);
        host.Key(InputKey.Down);

        b.Cursor.Index.ShouldBe(2);
        a.Cursor.IsOpen.ShouldBeFalse();
        a.Cursor.Index.ShouldBe(-1);
    }

    [Fact]
    public void TheNextPaintLightsRowTwoWithItsFocusBackground_AndOnlyInTheSteppedList()
    {
        var host = new Host();
        var a = new Widget("a", 5);
        var b = new Widget("b", 5);
        host.Frame(a, b);
        host.Press(20, 0);
        host.Key(InputKey.Down);
        host.Key(InputKey.Down);

        host.Frame(a, b);

        // The buffer may store the fill in the terminal's own palette, so row 2 is compared with its neighbours
        // rather than with the authored colour: only the cursor's row of the stepped list differs.
        var lit = host.BackgroundAt(20, 2);
        lit.Alpha.ShouldBe((byte)255);
        lit.ShouldBe(PaintedAuthoredFill(Focus), "the lit row is FocusBackground, as the same pipeline stores it");
        host.BackgroundAt(20, 1).ShouldNotBe(lit);
        host.BackgroundAt(20, 3).ShouldNotBe(lit);
        host.BackgroundAt(0, 2).ShouldNotBe(lit);
    }

    [Fact]
    public void EnterFiresRowTwosOnActivate_OnTheSteppedListOnly()
    {
        var host = new Host();
        var a = new Widget("a", 5);
        var b = new Widget("b", 5);
        host.Frame(a, b);
        host.Press(20, 0);
        host.Key(InputKey.Down);
        host.Key(InputKey.Down);

        host.Key(InputKey.Enter).ShouldBeTrue();

        b.Activated.ShouldBe([2]);
        a.Activated.ShouldBeEmpty();
    }

    [Fact]
    public void ADeclaredCursorScrolledByAViewportRevealsItsRow()
    {
        var host = new Host();
        var list = new Widget("a", 6, viewportRows: 3);
        host.Frame(list);

        for (var i = 0; i < 5; i++)
        {
            host.Key(InputKey.Down).ShouldBeTrue();
        }

        list.Cursor.Index.ShouldBe(4);
        list.Scroll.Offset.ShouldBe(2f);

        host.Frame(list); // the host repaints each frame, and row 4 now has a region
        host.Key(InputKey.Enter).ShouldBeTrue();
        list.Activated.ShouldBe([4]);
    }

    // DIR.Lib 11.90's rule in cells. A scrolled stack lays out only the rows within a viewport's extent of its
    // viewport, so a run of disabled rows taller than that holds no row the cursor can take, and the rows after it
    // are not in the frame: Down was refused as if the list had ended. The surface now scrolls the farthest row it
    // holds that way into view and says the list moved, so the key is taken and the next press continues.
    [Fact]
    public void ADeclaredListScrolledByAViewportGetsPastARunOfDisabledRowsTallerThanIt()
    {
        var host = new Host();
        var list = new Widget("a", 14, viewportRows: 2, disabledFrom: 2, disabledTo: 8);
        list.Cursor.Open("a", 1);
        host.Frame(list);

        var presses = 0;
        while (list.Cursor.Index != 9 && presses < 10)
        {
            host.Key(InputKey.Down).ShouldBeTrue($"press {presses + 1} is taken: the list goes on");
            host.Frame(list);
            presses++;
        }

        list.Cursor.Index.ShouldBe(9, "the first row after the seven disabled ones");

        // And back up across the same run.
        presses = 0;
        while (list.Cursor.Index != 1 && presses < 10)
        {
            host.Key(InputKey.Up).ShouldBeTrue($"press {presses + 1} up is taken");
            host.Frame(list);
            presses++;
        }

        list.Cursor.Index.ShouldBe(1);
    }

    [Fact]
    public void AtTheRealEndOfAScrolledListTheArrowIsStillRefused()
    {
        // Rows 3 to 5 are disabled and the list ends with them: the presses that bring them into view are taken,
        // and once they are in view there is nothing further.
        var host = new Host();
        var list = new Widget("a", 6, viewportRows: 2, disabledFrom: 3, disabledTo: 5);
        list.Cursor.Open("a", 2);
        host.Frame(list);

        var taken = 0;
        while (taken < 8 && host.Key(InputKey.Down))
        {
            host.Frame(list);
            taken++;
        }

        taken.ShouldBeLessThan(8, "the scroll reaches the end and the key is then refused, not taken for ever");
        list.Cursor.Index.ShouldBe(2, "no row after 2 can take the cursor");
    }
}
