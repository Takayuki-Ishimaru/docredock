using System.Text;
using DocRedock.Api;
using DocRedock.Core.Documents;
using DocRedock.Providers.Abstractions.Providers;
using DocRedock.Render;

namespace DocRedock.Tests.Api;

/// <summary>v0.2.10 evaluation, sections 3 and 6: a three-page PDF whose first page is a clean
/// table, second page carries a diagonal line over a table, and third page a small unresolved path
/// reviews exactly pages 2 and 3, each with its own image and located element; a rotated page keeps
/// its review image but is never framed at a position that could be wrong.</summary>
public sealed class MultiPageReviewTests
{
    private const string Table =
        "60 60 m 460 60 l S\n60 100 m 460 100 l S\n60 140 m 460 140 l S\n60 180 m 460 180 l S\n60 220 m 460 220 l S\n" +
        "60 60 m 60 220 l S\n160 60 m 160 220 l S\n260 60 m 260 220 l S\n360 60 m 360 220 l S\n460 60 m 460 220 l S\n" +
        "BT 1 0 0 1 70 190 Tm (Task) Tj ET\nBT 1 0 0 1 70 150 Tm (DESIGN) Tj ET\nBT 1 0 0 1 70 110 Tm (BUILD) Tj ET\nBT 1 0 0 1 70 70 Tm (TEST) Tj ET\n" +
        "BT 1 0 0 1 170 190 Tm (Jan) Tj ET\nBT 1 0 0 1 270 190 Tm (Feb) Tj ET\nBT 1 0 0 1 370 190 Tm (Mar) Tj ET\n";

    /// <summary>A PDF with one page per content stream, each 612x400 points, optionally rotated.</summary>
    private static byte[] Pages(IReadOnlyList<string> contents, int rotate = 0)
    {
        var body = new StringBuilder("%PDF-1.4\n");
        var kids = string.Join(" ", contents.Select((_, index) => $"{3 + index * 2} 0 R"));
        body.Append("1 0 obj << /Type /Catalog /Pages 2 0 R >> endobj\n");
        body.Append($"2 0 obj << /Type /Pages /Kids [{kids}] /Count {contents.Count} >> endobj\n");
        for (var index = 0; index < contents.Count; index++)
        {
            var page = 3 + index * 2;
            body.Append($"{page} 0 obj << /Type /Page /Parent 2 0 R /MediaBox [0 0 612 400]{(rotate == 0 ? "" : $" /Rotate {rotate}")} /Contents {page + 1} 0 R >> endobj\n");
            body.Append($"{page + 1} 0 obj << /Length {contents[index].Length} >> stream\n{contents[index]}\nendstream endobj\n");
        }
        return Encoding.Latin1.GetBytes(body.Append("%%EOF").ToString());
    }

    [Fact]
    public async Task Only_the_unresolved_pages_are_reviewed_each_with_its_own_image_and_located_element()
    {
        var root = Directory.CreateTempSubdirectory("docredock-multipage-").FullName;
        try
        {
            var source = Path.Combine(root, "pages.pdf");
            await File.WriteAllBytesAsync(source, Pages(
            [
                Table,
                Table + "170 165 m 410 85 l S\n",
                "BT 1 0 0 1 60 300 Tm (Page three) Tj ET\n500 300 m 506 306 l 512 300 l 518 306 l S\n",
            ]));
            var result = await new DocumentService(null, new BlankRasterizer(), discoverPdfRasterizer: false)
                .ExportReadableAsync(new ReadableDocumentExportOptions(source, Path.Combine(root, "pages.md"), IncludePdfFallbackImages: true));
            var review = ExportReviewBuilder.Build(result.Graph, result.Diagnostics);
            var summary = ExportSummaryBuilder.Build(result.Graph, result.Diagnostics);

            Assert.Equal([2, 3], review.Pages.Select(page => page.Number));
            Assert.Equal((2, 2, 2), (summary.ReviewPages, summary.ReviewImagePages, summary.UnresolvedElements));
            Assert.Equal(["pages.assets/page-0002.png", "pages.assets/page-0003.png"], review.Pages.Select(page => page.ReviewImageReference));
            var diagonal = Assert.Single(review.Pages[0].Elements);
            Assert.Equal(ReviewElementKind.TableDiagonalLine, diagonal.Kind);
            Assert.NotNull(diagonal.Region);
            var small = Assert.Single(review.Pages[1].Elements);
            // The zigzag spans 18x6 points on a 612x400 page: a small, but located, target.
            var region = Assert.IsType<ReviewRegion>(small.Region);
            Assert.InRange(region.X, .79, .83);
            Assert.InRange(region.Width, .02, .06);
            Assert.StartsWith("2ページ目：", ExportReviewText.DescribeJapanese(review.Pages[0]), StringComparison.Ordinal);
            Assert.StartsWith("3ページ目：", ExportReviewText.DescribeJapanese(review.Pages[1]), StringComparison.Ordinal);
            Assert.DoesNotContain(result.Diagnostics, d => d.PartUri == "pdf:page:1" && d.Severity != DocRedock.Core.Reporting.DiagnosticSeverity.Information);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task A_rotated_page_keeps_its_review_image_but_frames_no_position()
    {
        var root = Directory.CreateTempSubdirectory("docredock-rotated-").FullName;
        try
        {
            var source = Path.Combine(root, "rotated.pdf");
            await File.WriteAllBytesAsync(source, Pages([Table + "170 165 m 410 85 l S\n"], rotate: 90));
            var result = await new DocumentService(null, new BlankRasterizer(), discoverPdfRasterizer: false)
                .ExportReadableAsync(new ReadableDocumentExportOptions(source, Path.Combine(root, "rotated.md"), IncludePdfFallbackImages: true));

            var page = Assert.Single(ExportReviewBuilder.Build(result.Graph, result.Diagnostics).Pages);
            Assert.Equal("rotated.assets/page-0001.png", page.ReviewImageReference);
            var element = Assert.Single(page.Elements);
            Assert.Null(element.Region);
            Assert.Null(element.Outline);
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class BlankRasterizer : IPdfRasterizer
    {
        public ProviderDescriptor Descriptor { get; } = new("test.pdf.rasterizer", new Version(1, 0), 1,
            new HashSet<string> { "rasterize.pdf" }, "MIT", "built-in", true);

        public ValueTask<IReadOnlyList<RasterizedPdfPage>> RasterizeAsync(string pdfPath, IReadOnlyList<int> pageNumbers,
            PdfRasterizationOptions options, CancellationToken cancellationToken = default)
        {
            const int width = 1224, height = 800;
            var rgb = new byte[width * height * 3];
            Array.Fill(rgb, (byte)255);
            var png = PngRasterImage.Encode(width, height, rgb);
            return ValueTask.FromResult<IReadOnlyList<RasterizedPdfPage>>(pageNumbers
                .Select(page => new RasterizedPdfPage(page, "image/png", png, width, height)).ToList());
        }
    }
}
