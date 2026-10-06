using System.Text;
using DocRedock.Api;
using DocRedock.Core.Documents;
using DocRedock.Formats.Pdf;
using DocRedock.Providers.Abstractions.Providers;
using DocRedock.Render;

namespace DocRedock.Tests.Api;

/// <summary>How a Form XObject the extractor could not analyze is placed in the page and named in
/// the review image and its diagnostics, including next to other review reasons.</summary>
public sealed class UnanalyzedContentProjectionTests
{
    /// <summary>A 612x400 page drawing <paramref name="content"/>, with /Fm1 an LZW-encoded form
    /// (which DocRedock cannot decode) whose /BBox is <paramref name="formBox"/>, and /Im1 an image.</summary>
    private static byte[] Page(string content, string formBox)
    {
        const string form = "BT 1 0 0 1 10 10 Tm (HIDDEN) Tj ET";
        return Encoding.Latin1.GetBytes("%PDF-1.4\n" +
            "1 0 obj << /Type /Page /MediaBox [0 0 612 400] /Contents 2 0 R /Resources << /XObject << /Fm1 5 0 R /Im1 6 0 R >> >> >> endobj\n" +
            "2 0 obj << /Length " + content.Length + " >> stream\n" + content + "\nendstream endobj\n" +
            "5 0 obj << /Type /XObject /Subtype /Form /BBox [" + formBox + "] /Filter /LZWDecode /Length " + form.Length + " >> stream\n" + form + "\nendstream endobj\n" +
            "6 0 obj << /Type /XObject /Subtype /Image /Width 8 /Height 8 >> endobj\n%%EOF");
    }

    [Fact]
    public void The_marker_sits_where_the_form_paints_even_beside_an_unplaceable_image_placeholder()
    {
        // The image is drawn with a degenerate CTM, so its placeholder has no position and goes
        // last; the form paints between the two paragraphs and must stay there.
        var extraction = PdfTextExtractor.Extract(Page(
            "BT 1 0 0 1 10 350 Tm (TOP) Tj ET\nBT 1 0 0 1 10 20 Tm (BOTTOM) Tj ET\nq 0 0 0 0 0 0 cm /Im1 Do Q\n/Fm1 Do",
            "0 150 612 250"));

        var nodes = Assert.Single(PdfDocumentGraphProjection.CreateGraph(extraction, new string('a', 64)).Partitions).Nodes;

        var order = nodes.Select(node => node.Extensions?.ContainsKey("pdf_unparsed_form_xobjects") == true ? "FORM"
            : node.Extensions?.ContainsKey("pdf_embedded_image_placeholder") == true ? "IMAGE"
            : (node.Content as TextNodeContent)?.Text ?? string.Empty).ToArray();
        Assert.Equal(["TOP", "FORM", "BOTTOM", "IMAGE"], order);
    }

    [Fact]
    public async Task A_page_with_an_unresolved_line_and_an_unparsed_form_names_both_reasons()
    {
        var root = Directory.CreateTempSubdirectory("docredock-both-").FullName;
        try
        {
            var source = Path.Combine(root, "both.pdf");
            await File.WriteAllBytesAsync(source, Page(
                "BT 1 0 0 1 10 350 Tm (TOP) Tj ET\n100 100 m 300 300 l S\n/Fm1 Do", "0 0 200 100"));
            var result = await new DocumentService(null, new BlankRasterizer(), discoverPdfRasterizer: false)
                .ExportReadableAsync(new ReadableDocumentExportOptions(source, Path.Combine(root, "both.md"), IncludePdfFallbackImages: true));

            var image = Assert.Single(result.Graph.Nodes, node => node.Kind == NodeKind.Image);
            Assert.Equal("PDF page 1: 原本照合用画像（未解決の表・図と解析できなかった描画部品を確認）",
                Assert.IsType<ReferenceNodeContent>(image.Content).AltText);
            Assert.Contains(result.Diagnostics, d => d.Code == "PdfReviewImageAttached" &&
                d.Message.Contains("unresolved tables/figures and content that could not be analyzed", StringComparison.Ordinal));
            var summary = ExportSummaryBuilder.Build(result.Graph, result.Diagnostics);
            Assert.Equal((1, 1, 1), (summary.ReviewPages, summary.VisualReviewPages, summary.UnanalyzedContentPages));
            Assert.Contains("Detected review items: visual 1 page(s), unanalyzed content 1 page(s)", summary.ToString(), StringComparison.Ordinal);
            var page = Assert.Single(ExportReviewBuilder.Build(result.Graph, result.Diagnostics).Pages);
            Assert.Contains(page.Elements, element => element.Kind == ReviewElementKind.UnanalyzedContent);
            Assert.Contains(page.Elements, element => element.Kind is ReviewElementKind.Line or ReviewElementKind.Arrow);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Any_node_asking_for_source_review_counts_as_unanalyzed_content_in_both_the_summary_and_the_review()
    {
        // A node an adapter marks for source review without recording what it could not analyze.
        var node = new DocumentNode("note", NodeKind.Annotation, null, 0, ContentLayer.Body, new TextNodeContent("[not analyzed]"),
            Extensions: new Dictionary<string, System.Text.Json.JsonElement>(StringComparer.Ordinal)
            {
                [DocRedock.Markdown.ReadableMarkdownSerializer.SourceReviewRequiredExtension] = System.Text.Json.JsonSerializer.SerializeToElement(true),
            });
        var graph = new DocumentGraph(DocumentGraph.CurrentSchemaVersion, "note", DocumentFormatKind.Pdf,
            [new DocumentPartition("page-0001", 0, [node])]);

        var summary = ExportSummaryBuilder.Build(graph, []);
        var page = Assert.Single(ExportReviewBuilder.Build(graph, []).Pages);

        Assert.Equal((1, 1, 1), (summary.ReviewPages, summary.UnanalyzedContent, summary.UnanalyzedContentPages));
        Assert.Equal(ReviewElementKind.UnanalyzedContent, Assert.Single(page.Elements).Kind);
        Assert.Contains("Detected review items: unanalyzed content 1 page(s)", summary.ToString(), StringComparison.Ordinal);
    }

    private sealed class BlankRasterizer : IPdfRasterizer
    {
        public ProviderDescriptor Descriptor { get; } = new("test.pdf.rasterizer", new Version(1, 0), 1,
            new HashSet<string> { "rasterize.pdf" }, "MIT", "built-in", true);

        public ValueTask<IReadOnlyList<RasterizedPdfPage>> RasterizeAsync(string pdfPath, IReadOnlyList<int> pageNumbers,
            PdfRasterizationOptions options, CancellationToken cancellationToken = default)
        {
            const int width = 612, height = 400;
            var rgb = new byte[width * height * 3];
            Array.Fill(rgb, (byte)255);
            var png = PngRasterImage.Encode(width, height, rgb);
            return ValueTask.FromResult<IReadOnlyList<RasterizedPdfPage>>(pageNumbers
                .Select(page => new RasterizedPdfPage(page, "image/png", png, width, height)).ToList());
        }
    }
}
