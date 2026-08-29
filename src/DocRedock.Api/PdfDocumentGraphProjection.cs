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
                    : null;
                return new DocumentNode(
                $"n_{hashPrefix[..Math.Min(8, hashPrefix.Length)]}_{page.PageNumber}_{region.ReadingOrder}", NodeKind.Paragraph, null, region.ReadingOrder,
                ContentLayer.Body, new TextNodeContent(region.Text),
                new SourceAnchor("pdf", $"pdf:page:{page.PageNumber}", [new AnchorLocator("reading_order", region.ReadingOrder.ToString())]),
                Geometry: region.BoundingBox, Editability: NodeEditability.RenderOnly,
                Provenance: [new ProvenanceItem(EvidenceKind.Native, PageNumber: page.PageNumber, Bbox: region.BoundingBox)], Extensions: extensions);
            }).ToList();
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
        AdapterWarningDiagnostics.Create(message.StartsWith("PdfRasterizerUnavailable", StringComparison.Ordinal)
            ? "PdfRasterizerUnavailable" : "VisualSemanticProjectionUnavailable", message)).ToArray() ?? [];
}
