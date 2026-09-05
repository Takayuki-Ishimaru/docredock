using System.Text.Json;
using DocRedock.Core.Documents;
using DocRedock.Core.Reporting;

namespace DocRedock.Api;

public sealed record ExportSummary(int Warnings, int Tables, int Diagrams, int FallbackPages)
{
    public override string ToString() => $"Export completed\nOutput: Markdown\nWarnings: {Warnings}\nTables reconstructed: {Tables}\nDiagrams reconstructed: {Diagrams}\nFallback pages: {FallbackPages}";
}

public static class ExportSummaryBuilder
{
    public static ExportSummary Build(DocumentGraph graph, IReadOnlyList<Diagnostic> diagnostics)
    {
        var graphs = graph.Nodes.Select(ReadVisualGraph).OfType<VisualGraph>().ToArray();
        var diagrams = graphs.Count(HasResolvedKnownEdge);
        // A PDF page can yield several diagram nodes.  Count its partition once,
        // so the displayed fallback total is a page total rather than a node total.
        var fallbackPages = graph.Partitions.Count(partition => partition.Nodes.Select(ReadVisualGraph)
            .OfType<VisualGraph>().Any(HasFallback));
        return new(diagnostics.Count(diagnostic => diagnostic.Severity == DiagnosticSeverity.Warning),
            graph.Nodes.Count(node => node.Kind == NodeKind.Table), diagrams, fallbackPages);
    }

    private static VisualGraph? ReadVisualGraph(DocumentNode node) => node.Extensions?.TryGetValue("visual_graph", out var value) == true
        ? value.Deserialize<VisualGraph>() : null;

    private static bool HasResolvedKnownEdge(VisualGraph graph)
    {
        var ids = (graph.Nodes ?? []).Where(node => node is not null).Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
        return (graph.Edges ?? []).Any(edge => edge is not null && edge.Resolution != VisualEdgeResolution.Unresolved &&
            edge.SourceId is not null && edge.TargetId is not null && ids.Contains(edge.SourceId) && ids.Contains(edge.TargetId));
    }

    private static bool HasFallback(VisualGraph graph) => (graph.Paths ?? []).Any(path => path?.IsFallback == true) ||
        (graph.SourceItems ?? []).Any(item => item?.Disposition == VisualDisposition.VisualFallback);
}
