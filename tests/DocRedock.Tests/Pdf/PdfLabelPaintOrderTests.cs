using System.Text;
using System.Text.Json;
using DocRedock.Api;
using DocRedock.Core.Documents;
using DocRedock.Formats.Pdf;

namespace DocRedock.Tests.Pdf;

public sealed class PdfLabelPaintOrderTests
{
    [Fact]
    public async Task Evaluation_marker_order_preserves_table_flow_and_unique_captions()
    {
        var root = Directory.CreateTempSubdirectory("docredock-paint-order-").FullName;
        try
        {
            var results = new List<PdfExtractionResult>();
            var markdown = new List<string>();
            foreach (var name in new[] { "schedule-overlay.pdf", "schedule-marker-last.pdf" })
            {
                var source = Fixture(name);
                var extraction = PdfTextExtractor.Extract(source);
                results.Add(extraction);
                var raw = extraction.VisualGraphs![1];
                var done = Assert.Single(raw.Nodes, node => node.Label == "DONE");
                Assert.Equal(new Geometry("pdf-user-space", 470, 75, 150, 55), done.Geometry);
                var projected = extraction.VisualProjections![1].Graph;
                Assert.Equal(new[] { "DONE", "START" }, projected.Nodes.Select(node => node.Label).Order().ToArray());
                var edge = Assert.Single(projected.Edges);
                Assert.Equal("START", projected.Nodes.Single(node => node.Id == edge.SourceId).Label);
                Assert.Equal("DONE", projected.Nodes.Single(node => node.Id == edge.TargetId).Label);
                Assert.True(projected.SourceAccounting.IsConsistent);
                Assert.DoesNotContain(projected.Diagnostics ?? [], diagnostic => diagnostic.Code == "VisualConnectorUnresolved");

                var path = Path.Combine(root, name + ".md");
                var exported = await new DocumentService().ExportReadableAsync(new ReadableDocumentExportOptions(source, path));
                Assert.False(ExportReviewBuilder.Build(exported.Graph, exported.Diagnostics).Required);
                var text = await File.ReadAllTextAsync(path);
                Assert.Contains("◆", text);
                Assert.Contains("━━", text);
                Assert.Contains("```mermaid", text);
                Assert.Equal(1, Count(text, "START"));
                Assert.Equal(1, Count(text, "DONE"));
                markdown.Add(text);
            }
            Assert.Equal(TableSemantics(results[0]), TableSemantics(results[1]));
            // Node/path IDs are source anchors and may follow paint order; table cells do not.
            Assert.Equal(TableMarkdown(markdown[0]), TableMarkdown(markdown[1]));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void External_captions_choose_their_closest_box_in_either_paint_order(bool reverse)
    {
        var boxes = new[] { "0 0 100 50 re S", "200 0 100 50 re S" };
        if (reverse) Array.Reverse(boxes);
        var graph = Graph(string.Join("\n", boxes) + """

            BT /F1 12 Tf 1 0 0 1 10 65 Tm (START) Tj ET
            BT /F1 12 Tf 1 0 0 1 210 65 Tm (DONE) Tj ET
            100 25 m 200 25 l S
            200 25 m 190 32 l 190 18 l h f
            """);
        Assert.Equal(0, Assert.Single(graph.Nodes, node => node.Label == "START").Geometry!.X);
        Assert.Equal(200, Assert.Single(graph.Nodes, node => node.Label == "DONE").Geometry!.X);
        Assert.Equal(2, graph.Nodes.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Equal_shape_scores_do_not_claim_the_same_caption_by_paint_order(bool reverse)
    {
        var boxes = new[] { "0 0 50 50 re S", "150 0 50 50 re S" };
        if (reverse) Array.Reverse(boxes);
        var graph = Graph(string.Join("\n", boxes) + "\nBT /F1 12 Tf 1 0 0 1 94 20 Tm (XX) Tj ET");
        Assert.DoesNotContain(graph.Nodes, node => node.Label == "XX");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_chain_of_near_equal_scores_is_ambiguous_in_both_paint_orders(bool reverse)
    {
        // Distinct nested boxes have almost equal caption scores. Comparing each new score
        // against the last winner with a tolerance would make this three-way tie non-transitive.
        var boxes = new[]
        {
            "90 6 40 40 re S",
            "70.0000000726 -14 80 80 re S",
            "30.0000001452 -54 160 160 re S"
        };
        if (reverse) Array.Reverse(boxes);
        var graph = Graph(string.Join("\n", boxes) + "\nBT /F1 12 Tf 1 0 0 1 94 20 Tm (XX) Tj ET");
        Assert.DoesNotContain(graph.Nodes, node => node.Label == "XX");
    }

    private static VisualGraph Graph(string content)
    {
        var pdf = Encoding.Latin1.GetBytes("%PDF-1.4\n1 0 obj << /Type /Page >> endobj\n2 0 obj << /Length " +
            content.Length + " >> stream\n" + content + "\nendstream\n%%EOF");
        return Assert.Single(PdfTextExtractor.Extract(pdf).VisualGraphs!).Value;
    }

    private static string TableSemantics(PdfExtractionResult result) => JsonSerializer.Serialize(
        result.Tables![1].Select(table => new
        {
            Rows = table.Rows.Select(row => row.Cells.Select(cell => cell.Text).ToArray()).ToArray(),
            Overlays = table.Overlays!.Select(overlay => new
            {
                overlay.Kind, overlay.Direction, overlay.ShapePreset,
                overlay.StartRow, overlay.EndRow, overlay.StartColumn, overlay.EndColumn, overlay.Text
            }).OrderBy(overlay => overlay.StartRow).ThenBy(overlay => overlay.StartColumn).ToArray()
        }));

    private static string TableMarkdown(string text) => string.Join("\n", text.Split('\n').Where(line => line.StartsWith('|')));
    private static int Count(string text, string value) => (text.Length - text.Replace(value, "", StringComparison.Ordinal).Length) / value.Length;
    private static string Fixture(string name)
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
        {
            var path = Path.Combine(current.FullName, "tests", "DocRedock.Tests", "Fixtures", "Evaluation", "V036", name);
            if (File.Exists(path)) return path;
        }
        throw new FileNotFoundException(name);
    }
}
