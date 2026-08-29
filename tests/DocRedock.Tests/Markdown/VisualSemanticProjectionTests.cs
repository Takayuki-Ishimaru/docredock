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
