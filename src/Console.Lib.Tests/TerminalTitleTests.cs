using Console.Lib;
using DIR.Lib;
using Shouldly;
using Xunit;

namespace Console.Lib.Tests;

/// <summary>
/// The terminal title (OSC 2) with the xterm title stack: the first title pushes the user's title
/// (CSI 22;0 t), dispose pops it (CSI 23;0 t). The protocol is a pure function, asserted without a console.
/// </summary>
public sealed class TerminalTitleTests
{
    private const string Push = "\e[22;0t";
    private const string Pop = "\e[23;0t";

    [Fact]
    public void FirstSet_PushesThenSetsTitle()
    {
        var (escape, last, pushed) = VirtualTerminal.TitleTransition("Hello", null, false, false);
        escape.ShouldBe(Push + "\e]2;Hello\e\\");
        last.ShouldBe("Hello");
        pushed.ShouldBeTrue();
    }

    [Fact]
    public void SameTitleTwice_EmitsOnce()
    {
        var (_, last, pushed) = VirtualTerminal.TitleTransition("Hello", null, false, false);
        var (escape, last2, pushed2) = VirtualTerminal.TitleTransition("Hello", last, pushed, false);
        escape.ShouldBe("");
        last2.ShouldBe("Hello");
        pushed2.ShouldBeTrue();
    }

    [Fact]
    public void DifferentTitle_EmitsOsc2OnlyWithoutSecondPush()
    {
        var (escape, last, pushed) = VirtualTerminal.TitleTransition("B", "A", true, false);
        escape.ShouldBe("\e]2;B\e\\");
        last.ShouldBe("B");
        pushed.ShouldBeTrue();
    }

    [Fact]
    public void ControlCharacters_AreStripped()
    {
        var (escape, last, _) = VirtualTerminal.TitleTransition("a\e]0;x\ab\u0085c\u007Fd\n", null, false, false);
        escape.ShouldBe(Push + "\e]2;a]0;xbcd\e\\");
        last.ShouldBe("a]0;xbcd");
    }

    [Fact]
    public void Suppressed_EmitsNothingAndNeverPushes()
    {
        var (escape, last, pushed) = VirtualTerminal.TitleTransition("Hello", null, false, true);
        escape.ShouldBe("");
        last.ShouldBeNull();
        pushed.ShouldBeFalse();
    }

    [Fact]
    public void Dispose_PopsOnlyWhenPushed()
    {
        VirtualTerminal.TitleRestoreEscape(true).ShouldBe(Pop);
        VirtualTerminal.TitleRestoreEscape(false).ShouldBe("");
    }

    [Fact]
    public void NullTitle_IsIgnoredAndEmptyTitleIsASetTitle()
    {
        var (nullEscape, nullLast, nullPushed) = VirtualTerminal.TitleTransition(null, "A", true, false);
        nullEscape.ShouldBe("");
        nullLast.ShouldBe("A");
        nullPushed.ShouldBeTrue();

        var (emptyEscape, emptyLast, _) = VirtualTerminal.TitleTransition("\n", "A", true, false);
        emptyEscape.ShouldBe("\e]2;\e\\");
        emptyLast.ShouldBe("");
    }

    [Fact]
    public void TerminalViewport_ForwardsTitleToItsParent()
    {
        var parent = new RecordingViewport();
        var child = new TerminalViewport(parent, 2, 3, 10, 5);

        child.Title = "Hello";

        parent.Titles.ShouldBe(["Hello"]);
    }

    private sealed class RecordingViewport : ITerminalViewport
    {
        public List<string?> Titles { get; } = [];
        public string? Title { set => Titles.Add(value); }
        public (int Column, int Row) Offset => (0, 0);
        public (int Width, int Height) Size => (80, 24);
        public void SetCursorPosition(int left, int top) { }
        public void Write(string text) { }
        public void WriteLine(string? text = null) { }
        public TermCell CellSize => default;
        public void Flush() { }
        public Stream OutputStream => Stream.Null;
    }
}
