using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DocRedock.Core.Documents;

namespace DocRedock.Markdown;

/// <summary>Controls optional detail in the reader-oriented Markdown projection.</summary>
public sealed record ReadableMarkdownOptions(
    bool ShowFormulas = false,
    bool IncludeSvgPreviews = false,
    bool IncludeDiagrams = true,
    IReadOnlyList<string>? IncludedSheets = null,
    string? Title = null,
    string ContentPolicy = "visible");

/// <summary>
/// Produces Markdown intended for reading rather than round-tripping. Unlike the
/// DRMD projection, this format deliberately omits source coordinates and DRMD
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

    /// <summary>Diagnostics produced while the last projection was serialized.</summary>
    public IReadOnlyList<MarkdownDiagnostic> Diagnostics { get; private set; } = Array.Empty<MarkdownDiagnostic>();

    public ReadableMarkdownSerializer(ReadableMarkdownOptions? options = null) => this.options = options ?? new();

    public string Serialize(DocumentGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var policy = DocumentContentPolicyRules.Parse(options.ContentPolicy);
        var excludedCount = graph.Nodes.Count(node => !DocumentContentPolicyRules.Includes(node, policy));
        var sensitiveCount = graph.Nodes.Count(node => node.Layer is ContentLayer.Hidden or ContentLayer.Metadata ||
            node.Kind is NodeKind.Comment or NodeKind.Revision or NodeKind.SpeakerNotes);
        Diagnostics = policy == DocumentContentPolicy.Complete && sensitiveCount > 0
            ? [new MarkdownDiagnostic("HiddenContentIncluded", $"Complete content policy included {sensitiveCount} hidden or metadata node(s).", MarkdownDiagnosticSeverity.Warning)]
            : excludedCount > 0
                ? [new MarkdownDiagnostic("HiddenContentExcluded", $"{DocumentContentPolicyRules.Name(policy)} content policy excluded {excludedCount} hidden or metadata node(s).", MarkdownDiagnosticSeverity.Info)]
                : Array.Empty<MarkdownDiagnostic>();
        var projectedGraph = graph with
        {
            Partitions = graph.Partitions.Select(partition => partition with
            {
                Nodes = partition.Nodes.Where(node => DocumentContentPolicyRules.Includes(node, policy)).ToArray()
            }).ToArray()
        };
        return projectedGraph.Format == DocumentFormatKind.Xlsx
            ? SerializeWorkbook(projectedGraph)
            : SerializeDocument(projectedGraph);
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
            var images = ReadImages(partition);
            var charts = partition.Nodes.Any(node => node.Kind == NodeKind.Chart && HasExtension(node, "chart_series"));
            var partitionMedia = partition.Nodes.Any(node => node.Kind is NodeKind.Image or NodeKind.ImageText);
            if (rows.Count == 0 && diagrams.Count == 0 && images.Count == 0 && !charts && !partitionMedia) continue;

            WriteHeading(output, 2, HumanizePartitionName(partition.Id));
            var hasSectionHeading = false;
            var index = 0;
            var insertions = diagrams.Select(diagram => new WorkbookInsertion(diagram.MinRow, diagram.Mermaid, null, diagram))
                .Concat(images.Select(image => new WorkbookInsertion(image.Row, image.Node.Id, image.Node, null)))
                .OrderBy(item => item.Row).ThenBy(item => item.Id, StringComparer.Ordinal)
                .ToArray();
            foreach (var insertion in insertions)
            {
                var next = index;
                while (next < rows.Count && rows[next].Number < insertion.Row) next++;
                RenderWorkbookRows(output, rows[index..next]
                    .Where(row => !IsRedundantTitle(row, title, partition.Id)).ToArray(), ref hasSectionHeading);
                if (insertion.Diagram is { } diagram)
                {
                    WriteMermaid(output, diagram.Mermaid);
                    while (next < rows.Count && rows[next].Number <= diagram.MaxRow) next++;
                }
                else if (insertion.Image is { } image)
                    WriteImageNode(output, image, partition);
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
        var metadataRows = rows.TakeWhile(row => row.Cells.Count == 2 && LooksLikeLabel(row.Cells[0])).ToArray();
        if (metadataRows.Length > 0 && metadataRows.Length < rows.Count && rows[metadataRows.Length].Cells.Count >= 3)
        {
            WriteKeyValueRows(output, metadataRows);
            RenderWorkbookRows(output, rows.Skip(metadataRows.Length).ToArray(), ref hasSectionHeading);
            return;
        }
        var isolatedMetadataRows = rows.Where(row => IsMetadataFragment(row.Cells)).ToArray();
        if (isolatedMetadataRows.Length > 0)
        {
            RenderWorkbookRows(output, rows.Except(isolatedMetadataRows).ToArray(), ref hasSectionHeading);
            foreach (var row in isolatedMetadataRows) RenderStandaloneRow(output, row);
            return;
        }
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

    private string SerializeDocument(DocumentGraph graph)
    {
        var output = new StringBuilder();
        var wroteTitle = false;
        var previousWasListItem = false;
        var partitions = graph.Partitions.OrderBy(partition => partition.Order)
            .ThenBy(partition => partition.Id, StringComparer.Ordinal).ToArray();
        var isPptx = graph.Format == DocumentFormatKind.Pptx;
        var documentTitleNode = partitions.SelectMany(partition => partition.Nodes)
            .FirstOrDefault(node => node.Kind == NodeKind.Heading && ExtensionBool(node, "document_title"));
        if (isPptx)
        {
            WriteHeading(output, 1, options.Title?.Trim() is { Length: > 0 } presentationTitle ? presentationTitle : "プレゼンテーション");
            wroteTitle = true;
        }
        else if (options.Title?.Trim() is { Length: > 0 } documentTitle)
        {
            WriteHeading(output, 1, documentTitle);
            wroteTitle = true;
        }
        else if (documentTitleNode is null)
        {
            WriteHeading(output, 1, "ドキュメント");
            wroteTitle = true;
        }
        // D04/D16/D17 readability pass: headers, footers, footnotes, and endnotes no longer
        // print as bare, unlabeled paragraphs wherever their extraction order happens to land
        // them in the body flow (previously mid-appendix for the furniture, since AddFootnotes/
        // AddEndnotes append after all body content). Each kind is pulled out of the main loop
        // and re-emitted, labeled, in one aggregated section per kind at the document's end.
        var aggregated = ComputeAggregatedSections(partitions);
        for (var partitionIndex = 0; partitionIndex < partitions.Length; partitionIndex++)
        {
            var partition = partitions[partitionIndex];
            // The visible policy can empty an entire hidden slide partition. Do not leave a
            // presentation-only slide label behind when none of its content is exportable.
            if (isPptx && partition.Nodes.Count == 0) continue;
            if (isPptx)
            {
                var slideTitle = partition.Nodes.FirstOrDefault(node =>
                    StringComparer.OrdinalIgnoreCase.Equals(ExtensionString(node, "shape_role"), "title"));
                var label = $"スライド {partitionIndex + 1}";
                var titleText = slideTitle is null ? string.Empty : NodeText(slideTitle).Trim();
                WriteHeading(output, 2, titleText.Length == 0 ? label : $"{label} — {titleText}");
                previousWasListItem = false;
            }
            foreach (var node in isPptx
                         ? PresentationReadingOrder(partition.Nodes)
                         : partition.Nodes.OrderBy(node => node.Order).ThenBy(node => node.Id, StringComparer.Ordinal))
            {
                if (aggregated.SkipIds.Contains(node.Id)) continue;
                var text = NodeText(node).Trim();
                if (string.IsNullOrWhiteSpace(text)) continue;
                if (isPptx && (IsPresentationFurniture(node) || IsRepeatedPresentationFooter(partitions, node))) continue;
                if (isPptx && StringComparer.OrdinalIgnoreCase.Equals(ExtensionString(node, "shape_role"), "title")) continue;
                if (node.Kind == NodeKind.Link) continue; // D12: already inlined as [text](url) by the owning paragraph's rich text.
                var isListItem = node.Kind is NodeKind.List or NodeKind.ListItem or NodeKind.Connector;
                if (previousWasListItem && !isListItem) output.AppendLine();
                switch (node.Kind)
                {
                    case NodeKind.Heading:
                        var sourceLevel = ExtensionInt(node, "heading_level") ?? 1;
                        var headingLevel = ExtensionBool(node, "document_title") ? 1 : wroteTitle ? sourceLevel + 1 : sourceLevel;
                        WriteHeading(output, headingLevel, text);
                        wroteTitle = true;
                        break;
                    case NodeKind.Section when ExtensionString(node, "section_orientation") is { Length: > 0 } orientation:
                        // D05: a machine-readable marker for a section's page orientation, not a
                        // heading — the surrounding chapter headings already describe the content.
                        output.Append("<!-- section:").Append(orientation).Append(" -->").AppendLine().AppendLine();
                        break;
                    case NodeKind.Section:
                    case NodeKind.Slide:
                    case NodeKind.Page:
                        WriteHeading(output, wroteTitle ? 2 : 1, text);
                        wroteTitle = true;
                        break;
                    case NodeKind.Comment:
                        // D17: label a reviewer comment instead of an unmarked blockquote so it
                        // reads distinctly from ordinary quoted text and tracked-change markup.
                        var commentAuthor = ExtensionString(node, "comment_author");
                        WriteQuote(output, commentAuthor is { Length: > 0 }
                            ? $"**コメント** ({commentAuthor}): {text}"
                            : $"**コメント**: {text}");
                        break;
                    case NodeKind.Quote:
                    case NodeKind.Annotation:
                        WriteQuote(output, text);
                        break;
                    case NodeKind.CodeBlock:
                        // D11: render literal line breaks, not the styled `<br>` markdown used
                        // inline elsewhere — a code fence's content must stay verbatim.
                        var codeText = node.Content is RichTextNodeContent codeRich
                            ? string.Concat(codeRich.Runs.Select(run => run.Text)).Trim()
                            : text;
                        output.AppendLine("```").AppendLine(codeText).AppendLine("```").AppendLine();
                        break;
                    case NodeKind.List:
                    case NodeKind.ListItem:
                        // D10: numbering.xml resolution (DocxAdapter.ResolveListNumbering) already
                        // populates list_format/list_number; D10-1's guard was relaxed to a
                        // marker-agnostic contains check, so this can render the real sequence
                        // number for an ordered item instead of always "- ".
                        var marker = StringComparer.Ordinal.Equals(ExtensionString(node, "list_format"), "ordered") && ExtensionInt(node, "list_number") is { } listNumber
                            ? listNumber.ToString(CultureInfo.InvariantCulture) + ". "
                            : "- ";
                        var listText = InlineText(text);
                        // Some producers include the visible ordinal in w:t as well as w:numPr.
                        // The semantic marker above is authoritative, so suppress that duplicate.
                        if (marker.EndsWith(". ", StringComparison.Ordinal) && listText.StartsWith(marker, StringComparison.Ordinal))
                            listText = listText[marker.Length..].TrimStart();
                        output.Append(' ', Math.Max(0, ExtensionInt(node, "list_level") ?? 0) * 2)
                            .Append(marker).AppendLine(listText);
                        break;
                    case NodeKind.Table when node.Content is TableNodeContent table:
                        WriteArbitraryTable(output, table.Rows);
                        break;
                    case NodeKind.Image when node.Content is ReferenceNodeContent:
                        WriteImageNode(output, node, partition, includeOcr: false);
                        break;
                    case NodeKind.ImageText:
                        WriteOcrDetails(output, text);
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
                    case NodeKind.Shape:
                        if (isPptx && StringComparer.OrdinalIgnoreCase.Equals(ExtensionString(node, "shape_role"), "title"))
                        {
                            WriteHeading(output, wroteTitle ? 2 : 1, text);
                            wroteTitle = true;
                        }
                        else if (isPptx)
                        {
                            WritePptxParagraphs(output, node);
                        }
                        else
                        {
                            WriteParagraph(output, text);
                        }
                        break;
                    case NodeKind.PageBreak:
                        // D18: the sole rendering of an explicit page break (a horizontal-rule
                        // chapter separator) — DocxAdapter excludes w:br type="page" from the
                        // owning paragraph's own text/rich-runs, so this marker node is not
                        // duplicating anything.
                        output.Append("---").AppendLine().AppendLine();
                        break;
                    case NodeKind.SpeakerNotes:
                        WriteSpeakerNotesDetails(output, node, text);
                        break;
                    case NodeKind.Connector:
                        // P08: a resolved stCxn/endCxn transition renders as a compact list so a
                        // chain of connectors reads as one flow instead of scattered paragraphs.
                        output.Append("- ").AppendLine(InlineText(text));
                        break;
                    case NodeKind.Chart when HasExtension(node, "chart_series"):
                        WriteChart(output, node);
                        break;
                    case NodeKind.Diagram when HasExtension(node, "diagram_items"):
                        WriteDiagram(output, node);
                        break;
                    case NodeKind.Chart:
                    case NodeKind.Diagram:
                        WriteQuote(output, $"図: {text}");
                        break;
                    default:
                        WriteParagraph(output, text);
                        break;
                }
                previousWasListItem = isListItem;
            }
            if (previousWasListItem) output.AppendLine();
            previousWasListItem = false;
        }

        WriteAggregatedSections(output, aggregated);
        if (output.Length == 0) WriteHeading(output, 1, "ドキュメント");
        return Finish(output);
    }

    private static void WriteAggregatedSections(StringBuilder output, AggregatedSections aggregated)
    {
        if (aggregated.FurnitureItems.Count > 0)
        {
            WriteHeading(output, 3, "文書ヘッダー・フッター（参考）");
            foreach (var (label, text) in aggregated.FurnitureItems)
                output.Append("- ").Append(label).Append(": ").AppendLine(InlineText(text));
            output.AppendLine();
        }
        if (aggregated.Footnotes.Count > 0)
        {
            WriteHeading(output, 3, "脚注");
            for (var index = 0; index < aggregated.Footnotes.Count; index++)
                output.Append(index + 1).Append(". ").AppendLine(InlineText(aggregated.Footnotes[index]));
            output.AppendLine();
        }
        if (aggregated.Endnotes.Count > 0)
        {
            WriteHeading(output, 3, "文末脚注");
            for (var index = 0; index < aggregated.Endnotes.Count; index++)
                output.Append(index + 1).Append(". ").AppendLine(InlineText(aggregated.Endnotes[index]));
            output.AppendLine();
        }
    }

    private static IEnumerable<DocumentNode> PresentationReadingOrder(IReadOnlyList<DocumentNode> nodes)
    {
        var titleRoles = new[] { "title", "ctrtitle", "subtitle" };
        var titleNodes = nodes.Where(node => titleRoles.Contains(ExtensionString(node, "shape_role"), StringComparer.OrdinalIgnoreCase))
            .OrderBy(node => Array.IndexOf(titleRoles, ExtensionString(node, "shape_role")?.ToLowerInvariant()))
            .ThenBy(node => node.Geometry?.Y ?? double.MaxValue).ThenBy(node => node.Geometry?.X ?? double.MaxValue)
            .ThenBy(node => node.Order).ThenBy(node => node.Id, StringComparer.Ordinal);
        var bodyNodes = PresentationBodyReadingOrder(nodes.Where(node =>
            !titleRoles.Contains(ExtensionString(node, "shape_role"), StringComparer.OrdinalIgnoreCase) &&
            node.Kind is not NodeKind.Connector and not NodeKind.SpeakerNotes).ToArray());
        var connectors = nodes.Where(node => node.Kind == NodeKind.Connector)
            .OrderBy(node => node.Geometry?.Y ?? double.MaxValue).ThenBy(node => node.Geometry?.X ?? double.MaxValue)
            .ThenBy(node => node.Order).ThenBy(node => node.Id, StringComparer.Ordinal);
        var notes = nodes.Where(node => node.Kind == NodeKind.SpeakerNotes).OrderBy(node => node.Order).ThenBy(node => node.Id, StringComparer.Ordinal);
        return titleNodes.Concat(bodyNodes).Concat(connectors).Concat(notes);
    }

    private static IEnumerable<DocumentNode> PresentationBodyReadingOrder(IReadOnlyList<DocumentNode> nodes)
    {
        var spatial = nodes.Where(HasFiniteGeometry).ToArray();
        var withoutGeometry = nodes.Where(node => !HasFiniteGeometry(node))
            .OrderBy(node => node.Order).ThenBy(node => node.Id, StringComparer.Ordinal);
        if (spatial.Length == 0) return withoutGeometry;

        var minX = spatial.Min(node => node.Geometry!.X);
        var maxX = spatial.Max(node => node.Geometry!.X + node.Geometry.Width);
        var slideWidth = maxX - minX;
        if (!double.IsFinite(slideWidth) || slideWidth <= 0)
            return spatial.OrderBy(node => node.Geometry!.Y).ThenBy(node => node.Geometry!.X)
                .ThenBy(node => node.Order).ThenBy(node => node.Id, StringComparer.Ordinal).Concat(withoutGeometry);

        var fullWidth = spatial.Where(node => node.Geometry!.Width >= slideWidth * 0.7)
            .OrderBy(node => node.Geometry!.Y).ThenBy(node => node.Geometry!.X).ThenBy(node => node.Order).ToArray();
        var remaining = spatial.Except(fullWidth).ToList();
        var ordered = new List<DocumentNode>(spatial.Length);
        foreach (var divider in fullWidth)
        {
            var before = remaining.Where(node => node.Geometry!.Y + node.Geometry.Height / 2 < divider.Geometry!.Y).ToArray();
            ordered.AddRange(OrderPresentationBand(before, slideWidth));
            remaining.RemoveAll(before.Contains);
            ordered.Add(divider);
        }
        ordered.AddRange(OrderPresentationBand(remaining, slideWidth));
        return ordered.Concat(withoutGeometry);
    }

    private static IEnumerable<DocumentNode> OrderPresentationBand(IReadOnlyList<DocumentNode> nodes, double slideWidth)
    {
        IOrderedEnumerable<DocumentNode> TopThenLeft(IEnumerable<DocumentNode> items) => items
            .OrderBy(node => node.Geometry!.Y).ThenBy(node => node.Geometry!.X)
            .ThenBy(node => node.Order).ThenBy(node => node.Id, StringComparer.Ordinal);
        if (nodes.Count < 4) return TopThenLeft(nodes);

        var byCenter = nodes.OrderBy(node => node.Geometry!.X + node.Geometry!.Width / 2).ToArray();
        var split = Enumerable.Range(1, byCenter.Length - 1)
            .Select(index => (Index: index, Gap: byCenter[index].Geometry!.X + byCenter[index].Geometry!.Width / 2 -
                                                  (byCenter[index - 1].Geometry!.X + byCenter[index - 1].Geometry!.Width / 2)))
            .MaxBy(item => item.Gap);
        var left = byCenter[..split.Index];
        var right = byCenter[split.Index..];
        if (left.Length < 2 || right.Length < 2 || split.Gap < slideWidth * 0.15 ||
            !LooksLikeColumn(left, split.Gap) || !LooksLikeColumn(right, split.Gap))
            return TopThenLeft(nodes);
        return TopThenLeft(left).Concat(TopThenLeft(right));
    }

    private static bool LooksLikeColumn(IReadOnlyList<DocumentNode> nodes, double columnGap)
    {
        var centersX = nodes.Select(node => node.Geometry!.X + node.Geometry.Width / 2).ToArray();
        var centersY = nodes.Select(node => node.Geometry!.Y + node.Geometry.Height / 2).ToArray();
        var medianWidth = nodes.Select(node => node.Geometry!.Width).Order().ElementAt(nodes.Count / 2);
        var medianHeight = nodes.Select(node => node.Geometry!.Height).Order().ElementAt(nodes.Count / 2);
        return centersX.Max() - centersX.Min() <= Math.Max(medianWidth, columnGap * 0.6) &&
               centersY.Max() - centersY.Min() >= Math.Max(1, medianHeight * 0.5);
    }

    private static bool HasFiniteGeometry(DocumentNode node) => node.Geometry is { } geometry &&
        double.IsFinite(geometry.X) && double.IsFinite(geometry.Y) && double.IsFinite(geometry.Width) && double.IsFinite(geometry.Height);

    private static bool IsRepeatedPresentationFooter(IReadOnlyList<DocumentPartition> partitions, DocumentNode node)
    {
        if (node.Geometry is not { } geometry) return false;
        var maxY = partitions.SelectMany(partition => partition.Nodes).Where(HasFiniteGeometry)
            .Select(item => item.Geometry!.Y + item.Geometry.Height).DefaultIfEmpty(0).Max();
        if (maxY <= 0 || geometry.Y + geometry.Height < maxY * 0.8) return false;
        var normalized = Regex.Replace(NodeText(node), @"\d+", "#").Trim();
        return normalized.Length > 0 && partitions.SelectMany(partition => partition.Nodes)
            .Count(item => Regex.Replace(NodeText(item), @"\d+", "#").Trim().Equals(normalized, StringComparison.Ordinal)) >= 2;
    }

    private static bool IsPresentationFurniture(DocumentNode node)
    {
        if (node.Kind == NodeKind.SpeakerNotes) return false;
        var role = ExtensionString(node, "shape_role")?.Trim();
        return role is not null && role.Equals("footer", StringComparison.OrdinalIgnoreCase) ||
            role is not null && role.Equals("date", StringComparison.OrdinalIgnoreCase) ||
            role is not null && role.Equals("sldnum", StringComparison.OrdinalIgnoreCase) ||
            role is not null && role.Equals("slide-number", StringComparison.OrdinalIgnoreCase) ||
            role is not null && role.Equals("ftr", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record AggregatedSections(
        HashSet<string> SkipIds,
        IReadOnlyList<(string Label, string Text)> FurnitureItems,
        IReadOnlyList<string> Footnotes,
        IReadOnlyList<string> Endnotes);

    // D04/D16/D17 readability pass: Header/Footer/Footnote/Endnote nodes are pulled out of the
    // main body loop entirely (SkipIds) and instead summarized in labeled sections at the
    // document's end (see WriteAggregatedSections) rather than appearing as bare, unlabeled
    // paragraphs wherever AddRelatedTextPartsAsync/AddFootnotes/AddEndnotes happened to order
    // them (previously mid-appendix, since those all append after every body paragraph).
    //
    // DOCX sections that are "unlinked" (each keeps its own header/footer part) very often still
    // repeat the same header/footer text verbatim; a section whose text genuinely differs (e.g. a
    // landscape section's extra suffix, D05) still contains the shared text as a substring. Keep
    // only the longest text in each duplicate/subset chain, and only its first occurrence, so
    // D04-3's duplicate-count guard still holds once the shared text is aggregated instead of
    // dropped outright.
    private static AggregatedSections ComputeAggregatedSections(IReadOnlyList<DocumentPartition> partitions)
    {
        var skipIds = new HashSet<string>(StringComparer.Ordinal);
        var furnitureItems = new List<(string Label, string Text)>();
        foreach (var (kind, label) in new[] { (NodeKind.Header, "ヘッダー"), (NodeKind.Footer, "フッター") })
        {
            var candidates = partitions.SelectMany(partition => partition.Nodes).Where(node => node.Kind == kind)
                .OrderBy(node => node.Order).ThenBy(node => node.Id, StringComparer.Ordinal).ToArray();
            if (candidates.Length == 0) continue;
            var texts = candidates.Select(node => NodeText(node).Trim()).ToArray();
            var distinctTexts = texts.Where(value => value.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
            var keepTexts = distinctTexts
                .Where(value => !distinctTexts.Any(other => !StringComparer.Ordinal.Equals(other, value) && other.Contains(value, StringComparison.Ordinal)))
                .ToHashSet(StringComparer.Ordinal);
            var rendered = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < candidates.Length; index++)
            {
                skipIds.Add(candidates[index].Id);
                if (texts[index].Length == 0 || !keepTexts.Contains(texts[index]) || !rendered.Add(texts[index])) continue;
                furnitureItems.Add((label, texts[index]));
            }
        }

        List<string> CollectNotes(NodeKind kind)
        {
            var candidates = partitions.SelectMany(partition => partition.Nodes).Where(node => node.Kind == kind)
                .OrderBy(node => node.Order).ThenBy(node => node.Id, StringComparer.Ordinal).ToArray();
            var texts = new List<string>();
            foreach (var node in candidates)
            {
                skipIds.Add(node.Id);
                var text = NodeText(node).Trim();
                if (text.Length > 0) texts.Add(text);
            }
            return texts;
        }

        return new AggregatedSections(skipIds, furnitureItems, CollectNotes(NodeKind.Footnote), CollectNotes(NodeKind.Endnote));
    }

    private void RenderPartitionMedia(StringBuilder output, DocumentPartition partition)
    {
        var images = partition.Nodes.Where(node => node.Kind == NodeKind.Image && node.Content is ReferenceNodeContent)
            .Where(node => ExtensionInt(node, "row") is null)
            .OrderBy(node => node.Order).ThenBy(node => node.Id, StringComparer.Ordinal).ToArray();
        var imageText = partition.Nodes.Where(node => node.Kind == NodeKind.ImageText)
            .Where(node => node.ParentId is null || !partition.Nodes.Any(parent => parent.Id == node.ParentId && parent.Kind == NodeKind.Image && ExtensionInt(parent, "row") is not null))
            .OrderBy(node => node.Order).ThenBy(node => node.Id, StringComparer.Ordinal).ToArray();
        var charts = partition.Nodes.Where(node => node.Kind == NodeKind.Chart && HasExtension(node, "chart_series"))
            .OrderBy(node => ExtensionInt(node, "row") ?? int.MaxValue).ThenBy(node => ExtensionInt(node, "column") ?? int.MaxValue)
            .ThenBy(node => node.Order).ThenBy(node => node.Id, StringComparer.Ordinal).ToArray();
        if (images.Length == 0 && imageText.Length == 0 && charts.Length == 0) return;

        if (images.Length > 0 || imageText.Length > 0) WriteHeading(output, 3, "埋め込み画像");
        var renderedTextIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var imageNode in images)
        {
            WriteImageNode(output, imageNode, partition, includeOcr: false);
            foreach (var textNode in imageText.Where(node => StringComparer.Ordinal.Equals(node.ParentId, imageNode.Id)))
            {
                WriteOcrDetails(output, NodeText(textNode).Trim());
                renderedTextIds.Add(textNode.Id);
            }
        }
        foreach (var textNode in imageText.Where(node => !renderedTextIds.Contains(node.Id)))
            WriteOcrDetails(output, NodeText(textNode).Trim());
        if (charts.Length > 0)
        {
            WriteHeading(output, 3, "グラフ");
            foreach (var chart in charts) WriteChart(output, chart);
        }
    }

    private static void WriteImage(StringBuilder output, ReferenceNodeContent image)
    {
        var alt = (image.AltText ?? "図").Replace("[", "\\[", StringComparison.Ordinal)
            .Replace("]", "\\]", StringComparison.Ordinal);
        output.Append("![").Append(alt).Append("](").Append(MarkdownPathEncoder.Encode(image.Reference)).AppendLine(")").AppendLine();
    }

    private void WriteImageNode(StringBuilder output, DocumentNode imageNode, DocumentPartition partition, bool includeOcr = true)
    {
        if (imageNode.Content is not ReferenceNodeContent image) return;
        var mediaType = ImageMediaType(imageNode, image.Reference);
        if (!ImageDisplayPolicy.IsMarkdownDisplayable(mediaType))
        {
            var alt = string.IsNullOrWhiteSpace(image.AltText) ? "図" : image.AltText.Trim();
            var extension = Path.GetExtension(image.Reference);
            if (string.IsNullOrWhiteSpace(extension)) extension = "." + (mediaType?.Split('/').LastOrDefault() ?? "unknown");
            WriteQuote(output, $"図: {alt}（{extension} 形式は Markdown で表示できません: {MarkdownPathEncoder.Encode(image.Reference)}）");
            AddDiagnostic(new MarkdownDiagnostic("ImageFormatNotDisplayable",
                $"Image '{image.Reference}' uses a format that Markdown cannot display.", MarkdownDiagnosticSeverity.Warning, imageNode.Id));
        }
        else WriteImage(output, image);

        if (includeOcr)
            foreach (var textNode in partition.Nodes.Where(node => node.Kind == NodeKind.ImageText &&
                         StringComparer.Ordinal.Equals(node.ParentId, imageNode.Id))
                         .OrderBy(node => node.Order).ThenBy(node => node.Id, StringComparer.Ordinal))
                WriteOcrDetails(output, NodeText(textNode).Trim());
    }

    private void AddDiagnostic(MarkdownDiagnostic diagnostic)
    {
        Diagnostics = Diagnostics.Concat([diagnostic]).ToArray();
    }

    private static string? ImageMediaType(DocumentNode node, string reference)
    {
        var declared = ExtensionString(node, "image_media_type");
        if (!string.IsNullOrWhiteSpace(declared)) return declared;
        return Path.GetExtension(reference).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            ".svg" => "image/svg+xml",
            ".tif" or ".tiff" => "image/tiff",
            ".emf" => "image/emf",
            ".wmf" => "image/wmf",
            _ => "application/octet-stream",
        };
    }

    private static void WritePptxParagraphs(StringBuilder output, DocumentNode node)
    {
        // P15: a rotated shape's own text carries no visual cue once flattened to Markdown, so a
        // quiet HTML-comment annotation keeps the rotation fact readable next to its text.
        if (node.Geometry is { RotationDegrees: var rotation } && Math.Abs(rotation) >= 1)
            output.Append("<!-- 回転").Append(rotation.ToString("0.##", CultureInfo.InvariantCulture)).Append("° -->").AppendLine().AppendLine();

        if (node.Extensions is null || !node.Extensions.TryGetValue("paragraph_details", out var details) ||
            details.ValueKind != JsonValueKind.Array)
        {
            WritePptxFallbackParagraphs(output, NodeText(node));
            return;
        }

        var wroteBullet = false;
        foreach (var paragraph in details.EnumerateArray())
        {
            var text = RichParagraphText(paragraph);
            if (string.IsNullOrWhiteSpace(text)) continue;
            var isBullet = JsonBool(paragraph, "IsBullet", "isBullet", "is_bullet");
            var level = JsonInt(paragraph, "Level", "level") ?? 0;
            var literalBulletText = string.Empty;
            var hasLiteralBullet = TryStripPptxBullet(text.TrimStart(), out literalBulletText);
            if (isBullet || hasLiteralBullet)
            {
                // P03: a buAutoNum paragraph carries its resolved sequence number instead of always
                // degrading to "- ", consistent with the DOCX numbered-list projection.
                var isOrdered = isBullet && JsonBool(paragraph, "IsOrdered", "isOrdered", "is_ordered");
                var number = JsonInt(paragraph, "ListNumber", "listNumber", "list_number");
                var marker = isOrdered && number is { } ordinal ? ordinal.ToString(CultureInfo.InvariantCulture) + ". " : "- ";
                var itemText = hasLiteralBullet ? InlineText(literalBulletText) : text;
                output.Append(' ', Math.Max(0, level) * 2).Append(marker).AppendLine(itemText);
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

    private static void WritePptxFallbackParagraphs(StringBuilder output, string text)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal).Split('\n');
        var wroteBullet = false;
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                if (wroteBullet) output.AppendLine();
                wroteBullet = false;
                continue;
            }
            if (TryStripPptxBullet(line, out var bulletText))
            {
                output.Append("- ").AppendLine(InlineText(bulletText));
                wroteBullet = true;
            }
            else
            {
                if (wroteBullet) output.AppendLine();
                WriteParagraph(output, line);
                wroteBullet = false;
            }
        }
        if (wroteBullet) output.AppendLine();
    }

    private static bool TryStripPptxBullet(string text, out string value)
    {
        value = text;
        var leading = text.TrimStart();
        var indent = text[..(text.Length - leading.Length)];
        if (leading.Length >= 2 && (leading[0] is '•' or '●' or '▪' or '◦' or '–' or '—') && char.IsWhiteSpace(leading[1]))
        {
            value = indent + leading[2..].TrimStart();
            return value.Length > indent.Length;
        }
        if (leading.StartsWith("- ", StringComparison.Ordinal) || leading.StartsWith("* ", StringComparison.Ordinal))
        {
            value = indent + leading[2..].TrimStart();
            return value.Length > indent.Length;
        }
        foreach (var (open, close) in new[] { ("***", "***"), ("**", "**"), ("__", "__"), ("_", "_"), ("~~", "~~"), ("<u>", "</u>") })
        {
            if (!leading.StartsWith(open, StringComparison.Ordinal) || !leading.EndsWith(close, StringComparison.Ordinal) || leading.Length <= open.Length + close.Length) continue;
            var inner = leading[open.Length..^close.Length];
            if (!TryStripPptxBullet(inner, out var stripped)) continue;
            value = indent + open + stripped + close;
            return true;
        }
        return false;
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
                var strike = JsonBool(run, "Strike", "strike", "is_strike");
                value = InlineText(value);
                if (strike) value = "~~" + value + "~~";
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

    private static List<ReadableImage> ReadImages(DocumentPartition partition) => partition.Nodes
        .Where(node => node.Kind == NodeKind.Image && node.Content is ReferenceNodeContent)
        .Select(node => (Node: node, Row: ExtensionInt(node, "row")))
        .Where(item => item.Row is not null)
        .Select(item => new ReadableImage(item.Row!.Value, item.Node))
        .OrderBy(image => image.Row)
        .ThenBy(image => image.Node.Id, StringComparer.Ordinal)
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
        var text = !string.IsNullOrWhiteSpace(formula)
            ? options.ShowFormulas
                ? string.IsNullOrWhiteSpace(value)
                    ? $"（保存済み計算値なし: `={formula!.TrimStart('=')}`）"
                    : $"`={formula!.TrimStart('=')}` → {value}"
                : string.IsNullOrWhiteSpace(value) ? "（保存済み計算値なし）" : value
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
            WriteKeyValueRows(output, rows);
            return;
        }

        if (rows.Count >= 2 && width is >= 2 and <= 16)
        {
            if (FirstColumnLooksLikeData(rows))
                WriteTable(output, Enumerable.Repeat(string.Empty, width).ToArray(), rows.Select(row => row.Cells.Select(cell => cell.Text).ToArray()));
            else
                WriteTable(output, rows[0].Cells.Select(cell => cell.Text).ToArray(), rows.Skip(1).Select(row => row.Cells.Select(cell => cell.Text).ToArray()));
            return;
        }

        foreach (var row in rows) RenderStandaloneRow(output, row);
    }

    private static void WriteKeyValueRows(StringBuilder output, IReadOnlyList<SheetRow> rows)
    {
        WriteInference(output, "キー・値の配置を文書情報として分離");
        foreach (var row in rows)
            for (var index = 0; index + 1 < row.Cells.Count; index += 2)
                output.Append("- **").Append(InlineText(row.Cells[index].Text)).Append("**: ")
                    .AppendLine(InlineText(row.Cells[index + 1].Text));
        output.AppendLine();
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
            var metadataFragment = IsMetadataFragment(fragment.Cells);
            var startsSection = fragment.Cells.Count == 1 && !fragment.Cells[0].IsNumeric &&
                                TryGetSectionHeading(fragment.Cells[0].Text, out _, out _);
            var matching = startsSection || metadataFragment ? null : regions
                    .Where(region => (region.HasSectionHeading
                                         ? region.MaxColumn - region.MinColumn >= 3
                                         : fragment.Cells.Count > 1) &&
                                     fragment.Row.Number - region.MaxRow <= (region.HasSectionHeading ? 12 : 4) &&
                                     fragment.Row.Number >= region.MinRow &&
                                     !region.ContainsRow(fragment.Row.Number) &&
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

    private static bool IsMetadataFragment(IReadOnlyList<ReadableCell> cells)
    {
        if (cells.Count != 2) return false;
        var label = PlainText(cells[0].Text).Trim();
        return label.Contains("更新日", StringComparison.OrdinalIgnoreCase) ||
               label.Contains("作成日", StringComparison.OrdinalIgnoreCase) ||
               label.Equals("状態", StringComparison.OrdinalIgnoreCase) ||
               label.Equals("Status", StringComparison.OrdinalIgnoreCase) ||
               label.Equals("Public Beta", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<RowFragment> SplitRow(SheetRow row)
    {
        // Preserve compact rows as one logical table row. This keeps a four-column
        // header aligned with its following data row even when the source uses wide
        // visual spacing between cells (a common revision-history layout).
        if (row.Cells.Count <= 4)
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

    private static void WriteArbitraryTable(StringBuilder output, IReadOnlyList<IReadOnlyList<TableCell>> rows)
    {
        if (rows.Count == 0) return;
        var (tableRows, noteRows) = SplitFullWidthNoteRows(rows);
        var grid = ExpandTableGrid(tableRows);
        if (grid.Count > 0)
        {
            var width = grid.Max(row => row.Count);
            var headers = grid[0].Count == width ? grid[0] : Enumerable.Range(1, width).Select(index => $"内容{index}").ToArray();
            var data = grid[0].Count == width ? grid.Skip(1) : grid;
            WriteTable(output, headers, data);
        }
        // Coordinator-adjudicated readability rule: a row whose single cell spans the table's
        // full grid width (the common Word "note row" pattern — see the state-code table's
        // trailing "終端状態（○）に達した申請は…" row) reads as noise when duplicated across
        // every column, so it renders as a plain paragraph after the table instead. A partial
        // span (covering only some of the columns) is unaffected and still cell-duplicated by
        // ExpandTableGrid. The table itself is never split mid-way for this — every note row is
        // collected and emitted together, right after the (possibly shortened) table.
        foreach (var note in noteRows) WriteParagraph(output, note.Text);
    }

    private static (List<IReadOnlyList<TableCell>> TableRows, List<TableCell> NoteRows) SplitFullWidthNoteRows(IReadOnlyList<IReadOnlyList<TableCell>> rows)
    {
        var totalWidth = rows.Max(row => row.Sum(cell => Math.Max(1, cell.ColSpan)));
        var tableRows = new List<IReadOnlyList<TableCell>>();
        var noteRows = new List<TableCell>();
        foreach (var row in rows)
        {
            if (totalWidth > 1 && row.Count == 1 && row[0].RowSpan != 0 && row[0].ColSpan >= totalWidth)
                noteRows.Add(row[0]);
            else
                tableRows.Add(row);
        }
        return (tableRows, noteRows);
    }

    // GFM has no colspan/rowspan. Preserve the visual grid width, but emit text only at a
    // merge origin; horizontal and vertical continuation coordinates are deliberately blank.
    private static List<IReadOnlyList<string>> ExpandTableGrid(IReadOnlyList<IReadOnlyList<TableCell>> rows)
    {
        if (!TableGrid.TryCreate(new TableNodeContent(rows), out var grid, out _)) return [];
        return grid.Rows.Select(row => (IReadOnlyList<string>)row
            .Select(slot => slot.IsContinuation ? string.Empty : slot.Origin.Text).ToArray()).ToList();
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

    private static void WriteOcrDetails(StringBuilder output, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        output.AppendLine("<details class=\"ocr-extraction\">")
            .AppendLine("<summary>OCR抽出テキスト（クリックで展開）</summary>")
            .AppendLine();
        foreach (var line in text.Trim().Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal).Split('\n'))
            output.Append("> ").Append(line.Trim()).AppendLine("  ");
        output.AppendLine();
        output.AppendLine("</details>").AppendLine();
    }

    private static void WriteSpeakerNotesDetails(StringBuilder output, DocumentNode node, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        output.AppendLine("<details class=\"speaker-notes\">")
            .AppendLine("<summary>スピーカーノート（クリックで展開）</summary>")
            .AppendLine();
        // P10: when the notes shape's own paragraph/run structure survived extraction, reuse the
        // same bold/bullet-aware writer PPTX slide bodies use instead of a flattened blockquote.
        if (HasExtension(node, "paragraph_details")) WritePptxParagraphs(output, node);
        else WriteQuote(output, text.Trim());
        output.AppendLine("</details>").AppendLine();
    }

    // P06: a native chart's c:title + c:ser category/value pairs, extracted by PptxAdapter into the
    // chart_title/chart_series extensions, become a bold title and one GFM table per series instead
    // of vanishing (the chart shape itself carries no text of its own).
    private static void WriteChart(StringBuilder output, DocumentNode node)
    {
        var title = ExtensionString(node, "chart_title");
        var rawType = ExtensionString(node, "chart_type");
        var type = ChartTypeLabel(rawType);
        if (!string.IsNullOrWhiteSpace(title))
        {
            output.Append("**").Append(InlineText(title)).Append("**");
            if (type is { Length: > 0 }) output.Append("（").Append(type).Append("）");
            output.AppendLine().AppendLine();
        }
        if (node.Extensions is null || !node.Extensions.TryGetValue("chart_series", out var seriesElement) || seriesElement.ValueKind != JsonValueKind.Array) return;
        var seriesItems = seriesElement.EnumerateArray().ToArray();
        output.Append("要約: ").Append(seriesItems.Length.ToString(CultureInfo.InvariantCulture)).AppendLine(" 系列のグラフです。");
        foreach (var series in seriesItems)
        {
            var categories = TryProperty(series, out var catElement, "Categories", "categories") && catElement.ValueKind == JsonValueKind.Array
                ? catElement.EnumerateArray().Select(item => item.GetString() ?? string.Empty).ToArray() : [];
            var values = TryProperty(series, out var valElement, "Values", "values") && valElement.ValueKind == JsonValueKind.Array
                ? valElement.EnumerateArray().Select(item => item.GetString() ?? string.Empty).ToArray() : [];
            var name = JsonString(series, "Name", "name");
            WriteChartSummary(output, string.IsNullOrWhiteSpace(name) ? "値" : name, categories, values, rawType);
            if (categories.Length == 0) continue;
            var headers = new[] { "カテゴリ", string.IsNullOrWhiteSpace(name) ? "値" : name };
            var rows = categories.Select((category, index) => new[] { category, index < values.Length ? values[index] : string.Empty });
            WriteTable(output, headers, rows);
        }
    }

    private static void WriteChartSummary(StringBuilder output, string name, IReadOnlyList<string> categories, IReadOnlyList<string> values, string? chartType)
    {
        var numeric = values.Select((display, index) =>
                (Index: index, Display: display, Value: TryParseChartNumber(display, out var parsed) ? parsed : (double?)null))
            .Where(item => item.Value is not null)
            .Select(item => (item.Index, item.Display, Value: item.Value!.Value)).ToArray();
        if (numeric.Length == 0) return;
        if (chartType is "pie" or "doughnut")
        {
            WriteCompositionChartSummary(output, name, categories, numeric);
            return;
        }

        var first = numeric[0]; var last = numeric[^1];
        var direction = last.Value > first.Value ? "増加" : last.Value < first.Value ? "減少" : "横ばい";
        var minimum = numeric.MinBy(item => item.Value); var maximum = numeric.MaxBy(item => item.Value);
        output.Append("- ").Append(InlineText(name)).Append(": ").Append(InlineText(ChartCategory(categories, first.Index))).Append(" の ")
            .Append(InlineText(first.Display)).Append(" から ").Append(InlineText(ChartCategory(categories, last.Index))).Append(" の ")
            .Append(InlineText(last.Display)).Append(" へ ").Append(direction)
            .Append("。最小 ").Append(InlineText(minimum.Display)).Append("、最大 ")
            .Append(InlineText(maximum.Display)).AppendLine("。");
    }

    private static void WriteCompositionChartSummary(StringBuilder output, string name, IReadOnlyList<string> categories,
        IReadOnlyList<(int Index, string Display, double Value)> numeric)
    {
        var minimum = numeric.MinBy(item => item.Value);
        var maximum = numeric.MaxBy(item => item.Value);
        var hasValidTotal = numeric.All(item => item.Value >= 0) && numeric.Sum(item => item.Value) > 0;
        var total = hasValidTotal ? numeric.Sum(item => item.Value) : 0;
        output.Append("- ").Append(InlineText(name)).Append(": 最大 ")
            .Append(InlineText(ChartCategory(categories, maximum.Index))).Append(' ').Append(InlineText(maximum.Display));
        if (hasValidTotal) output.Append("（全体の ").Append((maximum.Value / total).ToString("0.#%", CultureInfo.InvariantCulture)).Append("）");
        output.Append("、最小 ").Append(InlineText(ChartCategory(categories, minimum.Index))).Append(' ').Append(InlineText(minimum.Display));
        if (hasValidTotal) output.Append("（全体の ").Append((minimum.Value / total).ToString("0.#%", CultureInfo.InvariantCulture)).Append("）");
        output.AppendLine("。");
    }

    private static string ChartCategory(IReadOnlyList<string> categories, int index) =>
        index < categories.Count && !string.IsNullOrWhiteSpace(categories[index])
            ? categories[index]
            : (index + 1).ToString(CultureInfo.InvariantCulture);

    private static bool TryParseChartNumber(string value, out double number)
    {
        var normalized = value.Trim();
        var negative = normalized.StartsWith('(') && normalized.EndsWith(')');
        var percentage = normalized.EndsWith('%');
        normalized = Regex.Replace(normalized, @"[^0-9eE+\-.,]", string.Empty, RegexOptions.CultureInvariant)
            .Replace(",", string.Empty, StringComparison.Ordinal);
        if (!double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out number)) return false;
        if (negative) number = -Math.Abs(number);
        if (percentage) number /= 100;
        return true;
    }

    private static string? ChartTypeLabel(string? type) => type?.ToLowerInvariant() switch
    {
        "bar" => "棒グラフ", "line" => "折れ線グラフ", "pie" => "円グラフ", "doughnut" => "ドーナツグラフ",
        "area" => "面グラフ", "scatter" => "散布図", "bubble" => "バブルチャート", "radar" => "レーダーチャート",
        "stock" => "株価チャート", "surface" => "等高線グラフ", _ => type,
    };

    // P07: SmartArt's dgm:dataModel text (invisible to a normal shape scan) becomes a plain bullet
    // list, extracted by PptxAdapter into the diagram_items extension.
    private static void WriteDiagram(StringBuilder output, DocumentNode node)
    {
        if (node.Extensions is null || !node.Extensions.TryGetValue("diagram_items", out var items) || items.ValueKind != JsonValueKind.Array) return;
        foreach (var item in items.EnumerateArray())
        {
            var text = item.GetString();
            if (string.IsNullOrWhiteSpace(text)) continue;
            output.Append("- ").AppendLine(InlineText(text));
        }
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
        RichTextNodeContent rich => ReadableInlineText(rich.Runs),
        ReferenceNodeContent reference => reference.AltText ?? reference.Reference,
        TableNodeContent table => string.Join("\n", table.Rows.Select(row => string.Join(" | ", row.Select(cell => cell.Text)))),
        _ => string.Empty
    };

    // Readable-only inline rendering layered on top of the shared, round-trippable
    // DocRedockInlineMarkdown.Serialize: it additionally understands TextRun.LinkTarget/Color/
    // HighlightColor (D12, D15) and renders a tab as a literal tab instead of the `&#9;` HTML
    // entity DocRedockInlineMarkdown uses so a Tab-kind run survives an inline-markdown round trip
    // (D03). Neither concern touches DocRedockInlineMarkdown.cs itself, so the roundtrip `.docredock`
    // profile — which reuses that same shared serializer — renders exactly as it did before.
    private static string ReadableInlineText(IReadOnlyList<TextRun> runs)
    {
        var needsDecoration = runs.Any(run =>
            run.LinkTarget is not null ||
            !string.IsNullOrWhiteSpace(run.Color) ||
            !string.IsNullOrWhiteSpace(run.HighlightColor));
        var serialized = needsDecoration ? SerializeReadableRuns(runs) : DocRedockInlineMarkdown.Serialize(runs);
        return serialized.Contains("&#9;", StringComparison.Ordinal) ? serialized.Replace("&#9;", "\t", StringComparison.Ordinal) : serialized;
    }

    private static string SerializeReadableRuns(IReadOnlyList<TextRun> runs)
    {
        var output = new StringBuilder();
        var index = 0;
        while (index < runs.Count)
        {
            var current = runs[index];
            var start = index;
            while (index < runs.Count &&
                   runs[index].LinkTarget == current.LinkTarget &&
                   runs[index].Color == current.Color &&
                   runs[index].HighlightColor == current.HighlightColor)
            {
                index++;
            }

            var segmentRuns = runs.Skip(start).Take(index - start)
                .Select(run => run with { LinkTarget = null, Color = null, HighlightColor = null }).ToArray();
            var segment = DocRedockInlineMarkdown.Serialize(segmentRuns);
            if (segment.Length == 0) continue;

            if (!string.IsNullOrWhiteSpace(current.HighlightColor))
            {
                segment = "<mark>" + segment + "</mark>";
            }
            if (TryNormalizeHtmlColor(current.Color, out var color))
            {
                segment = "<span style=\"color:" + color + "\">" + segment + "</span>";
            }
            if (current.LinkTarget is not null)
            {
                var url = current.LinkTarget.IndexOfAny([' ', '(', ')']) >= 0 ? "<" + current.LinkTarget + ">" : current.LinkTarget;
                segment = "[" + segment + "](" + url + ")";
            }
            output.Append(segment);
        }
        return output.ToString();
    }

    private static bool TryNormalizeHtmlColor(string? value, out string color)
    {
        color = string.Empty;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var hex = value.Trim().TrimStart('#');
        if (hex.Length is not (6 or 8) || !hex.All(Uri.IsHexDigit)) return false;
        color = "#" + hex.ToUpperInvariant();
        return true;
    }

    private static string HumanizePartitionName(string id)
    {
        var value = id.Trim();
        foreach (var prefix in new[] { "worksheet-", "sheet-", "partition-" })
            if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) value = value[prefix.Length..];
        return value.Replace('_', ' ').Trim() is { Length: > 0 } result ? result : "シート";
    }

    private static string PlainText(string value) => value.Replace("`", string.Empty, StringComparison.Ordinal);
    private static string InlineText(string value) => Regex.Replace(value, @"</?span\b[^>]*>", string.Empty, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
        .Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal)
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
        public bool ContainsRow(int row) => cellsByRow.ContainsKey(row);
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
    private sealed record ReadableImage(int Row, DocumentNode Node);
    private sealed record WorkbookInsertion(int Row, string Id, DocumentNode? Image, ReadableDiagram? Diagram);

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
