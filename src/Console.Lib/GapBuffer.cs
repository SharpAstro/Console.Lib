namespace Console.Lib;

/// <summary>
/// UTF-8 byte gap buffer for editor-style text storage. The backing array is
/// laid out as <c>[pre-gap bytes][gap][post-gap bytes]</c>; a *logical* position
/// <c>p</c> in <c>[0, Length]</c> refers to where the p-th byte would be if the
/// gap were closed. Physical position is <c>p</c> when <c>p &lt;= GapStart</c>,
/// otherwise <c>p + GapLength</c>.
/// <para>
/// The encoding is fixed to UTF-8 — that's the lingua franca for terminal I/O
/// and source code, and exposing the buffer as raw bytes lets pipe-based
/// consumers (lexers, encoders) walk it directly without re-encoding. Cursor
/// movement (<see cref="TextAreaState"/>) is codepoint-aware: it never lands
/// in the middle of a multi-byte sequence.
/// </para>
/// <para>
/// Insert and delete at the cursor are O(1) amortised; moving the gap pays an
/// O(d) memmove for distance d. The expected workload (a few keystrokes followed
/// by a cursor move) makes this the right shape for editor use — see Finseth,
/// "The Craft of Text Editing" (1991), or Emacs's <c>buffer.c</c>.
/// </para>
/// </summary>
public sealed class GapBuffer
{
    private const int InitialCapacity = 256;
    private const int MinGap = 32;

    private byte[] _buf;
    private int _gapStart;
    private int _gapEnd;        // exclusive; gap occupies [_gapStart, _gapEnd)

    /// <summary>Creates an empty buffer.</summary>
    public GapBuffer() : this(ReadOnlySpan<byte>.Empty) { }

    /// <summary>Creates a buffer pre-populated with the UTF-8 encoding of <paramref name="initial"/>.</summary>
    public GapBuffer(string initial)
        : this(System.Text.Encoding.UTF8.GetBytes(initial ?? "")) { }

    /// <summary>Creates a buffer pre-populated with the supplied UTF-8 bytes.</summary>
    public GapBuffer(ReadOnlySpan<byte> initialUtf8)
    {
        var cap = Math.Max(InitialCapacity, initialUtf8.Length + MinGap);
        _buf = new byte[cap];
        initialUtf8.CopyTo(_buf);
        _gapStart = initialUtf8.Length;
        _gapEnd = cap;
    }

    /// <summary>Logical length in bytes (gap excluded).</summary>
    public int Length => _buf.Length - (_gapEnd - _gapStart);

    /// <summary>Reads the byte at the given logical position.</summary>
    public byte this[int logical]
    {
        get
        {
            if ((uint)logical >= (uint)Length) throw new ArgumentOutOfRangeException(nameof(logical));
            return _buf[PhysicalIndex(logical)];
        }
    }

    /// <summary>Inserts a single byte at the given logical position.</summary>
    public void Insert(int logical, byte b)
    {
        if ((uint)logical > (uint)Length) throw new ArgumentOutOfRangeException(nameof(logical));
        MoveGapTo(logical);
        EnsureGap(1);
        _buf[_gapStart++] = b;
    }

    /// <summary>Inserts a span of bytes at the given logical position.</summary>
    public void Insert(int logical, ReadOnlySpan<byte> bytes)
    {
        if ((uint)logical > (uint)Length) throw new ArgumentOutOfRangeException(nameof(logical));
        if (bytes.IsEmpty) return;
        MoveGapTo(logical);
        EnsureGap(bytes.Length);
        bytes.CopyTo(_buf.AsSpan(_gapStart));
        _gapStart += bytes.Length;
    }

    /// <summary>Deletes the byte at <paramref name="logical"/>. Returns <c>true</c> if anything was deleted.</summary>
    public bool DeleteAt(int logical)
    {
        if ((uint)logical >= (uint)Length) return false;
        MoveGapTo(logical);
        _gapEnd++;              // expand gap forward, dropping the byte just past it
        return true;
    }

    /// <summary>Deletes <paramref name="count"/> bytes starting at <paramref name="logical"/> (clamped to the buffer end).</summary>
    public void DeleteRange(int logical, int count)
    {
        if (count <= 0) return;
        if ((uint)logical > (uint)Length) throw new ArgumentOutOfRangeException(nameof(logical));
        count = Math.Min(count, Length - logical);
        if (count <= 0) return;
        MoveGapTo(logical);
        _gapEnd += count;
    }

    /// <summary>Materialises the buffer to a UTF-8 decoded string. O(Length).</summary>
    public string GetText()
    {
        var len = Length;
        if (len == 0) return "";
        Span<byte> dest = len <= 1024 ? stackalloc byte[len] : new byte[len];
        CopyTo(0, dest, len);
        return System.Text.Encoding.UTF8.GetString(dest);
    }

    /// <summary>Pre-gap segment (bytes <c>[0, GapStart)</c>) for zero-alloc consumption.</summary>
    public ReadOnlySpan<byte> SpanBeforeGap => _buf.AsSpan(0, _gapStart);

    /// <summary>Post-gap segment (bytes <c>[GapEnd, end)</c>) for zero-alloc consumption.</summary>
    public ReadOnlySpan<byte> SpanAfterGap => _buf.AsSpan(_gapEnd);

    /// <summary>Pre-gap segment as <see cref="ReadOnlyMemory{T}"/> for async / Pipe consumers (which cannot hold a span).</summary>
    public ReadOnlyMemory<byte> MemoryBeforeGap => new(_buf, 0, _gapStart);

    /// <summary>Post-gap segment as <see cref="ReadOnlyMemory{T}"/>.</summary>
    public ReadOnlyMemory<byte> MemoryAfterGap => new(_buf, _gapEnd, _buf.Length - _gapEnd);

    /// <summary>Copies up to <paramref name="count"/> bytes starting at <paramref name="logical"/> into <paramref name="dest"/>. Returns the number of bytes actually copied.</summary>
    public int CopyTo(int logical, Span<byte> dest, int count)
    {
        if ((uint)logical > (uint)Length) throw new ArgumentOutOfRangeException(nameof(logical));
        count = Math.Min(count, Length - logical);
        count = Math.Min(count, dest.Length);
        if (count <= 0) return 0;

        // Split the read across the gap if necessary.
        if (logical + count <= _gapStart)
        {
            _buf.AsSpan(logical, count).CopyTo(dest);
        }
        else if (logical >= _gapStart)
        {
            _buf.AsSpan(logical + (_gapEnd - _gapStart), count).CopyTo(dest);
        }
        else
        {
            var before = _gapStart - logical;
            _buf.AsSpan(logical, before).CopyTo(dest);
            _buf.AsSpan(_gapEnd, count - before).CopyTo(dest[before..]);
        }
        return count;
    }

    private int PhysicalIndex(int logical)
        => logical < _gapStart ? logical : logical + (_gapEnd - _gapStart);

    private void MoveGapTo(int logical)
    {
        if (logical == _gapStart) return;
        if (logical < _gapStart)
        {
            // Shift [logical, _gapStart) right into [_gapEnd - delta, _gapEnd).
            var delta = _gapStart - logical;
            Array.Copy(_buf, logical, _buf, _gapEnd - delta, delta);
            _gapStart -= delta;
            _gapEnd -= delta;
        }
        else
        {
            // Shift [_gapEnd, _gapEnd + delta) left into [_gapStart, _gapStart + delta).
            var delta = logical - _gapStart;
            Array.Copy(_buf, _gapEnd, _buf, _gapStart, delta);
            _gapStart += delta;
            _gapEnd += delta;
        }
    }

    private void EnsureGap(int needed)
    {
        var gap = _gapEnd - _gapStart;
        if (gap >= needed) return;
        var content = Length;
        var newCap = Math.Max(_buf.Length * 2, content + needed + MinGap);
        var newBuf = new byte[newCap];
        Array.Copy(_buf, 0, newBuf, 0, _gapStart);
        var postLen = _buf.Length - _gapEnd;
        Array.Copy(_buf, _gapEnd, newBuf, newCap - postLen, postLen);
        _buf = newBuf;
        _gapEnd = newCap - postLen;
    }
}
