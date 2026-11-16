using System.Collections.Immutable;
using System.Linq;
using Console.Lib;
using DIR.Lib;
using Shouldly;
using Xunit;

namespace Console.Lib.Tests;

/// <summary>
/// Tests for <see cref="MenuWidget"/>: BuildTree produces the expected arranged item count,
/// HitTest maps cells back to item clicks, and HandleKey navigates the model.
/// </summary>
public class MenuWidgetTests
{
    /// <summary>Minimal recording viewport for cell-layout tests (no color escapes).</summary>
    private sealed class SimpleViewport(int width, int height) : ITerminalViewport
    {
        public (int Column, int Row) Offset => (0, 0);
        public (int Width, int Height) Size => (width, height);
        public TermCell CellSize => new(10, 20);
        public ColorMode ColorMode => ColorMode.None;

        public void SetCursorPosition(int left, int top) { }
        public void Write(string text) { }
        public void WriteLine(string? text = null) { }
        public void Flush() { }
        public Stream OutputStream => Stream.Null;
    }

    private static readonly ImmutableArray<string> ThreeItems = ["Alpha", "Beta", "Gamma"];

    [Fact]
    public void BuildTree_ThreeItems_ProducesExpectedArrangedNodeCount()
    {
        // MenuLayout.BuildTree with 3 items = outer Stack containing:
        //   top spacer + title + prompt + gap box + 3 item leaves + bottom spacer = 8 children.
        // Layout.Engine.Arrange flattens the tree into one Layout.ArrangedNode per Layout.Node visited.
        var tree = MenuLayout.BuildTree(
            BuildModel("Title", "Pick:", ThreeItems),
            new MenuColors(),
            fontSize: 1f);

        var arranged = Layout.Engine.Arrange(tree, new Rect<int>(0, 0, 40, 20), new CellMeasureContext());

        // The outer Stack node + 8 children = 9 arranged nodes at minimum.
        arranged.Length.ShouldBeGreaterThanOrEqualTo(9);
    }

    [Fact]
    public void HitTest_ClickOnFirstItemRow_ConfirmsAtIndexZero()
    {
        // fontSize=1f, so each item row is Fixed(itemLineH = 2 cells tall).
        // Layout: top Star spacer | title(2) | prompt(2) | gap(0) | item0(2) | item1(2) | item2(2) | bottom Star.
        // With a 20-row viewport the Star spacers split the slack. Total content = 2+2+0+6 = 10 rows.
        // Slack = 20 - 10 = 10, each Star spacer gets 5 rows. So item0 starts at row 5+2+2+0 = 9.
        var vp = new SimpleViewport(40, 20);
        var widget = new MenuWidget(vp);
        widget.Reset("Title", "Pick:", ThreeItems);

        // Render to populate _arranged.
        widget.Render();

        // Click on column 5, row 9 should land on item 0.
        // Use pixel coords: viewport offset=(0,0), cellSize=(10,20), so pixel (50, 180) -> (col=5, row=9).
        var clicked = widget.HandleMouse(new MouseEvent(0, 50, 180, IsRelease: false));

        clicked.ShouldBeTrue();
        widget.IsConfirmed.ShouldBeTrue();
        widget.SelectedIndex.ShouldBe(0);
    }

    [Fact]
    public void HandleKey_DownThenEnter_ConfirmsAtIndexOne()
    {
        var vp = new SimpleViewport(40, 20);
        var widget = new MenuWidget(vp);
        widget.Reset("Title", "Pick:", ThreeItems);

        widget.HandleKey(InputKey.Down).ShouldBeTrue();
        widget.SelectedIndex.ShouldBe(1);
        widget.IsConfirmed.ShouldBeFalse();

        widget.HandleKey(InputKey.Enter).ShouldBeTrue();
        widget.IsConfirmed.ShouldBeTrue();
        widget.SelectedIndex.ShouldBe(1);
    }

    [Fact]
    public void HandleKey_UpWraps_FromFirstToLast()
    {
        var vp = new SimpleViewport(40, 20);
        var widget = new MenuWidget(vp);
        widget.Reset("Title", "Pick:", ThreeItems);

        // SelectedIndex starts at 0; Up should wrap to last item (index 2).
        widget.HandleKey(InputKey.Up).ShouldBeTrue();
        widget.SelectedIndex.ShouldBe(2);
    }

    [Fact]
    public void HandleKey_D2_ConfirmsAtIndexOne()
    {
        var vp = new SimpleViewport(40, 20);
        var widget = new MenuWidget(vp);
        widget.Reset("Title", "Pick:", ThreeItems);

        widget.HandleKey(InputKey.D2).ShouldBeTrue();
        widget.IsConfirmed.ShouldBeTrue();
        widget.SelectedIndex.ShouldBe(1);
    }

    [Fact]
    public void Reset_ClearsConfirmedState()
    {
        var vp = new SimpleViewport(40, 20);
        var widget = new MenuWidget(vp);
        widget.Reset("Title", "Pick:", ThreeItems);
        widget.HandleKey(InputKey.Enter);
        widget.IsConfirmed.ShouldBeTrue();

        // Reset clears IsConfirmed.
        widget.Reset("New Title", "Pick again:", ThreeItems);
        widget.IsConfirmed.ShouldBeFalse();
        widget.SelectedIndex.ShouldBe(0);
    }

    private static (CellBuffer Buffer, CellBufferViewport Viewport) Backdrop(int w, int h)
    {
        var buffer = new CellBuffer { ColorMode = ColorMode.Sgr16 };
        buffer.Resize(w, h);
        var viewport = new CellBufferViewport(buffer, w, h);
        for (var y = 0; y < h; y++)
        {
            viewport.SetCursorPosition(0, y);
            viewport.Write(new string('.', w));
        }
        return (buffer, viewport);
    }

    private static string RowText(CellBuffer b, int row)
        => string.Concat(Enumerable.Range(0, b.Width).Select(x => b.BackAt(x, row).Glyph));

    [Fact]
    public void RenderPanel_PaintsCardOverUntouchedBackdrop()
    {
        var (buffer, vp) = Backdrop(60, 24);
        var widget = new MenuWidget(vp);
        widget.Reset("Title", "Pick:", ThreeItems);

        widget.RenderPanel();

        var top = Enumerable.Range(0, 24).First(r => RowText(buffer, r).Contains('┌'));
        var left = RowText(buffer, top).IndexOf('┌');
        var right = RowText(buffer, top).IndexOf('┐');
        left.ShouldBe(18);
        right.ShouldBe(41);
        buffer.BackAt(left + 1, top).Glyph.ShouldBe('─');
        var bottom = Enumerable.Range(top + 1, 23 - top).First(r => RowText(buffer, r).Contains('└'));
        buffer.BackAt(left, bottom).Glyph.ShouldBe('└');
        buffer.BackAt(right, bottom).Glyph.ShouldBe('┘');
        buffer.BackAt(left, top + 1).Glyph.ShouldBe('│');
        Enumerable.Range(top, bottom - top + 1).Any(r => RowText(buffer, r).Contains("Beta")).ShouldBeTrue();

        // The scrim is transparent: every cell outside the card is still the backdrop.
        for (var y = 0; y < 24; y++)
            for (var x = 0; x < 60; x++)
                if (x < left || x > right || y < top || y > bottom)
                    buffer.BackAt(x, y).Glyph.ShouldBe('.', $"cell ({x},{y}) is outside the card");
    }

    [Fact]
    public void RenderPanel_PressOnScrimIsConsumed_PressOnRowConfirms()
    {
        var (buffer, vp) = Backdrop(60, 24);
        var widget = new MenuWidget(vp);
        widget.Reset("Title", "Pick:", ThreeItems);
        widget.RenderPanel();

        widget.HandleMouse(new MouseEvent(0, 5 * 10 + 5, 2 * 20 + 5, IsRelease: false)).ShouldBeTrue();
        widget.IsConfirmed.ShouldBeFalse();

        var beta = Enumerable.Range(0, 24).First(r => RowText(buffer, r).Contains("Beta"));
        widget.HandleMouse(new MouseEvent(0, 23 * 10 + 5, beta * 20 + 5, IsRelease: false)).ShouldBeTrue();
        widget.IsConfirmed.ShouldBeTrue();
        widget.SelectedIndex.ShouldBe(1);
    }

    [Fact]
    public void RenderPanel_PortraitViewport_CardIsFullWidthLessSideMargins()
    {
        var (buffer, vp) = Backdrop(30, 60);
        var widget = new MenuWidget(vp);
        widget.Reset("Title", "Pick:", ThreeItems);

        widget.RenderPanel();

        var top = Enumerable.Range(0, 60).First(r => RowText(buffer, r).Contains('┌'));
        var line = RowText(buffer, top);
        var margin = (int)widget.PanelStyle.SideMargin;
        line.IndexOf('┌').ShouldBe(margin);
        (line.IndexOf('┐') - line.IndexOf('┌') + 1).ShouldBe(30 - 2 * margin);
    }

    private const int PanelW = 60, PanelH = 24;

    private static (CellBuffer Buffer, MenuWidget Widget) Panel(Action<MenuWidget>? configure = null, string? closeMark = null)
    {
        var (buffer, vp) = Backdrop(PanelW, PanelH);
        var widget = closeMark is null ? new MenuWidget(vp) : new MenuWidget(vp) { CloseMark = closeMark };
        widget.Reset("Title", "Pick:", ThreeItems);
        configure?.Invoke(widget);
        widget.RenderPanel();
        return (buffer, widget);
    }

    private static (int Col, int Row)? Find(CellBuffer b, string text)
    {
        for (var r = 0; r < PanelH; r++)
        {
            var i = RowText(b, r).IndexOf(text, StringComparison.Ordinal);
            if (i >= 0) return (i, r);
        }
        return null;
    }

    private static MouseEvent PressAt(int col, int row) => new(0, col * 10 + 5, row * 20 + 5, IsRelease: false);

    private static int CardHeight(CellBuffer b)
    {
        var rows = Enumerable.Range(0, PanelH).Where(r => RowText(b, r).Contains('┌') || RowText(b, r).Contains('└')).ToArray();
        return rows[^1] - rows[0] + 1;
    }

    [Fact]
    public void RenderPanel_NeitherAction_HasNoHeaderRow()
    {
        var (buffer, widget) = Panel();

        Find(buffer, "< Back").ShouldBeNull();
        Find(buffer, "×").ShouldBeNull();
        widget.Back.ShouldBeNull();
        widget.Close.ShouldBeNull();
    }

    [Fact]
    public void RenderPanel_BackOnly_ShowsBackWithoutClose()
    {
        var (none, _) = Panel();
        var (buffer, _) = Panel(w => w.Back = () => { });

        Find(buffer, "< Back").ShouldNotBeNull();
        Find(buffer, "×").ShouldBeNull();
        CardHeight(buffer).ShouldBeGreaterThan(CardHeight(none));
    }

    [Fact]
    public void RenderPanel_CloseOnly_ShowsCloseWithoutBack()
    {
        var (buffer, _) = Panel(w => w.Close = () => { });

        Find(buffer, "×").ShouldNotBeNull();
        Find(buffer, "< Back").ShouldBeNull();
    }

    [Fact]
    public void RenderPanel_BothActions_ShowsBackLeftOfCloseOnOneRow()
    {
        var (buffer, _) = Panel(w => { w.Back = () => { }; w.Close = () => { }; });

        var back = Find(buffer, "< Back").ShouldNotBeNull();
        var close = Find(buffer, "×").ShouldNotBeNull();
        close.Row.ShouldBe(back.Row);
        close.Col.ShouldBeGreaterThan(back.Col);
    }

    [Fact]
    public void HandleMouse_PressOnBack_RunsBackOnceAndConfirmsNothing()
    {
        var backs = 0;
        var closes = 0;
        var (buffer, widget) = Panel(w => { w.Back = () => backs++; w.Close = () => closes++; });
        var back = Find(buffer, "< Back").ShouldNotBeNull();

        widget.HandleMouse(PressAt(back.Col + 2, back.Row)).ShouldBeTrue();

        backs.ShouldBe(1);
        closes.ShouldBe(0);
        widget.IsConfirmed.ShouldBeFalse();
    }

    [Fact]
    public void HandleMouse_PressOnCloseMark_RunsCloseOnceAndConfirmsNothing()
    {
        var backs = 0;
        var closes = 0;
        var (buffer, widget) = Panel(w => { w.Back = () => backs++; w.Close = () => closes++; });
        var close = Find(buffer, "×").ShouldNotBeNull();

        widget.HandleMouse(PressAt(close.Col, close.Row)).ShouldBeTrue();

        closes.ShouldBe(1);
        backs.ShouldBe(0);
        widget.IsConfirmed.ShouldBeFalse();
    }

    [Fact]
    public void RenderPanel_CloseMarkX_PaintsXWhereDefaultPaintsMultiplicationSign()
    {
        var (def, _) = Panel(w => w.Close = () => { });
        var (ascii, widget) = Panel(w => w.Close = () => { }, closeMark: "x");

        var defAt = Find(def, "×").ShouldNotBeNull();
        Find(ascii, "×").ShouldBeNull();
        Find(ascii, "x").ShouldNotBeNull();
        var x = Find(ascii, "x").ShouldNotBeNull();
        x.Row.ShouldBe(defAt.Row);
        widget.CloseMark.ShouldBe("x");
    }

    [Fact]
    public void HandleMouse_PressOnWideCloseMark_RunsClose()
    {
        var closes = 0;
        var (buffer, widget) = Panel(w => w.Close = () => closes++, closeMark: "[x]");
        var close = Find(buffer, "[x]").ShouldNotBeNull();

        widget.HandleMouse(PressAt(close.Col + 1, close.Row)).ShouldBeTrue();

        closes.ShouldBe(1);
        widget.IsConfirmed.ShouldBeFalse();
    }

    // ---- Console.Lib #96: the card's content is centred on every step ----

    private static (CellBuffer Buffer, int Left, int Right) CentredPanel(string title, string prompt, string[] items, bool back = false, bool close = false)
    {
        var (buffer, vp) = Backdrop(120, 30);
        var widget = new MenuWidget(vp);
        widget.Reset(title, prompt, [.. items]);
        if (back) widget.Back = () => { };
        if (close) widget.Close = () => { };
        widget.RenderPanel();
        var top = Enumerable.Range(0, 30).First(r => RowText(buffer, r).Contains('┌'));
        var line = RowText(buffer, top);
        return (buffer, line.IndexOf('┌'), line.IndexOf('┐'));
    }

    private static (int Start, int Len, int Row) TextAt(CellBuffer b, string text)
    {
        for (var r = 0; r < 30; r++)
        {
            var i = RowText(b, r).IndexOf(text, StringComparison.Ordinal);
            if (i >= 0) return (i, text.Length, r);
        }
        throw new Xunit.Sdk.XunitException($"'{text}' not painted");
    }

    private static void AssertCentred(CellBuffer b, int left, int right, string text)
    {
        var (start, len, _) = TextAt(b, text);
        var cardCentre = (left + right) / 2.0;
        var centre = start + (len - 1) / 2.0;
        Math.Abs(centre - cardCentre).ShouldBeLessThanOrEqualTo(0.5, $"'{text}' centre {centre} vs card centre {cardCentre}");
        (start - left).ShouldBeGreaterThanOrEqualTo(2, $"'{text}' needs a blank cell inside the left border");
        (right - (start + len - 1)).ShouldBeGreaterThanOrEqualTo(2, $"'{text}' needs a blank cell inside the right border");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void RenderPanel_GameModeStep_ContentCentredOnCard(bool back, bool close)
    {
        string[] items = ["Player vs Player", "Player vs Computer", "Custom Game"];
        var (b, l, r) = CentredPanel("♚ Chess ♔", "Select game mode:", items, back, close);
        AssertCentred(b, l, r, "♚ Chess ♔");
        AssertCentred(b, l, r, "Select game mode:");
        foreach (var it in items) AssertCentred(b, l, r, it);
        var (s, _, row) = TextAt(b, "Player vs Player");
        RowText(b, row).IndexOf('▶').ShouldBeLessThan(s);
        RowText(b, row).IndexOf('▶').ShouldBeGreaterThan(l);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void RenderPanel_PlayAsStep_ContentCentredAndHeaderEdges(bool back, bool close)
    {
        string[] items = ["White", "Black"];
        var (b, l, r) = CentredPanel("♚ Chess ♔", "Play as:", items, back, close);
        AssertCentred(b, l, r, "♚ Chess ♔");
        AssertCentred(b, l, r, "Play as:");
        foreach (var it in items) AssertCentred(b, l, r, it);
        var (bs, _, brow) = TextAt(b, "< Back");
        (bs - l).ShouldBeGreaterThanOrEqualTo(2);
        bs.ShouldBeLessThan((l + r) / 2);
        if (close)
        {
            var x = RowText(b, brow).IndexOf('×');
            x.ShouldBeGreaterThan((l + r) / 2);
            (r - x).ShouldBeGreaterThanOrEqualTo(2);
        }
    }

    // ---- Console.Lib #96: one terminal line per row ----

    private static (CellBuffer Buffer, int W, int H) SizedPanel(int w, int h, string title, string prompt, string[] items, bool back = false)
    {
        var (buffer, vp) = Backdrop(w, h);
        var widget = new MenuWidget(vp);
        widget.Reset(title, prompt, [.. items]);
        if (back) widget.Back = () => { };
        widget.RenderPanel();
        return (buffer, w, h);
    }

    private static string Picture(CellBuffer b, int h) => string.Join(Environment.NewLine, Enumerable.Range(0, h).Select(r => RowText(b, r)));

    private static (int Top, int Bottom) CardRows(CellBuffer b, int h)
    {
        var rows = Enumerable.Range(0, h).Where(r => RowText(b, r).Contains('┌') || RowText(b, r).Contains('└')).ToArray();
        return (rows[0], rows[^1]);
    }

    private static int RowOf(CellBuffer b, int h, string text)
    {
        for (var r = 0; r < h; r++)
            if (RowText(b, r).Contains(text, StringComparison.Ordinal)) return r;
        throw new Xunit.Sdk.XunitException($"'{text}' not painted:{Environment.NewLine}{Picture(b, h)}");
    }

    [Fact]
    public void RenderPanel_GameModeStep_ItemsOnConsecutiveRowsAndCardHeightIsRowsPlusPadding()
    {
        string[] items = ["Player vs Player", "Player vs Computer", "Custom Game"];
        var (b, _, h) = SizedPanel(120, 30, "♚ Chess ♔", "Select game mode:", items);
        var pic = Picture(b, h);

        var rows = items.Select(i => RowOf(b, h, i)).ToArray();
        rows[1].ShouldBe(rows[0] + 1, pic);
        rows[2].ShouldBe(rows[1] + 1, pic);
        var title = RowOf(b, h, "♚ Chess ♔");
        RowOf(b, h, "Select game mode:").ShouldBe(title + 1, pic);
        // one blank row between prompt and items
        rows[0].ShouldBe(title + 3, pic);

        // border 2 + padding 2 + title 1 + prompt 1 + gap 1 + 3 items
        var (top, bottom) = CardRows(b, h);
        (bottom - top + 1).ShouldBe(2 + 2 + 1 + 1 + 1 + 3, pic);
        (title - top).ShouldBe(2, pic);
        (bottom - rows[2]).ShouldBe(2, pic);
    }

    [Fact]
    public void RenderPanel_NineItemsFitA120x16Viewport()
    {
        var items = Enumerable.Range(1, 9).Select(i => $"Item {i}").ToArray();
        var (b, _, h) = SizedPanel(120, 16, "Title", "Pick one:", items);
        var pic = Picture(b, h);

        foreach (var it in items) RowOf(b, h, it);
        var (top, bottom) = CardRows(b, h);
        top.ShouldBeGreaterThanOrEqualTo(0, pic);
        bottom.ShouldBeLessThan(h, pic);
        RowOf(b, h, "Item 9").ShouldBeLessThan(bottom, pic);
    }

    [Fact]
    public void RenderPanel_BackAddsExactlyOneRow()
    {
        string[] items = ["White", "Black"];
        var (without, _, h) = SizedPanel(120, 30, "Chess", "Play as:", items);
        var (with, _, _) = SizedPanel(120, 30, "Chess", "Play as:", items, back: true);

        var a = CardRows(without, h);
        var c = CardRows(with, h);
        (c.Bottom - c.Top).ShouldBe(a.Bottom - a.Top + 1, Picture(with, h));
    }

    [Theory]
    [InlineData(30, false)]
    [InlineData(30, true)]
    [InlineData(21, false)]
    [InlineData(24, true)]
    public void RenderPanel_CardIsVerticallyCentred(int h, bool back)
    {
        var (b, _, _) = SizedPanel(120, h, "Chess", "Play as:", ["White", "Black"], back);
        var (top, bottom) = CardRows(b, h);
        Math.Abs(top - (h - 1 - bottom)).ShouldBeLessThanOrEqualTo(1, Picture(b, h));
    }

    private static MenuModel BuildModel(string title, string prompt, ImmutableArray<string> items)
    {
        var m = new MenuModel();
        m.Reset(title, prompt, items);
        return m;
    }
}
