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
    /// Look and sizing of <see cref="RenderPanel"/>, in cells: card 24..60 wide (full width less the side margin on
    /// a portrait viewport), 2 cells of margin, a one-cell light border, and a transparent scrim.
    /// </summary>
    public MenuPanelStyle PanelStyle { get; init; } = new()
    {
        MinWidth = 24f,
        MaxWidth = 60f,
        SideMargin = 2f,
        Border = new Layout.BorderStyle { Color = new RGBAColor32(0x60, 0x70, 0xa0, 0xff), Width = 1f, Radius = 0f },
        Scrim = new RGBAColor32(0, 0, 0, 0),
    };

    /// <summary>
    /// Renders the menu as a card (<see cref="MenuLayout.BuildPanel"/>) centred over whatever the host drew first.
    /// The viewport is NOT cleared. The scrim is transparent by default and a fill of alpha 0 writes no cell, so
    /// every cell outside the card is left exactly as it was: a cell viewport cannot read cells back, so there is
    /// no dimming. A press anywhere outside the card is still consumed by <see cref="HandleMouse"/>.
    /// The font is planned as one cell, so rows are as <see cref="MenuLayout.BuildTree"/> has them at size 1.
    /// </summary>
    public void RenderPanel()
    {
        var (w, h) = Viewport.Size;
        if (w <= 0 || h <= 0) return;

        var style = PanelStyle;
        var portrait = h > w * style.PortraitAspect;
        var room = MathF.Max(1f, w - 2f * style.SideMargin);
        var maxW = portrait ? room : MathF.Min(style.MaxWidth, room);
        // The planner starts the font at maxW * FontToCard; make that one cell (it is capped further to fit).
        style = style with { FontToCard = 1f / maxW };

        var tree = MenuLayout.BuildPanel(_model, _colors, style, CellFloatMeasureContext.Instance, w, h);
        _arranged = Layout.Engine.Arrange(tree, new Rect<int>(0, 0, w, h), CellMeasureContext.CellAuthored);
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
