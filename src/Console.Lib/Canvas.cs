using DIR.Lib;

namespace Console.Lib;

/// <summary>
/// A widget that renders an <see cref="ISixelEncoder"/> to a viewport.
/// <see cref="Widget.Render()"/> performs a full Sixel blit;
/// <see cref="Render(RectInt)"/> renders only the dirty region.
/// </summary>
public class Canvas(ITerminalViewport viewport, ISixelEncoder encoder) : Widget(viewport)
{
    /// <summary>Viewport size in pixels.</summary>
    public (uint Width, uint Height) PixelSize => Viewport.PixelSize;

    /// <summary>The Sixel encoder that owns the drawing surface.</summary>
    public ISixelEncoder Encoder => encoder;

    /// <summary>Position the cursor within the canvas.</summary>
    public void SetCursorPosition(int col, int row) => Viewport.SetCursorPosition(col, row);

    /// <summary>
    /// Renders a partial Sixel update for the given dirty region.
    /// The clip rectangle's Y bounds (in pixels) are aligned to cell-height
    /// boundaries before encoding, since Sixel output must start at a character row.
    /// </summary>
    /// <param name="clip">Dirty region in pixel coordinates.</param>
    public void Render(RectInt clip)
    {
        var cellHeight = Viewport.CellSize.Height;
        var startRow = clip.UpperLeft.Y / cellHeight;
        var endRow = (clip.LowerRight.Y + cellHeight - 1) / cellHeight;

        var pixelStartY = startRow * cellHeight;
        var pixelEndY = (int)Math.Min(encoder.Height, (uint)(endRow * cellHeight));
        var cropHeight = pixelEndY - pixelStartY;

        if (cropHeight > 0)
        {
            SetCursorPosition(0, startRow);
            encoder.EncodeSixel(pixelStartY, (uint)cropHeight, Viewport.OutputStream);
        }
    }

    /// <summary>Performs a full Sixel blit of the renderer surface.</summary>
    public override void Render()
    {
        SetCursorPosition(0, 0);
        encoder.EncodeSixel(Viewport.OutputStream);
    }
}
