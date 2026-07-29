using DIR.Lib;

namespace Console.Lib;

/// <summary>
/// A node in a <see cref="TreeView{T}"/>. The widget owns indent + twirl drawing
/// and scrolling — implementations only describe the row's content cell (the
/// portion of the row after indent + twirl), as a <see cref="Layout.Node"/> tree.
/// <para>
/// <see cref="Children"/>, <see cref="HasChildren"/> and <see cref="EnsureChildrenLoaded"/> keep their
/// default implementations: those describe genuinely optional BEHAVIOUR (a leaf has no children; lazy
/// loading is opt-in), not a second way to satisfy the same obligation. <see cref="BuildNodeContent"/>
/// has no default on purpose -- a node that does not describe itself has nothing to draw.
/// </para>
/// </summary>
/// <typeparam name="TSelf">
/// CRTP self-type — lets the widget walk concrete children without
/// allocating wrapper boxes for each row.
/// </typeparam>
public interface ITreeNode<TSelf> where TSelf : class, ITreeNode<TSelf>
{
    /// <summary>
    /// Direct children of this node. May be empty for a leaf, or for an
    /// unloaded lazy node (in which case <see cref="HasChildren"/> should
    /// still return <c>true</c> so the twirl is drawn).
    /// </summary>
    IReadOnlyList<TSelf> Children { get; }

    /// <summary>
    /// True when this node has (or could have) children — used to decide
    /// whether to draw an expandable twirl ('▶' / '▼') versus a leaf marker
    /// ('·'). Default implementation returns <c>Children.Count &gt; 0</c>;
    /// override when children are populated lazily (e.g. for an on-disk folder
    /// that has not yet been enumerated, return <c>true</c> so the twirl is
    /// drawn — the widget will call <see cref="EnsureChildrenLoaded"/> before
    /// it tries to flatten the subtree).
    /// </summary>
    bool HasChildren => Children.Count > 0;

    /// <summary>
    /// Hook invoked by the widget the first time a node is about to be expanded
    /// (i.e. before its <see cref="Children"/> are read for flattening). Default
    /// is a no-op. Override to populate <see cref="Children"/> lazily — e.g. by
    /// enumerating the file system or fetching a remote subtree. Must be
    /// idempotent: the widget calls this every render cycle for any expanded
    /// node, so the implementation is expected to gate itself on a "loaded"
    /// flag.
    /// </summary>
    void EnsureChildrenLoaded() { }

    /// <summary>
    /// Builds the content portion of this node's row as a layout tree. The widget arranges it into the
    /// rect left after the indent + twirl and paints it via <see cref="CellLayout"/>, so an
    /// implementation states structure and colour and never pads, truncates, or emits an escape code --
    /// see <see cref="IRowLayout.BuildRow"/> for why that obligation moved out of the implementation.
    /// </summary>
    Layout.Node BuildNodeContent(in RowContext context);
}
