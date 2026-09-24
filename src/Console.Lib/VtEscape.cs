namespace Console.Lib;

/// <summary>
/// Where an escape sequence embedded in rendered text ends, for code that has to step over one without
/// interpreting it: measuring what is visible, and wrapping a styled run without cutting through a sequence.
/// <para>
/// It exists because "skip to the next 'm'" is only right for SGR. An OSC 8 hyperlink is terminated by BEL or
/// ST, and its URL routinely contains an 'm' (<c>.com</c>, <c>.md</c>), so that rule ended the sequence in the
/// middle of the URL and counted the rest of it as text. <see cref="CellBuffer"/> has its own parser because
/// it also interprets what it reads; this one only measures.
/// </para>
/// </summary>
internal static class VtEscape
{
    /// <summary>
    /// The length of the escape sequence starting at <paramref name="start"/>, where
    /// <paramref name="text"/>[<paramref name="start"/>] is ESC. CSI runs to its final byte (<c>@</c>..<c>~</c>),
    /// OSC to BEL or ST, and anything else is ESC plus one character. An unterminated sequence runs to the end
    /// of the text, since everything after it is still inside it.
    /// </summary>
    internal static int Length(string text, int start)
    {
        var next = start + 1;
        if (next >= text.Length)
        {
            return 1;
        }

        if (text[next] == '[')
        {
            var end = next + 1;
            while (end < text.Length && !char.IsBetween(text[end], '@', '~'))
            {
                end++;
            }

            return end < text.Length ? end + 1 - start : text.Length - start;
        }

        if (text[next] == ']')
        {
            for (var end = next + 1; end < text.Length; end++)
            {
                if (text[end] == '\a')
                {
                    return end + 1 - start;
                }

                if (text[end] == '\e' && end + 1 < text.Length && text[end + 1] == '\\')
                {
                    return end + 2 - start;
                }
            }

            return text.Length - start;
        }

        return 2;
    }
}
