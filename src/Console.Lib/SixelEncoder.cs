using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
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
/// <item>One hash per pixel, not two: the counting pass records each pixel's colour as an entry number in
/// first-seen order, so when the palette is that order (the common case: no reservations and no more
/// colours than slots) the index map is written in the same pass, and runs of one colour skip the table.</item>
/// <item>Vectorised skips over empty space: runs of cleared pixels while the palette is counted,
/// transparent spans of a row while the grid is built, and zero spans of a colour's slice while it is
/// run-length encoded, are crossed at SIMD width once they are more than a pixel or a few bytes long. A
/// display-math raster is mostly all three. The grid pass also records each colour's first and last
/// column, so its margins are never scanned.</item>
/// <item>Each colour's slice is cleared after it is encoded, over the columns it touched, instead of the
/// whole palette × width grid being cleared every band.</item>
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
        var writer = new BufferedWriter(output, ArrayPool<byte>.Shared.Rent(65_536));
        try
        {
            EncodeCore(rawPixels, width, height, channels, reserved, ref writer);
            writer.Flush();
        }
        finally
        {
            writer.ReturnBuffer();
        }
    }

    /// <summary>
    /// <see cref="Encode"/>'s stream as a string, for callers that hand it on as text (display math, and
    /// <see cref="BoxRenderer.EncodeImage"/>). The bytes are built in one growing pooled buffer and widened
    /// once, where going through a <see cref="MemoryStream"/> cost a copy out of it and then the string.
    /// The stream is pure ASCII, so the string is the same bytes.
    /// </summary>
    internal static string EncodeToString(byte[] rawPixels, int width, int height, int channels,
        ReadOnlySpan<RGBAColor32> reserved = default)
    {
        var writer = new BufferedWriter(null, ArrayPool<byte>.Shared.Rent(65_536));
        try
        {
            EncodeCore(rawPixels, width, height, channels, reserved, ref writer);
            return Encoding.ASCII.GetString(writer.Written);
        }
        finally
        {
            writer.ReturnBuffer();
        }
    }

    private static void EncodeCore(byte[] rawPixels, int width, int height, int channels,
        ReadOnlySpan<RGBAColor32> reserved, ref BufferedWriter writer)
    {
        var pixelCount = width * height;
        var indexMap = ArrayPool<byte>.Shared.Rent(pixelCount);
        var sixelGrid = ArrayPool<byte>.Shared.Rent(MaxColors * width);
        var paletteArr = ArrayPool<int>.Shared.Rent(MaxColors);

        try
        {
            var paletteSize = BuildPaletteAndIndexMap(rawPixels, pixelCount, channels, indexMap, paletteArr, reserved);

            WriteHeader(ref writer, width, height);
            WritePalette(ref writer, paletteArr, paletteSize);
            WriteSixelData(ref writer, indexMap, sixelGrid, width, height, paletteSize);
            WriteTerminator(ref writer);
        }
        finally
        {
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
    /// <para>
    /// The counting pass numbers each colour in the order it is first seen, which is the order the
    /// palette takes when everything fits, so without reservations that pass writes the index map as it
    /// goes and the second pass is skipped. Otherwise the second pass maps entry numbers, not colours.
    /// The order, the ranking and so the stream are what the two-dictionary version produced: its
    /// dictionary enumerated in insertion order, which is this first-seen order.
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
        var colours = new ColourTable();

        // Pass 1: count each colour's pixels. Each pixel's entry number goes into indexMap while it still
        // fits a byte; that is the final map whenever the palette turns out to be first-seen order.
        if (channels == 4 && BitConverter.IsLittleEndian)
        {
            // R, G, B, A little-endian is A << 24 | B << 16 | G << 8 | R; shifting A out and swapping the
            // bytes gives R << 16 | G << 8 | B, the packing used everywhere else.
            var pixels = MemoryMarshal.Cast<byte, uint>(rawPixels.AsSpan(0, pixelCount * 4));
            var previous = -1;
            var entry = 0;
            for (var i = 0; i < pixels.Length; i++)
            {
                var pixel = pixels[i];
                if (pixel == 0 && i + 1 < pixels.Length && pixels[i + 1] == 0)
                {
                    // A run of all-zero pixels, which is how a cleared RGBA buffer leaves everything nothing
                    // was drawn on: find where it ends at SIMD width and mark it in one go. A lone one is
                    // left to the alpha check below, which costs less than the search.
                    var run = pixels[i..].IndexOfAnyExcept(0u);
                    if (run < 0) run = pixels.Length - i;
                    indexMap.AsSpan(i, run).Fill(TransparentIndex);
                    i += run - 1;
                    continue;
                }

                if (pixel >> 24 == 0)
                {
                    indexMap[i] = TransparentIndex;
                    continue;
                }

                var packed = (int)BinaryPrimitives.ReverseEndianness(pixel << 8);
                if (packed == previous)
                {
                    colours.Counts[entry]++;
                }
                else
                {
                    entry = colours.Add(packed);
                    previous = packed;
                }
                indexMap[i] = entry < MaxColors ? (byte)entry : TransparentIndex;
            }
        }
        else
        {
            var honourAlpha = channels >= 4;
            var previous = -1;
            var entry = 0;
            for (var i = 0; i < pixelCount; i++)
            {
                var offset = i * channels;
                if (honourAlpha && rawPixels[offset + 3] == 0)
                {
                    indexMap[i] = TransparentIndex;
                    continue;
                }

                var packed = (rawPixels[offset] << 16) | (rawPixels[offset + 1] << 8) | rawPixels[offset + 2];
                if (packed == previous)
                {
                    colours.Counts[entry]++;
                }
                else
                {
                    entry = colours.Add(packed);
                    previous = packed;
                }
                indexMap[i] = entry < MaxColors ? (byte)entry : TransparentIndex;
            }
        }

        var uniqueColors = colours.Count;
        if (reserved.IsEmpty && uniqueColors <= MaxColors)
        {
            // The palette is the colours in first-seen order, which is what pass 1 numbered them by, so
            // the index map is already final.
            colours.Keys.AsSpan(0, uniqueColors).CopyTo(palette);
            return uniqueColors;
        }

        // Palette index per entry number. A colour the frame shares with a reservation takes the
        // reservation's index, so colorToIndex need hold only the reservations and the colours given a slot.
        var entryToIndex = ArrayPool<byte>.Shared.Rent(uniqueColors);
        var colorToIndex = new Dictionary<int, byte>(capacity: reserved.Length + MaxColors);
        var paletteSize = 0;
        try
        {
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
                for (var e = 0; e < uniqueColors; e++)
                {
                    var packed = colours.Keys[e];
                    if (colorToIndex.TryAdd(packed, (byte)paletteSize))
                    {
                        entryToIndex[e] = (byte)paletteSize;
                        palette[paletteSize++] = packed;
                    }
                    else
                    {
                        entryToIndex[e] = colorToIndex[packed];
                    }
                }
            }
            else
            {
                // More unique colors than the slots reservations left: prioritize most frequent.
                //
                // Each pair is (entry number, count). The comparison reads only the count, so the sort
                // moves these pairs exactly as it moved the (colour, count) pairs it used to be given in
                // the same first-seen order, and the ranking, ties included, is the one it always was.
                var entries = ArrayPool<KeyValuePair<int, int>>.Shared.Rent(uniqueColors);
                try
                {
                    for (var e = 0; e < uniqueColors; e++)
                    {
                        entries[e] = new KeyValuePair<int, int>(e, colours.Counts[e]);
                    }

                    entries.AsSpan(0, uniqueColors).Sort(static (a, b) => b.Value.CompareTo(a.Value));

                    // Walk the ranking once. The cut is wherever the palette fills up, which depends on how
                    // many slots the reservations took AND on how many of the frame's own colours were
                    // already among them — so it is not a fixed index the way it was before.
                    var rank = 0;
                    for (; rank < uniqueColors && paletteSize < MaxColors; rank++)
                    {
                        var e = entries[rank].Key;
                        var packed = colours.Keys[e];
                        if (colorToIndex.TryAdd(packed, (byte)paletteSize))
                        {
                            entryToIndex[e] = (byte)paletteSize;
                            palette[paletteSize++] = packed;
                        }
                        else
                        {
                            entryToIndex[e] = colorToIndex[packed];
                        }
                    }

                    // Everything below the cut snaps to its nearest surviving entry — reservations included,
                    // which is the other half of the point: a shade can now land on a colour that means
                    // something rather than only on whichever near-duplicate of the background outranked it.
                    for (; rank < uniqueColors; rank++)
                    {
                        var e = entries[rank].Key;
                        var packed = colours.Keys[e];
                        entryToIndex[e] = colorToIndex.TryGetValue(packed, out var index)
                            ? index
                            : FindNearest(palette, paletteSize, packed);
                    }
                }
                finally
                {
                    ArrayPool<KeyValuePair<int, int>>.Shared.Return(entries);
                }
            }

            // Pass 2: map pixels to palette indices, through entry numbers
            if (uniqueColors <= MaxColors)
            {
                // Every entry number fitted a byte in pass 1, so the map holds them all.
                for (var i = 0; i < pixelCount; i++)
                {
                    var e = indexMap[i];
                    if (e != TransparentIndex)
                    {
                        indexMap[i] = entryToIndex[e];
                    }
                }
            }
            else
            {
                // Some did not, so look each pixel's colour up again; runs of one colour skip the table.
                var honourAlpha = channels >= 4;
                var previous = -1;
                byte index = 0;
                for (var i = 0; i < pixelCount; i++)
                {
                    var offset = i * channels;
                    if (honourAlpha && rawPixels[offset + 3] == 0)
                    {
                        continue;
                    }

                    var packed = (rawPixels[offset] << 16) | (rawPixels[offset + 1] << 8) | rawPixels[offset + 2];
                    if (packed != previous)
                    {
                        index = entryToIndex[colours.Find(packed)];
                        previous = packed;
                    }
                    indexMap[i] = index;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(entryToIndex);
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
        // Each colour's first and last set column in the current band; a last of -1 means the colour
        // is absent from it.
        Span<int> firstColumn = stackalloc int[MaxColors];
        Span<int> lastColumn = stackalloc int[MaxColors];
        firstColumn.Fill(int.MaxValue);
        lastColumn.Fill(-1);

        // Cleared once here, and after that each colour's slice is cleared as soon as it is encoded, over
        // the columns it touched, so every band starts on a clear grid without clearing palette × width.
        sixelGrid.AsSpan(0, paletteSize * width).Clear();

        for (var band = 0; band < height; band += 6)
        {
            var bandH = Math.Min(6, height - band);

            // Single pass over the band: build sixel bits AND detect color presence,
            // as the span of columns each colour occupies.
            // Transparent pixels (TransparentIndex sentinel) contribute no sixel
            // bit, so the corresponding cell remains "un-painted" and the
            // P2=1 header lets the terminal leave that cell at its current
            // contents instead of overwriting it with the background colour.
            // A transparent span longer than a pixel is crossed by a vectorised
            // search rather than pixel by pixel.
            for (var row = 0; row < bandH; row++)
            {
                var rowBit = (byte)(1 << row);
                var pixels = indexMap.AsSpan((band + row) * width, width);
                for (var col = 0; col < width; col++)
                {
                    var ci = pixels[col];
                    if (ci == TransparentIndex)
                    {
                        if (col + 1 < width && pixels[col + 1] == TransparentIndex)
                        {
                            var skip = pixels[(col + 1)..].IndexOfAnyExcept(TransparentIndex);
                            if (skip < 0)
                            {
                                break;
                            }
                            col += skip;
                        }
                        continue;
                    }

                    sixelGrid[ci * width + col] |= rowBit;
                    if (col < firstColumn[ci]) firstColumn[ci] = col;
                    if (col > lastColumn[ci]) lastColumn[ci] = col;
                }
            }

            // Encode each present color from its contiguous slice
            var firstColor = true;
            for (var ci = 0; ci < paletteSize; ci++)
            {
                var first = firstColumn[ci];
                var last = lastColumn[ci];
                if (last < 0)
                {
                    continue;
                }
                firstColumn[ci] = int.MaxValue;
                lastColumn[ci] = -1;

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
                // The grid pass above recorded first and last as it set the bits, so the empty margins
                // are not scanned at all and are re-emitted arithmetically below. Byte-for-byte
                // identical output: the loop would have produced exactly these two runs for the margins.
                var colorSlice = sixelGrid.AsSpan(ci * width, width);

                // Leading empty columns: one run of the zero sixel ('?' == 0x3F + 0).
                w.WriteRun(0x3F, first);

                // Maximal runs between first and last. A long empty run inside that span is crossed by
                // a vectorised search, and is most of the span for an antialiasing shade that touches a
                // few scattered columns. A run of one non-zero sixel is walked, being short in practice.
                var col = first;
                while (col <= last)
                {
                    var sixel = colorSlice[col];
                    var end = col + 1;
                    if (sixel == 0)
                    {
                        // Bounded, since the slice is non-zero at last. A short gap is walked; the
                        // search only pays for itself past a few bytes.
                        while (colorSlice[end] == 0)
                        {
                            if (end - col >= 8)
                            {
                                end += colorSlice[end..(last + 1)].IndexOfAnyExcept((byte)0);
                                break;
                            }
                            end++;
                        }
                    }
                    else
                    {
                        while (end <= last && colorSlice[end] == sixel)
                        {
                            end++;
                        }
                    }

                    w.WriteRun((byte)(sixel + 0x3F), end - col);
                    col = end;
                }

                // Trailing empty columns, likewise collapsed without scanning them.
                w.WriteRun(0x3F, width - 1 - last);

                // Leave the slice clear for the next band; nothing outside first..last was set.
                colorSlice[first..(last + 1)].Clear();
            }

            // LF — advance to next 6-pixel band (skip after the last band)
            if (band + 6 < height)
            {
                w.WriteByte((byte)'-');
            }
        }
    }

    private static void WriteTerminator(ref BufferedWriter w)
    {
        w.WriteByte(0x1B);          // ESC
        w.WriteByte((byte)'\\');    // ST
    }

    /// <summary>
    /// Counts colours by their packed RGB, numbering each in the order it is first added. Open
    /// addressing with linear probing over a power-of-two table; the entry number, not the slot, is
    /// what callers keep, so growing the table does not disturb them.
    /// </summary>
    private sealed class ColourTable
    {
        public int[] Keys = new int[256];
        public int[] Counts = new int[256];
        public int Count;

        // Entry number + 1 per slot, so a fresh table is all empty.
        private int[] _slots = new int[512];
        private int _shift = 32 - 9;

        /// <summary>Adds one pixel of <paramref name="packed"/> and returns its entry number.</summary>
        public int Add(int packed)
        {
            var mask = _slots.Length - 1;
            for (var slot = Slot(packed); ; slot = (slot + 1) & mask)
            {
                var stored = _slots[slot];
                if (stored == 0)
                {
                    return Insert(packed, slot);
                }

                if (Keys[stored - 1] == packed)
                {
                    Counts[stored - 1]++;
                    return stored - 1;
                }
            }
        }

        /// <summary>The entry number of a colour <see cref="Add"/> has seen.</summary>
        public int Find(int packed)
        {
            var mask = _slots.Length - 1;
            for (var slot = Slot(packed); ; slot = (slot + 1) & mask)
            {
                var stored = _slots[slot];
                if (Keys[stored - 1] == packed)
                {
                    return stored - 1;
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int Slot(int packed) => (int)((uint)packed * 0x9E3779B1u >> _shift);

        private int Insert(int packed, int slot)
        {
            var entry = Count++;
            if (entry == Keys.Length)
            {
                Array.Resize(ref Keys, entry * 2);
                Array.Resize(ref Counts, entry * 2);
            }

            Keys[entry] = packed;
            Counts[entry] = 1;
            _slots[slot] = entry + 1;

            // Keep the table at most half full, so probes stay short.
            if (Count * 2 > _slots.Length)
            {
                Rehash();
            }

            return entry;
        }

        private void Rehash()
        {
            _slots = new int[_slots.Length * 2];
            _shift--;
            var mask = _slots.Length - 1;
            for (var entry = 0; entry < Count; entry++)
            {
                var slot = Slot(Keys[entry]);
                while (_slots[slot] != 0)
                {
                    slot = (slot + 1) & mask;
                }
                _slots[slot] = entry + 1;
            }
        }
    }

    /// <summary>
    /// Minimal buffered writer that batches small writes into a pooled buffer before flushing it to the
    /// underlying <see cref="Stream"/>, or, without one, grows the buffer to hold the whole stream (see
    /// <see cref="EncodeToString"/>). Space is checked once per write rather than once per byte; when it
    /// runs out the buffer is flushed early, so a stream receives the same bytes in different chunks.
    /// </summary>
    private ref struct BufferedWriter(Stream? output, byte[] buffer)
    {
        private readonly Stream? _output = output;
        private byte[] _buffer = buffer;
        private int _pos;

        /// <summary>Everything written so far, when there is no stream to flush to.</summary>
        public readonly ReadOnlySpan<byte> Written => _buffer.AsSpan(0, _pos);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void Reserve(int count)
        {
            if (_pos + count > _buffer.Length)
            {
                MakeRoom(count);
            }
        }

        private void MakeRoom(int count)
        {
            if (_output is not null)
            {
                Flush();
                if (count <= _buffer.Length)
                {
                    return;
                }
            }

            var grown = ArrayPool<byte>.Shared.Rent(Math.Max(_buffer.Length * 2, _pos + count));
            _buffer.AsSpan(0, _pos).CopyTo(grown);
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = grown;
        }

        public void WriteByte(byte b)
        {
            Reserve(1);
            _buffer[_pos++] = b;
        }

        public void WriteAscii(ReadOnlySpan<byte> data)
        {
            Reserve(data.Length);
            data.CopyTo(_buffer.AsSpan(_pos));
            _pos += data.Length;
        }

        public void WriteInt(int value)
        {
            Reserve(11);
            _pos += FormatInt(value, _buffer.AsSpan(_pos));
        }

        /// <summary>
        /// One run of <paramref name="count"/> copies of a sixel: up to three written out, longer ones as
        /// <c>!count</c> and the sixel. Nothing for a count of zero or less.
        /// </summary>
        public void WriteRun(byte ch, int count)
        {
            if (count <= 0)
            {
                return;
            }

            Reserve(12);
            var buffer = _buffer;
            if (count <= 3)
            {
                buffer[_pos++] = ch;
                if (count > 1) buffer[_pos++] = ch;
                if (count > 2) buffer[_pos++] = ch;
            }
            else
            {
                buffer[_pos++] = (byte)'!';
                _pos += FormatInt(count, buffer.AsSpan(_pos));
                buffer[_pos++] = ch;
            }
        }

        /// <summary>ASCII decimal digits of <paramref name="value"/>, with a leading '-' when negative.</summary>
        private static int FormatInt(int value, Span<byte> destination)
        {
            if ((uint)value < 10)
            {
                destination[0] = (byte)('0' + value);
                return 1;
            }

            if ((uint)value < 100)
            {
                destination[1] = (byte)('0' + value % 10);
                destination[0] = (byte)('0' + value / 10);
                return 2;
            }

            if ((uint)value < 1000)
            {
                destination[2] = (byte)('0' + value % 10);
                destination[1] = (byte)('0' + value / 10 % 10);
                destination[0] = (byte)('0' + value / 100);
                return 3;
            }

            value.TryFormat(destination, out var written, provider: System.Globalization.CultureInfo.InvariantCulture);
            return written;
        }

        public void Flush()
        {
            if (_output is not null && _pos > 0)
            {
                _output.Write(_buffer.AsSpan(0, _pos));
                _pos = 0;
            }
        }

        public readonly void ReturnBuffer() => ArrayPool<byte>.Shared.Return(_buffer);
    }
}
