using System.Text;
using Console.Lib;
using Shouldly;
using Xunit;

namespace Console.Lib.Tests;

public sealed class TextAreaStateTests
{
    [Fact]
    public void Empty_HasOneLine()
    {
        var s = new TextAreaState();
        s.LineCount.ShouldBe(1);
        s.CursorLineColumn.ShouldBe((0, 0));
        s.GetLine(0).ShouldBe("");
    }

    [Fact]
    public void InitialText_IndexesLines()
    {
        var s = new TextAreaState("a\nbb\nccc");
        s.LineCount.ShouldBe(3);
        s.GetLine(0).ShouldBe("a");
        s.GetLine(1).ShouldBe("bb");
        s.GetLine(2).ShouldBe("ccc");
    }

    [Fact]
    public void TrailingNewline_AddsEmptyFinalLine()
    {
        var s = new TextAreaState("a\n");
        s.LineCount.ShouldBe(2);
        s.GetLine(0).ShouldBe("a");
        s.GetLine(1).ShouldBe("");
    }

    [Fact]
    public void InsertChar_AdvancesCursor()
    {
        var s = new TextAreaState();
        s.InsertChar('a').ShouldBeTrue();
        s.InsertChar('b').ShouldBeTrue();
        s.GetText().ShouldBe("ab");
        s.CursorLineColumn.ShouldBe((0, 2));
    }

    [Fact]
    public void Enter_SplitsLine()
    {
        var s = new TextAreaState("ab");
        s.MoveRight();                            // cursor at byte 1
        s.InsertChar('\n').ShouldBeTrue();        // → "a\nb"
        s.GetText().ShouldBe("a\nb");
        s.LineCount.ShouldBe(2);
        s.CursorLineColumn.ShouldBe((1, 0));
    }

    [Fact]
    public void Backspace_DeletesCodepointBeforeCursor()
    {
        var s = new TextAreaState("héllo");        // é is 2 bytes
        s.MoveDocumentEnd();
        s.Backspace().ShouldBeTrue();
        s.Backspace().ShouldBeTrue();
        s.Backspace().ShouldBeTrue();
        s.Backspace().ShouldBeTrue();              // removes the é as a single codepoint
        s.GetText().ShouldBe("h");
    }

    [Fact]
    public void DeleteForward_RemovesCodepoint()
    {
        var s = new TextAreaState("héllo");
        s.DeleteForward().ShouldBeTrue();          // removes 'h'
        s.GetText().ShouldBe("éllo");
        s.DeleteForward().ShouldBeTrue();          // removes the multi-byte 'é' as one codepoint
        s.GetText().ShouldBe("llo");
    }

    [Fact]
    public void MoveLeftRight_StepsOverCodepoints()
    {
        var s = new TextAreaState("é");            // 2 bytes
        s.CursorPos.ShouldBe(0);
        s.MoveRight().ShouldBeTrue();
        s.CursorPos.ShouldBe(2);                   // landed past both bytes of é
        s.MoveRight().ShouldBeFalse();             // already at end
        s.MoveLeft().ShouldBeTrue();
        s.CursorPos.ShouldBe(0);
    }

    [Fact]
    public void MoveByLines_PreservesStickyColumn()
    {
        var s = new TextAreaState("hello\na\nworld");
        // Move to (line 0, col 4); MoveDown should skip into the short line at col 1
        // (clamped) but a subsequent MoveDown should restore col 4.
        s.MoveLineEnd();
        s.MoveLeft();                              // (0, 4)
        s.CursorLineColumn.ShouldBe((0, 4));

        s.MoveDown().ShouldBeTrue();
        s.CursorLineColumn.ShouldBe((1, 1));        // clamped to short line "a"

        s.MoveDown().ShouldBeTrue();
        s.CursorLineColumn.ShouldBe((2, 4));        // restored to sticky column
    }

    [Fact]
    public void HomeEnd_HitLineBounds()
    {
        var s = new TextAreaState("abc\ndef");
        s.MoveDown();                               // (1, 0)
        s.MoveLineEnd();
        s.CursorLineColumn.ShouldBe((1, 3));
        s.MoveLineStart();
        s.CursorLineColumn.ShouldBe((1, 0));
    }

    [Fact]
    public void DocumentStartEnd_HitBufferBounds()
    {
        var s = new TextAreaState("abc\ndef");
        s.MoveDocumentEnd();
        s.CursorPos.ShouldBe(7);
        s.MoveDocumentStart();
        s.CursorPos.ShouldBe(0);
    }

    [Fact]
    public void GetLineLength_DoesNotCountNewline()
    {
        var s = new TextAreaState("abc\ndef\n");
        s.GetLineLength(0).ShouldBe(3);
        s.GetLineLength(1).ShouldBe(3);
        s.GetLineLength(2).ShouldBe(0);   // empty trailing line
    }

    [Fact]
    public void InsertText_HandlesEmbeddedNewlines()
    {
        var s = new TextAreaState();
        s.InsertText("ab\ncd\nef").ShouldBeTrue();
        s.LineCount.ShouldBe(3);
        s.GetText().ShouldBe("ab\ncd\nef");
        s.CursorLineColumn.ShouldBe((2, 2));
    }

    [Fact]
    public void InsertRune_BmpCodepoint()
    {
        var s = new TextAreaState();
        s.InsertRune(new Rune('a')).ShouldBeTrue();
        s.InsertRune(new Rune('é')).ShouldBeTrue();   // 2-byte UTF-8
        s.GetText().ShouldBe("aé");
        // Cursor advanced by total UTF-8 bytes: 1 + 2 = 3
        s.CursorPos.ShouldBe(3);
    }

    [Fact]
    public void InsertRune_NonBmpCodepoint()
    {
        // Surrogate-pair codepoint (4-byte UTF-8) — the InsertChar path can't handle these;
        // InsertRune is the API for them.
        var s = new TextAreaState();
        s.InsertRune(Rune.GetRuneAt("🙂", 0)).ShouldBeTrue();
        s.GetText().ShouldBe("🙂");
        s.CursorPos.ShouldBe(4);
    }

    [Fact]
    public void MoveWordRight_SkipsSpacesThenWord()
    {
        var s = new TextAreaState("foo  bar baz");
        // Cursor starts at byte 0 (start of "foo"). One MoveWordRight skips
        // through "foo" — emacs M-f / VS Code Ctrl+Right semantics.
        s.MoveWordRight().ShouldBeTrue();
        s.CursorPos.ShouldBe(3);                  // end of "foo"
        s.MoveWordRight().ShouldBeTrue();
        s.CursorPos.ShouldBe(8);                  // end of "bar" (skipped two spaces first)
        s.MoveWordRight().ShouldBeTrue();
        s.CursorPos.ShouldBe(12);                 // end of "baz"
        s.MoveWordRight().ShouldBeFalse();        // already at EOF
    }

    [Fact]
    public void MoveWordLeft_SkipsSpacesThenWord()
    {
        var s = new TextAreaState("foo  bar baz");
        s.MoveDocumentEnd();
        s.MoveWordLeft().ShouldBeTrue();
        s.CursorPos.ShouldBe(9);                  // start of "baz"
        s.MoveWordLeft().ShouldBeTrue();
        s.CursorPos.ShouldBe(5);                  // start of "bar"
        s.MoveWordLeft().ShouldBeTrue();
        s.CursorPos.ShouldBe(0);                  // start of "foo"
        s.MoveWordLeft().ShouldBeFalse();         // already at start of buffer
    }

    [Fact]
    public void MoveWordLeftRight_KeepsNonAsciiInsideWord()
    {
        // "café bär" — non-ASCII bytes are word bytes, so a single
        // MoveWordRight skips through "café" without splitting on the é.
        var s = new TextAreaState("café bär");
        s.MoveWordRight().ShouldBeTrue();
        s.CursorPos.ShouldBe(5);                  // c=1 + a=1 + f=1 + é=2 = 5 bytes
        s.MoveWordRight().ShouldBeTrue();
        s.CursorPos.ShouldBe(10);                 // + space=1 + b=1 + ä=2 + r=1 = 10
    }

    [Fact]
    public void MoveTo_ClampsToValidLineAndColumn()
    {
        var s = new TextAreaState("abc\nde\nfghi");
        s.MoveTo(1, 1).ShouldBeTrue();
        s.CursorLineColumn.ShouldBe((1, 1));
        // Past line end — clamped to line end (line 1 = "de", length 2)
        s.MoveTo(1, 99).ShouldBeTrue();
        s.CursorLineColumn.ShouldBe((1, 2));
        // Past last line — clamped to last valid line
        s.MoveTo(99, 0).ShouldBeTrue();
        s.CursorLineColumn.ShouldBe((2, 0));
        // No-op when target equals current
        s.MoveTo(2, 0).ShouldBeFalse();
    }

    [Fact]
    public void MoveTo_SnapsBackOffMultiByteCodepoint()
    {
        // "café" — 'é' starts at byte 3 and spans bytes 3-4. A click that
        // lands on the second byte of 'é' (col 4) should snap back to col 3.
        var s = new TextAreaState("café");
        s.MoveTo(0, 4).ShouldBeTrue();
        s.CursorPos.ShouldBe(3);                  // snapped to start of 'é'
    }
}
