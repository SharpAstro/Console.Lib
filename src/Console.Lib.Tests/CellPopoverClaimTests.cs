using DIR.Lib;
using Shouldly;
using Xunit;

namespace Console.Lib.Tests;

/// <summary>
/// An open popover claims the pointer for its content: a press inside it, on a child or on its empty padding,
/// never reaches a list row painted under it and never moves the list cursor, while a press outside still
/// dismisses as the popover declares. Fakes only: no console, no clock.
/// </summary>
public class CellPopoverClaimTests
{
    private sealed class Rig
    {
        public const int Width = 40;
        public const int Height = 10;

        private readonly CellBuffer _buffer = new() { ColorMode = ColorMode.TrueColor };
        private readonly CellBufferViewport _viewport;

        public Rig()
        {
            _buffer.Resize(Width, Height);
            _viewport = new CellBufferViewport(_buffer, Width, Height);
            Surface = new CellInputSurface(new WindowUiSettings());
            Router = CellInputRouter.Create(Surface, new BackgroundTaskTracker(), () => Redraws++);
            Surface.ListCursor.Open("rows", 0);
            Popover.Open();
        }

        public int Redraws { get; private set; }

        public CellInputSurface Surface { get; }

        public InputRouter Router { get; }

        public PopoverState Popover { get; } = new();

        public List<int> RowsFired { get; } = [];

        public int ChildFired { get; private set; }

        private Layout.Node Tree()
        {
            var rows = new Layout.Node[6];
            for (var i = 0; i < rows.Length; i++)
            {
                var index = i;
                rows[i] = Layout.Builder.Text($"row {i}", 1f).RowH(1).WStar()
                    .Clickable(new HitResult.ListItemHit("rows", i), _ => RowsFired.Add(index));
            }

            // A 2-cell frame of empty padding round one child.
            var child = Layout.Builder.Text("ok", 1f).RowH(1).WFixed(8)
                .Clickable(new HitResult.ButtonHit("ok"), _ => ChildFired++);
            var content = Layout.Builder.VStack(child).Pad(2, 1).Bg(new RGBAColor32(10, 10, 10, 255));
            return Layout.Builder.Overlay(
                Layout.Builder.VStack(rows),
                Layout.Builder.Popover(new RectF32(0, 0, 12, 1), content, Popover));
        }

        public void Frame()
        {
            var arranged = Layout.Engine.Arrange(Tree(), new Rect<int>(0, 0, Width, Height), CellMeasureContext.CellAuthored);
            CellLayout.Paint(_viewport, arranged, Surface);
        }

        public bool Press(int column, int row)
            => Router.Handle(new InputEvent.MouseDown(column, row, MouseButton.Left, InputModifier.None, 1));

        /// <summary>The painted rect of the popover's one button.</summary>
        public Rect<float> ChildRect()
        {
            var painted = new List<ClickableRegion>();
            Surface.CollectPaintedRegions(painted);
            var r = painted.Single(p => p.Result is HitResult.ButtonHit);
            return new Rect<float>(r.X, r.Y, r.Width, r.Height);
        }
    }

    [Fact]
    public void APressOnAChildInsideThePopoverFiresTheChildNotTheRowUnderIt()
    {
        var rig = new Rig();
        rig.Frame();
        var child = rig.ChildRect();

        rig.Press((int)child.X + 1, (int)child.Y);

        rig.ChildFired.ShouldBe(1);
        rig.RowsFired.ShouldBeEmpty();
        rig.Surface.ListCursor.Index.ShouldBe(0);
        rig.Popover.IsOpen.ShouldBeTrue();
    }

    [Fact]
    public void APressOnThePopoversEmptyPaddingFiresNoRowAndMovesNoCursorAndKeepsItOpen()
    {
        var rig = new Rig();
        rig.Frame();
        var child = rig.ChildRect();

        // One cell left of the child, and one row above it, are the content's padding over list rows.
        rig.Press((int)child.X - 1, (int)child.Y);
        rig.Press((int)child.X + 1, (int)child.Y - 1);

        rig.RowsFired.ShouldBeEmpty();
        rig.ChildFired.ShouldBe(0);
        rig.Surface.ListCursor.Index.ShouldBe(0);
        rig.Popover.IsOpen.ShouldBeTrue();
    }

    [Fact]
    public void APressOutsideThePopoverDismissesItAndFiresNoRow()
    {
        var rig = new Rig();
        rig.Frame();

        rig.Press(30, 3);

        rig.Popover.IsOpen.ShouldBeFalse();
        rig.RowsFired.ShouldBeEmpty();
        rig.Surface.ListCursor.Index.ShouldBe(0);
    }

    [Fact]
    public void AnOpenPopoversClaimSettlesSoAfterPaintAsksForNoFurtherFrame()
    {
        var rig = new Rig();

        rig.Frame();
        rig.Router.AfterPaint();
        var afterFirst = rig.Redraws;
        rig.Frame();
        rig.Router.AfterPaint();

        rig.Redraws.ShouldBe(afterFirst, "the second frame repeats the same claim and must not ask for another");
        rig.Surface.Ui.PointerClaimSettled.ShouldBeTrue();
    }

    [Fact]
    public void APressOnThePopoversPaddingKeepsTheListThatOwnsTheArrows()
    {
        var surface = new CellInputSurface(new WindowUiSettings());
        var router = CellInputRouter.Create(surface, new BackgroundTaskTracker(), () => { });
        var buffer = new CellBuffer { ColorMode = ColorMode.TrueColor };
        buffer.Resize(Rig.Width, Rig.Height);
        var viewport = new CellBufferViewport(buffer, Rig.Width, Rig.Height);
        var popover = new PopoverState();
        var a = new ListCursor();
        var b = new ListCursor();

        Layout.Node ListNode(string id, ListCursor cursor)
        {
            var rows = new Layout.Node[4];
            for (var i = 0; i < rows.Length; i++)
            {
                rows[i] = Layout.Builder.Text($"{id}{i}", 1f).RowH(1).WStar()
                    .Clickable(new HitResult.ListItemHit(id, i));
            }

            return Layout.Builder.VStack(rows).ListOf(cursor, id);
        }

        void Frame()
        {
            var child = Layout.Builder.Text("ok", 1f).RowH(1).WFixed(8).Clickable(new HitResult.ButtonHit("ok"));
            var content = Layout.Builder.VStack(child).Pad(2, 1).Bg(new RGBAColor32(10, 10, 10, 255));
            var tree = Layout.Builder.Overlay(
                Layout.Builder.HStack(ListNode("a", a).WStar(), ListNode("b", b).WStar()),
                Layout.Builder.Popover(new RectF32(0, 0, 12, 1), content, popover));
            CellLayout.Paint(viewport, Layout.Engine.Arrange(tree, new Rect<int>(0, 0, Rig.Width, Rig.Height), CellMeasureContext.CellAuthored), surface);
        }

        Frame();
        router.Handle(new InputEvent.MouseDown(30, 1, MouseButton.Left, InputModifier.None, 1));
        popover.Open();
        Frame();
        var painted = new List<ClickableRegion>();
        surface.CollectPaintedRegions(painted);
        var ok = painted.Single(p => p.Result is HitResult.ButtonHit);
        router.Handle(new InputEvent.MouseDown((int)ok.X - 1, (int)ok.Y, MouseButton.Left, InputModifier.None, 1));

        router.Handle(new InputEvent.KeyDown(InputKey.Down, InputModifier.None)).ShouldBeTrue("list b still owns the arrows");
        b.Index.ShouldBe(2, "the press opened b at row 1 and Down stepped it once");
        a.IsOpen.ShouldBeFalse();
    }
}
