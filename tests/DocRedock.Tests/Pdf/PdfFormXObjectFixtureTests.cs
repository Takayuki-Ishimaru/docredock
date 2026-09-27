using DocRedock.Api;
using DocRedock.Core.Documents;
using DocRedock.Core.Reporting;
using DocRedock.Formats.Pdf;
using DocRedock.Providers.Abstractions.Providers;
using DocRedock.Render;

namespace DocRedock.Tests.Pdf;

/// <summary>The v0.2.10 evaluation's pair of visually identical PDFs - one page painting its
/// lower block directly, the other through a Form XObject - kept as fixtures (see
/// generate_form_xobject_pdf.py). The form files must export exactly like the flat file, warnings
/// and review pages included; a form DocRedock cannot decode must ask for source review.</summary>
public sealed class PdfFormXObjectFixtureTests
{
    private const string Sentinel = "FORM\\_TEXT\\_SENTINEL: approved";

    [Theory]
    [InlineData("form-xobject-form.pdf")]
    [InlineData("form-xobject-nested.pdf")]
    public async Task Form_fixture_exports_exactly_like_the_flat_page(string variant)
    {
        var flatExtraction = PdfTextExtractor.Extract(FixturePath("form-xobject-flat.pdf"));
        var formExtraction = PdfTextExtractor.Extract(FixturePath(variant));
        Assert.Equal(flatExtraction.Text, formExtraction.Text);
        Assert.Equal(flatExtraction.Diagnostics, formExtraction.Diagnostics);

        var root = Directory.CreateTempSubdirectory("docredock-form-").FullName;
        try
        {
            var flat = await ExportAsync(root, "form-xobject-flat.pdf", rasterizer: null);
            var form = await ExportAsync(root, variant, rasterizer: null);

            Assert.Equal(flat.Markdown, form.Markdown);
            Assert.Contains(Sentinel, form.Markdown, StringComparison.Ordinal);
            Assert.Contains("[START]", form.Markdown, StringComparison.Ordinal);
            Assert.Contains("[END]", form.Markdown, StringComparison.Ordinal);
            var summary = ExportSummaryBuilder.Build(form.Result.Graph, form.Result.Diagnostics);
            Assert.Equal((0, 0, 0), (summary.ReviewPages, summary.UnanalyzedContent, summary.Warnings));
            Assert.Contains("Human review: not required", summary.ToString(), StringComparison.Ordinal);
            Assert.Contains("Unanalyzed content: none", summary.ToString(), StringComparison.Ordinal);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Form_fixture_keeps_the_flat_page_warning_and_review_image()
    {
        var root = Directory.CreateTempSubdirectory("docredock-form-").FullName;
        try
        {
            var flat = await ExportAsync(root, "form-xobject-flat-review.pdf", new BlankRasterizer());
            var form = await ExportAsync(root, "form-xobject-form-review.pdf", new BlankRasterizer());

            Assert.Equal(flat.Markdown, form.Markdown);
            Assert.Equal(flat.Result.Diagnostics.Select(d => (d.Code, d.Message)), form.Result.Diagnostics.Select(d => (d.Code, d.Message)));
            var flatReview = ExportReviewBuilder.Build(flat.Result.Graph, flat.Result.Diagnostics);
            var formReview = ExportReviewBuilder.Build(form.Result.Graph, form.Result.Diagnostics);
            var page = Assert.Single(formReview.Pages);
            Assert.Equal(ExportReviewText.DescribeJapanese(Assert.Single(flatReview.Pages)), ExportReviewText.DescribeJapanese(page));
            Assert.Equal(ReviewElementKind.Line, Assert.Single(page.Elements).Kind);
            Assert.Equal("out.assets/page-0001.png", page.ReviewImageReference);
            Assert.Contains(Sentinel, form.Markdown, StringComparison.Ordinal);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Undecodable_form_is_reported_where_it_paints()
    {
        var extraction = PdfTextExtractor.Extract(FixturePath("form-xobject-unreadable.pdf"));
        var page = Assert.Single(extraction.Pages);

        Assert.DoesNotContain("FORM_TEXT_SENTINEL", extraction.Text, StringComparison.Ordinal);
        Assert.Contains("Quarterly review", extraction.Text, StringComparison.Ordinal);
        var unparsed = Assert.Single(page.UnparsedFormXObjects!);
        Assert.Equal(PdfFormXObjectUnparsedReason.UnreadableStream, unparsed.Reason);
        // ReportLab's form /BBox is the page itself.
        Assert.Equal(0d, unparsed.Bounds!.X, 3);
        Assert.Equal(0d, unparsed.Bounds.Y, 3);
        Assert.Equal(595.2756, unparsed.Bounds.Width, 3);
        Assert.Equal(841.8898, unparsed.Bounds.Height, 3);
        Assert.Single(extraction.Diagnostics!, d => d.StartsWith("PdfFormXObjectUnparsed: PDF page 1:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Undecodable_form_page_requires_review_and_carries_a_located_review_image()
    {
        var root = Directory.CreateTempSubdirectory("docredock-form-").FullName;
        try
        {
            var export = await ExportAsync(root, "form-xobject-unreadable.pdf", new BlankRasterizer());
            var summary = ExportSummaryBuilder.Build(export.Result.Graph, export.Result.Diagnostics);
            var review = ExportReviewBuilder.Build(export.Result.Graph, export.Result.Diagnostics);

            Assert.Equal((1, 0, 1, 1, 1, 0), (summary.ReviewPages, summary.VisualReviewPages, summary.UnanalyzedContent,
                summary.UnanalyzedContentPages, summary.ReviewImagePages, summary.UnresolvedElements));
            // Everything that was recognized converted; the page as a whole was not analyzed.
            Assert.True(summary.AllVisualElementsConverted);
            var text = summary.ToString();
            Assert.Contains("Visual elements converted: all", text, StringComparison.Ordinal);
            Assert.Contains("Unanalyzed content: 1 item(s) on 1 page(s)", text, StringComparison.Ordinal);
            Assert.Contains("Human review: required (unanalyzed content 1 page(s))", text, StringComparison.Ordinal);
            Assert.Contains(export.Result.Diagnostics, d => d.Code == "PdfFormXObjectUnparsed" && d.Severity == DiagnosticSeverity.Warning);
            Assert.Contains(export.Result.Diagnostics, d => d.Code == "PdfReviewImageAttached");

            var page = Assert.Single(review.Pages);
            var element = Assert.Single(page.Elements);
            Assert.Equal(ReviewElementKind.UnanalyzedContent, element.Kind);
            // The form may paint anywhere in its /BBox, which here is the whole page.
            var region = Assert.IsType<ReviewRegion>(element.Region);
            Assert.Equal((0d, 0d, 1d, 1d), (region.X, region.Y, region.Width, region.Height));
            Assert.Equal("out.assets/page-0001.png", page.ReviewImageReference);
            Assert.Equal("1ページ目：解析できなかった描画部品1件の文字・図はMarkdownに含まれていません。照合画像で内容を確認してください。",
                ExportReviewText.DescribeJapanese(page));
            Assert.Equal("page 1: 1 drawing component(s) not analyzed; their text and graphics are missing from the Markdown",
                ExportReviewText.DescribeEnglish(page));
            Assert.Equal("要確認 1ページ／照合画像 1ページ添付／未解析の描画部品 1件", ExportReviewText.CountsJapanese(review));

            Assert.Contains("> \\[PDF page 1: 1 drawing component(s) (Form XObject) could not be analyzed", export.Markdown, StringComparison.Ordinal);
            Assert.Contains("](out.assets/page-0001.png)", export.Markdown, StringComparison.Ordinal);
            Assert.DoesNotContain("FORM\\_TEXT\\_SENTINEL", export.Markdown, StringComparison.Ordinal);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Undecodable_form_without_a_rasterizer_says_the_review_image_is_unavailable()
    {
        var root = Directory.CreateTempSubdirectory("docredock-form-").FullName;
        try
        {
            var export = await ExportAsync(root, "form-xobject-unreadable.pdf", rasterizer: null);
            var page = Assert.Single(ExportReviewBuilder.Build(export.Result.Graph, export.Result.Diagnostics).Pages);

            Assert.Null(page.ReviewImageReference);
            Assert.True(page.ReviewImageUnavailable);
            Assert.Contains(export.Result.Diagnostics, d => d.Code == "PdfReviewImageUnavailable");
            Assert.Equal("1ページ目：解析できなかった描画部品1件の文字・図はMarkdownに含まれていません。" +
                "照合画像を作成できなかったため、原本PDFの1ページ目で内容を確認してください。", ExportReviewText.DescribeJapanese(page));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task With_ocr_on_the_undecodable_form_text_comes_back_from_the_page_image(bool reviewImages)
    {
        var root = Directory.CreateTempSubdirectory("docredock-form-").FullName;
        try
        {
            // The engine reads the whole page: the native heading again, and the form's sentence.
            var ocr = new PageOcr("Quarterly review", "FORM_TEXT_SENTINEL: approved");
            var export = await ExportAsync(root, "form-xobject-unreadable.pdf", new BlankRasterizer(), ocr, reviewImages);

            Assert.Equal(1, ocr.Calls);
            var recognized = Assert.Single(export.Result.Graph.Nodes, node => node.Kind == NodeKind.ImageText);
            Assert.Contains("FORM_TEXT_SENTINEL: approved", Assert.IsType<TextNodeContent>(recognized.Content).Text, StringComparison.Ordinal);
            // OCR that repeats native text is dropped, so the heading is not doubled.
            Assert.DoesNotContain("Quarterly review", Assert.IsType<TextNodeContent>(recognized.Content).Text, StringComparison.Ordinal);
            // Recovered text is still OCR text: the page stays flagged for comparison.
            Assert.Contains(export.Result.Diagnostics, d => d.Code == "PdfFormXObjectUnparsed");
            Assert.Equal(1, ExportSummaryBuilder.Build(export.Result.Graph, export.Result.Diagnostics).UnanalyzedContent);
        }
        finally { Directory.Delete(root, true); }
    }

    private static async Task<(ReadableDocumentExportResult Result, string Markdown)> ExportAsync(string root, string fixture, IPdfRasterizer? rasterizer,
        IOcrEngine? ocr = null, bool reviewImages = true)
    {
        // Every export writes out.md in its own folder so image references compare equal.
        var directory = Directory.CreateDirectory(Path.Combine(root, Path.GetFileNameWithoutExtension(fixture))).FullName;
        var markdown = Path.Combine(directory, "out.md");
        var result = await new DocumentService(ocr, rasterizer, discoverPdfRasterizer: false)
            .ExportReadableAsync(new ReadableDocumentExportOptions(FixturePath(fixture), markdown, EnableOcr: ocr is not null,
                IncludePdfFallbackImages: reviewImages));
        return (result, await File.ReadAllTextAsync(markdown));
    }

    private sealed class PageOcr(params string[] lines) : IOcrEngine
    {
        public int Calls { get; private set; }

        public ProviderDescriptor Descriptor { get; } = new("test.ocr", new Version(1, 0), 1,
            new HashSet<string> { "ocr.text" }, "MIT", "built-in", true);

        public ValueTask<OcrAttemptResult> RecognizeAsync(OcrInput input, OcrOptions options, CancellationToken cancellationToken)
        {
            Calls++;
            return ValueTask.FromResult(new OcrAttemptResult(OcrProcessingStatus.Completed,
                new OcrResult(string.Join("\n", lines), lines
                    .Select((text, index) => new OcrTextRegion(text, new Geometry("image-pixels", 10, 10 + index * 20, 200, 16), 0.95))
                    .ToArray()), []));
        }
    }

    internal static string FixturePath(string name)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var path = Path.Combine(current.FullName, "tests", "DocRedock.Tests", "Fixtures", "Pdf", name);
            if (File.Exists(path)) return path;
            current = current.Parent;
        }
        throw new FileNotFoundException("PDF fixture was not found.", name);
    }

    private sealed class BlankRasterizer : IPdfRasterizer
    {
        public ProviderDescriptor Descriptor { get; } = new("test.pdf.rasterizer", new Version(1, 0), 1,
            new HashSet<string> { "rasterize.pdf" }, "MIT", "built-in", true);

        public ValueTask<IReadOnlyList<RasterizedPdfPage>> RasterizeAsync(string pdfPath, IReadOnlyList<int> pageNumbers,
            PdfRasterizationOptions options, CancellationToken cancellationToken = default)
        {
            const int width = 596, height = 842;
            var rgb = new byte[width * height * 3];
            Array.Fill(rgb, (byte)255);
            var png = PngRasterImage.Encode(width, height, rgb);
            return ValueTask.FromResult<IReadOnlyList<RasterizedPdfPage>>(pageNumbers
                .Select(page => new RasterizedPdfPage(page, "image/png", png, width, height)).ToList());
        }
    }
}
