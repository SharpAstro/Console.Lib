using Console.Lib;
using Shouldly;
using Xunit;

namespace Console.Lib.Tests;

public sealed class SoftRendererTests
{
    [Fact]
    public void SingleLine_LeftAligned_PadsRight()
    {
        var capture = new CapturingTerminal(40, 5);
        var soft = new SoftText(10, 1, [SoftLine.Of("hi", HAlign.Left)]);

        SoftRenderer.Render(capture, 0, 0, soft, ColorMode.None);

        var stripped = StripVtEscapes(capture.Joined());
        stripped.ShouldBe("hi        ");
    }

    [Fact]
    public void SingleLine_RightAligned_PadsLeft()
    {
        var capture = new CapturingTerminal(40, 5);
        var soft = new SoftText(10, 1, [SoftLine.Of("hi", HAlign.Right)]);

        SoftRenderer.Render(capture, 0, 0, soft, ColorMode.None);

        StripVtEscapes(capture.Joined()).ShouldBe("        hi");
    }

    [Fact]
    public void SingleLine_CenterAligned_BalancesPadding()
    {
        var capture = new CapturingTerminal(40, 5);
        var soft = new SoftText(8, 1, [SoftLine.Of("ab", HAlign.Center)]);

        SoftRenderer.Render(capture, 0, 0, soft, ColorMode.None);

        // extra=6, leftPad=3, rightPad=3
        StripVtEscapes(capture.Joined()).ShouldBe("   ab   ");
    }

    [Fact]
    public void OverlongLine_TruncatesToWidth()
    {
        var capture = new CapturingTerminal(40, 5);
        var soft = new SoftText(4, 1, [SoftLine.Of("abcdefghi", HAlign.Left)]);

        SoftRenderer.Render(capture, 0, 0, soft, ColorMode.None);

        StripVtEscapes(capture.Joined()).ShouldBe("abcd");
    }

    [Fact]
    public void EmitsExactlyOneWritePerRow()
    {
        var capture = new CapturingTerminal(40, 5);
        var soft = new SoftText(6, 3,
        [
            SoftLine.Of("a", HAlign.Center),
            SoftLine.Of("bb", HAlign.Center),
            SoftLine.Of("ccc", HAlign.Center),
        ]);

        SoftRenderer.Render(capture, 0, 0, soft, ColorMode.None);

        capture.Writes.Count.ShouldBe(3);
    }

    [Fact]
    public void StyledSpan_EmitsApplyAndReset()
    {
        var capture = new CapturingTerminal(40, 5);
        var style = new VtStyle(SgrColor.Red, SgrColor.Black);
        var soft = new SoftText(4, 1, [new SoftLine([new SoftSpan("ab", style)], HAlign.Left)]);

        SoftRenderer.Render(capture, 0, 0, soft, ColorMode.Sgr16);

        var written = capture.Joined();
        written.ShouldContain("\u001b[");          // some SGR escape
        written.ShouldContain("ab");
        written.ShouldEndWith(VtStyle.Reset);
    }

    [Fact]
    public void OffViewport_RowsAreSkipped()
    {
        var capture = new CapturingTerminal(40, 2);
        var soft = new SoftText(4, 5, [
            SoftLine.Of("a"), SoftLine.Of("b"), SoftLine.Of("c"), SoftLine.Of("d"), SoftLine.Of("e"),
        ]);

        SoftRenderer.Render(capture, 0, 0, soft, ColorMode.None);

        // Viewport height=2, so only rows 0 and 1 fit; rows 2-4 silently skipped.
        capture.Writes.Count.ShouldBe(2);
    }

    private static string StripVtEscapes(string s)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '\u001b' && i + 1 < s.Length && s[i + 1] == '[')
            {
                i += 2;
                while (i < s.Length && s[i] != 'm' && s[i] != 'H') i++;
                continue;
            }
            sb.Append(s[i]);
        }
        return sb.ToString();
    }

    private sealed class CapturingTerminal : ITerminalViewport
    {
        public List<string> Writes { get; } = new();
        public List<(int Col, int Row)> Cursors { get; } = new();
        private readonly int _w, _h;

        public CapturingTerminal(int width, int height) { _w = width; _h = height; }

        public (int Column, int Row) Offset => (0, 0);
        public (int Width, int Height) Size => (_w, _h);
        public TermCell CellSize => new(10, 20);
        public Stream OutputStream => Stream.Null;
        public ColorMode ColorMode => ColorMode.None;

        public void SetCursorPosition(int left, int top) => Cursors.Add((left, top));
        public void Write(string text) => Writes.Add(text);
        public void WriteLine(string? text = null) => Writes.Add((text ?? "") + "\n");
        public void Flush() { }

        public string Joined() => string.Concat(Writes);
    }
}
