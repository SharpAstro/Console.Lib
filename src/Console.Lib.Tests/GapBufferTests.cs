using Console.Lib;
using Shouldly;
using Xunit;

namespace Console.Lib.Tests;

public sealed class GapBufferTests
{
    [Fact]
    public void Empty_HasZeroLength()
    {
        var b = new GapBuffer();
        b.Length.ShouldBe(0);
        b.GetText().ShouldBe("");
    }

    [Fact]
    public void Insert_AppendsAtEnd()
    {
        var b = new GapBuffer();
        b.Insert(0, (byte)'a');
        b.Insert(1, (byte)'b');
        b.Insert(2, (byte)'c');
        b.Length.ShouldBe(3);
        b.GetText().ShouldBe("abc");
    }

    [Fact]
    public void InsertSpan_SeedsAndExtends()
    {
        var b = new GapBuffer("hello");
        b.GetText().ShouldBe("hello");
        b.Insert(5, " world"u8);
        b.GetText().ShouldBe("hello world");
    }

    [Fact]
    public void Insert_AtMiddle_MovesGap()
    {
        var b = new GapBuffer("ace");
        b.Insert(1, (byte)'b'); // → "abce"
        b.Insert(3, (byte)'d'); // → "abcde"
        b.GetText().ShouldBe("abcde");
    }

    [Fact]
    public void DeleteAt_RemovesByte()
    {
        var b = new GapBuffer("abcdef");
        b.DeleteAt(2).ShouldBeTrue(); // remove 'c' → "abdef"
        b.GetText().ShouldBe("abdef");
        b.Length.ShouldBe(5);
    }

    [Fact]
    public void DeleteRange_HandlesOverflow()
    {
        var b = new GapBuffer("hello");
        b.DeleteRange(2, 100);  // clamped to end
        b.GetText().ShouldBe("he");
    }

    [Fact]
    public void Indexer_ReadsAcrossGap()
    {
        var b = new GapBuffer("ace");
        b.Insert(1, (byte)'b');     // gap now sits after position 2 in physical layout
        b[0].ShouldBe((byte)'a');
        b[1].ShouldBe((byte)'b');
        b[2].ShouldBe((byte)'c');
        b[3].ShouldBe((byte)'e');
    }

    [Fact]
    public void CopyTo_SpansGap()
    {
        var b = new GapBuffer("abcdef");
        b.Insert(3, (byte)'X');     // → "abcXdef", gap moves to after X
        Span<byte> dest = stackalloc byte[7];
        var n = b.CopyTo(0, dest, 7);
        n.ShouldBe(7);
        System.Text.Encoding.UTF8.GetString(dest).ShouldBe("abcXdef");
    }

    [Fact]
    public void Spans_ExposeBothHalves()
    {
        var b = new GapBuffer("hello");
        b.Insert(2, (byte)'X');     // → "heXllo", gap right after X
        // The pre-gap span should contain "heX"; the post-gap span "llo".
        // (We don't depend on exact gap position, only on concatenation.)
        var combined = new System.Text.StringBuilder()
            .Append(System.Text.Encoding.UTF8.GetString(b.SpanBeforeGap))
            .Append(System.Text.Encoding.UTF8.GetString(b.SpanAfterGap))
            .ToString();
        combined.ShouldBe("heXllo");
    }

    [Fact]
    public void GrowBeyondInitialCapacity()
    {
        var b = new GapBuffer();
        // InitialCapacity is 256; force at least one grow.
        for (var i = 0; i < 1000; i++) b.Insert(b.Length, (byte)('a' + (i % 26)));
        b.Length.ShouldBe(1000);
        b.GetText().Length.ShouldBe(1000);
    }

    [Fact]
    public void OutOfRangeIndexer_Throws()
    {
        var b = new GapBuffer("ab");
        Should.Throw<ArgumentOutOfRangeException>(() => _ = b[2]);
        Should.Throw<ArgumentOutOfRangeException>(() => _ = b[-1]);
    }

    [Fact]
    public void Utf8Roundtrip_PreservesMultibyte()
    {
        var b = new GapBuffer("héllo🙂");   // 2-byte é, 4-byte 🙂
        b.GetText().ShouldBe("héllo🙂");
        b.Length.ShouldBe(System.Text.Encoding.UTF8.GetByteCount("héllo🙂"));
    }
}
