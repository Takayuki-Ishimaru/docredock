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

        foreach (var node in nodes)
        {
            if (node is null) { Error("VisualNodeInvalid", "A visual graph contains a null node."); continue; }
            if (string.IsNullOrWhiteSpace(node.Id)) Error("VisualNodeIdMissing", "A promoted visual node has no ID.", node.SourceNodeId);
            else if (!nodeIds.Add(node.Id)) Error("VisualNodeIdDuplicate", $"Visual node ID '{node.Id}' is duplicated.", node.SourceNodeId);
            if (string.IsNullOrWhiteSpace(node.Label)) Error("VisualNodeLabelMissing", $"Visual node '{node.Id}' has no label.", node.SourceNodeId);
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
            if (edge.Resolution is VisualEdgeResolution.GeometryInferred or VisualEdgeResolution.LayoutInferred &&
                edge.Confidence is < MinimumInferredEdgeConfidence)
                Error("VisualEdgeConfidenceTooLow", $"Inferred visual edge '{edge.Id}' has insufficient confidence.", edge.SourceNodeId);
        }

        foreach (var path in paths)
            if (path is null || string.IsNullOrWhiteSpace(path.Id) || !pathIds.Add(path.Id))
                Error("VisualPathInvalid", "A visual path has a missing or duplicate ID.", path?.SourceNodeId);

        var sourceAccounting = ValidateSourceItems(graph.SourceItems, nodeIds, edgeIds, pathIds,
            diagnostics.Where(item => item is not null).Select(item => item.Code).ToHashSet(StringComparer.Ordinal), errors);
        return new VisualGraphValidationResult(errors.Count == 0, errors, warnings, sourceAccounting);

        void Error(string code, string message, string? sourceNodeId = null) =>
            errors.Add(new VisualGraphValidationIssue(code, message, sourceNodeId));
    }

    private static VisualSourceAccounting ValidateSourceItems(IReadOnlyList<VisualSourceItem>? items,
        IReadOnlySet<string> nodeIds, IReadOnlySet<string> edgeIds, IReadOnlySet<string> pathIds,
        IReadOnlySet<string> diagnosticCodes, List<VisualGraphValidationIssue> errors)
    {
        if (items is null) return VisualSourceAccounting.Legacy;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var projectedNodeSources = new HashSet<string>(StringComparer.Ordinal);
        var projectedEdgeSources = new HashSet<string>(StringComparer.Ordinal);
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
                    projectedNodeSources.Add(item.Id),
                VisualDisposition.ProjectedEdge =>
                    !string.IsNullOrWhiteSpace(item.ProjectedEdgeId) &&
                    edgeIds.Contains(item.ProjectedEdgeId) &&
                    projectedEdgeSources.Add(item.Id),
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

        return new VisualSourceAccounting(items.Count, projectedNodes, projectedEdges, fallbacks, diagnosticOnly, suppressed, ignored,
            Unaccounted: 0, InvalidReferences: invalid);
    }
}

public sealed record VisualGraphValidationIssue(string Code, string Message, string? SourceNodeId = null);

public sealed record VisualGraphValidationResult(
    bool IsValidForSemanticProjection,
    IReadOnlyList<VisualGraphValidationIssue> Errors,
    IReadOnlyList<VisualGraphValidationIssue> Warnings,
    VisualSourceAccounting Accounting);
