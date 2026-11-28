using System.Collections.Immutable;
using DIR.Lib;
using Shouldly;
using Xunit;

namespace Console.Lib.Tests;

/// <summary>
/// DIR.Lib's <c>Node.RevealsOnRowHover</c> / <c>Node.IsSelected</c> on the cell surface: a revealed node is
/// shown only while the pointer is in its nearest enclosing row (or the row is selected); hidden means not
/// painted, not registered, its room kept. And a tree row's long name ends in an ellipsis. In memory only.
/// </summary>
public class CellLayoutRowRevealTests
{
    private const int Width = 20;
    private static readonly RGBAColor32 Ink = new(0xdd, 0xdd, 0xdd, 0xff);
    private static readonly RGBAColor32 Row = new(0x20, 0x20, 0x30, 0xff);
    private static readonly RGBAColor32 Hover = new(0x30, 0x40, 0x60, 0xff);

    private sealed class Host
    {
        private readonly CellBuffer _buffer = new() { ColorMode = ColorMode.TrueColor };
        private readonly CellBufferViewport _viewport;
        private readonly int _width;

        public Host(int width = Width)
        {
            _width = width;
            _buffer.Resize(width, 3);
            _viewport = new CellBufferViewport(_buffer, width, 3, ColorMode.TrueColor);
            Surface = new CellInputSurface(new WindowUiSettings());
            Router = CellInputRouter.Create(Surface, new BackgroundTaskTracker(), () => { }, _ => false);
        }

        public CellInputSurface Surface { get; }

        public InputRouter Router { get; }

        public CellBuffer Buffer => _buffer;

        public ImmutableArray<Layout.ArrangedNode<int>> Arranged { get; private set; }

        public void Frame(Layout.Node tree, (float X, float Y)? pointer = null)
        {
            Surface.Pointer = pointer;
            Arranged = Layout.Engine.Arrange(tree, new Rect<int>(0, 0, _width, 3), CellMeasureContext.CellAuthored);
            CellLayout.Paint(_viewport, Arranged, Surface);
        }

        public bool Press(int column, int row)
            => Router.Handle(new InputEvent.MouseDown(column, row, MouseButton.Left, InputModifier.None, 1));

        public string Text(int row, int from = 0, int count = -1)
        {
            var end = count < 0 ? _width : from + count;
            return string.Concat(Enumerable.Range(from, end - from).Select(c => _buffer.BackAt(c, row).Glyph));
        }
    }

    private static Layout.Node Label() => Layout.Builder.Text("name", 1f, Ink).WStar();

    private static Layout.Node RunButton(Action? onRun = null) =>
        Layout.Builder.Text("[>]", 1f, Ink).RevealOnRowHover().Clickable(new HitResult.ButtonHit("run"), _ => onRun?.Invoke());

    private static Layout.Node RowOf(Layout.Node button, bool selected = false, Action? onRow = null)
    {
        var row = Layout.Builder.HStack(Label(), button).RowH(1).WStar().Bg(Row).BgHover(Hover)
            .Clickable(new HitResult.ButtonHit("row"), _ => onRow?.Invoke());
        return Layout.Builder.VStack(selected ? row.Selected() : row);
    }

    [Fact]
    public void AtRest_TheRevealedButtonIsNotPainted_AndCannotBePressed()
    {
        var host = new Host();
        int run = 0, row = 0;
        host.Frame(RowOf(RunButton(() => run++), onRow: () => row++));

        host.Text(0, Width - 3, 3).ShouldBe("   ", "the button's room is kept but nothing is drawn in it");
        host.Press(Width - 2, 0);

        run.ShouldBe(0, "a hidden button registers no region");
        row.ShouldBe(1, "the press lands on the row under it");
    }

    [Fact]
    public void WithThePointerInTheRow_TheButtonPaintsAndPresses()
    {
        var host = new Host();
        int run = 0;
        host.Frame(RowOf(RunButton(() => run++)), (1f, 0f));

        host.Text(0, Width - 3, 3).ShouldBe("[>]");
        host.Press(Width - 2, 0);

        run.ShouldBe(1);
    }

    [Fact]
    public void ASelectedRow_ShowsTheButtonWithThePointerElsewhere()
    {
        var host = new Host();
        host.Frame(RowOf(RunButton(), selected: true), (1f, 2f));

        host.Text(0, Width - 3, 3).ShouldBe("[>]");
    }

    [Fact]
    public void ThePointerOnAnotherRow_KeepsItHidden()
    {
        var host = new Host();
        host.Frame(RowOf(RunButton()), (1f, 2f));

        host.Text(0, Width - 3, 3).ShouldBe("   ");
    }

    [Fact]
    public void TheLabelIsIdenticalHiddenShownAndSelected()
    {
        var rest = new Host();
        var hover = new Host();
        var selected = new Host();
        rest.Frame(RowOf(RunButton()));
        hover.Frame(RowOf(RunButton()), (1f, 0f));
        selected.Frame(RowOf(RunButton(), selected: true), (1f, 2f));

        var label = rest.Text(0, 0, Width - 3);
        label.ShouldStartWith("name");
        // Room kept: every arranged rect is where it was, so a sibling that moved is seen here too.
        hover.Arranged.Select(n => n.Bounds).ShouldBe(rest.Arranged.Select(n => n.Bounds));
        selected.Arranged.Select(n => n.Bounds).ShouldBe(rest.Arranged.Select(n => n.Bounds));
        hover.Text(0, 0, Width - 3).ShouldBe(label);
        selected.Text(0, 0, Width - 3).ShouldBe(label);
    }

    [Fact]
    public void HitTest_TakesTheAskedCellAsThePointer()
    {
        var host = new Host();
        host.Frame(RowOf(RunButton()));

        CellLayout.HitTest(host.Arranged, Width - 2, 0).ShouldBe(new HitResult.ButtonHit("run"));
    }

    [Fact]
    public void ARevealNodeUnderNoRow_ResolvesAgainstItsOwnRect()
    {
        var host = new Host();
        var lone = Layout.Builder.VStack(Layout.Builder.Text("x", 1f, Ink).RevealOnRowHover());

        host.Frame(lone);
        host.Text(0, 0, 1).ShouldBe(" ");

        host.Frame(lone, (0f, 0f));
        host.Text(0, 0, 1).ShouldBe("x");
    }

    [Fact]
    public void ATreeRowsLongName_EndsInAnEllipsis_AndTheNoteIsPaintedWhole()
    {
        const int width = 24;
        var host = new Host(width);
        var name = Layout.Builder.WrapH(Layout.Builder.Text("a-very-long-directory-name.txt", 1f, Ink))
            .WithMaxLines(1, ellipsis: true).WStar();
        var note = Layout.Builder.Text("12 KB", 1f, Ink);
        var tree = Layout.Builder.VStack(Layout.Builder.HStack(name, note).RowH(1).WStar());

        host.Frame(tree);
        var text = host.Text(0);

        text.EndsWith("12 KB", StringComparison.Ordinal).ShouldBeTrue($"the note is painted whole: '{text}'");
        var nameArea = text[..(width - 5)].TrimEnd();
        nameArea.EndsWith('…').ShouldBeTrue($"the name ends in U+2026 at the cut: '{text}'");
        nameArea.ShouldStartWith("a-very-long");

        var buffer = new CellBuffer { ColorMode = ColorMode.TrueColor };
        buffer.Resize(width, 3);
        var frame = CellWireframe.RecordCells(new CellBufferViewport(buffer, width, 3, ColorMode.TrueColor), host.Arranged);
        var result = CellWireframe.CheckCells(frame);
        result.IsEmpty.ShouldBeTrue(CellWireframe.Describe(result));
    }
}
