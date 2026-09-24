using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Console.Lib;

/// <summary>Horizontal alignment of a table cell within its column.</summary>
public enum CellAlignment
{
    Left,
    Center,
    Right,
}

/// <summary>
/// Renders a bordered table to VT lines.
/// <para>
/// Content-agnostic: cells arrive as <b>already-formatted strings</b> that may carry SGR escapes, and
/// column widths are measured with <paramref name="visibleLength"/> so styling never inflates a column.
/// That is what lets one renderer serve Markdown tables (whose cells are formatted inline runs) and a
/// plain string table alike.
/// </para>
/// <para>
/// This was private inside the Markdown renderer, which is why nothing else could draw a table. The
/// junction logic is the part worth having once: the top edge, the header separator and the bottom edge
/// each need a different tee where a column divider meets them, and getting one of the four wrong is
/// invisible until a table happens to be rendered with that style.
/// </para>
/// </summary>
public static class TextTable
{
    /// <summary>
    /// Appends the table's lines to <paramref name="output"/>: top border, header, separator, one line
    /// per row, bottom border. Every column is as wide as its widest cell, however wide that makes the
    /// table; pass a <c>maxWidth</c> to the other overload to keep it on screen.
    /// </summary>
    /// <param name="headers">Header cells, pre-formatted. Column count comes from this list.</param>
    /// <param name="rows">Body rows. A row shorter than the header is padded with empty cells.</param>
    /// <param name="alignments">Per-column alignment; columns beyond the end default to Left.</param>
    /// <param name="output">Receives the rendered lines.</param>
    /// <param name="style">Border character family.</param>
    /// <param name="borderColor">SGR prefix applied to border glyphs (e.g. a dim colour). May be empty.</param>
    /// <param name="reset">SGR reset emitted after each styled run. May be empty.</param>
    /// <param name="visibleLength">
    /// Measures a cell's on-screen width, ignoring escapes. Defaults to the ANSI-aware
    /// <see cref="MarkdownRenderer.VisibleLength"/>; pass your own to add east-asian-width handling.
    /// </param>
    public static void Render(
        IReadOnlyList<string> headers,
        IReadOnlyList<IReadOnlyList<string>> rows,
        IReadOnlyList<CellAlignment> alignments,
        List<string> output,
        BorderStyle style = BorderStyle.Light,
        string borderColor = "",
        string reset = "",
        Func<string, int>? visibleLength = null)
        => Render(headers, rows, alignments, output, int.MaxValue, style, borderColor, reset, visibleLength);

    /// <summary>
    /// Appends the table's lines to <paramref name="output"/>, no line wider than
    /// <paramref name="maxWidth"/>. A table that fits renders exactly as the unbounded overload does.
    /// <para>
    /// One that does not narrows its widest columns first, so a narrow column (a name, a flag) keeps its
    /// natural width while a prose column wraps. Cells wrap between words; a word is broken only when
    /// even the longest word of every column cannot stand side by side, and a table whose borders alone
    /// exceed the width is drawn at one cell per column and overflows. A row that wraps is taller than
    /// one line, so when any body row does, every body row is ruled off from the next — otherwise a
    /// continuation line reads as the start of the next row.
    /// </para>
    /// <para>
    /// A wrapped cell's styling carries across its lines: each line closes the SGR run and hyperlink in
    /// force where it breaks, so the padding and border after it stay plain, and the next line re-opens
    /// them.
    /// </para>
    /// </summary>
    /// <param name="maxWidth">The widest a line may be, borders included, in cells.</param>
    /// <inheritdoc cref="Render(IReadOnlyList{string}, IReadOnlyList{IReadOnlyList{string}}, IReadOnlyList{CellAlignment}, List{string}, BorderStyle, string, string, Func{string, int}?)"/>
    public static void Render(
        IReadOnlyList<string> headers,
        IReadOnlyList<IReadOnlyList<string>> rows,
        IReadOnlyList<CellAlignment> alignments,
        List<string> output,
        int maxWidth,
        BorderStyle style = BorderStyle.Light,
        string borderColor = "",
        string reset = "",
        Func<string, int>? visibleLength = null)
    {
        var columns = headers.Count;
        if (columns == 0)
        {
            return;
        }

        var measure = visibleLength ?? MarkdownRenderer.VisibleLength;
        var chars = BorderChars.For(style);

        var widths = new int[columns];
        for (var c = 0; c < columns; c++)
        {
            widths[c] = measure(headers[c]);
        }

        foreach (var row in rows)
        {
            for (var c = 0; c < columns && c < row.Count; c++)
            {
                var w = measure(row[c]);
                if (w > widths[c])
                {
                    widths[c] = w;
                }
            }
        }

        // Each column costs its content, a space either side and the divider after it; the left edge is
        // the one border that belongs to no column.
        var budget = maxWidth - (3 * columns + 1);
        if (widths.Sum() > budget)
        {
            widths = Fit(widths, headers, rows, measure, budget);
        }

        var header = WrapRow(headers, widths, measure);
        var body = new List<List<string>[]>(rows.Count);
        foreach (var row in rows)
        {
            body.Add(WrapRow(row, widths, measure));
        }
        var ruled = body.Exists(cells => Height(cells) > 1);

        var separator = BuildEdge(widths, chars, chars.TeeRight, chars.Cross, chars.TeeLeft, borderColor, reset);
        output.Add(BuildEdge(widths, chars, chars.TopLeft, chars.TeeDown, chars.TopRight, borderColor, reset));
        AddRow(header, widths, alignments, chars, borderColor, reset, measure, output);
        output.Add(separator);
        for (var r = 0; r < body.Count; r++)
        {
            if (ruled && r > 0)
            {
                output.Add(separator);
            }
            AddRow(body[r], widths, alignments, chars, borderColor, reset, measure, output);
        }
        output.Add(BuildEdge(widths, chars, chars.BottomLeft, chars.TeeUp, chars.BottomRight, borderColor, reset));
    }

    /// <summary>
    /// Narrows <paramref name="natural"/> to sum to <paramref name="budget"/> by capping the widest columns
    /// at one common width, so a column narrower than the cap is left alone. The cap never goes below a
    /// column's longest word unless those words cannot all fit side by side. Then the floor is the longest
    /// run between a word's hyphens and slashes, where <see cref="WrapCell"/> breaks a word first, and only
    /// when even those cannot fit does it drop to one cell.
    /// </summary>
    private static int[] Fit(int[] natural, IReadOnlyList<string> headers,
        IReadOnlyList<IReadOnlyList<string>> rows, Func<string, int> measure, int budget)
    {
        var floor = LongestWords(headers, rows, natural.Length, measure, bySegment: false);
        if (floor.Sum() > budget)
        {
            floor = LongestWords(headers, rows, natural.Length, measure, bySegment: true);
        }
        if (floor.Sum() > budget)
        {
            floor = Array.ConvertAll(natural, n => Math.Min(1, n));
        }
        if (floor.Sum() >= budget)
        {
            return floor;
        }

        // The largest cap that fits. It terminates: at a cap of zero every column sits at its floor, and
        // the floors were just shown to fit.
        var cap = natural.Max();
        while (Capped(natural, floor, cap).Sum() > budget)
        {
            cap--;
        }

        // A whole cap step can overshoot, so hand what it left over, a cell at a time, to the columns the
        // cap is still holding back.
        var widths = Capped(natural, floor, cap);
        var spare = budget - widths.Sum();
        for (var c = 0; c < widths.Length && spare > 0; c++)
        {
            if (widths[c] < natural[c])
            {
                widths[c]++;
                spare--;
            }
        }
        return widths;
    }

    private static int[] Capped(int[] natural, int[] floor, int cap)
    {
        var widths = new int[natural.Length];
        for (var c = 0; c < widths.Length; c++)
        {
            widths[c] = Math.Max(floor[c], Math.Min(cap, natural[c]));
        }
        return widths;
    }

    private static int[] LongestWords(IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<string>> rows,
        int columns, Func<string, int> measure, bool bySegment)
    {
        var longest = new int[columns];
        for (var c = 0; c < columns; c++)
        {
            longest[c] = LongestWord(headers[c], measure, bySegment);
        }
        foreach (var row in rows)
        {
            for (var c = 0; c < columns && c < row.Count; c++)
            {
                longest[c] = Math.Max(longest[c], LongestWord(row[c], measure, bySegment));
            }
        }
        return longest;
    }

    private static int LongestWord(string cell, Func<string, int> measure, bool bySegment)
    {
        var longest = 0;
        foreach (var word in VtWords.Split(cell))
        {
            if (bySegment)
            {
                foreach (var segment in VtWords.Segments(word))
                {
                    longest = Math.Max(longest, measure(segment));
                }
            }
            else
            {
                longest = Math.Max(longest, measure(word));
            }
        }
        return longest;
    }

    private static List<string>[] WrapRow(IReadOnlyList<string> cells, int[] widths, Func<string, int> measure)
    {
        var wrapped = new List<string>[widths.Length];
        for (var c = 0; c < widths.Length; c++)
        {
            wrapped[c] = c < cells.Count ? WrapCell(cells[c], widths[c], measure) : [string.Empty];
        }
        return wrapped;
    }

    private static int Height(List<string>[] cells)
    {
        var height = 1;
        foreach (var lines in cells)
        {
            height = Math.Max(height, lines.Count);
        }
        return height;
    }

    /// <summary>
    /// Splits one cell into lines no wider than <paramref name="width"/>. A cell that already fits comes
    /// back untouched, which is what keeps a table that needs no wrapping byte-identical to one rendered
    /// without a width.
    /// </summary>
    private static List<string> WrapCell(string cell, int width, Func<string, int> measure)
    {
        if (measure(cell) <= width)
        {
            return [cell];
        }

        var lines = new List<string>();
        var line = new StringBuilder();
        var lineWidth = 0;
        // What is in force at the current point, so a break can close it and the next line re-open it.
        var sgr = new StringBuilder();
        string? link = null;

        foreach (var word in VtWords.Split(cell))
        {
            var wordWidth = measure(word);
            if (wordWidth == 0)
            {
                // Styling alone, e.g. a reset standing between two spaces. It takes no room, so it
                // neither breaks the line nor earns a space.
                Append(word, breakInside: false);
                continue;
            }

            if (lineWidth > 0 && lineWidth + 1 + wordWidth <= width)
            {
                line.Append(' ');
                Append(word, breakInside: false);
                lineWidth += 1 + wordWidth;
                continue;
            }

            if (lineWidth > 0)
            {
                Break();
            }

            if (wordWidth <= width)
            {
                Append(word, breakInside: false);
                lineWidth += wordWidth;
                continue;
            }

            // Too long for the column even alone: break it after a hyphen or slash where one falls, and
            // inside a segment only where a segment is itself too long.
            foreach (var segment in VtWords.Segments(word))
            {
                var segmentWidth = measure(segment);
                if (lineWidth > 0 && lineWidth + segmentWidth > width)
                {
                    Break();
                }

                if (segmentWidth <= width - lineWidth)
                {
                    Append(segment, breakInside: false);
                    lineWidth += segmentWidth;
                }
                else
                {
                    Append(segment, breakInside: true);
                }
            }
        }

        lines.Add(line.ToString());
        return lines;

        void Append(string word, bool breakInside)
        {
            var i = 0;
            while (i < word.Length)
            {
                if (word[i] == '\e')
                {
                    var length = VtEscape.Length(word, i);
                    var sequence = word.Substring(i, length);
                    line.Append(sequence);
                    Track(sequence);
                    i += length;
                    continue;
                }

                var units = char.IsHighSurrogate(word[i]) && i + 1 < word.Length && char.IsLowSurrogate(word[i + 1]) ? 2 : 1;
                if (breakInside)
                {
                    var glyph = measure(word.Substring(i, units));
                    if (lineWidth > 0 && lineWidth + glyph > width)
                    {
                        Break();
                    }
                    lineWidth += glyph;
                }
                line.Append(word, i, units);
                i += units;
            }
        }

        void Break()
        {
            if (sgr.Length > 0)
            {
                line.Append(VtStyle.Reset);
            }
            if (link is not null)
            {
                line.Append(Osc8.Close);
            }
            lines.Add(line.ToString());
            line.Clear();
            if (link is not null)
            {
                line.Append(link);
            }
            line.Append(sgr);
            lineWidth = 0;
        }

        void Track(string sequence)
        {
            if (sequence.StartsWith("\e[", StringComparison.Ordinal) && sequence.EndsWith('m'))
            {
                // A reset clears everything before it; anything else layers on, and replaying the layers
                // in order (bold, then no-bold) lands on the same state.
                if (sequence is "\e[m" or "\e[0m")
                {
                    sgr.Clear();
                }
                else
                {
                    sgr.Append(sequence);
                }
            }
            else if (Osc8.IsHyperlink(sequence, out var opens))
            {
                link = opens ? sequence : null;
            }
        }
    }

    private static void AddRow(List<string>[] cells, int[] widths, IReadOnlyList<CellAlignment> alignments,
        BorderChars chars, string borderColor, string reset, Func<string, int> measure, List<string> output)
    {
        var height = Height(cells);
        var line = new string[cells.Length];
        for (var k = 0; k < height; k++)
        {
            for (var c = 0; c < cells.Length; c++)
            {
                line[c] = k < cells[c].Count ? cells[c][k] : string.Empty;
            }
            output.Add(BuildRow(line, widths, alignments, chars, borderColor, reset, measure));
        }
    }

    /// <summary>
    /// A full-width horizontal line: <paramref name="left"/>, then one run of
    /// <see cref="BorderChars.Horizontal"/> per column, separated by <paramref name="junction"/> and closed
    /// by <paramref name="right"/>. The three edges differ only in those three characters.
    /// </summary>
    private static string BuildEdge(int[] widths, BorderChars chars, char left, char junction, char right,
        string borderColor, string reset)
    {
        var sb = new StringBuilder();
        sb.Append(borderColor).Append(left);
        for (var i = 0; i < widths.Length; i++)
        {
            // +2 for the single space of padding either side of the cell content.
            sb.Append(new string(chars.Horizontal, widths[i] + 2));
            sb.Append(i < widths.Length - 1 ? junction : right);
        }
        sb.Append(reset);
        return sb.ToString();
    }

    private static string BuildRow(IReadOnlyList<string> cells, int[] widths,
        IReadOnlyList<CellAlignment> alignments, BorderChars chars, string borderColor, string reset,
        Func<string, int> measure)
    {
        var sb = new StringBuilder();
        sb.Append(borderColor).Append(chars.Vertical).Append(reset);
        for (var i = 0; i < widths.Length; i++)
        {
            var content = i < cells.Count ? cells[i] : string.Empty;
            var alignment = i < alignments.Count ? alignments[i] : CellAlignment.Left;
            sb.Append(' ')
              .Append(Align(content, measure(content), widths[i], alignment))
              .Append(' ')
              .Append(borderColor).Append(chars.Vertical).Append(reset);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Pads <paramref name="content"/> to <paramref name="columnWidth"/>. The padding is derived from
    /// <paramref name="visibleWidth"/>, not <c>content.Length</c>, so SGR escapes inside the cell do not
    /// eat the padding.
    /// </summary>
    private static string Align(string content, int visibleWidth, int columnWidth, CellAlignment alignment)
    {
        var pad = Math.Max(0, columnWidth - visibleWidth);
        return alignment switch
        {
            CellAlignment.Right => new string(' ', pad) + content,
            CellAlignment.Center => new string(' ', pad / 2) + content + new string(' ', pad - pad / 2),
            _ => content + new string(' ', pad),
        };
    }
}
