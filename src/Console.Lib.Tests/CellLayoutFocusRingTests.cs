using DIR.Lib;
using Shouldly;
using Xunit;

namespace Console.Lib.Tests;

/// <summary>
/// A node that declares <c>Node.FocusRing</c> shows focus on the cell surface (issue #107): while the declared
/// cursor is on its hit, its own cells carry the ring colour as a fill (an Accent button, already in that colour,
/// reverses instead), and the label stays WCAG-AA legible. Disabled nodes, a transparent or zero-width ring and a
/// cursor elsewhere paint as before. Fakes only: an in-memory buffer, no process, no clock.
/// </summary>
public class CellLayoutFocusRingTests
{
    private const int Width = 40;
    private const int Rows = 3;

    private static readonly UiTheme Dark = new()
    {
        Palette = new UiPalette
        {
            ContentBg = new(18, 18, 20, 255), PanelBg = new(28, 29, 32, 255), HeaderBg = new(40, 42, 46, 255),
            Separator = new(64, 66, 72, 255), BodyText = new(220, 222, 226, 255), DimText = new(150, 154, 162, 255),
            Accent = new(90, 160, 255, 255), Selection = new(50, 80, 130, 255), HeaderText = new(235, 237, 240, 255),
            SelectionText = new(255, 255, 255, 255), Info = new(90, 160, 255, 255), Warn = new(240, 190, 60, 255),
            Error = new(230, 80, 80, 255),
        },
        Metrics = new UiMetrics(14f, 6f, 28f, 24f, 28f),
    };

    private static readonly UiTheme Light = new()
    {
        Palette = new UiPalette
        {
            ContentBg = new(246, 247, 249, 255), PanelBg = new(255, 255, 255, 255), HeaderBg = new(236, 238, 242, 255),
            Separator = new(208, 212, 220, 255), BodyText = new(30, 32, 38, 255), DimText = new(92, 98, 110, 255),
            Accent = new(20, 90, 200, 255), Selection = new(200, 218, 245, 255), HeaderText = new(20, 22, 28, 255),
            SelectionText = new(0, 0, 0, 255), Info = new(20, 100, 210, 255), Warn = new(170, 100, 0, 255),
            Error = new(200, 30, 40, 255),
        },
        Metrics = new UiMetrics(14f, 6f, 28f, 24f, 28f),
    };

    public static TheoryData<string> Themes => ["dark", "light"];

    private static UiTheme ThemeOf(string name) => name == "dark" ? Dark : Light;

    private static HitResult Row(int i) => new HitResult.ListItemHit("l", i);

    private static (CellBuffer Buffer, CellWireframeFrame Frame) PaintBoth(Layout.Node content, int? cursorOn)
    {
        var cursor = new ListCursor();
        if (cursorOn is { } on)
        {
            cursor.Open("l", on);
        }

        var tree = Layout.Builder.VStack(content).ListOf(cursor, "l");
        var buffer = new CellBuffer { ColorMode = ColorMode.TrueColor };
        buffer.Resize(Width, Rows);
        var viewport = new CellBufferViewport(buffer, Width, Rows, ColorMode.TrueColor);
        var arranged = Layout.Engine.Arrange(tree, new Rect<int>(0, 0, Width, Rows), CellMeasureContext.PixelAuthored);
        var surface = new CellInputSurface(new WindowUiSettings());
        CellLayout.Paint(viewport, arranged, surface);

        var recordedBuffer = new CellBuffer { ColorMode = ColorMode.TrueColor };
        recordedBuffer.Resize(Width, Rows);
        var frame = CellWireframe.RecordCells(new CellBufferViewport(recordedBuffer, Width, Rows, ColorMode.TrueColor), arranged);
        return (buffer, frame);
    }

    private static CellBuffer Paint(Layout.Node content, int? cursorOn) => PaintBoth(content, cursorOn).Buffer;

    private static Layout.Node Btn(UiTheme t, Layout.ButtonKind kind, string label = "Save", string? disabled = null)
        => Layout.Builder.Button(label, kind, () => { }, t, disabledReason: disabled, hit: Row(0));

    private static List<Cell> Cells(CellBuffer b)
    {
        var cells = new List<Cell>();
        for (var r = 0; r < Rows; r++)
        {
            for (var c = 0; c < Width; c++)
            {
                cells.Add(b.BackAt(c, r));
            }
        }

        return cells;
    }

    private static void ShouldPaintSame(CellBuffer a, CellBuffer b) => Cells(a).ShouldBe(Cells(b));

    private static void ShouldPaintDifferently(CellBuffer a, CellBuffer b) => Cells(a).ShouldNotBe(Cells(b));

    // The glyphs of the buffer, a row a line: what a failure message shows of a paint.
    private static string Dump(CellBuffer b)
    {
        var rows = new List<string>();
        for (var r = 0; r < Rows; r++)
        {
            rows.Add(string.Concat(Enumerable.Range(0, Width).Select(c => b.BackAt(c, r).Glyph)));
        }

        return string.Join('\n', rows);
    }

    private static void LabelShouldBeLegible(CellBuffer b)
    {
        var inked = Cells(b).Where(c => char.IsLetterOrDigit((char)c.Glyph)).ToList();
        inked.ShouldNotBeEmpty(Dump(b));
        foreach (var cell in inked)
        {
            RGBAColor32.ContrastRatio(cell.Style.Foreground, cell.Style.Background).ShouldBeGreaterThanOrEqualTo(4.5f,
                $"'{cell.Glyph}' fg {cell.Style.Foreground} on bg {cell.Style.Background}");
        }
    }

    private static HashSet<(int, int)> Changed(CellBuffer a, CellBuffer b)
    {
        var set = new HashSet<(int, int)>();
        for (var r = 0; r < Rows; r++)
        {
            for (var c = 0; c < Width; c++)
            {
                if (a.BackAt(c, r) != b.BackAt(c, r))
                {
                    set.Add((c, r));
                }
            }
        }

        return set;
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void AStandardButtonPaintsDifferentlyFocused_AndItsLabelStaysLegible(string theme)
    {
        var t = ThemeOf(theme);
        var off = Paint(Btn(t, Layout.ButtonKind.Standard), null);
        var on = Paint(Btn(t, Layout.ButtonKind.Standard), 0);
        ShouldPaintDifferently(on, off);
        LabelShouldBeLegible(on);
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void AnAccentButtonPaintsDifferentlyFocused_AndItsLabelStaysLegible(string theme)
    {
        var t = ThemeOf(theme);
        var off = Paint(Btn(t, Layout.ButtonKind.Accent), null);
        var on = Paint(Btn(t, Layout.ButtonKind.Accent), 0);
        ShouldPaintDifferently(on, off);
        LabelShouldBeLegible(on);
    }

    [Theory]
    [MemberData(nameof(Themes))]
    public void ADisabledButtonPaintsAsItDidWithTheCursorOnIt(string theme)
    {
        var t = ThemeOf(theme);
        ShouldPaintSame(Paint(Btn(t, Layout.ButtonKind.Standard, disabled: "no"), 0), Paint(Btn(t, Layout.ButtonKind.Standard, disabled: "no"), null));
    }

    [Fact]
    public void ADisabledAncestorSuppressesTheRingOfWhatItHolds()
    {
        var node = Layout.Builder.VStack(Btn(Dark, Layout.ButtonKind.Standard)).Disabled("no");
        ShouldPaintSame(Paint(node, 0), Paint(node, null));
    }

    [Theory]
    [InlineData(0, 2f)]
    [InlineData(255, 0f)]
    public void ATransparentOrZeroWidthRingPaintsNothing(int alpha, float width)
    {
        var node = Layout.Builder.Text("Hi", 1f).RowH(1).WFixed(6).Bg(new RGBAColor32(10, 10, 10, 255))
            .Clickable(Row(0)).WithFocusRing(new RGBAColor32(255, 0, 0, (byte)alpha), width);
        ShouldPaintSame(Paint(node, 0), Paint(node, null));
    }

    [Fact]
    public void ACursorElsewherePaintsAsIfNoCursor()
    {
        ShouldPaintSame(Paint(Btn(Dark, Layout.ButtonKind.Standard), 5), Paint(Btn(Dark, Layout.ButtonKind.Standard), null));
    }

    [Fact]
    public void FocusBackgroundWinsOverTheRing()
    {
        var plain = Layout.Builder.Text("Hi", 1f).RowH(1).WFixed(6).Bg(new RGBAColor32(10, 10, 10, 255))
            .Clickable(Row(0)).BgFocus(new RGBAColor32(0, 200, 0, 255));
        var both = plain.WithFocusRing(new RGBAColor32(255, 0, 0, 255), 2f);
        ShouldPaintSame(Paint(both, 0), Paint(plain, 0));
    }

    [Fact]
    public void EachHalfOfASplitButtonLightsAlone()
    {
        Layout.Node Split() => Layout.Builder.SplitButton("Run", false, _ => { }, () => { }, Dark, hit: Row(0), detailsHit: Row(1));
        var off = Paint(Split(), null);
        var main = Paint(Split(), 0);
        var details = Paint(Split(), 1);

        var mainChanged = Changed(off, main);
        var detailsChanged = Changed(off, details);
        mainChanged.ShouldNotBeEmpty(Dump(main));
        detailsChanged.ShouldNotBeEmpty();
        mainChanged.Intersect(detailsChanged).ShouldBeEmpty("the halves do not share a cell");
        LabelShouldBeLegible(main);
        LabelShouldBeLegible(details);
    }

    [Fact]
    public void AChipPaintsDifferentlyFocused_AndItsLabelStaysLegible()
    {
        Layout.Node Chip() => Layout.Builder.Chip("Filter", false, _ => { }, Dark, hit: Row(0));
        var on = Paint(Chip(), 0);
        ShouldPaintDifferently(on, Paint(Chip(), null));
        LabelShouldBeLegible(on);
    }

    // A look-at capture, written only when PILOT_TEST_CAPTURE names a file (outside the repo); otherwise a no-op.
    [Fact]
    public void CaptureFocusedAndUnfocusedControls()
    {
        if (Environment.GetEnvironmentVariable("PILOT_TEST_CAPTURE") is not { Length: > 0 } path)
        {
            return;
        }

        var text = new System.Text.StringBuilder();
        foreach (var theme in new[] { ("dark", Dark), ("light", Light) })
        {
            foreach (var on in new int?[] { null, 0 })
            {
                var state = on is null ? "unfocused" : "focused";
                var cases = new (string, Func<Layout.Node>)[]
                {
                    ("Standard", () => Btn(theme.Item2, Layout.ButtonKind.Standard)),
                    ("Accent", () => Btn(theme.Item2, Layout.ButtonKind.Accent)),
                    ("Chip", () => Layout.Builder.Chip("Filter", false, _ => { }, theme.Item2, hit: Row(0))),
                    ("SplitButton", () => Layout.Builder.SplitButton("Run", false, _ => { }, () => { }, theme.Item2, hit: Row(0), detailsHit: Row(1))),
                };
                foreach (var (name, make) in cases)
                {
                    var b = Paint(make(), on);
                    var label = Enumerable.Range(0, Width).Select(c => b.BackAt(c, 1)).FirstOrDefault(c => char.IsLetter((char)c.Glyph));
                    text.AppendLine($"[{theme.Item1} {name} {state}] label bg={label.Style.Background} fg={label.Style.Foreground}");
                    text.AppendLine(Dump(b).TrimEnd());
                }
            }
        }

        File.WriteAllText(path, text.ToString());
    }

    [Fact]
    public void TheWireframeRecordsTheFocusedFillItSeesForFocusBackground()
    {
        // ResolveFill (neighbour fills) and the recorder read what Paint decides, so a focused button's frame differs.
        var on = PaintBoth(Btn(Dark, Layout.ButtonKind.Standard), 0).Frame.Frame.Fills;
        var off = PaintBoth(Btn(Dark, Layout.ButtonKind.Standard), null).Frame.Frame.Fills;
        on.ShouldNotBe(off);
    }
}
