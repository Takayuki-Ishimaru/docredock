using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DocRedock.Core.Documents;

namespace DocRedock.Markdown;

public enum OcrReviewMode { LowConfidence, All, Summary }

/// <summary>Controls optional detail in the reader-oriented Markdown projection.</summary>
public sealed record ReadableMarkdownOptions(
    bool ShowFormulas = false,
    bool IncludeSvgPreviews = false,
    bool IncludeDiagrams = true,
    IReadOnlyList<string>? IncludedSheets = null,
    string? Title = null,
    string ContentPolicy = "visible",
    OcrReviewMode OcrReview = OcrReviewMode.LowConfidence);

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

    /// <summary>Number of GFM tables in the last readable projection, including tables formed
    /// from worksheet cells and chart data. Fenced code is excluded.</summary>
    public int RenderedTables => Report.RenderedTables;

    /// <summary>What the last projection inferred from layout: rendered tables, worksheet regions
    /// written one after another although they sit side by side, and table boundaries that need
    /// comparison with the source.</summary>
    public ReadableProjectionReport Report { get; private set; } = ReadableProjectionReport.Empty;

    /// <summary>When set, a workbook projection records where each worksheet's content was written
    /// (<see cref="WorkbookLayout"/>). Off by default; it never changes the Markdown.</summary>
    public bool RecordWorkbookLayout { get; init; }

    /// <summary>The worksheet layout of the last workbook projection, or null when it was not
    /// recorded or the graph is not a workbook.</summary>
    public ReadableWorkbookLayout? WorkbookLayout { get; private set; }

    private int sideBySideRegions;
    private readonly List<ReadableTableBoundaryReview> tableBoundaryReviews = [];
    private SheetLayoutRecorder? sheetRecorder;

    public ReadableMarkdownSerializer(ReadableMarkdownOptions? options = null) => this.options = options ?? new();

    public string Serialize(DocumentGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var policy = DocumentContentPolicyRules.Parse(options.ContentPolicy);
        // Sheet filtering is an explicit projection boundary. Diagnostics must
        // describe the selected workbook, not sheets that cannot appear in output.
        var selectedGraph = graph.Format == DocumentFormatKind.Xlsx && options.IncludedSheets is { Count: > 0 }
            ? graph with { Partitions = graph.Partitions.Where(partition => IsIncludedPartition(partition.Id)).ToArray() }
            : graph;
        // P-Overlay (XLSX): a "sheet_overlay" Shape node is neither excluded from the counts below
        // nor from projectedGraph -- see IsAlwaysReadableSheetOverlay for why.
        var excludedCount = selectedGraph.Nodes.Count(node => !DocumentContentPolicyRules.Includes(node, policy) && !IsAlwaysReadableSheetOverlay(node));
        var sensitiveCount = selectedGraph.Nodes.Count(node => (node.Layer is ContentLayer.Hidden or ContentLayer.Metadata ||
            node.Kind is NodeKind.Comment or NodeKind.Revision or NodeKind.SpeakerNotes) && !IsAlwaysReadableSheetOverlay(node));
        Diagnostics = policy == DocumentContentPolicy.Complete && sensitiveCount > 0
            ? [new MarkdownDiagnostic("HiddenContentIncluded", $"Complete content policy included {sensitiveCount} hidden or metadata node(s).", MarkdownDiagnosticSeverity.Warning)]
            : excludedCount > 0
                ? [new MarkdownDiagnostic("HiddenContentExcluded", $"{DocumentContentPolicyRules.Name(policy)} content policy excluded {excludedCount} hidden or metadata node(s).", MarkdownDiagnosticSeverity.Info)]
                : Array.Empty<MarkdownDiagnostic>();
        var projectedGraph = selectedGraph with
        {
            Partitions = selectedGraph.Partitions.Select(partition => partition with
            {
                Nodes = partition.Nodes.Where(node => DocumentContentPolicyRules.Includes(node, policy) || IsAlwaysReadableSheetOverlay(node)).ToArray()
            }).ToArray()
        };
        sideBySideRegions = 0;
        tableBoundaryReviews.Clear();
        WorkbookLayout = null;
        var markdown = projectedGraph.Format == DocumentFormatKind.Xlsx
            ? SerializeWorkbook(projectedGraph)
            : SerializeDocument(projectedGraph);
        Report = new ReadableProjectionReport(CountRenderedTables(markdown), sideBySideRegions, tableBoundaryReviews.ToArray());
        return markdown;
    }

    private static int CountRenderedTables(string markdown)
    {
        var count = 0;
        char fenceCharacter = '\0';
        var fenceLength = 0;
        // The OCR review block lists recognized lines with their confidence in a table of its own;
        // that is review information written by DocRedock, not a table of the document.
        var inOcrReview = false;
        foreach (var line in markdown.Split('\n'))
        {
            var text = line.Trim();
            if (fenceCharacter == '\0' && text.StartsWith("<details class=\"ocr-extraction\">", StringComparison.Ordinal)) inOcrReview = true;
            else if (inOcrReview && text == "</details>") inOcrReview = false;
            if (inOcrReview) continue;
            if (text.Length >= 3 && text[0] is '`' or '~')
            {
                var length = text.TakeWhile(character => character == text[0]).Count();
                if (length >= 3)
                {
                    if (fenceCharacter == '\0') { fenceCharacter = text[0]; fenceLength = length; }
                    else if (fenceCharacter == text[0] && length >= fenceLength && text[length..].Length == 0)
                        fenceCharacter = '\0';
                    continue;
                }
            }
            if (fenceCharacter == '\0' && System.Text.RegularExpressions.Regex.IsMatch(text,
                    @"^\|(?:\s*:?-{3,}:?\s*\|)+$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)) count++;
        }
        return count;
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
        WriteHeading(output, 1, EscapeLiteral(title));
        var sheets = RecordWorkbookLayout ? new List<(string PartitionId, int Start, int ContentStart, int End, List<ReadableSheetSegment> Segments)>() : null;

        foreach (var partition in partitions)
        {
            var rows = ReadRows(partition);
            var diagrams = options.IncludeDiagrams ? ReadDiagrams(partition) : [];
            var images = ReadImages(partition);
            var charts = partition.Nodes.Any(node => node.Kind == NodeKind.Chart && HasExtension(node, "chart_series"));
            var partitionMedia = partition.Nodes.Any(node => node.Kind is NodeKind.Image or NodeKind.ImageText);
            if (rows.Count == 0 && diagrams.Count == 0 && images.Count == 0 && !charts && !partitionMedia) continue;

            var sheetStart = output.Length;
            WriteHeading(output, 2, EscapeLiteral(HumanizePartitionName(partition.Id)));
            var contentStart = output.Length;
            sheetRecorder = sheets is null ? null : new SheetLayoutRecorder();
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
                RenderWorkbookRows(output, partition, rows[index..next]
                    .Where(row => !IsRedundantTitle(row, title, partition.Id)).ToArray(), ref hasSectionHeading);
                var start = output.Length;
                if (insertion.Diagram is { } diagram)
                {
                    WriteMermaid(output, diagram.Mermaid);
                    var covered = next;
                    while (next < rows.Count && rows[next].Number <= diagram.MaxRow) next++;
                    // The diagram stands for the cells it covers; they are not written separately.
                    sheetRecorder?.Add(start, output.Length, rows[covered..next].SelectMany(row => row.Cells)
                        .Select(cell => cell.NodeId).Prepend(diagram.NodeId));
                }
                else if (insertion.Image is { } image)
                {
                    WriteImageNode(output, image, partition);
                    sheetRecorder?.Add(start, output.Length, partition.Nodes
                        .Where(node => node.Kind == NodeKind.ImageText && node.ParentId == image.Id).Select(node => node.Id).Prepend(image.Id));
                }
                index = next;
            }
            RenderWorkbookRows(output, partition, rows[index..]
                .Where(row => !IsRedundantTitle(row, title, partition.Id)).ToArray(), ref hasSectionHeading);
            var notesStart = output.Length;
            WriteSheetOverlayLineStyleNotes(output, partition);
            sheetRecorder?.Add(notesStart, output.Length, partition.Nodes
                .Where(node => node.Kind == NodeKind.Shape && HasExtension(node, "sheet_overlay")).Select(node => node.Id));
            var mediaStart = output.Length;
            RenderPartitionMedia(output, partition);
            var rowImages = partition.Nodes.Where(node => node.Kind == NodeKind.Image && ExtensionInt(node, "row") is not null)
                .Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
            sheetRecorder?.Add(mediaStart, output.Length, partition.Nodes.Where(node =>
                node.Kind == NodeKind.Chart || node.Kind == NodeKind.Image && !rowImages.Contains(node.Id) ||
                node.Kind == NodeKind.ImageText && (node.ParentId is null || !rowImages.Contains(node.ParentId))).Select(node => node.Id));
            if (sheetRecorder is not null)
                sheets!.Add((partition.Id, sheetStart, contentStart, output.Length, sheetRecorder.Segments));
            sheetRecorder = null;
        }

        var raw = output.ToString();
        var markdown = Finish(output);
        if (sheets is not null)
        {
            // Finish turns CRLF into LF and trims the end, so raw offsets are mapped onto its result.
            var removed = new int[raw.Length + 1];
            for (var position = 0; position < raw.Length; position++)
                removed[position + 1] = removed[position] + (raw[position] == '\r' && position + 1 < raw.Length && raw[position + 1] == '\n' ? 1 : 0);
            int Map(int offset) => Math.Min(offset - removed[offset], markdown.Length);
            WorkbookLayout = new ReadableWorkbookLayout(sheets.Select(sheet => new ReadableSheetLayout(sheet.PartitionId,
                Map(sheet.Start), Map(sheet.ContentStart), Map(sheet.End), sheet.Segments.Select(segment => segment with
                {
                    Start = Map(segment.Start), End = Map(segment.End),
                    Table = segment.Table is not { } table ? null : table with
                    {
                        HeaderEnd = Map(table.HeaderEnd),
                        Rows = table.Rows.Select(row => row with { Start = Map(row.Start), End = Map(row.End) }).ToArray(),
                    },
                }).ToArray())).ToArray());
        }
        return markdown;
    }

    // Records where each piece of a worksheet's projection was written and which nodes it came
    // from. Only an explicit RecordWorkbookLayout request creates one.
    private sealed class SheetLayoutRecorder
    {
        public List<ReadableSheetSegment> Segments { get; } = [];

        public void Add(int start, int end, IEnumerable<string?> nodeIds, ReadableSheetTable? table = null)
        {
            if (end > start)
                Segments.Add(new(start, end, nodeIds.OfType<string>().Distinct(StringComparer.Ordinal).ToArray(), table));
        }
    }

    private void RenderWorkbookRows(StringBuilder output, DocumentPartition partition, IReadOnlyList<SheetRow> rows, ref bool hasSectionHeading)
    {
        if (rows.Count == 0) return;
        var metadataRows = rows.TakeWhile(row => row.Cells.Count == 2 && LooksLikeLabel(row.Cells[0])).ToArray();
        if (metadataRows.Length > 0 && metadataRows.Length < rows.Count && rows[metadataRows.Length].Cells.Count >= 3 &&
            !metadataRows.Any(row => row.Cells.All(cell => cell.IsBold || cell.HasFill || cell.IsCentered)))
        {
            var start = output.Length;
            WriteKeyValueRows(output, metadataRows);
            sheetRecorder?.Add(start, output.Length, NodeIdsOf(metadataRows));
            RenderWorkbookRows(output, partition, rows.Skip(metadataRows.Length).ToArray(), ref hasSectionHeading);
            return;
        }
        var isolatedMetadataRows = rows.Where(row => IsMetadataFragment(row.Cells)).ToArray();
        if (isolatedMetadataRows.Length > 0)
        {
            RenderWorkbookRows(output, partition, rows.Except(isolatedMetadataRows).ToArray(), ref hasSectionHeading);
            foreach (var row in isolatedMetadataRows) RecordStandaloneRow(output, row);
            return;
        }
        var regions = BuildRegions(rows, out var uncertainGaps);
        // Name a document-information section only for label/value pairs: a table whose first row
        // names its columns ("Item | Q1") is data, not information about the document.
        if (!hasSectionHeading && regions.Any(region => region.MinRow <= 15 && IsDocumentInformationGroup(region.Rows)))
        {
            WriteInference(output, "セル配置から文書情報セクションを推定");
            WriteHeading(output, 3, "文書情報");
            hasSectionHeading = true;
        }
        sideBySideRegions += regions.Count(region => regions.Any(other => other.MinColumn < region.MinColumn &&
            other.MinRow <= region.Rows[^1].Number && region.MinRow <= other.Rows[^1].Number));
        var pending = uncertainGaps.ToList();
        foreach (var region in regions)
        {
            if (pending.FirstOrDefault(gap => region.MinColumn >= gap.RightStart && region.MinColumn <= gap.RightEnd &&
                    region.MinRow <= gap.MaxRow && region.Rows[^1].Number >= gap.MinRow) is { } gap)
            {
                pending.Remove(gap);
                ReportUncertainBoundary(output, partition, gap);
            }
            RenderRowGroup(output, region.Rows);
            hasSectionHeading |= region.Rows.SelectMany(row => row.Cells)
                .Any(cell => !cell.IsNumeric && TryGetSectionHeading(cell.Text, out _, out _));
        }
    }

    // The two sides of the gap are written as separate tables. Say so where it happens, and report
    // the ranges once, so a reader of the Markdown and of the export summary compares the same place.
    private void ReportUncertainBoundary(StringBuilder output, DocumentPartition partition, SheetGap gap)
    {
        var gapColumn = gap.LeftEnd + 1;
        var sheet = SheetName(partition.Id);
        var review = new ReadableTableBoundaryReview(partition.Id, sheet,
            CellRange(gapColumn, gap.MinRow, gapColumn, gap.MaxRow),
            CellRange(gap.LeftStart, gap.MinRow, gap.LeftEnd, gap.MaxRow),
            CellRange(gap.RightStart, gap.MinRow, gap.RightEnd, gap.MaxRow));
        WriteInference(output, $"{ColumnLetters(gapColumn)}列の空白を境に左右を別の表として出力（同じ行の対応は原本で確認）");
        tableBoundaryReviews.Add(review);
        AddDiagnostic(new MarkdownDiagnostic("XlsxTableBoundaryAmbiguous",
            $"Sheet '{sheet}': the cells on either side of blank column {ColumnLetters(gapColumn)} ({review.LeftRange} and {review.RightRange}) " +
            "were output as separate tables; whether their rows belong together could not be determined from the layout. Compare with the source.",
            MarkdownDiagnosticSeverity.Info, gap.RightNodeId ?? partition.Id + "!" + review.GapRange));
    }

    private static IEnumerable<string?> NodeIdsOf(IEnumerable<SheetRow> rows) =>
        rows.SelectMany(row => row.Cells).SelectMany(cell => (cell.OverlayNodeIds ?? []).Prepend(cell.NodeId));

    // Label/value pairs ("文書番号 | EXPS-DES-001 | 版 | 1.2") are information about the document.
    // A group whose first row names its columns ("Item | Q1") above data rows is a table, even when
    // every first cell happens to be a short label, so it gets no document-information heading.
    private static bool IsDocumentInformationGroup(IReadOnlyList<SheetRow> rows)
    {
        if (!LooksLikeKeyValueGroup(rows)) return false;
        var leadingCells = rows[0].Cells.Where(cell => !string.IsNullOrWhiteSpace(cell.Text)).ToArray();
        if (leadingCells.Length == 1 && (leadingCells[0].MaxColumn > leadingCells[0].Column ||
            !leadingCells[0].IsNumeric && TryGetSectionHeading(leadingCells[0].Text, out _, out _))) return false;
        if (rows.Count == 1 && rows[0].Cells.Count > 1 && rows[0].Cells.All(cell => TryGetSectionHeading(cell.Text, out _, out _))) return false;
        var namesColumns = rows.Count >= 2 && IsHeaderRow(rows[0]) &&
            Enumerable.Range(0, rows[0].Cells.Count / 2).All(index => LooksLikeLabel(rows[0].Cells[index * 2 + 1]));
        return !namesColumns;
    }

    private void RecordStandaloneRow(StringBuilder output, SheetRow row)
    {
        var start = output.Length;
        RenderStandaloneRow(output, row);
        sheetRecorder?.Add(start, output.Length, NodeIdsOf([row]));
    }

    private bool IsIncludedPartition(string partitionId)
    {
        if (options.IncludedSheets is not { Count: > 0 }) return true;
        var sheetName = partitionId.Trim();
        if (sheetName.StartsWith("sheet-", StringComparison.OrdinalIgnoreCase)) sheetName = sheetName["sheet-".Length..];
        else if (sheetName.StartsWith("worksheet-", StringComparison.OrdinalIgnoreCase)) sheetName = sheetName["worksheet-".Length..];
        else if (sheetName.StartsWith("partition-", StringComparison.OrdinalIgnoreCase)) sheetName = sheetName["partition-".Length..];
        return options.IncludedSheets.Any(sheet =>
            StringComparer.OrdinalIgnoreCase.Equals(sheet.Trim(), sheetName));
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
            WriteHeading(output, 1, options.Title?.Trim() is { Length: > 0 } presentationTitle ? EscapeLiteral(presentationTitle) : "プレゼンテーション");
            wroteTitle = true;
        }
        else if (options.Title?.Trim() is { Length: > 0 } documentTitle)
        {
            WriteHeading(output, 1, EscapeLiteral(documentTitle));
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
        var nestedTableFolds = ComputeNestedTableFolds(partitions);
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
                var titleText = slideTitle is null ? string.Empty : DisplayText(slideTitle, NodeText(slideTitle).Trim());
                WriteHeading(output, 2, titleText.Length == 0 ? label : $"{label} — {titleText}");
                previousWasListItem = false;
            }
            var visualGraphNodes = partition.Nodes
                .Where(node => node.Kind == NodeKind.Diagram && HasVisualGraph(node)).ToArray();
            // Suppress source members only when a validated graph can represent them. Invalid
            // metadata must retain its original connector/label fallback for diagnostics.
            var suppressVisualGraphMembers = visualGraphNodes.Any(node =>
                options.IncludeDiagrams ? TryGetRenderableVisualGraph(node) : TryGetRelationVisualGraph(node));
            var consumedVisualShapeIds = visualGraphNodes
                .SelectMany(VisualGraphMemberShapeIds).ToHashSet(StringComparer.Ordinal);
            // F-Issue7 (extended for P-Overlay): the pre-switch filtering chain is factored into a
            // local predicate so the same rules decide, up front, which table_overlays hosts in this
            // partition actually reach the switch -- that set is what silences their absorbed shapes.
            bool TryPrepareNode(DocumentNode candidate, out string rawText, out string display)
            {
                rawText = string.Empty;
                display = string.Empty;
                if (aggregated.SkipIds.Contains(candidate.Id) || nestedTableFolds.FoldedChildIds.Contains(candidate.Id)) return false;
                var candidateText = NodeText(candidate).Trim();
                if (string.IsNullOrWhiteSpace(candidateText)) return false;
                // F-Issue7: text above stays raw (comparisons, CodeBlock verbatim content);
                // displayText is what every other branch below actually renders.
                var candidateDisplay = DisplayText(candidate, candidateText);
                if (isPptx && (IsPresentationFurniture(candidate) || IsRepeatedPresentationFooter(partitions, candidate))) return false;
                if (suppressVisualGraphMembers && (ExtensionBool(candidate, "visual_graph_member") ||
                    ExtensionBool(candidate, "visual_edge_label") || ExtensionBool(candidate, "visual_graph_edge") ||
                    (ExtensionBool(candidate, "visual_graph_node") &&
                        ExtensionString(candidate, "shape_id") is { Length: > 0 } memberShapeId && consumedVisualShapeIds.Contains(memberShapeId)))) return false;
                if (isPptx && StringComparer.OrdinalIgnoreCase.Equals(ExtensionString(candidate, "shape_role"), "title")) return false;
                if (candidate.Kind == NodeKind.Link) return false; // D12: already inlined as [text](url) by the owning paragraph's rich text.
                rawText = candidateText;
                display = candidateDisplay;
                return true;
            }
            // P-Overlay: a table's own overlay markers/labels already carry what an absorbed shape
            // would otherwise print as a stray paragraph, so its host id is only suppressed once the
            // table itself is confirmed to actually reach the switch under this partition's filtering.
            // PPTX/XLSX host tables carry their own "shape_id" extension (the underlying shape's own
            // id); DOCX has no such extension on a w:tbl-derived Table node, so its own node Id -- the
            // value DocxAdapter stamps as table_overlay_host on the (B) TextBox node it tags -- is
            // included here too, matching whichever identifier space the host format actually used.
            var renderedOverlayTableIds = partition.Nodes
                .Where(candidate => candidate.Kind == NodeKind.Table && HasExtension(candidate, "table_overlays") &&
                    TryPrepareNode(candidate, out _, out _))
                .SelectMany(candidate => new[] { ExtensionString(candidate, "shape_id"), candidate.Id })
                .OfType<string>()
                .ToHashSet(StringComparer.Ordinal);
            foreach (var node in isPptx
                         ? PresentationReadingOrder(partition.Nodes)
                         : partition.Nodes.OrderBy(node => node.Order).ThenBy(node => node.Id, StringComparer.Ordinal))
            {
                if (!TryPrepareNode(node, out var text, out var displayText)) continue;
                if (ExtensionBool(node, "decorative_toc_leader")) continue;
                // P-Overlay suppression: no heading, no paragraph, and no rotation annotation -- the
                // host table already rendered this shape's text/marker in its own cells. DOCX tags a
                // (B) preceding-paragraph overlay shape's NodeKind.TextBox node (it has no
                // NodeKind.Shape node at all); PPTX/XLSX tag a NodeKind.Shape node.
                // P-ShapeGrid: a header/label/body-cell member of a synthesized grid table
                // (table_grid_member_host) is suppressed the same way, against the SAME
                // renderedOverlayTableIds set -- a grid table's own "shape_id" extension is
                // "grid:<headerFirstShapeId>", the identifier both extensions point at.
                if (node.Kind is NodeKind.Shape or NodeKind.TextBox &&
                    (ExtensionString(node, "table_overlay_host") is { Length: > 0 } overlayHostId && renderedOverlayTableIds.Contains(overlayHostId) ||
                     ExtensionString(node, "table_grid_member_host") is { Length: > 0 } gridHostId && renderedOverlayTableIds.Contains(gridHostId)))
                    continue;
                var isListItem = node.Kind is NodeKind.List or NodeKind.ListItem or NodeKind.Connector;
                if (previousWasListItem && !isListItem) output.AppendLine();
                switch (node.Kind)
                {
                    case NodeKind.Heading:
                        var sourceLevel = ExtensionInt(node, "heading_level") ?? 1;
                        var headingLevel = ExtensionBool(node, "document_title") ? 1 : wroteTitle ? sourceLevel + 1 : sourceLevel;
                        WriteHeading(output, headingLevel, displayText);
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
                        WriteHeading(output, wroteTitle ? 2 : 1, displayText);
                        wroteTitle = true;
                        break;
                    case NodeKind.Comment:
                        // D17: label a reviewer comment instead of an unmarked blockquote so it
                        // reads distinctly from ordinary quoted text and tracked-change markup.
                        var commentAuthor = ExtensionString(node, "comment_author");
                        WriteQuote(output, commentAuthor is { Length: > 0 }
                            ? $"**コメント** ({EscapeLiteral(commentAuthor)}): {displayText}"
                            : $"**コメント**: {displayText}");
                        break;
                    case NodeKind.Quote:
                    case NodeKind.Annotation:
                        WriteQuote(output, displayText);
                        break;
                    case NodeKind.CodeBlock:
                        // D11: render literal line breaks, not the styled `<br>` markdown used
                        // inline elsewhere — a code fence's content must stay verbatim, never
                        // Markdown-escaped; only the fence length adapts to what it wraps.
                        var codeText = node.Content is RichTextNodeContent codeRich
                            ? string.Concat(codeRich.Runs.Select(run => run.Text)).Trim()
                            : text;
                        var codeFence = CodeFence(codeText);
                        output.AppendLine(codeFence).AppendLine(codeText).AppendLine(codeFence).AppendLine();
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
                        var listText = InlineText(displayText);
                        // Some producers include the visible ordinal in w:t as well as w:numPr.
                        // The semantic marker above is authoritative, so suppress that duplicate.
                        if (marker.EndsWith(". ", StringComparison.Ordinal) && listText.StartsWith(marker, StringComparison.Ordinal))
                            listText = listText[marker.Length..].TrimStart();
                        output.Append(' ', Math.Max(0, ExtensionInt(node, "list_level") ?? 0) * 2)
                            .Append(marker).AppendLine(listText);
                        break;
                    case NodeKind.Table when node.Content is TableNodeContent table:
                        var overlayHostRows = FoldNestedTableRows(node.Id, table.Rows, nestedTableFolds);
                        WriteArbitraryTable(output, ApplyTableOverlays(node, overlayHostRows));
                        WriteOverlayLineStyleNotes(output, node, overlayHostRows);
                        break;
                    case NodeKind.Image when node.Content is ReferenceNodeContent:
                        WriteImageNode(output, node, partition, includeOcr: false);
                        break;
                    case NodeKind.ImageText:
                        WriteOcrDetails(output, displayText, node, partition);
                        break;
                    case NodeKind.Shape when HasExtension(node, "paragraph_details"):
                        if (StringComparer.OrdinalIgnoreCase.Equals(ExtensionString(node, "shape_role"), "title"))
                        {
                            WriteHeading(output, wroteTitle ? 2 : 1, displayText);
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
                            WriteHeading(output, wroteTitle ? 2 : 1, displayText);
                            wroteTitle = true;
                        }
                        else if (isPptx)
                        {
                            WritePptxParagraphs(output, node);
                        }
                        else
                        {
                            WriteParagraph(output, displayText);
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
                        WriteSpeakerNotesDetails(output, node, displayText);
                        break;
                    case NodeKind.Connector:
                        // P08: a resolved stCxn/endCxn transition renders as a compact list so a
                        // chain of connectors reads as one flow instead of scattered paragraphs.
                        output.Append("- ").AppendLine(InlineText(displayText));
                        break;
                    case NodeKind.Chart when HasExtension(node, "chart_series"):
                        WriteChart(output, node);
                        break;
                    case NodeKind.Diagram when HasExtension(node, "visual_graph"):
                        WriteVisualGraph(output, node);
                        break;
                    case NodeKind.Diagram when HasExtension(node, "diagram_items"):
                        WriteDiagram(output, node);
                        break;
                    case NodeKind.Chart:
                    case NodeKind.Diagram:
                        WriteQuote(output, $"図: {displayText}");
                        break;
                    default:
                        WriteParagraph(output, displayText);
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
        var visualGraphs = nodes.Where(node => node.Kind == NodeKind.Diagram && HasExtension(node, "visual_graph"))
            .OrderBy(node => node.Order).ThenBy(node => node.Id, StringComparer.Ordinal);
        return titleNodes.Concat(visualGraphs).Concat(bodyNodes.Where(node => !HasExtension(node, "visual_graph"))).Concat(connectors).Concat(notes);
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
                // Dedup/containment above is computed on the raw text; only the text actually
                // rendered in WriteAggregatedSections needs to be display-safe.
                furnitureItems.Add((label, DisplayText(candidates[index], texts[index])));
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
                if (text.Length > 0) texts.Add(DisplayText(node, text));
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
                WriteOcrDetails(output, DisplayText(textNode, NodeText(textNode).Trim()), textNode, partition);
                renderedTextIds.Add(textNode.Id);
            }
        }
        foreach (var textNode in imageText.Where(node => !renderedTextIds.Contains(node.Id)))
            WriteOcrDetails(output, DisplayText(textNode, NodeText(textNode).Trim()), textNode, partition);
        if (charts.Length > 0)
        {
            WriteHeading(output, 3, "グラフ");
            foreach (var chart in charts) WriteChart(output, chart);
        }
    }

    private static void WriteImage(StringBuilder output, ReferenceNodeContent image)
    {
        // EscapeLiteral escapes '[' and ']' itself (D07); bracket-escaping the alt text a second
        // time here would double the backslash and render a stray '\' inside the image label.
        var alt = EscapeLiteral(image.AltText ?? "図");
        output.Append("![").Append(alt).Append("](").Append(MarkdownPathEncoder.Encode(image.Reference)).AppendLine(")").AppendLine();
    }

    private void WriteImageNode(StringBuilder output, DocumentNode imageNode, DocumentPartition partition, bool includeOcr = true)
    {
        if (imageNode.Content is not ReferenceNodeContent image) return;
        var mediaType = ImageMediaType(imageNode, image.Reference);
        if (!ImageDisplayPolicy.IsMarkdownDisplayable(mediaType))
        {
            var alt = EscapeLiteral(string.IsNullOrWhiteSpace(image.AltText) ? "図" : image.AltText.Trim());
            var extension = Path.GetExtension(image.Reference);
            if (string.IsNullOrWhiteSpace(extension)) extension = "." + (mediaType?.Split('/').LastOrDefault() ?? "unknown");
            WriteQuote(output, $"図: {alt}（{extension} 形式は Markdown で表示できません: {MarkdownPathEncoder.Encode(image.Reference)}）");
            AddDiagnostic(new MarkdownDiagnostic("ImageFormatNotDisplayable",
                $"Image '{image.Reference}' uses a format that Markdown cannot display.", MarkdownDiagnosticSeverity.Warning, imageNode.Id));
        }
        else if (ExtensionDouble(imageNode, "display_width_px") is { } width &&
            ExtensionDouble(imageNode, "display_height_px") is { } height &&
            double.IsFinite(width) && double.IsFinite(height) && width is >= 1 and <= 8192 && height is >= 1 and <= 8192)
        {
            var scale = Math.Min(1, 960 / width);
            output.Append("<img src=\"").Append(System.Net.WebUtility.HtmlEncode(MarkdownPathEncoder.Encode(image.Reference)))
                .Append("\" alt=\"").Append(System.Net.WebUtility.HtmlEncode(image.AltText ?? "図"))
                .Append("\" width=\"").Append(Math.Max(1, (int)Math.Round(width * scale)).ToString(CultureInfo.InvariantCulture))
                .Append("\" height=\"").Append(Math.Max(1, (int)Math.Round(height * scale)).ToString(CultureInfo.InvariantCulture))
                .AppendLine("\">").AppendLine();
        }
        else WriteImage(output, image);

        if (includeOcr)
            foreach (var textNode in partition.Nodes.Where(node => node.Kind == NodeKind.ImageText &&
                         StringComparer.Ordinal.Equals(node.ParentId, imageNode.Id))
                         .OrderBy(node => node.Order).ThenBy(node => node.Id, StringComparer.Ordinal))
                WriteOcrDetails(output, DisplayText(textNode, NodeText(textNode).Trim()), textNode, partition);
    }

    private static string VisualDiagnosticBlockId(string fallbackBlockId, VisualDiagnostic diagnostic)
    {
        var location = new[]
        {
            diagnostic.Format,
            diagnostic.PartUri,
            diagnostic.PartitionId,
            diagnostic.SourceObjectId,
            diagnostic.SourceObjectType,
            diagnostic.Fallback,
            diagnostic.Remedy,
        }.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
        return location.Length == 0
            ? diagnostic.SourceNodeId ?? fallbackBlockId
            : string.Join("\u001f", new[] { diagnostic.SourceNodeId ?? fallbackBlockId }.Concat(location));
    }

    private void AddDiagnostic(MarkdownDiagnostic diagnostic)
    {
        // Adapters and graph validation can report the same issue at the same structured
        // location; keep one stable user-facing diagnostic per code/location in the projection.
        if (Diagnostics.Any(existing =>
                string.Equals(existing.Code, diagnostic.Code, StringComparison.Ordinal) &&
                string.Equals(existing.BlockId, diagnostic.BlockId, StringComparison.Ordinal)))
            return;
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
            WritePptxFallbackParagraphs(output, DisplayText(node, NodeText(node)));
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
                // F-Issue7: escape the literal run text before any decoration is wrapped around
                // it — escaping the composed "**bold**" afterward would corrupt the markers we
                // just added.
                value = InlineText(EscapeLiteral(value));
                if (strike) value = "~~" + value + "~~";
                if (underline) value = "<u>" + value + "</u>";
                if (bold && italic) value = "***" + value + "***";
                else if (bold) value = "**" + value + "**";
                else if (italic) value = "_" + value + "_";
                output.Append(value);
            }
            if (output.Length > 0) return output.ToString();
        }
        return InlineText(EscapeLiteral(JsonString(paragraph, "Text", "text") ?? string.Empty));
    }

    private static bool HasExtension(DocumentNode node, string key) => node.Extensions?.ContainsKey(key) == true;

    // P-Overlay (XLSX): XlsxAdapter stamps a "sheet_overlay" Shape node ContentLayer.Hidden purely
    // so the *generic* DRMD/roundtrip projection (DocRedockMarkdown.cs -- untouched by this
    // feature, and which applies this exact same DocumentContentPolicyRules.Includes gate) keeps
    // excluding it exactly as it always has: Xlsx drawings never became graph nodes before this
    // feature, so DRMD must not start rendering a new, disconnected shape block for one. This is
    // the one, narrow place that reaches back around that Hidden-layer exclusion so ReadRows (see
    // ApplySheetOverlays) can still see the node and fold its marker into the covered cell -- and
    // only for a node that (a) really is a table overlay and (b) is not *also* hidden for a
    // genuine reason. A hidden sheet stamps every one of its nodes' "sheet_state" extension to
    // something other than "visible" (see XlsxAdapter.Extract's "if (sheet.IsHidden)" remap); that
    // case keeps the ordinary hidden-content behavior instead of leaking through this bypass.
    private static bool IsAlwaysReadableSheetOverlay(DocumentNode node) =>
        node.Kind == NodeKind.Shape && HasExtension(node, "sheet_overlay") &&
        ExtensionString(node, "sheet_state") is null or "visible";

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
        var cellsByPosition = new Dictionary<(int Row, int Column), ReadableCell>();
        foreach (var node in partition.Nodes.Where(node => node.Kind == NodeKind.Cell))
        {
            var cell = ToCell(node);
            if (cell is not null) cellsByPosition[(cell.Row, cell.Column)] = cell;
        }
        // P-Overlay (XLSX): fold sheet_overlay markers in after ToCell, before the blank-text
        // filter below -- a covered position with no existing Cell node still needs a synthesized
        // cell so its marker survives that filter and joins the header/label region.
        ApplySheetOverlays(partition, cellsByPosition);
        // A copy of a filled-down (or filled-across) formula is judged against the written formula
        // nearest above it in its column, or else to its left in its row.
        var copies = cellsByPosition.Values.Where(cell => cell.IsFormulaCopy).ToArray();
        if (copies.Length > 0)
        {
            var written = cellsByPosition.Values.Where(cell => cell.IsFormula).ToArray();
            var byColumn = written.GroupBy(cell => cell.Column).ToDictionary(group => group.Key, group => group.OrderBy(cell => cell.Row).ToArray());
            var byRow = written.GroupBy(cell => cell.Row).ToDictionary(group => group.Key, group => group.OrderBy(cell => cell.Column).ToArray());
            static ReadableCell? Before(ReadableCell[]? ordered, int position, Func<ReadableCell, int> key)
            {
                if (ordered is null) return null;
                var (low, high) = (0, ordered.Length - 1);
                ReadableCell? found = null;
                while (low <= high)
                {
                    var middle = (low + high) / 2;
                    if (key(ordered[middle]) < position) { found = ordered[middle]; low = middle + 1; }
                    else high = middle - 1;
                }
                return found;
            }
            foreach (var copy in copies)
                if ((Before(byColumn.GetValueOrDefault(copy.Column), copy.Row, cell => cell.Row) ??
                     Before(byRow.GetValueOrDefault(copy.Row), copy.Column, cell => cell.Column)) is { } source)
                    cellsByPosition[(copy.Row, copy.Column)] = copy with { IsComputed = IsComputedResult(source.Formula!, DisplayOf(copy), copy.ReturnsText) };
        }

        var cells = cellsByPosition.Values
            .Where(cell => !string.IsNullOrWhiteSpace(cell.Text))
            .OrderBy(cell => cell.Row)
            .ThenBy(cell => cell.Column)
            .ToList();

        return cells.GroupBy(cell => cell.Row)
            .Select(group => new SheetRow(group.Key, group.ToList()))
            .OrderBy(row => row.Number)
            .ToList();
    }

    /// <summary>
    /// XLSX port of PptxAdapter/ApplyTableOverlays' cell folding: each "sheet_overlay" Shape node
    /// (one per detected overlay -- see XlsxAdapter.DetectSheetOverlays) writes its marker into
    /// every cell its StartRow..EndRow/StartColumn..EndColumn range covers. Unlike the PPTX table
    /// path there is no TableGrid/merged-slot dedup to do here: XlsxAdapter already resolves a
    /// covered cell inside a merged range to that range's origin before the overlay node is ever
    /// created, so every (row, column) this loop visits is already a distinct origin cell.
    /// Overlay nodes are visited in partition order, which XlsxAdapter.Extract populates in the
    /// same (StartRow, StartColumn, ShapeId) order DetectSheetOverlays returns -- so two overlays
    /// landing on the same cell still append in that deterministic order (spec: "同一セルへの複数
    /// 書き込み").
    /// </summary>
    private static void ApplySheetOverlays(DocumentPartition partition, Dictionary<(int Row, int Column), ReadableCell> cellsByPosition)
    {
        foreach (var node in partition.Nodes.Where(node => node.Kind == NodeKind.Shape && HasExtension(node, "sheet_overlay")))
        {
            var overlay = ReadSheetOverlay(node);
            if (overlay is null || overlay.EndRow < overlay.StartRow || overlay.EndColumn < overlay.StartColumn) continue;
            for (var row = overlay.StartRow; row <= overlay.EndRow; row++)
            for (var column = overlay.StartColumn; column <= overlay.EndColumn; column++)
            {
                var isLabelSlot = row == overlay.StartRow && column == overlay.StartColumn;
                var fragment = OverlayFragment(overlay, OverlayGlyph(overlay, row, column), isLabelSlot);
                if (fragment.Length == 0) continue;
                cellsByPosition[(row, column)] = cellsByPosition.TryGetValue((row, column), out var existing)
                    ? existing with { Text = existing.Text.Length == 0 ? fragment : existing.Text + "\n" + fragment, IsOverlay = true,
                        OverlayNodeIds = [.. existing.OverlayNodeIds ?? [], node.Id] }
                    : new ReadableCell(row, column, fragment, false, false, false, false, false, false, null, column, IsOverlay: true,
                        OverlayNodeIds: [node.Id]);
            }
        }
    }

    private static TableOverlay? ReadSheetOverlay(DocumentNode node)
    {
        if (node.Extensions is null || !node.Extensions.TryGetValue("sheet_overlay", out var raw) ||
            raw.ValueKind != JsonValueKind.Object) return null;
        var startRow = JsonInt(raw, "StartRow", "startRow") ?? 0;
        var startColumn = JsonInt(raw, "StartColumn", "startColumn") ?? 0;
        return new TableOverlay(
            JsonString(raw, "ShapeId", "shapeId") ?? string.Empty,
            JsonString(raw, "Text", "text") ?? string.Empty,
            JsonString(raw, "Kind", "kind") ?? string.Empty,
            JsonString(raw, "Direction", "direction") ?? "none",
            JsonString(raw, "Axis", "axis") ?? "horizontal",
            startRow, JsonInt(raw, "EndRow", "endRow") ?? startRow,
            startColumn, JsonInt(raw, "EndColumn", "endColumn") ?? startColumn,
            JsonString(raw, "ShapePreset", "shapePreset"),
            OverlayLineStyle(raw));
    }

    private static List<ReadableDiagram> ReadDiagrams(DocumentPartition partition) => partition.Nodes
        .Where(node => node.Kind == NodeKind.Diagram &&
                       StringComparer.OrdinalIgnoreCase.Equals(ExtensionString(node, "diagram_language"), "mermaid"))
        .Select(node => new ReadableDiagram(
            ExtensionInt(node, "diagram_min_row") ?? int.MaxValue,
            ExtensionInt(node, "diagram_max_row") ?? int.MaxValue,
            NodeText(node).Trim(), node.Id))
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
        var returnsText = StringComparer.Ordinal.Equals(ExtensionString(node, "cell_type"), "str");
        return new ReadableCell(row.Value, column.Value, text, !string.IsNullOrWhiteSpace(formula),
            ExtensionBool(node, "is_numeric") || StringComparer.Ordinal.Equals(ExtensionString(node, "cell_type"), "n"),
            ExtensionBool(node, "is_bold"), ExtensionBool(node, "has_fill"), ExtensionBool(node, "has_border"),
            ExtensionBool(node, "is_centered"), ExtensionDouble(node, "font_size"),
            Math.Max(ExtensionInt(node, "merged_to_column") ?? column.Value, ExtensionInt(node, "center_across_to_column") ?? column.Value),
            MaxRow: ExtensionInt(node, "merged_to_row") ?? row.Value,
            IsCenterAcross: ExtensionInt(node, "center_across_to_column") > column.Value,
            NodeId: node.Id, Table: ExtensionString(node, "excel_table"), Display: value,
            IsComputed: !string.IsNullOrWhiteSpace(formula) && IsComputedResult(formula!, value, returnsText),
            IsFormulaCopy: string.IsNullOrWhiteSpace(formula) && ExtensionBool(node, "is_formula"),
            Formula: string.IsNullOrWhiteSpace(formula) ? null : formula, ReturnsText: returnsText);
    }

    // A formula's result is a value unless it is text the formula did not choose itself: one of the
    // fixed words written in the formula (=IF(…,"達成","未達")) is a computed value, while text it looked
    // up or built from other cells (='月次分析'!H5, VLOOKUP, =B2&" "&C2), like a name, is read as typed
    // text. Numbers, logical values, errors and results never saved are values.
    private static bool IsComputedResult(string formula, string result, bool returnsText) =>
        !returnsText || result.Length == 0 || FormulaStringRegex().Matches(formula)
            .Any(literal => literal.Groups[1].Value.Replace("\"\"", "\"", StringComparison.Ordinal).Trim() is { Length: > 0 } word && word == result);

    private void RenderRowGroup(StringBuilder output, IReadOnlyList<SheetRow> rows)
    {
        var leadingCells = rows[0].Cells.Where(cell => !string.IsNullOrWhiteSpace(cell.Text)).ToArray();
        var start = output.Length;
        if (rows.Count > 1 && leadingCells.Length == 1 && !leadingCells[0].IsOverlay &&
            leadingCells[0].MaxColumn > leadingCells[0].Column && leadingCells[0].MaxRow <= leadingCells[0].Row &&
            leadingCells[0].MaxColumn >= rows.SelectMany(row => row.Cells).Max(cell => cell.Column))
        {
            // A merged banner describes the region. Once adjacent tables have been separated,
            // it must not become the first column's header or push the real header into the data.
            WriteParagraph(output, EscapeLiteral(leadingCells[0].Text));
            sheetRecorder?.Add(start, output.Length, [leadingCells[0].NodeId]);
            RenderRowGroup(output, WithoutEmptyColumns(rows.Skip(1)));
            return;
        }
        if (leadingCells.Length == 1 && !leadingCells[0].IsNumeric &&
            TryGetSectionHeading(leadingCells[0].Text, out var leadingHeading, out var leadingLevel))
        {
            WriteHeading(output, leadingLevel, EscapeLiteral(leadingHeading));
            sheetRecorder?.Add(start, output.Length, [leadingCells[0].NodeId]);
            if (rows.Count > 1) RenderRowGroup(output, WithoutEmptyColumns(rows.Skip(1)));
            return;
        }
        var width = rows[0].Cells.Count;
        if (rows.Count == 1 && width > 1 && rows[0].Cells.All(cell => TryGetSectionHeading(cell.Text, out _, out _)))
        {
            foreach (var cell in rows[0].Cells)
            {
                _ = TryGetSectionHeading(cell.Text, out var heading, out var level);
                WriteHeading(output, level, EscapeLiteral(heading));
            }
            sheetRecorder?.Add(start, output.Length, NodeIdsOf(rows));
            return;
        }

        if (TryWriteMultiRowHeaderTables(output, rows)) return;

        // A lone caption in the first column above a table (an unmerged title, an instruction, a unit
        // note) describes the table. It is not the first column's header: the table's own header is
        // the next row, and the caption must not push it down into the data.
        if (rows.Count >= 2 && leadingCells.Length == 1 && !leadingCells[0].IsOverlay && !leadingCells[0].IsNumeric &&
            leadingCells[0].Column == rows[0].Cells[0].Column &&
            rows[1].Cells.Count(cell => !string.IsNullOrWhiteSpace(cell.Text)) >= 2)
        {
            RecordStandaloneRow(output, rows[0]);
            RenderRowGroup(output, WithoutEmptyColumns(rows.Skip(1)));
            return;
        }

        if (rows.Count >= 2 && IsHeaderRow(rows[0]))
        {
            WriteSheetTable(output, rows[0].Cells.Select(cell => cell.Text).ToArray(), rows.Take(1).ToArray(), rows.Skip(1).ToArray());
            return;
        }

        if (LooksLikeKeyValueGroup(rows))
        {
            WriteKeyValueRows(output, rows);
            sheetRecorder?.Add(start, output.Length, NodeIdsOf(rows));
            return;
        }

        if (rows.Count >= 2 && width is >= 2 and <= 16)
        {
            if (FirstColumnLooksLikeData(rows))
                WriteSheetTable(output, Enumerable.Repeat(string.Empty, width).ToArray(), [], rows);
            else
                WriteSheetTable(output, rows[0].Cells.Select(cell => cell.Text).ToArray(), rows.Take(1).ToArray(), rows.Skip(1).ToArray());
            return;
        }

        // A one-column list under an emphasized header (a candidate or lookup list beside a table)
        // keeps its items under that header. Instructions, notes, sentences and headings stay
        // paragraphs: every item must be a short, unemphasized name.
        static bool IsListName(ReadableCell cell) => cell.Text.Length <= 40 && !cell.Text.Contains('\n') &&
            cell.Text.IndexOfAny(['。', '．', '！', '？', '!', '?']) < 0 && !NoteRegex().IsMatch(cell.Text) && !LooksLikeCode(cell.Text);
        if (rows.Count >= 3 && width == 1 && rows[0].Cells[0] is { IsNumeric: false } header && (header.IsBold || header.HasFill) &&
            IsListName(header) && rows.Skip(1).All(row => !row.Cells[0].IsBold && IsListName(row.Cells[0])))
        {
            WriteSheetTable(output, [header.Text], rows.Take(1).ToArray(), rows.Skip(1).ToArray());
            return;
        }

        foreach (var row in rows) RecordStandaloneRow(output, row);
    }

    // A region's rows are padded to every column any of its rows uses. Once a banner, caption or
    // heading above a table has been written on its own, the columns only it occupied are left out,
    // so a list under a wide banner does not get an empty first column.
    private static SheetRow[] WithoutEmptyColumns(IEnumerable<SheetRow> rows)
    {
        var rest = rows.ToArray();
        var used = rest.SelectMany(row => row.Cells).Where(cell => !string.IsNullOrWhiteSpace(cell.Text)).Select(cell => cell.Column).ToHashSet();
        return rest.Select(row => row with { Cells = row.Cells.Where(cell => used.Contains(cell.Column)).ToArray() }).ToArray();
    }

    // WriteTable for worksheet rows: the same Markdown, plus, when a layout is being recorded, where
    // the header and each data row line were written and which cells they came from.
    private void WriteSheetTable(StringBuilder output, IReadOnlyList<string> headers, IReadOnlyList<SheetRow> headerRows,
        IReadOnlyList<SheetRow> dataRows)
    {
        if (sheetRecorder is null)
        {
            WriteTable(output, headers, dataRows.Select(row => row.Cells.Select(cell => cell.Text).ToArray()));
            return;
        }
        var start = output.Length;
        WriteTableRow(output, headers);
        WriteTableRow(output, headers.Select(_ => "---").ToArray());
        var headerEnd = output.Length;
        var lines = new List<ReadableSheetTableRow>();
        foreach (var row in dataRows)
        {
            var lineStart = output.Length;
            WriteTableRow(output, Enumerable.Range(0, headers.Count).Select(index => index < row.Cells.Count ? row.Cells[index].Text : string.Empty).ToArray());
            lines.Add(new(lineStart, output.Length, row.Number, NodeIdsOf([row]).OfType<string>().Distinct(StringComparer.Ordinal).ToArray()));
        }
        output.AppendLine();
        var cells = headerRows.Concat(dataRows).SelectMany(row => row.Cells).ToArray();
        sheetRecorder.Add(start, output.Length, NodeIdsOf(headerRows.Concat(dataRows)), new ReadableSheetTable(headerEnd,
            NodeIdsOf(headerRows).OfType<string>().Distinct(StringComparer.Ordinal).ToArray(), headerRows.Select(row => row.Number).ToArray(),
            cells.Length == 0 ? 0 : cells.Min(cell => cell.Column), cells.Length == 0 ? 0 : cells.Max(cell => cell.MaxColumn), lines));
    }

    private bool TryWriteMultiRowHeaderTables(StringBuilder output, IReadOnlyList<SheetRow> rows)
    {
        var start = -1;
        for (var index = 2; index < Math.Min(rows.Count - 1, 17); index++)
            if (IsNumericDataRow(rows[index]) && IsNumericDataRow(rows[index + 1]) &&
                rows[index + 1].Number == rows[index].Number + 1) { start = index; break; }
        if (start < 0) return false;
        var headerRows = rows.Take(start).ToArray();
        if (headerRows.Any(row => IsNumericDataRow(row) || row.Cells.Any(cell => cell.IsOverlay || LooksLikeCode(cell.Text) || cell.MaxRow >= rows[start].Number))) return false;
        var hierarchy = headerRows.SelectMany(row => row.Cells).Any(cell =>
            !string.IsNullOrWhiteSpace(cell.Text) && (cell.MaxColumn > cell.Column || cell.MaxRow > cell.Row));
        if (!hierarchy && headerRows.Count(row => row.Cells.Count(cell => !string.IsNullOrWhiteSpace(cell.Text) &&
            (cell.IsBold || cell.HasFill || cell.IsCentered)) >= 2) < 2) return false;

        // Banner titles and unit declarations describe the whole table, rather than
        // only the first column. Keep them outside the flattened header.
        var banners = headerRows.TakeWhile(row =>
        {
            var cells = row.Cells.Where(cell => !string.IsNullOrWhiteSpace(cell.Text)).ToArray();
            return cells.Length == 1 && cells[0].MaxRow <= cells[0].Row && cells[0].Column == row.Cells[0].Column &&
                (cells[0].MaxColumn == cells[0].Column || cells[0].MaxColumn >= row.Cells[^1].Column) ||
                cells.Length > 0 && cells.All(cell => System.Text.RegularExpressions.Regex.IsMatch(PlainText(cell.Text),
                    @"(?:単位|\bunit\b|^\(?In thousands|^\(?In millions)", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
        }).ToArray();
        headerRows = headerRows.Skip(banners.Length).ToArray();
        if (headerRows.Length < 2) return false;
        var dataEnd = start;
        while (dataEnd < rows.Count && IsNumericDataRow(rows[dataEnd])) dataEnd++;
        if (dataEnd - start < 2) return false;
        var bannerStart = output.Length;
        foreach (var banner in banners)
            WriteParagraph(output, EscapeLiteral(string.Join(" — ", banner.Cells.Where(cell => !string.IsNullOrWhiteSpace(cell.Text)).Select(cell => cell.Text))));
        sheetRecorder?.Add(bannerStart, output.Length, NodeIdsOf(banners));

        var columns = rows[0].Cells.Select(cell => cell.Column).ToArray();
        var sourceHeaders = headerRows.SelectMany(row => row.Cells).Where(cell => !string.IsNullOrWhiteSpace(cell.Text)).ToArray();
        var headers = columns.Select(column =>
        {
            // Fill only a merge's declared span. Arbitrary empty cells never inherit
            // the label to their left. Vertical merges appear once in each path.
            var path = sourceHeaders.Where(cell => cell.Column <= column && cell.MaxColumn >= column)
                .OrderBy(cell => cell.Row).Select(cell => cell.Text).Distinct(StringComparer.Ordinal).ToArray();
            return path.Length > 0 ? string.Join(" / ", path) : $"列 {column}";
        }).ToArray();
        WriteSheetTable(output, headers, headerRows, rows.Skip(start).Take(dataEnd - start).ToArray());
        if (dataEnd < rows.Count) RenderRowGroup(output, rows.Skip(dataEnd).ToArray());
        return true;
    }

    private static bool IsNumericDataRow(SheetRow row) =>
        row.Cells.Count(cell => !string.IsNullOrWhiteSpace(cell.Text) && (cell.IsNumeric || cell.IsFormula) &&
            !(cell.IsNumeric && (cell.IsBold || cell.HasFill || cell.IsCentered) &&
                int.TryParse(PlainText(cell.Text), out var year) && year is >= 1900 and <= 2100)) >=
        Math.Max(2, (int)Math.Ceiling(row.Cells.Count * .35));

    private static void WriteKeyValueRows(StringBuilder output, IReadOnlyList<SheetRow> rows)
    {
        WriteInference(output, "キー・値の配置を文書情報として分離");
        foreach (var row in rows)
            for (var index = 0; index + 1 < row.Cells.Count; index += 2)
                output.Append("- **").Append(EscapedInlineText(row.Cells[index].Text)).Append("**: ")
                    .AppendLine(EscapedInlineText(row.Cells[index + 1].Text));
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
    private static IReadOnlyList<SheetRegion> BuildRegions(IReadOnlyList<SheetRow> rows, out IReadOnlyList<SheetGap> uncertainGaps)
    {
        var regions = new List<MutableRegion>();
        var gaps = SheetRegionBoundaries(rows);
        uncertainGaps = gaps.Where(gap => gap.Split && gap.Uncertain).ToArray();
        // A single blank column that was judged across rows, or the edge between two tables that touch,
        // separates every row it applies to, even one whose cells happen to sit far apart because a cell
        // beside the boundary is blank.
        IEnumerable<RowFragment> Fragments(SheetRow row)
        {
            var applying = gaps.Where(gap => SplitsRow(gap, row.Number)).ToArray();
            return SplitRow(row, applying.Select(gap => gap.RightStart).ToArray(),
                applying.Where(gap => gap.RightStart - gap.LeftEnd <= 2).Select(gap => gap.RightStart).ToHashSet());
        }
        foreach (var fragment in rows.SelectMany(Fragments).OrderBy(fragment => fragment.Row.Number).ThenBy(fragment => fragment.MinColumn))
        {
            var metadataFragment = IsMetadataFragment(fragment.Cells);
            var startsSection = fragment.Cells.Count == 1 && !fragment.Cells[0].IsNumeric &&
                                TryGetSectionHeading(fragment.Cells[0].Text, out _, out _);
            var matching = startsSection || metadataFragment ? null : regions
                    .Where(region => (region.HasSectionHeading
                                         ? region.MaxColumn - region.MinColumn >= 3
                                         : fragment.Cells.Count > 1 || region.MaxColumn == region.MinColumn ||
                                           fragment.Cells[0].IsHeaderStyled && fragment.Cells[0].Text.Length <= 80 &&
                                           (fragment.Cells[0].MaxColumn == fragment.Cells[0].Column || fragment.Cells[0].IsCenterAcross && fragment.MaxColumn <= region.MaxColumn) && !LooksLikeCode(fragment.Cells[0].Text) &&
                                           fragment.MinColumn >= region.MinColumn && fragment.MaxColumn <= region.MaxColumn) &&
                                     fragment.Row.Number - region.MaxRow <= (region.HasSectionHeading ? 12 : 4) &&
                                     fragment.Row.Number >= region.MinRow &&
                                     !region.ContainsRow(fragment.Row.Number) &&
                                     BandsOverlap(region.MinColumn, region.MaxColumn, fragment.MinColumn, fragment.MaxColumn))
                    .OrderByDescending(region => region.MaxRow)
                    .ThenBy(region => Math.Abs(region.MinColumn - fragment.MinColumn))
                    // A banner spanning the sheet must not take rows that fit a table below it exactly.
                    .ThenBy(region => Math.Abs(region.MaxColumn - fragment.MaxColumn))
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

    /// <summary>One empty-column gap between two column blocks of a section. <see cref="Split"/>
    /// separates the blocks into different regions; <see cref="Uncertain"/> marks a split that the
    /// layout could not justify either way, so the output says so and a person compares it.</summary>
    private sealed record SheetGap(int LeftStart, int LeftEnd, int RightStart, int RightEnd, int MinRow, int MaxRow,
        bool Split, bool Uncertain = false, string? RightNodeId = null);

    // Rows of merged titles that would bridge a blank column of the ordinary rows: a single banner,
    // or a row of group titles where one title spans a column every other row leaves empty
    // ("対象者タグ" over three code lists). They neither reconnect the tables below nor count as rows
    // of them. A sheet drawn entirely in merged cells (方眼紙) has no ordinary rows, so its merged
    // cells keep defining its columns, and a merged header beside ordinary cells still bridges.
    private static HashSet<int> BridgingTitleRows(IReadOnlyList<SheetRow> rows)
    {
        static bool Merged(ReadableCell cell) => cell.MaxColumn > cell.Column && !cell.IsOverlay;
        var covered = new HashSet<int>();
        foreach (var cell in rows.Where(row => !row.Cells.All(Merged)).SelectMany(row => row.Cells))
            for (var column = cell.Column; column <= cell.MaxColumn; column++) covered.Add(column);
        var (first, last) = covered.Count == 0 ? (0, 0) : (covered.Min(), covered.Max());
        bool Bridges(ReadableCell cell)
        {
            for (var column = Math.Max(cell.Column, first + 1); column <= Math.Min(cell.MaxColumn, last - 1); column++)
                if (!covered.Contains(column)) return true;
            return false;
        }
        return rows.Where(row => row.Cells.Count > 0 && row.Cells.All(Merged) &&
                (row.Cells.Count == 1 || row.Cells.Any(Bridges)))
            .Select(row => row.Number).ToHashSet();
    }

    private static IReadOnlyList<SheetGap> SheetRegionBoundaries(IReadOnlyList<SheetRow> rows)
    {
        // Column blocks are separated by at least one column that no cell (and no merge) covers.
        // Two or more empty columns always separate regions. A single empty column can separate
        // independent tables, or be a spacer inside one table (bilingual labels before their data,
        // an ID beside its quantities); DecideSingleColumnGap weighs the surrounding rows.
        var titles = BridgingTitleRows(rows);
        var cells = rows.Where(row => !titles.Contains(row.Number))
            .SelectMany(row => row.Cells).OrderBy(cell => cell.Column).ToArray();
        if (cells.Length == 0) return [];
        var blocks = new List<(int Start, int End)>();
        var start = cells[0].Column;
        var end = cells[0].MaxColumn;
        foreach (var cell in cells.Skip(1))
        {
            if (cell.Column - end >= 2)
            {
                blocks.Add((start, end));
                start = cell.Column;
            }
            end = Math.Max(end, cell.MaxColumn);
        }
        blocks.Add((start, end));
        var gaps = new List<SheetGap>();
        var groupStart = blocks[0].Start;
        var groupEnd = blocks[0].End;
        foreach (var block in blocks.Skip(1))
        {
            // The left side is everything joined since the previous split, so a later gap is judged
            // against the whole table it would extend, not only the nearest block.
            var gap = block.Start - groupEnd == 2
                ? DecideSingleColumnGap(rows, titles, groupStart, groupEnd, block.Start, block.End)
                : new SheetGap(groupStart, groupEnd, block.Start, block.End, 0, 0, Split: true);
            gaps.Add(gap);
            if (gap.Split) groupStart = block.Start;
            groupEnd = block.End;
        }
        // Two tables can also touch, with no blank column between them. Such an edge only separates the
        // rows it was found in; blank columns are still judged over whole blocks.
        foreach (var block in blocks)
            gaps.AddRange(TouchingTableEdges(rows, block.Start, block.End).Select(edge =>
                new SheetGap(block.Start, edge.Column - 1, edge.Column, block.End, edge.MinRow, edge.MaxRow, Split: true)));
        return gaps;
    }

    // A blank column separates every row; the edge between touching tables only the rows it was found in.
    private static bool SplitsRow(SheetGap gap, int row) =>
        gap.Split && (gap.RightStart - gap.LeftEnd > 1 || row >= gap.MinRow && row <= gap.MaxRow);

    /// <summary>
    /// Edges inside one block where a table starts right after another, with no blank column between
    /// them, and the rows each edge separates. The evidence must come from the author's own layout: a
    /// row of merged titles in which two titles touch, over ordinary (unmerged) cells in the rows
    /// directly below; under each title a first column of labels; and a difference in structure, not
    /// only in styling: one block is a declared Excel table the other is not part of, or one block
    /// starts with a header row while the other, starting on the same row, labels each of its rows in
    /// an emphasised first column (a legend such as 読み方 beside a list). Group titles over the values of one table (2024年 |
    /// 2025年), over columns of the same rows (担当者 | 連絡先, a WBS schedule), and remarks titles
    /// therefore keep one table, and so does a sheet drawn entirely in merged cells (方眼紙), whose every
    /// row would otherwise look like titles. Only the title row and the rows under the titles are cut.
    /// </summary>
    private static IEnumerable<(int Column, int MinRow, int MaxRow)> TouchingTableEdges(IReadOnlyList<SheetRow> rows, int blockStart, int blockEnd)
    {
        static bool Merged(ReadableCell cell) => cell.MaxColumn > cell.Column && !cell.IsOverlay;
        for (var index = 0; index < rows.Count; index++)
        {
            var titles = rows[index].Cells.Where(cell => cell.Column >= blockStart && cell.MaxColumn <= blockEnd).ToArray();
            if (titles.Length < 2 || !rows[index].Cells.All(Merged)) continue;
            for (var pair = 1; pair < titles.Length; pair++)
            {
                var (left, right) = (titles[pair - 1], titles[pair]);
                if (right.Column != left.MaxColumn + 1 ||
                    new[] { left, right }.Any(title => AnnotationHeaderRegex().IsMatch(PlainText(title.Text).Trim()))) continue;
                // The rows directly under the titles: contiguous, inside the columns of the row of titles,
                // up to a blank row, the next row of merged cells or a cell across the edge.
                var below = new List<SheetRow>();
                var previous = Math.Max(rows[index].Number, rows[index].Cells.Max(cell => cell.MaxRow));
                foreach (var row in rows.Skip(index + 1))
                {
                    var inBlock = row.Cells.Where(cell => cell.MaxColumn >= blockStart && cell.Column <= blockEnd).ToArray();
                    if (row.Number != previous + 1 || row.Cells.Count > 1 && row.Cells.All(Merged) ||
                        inBlock.Any(cell => cell.Column < titles[0].Column || cell.MaxColumn > titles[^1].MaxColumn ||
                            cell.Column <= left.MaxColumn && cell.MaxColumn >= right.Column)) break;
                    below.Add(row);
                    previous = row.Number;
                }
                var leftRows = Under(below, left);
                var rightRows = Under(below, right);
                if (leftRows.Length < 2 || rightRows.Length < 2) continue;
                if (leftRows.Concat(rightRows).SelectMany(row => row.Cells).Any(Merged)) continue;
                if (!HasOwnLabels(leftRows, left.Column) || !HasOwnLabels(rightRows, right.Column)) continue;
                var tables = (Left: leftRows.SelectMany(row => row.Cells).Select(cell => cell.Table).Distinct().ToArray(),
                    Right: rightRows.SelectMany(row => row.Cells).Select(cell => cell.Table).Distinct().ToArray());
                // A legend beside a table starts beside the table's header; a column of emphasised values
                // that starts beside its first data row (grades of each person) belongs to those rows.
                var shapes = new[] { Shape(leftRows, left.Column), Shape(rightRows, right.Column) };
                if (tables.Left.Concat(tables.Right).Any(table => table is not null) && !tables.Left.Intersect(tables.Right).Any() ||
                    shapes.Contains("header-row") && shapes.Contains("header-column") && leftRows[0].Number == rightRows[0].Number)
                    yield return (right.Column, rows[index].Number, previous);
            }
        }

        static SheetRow[] Under(IEnumerable<SheetRow> below, ReadableCell title) => below
            .Select(row => new SheetRow(row.Number, row.Cells.Where(cell => cell.Column >= title.Column && cell.MaxColumn <= title.MaxColumn).ToArray()))
            .Where(row => row.Cells.Count > 0).ToArray();
        // The block's first column names its rows: every cell there is a label, not a value.
        static bool HasOwnLabels(IReadOnlyList<SheetRow> block, int column)
        {
            var first = block.SelectMany(row => row.Cells).Where(cell => cell.Column == column).ToArray();
            return first.Length >= 2 && !first.Any(IsValueCell);
        }
        // How a block names its contents: a header row (an emphasised first row over plainer rows), a
        // header column (an emphasised first cell in every row beside plain cells), or neither.
        static string Shape(IReadOnlyList<SheetRow> block, int firstColumn)
        {
            static bool Emphasised(ReadableCell cell) => cell.IsBold || cell.HasFill;
            if (block[0].Cells.All(Emphasised) && !IsSubstantiveValue(block[0].Cells[0]) && block.Skip(1).Any(row => !row.Cells.All(Emphasised)))
                return "header-row";
            return block.All(row => row.Cells[0].Column == firstColumn && Emphasised(row.Cells[0]) && row.Cells.Skip(1).Any(cell => !Emphasised(cell)))
                ? "header-column" : "other";
        }
    }

    /// <summary>
    /// Decides whether one empty column separates two tables. Evidence is weighed in this order:
    /// <list type="number">
    /// <item>Explicit Excel table (ListObject) ranges: different tables, or a table and plain cells,
    /// are separate; one table is never split.</item>
    /// <item>A side that holds the values of the rows the other side labels stays with them: below at
    /// most two header rows its first column has no labels, at least three quarters of its cells are
    /// values (numbers, dates, formula results other than looked-up names, marks; a stray "未定" is
    /// allowed), and its value rows lie within the other side's rows. Columns headed 備考/Notes are
    /// annotations and do not count.
    /// This keeps an ID/name column with its quantities, and row labels with their data. A values
    /// block is a table of its own instead when it is out of step with the other side: its header
    /// beside the other side's data rows (it starts lower down) or its values beside the other side's
    /// header (it starts higher up). It is also a table of its own when its first column is headed
    /// as a period (年, 月, 日付, Year ...) and runs through regular periods while the other side has
    /// values of its own: two tables, each keyed by its own rows. Beside text only (施策, 担当), a
    /// period column keys the rows of one table and stays with them.</item>
    /// <item>A single remarks column (備考, Notes, ...) annotates the rows beside it.</item>
    /// <item>Otherwise both sides carry their own labels, and they are output as separate tables, so
    /// no row of one is presented as belonging to a row of the other. When the two sides start or end
    /// on different rows (a header above the other's, a shorter list) or each has its own merged
    /// title, the layout itself shows they are independent. When they occupy exactly the same rows
    /// with nothing else to tell them apart, the split is marked uncertain for comparison with the
    /// source.</item>
    /// </list>
    /// The number of cells on a side is never evidence by itself: a numeric ID column is part of
    /// its table, not a sign that a neighbouring block is independent.
    /// </summary>
    private static SheetGap DecideSingleColumnGap(IReadOnlyList<SheetRow> rows, IReadOnlySet<int> titleRows,
        int leftStart, int leftEnd, int rightStart, int rightEnd)
    {
        var sides = rows.Where(row => !titleRows.Contains(row.Number)).Select(row => (row.Number,
                Left: row.Cells.Where(cell => cell.Column >= leftStart && cell.MaxColumn <= leftEnd).ToArray(),
                Right: row.Cells.Where(cell => cell.Column >= rightStart && cell.MaxColumn <= rightEnd).ToArray()))
            .Where(row => row.Left.Length > 0 || row.Right.Length > 0).ToArray();
        // A row belongs to a side's extent when that side has a cell in each of two columns (or its
        // only column). Titles, unit notes and lone total labels therefore do not shift the extent.
        var leftExtent = sides.Where(row => row.Left.Length >= Math.Min(2, leftEnd - leftStart + 1)).ToArray();
        var rightExtent = sides.Where(row => row.Right.Length >= Math.Min(2, rightEnd - rightStart + 1)).ToArray();
        var leftRows = leftExtent.Select(row => row.Number).ToArray();
        var rightRows = rightExtent.Select(row => row.Number).ToArray();
        // The ranges a reviewer compares are the two tables, not every note elsewhere in the section.
        var spanned = leftRows.Concat(rightRows).DefaultIfEmpty().ToArray();
        var occupied = sides.Select(row => row.Number).DefaultIfEmpty().ToArray();
        var (minRow, maxRow) = leftRows.Length + rightRows.Length > 0 ? (spanned.Min(), spanned.Max()) : (occupied.Min(), occupied.Max());
        SheetGap Decide(bool split, bool uncertain = false) => new(leftStart, leftEnd, rightStart, rightEnd, minRow, maxRow,
            split, uncertain, sides.SelectMany(row => row.Right).Select(cell => cell.NodeId).FirstOrDefault(id => id is not null));

        var leftTables = (leftExtent.Length > 0 ? leftExtent : sides).SelectMany(row => row.Left)
            .Select(cell => cell.Table).Distinct().ToArray();
        var rightTables = (rightExtent.Length > 0 ? rightExtent : sides).SelectMany(row => row.Right)
            .Select(cell => cell.Table).Distinct().ToArray();
        if (leftTables.Concat(rightTables).Any(table => table is not null))
        {
            if (leftTables.Length == 1 && rightTables.Length == 1 && leftTables[0] == rightTables[0]) return Decide(false);
            if (!leftTables.Intersect(rightTables).Any()) return Decide(true);
        }

        if (leftRows.Length < 2 || rightRows.Length < 2) return Decide(false);
        var leftSide = sides.Select(row => (row.Number, Cells: row.Left)).ToArray();
        var rightSide = sides.Select(row => (row.Number, Cells: row.Right)).ToArray();
        if (HoldsValuesOf(leftSide, rightSide, rightRows) || HoldsValuesOf(rightSide, leftSide, leftRows)) return Decide(false);
        if (rightStart == rightEnd && sides.SelectMany(row => row.Right).FirstOrDefault() is { } rightHeader &&
            AnnotationHeaderRegex().IsMatch(PlainText(rightHeader.Text).Trim())) return Decide(false);
        // Each side under its own merged title in one row ("計画前提" over A:B, "分類マスタ" over
        // D:E) is two tables by the author's own layout, not one table with a spacer column.
        var ownTitles = rows.Any(row => row.Cells.Count > 1 && row.Cells.All(cell => cell.MaxColumn > cell.Column && !cell.IsOverlay) &&
            row.Cells.Any(cell => cell.Column >= leftStart && cell.MaxColumn <= leftEnd) &&
            row.Cells.Any(cell => cell.Column >= rightStart && cell.MaxColumn <= rightEnd));
        return Decide(true, uncertain: !ownTitles && rightRows[0] == leftRows[0] && rightRows[^1] == leftRows[^1]);
    }

    // Whether one side holds the values of rows labelled by the other side (the other side's cells by
    // row, and otherRows, the rows it spans, in order). Header rows are the text rows before the first
    // value (at most two). From there on, the side's first column has no labels and at least three
    // quarters of its cells are values; all those rows lie within the other side's rows. A dash or
    // similar placeholder is neutral: it neither starts the values nor counts as a label. Columns
    // headed 備考/Notes are annotations and are left out.
    private static bool HoldsValuesOf(IReadOnlyList<(int Number, ReadableCell[] Cells)> side,
        IReadOnlyList<(int Number, ReadableCell[] Cells)> other, IReadOnlyList<int> otherRows)
    {
        var rows = side.Where(row => row.Cells.Length > 0).ToArray();
        var annotations = rows.SelectMany(row => row.Cells).GroupBy(cell => cell.Column)
            .Where(group => AnnotationHeaderRegex().IsMatch(PlainText(group.First().Text).Trim()))
            .Select(group => group.Key).ToHashSet();
        var headers = new List<(int Number, ReadableCell[] Cells)>();
        var data = new List<(int Number, ReadableCell[] Cells)>();
        foreach (var (number, all) in rows)
        {
            var cells = all.Where(cell => !annotations.Contains(cell.Column)).ToArray();
            if (cells.Length == 0) continue;
            if (data.Count == 0 && !cells.Any(IsSubstantiveValue))
            {
                if (cells.Any(cell => !IsValueCell(cell)))
                {
                    headers.Add((number, cells));
                    if (headers.Count > 2) return false;
                }
                continue;
            }
            data.Add((number, cells));
        }
        if (data.Count == 0 || otherRows.Count == 0 || data[0].Number < otherRows[0] || data[^1].Number > otherRows[^1]) return false;
        // Only the other side's rows beside this block count: a table stacked above or below in the
        // same columns, or a list lower on the sheet, says nothing about it.
        var beside = RowsBeside(other, headers.Count > 0 ? headers[0].Number : data[0].Number, data[^1].Number);
        var besideCells = beside.ToDictionary(row => row.Number, row => row.Cells);
        // Out of step with the other side, as its values show: a header of its own beside a data row of
        // the other side (labels with values, not emphasised like a header: a table that starts lower
        // down), or values that start beside the other side's first row while that row heads its
        // values (one that starts higher up): no values in it and values in the row below, or, for a
        // header of several columns, emphasised over a plain row below. A row the other side merely
        // lacks, a second header row, a caption or date in the corner, a year in a header, bold row
        // labels of a statement, or a category row without a count inside a table is neither.
        static bool Emphasised(ReadableCell cell) => cell.IsBold || cell.HasFill;
        if (headers.Any(header => besideCells.TryGetValue(header.Number, out var cells) && cells.Length >= 2 &&
                cells.Any(IsSubstantiveValue) && !cells.All(Emphasised))) return false;
        if (headers.Count > 0 && beside.Length > 1 && beside[0].Number == data[0].Number && !beside[0].Cells.Any(IsSubstantiveValue) &&
            (beside[1].Cells.Any(IsSubstantiveValue) ||
             beside[0].Cells.Length >= 2 && beside[0].Cells.All(Emphasised) && !beside[1].Cells.All(Emphasised)))
            return false;
        var cellsOfData = data.SelectMany(row => row.Cells).ToArray();
        var firstColumn = cellsOfData.Min(cell => cell.Column);
        if (cellsOfData.Any(cell => cell.Column == firstColumn && !IsValueCell(cell))) return false;
        if (cellsOfData.Count(IsValueCell) * 4 < cellsOfData.Length * 3) return false;
        // A first column headed as a period whose entries run through regular periods keys rows of its
        // own. Beside another table with values of its own, that is a second table by year, month or
        // date, not values of the other's rows; beside text only, the periods key one table.
        var keys = data.Select(row => row.Cells.FirstOrDefault(cell => cell.Column == firstColumn)).ToArray();
        return !(beside.Any(row => row.Cells.Any(IsSubstantiveValue)) && keys.Length >= 3 && keys.All(cell => cell is not null) &&
            cellsOfData.Any(cell => cell.Column != firstColumn) &&
            headers.Count > 0 && headers[^1].Cells.FirstOrDefault(cell => cell.Column <= firstColumn && cell.MaxColumn >= firstColumn) is { } keyHeader &&
            PeriodHeaderRegex().IsMatch(PlainText(keyHeader.Text).Trim()) && IsPeriodSequence(keys!));
    }

    // The rows of a side next to rows first..last: its runs of rows (two blank rows or more end a run)
    // that overlap them.
    private static (int Number, ReadableCell[] Cells)[] RowsBeside(IReadOnlyList<(int Number, ReadableCell[] Cells)> side, int first, int last)
    {
        var rows = side.Where(row => row.Cells.Length > 0).OrderBy(row => row.Number).ToArray();
        var beside = new List<(int Number, ReadableCell[] Cells)>();
        for (var start = 0; start < rows.Length;)
        {
            var end = start;
            while (end + 1 < rows.Length && rows[end + 1].Number - rows[end].Number <= 2) end++;
            if (rows[start].Number <= last && rows[end].Number >= first) beside.AddRange(rows[start..(end + 1)]);
            start = end + 1;
        }
        return beside.ToArray();
    }

    // Periods with a constant step, newest first or last: years (2024, 2025), months (4月, 5月,
    // 2024年4月, or dates on the same day or the last day of each month), or dates (2026-04-01,
    // 4月8日 ...). Full-width digits are read as digits. Repeated or unordered entries are data.
    private static bool IsPeriodSequence(IReadOnlyList<ReadableCell> cells)
    {
        var periods = cells.Select(cell => TryReadPeriod(PlainText(DisplayOf(cell)).Normalize(NormalizationForm.FormKC).Trim(), out var unit, out var index)
            ? (Unit: unit, Index: index) : ((string Unit, long Index)?)null).ToArray();
        if (periods.Any(period => period is null) || periods.Select(period => period!.Value.Unit).Distinct().Count() != 1) return false;
        var unitOfAll = periods[0]!.Value.Unit;
        bool Regular(IReadOnlyList<long> indexes, bool aroundTheYear)
        {
            var steps = indexes.Zip(indexes.Skip(1), (previous, next) => aroundTheYear ? (next - previous + 12) % 12 : next - previous).Distinct().ToArray();
            return steps.Length == 1 && steps[0] != 0;
        }
        var indexes = periods.Select(period => period!.Value.Index).ToArray();
        if (Regular(indexes, unitOfAll == "month-of-year")) return true;
        // Monthly dates are a whole month apart, which is not a constant number of days.
        if (unitOfAll != "day") return false;
        var days = indexes.Select(day => new DateTime(day * TimeSpan.TicksPerDay)).ToArray();
        return (days.All(day => day.Day == days[0].Day) || days.All(day => day.Day == DateTime.DaysInMonth(day.Year, day.Month))) &&
            Regular(days.Select(day => day.Year * 12L + day.Month).ToArray(), false);
    }

    private static bool TryReadPeriod(string text, out string unit, out long index)
    {
        (unit, index) = (string.Empty, 0);
        // Only ASCII digits from here on: int.Parse does not read other digits that \d matches.
        if (text.Any(character => char.IsDigit(character) && character is < '0' or > '9')) return false;
        if (Regex.Match(text, @"^(\d{4})$") is { Success: true } year && int.Parse(year.Groups[1].Value, CultureInfo.InvariantCulture) is >= 1900 and <= 2100 and var y)
            (unit, index) = ("year", y);
        else if (Regex.Match(text, @"^(\d{4})年(\d{1,2})月$") is { Success: true } yearMonth)
            (unit, index) = ("month", long.Parse(yearMonth.Groups[1].Value, CultureInfo.InvariantCulture) * 12 + int.Parse(yearMonth.Groups[2].Value, CultureInfo.InvariantCulture));
        else if (Regex.Match(text, @"^(\d{1,2})月$") is { Success: true } month && int.Parse(month.Groups[1].Value, CultureInfo.InvariantCulture) is >= 1 and <= 12 and var m)
            (unit, index) = ("month-of-year", m);
        else if (Regex.Match(text, @"^(?:(\d{4})年)?(\d{1,2})月(\d{1,2})日$") is { Success: true } japaneseDate &&
                 DateTime.TryParseExact($"{(japaneseDate.Groups[1].Success ? japaneseDate.Groups[1].Value : "2000")}-{int.Parse(japaneseDate.Groups[2].Value, CultureInfo.InvariantCulture):D2}-" +
                     $"{int.Parse(japaneseDate.Groups[3].Value, CultureInfo.InvariantCulture):D2}", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var japaneseDay))
            // A month and day without a year ("4月1日") is placed in a leap year, so 2月29日 parses too.
            (unit, index) = (japaneseDate.Groups[1].Success ? "day" : "day-of-year", japaneseDay.Ticks / TimeSpan.TicksPerDay);
        else if (Regex.Match(text, @"^(\d{4})[-/](\d{1,2})(?:[-/](\d{1,2}))?$") is { Success: true } date)
        {
            var (dateYear, dateMonth) = (int.Parse(date.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(date.Groups[2].Value, CultureInfo.InvariantCulture));
            if (!date.Groups[3].Success)
                (unit, index) = dateMonth is >= 1 and <= 12 ? ("month", dateYear * 12L + dateMonth) : (string.Empty, 0);
            else if (DateTime.TryParseExact($"{dateYear:D4}-{dateMonth:D2}-{int.Parse(date.Groups[3].Value, CultureInfo.InvariantCulture):D2}", "yyyy-MM-dd",
                         CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
                (unit, index) = ("day", day.Ticks / TimeSpan.TicksPerDay);
        }
        return unit.Length > 0;
    }

    // A value other than a placeholder dash: something that shows the column holds data.
    private static bool IsSubstantiveValue(ReadableCell cell) =>
        IsValueCell(cell) && !PlaceholderRegex().IsMatch(PlainText(DisplayOf(cell)).Trim());

    // A computed formula result is a value (see ReadableCell.IsComputed); other cells, including text
    // a formula looked up, count by what they show.
    private static bool IsValueCell(ReadableCell cell)
    {
        if (cell.IsNumeric || cell.IsOverlay || cell.IsComputed) return true;
        var text = PlainText(DisplayOf(cell)).Trim();
        return ValueTextRegex().IsMatch(text) || DateValueRegex().IsMatch(text);
    }

    private static string DisplayOf(ReadableCell cell) => cell.Display ?? cell.Text;

    private static string ColumnLetters(int column)
    {
        var letters = new StringBuilder();
        for (; column > 0; column = (column - 1) / 26) letters.Insert(0, (char)('A' + (column - 1) % 26));
        return letters.ToString();
    }

    private static string CellRange(int startColumn, int startRow, int endColumn, int endRow) =>
        $"{ColumnLetters(startColumn)}{startRow}:{ColumnLetters(endColumn)}{endRow}";

    private static IEnumerable<RowFragment> SplitRow(SheetRow row, IReadOnlyList<int> boundaries, IReadOnlySet<int> judged)
    {
        // Preserve compact rows as one logical table row. This keeps a four-column
        // header aligned with its following data row even when the source uses wide
        // visual spacing between cells (a common revision-history layout).
        // P-Overlay (XLSX): see the ReadableCell.IsOverlay comment -- a row an overlay touched
        // must never split into an orphaned label fragment and an orphaned marker fragment.
        var crossesJudgedGap = Enumerable.Range(1, Math.Max(0, row.Cells.Count - 1)).Any(index =>
            judged.Any(boundary => row.Cells[index - 1].MaxColumn < boundary && row.Cells[index].Column >= boundary));
        if (boundaries.Count == 0 || row.Cells.Any(cell => cell.IsOverlay) ||
            row.Cells.Count == 1 && row.Cells[0].MaxColumn > row.Cells[0].Column ||
            !crossesJudgedGap && row.Cells.Count <= 4 && !Enumerable.Range(1, row.Cells.Count - 1)
                .Any(index => row.Cells[index].Column - row.Cells[index - 1].MaxColumn <= 2))
        {
            yield return new(row, row.Cells);
            yield break;
        }
        var splits = Enumerable.Range(1, row.Cells.Count - 1)
            .Where(index => boundaries.Any(boundary => row.Cells[index - 1].MaxColumn < boundary && row.Cells[index].Column >= boundary))
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
            if (!cell.IsNumeric && TryGetSectionHeading(text, out var heading, out var level)) WriteHeading(output, level, EscapeLiteral(heading));
            else if (NoteRegex().IsMatch(text)) WriteQuote(output, EscapeLiteral(text));
            else if (LooksLikeCode(text)) WriteCodeBlock(output, text);
            else WriteParagraph(output, EscapeLiteral(text));
            return;
        }

        var sectionCells = meaningfulCells.Where(cell => TryGetSectionHeading(cell.Text, out _, out _)).ToList();
        if (sectionCells.Count > 0)
        {
            var ordinaryCells = meaningfulCells.Except(sectionCells).ToList();
            if (ordinaryCells.Count > 0)
                output.Append("- ").AppendLine(string.Join(" — ", ordinaryCells.Select(cell => EscapedInlineText(cell.Text)))).AppendLine();
            foreach (var cell in sectionCells)
            {
                _ = TryGetSectionHeading(cell.Text, out var heading, out var level);
                WriteHeading(output, level, EscapeLiteral(heading));
            }
            return;
        }

        if (meaningfulCells.All(cell => SelfLabeledRegex().IsMatch(cell.Text)))
        {
            WriteQuote(output, string.Join(" · ", meaningfulCells.Select(cell => EscapeLiteral(cell.Text))));
            return;
        }

        output.Append("- ").AppendLine(string.Join(" — ", meaningfulCells.Select(cell => EscapedInlineText(cell.Text)))).AppendLine();
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
            var row = ReadRows(partition).FirstOrDefault();
            if (row is null) continue;
            // The first cell of a header row ("ID", "Item", ...) names a column, not the document. In
            // a header the unmerged first cell has a neighbour right next to it, or within one spacer
            // column when more headers follow. A title stands alone, is merged across, or is followed
            // only by a note or number further away ("見積書 | | No.123", "Title ... Updated 2026-08-23").
            var first = row.Cells[0];
            var gap = row.Cells.Count > 1 ? row.Cells[1].Column - first.MaxColumn : int.MaxValue;
            if (first.MaxColumn == first.Column && (gap == 1 || gap == 2 && row.Cells.Count >= 3)) return null;
            if (PlainText(first.Text).Length <= 140) return PlainText(first.Text);
        }
        return null;
    }

    // D08 readability pass: DocxAdapter.AddTable emits a nested w:tbl as an independent sibling
    // Table node (nested_table_parent/_row/_column extensions record its host cell) so the F1
    // restore path keeps the outer table's tr/tc shape untouched. Left alone, that sibling prints
    // as a second, disconnected table with no indication of which cell it came from. This pass
    // instead folds every Table node whose nested_table_parent resolves — a Table node present in
    // the SAME partition, with nested_table_row/_column in range of that parent's own
    // TableNodeContent.Rows — into its host cell's text, and excludes it from top-level rendering
    // (FoldedChildIds is checked next to the existing AggregatedSections.SkipIds). A node that
    // cannot be resolved this way — parent missing (a different partition, or dropped by the
    // content policy), or a row/column out of range — is deliberately left out of FoldedChildIds
    // so it still renders as an independent table; nothing is ever silently dropped. A chain
    // deeper than maxDepth (defensive: also covers an accidental reference cycle) stops folding
    // there and renders that node standalone too, the same as an unresolved reference.
    private static NestedTableFolds ComputeNestedTableFolds(IReadOnlyList<DocumentPartition> partitions)
    {
        const int maxDepth = 8;
        var foldedChildIds = new HashSet<string>(StringComparer.Ordinal);
        var childrenByParentId = new Dictionary<string, List<NestedTableSlot>>(StringComparer.Ordinal);
        foreach (var partition in partitions)
        {
            var nodesById = new Dictionary<string, DocumentNode>(StringComparer.Ordinal);
            foreach (var node in partition.Nodes) nodesById[node.Id] = node;
            var depthCache = new Dictionary<string, int>(StringComparer.Ordinal);

            // Depth of `candidate` in its nested_table_parent chain (0 = not itself resolved as
            // nested), or null when it cannot fold: no parent extension, an unresolved/foreign/
            // out-of-range reference, a reference cycle, or a chain longer than maxDepth. Recurses
            // into the parent first so a grandchild only resolves once its whole ancestor chain
            // does, keeping multi-level nesting consistent; memoized per partition since node ids
            // are unique document-wide (DocumentGraph.NormalizeNodeIds).
            int? ResolveDepth(DocumentNode candidate, HashSet<string> visiting)
            {
                if (depthCache.TryGetValue(candidate.Id, out var cached)) return cached;
                var parentId = ExtensionString(candidate, "nested_table_parent");
                if (parentId is null) { depthCache[candidate.Id] = 0; return 0; }
                if (!visiting.Add(candidate.Id)) return null; // defensive cycle guard
                try
                {
                    if (!nodesById.TryGetValue(parentId, out var parentNode) ||
                        parentNode.Content is not TableNodeContent parentTable) return null;
                    var row = ExtensionInt(candidate, "nested_table_row");
                    var column = ExtensionInt(candidate, "nested_table_column");
                    if (row is not { } r || r < 0 || r >= parentTable.Rows.Count ||
                        column is not { } c || c < 0 || c >= parentTable.Rows[r].Count) return null;
                    if (ResolveDepth(parentNode, visiting) is not { } parentDepth || parentDepth + 1 > maxDepth) return null;
                    depthCache[candidate.Id] = parentDepth + 1;
                    return parentDepth + 1;
                }
                finally { visiting.Remove(candidate.Id); }
            }

            foreach (var node in partition.Nodes)
            {
                if (node.Kind != NodeKind.Table || node.Content is not TableNodeContent) continue;
                var parentId = ExtensionString(node, "nested_table_parent");
                if (parentId is null || ResolveDepth(node, new HashSet<string>(StringComparer.Ordinal)) is null) continue;
                foldedChildIds.Add(node.Id);
                // The line offset is authoritative: the host text is split by "\n" below, and a
                // paragraph holding a line break contributes more than one line. The paragraph
                // offset only serves graphs written before the line offset existed.
                var slot = new NestedTableSlot(ExtensionInt(node, "nested_table_row")!.Value, ExtensionInt(node, "nested_table_column")!.Value, node,
                    ExtensionInt(node, "nested_table_line_offset") ?? ExtensionInt(node, "nested_table_paragraph_offset"));
                if (!childrenByParentId.TryGetValue(parentId, out var slots)) childrenByParentId[parentId] = slots = new List<NestedTableSlot>();
                slots.Add(slot);
            }
        }
        return new NestedTableFolds(foldedChildIds, childrenByParentId);
    }

    // Recursively folds any resolved nested-table children of `nodeId` into their host cell's text
    // (grandchildren first, so a multi-level nest reads as one flattened block in the outermost
    // cell) and returns a new grid; `rows` itself is never mutated, and a node with no resolved
    // children returns it unchanged. Each nested row renders as its cells joined by " / "; nested
    // rows join with "\n" — TableText (used by WriteTableRow below) turns that into "<br>" exactly
    // like any other multi-line cell — appended after the host cell's own text with one separating
    // "\n" only when that text is non-empty. Cell text is left unescaped here: WriteTableRow's
    // existing TableText call is what escapes the merged text, same as every ordinary cell, so
    // folded content never bypasses or duplicates that escaping.
    private static IReadOnlyList<IReadOnlyList<TableCell>> FoldNestedTableRows(
        string nodeId, IReadOnlyList<IReadOnlyList<TableCell>> rows, NestedTableFolds folds)
    {
        if (!folds.ChildrenByParentId.TryGetValue(nodeId, out var slots) || slots.Count == 0) return rows;
        var grid = rows.Select(row => row.ToArray()).ToArray();
        // All nested tables of one host cell are placed together, against that cell's ORIGINAL
        // "\n"-separated lines: nested_table_line_offset counts the lines of the host's own text
        // that came before the table in the source (a paragraph with a line break spans several),
        // so "before / nested / after" keeps its order. A child without an offset (older graphs)
        // still goes after the host's text.
        foreach (var cellSlots in slots.GroupBy(slot => (slot.Row, slot.Column)))
        {
            var (row, column) = cellSlots.Key;
            if (row < 0 || row >= grid.Length || column < 0 || column >= grid[row].Length) continue;
            var hostCell = grid[row][column];
            var paragraphs = string.IsNullOrEmpty(hostCell.Text) ? [] : hostCell.Text.Split('\n').ToList();
            var placed = cellSlots.Select(slot =>
            {
                var childRows = slot.Child.Content is TableNodeContent childTable ? childTable.Rows : Array.Empty<IReadOnlyList<TableCell>>();
                var foldedChildRows = FoldNestedTableRows(slot.Child.Id, childRows, folds);
                var nestedText = string.Join("\n", foldedChildRows.Select(nestedRow => string.Join(" / ", nestedRow.Select(cell => cell.Text))));
                var offset = slot.ParagraphOffset is { } known ? Math.Clamp(known, 0, paragraphs.Count) : paragraphs.Count;
                return (Offset: offset, Text: nestedText);
            }).OrderBy(item => item.Offset).ToArray();
            var lines = new List<string>();
            var next = 0;
            for (var index = 0; index <= paragraphs.Count; index++)
            {
                while (next < placed.Length && placed[next].Offset == index) lines.Add(placed[next++].Text);
                if (index < paragraphs.Count) lines.Add(paragraphs[index]);
            }
            grid[row][column] = hostCell with { Text = string.Join("\n", lines.Where(line => line.Length > 0)) };
        }
        return grid.Select(row => (IReadOnlyList<TableCell>)row).ToArray();
    }

    private sealed record NestedTableSlot(int Row, int Column, DocumentNode Child, int? ParagraphOffset = null);

    private sealed record NestedTableFolds(HashSet<string> FoldedChildIds, IReadOnlyDictionary<string, List<NestedTableSlot>> ChildrenByParentId);

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
        foreach (var note in noteRows) WriteParagraph(output, EscapeLiteral(note.Text));
    }

    // P-Overlay: schedule-arrow/bar/marker/line/label shapes that PptxAdapter has already resolved
    // onto a table's grid cells (table_overlays extension) get folded into the cell text here, so
    // the reader sees "設計 ━━" beside the right column instead of a disconnected paragraph after
    // the table. This runs on the original a:tr row/column indices -- the same indices the
    // extension uses -- before SplitFullWidthNoteRows (inside WriteArbitraryTable) can drop or
    // renumber rows.
    private static IReadOnlyList<IReadOnlyList<TableCell>> ApplyTableOverlays(DocumentNode node, IReadOnlyList<IReadOnlyList<TableCell>> rows)
    {
        if (!HasExtension(node, "table_overlays")) return rows;
        var overlays = ReadTableOverlays(node);
        if (overlays.Count == 0) return rows;
        if (!TableGrid.TryCreate(new TableNodeContent(rows), out var grid, out _)) return rows;

        var mutableRows = rows.Select(row => row.ToArray()).ToArray();
        foreach (var overlay in overlays)
        {
            if (overlay.EndRow < overlay.StartRow || overlay.EndColumn < overlay.StartColumn) continue;
            var rowFrom = Math.Max(0, overlay.StartRow);
            var rowTo = Math.Min(grid.RowCount - 1, overlay.EndRow);
            var columnFrom = Math.Max(0, overlay.StartColumn);
            var columnTo = Math.Min(grid.ColumnCount - 1, overlay.EndColumn);
            if (rowFrom > rowTo || columnFrom > columnTo) continue; // wholly out of the grid: ignored, not an error

            // F5: glyph selection (head/tail vs. body) must key off the range actually rendered,
            // not the extension's raw indices -- otherwise an arrow clamped at the grid edge loses
            // its arrowhead (isLastColumn/isLastRow compares against an EndColumn/EndRow that was
            // never actually reached) instead of ending in it one column/row earlier.
            var clampedOverlay = overlay with { StartRow = rowFrom, EndRow = rowTo, StartColumn = columnFrom, EndColumn = columnTo };

            // One overlay can cover several grid slots that all resolve to the same merged origin
            // cell (rowspan/colspan); it writes that cell once, using its first slot's marker.
            var perOrigin = new Dictionary<(int Row, int Column), string>();
            for (var row = rowFrom; row <= rowTo; row++)
            for (var column = columnFrom; column <= columnTo; column++)
            {
                var slot = grid.Rows[row][column];
                var originKey = (slot.OriginRow, slot.OriginCellIndex);
                if (perOrigin.ContainsKey(originKey)) continue;
                var isLabelSlot = row == clampedOverlay.StartRow && column == clampedOverlay.StartColumn;
                var fragment = OverlayFragment(clampedOverlay, OverlayGlyph(clampedOverlay, row, column), isLabelSlot);
                if (fragment.Length == 0) continue;
                perOrigin[originKey] = fragment;
            }
            foreach (var (origin, fragment) in perOrigin)
            {
                var cell = mutableRows[origin.Row][origin.Column];
                mutableRows[origin.Row][origin.Column] = cell with { Text = cell.Text.Length == 0 ? fragment : cell.Text + "\n" + fragment };
            }
        }
        return mutableRows.Select(row => (IReadOnlyList<TableCell>)row).ToArray();
    }

    /// <summary>The glyph for one grid slot of an overlay, ignoring any label text (see
    /// OverlayFragment). Horizontal axis varies the glyph by column position (c1/c2 ends);
    /// vertical axis varies it by row position (r1/r2 ends) and repeats per column for the unusual
    /// case of a vertical overlay spanning more than one column.</summary>
    private static string OverlayGlyph(TableOverlay overlay, int row, int column) =>
        ApplyOverlayLineStyle(SolidOverlayGlyph(overlay, row, column), overlay.LineStyle);

    // A dashed or dotted source stroke keeps its direction and arrowheads but swaps the stroke
    // characters, so the difference survives in every covered cell (see WriteOverlayLineStyleNotes
    // for the legend written after the table).
    private static string ApplyOverlayLineStyle(string glyph, string? lineStyle) => lineStyle switch
    {
        VisualLineStyles.Dashed => glyph.Replace('━', '┅').Replace('─', '┄').Replace('│', '┆'),
        VisualLineStyles.Dotted => glyph.Replace('━', '⋯').Replace('─', '⋯').Replace('│', '⋮'),
        _ => glyph,
    };

    private static string SolidOverlayGlyph(TableOverlay overlay, int row, int column)
    {
        if (StringComparer.Ordinal.Equals(overlay.Kind, "label")) return string.Empty;
        if (StringComparer.Ordinal.Equals(overlay.Axis, "vertical"))
        {
            var single = overlay.StartRow == overlay.EndRow;
            var isFirst = row == overlay.StartRow;
            var isLast = row == overlay.EndRow;
            return overlay.Kind switch
            {
                "arrow" => overlay.Direction switch
                {
                    "down" => single ? "▼" : isLast ? "▼" : "│",
                    "up" => single ? "▲" : isFirst ? "▲" : "│",
                    "both" => single ? "▲▼" : isFirst ? "▲" : isLast ? "▼" : "│",
                    _ => "│",
                },
                "marker" => OverlayMarkerGlyph(overlay.ShapePreset),
                _ => "│", // bar / line
            };
        }
        var singleColumn = overlay.StartColumn == overlay.EndColumn;
        var isFirstColumn = column == overlay.StartColumn;
        var isLastColumn = column == overlay.EndColumn;
        return overlay.Kind switch
        {
            "arrow" => overlay.Direction switch
            {
                "right" => singleColumn ? "━▶" : isLastColumn ? "━━▶" : "━━",
                "left" => singleColumn ? "◀━" : isFirstColumn ? "◀━━" : "━━",
                "both" => singleColumn ? "◀━▶" : isFirstColumn ? "◀━━" : isLastColumn ? "━━▶" : "━━",
                _ => singleColumn ? "━" : "━━",
            },
            "bar" => singleColumn ? "━" : "━━",
            "line" => singleColumn ? "─" : "──",
            "marker" => OverlayMarkerGlyph(overlay.ShapePreset),
            _ => singleColumn ? "━" : "━━",
        };
    }

    // F6: the adapter classifies ShapePreset case-insensitively (ToLowerInvariant, see
    // IsOverlayMarkerPreset/ClassifyTableOverlay) but stores the shape's original-case preset text
    // in the overlay -- lower-case here too so e.g. "Ellipse" still resolves to its glyph.
    private static string OverlayMarkerGlyph(string? shapePreset) => shapePreset?.ToLowerInvariant() switch
    {
        "ellipse" => "●",
        "triangle" => "▲",
        _ => "◆",
    };

    /// <summary>Prefixes the overlay's label text (only at its first covered slot) onto the glyph,
    /// separated by one space; a bare label (Kind "label", no glyph) is just the text. Both flow
    /// through the same TableText/EscapeLiteral path as ordinary cell text afterwards -- no extra
    /// escaping is applied here.</summary>
    private static string OverlayFragment(TableOverlay overlay, string glyph, bool isLabelSlot)
    {
        var label = isLabelSlot ? overlay.Text : string.Empty;
        if (label.Length == 0) return glyph;
        return glyph.Length == 0 ? label : label + " " + glyph;
    }

    private sealed record TableOverlay(
        string ShapeId, string Text, string Kind, string Direction, string Axis,
        int StartRow, int EndRow, int StartColumn, int EndColumn, string? ShapePreset, string? LineStyle = null);

    private static List<TableOverlay> ReadTableOverlays(DocumentNode node)
    {
        if (node.Extensions is null || !node.Extensions.TryGetValue("table_overlays", out var raw) ||
            raw.ValueKind != JsonValueKind.Array) return [];
        var overlays = new List<TableOverlay>();
        foreach (var item in raw.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            // F7: every other JSON reader in this file accepts both PascalCase (the default
            // JsonSerializer.SerializeToElement casing PptxAdapter writes) and camelCase.
            var startRow = JsonInt(item, "StartRow", "startRow") ?? 0;
            var startColumn = JsonInt(item, "StartColumn", "startColumn") ?? 0;
            overlays.Add(new TableOverlay(
                JsonString(item, "ShapeId", "shapeId") ?? string.Empty,
                JsonString(item, "Text", "text") ?? string.Empty,
                JsonString(item, "Kind", "kind") ?? string.Empty,
                JsonString(item, "Direction", "direction") ?? "none",
                JsonString(item, "Axis", "axis") ?? "horizontal",
                startRow, JsonInt(item, "EndRow", "endRow") ?? startRow,
                startColumn, JsonInt(item, "EndColumn", "endColumn") ?? startColumn,
                JsonString(item, "ShapePreset", "shapePreset"),
                OverlayLineStyle(item)));
        }
        return overlays;
    }

    private static string? OverlayLineStyle(JsonElement item) =>
        JsonString(item, "LineStyle", "lineStyle") is { } style && VisualLineStyles.IsKnown(style) ? style : null;

    // The legend that turns stroke glyphs back into words. Cell glyphs alone keep the dashed and
    // dotted shapes distinct; this note names what each styled shape is and where it lies, so a
    // reader (or an AI) can relate it to the document's own legend (for example planned vs. fixed).
    private static void WriteOverlayLineStyleNotes(StringBuilder output, DocumentNode node, IReadOnlyList<IReadOnlyList<TableCell>> rows)
    {
        if (!HasExtension(node, "table_overlays")) return;
        var styled = ReadTableOverlays(node).Where(IsStyledStroke).ToArray();
        if (styled.Length == 0) return;
        TableGrid.TryCreate(new TableNodeContent(rows), out var grid, out _);
        string Label(int row, int column)
        {
            if (grid is null || row < 0 || column < 0 || row >= grid.RowCount || column >= grid.ColumnCount) return string.Empty;
            var text = grid.Rows[row][column].Origin.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')[0].Trim();
            return text.Length > 24 ? text[..24] + "…" : text;
        }
        string RowName(int row) => Label(row, 0) is { Length: > 0 } text ? $"「{text}」" : $"{row + 1}行目";
        string ColumnName(int column) => Label(0, column) is { Length: > 0 } text ? $"「{text}」" : $"{column + 1}列目";
        string Span(Func<int, string> name, int start, int end) => start == end ? name(start) : name(start) + "〜" + name(end);
        WriteLineStyleNotes(output, styled, overlay => StringComparer.Ordinal.Equals(overlay.Axis, "vertical")
            ? $"列{Span(ColumnName, overlay.StartColumn, overlay.StartColumn)}、行{Span(RowName, overlay.StartRow, overlay.EndRow)}"
            : $"行{Span(RowName, overlay.StartRow, overlay.EndRow)}、列{Span(ColumnName, overlay.StartColumn, overlay.EndColumn)}");
    }

    // XLSX overlays are positioned by worksheet cell, so the note names cells in A1 notation.
    private static void WriteSheetOverlayLineStyleNotes(StringBuilder output, DocumentPartition partition)
    {
        var styled = partition.Nodes.Where(node => node.Kind == NodeKind.Shape && HasExtension(node, "sheet_overlay"))
            .Select(ReadSheetOverlay).OfType<TableOverlay>().Where(IsStyledStroke).ToArray();
        if (styled.Length == 0) return;
        static string CellName(int row, int column)
        {
            var letters = string.Empty;
            for (var value = Math.Max(1, column); value > 0; value = (value - 1) / 26)
                letters = (char)('A' + (value - 1) % 26) + letters;
            return letters + Math.Max(1, row).ToString(CultureInfo.InvariantCulture);
        }
        WriteLineStyleNotes(output, styled, overlay =>
        {
            var start = CellName(overlay.StartRow, overlay.StartColumn);
            var end = CellName(overlay.EndRow, overlay.EndColumn);
            return start == end ? $"セル {start}" : $"セル {start}〜{end}";
        });
    }

    private static bool IsStyledStroke(TableOverlay overlay) =>
        overlay.LineStyle is not null && overlay.Kind is not ("label" or "marker");

    private static void WriteLineStyleNotes(StringBuilder output, IReadOnlyList<TableOverlay> styled, Func<TableOverlay, string> location)
    {
        const int maxListed = 20;
        var legend = new List<string>();
        if (styled.Any(overlay => overlay.LineStyle == VisualLineStyles.Dashed)) legend.Add("┅ ┄ ┆ は破線");
        if (styled.Any(overlay => overlay.LineStyle == VisualLineStyles.Dotted)) legend.Add("⋯ ⋮ は点線");
        output.Append("> 線種の注記: 表中の ").Append(string.Join("、", legend))
            .AppendLine("で描かれた図形です（原本の線種を記号で区別しています）。");
        foreach (var overlay in styled.Take(maxListed))
            output.Append("> - ").Append(LineStyleName(overlay.LineStyle)).Append("の").Append(OverlayKindName(overlay))
                .Append(": ").AppendLine(EscapeLiteral(location(overlay)));
        if (styled.Count > maxListed)
            output.Append("> - ほか ").Append((styled.Count - maxListed).ToString(CultureInfo.InvariantCulture)).AppendLine(" 件");
        output.AppendLine();
    }

    internal static string LineStyleName(string? lineStyle) => lineStyle switch
    {
        VisualLineStyles.Dashed => "破線",
        VisualLineStyles.Dotted => "点線",
        _ => "実線",
    };

    private static string OverlayKindName(TableOverlay overlay) => overlay.Kind switch
    {
        "arrow" => overlay.Direction switch
        {
            "right" => "右向き矢印",
            "left" => "左向き矢印",
            "up" => "上向き矢印",
            "down" => "下向き矢印",
            "both" => "両矢印",
            _ => "矢印",
        },
        "bar" => "バー",
        "line" => "線",
        _ => "図形",
    };

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
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal).Trim();
        var escaped = EscapeLineStarts(normalized);
        output.AppendLine(escaped.Replace("\n", "  \n", StringComparison.Ordinal)).AppendLine();
    }

    private static bool LooksLikeCode(string text)
    {
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal).TrimStart();
        return normalized.Contains('\n') && (normalized.StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("GET ", StringComparison.OrdinalIgnoreCase) || normalized.StartsWith("POST ", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith('{') || normalized.StartsWith('[') || text.Split('\n').Any(line => line.StartsWith(' ') || line.StartsWith('\t')));
    }

    private static void WriteCodeBlock(StringBuilder output, string text)
    {
        var trimmed = text.Trim();
        var fence = CodeFence(trimmed);
        output.AppendLine(fence).AppendLine(trimmed).AppendLine(fence).AppendLine();
    }

    private static void WriteInference(StringBuilder output, string message) =>
        output.Append("<!-- inferred: ").Append(message.Replace("--", "—", StringComparison.Ordinal)).AppendLine(" -->");

    private static void WriteQuote(StringBuilder output, string text)
    {
        foreach (var line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal).Split('\n'))
            output.Append("> ").AppendLine(line.Trim());
        output.AppendLine();
    }

    /// <summary>The single OCR review rule shared by the readable detail table and export
    /// summaries: confidence below 80% or not reported by the engine.</summary>
    public static bool OcrRegionNeedsReview(JsonElement region) =>
        !(region.ValueKind == JsonValueKind.Object && region.TryGetProperty("confidence", out var c) &&
          c.ValueKind == JsonValueKind.Number && c.TryGetDouble(out var score) && double.IsFinite(score) && score >= .8);

    private void WriteOcrDetails(StringBuilder output, string text, DocumentNode node, DocumentPartition partition)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        output.AppendLine("<details class=\"ocr-extraction\">")
            .AppendLine("<summary>OCR抽出テキスト（クリックで展開）</summary>")
            .AppendLine();
        foreach (var line in text.Trim().Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal).Split('\n'))
            output.Append("> ").Append(line.Trim()).AppendLine("  ");
        output.AppendLine();
        if (node.Extensions?.TryGetValue("ocr_regions", out var regions) == true && regions.ValueKind == JsonValueKind.Array)
        {
            var image = partition.Nodes.FirstOrDefault(n => n.Id == node.ParentId)?.Content as ReferenceNodeContent;
            var allRegions = regions.EnumerateArray().ToArray();
            var needsReview = allRegions.Where(OcrRegionNeedsReview).ToArray();
            var shown = options.OcrReview switch
            {
                OcrReviewMode.All => allRegions,
                OcrReviewMode.Summary => [],
                _ => needsReview,
            };
            output.AppendLine("OCR照合情報：信頼度はエンジンの推定値であり、正解率ではありません。識別子・品番・数値は原画像と照合してください。")
                .AppendLine($"全{allRegions.Length}件、低信頼・信頼度不明{needsReview.Length}件、詳細表示{shown.Length}件。")
                .AppendLine();
            if (shown.Length > 0)
                output.AppendLine("| 行 | 認識文字 | 信頼度 | 原画像の位置 |")
                    .AppendLine("| --- | --- | --- | --- |");
            foreach (var region in shown)
            {
                var word = region.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
                var confidence = region.TryGetProperty("confidence", out var c) && c.ValueKind == JsonValueKind.Number && c.TryGetDouble(out var score) && double.IsFinite(score)
                    ? score : (double?)null;
                var label = confidence is { } value ? value.ToString("P0", CultureInfo.InvariantCulture) + (value < .8 ? "（要照合）" : "") : "未提供";
                var position = "未提供";
                if (region.TryGetProperty("bounding_box", out var bbox) && bbox.ValueKind == JsonValueKind.Object &&
                    bbox.Deserialize<Geometry>() is { } box)
                {
                    position = FormattableString.Invariant($"{box.CoordinateSpace}: x={box.X:0.##}, y={box.Y:0.##}, w={box.Width:0.##}, h={box.Height:0.##}");
                    if (image is not null && box.CoordinateSpace is "image-pixels" or "vision-normalized-bottom-left" &&
                        !image.Reference.StartsWith("data:", StringComparison.Ordinal))
                    {
                        var fragment = box.CoordinateSpace == "image-pixels"
                            ? FormattableString.Invariant($"#xywh=pixel:{box.X:0},{box.Y:0},{box.Width:0},{box.Height:0}")
                            : FormattableString.Invariant($"#xywh=percent:{box.X*100:0.##},{(1-box.Y-box.Height)*100:0.##},{box.Width*100:0.##},{box.Height*100:0.##}");
                        position = "[" + EscapeLiteral(position) + "](" + MarkdownPathEncoder.Encode(image.Reference) + fragment + ")";
                    }
                    else position = EscapeLiteral(position);
                }
                var line = region.TryGetProperty("line_number", out var lineNumber) && lineNumber.ValueKind == JsonValueKind.Number && lineNumber.TryGetInt32(out var number)
                    ? number.ToString(CultureInfo.InvariantCulture) : "未提供";
                output.Append("| ").Append(line).Append(" | ").Append(TableText(word)).Append(" | ").Append(label).Append(" | ").Append(position).AppendLine(" |");
            }
            output.AppendLine();
        }
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
            output.Append("**").Append(EscapedInlineText(title)).Append("**");
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
        output.Append("- ").Append(EscapedInlineText(name)).Append(": ").Append(EscapedInlineText(ChartCategory(categories, first.Index))).Append(" の ")
            .Append(EscapedInlineText(first.Display)).Append(" から ").Append(EscapedInlineText(ChartCategory(categories, last.Index))).Append(" の ")
            .Append(EscapedInlineText(last.Display)).Append(" へ ").Append(direction)
            .Append("。最小 ").Append(EscapedInlineText(minimum.Display)).Append("、最大 ")
            .Append(EscapedInlineText(maximum.Display)).AppendLine("。");
    }

    private static void WriteCompositionChartSummary(StringBuilder output, string name, IReadOnlyList<string> categories,
        IReadOnlyList<(int Index, string Display, double Value)> numeric)
    {
        var minimum = numeric.MinBy(item => item.Value);
        var maximum = numeric.MaxBy(item => item.Value);
        var hasValidTotal = numeric.All(item => item.Value >= 0) && numeric.Sum(item => item.Value) > 0;
        var total = hasValidTotal ? numeric.Sum(item => item.Value) : 0;
        output.Append("- ").Append(EscapedInlineText(name)).Append(": 最大 ")
            .Append(EscapedInlineText(ChartCategory(categories, maximum.Index))).Append(' ').Append(EscapedInlineText(maximum.Display));
        if (hasValidTotal) output.Append("（全体の ").Append((maximum.Value / total).ToString("0.#%", CultureInfo.InvariantCulture)).Append("）");
        output.Append("、最小 ").Append(EscapedInlineText(ChartCategory(categories, minimum.Index))).Append(' ').Append(EscapedInlineText(minimum.Display));
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
            output.Append("- ").AppendLine(EscapedInlineText(text));
        }
        output.AppendLine();
    }

    /// <summary>Marks a node that stands for source content an adapter could not analyze at all
    /// (for example a PDF Form XObject it could not read). Such content is no visual graph, but it
    /// is just as missing from the Markdown and needs the same comparison with the source.</summary>
    public const string SourceReviewRequiredExtension = "source_review_required";

    /// <summary>Uses the actual Markdown projection to decide whether source comparison is
    /// needed, including raw-path-only fallback that graph accounting considers resolved.</summary>
    public static bool RequiresSourceReview(DocumentNode node)
    {
        if (node.Extensions?.TryGetValue(SourceReviewRequiredExtension, out var required) == true &&
            required.ValueKind == JsonValueKind.True)
            return true;
        if (!TryGetVisualGraph(node, out var graph)) return false;
        if (graph is null || graph.IsPartialProjection) return true;
        var probe = new ReadableMarkdownSerializer();
        probe.WriteVisualGraph(new StringBuilder(), node);
        return probe.Diagnostics.Any(d => d.Severity is MarkdownDiagnosticSeverity.Warning or MarkdownDiagnosticSeverity.Error);
    }

    private void WriteVisualGraph(StringBuilder output, DocumentNode node)
    {
        if (!TryGetVisualGraph(node, out var graph)) return;
        if (graph is null)
        {
            AddDiagnostic(new MarkdownDiagnostic("VisualSemanticProjectionPartial", "The visual graph metadata could not be read; connector and label fallback was not suppressed.", MarkdownDiagnosticSeverity.Warning, node.Id));
            return;
        }
        // Adapters may retain table borders and other non-semantic vectors as
        // accounted source items. When every item was deliberately ignored and
        // no fallback remains, there is nothing useful (or unresolved) to show.
        if ((graph.Nodes?.Count ?? 0) == 0 &&
            (graph.Edges?.Count ?? 0) == 0 &&
            graph.SourceItems is { Count: > 0 } sourceItems &&
            sourceItems.All(item => item is not null &&
                item.Disposition is VisualDisposition.IgnoredDecorative or VisualDisposition.SuppressedDuplicate) &&
            (graph.Paths ?? []).All(path => path is not null && !path.IsFallback) &&
            !(graph.Diagnostics ?? []).Any(diagnostic => diagnostic is not null))
        {
            if (sourceItems.Any(item => item?.Reason?.Contains("inferred from layout", StringComparison.OrdinalIgnoreCase) == true))
                AddDiagnostic(new MarkdownDiagnostic("VisualTableGridInferred",
                    "Regular vector lines were inferred as table borders and omitted from connector output; review the source if the layout represents a diagram.",
                    MarkdownDiagnosticSeverity.Info, node.Id));
            return;
        }
        foreach (var diagnostic in (graph.Diagnostics ?? []).Where(diagnostic => diagnostic is not null)
                     .GroupBy(diagnostic => (diagnostic.Code, diagnostic.PartitionId))
                     .SelectMany(group => group.Take(10)))
        {
            var message = diagnostic.LocationSummary is { Length: > 0 } location
                ? diagnostic.Message + " (" + location + ")"
                : diagnostic.Message;
            AddDiagnostic(new MarkdownDiagnostic(diagnostic.Code, message, MarkdownDiagnosticSeverity.Warning,
                VisualDiagnosticBlockId(node.Id, diagnostic)));
        }
        var validation = VisualGraphValidator.Validate(graph);
        foreach (var warning in validation.Warnings)
            AddDiagnostic(new MarkdownDiagnostic(warning.Code, warning.Message, MarkdownDiagnosticSeverity.Warning, warning.SourceNodeId ?? node.Id));
        var hasMediumConfidence = validation.Warnings.Any(warning => warning.Code == "VisualInferenceMediumConfidence");
        if (hasMediumConfidence)
        {
            const string contract = "一部の接続は図形配置から推定されています。診断を確認してください。";
            output.AppendLine("> ").AppendLine(contract).AppendLine();
            AddDiagnostic(new MarkdownDiagnostic("VisualSemanticProjectionPartial", contract,
                MarkdownDiagnosticSeverity.Warning, node.Id + "\u001eMediumConfidence"));
        }
        // Validation is authoritative: malformed metadata never reaches Mermaid.
        var quality = validation.Quality;
        if (quality == VisualGraphQuality.Invalid)
        {
            AddDiagnostic(new MarkdownDiagnostic("VisualSemanticProjectionFallback",
                "Visual graph is invalid; semantic Mermaid was omitted and source fallback is shown.",
                MarkdownDiagnosticSeverity.Error, node.Id));
            AddDiagnostic(new MarkdownDiagnostic("VisualSemanticProjectionPartial",
                "Visual graph validation failed; the source connector or vector fallback was retained.",
                MarkdownDiagnosticSeverity.Warning, node.Id));
            if (options.IncludeDiagrams) WriteVisualGraphFallbackDetails(output, graph, node);
            return;
        }
        if (!options.IncludeDiagrams)
        {
            WriteVisualEdgeFallback(output, graph);
            AddDiagnostic(new MarkdownDiagnostic("VisualDiagramRenderingDisabled",
                "Visual diagram rendering was disabled by the caller; resolved connections were retained as readable fallback.",
                MarkdownDiagnosticSeverity.Info, node.Id));
            return;
        }

        // The shared projection is intentionally format-neutral. It keeps every recognized
        // node when a connector is unresolved and recognizes sequence layouts from participant
        // headers, lifelines, and horizontal messages in DOCX, PPTX, and vector PDF graphs.
        var mermaid = ProjectVisualGraph(graph);
        if (mermaid is null)
        {
            foreach (var issue in validation.Errors)
                AddDiagnostic(new MarkdownDiagnostic(issue.Code, issue.Message, MarkdownDiagnosticSeverity.Warning, issue.SourceNodeId ?? node.Id));
            AddDiagnostic(new MarkdownDiagnostic("VisualSemanticProjectionFallback",
                quality == VisualGraphQuality.FallbackOnly
                    ? "Visual graph is fallback-only and has no safely renderable semantic nodes."
                    : "Visual graph has no safely renderable semantic topology.",
                MarkdownDiagnosticSeverity.Warning, node.Id));
            if ((graph.Edges?.Count > 0 || graph.Paths is { Count: > 0 }) && !(graph.Diagnostics ?? []).Any(diagnostic =>
                    diagnostic is not null && string.Equals(diagnostic.Code, "VisualSemanticProjectionPartial", StringComparison.Ordinal)))
                AddDiagnostic(new MarkdownDiagnostic("VisualSemanticProjectionPartial",
                    "Visual metadata did not contain a valid semantic topology; the source connector or vector fallback was retained.",
                    MarkdownDiagnosticSeverity.Warning, node.Id));
            if (options.IncludeDiagrams) WriteVisualGraphFallbackDetails(output, graph, node);
            return;
        }

        WriteMermaid(output, mermaid);
        WriteStyledEdgeNote(output, graph, mermaid);
        var hasUnresolvedEdges = (graph.Edges ?? []).Any(edge => edge.SourceId is null || edge.TargetId is null);
        if (quality is VisualGraphQuality.Partial or VisualGraphQuality.FallbackOnly || hasUnresolvedEdges)
            WritePartialVisualDetails(output, graph, node);
        else if (quality == VisualGraphQuality.HighConfidenceInferred)
            output.AppendLine("> 視覚構造は配置情報から推定されたものであり、内容の意味を保証するものではありません。").AppendLine();
    }

    private void WritePartialVisualDetails(StringBuilder output, VisualGraph graph, DocumentNode node) =>
        WriteBoundedVisualDetails(output, graph, node, partial: true);

    private void WriteVisualGraphFallbackDetails(StringBuilder output, VisualGraph graph, DocumentNode node) =>
        WriteBoundedVisualDetails(output, graph, node, partial: false);

    private void WriteBoundedVisualDetails(StringBuilder output, VisualGraph graph, DocumentNode node, bool partial)
    {
        var result = VisualFallbackMarkdownWriter.Write(output, graph, partial,
            ExtensionInt(node, "visual_output_max_paths") ?? VisualFallbackMarkdownWriter.MaxItems,
            ExtensionInt(node, "visual_output_max_characters") ?? VisualFallbackMarkdownWriter.MaxCharacters);
        if (result.Omitted > 0 || result.Shortened)
            AddDiagnostic(new MarkdownDiagnostic("VisualFallbackCompacted",
                $"Visual fallback: {result.Emitted} details shown; {result.Omitted} additional details omitted; long fields may be shortened.",
                MarkdownDiagnosticSeverity.Warning, node.Id));
    }

    private static void WriteVisualEdgeFallback(StringBuilder output, VisualGraph graph)
    {
        var labels = (graph.Nodes ?? []).Where(node => node is not null)
            .ToDictionary(node => node.Id, node => string.IsNullOrWhiteSpace(node.Label) ? "[unlabeled shape: " + node.Id + "]" : node.Label,
                StringComparer.Ordinal);
        var edges = (graph.Edges ?? []).Where(edge => edge is not null && edge.SourceId is not null && edge.TargetId is not null)
            .OrderBy(edge => edge.Id, StringComparer.Ordinal).ToArray();
        if (edges.Length == 0) return;
        output.AppendLine("### 図の接続関係").AppendLine();
        foreach (var edge in edges)
        {
            var source = labels.TryGetValue(edge.SourceId!, out var sourceLabel) ? sourceLabel : edge.SourceId!;
            var target = labels.TryGetValue(edge.TargetId!, out var targetLabel) ? targetLabel : edge.TargetId!;
            output.Append("- ").Append(EscapedInlineText(source)).Append(edge.IsUndirected ? " — " : " → ").Append(EscapedInlineText(target));
            if (!string.IsNullOrWhiteSpace(edge.Label)) output.Append("（").Append(EscapedInlineText(edge.Label!)).Append("）");
            output.AppendLine();
        }
        output.AppendLine();
    }

    private static IEnumerable<string> VisualGraphMemberShapeIds(DocumentNode diagram)
    {
        if (diagram.Extensions is null || !diagram.Extensions.TryGetValue("visual_graph_member_shape_ids", out var raw) ||
            raw.ValueKind != JsonValueKind.Array) return [];
        return raw.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()).Where(value => !string.IsNullOrWhiteSpace(value)).Cast<string>();
    }

    private static bool HasVisualGraph(DocumentNode node) =>
        TryGetVisualGraph(node, out var graph) && graph is not null;

    private static bool TryGetRenderableVisualGraph(DocumentNode node) =>
        TryGetVisualGraph(node, out var graph) && graph is not null &&
        VisualGraphValidator.Validate(graph).Quality != VisualGraphQuality.Invalid &&
        ProjectVisualGraph(graph) is not null;

    private static bool TryGetRelationVisualGraph(DocumentNode node) =>
        TryGetVisualGraph(node, out var graph) && graph is not null &&
        VisualGraphValidator.Validate(graph).IsValidForSemanticProjection &&
        (graph.Edges ?? []).Any(edge => edge.SourceId is not null && edge.TargetId is not null);

    private static string? ProjectVisualGraph(VisualGraph graph)
    {
        var nodes = (graph.Nodes ?? []).Where(node => node is not null).ToArray();
        var edges = (graph.Edges ?? []).Where(edge => edge is not null).ToArray();
        if (nodes.Length == 0 || nodes.Any(node => string.IsNullOrWhiteSpace(node.Id) || string.IsNullOrWhiteSpace(node.Label)))
            return null;
        if (TryProjectSequenceGraph(nodes, edges, out var sequence)) return sequence;
        if (edges.Length == 0) return null;

        var output = new StringBuilder("flowchart " + (graph.Direction is "TD" ? "TD" : "LR") + "\n");
        foreach (var visualNode in nodes.OrderBy(item => item.Geometry?.Y ?? double.MaxValue)
                     .ThenBy(item => item.Geometry?.X ?? double.MaxValue).ThenBy(item => item.Id, StringComparer.Ordinal))
        {
            var label = MermaidText(visualNode.Label);
            var rendered = visualNode.Kind switch
            {
                VisualNodeKind.Decision => "{" + label + "}",
                VisualNodeKind.Terminator => "([" + label + "])",
                VisualNodeKind.Data => "[/" + label + "/]",
                VisualNodeKind.Process => "[" + label + "]",
                _ => "[" + label + "]",
            };
            output.Append("    ").Append(visualNode.Id).Append(rendered).AppendLine();
        }
        foreach (var edge in edges.Where(item => item.SourceId is not null && item.TargetId is not null)
                     .OrderBy(item => item.Geometry?.Y ?? double.MaxValue).ThenBy(item => item.Id, StringComparer.Ordinal))
        {
            // Mermaid has a single dotted link style; it stands for both dashed and dotted lines,
            // and WriteVisualGraph adds a note naming the source style.
            var link = VisualLineStyles.IsKnown(edge.LineStyle)
                ? edge.IsUndirected ? "-.-" : "-.->"
                : edge.IsUndirected ? "---" : "-->";
            var arrow = string.IsNullOrWhiteSpace(edge.Label) ? $" {link} " : $" {link}|" + MermaidText(edge.Label!) + "| ";
            output.Append("    ").Append(edge.SourceId).Append(arrow).Append(edge.TargetId).AppendLine();
        }
        return output.ToString().TrimEnd();
    }

    private static void WriteStyledEdgeNote(StringBuilder output, VisualGraph graph, string mermaid)
    {
        var sequence = mermaid.StartsWith("sequenceDiagram", StringComparison.Ordinal);
        if (!mermaid.Contains(sequence ? "-->>" : " -.-", StringComparison.Ordinal)) return;
        var styles = (graph.Edges ?? []).Where(edge => edge is not null && edge.SourceId is not null && edge.TargetId is not null &&
                VisualLineStyles.IsKnown(edge.LineStyle))
            .Select(edge => edge.LineStyle!).Distinct(StringComparer.Ordinal).OrderBy(style => style, StringComparer.Ordinal).ToArray();
        if (styles.Length == 0) return;
        output.Append("> 線種の注記: ").Append(sequence ? "点線の矢印（-->>）" : "点線の接続（-.-）").Append("は原本で")
            .Append(string.Join("・", styles.Select(LineStyleName))).AppendLine("で描かれた線です。").AppendLine();
    }

    private static bool TryProjectSequenceGraph(
        IReadOnlyList<VisualNode> nodes,
        IReadOnlyList<VisualEdge> edges,
        out string mermaid)
    {
        mermaid = string.Empty;
        var spatialNodes = nodes.Where(node => node.Geometry is { } geometry &&
                double.IsFinite(geometry.X) && double.IsFinite(geometry.Y) &&
                double.IsFinite(geometry.Width) && double.IsFinite(geometry.Height) &&
                geometry.Width > 0 && geometry.Height > 0)
            .ToArray();
        var segments = edges.Select(edge => TrySequenceSegment(edge, out var segment) ? segment : null)
            .Where(segment => segment is not null).Cast<SequenceSegment>().ToArray();
        if (spatialNodes.Length < 2 || segments.Length < 3) return false;

        static double Median(IEnumerable<double> values)
        {
            var ordered = values.Where(double.IsFinite).OrderBy(value => value).ToArray();
            return ordered.Length == 0 ? 1 : ordered[ordered.Length / 2];
        }

        var medianWidth = Math.Max(1, Median(spatialNodes.Select(node => node.Geometry!.Width)));
        var medianHeight = Math.Max(1, Median(spatialNodes.Select(node => node.Geometry!.Height)));
        var topCenter = spatialNodes.Min(node => node.Geometry!.Y + node.Geometry.Height / 2);
        var totalHeight = spatialNodes.Max(node => node.Geometry!.Y + node.Geometry.Height) -
                          spatialNodes.Min(node => node.Geometry!.Y);
        var headerBand = Math.Max(medianHeight, totalHeight * .08);
        var headerNodes = spatialNodes
            .Where(node => node.Geometry!.Y + node.Geometry.Height / 2 <= topCenter + headerBand)
            .OrderBy(node => node.Geometry!.X + node.Geometry.Width / 2)
            .ThenBy(node => node.Id, StringComparer.Ordinal).ToArray();
        if (headerNodes.Length < 2) return false;

        // Lifelines are long, vertical, normally undirected connectors without semantic
        // endpoints. Requiring them prevents an ordinary top-to-bottom workflow from being
        // misclassified as a sequence diagram.
        var lifelines = segments.Where(segment =>
                (segment.Edge.SourceId is null || segment.Edge.TargetId is null) &&
                segment.Edge.IsUndirected &&
                segment.Height >= Math.Max(medianHeight * 2, segment.Width * 3))
            .ToArray();
        var participants = headerNodes.Where(node =>
        {
            var geometry = node.Geometry!;
            var centerX = geometry.X + geometry.Width / 2;
            var tolerance = Math.Max(geometry.Width * .65, medianWidth * .35);
            return lifelines.Any(lifeline =>
                Math.Abs(lifeline.CenterX - centerX) <= tolerance &&
                lifeline.MinY <= geometry.Y + geometry.Height * 2 &&
                lifeline.MaxY >= geometry.Y + geometry.Height + medianHeight);
        }).ToArray();
        if (participants.Length < 2) return false;

        var aliases = participants.Select((node, index) => (node.Id, Alias: "P" + (index + 1).ToString(CultureInfo.InvariantCulture)))
            .ToDictionary(item => item.Id, item => item.Alias, StringComparer.Ordinal);
        var participantCenters = participants.Select(node => new SequenceParticipant(
                node, aliases[node.Id], node.Geometry!.X + node.Geometry.Width / 2))
            .OrderBy(item => item.CenterX).ToArray();
        var minGap = participantCenters.Zip(participantCenters.Skip(1),
                (left, right) => right.CenterX - left.CenterX)
            .Where(gap => gap > 0).DefaultIfEmpty(medianWidth * 2).Min();
        var endpointTolerance = Math.Max(medianWidth, minGap * .3);
        var participantBottom = participantCenters.Min(item => item.Node.Geometry!.Y + item.Node.Geometry.Height);
        var messages = segments.Where(segment =>
                segment.Width >= Math.Max(medianWidth * .5, segment.Height * 3) &&
                segment.CenterY >= participantBottom - medianHeight * .25)
            .Select(segment => new
            {
                Segment = segment,
                Covered = participantCenters.Where(participant =>
                        participant.CenterX >= segment.MinX - endpointTolerance &&
                        participant.CenterX <= segment.MaxX + endpointTolerance)
                    .ToArray()
            })
            .Where(item =>
                item.Covered.Length >= 2 ||
                item.Segment.Edge.SourceId is { } sourceId && aliases.ContainsKey(sourceId) &&
                item.Segment.Edge.TargetId is { } targetId && aliases.ContainsKey(targetId))
            .OrderBy(item => item.Segment.CenterY).ThenBy(item => item.Segment.Edge.Id, StringComparer.Ordinal)
            .ToArray();
        if (messages.Length == 0) return false;

        var output = new StringBuilder("sequenceDiagram\n");
        foreach (var participant in participantCenters)
            output.Append("    participant ").Append(participant.Alias).Append(" as ")
                .AppendLine(MermaidText(participant.Node.Label));

        foreach (var message in messages)
        {
            var edge = message.Segment.Edge;
            var label = SequenceMessageLabel(edge.Label);
            if (edge.SourceId is { } knownSource && edge.TargetId is { } knownTarget &&
                aliases.TryGetValue(knownSource, out var sourceAlias) &&
                aliases.TryGetValue(knownTarget, out var targetAlias) && !edge.IsUndirected)
            {
                output.Append("    ").Append(sourceAlias).Append(SequenceArrow(edge)).Append(targetAlias)
                    .Append(": ").AppendLine(MermaidText(label));
                continue;
            }

            // An unresolved endpoint remains a note, even when only two lifelines are nearby.
            if (message.Covered.Length > 2 || edge.IsUndirected || edge.SourceId is null || edge.TargetId is null)
            {
                var covered = message.Covered.Length == 0 ? participantCenters : message.Covered;
                output.Append("    Note over ").Append(covered[0].Alias).Append(',').Append(covered[^1].Alias)
                    .Append(": ").AppendLine(MermaidText(label));
                continue;
            }

            var from = message.Covered.MinBy(item => Math.Abs(item.CenterX - message.Segment.StartX));
            var to = message.Covered.MinBy(item => Math.Abs(item.CenterX - message.Segment.EndX));
            if (string.Equals(edge.Direction, "reverse", StringComparison.OrdinalIgnoreCase))
                (from, to) = (to, from);
            if (from is null || to is null || from.Alias == to.Alias)
            {
                output.Append("    Note over ").Append(message.Covered[0].Alias).Append(',')
                    .Append(message.Covered[^1].Alias).Append(": ").AppendLine(MermaidText(label));
                continue;
            }
            output.Append("    ").Append(from.Alias).Append(SequenceArrow(edge)).Append(to.Alias)
                .Append(": ").AppendLine(MermaidText(label));
        }

        mermaid = output.ToString().TrimEnd();
        return true;
    }

    // A dashed or dotted message (commonly a reply in UML) keeps its line style as Mermaid's
    // dotted message arrow.
    private static string SequenceArrow(VisualEdge edge) => VisualLineStyles.IsKnown(edge.LineStyle) ? "-->>" : "->>";

    private static bool TrySequenceSegment(VisualEdge edge, out SequenceSegment segment)
    {
        segment = default!;
        if (edge.Path is { Count: >= 2 } path)
        {
            var first = path[0];
            var last = path[^1];
            if (double.IsFinite(first.X) && double.IsFinite(first.Y) &&
                double.IsFinite(last.X) && double.IsFinite(last.Y))
            {
                segment = new SequenceSegment(edge, first.X, first.Y, last.X, last.Y);
                return true;
            }
        }
        if (edge.Geometry is not { } geometry ||
            !double.IsFinite(geometry.X) || !double.IsFinite(geometry.Y) ||
            !double.IsFinite(geometry.Width) || !double.IsFinite(geometry.Height))
            return false;
        segment = new SequenceSegment(edge, geometry.X, geometry.Y,
            geometry.X + geometry.Width, geometry.Y + geometry.Height);
        return true;
    }

    private static string SequenceMessageLabel(string? value)
    {
        var label = Regex.Replace(value ?? string.Empty,
            @"\s*(?:[←→▶◀][─━—\-]*|[─━—\-]{2,}[<>▶◀←→]?)\s*", " ").Trim();
        return label.Length == 0 ? "接続先未確定のメッセージ" : label;
    }

    /// <summary>Returns true when visual_graph metadata exists; a null graph means it was malformed.</summary>
    private static bool TryGetVisualGraph(DocumentNode node, out VisualGraph? graph)
    {
        graph = null;
        if (node.Kind != NodeKind.Diagram || node.Extensions is null || !node.Extensions.TryGetValue("visual_graph", out var raw)) return false;
        try { graph = raw.Deserialize<VisualGraph>(); }
        catch (JsonException) { }
        return true;
    }

    private static string MermaidText(string value) => value.Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal)
        .Replace("|", "&#124;", StringComparison.Ordinal).Replace("[", "&#91;", StringComparison.Ordinal).Replace("]", "&#93;", StringComparison.Ordinal);

    private void WriteMermaid(StringBuilder output, string mermaid)
    {
        if (options.IncludeSvgPreviews && MermaidSvgPreviewRenderer.Render(mermaid) is { } svg)
        {
            output.AppendLine(svg).AppendLine();
        }
        var trimmed = mermaid.Trim();
        var fence = CodeFence(trimmed);
        output.Append(fence).AppendLine("mermaid").AppendLine(trimmed).AppendLine(fence).AppendLine();
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

    // The worksheet's own name, as on its tab and in the AI package manifest (the section heading
    // uses HumanizePartitionName instead).
    private static string SheetName(string partitionId)
    {
        var value = partitionId.Trim();
        foreach (var prefix in new[] { "sheet-", "worksheet-", "partition-" })
            if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return value[prefix.Length..];
        return value;
    }

    private static string HumanizePartitionName(string id)
    {
        var value = id.Trim();
        if (value.StartsWith("sheet-", StringComparison.OrdinalIgnoreCase)) value = value["sheet-".Length..];
        else if (value.StartsWith("worksheet-", StringComparison.OrdinalIgnoreCase)) value = value["worksheet-".Length..];
        else if (value.StartsWith("partition-", StringComparison.OrdinalIgnoreCase)) value = value["partition-".Length..];
        return value.Replace('_', ' ').Trim() is { Length: > 0 } result ? result : "シート";
    }

    private static string PlainText(string value) => value.Replace("`", string.Empty, StringComparison.Ordinal);
    private static string InlineText(string value) => Regex.Replace(value, @"</?span\b[^>]*>", string.Empty, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
        .Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal)
        .Replace("#", "\\#", StringComparison.Ordinal).Trim();
    private static string TableText(string value) => EscapeLiteral(value.Replace("\r", string.Empty, StringComparison.Ordinal))
        .Replace("\n", "<br>", StringComparison.Ordinal).Replace("|", "\\|", StringComparison.Ordinal).Trim();
    private static string NormalizeComparison(string value) => Regex.Replace(value, "[\\s_\\-—:：.。/\\\\]", string.Empty);
    private static string Finish(StringBuilder output) =>
        output.ToString().Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd() + "\n";

    // F-Issue7: neutralizes literal Markdown/HTML metacharacters in text that originates from a
    // PLAIN source (TextNodeContent, ReferenceNodeContent alt text, TableCell text, JSON-extracted
    // strings from chart/diagram/paragraph_details extensions). RichTextNodeContent is already
    // escaped by DocRedockInlineMarkdown.Serialize before it ever reaches a writer, so callers must
    // apply this ONLY to plain-origin strings (see DisplayText/EscapedInlineText below) — applying it
    // to already-serialized rich text would double-escape real `**bold**`/`_italic_` markup.
    //
    // Backslash-escapes the same set DocRedockInlineMarkdown.Escape uses (\ * _ ~ ` [ ]), so a lone
    // emphasis/code delimiter never survives as live syntax. `[` and `]` are in that set (D07) so
    // source text that merely LOOKS like Markdown -- `[text](url)`, `![alt](path)`, `[ref][id]`, and a
    // `[id]: url` reference definition, which a renderer would otherwise consume and hide entirely --
    // stays visible literal text. `(`, `)`, `!` and `:` are deliberately NOT escaped: escaping `[`
    // alone already neutralizes every one of those forms, and escaping the rest only makes the
    // readable output noisier. Real links/images are built by writers that add their own unescaped
    // brackets around an EscapeLiteral'd label (WriteImage, SerializeReadableRuns), so they still work.
    //
    // `<` becomes `&lt;` only when it could
    // start a tag/comment/processing-instruction (next char is a letter, `/`, `!`, or `?`); the `>`
    // that appears to close such a tag is escaped too (`&gt;`) so `<b>text</b>` reads as literal
    // "<b>text</b>" instead of a dangling `>` next to an escaped `<`. A `>` with no preceding
    // tag-like `<` is left alone here — it is only dangerous at the start of a line (blockquote),
    // which WriteParagraph's line-start pass (EscapeLineStarts) handles separately. `&` becomes
    // `&amp;` only when it could already read as a character reference (`&name;`, `&#123;`,
    // `&#x1F;`); a bare `a & b` is left untouched.
    private static string EscapeLiteral(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        var output = new StringBuilder(value.Length);
        var pendingTagClose = false;
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            switch (character)
            {
                case '\\' or '*' or '_' or '~' or '`' or '[' or ']':
                    output.Append('\\').Append(character);
                    break;
                case '<':
                    if (IsHtmlTagStart(value, index + 1))
                    {
                        output.Append("&lt;");
                        pendingTagClose = true;
                    }
                    else output.Append('<');
                    break;
                case '>' when pendingTagClose:
                    output.Append("&gt;");
                    pendingTagClose = false;
                    break;
                case '&':
                    output.Append(IsEntityStart(value, index + 1) ? "&amp;" : "&");
                    break;
                case '\n':
                    pendingTagClose = false;
                    output.Append(character);
                    break;
                default:
                    output.Append(character);
                    break;
            }
        }
        return output.ToString();
    }

    private static bool IsHtmlTagStart(string value, int nextIndex) =>
        nextIndex < value.Length && (value[nextIndex] is '/' or '!' or '?' || char.IsAsciiLetter(value[nextIndex]));

    private static bool IsEntityStart(string value, int index) =>
        index < value.Length && EntityReferenceRegex().IsMatch(value.AsSpan(index));

    /// <summary>EscapeLiteral followed by the existing InlineText cleanup (span stripping, newline
    /// flattening, `#` escaping). Only for GUARANTEED-plain sources (JSON-extracted chart/diagram
    /// strings, table cell text, comment authors) — never for text that may already be
    /// rich-serialized, since InlineText alone is what those call sites need instead.</summary>
    private static string EscapedInlineText(string value) => InlineText(EscapeLiteral(value));

    /// <summary>Returns <paramref name="rawText"/> unchanged when <paramref name="node"/> carries
    /// RichTextNodeContent (already escaped by DocRedockInlineMarkdown.Serialize when it was turned
    /// into text), otherwise applies EscapeLiteral. NodeText(node) itself must stay raw for
    /// comparisons (dedup, footer-repetition, titles), so escaping is layered on at display time via
    /// this helper instead.</summary>
    private static string DisplayText(DocumentNode node, string rawText) =>
        node.Content is RichTextNodeContent ? rawText : EscapeLiteral(rawText);

    // Replaces the narrower EscapeParagraphStart: walks every line (not just the first) of a
    // paragraph and escapes whatever would otherwise open an ATX heading, blockquote, list item, or
    // ordered-list marker, plus a line that is nothing but a thematic break / setext underline
    // (`---`, `===`). Only WriteParagraph needs this — headings flatten to one line before it can
    // matter, and quotes/list items already start each rendered line with their own "> "/"- " marker.
    private static string EscapeLineStarts(string text)
    {
        var marked = LineStartMarkerRegex().Replace(text, match =>
        {
            var indent = match.Groups["indent"].Value;
            if (match.Groups["olnum"].Success)
                return indent + match.Groups["olnum"].Value + "\\" + match.Groups["oldelim"].Value;
            var marker = match.Groups["atx"].Success ? match.Groups["atx"].Value
                : match.Groups["quote"].Success ? match.Groups["quote"].Value
                : match.Groups["bullet"].Value;
            return indent + "\\" + marker;
        });
        return ThematicBreakLineRegex().Replace(marked, match => match.Groups["indent"].Value + "\\" + match.Groups["rule"].Value);
    }

    // Longest run of backticks in the content, plus one (minimum 3): guarantees the fence itself
    // can never appear, verbatim, inside the content it wraps.
    private static string CodeFence(string content)
    {
        var longest = 0;
        var current = 0;
        foreach (var character in content)
        {
            if (character == '`') { current++; longest = Math.Max(longest, current); }
            else current = 0;
        }
        return new string('`', Math.Max(3, longest + 1));
    }

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

    // P-Overlay (XLSX): IsOverlay marks a cell ApplySheetOverlays touched (synthesized, or an
    // existing cell it appended a marker to) -- see SplitRow, which never splits a row containing
    // one. A schedule row's non-overlay columns are commonly blank (only the label/owner columns
    // ever had real data), so folding in an arrow/bar spanning a handful of *other* columns opens a
    // wide gap in that row's now-non-blank column positions -- exactly what SplitRow's general
    // side-by-side-tables heuristic (row.Cells.Count > 4 and a gap >= 2 columns) exists to detect,
    // splitting one schedule row into a same-row label fragment and a same-row overlay fragment
    // that a region can then never join back together (BuildRegions accepts at most one fragment
    // per row per region). A row genuinely made of two independent tables never carries an overlay
    // marker, so this stays narrowly scoped to the case this feature introduces.
    // Table is the Excel table (ListObject) the cell belongs to, when the workbook declares one:
    // explicit source structure that outranks every layout heuristic in SheetRegionBoundaries.
    // Display is what the cell shows (a formula's saved result, without the formula ShowFormulas adds
    // to Text). IsComputed marks a formula result that is a value (see IsComputedResult); text a
    // formula looked up or built from other cells is read like typed text. IsFormulaCopy marks a copy
    // of a formula filled down or across, which Excel stores without its text (Formula is null); it is
    // judged against the formula it copies.
    private sealed record ReadableCell(int Row, int Column, string Text, bool IsFormula, bool IsNumeric, bool IsBold, bool HasFill, bool HasBorder, bool IsCentered, double? FontSize, int MaxColumn, bool IsOverlay = false, int MaxRow = 0, bool IsCenterAcross = false, string? NodeId = null, string? Table = null, IReadOnlyList<string>? OverlayNodeIds = null, string? Display = null, bool IsComputed = false, bool IsFormulaCopy = false, string? Formula = null, bool ReturnsText = false)
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
    private sealed record ReadableDiagram(int MinRow, int MaxRow, string Mermaid, string NodeId);
    private sealed record SequenceParticipant(VisualNode Node, string Alias, double CenterX);
    private sealed record SequenceSegment(VisualEdge Edge, double StartX, double StartY, double EndX, double EndY)
    {
        public double MinX => Math.Min(StartX, EndX);
        public double MaxX => Math.Max(StartX, EndX);
        public double MinY => Math.Min(StartY, EndY);
        public double MaxY => Math.Max(StartY, EndY);
        public double Width => MaxX - MinX;
        public double Height => MaxY - MinY;
        public double CenterX => (StartX + EndX) / 2;
        public double CenterY => (StartY + EndY) / 2;
    }
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

    // A value written as text: an amount or count with its sign, currency, grouping and unit, a
    // percentage, a time or date, or a mark that stands for a value (—, ○, ×, ✓ ...). Words are not
    // values, so a list of names or statuses is never mistaken for data columns.
    [GeneratedRegex(@"^(?:[+\-−－▲△]?[¥￥$€£]?[(（]?[0-9０-９][0-9０-９,，]*(?:[.．][0-9０-９]+)?[)）]?\s*(?:%|％|円|千円|万円|百万円|億円|件|人|名|個|台|回|日|時間|分|秒|歳|か月|ヶ月|km|m|kg|g)?|\d{1,2}:\d{2}(?::\d{2})?|\d{4}年\d{1,2}月(?:\d{1,2}日)?|\d{1,2}月(?:\d{1,2}日)?|[—–―‐\-－ー○◯◎●×✕△▲□■☆★✓✔])$", RegexOptions.CultureInvariant)]
    private static partial Regex ValueTextRegex();

    // A dash that stands for "no value" in a data cell.
    [GeneratedRegex(@"^[—–―‐\-－ー]+$", RegexOptions.CultureInvariant)]
    private static partial Regex PlaceholderRegex();

    // Headers of a column that annotates each row beside it rather than listing items of its own.
    [GeneratedRegex(@"^(?:備考|摘要|注記|注|メモ|コメント|補足|特記事項|説明|notes?|remarks?|comments?|memo)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AnnotationHeaderRegex();

    // A string written in a formula: "達成", with "" for a quotation mark inside it.
    [GeneratedRegex(@"""((?:[^""]|"""")*)""", RegexOptions.CultureInvariant)]
    private static partial Regex FormulaStringRegex();

    // Headers of a column that keys rows by period.
    [GeneratedRegex(@"^(?:年|年度|会計年度|月|年月|日|日付|期|期間|四半期|週|year|fy|fiscal year|month|date|period|quarter|week)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PeriodHeaderRegex();

    // Group names: indent (leading spaces/tabs, preserved verbatim), then exactly one of
    // atx (# .. ######), quote (>), bullet (- or +), or olnum+oldelim (ordered marker digits
    // and its . or ) delimiter, kept apart so only the delimiter needs escaping — "1\." renders
    // clean, whereas escaping the leading digit ("\1.") would leave a visible backslash.
    [GeneratedRegex(@"^(?<indent>[ \t]{0,3})(?:(?<atx>#{1,6})(?=[ \t]|$)|(?<quote>>)|(?<bullet>[+\-])(?=[ \t]|$)|(?<olnum>\d{1,9})(?<oldelim>[.)])(?=[ \t]|$))", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex LineStartMarkerRegex();

    // A line consisting solely of '-' or '=' (optionally spaced): a thematic break or a setext
    // heading underline for the paragraph line above it. Lines already handled by
    // LineStartMarkerRegex's bullet case (a lone "-" or "- - -") no longer start with the bare
    // character by the time this runs, so there is no double-escaping.
    [GeneratedRegex(@"^(?<indent>[ \t]{0,3})(?<rule>(?:-[ \t]*){1,}|(?:=[ \t]*){1,})$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex ThematicBreakLineRegex();

    // A leading '&' is escaped only when what follows could already read as a real character
    // reference: &name; / &#123; / &#x1F;.
    [GeneratedRegex(@"^(?:#[0-9]+|#[xX][0-9A-Fa-f]+|[A-Za-z][A-Za-z0-9]*);", RegexOptions.CultureInvariant)]
    private static partial Regex EntityReferenceRegex();
}
