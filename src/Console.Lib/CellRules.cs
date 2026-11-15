using System.Collections.Immutable;
using DIR.Lib;

namespace Console.Lib;

/// <summary>The three cell-only rules, judged over the facts the recorder kept. Findings are in cells; tolerance is 0.</summary>
internal static class CellRules
{
    public static ImmutableArray<CellWireframeFinding> Judge(CellWireframeFrame frame, CellWireframeRules rules)
    {
        var facts = frame.Facts;
        var found = ImmutableArray.CreateBuilder<CellWireframeFinding>();
        var wf = frame.Frame;
        if (rules.HasFlag(CellWireframeRules.NothingRecorded) && frame.CellsWritten > 0
            && wf.Texts.Length == 0 && wf.Fills.Length == 0 && wf.Regions.Length == 0)
        {
            found.Add(new CellWireframeFinding(CellWireframeRules.NothingRecorded,
                $"{frame.CellsWritten} cell(s) were written but the frame recorded no text, fill or region, so nothing was judged",
                wf.Bounds));
        }

        for (var i = 0; i < facts.Length; i++)
        {
            var f = facts[i];
            var box = new RectF32(f.Bounds.X, f.Bounds.Y, f.Bounds.Width, f.Bounds.Height);

            if (rules.HasFlag(CellWireframeRules.DoubledRow) && f.IsText && f.OwnFill && f.TextLength > 0 && f.Clipped.Height > 1)
            {
                found.Add(new CellWireframeFinding(CellWireframeRules.DoubledRow,
                    $"one-line text is arranged {f.Clipped.Height} rows tall; rows other than {f.Row} are empty inside its fill", box));
            }

            if (rules.HasFlag(CellWireframeRules.VanishedFill) && f.OwnFill && !f.FillPainted)
            {
                found.Add(new CellWireframeFinding(CellWireframeRules.VanishedFill,
                    "a filled node is arranged but paints no cell", box));
            }

            if (rules.HasFlag(CellWireframeRules.CutWithRoom) && f.IsText && f.PaintedLength < f.TextLength
                && RoomToTheRight(facts, i) is { } room)
            {
                found.Add(new CellWireframeFinding(CellWireframeRules.CutWithRoom,
                    $"text is cut ({f.PaintedLength} of {f.TextLength} cells) while {room.Width} free cell(s) remain to its right in its container",
                    new RectF32(room.X, f.Row, room.Width, 1f)));
            }
        }

        return found.ToImmutable();
    }

    /// <summary>The indices of the facts enclosing fact <paramref name="index"/>: walking back, each strictly shallower fact is an ancestor.</summary>
    private static HashSet<int> AncestorsOf(ImmutableArray<CellFact> facts, int index)
    {
        var ancestors = new HashSet<int>();
        var depth = facts[index].Depth;
        for (var j = index - 1; j >= 0 && depth > 0; j--)
        {
            if (facts[j].Depth < depth)
            {
                ancestors.Add(j);
                depth = facts[j].Depth;
            }
        }

        return ancestors;
    }

    /// <summary>The free cells right of text on its row inside the nearest enclosing container, or null when there are none.</summary>
    private static Rect<int>? RoomToTheRight(ImmutableArray<CellFact> facts, int index)
    {
        var f = facts[index];
        var container = -1;
        for (var j = index - 1; j >= 0; j--)
        {
            if (facts[j].Depth < f.Depth)
            {
                container = j;
                break;
            }
        }

        if (container < 0)
        {
            return null;
        }

        var from = f.Clipped.Right;
        var to = facts[container].Clipped.Right;
        if (to <= from || f.Row < facts[container].Clipped.Y || f.Row >= facts[container].Clipped.Bottom)
        {
            return null;
        }

        // Any other arranged thing that paints in the span takes the room: text, a painted fill (a Box
        // swatch or a node's own background), or a non-text leaf such as an icon or a text input. Ancestors
        // are skipped (the container's own fill is the ground the label sits on, not a neighbour).
        var ancestors = AncestorsOf(facts, index);
        for (var j = 0; j < facts.Length; j++)
        {
            if (j == index || ancestors.Contains(j))
            {
                continue;
            }

            var other = facts[j];
            var paints = other.IsText || (other.OwnFill && other.FillPainted) || other.PaintsContent;
            if (paints && other.Clipped.Y <= f.Row && other.Clipped.Bottom > f.Row
                && other.Clipped.X < to && other.Clipped.Right > from)
            {
                return null;
            }
        }

        return new Rect<int>(from, f.Row, to - from, 1);
    }
}
