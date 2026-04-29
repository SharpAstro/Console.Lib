using System.Text;
using Console.Lib;
using Shouldly;
using Xunit;

namespace Console.Lib.Tests;

public sealed class ClipboardTests
{
    [Fact]
    public void SetText_EmitsOsc52WithBase64UtfPayload()
    {
        var vp = new CapturingViewport();
        Clipboard.SetText(vp, "Hello, world");

        var written = string.Concat(vp.Writes);
        var expectedB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("Hello, world"));
        written.ShouldBe($"\u001b]52;c;{expectedB64}\u0007");
        vp.FlushCount.ShouldBe(1);
    }

    [Fact]
    public void SetText_EmptyString_StillEmitsValidEscape()
    {
        var vp = new CapturingViewport();
        Clipboard.SetText(vp, "");

        // OSC 52 with empty payload is a clear-clipboard request — still valid.
        string.Concat(vp.Writes).ShouldBe("\u001b]52;c;\u0007");
    }

    [Fact]
    public void SetText_Utf8Payload_RoundTripsThroughBase64()
    {
        var vp = new CapturingViewport();
        // Mix of ASCII, Unicode supers, Greek, emoji.
        var text = "²³⁸U → α → ²³⁴Th 🧪";
        Clipboard.SetText(vp, text);

        var written = string.Concat(vp.Writes);
        // Strip the OSC framing and decode.
        written.ShouldStartWith("\u001b]52;c;");
        written.ShouldEndWith("\u0007");
        var b64 = written["\u001b]52;c;".Length..^1];
        Encoding.UTF8.GetString(Convert.FromBase64String(b64)).ShouldBe(text);
    }

    private sealed class CapturingViewport : ITerminalViewport
    {
        public List<string> Writes { get; } = [];
        public int FlushCount { get; private set; }

        public (int Column, int Row) Offset => (0, 0);
        public (int Width, int Height) Size => (80, 24);
        public TermCell CellSize => new(10, 20);
        public Stream OutputStream => Stream.Null;
        public ColorMode ColorMode => ColorMode.None;

        public void SetCursorPosition(int left, int top) { }
        public void Write(string text) => Writes.Add(text);
        public void WriteLine(string? text = null) => Writes.Add((text ?? "") + "\n");
        public void Flush() => FlushCount++;
    }
}
