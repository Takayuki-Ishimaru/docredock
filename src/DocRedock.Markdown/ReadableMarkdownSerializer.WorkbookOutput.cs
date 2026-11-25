using System.Text;
using DocRedock.Core.Documents;

namespace DocRedock.Markdown;

public enum MarkdownTableLayout { Grid, Records, Auto }

public sealed partial class ReadableMarkdownSerializer
{
    private string? workbookOutputSheet;

    private void WriteWorkbookPrintFurniture(StringBuilder output, WorkbookHeaderFooter? furniture)
    {
        if (furniture is null) return;
        var variants = new[] { ("Header", furniture.OddHeader), ("Footer", furniture.OddFooter) }.ToList();
        if (furniture.DifferentOddEven) variants.AddRange([("Even header", furniture.EvenHeader), ("Even footer", furniture.EvenFooter)]);
        if (furniture.DifferentFirst) variants.AddRange([("First header", furniture.FirstHeader), ("First footer", furniture.FirstFooter)]);
        if (!variants.Any(v => !string.IsNullOrWhiteSpace(v.Item2))) return;
        WriteHeading(output, 3, EnglishOutput ? "Print headers and footers" : "印刷ヘッダー・フッター");
        output.AppendLine(EnglishOutput ? "Print fields are retained below; page positions and numbering are applied in the native HTML print layout." :
            "印刷用の文字列を以下に保持しています。位置とページ番号はHTMLの印刷レイアウトで反映します。").AppendLine();
        foreach (var (label, text) in variants)
        {
            if (string.IsNullOrWhiteSpace(text)) continue;
            output.Append("**").Append(label).AppendLine("**").AppendLine();
            var fence = CodeFence(text);
            output.AppendLine(fence).AppendLine(text).AppendLine(fence).AppendLine();
        }
    }

    private static Dictionary<string, HashSet<string>> RepresentedDiagramCells(DocumentPartition partition)
    {
        var cells = partition.Nodes.Where(n => n.Kind == NodeKind.Cell).ToArray();
        var byId = cells.ToDictionary(n => n.Id, StringComparer.Ordinal);
        var byAddress = cells.Where(n => ExtensionString(n, "address") is not null)
            .GroupBy(n => ExtensionString(n, "address")!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var diagram in partition.Nodes.Where(n => n.Kind == NodeKind.Diagram))
        {
            var represented = new HashSet<string>(StringComparer.Ordinal);
            result[diagram.Id] = represented;
            if (!TryGetVisualGraph(diagram, out var visual) || visual is null ||
                !VisualGraphValidator.Validate(visual).IsValidForSemanticProjection) continue;
            foreach (var node in visual.Nodes ?? [])
            {
                if (node is null) continue;
                DocumentNode? cell = null;
                if (node.SourceNodeId is { } id) byId.TryGetValue(id, out cell);
                if (cell is null && node.SourceAnchor?.Locators.FirstOrDefault(l => l.Kind == "cell_address")?.Value is { } address)
                    byAddress.TryGetValue(address, out cell);
                // A row interval is not proof of ownership. Suppress only a source
                // cell whose exact text is retained as this diagram node's label.
                if (cell is not null && StringComparer.Ordinal.Equals(NodeText(cell), node.Label)) represented.Add(cell.Id);
            }
        }
        return result;
    }

    private void WriteSeparateWorksheetAnnotations(StringBuilder output, DocumentPartition partition)
    {
        var annotations = partition.Nodes.Where(node => node.Kind == NodeKind.Shape && HasExtension(node, "sheet_overlay"))
            .Select(node => (Node: node, Overlay: ReadSheetOverlay(node))).Where(item => item.Overlay is not null).ToArray();
        if (annotations.Length == 0) return;
        WriteHeading(output, 3, EnglishOutput ? "Drawing annotations" : "図形注記");
        foreach (var (node, annotation) in annotations)
        {
            var overlay = annotation!;
            var from = ColumnLetters(Math.Clamp(overlay.StartColumn, 1, 16384)) + Math.Clamp(overlay.StartRow, 1, 1048576);
            var to = ColumnLetters(Math.Clamp(overlay.EndColumn, 1, 16384)) + Math.Clamp(overlay.EndRow, 1, 1048576);
            WriteQuote(output, EscapedInlineText(from + ":" + to + " (" + overlay.Kind + ", " + overlay.Direction + ") " + (overlay.Text ?? "")));
            AddDiagnostic(new MarkdownDiagnostic("ReadableOverlayLayout",
                "Drawing annotations are shown separately with their source ranges; compare their placement with the source.", MarkdownDiagnosticSeverity.Warning, node.Id));
        }
    }

    private void WriteSeparateTableAnnotations(StringBuilder output, DocumentNode node)
    {
        var annotations = ReadTableOverlays(node);
        if (annotations.Count == 0) return;
        WriteHeading(output, 3, EnglishOutput ? "Drawing annotations" : "図形注記");
        foreach (var overlay in annotations)
        {
            var from = ColumnLetters(Math.Clamp(overlay.StartColumn + 1, 1, 16384)) + Math.Clamp(overlay.StartRow + 1, 1, 1048576);
            var to = ColumnLetters(Math.Clamp(overlay.EndColumn + 1, 1, 16384)) + Math.Clamp(overlay.EndRow + 1, 1, 1048576);
            WriteQuote(output, EscapedInlineText(from + ":" + to + " (" + overlay.Kind + ", " + overlay.Direction + ") " + (overlay.Text ?? "")));
        }
        AddDiagnostic(new("ReadableOverlayLayout", "Drawing annotations are retained separately from source cell values with their ranges.", MarkdownDiagnosticSeverity.Warning, node.Id));
    }

    private void WriteWorkbookRecords(StringBuilder output, string[] headers,
        IReadOnlyList<SheetRow> headerRows, IReadOnlyList<SheetRow> rows)
    {
        var start = output.Length;
        foreach (var row in rows)
        {
            WriteHeading(output, 3, EnglishOutput ? $"Record {row.Number}" : $"レコード {row.Number}");
            WriteWorkbookSource(output, [row]);
            for (var column = 0; column < headers.Length; column++)
            {
                var label = string.IsNullOrWhiteSpace(headers[column])
                    ? EnglishOutput ? $"Column {column + 1}" : $"列 {column + 1}"
                    : headers[column];
                var value = column < row.Cells.Count ? WorkbookCellText(row.Cells[column], table: false) : string.Empty;
                var rawValue = column < row.Cells.Count ? row.Cells[column].Display ?? row.Cells[column].Text : string.Empty;
                if (ReadableStructuredText.IsJsonExample(rawValue) || rawValue.Contains('\n') || rawValue.Contains('\r'))
                {
                    output.Append("- **").Append(EscapedInlineText(label)).AppendLine("**:").AppendLine();
                    var fence = CodeFence(rawValue);
                    output.Append("  ").Append(fence).AppendLine(ReadableStructuredText.IsJsonExample(rawValue) ? "json" : "text");
                    foreach (var line in rawValue.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
                        output.Append("  ").AppendLine(line);
                    output.Append("  ").AppendLine(fence).AppendLine();
                    continue;
                }
                var lines = EscapeLineStarts(value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')).Split('\n');
                output.Append("- **").Append(EscapedInlineText(label)).Append("**: ").AppendLine(lines[0]);
                foreach (var line in lines.Skip(1)) output.Append("  ").AppendLine(line);
            }
            output.AppendLine();
        }
        // A record fallback is a normal source segment, never a fictitious pipe table.
        sheetRecorder?.Add(start, output.Length, NodeIdsOf(headerRows.Concat(rows)));
    }

    private void WriteWorkbookSource(StringBuilder output, IReadOnlyList<SheetRow> rows)
    {
        if (!options.IncludeSourceReferences || rows.Count == 0) return;
        var cells = rows.SelectMany(row => row.Cells).Where(cell => cell.NodeId is not null).ToArray();
        if (cells.Length == 0) return;
        var minRow = rows.Min(row => row.Number);
        var maxRow = Math.Max(rows.Max(row => row.Number), cells.Max(cell => cell.MaxRow));
        var minColumn = cells.Min(cell => cell.Column);
        var maxColumn = cells.Max(cell => cell.MaxColumn);
        var range = ColumnLetters(minColumn) + minRow + ":" + ColumnLetters(maxColumn) + maxRow;
        var source = new[] { options.SourceName, workbookOutputSheet, range }.Where(value => !string.IsNullOrWhiteSpace(value));
        WriteQuote(output, (EnglishOutput ? "Source: " : "出典: ") + string.Join(" / ", source.Select(value => EscapeLiteral(value!))));
        if (!options.PortableMarkdown)
            output.Append("<!-- docredock-source ").Append(System.Text.Json.JsonSerializer.Serialize(new
            { workbook = options.SourceName, sheet = workbookOutputSheet, range, object_id = cells[0].NodeId })).AppendLine(" -->").AppendLine();
    }

    private void WriteWorkbookObjectSource(StringBuilder output, DocumentNode node, DocumentPartition partition)
    {
        if (!options.IncludeSourceReferences) return;
        var origin = ReadableSourceProvenance.ForNode(node, partition.Id, true);
        var parts = new[] { options.SourceName, origin.SheetName, origin.Range, origin.ObjectId }.OfType<string>();
        WriteQuote(output, (EnglishOutput ? "Source: " : "出典: ") + string.Join(" / ", parts.Select(EscapeLiteral)));
    }

    private string WorkbookContents(IReadOnlyList<(string Id, string Name)> entries)
    {
        if (!options.IncludeTableOfContents || entries.Count == 0) return string.Empty;
        var output = new StringBuilder();
        WriteHeading(output, 2, EnglishOutput ? "Contents" : "目次");
        foreach (var entry in entries)
            if (options.PortableMarkdown) output.Append("- ").AppendLine(EscapeLiteral(entry.Name));
            else output.Append("- [").Append(EscapeLiteral(entry.Name)).Append("](#").Append(entry.Id).AppendLine(")");
        output.AppendLine();
        return output.ToString();
    }
}
