using System.Text.Json;
using DocRedock.Core.Documents;
using DocRedock.Core.Reporting;
using DocRedock.Formats.Pdf;
using DocRedock.Markdown;

namespace DocRedock.Api;

/// <summary>Projects PDF extraction results into the shared graph without discarding vector topology metadata.</summary>
internal static class PdfDocumentGraphProjection
{
    public static DocumentGraph CreateGraph(PdfExtractionResult extraction, string sourceHash, bool includeTextlessPlaceholderNodes = true)
    {
        ArgumentNullException.ThrowIfNull(extraction);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceHash);
        var hashPrefix = sourceHash[..Math.Min(16, sourceHash.Length)];
        var partitions = extraction.Pages.Select(page =>
        {
            VisualGraph? visualGraph = null;
            extraction.VisualGraphs?.TryGetValue(page.PageNumber, out visualGraph);
            if (extraction.VisualProjections?.GetValueOrDefault(page.PageNumber) is { } projection)
                visualGraph = projection.Graph;
            var hasTopology = visualGraph?.HasTopology == true;
            // A visual graph is derived from the page's native text/geometry. Membership is
            // decided by which fragment a node actually consumed as its label - the extractor
            // records that assignment by stable source text id - never by comparing rendered
            // strings: a heading or a sentence that merely repeats a node's caption ("START"
            // above the diagram, "START" in a paragraph) is ordinary body text, and a readable
            // projection that skips graph members would otherwise delete it with no diagnostic.
            var graphNodeIds = hasTopology
                ? visualGraph!.Nodes.Select(visualNode => visualNode.Id).ToHashSet(StringComparer.Ordinal)
                : null;
            var labelNodeIds = page.VisualLabelNodeIds;
            bool IsConsumedNodeLabel(PdfTextRegion region) =>
                graphNodeIds is not null && labelNodeIds is { Count: > 0 } && region.SourceTextIds.Count > 0 &&
                // Every fragment of a merged readable line must be label-consumed; a line that
                // also carries unconsumed text is body text that happens to touch a node.
                region.SourceTextIds.All(id => labelNodeIds.TryGetValue(id, out var nodeId) && graphNodeIds.Contains(nodeId));
            // The table path below re-projects the same regions without their source ids, so key
            // the decision it needs by the (text, bounds) pair those nodes carry verbatim.
            var consumedLabelBounds = graphNodeIds is null
                ? null
                : page.Regions.Where(IsConsumedNodeLabel).Select(region => (region.Text, region.BoundingBox)).ToHashSet();
            var nodes = page.Regions.Where(_ => includeTextlessPlaceholderNodes || !page.IsImageOnly).Select(region =>
            {
                var isVisualMember = IsConsumedNodeLabel(region);
                IReadOnlyDictionary<string, JsonElement>? extensions = isVisualMember
                    ? new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                    {
                        ["visual_graph_member"] = JsonSerializer.SerializeToElement(true)
                    }
                    : page.IsImageOnly
                        ? new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                        { ["pdf_textless_placeholder"] = JsonSerializer.SerializeToElement(true) }
                        : null;
                return new DocumentNode(
                $"n_{hashPrefix[..Math.Min(8, hashPrefix.Length)]}_{page.PageNumber}_{region.ReadingOrder}", NodeKind.Paragraph, null, region.ReadingOrder,
                ContentLayer.Body, new TextNodeContent(region.Text),
                new SourceAnchor("pdf", $"pdf:page:{page.PageNumber}", [new AnchorLocator("reading_order", region.ReadingOrder.ToString())]),
                Geometry: region.BoundingBox, Editability: NodeEditability.RenderOnly,
                Provenance: [new ProvenanceItem(EvidenceKind.Native, PageNumber: page.PageNumber, Bbox: region.BoundingBox)], Extensions: extensions);
            }).ToList();
            if (extraction.Tables?.GetValueOrDefault(page.PageNumber) is { Count: > 0 } tables)
            {
                nodes = PdfPageProjection.ToDocumentNodes(page, tables).Select(projected =>
                {
                    var extensions = projected.Extensions is null
                        ? new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                        : new Dictionary<string, JsonElement>(projected.Extensions, StringComparer.Ordinal);
                    if (projected.Kind == NodeKind.Paragraph && projected.Content is TextNodeContent text &&
                        projected.Geometry is { } projectedBounds &&
                        consumedLabelBounds?.Contains((text.Text, projectedBounds)) == true)
                        extensions["visual_graph_member"] = JsonSerializer.SerializeToElement(true);
                    return projected with
                    {
                        Source = new SourceAnchor("pdf", $"pdf:page:{page.PageNumber}",
                            [new AnchorLocator("reading_order", projected.Order.ToString(System.Globalization.CultureInfo.InvariantCulture))]),
                        Extensions = extensions
                    };
                }).ToList();
            }
            // A page that mixes native text with an embedded image used to drop the image without
            // leaving anything behind - no link, no asset, no diagnostic - because only textless
            // pages were ever treated as image-bearing. Record what is missing at the image's own
            // reading position; the OCR path replaces this node with the rasterized page.
            if (!page.IsImageOnly && page.EmbeddedImageCount > 0)
            {
                var bounds = MergeBounds(page.EmbeddedImages);
                var placeholder = new DocumentNode(
                    $"n_{hashPrefix[..Math.Min(8, hashPrefix.Length)]}_{page.PageNumber}_embedded_images", NodeKind.Annotation, null, nodes.Count,
                    ContentLayer.Body,
                    new TextNodeContent($"[PDF page {page.PageNumber}: {page.EmbeddedImageCount} embedded image(s) not extracted; run with --ocr on and a configured rasterizer to recover image text]"),
                    new SourceAnchor("pdf", $"pdf:page:{page.PageNumber}", [new AnchorLocator("embedded_images", page.PageNumber.ToString())]),
                    Geometry: bounds, Editability: NodeEditability.RenderOnly,
                    Provenance: [new ProvenanceItem(EvidenceKind.Native, PageNumber: page.PageNumber, Bbox: bounds)],
                    Extensions: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                    {
                        ["pdf_embedded_image_placeholder"] = JsonSerializer.SerializeToElement(true),
                        ["pdf_embedded_image_count"] = JsonSerializer.SerializeToElement(page.EmbeddedImageCount)
                    });
                nodes = InsertAtReadingPosition(nodes, placeholder, bounds);
            }
            // A drawn Form XObject the extractor could not inline painted text or drawings that are
            // missing here. The rest of the page's text made such a page look complete - no warning,
            // no review - so the spot is marked, and the node asks for source comparison itself.
            if (!page.IsImageOnly && page.UnparsedFormXObjects is { Count: > 0 } unparsed)
            {
                var bounds = MergeBounds(unparsed.Select(form => form.Bounds).OfType<Geometry>().ToArray());
                var marker = new DocumentNode(
                    $"n_{hashPrefix[..Math.Min(8, hashPrefix.Length)]}_{page.PageNumber}_unparsed_forms", NodeKind.Annotation, null, nodes.Count,
                    ContentLayer.Body,
                    new TextNodeContent($"[PDF page {page.PageNumber}: {unparsed.Count} drawing component(s) (Form XObject) could not be analyzed; their text and graphics are missing here - compare with the source page]"),
                    new SourceAnchor("pdf", $"pdf:page:{page.PageNumber}", [new AnchorLocator("unparsed_form_xobjects", page.PageNumber.ToString())]),
                    Geometry: bounds, Editability: NodeEditability.RenderOnly,
                    Provenance: [new ProvenanceItem(EvidenceKind.Native, PageNumber: page.PageNumber, Bbox: bounds)],
                    Extensions: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                    {
                        [ReadableMarkdownSerializer.SourceReviewRequiredExtension] = JsonSerializer.SerializeToElement(true),
                        [UnparsedFormXObjectsExtension] = JsonSerializer.SerializeToElement(unparsed.Select(form =>
                            new PdfUnparsedFormRecord(form.Name, ReasonCode(form.Reason), form.Bounds)).ToArray(), RecordJson)
                    });
                nodes = InsertAtReadingPosition(nodes, marker, bounds);
            }
            if (visualGraph is not null)
            {
                var visualAnchor = new SourceAnchor("pdf", $"pdf:page:{page.PageNumber}", [new AnchorLocator("visual_graph", page.PageNumber.ToString())]);
                nodes.Add(new DocumentNode($"n_{hashPrefix[..Math.Min(8, hashPrefix.Length)]}_{page.PageNumber}_visual", NodeKind.Diagram, null, nodes.Count,
                    ContentLayer.Derived, new TextNodeContent("PDF visual graph"), visualAnchor, Editability: NodeEditability.RenderOnly,
                    Provenance: [new ProvenanceItem(EvidenceKind.Native, PageNumber: page.PageNumber)],
                    Extensions: new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                    {
                        ["visual_graph"] = JsonSerializer.SerializeToElement(visualGraph),
                        ["diagram_language"] = JsonSerializer.SerializeToElement("mermaid"),
                        ["visual_fallback_suppressed"] = JsonSerializer.SerializeToElement(visualGraph.HasTopology)
                    }));
            }
            return new DocumentPartition($"page-{page.PageNumber:D4}", page.PageNumber - 1, nodes, $"pdf:page:{page.PageNumber}");
        }).ToArray();
        return new DocumentGraph(DocumentGraph.CurrentSchemaVersion, "doc_" + hashPrefix, DocumentFormatKind.Pdf, partitions);
    }

    /// <summary>Extension carrying one <see cref="PdfUnparsedFormRecord"/> per Form XObject draw the
    /// extractor could not analyze, on the page's review marker node.</summary>
    internal const string UnparsedFormXObjectsExtension = "pdf_unparsed_form_xobjects";

    /// <summary>One unanalyzed form draw as stored on the graph: its resource name, a stable reason
    /// code, and where it paints in PDF user space when that is known.</summary>
    internal sealed record PdfUnparsedFormRecord(string Name, string Reason, Geometry? Bounds);

    private static readonly JsonSerializerOptions RecordJson = new();

    private static string ReasonCode(PdfFormXObjectUnparsedReason reason) => reason switch
    {
        PdfFormXObjectUnparsedReason.UnreadableStream => "unreadable-stream",
        PdfFormXObjectUnparsedReason.NestingLimit => "nesting-limit",
        PdfFormXObjectUnparsedReason.SizeLimit => "size-limit",
        _ => "unresolved-resource",
    };

    /// <summary>Inserts a marker where its content sits in reading order. PDF user space grows
    /// upward and the page's nodes are already in reading order, so the marker belongs after every
    /// node whose top edge is at or above its own top; without a resolved rectangle there is
    /// nothing to place it by and it goes to the end. Order is then renumbered so it stays a total
    /// order over the page - a duplicated Order would make downstream ordering non-deterministic.</summary>
    private static List<DocumentNode> InsertAtReadingPosition(List<DocumentNode> nodes, DocumentNode marker, Geometry? bounds)
    {
        // Another marker is not content to read past: one without a position sits at the end, and
        // must not drag this one there with it.
        var insertAt = bounds is null
            ? nodes.Count
            : nodes.FindLastIndex(node => !IsMarker(node) && (node.Geometry is not { } geometry ||
                geometry.Y + geometry.Height >= bounds.Y + bounds.Height)) + 1;
        nodes.Insert(insertAt, marker);
        return nodes.Select((node, order) => node with { Order = order }).ToList();
    }

    private static bool IsMarker(DocumentNode node) => node.Extensions is { } extensions &&
        (extensions.ContainsKey("pdf_embedded_image_placeholder") || extensions.ContainsKey(UnparsedFormXObjectsExtension));

    /// <summary>Union of the placement rectangles the extractor could resolve, or null when it
    /// resolved none - the page then carries a count but no position.</summary>
    private static Geometry? MergeBounds(IReadOnlyList<Geometry>? bounds)
    {
        if (bounds is not { Count: > 0 }) return null;
        var left = bounds.Min(item => item.X);
        var bottom = bounds.Min(item => item.Y);
        var right = bounds.Max(item => item.X + item.Width);
        var top = bounds.Max(item => item.Y + item.Height);
        return new Geometry(bounds[0].CoordinateSpace, left, bottom, right - left, top - bottom);
    }

    public static IReadOnlyList<Diagnostic> Diagnostics(PdfExtractionResult extraction) => extraction.Diagnostics?.Select(message =>
    {
        var separator = message.IndexOf(':');
        var candidate = separator > 0 ? message[..separator] : string.Empty;
        var hasCode = (candidate.StartsWith("Pdf", StringComparison.Ordinal) || candidate.StartsWith("Visual", StringComparison.Ordinal)) &&
            candidate.All(char.IsLetterOrDigit);
        var code = hasCode ? candidate : "VisualSemanticProjectionUnavailable";
        var detail = hasCode ? message[(separator + 1)..].Trim() : message;
        var severity = code is "PdfTableInferred" or "PdfTableNative" ? DiagnosticSeverity.Information : DiagnosticSeverity.Warning;
        var diagnostic = new Diagnostic(code, detail, severity);
        if (code == "VisualFallbackCompacted" && extraction.VisualFallbacks is not null)
        {
            var page = extraction.VisualFallbacks.FirstOrDefault(item =>
                detail.StartsWith($"PDF page {item.Key}:", StringComparison.Ordinal));
            if (page.Value is not null)
                diagnostic = diagnostic with { Data = new SortedDictionary<string, string>(StringComparer.Ordinal)
                {
                    ["page"] = page.Key.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["primitive_count"] = (extraction.VisualGraphs?.GetValueOrDefault(page.Key)?.Paths?.Count ?? page.Value.TotalFallbackPaths).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["emitted_count"] = page.Value.Paths.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["omitted_count"] = page.Value.OmittedFallbackPaths.ToString(System.Globalization.CultureInfo.InvariantCulture)
                }};
        }
        return diagnostic;
    }).ToArray() ?? [];
}
