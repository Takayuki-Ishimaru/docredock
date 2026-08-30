using System.Text.Json.Serialization;

namespace DocRedock.Core.Documents;

/// <summary>Portable semantic description of a visual relationship, independent of an Office drawing format.</summary>
public enum VisualNodeKind { Process, Decision, Terminator, Data, Generic }
public enum VisualEdgeResolution { NativeConnection, GeometryInferred, LayoutInferred, Unresolved }
public enum VisualEdgeDirection { Directed, Undirected }

public sealed record VisualNode(string Id, string Label, VisualNodeKind Kind = VisualNodeKind.Generic, string? SourceNodeId = null,
    Geometry? Geometry = null, SourceAnchor? SourceAnchor = null, string? Group = null, string? Lane = null);
public sealed record VisualEdge(string Id, string? SourceId, string? TargetId, string? Label = null,
    VisualEdgeResolution Resolution = VisualEdgeResolution.NativeConnection, string? SourceNodeId = null,
    string? Direction = null, Geometry? Geometry = null, double? Confidence = null,
    IReadOnlyList<VisualPathPoint>? Path = null, SourceAnchor? SourceAnchor = null,
    VisualEdgeDirection? EdgeDirection = null, VisualConnectionEvidence? Evidence = null)
{
    [JsonIgnore]
    public bool IsUndirected => EdgeDirection == VisualEdgeDirection.Undirected ||
        string.Equals(Direction, "undirected", StringComparison.OrdinalIgnoreCase);
}
/// <summary>A point in an adapter-native visual path. <see cref="Geometry.CoordinateSpace"/> describes its units.</summary>
public sealed record VisualPathPoint(double X, double Y);
public sealed record VisualConnectionEvidence(string Method, string ConfidenceBand, double Score,
    double? SecondBestScore = null, double? CandidateMargin = null, double? BoundaryDistanceNormalized = null,
    bool? RayIntersects = null, bool? RayFirstHit = null, double? AngularDeviationDegrees = null,
    double? PerpendicularOffsetNormalized = null, int IntermediateNodeCount = 0, string? ArrowheadEvidence = null,
    string? ClusterId = null, IReadOnlyList<string>? EvidenceCodes = null, IReadOnlyList<string>? RejectedCandidateIds = null);
public enum VisualGraphQuality { ExactNative, HighConfidenceInferred, Partial, FallbackOnly, Invalid }
/// <summary>A recognized vector/path which could not necessarily be promoted to a semantic edge.</summary>
public sealed record VisualPath(string Id, IReadOnlyList<VisualPathPoint>? Points = null, Geometry? Geometry = null,
    SourceAnchor? SourceAnchor = null, double? Confidence = null, bool IsFallback = true, string? SourceNodeId = null);
public sealed record VisualDiagnostic(string Code, string Message, string? SourceNodeId = null, int Count = 1,
    string? Fallback = null, string? Remedy = null, string? Format = null, string? PartUri = null,
    string? PartitionId = null, string? SourceObjectId = null, string? SourceObjectType = null, double? Confidence = null)
{
    /// <summary>Recognizes stable adapter warnings formatted as <c>VisualCode: message</c>.</summary>
    public static bool TryParseWarning(string warning, out string code, out string message)
    {
        code = string.Empty; message = warning;
        var separator = warning.IndexOf(':');
        if (separator <= "Visual".Length || !warning.StartsWith("Visual", StringComparison.Ordinal)) return false;
        var candidate = warning[..separator];
        if (!candidate.All(character => char.IsLetterOrDigit(character))) return false;
        code = candidate; message = warning[(separator + 1)..].TrimStart();
        return true;
    }

    /// <summary>Formats this diagnostic in the stable adapter-warning syntax consumed by <see cref="TryParseWarning"/>.</summary>
    public string ToWarning() => Code + ": " + Message;

    /// <summary>A stable, compact source location suitable for verbose CLI and JSON reports.</summary>
    public string? LocationSummary
    {
        get
        {
            var fields = new[]
            {
                Format is { Length: > 0 } ? "format=" + Format : null,
                PartUri is { Length: > 0 } ? "part=" + PartUri : null,
                PartitionId is { Length: > 0 } ? "partition=" + PartitionId : null,
                SourceObjectId is { Length: > 0 } ? "source_object=" + SourceObjectId : null,
                SourceObjectType is { Length: > 0 } ? "source_type=" + SourceObjectType : null,
                Confidence is { } confidence ? "confidence=" + confidence.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) : null,
                Fallback is { Length: > 0 } ? "fallback=" + Fallback : null,
                Remedy is { Length: > 0 } ? "remedy=" + Remedy : null
            };
            var text = string.Join("; ", fields.Where(value => value is not null));
            return text.Length == 0 ? null : text;
        }
    }
}
public enum VisualSourceItemKind { Shape, Connector, DirectionalShape, VectorPath, TextLabel, ImageOnlyPage, Diagram }
public enum VisualDisposition { ProjectedNode, ProjectedEdge, VisualFallback, DiagnosticOnly, SuppressedDuplicate, IgnoredDecorative }
public sealed record VisualSourceItem(
    string Id,
    VisualSourceItemKind Kind,
    VisualDisposition Disposition,
    string? ProjectedNodeId = null,
    string? ProjectedEdgeId = null,
    string? FallbackPathId = null,
    string? DiagnosticCode = null,
    string? DuplicateOfSourceItemId = null,
    string? Reason = null,
    SourceAnchor? SourceAnchor = null);
public sealed record VisualGroup(string Id, string? Label = null, IReadOnlyList<string>? NodeIds = null, string? Lane = null);
public sealed record VisualGraphAccounting(int RecognizedNodes, int RecognizedEdges, int ResolvedEdges, int UnresolvedEdges, int Diagnostics,
    int RecognizedPaths = 0, int ProjectedPaths = 0, int FallbackPaths = 0)
{
    public bool IsConsistent => RecognizedEdges == ResolvedEdges + UnresolvedEdges &&
        RecognizedPaths == ProjectedPaths + FallbackPaths;
}
public sealed record VisualSourceAccounting(int RecognizedSourceItems, int ProjectedNodes, int ProjectedEdges,
    int VisualFallbacks, int DiagnosticOnly, int SuppressedDuplicates, int IgnoredDecorative,
    int Unaccounted, int InvalidReferences)
{
    public static VisualSourceAccounting Legacy { get; } = new(0, 0, 0, 0, 0, 0, 0, 0, 0);
    public bool IsConsistent => Unaccounted == 0 && InvalidReferences == 0 &&
        RecognizedSourceItems == ProjectedNodes + ProjectedEdges + VisualFallbacks + DiagnosticOnly + SuppressedDuplicates + IgnoredDecorative;
}

/// <summary>
/// A format-neutral graph for diagrams. IDs are required to be stable within the graph; unresolved
/// edges remain in the model so an adapter cannot silently discard a recognized visual relation.
/// </summary>
public sealed record VisualGraph(
    string Id,
    IReadOnlyList<VisualNode> Nodes,
    IReadOnlyList<VisualEdge> Edges,
    IReadOnlyList<VisualDiagnostic>? Diagnostics = null,
    string Direction = "LR",
    IReadOnlyList<VisualGroup>? Groups = null,
    IReadOnlyList<VisualPath>? Paths = null,
    IReadOnlyList<VisualSourceItem>? SourceItems = null,
    VisualGraphQuality? Quality = null)
{
    [JsonIgnore]
    public bool HasTopology
    {
        get
        {
            if (SourceItems is not null)
                return VisualGraphValidator.Validate(this).IsValidForSemanticProjection &&
                    (Edges ?? []).Any(edge => edge is not null && edge.SourceId is not null && edge.TargetId is not null);
            var nodes = Nodes ?? [];
            var edges = Edges ?? [];
            if (nodes.Any(node => node is null) || edges.Any(edge => edge is null)) return false;
            var nodeIds = nodes.Select(node => node.Id).ToArray();
            if (nodeIds.Length == 0 || nodeIds.Any(string.IsNullOrWhiteSpace) || nodeIds.Distinct(StringComparer.Ordinal).Count() != nodeIds.Length) return false;
            var knownNodes = nodeIds.ToHashSet(StringComparer.Ordinal);
            return Accounting.IsConsistent && edges.Any(edge => edge.SourceId is not null && edge.TargetId is not null &&
                knownNodes.Contains(edge.SourceId) && knownNodes.Contains(edge.TargetId));
        }
    }

    [JsonIgnore]
    public VisualSourceAccounting SourceAccounting => VisualGraphValidator.Validate(this).Accounting;
    [JsonIgnore]
    public VisualGraphAccounting Accounting
    {
        get
        {
            var paths = Paths ?? [];
            var nodes = Nodes ?? [];
            var edges = Edges ?? [];
            var resolvedEdges = edges.Count(edge => edge is not null && edge.SourceId is not null && edge.TargetId is not null);
            var diagnostics = Diagnostics ?? [];
            return new(nodes.Count, edges.Count,
                resolvedEdges, edges.Count - resolvedEdges, diagnostics.Sum(diagnostic => diagnostic is null ? 1 : Math.Max(1, diagnostic.Count)),
                paths.Count, paths.Count(path => path is not null && !path.IsFallback), paths.Count(path => path is null || path.IsFallback));
        }
    }
}
