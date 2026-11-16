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
        if (CloseMark != DefaultCloseMark) tree = ReplaceCloseMark(tree, CloseMark);
        tree = CentreCard(tree, MathF.Min(maxW, room), w, h, style.Border?.Width ?? 0f);
        _arranged = Layout.Engine.Arrange(tree, new Rect<int>(0, 0, w, h), CellMeasureContext.CellAuthored);
        CellLayout.Paint(Viewport, _arranged);
    }

    private const string DefaultCloseMark = "×";

    // DIR.Lib's markers ("▶  " / "   ") are internal; both are this wide.
    private const int MarkerWidth = 3;

    /// <summary>
    /// Adapts the built panel so its content is centred on the card: the card's column fills the card (DIR.Lib leaves
    /// it hugging its content, at the left when the card is held at its minimum width), and every item label gets a
    /// mirror gutter after the text so marker + text + gutter is symmetric, which puts all item texts on one centre
    /// with the marker in a fixed gutter at their left. The card is widened (up to <paramref name="maxW"/>) to hold the
    /// symmetric rows plus its padding and border, and re-centred on the surface.
    /// </summary>
    private static Layout.Node CentreCard(Layout.Node tree, float maxW, int surfaceW, int surfaceH, float border)
    {
        if (tree is not Layout.Node.Overlay { Top: Layout.Node.Anchored { Child: Layout.Node.Stack { Children: [Layout.Node.Stack column] } card } anchored } overlay
            || card.Width.Kind != Layout.SizeKind.Fixed)
            return tree;

        // One terminal row per header, title, prompt and item (DIR.Lib sizes them in font units for a pixel surface,
        // which leaves a blank line under every item). One blank row sits between prompt and items and one blank row
        // of padding inside the top and bottom border; when the viewport is too short the gap goes first, then the padding.
        var textRows = column.Children.Count(c => c is Layout.Node.Stack or Layout.Node.Leaf { Content: Layout.Content.Text });
        var avail = (int)MathF.Max(1f, surfaceH - 2f * border);
        var gapRows = textRows + 3 <= avail ? 1 : 0;
        var padRows = textRows + gapRows + 2 <= avail ? 1 : 0;
        var cardHeight = MathF.Min(surfaceH, textRows + gapRows + 2 * padRows + 2f * border);

        var widest = 0f;
        var last = column.Children.Length - 1;
        var rows = ImmutableArray.CreateBuilder<Layout.Node>(column.Children.Length);
        for (var i = 0; i <= last; i++)
        {
            var node = column.Children[i];
            switch (node)
            {
                case Layout.Node.Leaf { Content: Layout.Content.Box } spacer:
                    node = spacer with { Height = Layout.Sizing.Fixed(i == 0 || i == last ? padRows : gapRows) };
                    break;
                case Layout.Node.Leaf { Content: Layout.Content.Text } textRow:
                    node = textRow with { Height = Layout.Sizing.Fixed(1f) };
                    break;
                case Layout.Node.Stack header:
                    node = header with
                    {
                        Height = Layout.Sizing.Fixed(1f),
                        Children = [.. header.Children.Select(c => c is Layout.Node.Leaf l ? l with { Height = Layout.Sizing.Fixed(1f) } : c)],
                    };
                    break;
            }
            if (node is Layout.Node.Leaf { Hit: HitResult.ListItemHit, Content: Layout.Content.Text it } item
                && it.Value.Length >= MarkerWidth)
            {
                node = item with { Content = it with { Value = it.Value + new string(' ', MarkerWidth) } };
            }
            if (node is Layout.Node.Leaf { Content: Layout.Content.Text t })
                widest = MathF.Max(widest, CellFloatMeasureContext.Instance.MeasureText(t.Value.AsSpan(), t.FontSize).Width);
            else if (node is Layout.Node.Stack)
                widest = MathF.Max(widest, card.Width.Value - 2f * border - 2f * card.Padding);
            rows.Add(node);
        }

        var wanted = MathF.Ceiling(widest + 2f * card.Padding + 2f * border);
        var width = MathF.Max(card.Width.Value, MathF.Min(wanted, MathF.Max(maxW, card.Width.Value)));
        var newColumn = column with { Children = rows.ToImmutable(), Width = Layout.Sizing.Star() };
        var newCard = card with { Children = [newColumn], Width = Layout.Sizing.Fixed(width), Height = Layout.Sizing.Fixed(cardHeight) };
        return overlay with
        {
            Top = anchored with
            {
                Child = newCard,
                OffsetAlong = MathF.Floor((surfaceW - width) / 2f),
                OffsetAcross = MathF.Floor((surfaceH - cardHeight) / 2f),
            },
        };
    }

    /// <summary>
    /// The host's "go back" action. When set, <see cref="RenderPanel"/> shows a "&lt; Back" control at the left of a
    /// header row; a press on it runs the action and confirms no row. Passes through to <see cref="MenuModel.Back"/>.
    /// Shown only in <see cref="RenderPanel"/>: <see cref="Render"/>'s tree has no header.
    /// </summary>
    public Action? Back { get => _model.Back; set => _model.Back = value; }

    /// <summary>
    /// The host's "close" action. When set, <see cref="RenderPanel"/> shows a close mark at the right of the header
    /// row; a press on it runs the action and confirms no row. Passes through to <see cref="MenuModel.Close"/>.
    /// Shown only in <see cref="RenderPanel"/>: <see cref="Render"/>'s tree has no header.
    /// </summary>
    public Action? Close { get => _model.Close; set => _model.Close = value; }

    /// <summary>
    /// The text of the close control in <see cref="RenderPanel"/>. Defaults to U+00D7, which many terminal fonts lack
    /// or draw wide; set an ASCII mark such as <c>[x]</c> for those. The control widens to fit it.
    /// </summary>
    public string CloseMark { get; init; } = DefaultCloseMark;

    // DIR.Lib's close label is internal, so the mark is swapped in the built tree: the leaf hit as MenuClose gets the
    // new text and, when the mark needs more room, a wider Fixed width. Hit and OnClick are left as they are.
    private static Layout.Node ReplaceCloseMark(Layout.Node node, string mark) => node switch
    {
        Layout.Node.Overlay o => o with { Base = ReplaceCloseMark(o.Base, mark), Top = ReplaceCloseMark(o.Top, mark) },
        Layout.Node.Anchored a => a with { Child = ReplaceCloseMark(a.Child, mark) },
        Layout.Node.Stack s => s with { Children = [.. s.Children.Select(c => ReplaceCloseMark(c, mark))] },
        Layout.Node.Leaf { Hit: HitResult.ButtonHit { Action: "MenuClose" }, Content: Layout.Content.Text t } l => l with
        {
            Content = t with { Value = mark },
            Width = Layout.Sizing.Fixed(MathF.Max(l.Width.Value,
                l.Width.Value + CellFloatMeasureContext.Instance.MeasureText(mark.AsSpan(), t.FontSize).Width
                - CellFloatMeasureContext.Instance.MeasureText(DefaultCloseMark.AsSpan(), t.FontSize).Width)),
        },
        _ => node,
    };

    /// <summary>
    /// Routes an <see cref="InputKey"/> (e.g., Up/Down/Enter/D1..D9) to <see cref="MenuModel.HandleKey"/>.
    /// Returns <c>true</c> when the key was consumed.
    /// </summary>
    public bool HandleKey(InputKey key) => _model.HandleKey(key);

    /// <summary>
    /// Handles a mouse event: translates to viewport-local cells via <see cref="Widget.HitTest"/>,
    /// then delegates to <see cref="CellLayout.HitTest"/> to fire the matching item's OnClick
    /// (which calls <see cref="MenuModel.ConfirmAt"/>).
    /// Returns <c>true</c> when the press landed on a hit region (an item, or the panel's Back or close control,
    /// whose action runs without confirming a row); <c>false</c> otherwise.
    /// </summary>
    public bool HandleMouse(MouseEvent ev)
    {
        if (ev.IsRelease || ev.Button != 0) return false;
        if (HitTest(ev.X, ev.Y) is not { } local) return false;
        return CellLayout.HitTest(_arranged, local.Col, local.Row) is not null;
    }
}
