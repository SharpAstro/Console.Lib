using System;
using System.Collections.Generic;
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
    /// per row, bottom border.
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

        output.Add(BuildEdge(widths, chars, chars.TopLeft, chars.TeeDown, chars.TopRight, borderColor, reset));
        output.Add(BuildRow(headers, widths, alignments, chars, borderColor, reset, measure));
        output.Add(BuildEdge(widths, chars, chars.TeeRight, chars.Cross, chars.TeeLeft, borderColor, reset));
        foreach (var row in rows)
        {
            output.Add(BuildRow(row, widths, alignments, chars, borderColor, reset, measure));
        }
        output.Add(BuildEdge(widths, chars, chars.BottomLeft, chars.TeeUp, chars.BottomRight, borderColor, reset));
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
