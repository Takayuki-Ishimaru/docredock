using System.Text;
using System.Text.Json;
using DocRedock.Api;
using DocRedock.Core.Documents;
using DocRedock.Formats.Pdf;
using DocRedock.Markdown;
using static DocRedock.Tests.Pdf.MeasuredPdf;

namespace DocRedock.Tests.Pdf;

public sealed class PdfTableContinuationTests
{
    [Fact]
    public void Repeated_headers_at_page_boundaries_form_one_readable_table_without_changing_source_graph()
    {
        var extraction = PdfTextExtractor.Extract(Document(Table(30, 800, "Name", "A"), Table(30, 800, "Name", "B"), Table(720, 800, "Name", "C")));
        var graph = PdfDocumentGraphProjection.CreateGraph(extraction, "0123456789abcdef");
        var serializer = new ReadableMarkdownSerializer();
        var markdown = serializer.Serialize(graph);

        Assert.Equal(3, graph.Nodes.Count(node => node.Kind == NodeKind.Table));
        Assert.Equal(1, serializer.RenderedTables);
        Assert.Equal(1, markdown.Split("| Name | Amount |", StringSplitOptions.None).Length - 1);
        foreach (var label in new[] { "A", "B", "C" }) Assert.Contains("| " + label + " | 100 |", markdown);
        Assert.Equal(markdown, serializer.Serialize(graph));
    }

    [Theory]
    [InlineData("header")]
    [InlineData("columns")]
    [InlineData("intervening")]
    [InlineData("middle")]
    [InlineData("diagram")]
    public void Separate_tables_are_not_joined_without_all_continuation_evidence(string difference)
    {
        var first = Table(difference == "middle" ? 300 : 30, difference == "middle" ? 380 : 110, "Name", "A");
        var next = Table(720, 800, difference == "header" ? "Other" : "Name", "B", difference == "columns" ? 220 : 200);
        if (difference == "intervening") next = Text(50, 820, "New section") + "\n" + next;
        var graph = PdfDocumentGraphProjection.CreateGraph(PdfTextExtractor.Extract(Document(first, next)), "0123456789abcdef");
        if (difference == "diagram")
        {
            var firstPage = graph.Partitions[0];
            graph = graph with { Partitions = [firstPage with { Nodes = firstPage.Nodes.Concat([
                new DocumentNode("intervening", NodeKind.Diagram, null, firstPage.Nodes.Count, ContentLayer.Derived,
                    new TextNodeContent("flowchart LR\n A --> B"), Extensions: new Dictionary<string, JsonElement>
                    { ["diagram_language"] = JsonSerializer.SerializeToElement("mermaid") })]).ToArray() }, graph.Partitions[1]] };
        }
        var serializer = new ReadableMarkdownSerializer();
        var markdown = serializer.Serialize(graph);

        Assert.Equal(2, serializer.RenderedTables);
        Assert.Contains("| A | 100 |", markdown);
        Assert.Contains("| B | 100 |", markdown);
    }

    private static string Text(int x, int y, string text) => $"BT /F1 10 Tf 1 0 0 1 {x} {y} Tm ({text}) Tj ET";

    private static string Table(int bottom, int top, string header, string row, int divider = 200) =>
        $"50 {bottom} 300 {top - bottom} re S {divider} {bottom} m {divider} {top} l S 50 {top - 40} m 350 {top - 40} l S\n" +
        Text(60, top - 20, header) + "\n" + Text(divider + 10, top - 20, "Amount") + "\n" +
        Text(60, bottom + 20, row) + "\n" + Text(divider + 10, bottom + 20, "100");

    private static byte[] Document(params string[] pages)
    {
        var pdf = new StringBuilder("%PDF-1.4\n");
        pdf.Append("1 0 obj << /Type /Catalog /Pages 2 0 R >> endobj\n");
        pdf.Append("2 0 obj << /Type /Pages /MediaBox [0 0 595 842] /Kids [")
            .Append(string.Join(" ", pages.Select((_, index) => $"{10 + index * 2} 0 R")))
            .Append($"] /Count {pages.Length} >> endobj\n");
        pdf.Append("4 0 obj ").Append(SimpleFont).Append(" endobj\n");
        for (var index = 0; index < pages.Length; index++)
        {
            var id = 10 + index * 2;
            pdf.Append($"{id} 0 obj << /Type /Page /Parent 2 0 R /Resources {Resources} /Contents {id + 1} 0 R >> endobj\n")
                .Append($"{id + 1} 0 obj ").Append(Stream(pages[index])).Append(" endobj\n");
        }
        return Encoding.Latin1.GetBytes(pdf.Append("%%EOF").ToString());
    }
}
