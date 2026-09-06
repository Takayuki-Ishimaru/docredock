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
        // Cells record stable fragment ids, not list positions: `page.Regions` is re-ordered and
        // merged after inference, so indexing it by a cell's position silently deleted unrelated
        // native text. A region is dropped only when every fragment it carries is in a cell.
        var tableSourceIds = PdfTextAccounting.TableSourceIds(tables);
        // page.Regions already carries the extractor's final reading order - including any
        // detected column-major layout - as its list position (SortReadingOrder assigns
        // ReadingOrder sequentially over its own emitted order). Re-deriving order from raw Y/X
        // here would silently discard column-major ordering, since a row-major and a column-major
        // layout can otherwise share the same page-level Y/X extents. Only a table's position
        // relative to that flow is still decided geometrically, from its own bounds.
        var flow = page.Regions.Where(region => !PdfTextAccounting.IsTableOwned(region, tableSourceIds)).ToArray();
        var placements = tables.Select(table =>
        {
            // A table is inserted immediately before the first flow region that visually follows
            // it (a smaller PDF-space Y, since Y grows upward), which keeps it positioned relative
            // to the surrounding flow text without letting geometry override the flow's own order.
            var top = table.Bounds.Y + table.Bounds.Height;
            var position = Array.FindIndex(flow, region => region.BoundingBox.Y < top);
            return (Table: table, Position: position < 0 ? flow.Length : position);
        }).OrderBy(placement => placement.Position).ThenByDescending(placement => placement.Table.Bounds.Y).ToArray();

        var nodes = new List<DocumentNode>();
        var flowIndex = 0;
        void EmitFlowUpTo(int exclusiveEnd)
        {
            while (flowIndex < exclusiveEnd)
            {
                var region = flow[flowIndex++];
                var order = startingOrder + nodes.Count;
                nodes.Add(new DocumentNode($"pdf-p{page.PageNumber}-text-{order + 1}", NodeKind.Paragraph, null, order,
                    ContentLayer.Body, new TextNodeContent(region.Text), Geometry: region.BoundingBox,
                    Provenance: [new ProvenanceItem(EvidenceKind.Native, Engine: "pdf text operator")]));
            }
        }
        foreach (var (table, position) in placements)
        {
            EmitFlowUpTo(position);
            var order = startingOrder + nodes.Count;
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
        EmitFlowUpTo(flow.Length);
        return nodes;
    }
}
