using System.Text.Json;
using System.Runtime.CompilerServices;
using DocRedock.Core.Documents;

namespace DocRedock.Formats.Pdf;

/// <summary>Projects native PDF text and reconstructed tables into independent graph nodes.
/// Text assigned to a table is removed only from the native text projection, never from the
/// extractor result, so failed downstream rendering cannot lose native text evidence.</summary>
public static class PdfPageProjection
{
    /// <summary>The "strike_ranges" extension of a line: its struck-through characters as [start, end)
    /// offsets into its text; null when nothing is struck.</summary>
    public static JsonElement? StrikeRanges(PdfTextRegion region)
    {
        ArgumentNullException.ThrowIfNull(region);
        return Ranges(region.Struck);
    }

    /// <summary>The "underline_ranges" extension of a line: its underlined characters, in the same form.</summary>
    public static JsonElement? UnderlineRanges(PdfTextRegion region)
    {
        ArgumentNullException.ThrowIfNull(region);
        return Ranges(region.Underlined);
    }

    /// <summary>The extensions of a paragraph that a page break cuts: "pdf_continues_next_page" on its part on the
    /// first page, and on the rest "pdf_continues_previous_page", what goes between the two parts.</summary>
    public static void AddContinuation(PdfTextRegion region, ref Dictionary<string, JsonElement>? extensions)
    {
        ArgumentNullException.ThrowIfNull(region);
        if (region.ContinuesOnNextPage)
            (extensions ??= new Dictionary<string, JsonElement>(StringComparer.Ordinal))["pdf_continues_next_page"] = JsonSerializer.SerializeToElement(true);
        if (region.ContinuesPreviousPage is { } separator)
            (extensions ??= new Dictionary<string, JsonElement>(StringComparer.Ordinal))["pdf_continues_previous_page"] = JsonSerializer.SerializeToElement(separator);
    }

    private static JsonElement? Ranges(IReadOnlyList<TextRange>? ranges) => ranges is { Count: > 0 }
        ? JsonSerializer.SerializeToElement(ranges.Select(range => new[] { range.Start, range.End }).ToArray())
        : null;

    /// <summary>The marked cells of a table as the extensions Word and PowerPoint tables name them:
    /// [row, cell, [[start, end], ...]], indexing the rows as they are written and their cells by column.</summary>
    private static object[] MarkedCells(PdfTable table, Func<PdfTableCell, IReadOnlyList<TextRange>?> ranges) =>
        table.Rows.SelectMany((row, rowIndex) => row.Cells.OrderBy(cell => cell.Column)
                .Select((cell, cellIndex) => (rowIndex, cellIndex, Ranges: ranges(cell))))
            .Where(item => item.Ranges is { Count: > 0 })
            .Select(item => new object[] { item.rowIndex, item.cellIndex, item.Ranges!.Select(range => new[] { range.Start, range.End }).ToArray() })
            .ToArray();

    // BodySize, the median size of three or more long lines, sizes headings; TextSize, that of any long line, is the size
    // a heading in another face or in bold is set at.
    private sealed record HeadingMetrics(double BodySize, string? BodyFamily, bool BodyBold, double TextSize);
    private static readonly ConditionalWeakTable<PdfPageText, HeadingMetrics> HeadingSizes = new();

    // A heading at the size of the body - Word's Japanese headings are a Gothic face at 10.5-12 points over a
    // Mincho body of 10.5 - is a short line of its own in another face or in bold, that ends no sentence.
    private const int MaxStyledHeadingLength = 40;

    public static int HeadingLevel(PdfPageText page, PdfTextRegion region)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(region);
        if (region.IsPageFurniture || region.FontSize is not > 0 || region.Text.Length is 0 or > 120 ||
            region.Text.TrimEnd().EndsWith('.')) return 0;
        var metrics = HeadingSizes.GetValue(page, static source =>
        {
            var body = source.Regions.Where(item => item.FontSize > 0 && item.Text.Length >= 20 && !item.IsPageFurniture).ToArray();
            var sizes = body.Select(item => item.FontSize!.Value).Order().ToArray();
            // The face and the weight of most of the body's text; none when no one face sets most of it.
            var family = body.GroupBy(item => item.FontFamily).OrderByDescending(group => group.Sum(item => item.Text.Length))
                .FirstOrDefault() is { Key: { } name } top && 2 * top.Sum(item => item.Text.Length) > body.Sum(item => item.Text.Length) ? name : null;
            var bold = 2 * body.Where(item => item.IsBold).Sum(item => item.Text.Length) > body.Sum(item => item.Text.Length);
            return new HeadingMetrics(sizes.Length >= 3 ? sizes[sizes.Length / 2] : double.PositiveInfinity, family, bold,
                sizes.Length >= 1 ? sizes[sizes.Length / 2] : double.PositiveInfinity);
        });
        var bodySize = metrics.BodySize;
        if (region.FontSize >= bodySize * 1.6) return 1;
        if (region.FontSize >= bodySize * 1.2) return 2;
        var text = region.Text.Trim();
        var textSize = metrics.TextSize;
        if (!double.IsFinite(textSize) || region.FontSize < textSize * 0.95 || text.Length > MaxStyledHeadingLength || text.Contains('\n') ||
            text[^1] is '。' or '．' or '.' or '、' or ',' or '：' or ':' || IsListItem(text)) return 0;
        var styled = region.IsBold && !metrics.BodyBold ||
            region.FontFamily is { } family && metrics.BodyFamily is { } bodyFamily && !StringComparer.OrdinalIgnoreCase.Equals(family, bodyFamily);
        return !styled ? 0 : region.FontSize >= textSize * 1.1 ? 2 : 3;
    }

    private static bool IsListItem(string text) =>
        text.Length > 1 && (text[0] is '・' or '•' or '-' or '*' or '●' or '■' || char.IsDigit(text[0]) && text.Length > 2 && text[1] is ')' or '）');

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
            // A right-column table belongs after the left column, even when its top is higher.
            var columns = flow.Where(region => region.ReadingColumn is not null).GroupBy(region => region.ReadingColumn!.Value)
                .Select(group => new { Column = group.Key, Left = group.Min(region => region.BoundingBox.X),
                    Right = group.Max(region => region.BoundingBox.X + region.BoundingBox.Width) }).ToArray();
            var matching = columns.Where(column => table.Bounds.X >= column.Left - 12 &&
                table.Bounds.X < column.Right && table.Bounds.X + table.Bounds.Width <= column.Right + 24).ToArray();
            if (matching is [var match])
            {
                position = Array.FindIndex(flow, region => region.ReadingColumn == match.Column && region.BoundingBox.Y < top);
                if (position < 0) position = Array.FindLastIndex(flow, region => region.ReadingColumn == match.Column) + 1;
            }
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
                Dictionary<string, JsonElement>? extensions = null;
                if (StrikeRanges(region) is { } struck) (extensions ??= [])["strike_ranges"] = struck;
                if (UnderlineRanges(region) is { } underlined) (extensions ??= [])["underline_ranges"] = underlined;
                var headingLevel = HeadingLevel(page, region);
                if (headingLevel > 0) (extensions ??= [])["heading_level"] = JsonSerializer.SerializeToElement(headingLevel);
                if (region.IsPageFurniture)
                {
                    (extensions ??= [])["pdf_page_furniture"] = JsonSerializer.SerializeToElement(true);
                    // A running header lies in the upper half of the page, a footer in the lower (as PdfDocumentGraphProjection
                    // records it), so the reference listing does not call a header a footer.
                    if ((page.CropBox ?? page.MediaBox) is { Height: > 0 } box)
                        extensions["pdf_furniture_position"] = JsonSerializer.SerializeToElement(
                            region.BoundingBox.Y > box.Y + box.Height / 2 ? "header" : "footer");
                }
                AddContinuation(region, ref extensions);
                nodes.Add(new DocumentNode($"pdf-p{page.PageNumber}-text-{order + 1}", headingLevel > 0 ? NodeKind.Heading : NodeKind.Paragraph, null, order,
                    region.IsPageFurniture ? ContentLayer.Furniture : ContentLayer.Body, new TextNodeContent(region.Text), Geometry: region.BoundingBox,
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
                ["pdf_page_rotation"] = JsonSerializer.SerializeToElement(page.Rotation),
                ["pdf_source_path_ids"] = JsonSerializer.SerializeToElement(table.SourcePathIds),
                ["pdf_table_columns"] = JsonSerializer.SerializeToElement(table.Rows.SelectMany(row => row.Cells)
                    .SelectMany(cell => new[] { cell.Bounds.X, cell.Bounds.X + cell.Bounds.Width }).Distinct().Order().ToArray())
            };
            if ((page.CropBox ?? page.MediaBox) is { } pageBox)
                extensions["pdf_page_box"] = JsonSerializer.SerializeToElement(pageBox);
            // P-Overlay: ReadableMarkdownSerializer.ApplyTableOverlays is already format-neutral --
            // any NodeKind.Table node with a non-empty "table_overlays" extension gets its cell
            // text folded with glyphs, no PDF-specific serializer change needed.
            if (table.Overlays is { Count: > 0 } overlays)
                extensions["table_overlays"] = JsonSerializer.SerializeToElement(overlays);
            // Struck-through and underlined cell text, as Word and PowerPoint tables name it.
            var struckCells = MarkedCells(table, cell => cell.Struck);
            if (struckCells.Length > 0) extensions["strike_cells"] = JsonSerializer.SerializeToElement(struckCells);
            var underlinedCells = MarkedCells(table, cell => cell.Underlined);
            if (underlinedCells.Length > 0) extensions["underline_cells"] = JsonSerializer.SerializeToElement(underlinedCells);
            nodes.Add(new DocumentNode(table.Id, NodeKind.Table, null, order, ContentLayer.Body,
                new TableNodeContent(rows), Geometry: table.Bounds, Editability: NodeEditability.Protected,
                Provenance: [new ProvenanceItem(EvidenceKind.TableInferred, Engine: "pdf vector grid")], Extensions: extensions));
        }
        EmitFlowUpTo(flow.Length);
        return nodes;
    }
}
