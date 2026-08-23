using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Rtmd.Core.Documents;

namespace Rtmd.Markdown;

/// <summary>Controls optional detail in the reader-oriented Markdown projection.</summary>
public sealed record ReadableMarkdownOptions(
    bool ShowFormulas = false,
    bool IncludeSvgPreviews = false,
    bool IncludeDiagrams = true,
    IReadOnlyList<string>? IncludedSheets = null,
    string? Title = null);

/// <summary>
/// Produces Markdown intended for reading rather than round-tripping. Unlike the
/// RTMD projection, this format deliberately omits source coordinates and RTMD
/// integrity markers.
/// </summary>
public sealed partial class ReadableMarkdownSerializer
{
    private static readonly string[] HeaderWords =
    [
        "no", "id", "項目", "名称", "内容", "概要", "説明", "状態", "日付", "担当", "結果", "備考",
        "版", "変更", "作成者", "送信元", "送信先", "方式", "処理", "入力", "出力", "timeout", "retry",
        "番号", "遷移", "イベント", "ガード", "条件", "副作用", "ルール", "証跡", "参照", "分類", "型", "必須",
        "コード", "表示", "箇所", "テーブル", "列", "null", "キー", "method", "path", "response", "header", "body",
        "topic", "producer", "consumer", "delivery", "schema", "owner", "status", "alarm", "sla", "runbook", "環境", "設定値",
        "区分", "確認日", "判定", "期待", "メッセージ", "目的", "応答", "実装", "観点", "経路", "値", "役割", "桁"
    ];

    private readonly ReadableMarkdownOptions options;

    public ReadableMarkdownSerializer(ReadableMarkdownOptions? options = null) => this.options = options ?? new();

    public string Serialize(DocumentGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        return graph.Format == DocumentFormatKind.Xlsx
            ? SerializeWorkbook(graph)
            : SerializeDocument(graph);
    }

    private string SerializeWorkbook(DocumentGraph graph)
    {
        var output = new StringBuilder();
        var partitions = graph.Partitions
            .Where(partition => IsIncludedPartition(partition.Id))
            .OrderBy(partition => partition.Order).ThenBy(partition => partition.Id, StringComparer.Ordinal).ToList();
        var title = options.Title?.Trim() is { Length: > 0 } customTitle
            ? customTitle
            : FindWorkbookTitle(partitions) ?? "ドキュメント";
        WriteHeading(output, 1, title);

        foreach (var partition in partitions)
        {
            var rows = ReadRows(partition);
            var diagrams = options.IncludeDiagrams ? ReadDiagrams(partition) : [];
            if (rows.Count == 0 && diagrams.Count == 0) continue;

            WriteHeading(output, 2, HumanizePartitionName(partition.Id));
            var hasSectionHeading = false;
            var index = 0;
            foreach (var diagram in diagrams)
            {
                var next = index;
                while (next < rows.Count && rows[next].Number < diagram.MinRow) next++;
                RenderWorkbookRows(output, rows[index..next]
                    .Where(row => !IsRedundantTitle(row, title, partition.Id)).ToArray(), ref hasSectionHeading);
                WriteMermaid(output, diagram.Mermaid);
                while (next < rows.Count && rows[next].Number <= diagram.MaxRow) next++;
                index = next;
            }
            RenderWorkbookRows(output, rows[index..]
                .Where(row => !IsRedundantTitle(row, title, partition.Id)).ToArray(), ref hasSectionHeading);
            RenderPartitionMedia(output, partition);
        }

        return Finish(output);
    }

    private static void RenderWorkbookRows(StringBuilder output, IReadOnlyList<SheetRow> rows, ref bool hasSectionHeading)
    {
        if (rows.Count == 0) return;
        var regions = BuildRegions(rows);
        if (!hasSectionHeading && regions.Any(region => region.MinRow <= 15 && LooksLikeKeyValueGroup(region.Rows)))
        {
            WriteInference(output, "セル配置から文書情報セクションを推定");
            WriteHeading(output, 3, "文書情報");
            hasSectionHeading = true;
        }
        foreach (var region in regions)
        {
            RenderRowGroup(output, region.Rows);
            hasSectionHeading |= region.Rows.SelectMany(row => row.Cells)
                .Any(cell => !cell.IsNumeric && TryGetSectionHeading(cell.Text, out _, out _));
        }
    }

    private bool IsIncludedPartition(string partitionId)
    {
        if (options.IncludedSheets is not { Count: > 0 }) return true;
        var humanized = HumanizePartitionName(partitionId);
        return options.IncludedSheets.Any(sheet =>
            StringComparer.OrdinalIgnoreCase.Equals(sheet.Trim(), partitionId) ||
            StringComparer.OrdinalIgnoreCase.Equals(sheet.Trim(), humanized));
    }

    private static string SerializeDocument(DocumentGraph graph)
    {
        var output = new StringBuilder();
        var wroteTitle = false;
        var previousWasListItem = false;
        foreach (var partition in graph.Partitions.OrderBy(partition => partition.Order).ThenBy(partition => partition.Id, StringComparer.Ordinal))
        {
            foreach (var node in partition.Nodes.OrderBy(node => node.Order).ThenBy(node => node.Id, StringComparer.Ordinal))
            {
                var text = NodeText(node).Trim();
                if (string.IsNullOrWhiteSpace(text)) continue;
                var isListItem = node.Kind is NodeKind.List or NodeKind.ListItem;
                if (previousWasListItem && !isListItem) output.AppendLine();
                switch (node.Kind)
                {
                    case NodeKind.Heading:
                        WriteHeading(output, ExtensionInt(node, "heading_level") ?? (wroteTitle ? 2 : 1), text);
                        wroteTitle = true;
                        break;
                    case NodeKind.Section:
                    case NodeKind.Slide:
                    case NodeKind.Page:
                        WriteHeading(output, wroteTitle ? 2 : 1, text);
                        wroteTitle = true;
                        break;
                    case NodeKind.Quote:
                    case NodeKind.Comment:
                    case NodeKind.Annotation:
                        WriteQuote(output, text);
                        break;
                    case NodeKind.CodeBlock:
                        output.AppendLine("```").AppendLine(text).AppendLine("```").AppendLine();
                        break;
                    case NodeKind.List:
                    case NodeKind.ListItem:
                        output.Append(' ', Math.Max(0, ExtensionInt(node, "list_level") ?? 0) * 2)
                            .Append("- ").AppendLine(InlineText(text));
                        break;
                    case NodeKind.Table when node.Content is TableNodeContent table:
                        WriteArbitraryTable(output, table.Rows);
                        break;
                    case NodeKind.Image when node.Content is ReferenceNodeContent image:
                        WriteImage(output, image);
                        break;
                    case NodeKind.ImageText:
                        WriteQuote(output, "OCR抽出テキスト:\n" + text);
                        break;
                    case NodeKind.Shape when HasExtension(node, "paragraph_details"):
                        if (StringComparer.OrdinalIgnoreCase.Equals(ExtensionString(node, "shape_role"), "title"))
                        {
                            WriteHeading(output, wroteTitle ? 2 : 1, text);
                            wroteTitle = true;
                        }
                        else
                        {
                            WritePptxParagraphs(output, node);
                        }
                        break;
                    case NodeKind.Chart:
                    case NodeKind.Diagram:
                        WriteQuote(output, $"図: {text}");
                        break;
                    default:
                        if (!wroteTitle && text.Length <= 100)
                        {
                            WriteHeading(output, 1, text);
                            wroteTitle = true;
                        }
                        else
                        {
                            WriteParagraph(output, text);
                        }
                        break;
                }
                previousWasListItem = isListItem;
            }
            if (previousWasListItem) output.AppendLine();
            previousWasListItem = false;
        }

        if (output.Length == 0) WriteHeading(output, 1, "ドキュメント");
        return Finish(output);
    }

    private static void RenderPartitionMedia(StringBuilder output, DocumentPartition partition)
    {
        var images = partition.Nodes.Where(node => node.Kind == NodeKind.Image && node.Content is ReferenceNodeContent)
            .OrderBy(node => node.Order).ThenBy(node => node.Id, StringComparer.Ordinal).ToArray();
        var imageText = partition.Nodes.Where(node => node.Kind == NodeKind.ImageText)
            .OrderBy(node => node.Order).ThenBy(node => node.Id, StringComparer.Ordinal).ToArray();
        if (images.Length == 0 && imageText.Length == 0) return;

        WriteHeading(output, 3, "埋め込み画像");
        var renderedTextIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var imageNode in images)
        {
            WriteImage(output, (ReferenceNodeContent)imageNode.Content);
            foreach (var textNode in imageText.Where(node => StringComparer.Ordinal.Equals(node.ParentId, imageNode.Id)))
            {
                WriteQuote(output, "OCR抽出テキスト:\n" + NodeText(textNode).Trim());
                renderedTextIds.Add(textNode.Id);
            }
        }
        foreach (var textNode in imageText.Where(node => !renderedTextIds.Contains(node.Id)))
            WriteQuote(output, "OCR抽出テキスト:\n" + NodeText(textNode).Trim());
    }

    private static void WriteImage(StringBuilder output, ReferenceNodeContent image)
    {
        var alt = (image.AltText ?? "図").Replace("[", "\\[", StringComparison.Ordinal)
            .Replace("]", "\\]", StringComparison.Ordinal);
        var target = image.Reference.Contains(' ') ? $"<{image.Reference}>" : image.Reference;
        output.Append("![").Append(alt).Append("](").Append(target).AppendLine(")").AppendLine();
    }

    private static void WritePptxParagraphs(StringBuilder output, DocumentNode node)
    {
        if (node.Extensions is null || !node.Extensions.TryGetValue("paragraph_details", out var details) ||
            details.ValueKind != JsonValueKind.Array)
        {
            WriteParagraph(output, NodeText(node));
            return;
        }

        var wroteBullet = false;
        foreach (var paragraph in details.EnumerateArray())
        {
            var text = RichParagraphText(paragraph);
            if (string.IsNullOrWhiteSpace(text)) continue;
            var isBullet = JsonBool(paragraph, "IsBullet", "isBullet", "is_bullet");
            var level = JsonInt(paragraph, "Level", "level") ?? 0;
            if (isBullet)
            {
                output.Append(' ', Math.Max(0, level) * 2).Append("- ").AppendLine(text);
                wroteBullet = true;
            }
            else
            {
                if (wroteBullet) output.AppendLine();
                WriteParagraph(output, text);
                wroteBullet = false;
            }
        }
        if (wroteBullet) output.AppendLine();
    }

    private static string RichParagraphText(JsonElement paragraph)
    {
        if (TryProperty(paragraph, out var runs, "Runs", "runs") && runs.ValueKind == JsonValueKind.Array)
        {
            var output = new StringBuilder();
            foreach (var run in runs.EnumerateArray())
            {
                var value = JsonString(run, "Text", "text") ?? string.Empty;
                if (value.Length == 0) continue;
                var bold = JsonBool(run, "Bold", "bold");
                var italic = JsonBool(run, "Italic", "italic");
                var underline = JsonBool(run, "Underline", "underline");
                value = InlineText(value);
                if (underline) value = "<u>" + value + "</u>";
                if (bold && italic) value = "***" + value + "***";
                else if (bold) value = "**" + value + "**";
                else if (italic) value = "_" + value + "_";
                output.Append(value);
            }
            if (output.Length > 0) return output.ToString();
        }
        return InlineText(JsonString(paragraph, "Text", "text") ?? string.Empty);
    }

    private static bool HasExtension(DocumentNode node, string key) => node.Extensions?.ContainsKey(key) == true;

    private static bool TryProperty(JsonElement element, out JsonElement value, params string[] names)
    {
        foreach (var name in names)
            if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out value)) return true;
        value = default;
        return false;
    }

    private static string? JsonString(JsonElement element, params string[] names) =>
        TryProperty(element, out var value, names) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool JsonBool(JsonElement element, params string[] names) =>
        TryProperty(element, out var value, names) && value.ValueKind is JsonValueKind.True or JsonValueKind.False && value.GetBoolean();

    private static int? JsonInt(JsonElement element, params string[] names) =>
        TryProperty(element, out var value, names) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;

    private List<SheetRow> ReadRows(DocumentPartition partition)
    {
        var cells = partition.Nodes
            .Where(node => node.Kind == NodeKind.Cell)
            .Select(ToCell)
            .Where(cell => cell is not null && !string.IsNullOrWhiteSpace(cell.Text))
            .Cast<ReadableCell>()
            .OrderBy(cell => cell.Row)
            .ThenBy(cell => cell.Column)
            .ToList();

        return cells.GroupBy(cell => cell.Row)
            .Select(group => new SheetRow(group.Key, group.ToList()))
            .OrderBy(row => row.Number)
            .ToList();
    }

    private static List<ReadableDiagram> ReadDiagrams(DocumentPartition partition) => partition.Nodes
        .Where(node => node.Kind == NodeKind.Diagram &&
                       StringComparer.OrdinalIgnoreCase.Equals(ExtensionString(node, "diagram_language"), "mermaid"))
        .Select(node => new ReadableDiagram(
            ExtensionInt(node, "diagram_min_row") ?? int.MaxValue,
            ExtensionInt(node, "diagram_max_row") ?? int.MaxValue,
            NodeText(node).Trim()))
        .Where(diagram => !string.IsNullOrWhiteSpace(diagram.Mermaid))
        .OrderBy(diagram => diagram.MinRow)
        .ToList();

    private ReadableCell? ToCell(DocumentNode node)
    {
        var row = ExtensionInt(node, "row");
        var column = ExtensionInt(node, "column");
        if (row is null || column is null)
        {
            var address = node.Source?.Locators.FirstOrDefault(locator =>
                StringComparer.OrdinalIgnoreCase.Equals(locator.Kind, "cell_address"))?.Value;
            if (!TryParseAddress(address, out var parsedRow, out var parsedColumn)) return null;
            row ??= parsedRow;
            column ??= parsedColumn;
        }

        var formula = ExtensionString(node, "formula");
        var value = (ExtensionString(node, "display_value") ?? NodeText(node)).Trim();
        var text = options.ShowFormulas && !string.IsNullOrWhiteSpace(formula)
            ? string.IsNullOrWhiteSpace(value) ? $"`={formula!.TrimStart('=')}`" : $"`={formula!.TrimStart('=')}` → {value}"
            : value;
        return new ReadableCell(row.Value, column.Value, text, !string.IsNullOrWhiteSpace(formula),
            ExtensionBool(node, "is_numeric") || StringComparer.Ordinal.Equals(ExtensionString(node, "cell_type"), "n"),
            ExtensionBool(node, "is_bold"), ExtensionBool(node, "has_fill"), ExtensionBool(node, "has_border"),
            ExtensionBool(node, "is_centered"), ExtensionDouble(node, "font_size"),
            ExtensionInt(node, "merged_to_column") ?? column.Value);
    }

    private static void RenderRowGroup(StringBuilder output, IReadOnlyList<SheetRow> rows)
    {
        var leadingCells = rows[0].Cells.Where(cell => !string.IsNullOrWhiteSpace(cell.Text)).ToArray();
        if (leadingCells.Length == 1 && !leadingCells[0].IsNumeric &&
            TryGetSectionHeading(leadingCells[0].Text, out var leadingHeading, out var leadingLevel))
        {
            WriteHeading(output, leadingLevel, leadingHeading);
            if (rows.Count > 1) RenderRowGroup(output, rows.Skip(1).ToArray());
            return;
        }
        var width = rows[0].Cells.Count;
        if (rows.Count == 1 && width > 1 && rows[0].Cells.All(cell => TryGetSectionHeading(cell.Text, out _, out _)))
        {
            foreach (var cell in rows[0].Cells)
            {
                _ = TryGetSectionHeading(cell.Text, out var heading, out var level);
                WriteHeading(output, level, heading);
            }
            return;
        }

        if (rows.Count >= 2 && IsHeaderRow(rows[0]))
        {
            WriteTable(output, rows[0].Cells.Select(cell => cell.Text).ToArray(),
                rows.Skip(1).Select(row => row.Cells.Select(cell => cell.Text).ToArray()));
            return;
        }

        if (LooksLikeKeyValueGroup(rows))
        {
            var headers = Enumerable.Range(0, width / 2).SelectMany(_ => new[] { "項目", "内容" }).ToArray();
            WriteInference(output, "キー・値の配置から表見出しを補完");
            WriteTable(output, headers, rows.Select(row => row.Cells.Select(cell => cell.Text).ToArray()));
            return;
        }

        if (rows.Count >= 2 && width is >= 2 and <= 8)
        {
            if (FirstColumnLooksLikeData(rows))
                WriteTable(output, Enumerable.Repeat(string.Empty, width).ToArray(), rows.Select(row => row.Cells.Select(cell => cell.Text).ToArray()));
            else
                WriteTable(output, rows[0].Cells.Select(cell => cell.Text).ToArray(), rows.Skip(1).Select(row => row.Cells.Select(cell => cell.Text).ToArray()));
            return;
        }

        foreach (var row in rows) RenderStandaloneRow(output, row);
    }

    private static bool FirstColumnLooksLikeData(IReadOnlyList<SheetRow> rows) =>
        rows.Count > 0 && rows.All(row => row.Cells.Count > 0 &&
            (row.Cells[0].IsNumeric || NumberCellRegex().IsMatch(PlainText(row.Cells[0].Text)) || IdentifierValueRegex().IsMatch(PlainText(row.Cells[0].Text))));

    private static bool HasContentBeforeNextBoundary(IReadOnlyList<SheetRow> rows, int start, int diagramRow)
    {
        for (var index = start; index < rows.Count && rows[index].Number < diagramRow; index++)
            if (!TryGetHeading(rows[index], out _, out _)) return true;
        return false;
    }

    /// <summary>
    /// Splits a section into spatially distinct rectangular regions. A row with a
    /// wide empty band and at least two cells on both sides is treated as two
    /// regions; vertically adjacent fragments are then joined by their column band.
    /// This preserves blank values inside a table while keeping side-by-side tables
    /// independent.
    /// </summary>
    private static IReadOnlyList<SheetRegion> BuildRegions(IReadOnlyList<SheetRow> rows)
    {
        var regions = new List<MutableRegion>();
        foreach (var fragment in rows.SelectMany(SplitRow).OrderBy(fragment => fragment.Row.Number).ThenBy(fragment => fragment.MinColumn))
        {
            var startsSection = fragment.Cells.Count == 1 && !fragment.Cells[0].IsNumeric &&
                                TryGetSectionHeading(fragment.Cells[0].Text, out _, out _);
            var matching = startsSection ? null : regions
                    .Where(region => (region.HasSectionHeading
                                         ? region.MaxColumn - region.MinColumn >= 3
                                         : fragment.Cells.Count > 1) &&
                                     fragment.Row.Number - region.MaxRow <= (region.HasSectionHeading ? 12 : 4) &&
                                     fragment.Row.Number >= region.MinRow &&
                                     BandsOverlap(region.MinColumn, region.MaxColumn, fragment.MinColumn, fragment.MaxColumn))
                    .OrderByDescending(region => region.MaxRow)
                    .ThenBy(region => Math.Abs(region.MinColumn - fragment.MinColumn))
                    .FirstOrDefault();
            if (matching is null)
            {
                matching = new MutableRegion();
                regions.Add(matching);
            }
            matching.Add(fragment.Row.Number, fragment.Cells);
        }
        return regions.Select(region => region.Freeze()).OrderBy(region => region.MinRow).ThenBy(region => region.MinColumn).ToArray();
    }

    private static IEnumerable<RowFragment> SplitRow(SheetRow row)
    {
        // Keep the common two-pair metadata row together.  Wider even-column
        // rows may be two independent tables and must still be split by space.
        if (row.Cells.Count < 2 || (row.Cells.Count <= 4 && LooksLikeKeyValueRow(row.Cells)))
        {
            yield return new(row, row.Cells);
            yield break;
        }
        var splits = Enumerable.Range(1, row.Cells.Count - 1)
            .Where(index => row.Cells[index].Column - row.Cells[index - 1].MaxColumn >= 2)
            .ToArray();
        if (splits.Length == 0)
        {
            yield return new(row, row.Cells);
            yield break;
        }
        var start = 0;
        foreach (var split in splits.Append(row.Cells.Count))
        {
            yield return new(row, row.Cells.Skip(start).Take(split - start).ToArray());
            start = split;
        }
    }

    private static bool LooksLikeKeyValueRow(IReadOnlyList<ReadableCell> cells) =>
        cells.Count >= 4 && cells.Count % 2 == 0 && Enumerable.Range(0, cells.Count / 2).All(index => LooksLikeLabel(cells[index * 2]));

    private static bool BandsOverlap(int leftStart, int leftEnd, int rightStart, int rightEnd) =>
        leftStart <= rightEnd + 1 && rightStart <= leftEnd + 1;

    private static void RenderStandaloneRow(StringBuilder output, SheetRow row)
    {
        var meaningfulCells = row.Cells.Where(cell => !string.IsNullOrWhiteSpace(cell.Text)).ToArray();
        if (meaningfulCells.Length == 0) return;
        if (meaningfulCells.Length == 1)
        {
            var cell = meaningfulCells[0];
            var text = cell.Text;
            if (!cell.IsNumeric && TryGetSectionHeading(text, out var heading, out var level)) WriteHeading(output, level, heading);
            else if (NoteRegex().IsMatch(text)) WriteQuote(output, text);
            else if (LooksLikeCode(text)) WriteCodeBlock(output, text);
            else WriteParagraph(output, text);
            return;
        }

        var sectionCells = meaningfulCells.Where(cell => TryGetSectionHeading(cell.Text, out _, out _)).ToList();
        if (sectionCells.Count > 0)
        {
            var ordinaryCells = meaningfulCells.Except(sectionCells).ToList();
            if (ordinaryCells.Count > 0)
                output.Append("- ").AppendLine(string.Join(" — ", ordinaryCells.Select(cell => InlineText(cell.Text)))).AppendLine();
            foreach (var cell in sectionCells)
            {
                _ = TryGetSectionHeading(cell.Text, out var heading, out var level);
                WriteHeading(output, level, heading);
            }
            return;
        }

        if (meaningfulCells.All(cell => SelfLabeledRegex().IsMatch(cell.Text)))
        {
            WriteQuote(output, string.Join(" · ", meaningfulCells.Select(cell => cell.Text)));
            return;
        }

        output.Append("- ").AppendLine(string.Join(" — ", meaningfulCells.Select(cell => InlineText(cell.Text)))).AppendLine();
    }

    private static bool LooksLikeKeyValueGroup(IReadOnlyList<SheetRow> rows)
    {
        var width = rows[0].Cells.Count;
        if (width < 2 || width > 8 || width % 2 != 0 || rows.Any(row => row.Cells.Count != width)) return false;
        if (rows.Count == 1 && width < 4) return false;
        return rows.All(row => Enumerable.Range(0, width / 2).All(index => LooksLikeLabel(row.Cells[index * 2])));
    }

    private static bool LooksLikeLabel(ReadableCell cell)
    {
        var text = PlainText(cell.Text);
        if (cell.IsFormula || text.Length is < 1 or > 28 || text.Contains('。') || text.Contains("http", StringComparison.OrdinalIgnoreCase)) return false;
        if (IdentifierValueRegex().IsMatch(text) || DateValueRegex().IsMatch(text) || UppercaseValueRegex().IsMatch(text) || text is "—" or "-" or "○") return false;
        if (decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out _)) return false;
        return text.Count(character => character == ' ') <= 3 && !text.Contains('\n');
    }

    private static bool IsHeaderRow(SheetRow row)
    {
        if (row.Cells.Count < 2) return false;
        if (row.Cells.Count(cell => cell.IsHeaderStyled) >= Math.Max(1, row.Cells.Count / 2)) return true;
        var matches = row.Cells.Count(cell => HeaderWords.Any(word => PlainText(cell.Text).Contains(word, StringComparison.OrdinalIgnoreCase)));
        return matches >= Math.Max(2, (int)Math.Ceiling(row.Cells.Count * 0.5));
    }

    private static bool TryGetHeading(SheetRow row, out string heading, out int level)
    {
        heading = string.Empty;
        level = 3;
        if (row.Cells.Count != 1) return false;
        var cell = row.Cells[0];
        var text = PlainText(cell.Text).Trim();
        if (cell.IsNumeric) return false;
        if (!TryGetSectionHeading(text, out heading, out level)) return false;
        return cell.IsHeaderStyled || SectionHeadingRegex().IsMatch(text);
    }

    private static bool TryGetSectionHeading(string value, out string heading, out int level)
    {
        heading = PlainText(value).Trim();
        level = 3;
        if (heading.Length is < 2 or > 110 || heading.Contains('。') || heading.Contains("http", StringComparison.OrdinalIgnoreCase)) return false;
        if (!SectionHeadingRegex().IsMatch(heading)) return false;
        var number = HeadingNumberRegex().Match(heading).Groups[1].Value;
        level = number.Count(character => character == '.') >= 2 ? 4 : 3;
        return true;
    }

    private static bool IsRedundantTitle(SheetRow row, string documentTitle, string partitionId)
    {
        if (row.Cells.Count != 1) return false;
        var text = NormalizeComparison(PlainText(row.Cells[0].Text));
        if (text.Length == 0) return false;
        var title = NormalizeComparison(documentTitle);
        var partition = NormalizeComparison(HumanizePartitionName(partitionId));
        return StringComparer.OrdinalIgnoreCase.Equals(text, title) ||
               StringComparer.OrdinalIgnoreCase.Equals(text, partition);
    }

    private string? FindWorkbookTitle(IEnumerable<DocumentPartition> partitions)
    {
        foreach (var partition in partitions)
        {
            var cell = ReadRows(partition).SelectMany(row => row.Cells).FirstOrDefault();
            if (cell is not null && PlainText(cell.Text).Length <= 140) return PlainText(cell.Text);
        }
        return null;
    }

    private static void WriteArbitraryTable(StringBuilder output, IReadOnlyList<IReadOnlyList<string>> rows)
    {
        if (rows.Count == 0) return;
        var width = rows.Max(row => row.Count);
        var headers = rows[0].Count == width ? rows[0] : Enumerable.Range(1, width).Select(index => $"内容{index}").ToArray();
        var data = rows[0].Count == width ? rows.Skip(1) : rows;
        WriteTable(output, headers, data);
    }

    private static void WriteTable(StringBuilder output, IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string>> rows)
    {
        WriteTableRow(output, headers);
        WriteTableRow(output, headers.Select(_ => "---").ToArray());
        foreach (var row in rows)
        {
            var values = Enumerable.Range(0, headers.Count).Select(index => index < row.Count ? row[index] : string.Empty).ToArray();
            WriteTableRow(output, values);
        }
        output.AppendLine();
    }

    private static void WriteTableRow(StringBuilder output, IEnumerable<string> cells) =>
        output.Append("| ").Append(string.Join(" | ", cells.Select(TableText))).AppendLine(" |");

    private static void WriteHeading(StringBuilder output, int level, string text)
    {
        output.Append('#', Math.Clamp(level, 1, 6)).Append(' ').AppendLine(InlineText(text)).AppendLine();
    }

    private static void WriteParagraph(StringBuilder output, string text)
    {
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal)
            .Trim().Replace("\n", "  \n", StringComparison.Ordinal);
        output.AppendLine(EscapeParagraphStart(normalized)).AppendLine();
    }

    private static string EscapeParagraphStart(string text) => Regex.Replace(text, @"^(\s*)([#>*+-]|\d+[.)])(?=\s)", "$1\\$2");

    private static bool LooksLikeCode(string text)
    {
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal).TrimStart();
        return normalized.Contains('\n') && (normalized.StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("GET ", StringComparison.OrdinalIgnoreCase) || normalized.StartsWith("POST ", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith('{') || normalized.StartsWith('[') || text.Split('\n').Any(line => line.StartsWith(' ') || line.StartsWith('\t')));
    }

    private static void WriteCodeBlock(StringBuilder output, string text) =>
        output.AppendLine("```").AppendLine(text.Trim()).AppendLine("```").AppendLine();

    private static void WriteInference(StringBuilder output, string message) =>
        output.Append("<!-- inferred: ").Append(message.Replace("--", "—", StringComparison.Ordinal)).AppendLine(" -->");

    private static void WriteQuote(StringBuilder output, string text)
    {
        foreach (var line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal).Split('\n'))
            output.Append("> ").AppendLine(line.Trim());
        output.AppendLine();
    }

    private void WriteMermaid(StringBuilder output, string mermaid)
    {
        if (options.IncludeSvgPreviews && MermaidSvgPreviewRenderer.Render(mermaid) is { } svg)
        {
            output.AppendLine(svg).AppendLine();
        }
        output.AppendLine("```mermaid").AppendLine(mermaid.Trim()).AppendLine("```").AppendLine();
    }

    private static string NodeText(DocumentNode node) => node.Content switch
    {
        TextNodeContent text => text.Text,
        RichTextNodeContent rich => RtmdInlineMarkdown.Serialize(rich.Runs),
        ReferenceNodeContent reference => reference.AltText ?? reference.Reference,
        TableNodeContent table => string.Join("\n", table.Rows.Select(row => string.Join(" | ", row))),
        _ => string.Empty
    };

    private static string HumanizePartitionName(string id)
    {
        var value = id.Trim();
        foreach (var prefix in new[] { "worksheet-", "sheet-", "partition-" })
            if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) value = value[prefix.Length..];
        return value.Replace('_', ' ').Trim() is { Length: > 0 } result ? result : "シート";
    }

    private static string PlainText(string value) => value.Replace("`", string.Empty, StringComparison.Ordinal);
    private static string InlineText(string value) => value.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal)
        .Replace("#", "\\#", StringComparison.Ordinal).Trim();
    private static string TableText(string value) => value.Replace("\r", string.Empty, StringComparison.Ordinal)
        .Replace("\n", "<br>", StringComparison.Ordinal).Replace("|", "\\|", StringComparison.Ordinal).Trim();
    private static string NormalizeComparison(string value) => Regex.Replace(value, "[\\s_\\-—:：.。/\\\\]", string.Empty);
    private static string Finish(StringBuilder output) => output.ToString().TrimEnd() + "\n";

    private static int? ExtensionInt(DocumentNode node, string key)
    {
        if (node.Extensions is null || !node.Extensions.TryGetValue(key, out var element)) return null;
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var number)) return number;
        return int.TryParse(element.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number) ? number : null;
    }

    private static string? ExtensionString(DocumentNode node, string key)
    {
        if (node.Extensions is null || !node.Extensions.TryGetValue(key, out var element)) return null;
        return element.ValueKind == JsonValueKind.String ? element.GetString() : element.ToString();
    }

    private static bool ExtensionBool(DocumentNode node, string key) =>
        node.Extensions is not null && node.Extensions.TryGetValue(key, out var element) &&
        ((element.ValueKind is JsonValueKind.True or JsonValueKind.False && element.GetBoolean()) || bool.TryParse(element.ToString(), out var value) && value);

    private static double? ExtensionDouble(DocumentNode node, string key)
    {
        if (node.Extensions is null || !node.Extensions.TryGetValue(key, out var element)) return null;
        return element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var value) ? value :
            double.TryParse(element.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) ? value : null;
    }

    private static bool TryParseAddress(string? address, out int row, out int column)
    {
        row = 0;
        column = 0;
        if (string.IsNullOrWhiteSpace(address)) return false;
        var match = CellAddressRegex().Match(address.Replace("$", string.Empty, StringComparison.Ordinal));
        if (!match.Success || !int.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out row)) return false;
        foreach (var character in match.Groups[1].Value.ToUpperInvariant()) column = checked(column * 26 + character - 'A' + 1);
        return column > 0;
    }

    private sealed record ReadableCell(int Row, int Column, string Text, bool IsFormula, bool IsNumeric, bool IsBold, bool HasFill, bool HasBorder, bool IsCentered, double? FontSize, int MaxColumn)
    {
        public bool IsHeaderStyled => IsBold || HasFill || HasBorder || IsCentered || FontSize is >= 12;
    }
    private sealed record SheetRow(int Number, IReadOnlyList<ReadableCell> Cells);
    private sealed record RowFragment(SheetRow Row, IReadOnlyList<ReadableCell> Cells)
    {
        public int MinColumn => Cells.Min(cell => cell.Column);
        public int MaxColumn => Cells.Max(cell => cell.MaxColumn);
    }
    private sealed record SheetRegion(int MinRow, int MinColumn, IReadOnlyList<SheetRow> Rows);
    private sealed class MutableRegion
    {
        private readonly SortedDictionary<int, List<ReadableCell>> cellsByRow = [];
        public int MinRow { get; private set; } = int.MaxValue;
        public int MaxRow { get; private set; }
        public int MinColumn { get; private set; } = int.MaxValue;
        public int MaxColumn { get; private set; }
        public bool HasSectionHeading { get; private set; }
        public void Add(int row, IReadOnlyList<ReadableCell> cells)
        {
            if (!cellsByRow.TryGetValue(row, out var values)) cellsByRow[row] = values = [];
            values.AddRange(cells);
            MinRow = Math.Min(MinRow, row); MaxRow = Math.Max(MaxRow, row);
            MinColumn = Math.Min(MinColumn, cells.Min(cell => cell.Column)); MaxColumn = Math.Max(MaxColumn, cells.Max(cell => cell.MaxColumn));
            HasSectionHeading |= cells.Any(cell => !cell.IsNumeric && TryGetSectionHeading(cell.Text, out _, out _));
        }
        public SheetRegion Freeze()
        {
            var columns = cellsByRow.Values.SelectMany(cells => cells).Select(cell => cell.Column).Distinct().OrderBy(column => column).ToArray();
            var rows = cellsByRow.Select(entry =>
            {
                var lookup = entry.Value.GroupBy(cell => cell.Column).ToDictionary(group => group.Key, group => group.First());
                var values = columns.Select(column => lookup.TryGetValue(column, out var cell) ? cell : new ReadableCell(entry.Key, column, string.Empty, false, false, false, false, false, false, null, column)).ToArray();
                return new SheetRow(entry.Key, values);
            }).ToArray();
            return new SheetRegion(MinRow, MinColumn, rows);
        }
    }
    private sealed record ReadableDiagram(int MinRow, int MaxRow, string Mermaid);

    [GeneratedRegex(@"^\d{1,3}$", RegexOptions.CultureInvariant)]
    private static partial Regex NumberCellRegex();

    [GeneratedRegex(@"^(?:第?\d+(?:\.\d+)*(?:[.．]\s+|\s+)|\d{1,2}[）)]\s*)\S+", RegexOptions.CultureInvariant)]
    private static partial Regex SectionHeadingRegex();

    [GeneratedRegex(@"^(\d+(?:\.\d+)*)", RegexOptions.CultureInvariant)]
    private static partial Regex HeadingNumberRegex();

    [GeneratedRegex(@"^(?:注(?:記|意)?|重要|補足|備考|ADR|制約|前提)[:：\s]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NoteRegex();

    [GeneratedRegex(@"^[^:：]{1,24}[:：]\s*\S+", RegexOptions.CultureInvariant)]
    private static partial Regex SelfLabeledRegex();

    [GeneratedRegex(@"^([A-Za-z]+)(\d+)$", RegexOptions.CultureInvariant)]
    private static partial Regex CellAddressRegex();

    [GeneratedRegex(@"^[A-Z][A-Z0-9_]{0,20}[-_]\d", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierValueRegex();

    [GeneratedRegex(@"^\d{4}[-/]\d{1,2}(?:[-/]\d{1,2})?", RegexOptions.CultureInvariant)]
    private static partial Regex DateValueRegex();

    [GeneratedRegex(@"^[A-Z][A-Z0-9_]{2,}$", RegexOptions.CultureInvariant)]
    private static partial Regex UppercaseValueRegex();
}
