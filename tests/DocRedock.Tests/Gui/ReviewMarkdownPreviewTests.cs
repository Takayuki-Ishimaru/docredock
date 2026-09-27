using System.Text.Json;
using System.Text.RegularExpressions;
using DocRedock.Core.Documents;
using DocRedock.Gui;
using DocRedock.Markdown;

namespace DocRedock.Tests.Gui;

/// <summary>v0.2.10 evaluation, priority 2: the review window's rendered Markdown shows each
/// flowchart as its connections. It must read exactly what the readable serializer writes, and
/// fall back to the source rather than show a partial list.</summary>
public sealed class ReviewMarkdownPreviewTests
{
    private static string Mermaid(VisualGraph visual)
    {
        var node = new DocumentNode("visual", NodeKind.Diagram, null, 0, ContentLayer.Derived, new TextNodeContent("Visual flow"),
            Extensions: new Dictionary<string, JsonElement>(StringComparer.Ordinal) { ["visual_graph"] = JsonSerializer.SerializeToElement(visual) });
        var markdown = new ReadableMarkdownSerializer().Serialize(new DocumentGraph(DocumentGraph.CurrentSchemaVersion, "preview",
            DocumentFormatKind.Pptx, [new DocumentPartition("slide1", 0, [node])]));
        return Regex.Match(markdown, "```mermaid\n(?<body>.*?)\n```", RegexOptions.Singleline).Groups["body"].Value;
    }

    [Fact]
    public void Serialized_flowchart_reads_back_as_its_connections()
    {
        var mermaid = Mermaid(new VisualGraph("flow",
            [
                new VisualNode("n_start", "Start", VisualNodeKind.Terminator, Geometry: new Geometry("s", 0, 0, 1, 1)),
                new VisualNode("n_check", "OK?", VisualNodeKind.Decision, Geometry: new Geometry("s", 0, 1, 1, 1)),
                new VisualNode("n_data", "A[&\"|", VisualNodeKind.Data, Geometry: new Geometry("s", 0, 2, 1, 1)),
                new VisualNode("n_end", "End", VisualNodeKind.Process, Geometry: new Geometry("s", 0, 3, 1, 1)),
                new VisualNode("n_note", "Note", Geometry: new Geometry("s", 0, 4, 1, 1)),
            ],
            [
                new VisualEdge("e1", "n_start", "n_check", Geometry: new Geometry("s", 0, 0, 1, 1)),
                new VisualEdge("e2", "n_check", "n_data", "YES|NO", Geometry: new Geometry("s", 0, 1, 1, 1)),
                new VisualEdge("e3", "n_data", "n_end", EdgeDirection: VisualEdgeDirection.Undirected, Geometry: new Geometry("s", 0, 2, 1, 1)),
                new VisualEdge("e4", "n_end", "n_start", LineStyle: VisualLineStyles.Dashed, Geometry: new Geometry("s", 0, 3, 1, 1)),
            ]));
        Assert.StartsWith("flowchart", mermaid, StringComparison.Ordinal);

        Assert.Equal(
        [
            "Start → OK?",
            "OK? → A[&\"|（YES|NO）",
            "A[&\"| ― End",
            "End → Start（点線）",
            "・Note",
        ], ReviewMarkdownPreview.DescribeFlowchart(mermaid));
    }

    [Fact]
    public void Labels_with_parentheses_slashes_and_line_breaks_read_back_whole()
    {
        var mermaid = Mermaid(new VisualGraph("labels",
            [
                new VisualNode("a", "Review (QA)", VisualNodeKind.Process, Geometry: new Geometry("s", 0, 0, 1, 1)),
                new VisualNode("b", "(1) 受付", VisualNodeKind.Process, Geometry: new Geometry("s", 0, 1, 1, 1)),
                new VisualNode("c", "a/", VisualNodeKind.Process, Geometry: new Geometry("s", 0, 2, 1, 1)),
                new VisualNode("d", "x/", VisualNodeKind.Data, Geometry: new Geometry("s", 0, 3, 1, 1)),
            ],
            [
                new VisualEdge("e1", "a", "b", Geometry: new Geometry("s", 0, 0, 1, 1)),
                new VisualEdge("e2", "c", "d", Geometry: new Geometry("s", 0, 1, 1, 1)),
            ]));

        Assert.Equal(["Review (QA) → (1) 受付", "a/ → x/"], ReviewMarkdownPreview.DescribeFlowchart(mermaid));
        Assert.Equal(["上段 下段 → B"], ReviewMarkdownPreview.DescribeFlowchart("flowchart LR\n    n1[上段<br/>下段]\n    n2[B]\n    n1 --> n2"));
    }

    [Theory]
    [InlineData("sequenceDiagram\n    participant A\n    A->>B: hi")]
    [InlineData("flowchart LR\n    a[A]\n    a --> b\n    subgraph x\n    end")]
    [InlineData("pie title x\n    \"a\" : 1")]
    [InlineData("flowchart LR\n    a((circle))\n    a --> b")]
    [InlineData("")]
    public void Anything_it_cannot_read_completely_falls_back_to_the_source(string mermaid) =>
        Assert.Null(ReviewMarkdownPreview.DescribeFlowchart(mermaid));
}
