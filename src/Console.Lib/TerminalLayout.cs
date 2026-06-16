using DIR.Lib;
namespace Console.Lib;

public sealed class TerminalLayout
{
    private readonly ITerminalViewport _root;
    private readonly List<(DockStyle Dock, int Size, TerminalViewport Viewport)> _edgeDocked = [];
    private TerminalViewport? _fillViewport;
    private int _lastWidth, _lastHeight;

    public TerminalLayout(ITerminalViewport root)
    {
        _root = root;
        var (w, h) = root.Size;
        _lastWidth = w;
        _lastHeight = h;
    }

    public TerminalViewport Dock(DockStyle dock, int size = 0)
    {
        var viewport = new TerminalViewport(_root, 0, 0, 0, 0);
        if (dock == DockStyle.Fill)
            _fillViewport = viewport;
        else
            _edgeDocked.Add((dock, size, viewport));
        ComputeGeometries();
        return viewport;
    }

    public bool Recompute()
    {
        var (w, h) = _root.Size;
        if (w == _lastWidth && h == _lastHeight)
            return false;

        _lastWidth = w;
        _lastHeight = h;
        ComputeGeometries();
        return true;
    }

    private void ComputeGeometries()
    {
        // The four-way edge arithmetic lives once in DockLayout<int> (cells); TerminalLayout keeps only the
        // terminal-specific safety clamp (a strip never exceeds the cells still remaining) + the viewport wiring.
        var layout = new DockLayout<int>(new Rect<int>(0, 0, _lastWidth, _lastHeight));

        foreach (var (dock, size, viewport) in _edgeDocked)
        {
            var remaining = layout.Fill();
            var clamped = dock is DockStyle.Top or DockStyle.Bottom
                ? Math.Min(size, remaining.Height)
                : Math.Min(size, remaining.Width);

            var r = layout.Dock(dock, clamped);
            viewport.UpdateGeometry(r.X, r.Y, r.Width, r.Height);
        }

        var fill = layout.Fill();
        _fillViewport?.UpdateGeometry(fill.X, fill.Y, fill.Width, fill.Height);
    }
}
