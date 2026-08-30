using System.Collections.ObjectModel;

namespace DocRedock.Core.Documents;

/// <summary>Validates whether a format-neutral visual graph is safe to promote to semantic Markdown.</summary>
public static class VisualGraphValidator
{
    public const double MinimumInferredEdgeConfidence = 0.75;

    public static VisualGraphValidationResult Validate(VisualGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var errors = new List<VisualGraphValidationIssue>();
        var warnings = new List<VisualGraphValidationIssue>();
        var nodes = graph.Nodes ?? [];
        var edges = graph.Edges ?? [];
        var paths = graph.Paths ?? [];
        var diagnostics = graph.Diagnostics ?? [];
        var nodeIds = new HashSet<string>(StringComparer.Ordinal);
        var edgeIds = new HashSet<string>(StringComparer.Ordinal);
        var pathIds = new HashSet<string>(StringComparer.Ordinal);
        var semanticEdges = new HashSet<string>(StringComparer.Ordinal);
        var nodeLabels = new HashSet<string>(StringComparer.Ordinal);
        var nodeGeometryLabels = new HashSet<(string Label, Geometry Geometry)>();

        foreach (var node in nodes)
        {
            if (node is null) { Error("VisualNodeInvalid", "A visual graph contains a null node."); continue; }
            if (string.IsNullOrWhiteSpace(node.Id)) Error("VisualNodeIdMissing", "A promoted visual node has no ID.", node.SourceNodeId);
            else if (!nodeIds.Add(node.Id)) Error("VisualNodeIdDuplicate", $"Visual node ID '{node.Id}' is duplicated.", node.SourceNodeId);
            if (string.IsNullOrWhiteSpace(node.Label)) Error("VisualNodeLabelMissing", $"Visual node '{node.Id}' has no label.", node.SourceNodeId);
            else
            {
                var label = node.Label.Trim();
                nodeLabels.Add(label);
                if (node.Geometry is not null && !nodeGeometryLabels.Add((label, node.Geometry)))
                    Error("VisualNodeDuplicate", $"Visual node '{node.Id}' duplicates a node with the same label and geometry.", node.SourceNodeId);
            }
        }

        foreach (var edge in edges)
        {
            if (edge is null) { Error("VisualEdgeInvalid", "A visual graph contains a null edge."); continue; }
            if (!string.IsNullOrWhiteSpace(edge.Id) && !edgeIds.Add(edge.Id)) Error("VisualEdgeIdDuplicate", $"Visual edge ID '{edge.Id}' is duplicated.", edge.SourceNodeId);
            var resolved = edge.SourceId is not null || edge.TargetId is not null;
            if (!resolved) continue;
            if (string.IsNullOrWhiteSpace(edge.SourceId) || string.IsNullOrWhiteSpace(edge.TargetId) ||
                !nodeIds.Contains(edge.SourceId) || !nodeIds.Contains(edge.TargetId))
                Error("VisualEdgeReferenceInvalid", $"Visual edge '{edge.Id}' references a missing endpoint.", edge.SourceNodeId);
            else if (StringComparer.Ordinal.Equals(edge.SourceId, edge.TargetId))
                Error("VisualSelfEdge", $"Visual edge '{edge.Id}' is a self edge.", edge.SourceNodeId);
            else
            {
                var sourceId = edge.SourceId;
                var targetId = edge.TargetId;
                if (edge.IsUndirected && StringComparer.Ordinal.Compare(sourceId, targetId) > 0)
                    (sourceId, targetId) = (targetId, sourceId);
                var semanticKey = string.Join('\u001f', sourceId, targetId, edge.Label?.Trim() ?? string.Empty,
                    edge.IsUndirected ? "undirected" : "directed");
                if (!semanticEdges.Add(semanticKey))
                    Error("VisualEdgeDuplicate", $"Visual edge '{edge.Id}' duplicates an existing semantic relation.", edge.SourceNodeId);
            }
            if (!string.IsNullOrWhiteSpace(edge.Label) && nodeLabels.Contains(edge.Label.Trim()))
                Error("VisualEdgeLabelReusedAsNode", $"Visual edge '{edge.Id}' reuses a promoted node label as its edge label.", edge.SourceNodeId);
            if (edge.Resolution is VisualEdgeResolution.GeometryInferred or VisualEdgeResolution.LayoutInferred &&
                edge.Confidence is < MinimumInferredEdgeConfidence)
                Error("VisualEdgeConfidenceTooLow", $"Inferred visual edge '{edge.Id}' has insufficient confidence.", edge.SourceNodeId);
            ValidateEvidence(edge, Error);
        }

        var connectedNodeIds = edges.Where(edge => edge is not null && edge.SourceId is not null && edge.TargetId is not null)
            .SelectMany(edge => new[] { edge.SourceId!, edge.TargetId! }).ToHashSet(StringComparer.Ordinal);
        foreach (var node in nodes.Where(node => node is not null && IsSyntheticPlaceholder(node.Label)))
        {
            if (connectedNodeIds.Contains(node.Id))
                Warning("VisualSyntheticNodeConnected", $"Connected synthetic visual node '{node.Id}' was retained with a warning.", node.SourceNodeId);
            else
            {
                Error("VisualSyntheticNodePlaceholder", $"Isolated synthetic visual node '{node.Id}' cannot be promoted to semantic Markdown.", node.SourceNodeId);
                Error("VisualSyntheticNodeIsolated", $"Synthetic visual node '{node.Id}' is isolated and cannot be promoted to semantic Markdown.", node.SourceNodeId);
            }
        }

        foreach (var path in paths)
            if (path is null || string.IsNullOrWhiteSpace(path.Id) || !pathIds.Add(path.Id))
                Error("VisualPathInvalid", "A visual path has a missing or duplicate ID.", path?.SourceNodeId);

        var resolvedEdgeIds = edges.Where(edge => edge is not null && edge.SourceId is not null && edge.TargetId is not null)
            .Select(edge => edge.Id).Where(id => !string.IsNullOrWhiteSpace(id)).ToHashSet(StringComparer.Ordinal);
        var sourceAccounting = ValidateSourceItems(graph.SourceItems, nodeIds, edgeIds, resolvedEdgeIds, pathIds,
            diagnostics.Where(item => item is not null).Select(item => item.Code).ToHashSet(StringComparer.Ordinal), errors);
        if (graph.SourceItems is not null && edges.Any(edge => edge is not null && edge.SourceId is null && edge.TargetId is null) && !graph.SourceItems.Any(item => item is not null && item.Disposition is VisualDisposition.VisualFallback or VisualDisposition.DiagnosticOnly)) Error("VisualUnresolvedRelationUnaccounted", "Unresolved visual relations require a visible fallback or diagnostic.");
        if (edges.Any(edge => edge is not null && edge.SourceId is null || edge is not null && edge.TargetId is null))
            Warning("VisualConnectorUnresolved", "One or more visual connections remain unresolved and were retained as fallback.");
        if (edges.Any(edge => edge?.Evidence?.ConfidenceBand.Equals("Medium", StringComparison.OrdinalIgnoreCase) == true))
            Warning("VisualInferenceMediumConfidence", "One or more visual connections were inferred with medium confidence.");
        if (paths.Any(path => path?.IsFallback == true) || graph.SourceItems?.Any(item => item?.Disposition == VisualDisposition.VisualFallback) == true)
            Warning("VisualFallbackUsed", "One or more visual elements were retained as fallback instead of semantic topology.");
        var inferredQuality = InferQuality(edges);
        if (graph.Quality is { } declaredQuality && declaredQuality != inferredQuality)
            Error("VisualGraphQualityMismatch", $"Declared visual graph quality '{declaredQuality}' does not match computed quality '{inferredQuality}'.");
        var quality = errors.Count > 0 ? VisualGraphQuality.Invalid : graph.Quality ?? inferredQuality;
        return new VisualGraphValidationResult(errors.Count == 0, errors, warnings, sourceAccounting, quality);

        void Error(string code, string message, string? sourceNodeId = null) => errors.Add(new VisualGraphValidationIssue(code, message, sourceNodeId));
        void Warning(string code, string message, string? sourceNodeId = null) => warnings.Add(new VisualGraphValidationIssue(code, message, sourceNodeId));
    }

    /// <summary>Computes the topology quality independently of optional serialized metadata.</summary>
    public static VisualGraphQuality ComputeQuality(VisualGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        return InferQuality(graph.Edges ?? []);
    }

    private static void ValidateEvidence(VisualEdge edge, Action<string, string, string?> error)
    {
        if (edge.Evidence is not { } evidence) return;
        if (!double.IsFinite(evidence.Score) || evidence.Score is < 0 or > 1)
            error("VisualEdgeEvidenceInvalid", $"Visual edge '{edge.Id}' has an invalid evidence score.", edge.SourceNodeId);
        if (evidence.SecondBestScore is { } second && (!double.IsFinite(second) || second is < 0 or > 1) ||
            evidence.CandidateMargin is { } margin && (!double.IsFinite(margin) || margin is < 0 or > 1) ||
            evidence.BoundaryDistanceNormalized is { } distance && (!double.IsFinite(distance) || distance < 0) ||
            evidence.AngularDeviationDegrees is { } angle && (!double.IsFinite(angle) || angle is < 0 or > 180) ||
            evidence.PerpendicularOffsetNormalized is { } offset && (!double.IsFinite(offset) || offset < 0) ||
            evidence.IntermediateNodeCount < 0)
            error("VisualEdgeEvidenceInvalid", $"Visual edge '{edge.Id}' has malformed connection evidence.", edge.SourceNodeId);

        var inferred = edge.Resolution is VisualEdgeResolution.GeometryInferred or VisualEdgeResolution.LayoutInferred;
        if (inferred && (evidence.ConfidenceBand.Equals("Low", StringComparison.OrdinalIgnoreCase) ||
                         evidence.ConfidenceBand.Equals("Unresolved", StringComparison.OrdinalIgnoreCase)))
            error("VisualEdgeEvidenceTooWeak", $"Inferred visual edge '{edge.Id}' is backed only by weak evidence.", edge.SourceNodeId);
        if (inferred && evidence.IntermediateNodeCount > 0)
            error("VisualEdgeSkipsIntermediateNode", $"Inferred visual edge '{edge.Id}' crosses an intermediate node.", edge.SourceNodeId);
        if (edge.IsUndirected && evidence.ArrowheadEvidence is { Length: > 0 } arrowheads &&
            !arrowheads.Equals("none", StringComparison.OrdinalIgnoreCase))
            error("VisualEdgeDirectionContradiction", $"Undirected visual edge '{edge.Id}' contradicts its arrowhead evidence.", edge.SourceNodeId);
    }

    private static bool IsSyntheticPlaceholder(string? label)
    {
        var value = label?.Trim();
        return value is not null && (IsNumberedPlaceholder(value, "Vector node") || IsNumberedPlaceholder(value, "Shape"));
    }

    private static bool IsNumberedPlaceholder(string value, string prefix) =>
        value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
        int.TryParse(value.AsSpan(prefix.Length).Trim(), out _);

    private static VisualGraphQuality InferQuality(IReadOnlyList<VisualEdge> edges)
    {
        var resolved = edges.Where(edge => edge is not null && edge.SourceId is not null && edge.TargetId is not null).ToArray();
        if (resolved.Length == 0) return VisualGraphQuality.FallbackOnly;
        if (edges.Any(edge => edge is not null && (edge.SourceId is null || edge.TargetId is null))) return VisualGraphQuality.Partial;
        return resolved.All(edge => edge.Resolution == VisualEdgeResolution.NativeConnection) ? VisualGraphQuality.ExactNative : VisualGraphQuality.HighConfidenceInferred;
    }

    private static VisualSourceAccounting ValidateSourceItems(IReadOnlyList<VisualSourceItem>? items,
        IReadOnlySet<string> nodeIds, IReadOnlySet<string> edgeIds, IReadOnlySet<string> resolvedEdgeIds, IReadOnlySet<string> pathIds,
        IReadOnlySet<string> diagnosticCodes, List<VisualGraphValidationIssue> errors)
    {
        if (items is null) return VisualSourceAccounting.Legacy;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var projectedNodeTargets = new HashSet<string>(StringComparer.Ordinal);
        var projectedEdgeTargets = new HashSet<string>(StringComparer.Ordinal);
        var projectedNodes = 0;
        var projectedEdges = 0;
        var fallbacks = 0;
        var diagnosticOnly = 0;
        var suppressed = 0;
        var ignored = 0;
        var invalid = 0;

        foreach (var item in items)
        {
            if (item is null || string.IsNullOrWhiteSpace(item.Id) || !ids.Add(item.Id))
            {
                invalid++;
                errors.Add(new("VisualSourceItemInvalid", "A source visual item has a missing or duplicate ID.", item?.Id));
                continue;
            }

            bool valid = item.Disposition switch
            {
                VisualDisposition.ProjectedNode =>
                    item.Kind != VisualSourceItemKind.Connector &&
                    !string.IsNullOrWhiteSpace(item.ProjectedNodeId) &&
                    nodeIds.Contains(item.ProjectedNodeId) &&
                    projectedNodeTargets.Add(item.ProjectedNodeId),
                VisualDisposition.ProjectedEdge =>
                    !string.IsNullOrWhiteSpace(item.ProjectedEdgeId) &&
                    edgeIds.Contains(item.ProjectedEdgeId) &&
                    projectedEdgeTargets.Add(item.ProjectedEdgeId),
                VisualDisposition.VisualFallback =>
                    !string.IsNullOrWhiteSpace(item.FallbackPathId) &&
                    pathIds.Contains(item.FallbackPathId),
                VisualDisposition.DiagnosticOnly =>
                    !string.IsNullOrWhiteSpace(item.DiagnosticCode) &&
                    diagnosticCodes.Contains(item.DiagnosticCode),
                VisualDisposition.SuppressedDuplicate =>
                    !string.IsNullOrWhiteSpace(item.DuplicateOfSourceItemId) &&
                    !StringComparer.Ordinal.Equals(item.DuplicateOfSourceItemId, item.Id),
                VisualDisposition.IgnoredDecorative => !string.IsNullOrWhiteSpace(item.Reason),
                _ => false
            };

            switch (item.Disposition)
            {
                case VisualDisposition.ProjectedNode: projectedNodes++; break;
                case VisualDisposition.ProjectedEdge: projectedEdges++; break;
                case VisualDisposition.VisualFallback: fallbacks++; break;
                case VisualDisposition.DiagnosticOnly: diagnosticOnly++; break;
                case VisualDisposition.SuppressedDuplicate: suppressed++; break;
                case VisualDisposition.IgnoredDecorative: ignored++; break;
            }

            if (!valid)
            {
                invalid++;
                errors.Add(new("VisualSourceItemReferenceInvalid", $"Source visual item '{item.Id}' has an invalid {item.Disposition} reference.", item.Id));
            }
        }

        foreach (var item in items.Where(item => item is not null && item.Disposition == VisualDisposition.SuppressedDuplicate))
            if (!ids.Contains(item.DuplicateOfSourceItemId!))
            {
                invalid++;
                errors.Add(new("VisualSourceDuplicateReferenceInvalid", $"Duplicate source item '{item.Id}' references an unknown source item.", item.Id));
            }

        var unaccounted = nodeIds.Count(id => !projectedNodeTargets.Contains(id)) +
            resolvedEdgeIds.Count(id => !projectedEdgeTargets.Contains(id));
        if (unaccounted > 0)
            errors.Add(new("VisualSourceItemMissing", $"{unaccounted} promoted visual node or edge has no source-ledger entry."));

        return new VisualSourceAccounting(items.Count, projectedNodes, projectedEdges, fallbacks, diagnosticOnly, suppressed, ignored,
            Unaccounted: unaccounted, InvalidReferences: invalid);
    }
}

public sealed record VisualGraphValidationIssue(string Code, string Message, string? SourceNodeId = null);

public sealed record VisualGraphValidationResult(
    bool IsValidForSemanticProjection,
    IReadOnlyList<VisualGraphValidationIssue> Errors,
    IReadOnlyList<VisualGraphValidationIssue> Warnings,
    VisualSourceAccounting Accounting,
    VisualGraphQuality Quality = VisualGraphQuality.Invalid);
