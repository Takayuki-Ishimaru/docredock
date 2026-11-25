using System.Globalization;
using System.Text.Json;
using DocRedock.Core.Documents;
using static DocRedock.Markdown.ReadableGraphPreparation;

namespace DocRedock.Markdown;

public static partial class ReadableProjectionBuilder
{
    private sealed partial class Projection
    {
        private ReadableTable Table(DocumentNode node, TableNodeContent table, int depth)
        {
            var requestedWidth = table.Rows.Select(row => row.Sum(c => (long)Math.Clamp(c.ColSpan, 1, 16384))).DefaultIfEmpty().Max();
            var width = (int)Math.Clamp(requestedWidth, 0, 16384);
            if (requestedWidth > 16384) Add("ReadableTableIrregularGrid", "Table width exceeds the native grid bound; source cell text is retained.", node.Id);
            var coverage = new int[width];
            var rows = new List<ReadableTableRow>();
            for (var r = 0; r < table.Rows.Count; r++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var cells = new List<ReadableTableCell>(); var column = 1;
                for (var c = 0; c < table.Rows[r].Count; c++)
                {
                    var cell = table.Rows[r][c]; var sourceColumn = column;
                    var colSpan = Math.Clamp(cell.ColSpan, 1, Math.Max(1, width - column + 1)); column += colSpan;
                    var covered = sourceColumn <= coverage.Length && coverage[sourceColumn - 1] > r;
                    if (cell.RowSpan == 0 && covered && cell.Text.Length == 0) continue;
                    if (cell.RowSpan <= 0 || cell.ColSpan < 1 || cell.RowSpan > table.Rows.Count - r || cell.RowSpan != 0 && covered || colSpan != cell.ColSpan)
                        Add("ReadableTableIrregularGrid", "An invalid table merge was bounded to the surviving table.", node.Id);
                    for (var coveredColumn = sourceColumn - 1; coveredColumn < Math.Min(coverage.Length, sourceColumn - 1 + colSpan); coveredColumn++)
                        coverage[coveredColumn] = Math.Max(coverage[coveredColumn], r + Math.Clamp(cell.RowSpan, 1, table.Rows.Count - r));
                    var extensions = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                    foreach (var (source, target) in new[] { ("strike_cells", "strike_ranges"), ("underline_cells", "underline_ranges"), ("insert_cells", "insert_ranges"), ("delete_cells", "delete_ranges") })
                    {
                        var marks = Extension(node, source);
                        if (marks.ValueKind != JsonValueKind.Array) continue;
                        foreach (var mark in marks.EnumerateArray())
                            if (mark.ValueKind == JsonValueKind.Array && mark.GetArrayLength() == 3 && Number(mark[0]) == r && Number(mark[1]) == c) extensions[target] = mark[2];
                    }
                    var content = ReadableRunProjection.Content(node with
                    {
                        Content = cell.Runs is { } richRuns && string.Concat(richRuns.Select(run => run.Text)) == cell.Text
                            ? new RichTextNodeContent(richRuns) : new TextNodeContent(cell.Text),
                        Extensions = extensions,
                    });
                    var children = new List<ReadableBlock>();
                    if (depth < 24 && nested.TryGetValue(node.Id, out var nestedTables))
                    {
                        var offset = 0;
                        foreach (var child in nestedTables.Where(child => ExtensionInt(child, "nested_table_row") == r && ExtensionInt(child, "nested_table_column") == c)
                                     .OrderBy(child => ExtensionInt(child, "nested_table_line_offset") ?? ExtensionInt(child, "nested_table_paragraph_offset") ?? int.MaxValue).ThenBy(child => child.Order))
                        {
                            var line = ExtensionInt(child, "nested_table_line_offset") ?? ExtensionInt(child, "nested_table_paragraph_offset");
                            var insertion = content.PlainText.Length;
                            if (line is >= 0)
                            {
                                insertion = 0;
                                for (var l = 0; l < line && insertion < content.PlainText.Length; l++)
                                { var next = content.PlainText.IndexOf('\n', insertion); insertion = next < 0 ? content.PlainText.Length : next + 1; }
                            }
                            insertion = Math.Clamp(insertion, offset, content.PlainText.Length);
                            if (insertion > offset) children.Add(new ReadableParagraph(Slice(content, offset, insertion - offset)));
                            if (child.Content is TableNodeContent childTable) children.Add(Table(child, childTable, depth + 1));
                            offset = insertion;
                        }
                        if (children.Count > 0 && offset < content.PlainText.Length) children.Add(new ReadableParagraph(Slice(content, offset, content.PlainText.Length - offset)));
                    }
                    if (cellImages.TryGetValue(node.Id, out var images))
                    {
                        var cellItems = images.Where(image => (ExtensionInt(image,"table_image_row") ?? ExtensionInt(image,"table_cell_row")) == r &&
                            (ExtensionInt(image,"table_image_column") ?? ExtensionInt(image,"table_cell_column")) == c).InSourceOrder().ToArray();
                        if (cellItems.Length > 0)
                        {
                            // A page break that starts the cell's first paragraph is read before the cell's text.
                            var textPlaced = children.Count > 0;
                            var leading = cellItems.Where(item => item.Kind == NodeKind.PageBreak && DocumentSourceOrder.SiblingSequence(item) < DocumentSourceOrder.NoSequence).ToArray();
                            children.InsertRange(0, leading.SelectMany(Project));
                            if (!textPlaced && content.PlainText.Length > 0) children.Add(new ReadableParagraph(content));
                            foreach (var item in cellItems.Except(leading)) children.AddRange(Project(item));
                        }
                    }
                    cells.Add(new(children.Count == 0 ? content : ReadableContent.Text(""), Math.Clamp(cell.RowSpan, 1, table.Rows.Count - r), colSpan, sourceColumn)
                    { Origin = Origin(node), Children = children });
                }
                rows.Add(new(cells, r + 1));
            }
            var headerCount = Math.Clamp(ExtensionInt(node, "header_rows") ?? 1, 0, rows.Count);
            for (var r = 0; r < headerCount; r++)
                headerCount = Math.Min(rows.Count, Math.Max(headerCount, r + rows[r].Cells.Select(c => c.RowSpan).DefaultIfEmpty(1).Max()));
            return new(Enumerable.Range(1, width).Select(c => new ReadableColumn(c)).ToArray(), rows, headerCount) { Origin = Origin(node) };
        }

        private ReadableTable? WorkbookTable(DocumentPartition part)
        {
            var cells = part.Nodes.Where(n => n.Kind == NodeKind.Cell).Select(n =>
                (Node: n, Row: ExtensionInt(n, "row") ?? 0, Column: ExtensionInt(n, "column") ?? 0))
                .Where(c => c.Row is > 0 and <= 1048576 && c.Column is > 0 and <= 16384)
                .GroupBy(c => (c.Row, c.Column)).Select(g => g.Last()).ToArray();
            if (cells.Length == 0) return null;
            var sheet = layout?.Sheets.GetValueOrDefault(part.Id);
            if (sheet?.ConditionalFormatUnsupportedCount > 0)
                Add("XlsxConditionalFormattingPresentation", "Conditional colors, icons or data bars are not evaluated; compare this sheet with the source.", part.Id);
            var sourceRows = cells.Select(c => c.Row).Distinct().Order().ToArray();
            var firstColumn = cells.Min(c => c.Column); var lastColumn = cells.Max(c => Math.Clamp(ExtensionInt(c.Node, "merged_to_column") ?? c.Column, c.Column, 16384));
            var lastSourceRow = cells.Max(c => Math.Clamp(ExtensionInt(c.Node, "merged_to_row") ?? c.Row, c.Row, 1048576));
            var sourceRowCount = lastSourceRow - sourceRows[0] + 1;
            if (sourceRowCount > sourceRows.Length)
            {
                // Preserve meaningful blank rows without expanding an attacker-controlled sparse range.
                if (sourceRowCount <= Math.Min(65536, Math.Max(4096, sourceRows.Length * 4L)) &&
                    (long)sourceRowCount * (lastColumn - firstColumn + 1) <= 4_000_000)
                {
                    var hiddenRows = DocumentContentPolicyRules.Parse(options.ContentPolicy) != DocumentContentPolicy.Complete
                        ? (sheet?.HiddenRows ?? []).ToHashSet() : [];
                    sourceRows = Enumerable.Range(sourceRows[0], sourceRowCount).Where(r => !hiddenRows.Contains(r)).ToArray();
                }
                else Add("XlsxSparseRowsCompacted", "Large gaps between worksheet rows were compacted; original row numbers and merged ranges are retained.", part.Id);
            }
            var sparse = lastColumn - firstColumn >= 512 || (long)sourceRows.Length * (lastColumn - firstColumn + 1) > 4_000_000;
            var sourceColumns = sparse ? cells.Select(c => c.Column).Distinct().Order().ToArray() : Enumerable.Range(firstColumn, lastColumn - firstColumn + 1).ToArray();
            if (DocumentContentPolicyRules.Parse(options.ContentPolicy) != DocumentContentPolicy.Complete && sheet?.HiddenColumns is { Count: > 0 } hidden)
            { var hiddenSet = hidden.ToHashSet(); sourceColumns = sourceColumns.Where(c => !hiddenSet.Contains(c)).ToArray(); }
            if (sparse) Add("XlsxSparseLayoutCompacted", "A very sparse worksheet was compacted to populated columns; original coordinates are retained.", part.Id);
            var byCoordinate = cells.ToDictionary(c => (c.Row, c.Column), c => c.Node);
            if ((long)sourceRows.Length * sourceColumns.Length > 4_000_000)
            {
                Add("XlsxSparseLayoutCompacted", "The worksheet is too sparse for a rectangular grid; cells are listed with their source addresses.", part.Id);
                var records = new List<ReadableTableRow> { new([new(ReadableContent.Text(ja ? "元セル" : "Source cell")), new(ReadableContent.Text(ja ? "値" : "Value"))]) };
                var sparseRows = new List<ReadableTableRow>();
                foreach (var cell in cells.OrderBy(c => c.Row).ThenBy(c => c.Column))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var node = cell.Node; var value = ExtensionText(node, "display_value") ?? Plain(node); var expression = ExtensionText(node, "formula");
                    records.Add(new([new(ReadableContent.Text(Column(cell.Column) + cell.Row.ToString(CultureInfo.InvariantCulture))),
                        new(ReadableRunProjection.Content(node, value)) { StyleId = node.StyleId, Origin = Origin(node),
                            Formula = expression is not null || ExtensionBool(node, "is_formula") ? new(expression, value, HasExtension(node, "has_cached_value") ? ExtensionBool(node, "has_cached_value") : value.Length > 0, string.IsNullOrEmpty(expression)) : null }], cell.Row));
                    sparseRows.Add(new([records[^1].Cells[1] with { SourceColumn = cell.Column }], cell.Row));
                }
                return new([new(1, 96), new(2, 480)], records)
                {
                    Origin = new(part.Id, SheetName(part.Id)),
                    SourceGrid = new(sourceColumns.Select(c => new ReadableColumn(c)).ToArray(), sparseRows, 0),
                };
            }
            var rows = new List<ReadableTableRow>();
            // Drawing labels are annotations, never worksheet values. OverlayNotes keeps their
            // source ranges and warns about placement instead of inserting strokes/text into cells.
            var coveredUntil = new int[sourceColumns.Length];
            foreach (var sourceRow in sourceRows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var row = new List<ReadableTableCell>();
                for (var index = 0; index < sourceColumns.Length; index++)
                {
                    var sourceColumn = sourceColumns[index];
                    if (coveredUntil[index] >= sourceRow) continue;
                    if (!byCoordinate.TryGetValue((sourceRow, sourceColumn), out var node))
                    {
                        row.Add(new(ReadableContent.Text(""), SourceColumn: sourceColumn));
                        continue;
                    }
                    var endRow = Math.Clamp(ExtensionInt(node, "merged_to_row") ?? sourceRow, sourceRow, 1048576);
                    var endColumn = Math.Clamp(ExtensionInt(node, "merged_to_column") ?? sourceColumn, sourceColumn, 16384);
                    var colspan = 1;
                    while (index + colspan < sourceColumns.Length && sourceColumns[index + colspan] <= endColumn) colspan++;
                    var lastRowIndex = Array.BinarySearch(sourceRows, endRow);
                    if (lastRowIndex < 0) lastRowIndex = ~lastRowIndex - 1;
                    var rowspan = lastRowIndex - Array.BinarySearch(sourceRows, sourceRow) + 1;
                    for (var k = index; k < index + colspan; k++) coveredUntil[k] = endRow;
                    var value = ExtensionText(node, "display_value") ?? Plain(node);
                    var expression = ExtensionText(node, "formula");
                    var isFormula = expression is not null || ExtensionBool(node, "is_formula");
                    var saved = HasExtension(node, "has_cached_value") ? ExtensionBool(node, "has_cached_value") : !string.IsNullOrWhiteSpace(value);
                    var textContent = ReadableRunProjection.Content(node, value);
                    ReadableContent? sourceContent = null;
                    if (sourceSceneAvailable && Extension(node, "xlsx_source_text_decorations") is { ValueKind: JsonValueKind.Object } originalDecorations)
                    {
                        var sourceExtensions = node.Extensions!.Where(kv => kv.Key is not ("strike" or "underline" or "strike_ranges" or "underline_ranges"))
                            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
                        foreach (var original in originalDecorations.EnumerateObject()) sourceExtensions[original.Name] = original.Value;
                        sourceContent = ReadableRunProjection.Content(node with { Extensions = sourceExtensions }, value);
                    }
                    var jsonExample = !isFormula && ReadableStructuredText.IsJsonExample(value);
                    row.Add(new(jsonExample ? ReadableContent.Text("") : textContent, Math.Max(1, rowspan), colspan, sourceColumn)
                    {
                        StyleId = node.StyleId, Origin = endRow > sourceRow || endColumn > sourceColumn
                            ? Origin(node) with { Range = Column(sourceColumn) + sourceRow + ":" + Column(endColumn) + endRow } : Origin(node),
                        IsNumeric = ExtensionBool(node, "is_numeric"),
                        SourceContent = jsonExample ? null : sourceContent,
                        Formula = isFormula ? new(string.IsNullOrWhiteSpace(expression) ? null : expression, value, saved, string.IsNullOrWhiteSpace(expression)) : null,
                        Children = jsonExample ? [new ReadableCode(value, "json")] : [],
                    });
                    index += colspan - 1;
                }
                rows.Add(new(row, sourceRow, sheet?.RowHeights?.GetValueOrDefault(sourceRow) is > 0 and var height ? height * 96 / 72 : null));
            }
            var frozenRows = sheet?.FrozenRows ?? 0;
            var firstHeaderCells = cells.Where(c => c.Row == sourceRows[0] && !string.IsNullOrWhiteSpace(Plain(c.Node))).ToArray();
            // One bold section title beside prose does not name the grid's columns.
            // Promote a row only when several non-value cells support the header role.
            var inferredHeader = firstHeaderCells.Length >= 2 &&
                firstHeaderCells.Count(c => ExtensionBool(c.Node, "is_bold") || ExtensionBool(c.Node, "has_fill")) >= Math.Max(2, (firstHeaderCells.Length + 1) / 2) &&
                firstHeaderCells.All(c => !ExtensionBool(c.Node, "is_numeric") && !ExtensionBool(c.Node, "is_formula") ||
                    (ExtensionBool(c.Node, "is_bold") || ExtensionBool(c.Node, "has_fill")) &&
                    int.TryParse(Plain(c.Node), out var year) && year is >= 1900 and <= 2100);
            var headerRows = frozenRows is > 0 and <= 32 ? sourceRows.Count(r => r <= frozenRows) :
                inferredHeader ? 1 : 0;
            if (headerRows > 0)
                headerRows = Math.Max(headerRows, Math.Min(8, rows[0].Cells.Select(c => c.RowSpan).DefaultIfEmpty(1).Max()));
            var firstAddress = Column(firstColumn) + sourceRows[0].ToString(CultureInfo.InvariantCulture);
            var lastAddress = Column(lastColumn) + cells.Max(c => Math.Clamp(ExtensionInt(c.Node, "merged_to_row") ?? c.Row, c.Row, 1048576)).ToString(CultureInfo.InvariantCulture);
            return new(sourceColumns.Select(c => new ReadableColumn(c, Pixels(sheet?.ColumnWidths?.GetValueOrDefault(c) is > 0 and var w ? w : sheet?.DefaultColumnWidth ?? 8.43))).ToArray(), rows,
                Math.Clamp(headerRows, 0, rows.Count))
            { Origin = new(part.Id, SheetName(part.Id), firstAddress + ":" + lastAddress, part.Id, cells.Select(c => c.Node.Id).ToArray()) };
        }

        private static double Pixels(double width) => width < 1 ? Math.Floor(width * 12 + .5) : Math.Floor(width * 7 + 5);
        private static string Column(int number)
        { var result = ""; while (number > 0) { number--; result = (char)('A' + number % 26) + result; number /= 26; } return result; }
    }
}
