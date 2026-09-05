using System.Text.Json;
using DocRedock.Core.Documents;

namespace DocRedock.Formats.Pdf;

/// <summary>Projects native PDF text and reconstructed tables into independent graph nodes.
/// Text assigned to a table is removed only from the native text projection, never from the
/// extractor result, so failed downstream rendering cannot lose native text evidence.</summary>
public static class PdfPageProjection
{
    public static IReadOnlyList<DocumentNode> ToDocumentNodes(PdfPageText page,
        IReadOnlyList<PdfTable>? tables = null, int startingOrder = 0)
    {
        ArgumentNullException.ThrowIfNull(page);
        tables ??= [];
        var tableText = tables.SelectMany(table => table.Rows).SelectMany(row => row.Cells)
            .SelectMany(cell => cell.TextRegionIndexes).ToHashSet();
        var items = new List<(double Y, double X, bool IsTable, object Value)>();
        foreach (var table in tables) items.Add((table.Bounds.Y + table.Bounds.Height, table.Bounds.X, true, table));
        foreach (var (region, index) in page.Regions.Select((region, index) => (region, index)))
            if (!tableText.Contains(index)) items.Add((region.BoundingBox.Y, region.BoundingBox.X, false, region));

        var nodes = new List<DocumentNode>();
        foreach (var item in items.OrderByDescending(item => item.Y).ThenBy(item => item.X).ThenBy(item => item.IsTable ? 0 : 1))
        {
            var order = startingOrder + nodes.Count;
            if (item.IsTable)
            {
                var table = (PdfTable)item.Value;
                var rows = table.Rows.Select(row => (IReadOnlyList<TableCell>)row.Cells.OrderBy(cell => cell.Column)
                    .Select(cell => new TableCell(cell.Text, cell.ColumnSpan, cell.RowSpan)).ToArray()).ToArray();
                var extensions = new Dictionary<string, JsonElement>
                {
                    ["pdf_table_confidence"] = JsonSerializer.SerializeToElement(table.Confidence.ToString()),
                    ["pdf_source_path_ids"] = JsonSerializer.SerializeToElement(table.SourcePathIds)
                };
                nodes.Add(new DocumentNode(table.Id, NodeKind.Table, null, order, ContentLayer.Body,
                    new TableNodeContent(rows), Geometry: table.Bounds, Editability: NodeEditability.Protected,
                    Provenance: [new ProvenanceItem(EvidenceKind.TableInferred, Engine: "pdf vector grid")], Extensions: extensions));
            }
            else
            {
                var region = (PdfTextRegion)item.Value;
                nodes.Add(new DocumentNode($"pdf-p{page.PageNumber}-text-{order + 1}", NodeKind.Paragraph, null, order,
                    ContentLayer.Body, new TextNodeContent(region.Text), Geometry: region.BoundingBox,
                    Provenance: [new ProvenanceItem(EvidenceKind.Native, Engine: "pdf text operator")]));
            }
        }
        return nodes;
    }
}
