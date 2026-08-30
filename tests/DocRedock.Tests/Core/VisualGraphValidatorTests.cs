using DocRedock.Core.Documents;

namespace DocRedock.Tests.Core;

public sealed class VisualGraphValidatorTests
{
    [Fact]
    public void Source_ledger_is_complete_and_deterministic()
    {
        var graph = ValidGraph() with
        {
            SourceItems =
            [
                new VisualSourceItem("shape-start", VisualSourceItemKind.Shape, VisualDisposition.ProjectedNode, ProjectedNodeId: "start"),
                new VisualSourceItem("shape-end", VisualSourceItemKind.Shape, VisualDisposition.ProjectedNode, ProjectedNodeId: "end"),
                new VisualSourceItem("connector", VisualSourceItemKind.Connector, VisualDisposition.ProjectedEdge, ProjectedEdgeId: "edge")
            ]
        };

        var first = VisualGraphValidator.Validate(graph);
        var second = VisualGraphValidator.Validate(graph);

        Assert.True(first.IsValidForSemanticProjection);
        Assert.True(first.Accounting.IsConsistent);
        Assert.Equal(3, first.Accounting.RecognizedSourceItems);
        Assert.Equal(first.IsValidForSemanticProjection, second.IsValidForSemanticProjection);
        Assert.Equal(first.Errors, second.Errors);
        Assert.Equal(first.Warnings, second.Warnings);
        Assert.Equal(first.Accounting, second.Accounting);
    }

    [Theory]
    [InlineData(VisualDisposition.ProjectedNode)]
    [InlineData(VisualDisposition.ProjectedEdge)]
    public void Invalid_source_reference_rejects_semantic_projection(VisualDisposition disposition)
    {
        var item = disposition == VisualDisposition.ProjectedNode
            ? new VisualSourceItem("source", VisualSourceItemKind.Shape, disposition, ProjectedNodeId: "missing")
            : new VisualSourceItem("source", VisualSourceItemKind.Connector, disposition, ProjectedEdgeId: "missing");
        var result = VisualGraphValidator.Validate(ValidGraph() with { SourceItems = [item] });

        Assert.False(result.IsValidForSemanticProjection);
        Assert.False(result.Accounting.IsConsistent);
        Assert.Contains(result.Errors, issue => issue.Code == "VisualSourceItemReferenceInvalid");
    }

    [Fact]
    public void Blank_labels_self_edges_and_low_confidence_inference_are_rejected()
    {
        var blank = ValidGraph() with { Nodes = [new VisualNode("start", "")] };
        var self = ValidGraph() with { Edges = [new VisualEdge("edge", "start", "start")] };
        var inferred = ValidGraph() with { Edges = [new VisualEdge("edge", "start", "end", Resolution: VisualEdgeResolution.GeometryInferred, Confidence: 0.5)] };

        Assert.Contains(VisualGraphValidator.Validate(blank).Errors, issue => issue.Code == "VisualNodeLabelMissing");
        Assert.Contains(VisualGraphValidator.Validate(self).Errors, issue => issue.Code == "VisualSelfEdge");
        Assert.Contains(VisualGraphValidator.Validate(inferred).Errors, issue => issue.Code == "VisualEdgeConfidenceTooLow");
    }

    [Fact]
    public void Isolated_synthetic_placeholder_node_is_rejected()
    {
        var graph = ValidGraph() with
        {
            Nodes = [.. ValidGraph().Nodes, new VisualNode("vector", "Vector node 3")]
        };

        var result = VisualGraphValidator.Validate(graph);

        Assert.False(result.IsValidForSemanticProjection);
        Assert.Contains(result.Errors, issue => issue.Code == "VisualSyntheticNodeIsolated");
    }

    [Theory]
    [InlineData("Vector node 3")]
    [InlineData("Shape 42")]
    public void Connected_synthetic_placeholder_is_retained_with_a_warning(string label)
    {
        // R3 intentionally revises the old blanket rejection: a synthetic label may
        // participate in a resolved relation, while an isolated placeholder remains invalid.
        var graph = ValidGraph() with
        {
            Nodes = [new VisualNode("start", label), new VisualNode("end", "END")],
            SourceItems = Ledger()
        };

        var result = VisualGraphValidator.Validate(graph);

        Assert.True(result.IsValidForSemanticProjection);
        Assert.Contains(result.Warnings, issue => issue.Code == "VisualSyntheticNodeConnected");
        Assert.DoesNotContain(result.Errors, issue => issue.Code == "VisualSyntheticNodePlaceholder");
    }

    [Fact]
    public void Generic_placeholder_source_can_remain_as_accounted_visual_fallback()
    {
        var graph = ValidGraph() with
        {
            Paths = [new VisualPath("shape-42", IsFallback: true)],
            SourceItems = [.. Ledger(), new VisualSourceItem("Shape 42", VisualSourceItemKind.Shape,
                VisualDisposition.VisualFallback, FallbackPathId: "shape-42")]
        };

        var result = VisualGraphValidator.Validate(graph);

        Assert.True(result.IsValidForSemanticProjection);
        Assert.Equal(1, result.Accounting.VisualFallbacks);
    }

    [Fact]
    public void Connector_cannot_be_accounted_as_a_node()
    {
        var graph = ValidGraph() with
        {
            SourceItems = [new VisualSourceItem("edge-shape", VisualSourceItemKind.Connector, VisualDisposition.ProjectedNode, ProjectedNodeId: "start")]
        };

        var result = VisualGraphValidator.Validate(graph);

        Assert.False(result.IsValidForSemanticProjection);
        Assert.Contains(result.Errors, issue => issue.Code == "VisualSourceItemReferenceInvalid");
    }

    [Fact]
    public void Duplicate_relations_and_node_labels_reused_as_edge_labels_are_rejected()
    {
        var duplicate = ValidGraph() with
        {
            Edges =
            [
                new VisualEdge("edge-1", "start", "end", Confidence: 1),
                new VisualEdge("edge-2", "start", "end", Confidence: 1),
            ]
        };
        var reusedLabel = ValidGraph() with
        {
            Edges = [new VisualEdge("edge", "start", "end", Label: "START", Confidence: 1)]
        };

        Assert.Contains(VisualGraphValidator.Validate(duplicate).Errors, issue => issue.Code == "VisualEdgeDuplicate");
        Assert.Contains(VisualGraphValidator.Validate(reusedLabel).Errors, issue => issue.Code == "VisualEdgeLabelReusedAsNode");
    }

    [Fact]
    public void Weak_or_contradictory_connection_evidence_is_rejected()
    {
        var weak = ValidGraph() with
        {
            Edges = [new VisualEdge("edge", "start", "end", Resolution: VisualEdgeResolution.GeometryInferred,
                Confidence: .9, Evidence: new("geometry", "Low", .9))]
        };
        var contradictory = ValidGraph() with
        {
            Edges = [new VisualEdge("edge", "start", "end", Direction: "undirected", Confidence: 1,
                EdgeDirection: VisualEdgeDirection.Undirected, Evidence: new("native", "Native", 1, ArrowheadEvidence: "end"))]
        };

        Assert.Contains(VisualGraphValidator.Validate(weak).Errors, issue => issue.Code == "VisualEdgeEvidenceTooWeak");
        Assert.Contains(VisualGraphValidator.Validate(contradictory).Errors, issue => issue.Code == "VisualEdgeDirectionContradiction");
    }

    [Fact]
    public void Medium_unresolved_and_fallback_visuals_are_warnings_not_validation_errors()
    {
        var graph = ValidGraph() with
        {
            Edges = [new VisualEdge("edge", "start", "end", Resolution: VisualEdgeResolution.GeometryInferred,
                Confidence: .9, Evidence: new("geometry", "Medium", .9)), new VisualEdge("unresolved", null, null)],
            Paths = [new VisualPath("fallback", IsFallback: true)]
        };

        var result = VisualGraphValidator.Validate(graph);

        Assert.True(result.IsValidForSemanticProjection);
        Assert.Contains(result.Warnings, issue => issue.Code == "VisualInferenceMediumConfidence");
        Assert.Contains(result.Warnings, issue => issue.Code == "VisualConnectorUnresolved");
        Assert.Contains(result.Warnings, issue => issue.Code == "VisualFallbackUsed");
    }

    [Fact]
    public void Legacy_graphs_remain_compatible_but_new_source_ledger_is_serializable()
    {
        var legacy = ValidGraph();
        Assert.True(legacy.HasTopology);

        var anchored = ValidGraph() with
        {
            SourceItems = [new VisualSourceItem("start-source", VisualSourceItemKind.Shape, VisualDisposition.ProjectedNode,
                ProjectedNodeId: "start", SourceAnchor: new SourceAnchor("pptx", "/ppt/slides/slide1.xml", [new("shape_id", "1")]))]
        };
        var json = DeterministicJson.Serialize(anchored);
        var restored = DeterministicJson.Deserialize<VisualGraph>(json);

        Assert.NotNull(restored);
        Assert.Equal(json, DeterministicJson.Serialize(restored));
        Assert.Equal("start-source", restored!.SourceItems![0].Id);
        Assert.Equal("/ppt/slides/slide1.xml", restored.SourceItems[0].SourceAnchor!.PartUri);
    }

    private static VisualGraph ValidGraph() => new("flow",
        [new VisualNode("start", "START"), new VisualNode("end", "END")],
        [new VisualEdge("edge", "start", "end", Confidence: 1)]);

    private static VisualSourceItem[] Ledger() =>
    [
        new("shape-start", VisualSourceItemKind.Shape, VisualDisposition.ProjectedNode, ProjectedNodeId: "start"),
        new("shape-end", VisualSourceItemKind.Shape, VisualDisposition.ProjectedNode, ProjectedNodeId: "end"),
        new("connector", VisualSourceItemKind.Connector, VisualDisposition.ProjectedEdge, ProjectedEdgeId: "edge")
    ];
}
