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
}
