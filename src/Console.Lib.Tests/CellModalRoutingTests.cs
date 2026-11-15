using DIR.Lib;
using Shouldly;
using Xunit;

namespace Console.Lib.Tests;

/// <summary>
/// A declared modal (<c>Builder.Modal</c>) painted through <see cref="CellLayout.Paint"/> is routed by DIR.Lib's
/// modal rule with no call from the host: input reaches the card and nothing beneath. Fakes only: no console, no clock.
/// </summary>
public class CellModalRoutingTests
{
    private sealed class Rig
    {
        public const int Width = 40;
        public const int Height = 12;

        private readonly CellBuffer _buffer = new() { ColorMode = ColorMode.TrueColor };
        private readonly CellBufferViewport _viewport;

        public Rig()
        {
            _buffer.Resize(Width, Height);
            _viewport = new CellBufferViewport(_buffer, Width, Height);
            Surface = new CellInputSurface(new WindowUiSettings());
            Router = CellInputRouter.Create(Surface, new BackgroundTaskTracker(), () => { });
            Base.Open("base", 0);
        }

        public CellInputSurface Surface { get; }

        public InputRouter Router { get; }

        public ListCursor Base { get; } = new();

        public ListCursor Card { get; } = new();

        public bool Open { get; private set; } = true;

        public int Dismissed { get; private set; }

        public List<int> BaseRowsFired { get; } = [];

        private static Layout.Node ListNode(string id, ListCursor cursor, Action<int> fired)
        {
            var rows = new Layout.Node[4];
            for (var i = 0; i < rows.Length; i++)
            {
                var index = i;
                rows[i] = Layout.Builder.Text($"{id}{i}", 1f).RowH(1).WStar()
                    .Clickable(new HitResult.ListItemHit(id, i), _ => fired(index));
            }

            return Layout.Builder.VStack(rows).ListOf(cursor, id);
        }

        public void Frame()
        {
            var page = ListNode("base", Base, BaseRowsFired.Add).WStar();
            Layout.Node? top = null;
            if (Open)
            {
                var card = ListNode("card", Card, _ => { }).Pad(1, 1).Bg(new RGBAColor32(10, 10, 10, 255)).WFixed(10).HFixed(6);
                top = Layout.Builder.AnchoredTo(new RectF32(25, 4, 0, 0), card);
            }

            var tree = Layout.Builder.Modal(page, top, () => { Dismissed++; Open = false; });
            CellLayout.Paint(_viewport, Layout.Engine.Arrange(tree, new Rect<int>(0, 0, Width, Height), CellMeasureContext.CellAuthored), Surface);
        }

        public bool Press(int column, int row)
            => Router.Handle(new InputEvent.MouseDown(column, row, MouseButton.Left, InputModifier.None, 1));

        public bool Key(InputKey key) => Router.Handle(new InputEvent.KeyDown(key, InputModifier.None));
    }

    [Fact]
    public void APressOnTheListUnderTheScrimDoesNothing()
    {
        var rig = new Rig();
        rig.Frame();

        // The scrim is the whole surface; press on a base row well away from the card.
        rig.Press(2, 1);

        rig.BaseRowsFired.ShouldBeEmpty();
        rig.Base.Index.ShouldBe(0);
    }

    [Fact]
    public void APressOutsideTheCardRunsOnDismiss()
    {
        var rig = new Rig();
        rig.Frame();

        rig.Press(2, 9);

        rig.Dismissed.ShouldBe(1);
    }

    [Fact]
    public void DownMovesTheCardsOwnListAndNotTheOneBeneath()
    {
        var rig = new Rig();
        rig.Frame();
        rig.Card.Open("card", 0);

        rig.Key(InputKey.Down);

        rig.Card.Index.ShouldBe(1);
        rig.Base.Index.ShouldBe(0);
    }

    [Fact]
    public void EscapeDismisses()
    {
        var rig = new Rig();
        rig.Frame();

        rig.Key(InputKey.Escape);

        rig.Dismissed.ShouldBe(1);
    }
}
