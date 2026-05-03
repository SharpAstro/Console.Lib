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

    /// <summary>
    /// Column-aware overload. Used by <see cref="ScrollableList{TItem}"/> when
    /// <see cref="ScrollableList{TItem}.Columns(int)"/> is greater than one,
    /// so a row can paint a per-column cursor highlight (e.g. white-ply vs
    /// black-ply in a chess move-history row).
    /// <paramref name="selectedColumn"/> is <c>-1</c> when <paramref name="isSelected"/>
    /// is <c>false</c>; otherwise it is in <c>[0, columnCount)</c>.
    /// The default implementation drops the column info and falls back to
    /// <see cref="FormatRow(int, ColorMode, bool)"/> — single-column callers
    /// stay binary-compatible.
    /// </summary>
    string FormatRow(int width, ColorMode colorMode, bool isSelected, int selectedColumn, int columnCount) =>
        FormatRow(width, colorMode, isSelected);
}
