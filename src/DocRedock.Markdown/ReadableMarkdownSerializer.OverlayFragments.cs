using DocRedock.Core.Documents;

namespace DocRedock.Markdown;

public sealed partial class ReadableMarkdownSerializer
{
    internal sealed record SheetOverlayFragment(int Row, int Column, string Text, string NodeId, string ShapeId, bool LabelApplied);

    // Raw source text and Unicode strokes only: both readable writers may consume these fragments.
    // Worksheet dimensions are bounded before enumeration, including extension data supplied by API callers.
    internal static IEnumerable<SheetOverlayFragment> ProjectSheetOverlayFragments(DocumentPartition partition,
        IEnumerable<(int Row, int Column)> occupied)
    {
        foreach (var node in partition.Nodes.Where(n => n.Kind == NodeKind.Shape && HasExtension(n, "sheet_overlay")))
        {
            var overlay = ReadSheetOverlay(node);
            if (overlay is null || overlay.StartRow < 1 || overlay.StartColumn < 1 ||
                overlay.EndRow < overlay.StartRow || overlay.EndColumn < overlay.StartColumn || overlay.EndRow > 1048576 || overlay.EndColumn > 16384) continue;
            foreach (var (row, column) in OverlayPositions(overlay, occupied))
            {
                var labelSlot = row == overlay.StartRow && column == overlay.StartColumn;
                var text = OverlayFragment(overlay, OverlayGlyph(overlay, row, column), labelSlot);
                if (text.Length > 0) yield return new(row, column, text, node.Id, overlay.ShapeId, labelSlot || overlay.Text.Length == 0);
            }
        }
    }
}
