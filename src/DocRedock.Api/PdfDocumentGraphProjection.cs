using System.Text.Json;
using DocRedock.Core.Documents;
using DocRedock.Core.Reporting;
using DocRedock.Formats.Pdf;

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
            var nodes = page.Regions.Where(_ => includeTextlessPlaceholderNodes || !page.IsImageOnly).Select(region =>
            {
                // A visual graph is derived from the page's native text/geometry. Mark only
                // source regions that actually label graph nodes, never the Diagram node itself.
                var isVisualMember = hasTopology && visualGraph!.Nodes.Any(visualNode =>
                    string.Equals(visualNode.Label, region.Text, StringComparison.Ordinal));
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
                    if (projected.Kind == NodeKind.Paragraph && projected.Content is TextNodeContent text && hasTopology &&
                        visualGraph!.Nodes.Any(member => member.Label == text.Text && member.Geometry is { } box &&
                            projected.Geometry is { } region &&
                            region.X + region.Width / 2 >= box.X && region.X + region.Width / 2 <= box.X + box.Width &&
                            region.Y + region.Height / 2 >= box.Y && region.Y + region.Height / 2 <= box.Y + box.Height))
                        extensions["visual_graph_member"] = JsonSerializer.SerializeToElement(true);
                    return projected with
                    {
                        Source = new SourceAnchor("pdf", $"pdf:page:{page.PageNumber}",
                            [new AnchorLocator("reading_order", projected.Order.ToString(System.Globalization.CultureInfo.InvariantCulture))]),
                        Extensions = extensions
                    };
                }).ToList();
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
