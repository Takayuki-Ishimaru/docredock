using System.Globalization;
using System.Text.Json;
using DocRedock.Core.Documents;
using static DocRedock.Markdown.ReadableGraphPreparation;

namespace DocRedock.Markdown;

/// <summary>Builds native presentation blocks from source structure. No Markdown is generated or parsed.</summary>
public static partial class ReadableProjectionBuilder
{
    public static ReadableProjectionResult Build(DocumentGraph graph, ReadableProjectionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(graph);
        cancellationToken.ThrowIfCancellationRequested();
        options ??= new();
        var prepared = Prepare(graph, options, cancellationToken);
        return new Projection(PdfTableContinuationProjection.Project(prepared.Graph), options,
            prepared.Diagnostics, cancellationToken).Run();
    }

    private sealed partial class Projection(DocumentGraph graph, ReadableProjectionOptions options,
        IReadOnlyList<MarkdownDiagnostic> initialDiagnostics, CancellationToken cancellationToken)
    {
        private readonly List<MarkdownDiagnostic> diagnostics = [.. initialDiagnostics];
        private readonly List<DocumentNode> furniture = [];
        private readonly HashSet<string> renderedImages = new(StringComparer.Ordinal);
        private readonly HashSet<string> withheldImages = new(StringComparer.Ordinal);
        private readonly HashSet<string> appliedOverlayIds = new(StringComparer.Ordinal);
        private readonly HashSet<string> imageTextParents = graph.Nodes.Where(n => n.Kind == NodeKind.ImageText &&
            n.ParentId is not null && !string.IsNullOrWhiteSpace(Plain(n))).Select(n => n.ParentId!).ToHashSet(StringComparer.Ordinal);
        private readonly Dictionary<string, DocumentPartition> owners = graph.Partitions.SelectMany(p => p.Nodes.Select(n => (Node: n, Partition: p)))
            .ToDictionary(item => item.Node.Id, item => item.Partition, StringComparer.Ordinal);
        private Dictionary<string, List<DocumentNode>> nested = new(StringComparer.Ordinal);
        private HashSet<string> attached = new(StringComparer.Ordinal);
        private Dictionary<string, DocumentNode[]> cellImages = new(StringComparer.Ordinal);
        private DocumentPartition partition = new("", 0, []);
        private readonly bool ja = options.Language?.StartsWith("ja", StringComparison.OrdinalIgnoreCase) ??
            graph.Nodes.Any(n => Plain(n).Any(c => c is >= '\u3040' and <= '\u9FFF'));
        private WorkbookPresentationLayout? layout;
        private IReadOnlyDictionary<string, WorkbookDrawingScene> drawingScenes = new Dictionary<string, WorkbookDrawingScene>();
        private bool sourceSceneAvailable;
        private HashSet<string> nativeDrawingShapeIds = new(StringComparer.Ordinal);
        private IReadOnlyDictionary<string, IReadOnlyList<SourceDrawingScene>> sourceDrawingScenes = new Dictionary<string, IReadOnlyList<SourceDrawingScene>>();

        public ReadableProjectionResult Run()
        {
            if (graph.FormatExtensions?.TryGetValue(WorkbookPresentationLayoutJson.ExtensionKey, out var rawLayout) == true)
            {
                var notices = new List<string>();
                WorkbookPresentationLayoutJson.TryRead(rawLayout, out layout, notices);
                foreach (var notice in notices) Add("WorkbookLayoutInvalid", notice);
            }
            var sections = new List<ReadableBlock>();
            if (graph.FormatExtensions?.TryGetValue(SourceDrawingSceneJson.ExtensionKey, out var rawSourceScenes) == true)
            {
                var sceneWarnings = new List<string>();
                sourceDrawingScenes = SourceDrawingSceneJson.Read(rawSourceScenes, sceneWarnings);
                foreach (var warning in sceneWarnings) Add("SourceDrawingSceneInvalid", warning);
            }
            if (graph.FormatExtensions?.TryGetValue(WorkbookDrawingSceneJson.ExtensionKey, out var rawScenes) == true)
            {
                var drawingWarnings = new List<string>();
                drawingScenes = WorkbookDrawingSceneJson.Read(rawScenes, drawingWarnings);
                foreach (var warning in drawingWarnings) Add("WorkbookDrawingSceneInvalid", warning);
            }
            var sourcePagination = layout?.Sheets.Values.Any(s => s.Print?.HeaderFooter is not null || s.Print?.FixedSourcePagination == true) == true;
            foreach (var part in graph.Partitions.OrderBy(p => p.Order).ThenBy(p => p.Id, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (DocumentContentPolicyRules.Parse(options.ContentPolicy) != DocumentContentPolicy.Complete &&
                    layout?.Sheets.GetValueOrDefault(part.Id)?.State is "hidden" or "veryHidden") continue;
                partition = part;
                sourceSceneAvailable = drawingScenes.TryGetValue(part.Id, out var sourceScene) && sourceScene.Objects.Count > 0;
                nativeDrawingShapeIds = sourceDrawingScenes.GetValueOrDefault(part.Id, []).SelectMany(s => s.Objects)
                    .Where(o => DocumentContentPolicyRules.Parse(options.ContentPolicy) == DocumentContentPolicy.Complete || !o.Hidden)
                    .Where(o => o.Text.Length > 0).SelectMany(o => o.Source?.Locators ?? []).Where(l => l.Kind == "shape_id")
                    .Select(l => l.Value).ToHashSet(StringComparer.Ordinal);
                var nodes = part.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
                nested = new(StringComparer.Ordinal); attached = new(StringComparer.Ordinal);
                cellImages = part.Nodes.Where(n => n.Kind is NodeKind.Image or NodeKind.PageBreak && n.ParentId is not null &&
                        nodes.TryGetValue(n.ParentId, out var host) && host.Content is TableNodeContent hostTable &&
                        (ExtensionInt(n,"table_image_row") ?? ExtensionInt(n,"table_cell_row")) is {} row && row >= 0 && row < hostTable.Rows.Count &&
                        (ExtensionInt(n,"table_image_column") ?? ExtensionInt(n,"table_cell_column")) is {} column && column >= 0 && column < hostTable.Rows[row].Count)
                    .GroupBy(n => n.ParentId!, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
                attached.UnionWith(cellImages.Values.SelectMany(images => images).Select(image => image.Id));
                foreach (var child in part.Nodes.Where(n => n.Kind == NodeKind.Table))
                {
                    if (ExtensionText(child, "nested_table_parent") is not { } parent ||
                        !nodes.TryGetValue(parent, out var host) || host.Content is not TableNodeContent hostTable ||
                        ExtensionInt(child, "nested_table_row") is not { } row || row < 0 || row >= hostTable.Rows.Count ||
                        ExtensionInt(child, "nested_table_column") is not { } col || col < 0 || col >= hostTable.Rows[row].Count) continue;
                    var seen = new HashSet<string>(StringComparer.Ordinal) { child.Id };
                    var ancestor = parent;
                    while (seen.Count <= 24 && seen.Add(ancestor) && nodes.TryGetValue(ancestor, out var previous) &&
                           ExtensionText(previous, "nested_table_parent") is { } next) ancestor = next;
                    if (seen.Count > 24 || ancestor == child.Id) { Add("ReadableNestedTableDetached", "Cyclic or excessively deep nested table shown independently.", child.Id); continue; }
                    if (!nested.TryGetValue(parent, out var children)) nested[parent] = children = [];
                    children.Add(child); attached.Add(child.Id);
                }
                var semantic = part.Nodes.Where(n => HasExtension(n, "visual_graph")).ToDictionary(n => n.Id, Diagram, StringComparer.Ordinal);
                var renderedDiagrams = semantic.Where(d => !string.IsNullOrWhiteSpace(d.Value.MermaidSource)).ToArray();
                var consumedShapes = new HashSet<string>(StringComparer.Ordinal);
                foreach (var diagram in renderedDiagrams)
                {
                    var memberIds = Extension(nodes[diagram.Key], "visual_graph_member_shape_ids");
                    if (memberIds.ValueKind == JsonValueKind.Array)
                        consumedShapes.UnionWith(memberIds.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!));
                    foreach (var visualNode in diagram.Value.Graph?.Nodes ?? [])
                        if (visualNode.SourceNodeId is { } sourceId) consumedShapes.Add(sourceId);
                }
                var suppressUnscopedMembers = semantic.Count == 1 && renderedDiagrams.Length == 1;
                var overlayHosts = part.Nodes.Where(n => n.Kind == NodeKind.Table && n.Content is TableNodeContent { Rows.Count: > 0 } && HasExtension(n, "table_overlays"))
                    .SelectMany(n => new[] { n.Id, ExtensionText(n, "shape_id") }).OfType<string>().ToHashSet(StringComparer.Ordinal);
                var blocks = new List<ReadableBlock>();
                var workbookTable = graph.Format == DocumentFormatKind.Xlsx ? WorkbookTable(part) : null;
                if (workbookTable is not null) blocks.Add(workbookTable);
                var ordered = graph.Format == DocumentFormatKind.Pptx
                    ? ReadableMarkdownSerializer.PresentationReadingOrder(part.Nodes) : ReadableMarkdownSerializer.SourceOrder(part.Nodes);
                foreach (var node in ordered)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (WithholdSanitizedPdfPageRaster(node)) continue;
                    if (attached.Contains(node.Id) || graph.Format == DocumentFormatKind.Xlsx && node.Kind == NodeKind.Cell) continue;
                    if (ExtensionText(node, "table_overlay_host") is { } host && overlayHosts.Contains(host)) continue;
                    if (ExtensionText(node, "table_grid_member_host") is { } gridHost && overlayHosts.Contains(gridHost)) continue;
                    if (node.Kind is NodeKind.Header or NodeKind.Footer or NodeKind.Footnote or NodeKind.Endnote ||
                        node.Layer == ContentLayer.Furniture || ExtensionBool(node, "pdf_page_furniture"))
                    { furniture.Add(node); continue; }
                    if (node.Kind == NodeKind.Image && ExtensionBool(node, "slide_furniture"))
                    { withheldImages.Add(node.Id); continue; }
                    if ((suppressUnscopedMembers || consumedShapes.Contains(node.Id) || ExtensionText(node, "shape_id") is { } shapeId && consumedShapes.Contains(shapeId)) &&
                        (ExtensionBool(node, "visual_graph_member") || ExtensionBool(node, "visual_graph_edge") || ExtensionBool(node, "visual_edge_label") || ExtensionBool(node, "visual_graph_node")))
                    { if (node.Kind == NodeKind.Image) withheldImages.Add(node.Id); continue; }
                    if (semantic.TryGetValue(node.Id, out var diagram)) blocks.Add(diagram);
                    else blocks.AddRange(Project(node));
                    AddRevisions(blocks, node);
                }
                // Nested nodes were moved into a cell, but structural revision history belongs to the document too.
                foreach (var node in part.Nodes.Where(n => attached.Contains(n.Id))) AddRevisions(blocks, node);
                blocks = CombineLists(blocks);
                blocks = PreferSourceDrawings(part, nodes, blocks);
                blocks = PreferPdfSourcePage(part, blocks);
                var printSettings = layout?.Sheets.GetValueOrDefault(part.Id)?.Print;
                if (graph.Format == DocumentFormatKind.Xlsx && (sourceSceneAvailable || sourcePagination))
                {
                    var complete = DocumentContentPolicyRules.Parse(options.ContentPolicy) == DocumentContentPolicy.Complete;
                    var scene = (sourceScene ?? new WorkbookDrawingScene(SheetName(part.Id), [])) with
                    {
                        Objects = (sourceScene?.Objects ?? []).Where(o => (complete || !o.Hidden) &&
                            (o.Kind != "picture" || o.SourceNodeId is { } sourceId && nodes.ContainsKey(sourceId))).Select(o =>
                            o.SourceNodeId is { } id && nodes.TryGetValue(id, out var imageNode) && imageNode.Content is ReferenceNodeContent image
                                ? o with { ImageReference = ExtensionBool(imageNode, "image_omitted") ? null : image.Reference }
                                : o.Kind == "picture" ? o with { ImageReference = null } : o).ToArray(),
                    };
                    var sourcePictures = scene.Objects.Where(o => o.Kind == "picture").Select(o => o.SourceNodeId).OfType<string>().ToHashSet(StringComparer.Ordinal);
                    var extras = blocks.Where(b => !ReferenceEquals(b, workbookTable) &&
                        !(b is ReadableImage image && image.Origin?.NodeIds?.Any(sourcePictures.Contains) == true)).ToArray();
                    blocks = [new ReadableWorksheetScene(workbookTable, scene, complete) { Origin = new(part.Id, SheetName(part.Id)) }];
                    var chartIds = part.Nodes.Where(n => n.Kind == NodeKind.Chart).Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
                    bool IsChartData(ReadableBlock block) => block.Origin?.NodeIds?.Any(chartIds.Contains) == true;
                    var chartData = extras.Where(IsChartData).ToArray();
                    if (chartData.Length > 0) blocks.Add(new ReadableDetails("worksheet-chart-data", ReadableContent.Text(ja ? "グラフの保存済みデータ" : "Saved chart data"), chartData, true));
                    var explanations = extras.Where(b => !IsChartData(b)).ToArray();
                    if (explanations.Length > 0) blocks.Add(new ReadableDetails("worksheet-explanations", ReadableContent.Text(ja ? "図の説明・注釈" : "Drawing descriptions and notes"), explanations));
                }
                if (blocks.Count == 0) continue;
                var name = graph.Format == DocumentFormatKind.Xlsx ? SheetName(part.Id) :
                    graph.Format == DocumentFormatKind.Pptx ? (ja ? "スライド " : "Slide ") + (part.Order + 1) :
                    graph.Format == DocumentFormatKind.Pdf ? (ja ? "ページ " : "Page ") + (part.Order + 1) : "";
                if (graph.Format == DocumentFormatKind.Docx && graph.Partitions.Count == 1) sections.AddRange(blocks);
                else sections.Add(new ReadableSection("source-part-" + part.Order.ToString(CultureInfo.InvariantCulture),
                    ReadableContent.Text(name), blocks) { Origin = new(part.Id, graph.Format == DocumentFormatKind.Xlsx ? SheetName(part.Id) : null) });
            }
            foreach (var group in furniture.GroupBy(n => ExtensionText(n, "pdf_furniture_position") switch
            {
                "header" => NodeKind.Header, "footer" => NodeKind.Footer,
                _ => n.Layer == ContentLayer.Furniture && graph.Format == DocumentFormatKind.Pptx ? NodeKind.Footer : n.Kind,
            }))
            {
                var blocks = group.DistinctBy(Plain).Select(n => (ReadableBlock)new ReadableParagraph(ReadableRunProjection.Content(n)) { Origin = Origin(n) }).ToList();
                foreach (var node in group) AddRevisions(blocks, node);
                var label = Label(group.Key) + (group.Key is NodeKind.Header or NodeKind.Footer ?
                    ja ? "（原本の保存値）" : " (source saved values)" : "");
                sections.Add(new ReadableDetails("document-notes", ReadableContent.Text(label), blocks, true));
            }
            var titleNode = graph.Nodes.FirstOrDefault(n => n.Kind == NodeKind.Heading && ExtensionBool(n, "document_title"));
            var title = options.Title?.Trim() ?? (titleNode is null ? options.FallbackTitle?.Trim() ?? (ja ? "ドキュメント" : "Document") : Plain(titleNode));
            var document = new ReadableDocument(ReadableRunProjection.Clean(title), ja ? "ja" : "en", graph.Format, sections, options.SourceName)
            {
                Styles = StylesWithSceneCells(),
                WorkbookLayout = layout is null ? null : WorkbookPresentationLayoutJson.Write(layout with
                { Sheets = layout.Sheets.Where(kv => sections.OfType<ReadableSection>().Any(s => s.Origin?.PartitionId == kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal) }),
            };
            var unwritten = graph.Nodes.Where(n => n.Kind == NodeKind.Image && !renderedImages.Contains(n.Id) && !withheldImages.Contains(n.Id)).Select(n => n.Id).ToArray();
            foreach (var id in unwritten) Add("ImageNotWritten", "An extracted image was not referenced by the readable projection.", id);
            var report = new ReadableProjectionReport(CountTables(sections), 0, []) { UnwrittenImageIds = unwritten };
            return new(document, diagnostics, report);
        }

        private IEnumerable<ReadableBlock> Project(DocumentNode node)
        {
            var origin = Origin(node);
            var content = ReadableRunProjection.Content(node);
            if (IsReadableOverlay(node))
            { foreach (var overlay in OverlayNotes(node)) yield return overlay; yield break; }
            switch (node.Kind)
            {
                case NodeKind.Workbook or NodeKind.Worksheet or NodeKind.TableRow or NodeKind.TableCell:
                    yield break;
                case NodeKind.Link:
                    yield break; // Native adapters already attach the link to its owning rich-text run.
                case NodeKind.Section when HasExtension(node, "section_orientation"):
                    yield break;
                case NodeKind.Heading or NodeKind.Section or NodeKind.Slide or NodeKind.Page or NodeKind.PdfPage:
                    if (content.PlainText.Length > 0)
                        yield return new ReadableHeading(Math.Clamp(ExtensionInt(node, "heading_level") ?? 2, 1, 6), content) { Origin = origin };
                    yield break;
                case NodeKind.Table when node.Content is TableNodeContent table:
                    yield return Table(node, table, 0);
                    foreach (var overlay in OverlayNotes(node)) yield return overlay;
                    yield break;
                case NodeKind.PageBreak:
                    yield return new ReadablePageBreak { Origin = origin };
                    yield break;
                case NodeKind.CodeBlock:
                    if (content.Runs.Any(r => r.Strike || r.Underline || r.Inserted || r.Deleted))
                        yield return new ReadableParagraph(content) { Origin = origin };
                    else yield return new ReadableCode(content.PlainText, ExtensionText(node, "code_language")) { Origin = origin };
                    yield break;
                case NodeKind.Image when node.Content is ReferenceNodeContent image:
                    renderedImages.Add(node.Id);
                    if (graph.Format == DocumentFormatKind.Xlsx && !sourceSceneAvailable && !ExtensionBool(node, "image_omitted"))
                        Add("ReadableImagePlacement", origin.Range is null
                            ? "The image is shown separately; its original cell anchoring and print-area membership could not be resolved. Use the source drawing part and relationship to compare its placement."
                            : "The image is shown separately with its source range; overlapping cells and annotation placement require comparison with the original.", node.Id);
                    if (!imageTextParents.Contains(node.Id))
                        Add("ImageSearchTextUnavailable", "No extracted text is available for this image; its description does not make all image text searchable.", node.Id, MarkdownDiagnosticSeverity.Info);
                    if (ExtensionBool(node, "image_omitted"))
                        yield return new ReadableQuote([new ReadableParagraph(ReadableContent.Text((ja ? "画像（省略）: " : "Image omitted: ") + (image.AltText ?? image.Reference)))]) { Origin = origin };
                    else yield return new ReadableImage(image.Reference, image.AltText ?? (ja ? "図" : "Image"),
                        Number(Extension(node, "display_width_px")), Number(Extension(node, "display_height_px"))) { Origin = origin };
                    yield break;
                case NodeKind.ImageText:
                    yield return Ocr(node);
                    yield break;
                case NodeKind.SpeakerNotes:
                    yield return new ReadableDetails("speaker-notes", ReadableContent.Text(ja ? "スピーカーノート" : "Speaker notes"), Paragraphs(node)) { Origin = origin };
                    yield break;
                case NodeKind.Comment:
                    var about = string.Join(" / ", new[] { ExtensionText(node, "comment_author"), ExtensionText(node, "comment_target"), ExtensionText(node, "cell_ref") }.OfType<string>());
                    yield return new ReadableQuote([new ReadableParagraph(ReadableContent.Text(Label(node.Kind) + (about.Length > 0 ? " (" + about + ")" : "") + ": " + content.PlainText))]) { Origin = origin };
                    yield break;
                case NodeKind.Quote or NodeKind.Annotation:
                    var review = ExtensionText(node, "pdf_review_reason");
                    var message = review == "PdfTableReconstructionFailed" ? (ja ? "表を組み立てられませんでした。原本と照合してください。" : "The table could not be reconstructed. Compare with the source.") : content.PlainText;
                    var quoted = new List<ReadableBlock> { new ReadableParagraph(ReadableContent.Text(message)) };
                    var descriptions = Extension(node, "pdf_image_descriptions");
                    if (descriptions.ValueKind == JsonValueKind.Array)
                        quoted.AddRange(descriptions.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => new ReadableParagraph(ReadableContent.Text(x.GetString()!))));
                    yield return new ReadableQuote(quoted) { Origin = origin };
                    yield break;
                case NodeKind.List or NodeKind.ListItem or NodeKind.Connector:
                    var ordered = ExtensionText(node, "list_format") == "ordered";
                    var number = ExtensionInt(node, "list_number");
                    if (ordered && number is { } n) content = StripPrefix(content, n.ToString(CultureInfo.InvariantCulture) + ". ");
                    yield return new ReadableList([new(content, Math.Clamp(ExtensionInt(node, "list_level") ?? 0, 0, 16), ordered, number) { Origin = origin }]) { Origin = origin };
                    yield break;
                case NodeKind.Chart when HasExtension(node, "chart_series"):
                    foreach (var block in Chart(node)) yield return block;
                    yield break;
                case NodeKind.Diagram:
                    yield return Diagram(node);
                    yield break;
            }
            if (ExtensionBool(node, "pdf_textless_placeholder")) content = ReadableContent.Text(ja ? "このページの文字を抽出できませんでした。原本を確認してください。" : "Text could not be extracted from this page. Compare with the source.");
            if (ExtensionBool(node, "pdf_unparsed_form_xobjects")) content = ReadableContent.Text(ja ? "解析できなかった描画部品があります。原本を確認してください。" : "Some drawing objects could not be parsed. Compare with the source.");
            if (ExtensionBool(node, "pdf_uncertain_text")) content = ReadableContent.Text(ja ? "文字や表示状態を確定できない箇所があります。原本を確認してください。" : "Some text or its visibility could not be determined. Compare with the source.");
            if (HasExtension(node, "paragraph_details"))
            { foreach (var block in Paragraphs(node)) yield return block; }
            else if (content.PlainText.Length > 0) yield return new ReadableParagraph(content) { Origin = origin };
        }

        private ReadableSourceOrigin Origin(DocumentNode node) => ReadableSourceProvenance.ForNode(node,
            owners.GetValueOrDefault(node.Id, partition).Id, graph.Format == DocumentFormatKind.Xlsx);

        private string Label(NodeKind kind) => (kind, ja) switch
        {
            (NodeKind.Header, true) => "ヘッダー", (NodeKind.Footer, true) => "フッター",
            (NodeKind.Footnote, true) => "脚注", (NodeKind.Endnote, true) => "文末脚注", (NodeKind.Comment, true) => "コメント", _ => kind.ToString(),
        };

        private void Add(string code, string message, string? nodeId = null, MarkdownDiagnosticSeverity severity = MarkdownDiagnosticSeverity.Warning)
        {
            if (!diagnostics.Any(d => d.Code == code && d.BlockId == nodeId && d.Message == message))
                diagnostics.Add(new(code, message, severity, nodeId));
        }

        private static void AddRevisions(List<ReadableBlock> blocks, DocumentNode node)
        {
            var changes = Extension(node, "revision_changes");
            if (changes.ValueKind != JsonValueKind.Array) return;
            foreach (var change in changes.EnumerateArray())
                if (Text(change, "description") is { } description)
                    blocks.Add(new ReadableQuote([new ReadableParagraph(ReadableContent.Text("変更履歴 / Revision (" + (Text(change, "context") ?? "") + "): " + description))]));
        }

        private static string Plain(DocumentNode node) => node.Content switch
        { TextNodeContent t => t.Text, RichTextNodeContent r => string.Concat(r.Runs.Select(run => run.Text)), ReferenceNodeContent r => r.AltText ?? "", _ => "" };
        private static JsonElement Property(JsonElement value, params string[] names)
        { foreach (var name in names) if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var result)) return result; return default; }
        private static string? Text(JsonElement value, params string[] names)
        { var result = Property(value, names); return result.ValueKind == JsonValueKind.String ? ReadableRunProjection.Clean(result.GetString()!) : null; }
        private static double? Number(JsonElement value) => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var d) && double.IsFinite(d) ? d : null;
        private static int Int(JsonElement value, params string[] names) => Number(Property(value, names)) is >= 0 and <= int.MaxValue and var d ? (int)d : 0;
        private static bool Flag(JsonElement value, params string[] names) => Property(value, names).ValueKind == JsonValueKind.True;
        private static int CountTables(IEnumerable<ReadableBlock> blocks) => blocks.Sum(b => b switch
        {
            ReadableTable t => 1 + t.Rows.Sum(r => r.Cells.Sum(c => CountTables(c.Children))),
            ReadableWorksheetScene s => s.Grid is null ? 0 : CountTables([s.Grid]),
            // A table a slide layout or master lends to the scene is drawn on every slide and counted on none.
            ReadableSourceDrawingScene s => CountTables(s.ObjectBlocks.Where(block => !block.Key.StartsWith(LentTableKey, StringComparison.Ordinal)).Select(block => block.Value)) +
                CountTables(s.ChartData.Values.SelectMany(blocks => blocks)),
            ReadableSection s => CountTables(s.Blocks), ReadableQuote q => CountTables(q.Blocks),
            ReadableDetails d when d.Kind != "ocr-extraction" => CountTables(d.Blocks), _ => 0,
        });
        private static List<ReadableBlock> CombineLists(IEnumerable<ReadableBlock> blocks)
        {
            var result = new List<ReadableBlock>();
            foreach (var block in blocks)
                if (block is ReadableList list && result.LastOrDefault() is ReadableList previous)
                    result[^1] = previous with { Items = previous.Items.Concat(list.Items).ToArray() };
                else result.Add(block);
            return result;
        }
        private static ReadableContent StripPrefix(ReadableContent content, string prefix) =>
            content.PlainText.StartsWith(prefix, StringComparison.Ordinal) ? Slice(content, prefix.Length, content.PlainText.Length - prefix.Length) : content;
        private static ReadableContent Slice(ReadableContent content, int start, int length)
        {
            var runs = new List<TextRun>(); var position = 0;
            foreach (var run in content.Runs)
            {
                var from = Math.Max(start - position, 0); var end = Math.Min(start + length - position, run.Text.Length);
                if (end > from) runs.Add(run with { Text = run.Text[from..end] });
                position += run.Text.Length;
            }
            return new(runs);
        }
    }
}
