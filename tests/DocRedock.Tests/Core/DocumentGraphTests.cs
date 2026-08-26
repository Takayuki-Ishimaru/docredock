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

    private static DocumentGraph Graph(params DocumentNode[] nodes) => new(DocumentGraph.CurrentSchemaVersion, "doc", DocumentFormatKind.Docx, [new("part-0001", 0, nodes)]);
    private static DocumentNode Node(string id, string text, ContentLayer layer = ContentLayer.Body, NodeEditability editability = NodeEditability.EditableInPlace) =>
        new(id, NodeKind.Paragraph, null, 0, layer, new TextNodeContent(text), new SourceAnchor("docx", "/word/document.xml", [new("w14_para_id", id)]), Editability: editability);
}
