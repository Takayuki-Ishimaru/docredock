using DocRedock.Core.Documents;

namespace DocRedock.Formats.Pdf;

/// <summary>Hard limits for the readable projection of PDF vector fallback.  The native
/// graph remains complete, so accounting and evidence retain the original totals.</summary>
public sealed record PdfVisualOutputBudget(
    int MaxFallbackPathsPerPage = 100,
    int MaxFallbackCharactersPerPage = 32_768,
    int MaxDetailedDiagnosticsPerCodePerPage = 10,
    int MaxTableCandidatesPerPage = 64,
    int MaxGraphNodesPerPage = 512,
    int MaxGraphEdgesPerPage = 1024)
{
    private const int HardMaxFallbackPaths = 100;
    private const int HardMaxFallbackCharacters = 32_768;
    private const int HardMaxDetailedDiagnostics = 10;
    private const int HardMaxTableCandidates = 64;
    private const int HardMaxGraphNodes = 512;
    private const int HardMaxGraphEdges = 1024;

    internal PdfVisualOutputBudget Normalize() => this with
    {
        MaxFallbackPathsPerPage = Math.Clamp(MaxFallbackPathsPerPage, 0, HardMaxFallbackPaths),
        MaxFallbackCharactersPerPage = Math.Clamp(MaxFallbackCharactersPerPage, 0, HardMaxFallbackCharacters),
        MaxDetailedDiagnosticsPerCodePerPage = Math.Clamp(MaxDetailedDiagnosticsPerCodePerPage, 0, HardMaxDetailedDiagnostics),
        MaxTableCandidatesPerPage = Math.Clamp(MaxTableCandidatesPerPage, 0, HardMaxTableCandidates),
        MaxGraphNodesPerPage = Math.Clamp(MaxGraphNodesPerPage, 0, HardMaxGraphNodes),
        MaxGraphEdgesPerPage = Math.Clamp(MaxGraphEdgesPerPage, 0, HardMaxGraphEdges)
    };
}

public sealed record PdfVisualFallbackProjection(
    IReadOnlyList<VisualPath> Paths,
    int TotalFallbackPaths,
    int OmittedFallbackPaths,
    bool IsCompacted);

/// <summary>Bounded readable projection of a complete native visual graph. The original graph
/// is retained for accounting/evidence; this graph is the only one a Markdown projection should
/// render.</summary>
public sealed record PdfVisualGraphProjection(VisualGraph Graph, bool IsDowngraded, int OmittedNodes, int OmittedEdges);

public static class PdfVisualOutputCompactor
{
    /// <summary>Removes primitives consumed by a reconstructed table from the readable visual
    /// projection. The caller retains the original graph for evidence/accounting.</summary>
    public static VisualGraph RemoveConsumedTableVisuals(VisualGraph graph, IReadOnlyList<PdfTable> tables)
    {
        ArgumentNullException.ThrowIfNull(graph); ArgumentNullException.ThrowIfNull(tables);
        var pathIds = tables.SelectMany(table => table.SourcePathIds).ToHashSet(StringComparer.Ordinal);
        if (pathIds.Count == 0) return graph;
        var source = graph.SourceItems ?? [];
        var nodeIds = source.Where(item => pathIds.Contains(item.Id)).Select(item => item.ProjectedNodeId)
            .Where(id => id is not null).Cast<string>().ToHashSet(StringComparer.Ordinal);
        var edgeIds = source.Where(item => pathIds.Contains(item.Id)).Select(item => item.ProjectedEdgeId)
            .Where(id => id is not null).Cast<string>().ToHashSet(StringComparer.Ordinal);
        var nodes = (graph.Nodes ?? []).Where(node => !nodeIds.Contains(node.Id)).ToArray();
        var edges = (graph.Edges ?? []).Where(edge => !edgeIds.Contains(edge.Id) &&
            !nodeIds.Contains(edge.SourceId ?? string.Empty) && !nodeIds.Contains(edge.TargetId ?? string.Empty)).ToArray();
        var paths = (graph.Paths ?? []).Where(path => !pathIds.Contains(path.Id)).ToArray();
        var items = source.Select(item => pathIds.Contains(item.Id)
            ? item with { Disposition = VisualDisposition.IgnoredDecorative, ProjectedNodeId = null, ProjectedEdgeId = null,
                FallbackPathId = null, Reason = "reconstructed table grid consumed by table projection" }
            : item).ToArray();
        var projection = new VisualGraph(graph.Id, nodes, edges, graph.Diagnostics, graph.Direction, graph.Groups, paths, items);
        return projection with { Quality = VisualGraphValidator.ComputeQuality(projection) };
    }

    public static PdfVisualGraphProjection ProjectGraph(VisualGraph graph, PdfVisualOutputBudget? budget = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var limits = (budget ?? new PdfVisualOutputBudget()).Normalize();
        var nodes = graph.Nodes ?? [];
        var edges = graph.Edges ?? [];
        if (nodes.Count <= limits.MaxGraphNodesPerPage && edges.Count <= limits.MaxGraphEdgesPerPage)
            return new PdfVisualGraphProjection(graph, false, 0, 0);

        var paths = (graph.Paths ?? []).Select(path => path with { IsFallback = true }).ToArray();
        var pathIds = paths.Select(path => path.Id).ToHashSet(StringComparer.Ordinal);
        var source = (graph.SourceItems ?? []).Select(item => item.Disposition is VisualDisposition.ProjectedNode or VisualDisposition.ProjectedEdge
            ? pathIds.Contains(item.Id)
                ? item with { Disposition = VisualDisposition.VisualFallback, ProjectedNodeId = null, ProjectedEdgeId = null, FallbackPathId = item.Id,
                    Reason = "semantic graph exceeded readable output budget; source retained as fallback" }
                : item with { Disposition = VisualDisposition.DiagnosticOnly, ProjectedNodeId = null, ProjectedEdgeId = null, FallbackPathId = null,
                    DiagnosticCode = "VisualOutputBudgetExceeded", Reason = "semantic graph exceeded readable output budget" }
            : item).ToArray();
        var diagnostic = new VisualDiagnostic("VisualOutputBudgetExceeded",
            $"Semantic graph has {nodes.Count} nodes and {edges.Count} edges; readable projection was downgraded to bounded fallback.",
            Fallback: "bounded vector fallback", Remedy: "increase source simplicity; hard output caps cannot be bypassed",
            Format: "pdf");
        // Endpoint diagnostics describe the complete graph. Once every relation has been
        // deliberately downgraded to fallback, retaining them would violate INV-03 against
        // the projection's zero unresolved-edge count and can turn a valid dense PDF into a
        // fatal extraction error.
        var diagnostics = (graph.Diagnostics ?? []).Where(item => item.Code != "VisualConnectorUnresolved").ToArray();
        var projected = new VisualGraph(graph.Id, [], [], [.. diagnostics, diagnostic], graph.Direction,
            graph.Groups, paths, source, VisualGraphQuality.FallbackOnly);
        return new PdfVisualGraphProjection(projected, true, nodes.Count, edges.Count);
    }

    public static PdfVisualFallbackProjection Compact(VisualGraph graph, PdfVisualOutputBudget? budget = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var limits = (budget ?? new PdfVisualOutputBudget()).Normalize();
        var fallback = (graph.Paths ?? []).Where(path => path is not null && path.IsFallback).ToArray();
        var emitted = new List<VisualPath>(Math.Min(fallback.Length, limits.MaxFallbackPathsPerPage));
        long characters = 0;
        foreach (var path in fallback)
        {
            // A path is represented by a bounded coordinate list in the renderer. Estimate
            // pessimistically here so any projection is bounded even in verbose mode.
            var cost = EstimateRenderedCharacters(path);
            if (emitted.Count >= limits.MaxFallbackPathsPerPage || characters > limits.MaxFallbackCharactersPerPage - cost) break;
            emitted.Add(path);
            characters += cost;
        }
        return new PdfVisualFallbackProjection(emitted, fallback.Length, fallback.Length - emitted.Count,
            emitted.Count != fallback.Length);
    }

    private static long EstimateRenderedCharacters(VisualPath path)
    {
        // 32 characters per coordinate is sufficient for a finite IEEE 754 value rendered
        // with invariant culture. Include the path ID and punctuation; saturate rather than
        // allowing a maliciously large point collection to wrap the budget arithmetic.
        var pointCount = path.Points?.Count ?? 0;
        try { return checked(Math.Max(1, path.Id.Length + 32L + pointCount * 64L)); }
        catch (OverflowException) { return long.MaxValue; }
    }
}
