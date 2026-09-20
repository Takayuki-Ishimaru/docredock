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
        // P-Overlay: a ruling-line path whose SourceItem disposition is VisualFallback rather than
        // ProjectedEdge -- SuppressTableGridEdges declined to suppress it as an edge (e.g. a wide
        // schedule-overlay shape happened to touch that same ruling line, tripping its
        // network-diagram guard) -- is still one of this table's own boundaries, and must not
        // linger as a dangling, unresolved connector just because the ledger never called it a
        // "projected edge". Matched by Points *reference*, the same technique BuildSourceItems uses.
        var pathsByPoints = new Dictionary<IReadOnlyList<VisualPathPoint>, VisualPath>(ReferenceEqualityComparer.Instance);
        foreach (var path in graph.Paths ?? [])
            if (path.Points is { } points) pathsByPoints.TryAdd(points, path);
        var strandedEdgeIds = (graph.Edges ?? []).Where(edge => edge.Path is { } points &&
                pathsByPoints.TryGetValue(points, out var path) && pathIds.Contains(path.Id))
            .Select(edge => edge.Id).ToArray();
        edgeIds.UnionWith(strandedEdgeIds);
        var nodes = (graph.Nodes ?? []).Where(node => !nodeIds.Contains(node.Id)).ToArray();
        var edges = (graph.Edges ?? []).Where(edge => !edgeIds.Contains(edge.Id) &&
            !nodeIds.Contains(edge.SourceId ?? string.Empty) && !nodeIds.Contains(edge.TargetId ?? string.Empty)).ToArray();
        var paths = (graph.Paths ?? []).Where(path => !pathIds.Contains(path.Id)).ToArray();
        var items = source.Select(item => pathIds.Contains(item.Id)
            ? item with { Disposition = VisualDisposition.IgnoredDecorative, ProjectedNodeId = null, ProjectedEdgeId = null,
                FallbackPathId = null, Reason = "reconstructed table grid consumed by table projection" }
            : item).ToArray();
        var removedEdges = (graph.Edges ?? []).Where(edge => !edges.Any(kept => kept.Id == edge.Id)).ToArray();
        var removedIds = pathIds.Concat(nodeIds).Concat(removedEdges.Select(e => e.Id)).ToHashSet(StringComparer.Ordinal);
        var diagnostics = ReconcileConsumedDiagnostics(graph.Diagnostics, removedIds,
            removedEdges.Count(edge => edge.SourceId is null || edge.TargetId is null));
        var projection = new VisualGraph(graph.Id, nodes, edges, diagnostics, graph.Direction, graph.Groups, paths, items);
        return projection with { Quality = VisualGraphValidator.ComputeQuality(projection) };
    }

    // Only diagnostics for consumed objects are resolved. Legacy graphs without
    // object IDs retain the bounded count-based connector reconciliation.
    private static IReadOnlyList<VisualDiagnostic>? ReconcileConsumedDiagnostics(
        IReadOnlyList<VisualDiagnostic>? diagnostics, HashSet<string> consumedIds, int removedUnresolvedEdges)
    {
        if (diagnostics is null) return null;
        var retained = new List<VisualDiagnostic>();
        foreach (var diagnostic in diagnostics)
        {
            if (diagnostic.SourceObjectId is { } id && consumedIds.Contains(id) &&
                diagnostic.Code is "VisualConnectorUnresolved" or "VisualNodeLabelMissing" or "VisualEdgeDirectionUnknown")
            {
                if (diagnostic.Code == "VisualConnectorUnresolved") removedUnresolvedEdges--;
                continue;
            }
            retained.Add(diagnostic);
        }
        for (var i = retained.Count - 1; i >= 0 && removedUnresolvedEdges > 0; i--)
            if (retained[i].Code == "VisualConnectorUnresolved" && retained[i].SourceObjectId is null)
            { retained.RemoveAt(i); removedUnresolvedEdges--; }
        return retained;
    }

    /// <summary>P-Overlay: removes a schedule-arrow/bar/marker/line shape already folded into a
    /// table's own cells (see PdfTableOverlayDetector.Detect / ReadableMarkdownSerializer's
    /// format-neutral ApplyTableOverlays) from the readable visual projection, the same way
    /// <see cref="RemoveConsumedTableVisuals"/> removes a table's own ruling-line paths. The
    /// caller retains the original graph for evidence/accounting. <paramref name="overlayShapeIds"/>
    /// is whatever PdfTableOverlayDetector used as an overlay's own ShapeId -- a promoted
    /// VisualNode's or VisualEdge's own id, or (for a shape that never resolved into either, e.g.
    /// a filled bar with no matching label) a raw VisualPath id directly.</summary>
    public static VisualGraph RemoveConsumedOverlayVisuals(VisualGraph graph, IReadOnlyList<string> overlayShapeIds)
    {
        ArgumentNullException.ThrowIfNull(graph); ArgumentNullException.ThrowIfNull(overlayShapeIds);
        if (overlayShapeIds.Count == 0) return graph;
        var overlaySet = overlayShapeIds.ToHashSet(StringComparer.Ordinal);
        var nodesToRemove = (graph.Nodes ?? []).Where(node => overlaySet.Contains(node.Id)).ToArray();
        var edgesToRemove = (graph.Edges ?? []).Where(edge => overlaySet.Contains(edge.Id)).ToArray();
        var nodeIds = nodesToRemove.Select(node => node.Id).ToHashSet(StringComparer.Ordinal);
        var edgeIds = edgesToRemove.Select(edge => edge.Id).ToHashSet(StringComparer.Ordinal);

        // P1 fix: derive the raw path ids to re-disposition from the source-item LEDGER itself
        // (mirroring RemoveConsumedTableVisuals), never from a Geometry/Points reverse lookup. That
        // reverse lookup's `TryAdd` kept only the FIRST raw path for a given Geometry/Points, so a
        // rectangle drawn twice at identical coordinates (e.g. `re f` immediately followed by
        // `re S` -- a common "filled + outlined" bar) could point the ledger update at the wrong
        // path id, leaving the *actual* backing path's ledger entry still claiming
        // ProjectedNodeId/ProjectedEdgeId of a node/edge that this method just removed --
        // VisualSourceItemReferenceInvalid, IsConsistent=false, PdfExtractionException.
        var source = graph.SourceItems ?? [];
        var consumedPathIds = source.Where(item => overlaySet.Contains(item.Id) ||
                (item.ProjectedNodeId is not null && overlaySet.Contains(item.ProjectedNodeId)) ||
                (item.ProjectedEdgeId is not null && overlaySet.Contains(item.ProjectedEdgeId)))
            .Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        // An overlay ShapeId naming an EDGE that never resolved into two nodes (the common case for
        // a schedule "today line") never earns a ProjectedEdge ledger entry at all -- BuildSourceItems
        // sets that disposition only once BOTH endpoints resolve. Its raw path is ledgered as
        // VisualFallback instead (referenced only by FallbackPathId), invisible to the ledger scan
        // above via overlaySet membership. Recover it by matching the edge's own Path *reference*
        // to its backing VisualPath's Points -- reference equality, never value-equal Geometry, so
        // this can never reintroduce the P1 bug (two distinct VisualPath objects, even with
        // identical coordinates, never share one Points list instance).
        var pathsByPoints = new Dictionary<IReadOnlyList<VisualPathPoint>, VisualPath>(ReferenceEqualityComparer.Instance);
        foreach (var path in graph.Paths ?? [])
            if (path.Points is { } points) pathsByPoints.TryAdd(points, path);
        foreach (var edge in edgesToRemove)
            if (edge.Path is { } points && pathsByPoints.TryGetValue(points, out var path)) consumedPathIds.Add(path.Id);
        // An arrow overlay's small triangular arrowhead (e.g. the filled marker at the bottom of a
        // schedule "today line") is a separate VisualPath from its shaft, matched to it only by
        // proximity (see PdfTableInference.FindArrowShaftMatches / PdfTableOverlayDetector.Detect,
        // which folds it into the shaft's own arrow classification rather than treating it as an
        // independent overlay). It must disappear from the readable graph together with its shaft,
        // or it survives as its own orphaned "vector fallback" entry.
        foreach (var match in PdfTableInference.FindArrowShaftMatches(graph.Paths ?? []))
            if (consumedPathIds.Contains(match.ShaftPathId)) consumedPathIds.Add(match.MarkerPathId);

        var nodes = (graph.Nodes ?? []).Where(node => !nodeIds.Contains(node.Id)).ToArray();
        var edges = (graph.Edges ?? []).Where(edge => !edgeIds.Contains(edge.Id) &&
            !nodeIds.Contains(edge.SourceId ?? string.Empty) && !nodeIds.Contains(edge.TargetId ?? string.Empty)).ToArray();
        var paths = (graph.Paths ?? []).Where(path => !consumedPathIds.Contains(path.Id)).ToArray();
        var items = source.Select(item => consumedPathIds.Contains(item.Id)
            ? item with { Disposition = VisualDisposition.IgnoredDecorative, ProjectedNodeId = null, ProjectedEdgeId = null,
                FallbackPathId = null, Reason = "table overlay consumed by table projection" }
            : item).ToArray();
        // P2 fix: count unresolved-edge removals from the actual, final removed-edge set (every
        // graph.Edges member not present in the surviving `edges` array above), not merely
        // `edgesToRemove` (edges whose own Id was named directly by overlayShapeIds). An edge whose
        // SourceId is null but whose TargetId names a REMOVED NODE also disappears above (via the
        // `!nodeIds.Contains(edge.TargetId...)` filter) without ever being a member of
        // `edgesToRemove`, so counting only `edgesToRemove` under-counted -- leaving one stale
        // "VisualConnectorUnresolved" VisualDiagnostic behind whenever that happened, desynchronized
        // from the graph's own (now smaller) live unresolved-edge count: INV-03, IsConsistent=false.
        var survivingEdgeIds = edges.Select(edge => edge.Id).ToHashSet(StringComparer.Ordinal);
        var removedUnresolvedEdgeCount = (graph.Edges ?? [])
            .Count(edge => !survivingEdgeIds.Contains(edge.Id) && (edge.SourceId is null || edge.TargetId is null));
        var removedIds = consumedPathIds.Concat(nodeIds).Concat((graph.Edges ?? [])
            .Where(edge => !survivingEdgeIds.Contains(edge.Id)).Select(edge => edge.Id)).ToHashSet(StringComparer.Ordinal);
        var diagnostics = ReconcileConsumedDiagnostics(graph.Diagnostics, removedIds, removedUnresolvedEdgeCount);
        var projection = new VisualGraph(graph.Id, nodes, edges, diagnostics, graph.Direction, graph.Groups, paths, items);
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
        var fallbackIds = graph.SourceItems is { Count: > 0 } items
            ? items.Where(item => item?.Disposition == VisualDisposition.VisualFallback)
                .Select(item => item.FallbackPathId).ToHashSet(StringComparer.Ordinal)
            : null;
        var fallback = (graph.Paths ?? []).Where(path => path is not null &&
            (fallbackIds is null ? path.IsFallback : fallbackIds.Contains(path.Id))).ToArray();
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
