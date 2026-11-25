using System.Text;
using DocRedock.Api;
using DocRedock.Core.Documents;
using DocRedock.Formats.Pdf;
using DocRedock.Markdown;
using DocRedock.Render;
using static DocRedock.Tests.Pdf.PdfImageFixtures;

namespace DocRedock.Tests.Pdf;

/// <summary>Source content must remain present under the correct policy, including images and actual link actions.</summary>
public sealed class PdfNativeSourceContractTests
{
    [Fact]
    public void Source_URI_links_keep_their_table_cell_scope()
    {
        const string uri = "https://example.test/table-source";
        var pdf = MeasuredPdf.Page("""
            50 550 m 350 550 l S 50 600 m 350 600 l S 50 650 m 350 650 l S 50 700 m 350 700 l S
            50 550 m 50 700 l S 150 550 m 150 700 l S 250 550 m 250 700 l S 350 550 m 350 700 l S
            BT /F1 12 Tf 60 675 Td (Item) Tj ET
            BT /F1 12 Tf 160 675 Td (Reference) Tj ET
            BT /F1 12 Tf 260 675 Td (Description) Tj ET
            BT /F1 12 Tf 60 625 Td (manual) Tj ET
            BT /F1 12 Tf 160 625 Td (LINKCELL) Tj ET
            BT /F1 12 Tf 260 625 Td (Explanation) Tj ET
            BT /F1 12 Tf 60 575 Td (extra) Tj ET
            BT /F1 12 Tf 160 575 Td (SECOND) Tj ET
            BT /F1 12 Tf 260 575 Td (remark) Tj ET
            """, annotation: "<< /Type /Annot /Subtype /Link /Rect [159 623 230 640] /A << /S /URI /URI (" + uri + ") >> >>");
        var extraction = PdfTextExtractor.Extract(pdf);
        Assert.True(extraction.Tables is { Count: > 0 }, string.Join(" | ", extraction.Diagnostics ?? []));
        var graph = PdfDocumentGraphProjection.CreateGraph(extraction, "0123456789abcdef0123456789abcdef");
        var table = Assert.IsType<TableNodeContent>(Assert.Single(graph.Nodes, n => n.Kind == NodeKind.Table).Content);
        var cell = Assert.Single(table.Rows.SelectMany(row => row), c => c.Text == "LINKCELL");
        Assert.Equal(uri, Assert.Single(cell.Runs!).LinkTarget);
        var markdown = new ReadableMarkdownSerializer().Serialize(graph);
        Assert.Contains("[LINKCELL]", markdown, StringComparison.Ordinal);
        Assert.Contains(uri, markdown, StringComparison.Ordinal);
        var html = ReadableHtmlWriter.WriteDocument(ReadableProjectionBuilder.Build(graph).Document).Html;
        Assert.Contains("href=\"" + uri + "\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Source_URI_link_ranges_follow_an_ordered_list_when_its_marker_is_removed()
    {
        const string uri = "https://example.test/list-source";
        var left = 72 + MeasuredPdf.Width("1. ", 12);
        var right = left + MeasuredPdf.Width("LINKLIST", 12);
        var pdf = MeasuredPdf.Page("BT /F1 12 Tf 72 700 Td (1. LINKLIST) Tj ET\nBT /F1 12 Tf 72 680 Td (2. SECOND) Tj ET",
            annotation: FormattableString.Invariant($"<< /Type /Annot /Subtype /Link /Rect [{left} 698 {right} 714] /A << /S /URI /URI ({uri}) >> >>"));
        var graph = PdfDocumentGraphProjection.CreateGraph(PdfTextExtractor.Extract(pdf), "0123456789abcdef0123456789abcdef");
        var item = Assert.Single(graph.Nodes, n => n.Kind == NodeKind.ListItem &&
            n.Content is RichTextNodeContent rich && rich.Runs.Any(run => run.LinkTarget == uri));
        var rich = Assert.IsType<RichTextNodeContent>(item.Content);
        Assert.Equal("LINKLIST", Assert.Single(rich.Runs, run => run.LinkTarget == uri).Text);
    }

    [Theory]
    [InlineData("layer", "visible", 0)]
    [InlineData("layer", "sanitized", 0)]
    [InlineData("layer", "complete", 1)]
    [InlineData("image-oc", "visible", 0)]
    [InlineData("image-oc", "sanitized", 0)]
    [InlineData("image-oc", "complete", 1)]
    [InlineData("crop", "visible", 0)]
    [InlineData("crop", "sanitized", 0)]
    [InlineData("crop", "complete", 1)]
    [InlineData("clip", "visible", 0)]
    [InlineData("clip", "sanitized", 0)]
    [InlineData("clip", "complete", 1)]
    [InlineData("form", "visible", 0)]
    [InlineData("form", "sanitized", 0)]
    [InlineData("form", "complete", 1)]
    public void Hidden_image_draws_obey_the_same_policy_contract_as_hidden_native_text(
        string hiddenBy, string policy, int expectedImages)
    {
        var extraction = PdfTextExtractor.Extract(HiddenImagePage(hiddenBy));
        var graph = PdfDocumentGraphProjection.CreateGraph(extraction, "0123456789abcdef0123456789abcdef");
        // Complete is a retained-source contract, not permission to reclassify hidden pixels as body content.
        var image = Assert.Single(graph.Nodes, node => node.Kind == NodeKind.Image);
        Assert.Equal(ContentLayer.Hidden, image.Layer);
        var projection = ReadableProjectionBuilder.Build(graph, new ReadableProjectionOptions(ContentPolicy: policy));
        Assert.Equal(expectedImages, Flatten(projection.Document.Blocks).OfType<ReadableImage>().Count());
        Assert.Empty(projection.Report.UnwrittenImageIds);
    }

    [Fact]
    public void Repeating_a_central_body_figure_on_two_of_three_pages_does_not_classify_it_as_a_running_logo()
    {
        var figure = Image(5, 2, 2, "/DeviceGray", 8, [1, 2, 3, 4]);
        var pages = new[]
        {
            new PdfPage("q 100 0 0 100 50 50 cm /Im1 Do Q", Text: "Step one"),
            new PdfPage("", Text: "An intervening text-only page"),
            new PdfPage("q 100 0 0 100 50 50 cm /Im1 Do Q", Text: "Step three"),
        };
        var extraction = PdfTextExtractor.Extract(Build(pages, [figure]));
        Assert.Equal(new[] { 1, 0, 1 }, extraction.Pages.Select(page => page.ExtractedImages?.Count ?? 0));
        Assert.All(extraction.Pages, page => Assert.Equal(0, page.EmbeddedImageCount));
        var graph = PdfDocumentGraphProjection.CreateGraph(extraction, "0123456789abcdef0123456789abcdef");
        Assert.Equal(2, graph.Nodes.Count(node => node.Kind == NodeKind.Image));
        foreach (var policy in new[] { "visible", "sanitized", "complete" })
        {
            var native = ReadableProjectionBuilder.Build(graph, new ReadableProjectionOptions(ContentPolicy: policy));
            Assert.Equal(2, Flatten(native.Document.Blocks).OfType<ReadableImage>().Count());
            var markdown = new ReadableMarkdownSerializer(new ReadableMarkdownOptions(ContentPolicy: policy)).Serialize(graph);
            Assert.Contains("pdf-img-0005", markdown, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Actual_pdf_uri_link_retains_its_label_and_action_in_native_html_and_markdown()
    {
        const string uri = "https://example.test/specification?q=1&mode=source";
        var pdf = MeasuredPdf.Page("BT /F1 12 Tf 60 700 Td (SOURCE_LINK) Tj ET", annotation:
            "<< /Type /Annot /Subtype /Link /Rect [60 698 160 714] /A << /S /URI /URI (" + uri + ") >> >>");
        var graph = PdfDocumentGraphProjection.CreateGraph(PdfTextExtractor.Extract(pdf), "0123456789abcdef0123456789abcdef");
        var projection = ReadableProjectionBuilder.Build(graph);
        var paragraphs = Flatten(projection.Document.Blocks).OfType<ReadableParagraph>().ToArray();
        var link = Assert.Single(paragraphs.SelectMany(paragraph => paragraph.Content.Runs), run => run.LinkTarget == uri);
        Assert.Equal("SOURCE_LINK", link.Text);
        var html = ReadableHtmlWriter.WriteDocument(projection.Document).Html;
        Assert.Contains("href=\"https://example.test/specification?q=1&amp;mode=source\"", html, StringComparison.Ordinal);
        var markdown = new ReadableMarkdownSerializer().Serialize(graph);
        Assert.Contains("https://example.test/specification", markdown, StringComparison.Ordinal);
        Assert.Contains("SOURCE_LINK", DocRedock.Tests.Markdown.MarkdownStructure.DisplayedText(
            DocRedock.Tests.Markdown.MarkdownStructure.Parse(markdown)), StringComparison.Ordinal);
    }

    private static IEnumerable<ReadableBlock> Flatten(IEnumerable<ReadableBlock> blocks)
    {
        foreach (var block in blocks)
        {
            yield return block;
            var children = block switch
            {
                ReadableSection section => section.Blocks,
                ReadableDetails details => details.Blocks,
                ReadableQuote quote => quote.Blocks,
                _ => Array.Empty<ReadableBlock>(),
            };
            foreach (var child in Flatten(children)) yield return child;
        }
    }

    private static byte[] HiddenImagePage(string hiddenBy)
    {
        const string draw = "q 40 0 0 40 60 60 cm /Im1 Do Q";
        var content = hiddenBy == "layer" ? "/OC /Off BDC " + draw + " EMC"
            : hiddenBy == "clip" ? "q 0 0 20 20 re W n " + draw + " Q"
            : hiddenBy == "form" ? "/Fm1 Do" : draw;
        var image = Image(5, 2, 2, "/DeviceGray", 8, [1, 2, 3, 4], hiddenBy == "image-oc" ? "/OC 31 0 R" : "");
        var objects = new List<PdfObject> { image };
        var xobjects = "/Im1 5 0 R";
        if (hiddenBy == "form")
        {
            xobjects += " /Fm1 6 0 R";
            objects.Add(new PdfObject(6, "/Type /XObject /Subtype /Form /BBox [0 0 20 20] /Resources << /XObject << /Im1 5 0 R >> >>", Encoding.Latin1.GetBytes(draw)));
        }
        var pdf = Build([new PdfPage(content, xobjects)], objects);
        var text = Encoding.Latin1.GetString(pdf);
        if (hiddenBy == "crop") text = text.Replace("/MediaBox [0 0 200 200]", "/MediaBox [0 0 200 200] /CropBox [0 150 200 200]", StringComparison.Ordinal);
        if (hiddenBy is "layer" or "image-oc")
        {
            text = text.Replace("/Resources << /XObject", "/Resources << /Properties << /Off 31 0 R >> /XObject", StringComparison.Ordinal);
            text = text.Replace("%%EOF", "30 0 obj << /Type /Catalog /OCProperties << /OCGs [31 0 R] /D << /OFF [31 0 R] >> >> >> endobj\n" +
                "31 0 obj << /Type /OCG /Name (Hidden source image) >> endobj\n%%EOF", StringComparison.Ordinal);
        }
        return Encoding.Latin1.GetBytes(text);
    }
}
