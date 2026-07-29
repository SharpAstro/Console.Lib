using System.Buffers;
using System.Runtime.InteropServices;
using DIR.Lib;

namespace Console.Lib;

/// <summary>
/// Encodes a <see cref="MagickImage"/> (Q8) to Sixel terminal graphics format,
/// replacing the built-in <see cref="MagickFormat.Sixel"/> writer with a custom
/// implementation that supports partial-image encoding without clone/crop.
///
/// The key optimizations that drove this:
/// <list type="number">
/// <item>Precomputed sixel grid — instead of scanning each pixel against each color per band (<c>O(colors × rows × width)</c>),
/// one row-major pass builds sixel bits for all colors simultaneously (<c>O(rows × width)</c>), then each color encodes from a contiguous memory slice.
/// </item>
/// <item>
/// <c>ArrayPool&lt;byte&gt;</c> — the indexMap, sixelGrid, palette, and output buffer are all rented from the shared pool, reducing managed allocations by ~23% and eliminating Gen2 GC pressure from repeated <c>new byte[]</c> calls.
/// </item>
/// <item>Single-pass palette +index mapping — combined the two - pass build(count frequencies → sort → map) into one pass using CollectionsMarshal.GetValueRefOrAddDefault for faster dictionary operations.</item>
/// <item>Cache-friendly access patterns — the sixel grid build reads indexMap row - major(sequential), and the RLE encoder reads each color's grid slice contiguously.</item>
/// </list>
/// <code>
/// Method              Mean        Ratio vs Magick
/// MagickSixel_Full    127.3 ms    1.00
/// CustomSixel_Full    9.1 ms      0.07 (14× faster)
/// MagickSixel_Partial 127.9 ms    1.00
/// CustomSixel_Partial 1.6 ms      0.01 (79× faster)
/// </code>
/// </summary>
public static class SixelEncoder
{
    /// <summary>
    /// Maximum number of distinct colours in a single sixel palette.
    /// 255 (not 256) so we can reserve <see cref="TransparentIndex"/> as
    /// the "do not paint this pixel" sentinel when encoding RGBA buffers.
    /// </summary>
    private const int MaxColors = 255;

    /// <summary>
    /// indexMap sentinel for "this pixel is transparent — skip it entirely
    /// when building sixel rows". Picked so it doesn't overlap any valid
    /// palette index (0..MaxColors-1 = 0..254).
    /// </summary>
    private const byte TransparentIndex = 0xFF;

    /// <summary>
    /// Writes a vertical slice of the image as a Sixel stream,
    /// avoiding the need to clone and crop for partial renders.
    /// </summary>
    /// <remarks>
    /// Only vertical clipping is supported — the full image width is always emitted.
    /// Sixel is a band-based format: each 6-pixel-tall band is encoded as a sequence
    /// of color runs spanning the full width, terminated by <c>$</c> (carriage return,
    /// overlays the next color in the same band) and <c>-</c> (line feed, advances to
    /// the next band). There is no horizontal positioning within a band — characters
    /// are emitted left to right with no way to skip columns. Horizontal clipping
    /// would require emitting a separate DCS sequence per band with cursor
    /// repositioning between them, adding significant complexity and overhead.
    ///
    /// <para>
    /// References:
    /// <list type="bullet">
    /// <item><see href="https://vt100.net/docs/vt3xx-gp/chapter14.html">
    /// DEC VT330/VT340 Programmer Reference Manual, Chapter 14 — Sixel Graphics</see></item>
    /// <item><see href="https://saitoha.github.io/libsixel/">
    /// libsixel — Sixel format description and reference implementation</see></item>
    /// </list>
    /// </para>
    /// </remarks>
    /// <param name="reserved">
    /// Colours guaranteed a palette slot, taking indices 0..n-1 in the order given, before any
    /// frequency ranking. Empty by default, which is the historical behaviour exactly.
    /// <para>
    /// <b>Why this is needed at all.</b> The palette is otherwise chosen purely by pixel FREQUENCY, and
    /// on any surface with antialiased text the frequency histogram is dominated by glyph edges: a real
    /// chess board render produces ~1966 distinct colours for 255 slots, of which three (background and
    /// the two square fills) cover ~95% of the pixels and the remaining ~250 slots are contested by ~1960
    /// near-identical shades, the 255th of which occupies twelve pixels. A colour that carries MEANING
    /// rather than area — a selection tint, a 3px last-move border, a hairline move arrow — is ranked
    /// against that tail on area alone, and if it loses it is snapped by <see cref="FindNearest"/> to the
    /// nearest survivor. Since the survivors are overwhelmingly near-duplicates of the background and the
    /// board, losing does not shift the colour slightly: it makes it board-coloured, i.e. invisible.
    /// </para>
    /// <para>
    /// Reserving also makes an index STABLE, which matters wherever more than one encode has to agree:
    /// a partial strip re-derives its palette from that strip's histogram alone, so the same accent could
    /// be represented differently by a partial update than by the full frame that preceded it; and an
    /// animated stream would otherwise reshuffle indices every frame as the histogram shifts, which both
    /// shimmers and forecloses any inter-frame delta encoding.
    /// </para>
    /// <para>
    /// Reserved colours hold their slots whether or not the frame uses them — that is the point — and an
    /// unused one costs a single palette declaration and nothing in the data section. Duplicates are
    /// ignored. A list of <see cref="MaxColors"/> entries leaves no room for the frequency pass, which
    /// makes it a FIXED palette: everything in the image then snaps to the colours given. That limit case
    /// is deliberate, so animation needs no separate mode.
    /// </para>
    /// </param>
    public static void Encode(byte[] rawPixels, int width, int height, int channels, Stream output,
        ReadOnlySpan<RGBAColor32> reserved = default)
    {
        var pixelCount = width * height;
        var indexMap = ArrayPool<byte>.Shared.Rent(pixelCount);
        var sixelGrid = ArrayPool<byte>.Shared.Rent(MaxColors * width);
        var paletteArr = ArrayPool<int>.Shared.Rent(MaxColors);
        var outputBuf = ArrayPool<byte>.Shared.Rent(65_536);

        try
        {
            var paletteSize = BuildPaletteAndIndexMap(rawPixels, pixelCount, channels, indexMap, paletteArr, reserved);

            var writer = new BufferedWriter(output, outputBuf);
            WriteHeader(ref writer, width, height);
            WritePalette(ref writer, paletteArr, paletteSize);
            WriteSixelData(ref writer, indexMap, sixelGrid, width, height, paletteSize);
            WriteTerminator(ref writer);
            writer.Flush();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(outputBuf);
            ArrayPool<int>.Shared.Return(paletteArr);
            ArrayPool<byte>.Shared.Return(sixelGrid);
            ArrayPool<byte>.Shared.Return(indexMap);
        }
    }

    /// <summary>
    /// Two-pass palette construction: first counts color frequencies, then assigns
    /// palette slots to the most frequent colors so large solid areas (board tiles,
    /// backgrounds) always get exact representation. Remaining colors are mapped
    /// to their nearest palette entry.
    /// <para>
    /// <paramref name="reserved"/> is claimed ahead of all of that, so a colour that matters by MEANING
    /// rather than by area is not ranked against the antialiasing tail. See <see cref="Encode"/>.
    /// </para>
    /// </summary>
    private static int BuildPaletteAndIndexMap(
        byte[] rawPixels, int pixelCount, int channels,
        byte[] indexMap, int[] palette, ReadOnlySpan<RGBAColor32> reserved)
    {
        // RGBA inputs (channels == 4) honour alpha: any pixel with alpha == 0
        // is skipped during palette construction and tagged as transparent in
        // indexMap, so it never contributes a sixel bit. Combined with the
        // P2=1 in WriteHeader (= "leave un-painted positions alone"), this
        // preserves the terminal's current background underneath those
        // pixels. RGB inputs (channels == 3) ignore alpha and treat every
        // pixel as opaque, matching the original behaviour.
        var honourAlpha = channels >= 4;

        // Pass 1: count frequency of each unique color
        var colorFrequency = new Dictionary<int, int>(capacity: 256);

        for (var i = 0; i < pixelCount; i++)
        {
            var offset = i * channels;
            if (honourAlpha && rawPixels[offset + 3] == 0)
            {
                // Defer the indexMap write to the second pass; we don't
                // populate the palette for transparent pixels.
                continue;
            }
            var packed = (rawPixels[offset] << 16) | (rawPixels[offset + 1] << 8) | rawPixels[offset + 2];

            ref var count = ref CollectionsMarshal.GetValueRefOrAddDefault(colorFrequency, packed, out _);
            count++;
        }

        var uniqueColors = colorFrequency.Count;
        var colorToIndex = new Dictionary<int, byte>(capacity: uniqueColors + reserved.Length);
        var paletteSize = 0;

        // Reservations claim their slots first, in the order given, and keep them whether or not this
        // frame contains a single pixel of them — that is what makes the index stable across a partial
        // strip or an animation frame. Duplicates are dropped rather than wasting a slot.
        foreach (var colour in reserved)
        {
            if (paletteSize >= MaxColors)
            {
                break;
            }

            var packed = (colour.Red << 16) | (colour.Green << 8) | colour.Blue;
            if (colorToIndex.TryAdd(packed, (byte)paletteSize))
            {
                palette[paletteSize++] = packed;
            }
        }

        if (paletteSize + uniqueColors <= MaxColors)
        {
            // Everything fits alongside the reservations — no ranking needed
            foreach (var (packed, _) in colorFrequency)
            {
                if (colorToIndex.TryAdd(packed, (byte)paletteSize))
                {
                    palette[paletteSize++] = packed;
                }
            }
        }
        else
        {
            // More unique colors than the slots reservations left: prioritize most frequent
            var entries = ArrayPool<KeyValuePair<int, int>>.Shared.Rent(uniqueColors);
            try
            {
                var idx = 0;
                foreach (var kv in colorFrequency)
                {
                    entries[idx++] = kv;
                }

                entries.AsSpan(0, uniqueColors).Sort(static (a, b) => b.Value.CompareTo(a.Value));

                // Walk the ranking once. The cut is wherever the palette fills up, which depends on how
                // many slots the reservations took AND on how many of the frame's own colours were
                // already among them — so it is not a fixed index the way it was before.
                var rank = 0;
                for (; rank < uniqueColors && paletteSize < MaxColors; rank++)
                {
                    var packed = entries[rank].Key;
                    if (colorToIndex.TryAdd(packed, (byte)paletteSize))
                    {
                        palette[paletteSize++] = packed;
                    }
                }

                // Everything below the cut snaps to its nearest surviving entry — reservations included,
                // which is the other half of the point: a shade can now land on a colour that means
                // something rather than only on whichever near-duplicate of the background outranked it.
                for (; rank < uniqueColors; rank++)
                {
                    var packed = entries[rank].Key;
                    if (!colorToIndex.ContainsKey(packed))
                    {
                        colorToIndex[packed] = FindNearest(palette, paletteSize, packed);
                    }
                }
            }
            finally
            {
                ArrayPool<KeyValuePair<int, int>>.Shared.Return(entries);
            }
        }

        // Pass 2: map pixels to palette indices (all lookups are pre-computed)
        for (var i = 0; i < pixelCount; i++)
        {
            var offset = i * channels;
            if (honourAlpha && rawPixels[offset + 3] == 0)
            {
                indexMap[i] = TransparentIndex;
                continue;
            }
            var packed = (rawPixels[offset] << 16) | (rawPixels[offset + 1] << 8) | rawPixels[offset + 2];
            indexMap[i] = colorToIndex[packed];
        }

        return paletteSize;
    }

    private static byte FindNearest(int[] palette, int paletteSize, int packed)
    {
        var r = (packed >> 16) & 0xFF;
        var g = (packed >> 8) & 0xFF;
        var b = packed & 0xFF;

        var bestIdx = 0;
        var bestDist = int.MaxValue;

        for (var i = 0; i < paletteSize; i++)
        {
            var pr = (palette[i] >> 16) & 0xFF;
            var pg = (palette[i] >> 8) & 0xFF;
            var pb = palette[i] & 0xFF;

            var dist = (r - pr) * (r - pr) + (g - pg) * (g - pg) + (b - pb) * (b - pb);
            if (dist < bestDist)
            {
                bestDist = dist;
                bestIdx = i;
            }
        }

        return (byte)bestIdx;
    }

    // DCS 0 ; 1 q  — macro param 0, background mode "no change"
    private static void WriteHeader(ref BufferedWriter w, int width, int height)
    {
        w.WriteByte(0x1B);              // ESC
        w.WriteByte((byte)'P');         // DCS introducer
        w.WriteAscii("0;1q"u8);
        w.WriteByte((byte)'"');         // raster attributes
        w.WriteInt(1);                  // Pan (aspect numerator)
        w.WriteByte((byte)';');
        w.WriteInt(1);                  // Pad (aspect denominator)
        w.WriteByte((byte)';');
        w.WriteInt(width);              // Ph (pixel width)
        w.WriteByte((byte)';');
        w.WriteInt(height);             // Pv (pixel height)
    }

    private static void WritePalette(ref BufferedWriter w, int[] palette, int paletteSize)
    {
        for (var i = 0; i < paletteSize; i++)
        {
            var packed = palette[i];
            w.WriteByte((byte)'#');
            w.WriteInt(i);
            w.WriteAscii(";2;"u8);
            w.WriteInt(((packed >> 16) & 0xFF) * 100 / 255);   // R %
            w.WriteByte((byte)';');
            w.WriteInt(((packed >> 8) & 0xFF) * 100 / 255);    // G %
            w.WriteByte((byte)';');
            w.WriteInt((packed & 0xFF) * 100 / 255);            // B %
        }
    }

    /// <summary>
    /// Precomputes sixel bits for all colors in each band in a single row-major pass,
    /// then RLE-encodes each present color from the contiguous sixelGrid slice.
    /// </summary>
    private static void WriteSixelData(
        ref BufferedWriter w, byte[] indexMap, byte[] sixelGrid,
        int width, int height, int paletteSize)
    {
        Span<bool> colorPresent = stackalloc bool[MaxColors];

        for (var band = 0; band < height; band += 6)
        {
            var bandH = Math.Min(6, height - band);

            // Clear only the portion we use
            sixelGrid.AsSpan(0, paletteSize * width).Clear();
            colorPresent[..paletteSize].Clear();

            // Single pass over the band: build sixel bits AND detect color presence.
            // Transparent pixels (TransparentIndex sentinel) contribute no sixel
            // bit, so the corresponding cell remains "un-painted" and the
            // P2=1 header lets the terminal leave that cell at its current
            // contents instead of overwriting it with the background colour.
            for (var row = 0; row < bandH; row++)
            {
                var rowBit = (byte)(1 << row);
                var rowStart = (band + row) * width;
                for (var col = 0; col < width; col++)
                {
                    var ci = indexMap[rowStart + col];
                    if (ci == TransparentIndex) continue;
                    sixelGrid[ci * width + col] |= rowBit;
                    colorPresent[ci] = true;
                }
            }

            // Encode each present color from its contiguous slice
            var firstColor = true;
            for (var ci = 0; ci < paletteSize; ci++)
            {
                if (!colorPresent[ci])
                {
                    continue;
                }

                if (!firstColor)
                {
                    w.WriteByte((byte)'$');  // CR — overlay next color in same band
                }
                firstColor = false;

                // Select color register
                w.WriteByte((byte)'#');
                w.WriteInt(ci);

                // RLE-encode from the contiguous sixel grid slice.
                //
                // Only the span between this colour's first and last set column carries information;
                // outside it the slice is all-zero and RLE-collapses to a single empty run. Walking
                // the full width byte-by-byte to rediscover that is what made encode time scale with
                // palette size rather than with picture content -- a glyph colour touching 12 columns
                // still cost a full-width pass, and a 254-colour surface paid that 254 times per band.
                // IndexOfAnyExcept/LastIndexOfAnyExcept are vectorised, so the empty margins are
                // skipped at SIMD width and re-emitted arithmetically below. Byte-for-byte identical
                // output: the loop would have produced exactly these two runs for the margins.
                var colorSlice = sixelGrid.AsSpan(ci * width, width);
                var first = colorSlice.IndexOfAnyExcept((byte)0);
                var last = colorSlice.LastIndexOfAnyExcept((byte)0);

                // A colour is only marked present when a bit was set, so an all-zero slice is
                // unreachable; guard anyway rather than emit a negative-length run.
                if (first < 0)
                {
                    continue;
                }

                // Leading empty columns: one run of the zero sixel ('?' == 0x3F + 0).
                FlushRun(ref w, 0x3F, first);

                byte prevChar = 0;
                var runLen = 0;

                for (var col = first; col <= last; col++)
                {
                    var ch = (byte)(colorSlice[col] + 0x3F);

                    if (ch == prevChar && runLen > 0)
                    {
                        runLen++;
                    }
                    else
                    {
                        FlushRun(ref w, prevChar, runLen);
                        prevChar = ch;
                        runLen = 1;
                    }
                }

                FlushRun(ref w, prevChar, runLen);

                // Trailing empty columns, likewise collapsed without scanning them.
                FlushRun(ref w, 0x3F, width - 1 - last);
            }

            // LF — advance to next 6-pixel band (skip after the last band)
            if (band + 6 < height)
            {
                w.WriteByte((byte)'-');
            }
        }
    }

    private static void FlushRun(ref BufferedWriter w, byte ch, int count)
    {
        if (count <= 0)
        {
            return;
        }

        if (count <= 3)
        {
            for (var i = 0; i < count; i++)
            {
                w.WriteByte(ch);
            }
        }
        else
        {
            w.WriteByte((byte)'!');
            w.WriteInt(count);
            w.WriteByte(ch);
        }
    }

    private static void WriteTerminator(ref BufferedWriter w)
    {
        w.WriteByte(0x1B);          // ESC
        w.WriteByte((byte)'\\');    // ST
    }

    /// <summary>
    /// Minimal buffered writer that batches small writes into a pooled buffer
    /// before flushing to the underlying <see cref="Stream"/>.
    /// </summary>
    private ref struct BufferedWriter(Stream output, byte[] buffer)
    {
        private readonly Stream _output = output;
        private readonly byte[] _buffer = buffer;
        private int _pos;

        public void WriteByte(byte b)
        {
            if (_pos >= _buffer.Length)
            {
                Flush();
            }
            _buffer[_pos++] = b;
        }

        public void WriteAscii(ReadOnlySpan<byte> data)
        {
            if (_pos + data.Length > _buffer.Length)
            {
                Flush();
            }

            if (data.Length > _buffer.Length)
            {
                _output.Write(data);
                return;
            }

            data.CopyTo(_buffer.AsSpan(_pos));
            _pos += data.Length;
        }

        public void WriteInt(int value)
        {
            Span<byte> digits = stackalloc byte[11];
            var len = 0;

            if (value == 0)
            {
                WriteByte((byte)'0');
                return;
            }

            if (value < 0)
            {
                WriteByte((byte)'-');
                value = -value;
            }

            while (value > 0)
            {
                digits[len++] = (byte)('0' + value % 10);
                value /= 10;
            }

            // Reverse the digits into the buffer
            for (var i = len - 1; i >= 0; i--)
            {
                WriteByte(digits[i]);
            }
        }

        public void Flush()
        {
            if (_pos > 0)
            {
                _output.Write(_buffer.AsSpan(0, _pos));
                _pos = 0;
            }
        }
    }
}
