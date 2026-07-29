using DIR.Lib;

namespace Console.Lib;

/// <summary>
/// What a row knows about its own selection state when it builds itself. Shared by
/// <see cref="IRowLayout"/> and <see cref="ITreeNode{TSelf}"/>, because a list row and a tree node
/// need exactly the same thing -- which is why the two contracts used to carry duplicate
/// <c>isSelected</c> parameters that had to be kept in step by a doc comment.
/// <para>
/// Adding a future need adds a FIELD here, not another overload. The three-overload cascade this
/// replaced (<c>(width, mode)</c> -> <c>(.., isSelected)</c> -> <c>(.., selectedColumn, columnCount)</c>)
/// grew one rung per capability, and every rung was a place an implementation could silently opt out
/// of the newest information by only overriding an older shape.
/// </para>
/// </summary>
/// <param name="Selected">Whether the cursor is on this row.</param>
/// <param name="SelectedColumn">
/// Cursor column in <c>[0, ColumnCount)</c>, or <c>-1</c> when <paramref name="Selected"/> is false.
/// </param>
/// <param name="ColumnCount">
/// Selectable sub-cells per row; <c>1</c> for an ordinary row. Read through <see cref="Columns"/>
/// rather than directly -- see there.
/// </param>
public readonly record struct RowContext(bool Selected, int SelectedColumn, int ColumnCount)
{
    /// <summary>The ordinary single-column row.</summary>
    public static RowContext Single(bool selected) => new RowContext(selected, selected ? 0 : -1, 1);

    /// <summary>
    /// <see cref="ColumnCount"/> normalised to at least 1.
    /// <para>
    /// A <c>record struct</c>'s primary-constructor defaults do NOT apply to <c>default(RowContext)</c>
    /// or <c>new RowContext()</c>, so a zero-initialised context would otherwise report zero columns and
    /// make every column-aware row divide by it. Normalising on read means there is no way to hold an
    /// invalid context.
    /// </para>
    /// </summary>
    public int Columns => ColumnCount < 1 ? 1 : ColumnCount;
}

/// <summary>
/// Implemented by items in a <see cref="ScrollableList{TItem}"/> to build their row as a
/// <see cref="Layout.Node"/> tree.
/// <para>
/// <b>Why a tree and not a formatted string.</b> The old contract was
/// <c>string FormatRow(int width, ColorMode, ...)</c> whose documented obligation was "include VT escape
/// codes and pad to the full width" -- i.e. every row hand-rolled its own layout, its own padding, its
/// own truncation, and its own escape sequences, and any of those being subtly wrong was invisible until
/// it wasn't. Three consequences drove this cut:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>An inline button on a row had no hit region.</b> There was no arranged rect to bind to, so a
/// caller had to re-derive the button's columns alongside the code that drew them and keep the two in
/// step by hand. The row's usable width is also not the viewport width (the scrollbar takes a column
/// once the list overflows), so a right-aligned button silently drifted exactly when the list scrolled.
/// Hits now ride on <c>.Clickable(...)</c> and come back through <see cref="CellLayout.HitTest"/> over
/// the same arranged rect that was painted -- draw==hit by construction.
/// </description></item>
/// <item><description>
/// <b>A row cannot state a colour it does not own.</b> Foreground-only writes relied on whatever SGR
/// state a previous write happened to leave in effect, which a real terminal forgives and a cell buffer
/// cannot. <see cref="CellLayout"/> resolves each cell's background from the tree, so the row states
/// colours and the painter states the <see cref="ColorMode"/> -- which is why the mode is no longer a
/// parameter here.
/// </description></item>
/// <item><description>
/// <b>The same row is often also a GPU row.</b> A <see cref="Layout.Node"/> tree renders on a pixel
/// surface too, so a row authored once can serve both -- rather than being written twice and drifting,
/// which is how a terminal row ended up missing a button its GPU twin had.
/// </description></item>
/// </list>
/// </summary>
public interface IRowLayout
{
    /// <summary>
    /// Builds this row's content as a layout tree. The list arranges it into the row's rect (content
    /// width excludes the scrollbar column) and paints it via <see cref="CellLayout"/>, so an
    /// implementation states structure and colour and never pads, truncates, or emits an escape code.
    /// </summary>
    Layout.Node BuildRow(in RowContext context);
}
