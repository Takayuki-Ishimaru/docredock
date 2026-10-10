using System.Text.Json;
using DocRedock.Core.Documents;

namespace DocRedock.Formats.Pdf;

/// <summary>Projects native PDF text and reconstructed tables into independent graph nodes.
/// Text assigned to a table is removed only from the native text projection, never from the
/// extractor result, so failed downstream rendering cannot lose native text evidence.</summary>
public static class PdfPageProjection
{
    /// <summary>The "strike_ranges" extension of a line: its struck-through characters as [start, end)
    /// offsets into its text; null when nothing is struck.</summary>
    public static JsonElement? StrikeRanges(PdfTextRegion region) => region.Struck is { Count: > 0 } struck
        ? JsonSerializer.SerializeToElement(struck.Select(range => new[] { range.Start, range.End }).ToArray())
        : null;

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
                var extensions = StrikeRanges(region) is { } ranges ? new Dictionary<string, JsonElement> { ["strike_ranges"] = ranges } : null;
                nodes.Add(new DocumentNode($"pdf-p{page.PageNumber}-text-{order + 1}", NodeKind.Paragraph, null, order,
                    ContentLayer.Body, new TextNodeContent(region.Text), Geometry: region.BoundingBox,
                    Provenance: [new ProvenanceItem(EvidenceKind.Native, Engine: "pdf text operator")], Extensions: extensions));
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
            // P-Overlay: ReadableMarkdownSerializer.ApplyTableOverlays is already format-neutral --
            // any NodeKind.Table node with a non-empty "table_overlays" extension gets its cell
            // text folded with glyphs, no PDF-specific serializer change needed.
            if (table.Overlays is { Count: > 0 } overlays)
                extensions["table_overlays"] = JsonSerializer.SerializeToElement(overlays);
            // Struck-through cell text, as Word and PowerPoint tables name it: [row, cell, [[start, end], ...]].
            var struckCells = table.Rows.SelectMany((row, rowIndex) => row.Cells.OrderBy(cell => cell.Column)
                    .Select((cell, cellIndex) => (rowIndex, cellIndex, cell.Struck)))
                .Where(item => item.Struck is { Count: > 0 })
                .Select(item => new object[] { item.rowIndex, item.cellIndex, item.Struck!.Select(range => new[] { range.Start, range.End }).ToArray() })
                .ToArray();
            if (struckCells.Length > 0) extensions["strike_cells"] = JsonSerializer.SerializeToElement(struckCells);
            nodes.Add(new DocumentNode(table.Id, NodeKind.Table, null, order, ContentLayer.Body,
                new TableNodeContent(rows), Geometry: table.Bounds, Editability: NodeEditability.Protected,
                Provenance: [new ProvenanceItem(EvidenceKind.TableInferred, Engine: "pdf vector grid")], Extensions: extensions));
        }
        EmitFlowUpTo(flow.Length);
        return nodes;
    }
}
