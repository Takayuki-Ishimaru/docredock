namespace DocRedock.VisualInference;

public enum VisualInferenceMode { NativeOnly, Safe, Balanced }
public enum ConnectionConfidence { Unresolved, Low, Medium, High, Native }
public enum ConnectionDirection { Unknown, Forward, Reverse, Bidirectional }
public sealed record DiagramCluster(string Id, IReadOnlyList<string> PrimitiveIds);
public sealed record DiagramClusterOptions(double ProximityMultiplier = 2.5, int MaxPairChecks = 250000);
public sealed class DiagramClusterer
{
    public IReadOnlyList<VisualExtractionDiagnostic> Diagnostics { get; private set; } = [];
    public IReadOnlyList<DiagramCluster> Cluster(VisualPrimitiveDocument document, DiagramClusterOptions? options = null)
    {
        options ??= new(); var result = new List<DiagramCluster>();
        Diagnostics = [];
        var pairChecks = 0;
        foreach (var canvas in document.Canvases.OrderBy(c => c.Id, StringComparer.Ordinal))
        {
            var items = document.Primitives.Where(p => !p.IsHidden && p.CanvasId == canvas.Id).OrderBy(p => p.Id, StringComparer.Ordinal).ToArray();
            var parent = Enumerable.Range(0, items.Length).ToArray();
            int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
            void Union(int a, int b) { a = Find(a); b = Find(b); if (a != b) parent[Math.Min(a,b)] = Math.Max(a,b); }
            var cellSize = Math.Max(1, items.Where(item => item.Bounds is not null)
                .Select(item => Math.Min(item.Bounds!.Width, item.Bounds.Height))
                .DefaultIfEmpty(1).Max() * options.ProximityMultiplier);
            var index = new Dictionary<(int X, int Y), List<int>>();
            for (var itemIndex = 0; itemIndex < items.Length; itemIndex++)
            {
                if (items[itemIndex].Bounds is not { } bounds) continue;
                var cells = items[itemIndex] is VisualConnectorPrimitive connector
                    ? connector.Path.Points.Select(point => ((int)Math.Floor(point.X / cellSize), (int)Math.Floor(point.Y / cellSize)))
                    : [((int)Math.Floor(bounds.Center.X / cellSize), (int)Math.Floor(bounds.Center.Y / cellSize))];
                foreach (var cell in cells.Distinct())
                {
                    if (!index.TryGetValue(cell, out var bucket)) index[cell] = bucket = [];
                    bucket.Add(itemIndex);
                }
            }
            var pairs = new SortedSet<(int A, int B)>();
            var nativePairs = new HashSet<(int A, int B)>();
            for (var i = 0; i < items.Length; i++)
            {
                if (items[i].GroupId is not null)
                    for (var j = i + 1; j < items.Length; j++)
                        if (items[i].GroupId == items[j].GroupId) pairs.Add((i, j));
                if (items[i].Bounds is not { } bounds) continue;
                var cell = ((int)Math.Floor(bounds.Center.X / cellSize), (int)Math.Floor(bounds.Center.Y / cellSize));
                for (var dx = -1; dx <= 1; dx++) for (var dy = -1; dy <= 1; dy++)
                    if (index.TryGetValue((cell.Item1 + dx, cell.Item2 + dy), out var bucket))
                        foreach (var j in bucket.Where(j => j > i)) pairs.Add((i, j));
            }
            for (var i = 0; i < items.Length; i++)
                if (items[i] is VisualConnectorPrimitive connector)
                {
                    for (var j = 0; j < items.Length; j++)
                        if (i != j && Touches(connector, items[j])) pairs.Add((Math.Min(i, j), Math.Max(i, j)));
                    foreach (var alias in new[] { connector.NativeSourceAlias, connector.NativeTargetAlias }
                        .Where(alias => !string.IsNullOrWhiteSpace(alias)).Distinct(StringComparer.Ordinal))
                    {
                        var aliasMatches = items.Select((item, index) => (item, index))
                            .Where(candidate => candidate.item is VisualNodePrimitive node &&
                                (StringComparer.Ordinal.Equals(node.Id, alias) ||
                                 (node.Aliases ?? []).Any(item => StringComparer.Ordinal.Equals(item.Value, alias))))
                            .Take(2).ToArray();
                        // Ambiguous native aliases deliberately do not create cluster closure.
                        if (aliasMatches.Length == 1)
                        {
                            var pair = (Math.Min(i, aliasMatches[0].index), Math.Max(i, aliasMatches[0].index));
                            pairs.Add(pair);
                            nativePairs.Add(pair);
                        }
                    }
                }
            foreach (var (i, j) in pairs)
            {
                if (++pairChecks > options.MaxPairChecks)
                {
                    Diagnostics = [new VisualExtractionDiagnostic("VisualClusterResourceLimit", $"Deterministic pair-check limit {options.MaxPairChecks} exceeded; remaining comparisons were not evaluated.", canvas.Id)];
                    break;
                }
                var a = items[i]; var b = items[j];
                if (nativePairs.Contains((i, j)) ||
                    a is VisualConnectorPrimitive c && Touches(c, b) || b is VisualConnectorPrimitive d && Touches(d, a) ||
                    a.Bounds is { } ab && b.Bounds is { } bb && GeometryMath.Distance(ab.Center, bb.Center) <= options.ProximityMultiplier * Math.Max(1, Math.Min(Math.Min(ab.Width, ab.Height), Math.Min(bb.Width, bb.Height)))) Union(i, j);
            }
            foreach (var group in items.Select((p,i) => (p,i)).GroupBy(x => Find(x.i)).OrderBy(g => g.Min(x => x.p.Id), StringComparer.Ordinal)) result.Add(new($"{canvas.Id}:{result.Count:D3}", group.Select(x => x.p.Id).OrderBy(x => x, StringComparer.Ordinal).ToArray()));
        }
        return result;
    }
    private static bool Touches(VisualConnectorPrimitive connector, VisualPrimitive other)
    {
        if (other.Bounds is not { } bounds) return false;
        var threshold = Math.Max(1, Math.Min(bounds.Width, bounds.Height));
        var points = connector.Path.Points;
        if (points.Count < 2)
            return bounds.DistanceTo(connector.Path.Start) <= threshold || bounds.DistanceTo(connector.Path.End) <= threshold;
        // Check every shaft segment, not just the endpoints, so a node resting mid-shaft still merges in.
        for (var index = 1; index < points.Count; index++)
            if (GeometryMath.DistanceToSegmentRect(points[index - 1], points[index], bounds) <= threshold) return true;
        return false;
    }
}
public sealed record ConnectionFeatures(double BoundaryDistanceNormalized, bool RayIntersects, bool RayFirstHit, double AngularDeviationDegrees, double PerpendicularOffsetNormalized, double CorridorOverlapRatio, bool NativeAliasMatch, bool SameGroup, int IntermediateNodeCount, double CandidateMargin = 0);
public sealed record ConnectionCandidate(string ConnectorId, string NodeId, bool IsStart, ConnectionFeatures Features, double Score, bool IsHardRejected = false);
public sealed record ConnectionPairCandidate(string ConnectorId, string? SourceId, string? TargetId, double Score, ConnectionConfidence Confidence, string ClusterId, IReadOnlyList<string>? RejectedCandidateIds = null, bool IsNative = false, ConnectionDirection Direction = ConnectionDirection.Unknown);
public sealed record SoftConnectionOptions(
    VisualInferenceMode Mode = VisualInferenceMode.Safe,
    int CandidateLimit = 4,
    int PairCandidateLimit = 4,
    double HighThreshold = .85,
    double MediumThreshold = .70,
    double HighMargin = .15,
    double MediumMargin = .10,
    double GraphMargin = .10,
    double DistanceWeight = .45,
    double RayWeight = .20,
    double AngleWeight = .15,
    double CorridorWeight = .10,
    int MaxConnectors = 40,
    int BeamWidth = 128);
public sealed record SoftConnectionResult(IReadOnlyList<ConnectionPairCandidate> Resolved, IReadOnlyList<ConnectionPairCandidate> Unresolved, IReadOnlyList<ConnectionCandidate> Candidates, double GraphMargin = 0, IReadOnlyList<VisualExtractionDiagnostic>? Diagnostics = null);
public sealed class SoftConnectionEngine
{
    public SoftConnectionResult Infer(VisualPrimitiveDocument document, IReadOnlyList<DiagramCluster>? clusters = null, SoftConnectionOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new();
        cancellationToken.ThrowIfCancellationRequested();
        var clusterer = new DiagramClusterer();
        clusters ??= clusterer.Cluster(document);
        var diagnostics = new List<VisualExtractionDiagnostic>(clusterer.Diagnostics);
        var all = new List<ConnectionCandidate>();
        var resolved = new List<ConnectionPairCandidate>();
        var unresolved = new List<ConnectionPairCandidate>();
        var graphMargins = new List<double>();
        foreach (var cluster in clusters.OrderBy(c => c.Id, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var primitives = document.Primitives
                .Where(p => !p.IsHidden && cluster.PrimitiveIds.Contains(p.Id, StringComparer.Ordinal))
                .OrderBy(p => p.Id, StringComparer.Ordinal)
                .ToArray();
            var spaces = primitives.Select(p => document.Canvases.FirstOrDefault(c => c.Id == p.CanvasId)?.CoordinateSpace)
                .Where(space => !string.IsNullOrWhiteSpace(space)).Distinct(StringComparer.Ordinal).ToArray();
            if (spaces.Length > 1)
            {
                diagnostics.Add(new VisualExtractionDiagnostic("VisualCoordinateSpaceIncompatible",
                    "Primitives use incompatible coordinate spaces and were not compared or promoted.", cluster.Id));
                continue;
            }
            var canvas = document.Canvases.FirstOrDefault(item => primitives.Any(p => p.CanvasId == item.Id));
            var nodes = primitives.OfType<VisualNodePrimitive>().Where(n => n.Bounds is not null)
                .OrderBy(n => n.Id, StringComparer.Ordinal).ToArray();
            var connectors = primitives.OfType<VisualConnectorPrimitive>()
                .OrderBy(c => c.Id, StringComparer.Ordinal).ToArray();
            var scale = ScaleOf(canvas, nodes, connectors);
            var spatialIndex = new NodeSpatialIndex(nodes, scale);
            var rows = new List<IReadOnlyList<ConnectionPairCandidate>>();
            foreach (var connector in connectors)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var none = new ConnectionPairCandidate(connector.Id, null, null, 0,
                    ConnectionConfidence.Unresolved, cluster.Id);
                var nativeSource = ResolveAlias(connector.NativeSourceAlias, nodes);
                var nativeTarget = ResolveAlias(connector.NativeTargetAlias, nodes);
                // An explicit-but-ambiguous native alias is stronger evidence of corruption
                // than proximity is evidence of a relation. Never guess through it.
                if (connector.NativeSourceAlias is not null && nativeSource is null ||
                    connector.NativeTargetAlias is not null && nativeTarget is null)
                {
                    rows.Add([none with { RejectedCandidateIds = ["VisualNativeAliasAmbiguous"] }]);
                    continue;
                }
                if (nativeSource is not null && nativeTarget is not null && nativeSource != nativeTarget)
                {
                    var nativeDirection = DirectionOf(connector);
                    var sourceId = nativeDirection == ConnectionDirection.Reverse ? nativeTarget.Id : nativeSource.Id;
                    var targetId = nativeDirection == ConnectionDirection.Reverse ? nativeSource.Id : nativeTarget.Id;
                    rows.Add([new(connector.Id, sourceId, targetId, 1,
                        ConnectionConfidence.Native, cluster.Id, IsNative: true,
                        Direction: nativeDirection), none]);
                    continue;
                }

                if (options.Mode == VisualInferenceMode.NativeOnly)
                {
                    rows.Add([none]);
                    continue;
                }

                var start = nativeSource is null
                    ? Generate(connector, true, nodes, spatialIndex, cluster.Id, scale, options)
                    : [NativeCandidate(connector.Id, nativeSource.Id, true)];
                var end = nativeTarget is null
                    ? Generate(connector, false, nodes, spatialIndex, cluster.Id, scale, options)
                    : [NativeCandidate(connector.Id, nativeTarget.Id, false)];
                all.AddRange(start); all.AddRange(end);

                var direction = DirectionOf(connector);
                var intermediateNodeIds = FindIntermediateNodeIds(connector, spatialIndex, scale);
                var alternatives = (from left in start where !left.IsHardRejected
                                    from right in end where !right.IsHardRejected && left.NodeId != right.NodeId
                                    where !HasIntermediateNodeBetween(left.NodeId, right.NodeId, intermediateNodeIds)
                                    let score = (left.Score + right.Score) / 2
                                    let margin = Math.Min(left.Features.CandidateMargin, right.Features.CandidateMargin)
                                    let confidence = Classify(left, right, score, margin, options)
                                    let source = direction == ConnectionDirection.Reverse ? right.NodeId : left.NodeId
                                    let target = direction == ConnectionDirection.Reverse ? left.NodeId : right.NodeId
                                    select new ConnectionPairCandidate(connector.Id, source, target, score,
                                        confidence, cluster.Id, Direction: direction))
                    .OrderByDescending(pair => pair.Score)
                    .ThenBy(pair => pair.SourceId, StringComparer.Ordinal)
                    .ThenBy(pair => pair.TargetId, StringComparer.Ordinal)
                    .Take(options.PairCandidateLimit)
                    .ToArray();
                rows.Add([.. alternatives, none]);
            }

            if (rows.Count == 0) continue;
            var assignment = DiagramConnectionSolver.Solve(rows, options.MaxConnectors, options.BeamWidth);
            graphMargins.Add(assignment.Margin);
            var secondByConnector = (assignment.SecondSelected ?? [])
                .ToDictionary(pair => pair.ConnectorId, StringComparer.Ordinal);
            foreach (var pair in assignment.Selected.OrderBy(pair => pair.ConnectorId, StringComparer.Ordinal))
            {
                var changedInSecond = secondByConnector.TryGetValue(pair.ConnectorId, out var alternative) &&
                    (pair.SourceId != alternative.SourceId || pair.TargetId != alternative.TargetId);
                if (!pair.IsNative && pair.Confidence != ConnectionConfidence.High &&
                    changedInSecond && assignment.Margin < options.GraphMargin)
                {
                    unresolved.Add(pair with
                    {
                        SourceId = null,
                        TargetId = null,
                        Confidence = ConnectionConfidence.Unresolved,
                        RejectedCandidateIds = ["VisualGlobalAmbiguity"]
                    });
                }
                else if (pair.Confidence == ConnectionConfidence.Low)
                {
                    unresolved.Add(pair with
                    {
                        SourceId = null,
                        TargetId = null,
                        RejectedCandidateIds = ["VisualLowConfidence"]
                    });
                    diagnostics.Add(new VisualExtractionDiagnostic("VisualLowConfidence",
                        "A geometry candidate was retained as unresolved because its confidence is low.", pair.ConnectorId));
                }
                else if (pair.SourceId is not null && pair.TargetId is not null)
                {
                    resolved.Add(pair);
                }
                else
                {
                    unresolved.Add(pair);
                }
            }
        }
        return new(
            resolved.OrderBy(pair => pair.ConnectorId, StringComparer.Ordinal).ToArray(),
            unresolved.OrderBy(pair => pair.ConnectorId, StringComparer.Ordinal).ToArray(),
            all.OrderBy(c => c.ConnectorId, StringComparer.Ordinal).ThenBy(c => c.IsStart).ThenBy(c => c.NodeId, StringComparer.Ordinal).ToArray(),
            graphMargins.DefaultIfEmpty(0).Min(), diagnostics);
    }

    private static ConnectionCandidate NativeCandidate(string connectorId, string nodeId, bool isStart) =>
        new(connectorId, nodeId, isStart,
            new ConnectionFeatures(0, true, true, 0, 0, 1, true, false, 0, CandidateMargin: 1),
            Score: 1);

    private static ConnectionConfidence Classify(ConnectionCandidate start, ConnectionCandidate end,
        double score, double margin, SoftConnectionOptions options)
    {
        var exactBoundary = start.Features.BoundaryDistanceNormalized <= 1e-9 &&
            end.Features.BoundaryDistanceNormalized <= 1e-9 &&
            start.Features.IntermediateNodeCount == 0 && end.Features.IntermediateNodeCount == 0;
        var strongRay = start.Features.RayFirstHit && end.Features.RayFirstHit &&
            start.Features.AngularDeviationDegrees <= 15 && end.Features.AngularDeviationDegrees <= 15 &&
            start.Features.IntermediateNodeCount == 0 && end.Features.IntermediateNodeCount == 0;
        if (exactBoundary && margin >= options.MediumMargin / 2 ||
            margin >= options.HighMargin && (score >= options.HighThreshold || strongRay))
            return ConnectionConfidence.High;
        if (margin >= options.MediumMargin && score >= options.MediumThreshold)
            return ConnectionConfidence.Medium;
        if (score > 0 || margin > 0) return ConnectionConfidence.Low;
        return ConnectionConfidence.Unresolved;
    }

    private static bool HasIntermediateNodeBetween(string sourceId, string targetId,
        IReadOnlySet<string> intermediateNodeIds) =>
        intermediateNodeIds.Any(nodeId => nodeId != sourceId && nodeId != targetId);

    private static IReadOnlySet<string> FindIntermediateNodeIds(VisualConnectorPrimitive connector,
        NodeSpatialIndex spatialIndex, AdaptiveScale scale)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 1; index < connector.Path.Points.Count; index++)
        {
            var segmentStart = connector.Path.Points[index - 1];
            var segmentEnd = connector.Path.Points[index];
            foreach (var node in spatialIndex.QuerySegment(segmentStart, segmentEnd, scale.CorridorHalfWidth))
            {
                var distance = GeometryMath.DistanceToSegment(node.Bounds!.Center, segmentStart, segmentEnd,
                    out var projection);
                if (projection is > .02 and < .98 && distance <= scale.CorridorHalfWidth)
                    result.Add(node.Id);
            }
        }
        return result;
    }
    private static AdaptiveScale ScaleOf(VisualCanvas? canvas, IReadOnlyList<VisualNodePrimitive> nodes, IEnumerable<VisualConnectorPrimitive> connectors)
    {
        var axes = nodes.Select(n => Math.Min(n.Bounds!.Width, n.Bounds!.Height)).OrderBy(x => x).ToArray(); var minor = axes.Length == 0 ? 1 : axes[axes.Length / 2];
        var gaps = nodes.Select((n, i) => nodes.Where((_, j) => j != i).Select(other => GeometryMath.Distance(n.Bounds!.Center, other.Bounds!.Center)).DefaultIfEmpty(minor).Min()).OrderBy(x => x).ToArray(); var gap = gaps.Length == 0 ? minor : gaps[gaps.Length / 2];
        var lengths = connectors.Select(c => c.Path.Points.Zip(c.Path.Points.Skip(1), GeometryMath.Distance).Sum()).OrderBy(x => x).ToArray(); var length = lengths.Length == 0 ? minor : lengths[lengths.Length / 2];
        return new(canvas is { IsFinite: true } ? canvas.Diagonal : Math.Max(gap, minor), minor, gap, length);
    }

    private static ConnectionDirection DirectionOf(VisualConnectorPrimitive connector)
    { var start = connector.Path.StartArrowhead?.Present == true; var end = connector.Path.EndArrowhead?.Present == true; return start && end ? ConnectionDirection.Bidirectional : start ? ConnectionDirection.Reverse : end ? ConnectionDirection.Forward : ConnectionDirection.Unknown; }

    private static VisualNodePrimitive? ResolveAlias(string? alias, IEnumerable<VisualNodePrimitive> nodes)
    {
        if (alias is null) return null;
        var matches = nodes.Where(n => n.Id == alias || (n.Aliases ?? []).Any(a => a.Value == alias)).OrderBy(n => n.Id, StringComparer.Ordinal).ToArray();
        return matches.Length == 1 ? matches[0] : null; // alias collisions are deliberately unresolved, never thrown.
    }
    private static IReadOnlyList<ConnectionCandidate> Generate(VisualConnectorPrimitive connector, bool start,
        IReadOnlyList<VisualNodePrimitive> nodes, NodeSpatialIndex spatialIndex, string clusterId,
        AdaptiveScale adaptive, SoftConnectionOptions options)
    {
        var endpoint = start ? connector.Path.Start : connector.Path.End; // endpoint rays extend outward, away from the shaft.
        var tangent = start ? new VisualVector(-connector.Path.StartDirection.X, -connector.Path.StartDirection.Y) : connector.Path.EndDirection;
        var radius = options.Mode == VisualInferenceMode.Balanced ? adaptive.BalancedEndpointRadius : adaptive.SafeEndpointRadius;
        var candidateNodes = spatialIndex.QueryEndpoint(endpoint, tangent, radius, adaptive.RayExtension);
        var rayHits = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var node in candidateNodes)
            if (GeometryMath.RayIntersectsRect(endpoint, tangent, node.Bounds!, out var hit) && hit <= adaptive.RayExtension)
                rayHits[node.Id] = hit;

        var raw = new List<(ConnectionCandidate Candidate, double RayDistance)>();
        foreach (var node in candidateNodes)
        {
            var bounds = node.Bounds!;
            var distance = GeometryMath.BoundaryDistanceTo(endpoint, bounds, node.BoundaryKind);
            var ray = rayHits.TryGetValue(node.Id, out var rayDistance);
            if (!ray && distance > radius) continue;
            var intermediate = 0;
            var rayLimit = ray ? rayDistance : adaptive.RayExtension;
            foreach (var (nodeId, hit) in rayHits)
                if (nodeId != node.Id && hit > 0 && hit < rayLimit)
                    intermediate++;
            var segmentOffset = DistanceToPath(bounds.Center, connector.Path.Points);
            var corridor = segmentOffset <= adaptive.CorridorHalfWidth
                ? Math.Clamp(1 - segmentOffset / adaptive.CorridorHalfWidth, 0, 1)
                : 0;
            // A ray/boundary hit already proves directional alignment. Measuring the angle to
            // the node center would incorrectly penalize legitimate off-centre ports (for
            // example a thin Excel arrow entering the upper half of a tall process box).
            var angular = ray ? 0 : GeometryMath.AngleDegrees(tangent, bounds.Center - endpoint);
            var hard = intermediate > 0 && distance > radius;
            var totalWeight = Math.Max(options.DistanceWeight + options.RayWeight + options.AngleWeight + options.CorridorWeight, 1e-9);
            var score = (Math.Clamp(1 - distance / Math.Max(radius, 1), 0, 1) * options.DistanceWeight +
                (ray ? options.RayWeight : 0) + Math.Clamp(1 - angular / 180, 0, 1) * options.AngleWeight +
                corridor * options.CorridorWeight) / totalWeight - (intermediate > 0 ? .60 : 0);
            raw.Add((new(connector.Id, node.Id, start,
                new(distance / Math.Max(radius, 1), ray, false, angular,
                    segmentOffset / Math.Max(radius, 1), corridor, false,
                    connector.GroupId == node.GroupId && node.GroupId is not null, intermediate),
                score, hard), ray ? rayDistance : double.PositiveInfinity));
        }
        var firstRayDistance = rayHits.Count == 0 ? double.PositiveInfinity : rayHits.Values.Min();
        var firstHitTolerance = Math.Max(1e-9, adaptive.MinorAxis * 1e-6);
        var scored = raw.Select(x => x with
            {
                Candidate = x.Candidate with
                {
                    // A first ray hit establishes both direction and endpoint order even when
                    // Office grid spacing leaves a visible gap between the arrow body and box.
                    // Keep that evidence in the raw score; consumers must not need a format-level floor.
                    Score = Math.Clamp(x.Candidate.Score +
                        (Math.Abs(x.RayDistance - firstRayDistance) <= firstHitTolerance ? .40 : 0), 0, 1),
                    Features = x.Candidate.Features with
                    {
                        RayFirstHit = Math.Abs(x.RayDistance - firstRayDistance) <= firstHitTolerance
                    }
                }
            })
            .OrderByDescending(x => x.Candidate.Score)
            .ThenBy(x => x.Candidate.NodeId, StringComparer.Ordinal)
            .Take(options.CandidateLimit)
            .ToArray();
        var second = scored.Skip(1).FirstOrDefault().Candidate?.Score ?? 0;
        return scored.Select((x, index) => x.Candidate with
        {
            Features = x.Candidate.Features with
            {
                CandidateMargin = index == 0 ? x.Candidate.Score - second : 0
            }
        }).ToArray();
    }

    private static double DistanceToPath(VisualPoint point, IReadOnlyList<VisualPoint> points)
    {
        var distance = double.PositiveInfinity;
        for (var index = 1; index < points.Count; index++)
            distance = Math.Min(distance, GeometryMath.DistanceToSegment(point, points[index - 1], points[index], out _));
        return distance;
    }

    private sealed class NodeSpatialIndex
    {
        private readonly VisualNodePrimitive[] _nodes;
        private readonly Dictionary<(int X, int Y), List<int>> _cells = [];
        private readonly int[] _seen;
        private readonly double _cellSize;
        private readonly double _maxNodeExtent;
        private int _queryStamp;

        public NodeSpatialIndex(IReadOnlyList<VisualNodePrimitive> nodes, AdaptiveScale scale)
        {
            _nodes = nodes.ToArray();
            _seen = new int[_nodes.Length];
            _cellSize = Math.Max(1, Math.Max(scale.RayExtension,
                Math.Max(scale.SafeEndpointRadius, scale.CorridorHalfWidth)));
            _maxNodeExtent = _nodes.Select(node => Math.Max(node.Bounds!.Width, node.Bounds.Height)).DefaultIfEmpty(0).Max();
            for (var index = 0; index < _nodes.Length; index++)
            {
                var bounds = _nodes[index].Bounds!;
                for (var x = Cell(bounds.X); x <= Cell(bounds.Right); x++)
                for (var y = Cell(bounds.Y); y <= Cell(bounds.Bottom); y++)
                {
                    if (!_cells.TryGetValue((x, y), out var bucket)) _cells[(x, y)] = bucket = [];
                    bucket.Add(index);
                }
            }
        }

        public IReadOnlyList<VisualNodePrimitive> QueryEndpoint(VisualPoint endpoint, VisualVector tangent,
            double radius, double rayExtension)
        {
            var direction = tangent.Normalize();
            var rayEnd = direction.Length == 0
                ? endpoint
                : endpoint + new VisualVector(direction.X * rayExtension, direction.Y * rayExtension);
            var padding = Math.Max(radius, _maxNodeExtent);
            return QueryBounds(Math.Min(endpoint.X, rayEnd.X) - padding, Math.Min(endpoint.Y, rayEnd.Y) - padding,
                Math.Max(endpoint.X, rayEnd.X) + padding, Math.Max(endpoint.Y, rayEnd.Y) + padding);
        }

        public IReadOnlyList<VisualNodePrimitive> QuerySegment(VisualPoint start, VisualPoint end, double padding)
        {
            padding += _maxNodeExtent;
            return QueryBounds(Math.Min(start.X, end.X) - padding, Math.Min(start.Y, end.Y) - padding,
                Math.Max(start.X, end.X) + padding, Math.Max(start.Y, end.Y) + padding);
        }

        private IReadOnlyList<VisualNodePrimitive> QueryBounds(double minX, double minY, double maxX, double maxY)
        {
            var stamp = ++_queryStamp;
            if (stamp == 0)
            {
                Array.Clear(_seen);
                stamp = _queryStamp = 1;
            }
            var matches = new List<int>();
            for (var x = Cell(minX); x <= Cell(maxX); x++)
            for (var y = Cell(minY); y <= Cell(maxY); y++)
            {
                if (!_cells.TryGetValue((x, y), out var bucket)) continue;
                foreach (var index in bucket)
                {
                    if (_seen[index] == stamp) continue;
                    _seen[index] = stamp;
                    matches.Add(index);
                }
            }
            matches.Sort();
            return matches.Select(index => _nodes[index]).ToArray();
        }

        private int Cell(double coordinate) => (int)Math.Floor(coordinate / _cellSize);
    }
}
public sealed record EdgeLabelCandidate(string LabelId, string EdgeId, double Score);
public static class EdgeLabelAssigner
{
    private const int MaxCandidatesPerLabel = 4;
    private const int ExactAssignmentLabelLimit = 8;

    /// <summary>Deterministic maximum-weight one-to-one label/edge assignment.</summary>
    public static IReadOnlyDictionary<string,string> Assign(IEnumerable<EdgeLabelCandidate> candidates)
    {
        var rows = candidates.GroupBy(c => c.LabelId, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(c => c.Score).ThenBy(c => c.EdgeId, StringComparer.Ordinal)
                .Take(MaxCandidatesPerLabel).ToArray())
            .ToArray();

        if (rows.Length > ExactAssignmentLabelLimit)
        {
            var greedy = new Dictionary<string, string>(StringComparer.Ordinal);
            var usedEdges = new HashSet<string>(StringComparer.Ordinal);
            foreach (var candidate in rows.SelectMany(row => row)
                .OrderByDescending(candidate => candidate.Score)
                .ThenBy(candidate => candidate.LabelId, StringComparer.Ordinal)
                .ThenBy(candidate => candidate.EdgeId, StringComparer.Ordinal))
            {
                if (candidate.Score <= 0 || greedy.ContainsKey(candidate.LabelId) || !usedEdges.Add(candidate.EdgeId))
                    continue;
                greedy[candidate.LabelId] = candidate.EdgeId;
            }
            return greedy;
        }

        var best = new Dictionary<string,string>(StringComparer.Ordinal);
        var bestScore = double.NegativeInfinity;
        void Visit(int index, double score, HashSet<string> used, Dictionary<string,string> current)
        {
            if (index == rows.Length)
            {
                if (score > bestScore || score == bestScore &&
                    string.CompareOrdinal(string.Join("|", current.OrderBy(x => x.Key).Select(x => x.Key + ":" + x.Value)),
                        string.Join("|", best.OrderBy(x => x.Key).Select(x => x.Key + ":" + x.Value))) < 0)
                {
                    bestScore = score;
                    best = new(current, StringComparer.Ordinal);
                }
                return;
            }
            Visit(index + 1, score, used, current);
            foreach (var candidate in rows[index])
            {
                if (candidate.Score <= 0 || !used.Add(candidate.EdgeId))
                    continue;
                current[candidate.LabelId] = candidate.EdgeId;
                Visit(index + 1, score + candidate.Score, used, current);
                current.Remove(candidate.LabelId);
                used.Remove(candidate.EdgeId);
            }
        }
        Visit(0, 0, new(StringComparer.Ordinal), new(StringComparer.Ordinal));
        return best;
    }
}
