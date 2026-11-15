using System.Collections.Immutable;
using DIR.Lib;

namespace Console.Lib;

/// <summary>The cell-only rules, selectable through <see cref="CellWireframeOptions.Rules"/>. DIR.Lib's own rules stay in <see cref="WireframeRules"/>.</summary>
[Flags]
public enum CellWireframeRules
{
    None = 0,
    /// <summary>A one-line text leaf arranged taller than one row, with an empty row inside its own fill.</summary>
    DoubledRow = 1,
    /// <summary>A filled node that is arranged but paints no cell.</summary>
    VanishedFill = 2,
    /// <summary>Text cut while its row has free cells to its right within the same container.</summary>
    CutWithRoom = 4,
    /// <summary>The surface wrote cells but the frame holds no text, fill or region.</summary>
    NothingRecorded = 8,
    /// <summary>A filled container whose content all sits on one row while it is arranged taller than that row plus its own vertical padding. Whether the extra height came from a stretch or a fixed size, it is a finding. <see cref="CellWireframeFinding.Where"/> is the clipped rect, the part a person sees.</summary>
    EmptyRows = 16,
    /// <summary>A filled node one row tall, spanning its container's full clipped width, with no text or painted content inside, painted with whole cells. A rule drawn as a grey bar; a rule should be a thin line (upper half blocks). <see cref="CellWireframeFinding.Where"/> is the clipped rect.</summary>
    SolidStrip = 32,
    All = DoubledRow | VanishedFill | CutWithRoom | NothingRecorded | EmptyRows | SolidStrip,
}

/// <summary>A cell-only finding. <see cref="Where"/> is in cells, like DIR.Lib's findings on a cell frame.</summary>
public readonly record struct CellWireframeFinding(CellWireframeRules Rule, string Message, RectF32 Where);

/// <summary>Options for <see cref="CellWireframe.CheckCells"/>. Tolerance stays 0 cells unless <see cref="Pixel"/> says otherwise.</summary>
public sealed record CellWireframeOptions
{
    public CellWireframeRules Rules { get; init; } = CellWireframeRules.All;

    /// <summary>Options for DIR.Lib's rules (ignore callbacks, rule flags); its own <c>Tolerance</c> is not used, see <see cref="Tolerance"/>.</summary>
    public WireframeOptions? Pixel { get; init; }

    /// <summary>How many cells two things may share before it counts. 0 by default.</summary>
    public float Tolerance { get; init; }
}

/// <summary>What the recorder saw of one arranged leaf or filled node, beyond what <see cref="WireframeFrame"/> carries.</summary>
public readonly record struct CellFact(
    int Depth, Rect<int> Bounds, Rect<int> Clipped, bool IsText, bool OwnFill, bool FillPainted,
    int Row, int TextLength, int PaintedLength, bool PaintsContent = false)
{
    /// <summary>Top plus bottom vertical padding in cells. An init property, not a constructor parameter, so the constructor keeps the shape it had before 5.20.</summary>
    public int PaddingRows { get; init; }

    /// <summary>The own fill was painted as sub-cell upper half blocks (a thin line), not whole cells. An init property for the same reason as <see cref="PaddingRows"/>.</summary>
    public bool ThinFill { get; init; }

    /// <summary>The node painted a declared border on the outer ring of its rect. The border is the node's own and records no text or fill; this marks the cells it wrote as accounted for. An init property for the same reason as <see cref="PaddingRows"/>.</summary>
    public bool Bordered { get; init; }
}

/// <summary>A DIR.Lib <see cref="WireframeFrame"/> plus the cell facts the cell rules need.</summary>
public sealed record CellWireframeFrame(WireframeFrame Frame, ImmutableArray<CellFact> Facts, int CellsWritten = 0);

/// <summary>Both kinds of finding from one check.</summary>
public readonly record struct CellWireframeResult(
    ImmutableArray<WireframeFinding> Findings, ImmutableArray<CellWireframeFinding> CellFindings)
{
    public bool IsEmpty => Findings.IsEmpty && CellFindings.IsEmpty;
}
