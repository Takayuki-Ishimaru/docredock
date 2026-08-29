using DocRedock.Core.Diff;
using DocRedock.Core.Documents;
using DocRedock.Core.Reporting;

namespace DocRedock.Tests.Core;

public sealed class DocumentGraphTests
{
    [Fact]
    public void NodeId_is_stable_when_text_changes()
    {
        var anchor = new SourceAnchor("docx", "/word/document.xml", [new("w14_para_id", "A1B2")], 2, "fingerprint");
        var first = NodeIdGenerator.CreateForSource("document-namespace", DocumentFormatKind.Docx, anchor);
        var second = NodeIdGenerator.CreateForSource("document-namespace", DocumentFormatKind.Docx, anchor);

        Assert.Equal(first, second);
        Assert.StartsWith("n_", first);
        Assert.Equal(18, first.Length);
    }

    [Fact]
    public void Deterministic_json_sorts_dictionary_keys()
    {
        var value = new Dictionary<string, object> { ["z"] = 1, ["a"] = 2 };

        var json = DeterministicJson.Serialize(value);

        Assert.Equal("{\"a\":2,\"z\":1}", json);
    }

    [Fact]
    public void Deterministic_json_normalizes_unicode_to_form_c()
    {
        var composed = DeterministicJson.Serialize(new { Text = "é" });
        var decomposed = DeterministicJson.Serialize(new { Text = "e\u0301" });

        Assert.Equal(composed, decomposed);
    }

    [Fact]
    public void Canonical_graph_round_trips_with_polymorphic_content_and_capabilities()
    {
        var graph = Graph(Node("n_text", "本文")) with
        {
            Capabilities = new GraphCapabilities(["restore.byte_identical"])
        };

        var restored = DeterministicJson.Deserialize<DocumentGraph>(DeterministicJson.Serialize(graph));

        Assert.NotNull(restored);
        Assert.True(restored.Capabilities!.Supports("restore.byte_identical"));
        Assert.Equal("本文", Assert.IsType<TextNodeContent>(restored.FindNode("n_text")!.Content).Text);
    }

    [Fact]
    public void Diff_preserves_missing_nodes_but_applies_explicit_delete()
    {
        var original = Node("n_original", "before");
        var baseline = Graph(original);
        var edited = Graph();
        var engine = new DocumentGraphDiffEngine();

        var omitted = engine.Compare(baseline, edited);
        var deleted = engine.Compare(baseline, edited, new DiffOptions(new HashSet<string> { original.Id }));

        Assert.Empty(omitted.PatchSet.Operations);
        Assert.Contains(omitted.Diagnostics, diagnostic => diagnostic.Code == "MissingNode");
        Assert.Single(deleted.PatchSet.Operations);
        Assert.Equal(PatchOperationKind.ExplicitDelete, deleted.PatchSet.Operations[0].Kind);
        Assert.True(deleted.DirtySet.HasOriginalMutations);
    }

    [Fact]
    public void Derived_annotation_change_does_not_dirty_source_part()
    {
        var baseline = Graph(Node("n_ocr", "one", ContentLayer.Derived, NodeEditability.AnnotationOnly));
        var edited = Graph(Node("n_ocr", "two", ContentLayer.Derived, NodeEditability.AnnotationOnly));

        var result = new DocumentGraphDiffEngine().Compare(baseline, edited);

        Assert.Equal(PatchOperationKind.UpdateDerivedAnnotation, Assert.Single(result.PatchSet.Operations).Kind);
        Assert.False(result.DirtySet.HasOriginalMutations);
        Assert.Empty(result.DirtySet.DirtyPartUris);
    }

    [Fact]
    public void Equivalent_nodes_with_independent_collections_are_not_dirty()
    {
        var baseline = Graph(Node("n_same", "unchanged"));
        var edited = Graph(Node("n_same", "unchanged"));

        var result = new DocumentGraphDiffEngine().Compare(baseline, edited);

        Assert.Empty(result.PatchSet.Operations);
    }

    [Fact]
    public void Ocr_contract_has_all_six_explicit_states()
    {
        Assert.Equal(6, Enum.GetValues<OcrProcessingStatus>().Length);
    }

    [Fact]
    public void Graph_construction_normalizes_duplicate_node_ids_without_dictionary_failure()
    {
        var graph = new DocumentGraph(DocumentGraph.CurrentSchemaVersion, "doc", DocumentFormatKind.Docx,
        [
            new DocumentPartition("first", 0, [Node("duplicate", "one")]),
            new DocumentPartition("second", 1, [Node("duplicate", "two"), Node("", "three")])
        ]);

        Assert.Equal(["duplicate", "duplicate__2", "node_2_2"], graph.Nodes.Select(node => node.Id));
        Assert.Equal(3, graph.Nodes.Select(node => node.Id).ToHashSet(StringComparer.Ordinal).Count);
        Assert.Equal("duplicate__2", graph.FindNode("duplicate__2")!.Id);
    }

    [Fact]
    public void Graph_with_normalizes_empty_and_duplicate_ids_without_raw_dictionary_failure()
    {
        var graph = Graph(Node("stable", "one")) with
        {
            Partitions = [new DocumentPartition("part", 0, [Node("", "empty"), Node("stable", "one"), Node("stable", "duplicate")])]
        };

        Assert.Equal(["node_1_1", "stable", "stable__2"], graph.Nodes.Select(node => node.Id));
        Assert.Equal(graph.Nodes.Count(), graph.Nodes.Select(node => node.Id).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Graph_normalization_keeps_duplicate_parent_references_bound_to_first_source_node()
    {
        var parent = Node("duplicate", "parent");
        var duplicate = Node("duplicate", "second parent");
        var child = Node("child", "child") with { ParentId = "duplicate" };
        var graph = new DocumentGraph(DocumentGraph.CurrentSchemaVersion, "doc", DocumentFormatKind.Docx,
            [new DocumentPartition("part", 0, [parent, duplicate, child])]);

        Assert.Equal("duplicate", graph.FindNode("child")!.ParentId);
        Assert.NotNull(graph.FindNode(graph.FindNode("child")!.ParentId!));
    }

    [Fact]
    public void Visual_graph_exposes_compatible_metadata_and_consistent_accounting()
    {
        var anchor = new SourceAnchor("pptx", "ppt/slides/slide1.xml", [new("shape_id", "1")]);
        var graph = new VisualGraph("flow", [new VisualNode("a", "Start", Geometry: new Geometry("pptx-emu", 0, 0, 1, 1), SourceAnchor: anchor, Group: "g", Lane: "main")],
            [new VisualEdge("resolved", "a", "a", Direction: "forward", Confidence: 1, Path: [new(0, 0), new(1, 1)], SourceAnchor: anchor), new VisualEdge("unresolved", null, null, Resolution: VisualEdgeResolution.Unresolved)],
            [new VisualDiagnostic("VisualConnectorUnresolved", "unresolved")], Groups: [new VisualGroup("g", "Main", ["a"], "main")],
            Paths: [new VisualPath("fallback-vector", [new(2, 2)], SourceAnchor: anchor)]);

        Assert.Equal(1, graph.Accounting.RecognizedNodes);
        Assert.Equal(2, graph.Accounting.RecognizedEdges);
        Assert.Equal(1, graph.Accounting.ResolvedEdges);
        Assert.Equal(1, graph.Accounting.UnresolvedEdges);
        Assert.Equal(1, graph.Accounting.RecognizedPaths);
        Assert.Equal(1, graph.Accounting.FallbackPaths);
        Assert.True(graph.Accounting.IsConsistent);
    }

    [Fact]
    public void Visual_graph_rejects_dangling_or_duplicate_node_topology()
    {
        var dangling = new VisualGraph("flow", [new VisualNode("a", "A")], [new VisualEdge("edge", "a", "missing")]);
        var duplicate = new VisualGraph("flow", [new VisualNode("a", "A"), new VisualNode("a", "Again")], [new VisualEdge("edge", "a", "a")]);
        var malformed = new VisualGraph("flow", [null!], [null!]);

        Assert.False(dangling.HasTopology);
        Assert.False(duplicate.HasTopology);
        Assert.False(malformed.HasTopology);
        Assert.True(malformed.Accounting.IsConsistent);
    }

    [Fact]
    public void Legacy_json_with_null_partitions_deserializes_as_empty_graph()
    {
        const string legacyJson = """{"schema_version":"1.0","document_id":"legacy","format":"docx","partitions":null}""";

        var graph = DeterministicJson.Deserialize<DocumentGraph>(legacyJson);

        Assert.NotNull(graph);
        Assert.Empty(graph!.Partitions);
        Assert.Equal("legacy", graph.DocumentId);
        Assert.Equal(DocumentFormatKind.Docx, graph.Format);
    }

    private static DocumentGraph Graph(params DocumentNode[] nodes) => new(DocumentGraph.CurrentSchemaVersion, "doc", DocumentFormatKind.Docx, [new("part-0001", 0, nodes)]);
    private static DocumentNode Node(string id, string text, ContentLayer layer = ContentLayer.Body, NodeEditability editability = NodeEditability.EditableInPlace) =>
        new(id, NodeKind.Paragraph, null, 0, layer, new TextNodeContent(text), new SourceAnchor("docx", "/word/document.xml", [new("w14_para_id", id)]), Editability: editability);
}
