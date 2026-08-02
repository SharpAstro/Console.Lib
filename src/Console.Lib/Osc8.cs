namespace Console.Lib;

/// <summary>
/// The OSC 8 hyperlink sequences, in one place because three callers emit them and they have to agree:
/// <see cref="CellLayout"/> (a linked text leaf), <c>VirtualTerminal.ConsoleCellSink</c> (a diffed run), and
/// <see cref="MarkdownRenderer"/> (an inline link). <see cref="CellBuffer"/> parses the same shape back.
/// <para>
/// BEL-terminated rather than ST-terminated (<c>\e\\</c>). The two are equivalent per the spec and BEL has
/// the wider terminal support — notably Windows Terminal before 1.18. The parser accepts both, because an
/// app is free to write its own.
/// </para>
/// </summary>
internal static class Osc8
{
    /// <summary>Closes the open hyperlink. An empty URI field is what OSC 8 defines as "no link".</summary>
    internal const string Close = "\e]8;;\a";

    /// <summary>Opens a hyperlink to <paramref name="url"/>, for text emitted as one contiguous run.</summary>
    internal static string Open(string url) => $"\e]8;;{url}\a";

    /// <summary>
    /// Opens a hyperlink carrying an <c>id=</c>, which is how OSC 8 says "these separate runs are one link".
    /// <para>
    /// A diffing flush needs this and a direct write does not. The diff emits only the cells that changed,
    /// so one link's text routinely reaches the terminal as several runs split by unchanged cells, by a pen
    /// change, or by a cursor move — and a terminal receiving them without an id treats each as its own
    /// link. Visibly: hovering underlines only the fragment under the pointer instead of the whole path.
    /// </para>
    /// </summary>
    internal static string Open(string url, string id) => $"\e]8;id={id};{url}\a";

    /// <summary>
    /// A stable id for <paramref name="url"/> — FNV-1a, hex. Deterministic on purpose: <see cref="string.GetHashCode()"/>
    /// is randomised per process, which is sufficient at runtime and makes the emitted bytes unassertable in
    /// a test.
    /// <para>
    /// Two distinct links sharing a URL therefore share an id, and a terminal will treat them as one link.
    /// That is the right answer — they point at the same place — and it is also the only one available
    /// without the cell carrying an identity beyond its target.
    /// </para>
    /// </summary>
    internal static string IdFor(string url)
    {
        const uint offsetBasis = 2166136261;
        const uint prime = 16777619;

        var hash = offsetBasis;
        foreach (var ch in url)
        {
            hash = (hash ^ ch) * prime;
        }

        return hash.ToString("x8");
    }
}
