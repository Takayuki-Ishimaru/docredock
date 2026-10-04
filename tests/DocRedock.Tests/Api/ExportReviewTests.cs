using System.Text;
using System.Text.Json;
using DocRedock.Api;
using DocRedock.Core.Documents;
using DocRedock.Core.Reporting;
using DocRedock.Providers.Abstractions.Providers;
using DocRedock.Render;

namespace DocRedock.Tests.Api;

/// <summary>v0.2.9 evaluation, priority 3 and section 4: the result says separately that the
/// Markdown was written, what could not be converted (counted as elements and pages, not as
/// diagnostic records), where to look, and whether OCR text needs comparison.</summary>
public sealed class ExportReviewTests
{
    private static byte[] WithMediaBox(byte[] pdf) => Encoding.Latin1.GetBytes(
        Encoding.Latin1.GetString(pdf).Replace("<< /Type /Page >>", "<< /Type /Page /MediaBox [0 0 612 400] >>", StringComparison.Ordinal));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Diagonal_over_a_table_is_one_review_element_with_a_located_highlight(bool arrow)
    {
        var root = Directory.CreateTempSubdirectory("docredock-review-").FullName;
        try
        {
            var overlay = "170 165 m 410 85 l S";
            if (arrow)
            {
                var length = Math.Sqrt(240 * 240 + 80 * 80);
                var ux = 240 / length; var uy = -80 / length;
                overlay += FormattableString.Invariant($"\n{410 - uy * 5} {85 + ux * 5} m {410 + ux * 10} {85 + uy * 10} l {410 + uy * 5} {85 - ux * 5} l h f");
            }
            var source = Path.Combine(root, "diagonal.pdf");
            await File.WriteAllBytesAsync(source, WithMediaBox(Pdf.PdfEvaluationRegressionTests.Schedule(overlay)));
            var markdown = Path.Combine(root, "diagonal.md");
            var result = await new DocumentService(null, new SolidPngRasterizer(1224, 800), discoverPdfRasterizer: false)
                .ExportReadableAsync(new ReadableDocumentExportOptions(source, markdown, IncludePdfFallbackImages: true));

            var review = ExportReviewBuilder.Build(result.Graph, result.Diagnostics);
            var summary = ExportSummaryBuilder.Build(result.Graph, result.Diagnostics);
            var page = Assert.Single(review.Pages);
            Assert.Equal(summary.ReviewPages, review.Pages.Count);
            Assert.Equal(summary.UnresolvedElements, review.Elements);
            Assert.Equal(summary.ReviewImagePages, review.ReviewImagePages);
            var element = Assert.Single(page.Elements);
            Assert.Equal(arrow ? ReviewElementKind.TableDiagonalArrow : ReviewElementKind.TableDiagonalLine, element.Kind);
            Assert.Equal("diagonal.assets/page-0001.png", page.ReviewImageReference);
            Assert.True(page.HasTables);
            // The line runs from (170,165) to (410,85) on a 612x400 page: left ~28%, top ~59%.
            var region = Assert.IsType<ReviewRegion>(element.Region);
            Assert.InRange(region.X, .25, .29);
            Assert.InRange(region.Y, .55, .60);
            Assert.InRange(region.X + region.Width, .66, .70);
            Assert.InRange(region.Y + region.Height, .78, .82);
            // The stroke itself is traced end to end, not only its bounding box.
            var outline = Assert.IsAssignableFrom<IReadOnlyList<ReviewPoint>>(element.Outline);
            Assert.Equal(2, outline.Count);
            Assert.Equal((.278, .588), (Math.Round(outline[0].X, 3), Math.Round(outline[0].Y, 3)));
            Assert.Equal((.670, .788), (Math.Round(outline[1].X, 3), Math.Round(outline[1].Y, 3)));

            Assert.Equal(arrow
                ? "1ページ目：表の上の斜めの矢印1件を表の記号に変換できませんでした。表の文字は書き出されています。照合画像で線の意味を確認してください。"
                : "1ページ目：表の上の斜めの線1件を表の記号に変換できませんでした。表の文字は書き出されています。照合画像で線の意味を確認してください。",
                ExportReviewText.DescribeJapanese(page));
            Assert.Equal("要確認 1ページ／照合画像 1ページ添付／未解決の図形 1件", ExportReviewText.CountsJapanese(review));
            Assert.DoesNotContain("pdf_p1", ExportReviewText.DescribeJapanese(page));
            Assert.StartsWith(arrow ? "page 1: 1 diagonal arrow(s)" : "page 1: 1 diagonal line(s)", ExportReviewText.DescribeEnglish(page));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Missing_rasterizer_points_to_the_source_page_instead_of_an_image()
    {
        var root = Directory.CreateTempSubdirectory("docredock-review-").FullName;
        try
        {
            var source = Path.Combine(root, "diagonal.pdf");
            await File.WriteAllBytesAsync(source, Pdf.PdfEvaluationRegressionTests.Schedule("q [4 3] 0 d 170 165 m 410 85 l S Q"));
            var result = await new DocumentService(null, null, discoverPdfRasterizer: false)
                .ExportReadableAsync(new ReadableDocumentExportOptions(source, Path.Combine(root, "diagonal.md")));
            var page = Assert.Single(ExportReviewBuilder.Build(result.Graph, result.Diagnostics).Pages);
            Assert.Null(page.ReviewImageReference);
            Assert.True(page.ReviewImageUnavailable);
            Assert.Equal("dashed", Assert.Single(page.Elements).LineStyle);
            Assert.Equal("1ページ目：表の上の斜めの線（破線）1件を表の記号に変換できませんでした。表の文字は書き出されています。" +
                "照合画像を作成できなかったため、原本PDFの1ページ目で線の意味を確認してください。", ExportReviewText.DescribeJapanese(page));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Labels_and_raw_paths_are_named_by_what_they_are()
    {
        var visual = new VisualGraph("raw", [], [], [
                new VisualDiagnostic("VisualEdgeLabelUnresolved", "Text could not be uniquely assigned to an edge.", SourceObjectId: "region:4"),
            ], Paths: [new VisualPath("raw-path", [new(0, 0), new(10, 0), new(10, 10), new(0, 0)])],
            SourceItems: [new VisualSourceItem("raw-path", VisualSourceItemKind.VectorPath, VisualDisposition.VisualFallback, FallbackPathId: "raw-path")]);
        var node = new DocumentNode("diagram", NodeKind.Diagram, null, 0, ContentLayer.Derived, new TextNodeContent("diagram"),
            Extensions: new Dictionary<string, JsonElement> { ["visual_graph"] = JsonSerializer.SerializeToElement(visual) });
        var graph = new DocumentGraph(DocumentGraph.CurrentSchemaVersion, "raw", DocumentFormatKind.Pptx,
            [new DocumentPartition("slide-0002", 1, [node])]);
        var page = Assert.Single(ExportReviewBuilder.Build(graph, []).Pages);
        Assert.Equal(new[] { ReviewElementKind.Label, ReviewElementKind.Shape }, page.Elements.Select(e => e.Kind).Order());
        Assert.Equal(ExportSummaryBuilder.Build(graph, []).UnresolvedElements, page.Elements.Count);
        Assert.Equal("スライド2：文字1件を、どの線や図形の説明か確定できませんでした。図形1件を図として再構成できませんでした。元のファイルで内容を確認してください。",
            ExportReviewText.DescribeJapanese(page));
    }

    [Fact]
    public void Malformed_duplicate_path_ids_do_not_break_the_review_list()
    {
        var visual = new VisualGraph("dup", [], [], Paths: [new VisualPath("same"), new VisualPath("same")],
            SourceItems: [new VisualSourceItem("same", VisualSourceItemKind.VectorPath, VisualDisposition.VisualFallback, FallbackPathId: "same")]);
        var node = new DocumentNode("diagram", NodeKind.Diagram, null, 0, ContentLayer.Derived, new TextNodeContent("diagram"),
            Extensions: new Dictionary<string, JsonElement> { ["visual_graph"] = JsonSerializer.SerializeToElement(visual) });
        var graph = new DocumentGraph(DocumentGraph.CurrentSchemaVersion, "dup", DocumentFormatKind.Pdf,
            [new DocumentPartition("page-0001", 0, [node])]);
        var page = Assert.Single(ExportReviewBuilder.Build(graph, []).Pages);
        Assert.Equal("same", Assert.Single(page.Elements).SourceId);
    }

    [Fact]
    public void Ocr_review_items_are_counted_separately_from_warnings()
    {
        var regions = JsonSerializer.SerializeToElement(new object[]
        {
            new { text = "認結果", confidence = .63 }, new { text = "12", confidence = .12 },
            new { text = "OK", confidence = .95 }, new { text = "品番" },
        });
        var ocr = new DocumentNode("ocr", NodeKind.ImageText, "image", 1, ContentLayer.Derived, new TextNodeContent("認結果 12 OK 品番"),
            Extensions: new Dictionary<string, JsonElement> { ["ocr_regions"] = regions });
        var graph = new DocumentGraph(DocumentGraph.CurrentSchemaVersion, "ocr", DocumentFormatKind.Docx,
            [new DocumentPartition("document", 0, [ocr])]);

        var summary = ExportSummaryBuilder.Build(graph, []);
        Assert.Equal((1, 3, true, 0), (summary.OcrImages, summary.OcrReviewItems, summary.OcrReviewRequired, summary.Warnings));
        var text = summary.ToString();
        Assert.Contains("Output written: yes", text);
        Assert.Contains("Visual elements converted: all", text);
        Assert.Contains("Human review: required (OCR 3 item(s))", text);
        Assert.Contains("OCR review items: 3 (confidence below 80% or not reported, in 1 image(s))", text);
        var review = ExportReviewBuilder.Build(graph, []);
        Assert.Empty(review.Pages);
        Assert.Equal("OCR確認 3件", ExportReviewText.CountsJapanese(review));
        Assert.Contains("3件", ExportReviewText.OcrJapanese(review.Ocr));
    }

    [Fact]
    public void Clean_export_reports_nothing_to_review()
    {
        var graph = new DocumentGraph(DocumentGraph.CurrentSchemaVersion, "clean", DocumentFormatKind.Docx,
            [new DocumentPartition("document", 0, [new DocumentNode("p", NodeKind.Paragraph, null, 0, ContentLayer.Body, new TextNodeContent("text"))])]);
        var text = ExportSummaryBuilder.Build(graph, []).ToString();
        Assert.Contains("Visual elements converted: all", text);
        Assert.Contains("Human review: not required", text);
        Assert.DoesNotContain("OCR review items", text);
        Assert.False(ExportReviewBuilder.Build(graph, []).Required);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Hidden_content_sharing_review_is_separate_from_source_review(bool sourceReview, bool hiddenContent)
    {
        var graph = new DocumentGraph(DocumentGraph.CurrentSchemaVersion, "sharing", DocumentFormatKind.Docx, []);
        var diagnostics = new[] { new Diagnostic(hiddenContent ? "HiddenContentIncluded" : "OtherWarning",
            "Review output.", DiagnosticSeverity.Warning) };
        var summary = ExportSummaryBuilder.Build(graph, diagnostics);
        if (sourceReview) summary = summary with { VisualReviewPages = 1, ReviewPages = 1, UnresolvedElements = 1 };

        Assert.Equal(hiddenContent, summary.HiddenContentIncluded);
        Assert.Equal(1, summary.Warnings);
        Assert.Equal(sourceReview ? 1 : 0, summary.ReviewPages);
        Assert.Equal(0, summary.ReviewImagePages);
        var text = summary.ToString();
        if (!hiddenContent)
        {
            Assert.Contains("Human review: not required", text);
            Assert.DoesNotContain("review before sharing", text);
        }
        else
        {
            Assert.Contains(sourceReview
                ? "Human review: required (visual 1 page(s)); hidden content included - review before sharing"
                : "Human review: source comparison not required; hidden content included - review before sharing", text);
        }
    }

    private sealed class SolidPngRasterizer(int width, int height) : IPdfRasterizer
    {
        public ProviderDescriptor Descriptor { get; } = new("test.pdf.rasterizer", new Version(1, 0), 1,
            new HashSet<string> { "rasterize.pdf" }, "MIT", "built-in", true);

        public ValueTask<IReadOnlyList<RasterizedPdfPage>> RasterizeAsync(string pdfPath, IReadOnlyList<int> pageNumbers,
            PdfRasterizationOptions options, CancellationToken cancellationToken = default)
        {
            var rgb = new byte[width * height * 3];
            Array.Fill(rgb, (byte)255);
            var png = PngRasterImage.Encode(width, height, rgb);
            return ValueTask.FromResult<IReadOnlyList<RasterizedPdfPage>>(pageNumbers
                .Select(page => new RasterizedPdfPage(page, "image/png", png, width, height)).ToList());
        }
    }
}
