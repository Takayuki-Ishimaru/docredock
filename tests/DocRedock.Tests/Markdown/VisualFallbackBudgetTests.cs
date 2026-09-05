using System.Text;
using System.Text.Json;
using DocRedock.Core.Documents;
using DocRedock.Markdown;

namespace DocRedock.Tests.Markdown;

public sealed class VisualFallbackBudgetTests
{
    [Fact]
    public void Dense_fallback_is_bounded_preserves_body_and_is_deterministic()
    {
        var paths = Enumerable.Range(0, 10_000).Select(index => new VisualPath($"p{index:D5}")).ToArray();
        var visual = new VisualGraph("dense", [], [], Paths: paths);
        var serializer = new ReadableMarkdownSerializer();
        var graph = Document(visual, new string('文', 50_000));

        var markdown = serializer.Serialize(graph);

        Assert.Contains(new string('文', 50_000), markdown);
        var fallback = markdown[markdown.IndexOf("### 図", StringComparison.Ordinal)..];
        Assert.True(fallback.Length <= 32_768, $"Fallback length: {fallback.Length}");
        Assert.True(markdown.Split("- パス: ", StringSplitOptions.None).Length - 1 <= 100);
        Assert.Contains("9900", markdown);
        Assert.Contains(serializer.Diagnostics, diagnostic => diagnostic.Code == "VisualFallbackCompacted");
        Assert.Equal(markdown, serializer.Serialize(graph));
    }

    [Fact]
    public void Oversized_diagnostic_and_source_identifier_cannot_bypass_block_cap()
    {
        var huge = new string('x', 100_000);
        var visual = new VisualGraph("oversized", [], [],
            Diagnostics: [new VisualDiagnostic("VisualFallbackUsed", huge)],
            Paths: [new VisualPath(huge)]);
        var serializer = new ReadableMarkdownSerializer();
        var markdown = serializer.Serialize(Document(visual, "BODY"));

        Assert.True(Encoding.UTF8.GetByteCount(markdown) < 40_000);
        Assert.Contains("BODY", markdown);
        Assert.Contains(serializer.Diagnostics, diagnostic => diagnostic.Code == "VisualFallbackCompacted");
    }

    [Fact]
    public void Disabled_diagrams_do_not_render_invalid_raw_visual_fallback()
    {
        var visual = new VisualGraph("invalid",
            [new VisualNode("same", "ONE"), new VisualNode("same", "TWO")], [],
            Paths: [new VisualPath("raw")]);
        var serializer = new ReadableMarkdownSerializer(new(IncludeDiagrams: false));

        var markdown = serializer.Serialize(Document(visual, "BODY"));

        Assert.DoesNotContain("フォールバック", markdown);
        Assert.DoesNotContain("- パス:", markdown);
        Assert.Contains("BODY", markdown);
    }

    [Fact]
    public void Sequence_with_unresolved_two_participant_message_never_invents_direction()
    {
        var nodes = new[]
        {
            new VisualNode("a", "A", Geometry: new("test", 0, 0, 40, 20)),
            new VisualNode("b", "B", Geometry: new("test", 100, 0, 40, 20))
        };
        var edges = new[]
        {
            new VisualEdge("life-a", null, null, Resolution: VisualEdgeResolution.Unresolved,
                Geometry: new("test", 20, 20, 0, 180), EdgeDirection: VisualEdgeDirection.Undirected),
            new VisualEdge("life-b", null, null, Resolution: VisualEdgeResolution.Unresolved,
                Geometry: new("test", 120, 20, 0, 180), EdgeDirection: VisualEdgeDirection.Undirected),
            new VisualEdge("unknown", null, null, "UNCERTAIN", VisualEdgeResolution.Unresolved,
                Geometry: new("test", 20, 80, 100, 0), EdgeDirection: VisualEdgeDirection.Directed)
        };

        var markdown = new ReadableMarkdownSerializer().Serialize(Document(new("sequence", nodes, edges), "BODY"));

        Assert.Contains("sequenceDiagram", markdown);
        Assert.Contains("Note over P1,P2: UNCERTAIN", markdown);
        Assert.DoesNotContain("->>", markdown);
    }

    private static DocumentGraph Document(VisualGraph visual, string body) =>
        new(DocumentGraph.CurrentSchemaVersion, "budget", DocumentFormatKind.Pdf,
        [new DocumentPartition("page-1", 0,
        [
            new DocumentNode("body", NodeKind.Paragraph, null, 0, ContentLayer.Body, new TextNodeContent(body)),
            new DocumentNode("diagram", NodeKind.Diagram, null, 1, ContentLayer.Derived,
                new TextNodeContent("visual"), Extensions: new Dictionary<string, JsonElement>
                { ["visual_graph"] = JsonSerializer.SerializeToElement(visual) })
        ])]);
}
