using DocRedock.Core.Documents;
using DocRedock.Core.Reporting;

namespace DocRedock.Api;

public sealed partial class DocumentService
{
    private static DocumentGraph WithholdSanitizedPdfPageRasters(DocumentGraph graph, ICollection<Diagnostic> diagnostics)
    {
        if (graph.Format != DocumentFormatKind.Pdf) return graph;
        var omitted = graph.Nodes.Where(node => node.Kind == NodeKind.Image &&
            HasTrueExtension(node, "pdf_page_raster")).Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
        if (omitted.Count == 0) return graph;

        // Run before image asset selection/embedding. Hiding a figure in the renderer alone
        // would leave excluded furniture in a published PNG or in self-contained HTML bytes.
        foreach (var attached in diagnostics.Where(diagnostic => diagnostic.Code == "PdfReviewImageAttached").ToArray())
            diagnostics.Remove(attached);
        diagnostics.Add(new Diagnostic("PdfSourcePagePreviewWithheld",
            "Original PDF page images were withheld by sanitized policy because their pixels may contain excluded furniture or annotations.",
            DiagnosticSeverity.Information));
        return graph with
        {
            Partitions = graph.Partitions.Select(partition => partition with
            { Nodes = partition.Nodes.Where(node => !omitted.Contains(node.Id)).ToArray() }).ToArray()
        };
    }
}
