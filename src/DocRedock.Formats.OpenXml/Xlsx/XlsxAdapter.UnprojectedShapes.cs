using System.Text;
using System.Text.Json;
using DocRedock.Core.Documents;

namespace DocRedock.Formats.OpenXml.Xlsx;

// Text that sits in worksheet shapes must never disappear from the output. When shapes are not turned into a
// diagram (or a diagram leaves some of its shapes unused), their text is still listed here, in reading order
// (top to bottom, left to right), as an unconnected-node Mermaid block that is marked as needing review.
public sealed partial class XlsxAdapter
{
    internal const string UnprojectedShapeTextType = "shape_text";

    /// <summary>Shapes that carry text and were used neither by a diagram nor by a schedule overlay.</summary>
    private static List<XlsxDrawingShapeRecord> FindUnprojectedTextShapes(
        IReadOnlyList<XlsxDrawingShapeRecord> shapes, IReadOnlySet<string> consumedShapeIds, IReadOnlySet<string> overlayShapeIds) =>
        shapes.Where(shape => !string.IsNullOrWhiteSpace(shape.Text) &&
                              !consumedShapeIds.Contains(shape.Id) && !overlayShapeIds.Contains(shape.Id))
            .ToList();

    private static DocumentNode CreateUnprojectedShapeTextNode(
        SheetInfo sheet, IReadOnlyList<XlsxDrawingShapeRecord> shapes, XlsxWorksheetMetrics? metrics, int order)
    {
        var ordered = ReadingOrder(shapes);
        var mermaid = new StringBuilder("flowchart TB\n");
        mermaid.AppendLine("    U0[\"図形内のテキスト（図として解釈できなかったもの。読み順のみ保持） / Text from shapes that could not be read as a diagram (reading order only)\"]");
        for (var index = 0; index < ordered.Count; index++)
        {
            var text = ordered[index].Text!.Trim().Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')
                .Replace("\"", "'", StringComparison.Ordinal).Replace("\n", "<br/>", StringComparison.Ordinal);
            mermaid.Append("    U").Append(index + 1).Append("[\"").Append(text).AppendLine("\"]");
        }
        var firstRow = ordered.Min(shape => shape.AbsoluteBounds is { } b && metrics is not null
            ? (int)Math.Floor(metrics.RowFromEmu(b.YEmu)) + 1
            : shape.Row + 1);
        var extensions = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["diagram_language"] = JsonSerializer.SerializeToElement("mermaid"),
            ["diagram_type"] = JsonSerializer.SerializeToElement(UnprojectedShapeTextType),
            ["diagram_source"] = JsonSerializer.SerializeToElement("xlsx-drawingml-text-fallback"),
            ["diagram_min_row"] = JsonSerializer.SerializeToElement(Math.Max(1, firstRow)),
            // No cell row is covered: the cells stay in the sheet's tables.
            ["diagram_max_row"] = JsonSerializer.SerializeToElement(Math.Max(1, firstRow) - 1),
            ["unprojected_shape_ids"] = JsonSerializer.SerializeToElement(ordered.Select(shape => shape.Id).ToArray()),
            [ReviewExtensionKeys.SourceReviewRequired] = JsonSerializer.SerializeToElement(true),
        };
        return new DocumentNode(
            "n_" + Hash(sheet.Name + "!unprojected-shape-text")[..16],
            NodeKind.Diagram,
            null,
            order,
            ContentLayer.Derived,
            new TextNodeContent(mermaid.ToString().TrimEnd()),
            new SourceAnchor("xlsx", sheet.PartUri, [new AnchorLocator("diagram_projection", "shape_text")]),
            Editability: NodeEditability.Protected,
            Provenance: [new ProvenanceItem(EvidenceKind.LayoutInferred)],
            Extensions: extensions);
    }

    // Rows of shapes whose tops are within about half a shape height of each other read left to right.
    private static List<XlsxDrawingShapeRecord> ReadingOrder(IReadOnlyList<XlsxDrawingShapeRecord> shapes)
    {
        var byTop = shapes.Select(shape => (Shape: shape, Top: shape.AbsoluteBounds?.YEmu ?? shape.Row * 190_500L,
                                            Left: shape.AbsoluteBounds?.XEmu ?? shape.Column * 609_600L,
                                            Height: Math.Max(1L, shape.AbsoluteBounds?.HeightEmu ?? shape.HeightEmu)))
            .OrderBy(item => item.Top).ThenBy(item => item.Left).ToList();
        var result = new List<XlsxDrawingShapeRecord>();
        var index = 0;
        while (index < byTop.Count)
        {
            var rowTop = byTop[index].Top;
            var rowHeight = byTop[index].Height;
            var end = index;
            while (end < byTop.Count && byTop[end].Top - rowTop <= rowHeight / 2) end++;
            result.AddRange(byTop.Skip(index).Take(end - index).OrderBy(item => item.Left).ThenBy(item => item.Top).Select(item => item.Shape));
            index = end;
        }
        return result;
    }
}
