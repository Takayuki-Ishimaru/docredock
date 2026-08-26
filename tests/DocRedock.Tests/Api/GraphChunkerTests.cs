using DocRedock.Api;
using DocRedock.Core.Documents;

namespace DocRedock.Tests.Api;

public sealed class GraphChunkerTests
{
    [Fact]
    public void Keeps_nodes_complete_and_preserves_heading_context()
    {
        var graph = new DocumentGraph("1.1", "doc_1", DocumentFormatKind.Docx,
        [
            new DocumentPartition("part-1", 0,
            [
                new DocumentNode("h", NodeKind.Heading, null, 0, ContentLayer.Body, new TextNodeContent("Architecture")),
                new DocumentNode("p1", NodeKind.Paragraph, null, 1, ContentLayer.Body, new TextNodeContent(new string('a', 100))),
                new DocumentNode("p2", NodeKind.Paragraph, null, 2, ContentLayer.Body, new TextNodeContent(new string('b', 100))),
            ])
        ]);

        var chunks = new GraphChunker().Chunk(graph, new GraphChunkOptions(128));

        Assert.Equal(2, chunks.Count);
        Assert.All(chunks, chunk => Assert.True(chunk.CompleteNodes));
        Assert.Contains("Architecture", chunks[1].ContextHeadingPath);
        Assert.Equal(["h", "p1", "p2"], chunks.SelectMany(chunk => chunk.NodeIds));
    }
}
