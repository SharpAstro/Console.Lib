using System.Collections.Immutable;
using DIR.Lib;

namespace Console.Lib;

/// <summary>
/// TUI cell-surface widget for the vertical "wizard" menu. Wraps <see cref="MenuModel"/>
/// (state/input) and <see cref="MenuLayout.BuildTree"/> (rendering) via the cell painter
/// <see cref="CellLayout"/>. Surface-neutral counterpart to <c>PixelMenuWidget&lt;TSurface&gt;</c>
/// in DIR.Lib.
/// </summary>
public sealed class MenuWidget(ITerminalViewport viewport, MenuColors? colors = null) : Widget(viewport)
{
    private static readonly CellMeasureContext MeasureCtx = new();

    private readonly MenuModel _model = new();
    private readonly MenuColors _colors = colors ?? new MenuColors();

    // Last arranged tree from Render, reused for mouse hit-testing.
    private ImmutableArray<Layout.ArrangedNode<int>> _arranged = ImmutableArray<Layout.ArrangedNode<int>>.Empty;

    /// <summary>Zero-based index of the currently highlighted item.</summary>
    public int SelectedIndex => _model.SelectedIndex;

    /// <summary>True after the user has confirmed a selection.</summary>
    public bool IsConfirmed => _model.IsConfirmed;

    /// <summary>
    /// Resets the menu with new content and clears the confirmed state.
    /// <paramref name="selected"/> is clamped to the valid item range.
    /// </summary>
    public void Reset(string title, string prompt, ImmutableArray<string> items, int selected = 0)
        => _model.Reset(title, prompt, items, selected);

    /// <summary>
    /// Renders the menu to the viewport. Builds the layout tree via
    /// <see cref="MenuLayout.BuildTree"/> using <c>fontSize: 1f</c> (one cell = one design unit)
    /// then arranges and paints it via <see cref="CellLayout"/>.
    /// </summary>
    public override void Render()
    {
        var (w, h) = Viewport.Size;
        if (w <= 0 || h <= 0) return;

        var bounds = new Rect<int>(0, 0, w, h);
        _arranged = Layout.Engine.Arrange(MenuLayout.BuildTree(_model, _colors, fontSize: 1f), bounds, MeasureCtx);
        CellLayout.Paint(Viewport, _arranged);
    }

    /// <summary>
    /// Routes an <see cref="InputKey"/> (e.g., Up/Down/Enter/D1..D9) to <see cref="MenuModel.HandleKey"/>.
    /// Returns <c>true</c> when the key was consumed.
    /// </summary>
    public bool HandleKey(InputKey key) => _model.HandleKey(key);

    /// <summary>
    /// Handles a mouse event: translates to viewport-local cells via <see cref="Widget.HitTest"/>,
    /// then delegates to <see cref="CellLayout.HitTest"/> to fire the matching item's OnClick
    /// (which calls <see cref="MenuModel.ConfirmAt"/>).
    /// Returns <c>true</c> when a menu item was clicked.
    /// </summary>
    public bool HandleMouse(MouseEvent ev)
    {
        if (ev.IsRelease || ev.Button != 0) return false;
        if (HitTest(ev.X, ev.Y) is not { } local) return false;
        return CellLayout.HitTest(_arranged, local.Col, local.Row) is not null;
    }
}
