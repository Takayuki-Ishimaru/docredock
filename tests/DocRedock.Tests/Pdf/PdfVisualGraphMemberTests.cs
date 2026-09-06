using System.Text;
using DocRedock.Api;
using DocRedock.Core.Documents;
using DocRedock.Formats.Pdf;

namespace DocRedock.Tests.Pdf;

/// <summary>Regression coverage for the "vanishing heading" defect: <c>visual_graph_member</c> used
/// to be assigned by comparing a paragraph's rendered text with a visual node's label, so any
/// heading or sentence that merely repeated a node's caption ("START") was flagged as part of the
/// diagram and silently dropped by the readable serializer, which skips graph members whenever it
/// can draw the diagram itself. Membership now follows the extractor's own record of which text
/// fragment a node actually consumed as its label.</summary>
public sealed class PdfVisualGraphMemberTests
{
    // One heading and one body sentence both read exactly "START", far above a bottom-up two-box
    // flow whose lower box is labelled "START" by a text fragment drawn inside it. The boxes are
    // stacked rather than side by side so the two in-diagram labels stay on separate baselines and
    // therefore remain separate paragraphs in the projection.
    private static byte[] RepeatedLabelPage() => Encoding.Latin1.GetBytes("""
        %PDF-1.4
        1 0 obj << /Type /Page >> endobj
        2 0 obj << /Length 420 >> stream
        BT 1 0 0 1 60 700 Tm (START) Tj ET
        BT 1 0 0 1 60 660 Tm (START) Tj ET
        BT 1 0 0 1 10 20 Tm (START) Tj ET
        0 0 100 50 re S
        BT 1 0 0 1 10 170 Tm (FINISH) Tj ET
        0 150 100 50 re S
        50 50 m 50 150 l S
        50 150 m 44 140 l 56 140 l h f
        endstream
        %%EOF
        """);

    private static IReadOnlyList<DocumentNode> Paragraphs(PdfExtractionResult result) =>
        PdfDocumentGraphProjection.CreateGraph(result, "0123456789abcdef0123456789abcdef")
            .Partitions!.SelectMany(partition => partition.Nodes)
            .Where(node => node.Kind == NodeKind.Paragraph && node.Content is TextNodeContent).ToArray();

    private static bool IsVisualGraphMember(DocumentNode node) =>
        node.Extensions?.TryGetValue("visual_graph_member", out var marker) == true && marker.GetBoolean();

    [Fact]
    public void Only_the_fragment_a_node_consumed_as_its_label_is_a_visual_graph_member()
    {
        var result = PdfTextExtractor.Extract(RepeatedLabelPage());
        Assert.True(result.VisualGraphs![1].HasTopology);

        var paragraphs = Paragraphs(result);
        var starts = paragraphs.Where(node => ((TextNodeContent)node.Content).Text == "START").ToArray();

        Assert.Equal(3, starts.Length);
        // The in-box label sits at the flow's baseline (y == 20); the heading and the body
        // sentence are hundreds of points above it and were never consumed by the graph.
        var member = Assert.Single(starts, IsVisualGraphMember);
        Assert.Equal(20d, member.Geometry!.Y);
        Assert.All(starts.Where(node => node.Geometry!.Y > 500), node => Assert.False(IsVisualGraphMember(node)));
        // The other box's own label is still a member - suppression of real diagram text is intact.
        Assert.True(IsVisualGraphMember(Assert.Single(paragraphs,
            node => ((TextNodeContent)node.Content).Text == "FINISH")));
        Assert.Equal(2, result.Pages[0].VisualLabelNodeIds!.Count);
    }

    [Fact]
    public async Task A_node_label_is_still_suppressed_while_the_repeated_headings_survive_readable_markdown()
    {
        var root = Path.Combine(Path.GetTempPath(), "docredock-pdf-graph-member-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "flow.pdf");
            await File.WriteAllBytesAsync(source, RepeatedLabelPage());

            var export = await new DocumentService().ExportReadableAsync(
                new ReadableDocumentExportOptions(source, Path.Combine(root, "flow.md")));
            var markdown = await File.ReadAllTextAsync(export.MarkdownPath);

            // Exactly the two body-text occurrences survive as standalone paragraph lines; the
            // third "START" lives on inside the rendered diagram instead.
            var standalone = markdown.Split('\n').Count(line => line.Trim() == "START");
            Assert.Equal(2, standalone);
            Assert.Contains("START", markdown, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
