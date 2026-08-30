using DocRedock.Core.Documents;
using DocRedock.VisualInference;

namespace DocRedock.Tests.VisualInference;

public sealed class R5CoreInferenceTests
{
    [Fact]
    public void Ellipse_and_diamond_boundary_points_are_exact_endpoints()
    {
        var ellipse = new VisualRect(0, 0, 20, 10);
        var diamond = new VisualRect(0, 0, 20, 10);
        Assert.Equal(0, GeometryMath.BoundaryDistanceTo(new VisualPoint(20, 5), ellipse, VisualBoundaryKind.Ellipse), 8);
        Assert.Equal(0, GeometryMath.BoundaryDistanceTo(new VisualPoint(20, 5), diamond, VisualBoundaryKind.Diamond), 8);
    }

    [Fact]
    public void Low_confidence_candidates_are_never_promoted()
    {
        var connector = new VisualConnectorPrimitive("line", "c", Anchor(),
            new VisualConnectorPath([new(30, 5), new(70, 5)]));
        var result = new SoftConnectionEngine().Infer(Document([
            new VisualNodePrimitive("a", "c", Anchor(), new VisualRect(0, 0, 20, 10)),
            new VisualNodePrimitive("b", "c", Anchor(), new VisualRect(80, 0, 20, 10)), connector]),
            options: new SoftConnectionOptions(HighThreshold: .99, MediumThreshold: .99, HighMargin: 1, MediumMargin: 1));
        Assert.Empty(result.Resolved);
        var unresolved = Assert.Single(result.Unresolved);
        Assert.Equal(ConnectionConfidence.Low, unresolved.Confidence);
        Assert.Null(unresolved.SourceId);
        Assert.Null(unresolved.TargetId);
        Assert.Contains("VisualLowConfidence", unresolved.RejectedCandidateIds!);
        Assert.Contains(result.Diagnostics!, diagnostic => diagnostic.Code == "VisualLowConfidence");
    }

    [Fact]
    public void Cancellation_is_observed_before_heavy_inference()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => new SoftConnectionEngine().Infer(Document([]), cancellationToken: cancellation.Token));
    }

    [Fact]
    public void Weight_and_resource_limits_are_configurable_and_deterministic()
    {
        var options = new SoftConnectionOptions(DistanceWeight: .8, RayWeight: .1, AngleWeight: .05,
            CorridorWeight: .05, MaxConnectors: 1, BeamWidth: 2);
        Assert.Equal(.8, options.DistanceWeight);
        Assert.Equal(1, options.MaxConnectors);
        Assert.Equal(2, options.BeamWidth);
        var rows = Enumerable.Range(0, 2).Select(i => (IReadOnlyList<ConnectionPairCandidate>)[
            new ConnectionPairCandidate("c" + i, "a" + i, "b" + i, .9, ConnectionConfidence.High, "cluster"),
            new ConnectionPairCandidate("c" + i, null, null, 0, ConnectionConfidence.Unresolved, "cluster")]).ToArray();
        var assignment = DiagramConnectionSolver.Solve(rows, options.MaxConnectors, options.BeamWidth);
        Assert.Contains(assignment.Selected, item => item.RejectedCandidateIds?.Contains("VisualClusterLimitExceeded") == true);
    }

    [Fact]
    public void Mixed_coordinate_spaces_are_rejected_with_deterministic_diagnostic()
    {
        var connector = new VisualConnectorPrimitive("line", "b", Anchor(), new VisualConnectorPath([new(25, 5), new(75, 5)]));
        var document = new VisualPrimitiveDocument("d", DocumentFormatKind.Pptx,
            [new VisualCanvas("a", "/a", null, 100, 100, "pixels"), new VisualCanvas("b", "/b", null, 100, 100, "points")],
            [new VisualNodePrimitive("a", "a", Anchor(), new VisualRect(0, 0, 20, 10)),
             new VisualNodePrimitive("b", "b", Anchor(), new VisualRect(80, 0, 20, 10)), connector]);
        var result = new SoftConnectionEngine().Infer(document,
            [new DiagramCluster("mixed", ["a", "b", "line"])], new SoftConnectionOptions());
        Assert.Empty(result.Resolved);
        Assert.Contains(result.Diagnostics!, item => item.Code == "VisualCoordinateSpaceIncompatible");
    }

    [Fact]
    public void Cluster_resource_limit_is_reported_without_nondeterministic_output()
    {
        var primitives = Enumerable.Range(0, 8).Select(i => (VisualPrimitive)new VisualNodePrimitive(
            "n" + i, "c", Anchor(), new VisualRect(i * 2, 0, 1, 1))).ToArray();
        var clusterer = new DiagramClusterer();
        var clusters = clusterer.Cluster(Document(primitives), new DiagramClusterOptions(MaxPairChecks: 1));
        Assert.NotEmpty(clusters);
        Assert.Contains(clusterer.Diagnostics, item => item.Code == "VisualClusterResourceLimit");
    }

    private static VisualPrimitiveDocument Document(IReadOnlyList<VisualPrimitive> primitives) =>
        new("d", DocumentFormatKind.Pptx, [new VisualCanvas("c", "/slide", null, 1200, 100, "slide")], primitives);
    private static SourceAnchor Anchor() => new("test", "/test", []);
}
