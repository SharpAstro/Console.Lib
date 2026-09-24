using System.Collections.Generic;
using System.Text;

namespace Console.Lib;

/// <summary>
/// Splits styled text into the units a wrapper places: words, and when a word is too long for its line,
/// the pieces it breaks into. Escape sequences are kept whole (<see cref="VtEscape"/>) and travel with the
/// text they touch, so a piece carries the styling that opens or closes around it.
/// <para>
/// Shared by <see cref="MarkdownRenderer.WordWrap"/> and <see cref="TextTable"/>, so a paragraph and a
/// table cell break an over-long word at the same places.
/// </para>
/// </summary>
internal static class VtWords
{
    /// <summary>Splits <paramref name="text"/> at its spaces; runs of spaces collapse.</summary>
    internal static List<string> Split(string text)
    {
        var words = new List<string>();
        var current = new StringBuilder();
        var i = 0;
        while (i < text.Length)
        {
            if (text[i] == '\e')
            {
                var length = VtEscape.Length(text, i);
                current.Append(text, i, length);
                i += length;
            }
            else if (text[i] == ' ')
            {
                if (current.Length > 0)
                {
                    words.Add(current.ToString());
                    current.Clear();
                }
                i++;
            }
            else
            {
                current.Append(text[i]);
                i++;
            }
        }
        if (current.Length > 0)
        {
            words.Add(current.ToString());
        }
        return words;
    }

    /// <summary>
    /// Splits a word after each hyphen and slash — <c>printer-</c>, <c>driver-</c>, <c>cups-pdf</c>'s
    /// <c>pdf</c> — which is where a word too long for its line reads best broken. A split waits for the
    /// next visible character, so escapes straight after the hyphen stay with the part it ends.
    /// </summary>
    internal static List<string> Segments(string word)
    {
        var segments = new List<string>();
        var current = new StringBuilder();
        var split = false;
        var i = 0;
        while (i < word.Length)
        {
            if (word[i] == '\e')
            {
                var length = VtEscape.Length(word, i);
                current.Append(word, i, length);
                i += length;
                continue;
            }

            if (split)
            {
                segments.Add(current.ToString());
                current.Clear();
                split = false;
            }
            current.Append(word[i]);
            split = word[i] is '-' or '/';
            i++;
        }
        if (current.Length > 0)
        {
            segments.Add(current.ToString());
        }
        return segments;
    }

    /// <summary>
    /// Splits a segment into its visible characters — the last resort, for a segment that is itself too
    /// long. A surrogate pair stays together; escapes go with the character after them, and any trailing
    /// the last character go with it.
    /// </summary>
    internal static List<string> Glyphs(string segment)
    {
        var glyphs = new List<string>();
        var current = new StringBuilder();
        // Whether current already holds its character, i.e. whatever comes next starts the next glyph.
        var full = false;
        var i = 0;
        while (i < segment.Length)
        {
            var escape = segment[i] == '\e';
            var length = escape
                ? VtEscape.Length(segment, i)
                : char.IsHighSurrogate(segment[i]) && i + 1 < segment.Length && char.IsLowSurrogate(segment[i + 1]) ? 2 : 1;
            if (full)
            {
                glyphs.Add(current.ToString());
                current.Clear();
                full = false;
            }
            current.Append(segment, i, length);
            full = !escape;
            i += length;
        }
        if (current.Length > 0)
        {
            if (!full && glyphs.Count > 0)
            {
                glyphs[^1] += current.ToString();
            }
            else
            {
                glyphs.Add(current.ToString());
            }
        }
        return glyphs;
    }
}
