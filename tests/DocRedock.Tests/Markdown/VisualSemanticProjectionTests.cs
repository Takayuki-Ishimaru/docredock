using System.Text.Json;
using DocRedock.Core.Documents;
using DocRedock.Markdown;

namespace DocRedock.Tests.Markdown;

public sealed class VisualSemanticProjectionTests
{
    [Fact]
    public void Disabled_diagrams_keep_source_fallback_without_warning()
    {
        var visual = new VisualGraph("flow",
            [new VisualNode("start", "START"), new VisualNode("end", "END")],
            [new VisualEdge("edge", "start", "end")]);
        var diagram = new DocumentNode("diagram", NodeKind.Diagram, null, 0, ContentLayer.Derived,
            new TextNodeContent("derived visual"), Extensions: new Dictionary<string, JsonElement>
            {
                ["visual_graph"] = JsonSerializer.SerializeToElement(visual)
            });
        var graph = new DocumentGraph(DocumentGraph.CurrentSchemaVersion, "visual", DocumentFormatKind.Pptx,
            [new DocumentPartition("slide1", 0, [diagram])]);

        var serializer = new ReadableMarkdownSerializer(new ReadableMarkdownOptions(IncludeDiagrams: false));
        var markdown = serializer.Serialize(graph);

        Assert.DoesNotContain("flowchart LR", markdown, StringComparison.Ordinal);
        Assert.Contains("### 図の接続関係", markdown, StringComparison.Ordinal);
        Assert.Contains("START → END", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain(serializer.Diagnostics, item => item.Severity == MarkdownDiagnosticSeverity.Warning);
        Assert.Contains(serializer.Diagnostics, item => item.Code == "VisualDiagramRenderingDisabled");
    }

    [Fact]
    public void Medium_evidence_emits_contract_note_and_warning_diagnostic()
    {
        var visual = new VisualGraph("medium-flow",
            [new VisualNode("start", "START"), new VisualNode("end", "END")],
            [new VisualEdge("edge", "start", "end", Resolution: VisualEdgeResolution.GeometryInferred,
                Confidence: .9, Evidence: new("geometry", "Medium", .9))]);
        var diagram = new DocumentNode("diagram", NodeKind.Diagram, null, 0, ContentLayer.Derived,
            new TextNodeContent("derived visual"), Extensions: new Dictionary<string, JsonElement>
            {
                ["visual_graph"] = JsonSerializer.SerializeToElement(visual)
            });
        var graph = new DocumentGraph(DocumentGraph.CurrentSchemaVersion, "medium", DocumentFormatKind.Pptx,
            [new DocumentPartition("slide1", 0, [diagram])]);

        var serializer = new ReadableMarkdownSerializer();
        var markdown = serializer.Serialize(graph);

        Assert.Contains("一部の接続は図形配置から推定されています。診断を確認してください。", markdown, StringComparison.Ordinal);
        var partial = Assert.Single(serializer.Diagnostics, item => item.Code == "VisualSemanticProjectionPartial");
        Assert.Equal(MarkdownDiagnosticSeverity.Warning, partial.Severity);
        Assert.Contains(serializer.Diagnostics, item => item.Code == "VisualInferenceMediumConfidence" &&
            item.Severity == MarkdownDiagnosticSeverity.Warning);
    }

    [Fact]
    public void No_diagrams_suppresses_visual_members_while_retaining_one_relation_list()
    {
        var visual = new VisualGraph("flow",
            [new VisualNode("start", "START"), new VisualNode("end", "END")],
            [new VisualEdge("edge", "start", "end")]);
        var diagram = new DocumentNode("diagram", NodeKind.Diagram, null, 0, ContentLayer.Derived,
            new TextNodeContent("derived visual"), Extensions: new Dictionary<string, JsonElement>
            {
                ["visual_graph"] = JsonSerializer.SerializeToElement(visual)
            });
        var member = new DocumentNode("member", NodeKind.TextBox, null, 1, ContentLayer.Body,
            new TextNodeContent("START"), Extensions: new Dictionary<string, JsonElement>
            {
                ["visual_graph_member"] = JsonSerializer.SerializeToElement(true)
            });
        var graph = new DocumentGraph(DocumentGraph.CurrentSchemaVersion, "flow", DocumentFormatKind.Docx,
            [new DocumentPartition("document", 0, [diagram, member])]);

        var markdown = new ReadableMarkdownSerializer(new ReadableMarkdownOptions(IncludeDiagrams: false)).Serialize(graph);

        Assert.DoesNotContain("```mermaid", markdown, StringComparison.Ordinal);
        Assert.Equal(1, markdown.Split("START", StringSplitOptions.None).Length - 1);
        Assert.Contains("START → END", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void Structured_visual_diagnostics_dedupe_by_source_location()
    {
        var visual = new VisualGraph("diagnostics",
            [new VisualNode("start", "START"), new VisualNode("end", "END")],
            [new VisualEdge("edge", "start", "end")],
            Diagnostics:
            [
                new VisualDiagnostic("VisualConnectorUnresolved", "first", SourceNodeId: "connector",
                    Format: "pptx", PartUri: "/ppt/slides/slide1.xml", PartitionId: "slide1", SourceObjectId: "8"),
                new VisualDiagnostic("VisualConnectorUnresolved", "duplicate", SourceNodeId: "connector",
                    Format: "pptx", PartUri: "/ppt/slides/slide1.xml", PartitionId: "slide1", SourceObjectId: "8"),
                new VisualDiagnostic("VisualConnectorUnresolved", "another source", SourceNodeId: "connector",
                    Format: "pptx", PartUri: "/ppt/slides/slide1.xml", PartitionId: "slide1", SourceObjectId: "9"),
            ]);
        var diagram = new DocumentNode("diagram", NodeKind.Diagram, null, 0, ContentLayer.Derived,
            new TextNodeContent("derived visual"), Extensions: new Dictionary<string, JsonElement>
            {
                ["visual_graph"] = JsonSerializer.SerializeToElement(visual)
            });
        var graph = new DocumentGraph(DocumentGraph.CurrentSchemaVersion, "diagnostics", DocumentFormatKind.Pptx,
            [new DocumentPartition("slide1", 0, [diagram])]);

        var serializer = new ReadableMarkdownSerializer();
        serializer.Serialize(graph);

        Assert.Equal(2, serializer.Diagnostics.Count(item => item.Code == "VisualConnectorUnresolved"));
    }

    [Fact]
    public void Blank_promoted_node_label_is_not_rendered_as_mermaid()
    {
        var visual = new VisualGraph("blank-label",
            [new VisualNode("start", "START"), new VisualNode("missing", "")],
            [new VisualEdge("edge", "start", "missing")]);
        var diagram = new DocumentNode("diagram", NodeKind.Diagram, null, 0, ContentLayer.Derived,
            new TextNodeContent("derived visual"), Extensions: new Dictionary<string, JsonElement>
            {
                ["visual_graph"] = JsonSerializer.SerializeToElement(visual)
            });
        var fallback = new DocumentNode("fallback", NodeKind.Connector, null, 1, ContentLayer.Body,
            new TextNodeContent("START → [unlabeled shape: missing]"));
        var graph = new DocumentGraph(DocumentGraph.CurrentSchemaVersion, "visual", DocumentFormatKind.Pptx,
            [new DocumentPartition("slide1", 0, [diagram, fallback])]);

        var serializer = new ReadableMarkdownSerializer();
        var markdown = serializer.Serialize(graph);

        Assert.DoesNotContain("```mermaid", markdown, StringComparison.Ordinal);
        Assert.Contains("START → [unlabeled shape: missing]", markdown, StringComparison.Ordinal);
    }
}
