namespace Console.Lib;

/// <summary>
/// Implemented by items in a <see cref="ScrollableList{TItem}"/> to produce a styled VT row.
/// </summary>
public interface IRowFormatter
{
    /// <summary>
    /// Formats this item as a single row of the given <paramref name="width"/>.
    /// The returned string must include VT escape codes and pad to the full width.
    /// </summary>
    string FormatRow(int width, ColorMode colorMode);

    /// <summary>
    /// Selection-aware overload. The default implementation ignores
    /// <paramref name="isSelected"/> and falls back to <see cref="FormatRow(int, ColorMode)"/>;
    /// override this on rows that should paint a distinct cursor highlight.
    /// Mirrors <see cref="ITreeNode{TSelf}.FormatNodeContent(int, ColorMode, bool)"/>
    /// — same pattern, same contract. Existing implementations stay binary-compatible.
    /// </summary>
    string FormatRow(int width, ColorMode colorMode, bool isSelected) =>
        FormatRow(width, colorMode);
}
